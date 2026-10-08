using System.IO;

namespace DungeonRunners.Data
{
    public sealed class HeroAttributeData
    {
        public ushort Strength { get; }
        public ushort Agility { get; }
        public ushort Endurance { get; }
        public ushort Intellect { get; }
        public int HealthPerEnduranceFactorF32 { get; }
        public int ManaPerIntellectFactorF32 { get; }
        public int HealthPerEnduranceF32 { get; }
        public int HealthPerLevelF32 { get; }
        public int ManaPerIntellectF32 { get; }
        public int ManaPerLevelF32 { get; }

        private HeroAttributeData(GCNode description, GCDatabase database)
        {
            Strength = unchecked((ushort)description.GetInt("Strength"));
            Agility = unchecked((ushort)description.GetInt("Agility"));
            Endurance = unchecked((ushort)description.GetInt("Toughness"));
            Intellect = unchecked((ushort)description.GetInt("Power"));
            HealthPerEnduranceFactorF32 = description.GetFixed32("HealthPerEnduranceMod");
            ManaPerIntellectFactorF32 = description.GetFixed32("PowerPerIntellectMod");
            HealthPerEnduranceF32 = database.GetRequiredKnobFixed32("HealthPerEndurance");
            HealthPerLevelF32 = database.GetRequiredKnobFixed32("HeroHealthPerLevel");
            ManaPerIntellectF32 = database.GetRequiredKnobFixed32("PowerPerIntellect");
            ManaPerLevelF32 = database.GetRequiredKnobFixed32("PowerPerLevel");
        }

        public static HeroAttributeData Resolve(string avatarGcType)
        {
            if (string.IsNullOrWhiteSpace(avatarGcType))
                throw new InvalidDataException("Player authored avatar identity is missing.");
            GCDatabase database = GCDatabase.Instance;
            GCNode hero = database.ResolveWithInheritance(avatarGcType);
            if (hero == null || !NativeAuthoredClasses.IsDerivedFrom(hero.NativeClassName, "Hero"))
                throw new InvalidDataException($"Player authored avatar is not a Hero: {avatarGcType}");
            GCNode description = hero.GetDescription("HeroDesc");
            if (description == null)
                throw new InvalidDataException($"Player HeroDesc is missing or has the wrong native type: {avatarGcType}");
            return new HeroAttributeData(description, database);
        }
    }
}
