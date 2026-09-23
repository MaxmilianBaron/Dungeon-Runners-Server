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
    {        public bool IsMonsterClientVisibleMovingThisFrame(Monster monster)
        {
            if (monster == null || !monster.IsAlive)
                return false;
            if (!monster.AggroTriggered && WanderSimulator.Instance.TryGetSnapshot(monster.EntityId, out WanderStateSnapshot wander))
                return wander.MovingThisFrame;
            return monster.ClientVisibleMovingThisFrameTick == CombatTick && monster.ClientVisibleMovingThisFrame;
        }

        private void AdvanceMonsterClientVisiblePosition(Monster monster)
        {
            if (monster == null || !monster.ClientVisibleMoveInitialized)
                return;
            uint currentTick = _combatTick;
            if (currentTick < monster.ClientVisibleMoveLastTick)
            {
                monster.ClientVisibleMoveLastTick = currentTick;
                return;
            }

            if (currentTick <= monster.ClientVisibleMoveLastTick)
                return;

            if (!monster.ClientVisibleMoveActive)
            {
                monster.ClientVisibleMoveLastTick = currentTick;
                return;
            }

            int speedFixed = ResolveMonsterMovementSpeedFixed(monster);
            if (speedFixed <= 0)
            {
                if (!monster.ClientVisibleMoveTargetFixedInit)
                {
                    monster.ClientVisibleMoveTargetFixedX = monster.ClientVisibleFixedInit ? monster.ClientVisibleFixedX : monster.PosFixedX;
                    monster.ClientVisibleMoveTargetFixedY = monster.ClientVisibleFixedInit ? monster.ClientVisibleFixedY : monster.PosFixedY;
                    monster.ClientVisibleMoveTargetFixedInit = true;
                }
                monster.ClientVisibleFixedX = monster.ClientVisibleMoveTargetFixedX;
                monster.ClientVisibleFixedY = monster.ClientVisibleMoveTargetFixedY;
                monster.ClientVisibleFixedInit = true;
                monster.ClientVisibleMoveActive = false;
                monster.ClientVisibleMoveLastTick = currentTick;
                return;
            }

            int ticks = (int)(currentTick - monster.ClientVisibleMoveLastTick);
            if (ticks <= 0)
                return;

            if (!monster.ClientVisibleFixedInit)
            {
                monster.ClientVisibleFixedX = monster.PosFixedX;
                monster.ClientVisibleFixedY = monster.PosFixedY;
                monster.ClientVisibleFixedInit = true;
                monster.ClientVisibleHeadingInit = false;
            }
            if (!monster.ClientVisibleMoveTargetFixedInit)
            {
                monster.ClientVisibleMoveTargetFixedX = monster.ClientVisibleFixedX;
                monster.ClientVisibleMoveTargetFixedY = monster.ClientVisibleFixedY;
                monster.ClientVisibleMoveTargetFixedInit = true;
            }
            int tgtX = monster.ClientVisibleMoveTargetFixedX;
            int tgtY = monster.ClientVisibleMoveTargetFixedY;
            int stepFixed = (int)(((long)speedFixed << 8) / 0x1e00);
            if (stepFixed < 1) stepFixed = 1;
            int turnRate = UnitMover.TurnRatePerTickFixed(monster.TurnRateDegrees);
            if (!monster.ClientVisibleHeadingInit)
            {
                monster.ClientVisibleHeadingFixed = UnitMover.VectorToHeadingFixed(tgtX - monster.ClientVisibleFixedX, tgtY - monster.ClientVisibleFixedY);
                monster.ClientVisibleHeadingInit = true;
            }

            var pm = PathMapCatalog.Instance.GetPathMap(!string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName);
            int currentFixedZ = monster.PosFixedZ;
            bool arrived = false;
            for (int tickIndex = 0; tickIndex < ticks && !arrived; tickIndex++)
            {
                UnitMover.StepTowardFixedHeading(monster.ClientVisibleFixedX, monster.ClientVisibleFixedY, monster.ClientVisibleHeadingFixed, tgtX, tgtY, stepFixed, turnRate, out int nextX, out int nextY, out int nextHeading, out arrived);
                ResolveMonsterMovementFixed(monster, pm, monster.ClientVisibleFixedX, monster.ClientVisibleFixedY, currentFixedZ, nextX, nextY, out nextX, out nextY, out int nextZ, _combatTick);
                monster.ClientVisibleFixedX = nextX;
                monster.ClientVisibleFixedY = nextY;
                currentFixedZ = nextZ;
                monster.ClientVisibleHeadingFixed = nextHeading;
            }
            if (pm != null)
                monster.PosFixedZ = currentFixedZ;
            if (arrived)
                monster.ClientVisibleMoveActive = false;
            monster.ClientVisibleMoveLastTick = currentTick;
            if (VerboseMonsterDiag) Debug.LogError($"[MON-MOVER-STEP] entity={monster.EntityId} ticks={ticks} fixedX={monster.ClientVisibleFixedX} fixedY={monster.ClientVisibleFixedY} fixedZ={currentFixedZ} headingFixed={monster.ClientVisibleHeadingFixed} stepFixed={stepFixed} tgtFixedX={tgtX} tgtFixedY={tgtY} arrived={arrived} tick={_combatTick} sourceFunction=UnitMover::MoveUnit@0x5366C0");
        }

        private void AdvanceMonsterClientVisiblePosition(Monster monster, MonsterClientVisibleState state, string source)
        {
            if (monster == null || state == null || !state.Initialized)
                return;
            uint currentTick = _combatTick;
            if (currentTick < state.LastTick)
            {
                state.LastTick = currentTick;
                return;
            }

            if (currentTick <= state.LastTick)
                return;

            if (!state.Active)
            {
                state.LastTick = currentTick;
                return;
            }

            int speedFixed = ResolveMonsterMovementSpeedFixed(monster);
            if (speedFixed <= 0)
            {
                if (!state.TargetFixedInit)
                {
                    state.TargetFixedX = state.FixedInit ? state.FixedX : monster.PosFixedX;
                    state.TargetFixedY = state.FixedInit ? state.FixedY : monster.PosFixedY;
                    state.TargetFixedInit = true;
                }
                state.FixedX = state.TargetFixedX;
                state.FixedY = state.TargetFixedY;
                state.FixedInit = true;
                state.Active = false;
                state.LastTick = currentTick;
                return;
            }

            int ticks = (int)(currentTick - state.LastTick);
            if (ticks <= 0)
                return;

            if (!state.FixedInit)
            {
                state.FixedX = monster.PosFixedX;
                state.FixedY = monster.PosFixedY;
                state.FixedZ = monster.PosFixedZ;
                state.FixedInit = true;
                state.FixedZInit = true;
                state.HeadingInit = false;
            }
            if (!state.TargetFixedInit)
            {
                state.TargetFixedX = state.FixedX;
                state.TargetFixedY = state.FixedY;
                state.TargetFixedInit = true;
            }
            int tgtX = state.TargetFixedX;
            int tgtY = state.TargetFixedY;
            int stepFixed = (int)(((long)speedFixed << 8) / 0x1e00);
            if (stepFixed < 1) stepFixed = 1;
            int turnRate = UnitMover.TurnRatePerTickFixed(monster.TurnRateDegrees);
            if (!state.HeadingInit)
            {
                state.HeadingFixed = UnitMover.VectorToHeadingFixed(tgtX - state.FixedX, tgtY - state.FixedY);
                state.HeadingInit = true;
            }

            var pm = PathMapCatalog.Instance.GetPathMap(!string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName);
            int currentFixedZ = state.FixedZInit ? state.FixedZ : monster.PosFixedZ;
            bool arrived = false;
            for (int tickIndex = 0; tickIndex < ticks && !arrived; tickIndex++)
            {
                UnitMover.StepTowardFixedHeading(state.FixedX, state.FixedY, state.HeadingFixed, tgtX, tgtY, stepFixed, turnRate, out int nextX, out int nextY, out int nextHeading, out arrived);
                ResolveMonsterMovementFixed(monster, pm, state.FixedX, state.FixedY, currentFixedZ, nextX, nextY, out nextX, out nextY, out int nextZ, _combatTick);
                state.FixedX = nextX;
                state.FixedY = nextY;
                currentFixedZ = nextZ;
                state.HeadingFixed = nextHeading;
            }
            state.FixedZ = currentFixedZ;
            state.FixedZInit = true;
            if (arrived)
                state.Active = false;
            state.LastTick = currentTick;
            if (VerboseMonsterDiag) Debug.LogError($"[MON-MOVER-STEP-VIEWER] entity={monster.EntityId} ticks={ticks} fixedX={state.FixedX} fixedY={state.FixedY} headingFixed={state.HeadingFixed} stepFixed={stepFixed} tgtFixedX={tgtX} tgtFixedY={tgtY} arrived={arrived} tick={_combatTick} source={source ?? "unknown"} sourceFunction=UnitMover::MoveUnit@0x5366C0");
        }

        public void EngageMonsterFromClientAction(Monster monster, uint playerEntityId, bool clientWeaponUseStarted = false)
        {
            if (monster == null || !monster.IsAlive) return;
            if (!IsMonsterEnemyOfPlayer(monster)) return;
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null) return;
            if (!MatchesInstance(monster, player.InstanceKey)) return;
            int allowedRangeF32 = ResolveMonsterEffectiveAttackRangeFixed(monster);
            int monsterFixedX = monster.PosFixedX;
            int monsterFixedY = monster.PosFixedY;
            TryGetMonsterClientVisiblePositionFixed(monster, playerEntityId, out monsterFixedX, out monsterFixedY);
            int dxF32 = player.PosFixedX - monsterFixedX;
            int dyF32 = player.PosFixedY - monsterFixedY;
            int distanceF32 = UnitMover.IntSqrt((long)dxF32 * dxF32 + (long)dyF32 * dyF32);
            int contactRangeF32 = ResolveClientContactRangeFixed(monster, player, allowedRangeF32);
            bool inContact = contactRangeF32 > 0 && distanceF32 <= SaturatingAddFixed8(contactRangeF32, CLIENT_CONTACT_RANGE_EPSILON_FIXED);
            int aggroRangeF32 = ResolveMonsterAggroAdmissionRangeFixed(monster);
            bool inAuthoredAggro = aggroRangeF32 > 0 && distanceF32 <= SaturatingAddFixed8(aggroRangeF32, CLIENT_CONTACT_RANGE_EPSILON_FIXED);
            bool alreadyTargetingPlayer = monster.AggroTriggered && monster.TargetId == player.EntityId;
            bool contactWasActive = monster.CombatContactTargetId == player.EntityId && monster.CombatContactUntilTick > _combatTick;
            if (!alreadyTargetingPlayer && !inAuthoredAggro && !inContact && !clientWeaponUseStarted)
            {
                Debug.LogError($"[AGGRO-OBSERVE] client intent out-of-range no-admit {monster.Name}#{monster.EntityId}->{player.Name} distFixed8={distanceF32} aggroF32={aggroRangeF32} targetSearchF32={ResolveMonsterTargetSearchRangeFixed(monster)} clientRangeF32={contactRangeF32} sourceFunction=MonsterBehavior2::onAttacked@0x0051B550");
                return;
            }
            if (!alreadyTargetingPlayer && !inAuthoredAggro && !inContact && clientWeaponUseStarted)
            {
                if (monster.CombatContactTargetId == player.EntityId)
                {
                    monster.CombatContactTargetId = 0;
                    monster.CombatContactUntilTick = 0;
                }
                Debug.LogError($"[AGGRO-OBSERVE] client weapon use outside aggro {monster.Name}#{monster.EntityId}->{player.Name} distFixed8={distanceF32} aggroF32={aggroRangeF32} targetSearchF32={ResolveMonsterTargetSearchRangeFixed(monster)} clientRangeF32={contactRangeF32} action=client-use-aggro sourceFunction=RangedWeapon::doHit+Projectile::doImpact->MonsterBehavior2::onAttacked");
            }
            ApplyMonsterWanderClientVisiblePosition(monster, "client-action");
            bool attackPathClear = IsMonsterAttackPathClear(monster, player, inContact ? "client-contact" : null);
            if (!attackPathClear)
            {
                inContact = false;
                ClearMonsterCombatContact(monster, player);
            }
            string aggroReason = clientWeaponUseStarted && !inAuthoredAggro && !inContact ? "client-client-use" : "client";
            AggroMonster(monster, player, aggroReason, false);
            if (inContact)
            {
                RefreshMonsterCombatContact(monster, player);
                if (!contactWasActive)
                    Debug.LogError($"[MON-CONTACT] {monster.Name}#{monster.EntityId}->{player.Name} distFixed8={distanceF32} rangeF32={allowedRangeF32} clientRangeF32={contactRangeF32}");
            }
            else if (monster.CombatContactTargetId == player.EntityId)
            {
                monster.CombatContactTargetId = 0;
                monster.CombatContactUntilTick = 0;
            }
            TraceMonsterState(monster, "client-action", player, distanceF32, allowedRangeF32, inContact ? "client-contact" : aggroReason);
        }

        private uint PeekRuntimeMonsterHPWire(Monster monster)
        {
            return monster?.CurrentHPWire ?? 0;
        }

        private int ResolveMonsterHealthRegenFactor(Monster monster)
        {
            if (monster == null)
                return 0;
            int unitDescHealthRegenF32 = monster.HasAuthoredHealthRegen ? monster.HealthRegenF32 : 0x100;
            return ComputeUnitDescRegenFactor(unitDescHealthRegenF32, GetRequiredGlobalKnobFixed32("MonsterHealthRegen"));
        }

        private int ResolveMonsterHealthRegenModPct(Monster monster)
        {
            return 0;
        }

        private int ResolveMonsterAdditiveHealthRegen(Monster monster)
        {
            int additive = ResolveMonsterHitPointRegenBonusBase();
            if (monster?.AttributeModifiers == null || monster.AttributeModifiers.Count == 0)
                return additive;
            for (int i = 0; i < monster.AttributeModifiers.Count; i++)
                additive += monster.AttributeModifiers[i].HitPointRegenBonus
                    + ResolveMonsterModifierAttributeValue(monster.AttributeModifiers[i], "HIT_POINT_REGEN_BONUS");
            return additive;
        }

        private int ResolveMonsterHitPointRegenBonusBase()
        {
            return 0;
        }

        private int ResolveMonsterManaPointRegenBonusBase()
        {
            return 0;
        }

        internal static int ComputeUnitRegenDeltaWire(uint maxHPWire, int baseRegen, int regenModPct, int additiveRegen, bool cooldownActive)
        {
            if (maxHPWire == 0) return 0;
            long regen;
            long bonus = additiveRegen;
            if (cooldownActive)
            {
                if (bonus == 0) return 0;
                regen = bonus;
            }
            else
            {
                regen = (((long)regenModPct + CLIENT_PERCENT_SCALE) * baseRegen) / CLIENT_PERCENT_SCALE + bonus;
            }

            long delta = (regen * maxHPWire) / CLIENT_UNIT_REGEN_DIVISOR;
            if (!cooldownActive)
                delta += 1;
            if (delta > int.MaxValue) return int.MaxValue;
            if (delta < int.MinValue) return int.MinValue;
            return (int)delta;
        }

        internal static uint ApplyUnitHPShiftWire(uint hpWire, uint maxHPWire, int deltaWire)
        {
            long shifted = (long)hpWire + deltaWire;
            if (shifted <= 0) return 0;
            if (shifted >= maxHPWire) return maxHPWire;
            return (uint)shifted;
        }

        private int ComputeMonsterHealthRegenDeltaWire(Monster monster, bool cooldownActive)
        {
            if (monster == null) return 0;
            return ComputeUnitRegenDeltaWire(
                monster.MaxHPWire,
                ResolveMonsterHealthRegenFactor(monster),
                ResolveMonsterHealthRegenModPct(monster),
                ResolveMonsterAdditiveHealthRegen(monster),
                cooldownActive);
        }

        private void ResetMonsterHPRegenClock(Monster monster, uint tick)
        {
            if (monster == null) return;
            _monsterHPRegenLastTick[monster.EntityId] = tick;
        }

        private int ResolveMonsterManaRegenFactor(Monster monster)
        {
            if (monster == null)
                return 0;
            int unitDescManaRegenF32 = monster.HasAuthoredManaRegen ? monster.ManaRegenF32 : 0x100;
            return ComputeUnitDescRegenFactor(unitDescManaRegenF32, GetRequiredGlobalKnobFixed32("MonsterPowerRegen"));
        }

        private static int ComputeUnitDescRegenFactor(int authoredUnitF32, int authoredGlobalF32)
        {
            if (authoredUnitF32 <= 0 || authoredGlobalF32 <= 0)
                return 0;
            long value = ((long)authoredUnitF32 * authoredGlobalF32) >> 16;
            if (value <= 0) return 0;
            return value > ushort.MaxValue ? ushort.MaxValue : (int)value;
        }

        private int ComputeMonsterManaRegenDeltaWire(Monster monster, bool cooldownActive)
        {
            if (monster == null || monster.MaxManaWire == 0) return 0;
            return ComputeUnitRegenDeltaWire(
                monster.MaxManaWire,
                ResolveMonsterManaRegenFactor(monster),
                0,
                ResolveMonsterManaPointRegenBonusBase(),
                cooldownActive);
        }

        private void ResetMonsterManaRegenClock(Monster monster, uint tick)
        {
            if (monster == null) return;
            _monsterManaRegenLastTick[monster.EntityId] = tick;
        }

        private uint ApplyMonsterHealthRegen(Monster monster, uint tick, string source)
        {
            if (monster == null) return 0;
            uint hp = PeekRuntimeMonsterHPWire(monster);
            if (!monster.IsAlive || hp == 0 || monster.MaxHPWire == 0)
            {
                ResetMonsterHPRegenClock(monster, tick);
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
                return hp;
            }
            _monsterHPRegenCooldownTicks.TryGetValue(monster.EntityId, out ushort cooldown);
            if (hp >= monster.MaxHPWire && ResolveMonsterAdditiveHealthRegen(monster) >= 0 && cooldown == 0)
            {
                ResetMonsterHPRegenClock(monster, tick);
                return hp;
            }

            if (!_monsterHPRegenLastTick.TryGetValue(monster.EntityId, out uint lastTick))
                lastTick = tick > 0 ? tick - 1u : 0u;
            int ticks = tick > lastTick ? (int)Math.Min((uint)int.MaxValue, tick - lastTick) : 0;
            if (ticks <= 0) return hp;
            _monsterHPRegenLastTick[monster.EntityId] = tick;

            uint oldHP = hp;
            int regenFactor = ResolveMonsterHealthRegenFactor(monster);
            int regenMod = ResolveMonsterHealthRegenModPct(monster);
            int additiveRegen = ResolveMonsterAdditiveHealthRegen(monster);
            bool diedFromRegen = false;
            for (int tickIndex = 0; tickIndex < ticks && (hp < monster.MaxHPWire || additiveRegen < 0 || cooldown > 0); tickIndex++)
            {
                if (cooldown > 0)
                    cooldown--;
                bool cooldownActive = cooldown > 0;
                int regenWire = ComputeMonsterHealthRegenDeltaWire(monster, cooldownActive);
                if (regenWire != 0)
                {
                    hp = ApplyUnitHPShiftWire(hp, monster.MaxHPWire, regenWire);
                    if (hp == 0)
                    {
                        diedFromRegen = true;
                        break;
                    }
                }
                additiveRegen = ResolveMonsterAdditiveHealthRegen(monster);
            }
            additiveRegen = ResolveMonsterAdditiveHealthRegen(monster);

            if (cooldown > 0)
                _monsterHPRegenCooldownTicks[monster.EntityId] = cooldown;
            else
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);

            if (oldHP == hp) return hp;

            if (hp >= monster.MaxHPWire)
            {
                hp = monster.MaxHPWire;
                _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
            }

            monster.CurrentHPWire = hp;
            Debug.LogError($"[MON-REGEN] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={oldHP / 256f:F2}->{hp / 256f:F2}/{monster.MaxHPWire / 256f:F2} ticks={ticks} cooldown={cooldown} base={regenFactor} mod={regenMod} additive={additiveRegen}");
            if (diedFromRegen && monster.IsAlive)
            {
                Debug.LogError($"[MON-REGEN-DEATH] {monster.Name}#{monster.EntityId} curHp wire reached 0 via negative regen source={source ?? "regen"} sourceFunction=Unit::update@0x5093E0->setDead@0x50cb70");
                BeginMonsterDeathLifecycle(monster, "regen-to-zero");
            }
            return hp;
        }

        private void AdvanceMonsterAttributeModifierTick(Monster monster)
        {
            if (monster?.AttributeModifiers == null || monster.AttributeModifiers.Count == 0)
                return;
            for (int i = monster.AttributeModifiers.Count - 1; i >= 0; i--)
            {
                var mod = monster.AttributeModifiers[i];
                if (mod.Permanent)
                    continue;
                if (mod.RemainingTicks <= 0)
                {
                    monster.AttributeModifiers.RemoveAt(i);
                    continue;
                }
                mod.RemainingTicks--;
                if (mod.RemainingTicks == 0)
                {
                    monster.AttributeModifiers.RemoveAt(i);
                    Debug.LogError($"[MON-ATTRMOD-EXPIRE] entity={monster.EntityId} key={mod.ModifierKey}");
                }
            }
        }

        private static int ResolveMonsterModifierAttributeValue(MonsterAttributeModifier modifier, string attribute)
        {
            if (modifier?.Attributes == null || string.IsNullOrWhiteSpace(attribute))
                return 0;
            string key = attribute.Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
            return modifier.Attributes.TryGetValue(key, out int value) ? value : 0;
        }

        private bool ApplyMonsterAuthoredAttributeModifier(
            Monster monster,
            string modifierPath,
            IReadOnlyDictionary<string, int> attributes,
            int durationTicks,
            bool removeOnDeath,
            string stackRule,
            uint powerLevel,
            uint sourceEntityId,
            string skillPath,
            string effectPath,
            byte skillLevel = 1, int terminateWhenHitChance = 0)
        {
            if (monster == null || !monster.IsAlive || string.IsNullOrWhiteSpace(modifierPath))
                return false;
            monster.AttributeModifiers ??= new List<MonsterAttributeModifier>();
            string modifierKey = string.Equals(stackRule, "UNIQUEBYSOURCE", StringComparison.OrdinalIgnoreCase)
                ? $"{sourceEntityId}:{modifierPath}"
                : modifierPath;
            int existingIndex = monster.AttributeModifiers.FindIndex(modifier =>
                modifier != null && string.Equals(modifier.ModifierKey, modifierKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                MonsterAttributeModifier existing = monster.AttributeModifiers[existingIndex];
                uint existingDuration = existing.Permanent ? 0u : (uint)Math.Max(0, existing.RemainingTicks);
                uint incomingDuration = durationTicks <= 0 ? 0u : (uint)durationTicks;
                if (!ShouldAcceptModifierStack(stackRule, powerLevel, incomingDuration, existing.PowerLevel, existingDuration))
                {
                    Debug.LogError($"[MON-ATTRMOD] reject target={monster.Name}#{monster.EntityId} modifier={modifierPath} incomingPower={powerLevel} existingPower={existing.PowerLevel} incomingDuration={incomingDuration} existingDuration={existingDuration} stack={stackRule ?? ""} sourceFunction=Modifiers::addModifierLocal@0x00501770");
                    return false;
                }
            }
            var canonical = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (attributes != null)
                foreach (KeyValuePair<string, int> attribute in attributes)
                {
                    string key = (attribute.Key ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
                    if (!string.IsNullOrWhiteSpace(key))
                        canonical[key] = attribute.Value;
                }
            var runtime = new MonsterAttributeModifier
            {
                LocalId = unchecked((uint)--monster.ModifierLocalIdGenerator),
                TerminateWhenHitChance = terminateWhenHitChance,
                SkillLevel = skillLevel,
                ModifierKey = modifierKey,
                ModifierPath = modifierPath,
                SkillPath = skillPath,
                EffectPath = effectPath,
                SourceEntityId = sourceEntityId,
                PowerLevel = powerLevel,
                StackRule = stackRule,
                Attributes = canonical,
                RemainingTicks = Math.Max(0, durationTicks),
                Permanent = durationTicks <= 0,
                RemoveOnDeath = removeOnDeath
            };
            if (existingIndex >= 0)
                monster.AttributeModifiers[existingIndex] = runtime;
            else
                monster.AttributeModifiers.Add(runtime);
            Debug.LogError($"[MON-ATTRMOD] {(existingIndex >= 0 ? "replace" : "add")} target={monster.Name}#{monster.EntityId} modifier={modifierPath} attributes={string.Join(",", canonical)} durationTicks={durationTicks} permanent={runtime.Permanent} power={powerLevel} stack={stackRule ?? ""} sourceFunction=SpellModEffect::doEffect@0x00554460 Modifiers::addModifierLocal@0x00501770");
            return true;
        }

        private bool RemoveMonsterAuthoredAttributeModifier(Monster monster, string modifierKey, string source)
        {
            if (monster?.AttributeModifiers == null || string.IsNullOrWhiteSpace(modifierKey))
                return false;
            int removed = monster.AttributeModifiers.RemoveAll(modifier =>
                modifier != null
                && (string.Equals(modifier.ModifierKey, modifierKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(modifier.ModifierPath, modifierKey, StringComparison.OrdinalIgnoreCase)));
            if (removed > 0)
                Debug.LogError($"[MON-ATTRMOD] remove target={monster.Name}#{monster.EntityId} modifier={modifierKey} count={removed} source={source ?? "unknown"} sourceFunction=Modifiers::removeModifierLocal@0x00501B50");
            return removed > 0;
        }

        public void ApplyMonsterRegenBonusModifier(Monster monster, string modifierKey, int hitPointRegenBonus, bool overrideTable, int durationTicks, bool removeOnDeath)
        {
            if (monster == null || !monster.IsAlive || durationTicks <= 0)
                return;
            monster.AttributeModifiers ??= new System.Collections.Generic.List<MonsterAttributeModifier>();
            for (int i = 0; i < monster.AttributeModifiers.Count; i++)
            {
                if (monster.AttributeModifiers[i].ModifierKey == modifierKey)
                {
                    monster.AttributeModifiers[i].HitPointRegenBonus = hitPointRegenBonus;
                    monster.AttributeModifiers[i].OverrideTable = overrideTable;
                    monster.AttributeModifiers[i].RemainingTicks = durationTicks;
                    monster.AttributeModifiers[i].Permanent = false;
                    monster.AttributeModifiers[i].RemoveOnDeath = removeOnDeath;
                    return;
                }
            }
            monster.AttributeModifiers.Add(new MonsterAttributeModifier
            {
                ModifierKey = modifierKey,
                HitPointRegenBonus = hitPointRegenBonus,
                OverrideTable = overrideTable,
                RemainingTicks = durationTicks,
                Permanent = false,
                RemoveOnDeath = removeOnDeath
            });
            Debug.LogError($"[MON-ATTRMOD-APPLY] entity={monster.EntityId} key={modifierKey} regenBonus={hitPointRegenBonus} override={overrideTable} durationTicks={durationTicks}");
        }

        private void ApplyMonsterManaRegen(Monster monster, uint tick, string source)
        {
            if (monster == null) return;
            if (!monster.IsAlive || monster.MaxManaWire == 0)
            {
                ResetMonsterManaRegenClock(monster, tick);
                _monsterManaRegenCooldownTicks.Remove(monster.EntityId);
                return;
            }
            if (monster.CurrentManaWire > monster.MaxManaWire)
                monster.CurrentManaWire = monster.MaxManaWire;
            _monsterManaRegenCooldownTicks.TryGetValue(monster.EntityId, out ushort cooldown);
            if (monster.CurrentManaWire >= monster.MaxManaWire && cooldown == 0)
            {
                ResetMonsterManaRegenClock(monster, tick);
                return;
            }
            if (!_monsterManaRegenLastTick.TryGetValue(monster.EntityId, out uint lastTick))
                lastTick = tick > 0 ? tick - 1u : 0u;
            int ticks = tick > lastTick ? (int)Math.Min((uint)int.MaxValue, tick - lastTick) : 0;
            if (ticks <= 0) return;
            _monsterManaRegenLastTick[monster.EntityId] = tick;

            uint oldMana = monster.CurrentManaWire;
            uint mana = oldMana;
            for (int tickIndex = 0; tickIndex < ticks && (mana < monster.MaxManaWire || cooldown > 0); tickIndex++)
            {
                if (cooldown > 0)
                    cooldown--;

                int regenWire = ComputeMonsterManaRegenDeltaWire(monster, cooldown > 0);
                if (regenWire == 0) continue;
                mana = ApplyUnitHPShiftWire(mana, monster.MaxManaWire, regenWire);
            }

            if (cooldown > 0)
                _monsterManaRegenCooldownTicks[monster.EntityId] = cooldown;
            else
                _monsterManaRegenCooldownTicks.Remove(monster.EntityId);

            if (oldMana == mana) return;
            monster.CurrentManaWire = mana;
            Debug.LogError($"[MON-MANA-REGEN] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} mana={oldMana / 256f:F2}->{mana / 256f:F2}/{monster.MaxManaWire / 256f:F2} ticks={ticks} cooldown={cooldown} factor={ResolveMonsterManaRegenFactor(monster)}");
        }

        private uint ApplyMonsterVitalsRegen(Monster monster, uint tick, string source)
        {
            uint hp = ApplyMonsterHealthRegen(monster, tick, source);
            ApplyMonsterManaRegen(monster, tick, source);
            return hp;
        }

        private uint GetRuntimeMonsterHPWire(Monster monster, string source = null)
        {
            return PeekRuntimeMonsterHPWire(monster);
        }

        private static string BuildMonsterModifierKey(uint targetEntityId, uint sourceEntityId, SpellData spell)
        {
            string modifierId = spell?.ProjectileModifierId;
            if (string.IsNullOrEmpty(modifierId))
                modifierId = spell?.ProjectileModifierEffectId ?? "modifier";
            if (string.Equals(spell?.ProjectileModifierStackRule, "UNIQUEBYTYPE", StringComparison.OrdinalIgnoreCase))
                return $"{targetEntityId}:{modifierId}";
            return $"{targetEntityId}:{sourceEntityId}:{modifierId}";
        }

        private void RemoveMonsterModifiersForTarget(uint targetEntityId, string source)
        {
            if (targetEntityId == 0 || _activeMonsterModifiers.Count == 0) return;
            var keys = _activeMonsterModifiers
                .Where(modifierEntry => modifierEntry.Value != null && modifierEntry.Value.TargetEntityId == targetEntityId)
                .Select(modifierEntry => modifierEntry.Key)
                .ToList();
            foreach (string key in keys)
                _activeMonsterModifiers.Remove(key);
            if (keys.Count > 0)
                Debug.LogError($"[POISON-SHOT-MOD] remove target={targetEntityId} count={keys.Count} source={source ?? "unknown"}");
        }

        private string BuildPlayerDamageModifierKey(uint targetEntityId, uint sourceEntityId, MonsterDamageModifierSkillEffect effect)
        {
            return BuildPlayerRuntimeModifierKey(targetEntityId, sourceEntityId, effect?.ModifierPath ?? effect?.ModifierEffectPath ?? "modifier", effect?.StackRule ?? string.Empty, CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF);
        }

        private string BuildPlayerRuntimeModifierKey(uint targetEntityId, uint sourceEntityId, string modifierId, string stackRule, byte sourceIsSelf)
        {
            string key = BuildPlayerModifierStackKey(targetEntityId, sourceEntityId, modifierId, stackRule, sourceIsSelf);
            if (IsAdditiveModifierStack(stackRule))
                return $"{key}:stack:{_nextPlayerModifierStackSerial++}";
            return key;
        }

        private static bool IsAdditiveModifierStack(string stackRule)
        {
            return string.IsNullOrWhiteSpace(stackRule) || string.Equals(stackRule, "NONE", StringComparison.OrdinalIgnoreCase) || string.Equals(stackRule, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildPlayerModifierStackKey(uint targetEntityId, uint sourceEntityId, string modifierId, string stackRule, byte sourceIsSelf)
        {
            if (string.IsNullOrWhiteSpace(modifierId))
                modifierId = "modifier";
            if (string.Equals(stackRule, "UNIQUEBYSOURCE", StringComparison.OrdinalIgnoreCase) && sourceIsSelf == 0 && sourceEntityId != 0)
                return $"{targetEntityId}:{sourceEntityId}:{modifierId}";
            return $"{targetEntityId}:{modifierId}";
        }

        private static uint ResolveActivePlayerModifierRemainingTicks(ActivePlayerDamageModifier mod, int nowTick)
        {
            if (mod == null || mod.DurationTicks == 0)
                return 0;
            int remaining = mod.ExpireTick - nowTick;
            if (remaining <= 0)
                return 0;
            return (uint)Math.Min(ushort.MaxValue, remaining);
        }

        private static bool ShouldAcceptModifierStack(string stackRule, uint incomingPowerLevel, uint incomingDurationTicks, uint existingPowerLevel, uint existingDurationTicks)
        {
            if (string.IsNullOrWhiteSpace(stackRule))
                return true;
            if (string.Equals(stackRule, "UNIQUEBYSOURCE", StringComparison.OrdinalIgnoreCase))
                return incomingPowerLevel >= existingPowerLevel;
            if (string.Equals(stackRule, "UNIQUEBYTYPE", StringComparison.OrdinalIgnoreCase))
            {
                if (incomingPowerLevel <= existingPowerLevel)
                {
                    if (incomingDurationTicks == 0)
                        return existingDurationTicks != 0;
                    if (existingDurationTicks == 0 || incomingDurationTicks <= existingDurationTicks)
                        return false;
                }
                return true;
            }
            return true;
        }

        private bool TryAllocatePlayerModifierNetworkId(string key, bool track, out uint id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(key)) key = "modifier";
            if (_nextPlayerModifierNetworkId < FirstPlayerModifierNetworkId
                || _nextPlayerModifierNetworkId > LastPlayerModifierNetworkId)
            {
                Debug.LogError($"[PLAYER-MODIFIER] state=blocked reason=modifier-id-range-exhausted key={key}");
                return false;
            }
            id = _nextPlayerModifierNetworkId++;
            if (track)
                _playerModifierNetworkIds[key] = id;
            return true;
        }

        private void ReleasePlayerModifierNetworkId(string key, uint id)
        {
            if (!string.IsNullOrWhiteSpace(key)
                && _playerModifierNetworkIds.TryGetValue(key, out uint trackedId)
                && trackedId == id)
                _playerModifierNetworkIds.Remove(key);
        }

        private uint ResolveMonsterSkillPowerLevelWire(Monster monster, string skillPath)
        {
            int level = ResolveMonsterActiveSkillLevel(monster, skillPath);
            GCNode skill = null;
            if (!string.IsNullOrWhiteSpace(skillPath))
                skill = GCDatabase.Instance?.ResolveWithInheritance(skillPath);
            GCNode desc = skill?.GetChild("Description") ?? skill;
            int requiredLevel = desc != null ? desc.GetInt("RequiredLevel", 1) : 1;
            int requiredLevelIncF32 = desc != null ? desc.GetFixed32("RequiredLevelInc", 5 * 0x100) : 5 * 0x100;
            return unchecked((uint)SpellData.ResolvePowerLevelF32(requiredLevel, requiredLevelIncF32, level));
        }

        private static int ResolveMonsterActiveSkillLevel(Monster monster, string skillPath)
        {
            if (monster == null)
                return 1;
            if (monster.SelectedActiveSkill != null
                && string.Equals(monster.SelectedActiveSkill.Path, skillPath, StringComparison.OrdinalIgnoreCase))
                return Math.Max(1, (int)monster.SelectedActiveSkill.SkillLevel);
            if (monster.PrimaryAttackSkill != null
                && string.Equals(monster.PrimaryAttackSkill.Path, skillPath, StringComparison.OrdinalIgnoreCase))
                return Math.Max(1, (int)monster.PrimaryAttackSkill.SkillLevel);
            for (int skillIndex = 0; skillIndex < monster.ActiveSkills.Count; skillIndex++)
            {
                MonsterActiveSkillRuntime candidate = monster.ActiveSkills[skillIndex];
                if (candidate != null && string.Equals(candidate.Path, skillPath, StringComparison.OrdinalIgnoreCase))
                    return Math.Max(1, (int)candidate.SkillLevel);
            }
            return 1;
        }

        private PlayerModifierLifecycle ResolvePlayerModifierLifecycle(string modifierPath)
        {
            var lifecycle = new PlayerModifierLifecycle();
            if (string.IsNullOrWhiteSpace(modifierPath))
                return lifecycle;

            GCNode modifier = GCDatabase.Instance?.ResolveWithInheritance(modifierPath);
            GCNode desc = modifier?.GetChild("Description") ?? modifier;
            if (desc == null)
                return lifecycle;

            lifecycle.Visual = desc.GetString("Visual", null);
            lifecycle.InitSound = desc.GetString("InitSound", null);
            lifecycle.InitEffect = desc.GetString("InitEffect", null);
            lifecycle.RemoveEffect = desc.GetString("RemoveEffect", null);
            lifecycle.OverlayIcon = desc.GetString("OverlayIcon", null);
            lifecycle.OverlayDuration = desc.GetInt("OverlayDuration", 0);
            return lifecycle;
        }

        private void RaisePlayerModifierAddWithId(Monster sourceMonster, CombatTarget target, string modifierKey, string gcType, uint id, byte level, uint powerLevel, uint durationTicks, byte sourceIsSelf, bool replace, string skillPath, string effectPath, string source, string sourceFunction)
        {
            if (target == null || string.IsNullOrWhiteSpace(gcType))
                return;
            OnPlayerModifierNetworkEvent?.Invoke(sourceMonster, target, new PlayerModifierNetworkEvent
            {
                Add = true,
                ModifierKey = modifierKey,
                GCType = gcType,
                ModifierId = id,
                Level = level,
                PowerLevel = powerLevel,
                DurationTicks = durationTicks,
                SourceIsSelf = sourceIsSelf,
                Replace = replace,
                SkillPath = skillPath,
                EffectPath = effectPath,
                Source = source,
                SourceFunction = sourceFunction,
                Lifecycle = ResolvePlayerModifierLifecycle(gcType)
            });
        }

        private void RaisePlayerModifierRemove(Monster sourceMonster, CombatTarget target, string modifierKey, string gcType, string skillPath, string effectPath, string source, string sourceFunction)
        {
            if (target == null || string.IsNullOrWhiteSpace(modifierKey))
                return;
            if (!_playerModifierNetworkIds.TryGetValue(modifierKey, out uint id))
                return;
            _playerModifierNetworkIds.Remove(modifierKey);
            OnPlayerModifierNetworkEvent?.Invoke(sourceMonster, target, new PlayerModifierNetworkEvent
            {
                Add = false,
                ModifierKey = modifierKey,
                GCType = gcType,
                ModifierId = id,
                SkillPath = skillPath,
                EffectPath = effectPath,
                Source = source,
                SourceFunction = sourceFunction,
                Lifecycle = ResolvePlayerModifierLifecycle(gcType)
            });
        }

        private bool RaisePlayerModifierAdd(Monster sourceMonster, CombatTarget target, string modifierKey, string gcType, byte level, uint powerLevel, uint durationTicks, byte sourceIsSelf, bool replace, string skillPath, string effectPath, string source, string sourceFunction, bool trackModifierId = true)
        {
            if (target == null || string.IsNullOrWhiteSpace(gcType))
                return false;
            if (!TryAllocatePlayerModifierNetworkId(modifierKey, trackModifierId, out uint id))
                return false;
            RaisePlayerModifierAddWithId(sourceMonster, target, modifierKey, gcType, id, level, powerLevel, durationTicks, sourceIsSelf, replace, skillPath, effectPath, source, sourceFunction);
            return true;
        }

        public int ReplayPlayerAttributeModifiers(uint targetEntityId, string source)
        {
            if (!_players.TryGetValue(targetEntityId, out CombatPlayer target) || target?.PlayerState == null)
                return 0;
            List<PlayerState.AttributeModifierSnapshot> snapshots = target.PlayerState.GetAttributeModifierSnapshots();
            int replayed = 0;
            for (int snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
            {
                PlayerState.AttributeModifierSnapshot snapshot = snapshots[snapshotIndex];
                string modifierKey = snapshot.ModifierKey ?? snapshot.ModifierType;
                if (string.IsNullOrWhiteSpace(modifierKey)
                    || !_playerModifierNetworkIds.TryGetValue(modifierKey, out uint modifierId))
                    continue;
                _activeMonsters.TryGetValue(snapshot.SourceEntityId, out Monster sourceMonster);
                OnPlayerModifierNetworkEvent?.Invoke(sourceMonster, target, new PlayerModifierNetworkEvent
                {
                    Add = true,
                    ModifierKey = modifierKey,
                    GCType = snapshot.ModifierType,
                    ModifierId = modifierId,
                    Level = snapshot.Level,
                    PowerLevel = snapshot.PowerLevel,
                    DurationTicks = snapshot.RemainingTicks,
                    SourceIsSelf = snapshot.SourceIsSelf,
                    Replace = false,
                    SkillPath = snapshot.SkillPath,
                    EffectPath = snapshot.EffectPath,
                    Source = source ?? "zone-modifier-replay",
                    SourceFunction = "Modifiers::processAddModifier@0x00502280",
                    Lifecycle = ResolvePlayerModifierLifecycle(snapshot.ModifierType)
                });
                replayed++;
            }
            Debug.LogError($"[PLAYER-MODIFIER] state=replayed target={targetEntityId} count={replayed} source={source ?? "zone-modifier-replay"}");
            return replayed;
        }

        public int RemoveExternalPlayerAttributeModifiers(uint targetEntityId, string source)
        {
            if (!_players.TryGetValue(targetEntityId, out CombatPlayer target) || target?.PlayerState == null)
                return 0;
            int removed = target.PlayerState.RemoveAttributeModifiersFromExternalSources(targetEntityId, source);
            Debug.LogError($"[PLAYER-MODIFIER] state=external-sources-cleared target={targetEntityId} count={removed} source={source ?? "zone-transition"}");
            return removed;
        }

        private void HandlePlayerAttributeModifierRemoved(PlayerState state, PlayerState.AttributeModifierRemoval removal)
        {
            if (state == null || removal == null)
                return;
            CombatPlayer target = GetPlayersInEntityOrder().FirstOrDefault(player => player != null && ReferenceEquals(player.PlayerState, state));
            if (target == null)
                return;
            OnPlayerAttributeModifierRemoved?.Invoke(target, removal);
            _activeMonsters.TryGetValue(removal.SourceEntityId, out Monster sourceMonster);
            RaisePlayerModifierRemove(sourceMonster, target, removal.ModifierKey, removal.ModifierType, removal.SkillPath, removal.EffectPath, removal.Source ?? "unknown", removal.SourceFunction ?? "Modifiers::processRemoveModifier@0x00502390");
            Debug.LogError($"[PLAYER-MODIFIER] network-remove target={target.Name}#{target.EntityId} modifier={removal.ModifierType ?? ""} key={removal.ModifierKey ?? ""} sourceMonster={sourceMonster?.Name ?? "none"}#{sourceMonster?.EntityId ?? 0u} source={removal.Source ?? "unknown"} sourceFunction={removal.SourceFunction ?? "Modifiers::processRemoveModifier@0x00502390"}");
        }

        public bool TryCommitPlayerAttributeModifierDeathEvent(CombatPlayer target, string source)
        {
            if (target?.PlayerState == null)
                return false;
            if (!target.PlayerState.HasRemoveOnDeathAttributeModifiers())
                return true;
            Delegate[] handlers = OnPlayerAttributeModifiersRemovingForDeath?.GetInvocationList();
            if (handlers != null)
            {
                foreach (Delegate handler in handlers)
                {
                    try
                    {
                        if (!((Func<CombatPlayer, string, bool>)handler)(target, source))
                        {
                            Debug.LogError($"[PLAYER-MODIFIER] state=blocked phase=death-persist target={target.EntityId} source={source ?? "unknown"}");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[PLAYER-MODIFIER] state=blocked phase=death-persist target={target.EntityId} source={source ?? "unknown"} errorType={ex.GetType().Name} message='{ex.Message}'");
                        return false;
                    }
                }
            }
            target.PlayerState.DoAttributeModifierDeathEvent(source);
            return true;
        }

        private void RemovePlayerDamageModifiersForTarget(uint targetEntityId, string source, bool removeOnlyOnDeath = true)
        {
            if (targetEntityId == 0 || _activePlayerDamageModifiers.Count == 0) return;
            var removals = _activePlayerDamageModifiers
                .Where(modifierEntry => modifierEntry.Value != null && modifierEntry.Value.TargetEntityId == targetEntityId && (!removeOnlyOnDeath || modifierEntry.Value.RemoveOnDeath))
                .OrderBy(modifierEntry => modifierEntry.Value.Order)
                .Select(modifierEntry => new { Key = modifierEntry.Key, Mod = modifierEntry.Value })
                .ToList();
            TryGetCombatTarget(targetEntityId, out CombatTarget target);
            foreach (var removal in removals)
            {
                _activeMonsters.TryGetValue(removal.Mod.SourceEntityId, out Monster sourceMonster);
                RaisePlayerModifierRemove(sourceMonster, target, removal.Key, removal.Mod.ModifierPath, removal.Mod.SkillPath, removal.Mod.EffectPath, source ?? "unknown", "Modifiers::processRemoveModifier@0x00502390");
                _activePlayerDamageModifiers.Remove(removal.Key);
            }
            if (removals.Count > 0)
                Debug.LogError($"[PLAYER-EFFECTMOD] remove target={targetEntityId} count={removals.Count} source={source ?? "unknown"} removeOnlyOnDeath={removeOnlyOnDeath} sourceFunction=ModifierDesc.RemoveOnDeath");
        }

        private void RemovePlayerModifiersFromSource(uint sourceEntityId, string source)
        {
            if (sourceEntityId == 0) return;
            RemoveMonsterAurasFromSource(sourceEntityId, source);
            _activeMonsters.TryGetValue(sourceEntityId, out Monster sourceMonster);
            var removals = _activePlayerDamageModifiers
                .Where(modifierEntry => modifierEntry.Value != null && modifierEntry.Value.SourceEntityId == sourceEntityId)
                .OrderBy(modifierEntry => modifierEntry.Value.Order)
                .Select(modifierEntry => new { Key = modifierEntry.Key, Mod = modifierEntry.Value })
                .ToList();
            foreach (var removal in removals)
            {
                TryGetCombatTarget(removal.Mod.TargetEntityId, out CombatTarget target);
                RaisePlayerModifierRemove(sourceMonster, target, removal.Key, removal.Mod.ModifierPath, removal.Mod.SkillPath, removal.Mod.EffectPath, source ?? "unknown", "Modifier::update@0x004FF1B0 Modifiers::processRemoveModifier@0x00502390");
                _activePlayerDamageModifiers.Remove(removal.Key);
            }

            int attributeRemoved = 0;
            foreach (var player in GetPlayersInEntityOrder().ToList())
            {
                if (player?.PlayerState == null)
                    continue;
                attributeRemoved += player.PlayerState.RemoveAttributeModifiersFromSource(sourceEntityId, source ?? "source-unit-removed");
            }

            if (removals.Count > 0 || attributeRemoved > 0)
                Debug.LogError($"[PLAYER-MODIFIER] remove-source sourceEntity={sourceEntityId} effectMods={removals.Count} attributeMods={attributeRemoved} source={source ?? "unknown"} sourceFunction=Modifier::update@0x004FF1B0 Modifiers::processRemoveModifier@0x00502390");
        }

        private bool ApplyPlayerDamageModifierFromMonster(Monster sourceMonster, CombatTarget target, MonsterDamageModifierSkillEffect effect, string source, bool auraRefresh = false)
        {
            if (sourceMonster == null || target == null || !target.HasUnitState || effect == null)
                return false;
            if (!target.IsAlive || target.CurrentHPWire == 0)
                return false;

            int applyTick = (int)_combatTick;
            ushort durationTicks = effect.DurationTicks > 0 ? effect.DurationTicks : ComputeSpellModDurationTicks(effect.DurationF32);
            ushort frequencyTicks = effect.FrequencyTicks > 0 ? effect.FrequencyTicks : ComputeEffectModFrequencyTicks(effect.FrequencyF32 > 0 ? effect.FrequencyF32 : 0x100);
            int intervalTicks = Math.Max(1, (int)frequencyTicks);
            int maxTicks = auraRefresh && durationTicks > 0
                ? Math.Max(1, (durationTicks + intervalTicks - 1) / intervalTicks)
                : ComputeEffectModApplyTickBudget(durationTicks, frequencyTicks);
            int expireTick = durationTicks == 0 ? int.MaxValue : applyTick + durationTicks;
            string key = BuildPlayerDamageModifierKey(target.EntityId, sourceMonster.EntityId, effect);
            bool replace = _activePlayerDamageModifiers.TryGetValue(key, out ActivePlayerDamageModifier existing);
            uint powerLevel = ResolveMonsterSkillPowerLevelWire(sourceMonster, effect.SkillPath);
            if (auraRefresh && replace)
            {
                bool sameAura = existing.SourceEntityId == sourceMonster.EntityId
                    && existing.PowerLevel == powerLevel
                    && string.Equals(existing.SkillPath, effect.SkillPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.ModifierPath, effect.ModifierPath, StringComparison.OrdinalIgnoreCase);
                if (sameAura)
                {
                    existing.DurationTicks = durationTicks;
                    existing.ExpireTick = expireTick;
                    existing.MaxTicks = maxTicks == int.MaxValue
                        ? int.MaxValue
                        : Math.Max(existing.MaxTicks, existing.TicksApplied + maxTicks);
                    Debug.LogError($"[PLAYER-AURA-EFFECTMOD] refresh target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} skill={effect.SkillPath} modifier={effect.ModifierPath} durationTicks={durationTicks} nextTick={existing.NextTick} expireTick={expireTick} ticks={existing.TicksApplied}/{existing.MaxTicks} source={source ?? "unknown"} sourceFunction=AuraMod::addModifier@0x0055EE80");
                    return true;
                }
                if (powerLevel <= existing.PowerLevel)
                {
                    Debug.LogError($"[PLAYER-AURA-EFFECTMOD] reject target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} skill={effect.SkillPath} modifier={effect.ModifierPath} incomingPower={powerLevel} existingPower={existing.PowerLevel} source={source ?? "unknown"} sourceFunction=AuraMod::addModifier@0x0055EE80");
                    return false;
                }
            }
            uint existingRemainingTicks = replace ? ResolveActivePlayerModifierRemainingTicks(existing, applyTick) : 0u;
            bool stackAccepted = !replace || ShouldAcceptModifierStack(effect.StackRule, powerLevel, durationTicks, existing.PowerLevel, existingRemainingTicks);
            if (!RaisePlayerModifierAdd(sourceMonster, target, key, effect.ModifierPath, 1, powerLevel, durationTicks, CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF, replace, effect.SkillPath, effect.EffectPath, source ?? "unknown", "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280", stackAccepted))
                return false;
            if (!stackAccepted)
            {
                Debug.LogError($"[PLAYER-MODIFIER-STACK] result=reject target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} skill={effect.SkillPath} modifier={effect.ModifierPath} stack={effect.StackRule ?? "UNKNOWN"} key={key} incomingPower={powerLevel} existingPower={existing.PowerLevel} incomingDuration={durationTicks} existingRemaining={existingRemainingTicks} sourceIsSelf={CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF} sourceFunction=Modifiers::addModifierLocal@0x00501770");
                return false;
            }
            long modifierOrder = replace ? existing.Order : ++_nextCombatModifierOrder;
            _activePlayerDamageModifiers[key] = new ActivePlayerDamageModifier
            {
                Order = modifierOrder,
                TargetEntityId = target.EntityId,
                SourceEntityId = sourceMonster.EntityId,
                SkillPath = effect.SkillPath,
                EffectPath = effect.EffectPath,
                ModifierPath = effect.ModifierPath,
                ModifierEffectPath = effect.ModifierEffectPath,
                AttackType = effect.AttackType,
                DamageType = effect.DamageType,
                DamageTypeId = effect.DamageTypeId,
                DamageKind = effect.DamageKind,
                DamageModF32 = effect.DamageModF32,
                DamageVolatilityF32 = effect.DamageVolatilityF32,
                ChanceWire = effect.ChanceWire,
                CriticalChanceF32 = effect.CriticalChanceF32,
                DamageStunMod = effect.DamageStunMod,
                DamageStunModFactorF32 = effect.DamageStunModFactorF32,
                DamageStunModIncF32 = effect.DamageStunModIncF32,
                SourceLevel = Math.Max(1, (int)sourceMonster.Level),
                SourceIntellect = ResolveMonsterSpellIntellect(sourceMonster),
                SourceAgility = ResolveMonsterSpellAgility(sourceMonster),
                PowerLevel = powerLevel,
                DurationTicks = durationTicks,
                FrequencyTicks = frequencyTicks,
                ApplyTick = applyTick,
                NextTick = applyTick + intervalTicks,
                ExpireTick = expireTick,
                MaxTicks = maxTicks,
                TicksApplied = 0,
                RemoveOnDeath = effect.RemoveOnDeath,
                StackRule = effect.StackRule,
                ModifierKey = key
            };

            Debug.LogError($"[PLAYER-EFFECTMOD] {(replace ? "replace" : "add")} target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} skill={effect.SkillPath} modifier={effect.ModifierPath} effect={effect.ModifierEffectPath} attackType={effect.AttackType} damageTypeId={effect.DamageTypeId} damageKind={effect.DamageKind} durationTicks={durationTicks} frequencyTicks={frequencyTicks} maxTicks={maxTicks} power={powerLevel} stack={effect.StackRule ?? "UNKNOWN"} firstTick={applyTick + intervalTicks} expireTick={expireTick} source={source ?? "unknown"} sourceFunction=SpellModEffect::doEffect@0x00554460 EffectMod::init@0x0055FE20 EffectMod::update@0x0055FE70 Modifiers::addModifierLocal@0x00501770");
            return true;
        }

        private static int ResolveMonsterSpellIntellect(Monster monster)
        {
            if (monster?.Slots == null) return 10;
            return Math.Max(1, monster.Slots.Get(UnitSlot.Intellect, 10));
        }

        private static int ResolveMonsterSpellAgility(Monster monster)
        {
            if (monster?.Slots == null) return 10;
            return Math.Max(1, monster.Slots.Get(UnitSlot.Agility, 10));
        }

        public MonsterModifierApplyResult AdvancePlayerDamageModifierRuntime(uint onlyTargetEntityId, uint simulationTick, string source, long? onlyOrder = null)
        {
            int nowTick = (int)simulationTick;
            var aggregate = new MonsterModifierApplyResult
            {
                Reason = source ?? "player-effectmod-runtime"
            };
            if (_activePlayerDamageModifiers.Count == 0)
                return aggregate;
            var keys = _activePlayerDamageModifiers
                .OrderBy(entry => entry.Value?.Order ?? long.MaxValue)
                .Select(entry => entry.Key)
                .ToList();
            foreach (string key in keys)
            {
                if (!_activePlayerDamageModifiers.TryGetValue(key, out var mod) || mod == null)
                    continue;
                if ((onlyOrder.HasValue && mod.Order != onlyOrder.Value) || (onlyTargetEntityId != 0 && mod.TargetEntityId != onlyTargetEntityId))
                    continue;
                if (!TryGetCombatTarget(mod.TargetEntityId, out var target) || target == null || !target.HasUnitState)
                {
                    _activePlayerDamageModifiers.Remove(key);
                    _playerModifierNetworkIds.Remove(key);
                    Debug.LogError($"[PLAYER-EFFECTMOD] remove target={mod.TargetEntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=missing-target sourceFunction=EffectMod::update@0x0055FE70");
                    continue;
                }
                if (!target.IsAlive || target.CurrentHPWire == 0)
                {
                    if (mod.RemoveOnDeath)
                    {
                        RaisePlayerModifierRemove(null, target, key, mod.ModifierPath, mod.SkillPath, mod.EffectPath, source ?? "unknown", "ModifierDesc.RemoveOnDeath Modifiers::processRemoveModifier@0x00502390");
                        _activePlayerDamageModifiers.Remove(key);
                        Debug.LogError($"[PLAYER-EFFECTMOD] remove target={target.Name}#{target.EntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=RemoveOnDeath sourceFunction=ModifierDesc.RemoveOnDeath");
                    }
                    continue;
                }
                if (!_activeMonsters.TryGetValue(mod.SourceEntityId, out var sourceMonster) || sourceMonster == null)
                {
                    RaisePlayerModifierRemove(null, target, key, mod.ModifierPath, mod.SkillPath, mod.EffectPath, source ?? "unknown", "EffectMod::update@0x0055FE70 Modifiers::processRemoveModifier@0x00502390");
                    _activePlayerDamageModifiers.Remove(key);
                    Debug.LogError($"[PLAYER-EFFECTMOD] remove target={target.Name}#{target.EntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=missing-source-monster sourceFunction=EffectMod::update@0x0055FE70");
                    continue;
                }
                if (!MatchesInstance(sourceMonster, target.InstanceKey))
                {
                    RaisePlayerModifierRemove(sourceMonster, target, key, mod.ModifierPath, mod.SkillPath, mod.EffectPath, source ?? "unknown", "EffectMod::update@0x0055FE70 Modifiers::processRemoveModifier@0x00502390");
                    _activePlayerDamageModifiers.Remove(key);
                    Debug.LogError($"[PLAYER-EFFECTMOD] remove target={target.Name}#{target.EntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=instance-mismatch sourceFunction=EffectMod::update@0x0055FE70");
                    continue;
                }

                MersenneTwister modRng = GetRoomRngForMonster(sourceMonster);
                if (modRng == null)
                {
                    RuntimeEvidence.LogFallbackHit(
                        "rng-instance",
                        "player-effectmod-missing-instance-rng",
                        $"target={target.EntityId} sourceMonster={sourceMonster.EntityId} source={source ?? "unknown"}",
                        64);
                    Debug.LogError($"[PLAYER-EFFECTMOD] skip tick target={target.Name}#{target.EntityId} sourceMonster={sourceMonster.Name}#{sourceMonster.EntityId} source={source ?? "unknown"} reason=missing-instance-rng");
                    continue;
                }
                bool expiredBeforeTick = mod.DurationTicks > 0 && nowTick >= mod.ExpireTick;
                if (expiredBeforeTick && (mod.MaxTicks == 0 || mod.TicksApplied >= mod.MaxTicks || mod.NextTick > mod.ExpireTick))
                {
                    RaisePlayerModifierRemove(sourceMonster, target, key, mod.ModifierPath, mod.SkillPath, mod.EffectPath, source ?? "unknown", "Modifiers::update@0x00501E50 Modifiers::removeModifierLocal@0x00501B50");
                    _activePlayerDamageModifiers.Remove(key);
                    Debug.LogError($"[PLAYER-EFFECTMOD] complete target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} ticks={mod.TicksApplied}/{mod.MaxTicks} hp={target.CurrentHPWire} reason=duration-expired expireTick={mod.ExpireTick} nowTick={nowTick} source={source ?? "unknown"} sourceFunction=Modifier::update@0x004FF1B0 EffectMod::update@0x0055FE70");
                    continue;
                }
                if (nowTick < mod.NextTick)
                    continue;

                int dueTicksProcessed = 0;
                while (dueTicksProcessed == 0 && nowTick >= mod.NextTick && mod.TicksApplied < mod.MaxTicks && mod.NextTick <= mod.ExpireTick && target.IsAlive && target.CurrentHPWire > 0)
                {
                    dueTicksProcessed++;
                    int tickIndex = mod.TicksApplied + 1;
                    int chanceWire = Math.Clamp(mod.ChanceWire, 0, 0x6400);
                    uint chanceRaw = 0;
                    uint chanceRoll = 0;
                    if (chanceWire < 0x6400)
                    {
                        chanceRaw = RngLedger.Generate(modRng, "room", "EffectMod::applyEffect:SpellDamageEffect::CheckChance", $"{sourceMonster.Name}#{sourceMonster.EntityId}->{target.Name}#{target.EntityId}");
                        chanceRoll = chanceRaw % 0x6464u;
                        if (chanceRoll >= (uint)chanceWire)
                        {
                            Debug.LogError($"[PLAYER-EFFECTMOD-TICK] target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} tick={tickIndex}/{mod.MaxTicks} result=MISS skill={mod.SkillPath} modifier={mod.ModifierPath} effect={mod.ModifierEffectPath} chanceWire={chanceWire} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} dueTick={mod.NextTick} nowTick={nowTick} rngAfter={modRng.CallsSinceReseed} sourceFunction=EffectMod::applyEffect@0x0055FF70 SpellDamageEffect::doEffect@0x0054FD20 SpellEffect::CheckChance@0x00545FF0");
                            mod.TicksApplied++;
                            aggregate.TicksApplied++;
                            mod.NextTick += Math.Max(1, (int)mod.FrequencyTicks);
                            continue;
                        }
                    }
                    int minDamage;
                    int maxDamage;
                    ComputeMonsterSpellDamageRange(sourceMonster, mod.DamageTypeId, mod.DamageModF32, mod.DamageVolatilityF32, out minDamage, out maxDamage);

                    uint damageRaw = 0;
                    if ((minDamage >> 8) != (maxDamage >> 8))
                        damageRaw = RngLedger.Generate(modRng, "room", "EffectMod::applyEffect:SpellDamageEffect::damage", $"{sourceMonster.Name}#{sourceMonster.EntityId}->{target.Name}#{target.EntityId}");
                    int rawDamageWire = DamageResolver.RollSpellDamageRange(minDamage, maxDamage, damageRaw);
                    uint damageWire = (uint)Math.Max(0, rawDamageWire);
                    uint beforeHP = target.CurrentHPWire;
                    DamageQueryResult query = ApplyPlayerDamageQueryWire(damageWire, target, sourceMonster, mod.DamageTypeId, mod.DamageKind, source ?? "EffectMod::applyEffect", mod.SourceLevel, modRng, true);
                    uint adjustedDamageWire = query.AdjustedDamageWire;
                    uint afterHP = beforeHP;
                    uint effectRaw = 0;
                    bool applied = adjustedDamageWire > 0;

                    if (applied)
                    {
                        ApplyCombatTargetQueriedDamage(sourceMonster, target, adjustedDamageWire, source);
                        afterHP = target.CurrentHPWire;
                        target.IsAlive = afterHP > 0;
                        uint appliedDamageWire = beforeHP > afterHP ? beforeHP - afterHP : 0u;
                        int damageStunMod = ResolveSpellDamageStunMod(mod.DamageStunMod, sourceMonster.Slots?.Get(UnitSlot.StunMod, sourceMonster.StunMod) ?? sourceMonster.StunMod);
                        effectRaw = ConsumeOnApplyDamageEffectRng(modRng, "monster-effectmod", sourceMonster, target, beforeHP, afterHP, appliedDamageWire, damageStunMod, source ?? "EffectMod::applyEffect");
                    }
                    if (applied)
                        DispatchMonsterHitProcs(sourceMonster, target, mod.AttackType, mod.DamageTypeId, adjustedDamageWire, source ?? "EffectMod::applyEffect");
                    DispatchCombatTargetDamageEvent(target, modRng, target.EntityId, target.Name, source ?? "EffectMod::applyEffect");
                    if (mod.DamageKind != 3 && beforeHP > afterHP)
                        ApplyMonsterOnDamageCallback(sourceMonster, beforeHP - afterHP, source ?? "EffectMod::applyEffect");
                    if (afterHP == 0)
                        TryCommitCombatTargetDeathEvent(target, source ?? "EffectMod::target-death");

                    string resultName = applied ? "HIT" : query.ResultName;
                    Debug.LogError($"[PLAYER-EFFECTMOD-TICK] target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} tick={tickIndex}/{mod.MaxTicks} result={resultName} skill={mod.SkillPath} modifier={mod.ModifierPath} effect={mod.ModifierEffectPath} attackType={mod.AttackType} damageType={mod.DamageType} damageTypeId={mod.DamageTypeId} damageKind={mod.DamageKind} chanceWire={chanceWire} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} preQueryWire={damageWire} damageWire={adjustedDamageWire} hp={beforeHP}->{afterHP}/{target.MaxHPWire} range=[{minDamage},{maxDamage}] damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist={DamageResistLogCode(query)} dueTick={mod.NextTick} nowTick={nowTick} rngAfter={modRng.CallsSinceReseed} sourceFunction=EffectMod::applyEffect@0x0055FF70 SpellDamageEffect::doEffect@0x0054FD20");
                    Debug.LogError($"[CLIENT-DAMAGE-CONTRACT] source={source ?? "EffectMod::applyEffect"} target={target.Name}#{target.EntityId} damageTypeId={mod.DamageTypeId} damageKind={mod.DamageKind} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire={adjustedDamageWire} hp={beforeHP}->{afterHP}/{target.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} clientDamageTick={mod.NextTick} target=Avatar sourceFunction=Damage::apply@0x004F6580 Unit::onQueryApplyDamage@0x0050B9C0");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster-effectmod attacker={sourceMonster.Name}#{sourceMonster.EntityId} target={target.Name}#{target.EntityId} result={resultName} skill={mod.SkillPath} damageWire={adjustedDamageWire} preQueryWire={damageWire} hp={beforeHP}->{afterHP}/{target.MaxHPWire} marker=SPELL-MOD source={source} damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} rngPos={modRng.CallsSinceReseed}");
                    uint resolvedAfterHP = afterHP;
                    if (applied)
                    {
                        resolvedAfterHP = target.CurrentHPWire;
                        target.IsAlive = resolvedAfterHP > 0;
                    }
                    if (applied)
                        RaiseCombatTargetDamageResolved(sourceMonster, target, true, resolvedAfterHP, source != null ? $"EffectMod::applyEffect:{source}" : "EffectMod::applyEffect");

                    aggregate.DamageApplied |= applied;
                    aggregate.Died |= resolvedAfterHP == 0;
                    if (aggregate.TicksApplied == 0)
                        aggregate.OldHPWire = beforeHP;
                    aggregate.NewHPWire = resolvedAfterHP;
                    mod.TicksApplied++;
                    aggregate.TicksApplied++;
                    mod.NextTick += Math.Max(1, (int)mod.FrequencyTicks);
                    if (resolvedAfterHP == 0)
                    {
                        RemovePlayerDamageModifiersForTarget(target.EntityId, source ?? "EffectMod::target-death");
                        break;
                    }
                }

                if (_activePlayerDamageModifiers.TryGetValue(key, out var afterMod))
                {
                    bool targetDead = !target.IsAlive || target.CurrentHPWire == 0;
                    bool expired = afterMod.DurationTicks > 0 && nowTick >= afterMod.ExpireTick;
                    bool removeAfterTick = expired || (targetDead && afterMod.RemoveOnDeath);
                    if (removeAfterTick)
                    {
                        string reason = targetDead && afterMod.RemoveOnDeath ? "RemoveOnDeath" : "duration-expired";
                        RaisePlayerModifierRemove(sourceMonster, target, key, afterMod.ModifierPath, afterMod.SkillPath, afterMod.EffectPath, source ?? "unknown", reason == "RemoveOnDeath" ? "ModifierDesc.RemoveOnDeath Modifiers::processRemoveModifier@0x00502390" : "Modifiers::update@0x00501E50 Modifiers::removeModifierLocal@0x00501B50");
                        _activePlayerDamageModifiers.Remove(key);
                        Debug.LogError($"[PLAYER-EFFECTMOD] complete target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} ticks={afterMod.TicksApplied}/{afterMod.MaxTicks} hp={target.CurrentHPWire} reason={reason} expireTick={afterMod.ExpireTick} nowTick={nowTick} source={source ?? "unknown"} sourceFunction=EffectMod::update@0x0055FE70");
                    }
                }
            }

            return aggregate;
        }

        public MonsterModifierApplyResult ApplyProjectileModifierFromSpell(Monster target, uint sourceEntityId, PlayerState sourceState, SpellData spell, MersenneTwister rng, int skillLevel, string source)
        {
            var result = new MonsterModifierApplyResult
            {
                Reason = source ?? "spell-mod",
                OldHPWire = target != null ? PeekRuntimeMonsterHPWire(target) : 0,
                NewHPWire = target != null ? PeekRuntimeMonsterHPWire(target) : 0
            };

            if (target == null || spell == null || !spell.HasProjectileModifierDamage)
            {
                result.Reason = "missing-target-or-modifier";
                return result;
            }
            if (!target.IsAlive || PeekRuntimeMonsterHPWire(target) == 0)
            {
                result.Reason = "target-dead";
                return result;
            }
            if (sourceState == null)
            {
                result.Reason = "missing-source-state";
                return result;
            }
            if (rng == null)
            {
                result.Reason = "missing-rng";
                return result;
            }

            ushort durationTicks = ComputeSpellModDurationTicks(spell.ProjectileModifierDurationF32);
            ushort frequencyTicks = ComputeEffectModFrequencyTicks(spell.ProjectileModifierFrequencyF32 > 0 ? spell.ProjectileModifierFrequencyF32 : 0x100);
            int startTick = (int)_combatTick;
            int initialFrequencyCountdown = Math.Max(1, (int)frequencyTicks);
            string key = BuildMonsterModifierKey(target.EntityId, sourceEntityId, spell);
            bool replace = _activeMonsterModifiers.TryGetValue(key, out ActiveMonsterModifier existingModifier);
            uint powerLevel = unchecked((uint)spell.ResolvePowerLevelF32(skillLevel));
            uint existingRemainingTicks = replace && existingModifier.DurationTicksInitial != 0
                ? (uint)Math.Max(0, existingModifier.DurationTicksRemaining)
                : 0u;
            bool stackAccepted = !replace || ShouldAcceptModifierStack(
                spell.ProjectileModifierStackRule,
                powerLevel,
                durationTicks,
                existingModifier.PowerLevel,
                existingRemainingTicks);
            if (!stackAccepted)
            {
                result.Reason = "modifier-stack-rejected";
                Debug.LogError($"[POISON-SHOT-MOD] reject target={target.Name}#{target.EntityId} source={sourceEntityId} modifier={spell.ProjectileModifierId} incomingPower={powerLevel} existingPower={existingModifier.PowerLevel} incomingDuration={durationTicks} existingRemaining={existingRemainingTicks} stack={spell.ProjectileModifierStackRule ?? "UNKNOWN"} sourceFunction=Modifiers::addModifierLocal@0x00501770");
                return result;
            }
            long modifierOrder = replace ? existingModifier.Order : ++_nextCombatModifierOrder;
            _activeMonsterModifiers[key] = new ActiveMonsterModifier
            {
                Order = modifierOrder,
                TargetEntityId = target.EntityId,
                SourceEntityId = sourceEntityId,
                SourceState = sourceState,
                Spell = spell,
                SkillLevel = skillLevel,
                LastTick = startTick - 1,
                DurationTicksInitial = durationTicks,
                DurationTicksRemaining = durationTicks,
                FrequencyTicks = frequencyTicks,
                FrequencyCountdownTicks = initialFrequencyCountdown,
                TicksApplied = 0,
                ModifierKey = key,
                PowerLevel = powerLevel,
                StackRule = spell.ProjectileModifierStackRule
            };

            result.AppliedModifier = true;
            result.NewHPWire = PeekRuntimeMonsterHPWire(target);
            Debug.LogError($"[POISON-SHOT-MOD] {(replace ? "replace" : "add")} target={target.Name}#{target.EntityId} source={sourceEntityId} modifier={spell.ProjectileModifierId} effect={spell.ProjectileModifierEffectId} durationTicks={durationTicks} frequencyTicks={frequencyTicks} countdown={initialFrequencyCountdown} stack={spell.ProjectileModifierStackRule ?? "UNKNOWN"} firstTick={startTick + initialFrequencyCountdown - 1} clientTick={startTick} hp={result.OldHPWire}->{result.NewHPWire} rngBefore={rng.CallsSinceReseed} source={source ?? "unknown"} sourceFunction=SpellModEffect::doEffect@0x00554460 EffectMod::init@0x0055FE20 EffectMod::update@0x0055FE70");
            return result;
        }

        public MonsterModifierApplyResult AdvanceMonsterModifierRuntimeForTarget(uint targetEntityId, MersenneTwister rng, uint simulationTick, string source)
        {
            return AdvanceMonsterModifierRuntime(rng, simulationTick, source, targetEntityId);
        }

        private MonsterModifierApplyResult AdvanceMonsterModifierRuntime(MersenneTwister rng, uint simulationTick, string source, uint onlyTargetEntityId = 0, long? onlyOrder = null)
        {
            var aggregate = new MonsterModifierApplyResult
            {
                Reason = source ?? "modifier-runtime"
            };
            if (_advancingMonsterModifiers || _activeMonsterModifiers.Count == 0)
                return aggregate;
            int nowTick = (int)simulationTick;
            _advancingMonsterModifiers = true;
            try
            {
                var keys = _activeMonsterModifiers
                    .OrderBy(entry => entry.Value?.Order ?? long.MaxValue)
                    .Select(entry => entry.Key)
                    .ToList();
                foreach (string key in keys)
                {
                    if (!_activeMonsterModifiers.TryGetValue(key, out var mod) || mod == null)
                        continue;
                    if ((onlyOrder.HasValue && mod.Order != onlyOrder.Value) || (onlyTargetEntityId != 0 && mod.TargetEntityId != onlyTargetEntityId))
                        continue;
                    if (!_activeMonsters.TryGetValue(mod.TargetEntityId, out var monster) || monster == null || !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                    {
                        _activeMonsterModifiers.Remove(key);
                        Debug.LogError($"[POISON-SHOT-MOD] remove target={mod.TargetEntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=RemoveOnDeathOrMissing");
                        continue;
                    }
                    if (mod.SourceState == null || mod.Spell == null)
                    {
                        _activeMonsterModifiers.Remove(key);
                        Debug.LogError($"[POISON-SHOT-MOD] remove target={mod.TargetEntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=missing-source-state");
                        continue;
                    }
                    MersenneTwister modRng = GetRoomRngForMonster(monster);
                    if (modRng == null)
                    {
                        RuntimeEvidence.LogFallbackHit(
                            "rng-instance",
                            "modifier-missing-instance-rng",
                            $"target={monster.EntityId} source={source ?? "unknown"}",
                            64);
                        Debug.LogError($"[POISON-SHOT-MOD] skip tick target={monster.Name}#{monster.EntityId} source={source ?? "unknown"} reason=missing-instance-rng");
                        continue;
                    }
                    if (nowTick <= mod.LastTick)
                        continue;

                    int ticksToProcess = Math.Min(1, nowTick - mod.LastTick);
                    string removeAfterTick = null;
                    for (int modifierTickIndex = 0; modifierTickIndex < ticksToProcess && monster.IsAlive && PeekRuntimeMonsterHPWire(monster) > 0; modifierTickIndex++)
                    {
                        mod.LastTick++;
                        if (mod.DurationTicksInitial > 0)
                        {
                            if (mod.DurationTicksRemaining > 0)
                                mod.DurationTicksRemaining--;
                            if (mod.DurationTicksRemaining == 0)
                            {
                                removeAfterTick = "duration-expired";
                                break;
                            }
                        }

                        if (mod.FrequencyCountdownTicks > 0)
                            mod.FrequencyCountdownTicks--;
                        if (mod.FrequencyCountdownTicks != 0)
                            continue;

                        int appliedTickIndex = mod.TicksApplied + 1;
                        int modifierDamageTypeId = DamageResolver.ResolveDamageTypeId(mod.Spell.EffectiveProjectileModifierDamageType);
                        bool spellEffectVulnerable;
                        if (!ResolveSpellDamageResistance(
                                monster,
                                modifierDamageTypeId,
                                mod.SourceState.Level,
                                source ?? "modifier-tick",
                                out spellEffectVulnerable))
                        {
                            Debug.LogError($"[COMBAT-EVENT] actor=player-spell-mod actorId={mod.SourceEntityId} target=monster targetId={monster.EntityId} result=RESISTED damageWire=0 hp={PeekRuntimeMonsterHPWire(monster)}->{PeekRuntimeMonsterHPWire(monster)} spell={mod.Spell.DisplayName} rngAfter={modRng.CallsSinceReseed} marker=SPELL-MOD");
                            mod.TicksApplied++;
                            aggregate.TicksApplied++;
                            mod.FrequencyCountdownTicks = Math.Max(1, (int)mod.FrequencyTicks);
                            continue;
                        }
                        var damage = DamageResolver.ProcessProjectileModifierTick(
                            modRng,
                            mod.SourceState.Level,
                            mod.SourceState.ClientSpellIntellect,
                            mod.SourceState.ClientSpellAgility,
                            mod.SourceState.ClientSpellStrength,
                            mod.SourceState.WeaponDamageF32,
                            mod.SourceState.WeaponDamageVolatilityF32,
                            mod.Spell,
                            monster,
                            mod.SkillLevel,
                            DamageResolver.ResolveCriticalDamagePercent(mod.SourceState),
                            mod.SourceState,
                            spellEffectVulnerable ? -1 : DamageResolver.ResolveSpellCriticalThresholdF32(mod.SourceState, monster, mod.Spell?.ProjectileModifierCriticalChanceF32 ?? 0),
                            spellEffectVulnerable,
                            $"{monster.Name}#{monster.EntityId}");

                        if (damage.Type == AttackResultType.Miss || damage.DamageF32 <= 0)
                        {
                            Debug.LogError($"[POISON-SHOT-TICK] target={monster.Name}#{monster.EntityId} tick={appliedTickIndex} result={damage.Type} damageWire=0 hp={PeekRuntimeMonsterHPWire(monster)} durationRemaining={mod.DurationTicksRemaining} frequencyTicks={mod.FrequencyTicks} clientTick={mod.LastTick} source={source ?? "unknown"} rngAfter={modRng.CallsSinceReseed}");
                        }
                        else
                        {
                            bool applied = ApplyPlayerDamageToMonsterWire(
                                monster,
                                (uint)damage.DamageF32,
                                "SPELL-MOD",
                                out uint oldHPWire,
                                out uint newHPWire,
                                out bool died,
                                clientDamageTick: (uint)Math.Max(0, mod.LastTick),
                                damageTypeId: damage.DamageTypeId,
                                rawDamageWire: (uint)damage.DamageF32,
                                attackerLevel: Math.Max(1, mod.SourceState.Level),
                                damageKind: 3,
                                spellEffectResistResolved: true,
                                spellEffectVulnerable: spellEffectVulnerable,
                                sourceEntityId: mod.SourceEntityId);
                            uint appliedDamageWire = oldHPWire > newHPWire ? oldHPWire - newHPWire : 0u;
                            int damageStunMod = ResolveSpellDamageStunMod(mod.Spell.ProjectileModifierDamageStunMod, mod.SourceState.StunMod);
                            uint effectRaw = applied
                                ? ConsumeSpellDamageReactionRng(modRng, "player-spell-mod", monster, oldHPWire, newHPWire, appliedDamageWire, damageStunMod, mod.SourceState.Level, mod.SourceEntityId, source ?? "modifier-tick")
                                : 0;
                            DispatchPlayerDamageEvents(mod.SourceEntityId, monster, appliedDamageWire, mod.Spell.ProjectileModifierEffectId, source);
                            if (applied)
                                NotifyMonsterDamagedByPlayer(monster, mod.SourceEntityId, "modifier-tick");
                            string resultName = damage.Type.ToString().ToUpperInvariant();
                            Debug.LogError($"[POISON-SHOT-TICK] target={monster.Name}#{monster.EntityId} source={mod.SourceEntityId} tick={appliedTickIndex} result={resultName} damageWire={damage.DamageF32} hp={oldHPWire}->{newHPWire} range=[{damage.MinDamageF32},{damage.MaxDamageF32}] damageRaw=0x{damage.DamageRaw:X8} effectRaw=0x{effectRaw:X8} durationRemaining={mod.DurationTicksRemaining} frequencyTicks={mod.FrequencyTicks} clientTick={mod.LastTick} nowTick={nowTick} applied={applied} died={died} rngAfter={modRng.CallsSinceReseed}");
                            Debug.LogError($"[COMBAT-EVENT] actor=player-spell-mod actorId={mod.SourceEntityId} target=monster targetId={monster.EntityId} result={resultName} damageWire={damage.DamageF32} hp={oldHPWire}->{newHPWire} range=[{damage.MinDamageF32},{damage.MaxDamageF32}] damageRaw=0x{damage.DamageRaw:X8} effectRaw=0x{effectRaw:X8} spell={mod.Spell.DisplayName} rngAfter={modRng.CallsSinceReseed} marker=SPELL-MOD");
                            aggregate.DamageApplied |= applied;
                            aggregate.Died |= died;
                            if (aggregate.TicksApplied == 0)
                                aggregate.OldHPWire = oldHPWire;
                            aggregate.NewHPWire = newHPWire;
                            if (died)
                            {
                                _pendingModifierKills.Enqueue(new PendingModifierKill
                                {
                                    SourceEntityId = mod.SourceEntityId,
                                    TargetEntityId = monster.EntityId,
                                    Source = source ?? "modifier-tick",
                                    DamageTick = (uint)Math.Max(0, mod.LastTick)
                                });
                            }
                        }

                        mod.TicksApplied++;
                        aggregate.TicksApplied++;
                        mod.FrequencyCountdownTicks = Math.Max(1, (int)mod.FrequencyTicks);
                        if (!monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                            break;
                    }

                    if (removeAfterTick != null || !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                    {
                        _activeMonsterModifiers.Remove(key);
                        string reason = !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0 ? "target-dead" : removeAfterTick;
                        Debug.LogError($"[POISON-SHOT-MOD] complete target={monster.Name}#{monster.EntityId} source={mod.SourceEntityId} ticks={mod.TicksApplied} durationTicks={mod.DurationTicksInitial} remaining={mod.DurationTicksRemaining} hp={PeekRuntimeMonsterHPWire(monster)} reason={reason ?? "target-dead"} source={source ?? "unknown"} sourceFunction=Modifier::update@0x004FF1B0 EffectMod::update@0x0055FE70");
                    }
                }
            }
            finally
            {
                _advancingMonsterModifiers = false;
            }

            return aggregate;
        }

        private void SetRuntimeMonsterHPWire(Monster monster, uint hp, bool committedDamage, string source = "SET")
        {
            if (monster == null) return;
            if (hp > monster.MaxHPWire) hp = monster.MaxHPWire;
            uint oldHP = monster.CurrentHPWire;
            monster.CurrentHPWire = hp;
            if (committedDamage && hp > 0 && hp < monster.MaxHPWire)
            {
                _monsterRuntimeDamageCommitted.Add(monster.EntityId);
                ushort cooldown = ResolveDamageRegenCooldownTicks(monster);
                if (cooldown > 0)
                    _monsterHPRegenCooldownTicks[monster.EntityId] = cooldown;
                else
                    _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
                Debug.LogError($"[MON-REGEN-COOLDOWN] entity={monster.EntityId} name={monster.Name} cooldown={cooldown} source={source ?? "unknown"}");
            }
            else if (hp == 0 || (!committedDamage && hp >= monster.MaxHPWire))
            {
                _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
            }
            EntitySynchInfoAuthority.Instance.RegisterMonster(monster);
            if (oldHP != hp)
                Debug.LogError($"[MON-HP-CANON] write entity={monster.EntityId} old={oldHP} new={hp} committed={committedDamage} source={source ?? "unknown"}");
        }

        private ushort ResolveDamageRegenCooldownTicks(Monster monster)
        {
            return CLIENT_DAMAGE_REGEN_COOLDOWN_TICKS;
        }

        public uint GetMonsterCurrentHPWire(Monster monster)
        {
            return GetRuntimeMonsterHPWire(monster, "HP-READ");
        }

        public uint GetMonsterCurrentHPWire(Monster monster, string source)
        {
            return GetRuntimeMonsterHPWire(monster, source);
        }

        public uint PeekMonsterCurrentHPWire(Monster monster)
        {
            return PeekRuntimeMonsterHPWire(monster);
        }

        public string DescribeMonsterHPState(Monster monster)
        {
            if (monster == null) return "monster=<null>";
            bool dirty = _monsterRuntimeDamageCommitted.Contains(monster.EntityId);
            return $"{monster.Name}#{monster.EntityId} runtime={monster.CurrentHPWire}/{monster.MaxHPWire} dirty={dirty} lastPacket={monster.LastOutboundHPWire}";
        }

        public bool TryResolveMonsterEntitySynchInfoHP(Monster monster, string packetName, out uint hpWire)
        {
            return TryResolveMonsterEntitySynchInfoHP(monster, EntitySynchInfoContext.Unknown, packetName, out hpWire, out _);
        }

        public bool TryResolveMonsterEntitySynchInfoHP(Monster monster, EntitySynchInfoContext context, string packetName, out uint hpWire, out string reason)
        {
            return TryResolveMonsterEntitySynchInfoHP(monster, context, packetName, default(EntitySynchInfoVisibilityCutoff), out hpWire, out reason);
        }

        public bool TryResolveMonsterEntitySynchInfoHP(Monster monster, EntitySynchInfoContext context, string packetName, EntitySynchInfoVisibilityCutoff cutoff, out uint hpWire, out string reason)
        {
            hpWire = 0;
            reason = "missing-monster";
            if (monster == null) return false;
            uint serverHPWire = PeekRuntimeMonsterHPWire(monster);
            hpWire = serverHPWire;
            monster.LastOutboundHPWire = hpWire;
            reason = $"simulation-current-hp; tick={_combatTick}; runtimeHP={serverHPWire}";
            return true;
        }

        public void RecordMonsterHPObservation(Monster monster, uint hpWire, string source)
        {
            if (monster == null) return;
            uint runtimeHPWire = PeekRuntimeMonsterHPWire(monster);
            string result = hpWire == runtimeHPWire ? "match" : "mismatch-non-authoritative";
            Debug.LogError($"[MON-HP-DIAGNOSTIC] {result} {monster.Name}#{monster.EntityId} received={hpWire} runtime={runtimeHPWire} source={source ?? "unknown"}");
        }

        public void RecordMonsterOutboundHP(Monster monster, uint hpWire, string source)
        {
            if (monster == null) return;
            if (hpWire > monster.MaxHPWire) hpWire = monster.MaxHPWire;
            monster.LastOutboundHPWire = hpWire;
            EntitySynchInfoAuthority.Instance.RecordMonsterOutboundHP(monster, hpWire, source ?? "outbound");
        }

        public void SetMonsterHPWire(Monster monster, uint hp, bool committedDamage = false, string source = "SET")
        {
            SetRuntimeMonsterHPWire(monster, hp, committedDamage, source);
        }

        public void HealMonsterToFullOnIdle(Monster monster, string source)
        {
            if (monster == null || !monster.IsAlive || monster.MaxHPWire == 0)
                return;
            if (PeekMonsterCurrentHPWire(monster) >= monster.MaxHPWire)
                return;
            SetMonsterHPWire(monster, monster.MaxHPWire, false, source ?? "idle-heal");
            monster.CurrentManaWire = monster.MaxManaWire;
            Debug.LogError($"[MON-IDLE-HEAL] entity={monster.EntityId} name={monster.Name} hp->{monster.MaxHPWire} mana->{monster.MaxManaWire} source={source ?? "idle-heal"} sourceFunction=StockUnit::initState case4@0x5033c0");
        }

        public void NotifyMonsterDamagedByPlayer(Monster monster, uint playerEntityId, string reason)
        {
            NotifyMonsterOnAttackedAdmission(monster, playerEntityId, reason);
        }

        public void NotifyMonsterOnAttackedAdmission(Monster monster, uint playerEntityId, string reason)
        {
            if (monster == null || playerEntityId == 0) return;
            if (!TryGetCombatTarget(playerEntityId, out CombatTarget player) || !player.IsAlive || !IsMonsterEnemyOfTarget(monster, player)) return;
            if (!MatchesInstance(monster, player.InstanceKey)) return;
            if (!monster.IsAlive) return;
            Debug.LogError($"[MON-ONATTACKED-ADMISSION] monster={monster.Name}#{monster.EntityId} player={player.Name}#{player.EntityId} source={reason ?? "unknown"} rangeGate=False smmsg=0x09 sourceFunction=Damage::apply@0x004F6580->MonsterBehavior2::onAttacked@0x0051B550");
            uint directPlayerWeaponHitTick = WeaponUseRuntime.Instance.WasDirectPlayerWeaponHitAppliedAtTick(player.EntityId, monster.EntityId, _combatTick)
                ? _combatTick
                : 0;
            uint playerDamageTick = _combatTick;
            AggroMonster(monster, player, reason, false, true, uint.MaxValue, directPlayerWeaponHitTick, playerDamageTick);
            NotifyMonsterBehaviorThreatInput(monster, player, false);
        }

        public void NotifyDeferredMonsterOnAttackedThreat(Monster monster, uint playerEntityId, string reason)
        {
            if (monster == null || playerEntityId == 0 || !monster.IsAlive)
                return;
            if (!TryGetCombatTarget(playerEntityId, out CombatTarget player) || !player.IsAlive || !IsMonsterEnemyOfTarget(monster, player))
                return;
            if (!MatchesInstance(monster, player.InstanceKey))
                return;
            NotifyMonsterBehaviorThreatInput(monster, player, false);
            Debug.LogError($"[MON-ONATTACKED-THREAT] monster={monster.Name}#{monster.EntityId} player={player.Name}#{player.EntityId} source={reason ?? "unknown"} phase=Damage::apply sourceFunction=MonsterBehavior2::onAttacked@0x0051B550");
        }

        public void MarkMonsterDead(Monster monster, string source)
        {
            MarkMonsterDead(monster, source, true);
        }

        private void MarkMonsterDead(Monster monster, string source, bool mirrorActive)
        {
            if (monster == null) return;

            bool alreadyDead = (monster.UnitFlags & 0x800) != 0;
            if (alreadyDead)
            {
                if (mirrorActive && _activeMonsters.TryGetValue(monster.EntityId, out var existingActive) && existingActive != monster)
                    MarkMonsterDead(existingActive, source, false);
                return;
            }

            bool hadRuntimeState = monster.IsAlive
                || monster.State != MonsterState.Dead
                || monster.TargetId != 0
                || monster.AggroTriggered
                || monster.AggroSent
                || monster.AlertSourceEntityId != 0
                || monster.AttackPending
                || monster.AttackSoundPending
                || monster.AttackClientVisible
                || monster.AttackContactOnly
                || monster.CombatContactTargetId != 0;

            monster.IsAlive = false;
            monster.UnitFlags |= 0x800;
            monster.UnitFlags &= 0xFFFFFBFF;
            monster.State = MonsterState.Dead;
            MeleeBehaviorContext behaviorContext = BuildMeleeBehaviorContextForMonster(monster);
            monster.Behavior?.TerminateAllActionsForDeath(behaviorContext);
            StopMonsterMoving(monster);
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
            monster.AttackStartTick = 0;
            monster.AttackCommitTick = 0;
            monster.AttackSoundTick = 0;
            monster.AttackEndTick = 0;
            monster.AttackUseRaw = 0;
            monster.UsePrimaryActiveSkillThisAttack = false;
            ClearMonsterActiveSkillEffectCycle(monster);
            monster.SearchForAttackMoveActive = false;
            ClearMonsterAttackCommitTarget(monster);
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntilTick = 0;
            ClearMonsterDamageReaction(monster);
            monster.Ai?.PostMessage(MonsterMessageId.DeathWarning, source);
            monster.Ai?.SetState(MonsterStateId.DeathWarning, "death");
            _monsterFarTargetActionLogTick.Remove(monster.EntityId);
            RemoveMonsterModifiersForTarget(monster.EntityId, source ?? "death");
            RemovePlayerModifiersFromSource(monster.EntityId, source ?? "death");
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
            ClearMonsterViewerClientVisible(monster.EntityId);

            if (hadRuntimeState)
                Debug.LogError($"[MON-DEATH-STATE] cleared action/move target for {monster.Name}#{monster.EntityId} source={source ?? "unknown"}");

            if (mirrorActive && _activeMonsters.TryGetValue(monster.EntityId, out var active) && active != monster)
                MarkMonsterDead(active, source, false);
        }

        public void BeginMonsterDeathLifecycle(Monster monster, string source)
        {
            if (monster == null || monster.DeathLifecycleActive) return;
            monster.AttributeModifiers?.RemoveAll(m => m.RemoveOnDeath);
            if (monster.CowardiceRemoveOnDeath)
                ClearMonsterCowardiceModifier(monster, "RemoveOnDeath");
            MarkMonsterDead(monster, source);

            monster.DeathLifecycleActive = true;
            monster.DeathRemoveSent = false;
            monster.StockUnitState = 8;
            monster.OnDeadTicksRemaining = (byte)((monster.EntityFlags & 0x40) != 0 ? 60 : 5);
            monster.OnDeadDispatched = false;
            monster.DeathExperienceDispatched = false;
            monster.StockUnitStateTicksRemaining = 0;
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
            Debug.LogError($"[MON-DEATH-LIFECYCLE] state=8 onDeadTicks={monster.OnDeadTicksRemaining} entityFlags=0x{monster.EntityFlags:X2} unitFlags=0x{monster.UnitFlags:X8} monster={monster.Name}#{monster.EntityId} source={source ?? "unknown"}");
        }

        private static void InitializeMonsterStockUnitLifespan(Monster monster)
        {
            if (monster.StockUnitLifespanF32 == 0)
                return;
            int lifespanF32 = unchecked(monster.StockUnitLifespanF32 + monster.Level * monster.StockUnitLifespanIncrementF32);
            int ticksF32 = unchecked((int)(((long)lifespanF32 * 0x1E00) >> 8));
            monster.StockUnitLifespanTicksRemaining = unchecked((ushort)(ticksF32 >> 8));
        }

        public void AdvanceMonsterStockUnitLifespanForEntity(uint entityId)
        {
            if (!_activeMonsters.TryGetValue(entityId, out Monster monster)
                || monster.StockUnitState != 8 || monster.StockUnitLifespanTicksRemaining == 0)
                return;
            monster.StockUnitLifespanTicksRemaining--;
            if (monster.StockUnitLifespanTicksRemaining != 0)
                return;
            if (monster.Behavior == null)
            {
                DespawnMonster(entityId, true, true);
                return;
            }
            if (!monster.Behavior.BeginStockUnitLifespanKill(BuildMeleeBehaviorContextForMonster(monster)))
                return;
            SetRuntimeMonsterHPWire(monster, 0, false, "StockUnit::updateState:Kill");
            BeginMonsterDeathLifecycle(monster, "StockUnit::updateState:Kill");
            monster.Behavior.CompleteStockUnitLifespanKill(BuildMeleeBehaviorContextForMonster(monster));
        }

        public bool AdvanceMonsterDeathLifecycleForEntity(uint entityId)
        {
            if (!_activeMonsters.TryGetValue(entityId, out var monster)
                || !monster.DeathLifecycleActive
                || monster.DeathRemoveSent)
                return false;

            if (!monster.OnDeadDispatched)
            {
                if (monster.OnDeadTicksRemaining > 0)
                    monster.OnDeadTicksRemaining--;
                if (monster.OnDeadTicksRemaining == 0)
                {
                    monster.OnDeadDispatched = true;
                    Debug.LogError($"[MON-ONDEAD] dispatch monster={monster.Name}#{monster.EntityId} entityFlags=0x{monster.EntityFlags:X2} unitFlags=0x{monster.UnitFlags:X8} sourceFunction=Unit::update@0x005093E0->StockUnit::onDead@0x005038F0");
                    EncounterOnUnitDied(monster, "MonsterBehavior2::onDied");
                    OnStockUnitDead?.Invoke(monster);
                    monster.StockUnitState = 7;
                    monster.StockUnitStateTicksRemaining = monster.CorpseLingerTicks;
                    Debug.LogError($"[MON-DEATH-LIFECYCLE] state=7 corpse monster={monster.Name}#{monster.EntityId} ticks={monster.CorpseTicksRemaining} sourceFunction=StockUnit::onDead@0x005038F0->StockUnit::initState@0x005032A0");
                    DispatchMonsterDeathProcs(monster, "Unit::update:onDead");
                }
            }

            if (monster.StockUnitState == 8)
                return true;

            if (monster.StockUnitState == 7)
            {
                if (monster.StockUnitStateTicksRemaining > 0)
                    monster.StockUnitStateTicksRemaining--;
                if (monster.StockUnitStateTicksRemaining == 0)
                {
                    monster.StockUnitState = 9;
                    monster.UnitFlags |= 0x1000;
                    monster.StockUnitStateTicksRemaining = CLIENT_STOCKUNIT_FADE_TICKS;
                    Debug.LogError($"[MON-DEATH-LIFECYCLE] state=9 fade {monster.Name}#{monster.EntityId} ticks={monster.FadeTicksRemaining}");
                }
                return true;
            }

            if (monster.StockUnitState == 9)
            {
                if (monster.StockUnitStateTicksRemaining > 0)
                    monster.StockUnitStateTicksRemaining--;
                if (monster.StockUnitStateTicksRemaining != 0)
                    return true;

                monster.EntityFlags = (byte)(monster.EntityFlags & 0xBF);
                if (!monster.AutoRespawn)
                {
                    monster.DeathRemoveSent = true;
                    Debug.LogError($"[MON-DEATH-LIFECYCLE] remove {monster.Name}#{monster.EntityId} after corpse/fade autoRespawn=False");
                    DespawnMonster(entityId, false);
                    return true;
                }

                monster.StockUnitState = 6;
                monster.StockUnitStateTicksRemaining = monster.RespawnRateTicks;
                Debug.LogError($"[MON-DEATH-LIFECYCLE] state=6 respawn-wait {monster.Name}#{monster.EntityId} ticks={monster.RespawnTicksRemaining}");
                return true;
            }

            if (monster.StockUnitState == 6)
            {
                if (monster.StockUnitStateTicksRemaining > 0)
                    monster.StockUnitStateTicksRemaining--;
                if (monster.StockUnitStateTicksRemaining != 0)
                    return true;
                monster.StockUnitState = 3;
            }

            if (monster.StockUnitState == 3)
            {
                if (monster.RespawnWhenClear && HasEnemyWithinMonsterPerception(monster))
                {
                    monster.StockUnitState = 6;
                    monster.StockUnitStateTicksRemaining = (ushort)(monster.RespawnRateTicks >> 2);
                    Debug.LogError($"[MON-DEATH-LIFECYCLE] state=6 respawn-clear-wait {monster.Name}#{monster.EntityId} ticks={monster.RespawnTicksRemaining}");
                    return true;
                }

                BeginStockUnitSpawn(monster);
                return false;
            }

            return true;
        }

    }
}
