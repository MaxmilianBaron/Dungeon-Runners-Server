using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;

namespace DungeonRunners.Combat
{
    public partial class CombatRuntime
    {
        public sealed class PvpDamageResolution
        {
            public uint RawDamageWire;
            public uint RemappedDamageWire;
            public uint AdjustedDamageWire;
            public uint AppliedDamageWire;
            public uint OldHPWire;
            public uint NewHPWire;
            public uint ResistRaw;
            public int ResistChanceWire;
            public string ResultName;
            public bool Damaged;
            public bool Died;
        }

        public PvpDamageResolution ApplyPvpDamageQueryWire(
            CombatPlayer attacker,
            CombatPlayer target,
            uint rawDamageWire,
            int damageModPercent,
            int damageTypeId,
            byte damageKind,
            MersenneTwister rng,
            string source,
            bool invokeAttackerDamageCallback,
            bool physicalWeaponHit)
        {
            var result = new PvpDamageResolution
            {
                RawDamageWire = rawDamageWire,
                ResultName = "NONE",
                OldHPWire = target?.PlayerState?.CurrentHPWire ?? 0,
                NewHPWire = target?.PlayerState?.CurrentHPWire ?? 0
            };
            if (attacker?.PlayerState == null || target?.PlayerState == null || rng == null || rawDamageWire == 0)
                return result;

            int attackerLevel = Math.Max(1, attacker.PlayerState.Level);
            result.RemappedDamageWire = PvpBalance.RemapDamageWire(rawDamageWire, damageModPercent, damageKind, attackerLevel);
            DamageQueryResult query = ApplyPlayerDamageQueryWire(
                result.RemappedDamageWire,
                target,
                null,
                damageTypeId,
                damageKind,
                source ?? "PVP-DAMAGE",
                attackerLevel,
                rng,
                true);
            result.AdjustedDamageWire = query.AdjustedDamageWire;
            result.ResistRaw = query.ResistRaw;
            result.ResistChanceWire = query.ResistChanceWire;
            result.ResultName = query.ResultName;

            if (query.AdjustedDamageWire > 0)
            {
                target.PlayerState.TakeQueriedDamage(query.AdjustedDamageWire);
                result.NewHPWire = target.PlayerState.CurrentHPWire;
                target.IsAlive = result.NewHPWire > 0;
                result.AppliedDamageWire = result.OldHPWire > result.NewHPWire
                    ? result.OldHPWire - result.NewHPWire
                    : 0;
                result.Damaged = result.AppliedDamageWire > 0;
                result.Died = result.OldHPWire > 0 && result.NewHPWire == 0;
                if (invokeAttackerDamageCallback && result.AppliedDamageWire > 0)
                    attacker.PlayerState.ApplyOnDamageCallback(result.AppliedDamageWire, source ?? "PVP-DAMAGE");
                ConsumeOnApplyDamageEffectRng(
                    rng,
                    "player-pvp",
                    target.EntityId,
                    target.Name,
                    result.OldHPWire,
                    result.NewHPWire,
                    target.PlayerState.MaxHPWire,
                    result.AppliedDamageWire,
                    source ?? "PVP-DAMAGE",
                    physicalWeaponHit);
            }

            target.PlayerState.DoAttributeModifierDamageEvent(rng, target.EntityId, target.Name, source ?? "PVP-DAMAGE");
            if (result.Died)
            {
                TryCommitPlayerAttributeModifierDeathEvent(target, source ?? "PVP-DAMAGE");
                RemovePlayerDamageModifiersForTarget(target.EntityId, source ?? "PVP-DAMAGE");
            }

            Debug.LogError($"[PVP-DAMAGE] attacker={attacker.Name}#{attacker.EntityId} target={target.Name}#{target.EntityId} kind={damageKind} type={damageTypeId} rawWire={rawDamageWire} remappedWire={result.RemappedDamageWire} adjustedWire={result.AdjustedDamageWire} appliedWire={result.AppliedDamageWire} hp={result.OldHPWire}->{result.NewHPWire}/{target.PlayerState.MaxHPWire} result={result.ResultName} resistRaw=0x{result.ResistRaw:X8} resistChance={result.ResistChanceWire} rngPos={rng.CallsSinceReseed} source={source ?? "PVP-DAMAGE"}");
            return result;
        }

