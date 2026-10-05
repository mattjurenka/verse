using System;

namespace Verse
{
    /// <summary>
    /// Recognising the chat command, kept free of Unity and Valheim types so the tests can
    /// exercise it without a game.
    ///
    /// The trigger is a plain word and not "/verse", because a leading slash never leaves the
    /// player's machine: <c>Chat.InputText</c> strips it and runs the rest as a local console
    /// command, so the server never hears it. Anything without a slash is sent as ordinary
    /// chat, which is what the plugin reads.
    /// </summary>
    internal static class CommandText
    {
        /// <summary>
        /// Whether <paramref name="text"/> is addressed to us and, if so, what is left of it
        /// in <paramref name="rest"/>.
        /// </summary>
        public static bool Match(string commandWord, string text, out string rest)
        {
            rest = "";
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(commandWord))
                return false;

            string trigger = commandWord.Trim();
            string line = text.Trim();

            if (line.Length < trigger.Length ||
                !line.Substring(0, trigger.Length).Equals(trigger, StringComparison.OrdinalIgnoreCase))
                return false;

            string tail = line.Substring(trigger.Length);

            // "!verses" is not the command; a letter or digit may not follow the trigger.
            if (tail.Length > 0 && char.IsLetterOrDigit(tail[0])) return false;

            rest = tail.Trim();
            return true;
        }

        /// <summary>
        /// Splits what followed the trigger into a verb and its arguments. Lower-cases the
        /// verb only: a password or a player name has to survive exactly as typed.
        /// </summary>
        public static string[] Words(string rest) =>
            string.IsNullOrWhiteSpace(rest)
                ? new string[0]
                : rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        public static string Verb(string[] words) =>
            words.Length > 0 ? words[0].ToLowerInvariant() : "";

        /// <summary>Whether the player typed the confirmation word anywhere in the command.</summary>
        public static bool Confirmed(string[] words)
        {
            foreach (string word in words)
                if (word.Equals("confirm", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
