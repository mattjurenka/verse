using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Splatform;
using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// The talking: what to do with a chat message the server has overheard, and the
    /// per-player threads that let him follow a conversation.
    ///
    /// Everything here is driven by <see cref="Patches.ZRoutedRpc_HandleRoutedRPC_Patch"/>,
    /// which is where messages arrive.
    /// </summary>
    internal static class Conversation
    {
        private sealed class Thread
        {
            public readonly List<MuseClient.Turn> History = new List<MuseClient.Turn>();
            public bool Waiting;
            public float LastBusyLine;
            public float LastAsked = -999f;
            /// <summary>When each question in the last hour was asked.</summary>
            public readonly List<float> Recent = new List<float>();
        }

        private static readonly Dictionary<long, Thread> Threads = new Dictionary<long, Thread>();

        /// <summary>When he was last spoken to, for the idle timer.</summary>
        private static float _lastAttention;

        /// <summary>Model calls made today, against Limits.GlobalDaily.</summary>
        private static int _spentToday;
        private static int _dayStamp = -1;

        // A message is delivered once per recipient, so on a listen server the host's own line
        // arrives twice - once for them, once for Mark. Remember the last one briefly.
        private static long _echoSender;
        private static string _echoText = "";
        private static float _echoAt = -99f;

        /// <summary>Called for every chat message addressed to the server.</summary>
        internal static void Heard(
            long senderPeer, ZDOID speakerZdo, UserInfo who, string text, Vector3 fallbackPos)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // Never answer ourselves: on a listen server Mark's own lines come back through
            // this same path, and answering them would be a conversation with no exit.
            if (who != null && who.UserId == Identity.UserId) return;

            if (IsEcho(senderPeer, text)) return;

            Vector3 where = SpeakerPosition(senderPeer, speakerZdo, fallbackPos);
            string speaker = who != null && !string.IsNullOrWhiteSpace(who.Name) ? who.Name : "someone";

            CommandKind kind = Command.Parse(Plugin.CommandWord.Value, text, out string asked);

            Diagnostics.HeardCount++;
            Diagnostics.Verbose(
                $"heard from \"{speaker}\" (peer {senderPeer}) at {Diagnostics.Round(where)}: " +
                $"\"{text}\" -> {kind}" +
                (Historian.IsOut
                    ? $", he is {(where - Historian.Position).magnitude:0.#}m away"
                    : ", he is not out"));

            switch (kind)
            {
                case CommandKind.Help:
                    ServerChat.SayTo(senderPeer, Dialogue.Help(Plugin.CommandWord.Value), where);
                    return;

                case CommandKind.Debug:
                    foreach (string line in Diagnostics.Report(senderPeer))
                        ServerChat.SayTo(senderPeer, line, where);
                    return;

                case CommandKind.Lookup:
                    Knowledge.EnsureBuilt();
                    List<string> found = Knowledge.Lookup(asked, Plugin.KnowledgeMax.Value);
                    if (found.Count == 0)
                    {
                        ServerChat.SayTo(senderPeer,
                            $"Nothing in my notes for \"{asked}\" - {Knowledge.Count} entries to look through.",
                            where);
                        return;
                    }
                    foreach (string line in found)
                        ServerChat.SayTo(senderPeer, ReplyText.Sanitize(line, 220), where);
                    return;

                case CommandKind.Summon:
                    Summon(senderPeer, speaker, where);
                    return;

                case CommandKind.Dismiss:
                    if (!Historian.IsOut) return;
                    ServerChat.Broadcast(Dialogue.Leaving(), ServerChat.Mouth);
                    Historian.Dismiss();
                    return;

                case CommandKind.Stay:
                    if (!Historian.IsOut) return;
                    Historian.Stay = true;
                    _lastAttention = Time.time;
                    ServerChat.Broadcast("I'll wait here. Plenty to look at.", ServerChat.Mouth);
                    return;

                case CommandKind.Ask:
                    // Asked by name, so answered wherever they are standing.
                    if (!Historian.IsOut)
                    {
                        ServerChat.SayTo(senderPeer,
                            $"I'm not with you yet - type '{Plugin.CommandWord.Value}' and I'll come over.",
                            where);
                        return;
                    }
                    Answer(senderPeer, speaker, asked, where);
                    return;

                default:
                    // Ordinary chat: he answers what is said near him, and ignores the rest.
                    if (!Historian.IsOut) return;
                    float earshot = Mathf.Max(3f, Plugin.Earshot.Value);
                    if ((where - Historian.Position).sqrMagnitude > earshot * earshot) return;
                    Answer(senderPeer, speaker, text.Trim(), where);
                    return;
            }
        }

        /// <summary>True when he has been ignored for longer than the config allows.</summary>
        internal static bool IdleTooLong()
        {
            float minutes = Plugin.IdleDismissMinutes.Value;
            if (minutes <= 0f) return false;
            return Time.time - _lastAttention > minutes * 60f;
        }

        /// <summary>Drops the threads of players who have disconnected.</summary>
        internal static void Tick()
        {
            if (Threads.Count == 0 || ZNet.instance == null) return;

            List<long> gone = null;
            foreach (long peer in Threads.Keys)
            {
                if (peer == ZDOMan.GetSessionID()) continue;          // the listen-server host
                if (ZNet.instance.GetPeer(peer) != null) continue;
                (gone ?? (gone = new List<long>())).Add(peer);
            }

            if (gone == null) return;
            foreach (long peer in gone) Threads.Remove(peer);
        }

        // --- internals ---

        private static void Summon(long peerId, string speakerName, Vector3 playerPos)
        {
            Quaternion facing = Quaternion.identity;
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(peerId) : null;
            if (peer != null && peer.m_characterID != ZDOID.None)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (zdo != null) facing = zdo.GetRotation();
            }

            bool wasOut = Historian.IsOut;
            if (!Historian.Summon(peerId, speakerName, playerPos, facing))
            {
                ServerChat.SayTo(peerId, "Something is wrong at this end - see the server log.", playerPos);
                return;
            }

            Historian.Stay = false;
            _lastAttention = Time.time;

            // Coming back over when he is already out does not need re-introducing.
            ServerChat.Broadcast(wasOut ? "Right - I'm with you." : Dialogue.Greeting(), ServerChat.Mouth);
        }

        private static void Answer(long peerId, string speakerName, string question, Vector3 where)
        {
            if (string.IsNullOrWhiteSpace(question)) return;

            _lastAttention = Time.time;
            Diagnostics.AnsweredCount++;
            Thread thread = ThreadFor(peerId);

            if (thread.Waiting)
            {
                // One line, not one per message, if somebody keeps typing while he thinks.
                if (Time.time - thread.LastBusyLine > 10f)
                {
                    thread.LastBusyLine = Time.time;
                    ServerChat.SayTo(peerId, Dialogue.TooBusy(), ServerChat.Mouth);
                }
                return;
            }

            // Every model call is money and chat has no natural brake, so a player who holds
            // down Enter is a billing event unless something stops them.
            float cooldown = Mathf.Max(0f, Plugin.CooldownSeconds.Value);
            if (Time.time - thread.LastAsked < cooldown)
            {
                if (Time.time - thread.LastBusyLine > 10f)
                {
                    thread.LastBusyLine = Time.time;
                    ServerChat.SayTo(peerId, Dialogue.TooSoon(), ServerChat.Mouth);
                }
                return;
            }

            thread.Recent.RemoveAll(t => Time.time - t > 3600f);
            int hourly = Plugin.PerPlayerHourly.Value;
            if (hourly > 0 && thread.Recent.Count >= hourly)
            {
                if (Time.time - thread.LastBusyLine > 30f)
                {
                    thread.LastBusyLine = Time.time;
                    ServerChat.SayTo(peerId, Dialogue.Enough(), ServerChat.Mouth);
                    Diagnostics.Log($"peer {peerId} hit the hourly limit of {hourly}");
                }
                return;
            }

            thread.LastAsked = Time.time;
            thread.Recent.Add(Time.time);

            // Past the daily budget he keeps answering, from the built-in lines. A server
            // that degrades is better than one that goes silent.
            bool budget = BudgetLeft();
            if (!budget) Diagnostics.Verbose("daily model budget is spent; using built-in lines");

            if (!Plugin.MuseEnabled.Value || !MuseClient.Configured || Plugin.Instance == null || !budget)
            {
                // Built-in lines wait too: an instant answer reads like a vending machine.
                if (Plugin.Instance == null) ServerChat.Speak(Dialogue.Fallback(question), peerId);
                else Plugin.Instance.StartCoroutine(SpeakAfterPause(Dialogue.Fallback(question), peerId));
                return;
            }

            SpendOne();
            thread.Waiting = true;
            Plugin.Instance.StartCoroutine(AskMuse(peerId, speakerName, question, thread));
        }

        private static IEnumerator AskMuse(
            long peerId, string speakerName, string question, Thread thread)
        {
            string reply = null;
            string failure = null;
            bool done = false;

            float started = Time.time;
            float pause = Mathf.Clamp(Plugin.ThinkSeconds.Value, 0f, 30f);

            // Run the request alongside the clock rather than in front of it, so the pause is
            // a pause and not a delay added to however long the model took.
            Plugin.Instance.StartCoroutine(Run(
                MuseClient.Ask(question, Context(speakerName, question), thread.History,
                    r => reply = r, f => failure = f),
                () => done = true));

            while (!done && Time.time - started < pause) yield return null;

            // Only worth saying he is thinking if he is still thinking.
            if (!done && Plugin.Acknowledge.Value) ServerChat.Speak(Dialogue.Thinking(), peerId);

            while (!done) yield return null;

            // An answer that beat the clock waits out the rest of the pause.
            float left = pause - (Time.time - started);
            if (left > 0f) yield return new WaitForSeconds(left);

            thread.Waiting = false;

            if (reply == null)
            {
                Plugin.Log.LogWarning($"answer failed ({failure}); using a built-in line");
                Diagnostics.LastFailure = failure;
                Diagnostics.LastFailureAt = Time.time;
                reply = Dialogue.Fallback(question);
            }
            else
            {
                Remember(thread, question, reply);
            }

            // He may have been dismissed, or walked off, while the request was in flight.
            if (!Historian.IsOut)
            {
                ServerChat.SpeakTo(peerId, reply, PeerMouth(peerId));
                yield break;
            }

            ServerChat.Speak(reply, peerId);
        }

        /// <summary>Model calls left in today.s budget.</summary>
        internal static bool BudgetLeft()
        {
            int cap = Plugin.GlobalDaily.Value;
            if (cap <= 0) return true;
            RollDay();
            return _spentToday < cap;
        }

        internal static string BudgetState()
        {
            RollDay();
            int cap = Plugin.GlobalDaily.Value;
            return cap <= 0 ? $"{_spentToday} today, no cap" : $"{_spentToday}/{cap} today";
        }

        private static void SpendOne()
        {
            RollDay();
            _spentToday++;
        }

        /// <summary>Resets the budget at UTC midnight.</summary>
        private static void RollDay()
        {
            int today = DateTime.UtcNow.DayOfYear;
            if (today == _dayStamp) return;
            if (_dayStamp >= 0) Diagnostics.Log($"new day; {_spentToday} model calls yesterday");
            _dayStamp = today;
            _spentToday = 0;
        }

        /// <summary>Runs a coroutine to completion and then reports back.</summary>
        private static IEnumerator Run(IEnumerator inner, Action done)
        {
            yield return inner;
            done();
        }

        /// <summary>The same pause the model path uses, for the lines that need no model.</summary>
        private static IEnumerator SpeakAfterPause(string text, long peerId)
        {
            float pause = Mathf.Clamp(Plugin.ThinkSeconds.Value, 0f, 30f);
            if (pause > 0f) yield return new WaitForSeconds(pause);
            ServerChat.Speak(text, peerId);
        }

        private static void Remember(Thread thread, string question, string reply)
        {
            int keep = Mathf.Max(0, Plugin.HistoryTurns.Value);
            if (keep == 0)
            {
                thread.History.Clear();
                return;
            }

            thread.History.Add(new MuseClient.Turn { Player = question, Mark = reply });
            while (thread.History.Count > keep) thread.History.RemoveAt(0);
        }

        /// <summary>
        /// A compact note on who is asking and what is true around them, so an answer can sit
        /// in the moment rather than in the abstract.
        /// </summary>
        /// <summary>
        /// The bracketed note that rides along with a question: who is asking, what the world
        /// is doing, and whatever the world's own data has to say about what they asked.
        /// </summary>
        private static string Context(string speakerName, string question)
        {
            var parts = new List<string>(4) { "asked by " + speakerName };

            if (Historian.IsOut)
            {
                // Straight from the world generator: a dedicated server has no terrain loaded
                // out where the players are, so Heightmap.FindBiome would answer "None".
                if (WorldGenerator.instance != null)
                    parts.Add("biome " + WorldGenerator.instance.GetBiome(Historian.Position));
                // Time only. EnvMan derives night from ZNet.GetTimeSeconds, which is right
                // everywhere, but its weather comes from GetBiome, which reads the main
                // camera - a dedicated server has none, so IsWet would be the weather in an
                // empty Meadows rather than the weather over the player.
                if (EnvMan.instance != null) parts.Add(EnvMan.IsNight() ? "night" : "day");
            }

            string note = string.Join(", ", parts.ToArray());

            List<string> reference = Knowledge.Lookup(question, Plugin.KnowledgeMax.Value);
            if (reference.Count == 0)
            {
                Diagnostics.Verbose("knowledge: nothing matched that question");
                return note;
            }

            Diagnostics.Verbose(
                $"knowledge: {reference.Count} of {Knowledge.Count} entries matched, best: " +
                reference[0]);

            var sb = new StringBuilder(note);
            sb.Append("\nrecords from this world, correct for this version - use these numbers ")
              .Append("rather than your memory:");
            foreach (string line in reference) sb.Append("\n- ").Append(line);
            return sb.ToString();
        }

        private static Thread ThreadFor(long peerId)
        {
            if (Threads.TryGetValue(peerId, out Thread thread)) return thread;
            thread = new Thread();
            Threads[peerId] = thread;
            return thread;
        }

        private static bool IsEcho(long senderPeer, string text)
        {
            bool repeat = senderPeer == _echoSender
                          && text == _echoText
                          && Time.time - _echoAt < 1f;

            _echoSender = senderPeer;
            _echoText = text;
            _echoAt = Time.time;
            return repeat;
        }

        private static Vector3 SpeakerPosition(long senderPeer, ZDOID speakerZdo, Vector3 fallback)
        {
            if (speakerZdo != ZDOID.None && ZDOMan.instance != null)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(speakerZdo);
                if (zdo != null) return zdo.GetPosition();
            }

            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(senderPeer) : null;
            if (peer != null) return ServerChat.PeerPosition(peer);

            return fallback;
        }

        private static Vector3 PeerMouth(long peerId)
        {
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(peerId) : null;
            return peer != null ? ServerChat.PeerPosition(peer) + Vector3.up * 1.7f : Vector3.zero;
        }
    }
}
