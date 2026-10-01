using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// Mark himself: one networked object the server owns, with no GameObject behind it.
    ///
    /// A dedicated server only instantiates objects around its own reference position, which
    /// stays at the world origin - creatures near players are simulated by the players'
    /// clients, not by the server. So a server-side companion cannot be a MonoBehaviour
    /// riding on a spawned prefab the way a client-side mod would do it. Instead this creates
    /// the ZDO for the vanilla <c>Player</c> prefab and drives its fields directly: position,
    /// rotation, the animator parameters <c>ZSyncAnimation</c> syncs, and the equipment
    /// fields <c>VisEquipment</c> reads.
    ///
    /// Everything downstream of that is stock behaviour. Each client instantiates him exactly
    /// as it instantiates any other player it does not own, interpolates his position through
    /// <c>ZSyncTransform</c>, and renders his clothes, hair and beard from the ZDO - which is
    /// why nobody has to install anything.
    ///
    /// The <c>Player</c> prefab is used because it is the only humanoid a vanilla client can
    /// already build that carries a configurable face, hair, beard and clothes, and takes its
    /// displayed name from the ZDO rather than from the prefab.
    /// </summary>
    internal static class Historian
    {
        private const string SourcePrefab = "Player";

        /// <summary>Marks the ZDO as ours, for the <c>Player</c> patches in Patches.cs.</summary>
        internal static readonly int ZdoFlag = "MHP_historian".GetStableHashCode();

        /// <summary>
        /// ZSyncAnimation stores each animator parameter in the ZDO under this offset plus the
        /// parameter's hash, and non-owners read them back the same way. Matching that is what
        /// makes him walk rather than slide.
        /// </summary>
        private const int AnimOffset = 438569;

        private static readonly int AnimForward = ZSyncAnimation.GetHash("forward_speed");
        private static readonly int AnimSideways = ZSyncAnimation.GetHash("sideway_speed");
        private static readonly int AnimTurn = ZSyncAnimation.GetHash("turn_speed");
        private static readonly int AnimOnGround = ZSyncAnimation.GetHash("onGround");
        private static readonly int AnimInWater = ZSyncAnimation.GetHash("inWater");
        private static readonly int AnimFlying = ZSyncAnimation.GetHash("flying");
        private static readonly int AnimFalling = ZSyncAnimation.GetHash("falling");
        private static readonly int AnimEncumbered = ZSyncAnimation.GetHash("encumbered");
        private static readonly int AnimCrouching = ZSyncAnimation.GetHash("crouching");

        private static ZDO _zdo;
        private static long _host;
        private static string _hostName = "";

        /// <summary>Where the player he is following has been, so he walks the same ground.</summary>
        private static readonly List<Vector3> _trail = new List<Vector3>();
        private static float _crumbTimer;
        private static float _lastAnimForward = -1f;
        private static float _lookTimer;

        /// <summary>Told to wait where he is. Reset by the next summon.</summary>
        internal static bool Stay;

        internal static bool IsOut => _zdo != null && _zdo.IsValid();
        internal static ZDOID Id => _zdo != null ? _zdo.m_uid : ZDOID.None;
        internal static Vector3 Position => _zdo != null ? _zdo.GetPosition() : Vector3.zero;

        /// <summary>
        /// Brings him to a player: creates him if he is not out, walks him over if he is.
        /// There is only ever one of him - he is a real person with one calendar.
        /// </summary>
        internal static bool Summon(long peerId, string peerName, Vector3 playerPos, Quaternion playerRot)
        {
            _host = peerId;
            _hostName = peerName ?? "";
            _trail.Clear();

            Vector3 spot = SpotBeside(playerPos, playerRot);

            if (IsOut)
            {
                Place(spot, playerPos);
                return true;
            }

            int hash = SourcePrefab.GetStableHashCode();
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            if (prefab == null)
            {
                Plugin.Log.LogError(
                    $"No '{SourcePrefab}' prefab in ZNetScene - this Valheim version is not supported.");
                return false;
            }

            _zdo = ZDOMan.instance.CreateNewZDO(spot, hash);
            _zdo.SetPrefab(hash);

            // Copy the sync class off the prefab's own ZNetView rather than guessing, so he is
            // replicated to clients on exactly the terms a real player is.
            var view = prefab.GetComponent<ZNetView>();
            _zdo.Type = view != null ? view.m_type : ZDO.ObjectType.Prioritized;
            _zdo.Distant = view != null && view.m_distant;

            // Never written to the world save: he should not be standing in an empty world
            // after a restart, waiting for a question. Non-persistent ZDOs owned by the server
            // survive as long as the server does, which is exactly the lifetime wanted.
            _zdo.Persistent = false;

            _zdo.Set(ZdoFlag, true);
            Look.Apply(_zdo);
            SetAnim(AnimOnGround, true);
            SetAnim(AnimInWater, false);
            SetAnim(AnimFlying, false);
            SetAnim(AnimFalling, false);
            SetAnim(AnimEncumbered, false);
            SetAnim(AnimCrouching, false);
            SetAnim(AnimSideways, 0f);
            SetAnim(AnimTurn, 0f);
            SetAnim(AnimForward, 0f);

            Place(spot, playerPos);

            Diagnostics.Log(
                $"summoned by \"{_hostName}\" at {Diagnostics.Round(spot)}: " +
                $"zdo={_zdo.m_uid} prefab={hash} type={_zdo.Type} distant={_zdo.Distant} " +
                $"persistent={_zdo.Persistent} owner={_zdo.GetOwner()} (session {ZDOMan.GetSessionID()})");

            if (Diagnostics.ServerSideInstance())
                Diagnostics.Log(
                    "the server built a GameObject for him - he is within the server active area " +
                    "around the world origin, so the Player patches are what is keeping him alive");

            return true;
        }

        internal static void Dismiss()
        {
            if (_zdo == null) return;

            if (_zdo.IsValid() && ZDOMan.instance != null)
            {
                _zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(_zdo);
            }

            Diagnostics.Log("dismissed");

            _zdo = null;
            _host = 0;
            _hostName = "";
            Stay = false;
            _trail.Clear();
            _lastAnimForward = -1f;
        }

        /// <summary>Walks him after the player who summoned him, once per frame.</summary>
        internal static void Tick(float dt)
        {
            if (!IsOut)
            {
                if (_zdo != null) Dismiss();   // destroyed under us somehow
                return;
            }

            // A client can in principle take ownership of anything; take it back, because only
            // the owner may write the fields that keep him standing up and dressed.
            if (_zdo.GetOwner() != ZDOMan.GetSessionID())
                _zdo.SetOwner(ZDOMan.GetSessionID());

            // Nothing on the server simulates him, so nothing heals him either - and if he was
            // summoned near the world origin the server does have a real Player object for
            // him that a mob can hit. Top him back up rather than let it quietly die.
            if (_zdo.GetFloat(ZDOVars.s_health, 0f) < Look.Health) Look.Apply(_zdo);

            // Cheap safety net against anything that clears the look, and it picks up config
            // edits made while the server is running.
            _lookTimer -= dt;
            if (_lookTimer <= 0f)
            {
                _lookTimer = 5f;
                Look.Apply(_zdo);
            }

            if (!TryGetHost(out Vector3 hostPos, out Quaternion hostRot))
            {
                Plugin.Log.LogInfo($"{Identity.Name}'s host left; he heads home.");
                Dismiss();
                return;
            }

            if (Conversation.IdleTooLong())
            {
                ServerChat.Broadcast(Dialogue.Leaving(), ServerChat.Mouth);
                Dismiss();
                return;
            }

            if (!Plugin.Follow.Value || Stay)
            {
                Stand(hostPos);
                return;
            }

            Vector3 here = _zdo.GetPosition();
            float gap = Flat(hostPos - here).magnitude;

            if (gap > Mathf.Max(10f, Plugin.LeashDistance.Value))
            {
                // Sprinted off, took a boat, used a portal. Retracing a trail that long would
                // take him minutes, so he simply turns up.
                _trail.Clear();
                Place(SpotBeside(hostPos, hostRot), hostPos);
                return;
            }

            DropCrumb(dt, hostPos);

            float follow = Mathf.Max(1.5f, Plugin.FollowDistance.Value);
            if (gap <= follow || _trail.Count == 0)
            {
                Stand(hostPos);
                return;
            }

            Vector3 target = _trail[0];
            Vector3 toTarget = Flat(target - here);
            float step = Mathf.Max(0.5f, Plugin.WalkSpeed.Value) * dt;

            if (toTarget.magnitude <= Mathf.Max(step, 0.35f))
            {
                _trail.RemoveAt(0);
                MoveTo(target, toTarget);
                return;
            }

            Vector3 dir = toTarget.normalized;
            Vector3 next = here + dir * step;
            // Take the height from the ground the player was actually standing on, blended over
            // the step, so he keeps to their footing instead of the server guessing at terrain.
            next.y = Mathf.Lerp(here.y, target.y, Mathf.Clamp01(step / Mathf.Max(0.01f, toTarget.magnitude)));
            MoveTo(next, dir);
        }

        /// <summary>One line of state, for the log and for `!mark debug`.</summary>
        internal static string Describe()
        {
            if (!IsOut) return "not out";

            var sb = new StringBuilder();
            sb.Append(Stay ? "staying put" : (Plugin.Follow.Value ? "following" : "planted"));
            sb.Append(" host=\"").Append(_hostName).Append("\" (").Append(_host).Append(")");

            if (TryGetHost(out Vector3 hostPos, out Quaternion _))
                sb.Append(" gap=").Append(Flat(hostPos - _zdo.GetPosition()).magnitude.ToString("0.#")).Append("m");
            else
                sb.Append(" host=GONE");

            sb.Append(" trail=").Append(_trail.Count);
            sb.Append(" walkanim=").Append(_lastAnimForward.ToString("0.#"));
            sb.Append(" owner=").Append(_zdo.GetOwner() == ZDOMan.GetSessionID() ? "ok" : "LOST");
            sb.Append(" hp=").Append(_zdo.GetFloat(ZDOVars.s_health, -1f).ToString("0"));
            return sb.ToString();
        }

        // --- internals ---

        private static void DropCrumb(float dt, Vector3 hostPos)
        {
            _crumbTimer -= dt;
            if (_crumbTimer > 0f) return;
            _crumbTimer = 0.25f;

            // A player standing still leaves no crumbs, so he stops rather than creeping in.
            if (_trail.Count > 0 && Vector3.Distance(_trail[_trail.Count - 1], hostPos) < 0.6f) return;

            _trail.Add(hostPos);
            while (_trail.Count > 80) _trail.RemoveAt(0);
        }

        /// <summary>Standing still, turned towards whoever he is with.</summary>
        private static void Stand(Vector3 hostPos)
        {
            SetAnim(AnimForward, 0f);
            Face(hostPos - _zdo.GetPosition());
        }

        private static void MoveTo(Vector3 position, Vector3 facing)
        {
            _zdo.SetPosition(position);
            Face(facing);
            SetAnim(AnimForward, Mathf.Max(0.5f, Plugin.WalkSpeed.Value));
        }

        private static void Face(Vector3 direction)
        {
            Vector3 flat = Flat(direction);
            if (flat.sqrMagnitude < 0.0004f) return;

            Quaternion want = Quaternion.LookRotation(flat.normalized);
            // A standing player's position jitters, and every rotation written is a network
            // revision, so ignore anything that is not a real turn.
            if (Quaternion.Angle(_zdo.GetRotation(), want) < 3f) return;

            _zdo.SetRotation(want);
        }

        /// <summary>Puts him at a spot, facing the player, with no walking in between.</summary>
        private static void Place(Vector3 spot, Vector3 lookAt)
        {
            _zdo.SetPosition(spot);
            Face(lookAt - spot);
            SetAnim(AnimForward, 0f);
        }

        /// <summary>An arm's length in front of the player, on the player's own footing.</summary>
        private static Vector3 SpotBeside(Vector3 playerPos, Quaternion playerRot)
        {
            Vector3 forward = Flat(playerRot * Vector3.forward);
            if (forward.sqrMagnitude < 0.0004f) forward = Vector3.forward;
            return playerPos + forward.normalized * 1.6f;
        }

        private static bool TryGetHost(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (ZNet.instance == null) return false;

            // The host is normally a connected peer; on a listen server it can be the person
            // hosting, who is not in the peer list at all.
            ZNetPeer peer = ZNet.instance.GetPeer(_host);
            if (peer == null)
            {
                if (_host != ZDOMan.GetSessionID()) return false;
                ZDO own = LocalHostZdo();
                if (own == null) return false;
                position = own.GetPosition();
                rotation = own.GetRotation();
                return true;
            }

            if (!peer.IsReady()) return false;

            ZDO character = peer.m_characterID == ZDOID.None
                ? null
                : ZDOMan.instance.GetZDO(peer.m_characterID);

            // The character ZDO is the accurate answer; the peer's reference position is a
            // coarse fallback that is still good enough to walk towards.
            position = character != null ? character.GetPosition() : peer.m_refPos;
            if (character != null) rotation = character.GetRotation();
            return true;
        }

        private static ZDO LocalHostZdo()
        {
            ZDOID id = ZNet.instance.LocalPlayerCharacterID;
            return id == ZDOID.None ? null : ZDOMan.instance.GetZDO(id);
        }

        private static void SetAnim(int parameter, float value)
        {
            if (parameter == AnimForward)
            {
                // Position changes every frame; this one does not, and every write is a
                // network revision, so only send it when it actually moves.
                if (Mathf.Abs(value - _lastAnimForward) < 0.05f) return;
                _lastAnimForward = value;
            }
            _zdo.Set(AnimOffset + parameter, value);
        }

        private static void SetAnim(int parameter, bool value)
        {
            // ZSyncAnimation writes booleans as ints and reads them back with GetInt. It also
            // calls ZDO.AddSessionHash on these keys, which only keeps them out of the world
            // save - not needed here, since he is never saved.
            _zdo.Set(AnimOffset + parameter, value ? 1 : 0);
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
