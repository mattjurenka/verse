using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Firehose
{
    /// <summary>
    /// The measurement. This plugin exists to move one number - bytes per second the server
    /// manages to push at one player - so it reports that number itself rather than leaving
    /// it to be inferred from a stopwatch and a loading screen.
    ///
    /// <para>Throughput comes from <c>ISocket.GetAndResetStats</c>, which counts every byte
    /// the server handed that socket. Nothing in Valheim 1.0.16 calls it - it is on the
    /// interface and otherwise dead - so reading and resetting it here takes nothing away
    /// from the game.</para>
    ///
    /// <para>A <i>burst</i> is a player receiving faster than they would while simply walking
    /// around: a portal arrival, a first connection, a sprint into unexplored map. Each one
    /// is logged as a single line when it ends, which is the before-and-after this plugin is
    /// judged on.</para>
    /// </summary>
    internal static class Diagnostics
    {
        /// <summary>A ping to do the arithmetic at, for the startup check's estimate only.</summary>
        private const float AssumedPing = 0.16f;

        private static bool _startupLogged;
        private static float _sample;
        private static float _report;

        private class PeerTraffic
        {
            internal string Name;
            internal long Bytes;          // this report interval
            internal int Queue;           // last reading
            internal int PeakRate;        // this report interval
            internal bool Primed;         // first reading discarded: it covers the whole session

            internal bool InBurst;
            internal long BurstBytes;
            internal float BurstSeconds;
            internal int BurstPeak;
        }

        private static readonly Dictionary<ISocket, PeerTraffic> Traffic =
            new Dictionary<ISocket, PeerTraffic>();
        private static readonly List<ISocket> Gone = new List<ISocket>();

        /// <summary>
        /// One block, the first time the network is up, covering everything that decides
        /// whether this plugin does anything at all. Every one of its three patches can fail
        /// quietly - a transpiler that matched nothing still "applies" - so each reports what
        /// it actually bound.
        /// </summary>
        internal static void Startup(Harmony harmony)
        {
            if (_startupLogged || ZNet.instance == null || ZDOMan.instance == null) return;
            _startupLogged = true;

            SendRounds.Resolve();

            int window = SendWindow.Bytes();
            var sb = new StringBuilder();
            sb.AppendLine("--- Firehose: startup check ---");
            sb.AppendLine($"  role          : server={ZNet.instance.IsServer()} " +
                          $"dedicated={ZNet.instance.IsDedicated()} " +
                          $"backend={ZNet.m_onlineBackend}");

            // Replacements alone is not proof: it is counted while Harmony enumerates the
            // transpiler, before the rewritten IL is compiled, so a rejected patch still
            // reports both constants found. Ask Harmony whether it kept the rewrite.
            bool applied = SendWindow.Applied();
            if (applied && SendWindow.Replacements == 2)
            {
                sb.AppendLine($"  send window   : {window} bytes, both constants patched " +
                              $"(vanilla {SendWindow.Vanilla})");
            }
            else if (!applied)
            {
                sb.AppendLine("  send window   : **NOT APPLIED** - Harmony rejected the rewrite, " +
                              "so the window is still vanilla's. Look for a HarmonyX error " +
                              "earlier in this log.");
            }
            else
            {
                sb.AppendLine($"  send window   : NOT PATCHED as expected - " +
                              $"{SendWindow.Replacements} of 2 constants found in " +
                              $"ZDOMan.SendZDOs. A game update moved them.");
            }

            if (!FirehosePlugin.ServiceEveryPeer.Value)
                sb.AppendLine("  rounds        : vanilla (one peer per frame) - ServiceEveryPeer is off");
            else if (SendRounds.Ready)
                sb.AppendLine($"  rounds        : every peer each {FirehosePlugin.RoundSeconds.Value * 1000f:0} ms " +
                              $"(vanilla: one peer per frame, so 50 ms + N frames)");
            else
                sb.AppendLine($"  rounds        : vanilla - {SendRounds.Problem}");

            sb.AppendLine($"  patched body  : {SendWindow.Verify()}");
            sb.AppendLine($"  steam rate    : {SteamRate.Status}");

            // Proof that every patch bound. Harmony throws on an unresolvable target, so a
            // short list here means a target was renamed by a game update.
            var patched = new List<string>();
            foreach (MethodBase m in harmony.GetPatchedMethods())
                patched.Add((m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name);
            patched.Sort();
            sb.AppendLine($"  patched       : {(patched.Count > 0 ? string.Join(", ", patched.ToArray()) : "NOTHING")}");

            // window/ping is the real ceiling, because in-flight bytes count against the
            // window until they are acknowledged. Steam's rate cap then applies on top.
            float perPeer = window / AssumedPing;
            if (SteamRate.Wanted && ZNet.m_onlineBackend == OnlineBackendType.Steamworks)
                perPeer = System.Math.Min(perPeer, FirehosePlugin.SendRateMax.Value);
            float vanilla = System.Math.Min(SendWindow.Vanilla / AssumedPing, 153600f);

            sb.AppendLine($"  ceiling       : ~{perPeer / 1024f:0} KB/s per player at a {AssumedPing * 1000f:0} ms " +
                          $"ping, against ~{vanilla / 1024f:0} KB/s vanilla");
            sb.AppendLine($"  reporting     : {(FirehosePlugin.Report.Value ? "on, every 10s plus a line per burst" : "off")}");

            FirehosePlugin.Log.LogInfo(sb.ToString());
            SelfTest.Run();
        }

        internal static void Tick(float dt)
        {
            if (!FirehosePlugin.Report.Value) return;

            _sample += dt;
            _report += dt;

            if (_sample >= 1f) { Sample(_sample); _sample = 0f; }
            if (_report >= 10f) { Report(_report); _report = 0f; }
        }

        /// <summary>Per-socket byte counters, read and reset once a second.</summary>
        private static void Sample(float elapsed)
        {
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            int threshold = FirehosePlugin.BurstThreshold.Value;

            foreach (ISocket socket in Traffic.Keys)
                Gone.Add(socket);

            for (int i = 0; i < peers.Count; i++)
            {
                ZNetPeer peer = peers[i];
                ISocket socket = peer.m_socket;
                if (socket == null || !socket.IsConnected()) continue;
                Gone.Remove(socket);

                if (!Traffic.TryGetValue(socket, out PeerTraffic t))
                {
                    t = new PeerTraffic();
                    Traffic[socket] = t;
                }
                if (!string.IsNullOrEmpty(peer.m_playerName)) t.Name = peer.m_playerName;

                socket.GetAndResetStats(out int sent, out int _);
                t.Queue = socket.GetSendQueueSize();

                // The first reading covers everything since the connection opened, which is
                // not a rate; throw it away and start timing from the next one.
                if (!t.Primed) { t.Primed = true; continue; }

                int rate = (int)(sent / elapsed);
                t.Bytes += sent;
                if (rate > t.PeakRate) t.PeakRate = rate;

                if (!t.InBurst && rate >= threshold)
                {
                    t.InBurst = true;
                    t.BurstBytes = sent;
                    t.BurstSeconds = elapsed;
                    t.BurstPeak = rate;
                }
                else if (t.InBurst && rate >= threshold / 4)
                {
                    t.BurstBytes += sent;
                    t.BurstSeconds += elapsed;
                    if (rate > t.BurstPeak) t.BurstPeak = rate;
                }
                else if (t.InBurst)
                {
                    EndBurst(t);
                }
            }

            // A disconnect ends whatever it was in the middle of.
            for (int i = 0; i < Gone.Count; i++)
            {
                if (Traffic.TryGetValue(Gone[i], out PeerTraffic t) && t.InBurst) EndBurst(t);
                Traffic.Remove(Gone[i]);
            }
            Gone.Clear();
        }

        private static void EndBurst(PeerTraffic t)
        {
            t.InBurst = false;
            if (t.BurstBytes >= 131072)
                FirehosePlugin.Log.LogInfo(
                    $"firehose: {Named(t)} received {t.BurstBytes / 1024f:0} KB in {t.BurstSeconds:0.0}s " +
                    $"({t.BurstBytes / t.BurstSeconds / 1024f:0} KB/s average, {t.BurstPeak / 1024f:0} KB/s peak)");
            t.BurstBytes = 0;
            t.BurstSeconds = 0f;
            t.BurstPeak = 0;
        }

        private static void Report(float elapsed)
        {
            if (Traffic.Count == 0) return;

            var sb = new StringBuilder();
            sb.Append($"firehose: {Traffic.Count} peer(s), window {SendWindow.Bytes() / 1024}K, ");
            sb.Append(SendRounds.Active
                ? $"{SendRounds.Rounds / elapsed:0.0} rounds/s"
                : "vanilla scheduling");

            foreach (KeyValuePair<ISocket, PeerTraffic> entry in Traffic)
            {
                PeerTraffic t = entry.Value;
                sb.Append($" | {Named(t)} {t.Bytes / elapsed / 1024f:0} KB/s out " +
                          $"(peak {t.PeakRate / 1024f:0}, queue {t.Queue / 1024f:0.0}K)");
                t.Bytes = 0;
                t.PeakRate = 0;
            }

            SendRounds.Rounds = 0;
            FirehosePlugin.Log.LogInfo(sb.ToString());
        }

        private static string Named(PeerTraffic t) => string.IsNullOrEmpty(t.Name) ? "connecting" : t.Name;
    }
}
