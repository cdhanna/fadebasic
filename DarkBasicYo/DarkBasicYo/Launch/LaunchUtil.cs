using System;
namespace DarkBasicYo.Launch
{
    public class LaunchUtil
    {

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