using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Firehose
{
    /// <summary>
    /// Ceiling 1: the send window in <c>ZDOMan.SendZDOs</c>.
    ///
    /// <code>
    /// private bool SendZDOs(ZDOPeer peer, bool flush)
    /// {
    ///     int sendQueueSize = peer.m_peer.m_socket.GetSendQueueSize();
    ///     if (!flush &amp;&amp; sendQueueSize > 10240) return false;   // (1)
    ///     int num = 10240 - sendQueueSize;                      // (2)
    ///     if (num &lt; 2048) return false;
    ///     ...fills a package up to num bytes...
    /// </code>
    ///
    /// Both 10240s are replaced with a call to <see cref="Bytes"/>. The 2048 floor is left
    /// alone - it only says "do not bother with a package smaller than this", which is still
    /// what we want.
    ///
    /// <para>The reason this one constant costs so much: for the Steam backend
    /// <c>GetSendQueueSize</c> returns <c>m_cbPendingReliable + m_cbPendingUnreliable +
    /// m_cbSentUnackedReliable</c> plus the local queue - that is, bytes in flight count
    /// against the window until the client's acknowledgement comes back. A 10240-byte window
    /// over a 160 ms round trip is 64 KB/s and no amount of bandwidth changes it. Widening
    /// the window is the one-line fix for a bandwidth-delay product, same as TCP window
    /// scaling.</para>
    ///
    /// <para>Clients run this same method to push their own changes upstream, so
    /// <see cref="Bytes"/> hands back the vanilla value unless we are the server: a player
    /// who happens to have this plugin installed should not have their own uploads
    /// repaced.</para>
    /// </summary>
    internal static class SendWindow
    {
        /// <summary>The constant as it appears in 1.0.16, and the fallback everywhere.</summary>
        internal const int Vanilla = 10240;

        private const int Min = 4096;
        /// <summary>A Steam reliable message tops out at 512 KB; stay well inside it.</summary>
        private const int Max = 262144;

        /// <summary>
        /// Which other plugins have patched a game method. Whether somebody else is on the
        /// same method decides both whether we schedule sends and whether the IL probe can
        /// conclude anything, and asking Harmony is better than hardcoding a plugin GUID: a
        /// sibling that stops patching should stop counting.
        /// </summary>
        internal static List<string> OtherOwners(MethodBase method)
        {
            var others = new List<string>();
            if (method == null) return others;

            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return others;

            foreach (string owner in info.Owners)
                if (owner != FirehosePlugin.Guid) others.Add(owner);

            return others;
        }

        /// <summary>How many constants the transpiler found. 2 in 1.0.16; the startup check reports it.</summary>
        internal static int Replacements { get; private set; }

        /// <summary>
        /// Set by <see cref="SelfTest"/> for the length of one synchronous measurement, so a
        /// round can be run at a chosen window without touching the operator's configuration.
        /// 0 means "use the configuration", which is every other moment of the process.
        /// </summary>
        internal static int Override;

        /// <summary>The window, as the patched game code now reads it. Called twice per send.</summary>
        internal static int Bytes()
        {
            if (Override > 0) return Override;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return Vanilla;

            int bytes = FirehosePlugin.Window.Value;
            if (bytes < Min) return Min;
            if (bytes > Max) return Max;
            return bytes;
        }
        /// <summary>
        /// Whether the transpiler's output was actually accepted. <see cref="Replacements"/>
        /// alone is a liar: it is counted while Harmony *enumerates* the transpiler, which
        /// happens before the rewritten IL is compiled - so a patch that Harmony then
        /// rejected still reports both constants found. That happened on the live server and
        /// the startup check cheerfully said the window was 65536 while the game was running
        /// vanilla's 10240. Asking Harmony who owns the method is the honest question.
        /// </summary>
        internal static bool Applied()
        {
            MethodInfo send = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
            if (send == null) return false;

            Patches info = Harmony.GetPatchInfo(send);
            if (info?.Transpilers == null) return false;

            foreach (Patch patch in info.Transpilers)
                if (patch.owner == FirehosePlugin.Guid) return true;

            return false;
        }

        /// Proof that the rewritten method body is well formed, which cannot otherwise be known
        /// until a player connects: Mono verifies and JITs a whole method on its first call, so
        /// malformed IL - a dropped branch label, a stack that does not balance - throws
        /// <c>InvalidProgramException</c> there and then. Calling it with a null peer makes that
        /// happen on our terms. Vanilla dereferences the peer in its first three instructions,
        /// well before it touches any state, so a null argument faults harmlessly - and a
        /// <c>NullReferenceException</c> is therefore the result we want.
        /// </summary>
        internal static string Verify()
        {
            MethodInfo send = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
            if (send == null) return "ZDOMan.SendZDOs not found";

            // Another plugin patching the same method can fault on the null peer before
            // vanilla ever dereferences it, which says nothing either way about our IL. Name
            // it and report inconclusive rather than crying wolf.
            List<string> others = OtherOwners(send);

            try
            {
                send.Invoke(ZDOMan.instance, new object[] { null, false });
                return "SUSPECT - a null peer did not fault, so this is not the method we think it is";
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                System.Exception inner = e.InnerException;
                if (inner is System.NullReferenceException)
                    return "verified, the patched body JITs";

                string what = inner == null ? "unknown" : inner.GetType().Name + ": " + inner.Message;
                if (inner is System.InvalidProgramException)
                    return "BROKEN - the rewritten body will not JIT: " + what;
                if (others.Count > 0)
                    return $"inconclusive - also patched by {string.Join(", ", others.ToArray())}, " +
                           $"whose patch faulted on the test peer first ({what})";
                return "SUSPECT - " + what;
            }
            catch (System.Exception e)
            {
                return "SUSPECT - " + e.GetType().Name + ": " + e.Message;
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        internal static class ZDOMan_SendZDOs_Patch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo bytes = AccessTools.Method(typeof(SendWindow), nameof(Bytes));
                var code = new List<CodeInstruction>(instructions);
                int found = 0;

                foreach (CodeInstruction ins in code)
                {
                    if (ins.opcode != OpCodes.Ldc_I4 || !(ins.operand is int value) || value != Vanilla)
                        continue;

                    // Rewritten in place rather than swapped for a new instruction, so the
                    // labels and exception blocks attached to it survive: the second 10240 is
                    // the branch target of both early returns above it, and a replacement
                    // that dropped those labels would produce unbranchable IL.
                    ins.opcode = OpCodes.Call;
                    ins.operand = bytes;
                    found++;
                }

                Replacements = found;
                return code;
            }
        }
    }
}
