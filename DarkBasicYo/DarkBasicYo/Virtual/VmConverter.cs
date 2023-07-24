using System;
using System.Text;

namespace DarkBasicYo.Virtual
{
    public static class VmConverter
    {
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
    }
}