using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Terrain edits, written from the server with no client involved.
    ///
    /// <para><b>Why this looked impossible and is not.</b> A dedicated server holds no
    /// GameObjects out where the players are - no zones, no <c>Heightmap</c>, no
    /// <c>TerrainComp</c> - so none of vanilla's terrain machinery can be called here. But the
    /// machinery is not where the terrain lives. Every edit a hoe has ever made to a zone is one
    /// byte array on one ZDO: <c>TCData</c> on a <c>_TerrainCompiler</c> object sitting at the
    /// zone's centre, holding a per-vertex height delta for the zone's 65x65 grid. The server
    /// owns the object table, so the server can write that array, and the client applies it in
    /// <c>Heightmap.ApplyModifiers</c> the same way it applies a player's own digging. The
    /// format is read off <c>TerrainComp.Save</c>/<c>Load</c> and matched exactly, because
    /// <c>Load</c> abandons the whole array on a length mismatch and logs one warning.</para>
    ///
    /// <para><b>The delta is against generated ground, which is why this works at all.</b>
    /// <c>ApplyToHeightmap</c> adds <c>m_levelDelta</c> to the height the client generated from
    /// the seed, so to put a vertex at a chosen height the server needs that generated height -
    /// and generation is a pure function of the seed that runs perfectly well here. One subtlety
    /// decides whether a levelled floor is flat or ribbed: a zone's vertex heights are not
    /// <c>WorldGenerator.GetHeight</c>. <c>HeightmapBuilder.Build</c> takes the biome at the
    /// zone's four <i>corners</i> and, when they differ, blends four whole biome heights across
    /// the zone with a smoothstep - so a vertex in a Meadows zone that touches Black Forest is
    /// not at its own biome's height. <see cref="Blended"/> reproduces that, because an error of
    /// a few centimetres per vertex is a floor with a texture to it - and on a biome edge the
    /// error is more than a metre.</para>
    ///
    /// <para><b>What it cannot do.</b> <c>ApplyToHeightmap</c> clamps the result to the
    /// generated height plus or minus 8 m, and <c>m_levelDelta</c> is clamped to the same, so
    /// this flattens a slope and not a cliff. A site with more than 16 m of fall across it
    /// cannot be levelled by anybody, hoe included.</para>
    ///
    /// <para>Deliberately idempotent and absolute: vanilla's <c>LevelTerrain</c> accumulates
    /// onto whatever delta is already there, which is right for a player tapping a hoe and
    /// wrong for a server that re-runs its own setup on every boot. <see cref="Level"/> sets the
    /// delta it wants, so running it twice is running it once.</para>
    /// </summary>
    internal static class Ground
    {
        /// <summary>The ceiling vanilla puts on a single vertex's level delta, in metres.</summary>
        private const float Clamp = 8f;

        private static System.Reflection.FieldInfo _byId;

        private static int _width;
        private static float _scale;
        private static int _pitch;
        private static int _vertices;
        private static int _compilerHash;
        private static string _compilerName;
        private static bool _geometry;

        /// <summary>Decoded zone data, by packed zone id. One entry per zone ever touched.</summary>
        private static readonly Dictionary<int, Patch> Patches = new Dictionary<int, Patch>();

        /// <summary>The four corner biomes of each zone looked at, by packed zone id.</summary>
        private static readonly Dictionary<int, Corners> Zones = new Dictionary<int, Corners>();

        /// <summary>
        /// What <c>HeightmapBuilder</c> needs to know about a zone to build its vertices: the
        /// biome at each of its four corners, and where its lower-left corner is.
        /// </summary>
        private class Corners
        {
            internal Vector3 Corner;
            internal Heightmap.Biome B0, B1, B2, B3;
            internal bool One;                    // all four the same, so no blending
        }

        /// <summary>
        /// One zone's terrain edits, decoded, plus the generated heights they are deltas
        /// against.
        /// </summary>
        private class Patch
        {
            internal Vector2s Zone;
            internal ZDO Zdo;
            internal uint Revision = uint.MaxValue;
            internal Vector3 Corner;          // the zone's lower-left vertex, in world space
            internal int Operations;
            internal Vector3 LastPoint;
            internal float LastRadius;
            internal bool[] Modified;
            internal float[] Level;
            internal float[] Smooth;
            internal bool[] Painted;
            internal Color[] Mask;
            internal float[] Generated;       // filled on demand; null until needed
            internal bool Unreadable;         // a payload in a shape we refuse to rewrite
        }

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        /// <summary>
        /// The zone grid and the compiler prefab, read off the zone prefab itself rather than
        /// written down: the heightmap is 64 vertices wide at 1 m today, and an array sized from
        /// a constant that the game has since changed is a terrain edit that silently does
        /// nothing.
        /// </summary>
        private static bool Geometry()
        {
            if (_geometry) return true;
            if (ZoneSystem.instance == null || ZNetScene.instance == null) return false;

            Heightmap hmap = ZoneSystem.instance.m_zonePrefab != null
                ? ZoneSystem.instance.m_zonePrefab.GetComponentInChildren<Heightmap>()
                : null;

            if (hmap == null || hmap.m_terrainCompilerPrefab == null)
            {
                VersePlugin.Log.LogWarning(
                    "terrain: the zone prefab has no heightmap or no terrain compiler on it - " +
                    "no ground can be levelled");
                return false;
            }

            _width = hmap.m_width;
            _scale = hmap.m_scale;
            _pitch = _width + 1;
            _vertices = _pitch * _pitch;
            _compilerName = hmap.m_terrainCompilerPrefab.name;
            _compilerHash = _compilerName.GetStableHashCode();

            if (ZNetScene.instance.GetPrefab(_compilerHash) == null)
            {
                VersePlugin.Log.LogWarning(
                    $"terrain: '{_compilerName}' is not in this server's prefab table, so a " +
                    "compiler written here could not be instantiated by any client");
                return false;
            }

            _geometry = true;
            VersePlugin.Log.LogInfo(
                $"terrain: zones are {_width}x{_width} vertices at {_scale:0.##} m " +
                $"({_vertices} per zone), compiler prefab '{_compilerName}'");
            return true;
        }

        /// <summary>Confirms what the startup check needs: the grid and the prefab.</summary>
        internal static string Resolve()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (_byId == null) return "ZDOMan.m_objectsByID not found (terrain)";
            return Geometry() ? null : "the zone heightmap or terrain compiler prefab was not found";
        }

        private static int Key(Vector2s zone) => (zone.x << 16) ^ (ushort)zone.y;

        /// <summary>
        /// The height a client will generate for a point, which is not
        /// <c>WorldGenerator.GetHeight</c>.
        ///
        /// <para><b>This difference is worth more than a metre, and it was found by accident.</b>
        /// A zone's vertices come from <c>HeightmapBuilder.Build</c>, which asks the biome at the
        /// zone's four <i>corners</i> and, when they are not all the same, blends all four biome
        /// heights across the zone with a smoothstep. <c>GetHeight</c> instead asks the biome at
        /// the point itself. On a zone that straddles a biome edge the two answers differ - on
        /// this test world by 1.16 m - and the one the terrain is actually built from is this
        /// one. So this is what the arena measures its ground by, levelled or not: placing a
        /// chest at <c>GetHeight</c> on a biome boundary buries it or floats it by that
        /// difference, which is the floating-chest bug again with a different cause.</para>
        ///
        /// <para>Four noise samples rather than one, because the answer is interpolated between
        /// the four vertices around the point: the terrain is a mesh of triangles between
        /// vertices, so that is the height a player stands on, and evaluating the blend at the
        /// point instead is a few centimetres out wherever the ground curves inside a 1 m cell.
        /// The self-test measures those centimetres - it levels a patch, restores it, and
        /// expects the ground back to within a centimetre.</para>
        /// </summary>
        internal static float Blended(float x, float z)
        {
            WorldGenerator gen = WorldGenerator.instance;
            if (gen == null) return 0f;
            if (!_geometry && !Geometry()) return gen.GetHeight(x, z);

            Corners c = Of(ZoneSystem.GetZone(new Vector3(x, 0f, z)));

            // Interpolated between the four vertices around the point rather than evaluated at
            // the point, because the terrain is a mesh of triangles between those vertices -
            // the ideal surface is not what anybody stands on, and the two differ by a few
            // centimetres wherever the ground curves inside a 1 m cell. Doing it this way also
            // means this and the levelled path below agree to the millimetre, which is what the
            // self-test's restore check measures.
            float fx = (x - c.Corner.x) / _scale;
            float fz = (z - c.Corner.z) / _scale;

            int ix = Mathf.Clamp(Mathf.FloorToInt(fx), 0, _pitch - 2);
            int iz = Mathf.Clamp(Mathf.FloorToInt(fz), 0, _pitch - 2);
            float tx = Mathf.Clamp01(fx - ix);
            float tz = Mathf.Clamp01(fz - iz);

            float x0 = c.Corner.x + ix * _scale, x1 = x0 + _scale;
            float z0 = c.Corner.z + iz * _scale, z1 = z0 + _scale;

            return Mathf.Lerp(
                Mathf.Lerp(Blend(c, x0, z0), Blend(c, x1, z0), tx),
                Mathf.Lerp(Blend(c, x0, z1), Blend(c, x1, z1), tx), tz);
        }

        /// <summary>The corner biomes of a zone, worked out once.</summary>
        private static Corners Of(Vector2s zone)
        {
            int key = Key(zone);
            if (Zones.TryGetValue(key, out Corners known)) return known;

            WorldGenerator gen = WorldGenerator.instance;
            float span = _width * _scale;
            Vector3 corner = ZoneSystem.GetZonePos(zone) +
                             new Vector3(span * -0.5f, 0f, span * -0.5f);

            var c = new Corners
            {
                Corner = corner,
                B0 = gen.GetBiome(corner.x, corner.z),
                B1 = gen.GetBiome(corner.x + span, corner.z),
                B2 = gen.GetBiome(corner.x, corner.z + span),
                B3 = gen.GetBiome(corner.x + span, corner.z + span),
            };

            c.One = c.B1 == c.B0 && c.B2 == c.B0 && c.B3 == c.B0;

            Zones[key] = c;
            return c;
        }

        /// <summary>
        /// One point's generated height, exactly as <c>HeightmapBuilder.Build</c> computes a
        /// vertex: one biome height when the zone is all one biome, and a smoothstep blend of
        /// its four corner biomes when it is not.
        /// </summary>
        private static float Blend(Corners c, float wx, float wz)
        {
            WorldGenerator gen = WorldGenerator.instance;
            Color mask;

            if (c.One) return gen.GetBiomeHeight(c.B0, wx, wz, out mask);

            float span = _width * _scale;
            float tx = Mathf.SmoothStep(0f, 1f, (wx - c.Corner.x) / span);
            float tz = Mathf.SmoothStep(0f, 1f, (wz - c.Corner.z) / span);

            float h0 = gen.GetBiomeHeight(c.B0, wx, wz, out mask);
            float h1 = gen.GetBiomeHeight(c.B1, wx, wz, out mask);
            float h2 = gen.GetBiomeHeight(c.B2, wx, wz, out mask);
            float h3 = gen.GetBiomeHeight(c.B3, wx, wz, out mask);

            return Mathf.Lerp(Mathf.Lerp(h0, h1, tx), Mathf.Lerp(h2, h3, tx), tz);
        }

        /// <summary>Whether a circle on the ground overlaps a zone's square at all.</summary>
        private static bool Reaches(Vector2s zone, Vector3 at, float radius)
        {
            Vector3 zonePos = ZoneSystem.GetZonePos(zone);
            float half = _width * _scale * 0.5f;

            float dx = at.x - Mathf.Clamp(at.x, zonePos.x - half, zonePos.x + half);
            float dz = at.z - Mathf.Clamp(at.z, zonePos.z - half, zonePos.z + half);

            return dx * dx + dz * dz <= radius * radius;
        }

        /// <summary>
        /// Levels the ground inside <paramref name="radius"/> of <paramref name="at"/> to
        /// <paramref name="targetY"/>, tapering back to the generated ground over the outer
        /// <paramref name="taper"/> metres so the result is a plateau and not a plinth.
        /// </summary>
        /// <param name="paint">
        /// What to paint the levelled ground, or null to leave the ground's own colour alone.
        /// Dirt or paved both read as "cleared" to <c>ClutterSystem</c>, which is what stops
        /// grass growing back through a floor.
        /// </param>
        /// <returns>How many vertices were moved. Zero means nothing could be written.</returns>
        internal static int Level(Vector3 at, float radius, float targetY, float taper = 6f,
                                  Color? paint = null)
        {
            if (!Geometry()) return 0;
            if (ZDOMan.instance == null || WorldGenerator.instance == null) return 0;
            if (radius <= 0f) return 0;

            int moved = 0;
            int clamped = 0;
            float flat = Mathf.Max(0f, radius - Mathf.Max(0f, taper));

            Vector2s low = ZoneSystem.GetZone(new Vector3(at.x - radius, 0f, at.z - radius));
            Vector2s high = ZoneSystem.GetZone(new Vector3(at.x + radius, 0f, at.z + radius));

            for (int zx = low.x; zx <= high.x; zx++)
            {
                for (int zy = low.y; zy <= high.y; zy++)
                {
                    var zone = new Vector2s((short)zx, (short)zy);

                    // Does the circle actually reach into this zone? The loop above walks the
                    // bounding box, and a corner zone of that box can be entirely outside the
                    // circle - creating a compiler there would leave an empty one behind, which
                    // is an object every client in range instantiates for nothing.
                    if (!Reaches(zone, at, radius)) continue;

                    Patch patch = Open(zone, create: true);
                    if (patch == null || patch.Unreadable) continue;

                    float[] generated = Generated(patch);
                    if (generated == null) continue;

                    bool touched = false;

                    for (int iy = 0; iy < _pitch; iy++)
                    {
                        float wz = patch.Corner.z + iy * _scale;

                        for (int ix = 0; ix < _pitch; ix++)
                        {
                            float wx = patch.Corner.x + ix * _scale;

                            float dx = wx - at.x, dz = wz - at.z;
                            float d = Mathf.Sqrt(dx * dx + dz * dz);
                            if (d > radius) continue;

                            int v = iy * _pitch + ix;
                            float ground = generated[v];

                            // Full strength inside the flat part, easing out to nothing at the
                            // rim. Without this a levelled arena stands on a 2 m step all the
                            // way round, which is both ugly and climbable.
                            float strength = flat <= 0f || d <= flat
                                ? 1f
                                : Mathf.SmoothStep(1f, 0f, (d - flat) / Mathf.Max(0.01f, radius - flat));

                            float want = Mathf.Lerp(ground, targetY, strength);
                            float delta = Mathf.Clamp(want - ground, -Clamp, Clamp);

                            // Counted and reported, because this is the one way a level can
                            // quietly half-work: vanilla will not move a vertex more than 8 m
                            // from the ground the seed describes, so a site on a steep enough
                            // slope cannot be flattened by this or by anybody's hoe, and the
                            // result is a floor with the top of the hill still in it.
                            if (Mathf.Abs(want - ground) > Clamp + 0.001f) clamped++;

                            // Already exactly where we want it. Both halves are checked, so a
                            // second pass that only wants to paint ground this one has already
                            // levelled is not skipped as a no-op.
                            bool height = patch.Modified[v] && patch.Smooth[v] == 0f &&
                                          Mathf.Abs(patch.Level[v] - delta) < 0.001f;
                            bool painted = !paint.HasValue || Same(patch, v, paint.Value);
                            if (height && painted) continue;

                            patch.Modified[v] = true;
                            patch.Level[v] = delta;

                            // Cleared rather than kept: a smooth delta left over from somebody's
                            // hoe would ride on top of the height we just chose.
                            patch.Smooth[v] = 0f;

                            if (paint.HasValue)
                            {
                                patch.Painted[v] = true;
                                patch.Mask[v] = paint.Value;
                            }

                            touched = true;
                            moved++;
                        }
                    }

                    if (!touched) continue;

                    patch.Operations++;
                    patch.LastPoint = at;
                    patch.LastRadius = radius;
                    Write(patch);
                }
            }

            if (moved > 0)
                VersePlugin.Log.LogInfo(
                    $"terrain: levelled {moved} vertex/vertices to {targetY:0.00} m within " +
                    $"{radius:0.#} m of {at.x:0}, {at.z:0}" +
                    (paint.HasValue ? ", and painted the ground" : ""));

            if (clamped > 0)
                VersePlugin.Log.LogWarning(
                    $"terrain: {clamped} vertex/vertices near {at.x:0}, {at.z:0} needed more " +
                    $"than the {Clamp:0} m vanilla allows and were left part-way - this ground " +
                    "is too steep to flatten, by this or by a hoe. Pick a flatter site, or " +
                    "expect the hill to show through.");

            return moved;
        }

        /// <summary>
        /// The ground's height at a point: the levelled height where this has levelled it, and
        /// the generated height everywhere else.
        ///
        /// <para>Every placement in the arena goes through here rather than through
        /// <c>WorldGenerator</c> directly, because after a level the generator is describing
        /// ground that no client will ever draw - and a wall sunk to the height of the hill that
        /// used to be there is a wall nobody can see.</para>
        /// </summary>
        internal static float HeightAt(float x, float z)
        {
            if (WorldGenerator.instance == null) return 0f;
            if (!_geometry && !Geometry()) return WorldGenerator.instance.GetHeight(x, z);

            // The height the client's own heightmap will have here, biome blending included;
            // see Blended. Used as the answer on ground nothing has levelled, so the two cases
            // agree with each other and with the mesh.
            float generated = Blended(x, z);

            Patch patch = Open(ZoneSystem.GetZone(new Vector3(x, 0f, z)), create: false);
            if (patch == null || patch.Unreadable) return generated;

            float[] heights = Generated(patch);
            if (heights == null) return generated;

            // The four vertices around the point, interpolated the way the mesh between them is.
            float fx = (x - patch.Corner.x) / _scale;
            float fz = (z - patch.Corner.z) / _scale;

            int ix = Mathf.Clamp(Mathf.FloorToInt(fx), 0, _pitch - 2);
            int iz = Mathf.Clamp(Mathf.FloorToInt(fz), 0, _pitch - 2);
            float tx = Mathf.Clamp01(fx - ix);
            float tz = Mathf.Clamp01(fz - iz);

            float h00 = Final(patch, heights, iz * _pitch + ix);
            float h10 = Final(patch, heights, iz * _pitch + ix + 1);
            float h01 = Final(patch, heights, (iz + 1) * _pitch + ix);
            float h11 = Final(patch, heights, (iz + 1) * _pitch + ix + 1);

            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
        }

        /// <summary>Whether a vertex is already painted this colour.</summary>
        private static bool Same(Patch patch, int v, Color want)
        {
            if (!patch.Painted[v]) return false;

            Color had = patch.Mask[v];
            return Mathf.Abs(had.r - want.r) < 0.001f && Mathf.Abs(had.g - want.g) < 0.001f &&
                   Mathf.Abs(had.b - want.b) < 0.001f && Mathf.Abs(had.a - want.a) < 0.001f;
        }

        /// <summary>One vertex's height as a client will draw it, clamp included.</summary>
        private static float Final(Patch patch, float[] generated, int v)
        {
            float ground = generated[v];
            if (!patch.Modified[v]) return ground;

            return Mathf.Clamp(ground + patch.Level[v] + patch.Smooth[v],
                               ground - Clamp, ground + Clamp);
        }

        /// <summary>
        /// The zone's compiler and its decoded data, reloaded if somebody else has written to it
        /// since we last looked. Created only when <paramref name="create"/> says so, because
        /// reading the height of untouched ground must not litter the world with compilers.
        /// </summary>
        private static Patch Open(Vector2s zone, bool create)
        {
            int key = Key(zone);

            if (Patches.TryGetValue(key, out Patch known))
            {
                // A null entry is "this zone has no compiler", remembered so that reading the
                // height of untouched ground does not scan the object table every time.
                if (known == null)
                {
                    if (!create) return null;
                    Patches.Remove(key);
                }
                else if (known.Zdo == null || !known.Zdo.IsValid())
                {
                    Patches.Remove(key);                     // destroyed under us; look again
                }
                else
                {
                    Reload(known);
                    return known;
                }
            }

            Vector3 zonePos = ZoneSystem.GetZonePos(zone);
            ZDO found = Find(zonePos);

            if (found == null && !create)
            {
                // Remembered as absent, so the next height query does not scan the table again.
                Patches[key] = null;
                return null;
            }

            if (found == null)
            {
                Authorship.Suspend();
                try
                {
                    found = ZDOMan.instance.CreateNewZDO(zonePos, _compilerHash);

                    // CreateNewZDO's prefab argument does not set the ZDO's prefab field; see
                    // HANDOFF.md and BossStones. Without this the compiler is data no client can
                    // instantiate, and the terrain edit never appears.
                    found.SetPrefab(_compilerHash);
                    found.Persistent = true;
                }
                finally
                {
                    Authorship.Resume();
                }

                VersePlugin.Log.LogInfo(
                    $"terrain: laid a new {_compilerName} on zone {zone.x}, {zone.y} " +
                    $"at {zonePos.x:0}, {zonePos.z:0}");
            }

            var patch = new Patch
            {
                Zone = zone,
                Zdo = found,
                Corner = zonePos + new Vector3(_width * _scale * -0.5f, 0f, _width * _scale * -0.5f),
                Modified = new bool[_vertices],
                Level = new float[_vertices],
                Smooth = new float[_vertices],
                Painted = new bool[_vertices],
                Mask = new Color[_vertices],
            };

            Patches[key] = patch;
            Reload(patch);
            return patch;
        }

        /// <summary>The compiler already sitting on a zone, if there is one.</summary>
        private static ZDO Find(Vector3 zonePos)
        {
            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return null;

            foreach (ZDO zdo in all.Values)
            {
                if (zdo.GetPrefab() != _compilerHash) continue;

                Vector3 p = zdo.GetPosition();
                if (Mathf.Abs(p.x - zonePos.x) > 0.5f || Mathf.Abs(p.z - zonePos.z) > 0.5f) continue;

                return zdo;
            }

            return null;
        }

        /// <summary>
        /// Decodes <c>TCData</c> into the patch, if it has changed since the last read. Mirrors
        /// <c>TerrainComp.Load</c>, including its distrust: an array of the wrong length is a
        /// payload from a different grid, and the honest thing to do with it is nothing.
        /// </summary>
        private static void Reload(Patch patch)
        {
            if (patch.Zdo.DataRevision == patch.Revision) return;
            patch.Revision = patch.Zdo.DataRevision;

            byte[] raw = patch.Zdo.GetByteArray(ZDOVars.s_TCData);
            if (raw == null)
            {
                // A fresh compiler with nothing edited on it yet. Cleared rather than left as
                // it was, so a patch that is re-read after its data went away does not keep
                // answering with the deltas it used to hold.
                System.Array.Clear(patch.Modified, 0, patch.Modified.Length);
                System.Array.Clear(patch.Level, 0, patch.Level.Length);
                System.Array.Clear(patch.Smooth, 0, patch.Smooth.Length);
                System.Array.Clear(patch.Painted, 0, patch.Painted.Length);
                return;
            }

            try
            {
                var pkg = new ZPackage(Utils.Decompress(raw));
                pkg.ReadInt();                               // version; only 1 has ever existed
                patch.Operations = pkg.ReadInt();
                patch.LastPoint = pkg.ReadVector3();
                patch.LastRadius = pkg.ReadSingle();

                int heights = pkg.ReadInt();
                if (heights != _vertices)
                {
                    patch.Unreadable = true;
                    VersePlugin.Log.LogWarning(
                        $"terrain: a compiler at {patch.Zdo.GetPosition().x:0}, " +
                        $"{patch.Zdo.GetPosition().z:0} holds {heights} heights and this grid " +
                        $"has {_vertices} - leaving it alone");
                    return;
                }

                for (int i = 0; i < heights; i++)
                {
                    patch.Modified[i] = pkg.ReadBool();
                    patch.Level[i] = patch.Modified[i] ? pkg.ReadSingle() : 0f;
                    patch.Smooth[i] = patch.Modified[i] ? pkg.ReadSingle() : 0f;
                }

                int painted = pkg.ReadInt();
                if (painted != _vertices)
                {
                    // The legacy shape TerrainComp.Load remaps on the fly. Rewriting it would
                    // mean reproducing that remap, and getting it wrong would repaint somebody's
                    // ground, so this hands the zone back untouched.
                    patch.Unreadable = true;
                    VersePlugin.Log.LogWarning(
                        $"terrain: a compiler holds {painted} paint entries and this grid has " +
                        $"{_vertices} - leaving it alone");
                    return;
                }

                for (int i = 0; i < painted; i++)
                {
                    patch.Painted[i] = pkg.ReadBool();
                    if (!patch.Painted[i]) continue;

                    patch.Mask[i] = new Color(pkg.ReadSingle(), pkg.ReadSingle(),
                                              pkg.ReadSingle(), pkg.ReadSingle());
                }
            }
            catch (System.Exception e)
            {
                patch.Unreadable = true;
                VersePlugin.Log.LogWarning("terrain: could not read a compiler's data: " + e.Message);
            }
        }

        /// <summary>
        /// Encodes the patch back into <c>TCData</c>, byte for byte as <c>TerrainComp.Save</c>
        /// writes it - gzip included, since <c>Load</c> decompresses before it reads.
        /// </summary>
        private static void Write(Patch patch)
        {
            var pkg = new ZPackage();
            pkg.Write(1);                                    // terrainCompVersion
            pkg.Write(patch.Operations);
            pkg.Write(patch.LastPoint);
            pkg.Write(patch.LastRadius);

            pkg.Write(_vertices);
            for (int i = 0; i < _vertices; i++)
            {
                pkg.Write(patch.Modified[i]);
                if (!patch.Modified[i]) continue;

                pkg.Write(patch.Level[i]);
                pkg.Write(patch.Smooth[i]);
            }

            pkg.Write(_vertices);
            for (int i = 0; i < _vertices; i++)
            {
                pkg.Write(patch.Painted[i]);
                if (!patch.Painted[i]) continue;

                pkg.Write(patch.Mask[i].r);
                pkg.Write(patch.Mask[i].g);
                pkg.Write(patch.Mask[i].b);
                pkg.Write(patch.Mask[i].a);
            }

            // Claimed first. A persistent ZDO in a player's active area is handed to that player
            // by ReleaseNearbyZDOS every couple of seconds, and a terrain write the server makes
            // while a client owns the compiler is a write the owner's own Save can overwrite
            // from its older arrays.
            if (ZDOMan.instance != null) patch.Zdo.SetOwner(ZDOMan.GetSessionID());

            patch.Zdo.Set(ZDOVars.s_TCData, Utils.Compress(pkg.GetArray()));
            patch.Revision = patch.Zdo.DataRevision;
        }

        /// <summary>
        /// The heights a client will generate for this zone's vertices, reproducing
        /// <c>HeightmapBuilder.Build</c>: the biome at the zone's four corners, and a smoothstep
        /// blend of all four biome heights whenever they are not the same biome.
        ///
        /// <para>Computed once per zone and kept, because it is a few thousand noise samples and
        /// it cannot change - it is a function of the seed.</para>
        /// </summary>
        private static float[] Generated(Patch patch)
        {
            if (patch.Generated != null) return patch.Generated;

            if (WorldGenerator.instance == null) return null;

            Corners corners = Of(patch.Zone);
            var heights = new float[_vertices];

            for (int iy = 0; iy < _pitch; iy++)
            {
                float wz = patch.Corner.z + iy * _scale;

                for (int ix = 0; ix < _pitch; ix++)
                    heights[iy * _pitch + ix] = Blend(corners, patch.Corner.x + ix * _scale, wz);
            }

            patch.Generated = heights;
            return heights;
        }

        /// <summary>
        /// Puts the ground back the way the seed describes it: clears every delta and every
        /// repaint inside the radius, and removes the compiler altogether if nothing is left
        /// edited in that zone.
        ///
        /// <para>The counterpart <see cref="Level"/> needs in order to be usable at all. An
        /// arena that is moved - <c>ArenaSitePoint</c> is a config key - would otherwise leave a
        /// paved plateau behind it in a field nobody visits, for ever, and the self-test could
        /// not write real terrain and then leave the world as it found it.</para>
        /// </summary>
        internal static int Restore(Vector3 at, float radius)
        {
            if (!Geometry() || ZDOMan.instance == null) return 0;

            int cleared = 0;

            Vector2s low = ZoneSystem.GetZone(new Vector3(at.x - radius, 0f, at.z - radius));
            Vector2s high = ZoneSystem.GetZone(new Vector3(at.x + radius, 0f, at.z + radius));

            for (int zx = low.x; zx <= high.x; zx++)
            {
                for (int zy = low.y; zy <= high.y; zy++)
                {
                    var zone = new Vector2s((short)zx, (short)zy);
                    if (!Reaches(zone, at, radius)) continue;

                    Patch patch = Open(zone, create: false);
                    if (patch == null || patch.Unreadable) continue;

                    bool touched = false;
                    bool anything = false;

                    for (int iy = 0; iy < _pitch; iy++)
                    {
                        float wz = patch.Corner.z + iy * _scale;

                        for (int ix = 0; ix < _pitch; ix++)
                        {
                            int v = iy * _pitch + ix;

                            float dx = patch.Corner.x + ix * _scale - at.x, dz = wz - at.z;
                            if (dx * dx + dz * dz > radius * radius)
                            {
                                anything |= patch.Modified[v] || patch.Painted[v];
                                continue;
                            }

                            if (!patch.Modified[v] && !patch.Painted[v]) continue;

                            patch.Modified[v] = false;
                            patch.Level[v] = 0f;
                            patch.Smooth[v] = 0f;
                            patch.Painted[v] = false;

                            touched = true;
                            cleared++;
                        }
                    }

                    if (!touched) continue;

                    if (anything)
                    {
                        patch.Operations++;
                        patch.LastPoint = at;
                        patch.LastRadius = radius;
                        Write(patch);
                        continue;
                    }

                    // Nothing edited anywhere in this zone any more, so the compiler itself is
                    // litter. Removed rather than left holding an empty array, because a
                    // compiler is an object every client in range instantiates.
                    //
                    // The cleared payload is written first, and that is not belt and braces:
                    // ZDOMan.DestroyZDO only queues the id on m_destroySendList, so the object
                    // is still in the table - and still readable - until the next ZDOMan
                    // update. Destroying without writing left the old deltas being read back
                    // from a dead compiler, which is exactly what the self-test caught.
                    patch.Operations++;
                    patch.LastPoint = at;
                    patch.LastRadius = radius;
                    Write(patch);

                    Fixture.Destroy(patch.Zdo);
                    Patches.Remove(Key(zone));

                    VersePlugin.Log.LogInfo(
                        $"terrain: zone {zone.x}, {zone.y} is back to the seed's own ground - " +
                        $"removed its {_compilerName}");
                }
            }

            if (cleared > 0)
                VersePlugin.Log.LogInfo(
                    $"terrain: restored {cleared} vertex/vertices within {radius:0.#} m of " +
                    $"{at.x:0}, {at.z:0}");

            return cleared;
        }

        /// <summary>
        /// Reads a zone's written payload back the way <c>TerrainComp.Load</c> will: decompress,
        /// then the two array lengths it checks before it trusts anything. The self-test's one
        /// way of knowing the bytes on the ZDO are bytes the game would accept, rather than
        /// bytes this file happens to be able to re-read.
        /// </summary>
        internal static bool Audit(Vector3 point, out int heights, out int paints, out int edited)
        {
            heights = paints = edited = 0;
            if (!Geometry()) return false;

            ZDO zdo = Find(ZoneSystem.GetZonePos(ZoneSystem.GetZone(point)));
            byte[] raw = zdo?.GetByteArray(ZDOVars.s_TCData);
            if (raw == null) return false;

            var pkg = new ZPackage(Utils.Decompress(raw));
            pkg.ReadInt();
            pkg.ReadInt();
            pkg.ReadVector3();
            pkg.ReadSingle();

            heights = pkg.ReadInt();
            for (int i = 0; i < heights; i++)
            {
                if (!pkg.ReadBool()) continue;

                pkg.ReadSingle();
                pkg.ReadSingle();
                edited++;
            }

            paints = pkg.ReadInt();
            for (int i = 0; i < paints; i++)
            {
                if (!pkg.ReadBool()) continue;

                pkg.ReadSingle();
                pkg.ReadSingle();
                pkg.ReadSingle();
                pkg.ReadSingle();
            }

            return true;
        }

        /// <summary>How many vertices one zone has, which is what a payload must declare.</summary>
        internal static int Vertices => Geometry() ? _vertices : 0;

        /// <summary>
        /// Forgets every decoded zone, so the next read comes off the object table again. For
        /// the self-test, which writes terrain and then has to read it back as a stranger
        /// would.
        /// </summary>
        internal static void Forget() => Patches.Clear();
    }
}
