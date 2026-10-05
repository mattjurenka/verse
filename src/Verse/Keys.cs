using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// SPIKE 6. Progression per verse: one party beating a boss must not beat it for everyone.
    ///
    /// <para>Vanilla keeps a single key set for the whole world. A boss death routes
    /// <c>SetGlobalKey</c> to the server (<c>Character.cs:2986</c>), the server adds it, and
    /// <c>SendGlobalKeys(0L)</c> broadcasts it to every peer in every verse. That is not only
    /// anticlimactic for the party that has not killed it - it is dangerous, because the keys
    /// gate <b>creature spawn tables</b> (<c>SpawnSystem.cs:230</c>) and <b>raids</b>
    /// (<c>RandEventSystem</c>), so a brand-new verse would meet post-progression monsters and
    /// endgame raids at its starting base. Trader stock and boss map markers follow too.</para>
    ///
    /// <para><b>What is per verse and what stays global.</b> Not every key is progression:
    /// world modifiers and server options - combat difficulty, resource rate, portal rules,
    /// <c>PlayerEvents</c> - are the operator's and genuinely do apply everywhere. The split
    /// is vanilla's own: <c>GlobalKeys</c> lists every server option *before* the
    /// <c>NonServerOption</c> member and every progression key after it, and the game itself
    /// tests <c>gk &lt; GlobalKeys.NonServerOption</c> to tell them apart
    /// (<c>ZoneSystem.cs:742</c>, <c>:778</c>, <c>KeySlider.cs:196</c>). We use the same test.
    /// A key nothing recognises parses as <c>NonServerOption</c> and is therefore treated as
    /// progression, which is the safer default: a progression key leaking changes somebody's
    /// world, whereas a modifier key wrongly scoped merely fails to apply.</para>
    ///
    /// <para>Three hooks, all on the server: attribute a key to the sender's verse rather than
    /// the world, do the same for a removal, and send each peer its own verse's keys unioned
    /// with the world's. Nothing else reads the per-verse set, because every consumer that
    /// matters - <c>SpawnSystem</c>, <c>Trader</c>, <c>Vegvisir</c> - runs on the client
    /// against the set it was sent.</para>
    /// </summary>
    internal static class Keys
    {
        private static FieldInfo _worldKeys;
        private static MethodInfo _send;

        /// <summary>Whether the lookups resolved. Reported by the startup check.</summary>
        internal static bool Ready { get; private set; }

        internal static string Resolve()
        {
            _worldKeys = AccessTools.Field(typeof(ZoneSystem), "m_globalKeys");
            if (_worldKeys == null) return "ZoneSystem.m_globalKeys not found";

            _send = AccessTools.Method(typeof(ZoneSystem), "SendGlobalKeys", new[] { typeof(long) });
            if (_send == null) return "ZoneSystem.SendGlobalKeys(long) not found";

            Ready = true;
            return null;
        }

        internal static bool Active => Ready && VersePlugin.Isolate.Value && VersePlugin.PerVerseKeys.Value;

        /// <summary>
        /// Whether this key belongs to the whole world rather than to one verse. Server
        /// options and world modifiers do; everything else is progression.
        /// </summary>
        internal static bool WorldWide(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            ZoneSystem.GetKeyValue(key, out string _, out GlobalKeys gk);
            return gk < GlobalKeys.NonServerOption;
        }

        /// <summary>The world's own set, which after a migration holds only operator keys.</summary>
        private static HashSet<string> World() =>
            _worldKeys?.GetValue(ZoneSystem.instance) as HashSet<string>;

        /// <summary>A snapshot of the world set for diagnostics - everything in it, tagged with
        /// whether each key reads as world-wide or progression by the same test <see cref="Scope"/>
        /// uses, so a leak shows up as a progression key still sitting in the world set rather
        /// than having moved into a verse's own list.</summary>
        internal static List<string> DiagWorldSet()
        {
            var list = new List<string>();
            HashSet<string> world = World();
            if (world == null) return list;
            foreach (string key in world) list.Add($"{key} ({(WorldWide(key) ? "world-wide" : "PROGRESSION")})");
            return list;
        }

        /// <summary>Everything a peer in this verse should believe about the world.</summary>
        internal static List<string> For(int verse)
        {
            var all = new List<string>();

            HashSet<string> world = World();
            if (world != null) all.AddRange(world);

            Record record = Verses.Get(verse);
            if (record?.Keys != null)
                foreach (string key in record.Keys)
                    if (!all.Contains(key)) all.Add(key);

            return all;
        }

        /// <summary>Progression keys currently in the world set - what a migration moves.</summary>
        internal static List<string> WorldProgression()
        {
            var found = new List<string>();
            HashSet<string> world = World();
            if (world == null) return found;

            foreach (string key in world)
                if (!WorldWide(key)) found.Add(key);

            return found;
        }

        /// <summary>Drops a key from the world set, for the migration to move it into a verse.</summary>
        internal static void Forget(string key)
        {
            World()?.Remove(key);
        }

        /// <summary>Pushes a verse's view of the keys to every peer in it.</summary>
        internal static void Push(int verse)
        {
            if (ZNet.instance == null) return;

            List<string> keys = For(verse);
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                if (Peers.VerseOf(peer.m_uid) != verse) continue;
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "GlobalKeys", keys);
            }
        }

        private static bool Scope(long sender, string name, bool adding)
        {
            if (!Active) return false;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;
            if (WorldWide(name)) return false;              // the operator's, not a verse's

            int verse = Peers.VerseOf(sender);
            if (verse == Verses.None) return false;         // the server's own, or an unplaced peer

            Record record = Verses.Get(verse);
            if (record == null) return false;
            if (record.Keys == null) record.Keys = new List<string>();

            bool changed = adding ? Verses.AddKey(record, name) : Verses.RemoveKey(record, name);
            if (changed)
            {
                Metrics.KeysScoped++;
                VersePlugin.Log.LogInfo(
                    $"verse {verse} {(adding ? "earned" : "lost")} '{name}' - kept to that verse");
                Push(verse);
                Verses.SaveIfDirty();
            }
            return true;                                   // handled; vanilla must not broadcast
        }

        [HarmonyPatch(typeof(ZoneSystem), "RPC_SetGlobalKey")]
        internal static class ZoneSystem_RPC_SetGlobalKey_Patch
        {
            private static bool Prefix(long sender, string name) => !Scope(sender, name, adding: true);
        }

        [HarmonyPatch(typeof(ZoneSystem), "RPC_RemoveGlobalKey")]
        internal static class ZoneSystem_RPC_RemoveGlobalKey_Patch
        {
            private static bool Prefix(long sender, string name) => !Scope(sender, name, adding: false);
        }

        [HarmonyPatch(typeof(ZoneSystem), "SendGlobalKeys")]
        internal static class ZoneSystem_SendGlobalKeys_Patch
        {
            /// <summary>
            /// Vanilla sends one set to one peer, or to everybody when the target is 0. Both
            /// become per-verse sends. Returning false skips the original, which would
            /// otherwise broadcast the world set over the top of what we just sent.
            /// </summary>
            private static bool Prefix(long peer)
            {
                if (!Active) return true;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

                if (peer != 0L)
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer, "GlobalKeys", For(Peers.VerseOf(peer)));
                    return false;
                }

                foreach (ZNetPeer p in ZNet.instance.GetPeers())
                {
                    if (p == null || !p.IsReady()) continue;
                    ZRoutedRpc.instance.InvokeRoutedRPC(p.m_uid, "GlobalKeys", For(Peers.VerseOf(p.m_uid)));
                }
                return false;
            }
        }

        /// <summary>
        /// Ensures the <c>PlayerEvents</c> world modifier is set, which is a prerequisite
        /// rather than a nicety: with progression keys moved out of the world set, every
        /// raid's <c>m_requiredGlobalKeys</c> check would fail and raids would stop entirely,
        /// silently. With it on, <c>RandEventSystem</c> gates raids on each player's *own*
        /// synced progression instead, which under verses is per party.
        /// </summary>
        internal static void EnsurePlayerEvents()
        {
            if (!VersePlugin.EnsurePlayerEvents.Value) return;
            if (ZoneSystem.instance == null) return;
            if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.PlayerEvents)) return;

            ZoneSystem.instance.SetGlobalKey(GlobalKeys.PlayerEvents);
            VersePlugin.Log.LogInfo(
                "set the PlayerEvents world modifier: raids now follow each player's own " +
                "progression rather than the world's, which is what makes per-verse keys safe");
        }
    }
}
