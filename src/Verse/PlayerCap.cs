using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// Raises the ten-player limit, which is a hard-coded constant rather than a setting.
    ///
    /// <para>In <c>ZNet.RPC_PeerInfo</c>:</para>
    ///
    /// <code>
    /// IL_02e2:  call instance int32 ZNet::GetNrOfPlayers()
    /// IL_02e7:  ldc.i4.s 0x0a          // ten
    /// IL_02e9:  blt.s    IL_0326       // fewer than ten, so let them in
    /// </code>
    ///
    /// <para>The constant is matched by the call that precedes it rather than by its value
    /// alone - a bare 10 appears in plenty of methods and this one is only interesting where
    /// it is being compared against the player count.</para>
    ///
    /// <para>Ten is roughly where vanilla's send scheduler gives out, which is why the number
    /// is there at all; see the scaling notes. With `Firehose` servicing every peer on a
    /// fixed tick the round period no longer grows with the player count, so the number can
    /// move - but the other per-peer costs are real, and <c>ZoneSystem.CreateGhostZones</c>
    /// runs for every peer at 10 Hz. Raise it and watch the busiest core, not the average.</para>
    /// </summary>
    internal static class PlayerCap
    {
        /// <summary>The constant as it appears in 1.0.16.</summary>
        internal const int Vanilla = 10;

        /// <summary>How many constants the transpiler rewrote. 1 in 1.0.16.</summary>
        internal static int Replacements { get; private set; }

        /// <summary>Whether the number the community browser prints was rewritten too.</summary>
        internal static bool Advertised { get; private set; }

        /// <summary>Set when the helper could not be resolved, so nothing was rewritten.</summary>
        internal static string Problem { get; private set; }

        /// <summary>The cap, as the patched game code now reads it.</summary>
        internal static int Limit()
        {
            int wanted = VersePlugin.MaxPlayers.Value;
            if (wanted < 1) return Vanilla;
            return wanted > 200 ? 200 : wanted;
        }

        /// <summary>Whether Harmony kept the rewrite, rather than merely registering it.</summary>
        internal static bool Applied()
        {
            MethodInfo target = AccessTools.Method(typeof(ZNet), "RPC_PeerInfo");
            if (target == null) return false;

            Patches info = Harmony.GetPatchInfo(target);
            if (info?.Transpilers == null) return false;

            foreach (Patch patch in info.Transpilers)
                if (patch.owner == VersePlugin.Guid) return true;

            return false;
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class ZNet_RPC_PeerInfo_Patch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = new List<CodeInstruction>(instructions);

                MethodInfo count = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetNrOfPlayers));
                MethodInfo limit = AccessTools.Method(typeof(PlayerCap), nameof(Limit));

                // Emitting a call to a method we could not find writes a null operand, which
                // Harmony rejects - and it rejects the *whole* method, taking every other
                // plugin's patches on it down too. Firehose did exactly that on the live
                // server and spent an evening looking like something else. Leave the IL
                // untouched and say so instead.
                if (count == null || limit == null)
                {
                    Problem = count == null
                        ? "ZNet.GetNrOfPlayers not found"
                        : "PlayerCap.Limit not found";
                    return code;
                }

                int found = 0;
                for (int i = 1; i < code.Count; i++)
                {
                    if (code[i].opcode != OpCodes.Ldc_I4_S && code[i].opcode != OpCodes.Ldc_I4) continue;
                    if (Convert.ToInt32(code[i].operand) != Vanilla) continue;
                    if (!count.Equals(code[i - 1].operand)) continue;   // the count is what it is compared with

                    // Rewritten in place so any labels or exception blocks on it survive.
                    code[i].opcode = OpCodes.Call;
                    code[i].operand = limit;
                    found++;
                }

                Replacements = found;
                if (found == 0) Problem = "the ten-player constant was not where it was in 1.0.16";
                return code;
            }
        }

        /// <summary>
        /// The same ten, written down a second time where the community browser reads it.
        ///
        /// <para><c>ZSteamMatchmaking.RegisterServer</c> advertises the limit to Steam
        /// separately from enforcing it:</para>
        ///
        /// <code>
        /// SteamGameServer.SetServerName(name);
        /// SteamGameServer.SetMapName(name);
        /// SteamGameServer.SetMaxPlayerCount(10);      // &lt;- this
        /// SteamGameServer.SetPasswordProtected(password);
        /// </code>
        ///
        /// <para>It is what the browser prints after the slash, so leaving it alone on a server
        /// whose real cap is higher makes the entry read "12 / 10" - full, and worse than
        /// full - to everybody deciding whether to join. It has no part in admitting anyone;
        /// <c>ZNet.RPC_PeerInfo</c> above is the gate.</para>
        ///
        /// <para>Matched by the call that follows rather than by the constant, same as above.
        /// The call's name is compared as a string because <c>SteamGameServer</c> lives in the
        /// Steamworks assembly, which this plugin does not reference and does not need to in
        /// order to recognise a <c>MethodInfo</c> the game handed us.</para>
        ///
        /// <para>Steam-only: the PlayFab backend used by <c>-crossplay</c> has its own
        /// registration path, which this server does not run.</para>
        /// </summary>
        [HarmonyPatch(typeof(ZSteamMatchmaking), nameof(ZSteamMatchmaking.RegisterServer))]
        internal static class ZSteamMatchmaking_RegisterServer_Patch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = new List<CodeInstruction>(instructions);

                MethodInfo limit = AccessTools.Method(typeof(PlayerCap), nameof(Limit));
                if (limit == null) return code;   // reported by the transpiler above

                for (int i = 0; i < code.Count - 1; i++)
                {
                    if (code[i].opcode != OpCodes.Ldc_I4_S && code[i].opcode != OpCodes.Ldc_I4) continue;
                    if (Convert.ToInt32(code[i].operand) != Vanilla) continue;
                    if (!(code[i + 1].operand is MethodInfo called)) continue;
                    if (called.Name != "SetMaxPlayerCount") continue;

                    code[i].opcode = OpCodes.Call;
                    code[i].operand = limit;
                    Advertised = true;
                }

                return code;
            }
        }
    }
}
