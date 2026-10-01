using System;
using System.Collections.Generic;
using HallPatton;

class P
{
    static int fail;

    static void Check(bool ok, string what)
    {
        if (!ok) fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    // --- the chat command ---
    static void C(string typed, CommandKind want, string wantRest = null)
    {
        CommandKind got = Command.Parse("!mark", typed, out string rest);
        bool ok = got == want && (wantRest == null || rest == wantRest);
        Check(ok, $"\"{typed}\" -> {got}{(rest.Length > 0 ? $" [{rest}]" : "")}" +
                  (ok ? "" : $"  (wanted {want}{(wantRest != null ? $" [{wantRest}]" : "")})"));
    }

    // --- sanitising ---
    static void S(string input, string want)
    {
        string got = ReplyText.Sanitize(input, 180);
        Check(got == want, $"sanitize({Show(input)}) -> {Show(got)}" +
                           (got == want ? "" : $"  (wanted {Show(want)})"));
    }

    static string Show(string s) => s == null ? "null" : "\"" + s.Replace("\n", "\\n") + "\"";

    static void Main()
    {
        Console.WriteLine("--- keyword matching: regressions that used to misfire ---");
        string s1 = Matcher.Normalize("i am afraid of death");
        Check(!Matcher.Has(s1, "eat"), "\"death\" does not contain the word \"eat\"");
        Check(Matcher.Has(s1, "death"), "\"death\" is found as a whole word");
        string s2 = Matcher.Normalize("they attacked me");
        Check(!Matcher.Has(s2, "hey"), "\"they\" does not contain the word \"hey\"");
        Check(Matcher.Has(Matcher.Normalize("my apologies"), "apolog*"), "prefix needles match");
        Check(Matcher.Has(Matcher.Normalize("hello there"), "hello"), "first word matches");
        Check(Matcher.Has(Matcher.Normalize("say hello"), "hello"), "last word matches");
        Check(!Matcher.Has(Matcher.Normalize(""), "hello"), "empty input matches nothing");
        Check(!Matcher.Has(Matcher.Normalize(null), "hello"), "null input matches nothing");

        Console.WriteLine("\n--- the chat command ---");
        C("!mark", CommandKind.Summon);
        C("  !mark  ", CommandKind.Summon);
        C("!MARK", CommandKind.Summon);
        C("!mark come here", CommandKind.Summon);
        C("!mark follow me", CommandKind.Summon);
        C("!mark stay", CommandKind.Stay);
        C("!mark wait there", CommandKind.Stay);
        C("!mark go home", CommandKind.Dismiss);
        C("!mark bye", CommandKind.Dismiss);
        C("!mark dismiss", CommandKind.Dismiss);
        C("!mark help", CommandKind.Help);
        C("!mark debug", CommandKind.Debug);
        C("!mark lookup iron sword", CommandKind.Lookup, "iron sword");
        C("!mark lookup  troll ", CommandKind.Lookup, "troll");
        C("!mark status", CommandKind.Debug);
        C("!mark what can you do", CommandKind.Help);
        C("!mark when was the first land auction?", CommandKind.Ask,
            "when was the first land auction?");
        C("!mark: tell me about Gass Avenue", CommandKind.Ask, ": tell me about Gass Avenue");
        // Not the command: a word that merely starts with it, or someone else's name.
        C("!marks the spot", CommandKind.None);
        C("!markdown", CommandKind.None);
        C("mark, are you there", CommandKind.None);
        C("hello everyone", CommandKind.None);
        C("", CommandKind.None);
        C(null, CommandKind.None);

        Console.WriteLine("\n--- answer sanitising ---");
        S("Mark Hall-Patton, Clark County Museum.", "Mark Hall-Patton, Clark County Museum.");
        S("  Too   many   spaces.  ", "Too many spaces.");
        S("Line one\nline two", "Line one line two");
        S("\"A quoted answer.\"", "A quoted answer.");
        S("'Single quoted.'", "Single quoted.");
        S("*adjusts hat* Go ahead.", "adjusts hat Go ahead.");
        S("", "");
        S(null, "");
        // Overlong output trims back to the last sentence end.
        S(new string('a', 100) + ". " + new string('b', 200), new string('a', 100) + ".");
        // Overlong with no sentence end at all gets an ellipsis.
        S(new string('c', 300), new string('c', 180) + "...");

        Console.WriteLine("\n--- breaking an answer into chat lines ---");
        List<string> lines = ReplyText.Split(
            "The first Las Vegas land auction was the 15th of May, 1905. " +
            "At the centennial re-creation I sat next to a man who had been at the original " +
            "one, a few months old at the time. That history is tangible.", 80, 5);
        Check(lines.Count > 1, $"a long answer becomes several lines ({lines.Count})");
        foreach (string line in lines) Check(line.Length <= 80, $"line fits: \"{line}\"");
        Check(lines[0].EndsWith("1905."), "the first break lands on a sentence end");
        Check(string.Join(" ", lines.ToArray()).Contains("tangible"), "nothing is lost");

        Check(ReplyText.Split("Short one.", 160, 5).Count == 1, "a short answer stays one line");
        Check(ReplyText.Split("", 160, 5).Count == 0, "an empty answer sends nothing");
        Check(ReplyText.Split(new string('d', 2000), 100, 3).Count == 3, "MaxLines is a hard cap");

        Console.WriteLine("\n--- the persona prompt ---");
        string prompt = Persona.SystemPrompt(90, "Extra config text.");
        Check(prompt.Contains("Mark Hall-Patton"), "names him");
        Check(prompt.Contains("Clark County Museum"), "gives him his job");
        Check(prompt.Contains("at most 90 words"), "passes the length ceiling through");
        Check(prompt.EndsWith("Extra config text."), "appends the config addition");
        Check(prompt.Contains("real, living person"), "tells him to be honest about who he is");

        Console.WriteLine("\n--- built-in answers ---");
        Check(Dialogue.Fallback("who are you?").Contains("Clark County"), "introduces himself");
        Check(Dialogue.Fallback("why is Gass Avenue spelled like that?").Contains("Octavio"),
            "knows Gass Avenue without a model");
        Check(Dialogue.Fallback("what is the airspeed of a lox?").Length > 0,
            "anything else still gets an answer");
        Check(Dialogue.Help("!mark").Contains("!mark stay"), "help text uses the configured word");

        Console.WriteLine(fail == 0 ? "\nALL PASS" : $"\n{fail} FAILED");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
