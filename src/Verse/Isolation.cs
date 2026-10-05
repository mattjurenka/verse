using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// SPIKE 1. What makes a verse separate.
    ///
    /// All three hooks here exist because a dedicated server decides, per peer, what that peer
    /// is told about. There is no client-side cooperation involved and none is possible: a
    /// vanilla client simply never learns that the other verses are there.
    ///
    /// 1. <c>ZDOMan.ZDOPeer.ShouldSend</c> is the single gate every ZDO send path funnels
    ///    through - near objects, distant objects, force-sends and the client queue all call
    ///    it. Filtering here is what hides buildings, creatures, dropped items and other
    ///    players. It is also why portals need no work of their own: a client matches portals
    ///    against the ZDOs it knows about, so it cannot connect to one it was never sent.
    ///
    /// 2. <c>ZRoutedRpc.RouteRPC</c> is the relay. Chat, damage and every other
    ///    client-to-client message passes through the server here, so dropping cross-verse
    ///    traffic covers all of it at once rather than one message type at a time.
    ///
    /// 3. <c>ZNet.SendPlayerList</c> builds one list and sends it to everybody, which would
    ///    leak the names and positions of every player on the server. It is replaced with a
    ///    per-verse build.
    /// </summary>
    internal static class Isolation
    {
        /// <summary>
        /// The verse of the peer currently being sent to. <see cref="Verses.None"/> means "do
        /// not filter", which is the right default: every path that has not been taught about
        /// verses keeps working.
        ///
        /// It is set around <c>ZDOMan.SendZDOs</c> rather than by whichever scheduler called
        /// it, so isolation does not depend on who is doing the scheduling - vanilla, or the
        /// `Firehose` plugin that now owns it. Reading the peer here also
        /// keeps reflection out of the per-ZDO path: this runs once per peer per round, where
        /// <see cref="ZDOPeer_ShouldSend_Patch"/> runs once per ZDO per peer per round.
        /// </summary>
        internal static int ViewerVerse = Verses.None;

        [HarmonyPatch]
        internal static class ZDOMan_SendZDOs_Patch
        {
            private static FieldInfo _peerOfZdoPeer;

            internal static string Resolve()
            {
                System.Type zdoPeer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
                if (zdoPeer == null) return "ZDOMan.ZDOPeer not found";
                _peerOfZdoPeer = AccessTools.Field(zdoPeer, "m_peer");
                return _peerOfZdoPeer == null ? "ZDOMan.ZDOPeer.m_peer not found" : null;
            }

            private static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(ZDOMan), "SendZDOs",
                    new[] { AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), typeof(bool) });

            /// <summary>
            /// <c>ZDOPeer</c> is private, so the parameter is declared as <c>object</c> -
            /// Harmony matches it by name and allows an inaccessible type to be taken this way.
            /// </summary>
            private static void Prefix(object peer)
            {
                // Counted on the send itself rather than in a scheduler, so the number means
                // the same thing whoever is scheduling: a peer-send is one peer being
                // serviced once.
                Metrics.PeerSends++;

                if (_peerOfZdoPeer == null) return;
                ViewerVerse = Peers.VerseOf(_peerOfZdoPeer.GetValue(peer) as ZNetPeer);
            }

            private static void Postfix() => ViewerVerse = Verses.None;
        }

        // --- 1. ZDO visibility --------------------------------------------------------------

        [HarmonyPatch]
        internal static class ZDOPeer_ShouldSend_Patch
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), "ShouldSend");

            /// <summary>
            /// Runs as a postfix so vanilla's revision check still decides whether there is
            /// anything new to send; this only ever takes a yes and makes it a no.
            /// </summary>
            private static void Postfix(ZDO zdo, ref bool __result)
            {
                if (!__result || zdo == null) return;
                if (!VersePlugin.Isolate.Value) return;
                if (ViewerVerse == Verses.None) return;

                Metrics.Considered++;
                if (ZdoVerse.VisibleTo(zdo, ViewerVerse)) return;

                Metrics.Filtered++;
                __result = false;
                Trace(zdo, ViewerVerse);
            }

            /// <summary>
            /// Says so, once, when a <i>character</i> is hidden from a verse. Ordinary objects
            /// being filtered is the entire point of this plugin and logging them would be a
            /// torrent; a character going missing is almost always a bug - a plugin's NPC
            /// that one verse can see and another cannot, which is exactly how the Mark
            /// problem presented. Knowing whether he was refused, and whether it was a tag or
            /// a destruction mask that did it, is the difference between a diagnosis and a
            /// guess.
            /// </summary>
            private static void Trace(ZDO zdo, int verse)
            {
                if (!VersePlugin.TraceCharacters.Value) return;
                if (_tracedCount > 200) return;              // a stuck case must not fill the disk

                int prefab = zdo.GetPrefab();
                if (!IsCharacter(prefab)) return;

                long key = ((long)verse << 40) ^ zdo.m_uid.ID;
                if (!_traced.Add(key)) return;
                _tracedCount++;

                string name = ZNetScene.instance?.GetPrefab(prefab)?.name ?? prefab.ToString();
                VersePlugin.Log.LogWarning(
                    $"hid a character from verse {verse}: {name} {zdo.m_uid} " +
                    $"tag={ZdoVerse.Of(zdo)} masked={HideMask.HiddenFrom(zdo, verse)} " +
                    $"owner={zdo.GetOwner()} persistent={zdo.Persistent} at " +
                    $"{zdo.GetPosition().x:0},{zdo.GetPosition().z:0}");
            }

            private static readonly HashSet<long> _traced = new HashSet<long>();
            private static int _tracedCount;
            private static readonly Dictionary<int, bool> _isCharacter = new Dictionary<int, bool>();

            private static bool IsCharacter(int prefab)
            {
                if (prefab == 0 || ZNetScene.instance == null) return false;
                if (_isCharacter.TryGetValue(prefab, out bool known)) return known;

                GameObject go = ZNetScene.instance.GetPrefab(prefab);
                bool character = go != null && go.GetComponent<Character>() != null;
                _isCharacter[prefab] = character;
                return character;
            }
        }

        // --- 2. Routed RPC relay ------------------------------------------------------------

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        internal static class ZRoutedRpc_RouteRPC_Patch
        {
            private static FieldInfo _server;
            private static FieldInfo _peers;

            internal static string Resolve()
            {
                _server = AccessTools.Field(typeof(ZRoutedRpc), "m_server");
                _peers = AccessTools.Field(typeof(ZRoutedRpc), "m_peers");
                if (_server == null) return "ZRoutedRpc.m_server not found";
                if (_peers == null) return "ZRoutedRpc.m_peers not found";
                return null;
            }

            /// <summary>
            /// Drops a message whose sender and recipient are in different verses, and fans a
            /// peer's broadcast out to that peer's verse only.
            ///
            /// Anything the server itself sends is left alone deliberately: the server is not
            /// in a verse, and its broadcasts are the global ones (object destruction, world
            /// state) that every verse still needs.
            /// </summary>
            private static bool Prefix(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData rpcData)
            {
                if (_server == null || _peers == null) return true;
                if (rpcData == null) return true;
                if (!(bool)_server.GetValue(__instance)) return true;

                // Chat first, and before the verse checks: it is deliberately the one thing
                // that is not isolated, so people in different verses can still talk.
                if (GlobalChat.Deliver(rpcData)) return false;

                int from = Peers.VerseOf(rpcData.m_senderPeerID);
                if (from == Verses.None) return true;

                if (rpcData.m_targetPeerID != 0L)
                {
                    int to = Peers.VerseOf(rpcData.m_targetPeerID);
                    // An unplaced recipient is not silently allowed through: that would be the
                    // one case where a leak is invisible in the log.
                    if (to != from)
                    {
                        // Before dropping it: if this is somebody reaching for a *shared*
                        // object - a boss altar, a dungeon door, a creature a location placed -
                        // the right answer is not "no" but "here is your own copy". Dropping
                        // it is what left shared creatures invulnerable to every verse but
                        // whichever one happened to be standing nearest.
                        //
                        // And an object may be this verse's *own* and still be owned by a peer
                        // in another: ZDOMan.ReleaseNearbyZDOS hands ownership to whoever is
                        // standing nearest, by position, every two seconds, and knows nothing
                        // about verses (ZDOMan.cs:930). A player's own chest can end up owned
                        // by somebody who cannot even see it, and then refuse to open.
                        if (rpcData.m_targetZDO != ZDOID.None)
                        {
                            ZDO target = ZDOMan.instance.GetZDO(rpcData.m_targetZDO);
                            if (target != null)
                            {
                                bool mine = ZdoVerse.Of(target) == from;
                                if (mine || Divergence.Claim(target, from))
                                {
                                    // The actor owns it now, so send the message to them: they
                                    // are the one who will act on it. Their client does not yet
                                    // know it owns the object, and several vanilla handlers
                                    // check IsOwner() - Character.RPC_Damage among them - so
                                    // the first swing after a claim can be swallowed. The next
                                    // one lands, once the ownership change has synced.
                                    target.SetOwner(rpcData.m_senderPeerID);
                                    rpcData.m_targetPeerID = rpcData.m_senderPeerID;
                                    if (mine) Metrics.Reclaimed++;
                                    return true;
                                }
                            }
                        }

                        Metrics.Blocked++;
                        return false;
                    }
                    return true;
                }

                var peers = _peers.GetValue(__instance) as List<ZNetPeer>;
                if (peers == null) return true;

                var pkg = new ZPackage();
                rpcData.Serialize(pkg);

                foreach (ZNetPeer peer in peers)
                {
                    if (peer == null || !peer.IsReady()) continue;
                    if (peer.m_uid == rpcData.m_senderPeerID) continue;
                    if (Peers.VerseOf(peer.m_uid) != from) continue;
                    peer.m_rpc.Invoke("RoutedRPC", pkg);
                }

                return false;
            }
        }

        // --- 3. The player list -------------------------------------------------------------

        [HarmonyPatch(typeof(ZNet), "SendPlayerList")]
        internal static class ZNet_SendPlayerList_Patch
        {
            private static FieldInfo _peers;
            private static FieldInfo _players;
            private static MethodInfo _updatePlayerList;
            private static MethodInfo _writePlayerInfo;

            internal static string Resolve()
            {
                _peers = AccessTools.Field(typeof(ZNet), "m_peers");
                _players = AccessTools.Field(typeof(ZNet), "m_players");
                _updatePlayerList = AccessTools.Method(typeof(ZNet), "UpdatePlayerList");
                _writePlayerInfo = AccessTools.Method(typeof(ZNet), "WritePlayerInfo");

                if (_peers == null) return "ZNet.m_peers not found";
                if (_players == null) return "ZNet.m_players not found";
                if (_updatePlayerList == null) return "ZNet.UpdatePlayerList not found";
                if (_writePlayerInfo == null) return "ZNet.WritePlayerInfo not found";
                return null;
            }

            private static bool Prefix(ZNet __instance)
            {
                if (!VersePlugin.FilterPlayerList.Value) return true;
                if (_peers == null || _players == null) return true;
                if (!__instance.IsServer()) return true;

                _updatePlayerList.Invoke(__instance, null);

                var peers = _peers.GetValue(__instance) as List<ZNetPeer>;
                var players = _players.GetValue(__instance) as List<ZNet.PlayerInfo>;
                if (peers == null || players == null || peers.Count == 0) return false;

                // One package per verse rather than per peer: a verse is capped at six, so the
                // peers in one share a list and there is no reason to serialise it twice.
                var byVerse = new Dictionary<int, ZPackage>();

                foreach (ZNetPeer peer in peers)
                {
                    if (peer == null || !peer.IsReady()) continue;

                    int verse = Peers.VerseOf(peer.m_uid);
                    if (!byVerse.TryGetValue(verse, out ZPackage pkg))
                    {
                        var visible = new List<ZNet.PlayerInfo>();
                        foreach (ZNet.PlayerInfo player in players)
                        {
                            int of = VerseOfPlayer(player, peers);
                            if (of == Everyone || of == verse) visible.Add(player);
                        }
                        pkg = _writePlayerInfo.Invoke(__instance, new object[] { visible }) as ZPackage;
                        byVerse[verse] = pkg;
                    }

                    if (pkg != null) peer.m_rpc.Invoke("PlayerList", pkg);
                }

                return false;
            }

            /// <summary>
            /// A <c>PlayerInfo</c> carries no peer UID, so it is matched back to a connection
            /// through its character ZDO - the same field clients use to address chat. An
            /// entry that matches no peer is treated as belonging to every verse, which is
            /// what keeps a server-side synthetic entry (another plugin's NPC, for instance)
            /// visible to everyone.
            /// </summary>
            private const int Everyone = -1;

            private static int VerseOfPlayer(ZNet.PlayerInfo player, List<ZNetPeer> peers)
            {
                foreach (ZNetPeer peer in peers)
                {
                    if (peer != null && peer.m_characterID == player.m_characterID)
                        return Peers.VerseOf(peer.m_uid);
                }
                return Everyone;
            }
        }
    }
}
