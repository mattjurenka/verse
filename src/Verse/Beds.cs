using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Finds the bed an account has claimed, for the <c>!warp bed</c> command.
    ///
    /// A claimed bed's ZDO carries the claimer's player id in <c>ZDOVars.s_owner</c> -
    /// the same id <c>Player</c> writes into <c>ZDOVars.s_playerID</c> on its own character
    /// ZDO the moment it spawns (see <c>Bed.RPC_SetOwner</c> and <c>Player.SetPlayerID</c> in
    /// the decompiled client). Both are plain server-visible ZDO fields, so this needs no
    /// cooperation from the client and none of the <c>PlayerProfile</c> state that
    /// <see cref="Spawns"/> already documents as unreadable from here - the bed itself, unlike
    /// the client's notion of "my current spawn point", is a real networked object.
    ///
    /// If an account has claimed more than one bed in its own verse - nothing stops a player
    /// re-claiming a different bed later - this returns whichever match the scan reaches
    /// first. Only the client's own profile knows which claim is the "current" one, and that
    /// is exactly the state this plugin cannot see.
    /// </summary>
    internal static class Beds
    {
        private static FieldInfo _byId;
        private static readonly Dictionary<int, bool> IsBedPrefab = new Dictionary<int, bool>();

        /// <summary>The claimed bed's position, nudged up clear of the floor, or false if none was found.</summary>
        internal static bool Find(int verse, long playerId, out Vector3 point)
        {
            point = Vector3.zero;
            if (playerId == 0L || verse == Verses.None) return false;
            if (ZNetScene.instance == null || ZDOMan.instance == null) return false;

            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (!(_byId?.GetValue(ZDOMan.instance) is Dictionary<ZDOID, ZDO> all)) return false;

            foreach (ZDO zdo in all.Values)
            {
                if (ZdoVerse.Of(zdo) != verse) continue;
                if (!IsBed(zdo.GetPrefab())) continue;
                if (zdo.GetLong(ZDOVars.s_owner, 0L) != playerId) continue;

                point = zdo.GetPosition() + Vector3.up * 0.5f;
                return true;
            }

            return false;
        }

        /// <summary>By component rather than by prefab name, the same way <see cref="Sweep"/> classifies objects: a world has a handful of bed prefabs and this is cheap to cache once per hash.</summary>
        private static bool IsBed(int prefab)
        {
            if (IsBedPrefab.TryGetValue(prefab, out bool known)) return known;

            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            bool isBed = go != null && go.GetComponent<Bed>() != null;
            IsBedPrefab[prefab] = isBed;
            return isBed;
        }
    }
}
