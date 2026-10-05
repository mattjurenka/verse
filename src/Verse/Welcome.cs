using System.Collections.Generic;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// What a player is told the first time they ever arrive.
    ///
    /// <para><b>Why this is not cosmetic.</b> Somebody who joins from a clip lands in an empty
    /// private world with no idea that it is private, that it is theirs, that they can invite
    /// anybody, or that the arena they came for is a command away. Nothing on screen says any of
    /// it: the whole plugin is server-side, so there is no UI to put it in, and chat is the only
    /// channel a vanilla client will show. A new arrival with no explanation concludes the server
    /// is empty and leaves, which wastes every bit of the traffic the videos bring.</para>
    ///
    /// <para><b>Once per account, for ever.</b> Recorded in the registry next to everything else
    /// that has to survive a reconnect, because the disconnect-and-rejoin that changing verse is
    /// built on would otherwise replay the introduction every time.</para>
    ///
    /// <para><b>And deliberately late.</b> It waits for the player's character to exist and then
    /// a few seconds more. Lines sent during the loading screen or mid-teleport are shown to a
    /// player who is looking at a fade, and the one thing this has to do is be read.</para>
    /// </summary>
    internal static class Welcome
    {
        /// <summary>How long after a character appears the lines are sent, in seconds.</summary>
        private const float Settle = 6f;

        private const float TickEvery = 0.5f;

        private struct Pending { internal string Account; internal float DueAt; }

        private static readonly Dictionary<long, ZDOID> Seen = new Dictionary<long, ZDOID>();
        private static readonly Dictionary<long, Pending> Waiting = new Dictionary<long, Pending>();
        private static readonly List<long> Ready = new List<long>();

        private static float _timer;

        internal static void Tick(float dt)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!VersePlugin.IntroduceNewPlayers.Value) return;

            _timer += dt;
            if (_timer < TickEvery) return;
            _timer = 0f;

            float now = Time.time;
            List<ZNetPeer> peers = ZNet.instance.GetPeers();

            for (int i = 0; i < peers.Count; i++)
            {
                ZNetPeer peer = peers[i];
                if (peer == null || peer.m_characterID == ZDOID.None) continue;

                // A body we have not noticed before - a first join, or a respawn.
                if (Seen.TryGetValue(peer.m_uid, out ZDOID previous) && previous == peer.m_characterID)
                    continue;

                Seen[peer.m_uid] = peer.m_characterID;

                string account = Peers.PlatformId(peer);
                if (string.IsNullOrEmpty(account)) continue;
                if (Verses.WasIntroduced(account)) continue;
                if (Waiting.ContainsKey(peer.m_uid)) continue;

                Waiting[peer.m_uid] = new Pending { Account = account, DueAt = now + Settle };
            }

            if (Waiting.Count == 0) return;

            Ready.Clear();
            foreach (KeyValuePair<long, Pending> entry in Waiting)
                if (entry.Value.DueAt <= now) Ready.Add(entry.Key);

            for (int i = 0; i < Ready.Count; i++)
            {
                long uid = Ready[i];
                Pending pending = Waiting[uid];
                Waiting.Remove(uid);

                // They left during the wait; they will be introduced next time instead.
                if (ZNet.instance.GetPeer(uid) == null) continue;

                // Marked before speaking, not after: if a line throws, saying it twice is worse
                // than not saying it at all.
                Verses.Introduce(pending.Account);

                int verse = Verses.Of(pending.Account);
                VerseIdentity.SayTo(uid, Lines(verse));

                VersePlugin.Log.LogInfo($"introduced {pending.Account} to verse {verse}");
            }
        }

        /// <summary>
        /// Six lines, in the order the questions actually get asked.
        ///
        /// <para>The first line has to answer "why is nobody here", because that is what a new
        /// arrival is looking at and the wrong conclusion is that the server is dead. The arena
        /// comes third rather than first: it is what brought them, but the private world is what
        /// the server is, and somebody who only learns about the arena treats this as an arena
        /// server.</para>
        /// </summary>
        private static string[] Lines(int verse)
        {
            string word = string.IsNullOrWhiteSpace(VersePlugin.CommandWord.Value)
                ? "!verse" : VersePlugin.CommandWord.Value.Trim();
            string arena = string.IsNullOrWhiteSpace(VersePlugin.ArenaCommandWord.Value)
                ? "!arena" : VersePlugin.ArenaCommandWord.Value.Trim();
            string warp = string.IsNullOrWhiteSpace(VersePlugin.WarpCommandWord.Value)
                ? "!warp" : VersePlugin.WarpCommandWord.Value.Trim();

            var lines = new List<string>
            {
                $"Welcome. This world is yours alone - you are in verse {verse}, and nobody else " +
                "can see it, reach it or build in it. If it looks empty, that is why.",

                $"Your friends can join you in it: {word} open lets anyone in, or {word} invite " +
                $"<name> for one person. Type {word} to see where you stand.",
            };

            if (VersePlugin.ArenaEnabled.Value)
                lines.Add($"{arena} takes you to the challenge arena - {ArenaRules.Waves} waves of " +
                          "monsters, and a full set of gear lent to you at the gate, yours to keep.");

            lines.Add($"{warp} spawn brings you home from anywhere; {warp} bed goes to a bed you have slept in.");
            lines.Add($"Everything else: {word} help" +
                      (VersePlugin.ArenaEnabled.Value ? $" and {arena} help." : "."));

            return lines.ToArray();
        }
    }
}
