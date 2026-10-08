using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Core;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using DungeonRunners.Gameplay;
using DungeonRunners.Database;
using DungeonRunners.Engine.Playables;
using System.Security.Cryptography;
using DungeonRunners.Combat;
using DungeonRunners.Networking.EntitySynchInfo;
using DungeonRunners.Infrastructure;

namespace DungeonRunners.Networking
{
public partial class GameServer
    {        private bool TryResolveUseTargetMovingTargetFixedReadOnly(RRConnection conn, out int targetFixedX, out int targetFixedY, out string lostReason)
        {
            targetFixedX = conn.UseTargetMovingTargetFixedX;
            targetFixedY = conn.UseTargetMovingTargetFixedY;
            lostReason = null;
            if (conn.UseTargetMovingFollowConnId != 0)
            {
                if (!_connections.TryGetValue(conn.UseTargetMovingFollowConnId, out var followConn) || followConn == null || !followConn.IsSpawned
                    || !string.Equals(followConn.RuntimeInstanceKey ?? "", conn.RuntimeInstanceKey ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    lostReason = "follow-target-lost";
                    return false;
                }
                if (conn.ActiveUseTargetPlayerConnId == followConn.ConnId
                    && !IsPvpActionTargetAvailable(conn, followConn, conn.ActiveUseTargetFlags))
                {
                    lostReason = "follow-target-dead-or-ineligible";
                    return false;
                }
                if (conn.ActiveUseTargetPlayerConnId == followConn.ConnId)
                    ResolvePvpTargetPosition(followConn, out targetFixedX, out targetFixedY, out _);
                else
                {
                    targetFixedX = followConn.PlayerPosFixedX;
                    targetFixedY = followConn.PlayerPosFixedY;
                }
                return true;
            }
            if (conn.UseTargetMovingEntityId != 0)
            {
                var monster = CombatRuntime.Instance.GetMonster(conn.UseTargetMovingEntityId)
                           ?? CombatRuntime.Instance.GetMonsterByComponent(conn.UseTargetMovingEntityId);
                if (monster != null)
                {
                    if (!monster.IsAlive)
                    {
                        lostReason = "entity-dead";
                        return false;
                    }
                    uint viewerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
                    if (!CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(monster, viewerEntityId, out targetFixedX, out targetFixedY, out _))
                    {
                        lostReason = "entity-lost";
                        return false;
                    }
                    return true;
                }
                if (BlingGnomeRuntime.Instance.TryResolveGnomeTargetPositionFixed(conn, conn.UseTargetMovingEntityId, out targetFixedX, out targetFixedY, out string gnomeLostReason))
                {
                    return true;
                }
                lostReason = gnomeLostReason ?? "entity-lost";
                return false;
            }
            return true;
        }

        private bool TryPrepareZoneJoinRoomRuntime(
            RRConnection conn,
            string source,
            out Zone spawnZone,
            out string zoneName,
            out string instanceKey,
            out uint roomSeed,
            out uint layoutSeed)
        {
            spawnZone = null;
            zoneName = null;
            instanceKey = null;
            roomSeed = 0;
            layoutSeed = 0;

            if (conn == null || !_zones.TryGetValue(conn.CurrentZoneId, out spawnZone))
                return false;

            zoneName = spawnZone.name;
            instanceKey = GetInstanceZoneKey(conn);
            roomSeed = ResolveRuntimeZoneSeed(conn, zoneName);
            layoutSeed = ResolveZoneLayoutSeed(conn, zoneName);

            CombatRuntime.Instance.EnsureRoomRng(instanceKey, roomSeed, "zone-join-room-runtime");
            uint effectiveRoomSeed = CombatRuntime.Instance.GetRoomSeedForInstance(instanceKey);
            if (effectiveRoomSeed != 0)
                roomSeed = effectiveRoomSeed;

            Debug.LogError($"[ZONE-JOIN] Prepared room runtime before player spawn zone='{zoneName}' instance='{instanceKey}' entityManagerOpcode0CSeed=0x{roomSeed:X8} {FormatDungeonLayoutSeedForLog(zoneName, layoutSeed)} source={source ?? "unknown"}");
            return true;
        }

        private static string FormatDungeonLayoutSeedForLog(string zoneName, uint layoutSeed)
        {
            return !string.IsNullOrEmpty(zoneName) && DungeonMazeSpawner.IsProceduralZone(zoneName)
                ? $"dungeonLayoutSeed=0x{layoutSeed:X8}"
                : $"dungeonLayoutSeed=n/a(seedSlot=0x{layoutSeed:X8})";
        }

        private uint ResolveZoneConnectSeed(RRConnection conn, string zoneName)
        {
            PrepareDungeonQuestRooms(conn, zoneName);
            uint seed = ResolveDungeonLayoutSeed(conn, zoneName);
            if (conn != null)
                _pendingZoneConnectSeeds[conn.ConnId] = seed;
            return seed;
        }

        private uint ResolveZoneLayoutSeed(RRConnection conn, string zoneName)
        {
            PrepareDungeonQuestRooms(conn, zoneName);
            if (!string.IsNullOrEmpty(zoneName)
                && (DungeonMazeSpawner.IsProceduralZone(zoneName) || DungeonMazeSpawner.HasStaticEncounterData(zoneName))
                && conn != null
                && _pendingZoneConnectSeeds.TryGetValue(conn.ConnId, out uint seed))
                return seed;

            return ResolveDungeonLayoutSeed(conn, zoneName);
        }

        private uint ResolveDungeonLayoutSeed(RRConnection conn, string zoneName)
        {
            if (conn == null || string.IsNullOrEmpty(zoneName)
                || (!DungeonMazeSpawner.IsProceduralZone(zoneName) && !DungeonMazeSpawner.HasStaticEncounterData(zoneName)))
                return 0;

            string key = GetDungeonLayoutSeedKey(conn, zoneName);
            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group != null && conn.InstanceId == group.GroupId)
            {
                _zoneInstanceLayoutSeeds[key] = group.InstanceSeed;
                return group.InstanceSeed;
            }

            if (_zoneInstanceLayoutSeeds.TryGetValue(key, out uint seed))
                return seed;

            seed = GenerateDungeonLayoutSeed();
            _zoneInstanceLayoutSeeds[key] = seed;
            Debug.LogError($"[LAYOUT-SEED] zone={zoneName} instance={conn.InstanceId:X8} seed=0x{seed:X8} owner={GetSoloDungeonInstanceOwnerKey(conn, zoneName)}");
            return seed;
        }

