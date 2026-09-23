using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Networking;
using DungeonRunners.Core;

namespace DungeonRunners.Combat
{
    public static class DamageResolver
    {
        private const int NativeWeaponDamageVolatilityF32 = 0x40;

        public static int FromInt(int n) => n << 8;
        public static int ToInt(int f) => f >> 8;
        public static string FormatFixed8(int value)
        {
            long magnitude = value;
            bool negative = magnitude < 0;
            if (negative)
                magnitude = -magnitude;
            long whole = magnitude >> 8;
            long fraction = ((magnitude & 0xFF) * 10000 + 128) >> 8;
            if (fraction == 10000)
            {
                whole++;
                fraction = 0;
            }
            return $"{(negative ? "-" : "")}{whole}.{fraction:D4}";
        }
        public static int FixedMul(int a, int b) => (int)(((long)a * (long)b) >> 8);

        public static int ResolvePerStatBonus(int stat, int rateF32, int statModF32, int pctMod)
        {
            if (stat < 0) stat = 0;
            int innerMod = (int)(((long)statModF32 * (((pctMod + 100) * 0x10000) / 0x6400)) >> 8);
            if (innerMod < 0) innerMod = 0;
            int scaledRate = (int)(((long)rateF32 * innerMod) >> 8);
            return (int)(((long)(stat << 8) * scaledRate) >> 8) >> 8;
        }

