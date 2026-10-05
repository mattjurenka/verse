using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Copy-on-write, checked end to end on an empty server, with no client in the room.
    ///
    /// <para>The question this answers is the one that matters and is otherwise expensive to
    /// ask: <b>party A fights a draugr to half health; does party B, walking in later, find an
    /// untouched one?</b> Everything about the fork direction, the pristine snapshot and the
    /// mask exists to make that true, and all of it is invisible until two parties are in the
    /// same dungeon.</para>
    ///
    /// <para>It is a real test rather than a unit test of the arithmetic. `ISocket` is a
    /// public interface and `ZDOMan.AddPeer` takes any `ZNetPeer`, so two peers can be
    /// synthesised in two verses; the mutation path is then driven through the actual
    /// `ZDOMan.RPC_ZDOData`, wire format and all, which is the only way to exercise
    /// `Divergence`'s snapshot-and-fork rather than a convenient re-statement of it.</para>
    ///
    /// <para>Off by default, refuses to run with anybody connected, and everything it makes is
    /// non-persistent and destroyed afterwards. Turn it on with <c>Verse.SelfTest</c> or
    /// <c>VERSE_SELFTEST=1</c> in the server's environment.</para>
    /// </summary>
    internal static class SelfTest
    {
        /// <summary>Verses that exist only for the length of this test; never in the registry.</summary>
        private const int VerseA = 90001;
        private const int VerseB = 90002;
        private const int VerseC = 90003;

        private const long UidA = -90001L;
        private const long UidB = -90002L;

        /// <summary>A vanilla prefab, so nothing about the object under test is made up.</summary>
        private const string Creature = "Draugr";

        private static readonly List<string> Results = new List<string>();
        private static int _failed;

        internal static bool Wanted =>
            VersePlugin.SelfTestEnabled.Value ||
            Environment.GetEnvironmentVariable("VERSE_SELFTEST") == "1";

        /// <summary>An ISocket that is only ever written to, so a ZNetPeer can exist without a network.</summary>
        private class FakeSocket : ISocket
        {
            public void Send(ZPackage pkg) { }
            public int GetSendQueueSize() => 0;
            public bool IsConnected() => true;
            public ZPackage Recv() => null;
            public int GetCurrentSendRate() => 0;
            public bool IsHost() => false;
            public void Dispose() { }
            public bool GotNewData() => false;
            public void Close() { }
            public string GetEndPointString() => "verse-selftest";
            public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = 0; totalRecv = 0; }
            public void GetConnectionQuality(out float localQuality, out float remoteQuality,
                                             out int ping, out float outByteSec, out float inByteSec)
            {
                localQuality = 1f; remoteQuality = 1f; ping = 0; outByteSec = 0f; inByteSec = 0f;
            }
            public ISocket Accept() => null;
            public int GetHostPort() => 0;
            public bool Flush() => true;
            public string GetHostName() => "verse-selftest";
            public void VersionMatch() { }
        }

        internal static void Run()
        {
            if (!Wanted) return;

            int connected = ZNet.instance.GetPeers().Count;
            if (connected > 0)
            {
                VersePlugin.Log.LogWarning(
                    $"verse self-test skipped: {connected} peer(s) connected. It is for an empty server.");
                return;
            }
            if (!VersePlugin.Isolate.Value || !VersePlugin.DivergeOnChange.Value)
            {
                VersePlugin.Log.LogWarning(
                    "verse self-test skipped: it tests Isolate and DivergeOnChange, and one of them is off.");
                return;
            }

            int prefab = Creature.GetStableHashCode();
            if (ZNetScene.instance == null || ZNetScene.instance.GetPrefab(prefab) == null)
            {
                VersePlugin.Log.LogWarning($"verse self-test skipped: no '{Creature}' prefab.");
                return;
            }

            Results.Clear();
            _failed = 0;

            var made = new List<ZDOID>();
            ZNetPeer a = null, b = null;

            try
            {
                a = Attach(UidA, VerseA);
                b = Attach(UidB, VerseB);
                if (a == null || b == null)
                {
                    VersePlugin.Log.LogWarning("verse self-test skipped: could not synthesise peers.");
                    return;
                }

                Scenario(prefab, a, made);
                KeyScenario(a);
                ScatterScenario();
            }
            catch (Exception e)
            {
                VersePlugin.Log.LogError("verse self-test blew up: " + e);
                _failed++;
            }
            finally
            {
                Detach(a);
                Detach(b);
                // Saved immediately rather than on the next periodic write, so the throwaway
                // verses are never sitting in verses.json for somebody to wonder about.
                Verses.SaveIfDirty();
                foreach (ZDOID id in made)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo != null) ZDOMan.instance.DestroyZDO(zdo);
                }
                Report(made.Count);
            }
        }

        /// <summary>
        /// A fights the creature to half health; B then walks in and swings at what it finds.
        /// </summary>
        private static void Scenario(int prefab, ZNetPeer a, List<ZDOID> made)
        {
            var where = new Vector3(0f, 40f, 0f);

            // The shared creature, as a location or a dungeon would have left it: created by
            // the server, so untagged and visible to every verse.
            ZDO shared = ZDOMan.instance.CreateNewZDO(where, prefab);
            shared.Persistent = false;
            shared.SetPrefab(prefab);
            shared.Set(ZDOVars.s_health, 100f);
            made.Add(shared.m_uid);

            Check("the shared creature starts untagged", ZdoVerse.Of(shared) == Verses.None);
            Check("and is visible to both verses",
                ZdoVerse.VisibleTo(shared, VerseA) && ZdoVerse.VisibleTo(shared, VerseB));

            // --- A fights it to half health, through the real ZDO data path ----------------
            // Counted as "objects of this prefab that we did not make ourselves", because
            // Damage leaves a scratch ZDO of its own behind - see there.
            Damage(a, shared, 50f, made);

            ZDO copy = Newest(prefab, made);
            Check("damaging it forked the object in two", Unknown(prefab, made) == 1 && copy != null);
            if (copy == null) return;
            made.Add(copy.m_uid);

            Check("A keeps the one it fought", ZdoVerse.Of(shared) == VerseA);
            Check("A's creature is at half health", Mathf.Approximately(shared.GetFloat(ZDOVars.s_health, -1f), 50f));
            Check("the copy left behind is untouched", Mathf.Approximately(copy.GetFloat(ZDOVars.s_health, -1f), 100f));
            Check("the copy belongs to nobody yet", ZdoVerse.Of(copy) == Verses.None);

            // The bug this test was written for: an untagged copy is visible to everybody,
            // so without the mask A is sent its half-dead draugr *and* a fresh one.
            Check("A is not shown the copy as well", !ZdoVerse.VisibleTo(copy, VerseA));
            Check("A still sees the one it fought", ZdoVerse.VisibleTo(shared, VerseA));
            Check("B cannot see A's creature", !ZdoVerse.VisibleTo(shared, VerseB));
            Check("B sees the untouched copy", ZdoVerse.VisibleTo(copy, VerseB));

            // --- B swings at what it found -------------------------------------------------
            bool claimed = Divergence.Claim(copy, VerseB);
            ZDO second = Newest(prefab, made);
            bool one = Unknown(prefab, made) == 1;
            if (second != null) made.Add(second.m_uid);

            Check("B's swing claims it rather than being dropped", claimed);
            Check("claiming forked it again", one && second != null);
            Check("B now owns what it hit", ZdoVerse.Of(copy) == VerseB);
            Check("B's creature is still at full health",
                Mathf.Approximately(copy.GetFloat(ZDOVars.s_health, -1f), 100f));
            if (second != null)
            {
                Check("B is not shown its own leftover copy", !ZdoVerse.VisibleTo(second, VerseB));
                Check("a third verse would still find an untouched one",
                    ZdoVerse.VisibleTo(second, VerseC) &&
                    Mathf.Approximately(second.GetFloat(ZDOVars.s_health, -1f), 100f));
            }

            // --- the whole point, stated as the answer to the original question -------------
            Check("A and B each see exactly one creature, at their own health",
                ZdoVerse.VisibleTo(shared, VerseA) && !ZdoVerse.VisibleTo(copy, VerseA) &&
                ZdoVerse.VisibleTo(copy, VerseB) && !ZdoVerse.VisibleTo(shared, VerseB));
        }


        /// <summary>
        /// A new verse must start away from the centre, on land. Worth a check because the
        /// ground search can quietly settle on the sea, and the symptom - a player arriving
        /// underwater - would only show up in play.
        /// </summary>
        private static void ScatterScenario()
        {
            if (!VersePlugin.ScatterNewVerses.Value)
            {
                Check("scattering is off, so not checked", true);
                return;
            }

            const string account = "verse-selftest-scatter";
            int id = Verses.OfOrCreate(account);
            try
            {
                Record record = Verses.Get(id);
                bool placed = record?.Spawn != null && record.Spawn.Length == 3;
                Check("a new verse is given its own starting point", placed);
                if (!placed) return;

                float x = record.Spawn[0], y = record.Spawn[1], z = record.Spawn[2];
                float out_ = Mathf.Sqrt(x * x + z * z);

                Check("which is out at roughly the configured radius",
                    out_ >= VersePlugin.ScatterRadius.Value * 0.9f);
                Check("and on dry land rather than in the sea", y > 31f);
                Check("and not where another verse starts",
                    Verses.Get(VerseA)?.Spawn == null || !Mathf.Approximately(x, Verses.Get(VerseA).Spawn[0]));
            }
            finally
            {
                Verses.Discard(id);
            }
        }

        /// <summary>
        /// Progression per verse: the question "if we have already killed a boss, does a new
        /// verse inherit it?" answered without needing a second party and a second boss.
        /// </summary>
        private static void KeyScenario(ZNetPeer a)
        {
            const string Boss = "defeated_eikthyr";
            const string Modifier = "resourcerate 2";

            // The split is vanilla's own enum boundary, so check we read it the same way.
            Check("a boss key counts as progression", !Keys.WorldWide(Boss));
            Check("a world modifier counts as the operator's", Keys.WorldWide(Modifier));
            Check("activeBosses is progression too", !Keys.WorldWide("activeBosses 1"));
            Check("an unrecognised key is treated as progression",
                !Keys.WorldWide("some_mod_added_key"));

            MethodInfo set = AccessTools.Method(typeof(ZoneSystem), "RPC_SetGlobalKey");
            if (set == null) { Check("ZoneSystem.RPC_SetGlobalKey found", false); return; }

            // A kills a boss, through the real RPC the game would have used.
            set.Invoke(ZoneSystem.instance, new object[] { a.m_uid, Boss });

            Check("the verse that killed it has the key", Keys.For(VerseA).Contains(Boss));
            Check("the other verse does not", !Keys.For(VerseB).Contains(Boss));
            Check("and it is not left in the world set", !Keys.WorldProgression().Contains(Boss));

            // A modifier from the same peer is the operator's and must reach everyone.
            set.Invoke(ZoneSystem.instance, new object[] { a.m_uid, Modifier });
            Check("a modifier set by a player still reaches both verses",
                Keys.For(VerseA).Contains(Modifier) && Keys.For(VerseB).Contains(Modifier));

            // Leave the world set as we found it; the verse records go with the fake verses.
            Keys.Forget(Modifier);

            MethodInfo remove = AccessTools.Method(typeof(ZoneSystem), "RPC_RemoveGlobalKey");
            if (remove != null)
            {
                remove.Invoke(ZoneSystem.instance, new object[] { a.m_uid, Boss });
                Check("losing a key only affects the verse that lost it",
                    !Keys.For(VerseA).Contains(Boss));
            }
        }


        /// <summary>
        /// Feeds the server a ZDO update from <paramref name="peer"/> in vanilla's own wire
        /// format, which is what a client sends when it has changed something it owns.
        /// </summary>
        private static void Damage(ZNetPeer peer, ZDO target, float health, List<ZDOID> made)
        {
            // The changed field set, built on a throwaway ZDO because ZDO.Serialize is the
            // only thing that writes that format and it is an instance method. It keeps the
            // target's prefab, in case the prefab rides along in the serialised fields, and
            // is therefore indistinguishable from the real objects by prefab alone - so it
            // goes on the known list and is counted out of the checks and cleaned up with
            // everything else.
            ZDO scratch = ZDOMan.instance.CreateNewZDO(target.GetPosition(), target.GetPrefab());
            scratch.Persistent = false;
            scratch.Set(ZDOVars.s_health, health);
            var fields = new ZPackage();
            scratch.Serialize(fields);
            made.Add(scratch.m_uid);

            var pkg = new ZPackage();
            pkg.Write(0);                               // no invalidated sectors
            pkg.Write(target.m_uid);
            pkg.Write(target.OwnerRevision);
            pkg.Write(target.DataRevision + 1);         // a real change, or vanilla ignores it
            pkg.Write(peer.m_uid);
            pkg.Write(target.GetPosition());
            pkg.Write(fields);
            pkg.Write(ZDOID.None);                      // terminator
            pkg.SetPos(0);

            MethodInfo rpc = AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData");
            rpc.Invoke(ZDOMan.instance, new object[] { peer.m_rpc, pkg });
        }

        // --- peers ---------------------------------------------------------------------------

        private static FieldInfo _znetPeers;

        /// <summary>
        /// A peer has to be in both lists: `ZDOMan` to be sent anything, and `ZNet` because
        /// that is where `Divergence` looks up which verse an incoming RPC came from.
        /// </summary>
        private static ZNetPeer Attach(long uid, int verse)
        {
            _znetPeers = _znetPeers ?? AccessTools.Field(typeof(ZNet), "m_peers");
            if (_znetPeers == null) return null;
            if (!(_znetPeers.GetValue(ZNet.instance) is List<ZNetPeer> peers)) return null;

            var peer = new ZNetPeer(new FakeSocket(), false)
            {
                m_uid = uid,
                m_playerName = "verse-selftest",
                m_refPos = new Vector3(0f, 40f, 0f),
                m_publicRefPos = true,
                m_simulationDistance = SimulationDistance.OriginalDistance,
            };

            peers.Add(peer);

            // AddPeer would otherwise run this fake account through Peers.Place and create a
            // real verse in the registry for it.
            Peers.Suspend();
            try { ZDOMan.instance.AddPeer(peer); }
            finally { Peers.Resume(); }

            Peers.Pretend(uid, verse);

            // The key tests need a real record to attribute to, so one is created here and
            // discarded in Detach. Ids are far out of the way of anything real.
            if (Verses.Get(verse) == null) Verses.CreateWithId(verse, "verse-selftest");
            return peer;
        }

        private static void Detach(ZNetPeer peer)
        {
            if (peer == null) return;

            // Read the verse before RemovePeer, which forgets the mapping on its way out.
            int verse = Peers.VerseOf(peer.m_uid);

            if (_znetPeers != null && _znetPeers.GetValue(ZNet.instance) is List<ZNetPeer> peers)
                peers.Remove(peer);

            ZDOMan.instance.RemovePeer(peer);   // this also calls Peers.Forget
            peer.Dispose();
            Verses.Discard(verse);
        }

        // --- finding what the fork made --------------------------------------------------------

        private static FieldInfo _byId;

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        /// <summary>
        /// Objects of this prefab that the test did not make itself - which, on a world with
        /// no draugr loaded, is exactly what a fork left behind. Counting this way rather than
        /// by a before-and-after total keeps the check independent of dictionary order and of
        /// the scratch object Damage needs.
        /// </summary>
        private static int Unknown(int prefab, List<ZDOID> known)
        {
            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return 0;

            int n = 0;
            foreach (KeyValuePair<ZDOID, ZDO> entry in all)
            {
                if (known.Contains(entry.Key)) continue;
                if (entry.Value.GetPrefab() == prefab) n++;
            }
            return n;
        }

        /// <summary>The one object of this prefab the test has not already accounted for.</summary>
        private static ZDO Newest(int prefab, List<ZDOID> known)
        {
            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return null;

            foreach (KeyValuePair<ZDOID, ZDO> entry in all)
            {
                if (known.Contains(entry.Key)) continue;
                if (entry.Value.GetPrefab() != prefab) continue;
                return entry.Value;
            }
            return null;
        }

        // --- reporting --------------------------------------------------------------------------

        private static void Check(string what, bool passed)
        {
            if (!passed) _failed++;
            Results.Add((passed ? "  PASS  " : "  FAIL  ") + what);
        }

        private static void Report(int objects)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- Verse: self-test (copy-on-write) ---");
            foreach (string line in Results) sb.AppendLine(line);
            sb.AppendLine(_failed == 0
                ? $"  all {Results.Count} checks passed; {objects} throwaway object(s) destroyed"
                : $"  {_failed} of {Results.Count} FAILED - copy-on-write is not behaving as designed");

            if (_failed == 0) VersePlugin.Log.LogInfo(sb.ToString());
            else VersePlugin.Log.LogError(sb.ToString());
        }
    }
}
