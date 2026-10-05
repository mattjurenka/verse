using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The one way this plugin puts a permanent building piece into the world: the wall, the
    /// decks, the boardwalk, the gallery railing, the ladders and the gate chests all come
    /// through here.
    ///
    /// <para><b>Why one place.</b> Four separate builders had each learned the same four things
    /// the hard way - set the prefab field by hand or no client can instantiate it (see
    /// HANDOFF.md), mark it so the arena's own sweeps do not eat it, make it persistent or
    /// nothing ever simulates it, and suspend <see cref="Authorship"/> so it is not filed as
    /// some player's handiwork. The fifth thing is new and is the reason this file exists at
    /// all.</para>
    ///
    /// <para><b>The fifth thing: these pieces do not break.</b> A ring of stone is a few hundred
    /// hit points a segment, which is a troll's afternoon - and a wall with a hole in it is an
    /// arena a fighter can walk out of and a spectator can walk into. Stone was already the
    /// toughest material this server has (<see cref="ArenaRing"/> compares them at runtime), so
    /// there was no tougher prefab to reach for. There is, however, a number: a piece's current
    /// health is <c>ZDOVars.s_health</c> on its own ZDO, read by <c>WearNTear.Awake</c> with the
    /// prefab's <c>m_health</c> only as the default, and every hit is subtracted from it by
    /// <c>WearNTear.ApplyDamage</c>. Writing a billion into it costs one float per piece and
    /// means the venue outlasts whatever turns up in it. It also ends, as a side effect, the
    /// failure that the chest decks were built to work around: an unbreakable deposit chest
    /// cannot collapse and take somebody's gear with it.</para>
    ///
    /// <para>Honest about what it is: this is a number, not a flag. Vanilla has no
    /// indestructible bit, so a piece is "unbreakable" in the sense that a player would need
    /// millions of swings. A hammer-wielding player with build rights can still <i>repair</i> it
    /// back down to the prefab's own health, which is why <see cref="Harden"/> is re-run over
    /// every fixture at the start of each run.</para>
    /// </summary>
    internal static class Fixture
    {
        /// <summary>
        /// The health written into every fixture. Large enough to be unreachable, small enough
        /// to stay exact in a float and to survive arithmetic without going infinite.
        /// </summary>
        internal const float Health = 1e9f;

        /// <summary>
        /// Creates one shared, persistent, unbreakable piece and returns its ZDO.
        ///
        /// <para>Untagged, which is the point of a fixture: verse-design.md's rule is that
        /// anything the server creates without a verse tag is sent to every verse, so one copy
        /// of the arena serves all eighteen of them.</para>
        /// </summary>
        /// <param name="marker">The caller's own marker hash, stamped as 1 so its sweeps can
        /// recognise the piece later.</param>
        internal static ZDO Place(int prefabHash, Vector3 at, Quaternion facing, int marker)
        {
            if (ZDOMan.instance == null) return null;

            Authorship.Suspend();
            try
            {
                ZDO zdo = ZDOMan.instance.CreateNewZDO(at, prefabHash);

                // CreateNewZDO's prefab argument only feeds an internal portal index; it does
                // not set the ZDO's prefab field. Without this the piece is real data that no
                // client can turn into an object. BossStones learned this the expensive way.
                zdo.SetPrefab(prefabHash);
                zdo.SetRotation(facing);
                zdo.Persistent = true;

                if (marker != 0) zdo.Set(marker, 1, okForNotOwner: true);
                if (VersePlugin.ArenaUnbreakable.Value) zdo.Set(ZDOVars.s_health, Health);

                return zdo;
            }
            finally
            {
                Authorship.Resume();
            }
        }

        /// <summary>
        /// Puts a fixture's health back up where it belongs, for pieces built before this rule
        /// existed and for anything a player has repaired down to its prefab's own health.
        /// </summary>
        /// <returns>True if the piece needed it.</returns>
        internal static bool Harden(ZDO zdo)
        {
            if (zdo == null || !VersePlugin.ArenaUnbreakable.Value) return false;
            if (zdo.GetFloat(ZDOVars.s_health, 0f) >= Health) return false;

            zdo.Set(ZDOVars.s_health, Health);
            return true;
        }

        private static System.Reflection.FieldInfo _doomed;

        /// <summary>
        /// Whether a ZDO has already been told to go, and is only still in the object table
        /// because the telling has not been sent yet.
        ///
        /// <para><b>Needed because <c>ZDOMan.DestroyZDO</c> only queues an id.</b> The object
        /// stays in <c>m_objectsByID</c>, readable, until <c>SendDestroyed</c> runs on the next
        /// <c>ZDOMan</c> update - so anything that tears a fixture down and then counts what is
        /// standing counts the pieces it just destroyed as well as the ones it just built. That
        /// mistake has now been made three times in this plugin: a gallery reported as 334
        /// pieces, a nine-step stair reported as seventeen, and a walkway that reads as three
        /// metres out of level in the frame it was relaid flat. The queue is private, which is
        /// why this is reflection rather than an ordinary question.</para>
        /// </summary>
        internal static bool Doomed(ZDO zdo)
        {
            if (zdo == null || ZDOMan.instance == null) return false;

            _doomed = _doomed ?? AccessTools.Field(typeof(ZDOMan), "m_destroySendList");
            var queued = _doomed?.GetValue(ZDOMan.instance) as System.Collections.Generic.List<ZDOID>;

            return queued != null && queued.Contains(zdo.m_uid);
        }

        private static System.Reflection.MethodInfo _destroy;
        private static readonly object[] DestroyArgs = new object[1];

        /// <summary>
        /// Removes a fixture, claiming it first.
        ///
        /// <para><b>The claim is the whole method.</b> <c>ZDOMan.DestroyZDO</c> is
        /// <c>if (zdo.IsOwner())</c> and a silent no-op for anything the server does not own -
        /// and <c>ReleaseNearbyZDOS</c> hands every persistent ZDO near a player to that player
        /// every couple of seconds, so essentially nothing in the arena is ever the server's.
        /// Without the claim a teardown does nothing and the only evidence is a rebuild that
        /// keeps finding the same pieces.</para>
        /// </summary>
        internal static void Destroy(ZDO zdo)
        {
            if (zdo == null || ZDOMan.instance == null) return;

            _destroy = _destroy ??
                       AccessTools.Method(typeof(ZDOMan), "DestroyZDO", new[] { typeof(ZDO) });
            if (_destroy == null) return;

            zdo.SetOwner(ZDOMan.GetSessionID());

            DestroyArgs[0] = zdo;
            _destroy.Invoke(ZDOMan.instance, DestroyArgs);
        }
    }
}