        public static readonly WeaponDamageAddSlot[] WeaponDamageAddSlots =
        {
            new WeaponDamageAddSlot
            {
                Element = "Divine",
                DamageTypeId = 7,
                WeaponAddStats = new[] { "DIVINE_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "DIVINE_DAMAGE_BONUS", "DIVINEDAMAGEBONUS" },
                DamageModStats = new[] { "DIVINE_DAMAGE_MOD", "DIVINEDAMAGEMOD", "DIVINE_DAMAGE_PCT", "DIVINEDAMAGEPCT" }
            },
            new WeaponDamageAddSlot
            {
                Element = "Fire",
                DamageTypeId = 3,
                WeaponAddStats = new[] { "FIRE_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "FIRE_DAMAGE_BONUS", "FIREDAMAGEBONUS" },
                DamageModStats = new[] { "FIRE_DAMAGE_MOD", "FIREDAMAGEMOD", "FIRE_DAMAGE_PCT", "FIREDAMAGEPCT" }
            },
            new WeaponDamageAddSlot
            {
                Element = "Ice",
                DamageTypeId = 4,
                WeaponAddStats = new[] { "ICE_DAMAGE_WEAPON_ADD", "COLD_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "ICE_DAMAGE_BONUS", "ICEDAMAGEBONUS", "COLD_DAMAGE_BONUS", "COLDDAMAGEBONUS" },
                DamageModStats = new[] { "ICE_DAMAGE_MOD", "ICEDAMAGEMOD", "ICE_DAMAGE_PCT", "ICEDAMAGEPCT", "COLD_DAMAGE_MOD", "COLDDAMAGEMOD" }
            },
            new WeaponDamageAddSlot
            {
                Element = "Poison",
                DamageTypeId = 5,
                WeaponAddStats = new[] { "POISON_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "POISON_DAMAGE_BONUS", "POISONDAMAGEBONUS" },
                DamageModStats = new[] { "POISON_DAMAGE_MOD", "POISONDAMAGEMOD", "POISON_DAMAGE_PCT", "POISONDAMAGEPCT" }
            },
            new WeaponDamageAddSlot
            {
                Element = "Shadow",
                DamageTypeId = 6,
                WeaponAddStats = new[] { "SHADOW_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "SHADOW_DAMAGE_BONUS", "SHADOWDAMAGEBONUS" },
                DamageModStats = new[] { "SHADOW_DAMAGE_MOD", "SHADOWDAMAGEMOD", "SHADOW_DAMAGE_PCT", "SHADOWDAMAGEPCT" }
            }
        };

        public static int RoundFixed32(int f)
        {
            if ((f & 0xFF) > 0x7E) f += 0x100;
            return f & ~0xFF;
        }

        public static int RollDamageRange(int minDmg, int maxDmg, uint raw)
        {
            int range = Math.Max(0, maxDmg - minDmg);
            return range > 0 ? (int)(raw % ((uint)range + 1u)) + minDmg : minDmg;
        }

        public static int RollSpellDamageRange(int minDmg, int maxDmg, uint raw)
        {
            int minHp = minDmg >> 8;
            int maxHp = maxDmg >> 8;
            if (minHp < 1) minHp = 1;
            if (maxHp < minHp) maxHp = minHp;

            int damageHp = minHp;
            int rangeHp = maxHp - minHp;
            if (rangeHp > 0)
                damageHp = (int)(raw % (uint)rangeHp) + minHp;

            return Math.Max(0x100, damageHp << 8);
        }

        public static WeaponDamageResult ResolveWeaponDamage(WeaponDamageInput input)
        {
            var result = new WeaponDamageResult();
            if (input == null)
                return result;

            result.AttackRating = input.AttackRating;
            result.DefenseRating = input.DefenseRating;
            result.AttackerLevel = input.AttackerLevel;
            result.DefenderLevel = input.DefenderLevel;
            result.BlockChance = input.BlockChance;
            result.DamageLevel = input.DamageLevel;
            result.DamageBonus = input.DamageBonus;
            result.DamageMod = input.DamageMod;
            result.WeaponClassId = input.WeaponClassId;
            result.DamageTypeId = input.DamageTypeId;
            result.WeaponDamageF32 = input.WeaponDamageF32;
            result.WeaponVolatilityF32 = input.WeaponVolatilityF32;
            result.CritThreshold = input.CritThreshold;
            result.CritDamagePercent = input.CritDamagePercent;
            result.AttackerState = input.AttackerState;

            if (input.Rng == null)
            {
                result.Type = AttackResultType.Miss;
                result.ResultName = "NO-RNG";
                return result;
            }

            result.HitThreshold = ResolveHitThreshold(
                input.AttackRating,
                input.DefenseRating,
                input.AttackerLevel,
                input.IgnoreLevelDifference ? input.AttackerLevel : input.DefenderLevel);

            uint? combatEntityId = input.AttackerEntityId ?? input.DefenderEntityId;
            result.HitRaw = RngLedger.Generate(input.Rng, "unitOwnedCombat", $"{input.Source ?? "Weapon::applyDamage"}:hit", "Weapon::applyDamage", combatEntityId, input.AttackerEntityId, input.DefenderEntityId);
            result.HitRoll = (int)(result.HitRaw % 25700u);

            result.BlockRaw = RngLedger.Generate(input.Rng, "unitOwnedCombat", $"{input.Source ?? "Weapon::applyDamage"}:block", "Weapon::applyDamage", combatEntityId, input.AttackerEntityId, input.DefenderEntityId);
            result.BlockRoll = (int)(((result.BlockRaw >> 8) & 0xFF) % 100) + 1;

            result.IsHit = result.HitRoll < result.HitThreshold;
            result.IsBlocked = result.IsHit && result.BlockRoll < input.BlockChance;

            if (!result.IsHit)
            {
                result.Type = AttackResultType.Miss;
                result.ResultName = "MISS";
                result.RoomRngAfter = input.Rng.CallsSinceReseed;
                return result;
            }

            if (result.IsBlocked)
            {
                result.Type = AttackResultType.Block;
                result.ResultName = "BLOCK";
                result.RoomRngAfter = input.Rng.CallsSinceReseed;
                return result;
            }

            ComputeWeaponDamageRange(
                input.DamageLevel,
                input.DamageBonus,
                input.DamageMod,
                input.WeaponDamageF32,
                input.WeaponVolatilityF32,
                out int minDamage,
                out int maxDamage);
            result.MinDamageF32 = minDamage;
            result.MaxDamageF32 = maxDamage;

            result.DamageRaw = RngLedger.Generate(input.Rng, "unitOwnedCombat", $"{input.Source ?? "Weapon::applyDamage"}:damage", "Weapon::computeDamage", combatEntityId, input.AttackerEntityId, input.DefenderEntityId);
            int damage = RollDamageRange(minDamage, maxDamage, result.DamageRaw);

            if (input.CritThreshold > 0 && result.HitRoll < input.CritThreshold)
            {
                result.IsCritical = true;
                int critPercent = input.CritDamagePercent > 0 ? input.CritDamagePercent : 200;
                damage = (damage * critPercent) / 100;
            }
            if (damage < 0x100) damage = 0x100;

            result.DamageF32 = damage;
            result.DamageWire = (uint)Math.Max(1, damage);
            result.Type = result.IsCritical ? AttackResultType.Critical : AttackResultType.Hit;
            result.ResultName = result.IsCritical ? "CRIT" : "HIT";
            if (input.IncludeWeaponDamageAdds && input.AttackerState != null)
                result.DamageAdds.AddRange(ResolveWeaponDamageAdds(input.AttackerState, input.WeaponDamageF32));
            result.TotalDamageWire = result.DamageWire;
            result.TotalDamageF32 = result.DamageF32;
            foreach (WeaponDamageEvent add in result.DamageAdds)
            {
                result.TotalDamageWire = ClampWireAdd(result.TotalDamageWire, add.DamageWire);
                result.TotalDamageF32 = ClampIntAdd(result.TotalDamageF32, add.DamageF32);
            }
            result.RoomRngAfter = input.Rng.CallsSinceReseed;
            return result;
        }

        public static List<WeaponDamageEvent> ResolveWeaponDamageAdds(PlayerState state, int weaponDamageF32)
        {
            var events = new List<WeaponDamageEvent>();
            if (state == null)
                return events;

            foreach (WeaponDamageAddSlot slot in WeaponDamageAddSlots)
            {
                int weaponAdd = GetEquipmentStat(state, slot.WeaponAddStats);
                int damageBonus = GetEquipmentStat(state, slot.DamageBonusStats);
                int damageMod = GetEquipmentStat(state, slot.DamageModStats);
                int damageF32 = ComputeWeaponDamageAddF32(weaponAdd, damageBonus, damageMod, weaponDamageF32);
                if (damageF32 <= 0)
                    continue;

                events.Add(new WeaponDamageEvent
                {
                    Element = slot.Element,
                    DamageTypeId = slot.DamageTypeId,
                    DamageF32 = damageF32,
                    DamageWire = (uint)damageF32,
                    WeaponAdd = weaponAdd,
                    DamageBonus = damageBonus,
                    DamageMod = damageMod,
                    WeaponDamageF32 = weaponDamageF32
                });
            }

            return events;
        }

        public static int ComputeWeaponDamageAddF32(int weaponAdd, int damageBonus, int damageMod, int weaponDamageF32)
        {
            long baseF32 = (long)weaponAdd * 0x100L;
            if (baseF32 <= 0)
                return 0;

            long value = baseF32;
            value += (long)damageBonus * 0x100L;
            value += (baseF32 * ((long)damageMod << 8)) / 0x6400L;
            if (weaponDamageF32 > 0)
                value = (value * weaponDamageF32) >> 8;
            if (value <= 0)
                return 0;

            value = (value >> 8) << 8;
            if (value > int.MaxValue)
                return int.MaxValue & ~0xFF;
            return (int)value;
        }

        private static uint ClampWireAdd(uint left, uint right)
        {
            ulong sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }

        private static int ClampIntAdd(int left, int right)
        {
            long sum = (long)left + right;
            if (sum > int.MaxValue) return int.MaxValue;
            if (sum < int.MinValue) return int.MinValue;
            return (int)sum;
        }

        public static bool IsRangedWeapon(PlayerState state)
        {
            if (state == null) return false;

            string weaponClass = state.WeaponClass ?? string.Empty;
            string weaponCategory = state.WeaponCategory ?? string.Empty;

            if (ContainsIgnoreCase(weaponClass, "RANGED") ||
                ContainsIgnoreCase(weaponClass, "BOW") ||
                ContainsIgnoreCase(weaponClass, "CROSSBOW") ||
                ContainsIgnoreCase(weaponClass, "GUN") ||
                ContainsIgnoreCase(weaponClass, "CANNON"))
                return true;

            if (ContainsIgnoreCase(weaponCategory, "RANGED") ||
                ContainsIgnoreCase(weaponCategory, "BOW") ||
                ContainsIgnoreCase(weaponCategory, "CROSSBOW") ||
                ContainsIgnoreCase(weaponCategory, "GUN") ||
                ContainsIgnoreCase(weaponCategory, "CANNON"))
                return true;

            return false;
        }

        public static bool IsProjectileWeapon(PlayerState state)
        {
            if (state == null) return false;
            return IsRangedWeapon(state) && state.WeaponUsesProjectile;
        }

        public static string ResolveWeaponStatSource(PlayerState state)
        {
            return IsRangedWeapon(state) ? "UnitCache/RangedDamagePerAgility" : "UnitCache/MeleeDamagePerStrength";
        }

        public static int ResolveWeaponAttackSpeedPctF32(PlayerState state)
        {
            if (state == null) return 0;
            int valueF32 = checked(state.GetActiveAttributeModifierValue("ATTACK_SPEED_MOD") * 0x100);
            int weaponClassId = ResolveWeaponClassId(state);
            switch (weaponClassId)
            {
                case 1:
                    valueF32 = checked(valueF32
                        + state.MeleeAttackSpeedModPercentF32
                        + state.GetActiveAttributeModifierValue("MELEE_ATTACK_SPEED_MOD") * 0x100);
                    break;
                case 5:
                    valueF32 = checked(valueF32
                        + state.MeleeAttackSpeedModPercentF32
                        + state.GetActiveAttributeModifierValue("MELEE_ATTACK_SPEED_MOD") * 0x100
                        + state.GetActiveAttributeModifierValue("MELEE1H_ATTACK_SPEED_MOD") * 0x100);
                    break;
                case 6:
                case 8:
                    valueF32 = checked(valueF32
                        + state.MeleeAttackSpeedModPercentF32
                        + state.GetActiveAttributeModifierValue("MELEE_ATTACK_SPEED_MOD") * 0x100
                        + state.GetActiveAttributeModifierValue("MELEE2H_ATTACK_SPEED_MOD") * 0x100);
                    break;
                case 3:
                case 9:
                case 13:
                    valueF32 = checked(valueF32
                        + state.RangeAttackSpeedModPercentF32
                        + state.GetActiveAttributeModifierValue("RANGE_ATTACK_SPEED_MOD") * 0x100);
                    break;
            }
            return valueF32;
        }

        private static int ResolveAttackSpeedScaleF32(int pctF32)
        {
            int scaleF32 = 0x100 + (pctF32 / 100);
            const int minScaleF32 = 13;
            return Math.Max(minScaleF32, scaleF32);
        }

        public static int ApplyAttackSpeedPctToTicks(int ticks, int pctF32)
        {
            if (ticks <= 0 || pctF32 == 0) return Math.Max(0, ticks);
            int scaleF32 = ResolveAttackSpeedScaleF32(pctF32);
            int adjusted = (int)(((long)ticks * 0x100L + (scaleF32 / 2)) / scaleF32);
            return Math.Max(1, adjusted);
        }

        public static int ResolveWeaponSpeedField(PlayerState state)
        {
            int speedF32 = state != null && state.WeaponSpeedF32 > 0
                ? state.WeaponSpeedF32
                : 100 << 8;
            int scaleF32 = ResolveAttackSpeedScaleF32(ResolveWeaponAttackSpeedPctF32(state));
            int adjustedSpeedF32 = (int)(((long)speedF32 * scaleF32) >> 8);
            if (adjustedSpeedF32 <= 0x100)
                adjustedSpeedF32 = 100 << 8;
            return Math.Max(1, GCDatabase.RoundFixed32ToInt(adjustedSpeedF32));
        }

        public static int ResolveBasicAttackCooldownTicks(PlayerState state)
        {
            int cooldownF32 = state != null && state.WeaponCooldownF32 > 0
                ? state.WeaponCooldownF32
                : 0;
            if (cooldownF32 <= 0) return 0;
            return Math.Max(0, (int)(((long)cooldownF32 * 30L) >> 8));
        }

        public static int ResolveWeaponDamageBonus(PlayerState state)
        {
            if (state == null) return 0;
            int weaponClassId = ResolveWeaponClassId(state);
            int damageTypeId = ResolveDamageTypeId(state);
            int bonus = ResolveUnitBaseDamageBonus(state);
            bonus += ResolveUnitWeaponClassDamageBonus(state, weaponClassId);
            bonus += ResolveUnitDamageTypeBonus(state, damageTypeId);
            return ClampUShort(bonus);
        }

        private static bool ContainsIgnoreCase(string value, string needle)
        {
            return !string.IsNullOrEmpty(value) &&
                   !string.IsNullOrEmpty(needle) &&
                   value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int ResolveWeaponDamageLevel(PlayerState state)
        {
            if (state == null) return 1;
            if (state.WeaponBaseDamageTracksPlayerLevel)
            {
                int trackedLevel = Math.Max(1, ClampUShort(state.Level));
                state.WeaponBaseDamage = trackedLevel;
                state.WeaponDamageLevel = trackedLevel;
                return trackedLevel;
            }
            if (state.WeaponStatsResolved)
                return unchecked((ushort)state.WeaponBaseDamage);
            if (state.WeaponBaseDamage > 0)
                return Math.Max(1, ClampUShort(state.WeaponBaseDamage));
            int level = state.WeaponDamageLevel > 0
                ? state.WeaponDamageLevel
                : Math.Max(1, state.WeaponLevel);
            return Math.Max(1, ClampUShort(level));
        }

        public static int ResolveWeaponRuntimeBaseDamageLevel(GCObject item, int observerLevel)
        {
            int itemLevel = DungeonRunners.Gameplay.RPGSettings.ResolveNativeItemAttributeLevel(item, observerLevel);
            return ScaleWeaponDamageLevel(itemLevel);
        }

        private static int ScaleWeaponDamageLevel(int itemLevel)
        {
            int wdplF32 = GCDatabase.Instance.GetRequiredKnobFixed32("WeaponDamagePerLevel");
            int dpsF32 = GCDatabase.Instance.GetRequiredKnobFixed32("DPSModifier");
            int raw = FixedMul(itemLevel << 8, wdplF32);
            int scaled = FixedMul(raw, dpsF32) >> 8;
            return unchecked((ushort)scaled);
        }

        public static void ApplyWeaponRuntimeBaseDamage(
            PlayerState state,
            GCObject item,
            int playerLevel,
            string sourceTag)
        {
            if (state == null)
                return;

            int baseDamage = ResolveWeaponRuntimeBaseDamageLevel(item, playerLevel);
            const string source = "native-item-quality-level";

            state.WeaponBaseDamage = baseDamage;
            state.WeaponBaseDamageTracksPlayerLevel = false;
            state.WeaponBaseDamageSource = string.IsNullOrEmpty(sourceTag)
                ? source
                : $"{sourceTag}:{source}";
            state.WeaponDamageLevel = baseDamage;
            Debug.LogError($"[DAMAGE-LEVEL] sourceTag={sourceTag ?? "unknown"} source={source} playerLevel={playerLevel} storedLevel={item.StoredLevel} resolved={baseDamage} tracksPlayerLevel=False");
        }

        public static int ResolveLevelDamageBonus(PlayerState state)
        {
            return ResolveWeaponDamageLevel(state);
        }

        public static int ResolveDamageMod(PlayerState state)
        {
            return ResolveDamageMod(state, 0);
        }

        public static int ResolveDamageMod(PlayerState state, int skillDamageMod)
        {
            int weaponClassId = ResolveWeaponClassId(state);
            int damageTypeId = ResolveDamageTypeId(state);
            int damagePct = ResolveUnitBaseDamageModPct(state) + skillDamageMod;
            damagePct += ResolveUnitWeaponClassDamageModPct(state, weaponClassId);
            damagePct += ResolveUnitDamageTypeModPct(state, damageTypeId);
            int damageModPct = 100 + damagePct;
            if (damageModPct < 0) damageModPct = 0;
            return ClampUShort(damageModPct);
        }

        public static void LogDamageSlots(PlayerState state, WeaponDamageInput input, Monster monster, string source)
        {
            int weaponClassId = input != null ? input.WeaponClassId : ResolveWeaponClassId(state);
            int damageTypeId = input != null ? input.DamageTypeId : ResolveDamageTypeId(state);
            int baseBonus = ResolveUnitBaseDamageBonus(state);
            int classBonus = ResolveUnitWeaponClassDamageBonus(state, weaponClassId);
            int typeBonus = ResolveUnitDamageTypeBonus(state, damageTypeId);
            int categoryBonus = ResolveUnitWeaponCategoryDamageBonus(state);
            int baseModPct = ResolveUnitBaseDamageModPct(state);
            int classModPct = ResolveUnitWeaponClassDamageModPct(state, weaponClassId);
            int typeModPct = ResolveUnitDamageTypeModPct(state, damageTypeId);
            int categoryModPct = ResolveUnitWeaponCategoryDamageModPct(state);
            int dpsModifierF32 = GCDatabase.Instance.GetRequiredKnobFixed32("DPSModifier");
            string target = monster != null ? $"{monster.Name}#{monster.EntityId}" : "<none>";
            int rngBefore = input?.Rng != null ? input.Rng.CallsSinceReseed : -1;

            Debug.LogError(
                $"[DAMAGE-CLIENT-SLOTS] source={source ?? input?.Source ?? "unknown"} class={state?.ClassName ?? "<none>"} level={state?.Level ?? 0} target={target} " +
                $"weaponClass={state?.WeaponClass ?? "<none>"} weaponCategory={state?.WeaponCategory ?? "<none>"} damageType={state?.WeaponDamageType ?? "<none>"} weaponClassId={weaponClassId} damageTypeId={damageTypeId} " +
                $"clientBaseDamage={state?.WeaponBaseDamage ?? 0} clientBaseSource={state?.WeaponBaseDamageSource ?? "<none>"} damageLevel={input?.DamageLevel ?? ResolveWeaponDamageLevel(state)} " +
                $"bonusBase={baseBonus} bonusClass={classBonus} bonusType={typeBonus} bonusCategoryAudit={categoryBonus} bonusTotal={input?.DamageBonus ?? ResolveWeaponDamageBonus(state)} " +
                $"modBasePct={baseModPct} modClassPct={classModPct} modTypePct={typeModPct} modCategoryAudit={categoryModPct} dpsModifierF32={dpsModifierF32} damageMod={input?.DamageMod ?? ResolveDamageMod(state)} " +
                $"weaponDamageF32={input?.WeaponDamageF32 ?? GetWeaponBaseDamageF32(state)} volatilityF32={input?.WeaponVolatilityF32 ?? GetWeaponVolatilityF32(state)} critThreshold={input?.CritThreshold ?? ResolveCriticalThreshold(state, monster)} critPct={input?.CritDamagePercent ?? ResolveCriticalDamagePercent(state)} rngBefore={rngBefore}");
        }

        private static int ResolveClassDamageStatModF32(PlayerState state, string descKey)
        {
            string className = state?.ClassName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(className))
                throw new InvalidDataException($"HeroDesc class name is missing while resolving '{descKey}'.");

            string resolvedClassBase = null;
            var node = ResolveClassBaseNode(className, out resolvedClassBase);
            if (node == null)
                throw new InvalidDataException($"HeroDesc class '{className}' is not present in authored data while resolving '{descKey}'.");
            var desc = node?.GetChild("Description") ?? node;
            if (desc == null)
                throw new InvalidDataException($"HeroDesc description is missing for authored class '{resolvedClassBase}'.");
            if (!desc.HasProperty(descKey))
                throw new InvalidDataException($"HeroDesc property '{descKey}' is missing for authored class '{resolvedClassBase}'.");
            int valueF32 = desc.GetFixed32(descKey, 0);
            Debug.LogError($"[DAMAGE-CLASS-MOD] classBase={resolvedClassBase} className={className} descKey={descKey} valueF32={valueF32} source=gc");
            return valueF32;
        }

        private static GCNode ResolveClassBaseNode(string className, out string resolvedClassBase)
        {
            resolvedClassBase = null;
            if (string.IsNullOrWhiteSpace(className) || GCDatabase.Instance == null)
                return null;

            string classBase = className.Trim();
            if (!classBase.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                classBase += "Base";

            var candidates = new List<string> { classBase, $"avatar.classes.{classBase}" };
            if (classBase.Equals("MageBase", StringComparison.OrdinalIgnoreCase) ||
                classBase.Equals("WarlockBase", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("WarlockBase");
                candidates.Add("avatar.classes.WarlockBase");
            }

            foreach (string candidate in candidates)
            {
                var node = GCDatabase.Instance.ResolveWithInheritance(candidate);
                if (node == null)
                    continue;
                resolvedClassBase = candidate;
                return node;
            }

            return null;
        }

        public static int ResolveWeaponClassId(PlayerState state)
        {
            if (state != null && state.WeaponClassId > 0 && state.WeaponStatsResolved)
                return state.WeaponClassId;
            return ResolveWeaponClassId(state?.WeaponClass);
        }

        public static int ResolveWeaponClassId(string weaponClass)
        {
            string value = CanonicalStatName(weaponClass);
            if (TryResolveWeaponClassId(value, out int weaponClassId))
                return weaponClassId;
            return LogUnknownWeaponClass(value);
        }

        public static bool TryResolveWeaponClassId(string weaponClass, out int weaponClassId)
        {
            string value = CanonicalStatName(weaponClass);
            weaponClassId = 0;
            if (string.IsNullOrEmpty(value)) return false;
            return value switch
            {
                "HTH" => AssignWeaponClass(1, out weaponClassId),
                "2HRANGED" => AssignWeaponClass(3, out weaponClassId),
                "1HMELEE" or "1HSTAFF" or "1HMACE" or "1HSWORD" or "1HAXE" => AssignWeaponClass(5, out weaponClassId),
                "2HMELEE" or "2HMACE" or "2HSWORD" or "2HAXE" => AssignWeaponClass(6, out weaponClassId),
                "POLEARM" => AssignWeaponClass(8, out weaponClassId),
                "1HRANGED" or "1HCROSSBOW" or "1HBOW" or "1HGUN" => AssignWeaponClass(9, out weaponClassId),
                "2HCANNON" => AssignWeaponClass(13, out weaponClassId),
                "2HCROSSBOW" or "2HBOW" or "2HGUN" => AssignWeaponClass(3, out weaponClassId),
                _ => false
            };
        }

        private static bool AssignWeaponClass(int value, out int weaponClassId)
        {
            weaponClassId = value;
            return true;
        }

        private static int LogUnknownWeaponClass(string value)
        {
            RuntimeEvidence.LogFallbackHit("damage-weapon-class", "unknown", $"weaponClass={value ?? "<null>"} sourceFunction=blocked compatibility=none", 64);
            return 0;
        }

        public static int ResolveDamageTypeId(PlayerState state)
        {
            if (state != null && state.DamageTypeId >= 0 && state.WeaponStatsResolved)
                return state.DamageTypeId;
            return ResolveDamageTypeId(state?.WeaponDamageType);
        }

        public static int ResolveDamageTypeId(string damageType)
        {
            string value = CanonicalStatName(damageType);
            if (TryResolveDamageTypeId(value, out int damageTypeId))
                return damageTypeId;
            return LogUnknownDamageType(value);
        }

        public static bool TryResolveDamageTypeId(string damageType, out int damageTypeId)
        {
            string value = CanonicalStatName(damageType);
            damageTypeId = -1;
            if (string.IsNullOrEmpty(value)) return false;
            return value switch
            {
                "CRUSHING" => AssignDamageType(0, out damageTypeId),
                "PIERCING" => AssignDamageType(1, out damageTypeId),
                "SLASHING" => AssignDamageType(2, out damageTypeId),
                "FIRE" => AssignDamageType(3, out damageTypeId),
                "ICE" => AssignDamageType(4, out damageTypeId),
                "POISON" => AssignDamageType(5, out damageTypeId),
                "SHADOW" => AssignDamageType(6, out damageTypeId),
                "DIVINE" => AssignDamageType(7, out damageTypeId),
                _ => false
            };
        }

        private static bool AssignDamageType(int value, out int damageTypeId)
        {
            damageTypeId = value;
            return true;
        }

        private static int LogUnknownDamageType(string value)
        {
            RuntimeEvidence.LogFallbackHit("damage-type", "unknown", $"damageType={value ?? "<null>"} sourceFunction=blocked compatibility=none", 64);
            return -1;
        }

        public static int ResolveDamageTypeId(DamageElement element)
        {
            return element switch
            {
                DamageElement.PHYSICAL => 0,
                DamageElement.FIRE => 3,
                DamageElement.ICE => 4,
                DamageElement.POISON => 5,
                DamageElement.SHADOW => 6,
                DamageElement.DIVINE => 7,
                _ => LogUnknownDamageElement(element)
            };
        }

        private static int LogUnknownDamageElement(DamageElement element)
        {
            RuntimeEvidence.LogFallbackHit("damage-type", "unknown-damage-element", $"element={element} sourceFunction=blocked compatibility=none", 64);
            return -1;
        }

        public static int ResolveWeaponComputedDamageLevel(int weaponLevel)
        {
            return Math.Max(1, ClampUShort(weaponLevel));
        }

        private static int ResolveUnitBaseDamageBonus(PlayerState state)
        {
            return GetEquipmentStat(state, "DAMAGE_BONUS", "DAMAGEBONUS");
        }

        private static int ResolveUnitWeaponClassDamageBonus(PlayerState state, int weaponClassId)
        {
            switch (weaponClassId)
            {
                case 1:
                    return ResolveUnitMeleeCommonDamageBonus(state);
                case 3:
                case 9:
                case 13:
                    return ResolveUnitRangedDamageBonus(state);
                case 5:
                    return ResolveUnitMelee1HDamageBonus(state) +
                           ResolveUnitMeleeCommonDamageBonus(state);
                case 6:
                case 8:
                    return ResolveUnitMelee2HDamageBonus(state) +
                           ResolveUnitMeleeCommonDamageBonus(state);
                default:
                    RuntimeEvidence.LogFallbackHit("damage-weapon-class", "bonus-unresolved", $"weaponClass={state?.WeaponClass ?? "<null>"} weaponClassId={weaponClassId}", 64);
                    return 0;
            }
        }

        private static int ResolveUnitRangedDamageBonus(PlayerState state)
        {
            int agility = Math.Max(0, state?.Agility ?? 10);
            int rateF32 = GCDatabase.Instance.GetRequiredKnobFixed32("RangedDamagePerAgility");
            int statModF32 = ResolveClassDamageStatModF32(state, "RangedDamagePerAgilityMod");
            int bonus = ResolvePerStatBonus(agility, rateF32, statModF32, 0);
            bonus += GetEquipmentStat(state, "RANGE_DAMAGE_BONUS", "RANGED_DAMAGE_BONUS", "RANGEDAMAGEBONUS", "RANGEDDAMAGEBONUS");
            return bonus;
        }

        private static int ResolveUnitMeleeCommonDamageBonus(PlayerState state)
        {
            int strength = Math.Max(0, state?.Strength ?? 10);
            int rateF32 = GCDatabase.Instance.GetRequiredKnobFixed32("MeleeDamagePerStrength");
            int statModF32 = ResolveClassDamageStatModF32(state, "MeleeDamagePerStrengthMod");
            int bonus = ResolvePerStatBonus(strength, rateF32, statModF32, 0);
            bonus += GetEquipmentStat(state, "MELEE_DAMAGE_BONUS", "MELEEDAMAGEBONUS");
            return bonus;
        }

        private static int ResolveUnitMelee1HDamageBonus(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE1H_DAMAGE_BONUS", "1HMELEE_DAMAGE_BONUS", "MELEE_1H_DAMAGE_BONUS", "MELEE1HDAMAGEBONUS");
        }

        private static int ResolveUnitMelee2HDamageBonus(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE2H_DAMAGE_BONUS", "2HMELEE_DAMAGE_BONUS", "MELEE_2H_DAMAGE_BONUS", "MELEE2HDAMAGEBONUS");
        }

        private static int ResolveUnitDamageTypeBonus(PlayerState state, int damageTypeId)
        {
            return damageTypeId switch
            {
                0 => GetEquipmentStat(state, "CRUSHING_DAMAGE_BONUS", "CRUSHINGDAMAGEBONUS"),
                1 => GetEquipmentStat(state, "PIERCING_DAMAGE_BONUS", "PIERCINGDAMAGEBONUS"),
                2 => GetEquipmentStat(state, "SLASHING_DAMAGE_BONUS", "SLASHINGDAMAGEBONUS"),
                3 => GetEquipmentStat(state, "FIRE_DAMAGE_BONUS", "FIREDAMAGEBONUS"),
                4 => GetEquipmentStat(state, "ICE_DAMAGE_BONUS", "ICEDAMAGEBONUS", "COLD_DAMAGE_BONUS", "COLDDAMAGEBONUS"),
                5 => GetEquipmentStat(state, "POISON_DAMAGE_BONUS", "POISONDAMAGEBONUS"),
                6 => GetEquipmentStat(state, "SHADOW_DAMAGE_BONUS", "SHADOWDAMAGEBONUS"),
                7 => GetEquipmentStat(state, "DIVINE_DAMAGE_BONUS", "DIVINEDAMAGEBONUS"),
                _ => 0
            };
        }

        private static int ResolveUnitWeaponCategoryDamageBonus(PlayerState state)
        {
            string category = CanonicalStatName(state?.WeaponCategory);
            if (string.IsNullOrEmpty(category)) return 0;
            return GetEquipmentStat(state, category + "_DAMAGE_BONUS", category + "DAMAGEBONUS");
        }

        private static int ResolveUnitBaseDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "DAMAGE_MOD", "DAMAGEMOD", "DAMAGE_PCT", "DAMAGEPCT")
                + (state?.GetActiveAttributeModifierValue("DAMAGE_MOD") ?? 0);
        }

        private static int ResolveUnitWeaponClassDamageModPct(PlayerState state, int weaponClassId)
        {
            switch (weaponClassId)
            {
                case 1:
                    return ResolveUnitMeleeCommonDamageModPct(state);
                case 3:
                case 9:
                case 13:
                    return ResolveUnitRangedDamageModPct(state);
                case 5:
                    return ResolveUnitMelee1HDamageModPct(state) +
                           ResolveUnitMeleeCommonDamageModPct(state);
                case 6:
                case 8:
                    return ResolveUnitMelee2HDamageModPct(state) +
                           ResolveUnitMeleeCommonDamageModPct(state);
                default:
                    RuntimeEvidence.LogFallbackHit("damage-weapon-class", "mod-unresolved", $"weaponClass={state?.WeaponClass ?? "<null>"} weaponClassId={weaponClassId}", 64);
                    return 0;
            }
        }

        private static int ResolveUnitRangedDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "RANGE_DAMAGE_MOD", "RANGED_DAMAGE_MOD", "RANGEDAMAGEMOD", "RANGEDDAMAGEMOD");
        }

        private static int ResolveUnitMeleeCommonDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE_DAMAGE_MOD", "MELEEDAMAGEMOD")
                + (state?.GetActiveAttributeModifierValue("MELEE_DAMAGE_MOD") ?? 0);
        }

        private static int ResolveUnitMelee1HDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE1H_DAMAGE_MOD", "1HMELEE_DAMAGE_MOD", "MELEE_1H_DAMAGE_MOD", "MELEE1HDAMAGEMOD");
        }

        private static int ResolveUnitMelee2HDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE2H_DAMAGE_MOD", "2HMELEE_DAMAGE_MOD", "MELEE_2H_DAMAGE_MOD", "MELEE2HDAMAGEMOD");
        }

