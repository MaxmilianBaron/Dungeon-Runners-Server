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
    {        private void UpdateMonsterWorldEntityAnimationSnapshot(Monster monster)
        {
            if (monster == null)
                return;
            bool moving = monster.UnitMoverMovingThisFrame;
            if (!monster.AggroTriggered && WanderSimulator.Instance.TryGetSnapshot(monster.EntityId, out WanderStateSnapshot wander))
                moving = wander.MovingThisFrame;
            if (moving)
            {
                if (monster.WorldEntityAnimationState != 1)
                    return;
                monster.WorldEntityAnimationState = 2;
                monster.WorldEntityAnimationId = 107;
                monster.WorldEntityAnimationPlayTime = _combatTick;
                monster.WorldEntityAnimationSpeed = 0x100;
                return;
            }
            if (monster.WorldEntityAnimationState != 2)
                return;
            monster.WorldEntityAnimationState = 1;
            monster.WorldEntityAnimationId = 100;
            monster.WorldEntityAnimationPlayTime = _combatTick;
            monster.WorldEntityAnimationSpeed = 0x100;
        }

        private void CompleteStockUnitSpawn(Monster monster)
        {
            monster.UnitFlags |= 0x404;
            monster.StockUnitState = 5;
            monster.SpawnPosFixedX = monster.PosFixedX;
            monster.SpawnPosFixedY = monster.PosFixedY;
            monster.SpawnPosFixedZ = monster.PosFixedZ;
            monster.StockUnitState = 8;
            InitializeMonsterStockUnitLifespan(monster);
            EncounterRuntime encounter = monster.Encounter;
            if (encounter != null)
            {
                encounter.MarkUnitSpawned(monster.EntityId);
                ApplyEncounterGroup(encounter);
            }
            ApplyEncounterMirror(monster);
            OnStockUnitRespawned?.Invoke(monster);
            Debug.LogError($"[MON-DEATH-LIFECYCLE] state=5->8 spawned {monster.Name}#{monster.EntityId} unitFlags=0x{monster.UnitFlags:X8} entityFlags=0x{monster.EntityFlags:X2} encounterLive={monster.EncounterLiveUnitCount} sourceFunction=Spawn::update@0x0052F140->Unit::onSpawned@0x0050B550->StockUnit::onSpawned@0x005038C0");
        }

        private static void AdvanceMonsterUnitMoverFacing(Monster monster)
        {
            if (monster == null || !monster.UnitMoverDesiredHeadingInit)
                return;
            bool translating = monster.SearchForAttackMoveActive
                || monster.FollowRepositionMoveActive
                || monster.FollowFindLineOfSightMoveActive
                || monster.Behavior?.FollowMovesTowardTarget == true
                || monster.Behavior?.UseTargetActive == true;
            Behavior.MonsterBehavior2.ActionSlot currentAction = monster.Behavior?.CurrentAction
                ?? Behavior.MonsterBehavior2.ActionSlot.None;
            if (translating
                || currentAction == Behavior.MonsterBehavior2.ActionSlot.Spawn
                || currentAction == Behavior.MonsterBehavior2.ActionSlot.Wander
                || currentAction == Behavior.MonsterBehavior2.ActionSlot.DamageReaction)
                return;
            int turnRateFixed = UnitMover.TurnRatePerTickFixed(monster.TurnRateDegrees);
            int currentHeadingFixed = monster.ChaseHeadingInit
                ? monster.ChaseHeadingFixed
                : monster.HeadingFixed;
            int nextHeadingFixed = UnitMover.InterpolateHeading(
                currentHeadingFixed,
                monster.UnitMoverDesiredHeadingFixed,
                turnRateFixed);
            monster.ChaseHeadingFixed = nextHeadingFixed;
            monster.ChaseHeadingInit = true;
            monster.HeadingFixed = nextHeadingFixed;
        }

        private void TickMonsterWanderAction(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return;
            if (!WanderSimulator.Instance.IsRegisteredEntity(monster.EntityId))
                return;
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (rng == null)
                return;
            WanderSimulator.Instance.TickEntity(monster.EntityId, rng);
            if (!monster.AggroTriggered)
                ApplyMonsterWanderClientVisiblePosition(monster, "Wander::update-behavior");
        }

        public void CancelMonsterPendingAttack(Monster monster, string reason)
        {
            if (monster == null) return;
            bool hadPending = monster.AttackPending || monster.AttackSoundPending || monster.AttackClientVisible;
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            monster.AttackClientVisible = false;
            monster.AttackActionQueued = false;
            monster.AttackActionAdmissionTick = 0;
            ClearMonsterLocalAttackActionUse(monster);
            monster.AttackContactOnly = false;
            monster.AttackHitResolved = false;
            monster.AttackStartTick = 0;
            monster.AttackCommitTick = 0;
            monster.AttackSoundTick = 0;
            monster.AttackEndTick = 0;
            monster.AttackUseRaw = 0;
            ClearMonsterActiveSkillEffectCycle(monster);
            ClearMonsterAttackCommitTarget(monster);
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntilTick = 0;
            if (monster.State == MonsterState.Attacking)
                monster.State = MonsterState.Combat;
            if (hadPending)
                Debug.LogError($"[MON-ATTACK] canceled pending attack {monster.Name}#{monster.EntityId} reason={reason}");
            TraceMonsterState(monster, "attack-cancel", null, -1, -1, reason);
        }

        public void StopMonsterAttackTarget2Action(Monster monster)
        {
            if (monster == null || !monster.AttackPending)
                return;
            CancelMonsterPendingAttack(monster, "AttackTarget2::stop@0x00523CA0->MeleeWeapon::stop@0x00591910");
        }

        public void StopMonsterRuntimeAttackAction(Monster monster, byte sessionId, string reason)
        {
            if (monster == null || !monster.AttackPending || monster.AttackSessionId != sessionId)
                return;
            monster.AttackSoundPending = false;
            monster.AttackContactOnly = false;
            monster.AttackHitResolved = false;
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
            if (monster.State == MonsterState.Attacking)
                monster.State = MonsterState.Combat;
            Debug.LogError($"[MON-ATTACK-ACTION] stopped runtime {monster.Name}#{monster.EntityId} session={sessionId} reason={reason ?? "unknown"}");
        }

        private static void ClearMonsterAttackCommitTarget(Monster monster)
        {
            if (monster == null)
                return;
            monster.AttackCommitTargetFixedX = 0;
            monster.AttackCommitTargetFixedY = 0;
            monster.AttackCommitTargetFixedInit = false;
        }

        private static void SetMonsterAttackCommitTargetFixed(Monster monster, int targetFixedX, int targetFixedY)
        {
            if (monster == null)
                return;
            monster.AttackCommitTargetFixedX = targetFixedX;
            monster.AttackCommitTargetFixedY = targetFixedY;
            monster.AttackCommitTargetFixedInit = true;
        }

        public void DelayMonsterAttackRetry(Monster monster, string reason)
        {
            if (monster == null) return;
            int cooldownTicks = ResolveMonsterAttackCooldownTicks(monster);
            monster.NextAttackTick = (int)_combatTick + cooldownTicks;
            Debug.LogError($"[MON-ATTACK] retry delayed {monster.Name}#{monster.EntityId} reason={reason} cooldownTicks={cooldownTicks}");
        }

        private bool ShouldLogFarTargetAction(Monster monster)
        {
            if (monster == null) return false;
            if (!_monsterFarTargetActionLogTick.TryGetValue(monster.EntityId, out uint lastTick) || _combatTick - lastTick >= CLIENT_TICKS_PER_SECOND)
            {
                _monsterFarTargetActionLogTick[monster.EntityId] = _combatTick;
                return true;
            }
            return false;
        }

        private void TraceFarTargetAction(Monster monster, CombatTarget target, int distFixed, int allowedRangeFixed, string source)
        {
            if (!ShouldLogFarTargetAction(monster)) return;
            int targetRangeFixed = ResolveMonsterTargetSearchRangeFixed(monster);
            Debug.LogError($"[MON-ATTACK-REACH] target-action-only {monster.Name}#{monster.EntityId}->{target?.Name ?? "player"} source={source ?? "unknown"} distFixed8={distFixed} meleeRangeFixed8={allowedRangeFixed} targetRangeFixed8={targetRangeFixed} action=target-only-no-damage");
            TraceMonsterState(monster, "target-action-only", target, distFixed, allowedRangeFixed, source ?? "reach");
        }

        private bool AttackCommitTargetStillValid(Monster monster, CombatTarget target)
        {
            if (monster == null || target == null) return false;
            if (!monster.AttackCommitTargetFixedInit) return false;
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            int tcx = targetFixedX - monster.AttackCommitTargetFixedX;
            int tcy = targetFixedY - monster.AttackCommitTargetFixedY;
            long distSqFixed8 = (((long)tcx * tcx) >> 8) + (((long)tcy * tcy) >> 8);
            int tolFixed = ResolveAttackCommitTargetToleranceFixed(monster);
            return distSqFixed8 <= (((long)tolFixed * tolFixed) >> 8);
        }

        private static int ResolveAttackCommitTargetToleranceFixed(Monster monster)
        {
            if (monster == null)
                return 0;
            int toleranceFixed = ResolvePositiveFixed8(monster.ClientSyncToleranceF32);
            return toleranceFixed > 0 ? toleranceFixed : 10 * UnitMover.Fixed;
        }

        private bool HasCombatContact(Monster monster, CombatTarget target)
        {
            return monster != null && target != null && monster.CombatContactTargetId == target.EntityId && _combatTick <= monster.CombatContactUntilTick;
        }

        private int ResolveMonsterDamageTableF32(byte level, bool useHenchmanCurves = false)
        {
            return GCDatabase.Instance.RequireCurveValueFixed32(useHenchmanCurves ? "HenchmanDamage" : "MonsterDamage", Math.Clamp((int)level, 1, 110));
        }

        private int ResolveMonsterAttackRating(Monster monster)
        {
            return DamageResolver.ResolveMonsterAttackRating(monster);
        }

        private static uint ApplyDamageTakenModWire(uint damageWire, int damageTakenModPercent)
        {
            if (damageWire == 0) return 0;
            if (damageTakenModPercent < 1) return 0;
            ulong scaled = (ulong)damageWire * (uint)damageTakenModPercent / 100u;
            return scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled;
        }

        private static int ResolveMonsterDamageTypeTakenMod(Monster monster, int damageTypeId)
        {
            if (monster == null) return 100;
            return damageTypeId switch
            {
                3 => monster.FireDamageTakenModPercent,
                4 => monster.IceDamageTakenModPercent,
                5 => monster.PoisonDamageTakenModPercent,
                6 => monster.ShadowDamageTakenModPercent,
                7 => monster.DivineDamageTakenModPercent,
                _ => 100
            };
        }

        private sealed class DamageQueryResult
        {
            public uint AdjustedDamageWire;
            public int DamageTakenMod = 100;
            public int DamageTypeMod = 100;
            public string ResultName = "NONE";
            public uint ResistRaw;
            public int ResistChanceWire;
        }

        private enum DamageResistResult
        {
            None = 0,
            Immune = 1,
            Resisted = 2,
            Vulnerable = 3
        }

        public bool ResolveSpellDamageResistance(Monster monster, int damageTypeId, int attackerLevel, string source, out bool vulnerable)
        {
            DamageResistResult result = CheckMonsterDamageResist(monster, 3, damageTypeId, 0x100, attackerLevel, out _, out _, source);
            vulnerable = result == DamageResistResult.Vulnerable;
            return result != DamageResistResult.Immune && result != DamageResistResult.Resisted;
        }

        private static DamageQueryResult ApplyDamageQueryWire(uint damageWire, Monster monster, int damageTypeId, byte damageKind, string source, int attackerLevel, bool spellEffectResistResolved = false, bool spellEffectVulnerable = false)
        {
            var result = new DamageQueryResult
            {
                AdjustedDamageWire = damageWire,
                DamageTakenMod = monster != null ? monster.DamageTakenModPercent : 100,
                DamageTypeMod = ResolveMonsterDamageTypeTakenMod(monster, damageTypeId),
                ResultName = "NONE"
            };

            result.AdjustedDamageWire = ApplyDamageTakenModWire(result.AdjustedDamageWire, result.DamageTakenMod);
            if (damageTypeId >= 3 && damageTypeId <= 7 && result.DamageTypeMod != 100)
            {
                if (result.DamageTypeMod < 1)
                {
                    result.AdjustedDamageWire = 0;
                    result.ResultName = "TYPE_ABSORB";
                    return result;
                }
                result.AdjustedDamageWire = ApplyDamageTakenModWire(result.AdjustedDamageWire, result.DamageTypeMod);
            }

            if (result.AdjustedDamageWire == 0 || monster == null)
                return result;

            if (spellEffectResistResolved)
            {
                result.ResultName = spellEffectVulnerable ? "VULNERABLE" : "NONE";
                return result;
            }

            DamageResistResult resist = CheckMonsterDamageResist(monster, damageKind, damageTypeId, 0x100, attackerLevel, out int resistChanceWire, out uint resistRaw, source);
            result.ResistChanceWire = resistChanceWire;
            result.ResistRaw = resistRaw;
            result.ResultName = resist.ToString().ToUpperInvariant();
            if (resist == DamageResistResult.Immune || resist == DamageResistResult.Resisted)
            {
                result.AdjustedDamageWire = 0;
                return result;
            }
            if (resist == DamageResistResult.Vulnerable)
                result.AdjustedDamageWire = ClampWireAdd(result.AdjustedDamageWire, result.AdjustedDamageWire);
            return result;
        }

        public bool ApplySpellKnockDownEffectToMonster(MersenneTwister rng, Monster target, int attackerLevel, int strength, int chanceWire, string marker, uint sourceEntityId = 0, bool useWeaponImpactPosition = false, (int X, int Y)? effectSourcePosition = null)
        {
            if (target == null || !target.IsAlive)
                return false;
            string source = marker ?? "SPELL";
            if (rng == null)
            {
                Debug.LogError($"[SPELL-KNOCKDOWN] target={target.Name}#{target.EntityId} result=NO_RNG reason=missing-rng source={source} sourceFunction=SpellKnockDownEffect::doEffect@0x00553360 Unit::CheckStunResist@0x0050C630");
                return false;
            }

            int normalizedChance = Mathf.Clamp(chanceWire, 0, 0x6400);
            uint chanceRaw = 0;
            uint chanceRoll = 0;
            if (normalizedChance < 0x6400)
            {
                chanceRaw = RngLedger.Generate(rng, "unitOwnedCombat", $"{source}:SpellKnockDownEffect::CheckChance", "SpellEffect::CheckChance");
                chanceRoll = chanceRaw % 0x6464u;
                if (chanceRoll >= (uint)normalizedChance)
                {
                    Debug.LogError($"[SPELL-KNOCKDOWN] target={target.Name}#{target.EntityId} result=CHANCE_FAIL strength={strength} chanceWire={normalizedChance} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} rngPos={rng.CallsSinceReseed} source={source} sourceFunction=SpellEffect::CheckChance@0x00545FF0 SpellKnockDownEffect::doEffect@0x00553360");
                    return false;
                }
            }

            int stunResistWire = ResolveMonsterStunResistChanceWire(target, attackerLevel);
            uint stunRaw = RngLedger.Generate(rng, "unitOwnedCombat", $"{source}:SpellKnockDownEffect::CheckStunResist", "Unit::CheckStunResist");
            uint stunRoll = stunRaw % 0x6400u;
            if (stunRoll < (uint)Mathf.Max(0, stunResistWire))
            {
                Debug.LogError($"[SPELL-KNOCKDOWN] target={target.Name}#{target.EntityId} result=RESIST strength={strength} chanceWire={normalizedChance} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} stunResistWire={stunResistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} source={source} sourceFunction=SpellKnockDownEffect::doEffect@0x00553360 SpellEffect::CheckChance@0x00545FF0 Unit::CheckStunResist@0x0050C630");
                return false;
            }

            if (!StartMonsterDamageReaction(target, 0x0B, strength, sourceEntityId, $"{source}-SpellKnockDownEffect", !useWeaponImpactPosition,
                    effectSourcePosition.HasValue, effectSourcePosition?.X ?? 0, effectSourcePosition?.Y ?? 0))
            {
                Debug.LogError($"[SPELL-KNOCKDOWN] target={target.Name}#{target.EntityId} result=ACTION_REJECTED actionId=0x0B strength={strength} chanceWire={normalizedChance} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} stunResistWire={stunResistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} source={source} sourceFunction=SpellKnockDownEffect::doEffect@0x00553360 Behavior::doInterruptLocal@0x00515290");
                return false;
            }
            int stepFixed = (int)(((long)strength << 8) / CLIENT_TICKS_PER_SECOND);
            Debug.LogError($"[SPELL-KNOCKDOWN] target={target.Name}#{target.EntityId} result=ACTION actionId=0x0B strength={strength} heading={target.DamageReactionHeadingFixed} stepFixed={stepFixed} moveTicks={target.DamageReactionMoveTicksRemaining} chanceWire={normalizedChance} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} stunResistWire={stunResistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} startTick={_combatTick} rngPos={rng.CallsSinceReseed} source={source} sourceFunction=SpellKnockDownEffect::doEffect@0x00553360 Unit::CheckStunResist@0x0050C630 KnockDown::EnterKnockDown@0x0052A5E0");
            return true;
        }

        private static int ResolveMonsterStunResistChanceWire(Monster target, int attackerLevel)
        {
            int resist = (target?.Slots?.Get(UnitSlot.StunResist) ?? 0) * 0x100;
            int targetLevel = target?.Level ?? 0;
            int diff = (targetLevel * 0x100) - (Math.Max(1, attackerLevel) * 0x100);
            if (diff > 0x500)
                resist += (int)(((long)(diff - 0x500) * 0x500) >> 8);
            if (resist < 0x500) resist = 0x500;
            return resist;
        }

        private static DamageResistResult CheckMonsterDamageResist(Monster monster, byte damageKind, int damageTypeId, int fixedScale, int attackerLevel, out int resistChanceWire, out uint resistRaw, string source)
        {
            resistChanceWire = 0;
            resistRaw = 0;
            if (monster?.Slots == null)
                return DamageResistResult.None;

            if (unchecked(monster.Slots.Get(UnitSlot.DamageImmunity) + monster.GetActiveAttributeModifierValue("DAMAGE_IMMUNITY")) > 0)
            {
                resistChanceWire = 0x6400;
                Debug.LogError($"[DAMAGE-RESIST] target={monster.Name}#{monster.EntityId} result=IMMUNE chance=100.00 raw=0x00000000 damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} sourceFunction=Unit::CheckDamageResist@0x0050B660");
                return DamageResistResult.Immune;
            }

            int resistWire = monster.Slots.Get(UnitSlot.DamageResist) * 0x100;
            if (damageKind == 3)
                resistWire += monster.Slots.Get(UnitSlot.MagicResist) * 0x100;
            resistWire += ResolveMonsterDamageTypeResist(monster, damageTypeId) * 0x100;

            long scaledResist = ((long)resistWire * fixedScale) >> 8;
            attackerLevel = Math.Max(1, attackerLevel);
            int denom = Math.Max(1, attackerLevel * 0x0D00);
            long q = (scaledResist << 8) / denom;
            resistChanceWire = (int)((q * 0x4B00L) >> 8);
            int upperCapWire = 0x6400;
            if (damageKind == 3 && monster.UnitDescIsAlive && resistChanceWire < 0x6400)
            {
                resistChanceWire += ((int)monster.Level - attackerLevel) * 5 * 0x100;
                upperCapWire = 0x5A00;
                if (resistChanceWire > 0 && resistChanceWire < 0x500)
                    resistChanceWire = 0x500;
            }
            if (resistChanceWire > upperCapWire) resistChanceWire = upperCapWire;
            if (resistChanceWire >= 0x6400)
                return DamageResistResult.Immune;

            MersenneTwister rng = monster.Rng;
            if (rng == null)
            {
                Debug.LogError($"[DAMAGE-RESIST] target={monster.Name}#{monster.EntityId} result=NO_RNG chance={resistChanceWire / 256f:F2} raw=0x00000000 damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} reason=missing-unit-owned-rng sourceFunction=Unit::CheckDamageResist@0x0050B660");
                return DamageResistResult.None;
            }

            resistRaw = RngLedger.Generate(rng, "unitOwnedCombat", "Unit::CheckDamageResist", $"{monster.Name}#{monster.EntityId}");
            int roll = (int)(resistRaw % 0x6400);
            DamageResistResult result;
            if (resistChanceWire < 0)
                result = roll < -resistChanceWire ? DamageResistResult.Vulnerable : DamageResistResult.None;
            else
                result = roll < resistChanceWire ? DamageResistResult.Resisted : DamageResistResult.None;
            Debug.LogError($"[DAMAGE-RESIST] target={monster.Name}#{monster.EntityId} result={result.ToString().ToUpperInvariant()} chance={resistChanceWire / 256f:F2} roll={roll / 256f:F2} raw=0x{resistRaw:X8} damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} rngPos={rng.CallsSinceReseed} sourceFunction=Unit::CheckDamageResist@0x0050B660");
            return result;
        }

        private static int ResolveMonsterDamageTypeResist(Monster monster, int damageTypeId)
        {
            if (monster?.Slots == null) return 0;
            return damageTypeId switch
            {
                0 => monster.Slots.Get(UnitSlot.CrushingResist),
                1 => monster.Slots.Get(UnitSlot.PiercingResist),
                2 => monster.Slots.Get(UnitSlot.SlashingResist),
                3 => monster.Slots.Get(UnitSlot.FireResist),
                4 => monster.Slots.Get(UnitSlot.IceResist),
                5 => monster.Slots.Get(UnitSlot.PoisonResist),
                6 => monster.Slots.Get(UnitSlot.ShadowResist),
                7 => monster.Slots.Get(UnitSlot.DivineResist),
                _ => 0
            };
        }


        private static DamageQueryResult ApplyPlayerDamageQueryWire(uint damageWire, CombatTarget target, Monster attacker, int damageTypeId, byte damageKind, string source, int attackerLevel, MersenneTwister rng, bool resistable)
        {
            if (target?.Monster != null)
                return ApplyDamageQueryWire(damageWire, target.Monster, damageTypeId, damageKind, source, attackerLevel);
            PlayerState state = target?.PlayerState;
            var result = new DamageQueryResult
            {
                AdjustedDamageWire = damageWire,
                DamageTakenMod = state != null ? state.DamageTakenMod : 100,
                DamageTypeMod = ResolvePlayerDamageTypeTakenMod(state, damageTypeId),
                ResultName = "NONE"
            };

            if (state != null && state.HasAnyDamageImmunity)
            {
                result.AdjustedDamageWire = 0;
                result.ResultName = "IMMUNE";
                result.ResistChanceWire = 0x6400;
                Debug.LogError($"[DAMAGE-RESIST] target={target.Name}#{target.EntityId} result=IMMUNE chance=100.00 raw=0x00000000 damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} sourceFunction=Unit::CheckDamageResist@0x0050B660 target=Avatar");
                return result;
            }

            result.AdjustedDamageWire = ApplyDamageTakenModWire(result.AdjustedDamageWire, result.DamageTakenMod);
            if (damageTypeId >= 3 && damageTypeId <= 7 && result.DamageTypeMod != 100)
            {
                if (result.DamageTypeMod < 1)
                {
                    result.AdjustedDamageWire = 0;
                    result.ResultName = "TYPE_ABSORB";
                    return result;
                }
                result.AdjustedDamageWire = ApplyDamageTakenModWire(result.AdjustedDamageWire, result.DamageTypeMod);
            }

            if (result.AdjustedDamageWire == 0 || target == null)
                return result;

            if (!resistable)
                return result;

            DamageResistResult resist = CheckPlayerDamageResist(target, attacker, damageKind, damageTypeId, 0x100, attackerLevel, rng, out int resistChanceWire, out uint resistRaw, source);
            result.ResistChanceWire = resistChanceWire;
            result.ResistRaw = resistRaw;
            result.ResultName = resist.ToString().ToUpperInvariant();
            if (resist == DamageResistResult.Immune || resist == DamageResistResult.Resisted)
            {
                result.AdjustedDamageWire = 0;
                return result;
            }
            if (resist == DamageResistResult.Vulnerable)
                result.AdjustedDamageWire = ClampWireAdd(result.AdjustedDamageWire, result.AdjustedDamageWire);
            return result;
        }

        private static DamageResistResult CheckPlayerDamageResist(CombatTarget target, Monster attacker, byte damageKind, int damageTypeId, int fixedScale, int attackerLevel, MersenneTwister rng, out int resistChanceWire, out uint resistRaw, string source)
        {
            resistChanceWire = 0;
            resistRaw = 0;
            PlayerState state = target?.PlayerState;
            if (state == null)
                return DamageResistResult.None;

            int resistWire = GetEquipmentStatSum(state, "DAMAGE_RESIST", "DAMAGERESIST") * 0x100;
            if (damageKind == 3)
                resistWire += GetEquipmentStatSum(state, "MAGIC_DAMAGE_RESIST", "MAGIC_RESIST", "MAGICDAMAGERESIST", "MAGICRESIST") * 0x100;
            resistWire += ResolvePlayerDamageTypeResist(state, damageTypeId) * 0x100;

            long scaledResist = ((long)resistWire * fixedScale) >> 8;
            attackerLevel = Math.Max(1, attackerLevel);
            int denom = Math.Max(1, attackerLevel * 0x0D00);
            long q = (scaledResist << 8) / denom;
            resistChanceWire = (int)((q * 0x4B00L) >> 8);
            if (resistChanceWire > 0x5A00) resistChanceWire = 0x5A00;
            if (resistChanceWire < -0x6400) resistChanceWire = -0x6400;

            if (rng == null)
            {
                Debug.LogError($"[DAMAGE-RESIST] target={target.Name}#{target.EntityId} result=NO_RNG chance={resistChanceWire / 256f:F2} raw=0x00000000 damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} reason=missing-world-rng sourceFunction=Unit::CheckDamageResist@0x0050B660 target=Avatar attacker={attacker?.Name ?? "unknown"}#{attacker?.EntityId ?? 0}");
                return DamageResistResult.None;
            }

            resistRaw = RngLedger.Generate(rng, "unitOwnedCombat", "Unit::CheckDamageResist", $"{target.Name}#{target.EntityId}");
            int roll = (int)(resistRaw % 0x6400);
            DamageResistResult result;
            if (resistChanceWire < 0)
                result = roll < -resistChanceWire ? DamageResistResult.Vulnerable : DamageResistResult.None;
            else
                result = roll < resistChanceWire ? DamageResistResult.Resisted : DamageResistResult.None;
            Debug.LogError($"[DAMAGE-RESIST] target={target.Name}#{target.EntityId} result={result.ToString().ToUpperInvariant()} chance={resistChanceWire / 256f:F2} roll={roll / 256f:F2} raw=0x{resistRaw:X8} damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} rngPos={rng.CallsSinceReseed} sourceFunction=Unit::CheckDamageResist@0x0050B660 target=Avatar attacker={attacker?.Name ?? "unknown"}#{attacker?.EntityId ?? 0}");
            return result;
        }

        private static int ResolvePlayerDamageTypeResist(PlayerState state, int damageTypeId)
        {
            int authoredModifier = damageTypeId switch
            {
                0 => state?.GetActiveAttributeModifierValue("CRUSHING_DAMAGE_RESIST") ?? 0,
                1 => state?.GetActiveAttributeModifierValue("PIERCING_DAMAGE_RESIST") ?? 0,
                2 => state?.GetActiveAttributeModifierValue("SLASHING_DAMAGE_RESIST") ?? 0,
                3 => state?.GetActiveAttributeModifierValue("FIRE_DAMAGE_RESIST") ?? 0,
                4 => state?.GetActiveAttributeModifierValue("ICE_DAMAGE_RESIST") ?? 0,
                5 => state?.GetActiveAttributeModifierValue("POISON_DAMAGE_RESIST") ?? 0,
                6 => state?.GetActiveAttributeModifierValue("SHADOW_DAMAGE_RESIST") ?? 0,
                7 => state?.GetActiveAttributeModifierValue("DIVINE_DAMAGE_RESIST") ?? 0,
                _ => 0
            };
            return authoredModifier + (damageTypeId switch
            {
                0 => GetEquipmentStatSum(state, "CRUSHING_DAMAGE_RESIST", "CRUSHINGDAMAGERESIST"),
                1 => GetEquipmentStatSum(state, "PIERCING_DAMAGE_RESIST", "PIERCINGDAMAGERESIST"),
                2 => GetEquipmentStatSum(state, "SLASHING_DAMAGE_RESIST", "SLASHINGDAMAGERESIST"),
                3 => GetEquipmentStatSum(state, "FIRE_DAMAGE_RESIST", "FIREDAMAGERESIST"),
                4 => GetEquipmentStatSum(state, "ICE_DAMAGE_RESIST", "ICEDAMAGERESIST", "COLD_DAMAGE_RESIST", "COLDDAMAGERESIST"),
                5 => GetEquipmentStatSum(state, "POISON_DAMAGE_RESIST", "POISONDAMAGERESIST"),
                6 => GetEquipmentStatSum(state, "SHADOW_DAMAGE_RESIST", "SHADOWDAMAGERESIST"),
                7 => GetEquipmentStatSum(state, "DIVINE_DAMAGE_RESIST", "DIVINEDAMAGERESIST"),
                _ => 0
            });
        }

        private static int ResolvePlayerDamageTypeTakenMod(PlayerState state, int damageTypeId)
        {
            int mod = damageTypeId switch
            {
                3 => GetEquipmentStatSum(state, "FIRE_DAMAGE_TAKEN_MOD", "FIREDAMAGETAKENMOD"),
                4 => GetEquipmentStatSum(state, "ICE_DAMAGE_TAKEN_MOD", "ICEDAMAGETAKENMOD", "COLD_DAMAGE_TAKEN_MOD", "COLDDAMAGETAKENMOD"),
                5 => GetEquipmentStatSum(state, "POISON_DAMAGE_TAKEN_MOD", "POISONDAMAGETAKENMOD"),
                6 => GetEquipmentStatSum(state, "SHADOW_DAMAGE_TAKEN_MOD", "SHADOWDAMAGETAKENMOD"),
                7 => GetEquipmentStatSum(state, "DIVINE_DAMAGE_TAKEN_MOD", "DIVINEDAMAGETAKENMOD"),
                _ => 0
            };
            return 100 + mod;
        }

        private static int DamageResistLogCode(DamageQueryResult query)
        {
            if (query == null || query.ResultName == "NONE") return 0;
            return query.ResultName == "VULNERABLE" ? 3 : 1;
        }

        private static int GetEquipmentStatSum(PlayerState state, params string[] keys)
        {
            if (state?.EquipmentStats == null || keys == null || keys.Length == 0) return 0;
            int total = 0;
            var consumed = new List<string>();
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                string key = keys[keyIndex];
                if (!string.IsNullOrWhiteSpace(key) && !consumed.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    consumed.Add(key);
                    if (state.EquipmentStats.TryGetValue(key, out int value))
                        total += value;
                }
                string canonicalKey = CanonicalStatName(key);
                if (!string.IsNullOrEmpty(canonicalKey) && !consumed.Contains(canonicalKey, StringComparer.OrdinalIgnoreCase))
                {
                    consumed.Add(canonicalKey);
                    if (state.EquipmentStats.TryGetValue(canonicalKey, out int canonicalValue))
                        total += canonicalValue;
                }
            }
            return total;
        }

        private static string CanonicalStatName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
        }

        private int ResolveAvatarDefenseRating(PlayerState state, Monster attacker)
        {
            return ResolveAvatarDefenseRating(state, IsRangedWeaponClass(ResolveMonsterWeaponClass(attacker)));
        }

        public int ResolveAvatarDefenseRating(PlayerState state, bool rangedAttacker)
        {
            if (state == null) return 0;
            int defensePerStrengthF32 = GCDatabase.Instance.GetRequiredKnobFixed32("DefenseRatingPerStrength");
            long strengthRatingF32 = (long)Math.Max(0, state.Strength) * defensePerStrengthF32;
            int strengthRating = GCDatabase.RoundFixed32ToInt((int)Math.Min(int.MaxValue, strengthRatingF32));
            int rating = strengthRating + state.ArmorDefenseRating;
            int modifier = GetEquipmentStat(state, "DEFENSE_RATING_MOD");
            if (rangedAttacker)
            {
                rating += GetEquipmentStat(state, "RANGE_DEFENSE_RATING");
                modifier += GetEquipmentStat(state, "RANGE_DEFENSE_RATING_MOD");
            }
            else
            {
                rating += GetEquipmentStat(state, "MELEE_DEFENSE_RATING");
                modifier += GetEquipmentStat(state, "MELEE_DEFENSE_RATING_MOD");
            }
            return Mathf.Max(0, (int)(((long)Mathf.Max(0, rating) * (modifier + 100)) / 100));
        }

        private static int GetEquipmentStat(PlayerState state, string key)
        {
            if (state?.EquipmentStats == null || string.IsNullOrEmpty(key)) return 0;
            return state.EquipmentStats.TryGetValue(key, out int value) ? value : 0;
        }

        private static string ResolveMonsterWeaponClass(Monster monster)
        {
            if (!string.IsNullOrWhiteSpace(monster?.WeaponClass))
                return monster.WeaponClass;

            if (monster?.Manipulators != null &&
                monster.Manipulators.TryGetValue("primaryweapon", out var weapon) &&
                weapon?.properties != null &&
                weapon.properties.TryGetValue("WeaponClass", out var weaponClass) &&
                !string.IsNullOrWhiteSpace(weaponClass))
                return weaponClass;

            return monster?.BehaviourType?.IndexOf("ranged", StringComparison.OrdinalIgnoreCase) >= 0 ? "1HRANGED" : "HTH";
        }

        private static bool IsRangedWeaponClass(string weaponClass)
        {
            if (string.IsNullOrEmpty(weaponClass)) return false;
            return weaponClass.Equals("1HRANGED", StringComparison.OrdinalIgnoreCase)
                || weaponClass.Equals("2HRANGED", StringComparison.OrdinalIgnoreCase)
                || weaponClass.Equals("2HCANNON", StringComparison.OrdinalIgnoreCase);
        }

        public int ResolveAvatarBlockChance(PlayerState state)
        {
            if (state == null || state.EquipmentStats == null) return 0;
            return state.EquipmentStats.TryGetValue("BLOCK", out int block) ? Mathf.Clamp(block, 0, 100) : 0;
        }

        private int ResolveHitThreshold(int attackRating, int defenseRating, int attackerLevel, int defenderLevel)
        {
            return DamageResolver.ResolveHitThreshold(attackRating, defenseRating, attackerLevel, defenderLevel);
        }

        private WeaponDamageInput CreateMonsterWeaponDamageInput(Monster monster, CombatTarget target, MersenneTwister rng, string source)
        {
            int attackerLevel = monster != null ? Mathf.Clamp(monster.Level, 0, 110) : 1;
            int defenderLevel = target != null ? Mathf.Clamp(target.Level, 0, 110) : attackerLevel;
            int baseDamageF32 = monster != null ? ResolveMonsterDamageTableF32(monster.Level, monster.UseHenchmanCurveTables) : 0x100;
            baseDamageF32 = unchecked((int)(((long)baseDamageF32 * GCDatabase.Instance.GetRequiredKnobFixed32("DPSModifier")) >> 8));
            int damageModPercent = monster != null && monster.DamageModPercent > 0
                ? monster.DamageModPercent
                : 100;
            damageModPercent = Math.Max(0, damageModPercent
                + (monster?.GetActiveAttributeModifierValue("DAMAGE_MOD") ?? 0)
                + (monster?.GetActiveAttributeModifierValue("MELEE_DAMAGE_MOD") ?? 0));
            int weaponScaleF32 = monster != null && monster.WeaponDamageF32 > 0
                ? monster.WeaponDamageF32
                : 0x100;
            int volatilityF32 = monster != null && monster.DamageVolatilityF32 > 0
                ? Math.Min(0xF4, monster.DamageVolatilityF32)
                : 0x80;

            return new WeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerEntityId = monster?.EntityId,
                DefenderEntityId = target?.EntityId,
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = ResolveMonsterAttackRating(monster),
                DefenseRating = target?.Monster != null ? DamageResolver.ResolveMonsterDefenseRating(target.Monster) : ResolveAvatarDefenseRating(target?.PlayerState, monster),
                BlockChance = target?.Monster != null ? target.Monster.Slots.Get(UnitSlot.BlockChance) : ResolveAvatarBlockChance(target?.PlayerState),
                DamageLevel = Math.Max(1, baseDamageF32 >> 8),
                DamageBonus = 0,
                DamageMod = MonsterDifficultySettings.ScaleWeaponDamageMod(damageModPercent, monster?.DamageFactorF32 ?? 0x100),
                WeaponClassId = monster != null ? monster.WeaponClassId : 1,
                DamageTypeId = monster != null ? monster.DamageTypeId : 0,
                WeaponDamageF32 = weaponScaleF32,
                WeaponVolatilityF32 = volatilityF32,
                CritThreshold = DamageResolver.ResolveMonsterCriticalThreshold(monster, target?.Monster != null ? attackerLevel : defenderLevel),
                IgnoreLevelDifference = target?.Monster != null,
                CritDamagePercent = 200
            };
        }

        private bool MonsterUsesProjectileWeapon(Monster monster)
        {
            return monster != null && monster.WeaponUsesProjectile && monster.WeaponProjectileSpeedF32 > 0;
        }

        private bool TryDeferMonsterProjectileImpact(Monster monster, CombatTarget target, int distF32, string marker, string source)
        {
            if (!MonsterUsesProjectileWeapon(monster) || target == null || !target.HasUnitState)
                return false;

            TryGetMonsterClientVisiblePositionFixed(monster, target.EntityId, out int startFixedX, out int startFixedY);
            ResolveMonsterActionTargetPositionFixed(target, out int targetFixedX, out int targetFixedY, out _);
            int pathDistanceF32 = UnitMover.IntSqrt(
                (long)(targetFixedX - startFixedX) * (targetFixedX - startFixedX)
                + (long)(targetFixedY - startFixedY) * (targetFixedY - startFixedY));
            int weaponRangeF32 = monster.WeaponRangeF32 > 0 ? monster.WeaponRangeF32 : Math.Max(0, ResolveMonsterEffectiveAttackRangeFixed(monster));
            int flightDistanceF32 = weaponRangeF32 > 0 ? Math.Min(pathDistanceF32, weaponRangeF32) : pathDistanceF32;
            int speedF32 = monster.WeaponProjectileSpeedF32;
            int fireTick = (int)_combatTick;
            int flightTicks = WeaponUseRuntime.ProjectileFlightTicksFixed32(flightDistanceF32, speedF32);
            int firstCollisionTick = fireTick + 1;
            int dueTick = Math.Max(firstCollisionTick, fireTick + flightTicks);
            var pending = new PendingMonsterProjectileImpact
            {
                Sequence = ++_nextMonsterProjectileSequence,
                Monster = monster,
                MonsterEntityId = monster.EntityId,
                TargetEntityId = target.EntityId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(monster.InstanceKey),
                ZoneName = monster.ZoneName,
                Marker = marker,
                Source = source,
                DistF32 = Math.Max(0, distF32),
                FireTick = fireTick,
                FlightTicks = flightTicks,
                DueTick = dueTick
            };
            _pendingMonsterProjectiles.Add(pending);
            RegisterClientSubEntity(ClientSubEntityKind.MonsterProjectile, pending.Sequence, pending.InstanceKey, RemoveMonsterProjectileRuntime);

            Debug.LogError($"[MON-PROJECTILE] {monster.Name}#{monster.EntityId}->{target.Name} schedule seq={pending.Sequence} marker={marker} source={source} fireTick={fireTick} firstCollisionTick={firstCollisionTick} flightTicks={flightTicks} dueTick={dueTick} distF32={distF32} pathDistF32={pathDistanceF32} flightDistF32={flightDistanceF32} speedF32={monster.WeaponProjectileSpeedF32} sizeF32={monster.WeaponProjectileSizeF32} rangeF32={weaponRangeF32} sourceFunction=RangedWeapon::doHit@0x00595DD0->Projectile::init@0x005934D0 commitAtImpact=Projectile::doImpact@0x00594430->Weapon::applyDamage@0x00597E50");
            return true;
        }

        public void ClearMonsterProjectilesForMonster(uint monsterEntityId)
        {
            if (monsterEntityId == 0)
                return;
            if (_pendingMonsterProjectiles.Count > 0)
            {
                foreach (var pending in _pendingMonsterProjectiles)
                    if (pending != null && pending.MonsterEntityId == monsterEntityId)
                        RemoveClientSubEntity(ClientSubEntityKind.MonsterProjectile, pending.Sequence, pending.InstanceKey);
                _pendingMonsterProjectiles.RemoveAll(pending => pending != null && pending.MonsterEntityId == monsterEntityId);
            }
            ClearMonsterChainsForMonster(monsterEntityId);
        }

        private void RemoveMonsterProjectileRuntime(long sequence)
        {
            _pendingMonsterProjectiles.RemoveAll(pending => pending != null && pending.Sequence == sequence);
        }

        private void ResolveMonsterAttackDamage(Monster monster, CombatTarget target, int distF32, string marker, string source, bool applyActiveSkillEffect = true)
        {
            if (DungeonRunners.Core.RuntimeEvidence.TrackingEnabled)
                DungeonRunners.Core.RuntimeEvidence.LogForPlayer(target.Name, "[MON-HIT]", $"monster={monster.Name}#{monster.EntityId} distF32={distF32} target={target.EntityId} targetHp={target.CurrentHPWire} source={source ?? marker}");
            if (applyActiveSkillEffect && TryApplyMonsterActiveSkillEffect(monster, target, distF32, marker, source, out bool activeSkillHandled, out bool activeSkillHPShifted))
            {
                target.IsAlive = target.CurrentHPWire > 0;
                OnMonsterAttackResolved?.Invoke(monster, target, activeSkillHPShifted, target.CurrentHPWire);
                return;
            }

            RoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            if (runtime == null)
            {
                Debug.LogError($"[MON-DAMAGE-ENTITY-SYNCH-INFO] {monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} missing room runtime source={source ?? "unknown"}");
                return;
            }
            MersenneTwister rng = runtime.RoomRng;
            if (rng == null)
            {
                Debug.LogError($"[MON-DAMAGE-ENTITY-SYNCH-INFO] {monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} missing room RNG instance='{runtime.InstanceKey}' source={source ?? "unknown"}");
                return;
            }

            WeaponDamageInput damageInput = CreateMonsterWeaponDamageInput(monster, target, rng, source);
            WeaponDamageResult damageResult = DamageResolver.ResolveWeaponDamage(damageInput);
            int attackRating = damageResult.AttackRating;
            int defenseRating = damageResult.DefenseRating;
            int defenderLevel = damageResult.DefenderLevel;
            int attackerLevel = damageResult.AttackerLevel;
            int hitThreshold = damageResult.HitThreshold;
            uint hitRaw = damageResult.HitRaw;
            uint blockRaw = damageResult.BlockRaw;
            int hitRoll = damageResult.HitRoll;
            int blockRoll = damageResult.BlockRoll;
            int blockChance = damageResult.BlockChance;
            bool hit = damageResult.IsHit;
            bool blocked = damageResult.IsBlocked;
            if (hit && !blocked)
            {
                uint damageRaw = damageResult.DamageRaw;
                uint damageWire = damageResult.DamageWire;
                int minDamage = damageResult.MinDamageF32;
                int maxDamage = damageResult.MaxDamageF32;
                int averageDamage = (minDamage + maxDamage) / 2;
                uint currentHPWire = target.CurrentHPWire;
                DamageQueryResult query = ApplyPlayerDamageQueryWire(damageWire, target, monster, damageResult.DamageTypeId, 0, source ?? marker, attackerLevel, rng, true);
                uint adjustedDamageWire = query.AdjustedDamageWire;
                if (adjustedDamageWire == 0)
                {
                    uint sameHPWire = target.CurrentHPWire;
                    target.IsAlive = sameHPWire > 0;
                    Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} no-damage source={source} damageWire={damageWire} adjustedWire=0 hpWire={sameHPWire}/{target.MaxHPWire} rangeWire=[{minDamage},{maxDamage}] avgWire={averageDamage} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} hitThreshold={hitThreshold} rngPos={rng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dmgRaw=0x{damageRaw:X8} distF32={distF32} result={query.ResultName}");
                    Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source={source ?? marker ?? "monster"} target={target.Name}#{target.EntityId} weaponClassId={damageResult.WeaponClassId} damageTypeId={damageResult.DamageTypeId} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire=0 hp={currentHPWire}->{sameHPWire}/{target.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} clientTick={_combatTick} target=Avatar");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result={query.ResultName} damageWire=0 preQueryWire={damageWire} hp={sameHPWire}->{sameHPWire}/{target.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} damageRaw=0x{damageRaw:X8} rngSeed=0x{runtime.Seed:X8} rngPos={rng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result={query.ResultName} damageWire=0 hp={sameHPWire}->{sameHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} damageRaw=0x{damageRaw:X8} resist={DamageResistLogCode(query)} rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                    DispatchCombatTargetDamageEvent(target, rng, target.EntityId, target.Name, source ?? marker ?? "Weapon::applyDamage");
                    if (sameHPWire == 0)
                        TryCommitCombatTargetDeathEvent(target, source ?? marker ?? "Weapon::applyDamage");
                    OnMonsterAttackResolved?.Invoke(monster, target, false, sameHPWire);
                }
                else
                {
                    ApplyCombatTargetQueriedDamage(monster, target, adjustedDamageWire, source ?? marker);
                    uint newHPWire = target.CurrentHPWire;
                    target.IsAlive = newHPWire > 0;
                    uint appliedDamageWire = currentHPWire - newHPWire;
                    uint effectRaw = ConsumeOnApplyDamageEffectRng(rng, "monster", monster, target, currentHPWire, newHPWire, appliedDamageWire, source ?? marker ?? "Weapon::applyDamage");
                    DispatchMonsterHitProcs(monster, target, MonsterUsesProjectileWeapon(monster) ? "RANGED" : "MELEE", damageResult.DamageTypeId, adjustedDamageWire, source ?? marker ?? "Weapon::applyDamage");
                    DispatchCombatTargetDamageEvent(target, rng, target.EntityId, target.Name, source ?? marker ?? "Weapon::applyDamage");
                    if (currentHPWire > newHPWire)
                        ApplyMonsterOnDamageCallback(monster, currentHPWire - newHPWire, source ?? marker);
                    if (newHPWire == 0)
                    {
                        TryCommitCombatTargetDeathEvent(target, source ?? marker ?? "Weapon::applyDamage");
                        RemovePlayerDamageModifiersForTarget(target.EntityId, source ?? marker ?? "monster-damage-death");
                    }
                    Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} HIT source={source} damageWire={adjustedDamageWire} preQueryWire={damageWire} hpWire={currentHPWire}->{newHPWire}/{target.MaxHPWire} rangeWire=[{minDamage},{maxDamage}] avgWire={averageDamage} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} hitThreshold={hitThreshold} rngPos={rng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dmgRaw=0x{damageRaw:X8} distF32={distF32} result={query.ResultName}");
                    Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source={source ?? marker ?? "monster"} target={target.Name}#{target.EntityId} weaponClassId={damageResult.WeaponClassId} damageTypeId={damageResult.DamageTypeId} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire={adjustedDamageWire} hp={currentHPWire}->{newHPWire}/{target.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} clientTick={_combatTick} target=Avatar");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=HIT damageWire={adjustedDamageWire} preQueryWire={damageWire} hp={currentHPWire}->{newHPWire}/{target.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} rngSeed=0x{runtime.Seed:X8} rngPos={rng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=HIT damageWire={adjustedDamageWire} hp={currentHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist={DamageResistLogCode(query)} rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                    OnMonsterAttackResolved?.Invoke(monster, target, true, target.CurrentHPWire);
                }
            }
            else if (blocked)
            {
                Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} block source={source} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} hitThreshold={hitThreshold} rngPos={rng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} distF32={distF32}");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=BLOCK damageWire=0 hp={target.CurrentHPWire}->{target.CurrentHPWire}/{target.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} rngSeed=0x{runtime.Seed:X8} rngPos={rng.CallsSinceReseed}");
                Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=BLOCK damageWire=0 hp={target.CurrentHPWire}->{target.CurrentHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} resist=0 rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                OnMonsterAttackResolved?.Invoke(monster, target, false, target.CurrentHPWire);
            }
            else
            {
                Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} miss source={source} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} hitThreshold={hitThreshold} rngPos={rng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} threshold={hitThreshold} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} distF32={distF32}");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=MISS damageWire=0 hp={target.CurrentHPWire}->{target.CurrentHPWire}/{target.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} rngSeed=0x{runtime.Seed:X8} rngPos={rng.CallsSinceReseed}");
                Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=MISS damageWire=0 hp={target.CurrentHPWire}->{target.CurrentHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} resist=0 rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                OnMonsterAttackResolved?.Invoke(monster, target, false, target.CurrentHPWire);
            }
        }

        private bool TryApplyMonsterActiveSkillEffect(Monster monster, CombatTarget target, int distF32, string marker, string source, out bool handled, out bool hpShifted)
        {
            handled = false;
            hpShifted = false;
            if (monster == null || target == null || !target.HasUnitState || !monster.UsePrimaryActiveSkillThisAttack)
                return false;

            if (!TryResolveMonsterHitPointRegenSkillEffect(monster, out MonsterHitPointRegenSkillEffect effect))
            {
                if (TryResolveMonsterWeaponDamageSkillEffect(monster, out MonsterWeaponDamageSkillEffect weaponEffect))
                {
                    handled = true;
                    CommitMonsterPrimarySkillUse(monster, $"{marker ?? "MON-SKILL"}:ActiveSkill::use@0x00538DD0");
                    hpShifted = ApplyMonsterWeaponDamageSkillEffect(monster, target, weaponEffect, distF32, marker, source);
                    return true;
                }

                if (TryResolveMonsterStunActionSkillEffect(monster, out MonsterStunActionSkillEffect stunEffect))
                {
                    handled = true;
                    CommitMonsterPrimarySkillUse(monster, $"{marker ?? "MON-SKILL"}:ActiveSkill::use@0x00538DD0");
                    ApplyMonsterStunActionSkillEffect(monster, target, stunEffect, marker, source);
                    return true;
                }

                handled = true;
                MonsterSkillEffectSupport support = ResolveMonsterSkillEffectSupport(monster.PrimaryActiveSkillPath, monster.PrimaryActiveSkillEffect);
                string families = support != null && support.Families.Count > 0 ? string.Join(",", support.Families) : "none";
                string unsupported = support != null && support.UnsupportedFamilies.Count > 0 ? string.Join(",", support.UnsupportedFamilies) : "none";
                SkillEffectTracker.RecordRejected("monster", monster.EntityId, monster.PrimaryActiveSkillPath, target.EntityId, support?.Reason ?? "unsupported-effect", _combatTick);
                Debug.LogError($"[MON-SKILL-EFFECT] state=unhandled monster={monster.Name}#{monster.EntityId} target={target.Name} skill={monster.PrimaryActiveSkillPath ?? "none"} effect={monster.PrimaryActiveSkillEffect ?? "none"} families={families} status={support?.Status ?? "UNKNOWN"} unsupported={unsupported} source={source ?? marker ?? "unknown"} reason=unsupported-effect sourceFunction=ActiveSkill::doSkillEffect@0x00539630");
                return true;
            }

            handled = true;
            uint beforeHP = target.CurrentHPWire;
            uint powerLevel = ResolveMonsterSkillPowerLevelWire(monster, effect.SkillPath);
            if (target.Monster != null)
            {
                CommitMonsterPrimarySkillUse(monster, $"{marker ?? "MON-SKILL"}:ActiveSkill::use@0x00538DD0");
                return ApplyMonsterAuthoredAttributeModifier(target.Monster, effect.ModifierPath,
                    new Dictionary<string, int> { ["HIT_POINT_REGEN_BONUS"] = effect.HitPointRegenBonus },
                    effect.DurationTicks, effect.RemoveOnDeath, effect.StackRule, powerLevel,
                    monster.EntityId, effect.SkillPath, effect.EffectPath);
            }
            string modifierKey = BuildPlayerRuntimeModifierKey(target.EntityId, monster.EntityId, effect.ModifierPath, effect.StackRule, CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF);
            bool replaceModifier = _playerModifierNetworkIds.ContainsKey(modifierKey);
            bool stackAccepted = target.PlayerState.ShouldAcceptAttributeModifier(modifierKey, effect.ModifierPath, effect.StackRule, powerLevel, effect.DurationTicks);
            uint modifierId = 0;
            if (stackAccepted && !TryAllocatePlayerModifierNetworkId(modifierKey, true, out modifierId))
                return true;
            CommitMonsterPrimarySkillUse(monster, $"{marker ?? "MON-SKILL"}:ActiveSkill::use@0x00538DD0");
            if (!stackAccepted)
            {
                RaisePlayerModifierAdd(monster, target, modifierKey, effect.ModifierPath, 1, powerLevel, effect.DurationTicks, CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF, replaceModifier, effect.SkillPath, effect.EffectPath, source ?? marker ?? "unknown", "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280", false);
                uint rejectedHP = target.CurrentHPWire;
                Debug.LogError($"[PLAYER-MODIFIER-STACK] result=reject target={target.Name}#{target.EntityId} source={monster.Name}#{monster.EntityId} skill={effect.SkillPath} modifier={effect.ModifierPath} stack={effect.StackRule ?? "UNKNOWN"} key={modifierKey} incomingPower={powerLevel} incomingDuration={effect.DurationTicks} sourceIsSelf={CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF} sourceFunction=Modifiers::addModifierLocal@0x00501770");
                Debug.LogError($"[SPELL-MOD] skill={effect.SkillPath} effect={effect.EffectPath} modifier={effect.ModifierPath} attr=HIT_POINT_REGEN_BONUS value={effect.HitPointRegenBonus} durationF32={effect.DurationF32} durationTicks={effect.DurationTicks} applied=False hp={beforeHP}->{rejectedHP}/{target.MaxHPWire} source={source ?? marker ?? "unknown"} sourceFunction=SpellModEffect::doEffect@0x00554460 chanceDraw=false stack={effect.StackRule ?? ""} stackAccepted=False");
                return true;
            }
            bool applied = target.PlayerState.ApplyHitPointRegenBonusModifier(
                effect.ModifierPath,
                effect.HitPointRegenBonus,
                effect.DurationTicks,
                effect.RemoveOnDeath,
                $"{marker ?? "MON-SKILL"}:{effect.SkillPath}",
                modifierKey,
                monster.EntityId,
                effect.SkillPath,
                effect.EffectPath,
                powerLevel,
                effect.StackRule,
                level: 1,
                sourceIsSelf: CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF);
            uint afterHP = target.CurrentHPWire;
            hpShifted = afterHP != beforeHP;
            if (applied)
                RaisePlayerModifierAddWithId(monster, target, modifierKey, effect.ModifierPath, modifierId, 1, powerLevel, effect.DurationTicks, CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF, replaceModifier, effect.SkillPath, effect.EffectPath, source ?? marker ?? "unknown", "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280");
            else
                ReleasePlayerModifierNetworkId(modifierKey, modifierId);
            Debug.LogError($"[SPELL-MOD] skill={effect.SkillPath} effect={effect.EffectPath} modifier={effect.ModifierPath} attr=HIT_POINT_REGEN_BONUS value={effect.HitPointRegenBonus} durationF32={effect.DurationF32} durationTicks={effect.DurationTicks} power={powerLevel} applied={applied} hp={beforeHP}->{afterHP}/{target.MaxHPWire} source={source ?? marker ?? "unknown"} sourceFunction=SpellModEffect::doEffect@0x00554460 chanceDraw=false stack={effect.StackRule ?? ""} stackAccepted=True");
            return true;
        }

        private void LogMonsterActiveSkillEffectSupport(Monster monster, MonsterActiveSkillRuntime skill)
        {
            if (monster == null || skill == null || string.IsNullOrWhiteSpace(skill.Path))
                return;
            MonsterSkillEffectSupport support = ResolveMonsterSkillEffectSupport(skill.Path, skill.Effect);
            string families = support.Families.Count > 0 ? string.Join(",", support.Families) : "none";
            string unsupported = support.UnsupportedFamilies.Count > 0 ? string.Join(",", support.UnsupportedFamilies) : "none";
            Debug.LogError($"[MON-SKILL-EFFECT-SUPPORT] monster={monster.Name}#{monster.EntityId} skill={skill.Path} effect={support.EffectPath ?? skill.Effect ?? "none"} families={families} status={support.Status} unsupported={unsupported} modifier={support.ModifierPath ?? ""} attr={support.Attribute ?? ""} reason={support.Reason ?? ""} sourceFunction=ActiveSkill::doSkillEffect@0x00539630 SpellModEffect::doEffect@0x00554460 SpellWeaponDamageEffect::doEffect@0x0055E460 SpellKnockBackEffect::doEffect@0x00552C80 SpellKnockDownEffect::doEffect@0x005534F0");
        }

        private MonsterSkillEffectSupport ResolveMonsterSkillEffectSupport(string skillPath, string fallbackEffectPath)
        {
            var support = new MonsterSkillEffectSupport
            {
                SkillPath = skillPath,
                EffectPath = fallbackEffectPath
            };

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
            {
                support.Status = "UNRESOLVED";
                support.Reason = "missing-gc-database";
                return support;
            }

            GCNode skill = gc.ResolveWithInheritance(skillPath);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", fallbackEffectPath) ?? fallbackEffectPath;
            support.EffectPath = effectPath;
            if (string.IsNullOrWhiteSpace(effectPath))
            {
                support.Status = "NO_EFFECT";
                support.Reason = "skill-has-no-effect-property";
                return support;
            }

            GCNode effectNode = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectNode == null)
            {
                support.Status = "UNRESOLVED";
                support.Reason = "effect-path-unresolved";
                return support;
            }

            foreach (GCNode effectChild in EnumerateEffectChildren(effectNode))
            {
                string family = NormalizeEffectFamily(effectChild.Extends);
                if (string.IsNullOrWhiteSpace(family) || support.Families.Contains(family))
                    continue;
                support.Families.Add(family);
            }

            if (support.Families.Count == 0)
            {
                support.Status = "NO_EFFECT_CHILDREN";
                support.Reason = "effect-node-has-no-client-effect-children";
                return support;
            }

            bool hasSupportedHpRegenMod = TryClassifyHitPointRegenBonusModEffect(effectNode, skill, out string modifierPath, out string attribute);
            support.ModifierPath = modifierPath;
            support.Attribute = attribute;

            foreach (string family in support.Families)
            {
                if (family == "SpellSoundEffect")
                    continue;
                if (family == "SpellModEffect" && hasSupportedHpRegenMod)
                    continue;
                if (family == "SpellWeaponDamageEffect" || family == "SpellKnockBackEffect" || family == "SpellKnockDownEffect" || family == "SpellDamageEffect")
                    continue;
                support.UnsupportedFamilies.Add(family);
            }

            if (support.UnsupportedFamilies.Count > 0)
            {
                support.Status = "UNRESOLVED";
                support.Reason = "unhandled-client-effect-family";
            }
            else if (support.Families.Contains("SpellModEffect") && hasSupportedHpRegenMod && support.Families.All(f => f == "SpellModEffect" || f == "SpellSoundEffect"))
            {
                support.Status = "SUPPORTED_HP_REGEN_BONUS";
                support.Reason = "server-mirrors-SpellModEffect-HIT_POINT_REGEN_BONUS-runtime";
            }
            else if (support.Families.Contains("SpellWeaponDamageEffect"))
            {
                bool hasStunAction = support.Families.Contains("SpellKnockBackEffect") || support.Families.Contains("SpellKnockDownEffect");
                support.Status = hasStunAction ? "INCOMPLETE_WEAPON_DAMAGE_STUN_ACTION" : "INCOMPLETE_WEAPON_DAMAGE";
                support.Reason = hasStunAction ? "weapon-damage-and-stun-rng-mirrored-player-knockback-action-and-movement-handoff-unverified" : "weapon-damage-hp-mirror-exists";
            }
            else if (support.Families.Contains("SpellDamageEffect"))
            {
                support.Status = "INCOMPLETE_SPELL_DAMAGE";
                support.Reason = "SpellDamageEffect/DOT/client-modifier-update-timing-not-closed";
            }
            else if (support.Families.Contains("SpellKnockBackEffect") || support.Families.Contains("SpellKnockDownEffect"))
            {
                support.Status = "INCOMPLETE_STUN_ACTION";
                support.Reason = "stun-rng-mirrored-player-knockback-action-and-movement-handoff-unverified";
            }
            else
            {
                support.Status = "INCOMPLETE";
                support.Reason = "client-effect-family-present-without-runtime-mirror";
            }

            return support;
        }

        private static IEnumerable<GCNode> EnumerateEffectChildren(GCNode effectNode)
        {
            if (effectNode == null)
                yield break;
            foreach (var child in EnumerateEffectTree(effectNode))
                yield return child;
        }

        private static IEnumerable<GCNode> EnumerateEffectTree(GCNode effectNode)
        {
            if (effectNode == null)
                yield break;
            string family = NormalizeEffectFamily(effectNode.Extends);
            if (!string.IsNullOrWhiteSpace(family)
                && !string.Equals(family, "SpellEffect", StringComparison.OrdinalIgnoreCase))
                yield return effectNode;
            foreach (GCNode child in effectNode.EnumerateChildrenInOrder())
            {
                foreach (var nested in EnumerateEffectTree(child))
                    yield return nested;
            }
        }

        private static string NormalizeEffectFamily(string extendsRaw)
        {
            if (string.IsNullOrWhiteSpace(extendsRaw))
                return "";
            string value = extendsRaw.Replace('\\', '.').Replace('/', '.');
            int dot = value.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < value.Length)
                value = value.Substring(dot + 1);
            return value.EndsWith("Effect", StringComparison.OrdinalIgnoreCase) ? value : "";
        }

        private bool TryClassifyHitPointRegenBonusModEffect(GCNode effectNode, GCNode skillContext, out string modifierPath, out string attribute)
        {
            modifierPath = null;
            attribute = null;
            GCNode modEffect = FindEffectChild(effectNode, "SpellModEffect");
            if (modEffect == null)
                return false;

            modifierPath = modEffect.GetString("Modifier", null);
            if (string.IsNullOrWhiteSpace(modifierPath))
                return false;

            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skillContext);
            GCNode modifierDesc = modifier?.GetChild("Description") ?? modifier;
            GCNode attributeNode = FindEffectChild(modifierDesc, "Attribute");
            attribute = attributeNode?.GetString("Attribute", null);
            return string.Equals(attribute, "HIT_POINT_REGEN_BONUS", StringComparison.OrdinalIgnoreCase);
        }

        private bool ApplyMonsterWeaponDamageSkillEffect(Monster monster, CombatTarget target, MonsterWeaponDamageSkillEffect effect, int distF32, string marker, string source)
        {
            return ApplyMonsterWeaponDamageSkillEffect(monster, target, effect, distF32, marker, source, out _);
        }

        private bool ApplyMonsterWeaponDamageSkillEffect(Monster monster, CombatTarget target, MonsterWeaponDamageSkillEffect effect, int distF32, string marker, string source, out bool landed)
        {
            landed = false;
            RoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            MersenneTwister rng = runtime?.RoomRng;
            if (rng == null)
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] result=NO_RNG attacker={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} target={target?.Name ?? "unknown"} skill={effect?.SkillPath ?? "none"} source={source ?? marker ?? "unknown"} reason=missing-room-rng sourceFunction=SpellWeaponDamageEffect::doEffect@0x0055E460");
                return false;
            }

            if (!ConsumeSpellEffectChanceRng(rng, monster, target, effect.SkillPath, effect.WeaponEffectPath, "SpellWeaponDamageEffect", effect.WeaponEffectChanceWire, marker, source, out uint effectChanceRaw, out uint effectChanceRoll))
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] result=CHANCE_FAIL attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} skill={effect.SkillPath} effect={effect.WeaponEffectPath} chanceWire={effect.WeaponEffectChanceWire} chanceRaw=0x{effectChanceRaw:X8} chanceRoll={effectChanceRoll} rngPos={rng.CallsSinceReseed} source={source ?? marker ?? "unknown"} sourceFunction=SpellEffect::CheckChance@0x00545FF0 SpellWeaponDamageEffect::doEffect@0x0055E460");
                ConsumeWeaponStunActionRng(rng, monster, target, effect, marker, source);
                return false;
            }

            WeaponDamageInput damageInput = CreateMonsterWeaponDamageInput(monster, target, rng, $"{marker ?? "MON-SKILL"}:SpellWeaponDamageEffect");
            int baseAttackRating = damageInput.AttackRating;
            int baseDamageMod = damageInput.DamageMod;
            if (effect.AttackRatingMod != 0)
                damageInput.AttackRating = Math.Max(0, (damageInput.AttackRating * (100 + effect.AttackRatingMod)) / 100);
            if (effect.DamageMod != 0)
                damageInput.DamageMod = Math.Max(0, damageInput.DamageMod + effect.DamageMod);

            WeaponDamageResult damageResult = DamageResolver.ResolveWeaponDamage(damageInput);
            landed = damageResult.IsHit && !damageResult.IsBlocked;
            bool hpShifted = false;
            uint beforeHP = target.CurrentHPWire;
            if (damageResult.IsHit && !damageResult.IsBlocked)
            {
                uint damageWire = damageResult.DamageWire;
                DamageQueryResult query = ApplyPlayerDamageQueryWire(damageWire, target, monster, damageResult.DamageTypeId, 0, source ?? marker ?? "SpellWeaponDamageEffect", damageResult.AttackerLevel, rng, true);
                uint adjustedDamageWire = query.AdjustedDamageWire;
                if (adjustedDamageWire == 0)
                {
                    uint sameHP = target.CurrentHPWire;
                    target.IsAlive = sameHP > 0;
                    Debug.LogError($"[SPELL-WEAPON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} no-damage skill={effect.SkillPath} effect={effect.WeaponEffectPath} dmg={damageWire / 256f:F2} adjusted=0 hp={sameHP}->{sameHP}/{target.MaxHPWire} arMod={effect.AttackRatingMod} ar={baseAttackRating}->{damageInput.AttackRating} dmgMod={baseDamageMod}->{damageInput.DamageMod} hit=0x{damageResult.HitRaw:X8}/{damageResult.HitRoll} block=0x{damageResult.BlockRaw:X8}/{damageResult.BlockRoll}/{damageResult.BlockChance} dmgRaw=0x{damageResult.DamageRaw:X8} rngPos={rng.CallsSinceReseed} result={query.ResultName} sourceFunction=SpellWeaponDamageEffect::doEffect@0x0055E460");
                    Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source={source ?? marker ?? "SpellWeaponDamageEffect"} target={target.Name}#{target.EntityId} weaponClassId={damageResult.WeaponClassId} damageTypeId={damageResult.DamageTypeId} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire=0 hp={beforeHP}->{sameHP}/{target.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} clientTick={_combatTick} target=Avatar");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster-skill attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result={query.ResultName} skill={effect.SkillPath} damageWire=0 preQueryWire={damageWire} hp={sameHP}->{sameHP}/{target.MaxHPWire} marker={marker} source={source} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} damageRaw=0x{damageResult.DamageRaw:X8} rngSeed=0x{(runtime?.Seed ?? 0u):X8} rngPos={rng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result={query.ResultName} skill={effect.SkillPath} damageWire=0 hp={sameHP}->{sameHP} hitRaw=0x{damageResult.HitRaw:X8} hitRoll={damageResult.HitRoll} threshold={damageResult.HitThreshold} blockRaw=0x{damageResult.BlockRaw:X8} blockRoll={damageResult.BlockRoll} blockChance={damageResult.BlockChance} damageRaw=0x{damageResult.DamageRaw:X8} resist={DamageResistLogCode(query)} rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                    DispatchCombatTargetDamageEvent(target, rng, target.EntityId, target.Name, source ?? marker ?? "SpellWeaponDamageEffect");
                    if (sameHP == 0)
                        TryCommitCombatTargetDeathEvent(target, source ?? marker ?? "SpellWeaponDamageEffect");
                }
                else
                {
                    ApplyCombatTargetQueriedDamage(monster, target, adjustedDamageWire, source ?? marker);
                    uint afterHP = target.CurrentHPWire;
                    target.IsAlive = afterHP > 0;
                    hpShifted = afterHP != beforeHP;
                    uint appliedDamageWire = beforeHP - afterHP;
                    uint effectRaw = ConsumeOnApplyDamageEffectRng(rng, "monster-skill", monster, target, beforeHP, afterHP, appliedDamageWire, source ?? marker ?? "SpellWeaponDamageEffect");
                    DispatchMonsterHitProcs(monster, target, MonsterUsesProjectileWeapon(monster) ? "RANGED" : "MELEE", damageResult.DamageTypeId, adjustedDamageWire, source ?? marker ?? "SpellWeaponDamageEffect");
                    DispatchCombatTargetDamageEvent(target, rng, target.EntityId, target.Name, source ?? marker ?? "SpellWeaponDamageEffect");
                    if (beforeHP > afterHP)
                        ApplyMonsterOnDamageCallback(monster, beforeHP - afterHP, source ?? marker ?? "SpellWeaponDamageEffect");
                    if (afterHP == 0)
                    {
                        TryCommitCombatTargetDeathEvent(target, source ?? marker ?? "SpellWeaponDamageEffect");
                        RemovePlayerDamageModifiersForTarget(target.EntityId, source ?? marker ?? "SpellWeaponDamageEffect");
                    }
                    bool modifierApplied = effect.DamageModifier != null && target.CurrentHPWire > 0 && ApplyPlayerDamageModifierFromMonster(monster, target, effect.DamageModifier, source ?? marker ?? "SpellWeaponDamageEffect");
                    Debug.LogError($"[SPELL-WEAPON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} HIT skill={effect.SkillPath} effect={effect.WeaponEffectPath} damageWire={adjustedDamageWire} preQueryWire={damageWire} hpWire={beforeHP}->{afterHP}/{target.MaxHPWire} arMod={effect.AttackRatingMod} ar={baseAttackRating}->{damageInput.AttackRating} dmgMod={baseDamageMod}->{damageInput.DamageMod} hit=0x{damageResult.HitRaw:X8}/{damageResult.HitRoll} block=0x{damageResult.BlockRaw:X8}/{damageResult.BlockRoll}/{damageResult.BlockChance} dmgRaw=0x{damageResult.DamageRaw:X8} effectRaw=0x{effectRaw:X8} modifierApplied={modifierApplied} distF32={distF32} rngPos={rng.CallsSinceReseed} result={query.ResultName} sourceFunction=Weapon::applyDamage@0x00597E50");
                    Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source={source ?? marker ?? "SpellWeaponDamageEffect"} target={target.Name}#{target.EntityId} weaponClassId={damageResult.WeaponClassId} damageTypeId={damageResult.DamageTypeId} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire={adjustedDamageWire} hp={beforeHP}->{afterHP}/{target.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} clientTick={_combatTick} target=Avatar");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster-skill attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=HIT skill={effect.SkillPath} damageWire={adjustedDamageWire} preQueryWire={damageWire} hp={beforeHP}->{afterHP}/{target.MaxHPWire} marker={marker} source={source} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} damageRaw=0x{damageResult.DamageRaw:X8} effectRaw=0x{effectRaw:X8} rngSeed=0x{(runtime?.Seed ?? 0u):X8} rngPos={rng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=HIT skill={effect.SkillPath} damageWire={adjustedDamageWire} hp={beforeHP}->{afterHP} hitRaw=0x{damageResult.HitRaw:X8} hitRoll={damageResult.HitRoll} threshold={damageResult.HitThreshold} blockRaw=0x{damageResult.BlockRaw:X8} blockRoll={damageResult.BlockRoll} blockChance={damageResult.BlockChance} damageRaw=0x{damageResult.DamageRaw:X8} effectRaw=0x{effectRaw:X8} resist={DamageResistLogCode(query)} rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                }
            }
            else if (damageResult.IsBlocked)
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} BLOCK skill={effect.SkillPath} effect={effect.WeaponEffectPath} hpWire={beforeHP}->{beforeHP}/{target.MaxHPWire} arMod={effect.AttackRatingMod} ar={baseAttackRating}->{damageInput.AttackRating} dmgMod={baseDamageMod}->{damageInput.DamageMod} hit=0x{damageResult.HitRaw:X8}/{damageResult.HitRoll} block=0x{damageResult.BlockRaw:X8}/{damageResult.BlockRoll}/{damageResult.BlockChance} distF32={distF32} rngPos={rng.CallsSinceReseed} sourceFunction=Weapon::applyDamage@0x00597E50");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster-skill attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=BLOCK skill={effect.SkillPath} damageWire=0 hp={beforeHP}->{beforeHP}/{target.MaxHPWire} marker={marker} source={source} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} rngSeed=0x{(runtime?.Seed ?? 0u):X8} rngPos={rng.CallsSinceReseed}");
            }
            else
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} MISS skill={effect.SkillPath} effect={effect.WeaponEffectPath} hpWire={beforeHP}->{beforeHP}/{target.MaxHPWire} arMod={effect.AttackRatingMod} ar={baseAttackRating}->{damageInput.AttackRating} dmgMod={baseDamageMod}->{damageInput.DamageMod} hit=0x{damageResult.HitRaw:X8}/{damageResult.HitRoll} threshold={damageResult.HitThreshold} block=0x{damageResult.BlockRaw:X8}/{damageResult.BlockRoll}/{damageResult.BlockChance} distF32={distF32} rngPos={rng.CallsSinceReseed} sourceFunction=Weapon::applyDamage@0x00597E50");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster-skill attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=MISS skill={effect.SkillPath} damageWire=0 hp={beforeHP}->{beforeHP}/{target.MaxHPWire} marker={marker} source={source} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} rngSeed=0x{(runtime?.Seed ?? 0u):X8} rngPos={rng.CallsSinceReseed}");
            }

            ConsumeWeaponStunActionRng(rng, monster, target, effect, marker, source);
            return hpShifted;
        }

        private bool TryResolveMonsterHitPointRegenSkillEffect(Monster monster, out MonsterHitPointRegenSkillEffect effect)
        {
            effect = null;
            if (monster == null || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            GCNode skill = gc.ResolveWithInheritance(monster.PrimaryActiveSkillPath);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", monster.PrimaryActiveSkillEffect) ?? monster.PrimaryActiveSkillEffect;
            if (string.IsNullOrWhiteSpace(effectPath))
                return false;

            GCNode effectNode = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectNode == null)
                return false;

            GCNode modEffect = FindEffectChild(effectNode, "SpellModEffect");
            if (modEffect == null)
                return false;

            string modifierPath = modEffect.GetString("Modifier", null);
            if (string.IsNullOrWhiteSpace(modifierPath))
                return false;

            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skill);
            GCNode modifierDesc = modifier?.GetChild("Description") ?? modifier;
            if (modifierDesc == null)
                return false;

            GCNode attributeNode = FindEffectChild(modifierDesc, "Attribute");
            if (attributeNode == null)
                return false;

            string attribute = attributeNode.GetString("Attribute", null);
            if (!string.Equals(attribute, "HIT_POINT_REGEN_BONUS", StringComparison.OrdinalIgnoreCase))
                return false;

            int value = attributeNode.GetInt("Value", 0);
            if (value == 0)
                return false;

            int durationF32 = modEffect.GetFixed32("Duration", 0);
            int durationIncF32 = modEffect.GetFixed32("DurationInc", 0);
            int skillLevel = Math.Max(1, (int)(monster.SelectedActiveSkill?.SkillLevel ?? 1));
            long resolvedDurationF32 = durationF32 + (long)(skillLevel - 1) * durationIncF32;
            int resolvedDuration = resolvedDurationF32 <= 0
                ? 0
                : resolvedDurationF32 >= int.MaxValue
                    ? int.MaxValue
                    : (int)resolvedDurationF32;
            effect = new MonsterHitPointRegenSkillEffect
            {
                SkillPath = monster.PrimaryActiveSkillPath,
                EffectPath = effectPath,
                ModifierPath = modifierPath,
                HitPointRegenBonus = value,
                OverrideTable = attributeNode.GetBool("OverrideTable", false),
                DurationF32 = resolvedDuration,
                DurationTicks = ComputeSpellModDurationTicks(resolvedDuration),
                RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false),
                StackRule = modifierDesc.GetString("StackRule", "")
            };
            return true;
        }

        private bool TryResolveMonsterWeaponDamageSkillEffect(Monster monster, out MonsterWeaponDamageSkillEffect effect)
        {
            effect = null;
            if (monster == null || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            GCNode skill = gc.ResolveWithInheritance(monster.PrimaryActiveSkillPath);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", monster.PrimaryActiveSkillEffect) ?? monster.PrimaryActiveSkillEffect;
            if (string.IsNullOrWhiteSpace(effectPath))
                return false;

            GCNode effectNode = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectNode == null)
                return false;

            GCNode weaponDamage = FindEffectChild(effectNode, "SpellWeaponDamageEffect");
            if (weaponDamage == null)
                return false;

            GCNode knockBack = FindEffectChildByExtends(effectNode, "SpellKnockBackEffect");
            GCNode knockDown = FindEffectChildByExtends(effectNode, "SpellKnockDownEffect");
            TryResolveMonsterDamageModifierSkillEffect(effectNode, skill, monster.PrimaryActiveSkillPath, effectPath, out MonsterDamageModifierSkillEffect damageModifier);
            int skillLevel = ResolveMonsterActiveSkillLevel(monster, monster.PrimaryActiveSkillPath);
            effect = new MonsterWeaponDamageSkillEffect
            {
                SkillPath = monster.PrimaryActiveSkillPath,
                EffectPath = effectPath,
                WeaponEffectPath = BuildAuthoredEffectPath(effectPath, weaponDamage),
                WeaponEffectChanceWire = ResolveSpellEffectChanceWire(weaponDamage),
                AttackRatingMod = ResolveSkillLinearMod(weaponDamage, "ARMod", skillLevel),
                DamageMod = ResolveSkillLinearMod(weaponDamage, "DamageMod", skillLevel),
                HasKnockBack = knockBack != null,
                KnockBackEffectPath = knockBack != null ? BuildAuthoredEffectPath(effectPath, knockBack) : null,
                KnockBackStrength = knockBack != null ? ResolveSkillLinearMod(knockBack, "Strength", skillLevel) : 0,
                KnockBackChanceWire = ResolveSpellEffectChanceWire(knockBack),
                HasKnockDown = knockDown != null,
                KnockDownEffectPath = knockDown != null ? BuildAuthoredEffectPath(effectPath, knockDown) : null,
                KnockDownStrength = knockDown != null ? ResolveSkillLinearMod(knockDown, "Strength", skillLevel) : 0,
                KnockDownChanceWire = ResolveSpellEffectChanceWire(knockDown),
                DamageModifier = damageModifier
            };
            return true;
        }

    }
}
