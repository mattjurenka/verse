using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// A second, re-runnable pass over a migrated world, for the three things the one-shot
    /// migration could not see. All three turned up the first time two people played a
    /// migrated world side by side.
    ///
    /// <list type="number">
    /// <item><b>Levelled ground.</b> Terrain edits are <c>TerrainComp</c> objects and carry no
    /// creator, so they stayed shared - every verse got the old party's flattened building
    /// site.</item>
    /// <item><b>Creatures.</b> Anything alive in the world before the plugin existed is
    /// untagged, and a shared creature is simulated by exactly <i>one</i> client. The verse
    /// that does not own it sees it frozen in mid-air, which is what the seagulls were. New
    /// verses spawn their own wildlife, so handing the old ones near the legacy base to the
    /// legacy verse is both the fix and the right answer - "near" doing the same job it does
    /// for pieces and items: a boar at the old party's farm is theirs, a boar three biomes
    /// away belongs to whichever verse finds it first.</item>
    /// <item><b>Untagged pieces, creator or not.</b> Some built objects have no <c>s_creator</c>
    /// recorded, so the one-shot migration left them shared - these cannot be tagged by prefab
    /// alone, since village ruins and dungeon walls are <c>Piece</c>s too, so proximity to
    /// something the verse already owns does the job the creator field normally would. A piece
    /// *with* a creator but still untagged is swept the same way: migration had its one shot
    /// at those and either caught them or it did not, so by the time this runs again, one
    /// still untagged - whatever its creator field says - is an orphan live tagging missed,
    /// not something migration is still responsible for. The first version of this only took
    /// creator-less pieces, which is why a player-placed, player-owned portal could still turn
    /// up shared: it had a creator, so sweep skipped it on the assumption migration already
    /// had it.</item>
    /// <item><b>Classified as neither.</b> <c>Classify</c> sorts a prefab into terrain,
    /// creature, item or piece by component, and a prefab that is none of those - <c>Kind.Other</c> -
    /// is invisible to every sweep category no matter how close it sits to something owned.
    /// <c>BossStone</c> (the Guardian Power hook at the start temple) is exactly this: no
    /// <c>Piece</c> component, no creator at all - it is temple-generated furniture, not
    /// something a player built - so it stayed untagged forever and every verse could hang a
    /// trophy and activate a power nobody in that verse had earned. Classified as a piece now,
    /// same as the rest of this item; the general lesson is that a new <c>Kind.Other</c> prefab
    /// whose *state* players can change is a sweep gap by construction; this project does not
    /// yet have a way to notice one exists before someone finds it in play.</item>
    /// </list>
    ///
    /// <para>Unlike <see cref="Migration"/> this has no once-only marker: it only ever tags
    /// things that are still untagged, so running it twice is a no-op and running it after
    /// more play picks up whatever has appeared since. Dry run by default, for the same reason
    /// - the tag goes into the world save.</para>
    ///
    /// <para><b>The shared arrival point counts as owned too.</b> Proximity is judged against
    /// everything the target verse owns, seeded with the legacy base - but the temple is not
    /// necessarily anywhere near that base, and it is the one place every verse's players
    /// actually stand. A stray portal or a handful of boss trophies sitting right on top of
    /// spawn would never be reached by base-proximity alone, so <see cref="Spawns.TryGetTemple"/>
    /// seeds the same proximity grid with the temple itself. Found the hard way: two sweeps
    /// that correctly cleaned up around the legacy base left spawn untouched, because nothing
    /// there was within 64 m of anything the base-anchored grid called "owned."</para>
    /// </summary>
    internal static class Sweep
    {
        private enum Kind { Other, Terrain, Creature, Piece, Item }

        private static FieldInfo _byId;

        /// <summary>Prefab hash to what it is. A world has ~a thousand prefabs and a million objects.</summary>
        private static readonly Dictionary<int, Kind> Kinds = new Dictionary<int, Kind>();

        internal static void Run()
        {
            int into = VersePlugin.SweepInto.Value;
            if (into <= 0) return;

            if (Verses.Get(into) == null)
            {
                VersePlugin.Log.LogError($"sweep refused: there is no verse {into}.");
                return;
            }

            int connected = ZNet.instance.GetPeers().Count;
            if (connected > 0)
            {
                VersePlugin.Log.LogError(
                    $"sweep refused: {connected} player(s) are connected. It rewrites the world " +
                    "save and must run on an empty server.");
                return;
            }

            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (_byId == null || ZNetScene.instance == null)
            {
                VersePlugin.Log.LogError("sweep refused: the object table or scene did not resolve.");
                return;
            }
            if (!(_byId.GetValue(ZDOMan.instance) is Dictionary<ZDOID, ZDO> all))
            {
                VersePlugin.Log.LogError("sweep refused: could not read the object table.");
                return;
            }

            bool dry = VersePlugin.SweepDryRun.Value;
            float near = Mathf.Max(0f, VersePlugin.SweepPiecesNear.Value);

            var terrain = new List<ZDO>();
            var wildlife = new List<ZDO>();         // untagged creatures, before the distance test
            var orphans = new List<ZDO>();          // untagged pieces, before the distance test
            var loose = new List<ZDO>();            // dropped items, likewise
            var owned = new Grid(Mathf.Max(near, 1f));
            int total = 0;

            // The shared arrival point is not any verse's territory, but it is the one spot
            // every verse's players actually stand in, so untagged debris sitting right on top
            // of it - a stray portal, dropped trophies - is exactly as visible to every new
            // verse as debris at the legacy base would be. Proximity to the base alone would
            // never reach it if the temple happens to be nowhere near where the legacy party
            // settled, which is the common case, not the exception.
            if (Spawns.TryGetTemple(out Vector3 temple)) owned.Add(temple);

            foreach (ZDO zdo in all.Values)
            {
                total++;
                int verse = ZdoVerse.Of(zdo);

                if (verse == into)
                {
                    owned.Add(zdo.GetPosition());    // the anchor set for the proximity test
                    continue;
                }
                if (verse != Verses.None) continue;  // some other verse's; not ours to take

                switch (Classify(zdo.GetPrefab()))
                {
                    case Kind.Terrain:
                        if (VersePlugin.SweepTerrain.Value) terrain.Add(zdo);
                        break;
                    case Kind.Creature:
                        // Unconditional once genuinely was: every untagged creature in the
                        // *entire world* got handed to the legacy verse, including wildlife
                        // nowhere near it. A new verse walking through already-explored
                        // terrain found it lifeless everywhere the legacy party had ever
                        // been, not just at their base. Same proximity rule as pieces and
                        // items fixes it the same way.
                        if (VersePlugin.SweepCreatures.Value && near > 0f) wildlife.Add(zdo);
                        break;
                    case Kind.Piece:
                        // Not gated on creator==0 any more: migration already took every
                        // creator-having piece that existed at its one-shot run, so a piece
                        // that is both untagged and reached by Sweep now is an orphan either
                        // way - a creator field does not mean "migration already has this
                        // one," only "migration would have, had it existed yet."
                        if (near > 0f) orphans.Add(zdo);
                        break;
                    case Kind.Item:
                        // Dropped items carry no creator at all, so the same proximity rule
                        // decides: a pile of fine wood by the base is the old party's, a
                        // mushroom in a forest is anybody's.
                        if (near > 0f) loose.Add(zdo);
                        break;
                }
            }

            var creatures = new List<ZDO>();
            foreach (ZDO zdo in wildlife)
                if (owned.Near(zdo.GetPosition(), near)) creatures.Add(zdo);

            var pieces = new List<ZDO>();
            foreach (ZDO zdo in orphans)
                if (owned.Near(zdo.GetPosition(), near)) pieces.Add(zdo);

            var items = new List<ZDO>();
            foreach (ZDO zdo in loose)
                if (owned.Near(zdo.GetPosition(), near)) items.Add(zdo);

            Report(into, dry, total, terrain.Count, creatures.Count, wildlife.Count, pieces.Count,
                   orphans.Count, items.Count, loose.Count, near);
            if (dry) return;

            foreach (ZDO zdo in terrain) ZdoVerse.Set(zdo, into);
            foreach (ZDO zdo in creatures) ZdoVerse.Set(zdo, into);
            foreach (ZDO zdo in pieces) ZdoVerse.Set(zdo, into);
            foreach (ZDO zdo in items) ZdoVerse.Set(zdo, into);

            int tagged = terrain.Count + creatures.Count + pieces.Count + items.Count;
            Metrics.Tagged += tagged;

            // On disk now, for the same reason the migration does it: the tags only exist in
            // memory until a save, and half a sweep is worse than none.
            if (tagged > 0 && ZNet.instance != null) ZNet.instance.Save(sync: false);

            VersePlugin.Log.LogInfo(
                $"sweep: verse {into} took {tagged} more object(s) - {terrain.Count} terrain, " +
                $"{creatures.Count} creature(s), {pieces.Count} piece(s), {items.Count} " +
                $"dropped item(s). Set Verse.SweepInto " +
                "to 0 when you are done; running it again is harmless but pointless.");
        }

        /// <summary>
        /// What a prefab is, by the components it carries rather than by its name. Cached:
        /// this is asked once per object in a world with a million of them.
        /// </summary>
        private static Kind Classify(int prefab)
        {
            if (prefab == 0) return Kind.Other;
            if (Kinds.TryGetValue(prefab, out Kind known)) return known;

            Kind kind = Kind.Other;
            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            if (go != null)
            {
                if (go.GetComponent<TerrainComp>() != null) kind = Kind.Terrain;
                else if (go.GetComponent<Character>() != null) kind = Kind.Creature;
                else if (go.GetComponent<ItemDrop>() != null) kind = Kind.Item;
                else if (go.GetComponent<Piece>() != null) kind = Kind.Piece;
                // BossStone (the Guardian Power hook/trophy stand at the start temple) is
                // neither: it carries no Piece component, so it fell into Kind.Other and was
                // invisible to every sweep category regardless of proximity or creator. It is
                // also the one Kind.Other object whose *state* a player can change (hang a
                // trophy, activate a power) while carrying no creator at all - temple-generated
                // furniture, not something anyone built - so once untagged it stayed untagged
                // and visible to every verse forever. Treated as a piece for sweep purposes:
                // same proximity rule, same outcome.
                else if (go.GetComponent<BossStone>() != null) kind = Kind.Piece;
            }

            Kinds[prefab] = kind;
            return kind;
        }

        private static void Report(int into, bool dry, int total, int terrain, int creatures,
                                   int wildlife, int pieces, int orphans, int items, int loose,
                                   float near)
        {
            var sb = new StringBuilder();
            sb.AppendLine(dry
                ? "--- Verse: sweep DRY RUN (nothing will be written) ---"
                : $"--- Verse: sweeping more of this world into verse {into} ---");
            sb.AppendLine($"  objects in world : {total}");
            sb.AppendLine($"  levelled terrain : {terrain}   " +
                          (VersePlugin.SweepTerrain.Value ? "(TerrainComp, no creator field)" : "(disabled)"));
            sb.AppendLine(!VersePlugin.SweepCreatures.Value
                ? "  creatures        : disabled"
                : near > 0f
                    ? $"  creatures        : {creatures} of {wildlife} taken   (those within {near:0} m " +
                      "of something this verse already owns; the rest are wildlife elsewhere on the map)"
                    : "  creatures        : skipped (SweepPiecesNear is 0)");
            sb.AppendLine(near > 0f
                ? $"  untagged pieces  : {pieces} of {orphans} taken   (those within {near:0} m of " +
                  "something this verse already owns, creator field or not; the rest are ruins " +
                  "and dungeon walls)"
                : "  untagged pieces  : skipped (SweepPiecesNear is 0)");

            sb.AppendLine(near > 0f
                ? $"  dropped items      : {items} of {loose} taken   (fine wood by the base is " +
                  "the old party's; a mushroom in a forest is anybody's)"
                : "  dropped items      : skipped (SweepPiecesNear is 0)");

            if (dry)
                sb.AppendLine("  Set Verse.SweepDryRun = false to apply. Back the world up first.");

            VersePlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// A coarse spatial hash, so "is this piece near anything the verse owns?" does not
        /// become a million-by-few-thousand distance test.
        /// </summary>
        private class Grid
        {
            private readonly float _cell;
            private readonly Dictionary<long, List<Vector3>> _cells = new Dictionary<long, List<Vector3>>();

            internal Grid(float cell) { _cell = cell; }

            private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

            internal void Add(Vector3 at)
            {
                long key = Key(Mathf.FloorToInt(at.x / _cell), Mathf.FloorToInt(at.z / _cell));
                if (!_cells.TryGetValue(key, out List<Vector3> bucket))
                {
                    bucket = new List<Vector3>();
                    _cells[key] = bucket;
                }
                bucket.Add(at);
            }

            internal bool Near(Vector3 at, float radius)
            {
                int cx = Mathf.FloorToInt(at.x / _cell);
                int cz = Mathf.FloorToInt(at.z / _cell);
                float squared = radius * radius;

                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!_cells.TryGetValue(Key(cx + dx, cz + dz), out List<Vector3> bucket)) continue;
                    foreach (Vector3 other in bucket)
                    {
                        float ex = other.x - at.x, ez = other.z - at.z;
                        if (ex * ex + ez * ez <= squared) return true;
                    }
                }
                return false;
            }
        }
    }
}
