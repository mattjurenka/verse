using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The gallery: a plank walkway along the top of the wall, an iron cage railing on its inner
    /// edge, and a flight of steps up the outside to reach it.
    ///
    /// <para><b>What it is for.</b> A fighter who dies is out of the run, and until now that
    /// meant being out of the evening: their body is cleared, their gear is in their chest and
    /// their friends are still inside a sealed stone circle they cannot see into. So the arena
    /// gets somewhere to stand and watch. <see cref="Arena"/>'s <c>watch</c> brings a dead
    /// player back to the venue, the steps take them up, and the gallery is a seat with a view
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
    /// <para><b>The way up is steps, and it was a ladder until the game said otherwise.</b>
    /// Three stacked <c>wood_stepladder</c> went in and came back "not actually climbable".
    /// Valheim has no climbing: there is nothing about it in <c>Player</c> or <c>Character</c>,
    /// and the only <c>Ladder</c> component is a lift that teleports whoever uses it to a
    /// target transform - which the piece used here does not have. A wood ladder is therefore
    /// climbed by walking up its collider, so it works only when it is the right way round, and
    /// which way round that is lives in a Unity scene this server cannot read. See
    /// <see cref="Stair"/>: the same boards as the walkway, in steps a player walks up, laid
    /// level so there is no orientation to get wrong.</para>
    ///
    /// <para>Shared, untagged, marked <c>verse.arena.stand</c> and unbreakable, like every other
    /// fixture - see <see cref="Fixture"/>.</para>
    /// </summary>
    internal static class ArenaStand
    {
        /// <summary>Marks a piece as part of the gallery: walkway, railing or step.</summary>
        private static readonly int StandPiece = "verse.arena.stand".GetStableHashCode();

        /// <summary>
        /// Marks one of the stair boards in particular. The stair is made of the same prefab as
        /// the walkway, so without this the two can only be told apart by guessing at heights -
        /// which the self-test duly got wrong, counting 164 steps in a ten-step stair.
        /// </summary>
        private static readonly int StepPiece = "verse.arena.step".GetStableHashCode();

        /// <summary>How far the walkway's surface sits above the stone it is laid on.</summary>
        private const float Lift = 0.02f;

        /// <summary>How far the built radius may drift before the gallery is relaid.</summary>
        private const float Slack = 1f;

        /// <summary>Which way round the ring the stair starts, in radians.</summary>
        private const float StairBearing = 0f;

        private static System.Reflection.FieldInfo _byId;
        private static bool _built;

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        internal static bool IsStand(ZDO zdo) => zdo != null && zdo.GetInt(StandPiece, 0) == 1;

        /// <summary>Whether a ZDO is one of the stair boards.</summary>
        internal static bool IsStep(ZDO zdo) => zdo != null && zdo.GetInt(StepPiece, 0) == 1;

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
        /// Where a spectator is put down: the bottom of the steps, on the boardwalk outside the
        /// wall. Deliberately not the gallery itself - walking up tells them where the way up
        /// is, and lands them looking at the venue rather than at the floor.
        /// </summary>
        internal static Vector3 StairFoot(Vector3 centre)
        {
            var outward = new Vector3(Mathf.Cos(StairBearing), 0f, Mathf.Sin(StairBearing));
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

            // The walkway, centred on the wall's own circle so it overhangs both faces.
            int decks = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * radius /
                                                      Mathf.Max(0.5f, deck.size.x)));

            // Each board's own surface, taken from the wall underneath it rather than from one
            // height for the whole ring: the wall follows the ground wherever the ground could
            // not be flattened, and a level walkway over an unlevel wall is either buried in it
            // or floating over it. See ArenaRing.WallTopY.
            var surfaces = new float[decks];
            float highest = float.MinValue;

            for (int i = 0; i < decks; i++)
            {
                float angle = i * 2f * Mathf.PI / decks;
                var on = new Vector3(centre.x + Mathf.Cos(angle) * radius, 0f,
                                     centre.z + Mathf.Sin(angle) * radius);

                surfaces[i] = ArenaRing.WallTopY(on) + Lift;
                highest = Mathf.Max(highest, surfaces[i]);
            }

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
                int steps = 0;
                float walkway = float.MinValue;

                foreach (ZDO zdo in standing)
                {
                    if (zdo.GetPrefab() != deckHash) continue;

                    // The stair is made of these boards too, and carries its own marker so the
                    // two cannot be confused.
                    if (IsStep(zdo)) { steps++; continue; }

                    boards++;
                    walkway = Mathf.Max(walkway, zdo.GetPosition().y + deck.max.y);
                }

                // The stair counts as well as the walkway: a gallery with no way up is not a
                // gallery, and this is also how the ladder that was built before the stair
                // existed gets replaced rather than left standing beside it.
                if (boards >= decks && steps >= 2 && Mathf.Abs(walkway - highest) < Slack)
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
                    $"arena: took down {standing.Count} gallery piece(s) - rebuilding it, " +
                    $"topping out at {highest:0.0} m");
            }

            int made = 0;

            for (int i = 0; i < decks; i++)
            {
                float angle = i * 2f * Mathf.PI / decks;
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                var at = new Vector3(centre.x + outward.x * radius,
                                     surfaces[i] - deck.max.y,
                                     centre.z + outward.z * radius);

                Fixture.Place(deckHash, at, Quaternion.LookRotation(outward), StandPiece);
                made++;
            }

            for (int i = 0; i < rails; i++)
            {
                float angle = i * 2f * Mathf.PI / rails;
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                // On the walkway beside it, which is why this asks the wall again rather than
                // reusing a board's height: there are more rails than boards, or fewer, and the
                // two rings do not line up.
                var on = new Vector3(centre.x + outward.x * railRadius, 0f,
                                     centre.z + outward.z * railRadius);
                float surface = ArenaRing.WallTopY(on) + Lift;

                // Standing on the walkway, so its own base goes on the surface - not its origin.
                var at = new Vector3(on.x, surface - rail.min.y, on.z);

                Fixture.Place(railHash, at, Quaternion.LookRotation(outward), StandPiece);
                made++;
            }

            made += Stair(centre, deckHash, deck);

            _built = made > 0;

            VersePlugin.Log.LogInfo(
                $"arena: built the gallery - {decks} board(s) of walkway on top of the wall, " +
                $"topping out at {highest:0.0} m, {rails} piece(s) of {railName} railing at " +
                $"{railRadius:0.0} m, and a stair up the outside");

            return made;
        }

        /// <summary>
        /// A flight of steps from the boardwalk up to the walkway, sweeping around the outside of
        /// the wall.
        ///
        /// <para><b>This was a ladder, and the ladder did not work.</b> It went into the game as
        /// three stacked <c>wood_stepladder</c> and came back "not actually climbable". The
        /// reason is in the game's own code: there is no climbing in <c>Player</c> or
        /// <c>Character</c> at all. The only <c>Ladder</c> component is a lift that teleports you
        /// to a target transform, and the piece used here does not carry one - so a wood ladder
        /// is climbed purely by walking up its collider, which means it works only if it is the
        /// right way round, and which way round that is is a decision in a Unity scene this
        /// server cannot see. Guessing it from the collider shapes would be guessing.</para>
        ///
        /// <para>So the way up is made of the same boards as everything else, in steps low
        /// enough to walk up. A level 2 m tile has no orientation to get wrong, a
        /// <see cref="Rise"/> step is well inside what a player walks up without jumping, and
        /// every tread is measured off the prefab - which is the same reason the chests stopped
        /// floating. The flight hugs the wall at the boardwalk's own radius, so its top tile
        /// overlaps the walkway's outer edge and you simply walk on.</para>
        /// </summary>
        private static int Stair(Vector3 centre, int deckHash, Bounds deck)
        {
            // Valheim lets a player walk up a step of about half a metre without jumping; this
            // is comfortably inside that, and shallow enough that a wolf chasing somebody up it
            // is not a surprise either.
            const float Rise = 0.4f;

            // How far around the ring each step advances. Less than the board is long, so the
            // treads overlap and the flight reads as a stair rather than a row of shelves.
            const float Run = 1f;

            const int Most = 40;

            float radius = ArenaSite.Radius + ArenaRing.WallThickness * 0.5f +
                           Mathf.Max(0.5f, deck.size.z) * 0.5f;

            // Which way round to climb. The wall's top follows the ground, so sweeping towards
            // the low side is a shorter flight - and on ground that rises faster than the stair
            // does, sweeping the wrong way is a flight that never catches up with the walkway.
            float probe = 10f / radius;
            float up = ArenaRing.WallTopY(On(centre, StairBearing + probe, ArenaSite.Radius));
            float down = ArenaRing.WallTopY(On(centre, StairBearing - probe, ArenaSite.Radius));
            float sweep = down < up ? -1f : 1f;

            int made = 0;
            float y = float.NaN;

            for (int i = 0; i < Most; i++)
            {
                float bearing = StairBearing + sweep * i * Run / radius;
                var along = new Vector3(Mathf.Cos(bearing), 0f, Mathf.Sin(bearing));

                float x = centre.x + along.x * radius;
                float z = centre.z + along.z * radius;

                // The walkway's height on this bearing - asked at the wall's own circle, because
                // the wall is what the walkway is laid on and the wall follows the ground.
                float walkway = ArenaRing.WallTopY(On(centre, bearing, ArenaSite.Radius)) + Lift;

                // The first step starts one rise above the ground so there is something to step
                // up onto from the boardwalk, rather than a tile lying in it.
                if (float.IsNaN(y)) y = ArenaSite.HeightAt(x, z) + Rise;

                bool last = y >= walkway;
                float surface = last ? walkway : y;

                ZDO step = Fixture.Place(deckHash,
                                         new Vector3(x, surface - deck.max.y, z),
                                         Quaternion.LookRotation(along), StandPiece);
                step?.Set(StepPiece, 1, okForNotOwner: true);
                made++;

                if (last) break;

                y += Rise;
            }

            VersePlugin.Log.LogInfo(
                $"arena: a {made}-step stair up the outside of the wall at {radius:0.0} m, " +
                $"{Rise:0.00} m a step and {Run:0.00} m round the ring each time");

            return made;
        }

        /// <summary>A point on a circle of this radius about the centre, at this bearing.</summary>
        private static Vector3 On(Vector3 centre, float bearing, float radius) =>
            new Vector3(centre.x + Mathf.Cos(bearing) * radius, 0f,
                        centre.z + Mathf.Sin(bearing) * radius);

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
