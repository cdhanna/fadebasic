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
        private DebugData _dbg;
        private readonly CommandCollection _commandCollection;
        private readonly string _label;
        public readonly LaunchOptions _options;

        private bool requestedExit;
        private bool started;
        private Task _serverTask;
        private Task _processingTask;

        private ConcurrentQueue<DebugMessage> 
            outboundMessages = new ConcurrentQueue<DebugMessage>(),
            receivedMessages = new ConcurrentQueue<DebugMessage>();

        public CancellationTokenSource _cts;
        private Exception _serverTaskEx;

        private bool hasReceivedOpen = false;

        private int hasConnectedDebugger;
        private int pauseRequestedByMessageId;
        private int resumeRequestedByMessageId;
        
        private int messageIdCounter;


        private int currentInsLookupOffset = 0;
        private DebugMessage stepNextMessage;
        private DebugMessage stepIntoMessage;
        private DebugMessage stepOutMessage;

        private DebugToken stepOverFromToken;
        private DebugToken stepInFromToken;
        private DebugToken stepOutFromToken;
        private int stepStackDepth;

        public HashSet<DebugToken> breakpointTokens = new HashSet<DebugToken>();
        public DebugToken hitBreakpointToken;
        public IndexCollection instructionMap;
        
        public IDebugLogger logger;

        public DebugVariableDatabase variableDb;
        
        public int InstructionPointer => _vm.instructionIndex;

        public bool IsPaused => pauseRequestedByMessageId > resumeRequestedByMessageId;

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

            logger.Log("Starting debug session...");

            foreach (var token in _dbg.statementTokens)
            {
                var json = JsonableExtensions.Jsonify(token);
                logger.Log(json);
            }
            
            
            // _tree = IntervalTree.From(dbg.points);
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

        private bool didClientConnect = false;
        void RunServer(object state)
        {
            DebugServerStreamUtil.OpenServer2(_options.debugPort, outboundMessages, ref didClientConnect, receivedMessages, _cts.Token);
        }


        void RunDiscoverability(object state)
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
        

        void Ack(int originalId, DebugMessage responseMsg)
        {
            responseMsg.id = originalId;
            responseMsg.type = DebugMessageType.PROTO_ACK;
            outboundMessages.Enqueue(responseMsg);
        }
        
        void Ack(DebugMessage originalMessage)
        {
            outboundMessages.Enqueue(new DebugMessage
            {
                id = originalMessage.id,
                type = DebugMessageType.PROTO_ACK
            });
        }

        void Ack<T>(DebugMessage originalMessage, T responseMsg)
            where T : DebugMessage
        {
            responseMsg.id = originalMessage.id;
            responseMsg.type = DebugMessageType.PROTO_ACK;
            outboundMessages.Enqueue(responseMsg);
        }

        public int GetNextMessageId() => Interlocked.Decrement(ref messageIdCounter);

        void SendStopMessage()
        {
            var message = new DebugMessage()
            {
                id = GetNextMessageId(),
                type = DebugMessageType.REV_REQUEST_BREAKPOINT
            };
            outboundMessages.Enqueue(message);
        }

        void SendRuntimeErrorMessage(string message)
        {
            outboundMessages.Enqueue(new ExplodedMessage()
            {
                id = GetNextMessageId(),
                message = message,
                type = DebugMessageType.REV_REQUEST_EXPLODE
            });
        }

        void SendExitedMessage()
        {
            var message = new DebugMessage()
            {
                id = GetNextMessageId(),
                type = DebugMessageType.REV_REQUEST_EXITED
            };
            outboundMessages.Enqueue(message);
        }
        
        void ReadMessage()
        {
            if (receivedMessages.TryDequeue(out var message))
            {
                logger.Log($"[DBG] Received message : {message.id}, {message.type.ToString()}");
      
                switch (message.type)
                {
                    case DebugMessageType.PROTO_HELLO:
                        hasConnectedDebugger = 1;
                        Ack(message, new HelloResponseMessage()
                        {
                            processId = Process.GetCurrentProcess().Id
                        });
                        break;
                    case DebugMessageType.REQUEST_PAUSE:
                        pauseRequestedByMessageId = message.id;
                        Ack(message);
                        
                        if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex, out var pausedAtToken))
                        {
                            logger.Log($"[DBG] could not find pause token");
                        }
                        else
                        {
                            logger.Log($"[DBG] paused at ins=[{_vm.instructionIndex}]  token {pausedAtToken.Jsonify()}");
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
                        
                        logger.Log($"Handling breakpoint resolution... breakpoint-count=[{detail.breakpoints.Count}]");
                        breakpointTokens.Clear();

                        var verifiedBreakpoints = new List<Breakpoint>();
                        for (var i = 0; i < detail.breakpoints.Count; i++)
                        {
                            var requestedBreakPoint = detail.breakpoints[i];
                            var verified = instructionMap.TryFindClosestTokenAtLocation(requestedBreakPoint.lineNumber,
                                requestedBreakPoint.colNumber, out var token);

                            var bp = new Breakpoint
                            {
                                status = verified ? 1 : -1,
                                lineNumber = verified ? token.token.lineNumber : requestedBreakPoint.lineNumber,
                                colNumber = verified ? token.token.charNumber : requestedBreakPoint.colNumber
                            };
                            logger.Log($" breakpoint index=[{i}] is verified=[{verified}] line=[{bp.lineNumber}] cn=[{bp.colNumber}]");
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

                        { // reset the info to blank
                            stepInFromToken = null;
                            stepStackDepth = 0;
                            variableDb.ClearLifetime();

                        }
                        
                        if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex, out stepInFromToken))
                        {
                            logger.Log($"[DBG] could not find into starting token");
                            Ack(message, new StepNextResponseMessage
                            {
                                reason = "no source location available for starting location",
                                status = -1
                            });
                        }

                        stepIntoMessage = message; // need to to ACK later...
                        stepStackDepth = _vm.methodStack.Count;

                        logger.Log($"[DBG] stepping in ins=[{_vm.program[_vm.instructionIndex]}] depth=[{stepStackDepth}] from {stepInFromToken.Jsonify()}");
                        break;
                    case DebugMessageType.REQUEST_STEP_OUT:

                        { // reset the info to blank
                            stepOutFromToken = null;
                            stepStackDepth = 0;
                            variableDb.ClearLifetime();

                        }
                        
                        if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex, out stepOutFromToken))
                        {
                            logger.Log($"[DBG] could not find out starting token");
                            Ack(message, new StepNextResponseMessage
                            {
                                reason = "no source location available for starting location",
                                status = -1
                            });
                        }

                        stepOutMessage = message; // need to to ACK later...
                        stepStackDepth = _vm.methodStack.Count;

                        logger.Log($"[DBG] stepping out from {stepOutFromToken.Jsonify()}");
                        break;
                    case DebugMessageType.REQUEST_STEP_OVER:

                        // stepping NEXT means "step over"
                        //  that means go to the next statement that has the same stack depth, OR less than currently. 

                        { // reset the info to blank
                            stepOverFromToken = null;
                            stepStackDepth = 0;
                            variableDb.ClearLifetime();

                        }
                        
                        if (!instructionMap.TryFindClosestTokenBeforeIndex(_vm.instructionIndex, out stepOverFromToken))
                        {
                            logger.Log($"[DBG] could not find next starting token");
                            Ack(message, new StepNextResponseMessage
                            {
                                reason = "no source location available for starting location",
                                status = -1
                            });
                        }

                        stepNextMessage = message; // need to to ACK later...
                        stepStackDepth = _vm.methodStack.Count;

                        logger.Debug($"stepping from {stepOverFromToken.Jsonify()}");
                        break;
                    
                    case DebugMessageType.REQUEST_VARIABLE_EXPANSION:
                        var variableRequest = JsonableExtensions.FromJson<DebugVariableExpansionRequest>(message.RawJson);
                        var subScope = variableDb.Expand(variableRequest.variableId);
                        Ack(message, new ScopesMessage
                        {
                            scopes = new List<DebugScope>
                            {
                                subScope
                            }
                        });
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

            if (finalDeclare == null || finalDeclare.variable != SYNTHETIC_NAME2)
            {
                finalDeclare = null;
                // TypeInfo.FromVariableType()

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
            
            if (finalStatement == null)
            {
                return DebugEvalResult.Failed("only declarations are allowed");
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

            if (parseErrors.Count > 0)
            {
                if (parseErrors[0].errorCode.code == ErrorCodes.ImplicitArrayDeclaration.code)
                {
                    if (quickResult != null) return quickResult;
                }
                
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

            var originalError = _vm.error;
            _vm.error = default;
            while (_vm.instructionIndex < _vm.program.Length)
            {
                _vm.Execute2(_vm.program.Length - _vm.instructionIndex);
            }
            
            DebugEvalResult result = null;
            

            if (!compiler.scopeStack.Peek().TryGetVariable(SYNTHETIC_NAME2, out var synth))
            {
                throw new NotSupportedException("no compiled synthetic");
            }


            DebugRuntimeVariable runtimeSynth = null;
            if (synth.typeCode == TypeCodes.STRUCT)
            {
                var ptr = _vm.dataRegisters[synth.registerAddress];
                if (!_vm.heap.TryGetAllocation((int)ptr, out var alloc))
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

            _vm.instructionIndex = originalInstructionIndex;
            _vm.program = originalProgram;
            _vm.state = originalState;
            _vm.scopeStack = originalScopeStack;
            _vm.scopeStack.ptr = originalScopePtr;
            _vm.stack = originalStack;
            _vm.scope = originalScope;
            _vm.error = originalError;
            
            if (overwriteVariableId > -1)
            {
                if (!variableDb.TrySetValue(overwriteVariableId, runtimeSynth, out var setErr))
                {
                    return DebugEvalResult.Failed(setErr);
                }
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

        public void StartDebugging(int ops = 0)
        {
            var budget = ops;
            while (_options.debugWaitForConnection && hasConnectedDebugger == 0)
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
                
                ReadMessage();
                

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
                    _vm.Execute2(1);
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
                                _vm.Execute2(1);
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
                                _vm.Execute2(1);
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
                                _vm.Execute2(1);
                            }
                        }
                    }
                }
            }


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

        public static void Send<T>(Socket socket, T message)
            where T : IJsonable
        {
            
            var sendBytes = EncodeJsonable(message);

            var sendLength = sendBytes.Length;
            if (sendLength > MAX_MESSAGE_LENGTH)
                throw new InvalidOperationException("Cannot send message longer than max-length");

            var lengthBytes = BitConverter.GetBytes(sendLength);

            socket.Send(lengthBytes, 0, lengthBytes.Length, SocketFlags.None, out var sendError);
            if (sendError != SocketError.Success)
            {
                // TODO: uh oh?
            }
                        
            var sentByteCount = socket.Send(sendBytes, 0, sendBytes.Length, SocketFlags.None,
                out sendError);
            if (sendError != SocketError.Success)
            {
                // TODO: uh oh?
            }
        }
        
        
        public static void ConnectToServer2<T>(
            int port, 
            ConcurrentQueue<T> outputQueue, 
            ConcurrentQueue<T> inputQueue,
            CancellationToken cancellationToken)
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

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Thread.Sleep(1);

                    // try to send out all pending messages
                    while (outputQueue.TryDequeue(out var msgToSend))
                    {
                        Send(socket, msgToSend);
                        
                    }

                    int messageLength = 0;
                    int availableBytes = 0;
                    int count = 0;
                    SocketError err = default;

                    { // receive the length of the message
                        count = socket.Receive(buffer, 0, sizeof(int), SocketFlags.None, out err);
                        if (err != SocketError.Success) continue;

                        if (count == 0) continue;

                        var bufferSpan = new Span<byte>(buffer, 0, count);
                        messageLength = BitConverter.ToInt32(bufferSpan.ToArray(), 0);
                    }

                    { // receive the content of the message
                        count = socket.Receive(buffer, 0, messageLength, SocketFlags.None, out err);
                        if (err != SocketError.Success) continue;
                        if (count == 0) continue;
                        
                        var controlMessage = DecodeJsonable<T>(buffer, messageLength);
                        inputQueue.Enqueue(controlMessage);
                    }
                    
                  
                }
            }
            finally
            {
                socket.Disconnect(true);
                socket.Close();
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
            
            Socket handler = null;
            try
            {
                handler = socket.Accept();
                handler.ReceiveTimeout = 100;
                handler.ReceiveBufferSize = MAX_MESSAGE_LENGTH;

                didClientConnect = true;

            }
            catch (Exception ex)
            {
                throw;
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(1);

                    // try to send out all pending messages
                    while (outputQueue.TryDequeue(out var msgToSend))
                    {
                        Send(handler, msgToSend);
                    }

                    // read the length
                    for (var i = 0; i < sizeof(int); i++)
                    {
                        buffer[i] = 0;
                    }
                    
                    var count = handler.Receive(buffer, 0, sizeof(int), SocketFlags.None, out var err);
                    if (err != SocketError.Success) continue;
                    if (count == 0) continue;

                    var length = BitConverter.ToInt32(buffer, 0);
                    
                    if (length > buffer.Length)
                    {
                        throw new Exception(@$"buffer error has invalid size. port=[{port}] count=[{count}] b0=[{buffer[0]}] b1=[{buffer[1]}] b2=[{buffer[2]}] b3=[{buffer[3]}]");
                    }

                    // try to receive a single message
                    count = handler.Receive(buffer, 0, length, SocketFlags.None,
                        out err);
                    if (err != SocketError.Success) continue;
                    if (count == 0) continue;
                    // TODO: should check that the received byte count is big enough to read a full message.

                    var controlMessage = DecodeJsonable<T>(buffer, length);
                    inputQueue.Enqueue(controlMessage);

                }
            }
            finally
            {
                handler.Close();
                socket.Close();
            }

        }
        
    }

    public delegate void DebugConnectionFunction(int port, ConcurrentQueue<DebugControlMessage> outputQueue,
        ConcurrentQueue<DebugControlMessage> inputQueue, CancellationToken cancellationToken);
}