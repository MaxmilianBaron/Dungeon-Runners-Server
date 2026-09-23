using System;
using System.Collections.Generic;

namespace DungeonRunners.Combat
{
    public sealed class UnitSlotState
    {
        private readonly Dictionary<int, int> _slots = new Dictionary<int, int>();

        public IReadOnlyDictionary<int, int> Slots => _slots;

        public int this[int offset]
        {
            get => Get(offset);
            set => Set(offset, value);
        }

        public void ResetDefaults()
        {
            _slots.Clear();
            Set(UnitSlot.CriticalDamagePercent, 200);
            Set(UnitSlot.GlobalDamageTakenMod, 100);
            Set(UnitSlot.FireDamageTakenMod, 100);
            Set(UnitSlot.IceDamageTakenMod, 100);
            Set(UnitSlot.PoisonDamageTakenMod, 100);
            Set(UnitSlot.ShadowDamageTakenMod, 100);
            Set(UnitSlot.DivineDamageTakenMod, 100);
        }

        public int Get(int offset, int fallback = 0)
        {
            return _slots.TryGetValue(offset, out int value) ? value : fallback;
        }

        public void Set(int offset, int value)
        {
            _slots[offset] = value;
        }

        public void Add(int offset, int value)
        {
            _slots[offset] = Get(offset) + value;
        }

        public void ApplyPercentFinalizer(int valueOffset, int percentOffset)
        {
            int percent = Get(percentOffset, 100);
            if (percent == 100)
                return;
            Set(valueOffset, Fixed.MulPercent(Get(valueOffset), percent));
        }

        public string DescribeDamageCore()
        {
            return $"ar={Get(UnitSlot.AttackRating)}/{Get(UnitSlot.AttackRatingMod, 100)} " +
                   $"dmg={Get(UnitSlot.DamageBonus)}/{Get(UnitSlot.DamageMod, 100)} " +
                   $"def={Get(UnitSlot.DefenseRating)}/{Get(UnitSlot.DefenseRatingMod, 100)} " +
                   $"taken={Get(UnitSlot.GlobalDamageTakenMod, 100)} " +
                   $"fireTaken={Get(UnitSlot.FireDamageTakenMod, 100)} poisonTaken={Get(UnitSlot.PoisonDamageTakenMod, 100)}";
        }
    }

    public static class Fixed
    {
        public static int ToWireFromFixed32(int valueF32)
        {
            return valueF32;
        }

        public static int MulPercent(int value, int percent)
        {
            return (value * percent) / 100;
        }
    }

