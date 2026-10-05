using System;

namespace Verse
{
    /// <summary>
    /// The name the server hands out for a connection, and the badge a server admin wears in
    /// it.
    ///
    /// <para><b>Why a name is something the server decides at all.</b> The name on a chat line
    /// does not come from the chat message. <c>Terminal.AddString(PlatformUserID, ...)</c>
    /// takes only the sender's platform id, looks it up in the player list, and draws
    /// <c>playerInfo.m_name</c> - so the one place a vanilla client will read a name from is
    /// the <c>PlayerList</c> package this server builds. That is the whole reason a badge is
    /// possible without touching the client, and it is also why the badge turns up in the
    /// player panel and on map pins: they read the same list.</para>
    ///
    /// <para><b>The colour works because the client is already composing rich text.</b> The
    /// chat line is built as <c>"&lt;color=orange&gt;" + name + "&lt;/color&gt;: ..."</c> into
    /// a <c>TextMeshProUGUI</c>, and TMP keeps a colour stack, so a nested
    /// <c>&lt;color=red&gt;...&lt;/color&gt;</c> inside the name pops back to orange for the
    /// rest of it rather than falling through to white.</para>
    ///
    /// <para><b>Which is exactly why everybody else's name is scrubbed.</b> A character name
    /// is whatever the client said it was in <c>RPC_PeerInfo</c> - nothing validates it beyond
    /// three characters, and chat text has its angle brackets replaced on arrival
    /// (<c>Chat.OnNewChatMessage</c>) while names never do. So a player who could put
    /// <c>&lt;color=red&gt;[ADMIN]&lt;/color&gt;</c> in their name would be indistinguishable
    /// from the real thing, and a badge anyone can wear is not a badge. Angle brackets and a
    /// literal <c>[ADMIN]</c> come out of every name the server did not put them in.</para>
    ///
    /// <para>Unity-free on purpose, like <see cref="AccountId"/> and <see cref="CommandText"/>,
    /// so <c>tests/TextTests</c> compiles this very file rather than a copy of it.</para>
    /// </summary>
    internal static class AdminName
    {
        /// <summary>What the badge says.</summary>
        internal const string Tag = "[ADMIN]";

        /// <summary>
        /// TMP takes any named colour or <c>#rrggbb</c>. Red against the chat window's orange
        /// name and white body is the one that reads as "this one is different".
        /// </summary>
        internal const string Colour = "red";

        /// <summary>The badge exactly as it goes on the wire, trailing space included.</summary>
        internal const string Badge = "<color=" + Colour + ">" + Tag + "</color> ";

        /// <summary>
        /// Vanilla's own stand-in for a name it cannot read (<c>Player.GetPlayerName</c>), so
        /// a name that was nothing but markup still leaves something to address.
        /// </summary>
        internal const string Unnamed = "...";

        /// <summary>
        /// The name to broadcast for a player, badge and all.
        ///
        /// <para>Idempotent in both directions: the badge is taken off before the decision is
        /// made, so running this over an already-decorated name neither doubles it nor leaves
        /// it on somebody who has since dropped off the admin list.</para>
        /// </summary>
        internal static string Of(string name, bool admin)
        {
            string plain = Plain(Unbadge(name));
            return admin ? Badge + plain : plain;
        }

        /// <summary>
        /// Whether a name is trying to look like an admin's without being one. Nothing in the
        /// server depends on this - it is here so the log can say who tried.
        /// </summary>
        internal static bool Claims(string name) =>
            !string.IsNullOrEmpty(name) &&
            name.IndexOf(Tag, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// A name with nothing in it that could forge a badge: no angle brackets, so no rich
        /// text of any kind, and no literal tag either.
        /// </summary>
        private static string Plain(string name)
        {
            if (string.IsNullOrEmpty(name)) return Unnamed;

            string clean = name.Replace("<", "").Replace(">", "");

            // Removed rather than blanked out, which vanilla's own `Replace('<', ' ')` on chat
            // text cannot do because it has to keep words apart. A name has nothing to keep
            // apart, and a row of spaces where the markup used to be looks like a bug.
            for (int at = clean.IndexOf(Tag, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = clean.IndexOf(Tag, StringComparison.OrdinalIgnoreCase))
                clean = clean.Remove(at, Tag.Length);

            clean = clean.Trim();
            return clean.Length == 0 ? Unnamed : clean;
        }

        /// <summary>Takes off a badge this server put on, and only that exact spelling.</summary>
        private static string Unbadge(string name) =>
            name != null && name.StartsWith(Badge, StringComparison.Ordinal)
                ? name.Substring(Badge.Length)
                : name;
    }
}