        private string GetDungeonLayoutSeedKey(RRConnection conn, string zoneName)
        {
            return $"{zoneName ?? string.Empty}_inst{conn?.InstanceId ?? 0}";
        }

        private string GetSoloDungeonInstanceOwnerKey(uint charId, string zoneName)
        {
            string owner = charId != 0 ? $"char{charId}" : "char0";
            return $"{owner}:{zoneName ?? string.Empty}";
        }

        private string GetSoloDungeonInstanceOwnerKey(RRConnection conn, string zoneName)
        {
            uint charId = conn != null ? GetCharSqlId(conn) : 0;
            if (charId != 0)
                return GetSoloDungeonInstanceOwnerKey(charId, zoneName);

            string owner = $"conn{(conn?.ConnId ?? 0)}";
            return $"{owner}:{zoneName ?? string.Empty}";
        }

        private bool IsSoloDungeonMemoryExpired(string key)
        {
            if (string.IsNullOrEmpty(key))
                return true;
            if (!_soloDungeonLastActiveTicks.TryGetValue(key, out uint lastActiveTick))
                return false;
            return unchecked(_combatTick - lastActiveTick) > SoloDungeonMemoryTicks;
        }

        private void ForgetSoloDungeonInstance(string key, string zoneName, uint instanceId, string reason)
        {
            if (string.IsNullOrEmpty(key))
                return;

            _soloDungeonInstanceIds.Remove(key);
            _soloDungeonLastActiveTicks.Remove(key);
            string instanceKey = $"{zoneName ?? string.Empty}_inst{instanceId}";
            string seedKey = $"{zoneName ?? string.Empty}_inst{instanceId}";
            _zoneInstanceLayoutSeeds.Remove(seedKey);
            _zoneInstanceRoomSeeds.Remove(seedKey);
            ZoneSpawner.Instance.ResetZone(instanceKey);
            CombatRuntime.Instance.ClearInstanceMobs(instanceKey);
            ClearDroppedItemsForInstance(zoneName, instanceId, reason);
            Debug.LogError($"[INSTANCE-STATE] zone={zoneName ?? ""} owner={key} instance={instanceId:X8} state=forgot reason={reason ?? "unknown"}");
        }

        private bool HasRememberedSoloDungeonInstance(uint charId, string zoneName, out uint instanceId, out bool expired)
        {
            instanceId = 0;
            expired = false;
            if (charId == 0 || string.IsNullOrEmpty(zoneName))
                return false;

            string key = GetSoloDungeonInstanceOwnerKey(charId, zoneName);
            if (!_soloDungeonInstanceIds.TryGetValue(key, out instanceId))
                return false;

            if (IsSoloDungeonMemoryExpired(key))
            {
                expired = true;
                ForgetSoloDungeonInstance(key, zoneName, instanceId, "memory-expired");
                instanceId = 0;
                return false;
            }

            _soloDungeonLastActiveTicks[key] = _combatTick;
            return true;
        }

        private void TouchSoloDungeonInstance(RRConnection conn, string reason)
        {
            if (conn == null || string.IsNullOrEmpty(conn.CurrentZoneName) || IsPublicZone(conn.CurrentZoneName))
                return;
            if (GroupDirectory.Instance.GetGroupForConn(conn.ConnId) != null)
                return;

            string key = GetSoloDungeonInstanceOwnerKey(conn, conn.CurrentZoneName);
            if (!_soloDungeonInstanceIds.ContainsKey(key))
                return;

            _soloDungeonLastActiveTicks[key] = _combatTick;
            Debug.LogError($"[INSTANCE-STATE] zone={conn.CurrentZoneName} owner={key} instance={conn.InstanceId:X8} state=touch reason={reason ?? "unknown"}");
        }

