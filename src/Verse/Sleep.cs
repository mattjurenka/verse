using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// SPIKE 10. Lets a night be skipped when most of the server is in bed, rather than only
    /// when every single person is.
    ///
    /// <para>Vanilla's rule is unanimity across the whole server:</para>
    ///
    /// <code>
    /// private bool EverybodyIsTryingToSleep()
    /// {
    ///     List&lt;ZDO&gt; allCharacterZDOS = ZNet.instance.GetAllCharacterZDOS();
    ///     if (allCharacterZDOS.Count == 0) return false;
    ///     foreach (ZDO item in allCharacterZDOS)
    ///         if (!item.GetBool(ZDOVars.s_inBed)) return false;
    ///     return true;
    /// }
    /// </code>
    ///
    /// <para><c>GetAllCharacterZDOS</c> is every character on the server, so verses turn this
    /// into a deadlock: a party all climb into bed and nothing happens, because somebody in
    /// another verse is standing up. The more verses there are, the less likely sleeping ever
    /// works again. It is the reverse of the usual leak - not one verse affecting another, but
    /// one verse preventing another from doing anything at all.</para>
    ///
    /// <para><b>Why this is a vote and not a per-verse skip.</b> Time is one world clock.
    /// <c>EnvMan.SkipToMorning</c> moves it for the whole process, and every client derives
    /// day, night and weather from the same synced time, so a verse cannot have its own
    /// morning without having its own weather and day count as well. Given that the skip is
    /// unavoidably shared, the question becomes who gets to call for it, and "most of the
    /// people here" is a better answer than "every last one".</para>
    ///
    /// <para>Note what this leaves: a party can still move everybody's clock, and since raids
    /// happen at night, repeatedly skipping shortens other parties' nights. Vanilla's own
    /// 10-second cooldown (<c>m_lastSleepTime</c>) is the only brake, and it is still there.</para>
    /// </summary>
    internal static class Sleep
    {
        /// <summary>Counted for the log line, so a night that is skipped says who voted.</summary>
        private static int _lastInBed;
        private static int _lastTotal;

        [HarmonyPatch(typeof(Game), "EverybodyIsTryingToSleep")]
        internal static class Game_EverybodyIsTryingToSleep_Patch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!VersePlugin.Isolate.Value) return true;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

                int total = 0;
                int inBed = 0;
                foreach (ZDO zdo in ZNet.instance.GetAllCharacterZDOS())
                {
                    total++;
                    if (zdo.GetBool(ZDOVars.s_inBed)) inBed++;
                }

                __result = Enough(inBed, total);

                if (__result && (inBed != _lastInBed || total != _lastTotal))
                {
                    _lastInBed = inBed;
                    _lastTotal = total;
                    VersePlugin.Log.LogInfo(
                        $"skipping the night: {inBed} of {total} player(s) in bed across all verses");
                }

                return false;
            }
        }

        /// <summary>The rule itself lives in <see cref="SleepRule"/>, where a test can reach it.</summary>
        private static bool Enough(int inBed, int total) =>
            SleepRule.Enough(inBed, total, VersePlugin.SleepFraction.Value);
    }
}
