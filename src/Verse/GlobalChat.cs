using System.Collections.Generic;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Makes chat reach everybody on the server, whatever verse they are in and however far
    /// apart they are standing.
    ///
    /// <para><b>Why it was verse-local to begin with,</b> which is not where you would guess.
    /// Vanilla does not broadcast chat. The speaking client enumerates
    /// <c>ZNet.GetPlayerList()</c> and sends one RPC per listener
    /// (<c>Chat.CheckPermissionsAndSendChatMessageRPCsAsync</c>), and <see cref="Isolation"/>
    /// gives each verse its own player list - so a speaker only ever addresses their own
    /// verse. Nothing had to block chat; the sender simply never knew who else was there.</para>
    ///
    /// <para><b>And why distance was the other half.</b> Normal and whispered chat go out as
    /// <c>Say</c> on the speaker's own <c>ZNetView</c>, and the receiving client checks
    /// <c>m_normalDistance</c> (15 m) in <c>Talker.RPC_Say</c> before showing it. Worse,
    /// that RPC is addressed to the speaker's character - an object another verse has never
    /// been sent - so forwarding it across verses could not work even in principle.</para>
    ///
    /// <para><b>What this does instead.</b> It takes the utterance once, at the relay, and
    /// re-sends it to every connected peer as a <c>ChatMessage</c>, which is the shout path:
    /// self-contained, addressed to no object, and shown by
    /// <c>Chat.OnNewChatMessage</c> → <c>AddString</c> with no distance check at all. The
    /// original chat type is preserved, so a whisper still reads as a whisper - it simply
    /// reaches everybody.</para>
    ///
    /// <para>One visible difference: normal chat no longer renders as a speech bubble
    /// attached to the speaker's head, because that only happens on the <c>Say</c> path we are
    /// replacing. It appears as floating text at the speaker's position instead, which is how
    /// server-sent lines have always looked.</para>
    /// </summary>
    internal static class GlobalChat
    {
        private static readonly int SayHash = "Say".GetStableHashCode();
        private static readonly int ChatHash = "ChatMessage".GetStableHashCode();

        /// <summary>
        /// The last utterance fanned out. One thing said arrives here once per listener the
        /// speaker could see, so the copies after the first have to be dropped or everybody
        /// hears it N times.
        /// </summary>
        private static long _lastSender;
        private static string _lastText;
        private static int _lastType;
        private static float _lastAt;

        internal static bool Active => VersePlugin.GlobalChat.Value;

        /// <summary>
        /// Returns true when this message has been dealt with and vanilla's relay should not
        /// also carry it.
        /// </summary>
        internal static bool Deliver(ZRoutedRpc.RoutedRPCData data)
        {
            if (!Active || data == null) return false;
            if (data.m_methodHash != SayHash && data.m_methodHash != ChatHash) return false;
            if (ZNet.instance == null) return false;

            // Anything the server itself says - Mark, or this plugin - is already addressed
            // per recipient by whatever sent it, and is left alone.
            ZNetPeer from = ZNet.instance.GetPeer(data.m_senderPeerID);
            if (from == null) return false;

            if (!Decode(data, from, out Vector3 pos, out int type, out UserInfo who, out string text))
                return false;

            float now = Time.time;
            if (data.m_senderPeerID == _lastSender && type == _lastType &&
                text == _lastText && now - _lastAt < 0.5f)
                return true;        // a duplicate of one we have already sent on

            _lastSender = data.m_senderPeerID;
            _lastType = type;
            _lastText = text;
            _lastAt = now;

            int sent = 0;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ChatMessage", pos, type, who, text);
                sent++;
            }

            if (sent > 0) Metrics.ChatGlobal++;
            return true;
        }

        /// <summary>
        /// Reads the parameters back out of the routed call. <c>Say</c> carries no position -
        /// vanilla takes it from the speaker's own transform on the receiving end - so the
        /// peer's reference position stands in, which is where they are to within one update.
        /// </summary>
        private static bool Decode(ZRoutedRpc.RoutedRPCData data, ZNetPeer from,
                                   out Vector3 pos, out int type, out UserInfo who, out string text)
        {
            pos = from.m_refPos + Vector3.up * 1.5f;
            type = 1;
            who = null;
            text = "";

            try
            {
                // A copy, because the original package belongs to the call in flight and its
                // read position is not ours to move.
                var pkg = new ZPackage(data.m_parameters.GetArray());

                if (data.m_methodHash == ChatHash) pos = pkg.ReadVector3();
                type = pkg.ReadInt();

                who = new UserInfo();
                who.Deserialize(ref pkg);
                text = pkg.ReadString();

                return !string.IsNullOrEmpty(text);
            }
            catch
            {
                // A chat payload we do not recognise is left to vanilla rather than dropped.
                return false;
            }
        }
    }
}