        private static int ResolveUnitDamageTypeModPct(PlayerState state, int damageTypeId)
        {
            return damageTypeId switch
            {
                0 => GetEquipmentStat(state, "CRUSHING_DAMAGE_MOD", "CRUSHINGDAMAGEMOD", "CRUSHING_DAMAGE_PCT", "CRUSHINGDAMAGEPCT"),
                1 => GetEquipmentStat(state, "PIERCING_DAMAGE_MOD", "PIERCINGDAMAGEMOD", "PIERCING_DAMAGE_PCT", "PIERCINGDAMAGEPCT"),
                2 => GetEquipmentStat(state, "SLASHING_DAMAGE_MOD", "SLASHINGDAMAGEMOD", "SLASHING_DAMAGE_PCT", "SLASHINGDAMAGEPCT"),
                3 => GetEquipmentStat(state, "FIRE_DAMAGE_MOD", "FIREDAMAGEMOD", "FIRE_DAMAGE_PCT", "FIREDAMAGEPCT"),
                4 => GetEquipmentStat(state, "ICE_DAMAGE_MOD", "ICEDAMAGEMOD", "ICE_DAMAGE_PCT", "ICEDAMAGEPCT", "COLD_DAMAGE_MOD", "COLDDAMAGEMOD"),
                5 => GetEquipmentStat(state, "POISON_DAMAGE_MOD", "POISONDAMAGEMOD", "POISON_DAMAGE_PCT", "POISONDAMAGEPCT"),
                6 => GetEquipmentStat(state, "SHADOW_DAMAGE_MOD", "SHADOWDAMAGEMOD", "SHADOW_DAMAGE_PCT", "SHADOWDAMAGEPCT"),
                7 => GetEquipmentStat(state, "DIVINE_DAMAGE_MOD", "DIVINEDAMAGEMOD", "DIVINE_DAMAGE_PCT", "DIVINEDAMAGEPCT"),
                _ => 0
            };
        }

