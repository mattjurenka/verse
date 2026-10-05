using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Where the arena is, found rather than hard-coded.
    ///
    /// <para><b>Why it has to be found.</b> Every verse sits at the same coordinates on the
    /// same seed (see docs/verse-design.md), and clients generate terrain themselves from that
    /// seed, so the arena's ground is whatever <c>WorldGenerator</c> says it is and no
    /// server-side patch can flatten it. A coordinate written into the config would be a flat
    /// field on this world and a cliff face on the next one.</para>
    ///
    /// <para><b>Why it can be found.</b> <c>WorldGenerator.GetHeight</c> and
    /// <c>GetBiome</c> are pure functions of the seed - no scene, no heightmap, no client - so
    /// the server can evaluate candidate ground itself and pick the flattest patch in the
    /// wanted biome. That is the one thing about terrain a server-only plugin genuinely can
    /// do.</para>
    ///
    /// <para>The result is written back into the config, so the scan runs once per world and an
    /// operator who dislikes the answer can pin a different one by hand.</para>
    /// </summary>
    internal static class ArenaSite
    {
        /// <summary>Valheim's world is a 10 km disc; nothing generates past this.</summary>
        private const float WorldRadius = 10000f;

        /// <summary>Vanilla's sea level. Only used if <c>ZoneSystem</c> is not up yet.</summary>
        private const float DefaultWaterLevel = 30f;

        /// <summary>Clear of the water by this much, so the ring is not a paddling pool.</summary>
        private const float Freeboard = 4f;

        /// <summary>Coarse candidate spacing, in metres.</summary>
        private const float CoarseStep = 150f;

        /// <summary>How many coarse candidates survive into the fine pass.</summary>
        private const int Shortlist = 64;

        /// <summary>Hard ceiling on coarse samples, so a bad config cannot hang the server.</summary>
        private const int MaxCoarse = 60000;

        private static bool _resolved;
        private static Vector3 _centre;
        private static Heightmap.Biome _biome;

        internal static bool Ready => _resolved;
        internal static Vector3 Centre => _centre;

        /// <summary>
        /// Whether the site's biome applies the freezing effect, which decides whether the kit
        /// carries frost resistance. An iron kit in the Deep North without it is a death by
        /// weather rather than by monster.
        /// </summary>
        internal static bool Cold =>
            _biome == Heightmap.Biome.Mountain || _biome == Heightmap.Biome.DeepNorth;

        internal static float Radius => Mathf.Max(10f, VersePlugin.ArenaRadius.Value);

        /// <summary>
        /// Horizontal distance only. A drake climbing above the wall has not left the arena,
        /// and neither has a player who has been knocked into the air.
        /// </summary>
        internal static bool Inside(Vector3 point, float slack = 0f)
        {
            if (!_resolved) return false;
            float dx = point.x - _centre.x;
            float dz = point.z - _centre.z;
            return dx * dx + dz * dz <= (Radius + slack) * (Radius + slack);
        }

        /// <summary>
        /// The ground every arena placement is measured from.
        ///
        /// <para>Routed through <see cref="Ground"/> rather than straight to
        /// <c>WorldGenerator</c>, because the arena now levels its own site: past that point the
        /// generator describes a hill that no client will ever draw, and a wall or a chest placed
        /// at the generated height would be buried in the new floor or hanging over it. Off the
        /// levelled ground the two answers are the same number.</para>
        /// </summary>
        internal static float HeightAt(float x, float z) =>
            WorldGenerator.instance != null ? Ground.HeightAt(x, z) : _centre.y;

        /// <summary>
        /// The site, scanning for one if the config does not already hold it. Safe to call
        /// every time it is needed; the work happens once.
        /// </summary>
        internal static bool Resolve(out Vector3 centre)
        {
            centre = _centre;
            if (_resolved) return true;

            if (TryParse(VersePlugin.ArenaSitePoint.Value, out _centre))
            {
                _biome = BiomeAt(_centre);
                _resolved = true;
                centre = _centre;
                VersePlugin.Log.LogInfo(
                    $"arena site from config: {_centre.x:0}, {_centre.y:0}, {_centre.z:0} ({_biome})");
                return true;
            }

            if (WorldGenerator.instance == null) return false;   // world not generated yet

            if (!Scan(out _centre, out _biome)) return false;

            _resolved = true;
            centre = _centre;

            // Deliberately NOT written back here.
            //
            // It used to be, and that was a real bug found deploying to the live server: the
            // scan persisted its choice before anything had checked the site was usable, so a
            // site that Ensure then refused - because somebody was already standing on it -
            // stayed in the config for ever. The next restart loaded the refused point straight
            // back and skipped the scan, and the arena silently never built. It cost a restart
            // here and would look like "the arena just does not work" anywhere else.
            //
            // ArenaRing.Ensure calls Confirm() once a ring is actually standing.
            return true;
        }

        /// <summary>
        /// Persists the resolved site, so the scan is a one-off per world and the number is
        /// visible and overridable rather than buried in memory. Called only once the site has
        /// been accepted - see the note in <see cref="Resolve"/>.
        /// </summary>
        internal static void Confirm()
        {
            if (!_resolved) return;

            VersePlugin.ArenaSitePoint.Value =
                _centre.x.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                _centre.y.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                _centre.z.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static Heightmap.Biome BiomeAt(Vector3 p) =>
            WorldGenerator.instance != null
                ? WorldGenerator.instance.GetBiome(p.x, p.z)
                : Heightmap.Biome.None;

        private static bool TryParse(string text, out Vector3 point)
        {
            point = Vector3.zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string[] parts = text.Split(',');
            if (parts.Length != 3) return false;

            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                !float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                return false;

            point = new Vector3(x, y, z);
            return true;
        }

        private struct Candidate { internal float X, Z, Spread; }

        /// <summary>Positions of everything that belongs to a verse, so sites can avoid them.</summary>
        private static List<Vector3> Occupied()
        {
            var taken = new List<Vector3>();

            var byId = HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_objectsByID")
                ?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
            if (byId == null) return taken;

            // Buildings only, for the same reason ArenaRing.Occupants counts buildings: a
            // dropped sword or a corpse is not somebody using the ground, and counting them
            // rules out sites nobody is anywhere near.
            foreach (ZDO zdo in byId.Values)
            {
                if (!ZdoVerse.Tagged(zdo)) continue;

                GameObject prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                if (prefab?.GetComponent<Piece>() == null) continue;
                if (prefab.GetComponent<TombStone>() != null) continue;

                taken.Add(zdo.GetPosition());
            }

            return taken;
        }

        /// <summary>Whether anything anybody owns is standing inside this candidate ring.</summary>
        private static bool IsTaken(List<Vector3> taken, float x, float z, float radius)
        {
            float r2 = radius * radius;
            for (int i = 0; i < taken.Count; i++)
            {
                float dx = taken[i].x - x;
                float dz = taken[i].z - z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }

        /// <summary>
        /// Two passes, because the honest version of this is too expensive to run in one. The
        /// coarse pass asks a cheap question of a great many points - right biome, above water,
        /// roughly level across four probes - and keeps a shortlist. The fine pass asks the
        /// real question of only those: 37 probes over the whole footprint, and the lowest
        /// height spread wins.
        ///
        /// <para>It runs on the main thread, once, on a server that has nobody on it yet
        /// (<see cref="VersePlugin"/> fires it from the same one-shot that runs the migration
        /// and the sweep), and it reports how long it took. If that ever becomes a problem the
        /// answer is to pin <c>ArenaSitePoint</c>, not to make this cleverer.</para>
        /// </summary>
        private static bool Scan(out Vector3 centre, out Heightmap.Biome biome)
        {
            centre = Vector3.zero;
            biome = Heightmap.Biome.None;

            if (!Enum.TryParse(VersePlugin.ArenaBiome.Value, true, out Heightmap.Biome wanted))
            {
                VersePlugin.Log.LogError(
                    $"arena: '{VersePlugin.ArenaBiome.Value}' is not a biome name. Expected one of " +
                    string.Join(", ", Enum.GetNames(typeof(Heightmap.Biome))));
                return false;
            }

            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : DefaultWaterLevel;
            float floor = water + Freeboard;
            float radius = Radius;
            float minDistance = Mathf.Max(0f, VersePlugin.ArenaMinDistance.Value);
            float maxDistance = WorldRadius - radius - CoarseStep;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            WorldGenerator gen = WorldGenerator.instance;

            var shortlist = new List<Candidate>();
            int sampled = 0, inBiome = 0;

            // Polar rather than a square grid: it keeps the sample density even as the radius
            // grows, and the thing being searched for is a distance band from the origin.
            for (float r = minDistance; r <= maxDistance && sampled < MaxCoarse; r += CoarseStep)
            {
                int steps = Mathf.Max(16, (int)(2f * Mathf.PI * r / CoarseStep));
                for (int i = 0; i < steps && sampled < MaxCoarse; i++)
                {
                    float angle = i * 2f * Mathf.PI / steps;
                    float x = Mathf.Cos(angle) * r;
                    float z = Mathf.Sin(angle) * r;
                    sampled++;

                    if (gen.GetBiome(x, z) != wanted) continue;
                    inBiome++;

                    // Four probes at the rim plus the centre: enough to throw away a cliff,
                    // cheap enough to do tens of thousands of times.
                    float h0 = gen.GetHeight(x, z);
                    if (h0 < floor) continue;

                    float lo = h0, hi = h0;
                    bool wet = false;
                    for (int k = 0; k < 4; k++)
                    {
                        float a = k * Mathf.PI / 2f;
                        float h = gen.GetHeight(x + Mathf.Cos(a) * radius, z + Mathf.Sin(a) * radius);
                        if (h < floor) { wet = true; break; }
                        if (h < lo) lo = h;
                        if (h > hi) hi = h;
                    }
                    if (wet) continue;

                    shortlist.Add(new Candidate { X = x, Z = z, Spread = hi - lo });
                }
            }

            if (shortlist.Count == 0)
            {
                VersePlugin.Log.LogError(
                    $"arena: no dry {wanted} site found beyond {minDistance:0} m in {sampled} samples. " +
                    "Lower ArenaMinDistance, pick another ArenaBiome, or pin ArenaSitePoint by hand.");
                return false;
            }

            shortlist.Sort((a, b) => a.Spread.CompareTo(b.Spread));
            int fine = Mathf.Min(Shortlist, shortlist.Count);

            // Where people already are, gathered in one pass so each candidate can be tested
            // against it cheaply. Occupancy belongs in choosing a site, not in vetoing one
            // afterwards: the first live scan on the real world picked flat Meadows 4.8 km out
            // that three verse-tagged objects were already sitting on, and a veto leaves you
            // with no arena rather than the next-best patch of ground.
            //
            // Verse-tagged means somebody built, dropped or tamed it - Authorship only ever
            // tags what a client created - so it is the right test, and collecting the
            // positions once matters: the live world holds nearly a million ZDOs and walking
            // all of them per candidate would take minutes.
            List<Vector3> taken = Occupied();

            float bestSpread = float.MaxValue;
            var best = new Candidate { Spread = float.MaxValue };
            float bestMean = 0f;

            for (int c = 0; c < fine; c++)
            {
                Candidate cand = shortlist[c];
                float lo = float.MaxValue, hi = float.MinValue, sum = 0f;
                int n = 0;
                bool wet = false;

                // Centre plus three rings of twelve: 37 probes over the real footprint.
                //
                // These use Ground.Blended rather than GetHeight, and the coarse pass above
                // does not, deliberately. GetHeight asks the biome at the point; a client's
                // heightmap blends the four corner biomes of the whole zone, and on a zone that
                // straddles a shore the two disagree by tens of metres - 30 m at one point on
                // the test world. So the coarse pass stays cheap on the generator's own number,
                // and the sixty-four candidates that survive it are measured by the ground a
                // player will actually stand on. Choosing a site by the wrong one of the two is
                // how you build an arena in the sea and then assert that it is dry.
                for (int ring = 0; ring <= 3 && !wet; ring++)
                {
                    float rr = radius * ring / 3f;
                    int spokes = ring == 0 ? 1 : 12;
                    for (int s = 0; s < spokes; s++)
                    {
                        float a = s * 2f * Mathf.PI / spokes;
                        float h = Ground.Blended(cand.X + Mathf.Cos(a) * rr,
                                                 cand.Z + Mathf.Sin(a) * rr);
                        if (h < floor) { wet = true; break; }
                        if (h < lo) lo = h;
                        if (h > hi) hi = h;
                        sum += h;
                        n++;
                    }
                }

                if (wet || n == 0) continue;
                if (IsTaken(taken, cand.X, cand.Z, radius)) continue;

                float spread = hi - lo;
                if (spread >= bestSpread) continue;

                bestSpread = spread;
                best = cand;
                bestMean = sum / n;
            }

            if (bestSpread == float.MaxValue)
            {
                VersePlugin.Log.LogError(
                    $"arena: {fine} shortlisted {wanted} sites all failed the close look. " +
                    "Pin ArenaSitePoint by hand.");
                return false;
            }

            centre = new Vector3(best.X, bestMean, best.Z);
            biome = wanted;

            watch.Stop();
            VersePlugin.Log.LogInfo(
                $"arena site: {centre.x:0}, {centre.y:0}, {centre.z:0} in {wanted} - " +
                $"{bestSpread:0.0} m of height across a {radius:0} m ring, " +
                $"{Mathf.Sqrt(best.X * best.X + best.Z * best.Z):0} m from the centre of the world. " +
                $"Chosen from {inBiome} in-biome candidates of {sampled} sampled in {watch.ElapsedMilliseconds} ms." +
                (Cold ? " This biome freezes, so the kit carries frost resistance." : ""));

            return true;
        }

        /// <summary>
        /// Forgets the resolved site. Only for the self-test, which pins a site of its own and
        /// must not leave it behind.
        /// </summary>
        internal static void Forget()
        {
            _resolved = false;
            _centre = Vector3.zero;
            _biome = Heightmap.Biome.None;
        }

        /// <summary>For the self-test: pretend the scan found this, without touching config.</summary>
        internal static void Pretend(Vector3 centre, Heightmap.Biome biome)
        {
            _centre = centre;
            _biome = biome;
            _resolved = true;
        }
    }
}
