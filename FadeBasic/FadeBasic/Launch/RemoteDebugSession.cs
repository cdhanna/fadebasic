using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic.Json;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    public class RemoteDebugSession
    {
        private readonly int _port;

        private ConcurrentQueue<DebugMessage> outboundMessages = new ConcurrentQueue<DebugMessage>();
        private ConcurrentQueue<DebugMessage> inboundMessages = new ConcurrentQueue<DebugMessage>();
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private int messageIdCounter;
        private Task _receiveTask;
        public int RemoteProcessId { get; private set; }

        private ConcurrentDictionary<int, Action<DebugMessage>> _ackHandlers =
            new ConcurrentDictionary<int, Action<DebugMessage>>();

        public Action HitBreakpointCallback;
        public Action Exited;
        
        public RemoteDebugSession(int port)
        {
            _port = port;
            _cts = new CancellationTokenSource();
        }

        void RunClient(object state)
        {
            // outboundMessages.Enqueue(new DebugMessage
            // {
            //     id = GetNextMessageId(),
            //     type = DebugMessageType.PROTO_HELLO
            // });
            Send(new DebugMessage
            {
                id = GetNextMessageId(),
                type = DebugMessageType.PROTO_HELLO
            }, result =>
            {
                var detail = JsonableExtensions.FromJson<HelloResponseMessage>(result.RawJson);
                RemoteProcessId = detail.processId;
            });
            DebugServerStreamUtil.ConnectToServer2(_port, outboundMessages, inboundMessages, _cts.Token);
            throw new Exception("client died");
        }

        void Listen(object state)
        {
            while (!_cts.IsCancellationRequested)
            {
                while (inboundMessages.TryDequeue(out var message))
                {

                    if (message.id < 0)
                    {
                        // this is a rev-request

                        switch (message.type)
                        {
                            case DebugMessageType.REV_REQUEST_BREAKPOINT:
                                // ah, emit a stop event!
                                HitBreakpointCallback?.Invoke();
                                break;
                            case DebugMessageType.REV_REQUEST_EXITED:
                                Exited?.Invoke();
                                break;
                        }
                        
                        
                    } else if (_ackHandlers.TryGetValue(message.id, out var handler))
                    {
                        // handle ack callbacks
                        handler?.Invoke(message);
                    }

                }
                Thread.Sleep(10);
            }
        }
        
        public void Connect()
        {

            if (!ThreadPool.QueueUserWorkItem(RunClient))
            {
                throw new Exception("Failed to acquire debug client thread");
            }
            if (!ThreadPool.QueueUserWorkItem(Listen))
            {
                throw new Exception("Failed to acquire debug client messaging thread");
            }
        }

        public int GetNextMessageId() => Interlocked.Increment(ref messageIdCounter);

        public void Send(DebugMessage message, Action<DebugMessage> ackHandler)
        {
            _ackHandlers[message.id] = ackHandler;
            outboundMessages.Enqueue(message);
        }


        public void SendTerminate(Action handler) => Send(new DebugMessage
        {
            id = GetNextMessageId(),
            type = DebugMessageType.REQUEST_TERMINATE
        }, _ => handler());
        
        public void SendPause(Action handler) => Send(new DebugMessage()
        {
            id = GetNextMessageId(),
            type = DebugMessageType.REQUEST_PAUSE,
        }, _ => handler());
        
        public void SendPlay(Action handler) => Send(new DebugMessage
        {
            id = GetNextMessageId(),
            type = DebugMessageType.REQUEST_PLAY,
        }, _ => handler());

        public void SendStepOver(Action<StepNextResponseMessage> handler) => Send(new DebugMessage
        {
            id = GetNextMessageId(),
            type = DebugMessageType.REQUEST_STEP_OVER
        }, msg =>
        {
            var details = JsonableExtensions.FromJson<StepNextResponseMessage>(msg.RawJson);
            handler(details);
        });
        
        public void SendStepIn(Action<StepNextResponseMessage> handler) => Send(new DebugMessage
        {
            id = GetNextMessageId(),
            type = DebugMessageType.REQUEST_STEP_IN
        }, msg =>
        {
            var details = JsonableExtensions.FromJson<StepNextResponseMessage>(msg.RawJson);
            handler(details);
        });
        
        public void SendStepOut(Action<StepNextResponseMessage> handler) => Send(new DebugMessage
        {
            id = GetNextMessageId(),
            type = DebugMessageType.REQUEST_STEP_OUT
        }, msg =>
        {
            var details = JsonableExtensions.FromJson<StepNextResponseMessage>(msg.RawJson);
            handler(details);
        });

        public void RequestScopes(int frameIndex, Action<List<DebugScope>> handler)
        {
            Send(new DebugScopeRequest
            {
                id = GetNextMessageId(),
                type = DebugMessageType.REQUEST_SCOPES,
                frameIndex = frameIndex
            }, msg =>
            {
                var details = JsonableExtensions.FromJson<ScopesMessage>(msg.RawJson);
                handler?.Invoke(details.scopes);
            });
        }

        public void RequestVariableInfo(int variableId, Action<List<DebugScope>> handler)
        {
            Send(new DebugVariableExpansionRequest
            {
                id = GetNextMessageId(),
                type = DebugMessageType.REQUEST_VARIABLE_EXPANSION,
                variableId = variableId
            }, msg =>
            {
                var details = JsonableExtensions.FromJson<ScopesMessage>(msg.RawJson);
                handler?.Invoke(details.scopes);
            });
        }
        
        public void RequestStackFrames(Action<List<DebugStackFrame>> handler)
        {
            Send(new DebugMessage
            {
                id = GetNextMessageId(),
                type = DebugMessageType.REQUEST_STACK_FRAMES
            }, msg =>
            {
                var details = JsonableExtensions.FromJson<StackFrameMessage>(msg.RawJson);
                handler?.Invoke(details.frames);
            });
        }

        public void RequestBreakpoints(List<Breakpoint> breakpoints, Action<List<Breakpoint>> handler)
        {
            Send(new RequestBreakpointMessage
            {
                id = GetNextMessageId(),
                type = DebugMessageType.REQUEST_BREAKPOINTS,
                breakpoints = breakpoints
            }, msg =>
            {
                var details = JsonableExtensions.FromJson<ResponseBreakpointMessage>(msg.RawJson);
                handler?.Invoke(details.breakpoints);
            });
        }

    }

    public class Breakpoint : IJsonable
    {
        public int lineNumber;
        public int colNumber;
        public int status;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(lineNumber), ref lineNumber);
            op.IncludeField(nameof(colNumber), ref colNumber);
            op.IncludeField(nameof(status), ref status);
        }
    }

    public class RequestBreakpointMessage : DebugMessage
    {
        public List<Breakpoint> breakpoints;
        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(breakpoints), ref breakpoints);
        }
    }

    public class ResponseBreakpointMessage : DebugMessage
    {
        public List<Breakpoint> breakpoints;
        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(breakpoints), ref breakpoints);
        }
    }

    public class DebugScopeRequest : DebugMessage
    {
        public int frameIndex;

        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(frameIndex), ref frameIndex);
        }
    }
    
    public class DebugVariableExpansionRequest : DebugMessage
    {
        public int variableId;

        public override void ProcessJson(IJsonOperation op)
        {
            base.ProcessJson(op);
            op.IncludeField(nameof(variableId), ref variableId);
        }
    }

    public class DebugStackFrame : IJsonable
    {
        public int lineNumber;
        public int colNumber;
        public string name;

        public DebugStackFrame(){}
        
        public DebugStackFrame(int lineNumber, int colNumber)
        {
            this.lineNumber = lineNumber;
            this.colNumber = colNumber;
        }

        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(lineNumber), ref lineNumber);
            op.IncludeField(nameof(colNumber), ref colNumber);
            op.IncludeField(nameof(name), ref name);
        }
    }
}