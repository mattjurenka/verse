using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// A wooden deck under each row of gate chests.
    ///
    /// <para><b>Why the chests needed a floor at all.</b> They were placed at local terrain
    /// height plus a little clearance, which is fine on a billiard table and not fine on real
    /// ground: on a slope, or over the lip of a snowdrift, part of the chest's footprint has
    /// nothing under it. A container is a building piece with <c>WearNTear</c>, and a building
    /// piece with no support takes damage until it collapses - so chests at the gate were
    /// breaking some of the time and taking whatever a player had deposited with them, which is
    /// the worst failure this plugin has available to it. A floor piece is itself supported by
    /// the ground it is laid on and supports what stands on it, so one tile under each chest
    /// turns an intermittent terrain question into a flat answer.</para>
    ///
    /// <para><b>Level, not following the ground.</b> The deck's top is taken from the
    /// <i>highest</i> ground under the chests - along the row, half a chest to either side of
    /// it - so nothing a chest stands on pokes through the floor and every chest in a row sits
    /// at one height. Wood spans a few metres unsupported, and the tile on the high side is in
    /// the ground, so the low side hangs off its neighbours rather than off nothing - the
    /// opposite of the mistake <see cref="ArenaRing"/> made with stone, where a uniform base
    /// needed four rows and an 8 m stack of stone will not hold itself up.</para>
    ///
    /// <para><b>Laid by its surface, not by its origin.</b> Where a tile is positioned and
    /// where its top ends up are two different heights, and conflating them is what put the
    /// deck in the air with the chests hovering above it. <see cref="Footing"/> measures the
    /// difference off the prefab, so <see cref="Ensure"/> can position the tile and report its
    /// surface separately and <see cref="Loadout"/> can stand a chest's base exactly on
    /// it.</para>
    ///
    /// <para><b>Shared, like the ring, and marked as a deck.</b> Untagged so one copy serves
    /// every verse, and stamped with its own key so the two things that sweep this ground -
    /// <see cref="ArenaRing.Clear"/>, which now clears scenery out as far as the gates, and
    /// <see cref="ArenaGuard"/> - can tell the arena's own floor from a plank somebody dropped.
    /// Deliberately <i>not</i> stamped with the arena's creature marker: <see cref="Arena.CleanupOrphans"/>
    /// deletes everything carrying that on boot.</para>
    /// </summary>
    internal static class ArenaDeck
    {
        /// <summary>Marks a piece as part of a gate deck, so nothing else removes it.</summary>
        private static readonly int DeckPiece = "verse.arena.deck".GetStableHashCode();

        /// <summary>One floor piece is 2 m square.</summary>
        private const float Tile = 2f;

        /// <summary>How far past the last chest the deck reaches, so nothing stands on an edge.</summary>
        private const float Margin = 1.2f;

        /// <summary>How far the deck's top sits above the highest ground under the chests.</summary>
        private const float Lift = 0.02f;

        /// <summary>
        /// Half a chest's footprint across the row - how far to either side of the row line the
        /// ground has to be clear of the deck's surface.
        /// </summary>
        private const float Under = 0.5f;

        /// <summary>How finely the ground along the row is sampled, in metres.</summary>
        private const float Step = 0.5f;

        /// <summary>How close an existing tile must be to count as the one we were going to lay.</summary>
        private const float Same = 0.6f;

        private static System.Reflection.FieldInfo _byId;

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        /// <summary>Whether a ZDO is one of the arena's floor pieces.</summary>
        internal static bool IsDeck(ZDO zdo) => zdo != null && zdo.GetInt(DeckPiece, 0) == 1;

        /// <summary>
        /// The deck's own tiles within <paramref name="radius"/> metres of a point.
        ///
        /// <para>Found by position rather than remembered, for the same reason
        /// <see cref="Loadout.Chests"/> scans: the deck is persistent and outlives the
        /// process.</para>
        /// </summary>
        internal static List<ZDO> Tiles(Vector3 at, float radius)
        {
            var found = new List<ZDO>();

            Dictionary<ZDOID, ZDO> all = All();
            if (all == null) return found;

            foreach (ZDO zdo in all.Values)
            {
                if (!IsDeck(zdo)) continue;

                Vector3 p = zdo.GetPosition();
                float dx = p.x - at.x, dz = p.z - at.z;
                if (dx * dx + dz * dz > radius * radius) continue;

                found.Add(zdo);
            }

            return found;
        }

        private static string Prefab =>
            string.IsNullOrWhiteSpace(VersePlugin.ArenaDeckPrefab.Value)
                ? "wood_floor"
                : VersePlugin.ArenaDeckPrefab.Value.Trim();

        /// <summary>
        /// Lays a deck under a row of chests that runs <paramref name="span"/> metres from
        /// <paramref name="origin"/> along <paramref name="along"/>, and reports the height its
        /// top surface ended up at. Idempotent: an existing tile within
        /// <see cref="Same"/> metres of where one was going to go is that tile.
        /// </summary>
        /// <returns>False if no deck could be laid, in which case the caller falls back to the
        /// ground - a chest that may break is still better than no arena.</returns>
        internal static bool Ensure(Vector3 origin, Vector3 along, float span, out float surface)
        {
            surface = float.NaN;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return false;

            int hash = Prefab.GetStableHashCode();
            if (ZNetScene.instance.GetPrefab(hash) == null)
            {
                VersePlugin.Log.LogWarning(
                    $"arena: no '{Prefab}' prefab on this server - the gate chests will sit on " +
                    "bare ground, which is what ArenaDeckPrefab is for");
                return false;
            }

            Vector3 d = along.sqrMagnitude > 0f ? along.normalized : Vector3.right;

            // Centred on the row rather than started at it, so the margin falls on both ends.
            int tiles = Mathf.Max(2, Mathf.CeilToInt((span + 2f * Margin) / Tile));
            float mid = span * 0.5f;

            var want = new List<Vector3>();
            for (int i = 0; i < tiles; i++)
            {
                Vector3 at = origin + d * (mid + (i - (tiles - 1) * 0.5f) * Tile);
                at.y = 0f;
                want.Add(at);
            }

            // The ground the chests themselves stand over, and only that: along the row, half a
            // chest to either side of it.
            //
            // It used to be the highest ground under the whole deck - every tile's outer edge, a
            // metre to each side of the row and a tile and a bit past each end. That is half of
            // why the chests ended up in the air: a level deck sits at the height of the highest
            // thing it is levelled against, so a probe a metre uphill lifts the whole deck by
            // the rise over that metre, and the screenshot this was fixed from is a plank
            // hanging over grass with daylight under its near edge. Measured over the chests the
            // deck meets the ground where it matters, and the padding is allowed to sit a little
            // under the turf on the uphill side - which is what a floor laid on a slope looks
            // like anyway.
            Vector3 across = new Vector3(-d.z, 0f, d.x);
            float top = float.MinValue;
            int steps = Mathf.Max(1, Mathf.CeilToInt(span / Step));
            for (int i = 0; i <= steps; i++)
            {
                Vector3 on = origin + d * (span * i / steps);
                foreach (Vector3 probe in new[] { on, on + across * Under, on - across * Under })
                {
                    float h = ArenaSite.HeightAt(probe.x, probe.z);
                    if (h > top) top = h;
                }
            }

            if (top == float.MinValue) return false;

            // The surface the chests will stand on, and - separately - where the tile has to be
            // positioned to put its surface there. A ZDO's position is the prefab's origin, not
            // its top; see Footing.
            float deckY = top + Lift;
            float pivotY = Footing.Of(hash, out float _, out float pieceTop)
                ? deckY - pieceTop
                : deckY;

            // What is already there.
            List<ZDO> standing = Tiles(origin, (tiles * Tile) + Tile);

            var facing = Quaternion.LookRotation(d);
            int made = 0;

            Authorship.Suspend();
            try
            {
                foreach (Vector3 at in want)
                {
                    ZDO found = null;
                    foreach (ZDO zdo in standing)
                    {
                        Vector3 p = zdo.GetPosition();
                        float dx = p.x - at.x, dz = p.z - at.z;
                        if (dx * dx + dz * dz <= Same * Same) { found = zdo; break; }
                    }

                    if (found != null)
                    {
                        // Re-levelled rather than left alone. A deck laid before the site's
                        // ground was fully known, or before the radius moved the gate, is worth
                        // correcting in place: the alternative is a chest standing on a step.
                        Vector3 p = found.GetPosition();
                        if (Mathf.Abs(p.y - pivotY) > 0.05f)
                            found.SetPosition(new Vector3(p.x, pivotY, p.z));
                        continue;
                    }

                    ZDO tile = ZDOMan.instance.CreateNewZDO(new Vector3(at.x, pivotY, at.z), hash);

                    // CreateNewZDO's prefab argument does not set the ZDO's prefab field; see
                    // HANDOFF.md. Without this the deck is data no client can instantiate.
                    tile.SetPrefab(hash);
                    tile.SetRotation(facing);
                    tile.Persistent = true;
                    tile.Set(DeckPiece, 1, okForNotOwner: true);

                    // Untagged on purpose: one deck for every verse, like the ring.
                    made++;
                }
            }
            finally
            {
                Authorship.Resume();
            }

            if (made > 0)
                VersePlugin.Log.LogInfo(
                    $"arena: laid {made} deck tile(s) under the chests at " +
                    $"{origin.x:0}, {origin.z:0}, surface at {deckY:0.00} m " +
                    $"(ground {top:0.00} m, laid at {pivotY:0.00} m)");

            surface = deckY;
            return true;
        }

        /// <summary>
        /// The top of the deck at this point, if a tile covers it.
        ///
        /// <para>Needed because the deck is level with the <i>highest</i> ground under it, so on
        /// any slope its surface is above where the terrain is - and a teleport to the gate that
        /// aimed at terrain height would put the player inside the floor, or under it.</para>
        ///
        /// <para>A tile's position is its prefab's origin, so its own thickness is added back
        /// here: the answer is the surface, which is what every caller wants.</para>
        /// </summary>
        internal static bool TopAt(Vector3 point, out float y)
        {
            y = float.NaN;

            float best = float.MinValue;
            foreach (ZDO zdo in Tiles(point, Tile))
            {
                float at = zdo.GetPosition().y;
                if (at > best) best = at;
            }

            if (best == float.MinValue) return false;

            y = Footing.Of(Prefab.GetStableHashCode(), out float _, out float pieceTop)
                ? best + pieceTop
                : best;
            return true;
        }

    }
}
