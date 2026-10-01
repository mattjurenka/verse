using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// Everything that answers "why did nothing happen?".
    ///
    /// This plugin works by feeding vanilla code paths that were built for other players, and
    /// several of those paths fail *silently* - a chat message with an unresolvable sender is
    /// dropped without a word, and a ZDO nobody instantiates simply never appears. So the
    /// interesting events are logged with enough context to tell which gate closed, and
    /// <see cref="Report"/> puts the same picture in front of whoever is standing in the world,
    /// which is where they actually are when it goes wrong.
    /// </summary>
    internal static class Diagnostics
    {
        private static bool _startupLogged;
        private static readonly Dictionary<long, string> KnownPeers = new Dictionary<long, string>();
        private static float _peerScan;
        private static float _stateLog;

        /// <summary>Why the last answer fell back, for the in-world report.</summary>
        internal static string LastFailure = "";
        internal static float LastFailureAt = -999f;

        internal static int HeardCount;
        internal static int AnsweredCount;
        internal static int SentLines;

        internal static void Log(string message) => Plugin.Log.LogInfo(message);

        internal static void Verbose(string message)
        {
            if (Plugin.Verbose == null || !Plugin.Verbose.Value) return;
            Plugin.Log.LogInfo(message);
        }

        /// <summary>
        /// One block, the first time the network is up, covering everything that decides
        /// whether this plugin can work at all. Cheap insurance: most of the ways this can be
        /// broken are visible here rather than at the moment of failure.
        /// </summary>
        internal static void Startup(Harmony harmony)
        {
            if (_startupLogged || ZNet.instance == null || ZDOMan.instance == null) return;
            _startupLogged = true;

            var sb = new StringBuilder();
            sb.AppendLine("--- Mark Hall-Patton: startup check ---");
            sb.AppendLine($"  role            : server={ZNet.instance.IsServer()} " +
                          $"dedicated={ZNet.instance.IsDedicated()} " +
                          $"backend={ZNet.m_onlineBackend}");
            sb.AppendLine($"  session id      : {ZDOMan.GetSessionID()}");
            // A dedicated server has a Chat instance but no local player, so the player is the
            // part that says whether anything is drawn here.
            sb.AppendLine($"  local player    : {(Player.m_localPlayer != null ? "yes (listen server)" : "no")}");

            // The single hard dependency: without this prefab no client can build him.
            bool havePlayerPrefab = ZNetScene.instance != null &&
                                    ZNetScene.instance.GetPrefab("Player".GetStableHashCode()) != null;
            sb.AppendLine($"  Player prefab   : {(havePlayerPrefab ? "found" : "MISSING - he cannot be created")}");

            sb.AppendLine($"  command word    : \"{Plugin.CommandWord.Value}\"  (no leading '/', by design)");
            sb.AppendLine($"  display name    : \"{Identity.Name}\"  as {Identity.UserId}");
            sb.AppendLine($"  model           : enabled={Plugin.MuseEnabled.Value} " +
                          $"key={(MuseClient.Configured ? "present" : "MISSING")} " +
                          $"id={Plugin.MuseModel.Value}");
            sb.AppendLine($"  follow          : {Plugin.Follow.Value} at {Plugin.FollowDistance.Value}m, " +
                          $"leash {Plugin.LeashDistance.Value}m, earshot {Plugin.Earshot.Value}m");

            // Proof that every patch bound. Harmony throws on an unresolvable target, so a
            // short list here means a target was renamed by a game update.
            var patched = new List<string>();
            foreach (MethodBase m in harmony.GetPatchedMethods())
                patched.Add((m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name);
            patched.Sort();
            sb.AppendLine($"  patches ({patched.Count})    : {string.Join(", ", patched.ToArray())}");
            sb.Append("  verbose logging : ").Append(Plugin.Verbose.Value);

            Plugin.Log.LogInfo(sb.ToString());

            if (!ZNet.instance.IsServer())
                Plugin.Log.LogWarning(
                    "This is a client, not a server. The plugin does nothing here - install it " +
                    "on the server instead.");
        }

        /// <summary>
        /// Logs who is connected, and on which platform. Polled rather than patched: one more
        /// Harmony hook for a log line is a bad trade, and this also catches the peer who was
        /// already connected when the plugin loaded.
        /// </summary>
        internal static void PeerScan(float dt)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            _peerScan -= dt;
            if (_peerScan > 0f) return;
            _peerScan = 2f;

            var seen = new HashSet<long>();

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                seen.Add(peer.m_uid);
                if (KnownPeers.ContainsKey(peer.m_uid)) continue;

                KnownPeers[peer.m_uid] = peer.m_playerName;
                Log($"peer joined: \"{peer.m_playerName}\" uid={peer.m_uid} " +
                    $"platform={PlatformOf(peer)} at {Round(ServerChat.PeerPosition(peer))}");
            }

            List<long> left = null;
            foreach (KeyValuePair<long, string> known in KnownPeers)
                if (!seen.Contains(known.Key))
                    (left ?? (left = new List<long>())).Add(known.Key);

            if (left == null) return;
            foreach (long uid in left)
            {
                Log($"peer left: \"{KnownPeers[uid]}\" uid={uid}");
                KnownPeers.Remove(uid);
            }
        }

        /// <summary>
        /// The platform a peer is really on, which is not the same as the transport. On a
        /// crossplay server everyone arrives over PlayFab, but the socket host name still
        /// parses into a platform-prefixed ID.
        /// </summary>
        internal static string PlatformOf(ZNetPeer peer)
        {
            if (ZNet.instance == null) return "?";

            foreach (ZNet.PlayerInfo player in ZNet.instance.GetPlayerList())
                if (player.m_characterID == peer.m_characterID && player.m_userInfo.m_id.IsValid)
                    return player.m_userInfo.m_id.m_platform.ToString();

            return "?";
        }

        /// <summary>Where he is and what he is doing, while he is out.</summary>
        internal static void State(float dt)
        {
            if (Plugin.Verbose == null || !Plugin.Verbose.Value) return;
            if (!Historian.IsOut) return;

            _stateLog -= dt;
            if (_stateLog > 0f) return;
            _stateLog = 5f;

            Verbose($"state: at {Round(Historian.Position)} {Historian.Describe()}");
        }

        /// <summary>
        /// The in-world report, for `!mark debug`. Arriving at all is itself the headline
        /// result: it means the message was overheard and the reply reached a chat window,
        /// which is both gates that fail silently.
        /// </summary>
        internal static List<string> Report(long peerId)
        {
            var lines = new List<string>();

            lines.Add($"Diagnostics: you were heard and answered, so chat works both ways. " +
                      $"Heard {HeardCount}, answered {AnsweredCount}, lines sent {SentLines}.");

            if (Historian.IsOut)
            {
                string instance = ServerSideInstance() ? "yes (near world origin)" : "no";
                lines.Add($"I am out: {Historian.Describe()}. Server-side body: {instance}.");
            }
            else
            {
                lines.Add($"I am not out. Type '{Plugin.CommandWord.Value}' to summon me.");
            }

            int peers = 0, inRange = 0;
            if (ZNet.instance != null)
            {
                Vector3 mouth = ServerChat.Mouth;
                float range = Mathf.Max(10f, Plugin.Earshot.Value + 10f);
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || !peer.IsReady()) continue;
                    peers++;
                    if ((ServerChat.PeerPosition(peer) - mouth).magnitude <= range) inRange++;
                }
            }
            lines.Add($"Peers connected: {peers}, in earshot of me: {inRange}. " +
                      $"Model: {(Plugin.MuseEnabled.Value ? "on" : "off")}, " +
                      $"key {(MuseClient.Configured ? "present" : "missing")}, " +
                      $"calls {Conversation.BudgetState()}.");

            if (!string.IsNullOrEmpty(LastFailure))
                lines.Add($"Last model failure, {Mathf.RoundToInt(Time.time - LastFailureAt)}s ago: {LastFailure}");

            Log($"debug report for peer {peerId}: {string.Join(" | ", lines.ToArray())}");
            return lines;
        }

        /// <summary>
        /// True when the server built a real GameObject for him, which only happens near the
        /// world origin. Worth knowing, because that is the case the Player patches exist for.
        /// </summary>
        internal static bool ServerSideInstance()
        {
            if (!Historian.IsOut || ZDOMan.instance == null || ZNetScene.instance == null) return false;
            ZDO zdo = ZDOMan.instance.GetZDO(Historian.Id);
            return zdo != null && ZNetScene.instance.FindInstance(zdo) != null;
        }

        internal static string Round(Vector3 v) =>
            $"({v.x:0.#}, {v.y:0.#}, {v.z:0.#})";
    }
}
