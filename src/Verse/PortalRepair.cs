using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// One-off repair for a pre-existing problem portal pairing never accounted for: Valheim's
    /// portal connection system (<c>ZDOMan</c>'s own, via <c>ZDOExtraData.ConnectionType.Portal</c>)
    /// matches any two portals sharing a tag, anywhere in the world, with no idea a verse exists.
    /// Every tagged portal at the legacy base's hub turned out to have a duplicate "shadow" copy
    /// standing at the identical spot - one tagged verse 1, one untagged - almost certainly from
    /// whenever each was rebuilt before live tagging was reliable. The game's matcher paired
    /// whichever copies it happened to see, which has nothing to do with which one verse 1 can
    /// actually see, so most of the hub came back disconnected or wired to an unrelated verse's
    /// test portal the moment a tag collided (plain "Spawn", most of all).
    ///
    /// <para>Per tag: cluster every portal with that tag by position (5 m). Exactly two clusters
    /// is a normal pair - hub side and far side. In each cluster, prefer whichever copy verse 1
    /// already owns as the one to keep live; fall back to an untagged (shared) one if verse 1
    /// has none there. Force-connects the two chosen copies to each other, each direction, which
    /// is exactly what <c>TeleportWorld.SetConnectedPortal</c> itself writes - nothing invented,
    /// just told the pairing a verse-blind matcher could never have known to make. A tag with
    /// anything other than exactly two clusters is left alone and reported rather than guessed
    /// at.</para>
    /// </summary>
    internal static class PortalRepair
    {
        private static FieldInfo _byId;

        private struct Portal { internal ZDOID Uid; internal int Verse; internal Vector3 Position; }

        internal struct Fix
        {
            internal string Tag;
            internal ZDOID A;
            internal ZDOID B;
            internal Vector3 At;
            internal Vector3 BAt;
        }

        internal struct Report
        {
            internal List<Fix> Fixes;
            internal List<string> Ambiguous;
        }

        /// <summary>Works out what would change, for a given verse, without writing anything.</summary>
        internal static Report Plan(int verse)
        {
            var report = new Report { Fixes = new List<Fix>(), Ambiguous = new List<string>() };
            if (ZDOMan.instance == null || ZNetScene.instance == null) return report;

            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (!(_byId?.GetValue(ZDOMan.instance) is Dictionary<ZDOID, ZDO> all)) return report;

            var byTag = new Dictionary<string, List<Portal>>();
            foreach (ZDO zdo in all.Values)
            {
                if (ZNetScene.instance.GetPrefab(zdo.GetPrefab())?.name != "portal_wood") continue;

                string tag = zdo.GetString(ZDOVars.s_tag) ?? "";
                if (!byTag.TryGetValue(tag, out List<Portal> list))
                {
                    list = new List<Portal>();
                    byTag[tag] = list;
                }
                list.Add(new Portal { Uid = zdo.m_uid, Verse = ZdoVerse.Of(zdo), Position = zdo.GetPosition() });
            }

            foreach (KeyValuePair<string, List<Portal>> kv in byTag)
            {
                string tag = kv.Key;
                List<Portal> portals = kv.Value;
                if (portals.Count < 2) continue;

                bool involvesVerse = false;
                foreach (Portal p in portals)
                    if (p.Verse == verse) { involvesVerse = true; break; }
                if (!involvesVerse) continue;

                var clusters = new List<List<Portal>>();
                foreach (Portal p in portals)
                {
                    bool placed = false;
                    foreach (List<Portal> cluster in clusters)
                    {
                        if (Vector3.Distance(cluster[0].Position, p.Position) > 5f) continue;
                        cluster.Add(p);
                        placed = true;
                        break;
                    }
                    if (!placed) clusters.Add(new List<Portal> { p });
                }

                if (clusters.Count != 2)
                {
                    report.Ambiguous.Add($"\"{tag}\": {clusters.Count} cluster(s), not 2 - left alone");
                    continue;
                }

                Portal a = Pick(clusters[0], verse);
                Portal b = Pick(clusters[1], verse);
                if (a.Uid == ZDOID.None || b.Uid == ZDOID.None)
                {
                    report.Ambiguous.Add($"\"{tag}\": neither verse {verse} nor shared at one side - left alone");
                    continue;
                }

                ZDO za = ZDOMan.instance.GetZDO(a.Uid);
                ZDOID current = za?.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) ?? ZDOID.None;
                if (current == b.Uid) continue; // already correct

                report.Fixes.Add(new Fix { Tag = tag, A = a.Uid, B = b.Uid, At = a.Position, BAt = b.Position });
            }

            return report;
        }

        /// <summary>Prefers this verse's own copy in a cluster; falls back to a shared (untagged) one.</summary>
        private static Portal Pick(List<Portal> cluster, int verse)
        {
            foreach (Portal p in cluster)
                if (p.Verse == verse) return p;
            foreach (Portal p in cluster)
                if (p.Verse == Verses.None) return p;
            return default;
        }

        internal static string Describe(Report report)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- Verse: portal repair plan - {report.Fixes.Count} fix(es), {report.Ambiguous.Count} left alone ---");
            foreach (Fix f in report.Fixes)
                sb.AppendLine($"  \"{f.Tag}\": {f.A} ({f.At.x:0},{f.At.z:0}) <-> {f.B} ({f.BAt.x:0},{f.BAt.z:0})");
            foreach (string a in report.Ambiguous)
                sb.AppendLine($"  {a}");
            return sb.ToString();
        }

        /// <summary>Writes the connections both directions. Call <see cref="Plan"/> first and show it.</summary>
        internal static void Apply(Report report)
        {
            foreach (Fix f in report.Fixes)
            {
                ZDO za = ZDOMan.instance.GetZDO(f.A);
                ZDO zb = ZDOMan.instance.GetZDO(f.B);
                if (za == null || zb == null) continue;

                za.SetConnection(ZDOExtraData.ConnectionType.Portal, f.B);
                zb.SetConnection(ZDOExtraData.ConnectionType.Portal, f.A);
            }

            if (report.Fixes.Count > 0 && ZNet.instance != null) ZNet.instance.Save(sync: false);
        }
    }
}
