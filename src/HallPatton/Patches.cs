using System;
using System.Collections.Generic;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// Everything the plugin hooks. There are only four, and three of them are one-liners.
    /// </summary>
    internal static class Patches
    {
        /// <summary>
        /// Overhears chat addressed to the server.
        ///
        /// Normal and whispered chat travels as a <c>Say</c> RPC on the speaker's own
        /// character, and shouts as a <c>ChatMessage</c> routed RPC; both are addressed to
        /// individual recipients, one copy each. Because <see cref="Identity"/> puts Mark in
        /// the player list with a character ID belonging to the server, clients address a copy
        /// to us, and it arrives here. A dedicated server registers no handler for either
        /// method, so without this prefix the copy is simply dropped.
        ///
        /// Runs as a prefix and never skips the original: on a listen server the host's own
        /// Chat still has to handle its own messages.
        /// </summary>
        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
        internal static class ZRoutedRpc_HandleRoutedRPC_Patch
        {
            private static readonly int SayHash = "Say".GetStableHashCode();
            private static readonly int ChatMessageHash = "ChatMessage".GetStableHashCode();

            private static void Prefix(ZRoutedRpc.RoutedRPCData data)
            {
                if (data == null || data.m_parameters == null) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (ZDOMan.instance == null) return;

                try
                {
                    // Read a copy: the real package is about to be read by whatever the game
                    // does with this message, and its position matters.
                    var pkg = new ZPackage(data.m_parameters.GetArray());

                    if (data.m_methodHash == SayHash)
                    {
                        int type = pkg.ReadInt();
                        var who = new UserInfo();
                        who.Deserialize(ref pkg);
                        string text = pkg.ReadString();

                        if ((Talker.Type)type == Talker.Type.Ping) return;
                        // The target ZDO is the speaker's own character, which is the most
                        // accurate answer to "where are they standing".
                        Conversation.Heard(data.m_senderPeerID, data.m_targetZDO, who, text, Vector3.zero);
                    }
                    else if (data.m_methodHash == ChatMessageHash)
                    {
                        Vector3 position = pkg.ReadVector3();
                        int type = pkg.ReadInt();
                        var who = new UserInfo();
                        who.Deserialize(ref pkg);
                        string text = pkg.ReadString();

                        if ((Talker.Type)type == Talker.Type.Ping) return;
                        Conversation.Heard(data.m_senderPeerID, ZDOID.None, who, text, position);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Could not read a chat message: " + e.Message);
                }
            }
        }

        /// <summary>
        /// Adds Mark to the player list the server broadcasts. See <see cref="Identity"/> for
        /// why this is load-bearing rather than cosmetic: without it clients neither send him
        /// what they type nor know what to call him.
        ///
        /// A postfix, because <c>UpdatePlayerList</c> clears and rebuilds the list from the
        /// connected peers every time it runs.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "UpdatePlayerList")]
        internal static class ZNet_UpdatePlayerList_Patch
        {
            private static void Postfix(ZNet __instance)
            {
                if (!__instance.IsServer() || ZDOMan.instance == null) return;
                __instance.GetPlayerList().Add(Identity.PlayerEntry());
            }
        }

        /// <summary>
        /// Keeps Mark out of the world.s saved player history.
        ///
        /// <c>UpdatePlayerHistory</c> walks the same live player list and copies anything new
        /// into <c>World.m_playerHistory</c>, which is written to the save file - so without
        /// this he is remembered by the world as someone who once visited it. Lifting him out
        /// for the duration of the call is enough, and is why the entry is appended by a
        /// postfix rather than held in the list permanently.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "UpdatePlayerHistory")]
        internal static class ZNet_UpdatePlayerHistory_Patch
        {
            private static void Prefix(ZNet __instance, out bool __state)
            {
                __state = false;
                if (!__instance.IsServer()) return;

                List<ZNet.PlayerInfo> players = __instance.GetPlayerList();
                for (int i = players.Count - 1; i >= 0; i--)
                {
                    if (!(players[i].m_userInfo.m_id == Identity.UserId)) continue;
                    players.RemoveAt(i);
                    __state = true;
                }
            }

            private static void Postfix(ZNet __instance, bool __state)
            {
                if (__state) __instance.GetPlayerList().Add(Identity.PlayerEntry());
            }
        }

        /// <summary>
        /// Takes Mark back out of the player count.
        ///
        /// He has to be in the player list to be able to hear anyone (see <see cref="Identity"/>),
        /// but the count drawn from that list decides two things he has no business deciding:
        /// a server is full at ten players, and the world clock only advances while the count is
        /// above zero. Left alone he would cost a real player their slot and keep time moving in
        /// an empty world.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.GetNrOfPlayers))]
        internal static class ZNet_GetNrOfPlayers_Patch
        {
            private static void Postfix(ZNet __instance, ref int __result)
            {
                if (!__instance.IsServer()) return;

                foreach (ZNet.PlayerInfo player in __instance.GetPlayerList())
                {
                    if (!(player.m_userInfo.m_id == Identity.UserId)) continue;
                    __result--;
                    return;
                }
            }
        }

        /// <summary>
        /// Keeps the vanilla <c>Player</c> component off Mark's back on the machine that owns
        /// him.
        ///
        /// Normally the server never has a GameObject for him at all - it only holds the ZDO.
        /// But a dedicated server does instantiate whatever is near the world origin, so if he
        /// is summoned close to spawn the server builds him too, and then owns a Player. That
        /// is a case the game does not expect: <c>Player.FixedUpdate</c> destroys any Player it
        /// owns that is not the local player, <c>Player.Update</c> would read the keyboard, and
        /// <c>Player.LateUpdate</c> would drag the server's reference position across the map.
        ///
        /// Clients are unaffected either way: they do not own him, so none of these branches
        /// run for them and the patch never fires.
        /// </summary>
        [HarmonyPatch(typeof(Player))]
        internal static class Player_Patches
        {
            [HarmonyPrefix]
            [HarmonyPatch("FixedUpdate")]
            private static bool FixedUpdate(Player __instance) => !IsOurs(__instance);

            [HarmonyPrefix]
            [HarmonyPatch("Update")]
            private static bool Update(Player __instance) => !IsOurs(__instance);

            [HarmonyPrefix]
            [HarmonyPatch("LateUpdate")]
            private static bool LateUpdate(Player __instance) => !IsOurs(__instance);

            private static bool IsOurs(Player player)
            {
                var nview = player.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return false;
                return nview.GetZDO().GetBool(Historian.ZdoFlag);
            }
        }
    }
}
