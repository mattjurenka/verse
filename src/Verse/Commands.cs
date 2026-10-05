using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The one chat command, and the help that explains the whole idea to a player who has
    /// never heard of it.
    ///
    /// The trigger is a plain word and not "/verse", because a leading slash never leaves the
    /// player's machine: <c>Chat.InputText</c> strips it and runs the rest as a local console
    /// command, so the server never hears it. Anything without a slash is sent as ordinary
    /// chat, which is what <see cref="Overhear"/> reads.
    /// </summary>
    internal static class Commands
    {
        private static string Word =>
            string.IsNullOrWhiteSpace(VersePlugin.CommandWord.Value)
                ? "!verse"
                : VersePlugin.CommandWord.Value.Trim();

        private static string WarpWord =>
            string.IsNullOrWhiteSpace(VersePlugin.WarpCommandWord.Value)
                ? "!warp"
                : VersePlugin.WarpCommandWord.Value.Trim();

        internal static bool Match(string commandWord, string text, out string rest) =>
            CommandText.Match(commandWord, text, out rest);

        internal static void Handle(long peer, string account, string rest)
        {
            string[] parts = CommandText.Words(rest);
            string verb = CommandText.Verb(parts);

            switch (verb)
            {
                case "":
                case "status":
                case "who":
                    Status(peer, account);
                    return;

                case "help":
                    VerseIdentity.SayTo(peer, Help());
                    return;

                case "private":
                    Reply(peer, Verses.SetAccess(account, Access.Private, null),
                        "Your verse is private again. Nobody new can join.");
                    return;

                case "open":
                    Reply(peer, Verses.SetAccess(account, Access.Open, null),
                        $"Your verse is open. Anyone can join with: {Word} join {Verses.Of(account)}");
                    return;

                case "password":
                    if (parts.Length < 2)
                    {
                        VerseIdentity.SayTo(peer, $"Give a password: {Word} password <word>");
                        return;
                    }
                    Reply(peer, Verses.SetAccess(account, Access.Password, parts[1]),
                        $"Password set. Others can join with: {Word} join {Verses.Of(account)} {parts[1]}");
                    return;

                case "invite":
                    Invite(peer, account, parts);
                    return;

                case "spawn":
                case "home":
                    SetSpawn(peer, account);
                    return;

                case "join":
                    Join(peer, account, parts);
                    return;

                case "leave":
                    Leave(peer, account);
                    return;

                case "diag":
                    Diag(peer, account, parts);
                    return;

                default:
                    VerseIdentity.SayTo(peer, $"I do not know '{verb}'. Try {Word} help");
                    return;
            }
        }

        private static void Reply(long peer, string error, string success) =>
            VerseIdentity.SayTo(peer, error ?? success);

        /// <summary>
        /// Temporary diagnostic for the portal/leftover-debris isolation bug: logs every ZDO
        /// within 60 m of the caller - prefab, verse tag, creator and owner - so the actual
        /// state can be read from the server log instead of guessed at. Admins only; not meant
        /// to stay forever.
        /// </summary>
        private static System.Reflection.FieldInfo _diagById;

        private static void Diag(long peer, string account, string[] parts)
        {
            if (!Verses.IsServerAdmin(account))
            {
                VerseIdentity.SayTo(peer, "Admins only.");
                return;
            }

            if (parts.Length > 1 && parts[1].ToLowerInvariant() == "keys")
            {
                DiagKeys(peer, account);
                return;
            }

            _diagById = _diagById ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");

            if (parts.Length > 1 && parts[1].ToLowerInvariant() == "portals")
            {
                if (_diagById?.GetValue(ZDOMan.instance) is Dictionary<ZDOID, ZDO> allForPortals)
                    DiagPortals(peer, allForPortals);
                return;
            }

            if (parts.Length > 1 && parts[1].ToLowerInvariant() == "portalfix")
            {
                int target = parts.Length > 2 && int.TryParse(parts[2], out int v) ? v : Verses.Of(account);
                PortalRepair.Report plan = PortalRepair.Plan(target);
                VersePlugin.Log.LogInfo(PortalRepair.Describe(plan));
                VerseIdentity.SayTo(peer,
                    $"Planned {plan.Fixes.Count} portal fix(es) for verse {target}, {plan.Ambiguous.Count} left alone - " +
                    $"see the server log. Nothing written yet; apply with {Word} diag portalapply {target}.");
                return;
            }

            if (parts.Length > 1 && parts[1].ToLowerInvariant() == "portalapply")
            {
                int target = parts.Length > 2 && int.TryParse(parts[2], out int v) ? v : Verses.Of(account);
                PortalRepair.Report plan = PortalRepair.Plan(target);
                VersePlugin.Log.LogInfo($"applying for verse {target}: " + PortalRepair.Describe(plan));
                PortalRepair.Apply(plan);
                VerseIdentity.SayTo(peer, $"Applied {plan.Fixes.Count} portal fix(es) for verse {target}.");
                return;
            }
            if (!(_diagById?.GetValue(ZDOMan.instance) is Dictionary<ZDOID, ZDO> all))
            {
                VerseIdentity.SayTo(peer, "Could not read the object table.");
                return;
            }

            if (parts.Length > 1 && parts[1].ToLowerInvariant() == "world")
            {
                DiagWorld(peer, all);
                return;
            }

            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            if (connection == null) return;
            Vector3 at = connection.m_refPos;

            int myVerse = Verses.Of(account);
            VersePlugin.Log.LogInfo(
                $"diag: {peer} ({account}) at {at.x:0},{at.z:0}, verse {myVerse} - objects within 100 m:");

            // No display cap: the first diag capped at 80 of (that run) 350 nearby objects, and
            // whatever was being looked for could easily have been past the cut rather than
            // genuinely absent - this logs every one, however many that turns out to be.
            int total = 0;
            foreach (ZDO zdo in all.Values)
            {
                if (Vector3.Distance(zdo.GetPosition(), at) > 100f) continue;
                total++;

                string prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab())?.name ?? zdo.GetPrefab().ToString();
                VersePlugin.Log.LogInfo(
                    $"  diag: {prefab} verse={ZdoVerse.Of(zdo)} creator={zdo.GetLong(ZDOVars.s_creator, 0L)} " +
                    $"owner={zdo.GetOwner()} persistent={zdo.Persistent} dist={Vector3.Distance(zdo.GetPosition(), at):0} " +
                    $"at {zdo.GetPosition().x:0},{zdo.GetPosition().z:0}");
            }

            VersePlugin.Log.LogInfo($"diag: {total} object(s) within 100 m, all logged.");
            VerseIdentity.SayTo(peer, $"Logged {total} nearby object(s) to the server log.");
        }

        /// <summary>
        /// Guardian Powers showing up for bosses the caller's own verse has not earned are not
        /// a ZDO problem at all - they come from <c>GlobalKeys</c>, not from any networked
        /// object, so no amount of sweeping ZDOs could ever touch them. Logs whether per-verse
        /// key scoping is even active, the caller's resolved verse, that verse's own key list,
        /// and - the one that actually matters here - every key still sitting in the *world*
        /// set, tagged progression or not. A progression key still there after migration is
        /// the leak: <c>Keys.For</c> unions the world set into every verse unconditionally.
        /// </summary>
        private static void DiagKeys(long peer, string account)
        {
            int verse = Verses.Of(account);
            VersePlugin.Log.LogInfo(
                $"diag keys: Keys.Active={Keys.Active} caller verse={verse}");

            Record record = Verses.Get(verse);
            VersePlugin.Log.LogInfo(
                $"  diag: verse {verse}'s own keys: {(record?.Keys == null ? "(no record)" : string.Join(", ", record.Keys))}");

            List<string> world = Keys.DiagWorldSet();
            VersePlugin.Log.LogInfo($"  diag: world set has {world.Count} key(s):");
            foreach (string key in world) VersePlugin.Log.LogInfo($"    diag: {key}");

            VerseIdentity.SayTo(peer, "Logged key state to the server log.");
        }

        /// <summary>
        /// Portal pairing is not verse-aware at all - it is <c>ZDOMan</c>'s own global,
        /// tag-matching connection system (<c>ZDOExtraData.ConnectionType.Portal</c>), built
        /// once at world load and maintained for every portal in the world regardless of who
        /// can see it. Two portals in two different verses with the same tag text (including
        /// two blank tags) are, as far as that system is concerned, just a pair - it has no
        /// idea a verse exists. Logs every portal's tag, verse, and what it is actually
        /// connected to, so a wrong pairing shows up directly instead of being guessed at.
        /// </summary>
        private static void DiagPortals(long peer, Dictionary<ZDOID, ZDO> all)
        {
            int shown = 0;
            foreach (ZDO zdo in all.Values)
            {
                string prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab())?.name;
                if (prefab != "portal_wood") continue;
                shown++;

                string tag = zdo.GetString(ZDOVars.s_tag);
                ZDOID target = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                ZDO targetZdo = target != ZDOID.None ? ZDOMan.instance.GetZDO(target) : null;

                string targetDesc = target == ZDOID.None
                    ? "none"
                    : targetZdo == null
                        ? $"{target} (not loaded here)"
                        : $"{target} verse={ZdoVerse.Of(targetZdo)} at {targetZdo.GetPosition().x:0},{targetZdo.GetPosition().z:0}";

                VersePlugin.Log.LogInfo(
                    $"  diag: portal {zdo.m_uid} verse={ZdoVerse.Of(zdo)} tag=\"{tag}\" " +
                    $"at {zdo.GetPosition().x:0},{zdo.GetPosition().z:0} -> {targetDesc}");
            }

            VersePlugin.Log.LogInfo($"diag portals: {shown} portal(s) logged.");
            VerseIdentity.SayTo(peer, $"Logged {shown} portal(s) to the server log.");
        }

        private static readonly string[] DiagWorldNeedles = { "trophy", "guardianstone", "sacrificial", "gp_" };

        /// <summary>A count and one example position for <see cref="DiagWorld"/> - a plain
        /// struct rather than a tuple, because this targets net462 without System.ValueTuple
        /// shipped alongside the plugin: tuple syntax compiles fine and throws at the Valheim
        /// server's own runtime the first time it actually executes, which is exactly as loud
        /// as "no output and no error" - the catch in Overhear swallows it as a warning.</summary>
        private struct DiagWorldEntry { internal int Count; internal Vector3 At; }

        /// <summary>
        /// World-wide, not position-limited: every distinct prefab/verse combination whose
        /// prefab name matches one of <see cref="DiagWorldNeedles"/>, tagged and untagged both
        /// shown. Started as a trophy-only, untagged-only search; "Sacrificial Stone"/"Trophy
        /// Hook" (localization keys guardianstone_name / guardianstone_hook_name) turned up
        /// instead, so the actual prefab names needed were not "trophy" at all - the display
        /// text and the prefab name are not the same string, which is the whole reason this
        /// needs a real lookup rather than a radius guess.
        /// </summary>
        private static void DiagWorld(long peer, Dictionary<ZDOID, ZDO> all)
        {
            var byKey = new Dictionary<string, DiagWorldEntry>();

            foreach (ZDO zdo in all.Values)
            {
                string prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab())?.name ?? zdo.GetPrefab().ToString();
                bool matches = false;
                foreach (string needle in DiagWorldNeedles)
                    if (prefab.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0) { matches = true; break; }
                if (!matches) continue;

                string key = $"{prefab} verse={ZdoVerse.Of(zdo)}";
                byKey.TryGetValue(key, out DiagWorldEntry entry);
                entry.Count++;
                entry.At = zdo.GetPosition();
                byKey[key] = entry;
            }

            VersePlugin.Log.LogInfo($"diag world: {byKey.Count} distinct prefab/verse combination(s) found (tagged and untagged both shown):");
            foreach (KeyValuePair<string, DiagWorldEntry> kv in byKey)
                VersePlugin.Log.LogInfo(
                    $"  diag: {kv.Key} x{kv.Value.Count}, e.g. at {kv.Value.At.x:0},{kv.Value.At.z:0}");
            if (byKey.Count == 0)
                VersePlugin.Log.LogInfo("  diag: no matching ZDO anywhere in the world - whatever this is, it is not a networked object at all.");

            VerseIdentity.SayTo(peer, $"Logged {byKey.Count} distinct prefab/verse combination(s) to the server log.");
        }

        /// <summary>
        /// Moves where arrivals in this verse are put down to where the leader is standing, so
        /// an invited player lands at the base instead of the start temple.
        /// </summary>
        private static void SetSpawn(long peer, string account)
        {
            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            string problem = Spawns.SetHere(account, connection);
            if (problem != null)
            {
                VerseIdentity.SayTo(peer, problem);
                return;
            }

            VerseIdentity.SayTo(peer, "Anyone arriving in your verse will be put down here.");
        }

        private static void Status(long peer, string account)
        {
            int id = Verses.Of(account);
            Record r = Verses.Get(id);
            if (r == null)
            {
                VerseIdentity.SayTo(peer, "You are not in a verse yet.");
                return;
            }

            string access;
            switch (r.Access)
            {
                case Access.Open: access = "open to anyone"; break;
                case Access.Password: access = "open with a password"; break;
                case Access.Invite: access = "invite only"; break;
                default: access = "private"; break;
            }

            var names = new List<string>();
            foreach (string member in r.Members) names.Add(NameOf(member) ?? "(offline)");

            VerseIdentity.SayTo(peer, new[]
            {
                $"You are in verse {id}, which is {access}.",
                $"Party: {string.Join(", ", names.ToArray())} ({r.Members.Count}/{Verses.MaxParty})" +
                (Verses.IsLeader(account, r) ? " - you lead it." : $" - led by {NameOf(r.Leader) ?? "someone offline"}."),
                $"{Word} help explains what you can do with it - {Spawns.Describe(id)}."
            });
        }

        private static void Invite(long peer, string account, string[] parts)
        {
            if (parts.Length < 2)
            {
                VerseIdentity.SayTo(peer, $"Who? {Word} invite <player name>");
                return;
            }

            string wanted = string.Join(" ", parts, 1, parts.Length - 1);
            string guest = AccountOf(wanted);
            if (guest == null)
            {
                // Only connected players can be invited: an invite is keyed on the account id,
                // and a name is only tied to one while they are online.
                VerseIdentity.SayTo(peer, $"I cannot see anyone called '{wanted}' online.");
                return;
            }

            string error = Verses.Invite(account, guest);
            Reply(peer, error,
                $"Invited {wanted}. They join with: {Word} join {Verses.Of(account)}");

            if (error == null)
            {
                long them = PeerOf(guest);
                if (them != 0L)
                    VerseIdentity.SayTo(them,
                        $"{NameOf(account)} invited you to verse {Verses.Of(account)}. " +
                        $"Join with: {Word} join {Verses.Of(account)}");
            }
        }

        private static void Join(long peer, string account, string[] parts)
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], out int id))
            {
                VerseIdentity.SayTo(peer, $"Which verse? {Word} join <number> [password]");
                return;
            }

            string password = parts.Length > 2 ? parts[2] : null;
            bool confirmed = CommandText.Confirmed(parts);

            // Giving up a verse nobody else is in means its world stops being reachable, so
            // that specific case is confirmed rather than just done. Joining when others
            // remain behind costs nothing and needs no ceremony.
            Record mine = Verses.Get(Verses.Of(account));
            bool losesWorld = mine != null && mine.Members.Count <= 1;

            if (losesWorld && !confirmed)
            {
                VerseIdentity.SayTo(peer, new[]
                {
                    $"You are the only one in verse {mine.Id}. Joining verse {id} gives it up,",
                    "and everything you have built there stops being reachable.",
                    $"If you are sure: {Word} join {id} " + (password ?? "") + " confirm"
                });
                return;
            }

            string error = Verses.Join(account, id, password);
            if (error != null)
            {
                VerseIdentity.SayTo(peer, error);
                return;
            }

            Depart(peer, account, $"Welcome to verse {id}.");
        }

        private static void Leave(long peer, string account)
        {
            Record mine = Verses.Get(Verses.Of(account));
            if (mine == null)
            {
                VerseIdentity.SayTo(peer, "You are not in a verse.");
                return;
            }

            // Leaving always works, including when you are the only one in your verse: it is
            // how you ask for a brand new world, and it is the quickest way to move an
            // account between verses when testing.
            bool wasAlone = mine.Members.Count <= 1;
            int old = mine.Id;
            int fresh = Verses.LeaveToNew(account);

            Depart(peer, account, wasAlone
                ? $"Verse {old} is behind you - you are now in verse {fresh}, empty and yours alone."
                : $"You have left verse {old} - you are now in verse {fresh}, which is yours alone.");
        }

        /// <summary>
        /// Tells a player what happened and then disconnects them, because a verse change only
        /// takes effect on reconnect - that is when a client rebuilds its view of the world
        /// from nothing. The pause is to let the chat line actually reach them first; the
        /// disconnect screen itself cannot carry a message, because the reason is picked from
        /// a fixed vanilla enum.
        /// </summary>
        private static void Depart(long peer, string account, string line)
        {
            // The new verse only takes effect on reconnect, and the client will spawn at
            // wherever it last logged out - which belongs to the verse they are leaving. Note
            // the account now; Spawns moves them once their new body appears.
            Spawns.Expect(account);

            VerseIdentity.SayTo(peer, new[]
            {
                line,
                "Reconnect to the same server to arrive - you will be dropped in a moment."
            });

            Verses.SaveIfDirty();

            if (VersePlugin.Instance != null)
                VersePlugin.Instance.StartCoroutine(Kick(peer));
        }

        private static IEnumerator Kick(long peerId)
        {
            yield return new WaitForSeconds(2.5f);

            ZNetPeer peer = ZNet.instance?.GetPeer(peerId);
            if (peer != null)
            {
                VersePlugin.Log.LogInfo($"dropping peer {peerId} so they can rejoin in their new verse");
                ZNet.instance.Disconnect(peer);
            }
        }

        private static string[] Help()
        {
            return new[]
            {
                "Every player has their own world here - a verse. Yours is private: nobody",
                "else can see it, reach it or build in it, and no portal leads out of it.",
                $"You can have up to {Verses.MaxParty} players in one verse, including you.",
                $"{Word}                 - which verse you are in, and who is in it",
                $"{Word} open            - let anyone join yours",
                $"{Word} password <word> - let anyone with the word join yours",
                $"{Word} invite <player> - invite one player who is online now",
                $"{Word} private         - close it again",
                $"{Word} join <n> [word] - join someone else's verse, giving up your own",
                $"{Word} leave           - start over in a brand new empty verse of your own",
                $"{Word} spawn           - put arrivals where you are standing, not at the temple",
                $"{WarpWord} spawn          - teleport to where arrivals land",
                $"{WarpWord} bed            - teleport to a bed you have claimed",
                "Changing verse disconnects you; just reconnect to the same server.",
                "Joining someone gives up your own world, so you are asked to confirm it."
            };
        }

        // --- names and accounts -------------------------------------------------------------
        // Resolved through connected peers rather than the player list, because the registry is
        // keyed on the socket's platform id and a PlayerInfo carries a differently-formatted one.

        private static string NameOf(string account)
        {
            if (ZNet.instance == null) return null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                if (Peers.PlatformId(peer) == account) return peer.m_playerName;
            }
            return null;
        }

        private static string AccountOf(string playerName)
        {
            if (ZNet.instance == null) return null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                if (string.Equals(peer.m_playerName, playerName, StringComparison.OrdinalIgnoreCase))
                    return Peers.PlatformId(peer);
            }
            return null;
        }

        private static long PeerOf(string account)
        {
            if (ZNet.instance == null) return 0L;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                if (Peers.PlatformId(peer) == account) return peer.m_uid;
            }
            return 0L;
        }
    }

    /// <summary>
    /// Hears what players type. Chat addressed to the server arrives as a routed RPC that a
    /// dedicated server registers no handler for, so without this prefix the copy addressed to
    /// <see cref="VerseIdentity"/> is simply dropped. Never skips the original: on a listen
    /// server the host's own <c>Chat</c> still has to handle its own messages.
    /// </summary>
    /// <summary>Which of the three command words a line matched.</summary>
    internal enum Spoken { Verse, Warp, Arena }

    internal static class Overhear
    {
        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
        internal static class ZRoutedRpc_HandleRoutedRPC_Patch
        {
            private static readonly int SayHash = "Say".GetStableHashCode();
            private static readonly int ChatMessageHash = "ChatMessage".GetStableHashCode();

            /// <summary>
            /// One line typed in chat is not one RPC: vanilla's <c>Chat.CheckPermissionsAndSendChatMessageRPCsAsync</c>
            /// sends one <c>Say</c> per listener in the sender's own player list (see
            /// <see cref="GlobalChat"/>'s doc comment), and <see cref="VerseIdentity"/>
            /// deliberately keeps a synthetic entry in that list so a lone player's chat
            /// reaches the server at all. A solo player therefore sends the same text twice -
            /// once addressed to themselves, once to the synthetic entry - and this patch
            /// filters by sender and hash only, never by target, so both ran the command.
            /// Same guard <see cref="GlobalChat.Deliver"/> already uses for its own copy of
            /// this exact problem.
            /// </summary>
            private static long _lastSender;
            private static string _lastText;
            private static float _lastAt;

            private static void Prefix(ZRoutedRpc.RoutedRPCData data)
            {
                if (data?.m_parameters == null) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (data.m_methodHash != SayHash && data.m_methodHash != ChatMessageHash) return;

                try
                {
                    // A copy: the real package is about to be read by whatever else handles
                    // this message, and its read position matters.
                    var pkg = new ZPackage(data.m_parameters.GetArray());

                    if (data.m_methodHash == ChatMessageHash) pkg.ReadVector3();
                    int type = pkg.ReadInt();
                    var who = new UserInfo();
                    who.Deserialize(ref pkg);
                    string text = pkg.ReadString();

                    if ((Talker.Type)type == Talker.Type.Ping) return;

                    float now = Time.time;
                    if (data.m_senderPeerID == _lastSender && text == _lastText && now - _lastAt < 0.5f)
                        return;             // the same line, addressed to a different listener
                    _lastSender = data.m_senderPeerID;
                    _lastText = text;
                    _lastAt = now;

                    // Three command words now, tried in turn: the verse command, the warp
                    // command and the arena. Matched by word rather than by target, for the
                    // duplicate-send reason above.
                    var which = Spoken.Verse;
                    if (!Commands.Match(
                        string.IsNullOrWhiteSpace(VersePlugin.CommandWord.Value)
                            ? "!verse" : VersePlugin.CommandWord.Value.Trim(),
                        text, out string rest))
                    {
                        if (Warp.Match(text, out rest)) which = Spoken.Warp;
                        else if (Arena.Match(text, out rest)) which = Spoken.Arena;
                        else return;
                    }

                    ZNetPeer peer = ZNet.instance.GetPeer(data.m_senderPeerID);
                    string account = Peers.PlatformId(peer);
                    if (string.IsNullOrEmpty(account))
                    {
                        VersePlugin.Log.LogWarning(
                            $"command from peer {data.m_senderPeerID} with no platform id, ignored");
                        return;
                    }

                    VersePlugin.Log.LogInfo(
                        $"{which} command from {peer.m_playerName}: verse {Verses.Of(account)} <- \"{rest}\"");
                    switch (which)
                    {
                        case Spoken.Warp: Warp.Handle(data.m_senderPeerID, account, rest); break;
                        case Spoken.Arena: Arena.Handle(data.m_senderPeerID, account, rest); break;
                        default: Commands.Handle(data.m_senderPeerID, account, rest); break;
                    }
                }
                catch (Exception e)
                {
                    VersePlugin.Log.LogWarning("Could not read a chat message: " + e.Message);
                }
            }
        }
    }
}
