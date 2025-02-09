using System;
using System.IO;

namespace FadeBasic
{
    public static class StringUtil
    {
        public static string[] SplitNewLines(this string self)
        {
            return self.Split(new string[] { "\n", "\r\n" }, StringSplitOptions.None);
        }

        /// <summary>
        ///  any backslashes in the string will be replaced with forward slashes.
        /// </summary>
        /// <param name="self"></param>
        /// <returns></returns>
        public static string ConvertToForwardSlashPath(this string self)
        {
            return self.Replace("\\", "/");
        }
    }
}