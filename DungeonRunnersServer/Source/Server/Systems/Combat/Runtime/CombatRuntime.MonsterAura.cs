using System;
using System.Collections.Generic;
using DungeonRunners.Data;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        private sealed class MonsterAuraPlan
        {
            public string AuraModifierPath;
            public string ChildModifierPath;
            public string ChildEffectPath;
            public string TargetType;
            public string StackRule;
            public int PowerLevelF32;
            public int DurationTicks;
            public int RangeF32;
            public int MaxTargets;
            public ushort ScanIntervalTicks;
            public bool RemoveOnDeath;
            public MonsterDamageModifierSkillEffect DamageEffect;
        }

        private sealed class ActiveMonsterAura
        {
            public long Order;
            public uint SourceEntityId;
            public string SkillPath;
            public string EffectPath;
            public MonsterAuraPlan Plan;
            public int RemainingTicks;
            public bool Permanent;
            public ushort ScanCountdownTicks;
        }

        private readonly Dictionary<string, ActiveMonsterAura> _activeMonsterAuras = new Dictionary<string, ActiveMonsterAura>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _activeMonsterAuraKeys = new List<string>();
        private long _nextMonsterAuraOrder;

        private sealed class ActivePlayerAnchoredMonsterAura
        {
            public long Order;
            public uint SourceEntityId;
            public uint AnchorEntityId;
            public string SkillPath;
            public string EffectPath;
            public string ModifierKey;
            public MonsterAuraPlan Plan;
            public int RemainingTicks;
            public bool Permanent;
            public ushort ScanCountdownTicks;
        }

        private readonly Dictionary<string, ActivePlayerAnchoredMonsterAura> _activePlayerAnchoredMonsterAuras = new Dictionary<string, ActivePlayerAnchoredMonsterAura>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _activePlayerAnchoredMonsterAuraKeys = new List<string>();
        private long _nextPlayerAnchoredMonsterAuraOrder;

        private bool TryBuildMonsterAuraEffectNode(
            GCNode modEffect,
            string effectPath,
            GCNode skillContext,
            int skillLevel,
            int powerLevelF32,
            string auraModifierPath,
            GCNode auraModifier,
            GCNode unresolvedAuraDesc,
            int durationTicks,
            int chanceWire,
            out MonsterSelfEffectNode plan,
            out string reason)
        {
            plan = null;
            if (!TryBuildMonsterAuraPlan(
                skillContext,
                skillLevel,
                powerLevelF32,
                auraModifierPath,
                auraModifier,
                unresolvedAuraDesc,
                durationTicks,
                out MonsterAuraPlan aura,
                out reason))
                return false;
            if (!string.Equals(aura.TargetType, "ENEMY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(aura.TargetType, "2", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"unsupported-aura-target:{aura.TargetType}";
                return false;
            }
            plan = new MonsterSelfEffectNode
            {
                Kind = MonsterSelfEffectNodeKind.EnemyDamageAura,
                EffectPath = effectPath,
                ModifierPath = auraModifierPath,
                ChanceWire = chanceWire,
                PowerLevelF32 = powerLevelF32,
                DurationTicks = durationTicks,
                Aura = aura
            };
            return true;
        }

        private bool TryBuildMonsterAuraPlan(
            GCNode skillContext,
            int skillLevel,
            int powerLevelF32,
            string auraModifierPath,
            GCNode auraModifier,
            GCNode unresolvedAuraDesc,
            int durationTicks,
            out MonsterAuraPlan aura,
            out string reason)
        {
            aura = null;
            reason = null;
            GCNode auraDesc = FindEffectChildByExtends(auraModifier, "AuraModDesc") ?? unresolvedAuraDesc;
            if (auraDesc == null)
            {
                reason = "missing-aura-description";
                return false;
            }
            string targetType = auraDesc.GetString("TargetType", "SELF");
            int rangeF32 = auraDesc.GetFixed32("Range", 100 * 0x100);
            int auraFrequencyF32 = auraDesc.GetFixed32("Frequency", 0x100);
            long auraFrequencyTicks = ((long)auraFrequencyF32 * CLIENT_TICKS_PER_SECOND) >> 8;
            long scanIntervalTicks = auraFrequencyTicks / 2;
            if (rangeF32 <= 0 || scanIntervalTicks <= 0 || scanIntervalTicks > byte.MaxValue)
            {
                reason = "invalid-aura-range-or-frequency";
                return false;
            }
            string childModifierPath = auraDesc.GetString("Modifier", null);
            GCNode childModifier = ResolveAuthoredNodeReference(childModifierPath, auraModifier ?? skillContext);
            if (childModifier == null || !AuthoredExtends(childModifier, "EffectMod"))
            {
                reason = $"unsupported-aura-child-modifier:{childModifier?.Extends ?? "missing"}";
                return false;
            }
            GCNode childDesc = FindEffectChildByExtends(childModifier, "EffectModDesc") ?? childModifier.GetChild("Description") ?? childModifier;
            int childFrequencyF32 = childDesc.GetFixed32("Frequency", 0x100);
            ushort childFrequencyTicks = ComputeEffectModFrequencyTicks(childFrequencyF32);
            if (childFrequencyTicks == 0)
            {
                reason = "invalid-aura-child-frequency";
                return false;
            }
            int lingerF32 = auraDesc.GetFixed32("LingerTime", 0);
            int childDurationF32 = lingerF32 > 0 ? lingerF32 : auraFrequencyF32;
            long childDurationTicksLong = ((long)childDurationF32 * CLIENT_TICKS_PER_SECOND) >> 8;
            if (childDurationTicksLong <= 0 || childDurationTicksLong > ushort.MaxValue)
            {
                reason = "invalid-aura-child-duration";
                return false;
            }
            string childEffectPath = childDesc.GetString("Effect", null);
            GCNode childEffect = ResolveAuthoredNodeReference(childEffectPath, childModifier ?? skillContext);
            GCNode damageEffect = FindEffectChildByExtends(childEffect, "SpellDamageEffect");
            if (childEffect == null || damageEffect == null)
            {
                reason = "unsupported-aura-child-effect";
                return false;
            }
            string attackType = damageEffect.GetString("AttackType", "MAGIC");
            string damageType = damageEffect.GetString("DamageType", "SHADOW");
            if (!DamageResolver.TryResolveDamageTypeId(damageType, out int damageTypeId))
            {
                reason = $"unsupported-aura-damage-type:{damageType}";
                return false;
            }
            int damageModF32 = damageEffect.GetFixed32Ceiling("DamageMod", 0);
            if (damageModF32 <= 0)
            {
                reason = "non-positive-aura-damage";
                return false;
            }
            var damage = new MonsterDamageModifierSkillEffect
            {
                EffectPath = childEffectPath,
                ModifierPath = childModifierPath,
                ModifierEffectPath = BuildAuthoredEffectPath(childEffectPath, damageEffect),
                AttackType = attackType,
                DamageType = damageType,
                DamageTypeId = damageTypeId,
                DamageKind = string.Equals(attackType, "MAGIC", StringComparison.OrdinalIgnoreCase) ? (byte)3 : (byte)0,
                DamageModF32 = damageModF32,
                DamageVolatilityF32 = damageEffect.GetFixed32Ceiling("DamageVolatility", 0),
                ChanceWire = ResolveSpellEffectChanceWire(damageEffect),
                CriticalChanceF32 = damageEffect.GetFixed32("CriticalChance", 0),
                DamageStunMod = damageEffect.GetInt("StunMod", 50),
                DamageStunModFactorF32 = damageEffect.GetFixed32("StunModFactor", 0x100),
                DamageStunModIncF32 = damageEffect.GetFixed32("StunModInc", 0),
                DurationF32 = childDurationF32,
                DurationTicks = (ushort)childDurationTicksLong,
                FrequencyF32 = childFrequencyF32,
                FrequencyTicks = childFrequencyTicks,
                RemoveOnDeath = childDesc.GetBool("RemoveOnDeath", false),
                StackRule = "UNIQUEBYTYPE"
            };
            aura = new MonsterAuraPlan
            {
                AuraModifierPath = auraModifierPath,
                ChildModifierPath = childModifierPath,
                ChildEffectPath = childEffectPath,
                TargetType = targetType,
                StackRule = auraDesc.GetString("StackRule", "UNIQUEBYTYPE"),
                PowerLevelF32 = powerLevelF32,
                DurationTicks = durationTicks,
                RangeF32 = rangeF32,
                MaxTargets = ResolveMonsterAuraMaxTargets(auraDesc, skillLevel),
                ScanIntervalTicks = (ushort)scanIntervalTicks,
                RemoveOnDeath = auraDesc.GetBool("RemoveOnDeath", false),
                DamageEffect = damage
            };
            return true;
        }

        private static int ResolveMonsterAuraMaxTargets(GCNode auraDesc, int skillLevel)
        {
            long minimumF32 = auraDesc.GetFixed32("NumTargetsMin", 100 * 0x100);
            long maximumF32 = auraDesc.GetFixed32("NumTargetsMax", 100 * 0x100);
            long incrementF32 = auraDesc.GetFixed32("NumTargetsInc", 0);
            long resolvedF32 = minimumF32 + Math.Max(1, skillLevel) * incrementF32;
            if (maximumF32 > 0 && resolvedF32 > maximumF32)
                resolvedF32 = maximumF32;
            long count = resolvedF32 >> 8;
            if (count <= 0)
                return 1;
            return count >= int.MaxValue ? int.MaxValue : (int)count;
        }

        private bool ApplyMonsterAuraModifier(Monster sourceMonster, MonsterAuraPlan auraPlan)
        {
            if (sourceMonster == null || !sourceMonster.IsAlive || auraPlan?.DamageEffect == null)
                return false;
            string skillPath = sourceMonster.SelectedActiveSkill?.Path;
            if (string.IsNullOrWhiteSpace(skillPath))
                return false;
            auraPlan.DamageEffect.SkillPath = skillPath;
            string key = $"{sourceMonster.EntityId}:{auraPlan.AuraModifierPath ?? "AuraMod"}";
            bool replace = _activeMonsterAuras.TryGetValue(key, out ActiveMonsterAura existing);
            long order = replace ? existing.Order : ++_nextMonsterAuraOrder;
            if (!replace)
                _activeMonsterAuraKeys.Add(key);
            _activeMonsterAuras[key] = new ActiveMonsterAura
            {
                Order = order,
                SourceEntityId = sourceMonster.EntityId,
                SkillPath = skillPath,
                EffectPath = sourceMonster.SelectedActiveSkill.Effect,
                Plan = auraPlan,
                RemainingTicks = auraPlan.DurationTicks,
                Permanent = auraPlan.DurationTicks == 0,
                ScanCountdownTicks = 0
            };
            Debug.LogError($"[MON-AURA] {(replace ? "replace" : "add")} source={sourceMonster.Name}#{sourceMonster.EntityId} skill={skillPath} aura={auraPlan.AuraModifierPath} child={auraPlan.ChildModifierPath} durationTicks={auraPlan.DurationTicks} scanTicks={auraPlan.ScanIntervalTicks} childDurationTicks={auraPlan.DamageEffect.DurationTicks} childFrequencyTicks={auraPlan.DamageEffect.FrequencyTicks} rangeF32={auraPlan.RangeF32} maxTargets={auraPlan.MaxTargets} power={auraPlan.PowerLevelF32} sourceFunction=SpellModEffect::doEffect@0x00554460->AuraMod::update@0x0055EA40");
            return true;
        }

        private bool ApplyPlayerAnchoredMonsterAura(
            Monster sourceMonster,
            CombatTarget anchor,
            MonsterAuraPlan auraPlan,
            string marker,
            string source)
        {
            if (sourceMonster == null
                || anchor == null || !anchor.HasUnitState
                || !anchor.IsAlive
                || anchor.CurrentHPWire == 0
                || auraPlan?.DamageEffect == null)
                return false;
            string skillPath = sourceMonster.SelectedActiveSkill?.Path;
            if (string.IsNullOrWhiteSpace(skillPath))
                return false;
            auraPlan.DamageEffect.SkillPath = skillPath;
            string effectPath = sourceMonster.SelectedActiveSkill?.Effect;
            string modifierKey = BuildPlayerRuntimeModifierKey(
                anchor.EntityId,
                sourceMonster.EntityId,
                auraPlan.AuraModifierPath,
                auraPlan.StackRule,
                CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF);
            bool replace = _activePlayerAnchoredMonsterAuras.TryGetValue(modifierKey, out ActivePlayerAnchoredMonsterAura existing);
            uint incomingPower = unchecked((uint)Math.Max(0, auraPlan.PowerLevelF32));
            uint incomingDuration = unchecked((uint)Math.Max(0, auraPlan.DurationTicks));
            uint existingDuration = replace && existing != null && !existing.Permanent
                ? unchecked((uint)Math.Max(0, existing.RemainingTicks))
                : 0u;
            bool stackAccepted = !replace
                || ShouldAcceptModifierStack(
                    auraPlan.StackRule,
                    incomingPower,
                    incomingDuration,
                    unchecked((uint)Math.Max(0, existing.Plan?.PowerLevelF32 ?? 0)),
                    existingDuration);
            if (!RaisePlayerModifierAdd(
                sourceMonster,
                anchor,
                modifierKey,
                auraPlan.AuraModifierPath,
                1,
                incomingPower,
                incomingDuration,
                CLIENT_RUNTIME_MODIFIER_SOURCE_IS_SELF,
                replace,
                skillPath,
                effectPath,
                source ?? marker ?? "SpellModEffect",
                "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280",
                stackAccepted))
                return false;
            if (!stackAccepted)
            {
                Debug.LogError($"[MON-PLAYER-AURA] reject source={sourceMonster.Name}#{sourceMonster.EntityId} anchor={anchor.Name}#{anchor.EntityId} skill={skillPath} aura={auraPlan.AuraModifierPath} incomingPower={incomingPower} existingPower={existing?.Plan?.PowerLevelF32 ?? 0} incomingDuration={incomingDuration} existingRemaining={existingDuration} stack={auraPlan.StackRule ?? "UNKNOWN"} sourceFunction=Modifiers::addModifierLocal@0x00501770");
                return true;
            }
            long order = replace ? existing.Order : ++_nextCombatModifierOrder;
            if (!replace)
                _activePlayerAnchoredMonsterAuraKeys.Add(modifierKey);
            _activePlayerAnchoredMonsterAuras[modifierKey] = new ActivePlayerAnchoredMonsterAura
            {
                Order = order,
                SourceEntityId = sourceMonster.EntityId,
                AnchorEntityId = anchor.EntityId,
                SkillPath = skillPath,
                EffectPath = effectPath,
                ModifierKey = modifierKey,
                Plan = auraPlan,
                RemainingTicks = auraPlan.DurationTicks,
                Permanent = auraPlan.DurationTicks == 0,
                ScanCountdownTicks = 0
            };
            Debug.LogError($"[MON-PLAYER-AURA] {(replace ? "replace" : "add")} source={sourceMonster.Name}#{sourceMonster.EntityId} anchor={anchor.Name}#{anchor.EntityId} skill={skillPath} aura={auraPlan.AuraModifierPath} targetType={auraPlan.TargetType} child={auraPlan.ChildModifierPath} durationTicks={auraPlan.DurationTicks} scanTicks={auraPlan.ScanIntervalTicks} childDurationTicks={auraPlan.DamageEffect.DurationTicks} childFrequencyTicks={auraPlan.DamageEffect.FrequencyTicks} rangeF32={auraPlan.RangeF32} power={auraPlan.PowerLevelF32} stack={auraPlan.StackRule} sourceFunction=SpellModEffect::doEffect@0x00554460->AuraMod::update@0x0055EA40");
            return true;
        }

        public void AdvancePlayerAnchoredMonsterAurasForEntity(uint anchorEntityId, long? onlyOrder = null)
        {
            if (anchorEntityId == 0 || _activePlayerAnchoredMonsterAuras.Count == 0)
                return;
            var entries = new List<KeyValuePair<string, ActivePlayerAnchoredMonsterAura>>();
            for (int index = 0; index < _activePlayerAnchoredMonsterAuraKeys.Count; index++)
            {
                string key = _activePlayerAnchoredMonsterAuraKeys[index];
                if (_activePlayerAnchoredMonsterAuras.TryGetValue(key, out ActivePlayerAnchoredMonsterAura candidate)
                    && candidate != null
                    && candidate.AnchorEntityId == anchorEntityId && (!onlyOrder.HasValue || candidate.Order == onlyOrder.Value))
                    entries.Add(new KeyValuePair<string, ActivePlayerAnchoredMonsterAura>(key, candidate));
            }
            entries.Sort((left, right) => left.Value.Order.CompareTo(right.Value.Order));
            foreach (KeyValuePair<string, ActivePlayerAnchoredMonsterAura> entry in entries)
            {
                if (!_activePlayerAnchoredMonsterAuras.TryGetValue(entry.Key, out ActivePlayerAnchoredMonsterAura aura)
                    || aura?.Plan == null)
                    continue;
                TryGetCombatTarget(aura.AnchorEntityId, out CombatTarget anchor);
                Monster sourceMonster = GetMonster(aura.SourceEntityId);
                if (anchor == null || sourceMonster == null)
                {
                    RemovePlayerAnchoredMonsterAura(entry.Key, aura, anchor, sourceMonster, "owner-missing", "Modifier::deinit@0x004FF090");
                    continue;
                }
                if (!MatchesInstance(sourceMonster, anchor.InstanceKey))
                {
                    RemovePlayerAnchoredMonsterAura(entry.Key, aura, anchor, sourceMonster, "instance-mismatch", "Modifier::deinit@0x004FF090");
                    continue;
                }
                if ((!anchor.IsAlive || !anchor.HasUnitState || anchor.CurrentHPWire == 0)
                    && aura.Plan.RemoveOnDeath)
                {
                    RemovePlayerAnchoredMonsterAura(entry.Key, aura, anchor, sourceMonster, "anchor-death", "ModifierDesc.RemoveOnDeath");
                    continue;
                }
                if (!aura.Permanent)
                {
                    if (aura.RemainingTicks > 0)
                        aura.RemainingTicks--;
                    if (aura.RemainingTicks == 0)
                    {
                        RemovePlayerAnchoredMonsterAura(entry.Key, aura, anchor, sourceMonster, "duration-expired", "Modifier::update@0x004FF1B0");
                        continue;
                    }
                }
                if (aura.ScanCountdownTicks > 0)
                    aura.ScanCountdownTicks--;
                if (aura.ScanCountdownTicks != 0)
                    continue;
                List<CombatTarget> targets = CollectPlayerAnchoredMonsterAuraFriendTargets(anchor, aura.Plan.RangeF32);
                Debug.LogError($"[MON-PLAYER-AURA-SCAN] source={sourceMonster.Name}#{sourceMonster.EntityId} anchor={anchor.Name}#{anchor.EntityId} skill={aura.SkillPath} aura={aura.Plan.AuraModifierPath} targetType={aura.Plan.TargetType} rangeF32={aura.Plan.RangeF32} targets={string.Join(",", targets.ConvertAll(target => target.EntityId.ToString()))} sourceFunction=AuraMod::update@0x0055EA40->AuraMod::applyModifier@0x0055EAC0->UnitFinder2::findFriends@0x00510E50");
                foreach (CombatTarget target in targets)
                    ApplyPlayerDamageModifierFromMonster(sourceMonster, target, aura.Plan.DamageEffect, "AuraMod::addModifier", true);
                aura.ScanCountdownTicks = aura.Plan.ScanIntervalTicks;
            }
        }

        private List<CombatTarget> CollectPlayerAnchoredMonsterAuraFriendTargets(CombatTarget anchor, int rangeF32)
        {
            var result = new List<CombatTarget>();
            if (anchor == null || !anchor.HasUnitState || rangeF32 <= 0)
                return result;
            if (anchor.IsAlive && anchor.CurrentHPWire > 0)
                result.Add(anchor);
            string anchorInstance = RoomRuntime.NormalizeInstanceKey(anchor.InstanceKey);
            long rangeSquaredF32 = ((long)rangeF32 * rangeF32) >> 8;
            var ranked = new List<(CombatTarget Player, long DistanceSquaredF32)>();
            foreach (CombatTarget candidate in GetCombatTargetsInEntityOrder())
            {
                if (candidate == null
                    || candidate.EntityId == anchor.EntityId
                    || !candidate.IsAlive
                    || !candidate.HasUnitState
                    || candidate.CurrentHPWire == 0
                    || IsCombatTargetEnemy(anchor, candidate)
                    || !string.Equals(anchorInstance, RoomRuntime.NormalizeInstanceKey(candidate.InstanceKey), StringComparison.OrdinalIgnoreCase))
                    continue;
                long dx = (long)candidate.UnitFinderMembershipPosFixedX - anchor.UnitFinderMembershipPosFixedX;
                long dy = (long)candidate.UnitFinderMembershipPosFixedY - anchor.UnitFinderMembershipPosFixedY;
                long dz = (long)candidate.ClientSimulationPosFixedZ - anchor.ClientSimulationPosFixedZ;
                long distanceSquaredF32 = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                if (distanceSquaredF32 <= rangeSquaredF32)
                    ranked.Add((candidate, distanceSquaredF32));
            }
            ranked.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceSquaredF32.CompareTo(right.DistanceSquaredF32);
                return distanceOrder != 0 ? distanceOrder : left.Player.EntityId.CompareTo(right.Player.EntityId);
            });
            foreach ((CombatTarget Player, long DistanceSquaredF32) candidate in ranked)
                result.Add(candidate.Player);
            return result;
        }

        private void RemovePlayerAnchoredMonsterAura(
            string key,
            ActivePlayerAnchoredMonsterAura aura,
            CombatTarget anchor,
            Monster sourceMonster,
            string reason,
            string sourceFunction)
        {
            if (string.IsNullOrWhiteSpace(key) || aura == null)
                return;
            _activePlayerAnchoredMonsterAuras.Remove(key);
            _activePlayerAnchoredMonsterAuraKeys.Remove(key);
            if (anchor != null)
                RaisePlayerModifierRemove(sourceMonster, anchor, aura.ModifierKey, aura.Plan?.AuraModifierPath, aura.SkillPath, aura.EffectPath, reason, sourceFunction);
            else
                _playerModifierNetworkIds.Remove(aura.ModifierKey);
            Debug.LogError($"[MON-PLAYER-AURA] remove source={aura.SourceEntityId} anchor={aura.AnchorEntityId} skill={aura.SkillPath} aura={aura.Plan?.AuraModifierPath ?? "unknown"} reason={reason ?? "unknown"} sourceFunction={sourceFunction ?? "unknown"}");
        }

        private void AdvanceMonsterAuraModifierTick(Monster sourceMonster)
        {
            if (sourceMonster == null || _activeMonsterAuras.Count == 0)
                return;
            var entries = new List<KeyValuePair<string, ActiveMonsterAura>>();
            for (int index = 0; index < _activeMonsterAuraKeys.Count; index++)
            {
                string key = _activeMonsterAuraKeys[index];
                if (_activeMonsterAuras.TryGetValue(key, out ActiveMonsterAura candidate)
                    && candidate != null
                    && candidate.SourceEntityId == sourceMonster.EntityId)
                    entries.Add(new KeyValuePair<string, ActiveMonsterAura>(key, candidate));
            }
            entries.Sort((left, right) => left.Value.Order.CompareTo(right.Value.Order));
            foreach (KeyValuePair<string, ActiveMonsterAura> entry in entries)
            {
                if (!_activeMonsterAuras.TryGetValue(entry.Key, out ActiveMonsterAura aura) || aura?.Plan == null)
                    continue;
                if (!sourceMonster.IsAlive || sourceMonster.CurrentHPWire == 0)
                {
                    _activeMonsterAuras.Remove(entry.Key);
                    _activeMonsterAuraKeys.Remove(entry.Key);
                    continue;
                }
                if (!aura.Permanent)
                {
                    if (aura.RemainingTicks > 0)
                        aura.RemainingTicks--;
                    if (aura.RemainingTicks == 0)
                    {
                        _activeMonsterAuras.Remove(entry.Key);
                        _activeMonsterAuraKeys.Remove(entry.Key);
                        Debug.LogError($"[MON-AURA] remove source={sourceMonster.Name}#{sourceMonster.EntityId} skill={aura.SkillPath} aura={aura.Plan.AuraModifierPath} reason=duration-expired sourceFunction=Modifier::update@0x004FF1B0->AuraMod::update@0x0055EA40");
                        continue;
                    }
                }
                if (aura.ScanCountdownTicks > 0)
                    aura.ScanCountdownTicks--;
                if (aura.ScanCountdownTicks != 0)
                    continue;
                List<CombatTarget> targets = CollectMonsterAuraEnemyTargets(sourceMonster, aura.Plan.RangeF32, aura.Plan.MaxTargets);
                Debug.LogError($"[MON-AURA-SCAN] source={sourceMonster.Name}#{sourceMonster.EntityId} skill={aura.SkillPath} aura={aura.Plan.AuraModifierPath} rangeF32={aura.Plan.RangeF32} maxTargets={aura.Plan.MaxTargets} targets={string.Join(",", targets.ConvertAll(target => target.EntityId.ToString()))} sourceFunction=AuraMod::update@0x0055EA40->AuraMod::applyModifier@0x0055EAC0->UnitFinder2::findEnemies@0x00510F50");
                foreach (CombatTarget target in targets)
                    ApplyPlayerDamageModifierFromMonster(sourceMonster, target, aura.Plan.DamageEffect, "AuraMod::addModifier", true);
                aura.ScanCountdownTicks = aura.Plan.ScanIntervalTicks;
            }
        }

        private List<CombatTarget> CollectMonsterAuraEnemyTargets(Monster sourceMonster, int rangeF32, int maxTargets)
        {
            var ranked = new List<(CombatTarget Player, long DistanceSquaredF32)>();
            if (rangeF32 <= 0 || maxTargets <= 0)
                return new List<CombatTarget>();
            string sourceInstance = RoomRuntime.NormalizeInstanceKey(sourceMonster.InstanceKey);
            long rangeSquaredF32 = ((long)rangeF32 * rangeF32) >> 8;
            foreach (CombatTarget candidate in GetCombatTargetsInEntityOrder())
            {
                if (candidate == null
                    || !candidate.IsAlive
                    || !candidate.HasUnitState
                    || candidate.CurrentHPWire == 0
                    || !IsMonsterEnemyOfTarget(sourceMonster, candidate)
                    || !string.Equals(sourceInstance, RoomRuntime.NormalizeInstanceKey(candidate.InstanceKey), StringComparison.OrdinalIgnoreCase))
                    continue;
                long dx = (long)candidate.UnitFinderMembershipPosFixedX - sourceMonster.PosFixedX;
                long dy = (long)candidate.UnitFinderMembershipPosFixedY - sourceMonster.PosFixedY;
                long dz = (long)candidate.ClientSimulationPosFixedZ - sourceMonster.PosFixedZ;
                long distanceSquaredF32 = ((dx * dx) >> 8) + ((dy * dy) >> 8) + ((dz * dz) >> 8);
                if (distanceSquaredF32 <= rangeSquaredF32)
                    ranked.Add((candidate, distanceSquaredF32));
            }
            ranked.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceSquaredF32.CompareTo(right.DistanceSquaredF32);
                return distanceOrder != 0 ? distanceOrder : left.Player.EntityId.CompareTo(right.Player.EntityId);
            });
            int count = Math.Min(maxTargets, ranked.Count);
            var result = new List<CombatTarget>(count);
            for (int index = 0; index < count; index++)
                result.Add(ranked[index].Player);
            return result;
        }

        private void RemoveMonsterAurasFromSource(uint sourceEntityId, string source)
        {
            if (sourceEntityId == 0
                || (_activeMonsterAuras.Count == 0 && _activePlayerAnchoredMonsterAuras.Count == 0))
                return;
            var removals = new List<KeyValuePair<string, ActiveMonsterAura>>();
            for (int index = 0; index < _activeMonsterAuraKeys.Count; index++)
            {
                string key = _activeMonsterAuraKeys[index];
                if (_activeMonsterAuras.TryGetValue(key, out ActiveMonsterAura candidate)
                    && candidate != null
                    && candidate.SourceEntityId == sourceEntityId)
                    removals.Add(new KeyValuePair<string, ActiveMonsterAura>(key, candidate));
            }
            removals.Sort((left, right) => left.Value.Order.CompareTo(right.Value.Order));
            foreach (KeyValuePair<string, ActiveMonsterAura> removal in removals)
            {
                _activeMonsterAuras.Remove(removal.Key);
                _activeMonsterAuraKeys.Remove(removal.Key);
            }
            if (removals.Count > 0)
                Debug.LogError($"[MON-AURA] remove-source source={sourceEntityId} count={removals.Count} reason={source ?? "unknown"} sourceFunction=Modifier::deinit@0x004FF090");
            var anchoredRemovals = new List<KeyValuePair<string, ActivePlayerAnchoredMonsterAura>>();
            for (int index = 0; index < _activePlayerAnchoredMonsterAuraKeys.Count; index++)
            {
                string key = _activePlayerAnchoredMonsterAuraKeys[index];
                if (_activePlayerAnchoredMonsterAuras.TryGetValue(key, out ActivePlayerAnchoredMonsterAura candidate)
                    && candidate != null
                    && candidate.SourceEntityId == sourceEntityId)
                    anchoredRemovals.Add(new KeyValuePair<string, ActivePlayerAnchoredMonsterAura>(key, candidate));
            }
            anchoredRemovals.Sort((left, right) => left.Value.Order.CompareTo(right.Value.Order));
            foreach (KeyValuePair<string, ActivePlayerAnchoredMonsterAura> removal in anchoredRemovals)
                RemovePlayerAnchoredMonsterAura(
                    removal.Key,
                    removal.Value,
                    TryGetCombatTarget(removal.Value.AnchorEntityId, out CombatTarget removalAnchor) ? removalAnchor : null,
                    GetMonster(sourceEntityId),
                    source ?? "source-removed",
                    "Modifier::deinit@0x004FF090");
        }

        private void RemovePlayerAnchoredMonsterAurasForAnchor(uint anchorEntityId, string source)
        {
            if (anchorEntityId == 0 || _activePlayerAnchoredMonsterAuras.Count == 0)
                return;
            var removals = new List<KeyValuePair<string, ActivePlayerAnchoredMonsterAura>>();
            for (int index = 0; index < _activePlayerAnchoredMonsterAuraKeys.Count; index++)
            {
                string key = _activePlayerAnchoredMonsterAuraKeys[index];
                if (_activePlayerAnchoredMonsterAuras.TryGetValue(key, out ActivePlayerAnchoredMonsterAura candidate)
                    && candidate != null
                    && candidate.AnchorEntityId == anchorEntityId)
                    removals.Add(new KeyValuePair<string, ActivePlayerAnchoredMonsterAura>(key, candidate));
            }
            removals.Sort((left, right) => left.Value.Order.CompareTo(right.Value.Order));
            TryGetCombatTarget(anchorEntityId, out CombatTarget anchor);
            foreach (KeyValuePair<string, ActivePlayerAnchoredMonsterAura> removal in removals)
                RemovePlayerAnchoredMonsterAura(
                    removal.Key,
                    removal.Value,
                    anchor,
                    GetMonster(removal.Value.SourceEntityId),
                    source ?? "anchor-removed",
                    "Modifier::deinit@0x004FF090");
        }
    }
}
