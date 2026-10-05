using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The ring itself: a wall around the arena, in the toughest material the server has, and
    /// bare ground inside it and for a good way outside it.
    ///
    /// <para><b>One copy, shared by every verse, and that is right rather than a compromise.</b>
    /// Static geometry nobody is meant to change is the single case verse-design.md's shared
    /// world handles properly: build it untagged and all eighteen verses stand in it for one
    /// copy's worth of ZDOs, against the ~1.2 GB of measured headroom. Build it per verse and
    /// the cost multiplies by the verse count for no gain. When a troll does put a wall
    /// through, <see cref="Destruction"/> masks it for that verse alone and leaves it standing
    /// for everyone else - which is why <see cref="HideMask.Reveal"/> exists and why
    /// <see cref="Repair"/> runs at the start of every run.
    ///
    /// <para><b>The ground is cleared once, for everybody, and well past the wall.</b> The ring
    /// sits in real terrain, so it and the apron around it hold the biome's own trees, bushes and
    /// rocks, and those are shared world objects: picking a cloudberry inside the arena forks it
    /// to that verse for good and felling a tree masks it for good. The first live run showed
    /// exactly that - a climbing `forked` count and a floor full of culled berries - and the venue
    /// would have degraded a little every run, permanently and differently per verse. Clearing the
    /// scenery once costs nothing anybody will miss in a place chosen because nobody plays there,
    /// and <see cref="Arena.ClearRadius"/> says how far out "the venue" reaches - wider than the
    /// wall, because a tree just outside it is in shot and ends up on the floor.</para>
    ///
    /// <para><b>Uniform base height, which the first build got wrong.</b> Placing each segment
    /// at its own local ground height left pieces that neither met their neighbours nor reached
    /// the ground, and stone needs support: 34 of 120 pieces collapsed on the first client to
    /// load them, and the repair pass dutifully resurrected them so they could collapse again.
    /// One base taken from the lowest ground on the ring, with enough rows to clear the
    /// highest, buries the wall on the high side instead - a few more pieces of stone and
    /// nothing else.</para>
    /// </summary>
    internal static class ArenaRing
    {
        /// <summary>Marks a piece as part of the ring, so it can be found and repaired.</summary>
        private static readonly int RingPiece = "verse.arena.ring".GetStableHashCode();

        /// <summary>
        /// A material the ring can be built from: the prefab, how wide one segment is and how
        /// tall one row is. The dimensions are not decoration - the circle is laid out from the
        /// width and the stack from the height - so a material cannot be swapped in without them.
        /// </summary>
        private struct WallKind
        {
            internal readonly string Prefab;
            internal readonly float Width;
            internal readonly float Height;

            internal WallKind(string prefab, float width, float height)
            {
                Prefab = prefab;
                Width = width;
                Height = height;
            }
        }

        /// <summary>
        /// What the ring may be built from. <b>Stone is first because it is the incumbent</b>: the
        /// comparison in <see cref="Wall"/> keeps the first of equals, so a material only displaces
        /// stone by being strictly tougher. A swap that doubles the piece count for the same
        /// durability is not an upgrade.
        ///
        /// <para><b>Which of these is toughest is read off the prefabs, not written down here.</b>
        /// Black marble and grausten are both meant to outlast stone, but by how much is a number
        /// in <c>WearNTear.m_health</c> that moves between builds of the game, and a constant in
        /// this file claiming otherwise would be wrong silently. <see cref="Wall"/> compares them
        /// at runtime and says in the log what it picked, with everything it compared.</para>
        ///
        /// <para><b>Half of a first guess at this list was not on the server at all.</b>
        /// <c>grausten_wall_4x2</c> and <c>blackmarble_2x2</c> are both in the game's own asset
        /// manifest and neither is in this server's <c>ZNetScene</c> - the live box has
        /// <c>grausten_pile</c> and no grausten walls, and the black marble blocks it has are
        /// 1x1x1, 2x1x1 and 2x2x2. The absent ones are kept because another install may have them
        /// and <see cref="Wall"/> skips what it cannot find, but the lesson is that the asset
        /// manifest is not the prefab table.</para>
        /// </summary>
        private static readonly WallKind[] Materials =
        {
            new WallKind("stone_wall_4x2", 4f, 2f),
            new WallKind("blackmarble_2x2x2", 2f, 2f),
            new WallKind("blackmarble_2x2", 2f, 2f),
            new WallKind("grausten_wall_4x2", 4f, 2f),
        };

        private static WallKind _wall = Materials[0];
        private static bool _wallChosen;

        /// <summary>
        /// Three rows, about 4.5 m above grade. Two was unjumpable but low to look at; four on a
        /// uniform base collapsed, because stone will not self-support an 8 m stack. Three with
        /// the bottom row buried 1.5 m into the ground is inside what stone holds - and the
        /// "restored N wall piece(s)" line at the next run start is how we find out if it is not.
        /// Kept at three for the tougher materials too: their support rules are stone's, so a
        /// stack that stood in stone stands in grausten and a taller one is a new experiment.
        /// </summary>
        private const int Rows = 3;

        /// <summary>How far the bottom row.s underside is buried, in metres.</summary>
        private const float Sink = -1.5f;

        /// <summary>How far the built radius may drift before the ring is rebuilt.</summary>
        private const float RadiusSlack = 1.5f;

        /// <summary>
        /// How far the standing wall's average height may be from the ground's before it is
        /// rebuilt. Generous, because on unlevelled ground the segments follow the terrain and
        /// their average is only roughly the height at the centre - this is here to catch a
        /// ring left on the hillside after the site was levelled, which is metres out, not
        /// centimetres.
        /// </summary>
        private const float HeightSlack = 1.5f;

        /// <summary>How wide the band is where the levelled ground eases back into the hillside.</summary>
        private const float LevelTaper = 8f;

        /// <summary>
        /// How far out the ground is levelled flat: past the wall, past the gates and their
        /// chests, and far enough to take the boardwalk, with the taper outside that again.
        /// </summary>
        private static float LevelReach => Arena.GateDistance + 4f;

        private static bool _built;

        /// <summary>Prefab names found occupying a site, for the refusal message.</summary>
        private static readonly List<string> Blocking = new List<string>();

        private static System.Reflection.FieldInfo _byId;
        private static System.Reflection.MethodInfo _destroy;
        private static readonly object[] DestroyArgs = new object[1];

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        /// <summary>
        /// Which material the ring is built from, resolved once the server has a prefab table to
        /// ask and then remembered. Logged, because "stronger than stone" is a claim and the log
        /// line is the evidence for it.
        /// </summary>
        private static WallKind Wall()
        {
            if (_wallChosen) return _wall;
            if (ZNetScene.instance == null) return _wall;

            string asked = (VersePlugin.ArenaWallPrefab.Value ?? "").Trim();
            if (asked.Length > 0 && !asked.Equals("auto", System.StringComparison.OrdinalIgnoreCase))
            {
                foreach (WallKind kind in Materials)
                {
                    if (!kind.Prefab.Equals(asked, System.StringComparison.OrdinalIgnoreCase)) continue;

                    _wall = kind;
                    _wallChosen = true;
                    VersePlugin.Log.LogInfo(
                        $"arena: the ring will be built from {kind.Prefab}, as ArenaWallPrefab asks");
                    return _wall;
                }

                // A prefab Verse has no dimensions for. Honoured rather than refused - it is a
                // deliberate setting - but the size is a guess, and a guess shows up as gaps.
                if (ZNetScene.instance.GetPrefab(asked.GetStableHashCode()) != null)
                {
                    _wall = new WallKind(asked, 4f, 2f);
                    _wallChosen = true;
                    VersePlugin.Log.LogWarning(
                        $"arena: building the ring from '{asked}', whose size Verse does not know " +
                        "- assuming 4 m wide and 2 m tall. Expect gaps or overlap if it is neither.");
                    return _wall;
                }

                VersePlugin.Log.LogWarning(
                    $"arena: no prefab called '{asked}' on this server - choosing the ring's " +
                    "material automatically instead");
            }

            var considered = new List<string>();
            WallKind best = default(WallKind);
            float bestHealth = -1f;

            foreach (WallKind kind in Materials)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(kind.Prefab.GetStableHashCode());
                if (prefab == null) continue;

                WearNTear wear = prefab.GetComponent<WearNTear>();
                float health = wear == null ? 0f : wear.m_health;
                considered.Add($"{kind.Prefab} {health:0} hp");

                if (health <= bestHealth) continue;
                best = kind;
                bestHealth = health;
            }

            if (bestHealth < 0f)
            {
                // Not even stone. Left unchosen so a later call can try again, and Ensure reports
                // the missing prefab by name.
                VersePlugin.Log.LogError(
                    "arena: none of the ring's wall materials exist on this server - the ring " +
                    "cannot be built");
                return _wall;
            }

            _wall = best;
            _wallChosen = true;
            VersePlugin.Log.LogInfo(
                $"arena: the ring will be built from {best.Prefab} at {bestHealth:0} hp a piece, " +
                "the toughest of " + string.Join(", ", considered.ToArray()));
            return _wall;
        }

        /// <summary>
        /// Builds the ring if the world does not already hold it at the configured radius, in the
        /// configured material. Idempotent, and cheap after the first call.
        /// </summary>
        internal static int Ensure()
        {
            if (_built) return 0;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return 0;
            if (!ArenaSite.Resolve(out Vector3 centre)) return 0;

            WallKind wall = Wall();
            int wallHash = wall.Prefab.GetStableHashCode();
            if (ZNetScene.instance.GetPrefab(wallHash) == null)
            {
                VersePlugin.Log.LogError($"arena: no '{wall.Prefab}' prefab - the ring cannot be built");
                return 0;
            }

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return 0;

            float radius = ArenaSite.Radius;

            // The ground first, and before the "already standing" test rather than after it.
            //
            // A server that already has a ring - which is every server this is deployed to -
            // would otherwise never level its site at boot: Ensure would recognise the ring,
            // return, and leave the venue on its hillside until somebody started a run. And the
            // moment the ground does move, a ring built on the old ground is at the wrong
            // height, which is why the test below now looks at the pieces' height as well as
            // their radius.
            LevelSite(centre);

            // Already standing - but at the right radius and the right height? ArenaRadius is a
            // config key, so it can change between runs, and a ring built at the old one would
            // be left as a second circle nobody asked for. Judged by where the pieces actually
            // are.
            List<ZDO> standing = Standing(all);

            if (standing.Count > 0)
            {
                float sum = 0f;
                float height = 0f;
                foreach (ZDO zdo in standing)
                {
                    Vector3 p = zdo.GetPosition();
                    sum += Mathf.Sqrt((p.x - centre.x) * (p.x - centre.x) +
                                      (p.z - centre.z) * (p.z - centre.z));
                    height += p.y;
                }

                float was = sum / standing.Count;

                // The mean of Rows row-centres is the base plus half the stack, so this is where
                // the pieces' average height should be for the ground as it is now.
                float wasY = height / standing.Count;
                float wantY = ArenaSite.HeightAt(centre.x, centre.z) + Sink +
                              wall.Height * Rows * 0.5f;
                int wantPieces = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * radius / wall.Width)) * Rows;

                // Judged on the piece count and the material as well as the radius. Changing Rows
                // to raise the walls used to do nothing at all: the radius still matched, so
                // Ensure decided the ring was already standing and skipped the rebuild, and the
                // only clue was a count in the log nobody was comparing to anything. The material
                // is in the test for the same reason and it is not hypothetical - swapping stone
                // for grausten changes neither the radius nor the piece count, so without this
                // line the stone ring would stand for ever and the new setting would do nothing.
                // Destroyed pieces do not affect any of it - Destruction masks them rather than
                // removing them - so the counts are stable for a given geometry.
                int wrongMaterial = 0;
                foreach (ZDO zdo in standing)
                    if (zdo.GetPrefab() != wallHash) wrongMaterial++;

                if (Mathf.Abs(was - radius) < RadiusSlack && standing.Count == wantPieces &&
                    wrongMaterial == 0 && Mathf.Abs(wasY - wantY) < HeightSlack)
                {
                    _built = true;
                    ArenaSite.Confirm();
                    VersePlugin.Log.LogInfo(
                        $"arena: the ring is already standing ({standing.Count} piece(s) of " +
                        $"{wall.Prefab} at {was:0.0} m, averaging {wasY:0.0} m high)");
                    return 0;
                }

                foreach (ZDO zdo in standing) Destroy(zdo);
                VersePlugin.Log.LogInfo(
                    $"arena: tore down {standing.Count} wall piece(s) built at {was:0.0} m" +
                    (wrongMaterial > 0 ? $", {wrongMaterial} of them the wrong material" : "") +
                    (Mathf.Abs(wasY - wantY) >= HeightSlack
                        ? $", standing at {wasY:0.0} m where the ground now wants {wantY:0.0} m"
                        : "") +
                    $" - the ring is now {wall.Prefab} on a {radius:0} m circle");
            }

            // Refuse to build on top of anybody. On an empty test world every site is fair game;
            // on a world with eighteen verses and real bases, a scan that lands on someone.s
            // hall would put a stone wall through it and strip the trees around it, for every
            // verse, permanently. Verse-tagged objects are by definition somebody.s work
            // (Authorship only ever tags what a client created), so their presence is the test.
            int occupied = Occupants(all, centre, radius);

            if (occupied > 0)
            {
                VersePlugin.Log.LogError(
                    $"arena: REFUSING to build the ring at {centre.x:0}, {centre.z:0} - " +
                    $"{occupied} object(s) there belong to a verse, so somebody is using that " +
                    "ground. Pick another spot: set ArenaSitePoint by hand, or raise " +
                    "ArenaMinDistance and clear ArenaSitePoint to rescan. Found: " +
                    string.Join(", ", Blocking.ToArray()));
                return 0;
            }

            int cleared = Flatten(centre);

            // Before anything is placed: every height below is read back through
            // ArenaSite.HeightAt, which answers with the levelled ground once this has run.
            LevelSite(centre);

            int made = Raise(centre, wall, radius);
            int segments = made / Rows;

            _built = true;
            ArenaSite.Confirm();
            VersePlugin.Log.LogInfo(
                $"arena: built the ring - {made} piece(s) of {wall.Prefab}, {segments} segments x " +
                $"{Rows} rows following the ground around a {radius:0} m circle at " +
                $"{centre.x:0}, {centre.z:0}; cleared {cleared} piece(s) of scenery out to " +
                $"{Arena.ClearRadius:0} m");

            return made;
        }

        /// <summary>
        /// Flattens the site before anything is built on it, and paints the fighting floor.
        ///
        /// <para><b>This is what the ring's own history was asking for.</b> Two earlier builds
        /// collapsed over uneven ground and the surviving design follows the terrain per
        /// segment, with the chest decks hung level above whatever was under them - all of it
        /// working around ground the server could not change. It can:
        /// <see cref="Ground.Level"/> writes a zone's terrain deltas straight into its
        /// compiler's ZDO. So the floor is made flat first, and the wall, the decks, the chests
        /// and the boardwalk are all placed on a known height instead of a sampled one.</para>
        ///
        /// <para>Level out past the gates, easing back into the hillside over the last few
        /// metres so the venue is a plateau rather than a plinth, and the fighting floor painted
        /// paved: <c>ClutterSystem</c> reads that as cleared ground and stops growing grass
        /// through it, which is also why the floor no longer needs sweeping for berries.</para>
        /// </summary>
        internal static int LevelSite(Vector3 centre)
        {
            if (!VersePlugin.ArenaLevelGround.Value) return 0;

            float target = ArenaSite.HeightAt(centre.x, centre.z);

            // The whole venue first, with no paint: this is the pass that decides the height.
            int moved = Ground.Level(centre, LevelReach + LevelTaper, target, LevelTaper);

            // Then the floor inside the wall again, for the paint alone - same height, so every
            // vertex it touches is already where this wants it.
            moved += Ground.Level(centre, ArenaSite.Radius, target, 0f, Heightmap.m_paintMaskPaved);

            if (moved > 0)
                VersePlugin.Log.LogInfo(
                    $"arena: levelled the site to {target:0.00} m - flat out to {LevelReach:0} m, " +
                    $"easing back over the next {LevelTaper:0} m, floor paved inside " +
                    $"{ArenaSite.Radius:0} m");

            return moved;
        }

        /// <summary>
        /// How thick the wall is, measured off its own prefab: a wall piece's forward axis is
        /// its thickness, which is why <see cref="Raise"/> points that axis along the radius.
        ///
        /// <para>Needed by everything that has to meet the wall rather than guess at it - the
        /// boardwalk outside its foot, the gallery on its top, the railing along its inner
        /// edge. A number written down here would be wrong the moment the ring is built from
        /// grausten instead of stone.</para>
        /// </summary>
        internal static float WallThickness =>
            Footing.Box(Wall().Prefab.GetStableHashCode(), out Bounds box) ? box.size.z : 1f;

        /// <summary>
        /// The height of the top of the wall, where the gallery goes. The bottom row is buried
        /// by <see cref="Sink"/>, so this is <see cref="Rows"/> rows of wall above that.
        /// </summary>
        internal static float WallTopY(Vector3 centre) =>
            ArenaSite.HeightAt(centre.x, centre.z) + Sink + Wall().Height * Rows;

        /// <summary>The ring's own pieces, wherever they are and whatever they are made of.</summary>
        private static List<ZDO> Standing(Dictionary<ZDOID, ZDO> all)
        {
            var standing = new List<ZDO>();
            foreach (ZDO zdo in all.Values)
                if (zdo.GetInt(RingPiece, 0) == 1) standing.Add(zdo);
            return standing;
        }

        /// <summary>
        /// Writes the circle into the object table and returns how many pieces it made. The
        /// geometry, and nothing else - the caller decides whether the ground was clear enough to
        /// be doing this.
        ///
        /// <para><b>Per-segment ground height, sunk deep, and only three rows.</b> Both earlier
        /// attempts collapsed, for opposite reasons, and the second is the instructive one.
        /// Placing each segment at its own ground height sunk 0.3 m left pieces floating on a
        /// slope: 34 of 120 gone. Replacing that with one uniform base taken from the lowest
        /// ground meant stacking four rows to clear the highest - and stone will not self-support
        /// an 8 m stack, so 120 of 164 went, which is 41 segments x the top three rows almost
        /// exactly.</para>
        ///
        /// <para>So: follow the terrain, bury the bottom row properly, and stay short. Three rows
        /// reach about 4.5 m above grade, well over Valheim's ~1.2 m jump and well inside what
        /// stone holds up, and adjacent segments differ by ~0.2 m across this site's ground
        /// variance, so following the terrain leaves no gaps.</para>
        /// </summary>
        private static int Raise(Vector3 centre, WallKind wall, float radius)
        {
            int wallHash = wall.Prefab.GetStableHashCode();
            int segments = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * radius / wall.Width));
            int made = 0;

            Authorship.Suspend();
            try
            {
                for (int i = 0; i < segments; i++)
                {
                    float angle = i * 2f * Mathf.PI / segments;
                    float x = centre.x + Mathf.Cos(angle) * radius;
                    float z = centre.z + Mathf.Sin(angle) * radius;

                    // Facing outward along the radius, which leaves the wall tangent to the
                    // circle - a wall piece's forward axis is its thickness.
                    var facing = Quaternion.LookRotation(
                        new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)));

                    float ground = ArenaSite.HeightAt(x, z);

                    for (int row = 0; row < Rows; row++)
                    {
                        // Bottom row spans ground-1.5 to ground+0.5; the second carries on up.
                        var at = new Vector3(x, ground + Sink + wall.Height * (row + 0.5f), z);

                        // Shared, persistent and unbreakable; deliberately NOT tagged to a verse,
                        // because untagged means every verse is sent it, which is what one shared
                        // fixture should be. See Fixture for the health.
                        Fixture.Place(wallHash, at, facing, RingPiece);
                        made++;
                    }
                }
            }
            finally
            {
                Authorship.Resume();
            }

            return made;
        }

        /// <summary>
        /// Tears the ring down and raises it again, however sound it looked. Returns the number
        /// of pieces now standing, or 0 if it could not be done - in which case the old ring is
        /// still up, because nothing is destroyed until the rebuild is known to be possible.
        ///
        /// <para><b>Why a run cannot just repair it.</b> <see cref="Repair"/> un-hides pieces a
        /// verse has broken, and for a verse that broke one that is enough. A piece destroyed in
        /// the shared world is not masked and does not come back, and the ring has no way to
        /// notice it is one segment short: <see cref="Ensure"/> runs once per boot and only
        /// compares counts when it is rebuilding for another reason. So the wall slowly loses
        /// segments between restarts, and the hole is in the venue for every verse.</para>
        ///
        /// <para><b>No occupancy check here, deliberately.</b> <see cref="Ensure"/> refuses to
        /// build on ground a verse is using, which is right when it is choosing to put a ring
        /// somewhere - but this runs with the old ring already down, and a refusal at that point
        /// would leave the arena with no wall at all. Where the ring goes was settled at boot;
        /// this only puts it back.</para>
        /// </summary>
        internal static int Rebuild()
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null) return 0;
            if (!ArenaSite.Resolve(out Vector3 centre)) return 0;

            WallKind wall = Wall();
            int wallHash = wall.Prefab.GetStableHashCode();
            if (ZNetScene.instance.GetPrefab(wallHash) == null)
            {
                VersePlugin.Log.LogError(
                    $"arena: no '{wall.Prefab}' prefab - the ring cannot be rebuilt, leaving the " +
                    "one that is standing");
                return 0;
            }

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return 0;

            List<ZDO> standing = Standing(all);
            foreach (ZDO zdo in standing)
            {
                // The mask goes with the piece. These are the very pieces a troll had broken, so
                // without this every run would leave a handful of entries behind for a ZDOID that
                // no longer exists.
                HideMask.Forget(zdo);
                Destroy(zdo);
            }

            // Re-levelled as well as rebuilt, and both are idempotent: a site that is already
            // flat costs one pass over the zone's vertices and writes nothing.
            LevelSite(centre);

            int made = Raise(centre, wall, ArenaSite.Radius);
            _built = made > 0;

            // The venue's woodwork stands on the wall and against its foot, so it is looked at
            // again whenever the wall moves - the first level of the site raises the whole thing
            // by the height of the hill that used to be there. Both are idempotent and give up
            // after one scan of the table when nothing has changed.
            ArenaApron.Forget();
            ArenaStand.Forget();
            ArenaApron.Ensure();
            ArenaStand.Ensure();

            VersePlugin.Log.LogInfo(
                $"arena: rebuilt the ring - {standing.Count} piece(s) down, {made} piece(s) of " +
                $"{wall.Prefab} up on a {ArenaSite.Radius:0} m circle");

            return made;
        }

        /// <summary>
        /// How many things somebody has *built* stand inside this circle.
        ///
        /// <para><b>Buildings, not merely verse-tagged objects.</b> The first version counted
        /// anything with a verse tag, which immediately locked the arena out of its own site:
        /// after one session of play the ring held 114 tagged objects - tombstones, dropped kit,
        /// the gate chests, leftover creatures - so raising the walls tore the ring down and then
        /// refused to rebuild it, leaving no arena at all. A base is pieces somebody placed; a
        /// corpse and a dropped sword are not, and the arena's own furniture certainly is not.</para>
        /// </summary>
        private static int Occupants(Dictionary<ZDOID, ZDO> all, Vector3 centre, float radius)
        {
            float r2 = radius * radius;
            int n = 0;

            foreach (ZDO zdo in all.Values)
            {
                if (!ZdoVerse.Tagged(zdo)) continue;
                if (Arena.Ours(zdo) || zdo.GetInt(RingPiece, 0) == 1) continue;

                Vector3 p = zdo.GetPosition();
                float dx = p.x - centre.x, dz = p.z - centre.z;
                if (dx * dx + dz * dz > r2) continue;

                GameObject prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                if (prefab == null) continue;

                // Only something placed with a hammer counts as "somebody is using this".
                if (prefab.GetComponent<Piece>() == null) continue;
                if (prefab.GetComponent<TombStone>() != null) continue;

                // Named, not just counted. Refusing with a number taught us nothing twice over;
                // the prefab names say in one restart whether this is a base or the arena's own
                // furniture.
                if (Blocking.Count < 12 && !Blocking.Contains(prefab.name)) Blocking.Add(prefab.name);
                n++;
            }

            return n;
        }

        /// <summary>
        /// Clears the biome's own scenery out of the arena and the apron around it, for every
        /// verse at once.
        ///
        /// <para>Only shared objects are touched - anything already tagged to a verse is
        /// somebody's, however unlikely that is out here - and only things that are scenery.
        /// Terrain is left alone entirely: it is <c>TerrainComp</c>, it is not in this list, and
        /// levelling the ground is not this method's business.</para>
        /// </summary>
        internal static int Clear()
        {
            return ArenaSite.Resolve(out Vector3 centre) ? Flatten(centre) : 0;
        }

        private static int Flatten(Vector3 centre)
        {
            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return 0;

            var doomed = new List<ZDO>();
            foreach (ZDO zdo in all.Values)
            {
                if (ZdoVerse.Tagged(zdo)) continue;

                // The arena's own fixtures, which all three markers exist to protect: the wall,
                // the decks under the gate chests, and the chests themselves - a chest carries
                // the arena's marker, and the deck is a building piece this method would
                // otherwise tear up one second after ArenaDeck laid it.
                // ...and the boardwalk and the gallery, which are building pieces this method
                // would otherwise take up one second after they were laid.
                if (zdo.GetInt(RingPiece, 0) == 1 || ArenaDeck.IsDeck(zdo) || Arena.Ours(zdo) ||
                    ArenaApron.IsApron(zdo) || ArenaStand.IsStand(zdo)) continue;

                // Out to Arena.ClearRadius, which is wider than the wall, wider than the gates
                // and wider than the tolerance a fighter is judged by. It was the stray tolerance
                // once, for the sound reason that the gate chests sit outside the ring and a fir
                // tree growing through somebody's deposit chest is the same nuisance there as it
                // is on the floor - but it left the arena standing in a wood. A tree just beyond
                // the wall is in every shot, and a wave that smashes it leaves the trunk on the
                // floor, so the venue is cleared rather than the fighting circle.
                if (!Arena.InClearing(zdo.GetPosition())) continue;

                GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab == null) continue;

                bool scenery =
                    prefab.GetComponent<Pickable>() != null ||
                    prefab.GetComponent<TreeBase>() != null ||
                    prefab.GetComponent<TreeLog>() != null ||
                    prefab.GetComponent<MineRock5>() != null ||
                    prefab.GetComponent<MineRock>() != null ||
                    prefab.GetComponent<Destructible>() != null ||
                    // Lodges, ruins, abandoned camps and their fences: world-generated
                    // structures are building pieces rather than scenery, so none of the tests
                    // above see them, and a hut inside the clearing is as much in the way as a
                    // boulder. Only untagged ones reach this line, and Occupants refuses to
                    // build the ring anywhere a verse has built anything, so this cannot be
                    // somebody's hall.
                    prefab.GetComponent<WearNTear>() != null;

                if (!scenery) continue;

                // Never a location marker: those are how the world remembers what it generated
                // where, and removing one is not undone by anything.
                if (prefab.GetComponent<Location>() != null) continue;

                // Never a grave, and never anything else that holds items. A tombstone is a
                // player's whole inventory and Arena.ClearGraves is the only thing allowed to
                // touch one; a world chest in a ruin is somebody's find either way. Cheap
                // insurance in a method that now runs over the gates, where the chests are.
                if (prefab.GetComponent<TombStone>() != null) continue;
                if (prefab.GetComponent<Container>() != null) continue;

                doomed.Add(zdo);
            }

            foreach (ZDO zdo in doomed) Destroy(zdo);
            return doomed.Count;
        }

        /// <summary>
        /// Un-hides the ring's own pieces from a verse, so a wall a troll put through last run is
        /// standing again for this one.
        ///
        /// <para><see cref="Destruction"/> masks a shared object rather than destroying it, which
        /// is what keeps one verse from demolishing everybody's arena - but it also makes the
        /// damage permanent for the verse that did it. This is the only caller of
        /// <see cref="HideMask.Reveal"/> and the reason it was written.</para>
        /// </summary>
        internal static int Repair(int verse)
        {
            if (verse == Verses.None) return 0;

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return 0;

            int repaired = 0;
            int hardened = 0;

            foreach (ZDO zdo in all.Values)
            {
                // Every fixture, and all for the same reason: they are shared, so a troll that
                // puts one through has broken it for that verse for good otherwise - a missing
                // deck is a chest standing on nothing, and a missing railing is a spectator
                // dropping into somebody's run.
                bool fixture = zdo.GetInt(RingPiece, 0) == 1 || ArenaDeck.IsDeck(zdo) ||
                               ArenaApron.IsApron(zdo) || ArenaStand.IsStand(zdo);
                if (!fixture) continue;

                if (HideMask.Reveal(zdo, verse)) repaired++;

                // Health re-asserted as well as masks lifted: this is what makes the rule stick
                // for a venue built before the rule existed, and for anything a player has
                // repaired back down to its prefab's own health with a hammer.
                if (Fixture.Harden(zdo)) hardened++;
            }

            if (repaired > 0 || hardened > 0)
                VersePlugin.Log.LogInfo(
                    $"arena: restored {repaired} fixture(s) for verse {verse}" +
                    (hardened > 0 ? $" and made {hardened} of them unbreakable again" : ""));

            return repaired;
        }

        /// <summary>
        /// Removes ring pieces that are nowhere near the current site - leftovers from a ring
        /// built before <c>ArenaBiome</c> or <c>ArenaRadius</c> was changed.
        ///
        /// <para>Needed because moving the arena abandons its ring rather than taking it with
        /// it, and the teardown in <see cref="Ensure"/> judges by the average distance of every
        /// ring piece in the world - which is a useless measure once there are rings at three
        /// different sites. This judges each piece on its own position instead, which is what
        /// should have been done in the first place.</para>
        ///
        /// <para>An operator tool rather than something automatic: it deletes world objects
        /// outright, and deciding that a stone wall 9 km away is rubbish rather than somebody.s
        /// is a judgement a person should make.</para>
        /// </summary>
        internal static int PurgeStrays(out int kept, out float furthest)
        {
            kept = 0;
            furthest = 0f;

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null || !ArenaSite.Resolve(out Vector3 centre)) return 0;

            float keepWithin = ArenaSite.Radius + 8f;
            var strays = new List<ZDO>();

            foreach (ZDO zdo in all.Values)
            {
                if (zdo.GetInt(RingPiece, 0) != 1) continue;

                Vector3 p = zdo.GetPosition();
                float d = Mathf.Sqrt((p.x - centre.x) * (p.x - centre.x) +
                                     (p.z - centre.z) * (p.z - centre.z));

                if (d <= keepWithin) { kept++; continue; }

                if (d > furthest) furthest = d;
                strays.Add(zdo);
            }

            foreach (ZDO zdo in strays) Destroy(zdo);
            return strays.Count;
        }

        /// <summary>
        /// How many ring pieces exist right now. Reported with the metrics so the count can be
        /// watched rather than reasoned about: it has drifted upwards across boots in exact
        /// multiples of one ring, and the two explanations - a second build, or clients
        /// re-creating pieces the server deleted - look identical from a single boot.
        /// </summary>
        internal static int Count() => Count(out int _);

        /// <summary>
        /// The same count, and the gate decks' tiles with it.
        ///
        /// <para>One pass for both on purpose. This walks every ZDO in the world - a million of
        /// them on the live world - and the metrics line asks every ten seconds, so counting the
        /// decks separately would have doubled the only part of that line that is not free.</para>
        /// </summary>
        internal static int Count(out int deck)
        {
            deck = 0;

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return -1;

            int n = 0;
            foreach (ZDO zdo in all.Values)
            {
                if (zdo.GetInt(RingPiece, 0) == 1) n++;
                else if (ArenaDeck.IsDeck(zdo)) deck++;
            }

            return n;
        }

        /// <summary>Whether a ZDO is part of the ring - so the guard never culls its own walls.</summary>
        internal static bool IsRing(ZDO zdo) => zdo != null && zdo.GetInt(RingPiece, 0) == 1;

        /// <summary>
        /// Destroys a ZDO, taking ownership of it first.
        ///
        /// <para><b>The ownership claim is the whole method.</b> <c>ZDOMan.DestroyZDO</c> is
        /// <c>if (zdo.IsOwner()) m_destroySendList.Add(...)</c> - a silent no-op for anything
        /// the server does not own. And <c>ReleaseNearbyZDOS</c> hands every persistent ZDO in
        /// a player's active area to that player every two seconds, so essentially nothing in
        /// the ring is ever the server's: dropped loot, wandering wildlife, scenery, even the
        /// arena's own creatures once somebody stands near them. Without the claim, every cull
        /// this plugin makes does nothing, the swept objects come straight back, and the only
        /// evidence is a sweep that keeps finding the same things - which is how this was
        /// found, after a session of counting culls that never happened.</para>
        /// </summary>
        private static void Destroy(ZDO zdo)
        {
            if (zdo == null) return;

            _destroy = _destroy ?? AccessTools.Method(typeof(ZDOMan), "DestroyZDO", new[] { typeof(ZDO) });
            if (_destroy == null) return;

            zdo.SetOwner(ZDOMan.GetSessionID());

            DestroyArgs[0] = zdo;
            _destroy.Invoke(ZDOMan.instance, DestroyArgs);
        }
    }
}
