using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Gives a verse its own, unused Guardian Power stones at the temple.
    ///
    /// <para>These (<c>BossStone_Eikthyr</c> and six more) are temple-generated furniture, not
    /// anything a player built - <c>creator</c> is always 0 on them, which is exactly why they
    /// were invisible to every sweep category (see <see cref="Sweep"/>'s fourth item) until
    /// <c>Classify</c> learned to recognise them. Once swept into the legacy verse, every other
    /// verse simply lost them - correct in that nobody sees someone else's stones any more, but
    /// a fresh verse should have its <i>own</i>, not none at all, the same way a fresh Valheim
    /// world would.</para>
    ///
    /// <para>Deliberately copies only position and rotation from whichever copy it finds first,
    /// never the rest of the field set: an attached trophy is a separate <c>ItemDrop</c> ZDO
    /// parented to the stand, not a field on the stand's own ZDO, so a position-and-rotation-only
    /// copy is already a blank, unused stone - there is no "has trophy" flag here to
    /// accidentally carry over.</para>
    /// </summary>
    internal static class BossStones
    {
        private static readonly string[] Prefabs =
        {
            "BossStone_Eikthyr", "BossStone_TheElder", "BossStone_Bonemass",
            "BossStone_DragonQueen", "BossStone_Yagluth", "BossStone_TheQueen", "BossStone_Fader",
        };

        private struct Template { internal int Prefab; internal Vector3 Position; internal Quaternion Rotation; }

        private static FieldInfo _byId;
        private static MethodInfo _handleDestroyed;
        private static readonly object[] DestroyArgs = new object[1];

        /// <summary>
        /// Creates this verse's own copy of each known boss stone it does not already have.
        /// Called once from <see cref="Verses.OfOrCreate"/>, the moment a verse is born -
        /// never player-triggered. Safe to call more than once anyway, the same
        /// "harmless but pointless" rule <see cref="Sweep"/> follows: already-present ones are
        /// skipped rather than duplicated, and any broken leftovers from an earlier version of
        /// this method are cleaned up rather than left as junk.
        /// </summary>
        internal static int Seed(int verse)
        {
            if (verse == Verses.None) return 0;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return 0;

            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (!(_byId?.GetValue(ZDOMan.instance) is Dictionary<ZDOID, ZDO> all)) return 0;

            // Pass 1: every known stone found anywhere becomes a template (first one seen),
            // and note which of them this verse already has one of.
            var templates = new Dictionary<string, Template>();
            var already = new HashSet<string>();
            foreach (ZDO zdo in all.Values)
            {
                string prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab())?.name;
                if (prefab == null) continue;

                bool isStone = false;
                foreach (string name in Prefabs)
                {
                    if (prefab != name) continue;
                    isStone = true;
                    break;
                }
                if (!isStone) continue;

                if (ZdoVerse.Of(zdo) == verse) already.Add(prefab);
                if (!templates.ContainsKey(prefab))
                    templates[prefab] = new Template
                    {
                        Prefab = zdo.GetPrefab(),
                        Position = zdo.GetPosition(),
                        Rotation = zdo.GetRotation(),
                    };
            }

            // Pass 2, needing every template already found: a prefab-0 ZDO at one of these
            // exact spots, tagged to this verse, can only be a leftover from the version of
            // this method that forgot SetPrefab - real data, no registered prefab, invisible
            // forever. Cleaned up rather than left as junk.
            var broken = new List<ZDOID>();
            foreach (ZDO zdo in all.Values)
            {
                if (zdo.GetPrefab() != 0 || ZdoVerse.Of(zdo) != verse) continue;

                foreach (Template known in templates.Values)
                {
                    if (Vector3.Distance(zdo.GetPosition(), known.Position) > 1f) continue;
                    broken.Add(zdo.m_uid);
                    break;
                }
            }

            if (broken.Count > 0)
            {
                _handleDestroyed = _handleDestroyed ??
                    AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) });
                if (_handleDestroyed != null)
                {
                    foreach (ZDOID uid in broken)
                    {
                        DestroyArgs[0] = uid;
                        _handleDestroyed.Invoke(ZDOMan.instance, DestroyArgs);
                    }
                    VersePlugin.Log.LogInfo(
                        $"removed {broken.Count} broken (prefab-less) boss stone(s) left behind " +
                        $"by an earlier version of this for verse {verse}");
                }
            }

            int made = 0;
            Authorship.Suspend();
            try
            {
                foreach (string name in Prefabs)
                {
                    if (already.Contains(name)) continue;
                    if (!templates.TryGetValue(name, out Template t)) continue;

                    // CreateNewZDO's prefab parameter only feeds an internal portal side-index
                    // (ZDOMan.cs:733-741) - it never actually sets the ZDO's own prefab field,
                    // so skipping this leaves a ZDO with prefab 0: real data, invisible to
                    // every client, because ZNetScene has nothing registered under hash 0 to
                    // instantiate. Divergence.Fork/Claim already call this for the same reason.
                    ZDO fresh = ZDOMan.instance.CreateNewZDO(t.Position, t.Prefab);
                    fresh.SetPrefab(t.Prefab);
                    fresh.SetRotation(t.Rotation);
                    ZdoVerse.Set(fresh, verse);
                    made++;
                }
            }
            finally
            {
                Authorship.Resume();
            }

            if (made > 0)
                VersePlugin.Log.LogInfo($"seeded verse {verse} with {made} default boss stone(s) at the temple");
            else if (already.Count > 0)
                VersePlugin.Log.LogInfo($"verse {verse} already has all its boss stones; nothing to do");
            else
                VersePlugin.Log.LogWarning(
                    "could not seed boss stones: found no existing copy of any of them to use as a template");

            return made;
        }
    }
}
