using System.Text.RegularExpressions;

namespace FadeBasic
{
    public static class RegexUtil
    {
        public static string ReplaceDocSlashes(string input) => Regex.Replace(input, "\\n\\s*///\\s*", "\n///").Replace("///", "");
    }
}