using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// The challenge arena: ten waves, a loaner kit, and a line broadcast to the whole server
    /// when it ends. See docs/arena-design.md for why it is shaped like this; the short version
    /// is that it is clip bait, so every decision resolves in favour of legible over fair.
    ///
    /// <para><b>One run per verse.</b> Players in different verses cannot see each other, damage
    /// each other's creatures or appear in each other's player lists, so "more players" can only
    /// ever mean "more players of this verse" - scaling on the server population would hand a
    /// solo runner a twelve-player wave because strangers in other verses happen to be standing
    /// on the same coordinates. There is no hub verse yet, so in practice the multiplier fires
    /// for a real party, which is exactly who it should fire for.</para>
    ///
    /// <para><b>Creatures are tagged to the verse at creation, explicitly.</b> Anything the
    /// server creates is untagged and therefore shared by every verse, and a shared creature is
    /// owned by whoever stands nearest and invulnerable to everyone else - the statue bug
    /// verse-design.md's spike 7 is about. Note there is deliberately no inherit-from-owner
    /// helper in <see cref="ZdoVerse"/>; the verse is known here because the run knows it.</para>
    ///
    /// <para><b>They are persistent, which is not a preference.</b>
    /// <c>ZDOMan.ReleaseNearbyZDOS</c> only ever reassigns a <i>persistent</i> ZDO, and
    /// ownership is what makes a client simulate a creature's AI at all - the server holds no
    /// GameObjects out where the players are. A non-persistent creature would therefore stand
    /// inert for ever. The cost is that a crash mid-run leaves creatures in the world, which is
    /// what <see cref="CleanupOrphans"/> is for.</para>
    /// </summary>
    internal static class Arena
    {
        /// <summary>Marks a ZDO as ours, so a crashed run can be cleaned up on the next boot.</summary>
        private static readonly int MadeByArena = "verse.arena".GetStableHashCode();

        /// <summary>How long an unstarted run lingers before it is forgotten, in seconds.</summary>
        private const float GatherTimeout = 900f;
        private const float CountdownSeconds = 12f;
        private const float BetweenWaves = 8f;
        private const float BoundsSlack = 8f;
        private const float GateOffset = 10f;

        /// <summary>
        /// How far past the wall a fighter may be before they count as having left the ring.
        ///
        /// <para>Deliberately not <see cref="BoundsSlack"/>, which it used to be, and twice as
        /// wide: the two numbers are answering different questions. The guard's slack decides
        /// whether to destroy an object and should be tight; this one decides whether to end
        /// somebody's run, and at 8 m it was ending them for standing a step outside their own
        /// gate - the chests are at Radius + GateOffset, which this now comfortably covers.
        /// Raising the guard's number instead would have put the gate inside the cull radius
        /// and started eating gear a player set down beside their chest.</para>
        /// </summary>
        private const float StraySlack = 16f;

        /// <summary>
        /// How far beyond the ring death markers are cleared from. Wider than the ring on
        /// purpose: the gates sit at radius + GateOffset, so a grave left at a chest was
        /// outside the bounds check that cleared the ring itself and simply stayed there.
        /// </summary>
        private const float GraveSlack = GateOffset + 20f;

        /// <summary>A fighter is only out of bounds once they have been out for this long.</summary>
        private const float StrayGrace = 3f;

        private enum Phase { Gathering, Countdown, Between, Fighting, Done }

        private class Fighter
        {
            internal long Peer;
            internal string Account;
            internal string Name;
            internal long PlayerId;
            internal bool Ready;
            internal bool Out;
            internal float StrayedAt;
        }

        private class Run
        {
            internal int Verse;
            internal Phase Phase = Phase.Gathering;
            internal float At;
            internal int Wave;
            internal float Started;
            internal int Players = 1;
            internal int Announced = -1;
            internal readonly List<Fighter> Fighters = new List<Fighter>();
            internal readonly List<ZDOID> Spawned = new List<ZDOID>();
        }

        private static readonly Dictionary<int, Run> Runs = new Dictionary<int, Run>();
        /// <summary>Created inside a ring since the last tick, awaiting classification.</summary>
        private static readonly List<ZDOID> Candidates = new List<ZDOID>();
        private static readonly List<int> Finished = new List<int>();

        /// <summary>How often Rested is topped up, in seconds. Its floor is 240 s.</summary>
        private const float RestEvery = 60f;
        private static float _restIn;

        private static System.Reflection.MethodInfo _destroy;
        private static readonly object[] DestroyArgs = new object[1];

        private static string Word =>
            string.IsNullOrWhiteSpace(VersePlugin.ArenaCommandWord.Value)
                ? "!arena"
                : VersePlugin.ArenaCommandWord.Value.Trim();

        internal static bool Match(string text, out string rest) =>
            CommandText.Match(Word, text, out rest);

        internal static string Resolve()
        {
            _destroy = _destroy ?? AccessTools.Method(typeof(ZDOMan), "DestroyZDO", new[] { typeof(ZDO) });
            return _destroy == null ? "ZDOMan.DestroyZDO(ZDO) not found (arena)" : Loadout.Resolve();
        }

        // --- what the guard needs to know --------------------------------------------------

        /// <summary>
        /// Whether a point is inside a ring with a run under way in it, and whose. Used by
        /// <see cref="ArenaGuard"/> to decide whether a newly created object is an intruder.
        /// </summary>
        internal static bool InAnyRun(Vector3 point, out int verse)
        {
            verse = Verses.None;
            if (!ArenaSite.Ready || Runs.Count == 0) return false;
            if (!ArenaSite.Inside(point, BoundsSlack)) return false;

            // Every ring is at the same coordinates, so the position alone cannot say which
            // verse; any active run is enough for the guard, which decides by authorship.
            foreach (KeyValuePair<int, Run> entry in Runs)
            {
                if (entry.Value.Phase == Phase.Gathering) continue;
                verse = entry.Key;
                return true;
            }
            return false;
        }

        /// <summary>Whether this object is one the arena made, and so not an intruder.</summary>
        internal static bool Ours(ZDO zdo) => zdo != null && zdo.GetInt(MadeByArena, 0) == 1;

        /// <summary>
        /// Whether a fighter standing here still counts as being at the arena. The one test for
        /// it, so there is one number to argue about; internal so the self-test can assert that
        /// the gate, where a fighter's own chests are, falls inside it.
        /// </summary>
        internal static bool AtArena(Vector3 point) => ArenaSite.Inside(point, StraySlack);

        /// <summary>
        /// How far out the ground is kept bare, in metres: no trees, no bushes, no rocks, no
        /// ruins, no native wildlife.
        ///
        /// <para><b>A third radius, and the widest, because it answers a third question.</b>
        /// <see cref="ArenaSite.Radius"/> is where the wall stands and <see cref="StraySlack"/>
        /// is how far a fighter may wander; this is how much of the landscape is the venue. A fir
        /// ten metres outside the wall is in every shot, and when a wave smashes it the trunk
        /// lands on the floor - so the clearing is deliberately wider than anything the rules
        /// care about. Never narrower than the stray tolerance, or there would be scenery inside
        /// the circle a fighter is allowed to stand in.</para>
        /// </summary>
        internal static float ClearRadius =>
            Mathf.Max(ArenaSite.Radius + StraySlack, VersePlugin.ArenaClearRadius.Value);

        /// <summary>Whether a point is inside the cleared apron. See <see cref="ClearRadius"/>.</summary>
        internal static bool InClearing(Vector3 point) =>
            ArenaSite.Inside(point, ClearRadius - ArenaSite.Radius);

        /// <summary>How far out the gates sit, for the self-test's benefit.</summary>
        internal static float GateDistance => ArenaSite.Radius + GateOffset;

        /// <summary>
        /// Queued rather than judged on the spot. At the moment a ZDO is created its field set
        /// is still empty - <c>RPC_ZDOData</c> deserialises into it afterwards - so neither its
        /// prefab nor its position is readable yet. See <see cref="ArenaGuard"/>.
        /// </summary>
        internal static void Consider(ZDOID uid)
        {
            if (!Candidates.Contains(uid)) Candidates.Add(uid);
        }

        // --- the command -------------------------------------------------------------------

        internal static void Handle(long peer, string account, string rest)
        {
            string[] parts = CommandText.Words(rest);
            switch (CommandText.Verb(parts))
            {
                case "":
                case "enter":
                    Enter(peer, account);
                    return;

                case "join":
                case "ready":
                    Join(peer, account);
                    return;

                case "start":
                case "go":
                    Start(peer, account);
                    return;

                case "leave":
                case "out":
                case "quit":
                    Leave(peer, account);
                    return;

                case "watch":
                case "spectate":
                case "back":
                    Watch(peer, account);
                    return;

                case "site":
                    Site(peer, account);
                    return;

                case "stop":
                    Stop(peer, account);
                    return;

                case "purge":
                    Purge(peer, account);
                    return;

                case "help":
                    VerseIdentity.SayTo(peer, Help());
                    return;

                default:
                    VerseIdentity.SayTo(peer, $"I do not know that. Try {Word} help");
                    return;
            }
        }

        private static string[] Help() => new[]
        {
            $"{Word} - {ArenaRules.Waves} waves of monsters in a ring. You are lent a full set of Mistlands gear.",
            $"{Word}         takes you to the gate, and puts you in the next run if the kit is already on.",
            $"{Word} join    puts you in the next run. Do it once you have the kit on and your own things in your chest.",
            $"{Word} start   begins it, with everybody who has joined. Anyone who has joined can call it.",
            $"{Word} leave   drops you out again, before it starts.",
            $"{Word} watch   takes you back to the arena after you have fallen. Up the steps is a gallery with a view of the floor; you cannot get back onto it.",
            "WARNING: anything you carry in can be lost for good - your corpse is cleared away when a run starts. Use the chest with your name on it.",
            "Leaving the ring, or wearing anything that is not the kit, ends your run. The kit is yours to keep.",
        };

        private static void Enter(long peer, string account)
        {
            if (!VersePlugin.ArenaEnabled.Value)
            {
                VerseIdentity.SayTo(peer, "The arena is closed.");
                return;
            }

            int verse = Verses.Of(account);
            if (verse == Verses.None)
            {
                VerseIdentity.SayTo(peer, "You are not in a verse.");
                return;
            }

            if (!ArenaSite.Resolve(out Vector3 centre))
            {
                VerseIdentity.SayTo(peer,
                    "The arena has not found its ground yet - try again in a moment.");
                return;
            }

            if (Runs.TryGetValue(verse, out Run existing) && existing.Phase != Phase.Gathering)
            {
                VerseIdentity.SayTo(peer,
                    "A run is already under way in your verse. Type " + Word + " watch to go " +
                    "and watch it from the gallery.");
                return;
            }

            long playerId = PlayerIdOf(peer);
            if (playerId == 0L)
            {
                VerseIdentity.SayTo(peer, "I cannot tell who you are yet - try again in a moment.");
                return;
            }

            Run run = existing;
            if (run == null)
            {
                run = new Run { Verse = verse, At = 0f };
                Runs[verse] = run;
            }

            // Somebody typing the word is somebody still getting ready, so the idle clock that
            // discards a forgotten lobby starts again here. Without this the gather timeout ran
            // from the first entry: a player reading the warning, emptying their pockets and
            // sorting out the kit can easily spend longer than that at the gate, and would have
            // had the run - and now their place in it - dropped out from under them.
            run.At = 0f;

            Vector3 gate = Gate(centre, playerId);

            // Two sets of private chests: one to leave their own gear in, one holding the kit.
            // Both are stamped with their player id, so vanilla's own lock keeps everybody
            // else out - including the other eleven members of their verse.
            // Twelve metres apart, and marked as different kinds: the first live test found the
            // kit call reusing the deposit chests because three metres is inside the search
            // radius. The marker is the real fix; the distance is so a player can tell at a
            // glance which row is theirs to empty and which is theirs to raid.
            //
            // A wooden deck goes down under each row first, and the chests are stood on it
            // rather than on the terrain: a chest is a building piece, and one whose footprint
            // hangs over uneven ground loses its support and breaks, taking a player's deposit
            // with it. See ArenaDeck. If no deck can be laid the chests fall back to the
            // ground, which is where they were before and is better than no arena.
            // Both rows follow the arc at the gate's own radius, rather than the kit row sitting
            // twelve metres along world +z from the deposit row as it used to. That fixed
            // direction was always a little arbitrary; with the ring at 19.5 m it became wrong,
            // because for any gate on the far side of the circle it put the kit chests inside
            // the wall - on the fighting floor, where the waves spawn and a troll would
            // eventually flatten them. Along the tangent they stay beside their own gate, out of
            // the ring and inside the cleared ground, whichever way round the circle it is.
            const int depositSlots = 36;

            Vector3 outward = gate - centre;
            outward.y = 0f;
            outward = outward.sqrMagnitude > 0f ? outward.normalized : Vector3.right;

            Vector3 along = Vector3.Cross(Vector3.up, outward);
            Vector3 kitAt = gate + along * 12f;

            float depositSpan = Loadout.RowSpan(VersePlugin.ArenaChestPrefab.Value, depositSlots);
            float kitSpan = Loadout.RowSpan(VersePlugin.ArenaChestPrefab.Value,
                ArenaRules.Kit(ArenaSite.Cold).Length);

            ArenaDeck.Ensure(gate, along, depositSpan, out float depositY);
            ArenaDeck.Ensure(kitAt, along, kitSpan, out float kitY);

            List<ZDO> deposit = Loadout.Chests(VersePlugin.ArenaChestPrefab.Value, verse, playerId,
                peer, Loadout.Deposit, gate, along, depositSlots, depositY);
            List<ZDO> kit = Loadout.Chests(VersePlugin.ArenaChestPrefab.Value, verse, playerId,
                peer, Loadout.Kit, kitAt, along,
                ArenaRules.Kit(ArenaSite.Cold).Length, kitY);

            if (deposit.Count == 0 || kit.Count == 0)
            {
                VerseIdentity.SayTo(peer, "I could not set out the chests - the arena is broken, sorry.");
                return;
            }

            if (!Loadout.Fill(kit, VersePlugin.ArenaChestPrefab.Value, ArenaRules.Kit(ArenaSite.Cold),
                    out string problem))
                VersePlugin.Log.LogWarning($"arena kit for {account}: {problem}");

            // Asked again now the deck is down, so they land on the floor rather than under it:
            // Gate() lifts its answer onto the deck when it finds one, and on a first entry
            // there was no deck to find when it ran a few lines above.
            gate = Gate(centre, playerId);
            Teleport(peer, gate);

            int oldGraves = ClearGraves();
            if (oldGraves > 0)
                VersePlugin.Log.LogInfo(
                    $"arena: cleared {oldGraves} death marker(s) from the ring on arrival");

            // Logged because the alternative was silence: the first live test of this could not
            // be told apart from the player having typed some other command, since a successful
            // entry wrote nothing to the log at all.
            VersePlugin.Log.LogInfo(
                $"arena: {account} of verse {verse} sent to the gate at " +
                $"{gate.x:0}, {gate.y:0}, {gate.z:0} - {deposit.Count} deposit chest(s), " +
                $"{kit.Count} kit chest(s) holding {ArenaRules.Kit(ArenaSite.Cold).Length} stack(s); " +
                $"{run.Fighters.Count} at the gate, phase {run.Phase}");

            // Enrolled here as well as on `join`, when they can be - which is to say when the
            // kit is already on. Arriving at the gate and entering the fight are still two
            // decisions, and the kit check is what keeps them apart: nobody still wearing their
            // own things can be enrolled, so a player who only wanted to look is in no danger
            // of being swept into somebody else's start. What this does catch is the player who
            // has emptied their pockets, dressed in the loaner gear, and then typed the bare
            // word again instead of the subcommand - the commonest way a first run never
            // happens at all. `leave` undoes it, and costs nothing before a run starts.
            bool enrolled = Enroll(run, peer, account, out string _);

            // The warning is first and blunt, and it is the only protection anybody gets.
            // Nothing the player carries in is recoverable: their pockets are invisible to the
            // server, their corpse is cleared from the ring when they start a run, and a
            // tombstone cannot be told apart from a lost loaner sword. The chest is the answer,
            // so the chest is what the first two lines are about.
            VerseIdentity.SayTo(peer, new[]
            {
                $"The arena: {ArenaRules.Waves} waves, and they do not get easier.",
                "WARNING - anything you carry in here can be lost for good. Your corpse is " +
                "cleared away when you start a run, and nobody can get it back for you.",
                "Put EVERYTHING you own in the chest with your name on it. It opens for you and " +
                "for nobody else, and it will still be there when you are done.",
                "The far chest holds the kit - full Carapace armour, three Mistwalkers, food " +
                "and mead. It is yours to keep, so there is nothing to be careful with.",
                "Take it, wear it, and eat all three foods, or wave one will kill you.",
                enrolled
                    ? $"The kit is on, so you are in already - type {Word} start when you are " +
                      $"ready, or {Word} leave to step back out."
                    : $"Then type {Word} join, and {Word} start when everybody is in.",
            });

            // Said to the run rather than only to them, so whoever is about to call start can
            // see who has turned up and is in. Pointless for a solo runner, whose own line
            // above has just told them, so it is only sent when there is somebody else to hear.
            if (enrolled && run.Fighters.Count > 1)
                Say(run, $"{NameOf(peer)} is at the gate, kitted and in. {run.Fighters.Count} in.");
        }

        /// <summary>
        /// Joins the run being gathered in this verse. Joining *is* being ready - there is no
        /// separate confirmation, because the previous flow had one and it was a trap: an
        /// auto-start on a sixty-second timer silently dropped anybody whose "ready" had been
        /// refused, so a run two people were standing at could begin with one of them in it.
        /// Nothing starts on a timer now; somebody has to say so.
        /// </summary>
        private static void Join(long peer, string account)
        {
            int verse = Verses.Of(account);
            if (!Runs.TryGetValue(verse, out Run run))
            {
                VerseIdentity.SayTo(peer, $"Nobody is at the arena. Type {Word} to go there.");
                return;
            }

            if (run.Phase != Phase.Gathering)
            {
                VerseIdentity.SayTo(peer, "A run is already under way - wait for it to finish.");
                return;
            }

            if (!Enroll(run, peer, account, out string offending))
            {
                VerseIdentity.SayTo(peer,
                    $"You are still wearing your own {offending}. Put it in your chest and take the kit's, then {Word} join again.");
                return;
            }

            Say(run, $"{NameOf(peer)} joined. {run.Fighters.Count} in. Type {Word} start when everybody is ready.");
        }

        /// <summary>
        /// Puts a player in the run being gathered in their verse, or reports what is stopping
        /// it. Shared by <see cref="Join"/> and by <see cref="Enter"/>'s auto-join, and silent
        /// on purpose: the two callers have different things to say about the same outcome, and
        /// Enter's is the last line of a block that has to stay in one piece.
        /// </summary>
        private static bool Enroll(Run run, long peer, string account, out string offending)
        {
            offending = null;

            // Checked at the door rather than mid-run on purpose: being thrown out of wave four
            // over a helmet is a worse experience than being told before you start. It is also
            // the only thing that makes Enter's auto-join safe - see the note there.
            if (VersePlugin.ArenaEnforceKit.Value && !KitOnly(peer, out offending)) return false;

            Fighter fighter = Find(run, peer);
            if (fighter == null)
            {
                fighter = new Fighter
                {
                    Peer = peer,
                    Account = account,
                    Name = NameOf(peer),
                    PlayerId = PlayerIdOf(peer),
                };
                run.Fighters.Add(fighter);
            }

            fighter.Ready = true;
            fighter.Out = false;
            return true;
        }

        /// <summary>
        /// Starts the run, taking everybody who has joined. Any of them may call it - waiting
        /// for one nominated person to do it is a worse failure than starting a second early.
        /// </summary>
        private static void Start(long peer, string account)
        {
            int verse = Verses.Of(account);
            if (!Runs.TryGetValue(verse, out Run run))
            {
                VerseIdentity.SayTo(peer, $"Nobody is at the arena. Type {Word} to go there.");
                return;
            }

            if (run.Phase != Phase.Gathering)
            {
                VerseIdentity.SayTo(peer, "It has already started.");
                return;
            }

            if (Find(run, peer) == null)
            {
                VerseIdentity.SayTo(peer, $"Join first: {Word} join.");
                return;
            }

            // Anybody who wandered off and is no longer connected is not coming.
            for (int i = run.Fighters.Count - 1; i >= 0; i--)
                if (ZNet.instance.GetPeer(run.Fighters[i].Peer) == null) run.Fighters.RemoveAt(i);

            if (run.Fighters.Count == 0)
            {
                Runs.Remove(verse);
                return;
            }

            Begin(run);
        }

        private static void Leave(long peer, string account)
        {
            int verse = Verses.Of(account);
            if (!Runs.TryGetValue(verse, out Run run)) return;

            Fighter fighter = Find(run, peer);
            if (fighter == null) return;

            if (run.Phase == Phase.Gathering)
            {
                run.Fighters.Remove(fighter);
                if (run.Fighters.Count == 0) Runs.Remove(verse);
                VerseIdentity.SayTo(peer, "Left the arena. Your things are still in your chest.");
                return;
            }

            fighter.Out = true;
            VerseIdentity.SayTo(peer, "You walked out mid-run. That counts as falling.");
        }

        /// <summary>
        /// Takes somebody back to the arena to watch, and nothing else.
        ///
        /// <para><b>Why this is a different command from <c>enter</c>.</b> Dying in a run used
        /// to be the end of the evening: the body is cleared when the next run starts, the gear
        /// is in a chest at a gate kilometres from anywhere, and <c>enter</c> refuses while a
        /// run is under way - which it has to, because a dead fighter rejoining the fight they
        /// just lost is not a fight. So the way back is its own word, and it goes to the
        /// outside: the bottom of the steps on the boardwalk, with the gallery above it. Nothing
        /// here puts anybody in a run, marks anybody ready, or so much as looks at the run
        /// table.</para>
        ///
        /// <para><b>"Cannot enter" is geometry, not a rule.</b> The ring has no door - fighters
        /// are teleported onto the floor at the end of the countdown - so the only way in from
        /// the gallery would be over the inner edge of the wall, and
        /// <see cref="ArenaStand"/>'s iron railing is there precisely so that drop does not
        /// exist. A check in this method would be a check somebody could walk around; a cage
        /// wall is not.</para>
        /// </summary>
        private static void Watch(long peer, string account)
        {
            if (!VersePlugin.ArenaEnabled.Value)
            {
                VerseIdentity.SayTo(peer, "The arena is closed.");
                return;
            }

            if (Verses.Of(account) == Verses.None)
            {
                VerseIdentity.SayTo(peer, "You are not in a verse.");
                return;
            }

            if (!ArenaSite.Resolve(out Vector3 centre))
            {
                VerseIdentity.SayTo(peer,
                    "The arena has not found its ground yet - try again in a moment.");
                return;
            }

            if (!VersePlugin.ArenaGallery.Value)
            {
                // Still worth the trip: the gate and the chests are out here either way.
                VerseIdentity.SayTo(peer, "There is no gallery at the arena - taking you to the gate.");
            }

            Teleport(peer, ArenaStand.StairFoot(centre));
            Rest(peer);
            Warm(peer);

            VerseIdentity.SayTo(peer, VersePlugin.ArenaGallery.Value
                ? "Outside the wall, at the bottom of the steps. Walk up to the gallery - you " +
                  "can watch through the railing, but you cannot get back in."
                : "Outside the wall. Your things are in the chest with your name on it.");
        }

        private static void Site(long peer, string account)
        {
            if (!Verses.IsServerAdmin(account))
            {
                VerseIdentity.SayTo(peer, "Admins only.");
                return;
            }

            if (!ArenaSite.Resolve(out Vector3 centre))
            {
                VerseIdentity.SayTo(peer, "No site found yet - see the server log.");
                return;
            }

            VerseIdentity.SayTo(peer,
                $"Arena at {centre.x:0}, {centre.y:0}, {centre.z:0}, radius {ArenaSite.Radius:0} m" +
                (ArenaSite.Cold ? ", and it freezes." : "."));
        }

        /// <summary>
        /// Admin tool: clear away ring pieces left at a site the arena has moved away from.
        /// Reports what it found rather than acting silently, because it deletes world objects.
        /// </summary>
        private static void Purge(long peer, string account)
        {
            if (!Verses.IsServerAdmin(account))
            {
                VerseIdentity.SayTo(peer, "Admins only.");
                return;
            }

            if (!ArenaSite.Resolve(out Vector3 _))
            {
                VerseIdentity.SayTo(peer, "The arena has no site yet, so nothing is a stray.");
                return;
            }

            int removed = ArenaRing.PurgeStrays(out int kept, out float furthest);

            // Graves too. The command was only ever about stray walls, which is not what an
            // operator typing "purge" in an arena full of headstones expects of it.
            int graves = ClearGraves();

            string line =
                (removed == 0
                    ? $"No stray ring pieces; {kept} standing at the current site. "
                    : $"Removed {removed} stray ring piece(s), the furthest {furthest:0} m away; " +
                      $"{kept} left standing at the current site. ") +
                (graves == 0
                    ? "No death markers to clear."
                    : $"Cleared {graves} death marker(s).");

            VerseIdentity.SayTo(peer, line);
            VersePlugin.Log.LogInfo("arena purge: " + line);
        }

        private static void Stop(long peer, string account)
        {
            if (!Verses.IsServerAdmin(account))
            {
                VerseIdentity.SayTo(peer, "Admins only.");
                return;
            }

            int verse = Verses.Of(account);
            if (!Runs.TryGetValue(verse, out Run run))
            {
                VerseIdentity.SayTo(peer, "No run in your verse.");
                return;
            }

            Finish(run, false, true);
            VerseIdentity.SayTo(peer, "Stopped.");
        }

        // --- the run -----------------------------------------------------------------------

        /// <summary>
        /// Clears the ring of everything that is not part of the run: intruders that walked or
        /// flew in, litter dropped on the floor, and scenery that streamed in late.
        ///
        /// <para><b>Called at the two edges of a wave, not on a timer.</b> It used to run once a
        /// second for as long as a run was live, and both halves of it are a pass over the
        /// object table - tens of thousands of entries, twice a second between them, on the
        /// server's main thread. That is what players were feeling as lag. A wave's own
        /// creatures are tagged and never candidates either way, so the only thing the edges
        /// miss is a wanderer that arrives mid-wave: it now lives until the wave is down instead
        /// of for up to a second.</para>
        /// </summary>
        private static void SweepRing(string when)
        {
            int intruders = ArenaGuard.Sweep();
            int scenery = ArenaRing.Clear();

            if ((intruders > 0 || scenery > 0) && VersePlugin.ArenaTrace.Value)
                VersePlugin.Log.LogInfo(
                    $"arena: swept the ring {when} - {intruders} intruder(s), " +
                    $"{scenery} piece(s) of scenery");
        }

        private static void Begin(Run run)
        {
            run.Phase = Phase.Countdown;
            run.At = 0f;
            run.Announced = -1;

            int live = 0;
            foreach (Fighter f in run.Fighters) if (!f.Out) live++;
            run.Players = Mathf.Max(1, live);

            // A fresh ring for every run, torn down and raised again rather than repaired.
            // Repair only un-hides pieces this verse broke; a piece destroyed in the shared world
            // stays gone, and the wall has been quietly losing segments between restarts.
            //
            // At the start of the countdown and not at the teleport, so the twelve seconds are
            // spent streaming a couple of hundred new pieces out to the clients rather than the
            // first wave doing it. Skipped while another verse is actually fighting: the ring is
            // one shared structure, and taking the walls out from around somebody else's live
            // wave to freshen them for this one is not a trade worth making.
            if (Busy(run.Verse))
                VersePlugin.Log.LogInfo(
                    "arena: left the ring alone - another verse is mid-run in it");
            else
                ArenaRing.Rebuild();

            Say(run, $"{ArenaRules.Waves} waves, {run.Players} of you. Starting in {(int)CountdownSeconds} seconds.");
        }

        /// <summary>
        /// Whether a verse other than <paramref name="except"/> is mid-run, and so would notice
        /// the shared ring being rebuilt around it.
        /// </summary>
        private static bool Busy(int except)
        {
            foreach (KeyValuePair<int, Run> entry in Runs)
            {
                if (entry.Key == except) continue;
                if (entry.Value.Phase == Phase.Fighting || entry.Value.Phase == Phase.Between ||
                    entry.Value.Phase == Phase.Countdown)
                    return true;
            }

            return false;
        }

        internal static void Tick(float dt)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            ReapCulled();

            _restIn -= dt;
            if (_restIn <= 0f)
            {
                _restIn = RestEvery;
                foreach (KeyValuePair<int, Run> entry in Runs)
                {
                    if (entry.Value.Phase != Phase.Fighting && entry.Value.Phase != Phase.Between) continue;
                    foreach (Fighter f in entry.Value.Fighters)
                        if (!f.Out) Rest(f.Peer);
                }

                // And everybody at the venue who is not fighting - the gallery, the gates - so a
                // spectator in the Deep North is watching rather than freezing.
                Watchers();
            }

            if (Runs.Count == 0) return;

            Finished.Clear();
            foreach (KeyValuePair<int, Run> entry in Runs)
            {
                Run run = entry.Value;
                run.At += dt;

                switch (run.Phase)
                {
                    case Phase.Gathering:
                        Gather(run);
                        break;

                    case Phase.Countdown:
                        Countdown(run);
                        break;

                    case Phase.Between:
                        if (run.At >= BetweenWaves) NextWave(run);
                        break;

                    case Phase.Fighting:
                        Fight(run);
                        break;
                }

                if (run.Phase == Phase.Done) Finished.Add(entry.Key);
            }

            foreach (int verse in Finished) Runs.Remove(verse);
        }

        /// <summary>
        /// Keeps a gathering run tidy while it waits. It no longer starts anything on a timer:
        /// an auto-start used to fire after sixty seconds and drop everybody who was not marked
        /// ready, so a run two people were standing at could begin with one. Starting is
        /// somebody typing <c>start</c> now, and nothing else.
        /// </summary>
        private static void Gather(Run run)
        {
            // Anyone who disconnected while waiting is simply no longer waiting.
            for (int i = run.Fighters.Count - 1; i >= 0; i--)
                if (ZNet.instance.GetPeer(run.Fighters[i].Peer) == null)
                    run.Fighters.RemoveAt(i);

            // An unstarted run is discarded once nobody has been near it for a long while, so a
            // forgotten lobby does not sit in the table for the rest of the server's life. Long
            // enough that it cannot surprise anybody who is still getting ready.
            if (run.At >= GatherTimeout) run.Phase = Phase.Done;
        }

        private static void Countdown(Run run)
        {
            int left = Mathf.CeilToInt(CountdownSeconds - run.At);
            if (left != run.Announced && (left == 10 || left == 5 || left <= 3) && left > 0)
            {
                run.Announced = left;
                Say(run, left == 10 ? "Ten seconds. Eat now if you have not." : left.ToString());
            }

            if (run.At < CountdownSeconds) return;

            if (!ArenaSite.Resolve(out Vector3 centre))
            {
                run.Phase = Phase.Done;
                return;
            }

            // Spread around the middle so a party does not start inside one another.
            int n = 0;
            foreach (Fighter f in run.Fighters)
            {
                if (f.Out) continue;
                float angle = run.Fighters.Count <= 1 ? 0f : n * Mathf.PI * 2f / run.Fighters.Count;
                float r = run.Fighters.Count <= 1 ? 0f : 3f;
                var at = new Vector3(centre.x + Mathf.Cos(angle) * r, 0f, centre.z + Mathf.Sin(angle) * r);
                at.y = ArenaSite.HeightAt(at.x, at.z) + 0.5f;
                Teleport(f.Peer, at);
                Rest(f.Peer);

                n++;
            }

            // A wall a troll put through last run is masked for this verse, not destroyed, so
            // the ring is restored before the first wave rather than fought in broken.
            ArenaRing.Repair(run.Verse);

            // Cleared again here, not just when the ring was built. The arena is kilometres
            // from anywhere, so at boot its zone has never been generated and its bushes and
            // rocks do not exist as objects yet - the first build reported "cleared 0" for
            // exactly that reason. They are created by the ghost-zone pass once somebody is
            // near, which is now.
            int scenery = ArenaRing.Clear();
            if (scenery > 0)
                VersePlugin.Log.LogInfo($"arena: cleared {scenery} piece(s) of scenery out of the ring");

            int graves = ClearGraves();
            if (graves > 0)
                VersePlugin.Log.LogInfo($"arena: cleared {graves} death marker(s) from the ring");

            run.Started = Time.time;
            run.Wave = 0;
            run.Phase = Phase.Between;
            run.At = BetweenWaves - 3f;      // a beat to land, then wave one
            Metrics.ArenaRuns++;
        }

        private static void NextWave(Run run)
        {
            run.Wave++;
            run.At = 0f;

            if (run.Wave > ArenaRules.Waves)
            {
                Finish(run, true, false);
                return;
            }

            if (!ArenaSite.Resolve(out Vector3 centre))
            {
                Finish(run, false, true);
                return;
            }

            // Before the wave arrives, so it lands on a clean floor. The clear in Countdown is
            // never the last word: a run begins the instant the player is teleported in, when
            // the zone is still streaming, and that first clear once caught 56 pieces with the
            // rest of the scenery appearing afterwards - to be smashed by the wave and leave the
            // floor covered in wood and stone.
            SweepRing($"before wave {run.Wave}");

            ArenaGroup[] groups = ArenaRules.Scale(run.Wave, run.Players);
            float ring = ArenaSite.Radius * 0.85f;
            int made = 0, index = 0, total = ArenaRules.Size(run.Wave, run.Players);

            foreach (ArenaGroup group in groups)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    // Spread around the rim rather than piled on one spot: it films better and
                    // it stops a wave arriving as a single wall of bodies.
                    float angle = total <= 1 ? 0f : index * Mathf.PI * 2f / total;
                    index++;

                    var at = new Vector3(centre.x + Mathf.Cos(angle) * ring, 0f,
                                         centre.z + Mathf.Sin(angle) * ring);
                    at.y = ArenaSite.HeightAt(at.x, at.z) + 0.2f;

                    ZDO zdo = SpawnCreature(group.Prefab, group.Level, at, centre, run.Verse);
                    if (zdo == null) continue;

                    run.Spawned.Add(zdo.m_uid);
                    made++;
                }
            }

            Metrics.ArenaSpawned += made;
            run.Phase = Phase.Fighting;

            Say(run, ArenaRules.Describe(run.Wave, run.Players));
            VersePlugin.Log.LogInfo(
                $"arena: verse {run.Verse} wave {run.Wave}/{ArenaRules.Waves}, " +
                $"{made} creature(s) for {run.Players} player(s)");
        }

        /// <summary>
        /// One creature, written straight into the object table. See the class comment for why
        /// it is tagged, why it is persistent, and why nothing simulates it for the first second
        /// or two.
        /// </summary>
        private static ZDO SpawnCreature(string prefabName, int level, Vector3 at, Vector3 facing, int verse)
        {
            int hash = prefabName.GetStableHashCode();
            if (ZNetScene.instance?.GetPrefab(hash) == null)
            {
                VersePlugin.Log.LogWarning($"arena: no prefab called '{prefabName}' on this server");
                return null;
            }

            Authorship.Suspend();
            try
            {
                ZDO zdo = ZDOMan.instance.CreateNewZDO(at, hash);
                zdo.SetPrefab(hash);
                zdo.SetRotation(Quaternion.LookRotation(
                    new Vector3(facing.x - at.x, 0f, facing.z - at.z).normalized));
                zdo.Persistent = true;

                ZdoVerse.Set(zdo, verse);
                zdo.Set(MadeByArena, 1, okForNotOwner: true);
                if (level > 1) zdo.Set(ZDOVars.s_level, level);

                return zdo;
            }
            finally
            {
                Authorship.Resume();
            }
        }

        private static void Fight(Run run)
        {
            // A creature is dead when its object is gone: Character.OnDeath runs on the client
            // that owns it and destroys the ZDO, which reaches us as a destroy. Reading health
            // would be less reliable, because the field is only written once somebody has
            // actually hit it.
            for (int i = run.Spawned.Count - 1; i >= 0; i--)
                if (ZDOMan.instance.GetZDO(run.Spawned[i]) == null)
                    run.Spawned.RemoveAt(i);

            int standing = 0;
            float now = Time.time;

            foreach (Fighter f in run.Fighters)
            {
                if (f.Out) continue;

                ZNetPeer peer = ZNet.instance.GetPeer(f.Peer);
                if (peer == null) { f.Out = true; continue; }

                ZDO body = peer.m_characterID != ZDOID.None
                    ? ZDOMan.instance.GetZDO(peer.m_characterID)
                    : null;

                if (body == null)
                {
                    f.Out = true;
                    VerseIdentity.SayTo(f.Peer,
                        "You fell. Your things are in your chest at the gate. Type " + Word +
                        " watch to come back and see the rest of it.");
                    continue;
                }

                if (body.GetFloat(ZDOVars.s_health, 1f) <= 0f)
                {
                    f.Out = true;
                    VerseIdentity.SayTo(f.Peer,
                        "You fell. Your things are in your chest at the gate. Type " + Word +
                        " watch to come back and see the rest of it.");
                    continue;
                }

                // Out of the ring. Given a few seconds' grace, because a knockback can throw
                // somebody over the wall through no fault of their own.
                if (!AtArena(body.GetPosition()))
                {
                    if (f.StrayedAt == 0f) f.StrayedAt = now;
                    else if (now - f.StrayedAt > StrayGrace)
                    {
                        f.Out = true;
                        VerseIdentity.SayTo(f.Peer, "You left the ring. Your run is over.");
                        continue;
                    }
                }
                else
                {
                    f.StrayedAt = 0f;
                }

                if (VersePlugin.ArenaEnforceKit.Value && !KitOnly(f.Peer, out string offending))
                {
                    f.Out = true;
                    VerseIdentity.SayTo(f.Peer, $"That {offending} is not the kit's. Your run is over.");
                    continue;
                }

                standing++;
            }

            if (standing == 0)
            {
                Finish(run, false, false);
                return;
            }

            if (run.Spawned.Count == 0)
            {
                run.Phase = Phase.Between;
                run.At = 0f;

                // The wave is down, so the floor is swept while nobody is fighting on it: a
                // wave's worth of dropped loot, and anything that wandered in while it was
                // being fought, goes here.
                SweepRing($"after wave {run.Wave}");

                Say(run, run.Wave >= ArenaRules.Waves
                    ? "Clear. That was the last of them."
                    : $"Wave {run.Wave} down. Next in {(int)BetweenWaves}.");
            }
        }

        private static void Finish(Run run, bool cleared, bool quiet)
        {
            float seconds = run.Started > 0f ? Time.time - run.Started : 0f;

            // Anything still standing goes, so the ring is empty for the next run and a
            // half-finished wave is not left wandering the Deep North.
            foreach (ZDOID uid in run.Spawned)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(uid);
                if (zdo != null) Destroy(zdo);
            }
            Metrics.ArenaCulled += run.Spawned.Count;
            run.Spawned.Clear();

            // And the last wave's leftovers, so the ring is left as the next run will find it.
            SweepRing("at the end of the run");

            if (!quiet)
            {
                string who = Who(run);
                string line = ArenaRules.Result(who, Mathf.Max(1, run.Wave), cleared, seconds, run.Players);

                // Broadcast rather than said to the verse: with no leaderboard this line is the
                // entire reward, and the point of it is that the rest of the server sees it.
                Announce(line);
                VersePlugin.Log.LogInfo("arena: " + line);
            }

            // Back to the gate, not to spawn: their own gear is in a chest there, and sending
            // them home would mean walking back across the world for it.
            if (ArenaSite.Resolve(out Vector3 centre))
            {
                foreach (Fighter f in run.Fighters)
                {
                    ZNetPeer peer = ZNet.instance.GetPeer(f.Peer);
                    if (peer == null) continue;

                    if (peer.m_characterID == ZDOID.None ||
                        ZDOMan.instance.GetZDO(peer.m_characterID) == null)
                    {
                        VerseIdentity.SayTo(f.Peer,
                            $"Your things are in your chest at the gate - type {Word} to go back for them.");
                        continue;
                    }

                    Teleport(f.Peer, Gate(centre, f.PlayerId));
                    VerseIdentity.SayTo(f.Peer,
                        "Back at the gate. Your own things are in your chest; the kit is yours to keep. " +
                        "Use !warp spawn to go home.");
                }
            }

            run.Phase = Phase.Done;
        }

        /// <summary>
        /// Destroys creatures left behind by a run that never finished - a crash, a kill -9, a
        /// deploy mid-wave. They are persistent by necessity, so without this they stay in the
        /// world for ever, and the next person to walk in meets wave seven.
        /// </summary>
        internal static int CleanupOrphans()
        {
            if (ZDOMan.instance == null) return 0;

            var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID")
                ?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
            if (byId == null) return 0;

            var doomed = new List<ZDO>();
            foreach (ZDO zdo in byId.Values)
            {
                if (!Ours(zdo)) continue;

                // Creatures only, which is what this method is for and what its log line has
                // always claimed. The same marker is on the gate chests - Loadout stamps them
                // so the ring's occupancy check does not mistake the arena's own furniture for
                // somebody's base - and a chest at the gate holds a player's entire inventory
                // between sessions. Taking those out on every boot would be the most expensive
                // thing in this plugin, and nothing but the breadth of this loop asked for it.
                GameObject prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                if (prefab == null || prefab.GetComponent<Character>() == null) continue;

                doomed.Add(zdo);
            }

            foreach (ZDO zdo in doomed) Destroy(zdo);

            if (doomed.Count > 0)
                VersePlugin.Log.LogInfo(
                    $"arena: removed {doomed.Count} creature(s) left over from a run that did not finish");

            return doomed.Count;
        }

        // --- helpers ------------------------------------------------------------------------

        /// <summary>
        /// Where a player's chests and the way in sit: just outside the ring, at an angle
        /// derived from their own id so two members of a verse do not share a spot.
        /// </summary>
        private static Vector3 Gate(Vector3 centre, long playerId)
        {
            float angle = (playerId % 360L) * Mathf.Deg2Rad;
            float r = ArenaSite.Radius + GateOffset;
            var at = new Vector3(centre.x + Mathf.Cos(angle) * r, 0f, centre.z + Mathf.Sin(angle) * r);
            at.y = ArenaSite.HeightAt(at.x, at.z) + 0.5f;

            // On top of the deck rather than at terrain height, where there is a deck. It is
            // laid level with the highest ground under it, so on a slope aiming at the terrain
            // would land the player inside the floor.
            if (ArenaDeck.TopAt(at, out float deck) && deck + 0.5f > at.y) at.y = deck + 0.5f;

            return at;
        }

        /// <summary>
        /// Whether everything this player is wearing or holding came out of the kit.
        ///
        /// <para>Equipped items are ZDO fields - that is how one client renders another's gear -
        /// so this much the server really can see. Their backpack, their food and their armour's
        /// upgrade level it cannot, and no amount of effort here will change that; see
        /// docs/arena-design.md. The rule is therefore "nothing of your own in a visible slot"
        /// rather than any pretence of a fair fight.</para>
        /// </summary>
        private static bool KitOnly(long peer, out string offending)
        {
            offending = null;

            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            if (connection == null || connection.m_characterID == ZDOID.None) return true;

            ZDO body = ZDOMan.instance?.GetZDO(connection.m_characterID);
            if (body == null) return true;

            var allowed = new HashSet<int> { 0 };
            foreach (KitItem item in ArenaRules.Kit(ArenaSite.Cold))
                allowed.Add(item.Prefab.GetStableHashCode());

            foreach (KeyValuePair<int, string> slot in Slots)
            {
                int worn = body.GetInt(slot.Key, 0);
                if (worn == 0 || allowed.Contains(worn)) continue;

                offending = slot.Value;
                return false;
            }

            return true;
        }

        /// <summary>The visible equipment slots, and what to call them in a chat line.</summary>
        private static readonly Dictionary<int, string> Slots = new Dictionary<int, string>
        {
            { ZDOVars.s_rightItem, "weapon" },
            { ZDOVars.s_leftItem, "shield" },
            { ZDOVars.s_chestItem, "chest armour" },
            { ZDOVars.s_legItem, "leg armour" },
            { ZDOVars.s_helmetItem, "helmet" },
            { ZDOVars.s_shoulderItem, "cape" },
            { ZDOVars.s_utilityItem, "trinket" },
        };

        private static Fighter Find(Run run, long peer)
        {
            foreach (Fighter f in run.Fighters) if (f.Peer == peer) return f;
            return null;
        }

        private static string Who(Run run)
        {
            var names = new List<string>();
            foreach (Fighter f in run.Fighters)
                if (!string.IsNullOrEmpty(f.Name)) names.Add(f.Name);

            if (names.Count == 0) return "Somebody";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return names[0] + " and " + names[1];
            return names[0] + " and " + (names.Count - 1) + " others";
        }

        private static string NameOf(long peer) =>
            ZNet.instance?.GetPeer(peer)?.m_playerName ?? "Somebody";

        private static long PlayerIdOf(long peer)
        {
            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            if (connection == null || connection.m_characterID == ZDOID.None) return 0L;

            ZDO zdo = ZDOMan.instance?.GetZDO(connection.m_characterID);
            return zdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
        }
        /// <summary>
        /// Removes every death marker inside the ring.
        ///
        /// <para>Deliberately not scoped to one owner. An earlier version cleared only the
        /// fighter's own graves, on the reasoning that a tombstone holds whatever was in
        /// somebody's pockets and the server cannot tell a lost loaner sword from a lost
        /// Mistlands one. That is still true - this is the only place the arena destroys
        /// something a player owns - but the operator's call is that the arena should simply be
        /// clear, and the gate message now warns in as many words that anything carried in can
        /// be lost for good. <see cref="ArenaGuard"/> still refuses to touch a tombstone
        /// anywhere else; this is the one deliberate exception.</para>
        ///
        /// <para>Run both when a player arrives at the gate and again when a run starts, so the
        /// ring is clear whether somebody is about to fight in it or just came to look.</para>
        /// </summary>
        private static int ClearGraves()
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null) return 0;

            var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID")
                ?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
            if (byId == null) return 0;

            var graves = new List<ZDO>();
            foreach (ZDO zdo in byId.Values)
            {
                if (!ArenaSite.Inside(zdo.GetPosition(), GraveSlack)) continue;

                GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab?.GetComponent<TombStone>() == null) continue;

                graves.Add(zdo);
            }

            foreach (ZDO zdo in graves) Destroy(zdo);
            return graves.Count;
        }

        private static void Rest(long peer)
        {
            if (!VersePlugin.ArenaRested.Value) return;

            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            ZDOID character = connection?.m_characterID ?? ZDOID.None;
            if (character == ZDOID.None) return;

            string effect = string.IsNullOrWhiteSpace(VersePlugin.ArenaRestedEffect.Value)
                ? "Rested"
                : VersePlugin.ArenaRestedEffect.Value.Trim();

            // nameHash, resetTime, itemLevel, skillLevel, variant - the order SEMan registers.
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, character, "RPC_AddStatusEffect",
                effect.GetStableHashCode(), true, 0, 0f, 0);
        }

        /// <summary>The mead whose own status effect keeps the weather off a spectator.</summary>
        private const string FrostMead = "MeadFrostResist";

        /// <summary>
        /// Keeps the cold off somebody at the venue who is not in a run.
        ///
        /// <para><b>Needed the moment <c>watch</c> existed.</b> The arena is sited in the Deep
        /// North by default, where the weather kills you - which the kit already answers for
        /// fighters, with two frost resistance meads
        /// (<see cref="ArenaRules.Kit"/>). A spectator arrives from their own bed with whatever
        /// they respawned in, usually nothing, so "come back and watch your friends finish"
        /// would have been an invitation to freeze to death on a wall. The effect is the mead's
        /// own, applied by the same routed RPC <see cref="Rest"/> uses, and topped up on the
        /// same clock.</para>
        /// </summary>
        private static void Warm(long peer)
        {
            if (!ArenaSite.Cold) return;

            string effect = Loadout.Effect(FrostMead);
            if (string.IsNullOrEmpty(effect)) return;

            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            ZDOID character = connection?.m_characterID ?? ZDOID.None;
            if (character == ZDOID.None) return;

            ZRoutedRpc.instance.InvokeRoutedRPC(peer, character, "RPC_AddStatusEffect",
                effect.GetStableHashCode(), true, 0, 0f, 0);
        }

        /// <summary>
        /// Tops up everybody standing at the venue who is not in a run: the gallery, the
        /// boardwalk, the gates. One pass over the connected peers on the Rested clock.
        /// </summary>
        private static void Watchers()
        {
            if (ZNet.instance == null || !ArenaSite.Ready) return;
            if (!VersePlugin.ArenaRested.Value && !ArenaSite.Cold) return;

            foreach (ZNetPeer connection in ZNet.instance.GetPeers())
            {
                if (connection == null || connection.m_characterID == ZDOID.None) continue;

                ZDO body = ZDOMan.instance.GetZDO(connection.m_characterID);
                if (body == null || !InClearing(body.GetPosition())) continue;

                // A fighter in a live run is already being looked after by the run itself, and
                // warming somebody who is out of bounds mid-fight is not this method's
                // business either way.
                bool fighting = false;
                foreach (KeyValuePair<int, Run> entry in Runs)
                {
                    Fighter f = Find(entry.Value, connection.m_uid);
                    if (f == null || f.Out) continue;

                    fighting = true;
                    break;
                }

                if (fighting) continue;

                Rest(connection.m_uid);
                Warm(connection.m_uid);
            }
        }

        private static void Teleport(long peer, Vector3 point)
        {
            ZNetPeer connection = ZNet.instance?.GetPeer(peer);
            ZDOID character = connection?.m_characterID ?? ZDOID.None;
            if (character == ZDOID.None) return;

            // Distant, so the client fades and loads the area itself. The arena is kilometres
            // from anywhere, and Firehose is what makes that arrival quick - see
            // docs/portal-sync.md.
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, character, "RPC_TeleportTo",
                point, Quaternion.identity, true);
        }

        private static void Say(Run run, string line)
        {
            foreach (Fighter f in run.Fighters) VerseIdentity.SayTo(f.Peer, line);
        }

        private static void Announce(string line)
        {
            if (ZNet.instance == null) return;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer != null && peer.IsReady())
                    VerseIdentity.SayTo(peer.m_uid, line);
        }

        /// <summary>
        /// Sorts out the objects clients created inside an active ring since the last tick: by
        /// now they have a prefab and a position, which is exactly what the creation hook did
        /// not. Anything <see cref="ArenaGuard"/> does not positively recognise is left alone.
        /// </summary>
        private static void ReapCulled()
        {
            if (Candidates.Count == 0) return;

            for (int i = 0; i < Candidates.Count; i++)
            {
                ZDO zdo = ZDOMan.instance?.GetZDO(Candidates[i]);
                if (zdo == null || Ours(zdo)) continue;
                if (!InAnyRun(zdo.GetPosition(), out int _)) continue;
                if (!ArenaGuard.Unwanted(zdo, out string why)) continue;

                Destroy(zdo);
                Metrics.ArenaCulled++;

                if (VersePlugin.ArenaTrace.Value)
                    VersePlugin.Log.LogInfo(
                        $"arena: removed {why} that appeared in the ring " +
                        $"({ZNetScene.instance?.GetPrefab(zdo.GetPrefab())?.name ?? "?"})");
            }

            Candidates.Clear();
        }

        /// <summary>Destroys a ZDO on the arena.s behalf. Public so <see cref="ArenaGuard"/> can.</summary>
        internal static void Remove(ZDO zdo)
        {
            Destroy(zdo);
            Metrics.ArenaCulled++;
        }

        /// <summary>
        /// Destroys a ZDO, taking ownership of it first.
        ///
        /// <para><b>The ownership claim is the whole method.</b> <c>ZDOMan.DestroyZDO</c> is
        /// <c>if (zdo.IsOwner()) m_destroySendList.Add(...)</c> - a silent no-op for anything
        /// the server does not own. And <c>ReleaseNearbyZDOS</c> hands every persistent ZDO in
        /// a player's active area to that player every two seconds, so essentially nothing in
        /// the ring is ever the server's: dropped loot, wandering wildlife, scenery, even the
        /// arena's own creatures once somebody stands near them. Without the claim, every cull
        /// this plugin makes does nothing, the swept objects come straight back, and the only
        /// evidence is a sweep that keeps finding the same things - which is how this was
        /// found, after a session of counting culls that never happened.</para>
        /// </summary>
        private static void Destroy(ZDO zdo)
        {
            if (zdo == null) return;

            _destroy = _destroy ?? AccessTools.Method(typeof(ZDOMan), "DestroyZDO", new[] { typeof(ZDO) });
            if (_destroy == null) return;

            zdo.SetOwner(ZDOMan.GetSessionID());

            DestroyArgs[0] = zdo;
            _destroy.Invoke(ZDOMan.instance, DestroyArgs);
        }
    }
}
