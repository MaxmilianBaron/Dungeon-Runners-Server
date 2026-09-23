using DungeonRunners.Combat;
using DungeonRunners.Data;
using DungeonRunners.Core;
using DungeonRunners.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DungeonRunners.Networking
{
    public class PlayerState
    {
        public GCObject ActiveItem { get; set; }

        public int WeaponDamageF32 { get; set; } = 0;
        public int WeaponDamageVolatilityF32 { get; set; } = 0;
        public int WeaponLevel { get; set; } = 0;
        public string WeaponClass { get; set; } = "";
        public string WeaponDamageType { get; set; } = "";
        public string WeaponCategory { get; set; } = "";
        public bool WeaponStatsResolved { get; set; } = false;
        public int WeaponClassId { get; set; } = 0;
        public int DamageTypeId { get; set; } = -1;
        public int WeaponDamageLevel { get; set; } = 0;
        public int WeaponBaseDamage { get; set; } = 0;
        public bool WeaponBaseDamageTracksPlayerLevel { get; set; } = false;
        public string WeaponBaseDamageSource { get; set; } = "unresolved";
        public int WeaponRange { get; set; } = 0;
        public int WeaponRangeF32 { get; set; } = 0;
        public int WeaponInitUseRangeF32 { get; set; } = 64000;
        public int WeaponClientSyncToleranceF32 { get; set; } = 0;
        public int WeaponCooldownF32 { get; set; } = 0;
        public int WeaponSpeedF32 { get; set; } = 0;
        public bool WeaponUsesProjectile { get; set; } = false;
        public int WeaponShotType { get; set; } = 0;
        public int WeaponProjectileSpeedF32 { get; set; } = 0;
        public int WeaponProjectileSizeF32 { get; set; } = 0;
        public int WeaponBurstCount { get; set; } = 1;
        public uint WeaponEquipmentSlot { get; set; } = 0;
        public int WeaponStunMod { get; set; } = 100;
        public int StunMod { get; set; } = 100;
        public int MeleeAttackRatingModPercent { get; set; } = 0;
        public int MeleeAttackSpeedModPercentF32 { get; set; } = 0;
        public int RangeAttackSpeedModPercentF32 { get; set; } = 0;
        public int MagicDamageModPercentF32 { get; set; } = 0;
        public int AttackSpeed { get; private set; } = 100;
        public int CastSpeedMod { get; private set; } = 0;
        public int SpeedF32 { get; private set; } = 50 * 0x100;
        private int _baseSpeedMod = 125;
        public int SpeedMod => Math.Max(
            _baseSpeedMod + GetActiveAttributeModifierValue("SPEEDMOD"),
            GetActiveAttributeModifierValue("MIN_SPEED_MOD"));
        public int DamageTakenMod { get; set; } = 100;
        public int ArmorDefenseRating { get; set; } = 0;

        public int Strength { get; set; } = 10;
        public int Agility { get; set; } = 10;
        public int Intelligence { get; set; } = 10;
        public int Toughness { get; set; } = 10;
        public int Power { get; set; } = 10;
        private const int BASE_STAT_VALUE = 10;
        private int _allocatedStrength = 0;
        private int _allocatedAgility = 0;
        private int _allocatedEndurance = 0;
        private int _allocatedIntellect = 0;

        private int _level = 1;
        private string _className;
        public int Level => _level;
        public string ClassName => _className;
        public string AvatarGcType { get; set; } = "";
        public uint Experience { get; set; } = 0;
        public int ExperienceModPercent { get; set; } = 100;
        public uint Gold { get; set; } = 0;

        private static int ResolveXpCurveValueF32(int level)
        {
            return GCDatabase.Instance.RequireCurveValueFixed32("Experience", level);
        }

        public uint GetXPThreshold()
        {
            int targetLevel = _level + 1;
            if (targetLevel > GCDatabase.Instance.GetRequiredKnobInt("MaxLevel")) return uint.MaxValue;

            return checked((uint)(ResolveXpCurveValueF32(targetLevel) >> 8) * 100u);
        }
        public static uint GetXPPerKill(int monsterLevel, int playerLevel)
        {
            if (monsterLevel <= playerLevel - 5)
                return 0;

            int effectiveLevel = Math.Min(monsterLevel, playerLevel);

            long num = (long)(effectiveLevel << 8) << 8;
            int den = playerLevel << 8;
            int ratioF32 = (int)(num / den);

            uint xp = (uint)((ratioF32 * 500) >> 8);
            if (xp < 1) xp = 1;
            return xp;
        }

        public static uint GetBaseXPForLevel(int level)
        {
            return (uint)(((long)ResolveXpCurveValueF32(level) * 50L) >> 8);
        }

        public static uint GetClientThreshold(int nextLevel)
        {
            return checked((uint)(ResolveXpCurveValueF32(nextLevel) >> 8) * 100u);
        }


        public bool AddExperience(uint xp, Action<PlayerState> recomputeAttributes = null)
        {
            Experience += xp;
            bool didLevel = false;
            uint needed = GetClientThreshold(_level + 1);
            while (Experience >= needed && _level < GCDatabase.Instance.GetRequiredKnobInt("MaxLevel"))
            {
                int oldLevel = _level;
                uint oldHPWire = _currentHPWire;
                uint oldManaWire = _currentManaWire;
                uint oldMaxHPWire = MaxHPWire;
                uint oldMaxManaWire = MaxManaWire;

                _level++;
                Experience -= needed;
                _baseHPWire = CalculateBaseHP();
                _baseManaWire = CalculateMaxMana();
                RefreshRegenFactors("Hero::onAddExperience");
                recomputeAttributes?.Invoke(this);
                _currentHPWire = MaxHPWire;
                _currentManaWire = MaxManaWire;
                HasClientHP = true;
                HasClientMana = true;
                SetEntitySynchInfoHP(_currentHPWire);
                Debug.LogError($"[LEVEL-UP-CLIENT] level={oldLevel}->{_level} hp={oldHPWire}->{_currentHPWire}/{oldMaxHPWire}->{MaxHPWire} mana={oldManaWire}->{_currentManaWire}/{oldMaxManaWire}->{MaxManaWire} sourceFunction=Hero::onAddExperience full-hp-mana next={GetClientThreshold(_level + 1)}");
                didLevel = true;
                needed = GetClientThreshold(_level + 1);
            }
            return didLevel;
        }

        private static uint HP_PER_LEVEL_WIRE => (uint)GCDatabase.Instance.GetRequiredKnobFixed32("HeroHealthPerLevel");
        private uint _baseHPWire = 0;
        private uint _allocatedHPBonusWire = 0;
        private uint _equipmentHPBonusWire = 0;
        private uint _modifierHPBonusWire = 0;
        private int _passiveHPBonusWire = 0;
        private int _equipmentStrengthBonus = 0;
        private int _equipmentAgilityBonus = 0;
        private int _equipmentEnduranceBonus = 0;
        private int _equipmentIntellectBonus = 0;
        private long _attributeMaxHPWire = 0;
        private long _attributeMaxHPWireWithoutPassives = 0;
        private int _passiveStrengthMod = 0;
        private int _passiveAgilityMod = 0;
        private int _passiveEnduranceMod = 0;
        private int _passiveIntellectMod = 0;
        private int _passiveHealthPerEnduranceModPercent = 0;
        private int _passiveManaPerIntellectModPercent = 0;
        private int _passiveHealthModF32 = 0;
        private int _passiveDivineDamageResist = 0;
        private int _passiveFireDamageResist = 0;
        private int _passiveIceDamageResist = 0;
        private int _passivePoisonDamageResist = 0;
        private int _passiveShadowDamageResist = 0;
        private uint _currentHPWire = 0;
        private long _entitySynchInfoHPTick = -1;
        private bool _passiveMaxTransition = false;
        private uint _passiveTransitionMaxWire = 0;
        public const ushort DamageRegenSuppressTicks = 300;
        public const ushort ManaRegenSuppressTicks = 0x96;
        private uint _baseManaWire = 0;
        private uint _equipmentManaBonusWire = 0;
        private int _passiveManaBonusWire = 0;
        private long _attributeMaxManaWire = 0;
        private long _attributeMaxManaWireWithoutPassives = 0;
        private uint _currentManaWire = 0;

        public Dictionary<string, int> EquipmentStats { get; private set; } = new Dictionary<string, int>();
        public int AggroIncreaseModPercent { get; set; } = 0;
        public int AggroIncreaseBonus { get; set; } = 0;

        private int _regenFactor = 0;
        private ushort _regenCooldown = 0;
        private int _manaRegenFactor = 0;
        private ushort _manaRegenCooldown = 0;
        public ushort HealthRegenCooldownTicks => _regenCooldown;
        public ushort ManaRegenCooldownTicks => _manaRegenCooldown;
        private readonly List<AttributeModifierRuntime> _attributeModifiers = new List<AttributeModifierRuntime>();

        private class AttributeModifierRuntime
        {
            public string ModifierType;
            public string ModifierKey;
            public string SkillPath;
            public string EffectPath;
            public string Source;
            public uint SourceEntityId;
            public int HitPointRegenBonus;
            public int ManaPointRegenBonus;
            public int StrengthBonus;
            public int EnduranceBonus;
            public int IntellectBonus;
            public int StunResistBonus;
            public uint PowerLevel;
            public byte Level;
            public byte SourceIsSelf;
            public string StackRule;
            public ushort RemainingTicks;
            public bool RemoveOnDeath;
            public int TerminateWhenHitChance;
            public Dictionary<string, int> AuthoredAttributes;
        }

        public sealed class AttributeModifierSnapshot
        {
            public string ModifierType;
            public string ModifierKey;
            public string SkillPath;
            public string EffectPath;
            public string Source;
            public uint SourceEntityId;
            public uint PowerLevel;
            public byte Level;
            public byte SourceIsSelf;
            public ushort RemainingTicks;
        }

        public class AttributeModifierRemoval
        {
            public string ModifierType;
            public string ModifierKey;
            public string SkillPath;
            public string EffectPath;
            public string Source;
            public uint SourceEntityId;
            public string SourceFunction;
        }

        public event Action<PlayerState, AttributeModifierRemoval> OnAttributeModifierRemoved;

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

        public bool ShouldAcceptAttributeModifier(string modifierKey, string modifierType, string stackRule, uint powerLevel, ushort durationTicks)
        {
            string runtimeKey = !string.IsNullOrWhiteSpace(modifierKey) ? modifierKey : modifierType;
            int existingIndex = _attributeModifiers.FindIndex(m =>
                string.Equals(!string.IsNullOrWhiteSpace(m.ModifierKey) ? m.ModifierKey : m.ModifierType, runtimeKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0)
                return true;
            var existing = _attributeModifiers[existingIndex];
            return ShouldAcceptModifierStack(stackRule, powerLevel, durationTicks, existing.PowerLevel, existing.RemainingTicks);
        }

        public bool HasRemoveOnDeathAttributeModifier(string modifierType)
        {
            return _attributeModifiers.Any(modifier => modifier.RemoveOnDeath
                && string.Equals(modifier.ModifierType, modifierType, StringComparison.OrdinalIgnoreCase));
        }

        public bool HasRemoveOnDeathAttributeModifiers()
        {
            return _attributeModifiers.Any(modifier => modifier.RemoveOnDeath);
        }

        private uint CalculateBaseHP()
        {
            return ClassPassiveData.CalculateHPWire(_level, BASE_STAT_VALUE + Math.Max(0, _allocatedEndurance), 0);
        }

        private static uint ClampWire(long wire)
        {
            if (wire <= 0) return 0;
            if (wire >= uint.MaxValue) return uint.MaxValue;
            return (uint)wire;
        }

        public void SetCurrentMana(uint wireMana, string source = null, bool applyCooldown = true)
        {
            uint oldMana = _currentManaWire;
            _currentManaWire = Math.Min(wireMana, MaxManaWire);
            HasClientMana = true;
            if (applyCooldown && _currentManaWire < oldMana)
                _manaRegenCooldown = ManaRegenSuppressTicks;
            if (_currentManaWire != oldMana)
                Debug.LogError($"[MANA] source={source ?? "SetCurrentMana"} mana={oldMana}->{_currentManaWire}/{MaxManaWire} cooldown={_manaRegenCooldown}");
        }

        public void SetCurrentManaDeferClamp(uint wireMana)
        {
            _currentManaWire = wireMana;
            HasClientMana = true;
        }
        private uint CalculateMaxMana()
        {
            return ClassPassiveData.CalculateManaWire(_level, BASE_STAT_VALUE + Math.Max(0, _allocatedIntellect), 0);
        }

        public uint Op12HP => MaxHPWire;
        public uint Op12MaxHP => MaxHPWire;
        public uint EntitySynchInfoHP
        {
            get => _currentHPWire;
        }

        private uint _pvpRemapMaxHpWire = 0;
        public void SetPvpRemap(uint maxHpWire) => _pvpRemapMaxHpWire = maxHpWire;
        public uint PvpRemapMaxHpWire => _pvpRemapMaxHpWire;

        public uint MaxHPWire => _pvpRemapMaxHpWire > 0 ? _pvpRemapMaxHpWire
            : ClampWire((long)_baseHPWire + _allocatedHPBonusWire + _equipmentHPBonusWire + _modifierHPBonusWire + _passiveHPBonusWire + _attributeMaxHPWire);
        public uint CurrentHPWire => _currentHPWire;
        public uint MaxManaWire => ClampWire((long)_baseManaWire + _equipmentManaBonusWire + _passiveManaBonusWire + _attributeMaxManaWire);
        public uint CurrentManaWire => _currentManaWire;
        public uint MaxHPWireWithoutPassives => ClampWire((long)_baseHPWire + _allocatedHPBonusWire + _equipmentHPBonusWire + _modifierHPBonusWire + _attributeMaxHPWireWithoutPassives);
        public uint MaxManaWireWithoutPassives => ClampWire((long)_baseManaWire + _equipmentManaBonusWire + _attributeMaxManaWireWithoutPassives);
        public uint AllocatedHPBonusWire => _allocatedHPBonusWire;
        public uint EquipmentHPBonusWire => _equipmentHPBonusWire;
        public uint EquipmentManaBonusWire => _equipmentManaBonusWire;
        public uint ModifierHPBonusWire => _modifierHPBonusWire;
        public int PassiveHPBonusWire => _passiveHPBonusWire;
        public int PassiveManaBonusWire => _passiveManaBonusWire;
        public int AllocatedStrength => _allocatedStrength;
        public int AllocatedAgility => _allocatedAgility;
        public int AllocatedEndurance => _allocatedEndurance;
        public int AllocatedIntellect => _allocatedIntellect;
        public int ClientSpellStrength => Math.Max(1, BASE_STAT_VALUE + _allocatedStrength);
        public int ClientSpellAgility => Math.Max(1, BASE_STAT_VALUE + _allocatedAgility);
        public int ClientSpellIntellect => Intelligence;
        public int StunResist => Math.Max(0, 4 + GetEquipmentStat("STUN_RESIST", "STUNRESIST") + ResolveStunResistBonus());
        public byte UpdateNumber = 0;
        public bool IsDamageImmune { get; set; } = false;
        public bool IsZoneSpawnDamageImmune { get; set; } = false;
        public bool HasAnyDamageImmunity => IsDamageImmune || IsZoneSpawnDamageImmune;
        public bool IsInvisible { get; set; } = false;
        public bool HasClientHP { get; private set; } = false;
        public bool HasEntitySynchInfoHP { get; private set; } = false;
        public bool HasClientMana { get; private set; } = false;
        public bool HasPreservableHP => HasClientHP;
        public uint AvatarHP
        {
            get => _currentHPWire;
            set
            {
                _currentHPWire = MaxHPWire > 0 ? Math.Min(value, MaxHPWire) : value;
                HasClientHP = true;
                SetEntitySynchInfoHP(_currentHPWire);
            }
        }

        public PlayerState()
        {
            ActiveItem = null;
        }

        public void InitializeEntityEpochRuntime(bool preserveAttributeModifiers = false)
        {
            if (!preserveAttributeModifiers)
                ClearEntityEpochAttributeModifiers();
            _regenCooldown = 0;
            _manaRegenCooldown = 0;
            _entitySynchInfoHPTick = -1;
            _passiveMaxTransition = false;
            _passiveTransitionMaxWire = 0;
        }

        public void ClearEntityEpochAttributeModifiers()
        {
            _attributeModifiers.Clear();
            RefreshActiveAttributeModifierTotals();
        }

        public void InitializeStats(string className, int level, bool preserveAttributeModifiers = false)
        {
            if (string.IsNullOrWhiteSpace(className))
                throw new InvalidDataException("Player class is missing from persisted authored character data.");
            InitializeEntityEpochRuntime(preserveAttributeModifiers);
            _className = className.Trim();
            _level = Math.Max(1, level);
            _allocatedStrength = 0;
            _allocatedAgility = 0;
            _allocatedEndurance = 0;
            _allocatedIntellect = 0;
            _baseHPWire = CalculateBaseHP();
            _allocatedHPBonusWire = 0;
            _equipmentHPBonusWire = 0;
            _modifierHPBonusWire = 0;
            _passiveHPBonusWire = 0;
            _equipmentStrengthBonus = 0;
            _equipmentAgilityBonus = 0;
            _equipmentEnduranceBonus = 0;
            _equipmentIntellectBonus = 0;
            _passiveStrengthMod = 0;
            _passiveAgilityMod = 0;
            _passiveEnduranceMod = 0;
            _passiveIntellectMod = 0;
            MeleeAttackRatingModPercent = 0;
            MeleeAttackSpeedModPercentF32 = 0;
            RangeAttackSpeedModPercentF32 = 0;
            MagicDamageModPercentF32 = 0;
            _passiveHealthPerEnduranceModPercent = 0;
            _passiveManaPerIntellectModPercent = 0;
            _passiveHealthModF32 = 0;
            _passiveDivineDamageResist = 0;
            _passiveFireDamageResist = 0;
            _passiveIceDamageResist = 0;
            _passivePoisonDamageResist = 0;
            _passiveShadowDamageResist = 0;
            RefreshActiveAttributeModifierTotals();
            SpeedF32 = 50 * 0x100;
            _baseSpeedMod = 125;
            _currentHPWire = _baseHPWire;
            _entitySynchInfoHPTick = -1;
            RefreshRegenFactors("InitializeStats");
            _regenCooldown = 0;
            _manaRegenCooldown = 0;
            _baseManaWire = CalculateMaxMana();
            _equipmentManaBonusWire = 0;
            _passiveManaBonusWire = 0;
            _currentManaWire = _baseManaWire;
            HasClientHP = false;
            HasEntitySynchInfoHP = false;
            HasClientMana = false;
            EquipmentStats.Clear();
            Debug.LogError($"[PLAYERSTATE] INITIALIZED: {_className} Level {_level} | BaseHP={_baseHPWire} BaseMana={_baseManaWire}");
        }

        public void ApplyAllocatedStats(int strength, int agility, int endurance, int intellect)
        {
            bool preserveRegenClock = HasEntitySynchInfoHP && _entitySynchInfoHPTick >= 0;

            _allocatedStrength = Math.Max(0, strength);
            _allocatedAgility = Math.Max(0, agility);
            _allocatedEndurance = Math.Max(0, endurance);
            _allocatedIntellect = Math.Max(0, intellect);
            int finalStrength = BASE_STAT_VALUE + _allocatedStrength + _passiveStrengthMod;
            int finalAgility = BASE_STAT_VALUE + _allocatedAgility + _passiveAgilityMod;
            int finalEndurance = BASE_STAT_VALUE + _allocatedEndurance + _passiveEnduranceMod;
            int finalIntellect = BASE_STAT_VALUE + _allocatedIntellect + _passiveIntellectMod;
            Strength = Math.Max(1, finalStrength);
            Agility = Math.Max(1, finalAgility);
            Toughness = Math.Max(1, finalEndurance);
            Intelligence = Math.Max(1, finalIntellect);
            Power = Intelligence;
            _baseHPWire = CalculateBaseHP();
            _allocatedHPBonusWire = 0;
            _baseManaWire = CalculateMaxMana();
            RefreshActiveAttributeModifierTotals();

            if (!HasClientHP)
            {
                _currentHPWire = MaxHPWire;
            }

            if (!HasClientMana)
            {
                _currentManaWire = MaxManaWire;
            }

            SetEntitySynchInfoHP(_currentHPWire);
            Debug.LogError($"[ALLOC-STATS] STR={strength}->{Strength} AGI={agility}->{Agility} END={endurance}->{Toughness} INT={intellect}->{Intelligence} AllocHP={_allocatedHPBonusWire} MaxHP={MaxHPWire} CurrentHP={_currentHPWire} preserveRegenClock={preserveRegenClock}");
        }

        public void AddTotalHealthBonus(int hpBonus)
        {
            uint wireBonus = (uint)(hpBonus * 256);
            _equipmentHPBonusWire += wireBonus;
        }

        public void AddEnduranceBonus(int enduranceBonus)
        {
            _equipmentEnduranceBonus = checked(_equipmentEnduranceBonus + enduranceBonus);
        }

        public void AddStrengthBonus(int strengthBonus)
        {
            _equipmentStrengthBonus = checked(_equipmentStrengthBonus + strengthBonus);
        }

        public void AddAgilityBonus(int agilityBonus)
        {
            _equipmentAgilityBonus = checked(_equipmentAgilityBonus + agilityBonus);
        }

        public void AddModifierHPBonus(uint wireBonus)
        {
            _modifierHPBonusWire += wireBonus;
        }

        public void SetPassiveBonuses(int hpWireBonus, int manaWireBonus, int meleeAttackRatingModPercent = 0, int meleeAttackSpeedModPercentF32 = 0, int rangeAttackSpeedModPercentF32 = 0, int strengthMod = 0, int agilityMod = 0, int enduranceMod = 0, int intellectMod = 0, int healthPerEnduranceModPercent = 0, int manaPerIntellectModPercent = 0, int magicDamageModPercentF32 = 0, int healthModF32 = 0, int divineDamageResist = 0, int fireDamageResist = 0, int iceDamageResist = 0, int poisonDamageResist = 0, int shadowDamageResist = 0)
        {
            _passiveHPBonusWire = hpWireBonus;
            _passiveManaBonusWire = manaWireBonus;
            MeleeAttackRatingModPercent = meleeAttackRatingModPercent;
            MeleeAttackSpeedModPercentF32 = meleeAttackSpeedModPercentF32;
            RangeAttackSpeedModPercentF32 = rangeAttackSpeedModPercentF32;
            MagicDamageModPercentF32 = magicDamageModPercentF32;
            _passiveStrengthMod = strengthMod;
            _passiveAgilityMod = agilityMod;
            _passiveEnduranceMod = enduranceMod;
            _passiveIntellectMod = intellectMod;
            _passiveHealthPerEnduranceModPercent = healthPerEnduranceModPercent;
            _passiveManaPerIntellectModPercent = manaPerIntellectModPercent;
            _passiveHealthModF32 = healthModF32;
            _passiveDivineDamageResist = divineDamageResist;
            _passiveFireDamageResist = fireDamageResist;
            _passiveIceDamageResist = iceDamageResist;
            _passivePoisonDamageResist = poisonDamageResist;
            _passiveShadowDamageResist = shadowDamageResist;
            Strength = Math.Max(1, BASE_STAT_VALUE + _allocatedStrength + strengthMod);
            Agility = Math.Max(1, BASE_STAT_VALUE + _allocatedAgility + agilityMod);
            Toughness = Math.Max(1, BASE_STAT_VALUE + _allocatedEndurance + enduranceMod);
            Intelligence = Math.Max(1, BASE_STAT_VALUE + _allocatedIntellect + intellectMod);
            Power = Intelligence;
            RefreshActiveAttributeModifierTotals();
            if (!HasClientHP)
            {
                _currentHPWire = MaxHPWire;
            }
            SetEntitySynchInfoHP(_currentHPWire);
            if (!HasClientMana)
            {
                _currentManaWire = MaxManaWire;
            }
        }

        private void RefreshActiveAttributeModifierTotals()
        {
            int strengthBonus = 0;
            int agilityBonus = 0;
            int enduranceBonus = 0;
            int intellectBonus = 0;
            foreach (AttributeModifierRuntime modifier in _attributeModifiers)
            {
                strengthBonus += modifier.StrengthBonus + GetAuthoredAttributeValue(modifier, "STRENGTH");
                agilityBonus += GetAuthoredAttributeValue(modifier, "AGILITY");
                enduranceBonus += modifier.EnduranceBonus + GetAuthoredAttributeValue(modifier, "ENDURANCE");
                intellectBonus += modifier.IntellectBonus + GetAuthoredAttributeValue(modifier, "INTELLECT");
            }

            int passiveStrength = BASE_STAT_VALUE + _allocatedStrength + _passiveStrengthMod;
            int passiveAgility = BASE_STAT_VALUE + _allocatedAgility + _passiveAgilityMod;
            int passiveEndurance = BASE_STAT_VALUE + _allocatedEndurance + _passiveEnduranceMod;
            int passiveIntellect = BASE_STAT_VALUE + _allocatedIntellect + _passiveIntellectMod;
            Strength = Math.Max(1, passiveStrength + _equipmentStrengthBonus + strengthBonus);
            Agility = Math.Max(1, passiveAgility + _equipmentAgilityBonus + agilityBonus);
            Toughness = Math.Max(1, passiveEndurance + _equipmentEnduranceBonus + enduranceBonus);
            Intelligence = Math.Max(1, passiveIntellect + _equipmentIntellectBonus + intellectBonus);
            Power = Intelligence;

            uint passiveBaseHP = ScaleWirePercentF32(ClassPassiveData.CalculateHPWire(_level, Math.Max(1, passiveEndurance), _passiveHealthPerEnduranceModPercent), 100 * 0x100 + _passiveHealthModF32);
            uint passiveModifiedHP = ScaleWirePercentF32(ClassPassiveData.CalculateHPWire(_level, Toughness, _passiveHealthPerEnduranceModPercent), 100 * 0x100 + _passiveHealthModF32);
            _attributeMaxHPWire = (long)passiveModifiedHP - passiveBaseHP;

            int noPassiveEndurance = BASE_STAT_VALUE + _allocatedEndurance;
            uint noPassiveBaseHP = ClassPassiveData.CalculateHPWire(_level, Math.Max(1, noPassiveEndurance), 0);
            uint noPassiveModifiedHP = ClassPassiveData.CalculateHPWire(_level, Math.Max(1, noPassiveEndurance + _equipmentEnduranceBonus + enduranceBonus), 0);
            _attributeMaxHPWireWithoutPassives = (long)noPassiveModifiedHP - noPassiveBaseHP;

            uint passiveBaseMana = ClassPassiveData.CalculateManaWire(_level, Math.Max(1, passiveIntellect), _passiveManaPerIntellectModPercent);
            uint passiveModifiedMana = ClassPassiveData.CalculateManaWire(_level, Intelligence, _passiveManaPerIntellectModPercent);
            _attributeMaxManaWire = (long)passiveModifiedMana - passiveBaseMana;

            int noPassiveIntellect = BASE_STAT_VALUE + _allocatedIntellect;
            uint noPassiveBaseMana = ClassPassiveData.CalculateManaWire(_level, Math.Max(1, noPassiveIntellect), 0);
            uint noPassiveModifiedMana = ClassPassiveData.CalculateManaWire(_level, Math.Max(1, noPassiveIntellect + _equipmentIntellectBonus + intellectBonus), 0);
            _attributeMaxManaWireWithoutPassives = (long)noPassiveModifiedMana - noPassiveBaseMana;
        }

        private static uint ScaleWirePercentF32(uint wire, int percentF32)
        {
            if (wire == 0 || percentF32 <= 0) return 0;
            ulong scaled = (ulong)wire * (uint)percentF32 / (100u * 0x100u);
            return scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled;
        }

        public void ApplyMaxHPModifier(uint wireBonusToAdd)
        {
            uint oldMax = MaxHPWire;
            _modifierHPBonusWire += wireBonusToAdd;
            Debug.LogError($"[HP-MOD] MaxHP: {oldMax} -> {MaxHPWire} (CurrentHP stays {_currentHPWire})");
        }

        public void RemoveMaxHPModifier(uint wireBonusToRemove)
        {
            _modifierHPBonusWire = wireBonusToRemove > _modifierHPBonusWire
                ? 0 : _modifierHPBonusWire - wireBonusToRemove;
            if (_currentHPWire > MaxHPWire) _currentHPWire = MaxHPWire;
            SetEntitySynchInfoHP(_currentHPWire);
            Debug.LogError($"[HP-MOD] MaxHP now {MaxHPWire} (CurrentHP={_currentHPWire})");
        }

        public void ClearEquipmentBonuses()
        {
            _equipmentHPBonusWire = 0;
            _modifierHPBonusWire = 0;
            _equipmentManaBonusWire = 0;
            _equipmentStrengthBonus = 0;
            _equipmentAgilityBonus = 0;
            _equipmentEnduranceBonus = 0;
            _equipmentIntellectBonus = 0;
            WeaponDamageF32 = 0;
            WeaponDamageVolatilityF32 = 0;
            WeaponLevel = 0;
            WeaponClass = "";
            WeaponDamageType = "";
            WeaponCategory = "";
            WeaponStatsResolved = false;
            WeaponClassId = 0;
            DamageTypeId = -1;
            WeaponDamageLevel = 0;
            WeaponBaseDamage = 0;
            WeaponBaseDamageTracksPlayerLevel = false;
            WeaponBaseDamageSource = "unresolved";
            WeaponRange = 0;
            WeaponRangeF32 = 0;
            WeaponInitUseRangeF32 = 64000;
            WeaponClientSyncToleranceF32 = 0;
            WeaponCooldownF32 = 0;
            WeaponSpeedF32 = 0;
            WeaponUsesProjectile = false;
            WeaponShotType = 0;
            WeaponProjectileSpeedF32 = 0;
            WeaponProjectileSizeF32 = 0;
            WeaponBurstCount = 1;
            WeaponEquipmentSlot = 0;
            WeaponStunMod = 100;
            StunMod = 100;
            AttackSpeed = 100;
            CastSpeedMod = 0;
            ArmorDefenseRating = 0;
            EquipmentStats.Clear();
        }

        public void SetActiveSkillSpeedAttributes(int attackSpeed, int castSpeedMod)
        {
            AttackSpeed = unchecked((ushort)attackSpeed);
            CastSpeedMod = unchecked((ushort)castSpeedMod);
        }

        public void SetMovementSpeed(int speedF32, int speedMod)
        {
            if (speedF32 <= 0)
                throw new ArgumentOutOfRangeException(nameof(speedF32));
            SpeedF32 = speedF32;
            _baseSpeedMod = speedMod;
        }

        public void AddArmorDefenseRating(int defenseRating)
        {
            if (defenseRating > 0)
                ArmorDefenseRating += defenseRating;
        }

        public void AddManaBonus(int manaBonus)
        {
            uint wireBonus = (uint)(manaBonus * 256);
            _equipmentManaBonusWire += wireBonus;
        }

        public void AddIntellectManaBonus(int intellectBonus)
        {
            _equipmentIntellectBonus = checked(_equipmentIntellectBonus + intellectBonus);
        }

        public void RecalculateCurrentHP()
        {
            uint newMaxHP = MaxHPWire;
            if (HasClientHP)
            {
                if (_currentHPWire > newMaxHP)
                    _currentHPWire = newMaxHP;
                HasClientHP = true;
                Debug.LogError($"[HP-FINAL] Kept client HP: {_currentHPWire} (max={newMaxHP}, EntitySynchInfoHP={EntitySynchInfoHP})");
            }
            else
            {
                _currentHPWire = newMaxHP;
                Debug.LogError($"[HP-FINAL] base={_baseHPWire} + allocated={_allocatedHPBonusWire} + equip={_equipmentHPBonusWire} + mod={_modifierHPBonusWire} + passive={_passiveHPBonusWire} = {_currentHPWire} (EntitySynchInfoHP={EntitySynchInfoHP})");
            }
            SetEntitySynchInfoHP(_currentHPWire);
        }


        public bool ApplyOnDamageCallback(uint appliedDamageWire, string source = null)
        {
            if (appliedDamageWire == 0 || EquipmentStats == null) return false;
            int hpSteal = GetEquipmentStat("HIT_POINT_STEAL", "HITPOINTSTEAL");
            int manaSteal = GetEquipmentStat("MANA_POINT_STEAL", "MANAPOINTSTEAL", "MANA_STEAL", "MANASTEAL");
            if (hpSteal <= 0 && manaSteal <= 0) return false;

            uint oldHP = _currentHPWire;
            uint oldMana = _currentManaWire;
            uint maxHP = MaxHPWire;
            uint maxMana = MaxManaWire;

            if (hpSteal > 0 && maxHP > 0 && _currentHPWire > 0)
            {
                ulong heal = ((ulong)appliedDamageWire * (uint)hpSteal) / 100UL;
                if (heal > 0)
                {
                    ulong next = (ulong)_currentHPWire + heal;
                    _currentHPWire = next >= maxHP ? maxHP : (uint)next;
                    SetEntitySynchInfoHP(_currentHPWire);
                    HasClientHP = true;
                }
            }

            if (manaSteal > 0 && maxMana > 0)
            {
                ulong restore = ((ulong)appliedDamageWire * (uint)manaSteal) / 100UL;
                if (restore > 0)
                {
                    ulong next = (ulong)_currentManaWire + restore;
                    _currentManaWire = next >= maxMana ? maxMana : (uint)next;
                    HasClientMana = true;
                }
            }

            bool changed = oldHP != _currentHPWire || oldMana != _currentManaWire;
            if (changed)
                Debug.LogError($"[DAMAGE-CALLBACK] source=player hpSteal={hpSteal} manaSteal={manaSteal} appliedWire={appliedDamageWire} hp={oldHP}->{_currentHPWire}/{maxHP} mana={oldMana}->{_currentManaWire}/{maxMana} sourceFunction=Unit::onDamageCallback@0x0050C470");
            return changed;
        }

        private int GetEquipmentStat(params string[] keys)
        {
            if (EquipmentStats == null || keys == null) return 0;
            foreach (string key in keys)
                if (!string.IsNullOrEmpty(key) && EquipmentStats.TryGetValue(key, out int value))
                    return value;
            return 0;
        }

        public void TakeDamage(uint wireAmount)
        {
            ApplyDamage(wireAmount, true, true);
        }

        public void TakeQueriedDamage(uint wireAmount)
        {
            ApplyDamage(wireAmount, false, false);
        }

        private void ApplyDamage(uint wireAmount, bool applyDamageTakenMod, bool processDeathModifierEvent)
        {
            if (HasAnyDamageImmunity)
            {
                Debug.LogError($"[TAKEDAMAGE] Immune: {wireAmount} ignored at hp={_currentHPWire}");
                return;
            }
            uint adjustedWireAmount = applyDamageTakenMod ? ApplyDamageTakenMod(wireAmount, DamageTakenMod) : wireAmount;
            Debug.LogError($"[TAKEDAMAGE] Before: {_currentHPWire}, Subtracting: {adjustedWireAmount}");
            _currentHPWire = adjustedWireAmount > _currentHPWire ? 0 : _currentHPWire - adjustedWireAmount;
            HasClientHP = true;
            SetEntitySynchInfoHP(_currentHPWire);
            if (adjustedWireAmount != 0)
                ApplyDamageRegenCooldown();
            if (_currentHPWire == 0 && processDeathModifierEvent)
                ClearAttributeModifiers("death-damage");
            Debug.LogError($"[TAKEDAMAGE] After: {_currentHPWire}");
        }

        private static uint ApplyDamageTakenMod(uint wireAmount, int damageTakenMod)
        {
            if (wireAmount == 0) return 0;
            if (damageTakenMod < 1) return 0;
            ulong scaled = (ulong)wireAmount * (uint)damageTakenMod / 100UL;
            if (scaled >= uint.MaxValue) return uint.MaxValue;
            return (uint)scaled;
        }

        public void Heal(uint wireAmount)
        {
            _currentHPWire = Math.Min(_currentHPWire + wireAmount, MaxHPWire);
            HasClientHP = true;
            SetEntitySynchInfoHP(_currentHPWire);
        }

        public void SetCurrentHP(uint wireHP, bool applyDamageCooldown = false)
        {
            _currentHPWire = Math.Min(wireHP, MaxHPWire);
            HasClientHP = true;
            SetEntitySynchInfoHP(_currentHPWire);
            if (applyDamageCooldown && _currentHPWire < MaxHPWire)
                ApplyDamageRegenCooldown();
        }

        public void SetCurrentHPDeferClamp(uint wireHP)
        {
            _currentHPWire = wireHP;
            HasClientHP = true;
            HasEntitySynchInfoHP = true;
        }

        public void BeginPassiveMaxTransition(uint oldMaxWire)
        {
            if (oldMaxWire == 0) return;
            _passiveMaxTransition = true;
            _passiveTransitionMaxWire = oldMaxWire;
            Debug.LogError($"[MAX-TRANSITION] begin oldMax={oldMaxWire} newMax={MaxHPWire} curHp={_currentHPWire}");
        }

        public void RestoreToFull(bool preserveAttributeModifiers = true)
        {
            _currentHPWire = MaxHPWire;
            _currentManaWire = MaxManaWire;
            HasClientHP = true;
            HasClientMana = true;
            _regenCooldown = 0;
            _manaRegenCooldown = 0;
            if (!preserveAttributeModifiers)
                ClearAttributeModifiers("RestoreToFull");
            SetEntitySynchInfoHP(_currentHPWire);
        }

        private void SetEntitySynchInfoHP(uint wireHP)
        {
            _currentHPWire = wireHP;
            HasEntitySynchInfoHP = true;
        }

        public void AnchorEntitySynchInfoHPToTick(uint tick)
        {
            _entitySynchInfoHPTick = tick;
        }

        private int ResolveHitPointRegenBase()
        {
            return ResolveHeroDescRegenBase("HeroHealthRegen");
        }

        private int ResolveManaPointRegenBase()
        {
            return ResolveHeroDescRegenBase("HeroPowerRegen");
        }

        private static int ResolveHeroDescRegenBase(string knobName)
        {
            const int heroDescDefaultRegenF32 = 0x100;
            int knobF32 = GCDatabase.Instance.GetRequiredKnobFixed32(knobName);
            long value = (((long)knobF32 * heroDescDefaultRegenF32) >> 8) >> 8;
            return (ushort)value;
        }

        public void AdvanceEntitySynchInfoHPToTick(uint tick, string source = null, bool clientDriven = false)
        {
            if (_entitySynchInfoHPTick < 0)
            {
                _entitySynchInfoHPTick = tick;
                AdvanceEntitySynchInfoHPCore(0, source ?? "init", clientDriven);
                return;
            }

            long delta = (long)tick - _entitySynchInfoHPTick;
            int ticks = delta <= 0 ? 0 : delta >= int.MaxValue ? int.MaxValue : (int)delta;
            AdvanceEntitySynchInfoHPCore(ticks, source, clientDriven);
            _entitySynchInfoHPTick = tick;
        }

        private void AdvanceEntitySynchInfoHPCore(int ticks, string source = null, bool clientDriven = false)
        {
            if (!HasEntitySynchInfoHP)
            {
                SetEntitySynchInfoHP(_currentHPWire);
                return;
            }

            uint maxHP = MaxHPWire;
            if (_passiveMaxTransition)
            {
                if (clientDriven)
                {
                    _passiveMaxTransition = false;
                    Debug.LogError($"[MAX-TRANSITION] end source={source ?? "client-mover"} newMax={maxHP} curHp={_currentHPWire}");
                }
                else
                    maxHP = _passiveTransitionMaxWire;
            }
            uint maxMana = MaxManaWire;
            if (_currentHPWire > maxHP)
                _currentHPWire = maxHP;
            if (_currentManaWire > maxMana)
                _currentManaWire = maxMana;

            uint oldCurrentHP = _currentHPWire;
            uint oldMana = _currentManaWire;
            ushort oldHPCooldown = _regenCooldown;
            ushort oldManaCooldown = _manaRegenCooldown;
            int totalModifierDelta = 0;
            int totalModifierTicks = 0;
            int lastModifierBonus = ResolveHitPointRegenBonus();

            if (maxHP > 0)
            {
                uint runtimeHP = Math.Min(maxHP, _currentHPWire);
                _currentHPWire = runtimeHP;
            }

            if (_entitySynchInfoHPTick < 0)
            {
                if (oldCurrentHP != _currentHPWire)
                    Debug.LogError($"[PLAYER-REGEN] source={source ?? "init"} hp={oldCurrentHP}->{_currentHPWire} mana={oldMana}->{_currentManaWire}/{maxMana} hpCooldown={oldHPCooldown}->{_regenCooldown} manaCooldown={oldManaCooldown}->{_manaRegenCooldown} ticks=0 hpFactor={_regenFactor} manaFactor={_manaRegenFactor}");
                return;
            }

            if (ticks <= 0)
            {
                if (oldCurrentHP != _currentHPWire)
                    Debug.LogError($"[PLAYER-REGEN] source={source ?? "entity-synch-info"} hp={oldCurrentHP}->{_currentHPWire} mana={oldMana}->{_currentManaWire}/{maxMana} hpCooldown={oldHPCooldown}->{_regenCooldown} manaCooldown={oldManaCooldown}->{_manaRegenCooldown} ticks=0 hpFactor={_regenFactor} manaFactor={_manaRegenFactor}");
                return;
            }

            if (_passiveMaxTransition)
            {
                _passiveMaxTransition = false;
                maxHP = MaxHPWire;
                if (maxHP > 0 && _currentHPWire > maxHP)
                    _currentHPWire = maxHP;
                Debug.LogError($"[MAX-TRANSITION] end source=tick-clamp newMax={maxHP} curHp={_currentHPWire} sourceFunction=Unit::update@0x005093E0");
            }

            if (_regenFactor == 0)
                _regenFactor = ResolveHitPointRegenBase();
            if (_manaRegenFactor == 0)
                _manaRegenFactor = ResolveManaPointRegenBase();

            uint runtime = _currentHPWire;
            uint mana = _currentManaWire;
            for (int tickIndex = 0; tickIndex < ticks; tickIndex++)
            {
                maxHP = MaxHPWire;
                maxMana = MaxManaWire;
                if (runtime > maxHP)
                    runtime = maxHP;
                if (mana > maxMana)
                    mana = maxMana;
                int hitPointRegenBonus = ResolveHitPointRegenBonus();
                lastModifierBonus = hitPointRegenBonus;
                if (_regenCooldown > 0)
                    _regenCooldown--;
                if (_manaRegenCooldown > 0)
                    _manaRegenCooldown--;

                if (runtime > 0 && (runtime < maxHP || hitPointRegenBonus < 0))
                {
                    int regenDelta = CombatRuntime.ComputeUnitRegenDeltaWire(maxHP, _regenFactor, 0, hitPointRegenBonus, _regenCooldown > 0);
                    if (regenDelta != 0)
                    {
                        uint beforeRuntime = runtime;
                        runtime = CombatRuntime.ApplyUnitHPShiftWire(runtime, maxHP, regenDelta);
                        if (hitPointRegenBonus != 0 && beforeRuntime != runtime)
                        {
                            totalModifierTicks++;
                            totalModifierDelta += (int)runtime - (int)beforeRuntime;
                        }
                    }
                }

                if (maxMana > 0 && mana < maxMana)
                {
                    int manaRegenDelta = CombatRuntime.ComputeUnitRegenDeltaWire(maxMana, _manaRegenFactor, 0, ManaPointRegenBonus, _manaRegenCooldown > 0);
                    if (manaRegenDelta != 0)
                        mana = CombatRuntime.ApplyUnitHPShiftWire(mana, maxMana, manaRegenDelta);
                }
            }

            _currentHPWire = runtime;
            if (_currentHPWire == 0)
                ClearAttributeModifiers("death-regen");
            _currentManaWire = mana;
            if (_currentHPWire > 0)
            {
                HasClientHP = true;
                HasEntitySynchInfoHP = true;
            }
            if (oldMana != _currentManaWire)
                HasClientMana = true;
            if (oldCurrentHP != _currentHPWire
                || oldMana != _currentManaWire
                || (oldHPCooldown > 0 && _regenCooldown == 0)
                || (oldManaCooldown > 0 && _manaRegenCooldown == 0))
            {
                Debug.LogError($"[PLAYER-REGEN] source={source ?? "tick"} hp={oldCurrentHP}->{_currentHPWire} mana={oldMana}->{_currentManaWire}/{maxMana} hpCooldown={oldHPCooldown}->{_regenCooldown} manaCooldown={oldManaCooldown}->{_manaRegenCooldown} ticks={ticks} hpFactor={_regenFactor} manaFactor={_manaRegenFactor}");
            }
            if (totalModifierTicks > 0)
            {
                Debug.LogError($"[PLAYER-REGEN-MOD] source={source ?? "tick"} hp={oldCurrentHP}->{_currentHPWire}/{maxHP} ticks={ticks} modTicks={totalModifierTicks} bonus={lastModifierBonus} modDelta={totalModifierDelta} active={_attributeModifiers.Count} sourceFunction=Unit::update@0x005093E0 attr=HIT_POINT_REGEN_BONUS");
            }
        }

        public void AdvanceModifiersChild(string source = null)
        {
            AdvanceAttributeModifierTick(source ?? "Modifiers::update@0x00501E50");
        }

        public bool ApplyAttributeModifier(string modifierType, int hitPointRegenBonus, int manaPointRegenBonus, int strengthBonus, int enduranceBonus, int intellectBonus, int stunResistBonus, ushort durationTicks, bool removeOnDeath, string source = null, string modifierKey = null, uint sourceEntityId = 0, string skillPath = null, string effectPath = null, uint powerLevel = 0, string stackRule = null, int terminateWhenHitChance = 0, byte level = 0, byte sourceIsSelf = 1)
        {
            if (string.IsNullOrWhiteSpace(modifierType))
                return false;

            string runtimeKey = !string.IsNullOrWhiteSpace(modifierKey) ? modifierKey : modifierType;
            int existingIndex = _attributeModifiers.FindIndex(m =>
                string.Equals(!string.IsNullOrWhiteSpace(m.ModifierKey) ? m.ModifierKey : m.ModifierType, runtimeKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0 && !ShouldAcceptModifierStack(stackRule, powerLevel, durationTicks, _attributeModifiers[existingIndex].PowerLevel, _attributeModifiers[existingIndex].RemainingTicks))
            {
                Debug.LogError($"[PLAYER-MODIFIER] reject type={modifierType} key={runtimeKey} hpRegenBonus={hitPointRegenBonus} manaRegenBonus={manaPointRegenBonus} strength={strengthBonus} endurance={enduranceBonus} intellect={intellectBonus} stunResist={stunResistBonus} durationTicks={durationTicks} power={powerLevel} existingPower={_attributeModifiers[existingIndex].PowerLevel} existingRemaining={_attributeModifiers[existingIndex].RemainingTicks} stack={stackRule ?? ""} source={source ?? "unknown"} sourceFunction=Modifiers::addModifierLocal@0x00501770");
                return false;
            }

            var runtime = new AttributeModifierRuntime
            {
                ModifierType = modifierType,
                ModifierKey = runtimeKey,
                SkillPath = skillPath,
                EffectPath = effectPath,
                Source = source ?? "unknown",
                SourceEntityId = sourceEntityId,
                HitPointRegenBonus = hitPointRegenBonus,
                ManaPointRegenBonus = manaPointRegenBonus,
                StrengthBonus = strengthBonus,
                EnduranceBonus = enduranceBonus,
                IntellectBonus = intellectBonus,
                StunResistBonus = stunResistBonus,
                PowerLevel = powerLevel,
                Level = level,
                SourceIsSelf = sourceIsSelf,
                StackRule = stackRule,
                RemainingTicks = durationTicks,
                RemoveOnDeath = removeOnDeath,
                TerminateWhenHitChance = terminateWhenHitChance
            };

            if (existingIndex >= 0)
                _attributeModifiers[existingIndex] = runtime;
            else
                _attributeModifiers.Add(runtime);

            RefreshActiveAttributeModifierTotals();
            Debug.LogError($"[PLAYER-MODIFIER] apply type={modifierType} key={runtimeKey} hpRegenBonus={hitPointRegenBonus} manaRegenBonus={manaPointRegenBonus} strength={strengthBonus} endurance={enduranceBonus} intellect={intellectBonus} stunResist={stunResistBonus} durationTicks={durationTicks} power={powerLevel} stack={stackRule ?? ""} replace={existingIndex >= 0} removeOnDeath={removeOnDeath} terminateWhenHitChance={terminateWhenHitChance} source={source ?? "unknown"} sourceFunction=SpellModEffect::doEffect@0x00554460 Modifiers::addModifierLocal@0x00501770");
            return true;
        }

        public bool ApplyAuthoredAttributeModifier(string modifierType, IReadOnlyDictionary<string, int> attributes, ushort durationTicks, bool removeOnDeath, string source = null, string modifierKey = null, uint sourceEntityId = 0, string skillPath = null, string effectPath = null, uint powerLevel = 0, string stackRule = null, int terminateWhenHitChance = 0, byte level = 0, byte sourceIsSelf = 1)
        {
            if (string.IsNullOrWhiteSpace(modifierType))
                return false;
            string runtimeKey = !string.IsNullOrWhiteSpace(modifierKey) ? modifierKey : modifierType;
            int existingIndex = _attributeModifiers.FindIndex(modifier =>
                string.Equals(!string.IsNullOrWhiteSpace(modifier.ModifierKey) ? modifier.ModifierKey : modifier.ModifierType, runtimeKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0 && !ShouldAcceptModifierStack(stackRule, powerLevel, durationTicks, _attributeModifiers[existingIndex].PowerLevel, _attributeModifiers[existingIndex].RemainingTicks))
                return false;
            var authored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (attributes != null)
                foreach (KeyValuePair<string, int> attribute in attributes)
                    authored[CanonicalAttributeName(attribute.Key)] = attribute.Value;
            var runtime = new AttributeModifierRuntime
            {
                ModifierType = modifierType,
                ModifierKey = runtimeKey,
                SkillPath = skillPath,
                EffectPath = effectPath,
                Source = source ?? "unknown",
                SourceEntityId = sourceEntityId,
                PowerLevel = powerLevel,
                Level = level,
                SourceIsSelf = sourceIsSelf,
                StackRule = stackRule,
                RemainingTicks = durationTicks,
                RemoveOnDeath = removeOnDeath,
                TerminateWhenHitChance = terminateWhenHitChance,
                AuthoredAttributes = authored
            };
            if (existingIndex >= 0)
                _attributeModifiers[existingIndex] = runtime;
            else
                _attributeModifiers.Add(runtime);
            RefreshActiveAttributeModifierTotals();
            Debug.LogError($"[PLAYER-MODIFIER] apply-authored type={modifierType} key={runtimeKey} attributes={string.Join(",", authored)} durationTicks={durationTicks} power={powerLevel} stack={stackRule ?? ""} replace={existingIndex >= 0} removeOnDeath={removeOnDeath} source={source ?? "unknown"} sourceFunction=SpellModEffect::doEffect@0x00554460 Modifiers::addModifierLocal@0x00501770");
            return true;
        }

        public int GetActiveAttributeModifierValue(string attribute)
        {
            string key = CanonicalAttributeName(attribute);
            int value = GetPassiveAttributeModifierValue(key);
            foreach (AttributeModifierRuntime modifier in _attributeModifiers)
                value = checked(value + GetAuthoredAttributeValue(modifier, key));
            return value;
        }

        private int GetPassiveAttributeModifierValue(string attribute)
        {
            return CanonicalAttributeName(attribute) switch
            {
                "DIVINEDAMAGERESIST" => _passiveDivineDamageResist,
                "FIREDAMAGERESIST" => _passiveFireDamageResist,
                "ICEDAMAGERESIST" => _passiveIceDamageResist,
                "POISONDAMAGERESIST" => _passivePoisonDamageResist,
                "SHADOWDAMAGERESIST" => _passiveShadowDamageResist,
                _ => 0
            };
        }

        private static int GetAuthoredAttributeValue(AttributeModifierRuntime modifier, string attribute)
        {
            return modifier?.AuthoredAttributes != null
                ? GetDictionaryAttributeValue(modifier.AuthoredAttributes, attribute)
                : 0;
        }

        private static int GetDictionaryAttributeValue(IReadOnlyDictionary<string, int> attributes, string attribute)
        {
            return attributes != null && attributes.TryGetValue(CanonicalAttributeName(attribute), out int value) ? value : 0;
        }

        private static string CanonicalAttributeName(string attribute)
        {
            return (attribute ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
        }

        public bool ApplyHitPointRegenBonusModifier(string modifierType, int hitPointRegenBonus, ushort durationTicks, bool removeOnDeath, string source = null, string modifierKey = null, uint sourceEntityId = 0, string skillPath = null, string effectPath = null, uint powerLevel = 0, string stackRule = null, int terminateWhenHitChance = 0, byte level = 0, byte sourceIsSelf = 1)
        {
            return ApplyAttributeModifier(modifierType, hitPointRegenBonus, 0, 0, 0, 0, 0, durationTicks, removeOnDeath, source, modifierKey, sourceEntityId, skillPath, effectPath, powerLevel, stackRule, terminateWhenHitChance, level, sourceIsSelf);
        }

        public List<AttributeModifierSnapshot> GetAttributeModifierSnapshots()
        {
            var snapshots = new List<AttributeModifierSnapshot>(_attributeModifiers.Count);
            for (int modifierIndex = 0; modifierIndex < _attributeModifiers.Count; modifierIndex++)
            {
                AttributeModifierRuntime modifier = _attributeModifiers[modifierIndex];
                snapshots.Add(new AttributeModifierSnapshot
                {
                    ModifierType = modifier.ModifierType,
                    ModifierKey = modifier.ModifierKey,
                    SkillPath = modifier.SkillPath,
                    EffectPath = modifier.EffectPath,
                    Source = modifier.Source,
                    SourceEntityId = modifier.SourceEntityId,
                    PowerLevel = modifier.PowerLevel,
                    Level = modifier.Level,
                    SourceIsSelf = modifier.SourceIsSelf,
                    RemainingTicks = modifier.RemainingTicks
                });
            }
            return snapshots;
        }

        public bool TryGetAttributeModifierRemainingTicks(string modifierKey, out ushort remainingTicks)
        {
            remainingTicks = 0;
            if (string.IsNullOrWhiteSpace(modifierKey))
                return false;
            AttributeModifierRuntime modifier = _attributeModifiers.FirstOrDefault(candidate =>
                string.Equals(candidate.ModifierKey ?? candidate.ModifierType, modifierKey, StringComparison.OrdinalIgnoreCase));
            if (modifier == null)
                return false;
            remainingTicks = modifier.RemainingTicks;
            return true;
        }

        public int DoAttributeModifierDamageEvent(MersenneTwister rng, uint targetEntityId, string targetName, string source)
        {
            if (rng == null)
                throw new ArgumentNullException(nameof(rng));
            int draws = 0;
            for (int modifierIndex = 0; modifierIndex < _attributeModifiers.Count; modifierIndex++)
            {
                AttributeModifierRuntime modifier = _attributeModifiers[modifierIndex];
                if (modifier.RemainingTicks <= 1 || modifier.TerminateWhenHitChance <= 0)
                    continue;
                uint raw = RngLedger.Generate(
                    rng,
                    "room",
                    "Modifier::doEvent@0x004FEB40:TerminateWhenHitChance",
                    $"{targetName ?? "unknown"}#{targetEntityId}:{modifier.ModifierType ?? "modifier"}",
                    targetEntityId);
                draws++;
                int roll = (int)(raw % 100u) + 1;
                bool terminate = roll <= modifier.TerminateWhenHitChance;
                if (terminate)
                    modifier.RemainingTicks = 1;
                Debug.LogError($"[PLAYER-MODIFIER-HIT] target={targetName ?? "unknown"}#{targetEntityId} modifier={modifier.ModifierType ?? ""} key={modifier.ModifierKey ?? ""} remainingTicks={modifier.RemainingTicks} chance={modifier.TerminateWhenHitChance} raw=0x{raw:X8} roll={roll} terminate={terminate} rngPos={rng.CallsSinceReseed} source={source ?? "Damage::apply"} sourceFunction=Damage::apply@0x004F6580 Modifier::doEvent@0x004FEB40");
            }
            return draws;
        }

        public void DoAttributeModifierDeathEvent(string source)
        {
            ClearAttributeModifiers(source ?? "Unit::setDead");
        }

        private int ResolveHitPointRegenBonus()
        {
            int bonus = 0;
            foreach (var mod in _attributeModifiers)
                bonus += mod.HitPointRegenBonus + GetAuthoredAttributeValue(mod, "HIT_POINT_REGEN_BONUS");
            return bonus;
        }

        private int ManaPointRegenBonus
        {
            get
            {
                int bonus = 0;
                foreach (AttributeModifierRuntime modifier in _attributeModifiers)
                    bonus += modifier.ManaPointRegenBonus + GetAuthoredAttributeValue(modifier, "MANA_POINT_REGEN_BONUS");
                return bonus;
            }
        }

        private int ResolveStunResistBonus()
        {
            int bonus = 0;
            foreach (AttributeModifierRuntime modifier in _attributeModifiers)
                bonus += modifier.StunResistBonus + GetAuthoredAttributeValue(modifier, "STUN_RESIST");
            return bonus;
        }

        private void EmitAttributeModifierRemoved(AttributeModifierRuntime mod, string source, string sourceFunction)
        {
            if (mod == null)
                return;
            OnAttributeModifierRemoved?.Invoke(this, new AttributeModifierRemoval
            {
                ModifierType = mod.ModifierType,
                ModifierKey = !string.IsNullOrWhiteSpace(mod.ModifierKey) ? mod.ModifierKey : mod.ModifierType,
                SkillPath = mod.SkillPath,
                EffectPath = mod.EffectPath,
                Source = source ?? mod.Source ?? "unknown",
                SourceEntityId = mod.SourceEntityId,
                SourceFunction = sourceFunction
            });
        }

        private void AdvanceAttributeModifierTick(string source)
        {
            int modifierIndex = 0;
            while (modifierIndex < _attributeModifiers.Count)
            {
                var mod = _attributeModifiers[modifierIndex];
                if (mod.RemainingTicks == 0)
                {
                    modifierIndex++;
                    continue;
                }
                mod.RemainingTicks--;
                if (mod.RemainingTicks == 0)
                {
                    Debug.LogError($"[PLAYER-MODIFIER] expire type={mod.ModifierType} key={mod.ModifierKey ?? mod.ModifierType} hpRegenBonus={mod.HitPointRegenBonus} manaRegenBonus={mod.ManaPointRegenBonus} strength={mod.StrengthBonus} endurance={mod.EnduranceBonus} intellect={mod.IntellectBonus} stunResist={mod.StunResistBonus} source={source ?? "unknown"} sourceFunction=Modifier::update@0x004FF1B0");
                    _attributeModifiers.RemoveAt(modifierIndex);
                    RefreshActiveAttributeModifierTotals();
                    EmitAttributeModifierRemoved(mod, source, "Modifiers::update@0x00501E50 Modifiers::removeModifierLocal@0x00501B50");
                    continue;
                }
                modifierIndex++;
            }
        }

        private void ClearAttributeModifiers(string source)
        {
            int modifierIndex = 0;
            while (modifierIndex < _attributeModifiers.Count)
            {
                if (!_attributeModifiers[modifierIndex].RemoveOnDeath)
                {
                    modifierIndex++;
                    continue;
                }
                AttributeModifierRuntime modifier = _attributeModifiers[modifierIndex];
                Debug.LogError($"[PLAYER-MODIFIER] remove type={modifier.ModifierType} key={modifier.ModifierKey ?? modifier.ModifierType} source={source ?? "unknown"} sourceFunction=ModifierDesc.RemoveOnDeath");
                _attributeModifiers.RemoveAt(modifierIndex);
                RefreshActiveAttributeModifierTotals();
                EmitAttributeModifierRemoved(modifier, source, "ModifierDesc.RemoveOnDeath Modifiers::processRemoveModifier@0x00502390");
            }
        }

        public int RemoveAttributeModifiersFromSource(uint sourceEntityId, string source = null)
        {
            if (sourceEntityId == 0 || _attributeModifiers.Count == 0)
                return 0;
            int removed = 0;
            int modifierIndex = 0;
            while (modifierIndex < _attributeModifiers.Count)
            {
                var mod = _attributeModifiers[modifierIndex];
                if (mod.SourceEntityId != sourceEntityId)
                {
                    modifierIndex++;
                    continue;
                }
                Debug.LogError($"[PLAYER-MODIFIER] remove-source type={mod.ModifierType} key={mod.ModifierKey ?? mod.ModifierType} sourceEntity={sourceEntityId} source={source ?? "unknown"} sourceFunction=Modifier::update@0x004FF1B0 Modifiers::processRemoveModifier@0x00502390");
                _attributeModifiers.RemoveAt(modifierIndex);
                RefreshActiveAttributeModifierTotals();
                EmitAttributeModifierRemoved(mod, source ?? "source-unit-removed", "Modifier::update@0x004FF1B0 Modifiers::processRemoveModifier@0x00502390");
                removed++;
            }
            return removed;
        }

        public int RemoveAttributeModifiersFromExternalSources(uint ownerEntityId, string source = null)
        {
            if (ownerEntityId == 0 || _attributeModifiers.Count == 0)
                return 0;
            int removed = 0;
            int modifierIndex = 0;
            while (modifierIndex < _attributeModifiers.Count)
            {
                AttributeModifierRuntime modifier = _attributeModifiers[modifierIndex];
                if (modifier.SourceEntityId == 0 || modifier.SourceEntityId == ownerEntityId)
                {
                    modifierIndex++;
                    continue;
                }
                _attributeModifiers.RemoveAt(modifierIndex);
                RefreshActiveAttributeModifierTotals();
                EmitAttributeModifierRemoved(modifier, source ?? "external-source-left-instance", "Modifier::update@0x004FF1B0 Modifiers::processRemoveModifier@0x00502390");
                removed++;
            }
            return removed;
        }
        public void RefreshRegenFactors(string source = null)
        {
            _regenFactor = ResolveHitPointRegenBase();
            _manaRegenFactor = ResolveManaPointRegenBase();
            Debug.LogError($"[REGEN] source={source ?? "RefreshRegenFactors"} hpRegenFactor={_regenFactor} manaRegenFactor={_manaRegenFactor}");
        }

        public void SetRegenFactor(int hitPointRegen, int hitPointRegenMod = 0, int hitPointRegenBonus = 0)
        {
            _regenFactor = (hitPointRegenMod + 100) * hitPointRegen / 100 + hitPointRegenBonus;
            _manaRegenFactor = ResolveManaPointRegenBase();
            Debug.LogError($"[REGEN] hpRegenFactor={_regenFactor} manaRegenFactor={_manaRegenFactor}");
        }

        public void SetRegenCooldown(ushort ticks)
        {
            _regenCooldown = ticks;
        }

        public void ApplyDamageRegenCooldown()
        {
            _regenCooldown = DamageRegenSuppressTicks;
            Debug.LogError($"[PLAYER-REGEN-COOLDOWN] hpCooldown={_regenCooldown} hp={_currentHPWire}/{MaxHPWire}");
        }

        public bool IsRegenComplete => _currentHPWire >= MaxHPWire
            && (_currentManaWire >= MaxManaWire || _manaRegenFactor == 0);
        public void LogFullState(string context)
        {
            Debug.LogError($"[PLAYERSTATE-{context}] {_className} L{_level} | Base:{_baseHPWire} + Alloc:{_allocatedHPBonusWire} + Equip:{_equipmentHPBonusWire} + Mod:{_modifierHPBonusWire} + Passive:{_passiveHPBonusWire} = {EntitySynchInfoHP}");
        }
    }
}
