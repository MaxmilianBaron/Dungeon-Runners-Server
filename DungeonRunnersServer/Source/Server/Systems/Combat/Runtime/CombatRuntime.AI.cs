using System;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Networking;
using DungeonRunners.Networking.EntitySynchInfo;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Combat.Behavior;
using System.Linq;
using System.IO;
using System.Text;
namespace DungeonRunners.Combat
{
public partial class CombatRuntime
    {        private void BeginMonsterFollowFindLineOfSightMove(Monster monster)
        {
            uint followTargetEntityId = ResolveMonsterFollowTargetEntityId(monster);
            if (monster == null
                || !TryGetCombatTarget(followTargetEntityId, out CombatTarget target)
                || target == null
                || !target.IsAlive)
                return;
            ResolveMonsterFollowTargetStateFixed(target, out int targetFixedX, out int targetFixedY, out _, out _);
            monster.FollowFindLineOfSightTargetFixedX = targetFixedX;
            monster.FollowFindLineOfSightTargetFixedY = targetFixedY;
            monster.FollowFindLineOfSightMoveActive = true;
            BeginMonsterMoveToPoint(monster, MONSTER_MOVE_TO_POINT_FOLLOW_LINE_OF_SIGHT, targetFixedX, targetFixedY);
        }

        private void FaceMonsterSearchTarget(Monster monster)
        {
            if (monster == null)
                return;
            uint targetEntityId = ResolveMonsterActionTargetEntityId(monster);
            if (targetEntityId == 0
                || !TryGetCombatTarget(targetEntityId, out CombatTarget target)
                || target == null
                || !target.IsAlive
                || !MatchesInstance(monster, target.InstanceKey))
                return;
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            int desiredHeadingFixed = UnitMover.VectorToHeadingFixed(
                targetFixedX - monster.PosFixedX,
                targetFixedY - monster.PosFixedY);
            monster.UnitMoverDesiredHeadingFixed = desiredHeadingFixed;
            monster.UnitMoverDesiredHeadingInit = true;
            Debug.LogError($"[MON-SEARCH-FACING] entity={monster.EntityId} tick={_combatTick} actorFixed=({monster.PosFixedX},{monster.PosFixedY}) targetFixed=({targetFixedX},{targetFixedY}) currentHeading={monster.HeadingFixed} desiredHeading={desiredHeadingFixed} sourceFunction=SearchForAttack::States@0x0052DFB0->UnitMover::FaceTarget@0x00535EF0");
        }

        public void ResetMonsterFollowSpeedMod(Monster monster)
        {
            if (monster != null)
                monster.SpeedMod = monster.BaseSpeedMod;
        }

        private void StopMonsterMoving(Monster monster)
        {
            if (monster == null)
                return;
            monster.MoveInDirectionHeadingInit = false;
            monster.UnitMoverMovingThisFrame = false;
            monster.ClientVisibleMovingThisFrame = false;
            ClearMonsterMoveToPointPath(monster, 0);
        }

        private readonly Dictionary<uint, MeleeBehaviorContext> _meleeBehaviorContexts = new Dictionary<uint, MeleeBehaviorContext>();

        private bool IsMonsterFollowTargetValid(Monster monster, uint targetEntityId)
        {
            return monster != null
                && targetEntityId != 0
                && TryGetCombatTarget(targetEntityId, out CombatTarget target)
                && target != null
                && target.IsAlive
                && target.HasUnitState
                && MatchesInstance(monster, target.InstanceKey);
        }

        private bool IsMonsterBehaviorPrimaryTargetValid(Monster monster, uint targetEntityId)
        {
            return monster != null
                && TryGetCombatTarget(targetEntityId, out CombatTarget enemyTarget)
                && IsMonsterEnemyOfTarget(monster, enemyTarget)
                && monster.TargetId == targetEntityId
                && IsMonsterFollowTargetValid(monster, targetEntityId);
        }

        private bool IsMonsterBehaviorAssistSourceValid(Monster monster, uint sourceEntityId)
        {
            return monster != null
                && sourceEntityId != 0
                && _activeMonsters.TryGetValue(sourceEntityId, out Monster source)
                && source != null
                && source.IsAlive
                && source.Behavior != null
                && MatchesInstance(monster, source.InstanceKey);
        }

        private int ResolveMonsterSearchAttackMinFixed(Monster monster)
        {
            return Math.Max(0, ResolveUnitBehaviorRadius130F32(monster));
        }

        private int ResolveMonsterSearchAttackMaxFixed(Monster monster)
        {
            if (monster == null)
                return 0;
            return SaturatingAddFixed8(
                ResolveMonsterUnitBehaviorRange138F32(monster),
                ResolveTargetCombatRadiusFixed(TryGetCombatTarget(ResolveMonsterActionTargetEntityId(monster), out CombatTarget target) ? target : null));
        }

        private int ResolveMonsterSearchAttackDistanceSpanFixed(Monster monster)
        {
            int minFixed = ResolveMonsterSearchAttackMinFixed(monster);
            int maxFixed = ResolveMonsterSearchAttackMaxFixed(monster);
            return Math.Max(0, (maxFixed - minFixed) >> 8);
        }

        private int ResolveMonsterSearchAttackDistanceFixed(Monster monster, uint raw)
        {
            int minFixed = ResolveMonsterSearchAttackMinFixed(monster);
            int spanFixed = ResolveMonsterSearchAttackDistanceSpanFixed(monster);
            if (spanFixed == 0)
                return minFixed;
            return SaturatingAddFixed8(minFixed, (int)(raw % (uint)spanFixed) << 8);
        }

        private int ResolveMonsterSearchRetreatMinFixed(Monster monster)
        {
            if (monster == null)
                return 0;
            return SaturatingAddFixed8(
                SaturatingAddFixed8(Math.Max(0, ResolveUnitBehaviorRadius130F32(monster)), ResolveAvatarCombatRadiusFixed()),
                0x1e00);
        }

        private int ResolveMonsterSearchRetreatMaxFixed(Monster monster)
        {
            if (monster == null)
                return 0;
            return SaturatingAddFixed8(
                SaturatingAddFixed8(
                    SaturatingAddFixed8(Math.Max(0, ResolveUnitBehaviorRadius130F32(monster)), ResolveAvatarCombatRadiusFixed()),
                    Math.Max(0, monster.UnitDescAttackRangeF32)),
                0x4b00);
        }

        private int ResolveMonsterSearchRetreatDistanceSpanFixed(Monster monster)
        {
            int minFixed = ResolveMonsterSearchRetreatMinFixed(monster);
            int maxFixed = ResolveMonsterSearchRetreatMaxFixed(monster);
            return Math.Max(0, (maxFixed - minFixed) >> 8);
        }

        private int ResolveMonsterSearchRetreatDistanceFixed(Monster monster, uint raw)
        {
            int minFixed = ResolveMonsterSearchRetreatMinFixed(monster);
            int spanFixed = ResolveMonsterSearchRetreatDistanceSpanFixed(monster);
            if (spanFixed == 0)
                return minFixed;
            return SaturatingAddFixed8(minFixed, (int)(raw % (uint)spanFixed) << 8);
        }

        private int ScoreMonsterCurrentAttackLocationFixed(Monster monster)
        {
            if (monster == null || !TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive)
                return 0;
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY, out _);
            return ScoreMonsterAttackPointFixed(monster, monsterFixedX, monsterFixedY, out _, out _);
        }

        private int ScoreMonsterAttackLocationFixed(Monster monster, int headingFixed, int distanceFixed, out int fixedX, out int fixedY)
        {
            fixedX = monster?.PosFixedX ?? 0;
            fixedY = monster?.PosFixedY ?? 0;
            if (monster == null || !TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive)
                return 0;
            int headingDegrees = (headingFixed >> 8) % 360;
            if (headingDegrees < 0)
                headingDegrees += 360;
            int directionFixedX = -UnitMover.ZRotateSinFixed(headingDegrees);
            int directionFixedY = UnitMover.ZRotateCosFixed(headingDegrees);
            ResolveSearchForAttackTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            fixedX = targetFixedX + (int)(((long)directionFixedX * distanceFixed) >> 8);
            fixedY = targetFixedY + (int)(((long)directionFixedY * distanceFixed) >> 8);
            return ScoreMonsterAttackPointFixed(monster, fixedX, fixedY, out fixedX, out fixedY);
        }

