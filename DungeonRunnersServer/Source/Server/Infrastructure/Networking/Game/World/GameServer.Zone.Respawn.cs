using System;
using System.Linq;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;
using DungeonRunners.Infrastructure;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private sealed class ResolvedRespawnDestination
        {
            public Zone Zone;
            public string SpawnPoint;
            public int PosFixedX;
            public int PosFixedY;
            public int PosFixedZ;
            public int HeadingFixed;
        }

        private static bool RespawnTracking => ServerDiagnostics.IsEnabled("respawnTracking");

        private void HandleClientRequestRespawn(RRConnection conn, bool zoneErrorTransfer = false)
        {
            using var trace = SimulationParityLedger.EnterOperationPhase("respawn", conn == null ? null : GetInstanceZoneKey(conn), (uint?)conn?.Avatar?.Id);
            if (SimulationParityLedger.OperationTracking)
                SimulationParityLedger.RecordOperation("respawn", "request", new
                {
                    zoneErrorTransfer, SourceAddress = conn?.ZoneSourceAddressId, CompletedAddress = conn?.CompletedZoneErrorRespawnAddress
                });
            Debug.LogError($"[RESPAWN] HandleClientRequestRespawn for {conn?.LoginName ?? ""}");

            try
            {
                if (conn == null || !_playerStates.TryGetValue(conn.ConnId.ToString(), out var playerState) || playerState == null)
                {
                    Debug.LogError("[RESPAWN] state=blocked reason=missing-player-state");
                    return;
                }

                CombatPlayer combatPlayer = conn.Avatar != null && conn.Avatar.Id > 0
                    ? CombatRuntime.Instance.GetPlayer((uint)conn.Avatar.Id)
                    : null;
                bool restoreFullHealth = playerState.CurrentHPWire == 0 && combatPlayer != null && !combatPlayer.IsAlive;
                if (!restoreFullHealth && (!zoneErrorTransfer || playerState.CurrentHPWire == 0 || combatPlayer?.IsAlive != true))
                {
                    Debug.LogError($"[RESPAWN] state=blocked reason=player-alive hp={playerState.CurrentHPWire} combatAlive={combatPlayer?.IsAlive}");
                    return;
                }

                if (zoneErrorTransfer && !restoreFullHealth && conn.CompletedZoneErrorRespawnAddress == conn.ZoneSourceAddressId)
                {
                    if (SimulationParityLedger.OperationTracking)
                        SimulationParityLedger.RecordOperation("respawn", "duplicate", new { conn.ZoneSourceAddressId });
                    Debug.LogError("[RESPAWN] state=ignored reason=zone-error-recovery-already-committed");
                    return;
                }

                if (_duelRuntime.IsInDuel(conn.LoginName))
                {
                    Debug.LogError("[RESPAWN] state=blocked reason=duel-does-not-allow-respawn");
                    return;
                }

                Gameplay.PVPMatchmaking.Match match = Gameplay.PVPMatchmaking.Instance.GetMatchForPlayer(conn.LoginName);
                if (match != null)
                {
                    if (match.Phase != Gameplay.PVPMatchmaking.Match.MatchPhase.Combat || !match.AllowRespawn)
                    {
                        Debug.LogError($"[RESPAWN] state=blocked reason=pvp-rules phase={match.Phase} allowRespawn={match.AllowRespawn}");
                        return;
                    }

                    string entryPoint = match.FreeForAll
                        ? "ffa_respawn"
                        : match.IsRed(conn.LoginName) ? "red_team_respawn" : "blue_team_respawn";
                    if (!TryResolveRespawnDestination(match.ZoneName, entryPoint, out ResolvedRespawnDestination pvpDestination, out string pvpFailure))
                    {
                        Debug.LogError($"[RESPAWN] state=blocked reason={pvpFailure} zone='{match.ZoneName}' spawnPoint='{entryPoint}'");
                        if (RespawnTracking)
                            Debug.LogError($"[RESPAWN-TRACK] phase=resolve result=blocked characterId={GetCharSqlId(conn)} mode=pvp sourceZone='{conn.CurrentZoneName}' sourceInstance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' targetZone='{match.ZoneName}' spawnPoint='{entryPoint}' reason='{pvpFailure}' hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                        return;
                    }
                    string pvpSourceZone = conn.CurrentZoneName ?? string.Empty;
                    string pvpSourceInstance = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn));
                    if (!ChangeZone(
                        conn,
                        pvpDestination.Zone.name,
                        pvpDestination.SpawnPoint,
                        (uint)match.InstanceId,
                        restoreFullHealth: restoreFullHealth,
                        exactSpawnFixedX: pvpDestination.PosFixedX,
                        exactSpawnFixedY: pvpDestination.PosFixedY,
                        exactSpawnFixedZ: pvpDestination.PosFixedZ,
                        exactSpawnHeadingFixed: pvpDestination.HeadingFixed))
                    {
                        Debug.LogError($"[RESPAWN] state=blocked reason=persist-failed zone='{pvpDestination.Zone.name}' spawnPoint='{pvpDestination.SpawnPoint}'");
                        if (RespawnTracking)
                            Debug.LogError($"[RESPAWN-TRACK] phase=persist result=blocked characterId={GetCharSqlId(conn)} mode=pvp sourceZone='{pvpSourceZone}' sourceInstance='{pvpSourceInstance}' targetZone='{pvpDestination.Zone.name}' targetInstance={match.InstanceId:X8} spawnPoint='{pvpDestination.SpawnPoint}' reason=persist-failed hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                        return;
                    }
                    if (zoneErrorTransfer)
                        conn.CompletedZoneErrorRespawnAddress = conn.ZoneSourceAddressId;
                    Debug.LogError($"[RESPAWN] mode=pvp match={match.MatchId} zone={pvpDestination.Zone.name}#{match.InstanceId} entry={pvpDestination.SpawnPoint} fixed=({pvpDestination.PosFixedX},{pvpDestination.PosFixedY},{pvpDestination.PosFixedZ}) headingFixed={pvpDestination.HeadingFixed} hp={playerState.CurrentHPWire} mana={playerState.CurrentManaWire}");
                    if (RespawnTracking)
                        Debug.LogError($"[RESPAWN-TRACK] phase=commit characterId={GetCharSqlId(conn)} mode=pvp sourceZone='{pvpSourceZone}' sourceInstance='{pvpSourceInstance}' targetZone='{pvpDestination.Zone.name}' targetInstance={match.InstanceId:X8} spawnPoint='{pvpDestination.SpawnPoint}' fixed=({pvpDestination.PosFixedX},{pvpDestination.PosFixedY},{pvpDestination.PosFixedZ}) headingFixed={pvpDestination.HeadingFixed} pathMap=walkable hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                    return;
                }

                string respawnZone = GetRespawnZone(conn.CurrentZoneGcType);
                if (string.IsNullOrWhiteSpace(respawnZone))
                {
                    Debug.LogError($"[RESPAWN] state=blocked reason=missing-authored-respawn-zone currentGcType='{conn.CurrentZoneGcType ?? ""}' currentZone='{conn.CurrentZoneName ?? ""}'");
                    if (RespawnTracking)
                        Debug.LogError($"[RESPAWN-TRACK] phase=resolve result=blocked characterId={GetCharSqlId(conn)} mode=pve sourceGc='{conn.CurrentZoneGcType ?? ""}' sourceZone='{conn.CurrentZoneName ?? ""}' sourceInstance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' reason=missing-authored-respawn-zone hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                    return;
                }

                Zone respawnTarget = _zoneOrder.FirstOrDefault(zone => zone.name.Equals(respawnZone, StringComparison.OrdinalIgnoreCase));
                string respawnSpawnPoint = respawnTarget?.respawnSpawnPoint;
                if (!TryResolveRespawnDestination(respawnZone, respawnSpawnPoint, out ResolvedRespawnDestination destination, out string failure))
                {
                    Debug.LogError($"[RESPAWN] state=blocked reason={failure} zone='{respawnZone}' spawnPoint='{respawnSpawnPoint ?? ""}'");
                    if (RespawnTracking)
                        Debug.LogError($"[RESPAWN-TRACK] phase=resolve result=blocked characterId={GetCharSqlId(conn)} mode=pve sourceGc='{conn.CurrentZoneGcType ?? ""}' sourceZone='{conn.CurrentZoneName ?? ""}' sourceInstance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' targetZone='{respawnZone}' spawnPoint='{respawnSpawnPoint ?? ""}' reason='{failure}' hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                    return;
                }

                string sourceGcType = conn.CurrentZoneGcType ?? string.Empty;
                string sourceZone = conn.CurrentZoneName ?? string.Empty;
                string sourceInstance = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn));
                if (!ChangeZone(
                    conn,
                    destination.Zone.name,
                    destination.SpawnPoint,
                    restoreFullHealth: restoreFullHealth,
                    exactSpawnFixedX: destination.PosFixedX,
                    exactSpawnFixedY: destination.PosFixedY,
                    exactSpawnFixedZ: destination.PosFixedZ,
                    exactSpawnHeadingFixed: destination.HeadingFixed))
                {
                    Debug.LogError($"[RESPAWN] state=blocked reason=persist-failed zone='{destination.Zone.name}' spawnPoint='{destination.SpawnPoint}'");
                    if (RespawnTracking)
                        Debug.LogError($"[RESPAWN-TRACK] phase=persist result=blocked characterId={GetCharSqlId(conn)} mode=pve sourceGc='{sourceGcType}' sourceZone='{sourceZone}' sourceInstance='{sourceInstance}' targetZone='{destination.Zone.name}' spawnPoint='{destination.SpawnPoint}' reason=persist-failed hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
                    return;
                }
                if (zoneErrorTransfer)
                    conn.CompletedZoneErrorRespawnAddress = conn.ZoneSourceAddressId;
                Debug.LogError($"[RESPAWN] {sourceGcType} -> {destination.Zone.name} spawnPoint='{destination.SpawnPoint}' fixed=({destination.PosFixedX},{destination.PosFixedY},{destination.PosFixedZ}) headingFixed={destination.HeadingFixed} hp={playerState.CurrentHPWire} mana={playerState.CurrentManaWire}");
                if (SimulationParityLedger.OperationTracking)
                    SimulationParityLedger.RecordOperation("respawn", "commit", new
                    {
                        SourceZone = sourceZone, TargetZone = destination.Zone.name, destination.SpawnPoint, playerState.CurrentHPWire
                    });
                if (RespawnTracking)
                    Debug.LogError($"[RESPAWN-TRACK] phase=commit characterId={GetCharSqlId(conn)} mode=pve sourceGc='{sourceGcType}' sourceZone='{sourceZone}' sourceInstance='{sourceInstance}' targetZone='{destination.Zone.name}' spawnPoint='{destination.SpawnPoint}' fixed=({destination.PosFixedX},{destination.PosFixedY},{destination.PosFixedZ}) headingFixed={destination.HeadingFixed} pathMap=walkable hp={playerState.CurrentHPWire} alive={combatPlayer?.IsAlive}");
            }
            catch (Exception ex)
            {
                if (conn != null)
                {
                    conn.PendingSpawnPreserveAuthoredPosition = false;
                    conn.PendingSpawnHeadingOverride = false;
                    conn.RespawnFullHPPending = false;
                    conn.PendingSpawnFixedX = 0;
                    conn.PendingSpawnFixedY = 0;
                    conn.PendingSpawnFixedZ = 0;
                }
                Debug.LogError($"[RESPAWN] state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
            }
        }

        private bool TryResolveRespawnDestination(string zoneName, string spawnPoint, out ResolvedRespawnDestination destination, out string failure)
        {
            destination = null;
            failure = string.Empty;
            if (string.IsNullOrWhiteSpace(zoneName))
            {
                failure = "missing-authored-respawn-zone";
                return false;
            }
            Zone zone = _zoneOrder.FirstOrDefault(candidate => candidate.name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));
            if (zone == null)
            {
                failure = "unresolved-respawn-zone";
                return false;
            }
            if (string.IsNullOrWhiteSpace(spawnPoint))
            {
                failure = "missing-authored-respawn-spawn-point";
                return false;
            }
            ZoneWaypointData waypoint = AuthoredGameplayCatalog.GetWaypointsForZone(zone.name).FirstOrDefault(candidate =>
                string.Equals(candidate.name, spawnPoint, StringComparison.OrdinalIgnoreCase));
            if (waypoint == null)
            {
                failure = "unresolved-respawn-spawn-point";
                return false;
            }
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(zone.name);
            if (pathMap == null)
            {
                failure = "missing-respawn-pathmap";
                return false;
            }
            if (!pathMap.IsWalkableFixed(waypoint.PosFixedX, waypoint.PosFixedY))
            {
                failure = "respawn-spawn-point-not-walkable";
                return false;
            }
            destination = new ResolvedRespawnDestination
            {
                Zone = zone,
                SpawnPoint = waypoint.name,
                PosFixedX = waypoint.PosFixedX,
                PosFixedY = waypoint.PosFixedY,
                PosFixedZ = waypoint.PosFixedZ,
                HeadingFixed = waypoint.HeadingFixed
            };
            return true;
        }

        private (int x, int y, int z)? GetRespawnPosition(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName))
                return null;
            var zone = _zoneOrder.FirstOrDefault(z =>
                z.name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));
            if (zone == null || string.IsNullOrWhiteSpace(zone.respawnSpawnPoint))
                return null;
            ZoneWaypointData waypoint = AuthoredGameplayCatalog.GetWaypointsForZone(zone.name).FirstOrDefault(candidate =>
                string.Equals(candidate.name, zone.respawnSpawnPoint, StringComparison.OrdinalIgnoreCase));
            return waypoint == null
                ? null
                : (waypoint.PosFixedX, waypoint.PosFixedY, waypoint.PosFixedZ);
        }
        private string GetRespawnZone(string currentZoneGcType)
        {
            string requested = currentZoneGcType?.Trim();
            var zone = _zoneOrder.FirstOrDefault(z =>
                !string.IsNullOrEmpty(requested) &&
                !string.IsNullOrEmpty(z.gcType) &&
                z.gcType.Equals(requested, StringComparison.OrdinalIgnoreCase));

            if (zone == null && !string.IsNullOrEmpty(requested))
            {
                zone = _zoneOrder.FirstOrDefault(z =>
                    !string.IsNullOrEmpty(z.name) &&
                    (z.name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                     ("world." + z.name).Equals(requested, StringComparison.OrdinalIgnoreCase)));
            }

            if (zone != null && !string.IsNullOrEmpty(zone.respawnZone))
                return zone.respawnZone;

            return null;
        }
    }
}
