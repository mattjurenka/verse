namespace Verse
{
    /// <summary>
    /// Counters for the spike measurements, reported to the log on a timer.
    ///
    /// <para>These used to live beside Verse's own send scheduler, which has been retired: the
    /// `Firehose` plugin in this repo replaces the same vanilla method for the same reason,
    /// and two plugins on <c>ZDOMan.SendZDOToPeers2</c> would have serviced every peer twice a
    /// round. Firehose reports throughput per player in bytes, which is a better number than
    /// anything here; what is left in this class is the isolation and copy-on-write
    /// accounting, which is Verse's own.</para>
    /// </summary>
    internal static class Metrics
    {
        internal static long Tagged;
        internal static long Considered;
        internal static long Filtered;
        /// <summary>Cross-verse routed RPCs dropped at the relay.</summary>
        internal static long Blocked;
        /// <summary>Objects really destroyed, because they belonged to the verse destroying them.</summary>
        internal static long Destroyed;
        /// <summary>Shared objects kept, but hidden from the verse that destroyed them.</summary>
        internal static long Masked;
        /// <summary>Shared objects handed to a verse that changed them, leaving a pristine copy.</summary>
        internal static long Forked;
        /// <summary>Shared objects handed to a verse that reached for one somebody else owned.</summary>
        internal static long Claimed;
        /// <summary>A verse's own objects taken back from a peer that position alone had made the owner.</summary>
        internal static long Reclaimed;
        /// <summary>Players put down at their verse's spawn after changing verse.</summary>
        internal static long Placed;
        /// <summary>Progression keys kept to one verse instead of the whole world.</summary>
        internal static long KeysScoped;
        /// <summary>Utterances re-sent to the whole server rather than to one verse.</summary>
        internal static long ChatGlobal;
        /// <summary>Arena runs started.</summary>
        internal static long ArenaRuns;
        /// <summary>Creatures the arena put in the ring.</summary>
        internal static long ArenaSpawned;
        /// <summary>Objects taken back out of the ring - wildlife, loot, buildings, leftovers.</summary>
        internal static long ArenaCulled;
        /// <summary>Culled objects a client then re-created, which should be none.</summary>
        internal static long ArenaResurrected;

        /// <summary>
        /// One peer being serviced once, counted by <see cref="Isolation.ZDOMan_SendZDOs_Patch"/>
        /// so that it is counted whichever scheduler is driving the sends.
        /// </summary>
        internal static long PeerSends;

        private static float _since;

        internal static void Report(float dt)
        {
            _since += dt;
            if (_since < 10f) return;

            float seconds = _since;
            _since = 0f;

            // updates/s per peer: how often one player hears from the server. Vanilla costs
            // 50ms + N frames per round, so it sags as players arrive; with Firehose loaded it
            // should hold steady whatever N is. Derived from peer-sends rather than from
            // rounds, because that is counted under either scheduler.
            int peers = Peers.Known;
            float perPeer = peers > 0 ? PeerSends / seconds / peers : 0f;
            bool firehose = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.matthew.firehose");

            // Both fixtures in one pass over the object table, which is the expensive part of
            // this line; skipped entirely when the arena is off.
            int ringPieces = 0, deckTiles = 0;
            if (VersePlugin.ArenaEnabled.Value) ringPieces = ArenaRing.Count(out deckTiles);

            VersePlugin.Log.LogInfo(
                $"verse metrics: {perPeer:0.0} updates/s per peer " +
                $"({(firehose ? "firehose" : "vanilla")} scheduling, {peers} peer(s)), " +
                $"{Considered} zdos considered, {Filtered} hidden, {Tagged} tagged, {Blocked} rpcs blocked, " +
                $"{Destroyed} destroyed, {Masked} masked, {Forked} forked, {Claimed} claimed, {Reclaimed} reclaimed, {Placed} placed, {KeysScoped} keys scoped, {ChatGlobal} chat relayed, " +
                $"{Verses.Count} verse(s)" +
                (VersePlugin.ArenaEnabled.Value
                    ? ", ring: " + ringPieces + " piece(s), deck: " + deckTiles + " tile(s)"
                    : "") +
                (ArenaRuns + ArenaSpawned + ArenaCulled > 0
                    ? $", arena: {ArenaRuns} run(s), {ArenaSpawned} spawned, {ArenaCulled} culled, {ArenaResurrected} resurrected"
                    : ""));

            PeerSends = 0;
            Considered = 0;
            Filtered = 0;
            Blocked = 0;
            Destroyed = 0;
            Masked = 0;
            Forked = 0;
            Claimed = 0;
            Reclaimed = 0;
            Placed = 0;
            KeysScoped = 0;
            ChatGlobal = 0;
            ArenaRuns = 0;
            ArenaSpawned = 0;
            ArenaCulled = 0;
            ArenaResurrected = 0;
        }
    }
}
