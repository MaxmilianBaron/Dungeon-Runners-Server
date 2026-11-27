using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DungeonRunners.Utilities;

namespace DungeonRunners.Data
{
    public sealed class PassiveAttributeTotals
    {
        private readonly Dictionary<string, int> _attributes = new Dictionary<string, int>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> AttributesF32 => _attributes.ToDictionary(pair => pair.Key, pair => unchecked(pair.Value << 8), StringComparer.Ordinal);
        public int Strength => GetInteger("STRENGTH");
        public int Agility => GetInteger("AGILITY");
        public int Endurance => GetInteger("ENDURANCE");
        public int Intellect => GetInteger("INTELLECT");
        public int HealthPerEnduranceMod => GetInteger("HEALTH_PER_ENDURANCE_MOD");
        public int ManaPerIntellectMod => GetInteger("MANA_PER_INTELLECT_MOD");
        public int HealthModF32 => GetF32("HEALTH_MOD");
        public int MeleeAttackRatingMod => GetInteger("MELEE_ATTACK_RATING_MOD");
        public int MeleeAttackSpeedModF32 => GetF32("MELEE_ATTACK_SPEED_MOD");
        public int RangeAttackSpeedModF32 => GetF32("RANGE_ATTACK_SPEED_MOD");
        public int MagicDamageModF32 => GetF32("MAGIC_DAMAGE_MOD");
        public int DivineDamageResist => GetInteger("DIVINE_DAMAGE_RESIST");
        public int FireDamageResist => GetInteger("FIRE_DAMAGE_RESIST");
        public int IceDamageResist => GetInteger("ICE_DAMAGE_RESIST");
        public int PoisonDamageResist => GetInteger("POISON_DAMAGE_RESIST");
        public int ShadowDamageResist => GetInteger("SHADOW_DAMAGE_RESIST");

        internal void AddInteger(string attribute, int value)
        {
            _attributes.TryGetValue(attribute, out int current);
            _attributes[attribute] = attribute == "FACTIONOVERRIDE" ? value : unchecked(current + value);
        }

        public int GetInteger(string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute)) return 0;
            _attributes.TryGetValue(attribute, out int value);
            return value;
        }

        public int GetF32(string attribute)
        {
            return unchecked(GetInteger(attribute) << 8);
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
                AddModifierAttributes(totals, skill.Skill, modifierPath, skill.Level);
            }

            return totals;
        }

        public static string ResolveModifierPath(string skillPath)
        {
            if (string.IsNullOrWhiteSpace(skillPath) || GCDatabase.Instance == null)
                return null;

            GCNode description = ResolveDescription(skillPath, "PassiveSkill", "PassiveSkillDesc");
            string modifierPath = description?.GetString("Modifier", null);
            return string.IsNullOrWhiteSpace(modifierPath) ? null : modifierPath.Trim();
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

            GCNode description = ResolveDescription(modifierPath, "AttributeModifier", "AttributeModifierDesc");
            if (description == null)
            {
                reason = "passive-modifier-description-unresolved";
                return false;
            }

            foreach (GCNode child in EnumerateAttributeNodes(description))
            {
                string attribute;
                try
                {
                    attribute = NativeAttributeIdentity.ResolveName(child);
                }
                catch (InvalidDataException)
                {
                    reason = $"unsupported-passive-attribute-id:{child.GetString("Attribute", string.Empty)}";
                    return false;
                }
                if (!SupportedRuntimeAttributes.Contains(attribute))
                {
                    reason = $"unsupported-passive-attribute:{attribute}";
                    return false;
                }
            }
            return true;
        }

        private static void AddModifierAttributes(PassiveAttributeTotals totals, string skillPath, string modifierPath, int level)
        {
            int modifierPowerLevelF32 = ResolveModifierPowerLevelF32(skillPath, level);
            GCNode description = ResolveDescription(modifierPath, "AttributeModifier", "AttributeModifierDesc")
                ?? throw new InvalidDataException($"AttributeModifier invalid description modifier={modifierPath}");

            foreach (GCNode attributeNode in EnumerateAttributeNodes(description))
            {
                string attribute = NativeAttributeIdentity.ResolveName(attributeNode);
                int inputF32 = attributeNode.GetBool("UsePowerLevel", false)
                    ? modifierPowerLevelF32
                    : unchecked((byte)level) << 8;
                totals.AddInteger(attribute, ResolveAttributeValue(attributeNode, inputF32));
            }
        }

        private static short ResolveAttributeValue(GCNode attributeNode, int inputF32)
        {
            int resolvedF32;
            if (!attributeNode.GetBool("OverrideTable", false) && attributeNode.EnumerateChildrenInOrder().Any())
            {
                var curve = new NativeCurveTable(attributeNode.EnumerateChildrenInOrder()
                    .Where(child => NativeAuthoredClasses.IsDerivedFrom(child.NativeClassName, "CurveTableEntry"))
                    .Select(child => (child.GetFixed32("Level", 0), child.GetFixed32("Value", 0))));
                resolvedF32 = curve.EvalFixed32(inputF32);
                if ((resolvedF32 & 0xFF) >= 0x7F)
                    resolvedF32 = unchecked(resolvedF32 + 0x100);
            }
            else
            {
                int valueF32 = attributeNode.GetFixed32("Value", 0);
                int incrementF32 = attributeNode.GetFixed32("ValueInc", 0);
                int inputOffsetF32 = inputF32 >= 0x100 ? inputF32 - 0x100 : inputF32;
                int scaledIncrementF32 = unchecked((int)(((long)inputOffsetF32 * incrementF32) >> 8));
                resolvedF32 = unchecked(valueF32 + scaledIncrementF32);
            }
            return unchecked((short)(resolvedF32 >> 8));
        }

        private static int ResolveModifierPowerLevelF32(string skillPath, int level)
        {
            GCNode description = ResolveDescription(skillPath, "PassiveSkill", "PassiveSkillDesc")
                ?? throw new InvalidDataException($"PassiveSkill invalid description skill={skillPath}");
            int requiredLevel = description.GetInt("RequiredLevel", 1);
            int requiredLevelIncF32 = description.GetFixed32("RequiredLevelInc", 5 * 0x100);
            int levelOffsetF32 = (unchecked((byte)level) - 1) << 8;
            int scaledIncrementF32 = unchecked((int)(((long)levelOffsetF32 * requiredLevelIncF32) >> 8));
            int powerLevelF32 = unchecked((requiredLevel << 8) + scaledIncrementF32);
            return powerLevelF32 & ~0xFF;
        }

        private static GCNode ResolveDescription(string path, string objectType, string descriptionType)
        {
            GCNode node = GCDatabase.Instance?.ResolveWithInheritance(path);
            return node != null && NativeAuthoredClasses.IsDerivedFrom(node.NativeClassName, objectType)
                ? node.GetDescription(descriptionType)
                : null;
        }

        private static IEnumerable<GCNode> EnumerateAttributeNodes(GCNode node)
        {
            if (node == null)
                yield break;
            foreach (GCNode child in node.EnumerateChildrenInOrder())
                if (NativeAuthoredClasses.IsDerivedFrom(child.NativeClassName, "Attribute"))
                    yield return child;
        }

    }
}
