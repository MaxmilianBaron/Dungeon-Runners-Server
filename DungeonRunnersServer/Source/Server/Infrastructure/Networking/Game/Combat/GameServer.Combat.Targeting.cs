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
    {        private struct ItemDropPlacement
        {
            public bool Success;
            public int FixedX;
            public int FixedY;
            public int FixedZ;
            public int Draws;
            public string Result;
        }

        private static int ConsumeItemAddToWorldHeading(string owner)
        {
            uint headingRaw = RandomStreams.GenerateGlobalStatic(
                "ItemObject.addToWorld.heading",
                owner ?? "ItemObject::addToWorld");
            int headingFixed8 = unchecked((int)((headingRaw % 360u) << 8));
            Debug.LogError($"[ITEM-ADDWORLD-CLIENT] source={owner ?? "unknown"} headingRaw=0x{headingRaw:X8} headingFixed8=0x{headingFixed8:X8} sourceFunction=ItemObject::addToWorld@0x0058A0A0 RandomStreams.GenerateGlobalStatic");
            return headingFixed8;
        }

        private static ItemDropPlacement ResolveItemDropPlacement(
            RRConnection conn,
            string zoneName,
            uint instanceId,
            int sourceFixedX,
            int sourceFixedY,
            int sourceFixedZ,
            int sourceHeadingFixed,
            string owner)
        {
            string pathMapKey = conn != null ? ResolveConnectionInstanceKey(conn) : zoneName;
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
            if (pathMap == null)
            {
                Debug.LogError($"[ITEM-PLACEMENT] source={owner ?? "unknown"} zone='{zoneName ?? ""}' instance={instanceId:X8} pathMap=missing result=blocked draws=0 sourceFunction=ItemObject::SetPositionRandomly@0x0058B400 no-PathMap-no-RNG");
                return new ItemDropPlacement { Success = false, Draws = 0, Result = "blocked" };
            }

            uint headingRaw = RandomStreams.GenerateGlobalStatic(
                "ItemObject::SetPositionRandomly.heading",
                owner ?? "ItemObject::SetPositionRandomly");
            int randomHeading = unchecked((int)((headingRaw % 360u) << 8));
            int reverseHeadingFixed8 = sourceHeadingFixed - 0xB400;
            int[] headings = { randomHeading, sourceHeadingFixed, reverseHeadingFixed8 };
            string[] labels = { "random", "source", "reverse" };

            for (int headingIndex = 0; headingIndex < headings.Length; headingIndex++)
            {
                if (!TryItemDropPlacement(pathMap, sourceFixedX, sourceFixedY, headings[headingIndex], out int placementFixedX, out int placementFixedY, out uint minRadius))
                    continue;

                uint radius = RandomStreams.GenerateGlobalStaticRangeInclusive(
                    minRadius,
                    25u,
                    "ItemObject::SetPositionRandomly.radius",
                    owner ?? "ItemObject::SetPositionRandomly");
                HeadingVectorFixed(headings[headingIndex], out int dirFixedX, out int dirFixedY);
                int radiusFixed = unchecked((int)radius) * UnitMover.Fixed;
                int targetFixedX = sourceFixedX + (int)(((long)dirFixedX * radiusFixed) >> 8);
                int targetFixedY = sourceFixedY + (int)(((long)dirFixedY * radiusFixed) >> 8);
                int placementFixedZ = 0;
                pathMap.TryGetHeightAtFixed(placementFixedX, placementFixedY, out placementFixedZ);
                bool collided = pathMap.CastGroundRayFixed(
                    placementFixedX,
                    placementFixedY,
                    placementFixedZ,
                    targetFixedX,
                    targetFixedY,
                    out int resolvedFixedX,
                    out int resolvedFixedY,
                    out int resolvedFixedZ);
                Debug.LogError($"[ITEM-PLACEMENT-CLIENT] source={owner ?? "unknown"} zone='{zoneName ?? ""}' instance={instanceId:X8} pathMap='{pathMapKey}' result=random branch={labels[headingIndex]} draws=2 headingRaw=0x{headingRaw:X8} headingFixed8=0x{headings[headingIndex]:X8} placementFixed=({placementFixedX},{placementFixedY}) radius={radius} collided={collided} posFixed=({resolvedFixedX},{resolvedFixedY},{resolvedFixedZ}) sourceFunction=ItemObject::SetPositionRandomly@0x0058B400 PathManager::FindFirstValidPointInDir@0x00589880 PathMap::CastGroundRay@0x004C60A0");
                return new ItemDropPlacement { Success = true, FixedX = resolvedFixedX, FixedY = resolvedFixedY, FixedZ = resolvedFixedZ, Draws = 2, Result = labels[headingIndex] };
            }

            Debug.LogError($"[ITEM-PLACEMENT-CLIENT] source={owner ?? "unknown"} zone='{zoneName ?? ""}' instance={instanceId:X8} pathMap='{pathMapKey}' result=source-fallback draws=1 headingRaw=0x{headingRaw:X8} posFixed=({sourceFixedX},{sourceFixedY},{sourceFixedZ}) sourceFunction=ItemObject::SetPositionRandomly@0x0058B400 PathManager::FindFirstValidPointInDir@0x00589880 no-radius-draw");
            return new ItemDropPlacement { Success = true, FixedX = sourceFixedX, FixedY = sourceFixedY, FixedZ = sourceFixedZ, Draws = 1, Result = "native-source" };
        }

        private static int ResolveItemDropGroundHeightFixed(string zoneName, string instanceKey, PathMap pathMap, int worldFixedX, int worldFixedY, int fallbackFixedZ)
        {
            if (pathMap != null && pathMap.TryGetHeightAtFixed(worldFixedX, worldFixedY, out int pathGroundFixedZ))
                return pathGroundFixedZ;

            if (WorldCollision.Instance.TryGetTerrainHeightFixed(zoneName, instanceKey, worldFixedX, worldFixedY, fallbackFixedZ, out int worldGroundFixedZ, out _))
                return worldGroundFixedZ;

            return fallbackFixedZ;
        }

        private static bool TryItemDropPlacement(PathMap pathMap, int sourceFixedX, int sourceFixedY, int headingFixed8, out int placementFixedX, out int placementFixedY, out uint minRadius)
        {
            HeadingVectorFixed(headingFixed8, out int dirFixedX, out int dirFixedY);
            const int startDistanceFixed = 0x500;
            const int searchDistanceFixed = 0x1400;
            int originFixedX = sourceFixedX + (int)(((long)dirFixedX * startDistanceFixed) >> 8);
            int originFixedY = sourceFixedY + (int)(((long)dirFixedY * startDistanceFixed) >> 8);
            placementFixedX = originFixedX;
            placementFixedY = originFixedY;
            minRadius = 0;
            if (pathMap == null)
                return false;
            if (!pathMap.FindFirstValidPointInDirFixed(
                    originFixedX,
                    originFixedY,
                    dirFixedX,
                    dirFixedY,
                    searchDistanceFixed,
                    out placementFixedX,
                    out placementFixedY))
                return false;

            int deltaFixedX = placementFixedX - sourceFixedX;
            int deltaFixedY = placementFixedY - sourceFixedY;
            uint distanceSquaredFixed = unchecked((uint)(
                (((long)deltaFixedX * deltaFixedX) >> 8)
                + (((long)deltaFixedY * deltaFixedY) >> 8)));
            int distanceFixed = UnitMover.TableSquareRoot(distanceSquaredFixed);
            minRadius = unchecked((uint)(distanceFixed >> 8));
            return true;
        }

        private static void HeadingVectorFixed(int headingFixed8, out int xFixed, out int yFixed)
        {
            int tableIndex = (360 - (headingFixed8 >> 8)) % 360;
            if (tableIndex < 0)
                tableIndex += 360;
            xFixed = UnitMover.ZRotateSinFixed(tableIndex);
            yFixed = UnitMover.ZRotateCosFixed(tableIndex);
        }

        private static void HeadingVectorFixed(uint headingFixed8, out int xFixed, out int yFixed)
        {
            int degrees = (int)((headingFixed8 >> 8) % 360u);
            int tableIndex = (360 - degrees) % 360;
            xFixed = UnitMover.ZRotateSinFixed(tableIndex);
            yFixed = UnitMover.ZRotateCosFixed(tableIndex);
        }

        private static bool IsBasicWeaponManipulatorId(byte manipulatorId)
        {
            return manipulatorId == 0x0A || manipulatorId == 0x0B;
        }

        private const int USE_TARGET_UPDATE_MOVING_STOP_DISTANCE_FIXED = 33 * UnitMover.Fixed;
        private const long USE_TARGET_UPDATE_MOVING_STOP_DISTANCE_FIXED_SQ = (long)USE_TARGET_UPDATE_MOVING_STOP_DISTANCE_FIXED * USE_TARGET_UPDATE_MOVING_STOP_DISTANCE_FIXED;
        private const int USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED = 4 * UnitMover.Fixed;
        private const long USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED_SQ = (long)USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED * USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED;
        private const long USE_TARGET_CLIENT_MOVE_AWAY_TOLERANCE_FIXED_SQ = 4L * UnitMover.Fixed * UnitMover.Fixed;
        private const int USE_TARGET_AVATAR_TURN_RATE_PER_SECOND_FIXED = 720 * UnitMover.Fixed;
        private const int USE_TARGET_AVATAR_TURN_RATE_FIXED = USE_TARGET_AVATAR_TURN_RATE_PER_SECOND_FIXED / 30;
        private const int USE_TARGET_PATH_REBUILD_DISTANCE_FIXED = 8 * UnitMover.Fixed;
        private const long USE_TARGET_PATH_REBUILD_DISTANCE_FIXED_SQ = (long)USE_TARGET_PATH_REBUILD_DISTANCE_FIXED * USE_TARGET_PATH_REBUILD_DISTANCE_FIXED;
        private const int USE_TARGET_WAYPOINT_REACHED_DISTANCE_FIXED = 3 * UnitMover.Fixed;
        private const long USE_TARGET_WAYPOINT_REACHED_DISTANCE_FIXED_SQ = (long)USE_TARGET_WAYPOINT_REACHED_DISTANCE_FIXED * USE_TARGET_WAYPOINT_REACHED_DISTANCE_FIXED;
        private const int ACTIVATE_ACTOR_RADIUS_FIXED = 5 * UnitMover.Fixed;
        private const int ACTIVATE_UNIT_TARGET_RADIUS_FIXED = 5 * UnitMover.Fixed;
        private const int ACTIVATE_ITEM_TARGET_RADIUS_FIXED = 5 * UnitMover.Fixed;
        private const int ACTIVATE_RANGE_MARGIN_FIXED = 0x1400;
        private const int UNIT_MOVER_AVATAR_TOUCHING_SPEED_MOD_F32 = 0x3200;
        private const int CLIENT_ENTITY_MOVEMENT_RECORD_BUDGET_PER_FLUSH = int.MaxValue - 1;
        private const int PORTAL_ACTIVATION_DISTANCE_FIXED = 40 * UnitMover.Fixed;
        private const long PORTAL_ACTIVATION_DISTANCE_FIXED_SQ = (long)PORTAL_ACTIVATION_DISTANCE_FIXED * PORTAL_ACTIVATION_DISTANCE_FIXED;

        private static bool ShouldEndClientDrivenTargetApproachForOwnerMove(bool positionChanged, long beforeDistSqFixed, long afterDistSqFixed)
        {
            if (!positionChanged)
                return false;
            return afterDistSqFixed + USE_TARGET_CLIENT_MOVE_AWAY_TOLERANCE_FIXED_SQ >= beforeDistSqFixed;
        }

        private static int WrapHeadingFixed(int value)
        {
            value %= UnitMover.FullCircleFixed;
            if (value < 0) value += UnitMover.FullCircleFixed;
            return value;
        }

        private static long DistanceSqFixed(int ax, int ay, int bx, int by)
        {
            long dx = (long)bx - ax;
            long dy = (long)by - ay;
            return dx * dx + dy * dy;
        }

        private static int ResolveActivationRangeFixed(int targetRadiusFixed)
        {
            return Math.Max(0, targetRadiusFixed) + ACTIVATE_ACTOR_RADIUS_FIXED * 2 + ACTIVATE_RANGE_MARGIN_FIXED;
        }

        private static bool IsWithinActivationRangeFixed(
            RRConnection conn,
            int targetFixedX,
            int targetFixedY,
            int targetRadiusFixed,
            out long distanceSqFixed,
            out int rangeFixed)
        {
            rangeFixed = ResolveActivationRangeFixed(targetRadiusFixed);
            distanceSqFixed = long.MaxValue;
            if (conn == null)
                return false;
            int actorFixedX = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedX : conn.PlayerPosFixedX;
            int actorFixedY = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedY : conn.PlayerPosFixedY;
            if (conn.UseTargetMovingActive && conn.UseTargetMovingHasFixedState)
            {
                actorFixedX = conn.UseTargetMovingFixedX;
                actorFixedY = conn.UseTargetMovingFixedY;
            }
            distanceSqFixed = DistanceSqFixed(actorFixedX, actorFixedY, targetFixedX, targetFixedY);
            return distanceSqFixed <= (long)rangeFixed * rangeFixed;
        }

        private static bool TryResolveWorldEntityActivationTargetRadiusFixed(string gcType, out int targetRadiusFixed)
        {
            targetRadiusFixed = 0;
            if (string.IsNullOrWhiteSpace(gcType))
                return false;
            GCNode node = GCDatabase.Instance.ResolveWithInheritance(gcType);
            GCNode description = node?.GetChild("Object")?.GetChild("Description");
            if (description == null
                || !description.HasProperty("MinX")
                || !description.HasProperty("MinY")
                || !description.HasProperty("MaxX")
                || !description.HasProperty("MaxY"))
                return false;
            int minX = description.GetFixed32("MinX");
            int minY = description.GetFixed32("MinY");
            int maxX = description.GetFixed32("MaxX");
            int maxY = description.GetFixed32("MaxY");
            int extentX = maxX - minX;
            int extentY = maxY - minY;
            if (extentX < 0 || extentY < 0)
                return false;
            targetRadiusFixed = Math.Max(extentX, extentY) / 2;
            return true;
        }

        private static void WriteInt32(byte[] data, int offset, int value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static byte BuildUseTargetMovingFlags(int prevHeading, int heading, bool terminal, bool touchingMonsters)
        {
            byte flags = 0;
            if (terminal) flags |= 0x01;
            if (prevHeading != heading) flags |= 0x02;
            if (touchingMonsters) flags |= 0x04;
            return (byte)(flags & 0x07);
        }

        private static bool ResolveUseTargetMovingTouchingMonsters(RRConnection conn)
        {
            if (conn?.Avatar == null || conn.Avatar.Id <= 0)
                return false;
            CombatPlayer player = CombatRuntime.Instance.GetPlayer((uint)conn.Avatar.Id);
            return player?.OwnerAckFollowClientTouchingMonsters == true;
        }

        private static byte[] BuildUseTargetMovingRecord(byte flags, int heading, int posX, int posY)
        {
            var data = new byte[UnitMoverUpdateRecordSize];
            data[0] = (byte)(flags & 0x07);
            WriteInt32(data, 1, heading);
            WriteInt32(data, 5, posX);
            WriteInt32(data, 9, posY);
            return data;
        }

        private static void SyncUseTargetMovingFixedState(RRConnection conn)
        {
            if (conn == null) return;
            bool actionMirroredUnitMover = conn.UseTargetMovingActionMirrored && conn.HasOwnerAckFollowClientPosition;
            conn.UseTargetMovingFixedX = actionMirroredUnitMover
                ? conn.OwnerAckFollowClientPosFixedX
                : conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedX : conn.PlayerPosFixedX;
            conn.UseTargetMovingFixedY = actionMirroredUnitMover
                ? conn.OwnerAckFollowClientPosFixedY
                : conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedY : conn.PlayerPosFixedY;
            conn.UseTargetMovingFixedZ = actionMirroredUnitMover
                ? conn.OwnerAckFollowClientPosFixedZ
                : conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedZ : conn.PlayerPosFixedZ;
            int headingFixed = actionMirroredUnitMover
                ? conn.OwnerAckFollowClientHeadingFixed
                : conn.HasLivePlayerPosition ? conn.LivePlayerHeadingFixed : conn.PlayerHeadingFixed;
            conn.UseTargetMovingHeadingFixed = WrapHeadingFixed(headingFixed);
            conn.UseTargetMovingHasFixedState = true;
        }

        private bool PrimeUseTargetMovingHeading(RRConnection conn, int targetFixedX, int targetFixedY)
        {
            if (conn == null || !conn.UseTargetMovingHasFixedState) return false;
            int dx = targetFixedX - conn.UseTargetMovingFixedX;
            int dy = targetFixedY - conn.UseTargetMovingFixedY;
            if (dx == 0 && dy == 0) return false;
            int heading = UnitMover.VectorToHeadingFixed(dx, dy);
            conn.UseTargetMovingHeadingFixed = heading;
            conn.PlayerHeadingFixed = heading;
            conn.LivePlayerHeadingFixed = heading;
            if (conn.HasReflectedAvatarPosition)
                conn.ReflectedAvatarHeadingFixed = heading;
            if (conn.HasOwnerAckFollowClientPosition)
                conn.OwnerAckFollowClientHeadingFixed = heading;
            if (conn.HasUnitFollowClientPosition && CanonicalActionOwnsFollowClientFacing(conn))
                conn.UnitFollowClientHeadingFixed = heading;
            return true;
        }

        private bool UpdateUseTargetActionFacingFromOwnerAck(RRConnection conn, int targetFixedX, int targetFixedY)
        {
            if (conn == null)
                return false;
            int actorFixedX = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedX
                : conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarPosFixedX
                    : conn.PlayerPosFixedX;
            int actorFixedY = conn.HasOwnerAckFollowClientPosition
                ? conn.OwnerAckFollowClientPosFixedY
                : conn.HasReflectedAvatarPosition
                    ? conn.ReflectedAvatarPosFixedY
                    : conn.PlayerPosFixedY;
            int dx = targetFixedX - actorFixedX;
            int dy = targetFixedY - actorFixedY;
            if (dx == 0 && dy == 0)
                return false;
            int heading = UnitMover.VectorToHeadingFixed(dx, dy);
            conn.UseTargetMovingHeadingFixed = heading;
            conn.PlayerHeadingFixed = heading;
            conn.LivePlayerHeadingFixed = heading;
            if (conn.HasReflectedAvatarPosition)
                conn.ReflectedAvatarHeadingFixed = heading;
            if (conn.HasOwnerAckFollowClientPosition)
                conn.OwnerAckFollowClientHeadingFixed = heading;
            if (conn.HasUnitFollowClientPosition && CanonicalActionOwnsFollowClientFacing(conn))
                conn.UnitFollowClientHeadingFixed = heading;
            return true;
        }

        private bool UpdateUsePositionActionFacingFromUnitBehavior(RRConnection conn)
        {
            if (conn == null || !conn.UsePositionActionMirrored)
                return false;
            int actorFixedX = conn.HasUnitFollowClientPosition
                ? conn.UnitFollowClientPosFixedX
                : conn.HasOwnerAckFollowClientPosition
                    ? conn.OwnerAckFollowClientPosFixedX
                    : conn.HasReflectedAvatarPosition
                        ? conn.ReflectedAvatarPosFixedX
                        : conn.PlayerPosFixedX;
            int actorFixedY = conn.HasUnitFollowClientPosition
                ? conn.UnitFollowClientPosFixedY
                : conn.HasOwnerAckFollowClientPosition
                    ? conn.OwnerAckFollowClientPosFixedY
                    : conn.HasReflectedAvatarPosition
                        ? conn.ReflectedAvatarPosFixedY
                        : conn.PlayerPosFixedY;
            int dx = conn.UsePositionActionTargetFixedX - actorFixedX;
            int dy = conn.UsePositionActionTargetFixedY - actorFixedY;
            if (dx == 0 && dy == 0)
                return false;
            int heading = UnitMover.VectorToHeadingFixed(dx, dy);
            conn.PlayerHeadingFixed = heading;
            conn.LivePlayerHeadingFixed = heading;
            conn.ReflectedAvatarHeadingFixed = heading;
            if (conn.HasOwnerAckFollowClientPosition)
                conn.OwnerAckFollowClientHeadingFixed = heading;
            if (conn.HasUnitFollowClientPosition && CanonicalActionOwnsFollowClientFacing(conn))
                conn.UnitFollowClientHeadingFixed = heading;
            return true;
        }

        private bool ApplyUsePositionFollowClientGate(RRConnection conn, uint simulationTick)
        {
            if (conn.UsePositionActionStoppedFollowClient)
                return true;
            int queuedUnitFollowClientSamplesAtAdmission = Math.Max(0, conn.UsePositionActionAdmissionFollowClientRecords);
            int queuedUnitFollowClientSamples = conn.UnitFollowClientMovementSamples.Count;
            if (queuedUnitFollowClientSamples > 0)
            {
                SyncOwnerAckFollowClientCurrentState(conn);
                SyncUnitFollowClientCurrentState(conn);
                SyncReflectedAvatarCombatPosition(conn);
                Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=wait-following-client conn={conn.ConnId} tick={simulationTick} ownerQueued={conn.OwnerAckFollowClientMovementSamples.Count} reflectedQueued={conn.PendingReflectedAvatarMovementSamples.Count + conn.HeldReflectedAvatarMovementSamples.Count + conn.AdmittedReflectedAvatarMovementSamples.Count + conn.ReadyReflectedAvatarMovementSamples.Count} unitQueuedAtAdmission={queuedUnitFollowClientSamplesAtAdmission} unitQueuedNow={queuedUnitFollowClientSamples} ownerMode={conn.OwnerUnitBehaviorMoverMode} reflectedMode={conn.ReflectedAvatarUnitMoverMode} sourceFunction=UsePosition::start@0x00546F80->UsePosition::States@0x00547360 state=0x1D");
                return false;
            }
            bool writerStoppedFollowClient = conn.UnitFollowClientWriterStateInitialized
                && conn.UnitFollowClientActionActive
                && !conn.UnitFollowClientFollowingClient;
            if (writerStoppedFollowClient)
            {
                int fixedX = conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedX
                    : conn.HasOwnerAckFollowClientPosition
                        ? conn.OwnerAckFollowClientPosFixedX
                        : conn.PlayerPosFixedX;
                int fixedY = conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedY
                    : conn.HasOwnerAckFollowClientPosition
                        ? conn.OwnerAckFollowClientPosFixedY
                        : conn.PlayerPosFixedY;
                int fixedZ = conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedZ
                    : conn.HasOwnerAckFollowClientPosition
                        ? conn.OwnerAckFollowClientPosFixedZ
                        : conn.PlayerPosFixedZ;
                int headingFixed = conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientHeadingFixed
                    : conn.HasOwnerAckFollowClientPosition
                        ? conn.OwnerAckFollowClientHeadingFixed
                        : conn.PlayerHeadingFixed;
                StopFollowingClientAtCurrentPosition(
                    conn,
                    fixedX,
                    fixedY,
                    fixedZ,
                    headingFixed,
                    "UsePosition::States@0x00547360+0x294->UnitBehavior::StopMoving@0x005202D0");
                conn.UsePositionActionStoppedFollowClient = true;
                SyncOwnerAckFollowClientCurrentState(conn);
                SyncUnitFollowClientCurrentState(conn);
                SyncReflectedAvatarCombatPosition(conn);
                Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=stopped-at-writer-action-start conn={conn.ConnId} tick={simulationTick} unitQueuedAtAdmission={queuedUnitFollowClientSamplesAtAdmission} unitQueuedNow={conn.UnitFollowClientMovementSamples.Count} positionFixed=({fixedX},{fixedY},{fixedZ}) headingFixed={headingFixed} ownerMode={conn.OwnerUnitBehaviorMoverMode} reflectedMode={conn.ReflectedAvatarUnitMoverMode} sourceFunction=UsePosition::States@0x00547360+0x294->UnitBehavior::StopMoving@0x005202D0");
                return true;
            }
            SyncOwnerAckFollowClientCurrentState(conn);
            SyncUnitFollowClientCurrentState(conn);
            SyncReflectedAvatarCombatPosition(conn);
            Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=unit-queue-drained conn={conn.ConnId} tick={simulationTick} unitQueuedAtAdmission={queuedUnitFollowClientSamplesAtAdmission} unitQueuedNow={conn.UnitFollowClientMovementSamples.Count} ownerMode={conn.OwnerUnitBehaviorMoverMode} reflectedMode={conn.ReflectedAvatarUnitMoverMode} sourceFunction=UsePosition::States@0x00547360 state=0x1D->0x02");
            return true;
        }

        private void StopPlayerUsePositionAction(
            RRConnection conn,
            uint simulationTick,
            bool cancelWeapon,
            string source,
            bool peerCancelPacketOwnsTermination = false)
        {
            if (conn == null || !conn.UsePositionActionMirrored)
                return;
            byte sessionId = conn.UsePositionActionSessionId;
            byte manipulatorId = conn.UsePositionManipulatorId;
            bool weaponAction = conn.UsePositionWeaponAction;
            bool actionStoppedRemarry = conn.UsePositionActionMirrored;
            bool stoppedFollowClient = conn.UsePositionActionStoppedFollowClient;
            if (weaponAction)
            {
                if (cancelWeapon)
                    Combat.WeaponUseRuntime.Instance.CancelUsePositionAction(conn.ConnId.ToString(), source);
                else
                    Combat.WeaponUseRuntime.Instance.CompleteUsePositionAction(conn.ConnId.ToString(), source);
            }
            if (!peerCancelPacketOwnsTermination)
            {
                ArmPeerUsePositionTermination(conn, sessionId, simulationTick);
                MarkPeerUsePositionTerminated(conn, sessionId, simulationTick);
            }
            conn.UsePositionActionMirrored = false;
            conn.UsePositionActionStoppedFollowClient = false;
            conn.UsePositionActionComponentId = 0;
            conn.UsePositionManipulatorId = 0;
            conn.UsePositionActionSessionId = 0;
            conn.UsePositionActionTargetFixedX = 0;
            conn.UsePositionActionTargetFixedY = 0;
            conn.UsePositionActionTargetFixedZ = 0;
            conn.UsePositionActionApplyTick = 0;
            conn.UsePositionActionBusyUntilTick = 0;
            conn.UsePositionWeaponAction = false;
            conn.UsePositionActionAdmissionFollowClientRecords = 0;
            conn.OwnerUnitBehaviorMoverMode = UnitMover.FollowClientMode;
            conn.ReflectedAvatarUnitMoverMode = UnitMover.FollowClientMode;
            SyncOwnerAckFollowClientCurrentState(conn);
            SyncUnitFollowClientCurrentState(conn);
            SyncReflectedAvatarCombatPosition(conn);
            if (actionStoppedRemarry)
                RemarryClientToLogicalMovement(conn, "Behavior::terminate@0x00515CC0->UnitBehavior::onActionStopped@0x005205E0");
            Debug.LogError($"[PLAYER-ACTION-TERMINATE] action=UsePosition conn={conn.ConnId} session={sessionId} manipulatorId={manipulatorId} weapon={weaponAction} cancelled={cancelWeapon} tick={simulationTick} stoppedFollowClient={stoppedFollowClient} remarry={actionStoppedRemarry} source={source ?? "unknown"} sourceFunction=Behavior::terminate@0x00515CC0");
        }

        private void UpdateUsePositionActionFacing(uint playerEntityId, uint simulationTick)
        {
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            if (conn == null || !conn.UsePositionActionMirrored)
                return;
            if (!ApplyUsePositionFollowClientGate(conn, simulationTick))
                return;
            if (conn.UsePositionWeaponAction)
            {
                if (!Combat.WeaponUseRuntime.Instance.IsUsePositionBusy(playerEntityId))
                {
                    StopPlayerUsePositionAction(conn, simulationTick, false, "Weapon::isBusy-false");
                    return;
                }
            }
            else if (conn.UsePositionActionBusyUntilTick == 0
                || simulationTick >= conn.UsePositionActionBusyUntilTick - 1)
            {
                StopPlayerUsePositionAction(conn, simulationTick, false, "ActiveSkill::isBusy-false");
                return;
            }
            bool updated = UpdateUsePositionActionFacingFromUnitBehavior(conn);
            Debug.LogError($"[USE-POSITION-FACING] state=update conn={conn.ConnId} component={conn.UsePositionActionComponentId} manipulator={conn.UsePositionManipulatorId} session={conn.UsePositionActionSessionId} tick={simulationTick} busyUntil={conn.UsePositionActionBusyUntilTick} weapon={conn.UsePositionWeaponAction} stoppedFollowClient={conn.UsePositionActionStoppedFollowClient} admissionQueued={conn.UsePositionActionAdmissionFollowClientRecords} actorFixed=({conn.UnitFollowClientPosFixedX},{conn.UnitFollowClientPosFixedY},{conn.UnitFollowClientPosFixedZ}) targetFixed=({conn.UsePositionActionTargetFixedX},{conn.UsePositionActionTargetFixedY},{conn.UsePositionActionTargetFixedZ}) headingFixed={conn.UnitFollowClientHeadingFixed} updated={updated} sourceFunction=UsePosition::States@0x00547360->UnitBehavior::FaceTarget@0x00520200");
        }

        private static ulong GetOwnerAckFollowClientPendingRecords(RRConnection conn)
        {
            if (conn == null)
                return 0;
            if (conn.OwnerAckFollowClientAppliedOrdinal > conn.OwnerAckFollowClientReceivedOrdinal)
                throw new InvalidOperationException("Owner ACK FollowClient ordinal inversion");
            return conn.OwnerAckFollowClientReceivedOrdinal - conn.OwnerAckFollowClientAppliedOrdinal;
        }

        private static bool ActionMirroredMoveToPointOwnsUnitMover(RRConnection conn)
        {
            return conn != null
                && conn.UseTargetMovingActive
                && conn.UseTargetMovingActionMirrored
                && conn.UseTargetMovingMoverStarted
                && conn.UseTargetMovingMoverActive;
        }

        private bool CanonicalActionOwnsUnitMover(RRConnection conn)
        {
            if (conn == null)
                return false;
            if (conn.UseTargetMovingActive
                && conn.UseTargetMovingActionMirrored
                && conn.UseTargetMovingUnitFollowClientWaitActive)
                return false;
            RRConnection viewer = ResolveCanonicalUnitFollowClientViewer(conn);
            if (viewer == null || !conn.UnitFollowClientWriterStateInitialized)
                return ActionMirroredMoveToPointOwnsUnitMover(conn) || conn.UsePositionActionMirrored;
            return conn.UnitFollowClientActionActive && !conn.UnitFollowClientFollowingClient;
        }

        private bool CanonicalActionMirroredMoveToPointOwnsUnitMover(RRConnection conn)
        {
            return ActionMirroredMoveToPointOwnsUnitMover(conn) && CanonicalActionOwnsUnitMover(conn);
        }

        private bool CanonicalActionOwnsFollowClientFacing(RRConnection conn)
        {
            return CanonicalActionOwnsUnitMover(conn);
        }

        private static int ClearReflectedAvatarFollowClientQueueForActionMoveToPoint(RRConnection conn)
        {
            int discardedRecords = checked(
                conn.PendingReflectedAvatarMovementSamples.Count
                + conn.HeldReflectedAvatarMovementSamples.Count
                + conn.AdmittedReflectedAvatarMovementSamples.Count
                + conn.ReadyReflectedAvatarMovementSamples.Count);
            conn.PendingReflectedAvatarMovementSamples.Clear();
            conn.HeldReflectedAvatarMovementSamples.Clear();
            conn.AdmittedReflectedAvatarMovementSamples.Clear();
            conn.ReadyReflectedAvatarMovementSamples.Clear();
            conn.ReflectedAvatarMovementStreamAdmitted = false;
            conn.ReflectedAvatarMovementTerminalRestartPending = false;
            conn.ReflectedAvatarMovementPostAdmissionBarrierPending = false;
            conn.ReflectedAvatarMovementIdleBatchBarrierPending = false;
            conn.ReflectedAvatarMovementAppliedOrdinal = conn.ReflectedAvatarMovementReceivedOrdinal;
            conn.ReflectedAvatarUnitMoverMode = UnitMover.StoppedMode;
            conn.ReflectedAvatarMovingThisFrame = false;
            return discardedRecords;
        }

        private void StopFollowingClientForActionMoveToPoint(RRConnection conn)
        {
            if (conn == null || !conn.UseTargetMovingHasFixedState)
                return;
            StopFollowingClientAtCurrentPosition(
                conn,
                conn.UseTargetMovingFixedX,
                conn.UseTargetMovingFixedY,
                conn.UseTargetMovingFixedZ,
                conn.UseTargetMovingHeadingFixed,
                "UnitBehavior::MoveToPoint@0x00520260");
        }

        private void StopFollowingClientAtCurrentPosition(
            RRConnection conn,
            int fixedX,
            int fixedY,
            int fixedZ,
            int headingFixed,
            string sourceFunction)
        {
            if (conn == null)
                return;
            ulong discardedOwnerRecords = GetOwnerAckFollowClientPendingRecords(conn);
            int discardedReflectedRecords = ClearReflectedAvatarFollowClientQueueForActionMoveToPoint(conn);
            conn.OwnerAckFollowClientMovementSamples.Clear();
            conn.OwnerAckFollowClientAppliedOrdinal = conn.OwnerAckFollowClientReceivedOrdinal;
            SetReflectedAvatarPosition(
                conn,
                fixedX,
                fixedY,
                fixedZ,
                headingFixed,
                false);
            SetOwnerAckFollowClientPosition(
                conn,
                fixedX,
                fixedY,
                fixedZ,
                headingFixed,
                false);
            conn.UseTargetMovingUnitFollowClientWaitActive = false;
            conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal = 0;
            conn.OwnerUnitBehaviorMoverMode = UnitMover.StoppedMode;
            SyncReflectedAvatarCombatPosition(conn);
            Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=stop-following-client conn={conn.ConnId} tick={_combatTick} discardedOwnerRecords={discardedOwnerRecords} discardedReflectedRecords={discardedReflectedRecords} ownerReceivedOrdinal={conn.OwnerAckFollowClientReceivedOrdinal} ownerAppliedOrdinal={conn.OwnerAckFollowClientAppliedOrdinal} unitReceivedOrdinal={conn.UnitFollowClientReceivedOrdinal} unitAppliedOrdinal={conn.UnitFollowClientAppliedOrdinal} reflectedReceivedOrdinal={conn.ReflectedAvatarMovementReceivedOrdinal} reflectedAppliedOrdinal={conn.ReflectedAvatarMovementAppliedOrdinal} positionFixed=({conn.OwnerAckFollowClientPosFixedX},{conn.OwnerAckFollowClientPosFixedY},{conn.OwnerAckFollowClientPosFixedZ}) headingFixed={conn.OwnerAckFollowClientHeadingFixed} sourceFunction={sourceFunction}->UnitBehavior::StopFollowingClient@0x005203A0->UnitMover::StopMoving@0x00536190");
        }

        private bool StartUseTargetMoving(RRConnection conn, int targetFixedX, int targetFixedY, int followConnId = 0, ushort entityId = 0, bool mirrorWithAction = false, int admissionTicks = 0, bool includeQueuedUnitFollowClientRecords = false, ulong unitFollowClientWaitThroughOverride = ulong.MaxValue, bool awaitActionResponseWriter = false)
        {
            if (conn == null || !conn.IsSpawned) return false;
            conn.UseTargetMovingTargetFixedX = targetFixedX;
            conn.UseTargetMovingTargetFixedY = targetFixedY;
            conn.UseTargetMovingFollowConnId = followConnId;
            conn.UseTargetMovingEntityId = entityId;
            conn.UseTargetMovingInstanceKey = conn.RuntimeInstanceKey ?? "";
            conn.UseTargetMovingClientDriven = false;
            conn.UseTargetMovingActionMirrored = mirrorWithAction;
            conn.UseTargetMovingActivate = false;
            conn.UseTargetMovingActivateTargetId = 0;
            conn.UseTargetMovingActivateRangeFixed = 0;
            conn.UseTargetMovingLastAdvancedTick = uint.MaxValue;
            conn.UseTargetMovingMoverActive = false;
            conn.UseTargetMovingMoverStarted = false;
            conn.UseTargetMovingMoverMovingThisFrame = false;
            conn.UseTargetMovingMoverInitialUpdatePending = false;
            conn.UseTargetMovingInitUseInitialized = false;
            int unitFollowClientQueuedBeforeAction = includeQueuedUnitFollowClientRecords && conn.UnitBehaviorId <= ushort.MaxValue
                ? conn.MessageQueue.CountPendingUnitMoverRecords((ushort)conn.UnitBehaviorId)
                : 0;
            ulong unitFollowClientWaitThroughOrdinal = unitFollowClientWaitThroughOverride != ulong.MaxValue
                ? unitFollowClientWaitThroughOverride
                : checked(conn.UnitFollowClientReceivedOrdinal + (ulong)Math.Max(0, unitFollowClientQueuedBeforeAction));
            conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal = unitFollowClientWaitThroughOrdinal;
            conn.UseTargetMovingUnitFollowClientWaitActive = mirrorWithAction
                && conn.UnitFollowClientAppliedOrdinal < unitFollowClientWaitThroughOrdinal;
            conn.UseTargetMovingUsing = false;
            conn.UseTargetMovingInputAdmitted = !awaitActionResponseWriter;
            conn.UseTargetMovingAdmissionTick = unchecked(_combatTick + (uint)Math.Max(0, admissionTicks));
            conn.UseTargetMovingRetargetCountdown = 15;
            conn.UseTargetMovingPathRetryCountdown = 0;
            ClearUseTargetMovingBatch(conn);
            ClearUseTargetMovingPath(conn);
            SyncUseTargetMovingFixedState(conn);
            if (!mirrorWithAction)
                PrimeUseTargetMovingHeading(conn, targetFixedX, targetFixedY);
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            if (!mirrorWithAction && DistanceSqFixed(conn.UseTargetMovingFixedX, conn.UseTargetMovingFixedY, targetFixedX, targetFixedY) <= USE_TARGET_UPDATE_MOVING_STOP_DISTANCE_FIXED_SQ)
            {
                SendUseTargetMovingHeadingTerminal(conn, targetFixedX, targetFixedY, pathMap);
                ClearUseTargetMovingBatch(conn);
                ClearUseTargetMovingPath(conn);
                Debug.LogError($"[USE-TARGET-MOVING] state=face conn={conn.ConnId} fromFixed=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY}) targetFixed=({targetFixedX},{targetFixedY}) follow={followConnId} entity={entityId}");
                return true;
            }
            if (!mirrorWithAction)
                BuildUseTargetMovingPath(conn, pathMap, targetFixedX, targetFixedY);
            conn.UseTargetMovingActive = true;
            Debug.LogError($"[USE-TARGET-MOVING] state=start conn={conn.ConnId} startTick={_combatTick} admissionTick={conn.UseTargetMovingAdmissionTick} moverFixed=({conn.UseTargetMovingFixedX},{conn.UseTargetMovingFixedY},{conn.UseTargetMovingFixedZ}) liveFixed=({conn.LivePlayerPosFixedX},{conn.LivePlayerPosFixedY},{conn.LivePlayerPosFixedZ}) reflectedFixed=({conn.ReflectedAvatarPosFixedX},{conn.ReflectedAvatarPosFixedY},{conn.ReflectedAvatarPosFixedZ}) reflectedReady={conn.ReadyReflectedAvatarMovementSamples.Count} reflectedReceivedOrdinal={conn.ReflectedAvatarMovementReceivedOrdinal} reflectedAppliedOrdinal={conn.ReflectedAvatarMovementAppliedOrdinal} unitReceivedOrdinal={conn.UnitFollowClientReceivedOrdinal} unitAppliedOrdinal={conn.UnitFollowClientAppliedOrdinal} unitQueuedBeforeAction={unitFollowClientQueuedBeforeAction} unitWaitOverride={(unitFollowClientWaitThroughOverride == ulong.MaxValue ? "none" : unitFollowClientWaitThroughOverride.ToString())} unitWaitActive={conn.UseTargetMovingUnitFollowClientWaitActive} unitWaitThroughOrdinal={conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal} targetFixed=({targetFixedX},{targetFixedY}) follow={followConnId} entity={entityId} mirrorAction={mirrorWithAction}");
            return true;
        }

        private bool StartActivateMoving(RRConnection conn, ushort targetEntityId, int targetFixedX, int targetFixedY, int targetRadiusFixed, int followConnId = 0, ushort movingEntityId = 0)
        {
            if (!StartUseTargetMoving(conn, targetFixedX, targetFixedY, followConnId, movingEntityId, mirrorWithAction: true, includeQueuedUnitFollowClientRecords: true, awaitActionResponseWriter: true))
                return false;
            conn.UseTargetMovingActivate = true;
            conn.UseTargetMovingActivateTargetId = targetEntityId;
            conn.UseTargetMovingActivateRangeFixed = ResolveActivationRangeFixed(targetRadiusFixed);
            Debug.LogError($"[ACTIVATE-MOVING] state=start conn={conn.ConnId} target={targetEntityId} range={conn.UseTargetMovingActivateRangeFixed} tick={_combatTick} sourceFunction=Activate::start@0x00522210");
            return true;
        }

        private bool BeginDroppedItemActivation(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId)
        {
            if (conn == null
                || !_droppedItems.TryGetValue(targetEntityId, out DroppedItemInfo info)
                || _pendingDroppedItemExpirations.Contains(targetEntityId)
                || !DroppedItemMatchesConnection(conn, info))
            {
                Debug.LogError($"[PICKUP-ACTIVATE] state=rejected target=0x{targetEntityId:X4} reason=missing-or-instance");
                return false;
            }

            if (conn.UseTargetMovingActive)
                CancelUseTargetMoving(conn, "dropped-item-retarget", false);
            else
                ClearPendingDroppedItemActivation(conn);

            conn.SessionID = sessionId;
            conn.PendingDroppedItemComponentId = componentId;
            conn.PendingDroppedItemTargetEntityId = targetEntityId;
            conn.PendingDroppedItemResponseId = responseId;
            conn.PendingDroppedItemSessionId = sessionId;
            conn.PendingDroppedItemInstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);

            bool inRange = IsWithinActivationRangeFixed(
                conn,
                info.PosFixedX,
                info.PosFixedY,
                ACTIVATE_ITEM_TARGET_RADIUS_FIXED,
                out long distanceSqFixed,
                out int rangeFixed);
            if (inRange)
            {
                ClearPendingDroppedItemActivation(conn);
                Debug.LogError($"[PICKUP-ACTIVATE] state=3 target=0x{targetEntityId:X4} distanceSq={distanceSqFixed} range={rangeFixed} sourceFunction=Activate::isWithinActivationRange@0x005228A0");
                HandleItemRightClickPickup(conn, componentId, targetEntityId, responseId, sessionId);
                return true;
            }

            if (!StartActivateMoving(
                    conn,
                    targetEntityId,
                    info.PosFixedX,
                    info.PosFixedY,
                    ACTIVATE_ITEM_TARGET_RADIUS_FIXED))
            {
                ClearPendingDroppedItemActivation(conn);
                Debug.LogError($"[PICKUP-ACTIVATE] state=rejected target=0x{targetEntityId:X4} distanceSq={distanceSqFixed} range={rangeFixed} reason=movement-start");
                return false;
            }

            QueueDroppedItemActivateResponse(conn, componentId, targetEntityId, responseId, sessionId); Debug.LogError($"[PICKUP-ACTIVATE] state=2 conn={conn.ConnId} tick={_combatTick} target=0x{targetEntityId:X4} targetFixed=({info.PosFixedX},{info.PosFixedY}) distanceSq={distanceSqFixed} range={rangeFixed} sourceFunction=Activate::States@0x00522300");
            return true;
        }

        private bool BeginWorldEntityChestActivation(
            RRConnection conn,
            ushort componentId,
            ushort targetEntityId,
            byte responseId,
            byte sessionId,
            WorldEntityData chest)
        {
            if (conn == null
                || chest == null
                || !(chest.IsChest || string.Equals(chest.EntityType, "boss_chest", StringComparison.OrdinalIgnoreCase))
                || !string.Equals(chest.Zone ?? "", conn.CurrentZoneName ?? "", StringComparison.OrdinalIgnoreCase)
                || !TryResolveWorldEntityActivationTargetRadiusFixed(chest.GCType, out int targetRadiusFixed))
            {
                Debug.LogError($"[CHEST-ACTIVATE] state=rejected target=0x{targetEntityId:X4} gc='{chest?.GCType ?? ""}' reason=target-or-bounds");
                return false;
            }

            if (conn.UseTargetMovingActive)
                CancelUseTargetMoving(conn, "world-entity-chest-retarget", false);
            else
                ClearPendingWorldEntityChestActivation(conn);

            conn.SessionID = sessionId;
            conn.PendingWorldEntityChestComponentId = componentId;
            conn.PendingWorldEntityChestTargetEntityId = targetEntityId;
            conn.PendingWorldEntityChestResponseId = responseId;
            conn.PendingWorldEntityChestSessionId = sessionId;
            conn.PendingWorldEntityChestInstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);
            int targetFixedX = chest.PosFixedX;
            int targetFixedY = chest.PosFixedY;

            bool inRange = IsWithinActivationRangeFixed(
                conn,
                targetFixedX,
                targetFixedY,
                targetRadiusFixed,
                out long distanceSqFixed,
                out int rangeFixed);
            if (inRange)
            {
                ClearPendingWorldEntityChestActivation(conn);
                Debug.LogError($"[CHEST-ACTIVATE] state=3 target=0x{targetEntityId:X4} gc='{chest.GCType}' targetRadius={targetRadiusFixed} distanceSq={distanceSqFixed} range={rangeFixed} sourceFunction=Activate::isWithinActivationRange@0x005228A0");
                HandleWorldEntityChestActivation(conn, componentId, targetEntityId, responseId, sessionId, chest);
                return true;
            }

            if (!StartActivateMoving(
                    conn,
                    targetEntityId,
                    targetFixedX,
                    targetFixedY,
                    targetRadiusFixed))
            {
                ClearPendingWorldEntityChestActivation(conn);
                Debug.LogError($"[CHEST-ACTIVATE] state=rejected target=0x{targetEntityId:X4} gc='{chest.GCType}' targetRadius={targetRadiusFixed} distanceSq={distanceSqFixed} range={rangeFixed} reason=movement-start");
                return false;
            }

            QueueTargetActionAck(conn, componentId, targetEntityId, responseId, sessionId, "CHEST-ACTIVATE");
            Debug.LogError($"[CHEST-ACTIVATE] state=2 target=0x{targetEntityId:X4} gc='{chest.GCType}' targetRadius={targetRadiusFixed} distanceSq={distanceSqFixed} range={rangeFixed} sourceFunction=Activate::States@0x00522300");
            return true;
        }

        private bool BeginCheckpointActivation(
            RRConnection conn,
            ushort componentId,
            ushort targetEntityId,
            byte responseId,
            byte sessionId,
            ZoneCheckpoint checkpoint)
        {
            if (conn == null
                || checkpoint == null
                || string.IsNullOrWhiteSpace(checkpoint.GCType)
                || !TryResolveWorldEntityActivationTargetRadiusFixed(checkpoint.GCType, out int targetRadiusFixed))
            {
                Debug.LogError($"[CHECKPOINT-ACTIVATE] state=rejected target=0x{targetEntityId:X4} gc='{checkpoint?.GCType ?? ""}' reason=target-or-bounds");
                return false;
            }

            if (conn.UseTargetMovingActive)
                CancelUseTargetMoving(conn, "checkpoint-retarget", false);
            else
                ClearPendingCheckpointActivation(conn);

            conn.SessionID = sessionId;
            conn.PendingCheckpointComponentId = componentId;
            conn.PendingCheckpointTargetEntityId = targetEntityId;
            conn.PendingCheckpointResponseId = responseId;
            conn.PendingCheckpointSessionId = sessionId;
            conn.PendingCheckpointInstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey);

            bool inRange = IsWithinActivationRangeFixed(
                conn,
                checkpoint.PosFixedX,
                checkpoint.PosFixedY,
                targetRadiusFixed,
                out long distanceSqFixed,
                out int rangeFixed);
            if (inRange)
            {
                ClearPendingCheckpointActivation(conn);
                Debug.LogError($"[CHECKPOINT-ACTIVATE] state=3 target=0x{targetEntityId:X4} gc='{checkpoint.GCType}' targetRadius={targetRadiusFixed} distanceSq={distanceSqFixed} range={rangeFixed} sourceFunction=Activate::isWithinActivationRange@0x005228A0");
                HandleCheckpointActivation(conn, componentId, targetEntityId, responseId, sessionId, checkpoint);
                return true;
            }

            if (!StartActivateMoving(
                    conn,
                    targetEntityId,
                    checkpoint.PosFixedX,
                    checkpoint.PosFixedY,
                    targetRadiusFixed))
            {
                ClearPendingCheckpointActivation(conn);
                Debug.LogError($"[CHECKPOINT-ACTIVATE] state=rejected target=0x{targetEntityId:X4} gc='{checkpoint.GCType}' targetRadius={targetRadiusFixed} distanceSq={distanceSqFixed} range={rangeFixed} reason=movement-start");
                return false;
            }

            QueueTargetActionAck(conn, componentId, targetEntityId, responseId, sessionId, "CHECKPOINT");
            Debug.LogError($"[CHECKPOINT-ACTIVATE] state=2 conn={conn.ConnId} tick={_combatTick} target=0x{targetEntityId:X4} gc='{checkpoint.GCType}' targetRadius={targetRadiusFixed} targetFixed=({checkpoint.PosFixedX},{checkpoint.PosFixedY}) distanceSq={distanceSqFixed} range={rangeFixed} sourceFunction=Activate::States@0x00522300");
            return true;
        }

        private static void ClearPendingDroppedItemActivation(RRConnection conn)
        {
            if (conn == null)
                return;
            conn.PendingDroppedItemComponentId = 0;
            conn.PendingDroppedItemTargetEntityId = 0;
            conn.PendingDroppedItemResponseId = 0;
            conn.PendingDroppedItemSessionId = 0;
            conn.PendingDroppedItemInstanceKey = "";
        }

        private static void ClearPendingWorldEntityChestActivation(RRConnection conn)
        {
            if (conn == null)
                return;
            conn.PendingWorldEntityChestComponentId = 0;
            conn.PendingWorldEntityChestTargetEntityId = 0;
            conn.PendingWorldEntityChestResponseId = 0;
            conn.PendingWorldEntityChestSessionId = 0;
            conn.PendingWorldEntityChestInstanceKey = "";
        }

        private static void ClearPendingCheckpointActivation(RRConnection conn)
        {
            if (conn == null)
                return;
            conn.PendingCheckpointComponentId = 0;
            conn.PendingCheckpointTargetEntityId = 0;
            conn.PendingCheckpointResponseId = 0;
            conn.PendingCheckpointSessionId = 0;
            conn.PendingCheckpointInstanceKey = "";
        }

        private void HandleWorldEntityChestActivation(
            RRConnection conn,
            ushort componentId,
            ushort targetEntityId,
            byte responseId,
            byte sessionId,
            WorldEntityData chest)
        {
            if (conn == null
                || chest == null
                || !(chest.IsChest || string.Equals(chest.EntityType, "boss_chest", StringComparison.OrdinalIgnoreCase))
                || !string.Equals(chest.Zone ?? "", conn.CurrentZoneName ?? "", StringComparison.OrdinalIgnoreCase))
                return;
            ExecuteWorldEntityChestActivationTransaction(conn, componentId, targetEntityId, responseId, sessionId, chest);
        }

        private static void ClearUseTargetMovingBatch(RRConnection conn)
        {
            if (conn == null) return;
            conn.UseTargetMovingPendingMoveCount = 0;
            conn.UseTargetMovingPendingMoveData = Array.Empty<byte>();
        }

        private static void ClearUseTargetMovingPath(RRConnection conn)
        {
            if (conn == null) return;
            if (conn.UseTargetMovingPathRequestId > 0)
                CombatRuntime.Instance.CancelMoveToPointPathRequest(
                    conn.UseTargetMovingPathRequestInstanceKey,
                    conn.UseTargetMovingPathRequestMap,
                    conn.UseTargetMovingPathRequestId);
            conn.UseTargetMovingPathRequestId = -1;
            conn.UseTargetMovingPathRequestInstanceKey = string.Empty;
            conn.UseTargetMovingPathRequestMap = null;
            conn.UseTargetMovingPathRequestTick = 0;
            conn.UseTargetMovingPathReadyTick = 0;
            conn.UseTargetMovingPathFixed.Clear();
            conn.UseTargetMovingPathIndex = 0;
            conn.UseTargetMovingPathTargetFixedX = 0;
            conn.UseTargetMovingPathTargetFixedY = 0;
        }

        private static bool UseTargetMovingPathTargetChanged(RRConnection conn, int targetFixedX, int targetFixedY)
        {
            if (conn == null) return true;
            long thresholdSq = conn.UseTargetMovingFollowConnId != 0 || conn.UseTargetMovingEntityId != 0
                ? USE_TARGET_WAYPOINT_REACHED_DISTANCE_FIXED_SQ
                : USE_TARGET_PATH_REBUILD_DISTANCE_FIXED_SQ;
            return DistanceSqFixed(conn.UseTargetMovingPathTargetFixedX, conn.UseTargetMovingPathTargetFixedY, targetFixedX, targetFixedY) > thresholdSq;
        }

        private bool IsUseTargetMovingPathRequestCurrent(
            RRConnection conn,
            int requestId,
            string instanceKey,
            PathMap pathMap,
            int targetFixedX,
            int targetFixedY)
        {
            return conn != null
                && conn.IsConnected
                && conn.IsSpawned
                && conn.UseTargetMovingActive
                && conn.UseTargetMovingPathRequestId == requestId
                && ReferenceEquals(conn.UseTargetMovingPathRequestMap, pathMap)
                && string.Equals(conn.UseTargetMovingPathRequestInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(conn.UseTargetMovingInstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ResolveConnectionInstanceKey(conn), instanceKey, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(ResolveUseTargetMovingPathMap(conn), pathMap)
                && conn.UseTargetMovingPathTargetFixedX == targetFixedX
                && conn.UseTargetMovingPathTargetFixedY == targetFixedY;
        }

        private void InstallUseTargetMovingPath(
            RRConnection conn,
            int requestId,
            string instanceKey,
            PathMap pathMap,
            int targetFixedX,
            int targetFixedY,
            Pathfinder pathfinder,
            uint simulationTick)
        {
            if (!IsUseTargetMovingPathRequestCurrent(
                conn,
                requestId,
                instanceKey,
                pathMap,
                targetFixedX,
                targetFixedY))
                return;
            conn.UseTargetMovingPathRequestId = -1;
            conn.UseTargetMovingPathRequestInstanceKey = string.Empty;
            conn.UseTargetMovingPathRequestMap = null;
            conn.UseTargetMovingPathReadyTick = simulationTick;
            conn.UseTargetMovingPathFixed.Clear();
            conn.UseTargetMovingPathIndex = 0;
            List<(int FixedX, int FixedY)> fixedPath = CombatRuntime.BuildUnitMoverPath(pathfinder, targetFixedX, targetFixedY);
            conn.UseTargetMovingPathFixed.AddRange(fixedPath);
            bool ready = fixedPath.Count > 0;
            conn.UseTargetMovingMoverActive = ready;
            Debug.LogError($"[USE-TARGET-MOVING-PATH] conn={conn.ConnId} request={requestId} tick={simulationTick} target=({targetFixedX},{targetFixedY}) directReach={pathfinder.DirectReach} reachedGoal={pathfinder.ReachedGoal} done={pathfinder.IsDone} expanded={pathfinder.NodesExpanded} waypoints={conn.UseTargetMovingPathFixed.Count} result={(ready ? "ready" : "no-path")} sourceFunction=PathManager::UpdateRequests@0x004C3D40->UnitMover::OnPathRequestComplete@0x005369B0");
        }

        private bool BuildUseTargetMovingPath(RRConnection conn, PathMap pathMap, int targetFixedX, int targetFixedY)
        {
            if (conn == null) return false;
            ClearUseTargetMovingPath(conn);
            conn.UseTargetMovingPathTargetFixedX = targetFixedX;
            conn.UseTargetMovingPathTargetFixedY = targetFixedY;
            if (pathMap == null || !conn.UseTargetMovingHasFixedState)
            {
                conn.UseTargetMovingPathRequestId = -2;
                return false;
            }
            var startNode = pathMap.GetClosestNodeFixed(conn.UseTargetMovingFixedX, conn.UseTargetMovingFixedY, conn.UseTargetMovingFixedZ);
            int resolvedTargetFixedX = conn.UseTargetMovingFixedX;
            int resolvedTargetFixedY = conn.UseTargetMovingFixedY;
            PathNode goalNode = null;
            if (startNode != null)
            {
                pathMap.FindValidDestPointFixed(
                    conn.UseTargetMovingFixedX,
                    conn.UseTargetMovingFixedY,
                    targetFixedX,
                    targetFixedY,
                    startNode,
                    0x6400,
                    out resolvedTargetFixedX,
                    out resolvedTargetFixedY,
                    out goalNode);
            }
            if (startNode == null || goalNode == null)
            {
                conn.UseTargetMovingPathRequestId = -2;
                return false;
            }
            var pathfinder = new Pathfinder(pathMap);
            pathfinder.RequestPath(
                conn.UseTargetMovingFixedX,
                conn.UseTargetMovingFixedY,
                startNode,
                resolvedTargetFixedX,
                resolvedTargetFixedY,
                goalNode);
            string instanceKey = ResolveConnectionInstanceKey(conn);
            conn.UseTargetMovingPathRequestInstanceKey = instanceKey;
            conn.UseTargetMovingPathRequestMap = pathMap;
            uint ownerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            int requestId = CombatRuntime.Instance.RequestMoveToPointPathSync(
                instanceKey,
                pathMap,
                pathfinder,
                "player",
                ownerEntityId,
                targetFixedX,
                targetFixedY,
                candidateRequestId => IsUseTargetMovingPathRequestCurrent(
                    conn,
                    candidateRequestId,
                    instanceKey,
                    pathMap,
                    targetFixedX,
                    targetFixedY),
                (candidateRequestId, completedPathfinder, simulationTick) => InstallUseTargetMovingPath(
                    conn,
                    candidateRequestId,
                    instanceKey,
                    pathMap,
                    targetFixedX,
                    targetFixedY,
                    completedPathfinder,
                    simulationTick));
            conn.UseTargetMovingPathRequestId = requestId;
            conn.UseTargetMovingPathRequestTick = _combatTick;
            conn.UseTargetMovingMoverActive = requestId > 0;
            Debug.LogError($"[USE-TARGET-MOVING-PATH] conn={conn.ConnId} request={requestId} start=({conn.UseTargetMovingFixedX},{conn.UseTargetMovingFixedY}) target=({targetFixedX},{targetFixedY}) tick={_combatTick} result=queued sourceFunction=UnitMover::MoveToPoint@0x00535FB0->PathManager::RequestPathSync<UnitBehavior>@0x00519920");
            return requestId > 0;
        }

        private static int UseTargetMovingDistanceSquared(int deltaX, int deltaY)
        {
            return unchecked((int)(((long)deltaX * deltaX) >> 8) + (int)(((long)deltaY * deltaY) >> 8));
        }

        private static void AdvanceExactUseTargetMovingWaypoints(RRConnection conn)
        {
            if (conn == null || !conn.UseTargetMovingHasFixedState)
                return;
            while (conn.UseTargetMovingPathIndex < conn.UseTargetMovingPathFixed.Count)
            {
                var waypoint = conn.UseTargetMovingPathFixed[conn.UseTargetMovingPathIndex];
                if (UseTargetMovingDistanceSquared(waypoint.X - conn.UseTargetMovingFixedX, waypoint.Y - conn.UseTargetMovingFixedY) != 0)
                    break;
                conn.UseTargetMovingPathIndex++;
            }
        }

        private bool UpdateUseTargetMovingSteering(RRConnection conn, PathMap pathMap, int speedPerFrameFixed, int steeringArrivalDistanceFixed,
            out int desiredHeading, out int residualX, out int residualY, out bool nearWaypoint)
        {
            desiredHeading = conn?.UseTargetMovingHeadingFixed ?? 0;
            residualX = 0;
            residualY = 0;
            nearWaypoint = false;
            if (conn == null || pathMap == null || !conn.UseTargetMovingHasFixedState)
                return false;
            AdvanceExactUseTargetMovingWaypoints(conn);
            if (conn.UseTargetMovingPathIndex >= conn.UseTargetMovingPathFixed.Count)
            {
                conn.UseTargetMovingMoverActive = false;
                conn.UseTargetMovingPathRetryCountdown = 0;
                return true;
            }
            var waypoint = conn.UseTargetMovingPathFixed[conn.UseTargetMovingPathIndex];
            int deltaX = unchecked(waypoint.X - conn.UseTargetMovingFixedX);
            int deltaY = unchecked(waypoint.Y - conn.UseTargetMovingFixedY);
            desiredHeading = UnitMover.VectorToHeadingFixed(deltaX, deltaY);
            if (pathMap.CanReachPointFixed(
                    conn.UseTargetMovingFixedX,
                    conn.UseTargetMovingFixedY,
                    waypoint.X,
                    waypoint.Y))
            {
                conn.UseTargetMovingPathRetryCountdown = 0;
            }
            else if (conn.UseTargetMovingPathRetryCountdown == 0)
            {
                conn.UseTargetMovingPathRetryCountdown = 15;
            }
            else if (--conn.UseTargetMovingPathRetryCountdown == 0)
            {
                int targetFixedX = conn.UseTargetMovingPathTargetFixedX;
                int targetFixedY = conn.UseTargetMovingPathTargetFixedY;
                bool rebuilt = BuildUseTargetMovingPath(conn, pathMap, targetFixedX, targetFixedY);
                desiredHeading = conn.UseTargetMovingHeadingFixed;
                Debug.LogError($"[USE-TARGET-MOVING-PATH] conn={conn.ConnId} state=retry target=({targetFixedX},{targetFixedY}) mover={rebuilt} sourceFunction=UnitMover::UpdateSteering@0x00536380");
                return rebuilt;
            }
            bool skipWaypoint = false;
            if (conn.UseTargetMovingPathIndex < conn.UseTargetMovingPathFixed.Count - 1)
            {
                var nextWaypoint = conn.UseTargetMovingPathFixed[conn.UseTargetMovingPathIndex + 1];
                skipWaypoint = !pathMap.CastGroundRayFixed(conn.UseTargetMovingFixedX, conn.UseTargetMovingFixedY,
                    conn.UseTargetMovingFixedZ, nextWaypoint.X, nextWaypoint.Y, out _, out _, out _);
            }
            if (!skipWaypoint)
            {
                int distanceSquared = UseTargetMovingDistanceSquared(deltaX, deltaY);
                nearWaypoint = distanceSquared <= unchecked((int)(((long)steeringArrivalDistanceFixed * steeringArrivalDistanceFixed) >> 8));
                if (!nearWaypoint || distanceSquared > unchecked((int)(((long)speedPerFrameFixed * speedPerFrameFixed) >> 8)))
                    return true;
                residualX = deltaX;
                residualY = deltaY;
            }
            conn.UseTargetMovingPathIndex++;
            nearWaypoint = false;
            if (conn.UseTargetMovingPathIndex >= conn.UseTargetMovingPathFixed.Count)
            {
                conn.UseTargetMovingMoverActive = false;
                conn.UseTargetMovingPathRetryCountdown = 0;
                desiredHeading = conn.UseTargetMovingHeadingFixed;
            }
            return true;
        }

        private bool AdvanceActionMirroredMoveToPoint(RRConnection conn, PathMap pathMap, int speedPerFrameFixed,
            int steeringArrivalDistanceFixed, bool sendMovement, bool initialHeadingOnly)
        {
            bool ownerMoverPosition = ActionMirroredMoveToPointOwnsUnitMover(conn);
            bool canonicalMoverPosition = CanonicalActionMirroredMoveToPointOwnsUnitMover(conn);
            if (!UpdateUseTargetMovingSteering(conn, pathMap, speedPerFrameFixed, steeringArrivalDistanceFixed,
                out int desiredHeading, out int residualX, out int residualY, out bool nearWaypoint))
                return false;
            if (conn.UseTargetMovingPathRequestId > 0)
                return true;
            int prevX = conn.UseTargetMovingFixedX;
            int prevY = conn.UseTargetMovingFixedY;
            int prevZ = conn.UseTargetMovingFixedZ;
            int prevHeading = conn.UseTargetMovingHeadingFixed;
            int nextX = prevX;
            int nextY = prevY;
            int nextHeading = UnitMover.InterpolateHeading(prevHeading, desiredHeading, USE_TARGET_AVATAR_TURN_RATE_FIXED);
            bool movingThisFrame = false;
            if (conn.UseTargetMovingMoverActive && !initialHeadingOnly)
                UnitMover.StepInDirectionFixedHeading(prevX, prevY, prevHeading, desiredHeading, speedPerFrameFixed,
                    USE_TARGET_AVATAR_TURN_RATE_FIXED, true, !nearWaypoint && conn.UseTargetMovingMoverMovingThisFrame,
                    out nextX, out nextY, out nextHeading, out movingThisFrame);
            nextX = unchecked(nextX + residualX);
            nextY = unchecked(nextY + residualY);
            ResolveUseTargetMovingMovementFixed(conn, pathMap, prevX, prevY, prevZ, nextX, nextY,
                out nextX, out nextY, out int nextZ);
            conn.UseTargetMovingMoverMovingThisFrame = movingThisFrame;
            if (nextX != prevX || nextY != prevY || nextHeading != prevHeading)
            {
                if (sendMovement)
                    SendUseTargetMovingRecord(conn, prevHeading, prevX, prevY, nextHeading, nextX, nextY, false);
                ApplyUseTargetMovingPosition(conn, nextX, nextY, nextHeading, pathMap, nextZ, ownerMoverPosition, canonicalMoverPosition);
            }
            if (!conn.UseTargetMovingMoverActive)
            {
                conn.ReflectedAvatarUnitMoverMode = UnitMover.StoppedMode;
                conn.OwnerUnitBehaviorMoverMode = UnitMover.StoppedMode;
                SyncReflectedAvatarCombatPosition(conn);
            }
            return true;
        }

        private void QueueUseTargetMovingRecord(RRConnection conn, byte[] record, bool terminal)
        {
            if (conn == null || record == null || record.Length != UnitMoverUpdateRecordSize) return;
            ClearUseTargetMovingBatch(conn);
            BroadcastPlayerMovement(conn, conn.SessionID, 1, record, fromTargetBridge: true);
        }

        private void SendUseTargetMovingRecord(RRConnection conn, int prevHeading, int prevX, int prevY, int heading, int posX, int posY, bool terminal)
        {
            if (conn == null) return;
            if (!terminal && prevX == posX && prevY == posY) return;
            byte flags = BuildUseTargetMovingFlags(
                prevHeading,
                heading,
                terminal,
                ResolveUseTargetMovingTouchingMonsters(conn));
            var record = BuildUseTargetMovingRecord(flags, heading, posX, posY);
            QueueUseTargetMovingRecord(conn, record, terminal);
        }

        private void SendUseTargetMovingTerminal(RRConnection conn)
        {
            if (conn == null || !conn.UseTargetMovingHasFixedState) return;
            SendUseTargetMovingRecord(conn,
                conn.UseTargetMovingHeadingFixed,
                conn.UseTargetMovingFixedX,
                conn.UseTargetMovingFixedY,
                conn.UseTargetMovingHeadingFixed,
                conn.UseTargetMovingFixedX,
                conn.UseTargetMovingFixedY,
                true);
        }

        private bool SendUseTargetMovingHeadingTerminal(RRConnection conn, int targetFixedX, int targetFixedY, PathMap pathMap)
        {
            if (conn == null || !conn.UseTargetMovingHasFixedState) return false;
            int prevX = conn.UseTargetMovingFixedX;
            int prevY = conn.UseTargetMovingFixedY;
            int dx = targetFixedX - prevX;
            int dy = targetFixedY - prevY;
            if (dx == 0 && dy == 0) return false;
            int prevHeading = conn.UseTargetMovingHeadingFixed;
            int heading = UnitMover.VectorToHeadingFixed(dx, dy);
            if (heading == prevHeading) return false;
            SendUseTargetMovingRecord(conn, prevHeading, prevX, prevY, heading, prevX, prevY, true);
            ApplyUseTargetMovingPosition(conn, prevX, prevY, heading, pathMap);
            return true;
        }

        private static void UpdateUseTargetMovingFixedState(RRConnection conn, int fixedX, int fixedY, int fixedZ, int headingFixed)
        {
            if (conn == null) return;
            conn.UseTargetMovingFixedX = fixedX;
            conn.UseTargetMovingFixedY = fixedY;
            conn.UseTargetMovingFixedZ = fixedZ;
            conn.UseTargetMovingHeadingFixed = headingFixed;
            conn.UseTargetMovingHasFixedState = true;
        }

        private void CancelUseTargetMoving(RRConnection conn, string reason, bool sendTerminal = true)
        {
            if (conn == null) return;
            if (!conn.UseTargetMovingActive)
            {
                ClearPendingCheckpointActivation(conn);
                return;
            }
            bool cancelBasicUseTarget = conn.UseTargetMovingActionMirrored && conn.HasActiveUseTarget && conn.ActiveUseTargetFlags < 100;
            bool relayOwnsPeerMovement = HasAnyActivePeerAction(conn);
            if (sendTerminal && !string.Equals(reason, "client-move", StringComparison.OrdinalIgnoreCase))
                SendUseTargetMovingTerminal(conn);
            ClearUseTargetMovingBatch(conn);
            conn.UseTargetMovingActive = false;
            conn.UseTargetMovingClientDriven = false;
            conn.UseTargetMovingActionMirrored = false;
            conn.UseTargetMovingActivate = false;
            conn.UseTargetMovingActivateTargetId = 0;
            conn.UseTargetMovingActivateRangeFixed = 0;
            conn.UseTargetMovingFollowConnId = 0;
            conn.UseTargetMovingEntityId = 0;
            conn.UseTargetMovingTargetFixedX = 0;
            conn.UseTargetMovingTargetFixedY = 0;
            conn.UseTargetMovingHasFixedState = false;
            conn.UseTargetMovingFixedZ = 0;
            conn.UseTargetMovingLastAdvancedTick = uint.MaxValue;
            conn.UseTargetMovingMoverActive = false;
            conn.UseTargetMovingMoverStarted = false;
            conn.UseTargetMovingMoverMovingThisFrame = false;
            conn.UseTargetMovingMoverInitialUpdatePending = false;
            conn.UseTargetMovingInitUseInitialized = false;
            conn.UseTargetMovingUnitFollowClientWaitActive = false;
            conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal = 0;
            conn.UseTargetMovingUsing = false;
            conn.UseTargetMovingInputAdmitted = true;
            conn.UseTargetMovingAdmissionTick = 0;
            conn.UseTargetMovingRetargetCountdown = 15;
            conn.UseTargetMovingPathRetryCountdown = 0;
            ClearPendingPortalActivation(conn);
            ClearPendingDroppedItemActivation(conn);
            ClearPendingWorldEntityChestActivation(conn);
            ClearPendingCheckpointActivation(conn);
            ClearUseTargetMovingBatch(conn);
            ClearUseTargetMovingPath(conn);
            if (cancelBasicUseTarget)
            {
                Combat.WeaponUseRuntime.Instance.CancelConnectionUseTargetIntent(conn.ConnId.ToString(), $"UseTargetMoving-{reason}");
                ClearUseTarget(conn);
            }
            Debug.LogError($"[USE-TARGET-MOVING] state=end conn={conn.ConnId} reason={reason} posFixed=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY})");
            if (relayOwnsPeerMovement && ShouldStopRemoteActionOnUseTargetMovingEnd(reason))
                BroadcastRemoteActionStopForMovementHandoff(conn, $"MP-ACTION-STOP-MOVE-{reason}");
        }

        private static bool ShouldStopRemoteActionOnUseTargetMovingEnd(string reason)
        {
            return string.Equals(reason, "CANCEL-ACTION-process-update", StringComparison.Ordinal);
        }

        private PathMap ResolveUseTargetMovingPathMap(RRConnection conn)
        {
            string pathMapKey = ResolveConnectionInstanceKey(conn);
            return !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
        }

        private static int ResolveConnectionGroundHeightFixed(RRConnection conn, PathMap pathMap, int worldFixedX, int worldFixedY, int fallbackFixedZ)
        {
            string zoneName = conn?.CurrentZoneName;
            string pathMapKey = ResolveConnectionInstanceKey(conn);
            string collisionInstanceKey = string.Equals(pathMapKey, zoneName, StringComparison.OrdinalIgnoreCase) ? null : pathMapKey;
            return ResolveItemDropGroundHeightFixed(zoneName, collisionInstanceKey, pathMap, worldFixedX, worldFixedY, fallbackFixedZ);
        }

        private static bool TryResolveSpellTeleportGroundHeightFixed(RRConnection conn, PathMap pathMap, int worldFixedX, int worldFixedY, out int groundFixedZ)
        {
            string zoneName = conn?.CurrentZoneName;
            string pathMapKey = ResolveConnectionInstanceKey(conn);
            string collisionInstanceKey = string.Equals(pathMapKey, zoneName, StringComparison.OrdinalIgnoreCase) ? null : pathMapKey;
            groundFixedZ = 0;
            bool resolved = pathMap != null && pathMap.TryGetHeightAtFixed(worldFixedX, worldFixedY, out groundFixedZ);
            if (WorldCollision.Instance.TryGetTerrainHeightFixed(
                zoneName,
                collisionInstanceKey,
                worldFixedX,
                worldFixedY,
                0,
                out int collisionGroundFixedZ,
                out _)
                && (!resolved || collisionGroundFixedZ > groundFixedZ))
            {
                groundFixedZ = collisionGroundFixedZ;
                resolved = true;
            }
            return resolved;
        }

        private static void ResolveUseTargetMovingMovementFixed(
            RRConnection conn,
            PathMap pathMap,
            int curFixedX,
            int curFixedY,
            int curFixedZ,
            int candFixedX,
            int candFixedY,
            out int outFixedX,
            out int outFixedY,
            out int outFixedZ)
        {
            UnitMover.ResolveMovement(pathMap, curFixedX, curFixedY, curFixedZ, candFixedX, candFixedY, out outFixedX, out outFixedY, out outFixedZ);
        }

        private void ApplyUseTargetMovingPosition(RRConnection conn, int fixedX, int fixedY, int headingFixed, PathMap pathMap,
            int? resolvedGroundFixedZ = null, bool? ownerMoverPosition = null, bool? canonicalMoverPosition = null)
        {
            int fixedZ = conn.PlayerPosFixedZ;
            if (pathMap != null)
                fixedZ = resolvedGroundFixedZ ?? ResolveConnectionGroundHeightFixed(conn, pathMap, fixedX, fixedY, conn.PlayerPosFixedZ);
            bool ownerActionMoverOwnsUnitMover = ownerMoverPosition ?? ActionMirroredMoveToPointOwnsUnitMover(conn);
            bool canonicalActionMoverOwnsUnitMover = canonicalMoverPosition ?? CanonicalActionMirroredMoveToPointOwnsUnitMover(conn);
            bool actionMoverMoved = ownerActionMoverOwnsUnitMover
                && (!conn.HasOwnerAckFollowClientPosition
                    || conn.OwnerAckFollowClientPosFixedX != fixedX
                    || conn.OwnerAckFollowClientPosFixedY != fixedY);
            UpdateUseTargetMovingFixedState(conn, fixedX, fixedY, fixedZ, headingFixed);
            conn.PlayerPosFixedX = fixedX;
            conn.PlayerPosFixedY = fixedY;
            conn.PlayerPosFixedZ = fixedZ;
            conn.PlayerHeadingFixed = headingFixed;
            conn.HasLivePlayerPosition = true;
            conn.LivePlayerPosFixedX = fixedX;
            conn.LivePlayerPosFixedY = fixedY;
            conn.LivePlayerPosFixedZ = fixedZ;
            conn.LivePlayerHeadingFixed = headingFixed;
            if (ownerActionMoverOwnsUnitMover
                || conn.PendingReflectedAvatarMovementSamples.Count == 0
                && conn.HeldReflectedAvatarMovementSamples.Count == 0
                && conn.AdmittedReflectedAvatarMovementSamples.Count == 0
                && conn.ReadyReflectedAvatarMovementSamples.Count == 0)
            {
                SetReflectedAvatarPosition(conn, fixedX, fixedY, fixedZ, headingFixed, false);
                conn.ReflectedAvatarMovingThisFrame = actionMoverMoved || !ownerActionMoverOwnsUnitMover;
            }
            if (ownerActionMoverOwnsUnitMover)
            {
                SetOwnerAckFollowClientPosition(conn, fixedX, fixedY, fixedZ, headingFixed, false);
                conn.OwnerAckFollowClientMovingThisFrame = actionMoverMoved;
                SyncOwnerAckFollowClientCurrentState(conn);
            }
            if (canonicalActionMoverOwnsUnitMover)
            {
                SetUnitFollowClientPosition(conn, fixedX, fixedY, fixedZ, headingFixed, false);
                conn.UnitFollowClientMovingThisFrame = actionMoverMoved;
                SyncUnitFollowClientCurrentState(conn);
            }
            conn.AggroSamplePosFixedX = fixedX;
            conn.AggroSamplePosFixedY = fixedY;
            conn.AvatarAggroSampleQueue.Enqueue((fixedX, fixedY));
            if (conn.Avatar != null)
                CombatRuntime.Instance.UpdatePlayerPositionFixed((uint)conn.Avatar.Id, fixedX, fixedY, fixedZ);
        }

        private bool EvaluateActionMirroredUseTargetInitUse(RRConnection conn, PlayerState state, uint tickIndex)
        {
            if (conn == null || state == null || !conn.UseTargetMovingActionMirrored || !conn.HasActiveUseTarget)
                return false;
            if (conn.UseTargetMovingInitUseInitialized)
                return true;
            if (conn.ActiveUseTargetPlayerConnId != 0)
                return EvaluatePvpUseTargetInitUse(conn, state, tickIndex);
            if (IsBlingGnomeSkillTargetAvailable(conn, conn.ActiveUseTargetFlags, conn.ActiveUseTargetId))
                return EvaluateBlingGnomeInitUse(conn, state);
            var monster = CombatRuntime.Instance.GetMonster(conn.ActiveUseTargetId)
                       ?? CombatRuntime.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
            if (monster == null || !monster.IsAlive)
                return false;

            int actorFixedX = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedX : conn.PlayerPosFixedX;
            int actorFixedY = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedY : conn.PlayerPosFixedY;
            int actorFixedZ = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedZ : conn.PlayerPosFixedZ;
            uint viewerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            if (!CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(
                monster,
                viewerEntityId,
                out int targetFixedX,
                out int targetFixedY,
                out int targetFixedZ))
                return false;

            int rangeFixed;
            int toleranceFixed;
            if (conn.ActiveUseTargetFlags >= 100)
            {
                Combat.SpellData spell = ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags);
                if (spell == null)
                    return false;
                rangeFixed = spell.InitUseRangeF32 > 0 ? spell.InitUseRangeF32 : 64000;
                toleranceFixed = Math.Max(0, spell.ClientSyncToleranceF32);
            }
            else
            {
                rangeFixed = CombatRuntime.Instance.ResolveUseTargetInitUseRangeF32(state, monster);
                toleranceFixed = CombatRuntime.Instance.ResolveUseTargetClientSyncToleranceF32(state);
            }
            bool passed = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                actorFixedX,
                actorFixedY,
                actorFixedZ,
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                rangeFixed,
                toleranceFixed,
                out int distanceFixed,
                out long distanceSqFixed8,
                out long thresholdSqFixed8);
            if (passed)
            {
                conn.UseTargetMovingInitUseInitialized = true;
                if (conn.ActiveUseTargetFlags >= 100)
                {
                    Combat.SpellData spell = ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags);
                    if (spell != null
                        && spell.InitUseResetsCooldown
                        && !conn.ActiveUseTargetInitUseCooldownStarted
                        && !IsActiveSkillCooldown(conn, spell, conn.ActiveUseTargetFlags, out _))
                    {
                        int skillLevel = GetPlayerSkillLevel(conn, spell);
                        StartActiveSkillCooldown(conn, conn.ActiveUseTargetComponentId, conn.ActiveUseTargetFlags, spell, state, skillLevel, true);
                        conn.ActiveUseTargetInitUseCooldownStarted = true;
                        conn.ActiveUseTargetInitUseCooldownComponentId = conn.ActiveUseTargetComponentId;
                        conn.ActiveUseTargetInitUseCooldownActionId = conn.ActiveUseTargetFlags;
                    }
                }
            }
            Debug.LogError($"[USETARGET-INIT-3D] tick={tickIndex} conn={conn.ConnId} target={conn.ActiveUseTargetId} actor=({actorFixedX},{actorFixedY},{actorFixedZ}) targetPos=({targetFixedX},{targetFixedY},{targetFixedZ}) dist={distanceFixed} distSqFixed8={distanceSqFixed8} range={rangeFixed} tolerance={toleranceFixed} thresholdSqFixed8={thresholdSqFixed8} result={(passed ? "initialized" : "moving")} sourceFunction=UseTarget::CheckInitUse@0x00548980");
            return passed;
        }

        private bool EvaluateActionMirroredUseTargetTargetIsClear(
            RRConnection conn,
            PlayerState state,
            uint tickIndex,
            int actorFixedX,
            int actorFixedY,
            int actorFixedZ)
        {
            if (conn == null || state == null || !conn.UseTargetMovingActionMirrored || !conn.HasActiveUseTarget)
                return false;
            if (conn.ActiveUseTargetPlayerConnId != 0)
                return EvaluatePvpUseTargetTargetIsClear(conn, state, tickIndex, actorFixedX, actorFixedY, actorFixedZ);
            if (IsBlingGnomeSkillTargetAvailable(conn, conn.ActiveUseTargetFlags, conn.ActiveUseTargetId))
                return EvaluateBlingGnomeTargetIsClear(conn, state, actorFixedX, actorFixedY, actorFixedZ);
            var monster = CombatRuntime.Instance.GetMonster(conn.ActiveUseTargetId)
                       ?? CombatRuntime.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
            if (monster == null || !monster.IsAlive)
                return false;

            int actualFixedX = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedX : conn.PlayerPosFixedX;
            int actualFixedY = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedY : conn.PlayerPosFixedY;
            int actualFixedZ = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedZ : conn.PlayerPosFixedZ;
            uint viewerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            if (!CombatRuntime.Instance.TryPeekMonsterClientVisiblePositionFixed(
                monster,
                viewerEntityId,
                out int targetFixedX,
                out int targetFixedY,
                out int targetFixedZ))
                return false;

            bool skillAction = conn.ActiveUseTargetFlags >= 100;
            Combat.SpellData spell = skillAction ? ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags) : null;
            if (skillAction && spell == null)
                return false;
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            if (conn.Avatar != null)
            {
                Combat.CombatPlayer player = CombatRuntime.Instance.GetPlayer((uint)conn.Avatar.Id);
                if (player != null)
                {
                    actorFixedX = player.PredictedLocation2DFixedX;
                    actorFixedY = player.PredictedLocation2DFixedY;
                    if (skillAction && pathMap != null)
                        actorFixedZ = pathMap.GetHeightAtFixed(actorFixedX, actorFixedY, actorFixedZ);
                }
            }
            int rangeFixed = skillAction
                ? CombatRuntime.Instance.ResolvePlayerSkillTargetClearRangeF32(spell, monster)
                : CombatRuntime.Instance.ResolvePlayerWeaponTargetClearRangeF32(state, monster);
            int evaluatedActorFixedX = actorFixedX;
            int evaluatedActorFixedY = actorFixedY;
            int evaluatedActorFixedZ = actorFixedZ;
            int evaluatedTargetFixedX = targetFixedX;
            int evaluatedTargetFixedY = targetFixedY;
            int evaluatedTargetFixedZ = targetFixedZ;
            bool pathClear = true;
            bool staticBlocked = false;
            WorldCollisionHit staticHit = null;
            bool passed;
            int distanceFixed;
            long distanceSqFixed8;
            long thresholdSqFixed8;
            if (skillAction)
            {
                CombatRuntime.Instance.ResolvePlayerActiveSkillTargetPositionsFixed(
                    actorFixedX,
                    actorFixedY,
                    actorFixedZ,
                    monster,
                    targetFixedX,
                    targetFixedY,
                    targetFixedZ,
                    out evaluatedActorFixedX,
                    out evaluatedActorFixedY,
                    out evaluatedActorFixedZ,
                    out evaluatedTargetFixedX,
                    out evaluatedTargetFixedY,
                    out evaluatedTargetFixedZ);
                CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                    evaluatedActorFixedX,
                    evaluatedActorFixedY,
                    evaluatedActorFixedZ,
                    evaluatedTargetFixedX,
                    evaluatedTargetFixedY,
                    evaluatedTargetFixedZ,
                    rangeFixed,
                    0,
                    out distanceFixed,
                    out distanceSqFixed8,
                    out thresholdSqFixed8);
                passed = thresholdSqFixed8 > 0 && distanceSqFixed8 < thresholdSqFixed8;
                if (passed)
                {
                    staticBlocked = WorldCollision.Instance.TrySegmentHitFixed(
                        conn.CurrentZoneName,
                        conn.RuntimeInstanceKey,
                        evaluatedActorFixedX,
                        evaluatedActorFixedY,
                        evaluatedActorFixedZ,
                        evaluatedTargetFixedX,
                        evaluatedTargetFixedY,
                        evaluatedTargetFixedZ,
                        UnitMover.Fixed,
                        out staticHit);
                    pathClear = !staticBlocked;
                }
            }
            else
            {
                pathClear = pathMap != null
                    && !pathMap.CastGroundRayFixed(
                        actorFixedX,
                        actorFixedY,
                        actorFixedZ,
                        targetFixedX,
                        targetFixedY,
                        out evaluatedTargetFixedX,
                        out evaluatedTargetFixedY,
                        out evaluatedTargetFixedZ);
                passed = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed(
                    actorFixedX,
                    actorFixedY,
                    evaluatedTargetFixedX,
                    evaluatedTargetFixedY,
                    rangeFixed,
                    0,
                    out distanceFixed,
                    out distanceSqFixed8,
                    out thresholdSqFixed8);
            }
            string pathTrace = skillAction
                ? "active-skill-los"
                : pathMap == null
                ? "pathMap=none"
                : pathMap.DescribeGroundRayFixed(actorFixedX, actorFixedY, targetFixedX, targetFixedY);
            passed = passed && pathClear;
            conn.ActiveUseTargetInitUseEvaluationTick = tickIndex;
            conn.ActiveUseTargetInitUseEvaluationTargetId = conn.ActiveUseTargetId;
            conn.ActiveUseTargetInitUsePassed = passed;
            conn.ActiveUseTargetInitUseRangeF32 = rangeFixed;
            conn.ActiveUseTargetInitUseDistanceF32 = distanceFixed;
            conn.ActiveUseTargetClientToleranceF32 = 0;
            Debug.LogError($"[USETARGET-TARGET-CLEAR] tick={tickIndex} conn={conn.ConnId} target={conn.ActiveUseTargetId} actor=({actorFixedX},{actorFixedY},{actorFixedZ}) actorAim=({evaluatedActorFixedX},{evaluatedActorFixedY},{evaluatedActorFixedZ}) actual=({actualFixedX},{actualFixedY},{actualFixedZ}) actorModel={(skillAction ? "unit-mover-predicted-as-position" : "unit-mover-predicted-2d")} targetPos=({targetFixedX},{targetFixedY},{targetFixedZ}) rayTarget=({evaluatedTargetFixedX},{evaluatedTargetFixedY},{evaluatedTargetFixedZ}) dist={distanceFixed} distSqFixed8={distanceSqFixed8} range={rangeFixed} thresholdSqFixed8={thresholdSqFixed8} pathClear={pathClear} result={(passed ? "use" : "moving")} sourceFunction=Manipulator::targetIsClear@0x004FB7B0->{(skillAction ? "ActiveSkill::targetIsClear@0x00538A40->ASGetPosition@0x00538390->LOSChecker::isClear@0x00534F50" : "Weapon::targetIsClear@0x00597B00->PathMap::CastGroundRay@0x004C60A0->UnitBehavior::getPredictedLocation2D@0x005204F0")}");
            Debug.LogError($"[USETARGET-PATH-RAY] tick={tickIndex} conn={conn.ConnId} target={conn.ActiveUseTargetId} trace='{pathTrace}' staticBlocked={staticBlocked} staticObject='{staticHit?.ObjectPath ?? "none"}' staticCollision='{staticHit?.CollisionObject ?? "none"}'");
            return passed;
        }

        private void CompleteActionMirroredUseTargetMovingForUse(RRConnection conn, int targetFixedX, int targetFixedY)
        {
            if (conn == null) return;
            UpdateUseTargetActionFacingFromOwnerAck(conn, targetFixedX, targetFixedY);
            ClearUseTargetMovingBatch(conn);
            ClearUseTargetMovingPath(conn);
            conn.UseTargetMovingClientDriven = false;
            conn.UseTargetMovingMoverActive = false;
            conn.UseTargetMovingMoverStarted = true;
            conn.UseTargetMovingMoverInitialUpdatePending = false;
            conn.UseTargetMovingUnitFollowClientWaitActive = false;
            conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal = 0;
            conn.OwnerUnitBehaviorMoverMode = UnitMover.StoppedMode;
            conn.ReflectedAvatarUnitMoverMode = UnitMover.StoppedMode;
            if (conn.UnitFollowClientMovementSamples.Count == 0)
                conn.UnitFollowClientMoverMode = UnitMover.StoppedMode;
            conn.OwnerAckFollowClientMovingThisFrame = false;
            conn.UnitFollowClientMovingThisFrame = false;
            conn.ReflectedAvatarMovingThisFrame = false;
            conn.UseTargetMovingUsing = true;
            conn.UseTargetMovingRetargetCountdown = 15;
            SyncOwnerAckFollowClientCurrentState(conn);
            SyncUnitFollowClientCurrentState(conn);
            SyncReflectedAvatarCombatPosition(conn);
            Debug.LogError($"[USE-TARGET-MOVING] state=use conn={conn.ConnId} target={conn.ActiveUseTargetId} pos=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY}) ownerPending={GetOwnerAckFollowClientPendingRecords(conn)} unitPending={conn.UnitFollowClientReceivedOrdinal - conn.UnitFollowClientAppliedOrdinal} reflectedPending={conn.ReflectedAvatarMovementReceivedOrdinal - conn.ReflectedAvatarMovementAppliedOrdinal} ownerMode={conn.OwnerUnitBehaviorMoverMode} reflectedMode={conn.ReflectedAvatarUnitMoverMode} sourceFunction=UseTarget::States@0x00548370->ClientUnitBehavior::SuspendClientMovement@0x00518E10->UnitMover::StopMoving@0x00536190 state=0x18 transition=same-process");
        }

        private bool TryCompleteActionMirroredUseTargetMovingForUse(
            RRConnection conn,
            PlayerState state,
            uint tickIndex,
            int targetFixedX,
            int targetFixedY,
            int actorFixedX,
            int actorFixedY,
            int actorFixedZ)
        {
            if (!EvaluateActionMirroredUseTargetTargetIsClear(
                conn,
                state,
                tickIndex,
                actorFixedX,
                actorFixedY,
                actorFixedZ))
                return false;
            if (conn.ActiveUseTargetPlayerConnId == 0
                && conn.ActiveUseTargetFlags < 100
                && !Combat.WeaponUseRuntime.Instance.ValidateUse(conn.ConnId.ToString(), tickIndex, out int readyTick))
            {
                Debug.LogError($"[WEAPON-VALIDATE-USE] conn={conn.ConnId} target={conn.ActiveUseTargetId} tick={tickIndex} readyTick={readyTick} result=False sourceFunction=UseTarget::States@0x00548370->UseTarget::validateManipulator@0x005487E0->Weapon::validateUse@0x00597CA0");
                CancelUseTargetMoving(conn, "Weapon::validateUse-cooldown");
                return true;
            }
            conn.ActiveUseTargetInitUseEvaluationTick = tickIndex;
            conn.ActiveUseTargetInitUseEvaluationTargetId = conn.ActiveUseTargetId;
            conn.ActiveUseTargetInitUsePassed = true;
            Debug.LogError($"[USE-TARGET-MOVING] state=0x18-enter conn={conn.ConnId} target={conn.ActiveUseTargetId} tick={tickIndex} mover=({actorFixedX},{actorFixedY},{actorFixedZ}) actual=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY},{conn.PlayerPosFixedZ}) sourceFunction=UseTarget::States@0x00548370->StateMachine::Process@0x005F0980");
            CompleteActionMirroredUseTargetMovingForUse(conn, targetFixedX, targetFixedY);
            uint playerEntityId = GetPlayerAvatarId(conn.LoginName);
            if (conn.ActiveUseTargetPlayerConnId != 0)
            {
                if (conn.ActiveUseTargetFlags >= 100)
                    TryBeginPvpTargetSkill(conn, tickIndex, true);
                else if (playerEntityId != 0)
                    BeginPvpWeaponUse(conn, tickIndex);
            }
            else if (IsBlingGnomeSkillTargetAvailable(conn, conn.ActiveUseTargetFlags, conn.ActiveUseTargetId))
                TryBeginBlingGnomeSkill(conn, tickIndex, true);
            else if (playerEntityId != 0 && conn.ActiveUseTargetFlags < 100)
                Combat.WeaponUseRuntime.Instance.BeginPlayerMeleeUseTargetState(playerEntityId, conn.ActiveUseTargetId, tickIndex);
            return true;
        }

        private bool AwaitActionMirroredUnitFollowClient(RRConnection conn, uint tickIndex, string sourceFunction)
        {
            if (conn == null || !conn.UseTargetMovingUnitFollowClientWaitActive)
                return false;
            if (conn.UnitFollowClientAppliedOrdinal >= conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal)
            {
                conn.UseTargetMovingUnitFollowClientWaitActive = false;
                Debug.LogError($"[UNIT-FOLLOWCLIENT] state=action-wait-complete conn={conn.ConnId} tick={tickIndex} receivedOrdinal={conn.UnitFollowClientReceivedOrdinal} appliedOrdinal={conn.UnitFollowClientAppliedOrdinal} waitThroughOrdinal={conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal} sourceFunction={sourceFunction}->UnitBehavior::UpdateFollowClient@0x00520D50");
                return false;
            }
            Debug.LogError($"[UNIT-FOLLOWCLIENT] state=action-wait conn={conn.ConnId} tick={tickIndex} receivedOrdinal={conn.UnitFollowClientReceivedOrdinal} appliedOrdinal={conn.UnitFollowClientAppliedOrdinal} waitThroughOrdinal={conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal} sourceFunction={sourceFunction}->UnitBehavior::UpdateFollowClient@0x00520D50");
            return true;
        }

        private void TickActionMirroredUseTargetMoving(RRConnection conn, PlayerState state, CombatPlayer player, uint tickIndex, int targetFixedX, int targetFixedY)
        {
            if (!conn.UseTargetMovingInputAdmitted
                || unchecked((int)(tickIndex - conn.UseTargetMovingAdmissionTick)) < 0)
                return;
            if (conn.UseTargetMovingUsing)
            {
                UpdateUseTargetActionFacingFromOwnerAck(conn, targetFixedX, targetFixedY);
                return;
            }
            if (!conn.UseTargetMovingHasFixedState)
                SyncUseTargetMovingFixedState(conn);
            int actorFixedX = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedX : conn.PlayerPosFixedX;
            int actorFixedY = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedY : conn.PlayerPosFixedY;
            int actorFixedZ = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedZ : conn.PlayerPosFixedZ;
            if (conn.ActiveUseTargetFlags < 100 && conn.UseTargetMovingHasFixedState)
            {
                actorFixedX = conn.UseTargetMovingFixedX;
                actorFixedY = conn.UseTargetMovingFixedY;
                actorFixedZ = conn.UseTargetMovingFixedZ;
            }
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            if (!conn.UseTargetMovingMoverStarted)
            {
                if (AwaitActionMirroredUnitFollowClient(conn, tickIndex, "UseTarget::States@0x00548370 state=0x1D"))
                    return;
                uint moveToPointEnterTick = conn.UseTargetMovingAdmissionTick;
                if (unchecked((int)(tickIndex - moveToPointEnterTick)) < 0)
                {
                    Debug.LogError($"[USE-TARGET-MOVING] state=0x1d-update conn={conn.ConnId} target={conn.UseTargetMovingEntityId} tick={tickIndex} admissionTick={conn.UseTargetMovingAdmissionTick} moveToPointEnterTick={moveToPointEnterTick} unitFixed=({conn.UnitFollowClientPosFixedX},{conn.UnitFollowClientPosFixedY},{conn.UnitFollowClientPosFixedZ}) unitMode={conn.OwnerUnitBehaviorMoverMode} sourceFunction=UseTarget::States@0x00548370->StateMachine::Process@0x005F0980");
                    return;
                }
                SyncUseTargetMovingFixedState(conn);
                StopFollowingClientForActionMoveToPoint(conn);
                conn.UseTargetMovingMoverActive = BuildUseTargetMovingPath(conn, pathMap, targetFixedX, targetFixedY);
                conn.UseTargetMovingMoverStarted = true;
                conn.UseTargetMovingMoverInitialUpdatePending = false;
                conn.UseTargetMovingRetargetCountdown = 15;
                if (!conn.UseTargetMovingMoverActive)
                {
                    CancelUseTargetMoving(conn, "state2-path-request");
                    return;
                }
                conn.ReflectedAvatarUnitMoverMode = UnitMover.MoveToPointMode;
                conn.OwnerUnitBehaviorMoverMode = UnitMover.MoveToPointMode;
                SyncReflectedAvatarCombatPosition(conn);
                Debug.LogError($"[USE-TARGET-MOVING] state=2-enter conn={conn.ConnId} target={conn.UseTargetMovingEntityId} tick={tickIndex} admissionTick={conn.UseTargetMovingAdmissionTick} countdown={conn.UseTargetMovingRetargetCountdown} sourceFunction=UseTarget::States@0x00548370");
            }
            if (!conn.UseTargetMovingMoverActive)
            {
                CancelUseTargetMoving(conn, "state2-mover-stopped");
                return;
            }

            if (TryCompleteActionMirroredUseTargetMovingForUse(
                conn,
                state,
                tickIndex,
                targetFixedX,
                targetFixedY,
                actorFixedX,
                actorFixedY,
                actorFixedZ))
                return;
            EvaluateActionMirroredUseTargetInitUse(conn, state, tickIndex);

            if (conn.UseTargetMovingRetargetCountdown > 0)
                conn.UseTargetMovingRetargetCountdown--;
            if (conn.UseTargetMovingRetargetCountdown == 0)
            {
                var monster = CombatRuntime.Instance.GetMonster(conn.ActiveUseTargetId)
                           ?? CombatRuntime.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
                bool targetMovingThisFrame = BlingGnomeLockstepRuntime.Instance.TryGetSnapshot(conn.ActiveUseTargetId, out BlingGnomeLockstepSnapshot gnome)
                    ? gnome.FollowMoverMovingThisFrame
                    : CombatRuntime.Instance.IsMonsterClientVisibleMovingThisFrame(monster);
                bool canReachTarget = pathMap != null && pathMap.CanReachPointFixed(
                    conn.UseTargetMovingFixedX,
                    conn.UseTargetMovingFixedY,
                    targetFixedX,
                    targetFixedY);
                if (targetMovingThisFrame && canReachTarget)
                {
                    conn.UseTargetMovingMoverActive = BuildUseTargetMovingPath(conn, pathMap, targetFixedX, targetFixedY);
                    Debug.LogError($"[USE-TARGET-MOVING] state=2-retarget conn={conn.ConnId} target={conn.UseTargetMovingEntityId} movingThisFrame=True canReach=True mover={conn.UseTargetMovingMoverActive} sourceFunction=UseTarget::UpdateMoving@0x00548850");
                }
                conn.UseTargetMovingRetargetCountdown = 15;
            }
            if (!conn.UseTargetMovingMoverActive)
            {
                CancelUseTargetMoving(conn, "state2-mover-stopped");
                return;
            }
            if (conn.UseTargetMovingPathRequestId > 0)
                return;
            int avatarStepFixed = player.OwnerAckFollowClientSpeedPerFrameF32;
            int effectiveSpeedF32 = player.OwnerAckFollowClientEffectiveSpeedF32;
            int steeringArrivalDistanceFixed = UnitMover.CacheSteeringArrivalDistance(effectiveSpeedF32, USE_TARGET_AVATAR_TURN_RATE_PER_SECOND_FIXED);
            if (!AdvanceActionMirroredMoveToPoint(conn, pathMap, avatarStepFixed, steeringArrivalDistanceFixed, false, false))
                CancelUseTargetMoving(conn, "state2-path-retry");
        }

        private void TickActionMirroredActivateMoving(RRConnection conn, PlayerState state, CombatPlayer player, uint tickIndex, int targetFixedX, int targetFixedY)
        {
            if (!conn.UseTargetMovingInputAdmitted
                || unchecked((int)(tickIndex - conn.UseTargetMovingAdmissionTick)) < 0)
                return;
            if (conn.PendingDroppedItemTargetEntityId != 0
                && (!_droppedItems.TryGetValue(conn.PendingDroppedItemTargetEntityId, out DroppedItemInfo pendingItem)
                    || !DroppedItemMatchesConnection(conn, pendingItem)
                    || !string.Equals(
                        conn.PendingDroppedItemInstanceKey,
                        RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey),
                        StringComparison.OrdinalIgnoreCase)))
            {
                CancelUseTargetMoving(conn, "dropped-item-lost", false);
                return;
            }
            if (conn.PendingWorldEntityChestTargetEntityId != 0
                && (!WorldEntitySpawner.Instance.TryGetEntity(conn.PendingWorldEntityChestTargetEntityId, out WorldEntityData pendingChest)
                    || pendingChest == null
                    || !(pendingChest.IsChest || string.Equals(pendingChest.EntityType, "boss_chest", StringComparison.OrdinalIgnoreCase))
                    || !string.Equals(pendingChest.Zone ?? "", conn.CurrentZoneName ?? "", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        conn.PendingWorldEntityChestInstanceKey,
                        RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey),
                        StringComparison.OrdinalIgnoreCase)))
            {
                CancelUseTargetMoving(conn, "world-entity-chest-lost", false);
                return;
            }
            if (conn.PendingCheckpointTargetEntityId != 0
                && (!IsCheckpoint(conn.PendingCheckpointTargetEntityId, out ZoneCheckpoint pendingCheckpoint)
                    || pendingCheckpoint == null
                    || !string.Equals(
                        conn.PendingCheckpointInstanceKey,
                        RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey),
                        StringComparison.OrdinalIgnoreCase)))
            {
                CancelUseTargetMoving(conn, "checkpoint-lost", false);
                return;
            }
            if (!conn.UseTargetMovingMoverStarted)
            {
                if (AwaitActionMirroredUnitFollowClient(conn, tickIndex, "Activate::States@0x00522300 state=0x1D"))
                    return;
                SyncUseTargetMovingFixedState(conn);
            }
            int rangeFixed = Math.Max(0, conn.UseTargetMovingActivateRangeFixed);
            long rangeSqFixed = (long)rangeFixed * rangeFixed;
            long distanceSqFixed = DistanceSqFixed(conn.UseTargetMovingFixedX, conn.UseTargetMovingFixedY, targetFixedX, targetFixedY);
            if (distanceSqFixed <= rangeSqFixed)
            {
                ushort targetId = conn.UseTargetMovingActivateTargetId;
                ushort droppedItemComponentId = conn.PendingDroppedItemComponentId;
                byte droppedItemResponseId = conn.PendingDroppedItemResponseId;
                byte droppedItemSessionId = conn.PendingDroppedItemSessionId;
                bool completeDroppedItem = conn.PendingDroppedItemTargetEntityId == targetId;
                ushort chestComponentId = conn.PendingWorldEntityChestComponentId;
                byte chestResponseId = conn.PendingWorldEntityChestResponseId;
                byte chestSessionId = conn.PendingWorldEntityChestSessionId;
                WorldEntityData worldEntityChest = null;
                bool completeWorldEntityChest = conn.PendingWorldEntityChestTargetEntityId == targetId
                    && WorldEntitySpawner.Instance.TryGetEntity(targetId, out worldEntityChest);
                ushort checkpointComponentId = conn.PendingCheckpointComponentId;
                byte checkpointResponseId = conn.PendingCheckpointResponseId;
                byte checkpointSessionId = conn.PendingCheckpointSessionId;
                ZoneCheckpoint checkpoint = null;
                bool completeCheckpoint = conn.PendingCheckpointTargetEntityId == targetId
                    && IsCheckpoint(targetId, out checkpoint);
                PrimeUseTargetMovingHeading(conn, targetFixedX, targetFixedY);
                SendUseTargetMovingTerminal(conn);
                CancelUseTargetMoving(conn, "activate-range", false);
                Debug.LogError($"[ACTIVATE-MOVING] state=3 conn={conn.ConnId} target={targetId} tick={tickIndex} distanceSq={distanceSqFixed} rangeSq={rangeSqFixed} sourceFunction=Activate::isWithinActivationRange@0x005228A0");
                if (completeDroppedItem)
                    HandleItemRightClickPickup(conn, droppedItemComponentId, targetId, droppedItemResponseId, droppedItemSessionId, false);
                else if (completeWorldEntityChest)
                    HandleWorldEntityChestActivation(conn, chestComponentId, targetId, chestResponseId, chestSessionId, worldEntityChest);
                else if (completeCheckpoint)
                    HandleCheckpointActivation(conn, checkpointComponentId, targetId, checkpointResponseId, checkpointSessionId, checkpoint, false);
                return;
            }
            if (!conn.UseTargetMovingHasFixedState)
                SyncUseTargetMovingFixedState(conn);
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            if (!conn.UseTargetMovingMoverStarted)
            {
                StopFollowingClientForActionMoveToPoint(conn);
                conn.UseTargetMovingMoverActive = BuildUseTargetMovingPath(conn, pathMap, targetFixedX, targetFixedY);
                conn.UseTargetMovingMoverStarted = true;
                conn.UseTargetMovingMoverInitialUpdatePending = true;
                if (!conn.UseTargetMovingMoverActive)
                {
                    CancelUseTargetMoving(conn, "activate-path-request", false);
                    return;
                }
                conn.ReflectedAvatarUnitMoverMode = UnitMover.MoveToPointMode;
                conn.OwnerUnitBehaviorMoverMode = UnitMover.MoveToPointMode;
                SyncReflectedAvatarCombatPosition(conn);
                Debug.LogError($"[ACTIVATE-MOVING] state=2-enter conn={conn.ConnId} target={conn.UseTargetMovingActivateTargetId} tick={tickIndex} sourceFunction=Activate::States@0x00522300");
                return;
            }
            if (!conn.UseTargetMovingMoverActive)
            {
                CancelUseTargetMoving(conn, "activate-mover-stopped", false);
                return;
            }
            if (conn.UseTargetMovingPathRequestId > 0)
                return;
            int avatarStepFixed = player.OwnerAckFollowClientSpeedPerFrameF32;
            int effectiveSpeedF32 = player.OwnerAckFollowClientEffectiveSpeedF32;
            int steeringArrivalDistanceFixed = UnitMover.CacheSteeringArrivalDistance(effectiveSpeedF32, USE_TARGET_AVATAR_TURN_RATE_PER_SECOND_FIXED);
            bool initialHeadingOnly = conn.UseTargetMovingMoverInitialUpdatePending;
            conn.UseTargetMovingMoverInitialUpdatePending = false;
            if (!AdvanceActionMirroredMoveToPoint(conn, pathMap, avatarStepFixed, steeringArrivalDistanceFixed, true, initialHeadingOnly))
                CancelUseTargetMoving(conn, "activate-path-retry", false);
        }

        private void TickUseTargetMoving(uint playerEntityId, uint tickIndex)
        {
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            if (conn == null || !conn.IsSpawned || !conn.UseTargetMovingActive) return;
            if (conn.UseTargetMovingLastAdvancedTick == tickIndex) return;
            conn.UseTargetMovingLastAdvancedTick = tickIndex;
            if (!string.Equals(conn.UseTargetMovingInstanceKey ?? "", conn.RuntimeInstanceKey ?? "", StringComparison.OrdinalIgnoreCase))
            {
                CancelUseTargetMoving(conn, "instance-change");
                return;
            }
            var state = GetPlayerState(conn.ConnId.ToString());
            if (state == null || state.CurrentHPWire == 0)
            {
                StopPlayerUseTargetActionSlot(conn, "UseTarget::States-owner-dead");
                return;
            }
            CombatPlayer player = CombatRuntime.Instance.GetPlayer(playerEntityId);
            if (player == null)
                throw new InvalidOperationException($"missing-combat-player:{playerEntityId}");
            if (conn.UseTargetMovingActionMirrored && !conn.UseTargetMovingActivate && conn.ActiveUseTargetStartedWeaponUse)
            {
                if (conn.UseTargetMovingUsing
                    && TryResolveUseTargetMovingTargetFixedReadOnly(conn, out int activeTargetFixedX, out int activeTargetFixedY, out _))
                {
                    conn.UseTargetMovingTargetFixedX = activeTargetFixedX;
                    conn.UseTargetMovingTargetFixedY = activeTargetFixedY;
                    UpdateUseTargetActionFacingFromOwnerAck(conn, activeTargetFixedX, activeTargetFixedY);
                }
                return;
            }
            if (!TryResolveUseTargetMovingTargetFixedReadOnly(conn, out int targetFixedX, out int targetFixedY, out string lostReason))
            {
                if (string.Equals(lostReason, "entity-dead", StringComparison.Ordinal))
                {
                    var killedTarget = CombatRuntime.Instance.GetMonster(conn.ActiveUseTargetId)
                        ?? CombatRuntime.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
                    if (Combat.WeaponUseRuntime.Instance.IsWeaponBusyOnKilledTarget(conn, killedTarget))
                        return;
                }
                StopPlayerUseTargetActionSlot(conn, $"UseTarget::States-{lostReason ?? "target-lost"}");
                return;
            }
            conn.UseTargetMovingTargetFixedX = targetFixedX;
            conn.UseTargetMovingTargetFixedY = targetFixedY;
            long liveDistSq = DistanceSqFixed(conn.PlayerPosFixedX, conn.PlayerPosFixedY, targetFixedX, targetFixedY);
            if (conn.UseTargetMovingClientDriven)
            {
                if (liveDistSq <= USE_TARGET_UPDATE_MOVING_STOP_DISTANCE_FIXED_SQ)
                    CancelUseTargetMoving(conn, "client-move");
                return;
            }
            if (conn.UseTargetMovingActionMirrored && !conn.UseTargetMovingActivate && !conn.HasActiveUseTarget)
            {
                CancelUseTargetMoving(conn, "use-target-ended", false);
                return;
            }
            if (conn.UseTargetMovingActionMirrored)
            {
                if (conn.UseTargetMovingActivate)
                {
                    TickActionMirroredActivateMoving(conn, state, player, tickIndex, targetFixedX, targetFixedY);
                    return;
                }
                TickActionMirroredUseTargetMoving(conn, state, player, tickIndex, targetFixedX, targetFixedY);
                if (playerEntityId != 0 && conn.ActiveUseTargetFlags < 100)
                    Combat.WeaponUseRuntime.Instance.TryBeginDeferredPlayerMeleeUseTargetState(playerEntityId, conn.ActiveUseTargetId, tickIndex);
                if (conn.HasActiveUseTarget
                    && conn.ActiveUseTargetFlags >= 100
                    && conn.ActiveUseTargetInitUseEvaluationTick == tickIndex
                    && conn.ActiveUseTargetInitUsePassed)
                {
                    if (TryQueueActiveSkillUseTarget(conn, "use-target-init"))
                        Debug.LogError($"[USE-TARGET-MOVING] state=0x18-busy conn={conn.ConnId} target={conn.ActiveUseTargetId} tick={tickIndex} sourceFunction=UseTarget::States@0x00548370->ActiveSkill::use@0x005390E0->ActiveSkill::isBusy@0x005394F0");
                    else
                        StopPlayerUseTargetActionSlot(conn, "UseTarget::CheckInitUse-ActiveSkill::use-false");
                }
                return;
            }
            bool relayOwnsPeerMovement = HasAnyActivePeerAction(conn);
            bool movementStreamOwnedByAction = conn.UseTargetMovingActionMirrored || relayOwnsPeerMovement;
            if (relayOwnsPeerMovement && !conn.HasActiveUseTarget)
                return;
            if (!conn.UseTargetMovingHasFixedState)
                SyncUseTargetMovingFixedState(conn);
            long fixedDx = (long)targetFixedX - conn.UseTargetMovingFixedX;
            long fixedDy = (long)targetFixedY - conn.UseTargetMovingFixedY;
            long fixedDistSq = fixedDx * fixedDx + fixedDy * fixedDy;
            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            if (!conn.UseTargetMovingActionMirrored && (liveDistSq <= USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED_SQ || fixedDistSq <= USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED_SQ))
            {
                bool sentHeading = !movementStreamOwnedByAction && SendUseTargetMovingHeadingTerminal(conn, targetFixedX, targetFixedY, pathMap);
                Debug.LogError($"[USE-TARGET-MOVING] state=arrived-threshold conn={conn.ConnId} liveDistSq={liveDistSq} fixedDistSq={fixedDistSq} thresholdSq={USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED_SQ}");
                CancelUseTargetMoving(conn, "arrived", !movementStreamOwnedByAction && !sentHeading);
                return;
            }
            if ((conn.UseTargetMovingPathRequestId <= 0 && conn.UseTargetMovingPathFixed.Count == 0)
                || UseTargetMovingPathTargetChanged(conn, targetFixedX, targetFixedY))
                BuildUseTargetMovingPath(conn, pathMap, targetFixedX, targetFixedY);
            if (conn.UseTargetMovingPathRequestId > 0)
                return;
            int moveTargetX = targetFixedX;
            int moveTargetY = targetFixedY;
            if (conn.UseTargetMovingPathFixed.Count > 0)
            {
                while (conn.UseTargetMovingPathIndex < conn.UseTargetMovingPathFixed.Count - 1
                    && DistanceSqFixed(conn.UseTargetMovingFixedX, conn.UseTargetMovingFixedY,
                        conn.UseTargetMovingPathFixed[conn.UseTargetMovingPathIndex].X,
                        conn.UseTargetMovingPathFixed[conn.UseTargetMovingPathIndex].Y) <= USE_TARGET_WAYPOINT_REACHED_DISTANCE_FIXED_SQ)
                {
                    conn.UseTargetMovingPathIndex++;
                }
                if (conn.UseTargetMovingPathIndex < conn.UseTargetMovingPathFixed.Count)
                {
                    moveTargetX = conn.UseTargetMovingPathFixed[conn.UseTargetMovingPathIndex].X;
                    moveTargetY = conn.UseTargetMovingPathFixed[conn.UseTargetMovingPathIndex].Y;
                }
            }
            int prevX = conn.UseTargetMovingFixedX;
            int prevY = conn.UseTargetMovingFixedY;
            int prevZ = conn.UseTargetMovingFixedZ;
            int prevHeading = conn.UseTargetMovingHeadingFixed;
            int avatarStepFixed = UnitMover.CacheSpeedPerFrame(state.SpeedF32, state.SpeedMod, out _);
            UnitMover.StepTowardFixedHeading(prevX, prevY, prevHeading, moveTargetX, moveTargetY, avatarStepFixed, USE_TARGET_AVATAR_TURN_RATE_FIXED, out int nextX, out int nextY, out int nextHeading, out bool arrived);
            ResolveUseTargetMovingMovementFixed(conn, pathMap, prevX, prevY, prevZ, nextX, nextY, out nextX, out nextY, out int nextZ);
            if (nextX == prevX && nextY == prevY && nextHeading == prevHeading)
            {
                if (conn.UseTargetMovingActionMirrored)
                {
                    Debug.LogError($"[USE-TARGET-MOVING] state=blocked-hold conn={conn.ConnId} target={conn.UseTargetMovingEntityId} reason=mirrored-action-path-blocked");
                    return;
                }
                CancelUseTargetMoving(conn, "blocked");
                return;
            }
            ApplyUseTargetMovingPosition(conn, nextX, nextY, nextHeading, pathMap, nextZ);
            if (!movementStreamOwnedByAction)
            {
                SendUseTargetMovingRecord(conn, prevHeading, prevX, prevY, nextHeading, nextX, nextY, false);
                CheckPendingPortalActivation(conn);
                if (!conn.AllowFlush)
                    return;
            }
            if (arrived && conn.UseTargetMovingPathFixed.Count > 0 && conn.UseTargetMovingPathIndex < conn.UseTargetMovingPathFixed.Count - 1)
            {
                conn.UseTargetMovingPathIndex++;
                arrived = false;
            }
            long remainDx = (long)targetFixedX - nextX;
            long remainDy = (long)targetFixedY - nextY;
            long remainDistSq = remainDx * remainDx + remainDy * remainDy;
            if (!conn.UseTargetMovingActionMirrored && (arrived || remainDistSq <= USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED_SQ))
            {
                Debug.LogError($"[USE-TARGET-MOVING] state=arrived-threshold conn={conn.ConnId} remainDistSq={remainDistSq} thresholdSq={USE_TARGET_MOVING_VISUAL_STOP_DISTANCE_FIXED_SQ} arrived={arrived}");
                CancelUseTargetMoving(conn, "arrived", !movementStreamOwnedByAction);
            }
        }

        private void FlushAllQueues(bool heartbeatIfEmpty = false, int nativeMoverRecordBudgetPerComponent = CLIENT_ENTITY_MOVEMENT_RECORD_BUDGET_PER_FLUSH, uint clientEntityUpdateSerial = 0)
        {
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
                FlushConnQueue(conn, heartbeatIfEmpty, nativeMoverRecordBudgetPerComponent, clientEntityUpdateSerial);
        }

        private void FlushConnQueue(RRConnection conn, bool heartbeatIfEmpty = false, int nativeMoverRecordBudgetPerComponent = CLIENT_ENTITY_MOVEMENT_RECORD_BUDGET_PER_FLUSH, uint clientEntityUpdateSerial = 0)
        {
            if (conn == null || !conn.IsConnected || !conn.AllowFlush)
                return;
            lock (conn.SendLock)
            {
            if (conn.PortalClientEntityEpochClosing && conn.PendingPortalTransitionReflected)
            {
                int dropped = conn.MessageQueue.Count;
                conn.MessageQueue.Clear();
                if (dropped > 0)
                    Debug.LogError($"[PORTAL-EPOCH] conn={conn.ConnId} dropped={dropped} state=closed-await-next-writer responseSerial={conn.PendingPortalTransitionResponseWriterSerial} currentSerial={clientEntityUpdateSerial}");
                return;
            }
            if (clientEntityUpdateSerial != 0 && conn.LastClientEntityUpdateSerial == clientEntityUpdateSerial)
                return;
            bool queueEmpty = conn.MessageQueue.Count == 0;
            if (queueEmpty && !(heartbeatIfEmpty && conn.IsSpawned))
                return;
            var messageRoles = new List<ClientEntityMessageRole>();
            var messages = conn.MessageQueue.DequeueAll(nativeMoverRecordBudgetPerComponent, messageRoles);
            KeepPendingPortalResponseOnly(conn, messages);
            RewriteUnavailablePendingPlayerUseTargetActionMessages(conn, messages, _combatTick);
            RewritePeerPlayerActionMessages(conn, messages, _combatTick);
            if (messages.Count == 0)
            {
                if (conn.PortalClientEntityEpochClosing)
                    return;
                if (!(heartbeatIfEmpty && conn.IsSpawned))
                    return;
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);

            foreach (var queuedMessage in messages)
            {
                writer.WriteBytes(queuedMessage);
            }

            writer.WriteByte(0x06);

            byte[] packet = writer.ToArray();
            if (SendCompressedAImmediate(
                conn,
                0x01,
                0x0F,
                packet,
                InferSendCompressedAEntitySynchInfoContext(conn, packet),
                "CLIENT-ENTITY-FLUSH"))
            {
                LogClientEntityAdmissionBlock(conn, messages, _combatTick, clientEntityUpdateSerial);
                AdmitPeerPlayerActionResponses(conn, messages, _combatTick);
                MarkPendingPlayerUseTargetActionPacketsFlushed(
                    conn,
                    messages,
                    _combatTick,
                    _combatTick);
                MarkPendingPlayerActivateActionPacketFlushed(
                    conn,
                    messages,
                    _combatTick);
                MarkPendingPlayerUsePositionActionPacketsFlushed(
                    conn,
                    messages,
                    _combatTick,
                    _combatTick);
                MarkPendingOwnerClientControlInputsFlushed(conn, messages, messageRoles, _combatTick);
                MarkPendingGroupGotoWarpPacketsFlushed(
                    conn,
                    messages,
                    _combatTick,
                    _combatTick);
                MarkPendingMonsterBehaviorMessagesFlushed(
                    conn,
                    messages,
                    _combatTick);
                MarkPendingPlayerCancelActionPacketsFlushed(
                    conn,
                    messages,
                    _combatTick,
                    _combatTick);
                MarkPendingSelfCastActionResponsePacketsFlushed(
                    conn,
                    messages,
                    _combatTick,
                    _combatTick);
                MarkPendingSummonInitializationsFlushed(conn, messages, _combatTick);
                QueuePendingClientActionInputBatch(conn, messages.Count, _combatTick);
                _unitContainer?.ApplyPendingConsumableModifierAdmissions(conn, messages, _combatTick);
                ApplyPendingHotbarPassiveAdmissions(conn, messages, _combatTick);
                _equipment?.ApplyPendingAdmissions(conn, messages);
                AdmitPeerUnitFollowClientRecords(conn, messages, clientEntityUpdateSerial);
                AdmitOwnerAckFollowClientRecords(conn, messages, clientEntityUpdateSerial);
                BlingGnomeRuntime.Instance.MarkUnSpawnPacketsFlushed(conn, messages, _combatTick);
                MarkPendingPortalTransitionResponseFlushed(conn, messages, _combatTick, clientEntityUpdateSerial);
                if (clientEntityUpdateSerial != 0)
                    conn.LastClientEntityUpdateSerial = clientEntityUpdateSerial;
            }
            }
        }

        private void MarkPendingOwnerClientControlInputsFlushed(RRConnection conn, IReadOnlyList<byte[]> messages, IReadOnlyList<ClientEntityMessageRole> messageRoles, uint packetWriterTick)
        {
            if (conn == null || messages == null || messageRoles == null || messages.Count != messageRoles.Count)
                return;
            for (int messageIndex = 0; messageIndex < messageRoles.Count; messageIndex++)
            {
                if (messageRoles[messageIndex] != ClientEntityMessageRole.OwnerClientControlReset)
                    continue;
                byte[] message = messages[messageIndex];
                ushort componentId = message != null && message.Length >= 3 && message[0] == 0x35
                    ? (ushort)(message[1] | (message[2] << 8))
                    : (ushort)0;
                if (componentId == 0)
                    throw new InvalidOperationException("Owner client control reset is missing its UnitBehavior component");
                _pendingOwnerClientControlInputs.Add(new PendingOwnerClientControlInput
                {
                    ConnId = conn.ConnId,
                    ComponentId = componentId,
                    PacketWriterTick = packetWriterTick,
                    SimulationApplyTick = packetWriterTick,
                    WireMessageIndex = messageIndex
                });
                Debug.LogError($"[OWNER-CONTROL-INPUT] state=writer-admit conn={conn.ConnId} component={componentId} packetWriterTick={packetWriterTick} simulationApplyTick={packetWriterTick} messageIndex={messageIndex} sequence=false,true sourceFunction=ClientEntityManager::processMessage@0x005DA460->UnitBehavior::processUpdate@0x00520020");
            }
        }

        private void ApplyPendingOwnerClientControlInputs(uint simulationTick)
        {
            if (_pendingOwnerClientControlInputs.Count == 0)
                return;
            var pending = new List<PendingOwnerClientControlInput>(_pendingOwnerClientControlInputs);
            _pendingOwnerClientControlInputs.Clear();
            foreach (PendingOwnerClientControlInput input in pending)
            {
                if (!IsPendingClientActionMessageSelected(input.ConnId, input.PacketWriterTick, input.WireMessageIndex)
                    || input.SimulationApplyTick > simulationTick)
                {
                    _pendingOwnerClientControlInputs.Add(input);
                    continue;
                }
                if (!_connections.TryGetValue(input.ConnId, out RRConnection conn)
                    || conn == null
                    || !conn.IsConnected
                    || conn.UnitBehaviorId != input.ComponentId)
                    continue;
                int fixedX = conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedX
                    : conn.HasOwnerAckFollowClientPosition
                        ? conn.OwnerAckFollowClientPosFixedX
                        : conn.PlayerPosFixedX;
                int fixedY = conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedY
                    : conn.HasOwnerAckFollowClientPosition
                        ? conn.OwnerAckFollowClientPosFixedY
                        : conn.PlayerPosFixedY;
                int fixedZ = conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientPosFixedZ
                    : conn.HasOwnerAckFollowClientPosition
                        ? conn.OwnerAckFollowClientPosFixedZ
                        : conn.PlayerPosFixedZ;
                int headingFixed = conn.HasUnitFollowClientPosition
                    ? conn.UnitFollowClientHeadingFixed
                    : conn.HasOwnerAckFollowClientPosition
                        ? conn.OwnerAckFollowClientHeadingFixed
                        : conn.PlayerHeadingFixed;
                StopFollowingClientAtCurrentPosition(
                    conn,
                    fixedX,
                    fixedY,
                    fixedZ,
                    headingFixed,
                    "ClientUnitBehavior::SuspendClientMovement@0x00518E10->UnitBehavior::StopFollowingClient@0x005203A0");
                if (conn.UsePositionActionMirrored)
                {
                    conn.UsePositionActionStoppedFollowClient = true;
                    MarkPeerUsePositionStoppedFollowingClient(conn, simulationTick);
                }
                Debug.LogError($"[OWNER-CONTROL-INPUT] state=apply conn={conn.ConnId} component={input.ComponentId} value=false packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} messageIndex={input.WireMessageIndex} positionFixed=({fixedX},{fixedY},{fixedZ}) headingFixed={headingFixed} sourceFunction=UnitBehavior::processUpdate@0x00520020->ClientUnitBehavior::SuspendClientMovement@0x00518E10");
                Debug.LogError($"[OWNER-CONTROL-INPUT] state=apply conn={conn.ConnId} component={input.ComponentId} value=true packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} messageIndex={input.WireMessageIndex} positionFixed=({fixedX},{fixedY},{fixedZ}) headingFixed={headingFixed} sourceFunction=UnitBehavior::processUpdate@0x00520020");
            }
        }

        private static void KeepPendingPortalResponseOnly(RRConnection conn, List<byte[]> messages)
        {
            if (conn == null
                || !conn.PendingPortalTransition
                || !conn.PortalClientEntityEpochClosing
                || conn.PendingPortalTransitionReflected
                || messages == null)
                return;
            ushort componentId = conn.PendingPortalTransitionComponentId;
            ushort targetEntityId = conn.PendingPortalTransitionTargetEntityId;
            byte responseId = conn.PendingPortalTransitionResponseId;
            byte sessionId = conn.PendingPortalTransitionSessionId;
            int terminalIndex = -1;
            for (int i = 0; i < messages.Count; i++)
            {
                byte[] message = messages[i];
                if (message == null || message.Length < 9)
                    continue;
                ushort candidateComponentId = (ushort)(message[1] | (message[2] << 8));
                ushort candidateTargetEntityId = (ushort)(message[7] | (message[8] << 8));
                if (message[0] != 0x35
                    || candidateComponentId != componentId
                    || message[3] != 0x01
                    || message[4] != responseId
                    || message[5] != 0x06
                    || message[6] != sessionId
                    || candidateTargetEntityId != targetEntityId)
                    continue;
                terminalIndex = i;
                break;
            }
            if (terminalIndex < 0)
            {
                int dropped = messages.Count;
                messages.Clear();
                Debug.LogError($"[PORTAL-EPOCH] conn={conn.ConnId} component={componentId} dropped={dropped} state=terminal-response-missing");
                return;
            }
            byte[] terminalResponse = messages[terminalIndex];
            int droppedBefore = terminalIndex;
            int droppedAfter = messages.Count - terminalIndex - 1;
            messages.Clear();
            messages.Add(terminalResponse);
            Debug.LogError($"[PORTAL-EPOCH] conn={conn.ConnId} component={componentId} terminalIndex={terminalIndex} droppedBefore={droppedBefore} droppedAfter={droppedAfter} state=terminal-response-only");
        }

        private static void LogClientEntityAdmissionBlock(
            RRConnection conn,
            IReadOnlyList<byte[]> messages,
            uint packetWriterTick,
            uint clientEntityUpdateSerial)
        {
            if (conn == null || messages == null || messages.Count == 0)
                return;
            var entries = new StringBuilder();
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex] ?? Array.Empty<byte>();
                uint hash = 2166136261u;
                for (int byteIndex = 0; byteIndex < message.Length; byteIndex++)
                    hash = unchecked((hash ^ message[byteIndex]) * 16777619u);
                if (messageIndex != 0)
                    entries.Append(',');
                int prefixLength = Math.Min(12, message.Length);
                entries.Append(messageIndex)
                    .Append(':')
                    .Append(message.Length)
                    .Append(':')
                    .Append(prefixLength == 0 ? "" : Convert.ToHexString(message, 0, prefixLength))
                    .Append(':')
                    .Append(hash.ToString("X8"));
            }
            Debug.LogError($"[CEM-ADMISSION] conn={conn.ConnId} avatar={conn.Avatar?.Id ?? 0} packetWriterTick={packetWriterTick} serial={clientEntityUpdateSerial} messageCount={messages.Count} ordered={entries} phase=process-message-before-entities sourceFunction=ClientEntityManager::update@0x005D9E30->ClientEntityManager::processMessage@0x005DA460");
        }

        private void AdmitOwnerAckFollowClientRecords(RRConnection conn, IReadOnlyList<byte[]> messages, uint clientEntityUpdateSerial)
        {
            if (conn == null || conn.UnitBehaviorId == 0 || conn.UnitBehaviorId > ushort.MaxValue)
                return;
            int records = MessageQueue.CountUnitMoverRecords(messages, (ushort)conn.UnitBehaviorId);
            if (records <= 0)
                return;
            List<(byte MoveType, int Heading, int X, int Y)> movementSamples = MessageQueue.ReadUnitMoverRecordsForSession(
                messages,
                (ushort)conn.UnitBehaviorId,
                conn.MovementGeneration,
                out int observedRecords,
                out int rejectedRecords);
            if (rejectedRecords > 0)
            {
                Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=reject-generation conn={conn.ConnId} expectedSession=0x{conn.MovementGeneration:X2} advertisedRecords={records} observedRecords={observedRecords} rejectedRecords={rejectedRecords} acceptedRecords={movementSamples.Count} tick={_combatTick} clientEntityUpdateSerial={clientEntityUpdateSerial} sourceFunction=UnitBehavior::processUpdate@0x00520020->UnitMover::ReadUpdate@0x005356A0");
            }
            if (movementSamples.Count == 0)
                return;
            if (rejectedRecords == 0
                && movementSamples.Count != MessageQueue.ReadUnitMoverRecords(messages, (ushort)conn.UnitBehaviorId).Count)
                throw new InvalidOperationException($"Owner ACK FollowClient record count mismatch counted={records} parsed={movementSamples.Count}");
            if (ResolveCanonicalUnitFollowClientViewer(conn) == null)
                AdmitUnitFollowClientRecords(conn, movementSamples, _combatTick, clientEntityUpdateSerial, "owner");
            AdmitOwnerFollowClientSamples(
                conn,
                movementSamples,
                0,
                _combatTick,
                clientEntityUpdateSerial);
        }

        private static bool TryReadCanonicalUnitFollowClientUpdate(
            byte[] message,
            int offset,
            ushort remoteBehaviorId,
            out byte subtype,
            out byte actionOpcode,
            out int nextOffset)
        {
            subtype = 0;
            actionOpcode = 0;
            nextOffset = offset;
            if (message == null
                || offset < 0
                || offset + 4 > message.Length
                || message[offset] != 0x35
                || (ushort)(message[offset + 1] | (message[offset + 2] << 8)) != remoteBehaviorId)
                return false;

            subtype = message[offset + 3];
            int bodyLength;
            switch (subtype)
            {
                case 0x03:
                    bodyLength = 5;
                    break;
                case 0x05:
                    bodyLength = 4;
                    break;
                case 0x64:
                    bodyLength = 5;
                    break;
                case 0x65:
                    if (offset + 6 > message.Length)
                        return false;
                    bodyLength = checked(6 + message[offset + 5] * UnitMoverUpdateRecordSize);
                    break;
                case 0x04:
                    if (offset + 5 > message.Length)
                        return false;
                    actionOpcode = message[offset + 4];
                    bodyLength = actionOpcode switch
                    {
                        0x06 => 8,
                        0x50 => 9,
                        0x51 => 19,
                        0x52 => 7,
                        _ => 0
                    };
                    if (bodyLength == 0)
                        return false;
                    break;
                case 0x01:
                    if (offset + 6 > message.Length)
                        return false;
                    actionOpcode = message[offset + 5];
                    bodyLength = actionOpcode switch
                    {
                        0x06 => 9,
                        0x50 => 10,
                        0x51 => 20,
                        0x52 => 8,
                        _ => 0
                    };
                    if (bodyLength == 0)
                        return false;
                    break;
                default:
                    return false;
            }

            int synchOffset = checked(offset + bodyLength);
            if (synchOffset >= message.Length)
                return false;
            int synchLength = (message[synchOffset] & 0x02) != 0 ? 5 : 1;
            nextOffset = checked(synchOffset + synchLength);
            return nextOffset <= message.Length;
        }

        private void AdmitCanonicalUnitFollowClientWriterState(
            RRConnection viewer,
            RRConnection source,
            IReadOnlyList<byte[]> messages,
            ushort remoteBehaviorId,
            uint clientEntityUpdateSerial)
        {
            bool observed = false;
            int discardedRecords = 0;
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                int offset = 0;
                while (TryReadCanonicalUnitFollowClientUpdate(
                    message,
                    offset,
                    remoteBehaviorId,
                    out byte subtype,
                    out byte actionOpcode,
                    out int nextOffset))
                {
                    observed = true;
                    if (subtype == 0x04)
                    {
                        int usePositionFollowClientRecords = actionOpcode == 0x51
                            ? CountPeerFollowClientRecordsAtActionAdmission(source, messages, messageIndex, remoteBehaviorId)
                            : 0;
                        bool preserveQueuedFollowClient = actionOpcode == 0x51
                            && usePositionFollowClientRecords > 0;
                        source.UnitFollowClientActionActive = true;
                        source.UnitFollowClientFollowingClient = preserveQueuedFollowClient;
                        source.UnitFollowClientMovingThisFrame = false;
                        if (!preserveQueuedFollowClient)
                        {
                            discardedRecords = checked(discardedRecords + source.UnitFollowClientMovementSamples.Count);
                            source.UnitFollowClientMovementSamples.Clear();
                            source.UnitFollowClientAppliedOrdinal = source.UnitFollowClientReceivedOrdinal;
                        }
                    }
                    else if (subtype == 0x05)
                    {
                        source.UnitFollowClientActionActive = false;
                        source.UnitFollowClientFollowingClient = source.UnitFollowClientClientControlEnabled;
                    }
                    else if (subtype == 0x64)
                    {
                        source.UnitFollowClientClientControlEnabled = message[offset + 4] != 0;
                        if (source.UnitFollowClientClientControlEnabled && !source.UnitFollowClientActionActive)
                            source.UnitFollowClientFollowingClient = true;
                    }
                    offset = nextOffset;
                }
            }
            if (!observed)
                return;
            source.UnitFollowClientWriterStateInitialized = true;
            Debug.LogError($"[UNIT-FOLLOWCLIENT] state=writer-state conn={source.ConnId} admissionSource={viewer.LoginName ?? viewer.ConnId.ToString()} tick={_combatTick} clientEntityUpdateSerial={clientEntityUpdateSerial} actionActive={source.UnitFollowClientActionActive} clientControl={source.UnitFollowClientClientControlEnabled} followingClient={source.UnitFollowClientFollowingClient} discardedRecords={discardedRecords} receivedOrdinal={source.UnitFollowClientReceivedOrdinal} appliedOrdinal={source.UnitFollowClientAppliedOrdinal} queued={source.UnitFollowClientMovementSamples.Count} sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->UnitBehavior::UpdateFollowClient@0x00520D50");
        }

        private void AdmitPeerUnitFollowClientRecords(RRConnection viewer, IReadOnlyList<byte[]> messages, uint clientEntityUpdateSerial)
        {
            if (viewer == null || messages == null || messages.Count == 0)
                return;
            foreach (RRConnection source in GetConnectionInsertionOrderSnapshot())
            {
                if (source == null || source == viewer || ResolveCanonicalUnitFollowClientViewer(source) != viewer)
                    continue;
                if (!TryResolveRemoteBehaviorForViewer(viewer, source, out ushort remoteBehaviorId))
                    continue;
                AdmitCanonicalUnitFollowClientWriterState(viewer, source, messages, remoteBehaviorId, clientEntityUpdateSerial);
                int advertisedRecords = MessageQueue.CountUnitMoverRecords(messages, remoteBehaviorId);
                byte expectedSessionId = GetRemoteBehaviorSession(viewer, source);
                List<(byte MoveType, int Heading, int X, int Y)> movementSamples = MessageQueue.ReadUnitMoverRecordsForSession(
                    messages,
                    remoteBehaviorId,
                    expectedSessionId,
                    out int observedRecords,
                    out int rejectedRecords);
                if (rejectedRecords > 0)
                {
                    Debug.LogError($"[UNIT-FOLLOWCLIENT] state=reject-generation viewer={viewer.LoginName ?? viewer.ConnId.ToString()} source={source.LoginName ?? source.ConnId.ToString()} behavior={remoteBehaviorId} expectedSession=0x{expectedSessionId:X2} advertisedRecords={advertisedRecords} observedRecords={observedRecords} rejectedRecords={rejectedRecords} acceptedRecords={movementSamples.Count} tick={_combatTick} clientEntityUpdateSerial={clientEntityUpdateSerial} sourceFunction=UnitBehavior::processUpdate@0x00520020->UnitMover::ReadUpdate@0x005356A0");
                }
                if (movementSamples.Count == 0)
                    continue;
                if (rejectedRecords == 0
                    && movementSamples.Count != MessageQueue.ReadUnitMoverRecords(messages, remoteBehaviorId).Count)
                    throw new InvalidOperationException($"Peer FollowClient record count mismatch counted={advertisedRecords} parsed={movementSamples.Count}");
                source.UnitFollowClientWriterStateInitialized = true;
                AdmitUnitFollowClientRecords(source, movementSamples, _combatTick, clientEntityUpdateSerial, viewer.LoginName ?? viewer.ConnId.ToString());
            }
        }

        private RRConnection ResolveCanonicalUnitFollowClientViewer(RRConnection source)
        {
            if (source == null)
                return null;
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || viewer == source || !viewer.IsConnected || !viewer.IsSpawned || !viewer.AllowFlush || !viewer.TickUpdatesActive)
                    continue;
                if (!IsSameMovementRuntime(viewer, source))
                    continue;
                if (TryResolveRemoteBehaviorForViewer(viewer, source, out _))
                    return viewer;
            }
            return null;
        }

        private void AdmitUnitFollowClientRecords(
            RRConnection conn,
            IReadOnlyList<(byte MoveType, int Heading, int X, int Y)> movementSamples,
            uint simulationApplyTick,
            uint clientEntityUpdateSerial,
            string admissionSource)
        {
            if (conn == null || movementSamples == null || movementSamples.Count == 0)
                return;
            if (!conn.HasUnitFollowClientPosition)
            {
                int initialX = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedX : conn.PlayerPosFixedX;
                int initialY = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedY : conn.PlayerPosFixedY;
                int initialZ = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedZ : conn.PlayerPosFixedZ;
                int initialHeading = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarHeadingFixed : conn.PlayerHeadingFixed;
                SetUnitFollowClientPosition(conn, initialX, initialY, initialZ, initialHeading, false);
            }
            for (int index = 0; index < movementSamples.Count; index++)
            {
                var sample = movementSamples[index];
                conn.UnitFollowClientReceivedOrdinal = checked(conn.UnitFollowClientReceivedOrdinal + 1);
                conn.UnitFollowClientMovementSamples.Enqueue((
                    sample.MoveType,
                    WrapHeadingFixed(sample.Heading),
                    sample.X,
                    sample.Y,
                    simulationApplyTick,
                    conn.UnitFollowClientReceivedOrdinal));
            }
            SyncUnitBehaviorPredictedLocation2D(conn);
            Debug.LogError($"[UNIT-FOLLOWCLIENT] state=writer-admit conn={conn.ConnId} admissionSource={admissionSource} tick={_combatTick} simulationApplyTick={simulationApplyTick} clientEntityUpdateSerial={clientEntityUpdateSerial} records={movementSamples.Count} receivedOrdinal={conn.UnitFollowClientReceivedOrdinal} appliedOrdinal={conn.UnitFollowClientAppliedOrdinal} queued={conn.UnitFollowClientMovementSamples.Count} sourceFunction=ClientEntityManager::processMessage@0x005DA460->UnitBehavior::UpdateFollowClient@0x00520D50");
        }

        private void AdmitOwnerFollowClientSamples(
            RRConnection conn,
            IReadOnlyList<(byte MoveType, int Heading, int X, int Y)> movementSamples,
            int startIndex,
            uint simulationApplyTick,
            uint clientEntityUpdateSerial)
        {
            if (conn == null || movementSamples == null || startIndex < 0 || startIndex >= movementSamples.Count)
                return;
            if (!conn.HasOwnerAckFollowClientPosition)
            {
                int initialX = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedX : conn.PlayerPosFixedX;
                int initialY = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedY : conn.PlayerPosFixedY;
                int initialZ = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarPosFixedZ : conn.PlayerPosFixedZ;
                int initialHeading = conn.HasReflectedAvatarPosition ? conn.ReflectedAvatarHeadingFixed : conn.PlayerHeadingFixed;
                SetOwnerAckFollowClientPosition(conn, initialX, initialY, initialZ, initialHeading, false);
            }
            int admittedRecords = 0;
            for (int i = startIndex; i < movementSamples.Count; i++)
            {
                var sample = movementSamples[i];
                conn.OwnerAckFollowClientReceivedOrdinal = checked(conn.OwnerAckFollowClientReceivedOrdinal + 1);
                conn.OwnerAckFollowClientMovementSamples.Enqueue((
                    sample.MoveType,
                    sample.Heading,
                    sample.X,
                    sample.Y,
                    simulationApplyTick,
                    conn.OwnerAckFollowClientReceivedOrdinal));
                admittedRecords++;
            }
            Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=writer-admit conn={conn.ConnId} tick={_combatTick} simulationApplyTick={simulationApplyTick} clientEntityUpdateSerial={clientEntityUpdateSerial} records={admittedRecords} startIndex={startIndex} receivedOrdinal={conn.OwnerAckFollowClientReceivedOrdinal} appliedOrdinal={conn.OwnerAckFollowClientAppliedOrdinal} queued={conn.OwnerAckFollowClientMovementSamples.Count} unitWaitActive={conn.UseTargetMovingUnitFollowClientWaitActive} unitWaitThroughOrdinal={conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal} sourceFunction=UnitBehavior::processUpdate@0x00520020->ClientEntityManager::processMessage@0x005DA460");
        }

        private static int CacheFollowClientSpeedPerFrame(
            PlayerState state,
            byte moveType,
            out int speedModF32,
            out int effectiveSpeedF32)
        {
            if (state == null)
                throw new InvalidOperationException("FollowClient player state is missing");
            speedModF32 = checked(state.SpeedMod << 8);
            if ((moveType & 0x04) != 0)
                speedModF32 = UnitMover.ApplyPercentModifierF32(speedModF32, UNIT_MOVER_AVATAR_TOUCHING_SPEED_MOD_F32);
            return UnitMover.CacheSpeedPerFrameF32(state.SpeedF32, speedModF32, out effectiveSpeedF32);
        }

        private void AdvanceOwnerAckFollowClientQueue(uint playerEntityId, uint simulationTick)
        {
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            if (conn == null)
                return;
            if (ActionMirroredMoveToPointOwnsUnitMover(conn))
            {
                SyncOwnerAckFollowClientCurrentState(conn);
                SyncReflectedAvatarCombatPosition(conn);
                return;
            }
            conn.OwnerAckFollowClientMovingThisFrame = false;
            SyncOwnerAckFollowClientCurrentState(conn);
            if (conn.OwnerAckFollowClientAppliedOrdinal >= conn.OwnerAckFollowClientReceivedOrdinal)
            {
                SyncReflectedAvatarCombatPosition(conn);
                return;
            }
            if (conn.OwnerAckFollowClientMovementSamples.Count == 0)
                throw new InvalidOperationException("Owner ACK FollowClient geometry queue underflow");
            if (!conn.HasOwnerAckFollowClientPosition)
                throw new InvalidOperationException("Owner ACK FollowClient position is not initialized");

            var sample = conn.OwnerAckFollowClientMovementSamples.Peek();
            ulong nextOrdinal = checked(conn.OwnerAckFollowClientAppliedOrdinal + 1);
            if (sample.Ordinal != nextOrdinal)
                throw new InvalidOperationException($"Owner ACK FollowClient ordinal mismatch expected={nextOrdinal} actual={sample.Ordinal}");
            if (sample.SimulationApplyTick > simulationTick)
            {
                SyncReflectedAvatarCombatPosition(conn);
                Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=awaiting-simulation-admission conn={conn.ConnId} tick={simulationTick} simulationApplyTick={sample.SimulationApplyTick} receivedOrdinal={conn.OwnerAckFollowClientReceivedOrdinal} appliedOrdinal={conn.OwnerAckFollowClientAppliedOrdinal} pending={GetOwnerAckFollowClientPendingRecords(conn)} queued={conn.OwnerAckFollowClientMovementSamples.Count} frontOrdinal={sample.Ordinal} sourceFunction=ClientEntityManager::processMessage@0x005DA460->ClientEntityManager::updateEntities@0x005DA1B0");
                return;
            }

            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            int stepFixed = CacheFollowClientSpeedPerFrame(state, sample.MoveType, out int speedModF32, out int effectiveSpeedF32);
            int currentX = conn.OwnerAckFollowClientPosFixedX;
            int currentY = conn.OwnerAckFollowClientPosFixedY;
            int currentZ = conn.OwnerAckFollowClientPosFixedZ;
            int currentHeading = conn.OwnerAckFollowClientHeadingFixed;
            bool actionOwnsFacing = ActionOwnsFollowClientFacing(conn);
            int nextHeading = actionOwnsFacing
                ? currentHeading
                : UnitMover.InterpolateHeading(currentHeading, sample.Heading, USE_TARGET_AVATAR_TURN_RATE_FIXED);
            bool facingReached = actionOwnsFacing || nextHeading == sample.Heading;
            ResolveFollowClientCandidateFixed(currentX, currentY, sample.X, sample.Y, stepFixed, out int candidateX, out int candidateY, out long lengthSqFixedLong);

            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            bool collided = false;
            int nextX;
            int nextY;
            int nextZ;
            if (lengthSqFixedLong == 0)
            {
                nextX = sample.X;
                nextY = sample.Y;
                nextZ = ResolveConnectionGroundHeightFixed(conn, pathMap, sample.X, sample.Y, currentZ);
            }
            else if (pathMap != null)
                collided = pathMap.CastGroundRaySlideFixed(currentX, currentY, currentZ, candidateX, candidateY, out nextX, out nextY, out nextZ);
            else
            {
                nextX = candidateX;
                nextY = candidateY;
                nextZ = ResolveConnectionGroundHeightFixed(conn, null, candidateX, candidateY, currentZ);
            }

            SetOwnerAckFollowClientPosition(conn, nextX, nextY, nextZ, nextHeading, false);
            conn.OwnerAckFollowClientMovingThisFrame = (sample.MoveType & 0x01) == 0 && (nextX != currentX || nextY != currentY);
            SyncOwnerAckFollowClientCurrentState(conn);
            long remainingX = (long)sample.X - nextX;
            long remainingY = (long)sample.Y - nextY;
            bool positionReached = ((remainingX * remainingX) >> 8) + ((remainingY * remainingY) >> 8) == 0;
            if (!collided && facingReached)
            {
                conn.OwnerAckFollowClientMovementSamples.Dequeue();
                conn.OwnerAckFollowClientAppliedOrdinal = checked(conn.OwnerAckFollowClientAppliedOrdinal + 1);
            }
            SyncReflectedAvatarCombatPosition(conn);
            Debug.LogError($"[OWNER-ACK-FOLLOWCLIENT] state=drain conn={conn.ConnId} tick={simulationTick} receivedOrdinal={conn.OwnerAckFollowClientReceivedOrdinal} appliedOrdinal={conn.OwnerAckFollowClientAppliedOrdinal} pending={GetOwnerAckFollowClientPendingRecords(conn)} queued={conn.OwnerAckFollowClientMovementSamples.Count} frontOrdinal={sample.Ordinal} moveType=0x{sample.MoveType:X2} speedModF32={speedModF32} effectiveSpeedF32={effectiveSpeedF32} stepFixed={stepFixed} positionFixed=({conn.OwnerAckFollowClientPosFixedX},{conn.OwnerAckFollowClientPosFixedY},{conn.OwnerAckFollowClientPosFixedZ}) headingFixed={conn.OwnerAckFollowClientHeadingFixed} moving={conn.OwnerAckFollowClientMovingThisFrame} actionOwnsFacing={actionOwnsFacing} reached={positionReached} facing={facingReached} collided={collided} unitWaitActive={conn.UseTargetMovingUnitFollowClientWaitActive} unitWaitThroughOrdinal={conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal} sourceFunction=UnitBehavior::update@0x0051FBB0->UnitMover::Update@0x005357C0->UnitBehavior::UpdateMovement@0x00520C90->UnitBehavior::UpdateFollowClient@0x00520D50");
        }

        private void AdvanceUnitFollowClientQueue(uint playerEntityId, uint simulationTick)
        {
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            if (conn == null)
                return;
            if (CanonicalActionOwnsUnitMover(conn))
            {
                if (CanonicalActionMirroredMoveToPointOwnsUnitMover(conn))
                {
                    int fixedX = conn.HasOwnerAckFollowClientPosition ? conn.OwnerAckFollowClientPosFixedX : conn.PlayerPosFixedX;
                    int fixedY = conn.HasOwnerAckFollowClientPosition ? conn.OwnerAckFollowClientPosFixedY : conn.PlayerPosFixedY;
                    int fixedZ = conn.HasOwnerAckFollowClientPosition ? conn.OwnerAckFollowClientPosFixedZ : conn.PlayerPosFixedZ;
                    int heading = conn.HasOwnerAckFollowClientPosition ? conn.OwnerAckFollowClientHeadingFixed : conn.PlayerHeadingFixed;
                    SetUnitFollowClientPosition(conn, fixedX, fixedY, fixedZ, heading, false);
                    conn.UnitFollowClientMovingThisFrame = conn.OwnerAckFollowClientMovingThisFrame;
                }
                else
                    conn.UnitFollowClientMovingThisFrame = false;
                conn.UnitFollowClientMoverMode = conn.OwnerUnitBehaviorMoverMode;
                SyncUnitFollowClientCurrentState(conn);
                return;
            }
            conn.UnitFollowClientMovingThisFrame = false;
            SyncUnitFollowClientCurrentState(conn);
            if (conn.UnitFollowClientAppliedOrdinal >= conn.UnitFollowClientReceivedOrdinal)
                return;
            if (conn.UnitFollowClientMovementSamples.Count == 0)
                throw new InvalidOperationException("Unit FollowClient geometry queue underflow");
            if (!conn.HasUnitFollowClientPosition)
                throw new InvalidOperationException("Unit FollowClient position is not initialized");

            var sample = conn.UnitFollowClientMovementSamples.Peek();
            ulong nextOrdinal = checked(conn.UnitFollowClientAppliedOrdinal + 1);
            if (sample.Ordinal != nextOrdinal)
                throw new InvalidOperationException($"Unit FollowClient ordinal mismatch expected={nextOrdinal} actual={sample.Ordinal}");
            if (sample.SimulationApplyTick > simulationTick)
                return;

            conn.UnitFollowClientMoverMode = UnitMover.FollowClientMode;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            int stepFixed = CacheFollowClientSpeedPerFrame(state, sample.MoveType, out int speedModF32, out int effectiveSpeedF32);
            int currentX = conn.UnitFollowClientPosFixedX;
            int currentY = conn.UnitFollowClientPosFixedY;
            int currentZ = conn.UnitFollowClientPosFixedZ;
            int currentHeading = conn.UnitFollowClientHeadingFixed;
            bool actionOwnsFacing = CanonicalActionOwnsFollowClientFacing(conn);
            int nextHeading = actionOwnsFacing
                ? currentHeading
                : UnitMover.InterpolateHeading(currentHeading, sample.Heading, USE_TARGET_AVATAR_TURN_RATE_FIXED);
            bool facingReached = actionOwnsFacing || nextHeading == sample.Heading;
            ResolveFollowClientCandidateFixed(currentX, currentY, sample.X, sample.Y, stepFixed, out int candidateX, out int candidateY, out long lengthSqFixedLong);

            PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
            bool collided = false;
            int nextX;
            int nextY;
            int nextZ;
            if (lengthSqFixedLong == 0)
            {
                nextX = sample.X;
                nextY = sample.Y;
                nextZ = ResolveConnectionGroundHeightFixed(conn, pathMap, sample.X, sample.Y, currentZ);
            }
            else if (pathMap != null)
                collided = pathMap.CastGroundRaySlideFixed(currentX, currentY, currentZ, candidateX, candidateY, out nextX, out nextY, out nextZ);
            else
            {
                nextX = candidateX;
                nextY = candidateY;
                nextZ = ResolveConnectionGroundHeightFixed(conn, null, candidateX, candidateY, currentZ);
            }

            SetUnitFollowClientPosition(conn, nextX, nextY, nextZ, nextHeading, false);
            conn.UnitFollowClientMovingThisFrame = (sample.MoveType & 0x01) == 0 && (nextX != currentX || nextY != currentY);
            SyncUnitFollowClientCurrentState(conn);
            long remainingX = (long)sample.X - nextX;
            long remainingY = (long)sample.Y - nextY;
            bool positionReached = ((remainingX * remainingX) >> 8) + ((remainingY * remainingY) >> 8) == 0;
            if (!collided && facingReached)
            {
                conn.UnitFollowClientMovementSamples.Dequeue();
                conn.UnitFollowClientAppliedOrdinal = checked(conn.UnitFollowClientAppliedOrdinal + 1);
                SyncUnitBehaviorPredictedLocation2D(conn);
            }
            Debug.LogError($"[UNIT-FOLLOWCLIENT] state=drain conn={conn.ConnId} tick={simulationTick} receivedOrdinal={conn.UnitFollowClientReceivedOrdinal} appliedOrdinal={conn.UnitFollowClientAppliedOrdinal} queued={conn.UnitFollowClientMovementSamples.Count} frontOrdinal={sample.Ordinal} moveType=0x{sample.MoveType:X2} speedModF32={speedModF32} effectiveSpeedF32={effectiveSpeedF32} stepFixed={stepFixed} writerStateInitialized={conn.UnitFollowClientWriterStateInitialized} actionActive={conn.UnitFollowClientActionActive} clientControl={conn.UnitFollowClientClientControlEnabled} followingClient={conn.UnitFollowClientFollowingClient} positionFixed=({conn.UnitFollowClientPosFixedX},{conn.UnitFollowClientPosFixedY},{conn.UnitFollowClientPosFixedZ}) currentHeadingFixed={currentHeading} sampleHeadingFixed={sample.Heading} nextHeadingFixed={nextHeading} turnRateFixed={USE_TARGET_AVATAR_TURN_RATE_FIXED} headingFixed={conn.UnitFollowClientHeadingFixed} moving={conn.UnitFollowClientMovingThisFrame} actionOwnsFacing={actionOwnsFacing} reached={positionReached} facing={facingReached} collided={collided} sourceFunction=UnitBehavior::UpdateFollowClient@0x00520D50->Unit::applyMovement@0x0050BD40");
        }

        private static bool ActionOwnsFollowClientFacing(RRConnection conn)
        {
            return conn.UseTargetMovingActionMirrored && conn.UseTargetMovingUsing
                || conn.UsePositionActionMirrored;
        }

        private static void ResolveFollowClientCandidateFixed(int currentX, int currentY, int targetX, int targetY, int stepFixed, out int candidateX, out int candidateY, out long lengthSqFixedLong)
        {
            candidateX = targetX;
            candidateY = targetY;
            long dx = (long)targetX - currentX;
            long dy = (long)targetY - currentY;
            lengthSqFixedLong = ((dx * dx) >> 8) + ((dy * dy) >> 8);
            int distanceFixed = UnitMover.TableSquareRoot((uint)Math.Min((long)uint.MaxValue, Math.Max(0L, lengthSqFixedLong)));
            if (distanceFixed > 0 && stepFixed < 1)
            {
                candidateX = currentX;
                candidateY = currentY;
                return;
            }
            if (distanceFixed <= stepFixed)
                return;
            int unitX = (int)((dx << 8) / distanceFixed);
            int unitY = (int)((dy << 8) / distanceFixed);
            candidateX = currentX + (int)(((long)unitX * stepFixed) >> 8);
            candidateY = currentY + (int)(((long)unitY * stepFixed) >> 8);
            long residualX = (long)targetX - candidateX;
            long residualY = (long)targetY - candidateY;
            long residualLengthSqFixed = ((residualX * residualX) >> 8) + ((residualY * residualY) >> 8);
            if (residualLengthSqFixed == 0)
            {
                candidateX = targetX;
                candidateY = targetY;
            }
        }

        private bool QueueClientEntityStream(RRConnection conn, byte[] data)
        {
            if (conn == null || data == null || data.Length == 0)
                return false;
            if (conn.PortalClientEntityEpochClosing)
            {
                Debug.LogError($"[PORTAL-EPOCH] conn={conn.ConnId} state=drop-client-entity-write pending={conn.PendingPortalTransition} reflected={conn.PendingPortalTransitionReflected}");
                return false;
            }
            byte[] inner = StripClientEntityStreamEnvelope(data);
            if (_pendingRoomClientTokens.ContainsKey(conn.ConnId)
                && inner.Length > 0
                && inner[inner.Length - 1] == 0x46)
                Array.Resize(ref inner, inner.Length - 1);
            if (inner.Length == 0)
                return false;
            conn.MessageQueue.Enqueue(inner);
            return true;
        }

        private bool QueueHotbarPassiveResponse(RRConnection conn, byte[] responsePrefix, ushort componentId, SavedCharacter savedChar)
        {
            if (conn == null || responsePrefix == null || responsePrefix.Length == 0 || componentId == 0)
                return false;
            byte[] capturedResponsePrefix = (byte[])responsePrefix.Clone();
            long sequence = System.Threading.Interlocked.Increment(ref _nextHotbarPassiveAdmissionSequence);
            var admission = new PendingHotbarPassiveAdmission
            {
                Sequence = sequence,
                ConnId = conn.ConnId,
                InstanceKey = ResolveConnectionInstanceKey(conn),
                AvatarEntityId = conn.Avatar != null ? (uint)Math.Max(0, conn.Avatar.Id) : 0u,
                ResponseMessage = null,
                Passives = CollectPassiveManipulators(savedChar),
                ReceivedTick = _combatTick
            };
            lock (_pendingHotbarPassiveAdmissionLock)
                _pendingHotbarPassiveAdmissions.Add(admission);
            conn.MessageQueue.EnqueueDeferred(() => BuildHotbarPassiveResponse(conn, capturedResponsePrefix, componentId, sequence), componentId: componentId);
            return true;
        }

        private byte[] BuildHotbarPassiveResponse(RRConnection conn, byte[] responsePrefix, ushort componentId, long sequence)
        {
            lock (_pendingHotbarPassiveAdmissionLock)
            {
                int admissionIndex = _pendingHotbarPassiveAdmissions.FindIndex(candidate => candidate.Sequence == sequence);
                if (admissionIndex < 0
                    || conn == null
                    || !conn.IsConnected
                    || responsePrefix == null
                    || responsePrefix.Length == 0
                    || componentId == 0)
                    return Array.Empty<byte>();
                PendingHotbarPassiveAdmission admission = _pendingHotbarPassiveAdmissions[admissionIndex];
                if (admission.ConnId != conn.ConnId
                    || !IsPendingConnectionInstanceCurrent(conn, admission.InstanceKey)
                    || admission.AvatarEntityId == 0
                    || conn.Avatar == null
                    || admission.AvatarEntityId != (uint)conn.Avatar.Id)
                {
                    _pendingHotbarPassiveAdmissions.RemoveAt(admissionIndex);
                    return Array.Empty<byte>();
                }
                var writer = new LEWriter();
                writer.WriteBytes(responsePrefix);
                WritePlayerEntitySynch(conn, writer);
                byte[] responseMessage = writer.ToArray();
                admission.ResponseMessage = responseMessage;
                _pendingHotbarPassiveAdmissions[admissionIndex] = admission;
                return responseMessage;
            }
        }

        private void WriteHotbarResponse(RRConnection conn, LEWriter writer, byte[] responsePrefix)
        {
            writer.WriteBytes(responsePrefix);
            WritePlayerEntitySynch(conn, writer);
        }

        private byte[] BuildHotbarResponse(RRConnection conn, byte[] responsePrefix)
        {
            var writer = new LEWriter();
            WriteHotbarResponse(conn, writer, responsePrefix);
            return writer.ToArray();
        }

        private void ApplyPendingHotbarPassiveAdmissions(RRConnection conn, IReadOnlyList<byte[]> messages, uint simulationApplyTick)
        {
            if (conn == null || messages == null || messages.Count == 0)
                return;
            var admissions = new List<(PendingHotbarPassiveAdmission Admission, int MessageIndex)>();
            lock (_pendingHotbarPassiveAdmissionLock)
            {
                _pendingHotbarPassiveAdmissions.RemoveAll(admission =>
                    admission.ConnId == conn.ConnId
                    && (!IsPendingConnectionInstanceCurrent(conn, admission.InstanceKey)
                        || admission.AvatarEntityId == 0
                        || conn.Avatar == null
                        || admission.AvatarEntityId != (uint)conn.Avatar.Id));
                for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
                {
                    byte[] message = messages[messageIndex];
                    for (int admissionIndex = 0; admissionIndex < _pendingHotbarPassiveAdmissions.Count; admissionIndex++)
                    {
                        PendingHotbarPassiveAdmission admission = _pendingHotbarPassiveAdmissions[admissionIndex];
                        if (admission.ConnId != conn.ConnId
                            || message == null
                            || admission.ResponseMessage == null
                            || !message.SequenceEqual(admission.ResponseMessage))
                            continue;
                        admissions.Add((admission, messageIndex));
                        _pendingHotbarPassiveAdmissions.RemoveAt(admissionIndex);
                        break;
                    }
                }
            }
            foreach ((PendingHotbarPassiveAdmission admission, int messageIndex) in admissions)
            {
                if (!IsPendingConnectionInstanceCurrent(conn, admission.InstanceKey)
                    || conn.Avatar == null
                    || admission.AvatarEntityId != (uint)conn.Avatar.Id)
                    continue;
                lock (conn.SendLock)
                    RecalculateHotbarPassiveBonuses(conn, null, admission.Passives, sendModifiers: false, keepPvpPassive: true);
                Debug.LogError($"[HOTBAR-PASSIVE-ADMISSION] conn={conn.ConnId} sequence={admission.Sequence} avatar={admission.AvatarEntityId} receivedTick={admission.ReceivedTick} simulationApplyTick={simulationApplyTick} messageIndex={messageIndex} passiveCount={admission.Passives?.Count ?? 0} phase=client-entity-flush");
            }
        }

    }
}
