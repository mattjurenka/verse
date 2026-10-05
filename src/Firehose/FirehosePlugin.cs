using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Firehose
{
    /// <summary>
    /// Server-side BepInEx plugin: widens the pipe the server pushes world data down, so
    /// arriving somewhere new - stepping out of a portal, most visibly - takes a second
    /// rather than ten.
    ///
    /// Nothing is installed on the players' machines. The fix is entirely in how the server
    /// paces its own sends; a vanilla client receives faster without knowing anything changed.
    ///
    /// <para><b>What is actually slow.</b> Vanilla's <c>ZDOMan.SendZDOs</c> refuses to send
    /// anything to a peer whose socket already has 10240 bytes outstanding, and otherwise
    /// fills a package up to <c>10240 - outstanding</c> bytes. That number is a send
    /// <i>window</i>, and - this is the part that bites - <c>ZSteamSocket.GetSendQueueSize</c>
    /// counts bytes already on the wire but not yet acknowledged, not just bytes still
    /// waiting locally. So the window behaves exactly like a TCP window with no scaling:
    /// throughput is capped at <c>window / round-trip-time</c>, whatever the link can do.
    /// 10240 bytes over a ~160 ms round trip is ~64 KB/s - the flat line the profiling found.</para>
    ///
    /// <para>Three separate ceilings, each its own patch and its own setting:</para>
    /// <list type="number">
    /// <item><see cref="SendWindow"/> - the 10240-byte window itself.</item>
    /// <item><see cref="SendRounds"/> - vanilla services <i>one peer per frame</i>, so the
    /// window is also only refreshed every <c>50 ms + N frames</c>. That makes the real
    /// ceiling <c>window / max(RTT, round period)</c> and sags as players arrive.</item>
    /// <item><see cref="SteamRate"/> - Valheim pins Steam's own per-connection send rate to
    /// exactly 150 KB/s, min and max, which disables Steam's bandwidth estimation and caps
    /// everything above regardless of the window.</item>
    /// </list>
    ///
    /// <para>Each is independently switchable so the effect of each can be measured on the
    /// same build; <see cref="Diagnostics"/> reports per-player throughput, and logs a
    /// one-line summary of every burst, which is what a portal arrival looks like from here.</para>
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class FirehosePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.matthew.firehose";
        public const string Name = "Firehose";
        public const string Version = "1.0.0";

        internal static FirehosePlugin Instance;
        internal static ManualLogSource Log;

        // --- Send ---
        internal static ConfigEntry<int> Window;
        internal static ConfigEntry<bool> ServiceEveryPeer;
        internal static ConfigEntry<float> RoundSeconds;

        // --- Steam ---
        internal static ConfigEntry<int> SendRateMin;
        internal static ConfigEntry<int> SendRateMax;

        // --- Diagnostics ---
        internal static ConfigEntry<bool> Report;
        internal static ConfigEntry<int> BurstThreshold;
        internal static ConfigEntry<bool> SelfTestEnabled;   // not "SelfTest": that is the class
        internal static ConfigEntry<int> SelfTestObjects;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Window = Config.Bind("Send", "WindowBytes", 65536,
                "How many bytes of world data the server will keep in flight to one player " +
                "at a time. Vanilla is 10240, and because the count includes bytes already " +
                "on the wire awaiting acknowledgement, that alone caps a player at " +
                "window/round-trip - about 64 KB/s at 160 ms ping. 65536 saturates Steam's " +
                "own 150 KB/s cap for any realistic ping. The cost of a big window is " +
                "queueing delay: a player with a full window waits window/sendrate for " +
                "anything behind it, so 64 KB at 150 KB/s is up to ~430 ms added latency on " +
                "their own updates, and only while that much data is genuinely backed up - " +
                "which is the loading screen, not normal play. Raise it to 131072 or more " +
                "only if Steam.SendRateMax went up too, since the two multiply out. " +
                "Clamped to 4096-262144; a Steam reliable message cannot exceed 512 KB.");
            ServiceEveryPeer = Config.Bind("Send", "ServiceEveryPeer", true,
                "Refresh every player's send window on each round, instead of vanilla's one " +
                "peer per frame. Vanilla's round takes 50 ms + N frames, so the window is " +
                "topped up every ~120 ms at 4 players and ~380 ms at 10, which throttles " +
                "everyone as the server fills up; this makes the period constant in the " +
                "player count. Costs one sync-list build and sort per player per round, " +
                "which is CPU the profiling says is sitting idle. Off restores vanilla " +
                "scheduling, which is the comparison worth having.");
            RoundSeconds = Config.Bind("Send", "RoundSeconds", 0.05f,
                "How often a round happens, in seconds. 0.05 is vanilla's own interval. " +
                "Shorter rounds mean a smaller window achieves the same throughput - the " +
                "ceiling is window/max(ping, round) - but cost proportionally more CPU. " +
                "Only used when ServiceEveryPeer is on.");

            SendRateMin = Config.Bind("Steam", "SendRateMin", 153600,
                "Floor on Steam's own estimate of what this connection can carry, in bytes " +
                "per second. 153600 is what vanilla sets. 0 leaves Steam's config untouched, " +
                "including SendRateMax.");
            SendRateMax = Config.Bind("Steam", "SendRateMax", 1048576,
                "Ceiling on that estimate. Vanilla pins it to 153600 - the same value as the " +
                "floor - which means Steam never probes for more and 150 KB/s per player is " +
                "a hard wall no window can get past. Raising only the ceiling leaves Steam's " +
                "congestion control free to find the real capacity and back off on a weak " +
                "link, which is strictly more adaptive than vanilla's fixed rate. Remember " +
                "the player has to be able to receive it: 1048576 asks for up to 8 Mbit/s " +
                "down during a burst. 0 leaves Steam's config untouched.");

            Report = Config.Bind("Diagnostics", "Report", true,
                "Log a per-player line every ten seconds - bytes out, send-queue depth - and " +
                "a one-line summary of every burst, which is how a portal arrival shows up " +
                "('received 589 KB in 4.1 s'). This is the measurement the whole plugin " +
                "exists to move, so it is on by default; turn it off once a server is known " +
                "good. Reads the per-socket counters nothing in the game reads.");
            BurstThreshold = Config.Bind("Diagnostics", "BurstThresholdBytes", 32768,
                "Bytes per second to one player above which the plugin considers them to be " +
                "loading an area rather than just playing, and starts timing a burst.");

            SelfTestEnabled = Config.Bind("Diagnostics", "SelfTest", false,
                "Measure the patched send path at startup against a fake peer, instead of waiting " +
                "for a player to walk through a portal. It fills a patch of world with " +
                "throwaway objects, runs one send round at vanilla's window and one at the " +
                "configured window, and prints both - which is the only thing that proves a " +
                "wider window actually puts more bytes on the wire. Refuses to run with " +
                "anybody connected, and destroys what it made. FIREHOSE_SELFTEST=1 in the " +
                "environment turns it on too, for a one-off run with no config edit.");
            SelfTestObjects = Config.Bind("Diagnostics", "SelfTestObjects", 4000,
                "How many throwaway objects the self-test makes. Needs to be comfortably " +
                "more data than one window, or every round fills and nothing is learned: " +
                "4000 is roughly 300 KB. Clamped to 100-50000.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(FirehosePlugin).Assembly);

            Log.LogInfo($"{Name} {Version} loaded. Nothing is needed on the players' end; " +
                        $"see the startup check below for what bound and what it is worth.");
        }

        private void OnDestroy() => _harmony?.UnpatchSelf();

        private void Update()
        {
            if (ZNet.instance == null || ZDOMan.instance == null) return;

            float dt = Time.deltaTime;

            // The rate is applied before the startup check so the check reports what it did,
            // rather than that it had not run yet.
            if (ZNet.instance.IsServer()) SteamRate.Tick(dt);
            SelfTest.Verify(dt);
            Diagnostics.Startup(_harmony);
            if (!ZNet.instance.IsServer()) return;

            Diagnostics.Tick(dt);
        }
    }
}
