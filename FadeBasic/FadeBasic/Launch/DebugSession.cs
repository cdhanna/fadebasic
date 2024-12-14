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

        public bool IsPaused => pauseRequestedByMessageId > resumeRequestedByMessageId;

        public DebugSession(VirtualMachine vm, DebugData dbg, LaunchOptions options=null)
        {
            _options = options ?? LaunchOptions.DefaultOptions;
            _dbg = dbg;
            _vm = vm;
            var flat = dbg.GetFlatPoints();
            _tree = IntervalTree.From(flat);
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

        void Ack<T>(DebugMessage originalMessage, T responseMsg)
            where T : DebugMessage
        {
            responseMsg.id = originalMessage.id;
            responseMsg.type = DebugMessageType.PROTO_ACK;
            outboundMessages.Enqueue(responseMsg);
        }
        
        void ReadMessage()
        {
            if (receivedMessages.TryDequeue(out var message))
            {
                Console.WriteLine($"[DBG] Received message : {message.id}, {message.type}");

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
                        Console.WriteLine($"[DBG] enqueued ack for pause");

                        break;
                    case DebugMessageType.REQUEST_PLAY:
                        resumeRequestedByMessageId = message.id;
                        Ack(message);
                        break;
                    case DebugMessageType.REQUEST_NEXT:
                        
                        Ack(message); // TODO: only send the ack after we have started playing again...
                        break;
                    case DebugMessageType.REQUEST_STACK_FRAMES:
                        // TODO: 
                        //  how do I generate a list of stack frames at any given moment? 
                        //  probably by looking at the scopeStack in the vm
                        Console.WriteLine($"[DBG] stack frames");

                        if (this._tree.TryFind(_vm.instructionIndex, out var map))
                        {
                            var lineNumber = map.range.startToken.token.lineNumber;
                            var charNumber = map.range.startToken.token.charNumber;
                            var frame = new DebugStackFrame(lineNumber, charNumber);
                            
                            Ack(message, new StackFrameMessage
                            {
                                frames = new List<DebugStackFrame>
                                {
                                    frame
                                }
                            });
                            Console.WriteLine($"[DBG] enqueued stack frame response");

                        }
                        else
                        {
                            Console.WriteLine($"[DBG] no instruction for frame. ins=[{_vm.instructionIndex}]");
                            Ack(message, new StackFrameMessage
                            {
                                frames = new List<DebugStackFrame>()
                            });
                        }
                        break;
                }
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
            
            if (ops > 0 && budget <= 0) return; // the while-loop below should do this too, but for sake of reading...
            
            //
            while (_vm.instructionIndex < _vm.program.Length)
            {
                if (ops > 0 && budget-- <= 0)
                {
                    // break up the execution of the debug session so that it can be interwoven with client process.
                    break;
                }
                
                ReadMessage();

                if (!IsPaused)
                {
                    _vm.Execute2(1);
                }
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
        
        REQUEST_PAUSE,
        REQUEST_PLAY,
        REQUEST_NEXT,
        
        REQUEST_STACK_FRAMES
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