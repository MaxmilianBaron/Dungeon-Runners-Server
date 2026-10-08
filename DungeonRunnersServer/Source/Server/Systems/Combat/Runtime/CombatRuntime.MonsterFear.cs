using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private const byte FleeDirectionTicks = 15;

        private static bool HasMonsterCowardiceModifier(Monster monster)
        {
            return monster != null && (monster.CowardicePermanent || monster.CowardiceRemainingTicks > 0);
        }

        private static int NormalizeMonsterHeadingFixed(int headingFixed)
        {
            int normalized = headingFixed % UnitMover.FullCircleFixed;
            return normalized < 0 ? normalized + UnitMover.FullCircleFixed : normalized;
        }

        private bool StartMonsterFleeAction(Monster monster, MeleeBehaviorContext context)
        {
            if (monster == null
                || context == null
                || context.Rng == null
                || !monster.IsAlive
                || !HasMonsterCowardiceModifier(monster))
                return false;
            StopMonsterMoving(monster);
            int reverseHeadingFixed = NormalizeMonsterHeadingFixed(monster.HeadingFixed + 180 * UnitMover.Fixed);
            monster.HeadingFixed = reverseHeadingFixed;
            monster.ChaseHeadingFixed = reverseHeadingFixed;
            monster.ChaseHeadingInit = true;
            monster.FleeActionActive = true;
            monster.FleeTouchingCorner = false;
            SetMonsterFleeDirection(monster, context, "Flee::initState@0x00526880");
            Debug.LogError($"[MON-FLEE] entity={monster.EntityId} tick={_combatTick} event=start heading={monster.HeadingFixed} direction={monster.MoveInDirectionHeadingFixed} countdown={monster.FleeTurnCountdownTicks} modifier={monster.CowardiceModifierKey ?? "none"} sourceFunction=Flee::start@0x00526830->Flee::initState@0x00526880");
            return true;
        }

        private void UpdateMonsterFleeAction(Monster monster, MeleeBehaviorContext context)
        {
            if (monster == null || !monster.FleeActionActive)
                return;
            if (!monster.IsAlive || !HasMonsterCowardiceModifier(monster) || context?.Rng == null)
            {
                monster.FleeActionActive = false;
                return;
            }
            if (monster.CowardiceAvoidCorners && monster.FleeTouchingCorner)
            {
                monster.FleeTouchingCorner = false;
                SetMonsterFleeDirection(monster, context, "Flee::updateState@0x00526930:corner");
                return;
            }
            if (monster.FleeTurnCountdownTicks > 0)
                monster.FleeTurnCountdownTicks--;
            if (monster.FleeTurnCountdownTicks == 0)
                SetMonsterFleeDirection(monster, context, "Flee::updateState@0x00526930:countdown");
        }

        private void SetMonsterFleeDirection(Monster monster, MeleeBehaviorContext context, string site)
        {
            uint raw = context.RoomDraw(site);
            int offsetDegrees = (int)(raw % 90u) - 45;
            monster.MoveInDirectionHeadingFixed = NormalizeMonsterHeadingFixed(monster.HeadingFixed + offsetDegrees * UnitMover.Fixed);
            monster.MoveInDirectionHeadingInit = true;
            monster.UnitMoverDesiredHeadingFixed = monster.MoveInDirectionHeadingFixed;
            monster.UnitMoverDesiredHeadingInit = true;
            monster.FleeTurnCountdownTicks = FleeDirectionTicks;
            Debug.LogError($"[MON-FLEE-RNG] entity={monster.EntityId} tick={_combatTick} raw=0x{raw:X8} offset={offsetDegrees} heading={monster.HeadingFixed} direction={monster.MoveInDirectionHeadingFixed} countdown={monster.FleeTurnCountdownTicks} sourceFunction={site}");
        }

        private void StopMonsterFleeAction(Monster monster)
        {
            if (monster == null)
                return;
            bool wasActive = monster.FleeActionActive || monster.MoveInDirectionHeadingInit;
            monster.FleeActionActive = false;
            monster.FleeTurnCountdownTicks = 0;
            monster.FleeTouchingCorner = false;
            monster.MoveInDirectionHeadingInit = false;
            monster.UnitMoverDesiredHeadingInit = false;
            monster.UnitMoverMovingThisFrame = false;
            monster.ClientVisibleMovingThisFrame = false;
            if (wasActive)
                Debug.LogError($"[MON-FLEE] entity={monster.EntityId} tick={_combatTick} event=stop sourceFunction=Flee::stop@0x00526840");
        }

        private static bool ShouldReplaceCowardiceUniqueByType(Monster target, int powerLevelF32, int durationTicks)
        {
            if (target == null || !HasMonsterCowardiceModifier(target))
                return true;
            int existingDurationTicks = target.CowardicePermanent ? 0 : target.CowardiceRemainingTicks;
            if (powerLevelF32 > target.CowardicePowerLevelF32)
                return true;
            if (durationTicks == 0)
                return existingDurationTicks != 0;
            return existingDurationTicks != 0 && durationTicks > existingDurationTicks;
        }

        private bool ApplyMonsterCowardiceModifier(
            Monster sourceMonster,
            Monster target,
            string skillPath,
            string effectPath,
            string modifierPath,
            int powerLevelF32,
            int durationTicks,
            bool removeOnDeath,
            bool avoidCorners)
        {
            if (sourceMonster == null || target == null || !target.IsAlive || durationTicks < 0)
                return false;
            if (!ShouldReplaceCowardiceUniqueByType(target, powerLevelF32, durationTicks))
            {
                Debug.LogError($"[MON-COWARDICE] target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} result=rejected power={powerLevelF32}/{target.CowardicePowerLevelF32} duration={durationTicks}/{target.CowardiceRemainingTicks} modifier={modifierPath ?? "none"} sourceFunction=Modifiers::addModifierLocal@0x00501770");
                return true;
            }
            bool continueExistingFlee = target.FleeActionActive;
            target.CowardiceModifierKey = "CowardiceModifier";
            target.CowardiceSkillPath = skillPath;
            target.CowardiceEffectPath = effectPath;
            target.CowardiceSourceEntityId = sourceMonster.EntityId;
            target.CowardicePowerLevelF32 = powerLevelF32;
            target.CowardiceRemainingTicks = durationTicks;
            target.CowardicePermanent = durationTicks == 0;
            target.CowardiceRemoveOnDeath = removeOnDeath;
            target.CowardiceAvoidCorners = avoidCorners;
            bool fleeAccepted = continueExistingFlee;
            if (!continueExistingFlee && target.Behavior != null)
            {
                MeleeBehaviorContext targetContext = BuildMeleeBehaviorContextForMonster(target);
                fleeAccepted = targetContext != null && target.Behavior.BeginFlee(targetContext);
            }
            Debug.LogError($"[MON-COWARDICE] target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} result=applied power={powerLevelF32} duration={durationTicks} removeOnDeath={removeOnDeath} avoidCorners={avoidCorners} fleeAccepted={fleeAccepted} modifier={modifierPath ?? "none"} sourceFunction=SpellModEffect::doEffect@0x00554460->CowardiceModifier::init@0x00569220");
            return true;
        }

        private void AdvanceMonsterCowardiceModifierTick(Monster monster)
        {
            if (!HasMonsterCowardiceModifier(monster))
                return;
            if ((!monster.IsAlive || monster.CurrentHPWire == 0) && monster.CowardiceRemoveOnDeath)
            {
                ClearMonsterCowardiceModifier(monster, "RemoveOnDeath");
                return;
            }
            if (monster.CowardicePermanent)
                return;
            monster.CowardiceRemainingTicks--;
            if (monster.CowardiceRemainingTicks == 0)
                ClearMonsterCowardiceModifier(monster, "duration-expired");
        }

        private void ClearMonsterCowardiceModifier(Monster monster, string reason)
        {
            if (monster == null)
                return;
            bool hadModifier = HasMonsterCowardiceModifier(monster) || !string.IsNullOrWhiteSpace(monster.CowardiceModifierKey);
            monster.CowardiceModifierKey = null;
            monster.CowardiceSkillPath = null;
            monster.CowardiceEffectPath = null;
            monster.CowardiceSourceEntityId = 0;
            monster.CowardicePowerLevelF32 = 0;
            monster.CowardiceRemainingTicks = 0;
            monster.CowardicePermanent = false;
            monster.CowardiceRemoveOnDeath = false;
            monster.CowardiceAvoidCorners = true;
            monster.FleeActionActive = false;
            if (hadModifier)
                Debug.LogError($"[MON-COWARDICE] target={monster.Name}#{monster.EntityId} result=removed reason={reason ?? "unknown"} sourceFunction=CowardiceModifier::deinit@0x005694B0");
        }

        private bool ProcessMonsterFleeMovement(Monster monster, bool emitPositionChanged)
        {
            if (monster == null || !monster.IsAlive || !monster.FleeActionActive || !monster.MoveInDirectionHeadingInit)
                return false;
            bool previousMoving = monster.UnitMoverMovingThisFrame;
            monster.UnitMoverMovingThisFrame = false;
            monster.ClientVisibleMovingThisFrameTick = _combatTick;
            monster.ClientVisibleMovingThisFrame = false;
            int speedFixed = ResolveMonsterMovementSpeedFixed(monster);
            if (speedFixed <= 0)
                return true;
            int stepFixed = (int)(((long)speedFixed << 8) / 0x1e00);
            if (stepFixed < 1)
                stepFixed = 1;
            int curFixedX = monster.PosFixedX;
            int curFixedY = monster.PosFixedY;
            int curFixedZ = monster.PosFixedZ;
            if (!monster.ChaseHeadingInit)
            {
                monster.ChaseHeadingFixed = monster.HeadingFixed;
                monster.ChaseHeadingInit = true;
            }
            int turnRateFixed = UnitMover.TurnRatePerTickFixed(monster.TurnRateDegrees);
            UnitMover.StepInDirectionFixedHeading(
                curFixedX,
                curFixedY,
                monster.ChaseHeadingFixed,
                monster.MoveInDirectionHeadingFixed,
                stepFixed,
                turnRateFixed,
                monster.TurnBeforeMoving,
                previousMoving,
                out int candidateFixedX,
                out int candidateFixedY,
                out int nextHeadingFixed,
                out bool movingThisFrame);
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(ResolveMonsterPathMapKey(monster));
            ResolveMonsterMovementFixed(
                monster,
                pathMap,
                curFixedX,
                curFixedY,
                curFixedZ,
                candidateFixedX,
                candidateFixedY,
                out int nextFixedX,
                out int nextFixedY,
                out int nextFixedZ,
                _combatTick);
            monster.FleeTouchingCorner = nextFixedX != candidateFixedX || nextFixedY != candidateFixedY;
            monster.PosFixedX = nextFixedX;
            monster.PosFixedY = nextFixedY;
            monster.PosFixedZ = pathMap != null && pathMap.TryGetHeightAtFixed(nextFixedX, nextFixedY, out _)
                ? nextFixedZ
                : ResolveTerrainHeightFixedValue(monster, nextFixedX, nextFixedY, nextFixedZ);
            monster.ChaseHeadingFixed = nextHeadingFixed;
            monster.HeadingFixed = nextHeadingFixed;
            monster.UnitMoverMovingThisFrame = movingThisFrame && (nextFixedX != curFixedX || nextFixedY != curFixedY);
            monster.ClientVisibleMovingThisFrame = nextFixedX != curFixedX || nextFixedY != curFixedY;
            if (emitPositionChanged)
                OnMonsterPositionChanged?.Invoke(monster);
            Debug.LogError($"[MON-FLEE-MOVE] entity={monster.EntityId} tick={_combatTick} position=({curFixedX},{curFixedY})->({nextFixedX},{nextFixedY}) heading={nextHeadingFixed} desired={monster.MoveInDirectionHeadingFixed} touchingCorner={monster.FleeTouchingCorner} speedF32={speedFixed} sourceFunction=Flee::updateState@0x00526930->UnitMover::MoveInDirection@0x00536080");
            return true;
        }

        private static bool AreMonstersFriendsForAuthoredAoe(Monster source, Monster candidate)
        {
            if (source == null || candidate == null)
                return false;
            return !UnitFactionRules.IsEnemyFaction(source.FactionID, candidate.FactionID,
                source.UnitDescIsAlwaysFriendly, candidate.UnitDescIsAlwaysFriendly);
        }

        private List<Monster> CollectMonsterFriendAoeTargets(Monster center, int radiusF32, int maxTargets)
        {
            var ranked = new List<(Monster Monster, long DistanceSquaredF32)>();
            if (center == null || radiusF32 <= 0 || maxTargets <= 0)
                return new List<Monster>();
            long radiusSquaredF32 = ((long)radiusF32 * radiusF32) >> 8;
            foreach (Monster candidate in GetAllMonsters())
            {
                if (candidate == null
                    || candidate.EntityId == center.EntityId
                    || !candidate.IsAlive
                    || candidate.CurrentHPWire == 0
                    || !string.Equals(
                        RoomRuntime.NormalizeInstanceKey(center.InstanceKey),
                        RoomRuntime.NormalizeInstanceKey(candidate.InstanceKey),
                        StringComparison.OrdinalIgnoreCase)
                    || !AreMonstersFriendsForAuthoredAoe(center, candidate))
                    continue;
                long dx = (long)candidate.PosFixedX - center.PosFixedX;
                long dy = (long)candidate.PosFixedY - center.PosFixedY;
                long dz = (long)candidate.PosFixedZ - center.PosFixedZ;
                long distanceSquaredF32 = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                if (distanceSquaredF32 <= radiusSquaredF32)
                    ranked.Add((candidate, distanceSquaredF32));
            }
            ranked.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceSquaredF32.CompareTo(right.DistanceSquaredF32);
                return distanceOrder != 0 ? distanceOrder : left.Monster.EntityId.CompareTo(right.Monster.EntityId);
            });
            int count = Math.Min(maxTargets, ranked.Count);
            var result = new List<Monster>(count);
            for (int i = 0; i < count; i++)
                result.Add(ranked[i].Monster);
            return result;
        }
    }
}