        private static int ResolveUnitWeaponCategoryDamageModPct(PlayerState state)
        {
            string category = CanonicalStatName(state?.WeaponCategory);
            if (string.IsNullOrEmpty(category)) return 0;
            return GetEquipmentStat(state, category + "_DAMAGE_MOD", category + "DAMAGEMOD", category + "_DAMAGE_PCT", category + "DAMAGEPCT");
        }

        private static int ClampUShort(int value)
        {
            return Math.Max(0, Math.Min(0xFFFF, value));
        }

        private static int GetEquipmentStat(PlayerState state, params string[] names)
        {
            if (state?.EquipmentStats == null || state.EquipmentStats.Count == 0 || names == null) return 0;
            int total = 0;
            var consumed = new List<string>();
            for (int nameIndex = 0; nameIndex < names.Length; nameIndex++)
            {
                string name = names[nameIndex];
                if (!string.IsNullOrWhiteSpace(name) && !consumed.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    consumed.Add(name);
                    if (state.EquipmentStats.TryGetValue(name, out int value))
                        total += value;
                }
                string canonicalName = CanonicalStatName(name);
                if (!string.IsNullOrEmpty(canonicalName) && !consumed.Contains(canonicalName, StringComparer.OrdinalIgnoreCase))
                {
                    consumed.Add(canonicalName);
                    if (state.EquipmentStats.TryGetValue(canonicalName, out int canonicalValue))
                        total += canonicalValue;
                }
            }
            return total;
        }

