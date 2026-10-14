using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Skald
{
    /// <summary>
    /// Boss fights: summon → live HP and fighters → killed or despawned, plus world progression (defeated_* keys).
    /// Boss list is read from the game's own prefabs (Character.m_boss), so new bosses are picked up automatically.
    /// </summary>
    internal static class Bosses
    {
        internal sealed class BossInfo { public string Prefab, Name, BaseName, DefeatKey; public float BaseHealth; }

        internal sealed class Fight
        {
            public ZDOID Id;
            public BossInfo Boss;
            public string Summoner, Killer, Outcome = "in progress";
            public DateTime Start, End, LastData;
            public float Hp = -1f, MaxHp = -1f;
            public readonly HashSet<string> Fighters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Vector3 Pos;
        }

        static readonly object Gate = new object();
        static Dictionary<int, BossInfo> _byHash;
        static readonly Dictionary<ZDOID, Fight> Active = new Dictionary<ZDOID, Fight>();
        static readonly List<string> HistoryJson = new List<string>(); // newest last
        static readonly Dictionary<string, float> LastDefeatKey = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static HashSet<string> _defeatedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static List<BossInfo> _catalog = new List<BossInfo>();
        static string _file;
        static float _nextKeySnapshot;
        static bool _worldScanned;
        static readonly System.Reflection.FieldInfo KeysField = HarmonyLib.AccessTools.Field(typeof(ZoneSystem), "m_globalKeys");

        public static void Init(string dataDir)
        {
            _file = Path.Combine(dataDir, "bosses.jsonl");
            try
            {
                if (File.Exists(_file))
                    foreach (var line in File.ReadAllLines(_file).Reverse().Take(200).Reverse())
                        if (line.StartsWith("{")) HistoryJson.Add(line);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not read boss history: " + ex.Message); }
        }

        static void EnsureCatalog()
        {
            if (_byHash != null || ZNetScene.instance == null) return;
            var map = new Dictionary<int, BossInfo>();
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                var c = go != null ? go.GetComponent<Character>() : null;
                if (c == null || !c.m_boss) continue;
                string name = c.m_name;
                try { if (Localization.instance != null) name = Localization.instance.Localize(c.m_name); } catch { }
                if (string.IsNullOrEmpty(name) || name.StartsWith("$") || name.StartsWith("[")) name = Pretty(go.name);
                string baseName = name;
                var phase = System.Text.RegularExpressions.Regex.Match(go.name, @"_p(\d+)$");
                if (phase.Success) name = $"{baseName} (phase {phase.Groups[1].Value})";
                map[go.name.GetStableHashCode()] = new BossInfo { Prefab = go.name, Name = name, BaseName = baseName, DefeatKey = c.m_defeatSetGlobalKey, BaseHealth = c.m_health };
            }
            _byHash = map;
            lock (Gate) _catalog = map.Values.OrderBy(b => b.BaseHealth).ToList();
            Plugin.Log.LogInfo("Boss catalog: " + string.Join(", ", _catalog.Select(b => $"{b.Name} ({b.Prefab})")));
        }

        /// <summary>"TheHive" -> "The Hive", "gd_king" -> "Gd King" (only used when the game has no translation).</summary>
        static string Pretty(string prefab)
        {
            var words = System.Text.RegularExpressions.Regex.Replace(prefab.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words.Trim());
        }

        public static bool IsBoss(int prefabHash)
        {
            EnsureCatalog();
            return _byHash != null && _byHash.ContainsKey(prefabHash);
        }

        static Fight GetOrStart(ZDO zdo, string actor, bool announce)
        {
            if (Active.TryGetValue(zdo.m_uid, out var f)) return f;
            f = new Fight { Id = zdo.m_uid, Boss = _byHash[zdo.GetPrefab()], Summoner = actor, Start = DateTime.UtcNow, Pos = zdo.GetPosition() };
            lock (Gate) Active[zdo.m_uid] = f;
            if (announce)
            {
                EventLog.Add(new SkaldEvent { Time = f.Start, Type = "boss_summoned", Actor = actor, Target = f.Boss.Name, X = f.Pos.x, Y = f.Pos.y, Z = f.Pos.z });
                try { Heuristics.CheckBossSummon(zdo, actor, null, f.Boss.Prefab, f.Boss.Name); }
                catch (Exception ex) { Collectors.Warn("Boss summon check", ex); }
            }
            return f;
        }

        /// <summary>A peer sent data for a boss ZDO (they own it, so they are near the fight).</summary>
        public static void OnBossData(ZDO zdo, bool isNew, string actor)
        {
            var f = GetOrStart(zdo, isNew ? actor : null, announce: isNew);
            lock (Gate)
            {
                float max = zdo.GetFloat(ZDOVars.s_maxHealth, f.Boss.BaseHealth);
                f.MaxHp = max > 0 ? max : f.Boss.BaseHealth;
                f.Hp = zdo.GetFloat(ZDOVars.s_health, f.MaxHp);
                f.Pos = zdo.GetPosition();
                f.LastData = DateTime.UtcNow;
                f.Outcome = "in progress";
                if (!string.IsNullOrEmpty(actor) && !actor.StartsWith("peer:")) f.Fighters.Add(actor);
            }
        }

        public static void OnBossHit(ZDO target, string attacker)
        {
            if (string.IsNullOrEmpty(attacker)) return;
            var f = GetOrStart(target, null, announce: false);
            lock (Gate) { f.Fighters.Add(attacker); f.LastData = DateTime.UtcNow; }
        }

        public static void OnDefeatKey(string key) => LastDefeatKey[key] = Time.unscaledTime;

        public static void OnBossDestroyed(ZDO zdo, string reporter)
        {
            if (!Active.TryGetValue(zdo.m_uid, out var f)) f = GetOrStart(zdo, null, announce: false);
            float hp = zdo.GetFloat(ZDOVars.s_health, f.Hp);
            bool keyRecent = !string.IsNullOrEmpty(f.Boss.DefeatKey) && LastDefeatKey.TryGetValue(f.Boss.DefeatKey, out var t) && Time.unscaledTime - t < 10f;
            bool killed = hp <= 0f || keyRecent;
            string killer = null;
            if (killed)
                killer = RoutedRpcPatches.LastHitter.TryGetValue(zdo.m_uid, out var h) && Time.unscaledTime - h.time < 15f ? h.attacker : reporter;

            lock (Gate)
            {
                f.End = DateTime.UtcNow;
                f.Outcome = killed ? "killed" : "despawned";
                f.Killer = killer;
                f.Hp = killed ? 0f : hp;
                if (!string.IsNullOrEmpty(killer)) f.Fighters.Add(killer);
                Active.Remove(zdo.m_uid);
                var json = FightJson(f);
                HistoryJson.Add(json);
                if (HistoryJson.Count > 200) HistoryJson.RemoveAt(0);
                try { if (_file != null) File.AppendAllText(_file, json + "\n"); }
                catch (Exception ex) { Plugin.Log.LogWarning("Could not write boss history: " + ex.Message); }
            }
            var p = zdo.GetPosition();
            var secs = (int)(f.End - f.Start).TotalSeconds;
            EventLog.Add(new SkaldEvent
            {
                Time = f.End, Type = killed ? "boss_killed" : "boss_despawned", Actor = killed ? killer : reporter, Target = f.Boss.Name,
                Detail = $"{secs / 60}:{secs % 60:00} fight; fighters: {string.Join(", ", f.Fighters)}" + (f.Summoner != null ? $"; summoned by {f.Summoner}" : ""),
                X = p.x, Y = p.y, Z = p.z
            });
        }

        /// <summary>Game thread: refresh the world-progression snapshot.</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextKeySnapshot) return;
            _nextKeySnapshot = Time.unscaledTime + 5f;
            EnsureCatalog();
            if (!_worldScanned && _byHash != null && ZDOMan.instance != null) { _worldScanned = true; ScanWorld(); }
            if (ZoneSystem.instance == null) return;
            if (KeysField?.GetValue(ZoneSystem.instance) is IEnumerable<string> keys)
            {
                var set = new HashSet<string>(keys.Where(k => k.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
                lock (Gate) _defeatedKeys = set;
            }
        }

        /// <summary>
        /// Once at startup: the server keeps every object in the world, loaded or not. Find bosses left alive anywhere
        /// (e.g. summoned and abandoned). After that, bosses are tracked as their ZDOs appear and disappear.
        /// </summary>
        static void ScanWorld()
        {
            long t0 = Perf.Now();
            try { ScanWorld_Impl(); }
            finally { Perf.Add("scan_bosses", t0); }
        }

        static void ScanWorld_Impl()
        {
            if (_byHash == null || ZDOMan.instance == null) return;
            var alive = new HashSet<ZDOID>();
            foreach (var zdo in Heuristics.ObjectsById(ZDOMan.instance).Values)
            {
                if (!_byHash.ContainsKey(zdo.GetPrefab())) continue;
                alive.Add(zdo.m_uid);
                if (!Active.ContainsKey(zdo.m_uid))
                {
                    var f = GetOrStart(zdo, null, announce: false);
                    lock (Gate)
                    {
                        f.MaxHp = zdo.GetFloat(ZDOVars.s_maxHealth, f.Boss.BaseHealth);
                        f.Hp = zdo.GetFloat(ZDOVars.s_health, f.MaxHp);
                        f.Outcome = "alive";
                    }
                }
            }
            lock (Gate)
                foreach (var gone in Active.Keys.Where(k => !alive.Contains(k)).ToList()) Active.Remove(gone);
        }

        static string FightJson(Fight f)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"boss\":").Append(Q(f.Boss.Name)).Append(",\"prefab\":").Append(Q(f.Boss.Prefab))
              .Append(",\"outcome\":").Append(Q(f.Outcome))
              .Append(",\"fighting\":").Append(f.End == default && f.LastData != default && (DateTime.UtcNow - f.LastData).TotalSeconds < 30 ? "true" : "false")
              .Append(",\"start\":").Append(Q(f.Start.ToString("o", CultureInfo.InvariantCulture)))
              .Append(",\"end\":").Append(f.End == default ? "null" : Q(f.End.ToString("o", CultureInfo.InvariantCulture)))
              .Append(",\"seconds\":").Append(((int)((f.End == default ? DateTime.UtcNow : f.End) - f.Start).TotalSeconds).ToString(CultureInfo.InvariantCulture))
              .Append(",\"hpPct\":").Append(f.MaxHp > 0 && f.Hp >= 0 ? Mathf.Clamp(Mathf.Round(100f * f.Hp / f.MaxHp), 0, 100).ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"summoner\":").Append(Q(f.Summoner))
              .Append(",\"killer\":").Append(Q(f.Killer))
              .Append(",\"fighters\":[").Append(string.Join(",", f.Fighters.Select(Q))).Append(']')
              .Append(",\"pos\":[").Append(Mathf.Round(f.Pos.x).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Mathf.Round(f.Pos.y).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Mathf.Round(f.Pos.z).ToString(CultureInfo.InvariantCulture)).Append(']');
            return sb.Append('}').ToString();
        }

        public static string Json()
        {
            var sb = new StringBuilder("{\"active\":[");
            lock (Gate)
            {
                sb.Append(string.Join(",", Active.Values.OrderBy(f => f.Start).Select(FightJson)));
                sb.Append("],\"history\":[");
                sb.Append(string.Join(",", Enumerable.Reverse(HistoryJson)));
                sb.Append("],\"progression\":[");
                // One chip per boss (phases grouped); defeated if any of its defeat keys is set.
                sb.Append(string.Join(",", _catalog.GroupBy(b => b.BaseName)
                    .Where(g => g.Any(b => !string.IsNullOrEmpty(b.DefeatKey)))
                    .Select(g => "{\"boss\":" + Q(g.Key) + ",\"defeated\":" +
                        (g.Any(b => !string.IsNullOrEmpty(b.DefeatKey) && _defeatedKeys.Contains(b.DefeatKey)) ? "true" : "false") + "}")));
                sb.Append(']');
            }
            return sb.Append('}').ToString();
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
