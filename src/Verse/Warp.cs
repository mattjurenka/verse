using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The <c>!warp</c> command: teleport straight to a known point in your own verse.
    ///
    /// This is <see cref="Commands"/>'s opposite in one respect: a verse change there only
    /// takes effect through a disconnect-and-rejoin, because that is when a client rebuilds
    /// its world from nothing. A warp needs none of that - the player is already connected and
    /// already has a body in the right verse, so it is just <c>RPC_TeleportTo</c> addressed to
    /// their own character, the same primitive <see cref="Spawns"/> uses to land arrivals, sent
    /// once with no settle delay or retries because nobody just reloaded the world.
    /// </summary>
    internal static class Warp
    {
        private static string Word =>
            string.IsNullOrWhiteSpace(VersePlugin.WarpCommandWord.Value)
                ? "!warp"
                : VersePlugin.WarpCommandWord.Value.Trim();

        internal static bool Match(string text, out string rest) =>
            CommandText.Match(Word, text, out rest);

        internal static void Handle(long peer, string account, string rest)
        {
            string[] parts = CommandText.Words(rest);
            switch (CommandText.Verb(parts))
            {
                case "spawn":
                    ToSpawn(peer, account);
                    return;

                case "bed":
                    ToBed(peer, account);
                    return;

                default:
                    VerseIdentity.SayTo(peer, $"Warp where? {Word} spawn or {Word} bed");
                    return;
            }
        }

        private static void ToSpawn(long peer, string account)
        {
            int verse = Verses.Of(account);
            if (verse == Verses.None)
            {
                VerseIdentity.SayTo(peer, "You are not in a verse.");
                return;
            }

            if (!Spawns.Resolve(verse, out Vector3 point))
            {
                VerseIdentity.SayTo(peer, "I do not know where your verse's spawn is yet - try again in a moment.");
                return;
            }

            Teleport(peer, point, "Warping to spawn.");
        }

        private static void ToBed(long peer, string account)
        {
            int verse = Verses.Of(account);
            if (verse == Verses.None)
            {
                VerseIdentity.SayTo(peer, "You are not in a verse.");
                return;
            }

            long playerId = PlayerIdOf(peer);
            if (playerId == 0L)
            {
                VerseIdentity.SayTo(peer, "I cannot tell who you are yet - try again in a moment.");
                return;
            }

            if (!Beds.Find(verse, playerId, out Vector3 point))
            {
                VerseIdentity.SayTo(peer, "You have not claimed a bed yet - interact with one to set it as your spawn point.");
                return;
            }

            Teleport(peer, point, "Warping to your bed.");
        }

        /// <summary>
        /// The same persistent id a claimed bed's owner field holds - see <see cref="Beds"/> -
        /// read off the peer's own character ZDO rather than anything client-held.
        /// </summary>
        private static long PlayerIdOf(long peer)
        {
            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            if (connection == null || connection.m_characterID == ZDOID.None) return 0L;

            ZDO zdo = ZDOMan.instance?.GetZDO(connection.m_characterID);
            return zdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
        }

        private static void Teleport(long peer, Vector3 point, string message)
        {
            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            ZDOID character = connection?.m_characterID ?? ZDOID.None;
            if (character == ZDOID.None)
            {
                VerseIdentity.SayTo(peer, "I cannot find your character right now.");
                return;
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(peer, character, "RPC_TeleportTo",
                point, Quaternion.identity, true);
            VerseIdentity.SayTo(peer, message);
        }
    }
}
