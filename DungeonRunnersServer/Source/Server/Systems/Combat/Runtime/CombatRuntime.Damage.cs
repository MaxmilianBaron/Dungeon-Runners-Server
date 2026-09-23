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
    {        private bool HasEnemyWithinMonsterPerception(Monster monster)
        {
            if (monster == null || monster.PerceptionRangeF32 < 0)
                return false;
            long rangeFixed = monster.PerceptionRangeF32;
            long rangeSquared = rangeFixed * rangeFixed;
            foreach (CombatTarget player in GetCombatTargetsInEntityOrder())
            {
                if (player == null || !player.IsAlive || !player.HasUnitState || !IsMonsterEnemyOfTarget(monster, player))
                    continue;
                if (!MatchesInstance(monster, player.InstanceKey))
                    continue;
                long deltaX = (long)player.PosFixedX - monster.PosFixedX;
                long deltaY = (long)player.PosFixedY - monster.PosFixedY;
                if (deltaX * deltaX + deltaY * deltaY <= rangeSquared)
                    return true;
            }
            return false;
        }

        private void BeginStockUnitSpawn(Monster monster)
        {
            monster.CombatCredit.Clear();
            monster.StockUnitState = 4;
            monster.UnitFlags &= 0xFFFFE7FF;
            monster.CurrentHPWire = monster.MaxHPWire;
            monster.CurrentManaWire = monster.MaxManaWire;
            monster.PosFixedX = monster.SpawnPosFixedX;
            monster.PosFixedY = monster.SpawnPosFixedY;
            monster.PosFixedZ = monster.SpawnPosFixedZ;
            monster.IsAlive = true;
            monster.State = MonsterState.Idle;
            monster.DeathLifecycleActive = false;
            monster.DeathRemoveSent = false;
            monster.StockUnitStateTicksRemaining = 0;
            monster.OnDeadTicksRemaining = 0;
            monster.OnDeadDispatched = false;
            monster.MoveInDirectionHeadingInit = false;
            monster.UnitMoverMovingThisFrame = false;
            monster.ClientVisibleMovingThisFrame = false;
            monster.SpeedMod = monster.BaseSpeedMod;
            _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
            _monsterHPRegenLastTick[monster.EntityId] = _combatTick;
            _monsterManaRegenLastTick[monster.EntityId] = _combatTick;
            EntitySynchInfoAuthority.Instance.RegisterMonster(monster);
            ResetMonsterClientVisiblePositionFixed(monster, monster.PosFixedX, monster.PosFixedY, "StockUnit::initState4");
            foreach (CombatPlayer player in GetPlayersInEntityOrder())
                if (player != null && MatchesInstance(monster, player.InstanceKey))
                    ResetMonsterClientVisiblePositionFixed(monster, player.EntityId, monster.PosFixedX, monster.PosFixedY, "StockUnit::initState4");
            MeleeBehaviorContext context = BuildMeleeBehaviorContextForMonster(monster);
            if (monster.Behavior == null || context == null || !monster.Behavior.BeginSpawn(context))
                throw new InvalidOperationException($"StockUnit spawn action rejected for entity {monster.EntityId}");
            Debug.LogError($"[MON-DEATH-LIFECYCLE] state=4 spawn {monster.Name}#{monster.EntityId} pos=({monster.PosFixedX},{monster.PosFixedY},{monster.PosFixedZ}) sourceFunction=StockUnit::initState@0x005032A0->Behavior::doInterruptLocal@0x00515290");
        }

        public void LogMonsterClientVisibleSwingNoDamage(Monster monster, string reason)
        {
            if (monster == null || !monster.IsAlive) return;
            uint hp = GetRuntimeMonsterHPWire(monster, "SWING-NO-DAMAGE");
            Debug.LogError($"[MON-HP-TRUTH] NO-DAMAGE {monster.Name}#{monster.EntityId} hp={hp / 256f:F2}/{monster.MaxHPWire / 256f:F2} reason={reason ?? "unknown"}");
        }

        public bool ApplyPlayerDamageToMonsterWire(Monster monster, uint damageWire, string source, out uint oldHPWire, out uint newHPWire, out bool died, uint clientDamageTick = 0, int damageTypeId = 0, int weaponClassId = 0, uint rawDamageWire = 0, int attackerLevel = 1, byte damageKind = 0, bool spellEffectResistResolved = false, bool spellEffectVulnerable = false, uint sourceEntityId = 0)
        {
            oldHPWire = 0;
            newHPWire = 0;
            died = false;
            if (monster == null || !monster.IsAlive || damageWire == 0) return false;

            oldHPWire = PeekRuntimeMonsterHPWire(monster);
            newHPWire = oldHPWire;

            DamageQueryResult query = ApplyDamageQueryWire(damageWire, monster, damageTypeId, damageKind, source, attackerLevel, spellEffectResistResolved, spellEffectVulnerable);
            uint adjustedDamageWire = query.AdjustedDamageWire;
            if (adjustedDamageWire == 0)
            {
                newHPWire = oldHPWire;
                Debug.LogError($"[MONSTER-DAMAGE] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} damageWire={damageWire} adjustedWire=0 hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} died=False clientTick={clientDamageTick} result={query.ResultName}");
                Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} weaponClassId={weaponClassId} damageTypeId={damageTypeId} rawRollWire={(rawDamageWire != 0 ? rawDamageWire : damageWire)} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire=0 hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} clientTick={clientDamageTick}");
                TraceDesyncForensics(monster, "damage-resolve", source, null, oldHPWire, newHPWire, damageWire, clientDamageTick == 0 ? (uint?)null : clientDamageTick, null);
                return false;
            }

            newHPWire = adjustedDamageWire >= oldHPWire ? 0u : oldHPWire - adjustedDamageWire;
            RecordMonsterCombatParticipation(monster, sourceEntityId, oldHPWire - newHPWire);
            SetRuntimeMonsterHPWire(monster, newHPWire, true, source ?? "damage");

            if (newHPWire == 0)
            {
                MarkMonsterDead(monster, source);
                died = true;
            }

            Debug.LogError($"[MONSTER-DAMAGE] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} damageWire={damageWire} adjustedWire={adjustedDamageWire} hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} died={died} clientTick={clientDamageTick} result={query.ResultName}");
            Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} weaponClassId={weaponClassId} damageTypeId={damageTypeId} rawRollWire={(rawDamageWire != 0 ? rawDamageWire : damageWire)} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire={adjustedDamageWire} hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} clientTick={clientDamageTick}");
            Debug.LogError($"[MON-HP-TRUTH] COMPUTED {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={oldHPWire / 256f:F2}->{newHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} dmg={adjustedDamageWire / 256f:F2}");
            TraceDesyncForensics(monster, "damage-apply", source, null, oldHPWire, newHPWire, adjustedDamageWire, clientDamageTick == 0 ? (uint?)null : clientDamageTick, null);
            return true;
        }

        public bool ApplyPlayerWeaponDamageToMonsterWire(Monster monster, WeaponDamageResult damageResult, string source, out uint oldHPWire, out uint newHPWire, out bool died, out uint effectRaw, MersenneTwister effectRng, string effectActor, uint clientDamageTick = 0, uint sourceEntityId = 0, byte damageKind = 0, bool hasDamagePosition = false, int damagePositionFixedX = 0, int damagePositionFixedY = 0)
        {
            oldHPWire = 0;
            newHPWire = monster != null ? PeekRuntimeMonsterHPWire(monster) : 0;
            died = false;
            effectRaw = 0;
            if (monster == null || damageResult == null || !damageResult.IsHit || damageResult.IsBlocked || damageResult.DamageWire == 0)
                return false;

            string baseSource = source ?? "WeaponDamage";
            string rngActor = effectActor ?? "player-weapon";
            bool applied = ApplyPlayerDamageToMonsterWire(
                monster,
                damageResult.DamageWire,
                baseSource,
                out oldHPWire,
                out newHPWire,
                out died,
                clientDamageTick,
                damageResult.DamageTypeId,
                damageResult.WeaponClassId,
                damageResult.DamageWire,
                damageResult.AttackerLevel,
                damageKind, sourceEntityId: sourceEntityId);
            if (!applied)
                return false;

            int stunMod = ResolveWeaponDamageStunMod(damageResult);
            uint primaryAppliedWire = oldHPWire > newHPWire ? oldHPWire - newHPWire : 0;
            effectRaw = ConsumeOnApplyDamageEffectRng(effectRng, rngActor, monster, oldHPWire, newHPWire, primaryAppliedWire, stunMod, damageResult.AttackerLevel, sourceEntityId, baseSource, hasDamagePosition, damagePositionFixedX, damagePositionFixedY);
            DispatchPlayerDamageEvents(sourceEntityId, monster, primaryAppliedWire, null, baseSource);

            uint totalAppliedWire = primaryAppliedWire;
            if (damageResult.DamageAdds != null)
            {
                foreach (WeaponDamageEvent add in damageResult.DamageAdds)
                {
                    if (add == null || add.DamageWire == 0 || died || monster == null || !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                        break;

                    string addSource = $"{baseSource}-WeaponDamageAdd-{add.Element}";

                    bool addApplied = ApplyPlayerDamageToMonsterWire(
                        monster,
                        add.DamageWire,
                        addSource,
                        out uint addOldHPWire,
                        out uint addNewHPWire,
                        out bool addDied,
                        clientDamageTick,
                        add.DamageTypeId,
                        damageResult.WeaponClassId,
                        add.DamageWire,
                        damageResult.AttackerLevel,
                        3, sourceEntityId: sourceEntityId);
                    if (!addApplied)
                        continue;

                    uint appliedWire = addOldHPWire > addNewHPWire ? addOldHPWire - addNewHPWire : 0;
                    uint addEffectRaw = ConsumeOnApplyDamageEffectRng(effectRng, rngActor, monster, addOldHPWire, addNewHPWire, appliedWire, stunMod, damageResult.AttackerLevel, sourceEntityId, addSource);
                    DispatchPlayerDamageEvents(sourceEntityId, monster, appliedWire, null, addSource);

                    totalAppliedWire = ClampWireAdd(totalAppliedWire, appliedWire);
                    newHPWire = addNewHPWire;
                    died |= addDied;
                    Debug.LogError($"[CLIENT-DAMAGE-ADD] source={baseSource} target={monster.Name}#{monster.EntityId} element={add.Element} damageTypeId={add.DamageTypeId} weaponAdd={add.WeaponAdd} bonus={add.DamageBonus} mod={add.DamageMod} weaponDamageF32={add.WeaponDamageF32} preQueryWire={add.DamageWire} hp={addOldHPWire}->{addNewHPWire}/{monster.MaxHPWire} died={addDied} effectRaw=0x{addEffectRaw:X8} clientTick={clientDamageTick}");
                }
            }

            if (damageResult.AttackerState != null && primaryAppliedWire > 0)
                damageResult.AttackerState.ApplyOnDamageCallback(primaryAppliedWire, baseSource);

            Debug.LogError($"[CLIENT-WEAPON-DAMAGE-REPLAY] source={baseSource} target={monster.Name}#{monster.EntityId} primaryWire={damageResult.DamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} totalRawWire={damageResult.TotalDamageWire} totalAppliedWire={totalAppliedWire} hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} result={damageResult.ResultName} died={died} effectRaw=0x{effectRaw:X8}");
            return true;
        }

        private bool ApplyMonsterOnDamageCallback(Monster monster, uint appliedDamageWire, string source)
        {
            if (monster == null || appliedDamageWire == 0 || monster.Slots == null) return false;
            int hpSteal = monster.Slots.Get(UnitSlot.HitPointSteal);
            int manaSteal = monster.Slots.Get(UnitSlot.ManaPointSteal);
            if (hpSteal <= 0 && manaSteal <= 0) return false;

            uint oldHP = PeekRuntimeMonsterHPWire(monster);
            uint oldMana = monster.CurrentManaWire;
            uint nextHP = oldHP;
            uint nextMana = oldMana;

            if (hpSteal > 0 && oldHP > 0 && monster.MaxHPWire > 0)
            {
                ulong heal = ((ulong)appliedDamageWire * (uint)hpSteal) / 100UL;
                if (heal > 0)
                {
                    ulong hp = (ulong)oldHP + heal;
                    nextHP = hp >= monster.MaxHPWire ? monster.MaxHPWire : (uint)hp;
                    if (nextHP != oldHP)
                        SetRuntimeMonsterHPWire(monster, nextHP, false, $"{source ?? "Damage::apply"}:Unit::onDamageCallback");
                }
            }

            if (manaSteal > 0 && monster.MaxManaWire > 0)
            {
                ulong restore = ((ulong)appliedDamageWire * (uint)manaSteal) / 100UL;
                if (restore > 0)
                {
                    ulong mana = (ulong)oldMana + restore;
                    nextMana = mana >= monster.MaxManaWire ? monster.MaxManaWire : (uint)mana;
                    monster.CurrentManaWire = nextMana;
                }
            }

            bool changed = oldHP != nextHP || oldMana != nextMana;
            if (changed)
                Debug.LogError($"[DAMAGE-CALLBACK] source=monster monster={monster.Name}#{monster.EntityId} hpSteal={hpSteal} manaSteal={manaSteal} appliedWire={appliedDamageWire} hp={oldHP}->{nextHP}/{monster.MaxHPWire} mana={oldMana}->{nextMana}/{monster.MaxManaWire} sourceFunction=Unit::onDamageCallback@0x0050C470");
            return changed;
        }

        private static uint ClampWireAdd(uint left, uint right)
        {
            ulong sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }

        public bool ObserveClientMonsterHP(Monster monster, uint clientHPWire, string source)
        {
            if (monster == null) return false;
            uint runtimeHPWire = PeekRuntimeMonsterHPWire(monster);
            bool matches = clientHPWire == runtimeHPWire;
            Debug.LogError($"[{source}] Monster HP diagnostic: {monster.Name}#{monster.EntityId} received={clientHPWire} runtime={runtimeHPWire} result={(matches ? "match" : "mismatch-non-authoritative")}");
            return matches;
        }

        public bool CanSendMonsterEntitySynchInfoHP(Monster monster, string packetName)
        {
            if (monster == null) return false;
            return TryResolveMonsterEntitySynchInfoHP(monster, packetName, out _);
        }

        private static bool PreserveMonsterWanderMoverState(Monster monster)
        {
            if (monster?.Behavior?.CurrentAction != Behavior.MonsterBehavior2.ActionSlot.Wander
                || !WanderSimulator.Instance.TryGetSnapshot(monster.EntityId, out WanderStateSnapshot wander))
                return false;
            if (wander.FixedInit)
            {
                monster.PosFixedX = wander.FixedX;
                monster.PosFixedY = wander.FixedY;
            }
            if (wander.FixedZInit)
                monster.PosFixedZ = wander.FixedZ;
            if (wander.HeadingInit)
            {
                monster.HeadingFixed = wander.HeadingFixed;
                monster.ChaseHeadingFixed = wander.HeadingFixed;
                monster.ChaseHeadingInit = true;
                monster.UnitMoverDesiredHeadingFixed = wander.HeadingFixed;
                monster.UnitMoverDesiredHeadingInit = true;
            }
            monster.UnitMoverMovingThisFrame = wander.MovingThisFrame;
            monster.RetiredWanderMoverActive = wander.HasTarget;
            monster.RetiredWanderTargetFixedX = wander.TargetFixedX;
            monster.RetiredWanderTargetFixedY = wander.TargetFixedY;
            return true;
        }

        private void AdvanceRetiredWanderMover(Monster monster)
        {
            if (monster == null
                || !monster.RetiredWanderMoverActive
                || WanderSimulator.Instance.IsRegisteredEntity(monster.EntityId)
                || monster.UnitMoverPathOwner != 0
                || monster.SearchForAttackMoveActive
                || monster.FollowRepositionMoveActive
                || monster.FollowFindLineOfSightMoveActive
                || monster.Behavior?.FollowActive == true
                || monster.Behavior?.UseTargetActive == true)
                return;
            int speedFixed = monster.MoveSpeedF32 > 0 ? monster.MoveSpeedF32 : monster.WalkSpeedF32;
            int stepFixed = UnitMover.CacheSpeedPerFrame(speedFixed, monster.SpeedMod + monster.GetActiveAttributeModifierValue("SPEEDMOD"), out _);
            int turnRateFixed = UnitMover.TurnRatePerTickFixed(monster.TurnRateDegrees);
            UnitMover.StepTowardFixedHeading(monster.PosFixedX, monster.PosFixedY, monster.HeadingFixed, monster.RetiredWanderTargetFixedX, monster.RetiredWanderTargetFixedY, stepFixed, turnRateFixed, out int nextFixedX, out int nextFixedY, out int nextHeadingFixed, out bool arrived);
            PathMap pathMap = PathMapCatalog.Instance.GetPathMap(!string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName);
            ResolveMonsterMovementFixed(monster, pathMap, monster.PosFixedX, monster.PosFixedY, monster.PosFixedZ, nextFixedX, nextFixedY, out nextFixedX, out nextFixedY, out int nextFixedZ, _combatTick);
            monster.UnitMoverMovingThisFrame = nextFixedX != monster.PosFixedX || nextFixedY != monster.PosFixedY;
            monster.PosFixedX = nextFixedX;
            monster.PosFixedY = nextFixedY;
            monster.PosFixedZ = nextFixedZ;
            monster.HeadingFixed = nextHeadingFixed;
            monster.ChaseHeadingFixed = nextHeadingFixed;
            monster.ChaseHeadingInit = true;
            if (arrived)
                monster.RetiredWanderMoverActive = false;
        }

        private bool AggroMonster(Monster monster, CombatTarget player, string reason, bool alignForCombat, bool queueFollowBehaviorInput = true, uint followInputApplyTick = uint.MaxValue, uint directPlayerWeaponHitTick = 0, uint playerDamageTick = 0)
        {
            if (monster == null || player == null || !monster.IsAlive) return false;
            if (!IsMonsterEnemyOfTarget(monster, player)) return false;
            bool firstAggro = !monster.AggroTriggered || monster.TargetId != player.EntityId;
            bool hadWanderMover = firstAggro && PreserveMonsterWanderMoverState(monster);
            monster.FollowAdmissionReachable = true;
            monster.ProximityFollowAdmissionPending = false;
            monster.ProximityTargetScanMatched = false;
            monster.ProximityAttackInputApplyTick = 0;
            monster.DeferredTargetScanMessagePending = false;
            monster.DeferredTargetScanMessageReleased = false;
            monster.BehaviorAssistSourceEntityId = 0;
            monster.BehaviorTargetUsesClientSimulationPosition = false;
            monster.AggroTriggered = true;
            monster.TargetId = player.EntityId;
            monster.AlertSourceEntityId = 0;
            monster.State = MonsterState.Combat;
            monster.Ai?.PostMessage(MonsterMessageId.TargetChanged, reason);
            monster.Ai?.SetState(MonsterStateId.Attack, "aggro-target");
            if (firstAggro)
            {
                if (hadWanderMover)
                    monster.WanderActionTerminationPending = true;
                monster.AttackPending = false;
                monster.AttackSoundPending = false;
                if (queueFollowBehaviorInput)
                    QueueMonsterFollowBehaviorInput(monster, player, followInputApplyTick, directPlayerWeaponHitTick, playerDamageTick);
                Debug.LogError($"[SERVER-AGGRO] {monster.Name} -> {player.Name} reason={reason}");
                OnMonsterAggro?.Invoke(monster, player);
            }
            TraceMonsterState(monster, "aggro", player, Distance2DFixed(monster.PosFixedX, monster.PosFixedY, player.PosFixedX, player.PosFixedY), ResolveMonsterEffectiveAttackRangeFixed(monster), reason);
            return firstAggro;
        }

        private void PropagateMonsterShout(Monster source, CombatTarget player, bool notifyBehaviorThreat = true, uint proximityAssistApplyTick = uint.MaxValue)
        {
            int shoutFixed = ResolveMonsterShoutRangeFixed(source);
            if (source == null || player == null || shoutFixed <= 0) return;
            long shoutSq = ((long)shoutFixed * shoutFixed) >> 8;
            string pathMapKey = ResolveMonsterPathMapKey(source);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapCatalog.Instance.GetPathMap(pathMapKey) : null;
            var ranked = new List<(Monster Monster, long DistanceSq)>();
            foreach (var monster in GetAllMonsters())
            {
                if (monster == source || !monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0) continue;
                if (!string.Equals(ResolveMonsterPathMapKey(monster), pathMapKey, StringComparison.OrdinalIgnoreCase)) continue;

                int dx = monster.PosFixedX - source.PosFixedX;
                int dy = monster.PosFixedY - source.PosFixedY;
                int dz = monster.PosFixedZ - source.PosFixedZ;
                long distSq = (((long)dx * dx) >> 8) + (((long)dy * dy) >> 8) + (((long)dz * dz) >> 8);
                if (distSq > shoutSq) continue;
                ranked.Add((monster, distSq));
            }
            ranked.Sort((left, right) =>
            {
                int distanceCompare = left.DistanceSq.CompareTo(right.DistanceSq);
                return distanceCompare != 0
                    ? distanceCompare
                    : left.Monster.EntityId.CompareTo(right.Monster.EntityId);
            });
            foreach (var candidate in ranked)
            {
                Monster monster = candidate.Monster;
                if (!HasAlertEncounterRelation(monster, source, out string relation))
                {
                    Debug.LogError($"[SERVER-SHOUT] source={source.EntityId} target={monster.EntityId} shoutF32={source.ShoutRangeF32} distFixed8={Distance2DFixed(source.PosFixedX, source.PosFixedY, monster.PosFixedX, monster.PosFixedY)} action=ignored relation={relation} sourceGroup={source.EncounterGroupKey ?? ""} targetGroup={monster.EncounterGroupKey ?? ""}");
                    continue;
                }

                bool queueFollow = pathMap == null
                    || pathMap.GetReachabilityFixed(source.PosFixedX, source.PosFixedY, monster.PosFixedX, monster.PosFixedY) == PathReachability.Reachable;
                int alertDistanceFixed = Distance2DFixed(source.PosFixedX, source.PosFixedY, monster.PosFixedX, monster.PosFixedY);
                Debug.LogError($"[SERVER-SHOUT] source={source.EntityId} target={monster.EntityId} shoutF32={source.ShoutRangeF32} distFixed8={alertDistanceFixed} action=notify-friend relation={relation} follow={queueFollow} sourceTarget={source.TargetId} sourceGroup={source.EncounterGroupKey ?? ""} targetGroup={monster.EncounterGroupKey ?? ""}");
                TraceMonsterState(monster, "alert", player, alertDistanceFixed, ResolveMonsterEffectiveAttackRangeFixed(monster), $"source={source.EntityId} relation={relation}");
                bool proximityAssistInput = proximityAssistApplyTick != uint.MaxValue;
                uint followInputApplyTick = proximityAssistInput
                    ? proximityAssistApplyTick
                    : source.FollowInputPending
                        ? source.FollowInputApplyTick
                        : uint.MaxValue;
                uint directPlayerWeaponHitTick = proximityAssistInput
                    ? 0
                    : source.FollowInputPending
                        ? source.FollowInputDirectPlayerWeaponHitTick
                        : 0;
                uint playerDamageTick = proximityAssistInput
                    ? 0
                    : source.FollowInputPending
                        ? source.FollowInputPlayerDamageTick
                        : 0;
                AdmitMonsterAssist(monster, source, player, relation, queueFollow, "notifyFriends", notifyBehaviorThreat, followInputApplyTick, directPlayerWeaponHitTick, playerDamageTick);
            }
        }

        private static int ResolveWeaponDamageStunMod(WeaponDamageResult damageResult)
        {
            PlayerState state = damageResult?.AttackerState;
            int weaponStunMod = unchecked((ushort)Math.Max(0, state?.WeaponStunMod ?? 100));
            int unitStunMod = Math.Max(0, state?.StunMod ?? 100);
            int product = unchecked(weaponStunMod * unitStunMod);
            return unchecked((ushort)(product / 100));
        }

        private static int ResolveMonsterWeaponDamageStunMod(Monster monster)
        {
            int weaponStunMod = unchecked((ushort)Math.Max(0, monster?.WeaponStunMod ?? 100));
            int unitStunMod = Math.Max(0, monster?.Slots?.Get(UnitSlot.StunMod, monster.StunMod) ?? 100);
            int product = unchecked(weaponStunMod * unitStunMod);
            return unchecked((ushort)(product / 100));
        }

        private static int ResolveSpellDamageStunMod(int effectStunMod, int unitStunMod)
        {
            int effect = unchecked((ushort)effectStunMod);
            int product = unchecked(effect * unitStunMod);
            return unchecked((ushort)(product / 100));
        }

        private bool AdmitMonsterAssist(Monster monster, Monster alertSource, CombatTarget target, string relation, bool queueFollow, string source, bool notifyBehaviorThreat = true, uint followInputApplyTick = uint.MaxValue, uint directPlayerWeaponHitTick = 0, uint playerDamageTick = 0)
        {
            if (monster == null || alertSource == null || target == null || !monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0)
                return false;
            bool hadWanderMover = PreserveMonsterWanderMoverState(monster);
            monster.AlertSourceEntityId = 0;
            monster.BehaviorAssistSourceEntityId = 0;
            monster.AggroTriggered = true;
            monster.TargetId = target.EntityId;
            monster.State = MonsterState.Combat;
            monster.Ai?.SetState(MonsterStateId.Assist, source);
            monster.Ai?.PostMessage(MonsterMessageId.AlertChanged, source);
            EncounterLogAssistNotActive(monster, source);
            if (hadWanderMover)
                monster.WanderActionTerminationPending = true;
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            monster.FollowAdmissionReachable = queueFollow;
            if (queueFollow)
            {
                QueueMonsterFollowBehaviorInput(monster, target, followInputApplyTick, directPlayerWeaponHitTick, playerDamageTick);
                OnMonsterAggro?.Invoke(monster, target);
            }
            if (notifyBehaviorThreat)
            {
                monster.BehaviorAssistSourceEntityId = alertSource.EntityId;
                NotifyMonsterBehaviorThreatInput(monster, target, true);
            }
            Debug.LogError($"[SERVER-ASSIST] source={alertSource.Name}#{alertSource.EntityId} target={monster.Name}#{monster.EntityId} copiedTarget={target.Name}#{target.EntityId} relation={relation} follow={queueFollow} behaviorThreat={notifyBehaviorThreat} source={source ?? "unknown"}");
            TraceMonsterState(monster, "assist", target, Distance2DFixed(monster.PosFixedX, monster.PosFixedY, target.PosFixedX, target.PosFixedY), ResolveMonsterEffectiveAttackRangeFixed(monster), $"source={alertSource.EntityId} relation={relation} follow={queueFollow}");
            return true;
        }

        private bool PlayerHasIncomingAttacker(uint playerEntityId, uint exceptEntityId = 0)
        {
            foreach (var monster in GetAllMonsters())
            {
                if (monster == null || !monster.IsAlive || !monster.AggroTriggered) continue;
                if (exceptEntityId != 0 && monster.EntityId == exceptEntityId) continue;
                if (monster.TargetId == playerEntityId) return true;
            }
            return false;
        }

        public IEnumerable<Monster> GetAllMonsters()
        {
            foreach (uint entityId in _entityOrder)
                if (_activeMonsters.TryGetValue(entityId, out Monster monster))
                    yield return monster;
        }

        private IEnumerable<CombatPlayer> GetPlayersInEntityOrder()
        {
            foreach (uint entityId in _entityOrder)
                if (_players.TryGetValue(entityId, out CombatPlayer player))
                    yield return player;
        }
        public void UnregisterPlayer(uint entityId, bool preserveAttributeModifiers = false)
        {
            CancelMoveToPointPathRequestsForEntity(entityId);
            if (_players.TryGetValue(entityId, out CombatPlayer removedPlayer))
            {
                RemoveOwnedSummons(removedPlayer, preserveAttributeModifiers);
                RemovePlayerAnchoredMonsterAurasForAnchor(entityId, "UnregisterPlayer");
                RemovePlayerDamageModifiersForTarget(entityId, "UnregisterPlayer", false);
                removedPlayer.PlayerState?.InitializeEntityEpochRuntime(preserveAttributeModifiers);
            }
            if (!preserveAttributeModifiers)
            {
                string modifierKeyPrefix = entityId + ":";
                string[] modifierKeys = _playerModifierNetworkIds.Keys
                    .Where(key => key.StartsWith(modifierKeyPrefix, StringComparison.Ordinal))
                    .ToArray();
                foreach (string modifierKey in modifierKeys)
                    _playerModifierNetworkIds.Remove(modifierKey);
            }
            _players.Remove(entityId);
            UnregisterEntityOrder(entityId);
            int clearedMonsters = 0;
            foreach (var monster in GetAllMonsters())
            {
                if (monster == null) continue;
                if (removedPlayer != null)
                    monster.CombatCredit.Remove(removedPlayer);
                if (ClearMonsterTargetStateForLostPlayer(monster, entityId, "UnregisterPlayer"))
                    clearedMonsters++;
            }
            Debug.LogError($"[COMBAT-LIFECYCLE] unregistered player {entityId} and cleared monster targeting state count={clearedMonsters} preserveAttributeModifiers={preserveAttributeModifiers}");
        }

        private bool ClearMonsterTargetStateForLostPlayer(Monster monster, uint lostPlayerEntityId, string source)
        {
            if (monster == null || !monster.IsAlive || monster.State == MonsterState.Dead)
                return false;

            uint followTargetEntityId = ResolveMonsterFollowTargetEntityId(monster);
            bool touchesLostPlayer = lostPlayerEntityId != 0 &&
                (monster.TargetId == lostPlayerEntityId ||
                 followTargetEntityId == lostPlayerEntityId ||
                 monster.CombatContactTargetId == lostPlayerEntityId ||
                 monster.SelectedActiveSkillTargetEntityId == lostPlayerEntityId);
            bool hasRuntimeTargetState =
                monster.TargetId != 0 ||
                followTargetEntityId != 0 ||
                monster.CombatContactTargetId != 0 ||
                monster.SelectedActiveSkillTargetEntityId != 0 ||
                monster.AlertSourceEntityId != 0 ||
                monster.AggroTriggered ||
                monster.AggroSent ||
                monster.AttackPending ||
                monster.AttackSoundPending ||
                monster.AttackClientVisible ||
                monster.AttackContactOnly ||
                monster.AttackHitResolved ||
                monster.UsePrimaryActiveSkillThisAttack ||
                monster.State == MonsterState.Chase ||
                monster.State == MonsterState.Combat ||
                monster.State == MonsterState.Attacking;
            uint runtimeTargetId = monster.TargetId != 0 ? monster.TargetId : followTargetEntityId;
            bool targetMissing = runtimeTargetId == 0 || !TryGetCombatTarget(runtimeTargetId, out _);
            if (!hasRuntimeTargetState || (!touchesLostPlayer && !targetMissing))
                return false;

            MonsterState oldState = monster.State;
            uint oldTarget = monster.TargetId;
            uint oldContact = monster.CombatContactTargetId;
            bool oldAggro = monster.AggroTriggered;
            bool oldPending = monster.AttackPending;

            ClearMonsterMoveToPointPath(monster, 0);
            monster.ClearTarget();
            monster.AggroTriggered = false;
            monster.AggroSent = false;
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            monster.AttackClientVisible = false;
            monster.AttackActionQueued = false;
            monster.AttackActionAdmissionTick = 0;
            monster.AttackContactOnly = false;
            monster.AttackHitResolved = false;
            monster.UsePrimaryActiveSkillThisAttack = false;
            monster.SearchForAttackMoveActive = false;
            monster.AttackStartTick = 0;
            monster.AttackCommitTick = 0;
            monster.AttackSoundTick = 0;
            monster.AttackEndTick = 0;
            monster.AttackUseRaw = 0;
            monster.AttackSoundRaw = 0;
            monster.AttackSoundGateRaw = 0;
            monster.AttackSoundRepeatRaw = 0;
            ClearMonsterActiveSkillEffectCycle(monster);
            ClearMonsterAttackCommitTarget(monster);
            ClearMonsterUseTargetAction(monster);
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntilTick = 0;
            monster.ChaseHeadingInit = false;
            monster.UnitMoverDesiredHeadingFixed = monster.HeadingFixed;
            monster.UnitMoverDesiredHeadingInit = true;
            monster.ClientVisibleHeadingInit = false;
            if (monster.IsAlive)
            {
                monster.State = MonsterState.Idle;
                HealMonsterToFullOnIdle(monster, "target-clear");
            }
            _monsterFarTargetActionLogTick.Remove(monster.EntityId);

            Debug.LogError($"[MON-TARGET-CLEAR] monster={monster.Name}#{monster.EntityId} lostPlayer={lostPlayerEntityId} oldState={oldState} oldTarget={oldTarget} oldContact={oldContact} oldAggro={oldAggro} oldPending={oldPending} hp={GetRuntimeMonsterHPWire(monster, "target-clear")}/{monster.MaxHPWire} posFixed8=({monster.PosFixedX},{monster.PosFixedY},{monster.PosFixedZ}) source={source ?? "unknown"} sourceFunction=MonsterBehavior2::ClearTargets/UpdateTargets");
            TraceMonsterState(monster, "target-clear", null, -1, ResolveMonsterEffectiveAttackRangeFixed(monster), source);
            return true;
        }
        private RoomRuntime CurrentRoomRuntime => GetRoomRuntime(_currentRoomRuntimeKey);

        public MersenneTwister RoomRng => CurrentRoomRuntime.RoomRng;

        public uint RoomSeed => CurrentRoomRuntime.Seed;

        public bool IsRoomRngReady => CurrentRoomRuntime.Initialized;

        public int RoomRngCallsSinceReseed => CurrentRoomRuntime.RngCallsSinceReseed;

        public MersenneTwister RoomRandom => CurrentRoomRuntime.RoomRng;
        public uint RandomSeed => CurrentRoomRuntime.Seed;

        public string CurrentRoomRuntimeKey => _currentRoomRuntimeKey;

        public RoomRuntime GetRoomRuntime(string instanceKey)
        {
            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (!_roomRuntimes.TryGetValue(key, out var runtime))
            {
                runtime = new RoomRuntime(key);
                _roomRuntimes[key] = runtime;
            }
            return runtime;
        }

        public bool TryGetRoomRuntime(string instanceKey, out RoomRuntime runtime)
        {
            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            return _roomRuntimes.TryGetValue(key, out runtime);
        }

        public bool TryGetInitializedRoomRuntime(string instanceKey, out RoomRuntime runtime)
        {
            runtime = null;
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "missing-instance-key", "source=TryGetInitializedRoomRuntime", 64);
                Debug.LogError("[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=missing-instance-key rng=null");
                return false;
            }

            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (string.Equals(key, RoomRuntime.DefaultInstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "default-instance-key", $"source=TryGetInitializedRoomRuntime instance='{key}'", 64);
                Debug.LogError($"[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=default-instance-key instance='{key}' rng=null");
                return false;
            }

            if (!_roomRuntimes.TryGetValue(key, out runtime) || runtime == null || !runtime.Initialized)
            {
                RuntimeEvidence.LogFallbackHit("rng-instance", "uninitialized-instance", $"source=TryGetInitializedRoomRuntime instance='{key}'", 64);
                Debug.LogError($"[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=uninitialized-instance instance='{key}' rng=null");
                runtime = null;
                return false;
            }

            return true;
        }

        public RoomRuntime RequireInitializedRoomRuntime(string instanceKey, string source)
        {
            if (TryGetInitializedRoomRuntime(instanceKey, out var runtime))
                return runtime;

            Debug.LogError($"[RNG-INSTANCE] source={source ?? "unknown"} reason=required-runtime-unavailable instance='{instanceKey ?? "<null>"}'");
            return null;
        }

        public void SetCurrentRoomRuntime(string instanceKey, string source = null)
        {
            _currentRoomRuntimeKey = RoomRuntime.NormalizeInstanceKey(instanceKey);
            GetRoomRuntime(_currentRoomRuntimeKey);
            Debug.LogError($"[ROOM-RUNTIME] current='{_currentRoomRuntimeKey}' source={source ?? "unknown"}");
        }

        private string ResolveMonsterRuntimeKey(Monster monster)
        {
            if (!string.IsNullOrWhiteSpace(monster?.InstanceKey))
                return monster.InstanceKey;
            RuntimeEvidence.LogFallbackHit(
                "rng-instance",
                "monster-missing-instance",
                $"monster={monster?.Name ?? "<null>"}#{monster?.EntityId ?? 0}",
                64);
            return null;
        }

        public RoomRuntime GetRoomRuntimeForMonster(Monster monster)
        {
            return RequireInitializedRoomRuntime(ResolveMonsterRuntimeKey(monster), "monster-runtime");
        }

        public CombatContext GetCombatContextForMonster(Monster monster, string source = null)
        {
            RoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            return runtime != null ? runtime.CreateContext(source) : new CombatContext(null, source);
        }

        public MersenneTwister GetRoomRngForMonster(Monster monster)
        {
            return GetRoomRuntimeForMonster(monster)?.RoomRng;
        }

        public MersenneTwister GetRoomRngForInstance(string instanceKey)
        {
            return RequireInitializedRoomRuntime(instanceKey, "instance-rng")?.RoomRng;
        }

        public string GetPlayerInstanceKey(uint playerEntityId)
        {
            return _players.TryGetValue(playerEntityId, out var player) && !string.IsNullOrWhiteSpace(player?.InstanceKey)
                ? player.InstanceKey
                : null;
        }

        public MersenneTwister GetRoomRngForPlayerEntity(uint playerEntityId)
        {
            string instanceKey = GetPlayerInstanceKey(playerEntityId);
            if (string.IsNullOrWhiteSpace(instanceKey) || instanceKey == RoomRuntime.DefaultInstanceKey)
            {
                RuntimeEvidence.LogFallbackHit(
                    "rng-instance",
                    "player-missing-instance",
                    $"player={playerEntityId}",
                    64);
                Debug.LogError($"[RNG-INSTANCE] source=GetRoomRngForPlayerEntity reason=player-missing-instance player={playerEntityId} rng=null");
                return null;
            }
            return GetRoomRngForInstance(instanceKey);
        }

        public uint GetRoomSeedForInstance(string instanceKey)
        {
            return TryGetInitializedRoomRuntime(instanceKey, out var runtime) ? runtime.Seed : 0u;
        }

        public int GetRoomRngPosForInstance(string instanceKey)
        {
            return TryGetInitializedRoomRuntime(instanceKey, out var runtime) ? runtime.RngCallsSinceReseed : -1;
        }

        public void InitializeRoomRng(uint seed)
        {
            InitializeRoomRng(_currentRoomRuntimeKey, seed, "legacy-current");
        }

        public void InitializeRoomRng(string instanceKey, uint seed, string source = null)
        {
            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            _currentRoomRuntimeKey = key;
            RoomRuntime runtime = GetRoomRuntime(key);
            runtime.Initialize(seed, source ?? "InitializeRoomRng");
            Debug.LogError($"[RNG-SEED] room initialize instance='{key}' seed=0x{seed:X8} rngPos=0 monsters={_activeMonsters.Count(m => string.Equals(ResolveMonsterRuntimeKey(m.Value), key, StringComparison.OrdinalIgnoreCase))} players={_players.Count}");
        }

        public void EnsureRoomRng(string instanceKey, uint seed, string source = null)
        {
            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            _currentRoomRuntimeKey = key;
            RoomRuntime runtime = GetRoomRuntime(key);
            bool initialized = runtime.EnsureInitialized(seed, source ?? "EnsureRoomRng");
            Debug.LogError($"[RNG-SEED] ensure instance='{key}' seed=0x{seed:X8} initialized={initialized} current=0x{runtime.Seed:X8} rngPos={runtime.RngCallsSinceReseed} source={source ?? "unknown"}");
        }

        public void AdvanceRoomRng(int count, string source)
        {
            CurrentRoomRuntime.Advance(count, source);
        }

        public void AdvanceRoomRng(string instanceKey, int count, string source)
        {
            GetRoomRuntime(instanceKey).Advance(count, source);
        }

        public void ReseedRoomRng(uint seed)
        {
            ReseedRoomRng(_currentRoomRuntimeKey, seed, "legacy-current");
        }

        public void ReseedRoomRng(string instanceKey, uint seed, string source = null)
        {
            string key = RoomRuntime.NormalizeInstanceKey(instanceKey);
            _currentRoomRuntimeKey = key;
            RoomRuntime runtime = GetRoomRuntime(key);
            runtime.Reseed(seed, source ?? "ReseedRoomRng");
            foreach (uint entityId in _entityOrder)
            {
                if (!_activeMonsters.TryGetValue(entityId, out Monster monster)
                    || !string.Equals(ResolveMonsterRuntimeKey(monster), key, StringComparison.OrdinalIgnoreCase))
                    continue;
                monster.RngSeed = seed;
                monster.Rng = runtime.RoomRng;
            }
        }
        public void InitializeRandomSeed(uint seed)
        {
            RoomRuntime runtime = CurrentRoomRuntime;
            if (runtime.Initialized)
            {
                if (runtime.Seed != seed)
                    Debug.LogError($"[ROOM-RNG] Ignored legacy reseed request instance='{runtime.InstanceKey}' seed=0x{seed:X8} current=0x{runtime.Seed:X8} rngPos={runtime.RngCallsSinceReseed}");
                return;
            }
            InitializeRoomRng(seed);
        }
        public event Action<Monster, CombatTarget> OnMonsterAggro;
        public CombatPlayer GetPlayer(uint entityId)
        {
            return _players.TryGetValue(entityId, out var p) ? p : null;
        }

        public void SetPlayerActiveClientAttack(uint entityId, bool active, uint targetId = 0)
        {
            if (_players.TryGetValue(entityId, out var player) && player != null)
            {
                player.HasActiveClientAttack = active;
                player.ActiveClientAttackTargetId = active ? targetId : 0;
            }
        }

        public IEnumerable<CombatPlayer> GetAllPlayers()
        {
            return GetPlayersInEntityOrder();
        }
        private string ResolveSpawnBehaviourType(string spawnGcType, string baseGcType)
        {
            return ResolveAuthoredChildPath(spawnGcType, "Behavior") ?? ResolveAuthoredChildPath(baseGcType, "Behavior");
        }

        public Monster SpawnMonsterFixed(string gcType, int posFixedX, int posFixedY, int posFixedZ, int headingFixed = 0, string zoneName = null, string encounterGroupKey = null, int encounterDifficultyF32 = 0x100, string spawnGcTypeOverride = null, string instanceKey = null, int encounterLevelOffset = 0, CombatPlayer summoner = null, byte? summonLevel = null, FriendlySummonTransfer summonTransfer = null)
        {
            SummonedUnitData summonData = AuthoredGameplayCatalog.FindSummonedUnit(gcType);
            if (summonData != null && (summoner == null || summonLevel == null
                || !ReferenceEquals(GetPlayer(summoner.EntityId), summoner)
                || !summoner.IsAlive || summoner.PlayerState == null || summoner.PlayerState.CurrentHPWire == 0
                || !string.Equals(RoomRuntime.NormalizeInstanceKey(summoner.InstanceKey), RoomRuntime.NormalizeInstanceKey(instanceKey), StringComparison.OrdinalIgnoreCase)))
                return null;
            var creatureData = summonData?.unit ?? AuthoredGameplayCatalog.FindCreature(gcType);
            if (creatureData == null)
            {
                Debug.LogError($"[COMBAT] creature='{gcType}' state=missing");
                return null;
            }

            string spawnGcType = !string.IsNullOrEmpty(spawnGcTypeOverride)
                ? spawnGcTypeOverride
                : summonData != null ? creatureData.gcType : ResolveDungeonCreaturePath(zoneName, creatureData.gcType);
            GCNode authoredCreature = ResolveAuthoredCreatureNode(spawnGcType, creatureData.gcType);
            string spawnBehaviourType = ResolveSpawnBehaviourType(spawnGcType, creatureData.gcType);
            GCNode authoredDesc = authoredCreature?.GetChild("Description") ?? authoredCreature;
            GCNode authoredBehavior = ResolveAuthoredBehaviorNode(spawnBehaviourType);
            var spawnManipulators = BuildSpawnManipulators(spawnGcType, creatureData.gcType, authoredCreature, creatureData.manipulators);
            spawnManipulators.TryGetValue("primaryweapon", out ManipulatorData primaryWeaponManipulator);
            GCNode authoredWeapon = ResolveSpawnWeaponDescription(primaryWeaponManipulator);
            string creatureDifficulty = GetAuthoredString(authoredDesc, "CreatureDifficulty", creatureData.creatureDifficulty);
            int unitDifficultyF32 = GetAuthoredFixed32(authoredDesc, "Difficulty", MonsterHealthTable.GetDifficultyModifierF32(creatureDifficulty));
            if (unitDifficultyF32 <= 0) unitDifficultyF32 = 0x100;
            int experienceValueMultF32 = Math.Max(0, GetAuthoredFixed32(authoredDesc, "ExperienceValueMult", 0x100));
            int maxHealthF32 = GetAuthoredFixed32(authoredDesc, "MaxHealth", 0x100);
            int perceptionRangeF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "Perception", 100 * 0x100);
            int aggroRangeF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "AgroRange", 40 * 0x100);
            int shoutRangeF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "ShoutRange", 50 * 0x100);
            int attackCooldownF32 = GetAuthoredFixed32(authoredWeapon, "CoolDown", 0);
            bool weaponUsesProjectile = GetAuthoredBool(authoredWeapon, "UseProjectile", false);
            int weaponShotType = GetAuthoredInt(authoredWeapon, "ShotType", 0);
            int weaponProjectileSpeedF32 = Math.Max(0, GetAuthoredFixed32(authoredWeapon, "ProjectileSpeed", 180 * 0x100));
            int weaponProjectileSizeF32 = Math.Max(0, GetAuthoredFixed32(authoredWeapon, "ProjectileSize", 10 * 0x100));
            int weaponDescRangeF32 = Math.Max(0, GetAuthoredFixed32(authoredWeapon, "Range", 0));
            AttackTiming attackTiming = ResolveAttackTiming(authoredDesc, weaponShotType != 0, spawnGcType ?? creatureData.gcType);
            string attackType = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "AttackType", "0");
            string idleAction = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "IdleAction", "3");
            string logicType = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "LogicType", "0");
            string attackStyle = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "AttackStyle", "0");
            bool retreatable = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "Retreatable", CLIENT_DEFAULT_MONSTER_RETREATABLE);
            bool leashed = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "Leashed", CLIENT_DEFAULT_MONSTER_LEASHED);
            bool useIdleTime = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "UseIdleTime", CLIENT_DEFAULT_MONSTER_USE_IDLE_TIME);
            bool autoScan = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "AutoScan", CLIENT_DEFAULT_UNIT_AUTO_SCAN);
            bool avoidUnits = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "AvoidUnits", CLIENT_DEFAULT_UNIT_AVOID_UNITS);
            bool turnBeforeMoving = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "TurnBeforeMoving", CLIENT_DEFAULT_UNIT_TURN_BEFORE_MOVING);
            bool playerControlled = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "PlayerControlled", CLIENT_DEFAULT_UNIT_PLAYER_CONTROLLED);
            int collisionBand = GetAuthoredBehaviorInt(authoredBehavior, authoredCreature, "CollisionBand", CLIENT_DEFAULT_UNIT_COLLISION_BAND);
            int collisionPriority = GetAuthoredBehaviorInt(authoredBehavior, authoredCreature, "CollisionPriority", CLIENT_DEFAULT_UNIT_COLLISION_PRIORITY);
            int fleeRangeF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "FleeRange", 0);
            int retreatRangeSquaredF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "RetreatRangeSquared", 640000 * 0x100);
            int teleportFrequencyF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "TeleportFrequency", 150 * 0x100);
            int teleportLimboTimeF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "TeleportLimboTime", 60 * 0x100);
            ushort idleBaseTime = unchecked((ushort)GetAuthoredBehaviorInt(authoredBehavior, authoredCreature, "BaseTime", CLIENT_DEFAULT_MONSTER_BASE_TIME));
            ushort idleVariableTime = unchecked((ushort)GetAuthoredBehaviorInt(authoredBehavior, authoredCreature, "VariableTime", CLIENT_DEFAULT_MONSTER_VARIABLE_TIME));
            int leashRangeF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "LeashRange", 0);
            int attackRangeF32 = primaryWeaponManipulator != null && authoredWeapon != null && authoredWeapon.HasProperty("Range")
                ? GetAuthoredFixed32(authoredWeapon, "Range", 0)
                : GetAuthoredFixed32(authoredDesc, "AttackRange", 0);
            int unitDescAttackRangeF32 = GetAuthoredFixed32(authoredDesc, "AttackRange", 0);
            int clientSyncToleranceF32 = GetAuthoredFixed32(authoredWeapon, "ClientSyncTolerance", 0);
            int collisionRadiusF32 = GetAuthoredFixed32(authoredDesc, "CollisionRadius", 5 * 0x100);
            ResolveWorldEntityBoundingBoxF32(
                authoredCreature,
                spawnGcType ?? creatureData.gcType,
                out int boundingBoxRadiusXYF32,
                out int boundingBoxMinZF32,
                out int boundingBoxMaxZF32);
            int sizeModPercent = Math.Max(0, GetAuthoredInt(authoredDesc, "SizeMod", 100));
            int scanFrequencyF32 = GetAuthoredBehaviorFixed32(authoredBehavior, authoredCreature, "ScanFrequency", 0x100);
            int moveSpeedF32 = GetAuthoredMoveSpeedF32(authoredDesc, creatureData);
            int walkSpeedF32 = GetAuthoredWalkSpeedF32(authoredCreature, authoredDesc, creatureData);
            GCNode globalKnobs = GCDatabase.Instance?.GlobalKnobs;
            if (globalKnobs == null || !globalKnobs.HasProperty("MovementSpeedModifier"))
                throw new InvalidDataException("GlobalKnobs missing client RPGSettings field MovementSpeedModifier");
            int speedMod = checked(GetAuthoredInt(authoredDesc, "SpeedMod", 100) + GetAuthoredInt(globalKnobs, "MovementSpeedModifier", 0));
            int wanderRangeF32 = GetAuthoredWanderRangeF32(authoredBehavior, authoredCreature);

            int damageTakenModPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "DamageTakenMod", 100 * 0x100));
            int damageImmunityPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "DamageImmunity", 0));
            int damageResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "DamageResist", 0));
            int crushingResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "CrushingResist", 0));
            int piercingResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "PiercingResist", 0));
            int slashingResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "SlashingResist", 0));
            int divineResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "DivineResist", creatureData.DivineResistF32));
            int fireResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "FireResist", creatureData.FireResistF32));
            int iceResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "IceResist", creatureData.IceResistF32));
            int poisonResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "PoisonResist", creatureData.PoisonResistF32));
            int shadowResistPercent = GCDatabase.RoundFixed32ToInt(GetAuthoredFixed32(authoredDesc, "ShadowResist", creatureData.ShadowResistF32));
            int magicResistF32 = GetAuthoredFixed32(authoredDesc, "MagicResist", GetAuthoredFixed32(authoredDesc, "MagicDamageResist", 0));

            string runtimeInstanceKey = !string.IsNullOrWhiteSpace(instanceKey) ? instanceKey : zoneName;
            RoomRuntime spawnRuntime = RequireInitializedRoomRuntime(runtimeInstanceKey, "SpawnMonster");
            if (spawnRuntime == null)
            {
                Debug.LogError($"[ROOM-RNG] Blocked monster spawn before client seed gcType='{gcType}' zone='{zoneName}' instance='{runtimeInstanceKey ?? "<null>"}'");
                return null;
            }
            if (!spawnRuntime.Initialized)
            {
                Debug.LogError($"[ROOM-RNG] Blocked monster spawn before client seed gcType='{gcType}' zone='{zoneName}' instance='{spawnRuntime.InstanceKey}'");
                return null;
            }
            SetCurrentRoomRuntime(spawnRuntime.InstanceKey, "SpawnMonster");

            byte tierLevel = GetLevelForTier(creatureDifficulty);
            byte zoneBase = GetZoneBaseLevel(zoneName);
            byte calculatedLevel = (byte)Math.Clamp((int)tierLevel + zoneBase + encounterLevelOffset, 1, 110);
            if (summonData != null)
                calculatedLevel = (byte)Math.Clamp((int)summonLevel.Value, 1, 110);
            Debug.LogError($"[COMBAT] levelCalc tier={creatureDifficulty} tierLevel={tierLevel} zone={zoneName} zoneBase={zoneBase} encounterLevelOffset={encounterLevelOffset} level={calculatedLevel}");

            if (!TryAllocateMonsterComponentIds(
                    out uint entityId,
                    out uint behaviorId,
                    out uint skillsId,
                    out uint manipulatorsId,
                    out uint modifiersId,
                    out uint unitId))
            {
                Debug.LogError($"[COMBAT] creature='{gcType}' state=blocked reason=combat-network-id-exhausted range=0x{CombatNetworkIdMin:X4}-0x{CombatNetworkIdMax:X4}");
                return null;
            }
            if (ServerDiagnostics.IsEnabled("encounterTracking"))
                Debug.LogError($"[ENCOUNTER-TRACK] phase=monster-level entity={entityId} authoredGc='{gcType ?? ""}' authoredPath='{authoredCreature?.CanonicalPath ?? spawnGcType ?? creatureData.gcType ?? ""}' CreatureDifficulty='{creatureDifficulty ?? ""}' zoneBase={zoneBase} encounterLevelOffset={encounterLevelOffset} finalLevelWire={calculatedLevel}");

            bool isAlive = GetAuthoredBool(authoredDesc, "IsAlive", true);
            bool isOneHit = GetAuthoredBool(authoredDesc, "IsOneHit", false);
            uint initHPWire = (!isAlive && isOneHit)
                ? 256u
                : MonsterHealthTable.CalculateHPWireFixed32(calculatedLevel, unitDifficultyF32, maxHealthF32);
            bool useHenchmanCurves = GetAuthoredBool(authoredDesc, "UseHenchmanCurveTables", false);
            if (useHenchmanCurves && (isAlive || !isOneHit))
            {
                int healthCurveF32 = GCDatabase.Instance.RequireCurveValueFixed32("HenchmanHealth", calculatedLevel);
                int scaledHealthF32 = unchecked((int)(((long)maxHealthF32 * healthCurveF32) >> 8));
                int difficultyHealthF32 = unchecked((int)(((long)scaledHealthF32 * unitDifficultyF32) >> 8));
                initHPWire = unchecked((uint)(difficultyHealthF32 >> 8) << 8);
            }
            uint initManaWire = ResolveUnitDescMaxManaWire(authoredDesc);

            int healthRegenF32 = GetAuthoredFixed32(authoredDesc, "HealthRegen", 0);
            int manaRegenF32 = GetAuthoredFixed32(
                authoredDesc,
                authoredDesc != null && authoredDesc.HasProperty("ManaRegen") ? "ManaRegen" : "PowerRegen",
                0);

            if (_nextClientEntityUpdateTick < _combatTick)
                throw new InvalidOperationException($"Monster spawn client entity update tick {_nextClientEntityUpdateTick} precedes simulation tick {_combatTick}");
            uint entityUpdateAdmissionTick = _nextClientEntityUpdateTick;
            uint entityUpdateAdmissionDelayTicks = entityUpdateAdmissionTick - _combatTick;
            bool autoRespawn = GetAuthoredBool(authoredCreature, "AutoRespawn", GetAuthoredBool(authoredDesc, "AutoRespawn", false));
            bool respawnWhenClear = GetAuthoredBool(authoredCreature, "RespawnWhenClear", GetAuthoredBool(authoredDesc, "RespawnWhenClear", true));
            var monster = new Monster
            {
                Summoner = summoner,
                UseHenchmanCurveTables = useHenchmanCurves,
                ZoneAction = GetAuthoredString(authoredDesc, "ZoneAction", ""),
                EntityId = entityId,
                BehaviorId = behaviorId,
                SkillsId = skillsId,
                ManipulatorsId = manipulatorsId,
                ModifiersId = modifiersId,
                UnitId = unitId,
                UnitFlags = 0x6u | (autoRespawn ? 0x10000u : 0u) | (respawnWhenClear ? 0x20000u : 0u),

                GCType = creatureData.gcType,
                SpawnGCType = spawnGcType,
                BehaviourType = creatureData.behaviourType,
                Name = GetAuthoredString(authoredDesc, "Label", creatureData.name),
                Faction = creatureData.faction,
                FactionID = GetAuthoredInt(authoredDesc, "FactionID", 0),
                UnitDescIsAlwaysFriendly = GetAuthoredBool(authoredDesc, "IsAlwaysFriendly", false),

                CreatureType = creatureData.creatureType,
                Element = creatureData.element,
                Tier = creatureDifficulty,
                Level = calculatedLevel,
                StockUnitLifespanF32 = GetAuthoredFixed32(authoredDesc, "Lifespan", 0),
                StockUnitLifespanIncrementF32 = GetAuthoredFixed32(authoredDesc, "LifespanInc", 0),
                DifficultyF32 = unitDifficultyF32,
                ExperienceDifficultyF32 = Math.Max(0, encounterDifficultyF32),
                ExperienceValueMultF32 = experienceValueMultF32,

                MaxHPWire = initHPWire,
                CurrentHPWire = initHPWire,
                UnitDescIsAlive = isAlive,
                MaxManaWire = initManaWire,
                CurrentManaWire = initManaWire,
                BaseDamage = creatureData.baseDamage,
                AttackRatingF32 = GetAuthoredFixed32(authoredDesc, "AttackRating", creatureData.AttackRatingF32),
                DamageModPercent = (int)(((long)GetAuthoredFixed32(authoredDesc, "DamageMod", creatureData.DamageModF32) * 100L) >> 8),
                DamageTakenModPercent = damageTakenModPercent,
                DamageImmunityPercent = damageImmunityPercent,
                DamageResistPercent = damageResistPercent,
                CrushingResistPercent = crushingResistPercent,
                PiercingResistPercent = piercingResistPercent,
                SlashingResistPercent = slashingResistPercent,
                DefenseRatingF32 = GetAuthoredFixed32(authoredDesc, "DefenseRating", creatureData.DefenseRatingF32),
                CritChanceF32 = GetAuthoredFixed32(authoredDesc, "CriticalChance", creatureData.CritChanceF32),
                DivineResistPercent = divineResistPercent,
                FireResistPercent = fireResistPercent,
                IceResistPercent = iceResistPercent,
                PoisonResistPercent = poisonResistPercent,
                ShadowResistPercent = shadowResistPercent,
                MagicDamageResistPercent = GCDatabase.RoundFixed32ToInt(magicResistF32),
                HealthRegenF32 = healthRegenF32,
                HasAuthoredHealthRegen = authoredDesc != null && authoredDesc.HasProperty("HealthRegen"),
                ManaRegenF32 = manaRegenF32,
                HasAuthoredManaRegen = authoredDesc != null && (authoredDesc.HasProperty("ManaRegen") || authoredDesc.HasProperty("PowerRegen")),
                DamageVolatilityF32 = Math.Min(0xF4, Math.Max(0, GetAuthoredFixed32(authoredWeapon, "DamageVolatility", 0x40))),
                WeaponDamageF32 = Math.Max(1, GetAuthoredFixed32(authoredWeapon, "Damage", 0x100)),
                WeaponStunMod = Math.Max(0, GetAuthoredInt(authoredWeapon, "StunMod", 100)),
                StunMod = ResolveUnitDescStunMod(authoredDesc, isAlive),
                StunResist = ResolveUnitDescStunResist(authoredDesc, isAlive),
                WeaponClass = GetAuthoredString(authoredWeapon, "WeaponClass", "HTH"),
                WeaponDamageType = GetAuthoredString(authoredWeapon, "DamageType", "CRUSHING"),
                WeaponUsesProjectile = weaponUsesProjectile,
                WeaponShotType = weaponShotType,
                WeaponProjectileSpeedF32 = weaponProjectileSpeedF32,
                WeaponProjectileSizeF32 = weaponProjectileSizeF32,
                WeaponRangeF32 = weaponDescRangeF32,
                PosFixedX = posFixedX,
                PosFixedY = posFixedY,
                PosFixedZ = posFixedZ,
                SpawnPosFixedX = posFixedX,
                SpawnPosFixedY = posFixedY,
                SpawnPosFixedZ = posFixedZ,
                HeadingFixed = headingFixed,
                WorldEntityAnimationState = 1,
                WorldEntityAnimationId = 100,
                WorldEntityAnimationPlayTime = entityUpdateAdmissionTick,
                WorldEntityAnimationSpeed = 0x100,

                PerceptionRangeF32 = perceptionRangeF32,
                AggroRangeF32 = aggroRangeF32,
                ShoutRangeF32 = shoutRangeF32,
                LeashRangeF32 = leashRangeF32,
                AttackRangeF32 = attackRangeF32,
                UnitDescAttackRangeF32 = unitDescAttackRangeF32,
                ClientSyncToleranceF32 = clientSyncToleranceF32,
                CollisionRadiusF32 = collisionRadiusF32,
                BoundingBoxRadiusXYF32 = boundingBoxRadiusXYF32,
                BoundingBoxMinZF32 = boundingBoxMinZF32,
                BoundingBoxMaxZF32 = boundingBoxMaxZF32,
                SizeModPercent = sizeModPercent,
                AttackType = attackType,
                IdleAction = idleAction,
                LogicType = logicType,
                AttackStyle = attackStyle,
                Retreatable = retreatable,
                RetreatRangeSquaredF32 = retreatRangeSquaredF32,
                Leashed = leashed,
                UseIdleTime = useIdleTime,
                BaseTime = idleBaseTime,
                VariableTime = idleVariableTime,
                AutoScan = autoScan,
                AvoidUnits = avoidUnits,
                TurnBeforeMoving = turnBeforeMoving,
                PlayerControlled = playerControlled,
                CollisionBand = collisionBand,
                CollisionPriority = collisionPriority,
                ScanFrequencyF32 = scanFrequencyF32,
                CorpseLingerTicks = (ushort)Mathf.Clamp(GetAuthoredInt(authoredDesc, "CorpseLingerTime", 900), 0, ushort.MaxValue),
                AutoRespawn = autoRespawn,
                RespawnRateTicks = (ushort)Mathf.Clamp(GetAuthoredInt(authoredCreature, "RespawnRate", GetAuthoredInt(authoredDesc, "RespawnRate", 3600)), 0, ushort.MaxValue),
                RespawnWhenClear = respawnWhenClear,
                AttackSpeedF32 = GetAuthoredFixed32(authoredDesc, "AttackSpeed", 0x100),
                AttackCooldownF32 = attackCooldownF32,
                HasAttackSound = HasAuthoredAttackSound(authoredDesc),
                AttackWeaponSoundCount = CountAuthoredSounds(authoredDesc, "WEAPONATTACK"),
                AttackRepeatSoundCount = CountAuthoredSounds(authoredDesc, "ATTACK"),
                AttackTotalFrames = attackTiming.TotalFrames,
                AttackHitFrames = attackTiming.HitFrames,
                AttackSoundFrames = attackTiming.SoundFrames,
                AttackFrameResolved = attackTiming.VariantResolved,
                AttackTimingSource = attackTiming.Source,
                AttackTimingReason = attackTiming.Reason,
                MoveSpeedF32 = moveSpeedF32,
                WalkSpeedF32 = walkSpeedF32,
                SpeedMod = speedMod,
                BaseSpeedMod = speedMod,
                WanderRangeF32 = wanderRangeF32,

                Manipulators = spawnManipulators,
                ManipulatorOrder = BuildSpawnManipulatorOrder(spawnManipulators),

                State = MonsterState.Idle,
                IsAlive = true,
                ZoneName = zoneName,
                InstanceKey = spawnRuntime.InstanceKey,
                EntityUpdateAdmissionTick = entityUpdateAdmissionTick,
                EntityUpdateAdmissionDelayTicks = entityUpdateAdmissionDelayTicks,
                AuthoredArchetypeAncestry = BuildAuthoredArchetypeAncestry(spawnGcType, creatureData.gcType),
                EncounterGroupKey = encounterGroupKey,
                SpawnBehaviourType = spawnBehaviourType
            };

            monster.WeaponClassId = DamageResolver.ResolveWeaponClassId(monster.WeaponClass);
            monster.DamageTypeId = DamageResolver.ResolveDamageTypeId(monster.WeaponDamageType);
            monster.UnitMoverDesiredHeadingFixed = monster.HeadingFixed;
            monster.UnitMoverDesiredHeadingInit = true;
            int rawSpawnFixedZ = monster.PosFixedZ;
            bool terrainHeightResolved = TryResolveTerrainHeightFixed(
                monster,
                monster.PosFixedX,
                monster.PosFixedY,
                monster.PosFixedZ,
                out int terrainFixedZ,
                out string terrainSource);
            if (terrainHeightResolved)
            {
                monster.PosFixedZ = terrainFixedZ;
                monster.SpawnPosFixedZ = terrainFixedZ;
            }
            Debug.LogError($"[MON-SPAWN-Z] {monster.Name}#{entityId} rawFixedZ={rawSpawnFixedZ} resolved={terrainHeightResolved} terrainFixedZ={terrainFixedZ} appliedFixedZ={monster.PosFixedZ} posFixed=({monster.PosFixedX},{monster.PosFixedY}) source={terrainSource ?? "none"}");
            byte difficulty = 0;
            if (monster.Summoner == null && !DungeonRunners.Gameplay.WorldObjectSpawner.IsDestroyableObject(monster.GCType))
            {
                spawnRuntime.MonsterDifficulty ??= ResolveMonsterDifficulty?.Invoke(monster) ?? 0;
                difficulty = spawnRuntime.MonsterDifficulty.Value;
            }
            MonsterDifficultySettings.ApplyInitial(monster, difficulty);
            MaterializeMonsterUnitSlots(monster);
            InitializeMonsterAiRuntime(monster);
            ResetMonsterClientVisiblePositionFixed(monster, monster.PosFixedX, monster.PosFixedY, "SPAWN");
            _activeMonsters[entityId] = monster;
            RegisterEntityOrder(entityId, entityUpdateAdmissionTick);
            if (summonData != null && summonTransfer != null)
            {
                monster.CurrentHPWire = Math.Min(monster.MaxHPWire, summonTransfer.Health);
                monster.CurrentManaWire = Math.Min(monster.MaxManaWire, summonTransfer.Mana);
                _monsterHPRegenCooldownTicks[entityId] = summonTransfer.HealthCooldown;
                _monsterManaRegenCooldownTicks[entityId] = summonTransfer.ManaCooldown;
            }
            SetRuntimeMonsterHPWire(monster, monster.CurrentHPWire, false, "SPAWN");
            monster.RngSeed = spawnRuntime.Seed;
            monster.Rng = spawnRuntime.RoomRng;
            ConfigureMonsterPrimaryActiveSkill(monster);
            ConfigureMonsterProcModifiers(monster, authoredCreature);
            if (summonData != null)
                ConfigureSummonAuthoredAttributeModifiers(monster, authoredCreature);
            monster.Behavior = new Behavior.MonsterBehavior2();
            if (summonData != null)
                _pendingSummonInitializations.Add(entityId);
            else
                InitializeMonsterClientSimulation(monster);
            Debug.LogError($"[COMBAT] monster='{monster.Name}' roomRuntime='{monster.InstanceKey}' seed=0x{spawnRuntime.Seed:X8}");

            _componentToEntityMap[entityId] = entityId;
            _componentToEntityMap[behaviorId] = entityId;
            _componentToEntityMap[skillsId] = entityId;
            _componentToEntityMap[manipulatorsId] = entityId;
            _componentToEntityMap[modifiersId] = entityId;
            _componentToEntityMap[unitId] = entityId;

            int clientAttackRating = DamageResolver.ResolveMonsterAttackRating(monster);
            int clientDefenseRating = DamageResolver.ResolveMonsterDefenseRating(monster);
            int monsterDamageTableF32 = ResolveMonsterDamageTableF32(monster.Level);
            int effectiveWeaponDamageF32 = Math.Max(0x100, monster.WeaponDamageF32);
            int effectiveDamageModPercent = Math.Max(100, monster.DamageModPercent);
            Debug.LogError($"[COMBAT] spawn name='{monster.Name}' id={entityId} level={calculatedLevel} hp={monster.MaxHP} damage={creatureData.baseDamage} unitDifficultyF32={monster.DifficultyF32} damageModPercent={monster.DamageModPercent} encounterDifficultyF32={monster.ExperienceDifficultyF32} effectiveDamageModPercent={effectiveDamageModPercent}");
            Debug.LogError($"[SPAWN-AUDIT] id={entityId} name='{monster.Name}' baseGc='{monster.GCType}' spawnGc='{monster.SpawnGCType}' zone='{zoneName}' group='{monster.EncounterGroupKey}' level={monster.Level} hpWire={monster.MaxHPWire} manaWire={monster.MaxManaWire} hpRegenF32={monster.HealthRegenF32} manaRegenF32={monster.ManaRegenF32} hpRegenFactor={ResolveMonsterHealthRegenFactor(monster)} manaRegenFactor={ResolveMonsterManaRegenFactor(monster)} unitDiffF32={monster.DifficultyF32} encounterDiffF32={monster.ExperienceDifficultyF32} attackRatingF32={monster.AttackRatingF32} attackRating={clientAttackRating} defenseRatingF32={monster.DefenseRatingF32} defenseRating={clientDefenseRating} critF32={monster.CritChanceF32} damageTableF32={monsterDamageTableF32} damageModPercent={monster.DamageModPercent} weaponClass={monster.WeaponClass}/{monster.WeaponClassId} damageType={monster.WeaponDamageType}/{monster.DamageTypeId} weaponDamageF32={effectiveWeaponDamageF32} volatilityF32={monster.DamageVolatilityF32} aggroF32={monster.AggroRangeF32} wanderF32={monster.WanderRangeF32} posFixed8=({monster.PosFixedX},{monster.PosFixedY},{monster.PosFixedZ})");
            Debug.LogError($"[COMBAT] componentIds entity={entityId} behavior={behaviorId} skills={skillsId} manipulators={manipulatorsId} modifiers={modifiersId} unit={unitId}");
            Debug.LogError($"[MON-ENTITY-ADMISSION] entity={entityId} creationTick={_combatTick} admissionTick={monster.EntityUpdateAdmissionTick} delay={monster.EntityUpdateAdmissionDelayTicks} sourceFunction=ServerEntityManager::updateClients@0x005DF010->updateEntities@0x005DED50");
            Debug.LogError($"[COMBAT] positionFixed=({posFixedX},{posFixedY},{posFixedZ}) perceptionRangeF32={monster.PerceptionRangeF32} aggroRangeF32={monster.AggroRangeF32} shoutRangeF32={monster.ShoutRangeF32} leashRangeF32={monster.LeashRangeF32} attackRangeF32={monster.AttackRangeF32} syncToleranceF32={monster.ClientSyncToleranceF32} collisionRadiusF32={monster.CollisionRadiusF32} boundingBoxRadiusXYF32={monster.BoundingBoxRadiusXYF32} boundingBoxZF32=({monster.BoundingBoxMinZF32},{monster.BoundingBoxMaxZF32}) attackSpeedF32={monster.AttackSpeedF32} cooldownF32={monster.AttackCooldownF32} walkSpeedF32={monster.WalkSpeedF32} wanderRangeF32={monster.WanderRangeF32} group={monster.EncounterGroupKey}");
            Debug.LogError($"[COMBAT] ai attackType={monster.AttackType} idleAction={monster.IdleAction} logicType={monster.LogicType} attackStyle={monster.AttackStyle} retreatable={monster.Retreatable} leashed={monster.Leashed} useIdleTime={monster.UseIdleTime} autoScan={monster.AutoScan} avoidUnits={monster.AvoidUnits} turnBeforeMoving={monster.TurnBeforeMoving} playerControlled={monster.PlayerControlled} collisionBand={monster.CollisionBand} collisionPriority={monster.CollisionPriority} scanFrequencyF32={monster.ScanFrequencyF32} fleeRangeF32={fleeRangeF32} retreatRangeSquaredF32={retreatRangeSquaredF32} teleportFrequencyF32={teleportFrequencyF32} teleportLimboTimeF32={teleportLimboTimeF32} baseTime={monster.BaseTime} variableTime={monster.VariableTime}");
            TraceMonsterState(monster, "spawn", null, -1, monster.AttackRangeF32, "spawn");

            OnMonsterSpawned?.Invoke(monster);
            return monster;
        }

    }
}
