using System;
using static HallPatton.Matcher;

namespace HallPatton
{
    /// <summary>
    /// The lines the plugin owns rather than generates: greetings, the help text, and the
    /// handful of answers used when the model is switched off or unreachable.
    ///
    /// Every fact stated here is one of the public ones he has given about his own work, so a
    /// server running without a model key still never puts a made-up date in his mouth. Free
    /// of Unity and Valheim types, so the tests can reach it.
    /// </summary>
    internal static class Dialogue
    {
        private static readonly Random Rng = new Random();

        private static readonly string[] Greetings =
        {
            "Mark Hall-Patton, Clark County Museum. Ask me anything - I'm an omnivore when it comes to history.",
            "Mark Hall-Patton. I run the county museums back in Henderson. What do you want to know?",
            "Well. This is a new site. Mark Hall-Patton - go ahead and ask me something."
        };

        private static readonly string[] Farewells =
        {
            "I'll get back to the museum. We're only open seven days a week, so you can catch us.",
            "Come by the museum sometime - 1830 South Boulder Highway. Bring questions.",
            "Back to Henderson, then. There's a collection that doesn't catalogue itself."
        };

        private static readonly string[] ThinkingLines =
        {
            "Hm. Let me think about that for a moment.",
            "Good question. Give me a second.",
            "Now that's worth answering properly. One moment."
        };

        private static readonly string[] BusyLines =
        {
            "One at a time - I'm still on the last question.",
            "Let me finish the thought I'm on and I'll come to that."
        };

        /// <summary>Asked again too soon. Every question is a paid call, so there is a floor.</summary>
        private static readonly string[] TooSoonLines =
        {
            "Let me finish this thought first.",
            "One at a time - I'm not a card catalogue.",
            "Give me a moment to catch up with you."
        };

        /// <summary>Out of questions for the hour, said as a man running out of voice.</summary>
        private static readonly string[] EnoughLines =
        {
            "That's a good many questions for one hour. Come and find me again shortly.",
            "My voice is going. Let me rest it a while and we'll carry on.",
            "I've been talking all morning. Ask me again in a bit."
        };

        /// <summary>
        /// Used when there is no model to answer with. Deliberately honest: he says he would
        /// have to check, which is what a historian does, rather than guessing.
        /// </summary>
        private static readonly string[] Offline =
        {
            "I'd want to check that before I told you - I don't like being the source of a story that isn't true.",
            "That one I'd have to look up. There's a file on it back at the museum, I'm sure of it.",
            "Ask me that again when I have my notes. I'd rather be right than quick.",
            "Credo Quia Absurdum, as we say in the Clampers - but not about dates. I'd check that one."
        };

        public static string Greeting() => Pick(Greetings);
        public static string Leaving() => Pick(Farewells);
        public static string Thinking() => Pick(ThinkingLines);
        public static string TooBusy() => Pick(BusyLines);
        public static string TooSoon() => Pick(TooSoonLines);
        public static string Enough() => Pick(EnoughLines);

        public static string Help(string commandWord) =>
            $"Type '{commandWord}' and I'll come over. Then just talk to me in chat and I'll " +
            $"answer - southern Nevada history is what I know best. '{commandWord} stay' to " +
            $"leave me where I am, '{commandWord} go' to send me home, '{commandWord} debug' " +
            $"if you want to know what the server is making of all this.";

        /// <summary>
        /// A keyword-matched answer for the no-model case. Only covers things he has said
        /// publicly about his own work; anything else gets an honest "I'd have to check".
        /// </summary>
        public static string Fallback(string question)
        {
            string s = Normalize(question);

            if (Has(s, "who are you", "your name", "what are you", "introduce"))
                return "Mark Hall-Patton, museum administrator for the Clark County Museum " +
                       "System. A museologist - I run museums.";

            if (Has(s, "museum", "museums", "visit", "hours", "open", "where are you from"))
                return "The Clark County Museum is at 1830 South Boulder Highway in Henderson, " +
                       "open daily. Come by - we're only open seven days a week.";

            if (Has(s, "pawn", "pawn stars", "tv", "television", "famous", "celebrity"))
                return "People know the hat from the show. I didn't set out to be on television; " +
                       "I set out to run museums, and the documents came to me either way.";

            if (Has(s, "gass", "avenue", "street name*", "spelled", "spelling"))
                return "Gass Avenue isn't spelled wrong. It's named for Octavio Decatur Gass, a " +
                       "19th-century Las Vegan. That's the sort of thing I like - history at the " +
                       "human level.";

            if (Has(s, "auction", "1905", "founded", "founding", "land"))
                return "The first Las Vegas land auction was the 15th of May, 1905. At the " +
                       "centennial re-creation I sat next to a man who had been at the original " +
                       "one - a few months old at the time. That history is tangible.";

            if (Has(s, "airport", "mccarran", "aviation", "plane*", "flight*"))
                return "I came to Nevada in 1993 to run the Howard W. Cannon Aviation Museum " +
                       "inside McCarran. George Crockett's Alamo Airport became McCarran, and " +
                       "Florence Murphy founded the North Las Vegas Airport before the war - in " +
                       "1947 she was the first woman vice president of a scheduled US airline, " +
                       "with no aviation background at all.";

            if (Has(s, "nevada", "state", "favourite", "favorite", "why nevada"))
                return "It's a beautiful state with really interesting history, and I can be out " +
                       "in open country in very little time. I'm not a city person.";

            if (Has(s, "motto", "clamper*", "clampus", "quote"))
                return "'Credo Quia Absurdum' - I believe it because it is absurd. It's a joke " +
                       "inside E Clampus Vitus, and I use it more than I should.";

            return Pick(Offline);
        }

        private static string Pick(string[] lines) => lines[Rng.Next(lines.Length)];
    }
}
