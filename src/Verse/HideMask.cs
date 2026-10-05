using System.Collections.Generic;

namespace Verse
{
    /// <summary>
    /// Per-verse "this verse can no longer see that object", which is what makes
    /// copy-on-write possible.
    ///
    /// The shared world is generated once and belongs to nobody (see <see cref="Authorship"/>).
    /// When a verse changes a shared object the original is left intact for everyone else and
    /// simply stops being sent to the verse that changed it. Divergence then costs in
    /// proportion to what players actually do, rather than to verses times explored area -
    /// which matters because generating the world per verse would multiply the ZDO count in a
    /// process whose ceiling is already the send scheduler.
    ///
    /// Stored on the shared ZDO itself, so it is saved with the world like the verse tag is.
    /// A count is kept in its own int field purely as a fast path: the mask is read once per
    /// ZDO per peer per send round, and virtually every object in the world has no mask at
    /// all, so the common answer has to cost one int read and nothing more.
    /// </summary>
    internal static class HideMask
    {
        private static readonly int CountKey = "verse.hidden.n".GetStableHashCode();
        private static readonly int ListKey = "verse.hidden".GetStableHashCode();

        /// <summary>
        /// Parsed masks, so a non-empty mask is decoded once per server run rather than on
        /// every send. Keyed by ZDOID; dropped when the mask changes.
        /// </summary>
        private static readonly Dictionary<ZDOID, int[]> Cache = new Dictionary<ZDOID, int[]>();

        internal static int Masked => Cache.Count;

        internal static bool HiddenFrom(ZDO zdo, int verse)
        {
            if (zdo == null || verse == Verses.None) return false;

            // The fast path, and the one taken by almost every object in the world.
            if (zdo.GetInt(CountKey, 0) == 0) return false;

            int[] verses = Read(zdo);
            for (int i = 0; i < verses.Length; i++)
                if (verses[i] == verse) return true;

            return false;
        }

        /// <summary>Stops <paramref name="verse"/> being sent this object, for good.</summary>
        internal static void Hide(ZDO zdo, int verse)
        {
            if (zdo == null || verse == Verses.None) return;
            if (HiddenFrom(zdo, verse)) return;

            int[] existing = zdo.GetInt(CountKey, 0) == 0 ? new int[0] : Read(zdo);
            var updated = new int[existing.Length + 1];
            System.Array.Copy(existing, updated, existing.Length);
            updated[existing.Length] = verse;

            Write(zdo, updated);
        }

        /// <summary>
        /// Undoes a <see cref="Hide"/>, so <paramref name="verse"/> is sent this object again.
        ///
        /// <para>Added for the arena, which builds its ring once and shares it with every verse:
        /// a troll putting a wall through in one verse masks it there for good, and the second
        /// run in that verse would otherwise happen in a broken ring. Copy-on-write itself never
        /// needs this - a verse that has diverged from a shared object is meant to stay
        /// diverged - which is why there was nothing here before.</para>
        /// </summary>
        internal static bool Reveal(ZDO zdo, int verse)
        {
            if (zdo == null || verse == Verses.None) return false;
            if (zdo.GetInt(CountKey, 0) == 0) return false;

            int[] existing = Read(zdo);
            int at = -1;
            for (int i = 0; i < existing.Length; i++)
                if (existing[i] == verse) { at = i; break; }

            if (at < 0) return false;

            var updated = new int[existing.Length - 1];
            System.Array.Copy(existing, 0, updated, 0, at);
            System.Array.Copy(existing, at + 1, updated, at, existing.Length - at - 1);

            Write(zdo, updated);
            return true;
        }

        /// <summary>
        /// Drops a destroyed object's parsed mask, so the cache does not keep an entry for a
        /// ZDOID nothing will ask about again.
        ///
        /// <para>Needed once the arena started rebuilding its ring on every run: the pieces it
        /// tears down are exactly the ones a troll had masked, and without this each run would
        /// leave their entries behind for the rest of the server's life. A ZDOID is never reused,
        /// so forgetting one can only ever be right.</para>
        /// </summary>
        internal static void Forget(ZDO zdo)
        {
            if (zdo != null) Cache.Remove(zdo.m_uid);
        }

        private static int[] Read(ZDO zdo)
        {
            if (Cache.TryGetValue(zdo.m_uid, out int[] cached)) return cached;

            byte[] bytes = zdo.GetByteArray(ListKey);
            int count = bytes == null ? 0 : bytes.Length / 4;
            var verses = new int[count];
            for (int i = 0; i < count; i++) verses[i] = System.BitConverter.ToInt32(bytes, i * 4);

            Cache[zdo.m_uid] = verses;
            return verses;
        }

        private static void Write(ZDO zdo, int[] verses)
        {
            var bytes = new byte[verses.Length * 4];
            for (int i = 0; i < verses.Length; i++)
                System.Array.Copy(System.BitConverter.GetBytes(verses[i]), 0, bytes, i * 4, 4);

            zdo.Set(ListKey, bytes);
            zdo.Set(CountKey, verses.Length, okForNotOwner: true);
            Cache[zdo.m_uid] = verses;
        }

        /// <summary>
        /// Whether every verse that exists has hidden this object, which means nothing will
        /// ever be sent it again and it is only taking up room. Swept rather than destroyed on
        /// the spot: a verse created later would otherwise never see a world the earlier
        /// verses had collectively cleared.
        /// </summary>
        internal static bool HiddenFromAll(ZDO zdo)
        {
            if (zdo == null) return false;
            int n = zdo.GetInt(CountKey, 0);
            return n > 0 && n >= Verses.LivingCount;
        }
    }
}
