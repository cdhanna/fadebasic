using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic.Json;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    // make a debug server!
    public class DebugSession
    {
        private VirtualMachine _vm;
        private DebugData _dbg;
        private LaunchOptions _options;

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
        private IntervalTree _tree;
        private DebugMap _pauseLocal;

        private Task _taskGraph;

        public int InstructionPointer => _vm.instructionIndex;

        public DebugSession(VirtualMachine vm, DebugData dbg, LaunchOptions options=null)
        {
            _options = options ?? LaunchOptions.DefaultOptions;
            _dbg = dbg;
            _vm = vm;
            _tree = IntervalTree.From(dbg.points);
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
            
            // _taskGraph = Task.Run(async () =>
            // {
            //     try
            //     {
            //         var hostTask = MessageServer<DebugMessage>.Host(_options.debugPort);
            //
            //         var handler = await hostTask;
            //         var sendTask = MessageServer<DebugMessage>.Emit(handler, _cts, outboundMessages);
            //         var receiveTask = MessageServer<DebugMessage>.Listen(handler, _cts, receivedMessages);
            //
            //         await sendTask;
            //         await receiveTask;
            //     }
            //     catch (Exception ex)
            //     {
            //         _serverTaskEx = ex;
            //     }
            // });

            #region old junk
            // {
            //     try
            //     {
            //         DebugServerStreamUtil.OpenServer(_options.debugPort, outboundMessages, receivedMessages, _cts.Token);
            //     }
            //     catch (Exception ex)
            //     {
            //         _serverTaskEx = ex;
            //     }
            // });

            // _processingTask = Task.Run(async () =>
            // {
            //     try
            //     {
            //         while (!_cts.IsCancellationRequested)
            //         {
            //             await Task.Delay(1);
            //             while (receivedMessages.TryDequeue(out var message))
            //             {
            //                 switch (message.type)
            //                 {
            //                     case DebugControlMessageTypes.PROTOCOL_HELLO when !hasReceivedOpen:
            //                         hasReceivedOpen = true;
            //                         if (_options.debugWaitForConnection)
            //                         {
            //                             // TODO: okay, we can start the session now...
            //                         }
            //
            //                         outboundMessages.Enqueue(new DebugControlMessage
            //                         {
            //                             id = message.id,
            //                             type = DebugControlMessageTypes.PROTOCOL_ACK
            //                         });
            //                         break;
            //                     case DebugControlMessageTypes.GET_CURRENT_STACK_FRAMES:
            //
            //
            //                         // TODO: send multiple stack frames?
            //                         var token = _pauseLocal.range.startToken.token;
            //                         DebugUtil.PackPosition(token.lineNumber, token.charNumber, out var packed);
            //                         outboundMessages.Enqueue(new DebugControlMessage
            //                         {
            //                             id = message.id,
            //                             type = DebugControlMessageTypes.PROTOCOL_RES,
            //                             arg = packed
            //                         });
            //                         outboundMessages.Enqueue(new DebugControlMessage
            //                         {
            //                             id = message.id,
            //                             type = DebugControlMessageTypes.PROTOCOL_ACK,
            //                             arg = 1
            //                         });
            //
            //                         break;
            //                     case DebugControlMessageTypes.PAUSE:
            //                         if (Interlocked.Read(ref pauseRequestedByMessageId) > 0)
            //                         {
            //                             outboundMessages.Enqueue(new DebugControlMessage
            //                             {
            //                                 id = message.id,
            //                                 type = DebugControlMessageTypes.PROTOCOL_NACK,
            //                                 arg = DebugControlMessageArgFlags.ALREADY_IN_PROGRESS
            //                             });
            //                             break;
            //                         }
            //                         else
            //                         {
            //                             Interlocked.Exchange(ref pauseRequestedByMessageId, message.id);
            //                             break;
            //                         }
            //
            //                     case DebugControlMessageTypes.PLAY:
            //                         if (Interlocked.Read(ref resumeRequestedByMessageId) > 0)
            //                         {
            //                             // do nothing.
            //                             outboundMessages.Enqueue(new DebugControlMessage
            //                             {
            //                                 id = message.id,
            //                                 type = DebugControlMessageTypes.PROTOCOL_NACK,
            //                                 arg = DebugControlMessageArgFlags.ALREADY_IN_PROGRESS
            //                             });
            //                             break;
            //                         }
            //                         else
            //                         {
            //                             Interlocked.Exchange(ref resumeRequestedByMessageId, message.id);
            //                             break;
            //                         }
            //                 }
            //
            //             }
            //         }
            //     }
            //     catch (Exception ex)
            //     {
            //         throw; 
            //     }
            // });
            #endregion
        }

        void RunServer(object state)
        {
            DebugServerStreamUtil.OpenServer2(_options.debugPort, outboundMessages, receivedMessages, _cts.Token);
            throw new Exception("uh oh server2 is dead");
        }
        
        void Ack(DebugMessage originalMessage)
        {
            outboundMessages.Enqueue(new DebugMessage
            {
                id = originalMessage.id,
                type = DebugMessageType.PROTO_ACK
            });
        }
        
        void ReadMessage()
        {
            if (receivedMessages.TryDequeue(out var message))
            {
                switch (message.type)
                {
                    case DebugMessageType.PROTO_HELLO:
                        hasConnectedDebugger = 1;
                        Ack(message);
                        break;
                    case DebugMessageType.REQUEST_PAUSE:
                        Ack(message);
                        break;
                }
                // switch (message.type)
                // {
                //     case DebugControlMessageTypes.PROTOCOL_HELLO:
                //         hasConnectedDebugger = 1;
                //         outboundMessages.Enqueue(new DebugMessageLegacy
                //         {
                //             id = message.id,
                //             type = DebugControlMessageTypes.PROTOCOL_ACK
                //         });
                //         break;
                //     case DebugControlMessageTypes.PAUSE:
                //         if (pauseRequestedByMessageId > 0)
                //         {
                //             outboundMessages.Enqueue(new DebugMessageLegacy()
                //             {
                //                 id = message.id,
                //                 type = DebugControlMessageTypes.PROTOCOL_NACK,
                //             });
                //             break;
                //         }
                //         else
                //         {
                //             Interlocked.Exchange(ref pauseRequestedByMessageId, message.id);
                //             break;
                //         }
                // }
            }
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

            if (budget <= 0) return; // the while-loop below should do this too, but for sake of reading...
            
            //
            while (_vm.instructionIndex < _vm.program.Length)
            {
                if (ops > 0 && budget-- <= 0)
                {
                    // break up the execution of the debug session so that it can be interwoven with client process.
                    break;
                }
                
                ReadMessage();
                _vm.Execute2(1);
            }
            
        }
        
        // public void Continue() => Execute(true);

        // public void Execute(bool ignoreFirstBreakpoint=false)
        // {
        //     while (_vm.instructionIndex < _vm.program.Length)
        //     {
        //         var isBreakpoint = _dbg.insBreakpoints.Contains(_vm.instructionIndex);
        //         
        //         if (isBreakpoint)
        //         {
        //             if (!ignoreFirstBreakpoint)
        //             {
        //                 break;
        //             }
        //             else
        //             {
        //                 ignoreFirstBreakpoint = false;
        //             }
        //         }
        //         _vm.Execute2(1);
        //     }
        // }
        
    }


    public enum DebugMessageType
    {
        NOOP,
        PROTO_HELLO,
        PROTO_ACK,
        
        REQUEST_PAUSE
    }
    public class DebugMessage : IJsonable
    {
        public int id;
        public DebugMessageType type;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(id), ref id);
            op.IncludeField(nameof(type), ref type);
        }
    }

    public class DebugMessageLegacy : IJsonable
    {
        public int type;
        public int id;
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(id), ref id);
            op.IncludeField(nameof(type), ref type);
        }
    }
    
    public static class DebugControlMessageArgFlags
    {
        public const ulong ALREADY_IN_PROGRESS = 1;
    }

    public static class DebugControlMessageTypes
    {
        
        public const byte PLAY = 1;
        public const byte PAUSE = 2;
        public const byte ADD_BREAKPOINT = 3;
        public const byte REM_BREAKPOINT = 4;
        public const byte GET_CURRENT_STACK_FRAMES = 5;
        
        
        public const byte PROTOCOL_HELLO = 253;
        public const byte PROTOCOL_ACK = 255;
        public const byte PROTOCOL_NACK = 254;
        public const byte PROTOCOL_RES = 254;
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
        
        public static async Task ConnectToServerAsync(
            int port, 
            ConcurrentQueue<DebugControlMessage> outputQueue, 
            ConcurrentQueue<DebugControlMessage> inputQueue,
            CancellationToken cancellationToken)
        {
            var ip = new IPEndPoint(IPAddress.Any, port);

            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await socket.ConnectAsync(ip);
                    break;
                }
                catch
                {
                    // ignore
                    await Task.Delay(1);
                }
            }
            

            socket.ReceiveTimeout = 1;
            var buffer = new byte[socket.ReceiveBufferSize];
            var readSegment = new ArraySegment<byte>(buffer);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1);

                    // try to send out all pending messages
                    while (outputQueue.TryDequeue(out var msgToSend))
                    {
                        var sendBytes = EncodeMessage(msgToSend);
                        await socket.SendAsync(new ArraySegment<byte>(sendBytes), SocketFlags.None);
                        // var sentByteCount = socket.Send(sendBytes, 0, sendBytes.Length, SocketFlags.None,
                        //     out var sendError);
                        // if (sendError != SocketError.Success)
                        // {
                        //     // uh oh?
                        // }
                    }

                    var count = await socket.ReceiveAsync(readSegment, SocketFlags.None);
                    // var count = socket.Receive(buffer, 0, sizeof(byte) + 2 * sizeof(ulong), SocketFlags.None,
                    //     out var err);
                    // if (err != SocketError.Success) continue;
                    if (count == 0) continue;
                    // TODO: should check that the received byte count is big enough to read a full message.

                    var controlMessage = DecodeMessage(buffer);
                    inputQueue.Enqueue(controlMessage);
                }
            }
            finally
            {
                socket.Disconnect(true);
                socket.Close();
            }
        }
        
        public static void ConnectToServer(
            int port, 
            ConcurrentQueue<DebugControlMessage> outputQueue, 
            ConcurrentQueue<DebugControlMessage> inputQueue,
            CancellationToken cancellationToken)
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
            var buffer = new byte[socket.ReceiveBufferSize];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(1);

                    // try to send out all pending messages
                    while (outputQueue.TryDequeue(out var msgToSend))
                    {
                        var sendBytes = EncodeMessage(msgToSend);
                        var sentByteCount = socket.Send(sendBytes, 0, sendBytes.Length, SocketFlags.None,
                            out var sendError);
                        if (sendError != SocketError.Success)
                        {
                            // uh oh?
                        }
                    }

                    var count = socket.Receive(buffer, 0, sizeof(byte) + 2 * sizeof(ulong), SocketFlags.None,
                        out var err);
                    if (err != SocketError.Success) continue;
                    if (count == 0) continue;
                    // TODO: should check that the received byte count is big enough to read a full message.

                    var controlMessage = DecodeMessage(buffer);
                    inputQueue.Enqueue(controlMessage);
                }
            }
            finally
            {
                socket.Disconnect(true);
                socket.Close();
            }
        }
        
        public static void ConnectToServer2<T>(
            int port, 
            ConcurrentQueue<T> outputQueue, 
            ConcurrentQueue<T> inputQueue,
            CancellationToken cancellationToken)
            where T : IJsonable, new()
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
            var buffer = new byte[socket.ReceiveBufferSize];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Thread.Sleep(1);

                    // try to send out all pending messages
                    while (outputQueue.TryDequeue(out var msgToSend))
                    {
                        var sendBytes = EncodeJsonable(msgToSend);
                        var sentByteCount = socket.Send(sendBytes, 0, sendBytes.Length, SocketFlags.None,
                            out var sendError);
                        if (sendError != SocketError.Success)
                        {
                            // uh oh?
                        }
                    }

                    var count = socket.Receive(buffer, 0, sizeof(byte) + 2 * sizeof(ulong), SocketFlags.None,
                        out var err);
                    if (err != SocketError.Success) continue;
                    if (count == 0) continue;
                    // TODO: should check that the received byte count is big enough to read a full message.

                    var controlMessage = DecodeJsonable<T>(buffer);
                    inputQueue.Enqueue(controlMessage);
                }
            }
            finally
            {
                socket.Disconnect(true);
                socket.Close();
            }
        }

        
        static DebugControlMessage DecodeMessage(byte[] bytes)
        {
            var type = bytes[0];
            var arg = BitConverter.ToUInt64(bytes, sizeof(byte));
            var id = BitConverter.ToInt64(bytes, sizeof(byte) + sizeof(ulong));
            return new DebugControlMessage
            {
                type = type,
                arg = arg,
                id = id,
            };
        }

        static byte[] EncodeMessage(DebugControlMessage message)
        {
            var bytes = new byte[sizeof(byte) + 2 * sizeof(ulong)];
            bytes[0] = message.type;
            var argBytes = BitConverter.GetBytes(message.arg);
            for (var i = 0; i < argBytes.Length; i++)
            {
                bytes[i + sizeof(byte)] = argBytes[i];
            }
            argBytes = BitConverter.GetBytes(message.id);
            for (var i = 0; i < argBytes.Length; i++)
            {
                bytes[i + sizeof(byte) + sizeof(ulong)] = argBytes[i];
            }

            return bytes;
        }

        public static async Task OpenServerAsync(
            int port, 
            ConcurrentQueue<DebugControlMessage> outputQueue,
            ConcurrentQueue<DebugControlMessage> inputQueue,
            CancellationToken cancellationToken)
        {
            // server only runs on local machine, cannot do cross machine debugging yet
            var ip = new IPEndPoint(IPAddress.Any, port);

            // host a socket...
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            // socket.ReceiveTimeout = 1; // if there isn't data available, just bail!
            socket.ReceiveBufferSize = 2048; // messages must be less than 2k
            socket.Bind(ip);
            socket.Listen(100);
            // var buffer = new ArraySegment<byte>(new byte[socket.ReceiveBufferSize]);
            var buffer = new byte[socket.ReceiveBufferSize];
            
            var handler = await socket.AcceptAsync();
            handler.ReceiveTimeout = 1;


            try
            {
                var receiveArray = new ArraySegment<byte>(buffer);
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1);

                    // try to send out all pending messages
                    while (outputQueue.TryDequeue(out var msgToSend))
                    {
                        var sendBytes = EncodeMessage(msgToSend);
                        await handler.SendAsync(new ArraySegment<byte>(sendBytes), SocketFlags.None);
                        // var sentByteCount = handler.Send(sendBytes, 0, sendBytes.Length, SocketFlags.None,
                        //     out var sendError);
                        // if (sendError != SocketError.Success)
                        // {
                        //     // uh oh?
                        // }
                    }

                    // try to receive a single message

                    var count = await handler.ReceiveAsync(receiveArray, SocketFlags.None);
                    // var count = handler.Receive(buffer, 0, sizeof(byte) + 2 * sizeof(ulong), SocketFlags.None,
                    //     out var err);
                    // if (err != SocketError.Success) continue;
                    if (count == 0) continue;
                    // TODO: should check that the received byte count is big enough to read a full message.

                    var controlMessage = DecodeMessage(buffer);
                    inputQueue.Enqueue(controlMessage);

                    // the message always takes the form of
                    //  COMMAND,ARG0,ID
                }
            }
            catch
            {
                
            }
            finally
            {
                handler.Close();
                socket.Close();
            }

        }

        public static byte[] EncodeJsonable(IJsonable jsonable)
        {
            var json = jsonable.Jsonify();
            var bytes = Encoding.UTF8.GetBytes(json);
            return bytes;
        }

        public static T DecodeJsonable<T>(byte[] bytes) where T : IJsonable, new()
        {
            var json = Encoding.UTF8.GetString(bytes);
            var inst = JsonableExtensions.FromJson<T>(json);
            return inst;
        }
        
        public static void OpenServer2<T>(
            int port, 
            ConcurrentQueue<T> outputQueue,
            ConcurrentQueue<T> inputQueue,
            CancellationToken cancellationToken)
            where T : IJsonable, new()
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
                        var jsonBytes = EncodeJsonable(msgToSend);
                        // var sendBytes = EncodeMessage(msgToSend);
                        var sentByteCount = handler.Send(jsonBytes, 0, jsonBytes.Length, SocketFlags.None,
                            out var sendError);
                        if (sendError != SocketError.Success)
                        {
                            // uh oh?
                        }
                    }

                    // try to receive a single message
                    var count = handler.Receive(buffer, 0, sizeof(byte) + 2 * sizeof(ulong), SocketFlags.None,
                        out var err);
                    if (err != SocketError.Success) continue;
                    if (count == 0) continue;
                    // TODO: should check that the received byte count is big enough to read a full message.

                    var controlMessage = DecodeJsonable<T>(buffer);
                    inputQueue.Enqueue(controlMessage);

                    // the message always takes the form of
                    //  COMMAND,ARG0,ID
                }
            }
            finally
            {
                handler.Close();
                socket.Close();
            }

        }
                
        public static void OpenServer(
            int port, 
            ConcurrentQueue<DebugControlMessage> outputQueue,
            ConcurrentQueue<DebugControlMessage> inputQueue,
            CancellationToken cancellationToken)
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
                        var sendBytes = EncodeMessage(msgToSend);
                        var sentByteCount = handler.Send(sendBytes, 0, sendBytes.Length, SocketFlags.None,
                            out var sendError);
                        if (sendError != SocketError.Success)
                        {
                            // uh oh?
                        }
                    }

                    // try to receive a single message
                    var count = handler.Receive(buffer, 0, sizeof(byte) + 2 * sizeof(ulong), SocketFlags.None,
                        out var err);
                    if (err != SocketError.Success) continue;
                    if (count == 0) continue;
                    // TODO: should check that the received byte count is big enough to read a full message.

                    var controlMessage = DecodeMessage(buffer);
                    inputQueue.Enqueue(controlMessage);

                    // the message always takes the form of
                    //  COMMAND,ARG0,ID
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