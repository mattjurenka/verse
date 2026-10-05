using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The venue's dressing: banners down the inside of the wall, and fires to see by.
    ///
    /// <para><b>Lighting is the part with a mechanism behind it.</b> A torch or a brazier is a
    /// <c>Fireplace</c>, and a <c>Fireplace</c> burns fuel: <c>ZDOVars.s_fuel</c> on its own
    /// ZDO, ticked down against <c>s_lastTime</c>, which means an arena lit at build time is an
    /// arena in the dark a few hours later. It is also the same shape of problem as the gate
    /// chests - the fuel is a field on a ZDO the server owns - so <see cref="Fuel"/> simply
    /// fills them up again, at boot and at the start of every run. The last-burned timestamp
    /// goes with it, because a fire that has been full since this morning burns its whole load
    /// catching up the moment somebody loads the zone.</para>
    ///
    /// <para><b>Which way a wall-mounted piece faces is measured, not guessed.</b> A banner or a
    /// sconce hangs on one side of its own mounting point, so its box is lop-sided about its
    /// origin - and that lop-sidedness says which way is the front. The arena has already been
    /// caught twice by guessing a prefab's facing (a ladder that could not be climbed and a ramp
    /// tilted the wrong way), so this reads the sign off <see cref="Footing"/> instead.</para>
    ///
    /// <para>Shared, untagged, marked <c>verse.arena.trim</c>, and unbreakable like the rest of
    /// the venue - see <see cref="Fixture"/>. Which also means a troll cannot put the lights
    /// out.</para>
    /// </summary>
    internal static class ArenaTrim
    {
        /// <summary>Marks a piece as part of the venue's dressing.</summary>
        private static readonly int TrimPiece = "verse.arena.trim".GetStableHashCode();

        /// <summary>Metres of arc between banners, and between lights.</summary>
        private const float BannerEvery = 8f;
        private const float SconceEvery = 6f;

        /// <summary>How high up the inside of the wall the banners and sconces hang.</summary>
        private const float BannerHeight = 4f;
        private const float SconceHeight = 3f;

        /// <summary>How many braziers stand on the walkway, spaced evenly.</summary>
        private const int Braziers = 8;

        /// <summary>How far the built dressing may drift before it is replaced.</summary>
        private const float Slack = 1.5f;

        private static System.Reflection.FieldInfo _byId;
        private static bool _dressed;

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        internal static bool IsTrim(ZDO zdo) => zdo != null && zdo.GetInt(TrimPiece, 0) == 1;

        /// <summary>The dressing's own pieces, the doomed ones left out.</summary>
        internal static List<ZDO> Standing()
        {
            var standing = new List<ZDO>();

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return standing;

            foreach (ZDO zdo in all.Values)
                if (IsTrim(zdo) && !Fixture.Doomed(zdo)) standing.Add(zdo);

            return standing;
        }

        /// <summary>
        /// Hangs the banners, mounts the sconces and stands the braziers, if they are not
        /// already up. Idempotent on the same test the rest of the venue uses: how many pieces
        /// there are and how high they sit.
        /// </summary>
        internal static int Ensure()
        {
            if (_dressed) return 0;
            if (!VersePlugin.ArenaTrim.Value) return 0;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return 0;
            if (!ArenaSite.Resolve(out Vector3 centre)) return 0;

            float radius = ArenaSite.Radius;
            float inner = radius - ArenaRing.WallThickness * 0.5f;
            float floor = ArenaSite.HeightAt(centre.x, centre.z);
            float walkway = ArenaRing.WallTopY(centre + Vector3.right * radius) + 0.02f;

            string bannerName = VersePlugin.ArenaBannerPrefab.Value?.Trim() ?? "";
            string sconceName = VersePlugin.ArenaSconcePrefab.Value?.Trim() ?? "";
            string brazierName = VersePlugin.ArenaBrazierPrefab.Value?.Trim() ?? "";

            int banners = Mathf.Max(0, Mathf.RoundToInt(2f * Mathf.PI * inner / BannerEvery));
            int sconces = Mathf.Max(0, Mathf.RoundToInt(2f * Mathf.PI * inner / SconceEvery));

            // What should be there, so the "already dressed" test has something to compare to.
            int want = 0;
            if (Known(bannerName, out int bannerHash, out Bounds banner)) want += banners;
            if (Known(sconceName, out int sconceHash, out Bounds sconce)) want += sconces;
            if (Known(brazierName, out int brazierHash, out Bounds brazier)) want += Braziers;

            if (want == 0)
            {
                VersePlugin.Log.LogWarning(
                    "arena: none of the dressing prefabs are on this server - the venue stays " +
                    "bare, which costs nothing but the look of it");
                return 0;
            }

            List<ZDO> standing = Standing();
            if (standing.Count > 0)
            {
                float highest = float.MinValue;
                foreach (ZDO zdo in standing) highest = Mathf.Max(highest, zdo.GetPosition().y);

                if (standing.Count == want && Mathf.Abs(highest - walkway) < Slack + 2f)
                {
                    _dressed = true;
                    VersePlugin.Log.LogInfo(
                        $"arena: the venue is already dressed ({standing.Count} piece(s))");
                    return 0;
                }

                foreach (ZDO zdo in standing)
                {
                    HideMask.Forget(zdo);
                    Fixture.Destroy(zdo);
                }

                VersePlugin.Log.LogInfo(
                    $"arena: took down {standing.Count} piece(s) of dressing - putting up {want}");
            }

            int made = 0;

            // Banners, flat against the inside of the wall and facing the floor.
            if (banners > 0 && bannerHash != 0)
                made += Hang(centre, bannerHash, banner, inner, banners, floor + BannerHeight);

            // Sconces, the same way and a little lower, because they are what the floor is lit
            // by and light falls downwards.
            if (sconces > 0 && sconceHash != 0)
                made += Hang(centre, sconceHash, sconce, inner, sconces, floor + SconceHeight);

            // Braziers on the walkway, outside the railing's line, so the venue reads as lit
            // from the gallery and the fighting floor keeps nothing standing in it.
            if (brazierHash != 0)
            {
                for (int i = 0; i < Braziers; i++)
                {
                    float bearing = i * 2f * Mathf.PI / Braziers + Mathf.PI / Braziers;
                    var outward = new Vector3(Mathf.Cos(bearing), 0f, Mathf.Sin(bearing));

                    var at = new Vector3(centre.x + outward.x * (radius + 0.6f),
                                         walkway - brazier.min.y,
                                         centre.z + outward.z * (radius + 0.6f));

                    ZDO lit = Fixture.Place(brazierHash, at, Quaternion.LookRotation(-outward),
                                            TrimPiece);
                    Light(lit, brazierHash);
                    made++;
                }
            }

            _dressed = made > 0;

            VersePlugin.Log.LogInfo(
                $"arena: dressed the venue - {banners} banner(s) and {sconces} sconce(s) down " +
                $"the inside of the wall, {Braziers} brazier(s) on the walkway");

            return made;
        }

        /// <summary>
        /// Puts a row of wall-mounted pieces evenly around the inside of the wall, each one
        /// facing the floor.
        ///
        /// <para>The facing comes from the prefab's own box: a piece that hangs on a wall is
        /// lop-sided about its origin, and the heavy side is its front. So a box whose middle
        /// sits forward of the origin is mounted with its forward axis pointing into the arena,
        /// and one that sits behind it the other way round.</para>
        /// </summary>
        private static int Hang(Vector3 centre, int hash, Bounds box, float inner, int count,
                                float height)
        {
            int made = 0;

            for (int i = 0; i < count; i++)
            {
                float bearing = i * 2f * Mathf.PI / count;
                var outward = new Vector3(Mathf.Cos(bearing), 0f, Mathf.Sin(bearing));
                Vector3 facing = box.center.z >= 0f ? -outward : outward;

                var at = new Vector3(centre.x + outward.x * (inner - 0.1f),
                                     height,
                                     centre.z + outward.z * (inner - 0.1f));

                ZDO piece = Fixture.Place(hash, at, Quaternion.LookRotation(facing), TrimPiece);
                Light(piece, hash);
                made++;
            }

            return made;
        }

        /// <summary>
        /// Fills a fire up, if the piece is one. Harmless on a banner.
        ///
        /// <para>The timestamp matters as much as the fuel: <c>Fireplace.UpdateFireplace</c>
        /// burns whatever time has passed since <c>s_lastTime</c> the moment somebody loads the
        /// zone, so a fire filled at boot and first seen an hour later arrives empty.</para>
        /// </summary>
        private static void Light(ZDO zdo, int hash)
        {
            if (zdo == null) return;

            GameObject prefab = ZNetScene.instance?.GetPrefab(hash);
            Fireplace fire = prefab?.GetComponent<Fireplace>();
            if (fire == null || fire.m_infiniteFuel) return;

            zdo.Set(ZDOVars.s_fuel, fire.m_maxFuel);
            zdo.Set(ZDOVars.s_lastTime, System.DateTime.Now.Ticks);
        }

        /// <summary>
        /// Tops up every fire in the venue. Called where <see cref="ArenaRing.HardenAll"/> is -
        /// at boot and at the start of every run - because the two are the same kind of promise:
        /// the venue is as it was built, whatever has happened to it since.
        /// </summary>
        internal static int Fuel()
        {
            if (!VersePlugin.ArenaTrim.Value) return 0;

            int filled = 0;
            foreach (ZDO zdo in Standing())
            {
                GameObject prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                Fireplace fire = prefab?.GetComponent<Fireplace>();
                if (fire == null || fire.m_infiniteFuel) continue;

                if (zdo.GetFloat(ZDOVars.s_fuel, 0f) >= fire.m_maxFuel - 0.01f) continue;

                Light(zdo, zdo.GetPrefab());
                filled++;
            }

            return filled;
        }

        /// <summary>
        /// A prefab this server has and this plugin can measure, or nothing. Reports what it
        /// found, because a fire's own burn rate decides whether topping it up twice a run is
        /// often enough.
        /// </summary>
        private static bool Known(string name, out int hash, out Bounds box)
        {
            hash = 0;
            box = default(Bounds);
            if (name.Length == 0) return false;

            int candidate = name.GetStableHashCode();
            GameObject prefab = ZNetScene.instance.GetPrefab(candidate);
            if (prefab == null)
            {
                VersePlugin.Log.LogWarning($"arena: no '{name}' prefab on this server");
                return false;
            }

            if (!Footing.Box(candidate, out box))
            {
                VersePlugin.Log.LogWarning($"arena: '{name}' has no geometry to measure");
                return false;
            }

            Fireplace fire = prefab.GetComponent<Fireplace>();
            if (fire != null && !fire.m_infiniteFuel)
            {
                float burns = fire.m_maxFuel * fire.m_secPerFuel;
                if (burns < 300f)
                    VersePlugin.Log.LogWarning(
                        $"arena: '{name}' burns a full load in {burns:0} s, which is less than " +
                        "the gap between top-ups - expect it to go out mid-run");
            }

            hash = candidate;
            return true;
        }

        /// <summary>Forces the next <see cref="Ensure"/> to look at the world again.</summary>
        internal static void Forget() => _dressed = false;
    }
}
