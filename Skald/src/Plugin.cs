using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Skald
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "skald.servermetrics";
        public const string Name = "Skald";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<int> HttpPort;
        internal static ConfigEntry<string> HttpBind;
        internal static ConfigEntry<string> AdminToken;
        internal static ConfigEntry<string> RestartMode;
        internal static ConfigEntry<string> RestartCommand;
        internal static ConfigEntry<string> ConsoleTitle;
        internal static ConfigEntry<bool> FlagIgnoreAdmins;
        internal static ConfigEntry<bool> AlertInGame;
        internal static ConfigEntry<string> AlertStyle;
        internal static ConfigEntry<bool> AlertChat;
        internal static ConfigEntry<int> ServerFps;
        bool _fpsApplied;
        internal static ConfigEntry<string> AlertDiscordWebhook;
        bool _titleSet;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool SetConsoleTitle(string title);

        /// <summary>BepInEx names the console "BepInEx x - Valheim", the same as the game client's; give the server a distinct name.</summary>
        void ApplyConsoleTitle()
        {
            string template = ConsoleTitle.Value;
            if (string.IsNullOrEmpty(template)) return;
            if (Application.platform != RuntimePlatform.WindowsServer && Application.platform != RuntimePlatform.WindowsPlayer) return;
            var args = Environment.GetCommandLineArgs();
            string Arg(string name, string fallback)
            {
                int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            }
            try { SetConsoleTitle(template.Replace("{world}", Arg("-world", "Dedicated")).Replace("{port}", Arg("-port", "2456"))); }
            catch (Exception ex) { Log.LogWarning("Could not set console title: " + ex.Message); }
        }

        Harmony _harmony;
        WebServer _web;
        float _nextPeerPoll, _nextFlush;
        readonly Dictionary<long, string> _knownPeers = new Dictionary<long, string>();

        void Awake()
        {
            Log = Logger;
            HttpPort = Config.Bind("Web", "Port", 8642, "Port for the local dashboard.");
            HttpBind = Config.Bind("Web", "Bind", "localhost",
                "Host to listen on. 'localhost' = this machine only. Use '*' to allow other machines (put it behind a login first).");

            AdminToken = Config.Bind("Admin", "Token", "",
                "Secret for dashboard admin actions (save, restart). Generated on first start; paste it into the dashboard's Admin box.");
            bool windows = Application.platform == RuntimePlatform.WindowsServer || Application.platform == RuntimePlatform.WindowsPlayer;
            RestartMode = Config.Bind("Admin", "RestartMode", windows ? "relaunch" : "exit",
                "relaunch = start the server again after it shuts down (same command line, new console window on Windows). " +
                "exit = only shut down and let your host/service manager (systemd, Docker, panel) start it again.");
            RestartCommand = Config.Bind("Admin", "RestartCommand", "",
                "Optional command to start the server after a restart instead of reusing the current command line " +
                "(Linux with BepInEx: e.g. ./start_server_bepinex.sh).");
            ConsoleTitle = Config.Bind("Console", "Title", "Valheim Server (Skald) - {world} :{port}",
                "Title for the server's console window ({world} and {port} come from the command line). Empty = leave BepInEx's title.");
            FlagIgnoreAdmins = Config.Bind("Flags", "IgnoreAdmins", false,
                "Do not raise cheat flags for players on the server admin list.");
            AlertInGame = Config.Bind("Alerts", "InGame", true,
                "Alert online server admins in-game when a new cheat flag is raised (see InGameStyle and InChat).");
            AlertStyle = Config.Bind("Alerts", "InGameStyle", "center",
                "center = large message in the middle of the screen; topleft = small corner message; none = no on-screen message.");
            AlertChat = Config.Bind("Alerts", "InChat", true,
                "Also put the alert in online admins' chat box (bottom-left, kept in chat history).");
            AlertDiscordWebhook = Config.Bind("Alerts", "DiscordWebhook", "",
                "Optional Discord webhook URL (Channel settings > Integrations > Webhooks). New cheat flags are posted there. Keep it secret.");
            ServerFps = Config.Bind("Performance", "ServerFrameRateCap", 60,
                "Limit the dedicated server's frame rate. Unity servers otherwise run uncapped (thousands of frames per second), " +
                "keeping a core and dozens of worker threads busy for no benefit; on a PC that also runs the game this causes hitching. " +
                "Physics runs at 50 Hz and network sync at 20 Hz, so 60 is plenty. 0 = leave the server uncapped (vanilla behaviour).");
            Admin.EnsureToken();
            Log.LogInfo("Dashboard admin token is in BepInEx/config/" + Guid + ".cfg ([Admin] Token).");

            EventLog.Init(Path.Combine(Paths.BepInExRootPath, "skald"));
            Flags.Init(Path.Combine(Paths.BepInExRootPath, "skald"));
            Bosses.Init(Path.Combine(Paths.BepInExRootPath, "skald"));

            _harmony = new Harmony(Guid);
            // Fail soft: each collector is patched on its own, so a game update that breaks one
            // hook only disables that collector instead of the whole plugin (or the server).
            foreach (var type in new[] { typeof(ZdoDataPatches), typeof(DestroyPatches), typeof(RoutedRpcPatches), typeof(GlobalKeyPatches), typeof(CheatToolPatches) })
            {
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    Log.LogInfo("Collector enabled: " + type.Name);
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Collector disabled: {type.Name} ({ex.GetType().Name}: {ex.Message})");
                }
            }

            _web = new WebServer(HttpBind.Value, HttpPort.Value);
            _web.Start();
        }

        void Update()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) return;

            Perf.Frame(Time.unscaledDeltaTime);
            long t0 = Perf.Now();
            if (!_titleSet) { _titleSet = true; ApplyConsoleTitle(); }
            if (!_fpsApplied)
            {
                _fpsApplied = true;
                if (ServerFps.Value > 0 && Application.isBatchMode)
                {
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = ServerFps.Value;
                    Log.LogInfo($"Server frame rate capped at {ServerFps.Value} fps (config [Performance] ServerFrameRateCap).");
                }
            }
            Admin.Tick();
            Bosses.Tick();
            Heuristics.Tick();
            if (Time.unscaledTime >= _nextPeerPoll)
            {
                _nextPeerPoll = Time.unscaledTime + 3f;
                long t1 = Perf.Now();
                PollPeers(net);
                Perf.Add("poll_peers", t1);
            }
            if (Time.unscaledTime >= _nextFlush)
            {
                _nextFlush = Time.unscaledTime + 5f;
                long t1 = Perf.Now();
                EventLog.Flush();
                Flags.Save();
                Perf.Add("flush_files", t1);
            }
            Perf.Add("update_total", t0);
        }

        void PollPeers(ZNet net)
        {
            var current = new HashSet<long>();
            foreach (var peer in net.GetPeers())
            {
                if (!peer.IsReady() || string.IsNullOrEmpty(peer.m_playerName)) continue;
                current.Add(peer.m_uid);
                if (!_knownPeers.ContainsKey(peer.m_uid))
                {
                    _knownPeers[peer.m_uid] = peer.m_playerName;
                    Players.Remember(peer);
                    var p = peer.GetRefPos();
                    EventLog.Add(new SkaldEvent { Time = DateTime.UtcNow, Type = "join", Actor = peer.m_playerName, ActorId = Players.HostId(peer), X = p.x, Y = p.y, Z = p.z });
                }
            }
            var gone = new List<long>();
            foreach (var kv in _knownPeers)
                if (!current.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var uid in gone)
            {
                EventLog.Add(new SkaldEvent { Time = DateTime.UtcNow, Type = "leave", Actor = _knownPeers[uid] });
                _knownPeers.Remove(uid);
            }
        }

        void OnDestroy()
        {
            EventLog.Flush();
            Flags.Save();
            _web?.Stop();
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>Maps peers and player ids to names.</summary>
    internal static class Players
    {
        static readonly Dictionary<long, string> NameByPlayerId = new Dictionary<long, string>();

        public static void Remember(ZNetPeer peer)
        {
            if (string.IsNullOrEmpty(peer.m_playerName)) return;
            if (peer.m_playerID != 0) NameByPlayerId[peer.m_playerID] = peer.m_playerName;
            // Piece creators store the character's profile id, which lives on the Player ZDO.
            long charId = CharacterPlayerId(peer);
            if (charId != 0) NameByPlayerId[charId] = peer.m_playerName;
        }

        static long CharacterPlayerId(ZNetPeer peer)
        {
            if (peer.m_characterID.IsNone() || ZDOMan.instance == null) return 0;
            var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            return zdo != null ? zdo.GetLong(ZDOVars.s_playerID) : 0;
        }

        public static string HostId(ZNetPeer peer)
        {
            try { return peer.m_socket?.GetHostName(); } catch { return null; }
        }

        public static ZNetPeer PeerByUid(long uid) => uid == 0 ? null : ZNet.instance?.GetPeer(uid);

        public static string NameOfPeer(long uid)
        {
            var peer = PeerByUid(uid);
            if (peer == null) return uid == 0 ? "world" : "peer:" + uid;
            Remember(peer);
            return string.IsNullOrEmpty(peer.m_playerName) ? "peer:" + uid : peer.m_playerName;
        }

        public static string NameOfPlayerId(long playerId)
        {
            if (playerId == 0) return null;
            if (NameByPlayerId.TryGetValue(playerId, out var n)) return n;
            // Not cached yet: look through connected peers.
            var net = ZNet.instance;
            if (net != null)
                foreach (var peer in net.GetPeers())
                {
                    Remember(peer);
                    if (NameByPlayerId.TryGetValue(playerId, out n)) return n;
                }
            return "player:" + playerId;
        }
    }
}
