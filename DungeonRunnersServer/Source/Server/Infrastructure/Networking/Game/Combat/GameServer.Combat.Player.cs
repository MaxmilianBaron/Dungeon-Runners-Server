using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Core;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using DungeonRunners.Gameplay;
using DungeonRunners.Database;
using DungeonRunners.Engine.Playables;
using System.Security.Cryptography;
using DungeonRunners.Combat;
using DungeonRunners.Networking.EntitySynchInfo;
using DungeonRunners.Infrastructure;

namespace DungeonRunners.Networking
{
public partial class GameServer
    {        public PlayerState GetPlayerState(string connId)
        {
            if (!_playerStates.ContainsKey(connId))
            {
                _playerStates[connId] = new PlayerState();
            }
            return _playerStates[connId];
        }

        private static bool IsPassiveSkill(string skillGcClass)
        {
            if (string.IsNullOrEmpty(skillGcClass)) return false;
            string lower = skillGcClass.ToLowerInvariant();
            return lower.Contains("passive") || lower.Contains("trait");
        }

        internal struct PassiveManipulator
        {
            public uint Slot;
            public string Skill;
            public byte Level;
            public uint ModifierId;
        }

        private const uint PassiveModifierIdBase = 0xF000;
        private const uint PassiveModifierSlotMax = 0x0FFF;

        private static uint ResolvePassiveModifierId(uint slot)
        {
            if (slot > PassiveModifierSlotMax)
                throw new InvalidDataException($"Passive modifier slot is outside the reserved range slot={slot}");
            return PassiveModifierIdBase + slot;
        }

        internal bool IsCurrentPlayerState(string connId, PlayerState state)
        {
            return state != null
                && _playerStates.TryGetValue(connId, out PlayerState current)
                && ReferenceEquals(current, state);
        }

        internal static List<PassiveManipulator> CollectPassiveManipulators(SavedCharacter savedChar)
        {
            var result = new List<PassiveManipulator>();
            if (savedChar?.hotbarSlots == null) return result;
            var seenSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hotbarSlot in savedChar.hotbarSlots.OrderBy(h => h.slot))
            {
                if (!IsPassiveSkill(hotbarSlot.skill)) continue;
                if (!seenSkills.Add(hotbarSlot.skill)) continue;
                if (!PassiveAttributeModifiers.TryValidateRuntime(hotbarSlot.skill, out string passiveRuntimeReason))
                {
                    SkillEffectTracker.RecordPlayerGraph(0, 0, hotbarSlot.skill, "PASSIVE", 0, "rejected", passiveRuntimeReason, 0);
                    Debug.LogError($"[PASSIVE-RUNTIME] skill={hotbarSlot.skill} slot={hotbarSlot.slot} result=rejected reason={passiveRuntimeReason ?? "unsupported-passive-runtime"}");
                    continue;
                }
                int skillLevel = Math.Max(1, savedChar.GetSkillLevel(hotbarSlot.skill));
                byte level = (byte)Math.Min(byte.MaxValue, skillLevel);
                result.Add(new PassiveManipulator { Slot = hotbarSlot.slot, Skill = hotbarSlot.skill, Level = level, ModifierId = ResolvePassiveModifierId(hotbarSlot.slot) });
            }
            return result;
        }

        internal static void WriteActiveSkillManipulatorChild(LEWriter writer, string skillGcClass, uint manipulatorId, byte skillLevel)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (string.IsNullOrWhiteSpace(skillGcClass))
                throw new ArgumentException(nameof(skillGcClass));

