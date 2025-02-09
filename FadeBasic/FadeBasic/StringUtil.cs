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
        ///  the path is to-lower'd
        /// NOTE: this means that paths are defacto case-insensitive, meaning you cannot have both
        ///  - afile.fbasic,
        ///  - aFile.fbasic
        /// </summary>
        /// <param name="self"></param>
        /// <returns></returns>
        public static string NormalizePathString(this string self)
        {
            return self.Replace("\\", "/").ToLowerInvariant();
        }
    }
}