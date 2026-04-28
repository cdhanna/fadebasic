using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic.Ast;
using FadeBasic.Ast.Visitors;
using FadeBasic.Json;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{

    
    // make a debug server!
    public class DebugSession
    {
        public class DiscoveryMessage : IJsonable
        {
            public int port;
            public string label;
            public string processName;
            public int processId;
            public string processWindowTitle;
            public void ProcessJson(IJsonOperation op)
            {
                op.IncludeField(nameof(port), ref port);
                op.IncludeField(nameof(processId), ref processId);
                op.IncludeField(nameof(label), ref label);
                op.IncludeField(nameof(processName), ref processName);
                op.IncludeField(nameof(processWindowTitle), ref processWindowTitle);
            }
        }
        
        enum State
        {
            INIT,
            PLAYING,
            PAUSED,
            STEPPING_NEXT
        }
        
        
        public VirtualMachine _vm;
        protected DebugData _dbg;
        protected CommandCollection _commandCollection;
        protected readonly string _label;
        public readonly LaunchOptions _options;

        protected bool requestedExit;
        protected bool started;
        protected Task _serverTask;
        protected Task _processingTask;

        protected ConcurrentQueue<DebugMessage> 
            outboundMessages = new ConcurrentQueue<DebugMessage>(),
            receivedMessages = new ConcurrentQueue<DebugMessage>();

        public CancellationTokenSource _cts;
        protected Exception _serverTaskEx;

        protected bool hasReceivedOpen = false;

        protected int hasConnectedDebugger;
        protected int debuggerSaidHello;
        protected int debuggerReset;
        protected int pauseRequestedByMessageId;
        protected int resumeRequestedByMessageId;
        
        protected int messageIdCounter;


        protected int currentInsLookupOffset = 0;
        protected DebugMessage stepNextMessage;
        protected DebugMessage stepIntoMessage;
        protected DebugMessage stepOutMessage;

        protected DebugToken stepOverFromToken;
        protected DebugToken stepInFromToken;
        protected DebugToken stepOutFromToken;
        protected int stepStackDepth;

        public HashSet<DebugToken> breakpointTokens = new HashSet<DebugToken>();
      
        class DebugTokenComparer : IComparer<DebugToken>
        {
            public int Compare(DebugToken x, DebugToken y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (y is null) return 1;
                if (x is null) return -1;
                var insIndexComparison = x.insIndex.CompareTo(y.insIndex);
                if (insIndexComparison != 0) return insIndexComparison;
                return x.isComputed.CompareTo(y.isComputed);
            }
        }
        
        public DebugToken hitBreakpointToken;
        public IndexCollection instructionMap;
        
        public IDebugLogger logger;

        public DebugVariableDatabase variableDb;
        
        public int InstructionPointer => _vm.instructionIndex;

        public bool IsPaused => pauseRequestedByMessageId > resumeRequestedByMessageId;

        public bool IsClientConnected => didClientConnect;

        public const int DEBUG_SERVER_DISCOVERY_PORT = 21758;
        public static List<DiscoveryMessage> DiscoverServers()
        {
            UdpClient client = new UdpClient();
            IPEndPoint endPoint = new IPEndPoint(IPAddress.Broadcast, DEBUG_SERVER_DISCOVERY_PORT);
            byte[] requestData = Encoding.UTF8.GetBytes("FADE_DEBUG_DISCOVERY");

            client.EnableBroadcast = true;
            client.Send(requestData, requestData.Length, endPoint);

            client.Client.ReceiveTimeout = 500;
            var messages = new List<DiscoveryMessage>();
            try
            {
                while (true)
                {
                    var serverResponse = client.Receive(ref endPoint);
                    var json = Encoding.UTF8.GetString(serverResponse);
                    var message = JsonableExtensions.FromJson<DiscoveryMessage>(json);
                    messages.Add(message);
                }
            }
            catch (SocketException)
            {
                
            }

            return messages;
        }
        
        
        public DebugSession(VirtualMachine vm, DebugData dbg, CommandCollection commandCollection=null, LaunchOptions options=null, string label=null)
        {
            _options = options ?? LaunchOptions.DefaultOptions;
            _dbg = dbg;
            _commandCollection = commandCollection;
            _label = label;
            _vm = vm;
            _vm.shouldThrowRuntimeException = false;
            
            instructionMap = new IndexCollection(_dbg.statementTokens);

            if (!string.IsNullOrEmpty(options?.debugLogPath))
            {
                logger = new DebugLogger(options.debugLogPath);
            }
            else
            {
                logger = new EmptyDebugLogger();
            }
            _vm.logger = logger;
            variableDb = new DebugVariableDatabase(vm, dbg, logger);

            logger.Log("Starting debug session... version=" + typeof(DebugSession).Assembly.GetName().Version);

            foreach (var token in _dbg.statementTokens)
            {
                var json = JsonableExtensions.Jsonify(token);
                logger.Log(json);
            }
            
            
            // _tree = IntervalTree.From(dbg.points);
        }

        public void Restart(VirtualMachine nextVm, DebugData nextDebugData, CommandCollection commandCollection)
        {
            // put this as a message so that the read-loop causes an interupt in the running VM. 
            //  otherwise, the existing VM will get stuck in a read-loop.
            receivedMessages.Enqueue(new MockResetMessage()
            {
                type = DebugMessageType.REV_REQUEST_RESTART,
                nextDebugData = nextDebugData,
                nextMachine = nextVm,
                nextCommands = commandCollection
            });
            
            // flip some state so the program does not run, until hello is received. 
            debuggerSaidHello = 0;
            debuggerReset = 1;
            
            _vm.Suspend();
        }

        public void StartServer()
        {
            if (started) throw new InvalidOperationException("Debug server already started");
            started = true;

            _cts = new CancellationTokenSource();

            if (!ThreadPool.QueueUserWorkItem(RunServer))
            {
                throw new Exception("Could not acquire server thread.");
            }

            if (!ThreadPool.QueueUserWorkItem(RunDiscoverability))
            {
                throw new Exception("Could not acquire discoverability thread.");
            }
        }

        public void ShutdownServer()
        {
            logger.Log("Starting server shutdown...");
            while (didClientConnect && outboundMessages.Count > 0)
            {
                Thread.Sleep(10); // wait for messages to go away...
            }
            logger.Log("Messages done...");
            _cts.Cancel();
        }

        protected bool didClientConnect = false;
        protected void RunServer(object state)
        {
            DebugServerStreamUtil.OpenServer2(_options.debugPort, outboundMessages, ref didClientConnect, receivedMessages, _cts.Token);
        }


        protected void RunDiscoverability(object state)
        {
            UdpClient discoverabilityListener = new UdpClient();

            discoverabilityListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            discoverabilityListener.Client.ExclusiveAddressUse = false;

            var endpoint = new IPEndPoint(IPAddress.Any, DEBUG_SERVER_DISCOVERY_PORT);
            discoverabilityListener.Client.Bind(endpoint);
            while (!_cts.IsCancellationRequested)
            {
                var requestData = discoverabilityListener.Receive(ref endpoint);
                string request = Encoding.UTF8.GetString(requestData);

                if (request == "FADE_DEBUG_DISCOVERY")
                {
                    var proc = Process.GetCurrentProcess();
                    var response = new DiscoveryMessage
                    {
                        label = _label,
                        port = _options.debugPort,
                        processId = proc.Id,
                        processWindowTitle = proc.MainWindowTitle,
                        processName = proc.ProcessName
                    }.Jsonify();
                    byte[] responseData = Encoding.UTF8.GetBytes(response);

                    discoverabilityListener.Send(responseData, responseData.Length, endpoint);
                   
                }
            }

        }
        

        protected void Ack(int originalId, DebugMessage responseMsg)
        {
            responseMsg.id = originalId;
            responseMsg.type = DebugMessageType.PROTO_ACK;
            outboundMessages.Enqueue(responseMsg);
        }
        
        protected void Ack(DebugMessage originalMessage)
        {
            outboundMessages.Enqueue(new DebugMessage
            {
                id = originalMessage.id,
                type = DebugMessageType.PROTO_ACK
            });
        }

        protected void Ack<T>(DebugMessage originalMessage, T responseMsg)
            where T : DebugMessage
        {
            responseMsg.id = originalMessage.id;
            responseMsg.type = DebugMessageType.PROTO_ACK;
            outboundMessages.Enqueue(responseMsg);
        }

        public int GetNextMessageId() => Interlocked.Decrement(ref messageIdCounter);

        protected void SendStopMessage()
        {
            var message = new DebugMessage()
            {
                id = GetNextMessageId(),
                type = DebugMessageType.REV_REQUEST_BREAKPOINT
            };
            outboundMessages.Enqueue(message);
        }

        protected void SendRuntimeErrorMessage(string message)
        {
            outboundMessages.Enqueue(new ExplodedMessage()
            {
                id = GetNextMessageId(),
                message = message,
                type = DebugMessageType.REV_REQUEST_EXPLODE
            });
        }

        protected void SendExitedMessage()
        {
            logger?.Debug("Sending exit message");
            var message = new DebugMessage()
            {
                id = GetNextMessageId(),
                type = DebugMessageType.REV_REQUEST_EXITED
            };
            outboundMessages.Enqueue(message);
        }
        
        protected void ReadMessage()
        {
            if (receivedMessages.TryDequeue(out var message))
            {
                logger.Log($"[DBG] Received message : {message.id}, {message.type.ToString()}");

                try
                {
                    switch (message.type)
                    {
                        case DebugMessageType.REV_REQUEST_RESTART:

                            var mock = message as MockResetMessage;
                            _vm = mock.nextMachine;
                            _vm.shouldThrowRuntimeException = false;
                            _vm.logger = logger;
            
                            _commandCollection = mock.nextCommands;
            
                            _dbg = mock.nextDebugData;
            
                            instructionMap = new IndexCollection(_dbg.statementTokens);
                            variableDb = new DebugVariableDatabase(_vm, _dbg, logger);
            
                            logger.Log("RESTARTING debug session... version=" + typeof(DebugSession).Assembly.GetName().Version);
                            foreach (var token in _dbg.statementTokens)
                            {
                                var json = JsonableExtensions.Jsonify(token);
                                logger.Log(json);
                            }
            
                            // reset state variables 
                         
                            pauseRequestedByMessageId = 0;
                            resumeRequestedByMessageId = 0;
                            currentInsLookupOffset = 0;
                            stepNextMessage = null;
                            stepIntoMessage = null;
                            stepOutMessage = null;
                            stepStackDepth = 0;
                            stepOverFromToken = null;
                            stepInFromToken = null;
                            stepOutFromToken = null;
                            breakpointTokens.Clear();
                            hitBreakpointToken = null;
                            
                            // tell the DAP Host that we are planning to reboot!
                            outboundMessages.Enqueue(new DebugMessage()
                            {
                                id = GetNextMessageId(),
                                type = DebugMessageType.REV_REQUEST_RESTART
                            });
                            
                            break;
                        case DebugMessageType.PROTO_HELLO:
                            hasConnectedDebugger = 1;
                            debuggerSaidHello = 1;
                            debuggerReset = 0;
                            Ack(message, new HelloResponseMessage()
                            {
                                processId = Process.GetCurrentProcess().Id
                            });
                            break;
                        case DebugMessageType.REQUEST_PAUSE:
                            pauseRequestedByMessageId = message.id;
                            Ack(message);

                            if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex,
                                    out var pausedAtToken))
                            {
                                logger.Log($"[DBG] could not find pause token");
                            }
                            else
                            {
                                logger.Log(
                                    $"[DBG] paused at ins=[{_vm.instructionIndex}]  token {pausedAtToken.Jsonify()}");
                            }

                            break;
                        case DebugMessageType.REQUEST_PLAY:
                            resumeRequestedByMessageId = message.id;
                            variableDb.ClearLifetime();
                            Ack(message);
                            break;
                        case DebugMessageType.REQUEST_TERMINATE:
                            requestedExit = true;
                            Ack(message);
                            Environment.Exit(0);
                            break;
                        case DebugMessageType.REQUEST_BREAKPOINTS:
                            var detail = JsonableExtensions.FromJson<RequestBreakpointMessage>(message.RawJson);
                            // _breakpoints = detail.breakpoints;

                            logger.Log(
                                $"Handling breakpoint resolution... breakpoint-count=[{detail.breakpoints.Count}]");
                            breakpointTokens.Clear();
                            var verifiedBreakpoints = new List<Breakpoint>();
                            for (var i = 0; i < detail.breakpoints.Count; i++)
                            {
                                var requestedBreakPoint = detail.breakpoints[i];
                                var verified = instructionMap.TryFindClosestTokenAtLocation(
                                    requestedBreakPoint.lineNumber,
                                    requestedBreakPoint.colNumber, out var token);

                                var bp = new Breakpoint
                                {
                                    status = verified ? 1 : -1,
                                    lineNumber = verified ? token.token.lineNumber : requestedBreakPoint.lineNumber,
                                    colNumber = verified ? token.token.charNumber : requestedBreakPoint.colNumber
                                };
                                logger.Log(
                                    $" breakpoint index=[{i}] is verified=[{verified}] line=[{bp.lineNumber}] cn=[{bp.colNumber}]");
                                verifiedBreakpoints.Add(bp);
                                if (verified)
                                {
                                    breakpointTokens.Add(token);
                                }
                            }


                            Ack(message, new ResponseBreakpointMessage
                            {
                                breakpoints = verifiedBreakpoints
                            });

                            break;
                        case DebugMessageType.REQUEST_STEP_IN:

                        {
                            // reset the info to blank
                            stepInFromToken = null;
                            stepStackDepth = 0;
                            variableDb.ClearLifetime();
                        }

                            if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex,
                                    out stepInFromToken))
                            {
                                logger.Log($"[DBG] could not find into starting token");
                                Ack(message, new StepNextResponseMessage
                                {
                                    reason = "no source location available for starting location",
                                    status = -1
                                });
                                break; // do NOT set stepIntoMessage — Ack already sent
                            }

                            stepIntoMessage = message; // need to ACK later...
                            stepStackDepth = _vm.methodStack.Count;

                            logger.Log(
                                $"[DBG] stepping in ins=[{_vm.program[_vm.instructionIndex]}] depth=[{stepStackDepth}] from {stepInFromToken.Jsonify()}");
                            break;
                        case DebugMessageType.REQUEST_STEP_OUT:

                        {
                            // reset the info to blank
                            stepOutFromToken = null;
                            stepStackDepth = 0;
                            variableDb.ClearLifetime();
                        }

                            if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex,
                                    out stepOutFromToken))
                            {
                                logger.Log($"[DBG] could not find out starting token");
                                Ack(message, new StepNextResponseMessage
                                {
                                    reason = "no source location available for starting location",
                                    status = -1
                                });
                                break; // do NOT set stepOutMessage — Ack already sent
                            }

                            stepOutMessage = message; // need to ACK later...
                            stepStackDepth = _vm.methodStack.Count;

                            logger.Log($"[DBG] stepping out from {stepOutFromToken.Jsonify()}");
                            break;
                        case DebugMessageType.REQUEST_STEP_OVER:

                            // stepping NEXT means "step over"
                            //  that means go to the next statement that has the same stack depth, OR less than currently.

                        {
                            // reset the info to blank
                            stepOverFromToken = null;
                            stepStackDepth = 0;
                            variableDb.ClearLifetime();
                        }

                            if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex,
                                    out stepOverFromToken))
                            {
                                logger.Log($"[DBG] could not find next starting token");
                                Ack(message, new StepNextResponseMessage
                                {
                                    reason = "no source location available for starting location",
                                    status = -1
                                });
                                break; // do NOT set stepNextMessage — Ack already sent
                            }

                            stepNextMessage = message; // need to ACK later...
                            stepStackDepth = _vm.methodStack.Count;

                            logger.Debug($"stepping from {stepOverFromToken.Jsonify()}");
                            break;

                        case DebugMessageType.REQUEST_VARIABLE_EXPANSION:
                            try
                            {
                                var variableRequest =
                                    JsonableExtensions.FromJson<DebugVariableExpansionRequest>(message.RawJson);
                                var subScope = variableDb.Expand(variableRequest.variableId);
                                Ack(message, new ScopesMessage
                                {
                                    scopes = new List<DebugScope>
                                    {
                                        subScope
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                logger.Error($"Variable expansion error: {ex.Message}");
                                Ack(message, new ScopesMessage { scopes = new List<DebugScope>() });
                            }

                            break;
                        case DebugMessageType.REQUEST_EVAL:
                            var evalRequest = JsonableExtensions.FromJson<EvalMessage>(message.RawJson);
                            logger.Info($"doing eval. {evalRequest.expression}");
                            try
                            {
                                var evalResult = Eval(evalRequest.frameIndex, evalRequest.expression);
                                logger.Info($"did eval id=[{evalResult.id}] value=[{evalResult.value}]");

                                var retMsg = new EvalResponse
                                {
                                    result = evalResult
                                };
                                logger.Info($"Return eval msg=[{retMsg.Jsonify()}]");
                                Ack(evalRequest, retMsg);
                            }
                            catch (Exception ex)
                            {
                                logger.Error("OH NO SOMETHING BROKE!: " + ex.Message);
                                Ack(evalRequest, new EvalResponse
                                {
                                    result = DebugEvalResult.Failed($"internal error: {ex.Message}")
                                });
                            }

                            break;
                        case DebugMessageType.REQUEST_REPL:
                            var replRequest = JsonableExtensions.FromJson<EvalMessage>(message.RawJson);
                            logger.Info($"doing repl. {replRequest.expression}");
                            try
                            {
                                var replResult = ReplExec(replRequest.frameIndex, replRequest.expression);
                                Ack(replRequest, new EvalResponse { result = replResult });
                            }
                            catch (Exception ex)
                            {
                                logger.Error("REPL internal error: " + ex.Message);
                                Ack(replRequest, new EvalResponse
                                {
                                    result = DebugEvalResult.Failed($"internal error: {ex.Message}")
                                });
                            }

                            break;
                        case DebugMessageType.REQUEST_SET_VAR:
                            var setVarRequest = JsonableExtensions.FromJson<SetVariableMessage>(message.RawJson);
                            var exprResult = Eval(setVarRequest.frameId, setVarRequest.rhs, setVarRequest.variableId);

                            // now that we have the result, we need to assign it to the given value... 
                            // variableDb.ResetValue(setVarRequest.variableId, exprResult);

                            // throw new NotImplementedException();
                            Ack(setVarRequest, new EvalResponse
                            {
                                result = exprResult
                            });
                            break;
                        case DebugMessageType.REQUEST_SCOPES:
                            var scopeRequest = JsonableExtensions.FromJson<DebugScopeRequest>(message.RawJson);
                            var scopeResult = GetScopes(scopeRequest);
                            Ack(message, scopeResult);
                            break;
                        case DebugMessageType.REQUEST_STACK_FRAMES:
                            var frames = GetFrames2();
                            Ack(message, new StackFrameMessage
                            {
                                frames = frames
                            });
                            logger.Debug($"enqueued stack frame response. frame-count=[{frames.Count}]");

                            break;
                    }
                } catch (Exception ex){
                    logger?.Error($"Read Error: type=[{ex.GetType().Name}] msg=[{ex.Message}]");
                }
            }
        }

        public ScopesMessage GetScopes(DebugScopeRequest scopeRequest)
        {
            var scopeFrames = GetFrames2();
            var frameIndex = scopeRequest.frameIndex;
            logger.Debug($"got stack frame request with frame-id=[{frameIndex}] frame-count=[{scopeFrames.Count}] scope-stack=[{_vm.scopeStack.Count}] dbg-count=[{_dbg.insToVariable.Count}]");
            if (frameIndex < 0 || frameIndex >= scopeFrames.Count)
            {
                logger.Error($"scope request failed because frame-index=[{frameIndex}] was out of bounds of max=[{scopeFrames.Count}]");
            }

            var global = variableDb.GetGlobalVariablesForFrame(frameIndex);
            var local = variableDb.GetLocalVariablesForFrame(frameIndex);

            return new ScopesMessage
            {
                scopes = new List<DebugScope>
                {
                    global,
                    local
                }
            };
        }

        public DebugEvalResult Eval(int frameId, string rightHandExpression, int overwriteVariableId=-1)
        {
            // TODO: ignore the frame-id, and just assume everything is at frame-0...

            const string SYNTHETIC_NAME2 = "fade________eval";

            // VS Code's debug hover may send partial expressions like ".e" (dot-prefixed field)
            // or "1).e" when it can't fully resolve the dotted path through parenthesized
            // array indexing.  Strip leading junk to get the bare field name.
            if (rightHandExpression.Length > 0)
            {
                var dotIdx = rightHandExpression.LastIndexOf('.');
                if (dotIdx >= 0 && dotIdx < rightHandExpression.Length - 1)
                {
                    var afterDot = rightHandExpression.Substring(dotIdx + 1);
                    // If the part before the dot is NOT a valid identifier (e.g. "1)", "."),
                    // treat it as a bare field hover and use only the part after the last dot.
                    var beforeDot = rightHandExpression.Substring(0, dotIdx);
                    if ((beforeDot.Length == 0 || !char.IsLetter(beforeDot[0]))
                        && afterDot.Length > 0 && char.IsLetter(afterDot[0]))
                    {
                        rightHandExpression = afterDot;
                    }
                }
            }

            // Hover requests from the editor send the word under the cursor.  Resolve to the best
            // known eval-name before proceeding so the rest of Eval sees the correct expression.
            if (variableDb.TryResolveHoverExpression(rightHandExpression, out var resolved))
                rightHandExpression = resolved;

            rightHandExpression = SYNTHETIC_NAME2 + "=" + rightHandExpression;

            var globalVariableTable = new Dictionary<string, CompiledVariable>();
            var localVariableTable = new Dictionary<string, CompiledVariable>();
            
            var globalArrayVariableTable = new Dictionary<string, CompiledArrayVariable>();
            var localArrayVariableTable = new Dictionary<string, CompiledArrayVariable>();

            logger.Log($"Evaluating frame=[{frameId}], expr=[{rightHandExpression}]");
            
            var lexer = new Lexer();
            var lexResults = lexer.TokenizeWithErrors(rightHandExpression, _commandCollection);
            if (lexResults.tokenErrors.Count > 0)
            {
                return DebugEvalResult.Failed("Unable to lex expression");
                throw new NotImplementedException("lex error handling not supported yet");
            }

            /*
             * To parse correctly, we need to mock out the existing context...
             * - variables need to be declared with their full types
             * - function names need to exist
             * 
             */
            
            var parser = new Parser(lexResults.stream, _commandCollection);
            var node = parser.ParseProgram(new ParseOptions
            {
                ignoreChecks = true
            });
            if (node.statements.Count > 1)
            {
                return DebugEvalResult.Failed("only single declaration is allowed");
            }
            
            var locals = variableDb.GetLocalVariablesForFrame(frameId);
            var globals = variableDb.GetGlobalVariablesForFrame(frameId);
            
            var types = new List<CompiledType>();
            var idToTypeTable = new Dictionary<int, CompiledType>();
            foreach (var kvp in _vm.typeTable) // TODO: Cache this between life-cycles... 
            {
                var internedType = kvp.Value;
                var compileType = new CompiledType
                {
                    typeName = internedType.name,
                    typeId = internedType.typeId,
                    byteSize = internedType.byteSize,
                    fields = new Dictionary<string, CompiledTypeMember>()
                };
                idToTypeTable[compileType.typeId] = compileType;
                types.Add(compileType);

            }

            { // do a second pass to set all the field types...
                foreach (var kvp in _vm.typeTable)
                {
                    var internedType = kvp.Value;
                    var compiledType = idToTypeTable[internedType.typeId];
                    var members = new List<TypeDefinitionMember>();

                    foreach (var field in internedType.fields)
                    {
                        var fieldName = field.Key;
                        var internedField = field.Value;
                        ITypeReferenceNode fieldTypeNode = null;

                        var compiledField = new CompiledTypeMember
                        {
                            Length = internedField.length,
                            Offset = internedField.offset,
                            TypeCode = internedField.typeCode
                        };
                        if (internedField.typeId > 0)
                        {
                            if (!idToTypeTable.TryGetValue(internedField.typeId, out var linkedType))
                            {
                                throw new NotSupportedException("invalid type linkage in watch");
                            }
                            else
                            {
                                compiledField.Type = linkedType;
                                fieldTypeNode =
                                    new StructTypeReferenceNode(new VariableRefNode(Token.Blank, linkedType.typeName));
                            }
                        }
                        else
                        {
                            if (!VmUtil.TryGetVariableType(field.Value.typeCode, out var typeInfo))
                            {
                                throw new NotSupportedException(
                                    "Unknown type in watch field, " + internedField.typeCode);
                            }

                            fieldTypeNode = new TypeReferenceNode(typeInfo, Token.Blank);
                        }


                        members.Add(new TypeDefinitionMember(Token.Blank, Token.Blank,
                            new VariableRefNode(Token.Blank, field.Key), fieldTypeNode));
                        
                        compiledType.fields.Add(fieldName, compiledField);
                    }
                    
                    
                    var typeDef = new TypeDefinitionStatement(Token.Blank, Token.Blank,
                        new VariableRefNode(Token.Blank, internedType.name), members);
                    node.typeDefinitions.Add(typeDef);
                }
            }
            
            
            
            void AddVariable(DebugVariable local, bool isGlobal)
            {
                ITypeReferenceNode declType = null;
                string structName = null;
                var variable = variableDb.GetRuntimeVariable(local);
                
                var arrayLength = variable.GetElementCount(out var arrayRankCount, out var isArray);
                // if (isArray) throw new NotImplementedException("handle arrays, kid!");
                
                if (!TypeInfo.TryGetFromTypeCode(variable.typeCode, out var typeInfo))
                {
                    throw new InvalidOperationException($"unknown type code=[{variable.typeCode}] in eval function");
                }

                var typeCode = variable.typeCode;
                if (isArray)
                {
                    typeCode = variable.allocation.format.typeCode;
                }
                
                if (typeCode == TypeCodes.STRUCT )
                {
                    // structName = variable.GetTypeName();
                    structName = idToTypeTable[variable.allocation.format.typeId].typeName;
                    declType = new StructTypeReferenceNode(
                        new VariableRefNode(new Token(), structName));
                }
                else
                {
                    declType = new TypeReferenceNode(typeInfo.type, new Token());
                }

                if (!isArray)
                {
                    var compiledVariable = new CompiledVariable
                    {
                        typeCode = variable.typeCode,
                        name = local.name,
                        registerAddress = variable.regAddr,
                        byteSize = TypeCodes.GetByteSize(variable.typeCode),
                        structType = structName,
                        isGlobal = isGlobal
                    }; // TODO: support arrays
                    if (isGlobal)
                    {
                        globalVariableTable.Add(local.name, compiledVariable);
                    }
                    else
                    {
                        localVariableTable.Add(local.name, compiledVariable);
                    }


                    var decl = new DeclarationStatement(new Token
                        {
                            caseInsensitiveRaw = isGlobal ? "global" : "local"
                        }, new VariableRefNode(new Token(), local.name),
                        declType);

                    node.statements.Insert(0, decl);
                }
                else
                {
                    var elementSize = (int)TypeCodes.GetByteSize(typeCode);
                    CompiledType structType = null;
                    if (typeCode == TypeCodes.STRUCT)
                    {
                        structType = idToTypeTable[variable.allocation.format.typeId];
                        elementSize = structType.byteSize;
                    }

                    var compiledArrayVariable = new CompiledArrayVariable
                    {
                        name = variable.name,
                        typeCode = variable.typeCode,
                        structType = structType,
                        byteSize = elementSize,
                        registerAddress = (byte)variable.regAddr,
                        isGlobal = isGlobal,
                        rankSizeRegisterAddresses = new byte[arrayRankCount],
                        rankIndexScalerRegisterAddresses = new byte[arrayRankCount]
                    };
                    var rankExprs = new IExpressionNode[arrayRankCount];
                    for (var i = 0; i < arrayRankCount; i++)
                    {
                        var rankStrideRegAddr = variable.regAddr + (ulong)arrayRankCount * 2 - ((ulong)i * 2) ;
                        var rankSizeRegAddr = rankStrideRegAddr - 1;
                        var rankSize = _vm.scopeStack.buffer[variable.scopeIndex]
                            .dataRegisters[rankSizeRegAddr];
                        var rankStride = _vm.scopeStack.buffer[variable.scopeIndex]
                            .dataRegisters[rankStrideRegAddr];

                        var revI = arrayRankCount - i - 1;
                        compiledArrayVariable.rankSizeRegisterAddresses[i] = (byte)(rankSizeRegAddr);
                        compiledArrayVariable.rankIndexScalerRegisterAddresses[i] = (byte)(rankStrideRegAddr);
                        // variable.
                        rankExprs[i] = new LiteralIntExpression(Token.Blank, (int)rankSize);
                    }

                    var decl = new DeclarationStatement(Token.Blank, new VariableRefNode(Token.Blank, local.name),
                        declType, rankExprs);
                    node.statements.Insert(0, decl);

                    var selectedTable = isGlobal ? globalArrayVariableTable : localArrayVariableTable;
                    selectedTable.Add(variable.name, compiledArrayVariable);
                }
            }
            
           
            
            foreach (var global in globals.variables)
            {
                AddVariable(global, true);
            }
            foreach (var local in locals.variables)
            {
                AddVariable(local, false);
            }

            { // add in the functions
                foreach (var kvp in _vm.internedData.functions)
                {
                    var funcName = kvp.Key;
                    var func = kvp.Value;

                    var statement = new FunctionStatement
                    {
                        name = funcName,
                        nameToken = Token.Blank,
                    };
                    
                    // foreach (var internedParam in func.parameters)
                    for (var i = func.parameters.Count; i > 0 ; i --)
                    {
                        var internedParam = func.parameters[i - 1];
                        
                        ITypeReferenceNode t = null;
                        if (internedParam.typeId > 0)
                        {
                            t = new StructTypeReferenceNode(new VariableRefNode(Token.Blank,
                                _vm.typeTable[internedParam.typeId].name));
                        }
                        else
                        {
                            if (!VmUtil.TryGetVariableType(internedParam.typeCode, out var vt))
                            {
                                throw new NotSupportedException("invalid type code for function parameter");
                            }
                            t = new TypeReferenceNode(vt, Token.Blank);
                        }

                        var p = new ParameterNode(new VariableRefNode(Token.Blank, internedParam.name), t);
                        statement.parameters.Add(p);

                    }
                    
                    node.functions.Add(statement);
                }
            }


            var knownFunctionTypes = _vm.internedData.functions.ToDictionary(kvp => kvp.Key, kvp =>
            {
                if (kvp.Value.typeId == -1)
                {
                    return TypeInfo.Void;
                } 
                else if (kvp.Value.typeId > 0)
                {
                    return new TypeInfo
                    {
                        structName = _vm.typeTable[kvp.Value.typeId].name,
                        type = VariableType.Struct
                    };
                }
                else if (VmUtil.TryGetVariableType(kvp.Value.typeCode, out var vt))
                {
                    return new TypeInfo
                    {
                        type = vt
                    };
                }
                else
                {
                    throw new NotSupportedException("Unknown variable code for function");
                }
            });
            node.AddScopeRelatedErrors(new ParseOptions(), knownFunctionTypes);

            var finalStatement = (AssignmentStatement)node.statements.LastOrDefault(x => x is AssignmentStatement);
            var finalDeclare = (DeclarationStatement)node.statements.LastOrDefault(x => x is DeclarationStatement);

            if (finalStatement == null)
            {
                return DebugEvalResult.Failed("only declarations are allowed");
            }

            if (finalDeclare == null || finalDeclare.variable != SYNTHETIC_NAME2)
            {
                finalDeclare = null;

                var finalType = finalStatement.expression.ParsedType.type;
                if (overwriteVariableId > 1 && variableDb.TryGetTypeCodeForVariableId(overwriteVariableId, out var finaltypeCode))
                {
                    if (!VmUtil.TryGetVariableType(finaltypeCode, out finalType))
                    {
                        throw new Exception("invalid type code");
                    }
                }

                finalDeclare = new DeclarationStatement(new Token
                    {
                        caseInsensitiveRaw = "local"
                    }, new VariableRefNode(Token.Blank, SYNTHETIC_NAME2),
                    new TypeReferenceNode(finalType, Token.Blank));
                node.statements.Insert(node.statements.Count - 1, finalDeclare);
            }
            finalStatement.variable.Errors.Clear(); // remove the cast related error
            
            var parseErrors = node.GetAllErrors();
            Launch.DebugVariable match = null;
            DebugEvalResult quickResult = null;
            if (finalStatement.expression is VariableRefNode rhs)
            {
                match = locals.variables.FirstOrDefault(x => x.name == rhs.variableName);
                if (match == null)
                {
                    match = globals.variables.FirstOrDefault(x => x.name == rhs.variableName);
                }
            }
            if (match != null)
            {
                quickResult = new DebugEvalResult
                {
                    value = match.value,
                    type = match.type,
                    fieldCount = match.fieldCount,
                    elementCount = match.elementCount,
                    id = match.id,
                };

                if (quickResult.fieldCount > 0 || quickResult.elementCount > 0)
                {
                    quickResult.scope = variableDb.Expand(match.id);
                }
            }

            // VS Code's default word pattern strips sigils ($/#), so "name$" becomes "name".
            // This helper compares a stored field name against a lookup name with sigil tolerance.
            bool FieldNameMatches(string storedName, string lookupName)
            {
                if (string.Equals(storedName, lookupName, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (storedName.Length > 0 && (storedName[storedName.Length - 1] == '$' || storedName[storedName.Length - 1] == '#'))
                    return string.Equals(storedName.Substring(0, storedName.Length - 1), lookupName, StringComparison.OrdinalIgnoreCase);
                return false;
            }

            // For struct field references (e.g. "p.name$" or "people(0).name$"), walk the
            // left chain to find the root variable, then expand each level through the
            // variable database to read the live value directly.
            if (quickResult == null && finalStatement.expression is StructFieldReference fieldRef)
            {
                // Collect the field chain from right to left.
                var fieldChain = new List<string>();
                IExpressionNode current = fieldRef;
                while (current is StructFieldReference sfr)
                {
                    if (sfr.right is VariableRefNode rightVar)
                        fieldChain.Add(rightVar.variableName);
                    current = sfr.left;
                }

                // current is either a plain variable (VariableRefNode) or an array access (ArrayIndexReference)
                string rootName = null;
                int arrayIndex = -1;
                if (current is ArrayIndexReference arrayRef)
                {
                    rootName = arrayRef.variableName;
                    // Try to extract a constant index from the first rank expression
                    if (arrayRef.rankExpressions.Count > 0 && arrayRef.rankExpressions[0] is LiteralIntExpression litInt)
                        arrayIndex = litInt.value;
                }
                else if (current is VariableRefNode rootVar)
                {
                    rootName = rootVar.variableName;
                }

                if (rootName != null)
                {
                    var rootMatch = locals.variables.FirstOrDefault(x => x.name == rootName)
                                 ?? globals.variables.FirstOrDefault(x => x.name == rootName);

                    if (rootMatch != null && (rootMatch.fieldCount > 0 || (rootMatch.elementCount > 0 && arrayIndex >= 0)))
                    {
                        Launch.DebugVariable currentVar = rootMatch;

                        // If the root is an array, expand into the array and pick the element
                        if (arrayIndex >= 0 && currentVar.elementCount > 0)
                        {
                            var arrayScope = variableDb.Expand(currentVar.id);
                            if (arrayIndex < arrayScope.variables.Count)
                                currentVar = arrayScope.variables[arrayIndex];
                            else
                                currentVar = null;
                        }

                        // Walk down the field chain, expanding at each level
                        bool fieldResolved = currentVar != null;
                        if (fieldResolved)
                        {
                            for (int i = fieldChain.Count - 1; i >= 0; i--)
                            {
                                var scope = variableDb.Expand(currentVar.id);
                                var fieldMatch = scope.variables.FirstOrDefault(
                                    v => FieldNameMatches(v.name, fieldChain[i]));
                                if (fieldMatch == null)
                                {
                                    fieldResolved = false;
                                    break;
                                }
                                currentVar = fieldMatch;
                            }
                        }

                        if (fieldResolved)
                        {
                            quickResult = new DebugEvalResult
                            {
                                value = currentVar.value,
                                type = currentVar.type,
                                fieldCount = currentVar.fieldCount,
                                elementCount = currentVar.elementCount,
                                id = currentVar.id,
                            };
                            if (quickResult.fieldCount > 0 || quickResult.elementCount > 0)
                            {
                                quickResult.scope = variableDb.Expand(currentVar.id);
                            }
                        }
                    }
                }
            }

            // When hovering over a bare field name (e.g. "field" from "myStruct.field"),
            // VS Code sends just the word under the cursor. If it didn't match any
            // local/global variable, check whether it's a field on a struct in scope.
            if (quickResult == null && match == null && finalStatement.expression is VariableRefNode bareField)
            {
                string resolvedFieldExpr = null;
                bool fieldAmbiguous = false;

                foreach (var v in locals.variables.Concat(globals.variables))
                {
                    if (v.fieldCount > 0)
                    {
                        // Direct struct variable
                        var subScope = variableDb.Expand(v.id);
                        foreach (var sv in subScope.variables)
                        {
                            if (FieldNameMatches(sv.name, bareField.variableName))
                            {
                                if (resolvedFieldExpr != null) { fieldAmbiguous = true; break; }
                                resolvedFieldExpr = v.name + "." + sv.name;
                            }
                        }
                    }
                    else if (v.elementCount > 0)
                    {
                        // Array — check if it's an array-of-structs by expanding
                        // to get elements and checking if first element has fields.
                        var arrayScope = variableDb.Expand(v.id);
                        if (arrayScope.variables.Count > 0 && arrayScope.variables[0].fieldCount > 0)
                        {
                            var elemScope = variableDb.Expand(arrayScope.variables[0].id);
                            foreach (var sv in elemScope.variables)
                            {
                                if (FieldNameMatches(sv.name, bareField.variableName))
                                {
                                    if (resolvedFieldExpr != null) { fieldAmbiguous = true; break; }
                                    // Use element 0 as best guess — we don't know the index
                                    resolvedFieldExpr = v.name + "(0)." + sv.name;
                                }
                            }
                        }
                    }
                    if (fieldAmbiguous) break;
                }

                if (resolvedFieldExpr != null)
                {
                    // When ambiguous (field exists on multiple structs), use the first
                    // match rather than returning an error — showing some value is better
                    // than "Invalid Reference" on hover.
                    return Eval(frameId, resolvedFieldExpr, overwriteVariableId);
                }
            }

            if (parseErrors.Count > 0)
            {
                // If the quick-result path already resolved the value (e.g. via struct
                // field expansion), return it even if the scope error visitor reported
                // errors like "Member not declared" or "Invalid Reference".
                if (quickResult != null) return quickResult;

                return DebugEvalResult.Failed($"{string.Join(",\n", parseErrors.Select(x => x.errorCode))}");
            }

            if (quickResult != null)
            {
                if (overwriteVariableId > 0)
                {
                    if (!variableDb.TrySetValue(overwriteVariableId, quickResult.id, out var setErr))
                    {
                        return DebugEvalResult.Failed(setErr);
                    }
                }
                return quickResult;
            }
            
            
            /*
             * when the compilation happens, any references to existing variables and functions
             * need to get mapped into the current program space
             */

            var mergedVariableTable = new Dictionary<string, CompiledVariable>(globalVariableTable);
            var mergedArrayTable = new Dictionary<string, CompiledArrayVariable>(globalArrayVariableTable);
            foreach (var kvp in localVariableTable)
            {
                mergedVariableTable[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in localArrayVariableTable)
            {
                mergedArrayTable[kvp.Key] = kvp.Value;
            }

            var compileScope = new CompileScope(mergedVariableTable, mergedArrayTable);
            var compiler = new Compiler(_commandCollection, new CompilerOptions
            {
                GenerateDebugData = true,
                InternStrings = false
            }, givenGlobalScope: compileScope);

            foreach (var type in types)
            {
                compiler.AddType(type);
            }

            foreach (var func in _vm.internedData.functions)
            {
                compiler.AddFunction(func.Key, func.Value.insIndex);
            }
            
            // only compile the last statement, because this only evaluates one statement at a time.
            if (finalDeclare != null)
            {
                compiler.Compile(finalDeclare);
            }

            compiler.Compile(finalStatement);
            compiler.CompileJumpReplacements();
            
            byte[] originalProgram = _vm.program;
            var originalInstructionIndex = _vm.instructionIndex;
            var originalState = _vm.state;

            // make a backup of the entire scope state of the vm...
            var originalScopeStack = FastStack<VirtualScope>.Copy(_vm.scopeStack);
            var reverseIndex = _vm.scopeStack.Count - (frameId + 1);
            var originalScopePtr = _vm.scopeStack.ptr;
            var originalStack = _vm.stack;
            var originalScope = _vm.scope;

            // Deep-save globalScope arrays so STORE_GLOBAL/STORE_PTR_GLOBAL writes during
            // eval do not permanently corrupt global variable state.
            var savedGlobalData       = (ulong[])_vm.globalScope.dataRegisters.Clone();
            var savedGlobalTypes      = (byte[]) _vm.globalScope.typeRegisters.Clone();
            var savedGlobalInsIndexes = (int[])  _vm.globalScope.insIndexes.Clone();
            var savedGlobalFlags      = (byte[]) _vm.globalScope.flags.Clone();

            var fakeScopeStack = new FastStack<VirtualScope>(2);
            fakeScopeStack.Push(_vm.globalScope);
            if (_vm.scopeStack.Count > 1)
            {
                fakeScopeStack.Push(originalScopeStack.buffer[reverseIndex]);
            }
            _vm.scopeStack = fakeScopeStack;

            var newProgram = new List<byte>();
            newProgram.AddRange(_vm.program);
            newProgram.AddRange(compiler.Program);

            _vm.instructionIndex = _vm.program.Length;
            _vm.program = newProgram.ToArray();
            _vm.stack = new FastStack<byte>(8);
            _vm.state = new VirtualMachine.VmState
            {
            };
            _vm.scope = _vm.scopeStack.buffer[_vm.scopeStack.ptr - 1];

            // Always isolate the working scope with fresh array copies so that any writes
            // during eval do not bleed into the original scope's arrays via the shared
            // reference left behind by FastStack<VirtualScope>.Copy.
            _vm.scope.dataRegisters = (ulong[])_vm.scope.dataRegisters.Clone();
            _vm.scope.typeRegisters = (byte[]) _vm.scope.typeRegisters.Clone();
            _vm.scope.insIndexes    = (int[])  _vm.scope.insIndexes.Clone();
            _vm.scope.flags         = (byte[]) _vm.scope.flags.Clone();

            var originalError = _vm.error;
            _vm.error = default;

            if (!compiler.scopeStack.Peek().TryGetVariable(SYNTHETIC_NAME2, out var synth))
            {
                throw new NotSupportedException("no compiled synthetic");
            }

            // Grow the (already isolated) scope arrays if the synthetic register falls outside
            // current capacity.  The arrays are fresh clones at this point, so resizing them
            // only affects the eval's private copy and never touches the original scope.
            var requiredCapacity = (int)synth.registerAddress + 1;
            if (requiredCapacity > _vm.scope.dataRegisters.Length)
            {
                Array.Resize(ref _vm.scope.dataRegisters, requiredCapacity);
                Array.Resize(ref _vm.scope.typeRegisters, requiredCapacity);
                Array.Resize(ref _vm.scope.insIndexes, requiredCapacity);
                Array.Resize(ref _vm.scope.flags, requiredCapacity);
            }

            DebugEvalResult result = null;
            DebugRuntimeVariable runtimeSynth = null;
            try
            {
                try
                {
                    while (_vm.instructionIndex < _vm.program.Length
                           && _vm.error.type == VirtualRuntimeErrorType.NONE)
                    {
                        _vm.Execute2(_vm.program.Length - _vm.instructionIndex);
                    }
                }
                catch (Exception vmEx)
                {
                    return DebugEvalResult.Failed($"eval exception: {vmEx.Message}");
                }

                if (_vm.error.type != VirtualRuntimeErrorType.NONE)
                    return DebugEvalResult.Failed($"eval error: {_vm.error.message}");

                if (synth.typeCode == TypeCodes.STRUCT)
                {
                    var ptr = _vm.dataRegisters[synth.registerAddress];
                    if (!_vm.heap.TryGetAllocation(VmPtr.FromRaw(ptr), out var alloc))
                    {
                        return DebugEvalResult.Failed(
                            $"invalid heap, reg=[{synth.registerAddress}] data=[{ptr}] which is not a valid heap pointer");
                    }

                    runtimeSynth = new DebugRuntimeVariable(_vm, SYNTHETIC_NAME2, synth.typeCode,
                        _vm.dataRegisters[synth.registerAddress],
                        ref alloc, _vm.scopeStack.Count - frameId, synth.registerAddress);
                }
                else
                {
                    runtimeSynth = new DebugRuntimeVariable(_vm, SYNTHETIC_NAME2, synth.typeCode,
                        _vm.dataRegisters[synth.registerAddress],
                        _vm.scopeStack.Count - frameId, synth.registerAddress);
                }

                result = variableDb.AddWatchedExpression(runtimeSynth, synth);
                var srcScope = _vm.scopeStack.buffer[runtimeSynth.scopeIndex];
                var srcRaw = _vm.dataRegisters[runtimeSynth.regAddr];
            }
            finally
            {
                _vm.instructionIndex = originalInstructionIndex;
                _vm.program = originalProgram;
                _vm.state = originalState;
                _vm.scopeStack = originalScopeStack;
                _vm.scopeStack.ptr = originalScopePtr;
                _vm.stack = originalStack;
                _vm.scope = originalScope;
                _vm.error = originalError;

                // Restore globalScope array contents that may have been written by
                // STORE_GLOBAL / STORE_PTR_GLOBAL instructions during eval.
                Array.Copy(savedGlobalData,       _vm.globalScope.dataRegisters, savedGlobalData.Length);
                Array.Copy(savedGlobalTypes,      _vm.globalScope.typeRegisters, savedGlobalTypes.Length);
                Array.Copy(savedGlobalInsIndexes, _vm.globalScope.insIndexes,    savedGlobalInsIndexes.Length);
                Array.Copy(savedGlobalFlags,      _vm.globalScope.flags,         savedGlobalFlags.Length);
            }

            if (overwriteVariableId > -1)
            {
                if (runtimeSynth == null)
                    return DebugEvalResult.Failed("eval produced no result");
                if (!variableDb.TrySetValue(overwriteVariableId, runtimeSynth, out var setErr))
                    return DebugEvalResult.Failed(setErr);
            }

            return result;
        }

        /// <summary>
        /// Executes one or more FadeBasic statements in the REPL (Debug Console).
        /// Unlike <see cref="Eval"/>, mutations to existing variables ARE persisted back into
        /// the live VM state.  The VM execution context (instruction pointer, call stack,
        /// byte-code program) is fully restored so the paused program can continue normally.
        /// </summary>
        public DebugEvalResult ReplExec(int frameId, string code)
        {
            // ── 1. Lex ────────────────────────────────────────────────────────────────
            var lexer = new Lexer();
            var lexResults = lexer.TokenizeWithErrors(code, _commandCollection);
            if (lexResults.tokenErrors.Count > 0)
                return DebugEvalResult.Failed("Unable to lex REPL input");

            // ── 2. Parse (multiple statements allowed) ────────────────────────────────
            var parser = new Parser(lexResults.stream, _commandCollection);
            var node = parser.ParseProgram(new ParseOptions { ignoreChecks = true });

            // If parsing produced errors or the result is a bare expression,
            // delegate to Eval which already handles type inference correctly.
            // This handles cases like "x", "x + 1", "alan.x" that the parser
            // can't treat as standalone statements.
            var earlyErrors = node.GetAllErrors();
            bool isBareExpr = (node.statements.Count > 0 && node.statements[node.statements.Count - 1] is ExpressionStatement);
            if (isBareExpr || earlyErrors.Count > 0)
            {
                try
                {
                    var evalResult = Eval(frameId, code);
                    if (evalResult != null && evalResult.id >= 0)
                        return evalResult;
                }
                catch
                {
                    // If Eval also fails, fall through and let REPL report its own error.
                }
            }

            // Capture the user's statements BEFORE variable-context declarations are injected.
            var userStatements = new List<IStatementNode>(node.statements);

            // ── 3. Build type table (same as Eval) ───────────────────────────────────
            var types = new List<CompiledType>();
            var idToTypeTable = new Dictionary<int, CompiledType>();
            foreach (var kvp in _vm.typeTable)
            {
                var internedType = kvp.Value;
                var compileType = new CompiledType
                {
                    typeName = internedType.name,
                    typeId = internedType.typeId,
                    byteSize = internedType.byteSize,
                    fields = new Dictionary<string, CompiledTypeMember>()
                };
                idToTypeTable[compileType.typeId] = compileType;
                types.Add(compileType);
            }
            foreach (var kvp in _vm.typeTable)
            {
                var internedType = kvp.Value;
                var compiledType = idToTypeTable[internedType.typeId];
                var members = new List<TypeDefinitionMember>();
                foreach (var field in internedType.fields)
                {
                    var fieldName = field.Key;
                    var internedField = field.Value;
                    ITypeReferenceNode fieldTypeNode;
                    var compiledField = new CompiledTypeMember
                    {
                        Length = internedField.length,
                        Offset = internedField.offset,
                        TypeCode = internedField.typeCode
                    };
                    if (internedField.typeId > 0)
                    {
                        if (!idToTypeTable.TryGetValue(internedField.typeId, out var linkedType))
                            throw new NotSupportedException("invalid type linkage in REPL");
                        compiledField.Type = linkedType;
                        fieldTypeNode = new StructTypeReferenceNode(new VariableRefNode(Token.Blank, linkedType.typeName));
                    }
                    else
                    {
                        if (!VmUtil.TryGetVariableType(field.Value.typeCode, out var typeInfo))
                            throw new NotSupportedException("Unknown type in REPL field: " + internedField.typeCode);
                        fieldTypeNode = new TypeReferenceNode(typeInfo, Token.Blank);
                    }
                    members.Add(new TypeDefinitionMember(Token.Blank, Token.Blank,
                        new VariableRefNode(Token.Blank, field.Key), fieldTypeNode));
                    compiledType.fields.Add(fieldName, compiledField);
                }
                var typeDef = new TypeDefinitionStatement(Token.Blank, Token.Blank,
                    new VariableRefNode(Token.Blank, internedType.name), members);
                node.typeDefinitions.Add(typeDef);
            }

            // ── 4. Inject variable context (same as Eval) ────────────────────────────
            var globalVariableTable = new Dictionary<string, CompiledVariable>();
            var localVariableTable  = new Dictionary<string, CompiledVariable>();
            var globalArrayVariableTable = new Dictionary<string, CompiledArrayVariable>();
            var localArrayVariableTable  = new Dictionary<string, CompiledArrayVariable>();

            var locals  = variableDb.GetLocalVariablesForFrame(frameId);
            var globals = variableDb.GetGlobalVariablesForFrame(frameId);

            void AddVariable(DebugVariable local, bool isGlobal)
            {
                var variable = variableDb.GetRuntimeVariable(local);
                var arrayLength = variable.GetElementCount(out var arrayRankCount, out var isArray);
                if (!TypeInfo.TryGetFromTypeCode(variable.typeCode, out var typeInfo))
                    throw new InvalidOperationException($"unknown type code=[{variable.typeCode}] in REPL");

                var typeCode = isArray ? variable.allocation.format.typeCode : variable.typeCode;
                ITypeReferenceNode declType;
                string structName = null;
                if (typeCode == TypeCodes.STRUCT)
                {
                    structName = idToTypeTable[variable.allocation.format.typeId].typeName;
                    declType = new StructTypeReferenceNode(new VariableRefNode(new Token(), structName));
                }
                else
                {
                    declType = new TypeReferenceNode(typeInfo.type, new Token());
                }

                if (!isArray)
                {
                    var compiledVariable = new CompiledVariable
                    {
                        typeCode = variable.typeCode,
                        name = local.name,
                        registerAddress = variable.regAddr,
                        byteSize = TypeCodes.GetByteSize(variable.typeCode),
                        structType = structName,
                        isGlobal = isGlobal
                    };
                    if (isGlobal) globalVariableTable.Add(local.name, compiledVariable);
                    else          localVariableTable.Add(local.name, compiledVariable);

                    node.statements.Insert(0, new DeclarationStatement(new Token
                        { caseInsensitiveRaw = isGlobal ? "global" : "local" },
                        new VariableRefNode(new Token(), local.name), declType));
                }
                else
                {
                    var elementSize = (int)TypeCodes.GetByteSize(typeCode);
                    CompiledType structType = null;
                    if (typeCode == TypeCodes.STRUCT)
                    {
                        structType = idToTypeTable[variable.allocation.format.typeId];
                        elementSize = structType.byteSize;
                    }
                    var compiledArrayVariable = new CompiledArrayVariable
                    {
                        name = variable.name,
                        typeCode = variable.typeCode,
                        structType = structType,
                        byteSize = elementSize,
                        registerAddress = (byte)variable.regAddr,
                        isGlobal = isGlobal,
                        rankSizeRegisterAddresses = new byte[arrayRankCount],
                        rankIndexScalerRegisterAddresses = new byte[arrayRankCount]
                    };
                    var rankExprs = new IExpressionNode[arrayRankCount];
                    for (var i = 0; i < arrayRankCount; i++)
                    {
                        var rankStrideRegAddr = variable.regAddr + (ulong)arrayRankCount * 2 - ((ulong)i * 2);
                        var rankSizeRegAddr   = rankStrideRegAddr - 1;
                        var rankSize = _vm.scopeStack.buffer[variable.scopeIndex].dataRegisters[rankSizeRegAddr];
                        compiledArrayVariable.rankSizeRegisterAddresses[i]           = (byte)rankSizeRegAddr;
                        compiledArrayVariable.rankIndexScalerRegisterAddresses[i]    = (byte)rankStrideRegAddr;
                        rankExprs[i] = new LiteralIntExpression(Token.Blank, (int)rankSize);
                    }
                    node.statements.Insert(0, new DeclarationStatement(Token.Blank,
                        new VariableRefNode(Token.Blank, local.name), declType, rankExprs));
                    (isGlobal ? globalArrayVariableTable : localArrayVariableTable).Add(variable.name, compiledArrayVariable);
                }
            }

            foreach (var g in globals.variables) AddVariable(g, true);
            foreach (var l in locals.variables)  AddVariable(l, false);

            // ── 5. Inject function stubs (same as Eval) ──────────────────────────────
            foreach (var kvp in _vm.internedData.functions)
            {
                var funcName = kvp.Key;
                var func     = kvp.Value;
                var statement = new FunctionStatement { name = funcName, nameToken = Token.Blank };
                for (var i = func.parameters.Count; i > 0; i--)
                {
                    var internedParam = func.parameters[i - 1];
                    ITypeReferenceNode t;
                    if (internedParam.typeId > 0)
                        t = new StructTypeReferenceNode(new VariableRefNode(Token.Blank, _vm.typeTable[internedParam.typeId].name));
                    else
                    {
                        if (!VmUtil.TryGetVariableType(internedParam.typeCode, out var vt))
                            throw new NotSupportedException("invalid type code for function parameter");
                        t = new TypeReferenceNode(vt, Token.Blank);
                    }
                    statement.parameters.Add(new ParameterNode(new VariableRefNode(Token.Blank, internedParam.name), t));
                }
                node.functions.Add(statement);
            }

            var knownFunctionTypes = _vm.internedData.functions.ToDictionary(kvp => kvp.Key, kvp =>
            {
                if (kvp.Value.typeId == -1) return TypeInfo.Void;
                if (kvp.Value.typeId > 0)
                    return new TypeInfo { structName = _vm.typeTable[kvp.Value.typeId].name, type = VariableType.Struct };
                if (VmUtil.TryGetVariableType(kvp.Value.typeCode, out var vt))
                    return new TypeInfo { type = vt };
                throw new NotSupportedException("Unknown variable code for function");
            });
            node.AddScopeRelatedErrors(new ParseOptions(), knownFunctionTypes);

            var parseErrors = node.GetAllErrors();
            if (parseErrors.Count > 0)
                return DebugEvalResult.Failed(string.Join(",\n", parseErrors.Select(x => x.errorCode)));

            // ── 6. Build merged variable tables ──────────────────────────────────────
            var mergedVariableTable = new Dictionary<string, CompiledVariable>(globalVariableTable);
            var mergedArrayTable    = new Dictionary<string, CompiledArrayVariable>(globalArrayVariableTable);
            foreach (var kvp in localVariableTable)      mergedVariableTable[kvp.Key] = kvp.Value;
            foreach (var kvp in localArrayVariableTable) mergedArrayTable[kvp.Key]    = kvp.Value;

            // ── 7. Compile user statements ───────────────────────────────────────────
            // CompileScope stores _varToReg = mergedVariableTable (same reference).
            // Any new variables the compiler allocates via Create() will appear in
            // mergedVariableTable after compilation — we use that to detect new vars below.
            var existingVarNames = new HashSet<string>(mergedVariableTable.Keys);

            var compileScope = new CompileScope(mergedVariableTable, mergedArrayTable);
            var compiler = new Compiler(_commandCollection, new CompilerOptions
            {
                GenerateDebugData = false,
                InternStrings = false
            }, givenGlobalScope: compileScope);

            foreach (var type in types) compiler.AddType(type);
            foreach (var func in _vm.internedData.functions) compiler.AddFunction(func.Key, func.Value.insIndex);

            foreach (var stmt in userStatements)
                compiler.Compile(stmt);
            compiler.CompileJumpReplacements();

            if (compiler.Program.Count == 0)
                return DebugEvalResult.Failed("REPL produced no bytecode");

            // ── 8. Detect vars the compiler newly allocated ──────────────────────────
            // For scalars (e.g. `a = 1`), the scope visitor does NOT insert an implicit
            // DeclarationStatement — it just adds the symbol to its table.  The compiler
            // then auto-allocates a register via compileScope.Create().  Since _varToReg
            // inside CompileScope IS mergedVariableTable, those entries are visible here.
            var newVarDecls = new List<(string name, byte typeCode, ulong regAddr)>();
            foreach (var kvp in mergedVariableTable)
            {
                if (existingVarNames.Contains(kvp.Key)) continue;
                newVarDecls.Add((kvp.Key, kvp.Value.typeCode, kvp.Value.registerAddress));
            }

            // Resize the LIVE local scope arrays before saving VM state, so the
            // resized arrays are captured in originalScope / originalScopeStack.
            var vmScopeIdxForRepl = _vm.scopeStack.ptr - 1;
            if (newVarDecls.Count > 0)
            {
                var maxNewReg = 0UL;
                foreach (var (_, _, ra) in newVarDecls)
                    if (ra > maxNewReg) maxNewReg = ra;
                var requiredSize = (int)(maxNewReg + 1);
                var liveScope = _vm.scopeStack.buffer[vmScopeIdxForRepl];
                if (requiredSize > liveScope.dataRegisters.Length)
                {
                    Array.Resize(ref liveScope.dataRegisters, requiredSize);
                    Array.Resize(ref liveScope.typeRegisters,  requiredSize);
                    Array.Resize(ref liveScope.insIndexes,     requiredSize);
                    Array.Resize(ref liveScope.flags,          requiredSize);
                    _vm.scopeStack.buffer[vmScopeIdxForRepl] = liveScope;
                    if (vmScopeIdxForRepl == _vm.scopeStack.ptr - 1)
                    {
                        _vm.scope.dataRegisters = liveScope.dataRegisters;
                        _vm.scope.typeRegisters  = liveScope.typeRegisters;
                        _vm.scope.insIndexes     = liveScope.insIndexes;
                        _vm.scope.flags          = liveScope.flags;
                    }
                }
            }

            // ── 9. Save VM execution state (after resize so saved arrays are current) ─
            var originalProgram          = _vm.program;
            var originalInstructionIndex = _vm.instructionIndex;
            var originalState            = _vm.state;
            var originalScopeStack       = FastStack<VirtualScope>.Copy(_vm.scopeStack);
            var reverseIndex             = _vm.scopeStack.Count - (frameId + 1);
            var originalScopePtr         = _vm.scopeStack.ptr;
            var originalStack            = _vm.stack;
            var originalScope            = _vm.scope;
            var originalError            = _vm.error;
            var originalMethodStack      = FastStack<JumpHistoryData>.Copy(_vm.methodStack);
            // NOTE: globalScope arrays and local dataRegisters/typeRegisters are NOT saved —
            // REPL writes to variables are intentionally persistent.

            var fakeScopeStack = new FastStack<VirtualScope>(2);
            fakeScopeStack.Push(_vm.globalScope);
            if (_vm.scopeStack.Count > 1)
                fakeScopeStack.Push(originalScopeStack.buffer[reverseIndex]);
            _vm.scopeStack = fakeScopeStack;

            var newProgram = new List<byte>(_vm.program);
            newProgram.AddRange(compiler.Program);
            _vm.instructionIndex = _vm.program.Length;
            _vm.program          = newProgram.ToArray();
            _vm.stack            = new FastStack<byte>(8);
            _vm.state            = new VirtualMachine.VmState();
            _vm.scope            = _vm.scopeStack.buffer[_vm.scopeStack.ptr - 1];

            // REPL: do NOT clone dataRegisters or typeRegisters — writes must persist.
            // Clone insIndexes and flags to protect gosub/call-frame bookkeeping.
            _vm.scope.insIndexes = (int[])_vm.scope.insIndexes.Clone();
            _vm.scope.flags      = (byte[])_vm.scope.flags.Clone();

            _vm.error = default;

            // ── 10. Execute ───────────────────────────────────────────────────────────
            DebugEvalResult result = null;
            try
            {
                try
                {
                    while (_vm.instructionIndex < _vm.program.Length
                           && _vm.error.type == VirtualRuntimeErrorType.NONE)
                    {
                        _vm.Execute2(_vm.program.Length - _vm.instructionIndex);
                    }
                }
                catch (Exception vmEx)
                {
                    return DebugEvalResult.Failed($"REPL exception: {vmEx.Message}");
                }

                if (_vm.error.type != VirtualRuntimeErrorType.NONE)
                    return DebugEvalResult.Failed($"REPL error: {_vm.error.message}");

                // Register new variables with the variable database so they appear in the
                // variables panel and survive subsequent ClearLifetime() calls.
                foreach (var (name, typeCode, regAddr) in newVarDecls)
                    variableDb.AddReplVar(name, typeCode, regAddr, vmScopeIdxForRepl);

                // Invalidate the cached local scope so the next REQUEST_SCOPES includes the new vars.
                variableDb.InvalidateLocalScope(frameId);

                result = new DebugEvalResult { value = "", type = "void", id = 0 };

                // Try to produce a useful result for the debug console.
                // If the last user statement was an assignment (or a bare expression
                // rewritten to a synthetic assignment), read back the variable's value
                // directly from the VM registers.
                if (userStatements.Count > 0)
                {
                    var lastStmt = userStatements[userStatements.Count - 1];
                    if (lastStmt is AssignmentStatement assign && assign.variable is VariableRefNode varRef)
                    {
                        var varName = varRef.variableName;
                        if (mergedVariableTable.TryGetValue(varName, out var compiledVar))
                        {
                            var scopeIdx = compiledVar.isGlobal ? 0 : vmScopeIdxForRepl;
                            var rawValue = _vm.scopeStack.buffer[scopeIdx].dataRegisters[compiledVar.registerAddress];
                            var tc = compiledVar.typeCode;
                            VmUtil.TryGetVariableTypeDisplay(tc, out var typeName);

                            result = new DebugEvalResult
                            {
                                value = VmUtil.ConvertRawToDisplayString(tc, rawValue, _vm.heap),
                                type = typeName,
                                id = 0
                            };
                        }
                    }
                }
            }
            finally
            {
                // Restore VM execution state.
                // dataRegisters / typeRegisters are NOT restored — variable writes persist.
                // globalScope arrays are NOT restored — global writes persist.
                _vm.instructionIndex = originalInstructionIndex;
                _vm.program          = originalProgram;
                _vm.state            = originalState;
                _vm.scopeStack       = originalScopeStack;
                _vm.scopeStack.ptr   = originalScopePtr;
                _vm.stack            = originalStack;
                _vm.scope            = originalScope;
                _vm.error            = originalError;
                _vm.methodStack      = originalMethodStack;
            }

            return result;
        }

        public List<DebugStackFrame> GetFrames2()
        {
            var frames = new List<DebugStackFrame>();

            DebugToken current;
            
            // put the current location first, 
            if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex, out current))
            {
                logger.Error($"Failed to find current location at ins=[{_vm.instructionIndex}]");
                return frames;
            }

            // move up the method chain....
            {
                for (var i = _vm.methodStack.Count - 1; i >= 0; i--)
                {
                    var method = _vm.methodStack.buffer[i];

                    // find the name of the method we are current inside of...
                    if (!_dbg.insToFunction.TryGetValue(method.toIns, out var methodToken))
                    {
                        logger.Error($"There is no method token found for ins=[{method.toIns}]");
                        continue;
                    }
                    
                    // add a frame to represent this location...
                    frames.Add(new DebugStackFrame
                    {
                        name = methodToken.token.raw,
                        lineNumber = current.token.lineNumber,
                        colNumber = current.token.charNumber
                    });
                    
                    // find where this method was invoked from...
                    if (!instructionMap.TryFindClosestTokenBeforeIndex(method.fromIns - 1, out current))
                    {
                        logger.Error($"There is no method source site found for ins=[{method.fromIns - 1}]");
                    }
                }
            }
            
            frames.Add(new DebugStackFrame
            {
                name = "(top scope)",
                lineNumber = current.token.lineNumber,
                colNumber = current.token.charNumber
            });
            
            return frames;
        }

        /// <summary>
        /// Executes one VM instruction while stepping, converting any unhandled exception into a
        /// runtime-error pause so the debug session is never terminated by a VM crash.
        /// <para>If the instruction throws, <paramref name="activeStepMessage"/> is cleared so the
        /// step is implicitly "done" — the user will see the error stop event instead.</para>
        /// </summary>
        protected virtual void StepExecute(ref DebugMessage activeStepMessage)
        {
            try
            {
                _vm.Execute2(1);
                if (_vm.error.type != VirtualRuntimeErrorType.NONE)
                {
                    logger.Error($"Runtime error during step: {_vm.error.message}");
                    pauseRequestedByMessageId = resumeRequestedByMessageId + 1;
                    SendRuntimeErrorMessage(_vm.error.message);
                    activeStepMessage = null;
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Unhandled VM exception during step: {ex.Message}");
                pauseRequestedByMessageId = resumeRequestedByMessageId + 1;
                SendRuntimeErrorMessage(ex.Message);
                activeStepMessage = null;
            }
        }

        public virtual void DebugForever()
        {
            while (_vm.instructionIndex < _vm.program.Length)
            {
                StartDebugging();
            }
        }

        public virtual void StartDebugging(int ops = 0)
        {
            var handleManualSuspension = false;
            var budget = ops;
            // only wait for a post-restart hello if a debugger client actually connected — otherwise
            // a Restart() with no client attached would deadlock waiting for a PROTO_HELLO that never arrives.
            while ((_options.debugWaitForConnection && hasConnectedDebugger == 0)
                   || (hasConnectedDebugger != 0 && debuggerReset > 0 && debuggerSaidHello == 0))
            {
                if (ops > 0 && budget-- == 0) break; 
                ReadMessage();
                Thread.Sleep(1);
            }

            while (_vm.instructionIndex < _vm.program.Length)
            {
                if (ops > 0 && budget-- <= 0)
                {
                    // break up the execution of the debug session so that it can be interwoven with client process.
                    break;
                }

                if (requestedExit)
                {
                    break;
                }

                if (handleManualSuspension)
                {
                    break;
                }
                
                ReadMessage();

                // If the debug client disconnected while we were paused (breakpoint, step,
                // or explicit pause), auto-resume so the program isn't stuck forever waiting
                // for a continuation that will never arrive. Keep hitBreakpointToken set so
                // the breakpoint check at the current token doesn't immediately re-fire — it
                // clears naturally once the VM moves off this token. Breakpoints themselves
                // stay registered so a later reconnecting client can stop on them again.
                if (!IsClientConnected && (IsPaused || stepNextMessage != null || stepIntoMessage != null || stepOutMessage != null))
                {
                    resumeRequestedByMessageId = pauseRequestedByMessageId;
                    stepNextMessage = null;
                    stepIntoMessage = null;
                    stepOutMessage = null;
                }

                var hasCurrentToken =
                    instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex, out var currentToken);
                
                // logger.Log($"ins={_vm.instructionIndex} cts=[{hasCurrentToken}] ct=[{currentToken.token.Location}]");
                if (hasCurrentToken)
                {
                    // logger.Log($"_t= {currentToken.Jsonify()}");

                    { // check if we have moved past the current token.
                        if (hitBreakpointToken != null)
                        {
                            if (currentToken != hitBreakpointToken)
                            {
                                hitBreakpointToken = null;
                            }
                        }
                    }

                    if (!IsPaused && breakpointTokens.Contains(currentToken) && hitBreakpointToken == null)
                    {
                        logger.Log($"HIT BREAKPOINT {currentToken.Jsonify()}");
                        pauseRequestedByMessageId = 1;
                        resumeRequestedByMessageId = 0;
                        
                        hitBreakpointToken = currentToken;
                        // NEED TO ACTUALLY PAUSE AND WAIT FOR CONTINUATION...
                        SendStopMessage();
                        continue;
                    }
                
                    
                }
                
                if (!IsPaused)
                {
                    // handle the case where we exploded
                    if (_vm.error.type != VirtualRuntimeErrorType.NONE)
                    {
                        logger.Error("Due to runtime exception, breaking out of exection");
                        break;
                    }
                    
                    // execute to the next breakpoint.
                    var movedOff = false;
                    int spent = 0;
                    try
                    {
                        //if (!_vm.isSuspendRequested)
                        {
                            var wasSus = _vm.isSuspendRequested; // probably false... 
                            
                            var executionBudget = ops > 0
                                ? (budget > 0
                                    ? budget
                                    : 1) // min budget needs to be 1, or it will fail.
                                : 0; // infinite budget
                            
                            // calling this in a loop unsuspends the VM,
                            //  which may not give callers a chance to handle info. 
                            var hitBreakpoint = false;
                            spent = _vm.Execute3(executionBudget, ins =>
                            {
                                // if there are messages, we need to stop and read them, I GUESS!?
                                if (receivedMessages.Count > 0)
                                {
                                    return true;
                                }

                                if (instructionMap.TryFindClosestTokenBeforeIndex(ins, out var t))
                                {
                                    // Mark movedOff BEFORE checking shouldPause so that the very
                                    // first instruction of a new breakpoint token is caught.
                                    // (The old order checked shouldPause first, causing
                                    // single-instruction breakpoint tokens to be silently skipped.)
                                    if (t != currentToken)
                                    {
                                        movedOff = true;
                                        hitBreakpointToken = null;
                                    }

                                    if (movedOff && breakpointTokens.Contains(t))
                                    {
                                        if (_vm.isSuspendRequested && !hitBreakpoint)
                                        {
                                            // if we haven't hit a breakpoint yet, but somehow a sus was requested, we need to yield.
                                            handleManualSuspension = true;
                                            
                                        }
                                        hitBreakpoint = true;
                                        return true;
                                    }
                                }

                                return false;
                            });

                            if (_vm.isSuspendRequested && !hitBreakpoint)
                            {
                                // the vm itself requested a suspend operation. 
                                //  we should yield out of the function and let the caller
                                //  re-call us when they are ready. 
                                handleManualSuspension = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"Unhandled VM exception during execution: {ex.Message}");
                        pauseRequestedByMessageId = resumeRequestedByMessageId + 1;
                        SendRuntimeErrorMessage(ex.Message);
                        spent = 0;
                    }
                    budget -= spent;
                    if (budget < 0) budget = 0;
                    if (_vm.error.type != VirtualRuntimeErrorType.NONE)
                    {
                        logger.Error($"Hit a runtime exception! message=[{_vm.error.message}]");
                        pauseRequestedByMessageId = resumeRequestedByMessageId + 1; // hack to pause the program.
                        SendRuntimeErrorMessage(_vm.error.message);
                    }
                }
                else
                {
                   
                    // handle the step-next case
                    if (stepNextMessage != null)
                    {
                        if (!hasCurrentToken)
                        {
                            Ack(stepNextMessage, new StepNextResponseMessage
                            {
                                reason = $"no source location available while stepping. ins=[{_vm.instructionIndex}]",
                                status = -1
                            });
                            stepNextMessage = null;
                        }
                        else
                        {
                            var isNewToken = currentToken.insIndex != stepOverFromToken.insIndex;
                            var isSameOrLessDepth = _vm.methodStack.Count <= stepStackDepth;
                            var isReal = currentToken.isComputed == 0;

                            logger.Log($"[VRB] looking for into-over real=[{isReal}]  is-new=[{isNewToken}] ins=[{_vm.instructionIndex}] depth=[{_vm.methodStack.Count}] start-depth=[{stepStackDepth}] token=[{currentToken.Jsonify()}] ");

                            if (isNewToken && isSameOrLessDepth && isReal)
                            {
                                // we have arrived at the stop location.
                                Ack(stepNextMessage, new StepNextResponseMessage
                                {
                                    reason = "hit next",
                                    status = 1
                                });
                                stepNextMessage = null;
                            }
                            else
                            {
                                StepExecute(ref stepNextMessage);
                            }
                        }
                    }
                    // handle step-in case
                    else if (stepIntoMessage != null)
                    {
                        if (!hasCurrentToken)
                        {
                            logger.Log("[ERR] Failed to find step-into, so cancelling!");
                            Ack(stepIntoMessage, new StepNextResponseMessage
                            {
                                reason = $"no source location available while stepping in. ins=[{_vm.instructionIndex}]",
                                status = -1
                            });
                            stepIntoMessage = null;
                        }
                        else
                        {
                            var isNewToken = currentToken.insIndex != stepInFromToken.insIndex;
                            var isReal = currentToken.isComputed == 0;
                            // var isSameOrMoreDepth = _vm.scopeStack.Count >= stepStackDepth ;

                            if (isNewToken && isReal)
                            {
                                // we have arrived at the stop location.
                                Ack(stepIntoMessage, new StepNextResponseMessage
                                {
                                    reason = "hit in",
                                    status = 1
                                });
                                stepIntoMessage = null;
                            }
                            else
                            {
                                StepExecute(ref stepIntoMessage);
                            }
                        }
                    }
                    // handle step-out case
                    else if (stepOutMessage != null)
                    {
                        if (!hasCurrentToken)
                        {
                            Ack(stepOutMessage, new StepNextResponseMessage
                            {
                                reason = $"no source location available while stepping out. ins=[{_vm.instructionIndex}]",
                                status = -1
                            });
                            stepOutMessage = null;
                        }
                        else
                        {
                            var isNewToken = currentToken.insIndex != stepOutFromToken.insIndex;
                            var isLessThanOrZero = _vm.methodStack.Count < stepStackDepth || _vm.methodStack.Count == 0;
                            var isReal = currentToken.isComputed == 0;

                            logger.Log($"[VRB] looking for out-step is-new=[{isNewToken}] ins=[{_vm.instructionIndex}] depth=[{_vm.methodStack.Count}] start-depth=[{stepStackDepth}] token=[{currentToken.Jsonify()}] ");

                            if (isNewToken && isLessThanOrZero && isReal)
                            {
                                // we have arrived at the stop location.
                                Ack(stepOutMessage, new StepNextResponseMessage
                                {
                                    reason = "hit out",
                                    status = 1
                                });
                                stepOutMessage = null;
                            }
                            else
                            {
                                StepExecute(ref stepOutMessage);
                            }
                        }
                    }
                }
            }


            logger?.Debug("done with debug loop");
            if (_vm.instructionIndex >= _vm.program.Length || requestedExit)
            {
                SendExitedMessage();
            }
        }
        
    }


    public enum DebugMessageType
    {
        NOOP,
        PROTO_HELLO,
        PROTO_ACK,
        
        REV_REQUEST_BREAKPOINT,
        REV_REQUEST_EXITED,
        REV_REQUEST_RESTART,
        REV_REQUEST_EXPLODE,
        
        REQUEST_PAUSE,
        REQUEST_PLAY,
        REQUEST_STEP_OVER,
        REQUEST_STEP_IN,
        REQUEST_STEP_OUT,
        REQUEST_TERMINATE,
        
        REQUEST_STACK_FRAMES,
        REQUEST_SCOPES,
        REQUEST_EVAL,
        REQUEST_REPL,
        REQUEST_SET_VAR,
        REQUEST_VARIABLE_EXPANSION,
        REQUEST_BREAKPOINTS
    }

    public interface IHasRawBytes
    {
        
        /// <summary>
        /// this contains the actual serialized bytes of the message itself.
        /// It may contain more information than exist purely in the <see cref="DebugMessage"/>
        /// type.
        ///
        /// It is not meant to be serialized itself, as that would create a logical recursion. 
        /// </summary>
        public string RawJson { get; set; }
    }
    
    public class DebugMessage : IJsonable, IHasRawBytes
    {
        public int id;
        public DebugMessageType type;
        
        
        public virtual void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(id), ref id);
            op.IncludeField(nameof(type), ref type);
        }

        public string RawJson { get; set; }
    }

    public class ExplodedMessage : DebugMessage
    {
        public string message;
        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(message), ref message);
        }
    }

    public class EvalMessage : DebugMessage
    {
        public int frameIndex;
        public string expression;

        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(frameIndex), ref frameIndex);
            op.IncludeField(nameof(expression), ref expression);
        }
    }

    public class SetVariableMessage : DebugMessage
    {
        public int variableId;
        public int frameId;
        public string rhs;
        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(variableId), ref variableId);
            op.IncludeField(nameof(frameId), ref frameId);
            op.IncludeField(nameof(rhs), ref rhs);
        }
    }

    public class EvalResponse : DebugMessage
    {
        public DebugEvalResult result;
        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(result), ref result);
        }
    }

    public class StepNextResponseMessage : DebugMessage
    {
        public string reason;
        public int status;

        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(reason), ref reason);
            op.IncludeField(nameof(status), ref status);
        }
    }

    public class ScopesMessage : DebugMessage
    {
        public List<DebugScope> scopes = new List<DebugScope>();

        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(scopes), ref scopes);
        }
    }

    public class DebugScope : IJsonable
    {
        public int id;
        public string scopeName;
        public string evalName;
        public List<DebugVariable> variables = new List<DebugVariable>();
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(id), ref id);
            op.IncludeField(nameof(scopeName), ref scopeName);
            op.IncludeField(nameof(evalName), ref evalName);
            op.IncludeField(nameof(variables), ref variables);
        }
    }
    
    public class DebugVariable : IJsonable
    {
        public int id;
        public string name;
        public string type;
        public string value;
        public string evalName;

        public int fieldCount;
        public int elementCount;

        // json ignored on purpose. 
        public DebugRuntimeVariable runtimeVariable;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(id), ref id);
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(type), ref type);
            op.IncludeField(nameof(value), ref value);
            op.IncludeField(nameof(evalName), ref evalName);
            op.IncludeField(nameof(fieldCount), ref fieldCount);
            op.IncludeField(nameof(elementCount), ref elementCount);
        }
    }
    
    public class StackFrameMessage : DebugMessage
    {
        public List<DebugStackFrame> frames;

        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(frames), ref frames);
        }
    }

    public class HelloResponseMessage : DebugMessage
    {
        public int processId;
        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(processId), ref processId);
        }
    }

    public class MockResetMessage : DebugMessage
    {
        public VirtualMachine nextMachine;
        public CommandCollection nextCommands;
        public DebugData nextDebugData;
    }

    
    [StructLayout(LayoutKind.Sequential)]
    public struct DebugControlMessage
    {
        public byte type;
        public long id;
        public ulong arg;
    }
    
    public class DebugServerStreamUtil
    {
        // public const int MAX_MESSAGE_LENGTH = 1024 * 8; // 8kb.
        public const int MAX_MESSAGE_LENGTH = 1024 * 32; // 32kb. 32_768

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/> starting at
        /// <paramref name="offset"/>. TCP's <see cref="Socket.Receive(byte[],int,int,SocketFlags,out SocketError)"/>
        /// is allowed to return fewer bytes than requested (especially with a short ReceiveTimeout
        /// or under load with large messages). The previous receive code assumed a single Receive
        /// call satisfied the request, which corrupted the message stream when the kernel split
        /// reads — that mis-framed every subsequent message and the IJsonable parser eventually
        /// crashed on garbage with "json error. Expected [\"] but found [...]".
        ///
        /// Returns false if the connection dropped (count==0) or <paramref name="cancellationToken"/>
        /// fired before all bytes were read. Timeouts (SocketError.TimedOut) are NOT treated as
        /// errors here — we just retry until the full count is in.
        /// </summary>
        private static bool ReceiveExactly(
            Socket socket, byte[] buffer, int offset, int count,
            CancellationToken cancellationToken,
            out SocketError lastError)
        {
            lastError = SocketError.Success;
            var read = 0;
            while (read < count)
            {
                if (cancellationToken.IsCancellationRequested) return false;
                int got;
                try
                {
                    got = socket.Receive(buffer, offset + read, count - read, SocketFlags.None, out lastError);
                }
                catch (SocketException ex)
                {
                    lastError = ex.SocketErrorCode;
                    if (lastError == SocketError.TimedOut) continue;
                    return false;
                }
                if (lastError == SocketError.TimedOut) continue;
                if (lastError != SocketError.Success) return false;
                if (got == 0) return false; // peer closed
                read += got;
            }
            return true;
        }

        /// <summary>
        /// Writes exactly <paramref name="count"/> bytes from <paramref name="buffer"/>. TCP
        /// <see cref="Socket.Send(byte[],int,int,SocketFlags,out SocketError)"/> may return fewer
        /// bytes than requested under backpressure, and the previous code advanced the offset by
        /// a fixed amount regardless of the actual sent count — that silently dropped bytes.
        /// </summary>
        private static bool SendExactly(Socket socket, byte[] buffer, int offset, int count, out SocketError lastError)
        {
            lastError = SocketError.Success;
            var sent = 0;
            while (sent < count)
            {
                int got;
                try
                {
                    got = socket.Send(buffer, offset + sent, count - sent, SocketFlags.None, out lastError);
                }
                catch (SocketException ex)
                {
                    lastError = ex.SocketErrorCode;
                    return false;
                }
                if (lastError != SocketError.Success && lastError != SocketError.WouldBlock) return false;
                if (got == 0) return false; // socket closed
                sent += got;
            }
            return true;
        }

        public static void Send<T>(Socket socket, T message)
            where T : IJsonable
        {
            var sendBytes = EncodeJsonable(message);
            var sendLength = sendBytes.Length;

            var lengthBytes = BitConverter.GetBytes(sendLength);
            SendExactly(socket, lengthBytes, 0, lengthBytes.Length, out _);

            // Send the body in one shot — SendExactly loops on partial writes internally, so
            // we don't need the caller-side chunking the previous version did. The chunked
            // version also miscomputed offsets, occasionally skipping bytes for messages whose
            // length was exactly a multiple of MAX_MESSAGE_LENGTH.
            if (sendLength > 0) SendExactly(socket, sendBytes, 0, sendLength, out _);
        }
        
        
        public static void ConnectToServer2<T>(
            int port,
            ConcurrentQueue<T> outputQueue,
            ConcurrentQueue<T> inputQueue,
            CancellationToken cancellationToken,
            Action onConnectionDropped = null)
            where T : IJsonable, IHasRawBytes, new()
        {
            var ip = new IPEndPoint(IPAddress.Loopback, port);

            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    socket.Connect(ip);
                    break;
                }
                catch
                {
                    // ignore
                    Thread.Sleep(1);
                }
            }
            

            socket.ReceiveTimeout = 1;
            socket.ReceiveBufferSize = MAX_MESSAGE_LENGTH;
            var buffer = new byte[MAX_MESSAGE_LENGTH];
            var connectionDropped = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Thread.Sleep(1);

                    // try to send out all pending messages
                    try
                    {
                        while (outputQueue.TryDequeue(out var msgToSend))
                        {
                            Send(socket, msgToSend);
                        }
                    }
                    catch (SocketException)
                    {
                        connectionDropped = true;
                        break;
                    }

                    // Don't enter the read loop unless there's actually some data — otherwise
                    // ReceiveExactly will spin retrying on timeout and never let the outer loop
                    // drain its outbound queue. This preserves the original "poll for incoming,
                    // service outgoing" multiplexing without the partial-read framing bug.
                    int avail;
                    try { avail = socket.Available; }
                    catch (SocketException) { connectionDropped = true; break; }
                    if (avail <= 0) continue;

                    int messageLength;
                    SocketError err;

                    { // receive the length of the message — must be exactly 4 bytes
                        if (!ReceiveExactly(socket, buffer, 0, sizeof(int), cancellationToken, out err))
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            connectionDropped = true; // peer closed or hard error mid-read
                            break;
                        }

                        messageLength = BitConverter.ToInt32(buffer, 0);
                    }

                    if (messageLength <= 0)
                    {
                        // Garbage length means the stream is unrecoverable — bail rather than
                        // allocate ridiculous buffers or read for hours.
                        connectionDropped = true;
                        break;
                    }

                    { // receive the content of the message — must be exactly `messageLength` bytes
                        var giantBuffer = new byte[messageLength];
                        if (!ReceiveExactly(socket, giantBuffer, 0, messageLength, cancellationToken, out err))
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            connectionDropped = true;
                            break;
                        }

                        var controlMessage = DecodeJsonable<T>(giantBuffer, messageLength);
                        inputQueue.Enqueue(controlMessage);
                    }


                }
            }
            finally
            {
                try { socket.Disconnect(true); } catch { }
                socket.Close();
                if (connectionDropped)
                    onConnectionDropped?.Invoke();
            }
        }
        
        public static byte[] EncodeJsonable(IJsonable jsonable)
        {
            var json = jsonable.Jsonify();
            var bytes = Encoding.UTF8.GetBytes(json);
            return bytes;
        }

        public static T DecodeJsonable<T>(byte[] bytes, int messageLength) 
            where T : IJsonable, IHasRawBytes, new()
        {
            var json = Encoding.UTF8.GetString(bytes, 0, messageLength);
            var inst = JsonableExtensions.FromJson<T>(json);
            inst.RawJson = json;
            return inst;
        }
        
        public static void OpenServer2<T>(
            int port, 
            ConcurrentQueue<T> outputQueue,
            ref bool didClientConnect,
            ConcurrentQueue<T> inputQueue,
            CancellationToken cancellationToken)
            where T : IJsonable, IHasRawBytes, new()
        {
            // server only runs on local machine, cannot do cross machine debugging yet
            var ip = new IPEndPoint(IPAddress.Any, port);
            didClientConnect = false;
            // host a socket...
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.ReceiveTimeout = 1; // if there isn't data available, just bail!
            socket.ReceiveBufferSize = 2048; // messages must be less than 2k
            socket.Bind(ip);
            socket.Listen(100);
            // var buffer = new ArraySegment<byte>(new byte[socket.ReceiveBufferSize]);
            var buffer = new byte[MAX_MESSAGE_LENGTH];
            
            // Outer accept loop — handle reconnects from successive adapter sessions. The
            // previous version did a single Accept() and looped on that one handler forever;
            // when the adapter died and the user reconnected, the new connection got queued by
            // socket.Listen but was never accepted, so adapter→runtime requests piled up
            // unanswered and Rider sat at the breakpoint waiting for setBreakpoints responses
            // that never came.
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Socket handler;
                    try
                    {
                        // Wait for an incoming connection with periodic cancellation checks
                        // (Accept itself isn't cancellable; Poll lets us peek without blocking).
                        if (!socket.Poll(100_000, SelectMode.SelectRead))
                        {
                            continue; // 100ms with no pending connection — re-check cancel
                        }
                        handler = socket.Accept();
                        handler.ReceiveTimeout = 100;
                        handler.ReceiveBufferSize = MAX_MESSAGE_LENGTH;
                    }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }

                    didClientConnect = true;

                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            Thread.Sleep(1);

                            // try to send out all pending messages
                            try
                            {
                                while (outputQueue.TryDequeue(out var msgToSend))
                                {
                                    Send(handler, msgToSend);
                                }
                            }
                            catch (SocketException) { break; } // adapter died mid-send

                            // Detect graceful peer close: Poll says readable but no bytes are
                            // actually available — that's TCP's "FIN received" pattern. Without
                            // this, we'd loop forever on the dead handler and never re-accept.
                            bool peerClosed;
                            try
                            {
                                peerClosed = handler.Poll(0, SelectMode.SelectRead)
                                             && handler.Available == 0;
                            }
                            catch (SocketException) { break; }
                            if (peerClosed) break;

                            int handlerAvail;
                            try { handlerAvail = handler.Available; }
                            catch (SocketException) { break; }
                            if (handlerAvail <= 0) continue;

                            // read the length — must be exactly 4 bytes (TCP recv may return fewer)
                            if (!ReceiveExactly(handler, buffer, 0, sizeof(int), cancellationToken, out var err))
                            {
                                if (cancellationToken.IsCancellationRequested) break;
                                break; // mid-read disconnect — close handler, re-accept
                            }

                            var length = BitConverter.ToInt32(buffer, 0);
                            if (length <= 0) continue; // garbage length, skip

                            // The receive buffer is only MAX_MESSAGE_LENGTH bytes wide. If the
                            // sender chunked a larger payload, we need a buffer that fits it.
                            var bodyBuffer = length <= buffer.Length ? buffer : new byte[length];

                            if (!ReceiveExactly(handler, bodyBuffer, 0, length, cancellationToken, out err))
                            {
                                if (cancellationToken.IsCancellationRequested) break;
                                break; // mid-message disconnect — close handler, re-accept
                            }

                            var controlMessage = DecodeJsonable<T>(bodyBuffer, length);
                            inputQueue.Enqueue(controlMessage);
                        }
                    }
                    finally
                    {
                        try { handler.Close(); } catch { }
                        didClientConnect = false;
                    }
                    // Loop back to Accept the next adapter connection.
                }
            }
            finally
            {
                try { socket.Close(); } catch { }
            }

        }
        
    }

    public delegate void DebugConnectionFunction(int port, ConcurrentQueue<DebugControlMessage> outputQueue,
        ConcurrentQueue<DebugControlMessage> inputQueue, CancellationToken cancellationToken);
}