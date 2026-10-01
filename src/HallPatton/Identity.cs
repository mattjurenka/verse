using Splatform;
using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// The identity Mark's chat lines are sent under, and the entry the server adds to the
    /// player list on his behalf.
    ///
    /// The player-list entry is not decoration - two vanilla behaviours depend on it:
    ///
    /// 1. Hearing. A client sends what you type only to the players the server has told it
    ///    about (<c>Chat.CheckPermissionsAndSendChatMessageRPCsAsync</c> walks
    ///    <c>ZNet.GetPlayerList()</c>). With one player online and no entry for Mark, chat
    ///    never leaves that client. The entry's character ID belongs to the server, so those
    ///    messages are addressed to us.
    ///
    /// 2. His name. <c>Chat.AddString</c> resolves the name on a chat line by looking the
    ///    sender's platform ID up in the player list, and drops the line entirely if it is
    ///    not there. With the entry present his lines read "Mark Hall-Patton: ...".
    ///
    /// The platform in <see cref="UserId"/> is deliberately not "Steam" or "PlayFab": a
    /// receiving client runs the sender through <c>RelationsManager.CheckPermissionAsync</c>,
    /// which asks its own platform for a user profile. An ID from another platform is refused
    /// as DifferentPlatformsNotAvailable, which the permission check treats as granted, so the
    /// message goes through. A made-up ID on the client's *own* platform would instead be
    /// looked up for real and could come back as an error, which drops the message.
    /// </summary>
    internal static class Identity
    {
        /// <summary>Clark County Museum, 1830 S. Boulder Highway.</summary>
        internal static readonly PlatformUserID UserId = new PlatformUserID("Museum", "1830");

        internal static string Name =>
            string.IsNullOrWhiteSpace(Plugin.DisplayName.Value)
                ? "Mark Hall-Patton"
                : Plugin.DisplayName.Value.Trim();

        /// <summary>Fresh each time: <c>UserInfo</c> is a class and the game mutates it.</summary>
        internal static UserInfo Info => new UserInfo { Name = Name, UserId = UserId };

        /// <summary>
        /// Appended to the list the server broadcasts. The character ID is Mark's own ZDO when
        /// he is out; otherwise a placeholder that still carries the server's session ID, which
        /// is the part that routes chat to us. <c>uint.MaxValue</c> because the server hands out
        /// ZDO IDs from 1 upwards and will not reach it, so this can never collide with a real
        /// object.
        /// </summary>
        internal static ZNet.PlayerInfo PlayerEntry()
        {
            ZDOID character = Historian.IsOut
                ? Historian.Id
                : new ZDOID(ZDOMan.GetSessionID(), uint.MaxValue);

            return new ZNet.PlayerInfo
            {
                m_name = Name,
                m_characterID = character,
                m_userInfo = new ZNet.CrossNetworkUserInfo
                {
                    m_id = UserId,
                    m_displayName = Name,
                    m_serverAssignedDisplayName = Name,
                    m_playfabId = ""
                },
                // No map pin: he is not a player, and a pin nobody can follow is just noise.
                m_publicPosition = false,
                m_position = Vector3.zero
            };
        }
    }
}
