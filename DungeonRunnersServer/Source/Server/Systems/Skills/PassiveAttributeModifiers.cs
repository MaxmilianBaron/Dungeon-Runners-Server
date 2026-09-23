using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonRunners.Data
{
    public sealed class PassiveAttributeTotals
    {
        private readonly Dictionary<string, int> _attributesF32 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, int> AttributesF32 => _attributesF32;
        public int Strength => ToInt(GetF32("STRENGTH"));
        public int Agility => ToInt(GetF32("AGILITY"));
        public int Endurance => ToInt(GetF32("ENDURANCE"));
        public int Intellect => ToInt(GetF32("INTELLECT"));
        public int HealthPerEnduranceMod => ToInt(GetF32("HEALTH_PER_ENDURANCE_MOD"));
        public int ManaPerIntellectMod => ToInt(GetF32("MANA_PER_INTELLECT_MOD"));
        public int HealthModF32 => GetF32("HEALTH_MOD");
        public int MeleeAttackRatingMod => ToInt(GetF32("MELEE_ATTACK_RATING_MOD"));
        public int MeleeAttackSpeedModF32 => GetF32("MELEE_ATTACK_SPEED_MOD");
        public int RangeAttackSpeedModF32 => GetF32("RANGE_ATTACK_SPEED_MOD");
        public int MagicDamageModF32 => GetF32("MAGIC_DAMAGE_MOD");
        public int DivineDamageResist => ToInt(GetF32("DIVINE_DAMAGE_RESIST"));
        public int FireDamageResist => ToInt(GetF32("FIRE_DAMAGE_RESIST"));
        public int IceDamageResist => ToInt(GetF32("ICE_DAMAGE_RESIST"));
        public int PoisonDamageResist => ToInt(GetF32("POISON_DAMAGE_RESIST"));
        public int ShadowDamageResist => ToInt(GetF32("SHADOW_DAMAGE_RESIST"));

        public void AddF32(string attribute, int valueF32)
        {
            if (string.IsNullOrWhiteSpace(attribute)) return;
            string key = NormalizeAttribute(attribute);
            _attributesF32.TryGetValue(key, out int current);
            long total = (long)current + valueF32;
            _attributesF32[key] = total > int.MaxValue ? int.MaxValue : total < int.MinValue ? int.MinValue : (int)total;
        }

        public int GetF32(string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute)) return 0;
            _attributesF32.TryGetValue(NormalizeAttribute(attribute), out int value);
            return value;
        }

        private int ToInt(int valueF32)
        {
            return valueF32 >> 8;
        }

        private static string NormalizeAttribute(string attribute)
        {
            return attribute.Trim().Replace(" ", "_").Replace("-", "_").ToUpperInvariant();
        }
    }

    public static class PassiveAttributeModifiers
    {
        private static readonly HashSet<string> SupportedRuntimeAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "STRENGTH",
            "AGILITY",
            "ENDURANCE",
            "INTELLECT",
            "HEALTH_PER_ENDURANCE_MOD",
            "MANA_PER_INTELLECT_MOD",
            "HEALTH_MOD",
            "MELEE_ATTACK_RATING_MOD",
            "MELEE_ATTACK_SPEED_MOD",
            "RANGE_ATTACK_SPEED_MOD",
            "MAGIC_DAMAGE_MOD",
            "DIVINE_DAMAGE_RESIST",
            "FIRE_DAMAGE_RESIST",
            "ICE_DAMAGE_RESIST",
            "POISON_DAMAGE_RESIST",
            "SHADOW_DAMAGE_RESIST"
        };

        public static PassiveAttributeTotals Resolve(IEnumerable<(string Skill, int Level)> skills)
        {
            var totals = new PassiveAttributeTotals();
            if (skills == null) return totals;

            foreach (var skill in skills)
            {
                if (string.IsNullOrWhiteSpace(skill.Skill)) continue;
                if (!TryValidateRuntime(skill.Skill, out _)) continue;
                string modifierPath = ResolveModifierPath(skill.Skill);
                if (string.IsNullOrWhiteSpace(modifierPath)) continue;
                AddModifierAttributes(totals, skill.Skill, modifierPath, Math.Max(1, skill.Level));
            }

            return totals;
        }

        public static string ResolveModifierPath(string skillPath)
        {
            if (string.IsNullOrWhiteSpace(skillPath) || GCDatabase.Instance == null)
                return null;

            GCNode skillNode = GCDatabase.Instance.ResolveWithInheritance(skillPath);
            GCNode description = skillNode?.GetChild("Description") ?? skillNode;
            string modifierPath = description?.GetString("Modifier", null);
            if (!string.IsNullOrWhiteSpace(modifierPath))
                return modifierPath.Trim();

            string nestedPath = skillPath.Trim() + ".Modifier";
            return GCDatabase.Instance.ResolveWithInheritance(nestedPath) != null ? nestedPath : null;
        }

        public static bool TryValidateRuntime(string skillPath, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(skillPath) || GCDatabase.Instance == null)
            {
                reason = "passive-skill-unresolved";
                return false;
            }

            string modifierPath = ResolveModifierPath(skillPath);
            if (string.IsNullOrWhiteSpace(modifierPath))
            {
                reason = "passive-modifier-unresolved";
                return false;
            }

            GCNode modifier = GCDatabase.Instance.ResolveWithInheritance(modifierPath);
            GCNode description = modifier?.GetChild("Description") ?? modifier;
            if (description == null)
            {
                reason = "passive-modifier-description-unresolved";
                return false;
            }

            int attributeCount = 0;
            foreach (GCNode child in description.EnumerateChildrenInOrder())
            {
                if (!IsAttributeNode(child))
                {
                    reason = $"unsupported-passive-modifier-child:{child?.Extends ?? child?.Name ?? "unknown"}";
                    return false;
                }

                attributeCount++;
                string attribute = child.GetString("Attribute", null);
                if (string.IsNullOrWhiteSpace(attribute))
                {
                    reason = "unsupported-passive-empty-attribute";
                    return false;
                }
                string normalizedAttribute = NormalizeAttribute(attribute);
                if (!SupportedRuntimeAttributes.Contains(normalizedAttribute))
                {
                    reason = $"unsupported-passive-attribute:{attribute}";
                    return false;
                }
                if (!child.HasProperty("Value") && !child.HasProperty("ValueInc"))
                {
                    reason = $"unsupported-passive-attribute-value:{attribute}";
                    return false;
                }
                if (!child.GetBool("OverrideTable", false))
                {
                    reason = $"unsupported-passive-non-override-table:{attribute}";
                    return false;
                }
                int valueF32 = child.GetFixed32("Value", 0);
                int incrementF32 = child.GetFixed32("ValueInc", 0);
                if (valueF32 == 0 && incrementF32 == 0)
                {
                    reason = $"unsupported-passive-zero-attribute:{attribute}";
                    return false;
                }
            }

            if (attributeCount == 0)
            {
                reason = "unsupported-passive-empty-modifier";
                return false;
            }
            return true;
        }

        private static void AddModifierAttributes(PassiveAttributeTotals totals, string skillPath, string modifierPath, int level)
        {
            int modifierPowerLevelF32 = ResolveModifierPowerLevelF32(skillPath, level);
            GCNode modifier = GCDatabase.Instance.ResolveWithInheritance(modifierPath);
            GCNode description = modifier?.GetChild("Description") ?? modifier;
            if (description == null) return;

            foreach (GCNode attributeNode in EnumerateAttributeNodes(description))
            {
                string attribute = attributeNode.GetString("Attribute", null);
                if (string.IsNullOrWhiteSpace(attribute)) continue;
                int valueF32 = attributeNode.GetFixed32("Value", 0);
                int incrementF32 = attributeNode.GetFixed32("ValueInc", 0);
                int inputF32 = attributeNode.GetBool("UsePowerLevel", false)
                    ? modifierPowerLevelF32
                    : Math.Max(1, level) << 8;
                int inputOffsetF32 = inputF32 >= 0x100 ? inputF32 - 0x100 : inputF32;
                long scaledIncrementF32 = ((long)inputOffsetF32 * incrementF32) >> 8;
                long resolvedF32 = valueF32 + scaledIncrementF32;
                short nativeValue = unchecked((short)(resolvedF32 >> 8));
                totals.AddF32(attribute, nativeValue << 8);
            }
        }

        private static int ResolveModifierPowerLevelF32(string skillPath, int level)
        {
            GCNode skill = GCDatabase.Instance.ResolveWithInheritance(skillPath);
            GCNode description = skill?.GetChild("Description") ?? skill;
            if (description == null)
                return 0x100;
            int requiredLevel = description.GetInt("RequiredLevel", 1);
            int requiredLevelIncF32 = description.GetFixed32("RequiredLevelInc", 5 * 0x100);
            int levelOffsetF32 = Math.Max(0, level - 1) << 8;
            long scaledIncrementF32 = ((long)levelOffsetF32 * requiredLevelIncF32) >> 8;
            long powerLevelF32 = ((long)requiredLevel << 8) + scaledIncrementF32;
            if (powerLevelF32 > int.MaxValue)
                return int.MaxValue & ~0xFF;
            if (powerLevelF32 < int.MinValue)
                return int.MinValue;
            return (int)powerLevelF32 & ~0xFF;
        }

        private static IEnumerable<GCNode> EnumerateAttributeNodes(GCNode node)
        {
            if (node == null)
                yield break;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
                if (IsAttributeNode(child))
                    yield return child;
        }

        private static bool IsAttributeNode(GCNode node)
        {
            if (node == null) return false;
            if (node.HasProperty("Attribute")) return true;
            return !string.IsNullOrWhiteSpace(node.Extends) &&
                   node.Extends.IndexOf("Attribute", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeAttribute(string attribute)
        {
            return (attribute ?? string.Empty).Trim().Replace(" ", "_").Replace("-", "_").ToUpperInvariant();
        }

    }
}
