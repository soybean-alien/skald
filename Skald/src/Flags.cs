using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Skald
{
    /// <summary>
    /// Per-character cheat flags from Valheim's own cheat tracking plus Skald's server-side checks.
    /// Observe-only: nothing is blocked. Admins can dismiss a reason (it comes back if it happens again) or mark a
    /// player as trusted (still recorded, never alerted). New flags can alert online admins in-game and a Discord webhook.
    /// </summary>
    internal static class Flags
    {
        internal sealed class Reason
        {
            public int Count;
            public DateTime First, Last, DismissedAt;
            public string Example;
            public DateTime LastEvent;
            public bool Dismissed => DismissedAt != default && DismissedAt >= Last;
        }

        internal sealed class PlayerFlags
        {
            public string ActorId;
            public bool Trusted;
            public readonly Dictionary<string, Reason> Reasons = new Dictionary<string, Reason>();
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, PlayerFlags> ByPlayer = new Dictionary<string, PlayerFlags>(StringComparer.OrdinalIgnoreCase);
        static string _file, _trustedFile;
        static bool _dirty;

        // Items that only exist through developer commands.
        static readonly HashSet<string> DevOnlyItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SwordCheat", "PickaxeCheat", "AxeCheat", "HammerCheat"
        };

        public static bool IsDevOnlyItem(string prefabName) =>
            prefabName != null && (DevOnlyItems.Contains(prefabName) || prefabName.EndsWith("Cheat", StringComparison.OrdinalIgnoreCase));

        public static void Init(string dataDir)
        {
            _file = Path.Combine(dataDir, "flags.tsv");
            _trustedFile = Path.Combine(dataDir, "trusted.txt");
            try
            {
                if (File.Exists(_file))
                    foreach (var line in File.ReadAllLines(_file))
                    {
                        var c = line.Split('\t');
                        if (c.Length < 7) continue;
                        var pf = Get(c[0], c[1]);
                        pf.Reasons[c[2]] = new Reason
                        {
                            Count = int.Parse(c[3], CultureInfo.InvariantCulture),
                            First = ParseDate(c[4]),
                            Last = ParseDate(c[5]),
                            Example = c[6],
                            DismissedAt = c.Length > 7 ? ParseDate(c[7]) : default
                        };
                    }
                if (File.Exists(_trustedFile))
                    foreach (var name in File.ReadAllLines(_trustedFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")))
                        Get(name, null).Trusted = true;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not read flags files: " + ex.Message); }
        }

        static DateTime ParseDate(string s) =>
            string.IsNullOrEmpty(s) ? default : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        static PlayerFlags Get(string player, string actorId)
        {
            if (!ByPlayer.TryGetValue(player, out var pf)) ByPlayer[player] = pf = new PlayerFlags();
            if (!string.IsNullOrEmpty(actorId)) pf.ActorId = actorId;
            return pf;
        }

        /// <summary>Record a flag. Emits a cheat_flag event (and alerts) at most once a minute per player and reason.</summary>
        public static void Raise(string player, string actorId, string reason, string example, ZDO where = null)
        {
            if (string.IsNullOrEmpty(player) || player == "world" || player.StartsWith("peer:")) return;
            if (Plugin.FlagIgnoreAdmins.Value && IsAdmin(actorId)) return;
            var now = DateTime.UtcNow;
            bool emit, trusted;
            lock (Gate)
            {
                var pf = Get(player, actorId);
                trusted = pf.Trusted;
                if (!pf.Reasons.TryGetValue(reason, out var r)) pf.Reasons[reason] = r = new Reason { First = now };
                r.Count++;
                r.Last = now; // a new occurrence un-dismisses the reason
                if (!string.IsNullOrEmpty(example)) r.Example = example;
                emit = (now - r.LastEvent).TotalSeconds >= 60;
                if (emit) r.LastEvent = now;
                _dirty = true;
            }
            if (!emit) return;
            var p = where != null ? where.GetPosition() : UnityEngine.Vector3.zero;
            EventLog.Add(new SkaldEvent
            {
                Time = now, Type = "cheat_flag", Actor = player, ActorId = actorId, Target = reason,
                Detail = trusted ? (example + " (trusted player)").Trim() : example, X = p.x, Y = p.y, Z = p.z
            });
            if (!trusted) Alert(player, reason, example);
        }

        static bool IsAdmin(string actorId)
        {
            try { return !string.IsNullOrEmpty(actorId) && ZNet.instance != null && ZNet.instance.IsAdmin(actorId); }
            catch { return false; }
        }

        // ---------------------------------------------------------------- alerts

        static void Alert(string player, string reason, string example)
        {
            string text = $"Skald flag: {player} - {reason}" + (string.IsNullOrEmpty(example) ? "" : $" ({example})");

            if (Plugin.AlertInGame.Value)
            {
                try
                {
                    var net = ZNet.instance;
                    var style = string.Equals(Plugin.AlertStyle.Value, "topleft", StringComparison.OrdinalIgnoreCase)
                        ? MessageHud.MessageType.TopLeft : MessageHud.MessageType.Center;
                    var sentTo = new List<string>();
                    if (net != null && ZRoutedRpc.instance != null)
                        foreach (var peer in net.GetPeers())
                        {
                            string host = Players.HostId(peer);
                            if (peer.IsReady() && !string.IsNullOrEmpty(host) && net.IsAdmin(host))
                            {
                                if (!string.Equals(Plugin.AlertStyle.Value, "none", StringComparison.OrdinalIgnoreCase))
                                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", (int)style, text);
                                if (Plugin.AlertChat.Value) SendChat(net, peer, text);
                                sentTo.Add(peer.m_playerName);
                            }
                        }
                    Plugin.Log.LogInfo($"[alert] {text} -> in-game to {sentTo.Count} admin(s){(sentTo.Count > 0 ? ": " + string.Join(", ", sentTo) : "")}");
                }
                catch (Exception ex) { Collectors.Warn("In-game alert", ex); }
            }

            string hook = Plugin.AlertDiscordWebhook.Value?.Trim();
            if (!string.IsNullOrEmpty(hook))
                ThreadPool.QueueUserWorkItem(_ => PostDiscord(hook, ":triangular_flag_on_post: **" + Md(player) + "**: " + Md(reason) +
                                                                     (string.IsNullOrEmpty(example) ? "" : "\n> " + Md(example))));
        }

        /// <summary>
        /// Puts a line in one admin's chat box (bottom-left; stays in chat history and is not hidden by the F5 console).
        /// The vanilla chat only shows lines from a known player, so the line is sent under the admin's own identity,
        /// far away so no floating text appears, and prefixed [Skald].
        /// </summary>
        static void SendChat(ZNet net, ZNetPeer peer, string text)
        {
            foreach (var info in net.GetPlayerList())
            {
                if (info.m_characterID != peer.m_characterID) continue;
                var user = new UserInfo { Name = info.m_name, UserId = info.m_userInfo.m_id };
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ChatMessage", new UnityEngine.Vector3(0f, -10000f, 0f),
                    (int)Talker.Type.Normal, user, "[Skald] " + text);
                return;
            }
        }

        static string Md(string s) => (s ?? "").Replace("@", "@​").Replace("*", "\\*").Replace("_", "\\_");

        static void PostDiscord(string url, string content)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var body = Encoding.UTF8.GetBytes("{\"content\":" + Q(content) + ",\"allowed_mentions\":{\"parse\":[]}}");
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 10000;
                req.UserAgent = "Skald-Valheim/" + Plugin.Version;
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (req.GetResponse()) { }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Discord alert failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- admin actions (HTTP thread)

        public static bool Dismiss(string player, string reason)
        {
            lock (Gate)
            {
                if (!ByPlayer.TryGetValue(player ?? "", out var pf)) return false;
                var now = DateTime.UtcNow;
                if (string.IsNullOrEmpty(reason)) foreach (var r in pf.Reasons.Values) r.DismissedAt = now;
                else if (pf.Reasons.TryGetValue(reason, out var r)) r.DismissedAt = now;
                else return false;
                _dirty = true;
                return true;
            }
        }

        public static void SetTrusted(string player, bool trusted)
        {
            if (string.IsNullOrEmpty(player)) return;
            lock (Gate)
            {
                Get(player, null).Trusted = trusted;
                try
                {
                    File.WriteAllLines(_trustedFile, new[] { "# Players whose cheat flags are recorded but never alerted (managed from the dashboard)" }
                        .Concat(ByPlayer.Where(kv => kv.Value.Trusted).Select(kv => kv.Key)));
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Could not write trusted list: " + ex.Message); }
            }
        }

        // ---------------------------------------------------------------- persistence / json

        public static void Save()
        {
            if (_file == null || !_dirty) return;
            var sb = new StringBuilder();
            lock (Gate)
            {
                foreach (var kv in ByPlayer)
                    foreach (var r in kv.Value.Reasons)
                        sb.Append(Clean(kv.Key)).Append('\t').Append(Clean(kv.Value.ActorId)).Append('\t').Append(Clean(r.Key)).Append('\t')
                          .Append(r.Value.Count.ToString(CultureInfo.InvariantCulture)).Append('\t')
                          .Append(r.Value.First.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                          .Append(r.Value.Last.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                          .Append(Clean(r.Value.Example)).Append('\t')
                          .Append(r.Value.DismissedAt == default ? "" : r.Value.DismissedAt.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                _dirty = false;
            }
            try { File.WriteAllText(_file, sb.ToString()); }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not write flags file: " + ex.Message); }
        }

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        public static string Json()
        {
            var sb = new StringBuilder("[");
            lock (Gate)
            {
                bool first = true;
                foreach (var kv in ByPlayer.Where(p => p.Value.Reasons.Count > 0).OrderByDescending(p => p.Value.Reasons.Values.Max(r => r.Last)))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"player\":").Append(Q(kv.Key)).Append(",\"actorId\":").Append(Q(kv.Value.ActorId))
                      .Append(",\"trusted\":").Append(kv.Value.Trusted ? "true" : "false").Append(",\"reasons\":[");
                    bool f2 = true;
                    foreach (var r in kv.Value.Reasons.OrderByDescending(r => r.Value.Last))
                    {
                        if (!f2) sb.Append(',');
                        f2 = false;
                        sb.Append("{\"reason\":").Append(Q(r.Key))
                          .Append(",\"count\":").Append(r.Value.Count)
                          .Append(",\"first\":").Append(Q(r.Value.First.ToString("o", CultureInfo.InvariantCulture)))
                          .Append(",\"last\":").Append(Q(r.Value.Last.ToString("o", CultureInfo.InvariantCulture)))
                          .Append(",\"dismissed\":").Append(r.Value.Dismissed ? "true" : "false")
                          .Append(",\"example\":").Append(Q(r.Value.Example)).Append('}');
                    }
                    sb.Append("]}");
                }
            }
            return sb.Append(']').ToString();
        }

        static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
