using System.Collections.Generic;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// SPIKE 4. Puts a player down in the verse they just joined, rather than wherever they
    /// last logged out of the one they left.
    ///
    /// <para><b>Why this cannot be done by sending different location icons</b>, which is how
    /// the design notes originally proposed it. <c>Game.FindSpawnPoint</c> (`Game.cs:524`)
    /// resolves a spawn in three steps, and reads all three off the <i>client's own</i>
    /// profile:</para>
    ///
    /// <list type="number">
    /// <item>a logout point, if the player has one and did not just die;</item>
    /// <item>a custom spawn point - a bed - which needs a real <c>Bed</c> object within 5 m
    /// of it or the client clears it and falls through;</item>
    /// <item>otherwise <c>ZoneSystem.GetLocationIcon(m_StartLocation)</c>, i.e. the start
    /// temple, from the icon list the server sent.</item>
    /// </list>
    ///
    /// <para>Only the third step is the server's to influence, and every verse sits at the
    /// same coordinates, so sending per-verse icons would change nothing today. Step 2 already
    /// behaves correctly by accident: a bed in another verse is hidden by the ZDO filter, so
    /// <c>FindBedNearby</c> fails and the client gives up on it. Step 1 is the one that
    /// actually bites, and it is pure client state that no server-side plugin can read or
    /// write.</para>
    ///
    /// <para>So the server lets the client spawn wherever it likes and then <b>moves</b> it.
    /// <c>Character</c> registers <c>RPC_TeleportTo</c> on the player's own <c>ZNetView</c>
    /// (`Character.cs:713`) and acts on it only if the receiver owns that character - which
    /// the client does - so a routed RPC addressed to the character's ZDOID is all it takes.
    /// The server holds no <c>GameObject</c> for the player and does not need one.</para>
    ///
    /// <para>A move is only warranted when the verse changed, which is a thing the server
    /// knows exactly: every verse change goes through the kick-and-rejoin in
    /// <see cref="Commands"/>. That account is noted, and the next character it spawns gets
    /// moved once.</para>
    /// </summary>
    internal static class Spawns
    {
        /// <summary>
        /// How long to let the client finish arriving before moving it. The teleport is sent
        /// as a distant one, so the client reloads the area itself; this is only to avoid
        /// racing its own spawn.
        /// </summary>
        /// <summary>
        /// How long to let the client finish arriving before moving it. Was 1.5s, which was
        /// not enough: the teleport landed while the client was still running its own spawn
        /// sequence, and the spawn won - the player stayed where they logged out and the
        /// server log cheerfully reported a move that never happened.
        /// </summary>
        private const float Settle = 4f;

        /// <summary>
        /// Tries after the first, in case the client was still not ready. Each one is checked
        /// against where the player actually is, so a teleport that worked costs nothing more.
        /// </summary>
        private const int Attempts = 4;
        private const float Retry = 3f;

        /// <summary>Close enough to the target to call it arrived.</summary>
        private const float Arrived = 60f;

        private const float TickEvery = 0.5f;

        /// <summary>A pending move: which body, and when it is due.</summary>
        private struct Arrival
        {
            internal ZDOID Character;
            internal float DueAt;
            internal int Tries;
        }

        /// <summary>Accounts that changed verse and have not yet been put down in the new one.</summary>
        private static readonly HashSet<string> Expected = new HashSet<string>();
        /// <summary>The character body last seen for a connection, to notice a new one.</summary>
        private static readonly Dictionary<long, ZDOID> Seen = new Dictionary<long, ZDOID>();
        private static readonly Dictionary<long, Arrival> Pending = new Dictionary<long, Arrival>();
        private static readonly List<long> Ready = new List<long>();

        private static bool _haveTemple;
        private static Vector3 _temple;
        private static float _timer;
        private static float _templePoll;

        /// <summary>
        /// Note that this account's next spawn belongs to a different verse than its last one.
        /// Keyed by account rather than connection because the connection is about to be
        /// dropped - that is how a verse change takes effect.
        /// </summary>
        internal static void Expect(string account)
        {
            if (!string.IsNullOrEmpty(account)) Expected.Add(account);
        }

        internal static void Forget(long uid)
        {
            Seen.Remove(uid);
            Pending.Remove(uid);
        }

        /// <summary>
        /// Whether the world's start temple is known yet, for anything outside this class that
        /// needs the shared arrival point - <see cref="Sweep"/> treats it as ground every verse
        /// starts from, not territory any one verse owns.
        /// </summary>
        internal static bool TryGetTemple(out Vector3 point) => Temple(out point);

        /// <summary>Whether the world's start temple is known yet. Locations exist by the time a peer can connect.</summary>
        private static bool Temple(out Vector3 point)
        {
            if (_haveTemple)
            {
                point = _temple;
                return true;
            }

            point = Vector3.zero;
            if (ZoneSystem.instance == null) return false;

            string name = Game.instance != null && !string.IsNullOrEmpty(Game.instance.m_StartLocation)
                ? Game.instance.m_StartLocation
                : "StartTemple";

            if (!ZoneSystem.instance.GetLocationIcon(name, out Vector3 at)) return false;

            _temple = at + Vector3.up * 2f;   // the same clearance Game.FindSpawnPoint adds
            _haveTemple = true;
            // Logged once, because a world where this never resolves would otherwise only
            // show up as arrivals quietly not being moved. Locations are generated during
            // world load, well after this plugin's Awake, so the first few looks miss.
            VersePlugin.Log.LogInfo($"start temple at {_temple.x:0}, {_temple.z:0} - the default arrival point for every verse");
            point = _temple;
            return true;
        }

        /// <summary>Where an arrival in this verse is put down.</summary>
        internal static bool Resolve(int verse, out Vector3 point)
        {
            Record record = Verses.Get(verse);
            if (record?.Spawn != null && record.Spawn.Length == 3)
            {
                point = new Vector3(record.Spawn[0], record.Spawn[1], record.Spawn[2]);
                return true;
            }

            return Temple(out point);
        }

        /// <summary>One line for the status command.</summary>
        internal static string Describe(int verse)
        {
            Record record = Verses.Get(verse);
            if (record?.Spawn != null && record.Spawn.Length == 3)
                return $"arrivals land at {record.Spawn[0]:0}, {record.Spawn[2]:0}";
            return "arrivals land at the start temple";
        }

        /// <summary>
        /// The spawn command: move this verse's arrival point to where the caller is standing.
        /// Their reference position is what the client reports for itself every few hundred
        /// milliseconds, which is close enough to "here" and costs the server nothing.
        /// </summary>
        internal static string SetHere(string account, ZNetPeer peer)
        {
            if (peer == null) return "I cannot tell where you are standing.";

            Vector3 at = peer.m_refPos;
            if (at == Vector3.zero)
                return "I do not know where you are yet - try again in a moment.";

            string problem = Verses.SetSpawn(account, at);
            if (problem != null) return problem;

            Verses.SaveIfDirty();
            return null;
        }


        /// <summary>
        /// Gives a brand-new verse a starting point away from where the older ones have been
        /// playing.
        ///
        /// <para>This exists because of the one thing copy-on-write cannot undo. Objects
        /// destroyed *before* the plugin was installed are gone from the world, not hidden:
        /// trees felled, ore mined out and world chests emptied by the original party are
        /// simply absent, and a new verse would inherit that stripped ground. The damage is
        /// local, though - a few hundred metres of a ten-kilometre map - so starting
        /// elsewhere sidesteps nearly all of it for nothing.</para>
        ///
        /// <para>The angle comes from the verse id rather than a random number, so a verse
        /// always lands in the same place however many times the server restarts, and two
        /// verses never share a spot. Candidate points step outwards until one is on land:
        /// the ring radius is a preference, not a promise.</para>
        /// </summary>
        internal static void Scatter(Record record)
        {
            if (record == null || !VersePlugin.ScatterNewVerses.Value) return;
            if (record.Spawn != null) return;                 // somebody already chose
            if (WorldGenerator.instance == null) return;      // too early; stays at the temple

            float radius = Mathf.Max(0f, VersePlugin.ScatterRadius.Value);
            if (radius <= 0f) return;

            // The golden angle keeps successive ids far apart on the ring rather than adjacent.
            float start = record.Id * 2.39996323f;

            // Both the bearing and the distance are swept. An earlier version only stepped
            // outwards, which cannot help when the bearing points out to sea - and a bearing
            // that does is a coin toss on a world that is mostly ocean past the first few
            // kilometres. The self-test caught that.
            for (int attempt = 0; attempt < 32; attempt++)
            {
                float angle = start + attempt * 0.73f;
                float r = radius * (1f + attempt / 8 * 0.2f);
                float x = Mathf.Sin(angle) * r;
                float z = Mathf.Cos(angle) * r;

                // WorldGenerator rather than ZoneSystem.GetGroundHeight: that one is a
                // Physics.Raycast against loaded terrain (`ZoneSystem.cs:2752`), and on a
                // server with nobody on it there is no terrain loaded two kilometres out, so
                // it answers "no ground" everywhere. The generator is a pure function of the
                // seed and needs no mesh.
                if (WorldGenerator.instance.GetBiome(x, z) == Heightmap.Biome.Ocean) continue;

                float height = WorldGenerator.instance.GetHeight(x, z);
                if (height < WaterLevel + 2f) continue;       // a beach at high tide is not land

                record.Spawn = new[] { x, height + 0.25f, z };
                VersePlugin.Log.LogInfo(
                    $"verse {record.Id} will start at {x:0}, {z:0} - {r:0} m out, " +
                    "clear of wherever anyone else has been playing");
                return;
            }

            VersePlugin.Log.LogWarning(
                $"found no land for verse {record.Id} near {radius:0} m; it will start at the " +
                "temple with everybody else");
        }

        /// <summary>Vanilla's sea level (`ZoneSystem.m_waterLevel`). Anything below it is water.</summary>
        private const float WaterLevel = 30f;

        internal static void Tick(float dt)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            _timer += dt;
            if (_timer < TickEvery) return;
            _timer = 0f;

            // Looked for ahead of needing it, so the log says at boot whether the fallback
            // arrival point exists rather than only when somebody changes verse. The lookup
            // is a scan of every location instance in the world, so it is slow enough to be
            // worth doing once and cheap enough to retry every ten seconds until it lands:
            // locations are generated during world load, after this plugin starts.
            if (!_haveTemple)
            {
                _templePoll += TickEvery;
                if (_templePoll >= 10f)
                {
                    _templePoll = 0f;
                    Temple(out Vector3 _);
                }
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            float now = Time.time;

            for (int i = 0; i < peers.Count; i++)
            {
                ZNetPeer peer = peers[i];
                ZDOID character = peer.m_characterID;
                if (character == ZDOID.None) continue;

                if (Seen.TryGetValue(peer.m_uid, out ZDOID previous) && previous == character) continue;
                Seen[peer.m_uid] = character;

                // A new body for a connection we were waiting on. Anybody else's respawn is
                // their own business - their bed and logout point are inside their own verse.
                string account = Peers.PlatformId(peer);
                if (account == null || !Expected.Remove(account)) continue;

                Pending[peer.m_uid] = new Arrival { Character = character, DueAt = now + Settle };
            }

            if (Pending.Count == 0) return;

            Ready.Clear();
            foreach (KeyValuePair<long, Arrival> entry in Pending)
                if (entry.Value.DueAt <= now) Ready.Add(entry.Key);

            for (int i = 0; i < Ready.Count; i++)
            {
                long uid = Ready[i];
                Arrival arrival = Pending[uid];

                // Keep trying until the player is actually standing there. A teleport sent
                // while the client is still spawning is simply lost, and the only way to
                // know from here is to look at where they ended up.
                if (Place(uid, arrival.Character, arrival.Tries))
                {
                    Pending.Remove(uid);
                    continue;
                }

                arrival.Tries++;
                if (arrival.Tries >= Attempts)
                {
                    Pending.Remove(uid);
                    VersePlugin.Log.LogWarning(
                        $"peer {uid} did not end up at their verse's start after {Attempts} " +
                        "attempts; leaving them where they are");
                    continue;
                }

                arrival.DueAt = now + Retry;
                Pending[uid] = arrival;
            }
        }

        /// <summary>
        /// Sends the move, and reports whether the player is now where they should be.
        /// Returns true once they have arrived, so the caller can stop trying.
        /// </summary>
        private static bool Place(long uid, ZDOID recorded, int tries)
        {
            ZNetPeer peer = ZNet.instance?.GetPeer(uid);
            if (peer == null) return true;   // they left again; nothing left to do

            int verse = Peers.VerseOf(uid);
            if (verse == Verses.None) return true;

            if (!Resolve(verse, out Vector3 point))
            {
                VersePlugin.Log.LogWarning(
                    $"no spawn point for verse {verse} and no start temple either - peer {uid} " +
                    "left where their client put them");
                return true;
            }

            // Already there - either the last attempt worked, or they walked back.
            if (tries > 0 && Vector3.Distance(peer.m_refPos, point) < Arrived)
            {
                Metrics.Placed++;
                VersePlugin.Log.LogInfo(
                    $"peer {uid} arrived in verse {verse} at {point.x:0}, {point.z:0}");
                VerseIdentity.SayTo(uid, $"Welcome to verse {verse}.");
                return true;
            }

            // Their current body, not the one that triggered this: arriving somewhere
            // dangerous and dying inside the settle delay would otherwise address the
            // teleport to a corpse, which the client rightly ignores.
            ZDOID character = peer.m_characterID != ZDOID.None ? peer.m_characterID : recorded;
            if (character == ZDOID.None) return false;

            // Distant, so the client runs its own teleport: fade, reload the area, put the
            // player down. Without it a client can be dropped into terrain it has not loaded.
            ZRoutedRpc.instance.InvokeRoutedRPC(uid, character, "RPC_TeleportTo",
                point, Quaternion.identity, true);

            VersePlugin.Log.LogInfo(
                $"peer {uid} arriving in verse {verse}: sent to {point.x:0}, {point.y:0}, " +
                $"{point.z:0} (attempt {tries + 1})");
            return false;
        }
    }
}
