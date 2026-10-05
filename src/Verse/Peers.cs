using System.Collections.Generic;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// Maps a live connection to a verse.
    ///
    /// Two separate identities matter here and conflating them is the easiest way to get this
    /// wrong. A peer's <c>m_uid</c> is per-connection and is what RPCs are addressed to; the
    /// platform user ID is the account, survives a reconnect, and is what the registry is
    /// keyed by. This class is the only place the two are tied together.
    ///
    /// The lookup has to be ready before the first ZDO is sent to a new peer, which is why it
    /// is built in a postfix on <c>ZDOMan.AddPeer</c> - the same call that first makes the
    /// peer eligible for sync.
    /// </summary>
    internal static class Peers
    {
        private static readonly Dictionary<long, int> VerseByUid = new Dictionary<long, int>();

        /// <summary>The verse a connection belongs to, or <see cref="Verses.None"/>.</summary>
        internal static int VerseOf(long uid) =>
            VerseByUid.TryGetValue(uid, out int verse) ? verse : Verses.None;

        internal static int VerseOf(ZNetPeer peer) =>
            peer == null ? Verses.None : VerseOf(peer.m_uid);

        internal static int Known => VerseByUid.Count;

        /// <summary>
        /// The account behind a connection. <c>ZNet.RPC_PeerInfo</c> reads this same value off
        /// the socket and checks it against a Steam session ticket before accepting the peer,
        /// so by the time we see it the client cannot have made it up.
        ///
        /// <para>Normalised through <see cref="AccountId.Canonical"/>, because the socket does
        /// not spell the account the same way on both backends and the registry has to survive
        /// the server changing backend under it.</para>
        /// </summary>
        internal static string PlatformId(ZNetPeer peer)
        {
            try
            {
                string host = peer?.m_socket?.GetHostName();
                return string.IsNullOrEmpty(host) ? null : AccountId.Canonical(host);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Places a connection in a verse without touching the registry. Only the self-test
        /// uses this: it needs two peers in two verses without inventing two accounts and
        /// leaving them in verses.json afterwards.
        /// </summary>
        internal static void Pretend(long uid, int verse) => VerseByUid[uid] = verse;

        /// <summary>
        /// Depth of <see cref="Suspend"/> calls, counted rather than flagged so nesting and
        /// Harmony's patch ordering cannot leave it stuck. The self-test brackets its fake
        /// peers with this: without it, AddPeer would run them through <see cref="Place"/>
        /// and create a real verse in the registry for an account that does not exist.
        /// </summary>
        private static int _suspended;

        internal static void Suspend() => _suspended++;

        internal static void Resume()
        {
            if (_suspended > 0) _suspended--;
        }

        internal static void Forget(long uid)
        {
            VerseByUid.Remove(uid);
            Spawns.Forget(uid);
        }

        /// <summary>
        /// Places a freshly connected peer. A player with no verse becomes the leader of a new
        /// private one, which is the whole of "on spawn each player gets their own world".
        /// </summary>
        private static void Place(ZNetPeer peer)
        {
            if (peer == null || _suspended > 0) return;

            string account = PlatformId(peer);
            if (string.IsNullOrEmpty(account))
            {
                // Better to refuse to place them than to guess: an unplaced peer sees only
                // untagged world content and nobody else's anything, which is a visible
                // failure rather than a silent leak into someone else's verse.
                VersePlugin.Log.LogError($"peer {peer.m_uid} has no platform id - left unplaced");
                return;
            }

            int verse = Verses.OfOrCreate(account);
            VerseByUid[peer.m_uid] = verse;
            VersePlugin.Log.LogInfo($"peer {peer.m_uid} ({account}) placed in verse {verse}");
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddPeer))]
        internal static class ZDOMan_AddPeer_Patch
        {
            private static void Postfix(ZNetPeer netPeer)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                Place(netPeer);
            }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemovePeer))]
        internal static class ZDOMan_RemovePeer_Patch
        {
            private static void Postfix(ZNetPeer netPeer)
            {
                if (netPeer == null) return;
                Forget(netPeer.m_uid);
            }
        }
    }
}
