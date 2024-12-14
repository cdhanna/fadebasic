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

                    // handle ack callbacks
                    if (_ackHandlers.TryGetValue(message.id, out var handler))
                    {
                        handler?.Invoke(message);
                    }

                    // TODO: handle actual events from the debugger like, "hey do something!"
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
            // Task.Run(async () =>
            // {
            //     try
            //     {
            //         var joinTask = MessageServer<DebugMessage>.Join(_port);
            //
            //         var socket = await joinTask;
            //
            //         var sendTask = MessageServer<DebugMessage>.Emit(socket, _cts, outboundMessages);
            //         var receiveTask = MessageServer<DebugMessage>.Listen(socket, _cts, inboundMessages);
            //
            //         Send(new DebugMessage
            //         {
            //             id = GetNextMessageId(),
            //             type = DebugControlMessageTypes.PROTOCOL_HELLO
            //         }, res =>
            //         {
            //             var isSuccess = (res.type == DebugControlMessageTypes.PROTOCOL_ACK);
            //         });
            //         
            //         await sendTask;
            //         await receiveTask;
            //     }
            //     catch (Exception ex)
            //     {
            //         throw;
            //     }
            // });
            //
            //
            // // _listenTask =  DebugServerStreamUtil.ConnectToServerAsync(_port, outboundMessages, inboundMessages, _cts.Token);
            //
            // _receiveTask = Task.Run(async () =>
            // {
            //     try
            //     {
            //         while (!_cts.IsCancellationRequested)
            //         {
            //             await Task.Delay(1);
            //             while (inboundMessages.TryDequeue(out var message))
            //             {
            //
            //                 // handle ack callbacks
            //                 if (_ackHandlers.TryGetValue(message.id, out var handler))
            //                 {
            //                     handler?.Invoke(message);
            //                 }
            //
            //                 // TODO: handle actual events from the debugger like, "hey do something!"
            //             }
            //
            //             Thread.Sleep(1);
            //         }
            //     }
            //     catch (Exception ex)
            //     {
            //         throw;
            //     }
            // });
        }

        public int GetNextMessageId() => Interlocked.Increment(ref messageIdCounter);

        public void Send(DebugMessage message, Action<DebugMessage> ackHandler)
        {
            _ackHandlers[message.id] = ackHandler;
            outboundMessages.Enqueue(message);
        }

        // public void RequestStackFrames(Action<List<DebugStackFrame>> handler)
        // {
        //     var frames = new List<DebugStackFrame>();
        //     DebugStackFrame current = null;
        //     
        //     Send(new DebugControlMessage
        //     {
        //         id = GetNextMessageId(),
        //         arg = 0,
        //         type = DebugControlMessageTypes.GET_CURRENT_STACK_FRAMES
        //     }, msg =>
        //     {
        //         switch (msg.type)
        //         {
        //             case DebugControlMessageTypes.PROTOCOL_RES:
        //                 if (current == null)
        //                 {
        //                     DebugUtil.UnpackPosition(msg.arg, out var lineNumber, out var charNumber );
        //                     // line number, and char number are packed into a long.
        //                     current = new DebugStackFrame(lineNumber, charNumber);
        //                     frames.Add(current);
        //                 }
        //                 break;
        //             case DebugControlMessageTypes.PROTOCOL_ACK:
        //                 handler?.Invoke(frames);
        //                 break;
        //         }
        //         // building up a response....
        //         
        //     });
        // }
        //
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

        public void SendNext(Action handler) => Send(new DebugMessage
        {
            id = GetNextMessageId(),
            type = DebugMessageType.REQUEST_NEXT
        }, _ => handler());

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
    }

    public class DebugStackFrame : IJsonable
    {
        public int lineNumber;
        public int colNumber;

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
        }
    }
}