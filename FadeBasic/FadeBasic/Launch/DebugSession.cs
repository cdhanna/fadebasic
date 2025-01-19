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

        public DebugSession(VirtualMachine vm, DebugData dbg, CommandCollection commandCollection=null, LaunchOptions options=null)
        {
            _options = options ?? LaunchOptions.DefaultOptions;
            _dbg = dbg;
            _commandCollection = commandCollection;
            _vm = vm;
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
        }

        public void ShutdownServer()
        {
            logger.Log("Starting server shutdown...");
            while (outboundMessages.Count > 0)
            {
                Thread.Sleep(10); // wait for messages to go away...
            }
            logger.Log("Messages done...");
            _cts.Cancel();
        }

        void RunServer(object state)
        {
            DebugServerStreamUtil.OpenServer2(_options.debugPort, outboundMessages, receivedMessages, _cts.Token);
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
                        var evalResult = Eval(evalRequest.frameIndex, evalRequest.expression);
                        Ack(evalRequest, new EvalResponse
                        {
                            result = evalResult
                        });
                        break;
                    case DebugMessageType.REQUEST_SCOPES:
                        var scopeRequest = JsonableExtensions.FromJson<DebugScopeRequest>(message.RawJson);
                        var scopeFrames = GetFrames2();
                        var frameIndex = scopeRequest.frameIndex;
                        logger.Debug($"got stack frame request with frame-id=[{frameIndex}] frame-count=[{scopeFrames.Count}] scope-stack=[{_vm.scopeStack.Count}] dbg-count=[{_dbg.insToVariable.Count}]");
                        if (frameIndex < 0 || frameIndex >= scopeFrames.Count)
                        {
                            logger.Error($"scope request failed because frame-index=[{frameIndex}] was out of bounds of max=[{scopeFrames.Count}]");
                        }

                        var global = variableDb.GetGlobalVariablesForFrame(frameIndex);
                        var local = variableDb.GetLocalVariablesForFrame(frameIndex);

                        Ack(message, new ScopesMessage
                        {
                            scopes = new List<DebugScope>
                            {
                                global,
                                local
                            }
                        });
                        
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

        public DebugEvalResult Eval(int frameId, string expression)
        {
            // TODO: ignore the frame-id, and just assume everything is at frame-0...

            const string SYNTHETIC_NAME = "fade________eval";
            expression = SYNTHETIC_NAME + "=" + expression;
            var globalVariableTable = new Dictionary<string, CompiledVariable>();
            var localVariableTable = new Dictionary<string, CompiledVariable>();

            logger.Log($"Evaluating frame=[{frameId}], expr=[{expression}]");
            
            var lexer = new Lexer();
            var lexResults = lexer.TokenizeWithErrors(expression, _commandCollection);
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
            
            // TODO: inject all known decls that are valid for this scope.
            var stacks = GetFrames2();
            var locals = variableDb.GetLocalVariablesForFrame(frameId);
            var globals = variableDb.GetGlobalVariablesForFrame(frameId);
            
            void AddVariable(DebugVariable local, bool isGlobal)
            {
                var variable = variableDb.GetRuntimeVariable(local);

                if (variable.typeCode == TypeCodes.STRUCT)
                {
                    throw new NotImplementedException("structs are a no go, friend");
                }

                if (variable.GetElementCount() > 0)
                {
                    throw new NotImplementedException("arrays are a no go, friend");
                }

                if (!TypeInfo.TryGetFromTypeCode(variable.typeCode, out var typeInfo))
                {
                    throw new InvalidOperationException($"unknown type code=[{variable.typeCode}] in eval function");
                }

                var compiledVariable = new CompiledVariable
                {
                    typeCode = variable.typeCode,
                    name = local.name,
                    registerAddress = (byte)variable.regAddr,
                    byteSize = TypeCodes.GetByteSize(variable.typeCode),
                    isGlobal = isGlobal
                }; // TODO: support structs and arrays
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
                    new TypeReferenceNode(typeInfo.type, new Token()));
                
                node.statements.Insert(0, decl);
            }
            
           
            
            foreach (var global in globals.variables)
            {
                AddVariable(global, true);
            }
            foreach (var local in locals.variables)
            {
                AddVariable(local, false);
            }
            
            
            node.AddScopeRelatedErrors(new ParseOptions());

            var finalStatement = (AssignmentStatement)node.statements.LastOrDefault(x => x is AssignmentStatement);
            if (finalStatement == null)
            {
                return DebugEvalResult.Failed("only declarations are allowed");
            }
            finalStatement.variable.Errors.Clear(); // remove the cast related error
            
            var parseErrors = node.GetAllErrors();
            if (parseErrors.Count > 0)
            {
                return DebugEvalResult.Failed($"{string.Join(",\n", parseErrors.Select(x => x.message))}");
            }
            
            
            /*
             * when the compilation happens, any references to existing variables and functions
             * need to get mapped into the current program space
             */

            var mergedVariableTable = new Dictionary<string, CompiledVariable>(globalVariableTable);
            foreach (var kvp in localVariableTable)
            {
                mergedVariableTable[kvp.Key] = kvp.Value;
            }

            var compileScope = new CompileScope(mergedVariableTable);
            compileScope.Create(SYNTHETIC_NAME, VmUtil.GetTypeCode(finalStatement.expression.ParsedType.type), false, regOffset:1);
            var compiler = new Compiler(_commandCollection, new CompilerOptions
            {
                GenerateDebugData = true
            }, givenGlobalScope: compileScope);
            // only compile the last statement, because this only evaluates one statement at a time.
            compiler.Compile(finalStatement);
            
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
            while (_vm.instructionIndex < _vm.program.Length)
            {
                _vm.Execute2(1);
            }

            if (!compiler.scopeStack.Peek().TryGetVariable(SYNTHETIC_NAME, out var synth))
            {
                throw new NotSupportedException("no compiled synthetic");
            }
            
            var runtimeSynth = new DebugRuntimeVariable(_vm, SYNTHETIC_NAME, synth.typeCode, _vm.dataRegisters[synth.registerAddress],
                _vm.scopeStack.Count -  frameId, synth.registerAddress);

            

            _vm.instructionIndex = originalInstructionIndex;
            _vm.program = originalProgram;
            _vm.state = originalState;
            _vm.scopeStack = originalScopeStack;
            _vm.scopeStack.ptr = originalScopePtr;
            _vm.stack = originalStack;
            _vm.scope = originalScope;


            var res = new DebugEvalResult
            {
                type = runtimeSynth.GetTypeName(),
                value = runtimeSynth.GetValueDisplay()
            };
            // TODO: only set if there are children variables...
            //res.id = variableDb.NextId();
            
            return res;
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
                    _vm.Execute2(1);
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


            SendExitedMessage();
        }
        
    }


    public enum DebugMessageType
    {
        NOOP,
        PROTO_HELLO,
        PROTO_ACK,
        
        REV_REQUEST_BREAKPOINT,
        REV_REQUEST_EXITED,
        
        REQUEST_PAUSE,
        REQUEST_PLAY,
        REQUEST_STEP_OVER,
        REQUEST_STEP_IN,
        REQUEST_STEP_OUT,
        REQUEST_TERMINATE,
        
        REQUEST_STACK_FRAMES,
        REQUEST_SCOPES,
        REQUEST_EVAL,
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
        public List<DebugVariable> variables = new List<DebugVariable>();
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(id), ref id);
            op.IncludeField(nameof(scopeName), ref scopeName);
            op.IncludeField(nameof(variables), ref variables);
        }
    }
    
    public class DebugVariable : IJsonable
    {
        public int id;
        public string name;
        public string type;
        public string value;

        public int fieldCount;
        public int elementCount;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(id), ref id);
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(type), ref type);
            op.IncludeField(nameof(value), ref value);
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
        public const int MAX_MESSAGE_LENGTH = 1024 * 8; // 8kb.

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
            var ip = new IPEndPoint(IPAddress.Any, port);

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
                        
                        var controlMessage = DecodeJsonable<T>(buffer);
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

        public static T DecodeJsonable<T>(byte[] bytes) 
            where T : IJsonable, IHasRawBytes, new()
        {
            var json = Encoding.UTF8.GetString(bytes);
            var inst = JsonableExtensions.FromJson<T>(json);
            inst.RawJson = json;
            return inst;
        }
        
        public static void OpenServer2<T>(
            int port, 
            ConcurrentQueue<T> outputQueue,
            ConcurrentQueue<T> inputQueue,
            CancellationToken cancellationToken)
            where T : IJsonable, IHasRawBytes, new()
        {
            // server only runs on local machine, cannot do cross machine debugging yet
            var ip = new IPEndPoint(IPAddress.Any, port);

            // host a socket...
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.ReceiveTimeout = 1; // if there isn't data available, just bail!
            socket.ReceiveBufferSize = 2048; // messages must be less than 2k
            socket.Bind(ip);
            socket.Listen(100);
            // var buffer = new ArraySegment<byte>(new byte[socket.ReceiveBufferSize]);
            var buffer = new byte[socket.ReceiveBufferSize];


            Socket handler = null;
            try
            {
                handler = socket.Accept();
                handler.ReceiveTimeout = 1;
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
                    var count = handler.Receive(buffer, 0, sizeof(int), SocketFlags.None, out var err);
                    if (err != SocketError.Success) continue;
                    if (count == 0) continue;

                    var length = BitConverter.ToInt32(buffer, 0);
                    
                    // try to receive a single message
                    count = handler.Receive(buffer, 0, length, SocketFlags.None,
                        out err);
                    if (err != SocketError.Success) continue;
                    if (count == 0) continue;
                    // TODO: should check that the received byte count is big enough to read a full message.

                    var controlMessage = DecodeJsonable<T>(buffer);
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