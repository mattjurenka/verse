using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// Server-side BepInEx plugin: gives every player their own private world - a verse -
    /// inside one Valheim server process, and keeps the verses from being able to see or
    /// reach each other.
    ///
    /// This is installed on the server only. Players connect with an unmodified game. The
    /// isolation is not something clients cooperate with: a dedicated server decides per peer
    /// which networked objects that peer is told about, and a vanilla client cannot act on
    /// something it was never sent. See <see cref="Isolation"/>, and docs/verse-design.md for
    /// the architecture and the measurements behind it.
    ///
    /// STATUS: isolation, copy-on-write, the chat commands and per-verse arrival are built;
    /// per-verse world generation is not, so every verse is the same map at the same
    /// coordinates. Send scheduling used to live here too and is now the `Firehose` plugin's
    /// job - loading both is fine, and Firehose stands down if anything else patches the
    /// scheduler. docs/verse-design.md lists exactly what is and is not done.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class VersePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.matthew.verse";
        public const string Name = "Verse";
        public const string Version = "0.1.0";

        internal static VersePlugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Isolate;
        internal static ConfigEntry<bool> FilterPlayerList;
        internal static ConfigEntry<bool> DivergeOnChange;
        internal static ConfigEntry<bool> ClaimOnInteraction;
        internal static ConfigEntry<float> SleepFraction;
        internal static ConfigEntry<bool> TraceCharacters;
        internal static ConfigEntry<bool> SelfTestEnabled;   // not "SelfTest": that is the class
        internal static ConfigEntry<bool> PerVerseKeys;
        internal static ConfigEntry<bool> EnsurePlayerEvents;
        internal static ConfigEntry<bool> GlobalChat;
        internal static ConfigEntry<bool> ScatterNewVerses;
        internal static ConfigEntry<float> ScatterRadius;
        internal static ConfigEntry<int> MigrateLegacyInto;
        internal static ConfigEntry<bool> MigrateDryRun;
        internal static ConfigEntry<int> SweepInto;
        internal static ConfigEntry<bool> SweepDryRun;
        internal static ConfigEntry<bool> SweepTerrain;
        internal static ConfigEntry<bool> SweepCreatures;
        internal static ConfigEntry<float> SweepPiecesNear;
        internal static ConfigEntry<int> MaxPartySize;
        internal static ConfigEntry<int> MaxPlayers;
        internal static ConfigEntry<bool> AcceptAnyPassword;
        internal static ConfigEntry<bool> NoPassword;
        internal static ConfigEntry<bool> Metrics10s;
        internal static ConfigEntry<string> CommandWord;
        internal static ConfigEntry<string> WarpCommandWord;
        internal static ConfigEntry<string> SpeakerName;
        internal static ConfigEntry<bool> IntroduceNewPlayers;
        internal static ConfigEntry<bool> ArenaEnabled;
        internal static ConfigEntry<string> ArenaCommandWord;
        internal static ConfigEntry<string> ArenaBiome;
        internal static ConfigEntry<float> ArenaMinDistance;
        internal static ConfigEntry<float> ArenaRadius;
        internal static ConfigEntry<float> ArenaClearRadius;
        internal static ConfigEntry<string> ArenaWallPrefab;
        internal static ConfigEntry<string> ArenaSitePoint;
        internal static ConfigEntry<string> ArenaChestPrefab;
        internal static ConfigEntry<string> ArenaDeckPrefab;
        internal static ConfigEntry<bool> ArenaLevelGround;
        internal static ConfigEntry<bool> ArenaBoardwalk;
        internal static ConfigEntry<string> ArenaBoardPrefab;
        internal static ConfigEntry<bool> ArenaGallery;
        internal static ConfigEntry<string> ArenaRailPrefab;
        internal static ConfigEntry<string> ArenaLadderPrefab;
        internal static ConfigEntry<bool> ArenaUnbreakable;
        internal static ConfigEntry<bool> ArenaEnforceKit;
        internal static ConfigEntry<bool> ArenaTrace;
        internal static ConfigEntry<bool> ArenaRested;
        internal static ConfigEntry<string> ArenaRestedEffect;

        private Harmony _harmony;
        private float _since;
        private bool _selfTested;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Isolate = Config.Bind("Verse", "Isolate", true,
                "Hide each verse's objects, chat and players from the others. Off leaves one " +
                "shared world and is only useful for comparing behaviour against vanilla.");
            MaxPartySize = Config.Bind("Verse", "MaxPartySize", 12,
                "Most players in one verse, the leader included.");
            DivergeOnChange = Config.Bind("Verse", "DivergeOnChange", true,
                "When a verse changes a shared world object - picking a berry, part-mining " +
                "ore, looting a world chest - give that verse the changed object and leave a " +
                "pristine one for everybody else. Off lets those changes leak between " +
                "verses, which is what the plugin did before this existed.");
            FilterPlayerList = Config.Bind("Verse", "FilterPlayerList", true,
                "Send each verse its own player list. Off lets everyone see every connected " +
                "player's name and position, which is a visible isolation leak.");
            ClaimOnInteraction = Config.Bind("Verse", "ClaimOnInteraction", true,
                "When a player reaches for a shared world object that another verse happens " +
                "to own - swinging at a dungeon creature, using a boss altar, opening a crypt " +
                "door - hand that verse its own copy instead of dropping the message. Much of " +
                "Valheim works by sending an RPC to whoever owns an object, and every " +
                "creature a dungeon or location placed is server-created and therefore " +
                "shared, so without this a shared draugr is invulnerable to everyone except " +
                "whichever verse was standing nearest when it loaded. Off restores that, " +
                "which is only useful for comparison.");
            SleepFraction = Config.Bind("Verse", "SleepFraction", 0.5f,
                "What share of the players on the server must be in bed before the night is " +
                "skipped, read as 'strictly more than'. Vanilla requires all of them, which " +
                "with verses means a party can never sleep while anyone in another verse is " +
                "awake - the night skip is one world clock, so it cannot be made per-verse, " +
                "and a majority vote is the fairest thing left. 1.0 restores vanilla; 0 lets " +
                "a single sleeper skip the night for everybody.");

            MaxPlayers = Config.Bind("Verse", "MaxPlayers", 10,
                "How many players may be connected at once. Vanilla hard-codes ten in " +
                "ZNet.RPC_PeerInfo - it is a constant, not a setting - and ten is roughly " +
                "where vanilla's send scheduler gives out, which is why the number is there " +
                "at all. With Firehose servicing every peer on a fixed tick the round period " +
                "no longer grows with the player count, so the number can move. The other " +
                "per-peer costs are real though: ZoneSystem.CreateGhostZones runs for every " +
                "peer at 10 Hz, and memory holds every verse's explored area at once. Raise " +
                "it and watch the busiest core rather than the average - Valheim's simulation " +
                "is largely one thread, so a pegged core reads as ~50% on two vCPUs. " +
                "Clamped to 1-200.");

            AcceptAnyPassword = Config.Bind("Verse", "AcceptAnyPassword", false,
                "Accept whatever a player types into the password dialog. Off by default " +
                "because this undoes the only lock a Valheim server has, and nobody should " +
                "get that by upgrading a plugin. Everything else about the password stays " +
                "vanilla on purpose: the browser still shows the padlock and the client is " +
                "still asked, so the entry looks like every other listed server - only the " +
                "comparison in ZNet.RPC_PeerInfo is rewritten. See PublicServer.cs. A " +
                "password is not optional for a listed server either way: '-public 1' with " +
                "no valid '-password' calls Application.Quit() instead of running, and " +
                "valid means five or more characters that are not a substring of the world " +
                "name or the seed name. Note that the client ignores an empty submission, so " +
                "players must type something - it just does not matter what.");

            NoPassword = Config.Bind("Verse", "NoPassword", false,
                "Remove the password altogether - no dialog on connect and no padlock in the " +
                "browser. Blanks ZNet.m_serverPassword in a postfix on ZNet.SetServer, the " +
                "one place it is ever assigned, so every read of it afterwards agrees: the " +
                "browser advertises no lock, the handshake does not raise the dialog, and a " +
                "client that was never asked sends \"\" up, which then matches. Takes " +
                "precedence over AcceptAnyPassword, which from then on only matters as the " +
                "fallback if this is on and the blanking fails. This does NOT let you drop " +
                "'-password' from the command line: FejdStartup.ParseServerArguments " +
                "validates it before SetServer runs and calls Application.Quit() on a listed " +
                "server without one, so the argument stays real - it just stops reaching " +
                "players. See PublicServer.cs.");

            CommandWord = Config.Bind("Verse", "CommandWord", "!verse",
                "What players type in chat to manage their verse. A leading slash cannot be " +
                "used: the vanilla client runs slash commands locally and never sends them on.");
            WarpCommandWord = Config.Bind("Verse", "WarpCommandWord", "!warp",
                "What players type in chat to teleport within their own verse - '<word> spawn' " +
                "for where arrivals land, '<word> bed' for a bed they have claimed. Same " +
                "leading-slash restriction as CommandWord.");
            SpeakerName = Config.Bind("Verse", "SpeakerName", "Verse",
                "The name the plugin answers under. It must appear in the player list for a " +
                "lone player's chat to reach the server at all, so players will see it there.");

            IntroduceNewPlayers = Config.Bind("Verse", "IntroduceNewPlayers", true,
                "Explain the server in chat the first time an account ever arrives, once. " +
                "Not cosmetic: a new player lands in an empty private world with nothing on " +
                "screen to say that it is private, that it is theirs, that they can invite " +
                "anybody, or that the arena is a command away - the plugin is server-side, so " +
                "chat is the only channel a vanilla client will show them. Without it the " +
                "reasonable conclusion is that the server is dead. Recorded per account in the " +
                "registry so a reconnect does not replay it.");
            PerVerseKeys = Config.Bind("Verse", "PerVerseKeys", true,
                "Keep progression to the verse that earned it. Vanilla has one key set for " +
                "the whole world, so one party killing a boss would unlock every other " +
                "party's spawn tables, raids, trader stock and map markers - which is not " +
                "merely anticlimactic but dangerous, because a new verse would meet " +
                "post-progression monsters and endgame raids at its first base. World " +
                "modifiers and server options stay global: the split is vanilla's own, " +
                "everything before GlobalKeys.NonServerOption is the operator's and " +
                "everything after it is progression.");
            EnsurePlayerEvents = Config.Bind("Verse", "EnsurePlayerEvents", true,
                "Set the PlayerEvents world modifier at startup if it is not already set. " +
                "This is a prerequisite for PerVerseKeys rather than a preference: with " +
                "progression moved out of the world set, every raid's required-key check " +
                "would fail and raids would stop entirely, with nothing in the log to say " +
                "so. With it on, raids are gated on each player's own progression instead, " +
                "which under verses is per party.");

            GlobalChat = Config.Bind("Verse", "GlobalChat", true,
                "Let everyone on the server hear everyone else, whatever verse they are in " +
                "and however far apart they are standing. Vanilla does not broadcast chat: " +
                "the speaking client sends one message per listener taken from its own " +
                "player list, which this plugin gives out per verse, and normal chat is then " +
                "distance-checked at 15 m by the receiver. This re-sends each utterance to " +
                "every peer along the shout path, which has no distance check, keeping the " +
                "original chat type so a whisper still reads as a whisper. One visible " +
                "difference: normal chat stops appearing as a bubble over the speaker's head " +
                "and appears as floating text at their position, because the bubble only " +
                "exists on the path being replaced. Off isolates chat to a verse.");
            ScatterNewVerses = Config.Bind("Verse", "ScatterNewVerses", false,
                "Start each new verse away from where the older ones have been playing, " +
                "instead of at the world's start temple like every other verse. The one thing " +
                "copy-on-write cannot undo is destruction that happened before the plugin " +
                "existed: trees felled and ore mined by the original party are gone from the " +
                "world rather than hidden, so a new verse inherits that stripped ground. Off " +
                "by default so every verse starts in the same place; worth turning on only on " +
                "a migrated world where that stripped ground is a real problem.");
            ScatterRadius = Config.Bind("Verse", "ScatterRadius", 2000f,
                "Roughly how far from the world centre a new verse starts, in metres. The " +
                "angle is derived from the verse id so a verse always lands in the same " +
                "place and no two share a spot; the radius steps outwards until the point is " +
                "on land, so it is a preference rather than a promise.");

            MigrateLegacyInto = Config.Bind("Migration", "MigrateLegacyInto", 0,
                "Turn a world that was played before this plugin existed into one verse, " +
                "once. Set it to the verse id the existing world should become - 1 on a " +
                "world with no verses yet. Everything a player built or tamed is tagged as " +
                "that verse.s, everyone in the world's player history becomes a member, and " +
                "the progression keys earned so far move to that verse so new ones do not " +
                "inherit them. Terrain, vegetation, locations and dungeons stay shared, " +
                "which is what a new verse needs in order to have a world at all. 0 is off, " +
                "and it refuses to run twice or with anybody connected.");
            MigrateDryRun = Config.Bind("Migration", "MigrateDryRun", true,
                "Survey and report what the migration would do, writing nothing. On by " +
                "default on purpose: the verse tag goes into the ZDO field set and therefore " +
                "into the world save, so the only way to undo it is to restore the world. " +
                "Read the report, check the member list is who you expect, back the world " +
                "up, then set this to false.");

            SweepInto = Config.Bind("Migration", "SweepInto", 0,
                "A second pass over an already-migrated world, for the three things the " +
                "one-shot migration cannot see: levelled terrain (TerrainComp carries no " +
                "creator), creatures that existed before the plugin (a shared creature is " +
                "simulated by one verse and frozen for the rest - those were the seagulls " +
                "stuck in mid-air), and built pieces whose creator was never recorded. Set " +
                "it to the verse that should take them. Unlike the migration it has no " +
                "once-only marker: it only tags what is still untagged, so running it again " +
                "is a no-op, and running it after more play picks up whatever has appeared. " +
                "0 is off.");
            SweepDryRun = Config.Bind("Migration", "SweepDryRun", true,
                "Survey and report what the sweep would take, writing nothing. On by default " +
                "for the same reason as the migration's: the tag goes into the world save.");
            SweepTerrain = Config.Bind("Migration", "SweepTerrain", true,
                "Include levelled ground. Off leaves the old party's flattened building site " +
                "visible in every verse.");
            SweepCreatures = Config.Bind("Migration", "SweepCreatures", true,
                "Include creatures that predate the plugin. New verses spawn their own " +
                "wildlife, so handing the old animals to the legacy verse is both the fix for " +
                "the frozen ones and the right answer.");
            SweepPiecesNear = Config.Bind("Migration", "SweepPiecesNear", 64f,
                "Take built pieces that have no creator recorded, but only within this many " +
                "metres of something the verse already owns. Prefab alone cannot tell a " +
                "player's wall from a village ruin or a dungeon wall - they are all Pieces - " +
                "so proximity does the work the creator field normally would. Deliberately " +
                "conservative; raise it if parts of a base are still shared, and check the " +
                "dry run before applying. 0 skips them entirely.");

            TraceCharacters = Config.Bind("Diagnostics", "TraceCharacters", true,
                "Log the first time each character is hidden from each verse. Ordinary " +
                "objects being filtered is the whole point of this plugin and logging them " +
                "would be a torrent, but a character going missing is nearly always a bug - " +
                "a plugin's NPC that one verse can see and another cannot. The line says " +
                "whether a verse tag or a destruction mask did it, which is the difference " +
                "between a diagnosis and a guess. Capped at 200 lines per run.");
            SelfTestEnabled = Config.Bind("Diagnostics", "SelfTest", false,
                "Check copy-on-write at startup against two synthesised players in two " +
                "verses, instead of waiting for two real parties to meet in a dungeon. It " +
                "makes a shared creature, damages it as one verse, has the other swing at " +
                "what is left, and asserts that each verse ends up with exactly one creature " +
                "at its own health. Refuses to run with anybody connected, and destroys what " +
                "it made. VERSE_SELFTEST=1 in the environment turns it on too.");

            Metrics10s = Config.Bind("Diagnostics", "Metrics", true,
                "Log send-round and filtering counters every ten seconds. This is how both " +
                "spikes are measured; turn it off once a server is known good.");

            ArenaEnabled = Config.Bind("Arena", "ArenaEnabled", false,
                "The challenge arena: ten waves of monsters in a ring, with a loaner kit of " +
                "gear handed out at the gate. Off by default, like every other part of this " +
                "plugin that has not been through a play test. See docs/arena-design.md.");
            ArenaCommandWord = Config.Bind("Arena", "ArenaCommandWord", "!arena",
                "What players type in chat to go to the arena. Same leading-slash restriction " +
                "as the other command words: a '/' never leaves the player's machine.");
            ArenaBiome = Config.Bind("Arena", "ArenaBiome", "Meadows",
                "Which biome to look for the arena's ground in. Any name from Heightmap.Biome. " +
                "Wanted: flat, open, far from where anybody plays - the ring is one shared " +
                "structure every verse sees, so it must not sit where somebody might build, " +
                "and distance from the temple and from any legacy base also keeps it out of " +
                "Sweep's reach. The Deep North was the first choice and is a trap: it freezes, " +
                "and the gate is where a player stands longest - emptying their pockets, " +
                "taking the kit, eating - so the first live test froze to death at the chests " +
                "before it could drink the frost mead that was inside them. Nothing on the " +
                "server side can stop that, because cold resistance is an item in an inventory " +
                "it cannot reach. Plains is warm, flat, open and bright, which is also what " +
                "films best. Mountain and DeepNorth still work if the gate is given a fire.");
            ArenaMinDistance = Config.Bind("Arena", "ArenaMinDistance", 2000f,
                "How far from the centre of the world to start looking, in metres. Biomes are " +
                "radial from the origin, so this is really 'how far out is the biome you " +
                "asked for'. The world is a 10 km disc.");
            ArenaRadius = Config.Bind("Arena", "ArenaRadius", 19.5f,
                "Radius of the ring, in metres - the wall is built on this circle, and it is " +
                "the fighting floor. A quarter smaller than the 26 m the first live runs used: " +
                "at that size a wave of a dozen creatures spread out far enough that a fight " +
                "became a walk between them, and nothing a camera wants is more than a few " +
                "metres wide. A verse holds twelve players, so do not shrink it much further.");
            ArenaClearRadius = Config.Bind("Arena", "ArenaClearRadius", 60f,
                "How far out from the centre the ground is stripped of trees, bushes, rocks, " +
                "ruins and native wildlife, in metres. Wider than ArenaRadius on purpose: a fir " +
                "standing just outside the wall is in every shot and drops a trunk across the " +
                "floor when a wave smashes it, so the cleared apron is deliberately wider than " +
                "the arena: this is the circle the venue is, not the circle the fight is. " +
                "Never less than the stray tolerance, whatever is set here. Verse-tagged " +
                "objects - anything a player built or dropped - are never touched at any " +
                "radius, so this cannot eat somebody's base.");
            ArenaWallPrefab = Config.Bind("Arena", "ArenaWallPrefab", "auto",
                "Which building piece the wall is made of. 'auto' compares the WearNTear " +
                "health of the materials Verse knows how to lay a circle from - grausten, " +
                "black marble, stone - and takes the toughest the server has, which is decided " +
                "by reading the prefabs rather than by a number written into the plugin. Name " +
                "one of them to pin it. A prefab Verse does not know the dimensions of is " +
                "honoured but assumed to be 4 m wide and 2 m tall, which will leave gaps if it " +
                "is not.");
            ArenaSitePoint = Config.Bind("Arena", "ArenaSitePoint", "",
                "Where the arena is, as 'x,y,z'. Left empty the server scans for the flattest " +
                "dry patch of the chosen biome and writes the answer back here, so the scan " +
                "runs once per world. Terrain comes from the seed and clients generate it " +
                "themselves, so this cannot be a fixed number shipped with the plugin - a flat " +
                "field on one world is a cliff face on the next. Set it by hand to override.");
            ArenaChestPrefab = Config.Bind("Arena", "ArenaChestPrefab", "piece_chest_private",
                "Which container the gate chests are made from. The private chest is the " +
                "default because vanilla refuses to open it for anybody but the player whose " +
                "id is in its creator field, which is a lock the server can set and does not " +
                "have to enforce itself. As many are placed as the kit and a full inventory " +
                "need, read off the prefab's own grid size.");
            ArenaDeckPrefab = Config.Bind("Arena", "ArenaDeckPrefab", "wood_floor",
                "Which floor piece the deck under the gate chests is laid from, 2 m square. A " +
                "chest is a building piece, and a building piece whose footprint hangs over " +
                "uneven ground loses its support and eventually collapses - with a player's " +
                "whole deposit inside it. The deck is what it stands on instead. Set empty to " +
                "go back to putting chests straight on the terrain.");
            ArenaLevelGround = Config.Bind("Arena", "ArenaLevelGround", true,
                "Flatten the arena's own ground before building on it, and pave the fighting " +
                "floor. The server does this by writing the zone's terrain deltas into its " +
                "own _TerrainCompiler object - the same data a player's hoe produces - so no " +
                "client has to be present and nothing is faked: see src/Verse/Ground.cs. Off " +
                "leaves the venue standing on the hillside it was scanned out of, which is " +
                "how it was before and is still an arena.");
            ArenaBoardwalk = Config.Bind("Arena", "ArenaBoardwalk", true,
                "Lay a ring of wooden boards on the ground around the outside of the wall. " +
                "Cosmetic: it gives the venue a floor to stand on rather than trodden grass, " +
                "and it is where a spectator lands.");
            ArenaBoardPrefab = Config.Bind("Arena", "ArenaBoardPrefab", "wood_floor",
                "Which floor piece the boardwalk is laid from, 2 m square. Set empty to skip " +
                "the boardwalk the same way ArenaBoardwalk=false does.");
            ArenaGallery = Config.Bind("Arena", "ArenaGallery", true,
                "Put a ladder up the outside of the wall and an iron cage railing along the " +
                "inside of its top, so a fighter who has died can climb up and watch their " +
                "friends finish the run without being able to drop back in. The railing is the " +
                "half that matters: it is see-through and solid, so the gallery is a view and " +
                "not a way in.");
            ArenaRailPrefab = Config.Bind("Arena", "ArenaRailPrefab", "iron_wall_2x2",
                "The railing piece - vanilla's Cage Wall 2x2, which is iron bars you can see " +
                "through and cannot walk through. Any 2 m x 2 m wall piece works; its real " +
                "size is measured off the prefab.");
            ArenaLadderPrefab = Config.Bind("Arena", "ArenaLadderPrefab", "wood_stepladder",
                "The ladder piece, stacked as many times as it takes to reach the top of the " +
                "wall. Its height is measured off the prefab rather than assumed.");
            ArenaUnbreakable = Config.Bind("Arena", "ArenaUnbreakable", true,
                "Write an unreachable amount of health into everything the arena builds - the " +
                "wall, the decks, the boardwalk, the gallery and the gate chests. Vanilla has " +
                "no indestructible flag, but a piece's current health is a field on its own " +
                "ZDO, so this is just a number the server owns. Without it a troll can open " +
                "the wall, which is a hole a fighter can leave through and a spectator can " +
                "come in through, and a deposit chest can be smashed with somebody's gear in " +
                "it. Off restores vanilla health for pieces built from then on.");
            ArenaEnforceKit = Config.Bind("Arena", "ArenaEnforceKit", true,
                "Require that everything a fighter is wearing or holding came out of the kit, " +
                "checked at the gate and again while they fight. Only the visible equipment " +
                "slots are ZDO fields, so this is all the server can see: a backpack full of " +
                "Mistlands food is invisible and always will be. Off for testing.");
            ArenaRested = Config.Bind("Arena", "ArenaRested", true,
                "Grant the Rested buff when a run starts, and top it up while it lasts. " +
                "Without it the arena is a stamina-starved slog and the player cannot do " +
                "anything about it: Rested comes from comfort, comfort comes from a fire and a " +
                "roof, and a stone ring in a field has neither. Reachable because status " +
                "effects are applied by a routed RPC to the target's own character, the same " +
                "path teleports use - and worth granting even with no fire nearby, because " +
                "SE_Rested's base duration is a flat 300 s.");
            ArenaRestedEffect = Config.Bind("Arena", "ArenaRestedEffect", "Rested",
                "Which status effect to grant, by its name in ObjectDB. A config key rather " +
                "than a constant because the server cannot see the effect list until it is " +
                "running, and a name that stops resolving would fail silently - the arena " +
                "self-test checks this one exists at startup.");
            ArenaTrace = Config.Bind("Arena", "ArenaTrace", false,
                "Log every object taken back out of the ring - wandering wildlife, loot, " +
                "building pieces. Useful once, while settling what the guard should and should " +
                "not catch, and noise after that.");

            // Beside the world save rather than in the plugin folder: the registry is only
            // meaningful for the world whose ZDOs carry the matching verse tags.
            string dir = Path.Combine(Paths.ConfigPath, "Verse");
            Directory.CreateDirectory(dir);
            Verses.Load(Path.Combine(dir, "verses.json"));

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(VersePlugin).Assembly);

            StartupCheck();
        }

        /// <summary>
        /// Every hook here reaches a private member by name, and a name that stops resolving
        /// is the failure this plugin is most likely to meet on a game update. Harmony itself
        /// throws on an unresolvable patch target, but the reflective lookups inside the
        /// patches would otherwise fail silently and quietly stop isolating anything - so they
        /// are resolved once, now, and reported.
        /// </summary>
        private void StartupCheck()
        {
            string[] problems =
            {
                Isolation.ZDOMan_SendZDOs_Patch.Resolve(),
                Authorship.Check(),
                Isolation.ZRoutedRpc_RouteRPC_Patch.Resolve(),
                Isolation.ZNet_SendPlayerList_Patch.Resolve(),
                Keys.Resolve(),
                Destruction.Resolve(),
                PublicServer.Resolve(),
                Arena.Resolve()
            };

            bool ok = true;
            foreach (string problem in problems)
            {
                if (problem == null) continue;
                ok = false;
                Log.LogError("startup check: " + problem);
            }

            string cap = PlayerCap.Applied()
                ? PlayerCap.Limit().ToString()
                : "NOT PATCHED (" + (PlayerCap.Problem ?? "Harmony rejected the rewrite") + ")";

            // Only cosmetic, so it is a note on the cap rather than a failed hook: the browser
            // would go on printing "/ 10" while the server really admits more.
            if (PlayerCap.Applied() && !PlayerCap.Advertised) cap += " (browser still says 10)";

            Log.LogInfo($"{Name} {Version} startup check: " +
                        $"isolation {(Isolate.Value ? "on" : "OFF")}, " +
                        $"party cap {MaxPartySize.Value}, " +
                        (ArenaEnabled.Value ? "arena on, " : "") +
                        $"player cap {cap}, " +
                        $"password {PublicServer.Status()}, " +
                        (ok ? "all hooks resolved." : "SOME HOOKS DID NOT RESOLVE - see above."));

            if (!ok)
                Log.LogError("Verse is NOT isolating correctly. Do not let players on: they " +
                             "may be able to see and reach each other's verses.");
        }

        private void Update()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // Once the world is up: ZNetScene is the last of the three to appear, and the
            // test needs a real prefab out of it.
            if (!_selfTested && ZDOMan.instance != null && ZNetScene.instance != null)
            {
                _selfTested = true;
                Keys.EnsurePlayerEvents();
                Migration.Run();
                Sweep.Run();
                SelfTest.Run();

                // Creatures from a run that never finished. They have to be persistent for a
                // client to ever simulate them, so a crash mid-wave leaves them standing.
                if (ArenaEnabled.Value && ArenaSite.Resolve(out UnityEngine.Vector3 _))
                {
                    Arena.CleanupOrphans();
                    ArenaRing.Ensure();
                    ArenaApron.Ensure();
                    ArenaStand.Ensure();

                    // After the venue is up, and whatever built it: "unbreakable" is a property
                    // of the arena, not of the code path that happened to place a piece. See
                    // ArenaRing.HardenAll.
                    int hardened = ArenaRing.HardenAll();
                    if (hardened > 0)
                        Log.LogInfo($"arena: made {hardened} fixture(s) unbreakable");

                    ArenaSelfTest.Run();
                }
            }

            float dt = UnityEngine.Time.deltaTime;
            if (Metrics10s.Value) Metrics.Report(dt);
            Spawns.Tick(dt);
            Welcome.Tick(dt);
            if (ArenaEnabled.Value) Arena.Tick(dt);

            _since += dt;
            if (_since < 30f) return;
            _since = 0f;
            Verses.SaveIfDirty();
        }

        private void OnDestroy()
        {
            Verses.SaveIfDirty();
            _harmony?.UnpatchSelf();
        }
    }
}
