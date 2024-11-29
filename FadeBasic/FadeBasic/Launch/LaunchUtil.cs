using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FadeBasic.Json;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    public class LaunchUtil
    {

        public static int FreeTcpPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public static string PackDebugData(DebugData data)
        {
            var json = data.Jsonify();
            var bytes = Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes);
        }

        public static DebugData UnpackDebugData(string base64Json)
        {
            var bytes = Convert.FromBase64String(base64Json);
            var json = Encoding.UTF8.GetString(bytes);
            return JsonableExtensions.FromJson<DebugData>(json);
        }
        
        public static byte[] Unpack64(string encoded)
        {
            return Convert.FromBase64String(encoded);
        }
        
        public static string Pack64(byte[] byteCode)
        {
            return Convert.ToBase64String(byteCode);
            
            // var sb = new StringBuilder();
            // var byteCodeSpan = byteCodeStr.AsSpan();
            // var lineLength = 100;
            // sb.Append("\n");
            // sb.Append(TEMPLATE_BYTECODE_TAB);
            // for (var i = 0; i < byteCodeStr.Length; i += lineLength)
            // {
            //     var length = (int)MathF.Min(lineLength, byteCodeStr.Length - i);
            //     var slice = byteCodeSpan.Slice(i, length);
            //     sb.Append("\"");
            //     sb.Append(slice);
            //     sb.Append("\"");
            //     if (i+length < byteCodeStr.Length)
            //     {
            //         sb.Append("+\n");
            //         sb.Append(TEMPLATE_BYTECODE_TAB);
            //     }
            //
            // }
            //
            // byteCodeReplacement = sb.ToString();

        }
    }
}