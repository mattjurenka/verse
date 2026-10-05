using System;

namespace Verse
{
    /// <summary>
    /// The one spelling of a player's account that the verse registry is keyed by, whatever
    /// socket the connection arrived on.
    ///
    /// <para>Deliberately free of Unity, Harmony and the game assembly so the tests can compile
    /// it directly — the same reason <c>CommandText</c> and <c>SleepRule</c> live apart from
    /// their callers. <see cref="Peers.PlatformId"/> is the only caller.</para>
    /// </summary>
    internal static class AccountId
    {
        /// <summary>Steam's prefix in a <c>PlatformUserID</c>, which is <c>$"{platform}_"</c>.</summary>
        private const string SteamPrefix = "Steam_";

        /// <summary>
        /// Reduces whatever the socket called the account to the registry's spelling.
        ///
        /// <para>The two socket types disagree, and the difference stays invisible until the
        /// server's backend changes under a world that already has verses in it:</para>
        ///
        /// <code>
        /// ZSteamSocket.GetHostName()   =>  m_peerID.GetSteamID().ToString()   // "76561198033210929"
        /// ZPlayFabSocket.GetHostName() =>  m_platformPlayerId.ToString()      // "Steam_76561198033210929"
        /// </code>
        ///
        /// <para>because <c>PlatformUserID.ToString()</c> returns
        /// <c>GetPlatformPrefix(m_platform) + m_userID</c>. Same account, two strings. Adding
        /// <c>-crossplay</c> to a running server swaps <c>ZSteamSocket</c> for
        /// <c>ZPlayFabSocket</c>, so without this every returning player would miss their entry
        /// in <c>verses.json</c>, be treated as new by <c>Verses.OfOrCreate</c>, and be handed a
        /// fresh empty world while their real one sat there orphaned. Sixteen verses, the
        /// migrated legacy world among them, were one restart away from exactly that.</para>
        ///
        /// <para>The bare form wins because it is what the registry on disk already holds, so
        /// there is nothing to migrate. Only Steam's prefix is stripped: every other platform
        /// keeps its own, which is what stops an Xbox user ID from ever being read as the Steam
        /// ID of the same digits.</para>
        /// </summary>
        internal static string Canonical(string host)
        {
            if (string.IsNullOrEmpty(host)) return host;

            return host.StartsWith(SteamPrefix, StringComparison.Ordinal)
                ? host.Substring(SteamPrefix.Length)
                : host;
        }
    }
}
