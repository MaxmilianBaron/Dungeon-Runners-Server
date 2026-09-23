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
    {public int GetComponentOffset(uint componentId)
        {
            if (_componentToEntityMap.TryGetValue(componentId, out uint entityId))
                return (int)(componentId - entityId);
            return -1;
        }
        public void DespawnMonster(uint entityId, bool destroyRoomOwnedSubEntities = false, bool deferRoomOwnedSubEntities = false)
        {
            if (!_activeMonsters.TryGetValue(entityId, out var monster))
                return;

            if (!destroyRoomOwnedSubEntities)
            {
                if (!_pendingMonsterFinalRemovals.Add(entityId))
                    return;
                OnMonsterDespawned?.Invoke(monster);
                ScheduleEntityOrderRemoval(entityId, _nextClientEntityUpdateTick);
                return;
            }

            FinalizeMonsterDespawn(entityId, destroyRoomOwnedSubEntities, deferRoomOwnedSubEntities);
        }

        private void FinalizeMonsterDespawn(uint entityId, bool destroyRoomOwnedSubEntities, bool deferRoomOwnedSubEntities)
        {
            if (!_activeMonsters.TryGetValue(entityId, out var monster))
                return;

            _pendingMonsterFinalRemovals.Remove(entityId);
            _pendingSummonInitializations.Remove(entityId);
            ClearMonsterMoveToPointPath(monster, 0);
            string reason = destroyRoomOwnedSubEntities ? "manager-destroy" : "despawn";
            EncounterOnUnitRemoved(monster, reason);
            RemovePlayerModifiersFromSource(monster.EntityId, reason);
            DiscardRemovedCombatTargetModifiers(entityId);

            _componentToEntityMap.Remove(monster.EntityId);
            _componentToEntityMap.Remove(monster.BehaviorId);
            _componentToEntityMap.Remove(monster.SkillsId);
            _componentToEntityMap.Remove(monster.ManipulatorsId);
            _componentToEntityMap.Remove(monster.ModifiersId);
            _componentToEntityMap.Remove(monster.UnitId);
            _monsterRuntimeDamageCommitted.Remove(entityId);
            _monsterHPRegenLastTick.Remove(entityId);
            _monsterHPRegenCooldownTicks.Remove(entityId);
            _monsterManaRegenLastTick.Remove(entityId);
            _monsterManaRegenCooldownTicks.Remove(entityId);
            _monsterStateTraceSignatures.Remove(entityId);
            _meleeBehaviorContexts.Remove(entityId);
            if (destroyRoomOwnedSubEntities && !deferRoomOwnedSubEntities)
                ClearMonsterProjectilesForMonster(entityId);

            WanderSimulator.Instance.UnregisterEntity(entityId);
            if (destroyRoomOwnedSubEntities)
                OnMonsterDespawned?.Invoke(monster);
            UnregisterEntityOrder(entityId);
            _activeMonsters.Remove(entityId);
            ReleaseCombatNetworkId(monster.EntityId);
            ReleaseCombatNetworkId(monster.BehaviorId);
            ReleaseCombatNetworkId(monster.SkillsId);
            ReleaseCombatNetworkId(monster.ManipulatorsId);
            ReleaseCombatNetworkId(monster.ModifiersId);
            ReleaseCombatNetworkId(monster.UnitId);
        }
        public void RemoveComponentMapping(uint componentId)
        {
            _componentToEntityMap.Remove(componentId);
        }

        public void AddComponentMapping(uint componentId, uint entityId)
        {
            _componentToEntityMap[componentId] = entityId;
        }
        public Monster GetMonster(uint entityId)
        {
            return _activeMonsters.TryGetValue(entityId, out var m) ? m : null;
        }

        public bool IsMonster(uint entityId)
        {
            return _activeMonsters.ContainsKey(entityId);
        }

        public bool IsKnownMonsterTarget(ushort clientTargetId, string instanceKey = null)
        {
            Monster monster = GetMonster(clientTargetId);
            if (monster == null && _componentToEntityMap.TryGetValue(clientTargetId, out uint entityId))
                monster = GetMonster(entityId);
            return monster != null && IsMonsterCombatSelectable(monster) && MatchesInstance(monster, instanceKey);
        }

        public IEnumerable<Monster> GetActiveMonsters() => GetAllMonsters();

        public Monster GetNearestMonsterFixed(int originFixedX, int originFixedY, int maxRangeFixed, string instanceKey = null)
        {
            Monster nearest = null;
            maxRangeFixed = Math.Max(0, maxRangeFixed);
            long nearestDistSq = (long)maxRangeFixed * maxRangeFixed;
            string normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey)
                ? null
                : RoomRuntime.NormalizeInstanceKey(instanceKey);

            Debug.LogError($"[GET-NEAREST] posFixed8=({originFixedX},{originFixedY}) rangeFixed8={maxRangeFixed} instance={normalizedInstanceKey ?? "any"} monsters={_activeMonsters.Count}");

            foreach (var monster in GetAllMonsters())
            {
                if (!IsMonsterCombatSelectable(monster)) continue;
                if (!MatchesInstance(monster, normalizedInstanceKey)) continue;

                long dx = (long)monster.PosFixedX - originFixedX;
                long dy = (long)monster.PosFixedY - originFixedY;
                long distSq = dx * dx + dy * dy;
                int distFixed = UnitMover.IntSqrt(distSq);

                Debug.LogError($"[GET-NEAREST] candidate='{monster.Name}' posFixed8=({monster.PosFixedX},{monster.PosFixedY}) distFixed8={distFixed}");

                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearest = monster;
                }
            }

            if (nearest != null)
                Debug.LogError($"[GET-NEAREST] result='{nearest.Name}' posFixed8=({nearest.PosFixedX},{nearest.PosFixedY})");
            else
                Debug.LogError($"[GET-NEAREST] result=none rangeFixed8={maxRangeFixed}");

            return nearest;
        }

        private bool IsMonsterCombatSelectable(Monster monster)
        {
            return monster != null && monster.IsAlive && GetRuntimeMonsterHPWire(monster, "SELECT") > 0;
        }

        public bool MatchesInstance(Monster monster, string instanceKey)
        {
            if (monster == null)
                return false;
            if (string.IsNullOrWhiteSpace(instanceKey))
                return false;
            string normalizedInstanceKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            string monsterKey = RoomRuntime.NormalizeInstanceKey(monster.InstanceKey);
            return string.Equals(monsterKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetManipulatorFixed32(ManipulatorData manipulator, string property, int fallbackF32)
        {
            return TryGetManipulatorFixed32(manipulator, property, out int valueF32) ? valueF32 : fallbackF32;
        }

        private static bool TryGetManipulatorFixed32(ManipulatorData manipulator, string property, out int valueF32)
        {
            valueF32 = 0;
            return manipulator?.properties != null
                && manipulator.properties.TryGetValue(property, out string raw)
                && GCNode.TryParseFixed32(raw, out valueF32);
        }

        public bool IsProjectileHittableMonster(Monster monster, string instanceKey, string zoneName, ProjectileUnitFinderMode mode = ProjectileUnitFinderMode.RepeatEnemies)
        {
            if (!MatchesProjectileUnitFinderBasicLoopChecks(monster, mode))
                return false;
            if (!MatchesInstance(monster, instanceKey))
                return false;
            if (!string.IsNullOrWhiteSpace(zoneName)
                && !string.IsNullOrWhiteSpace(monster.ZoneName)
                && !string.Equals(monster.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        private bool MatchesProjectileUnitFinderBasicLoopChecks(Monster monster, ProjectileUnitFinderMode mode)
        {
            if (!IsProjectileUnitHittableMonster(monster))
                return false;

            int scanFlags = ProjectileUnitFinderFlags(mode);
            if ((scanFlags & UNITFINDER_FLAG_REQUIRE_IS_HITTABLE) != 0 && !MatchesProjectileUnitIsHittable(monster, scanFlags))
                return false;
            if ((scanFlags & UNITFINDER_FLAG_REQUIRE_ENEMY) != 0 && !IsProjectileEnemyMonster(monster))
                return false;
            return true;
        }

        private int ProjectileUnitFinderFlags(ProjectileUnitFinderMode mode)
        {
            return mode == ProjectileUnitFinderMode.FirstTimeHittableUnits
                ? PROJECTILECHECKER_FIND_HITTABLE_UNITS_FLAGS
                : PROJECTILECHECKER_FIND_ENEMIES_FLAGS;
        }

        private static bool ProjectileUnitFinderCollectsResults(int scanFlags)
        {
            return (scanFlags & UNITFINDER_FLAG_PUSH_RESULTS) != 0;
        }

        private bool IsProjectileUnitHittableMonster(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return false;
            if (monster.State == MonsterState.Dead || monster.DeathLifecycleActive)
                return false;
            if (PeekMonsterCurrentHPWire(monster) == 0)
                return false;
            return true;
        }

        private bool MatchesProjectileUnitIsHittable(Monster monster, int scanFlags)
        {
            if (!IsProjectileUnitHittableMonster(monster))
                return false;

            if ((scanFlags & UNITFINDER_FLAG_IS_HITTABLE_FILTER_ENABLED) != 0)
                return IsProjectileEnemyMonster(monster);

            return IsProjectileEnemyMonster(monster);
        }

        private static bool IsProjectileEnemyMonster(Monster monster)
        {
            return monster != null
                && UnitFactionRules.IsEnemyFaction(0, monster.FactionID, false, monster.UnitDescIsAlwaysFriendly);
        }

        private static bool IsMonsterEnemyOfPlayer(Monster monster)
        {
            return monster != null
                && UnitFactionRules.IsEnemyFaction(monster.FactionID, 0, monster.UnitDescIsAlwaysFriendly);
        }

        public List<Monster> GetProjectileHittableMonstersInNativeDistanceOrder(
            int centerFixedX,
            int centerFixedY,
            int centerFixedZ,
            string instanceKey,
            string zoneName,
            int rangeFixed = 0,
            ProjectileUnitFinderMode mode = ProjectileUnitFinderMode.RepeatEnemies,
            uint playerEntityId = 0,
            bool useClientVisiblePosition = false)
        {
            var ranked = new List<(Monster Monster, long DistanceSqFixed8)>();
            long rangeSqFixed8 = rangeFixed > 0 ? (((long)rangeFixed * rangeFixed) >> 8) : long.MaxValue;
            ProjectileUnitFinderMode scanMode = mode;
            int scanFlags = ProjectileUnitFinderFlags(scanMode);
            if (!ProjectileUnitFinderCollectsResults(scanFlags))
                return new List<Monster>(0);
            foreach (var monster in GetAllMonsters())
            {
                if (!IsProjectileHittableMonster(monster, instanceKey, zoneName, scanMode))
                    continue;

                int candidateFixedX = monster.PosFixedX;
                int candidateFixedY = monster.PosFixedY;
                int candidateFixedZ = monster.PosFixedZ;
                if (useClientVisiblePosition
                    && !TryGetMonsterClientVisiblePositionFixed(monster, playerEntityId, out candidateFixedX, out candidateFixedY, out candidateFixedZ))
                    continue;

                long distanceSqFixed8 = ProjectileFinderDistanceSqFixed8(
                    candidateFixedX,
                    candidateFixedY,
                    candidateFixedZ,
                    centerFixedX,
                    centerFixedY,
                    centerFixedZ);
                if (distanceSqFixed8 > rangeSqFixed8)
                    continue;
                ranked.Add((monster, distanceSqFixed8));
            }

            ranked.Sort(CompareProjectileUnitFinderResultsByNativeDistance);

            var result = new List<Monster>(ranked.Count);
            foreach (var item in ranked)
                result.Add(item.Monster);
            return result;
        }

        private static List<KeyValuePair<string, ManipulatorData>> BuildSpawnManipulatorOrder(Dictionary<string, ManipulatorData> manipulators)
        {
            var order = new List<KeyValuePair<string, ManipulatorData>>();
            if (manipulators == null)
                return order;
            for (int skillIndex = 1; manipulators.TryGetValue("skill" + skillIndex, out ManipulatorData skill); skillIndex++)
                order.Add(new KeyValuePair<string, ManipulatorData>("skill" + skillIndex, skill));
            if (manipulators.TryGetValue("primaryweapon", out ManipulatorData primaryWeapon))
                order.Add(new KeyValuePair<string, ManipulatorData>("primaryweapon", primaryWeapon));
            return order;
        }

        private static int CompareProjectileUnitFinderResultsByNativeDistance(
            (Monster Monster, long DistanceSqFixed8) left,
            (Monster Monster, long DistanceSqFixed8) right)
        {
            int distanceCompare = left.DistanceSqFixed8.CompareTo(right.DistanceSqFixed8);
            if (distanceCompare != 0)
                return distanceCompare;

            uint leftTieId = ProjectileFinderTieId(left.Monster);
            uint rightTieId = ProjectileFinderTieId(right.Monster);
            return leftTieId.CompareTo(rightTieId);
        }

        public static long ProjectileFinderDistanceSqFixed8(Monster monster, int centerFixedX, int centerFixedY, int centerFixedZ)
        {
            if (monster == null)
                return long.MaxValue;
            return ProjectileFinderDistanceSqFixed8(
                monster.PosFixedX,
                monster.PosFixedY,
                monster.PosFixedZ,
                centerFixedX,
                centerFixedY,
                centerFixedZ);
        }

        public static long ProjectileFinderDistanceSqFixed8(
            int unitFixedX,
            int unitFixedY,
            int unitFixedZ,
            int centerFixedX,
            int centerFixedY,
            int centerFixedZ)
        {
            long dx = (long)centerFixedX - unitFixedX;
            long dy = (long)centerFixedY - unitFixedY;
            long dz = (long)centerFixedZ - unitFixedZ;
            return ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
        }

        public static uint ProjectileFinderTieId(Monster monster)
        {
            if (monster == null)
                return uint.MaxValue;
            return monster.EntityId != 0 ? monster.EntityId : monster.UnitId;
        }

        public static int ResolveProjectileUnitCollisionRadiusF32(Monster monster)
        {
            return ResolveUnitBehaviorRadius130F32(monster);
        }

        public int ResolveAvatarUnitBehaviorRadiusF32()
        {
            return ResolveAvatarCombatRadiusFixed();
        }

        public static int ResolveUnitBehaviorRadius130F32(Monster monster)
        {
            int unitDescRadiusF32 = ResolveUnitDescCollisionRadiusF32(monster);
            int sizeModPercent = monster != null
                ? Math.Max(0, monster.SizeModPercent + monster.GetActiveAttributeModifierValue("SIZEMOD"))
                : 0;
            return ScaleNativeUnitBehaviorRadiusF32(unitDescRadiusF32, sizeModPercent);
        }

        private static int ResolveUnitDescCollisionRadiusF32(Monster monster)
        {
            if (monster == null)
                return 0;
            if (monster.CollisionRadiusF32 > 0)
                return monster.CollisionRadiusF32;
            return 0;
        }

        private static int ScaleNativeUnitBehaviorRadiusF32(int radiusF32, int unitScalePercent)
        {
            if (radiusF32 <= 0 || unitScalePercent <= 0)
                return 0;
            return (int)(((long)radiusF32 * unitScalePercent) / 100);
        }

        public static bool IsProjectileHitBetterFixed(Monster candidate, int candidateAlongFixed, long candidateDistSqFixed, Monster best, int bestAlongFixed, long bestDistSqFixed)
        {
            if (candidate == null)
                return false;
            if (best == null)
                return true;
            if (candidateAlongFixed < bestAlongFixed)
                return true;
            if (candidateAlongFixed > bestAlongFixed)
                return false;
            if (candidateDistSqFixed < bestDistSqFixed)
                return true;
            if (candidateDistSqFixed > bestDistSqFixed)
                return false;

            uint candidateTieId = ProjectileFinderTieId(candidate);
            uint bestTieId = ProjectileFinderTieId(best);
            return candidateTieId < bestTieId;
        }

        public Monster FindMonsterForTarget(ushort clientTargetId, string instanceKey = null)
        {
            Debug.LogError($"[COMBAT] findMonsterForTarget target={clientTargetId} active={_activeMonsters.Count}");

            var monster = GetMonster(clientTargetId);
            if (monster != null)
            {
                if (!IsMonsterCombatSelectable(monster))
                {
                    Debug.LogError($"[COMBAT] target={clientTargetId} resolved=entityId monster='{monster.Name}' alive={monster.IsAlive} hp={monster.CurrentHPWire / 256}/{monster.MaxHPWire / 256}");
                    return null;
                }
                Debug.LogError($"[COMBAT] target={clientTargetId} resolved=entityId");
                return monster;
            }

            if (_componentToEntityMap.TryGetValue(clientTargetId, out uint entityId))
            {
                monster = GetMonster(entityId);
                if (monster != null)
                {
                    if (!IsMonsterCombatSelectable(monster))
                    {
                        Debug.LogError($"[COMBAT] targetComponent={clientTargetId} monster='{monster.Name}' alive={monster.IsAlive} hp={monster.CurrentHPWire / 256}/{monster.MaxHPWire / 256}");
                        return null;
                    }
                    Debug.LogError($"[COMBAT] target={clientTargetId} resolved=componentId entityId={entityId}");
                    return monster;
                }
            }

            Debug.LogError($"[COMBAT] target={clientTargetId} state=notFound action=reject reason=unknown-entity-or-component instance='{instanceKey ?? ""}'");
            return null;
        }

        public DamageResult ApplyDamage(uint attackerId, uint defenderId, int damageAmount)
        {
            var monster = GetMonster(defenderId);
            uint damageWire = (uint)Math.Max(1, damageAmount) * 256u;
            bool applied = ApplyPlayerDamageToMonsterWire(monster, damageWire, "ApplyDamage", out uint oldHPWire, out uint newHPWire, out bool died, sourceEntityId: attackerId);
            if (!applied)
                return new DamageResult { Success = false };
            int appliedDamage = (int)(((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0u) + 255u) / 256u);

            return new DamageResult
            {
                Success = true,
                DamageDealt = appliedDamage,
                IsCritical = false,
                DefenderDied = died,
                NewHPWire = newHPWire
            };
        }

        private int ResolveMonsterEffectiveAttackRangeFixed(Monster monster)
        {
            if (monster == null) return 0;
            TryGetCombatTarget(monster.TargetId, out CombatTarget target);
            return SaturatingAddFixed8(
                ResolveMonsterUnitBehaviorRange138F32(monster),
                ResolveTargetCombatRadiusFixed(target));
        }

        private static int ResolveMonsterUnitBehaviorRange138F32(Monster monster)
        {
            if (monster == null) return 0;
            int extensionF32 = monster.Manipulators != null && monster.Manipulators.ContainsKey("primaryweapon")
                ? Math.Max(0, monster.WeaponRangeF32)
                : Math.Max(0, monster.UnitDescAttackRangeF32);
            return SaturatingAddFixed8(ResolveUnitBehaviorRadius130F32(monster), extensionF32);
        }

        private static int ResolvePositiveFixed8(int fixed8)
        {
            if (fixed8 > 0) return fixed8;
            return 0;
        }

        private static int SaturatingAddFixed8(int left, int right)
        {
            long sum = (long)Math.Max(0, left) + Math.Max(0, right);
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        private static int AbsFixed8(int value)
        {
            return value == int.MinValue ? int.MaxValue : Math.Abs(value);
        }

        public int GetMonsterEffectiveAttackRangeF32(Monster monster)
        {
            return ResolveMonsterEffectiveAttackRangeFixed(monster);
        }

        public int ResolvePlayerMeleeRangeF32(PlayerState state, Monster monster)
        {
            int weaponRangeF32 = state != null && state.WeaponRangeF32 > 0
                ? state.WeaponRangeF32
                : monster != null ? Math.Max(0, monster.AttackRangeF32) : 0;
            int monsterRadiusF32 = ResolveUnitBehaviorRadius130F32(monster);
            return SaturatingAddFixed8(SaturatingAddFixed8(Math.Max(0x100, weaponRangeF32), ResolveAvatarCombatRadiusFixed()), monsterRadiusF32);
        }

        public int ResolvePlayerWeaponTargetClearRangeF32(PlayerState state, Monster monster)
        {
            int weaponRangeF32 = state != null && state.WeaponRangeF32 > 0
                ? state.WeaponRangeF32
                : monster != null ? Math.Max(0, monster.AttackRangeF32) : 0;
            int actorRangeF32 = SaturatingAddFixed8(Math.Max(0x100, weaponRangeF32), ResolveAvatarCombatRadiusFixed());
            int monsterRadiusF32 = ResolveUnitBehaviorRadius130F32(monster);
            return SaturatingAddFixed8(actorRangeF32, monsterRadiusF32);
        }

        public int ResolvePlayerPvpWeaponTargetClearRangeF32(PlayerState state)
        {
            int weaponRangeF32 = state != null && state.WeaponRangeF32 > 0
                ? state.WeaponRangeF32
                : 0x100;
            int avatarRadiusF32 = ResolveAvatarCombatRadiusFixed();
            return SaturatingAddFixed8(
                SaturatingAddFixed8(Math.Max(0x100, weaponRangeF32), avatarRadiusF32),
                avatarRadiusF32);
        }

        public int ResolvePlayerSkillTargetClearRangeF32(SpellData spell, Monster monster)
        {
            int skillRangeF32 = Math.Max(0, spell?.Range ?? 0) * UnitMover.Fixed;
            int actorRadiusF32 = ResolveAvatarBoundingBoxRadiusXYF32();
            int targetRadiusF32 = Math.Max(0, monster?.BoundingBoxRadiusXYF32 ?? 0);
            int scaledActorRadiusF32 = (int)(((long)actorRadiusF32 * 0x180) >> 8);
            int scaledTargetRadiusF32 = (int)(((long)targetRadiusF32 * 0x180) >> 8);
            return SaturatingAddFixed8(
                skillRangeF32,
                SaturatingAddFixed8(scaledActorRadiusF32, scaledTargetRadiusF32));
        }

        public int ResolvePlayerBlingGnomeSkillGeometry(SpellData spell,
            int actorX, int actorY, int actorZ, int targetX, int targetY, int targetZ,
            out int ax, out int ay, out int az, out int tx, out int ty, out int tz)
        {
            const string type = "creatures.summon.blinggnome.base.BlingGnome_Summon";
            ResolveWorldEntityBoundingBoxF32(GCDatabase.Instance.ResolveWithInheritance(type), type,
                out int targetRadius, out int targetMinZ, out int targetMaxZ);
            ResolveAvatarBoundingBoxF32(out int actorRadius, out int actorMinZ, out int actorMaxZ);
            ASGetPositionFixed(actorX, actorY, actorZ, actorMinZ, actorMaxZ, out ax, out ay, out az);
            ASGetPositionFixed(targetX, targetY, targetZ, targetMinZ, targetMaxZ, out tx, out ty, out tz);
            return SaturatingAddFixed8(Math.Max(0, spell?.Range ?? 0) * UnitMover.Fixed,
                SaturatingAddFixed8((int)(((long)actorRadius * 0x180) >> 8), (int)(((long)targetRadius * 0x180) >> 8)));
        }

        public int ResolvePlayerPvpSkillTargetClearRangeF32(SpellData spell)
        {
            int skillRangeF32 = Math.Max(0, spell?.Range ?? 0) * UnitMover.Fixed;
            int avatarRadiusF32 = ResolveAvatarBoundingBoxRadiusXYF32();
            int scaledAvatarRadiusF32 = (int)(((long)avatarRadiusF32 * 0x180) >> 8);
            return SaturatingAddFixed8(
                skillRangeF32,
                SaturatingAddFixed8(scaledAvatarRadiusF32, scaledAvatarRadiusF32));
        }

        private int ResolveMonsterSkillTargetClearRangeF32(Monster monster, MonsterActiveSkillRuntime skill, CombatTarget target = null)
        {
            int skillRangeF32 = Math.Max(0, skill?.RangeF32 ?? 0);
            if (skillRangeF32 == 0)
                skillRangeF32 = ResolveMonsterEffectiveAttackRangeFixed(monster);
            int actorRadiusF32 = Math.Max(0, monster?.BoundingBoxRadiusXYF32 ?? 0);
            int targetRadiusF32 = target?.Monster?.BoundingBoxRadiusXYF32 ?? ResolveAvatarBoundingBoxRadiusXYF32();
            int scaledActorRadiusF32 = (int)(((long)actorRadiusF32 * 0x180) >> 8);
            int scaledTargetRadiusF32 = (int)(((long)targetRadiusF32 * 0x180) >> 8);
            return SaturatingAddFixed8(
                skillRangeF32,
                SaturatingAddFixed8(scaledActorRadiusF32, scaledTargetRadiusF32));
        }

        private bool EvaluateMonsterActiveSkillTargetClearFixed(
            Monster monster,
            CombatTarget target,
            int rangeF32)
        {
            if (monster == null || target == null || rangeF32 <= 0)
                return false;
            TryGetMonsterClientVisiblePositionFixed(
                monster,
                target.EntityId,
                out int monsterFixedX,
                out int monsterFixedY,
                out int monsterFixedZ);
            ResolveMonsterActionTargetPositionFixed(
                target,
                out int targetFixedX,
                out int targetFixedY,
                out int targetFixedZ);
            ResolveAvatarBoundingBoxF32(out _, out int targetMinZF32, out int targetMaxZF32);
            if (target.Monster != null)
            {
                targetMinZF32 = target.Monster.BoundingBoxMinZF32;
                targetMaxZF32 = target.Monster.BoundingBoxMaxZF32;
            }
            ASGetPositionFixed(
                monsterFixedX,
                monsterFixedY,
                monsterFixedZ,
                monster.BoundingBoxMinZF32,
                monster.BoundingBoxMaxZF32,
                out int evaluatedMonsterFixedX,
                out int evaluatedMonsterFixedY,
                out int evaluatedMonsterFixedZ);
            ASGetPositionFixed(
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                targetMinZF32,
                targetMaxZF32,
                out int evaluatedTargetFixedX,
                out int evaluatedTargetFixedY,
                out int evaluatedTargetFixedZ);
            EvaluateUseTargetInitUseFixed3D(
                evaluatedMonsterFixedX,
                evaluatedMonsterFixedY,
                evaluatedMonsterFixedZ,
                evaluatedTargetFixedX,
                evaluatedTargetFixedY,
                evaluatedTargetFixedZ,
                rangeF32,
                0,
                out _,
                out long distanceSqFixed8,
                out long thresholdSqFixed8);
            if (thresholdSqFixed8 <= 0 || distanceSqFixed8 >= thresholdSqFixed8)
                return false;
            string instanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
            return !WorldCollision.Instance.TrySegmentHitFixed(
                monster.ZoneName,
                instanceKey,
                evaluatedMonsterFixedX,
                evaluatedMonsterFixedY,
                evaluatedMonsterFixedZ,
                evaluatedTargetFixedX,
                evaluatedTargetFixedY,
                evaluatedTargetFixedZ,
                UnitMover.Fixed,
                out _);
        }

        public void ResolvePlayerActiveSkillTargetPositionsFixed(
            int actorFixedX,
            int actorFixedY,
            int actorFixedZ,
            Monster target,
            int targetFixedX,
            int targetFixedY,
            int targetFixedZ,
            out int evaluatedActorFixedX,
            out int evaluatedActorFixedY,
            out int evaluatedActorFixedZ,
            out int evaluatedTargetFixedX,
            out int evaluatedTargetFixedY,
            out int evaluatedTargetFixedZ)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            ResolveAvatarBoundingBoxF32(out _, out int actorMinZF32, out int actorMaxZF32);
            ASGetPositionFixed(
                actorFixedX,
                actorFixedY,
                actorFixedZ,
                actorMinZF32,
                actorMaxZF32,
                out evaluatedActorFixedX,
                out evaluatedActorFixedY,
                out evaluatedActorFixedZ);
            ASGetPositionFixed(
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                target.BoundingBoxMinZF32,
                target.BoundingBoxMaxZF32,
                out evaluatedTargetFixedX,
                out evaluatedTargetFixedY,
                out evaluatedTargetFixedZ);
        }

        public int ResolvePlayerRangedProjectileRangeF32(PlayerState state, Monster monster)
        {
            int rangeF32 = ResolvePlayerMeleeRangeF32(state, monster);
            if (state == null || !DamageResolver.IsProjectileWeapon(state))
                return rangeF32;

            int projectileSizeF32 = Math.Max(0, state.WeaponProjectileSizeF32);
            int firstTickTravelF32 = state.WeaponProjectileSpeedF32 > 0
                ? WeaponUseRuntime.ProjectileStepDistanceF32(state.WeaponProjectileSpeedF32)
                : 0;
            return SaturatingAddFixed8(SaturatingAddFixed8(rangeF32, projectileSizeF32), firstTickTravelF32);
        }

        public int ResolveUseTargetInitUseRangeF32(PlayerState state, Monster monster)
        {
            return state != null && state.WeaponInitUseRangeF32 > 0
                ? state.WeaponInitUseRangeF32
                : 64000;
        }

        public int ResolveUseTargetClientSyncToleranceF32(PlayerState state)
        {
            return state != null ? Math.Max(0, state.WeaponClientSyncToleranceF32) : 0;
        }

        public bool EvaluateUseTargetInitUseFixed(int actorFixedX, int actorFixedY, int targetFixedX, int targetFixedY,
            int initUseRangeFixed, int clientToleranceFixed, out int distanceFixed, out long distanceSqFixed8, out long thresholdSqFixed8)
        {
            int dxFixed = targetFixedX - actorFixedX;
            int dyFixed = targetFixedY - actorFixedY;
            distanceFixed = UnitMover.IntSqrt((long)dxFixed * dxFixed + (long)dyFixed * dyFixed);
            distanceSqFixed8 = (((long)dxFixed * dxFixed) >> 8) + (((long)dyFixed * dyFixed) >> 8);

            int rangeFixed = Math.Max(0, initUseRangeFixed + clientToleranceFixed);
            thresholdSqFixed8 = ((long)rangeFixed * rangeFixed) >> 8;
            return thresholdSqFixed8 > 0 && distanceSqFixed8 <= thresholdSqFixed8;
        }

        public bool EvaluateUseTargetInitUseFixed3D(int actorFixedX, int actorFixedY, int actorFixedZ,
            int targetFixedX, int targetFixedY, int targetFixedZ, int initUseRangeFixed, int clientToleranceFixed,
            out int distanceFixed, out long distanceSqFixed8, out long thresholdSqFixed8)
        {
            int dxFixed = targetFixedX - actorFixedX;
            int dyFixed = targetFixedY - actorFixedY;
            int dzFixed = targetFixedZ - actorFixedZ;
            long distanceSqFixed16 = (long)dxFixed * dxFixed + (long)dyFixed * dyFixed + (long)dzFixed * dzFixed;
            distanceFixed = UnitMover.IntSqrt(distanceSqFixed16);
            distanceSqFixed8 = (((long)dxFixed * dxFixed) >> 8)
                + (((long)dyFixed * dyFixed) >> 8)
                + (((long)dzFixed * dzFixed) >> 8);

            int rangeFixed = SaturatingAddFixed8(Math.Max(0, initUseRangeFixed), Math.Max(0, clientToleranceFixed));
            thresholdSqFixed8 = ((long)rangeFixed * rangeFixed) >> 8;
            return thresholdSqFixed8 > 0 && distanceSqFixed8 <= thresholdSqFixed8;
        }

        private int ResolveClientContactRangeFixed(Monster monster, CombatTarget player, int allowedRangeFixed)
        {
            if (monster == null || player == null) return 0;
            return Math.Max(0, allowedRangeFixed);
        }

        private int ResolveMonsterManipulatorUseRangeFixed(Monster monster)
        {
            if (monster == null) return 0;
            int rangeFixed = monster.WeaponRangeF32 > 0
                ? monster.WeaponRangeF32
                : ResolvePositiveFixed8(monster.AttackRangeF32);
            if (rangeFixed <= 0) return 0;
            int toleranceFixed = Math.Max(0, ResolvePositiveFixed8(monster.ClientSyncToleranceF32));
            return SaturatingAddFixed8(rangeFixed, toleranceFixed);
        }

        private bool TryGetMonsterClientVisibleDistanceFixed(Monster monster, CombatTarget target, out int distanceFixed, out long distanceSqFixed8)
        {
            distanceFixed = 0;
            distanceSqFixed8 = 0;
            if (monster == null || target == null) return false;
            int monsterFixedX = monster.PosFixedX;
            int monsterFixedY = monster.PosFixedY;
            if (TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out int visibleFixedX, out int visibleFixedY))
            {
                monsterFixedX = visibleFixedX;
                monsterFixedY = visibleFixedY;
            }
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            int dxFixed = targetFixedX - monsterFixedX;
            int dyFixed = targetFixedY - monsterFixedY;
            distanceFixed = UnitMover.IntSqrt((long)dxFixed * dxFixed + (long)dyFixed * dyFixed);
            distanceSqFixed8 = (((long)dxFixed * dxFixed) >> 8) + (((long)dyFixed * dyFixed) >> 8);
            return true;
        }

        private static string ResolveMonsterPathMapKey(Monster monster)
        {
            if (!string.IsNullOrWhiteSpace(monster?.InstanceKey))
                return monster.InstanceKey;
            return monster?.ZoneName;
        }

        private static void ResolveMonsterMovementFixed(
            Monster monster,
            PathMap pathMap,
            int curFixedX,
            int curFixedY,
            int curFixedZ,
            int candFixedX,
            int candFixedY,
            out int outFixedX,
            out int outFixedY,
            out int outFixedZ,
            uint simulationTick = 0)
        {
            UnitMover.ResolveMovement(pathMap, curFixedX, curFixedY, curFixedZ, candFixedX, candFixedY, out outFixedX, out outFixedY, out outFixedZ);
            if ((outFixedX != candFixedX || outFixedY != candFixedY) && (candFixedX != curFixedX || candFixedY != curFixedY))
            {
                string result = outFixedX == curFixedX && outFixedY == curFixedY ? "blocked" : "slide";
                Debug.LogError($"[MON-MOVEMENT-RESOLVE] entity={monster?.EntityId ?? 0} tick={simulationTick} startFixed8=({curFixedX},{curFixedY},{curFixedZ}) candidateFixed8=({candFixedX},{candFixedY}) resultFixed8=({outFixedX},{outFixedY},{outFixedZ}) result={result} pathKey='{ResolveMonsterPathMapKey(monster) ?? string.Empty}' sourceFunction=UnitMover::ResolveMovement@0x00536870");
            }
        }

        private bool IsMonsterAttackPathClear(Monster monster, CombatTarget target, string source)
        {
            if (monster == null || target == null) return false;
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            if (string.IsNullOrWhiteSpace(pathMapKey))
            {
                if (!string.IsNullOrEmpty(source))
                    Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} pathCoverage=False source={source} pathKey='' reason=missing-path-key action=blocked");
                return false;
            }
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(pathMapKey);
            if (pathMap == null)
            {
                if (!string.IsNullOrEmpty(source))
                    Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} pathCoverage=False source={source} pathKey='{pathMapKey}' reason=missing-pathmap action=blocked");
                return false;
            }
            int monsterFixedX = monster.PosFixedX;
            int monsterFixedY = monster.PosFixedY;
            if (TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out int visibleFixedX, out int visibleFixedY))
            {
                monsterFixedX = visibleFixedX;
                monsterFixedY = visibleFixedY;
            }
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            PathReachability reachability = pathMap.GetReachabilityFixed(monsterFixedX, monsterFixedY, targetFixedX, targetFixedY);
            if (reachability == PathReachability.CoverageMissing)
            {
                if (!string.IsNullOrEmpty(source))
                    Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} pathCoverage=False source={source} pathKey='{pathMapKey}' path=({FormatFixed8Diagnostic(monsterFixedX)},{FormatFixed8Diagnostic(monsterFixedY)})->({FormatFixed8Diagnostic(targetFixedX)},{FormatFixed8Diagnostic(targetFixedY)}) action=blocked");
                return false;
            }
            if (reachability == PathReachability.Blocked && !string.IsNullOrEmpty(source))
                Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} worldBlocked=True source={source} pathKey='{pathMapKey}' path=({FormatFixed8Diagnostic(monsterFixedX)},{FormatFixed8Diagnostic(monsterFixedY)})->({FormatFixed8Diagnostic(targetFixedX)},{FormatFixed8Diagnostic(targetFixedY)})");
            if (reachability != PathReachability.Reachable)
                return false;

            int monsterFixedZ = monster.PosFixedZ;
            TryResolveTerrainHeightFixed(monster, monsterFixedX, monsterFixedY, monsterFixedZ, out monsterFixedZ, out _);
            int targetFixedZ = pathMap.GetHeightAtFixed(targetFixedX, targetFixedY, monsterFixedZ);
            string zoneName = monster.ZoneName;
            string instanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
            bool staticBlocked = WorldCollision.Instance.TrySegmentHitFixed(
                zoneName,
                instanceKey,
                monsterFixedX,
                monsterFixedY,
                monsterFixedZ,
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                0,
                out WorldCollisionHit hit);
            if (staticBlocked)
            {
                if (!string.IsNullOrEmpty(source))
                    Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} staticWorldBlocked=True source={source} pathKey='{pathMapKey}' path=({FormatFixed8Diagnostic(monsterFixedX)},{FormatFixed8Diagnostic(monsterFixedY)},{FormatFixed8Diagnostic(monsterFixedZ)})->({FormatFixed8Diagnostic(targetFixedX)},{FormatFixed8Diagnostic(targetFixedY)},{FormatFixed8Diagnostic(targetFixedZ)}) hit='{hit?.ObjectPath ?? ""}' action=blocked");
                return false;
            }
            return true;
        }

        private void ClearMonsterCombatContact(Monster monster, CombatTarget target)
        {
            if (monster == null || target == null) return;
            if (monster.CombatContactTargetId != target.EntityId) return;
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntilTick = 0;
        }

        private void RefreshMonsterCombatContact(Monster monster, CombatTarget target)
        {
            if (monster == null || target == null) return;
            int windupTicks = ResolveMonsterAttackWindupTicks(monster);
            if (windupTicks <= 0)
            {
                ClearMonsterCombatContact(monster, target);
                return;
            }
            int durationTicks = Math.Max(8, windupTicks + 8);
            monster.CombatContactTargetId = target.EntityId;
            monster.CombatContactUntilTick = _combatTick + (uint)durationTicks;
        }

        private bool IsClientCombatContactFixed(Monster monster, CombatTarget target, int allowedRangeFixed)
        {
            if (!IsMonsterAttackPathClear(monster, target, null))
            {
                ClearMonsterCombatContact(monster, target);
                return false;
            }
            if (HasCombatContact(monster, target)) return true;
            if (monster == null || target == null) return false;
            int contactRangeFixed = ResolveClientContactRangeFixed(monster, target, allowedRangeFixed);
            if (contactRangeFixed <= 0) return false;
            if (!TryGetMonsterClientVisibleDistanceFixed(monster, target, out _, out long distanceSqFixed8))
                return false;
            int thresholdFixed = SaturatingAddFixed8(contactRangeFixed, CLIENT_CONTACT_RANGE_EPSILON_FIXED);
            long thresholdSqFixed8 = ((long)thresholdFixed * thresholdFixed) >> 8;
            return thresholdSqFixed8 > 0 && distanceSqFixed8 <= thresholdSqFixed8;
        }

        private bool IsMonsterActionTargetClearFixed(Monster monster, CombatTarget target, int allowedRangeFixed)
        {
            if (target == null)
                return false;
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            return EvaluateMonsterActionTargetClearFixed(monster, target, allowedRangeFixed, targetFixedX, targetFixedY);
        }

        private bool EvaluateMonsterActionTargetClearFixed(Monster monster, CombatTarget target, int allowedRangeFixed, int targetFixedX, int targetFixedY)
        {
            if (monster == null || target == null || allowedRangeFixed <= 0)
                return false;
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            if (string.IsNullOrWhiteSpace(pathMapKey))
                return false;
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(pathMapKey);
            if (pathMap == null)
                return false;
            int monsterFixedX = monster.PosFixedX;
            int monsterFixedY = monster.PosFixedY;
            if (TryPeekMonsterClientVisiblePositionFixed(monster, target.EntityId, out int visibleFixedX, out int visibleFixedY, out _))
            {
                monsterFixedX = visibleFixedX;
                monsterFixedY = visibleFixedY;
            }
            bool blocked = pathMap.CastGroundRayFixed(
                monsterFixedX,
                monsterFixedY,
                monster.PosFixedZ,
                targetFixedX,
                targetFixedY,
                out int rayEndFixedX,
                out int rayEndFixedY,
                out _);
            if (blocked)
                return false;
            int contactRangeFixed = ResolveClientContactRangeFixed(monster, target, allowedRangeFixed);
            if (contactRangeFixed <= 0)
                return false;
            long dxFixed = (long)rayEndFixedX - monsterFixedX;
            long dyFixed = (long)rayEndFixedY - monsterFixedY;
            long distanceSqFixed8 = ((dxFixed * dxFixed) >> 8) + ((dyFixed * dyFixed) >> 8);
            int thresholdFixed = contactRangeFixed;
            long thresholdSqFixed8 = ((long)thresholdFixed * thresholdFixed) >> 8;
            return thresholdSqFixed8 > 0 && distanceSqFixed8 <= thresholdSqFixed8;
        }

        private bool HasMonsterTargetAction(Monster monster, CombatTarget target, int distFixed)
        {
            if (monster == null || target == null) return false;
            if (!monster.AggroTriggered || monster.TargetId != target.EntityId) return false;
            int targetRangeFixed = ResolveMonsterTargetSearchRangeFixed(monster);
            return targetRangeFixed <= 0 || distFixed <= SaturatingAddFixed8(targetRangeFixed, CLIENT_CONTACT_RANGE_EPSILON_FIXED);
        }

        private bool HasMonsterWeaponRuntimeReachFixed(Monster monster, CombatTarget target, int allowedRangeFixed, bool clientContact)
        {
            if (monster == null || target == null) return false;
            int runtimeRangeFixed = ResolveMonsterManipulatorUseRangeFixed(monster);
            if (runtimeRangeFixed <= 0)
                runtimeRangeFixed = Math.Max(0, allowedRangeFixed);
            if (runtimeRangeFixed <= 0) return false;
            if (!TryGetMonsterClientVisibleDistanceFixed(monster, target, out _, out long distanceSqFixed8))
                return false;
            int thresholdFixed = SaturatingAddFixed8(runtimeRangeFixed, CLIENT_CONTACT_RANGE_EPSILON_FIXED);
            long thresholdSqFixed8 = ((long)thresholdFixed * thresholdFixed) >> 8;
            return thresholdSqFixed8 > 0 && distanceSqFixed8 <= thresholdSqFixed8;
        }

        private bool HasMonsterInitUseGateway(Monster monster, CombatTarget target)
        {
            if (monster == null || target == null) return false;
            int monsterFixedX = monster.PosFixedX;
            int monsterFixedY = monster.PosFixedY;
            TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out monsterFixedX, out monsterFixedY);
            int initUseRangeFixed = ResolveMonsterManipulatorUseRangeFixed(monster);
            if (initUseRangeFixed <= 0)
                return false;
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            return EvaluateUseTargetInitUseFixed(monsterFixedX, monsterFixedY, targetFixedX, targetFixedY,
                initUseRangeFixed, 0, out _, out _, out _);
        }

        private int ResolveAvatarCombatRadiusFixed()
        {
            if (_avatarCombatRadiusFixed.HasValue) return _avatarCombatRadiusFixed.Value;
            int radiusFixed = 5 * UnitMover.Fixed;
            var avatar = GCDatabase.Instance?.ResolveWithInheritance("avatar.base.avatar");
            var desc = avatar?.GetChild("Description") ?? avatar;
            radiusFixed = GetAuthoredFixed32(desc, "CollisionRadius", radiusFixed);
            _avatarCombatRadiusFixed = Math.Max(0, radiusFixed);
            return _avatarCombatRadiusFixed.Value;
        }

        private int ResolveAvatarBoundingBoxRadiusXYF32()
        {
            ResolveAvatarBoundingBoxF32(out int radiusXYF32, out _, out _);
            return radiusXYF32;
        }

        private void ResolveAvatarBoundingBoxF32(out int radiusXYF32, out int minZF32, out int maxZF32)
        {
            if (!_avatarBoundingBoxRadiusXYF32.HasValue
                || !_avatarBoundingBoxMinZF32.HasValue
                || !_avatarBoundingBoxMaxZF32.HasValue)
            {
                GCNode avatar = GCDatabase.Instance?.ResolveWithInheritance("avatar.base.avatar");
                ResolveWorldEntityBoundingBoxF32(
                    avatar,
                    "avatar.base.avatar",
                    out int resolvedRadiusXYF32,
                    out int resolvedMinZF32,
                    out int resolvedMaxZF32);
                _avatarBoundingBoxRadiusXYF32 = resolvedRadiusXYF32;
                _avatarBoundingBoxMinZF32 = resolvedMinZF32;
                _avatarBoundingBoxMaxZF32 = resolvedMaxZF32;
            }
            radiusXYF32 = _avatarBoundingBoxRadiusXYF32.Value;
            minZF32 = _avatarBoundingBoxMinZF32.Value;
            maxZF32 = _avatarBoundingBoxMaxZF32.Value;
        }

        private static void ResolveWorldEntityBoundingBoxF32(
            GCNode worldEntity,
            string gcType,
            out int radiusXYF32,
            out int minZF32,
            out int maxZF32)
        {
            GCNode description = worldEntity?.GetChild("Object")?.GetChild("Description");
            if (description == null
                || !description.HasProperty("MinX")
                || !description.HasProperty("MinY")
                || !description.HasProperty("MinZ")
                || !description.HasProperty("MaxX")
                || !description.HasProperty("MaxY")
                || !description.HasProperty("MaxZ"))
                throw new InvalidDataException($"WorldEntity '{gcType ?? ""}' missing Object.Description bounding box");

            int minX = description.GetFixed32("MinX", 0);
            int minY = description.GetFixed32("MinY", 0);
            minZF32 = description.GetFixed32("MinZ", 0);
            int maxX = description.GetFixed32("MaxX", 0);
            int maxY = description.GetFixed32("MaxY", 0);
            maxZF32 = description.GetFixed32("MaxZ", 0);
            long extentX = (long)maxX - minX;
            long extentY = (long)maxY - minY;
            long extentZ = (long)maxZF32 - minZF32;
            if (extentX < 0
                || extentY < 0
                || extentZ < 0
                || extentX > int.MaxValue
                || extentY > int.MaxValue)
                throw new InvalidDataException($"WorldEntity '{gcType ?? ""}' has invalid Object.Description bounding box");

            radiusXYF32 = Math.Max((int)extentX, (int)extentY) / 2;
        }

        private static void ASGetPositionFixed(
            int positionFixedX,
            int positionFixedY,
            int positionFixedZ,
            int boundingBoxMinZF32,
            int boundingBoxMaxZF32,
            out int resultFixedX,
            out int resultFixedY,
            out int resultFixedZ)
        {
            long extentZF32 = (long)boundingBoxMaxZF32 - boundingBoxMinZF32;
            if (extentZF32 < 0)
                throw new InvalidDataException("WorldEntity has invalid Object.Description Z bounds");
            long scaledExtentZF32 = (extentZF32 * 0x200L) >> 8;
            long offsetZF32 = (scaledExtentZF32 << 8) / 0x300L;
            long positionZF32 = (long)positionFixedZ + boundingBoxMinZF32 + offsetZF32;
            if (positionZF32 < int.MinValue || positionZF32 > int.MaxValue)
                throw new OverflowException("ActiveSkill position exceeds fixed8 range");
            resultFixedX = positionFixedX & ~0xFF;
            resultFixedY = positionFixedY & ~0xFF;
            resultFixedZ = (int)positionZF32 & ~0xFF;
        }

        private int ResolveMonsterAttackWindupTicks(Monster monster)
        {
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            return hitFrame > 0 ? ResolveMonsterAttackFrameTicks(monster, hitFrame) : 0;
        }

        private int ResolveMonsterAttackSoundDelayTicks(Monster monster, int windupTicks)
        {
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            if (soundFrame <= 0 || windupTicks <= 0)
                return 0;
            int soundTicks = ResolveMonsterAttackFrameTicks(monster, soundFrame);
            return Mathf.Clamp(soundTicks, 0, windupTicks);
        }

        private void ResolveMonsterAttackAnimationFrames(Monster monster, out int totalFrames, out int hitFrame, out int soundFrame)
        {
            int attackIndex = monster != null ? Mathf.Clamp(monster.AttackAnimationIndex, 0, 2) : 0;
            totalFrames = monster?.AttackTotalFrames != null && monster.AttackTotalFrames.Length > attackIndex ? monster.AttackTotalFrames[attackIndex] : 0;
            hitFrame = monster?.AttackHitFrames != null && monster.AttackHitFrames.Length > attackIndex ? monster.AttackHitFrames[attackIndex] : 0;
            soundFrame = monster?.AttackSoundFrames != null && monster.AttackSoundFrames.Length > attackIndex ? monster.AttackSoundFrames[attackIndex] : 0;
            bool resolved = monster?.AttackFrameResolved != null &&
                monster.AttackFrameResolved.Length > attackIndex &&
                monster.AttackFrameResolved[attackIndex] &&
                totalFrames > 0 && hitFrame > 0 && soundFrame > 0;
            if (!resolved)
            {
                if (monster != null && !monster.WeaponUsesProjectile && !monster.UsePrimaryActiveSkillThisAttack)
                {
                    totalFrames = 30;
                    hitFrame = 15;
                    soundFrame = 10;
                    Debug.LogError($"[ATTACK-TIMING] monster={monster.Name} gc={monster.SpawnGCType ?? monster.GCType ?? "<null>"} variant={attackIndex} sourceKind={ResolutionSource.CompatibilityFallback} reason=native-default-basic-melee-frames sourceFunction=Weapon::update@0x00591980");
                    return;
                }
                totalFrames = 0;
                hitFrame = 0;
                soundFrame = 0;
                Debug.LogError($"[ATTACK-TIMING] monster={monster?.Name ?? "<null>"} gc={monster?.SpawnGCType ?? monster?.GCType ?? "<null>"} variant={attackIndex} sourceKind={ResolutionSource.Blocked} reason={monster?.AttackTimingReason ?? "unresolved"} sourceFunction=Weapon::computeAttackTicks@0x00598E50");
                return;
            }
        }

        private int ResolveMonsterAttackFrameTicks(Monster monster, int frame)
        {
            if (frame <= 0)
                return 0;
            int authoredSpeedF32 = GCDatabase.Instance.GetRequiredKnobFixed32("MonsterAttackSpeed");
            int multiplierF32 = monster != null && monster.AttackSpeedF32 > 0
                ? monster.AttackSpeedF32
                : 0x100;
            int attackSpeedF32 = (int)Math.Min(int.MaxValue, ((long)authoredSpeedF32 * Math.Max(1, multiplierF32)) >> 8);
            int speedField = Math.Max(1, GCDatabase.RoundFixed32ToInt(attackSpeedF32)
                + (monster?.GetActiveAttributeModifierValue("ATTACK_SPEED_MOD") ?? 0));
            return (frame * 100) / speedField;
        }

        private static int Fixed32SecondsToClientTicks(int secondsF32)
        {
            if (secondsF32 <= 0)
                return 0;
            long scaledTicksF32 = (long)secondsF32 * CLIENT_TICKS_PER_SECOND;
            long ticks = scaledTicksF32 >> 8;
            if (ticks <= 0) return 0;
            return ticks > ushort.MaxValue ? ushort.MaxValue : (int)ticks;
        }

        private int ResolveMonsterAttackTotalTicks(Monster monster)
        {
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            return totalFrames > 0 ? ResolveMonsterAttackFrameTicks(monster, totalFrames) : 0;
        }

        private int ResolveMonsterAttackCooldownTicks(Monster monster)
        {
            int cooldownF32 = monster != null ? Math.Max(0, monster.AttackCooldownF32) : 0;
            return Fixed32SecondsToClientTicks(cooldownF32);
        }

        private void AdvanceMonsterAttackAnimation(Monster monster)
        {
            if (monster == null) return;
            if (monster.WeaponUsesProjectile && !monster.UsePrimaryActiveSkillThisAttack)
            {
                monster.AttackAnimationIndex = 0;
                monster.AttackUseRaw = 0;
                return;
            }
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (rng == null) return;
            uint useRaw = RngLedger.Generate(rng, "room", "monster-attack:use-animation", monster.InstanceKey ?? monster.ZoneName, monster.EntityId);
            uint previous = monster.AttackAnimationIndex;
            monster.AttackUseRaw = useRaw;
            monster.AttackAnimationIndex = (byte)(((useRaw & 1u) + previous + 1u) % 3u);
        }

        private void ConsumeMonsterAttackSoundRng(Monster monster)
        {
            if (monster == null || !monster.AttackSoundPending) return;
            monster.AttackSoundPending = false;
            uint soundRaw = RandomStreams.GenerateGlobalSound("monster-attack:sound", $"{monster.Name}#{monster.EntityId}");
            monster.AttackSoundRaw = soundRaw;
            monster.AttackSoundGateRaw = soundRaw;
            monster.AttackSoundRepeatRaw = (soundRaw & 3u) == 0 ? soundRaw : 0;
            var rng = GetRoomRngForMonster(monster);
            string roomRngPos = rng != null ? rng.CallsSinceReseed.ToString() : "n/a";
            Debug.LogError($"[MON-ATTACK] {monster.Name} sound clientGlobalSoundRng=True raw=0x{soundRaw:X8} repeat={(monster.AttackSoundRepeatRaw != 0)} weaponSounds={monster.AttackWeaponSoundCount} attackSounds={monster.AttackRepeatSoundCount} globalSoundRngPos={RandomStreams.GlobalSoundCalls} roomRngPos={roomRngPos}");
        }

        public uint ConsumeOnApplyDamageEffectRng(MersenneTwister rng, string actor, uint targetId, string targetName, uint oldHPWire, uint newHPWire, uint targetMaxHPWire, uint damageWire, string source, bool physicalWeaponHit = true)
        {
            if (rng == null || !physicalWeaponHit || damageWire == 0 || oldHPWire == 0 ||
                newHPWire == 0 || newHPWire >= oldHPWire)
                return 0;

            int rngBefore = rng.CallsSinceReseed;
            string owner = $"{actor ?? "unknown"}->{targetName ?? "unknown"}#{targetId}";
            uint reactionRaw = RngLedger.Generate(
                rng,
                "unitOwnedCombat",
                $"{source ?? "Unit::onApplyDamage"}:surviving-reaction-gate",
                owner);
            uint reactionRoll = reactionRaw % 100u;
            Debug.LogError(
                $"[ON-APPLY-DAMAGE-RNG] actor={actor ?? "unknown"} target={targetName ?? "unknown"}#{targetId} " +
                $"damageFlags=0x10 damageWire={damageWire} hp={oldHPWire}->{newHPWire}/{targetMaxHPWire} " +
                $"reactionRaw=0x{reactionRaw:X8} reactionRoll={reactionRoll} rngPos={rngBefore}->{rng.CallsSinceReseed} " +
                $"source={source ?? "Unit::onApplyDamage"} sourceFunction=Unit::onApplyDamage@0x0050BE50");

            if (reactionRoll == 0)
            {
                Debug.LogError(
                    $"[NATIVE-GAP] Unit::onApplyDamage selected-reaction branch target={targetName ?? "unknown"}#{targetId} " +
                    $"damageWire={damageWire} maxHPWire={targetMaxHPWire} reactionRaw=0x{reactionRaw:X8} " +
                    $"missing=exact-reaction-selection-and-conditional-CheckStunResist source={source ?? "Unit::onApplyDamage"}");
            }

            return reactionRaw;
        }

        private uint ConsumeOnApplyDamageEffectRng(MersenneTwister rng, string actor, Monster target, uint oldHPWire, uint newHPWire, uint damageWire, int stunMod, int attackerLevel, uint sourceEntityId, string source, bool hasDamagePosition = false, int damagePositionFixedX = 0, int damagePositionFixedY = 0)
        {
            if (target == null || rng == null || damageWire == 0 || oldHPWire == 0 || newHPWire == 0 || newHPWire >= oldHPWire)
                return 0;

            int rngBefore = rng.CallsSinceReseed;
            string owner = $"{actor ?? "unknown"}->{target.Name ?? "unknown"}#{target.EntityId}";
            uint reactionRaw = RngLedger.Generate(rng, "unitOwnedCombat", $"{source ?? "Unit::onApplyDamage"}:surviving-reaction-gate", owner, target.EntityId);
            uint reactionRoll = reactionRaw % 100u;
            Debug.LogError($"[ON-APPLY-DAMAGE-RNG] actor={actor ?? "unknown"} target={target.Name ?? "unknown"}#{target.EntityId} damageFlags=0x10 damageWire={damageWire} stunMod={stunMod} hp={oldHPWire}->{newHPWire}/{target.MaxHPWire} reactionRaw=0x{reactionRaw:X8} reactionRoll={reactionRoll} rngPos={rngBefore}->{rng.CallsSinceReseed} source={source ?? "Unit::onApplyDamage"} sourceFunction=Unit::onApplyDamage@0x0050BE50");
            if (reactionRoll != 0 || target.MaxHPWire == 0 || stunMod <= 0)
                return reactionRaw;

            int ratio = ComputeOnApplyDamageReactionRatio(damageWire, stunMod, target.MaxHPWire);
            if (ratio < 10)
                return reactionRaw;

            int stunResistWire = ResolveMonsterStunResistChanceWire(target, attackerLevel);
            uint stunRaw = RngLedger.Generate(rng, "unitOwnedCombat", $"{source ?? "Unit::onApplyDamage"}:CheckStunResist", "Unit::CheckStunResist", target.EntityId);
            uint stunRoll = stunRaw % 0x6400u;
            if (stunRoll < (uint)Math.Max(0, stunResistWire))
            {
                Debug.LogError($"[DAMAGE-REACTION] target={target.Name}#{target.EntityId} result=RESIST ratio={ratio} stunMod={stunMod} stunResistWire={stunResistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} source={source ?? "Unit::onApplyDamage"} sourceFunction=Unit::CheckStunResist@0x0050C630");
                return reactionRaw;
            }

            byte actionId = ratio >= 50 ? (byte)0x0B : ratio >= 25 ? (byte)0x0A : (byte)0x0C;
            bool actionStarted = StartMonsterDamageReaction(target, actionId, 60, sourceEntityId, source, false, hasDamagePosition, damagePositionFixedX, damagePositionFixedY);
            string actionResult = actionStarted ? "ACTION" : "ACTION_REJECTED";
            Debug.LogError($"[DAMAGE-REACTION] target={target.Name}#{target.EntityId} result={actionResult} actionId=0x{actionId:X2} ratio={ratio} stunMod={stunMod} stunResistWire={stunResistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} source={source ?? "Unit::onApplyDamage"} sourceFunction=Unit::onApplyDamage@0x0050BE50 Unit::CheckStunResist@0x0050C630 Behavior::doInterruptLocal@0x00515290");
            return reactionRaw;
        }

    }
}