        private static string CanonicalStatName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
        }

        public static int ResolveCriticalThreshold(PlayerState state, Monster defender)
        {
            int attackerLevel = Math.Max(0, state?.Level ?? 1);
            return ResolveCriticalThreshold(state, Math.Max(0, defender?.Level ?? attackerLevel));
        }

        public static int ResolveCriticalThreshold(PlayerState state, int defenderLevel)
        {
            int baseCrit = GCDatabase.Instance.GetRequiredKnobInt("HeroCriticalChance");
            int threshold = baseCrit << 8;
            int weaponClassId = ResolveWeaponClassId(state);
            int modifierPercent = weaponClassId == 1 || weaponClassId == 5 || weaponClassId == 6 || weaponClassId == 8
                ? (state?.GetActiveAttributeModifierValue("MELEE_CRITICAL_CHANCE_MOD") ?? 0)
                : 0;
            int modifierF32 = modifierPercent << 8;
            int modifierScaleF32 = (int)(((long)modifierF32 << 8) / 0x6400L);
            long modifiedThreshold = threshold + (((long)threshold * modifierScaleF32) >> 8);
            threshold = (int)Math.Clamp(modifiedThreshold, 0L, 0x6400L);
            int attackerLevel = Math.Max(0, state?.Level ?? 1);
            defenderLevel = Math.Max(0, defenderLevel);
            int levelDelta = attackerLevel - defenderLevel;
            if (levelDelta > 0)
                threshold += levelDelta * 0x500;
            if (threshold > 0x5A00) threshold = 0x5A00;
            if (threshold < 0) threshold = 0;
            return threshold;
        }

        public static int ResolveCriticalDamagePercent(PlayerState state)
        {
            return 200;
        }

        public static int ResolveMonsterCriticalThreshold(Monster attacker, PlayerState defender)
        {
            return ResolveMonsterCriticalThreshold(attacker, defender?.Level ?? attacker?.Level ?? 1);
        }

        public static int ResolveMonsterCriticalThreshold(Monster attacker, int defenderLevel)
        {
            int baseCrit = MonsterUnitStatsBuilder.ComputeBaseCriticalChanceF32(attacker?.CritChanceF32 ?? 0);
            if (baseCrit <= 0) return 0;
            int threshold = baseCrit << 8;
            int attackerLevel = attacker != null ? attacker.Level : 1;
            int levelDelta = attackerLevel - defenderLevel;
            if (levelDelta > 0)
                threshold += levelDelta * 0x500;
            if (threshold > 0x5A00) threshold = 0x5A00;
            if (threshold < 0) threshold = 0;
            return threshold;
        }

        public static int ResolveAvatarAttackRating(PlayerState state)
        {
            if (state == null) return 0;
            int rateF32 = GCDatabase.Instance.GetRequiredKnobFixed32("AttackRatingPerAgility");
            int statModF32 = ResolveClassDamageStatModF32(state, "AttackRatingPerAgilityMod");
            int modPercent = (IsRangedWeapon(state) ? 0 : state.MeleeAttackRatingModPercent)
                + state.GetActiveAttributeModifierValue("ATTACK_RATING_MOD");
            modPercent = Math.Max(-100, modPercent);
            return Math.Max(0, ResolvePerStatBonus(state.Agility, rateF32, statModF32, modPercent));
        }

        public static int ResolveMonsterDefenseRating(Monster monster)
        {
            return ResolveMonsterCurveRating(monster?.UseHenchmanCurveTables == true ? "HenchmanDefenseRating" : "MonsterDefenseRating", monster);
        }

        public static int ResolveMonsterAttackRating(Monster monster)
        {
            return ResolveMonsterCurveRating(monster?.UseHenchmanCurveTables == true ? "HenchmanAttackRating" : "MonsterAttackRating", monster, true);
        }

