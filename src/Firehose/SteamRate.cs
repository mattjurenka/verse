using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;

namespace Firehose
{
    /// <summary>
    /// Ceiling 3: Steam's own per-connection send rate, which Valheim pins shut.
    ///
    /// <c>ZSteamSocket.RegisterGlobalCallbacks</c> sets <b>both</b> ends of Steam's rate
    /// estimate to the same number:
    ///
    /// <code>
    /// GCHandle gCHandle3 = GCHandle.Alloc(153600, GCHandleType.Pinned);
    /// SetConfigValue(k_ESteamNetworkingConfig_SendRateMin, ... gCHandle3 ...);
    /// SetConfigValue(k_ESteamNetworkingConfig_SendRateMax, ... gCHandle3 ...);
    /// </code>
    ///
    /// Min == max means Steam's bandwidth estimation has nothing to estimate: every
    /// connection is fixed at exactly 150 KB/s, which is a hard ceiling above anything the
    /// send window does. Lifting only the maximum hands Steam's congestion control back the
    /// range it was built to search - it will still back off on a bad link, which vanilla's
    /// fixed rate cannot do.
    ///
    /// <para>Rather than patch that method (it is small enough to worry about inlining, and
    /// both values share one pinned integer so a transpiler could not tell them apart), the
    /// values are simply set again from here: globally, which is the default every new
    /// connection inherits, and then per connection for peers that are already up, so the
    /// order the two happen in cannot matter. Setting them needs Steam's game-server
    /// interface to be initialised, which is why this runs on a timer rather than at
    /// <c>Awake</c>.</para>
    ///
    /// <para>Only the Steam backend has this limit. Under crossplay the sockets are
    /// <c>ZPlayFabSocket</c>, whose <c>GetSendQueueSize</c> reports a quarter of its in-flight
    /// bytes and which has no equivalent pinned rate, so this stands down.</para>
    /// </summary>
    internal static class SteamRate
    {
        private const int VanillaRate = 153600;
        private const float Interval = 2f;

        private static FieldInfo _connection;
        private static bool _globalDone;
        /// <summary>Primed so the first tick applies at once, in time for the startup check.</summary>
        private static float _timer = Interval;

        /// <summary>Connection handles already configured. Reapplying is harmless, just noise.</summary>
        private static readonly HashSet<uint> Done = new HashSet<uint>();

        /// <summary>What happened, for the startup check and the report.</summary>
        internal static string Status { get; private set; } = "not applied yet";

        /// <summary>Whether the operator asked for any of this.</summary>
        internal static bool Wanted =>
            FirehosePlugin.SendRateMin.Value > 0 && FirehosePlugin.SendRateMax.Value > 0;

        internal static void Tick(float dt)
        {
            if (!Wanted) { Status = "off (SendRateMin or SendRateMax is 0)"; return; }
            if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
            {
                Status = $"off (backend is {ZNet.m_onlineBackend}, which has no pinned rate)";
                return;
            }

            _timer += dt;
            if (_timer < Interval) return;
            _timer = 0f;

            int min = FirehosePlugin.SendRateMin.Value;
            int max = FirehosePlugin.SendRateMax.Value;
            if (max < min) max = min;

            // The global scope is the default inherited by connections made from now on. It
            // is set once, as early as Steam will accept it - well before anybody can join.
            if (!_globalDone)
            {
                bool ok = SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, min)
                        & SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, max);
                if (ok)
                {
                    _globalDone = true;
                    Status = $"min {min} max {max} B/s (vanilla pins both at {VanillaRate})";
                    FirehosePlugin.Log.LogInfo(
                        $"firehose: Steam send rate now min {min / 1024} KB/s, max {max / 1024} KB/s " +
                        $"per connection - vanilla fixes both at {VanillaRate / 1024} KB/s");
                }
                else
                {
                    Status = "Steam rejected the global config value (game server not up yet?)";
                }
            }

            ApplyToLivePeers(min, max);
        }

        /// <summary>
        /// Peers that connected before the global default took, or in case Steam consults the
        /// value once at connection time rather than on every send. Belt and braces: the cost
        /// is two calls per connection, once.
        /// </summary>
        private static void ApplyToLivePeers(int min, int max)
        {
            if (_connection == null)
            {
                _connection = AccessTools.Field(typeof(ZSteamSocket), "m_con");
                if (_connection == null) return;
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (Done.Count > 256) Done.Clear();   // handles get reused; this is only a de-noiser

            for (int i = 0; i < peers.Count; i++)
            {
                var socket = peers[i].m_socket as ZSteamSocket;
                if (socket == null || !socket.IsConnected()) continue;

                object boxed = _connection.GetValue(socket);
                if (!(boxed is HSteamNetConnection connection)) continue;

                uint handle = connection.m_HSteamNetConnection;
                if (handle == 0 || !Done.Add(handle)) continue;

                SetForConnection(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, min);
                SetForConnection(handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, max);
            }
        }

        private static bool SetGlobal(ESteamNetworkingConfigValue key, int value) =>
            Set(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, value);

        private static bool SetForConnection(uint handle, ESteamNetworkingConfigValue key, int value) =>
            Set(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, new IntPtr(handle), value);

        /// <summary>
        /// Steam takes the value by pointer, so the integer has to be pinned for the length of
        /// the call - the same dance vanilla does.
        /// </summary>
        private static bool Set(ESteamNetworkingConfigValue key, ESteamNetworkingConfigScope scope,
                                IntPtr scopeObject, int value)
        {
            GCHandle pin = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                return SteamGameServerNetworkingUtils.SetConfigValue(
                    key, scope, scopeObject,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    pin.AddrOfPinnedObject());
            }
            catch (Exception e)
            {
                Status = "Steamworks threw: " + e.Message;
                return false;
            }
            finally
            {
                pin.Free();
            }
        }
    }
}
