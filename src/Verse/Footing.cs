using System.Collections.Generic;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Where a prefab's bottom and its top are, relative to the point the server positions it
    /// by. Measured off the prefab itself, once per prefab.
    ///
    /// <para><b>Why this is measured and not a constant.</b> A ZDO's position is the prefab's
    /// own origin, and where that origin sits inside the object is a decision whoever authored
    /// the prefab made: a chest's is at its base, a stone wall's is at its middle (which
    /// <see cref="ArenaRing"/> already compensates for by hand), and a floor tile's is its own
    /// business. Placing by the origin and hoping therefore produces exactly what the
    /// floating-chest screenshot showed - a deck hanging in the air with chests hovering over
    /// it - and the only way to fix that by eye is to tune an offset per prefab, which breaks
    /// again the moment <c>ArenaDeckPrefab</c> or <c>ArenaChestPrefab</c> is pointed at
    /// something else. Asking the prefab where its own bottom is costs one pass over its
    /// colliders and turns "+0.3 m seems about right" into arithmetic.</para>
    ///
    /// <para><b>Colliders first, meshes second.</b> The collider is what the game stands things
    /// on and is certain to exist on a dedicated server, which holds no graphics. A mesh's
    /// <c>bounds</c> is serialised metadata rather than vertex data, so it is still readable
    /// there too, and it covers a prefab whose only collider is a trigger.</para>
    ///
    /// <para>Follows the same idiom as <see cref="Loadout.Grid"/>, which reads a chest's item
    /// grid off its own <c>Container</c> rather than assuming 6x3: a number the game already
    /// knows is not ours to guess.</para>
    /// </summary>
    internal static class Footing
    {
        /// <summary>Measured boxes by prefab hash. A null entry is "nothing to measure".</summary>
        private static readonly Dictionary<int, Bounds?> Known = new Dictionary<int, Bounds?>();

        /// <summary>
        /// A child that is scenery rather than the object: smoke off a torch, or the marker a
        /// piece shows while it is being placed. Including one would put the "top" of a chest
        /// wherever its particles reach.
        /// </summary>
        private static bool Effect(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("vfx") || n.Contains("sfx") || n.Contains("fx_") ||
                   n.Contains("particle") || n.Contains("placemarker") || n.Contains("snappoint");
        }

        /// <summary>
        /// How far this prefab reaches below and above its own origin, in metres.
        /// <paramref name="bottom"/> is normally zero or a little under it, and
        /// <paramref name="top"/> is the surface you could stand something on.
        /// </summary>
        /// <returns>False if the prefab is unknown or has no geometry to measure, in which case
        /// the caller places by the origin as it did before - a little off is better than
        /// nothing placed.</returns>
        internal static bool Of(int prefabHash, out float bottom, out float top)
        {
            bottom = top = float.NaN;
            if (!Box(prefabHash, out Bounds box)) return false;

            bottom = box.min.y;
            top = box.max.y;
            return true;
        }

        /// <summary>
        /// The whole box a prefab occupies, in its own space: the vertical extent that
        /// <see cref="Of"/> reports plus the two horizontal ones, which is what a railing laid
        /// along the inside of a wall needs - the wall's thickness decides where the inside
        /// is.
        /// </summary>
        internal static bool Box(int prefabHash, out Bounds local) =>
            Box(prefabHash, false, out local);

        /// <summary>
        /// The same box, optionally measured from the prefab's <i>meshes</i> rather than its
        /// colliders.
        ///
        /// <para><b>Which one is right depends on the question.</b> For a thing standing on a
        /// surface the answer is "where does it look like it touches", and that is the mesh: a
        /// brazier's colliders turned out to reach well below its feet - the fire's own area,
        /// probably - so standing it on its collider floor left it hovering a metre above the
        /// gallery, which is what the game showed. For a thing the game stands other things on,
        /// the collider is the honest answer, because that is the surface physics uses.</para>
        /// </summary>
        internal static bool Box(int prefabHash, bool meshes, out Bounds local)
        {
            local = default(Bounds);

            int key = meshes ? ~prefabHash : prefabHash;

            if (Known.TryGetValue(key, out Bounds? cached))
            {
                if (!cached.HasValue) return false;
                local = cached.Value;
                return true;
            }

            GameObject prefab = ZNetScene.instance?.GetPrefab(prefabHash);

            // Deliberately not cached as a failure: the scene is not up yet on an early call,
            // and caching "unknown" then would keep the answer wrong for the whole run.
            if (prefab == null) return false;

            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            if (!meshes)
                foreach (Collider c in prefab.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null || c.isTrigger || Effect(c.name)) continue;
                    if (!Shape(c, out Bounds box)) continue;
                    Span(prefab.transform, c.transform, box, ref lo, ref hi);
                }

            if (lo.y > hi.y)
            {
                foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null || Effect(mf.name)) continue;
                    Span(prefab.transform, mf.transform, mf.sharedMesh.bounds, ref lo, ref hi);
                }
            }

            if (lo.y > hi.y)
            {
                Known[key] = null;
                return false;
            }

            local = new Bounds((lo + hi) * 0.5f, hi - lo);
            Known[key] = local;
            return true;
        }

        /// <summary>A collider's extent in its own local space, whatever shape it is.</summary>
        private static bool Shape(Collider c, out Bounds local)
        {
            local = default(Bounds);

            var box = c as BoxCollider;
            if (box != null) { local = new Bounds(box.center, box.size); return true; }

            var mesh = c as MeshCollider;
            if (mesh != null && mesh.sharedMesh != null) { local = mesh.sharedMesh.bounds; return true; }

            var sphere = c as SphereCollider;
            if (sphere != null)
            {
                local = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                return true;
            }

            var capsule = c as CapsuleCollider;
            if (capsule != null)
            {
                Vector3 size = Vector3.one * (capsule.radius * 2f);
                float along = Mathf.Max(capsule.height, capsule.radius * 2f);
                if (capsule.direction == 0) size.x = along;
                else if (capsule.direction == 1) size.y = along;
                else size.z = along;
                local = new Bounds(capsule.center, size);
                return true;
            }

            // A terrain or wheel collider has no business under a chest.
            return false;
        }

        /// <summary>
        /// Widens <paramref name="lo"/>/<paramref name="hi"/> to cover a child's box, in the
        /// prefab root's own space. All eight corners, because a child may be rotated: a
        /// rotated box's lowest corner is not its own local minimum.
        /// </summary>
        private static void Span(Transform root, Transform at, Bounds local,
                                 ref Vector3 lo, ref Vector3 hi)
        {
            Vector3 c = local.center, e = local.extents;

            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));

                Vector3 p = root.InverseTransformPoint(at.TransformPoint(corner));
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
        }
    }
}
