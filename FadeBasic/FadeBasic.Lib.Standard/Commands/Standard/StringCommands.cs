using System;
using FadeBasic.Lib.Standard.Util;
using FadeBasic.SourceGenerators;

namespace FadeBasic.Lib.Standard
{
    public partial class StandardCommands
    {
        [FadeBasicCommand("upper$", FadeBasicCommandUsage.Both)]
        public static string Upper(string str)
        {
            return str.ToUpperInvariant();
        }
        
        [FadeBasicCommand("lower$", FadeBasicCommandUsage.Both)]
        public static string Lower(string str)
        {
            return str.ToLowerInvariant();
        }
        
        [FadeBasicCommand("right$", FadeBasicCommandUsage.Both)]
        public static string Right(string str, int value)
        {
            var arg = MathUtil.Clamp(str.Length - value, 0, str.Length);
            return str.Substring(arg);
        }
        
        [FadeBasicCommand("left$", FadeBasicCommandUsage.Both)]
        public static string Left(string str, int value)
        {
            // hello -> h
            // substr (0, 
            var arg = MathUtil.Clamp(value , 0, str.Length);
            return str.Substring(0, arg);
        }
        
        [FadeBasicCommand("mid$", FadeBasicCommandUsage.Both)]
        public static string Mid(string str, int value)
        {
            value = MathUtil.Clamp(value - 1, 0, str.Length - 1);
            return str[value].ToString();
        }
        
        [FadeBasicCommand("chr$", FadeBasicCommandUsage.Both)]
        public static string StringChr(int value)
        {
            return ((char)value).ToString();
        }

        [FadeBasicCommand("str$", FadeBasicCommandUsage.Both)]
        public static string Str(int data)
        {
            return data.ToString();
        }
        
        // TODO: add support for Bin$
        // TODO: add support for Hex$
        
        [FadeBasicCommand("spaces$", FadeBasicCommandUsage.Both)]
        public static string StringSpaces(int count)
        {
            return new string(' ', count);
        }
       
        [FadeBasicCommand("val", FadeBasicCommandUsage.Both)]
        public static float StringValue(string data)
        {
            // TODO; actually, input like "12ABC" should map to 12
            if (!float.TryParse(data, out var val))
            {
                val = 0;
            }

            return val;
        }
        
        [FadeBasicCommand("len", FadeBasicCommandUsage.Both)]
        public static int StringLen(string str)
        {
            return str.Length;
        }
        
        [FadeBasicCommand("asc", FadeBasicCommandUsage.Both)]
        public static int StringAsc(string str)
        {
            if (str.Length == 0) return 0;
            return (int)str[0];
        }
    }
}