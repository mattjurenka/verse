using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Firehose
{
    /// <summary>
    /// A measurement of the patched send path with no game client in the room.
    ///
    /// <para>Everything else this plugin reports is either arithmetic or a proof that a patch
    /// bound. Neither answers the only question that matters - does a bigger window actually
    /// put more bytes on the wire - and answering it normally takes a player walking through a
    /// portal. This builds the smallest thing `ZDOMan` cannot tell from a player: an
    /// <see cref="ISocket"/> that swallows what it is handed and reports whatever send-queue
    /// depth we want, wrapped in a <c>ZNetPeer</c> and registered as a peer. Then it fills a
    /// patch of world with throwaway objects and measures one send round, three ways:</para>
    ///
    /// <list type="bullet">
    /// <item><b>window 10240, queue empty</b> - vanilla's budget, as a baseline.</item>
    /// <item><b>window as configured, queue empty</b> - proves the budget constant is live,
    /// the one that decides how much goes out per round.</item>
    /// <item><b>window as configured, queue above 10240</b> - vanilla would refuse to send at
    /// all here, so anything that comes out proves the gate constant is live too. This is the
    /// case that matters in flight, because the queue is never empty mid-transfer.</item>
    /// </list>
    ///
    /// <para>Off by default and it refuses to run with anybody connected: it creates objects
    /// with no prefab behind them, which a real client has no business receiving. They are
    /// non-persistent and explicitly destroyed afterwards, so nothing reaches the world save
    /// either way. Turn it on with <c>Diagnostics.SelfTest</c> or <c>FIREHOSE_SELFTEST=1</c>
    /// in the server's environment.</para>
    /// </summary>
    internal static class SelfTest
    {
        /// <summary>An id no real Steam peer will have, so a stray fake peer is recognisable.</summary>
        private const long FakeUid = -424242L;

        /// <summary>
        /// An <see cref="ISocket"/> that is only ever written to. <c>Queue</c> is what it claims
        /// is outstanding, which is the input the window arithmetic actually reads;
        /// <c>Bytes</c> is what the game handed it.
        /// </summary>
        private class FakeSocket : ISocket
        {
            internal int Queue;
            internal int Bytes;
            internal int Packages;

            public void Send(ZPackage pkg) { Bytes += pkg.Size(); Packages++; }
            public int GetSendQueueSize() => Queue;

            public bool IsConnected() => true;
            public ZPackage Recv() => null;
            public int GetCurrentSendRate() => 0;
            public bool IsHost() => false;
            public void Dispose() { }
            public bool GotNewData() => false;
            public void Close() { }
            public string GetEndPointString() => "firehose-selftest";
            public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = Bytes; totalRecv = 0; }
            public void GetConnectionQuality(out float localQuality, out float remoteQuality,
                                             out int ping, out float outByteSec, out float inByteSec)
            {
                localQuality = 1f; remoteQuality = 1f; ping = 0; outByteSec = 0f; inByteSec = 0f;
            }
            public ISocket Accept() => null;
            public int GetHostPort() => 0;
            public bool Flush() => true;
            public string GetHostName() => "firehose-selftest";
            public void VersionMatch() { }
        }

        /// <summary>Ids of what the last run made, kept until they are confirmed gone.</summary>
        private static List<ZDOID> _created;
        private static float _verifyIn;

        internal static bool Wanted =>
            FirehosePlugin.SelfTestEnabled.Value ||
            Environment.GetEnvironmentVariable("FIREHOSE_SELFTEST") == "1";

        internal static void Run()
        {
            if (!Wanted) return;

            // Another plugin on SendZDOs itself makes the measurement meaningless, and in
            // Verse's case leaves a junk verse behind for the fake peer.
            List<string> sharing = SendWindow.OtherOwners(
                AccessTools.Method(typeof(ZDOMan), "SendZDOs"));
            if (sharing.Count > 0)
            {
                FirehosePlugin.Log.LogWarning(
                    "firehose self-test skipped: ZDOMan.SendZDOs is also patched by " +
                    string.Join(", ", sharing.ToArray()) + ". Run the test server with " +
                    "PLUGINS=\"HallPatton Firehose\" for a clean measurement.");
                return;
            }

            // Fake objects with no prefab behind them would be nonsense on a real client, and
            // the destroy broadcast afterwards would be noise. Both are reasons not to do this
            // with anybody on.
            int connected = ZNet.instance.GetPeers().Count;
            if (connected > 0)
            {
                FirehosePlugin.Log.LogWarning(
                    $"firehose self-test skipped: {connected} peer(s) connected. It is for an empty server.");
                return;
            }

            MethodInfo sendZdos = AccessTools.Method(typeof(ZDOMan), "SendZDOs",
                new[] { AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), typeof(bool) });
            FieldInfo peersField = AccessTools.Field(typeof(ZDOMan), "m_peers");
            if (sendZdos == null || peersField == null)
            {
                FirehosePlugin.Log.LogWarning("firehose self-test skipped: ZDOMan internals did not resolve.");
                return;
            }

            int count = Mathf.Clamp(FirehosePlugin.SelfTestObjects.Value, 100, 50000);
            int window = SendWindow.Bytes();
            var spot = new Vector3(0f, 32f, 0f);
            var made = new List<ZDO>(count);

            try
            {
                Populate(spot, count, made);

                // Three rounds, each on a peer that has never been sent anything, so they are
                // measuring the same thing from the same start.
                int vanillaEmpty = Round(sendZdos, peersField, spot, SendWindow.Vanilla, 0);
                int wideEmpty = Round(sendZdos, peersField, spot, window, 0);
                int wideBusy = Round(sendZdos, peersField, spot, window, SendWindow.Vanilla * 2);

                Report(count, window, vanillaEmpty, wideEmpty, wideBusy);
            }
            catch (Exception e)
            {
                FirehosePlugin.Log.LogError($"firehose self-test failed: {e}");
            }
            finally
            {
                SendWindow.Override = 0;

                // DestroyZDO only queues the removal - ZDOMan does it on its next update -
                // so the ids are kept and checked a second later by Verify. 4000 prefabless
                // objects left behind would be sent to the next player who joined.
                _created = new List<ZDOID>(made.Count);
                foreach (ZDO zdo in made)
                {
                    _created.Add(zdo.m_uid);
                    ZDOMan.instance.DestroyZDO(zdo);
                }
                _verifyIn = 1f;
            }
        }

        /// <summary>
        /// Throwaway objects for the peer to be sent. A handful of fields each, so they
        /// serialise to something in the same order as a real piece of world rather than to a
        /// bare header.
        /// </summary>
        private static void Populate(Vector3 centre, int count, List<ZDO> made)
        {
            var random = new System.Random(1);
            for (int i = 0; i < count; i++)
            {
                var at = centre + new Vector3(
                    (float)(random.NextDouble() * 60.0 - 30.0), 0f,
                    (float)(random.NextDouble() * 60.0 - 30.0));

                ZDO zdo = ZDOMan.instance.CreateNewZDO(at, 0);
                zdo.Persistent = false;   // never written to the world save
                zdo.Distant = false;
                zdo.Type = ZDO.ObjectType.Default;
                zdo.Set("firehose_selftest", true);
                zdo.Set("firehose_health", 100f);
                zdo.Set("firehose_seed", i);
                made.Add(zdo);
            }
        }

        /// <summary>One send round to a brand-new fake peer. Returns the bytes it was handed.</summary>
        private static int Round(MethodInfo sendZdos, FieldInfo peersField, Vector3 at, int window, int queue)
        {
            var socket = new FakeSocket { Queue = queue };
            var peer = new ZNetPeer(socket, false)
            {
                m_uid = FakeUid,
                m_playerName = "firehose-selftest",
                m_refPos = at,
                m_publicRefPos = true,
                m_simulationDistance = SimulationDistance.OriginalDistance,
            };

            SendWindow.Override = window;
            ZDOMan.instance.AddPeer(peer);
            try
            {
                var peers = (IList)peersField.GetValue(ZDOMan.instance);
                object zdoPeer = peers[peers.Count - 1];
                sendZdos.Invoke(ZDOMan.instance, new[] { zdoPeer, (object)false });
                return socket.Bytes;
            }
            finally
            {
                SendWindow.Override = 0;
                ZDOMan.instance.RemovePeer(peer);
                peer.Dispose();
            }
        }

        private static void Report(int count, int window, int vanillaEmpty, int wideEmpty, int wideBusy)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- Firehose: self-test ---");
            sb.AppendLine($"  objects       : {count} throwaway, non-persistent, within 30 m of the fake peer");
            sb.AppendLine($"  window 10240, queue empty  : {Kb(vanillaEmpty)} sent   (vanilla's budget)");
            sb.AppendLine($"  window {window}, queue empty : {Kb(wideEmpty)} sent   " +
                          $"({Ratio(wideEmpty, vanillaEmpty)} vanilla's)");
            sb.AppendLine($"  window {window}, queue {SendWindow.Vanilla * 2}  : {Kb(wideBusy)} sent   " +
                          $"(vanilla sends nothing at all with this much outstanding)");

            // The budget term should scale with the window; the gate term is simply whether
            // anything came out once the queue is past where vanilla gives up.
            bool budget = wideEmpty > vanillaEmpty * 2;
            bool gate = wideBusy > 0;
            sb.AppendLine($"  verdict       : budget constant {(budget ? "live" : "NOT WORKING")}, " +
                          $"gate constant {(gate ? "live" : "NOT WORKING")}");
            if (!budget || !gate)
                sb.AppendLine("  NOTE          : a patch bound but did not change behaviour. Do not deploy this.");

            FirehosePlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// Confirms the throwaway objects really left the world, a second after asking for it.
        /// Called every frame; does nothing unless a run is waiting to be checked.
        /// </summary>
        internal static void Verify(float dt)
        {
            if (_created == null) return;

            _verifyIn -= dt;
            if (_verifyIn > 0f) return;

            int left = 0;
            foreach (ZDOID id in _created)
                if (ZDOMan.instance.GetZDO(id) != null) left++;

            if (left == 0)
                FirehosePlugin.Log.LogInfo(
                    $"firehose self-test: all {_created.Count} throwaway objects are gone from the world");
            else
                FirehosePlugin.Log.LogWarning(
                    $"firehose self-test: {left} of {_created.Count} throwaway objects are STILL in the " +
                    $"world. They have no prefab, so restart the server before anybody joins.");

            _created = null;
        }

        private static string Kb(int bytes) => $"{bytes / 1024f:0.0} KB".PadLeft(9);

        private static string Ratio(int a, int b) => b > 0 ? $"{(float)a / b:0.0}x" : "n/a";
    }
}