        private static int ResolveMonsterCurveRating(string curveName, Monster monster, bool attack = false)
        {
            if (monster == null) return 0;
            int authoredF32 = attack ? monster.AttackRatingF32 : monster.DefenseRatingF32;
            int modifierPercent = attack
                ? monster.GetActiveAttributeModifierValue("ATTACK_RATING_MOD")
                : monster.GetActiveAttributeModifierValue("DEFENSE_RATING_MOD");
            if (modifierPercent != 0)
                authoredF32 = (int)Math.Max(0, ((long)authoredF32 * Math.Max(0, 100 + modifierPercent)) / 100L);
            if (authoredF32 <= 0) return 0;
            int tableF32 = GCDatabase.Instance.RequireCurveValueFixed32(curveName, Mathf.Clamp(monster.Level, 1, 110));
            int rating = (int)(((long)authoredF32 * tableF32) >> 16);
            return (ushort)rating;
        }

        public static int ResolveHitThreshold(int attackRating, int defenseRating, int attackerLevel, int defenderLevel)
        {
            int attack = Math.Max(0, attackRating);
            int defense = Math.Max(0, defenseRating);
            int chancePercent = attack + defense == 0 ? 0 : (attack * 100) / (attack + defense);
            int threshold = chancePercent << 8;
            int levelDelta = Math.Max(0, Math.Min(110, defenderLevel)) - Math.Max(0, Math.Min(110, attackerLevel));
            threshold -= levelDelta * 0x500;
            if (threshold < 0x0A00) threshold = 0x0A00;
            return threshold;
        }

        public static void ComputeWeaponDamageRange(int damageLevel, int damageBonus, int damageMod, int weaponDamageF32, int volatilityF32, out int minDamage, out int maxDamage)
        {
            if (damageLevel < 0) damageLevel = 0;
            if (damageBonus < 0) damageBonus = 0;
            if (damageMod < 0) damageMod = 0;
            if (weaponDamageF32 <= 0) weaponDamageF32 = 0x100;
            if (volatilityF32 < 0) volatilityF32 = 0;

            int normalized = FixedMul((damageLevel + damageBonus) << 8, weaponDamageF32);
            normalized = (int)(((long)normalized * ((long)damageMod << 8)) / 0x6400L);

            int spread = FixedMul(normalized, volatilityF32);
            minDamage = RoundFixed32(normalized - spread);
            maxDamage = RoundFixed32(normalized + spread);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            if (maxDamage < minDamage) maxDamage = minDamage;
        }

        public static int GetWeaponBaseDamageF32(PlayerState state)
        {
            if (state?.WeaponDamageF32 > 0)
            {
                Debug.LogError($"[DMG] Weapon multiplier: {FormatFixed8(state.WeaponDamageF32)} (F32: 0x{state.WeaponDamageF32:X}) source=playerState-fixed");
                return state.WeaponDamageF32;
            }
            RuntimeEvidence.LogFallbackHit("damage-weapon", "missing-fixed-weapon-damage", $"class={state?.ClassName ?? "<null>"}", 64);
            return 0x100;
        }

        public static int GetWeaponVolatilityF32(PlayerState state)
        {
            if (state?.WeaponDamageVolatilityF32 > 0)
                return Math.Max(0, Math.Min(0xF4, state.WeaponDamageVolatilityF32));

            if (state == null || !state.WeaponStatsResolved)
            {
                RuntimeEvidence.LogFallbackHit(
                    "damage-volatility",
                    "unresolved-player-weapon",
                    $"class={state?.WeaponClass ?? "<null>"}",
                    64);
            }
            return NativeWeaponDamageVolatilityF32;
        }

        public static int ResolveSkillPowerLevelF32(SpellData spell, int skillLevel)
        {
            return spell?.ResolvePowerLevelF32(skillLevel) ?? 0x100;
        }

        public static int ResolveSpellCriticalThreshold(PlayerState state, Monster defender, SpellData spell)
        {
            return ResolveSpellCriticalThresholdF32(state, defender, spell?.CriticalChanceF32 ?? 0);
        }

        public static int ResolveSpellCriticalThresholdF32(PlayerState state, Monster defender, int spellCriticalChanceF32)
        {
            if (spellCriticalChanceF32 <= 0)
                return 0;

            int baseCritF32 = GCDatabase.Instance.GetRequiredKnobFixed32("HeroCriticalChance");
            int equipmentCrit = GetEquipmentStat(state, "MAGIC_CRITICAL_CHANCE", "MAGICCRITICALCHANCE");
            int threshold = FixedMul(baseCritF32 + (equipmentCrit << 8), spellCriticalChanceF32);
            if (threshold > 0x5A00) threshold = 0x5A00;
            if (threshold < 0) threshold = 0;
            return threshold;
        }

        private static int ResolveSpellDamageBonus(PlayerState state, int attackerIntellect, DamageElement damageType)
        {
            int damageTypeId = ResolveDamageTypeId(damageType);
            return ResolveUnitBaseDamageBonus(state) +
                   ResolveUnitDamageTypeBonus(state, damageTypeId) +
                   ResolveMagicDamageBonus(state, attackerIntellect);
        }

        private static int ResolveSpellDamageMod(PlayerState state, DamageElement damageType)
        {
            int damageTypeId = ResolveDamageTypeId(damageType);
            int damagePct = ResolveUnitBaseDamageModPct(state) + ResolveUnitDamageTypeModPct(state, damageTypeId);
            int damageModPct = 100 + damagePct;
            if (damageModPct < 0) damageModPct = 0;
            return ClampUShort(damageModPct);
        }

        private static int ResolveMagicDamageBonus(PlayerState state, int attackerIntellect)
        {
            int intellect = Math.Max(0, attackerIntellect);
            int statModF32 = ResolveClassDamageStatModF32(state, "SkillDamagePerIntellectMod");
            int rateF32 = GCDatabase.Instance.GetRequiredKnobFixed32("SkillDamagePerIntellect");
            int bonus = ResolvePerStatBonus(intellect, rateF32, statModF32, 0);
            bonus += GetEquipmentStat(state, "MAGIC_DAMAGE_BONUS", "MAGICDAMAGEBONUS");
            return bonus;
        }

        private static int ResolveMagicDamageModPct(PlayerState state)
        {
            int classMod = 0;
            if (state != null)
                classMod = FloorFixed32ToInt(ResolveClassDamageStatModF32(state, "MagicDamageMod"));
            int equipmentMod = GetEquipmentStat(state, "MAGIC_DAMAGE_MOD", "MAGICDAMAGEMOD", "MAGIC_DAMAGE_PCT", "MAGICDAMAGEPCT");
            int passiveMod = state != null ? FloorFixed32ToInt(state.MagicDamageModPercentF32) : 0;
            return classMod + equipmentMod + passiveMod;
        }

        private static int FloorFixed32ToInt(int valueF32)
        {
            if (valueF32 >= 0) return valueF32 >> 8;
            return -(((-valueF32) + 0xFF) >> 8);
        }

