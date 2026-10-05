using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The gallery: a plank walkway along the top of the wall, an iron cage railing on its inner
    /// edge, and a ladder up the outside to reach it.
    ///
    /// <para><b>What it is for.</b> A fighter who dies is out of the run, and until now that
    /// meant being out of the evening: their body is cleared, their gear is in their chest and
    /// their friends are still inside a sealed stone circle they cannot see into. So the arena
    /// gets somewhere to stand and watch. <see cref="Arena"/>'s <c>watch</c> brings a dead
    /// player back to the venue, the ladder takes them up, and the gallery is a seat with a view
    /// of the floor.</para>
    ///
    /// <para><b>And the railing is what makes it a seat rather than a way in.</b> A walkway on
    /// top of the wall with nothing on its inner edge is a diving board: anybody who has been
    /// knocked out of a run could climb up and drop straight back onto the floor, and the whole
    /// point of being out is being out. Vanilla's Cage Wall is iron bars - solid to walk into,
    /// transparent to look through - so the gallery can be open to the fight and closed to
    /// re-entry at the same time. That is the one thing the piece had to do, and it is why the
    /// railing is iron and not another course of stone.</para>
    ///
    /// <para><b>The ladder is measured, because the game has two kinds.</b> Vanilla's
    /// <c>Ladder</c> component does not animate a climb at all - <c>Ladder.Interact</c> moves
    /// the character to a <c>m_targetPos</c> transform on the prefab, so a piece carrying one is
    /// a lift and a stack of them is a staircase of presses. <c>wood_stepladder</c>, which is
    /// what this uses, turns out not to carry one on this server: it is climbed by walking into
    /// it, and a flush stack is simply one tall ladder. So the spacing comes from
    /// <c>m_targetPos</c> when there is one and from the piece's own height when there is not,
    /// and both end up at the same place - which is the whole reason for measuring rather than
    /// writing 2 m in this file.</para>
    ///
    /// <para><b>And there is a landing at the top, which is not decoration.</b> The ladder
    /// leans against the outside of the wall, so its top is a little further out than the
    /// walkway's outer edge, and whether a lift's target leans in or out from there is a
    /// decision somebody made in the Unity editor. Three boards at the ladder's own radius and
    /// the walkway's own height overlap the walkway and reach past the ladder, so the last step
    /// lands on planks either way.</para>
    ///
    /// <para>Shared, untagged, marked <c>verse.arena.stand</c> and unbreakable, like every other
    /// fixture - see <see cref="Fixture"/>.</para>
    /// </summary>
    internal static class ArenaStand
    {
        /// <summary>Marks a piece as part of the gallery: walkway, railing or ladder.</summary>
        private static readonly int StandPiece = "verse.arena.stand".GetStableHashCode();

        /// <summary>How far the walkway's surface sits above the stone it is laid on.</summary>
        private const float Lift = 0.02f;

        /// <summary>How far the built radius may drift before the gallery is relaid.</summary>
        private const float Slack = 1f;

        /// <summary>Which way round the ring the ladder stands, in radians.</summary>
        private const float LadderBearing = 0f;

        private static System.Reflection.FieldInfo _byId;
        private static bool _built;

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        internal static bool IsStand(ZDO zdo) => zdo != null && zdo.GetInt(StandPiece, 0) == 1;

        /// <summary>The gallery's own pieces, wherever they are.</summary>
        internal static List<ZDO> Standing()
        {
            var standing = new List<ZDO>();

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return standing;

            foreach (ZDO zdo in all.Values)
                if (IsStand(zdo)) standing.Add(zdo);

            return standing;
        }

        /// <summary>
        /// Where a spectator is put down: the foot of the ladder, on the boardwalk outside the
        /// wall. Deliberately not the gallery itself - the climb is three presses of the use
        /// key and it tells them where the way up is.
        /// </summary>
        internal static Vector3 LadderFoot(Vector3 centre)
        {
            var outward = new Vector3(Mathf.Cos(LadderBearing), 0f, Mathf.Sin(LadderBearing));
            float radius = ArenaSite.Radius + ArenaRing.WallThickness * 0.5f + 2f;

            float x = centre.x + outward.x * radius;
            float z = centre.z + outward.z * radius;

            return new Vector3(x, ArenaSite.HeightAt(x, z) + 0.5f, z);
        }

        /// <summary>
        /// Builds the gallery if it is not already standing. Idempotent on the same test the
        /// wall and the boardwalk use: the piece count and the radius they sit at.
        /// </summary>
        internal static int Ensure()
        {
            if (_built) return 0;
            if (!VersePlugin.ArenaGallery.Value) return 0;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return 0;
            if (!ArenaSite.Resolve(out Vector3 centre)) return 0;

            string deckName = VersePlugin.ArenaBoardPrefab.Value?.Trim() ?? "";
            string railName = VersePlugin.ArenaRailPrefab.Value?.Trim() ?? "";
            string ladderName = VersePlugin.ArenaLadderPrefab.Value?.Trim() ?? "";

            if (deckName.Length == 0 || railName.Length == 0)
            {
                VersePlugin.Log.LogWarning(
                    "arena: the gallery needs both a board prefab and a railing prefab - " +
                    "skipping it");
                return 0;
            }

            int deckHash = deckName.GetStableHashCode();
            int railHash = railName.GetStableHashCode();

            if (!Measured(deckName, deckHash, out Bounds deck)) return 0;
            if (!Measured(railName, railHash, out Bounds rail)) return 0;

            float radius = ArenaSite.Radius;
            float top = ArenaRing.WallTopY(centre);
            float surface = top + Lift;

            // The walkway, centred on the wall's own circle so it overhangs both faces.
            int decks = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * radius /
                                                      Mathf.Max(0.5f, deck.size.x)));

            // The railing, just inside the walkway's inner edge.
            float railRadius = radius - deck.size.z * 0.5f + rail.size.z * 0.5f;
            int rails = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * railRadius /
                                                      Mathf.Max(0.5f, rail.size.x)));

            List<ZDO> standing = Standing();
            if (standing.Count > 0)
            {
                // Judged on the walkway alone, by its prefab and the height of its surface.
                //
                // The first version of this took the highest piece of the gallery and compared
                // it to where a board should be, which tore the whole thing down and rebuilt it
                // on every boot: the highest piece is a railing, a railing's origin is its
                // middle, and "a metre out" was read as "somebody changed the geometry". The
                // lesson is the same one Footing exists for - a piece's position is not its
                // surface, so compare like with like.
                int boards = 0;
                float walkway = float.MinValue;

                foreach (ZDO zdo in standing)
                {
                    if (zdo.GetPrefab() != deckHash) continue;

                    boards++;
                    walkway = Mathf.Max(walkway, zdo.GetPosition().y + deck.max.y);
                }

                if (boards >= decks && Mathf.Abs(walkway - surface) < Slack)
                {
                    _built = true;
                    VersePlugin.Log.LogInfo(
                        $"arena: the gallery is already up ({standing.Count} piece(s), walkway " +
                        $"at {walkway:0.00} m)");
                    return 0;
                }

                foreach (ZDO zdo in standing)
                {
                    HideMask.Forget(zdo);
                    Fixture.Destroy(zdo);
                }

                VersePlugin.Log.LogInfo(
                    $"arena: took down {standing.Count} gallery piece(s) - rebuilding it at " +
                    $"{surface:0.0} m");
            }

            int made = 0;

            for (int i = 0; i < decks; i++)
            {
                float angle = i * 2f * Mathf.PI / decks;
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                var at = new Vector3(centre.x + outward.x * radius,
                                     surface - deck.max.y,
                                     centre.z + outward.z * radius);

                Fixture.Place(deckHash, at, Quaternion.LookRotation(outward), StandPiece);
                made++;
            }

            for (int i = 0; i < rails; i++)
            {
                float angle = i * 2f * Mathf.PI / rails;
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                // Standing on the walkway, so its own base goes on the surface - not its origin.
                var at = new Vector3(centre.x + outward.x * railRadius,
                                     surface - rail.min.y,
                                     centre.z + outward.z * railRadius);

                Fixture.Place(railHash, at, Quaternion.LookRotation(outward), StandPiece);
                made++;
            }

            made += Climb(centre, ladderName, surface, deckHash, deck);

            _built = made > 0;

            VersePlugin.Log.LogInfo(
                $"arena: built the gallery - {decks} board(s) of walkway on top of the wall at " +
                $"{surface:0.0} m, {rails} piece(s) of {railName} railing at {railRadius:0.0} m, " +
                $"and a ladder up the outside");

            return made;
        }

        /// <summary>
        /// Stacks ladder pieces from the ground to the walkway.
        ///
        /// <para>How far one piece lifts a player comes from the prefab's own
        /// <c>m_targetPos</c>, which is where <c>Ladder.Interact</c> puts them. Its height is
        /// not the piece's height and need not be: what matters is that each step lands on the
        /// next piece, so the stack is spaced by the lift and not by the model.</para>
        /// </summary>
        private static int Climb(Vector3 centre, string name, float surface,
                                 int deckHash, Bounds deck)
        {
            if (name.Length == 0) return 0;

            int hash = name.GetStableHashCode();
            GameObject prefab = ZNetScene.instance.GetPrefab(hash);
            if (prefab == null)
            {
                VersePlugin.Log.LogWarning(
                    $"arena: no '{name}' prefab on this server - the gallery has no way up, so " +
                    "nobody can use it");
                return 0;
            }

            if (!Footing.Box(hash, out Bounds box)) return 0;

            float lift = box.size.y;
            string target = "";

            Ladder ladder = prefab.GetComponent<Ladder>();
            if (ladder != null && ladder.m_targetPos != null)
            {
                Vector3 local = prefab.transform.InverseTransformPoint(ladder.m_targetPos.position);
                if (local.y > 0.5f) lift = local.y;

                // The sideways part is logged rather than acted on: where a step lands across
                // the wall is what the landing below is for.
                target = $" (one press lifts {local.y:0.00} m and " +
                         $"{new Vector2(local.x, local.z).magnitude:0.00} m to one side)";
            }
            else
            {
                // Which is the case on this server: wood_stepladder carries no Ladder
                // component, so it is climbed by walking into it rather than by pressing use,
                // and a flush stack of them is one tall ladder. Either way the spacing is the
                // prefab's own, which is the point of measuring instead of assuming.
                target = " (no Ladder component on it, so the stack is spaced by the piece's " +
                         "own height and climbed rather than pressed)";
            }

            lift = Mathf.Max(0.5f, lift);

            var outward = new Vector3(Mathf.Cos(LadderBearing), 0f, Mathf.Sin(LadderBearing));
            float radius = ArenaSite.Radius + ArenaRing.WallThickness * 0.5f + box.size.z * 0.5f;

            float x = centre.x + outward.x * radius;
            float z = centre.z + outward.z * radius;
            float ground = ArenaSite.HeightAt(x, z);

            // Enough lifts to reach the walkway. Each piece's base is below it by construction,
            // because (steps - 1) lifts is less than the climb, so none of them stands on the
            // gallery itself.
            int steps = Mathf.Clamp(Mathf.CeilToInt((surface - ground) / lift), 1, 12);

            for (int i = 0; i < steps; i++)
            {
                // Rungs facing out, away from the wall, which is both how one is built against a
                // wall and the side there is room to stand on.
                var at = new Vector3(x, ground + i * lift - box.min.y, z);
                Fixture.Place(hash, at, Quaternion.LookRotation(outward), StandPiece);
            }

            // A landing at the top, and this is the piece that makes the climb land somewhere.
            //
            // The last lift ends above the ladder's own origin, and the ladder leans on the
            // outside of the wall, so its origin sits a little further out than the walkway's
            // outer edge - and whether m_targetPos leans in or out from there is a decision
            // somebody made in the Unity editor, not a number this can rely on. A couple of
            // boards at the ladder's own radius and the walkway's own height overlap the
            // walkway and reach past the ladder, so the step lands on planks either way.
            int boards = 0;
            float span = Mathf.Max(0.5f, deck.size.x);
            for (int i = -1; i <= 1; i++)
            {
                float bearing = LadderBearing + i * span / radius;
                var along = new Vector3(Mathf.Cos(bearing), 0f, Mathf.Sin(bearing));

                var at = new Vector3(centre.x + along.x * radius,
                                     surface - deck.max.y,
                                     centre.z + along.z * radius);

                Fixture.Place(deckHash, at, Quaternion.LookRotation(along), StandPiece);
                boards++;
            }

            VersePlugin.Log.LogInfo(
                $"arena: {steps} x {name} up the outside of the wall at {radius:0.0} m - each " +
                $"one lifts {lift:0.00} m, from {ground:0.0} m to a {boards}-board landing at " +
                $"the walkway's own height, {surface:0.0} m" + target);

            return steps + boards;
        }

        private static bool Measured(string name, int hash, out Bounds box)
        {
            box = default(Bounds);

            if (ZNetScene.instance.GetPrefab(hash) == null)
            {
                VersePlugin.Log.LogWarning(
                    $"arena: no '{name}' prefab on this server - the gallery cannot be built");
                return false;
            }

            if (!Footing.Box(hash, out box))
            {
                VersePlugin.Log.LogWarning(
                    $"arena: '{name}' has no geometry to measure, so the gallery cannot be laid " +
                    "out around the wall");
                return false;
            }

            return true;
        }

        /// <summary>Forces the next <see cref="Ensure"/> to look at the world again.</summary>
        internal static void Forget() => _built = false;
    }
}
