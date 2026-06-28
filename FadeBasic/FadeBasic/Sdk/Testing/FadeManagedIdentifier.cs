using System.Text;

namespace FadeBasic.Testing
{
    /// <summary>
    /// Shared coercion of arbitrary strings (file basenames, assembly names)
    /// into C#-shaped identifiers. IDE Test Explorers (Rider, VS Code C# Dev
    /// Kit, Visual Studio) parse <c>TestCase.ManagedType</c> as a dotted path
    /// of valid identifiers; emitting raw <c>.fbasic</c> basenames with dashes
    /// or dots in them produces a broken tree. Both the VSTest adapter and
    /// the LSP-based discovery path call into this helper so the tree groups
    /// identically across IDEs.
    /// </summary>
    public static class FadeManagedIdentifier
    {
        public static string ToManagedIdentifier(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Tests";
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            // C# identifiers cannot start with a digit.
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.Length == 0 ? "Tests" : sb.ToString();
        }
    }
}