            GCTypeCodec.WriteTypeId(writer, skillGcClass);
            writer.WriteUInt32(manipulatorId);
            writer.WriteByte(skillLevel);
            writer.WriteByte(0x00);
        }

        internal static void WritePassiveManipulatorChild(LEWriter writer, PassiveManipulator passive)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (string.IsNullOrWhiteSpace(passive.Skill))
                throw new ArgumentException(nameof(passive));

            GCTypeCodec.WriteTypeId(writer, passive.Skill);
            writer.WriteUInt32(passive.Slot);
            writer.WriteByte(passive.Level);
            writer.WriteUInt32(passive.ModifierId);
        }

        internal static void WriteEquipmentManipulatorChild(LEWriter writer, GCObject item, uint manipulatorId, int itemLevel)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            if (string.IsNullOrWhiteSpace(item.GCClass))
                throw new InvalidDataException("Equipment manipulator GCClass is required.");

            item.WriteItemData(writer, manipulatorId, 0, 0, 1, itemLevel);
            switch (item.DFCClass)
            {
                case "Armor":
                case "Item":
                    break;
                case "MeleeWeapon":
                    writer.WriteUInt16(0x0000);
                    writer.WriteByte(0x00);
                    writer.WriteUInt16(0x0000);
                    break;
                case "RangedWeapon":
                    writer.WriteUInt16(0x0000);
                    writer.WriteUInt16(0x0000);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported equipment manipulator class '{item.DFCClass}'.");
            }
        }

        internal static void WritePassiveModifiersComponent(LEWriter writer, List<PassiveManipulator> passives, ushort sourceAvatarEntityId)
        {
            var resolvedPassives = (passives ?? new List<PassiveManipulator>())
                .Select(passive => (Passive: passive, ModifierPath: PassiveAttributeModifiers.ResolveModifierPath(passive.Skill)))
                .Where(passive => !string.IsNullOrWhiteSpace(passive.ModifierPath))
                .ToList();
            uint nextModifierId = resolvedPassives.Count > 0 ? resolvedPassives.Max(p => p.Passive.ModifierId) + 1u : PassiveModifierIdBase;
            int posBefore = writer.Length;
            writer.WriteUInt32(nextModifierId);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte((byte)resolvedPassives.Count);
            foreach (var resolvedPassive in resolvedPassives)
            {
                GCTypeCodec.WriteTypeId(writer, resolvedPassive.ModifierPath);
                writer.WriteUInt32(resolvedPassive.Passive.ModifierId);
                writer.WriteByte(resolvedPassive.Passive.Level);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteByte(0x01);
            }
            foreach (var resolvedPassive in resolvedPassives)
            {
                writer.WriteByte(0x01);
            }
            if (resolvedPassives.Count > 0)
            {
                byte[] buffer = writer.GetBuffer();
                int length = buffer.Length - posBefore;
                string hex = BitConverter.ToString(buffer, posBefore, length).Replace("-", "");
                string paths = string.Join(",", resolvedPassives.Select(p => p.ModifierPath + "@0x" + GCTypeCodec.HashString(p.ModifierPath).ToString("X8") + "#" + p.Passive.ModifierId));
                Debug.LogError($"[MODIFIERS-COMPONENT] avatar={sourceAvatarEntityId} count={resolvedPassives.Count} bytes={length} paths={paths} hex={hex}");
            }
        }

        private static string NormalizeClassPassiveKey(string className)
        {
            if (string.IsNullOrWhiteSpace(className)) return "Fighter";
            string key = className.Trim();
            if (key.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 4);
            int lastDot = key.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < key.Length)
                key = key.Substring(lastDot + 1);
            if (key.Equals("Warlock", StringComparison.OrdinalIgnoreCase))
                return "Mage";
            if (key.Equals("Warrior", StringComparison.OrdinalIgnoreCase))
                return "Fighter";
            if (key.Equals("Ranger", StringComparison.OrdinalIgnoreCase))
                return "Ranger";
            if (key.Equals("Mage", StringComparison.OrdinalIgnoreCase))
                return "Mage";
            if (key.Equals("Fighter", StringComparison.OrdinalIgnoreCase))
                return "Fighter";
            return key;
        }

        private static string ResolveSavedCharacterClassPassiveKey(SavedCharacter savedChar)
        {
            if (!string.IsNullOrWhiteSpace(savedChar?.className))
                return NormalizeClassPassiveKey(savedChar.className);
            if (!string.IsNullOrWhiteSpace(savedChar?.avatarClass))
                return NormalizeClassPassiveKey(savedChar.avatarClass);
            return null;
        }

        private struct PlayerHPPreserve
        {
            public bool HasHP;
            public uint HPWire;
            public uint MaxAtCapture;
            public string Source;
            public bool FromLiveState;
            public bool FromObserved;
            public bool FromSaved;
        }

        private static PlayerHPPreserve MakePlayerHPPreserve(uint hpWire, uint maxAtCapture, string source, bool live, bool observed, bool saved)
        {
            return new PlayerHPPreserve
            {
                HasHP = hpWire > 0,
                HPWire = hpWire,
                MaxAtCapture = maxAtCapture,
                Source = source ?? "unknown",
                FromLiveState = live,
                FromObserved = observed,
                FromSaved = saved
            };
        }

        private PlayerHPPreserve CapturePlayerHPPreserve(RRConnection conn, PlayerState playerState, SavedCharacter savedChar, string phase, bool includeSavedCharacter)
        {
            PlayerHPPreserve preserve = default;
            uint maxAtCapture = playerState != null ? playerState.MaxHPWire : 0;

            if (playerState != null)
            {
                if (playerState.HasClientHP && playerState.CurrentHPWire > 0)
                    preserve = MakePlayerHPPreserve(playerState.CurrentHPWire, maxAtCapture, "live-playerstate", true, false, false);
                else if (playerState.HasEntitySynchInfoHP && playerState.EntitySynchInfoHP > 0)
                    Debug.LogError($"[HP-PRESERVE] phase={phase}-ignore-synthetic-entity-synch-info hp={playerState.EntitySynchInfoHP} maxAtCapture={maxAtCapture} source=server-entity-synch-info sourceFunction=client-mirror-only");

            }

            if (!preserve.HasHP && includeSavedCharacter && savedChar != null && savedChar.currentHP > 0)
                preserve = MakePlayerHPPreserve(savedChar.currentHP, maxAtCapture, "saved-character", false, false, true);

            Debug.LogError($"[HP-PRESERVE] phase={phase}-capture source={(preserve.HasHP ? preserve.Source : "none")} hp={preserve.HPWire} maxAtCapture={maxAtCapture} live={preserve.FromLiveState} observed={preserve.FromObserved} saved={preserve.FromSaved}");
            return preserve;
        }

        private bool ApplyPlayerHPPreserve(RRConnection conn, PlayerState playerState, PlayerHPPreserve preserve, string phase, bool applyDamageCooldown)
        {
            if (playerState == null) return false;
            uint beforeHP = playerState.CurrentHPWire;
            uint maxAfter = playerState.MaxHPWire;
            if (!preserve.HasHP)
            {
                Debug.LogError($"[HP-PRESERVE] phase={phase} source=none before={beforeHP} maxAfter={maxAfter} applied={playerState.CurrentHPWire} entitySynchInfoHP={playerState.EntitySynchInfoHP}");
                return false;
            }

            if (maxAfter > 0 && preserve.HPWire > maxAfter)
            {
                playerState.SetCurrentHPDeferClamp(preserve.HPWire);
            }
            else
            {
                uint appliedHP = maxAfter > 0 ? Math.Min(preserve.HPWire, maxAfter) : preserve.HPWire;
                playerState.SetCurrentHP(appliedHP, applyDamageCooldown && appliedHP < beforeHP);
            }
            if (conn?.Avatar != null && conn.Avatar.Id != 0)
                EntitySynchInfoAuthority.Instance.RegisterPlayer(conn, playerState, (uint)conn.Avatar.Id);
            Debug.LogError($"[HP-PRESERVE] phase={phase} source={preserve.Source} captured={preserve.HPWire} before={beforeHP} maxBefore={preserve.MaxAtCapture} maxAfter={maxAfter} applied={playerState.CurrentHPWire} entitySynchInfoHP={playerState.EntitySynchInfoHP} live={preserve.FromLiveState} observed={preserve.FromObserved} saved={preserve.FromSaved}");
            return true;
        }

        private void ApplyFullHPBaseline(RRConnection conn, PlayerState playerState, PlayerHPPreserve ignoredPreserve, string phase)
        {
            if (playerState == null) return;
            uint beforeHP = playerState.CurrentHPWire;
            uint beforeEntitySynchInfoHP = playerState.EntitySynchInfoHP;
            uint maxBefore = playerState.MaxHPWire;
            playerState.RestoreToFull();
            if (phase != null && phase.StartsWith("zone"))
            {
                uint noPassiveMax = playerState.MaxHPWireWithoutPassives;
                if (noPassiveMax > playerState.MaxHPWire)
                {
                    playerState.SetCurrentHPDeferClamp(noPassiveMax);
                    playerState.BeginPassiveMaxTransition(noPassiveMax);
                }
            }
            if (conn?.Avatar != null && conn.Avatar.Id != 0)
                EntitySynchInfoAuthority.Instance.RegisterPlayer(conn, playerState, (uint)conn.Avatar.Id);
            Debug.LogError($"[ZONE-HP-BASELINE] phase={phase} ignoredSource={(ignoredPreserve.HasHP ? ignoredPreserve.Source : "none")} ignoredHP={ignoredPreserve.HPWire} before={beforeHP}/{maxBefore} beforeEntitySynchInfoHP={beforeEntitySynchInfoHP} applied={playerState.CurrentHPWire}/{playerState.MaxHPWire} entitySynchInfoHP={playerState.EntitySynchInfoHP}");
        }

        private SavedCharacter GetActiveCharacter(RRConnection conn)
        {
            if (conn == null || conn.LoginName == null) return null;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out var gc) || gc == null) return null;
            if (_activeCharacter.TryGetValue(conn.LoginName, out var cached) && cached != null && cached.id == gc.Id)
                return cached;
            var loaded = CharacterRepository.GetCharacter(gc.Id);
            if (loaded != null) _activeCharacter[conn.LoginName] = loaded;
            return loaded;
        }

        private void InvalidateActiveCharacter(RRConnection conn)
        {
            if (conn?.LoginName != null) _activeCharacter.Remove(conn.LoginName);
        }

        private void RecalculateHotbarPassiveBonuses(string connId)
        {
            RRConnection conn = GetConnectionInsertionOrderSnapshot().FirstOrDefault(c => c.ConnId.ToString() == connId);
            if (conn == null || conn.LoginName == null || !_selectedCharacter.ContainsKey(conn.LoginName))
            {
                PlayerState defaultState = GetPlayerState(connId);
                PlayerHPPreserve defaultHP = CapturePlayerHPPreserve(conn, defaultState, null, "passive", false);
                defaultState.SetPassiveBonuses(0, 0);
                ApplyPlayerHPPreserve(conn, defaultState, defaultHP, "passive", true);
                return;
            }

            SavedCharacter savedChar = GetActiveCharacter(conn);
            RecalculateHotbarPassiveBonuses(conn, savedChar, sendModifiers: false);
        }

        private static uint ScaleWirePercentF32(uint wire, int percentF32)
        {
            if (wire == 0 || percentF32 <= 0) return 0;
            ulong scaled = (ulong)wire * (uint)percentF32 / (100u * 0x100u);
            return scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled;
        }

        private static int ClampInt64(long value)
        {
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        private void RecalculateHotbarPassiveBonuses(RRConnection conn, SavedCharacter savedChar, bool sendModifiers = true, bool keepPvpPassive = false)
        {
            List<PassiveManipulator> passives = CollectPassiveManipulators(savedChar);
            RecalculateHotbarPassiveBonuses(conn, savedChar, passives, sendModifiers, keepPvpPassive);
        }

        private void RecalculateHotbarPassiveBonuses(RRConnection conn, SavedCharacter savedChar, IReadOnlyList<PassiveManipulator> passives, bool sendModifiers, bool keepPvpPassive)
        {
            if (conn == null) return;

            if (!keepPvpPassive && IsPvpZone(conn.CurrentZoneName))
            {
                PlayerState pvpState = GetPlayerState(conn.ConnId.ToString());
                if (pvpState != null)
                {
                    uint baseHpWire = pvpState.MaxHPWireWithoutPassives;
                    uint remappedHpWire = Gameplay.PvpBalance.RemapMaxHealthWire(baseHpWire, pvpState.Level);
                    uint currentHpWire = pvpState.CurrentHPWire;
                    pvpState.SetPvpRemap(remappedHpWire);
                    pvpState.SetCurrentHP(currentHpWire);
                    Debug.LogError($"[PVP-BALANCE] {conn.LoginName}: no-passive base maxHP={baseHpWire} remappedMaxHP={pvpState.MaxHPWire} (display={pvpState.MaxHPWire / 256}) level={pvpState.Level} zone={conn.CurrentZoneName}");
                }
                return;
            }

            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            if (playerState != null && playerState.PvpRemapMaxHpWire > 0 && !IsPvpZone(conn.CurrentZoneName))
                playerState.SetPvpRemap(0);
            PlayerHPPreserve hpPreserve = CapturePlayerHPPreserve(conn, playerState, savedChar, "passive", false);
            int hpWireBonus = 0;
            int manaWireBonus = 0;
            int strengthMod = 0;
            int agilityMod = 0;
            int enduranceMod = 0;
            int intellectMod = 0;
            int meleeAttackRatingModPercent = 0;
            int meleeAttackSpeedModPercentF32 = 0;
            int rangeAttackSpeedModPercentF32 = 0;
            int magicDamageModPercentF32 = 0;
            int healthPerEnduranceModPercent = 0;
            int manaPerIntellectModPercent = 0;
            int healthModF32 = 0;
            int divineDamageResist = 0;
            int fireDamageResist = 0;
            int iceDamageResist = 0;
            int poisonDamageResist = 0;
            int shadowDamageResist = 0;
            IReadOnlyList<PassiveManipulator> passiveSnapshot = passives ?? Array.Empty<PassiveManipulator>();

            if (passiveSnapshot.Count > 0)
            {
                var passiveLevels = passiveSnapshot
                    .Select(passive => (Skill: passive.Skill, Level: Math.Max(1, (int)passive.Level)))
                    .ToList();
                PassiveAttributeTotals totals = PassiveAttributeModifiers.Resolve(passiveLevels);
                strengthMod = totals.Strength;
                agilityMod = totals.Agility;
                enduranceMod = totals.Endurance;
                intellectMod = totals.Intellect;
                healthPerEnduranceModPercent = totals.HealthPerEnduranceMod;
                manaPerIntellectModPercent = totals.ManaPerIntellectMod;
                healthModF32 = totals.HealthModF32;
                divineDamageResist = totals.DivineDamageResist;
                fireDamageResist = totals.FireDamageResist;
                iceDamageResist = totals.IceDamageResist;
                poisonDamageResist = totals.PoisonDamageResist;
                shadowDamageResist = totals.ShadowDamageResist;
                meleeAttackRatingModPercent = totals.MeleeAttackRatingMod;
                meleeAttackSpeedModPercentF32 = totals.MeleeAttackSpeedModF32;
                rangeAttackSpeedModPercentF32 = totals.RangeAttackSpeedModF32;
                magicDamageModPercentF32 = totals.MagicDamageModF32;

                int baseEndurance = 10 + Math.Max(0, playerState.AllocatedEndurance);
                int passiveEndurance = Math.Max(1, baseEndurance + enduranceMod);
                uint noPassiveHP = ClassPassiveData.CalculateHPWire(playerState.Level, baseEndurance, 0);
                uint passiveHP = ClassPassiveData.CalculateHPWire(playerState.Level, passiveEndurance, healthPerEnduranceModPercent);
                passiveHP = ScaleWirePercentF32(passiveHP, 100 * 0x100 + totals.HealthModF32);
                hpWireBonus = ClampInt64((long)passiveHP - noPassiveHP);

                int baseIntellect = 10 + Math.Max(0, playerState.AllocatedIntellect);
                int passiveIntellect = Math.Max(1, baseIntellect + intellectMod);
                uint noPassiveMana = ClassPassiveData.CalculateManaWire(playerState.Level, baseIntellect, 0);
                uint passiveMana = ClassPassiveData.CalculateManaWire(playerState.Level, passiveIntellect, manaPerIntellectModPercent);
                manaWireBonus = ClampInt64((long)passiveMana - noPassiveMana);
            }

            uint oldMaxWire = playerState.MaxHPWire;
            playerState.SetPassiveBonuses(hpWireBonus, manaWireBonus, meleeAttackRatingModPercent, meleeAttackSpeedModPercentF32, rangeAttackSpeedModPercentF32, strengthMod, agilityMod, enduranceMod, intellectMod, healthPerEnduranceModPercent, manaPerIntellectModPercent, magicDamageModPercentF32, healthModF32, divineDamageResist, fireDamageResist, iceDamageResist, poisonDamageResist, shadowDamageResist);
            ApplyPlayerHPPreserve(conn, playerState, hpPreserve, "passive", true);
            if (playerState.MaxHPWire < oldMaxWire && playerState.CurrentHPWire > playerState.MaxHPWire)
                playerState.BeginPassiveMaxTransition(oldMaxWire);

            if (sendModifiers)
                SendPassiveModifiers(conn, savedChar);
        }

        private void SendPassiveModifiers(RRConnection conn, SavedCharacter savedChar)
        {
            if (conn.ModifiersId == 0 || conn.Avatar == null) return;
            var passives = CollectPassiveManipulators(savedChar)
                .Select(passive => (Passive: passive, ModifierPath: PassiveAttributeModifiers.ResolveModifierPath(passive.Skill)))
                .Where(passive => !string.IsNullOrWhiteSpace(passive.ModifierPath))
                .ToList();
            foreach (var resolved in passives)
            {
                if (!conn.SentPassiveModifierIds.Add(resolved.Passive.ModifierId))
                    continue;
                var writer = new LEWriter();
                writer.WriteByte(0x07);
                writer.WriteByte(0x35);
                writer.WriteUInt16((ushort)conn.ModifiersId);
                writer.WriteByte(0x00);
                GCTypeCodec.WriteTypeId(writer, resolved.ModifierPath);
                writer.WriteUInt32(resolved.Passive.ModifierId);
                writer.WriteByte(resolved.Passive.Level);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteByte(0x01);
                if (!TryWriteEntitySynchForComponent(conn, writer, (ushort)conn.ModifiersId, 0x00, EntitySynchInfoContext.PlayerActionResponse, "SendPassiveModifiers"))
                    continue;
                writer.WriteByte(0x06);
                SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            }
        }

        private static int ResolveEquipmentItemModLevel(GCObject item, string gcClass, int rarity)
        {
            int itemLevel = item != null && item.StoredLevel >= 0
                ? item.StoredLevel
                : RPGSettings.GetItemLevel(gcClass);
            return RPGSettings.GetEquipRequiredLevel(Math.Max(1, itemLevel), (ItemRarity)rarity);
        }

        private static bool TryResolveClientVisibleEquipmentStats(
            GCObject item,
            string gcClass,
            DungeonRunners.Data.ItemStatDatabase itemStatDb,
            out int authoredModCount,
            out int resolvedModCount,
            out string reason)
        {
            authoredModCount = 0;
            resolvedModCount = 0;
            reason = "missing-item-stat-db";
            return itemStatDb != null
                && itemStatDb.TryResolveItemModifierCoverage(item, out authoredModCount, out resolvedModCount, out reason);
        }

        private bool TryResolveWeaponDescIds(
            string source,
            string weaponPath,
            string weaponClass,
            string damageType,
            out int weaponClassId,
            out int damageTypeId)
        {
            bool classOk = DamageResolver.TryResolveWeaponClassId(weaponClass, out weaponClassId);
            bool typeOk = DamageResolver.TryResolveDamageTypeId(damageType, out damageTypeId);
            if (classOk && typeOk)
                return true;

            RuntimeEvidence.LogFallbackHit(
                "damage-weapon-desc",
                "unresolved-client-id",
                $"source={source ?? "<null>"} weapon={weaponPath ?? "<null>"} weaponClass={weaponClass ?? "<null>"} damageType={damageType ?? "<null>"} classOk={classOk} typeOk={typeOk} sourceFunction=Weapon::ComputeAttributes-blocked",
                64);
            return false;
        }

        public void CalculateEquipmentBonuses(string connId, GCObject avatar, IReadOnlyDictionary<uint, GCObject> equipmentSnapshot)
        {
            PlayerState playerState = GetPlayerState(connId);
            RRConnection hpConn = GetConnectionInsertionOrderSnapshot().FirstOrDefault(c => c.ConnId.ToString() == connId);
            SavedCharacter hpSavedChar = null;
            if (hpConn != null && hpConn.LoginName != null && _selectedCharacter.ContainsKey(hpConn.LoginName))
                hpSavedChar = GetActiveCharacter(hpConn);
            PlayerHPPreserve hpPreserve = CapturePlayerHPPreserve(hpConn, playerState, hpSavedChar, "equip", false);
            bool preserveMana = playerState.HasClientMana;
            uint manaToPreserveWire = playerState.CurrentManaWire;
            playerState.ClearEquipmentBonuses();

            int bestWeaponDamageF32 = 203;
            int bestWeaponVolatilityF32 = 0x40;
            int bestWeaponLevel = 1;
            GCObject bestWeaponItem = null;
            string bestWeaponClass = "";
            string bestWeaponDamageType = "";
            string bestWeaponCategory = "";
            int bestWeaponClassId = 0;
            int bestDamageTypeId = -1;
            int bestWeaponRange = 0;
            int bestWeaponRangeF32 = 0;
            int bestWeaponInitUseRangeF32 = 64000;
            int bestWeaponClientSyncToleranceF32 = 0;
            int bestWeaponCooldownF32 = 0;
            int bestWeaponSpeedF32 = 100 * 0x100;
            bool bestWeaponUsesProjectile = false;
            int bestWeaponShotType = 0;
            int bestWeaponProjectileSpeedF32 = 0;
            int bestWeaponProjectileSizeF32 = 0;
            int bestWeaponBurstCount = 1;
            uint bestWeaponEquipmentSlot = 0;
            int bestWeaponStunMod = 100;
            int equipmentSpeedMod = 0;
            bool foundWeapon = false;

            if (avatar == null) { Debug.LogError("[EQUIP-STATS] avatar is NULL"); return; }
            var equipment = avatar.Children?.FirstOrDefault(c => c.GCClass == "avatar.base.Equipment");

            var allItems = new List<(uint Slot, GCObject Item)>();
            bool usedTrackedItems = false;
            if (equipmentSnapshot != null)
            {
                usedTrackedItems = true;
                foreach (var kv in equipmentSnapshot)
                {
                    if (kv.Value?.GCClass != null)
                        allItems.Add((kv.Key, kv.Value));
                }
            }
            else if (_playerEquippedItems.TryGetValue(connId, out var tracked) && tracked != null && tracked.Count > 0)
            {
                var trackedSlots = tracked.Keys.ToList();
                trackedSlots.Sort();
                foreach (uint trackedSlot in trackedSlots)
                    if (tracked.TryGetValue(trackedSlot, out GCObject trackedItem) && trackedItem?.GCClass != null)
                        allItems.Add((trackedSlot, trackedItem));
                usedTrackedItems = allItems.Count > 0;
                Debug.LogError($"[EQUIP-STATS] Using {allItems.Count}/{tracked.Count} TRACKED items (authoritative)");
            }
            if (!usedTrackedItems && equipment?.Children != null)
            {
                foreach (var child in equipment.Children)
                    allItems.Add((child.TargetSlot ?? child.GetEquipmentSlotFromGCClass(), child));
                Debug.LogError($"[EQUIP-STATS] Using {allItems.Count} AVATAR children (runtime equipment fallback)");
            }

            Debug.LogError($"[EQUIP-STATS] Processing {allItems.Count} equipped items, DB loaded={DungeonRunners.Data.ItemStatDatabase.Instance.IsLoaded}");

            var itemStatDb = DungeonRunners.Data.ItemStatDatabase.Instance;

            var orderedItems = allItems.ToList();
            orderedItems.Sort((left, right) =>
            {
                int slotCompare = left.Slot.CompareTo(right.Slot);
                return slotCompare != 0 ? slotCompare : StringComparer.OrdinalIgnoreCase.Compare(left.Item.GCClass ?? "", right.Item.GCClass ?? "");
            });
            foreach (var itemEntry in orderedItems)
            {
                GCObject item = itemEntry.Item;
                uint itemSlot = itemEntry.Slot;
                string gc = item.GCClass ?? "";
                int rarity = item.GetEffectiveRarity();
                bool isWeapon = item.DFCClass == "MeleeWeapon" || item.DFCClass == "RangedWeapon";
                bool isArmor = item.DFCClass == "Armor";
                bool isEquippableItem = isWeapon || isArmor || item.DFCClass == "Item";
                if (isArmor)
                {
                    int armorDefenseF32 = GCDatabase.Instance.GetArmorDefenseRatingF32(gc);
                    if (armorDefenseF32 > 0)
                    {
                        int armorLevel = RPGSettings.ResolveNativeItemAttributeLevel(item, playerState.Level);
                        int itemDefensePerLevelF32 = GCDatabase.Instance.GetRequiredKnobFixed32("ItemDefenseRatingPerLevel");
                        long defenseFloor = ((long)itemDefensePerLevelF32 * armorDefenseF32 * armorLevel) >> 16;
                        int defenseRating = defenseFloor >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)defenseFloor + 1);
                        playerState.AddArmorDefenseRating(defenseRating);
                        if (playerState.EquipmentStats.ContainsKey("DEFENSE_RATING"))
                            playerState.EquipmentStats["DEFENSE_RATING"] += defenseRating;
                        else
                            playerState.EquipmentStats["DEFENSE_RATING"] = defenseRating;
                        Debug.LogError($"[EQUIP-ARMOR] {gc}: level={armorLevel} armorDefenseF32={armorDefenseF32} defenseRating={defenseRating}");
                    }
                }

                if (isEquippableItem && itemStatDb.IsLoaded)
                {
                    if (TryResolveClientVisibleEquipmentStats(item, gc, itemStatDb, out int authoredModCount, out int resolvedModCount, out string visibilityReason))
                    {
                        int itemModLevel = ResolveEquipmentItemModLevel(item, gc, rarity);
                        var stats = itemStatDb.GetItemStatsAtItemLevel(item, itemModLevel);
                        Debug.LogError($"[EQUIP-ITEM] {gc} rarity={rarity} itemLevel={itemModLevel} clientVisible=True mods={resolvedModCount}/{authoredModCount} source={visibilityReason} stats={stats.Count} attributes=[{string.Join(", ", stats.Keys)}]");

                        stats.TryGetValue("MAX_HIT_POINTS", out int hpBonus);
                        stats.TryGetValue("STRENGTH", out int strengthBonus);
                        stats.TryGetValue("AGILITY", out int agilityBonus);
                        stats.TryGetValue("ENDURANCE", out int endBonus);
                        stats.TryGetValue("MAX_MANA_POINTS", out int manaBonus);
                        stats.TryGetValue("INTELLECT", out int intBonus);
                        stats.TryGetValue("SPEEDMOD", out int generatedSpeedMod);

                        if (hpBonus > 0) playerState.AddTotalHealthBonus(hpBonus);
                        if (strengthBonus != 0) playerState.AddStrengthBonus(strengthBonus);
                        if (agilityBonus != 0) playerState.AddAgilityBonus(agilityBonus);
                        if (endBonus > 0) playerState.AddEnduranceBonus(endBonus);
                        if (manaBonus > 0) playerState.AddManaBonus(manaBonus);
                        if (intBonus > 0) playerState.AddIntellectManaBonus(intBonus);
                        equipmentSpeedMod = checked(equipmentSpeedMod + generatedSpeedMod);

                        var orderedStats = stats.ToList();
                        orderedStats.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
                        foreach (var statEntry in orderedStats)
                        {
                            if (playerState.EquipmentStats.ContainsKey(statEntry.Key))
                                playerState.EquipmentStats[statEntry.Key] += statEntry.Value;
                            else
                                playerState.EquipmentStats[statEntry.Key] = statEntry.Value;
                        }

                        if (hpBonus > 0 || strengthBonus != 0 || agilityBonus != 0 || endBonus > 0 || manaBonus > 0 || intBonus > 0)
                            Debug.LogError($"[EQUIP-STATS] {gc}: HP+{hpBonus} STR+{strengthBonus} AGI+{agilityBonus} END+{endBonus} MANA+{manaBonus} INT+{intBonus}");
                    }
                    else
                    {
                        Debug.LogError($"[EQUIP-ITEM] {gc} -> DB stats ignored clientVisible=False mods={resolvedModCount}/{authoredModCount} reason={visibilityReason} sourceFunction=Item::writeInit+ItemModifier::AddModifiers@0x00588890");
                    }
                }
                if (item.DFCClass == "MeleeWeapon" || item.DFCClass == "RangedWeapon")
                {
                    var weaponData = AuthoredGameplayCatalog.FindItem(gc);
                    var weaponNode = GCDatabase.Instance.ResolveWithInheritance(gc);
                    GCDatabase.WeaponStatsFixed weaponStats = default;
                    if (weaponNode == null)
                    {
                        RuntimeEvidence.LogFallbackHit("damage-weapon-desc", "missing-gc-node", $"source=equip weapon={gc} sourceFunction=Weapon::ComputeAttributes-return", 64);
                        continue;
                    }

                    weaponStats = GCDatabase.Instance.GetWeaponStatsFixed(gc);
                    int authoredWeaponDamageF32 = weaponStats.DamageF32 > 0 ? weaponStats.DamageF32 : 0;
                    int authoredWeaponVolatilityF32 = weaponStats.VolatilityF32 > 0
                        ? Math.Min(0xF4, weaponStats.VolatilityF32)
                        : 0x40;
                    if (authoredWeaponDamageF32 > 0)
                    {
                        string resolvedWeaponClass = !string.IsNullOrEmpty(weaponStats.WeaponClass) ? weaponStats.WeaponClass : weaponData != null && !string.IsNullOrEmpty(weaponData.weaponClass) ? weaponData.weaponClass : "";
                        string resolvedWeaponDamageType = !string.IsNullOrEmpty(weaponStats.DamageType) ? weaponStats.DamageType : "";
                        if (!TryResolveWeaponDescIds("equip", gc, resolvedWeaponClass, resolvedWeaponDamageType, out int clientWeaponClassId, out int clientDamageTypeId))
                            continue;

                        bestWeaponDamageF32 = authoredWeaponDamageF32;
                        bestWeaponVolatilityF32 = authoredWeaponVolatilityF32;
                        bestWeaponLevel = Math.Max(1, item.StoredLevel >= 0 ? item.StoredLevel : DungeonRunners.Gameplay.RPGSettings.GetItemLevel(gc));
                        bestWeaponItem = item;
                        bestWeaponClass = resolvedWeaponClass;
                        bestWeaponDamageType = resolvedWeaponDamageType;
                        bestWeaponCategory = !string.IsNullOrEmpty(weaponStats.WeaponCategory) ? weaponStats.WeaponCategory : "";
                        bestWeaponClassId = clientWeaponClassId;
                        bestDamageTypeId = clientDamageTypeId;
                        bestWeaponRange = weaponStats.RangeF32 > 0 ? weaponStats.RangeRoundedUnits : weaponData != null && weaponData.range > 0 ? weaponData.range : 0;
                        bestWeaponRangeF32 = weaponStats.RangeF32 > 0 ? weaponStats.RangeF32 : bestWeaponRange * 0x100;
                        bestWeaponInitUseRangeF32 = weaponStats.InitUseRangeF32;
                        bestWeaponClientSyncToleranceF32 = weaponStats.ClientSyncToleranceF32;
                        bestWeaponCooldownF32 = Math.Max(0, weaponStats.CooldownF32);
                        bestWeaponSpeedF32 = weaponStats.WeaponSpeedF32 > 0 ? weaponStats.WeaponSpeedF32 : 100 * 0x100;
                        bestWeaponUsesProjectile = weaponStats.UseProjectile;
                        bestWeaponShotType = weaponStats.ShotType;
                        bestWeaponProjectileSpeedF32 = weaponStats.ProjectileSpeedF32;
                        bestWeaponProjectileSizeF32 = weaponStats.ProjectileSizeF32;
                        bestWeaponBurstCount = Math.Max(1, weaponStats.BurstCount);
                        bestWeaponEquipmentSlot = itemSlot;
                        bestWeaponStunMod = Math.Max(0, weaponStats.StunMod);
                        foundWeapon = true;
                        Debug.LogError($"[EQUIP-WEAPON] {gc}: damageF32={bestWeaponDamageF32} volatilityF32={bestWeaponVolatilityF32} level={bestWeaponLevel} class={bestWeaponClass} damageType={bestWeaponDamageType} category={bestWeaponCategory} range={bestWeaponRange} cooldownF32={bestWeaponCooldownF32} speedF32={bestWeaponSpeedF32} useProjectile={bestWeaponUsesProjectile} projectileSpeedF32={bestWeaponProjectileSpeedF32} projectileSizeF32={bestWeaponProjectileSizeF32} burst={bestWeaponBurstCount}");
                    }
                }
            }
            string avatarGcClass = hpSavedChar?.avatarClass;
            if (string.IsNullOrWhiteSpace(avatarGcClass))
                avatarGcClass = avatar.GCClass;
            GCNode avatarNode = GCDatabase.Instance.ResolveWithInheritance(avatarGcClass);
            GCNode avatarDescription = avatarNode?.GetChild("Description") ?? avatarNode;
            if (avatarDescription == null)
                throw new InvalidDataException($"Avatar description missing for movement attributes path='{avatarGcClass}'");
            GCNode globalKnobs = GCDatabase.Instance.GlobalKnobs;
            if (globalKnobs == null || !globalKnobs.HasProperty("MovementSpeedModifier"))
                throw new InvalidDataException("GlobalKnobs missing client RPGSettings field MovementSpeedModifier");
            int playerSpeedF32 = avatarDescription.GetFixed32("Speed", 50 * 0x100);
            int descriptorSpeedMod = avatarDescription.GetInt("SpeedMod", 100);
            int globalSpeedMod = globalKnobs.GetInt("MovementSpeedModifier");
            int playerSpeedMod = checked(descriptorSpeedMod + globalSpeedMod + equipmentSpeedMod);
            playerState.SetMovementSpeed(playerSpeedF32, playerSpeedMod);
            Debug.LogError($"[EQUIP-SPEED] avatar={avatarGcClass} speedF32={playerSpeedF32} descriptorSpeedMod={descriptorSpeedMod} globalSpeedMod={globalSpeedMod} equipmentSpeedMod={equipmentSpeedMod} speedMod={playerSpeedMod} sourceFunction=Unit::computeAttributes@0x00509960 ItemAttributeModifier::doEvent@0x00584270");
            if (!globalKnobs.HasProperty("HeroAttackSpeed"))
                throw new InvalidDataException("GlobalKnobs missing client RPGSettings field HeroAttackSpeed");
            int descriptorAttackSpeedF32 = avatarDescription.GetFixed32("AttackSpeed", 0x100);
            int heroAttackSpeed = globalKnobs.GetInt("HeroAttackSpeed");
            int attackSpeed = unchecked((ushort)(((long)descriptorAttackSpeedF32 * heroAttackSpeed) >> 8));
            playerState.EquipmentStats.TryGetValue("CAST_SPEED_MOD", out int castSpeedMod);
            playerState.SetActiveSkillSpeedAttributes(attackSpeed, castSpeedMod);
            Debug.LogError($"[EQUIP-ACTIVE-SKILL-SPEED] avatar={avatarGcClass} descriptorAttackSpeedF32={descriptorAttackSpeedF32} heroAttackSpeed={heroAttackSpeed} attackSpeed={playerState.AttackSpeed} castSpeedMod={playerState.CastSpeedMod} sourceFunction=UnitDesc::getAttackSpeed@0x0050FA20->Unit::computeAttributes@0x00509960->ActiveSkill::computeSpeedMod@0x0053A020");
            RecalculateHotbarPassiveBonuses(connId);
            playerState.RecalculateCurrentHP();
            ApplyPlayerHPPreserve(hpConn, playerState, hpPreserve, "equip", true);

            if (preserveMana)
                playerState.SetCurrentManaDeferClamp(manaToPreserveWire);
            else
                playerState.SetCurrentMana(playerState.MaxManaWire, "equip-init", false);

            Debug.LogError($"[EQUIP-TOTAL] MaxHP={playerState.MaxHPWire / 256} MaxMana={playerState.MaxManaWire / 256} EquipStats={playerState.EquipmentStats.Count}");

            if (foundWeapon)
            {
                playerState.WeaponDamageF32 = bestWeaponDamageF32;
                playerState.WeaponDamageVolatilityF32 = bestWeaponVolatilityF32;
                playerState.WeaponLevel = bestWeaponLevel;
                playerState.WeaponClass = bestWeaponClass;
                playerState.WeaponDamageType = bestWeaponDamageType;
                playerState.WeaponCategory = bestWeaponCategory;
                playerState.WeaponStatsResolved = true;
                playerState.WeaponClassId = bestWeaponClassId;
                playerState.DamageTypeId = bestDamageTypeId;
                DamageResolver.ApplyWeaponRuntimeBaseDamage(playerState, bestWeaponItem, playerState.Level, "equip");
                playerState.WeaponRange = bestWeaponRange;
                playerState.WeaponRangeF32 = bestWeaponRangeF32;
                playerState.WeaponInitUseRangeF32 = bestWeaponInitUseRangeF32;
                playerState.WeaponClientSyncToleranceF32 = bestWeaponClientSyncToleranceF32;
                playerState.WeaponCooldownF32 = bestWeaponCooldownF32;
                playerState.WeaponSpeedF32 = bestWeaponSpeedF32;
                playerState.WeaponUsesProjectile = bestWeaponUsesProjectile;
                playerState.WeaponShotType = bestWeaponShotType;
                playerState.WeaponProjectileSpeedF32 = bestWeaponProjectileSpeedF32;
                playerState.WeaponProjectileSizeF32 = bestWeaponProjectileSizeF32;
                playerState.WeaponBurstCount = bestWeaponBurstCount;
                playerState.WeaponEquipmentSlot = bestWeaponEquipmentSlot;
                playerState.WeaponStunMod = bestWeaponStunMod;
                Debug.LogError($"[EQUIP-WEAPON] PlayerState updated: damageF32={bestWeaponDamageF32} volatilityF32={bestWeaponVolatilityF32} level={bestWeaponLevel} clientDamageLevel={playerState.WeaponDamageLevel} clientBaseDamage={playerState.WeaponBaseDamage} clientBaseSource={playerState.WeaponBaseDamageSource} class={bestWeaponClass}/{playerState.WeaponClassId} damageType={bestWeaponDamageType}/{playerState.DamageTypeId} category={bestWeaponCategory} cooldownF32={bestWeaponCooldownF32} speedF32={bestWeaponSpeedF32} useProjectile={bestWeaponUsesProjectile} projectileSpeedF32={bestWeaponProjectileSpeedF32} projectileSizeF32={bestWeaponProjectileSizeF32} burst={bestWeaponBurstCount}");
            }
        }
        public void CalculateEquipmentBonuses(string connId, GCObject avatar)
        {
            CalculateEquipmentBonuses(connId, avatar, null);
        }

        public Dictionary<uint, GCObject> GetAllEquippedItems(string connId)
        {
            var items = new Dictionary<uint, GCObject>();

            uint[] slots = { 1, 2, 3, 4, 5, 6, 7, 8, 10, 11 };

            foreach (uint slot in slots)
            {
                GCObject item = GetEquippedItem(connId, slot);
                if (item != null)
                {
                    items[slot] = item;
                }
            }

            return items;
        }



        private int EstimateItemLevel(string gcClass, int playerLevel)
        {
            for (int charIndex = gcClass.Length - 1; charIndex >= 0; charIndex--)
            {
                if (char.IsDigit(gcClass[charIndex]))
                {
                    int digitStartIndex = charIndex;
                    while (digitStartIndex > 0 && char.IsDigit(gcClass[digitStartIndex - 1]))
                        digitStartIndex--;

                    string tierStr = gcClass.Substring(digitStartIndex, charIndex - digitStartIndex + 1);
                    if (int.TryParse(tierStr, out int tier))
                        return Math.Min(tier * 10, playerLevel);
                }
            }
            return playerLevel / 2;
        }






        private string GetComponentType(string connId, ushort componentId)
        {
            if (_playerComponentTypes.ContainsKey(connId) && _playerComponentTypes[connId].ContainsKey(componentId))
            {
                return _playerComponentTypes[connId][componentId];
            }
            return "Unknown";
        }

        private void TrackComponent(string connId, ushort componentId, string componentType)
        {
            if (!_playerComponentTypes.ContainsKey(connId))
            {
                _playerComponentTypes[connId] = new Dictionary<ushort, string>();
            }
            _playerComponentTypes[connId][componentId] = componentType;
            if (string.Equals(componentType, "UnitContainer", StringComparison.Ordinal))
                _playerUnitContainerComponentIds[connId] = componentId;
            Debug.LogError($"[COMPONENT-TRACK] Player {connId}: ComponentID 0x{componentId:X4} = {componentType}");
        }

        public void TrackEquippedItem(string connId, uint slot, GCObject item)
        {
            if (!_playerEquippedItems.ContainsKey(connId))
            {
                _playerEquippedItems[connId] = new Dictionary<uint, GCObject>();
            }
            _playerEquippedItems[connId][slot] = item;
            Debug.LogError($"[EQUIP-TRACK] Player {connId}: Slot {slot} = {item.GCClass}");
        }



        public bool WritePlayerEntitySynch(RRConnection conn, LEWriter writer)
        {
            return TryWritePlayerEntitySynch(conn, writer, EntitySynchInfoContext.PlayerActionResponse, "WritePlayerEntitySynch");
        }

        public bool WritePlayerEntitySynch(RRConnection conn, LEWriter writer, EntitySynchInfoContext context)
        {
            return TryWritePlayerEntitySynch(conn, writer, context, context.ToString());
        }

        private bool WritePlayerEntitySynchNoFlush(RRConnection conn, LEWriter writer)
        {
            return TryWritePlayerEntitySynch(conn, writer, EntitySynchInfoContext.PlayerActionResponse, "WritePlayerEntitySynchNoFlush");
        }

        private bool WritePlayerEntitySynchNoCombatFlush(RRConnection conn, LEWriter writer)
        {
            return TryWritePlayerEntitySynch(conn, writer, EntitySynchInfoContext.PlayerActionResponse, "WritePlayerEntitySynchNoCombatFlush");
        }

        private bool TryWritePlayerEntitySynch(RRConnection conn, LEWriter writer, EntitySynchInfoContext context, string packetName)
        {
            if (writer == null) return false;

            if (TryResolveWriterComponentUpdate(conn, writer, out ushort componentId, out byte subtype))
                return TryWriteEntitySynchForComponent(conn, writer, componentId, subtype, context, packetName);

            if (!TryResolvePlayerEntitySynchInfoHP(conn, context, packetName, out uint hpWire))
            {
                Debug.LogError($"[ENTITY-SYNCH-INFO-UNRESOLVED] packet={packetName} context={context} owner=Avatar reason=player-hp-unresolved");
                return false;
            }

            GetValidationCutoff(out uint fallbackCutoffTick);
            string fallbackRuntimeKey = conn != null ? GetInstanceZoneKey(conn) : null;
            int fallbackRngPos = !string.IsNullOrWhiteSpace(fallbackRuntimeKey) ? CombatRuntime.Instance.GetRoomRngPosForInstance(fallbackRuntimeKey) : -1;
            return TryWriteResolvedEntitySynchInfo(writer, 0, 0, context, packetName, EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, hpWire, packetName, conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, 0, $"player-fallback; validationCutoffTick={fallbackCutoffTick}", fallbackCutoffTick, fallbackRuntimeKey, conn?.EntitySchedulerMirror?.SchedulerTick ?? 0, conn?.EntitySchedulerMirror?.SubEntityPhase ?? false, fallbackRngPos));
        }

        private bool TryWriteCapturedPlayerEntitySynchInfo(RRConnection conn, LEWriter writer, ushort ownerAvatarId, uint capturedHPWire, string packetName)
        {
            if (conn == null || writer == null || ownerAvatarId == 0)
                return false;

            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            if (playerState == null)
                return false;

            EntitySynchInfoAuthority.Instance.RecordPlayerOutboundHP(conn, playerState, ownerAvatarId, capturedHPWire, $"{packetName}-captured");
            GetValidationCutoff(out uint validationCutoffTick);
            string runtimeKey = GetInstanceZoneKey(conn);
            int rngPos = !string.IsNullOrWhiteSpace(runtimeKey) ? CombatRuntime.Instance.GetRoomRngPosForInstance(runtimeKey) : -1;
            EntitySynchInfoDecision decision = EntitySynchInfoDecision.HP(
                EntitySynchInfoOwner.Avatar,
                capturedHPWire,
                $"{packetName} captured-hp",
                ownerAvatarId,
                0,
                0,
                $"captured-stat-update-hp; validationCutoffTick={validationCutoffTick}",
                validationCutoffTick,
                runtimeKey,
                conn.EntitySchedulerMirror?.SchedulerTick ?? 0,
                conn.EntitySchedulerMirror?.SubEntityPhase ?? false,
                rngPos,
                "stat-spend-captured-hp");
            return TryWriteResolvedEntitySynchInfo(writer, 0, 0, EntitySynchInfoContext.PlayerActionResponse, packetName, decision, conn);
        }

        private bool TryWriteEntitySynchForComponent(RRConnection conn, LEWriter writer, ushort componentId, byte subtype, string tag)
        {
            return TryWriteEntitySynchForComponent(conn, writer, componentId, subtype, EntitySynchInfoContextFromTag(tag), tag);
        }

        private static uint ResolveAuthoredUnitMaxHealthWire(string gcType, uint fallbackHPWire = NonCombatInteractiveHPWire)
        {
            if (string.IsNullOrEmpty(gcType) || GCDatabase.Instance == null || !GCDatabase.Instance.IsLoaded)
                return fallbackHPWire;

            var node = GCDatabase.Instance.ResolveWithInheritance(gcType);
            var desc = node?.GetChild("Description") ?? node;
            if (desc == null || !desc.HasProperty("MaxHealth"))
                return fallbackHPWire;

            int maxHealthWire = desc.GetFixed32("MaxHealth", fallbackHPWire > int.MaxValue ? int.MaxValue : (int)fallbackHPWire);
            if (maxHealthWire <= 0)
                return fallbackHPWire;

            return (uint)maxHealthWire;
        }

        private static bool AuthoredExtendsClass(string gcType, string dfcClass)
        {
            if (string.IsNullOrEmpty(gcType) || string.IsNullOrEmpty(dfcClass) ||
                GCDatabase.Instance == null || !GCDatabase.Instance.IsLoaded)
                return false;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = gcType;
            for (int depth = 0; depth < 32 && !string.IsNullOrEmpty(current); depth++)
            {
                if (!seen.Add(current)) return false;
                var node = GCDatabase.Instance.Resolve(current);
                if (node == null) return false;
                if (AuthoredNameMatches(node.Name, dfcClass) || AuthoredNameMatches(node.Extends, dfcClass))
                    return true;
                current = node.Extends;
            }

            return false;
        }

        private static bool AuthoredNameMatches(string authoredName, string dfcClass)
        {
            if (string.IsNullOrEmpty(authoredName)) return false;
            if (string.Equals(authoredName, dfcClass, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = authoredName.LastIndexOf('.');
            return dot >= 0 &&
                string.Equals(authoredName.Substring(dot + 1), dfcClass, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveAuthoredItemClass(string gcType)
        {
            if (AuthoredExtendsClass(gcType, "RangedWeapon")) return "RangedWeapon";
            if (AuthoredExtendsClass(gcType, "MeleeWeapon")) return "MeleeWeapon";
            if (AuthoredExtendsClass(gcType, "ActiveItem")) return "ActiveItem";
            if (AuthoredExtendsClass(gcType, "Armor")) return "Armor";
            if (AuthoredExtendsClass(gcType, "Item")) return "Item";

            string gcLower = (gcType ?? string.Empty).ToLowerInvariant();
            if (gcLower.Contains("ring") || gcLower.Contains("amulet"))
                return "Item";
            if (gcLower.Contains("crossbow") || gcLower.Contains("bow") ||
                gcLower.Contains("gun") || gcLower.Contains("cannon") ||
                gcLower.Contains("ranged"))
                return "RangedWeapon";
            if (gcLower.Contains("sword") || gcLower.Contains("axe") ||
                gcLower.Contains("mace") || gcLower.Contains("dagger") ||
                gcLower.Contains("hammer") || gcLower.Contains("staff") ||
                gcLower.Contains("spear") || gcLower.Contains("pick") ||
                gcLower.Contains("club") || gcLower.Contains("scepter") ||
                gcLower.Contains("wand") || gcLower.Contains("katana") ||
                gcLower.Contains("polearm") || gcLower.Contains("melee"))
                return "MeleeWeapon";
            return "Armor";
        }

        private static void WriteNonCombatInteractiveEntitySynchInfo(LEWriter writer, string gcType = null)
        {
            uint hpWire = ResolveAuthoredUnitMaxHealthWire(gcType);
            writer.WriteByte(0x02);
            writer.WriteUInt32(hpWire);
            Debug.LogError($"[ENTITY-SYNCH-INFO] packet=NCI owner=NonCombatInteractive gc={gcType ?? "<default>"} flags=0x02 hp={hpWire}");
        }

        private bool TryWriteEntitySynchForComponent(RRConnection conn, LEWriter writer, ushort componentId, byte subtype, EntitySynchInfoContext context, string packetName)
        {
            if (!ResolveEntitySynchInfoForComponent(conn, componentId, subtype, context, 0, packetName, out EntitySynchInfoDecision decision))
            {
                Debug.LogError($"[ENTITY-SYNCH-INFO-UNRESOLVED] packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} owner={decision.Owner} reason={decision.Reason}");
                return false;
            }

            return TryWriteResolvedEntitySynchInfo(writer, componentId, subtype, context, packetName, decision, conn);
        }

        private bool TryWriteResolvedEntitySynchInfo(LEWriter writer, ushort componentId, byte subtype, EntitySynchInfoContext context, string packetName, EntitySynchInfoDecision decision, RRConnection conn = null)
        {
            if (!decision.Allow)
            {
                Debug.LogError($"[ENTITY-SYNCH-INFO-BLOCK] packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} owner={decision.Owner} reason={decision.Reason}");
                return false;
            }

            if (decision.Owner == EntitySynchInfoOwner.Avatar && (decision.Flags & 0x02) == 0 && !ShouldKeepPlayerComponentEntitySynchInfoEmpty(context))
            {
                if (conn != null && TryResolvePlayerEntitySynchInfoHP(conn, context, packetName, out uint avatarHPWire))
                {
                    decision.Flags |= 0x02;
                    decision.HPWire = avatarHPWire;
                    decision.OwnerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : decision.OwnerEntityId;
                    decision.Reason = $"{decision.Reason}; avatar-hp-required";
                    decision.Provenance = string.IsNullOrEmpty(decision.Provenance) ? "avatar-hp-recovered" : decision.Provenance + "; avatar-hp-recovered";
                    decision.HpMutationSource = string.IsNullOrEmpty(decision.HpMutationSource) ? "avatar-hp-required" : decision.HpMutationSource;
                }
                else
                {
                    Debug.LogError($"[ENTITY-SYNCH-INFO-BLOCK] Avatar suffix without HP packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} flags=0x{decision.Flags:X2} reason={decision.Reason}");
                    return false;
                }
            }

            if (decision.Owner == EntitySynchInfoOwner.Monster && (decision.Flags & 0x02) == 0)
            {
                if (!ShouldKeepMonsterComponentEntitySynchInfoEmpty(context, packetName))
                {
                    Debug.LogError($"[ENTITY-SYNCH-INFO-BLOCK] Monster suffix without HP packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} flags=0x{decision.Flags:X2} reason={decision.Reason}");
                    return false;
                }
            }

            new EntitySynchInfoPayload(decision.Flags, decision.HPWire).Write(writer);

            if (VerboseSynchLogging)
            {
                string hpText = (decision.Flags & 0x02) != 0 ? decision.HPWire.ToString() : "none";
                string useTargetState = conn != null && conn.HasActiveUseTarget
                    ? $" useTarget={conn.ActiveUseTargetId} initUsePassed={conn.ActiveUseTargetInitUsePassed} visibleHit={conn.ActiveUseTargetVisibleHit} lastProjectileSeq={conn.ActiveUseTargetLastProjectileSeq} lastImpactTick={conn.ActiveUseTargetLastImpactTick}"
                    : " useTarget=0 initUsePassed=False visibleHit=False lastProjectileSeq=0 lastImpactTick=-1";
                Debug.LogError($"[ENTITY-SYNCH-INFO] packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} owner={decision.Owner} ownerEntity={decision.OwnerEntityId} flags=0x{decision.Flags:X2} hp={hpText} cutoffTick={decision.ValidationCutoffTick} runtime='{decision.RuntimeInstanceKey ?? ""}' schedulerTick={decision.SchedulerTick} subentity={decision.SubEntityPhase} rngPos={decision.RngPos} hpMutation='{decision.HpMutationSource ?? ""}' reason={decision.Reason} provenance={decision.Provenance}{useTargetState}");
            }
            return true;
        }

        private bool TryWriteResolvedEntitySynchInfo(LEWriter writer, ushort componentId, byte subtype, EntitySynchInfoContext context, string packetName, uint ownerEntityId, EntitySynchInfoDecision decision)
        {
            return TryWriteResolvedEntitySynchInfo(writer, componentId, subtype, context, packetName, decision);
        }

        private bool TryWriteRemoteAvatarEntitySynchInfo(RRConnection sourceConn, LEWriter writer, ushort componentId, byte subtype, string packetName)
        {
            if (!TryResolvePlayerEntitySynchInfoHP(sourceConn, EntitySynchInfoContext.PlayerActionResponse, packetName, out uint hpWire))
            {
                Debug.LogError($"[ENTITY-SYNCH-INFO-UNRESOLVED] packet={packetName} owner=RemoteAvatar reason=player-hp-unresolved");
                return false;
            }

            string source = sourceConn?.LoginName ?? "unknown";
            GetValidationCutoff(out uint validationCutoffTick);
            string runtimeKey = sourceConn != null ? GetInstanceZoneKey(sourceConn) : null;
            int rngPos = !string.IsNullOrWhiteSpace(runtimeKey) ? CombatRuntime.Instance.GetRoomRngPosForInstance(runtimeKey) : -1;
            return TryWriteResolvedEntitySynchInfo(writer, componentId, subtype, EntitySynchInfoContext.PlayerActionResponse, packetName, EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, hpWire, $"{packetName} source={source}", sourceConn?.Avatar != null ? (uint)sourceConn.Avatar.Id : 0u, componentId, subtype, $"remote-avatar; validationCutoffTick={validationCutoffTick}", validationCutoffTick, runtimeKey, sourceConn?.EntitySchedulerMirror?.SchedulerTick ?? 0, sourceConn?.EntitySchedulerMirror?.SubEntityPhase ?? false, rngPos));
        }

        private bool ResolveEntitySynchInfoForComponent(RRConnection conn, ushort componentId, byte subtype, EntitySynchInfoContext context, uint ownerEntityId, string packetName, out EntitySynchInfoDecision decision)
        {
            decision = EntitySynchInfoDecision.Empty(EntitySynchInfoOwner.Unknown, packetName);

            if (conn == null || (componentId == 0 && ownerEntityId == 0))
                return true;

            if (IsNonUnitPlayerComponentId(conn, componentId))
            {
                decision = EntitySynchInfoDecision.Empty(EntitySynchInfoOwner.NonUnit, $"{packetName} non-unit-player-component");
                return true;
            }

            RegisterEntitySynchInfoPlayer(conn);
            Monster monster = ResolveMonsterForComponent(componentId, ownerEntityId);
            if (monster != null)
            {
                EntitySynchInfoAuthority.Instance.RegisterMonster(monster);
                string monsterPacketName = $"{packetName} context={context} cid={componentId} sub=0x{subtype:X2} owner={monster.EntityId}";
                CombatRuntime.EntitySynchInfoVisibilityCutoff hpCutoff = CombatRuntime.Instance.GetEntitySynchInfoValidationCutoff(context, monsterPacketName);
                uint validationCutoffTick = hpCutoff.Tick;
                string runtimeInstanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
                int rngPos = CombatRuntime.Instance.GetRoomRngPosForInstance(runtimeInstanceKey);
                conn.EntitySchedulerMirror.ObserveSuffixCutoff(runtimeInstanceKey, validationCutoffTick, hpCutoff.IncludeSubEntityEffects, hpCutoff.Phase, monsterPacketName);
                if (ShouldKeepMonsterComponentEntitySynchInfoEmpty(context, packetName))
                {
                    decision = EntitySynchInfoDecision.Empty(EntitySynchInfoOwner.Monster, monsterPacketName);
                    return true;
                }
                CombatRuntime.Instance.TryResolveMonsterEntitySynchInfoHP(monster, context, monsterPacketName, hpCutoff, out uint monsterHPWire, out string monsterHPReason);
                EntitySynchInfoAuthority.Instance.RecordMonsterOutboundHP(monster, monsterHPWire, monsterPacketName);
                string provenance = $"{monsterHPReason}; visibleCutoffTick={validationCutoffTick}; cutoffPhase={hpCutoff.Phase}; includeSubEntity={hpCutoff.IncludeSubEntityEffects}; cutoffReason={hpCutoff.Reason}; lastEntity={hpCutoff.LastEntityTick}; lastSubEntity={hpCutoff.LastSubEntityTick}";
                decision = EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Monster, monsterHPWire, $"{monsterPacketName} {monsterHPReason}", monster.EntityId, componentId != 0 ? componentId : monster.BehaviorId, subtype, provenance, validationCutoffTick, runtimeInstanceKey, conn.EntitySchedulerMirror.SchedulerTick, conn.EntitySchedulerMirror.SubEntityPhase, rngPos, monsterHPReason);
                return true;
            }

            if (!IsAvatarEntitySynchInfoComponentId(conn, componentId))
            {
                decision = EntitySynchInfoDecision.Empty(EntitySynchInfoOwner.Unknown, packetName);
                return true;
            }

            if (!TryResolvePlayerEntitySynchInfoHP(conn, context, packetName, out uint avatarHPWire))
            {
                var state = GetPlayerState(conn.ConnId.ToString());
                avatarHPWire = state != null ? state.EntitySynchInfoHP : 0;
                Debug.LogError($"[ENTITY-SYNCH-INFO-RECOVER] packet={packetName} owner=Avatar component={componentId} hp={avatarHPWire} reason=avatar-hp-unresolved");
            }

            GetValidationCutoff(out uint avatarCutoffTick);
            string avatarRuntimeKey = GetInstanceZoneKey(conn);
            int avatarRngPos = !string.IsNullOrWhiteSpace(avatarRuntimeKey) ? CombatRuntime.Instance.GetRoomRngPosForInstance(avatarRuntimeKey) : -1;
            decision = EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, avatarHPWire, packetName, conn.Avatar != null ? (uint)conn.Avatar.Id : 0u, componentId, subtype, $"avatar-hp; validationCutoffTick={avatarCutoffTick}", avatarCutoffTick, avatarRuntimeKey, conn.EntitySchedulerMirror.SchedulerTick, conn.EntitySchedulerMirror.SubEntityPhase, avatarRngPos);
            return true;
        }

        private bool TryResolveWriterComponentUpdate(RRConnection conn, LEWriter writer, out ushort componentId, out byte subtype)
        {
            componentId = 0;
            subtype = 0;
            byte[] data = writer?.GetBuffer();
            if (data == null || data.Length < 3) return false;

            int byteOffset = data[0] == 0x07 ? 1 : 0;
            if (byteOffset + 2 >= data.Length)
                return false;
            byte opcode = data[byteOffset];
            if (opcode != 0x35 && opcode != 0x36)
                return false;
            if (opcode == 0x35 && byteOffset + 3 >= data.Length)
                return false;

            ushort cid = (ushort)(data[byteOffset + 1] | (data[byteOffset + 2] << 8));
            bool knownPlayerComponent = IsAvatarEntitySynchInfoComponentId(conn, cid)
                || IsNonUnitPlayerComponentId(conn, cid);
            if (!knownPlayerComponent && ResolveMonsterForComponent(cid, 0) == null)
                return false;

            componentId = cid;
            subtype = opcode == 0x35 ? data[byteOffset + 3] : (byte)0;
            return true;
        }

        private Monster ResolveMonsterForComponent(uint componentId, uint ownerEntityId)
        {
            if (CombatRuntime.Instance == null) return null;
            Monster monster = null;
            if (componentId != 0)
            {
                monster = CombatRuntime.Instance.GetMonsterByComponent(componentId)
                    ?? CombatRuntime.Instance.GetMonsterByBehaviorId(componentId)
                    ?? CombatRuntime.Instance.GetMonsterBySkillsId(componentId)
                    ?? CombatRuntime.Instance.GetMonsterByManipulatorsId(componentId);
            }
            if (monster == null && ownerEntityId != 0)
                monster = CombatRuntime.Instance.GetMonster(ownerEntityId);
            return monster;
        }

        private static EntitySynchInfoContext EntitySynchInfoContextFromTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return EntitySynchInfoContext.Unknown;
            switch (tag)
            {
                case "WorldInterval": return EntitySynchInfoContext.WorldInterval;
                case "WorldRuntimeBaselineReplay": return EntitySynchInfoContext.BaselineReplay;
                case "WorldRuntimeRecoveryReplay": return EntitySynchInfoContext.RecoveryReplay;
                case "WorldRuntimeRepeatReplay": return EntitySynchInfoContext.RepeatReplay;
                case "WorldRuntimeInventoryReplay": return EntitySynchInfoContext.InventoryReplay;
                case "WorldRuntimeEquipmentReplay": return EntitySynchInfoContext.EquipmentReplay;
                case "WorldLateArmorReplay": return EntitySynchInfoContext.LateArmorReplay;
                case "WorldUnitBehaviorControlGrant": return EntitySynchInfoContext.ControlGrant;
                case "WorldUnitBehaviorControlAck": return EntitySynchInfoContext.ControlAck;
                case "WorldMoverAck": return EntitySynchInfoContext.MoverAck;
                case "PlayerBasicAttackResponse": return EntitySynchInfoContext.PlayerBasicAttackResponse;
            }
            if (tag.StartsWith("MON-ATTACK", StringComparison.Ordinal)) return EntitySynchInfoContext.MonsterAction;
            if (tag.StartsWith("MON-MOVE", StringComparison.Ordinal)) return EntitySynchInfoContext.MonsterMove;
            if (tag.StartsWith("DAMAGE-HP", StringComparison.Ordinal)) return EntitySynchInfoContext.MonsterDamage;
            if (tag.Contains("Inventory")) return EntitySynchInfoContext.InventoryReplay;
            if (tag.Contains("Equip")) return EntitySynchInfoContext.EquipmentReplay;
            return EntitySynchInfoContext.PlayerActionResponse;
        }

        private static bool ShouldKeepPlayerComponentEntitySynchInfoEmpty(EntitySynchInfoContext context)
        {
            return false;
        }

        private static bool ShouldKeepMonsterComponentEntitySynchInfoEmpty(EntitySynchInfoContext context, string packetName = null)
        {
            return false;
        }

        private void RegisterEntitySynchInfoPlayer(RRConnection conn)
        {
            if (conn?.Avatar == null) return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return;
            EntitySynchInfoAuthority.Instance.RegisterPlayer(conn, state, (uint)conn.Avatar.Id);
        }

        private bool TryResolvePlayerEntitySynchInfoHP(RRConnection conn, string packetName, out uint hpWire)
        {
            return TryResolvePlayerEntitySynchInfoHP(conn, EntitySynchInfoContextFromTag(packetName), packetName, out hpWire);
        }

        private bool TryResolvePlayerEntitySynchInfoHP(RRConnection conn, EntitySynchInfoContext context, string packetName, out uint hpWire)
        {
            hpWire = 0;
            if (conn == null) return false;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return false;
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (playerEntityId != 0)
            {
                var resolve = EntitySynchInfoAuthority.Instance.ResolveOutboundPlayer(conn, state, playerEntityId, context, packetName, out hpWire);
                if (!resolve.AllowPacket)
                {
                    Debug.LogError($"[{packetName}] player EntitySynchInfo HP unresolved by EntitySynchInfoAuthority for {conn.LoginName ?? conn.ConnId.ToString()}: serverHP={state.CurrentHPWire / 256f:F2}/{state.MaxHPWire / 256f:F2} entitySynchInfoHP={state.EntitySynchInfoHP / 256f:F2} lastOutbound={conn.LastOutboundHPWire / 256f:F2} reason={resolve.Reason}");
                    return false;
                }
                if (VerboseSynchLogging && resolve.HasHP && (context == EntitySynchInfoContext.PlayerActionResponse || context == EntitySynchInfoContext.PlayerBasicAttackResponse || hpWire != state.CurrentHPWire))
                    Debug.LogError($"[PLAYER-HP-SUFFIX] packet={packetName} context={context} player={conn.LoginName ?? conn.ConnId.ToString()} currentHP={state.CurrentHPWire} entitySynchInfoHP={state.EntitySynchInfoHP} outboundHP={hpWire} simulationTick={_combatTick}");
                return resolve.HasHP;
            }
            hpWire = state.EntitySynchInfoHP;
            return true;
        }

        private uint GetEntitySynchInfoHPValue(RRConnection conn)
        {
            string caller = null;
            string connIdStr = conn.ConnId.ToString();
            bool existed = _playerStates.ContainsKey(connIdStr);
            PlayerState state = GetPlayerState(connIdStr);

            if (VerboseSynchLogging)
            {
                var trace = new System.Diagnostics.StackTrace(1, false);
                caller = trace.GetFrame(0)?.GetMethod()?.Name ?? "unknown";
                Debug.LogWarning($"[ENTITY-SYNCH-INFO-VALUE] Called by {caller}, returning {state.EntitySynchInfoHP}");
            }

            if (VerboseSynchLogging)
            {
                Debug.LogError($"[ENTITY-SYNCH-INFO-DETAIL] conn={conn.ConnId} key='{connIdStr}' existed={existed} entitySynchInfoHP={state.EntitySynchInfoHP} level={state.Level}");
                Debug.LogError($"[ENTITY-SYNCH-INFO-VALUE] Returning EntitySynchInfoHP: {state.EntitySynchInfoHP} CurrentHPWire: {state.CurrentHPWire}");
                if (state.EntitySynchInfoHP == 0)
                {
                    Debug.LogError("[ENTITY-SYNCH-INFO-DETAIL] entitySynchInfoHP=0 listing player states");
                    foreach (RRConnection orderedConnection in GetConnectionInsertionOrderSnapshot())
                    {
                        string orderedKey = orderedConnection.ConnId.ToString();
                        if (_playerStates.TryGetValue(orderedKey, out PlayerState orderedState))
                            Debug.LogError($"[ENTITY-SYNCH-INFO-DETAIL] key='{orderedKey}' entitySynchInfoHP={orderedState.EntitySynchInfoHP} level={orderedState.Level}");
                    }
                }
            }
            return state.EntitySynchInfoHP;
        }



        private static string InvKey(string connId, byte containerId)
            => containerId == 0x0B ? connId : $"{connId}:0x{containerId:X2}";

    }
}
