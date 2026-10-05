using System.Collections.Generic;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The entry the server adds to the player list on the plugin's behalf, and how it talks
    /// back to a player.
    ///
    /// The entry is load-bearing rather than cosmetic, for the same reason it is in
    /// HallPatton: a client sends what you type only to the players the server has told it
    /// about. A player alone in their own verse has a player list containing nobody but
    /// themselves, so without an entry here their chat never leaves their machine and no
    /// command could ever be heard. The entry's character ID carries the server's session ID,
    /// which is the part that routes those messages to us.
    ///
    /// The platform is deliberately a made-up one. A receiving client runs a message's sender
    /// through its own platform's relations check; an ID from an unknown platform comes back
    /// as "different platforms not available", which counts as permitted, whereas an invented
    /// ID on the client's *real* platform would be looked up and could fail, dropping the line.
    /// </summary>
    internal static class VerseIdentity
    {
        internal static readonly PlatformUserID UserId = new PlatformUserID("Verse", "1");

        internal static string Name =>
            string.IsNullOrWhiteSpace(VersePlugin.SpeakerName.Value)
                ? "Verse"
                : VersePlugin.SpeakerName.Value.Trim();

        /// <summary>Fresh each time: <c>UserInfo</c> is a class and the game mutates it.</summary>
        internal static UserInfo Info => new UserInfo { Name = Name, UserId = UserId };

        /// <summary>
        /// Appended to the list the server broadcasts. <c>uint.MaxValue - 1</c> rather than
        /// <c>uint.MaxValue</c> so it cannot collide with HallPatton's entry when both plugins
        /// are installed; the server hands out ZDO IDs from 1 upwards and reaches neither.
        /// </summary>
        internal static ZNet.PlayerInfo PlayerEntry() => new ZNet.PlayerInfo
        {
            m_name = Name,
            m_characterID = new ZDOID(ZDOMan.GetSessionID(), uint.MaxValue - 1),
            m_userInfo = new ZNet.CrossNetworkUserInfo
            {
                m_id = UserId,
                m_displayName = Name,
                m_serverAssignedDisplayName = Name,
                m_playfabId = ""
            },
            m_publicPosition = false,
            m_position = Vector3.zero
        };

        /// <summary>
        /// Says something to one player, in their own chat window. Sent as the vanilla
        /// <c>ChatMessage</c> routed RPC so it arrives exactly as another player's line would,
        /// with no client-side code involved.
        /// </summary>
        internal static void SayTo(long peerId, string line)
        {
            if (ZRoutedRpc.instance == null || string.IsNullOrWhiteSpace(line)) return;

            Vector3 at = Where(peerId);
            ZRoutedRpc.instance.InvokeRoutedRPC(
                peerId, "ChatMessage", at, (int)Talker.Type.Normal, Info, line);
        }

        internal static void SayTo(long peerId, IEnumerable<string> lines)
        {
            foreach (string line in lines) SayTo(peerId, line);
        }

        /// <summary>Where that player is, so the line is not spoken from the world origin.</summary>
        private static Vector3 Where(long peerId)
        {
            ZNetPeer peer = ZNet.instance?.GetPeer(peerId);
            if (peer == null) return Vector3.zero;

            if (peer.m_characterID != ZDOID.None && ZDOMan.instance != null)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (zdo != null) return zdo.GetPosition() + Vector3.up * 1.7f;
            }
            return peer.m_refPos;
        }

        // --- the player list ----------------------------------------------------------------

        [HarmonyPatch(typeof(ZNet), "UpdatePlayerList")]
        internal static class ZNet_UpdatePlayerList_Patch
        {
            private static void Postfix(ZNet __instance)
            {
                if (!__instance.IsServer() || ZDOMan.instance == null) return;
                __instance.GetPlayerList().Add(PlayerEntry());
            }
        }

        /// <summary>
        /// Keeps the entry out of the world's saved player history, which
        /// <c>UpdatePlayerHistory</c> copies from the same live list.
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
                    if (!(players[i].m_userInfo.m_id == UserId)) continue;
                    players.RemoveAt(i);
                    __state = true;
                }
            }

            private static void Postfix(ZNet __instance, bool __state)
            {
                if (__state) __instance.GetPlayerList().Add(PlayerEntry());
            }
        }

        /// <summary>
        /// Takes the entry back out of the player count, which decides both when a server is
        /// full and whether the world clock advances. A phantom player should do neither.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.GetNrOfPlayers))]
        internal static class ZNet_GetNrOfPlayers_Patch
        {
            private static void Postfix(ZNet __instance, ref int __result)
            {
                if (!__instance.IsServer()) return;

                foreach (ZNet.PlayerInfo player in __instance.GetPlayerList())
                {
                    if (!(player.m_userInfo.m_id == UserId)) continue;
                    __result--;
                    return;
                }
            }
        }
    }
}
