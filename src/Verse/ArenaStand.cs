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
    /// <para><b>The way up took three goes, and each failure said something.</b> A ladder
    /// cannot work: Valheim has no climbing - nothing about it in <c>Player</c> or
    /// <c>Character</c>, and the only <c>Ladder</c> component is a lift that teleports whoever
    /// uses it to a target transform, which the piece used here does not carry. Steps did not
    /// work either: separate level plates 0.4 m apart came back as "I have to jump", because
    /// between the plates there is nothing to walk onto. So it is a <see cref="Ramp"/> - the
    /// same boards, tilted to the slope and overlapped into one continuous surface, which a
    /// character walks up the way it walks up a hill.</para>
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

        /// <summary>
        /// How far the standing walkway may be from where the wall now wants it before the
        /// gallery is relaid.
        ///
        /// <para>Tight, like the ring's own test, and for the same reason: both sides of the
        /// comparison are now worked out the same way, from the wall under each plank. It was a
        /// metre, which is the sort of error that reads in the game as a walkway hovering over
        /// the stone - the failure this whole venue has kept finding new ways to produce.</para>
        /// </summary>
        private const float Slack = 0.3f;

        /// <summary>
        /// Which way round the ring the way up starts, in radians: wherever the ground outside
        /// the wall is highest.
        ///
        /// <para>With the wall's top level, the climb is the height of the wall above the ground
        /// at its foot - so the high side of the site is the short way up. It was a fixed bearing
        /// of zero, which on the live site happened to land on the low side and produced a ramp
        /// of 10.6 m climbing 27 m around the ring. The same ramp on the high side is a third of
        /// that.</para>
        /// </summary>
        private static float StairBearing
        {
            get
            {
                Vector3 centre = ArenaSite.Centre;
                if (_bearingAt == centre) return _bearing;

                float radius = ArenaSite.Radius + ArenaRing.WallThickness * 0.5f + 1f;
                float best = 0f, highest = float.MinValue;

                for (int i = 0; i < 36; i++)
                {
                    float bearing = i * 2f * Mathf.PI / 36f;
                    Vector3 at = On(centre, bearing, radius);

                    float ground = ArenaSite.HeightAt(at.x, at.z);
                    if (ground <= highest) continue;

                    highest = ground;
                    best = bearing;
                }

                _bearing = best;
                _bearingAt = centre;
                return best;
            }
        }

        private static float _bearing;
        private static Vector3 _bearingAt = new Vector3(float.NaN, float.NaN, float.NaN);

        /// <summary>
        /// Where the second way up starts: opposite the first.
        ///
        /// <para>One ramp means that wherever somebody lands, half the time they walk the long
        /// way round a 26 m circle to use it. Two cost nine boards each.</para>
        /// </summary>
        private static float FarBearing => StairBearing + Mathf.PI;

        /// <summary>How far the railing's base is sunk below the walkway, to close the seam.</summary>
        private const float RailSink = 0.3f;

        /// <summary>How far the second course of railing overlaps the first.</summary>
        private const float RailOverlap = 1f;

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
                if (IsStand(zdo) && !Fixture.Doomed(zdo)) standing.Add(zdo);

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
            float lowest = float.MaxValue;

            for (int i = 0; i < decks; i++)
            {
                float angle = i * 2f * Mathf.PI / decks;
                var on = new Vector3(centre.x + Mathf.Cos(angle) * radius, 0f,
                                     centre.z + Mathf.Sin(angle) * radius);

                surfaces[i] = ArenaRing.WallTopY(on) + Lift;
                highest = Mathf.Max(highest, surfaces[i]);
                lowest = Mathf.Min(lowest, surfaces[i]);
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
                float under = float.MaxValue;

                // Where the way up starts now, so a ramp left on the far side of the ring by an
                // older build is noticed. The bearing is chosen from the ground rather than
                // written down, so it moves when the ground does.
                Vector3 foot = StairFoot(centre);
                Vector3 far = On(centre, FarBearing,
                                 ArenaSite.Radius + ArenaRing.WallThickness * 0.5f + 2f);
                bool atFoot = false, atFar = false;

                foreach (ZDO zdo in standing)
                {
                    if (zdo.GetPrefab() != deckHash) continue;

                    // The stair is made of these boards too, and carries its own marker so the
                    // two cannot be confused.
                    if (IsStep(zdo))
                    {
                        steps++;

                        Vector3 p = zdo.GetPosition();
                        float dx = p.x - foot.x, dz = p.z - foot.z;
                        if (dx * dx + dz * dz < 25f) atFoot = true;

                        float fx = p.x - far.x, fz = p.z - far.z;
                        if (fx * fx + fz * fz < 25f) atFar = true;

                        continue;
                    }

                    boards++;

                    float surface = zdo.GetPosition().y + deck.max.y;
                    walkway = Mathf.Max(walkway, surface);
                    under = Mathf.Min(under, surface);
                }

                // Both ends of the walkway, not just its top.
                //
                // Comparing the highest board alone kept a stepped walkway standing after the
                // wall's top was made level: the highest board was in the right place, every
                // other one was up to three metres below where it now belongs, and the gaps
                // that prompted the change were still there. A ring that is one height has a
                // walkway with no spread in it, so both ends have to match.
                //
                // The stair counts too: a gallery with no way up is not a gallery, and this is
                // how a ladder or a flight of steps from an older build gets replaced rather
                // than left standing beside the ramp.
                if (boards >= decks && steps >= 2 && atFoot && atFar &&
                    Mathf.Abs(walkway - highest) < Slack && Mathf.Abs(under - lowest) < Slack)
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

                // Standing on the walkway, its base sunk a little into it. The sink is what
                // closes the seam where the walkway is not perfectly flat - a railing sitting
                // exactly on a surface that steps leaves a wedge of daylight under it at every
                // step, which is what "weird gaps in the wall" turned out to be.
                var at = new Vector3(on.x, surface - rail.min.y - RailSink, on.z);

                Fixture.Place(railHash, at, Quaternion.LookRotation(outward), StandPiece);
                made++;

                // A second course on top of the first, and nothing subtle about why: one course
                // of bars is about 2 m and a player could just about jump it. This puts the top
                // at roughly 3 m over the walkway, which nobody clears.
                //
                // Offset half a piece around the ring so its bars fall between the first
                // course's rather than on top of them - two identical lattices in the same metre
                // of overlap would be coplanar, which renders as a flicker.
                float over = angle + Mathf.PI / rails;
                var above = new Vector3(centre.x + Mathf.Cos(over) * railRadius,
                                        surface - rail.min.y - RailSink + rail.size.y - RailOverlap,
                                        centre.z + Mathf.Sin(over) * railRadius);

                Fixture.Place(railHash, above,
                              Quaternion.LookRotation(new Vector3(Mathf.Cos(over), 0f,
                                                                  Mathf.Sin(over))),
                              StandPiece);
                made++;
            }

            // Two ways up, on opposite sides, so nobody walks half the circumference of the
            // arena to get to the gallery - which is what one ramp means wherever you land.
            made += Ramp(centre, StairBearing, deckHash, deck);
            made += Ramp(centre, FarBearing, deckHash, deck);

            _built = made > 0;

            VersePlugin.Log.LogInfo(
                $"arena: built the gallery - {decks} board(s) of walkway on top of the wall, " +
                $"topping out at {highest:0.0} m, {rails} piece(s) of {railName} railing at " +
                $"{railRadius:0.0} m, and a stair up the outside");

            return made;
        }

        /// <summary>
        /// A ramp from the boardwalk up to the walkway, sweeping around the outside of the wall.
        ///
        /// <para><b>Steps did not work either, and the reason is instructive.</b> This was a
        /// ladder first - which cannot work at all, because Valheim has no climbing (see the
        /// class note). It became a flight of level boards 0.4 m apart, which is inside the
        /// half-metre a player is usually said to step over, and the report from the game was
        /// "when my character tries to walk up the stairs it doesn't work, I have to jump". A
        /// stack of separate plates is not a stair: between the plates there is nothing, so a
        /// player walks into the next plate's edge rather than onto its surface, and whether
        /// they ride up it is a question about a capsule and a collider rather than about
        /// geometry.</para>
        ///
        /// <para>So the way up is now a <i>surface</i>: the same boards, tilted to the slope and
        /// overlapped along it, which is a continuous ramp a character walks up the way it walks
        /// up a hill. A floor tile has one axis that is its own up, which <see cref="Footing"/>
        /// measures, so the rotation is arithmetic: look along the slope, with the surface normal
        /// as up. Nothing about it depends on how the game treats steps, and the angle - capped
        /// at <see cref="MaxSlope"/>, well under where Valheim starts sliding you back down - is
        /// the only thing that has to be right.</para>
        /// </summary>
        private static int Ramp(Vector3 centre, float from, int deckHash, Bounds deck)
        {
            // Shallower than anything Valheim slides you down, and shallower than it needs to be:
            // a spectator climbing to the gallery is not a challenge to be set.
            const float MaxSlope = 22f;

            // How far around the ring each board advances. Shorter than the board, so consecutive
            // boards overlap along the slope and the surface is unbroken.
            const float Run = 1.6f;

            const int Most = 40;

            float radius = ArenaSite.Radius + ArenaRing.WallThickness * 0.5f +
                           Mathf.Max(0.5f, deck.size.z) * 0.5f;

            // Which way round to climb. With the wall's top level the climb is the same height
            // either way, but the ground is not: sweeping towards the higher ground is the
            // shorter ramp.
            float probe = 10f / radius;
            Vector3 sunwise = On(centre, from + probe, radius);
            Vector3 widdershins = On(centre, from - probe, radius);
            float sweep = ArenaSite.HeightAt(widdershins.x, widdershins.z) >
                          ArenaSite.HeightAt(sunwise.x, sunwise.z) ? -1f : 1f;

            Vector3 foot = On(centre, from, radius);
            float bottom = ArenaSite.HeightAt(foot.x, foot.z);
            float walkway = ArenaRing.WallTopY(On(centre, from, ArenaSite.Radius)) + Lift;

            float climb = walkway - bottom;
            if (climb <= 0.1f) return 0;

            // Evenly divided, so the last board lands exactly on the walkway rather than a
            // fraction of a step under or over it.
            int boards = Mathf.Clamp(
                Mathf.CeilToInt(climb / (Run * Mathf.Tan(MaxSlope * Mathf.Deg2Rad))), 1, Most);
            float rise = climb / boards;
            float slope = Mathf.Atan2(rise, Run);

            for (int i = 0; i < boards; i++)
            {
                // Half a step along from the bottom of this board's own span, so the board is
                // centred on the piece of ramp it is paving.
                float bearing = from + sweep * (i + 0.5f) * Run / radius;
                var along = new Vector3(Mathf.Cos(bearing), 0f, Mathf.Sin(bearing));

                // The way the ramp is actually travelling. Cross(up, radial) points the other
                // way round the circle from increasing bearing, and using it tilted every board
                // backwards: the report from the game was "the ramp is angled the wrong way",
                // which is exactly what a flight of boards each sloping against the climb looks
                // like. Cross(radial, up) is the direction the positions move in.
                Vector3 tangent = Vector3.Cross(along, Vector3.up) * sweep;

                // Up the slope, and the surface normal perpendicular to it. LookRotation maps
                // the piece's own forward onto the first and its own up onto the second.
                Vector3 up = tangent * Mathf.Cos(slope) + Vector3.up * Mathf.Sin(slope);
                Vector3 normal = Vector3.Cross(up, along).normalized;
                if (normal.y < 0f) normal = -normal;

                var facing = Quaternion.LookRotation(up, normal);

                float surface = bottom + rise * (i + 0.5f);
                Vector3 at = On(centre, bearing, radius);
                at.y = surface;
                at -= normal * deck.max.y;

                ZDO board = Fixture.Place(deckHash, at, facing, StandPiece);
                board?.Set(StepPiece, 1, okForNotOwner: true);
            }

            VersePlugin.Log.LogInfo(
                $"arena: a ramp of {boards} board(s) up the outside of the wall at " +
                $"{radius:0.0} m - {climb:0.0} m of climb at {slope * Mathf.Rad2Deg:0.#} degrees, " +
                $"from {bottom:0.0} m to the walkway at {walkway:0.0} m");

            return boards;
        }

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
