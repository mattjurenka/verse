using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// Puts words in players' chat windows from a server with no chat of its own.
    ///
    /// The server invokes the vanilla <c>ChatMessage</c> routed RPC directly, once per
    /// recipient, under Mark's identity. On each client that lands in <c>Chat.RPC_ChatMessage</c>
    /// exactly as another player's message would: a line in the chat log, named from the
    /// player-list entry <see cref="Identity"/> supplies, and floating text in the world at the
    /// position we send. No client-side code is involved, which is the whole point.
    ///
    /// Distance is judged here rather than left to the client, because <c>ChatMessage</c> is
    /// the RPC vanilla uses for shouts and clients do not range-check it.
    /// </summary>
    internal static class ServerChat
    {
        /// <summary>Roughly a normal speaking voice, plus room for bystanders.</summary>
        private static float Range => Mathf.Max(10f, Plugin.Earshot.Value + 10f);

        /// <summary>Speech bubbles want a head, and his ZDO position is at his feet.</summary>
        internal static Vector3 Mouth =>
            (Historian.IsOut ? Historian.Position : Vector3.zero) + Vector3.up * 1.7f;

        /// <summary>
        /// A whole answer, from where he stands, to everyone close enough to hear it - plus
        /// <paramref name="alsoTo"/> wherever they are, which is how somebody who asked him by
        /// name from across the map still gets their answer.
        /// </summary>
        internal static void Speak(string text, long alsoTo = 0L)
        {
            Deliver(Lines(text), null, alsoTo);
        }

        /// <summary>A whole answer straight at one player, for when he is not out to say it.</summary>
        internal static void SpeakTo(long peer, string text, Vector3 origin)
        {
            Deliver(Lines(text), origin, peer, onlyTo: true);
        }

        /// <summary>One line, to everyone close enough to hear it.</summary>
        internal static void Broadcast(string line, Vector3 origin)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            foreach (long peer in Recipients(origin, 0L)) Send(peer, line, origin);
        }

        /// <summary>One line, to one player, wherever they are.</summary>
        internal static void SayTo(long peer, string line, Vector3 origin)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Send(peer, line, origin);
        }

        private static List<string> Lines(string text) =>
            ReplyText.Split(ReplyText.Sanitize(text), Plugin.LineLength.Value, Plugin.MaxLines.Value);

        /// <summary>
        /// Sends the lines a couple of seconds apart, so he reads as talking rather than
        /// pasting. A single line needs no pacing, and neither does a server with no
        /// MonoBehaviour left to pace it with.
        /// </summary>
        private static void Deliver(List<string> lines, Vector3? origin, long target, bool onlyTo = false)
        {
            if (lines.Count == 0) return;

            if (lines.Count == 1 || Plugin.Instance == null)
            {
                foreach (string line in lines) SendLine(line, origin, target, onlyTo);
                return;
            }

            Plugin.Instance.StartCoroutine(Paced(lines, origin, target, onlyTo));
        }

        private static IEnumerator Paced(List<string> lines, Vector3? origin, long target, bool onlyTo)
        {
            float gap = Mathf.Clamp(Plugin.LineGapSeconds.Value, 0.5f, 10f);

            for (int i = 0; i < lines.Count; i++)
            {
                SendLine(lines[i], origin, target, onlyTo);
                if (i < lines.Count - 1) yield return new WaitForSeconds(gap);
            }
        }

        private static void SendLine(string line, Vector3? origin, long target, bool onlyTo)
        {
            // Read his position fresh each time: he may be walking while he talks.
            Vector3 at = origin ?? Mouth;

            if (onlyTo)
            {
                Send(target, line, at);
                return;
            }

            foreach (long peer in Recipients(at, target)) Send(peer, line, at);
        }

        private static void Send(long peerId, string line, Vector3 origin)
        {
            if (ZRoutedRpc.instance == null) return;

            ZRoutedRpc.instance.InvokeRoutedRPC(
                peerId, "ChatMessage", origin, (int)Talker.Type.Normal, Identity.Info, line);

            Diagnostics.SentLines++;
            Diagnostics.Verbose($"said to peer {peerId} from {Diagnostics.Round(origin)}: \"{line}\"");
        }

        /// <summary>
        /// Every connected player near <paramref name="origin"/>, and
        /// <paramref name="alsoTo"/> if it is not one of them already.
        /// </summary>
        private static IEnumerable<long> Recipients(Vector3 origin, long alsoTo)
        {
            if (ZNet.instance == null) yield break;

            float rangeSqr = Range * Range;
            bool sentToAlso = false;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                if ((PeerPosition(peer) - origin).sqrMagnitude > rangeSqr) continue;

                if (peer.m_uid == alsoTo) sentToAlso = true;
                yield return peer.m_uid;
            }

            // On a listen server the person hosting is not a peer, and routing to the server.s
            // own ID is handled locally by their own Chat. Tested on IsDedicated rather than
            // on Chat.instance: a dedicated server does have a Chat, it just has no player
            // for it to draw anything for.
            long session = ZDOMan.GetSessionID();
            if (!ZNet.instance.IsDedicated() && Chat.instance != null &&
                (ZNet.instance.GetReferencePosition() - origin).sqrMagnitude <= rangeSqr)
            {
                if (session == alsoTo) sentToAlso = true;
                yield return session;
            }

            if (alsoTo != 0L && !sentToAlso) yield return alsoTo;
        }

        internal static Vector3 PeerPosition(ZNetPeer peer)
        {
            if (peer.m_characterID != ZDOID.None && ZDOMan.instance != null)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (zdo != null) return zdo.GetPosition();
            }
            return peer.m_refPos;
        }
    }
}
