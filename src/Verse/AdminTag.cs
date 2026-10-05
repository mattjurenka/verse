using System.Collections.Generic;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// Puts a red <c>[ADMIN]</c> in front of a server admin's name, everywhere a vanilla
    /// client draws one.
    ///
    /// <para><b>One write, four places.</b> <see cref="AdminName"/> explains why the only name
    /// a client will read comes from the <c>PlayerList</c> package: rewriting the server's copy
    /// of it puts the badge on the chat line, in the player panel, on the map pin of anybody
    /// sharing their position, and in the floating shout text - without a client-side mod,
    /// because the client was already going to draw whatever this list said.</para>
    ///
    /// <para><b>What is deliberately not rewritten is <c>ZNetPeer.m_playerName</c>.</b> That
    /// field is the server's own handle on a player: <c>ZNet.GetPeerByPlayerName</c> resolves
    /// it, <c>!verse invite &lt;name&gt;</c> goes through it, and every log line the game
    /// prints about a connection uses it. Decorating it would mean an admin could no longer be
    /// invited, warped or kicked by name, and the journal - which <c>valpanel</c> parses for
    /// the player history - would start carrying markup. So the badge exists only on the copy
    /// that leaves the machine, and <c>UpdatePlayerList</c> rebuilds that copy from the
    /// undecorated field on every pass, which is also why it can never stack up.</para>
    ///
    /// <para><b>Admin is vanilla's own notion</b> - <c>adminlist.txt</c> beside the world save,
    /// re-read every ten seconds - so promoting somebody shows up on their next chat line
    /// without a restart, and demoting them takes it away just as quickly. The list is keyed by
    /// platform id, which is the one spelling <see cref="AccountId"/> exists to settle.</para>
    /// </summary>
    internal static class AdminTag
    {
        internal static bool Active => VersePlugin.AdminTagEnabled.Value;

        /// <summary>
        /// Connections already reported for wearing a badge they are not entitled to.
        ///
        /// <para><c>UpdatePlayerList</c> runs on a timer, and a client's name is fixed for the
        /// life of its connection (<c>RPC_PeerInfo</c> sets it once), so without this the same
        /// attempt would be logged every few seconds forever - into a journal <c>valpanel</c>
        /// tails and puts in a browser.</para>
        /// </summary>
        private static readonly HashSet<long> _reported = new HashSet<long>();

        /// <summary>
        /// Rewrites the live player list in place. <c>ZNet.PlayerInfo</c> is a struct, so the
        /// entry has to go back into the list - the same copy-out, copy-back vanilla does one
        /// loop earlier when it fills in <c>m_serverAssignedDisplayName</c>.
        /// </summary>
        internal static void Apply(ZNet net)
        {
            if (net == null) return;

            List<ZNet.PlayerInfo> players = net.GetPlayerList();
            List<ZNetPeer> peers = net.GetPeers();
            if (players == null || peers == null) return;

            if (_reported.Count > 0) _reported.RemoveWhere(uid => net.GetPeer(uid) == null);

            for (int i = 0; i < players.Count; i++)
            {
                ZNet.PlayerInfo entry = players[i];

                // An entry with no connection behind it is not a player - it is a server-side
                // phantom like Mark, whose name belongs to whatever put it there.
                ZNetPeer peer = PeerOf(entry, peers);
                if (peer == null) continue;

                bool admin = Verses.IsServerAdmin(Peers.PlatformId(peer));
                string shown = AdminName.Of(entry.m_name, admin);
                if (shown == entry.m_name) continue;

                if (!admin && AdminName.Claims(entry.m_name) && _reported.Add(peer.m_uid))
                    VersePlugin.Log.LogWarning(
                        $"Scrubbed an admin badge out of the name \"{entry.m_name}\" " +
                        $"({Peers.PlatformId(peer)}), who is not on the admin list");

                entry.m_name = shown;
                players[i] = entry;
            }
        }

        /// <summary>
        /// Matches a list entry back to its connection by character ZDO, which is what
        /// <see cref="Isolation"/> and HallPatton's diagnostics already use: a
        /// <c>PlayerInfo</c> carries no peer uid.
        /// </summary>
        private static ZNetPeer PeerOf(ZNet.PlayerInfo entry, List<ZNetPeer> peers)
        {
            if (entry.m_characterID == ZDOID.None) return null;

            foreach (ZNetPeer peer in peers)
                if (peer != null && peer.m_characterID == entry.m_characterID) return peer;

            return null;
        }

        /// <summary>
        /// A postfix, so the badge lands after vanilla has built the list and after
        /// <c>UpdatePlayerHistory</c> - called at the end of <c>UpdatePlayerList</c> - has
        /// copied it into the world save. The history only keeps <c>m_userInfo</c>, so a badge
        /// could not reach the <c>.db</c> anyway, but the ordering means it provably does not.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "UpdatePlayerList")]
        internal static class ZNet_UpdatePlayerList_Patch
        {
            private static void Postfix(ZNet __instance)
            {
                if (!Active) return;
                if (__instance == null || !__instance.IsServer()) return;

                Apply(__instance);
            }
        }
    }
}