        public bool ApplyPvpAuthoredModifier(
            CombatPlayer sourcePlayer,
            CombatPlayer target,
            SpellData spell,
            SpellModifierData modifier,
            int skillLevel,
            string source)
        {
            if (sourcePlayer == null || target?.PlayerState == null || spell == null || modifier == null)
                return false;
            if (!string.Equals(modifier.ModifierFamily, "AttributeModifier", StringComparison.Ordinal)
                || modifier.Attributes == null
                || modifier.Attributes.Count == 0)
                return false;
            string modifierType = !string.IsNullOrWhiteSpace(modifier.ModifierId)
                ? modifier.ModifierId
                : modifier.EffectId;
            if (string.IsNullOrWhiteSpace(modifierType))
                return false;
            ushort durationTicks = modifier.ResolveDurationTicks(skillLevel);
            uint powerLevel = unchecked((uint)spell.ResolvePowerLevelF32(skillLevel));
            byte sourceIsSelf = sourcePlayer.EntityId == target.EntityId ? (byte)1 : (byte)0;
            string modifierKey = BuildPlayerRuntimeModifierKey(
                target.EntityId,
                sourcePlayer.EntityId,
                modifierType,
                modifier.StackRule,
                sourceIsSelf);
            bool rejected = !target.PlayerState.ShouldAcceptAttributeModifier(
                modifierKey,
                modifierType,
                modifier.StackRule,
                powerLevel,
                durationTicks);
            if (rejected)
                return false;
            var attributes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (modifier.Attributes != null)
            {
                for (int attributeIndex = 0; attributeIndex < modifier.Attributes.Count; attributeIndex++)
                {
                    SpellAttributeModifierData attribute = modifier.Attributes[attributeIndex];
                    if (attribute == null || string.IsNullOrWhiteSpace(attribute.Attribute))
                        return false;
                    int value = attribute.ResolveLevelValue(skillLevel);
                    attributes[attribute.Attribute] = attributes.TryGetValue(attribute.Attribute, out int existing)
                        ? checked(existing + value)
                        : value;
                }
            }
            if (attributes.Count == 0)
                return false;
            if (!TryAllocatePlayerModifierNetworkId(modifierKey, true, out uint modifierId))
                return false;
            bool applied = target.PlayerState.ApplyAuthoredAttributeModifier(
                modifierType,
                attributes,
                durationTicks,
                modifier.RemoveOnDeath,
                source ?? "PVP-SPELL-MODIFIER",
                modifierKey,
                sourcePlayer.EntityId,
                spell.SkillId,
                modifier.EffectId ?? spell.EffectId,
                powerLevel,
                modifier.StackRule,
                modifier.TerminateWhenHitChance,
                (byte)Math.Clamp(skillLevel, 0, byte.MaxValue),
                sourceIsSelf);
            if (!applied)
            {
                ReleasePlayerModifierNetworkId(modifierKey, modifierId);
                return false;
            }
            RaisePlayerModifierAddWithId(
                null,
                target,
                modifierKey,
                modifierType,
                modifierId,
                (byte)Math.Clamp(skillLevel, 0, byte.MaxValue),
                powerLevel,
                durationTicks,
                sourceIsSelf,
                false,
                spell.SkillId,
                modifier.EffectId ?? spell.EffectId,
                source ?? "PVP-SPELL-MODIFIER",
                "ActiveSkill::doSkillEffect@0x00539630->SpellModEffect::doEffect@0x00554460");
            return true;
        }
    }
}
