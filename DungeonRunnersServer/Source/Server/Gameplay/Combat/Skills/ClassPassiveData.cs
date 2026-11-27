using System;
using DungeonRunners.Utilities;

namespace DungeonRunners.Data
{
    public static class ClassPassiveData
    {
        public static uint CalculateHPWire(int level, int endurance, int healthPerEnduranceModPercent, HeroAttributeData attributes)
        {
            return CalculateHPWire(level, endurance, healthPerEnduranceModPercent, 0, 0, attributes);
        }

        public static uint CalculateHPWire(int level, int endurance, int healthPerEnduranceModPercent, int hitPointBonus, int healthModPercent, HeroAttributeData attributes)
        {
            if (attributes == null) throw new ArgumentNullException(nameof(attributes));
            int derived = CalculateResource(level, endurance, healthPerEnduranceModPercent,
                attributes.HealthPerEnduranceF32, attributes.HealthPerLevelF32, attributes.HealthPerEnduranceFactorF32);
            int total = NativeAttributeMath.ScalePercent(unchecked(hitPointBonus + derived), healthModPercent);
            return unchecked((uint)(total << 8));
        }

        public static uint CalculateManaWire(int level, int intellect, int manaPerIntellectModPercent, HeroAttributeData attributes)
        {
            return CalculateManaWire(level, intellect, manaPerIntellectModPercent, 0, 0, attributes);
        }

        public static uint CalculateManaWire(int level, int intellect, int manaPerIntellectModPercent, int manaPointBonus, int manaModPercent, HeroAttributeData attributes)
        {
            if (attributes == null) throw new ArgumentNullException(nameof(attributes));
            int derived = CalculateResource(level, intellect, manaPerIntellectModPercent,
                attributes.ManaPerIntellectF32, attributes.ManaPerLevelF32, attributes.ManaPerIntellectFactorF32);
            int total = NativeAttributeMath.ScalePercent(unchecked(manaPointBonus + derived), manaModPercent);
            return unchecked((uint)(total << 8));
        }

        private static uint CalculateResourceWire(int level, int primaryAttribute, int modifierPercent,
            int resourcePerAttributeF32, int resourcePerLevelF32, int heroFactorF32)
        {
            return unchecked((uint)(CalculateResource(level, primaryAttribute, modifierPercent,
                resourcePerAttributeF32, resourcePerLevelF32, heroFactorF32) << 8));
        }

        private static int CalculateResource(int level, int primaryAttribute, int modifierPercent,
            int resourcePerAttributeF32, int resourcePerLevelF32, int heroFactorF32)
        {
            int percentF32 = unchecked((modifierPercent + 100) << 8);
            int ratioF32 = (int)(((long)percentF32 << 8) / (100 << 8));
            int factorF32 = Math.Max(0, MultiplyFixed32(heroFactorF32, ratioF32));
            int coefficientF32 = MultiplyFixed32(resourcePerAttributeF32, factorF32);
            int primaryF32 = unchecked(Math.Max(1, primaryAttribute) << 8);
            int primaryResource = MultiplyFixed32(primaryF32, coefficientF32) >> 8;
            int levelF32 = unchecked((ushort)level) << 8;
            int levelResource = MultiplyFixed32(levelF32, resourcePerLevelF32) >> 8;
            return unchecked(primaryResource + levelResource);
        }

        private static int MultiplyFixed32(int left, int right)
        {
            return unchecked((int)(((long)left * right) >> 8));
        }
    }
}