    public static class UnitSlot
    {
        public const int Strength = 0x0CC;
        public const int Agility = 0x0D0;
        public const int Endurance = 0x0D4;
        public const int Intellect = 0x0D8;
        public const int RpgSettings = 0x0EC;
        public const int AttackRating = 0x0F0;
        public const int AttackRatingMod = 0x0F4;
        public const int AttackSpeed = 0x0F8;
        public const int DamageBonus = 0x0FC;
        public const int DamageMod = 0x100;
        public const int CriticalChance = 0x104;
        public const int StunMod = 0x108;
        public const int MagicCriticalChance = 0x10C;
        public const int StunResist = 0x110;
        public const int HitPointSteal = 0x11C;
        public const int ManaPointSteal = 0x120;
        public const int CriticalDamagePercent = 0x118;
        public const int DefenseRating = 0x12C;
        public const int DefenseRatingMod = 0x130;
        public const int BlockChance = 0x138;
        public const int DamageImmunity = 0x140;
        public const int DamageResist = 0x144;
        public const int MaxHealth = 0x14C;
        public const int MaxMana = 0x150;
        public const int HealthRegen = 0x154;
        public const int ManaRegen = 0x158;
        public const int HealthRegenMod = 0x15C;
        public const int ManaRegenMod = 0x160;
        public const int HealthRegenBonus = 0x164;
        public const int ManaRegenBonus = 0x168;
        public const int MeleeAttackRating = 0x174;
        public const int MeleeAttackRatingMod = 0x178;
        public const int MeleeDamageBonus = 0x180;
        public const int MeleeDamageMod = 0x184;
        public const int MeleeCriticalChance = 0x188;
        public const int MeleeDefenseRating = 0x18C;
        public const int MeleeDefenseRatingMod = 0x190;
        public const int RangedAttackRating = 0x198;
        public const int RangedAttackRatingMod = 0x19C;
        public const int RangedDamageBonus = 0x1A4;
        public const int RangedDamageMod = 0x1A8;
        public const int RangedCriticalChance = 0x1AC;
        public const int RangedDefenseRating = 0x1B0;
        public const int RangedDefenseRatingMod = 0x1B4;
        public const int OneHandAttackRating = 0x1BC;
        public const int OneHandAttackRatingMod = 0x1C0;
        public const int OneHandDamageBonus = 0x1C8;
        public const int OneHandDamageMod = 0x1CC;
        public const int OneHandCriticalChance = 0x1D0;
        public const int TwoHandAttackRating = 0x1D4;
        public const int TwoHandAttackRatingMod = 0x1D8;
        public const int TwoHandDamageBonus = 0x1E0;
        public const int TwoHandDamageMod = 0x1E4;
        public const int TwoHandCriticalChance = 0x1E8;
        public const int IntellectMod = 0x1EC;
        public const int StrengthMod = 0x1F0;
        public const int AgilityMod = 0x1F4;
        public const int EnduranceMod = 0x1F8;
        public const int AllStatMod = 0x1FC;
        public const int MaxHealthMod = 0x200;
        public const int MaxManaMod = 0x204;
        public const int CrushingDamageMod = 0x230;
        public const int CrushingDamageBonus = 0x234;
        public const int CrushingResist = 0x238;
        public const int PiercingDamageMod = 0x23C;
        public const int PiercingDamageBonus = 0x240;
        public const int PiercingResist = 0x244;
        public const int SlashingDamageMod = 0x248;
        public const int SlashingDamageBonus = 0x24C;
        public const int SlashingResist = 0x250;
        public const int FireDamageMod = 0x254;
        public const int FireDamageBonus = 0x258;
        public const int FireResist = 0x25C;
        public const int FireWeaponAdd = 0x260;
        public const int IceDamageMod = 0x264;
        public const int IceDamageBonus = 0x268;
        public const int IceResist = 0x26C;
        public const int IceWeaponAdd = 0x270;
        public const int PoisonDamageMod = 0x274;
        public const int PoisonDamageBonus = 0x278;
        public const int PoisonResist = 0x27C;
        public const int PoisonWeaponAdd = 0x280;
        public const int ShadowDamageMod = 0x284;
        public const int ShadowDamageBonus = 0x288;
        public const int ShadowResist = 0x28C;
        public const int ShadowWeaponAdd = 0x290;
        public const int DivineDamageMod = 0x294;
        public const int DivineDamageBonus = 0x298;
        public const int DivineResist = 0x29C;
        public const int DivineWeaponAdd = 0x2A0;
        public const int MagicDamageMod = 0x2A4;
        public const int MagicDamageBonus = 0x2A8;
        public const int MagicResist = 0x2AC;
        public const int CastSpeedMod = 0x2BC;
        public const int GlobalDamageTakenMod = 0x2C4;
        public const int FireResistMod = 0x2C8;
        public const int IceResistMod = 0x2CC;
        public const int PoisonResistMod = 0x2D0;
        public const int ShadowResistMod = 0x2D4;
        public const int DivineResistMod = 0x2D8;
        public const int FireDamageTakenMod = 0x2DC;
        public const int IceDamageTakenMod = 0x2E0;
        public const int PoisonDamageTakenMod = 0x2E4;
        public const int ShadowDamageTakenMod = 0x2E8;
        public const int DivineDamageTakenMod = 0x2EC;
        public const int MaxHealthScalar = 0x2FC;
        public const int DpsModifier = 0x300;
    }
}
