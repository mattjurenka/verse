using System;
using System.Text;

namespace HallPatton
{
    /// <summary>
    /// Keyword matching for what players type. Deliberately free of Unity and Valheim types so
    /// the test project in tests/MatcherTests can compile and exercise this exact file.
    /// </summary>
    internal static class Matcher
    {
        /// <summary>
        /// Lowercases, flattens every non-alphanumeric character to a space, collapses runs
        /// of spaces, and pads both ends. The padding is what lets <see cref="Has"/> test for
        /// whole words by searching for " word " — including the first and last word.
        /// </summary>
        public static string Normalize(string input)
        {
            if (string.IsNullOrEmpty(input)) return " ";

            var sb = new StringBuilder(input.Length + 2);
            sb.Append(' ');
            foreach (char c in input)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
                else if (sb[sb.Length - 1] != ' ')
                    sb.Append(' ');
            }
            if (sb[sb.Length - 1] != ' ') sb.Append(' ');
            return sb.ToString();
        }

        /// <summary>
        /// Whole-word match against a <see cref="Normalize"/>d haystack, so "eat" no longer
        /// fires on "death" and "hey" no longer fires on "they". Multi-word needles match as
        /// a phrase. A needle ending in '*' matches a word prefix instead, which is how
        /// "apolog*" covers apology and apologise.
        /// </summary>
        public static bool Has(string padded, params string[] needles)
        {
            foreach (string n in needles)
            {
                if (string.IsNullOrEmpty(n)) continue;
                bool matched = n[n.Length - 1] == '*'
                    ? padded.IndexOf(" " + n.Substring(0, n.Length - 1), StringComparison.Ordinal) >= 0
                    : padded.IndexOf(" " + n + " ", StringComparison.Ordinal) >= 0;
                if (matched) return true;
            }
            return false;
        }
    }
}