        private int ScoreMonsterAttackPointFixed(Monster monster, int candidateFixedX, int candidateFixedY, out int fixedX, out int fixedY)
        {
            fixedX = candidateFixedX;
            fixedY = candidateFixedY;
            if (monster == null || !TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive)
                return 0;
            int minFixed = ResolveMonsterSearchAttackMinFixed(monster);
            int maxFixed = ResolveMonsterSearchAttackMaxFixed(monster);
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
            bool checkUnitCollisions = monster.UnitState31B <= 5;
            if (checkUnitCollisions && FindAnyCollidingUnitFixed(monster, target, candidateFixedX, candidateFixedY))
            {
                TraceMonsterAttackLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "candidate-collision");
                return 0;
            }
            ResolveSearchForAttackTargetPositionFixed(target, out int rayOriginFixedX, out int rayOriginFixedY, out _);
            if (pathMap == null || !pathMap.CanReachPointFixed(candidateFixedX, candidateFixedY, rayOriginFixedX, rayOriginFixedY))
            {
                TraceMonsterAttackLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "unreachable");
                return 0;
            }
            int deltaFixedX = candidateFixedX - rayOriginFixedX;
            int deltaFixedY = candidateFixedY - rayOriginFixedY;
            if (!PathMap.TryBuildNativeRayDirectionFixed(deltaFixedX, deltaFixedY, out int directionFixedX, out int directionFixedY, out int requestedDistanceFixed))
            {
                TraceMonsterAttackLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "zero-distance");
                return 0;
            }
            int castDistanceFixed = pathMap.CastGroundRayDistanceFixed(
                rayOriginFixedX,
                rayOriginFixedY,
                directionFixedX,
                directionFixedY,
                requestedDistanceFixed);
            fixedX = rayOriginFixedX + (int)(((long)directionFixedX * castDistanceFixed) >> 8);
            fixedY = rayOriginFixedY + (int)(((long)directionFixedY * castDistanceFixed) >> 8);
            if (castDistanceFixed < minFixed)
            {
                TraceMonsterAttackLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "below-min");
                return 0;
            }
            if (castDistanceFixed > maxFixed)
            {
                TraceMonsterAttackLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "above-max");
                return 0;
            }
            if (checkUnitCollisions && FindAnyCollidingUnitFixed(monster, target, fixedX, fixedY))
            {
                TraceMonsterAttackLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "endpoint-collision");
                return 0;
            }
            int spanFixed = maxFixed - minFixed;
            int distanceScoreFixed = spanFixed > 0
                ? (int)(((long)castDistanceFixed << 8) / spanFixed)
                : 0x100;
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int actorFixedX, out int actorFixedY, out _);
            long manhattanLong = Math.Abs((long)candidateFixedX - actorFixedX) + Math.Abs((long)candidateFixedY - actorFixedY);
            int manhattanFixed = manhattanLong > int.MaxValue ? int.MaxValue : (int)manhattanLong;
            int proximityWeightFixed = 0x1f400 - Math.Min(manhattanFixed, 0x1f300);
            long scoreFixed = ((long)distanceScoreFixed * proximityWeightFixed) >> 8;
            int score = scoreFixed > int.MaxValue ? int.MaxValue : (int)Math.Max(0, scoreFixed);
            TraceMonsterAttackLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, score, "scored");
            return score;
        }

        private int ScoreMonsterRetreatLocationFixed(Monster monster, int headingFixed, int distanceFixed, out int fixedX, out int fixedY)
        {
            fixedX = monster?.PosFixedX ?? 0;
            fixedY = monster?.PosFixedY ?? 0;
            if (monster == null || !TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive)
                return 0;
            int headingDegrees = (headingFixed >> 8) % 360;
            if (headingDegrees < 0)
                headingDegrees += 360;
            int directionFixedX = -UnitMover.ZRotateSinFixed(headingDegrees);
            int directionFixedY = UnitMover.ZRotateCosFixed(headingDegrees);
            ResolveMonsterBehaviorTargetPositionFixed(monster, target, out int targetFixedX, out int targetFixedY, out _);
            int candidateFixedX = targetFixedX + (int)(((long)directionFixedX * distanceFixed) >> 8);
            int candidateFixedY = targetFixedY + (int)(((long)directionFixedY * distanceFixed) >> 8);
            fixedX = candidateFixedX;
            fixedY = candidateFixedY;
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
            if (pathMap == null || !pathMap.CanReachPointFixed(candidateFixedX, candidateFixedY, targetFixedX, targetFixedY))
            {
                TraceMonsterRetreatLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "unreachable");
                return 0;
            }
            int deltaFixedX = candidateFixedX - targetFixedX;
            int deltaFixedY = candidateFixedY - targetFixedY;
            if (!PathMap.TryBuildNativeRayDirectionFixed(deltaFixedX, deltaFixedY, out int rayDirectionFixedX, out int rayDirectionFixedY, out int requestedDistanceFixed))
            {
                TraceMonsterRetreatLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "zero-distance");
                return 0;
            }
            int castDistanceFixed = pathMap.CastGroundRayDistanceFixed(
                targetFixedX,
                targetFixedY,
                rayDirectionFixedX,
                rayDirectionFixedY,
                requestedDistanceFixed);
            fixedX = targetFixedX + (int)(((long)rayDirectionFixedX * castDistanceFixed) >> 8);
            fixedY = targetFixedY + (int)(((long)rayDirectionFixedY * castDistanceFixed) >> 8);
            int minFixed = ResolveMonsterSearchRetreatMinFixed(monster);
            int maxFixed = ResolveMonsterSearchRetreatMaxFixed(monster);
            if (castDistanceFixed < minFixed)
            {
                TraceMonsterRetreatLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "below-min");
                return 0;
            }
            if (castDistanceFixed > maxFixed)
            {
                TraceMonsterRetreatLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, 0, "above-max");
                return 0;
            }
            int spanFixed = maxFixed - minFixed;
            int distanceScoreFixed = spanFixed > 0
                ? (int)(((long)castDistanceFixed << 8) / spanFixed)
                : 0x100;
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int actorFixedX, out int actorFixedY, out _);
            long manhattanLong = Math.Abs((long)candidateFixedX - actorFixedX) + Math.Abs((long)candidateFixedY - actorFixedY);
            int manhattanFixed = manhattanLong > int.MaxValue ? int.MaxValue : (int)manhattanLong;
            int proximityWeightFixed = 0x1f400 - Math.Min(manhattanFixed, 0x1f300);
            long scoreFixed = ((long)distanceScoreFixed * proximityWeightFixed) >> 8;
            int score = scoreFixed > int.MaxValue ? int.MaxValue : (int)Math.Max(0, scoreFixed);
            if (FindAnyCollidingUnitFixed(monster, target, fixedX, fixedY, false))
                score = (int)(((long)score << 8) / 0x200);
            TraceMonsterRetreatLocationScore(monster, target, candidateFixedX, candidateFixedY, fixedX, fixedY, score, "scored");
            return score;
        }

        private bool MonsterSearchCurrentLocationCollides(Monster monster)
        {
            if (monster == null || !TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive)
                return false;
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int actorFixedX, out int actorFixedY, out _);
            return FindAnyCollidingUnitFixed(monster, target, actorFixedX, actorFixedY);
        }

        private void UpdateMonsterAttackSearchMoveForTarget(Monster monster)
        {
            if (monster == null || !monster.SearchForAttackMoveActive
                || !TryGetCombatTarget(monster.TargetId, out CombatTarget target)
                || target == null || !target.IsAlive)
                return;
            ResolveSearchForAttackTargetPositionFixed(target, out int predictedFixedX, out int predictedFixedY, out _);
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int actorFixedX, out int actorFixedY, out _);
            int deltaFixedX = actorFixedX - predictedFixedX;
            int deltaFixedY = actorFixedY - predictedFixedY;
            int distanceFixed = UnitMover.IntSqrt((long)deltaFixedX * deltaFixedX + (long)deltaFixedY * deltaFixedY);
            int attackMinFixed = ResolveMonsterSearchAttackMinFixed(monster);
            if (distanceFixed <= attackMinFixed)
                return;
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
            if (pathMap == null || !pathMap.CanReachPointFixed(actorFixedX, actorFixedY, predictedFixedX, predictedFixedY))
                return;
            if (!PathMap.TryBuildNativeRayDirectionFixed(deltaFixedX, deltaFixedY, out int directionFixedX, out int directionFixedY, out _))
                return;
            int targetFixedX = predictedFixedX + (int)(((long)directionFixedX * attackMinFixed) >> 8);
            int targetFixedY = predictedFixedY + (int)(((long)directionFixedY * attackMinFixed) >> 8);
            monster.SearchForAttackTargetFixedX = targetFixedX;
            monster.SearchForAttackTargetFixedY = targetFixedY;
            BeginMonsterMoveToPoint(monster, MONSTER_MOVE_TO_POINT_SEARCH, targetFixedX, targetFixedY);
            Debug.LogError($"[MON-SEARCH-MOVE] entity={monster.EntityId} tick={_combatTick} actorFixed8=({actorFixedX},{actorFixedY}) predictedTargetFixed8=({predictedFixedX},{predictedFixedY}) distanceFixed={distanceFixed} attackMinFixed={attackMinFixed} destinationFixed8=({targetFixedX},{targetFixedY}) sourceFunction=SearchForAttack::States@0x0052DFB0");
        }

        private void TraceMonsterRetreatLocationScore(
            Monster monster,
            CombatTarget target,
            int candidateFixedX,
            int candidateFixedY,
            int endpointFixedX,
            int endpointFixedY,
            int score,
            string result)
        {
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int actorFixedX, out int actorFixedY, out _);
            ResolveMonsterBehaviorTargetPositionFixed(monster, target, out int targetFixedX, out int targetFixedY, out _);
            Debug.LogError($"[MON-SEARCH-RETREAT-SCORE] entity={monster.EntityId} tick={_combatTick} unitState31B={monster.UnitState31B} actorFixed8=({actorFixedX},{actorFixedY}) targetFixed8=({targetFixedX},{targetFixedY}) candidateFixed8=({candidateFixedX},{candidateFixedY}) endpointFixed8=({endpointFixedX},{endpointFixedY}) score={score} result={result}");
        }

        private void TraceMonsterAttackLocationScore(
            Monster monster,
            CombatTarget target,
            int candidateFixedX,
            int candidateFixedY,
            int endpointFixedX,
            int endpointFixedY,
            int score,
            string result)
        {
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int actorFixedX, out int actorFixedY, out _);
            ResolveSearchForAttackTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            Debug.LogError($"[MON-SEARCH-SCORE] entity={monster.EntityId} tick={_combatTick} unitState31B={monster.UnitState31B} stockState={monster.StockUnitState} checkUnitCollisions={monster.UnitState31B <= 5} actorFixed8=({actorFixedX},{actorFixedY}) targetFixed8=({targetFixedX},{targetFixedY}) candidateFixed8=({candidateFixedX},{candidateFixedY}) endpointFixed8=({endpointFixedX},{endpointFixedY}) score={score} result={result}");
        }

        private bool FindAnyCollidingUnitFixed(Monster scanUnit, CombatTarget viewer, int candidateFixedX, int candidateFixedY, bool ignoreMoving = true)
        {
            int scanRadiusFixed = ResolveUnitBehaviorRadius130F32(scanUnit);
            int scanCollisionBand = scanUnit.CollisionBand;
            int scanCollisionPriority = scanUnit.CollisionPriority;
            string instanceKey = RoomRuntime.NormalizeInstanceKey(scanUnit.InstanceKey);

            foreach (uint entityId in _entityOrder)
            {
                if (entityId == scanUnit.EntityId)
                    continue;

                if (_players.TryGetValue(entityId, out CombatPlayer player))
                {
                    if (player == null || !player.IsAlive
                        || !string.Equals(instanceKey, RoomRuntime.NormalizeInstanceKey(player.InstanceKey), StringComparison.OrdinalIgnoreCase)
                        || !PassesUnitCollisionBand(scanCollisionBand, scanCollisionPriority, ResolveAvatarCollisionBand(), ResolveAvatarCollisionPriority()))
                        continue;
                    bool ownerView = viewer != null && player.EntityId == viewer.EntityId;
                    byte moverMode = ownerView
                        ? player.MonsterActionTargetMoverMode
                        : player.ClientSimulationMoverMode;
                    if (ignoreMoving && UnitMover.IsMoving(moverMode))
                        continue;
                    int playerFixedX = ownerView ? player.MonsterActionTargetPosFixedX : player.ClientSimulationPosFixedX;
                    int playerFixedY = ownerView ? player.MonsterActionTargetPosFixedY : player.ClientSimulationPosFixedY;
                    int membershipFixedX = ownerView ? player.MonsterActionTargetPosFixedX : player.UnitFinderMembershipPosFixedX;
                    int membershipFixedY = ownerView ? player.MonsterActionTargetPosFixedY : player.UnitFinderMembershipPosFixedY;
                    if (!UnitFinderScanContainsMembershipFixed(candidateFixedX, candidateFixedY, scanRadiusFixed, membershipFixedX, membershipFixedY))
                    {
                        if (TestUnitCollisionFixed(candidateFixedX, candidateFixedY, scanRadiusFixed, playerFixedX, playerFixedY, ResolveAvatarCombatRadiusFixed()))
                            Debug.LogError($"[MON-SEARCH-BROADPHASE] entity={scanUnit.EntityId} blocker={player.EntityId} tick={_combatTick} candidateFixed8=({candidateFixedX},{candidateFixedY}) blockerFixed8=({playerFixedX},{playerFixedY}) membershipFixed8=({membershipFixedX},{membershipFixedY}) ownerView={ownerView} result=membership-cell-excluded sourceFunction=UnitFinder2::ScanHelper::ScanHelper@0x00510540->BasicPhysicsScan@0x00511B10");
                        continue;
                    }
                    if (TestUnitCollisionFixed(
                        candidateFixedX,
                        candidateFixedY,
                        scanRadiusFixed,
                        playerFixedX,
                        playerFixedY,
                        ResolveAvatarCombatRadiusFixed()))
                    {
                        Debug.LogError($"[MON-SEARCH-COLLISION] entity={scanUnit.EntityId} blocker={player.EntityId} blockerKind=player tick={_combatTick} candidateFixed8=({candidateFixedX},{candidateFixedY}) blockerFixed8=({playerFixedX},{playerFixedY}) blockerMoverMode={moverMode} ownerView={ownerView} sourceFunction=UnitFinder2::findAnyCollidingUnit@0x00510BD0->UnitMover::IsMoving@0x00536200");
                        return true;
                    }
                    continue;
                }

                if (!_activeMonsters.TryGetValue(entityId, out Monster other)
                    || other == null
                    || !other.IsAlive
                    || other.Behavior?.SpawnActive == true
                    || !string.Equals(instanceKey, RoomRuntime.NormalizeInstanceKey(other.InstanceKey), StringComparison.OrdinalIgnoreCase)
                    || !PassesUnitCollisionBand(scanCollisionBand, scanCollisionPriority, other.CollisionBand, other.CollisionPriority))
                    continue;

                byte otherMoverMode = GetMonsterUnitMoverMode(other);
                if (ignoreMoving && UnitMover.IsMoving(otherMoverMode))
                    continue;

                int otherFixedX = other.PosFixedX;
                int otherFixedY = other.PosFixedY;
                TryPeekMonsterClientVisiblePositionFixed(other, viewer.EntityId, out otherFixedX, out otherFixedY, out _);
                if (TestUnitCollisionFixed(
                    candidateFixedX,
                    candidateFixedY,
                    scanRadiusFixed,
                    otherFixedX,
                    otherFixedY,
                    ResolveUnitBehaviorRadius130F32(other)))
                {
                    Debug.LogError($"[MON-SEARCH-COLLISION] entity={scanUnit.EntityId} blocker={other.EntityId} blockerKind=monster tick={_combatTick} candidateFixed8=({candidateFixedX},{candidateFixedY}) blockerFixed8=({otherFixedX},{otherFixedY}) blockerMoverMode={otherMoverMode} blockerMovingThisFrame={other.UnitMoverMovingThisFrame} sourceFunction=UnitFinder2::findAnyCollidingUnit@0x00510BD0->UnitMover::IsMoving@0x00536200");
                    return true;
                }
            }

            return false;
        }

        private static bool PassesUnitCollisionBand(int actorBand, int actorPriority, int candidateBand, int candidatePriority)
        {
            return (actorBand & candidateBand) != 0 && actorPriority <= candidatePriority;
        }

        private static bool TestUnitCollisionFixed(
            int candidateFixedX,
            int candidateFixedY,
            int actorRadiusFixed,
            int unitFixedX,
            int unitFixedY,
            int unitRadiusFixed)
        {
            long dxFixed = (long)unitFixedX - candidateFixedX;
            long dyFixed = (long)unitFixedY - candidateFixedY;
            long distanceSquaredFixed = ((dxFixed * dxFixed) >> 8) + ((dyFixed * dyFixed) >> 8);
            int radiusFixed = SaturatingAddFixed8(Math.Max(0, actorRadiusFixed), Math.Max(0, unitRadiusFixed));
            long radiusSquaredFixed = ((long)radiusFixed * radiusFixed) >> 8;
            return distanceSquaredFixed <= radiusSquaredFixed;
        }

        private static bool UnitFinderScanContainsMembershipFixed(
            int candidateFixedX,
            int candidateFixedY,
            int scanRadiusFixed,
            int membershipFixedX,
            int membershipFixedY)
        {
            int radiusFixed = Math.Max(0, scanRadiusFixed);
            int minGridX = UnitFinderGridIndexFixed((long)candidateFixedX - radiusFixed);
            int maxGridX = UnitFinderGridIndexFixed((long)candidateFixedX + radiusFixed);
            int minGridY = UnitFinderGridIndexFixed((long)candidateFixedY - radiusFixed);
            int maxGridY = UnitFinderGridIndexFixed((long)candidateFixedY + radiusFixed);
            int membershipGridX = UnitFinderGridIndexFixed(membershipFixedX);
            int membershipGridY = UnitFinderGridIndexFixed(membershipFixedY);
            return membershipGridX >= minGridX
                && membershipGridX <= maxGridX
                && membershipGridY >= minGridY
                && membershipGridY <= maxGridY;
        }

        private static int UnitFinderGridIndexFixed(long fixedCoordinate)
        {
            const int cellSizeFixed = 128 << 8;
            long quotient = fixedCoordinate / cellSizeFixed;
            if (fixedCoordinate < 0 && fixedCoordinate % cellSizeFixed != 0)
                quotient--;
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, quotient));
        }

        private int ResolveAvatarCollisionBand()
        {
            if (_avatarCollisionBand.HasValue)
                return _avatarCollisionBand.Value;
            GCNode behavior = GCDatabase.Instance?.ResolveWithInheritance("avatar.base.UnitBehavior");
            GCNode description = behavior?.GetChild("Description") ?? behavior;
            if (description == null || !description.HasProperty("CollisionBand"))
                throw new InvalidOperationException("avatar.base.UnitBehavior CollisionBand missing");
            _avatarCollisionBand = description.GetInt("CollisionBand", 0);
            return _avatarCollisionBand.Value;
        }

        private int ResolveAvatarCollisionPriority()
        {
            if (_avatarCollisionPriority.HasValue)
                return _avatarCollisionPriority.Value;
            GCNode behavior = GCDatabase.Instance?.ResolveWithInheritance("avatar.base.UnitBehavior");
            GCNode description = behavior?.GetChild("Description") ?? behavior;
            if (description == null || !description.HasProperty("CollisionPriority"))
                throw new InvalidOperationException("avatar.base.UnitBehavior CollisionPriority missing");
            _avatarCollisionPriority = description.GetInt("CollisionPriority", 0);
            return _avatarCollisionPriority.Value;
        }

        private bool DispatchMonsterBehaviorUpdateTargets(MeleeBehaviorContext context)
        {
            if (context?.Monster == null)
                return false;
            Monster monster = context.Monster;
            uint previousTargetEntityId = monster.TargetId;
            CombatTarget target = FindMonsterBehaviorPrimaryTarget(monster);
            bool acquired = false;
            if (target != null && previousTargetEntityId == 0)
                acquired = AggroMonster(monster, target, "UnitFinder2::findEnemies", false, false);
            else if (target != null)
                monster.TargetId = target.EntityId;
            else if (previousTargetEntityId != 0)
                TryGetCombatTarget(previousTargetEntityId, out target);
            bool targetChanged = target != null && target.EntityId != previousTargetEntityId;
            context.TargetPresent = target != null && target.IsAlive;
            if (!context.TargetPresent)
            {
                context.TargetEntityIdValue = 0;
                context.DistFixed = int.MaxValue;
                return false;
            }
            context.TargetEntityIdValue = target.EntityId;
            TryPeekMonsterClientVisiblePositionFixed(context.Monster, target.EntityId, out int monsterFixedX, out int monsterFixedY, out _);
            ResolveMonsterBehaviorTargetPositionFixed(context.Monster, target, out int targetFixedX, out int targetFixedY, out _);
            long dxFixed = (long)targetFixedX - monsterFixedX;
            long dyFixed = (long)targetFixedY - monsterFixedY;
            context.DistFixed = UnitMover.IntSqrt(dxFixed * dxFixed + dyFixed * dyFixed);
            context.RangeFixed = Math.Max(0, ResolveMonsterEffectiveAttackRangeFixed(context.Monster));
            return acquired || targetChanged;
        }

        private CombatTarget FindMonsterBehaviorPrimaryTarget(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return null;
            if (!TryPeekMonsterClientVisiblePositionFixed(monster, 0, out int monsterFixedX, out int monsterFixedY, out int monsterFixedZ))
                return null;
            int rangeFixed = ResolveMonsterAggroAdmissionRangeFixed(monster);
            if (rangeFixed <= 0)
                return null;
            long rangeSqFixed8 = ((long)rangeFixed * rangeFixed) >> 8;
            PathMap pathMap = null;
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            if (!string.IsNullOrWhiteSpace(pathMapKey))
                pathMap = PathMapCatalog.Instance.GetPathMap(pathMapKey);
            var candidates = new List<(CombatTarget Player, long DistanceSquaredFixed8, int Aggro)>();
            foreach (CombatTarget player in GetCombatTargetsInEntityOrder())
            {
                if (player == null || !player.IsAlive || !player.HasUnitState)
                    continue;
                if (!MatchesInstance(monster, player.InstanceKey) || !IsMonsterEnemyOfTarget(monster, player))
                    continue;
                if (player.CurrentHPWire == 0)
                    continue;
                long dxFixed = (long)player.ClientSimulationPosFixedX - monsterFixedX;
                long dyFixed = (long)player.ClientSimulationPosFixedY - monsterFixedY;
                long dzFixed = (long)player.ClientSimulationPosFixedZ - monsterFixedZ;
                long distanceSquaredFixed8 = ((dxFixed * dxFixed) >> 8)
                    + ((dyFixed * dyFixed) >> 8)
                    + ((dzFixed * dzFixed) >> 8);
                if (distanceSquaredFixed8 > rangeSqFixed8)
                    continue;
                int aggro = ResolveUnitFinderPlayerAggro(monster, player, distanceSquaredFixed8);
                candidates.Add((player, distanceSquaredFixed8, aggro));
            }
            candidates.Sort((left, right) =>
            {
                int aggroCompare = right.Aggro.CompareTo(left.Aggro);
                return aggroCompare != 0 ? aggroCompare : left.Player.EntityId.CompareTo(right.Player.EntityId);
            });
            var order = new StringBuilder();
            CombatTarget selected = null;
            foreach ((CombatTarget Player, long DistanceSquaredFixed8, int Aggro) candidate in candidates)
            {
                PathReachability reachability = pathMap != null
                    ? pathMap.GetReachabilityFixed(monsterFixedX, monsterFixedY, candidate.Player.ClientSimulationPosFixedX, candidate.Player.ClientSimulationPosFixedY)
                    : PathReachability.Reachable;
                if (order.Length != 0)
                    order.Append(',');
                order.Append(candidate.Player.EntityId)
                    .Append(':')
                    .Append(candidate.Aggro)
                    .Append(':')
                    .Append(candidate.DistanceSquaredFixed8)
                    .Append(':')
                    .Append((int)reachability);
                if (selected == null && reachability == PathReachability.Reachable)
                    selected = candidate.Player;
            }
            Debug.LogError($"[MON-TARGET-SCAN] entity={monster.EntityId} tick={_combatTick} previous={monster.TargetId} selected={selected?.EntityId ?? 0} scanner=({monsterFixedX},{monsterFixedY},{monsterFixedZ}) range={rangeFixed} order={order} sourceFunction=MonsterBehavior2::UpdateTargets@0x0051CB50->UnitFinder2::findEnemies@0x00510F50->ResultSortByAggro@0x00510650");
            return selected;
        }

        private static int ResolveUnitFinderPlayerAggro(Monster monster, CombatTarget player, long distanceSquaredFixed8)
        {
            CombatTarget state = player;
            long score = ((long)monster.Level - state.Level) * 0x4C;
            score += ResolveUnitFinderMissingResourceAggro(state.CurrentHPWire, state.MaxHPWire, 0x1400);
            score += ResolveUnitFinderMissingResourceAggro(state.CurrentManaWire, state.MaxManaWire, 0x0A00);
            if (distanceSquaredFixed8 < 160000)
                score += 0x4B00;
            else if (distanceSquaredFixed8 < 640000)
                score += 0x1E00;
            else if (distanceSquaredFixed8 >= 0x57E400)
                score -= 0x4B00;
            score += (long)(state.PlayerState?.AggroIncreaseBonus ?? 0) * 0x100;
            int aggroIncreaseModPercent = (state.PlayerState?.AggroIncreaseModPercent ?? 0) + (state.PlayerState?.GetActiveAttributeModifierValue("AGGRO_MOD") ?? state.Monster.GetActiveAttributeModifierValue("AGGRO_MOD"));
            if (aggroIncreaseModPercent != 0)
            {
                long modifierFixed = ((long)aggroIncreaseModPercent << 16) / 0x6400;
                long scaled = score * modifierFixed >> 8;
                long magnitude = scaled < 0 ? -scaled : scaled;
                score += modifierFixed < 1 ? -magnitude : magnitude;
            }
            if (score > int.MaxValue)
                return int.MaxValue;
            if (score < int.MinValue)
                return int.MinValue;
            return (int)score;
        }

        private static long ResolveUnitFinderMissingResourceAggro(uint currentWire, uint maximumWire, int weightF32)
        {
            if (maximumWire == 0 || currentWire >= maximumWire)
                return 0;
            long missingPercent = 100 - ((long)currentWire * 100 / maximumWire);
            long normalizedF16 = missingPercent * 0x10000 / 0x6400;
            return normalizedF16 * weightF32 >> 8;
        }

        private bool DispatchMonsterBehaviorAssistAction(
            MeleeBehaviorContext context,
            uint sourceEntityId,
            out uint targetEntityId)
        {
            targetEntityId = 0;
            Monster monster = context?.Monster;
            Monster source = null;
            if (IsMonsterBehaviorAssistSourceValid(monster, sourceEntityId))
                _activeMonsters.TryGetValue(sourceEntityId, out source);
            uint sourcePrimaryTargetEntityId = source?.Behavior?.PrimaryTargetEntityId ?? 0;
            CombatTarget target = null;
            if (sourcePrimaryTargetEntityId != 0)
                TryGetCombatTarget(sourcePrimaryTargetEntityId, out target);
            if (source == null
                || sourcePrimaryTargetEntityId == 0
                || !IsMonsterBehaviorPrimaryTargetValid(source, sourcePrimaryTargetEntityId)
                || target == null
                || !target.IsAlive
                || !target.HasUnitState
                || !MatchesInstance(monster, target.InstanceKey))
            {
                if (monster != null)
                {
                    monster.TargetId = 0;
                    monster.BehaviorAssistSourceEntityId = 0;
                }
                if (context != null)
                {
                    context.TargetPresent = false;
                    context.TargetEntityIdValue = 0;
                    context.DistFixed = int.MaxValue;
                    context.DistSquaredFixed = int.MaxValue;
                    context.TargetMoving = false;
                }
                return false;
            }
            targetEntityId = target.EntityId;
            monster.TargetId = targetEntityId;
            context.TargetPresent = true;
            context.TargetEntityIdValue = targetEntityId;
            return true;
        }

        private void ClearMonsterBehaviorTargets(Monster monster)
        {
            if (monster == null)
                return;
            uint primaryTarget = monster.TargetId;
            uint secondaryTarget = monster.BehaviorAssistSourceEntityId != 0
                ? monster.BehaviorAssistSourceEntityId
                : monster.AlertSourceEntityId;
            monster.TargetId = 0;
            monster.AlertSourceEntityId = 0;
            monster.BehaviorAssistSourceEntityId = 0;
            monster.MeleeScanTargetId = 0;
            monster.BehaviorTargetUsesClientSimulationPosition = false;
            if (primaryTarget != 0 || secondaryTarget != 0)
                Debug.LogError($"[MON-TARGETS] entity={monster.EntityId} event=clear tick={_combatTick} primary={primaryTarget} secondary={secondaryTarget} sourceFunction=MonsterBehavior2::ClearTargets@0x0051C9B0");
        }

        private bool DispatchMonsterBehaviorThreatNear(Monster monster)
        {
            if (monster == null || monster.TargetId == 0)
                return false;
            if (!TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive)
                return false;
            if (monster.DeferredTargetScanMessageReleased)
            {
                monster.DeferredTargetScanMessageReleased = false;
                Debug.LogError($"[MON-THREAT-PHASE] entity={monster.EntityId} tick={_combatTick} follow={monster.FollowAdmissionReachable} transition=True phase=post-client-entity-input");
                return true;
            }
            if (monster.FollowAdmissionReachable && !monster.FollowClientVisible && !monster.FollowInputPending)
                QueueMonsterFollowBehaviorInput(monster, target);
            bool transitionClientBehavior = true;
            PropagateMonsterShout(monster, target, transitionClientBehavior, uint.MaxValue);
            Debug.LogError($"[MON-THREAT-PHASE] entity={monster.EntityId} tick={_combatTick} follow={monster.FollowAdmissionReachable} transition={transitionClientBehavior} clientScan={monster.ProximityTargetScanMatched} deferred={monster.DeferredTargetScanMessagePending}");
            monster.ProximityFollowAdmissionPending = false;
            monster.ProximityTargetScanMatched = false;
            return transitionClientBehavior;
        }

        public bool ReleaseDeferredMonsterTargetScanMessage(uint monsterEntityId, uint simulationTick)
        {
            if (!_activeMonsters.TryGetValue(monsterEntityId, out Monster monster)
                || monster == null
                || !monster.IsAlive
                || monster.TargetId == 0)
                return false;
            if (!TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive)
                return false;
            bool released = false;
            if (monster.DeferredTargetScanMessagePending && !monster.DeferredTargetScanMessageReleased)
            {
                monster.DeferredTargetScanMessagePending = false;
                monster.DeferredTargetScanMessageReleased = true;
                NotifyMonsterBehaviorThreatInput(monster, target, false);
                Debug.LogError($"[MON-THREAT-RELEASE] entity={monster.EntityId} target={target.EntityId} simulationTick={simulationTick} kind=near phase=after-client-entity-input sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                released = true;
            }
            return released;
        }

        private MeleeBehaviorContext BuildMeleeBehaviorContext(MersenneTwister rng, Monster monster, CombatTarget target, int distFixed, int allowedRangeFixed)
        {
            if (!_meleeBehaviorContexts.TryGetValue(monster.EntityId, out MeleeBehaviorContext context))
            {
                context = new MeleeBehaviorContext();
                _meleeBehaviorContexts[monster.EntityId] = context;
            }
            context.Runtime = this;
            context.Monster = monster;
            context.Rng = rng;
            context.Owner = monster.InstanceKey ?? monster.ZoneName;
            context.TargetPresent = target != null && target.IsAlive && target.HasUnitState;
            context.TargetEntityIdValue = context.TargetPresent ? target.EntityId : 0;
            context.DistFixed = distFixed;
            context.DistSquaredFixed = int.MaxValue;
            context.TargetMoving = false;
            context.DamageReactionActionActiveAtTickEntryValue = IsMonsterDamageReactionActive(monster);
            context.DamageReactionActionActiveNowValue = context.DamageReactionActionActiveAtTickEntryValue;
            context.UseTargetWeaponInterruptInput = false;
            if (context.TargetPresent)
            {
                TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY, out _);
                ResolveMonsterFollowTargetStateFixed(target, out int targetFixedX, out int targetFixedY, out _, out bool targetMovingThisFrame);
                long dxFixed = (long)targetFixedX - monsterFixedX;
                long dyFixed = (long)targetFixedY - monsterFixedY;
                long distanceSquaredFixed = ((dxFixed * dxFixed) >> 8) + ((dyFixed * dyFixed) >> 8);
                context.DistSquaredFixed = distanceSquaredFixed > int.MaxValue ? int.MaxValue : (int)distanceSquaredFixed;
                context.TargetMoving = targetMovingThisFrame;
            }
            context.RangeFixed = allowedRangeFixed;
            return context;
        }

        private MeleeBehaviorContext BuildMeleeBehaviorContextForMonster(Monster monster)
        {
            if (monster == null)
                return null;
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (rng == null)
                return null;
            CombatTarget target = null;
            uint targetEntityId = ResolveMonsterActionTargetEntityId(monster);
            if (targetEntityId != 0
                && TryGetCombatTarget(targetEntityId, out CombatTarget resolvedTarget)
                && resolvedTarget != null
                && MatchesInstance(monster, resolvedTarget.InstanceKey))
                target = resolvedTarget;
            int distFixed = int.MaxValue;
            if (target != null && target.IsAlive)
            {
                TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY, out _);
                ResolveMonsterBehaviorTargetPositionFixed(monster, target, out int targetFixedX, out int targetFixedY, out _);
                long deltaFixedX = (long)targetFixedX - monsterFixedX;
                long deltaFixedY = (long)targetFixedY - monsterFixedY;
                distFixed = UnitMover.IntSqrt(deltaFixedX * deltaFixedX + deltaFixedY * deltaFixedY);
            }
            int allowedRangeFixed = Math.Max(0, ResolveMonsterEffectiveAttackRangeFixed(monster));
            return BuildMeleeBehaviorContext(rng, monster, target, distFixed, allowedRangeFixed);
        }

        private void NotifyMonsterBehaviorThreatInput(Monster monster, CombatTarget target, bool alternate)
        {
            if (monster?.Behavior == null || target == null || !target.IsAlive)
                return;
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (rng == null)
                return;
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY, out _);
            ResolveMonsterBehaviorTargetPositionFixed(monster, target, out int targetFixedX, out int targetFixedY, out _);
            long dxFixed = (long)targetFixedX - monsterFixedX;
            long dyFixed = (long)targetFixedY - monsterFixedY;
            int distFixed = UnitMover.IntSqrt(dxFixed * dxFixed + dyFixed * dyFixed);
            int allowedRangeFixed = Math.Max(0, ResolveMonsterEffectiveAttackRangeFixed(monster));
            MeleeBehaviorContext context = BuildMeleeBehaviorContext(rng, monster, target, distFixed, allowedRangeFixed);
            if (alternate)
                monster.Behavior.NotifyThreatAlternate(context);
            else
                monster.Behavior.NotifyThreatNear(context);
        }

        private bool BeginMonsterFollowBehavior(Monster monster, CombatTarget target)
        {
            if (monster?.Behavior == null || target == null)
                return false;
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (rng == null)
                return false;
            TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY, out _);
            ResolveMonsterBehaviorTargetPositionFixed(monster, target, out int targetFixedX, out int targetFixedY, out _);
            long dxFixed = (long)targetFixedX - monsterFixedX;
            long dyFixed = (long)targetFixedY - monsterFixedY;
            int distFixed = UnitMover.IntSqrt(dxFixed * dxFixed + dyFixed * dyFixed);
            int allowedRangeFixed = Math.Max(0, ResolveMonsterEffectiveAttackRangeFixed(monster));
            return monster.Behavior.BeginFollow(BuildMeleeBehaviorContext(rng, monster, target, distFixed, allowedRangeFixed));
        }

        private void QueueMonsterFollowReachedNearStop(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return;
            uint targetEntityId = ResolveMonsterActionTargetEntityId(monster);
            if (targetEntityId == 0
                || !TryGetCombatTarget(targetEntityId, out CombatTarget target)
                || target == null
                || !target.IsAlive
                || !MatchesInstance(monster, target.InstanceKey))
                return;
            OnMonsterFollowReachedNearStop?.Invoke(monster, target);
        }

        private void QueueMonsterFollowBehaviorInput(Monster monster, CombatTarget target, uint applyTick = uint.MaxValue, uint directPlayerWeaponHitTick = 0, uint playerDamageTick = 0)
        {
            if (monster == null || target == null)
                return;
            monster.FollowInputPending = true;
            monster.FollowInputTargetId = target.EntityId;
            monster.FollowInputApplyTick = applyTick;
            monster.FollowInputDirectPlayerWeaponHitTick = directPlayerWeaponHitTick;
            monster.FollowInputPlayerDamageTick = playerDamageTick;
            string applyTickText = applyTick == uint.MaxValue ? "awaiting-packet" : applyTick.ToString();
            Debug.LogError($"[MON-FOLLOW-INPUT] queued {monster.Name}#{monster.EntityId}->{target.Name} queuedTick={_combatTick} applyTick={applyTickText} directPlayerWeaponHitTick={directPlayerWeaponHitTick} playerDamageTick={playerDamageTick} sourceFunction=MonsterBehavior2::onAttacked@0x0051B550");
        }

        public bool CommitMonsterFollowBehaviorInput(Monster monster, CombatTarget target, uint applyTick)
        {
            if (monster == null || target == null || !monster.IsAlive || !target.IsAlive)
                return false;
            if (!monster.FollowInputPending || monster.FollowInputTargetId != target.EntityId || applyTick < monster.FollowInputApplyTick)
                return false;
            monster.FollowInputPending = false;
            monster.FollowInputTargetId = 0;
            monster.FollowInputApplyTick = 0;
            monster.FollowInputDirectPlayerWeaponHitTick = 0;
            monster.FollowInputPlayerDamageTick = 0;
            if (!BeginMonsterFollowBehavior(monster, target))
            {
                Behavior.MonsterBehavior2Snapshot behavior = monster.Behavior?.GetSnapshot() ?? default;
                Debug.LogError($"[MON-FOLLOW-INPUT] rejected {monster.Name}#{monster.EntityId}->{target.Name} applyTick={applyTick} entityTick={_combatTick} currentAction={behavior.CurrentAction} interruptLocked={behavior.CurrentActionInterruptLocked} generation={behavior.ActionGeneration} reason=current-interrupt-locked sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->Behavior::doInterruptLocal@0x00515290");
                return false;
            }
            monster.RetiredWanderMoverActive = false;
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
            monster.WanderActionTerminationPending = false;
            CancelMonsterPendingAttack(monster, "Behavior::doInterruptLocal-Follow");
            Debug.LogError($"[MON-FOLLOW-INPUT] applied {monster.Name}#{monster.EntityId}->{target.Name} applyTick={applyTick} entityTick={_combatTick} sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->Follow::start@0x00526F70");
            return true;
        }

        public bool CommitMonsterAttackStopBehaviorInput(Monster monster, byte sessionId, uint applyTick)
        {
            if (monster == null)
                return false;
            if (sessionId != 0 && monster.AttackSessionId != 0 && monster.AttackSessionId != sessionId)
            {
                Debug.LogError($"[MON-ATTACK-STOP-INPUT] rejected {monster.Name}#{monster.EntityId} session={sessionId} currentSession={monster.AttackSessionId} applyTick={applyTick} reason=session-mismatch sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                return false;
            }
            MeleeBehaviorContext behaviorContext = BuildMeleeBehaviorContextForMonster(monster);
            if (behaviorContext == null)
                return false;
            monster.Behavior?.ProcessTerminateAllInput(behaviorContext);
            StopMonsterRuntimeAttackAction(monster, sessionId, "Behavior::processUpdate-subopcode-0x05");
            CancelMonsterPendingAttack(monster, "Behavior::processUpdate-subopcode-0x05");
            ResetMonsterFollowSpeedMod(monster);
            ClearMonsterFollowRepositionMove(monster);
            ClearMonsterFollowFindLineOfSightMove(monster);
            ClearMonsterAttackSearchMove(monster);
            monster.FollowClientVisible = false;
            MonsterBehavior2Snapshot behaviorAfterStop = monster.Behavior?.GetSnapshot() ?? default;
            if (behaviorAfterStop.PendingAction == MonsterBehavior2.ActionSlot.None
                && TryGetCombatTarget(monster.TargetId, out CombatTarget target)
                && target != null
                && target.IsAlive)
            {
                monster.LocalAttackActionUsePending = true;
                monster.LocalAttackActionUseTargetId = target.EntityId;
                monster.LocalAttackActionUseSuppressPacket = true;
                monster.LocalAttackActionUseTick = 0;
                Debug.LogError($"[MON-ATTACK-STOP-RETRY] entity={monster.EntityId} target={target.EntityId} applyTick={applyTick} useTick=AttackTarget2-state5 sourceFunction=Behavior::terminateAllActionsLocal@0x00515430->MonsterBehavior2::onDoActionFailed@0x00516860->StateMachine::SendMessageA@0x005F09F0");
            }
            Debug.LogError($"[MON-ATTACK-STOP-INPUT] applied {monster.Name}#{monster.EntityId} session={sessionId} applyTick={applyTick} entityTick={_combatTick} sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
            return true;
        }

        private Behavior.MonsterBehavior2.ActionSlot ResolveMonsterAttackAction(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return Behavior.MonsterBehavior2.ActionSlot.AttackTarget2;
            if (!TryGetCombatTarget(monster.TargetId, out CombatTarget target)
                || !TryValidateMonsterActiveSkillTarget(monster, monster.PrimaryAttackSkill, target))
            {
                if (!monster.AttackPending)
                {
                    monster.SelectedActiveSkill = null;
                    monster.SelectedActiveSkillTargetEntityId = 0;
                    monster.UsePrimaryActiveSkillThisAttack = false;
                    monster.ActiveSkillUseCommittedThisAttack = false;
                }
                return Behavior.MonsterBehavior2.ActionSlot.AttackTarget2;
            }
            ApplyMonsterActiveSkillSelection(monster, monster.PrimaryAttackSkill);
            monster.SelectedActiveSkillTargetEntityId = target.EntityId;
            monster.UsePrimaryActiveSkillThisAttack = true;
            monster.ActiveSkillUseCommittedThisAttack = false;
            return Behavior.MonsterBehavior2.ActionSlot.UseTarget;
        }

        private bool TryValidateMonsterActiveSkillTarget(Monster monster, MonsterActiveSkillRuntime skill, CombatTarget target)
        {
            if (monster == null || skill == null || target == null || !monster.IsAlive || !target.IsAlive || !target.HasUnitState)
                return false;
            if (!MatchesInstance(monster, target.InstanceKey))
                return false;
            if (monster.SilenceAttributeValue > 0 || skill.CooldownRemainingTicks != 0 || monster.CurrentManaWire < skill.ManaCostWire)
                return false;
            int targetType = ResolveMonsterTargetType(skill.TargetType);
            return targetType == 2;
        }

        private uint ResolveMonsterRuntimeAttackTargetId(Monster monster)
        {
            if (monster == null)
                return 0;
            return monster.UsePrimaryActiveSkillThisAttack && monster.SelectedActiveSkillTargetEntityId != 0
                ? monster.SelectedActiveSkillTargetEntityId
                : monster.TargetId;
        }

        private void ClearMonsterUseTargetAction(Monster monster)
        {
            if (monster == null)
                return;
            if (monster.ActiveSkillSelfCycle
                || monster.ActiveSkillEffectPending
                || monster.ActiveSkillCastModifierApplied
                || monster.ActiveSkillEffectPlan != null)
                ClearMonsterActiveSkillEffectCycle(monster);
            monster.SelectedActiveSkill = null;
            monster.SelectedActiveSkillTargetEntityId = 0;
            monster.UsePrimaryActiveSkillThisAttack = false;
            monster.ActiveSkillUseCommittedThisAttack = false;
        }

        public bool DoMonsterUseAction(Monster monster)
        {
            if (monster == null || !monster.IsAlive || monster.SelectedActiveSkill == null)
                return false;
            MonsterActiveSkillRuntime skill = monster.SelectedActiveSkill;
            if (ResolveMonsterTargetType(skill.TargetType) != 0
                || monster.SelectedActiveSkillTargetEntityId != monster.EntityId
                || skill.CooldownRemainingTicks != 0
                || monster.CurrentManaWire < skill.ManaCostWire
                || monster.SilenceAttributeValue > 0)
                return false;
            if (!CanExecuteMonsterSelfActiveSkillEffect(monster, out string reason))
            {
                Debug.LogError($"[MON-SKILL-USE] {monster.Name}#{monster.EntityId} blocked skill={skill.Path ?? "none"} reason={reason ?? "unsupported-self-effect"} sourceFunction=Use::States@0x005467D0->ActiveSkill::validateUse@0x00538710");
                return false;
            }
            if (!TryResolveMonsterActiveSkillAnimation(monster, skill, out int animationFrames, out int triggerFrames, out int speed)
                || !TryResolveMonsterActiveSkillCycle(skill, animationFrames, triggerFrames, speed, out ushort busyTicks, out ushort animationTicks, out ushort triggerTicks))
                return false;
            CommitMonsterPrimarySkillUse(monster, "Use::States@0x005467D0->ActiveSkill::use@0x00538DD0");
            if (!monster.ActiveSkillUseCommittedThisAttack)
                return false;
            ApplyMonsterActiveSkillCastModifier(monster, false);
            ArmMonsterSelfActiveSkillEffect(monster, _combatTick, busyTicks, animationTicks, triggerTicks, animationFrames, triggerFrames, speed);
            return monster.ActiveSkillSelfCycle;
        }

        public bool DoMonsterUseTargetAction(Monster monster, bool weaponInterruptInput = false)
        {
            if (monster == null || !monster.IsAlive || monster.SelectedActiveSkill == null)
                return false;
            int selectedTargetType = ResolveMonsterTargetType(monster.SelectedActiveSkill.TargetType);
            if (selectedTargetType == 1)
                return DoMonsterUseFriendTargetAction(monster);
            if (selectedTargetType == 6)
                return DoMonsterUseFriendCorpseTargetAction(monster);
            if (selectedTargetType != 2)
                return false;
            uint targetEntityId = ResolveMonsterRuntimeAttackTargetId(monster);
            if (!TryGetCombatTarget(targetEntityId, out CombatTarget target) || target == null || !target.IsAlive)
                return false;
            if (monster.AttackPending)
            {
                if (weaponInterruptInput && monster.AttackCommitTick <= 0)
                {
                    ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
                    SetMonsterAttackCommitTargetFixed(monster, targetFixedX, targetFixedY);
                    FaceMonsterAttackTarget(monster, target);
                    monster.AttackWeaponCycleFromUseTargetInterrupt = true;
                    ArmMonsterRuntimeAttack(monster, target, "USETARGET-WEAPON-INTERRUPT", _combatTick);
                }
                return true;
            }
            if (!TryValidateMonsterActiveSkillTarget(monster, monster.SelectedActiveSkill, target))
                return false;
            if (!CanExecuteMonsterTargetActiveSkillEffect(monster, out string reason))
            {
                Debug.LogError($"[MON-SKILL-USE] {monster.Name}#{monster.EntityId} blocked skill={monster.SelectedActiveSkill.Path ?? "none"} reason={reason ?? "unsupported-target-effect"} sourceFunction=UseTarget::States@0x00548370->ActiveSkill::validateUse@0x00538710");
                return false;
            }
            if (!TryResolveMonsterActiveSkillAnimation(monster, monster.SelectedActiveSkill, out int animationFrames, out int triggerFrames, out int speed)
                || !TryResolveMonsterActiveSkillCycle(monster.SelectedActiveSkill, animationFrames, triggerFrames, speed, out _, out _, out _))
            {
                Debug.LogError($"[MON-SKILL-USE] {monster.Name}#{monster.EntityId} blocked skill={monster.SelectedActiveSkill.Path ?? "none"} reason=active-skill-cycle-unresolved sourceFunction=UseTarget::States@0x00548370->ActiveSkill::useTarget@0x00538F00");
                return false;
            }
            int actualRangeF32 = ResolveMonsterSkillTargetClearRangeF32(monster, monster.SelectedActiveSkill, target);
            bool targetClear = EvaluateMonsterActiveSkillTargetClearFixed(monster, target, actualRangeF32);
            if (!targetClear)
            {
                ApplyMonsterActiveSkillCastModifier(monster, true);
                monster.State = MonsterState.Chase;
                return true;
            }
            return BeginMonsterRuntimeAttackAction(monster, target, true, "UseTarget::States@0x00548370");
        }

        private bool DoMonsterUseFriendTargetAction(Monster monster)
        {
            MonsterActiveSkillRuntime skill = monster?.SelectedActiveSkill;
            Monster target = GetMonster(monster?.SelectedActiveSkillTargetEntityId ?? 0);
            if (skill == null
                || target == null
                || !target.IsAlive
                || target.CurrentHPWire == 0
                || !MatchesInstance(monster, target.InstanceKey)
                || !AreMonstersFriendsForAuthoredAoe(monster, target))
                return false;
            if (monster.ActiveSkillSelfCycle)
                return monster.ActiveSkillBusyTicksRemaining != 0;
            if (ResolveMonsterTargetType(skill.TargetType) != 1
                || monster.SilenceAttributeValue > 0
                || skill.CooldownRemainingTicks != 0
                || monster.CurrentManaWire < skill.ManaCostWire
                || !PassesMonsterActiveSkillHealthPct(target.CurrentHPWire, target.MaxHPWire, skill.HasTargetHealthPct, skill.TargetHealthPctF32)
                || !IsMonsterFriendActiveSkillTargetClear(monster, target, skill))
                return false;
            if (!CanExecuteMonsterSelfActiveSkillEffect(monster, out string reason))
            {
                Debug.LogError($"[MON-SKILL-USE] {monster.Name}#{monster.EntityId} blocked skill={skill.Path ?? "none"} target={target.Name}#{target.EntityId} reason={reason ?? "unsupported-friend-effect"} sourceFunction=UseTarget::States@0x00548370->ActiveSkill::validateUse@0x00538710");
                return false;
            }
            if (!TryResolveMonsterActiveSkillAnimation(monster, skill, out int animationFrames, out int triggerFrames, out int speed)
                || !TryResolveMonsterActiveSkillCycle(skill, animationFrames, triggerFrames, speed, out ushort busyTicks, out ushort animationTicks, out ushort triggerTicks))
                return false;
            CommitMonsterPrimarySkillUse(monster, "UseTarget::States@0x00548370->ActiveSkill::useTarget@0x00538F00");
            if (!monster.ActiveSkillUseCommittedThisAttack)
                return false;
            ApplyMonsterActiveSkillCastModifier(monster, false);
            FaceMonsterFriendActiveSkillTarget(monster, target);
            ArmMonsterFriendActiveSkillEffect(monster, target, _combatTick, busyTicks, animationTicks, triggerTicks, animationFrames, triggerFrames, speed);
            return monster.ActiveSkillSelfCycle;
        }

        private bool DoMonsterUseFriendCorpseTargetAction(Monster monster)
        {
            MonsterActiveSkillRuntime skill = monster?.SelectedActiveSkill;
            Monster target = GetMonster(monster?.SelectedActiveSkillTargetEntityId ?? 0);
            if (skill == null || !IsMonsterFriendCorpseTarget(monster, target))
                return false;
            if (monster.ActiveSkillSelfCycle)
                return monster.ActiveSkillBusyTicksRemaining != 0;
            if (ResolveMonsterTargetType(skill.TargetType) != 6
                || monster.SilenceAttributeValue > 0
                || skill.CooldownRemainingTicks != 0
                || monster.CurrentManaWire < skill.ManaCostWire
                || !PassesMonsterActiveSkillHealthPct(target.CurrentHPWire, target.MaxHPWire, skill.HasTargetHealthPct, skill.TargetHealthPctF32)
                || !IsMonsterFriendActiveSkillTargetClear(monster, target, skill))
                return false;
            if (BuildMeleeBehaviorContextForMonster(target) == null)
            {
                SkillEffectTracker.RecordRejected("monster", monster.EntityId, skill.Path, target.EntityId, "target-behavior-context-unavailable", _combatTick);
                return false;
            }
            if (!CanExecuteMonsterCorpseActiveSkillEffect(monster, out string reason))
            {
                SkillEffectTracker.RecordRejected("monster", monster.EntityId, skill.Path, target.EntityId, reason ?? "unsupported-corpse-effect", _combatTick);
                Debug.LogError($"[MON-SKILL-USE] {monster.Name}#{monster.EntityId} blocked skill={skill.Path ?? "none"} target={target.Name}#{target.EntityId} reason={reason ?? "unsupported-corpse-effect"} sourceFunction=UseTarget::States@0x00548370->ActiveSkill::validateUse@0x00538710");
                return false;
            }
            if (!TryResolveMonsterActiveSkillAnimation(monster, skill, out int animationFrames, out int triggerFrames, out int speed)
                || !TryResolveMonsterActiveSkillCycle(skill, animationFrames, triggerFrames, speed, out ushort busyTicks, out ushort animationTicks, out ushort triggerTicks))
                return false;
            CommitMonsterPrimarySkillUse(monster, "UseTarget::States@0x00548370->ActiveSkill::useTarget@0x00538F00");
            if (!monster.ActiveSkillUseCommittedThisAttack)
                return false;
            FaceMonsterFriendActiveSkillTarget(monster, target);
            ArmMonsterFriendActiveSkillEffect(monster, target, _combatTick, busyTicks, animationTicks, triggerTicks, animationFrames, triggerFrames, speed);
            return monster.ActiveSkillSelfCycle;
        }

        private bool IsMonsterFriendActiveSkillTargetClear(Monster monster, Monster target, MonsterActiveSkillRuntime skill)
        {
            if (monster == null || target == null || skill == null)
                return false;
            int skillRangeF32 = Math.Max(0, skill.RangeF32);
            int actorRadiusF32 = (int)(((long)Math.Max(0, monster.BoundingBoxRadiusXYF32) * 0x180) >> 8);
            int targetRadiusF32 = (int)(((long)Math.Max(0, target.BoundingBoxRadiusXYF32) * 0x180) >> 8);
            int rangeF32 = SaturatingAddFixed8(skillRangeF32, SaturatingAddFixed8(actorRadiusF32, targetRadiusF32));
            ASGetPositionFixed(
                monster.PosFixedX,
                monster.PosFixedY,
                monster.PosFixedZ,
                monster.BoundingBoxMinZF32,
                monster.BoundingBoxMaxZF32,
                out int actorFixedX,
                out int actorFixedY,
                out int actorFixedZ);
            ASGetPositionFixed(
                target.PosFixedX,
                target.PosFixedY,
                target.PosFixedZ,
                target.BoundingBoxMinZF32,
                target.BoundingBoxMaxZF32,
                out int targetFixedX,
                out int targetFixedY,
                out int targetFixedZ);
            EvaluateUseTargetInitUseFixed3D(
                actorFixedX,
                actorFixedY,
                actorFixedZ,
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                rangeF32,
                0,
                out _,
                out long distanceSquaredFixed8,
                out long thresholdSquaredFixed8);
            if (thresholdSquaredFixed8 <= 0 || distanceSquaredFixed8 >= thresholdSquaredFixed8)
                return false;
            string instanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
            return !WorldCollision.Instance.TrySegmentHitFixed(
                monster.ZoneName,
                instanceKey,
                actorFixedX,
                actorFixedY,
                actorFixedZ,
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                UnitMover.Fixed,
                out _);
        }

        private void FaceMonsterFriendActiveSkillTarget(Monster monster, Monster target)
        {
            if (monster == null || target == null)
                return;
            int headingFixed = UnitMover.VectorToHeadingFixed(
                target.PosFixedX - monster.PosFixedX,
                target.PosFixedY - monster.PosFixedY);
            monster.HeadingFixed = headingFixed;
            monster.ChaseHeadingFixed = headingFixed;
            monster.ChaseHeadingInit = true;
            monster.UnitMoverDesiredHeadingFixed = headingFixed;
            monster.UnitMoverDesiredHeadingInit = true;
            monster.ClientVisibleHeadingFixed = headingFixed;
            monster.ClientVisibleHeadingInit = true;
            Debug.LogError($"[MON-SKILL-FACING] entity={monster.EntityId} target={target.EntityId} tick={_combatTick} actorFixed=({monster.PosFixedX},{monster.PosFixedY}) targetFixed=({target.PosFixedX},{target.PosFixedY}) heading={headingFixed} sourceFunction=UseTarget::States@0x00548370->UnitMover::FaceTarget@0x00535EF0");
        }

        private bool ValidateMonsterAttackTarget2Action(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return false;
            if (!TryGetCombatTarget(monster.TargetId, out CombatTarget target)
                || target == null
                || !target.IsAlive
                || !target.HasUnitState
                || !MatchesInstance(monster, target.InstanceKey))
                return false;
            return ValidateMonsterWeaponUseTargetAction(
                monster,
                target,
                "Behavior::doActionLocal@0x00515130->AttackTarget2::validate@0x00523B90");
        }

        private bool ValidateMonsterWeaponUseTargetAction(Monster monster, CombatTarget target, string sourceFunction)
        {
            if (monster == null
                || target == null
                || !monster.IsAlive
                || !target.IsAlive
                || !target.HasUnitState
                || !MatchesInstance(monster, target.InstanceKey))
                return false;
            int combatTick = unchecked((int)_combatTick);
            if (monster.NextAttackTick != int.MaxValue && combatTick < monster.NextAttackTick)
            {
                Debug.LogError($"[MON-WEAPON-VALIDATE] entity={monster.EntityId} target={target.EntityId} tick={_combatTick} result=False cooldown={monster.NextAttackTick - combatTick} readyTick={monster.NextAttackTick} sourceFunction={sourceFunction}->Weapon::validateUse@0x00597CA0 field=Weapon+0x86");
                return false;
            }
            return true;
        }

        public bool DoMonsterAttackAction(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return false;
            if (monster.AttackPending)
                return true;
            if (!TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive || !target.HasUnitState)
            {
                ClearMonsterLocalAttackActionUse(monster);
                return false;
            }
            int combatTick = unchecked((int)_combatTick);
            if (monster.NextAttackTick != int.MaxValue && combatTick < monster.NextAttackTick)
            {
                Debug.LogError($"[MON-WEAPON-VALIDATE] entity={monster.EntityId} target={target.EntityId} tick={_combatTick} result=False cooldown={monster.NextAttackTick - combatTick} readyTick={monster.NextAttackTick} sourceFunction=AttackTarget2::States@0x00523DA0->Weapon::validateUse@0x00597C40 field=Weapon+0x86");
                ClearMonsterLocalAttackActionUse(monster);
                return false;
            }
            int allowedRangeFixed = ResolveMonsterEffectiveAttackRangeFixed(monster);
            TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY);
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            bool actionTargetClear = allowedRangeFixed > 0
                && IsMonsterActionTargetClearFixed(monster, target, allowedRangeFixed);
            if (!actionTargetClear)
            {
                monster.NextAttackTick = unchecked((int)_combatTick);
                Debug.LogError($"[MON-BEHAVIOR-TARGET-CLEAR] entity={monster.EntityId} target={target.EntityId} tick={_combatTick} result=False lane=unit-current monsterFixed=({monsterFixedX},{monsterFixedY}) targetFixed=({targetFixedX},{targetFixedY}) rangeFixed={allowedRangeFixed} sourceFunction=AttackTarget2::validate@0x00523B90->Weapon::targetIsClear@0x00597B00");
                ClearMonsterLocalAttackActionUse(monster);
                return false;
            }
            monster.SelectedActiveSkill = null;
            monster.SelectedActiveSkillTargetEntityId = 0;
            monster.UsePrimaryActiveSkillThisAttack = false;
            monster.ActiveSkillUseCommittedThisAttack = false;
            return BeginMonsterRuntimeAttackAction(monster, target, false, "AttackTarget2::States@0x00523DA0");
        }

        private void PrepareMonsterSearchAttackTargetActionUse(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return;
            if (!TryGetCombatTarget(monster.TargetId, out CombatTarget target) || target == null || !target.IsAlive)
                return;
            if (monster.LocalAttackActionUsePending && monster.LocalAttackActionUseSuppressPacket)
                return;
            monster.LocalAttackActionUsePending = true;
            monster.LocalAttackActionUseTargetId = target.EntityId;
            monster.LocalAttackActionUseSuppressPacket = false;
            monster.LocalAttackActionUseTick = unchecked(_combatTick + 1);
            Debug.LogError($"[MON-SEARCH-LOCAL-USE] entity={monster.EntityId} target={target.EntityId} tick={_combatTick} useTick={monster.LocalAttackActionUseTick} admission=next-AttackTarget2-state5 sourceFunction=SearchForAttack::States@0x0052DFB0->Action::terminate@0x0052C1C0->MonsterBehavior2::DoAttackAction@0x0051D900");
        }

        private bool BeginMonsterRuntimeAttackAction(Monster monster, CombatTarget target, bool useActiveSkill, string source)
        {
            if (monster == null || target == null || !monster.IsAlive || !target.IsAlive || monster.AttackPending)
                return monster?.AttackPending == true;
            ushort activeSkillBusyTicks = 0;
            ushort activeSkillAnimationTicks = 0;
            ushort activeSkillTriggerTicks = 0;
            int activeSkillAnimationFrames = 0;
            int activeSkillTriggerFrames = 0;
            int activeSkillSpeed = 0;
            if (useActiveSkill
                && (!CanExecuteMonsterTargetActiveSkillEffect(monster, out string activeSkillReason)
                    || !TryResolveMonsterActiveSkillAnimation(monster, monster.SelectedActiveSkill, out activeSkillAnimationFrames, out activeSkillTriggerFrames, out activeSkillSpeed)
                    || !TryResolveMonsterActiveSkillCycle(monster.SelectedActiveSkill, activeSkillAnimationFrames, activeSkillTriggerFrames, activeSkillSpeed, out activeSkillBusyTicks, out activeSkillAnimationTicks, out activeSkillTriggerTicks)))
            {
                Debug.LogError($"[MON-SKILL-USE] {monster.Name}#{monster.EntityId} blocked skill={monster.SelectedActiveSkill?.Path ?? "none"} reason={activeSkillReason ?? "active-skill-cycle-unresolved"} sourceFunction=UseTarget::States@0x00548370->ActiveSkill::validateUse@0x00538710");
                return false;
            }
            bool localActionUse = monster.LocalAttackActionUsePending
                && monster.LocalAttackActionUseTargetId == target.EntityId;
            if (localActionUse
                && monster.LocalAttackActionUseTick != 0
                && monster.LocalAttackActionUseTick != _combatTick)
            {
                monster.LocalAttackActionUsePending = false;
                monster.LocalAttackActionUseTargetId = 0;
                monster.LocalAttackActionUseSuppressPacket = false;
                monster.LocalAttackActionUseTick = 0;
                localActionUse = false;
            }
            bool suppressPacket = localActionUse && monster.LocalAttackActionUseSuppressPacket;
            monster.AttackPending = true;
            monster.AttackClientVisible = false;
            monster.AttackActionQueued = false;
            monster.AttackActionAdmissionTick = 0;
            monster.AttackContactOnly = false;
            monster.AttackHitResolved = false;
            monster.AttackSoundRaw = 0;
            monster.AttackSoundGateRaw = 0;
            monster.AttackSoundRepeatRaw = 0;
            monster.AttackUseRaw = 0;
            monster.AttackStartTick = 0;
            monster.AttackCommitTick = 0;
            monster.AttackSoundTick = 0;
            monster.AttackEndTick = 0;
            monster.AttackSoundPending = false;
            ClearMonsterActiveSkillEffectCycle(monster, useActiveSkill);
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            SetMonsterAttackCommitTargetFixed(monster, targetFixedX, targetFixedY);
            monster.State = MonsterState.Attacking;
            monster.Ai?.SetState(MonsterStateId.Attack, "DoAttackAction");
            if (monster.Ai != null)
                monster.Ai.SkillDelayTimer = 300;
            EncounterMarkActive(monster, "DoAttackAction");
            monster.AttackSessionId++;
            if (monster.AttackSessionId == 0)
                monster.AttackSessionId = 1;
            Debug.LogError($"[MON-ONATTACKED-INPUT] {monster.Name}#{monster.EntityId}->{target.Name} session={monster.AttackSessionId} applyTick={_combatTick} activeSkill={useActiveSkill} skill={monster.SelectedActiveSkill?.Path ?? "none"} sourceFunction={source}->Behavior::doActionLocal@0x00515130");
            if (suppressPacket)
                monster.AttackClientVisible = true;
            else
                OnMonsterAttackStarted?.Invoke(monster, target, monster.AttackSessionId);
            if (!monster.AttackPending)
            {
                ClearMonsterLocalAttackActionUse(monster);
                return false;
            }
            ClearMonsterLocalAttackActionUse(monster);
            FaceMonsterAttackTarget(monster, target);
            if (monster.UsePrimaryActiveSkillThisAttack)
            {
                CommitMonsterPrimarySkillUse(monster, "UseTarget::States@0x00548370->ActiveSkill::use@0x00538DD0");
                if (!monster.ActiveSkillUseCommittedThisAttack)
                {
                    CancelMonsterPendingAttack(monster, "active-skill-validate-failed");
                    return false;
                }
                ApplyMonsterActiveSkillCastModifier(monster, false);
                ArmMonsterActiveSkillEffect(
                    monster,
                    target,
                    _combatTick,
                    activeSkillBusyTicks,
                    activeSkillAnimationTicks,
                    activeSkillTriggerTicks,
                    activeSkillAnimationFrames,
                    activeSkillTriggerFrames,
                    activeSkillSpeed);
            }
            else
                ArmMonsterRuntimeAttack(monster, target, "ATTACKTARGET2-LOCAL", _combatTick);
            string localActionSource = suppressPacket
                ? "Behavior::terminateAllActionsLocal@0x00515430->MonsterBehavior2::onDoActionFailed@0x00516860->MonsterBehavior2::DoAttackAction@0x0051D900->Behavior::doActionLocal@0x00515130->AttackTarget2::States@0x00523DA0"
                : "MonsterBehavior2::DoAttackAction@0x0051D900->Behavior::doActionLocal@0x00515130->AttackTarget2::States@0x00523DA0->MeleeWeapon::use@0x005917D0";
            Debug.LogError($"[MON-ATTACK-INPUT] {monster.Name}#{monster.EntityId}->{target.Name} session={monster.AttackSessionId} startTick={_combatTick} action={(monster.UsePrimaryActiveSkillThisAttack ? "UseTarget" : "AttackTarget2")} admission={(suppressPacket ? "local-terminate-all" : "local-behavior")} sourceFunction={localActionSource}");
            return monster.AttackPending;
        }

        public void TickMonsterBehaviorForEntity(uint entityId)
        {
            if (!_activeMonsters.TryGetValue(entityId, out Monster monster) || monster == null || monster.ReturnRemovalPending)
                return;
            var rng = GetRoomRngForMonster(monster);
            if (rng == null) return;
            bool damageReactionActionActiveAtTickEntry = IsMonsterDamageReactionActive(monster);
            AdvanceMonsterDamageReaction(monster, rng);
            bool damageReactionActionActiveNow = IsMonsterDamageReactionActive(monster);

            CombatTarget target = null;
            uint targetEntityId = ResolveMonsterActionTargetEntityId(monster);
            if (targetEntityId != 0
                && TryGetCombatTarget(targetEntityId, out CombatTarget resolvedTarget)
                && resolvedTarget != null
                && MatchesInstance(monster, resolvedTarget.InstanceKey))
                target = resolvedTarget;

            int allowedRangeFixed = Math.Max(0, ResolveMonsterEffectiveAttackRangeFixed(monster));
            int distFixed = int.MaxValue;
            if (target != null && target.IsAlive)
            {
                TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int monsterFixedX, out int monsterFixedY, out _);
                ResolveMonsterBehaviorTargetPositionFixed(monster, target, out int targetFixedX, out int targetFixedY, out _);
                long dxFixed = (long)targetFixedX - monsterFixedX;
                long dyFixed = (long)targetFixedY - monsterFixedY;
                distFixed = UnitMover.IntSqrt(dxFixed * dxFixed + dyFixed * dyFixed);
            }

            monster.MeleeScanTargetId = target?.EntityId ?? 0;
            var behaviorContext = BuildMeleeBehaviorContext(rng, monster, target, distFixed, allowedRangeFixed);
            behaviorContext.DamageReactionActionActiveAtTickEntryValue = damageReactionActionActiveAtTickEntry;
            behaviorContext.DamageReactionActionActiveNowValue = damageReactionActionActiveNow;
            if (monster.Behavior == null)
            {
                monster.Behavior = new Behavior.MonsterBehavior2();
                monster.Behavior.EnterSimulation(behaviorContext);
                monster.StockUnitState = 8;
            }
            if (monster.Behavior.SpawnActive)
            {
                int turnRateFixed = UnitMover.TurnRatePerTickFixed(monster.TurnRateDegrees);
                monster.HeadingFixed = UnitMover.InterpolateHeading(monster.HeadingFixed, 0, turnRateFixed);
            }
            monster.Behavior.UpdateActions(behaviorContext);
            AdvanceRetiredWanderMover(monster);
            if (monster.StockUnitState == 4)
                CompleteStockUnitSpawn(monster);
            bool moverSuppressed = (monster.UnitFlags & 0x1000) != 0;
            if (!moverSuppressed)
            {
                AdvanceMonsterUnitMoverFacing(monster);
                ProcessMonsterMovement(0, monster, true);
            }
            behaviorContext = BuildMeleeBehaviorContextForMonster(monster);
            behaviorContext.DamageReactionActionActiveAtTickEntryValue = damageReactionActionActiveAtTickEntry;
            behaviorContext.DamageReactionActionActiveNowValue = damageReactionActionActiveNow;
            monster.Behavior.UpdateStateMachine(behaviorContext);
            if (monster.Behavior.SpawnCompletedThisTick)
            {
                bool registerWander = ShouldRegisterWander(monster.IdleAction, monster.WanderRangeF32, monster.ZoneName);
                bool pendingWander = monster.Behavior.WanderActionPending;
                Debug.LogError($"[MON-SPAWN-ACTION] {monster.Name}#{monster.EntityId} tick={_combatTick} phase={monster.Behavior.SpawnPhase} counter={monster.Behavior.SpawnCounter} registerWander={registerWander} pendingWander={pendingWander} sourceFunction=Spawn::update@0x0052F140->Spawn::terminate@0x0052C1C0->Behavior::update@0x005154B0");
            }
            bool promoteWander = monster.Behavior.TryPromotePendingWanderAction();
            if (promoteWander && ShouldRegisterWander(monster.IdleAction, monster.WanderRangeF32, monster.ZoneName) && !WanderSimulator.Instance.IsRegisteredEntity(monster.EntityId))
            {
                bool canWander = !string.IsNullOrWhiteSpace(monster.EncounterGroupKey);
                Debug.LogError($"[WANDER-LEASH] {monster.Name}#{monster.EntityId} canWander={canWander} encGroup='{monster.EncounterGroupKey}' source=Unit+0x30c-EncounterObject");
                WanderSimulator.Instance.RegisterMonster(monster, canWander, false);
            }
            UpdateMonsterWorldEntityAnimationSnapshot(monster);
        }

    }
}
