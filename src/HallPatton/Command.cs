using System;

namespace HallPatton
{
    /// <summary>What a player asked the plugin to do.</summary>
    internal enum CommandKind
    {
        /// <summary>Not addressed to him at all.</summary>
        None,
        /// <summary>Come here - which also means "start following again".</summary>
        Summon,
        Dismiss,
        Help,
        Stay,
        /// <summary>Report plugin state in chat, where whoever needs it is standing.</summary>
        Debug,
        /// <summary>Show what the lookup finds for some text, for tuning it.</summary>
        Lookup,
        /// <summary>Addressed to him by name, with a question attached.</summary>
        Ask
    }

    /// <summary>
    /// Parses the one chat command this plugin has. Free of Unity and Valheim types so the
    /// tests can exercise it.
    ///
    /// The trigger is a plain word rather than "/mark", because a leading slash never leaves
    /// the player's machine: <c>Chat.InputText</c> strips it and runs the rest as a local
    /// console command, so the server never hears about it. Anything without a slash is sent
    /// as ordinary chat, which is what this reads.
    /// </summary>
    internal static class Command
    {
        /// <summary>
        /// Matches <paramref name="text"/> against the trigger word. Returns what was asked
        /// for and, for <see cref="CommandKind.Ask"/>, the question in <paramref name="rest"/>.
        /// </summary>
        public static CommandKind Parse(string commandWord, string text, out string rest)
        {
            rest = "";
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(commandWord))
                return CommandKind.None;

            string trigger = commandWord.Trim();
            string line = text.Trim();

            if (line.Length < trigger.Length ||
                !line.Substring(0, trigger.Length).Equals(trigger, StringComparison.OrdinalIgnoreCase))
                return CommandKind.None;

            string tail = line.Substring(trigger.Length);

            // "!marks" and "!markdown" are not the command; a letter or digit may not follow it.
            if (tail.Length > 0 && char.IsLetterOrDigit(tail[0])) return CommandKind.None;

            tail = tail.Trim();
            if (tail.Length == 0) return CommandKind.Summon;

            string padded = Matcher.Normalize(tail);

            if (Matcher.Has(padded, "help", "commands", "what can you do")) return CommandKind.Help;
            if (Matcher.Has(padded, "debug", "status", "diag", "diagnostics")) return CommandKind.Debug;

            // "lookup <thing>" has to be checked on the first word rather than by keyword,
            // because everything after it is the thing being looked up.
            if (tail.Length > 6 && tail.Substring(0, 6).Equals("lookup", StringComparison.OrdinalIgnoreCase))
            {
                rest = tail.Substring(6).Trim();
                return CommandKind.Lookup;
            }
            if (Matcher.Has(padded, "go", "away", "bye", "goodbye", "dismiss", "leave", "home"))
                return CommandKind.Dismiss;
            if (Matcher.Has(padded, "stay", "wait", "stop")) return CommandKind.Stay;
            if (Matcher.Has(padded, "come", "here", "follow", "heel", "with me"))
                return CommandKind.Summon;

            // "!mark when did the dam open?" - a question asked by name, from any distance.
            rest = tail;
            return CommandKind.Ask;
        }
    }
}
