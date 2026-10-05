using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// Gets players onto a listed server without a password, in either of two strengths:
    /// <see cref="VersePlugin.NoPassword"/> removes it outright — no padlock, no dialog — and
    /// <see cref="VersePlugin.AcceptAnyPassword"/> keeps both and accepts whatever is typed.
    ///
    /// <para>A password has to be there at all, because vanilla refuses to run a listed server
    /// without one. From <c>FejdStartup.ParseServerArguments</c>:</para>
    ///
    /// <code>
    /// if (flag &amp;&amp; !IsPublicPasswordValid(password, createWorld))
    /// {
    ///     ZLog.LogError("Error bad password:" + publicPasswordError);
    ///     Application.Quit();
    ///     return false;
    /// }
    /// </code>
    ///
    /// <para>where <c>flag</c> is <c>-public</c> and <c>IsPublicPasswordValid</c> wants five or
    /// more characters that are not a substring of the world name or the seed name. That check
    /// reads the command line directly and runs before <c>ZNet.SetServer</c>, so nothing a
    /// plugin does can satisfy it — the password on <c>ExecStart</c> has to be real. A server
    /// with <c>-public 1</c> and no <c>-password</c> exits the process rather than starting
    /// unlisted, which is worth knowing before editing the unit file. Note also that
    /// <c>-public</c> defaults to <em>true</em> when the flag is absent entirely.</para>
    ///
    /// <para>Everything after that check reads one static field, <c>ZNet.m_serverPassword</c>,
    /// and the field is assigned in exactly one place — the last line but two of
    /// <c>SetServer</c>. That is what makes removal clean rather than a game of whack-a-mole:
    /// blank it once, after vanilla's validation has had its look, and all four reads agree.</para>
    ///
    /// <list type="bullet">
    /// <item><c>ZNet.OpenServer</c> — <c>m_serverPassword != ""</c> becomes the padlock in the
    /// community browser, by way of <c>ZSteamMatchmaking.RegisterServer</c>'s
    /// <c>SetPasswordProtected</c>. Blank ⇒ no padlock.</item>
    /// <item><c>OnSteamServerRegistered</c>'s retry coroutine — recomputes the same flag for
    /// each re-register, so it follows the field and needs nothing of its own.</item>
    /// <item><c>ZNet.RPC_ServerHandshake</c> — <c>!string.IsNullOrEmpty(m_serverPassword)</c>
    /// is sent to the client as <c>ClientHandshake</c>'s <c>needPassword</c>, which is what
    /// raises the dialog. Blank ⇒ no dialog.</item>
    /// <item><c>ZNet.RPC_PeerInfo</c> — <c>m_serverPassword != text2</c>, the check itself.
    /// Also rewritten, independently: see <see cref="ZNet_RPC_PeerInfo_Patch"/>.</item>
    /// </list>
    ///
    /// <para>The two halves of the client side line up with the blanked field, which is why this
    /// works with no client cooperation at all. <c>RPC_ClientHandshake</c> takes the
    /// <c>else</c> branch when <c>needPassword</c> is false and calls <c>SendPeerInfo(rpc)</c>
    /// on its default <c>password = ""</c>; <c>SendPeerInfo</c> then writes
    /// <c>string.IsNullOrEmpty(password) ? "" : HashPassword(…)</c>, so it puts a literal empty
    /// string on the wire rather than the MD5 of one. The server compares <c>"" != ""</c> and
    /// admits the peer. Note the <c>FejdStartup.ServerPassword</c> auto-submit sits *inside*
    /// the <c>if (needPassword)</c> branch, so not even a client holding a remembered password
    /// for this server will send one.</para>
    ///
    /// <para>So <see cref="ZNet_SetServer_Patch"/> is sufficient on its own, and the
    /// <c>RPC_PeerInfo</c> transpiler is kept as the fallback for the case where the blanking
    /// does not take — a game update moving the field, mainly. With
    /// <see cref="VersePlugin.AcceptAnyPassword"/> also on, that degradation is graceful: the
    /// dialog comes back and still accepts anything, rather than locking everyone out of a
    /// server advertised as open.</para>
    ///
    /// <para>One consequence worth knowing if only <see cref="VersePlugin.AcceptAnyPassword"/>
    /// is used: the client's <c>OnPasswordEntered</c> ignores an empty submission
    /// (<c>!string.IsNullOrEmpty(pwd)</c>), so the dialog cannot be dismissed by pressing
    /// return on a blank field. The player has to type <em>something</em> — it just does not
    /// matter what. Removing the password is what does away with that.</para>
    ///
    /// <para>History, because this flip-flopped once: the dialog-and-padlock version was the
    /// original, then blanking was tried and verified against a vanilla client, then it was
    /// reverted because looking like every other listed server seemed worth more than skipping
    /// the dialog, and now both live here behind separate keys. The padlock is in any case
    /// display-only — <c>isPasswordProtected</c> is read in exactly one place,
    /// <c>ServerListElement.UpdateTextAndIcons</c>, to toggle the row's <c>Private</c> icon.
    /// Nothing in the list-building path tests it, so dropping it does not hide the server.</para>
    /// </summary>
    internal static class PublicServer
    {
        /// <summary>Whether the comparison was actually rewritten. 1 site in 1.0.16.</summary>
        internal static int Replacements { get; private set; }

        /// <summary>Set when the rewrite found nothing to do.</summary>
        internal static string Problem { get; private set; }

        /// <summary>
        /// Whether the postfix has actually blanked the field. Not a substitute for
        /// <see cref="RemovalApplied"/> in the startup check: <c>Awake</c> runs before
        /// <c>SetServer</c> does, so this is still false at the time the check prints.
        /// </summary>
        internal static bool Removed { get; private set; }

        /// <summary>Set when the postfix ran but could not reach the field.</summary>
        internal static string RemovalProblem { get; private set; }

        /// <summary>
        /// Whether Harmony kept the rewrite, rather than merely registering it — the same
        /// question <see cref="PlayerCap.Applied"/> asks, and for the same reason: a transpiler
        /// that was thrown out leaves <see cref="Replacements"/> looking fine.
        /// </summary>
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

        /// <summary>
        /// The same question as <see cref="Applied"/>, for the removal postfix. Asked rather
        /// than assumed for the same reason: Harmony registering a patch and Harmony keeping it
        /// are different things, and the difference is invisible until a player cannot connect.
        /// </summary>
        internal static bool RemovalApplied()
        {
            MethodInfo target = AccessTools.Method(typeof(ZNet), nameof(ZNet.SetServer));
            if (target == null) return false;

            Patches info = Harmony.GetPatchInfo(target);
            if (info?.Postfixes == null) return false;

            foreach (Patch patch in info.Postfixes)
                if (patch.owner == VersePlugin.Guid) return true;

            return false;
        }

        /// <summary>
        /// For the startup check. Silent unless something was asked for and is not in place —
        /// a server that is advertising itself as open and then turns people away is the one
        /// failure here worth shouting about.
        /// </summary>
        internal static string Resolve()
        {
            if (VersePlugin.NoPassword.Value)
            {
                if (RemovalProblem != null)
                    return "the password removal did not apply: " + RemovalProblem;
                if (!RemovalApplied())
                    return "the password removal was registered but Harmony rejected it";
            }

            if (!VersePlugin.AcceptAnyPassword.Value) return null;
            if (Problem != null) return "the password bypass did not apply: " + Problem;
            if (!Applied()) return "the password bypass was registered but Harmony rejected it";
            return null;
        }

        /// <summary>
        /// One phrase for the startup check's summary line. Reports what is *configured and
        /// patched*, not what has happened: <c>SetServer</c> has not run yet when this prints,
        /// so the removal cannot be confirmed here. The postfix logs its own line when it
        /// fires, and that is the one to grep for.
        /// </summary>
        internal static string Status()
        {
            if (VersePlugin.NoPassword.Value)
            {
                if (RemovalProblem != null) return "STILL ASKED FOR (" + RemovalProblem + ")";
                if (RemovalApplied()) return "being removed";
                return "STILL ASKED FOR (Harmony rejected the removal)";
            }

            if (!VersePlugin.AcceptAnyPassword.Value) return "enforced";
            if (Problem != null) return "STILL ENFORCED (" + Problem + ")";
            if (!Applied()) return "STILL ENFORCED (Harmony rejected the rewrite)";
            return "asked for, anything accepted";
        }

        /// <summary>
        /// Whether to turn a peer away over its password — the replacement for the
        /// <c>m_serverPassword != text2</c> comparison in <c>ZNet.RPC_PeerInfo</c>.
        ///
        /// <para>Reached only from rewritten IL, so it is called with the same two operands the
        /// comparison had, in the same order, and returns what the comparison returned whenever
        /// the bypass is switched off.</para>
        /// </summary>
        internal static bool Mismatch(string wanted, string offered)
        {
            if (VersePlugin.AcceptAnyPassword.Value) return false;
            return wanted != offered;
        }

        /// <summary>
        /// Blanks <c>ZNet.m_serverPassword</c> the moment vanilla finishes setting it, which is
        /// what removes the padlock and the dialog both.
        ///
        /// <code>
        /// public static void SetServer(bool server, bool openServer, bool publicServer,
        ///                              string serverName, string password, World world)
        /// {
        ///     m_isServer = server;
        ///     m_openServer = openServer;
        ///     m_publicServer = publicServer;
        ///     m_serverPassword = (string.IsNullOrEmpty(password) ? "" : HashPassword(password, ServerPasswordSalt()));
        ///     m_ServerName = serverName;
        ///     m_world = world;
        /// }
        /// </code>
        ///
        /// <para>A postfix rather than a prefix or an argument rewrite, for two reasons. The
        /// validation that would reject an empty password has already run by now — it is in
        /// <c>FejdStartup.ParseServerArguments</c>, several lines above the <c>SetServer</c>
        /// call, reading the command line rather than this parameter — so there is nothing left
        /// to satisfy. And passing <c>""</c> through instead would leave <c>SetServer</c>
        /// skipping <c>HashPassword</c>, hence skipping <c>ServerPasswordSalt()</c>, which
        /// lazily generates and caches the salt on first use; the salt is still sent to clients
        /// by <c>RPC_ServerHandshake</c> either way, so letting vanilla do its normal work and
        /// then dropping the result keeps one less thing uninitialised.</para>
        ///
        /// <para>Guarded on <c>server</c> so this is inert anywhere it is not wanted — the
        /// client calls <c>SetServer(server: false, …)</c> on its own way into a world, and a
        /// plugin that reaches across to blank a field there would be meddling in something
        /// that is not its business.</para>
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
        internal static class ZNet_SetServer_Patch
        {
            private static void Postfix(bool server)
            {
                if (!VersePlugin.NoPassword.Value) return;
                if (!server) return;

                FieldInfo password = AccessTools.Field(typeof(ZNet), "m_serverPassword");
                if (password == null)
                {
                    RemovalProblem = "ZNet.m_serverPassword not found";
                    VersePlugin.Log.LogError(
                        "NoPassword is on but ZNet.m_serverPassword could not be reached, so " +
                        "the password is still being asked for. Players need " +
                        (VersePlugin.AcceptAnyPassword.Value
                            ? "to type something into the dialog - anything will do."
                            : "the real password, which is not what this server advertises."));
                    return;
                }

                bool had = !string.IsNullOrEmpty((string)password.GetValue(null));
                password.SetValue(null, "");
                Removed = true;

                // The line to look for in the journal: the startup check above runs in Awake,
                // before SetServer, so it can only report the intent.
                VersePlugin.Log.LogInfo(had
                    ? "password removed: no padlock in the browser and no dialog on connect"
                    : "password removed: there was none set, so nothing to clear");
            }
        }

        /// <summary>
        /// Routes the password comparison through <see cref="Mismatch"/>.
        ///
        /// <code>
        /// IL_0326: ldsfld   string ZNet::m_serverPassword
        /// IL_032b: ldloc.s  13                                                    // the client's hash
        /// IL_032d: call     bool [netstandard]System.String::op_Inequality(string, string)   // &lt;- this
        /// IL_0332: brfalse  IL_03dd
        /// </code>
        ///
        /// <para>Only the <c>call</c>'s operand changes. The stack effect is identical — two
        /// strings off, one bool on — so the surrounding IL is left exactly as it was, including
        /// the invite-secret-key escape hatch inside the rejection branch. Rewriting in place
        /// also keeps whatever labels and exception blocks sit on the instruction.</para>
        ///
        /// <para><c>op_Inequality</c> on its own is far too common to match on — <c>ZNet</c>
        /// alone has dozens, nearly all of them <c>UnityEngine.Object</c>'s. It is the
        /// <c>ldsfld</c> of <c>m_serverPassword</c> feeding it that picks out this one. This is
        /// the same method <see cref="PlayerCap"/> transpiles; the two look for different
        /// instructions and run in either order.</para>
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class ZNet_RPC_PeerInfo_Patch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = new List<CodeInstruction>(instructions);

                FieldInfo password = AccessTools.Field(typeof(ZNet), "m_serverPassword");
                MethodInfo mismatch = AccessTools.Method(typeof(PublicServer), nameof(Mismatch));

                // Emitting a call with a null operand makes Harmony throw out the whole method,
                // taking every other plugin's patches on it down with it — the same guard
                // PlayerCap carries, which it learned the hard way on the live server.
                if (password == null || mismatch == null)
                {
                    Problem = password == null
                        ? "ZNet.m_serverPassword not found"
                        : "PublicServer.Mismatch not found";
                    return code;
                }

                int found = 0;
                for (int i = 1; i < code.Count; i++)
                {
                    if (code[i].opcode != OpCodes.Call) continue;
                    if (!(code[i].operand is MethodInfo called)) continue;

                    // Compared by name: the IL reads
                    // `call bool [netstandard]System.String::op_Inequality`, and relying on
                    // typeof(string) to come out equal to a type forwarded from netstandard is
                    // a bet with nothing to gain.
                    if (called.Name != "op_Inequality") continue;
                    if (called.DeclaringType?.FullName != "System.String") continue;

                    // The field load is two instructions back, with the local holding the
                    // client's hash in between. Looked for in a small window rather than at a
                    // fixed offset, in case that local load ever compiles to something longer.
                    bool ours = false;
                    for (int j = i - 1; j >= 0 && j >= i - 4; j--)
                        if (code[j].opcode == OpCodes.Ldsfld && password.Equals(code[j].operand)) { ours = true; break; }
                    if (!ours) continue;

                    code[i].operand = mismatch;
                    found++;
                }

                Replacements = found;
                if (found == 0)
                    Problem = "the password comparison in ZNet.RPC_PeerInfo is not where it was in 1.0.16";

                return code;
            }
        }
    }
}
