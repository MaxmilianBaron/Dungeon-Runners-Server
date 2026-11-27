using System;
using System.IO;

namespace DungeonRunners.Data
{
    public readonly struct SpellDamageEffectData
    {
        public byte AttackTypeId { get; }
        public byte DamageTypeId { get; }
        public int DamageModF32 { get; }
        public int DamageVolatilityF32 { get; }
        public int CriticalChanceF32 { get; }
        public ushort StunMod { get; }
        public int StunModIncF32 { get; }
        public int StunModFactorF32 { get; }
        public int ChanceF32 { get; }

        private SpellDamageEffectData(GCNode node)
        {
            AttackTypeId = ReadAttackTypeId(node);
            DamageTypeId = ReadDamageTypeId(node);
            DamageModF32 = node.GetFixed32("DamageMod", 0x100);
            DamageVolatilityF32 = Math.Clamp(node.GetFixed32("DamageVolatility", 0x40), 0, 0x100);
            CriticalChanceF32 = node.GetFixed32("CriticalChance");
            StunMod = unchecked((ushort)node.GetInt("StunMod", 50));
            StunModIncF32 = unchecked(node.GetInt("StunModInc") << 8);
            StunModFactorF32 = unchecked(node.GetInt("StunModFactor", 1) << 8);
            ChanceF32 = node.GetFixed32("Chance", 0x6400);
        }

        private static byte ReadAttackTypeId(GCNode node)
        {
            return node.GetString("AttackType", null) switch
            {
                "MELEE" => 1,
                "RANGED" => 2,
                "MAGIC" => 3,
                _ => unchecked((byte)node.GetInt("AttackType", 3))
            };
        }

        private static byte ReadDamageTypeId(GCNode node)
        {
            return node.GetString("DamageType", null) switch
            {
                "CRUSHING" => 0,
                "PIERCING" => 1,
                "SLASHING" => 2,
                "FIRE" => 3,
                "ICE" => 4,
                "POISON" => 5,
                "SHADOW" => 6,
                "DIVINE" => 7,
                "LIGHTNING" => 8,
                _ => unchecked((byte)node.GetInt("DamageType", 3))
            };
        }

        public static SpellDamageEffectData Read(GCNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));
            node = GCDatabase.Instance.ResolveWithInheritance(node);
            if (!NativeAuthoredClasses.IsDerivedFrom(node?.NativeClassName, "SpellDamageEffect"))
                throw new InvalidDataException($"SpellDamageEffect has the wrong native type: {node?.CanonicalPath}");
            return new SpellDamageEffectData(node);
        }
    }
}
