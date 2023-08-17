using System;
using System.Text;

namespace DarkBasicYo.Virtual
{
    public static class VmConverter
    {
        public static string ToStringSpan(ReadOnlySpan<byte> strBytes)
        {
            return ToString(strBytes.ToArray()); // TODO: revisit this later...
        }
        
        public static string ToString(byte[] strBytes)
        {
            var sb = new StringBuilder(); // TODO: make this a static cached non alloc sb
            for (var i = 0; i < strBytes.Length; i+=4)
            {
                uint c = BitConverter.ToUInt32(strBytes, i );
                var c2 = (char)c;
                sb.Append(c2);
            }

            return sb.ToString();
        }

        public static void FromStringSpan(string str, out ReadOnlySpan<byte> bytes)
        {
            FromString(str, out var arr); // TODO: revisit this later...
            bytes = new ReadOnlySpan<byte>(arr);
        }

        public static void FromString(string str, out byte[] bytes)
        {
            bytes = new byte[str.Length * 4];
            for (var i = 0; i < str.Length; i++)
            {
                var c = (int)str[i];
                var charBytes = BitConverter.GetBytes(c);
                for (var x = 0; x < charBytes.Length; x++)
                {
                    bytes[(i * 4) + x] = charBytes[x];
                }
            }
        }
    }
}