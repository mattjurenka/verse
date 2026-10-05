using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace Firehose
{
    /// <summary>
    /// Ceiling 2: how often that window is refreshed.
    ///
    /// <c>ZDOMan.SendZDOToPeers2</c> waits 50 ms, then services exactly <b>one peer per
    /// frame</b>:
    ///
    /// <code>
    /// m_sendTimer += dt;
    /// if (m_nextSendPeer &lt; 0) { if (m_sendTimer > 0.05f) { m_nextSendPeer = 0; m_sendTimer = 0f; } return; }
    /// if (m_nextSendPeer &lt; m_peers.Count) SendZDOs(m_peers[m_nextSendPeer], flush: false);
    /// m_nextSendPeer++;
    /// if (m_nextSendPeer >= m_peers.Count) m_nextSendPeer = -1;
    /// </code>
    ///
    /// So a round costs <c>50 ms + N frames</c> and each player's window is topped up that
    /// often: ~120 ms at 4 players, ~380 ms at 10. Together with ceiling 1 the real
    /// throughput per player is <c>window / max(round period, round trip)</c>, and the round
    /// period is the half that gets worse as the server fills up.
    ///
    /// <para>This services every peer on each round instead, which makes the period constant
    /// in the player count. There is nothing to be gained by spreading the calls over frames:
    /// <c>SendZDOs</c> already bounds itself per peer by checking the socket queue and
    /// returning early, so a peer with nothing owed costs a sync-list build and no bytes.</para>
    ///
    /// <para>Note that skipping the original leaves vanilla's <c>m_nextSendPeer</c> parked at
    /// -1 forever. Nothing else in 1.0.16 reads it, which was checked rather than assumed.</para>
    /// </summary>
    internal static class SendRounds
    {
        private static FieldInfo _peers;
        private static MethodInfo _sendZdos;
        private static readonly object[] Args = new object[2];

        private static float _timer;

        /// <summary>Whether every reflective lookup resolved. Reported by the startup check.</summary>
        internal static bool Ready { get; private set; }
        /// <summary>Why we are standing down, if we are. Reported by the startup check.</summary>
        internal static string Problem { get; private set; }

        /// <summary>Rounds run since the last report, for the throughput arithmetic.</summary>
        internal static long Rounds;

        internal static void Resolve()
        {
            // Somebody else on this method would mean every peer serviced twice a round. Asked
            // of Harmony rather than by plugin name, so a sibling that stops patching stops
            // counting - which is exactly what happened when Verse's own scheduler was retired.
            MethodInfo scheduler = AccessTools.Method(typeof(ZDOMan), "SendZDOToPeers2");
            System.Collections.Generic.List<string> others = SendWindow.OtherOwners(scheduler);
            if (others.Count > 0)
            {
                Problem = "ZDOMan.SendZDOToPeers2 is also patched by " +
                          string.Join(", ", others.ToArray()) + " - leaving the scheduling to it";
                return;
            }

            System.Type zdoPeer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
            if (zdoPeer == null) { Problem = "ZDOMan.ZDOPeer not found"; return; }

            _peers = AccessTools.Field(typeof(ZDOMan), "m_peers");
            if (_peers == null) { Problem = "ZDOMan.m_peers not found"; return; }

            _sendZdos = AccessTools.Method(typeof(ZDOMan), "SendZDOs", new[] { zdoPeer, typeof(bool) });
            if (_sendZdos == null) { Problem = "ZDOMan.SendZDOs(ZDOPeer, bool) not found"; return; }

            Ready = true;
            Problem = null;
        }

        /// <summary>Whether we are the one scheduling sends, rather than vanilla.</summary>
        internal static bool Active => Ready && FirehosePlugin.ServiceEveryPeer.Value;

        /// <summary>
        /// One round of our own scheduling. Returns false to let vanilla's scheduler run
        /// instead, which is what happens on a client, before resolution, or when the setting
        /// is off.
        /// </summary>
        private static bool Tick(ZDOMan zdoMan, float dt)
        {
            if (!Active) return false;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;

            _timer += dt;
            if (_timer < FirehosePlugin.RoundSeconds.Value) return true;   // handled: nothing due yet
            _timer = 0f;

            var peers = _peers.GetValue(zdoMan) as IList;
            if (peers == null || peers.Count == 0) return true;

            Rounds++;

            for (int i = 0; i < peers.Count; i++)
            {
                object peer = peers[i];
                if (peer == null) continue;

                Args[0] = peer;
                Args[1] = false;
                _sendZdos.Invoke(zdoMan, Args);
            }

            return true;
        }

        [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
        internal static class ZDOMan_SendZDOToPeers2_Patch
        {
            private static bool Prefix(ZDOMan __instance, float dt) => !Tick(__instance, dt);
        }
    }
}
