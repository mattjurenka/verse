namespace Verse
{
    /// <summary>
    /// The sleep vote, kept Unity-free so <c>./test.sh</c> can exercise it. The rule has three
    /// edges that are easy to get wrong - a tie is not a majority, "everybody" cannot be
    /// expressed as "strictly more than 1.0", and nobody in bed must never skip a night - so
    /// it is worth a test rather than a careful read.
    /// </summary>
    internal static class SleepRule
    {
        /// <summary>
        /// Whether enough of the server is in bed. <paramref name="share"/> is read as
        /// "strictly more than this fraction", so 0.5 is a majority rather than a tie. 1.0
        /// means everybody, which is vanilla's rule and is handled separately because nothing
        /// can be strictly more than all of them. 0 means any one sleeper is enough - but
        /// still not none.
        /// </summary>
        internal static bool Enough(int inBed, int total, float share)
        {
            if (total <= 0) return false;      // vanilla: nobody on, nothing to skip
            if (inBed <= 0) return false;      // and nobody asleep never skips a night

            if (share < 0f) share = 0f;
            if (share >= 1f) return inBed >= total;

            return inBed > total * share;
        }
    }
}
