using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Skald
{
    /// <summary>
    /// Admin actions from the dashboard: save now, save &amp; restart with an in-game countdown, cancel.
    /// HTTP requests are queued here and executed on the game thread in Tick().
    /// </summary>
    internal static class Admin
    {
        static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
        static readonly int[] WarnAt = { 600, 300, 120, 60, 30, 10, 5, 4, 3, 2, 1 };

        // Countdown state (game thread only, except RestartSecondsLeft which is read by HTTP).
        static float _restartAt = -1f;
        static int _nextWarnIndex;
        static string _requestedBy;
        internal static volatile int RestartSecondsLeft = -1;

        public static string Token => Plugin.AdminToken.Value;

        public static void EnsureToken()
        {
            if (!string.IsNullOrEmpty(Plugin.AdminToken.Value)) return;
            var bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Plugin.AdminToken.Value = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        public static bool CheckToken(string given)
        {
            var expected = Token;
            if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(expected) || given.Length != expected.Length) return false;
            int diff = 0;
            for (int i = 0; i < given.Length; i++) diff |= given[i] ^ expected[i];
            return diff == 0;
        }

        // ---- called from the HTTP thread

        public static void RequestSave(string who) => MainThread.Enqueue(() => SaveNow(who));
        public static void RequestRestart(int delaySeconds, string who) => MainThread.Enqueue(() => StartCountdown(delaySeconds, who));
        public static void RequestCancel(string who) => MainThread.Enqueue(() => Cancel(who));

        // ---- game thread

        public static void Tick()
        {
            while (MainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Plugin.Log.LogWarning("Admin action failed: " + ex); }
            }

            if (_restartAt < 0f) return;
            float left = _restartAt - Time.unscaledTime;
            RestartSecondsLeft = Mathf.Max(0, Mathf.CeilToInt(left));
            while (_nextWarnIndex < WarnAt.Length && left <= WarnAt[_nextWarnIndex])
            {
                Broadcast($"Server restarting in {Describe(WarnAt[_nextWarnIndex])}. The world will be saved first.");
                _nextWarnIndex++;
            }
            if (left <= 0f)
            {
                _restartAt = -1f;
                DoRestart();
            }
        }

        static void SaveNow(string who)
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) return;
            net.Save(sync: false);
            Broadcast("World saved.");
            AddEvent("admin_save", who, "world saved from dashboard");
        }

        static void StartCountdown(int delaySeconds, string who)
        {
            delaySeconds = Mathf.Clamp(delaySeconds, 0, 3600);
            _restartAt = Time.unscaledTime + delaySeconds;
            _requestedBy = who;
            // Skip warnings longer than the chosen delay; announce the total right away.
            _nextWarnIndex = 0;
            while (_nextWarnIndex < WarnAt.Length && WarnAt[_nextWarnIndex] >= delaySeconds) _nextWarnIndex++;
            RestartSecondsLeft = delaySeconds;
            if (delaySeconds > 0)
                Broadcast($"Server restarting in {Describe(delaySeconds)}. The world will be saved first.");
            AddEvent("admin_restart_scheduled", who, $"restart in {Describe(delaySeconds)}");
        }

        static void Cancel(string who)
        {
            if (_restartAt < 0f) return;
            _restartAt = -1f;
            RestartSecondsLeft = -1;
            Broadcast("Server restart cancelled.");
            AddEvent("admin_restart_cancelled", who, null);
        }

        static void DoRestart()
        {
            Broadcast("Server restarting now. Saving world...");
            AddEvent("admin_restart", _requestedBy, Plugin.RestartMode.Value);
            EventLog.Flush();

            string mode = Plugin.RestartMode.Value?.Trim().ToLowerInvariant();
            if (mode == "relaunch")
            {
                try { SpawnRelauncher(); }
                catch (Exception ex) { Plugin.Log.LogError("Could not schedule relaunch, server will only shut down: " + ex.Message); }
            }
            // Application.Quit runs Game.OnApplicationQuit -> ZNet.Shutdown(save: true): a normal, saved shutdown.
            Application.Quit();
        }

        /// <summary>
        /// Writes a helper script that waits for this process to exit, then starts the server again
        /// with the same command line in a new, visible console window (Windows) or detached (Linux).
        /// </summary>
        static void SpawnRelauncher()
        {
            var self = Process.GetCurrentProcess();
            string exe = self.MainModule.FileName;
            string dir = Path.GetDirectoryName(exe);
            string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(Quote));
            string custom = Plugin.RestartCommand.Value?.Trim();
            bool windows = Application.platform == RuntimePlatform.WindowsServer || Application.platform == RuntimePlatform.WindowsPlayer;

            string script = Path.Combine(Path.GetTempPath(), $"skald_relaunch_{self.Id}" + (windows ? ".cmd" : ".sh"));
            if (windows)
            {
                string start = string.IsNullOrEmpty(custom) ? $"start \"Valheim Server (Skald)\" /D \"{dir}\" \"{exe}\" {args}" : custom;
                string appId = Environment.GetEnvironmentVariable("SteamAppId") ?? "892970";
                string log = Path.Combine(Path.GetTempPath(), "skald_relaunch.log");
                File.WriteAllText(script, string.Join("\r\n",
                    "@echo off",
                    $"echo %date% %time% waiting for pid {self.Id} >> \"{log}\"",
                    ":wait",
                    $"tasklist /FI \"PID eq {self.Id}\" 2>NUL | find \" {self.Id} \" >NUL && (ping -n 2 127.0.0.1 >NUL & goto wait)",
                    $"set SteamAppId={appId}",
                    $"cd /d \"{dir}\"",
                    $"echo %date% %time% starting server >> \"{log}\"",
                    start,
                    "(goto) 2>nul & del \"%~f0\"",
                    ""));
                // Unity keeps its child processes in a kill-on-close job, so a normal child would die with the
                // server. Ask WMI to start the helper instead: it runs outside the job and survives the shutdown.
                string ps = "Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='cmd.exe /c \"" +
                            script.Replace("'", "''") + "\"'; CurrentDirectory='" + dir.Replace("'", "''") + "'} | Out-Null";
                var helper = Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + ps.Replace("\"", "\\\"") + "\"")
                    { UseShellExecute = false, CreateNoWindow = true });
                if (helper != null && !helper.WaitForExit(15000)) Plugin.Log.LogWarning("Relaunch helper is slow to start.");
            }
            else
            {
                string start = string.IsNullOrEmpty(custom) ? $"nohup \"{exe}\" {args} >/dev/null 2>&1 &" : custom;
                File.WriteAllText(script,
                    "#!/bin/sh\n" +
                    $"while kill -0 {self.Id} 2>/dev/null; do sleep 1; done\n" +
                    $"cd \"{dir}\"\n" +
                    start + "\n" +
                    "rm -- \"$0\"\n");
                Process.Start(new ProcessStartInfo("/bin/sh", $"\"{script}\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir });
            }
            Plugin.Log.LogInfo("Relaunch scheduled after shutdown.");
        }

        static string Quote(string a) => a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

        static void Broadcast(string text)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.Center, text);
            Plugin.Log.LogInfo("[broadcast] " + text);
        }

        static void AddEvent(string type, string who, string detail) =>
            EventLog.Add(new SkaldEvent { Time = DateTime.UtcNow, Type = type, Actor = who ?? "dashboard admin", Target = "server", Detail = detail });

        static string Describe(int s) =>
            s >= 120 && s % 60 == 0 ? $"{s / 60} minutes" : s == 60 ? "1 minute" : s == 1 ? "1 second" : $"{s} seconds";

        public static string StatusJson()
        {
            var sb = new StringBuilder("{");
            sb.Append("\"restartSecondsLeft\":").Append(RestartSecondsLeft < 0 ? "null" : RestartSecondsLeft.ToString());
            sb.Append(",\"restartMode\":\"").Append(Plugin.RestartMode.Value).Append('"');
            sb.Append('}');
            return sb.ToString();
        }
    }
}
