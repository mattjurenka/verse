using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// Server-side BepInEx plugin: puts Mark Hall-Patton, museum administrator for the Clark
    /// County Museum System, into a Valheim world and lets anyone ask him about southern
    /// Nevada history in chat.
    ///
    /// This is installed on the server only. Players connect with an unmodified game: Mark is
    /// a ZDO the server owns (see <see cref="Historian"/>), so every client builds him out of
    /// prefabs it already ships with, and he speaks through vanilla chat RPCs.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.matthew.hallpatton";
        public const string Name = "Mark Hall-Patton";
        public const string Version = "2.0.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // --- General ---
        internal static ConfigEntry<string> CommandWord;
        internal static ConfigEntry<string> DisplayName;
        internal static ConfigEntry<bool> Follow;
        internal static ConfigEntry<float> FollowDistance;
        internal static ConfigEntry<float> WalkSpeed;
        internal static ConfigEntry<float> LeashDistance;
        internal static ConfigEntry<float> IdleDismissMinutes;

        // --- Talking ---
        internal static ConfigEntry<float> Earshot;
        internal static ConfigEntry<float> ThinkSeconds;
        internal static ConfigEntry<bool> Acknowledge;
        internal static ConfigEntry<int> HistoryTurns;
        internal static ConfigEntry<int> MaxWords;
        internal static ConfigEntry<int> LineLength;
        internal static ConfigEntry<int> MaxLines;
        internal static ConfigEntry<float> LineGapSeconds;

        // --- Limits ---
        internal static ConfigEntry<float> CooldownSeconds;
        internal static ConfigEntry<int> PerPlayerHourly;
        internal static ConfigEntry<int> GlobalDaily;

        // --- Look ---
        internal static ConfigEntry<int> LookModel;
        internal static ConfigEntry<string> LookSkinColor;
        internal static ConfigEntry<string> LookHairColor;
        internal static ConfigEntry<string> LookHair;
        internal static ConfigEntry<string> LookBeard;
        internal static ConfigEntry<string> LookHelmet;
        internal static ConfigEntry<string> LookChest;
        internal static ConfigEntry<string> LookLegs;
        internal static ConfigEntry<string> LookShoulder;
        internal static ConfigEntry<string> LookRightHand;
        internal static ConfigEntry<string> LookLeftHand;

        // --- Model (Meta Muse or any OpenAI-compatible endpoint) ---
        internal static ConfigEntry<bool> MuseEnabled;
        internal static ConfigEntry<string> MuseEndpoint;
        internal static ConfigEntry<string> MuseApiKey;
        internal static ConfigEntry<string> MuseModel;
        internal static ConfigEntry<string> MuseReasoningEffort;
        internal static ConfigEntry<string> MusePersona;
        internal static ConfigEntry<int> MuseMaxTokens;
        internal static ConfigEntry<float> MuseTemperature;
        internal static ConfigEntry<int> MuseTimeoutSeconds;

        // --- Knowledge ---
        internal static ConfigEntry<bool> KnowledgeEnabled;
        internal static ConfigEntry<int> KnowledgeMax;
        internal static ConfigEntry<bool> KnowledgeDump;

        // --- Diagnostics ---
        internal static ConfigEntry<bool> Verbose;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            CommandWord = Config.Bind("General", "CommandWord", "!mark",
                "What players type in chat to summon him. A leading '/' cannot be used: the " +
                "vanilla client eats slash commands locally and never sends them to the server.");
            DisplayName = Config.Bind("General", "DisplayName", "Mark Hall-Patton",
                "The name on his chat lines, over his head, and in the player list.");
            Follow = Config.Bind("General", "Follow", true,
                "He walks after whoever summoned him. Off means he stands where he was put.");
            FollowDistance = Config.Bind("General", "FollowDistance", 3f,
                "How far behind you he trails, in metres.");
            WalkSpeed = Config.Bind("General", "WalkSpeed", 3.2f,
                "Metres per second. A little above a player's walk so he can catch up; he " +
                "retraces your own path rather than walking his own line, so this is the " +
                "speed along that path.");
            LeashDistance = Config.Bind("General", "LeashDistance", 40f,
                "If he falls further behind than this (you sprinted, swam or teleported) he " +
                "gives up and reappears next to you.");
            IdleDismissMinutes = Config.Bind("General", "IdleDismissMinutes", 0f,
                "Send him home after this long without being spoken to. 0 keeps him until " +
                "he is dismissed or the player who summoned him disconnects.");

            Earshot = Config.Bind("Talking", "Earshot", 15f,
                "How close you have to be for him to treat what you type as a question to " +
                "him. Matches vanilla's own normal-chat range.");
            ThinkSeconds = Config.Bind("Talking", "ThinkSeconds", 2.5f,
                "He says nothing at all for this long after a question, however fast the answer " +
                "comes back. Somebody who replies the instant you stop typing does not read as " +
                "somebody thinking about it. 0 answers as soon as he can.");
            Acknowledge = Config.Bind("Talking", "Acknowledge", true,
                "If the answer is still not ready once ThinkSeconds is up, he says a short " +
                "'let me think' line so you know he heard you. A quick answer never gets " +
                "announced this way - it simply arrives.");
            HistoryTurns = Config.Bind("Talking", "HistoryTurns", 8,
                "Remembered exchanges per player, so he can follow a conversation. 0 disables memory.");
            MaxWords = Config.Bind("Talking", "MaxWords", 90,
                "Length ceiling on an answer, in words, as asked of the model. Long enough " +
                "for a proper answer with names and dates in it.");
            LineLength = Config.Bind("Talking", "LineLength", 160,
                "An answer is broken into chat lines no longer than this, at sentence ends " +
                "where possible, because the floating speech bubble clips long text.");
            MaxLines = Config.Bind("Talking", "MaxLines", 5,
                "Most chat lines one answer may use. Anything past this is dropped.");
            LineGapSeconds = Config.Bind("Talking", "LineGapSeconds", 2.5f,
                "Pause between those lines, so he reads as talking rather than pasting.");

            CooldownSeconds = Config.Bind("Limits", "CooldownSeconds", 8f,
                "Shortest gap between one player's questions. Every question is a paid API " +
                "call, so this is the difference between a conversation and somebody holding " +
                "down Enter.");
            PerPlayerHourly = Config.Bind("Limits", "PerPlayerHourly", 40,
                "Most questions one player may ask in an hour. 0 removes the limit.");
            GlobalDaily = Config.Bind("Limits", "GlobalDaily", 600,
                "Most model calls the server will make in a day, across everyone. Past it he " +
                "still answers, from the built-in lines, so the server degrades rather than " +
                "going quiet. 0 removes the limit.");

            // Vanilla prefab names. Empty means nothing in that slot. Beard3 is the one the
            // character creator calls "Short"; Hair5 is "Short"; HelmetStrawHat is the closest
            // thing the game has to the wide-brimmed hat he is never seen without.
            LookModel = Config.Bind("Look", "Model", 0,
                "0 or 1 - the two vanilla body models.");
            LookSkinColor = Config.Bind("Look", "SkinColor", "1.0, 0.88, 0.78",
                "R, G, B multiplier on the skin.");
            LookHairColor = Config.Bind("Look", "HairColor", "0.88, 0.88, 0.86",
                "R, G, B on hair and beard. Near-white for a man in his seventies.");
            LookHair = Config.Bind("Look", "Hair", "Hair5",
                "Hair prefab: Hair1-Hair38, or HairNone.");
            LookBeard = Config.Bind("Look", "Beard", "Beard3",
                "Beard prefab: Beard1-Beard26, or BeardNone. Beard3 is the short one.");
            LookHelmet = Config.Bind("Look", "Helmet", "HelmetStrawHat",
                "Head slot. HelmetStrawHat is the wide brim; blank it for bare-headed.");
            LookChest = Config.Bind("Look", "Chest", "ArmorLeatherChest", "Chest slot.");
            LookLegs = Config.Bind("Look", "Legs", "ArmorLeatherLegs", "Legs slot.");
            LookShoulder = Config.Bind("Look", "Shoulder", "",
                "Shoulder slot - this is where capes go, e.g. CapeLinen.");
            LookRightHand = Config.Bind("Look", "RightHand", "",
                "Right hand. Blank: a museum administrator carries no weapon.");
            LookLeftHand = Config.Bind("Look", "LeftHand", "", "Left hand.");

            MuseEnabled = Config.Bind("Muse", "Enabled", true,
                "Generate answers with Meta's Muse instead of the handful of built-in lines. " +
                "Falls back to those lines on any error.");
            MuseEndpoint = Config.Bind("Muse", "Endpoint", "",
                "OpenAI-compatible chat-completions URL. Blank uses Meta's " +
                "https://api.meta.ai/v1/chat/completions. Point it elsewhere to use a different " +
                "provider or a local server; the request shape is the same.");
            MuseApiKey = Config.Bind("Muse", "ApiKey", "",
                "Meta Model API key. Blank falls back to the MODEL_API_KEY environment " +
                "variable. With neither set, Muse is off and the built-in lines answer.");
            MuseModel = Config.Bind("Muse", "Model", "muse-spark-1.1",
                "Model ID. muse-spark-1.1 answers in 1-2s; muse-spark-1.3 is 10-17s, " +
                "which is too slow for a conversation in chat.");
            MuseReasoningEffort = Config.Bind("Muse", "ReasoningEffort", "low",
                "MUST stay 'low'. These are reasoning models and with no effort set they " +
                "spend the entire MaxTokens budget thinking and return nothing at all. " +
                "'none' is rejected by the API; 'minimal' and 'medium' think more, not less.");
            MusePersona = Config.Bind("Muse", "ExtraPersona", "",
                "Appended to the personality prompt.");
            MuseMaxTokens = Config.Bind("Muse", "MaxTokens", 1000,
                "Budget for reasoning PLUS the answer, not a length limit on the answer. " +
                "Roughly 300 of these go on hidden reasoning. Answer length is set by " +
                "Talking.MaxWords instead.");
            MuseTemperature = Config.Bind("Muse", "Temperature", 0.7f,
                "Lower than you would give a joke character: he is answering history questions.");
            MuseTimeoutSeconds = Config.Bind("Muse", "TimeoutSeconds", 20,
                "Give up and use a built-in line after this long.");

            KnowledgeEnabled = Config.Bind("Knowledge", "Enabled", true,
                "Look things up in the game's own data before answering - every item, recipe, " +
                "creature and building piece, with the real numbers for this version. Item " +
                "numbers are exactly what a language model invents, so this is what stops him " +
                "being confidently wrong about the game.");
            KnowledgeMax = Config.Bind("Knowledge", "MaxEntries", 6,
                "How many matching entries ride along with a question. More is more reference " +
                "and more tokens.");
            KnowledgeDump = Config.Bind("Knowledge", "DumpIndex", true,
                "Write everything he can look up to BepInEx/config/HallPatton.knowledge.txt on " +
                "startup, so you can read and grep it.");

            Verbose = Config.Bind("Diagnostics", "Verbose", true,
                "Log every chat message the server overhears, what was decided about it, who " +
                "each reply went to, and where he is every few seconds. On by default because " +
                "the vanilla paths this plugin feeds fail silently; turn it off once a server " +
                "is known good.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{Name} {Version} loaded. Players summon him by typing " +
                        $"'{CommandWord.Value}' in chat; nothing is needed on their end.");
        }

        private void OnDestroy()
        {
            Historian.Dismiss();
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (ZNet.instance == null || ZDOMan.instance == null) return;

            float dt = Time.deltaTime;
            Diagnostics.Startup(_harmony);

            if (!ZNet.instance.IsServer()) return;

            Historian.Tick(dt);
            Conversation.Tick();
            Knowledge.EnsureBuilt();
            Diagnostics.PeerScan(dt);
            Diagnostics.State(dt);
        }

        /// <summary>Parses an "r, g, b" config string; falls back on anything unparseable.</summary>
        internal static Vector3 ParseColor(string value, Vector3 fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string[] parts = value.Split(',');
            if (parts.Length < 3) return fallback;

            var rgb = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out rgb[i]))
                    return fallback;

            return new Vector3(rgb[0], rgb[1], rgb[2]);
        }
    }
}
