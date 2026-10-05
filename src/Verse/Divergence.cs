using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Copy-on-write, mutation half: a verse changing a shared world object changes it only
    /// for itself.
    ///
    /// Destruction was the easy half - an object either goes or it does not (see
    /// <see cref="Destruction"/>). Mutation is the rest of it, and is what still leaked after
    /// that: picking a berry sets <c>s_picked</c> on the shared ZDO rather than destroying it
    /// (<c>Pickable</c> reads that flag back in <c>Awake</c>), part-mining ore writes
    /// <c>s_health</c>, and looting a world chest rewrites its inventory. Each of those is one
    /// field on one shared object, so every verse saw it.
    ///
    /// The trick is which way round to do the split. The obvious way - put the shared object
    /// back the way it was and give the changing verse a copy - does not work: <c>ZDO.Deserialize</c>
    /// adds fields rather than replacing them (<c>ZDOExtraData.Add</c>), so restoring a snapshot
    /// taken before the change leaves the change in place, and there is no public way to empty
    /// a ZDO's field set.
    ///
    /// So the roles are swapped instead. The object that was just changed is <b>kept</b> by the
    /// verse that changed it - it simply becomes theirs, tag and all - and a fresh pristine copy
    /// is built from the snapshot for everybody else. That needs no field clearing, because the
    /// copy starts empty. It also means the player who acted keeps the exact object they
    /// interacted with, under the ZDOID their client already holds, so nothing blinks for them
    /// and there is no stale copy left on their machine to be re-sent.
    ///
    /// Divergence then peels off cleanly and repeatedly: the first verse to pick a bush takes
    /// that bush and leaves a new one for everyone, the second verse takes that one and leaves
    /// a third, and so on, each verse holding only what it has actually touched.
    /// </summary>
    internal static class Divergence
    {
        /// <summary>A shared object that a verse is about to change, and what it looked like first.</summary>
        private struct Pending
        {
            public ZDOID Uid;
            public uint Revision;
            public int Prefab;
            public Vector3 Position;
            /// <summary>The pristine field set, to be handed to the replacement.</summary>
            public ZPackage Before;
        }

        private static readonly List<Pending> Watching = new List<Pending>();
        private static int _verse = Verses.None;

        /// <summary>
        /// Set if the fork path ever throws. Forking then stops for the rest of the session
        /// and the leak comes back, which is the right way to fail: a shared world that is
        /// too visible beats one being quietly corrupted object by object.
        /// </summary>
        private static bool _broken;

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        internal static class ZDOMan_RPC_ZDOData_Patch
        {
            /// <summary>
            /// Reads the package to see what is coming, then winds it back for vanilla. Only
            /// reads - getting this wrong costs a missed divergence, not a broken world.
            /// </summary>
            [HarmonyPriority(Priority.Low)]
            private static void Prefix(ZRpc rpc, ZPackage pkg)
            {
                Watching.Clear();
                _verse = Verses.None;

                if (_broken || pkg == null) return;
                if (!VersePlugin.Isolate.Value || !VersePlugin.DivergeOnChange.Value) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (ZDOMan.instance == null) return;

                int verse = VerseOf(rpc);
                if (verse == Verses.None) return;
                _verse = verse;

                int start = pkg.GetPos();
                try
                {
                    Scan(pkg);
                }
                catch (System.Exception e)
                {
                    Watching.Clear();
                    VersePlugin.Log.LogWarning("could not read incoming zdo data: " + e.Message);
                }
                pkg.SetPos(start);
            }

            private static void Postfix()
            {
                if (Watching.Count == 0) return;

                foreach (Pending p in Watching) Fork(p);
                Watching.Clear();
                _verse = Verses.None;
            }
        }

        /// <summary>
        /// Walks the incoming package looking for shared objects whose data is newer than
        /// ours, which is exactly vanilla's own test for "this is a change worth applying".
        /// </summary>
        private static void Scan(ZPackage pkg)
        {
            int invalidated = pkg.ReadInt();
            for (int i = 0; i < invalidated; i++) pkg.ReadZDOID();

            var data = new ZPackage();
            while (true)
            {
                ZDOID uid = pkg.ReadZDOID();
                if (uid.IsNone()) break;

                pkg.ReadUShort();                 // owner revision
                uint revision = pkg.ReadUInt();   // data revision
                pkg.ReadLong();                   // owner
                pkg.ReadVector3();                // position
                pkg.ReadPackage(ref data);

                ZDO zdo = ZDOMan.instance.GetZDO(uid);
                if (zdo == null) continue;                    // new: Authorship tags it instead
                if (ZdoVerse.Of(zdo) != Verses.None) continue; // already some verse's own

                // The arena's ring is a fixture, not world content, and copy-on-write exists to
                // protect what players own. Forking it duplicates it: a live test tore down 274
                // ring pieces where 120 had been built, because every wall a troll scuffed left
                // a pristine copy behind and the copy carried the ring marker too. Damage to the
                // ring is therefore shared, and ArenaRing.Repair puts it back each run.
                if (ArenaRing.IsRing(zdo)) continue;
                if (revision <= zdo.DataRevision) continue;    // nothing actually changing

                var before = new ZPackage();
                zdo.Serialize(before);

                Watching.Add(new Pending
                {
                    Uid = uid,
                    Revision = zdo.DataRevision,
                    Prefab = zdo.GetPrefab(),
                    Position = zdo.GetPosition(),
                    Before = before
                });
            }
        }

        /// <summary>
        /// Hands the changed object to the verse that changed it and leaves a pristine
        /// replacement behind for everyone else.
        /// </summary>
        private static void Fork(Pending p)
        {
            ZDO changed = ZDOMan.instance.GetZDO(p.Uid);
            if (changed == null) return;

            // Vanilla may have decided not to apply it after all; only a real change diverges.
            if (changed.DataRevision == p.Revision) return;
            if (ZdoVerse.Of(changed) != Verses.None) return;

            try
            {
                // The replacement is created first, while the snapshot is known good. Authorship
                // is suspended across it: this creation happens inside RPC_ZDOData, where
                // anything new would otherwise be tagged as the sending player's own work, and
                // this copy belongs to nobody.
                Authorship.Suspend();
                try
                {
                    ZDO pristine = ZDOMan.instance.CreateNewZDO(p.Position, p.Prefab);
                    p.Before.SetPos(0);
                    pristine.Deserialize(p.Before);
                    pristine.SetPrefab(p.Prefab);

                    // And hidden from the verse that is keeping the changed one, or that
                    // verse would be sent both: the copy is untagged, and untagged means
                    // visible to everybody. Without this a party that fought a draugr to half
                    // health would watch a second, untouched one appear beside it.
                    HideMask.Hide(pristine, _verse);
                }
                finally
                {
                    Authorship.Resume();
                }

                // And the changed one becomes the acting verse's, under the ZDOID its client is
                // already holding, so nothing on that machine has to change.
                ZdoVerse.Set(changed, _verse);
                Metrics.Forked++;
            }
            catch (System.Exception e)
            {
                _broken = true;
                VersePlugin.Log.LogError(
                    "Could not diverge a shared object, so shared-world changes will leak " +
                    "between verses from now on. Turn Verse.DivergeOnChange off to silence " +
                    "this and keep the rest working: " + e);
            }
        }


        /// <summary>
        /// Copy-on-write for an *interaction* rather than a change: the same split as
        /// <see cref="Fork"/>, but triggered before anything has happened to the object.
        ///
        /// <para>Needed because a great deal of Valheim works by RPC-to-owner rather than by
        /// writing a field. `ZNetView.InvokeRPC(string, …)` addresses the ZDO's owner
        /// (`ZNetView.cs:333`), and damage is no exception, so a shared creature — every
        /// creature placed by a dungeon or a location is server-created and therefore shared —
        /// is owned by one verse at a time and simply absorbs everyone else's blows. The relay
        /// used to drop those as cross-verse traffic, which left an invulnerable monster
        /// standing in the room.</para>
        ///
        /// <para>The direction is the same as the mutation split and for the same reason: the
        /// acting verse keeps the object under the ZDOID its client is already holding, and
        /// everyone else gets a fresh copy. Here the copy is taken from the object exactly as
        /// it stands, because nothing has changed yet — so the others keep the creature at the
        /// health it had, and the actor inherits that same health rather than a fresh one. A
        /// half-fought draugr stays half-fought for whoever walks in next, which is what the
        /// shared world already meant before the fork.</para>
        ///
        /// <para>Returns true if the caller now owns the object.</para>
        /// </summary>
        internal static bool Claim(ZDO shared, int verse)
        {
            if (_broken) return false;
            if (shared == null || verse == Verses.None) return false;
            if (!VersePlugin.ClaimOnInteraction.Value) return false;

            // Already somebody's, or this verse has destroyed it: neither is ours to take.
            if (ZdoVerse.Of(shared) != Verses.None) return false;
            if (HideMask.HiddenFrom(shared, verse)) return false;

            // The arena ring is a shared fixture, and the fork path is not the only one that
            // duplicates it: a creature swinging at a wall sends a damage RPC to that wall.s
            // owner, and a cross-verse one lands here, where Claim would hand the verse its own
            // copy. The mutation path was exempted earlier; this is the other door.
            if (ArenaRing.IsRing(shared)) return false;

            try
            {
                var snapshot = new ZPackage();
                shared.Serialize(snapshot);
                snapshot.SetPos(0);

                int prefab = shared.GetPrefab();
                UnityEngine.Vector3 at = shared.GetPosition();

                Authorship.Suspend();
                try
                {
                    ZDO pristine = ZDOMan.instance.CreateNewZDO(at, prefab);
                    pristine.Deserialize(snapshot);
                    pristine.SetPrefab(prefab);

                    // Hidden from the verse taking the original, for the same reason as in
                    // Fork: an untagged copy is visible to everybody, including the verse
                    // that just took its own.
                    HideMask.Hide(pristine, verse);
                }
                finally
                {
                    Authorship.Resume();
                }

                ZdoVerse.Set(shared, verse);
                Metrics.Claimed++;
                return true;
            }
            catch (System.Exception e)
            {
                _broken = true;
                VersePlugin.Log.LogError(
                    "Could not claim a shared object on interaction, so shared objects will " +
                    "stay unusable to every verse but their owner's from now on: " + e);
                return false;
            }
        }

        private static int VerseOf(ZRpc rpc)
        {
            if (rpc == null) return Verses.None;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || peer.m_rpc != rpc) continue;
                return Peers.VerseOf(peer.m_uid);
            }
            return Verses.None;
        }
    }
}