        public static void ComputeSpellDamageRange(
            PlayerState attackerState,
            int skillPowerLevelF32,
            int attackerIntellect,
            DamageElement damageType,
            int spellDamageModF32,
            int spellDamageVolatilityF32,
            out int minDamage, out int maxDamage)
        {
            int levelDamageF32 = FixedMul(GCDatabase.Instance.GetRequiredKnobFixed32("SkillDamagePerLevel"), Math.Max(0, skillPowerLevelF32));
            int dpsDamageF32 = FixedMul(levelDamageF32, GCDatabase.Instance.GetRequiredKnobFixed32("DPSModifier"));
            int damageBonus = ResolveSpellDamageBonus(attackerState, attackerIntellect, damageType);
            int damageMod = ResolveSpellDamageMod(attackerState, damageType);
            int magicDamageMod = 100 + ResolveMagicDamageModPct(attackerState);
            if (magicDamageMod < 0) magicDamageMod = 0;

            int baseDmgF32 = dpsDamageF32 + (damageBonus << 8);
            int normalized = FixedMul(baseDmgF32, spellDamageModF32);
            normalized = (int)(((long)normalized * ((long)magicDamageMod << 8)) / 0x6400L);
            normalized = (int)(((long)normalized * ((long)damageMod << 8)) / 0x6400L);

            if (normalized < 0x100) normalized = 0x100;

            int spread = FixedMul(normalized, spellDamageVolatilityF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;

            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;

            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[SPELL-DMG] DamageRange: powerF32={skillPowerLevelF32} int={attackerIntellect} levelBaseF32={levelDamageF32} bonus={damageBonus} spellModF32={spellDamageModF32} magicMod={magicDamageMod} damageMod={damageMod} volF32={spellDamageVolatilityF32} -> [{minDamage / 256},{maxDamage / 256}]");
        }

        public static void ComputeSpellDamageRange(
            int attackerLevel,
            int attackerIntellect,
            int spellDamageModF32,
            int spellDamageVolatilityF32,
            out int minDamage, out int maxDamage)
        {
            int levelComponentF32 = GCDatabase.Instance.GetRequiredKnobFixed32("SkillDamagePerLevel") * attackerLevel;
            int intellectComponentF32 = GCDatabase.Instance.GetRequiredKnobFixed32("SkillDamagePerIntellect") * attackerIntellect;
            int baseDmgF32 = levelComponentF32 + intellectComponentF32;
            int normalized = FixedMul(baseDmgF32, spellDamageModF32);
            if (normalized < 0x100) normalized = 0x100;

            int spread = FixedMul(normalized, spellDamageVolatilityF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;

            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;

            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[SPELL-DMG] DamageRange: level={attackerLevel} int={attackerIntellect} baseF32={baseDmgF32} spellModF32={spellDamageModF32} volF32={spellDamageVolatilityF32} -> [{minDamage / 256},{maxDamage / 256}]");
        }

        public static void ComputeRangedDamageRange(
            int attackerLevel,
            int attackerAgility,
            int weaponDamageF32,
            int spellDamageModF32,
            int spellDamageVolatilityF32,
            out int minDamage, out int maxDamage)
        {
            int baseDmgF32 = GCDatabase.Instance.GetRequiredKnobFixed32("WeaponDamagePerLevel") * attackerLevel
                + GCDatabase.Instance.GetRequiredKnobFixed32("RangedDamagePerAgility") * attackerAgility;
            int weaponScaled = FixedMul(baseDmgF32, weaponDamageF32);
            int normalized = FixedMul(weaponScaled, spellDamageModF32);
            if (normalized < 0x100) normalized = 0x100;

            int spread = FixedMul(normalized, spellDamageVolatilityF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;
            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[RANGED-DMG] DamageRange: level={attackerLevel} agi={attackerAgility} baseF32={baseDmgF32} weaponF32={weaponDamageF32} spellModF32={spellDamageModF32} volF32={spellDamageVolatilityF32} -> [{minDamage / 256},{maxDamage / 256}]");
        }


        public static void ComputeWeaponSkillDamageRange(
            int attackerLevel,
            int attackerStrength,
            int weaponDamageF32,
            int weaponVolatilityF32,
            int skillLevel,
            int damageModMin, int damageModMax, int damageModInc,
            out int minDamage, out int maxDamage)
        {
            int baseDmgF32 = GCDatabase.Instance.GetRequiredKnobFixed32("WeaponDamagePerLevel") * attackerLevel
                + GCDatabase.Instance.GetRequiredKnobFixed32("MeleeDamagePerStrength") * attackerStrength;
            int normalized = FixedMul(baseDmgF32, weaponDamageF32);
            if (normalized < 0x100) normalized = 0x100;

            int rawMod = damageModMin + (skillLevel * damageModInc);
            if (damageModMax > 0 && rawMod > damageModMax) rawMod = damageModMax;
            int skillModF32 = (int)(((long)(100 + rawMod) << 8) / 100L);
            normalized = FixedMul(normalized, skillModF32);
            if (normalized < 0x100) normalized = 0x100;

            int spread = FixedMul(normalized, weaponVolatilityF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;
            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[WPNSKILL-DMG] DamageRange: level={attackerLevel} str={attackerStrength} weaponF32={weaponDamageF32} skillLvl={skillLevel} mod={rawMod} skillModF32={skillModF32} -> [{minDamage / 256},{maxDamage / 256}]");
        }

        public static SpellAttackResult ProcessSpellAttack(
            MersenneTwister rng,
            int attackerLevel,
            int attackerIntellect,
            int attackerAgility,
            int attackerStrength,
            int weaponDamageF32,
            int weaponVolatilityF32,
            SpellData spell,
            Monster target,
            int skillLevel = 1,
            bool isChainTarget = false,
            int criticalDamagePercent = 200,
            PlayerState attackerState = null,
            int spellCriticalThreshold = -1,
            bool spellEffectVulnerable = false)
        {
            var result = new SpellAttackResult();
            result.Spell = spell;
            result.HitRoll = -1;

            if (spell == null || !spell.HasDirectDamageEffect)
            {
                result.Type = AttackResultType.Miss;
                Debug.LogError($"[SPELL-DMG] NO-DIRECT-DAMAGE: {spell?.DisplayName ?? "unknown"} directDamage=False modifierDamage={spell?.HasProjectileModifierDamage ?? false}");
                return result;
            }

            int chanceF32 = Math.Min(0x6400, Math.Max(0, spell.ChanceF32));
            bool consumedChanceRng = false;
            if (attackerState != null && chanceF32 < 0x6400)
            {
                uint hitRaw = RngLedger.Generate(rng, "room", $"{spell.DisplayName ?? "spell"}:SpellEffect::CheckChance", "SpellEffect");
                consumedChanceRng = true;
                result.HitRoll = (int)(hitRaw % 25700);

                if (result.HitRoll >= chanceF32)
                {
                    result.Type = AttackResultType.Miss;
                    Debug.LogError($"[SPELL-DMG] MISS: {spell.DisplayName} hitRoll={result.HitRoll} >= chance={chanceF32} (1 RNG consumed)");
                    return result;
                }
            }

            int minDmg, maxDmg;

            if (spell.IsWeaponSkill)
            {
                ComputeWeaponSkillDamageRange(
                    attackerLevel, attackerStrength,
                    weaponDamageF32, weaponVolatilityF32,
                    skillLevel,
                    spell.SkillDamageModMin, spell.SkillDamageModMax, spell.SkillDamageModInc,
                    out minDmg, out maxDmg);
            }
            else if (spell.AttackType == AttackType.RANGED)
            {
                ComputeRangedDamageRange(
                    attackerLevel, attackerAgility,
                    weaponDamageF32,
                    spell.DamageModF32, spell.DamageVolatilityF32,
                    out minDmg, out maxDmg);
            }
            else
            {
                ComputeSpellDamageRange(
                    attackerState,
                    ResolveSkillPowerLevelF32(spell, skillLevel),
                    attackerIntellect,
                    spell.DamageType,
                    spell.DamageModF32, spell.DamageVolatilityF32,
                    out minDmg, out maxDmg);
            }

            uint damageRaw = 0;
            bool consumedDamageRng = spell.IsWeaponSkill || (minDmg >> 8) != (maxDmg >> 8);
            if (consumedDamageRng)
                damageRaw = RngLedger.Generate(rng, "room", $"{spell.DisplayName ?? "spell"}:SpellEffect::damage", "SpellEffect::virtualA0");
            int damage = spell.IsWeaponSkill
                ? RollDamageRange(minDmg, maxDmg, damageRaw)
                : RollSpellDamageRange(minDmg, maxDmg, damageRaw);

            bool isCritical = spellEffectVulnerable;
            uint criticalRaw = 0;
            int criticalRoll = -1;
            int criticalThreshold = -1;
            if (spellEffectVulnerable)
            {
                if (criticalDamagePercent > 100)
                    damage = (int)(((long)damage * criticalDamagePercent) / 100L);
            }
            else
            {
                criticalThreshold = spellCriticalThreshold >= 0
                    ? spellCriticalThreshold
                    : ResolveSpellCriticalThreshold(attackerState, target, spell);
                if (!spell.IsWeaponSkill && criticalThreshold > 0)
                {
                    criticalRaw = RngLedger.Generate(rng, "room", $"{spell.DisplayName ?? "spell"}:SpellDamageEffect::critical", "SpellDamageEffect::doEffect");
                    criticalRoll = (int)(criticalRaw % 0x6464u);
                    if (criticalRoll < criticalThreshold)
                    {
                        isCritical = true;
                        damage = (int)(((long)damage * Math.Max(100, criticalDamagePercent)) / 100L);
                    }
                }
            }

            result.Type = isCritical ? AttackResultType.Critical : AttackResultType.Hit;
            result.DamageF32 = damage;
            result.MinDamageF32 = minDmg;
            result.MaxDamageF32 = maxDmg;
            result.DamageRaw = damageRaw;
            result.DamageTypeId = ResolveDamageTypeId(spell.DamageType);
            result.CritDamagePercent = Math.Max(100, criticalDamagePercent);
            result.CritRaw = criticalRaw;
            result.CritRoll = criticalRoll;

            int rngCalls = (consumedChanceRng ? 1 : 0) + (consumedDamageRng ? 1 : 0) + (criticalRoll >= 0 ? 1 : 0);
            string formulaTag = spell.IsWeaponSkill ? "WPNSKILL" : spell.AttackType == AttackType.RANGED ? "RANGED" : "MAGIC";
            string rollTag = spell.IsWeaponSkill ? "weapon-fixed" : "spell-hp";
            Debug.LogError($"[SPELL-DMG] {(isCritical ? "CRITICAL" : "HIT")}: {spell.DisplayName} [{formulaTag}] {(isChainTarget ? "CHAIN" : "PRIMARY")} dmg={damage / 256} ({damage}) range=[{minDmg / 256},{maxDmg / 256}] dmgRaw=0x{damageRaw:X8} roll={rollTag} hitRoll={result.HitRoll} critRaw=0x{criticalRaw:X8} critRoll={criticalRoll} critThreshold={criticalThreshold} ({rngCalls} RNG consumed)");
            return result;
        }

        public static SpellAttackResult ProcessProjectileModifierTick(
            MersenneTwister rng,
            int attackerLevel,
            int attackerIntellect,
            int attackerAgility,
            int attackerStrength,
            int weaponDamageF32,
            int weaponVolatilityF32,
            SpellData spell,
            Monster target,
            int skillLevel = 1,
            int criticalDamagePercent = 200,
            PlayerState attackerState = null,
            int spellCriticalThreshold = -1,
            bool spellEffectVulnerable = false,
            string rngOwner = null)
        {
            var result = new SpellAttackResult
            {
                Spell = spell,
                HitRoll = -1
            };

            if (rng == null || spell == null || target == null && spellCriticalThreshold < 0)
            {
                result.Type = AttackResultType.Miss;
                return result;
            }

            int chanceF32 = Math.Min(0x6400, Math.Max(0, spell.ProjectileModifierChanceF32));
            if (attackerState != null && chanceF32 < 0x6400)
            {
                uint chanceRaw = RngLedger.Generate(rng, "room", "modifier:SpellDamageEffect::CheckChance", rngOwner ?? "SpellModifier");
                result.HitRoll = (int)(chanceRaw % 0x6464u);
                if (result.HitRoll >= chanceF32)
                {
                    result.Type = AttackResultType.Miss;
                    Debug.LogError($"[SPELL-MOD-DMG] MISS: {spell.DisplayName} chanceRaw=0x{chanceRaw:X8} chanceRoll={result.HitRoll} chance={chanceF32}");
                    return result;
                }
            }

            int damageModF32 = spell.ProjectileModifierDamageModF32 > 0
                ? spell.ProjectileModifierDamageModF32
                : spell.DamageModF32;
            int damageVolatilityF32 = spell.ProjectileModifierDamageVolatilityF32 > 0
                ? spell.ProjectileModifierDamageVolatilityF32
                : spell.DamageVolatilityF32;
            AttackType modifierAttackType = spell.EffectiveProjectileModifierAttackType;
            DamageElement modifierDamageType = spell.EffectiveProjectileModifierDamageType;

            int minDmg;
            int maxDmg;
            if (modifierAttackType == AttackType.RANGED)
            {
                ComputeRangedDamageRange(
                    attackerLevel,
                    attackerAgility,
                    weaponDamageF32,
                    damageModF32,
                    damageVolatilityF32,
                    out minDmg,
                    out maxDmg);
            }
            else
            {
                ComputeSpellDamageRange(
                    attackerState,
                    ResolveSkillPowerLevelF32(spell, skillLevel),
                    attackerIntellect,
                    modifierDamageType,
                    damageModF32,
                    damageVolatilityF32,
                    out minDmg,
                    out maxDmg);
            }

            uint damageRaw = 0;
            if ((minDmg >> 8) != (maxDmg >> 8))
                damageRaw = RngLedger.Generate(rng, "room", "modifier:SpellEffect::damage", rngOwner ?? "SpellModifier");
            int damage = RollSpellDamageRange(minDmg, maxDmg, damageRaw);

            bool isCrit = spellEffectVulnerable;
            int critThreshold = spellCriticalThreshold >= 0
                ? spellCriticalThreshold
                : (int)Math.Min(int.MaxValue, (long)Math.Max(0, spell.ProjectileModifierCriticalChanceF32) * 100L);
            if (critThreshold > 23040) critThreshold = 23040;
            if (spellEffectVulnerable)
            {
                int critPercent = Math.Max(100, criticalDamagePercent);
                damage = (int)(((long)damage * critPercent) / 100L);
            }
            else if (critThreshold > 0)
            {
                uint critRaw = RngLedger.Generate(rng, "room", "modifier:SpellEffect::crit", rngOwner ?? "SpellModifier");
                int critRoll = (int)(critRaw % 25700);
                if (critRoll < critThreshold)
                {
                    isCrit = true;
                    int critPercent = Math.Max(100, criticalDamagePercent);
                    damage = (int)(((long)damage * critPercent) / 100L);
                }
                Debug.LogError($"[SPELL-MOD-DMG] CritRoll: {critRoll} vs threshold={critThreshold} chanceF32={spell.ProjectileModifierCriticalChanceF32} critPct={Math.Max(100, criticalDamagePercent)} -> {(isCrit ? "CRIT" : "no crit")}");
            }

            result.Type = isCrit ? AttackResultType.Critical : AttackResultType.Hit;
            result.DamageF32 = damage;
            result.MinDamageF32 = minDmg;
            result.MaxDamageF32 = maxDmg;
            result.DamageRaw = damageRaw;
            result.DamageTypeId = ResolveDamageTypeId(modifierDamageType);
            result.CritDamagePercent = Math.Max(100, criticalDamagePercent);

            Debug.LogError($"[SPELL-MOD-DMG] {(isCrit ? "CRIT" : "HIT")}: {spell.DisplayName} modifier [{spell.ProjectileModifierEffectId ?? "modifier"}] atk={modifierAttackType} dmgType={modifierDamageType} dmg={damage / 256} ({damage}) range=[{minDmg / 256},{maxDmg / 256}] dmgRaw=0x{damageRaw:X8} modF32={damageModF32} volF32={damageVolatilityF32} critPct={Math.Max(100, criticalDamagePercent)}");
            return result;
        }

    }

    public class SpellAttackResult
    {
        public AttackResultType Type;
        public SpellData Spell;
        public int DamageF32;
        public int MinDamageF32;
        public int MaxDamageF32;
        public uint DamageRaw;
        public int DamageTypeId;
        public int HitRoll;
        public int CritDamagePercent;
        public uint CritRaw;
        public int CritRoll;
    }

    public class WeaponDamageInput
    {
        public MersenneTwister Rng;
        public int AttackerLevel;
        public int DefenderLevel;
        public bool IgnoreLevelDifference;
        public int AttackRating;
        public int DefenseRating;
        public int BlockChance;
        public int DamageLevel;
        public int DamageBonus;
        public int DamageMod;
        public int WeaponClassId;
        public int DamageTypeId;
        public int WeaponDamageF32;
        public int WeaponVolatilityF32;
        public int CritThreshold;
        public int CritDamagePercent;
        public string Source;
        public PlayerState AttackerState;
        public bool IncludeWeaponDamageAdds = true;
        public uint? AttackerEntityId;
        public uint? DefenderEntityId;
    }

    public class WeaponDamageResult
    {
        public AttackResultType Type = AttackResultType.Miss;
        public string ResultName = "MISS";
        public uint HitRaw;
        public uint BlockRaw;
        public uint DamageRaw;
        public int HitRoll;
        public int BlockRoll;
        public int HitThreshold;
        public int AttackRating;
        public int DefenseRating;
        public int AttackerLevel;
        public int DefenderLevel;
        public int BlockChance;
        public int DamageLevel;
        public int DamageBonus;
        public int DamageMod;
        public int WeaponClassId;
        public int DamageTypeId;
        public int WeaponDamageF32;
        public int WeaponVolatilityF32;
        public int MinDamageF32;
        public int MaxDamageF32;
        public int DamageF32;
        public uint DamageWire;
        public int TotalDamageF32;
        public uint TotalDamageWire;
        public List<WeaponDamageEvent> DamageAdds = new List<WeaponDamageEvent>();
        public int CritThreshold;
        public int CritDamagePercent;
        public PlayerState AttackerState;
        public bool IsHit;
        public bool IsBlocked;
        public bool IsCritical;
        public int RoomRngAfter;
    }

    public class WeaponDamageAddSlot
    {
        public string Element;
        public int DamageTypeId;
        public string[] WeaponAddStats;
        public string[] DamageBonusStats;
        public string[] DamageModStats;
    }

    public class WeaponDamageEvent
    {
        public string Element;
        public int DamageTypeId;
        public int DamageF32;
        public uint DamageWire;
        public int WeaponAdd;
        public int DamageBonus;
        public int DamageMod;
        public int WeaponDamageF32;
    }

    public enum AttackResultType
    {
        Miss,
        Block,
        Hit,
        Critical
    }
}
