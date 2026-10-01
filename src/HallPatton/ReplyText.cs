using System.Collections.Generic;

namespace HallPatton
{
    /// <summary>
    /// Shapes a generated answer into chat. Kept free of Unity and Valheim types so
    /// tests/MatcherTests can compile and exercise this exact file.
    /// </summary>
    internal static class ReplyText
    {
        /// <summary>Hard ceiling on an answer, however long the model went on.</summary>
        public const int DefaultMaxLength = 700;

        /// <summary>
        /// Collapses the answer to a single line, strips the wrapping quotes and asterisks
        /// models like to add, and trims overlong output at a sentence boundary where it can.
        /// </summary>
        public static string Sanitize(string s, int maxLength = DefaultMaxLength)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (maxLength < 40) maxLength = 40;

            s = s.Replace("\r", " ").Replace("\n", " ").Replace('*', ' ').Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");

            if (s.Length >= 2 &&
                ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                s = s.Substring(1, s.Length - 2).Trim();

            if (s.Length > maxLength)
            {
                s = s.Substring(0, maxLength);
                int cut = s.LastIndexOfAny(new[] { '.', '!', '?' });
                s = cut > maxLength / 4 ? s.Substring(0, cut + 1) : s.TrimEnd() + "...";
            }

            return s;
        }

        /// <summary>
        /// Breaks an answer into chat lines, preferring sentence ends.
        ///
        /// A chat line can be any length, but the floating text above his head clips, so a
        /// paragraph arrives as a few lines a second or two apart instead - which also reads
        /// like someone talking rather than pasting.
        /// </summary>
        public static List<string> Split(string s, int lineLength, int maxLines)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return lines;
            if (lineLength < 40) lineLength = 40;
            if (maxLines < 1) maxLines = 1;

            string current = "";

            foreach (string token in Tokens(s, lineLength))
            {
                if (current.Length > 0 && current.Length + 1 + token.Length > lineLength)
                {
                    lines.Add(current);
                    if (lines.Count == maxLines) return lines;
                    current = token;
                }
                else
                {
                    current = current.Length == 0 ? token : current + " " + token;
                }

                // Break at a sentence end, but only once the line is worth sending, so a
                // reply made of short sentences does not go out one sentence at a time.
                char last = token[token.Length - 1];
                if ((last == '.' || last == '!' || last == '?') && current.Length >= lineLength / 2)
                {
                    lines.Add(current);
                    if (lines.Count == maxLines) return lines;
                    current = "";
                }
            }

            if (current.Length > 0) lines.Add(current);
            return lines;
        }

        /// <summary>
        /// The words, with anything longer than a whole line cut down to size - otherwise a
        /// single unbroken run of characters would sail past the line length and the cap on
        /// the number of lines along with it.
        /// </summary>
        private static IEnumerable<string> Tokens(string s, int lineLength)
        {
            foreach (string word in s.Split(' '))
            {
                if (word.Length == 0) continue;

                string rest = word;
                while (rest.Length > lineLength)
                {
                    yield return rest.Substring(0, lineLength);
                    rest = rest.Substring(lineLength);
                }
                yield return rest;
            }
        }
    }
}
