using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Skald
{
    [Flags]
    internal enum PrefabKind { None = 0, Container = 1, Tombstone = 2, Piece = 4, Player = 8, ItemDrop = 16 }

    /// <summary>Caches what kind of object a prefab hash is.</summary>
    internal static class Prefabs
    {
        static readonly Dictionary<int, PrefabKind> Kinds = new Dictionary<int, PrefabKind>();
        static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        static readonly int PlayerHash = "Player".GetStableHashCode();

        public static PrefabKind Kind(int hash)
        {
            if (hash == 0) return PrefabKind.None;
            if (Kinds.TryGetValue(hash, out var k)) return k;
            k = PrefabKind.None;
            var scene = ZNetScene.instance;
            var go = scene != null ? scene.GetPrefab(hash) : null;
            if (go != null)
            {
                if (go.GetComponent<Container>() != null) k |= PrefabKind.Container;
                if (go.GetComponent<TombStone>() != null) k |= PrefabKind.Tombstone;
                if (go.GetComponent<Piece>() != null) k |= PrefabKind.Piece;
                if (go.GetComponent<ItemDrop>() != null) k |= PrefabKind.ItemDrop;
                Names[hash] = go.name;
            }
            if (hash == PlayerHash) k |= PrefabKind.Player;
            Kinds[hash] = k;
            return k;
        }

        public static string Name(int hash)
        {
            Kind(hash);
            return Names.TryGetValue(hash, out var n) ? n : "prefab:" + hash;
        }
    }

    /// <summary>Decodes a container's saved items (ZDO s_items) into name -> count.</summary>
    internal static class ItemCodec
    {
        /// <summary>Appended to item names that carry Valheim's own "cheated" flag (spawned / made from spawned items).</summary>
        public const string CheatTag = " [spawned]";

        public static Dictionary<string, int> Count(byte[] data)
        {
            var result = new Dictionary<string, int>();
            if (data == null || data.Length == 0) return result;
            var inv = new Inventory("skald", null, 32, 32);
            inv.Load(new ZPackage(data));
            foreach (var item in inv.GetAllItems())
            {
                string name = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name ?? "?";
                if (item.m_cheated) name += CheatTag;
                result.TryGetValue(name, out var n);
                result[name] = n + item.m_stack;
            }
            return result;
        }

        public static string Format(Dictionary<string, int> items) =>
            items.Count == 0 ? "(nothing)" : string.Join(", ", items.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"));

        /// <summary>Returns (taken, added) between two snapshots.</summary>
        public static (Dictionary<string, int> taken, Dictionary<string, int> added) Diff(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var taken = new Dictionary<string, int>();
            var added = new Dictionary<string, int>();
            foreach (var kv in before)
            {
                after.TryGetValue(kv.Key, out var now);
                if (now < kv.Value) taken[kv.Key] = kv.Value - now;
            }
            foreach (var kv in after)
            {
                before.TryGetValue(kv.Key, out var was);
                if (kv.Value > was) added[kv.Key] = kv.Value - was;
            }
            return (taken, added);
        }
    }

    internal static class Collectors
    {
        static readonly HashSet<string> WarnedOnce = new HashSet<string>();

        internal static void Warn(string where, Exception ex)
        {
            if (WarnedOnce.Add(where)) Plugin.Log.LogWarning($"{where} failed (further errors hidden): {ex}");
        }

        static SkaldEvent At(ZDO zdo, string type, string actor, string actorId, string target, string detail)
        {
            var p = zdo.GetPosition();
            return new SkaldEvent { Time = DateTime.UtcNow, Type = type, Actor = actor, ActorId = actorId, Target = target, Detail = detail, X = p.x, Y = p.y, Z = p.z };
        }

        /// <summary>Flag the actor if any of these items are spawned (cheat-flagged) or developer-only.</summary>
        static void CheckItems(string actor, string actorId, Dictionary<string, int> items, string reason, ZDO zdo)
        {
            var bad = items.Where(kv => kv.Key.EndsWith(ItemCodec.CheatTag) || Flags.IsDevOnlyItem(kv.Key))
                           .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (bad.Count > 0) Flags.Raise(actor, actorId, reason, ItemCodec.Format(bad), zdo);
        }

        /// <summary>A peer sent us new data for a ZDO. Compare against what we had.</summary>
        public static void OnZdoChanged(ZDO zdo, int oldPrefab, byte[] oldItems, long sender, bool oldCheated, bool oldFly)
        {
            int prefab = zdo.GetPrefab();
            var kind = Prefabs.Kind(prefab);
            bool isNew = oldPrefab == 0;
            string actor = Players.NameOfPeer(sender);
            string actorId = Players.HostId(Players.PeerByUid(sender));

            if (Bosses.IsBoss(prefab)) Bosses.OnBossData(zdo, isNew, actor);
            if (isNew) Heuristics.OnNewZdo(zdo);

            if ((kind & PrefabKind.Container) != 0)
            {
                byte[] newItems = zdo.GetByteArray(ZDOVars.s_items);
                bool tomb = (kind & PrefabKind.Tombstone) != 0;
                if (ReferenceEquals(oldItems, newItems) || (oldItems != null && newItems != null && oldItems.SequenceEqual(newItems)))
                {
                    // unchanged contents
                }
                else if (oldItems == null && !tomb && zdo.GetLong(ZDOVars.s_creator) != 0)
                {
                    // First deposit into a player-built chest: everything in it was just added.
                    var added = ItemCodec.Count(newItems);
                    CheckItems(actor, actorId, added, "stored spawned items", zdo);
                    if (added.Count > 0)
                        EventLog.Add(At(zdo, "chest_add", actor, actorId, Prefabs.Name(prefab),
                            ItemCodec.Format(added) + $" (chest built by {Players.NameOfPlayerId(zdo.GetLong(ZDOVars.s_creator))})"));
                }
                else if (oldItems == null)
                {
                    if (tomb && newItems != null)
                    {
                        string owner = zdo.GetString(ZDOVars.s_ownerName);
                        var dropped = ItemCodec.Count(newItems);
                        EventLog.Add(At(zdo, "death", string.IsNullOrEmpty(owner) ? actor : owner, actorId, "grave",
                            "dropped: " + ItemCodec.Format(dropped)));
                        CheckItems(string.IsNullOrEmpty(owner) ? actor : owner, actorId, dropped, "died carrying spawned items", zdo);
                    }
                    // otherwise: first fill of a new/dungeon chest, not a player action
                }
                else
                {
                    var (taken, added) = ItemCodec.Diff(ItemCodec.Count(oldItems), ItemCodec.Count(newItems));
                    string target = tomb ? "grave of " + zdo.GetString(ZDOVars.s_ownerName) : Prefabs.Name(prefab);
                    string builder = tomb ? null : Players.NameOfPlayerId(zdo.GetLong(ZDOVars.s_creator));
                    string suffix = builder != null ? $" (chest built by {builder})" : "";
                    if (taken.Count > 0) EventLog.Add(At(zdo, tomb ? "grave_take" : "chest_take", actor, actorId, target, ItemCodec.Format(taken) + suffix));
                    if (added.Count > 0) EventLog.Add(At(zdo, tomb ? "grave_add" : "chest_add", actor, actorId, target, ItemCodec.Format(added) + suffix));
                    CheckItems(actor, actorId, taken, "took spawned items", zdo);
                    CheckItems(actor, actorId, added, "stored spawned items", zdo);
                }
            }

            if (isNew && (kind & PrefabKind.Piece) != 0 && zdo.GetLong(ZDOVars.s_creator) != 0)
            {
                EventLog.Add(At(zdo, "built", actor, actorId, Prefabs.Name(prefab), null));
                if (zdo.GetBool(ZDOVars.s_cheated))
                    Flags.Raise(actor, actorId, "built with cheats (no-cost or spawned materials)", Prefabs.Name(prefab), zdo);
            }

            // --- cheat signals Valheim itself tracks
            if ((kind & PrefabKind.Player) != 0)
            {
                Heuristics.OnPlayerData(zdo, isNew, actorId);
                if (zdo.GetBool(ZDOVars.s_debugFly) && (isNew || !oldFly))
                {
                    string who = zdo.GetString(ZDOVars.s_playerName);
                    Flags.Raise(string.IsNullOrEmpty(who) ? actor : who, actorId, "fly mode (debugfly)", null, zdo);
                }
            }
            else if (isNew && (kind & PrefabKind.ItemDrop) != 0)
            {
                var data = new ItemDrop.ItemData();
                ItemDrop.LoadFromZDO(data, zdo);
                string name = Prefabs.Name(prefab);
                if (data.m_cheated || Flags.IsDevOnlyItem(name))
                    Flags.Raise(actor, actorId, "spawned or dropped spawned items", $"{name} x{Math.Max(1, data.m_stack)}", zdo);
            }
            else if ((kind & PrefabKind.Piece) == 0 && !oldCheated && !isNew && zdo.GetBool(ZDOVars.s_cheated))
            {
                // The game marks a creature/object "cheated" when a player hits it in god/ghost/fly mode
                // or with a spawned weapon. Blame the last player we saw hitting it, else the reporting peer.
                string culprit = RoutedRpcPatches.LastHitter.TryGetValue(zdo.m_uid, out var h) && Time.unscaledTime - h.time < 10f ? h.attacker : actor;
                Flags.Raise(culprit, culprit == actor ? actorId : null, "hit things in god/ghost/fly mode or with a spawned weapon", Prefabs.Name(prefab), zdo);
            }
        }

        /// <summary>A peer told us a ZDO is gone.</summary>
        public static void OnZdoDestroyed(ZDO zdo, long sender)
        {
            int prefab = zdo.GetPrefab();
            if (Bosses.IsBoss(prefab)) { Bosses.OnBossDestroyed(zdo, Players.NameOfPeer(sender)); return; }
            if ((Prefabs.Kind(prefab) & PrefabKind.Piece) == 0) return;
            long creator = zdo.GetLong(ZDOVars.s_creator);
            if (creator == 0) return; // not player-built

            string builder = Players.NameOfPlayerId(creator);
            string reporter = Players.NameOfPeer(sender);
            var now = Time.unscaledTime;

            if (RoutedRpcPatches.PendingRemove.TryGetValue(zdo.m_uid, out var rm) && now - rm.time < 10f)
            {
                EventLog.Add(At(zdo, "removed", Players.NameOfPeer(rm.sender), Players.HostId(Players.PeerByUid(rm.sender)), Prefabs.Name(prefab), $"hammer; built by {builder}"));
            }
            else if (RoutedRpcPatches.LastDamage.TryGetValue(zdo.m_uid, out var dmg) && now - dmg.time < 60f)
            {
                EventLog.Add(At(zdo, "destroyed", dmg.attacker, null, Prefabs.Name(prefab),
                    (dmg.ownerReported ? "by damage, reported by area owner (attacker may be a creature)" : "by damage") + $"; built by {builder}"));
            }
            else
            {
                // The sender owned the piece: either they removed it with the hammer locally,
                // or it collapsed/decayed while they owned the area.
                EventLog.Add(At(zdo, "removed", reporter, Players.HostId(Players.PeerByUid(sender)), Prefabs.Name(prefab), $"hammer or collapse; built by {builder}"));
            }
            RoutedRpcPatches.PendingRemove.Remove(zdo.m_uid);
            RoutedRpcPatches.LastDamage.Remove(zdo.m_uid);
        }
    }

    // ---------------------------------------------------------------- patches

    [HarmonyPatch]
    internal static class ZdoDataPatches
    {
        internal static long CurrentSender;

        internal struct State { public bool Track; public int Prefab; public byte[] Items; public float Health; public bool Cheated; public bool Fly; }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyPrefix]
        static void BeforeZdoData(ZRpc rpc)
        {
            CurrentSender = 0;
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) return;
            foreach (var peer in net.GetPeers())
                if (peer.m_rpc == rpc) { CurrentSender = peer.m_uid; break; }
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData"), HarmonyFinalizer]
        static Exception AfterZdoData(Exception __exception)
        {
            CurrentSender = 0;
            return __exception;
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize)), HarmonyPrefix]
        static void BeforeDeserialize(ZDO __instance, out State __state)
        {
            long t0 = Perf.Now();
            try { BeforeDeserialize_Impl(__instance, out __state); }
            finally { Perf.Add("zdo_before", t0); }
        }

        static void BeforeDeserialize_Impl(ZDO __instance, out State __state)
        {
            __state = default;
            if (CurrentSender == 0) return;
            try
            {
                __state.Track = true;
                __state.Prefab = __instance.GetPrefab();
                var kind = Prefabs.Kind(__state.Prefab);
                if ((kind & PrefabKind.Container) != 0)
                    __state.Items = __instance.GetByteArray(ZDOVars.s_items);
                if ((kind & PrefabKind.Piece) != 0)
                    __state.Health = __instance.GetFloat(ZDOVars.s_health, -1f);
                if (__state.Prefab != 0)
                {
                    __state.Cheated = __instance.GetBool(ZDOVars.s_cheated);
                    if ((kind & PrefabKind.Player) != 0) __state.Fly = __instance.GetBool(ZDOVars.s_debugFly);
                }
            }
            catch (Exception ex) { __state.Track = false; Collectors.Warn("ZDO snapshot", ex); }
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize)), HarmonyPostfix]
        static void AfterDeserialize(ZDO __instance, State __state)
        {
            long t0 = Perf.Now();
            try { AfterDeserialize_Impl(__instance, __state); }
            finally { Perf.Add("zdo_after", t0); }
        }

        static void AfterDeserialize_Impl(ZDO __instance, State __state)
        {
            if (!__state.Track) return;
            try
            {
                Collectors.OnZdoChanged(__instance, __state.Prefab, __state.Items, CurrentSender, __state.Cheated, __state.Fly);
                // A piece's health dropping in data sent by its owner = owner-side damage, which
                // never passes through the server as an RPC_Damage.
                if (__state.Prefab != 0 && (Prefabs.Kind(__state.Prefab) & PrefabKind.Piece) != 0)
                {
                    float now = __instance.GetFloat(ZDOVars.s_health, -1f);
                    float t = UnityEngine.Time.unscaledTime;
                    bool knownAttacker = RoutedRpcPatches.LastDamage.TryGetValue(__instance.m_uid, out var prev) && !prev.ownerReported && t - prev.time < 60f;
                    if (now >= 0f && __state.Health >= 0f && now < __state.Health && !knownAttacker)
                        RoutedRpcPatches.LastDamage[__instance.m_uid] = (Players.NameOfPeer(CurrentSender), t, true);
                }
            }
            catch (Exception ex) { Collectors.Warn("ZDO change", ex); }
        }
    }

    [HarmonyPatch]
    internal static class DestroyPatches
    {
        static long _sender;

        [HarmonyPatch(typeof(ZDOMan), "RPC_DestroyZDO"), HarmonyPrefix]
        static void Before(long sender) => _sender = sender;

        [HarmonyPatch(typeof(ZDOMan), "RPC_DestroyZDO"), HarmonyFinalizer]
        static Exception After(Exception __exception) { _sender = 0; return __exception; }

        [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO"), HarmonyPrefix]
        static void OnDestroyed(ZDOID uid)
        {
            long t0 = Perf.Now();
            try { OnDestroyed_Impl(uid); }
            finally { Perf.Add("zdo_destroy", t0); }
        }

        static void OnDestroyed_Impl(ZDOID uid)
        {
            if (_sender == 0 || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                var zdo = ZDOMan.instance.GetZDO(uid);
                if (zdo != null) { Heuristics.OnZdoGone(zdo); Collectors.OnZdoDestroyed(zdo, _sender); }
            }
            catch (Exception ex) { Collectors.Warn("ZDO destroy", ex); }
        }
    }

    /// <summary>Watches RPCs the server relays: hammer removals and damage to pieces.</summary>
    [HarmonyPatch]
    internal static class RoutedRpcPatches
    {
        internal static readonly Dictionary<ZDOID, (long sender, float time)> PendingRemove = new Dictionary<ZDOID, (long, float)>();
        internal static readonly Dictionary<ZDOID, (string attacker, float time)> LastHitter = new Dictionary<ZDOID, (string, float)>();
        internal static readonly Dictionary<ZDOID, (string attacker, float time, bool ownerReported)> LastDamage = new Dictionary<ZDOID, (string, float, bool)>();
        static readonly int RemoveHash = "RPC_Remove".GetStableHashCode();
        static readonly int DamageHash = "RPC_Damage".GetStableHashCode();
        static readonly int PlayerHash = "Player".GetStableHashCode();
        static long _lastMsgId;

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC"), HarmonyPrefix]
        static void OnRoute(ZRoutedRpc.RoutedRPCData rpcData) => Inspect(rpcData);

        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC"), HarmonyPrefix]
        static void OnHandle(ZRoutedRpc.RoutedRPCData data) => Inspect(data);

        static void Inspect(ZRoutedRpc.RoutedRPCData data)
        {
            long t0 = Perf.Now();
            try { Inspect_Impl(data); }
            finally { Perf.Add("routed_rpc", t0); }
        }

        static void Inspect_Impl(ZRoutedRpc.RoutedRPCData data)
        {
            if (data == null || data.m_targetZDO.IsNone()) return;
            if (data.m_methodHash != RemoveHash && data.m_methodHash != DamageHash) return;
            if (data.m_msgID == _lastMsgId) return; // broadcast RPCs pass through both hooks
            _lastMsgId = data.m_msgID;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || data.m_senderPeerID == ZNet.GetUID()) return;
            try
            {
                var target = ZDOMan.instance.GetZDO(data.m_targetZDO);
                if (target == null) return;
                bool isPiece = (Prefabs.Kind(target.GetPrefab()) & PrefabKind.Piece) != 0;
                float now = Time.unscaledTime;
                Prune(now);

                if (data.m_methodHash == RemoveHash)
                {
                    if (isPiece) PendingRemove[data.m_targetZDO] = (data.m_senderPeerID, now);
                    return;
                }
                var pkg = new ZPackage(data.m_parameters.GetArray());
                var hit = new HitData();
                hit.Deserialize(ref pkg);
                if (hit.m_attacker.IsNone()) return;
                var attacker = ZDOMan.instance.GetZDO(hit.m_attacker);
                if (attacker == null || attacker.GetPrefab() != PlayerHash) return;
                string name = attacker.GetString(ZDOVars.s_playerName);
                LastHitter[data.m_targetZDO] = (name, now);
                if (Bosses.IsBoss(target.GetPrefab())) Bosses.OnBossHit(target, name);
                if (isPiece) LastDamage[data.m_targetZDO] = (name, now, false);
            }
            catch (Exception ex) { Collectors.Warn("Routed RPC", ex); }
        }

        static void Prune(float now)
        {
            if (PendingRemove.Count + LastDamage.Count + LastHitter.Count < 4096) return;
            foreach (var k in LastHitter.Where(kv => now - kv.Value.time > 30f).Select(kv => kv.Key).ToList()) LastHitter.Remove(k);
            foreach (var k in PendingRemove.Where(kv => now - kv.Value.time > 30f).Select(kv => kv.Key).ToList()) PendingRemove.Remove(k);
            foreach (var k in LastDamage.Where(kv => now - kv.Value.time > 120f).Select(kv => kv.Key).ToList()) LastDamage.Remove(k);
        }
    }

    [HarmonyPatch]
    internal static class GlobalKeyPatches
    {
        static readonly System.Reflection.FieldInfo KeysField = AccessTools.Field(typeof(ZoneSystem), "m_globalKeys");

        [HarmonyPatch(typeof(ZoneSystem), "RPC_SetGlobalKey"), HarmonyPrefix]
        static void Before(ZoneSystem __instance, long sender, string name)
        {
            long t0 = Perf.Now();
            try { Before_Impl(__instance, sender, name); }
            finally { Perf.Add("global_key", t0); }
        }

        static void Before_Impl(ZoneSystem __instance, long sender, string name)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || string.IsNullOrEmpty(name)) return;
            try
            {
                if (name.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase)) Bosses.OnDefeatKey(name);
                if (name.StartsWith("activeBosses", StringComparison.OrdinalIgnoreCase)) return; // tracked by Bosses
                if (KeysField?.GetValue(__instance) is ICollection<string> keys && keys.Contains(name)) return;
                bool boss = name.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase);
                var peer = Players.PeerByUid(sender);
                var p = peer != null ? peer.GetRefPos() : Vector3.zero;
                EventLog.Add(new SkaldEvent
                {
                    Time = DateTime.UtcNow, Type = boss ? "boss_defeated" : "global_key",
                    Actor = Players.NameOfPeer(sender), ActorId = Players.HostId(peer),
                    Target = boss ? name.Substring("defeated_".Length) : name, Detail = boss ? "first kill on this world (progression)" : null, X = p.x, Y = p.y, Z = p.z
                });
            }
            catch (Exception ex) { Collectors.Warn("Global key", ex); }
        }
    }
}
