using System;

namespace FadeBasic
{
    public static class StringUtil
    {
        public static string[] SplitNewLines(this string self)
        {
            return self.Split(new string[] { "\n", "\r\n" }, StringSplitOptions.None);
        }
    }
}