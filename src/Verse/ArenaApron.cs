using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// A ring of wooden boards on the ground around the outside of the wall.
    ///
    /// <para><b>Cosmetic, and the first thing here that is.</b> Everything else the arena builds
    /// answers a failure: the deck stops chests collapsing, the railing stops a spectator
    /// dropping in. This is a floor to walk on, because the venue was a stone circle standing in
    /// a mown field and it looked like one. It is also where a spectator lands and where the
    /// ladder stands, so the one piece of decoration is also the one bit of ground people
    /// actually use.</para>
    ///
    /// <para><b>Laid against the wall's outer face, not at a number.</b> The radius comes from
    /// the wall's own thickness (<see cref="ArenaRing.WallThickness"/>, measured off the prefab)
    /// plus half a board, so the boardwalk meets the stone whatever the ring is built from and
    /// whatever <c>ArenaRadius</c> is set to. The count comes from the circumference and the
    /// board's own width: a 2 m square on a 21 m circle leaves about 2 cm of gap at the outer
    /// corners, which is a joint and not a hole.</para>
    ///
    /// <para>Shared and untagged like the ring, marked <c>verse.arena.apron</c> so the scenery
    /// sweep leaves it standing, and unbreakable - see <see cref="Fixture"/>.</para>
    /// </summary>
    internal static class ArenaApron
    {
        /// <summary>Marks a piece as part of the boardwalk.</summary>
        private static readonly int ApronPiece = "verse.arena.apron".GetStableHashCode();

        /// <summary>How far the boards' surface sits above the ground.</summary>
        private const float Lift = 0.02f;

        /// <summary>How far the built radius may drift before the ring is relaid.</summary>
        private const float Slack = 1f;

        private static System.Reflection.FieldInfo _byId;
        private static bool _laid;

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        internal static bool IsApron(ZDO zdo) => zdo != null && zdo.GetInt(ApronPiece, 0) == 1;

        private static string Prefab =>
            string.IsNullOrWhiteSpace(VersePlugin.ArenaBoardPrefab.Value)
                ? ""
                : VersePlugin.ArenaBoardPrefab.Value.Trim();

        /// <summary>The boardwalk's own pieces, wherever they are.</summary>
        internal static List<ZDO> Standing()
        {
            var standing = new List<ZDO>();

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return standing;

            foreach (ZDO zdo in all.Values)
                if (IsApron(zdo)) standing.Add(zdo);

            return standing;
        }

        /// <summary>
        /// Lays the boardwalk if it is not already there, and reports how many boards went
        /// down. Idempotent: judged on the piece count and the radius they are at, the same
        /// test <see cref="ArenaRing.Ensure"/> makes of the wall.
        /// </summary>
        internal static int Ensure()
        {
            if (_laid) return 0;
            if (!VersePlugin.ArenaBoardwalk.Value) return 0;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return 0;
            if (!ArenaSite.Resolve(out Vector3 centre)) return 0;

            string prefab = Prefab;
            if (prefab.Length == 0) return 0;

            int hash = prefab.GetStableHashCode();
            if (ZNetScene.instance.GetPrefab(hash) == null)
            {
                VersePlugin.Log.LogWarning(
                    $"arena: no '{prefab}' prefab on this server - no boardwalk, which is only " +
                    "decoration");
                return 0;
            }

            if (!Footing.Box(hash, out Bounds board))
            {
                VersePlugin.Log.LogWarning(
                    $"arena: '{prefab}' has no geometry to measure, so the boardwalk cannot be " +
                    "laid out around the wall");
                return 0;
            }

            float width = Mathf.Max(0.5f, board.size.x);
            float depth = Mathf.Max(0.5f, board.size.z);
            float radius = ArenaSite.Radius + ArenaRing.WallThickness * 0.5f + depth * 0.5f;
            int count = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * radius / width));

            List<ZDO> standing = Standing();
            if (standing.Count > 0)
            {
                float sum = 0f;
                foreach (ZDO zdo in standing)
                {
                    Vector3 p = zdo.GetPosition();
                    sum += Mathf.Sqrt((p.x - centre.x) * (p.x - centre.x) +
                                      (p.z - centre.z) * (p.z - centre.z));
                }

                float was = sum / standing.Count;
                if (standing.Count == count && Mathf.Abs(was - radius) < Slack)
                {
                    _laid = true;
                    VersePlugin.Log.LogInfo(
                        $"arena: the boardwalk is already down ({standing.Count} board(s) at " +
                        $"{was:0.0} m)");
                    return 0;
                }

                foreach (ZDO zdo in standing)
                {
                    HideMask.Forget(zdo);
                    Fixture.Destroy(zdo);
                }

                VersePlugin.Log.LogInfo(
                    $"arena: took up {standing.Count} board(s) laid at {was:0.0} m - the " +
                    $"boardwalk is now {count} board(s) at {radius:0.0} m");
            }

            int made = 0;
            for (int i = 0; i < count; i++)
            {
                float angle = i * 2f * Mathf.PI / count;
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                float x = centre.x + outward.x * radius;
                float z = centre.z + outward.z * radius;

                // The board's top surface on the ground, which is not where its origin goes;
                // see Footing. One tile's plank direction follows the radius, so the ring reads
                // as laid rather than scattered.
                float y = ArenaSite.HeightAt(x, z) + Lift - board.max.y;

                Fixture.Place(hash, new Vector3(x, y, z), Quaternion.LookRotation(outward),
                              ApronPiece);
                made++;
            }

            _laid = made > 0;

            VersePlugin.Log.LogInfo(
                $"arena: laid a boardwalk of {made} board(s) of {prefab} around the outside of " +
                $"the wall at {radius:0.0} m");

            return made;
        }

        /// <summary>Forces the next <see cref="Ensure"/> to look at the world again.</summary>
        internal static void Forget() => _laid = false;
    }
}
