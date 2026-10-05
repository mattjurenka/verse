using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// Decides which verse a new object belongs to, at the moment it is created.
    ///
    /// The rule that matters, learned the hard way: <b>ownership is not authorship.</b>
    /// <c>ZDOMan.ReleaseNearbyZDOS</c> hands ownership of every persistent ZDO in a player's
    /// active area to that player every two seconds, so "owned by a peer" means "being
    /// simulated by whoever is standing nearest" and says nothing about who made it. Tagging
    /// on ownership tagged the entire landscape as the first player walked through it, and the
    /// next player to enter a different verse found a world with no trees in it.
    ///
    /// What does identify a player's own work is the ZDO arriving at the server for the first
    /// time from that player's connection. <c>ZDOMan.RPC_ZDOData</c> only calls
    /// <c>CreateNewZDO</c> for a ZDOID the server has never seen, so a creation inside that
    /// call is something that client just built, dropped, spawned or tamed. Everything the
    /// world generates is created by the server's own ghost-zone pass and never comes through
    /// here, so it stays untagged and shared - which is the known gap spike 3 closes.
    /// </summary>
    internal static class Authorship
    {
        /// <summary>Confirms the creation hook resolved; reported by the startup check.</summary>
        internal static string Check() =>
            AccessTools.Method(typeof(ZDOMan), "CreateNewZDO",
                new[] { typeof(ZDOID), typeof(UnityEngine.Vector3), typeof(int) }) == null
                ? "ZDOMan.CreateNewZDO(ZDOID, Vector3, int) not found"
                : null;

        /// <summary>
        /// The verse whose client we are currently deserializing ZDOs from, or
        /// <see cref="Verses.None"/> outside that call. Set around <c>RPC_ZDOData</c> so that
        /// the creation hook needs no reflection of its own.
        /// </summary>
        private static int _incoming = Verses.None;

        /// <summary>
        /// Depth of <see cref="Suspend"/> calls. Divergence creates ZDOs while inside
        /// <c>RPC_ZDOData</c> that belong to nobody, and they must not be mistaken for the
        /// sending player.s own work. A counter rather than a flag so nesting cannot
        /// accidentally re-enable it halfway.
        /// </summary>
        private static int _suspended;

        /// <summary>
        /// Whether a creation happening right now is the server.s own rather than a player.s.
        /// <see cref="ArenaGuard"/> reads this to tell the arena.s own creatures and chests from
        /// an intruder in the ring.
        /// </summary>
        internal static bool Suspended => _suspended > 0;

        /// <summary>
        /// The verse whose client is being deserialised from, or <see cref="Verses.None"/> when
        /// the creation did not come from a peer at all.
        /// </summary>
        internal static int Incoming => _incoming;

        internal static void Suspend() => _suspended++;

        internal static void Resume()
        {
            if (_suspended > 0) _suspended--;
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        internal static class ZDOMan_RPC_ZDOData_Patch
        {
            private static void Prefix(ZRpc rpc)
            {
                _incoming = Verses.None;
                if (ZNet.instance == null || !ZNet.instance.IsServer() || rpc == null) return;

                // Matched by rpc rather than through ZDOMan's own private FindPeer, which
                // would have to be reflected for no gain: this runs once per peer per round.
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || peer.m_rpc != rpc) continue;
                    _incoming = Peers.VerseOf(peer.m_uid);
                    if (_incoming == Verses.None)
                        VersePlugin.Log.LogWarning(
                            $"peer {peer.m_uid} ({peer.m_playerName}) sent ZDO data with no verse " +
                            "placement yet - whatever they create this round stays untagged and " +
                            "shared. Should only ever fire in the instant between connecting and " +
                            "Peers.Place running; a repeat for the same peer is a real bug.");
                    return;
                }
            }

            /// <summary>
            /// A finalizer rather than a postfix: if the deserialisation loop throws, a stale
            /// verse left here would tag the next thing the server created, wherever it came
            /// from.
            /// </summary>
            private static void Finalizer() => _incoming = Verses.None;
        }

        [HarmonyPatch]
        internal static class ZDOMan_CreateNewZDO_Patch
        {
            private static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method(typeof(ZDOMan), "CreateNewZDO",
                    new[] { typeof(ZDOID), typeof(UnityEngine.Vector3), typeof(int) });

            private static void Postfix(ZDO __result)
            {
                if (_suspended > 0) return;
                if (_incoming == Verses.None || __result == null) return;
                ZdoVerse.Set(__result, _incoming);
                Metrics.Tagged++;
            }
        }
    }
}
