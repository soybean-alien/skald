using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Skald
{
    /// <summary>
    /// Server-side cheat detection that does not rely on the client's own cheat tracking (which client mods such as
    /// Server devcommands switch off). Observe-only: nothing is blocked or changed.
    /// </summary>
    internal static class Heuristics
    {
        const float TeleportMeters = 100f;   // jump between two position updates
        const float TeleportWindow = 10f;    // seconds between those updates
        const float PortalRadius = 15f;
        const float AltarRadius = 120f;      // boss must appear this close to one of its own altars

        static readonly Dictionary<ZDOID, (Vector3 pos, float time)> LastPlayerPos = new Dictionary<ZDOID, (Vector3, float)>();
        // Indexes kept up to date incrementally (new/destroyed ZDOs) after ONE full scan at startup, so there are
        // no periodic world scans stalling the server thread.
        static readonly Dictionary<ZDOID, Vector3> Portals = new Dictionary<ZDOID, Vector3>();
        static readonly Dictionary<string, Dictionary<ZDOID, Vector3>> BowlAltars = new Dictionary<string, Dictionary<ZDOID, Vector3>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<Vector3>> LocationAltars = new Dictionary<string, List<Vector3>>(StringComparer.OrdinalIgnoreCase);
        static HashSet<int> _portalHashes;
        static Dictionary<int, string> _bowlPrefabToBoss;
        static bool _scanned;

        // Boss altars that are part of a map location rather than a networked object: boss prefab -> location name token.
        static readonly Dictionary<string, string> AltarLocationToken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Eikthyr", "eikthyr" }, { "gd_king", "gdking" }, { "Bonemass", "bonemass" }, { "Dragon", "dragonqueen" },
            { "GoblinKing", "goblinking" }, { "SeekerQueen", "dvergrbossentrance" }, { "Fader", "fader" }
        };

        static bool HasKnownAltar(string bossPrefab) =>
            AltarLocationToken.ContainsKey(bossPrefab) || (_bowlPrefabToBoss != null && _bowlPrefabToBoss.ContainsValue(bossPrefab));

        internal static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        // ---------------------------------------------------------------- startup scan (game thread)

        public static void Tick()
        {
            if (_scanned || ZDOMan.instance == null || Game.instance == null || ZNetScene.instance == null || ZoneSystem.instance == null) return;
            _scanned = true;
            long t0 = Perf.Now();
            try { InitialScan(); }
            catch (Exception ex) { Collectors.Warn("Heuristics scan", ex); }
            finally { Perf.Add("scan_startup_portals_altars", t0); }
        }

        static void BuildBowlCatalog()
        {
            if (_bowlPrefabToBoss != null || ZNetScene.instance == null) return;
            var map = new Dictionary<int, string>();
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (go == null) continue;
                foreach (var bowl in go.GetComponentsInChildren<OfferingBowl>(true))
                    if (bowl.m_bossPrefab != null) map[go.name.GetStableHashCode()] = bowl.m_bossPrefab.name;
            }
            _bowlPrefabToBoss = map;
            Plugin.Log.LogInfo($"Boss altar prefabs: {map.Count} ({string.Join(", ", map.Values.Distinct())})");
        }

        static void InitialScan()
        {
            BuildBowlCatalog();
            _portalHashes = new HashSet<int>(Game.instance.PortalPrefabHash);
            foreach (var zdo in ObjectsById(ZDOMan.instance).Values) OnNewZdo(zdo);
            // Map locations are fixed once the world is generated: read them once.
            foreach (var inst in ZoneSystem.instance.m_locationInstances.Values)
            {
                string loc = inst.m_location?.m_prefabName;
                if (string.IsNullOrEmpty(loc)) continue;
                foreach (var kv in AltarLocationToken)
                {
                    if (loc.IndexOf(kv.Value, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!LocationAltars.TryGetValue(kv.Key, out var list)) LocationAltars[kv.Key] = list = new List<Vector3>();
                    list.Add(inst.m_position);
                }
            }
            Plugin.Log.LogInfo("Boss altars on this world: " +
                string.Join(", ", LocationAltars.Select(kv => $"{kv.Key} x{kv.Value.Count}").Concat(BowlAltars.Select(kv => $"{kv.Key} x{kv.Value.Count}"))) +
                $"; portals: {Portals.Count}");
        }

        /// <summary>Keep the portal/altar index current. Called for every ZDO the server first sees.</summary>
        public static void OnNewZdo(ZDO zdo)
        {
            if (_portalHashes == null) return;
            int h = zdo.GetPrefab();
            if (_portalHashes.Contains(h)) Portals[zdo.m_uid] = zdo.GetPosition();
            else if (_bowlPrefabToBoss != null && _bowlPrefabToBoss.TryGetValue(h, out var boss))
            {
                if (!BowlAltars.TryGetValue(boss, out var d)) BowlAltars[boss] = d = new Dictionary<ZDOID, Vector3>();
                d[zdo.m_uid] = zdo.GetPosition();
            }
        }

        public static void OnZdoGone(ZDO zdo)
        {
            if (Portals.Remove(zdo.m_uid)) return;
            foreach (var d in BowlAltars.Values) if (d.Remove(zdo.m_uid)) return;
        }

        static List<Vector3> AltarsFor(string bossPrefab)
        {
            var list = new List<Vector3>();
            if (LocationAltars.TryGetValue(bossPrefab, out var l)) list.AddRange(l);
            if (BowlAltars.TryGetValue(bossPrefab, out var d)) list.AddRange(d.Values);
            return list;
        }

        static bool NearPortal(Vector3 p)
        {
            foreach (var x in Portals.Values) if ((x - p).sqrMagnitude < PortalRadius * PortalRadius) return true;
            return false;
        }

        // ---------------------------------------------------------------- teleports

        /// <summary>Called for every Player ZDO update a client sends.</summary>
        public static void OnPlayerData(ZDO zdo, bool isNew, string actorId)
        {
            var pos = zdo.GetPosition();
            float now = Time.unscaledTime;
            if (!isNew && LastPlayerPos.TryGetValue(zdo.m_uid, out var last) && now - last.time < TeleportWindow)
            {
                float dist = Vector3.Distance(new Vector3(last.pos.x, 0, last.pos.z), new Vector3(pos.x, 0, pos.z));
                if (dist > TeleportMeters && !(NearPortal(last.pos) && NearPortal(pos)))
                {
                    string who = zdo.GetString(ZDOVars.s_playerName);
                    Flags.Raise(who, actorId, "teleported without a portal",
                        $"{dist:0} m in {now - last.time:0.0} s, from {last.pos.x:0},{last.pos.z:0} to {pos.x:0},{pos.z:0}", zdo);
                }
            }
            LastPlayerPos[zdo.m_uid] = (pos, now);
            if (LastPlayerPos.Count > 512)
                foreach (var k in LastPlayerPos.Where(kv => now - kv.Value.time > 120f).Select(kv => kv.Key).ToList()) LastPlayerPos.Remove(k);
        }

        // ---------------------------------------------------------------- boss summons

        /// <summary>A boss just appeared. Legit summons happen at that boss's own altar.</summary>
        public static void CheckBossSummon(ZDO boss, string actor, string actorId, string bossPrefab, string bossName)
        {
            if (_bowlPrefabToBoss == null || !_scanned) return;
            var altars = AltarsFor(bossPrefab);
            if (altars.Count == 0)
            {
                // No altar for this boss exists anywhere on the world, so it cannot have been summoned normally.
                if (HasKnownAltar(bossPrefab))
                    Flags.Raise(actor, actorId, "boss spawned without its altar", $"{bossName}: no {bossName} altar exists on this world yet", boss);
                return;
            }
            var p = boss.GetPosition();
            float nearest = altars.Min(a => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(p.x, p.z)));
            if (nearest > AltarRadius)
                Flags.Raise(actor, actorId, "boss spawned without its altar", $"{bossName} appeared {nearest:0} m from the nearest {bossName} altar", boss);
        }
    }

    /// <summary>Server devcommands asks "am I admin?" by sending an unban for "admintest_&lt;id&gt;". Spot that fingerprint.</summary>
    [HarmonyPatch]
    internal static class CheatToolPatches
    {
        [HarmonyPatch(typeof(ZNet), "RPC_Unban"), HarmonyPrefix]
        static void OnUnban(ZRpc rpc, string user)
        {
            if (user == null || !user.StartsWith("admintest_", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var net = ZNet.instance;
                ZNetPeer peer = net?.GetPeers().FirstOrDefault(p => p.m_rpc == rpc);
                string host = peer != null ? Players.HostId(peer) : rpc?.GetSocket()?.GetHostName();
                string name = peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : "player " + host;
                bool admin = net != null && !string.IsNullOrEmpty(host) && net.IsAdmin(host);
                Flags.Raise(name, host, "runs Server devcommands (cheat tool)", admin ? "player is a server admin" : "player is NOT a server admin");
            }
            catch (Exception ex) { Collectors.Warn("Cheat tool check", ex); }
        }
    }
}