        private uint AllocateSoloDungeonInstanceId(RRConnection conn, string zoneName)
        {
            string key = GetSoloDungeonInstanceOwnerKey(conn, zoneName);
            if (_soloDungeonInstanceIds.TryGetValue(key, out uint instanceId))
            {
                if (IsSoloDungeonMemoryExpired(key))
                {
                    ForgetSoloDungeonInstance(key, zoneName, instanceId, "allocate-expired");
                }
                else
                {
                    _soloDungeonLastActiveTicks[key] = _combatTick;
                    Debug.LogError($"[INSTANCE-STATE] zone={zoneName ?? ""} owner={key} instance={instanceId:X8} state=late-join activeSolo={_soloDungeonInstanceIds.Count}");
                    return instanceId;
                }
            }

            _nextSoloDungeonInstanceId++;
            if (_nextSoloDungeonInstanceId < 0x80000000u)
                _nextSoloDungeonInstanceId = 0x80000001u;

            _soloDungeonInstanceIds[key] = _nextSoloDungeonInstanceId;
            _soloDungeonLastActiveTicks[key] = _combatTick;
            Debug.LogError($"[INSTANCE-STATE] zone={zoneName ?? ""} owner={key} instance={_nextSoloDungeonInstanceId:X8} state=fresh activeSolo={_soloDungeonInstanceIds.Count}");
            return _nextSoloDungeonInstanceId;
        }

        public int ResetSoloDungeonInstances(RRConnection conn)
        {
            if (conn == null)
                return 0;

            uint charId = GetCharSqlId(conn);
            string ownerPrefix = (charId != 0 ? $"char{charId}" : "char0") + ":";
            var ownerKeys = _soloDungeonInstanceIds.Keys
                .Where(k => k.StartsWith(ownerPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int resetCount = 0;
            foreach (string key in ownerKeys)
            {
                if (!_soloDungeonInstanceIds.TryGetValue(key, out uint instanceId))
                    continue;
                string zoneName = key.Substring(ownerPrefix.Length);
                string instanceKey = $"{zoneName}_inst{instanceId}";
                CombatRuntime.Instance.ClearInstanceMobs(instanceKey);
                ForgetSoloDungeonInstance(key, zoneName, instanceId, "reset-instances");
                resetCount++;
            }

            Debug.LogError($"[GROUP] ResetSoloDungeonInstances conn={conn.ConnId} char={charId:X8} reset {resetCount} solo instance(s)");
            return resetCount;
        }

        private uint GenerateDungeonLayoutSeed()
        {
            byte[] bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            uint seed = BitConverter.ToUInt32(bytes, 0);
            return seed != 0 ? seed : 1u;
        }

        private uint ResolveRuntimeZoneSeed(RRConnection conn, string zoneName)
        {
            if (conn == null)
            {
                Debug.LogError($"[RUNTIME-SEED] conn=<null> zone={zoneName ?? "<null>"} instance=<none> state=blocked reason=missing-connection");
                return 0;
            }

            string instanceKey = GetDungeonLayoutSeedKey(conn, zoneName);
            if (_zoneInstanceRoomSeeds.TryGetValue(instanceKey, out uint cachedRoomSeed) && cachedRoomSeed != 0)
            {
                LogRuntimeZoneSeed(conn, zoneName, instanceKey, cachedRoomSeed, "cached-room-epoch");
                return cachedRoomSeed;
            }
            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group != null)
            {
                if (group.EntityManagerSeed == 0)
                {
                    group.EntityManagerSeed = GenerateDungeonLayoutSeed();
                    Debug.LogError($"[RUNTIME-SEED] generated missing group entity-manager seed groupId={group.GroupId} seed=0x{group.EntityManagerSeed:X8}");
                }

                uint groupSeed = group.EntityManagerSeed != 0 ? group.EntityManagerSeed : 1u;
                _zoneInstanceRoomSeeds[instanceKey] = groupSeed;
                LogRuntimeZoneSeed(conn, zoneName, instanceKey, groupSeed, $"group-room={group.GroupId}");
                return groupSeed;
            }

            if (!_zoneInstanceRoomSeeds.TryGetValue(instanceKey, out uint seed))
            {
                seed = GenerateDungeonLayoutSeed();
                _zoneInstanceRoomSeeds[instanceKey] = seed;
                LogRuntimeZoneSeed(conn, zoneName, instanceKey, seed, "solo-new");
                return seed;
            }

            LogRuntimeZoneSeed(conn, zoneName, instanceKey, seed, "solo-cache");
            return seed;
        }

        private void LogRuntimeZoneSeed(RRConnection conn, string zoneName, string instanceKey, uint seed, string source)
        {
            string key = $"{conn.ConnId}:{zoneName ?? string.Empty}:{instanceKey}:{seed:X8}";
            if (_loggedRuntimeZoneSeeds.Add(key))
                Debug.LogError($"[RUNTIME-SEED] conn={conn.ConnId} zone={zoneName ?? "<null>"} instance='{instanceKey}' seed=0x{seed:X8} source={source}");
        }

    }
}
