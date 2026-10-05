using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Verse
{
    /// <summary>How a verse may be entered.</summary>
    internal enum Access
    {
        /// <summary>Nobody but the members. The default a new verse is created with.</summary>
        Private,
        /// <summary>Anyone holding an invite from the leader.</summary>
        Invite,
        /// <summary>Anyone who can supply the password alongside the verse id.</summary>
        Password,
        /// <summary>Anyone at all.</summary>
        Open
    }

    /// <summary>One group and the world it plays in.</summary>
    internal class Record
    {
        public int Id;
        /// <summary>Platform user ID of the leader - the only account that may change any of this.</summary>
        public string Leader = "";
        /// <summary>Platform user IDs, leader included. Capped at <see cref="Verses.MaxParty"/>.</summary>
        public List<string> Members = new List<string>();
        public Access Access = Access.Private;
        /// <summary>Only meaningful for <see cref="Access.Password"/>. Stored as given; see the design note.</summary>
        public string Password = "";
        /// <summary>
        /// Progression global keys this verse has earned - `defeated_eikthyr` and the rest.
        /// Vanilla keeps one set for the whole world and broadcasts it to everybody, which
        /// would mean one party's boss kill unlocking every other party's spawn tables, raids
        /// and trader stock. See <see cref="Keys"/>. Server options and world modifiers are
        /// deliberately *not* here: those are the operator's and apply to every verse.
        /// </summary>
        public List<string> Keys = new List<string>();

        /// <summary>
        /// True on the one verse the existing world was migrated into. Doubles as the "already
        /// migrated" marker, so the migration cannot run twice without needing a new field in
        /// the file format.
        /// </summary>
        public bool Legacy;

        /// <summary>Platform user IDs the leader has invited but who have not joined yet.</summary>
        public List<string> Invites = new List<string>();

        /// <summary>
        /// Accounts that have already been shown the introduction. Kept per verse rather than
        /// globally because the registry is a list of these and there is nowhere else to put it;
        /// the only consequence is that somebody who changes verse hears it once more, which is
        /// arguably correct anyway - the world around them just changed.
        /// </summary>
        public List<string> Introduced = new List<string>();
        /// <summary>
        /// Where a player arriving in this verse is put down, as x/y/z. Null means "the
        /// world's start temple", which is where a verse starts life. The leader moves it with
        /// the spawn command so that an invited player lands at the base rather than the
        /// temple. See <see cref="Spawns"/> for why arriving needs a teleport at all.
        /// </summary>
        public float[] Spawn;
    }

    /// <summary>
    /// The registry: who is in which verse, persisted beside the world save.
    ///
    /// Indexed by platform user ID rather than by the peer's network UID, because a UID is
    /// assigned per connection and we specifically need an identity that survives the
    /// disconnect-and-rejoin that changing verse is built on. <c>ZNet.RPC_PeerInfo</c>
    /// verifies that ID against a Steam session ticket before a peer is ever accepted, so it
    /// is not something a client can claim for itself.
    ///
    /// Verse IDs are small incrementing integers because players have to be able to say one
    /// out loud and type it into chat.
    /// </summary>
    internal static class Verses
    {
        /// <summary>Verse 0 is "not placed yet" and never a real verse.</summary>
        internal const int None = 0;

        internal static int MaxParty => VersePlugin.MaxPartySize.Value;

        private static readonly Dictionary<int, Record> ById = new Dictionary<int, Record>();
        private static readonly Dictionary<string, int> ByMember = new Dictionary<string, int>();
        private static int _nextId = 1;
        private static string _path;
        private static bool _dirty;

        internal static int Count => ById.Count;

        internal static void Load(string path)
        {
            _path = path;
            ById.Clear();
            ByMember.Clear();
            _nextId = 1;

            try
            {
                if (File.Exists(_path))
                {
                    var saved = JsonConvert.DeserializeObject<List<Record>>(File.ReadAllText(_path));
                    if (saved != null)
                        foreach (Record r in saved) Adopt(r);
                }
            }
            catch (System.Exception e)
            {
                // A registry we cannot read is not a reason to refuse to boot, but it is a
                // reason to be loud: every player would silently land in a fresh verse and
                // their existing base would be invisible to them.
                VersePlugin.Log.LogError($"Could not read the verse registry at {_path}: {e.Message}. " +
                                         "Starting empty - MOVE THE FILE ASIDE AND INVESTIGATE before " +
                                         "letting players on, or they will be placed in new verses.");
            }

            VersePlugin.Log.LogInfo($"verse registry: {ById.Count} verse(s), next id {_nextId}");
        }

        private static void Adopt(Record r)
        {
            if (r == null || r.Id <= None) return;
            ById[r.Id] = r;
            if (r.Members == null) r.Members = new List<string>();
            if (r.Invites == null) r.Invites = new List<string>();
            foreach (string member in r.Members) ByMember[member] = r.Id;
            if (r.Id >= _nextId) _nextId = r.Id + 1;
        }

        internal static Record Get(int id) =>
            ById.TryGetValue(id, out Record r) ? r : null;

        /// <summary>The verse this account belongs to, or <see cref="None"/>.</summary>
        internal static int Of(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return None;
            return ByMember.TryGetValue(platformId, out int id) ? id : None;
        }

        /// <summary>
        /// The verse this account belongs to, creating one they lead if they have none. This
        /// is what makes a first-time player the leader of their own private verse simply by
        /// connecting.
        /// </summary>
        internal static int OfOrCreate(string platformId)
        {
            int existing = Of(platformId);
            if (existing != None) return existing;

            var r = new Record { Id = _nextId++, Leader = platformId, Access = Access.Private };
            r.Members.Add(platformId);
            ById[r.Id] = r;
            ByMember[platformId] = r.Id;
            _dirty = true;

            // A fresh verse is scattered away from wherever the older ones have been playing.
            // Only matters on a migrated world, where the shared landscape around the legacy
            // base has already been logged and mined - see Spawns.Scatter.
            Spawns.Scatter(r);

            // And gets its own, unused Guardian Power stones at the temple rather than none at
            // all - see BossStones for why a fresh verse would otherwise have none.
            BossStones.Seed(r.Id);

            VersePlugin.Log.LogInfo($"created verse {r.Id} for {platformId}");
            return r.Id;
        }

        /// <summary>
        /// Verses that still have members. An emptied verse is kept rather than deleted (its
        /// objects are still tagged with its id), so this is what "how many verses are there"
        /// means for anything that has to reason about all of them.
        /// </summary>
        internal static int LivingCount
        {
            get
            {
                int n = 0;
                foreach (Record r in ById.Values)
                    if (r.Members.Count > 0) n++;
                return n;
            }
        }

        internal static bool IsLeader(string account, Record r) =>
            r != null && !string.IsNullOrEmpty(account) && r.Leader == account;

        /// <summary>
        /// Moves an account into another verse, giving up its own. Returns null on success or
        /// a message to show the player.
        ///
        /// The caller is expected to disconnect them afterwards: a verse change takes effect
        /// on reconnect, because that is when a client rebuilds its view of the world from
        /// nothing (see docs/verse-design.md).
        /// </summary>
        /// <summary>
        /// Whether this account may go anywhere, ignoring access and the party cap. Server
        /// admins can: they are the people who need to walk into a verse to see a bug
        /// reported in it, and withholding that from somebody who already has the console and
        /// the world file would be security theatre. The admin list is vanilla's own
        /// (`adminlist.txt` beside the world save) and it re-reads itself every ten seconds,
        /// so adding somebody takes effect without a restart.
        /// </summary>
        internal static bool IsServerAdmin(string account) =>
            !string.IsNullOrEmpty(account) &&
            ZNet.instance != null &&
            ZNet.instance.IsAdmin(account);

        internal static string Join(string account, int id, string password)
        {
            Record target = Get(id);
            if (target == null) return $"There is no verse {id}.";

            int current = Of(account);
            if (current == id) return $"You are already in verse {id}.";

            bool admin = IsServerAdmin(account);

            if (!admin && target.Members.Count >= MaxParty)
                return $"Verse {id} is full ({MaxParty} players).";

            if (!admin)
            {
                switch (target.Access)
                {
                    case Access.Private:
                        return $"Verse {id} is private.";
                    case Access.Invite:
                        if (!target.Invites.Contains(account))
                            return $"Verse {id} is invite only, and you have not been invited.";
                        break;
                    case Access.Password:
                        if (string.IsNullOrEmpty(password))
                            return $"Verse {id} needs a password: !verse join {id} <password>";
                        if (target.Password != password)
                            return "That password is not right.";
                        break;
                }
            }

            Depart(account);

            target.Members.Add(account);
            target.Invites.Remove(account);
            ByMember[account] = id;
            _dirty = true;

            VersePlugin.Log.LogInfo(
                admin ? $"{account} joined verse {id} as a server admin, ignoring its access"
                      : $"{account} joined verse {id}");
            return null;
        }

        /// <summary>
        /// Takes an account out of whatever verse it is in and gives it a fresh private one.
        /// </summary>
        internal static int LeaveToNew(string account)
        {
            Depart(account);
            return OfOrCreate(account);
        }

        /// <summary>
        /// Removes an account from its verse, handing leadership on if it led one that still
        /// has members. A verse left with nobody in it is deliberately kept: its buildings are
        /// still tagged with its id, so deleting the record would orphan them irrecoverably
        /// rather than merely hide them.
        /// </summary>
        private static void Depart(string account)
        {
            int current = Of(account);
            if (current == None) return;

            Record old = Get(current);
            ByMember.Remove(account);
            _dirty = true;
            if (old == null) return;

            old.Members.Remove(account);

            if (old.Leader == account && old.Members.Count > 0)
            {
                old.Leader = old.Members[0];
                VersePlugin.Log.LogInfo($"verse {old.Id} is now led by {old.Leader}");
            }
            else if (old.Members.Count == 0)
            {
                VersePlugin.Log.LogInfo(
                    $"verse {old.Id} has no members left; its objects stay tagged and hidden");
            }
        }

        /// <summary>
        /// Moves where arrivals are put down in the caller's verse. Returns null on success or
        /// a message to show the player.
        /// </summary>
        internal static string SetSpawn(string account, UnityEngine.Vector3 point)
        {
            Record r = Get(Of(account));
            if (r == null) return "You are not in a verse.";
            if (!IsLeader(account, r)) return "Only the verse leader can change that.";

            r.Spawn = new[] { point.x, point.y, point.z };
            _dirty = true;
            return null;
        }

        internal static string SetAccess(string account, Access access, string password)
        {
            Record r = Get(Of(account));
            if (r == null) return "You are not in a verse.";
            if (!IsLeader(account, r)) return "Only the verse leader can change that.";

            if (access == Access.Password && string.IsNullOrWhiteSpace(password))
                return "Give a password: !verse password <word>";

            r.Access = access;
            r.Password = access == Access.Password ? password.Trim() : "";
            _dirty = true;
            return null;
        }

        internal static string Invite(string account, string guest)
        {
            Record r = Get(Of(account));
            if (r == null) return "You are not in a verse.";
            if (!IsLeader(account, r)) return "Only the verse leader can invite.";
            if (r.Members.Contains(guest)) return "They are already in your verse.";

            if (!r.Invites.Contains(guest)) r.Invites.Add(guest);
            // An invite is pointless if nobody can act on it, so this also opens the gate.
            if (r.Access == Access.Private) r.Access = Access.Invite;
            _dirty = true;
            return null;
        }

        /// <summary>Writes the registry out if anything changed. Cheap to call on a timer.</summary>
        /// <summary>
        /// Adds a progression key to a verse. Returns false if it already had it, so callers
        /// can tell a real change from a repeat - boss deaths and `activeBosses` get set more
        /// than once.
        /// </summary>
        /// <summary>Whether this account has already seen the first-join introduction.</summary>
        internal static bool WasIntroduced(string account)
        {
            if (string.IsNullOrEmpty(account)) return true;

            Record r = Get(Of(account));
            return r?.Introduced != null && r.Introduced.Contains(account);
        }

        /// <summary>Records that this account has seen it, so it is never shown again.</summary>
        internal static void Introduce(string account)
        {
            if (string.IsNullOrEmpty(account)) return;

            Record r = Get(Of(account));
            if (r == null) return;

            if (r.Introduced == null) r.Introduced = new List<string>();
            if (r.Introduced.Contains(account)) return;

            r.Introduced.Add(account);
            _dirty = true;
        }

        internal static bool AddKey(Record r, string key)
        {
            if (r == null || string.IsNullOrEmpty(key)) return false;
            if (r.Keys == null) r.Keys = new List<string>();
            if (r.Keys.Contains(key)) return false;

            r.Keys.Add(key);
            _dirty = true;
            return true;
        }

        internal static bool RemoveKey(Record r, string key)
        {
            if (r?.Keys == null || !r.Keys.Remove(key)) return false;
            _dirty = true;
            return true;
        }

        /// <summary>Marks a verse as the one the pre-verse world was migrated into.</summary>
        internal static void MarkLegacy(Record r)
        {
            if (r == null) return;
            r.Legacy = true;
            _dirty = true;
        }

        /// <summary>The verse the existing world was migrated into, or null if none ever was.</summary>
        internal static Record LegacyVerse()
        {
            foreach (Record r in ById.Values)
                if (r.Legacy) return r;
            return null;
        }

        /// <summary>
        /// Puts an account into a verse without any of Join's checks - no password, no party
        /// cap, no invite. Only the migration uses it, which is seeding a verse that already
        /// has its members by definition.
        /// </summary>
        internal static void Enroll(string account, int id)
        {
            if (string.IsNullOrEmpty(account)) return;

            Record r = Get(id);
            if (r == null) return;
            if (!r.Members.Contains(account)) r.Members.Add(account);
            ByMember[account] = id;
            _dirty = true;
        }

        /// <summary>
        /// Creates a verse with a chosen id, for the migration. Ordinary verses take the next
        /// id; the legacy one is named explicitly so that "the old world is verse 1" is
        /// literal rather than a hope about what the counter happens to be. Returns null if
        /// that id is already taken.
        /// </summary>
        internal static Record CreateWithId(int id, string leader)
        {
            if (id == None || ById.ContainsKey(id)) return null;

            var r = new Record { Id = id, Leader = leader ?? "", Access = Access.Private };
            ById[id] = r;
            if (id >= _nextId) _nextId = id + 1;
            _dirty = true;
            return r;
        }

        /// <summary>
        /// Removes a verse outright. Used by the self-test, which creates two throwaway ones
        /// and must not leave them in the file. Deliberately not offered to players or
        /// operators: deleting a verse would orphan every object tagged with it, which is the
        /// open question in the design doc about tombstoning rather than destroying.
        /// </summary>
        internal static void Discard(int id)
        {
            Record r = Get(id);
            if (r == null) return;

            foreach (string member in r.Members) ByMember.Remove(member);
            ById.Remove(id);
            _dirty = true;
        }

        internal static void SaveIfDirty()
        {
            if (!_dirty || _path == null) return;
            _dirty = false;

            try
            {
                var all = new List<Record>(ById.Values);
                // Via a temp file: a half-written registry is worse than a stale one, because
                // on the next boot it reads as "these players have no verse".
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(all, Formatting.Indented));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (System.Exception e)
            {
                _dirty = true;
                VersePlugin.Log.LogError("Could not write the verse registry: " + e.Message);
            }
        }
    }
}
