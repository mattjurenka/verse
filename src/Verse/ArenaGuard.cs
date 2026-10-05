using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Keeps the ring clean while a run is under way: no native wildlife wandering in, no loot
    /// on the floor, no towers, no trenches, no summoned help.
    ///
    /// <para><b>Why any of this is needed.</b> Ambient wildlife is spawned by the client that
    /// owns the zone (<c>SpawnSystem.UpdateSpawning</c> needs <c>IsOwner</c> and a local
    /// player), so a player standing in the arena generates the local biome's monsters
    /// themselves - in the Deep North that is a 3,000 hp Gammeltroll arriving during wave one.
    /// The server cannot stop a client spawning them. What it can do is notice that the object
    /// appeared inside the ring and take it straight back out again.</para>
    ///
    /// <para><b>Why it is deferred rather than done on the spot.</b> The hook is the same
    /// <c>ZDOMan.CreateNewZDO</c> postfix <see cref="Authorship"/> uses, and at that moment the
    /// ZDO is empty: <c>RPC_ZDOData</c> creates it and only then deserialises the fields into
    /// it, so both the prefab and the position are still blank. Classifying there would read
    /// prefab 0 for everything. So candidates are queued and sorted out on the next tick, by
    /// which time they are fully formed - and destroying an object part-way through the
    /// deserialisation loop that is still reading it would be asking for trouble anyway.</para>
    ///
    /// <para><b>The two things it must never take.</b> A <c>TombStone</c> holds a player's
    /// whole inventory, and a <c>Player</c> is somebody who respawned inside the ring. Getting
    /// either wrong once is the end of anybody trusting the arena, so the test is a component
    /// lookup on the real prefab rather than a guess at the name, and anything this code does
    /// not positively recognise is left alone.</para>
    /// </summary>
    internal static class ArenaGuard
    {
        /// <summary>ZDOIDs already swept once, to tell a fresh drop from a resurrected one.</summary>
        private static readonly System.Collections.Generic.HashSet<ZDOID> Seen =
            new System.Collections.Generic.HashSet<ZDOID>();

        [HarmonyPatch]
        internal static class ZDOMan_CreateNewZDO_Patch
        {
            private static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.Method(typeof(ZDOMan), "CreateNewZDO",
                    new[] { typeof(ZDOID), typeof(Vector3), typeof(int) });

            private static void Postfix(ZDO __result)
            {
                if (__result == null) return;
                if (!VersePlugin.ArenaEnabled.Value) return;

                // Ours: every arena creation is bracketed in Authorship.Suspend().
                if (Authorship.Suspended) return;

                // Only a client's work is a candidate. Anything the server itself creates -
                // world generation, another plugin - is not something a player did inside the
                // ring, and the arena has no business policing it.
                if (Authorship.Incoming == Verses.None) return;

                Arena.Consider(__result.m_uid);
            }
        }

        /// <summary>
        /// What to do with a candidate, once it has fields. Returns true to destroy it.
        /// </summary>
        internal static bool Unwanted(ZDO zdo, out string why)
        {
            why = null;
            if (zdo == null) return false;

            GameObject prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
            if (prefab == null) return false;        // unknown: leave it alone

            // --- the two that are never touched ---
            if (prefab.GetComponent<Player>() != null) return false;
            if (prefab.GetComponent<TombStone>() != null) return false;

            // The arena.s own fixtures are building pieces, and the guard removes building
            // pieces. Unreachable in practice - everything Fixture places is created with
            // Authorship suspended, so it never becomes a candidate - and kept as the cheap
            // half of a bad trade: the cost is one field read, the cost of being wrong is the
            // venue deleting itself mid-run.
            if (ArenaRing.IsRing(zdo) || ArenaDeck.IsDeck(zdo) || ArenaApron.IsApron(zdo) ||
                ArenaStand.IsStand(zdo)) return false;

            if (prefab.GetComponent<Character>() != null)
            {
                // Native wildlife, and summons and tames with it. A summoned troll is help the
                // arena is not meant to provide, and culling it is gentler than voiding the
                // run over it.
                why = "creature";
                return true;
            }

            if (prefab.GetComponent<ItemDrop>() != null)
            {
                why = "loot";
                return true;
            }

            if (prefab.GetComponent<TerrainComp>() != null)
            {
                why = "terrain";
                return true;
            }

            if (prefab.GetComponent<Piece>() != null)
            {
                why = "building";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Walks the ring looking for things that wandered in rather than being created in it.
        ///
        /// <para><b>This is the half the creation hook cannot see, and a live test found it the
        /// hard way.</b> The postfix above only ever fires where an object is *created*, so it
        /// catches a bush spawned inside the ring and a greyling's resin dropped inside it - but
        /// a deathsquito that spawned sixty metres away in the Plains and then flew in was never
        /// a candidate at all, and killed the player during wave one. Ambient wildlife is
        /// spawned by whichever client owns the zone, anywhere in it, and then moves.</para>
        ///
        /// <para>Out to <c>Arena.ClearRadius</c> for anything alive, which is wider than the wall:
        /// a wolf twenty metres outside it is still in shot and still walks in. Loose items are
        /// the exception and are only taken off the ring's own floor - the gate chests are out
        /// there in the apron, and <c>Arena.StraySlack</c> records what happened the last time a
        /// cull radius reached them, which is that it started eating gear players had set down
        /// beside their own chest.</para>
        ///
        /// <para>Called at the start and end of every wave, by <c>Arena.SweepRing</c>. It used to
        /// run once a second for the whole of a run, and it showed up in the frame time exactly
        /// as the comment here predicted it would: it is a pass over the object table, tens of
        /// thousands of entries, and players reported the arena as laggy. If the wave edges turn
        /// out to be too coarse, the cheaper version is to ask <c>ZDOMan</c> for the handful of
        /// sectors the ring covers instead of looking at everything - not to go back to 1 Hz.</para>
        /// </summary>
        internal static int Sweep()
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null) return 0;

            var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID")
                ?.GetValue(ZDOMan.instance) as System.Collections.Generic.Dictionary<ZDOID, ZDO>;
            if (byId == null) return 0;

            var doomed = new System.Collections.Generic.List<ZDO>();
            foreach (ZDO zdo in byId.Values)
            {
                if (Arena.Ours(zdo) || ArenaRing.IsRing(zdo)) continue;

                Vector3 at = zdo.GetPosition();
                if (!Arena.InClearing(at)) continue;

                GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab == null) continue;

                // Only wanderers and litter. A player standing in the ring is the whole point,
                // and their tombstone is never touched - see Unwanted.
                bool creature = prefab.GetComponent<Character>() != null;
                bool litter = prefab.GetComponent<ItemDrop>() != null;
                if (!creature && !litter) continue;

                // Litter only off the floor itself, wildlife out to the full apron. See the
                // paragraph above: the gates are in the apron and a player's dropped gear is not
                // something to be tidying up out there.
                if (litter && !ArenaSite.Inside(at)) continue;

                if (!Unwanted(zdo, out string _)) continue;
                doomed.Add(zdo);
            }

            foreach (ZDO zdo in doomed) Arena.Remove(zdo);

            // A ZDOID we have already destroyed turning up again means the client that owns it
            // is re-sending an object the server deleted - RPC_ZDOData does not recognise the id
            // any more, so it creates it fresh and we destroy it again, round and round. Counted
            // rather than assumed: the live run showed ~15 culls a second against only a handful
            // of genuine drops, which is the shape of that loop but not proof of it.
            int repeats = 0;
            foreach (ZDO zdo in doomed)
                if (!Seen.Add(zdo.m_uid)) repeats++;

            if (repeats > 0)
            {
                Metrics.ArenaResurrected += repeats;
                if (VersePlugin.ArenaTrace.Value)
                    VersePlugin.Log.LogWarning(
                        $"arena: {repeats} of {doomed.Count} object(s) swept from the ring had " +
                        "already been destroyed once - a client is re-creating them");
            }

            if (Seen.Count > 4096) Seen.Clear();
            return doomed.Count;
        }
    }
}
