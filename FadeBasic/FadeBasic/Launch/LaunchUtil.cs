using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FadeBasic.Json;
using FadeBasic.Sdk;
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
            // Release builds skip debug-data emission, so the generated
            // launcher hands us an empty string. Return an empty DebugData
            // rather than letting the JSON parser index into "".
            if (string.IsNullOrEmpty(base64Json)) return new DebugData();
            var bytes = Convert.FromBase64String(base64Json);
            var json = Encoding.UTF8.GetString(bytes);
            return JsonableExtensions.FromJson<DebugData>(json);
        }

        public static string PackTestManifest(IReadOnlyList<TestManifestEntry> manifest)
        {
            var wrapper = new TestManifest();
            foreach (var entry in manifest) wrapper.entries.Add(entry);
            var json = wrapper.Jsonify();
            var bytes = Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes);
        }

        public static IReadOnlyList<TestManifestEntry> UnpackTestManifest(string base64Json)
        {
            if (string.IsNullOrEmpty(base64Json)) return new List<TestManifestEntry>();
            var bytes = Convert.FromBase64String(base64Json);
            var json = Encoding.UTF8.GetString(bytes);
            var wrapper = JsonableExtensions.FromJson<TestManifest>(json);
            return wrapper.entries;
        }

        /// <summary>
        /// Resolve each manifest entry's source location through the given
        /// <see cref="SourceMap"/>, replacing the concatenated-source line/char
        /// with in-file coordinates and stamping the originating
        /// <see cref="TestManifestEntry.sourceFilePath"/>. Idempotent: an entry
        /// whose <c>sourceFilePath</c> is already set is left alone, so calling
        /// this twice (e.g., once in the SDK path and again in the build-task
        /// generation path) doesn't double-shift line numbers.
        /// </summary>
        /// <remarks>
        /// Multi-<c>.fbasic</c> projects depend on this — the IDE Test Explorer
        /// uses each entry's <c>sourceFilePath</c> to source-link the right
        /// file when the user double-clicks a test. Without this remap, the
        /// adapter has no way to associate a manifest entry with its origin.
        /// </remarks>
        public static void ApplySourceMap(IReadOnlyList<TestManifestEntry> manifest, SourceMap map)
        {
            if (manifest == null || map == null) return;
            foreach (var entry in manifest)
            {
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(entry.sourceFilePath)) continue;
                try
                {
                    var loc = map.GetOriginalLocation(entry.sourceLine, entry.sourceChar);
                    entry.sourceFilePath = loc.fileName;
                    entry.sourceLine = loc.startLine;
                    entry.sourceChar = loc.startChar;
                }
                catch
                {
                    // SourceMap throws when the line falls outside any registered
                    // file (synthetic/computed positions). Leave the entry as-is
                    // — its sourceFilePath stays empty and the adapter falls
                    // back to omitting CodeFilePath rather than guessing.
                }
            }
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