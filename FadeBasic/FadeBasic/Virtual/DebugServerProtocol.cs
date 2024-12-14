// using System;
// using System.Collections.Concurrent;
// using System.Collections.Generic;
// using System.Net;
// using System.Net.Sockets;
// using System.Text;
// using System.Threading;
// using System.Threading.Tasks;
// using FadeBasic.Json;
//
// namespace FadeBasic.Virtual
// {
//     public class MessageServer<T> where T : IJsonable, new()
//     {
//         public static async Task Emit(Socket handler, CancellationTokenSource cts, ConcurrentQueue<T> outbound)
//         {
//             while (!cts.IsCancellationRequested)
//             {
//                 if (!outbound.TryDequeue(out var message))
//                 {
//                     await Task.Delay(10);
//                     continue;
//                 }
//
//                 var json = message.Jsonify();
//                 var jsonBytes = Encoding.UTF8.GetBytes(json);
//                 var segment = new ArraySegment<byte>(jsonBytes);
//                 await handler.SendAsync(segment, SocketFlags.None);
//             }
//         }
//         
//         public static async Task Listen(Socket handler, CancellationTokenSource cts, ConcurrentQueue<T> received)
//         {
//             var buffer = new byte[2048];
//             var segment = new ArraySegment<byte>(buffer);
//             while (!cts.IsCancellationRequested)
//             {
//                 await Task.Delay(10);
//                 var count = handler.Receive(buffer, 0, buffer.Length, SocketFlags.None, out var err);
//                 // var count = await handler.ReceiveAsync(segment, SocketFlags.None);
//                 if (err != SocketError.Success) continue;
//                 if (count == 0) continue;
//                 var str = Encoding.UTF8.GetString(buffer, 0, count);
//                 var msg = JsonableExtensions.FromJson<T>(str);
//                 received.Enqueue(msg);
//             }
//         }
//
//         public static async Task<Socket> Join(int port)
//         {
//             var ip = new IPEndPoint(IPAddress.Any, port);
//             var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
//             await socket.ConnectAsync(ip);
//             return socket;
//         }
//         
//         public static async Task<Socket> Host(int port)
//         {
//             // server only runs on local machine, cannot do cross machine debugging yet
//             var ip = new IPEndPoint(IPAddress.Any, port);
//
//             // host a socket...
//             var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
//             socket.ReceiveTimeout = 1; // if there isn't data available, just bail!
//             socket.ReceiveBufferSize = 2048; // messages must be less than 2k
//             socket.Bind(ip);
//             socket.Listen(100);
//             // socket.Blocking = false;
//             // var buffer = new ArraySegment<byte>(new byte[socket.ReceiveBufferSize]);
//           
//             var handler = await socket.AcceptAsync();
//             // handler.Blocking = false;
//             handler.ReceiveTimeout = 1;
//             return handler;
//         }
//     }
// }