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

    // --- the verse command ---
    static void V(string typed, bool want, string wantRest = null)
    {
        bool got = Verse.CommandText.Match("!verse", typed, out string rest);
        bool ok = got == want && (!want || wantRest == null || rest == wantRest);
        Check(ok, $"{Show(typed)} -> {(got ? "command" : "not a command")}" +
                  (got && rest.Length > 0 ? $" [{rest}]" : ""));
    }

    // --- the warp command: same matcher, a different trigger word ---
    static void W(string typed, bool want, string wantRest = null)
    {
        bool got = Verse.CommandText.Match("!warp", typed, out string rest);
        bool ok = got == want && (!want || wantRest == null || rest == wantRest);
        Check(ok, $"{Show(typed)} -> {(got ? "command" : "not a command")}" +
                  (got && rest.Length > 0 ? $" [{rest}]" : ""));
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
        Console.WriteLine("--- the verse command ---");
        V("!verse", true, "");
        V("!VERSE  JOIN 7 ", true, "JOIN 7");
        V("!verse join 7 hunter2 confirm", true, "join 7 hunter2 confirm");
        V("!verses", false);
        V("!verse7", false);
        V("hello !verse", false);
        V("", false);
        V(null, false);
        Check(Verse.CommandText.Verb(Verse.CommandText.Words("JOIN 7")) == "join",
              "the verb is lower-cased");
        Check(Verse.CommandText.Words("password Hunter2")[1] == "Hunter2",
              "a password keeps the capitals it was typed with");
        Check(Verse.CommandText.Confirmed(Verse.CommandText.Words("join 7 confirm")),
              "confirm is recognised");
        Check(!Verse.CommandText.Confirmed(Verse.CommandText.Words("join 7")),
              "an unconfirmed join is not treated as confirmed");

        Console.WriteLine();
        Console.WriteLine("--- the warp command: a second trigger word, same matcher ---");
        W("!warp", true, "");
        W("!warp spawn", true, "spawn");
        W("!WARP  BED ", true, "BED");
        W("!warps", false);
        W("!verse spawn", false);

        Console.WriteLine();
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


        Console.WriteLine("\n--- the sleep vote ---");
        // Default 0.5 is read as "strictly more than half", so a tie is not a majority.
        Check(!Verse.SleepRule.Enough(0, 0, 0.5f), "nobody on the server never skips");
        Check(!Verse.SleepRule.Enough(0, 4, 0.5f), "nobody in bed never skips");
        Check(Verse.SleepRule.Enough(1, 1, 0.5f), "one player alone can sleep");
        Check(!Verse.SleepRule.Enough(1, 2, 0.5f), "one of two is a tie, not a majority");
        Check(Verse.SleepRule.Enough(2, 3, 0.5f), "two of three is a majority");
        Check(!Verse.SleepRule.Enough(2, 4, 0.5f), "two of four is half, which is not more than half");
        Check(Verse.SleepRule.Enough(3, 4, 0.5f), "three of four is a majority");
        // 1.0 is vanilla: everybody, and nothing can be strictly more than all of them.
        Check(Verse.SleepRule.Enough(4, 4, 1f), "1.0 skips when everybody is in bed");
        Check(!Verse.SleepRule.Enough(3, 4, 1f), "1.0 refuses when one is still up");
        // 0 lets a single sleeper decide, but still not an empty room.
        Check(Verse.SleepRule.Enough(1, 4, 0f), "0 lets one sleeper skip for everybody");
        Check(!Verse.SleepRule.Enough(0, 4, 0f), "0 still needs somebody actually asleep");
        Check(Verse.SleepRule.Enough(4, 4, 2f), "a fraction above 1 is clamped to everybody");
        Check(Verse.SleepRule.Enough(1, 4, -1f), "a negative fraction is clamped to any sleeper");

        // --- the account key ---
        // A ZSteamSocket reports the bare Steam ID and a ZPlayFabSocket reports the same
        // account as "Steam_<id>", so adding -crossplay to a server with verses already in it
        // would otherwise hand every returning player a brand new empty world.
        const string steam = "76561198033210929";
        Check(Verse.AccountId.Canonical(steam) == steam,
              "a bare Steam id is already canonical");
        Check(Verse.AccountId.Canonical("Steam_" + steam) == steam,
              "a PlayFab socket's \"Steam_<id>\" reduces to the same key");
        Check(Verse.AccountId.Canonical("Steam_" + steam) == Verse.AccountId.Canonical(steam),
              "both sockets agree on one account");
        // Other platforms keep their prefix: it is the namespace that stops an Xbox user id
        // from ever being read as the Steam id of the same digits.
        Check(Verse.AccountId.Canonical("Xbox_" + steam) == "Xbox_" + steam,
              "a non-Steam platform keeps its prefix");
        Check(Verse.AccountId.Canonical("Xbox_" + steam) != Verse.AccountId.Canonical(steam),
              "the same digits on another platform stay a different account");
        // Only a real prefix counts, and only at the front.
        Check(Verse.AccountId.Canonical("steam_" + steam) == "steam_" + steam,
              "the prefix match is case-sensitive, as PlatformUserID writes it");
        Check(Verse.AccountId.Canonical(steam + "_Steam_1") == steam + "_Steam_1",
              "a prefix that is not at the front is left alone");
        Check(Verse.AccountId.Canonical("verse-selftest") == "verse-selftest",
              "the self-test's fake socket name is untouched");
        Check(Verse.AccountId.Canonical("") == "", "empty stays empty");
        Check(Verse.AccountId.Canonical(null) == null, "null stays null");

        Console.WriteLine("\n--- the admin badge ---");
        // The badge is only worth drawing if it cannot be worn by somebody who is not an
        // admin, and a character name is whatever the client said it was: nothing validates it
        // past three characters, and names - unlike chat text - never have their angle
        // brackets taken out on arrival. So every case here is really one question, which is
        // whether `Of` is the only way to end up wearing one.
        const string badge = "<color=red>[ADMIN]</color> ";

        Check(Verse.AdminName.Of("Matt", admin: true) == badge + "Matt",
              "an admin gets the badge in front of their name");
        Check(Verse.AdminName.Of("Matt", admin: false) == "Matt",
              "everybody else keeps the name they chose");

        // The forgery, spelled exactly as the real thing. Its brackets go, so what is left is
        // visibly inert text rather than a red badge.
        const string forged = "<color=red>[ADMIN]</color> Matt";
        Check(!Verse.AdminName.Of(forged, admin: false).Contains("<"),
              "a forged badge loses the markup that would have coloured it");
        Check(!Verse.AdminName.Of(forged, admin: false).Contains("[ADMIN]"),
              "and loses the word as well");
        Check(Verse.AdminName.Of(forged, admin: false) != forged,
              "so a non-admin cannot send a name that renders as one");
        Check(Verse.AdminName.Of("[ADMIN] Matt", admin: false) == "Matt",
              "an uncoloured [ADMIN] is taken out too, since the chat line colours the name");
        Check(Verse.AdminName.Of("[admin] Matt", admin: false) == "Matt",
              "in any case");

        // Scrubbing is for the tag and for markup, not for names that merely resemble it.
        Check(Verse.AdminName.Of("[ADMINISTRATOR]", admin: false) == "[ADMINISTRATOR]",
              "a name that only starts like the tag is left alone");
        Check(Verse.AdminName.Of("Gudrun the Admin", admin: false) == "Gudrun the Admin",
              "and so is the word on its own");

        // Idempotence in both directions. `UpdatePlayerList` rebuilds the list from the
        // undecorated peer field every pass, so this should never be load-bearing - which is
        // the reason to pin it rather than assume it.
        Check(Verse.AdminName.Of(Verse.AdminName.Of("Matt", true), true) == badge + "Matt",
              "running it twice does not stack two badges");
        Check(Verse.AdminName.Of(Verse.AdminName.Of("Matt", true), false) == "Matt",
              "and a demoted admin loses the one they had");

        // Only the brackets go, so the words inside a tag survive as inert text rather than
        // being interpreted - which is the point, and is also why scrubbing cannot be used to
        // make somebody's name disappear.
        Check(Verse.AdminName.Of("<b>Matt</b>", admin: false) == "bMatt/b",
              "markup is defused, not deleted");

        // A name that really was nothing but brackets would otherwise leave an empty orange
        // colon in the chat window; "..." is what vanilla's own GetPlayerName falls back to.
        Check(Verse.AdminName.Of("<<>>", admin: false) == "...",
              "a name made only of brackets still leaves something to address");
        Check(Verse.AdminName.Of("", admin: true) == badge + "...", "as does an empty one");
        Check(Verse.AdminName.Of(null, admin: false) == "...", "and a missing one");

        // What the server logs about an attempt, as opposed to what it shows.
        Check(Verse.AdminName.Claims(forged) && Verse.AdminName.Claims("[admin]"),
              "a name claiming to be an admin's is recognised as such");
        Check(!Verse.AdminName.Claims("Matt") && !Verse.AdminName.Claims(null),
              "an ordinary name is not");

        // --- the arena ---------------------------------------------------------------------
        // The multiplier is sub-linear because co-op power is super-linear; a linear one makes
        // a full party's run *easier* than a solo run. These are the edges of each band, which
        // is where an off-by-one in the ladder would hide.
        static int Count(int wave, int players, string prefab)
        {
            int n = 0;
            foreach (Verse.ArenaGroup g in Verse.ArenaRules.Scale(wave, players))
                if (g.Prefab == prefab) n += g.Count;
            return n;
        }

        // Distinct kinds of add in a wave, counting a starred creature as its own kind: a 1-star
        // wolf leading a pack is a different thing to fight, which is the point of using them.
        static int Kinds(int wave)
        {
            var seen = new System.Collections.Generic.List<string>();
            foreach (Verse.ArenaGroup g in Verse.ArenaRules.Scale(wave, 1))
            {
                if (g.Boss) continue;
                string kind = g.Prefab + "/" + g.Level;
                if (!seen.Contains(kind)) seen.Add(kind);
            }
            return seen.Count;
        }

        static int Starred(int wave)
        {
            int n = 0;
            foreach (Verse.ArenaGroup g in Verse.ArenaRules.Scale(wave, 1))
                if (!g.Boss && g.Level > 1) n += g.Count;
            return n;
        }

        static int Bosses(int wave, int players)
        {
            int n = 0;
            foreach (Verse.ArenaGroup g in Verse.ArenaRules.Scale(wave, players))
                if (g.Boss) n += g.Count;
            return n;
        }

        static bool KitHas(bool cold, string prefab)
        {
            foreach (Verse.KitItem i in Verse.ArenaRules.Kit(cold))
                if (i.Prefab == prefab) return true;
            return false;
        }

        static int KitStack(bool cold, string prefab)
        {
            foreach (Verse.KitItem i in Verse.ArenaRules.Kit(cold))
                if (i.Prefab == prefab) return i.Stack;
            return 0;
        }

        // The three food slots, by name - there is no component to ask out here, since
        // ArenaRules is deliberately Unity-free so these tests can compile it directly.
        static int KitFoods()
        {
            string[] foods = { "MisthareSupreme", "SeekerAspic", "MushroomOmelette",
                               "YggdrasilPorridge", "MagicallyStuffedShroom" };
            int n = 0;
            foreach (Verse.KitItem i in Verse.ArenaRules.Kit(false))
                foreach (string f in foods)
                    if (i.Prefab == f) { n++; break; }
            return n;
        }

        Check(Verse.ArenaRules.Multiplier(1) == 1.0f, "one player faces the baseline wave");
        Check(Verse.ArenaRules.Multiplier(2) == 1.5f, "two players: 1.5x");
        Check(Verse.ArenaRules.Multiplier(3) == 2.3f, "three players: 2.3x");
        Check(Verse.ArenaRules.Multiplier(4) == 2.8f, "four players: 2.8x");
        Check(Verse.ArenaRules.Multiplier(5) == 2.8f, "five is the top of the same band");
        Check(Verse.ArenaRules.Multiplier(6) == 3.4f, "six opens the next band");
        Check(Verse.ArenaRules.Multiplier(8) == 3.4f, "eight is the top of that one");
        Check(Verse.ArenaRules.Multiplier(9) == Verse.ArenaRules.MultiplierCap,
              "nine reaches the cap");
        Check(Verse.ArenaRules.Multiplier(12) == Verse.ArenaRules.MultiplierCap,
              "a full verse of 12 is still the cap");
        Check(Verse.ArenaRules.Multiplier(500) == Verse.ArenaRules.MultiplierCap,
              "a nonsense player count cannot produce a nonsense wave");
        Check(Verse.ArenaRules.Multiplier(0) == 1.0f, "zero players clamps to the baseline");
        Check(Verse.ArenaRules.Multiplier(-3) == 1.0f, "a negative count clamps too");

        // Adds scale and round up; a party of two meets 11 greylings, not 10.2 of them.
        Check(Count(1, 1, "Greyling") == 6, "wave 1 solo is six greylings");
        Check(Count(1, 2, "Greyling") == 9, "wave 1 for two rounds 6x1.5 up to 9");
        Check(Count(1, 12, "Greyling") == 24, "wave 1 at the cap is 24");

        // The capstone escalates in whole minibosses instead of multiplying one, because four
        // copies of Zil & Thungr is not a harder fight, it is a slideshow.
        const int last = Verse.ArenaRules.Waves;
        int soloBosses = Bosses(last, 1);

        // The capstone's named bosses are never multiplied by party size; the party instead
        // earns whole extra ones, which is a different thing and the thing that should scale.
        Check(soloBosses == 6, "solo faces the capstone's six named bosses");
        Check(Bosses(last, 3) == soloBosses, "three players face exactly the same six");
        Check(Bosses(last, 4) == soloBosses + 1, "four players earn a seventh");
        Check(Bosses(last, 6) == soloBosses + 2, "six players earn an eighth");
        Check(Bosses(last, 12) == soloBosses + Verse.ArenaRules.ExtraBosses(12),
              "a full verse faces every extra there is");
        Check(Count(last, 12, "Seeker") > Count(last, 1, "Seeker"), "the capstone's adds do scale");

        // The cap exists so an unmeasured load question is not first measured by a full party.
        Check(Verse.ArenaRules.Size(last, 12) <= Verse.ArenaRules.MaxPerWave,
              $"a full party's capstone is capped at {Verse.ArenaRules.MaxPerWave} creatures " +
              $"(it is {Verse.ArenaRules.Size(last, 12)})");
        Check(Verse.ArenaRules.Size(last, 1) <= Verse.ArenaRules.MaxPerWave,
              "and a solo capstone is under it anyway");
        Check(Verse.ArenaRules.Size(last, 12) > Verse.ArenaRules.Size(last, 1),
              "a full party's capstone is bigger than a solo one");

        // Out of range must be empty rather than throwing: the state machine asks for wave
        // Waves+1 to find out that the run is over.
        Check(Verse.ArenaRules.Scale(0, 1).Length == 0, "there is no wave 0");
        Check(Verse.ArenaRules.Scale(Verse.ArenaRules.Waves + 1, 1).Length == 0,
              "there is no wave past the last");
        Check(Verse.ArenaRules.Scale(Verse.ArenaRules.Waves, 1).Length > 0, "the last wave exists");

        // The food line is the one that would have broken the first play test: an unfed player
        // has 25 health and wave 1 kills them.
        Check(KitHas(false, "MisthareSupreme"), "the kit feeds you");
        Check(KitHas(false, "SeekerAspic"), "with stamina food too");
        Check(KitHas(false, "MushroomOmelette"), "all three slots");

        // A player has exactly three food slots, so the trio is a hard cap rather than a
        // preference - a fourth food would be one nobody can eat.
        Check(KitFoods() == 3, "three foods, because there are three slots");

        // Eitr comes from food and from nothing else, so a staff in the kit without an eitr
        // food in it is an ornament. The omelette is the one that makes the staff work, and
        // this is the assertion that stops a later tidy-up quietly breaking it.
        Check(!KitHas(false, "StaffFireball") || KitHas(false, "MushroomOmelette"),
              "the staff has an eitr food to run on");

        Check(KitHas(false, "SwordMistwalker") && KitHas(false, "SledgeDemolisher") &&
              KitHas(false, "StaffFireball"),
              "three damage types: slash, blunt for skeletons, fire for blobs");
        Check(KitHas(false, "ShieldCarapace"), "and something to block with");

        // "A full stack" is the ask; Loadout.Build clamps it down to the prefab's real
        // ceiling at hand-out time, so what is asserted here is the request, not the result.
        Check(KitStack(false, "MeadHealthMajor") == 10, "a full stack of healing potions");
        Check(KitStack(false, "MeadStaminaLingering") == 10, "a full stack of stamina potions");

        Check(!KitHas(false, "MeadFrostResist"), "no frost mead in a warm arena");
        Check(KitHas(true, "MeadFrostResist"), "frost mead when the site freezes");

        Check(Verse.ArenaRules.Clock(0f) == "0:00", "the clock starts at 0:00");
        Check(Verse.ArenaRules.Clock(65f) == "1:05", "65s reads as 1:05");
        Check(Verse.ArenaRules.Clock(600f) == "10:00", "ten minutes reads as 10:00");
        Check(Verse.ArenaRules.Clock(-5f) == "0:00", "a negative clock is not printed");

        Check(Verse.ArenaRules.Result("Bjorn", 10, true, 540f, 1)
                  .Contains($"cleared all {Verse.ArenaRules.Waves} waves"), "clearing it says so");
        Check(Verse.ArenaRules.Result("Bjorn", 7, false, 300f, 1)
                  .Contains($"fell on wave 7 of {Verse.ArenaRules.Waves}"), "falling short says where");
        Check(!Verse.ArenaRules.Result("Bjorn", 7, false, 300f, 1).Contains("handed"),
              "a solo run is not announced as n-handed");
        Check(Verse.ArenaRules.Result("Bjorn", 7, false, 300f, 4).Contains("4-handed"),
              "a party run is");
        // --- the Mountain block, waves 11-15 -------------------------------------------------
        // It was five wolf waves: 22, 12, 12, 14, 6, sixty-six wolves, and a player who died on
        // wave 14 reported it as back-to-back-to-back. What follows is what "varied" was taken to
        // mean, pinned so a later tuning pass cannot quietly walk it back. The health totals are
        // in the table's own comments; these are the shape.
        int mountainWolves = 0, mountainKinds = 0, mountainStars = 0;
        for (int wave = 11; wave <= 15; wave++)
        {
            mountainWolves += Count(wave, 1, "Wolf");
            mountainStars += Starred(wave);
            if (Kinds(wave) > mountainKinds) mountainKinds = Kinds(wave);

            Check(Kinds(wave) >= 3,
                  $"mountain wave {wave} has at least three kinds of creature in it (it has {Kinds(wave)})");
        }

        Check(mountainWolves <= 40,
              $"the mountain block is no longer mostly wolves ({mountainWolves} across five waves, " +
              "down from 66)");
        Check(mountainStars > 0, "and some of the wolves in it are starred");
        Check(Count(11, 1, "Fenring_Cultist") + Count(12, 1, "Fenring_Cultist") +
              Count(13, 1, "Hatchling") + Count(14, 1, "Fenring_Cultist") > 0,
              "the block brings in creatures the first five waves of it never had");

        // A wave that is one species in bulk is the thing being avoided, and "a shit ton of
        // wolves" was the player's phrase for it. Stated as a share rather than a count, because
        // the multiplier changes counts and does not change shares. Scoped to this block: wave 1
        // is six greylings and is meant to be.
        for (int wave = 11; wave <= 15; wave++)
            Check(Count(wave, 1, "Wolf") * 2 <= Verse.ArenaRules.Size(wave, 1),
                  $"wolves are at most half of mountain wave {wave} " +
                  $"({Count(wave, 1, "Wolf")} of {Verse.ArenaRules.Size(wave, 1)} bodies)");

        // Wave 10 keeps a single miniboss: the capstone extras only land on the last wave.
        Check(Bosses(10, 12) == 1, "a mid-gauntlet miniboss wave gains no extras");
        Check(Verse.ArenaRules.Scale(18, 1).Length > 0, "the gauntlet waves all exist");
        Check(Verse.ArenaRules.Describe(5, 1).Contains("Troll"),
              "the wave announcement names the creatures");

        Console.WriteLine(fail == 0 ? "\nALL PASS" : $"\n{fail} FAILED");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
