using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using DungeonRunners.Engine;
using Mono.Data.Sqlite;
using DungeonRunners.Data;
using DungeonRunners.Core;

namespace DungeonRunners.Database
{
    public static class CharacterRepository
    {
        private const int CharacterReadRetryCount = 3;
        private const int CharacterReadRetryDelayMilliseconds = 100;
        private static long _databaseTraceSequence;
        private static readonly object PendingFullSnapshotFenceSync = new object();
        private static readonly HashSet<uint> PendingFullSnapshotCharacterIds = new HashSet<uint>();
        public static Func<uint, bool> HasPendingPvpJournalFence;
        public static Func<bool> HasAnyPendingPvpJournalFence;

        public static bool TryRegisterPendingFullSnapshot(uint characterId)
        {
            if (characterId == 0 || characterId > int.MaxValue)
                return false;
            lock (PendingFullSnapshotFenceSync)
                return PendingFullSnapshotCharacterIds.Add(characterId);
        }

        public static void ReleasePendingFullSnapshot(uint characterId)
        {
            if (characterId == 0)
                return;
            lock (PendingFullSnapshotFenceSync)
                PendingFullSnapshotCharacterIds.Remove(characterId);
        }

        public static bool HasPendingFullSnapshot(uint characterId)
        {
            if (characterId == 0)
                return false;
            lock (PendingFullSnapshotFenceSync)
                return PendingFullSnapshotCharacterIds.Contains(characterId);
        }

        private static bool IsPersistedModifierId(uint modifierId)
        {
            return modifierId >= 1
                && modifierId <= 0x1FFFFFFF
                && (modifierId < 0x0000F000 || modifierId > 0x0000FFFF);
        }

        private sealed class PendingDroppedItemWrite
        {
            public string Zone;
            public uint ZoneId;
            public uint InstanceId;
            public GCObject Item;
            public int PosFixedX;
            public int PosFixedY;
            public int PosFixedZ;
            public int PlayerLevel;
            public int Quantity;
            public uint GoldAmount;
            public string DroppedBy;
            public uint OwnerCharacterId;
            public uint OwnerGroupId;
            public string OwnerName;
        }

        private sealed class PendingItemGrantWrite
        {
            public string GcClass;
            public int Count;
            public int Width;
            public int Height;
            public int Rarity;
        }

        public sealed class WorldActivationDropWrite
        {
            public string Zone;
            public uint ZoneId;
            public uint InstanceId;
            public GCObject Item;
            public int PosFixedX;
            public int PosFixedY;
            public int PosFixedZ;
            public int HeadingFixed;
            public int PlayerLevel;
            public int Quantity;
            public uint GoldAmount;
            public string DroppedBy;
            public uint OwnerCharacterId;
            public uint OwnerGroupId;
            public string OwnerName;
            public string QuestBindingKey;
        }

        private sealed class PendingWorldActivationWrite
        {
            public string ActivationKey;
            public string Zone;
            public uint ZoneId;
            public uint InstanceId;
            public int EntityId;
            public string OutcomeJson;
            public IReadOnlyList<WorldActivationDropWrite> Drops;
        }

        private sealed class PendingCharacterModifierWrite
        {
            public string GcType;
            public uint ModifierId;
            public byte Level;
            public uint PowerLevel;
            public uint DurationRemaining;
            public byte SourceIsSelf;
            public bool ReplaceSameType;
            public IReadOnlyList<string> ReplaceGcTypes;
        }

        private sealed class PendingDisconnectWrite
        {
            public string Login;
            public IReadOnlyList<KeyValuePair<uint, uint>> ModifierDurations;
        }

        private sealed class PendingPvpParticipantWrite
        {
            public PvpParticipantResult Result;
        }

        public sealed class PersistedCharacterModifier
        {
            public string GcType;
            public uint ModifierId;
            public byte Level;
            public uint PowerLevel;
            public uint DurationRemaining;
            public byte SourceIsSelf;
        }

        public static SavedCharacter CreateCharacter(string name, string className, uint accountId, string accountName, string avatarClass = "")
        {
            if (string.IsNullOrWhiteSpace(name) || accountId == 0 || accountId > int.MaxValue)
            {
                Debug.LogError("[DB-CHAR] operation=Create state=invalid-identity");
                return null;
            }

            var classDef = ClassConfig.GetClassDefinition(className);
            if (classDef == null)
            {
                Debug.LogError($"[DB-CHAR] Invalid class: {className}");
                return null;
            }

            try
            {
                using (var connection = GameDatabase.GetConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        @"INSERT INTO characters (account_id, name, class_name, avatar_class, level, experience, gold, current_zone)
                          VALUES (@aid, @name, @class, @avatar, 0, 0, 100, 'tutorial')",
                        ("@aid", (int)accountId), ("@name", name), ("@class", className), ("@avatar", avatarClass));

                    int characterId = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT last_insert_rowid()"));

                    var startingEquipment = classDef.startingEquipment;
                    InsertStartingEquipment(connection, characterId, startingEquipment, "weapon", startingEquipment.weapon);
                    InsertStartingEquipment(connection, characterId, startingEquipment, "armor", startingEquipment.armor);
                    InsertStartingEquipment(connection, characterId, startingEquipment, "helmet", startingEquipment.helmet);
                    InsertStartingEquipment(connection, characterId, startingEquipment, "gloves", startingEquipment.gloves);
                    InsertStartingEquipment(connection, characterId, startingEquipment, "boots", startingEquipment.boots);
                    InsertStartingEquipment(connection, characterId, startingEquipment, "shoulders", startingEquipment.shoulders ?? "");
                    InsertStartingEquipment(connection, characterId, startingEquipment, "shield", startingEquipment.shield ?? "");
                    InsertStartingEquipment(connection, characterId, startingEquipment, "ring1", startingEquipment.ring1 ?? "");
                    InsertStartingEquipment(connection, characterId, startingEquipment, "ring2", startingEquipment.ring2 ?? "");
                    InsertStartingEquipment(connection, characterId, startingEquipment, "amulet", startingEquipment.amulet ?? "");

                    if (classDef.startingSkills != null)
                    {
                        foreach (var skill in classDef.startingSkills)
                        {
                            int hotbarSlot = GetStartingSkillHotbarSlot(className, skill);
                            GameDatabase.ExecuteNonQuery(connection,
                                "INSERT OR IGNORE INTO character_skills (character_id, skill_gc_class, level, hotbar_slot) VALUES (@cid, @s, 1, @h)",
                                ("@cid", characterId), ("@s", skill), ("@h", hotbarSlot));
                        }
                    }

                    if (classDef.startingInventory != null)
                    {
                        int startingItemOrder = 0;
                        foreach (var item in classDef.startingInventory)
                        {
                            int count = item.count > 0 ? item.count : 1;
                            GameDatabase.ExecuteNonQuery(connection,
                                "INSERT INTO character_inventory (character_id, gc_class, slot_x, slot_y, count, container_id, item_order) VALUES (@cid, @gc, @x, @y, @count, 11, @itemOrder)",
                                ("@cid", characterId), ("@gc", item.gcClass), ("@x", (int)item.x), ("@y", (int)item.y), ("@count", count), ("@itemOrder", startingItemOrder++));
                        }
                    }

                    GameDatabase.ExecuteNonQuery(connection,
                        "INSERT OR IGNORE INTO character_skills (character_id, skill_gc_class, level) VALUES (@cid, @s, 1)",
                        ("@cid", characterId), ("@s", "skills.generic.SummonBlingGnome"));

                    transaction.Commit();
                    Debug.LogError($"[DB-CHAR] Created '{name}' (ID: {characterId}) class={className} account={accountId}");

                    return GetCharacter((uint)characterId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=Create state=failed message='{ex.Message}'");
                return null;
            }
        }

        private static int GetStartingSkillHotbarSlot(string className, string skill)
        {
            string classKey = (className ?? "").ToLowerInvariant();
            string skillGcType = skill ?? "";

            if (classKey.Contains("fight") || classKey.Contains("warrior"))
            {
                if (string.Equals(skillGcType, "skills.generic.Stomp", StringComparison.OrdinalIgnoreCase)) return 100;
                if (string.Equals(skillGcType, "skills.generic.Butcher", StringComparison.OrdinalIgnoreCase)) return 105;
                if (string.Equals(skillGcType, "skills.generic.FighterClassPassive", StringComparison.OrdinalIgnoreCase)) return 108;
                if (string.Equals(skillGcType, "skills.generic.MeleeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase)) return 109;
            }
            else if (classKey.Contains("ranger"))
            {
                if (string.Equals(skillGcType, "skills.generic.PoisonBlastRadius", StringComparison.OrdinalIgnoreCase)) return 100;
                if (string.Equals(skillGcType, "skills.generic.PoisonShot", StringComparison.OrdinalIgnoreCase)) return 105;
                if (string.Equals(skillGcType, "skills.generic.RangerClassPassive", StringComparison.OrdinalIgnoreCase)) return 108;
                if (string.Equals(skillGcType, "skills.generic.RangeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase)) return 109;
            }
            else if (classKey.Contains("mage") || classKey.Contains("warlock"))
            {
                if (string.Equals(skillGcType, "skills.generic.ShadowLightning", StringComparison.OrdinalIgnoreCase)) return 100;
                if (string.Equals(skillGcType, "skills.generic.FireBolt", StringComparison.OrdinalIgnoreCase)) return 105;
                if (string.Equals(skillGcType, "skills.generic.MageClassPassive", StringComparison.OrdinalIgnoreCase)) return 108;
                if (string.Equals(skillGcType, "skills.generic.MagicDamageModPassive", StringComparison.OrdinalIgnoreCase)) return 109;
            }

            return -1;
        }


        private static bool IsTransientDatabaseLock(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                string message = current.Message ?? string.Empty;
                if (message.IndexOf("database is locked", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("database is busy", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public static SavedCharacter GetCharacter(uint characterId)
        {
            for (int attempt = 0; attempt < CharacterReadRetryCount; attempt++)
            {
                try
                {
                    using (var connection = GameDatabase.GetConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        SavedCharacter character = null;

                        using (var reader = GameDatabase.ExecuteReader(connection,
                            "SELECT * FROM characters WHERE id = @id", ("@id", (int)characterId)))
                        {
                            if (!reader.Read()) return null;
                            character = ReadCharacterRow(reader);
                        }

                        character.equipment = LoadEquipment(connection, (int)characterId);
                        ApplyStartingEquipmentLevels(character);
                        character.skills = LoadSkillList(connection, (int)characterId);
                        character.skillLevels = LoadSkillLevels(connection, (int)characterId);
                        character.hotbarSlots = LoadHotbarSlots(connection, (int)characterId);
                        character.inventory = LoadInventory(connection, (int)characterId);
                        character.activeQuests = LoadActiveQuests(connection, (int)characterId);
                        character.completedQuests = LoadCompletedQuests(connection, (int)characterId, character.questCompletionTimes);
                        character.unlockedCheckpoints = LoadCheckpoints(connection, (int)characterId);

                        if (character.posseId != 0)
                        {
                            object posseNameValue = GameDatabase.ExecuteScalar(connection,
                                "SELECT name FROM posses WHERE id = @id", ("@id", (int)character.posseId));
                            character.posseName = posseNameValue == null || posseNameValue == DBNull.Value ? "" : Convert.ToString(posseNameValue);
                        }

                        transaction.Commit();
                        return character;
                    }
                }
                catch (Exception ex) when (IsTransientDatabaseLock(ex) && attempt + 1 < CharacterReadRetryCount)
                {
                    Debug.LogError($"[DB-CHAR] operation=GetCharacter state=retry characterId={characterId} attempt={attempt + 1} reason=transient-lock");
                    Thread.Sleep(CharacterReadRetryDelayMilliseconds * (attempt + 1));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DB-CHAR] operation=GetCharacter state=failed message='{ex.Message}'");
                    return null;
                }
            }
            Debug.LogError($"[DB-CHAR] operation=GetCharacter state=failed characterId={characterId} reason=retry-exhausted");
            return null;
        }

        public static SavedCharacter GetCharacterByName(string characterName)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    object characterIdValue = GameDatabase.ExecuteScalar(connection,
                        "SELECT id FROM characters WHERE name = @n", ("@n", characterName));
                    if (characterIdValue == null) return null;
                    return GetCharacter(Convert.ToUInt32(characterIdValue));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=GetCharacterByName state=failed name='{characterName ?? string.Empty}' message='{ex.Message}'");
                return null;
            }
        }

        public static List<SavedCharacter> GetCharactersForAccount(string accountName)
        {
            var result = new List<SavedCharacter>();
            var characterIds = new List<int>();
            try
            {
                uint accountId = AccountRepository.GetAccountId(accountName);
                if (accountId == 0) return result;

                using (var connection = GameDatabase.GetConnection())
                {
                    using (var reader = GameDatabase.ExecuteReader(connection,
                        "SELECT id FROM characters WHERE account_id = @aid ORDER BY id",
                        ("@aid", (int)accountId)))
                    {
                        while (reader.Read())
                            characterIds.Add(reader.GetInt32(0));
                    }

                }

                foreach (int characterId in characterIds)
                {
                    SavedCharacter character = GetCharacter((uint)characterId);
                    if (character == null)
                    {
                        Debug.LogError($"[DB-CHAR] operation=GetForAccount state=failed account='{accountName}' characterId={characterId} reason=character-load");
                        return null;
                    }
                    result.Add(character);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=GetForAccount state=failed message='{ex.Message}'");
                return null;
            }
            return result;
        }

        public static bool CharacterNameExists(string name)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    object characterCount = GameDatabase.ExecuteScalar(connection,
                        "SELECT COUNT(*) FROM characters WHERE name = @n COLLATE NOCASE", ("@n", name));
                    return Convert.ToInt32(characterCount) > 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=CharacterNameExists state=failed message='{ex.Message}'");
                return true;
            }
        }


        public static bool SaveCharacter(SavedCharacter character, [CallerMemberName] string caller = null)
        {
            return TrySaveCharacter(character, caller);
        }

        public static bool TrySaveCharacter(SavedCharacter character, [CallerMemberName] string caller = null)
        {
            return TrySaveCharacterInternal(character, 0, 0, null, null, null, null, null, null, caller, out _, out _, out _, out _, out _);
        }

        public static bool TrySaveDisconnectSnapshot(
            SavedCharacter character,
            string login,
            IReadOnlyDictionary<uint, uint> modifierDurations,
            [CallerMemberName] string caller = null)
        {
            if (!PvpResultRepository.TryNormalizeDisconnectSnapshot(character, out SavedCharacter normalizedCharacter))
                return false;
            character = normalizedCharacter;
            if (!IsValidDisconnectSnapshotPayload(character, login, modifierDurations)
                || !HasPendingFullSnapshot(character.id))
                return false;
            var orderedDurations = new List<KeyValuePair<uint, uint>>(modifierDurations.Count);
            foreach (KeyValuePair<uint, uint> entry in modifierDurations)
            {
                if (!IsPersistedModifierId(entry.Key) || entry.Value == 0 || entry.Value > int.MaxValue)
                    return false;
                orderedDurations.Add(entry);
            }
            orderedDurations.Sort((left, right) => left.Key.CompareTo(right.Key));
            var pendingDisconnect = new PendingDisconnectWrite
            {
                Login = login,
                ModifierDurations = orderedDurations
            };
            return TrySaveCharacterInternal(character, 0, 0, null, null, null, null, null, pendingDisconnect, caller, out _, out _, out _, out _, out _);
        }

        internal static bool IsValidDisconnectSnapshotPayload(
            SavedCharacter character,
            string login,
            IReadOnlyDictionary<uint, uint> modifierDurations)
        {
            if (character == null || string.IsNullOrWhiteSpace(login) || login.Length > 256
                || modifierDurations == null || modifierDurations.Count > ushort.MaxValue)
                return false;
            foreach (KeyValuePair<uint, uint> entry in modifierDurations)
                if (!IsPersistedModifierId(entry.Key) || entry.Value == 0 || entry.Value > int.MaxValue)
                    return false;
            try
            {
                ValidateCharacterForSave(character);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TrySaveCharacterWithModifier(
            SavedCharacter character,
            string gcType,
            uint modifierId,
            byte level,
            uint powerLevel,
            uint durationRemaining,
            byte sourceIsSelf,
            [CallerMemberName] string caller = null,
            bool replaceSameType = true,
            IReadOnlyList<string> replaceGcTypes = null)
        {
            if (string.IsNullOrWhiteSpace(gcType) || gcType.Length > 1024
                || !IsPersistedModifierId(modifierId)
                || powerLevel > int.MaxValue || durationRemaining > int.MaxValue
                || sourceIsSelf > 1)
                return false;
            if (replaceGcTypes != null)
                for (int index = 0; index < replaceGcTypes.Count; index++)
                    if (string.IsNullOrWhiteSpace(replaceGcTypes[index]) || replaceGcTypes[index].Length > 1024)
                        return false;
            var pendingModifier = new PendingCharacterModifierWrite
            {
                GcType = gcType,
                ModifierId = modifierId,
                Level = level,
                PowerLevel = powerLevel,
                DurationRemaining = durationRemaining,
                SourceIsSelf = sourceIsSelf,
                ReplaceSameType = replaceSameType,
                ReplaceGcTypes = replaceGcTypes
            };
            return TrySaveCharacterInternal(character, 0, 0, null, null, null, pendingModifier, null, null, caller, out _, out _, out _, out _, out _);
        }

        public static bool TryUpdateCharacterCurrentMana(uint characterId, uint currentMana, [CallerMemberName] string caller = null)
        {
            bool databaseTracking = ServerDiagnostics.IsEnabled("databaseTracking");
            long traceSequence = databaseTracking ? Interlocked.Increment(ref _databaseTraceSequence) : 0;
            if (databaseTracking)
                Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=update-current-mana phase=begin characterId={characterId} mana={currentMana} reason={caller ?? "unknown"}");
            if (characterId == 0 || characterId > int.MaxValue || currentMana > int.MaxValue
                || HasPendingFullSnapshot(characterId))
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=update-current-mana phase=rollback characterId={characterId} reason={caller ?? "unknown"} errorType=ArgumentOutOfRangeException");
                return false;
            }
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var transaction = connection.BeginTransaction();
                GameDatabase.ExecuteNonQuery(
                    connection,
                    "UPDATE characters SET current_mana=@mana WHERE id=@id",
                    ("@mana", (int)currentMana),
                    ("@id", (int)characterId));
                int affected = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                if (affected != 1)
                    throw new InvalidDataException($"Character {characterId} current mana was not updated exactly once");
                transaction.Commit();
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=update-current-mana phase=commit characterId={characterId} mana={currentMana} affected={affected} reason={caller ?? "unknown"}");
                return true;
            }
            catch (Exception ex)
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=update-current-mana phase=rollback characterId={characterId} reason={caller ?? "unknown"} errorType={ex.GetType().Name}");
                Debug.LogError($"[DB-CHAR] operation=UpdateCurrentMana state=failed characterId={characterId} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TrySaveCharacterConsumingDrop(SavedCharacter character, long droppedItemId, [CallerMemberName] string caller = null)
        {
            if (droppedItemId < 0)
                return false;
            return TrySaveCharacterInternal(character, droppedItemId, 0, null, null, null, null, null, null, caller, out _, out _, out _, out _, out _);
        }

        public static bool TrySaveCharacterConsumingGrant(SavedCharacter character, long pendingGrantId, [CallerMemberName] string caller = null)
        {
            if (pendingGrantId <= 0)
                return false;
            return TrySaveCharacterInternal(character, 0, pendingGrantId, null, null, null, null, null, null, caller, out _, out _, out _, out _, out _);
        }

        public static bool TrySaveCharacterCreatingDrop(
            SavedCharacter character,
            string zone,
            uint zoneId,
            uint instanceId,
            GCObject item,
            int posFixedX,
            int posFixedY,
            int posFixedZ,
            int playerLevel,
            int quantity,
            string droppedBy,
            uint ownerCharacterId,
            uint ownerGroupId,
            string ownerName,
            out long droppedItemId,
            [CallerMemberName] string caller = null)
        {
            droppedItemId = 0;
            if (string.IsNullOrWhiteSpace(zone) || item == null || string.IsNullOrWhiteSpace(item.GCClass)
                || ownerCharacterId > int.MaxValue
                || playerLevel <= 0 || quantity <= 0 || quantity > byte.MaxValue)
                return false;
            var pendingDrop = new PendingDroppedItemWrite
            {
                Zone = zone,
                ZoneId = zoneId,
                InstanceId = instanceId,
                Item = item,
                PosFixedX = posFixedX,
                PosFixedY = posFixedY,
                PosFixedZ = posFixedZ,
                PlayerLevel = playerLevel,
                Quantity = quantity,
                DroppedBy = droppedBy ?? "",
                OwnerCharacterId = ownerCharacterId,
                OwnerGroupId = ownerGroupId,
                OwnerName = ownerName ?? ""
            };
            return TrySaveCharacterInternal(character, 0, 0, pendingDrop, null, null, null, null, null, caller, out droppedItemId, out _, out _, out _, out _);
        }

        public static bool TrySaveCharacterCreatingGrant(
            SavedCharacter character,
            string gcClass,
            int count,
            int width,
            int height,
            int rarity,
            out long pendingGrantId,
            [CallerMemberName] string caller = null)
        {
            pendingGrantId = 0;
            if (string.IsNullOrWhiteSpace(gcClass) || count <= 0 || count > byte.MaxValue
                || width <= 0 || width > 10 || height <= 0 || height > 8
                || rarity < -1 || rarity > 5)
                return false;
            var pendingGrant = new PendingItemGrantWrite
            {
                GcClass = gcClass,
                Count = count,
                Width = width,
                Height = height,
                Rarity = rarity
            };
            return TrySaveCharacterInternal(character, 0, 0, null, pendingGrant, null, null, null, null, caller, out _, out pendingGrantId, out _, out _, out _);
        }

        public static bool TryApplyPvpParticipantResult(
            SavedCharacter character,
            PvpParticipantResult result,
            out long pendingGrantId,
            out bool alreadyApplied,
            [CallerMemberName] string caller = null)
        {
            pendingGrantId = 0;
            alreadyApplied = false;
            if (character == null || result == null || character.id != result.CharacterId
                || !PvpResultRepository.IsValidParticipant(result)
                || character.pvpWins != checked(result.WinsBefore + result.WinsDelta)
                || character.pvpRating != (result.ApplyRating ? result.RatingAfter : result.PersistedRatingBefore)
                || !string.Equals(character.currentZoneName, result.ReturnZone, StringComparison.OrdinalIgnoreCase)
                || character.zoneId != result.ReturnZoneId
                || character.positionFixedX != result.ReturnPosFixedX
                || character.positionFixedY != result.ReturnPosFixedY
                || character.positionFixedZ != result.ReturnPosFixedZ)
                return false;
            PendingItemGrantWrite pendingGrant = null;
            if (result.RewardCount > 0)
            {
                pendingGrant = new PendingItemGrantWrite
                {
                    GcClass = result.RewardGcClass,
                    Count = result.RewardCount,
                    Width = result.RewardWidth,
                    Height = result.RewardHeight,
                    Rarity = result.RewardRarity
                };
            }
            var pendingPvp = new PendingPvpParticipantWrite
            {
                Result = result
            };
            return TrySaveCharacterInternal(character, 0, 0, null, pendingGrant, null, null, pendingPvp, null, caller, out _, out pendingGrantId, out _, out _, out alreadyApplied);
        }

        public static bool TryIsWorldEntityActivationCommitted(string activationKey, out bool committed)
        {
            committed = false;
            if (string.IsNullOrWhiteSpace(activationKey))
                return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                committed = Convert.ToInt32(GameDatabase.ExecuteScalar(connection,
                    "SELECT COUNT(*) FROM world_entity_activations WHERE activation_key=@key",
                    ("@key", activationKey)) ?? 0) == 1;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=WorldEntityActivationLookup state=failed message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryHasCharacterModifier(uint characterId, string gcType, out bool exists)
        {
            exists = false;
            bool databaseTracking = ServerDiagnostics.IsEnabled("databaseTracking");
            long traceSequence = databaseTracking ? Interlocked.Increment(ref _databaseTraceSequence) : 0;
            if (databaseTracking)
                Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=character-modifier-lookup phase=begin characterId={characterId} modifier={gcType ?? ""}");
            if (characterId == 0 || characterId > int.MaxValue || string.IsNullOrWhiteSpace(gcType)
                || HasPendingFullSnapshot(characterId))
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=character-modifier-lookup phase=rollback characterId={characterId} errorType=ArgumentOutOfRangeException");
                return false;
            }
            try
            {
                using var connection = GameDatabase.GetConnection();
                int count = Convert.ToInt32(GameDatabase.ExecuteScalar(
                    connection,
                    "SELECT COUNT(*) FROM character_modifiers WHERE character_id=@cid AND gc_type=@gc COLLATE NOCASE",
                    ("@cid", (int)characterId),
                    ("@gc", gcType)) ?? 0);
                if (count < 0 || count > 1)
                    throw new InvalidDataException($"Character modifier type is duplicated characterId={characterId} gcType='{gcType}'");
                exists = count == 1;
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=character-modifier-lookup phase=commit characterId={characterId} exists={exists}");
                return true;
            }
            catch (Exception ex)
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=character-modifier-lookup phase=rollback characterId={characterId} errorType={ex.GetType().Name}");
                Debug.LogError($"[DB-CHAR] operation=CharacterModifierLookup state=failed characterId={characterId} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryDeleteCharacterModifier(uint characterId, string gcType, [CallerMemberName] string caller = null)
        {
            bool databaseTracking = ServerDiagnostics.IsEnabled("databaseTracking");
            long traceSequence = databaseTracking ? Interlocked.Increment(ref _databaseTraceSequence) : 0;
            if (databaseTracking)
                Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=character-modifier-delete phase=begin characterId={characterId} modifier={gcType ?? ""} reason={caller ?? "unknown"}");
            if (characterId == 0 || characterId > int.MaxValue || string.IsNullOrWhiteSpace(gcType)
                || HasPendingFullSnapshot(characterId))
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=character-modifier-delete phase=rollback characterId={characterId} reason={caller ?? "unknown"} errorType=ArgumentOutOfRangeException");
                return false;
            }
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var transaction = connection.BeginTransaction();
                GameDatabase.ExecuteNonQuery(
                    connection,
                    "DELETE FROM character_modifiers WHERE character_id=@cid AND gc_type=@gc COLLATE NOCASE",
                    ("@cid", (int)characterId),
                    ("@gc", gcType));
                int deleted = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                if (deleted < 0 || deleted > 1)
                    throw new InvalidDataException($"Character modifier delete affected an invalid row count characterId={characterId} gcType='{gcType}' count={deleted}");
                transaction.Commit();
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=character-modifier-delete phase=commit characterId={characterId} affected={deleted} reason={caller ?? "unknown"}");
                return true;
            }
            catch (Exception ex)
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=character-modifier-delete phase=rollback characterId={characterId} reason={caller ?? "unknown"} errorType={ex.GetType().Name}");
                Debug.LogError($"[DB-CHAR] operation=CharacterModifierDelete state=failed characterId={characterId} caller={caller ?? "unknown"} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryDeleteCharacterModifier(uint characterId, uint modifierId, [CallerMemberName] string caller = null)
        {
            if (characterId == 0 || characterId > int.MaxValue || modifierId == 0 || modifierId > int.MaxValue
                || HasPendingFullSnapshot(characterId))
                return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var transaction = connection.BeginTransaction();
                GameDatabase.ExecuteNonQuery(
                    connection,
                    "DELETE FROM character_modifiers WHERE character_id=@cid AND modifier_id=@mid",
                    ("@cid", (int)characterId),
                    ("@mid", (int)modifierId));
                int deleted = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                if (deleted < 0 || deleted > 1)
                    throw new InvalidDataException($"Character modifier delete affected an invalid row count characterId={characterId} modifierId={modifierId} count={deleted}");
                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=CharacterModifierDeleteById state=failed characterId={characterId} modifierId={modifierId} caller={caller ?? "unknown"} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryDeleteCharacterModifiers(uint characterId, IReadOnlyList<uint> modifierIds, [CallerMemberName] string caller = null)
        {
            if (characterId == 0 || characterId > int.MaxValue || modifierIds == null
                || HasPendingFullSnapshot(characterId))
                return false;
            var uniqueIds = new HashSet<uint>();
            for (int index = 0; index < modifierIds.Count; index++)
            {
                uint modifierId = modifierIds[index];
                if (modifierId == 0 || modifierId > int.MaxValue || !uniqueIds.Add(modifierId))
                    return false;
            }
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var transaction = connection.BeginTransaction();
                foreach (uint modifierId in uniqueIds)
                    GameDatabase.ExecuteNonQuery(
                        connection,
                        "DELETE FROM character_modifiers WHERE character_id=@cid AND modifier_id=@mid",
                        ("@cid", (int)characterId),
                        ("@mid", (int)modifierId));
                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=CharacterModifierDeleteBatch state=failed characterId={characterId} caller={caller ?? "unknown"} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryGetCharacterModifiersInRange(uint characterId, uint firstModifierId, uint lastModifierId, out List<PersistedCharacterModifier> modifiers)
        {
            modifiers = new List<PersistedCharacterModifier>();
            if (characterId == 0 || characterId > int.MaxValue || firstModifierId == 0
                || firstModifierId > lastModifierId || lastModifierId > int.MaxValue)
                return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var reader = GameDatabase.ExecuteReader(
                    connection,
                    @"SELECT gc_type, modifier_id, level, power_level, duration_remaining, source_is_self
                      FROM character_modifiers
                      WHERE character_id=@cid AND modifier_id>=@first AND modifier_id<=@last
                      ORDER BY modifier_id",
                    ("@cid", (int)characterId),
                    ("@first", (int)firstModifierId),
                    ("@last", (int)lastModifierId));
                while (reader.Read())
                {
                    string gcType = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    long modifierId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    long level = reader.IsDBNull(2) ? -1 : reader.GetInt64(2);
                    long powerLevel = reader.IsDBNull(3) ? -1 : reader.GetInt64(3);
                    long duration = reader.IsDBNull(4) ? -1 : reader.GetInt64(4);
                    long sourceIsSelf = reader.IsDBNull(5) ? -1 : reader.GetInt64(5);
                    if (string.IsNullOrWhiteSpace(gcType) || modifierId < firstModifierId || modifierId > lastModifierId
                        || level < byte.MinValue || level > byte.MaxValue || powerLevel < 0 || powerLevel > int.MaxValue
                        || duration < 0 || duration > int.MaxValue || sourceIsSelf < 0 || sourceIsSelf > 1)
                        throw new InvalidDataException($"Character modifier row is invalid characterId={characterId} modifierId={modifierId}");
                    modifiers.Add(new PersistedCharacterModifier
                    {
                        GcType = gcType,
                        ModifierId = checked((uint)modifierId),
                        Level = checked((byte)level),
                        PowerLevel = checked((uint)powerLevel),
                        DurationRemaining = checked((uint)duration),
                        SourceIsSelf = checked((byte)sourceIsSelf)
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                modifiers.Clear();
                Debug.LogError($"[DB-CHAR] operation=CharacterModifierRangeLoad state=failed characterId={characterId} first={firstModifierId} last={lastModifierId} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TryUpdateCharacterModifierDurations(uint characterId, IReadOnlyDictionary<uint, uint> durations, [CallerMemberName] string caller = null)
        {
            if (characterId == 0 || characterId > int.MaxValue || durations == null
                || HasPendingFullSnapshot(characterId))
                return false;
            foreach (KeyValuePair<uint, uint> entry in durations)
                if (!IsPersistedModifierId(entry.Key) || entry.Value == 0 || entry.Value > int.MaxValue)
                    return false;
            try
            {
                using var connection = GameDatabase.GetConnection();
                using var transaction = connection.BeginTransaction();
                foreach (KeyValuePair<uint, uint> entry in durations)
                {
                    GameDatabase.ExecuteNonQuery(
                        connection,
                        "UPDATE character_modifiers SET duration_remaining=@duration WHERE character_id=@cid AND modifier_id=@mid",
                        ("@duration", (int)entry.Value),
                        ("@cid", (int)characterId),
                        ("@mid", (int)entry.Key));
                    int affected = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                    if (affected != 1)
                        throw new InvalidDataException($"Character modifier duration update affected an invalid row count characterId={characterId} modifierId={entry.Key} count={affected}");
                }
                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=CharacterModifierDurationUpdate state=failed characterId={characterId} caller={caller ?? "unknown"} message='{ex.Message}'");
                return false;
            }
        }

        public static bool TrySaveCharacterCreatingWorldActivation(
            SavedCharacter character,
            string activationKey,
            string zone,
            uint zoneId,
            uint instanceId,
            int entityId,
            string outcomeJson,
            IReadOnlyList<WorldActivationDropWrite> drops,
            out List<long> droppedItemIds,
            out bool alreadyCommitted,
            [CallerMemberName] string caller = null,
            string modifierGcType = null,
            uint modifierId = 0,
            byte modifierLevel = 0,
            uint modifierPowerLevel = 0,
            uint modifierDurationRemaining = 0,
            byte modifierSourceIsSelf = 1)
        {
            droppedItemIds = new List<long>();
            alreadyCommitted = false;
            if (character == null || string.IsNullOrWhiteSpace(activationKey) || activationKey.Length > 2048
                || string.IsNullOrWhiteSpace(zone) || entityId < 0 || string.IsNullOrWhiteSpace(outcomeJson)
                || drops == null || drops.Count > byte.MaxValue)
                return false;
            try
            {
                using JsonDocument outcome = JsonDocument.Parse(outcomeJson);
                if (outcome.RootElement.ValueKind != JsonValueKind.Object)
                    return false;
            }
            catch (JsonException)
            {
                return false;
            }
            for (int index = 0; index < drops.Count; index++)
            {
                WorldActivationDropWrite drop = drops[index];
                if (drop == null || drop.Item == null || string.IsNullOrWhiteSpace(drop.Item.GCClass)
                    || !string.Equals(drop.Zone, zone, StringComparison.OrdinalIgnoreCase)
                    || drop.ZoneId != zoneId || drop.InstanceId != instanceId
                    || drop.PlayerLevel <= 0 || drop.Quantity <= 0 || drop.Quantity > byte.MaxValue
                    || drop.OwnerCharacterId != character.id || drop.OwnerCharacterId > int.MaxValue
                    || drop.GoldAmount > int.MaxValue
                    || (drop.GoldAmount > 0 && !string.Equals(drop.Item.GCClass, "Currency", StringComparison.OrdinalIgnoreCase))
                    || drop.QuestBindingKey == null)
                    return false;
            }
            var pendingActivation = new PendingWorldActivationWrite
            {
                ActivationKey = activationKey,
                Zone = zone,
                ZoneId = zoneId,
                InstanceId = instanceId,
                EntityId = entityId,
                OutcomeJson = outcomeJson,
                Drops = drops
            };
            PendingCharacterModifierWrite pendingModifier = null;
            if (!string.IsNullOrWhiteSpace(modifierGcType))
            {
                if (modifierGcType.Length > 1024 || !IsPersistedModifierId(modifierId)
                    || modifierPowerLevel > int.MaxValue || modifierDurationRemaining > int.MaxValue
                    || modifierSourceIsSelf > 1)
                    return false;
                pendingModifier = new PendingCharacterModifierWrite
                {
                    GcType = modifierGcType,
                    ModifierId = modifierId,
                    Level = modifierLevel,
                    PowerLevel = modifierPowerLevel,
                    DurationRemaining = modifierDurationRemaining,
                    SourceIsSelf = modifierSourceIsSelf,
                    ReplaceSameType = true
                };
            }
            else if (modifierId != 0 || modifierLevel != 0 || modifierPowerLevel != 0 || modifierDurationRemaining != 0 || modifierSourceIsSelf != 1)
            {
                return false;
            }
            return TrySaveCharacterInternal(character, 0, 0, null, null, pendingActivation, pendingModifier, null, null, caller, out _, out _, out droppedItemIds, out alreadyCommitted, out _);
        }

        private static void ValidateCharacterForSave(SavedCharacter character)
        {
            if (character.id == 0 || character.id > int.MaxValue)
                throw new InvalidDataException($"Character identity is outside SQLite runtime range characterId={character.id}");
            if (character.level > 99 || character.experience > int.MaxValue || character.gold > int.MaxValue
                || character.currentHP > int.MaxValue || character.currentMana > int.MaxValue
                || character.maxHP < 0 || character.maxMana < 0
                || character.statStrength < 0 || character.statStrength > ushort.MaxValue
                || character.statAgility < 0 || character.statAgility > ushort.MaxValue
                || character.statIntellect < 0 || character.statIntellect > ushort.MaxValue
                || character.statEndurance < 0 || character.statEndurance > ushort.MaxValue
                || character.lastRespecTime < 0 || character.respecCount < 0
                || character.pvpWins < 0 || character.pvpRating < 0 || character.pvpRating > 3000
                || character.monsterDifficulty > 3 || character.minimumItemQuality < 1 || character.minimumItemQuality > 7
                || character.posseJoinCooldown < 0 || character.posseRankId < 0 || character.posseRankId > 10)
                throw new InvalidDataException($"Character numeric value is outside SQLite runtime range characterId={character.id}");
            if (character.posseId > int.MaxValue
                || string.IsNullOrWhiteSpace(character.currentZoneName))
                throw new InvalidDataException($"Character topology value is invalid characterId={character.id}");

            var inventoryOrderKeys = new HashSet<string>(StringComparer.Ordinal);
            if (character.inventory != null)
            {
                foreach (SavedInventoryItem item in character.inventory)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.gcClass)
                        || item.count <= 0 || item.count > byte.MaxValue
                        || item.buyPrice > int.MaxValue
                        || item.rarity < -1 || item.rarity > 5
                        || (item.storedLevel != -1 && (item.storedLevel < 1 || item.storedLevel > 120))
                        || item.itemOrder < 0
                        || (item.containerId != 0x0B && item.containerId != 0x0C && (item.containerId < 0x0E || item.containerId > 0x13))
                        || item.x > 9 || item.y >= (item.containerId == 0x0C || (item.containerId >= 0x0E && item.containerId <= 0x13) ? 14 : 8))
                        throw new InvalidDataException($"Character inventory row is invalid characterId={character.id}");
                    if (!inventoryOrderKeys.Add($"{item.containerId}:{item.itemOrder}"))
                        throw new InvalidDataException($"Character inventory order is duplicated characterId={character.id} containerId={item.containerId} itemOrder={item.itemOrder}");
                }
            }

            var persistedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (character.skills != null)
            {
                foreach (string skill in character.skills)
                    if (string.IsNullOrWhiteSpace(skill) || !persistedSkills.Add(skill))
                        throw new InvalidDataException($"Character skill row is invalid characterId={character.id}");
            }

            var skillLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (character.skillLevels != null)
            {
                foreach (SkillLevelEntry skillLevel in character.skillLevels)
                    if (skillLevel == null || string.IsNullOrWhiteSpace(skillLevel.skill)
                        || skillLevel.level < 1 || skillLevel.level > byte.MaxValue
                        || !persistedSkills.Contains(skillLevel.skill) || !skillLevelNames.Add(skillLevel.skill))
                        throw new InvalidDataException($"Character skill level row is invalid characterId={character.id}");
            }

            var hotbarSlots = new HashSet<uint>();
            if (character.hotbarSlots != null)
            {
                foreach (HotbarSlotEntry slot in character.hotbarSlots)
                    if (slot == null || slot.slot > 0x0FFF || string.IsNullOrWhiteSpace(slot.skill)
                        || !persistedSkills.Contains(slot.skill) || !hotbarSlots.Add(slot.slot))
                        throw new InvalidDataException($"Character hotbar row is invalid characterId={character.id}");
            }
        }

        private static void EnsureCharacterIdentity(SqliteConnection connection, SavedCharacter character)
        {
            using (var reader = GameDatabase.ExecuteReader(
                connection,
                "SELECT account_id, name FROM characters WHERE id=@id",
                ("@id", (int)character.id)))
            {
                if (!reader.Read())
                    throw new InvalidDataException($"Character {character.id} is missing before save");
                if (reader.GetInt32(0) != character.accountId
                    || !string.Equals(reader.GetString(1), character.name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Character {character.id} identity changed before save");
            }
        }

        private static bool TrySaveCharacterInternal(SavedCharacter character, long droppedItemId, long pendingGrantId, PendingDroppedItemWrite pendingDrop, PendingItemGrantWrite pendingGrant, PendingWorldActivationWrite pendingWorldActivation, PendingCharacterModifierWrite pendingModifier, PendingPvpParticipantWrite pendingPvp, PendingDisconnectWrite pendingDisconnect, string caller, out long createdDroppedItemId, out long createdPendingGrantId, out List<long> createdWorldDropIds, out bool worldActivationAlreadyCommitted, out bool pvpResultAlreadyApplied)
        {
            createdDroppedItemId = 0;
            createdPendingGrantId = 0;
            createdWorldDropIds = new List<long>();
            worldActivationAlreadyCommitted = false;
            pvpResultAlreadyApplied = false;
            bool databaseTracking = ServerDiagnostics.IsEnabled("databaseTracking");
            long traceSequence = databaseTracking ? Interlocked.Increment(ref _databaseTraceSequence) : 0;
            uint characterId = character?.id ?? 0;
            if (databaseTracking)
                Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=begin characterId={characterId} consumedDropId={droppedItemId} consumedGrantId={pendingGrantId} createsDrop={(pendingDrop != null)} createsGrant={(pendingGrant != null)} createsWorldActivation={(pendingWorldActivation != null)} createsModifier={(pendingModifier != null)} appliesPvpResult={(pendingPvp != null)} disconnectSnapshot={(pendingDisconnect != null)} reason={caller ?? "unknown"}");
            if (character == null)
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=rollback characterId=0 reason={caller ?? "unknown"} errorType=ArgumentNullException");
                Debug.LogError("[DB-CHAR] operation=SaveCharacter state=failed message='character is null'");
                return false;
            }
            if (pendingDisconnect == null && HasPendingFullSnapshot(character.id))
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=rollback characterId={character.id} reason={caller ?? "unknown"} errorType=PendingFullSnapshotFence");
                Debug.LogError($"[DB-CHAR] operation=SaveCharacter state=pending characterId={character.id} reason=disconnect-writer-fence caller={caller ?? "unknown"}");
                return false;
            }
            if (pendingDisconnect == null && pendingPvp == null
                && !PvpResultRepository.CanPersistCharacterSnapshot(character))
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=rollback characterId={character.id} reason={caller ?? "unknown"} errorType=PendingPvpResultFence");
                Debug.LogError($"[DB-CHAR] operation=SaveCharacter state=pending characterId={character.id} reason=pvp-result-fence caller={caller ?? "unknown"}");
                return false;
            }
            try
            {
                ValidateCharacterForSave(character);
                using (var connection = GameDatabase.GetConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    EnsureCharacterIdentity(connection, character);
                    if (pendingPvp != null)
                    {
                        PvpParticipantResult expectedPvp = pendingPvp.Result;
                        if (!PvpResultRepository.TryGetParticipant(connection, expectedPvp.ResultId, expectedPvp.ParticipantOrder, out PvpParticipantResult persistedPvp)
                            || !PvpResultRepository.ParticipantPayloadEquals(persistedPvp, expectedPvp))
                            throw new InvalidDataException($"PvP participant payload mismatch resultId={expectedPvp.ResultId} order={expectedPvp.ParticipantOrder}");
                        if (persistedPvp.Applied)
                        {
                            transaction.Rollback();
                            pvpResultAlreadyApplied = true;
                            return true;
                        }
                        using var pvpBaselineCommand = connection.CreateCommand();
                        pvpBaselineCommand.CommandText = @"SELECT c.pvp_wins, c.pvp_rating, a.username
                            FROM characters c JOIN accounts a ON a.id=c.account_id WHERE c.id=@characterId";
                        pvpBaselineCommand.Parameters.AddWithValue("@characterId", (int)expectedPvp.CharacterId);
                        using SqliteDataReader pvpBaselineReader = pvpBaselineCommand.ExecuteReader();
                        if (!pvpBaselineReader.Read()
                            || pvpBaselineReader.GetInt32(0) != expectedPvp.WinsBefore
                            || pvpBaselineReader.GetInt32(1) != expectedPvp.PersistedRatingBefore
                            || !string.Equals(pvpBaselineReader.GetString(2), expectedPvp.LoginName, StringComparison.OrdinalIgnoreCase)
                            || pvpBaselineReader.Read())
                            throw new InvalidDataException($"PvP participant baseline changed resultId={expectedPvp.ResultId} order={expectedPvp.ParticipantOrder}");
                    }
                    if (pendingWorldActivation != null)
                    {
                        GameDatabase.ExecuteNonQuery(connection,
                            @"INSERT OR IGNORE INTO world_entity_activations
                              (activation_key, character_id, zone, zone_id, instance_id, entity_id, outcome_json)
                              VALUES (@key, @characterId, @zone, @zoneId, @instanceId, @entityId, @outcome)",
                            ("@key", pendingWorldActivation.ActivationKey),
                            ("@characterId", (int)character.id),
                            ("@zone", pendingWorldActivation.Zone),
                            ("@zoneId", unchecked((int)pendingWorldActivation.ZoneId)),
                            ("@instanceId", unchecked((int)pendingWorldActivation.InstanceId)),
                            ("@entityId", pendingWorldActivation.EntityId),
                            ("@outcome", pendingWorldActivation.OutcomeJson));
                        int insertedActivation = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                        if (insertedActivation == 0)
                        {
                            transaction.Rollback();
                            worldActivationAlreadyCommitted = true;
                            if (databaseTracking)
                                Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=idempotent characterId={character.id} reason={caller ?? "unknown"} activationEntity={pendingWorldActivation.EntityId} zoneId={pendingWorldActivation.ZoneId} instanceId={pendingWorldActivation.InstanceId}");
                            return true;
                        }
                        if (insertedActivation != 1)
                            throw new InvalidDataException($"World activation '{pendingWorldActivation.ActivationKey}' was not inserted exactly once");
                    }
                    object oldRow = null;
                    if (databaseTracking)
                    {
                        oldRow = GameDatabase.ExecuteScalar(connection,
                            "SELECT printf('level=%d xp=%d current_hp=%d current_mana=%d max_hp=%d max_mana=%d zone=%s', level, experience, current_hp, current_mana, max_hp, max_mana, current_zone) FROM characters WHERE id = @id",
                            ("@id", (int)character.id));
                    }
                    if (character.maxHP <= 0 || character.maxMana <= 0)
                    {
                        object oldMaxHP = GameDatabase.ExecuteScalar(connection, "SELECT max_hp FROM characters WHERE id = @id", ("@id", (int)character.id));
                        object oldMaxMana = GameDatabase.ExecuteScalar(connection, "SELECT max_mana FROM characters WHERE id = @id", ("@id", (int)character.id));
                        bool preservedMaxHP = false;
                        bool preservedMaxMana = false;
                        if (character.maxHP <= 0 && oldMaxHP != null && oldMaxHP != DBNull.Value)
                        {
                            int preserved = Convert.ToInt32(oldMaxHP);
                            if (preserved > 0)
                            {
                                character.maxHP = preserved;
                                preservedMaxHP = true;
                            }
                        }
                        if (character.maxMana <= 0 && oldMaxMana != null && oldMaxMana != DBNull.Value)
                        {
                            int preserved = Convert.ToInt32(oldMaxMana);
                            if (preserved > 0)
                            {
                                character.maxMana = preserved;
                                preservedMaxMana = true;
                            }
                        }
                        if (databaseTracking)
                            Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=preserve-resources characterId={character.id} maxHP={character.maxHP} preservedHP={preservedMaxHP} maxMana={character.maxMana} preservedMana={preservedMaxMana}");
                    }
                    if (databaseTracking)
                        Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=snapshot characterId={character.id} old='{oldRow ?? "<missing>"}' level={character.level} xp={character.experience} gold={character.gold} hp={character.currentHP} mana={character.currentMana} maxHP={character.maxHP} maxMana={character.maxMana} zone={character.currentZoneName ?? ""} positionFixed=({character.positionFixedX},{character.positionFixedY},{character.positionFixedZ}) inventory={character.inventory?.Count ?? 0} skills={character.skills?.Count ?? 0} activeQuests={character.activeQuests?.Count ?? 0} completedQuests={character.completedQuests?.Count ?? 0}");

                    GameDatabase.ExecuteNonQuery(connection, @"
                        UPDATE characters SET
                            level = @lv, experience = @xp, gold = @g,
                            avatar_class = @ac,
                            skin = @sk, face = @fc, face_feature = @ff,
                            hair = @hr, hair_color = @hc,
                            current_zone = @zone, zone_id = @zid,
                            position_x = 0, position_y = 0, position_z = 0,
                            position_fixed_x = @pfx, position_fixed_y = @pfy, position_fixed_z = @pfz,
                            current_hp = @hp, current_mana = @mp,
                            max_hp = @mxhp, max_mana = @mxmp,
                            stat_strength = @str, stat_agility = @agi,
                            stat_intellect = @int, stat_endurance = @end,
                            last_respec_time = @lrt,
                            respec_count = @rsc, pvp_wins = @pvpw, pvp_rating = @pvpr,
                            has_pvp_rating = MAX(has_pvp_rating, @hasPvpRating),
                            monster_difficulty = @difficulty, minimum_item_quality = @minimumQuality,
                            tp_zone = @tpz, tp_zone_id = @tpzid, tp_target_zone = @tptz,
                            tp_pos_x = 0, tp_pos_y = 0, tp_pos_z = 0,
                            tp_pos_fixed_x = @tppx, tp_pos_fixed_y = @tppy, tp_pos_fixed_z = @tppz,
                            posse_id = @pid, posse_rank_id = @prank, posse_join_cooldown = @pcd
                        WHERE id = @id AND account_id = @accountId AND name = @name COLLATE NOCASE",
                        ("@id", (int)character.id), ("@accountId", (int)character.accountId), ("@name", character.name),
                        ("@lv", (int)character.level), ("@xp", (int)character.experience),
                        ("@g", (int)character.gold), ("@ac", character.avatarClass ?? ""),
                        ("@sk", (int)character.skin), ("@fc", (int)character.face),
                        ("@ff", (int)character.faceFeature), ("@hr", (int)character.hair), ("@hc", (int)character.hairColor),
                        ("@zone", character.currentZoneName ?? "tutorial"), ("@zid", character.zoneId),
                        ("@pfx", character.positionFixedX), ("@pfy", character.positionFixedY), ("@pfz", character.positionFixedZ),
                        ("@hp", (int)character.currentHP), ("@mp", (int)character.currentMana),
                        ("@mxhp", character.maxHP), ("@mxmp", character.maxMana),
                        ("@str", character.statStrength), ("@agi", character.statAgility),
                        ("@int", character.statIntellect), ("@end", character.statEndurance),
                        ("@lrt", character.lastRespecTime),
                        ("@rsc", character.respecCount), ("@pvpw", character.pvpWins), ("@pvpr", character.pvpRating),
                        ("@hasPvpRating", character.hasPvpRating || pendingPvp?.Result.ApplyRating == true ? 1 : 0),
                        ("@difficulty", (int)character.monsterDifficulty), ("@minimumQuality", character.minimumItemQuality),
                        ("@tpz", character.tpZone ?? ""), ("@tpzid", character.tpZoneId),
                        ("@tptz", character.tpTargetZone ?? ""),
                        ("@tppx", character.tpPosFixedX), ("@tppy", character.tpPosFixedY), ("@tppz", character.tpPosFixedZ),
                        ("@pid", (int)character.posseId), ("@prank", character.posseRankId), ("@pcd", character.posseJoinCooldown));
                    int updatedCharacter = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                    if (updatedCharacter != 1)
                        throw new InvalidDataException($"Character {character.id} was not updated exactly once");

                    GameDatabase.ExecuteNonQuery(connection, "DELETE FROM character_equipment WHERE character_id = @cid", ("@cid", (int)character.id));
                    if (character.equipment != null)
                    {
                        var rarityBySlot = character.equipment.slotRarity ?? new Dictionary<string, int>();
                        var levelBySlot = character.equipment.slotLevel ?? new Dictionary<string, int>();
                        int slotRarity; int slotLevel;
                        InsertEquipment(connection, (int)character.id, "weapon", character.equipment.weapon, rarityBySlot.TryGetValue("weapon", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("weapon", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "weapon"));
                        InsertEquipment(connection, (int)character.id, "armor", character.equipment.armor, rarityBySlot.TryGetValue("armor", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("armor", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "armor"));
                        InsertEquipment(connection, (int)character.id, "helmet", character.equipment.helmet, rarityBySlot.TryGetValue("helmet", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("helmet", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "helmet"));
                        InsertEquipment(connection, (int)character.id, "gloves", character.equipment.gloves, rarityBySlot.TryGetValue("gloves", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("gloves", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "gloves"));
                        InsertEquipment(connection, (int)character.id, "boots", character.equipment.boots, rarityBySlot.TryGetValue("boots", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("boots", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "boots"));
                        InsertEquipment(connection, (int)character.id, "shoulders", character.equipment.shoulders ?? "", rarityBySlot.TryGetValue("shoulders", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("shoulders", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "shoulders"));
                        InsertEquipment(connection, (int)character.id, "shield", character.equipment.shield ?? "", rarityBySlot.TryGetValue("shield", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("shield", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "shield"));
                        InsertEquipment(connection, (int)character.id, "ring1", character.equipment.ring1 ?? "", rarityBySlot.TryGetValue("ring1", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("ring1", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "ring1"));
                        InsertEquipment(connection, (int)character.id, "ring2", character.equipment.ring2 ?? "", rarityBySlot.TryGetValue("ring2", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("ring2", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "ring2"));
                        InsertEquipment(connection, (int)character.id, "amulet", character.equipment.amulet ?? "", rarityBySlot.TryGetValue("amulet", out slotRarity) ? slotRarity : -1, levelBySlot.TryGetValue("amulet", out slotLevel) ? slotLevel : -1, GetEquipmentState(character.equipment, "amulet"));
                    }

                    GameDatabase.ExecuteNonQuery(connection, "DELETE FROM character_inventory WHERE character_id = @cid", ("@cid", (int)character.id));
                    if (character.inventory != null)
                    {
                        foreach (var item in character.inventory)
                        {
                            SavedItemRuntimeState itemState = item.itemState ?? new SavedItemRuntimeState();
                            GameDatabase.ExecuteNonQuery(connection,
                                "INSERT INTO character_inventory (character_id, gc_class, slot_x, slot_y, count, buy_price, rarity, stored_level, container_id, item_order, preset_scale_mod, has_generated_item_state, rolled_requires_membership, generated_requires_membership, soul_bound, no_sell, soul_bound_countdown, generated_item_modifiers) VALUES (@cid, @gc, @x, @y, @c, @bp, @r, @sl, @cont, @ord, @psm, @hgs, @rrm, @grm, @sb, @ns, @sbc, @gim)",
                                ("@cid", (int)character.id), ("@gc", item.gcClass), ("@x", (int)item.x), ("@y", (int)item.y), ("@c", item.count), ("@bp", (int)item.buyPrice), ("@r", item.rarity), ("@sl", item.storedLevel), ("@cont", (int)item.containerId),
                                ("@ord", item.itemOrder), ("@psm", itemState.presetScaleMod ?? ""), ("@hgs", itemState.hasGeneratedItemState ? 1 : 0), ("@rrm", itemState.rolledRequiresMembership ? 1 : 0), ("@grm", itemState.generatedRequiresMembership ? 1 : 0), ("@sb", itemState.soulBound ? 1 : 0), ("@ns", itemState.noSell ? 1 : 0), ("@sbc", itemState.soulBoundCountdown), ("@gim", itemState.SerializeModifiers()));
                        }
                    }

                    GameDatabase.ExecuteNonQuery(connection, "DELETE FROM character_skills WHERE character_id = @cid", ("@cid", (int)character.id));
                    if (character.skills != null)
                    {
                        foreach (var skill in character.skills)
                        {
                            int level = character.GetSkillLevel(skill);
                            int hotbar = -1;
                            if (character.hotbarSlots != null)
                            {
                                foreach (var hotbarSlotEntry in character.hotbarSlots)
                                    if (string.Equals(hotbarSlotEntry.skill, skill, StringComparison.OrdinalIgnoreCase)) { hotbar = (int)hotbarSlotEntry.slot; break; }
                            }
                            GameDatabase.ExecuteNonQuery(connection,
                                "INSERT INTO character_skills (character_id, skill_gc_class, level, hotbar_slot) VALUES (@cid, @s, @l, @h)",
                                ("@cid", (int)character.id), ("@s", skill), ("@l", level), ("@h", hotbar));
                        }
                    }

                    GameDatabase.ExecuteNonQuery(connection, "DELETE FROM character_quests WHERE character_id = @cid AND status = 'active'", ("@cid", (int)character.id));
                    GameDatabase.ExecuteNonQuery(connection, "DELETE FROM quest_objectives WHERE character_id = @cid", ("@cid", (int)character.id));
                    if (character.activeQuests != null)
                    {
                        foreach (var quest in character.activeQuests)
                        {
                            GameDatabase.ExecuteNonQuery(connection,
                                "INSERT OR REPLACE INTO character_quests (character_id, quest_id, quest_giver_id, accepted_at, status) VALUES (@cid, @qid, @gid, @at, 'active')",
                                ("@cid", (int)character.id), ("@qid", quest.questId), ("@gid", quest.questGiverId ?? ""), ("@at", quest.acceptedAt ?? ""));

                            if (quest.objectives != null)
                            {
                                foreach (var objective in quest.objectives)
                                {
                                    GameDatabase.ExecuteNonQuery(connection,
                                        "INSERT INTO quest_objectives (character_id, quest_id, objective_name, type, target, label, required, current) VALUES (@cid, @qid, @on, @t, @tgt, @lb, @req, @cur)",
                                        ("@cid", (int)character.id), ("@qid", quest.questId), ("@on", objective.objectiveName ?? ""),
                                        ("@t", objective.type ?? ""), ("@tgt", objective.target ?? ""), ("@lb", objective.label ?? ""),
                                        ("@req", objective.required), ("@cur", objective.current));
                                }
                            }
                        }
                    }

                    GameDatabase.ExecuteNonQuery(connection, "DELETE FROM completed_quests WHERE character_id = @cid", ("@cid", (int)character.id));
                    if (character.completedQuests != null)
                    {
                        foreach (var questId in character.completedQuests)
                        {
                            GameDatabase.ExecuteNonQuery(connection,
                                "INSERT OR IGNORE INTO completed_quests (character_id, quest_id, completed_at) VALUES (@cid, @qid, datetime(@at, 'unixepoch'))",
                                ("@cid", (int)character.id), ("@qid", questId),
                                ("@at", character.questCompletionTimes != null && character.questCompletionTimes.TryGetValue(questId, out long timestamp) ? timestamp : 0L));
                        }
                    }

                    GameDatabase.ExecuteNonQuery(connection, "DELETE FROM character_checkpoints WHERE character_id = @cid", ("@cid", (int)character.id));
                    if (character.unlockedCheckpoints != null)
                    {
                        foreach (var checkpointId in character.unlockedCheckpoints)
                        {
                            GameDatabase.ExecuteNonQuery(connection,
                                "INSERT OR IGNORE INTO character_checkpoints (character_id, checkpoint_id) VALUES (@cid, @cp)",
                                ("@cid", (int)character.id), ("@cp", checkpointId));
                        }
                    }

                    if (droppedItemId > 0)
                    {
                        GameDatabase.ExecuteNonQuery(connection,
                            "DELETE FROM dropped_items WHERE id = @id",
                            ("@id", droppedItemId));
                        int deletedDrop = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                        if (deletedDrop != 1)
                            throw new InvalidDataException($"Persistent drop {droppedItemId} was not consumed exactly once");
                    }

                    if (pendingGrantId > 0)
                    {
                        GameDatabase.ExecuteNonQuery(connection,
                            "DELETE FROM pending_item_grants WHERE id = @id AND character_id = @characterId",
                            ("@id", pendingGrantId), ("@characterId", (int)character.id));
                        int deletedGrant = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                        if (deletedGrant != 1)
                            throw new InvalidDataException($"Pending item grant {pendingGrantId} was not consumed exactly once");
                    }

                    long pendingDroppedItemId = 0;
                    if (pendingDrop != null)
                    {
                        SavedItemRuntimeState itemState = SavedItemRuntimeState.Capture(pendingDrop.Item);
                        GameDatabase.ExecuteNonQuery(connection,
                            @"INSERT INTO dropped_items (zone, zone_id, instance_id, gc_class, dfc_class, pos_x, pos_y, pos_z, pos_fixed_x, pos_fixed_y, pos_fixed_z, player_level, quantity, target_slot, preset_scale_mod, has_generated_item_state, rolled_requires_membership, generated_requires_membership, soul_bound, no_sell, soul_bound_countdown, generated_item_modifiers, dropped_by, rarity, stored_level, owner_character_id, owner_group_id, owner_name)
                              VALUES (@zone, @zid, @iid, @gc, @nc, 0, 0, 0, @pfx, @pfy, @pfz, @pl, @qty, @ts, @psm, @hgs, @rrm, @grm, @sb, @ns, @sbc, @gim, @db, @rar, @slv, @ocid, @ogid, @oname)",
                            ("@zone", pendingDrop.Zone),
                            ("@zid", unchecked((int)pendingDrop.ZoneId)),
                            ("@iid", unchecked((int)pendingDrop.InstanceId)),
                            ("@gc", pendingDrop.Item.GCClass),
                            ("@nc", pendingDrop.Item.DFCClass ?? "Armor"),
                            ("@pfx", pendingDrop.PosFixedX),
                            ("@pfy", pendingDrop.PosFixedY),
                            ("@pfz", pendingDrop.PosFixedZ),
                            ("@pl", pendingDrop.PlayerLevel),
                            ("@qty", pendingDrop.Quantity),
                            ("@ts", unchecked((int)(pendingDrop.Item.TargetSlot ?? 0xFFFFFFFF))),
                            ("@psm", itemState.presetScaleMod ?? ""),
                            ("@hgs", itemState.hasGeneratedItemState ? 1 : 0),
                            ("@rrm", itemState.rolledRequiresMembership ? 1 : 0),
                            ("@grm", itemState.generatedRequiresMembership ? 1 : 0),
                            ("@sb", itemState.soulBound ? 1 : 0),
                            ("@ns", itemState.noSell ? 1 : 0),
                            ("@sbc", itemState.soulBoundCountdown),
                            ("@gim", itemState.SerializeModifiers()),
                            ("@db", pendingDrop.DroppedBy),
                            ("@rar", pendingDrop.Item.GetEffectiveRarity()),
                            ("@slv", pendingDrop.Item.StoredLevel),
                            ("@ocid", (int)pendingDrop.OwnerCharacterId),
                            ("@ogid", (int)pendingDrop.OwnerGroupId),
                            ("@oname", pendingDrop.OwnerName));
                        pendingDroppedItemId = Convert.ToInt64(GameDatabase.ExecuteScalar(connection, "SELECT last_insert_rowid()") ?? 0);
                        if (pendingDroppedItemId <= 0)
                            throw new InvalidDataException("Persistent drop insert did not return an identity");
                    }

                    long pendingItemGrantId = 0;
                    if (pendingGrant != null)
                    {
                        GameDatabase.ExecuteNonQuery(connection,
                            "INSERT INTO pending_item_grants(character_id, gc_class, count, width, height, rarity) VALUES(@characterId,@gcClass,@count,@width,@height,@rarity)",
                            ("@characterId", (int)character.id),
                            ("@gcClass", pendingGrant.GcClass),
                            ("@count", pendingGrant.Count),
                            ("@width", pendingGrant.Width),
                            ("@height", pendingGrant.Height),
                            ("@rarity", pendingGrant.Rarity));
                        pendingItemGrantId = Convert.ToInt64(GameDatabase.ExecuteScalar(connection, "SELECT last_insert_rowid()") ?? 0);
                        if (pendingItemGrantId <= 0)
                            throw new InvalidDataException("Pending item grant insert did not return an identity");
                    }

                    var pendingWorldDropIds = new List<long>();
                    if (pendingWorldActivation != null)
                    {
                        for (int dropIndex = 0; dropIndex < pendingWorldActivation.Drops.Count; dropIndex++)
                        {
                            WorldActivationDropWrite drop = pendingWorldActivation.Drops[dropIndex];
                            SavedItemRuntimeState itemState = SavedItemRuntimeState.Capture(drop.Item);
                            GameDatabase.ExecuteNonQuery(connection,
                                @"INSERT INTO dropped_items (zone, zone_id, instance_id, gc_class, dfc_class, pos_x, pos_y, pos_z, pos_fixed_x, pos_fixed_y, pos_fixed_z, heading_fixed, player_level, quantity, gold_amount, target_slot, preset_scale_mod, has_generated_item_state, rolled_requires_membership, generated_requires_membership, soul_bound, no_sell, soul_bound_countdown, generated_item_modifiers, dropped_by, rarity, stored_level, owner_character_id, owner_group_id, owner_name, quest_binding_key, activation_key)
                                  VALUES (@zone, @zid, @iid, @gc, @nc, 0, 0, 0, @pfx, @pfy, @pfz, @heading, @pl, @qty, @gold, @ts, @psm, @hgs, @rrm, @grm, @sb, @ns, @sbc, @gim, @db, @rar, @slv, @ocid, @ogid, @oname, @qbk, @activationKey)",
                                ("@zone", drop.Zone),
                                ("@zid", unchecked((int)drop.ZoneId)),
                                ("@iid", unchecked((int)drop.InstanceId)),
                                ("@gc", drop.Item.GCClass),
                                ("@nc", drop.Item.DFCClass ?? "Armor"),
                                ("@pfx", drop.PosFixedX),
                                ("@pfy", drop.PosFixedY),
                                ("@pfz", drop.PosFixedZ),
                                ("@heading", drop.HeadingFixed),
                                ("@pl", drop.PlayerLevel),
                                ("@qty", drop.Quantity),
                                ("@gold", unchecked((int)drop.GoldAmount)),
                                ("@ts", unchecked((int)(drop.Item.TargetSlot ?? 0xFFFFFFFF))),
                                ("@psm", itemState.presetScaleMod ?? ""),
                                ("@hgs", itemState.hasGeneratedItemState ? 1 : 0),
                                ("@rrm", itemState.rolledRequiresMembership ? 1 : 0),
                                ("@grm", itemState.generatedRequiresMembership ? 1 : 0),
                                ("@sb", itemState.soulBound ? 1 : 0),
                                ("@ns", itemState.noSell ? 1 : 0),
                                ("@sbc", itemState.soulBoundCountdown),
                                ("@gim", itemState.SerializeModifiers()),
                                ("@db", drop.DroppedBy ?? ""),
                                ("@rar", drop.Item.GetEffectiveRarity()),
                                ("@slv", drop.Item.StoredLevel),
                                ("@ocid", (int)drop.OwnerCharacterId),
                                ("@ogid", (int)drop.OwnerGroupId),
                                ("@oname", drop.OwnerName ?? ""),
                                ("@qbk", drop.QuestBindingKey),
                                ("@activationKey", pendingWorldActivation.ActivationKey));
                            long worldDropId = Convert.ToInt64(GameDatabase.ExecuteScalar(connection, "SELECT last_insert_rowid()") ?? 0);
                            if (worldDropId <= 0)
                                throw new InvalidDataException($"World activation drop {dropIndex} did not return an identity");
                            pendingWorldDropIds.Add(worldDropId);
                        }
                    }

                    if (pendingModifier != null)
                    {
                        GameDatabase.ExecuteNonQuery(
                            connection,
                            "DELETE FROM character_modifiers WHERE character_id=@cid AND modifier_id=@mid",
                            ("@cid", (int)character.id),
                            ("@mid", (int)pendingModifier.ModifierId));
                        if (pendingModifier.ReplaceSameType)
                            GameDatabase.ExecuteNonQuery(
                                connection,
                                "DELETE FROM character_modifiers WHERE character_id=@cid AND gc_type=@gc COLLATE NOCASE",
                                ("@cid", (int)character.id),
                                ("@gc", pendingModifier.GcType));
                        if (pendingModifier.ReplaceGcTypes != null)
                            for (int replaceIndex = 0; replaceIndex < pendingModifier.ReplaceGcTypes.Count; replaceIndex++)
                                GameDatabase.ExecuteNonQuery(
                                    connection,
                                    "DELETE FROM character_modifiers WHERE character_id=@cid AND gc_type=@gc COLLATE NOCASE",
                                    ("@cid", (int)character.id),
                                    ("@gc", pendingModifier.ReplaceGcTypes[replaceIndex]));
                        GameDatabase.ExecuteNonQuery(
                            connection,
                            @"INSERT INTO character_modifiers
                              (character_id, gc_type, modifier_id, level, power_level, duration_remaining, source_is_self)
                              VALUES (@cid, @gc, @mid, @level, @power, @duration, @sourceIsSelf)",
                            ("@cid", (int)character.id),
                            ("@gc", pendingModifier.GcType),
                            ("@mid", (int)pendingModifier.ModifierId),
                            ("@level", (int)pendingModifier.Level),
                            ("@power", (int)pendingModifier.PowerLevel),
                            ("@duration", (int)pendingModifier.DurationRemaining),
                            ("@sourceIsSelf", (int)pendingModifier.SourceIsSelf));
                        int insertedModifier = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                        if (insertedModifier != 1)
                            throw new InvalidDataException($"Character modifier '{pendingModifier.GcType}' was not inserted exactly once");
                    }

                    if (pendingDisconnect != null)
                    {
                        for (int durationIndex = 0; durationIndex < pendingDisconnect.ModifierDurations.Count; durationIndex++)
                        {
                            KeyValuePair<uint, uint> duration = pendingDisconnect.ModifierDurations[durationIndex];
                            GameDatabase.ExecuteNonQuery(
                                connection,
                                "UPDATE character_modifiers SET duration_remaining=@duration WHERE character_id=@cid AND modifier_id=@mid",
                                ("@duration", (int)duration.Value),
                                ("@cid", (int)character.id),
                                ("@mid", (int)duration.Key));
                            int affectedDuration = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                            if (affectedDuration != 1)
                                throw new InvalidDataException($"Disconnect modifier duration update affected an invalid row count characterId={character.id} modifierId={duration.Key} count={affectedDuration}");
                        }

                        object currentSelectionValue = GameDatabase.ExecuteScalar(
                            connection,
                            "SELECT current_character_id FROM accounts WHERE username=@login COLLATE NOCASE",
                            ("@login", pendingDisconnect.Login));
                        if (currentSelectionValue == null || currentSelectionValue == DBNull.Value)
                            throw new InvalidDataException($"Disconnect account selection row is missing login='{pendingDisconnect.Login}'");
                        long currentSelection = Convert.ToInt64(currentSelectionValue);
                        if (currentSelection != 0 && currentSelection != character.id)
                            throw new InvalidDataException($"Disconnect account selection changed login='{pendingDisconnect.Login}' expected={character.id} actual={currentSelection}");
                        if (currentSelection == character.id)
                        {
                            GameDatabase.ExecuteNonQuery(
                                connection,
                                "UPDATE accounts SET current_character_id=0 WHERE username=@login COLLATE NOCASE AND current_character_id=@cid",
                                ("@login", pendingDisconnect.Login),
                                ("@cid", (int)character.id));
                            int clearedSelection = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                            if (clearedSelection != 1)
                                throw new InvalidDataException($"Disconnect account selection was not cleared exactly once login='{pendingDisconnect.Login}' characterId={character.id}");
                        }
                    }

                    if (pendingPvp != null)
                    {
                        PvpParticipantResult pvpResult = pendingPvp.Result;
                        if ((pvpResult.RewardCount > 0) != (pendingItemGrantId > 0))
                            throw new InvalidDataException($"PvP reward grant state mismatch resultId={pvpResult.ResultId} order={pvpResult.ParticipantOrder}");
                        GameDatabase.ExecuteNonQuery(
                            connection,
                            @"UPDATE pvp_participant_results
                              SET applied_at=datetime('now'), pending_grant_id=@pendingGrantId
                              WHERE result_id=@resultId AND participant_order=@participantOrder
                                AND character_id=@characterId AND applied_at IS NULL",
                            ("@pendingGrantId", pendingItemGrantId),
                            ("@resultId", pvpResult.ResultId),
                            ("@participantOrder", pvpResult.ParticipantOrder),
                            ("@characterId", (int)pvpResult.CharacterId));
                        int appliedPvpResult = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                        if (appliedPvpResult != 1)
                            throw new InvalidDataException($"PvP participant result was not applied exactly once resultId={pvpResult.ResultId} order={pvpResult.ParticipantOrder}");
                    }

                    transaction.Commit();
                    createdDroppedItemId = pendingDroppedItemId;
                    createdPendingGrantId = pendingItemGrantId;
                    createdWorldDropIds = pendingWorldDropIds;
                    if (databaseTracking)
                        Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=commit characterId={character.id} reason={caller ?? "unknown"}");
                    Debug.LogError($"[DB-CHAR] Saved id={character.id} lv={character.level} xp={character.experience} caller={caller ?? "unknown"} hp={character.currentHP}/{character.maxHP} mana={character.currentMana}/{character.maxMana}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (databaseTracking)
                    Debug.LogError($"[DB-TRACK] sequence={traceSequence} operation=save-character phase=rollback characterId={character.id} reason={caller ?? "unknown"} errorType={ex.GetType().Name}");
                Debug.LogError($"[DB-CHAR] operation=SaveCharacter state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
                return false;
            }
        }


        private static void EnsureCharacterRowsDeleted(SqliteConnection connection, uint characterId)
        {
            string[] queries =
            {
                "SELECT COUNT(*) FROM character_equipment WHERE character_id=@id",
                "SELECT COUNT(*) FROM character_inventory WHERE character_id=@id",
                "SELECT COUNT(*) FROM character_skills WHERE character_id=@id",
                "SELECT COUNT(*) FROM character_quests WHERE character_id=@id",
                "SELECT COUNT(*) FROM quest_objectives WHERE character_id=@id",
                "SELECT COUNT(*) FROM completed_quests WHERE character_id=@id",
                "SELECT COUNT(*) FROM character_checkpoints WHERE character_id=@id",
                "SELECT COUNT(*) FROM character_modifiers WHERE character_id=@id",
                "SELECT COUNT(*) FROM world_entity_activations WHERE character_id=@id",
                "SELECT COUNT(*) FROM pending_item_grants WHERE character_id=@id",
                "SELECT COUNT(*) FROM pending_admin_actions WHERE character_id=@id",
                "SELECT COUNT(*) FROM pvp_participant_results WHERE character_id=@id",
                "SELECT COUNT(*) FROM dropped_items WHERE owner_character_id=@id"
            };
            int remaining = 0;
            foreach (string query in queries)
                remaining += Convert.ToInt32(GameDatabase.ExecuteScalar(connection, query, ("@id", (int)characterId)) ?? 0);
            if (remaining != 0)
                throw new InvalidDataException($"Character {characterId} still owns {remaining} runtime rows after delete");
        }

        public static bool DeleteCharacter(uint characterId, uint expectedAccountId = 0, string expectedName = null)
        {
            if (HasPendingFullSnapshot(characterId)
                || (HasPendingPvpJournalFence != null && HasPendingPvpJournalFence(characterId))
                || (HasAnyPendingPvpJournalFence != null && HasAnyPendingPvpJournalFence()))
                return false;
            try
            {
                using (var connection = GameDatabase.GetConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    int pendingPvpResults = Convert.ToInt32(GameDatabase.ExecuteScalar(
                        connection,
                        @"SELECT COUNT(*) FROM pvp_participant_results pending
                          WHERE pending.result_id IN (
                              SELECT result_id FROM pvp_participant_results WHERE character_id=@id)
                            AND (pending.applied_at IS NULL OR pending.return_completed_at IS NULL)",
                        ("@id", (int)characterId)) ?? 0);
                    if (pendingPvpResults != 0)
                        return false;
                    uint persistedAccountId = 0;
                    string characterName = "";
                    uint posseId = 0;
                    bool isFounder = false;
                    using (var reader = GameDatabase.ExecuteReader(connection,
                        @"SELECT c.account_id, c.name, COALESCE(c.posse_id, 0), CASE WHEN p.founder_character_id = c.id THEN 1 ELSE 0 END
                          FROM characters c
                          LEFT JOIN posses p ON p.id = c.posse_id
                          WHERE c.id = @id",
                        ("@id", (int)characterId)))
                    {
                        if (!reader.Read())
                        {
                            Debug.LogError($"[DB-CHAR] action=delete state=missing id={characterId}");
                            return false;
                        }
                        persistedAccountId = checked((uint)reader.GetInt32(0));
                        characterName = reader.GetString(1);
                        posseId = (uint)reader.GetInt32(2);
                        isFounder = reader.GetInt32(3) != 0;
                    }
                    if ((expectedAccountId != 0 && persistedAccountId != expectedAccountId)
                        || (expectedName != null && !string.Equals(characterName, expectedName, StringComparison.OrdinalIgnoreCase)))
                    {
                        Debug.LogError($"[DB-CHAR] action=delete state=identity-mismatch id={characterId} account={persistedAccountId} name='{characterName}'");
                        return false;
                    }

                    if (posseId != 0)
                    {
                        if (isFounder)
                        {
                            int pendingPossePvpResults = Convert.ToInt32(GameDatabase.ExecuteScalar(
                                connection,
                                @"SELECT COUNT(*) FROM pvp_participant_results pending
                                  JOIN characters member ON member.id=pending.character_id
                                  WHERE member.posse_id=@pid
                                    AND (pending.applied_at IS NULL OR pending.return_completed_at IS NULL)",
                                ("@pid", (int)posseId)) ?? 0);
                            if (pendingPossePvpResults != 0)
                                return false;
                            using (var reader = GameDatabase.ExecuteReader(connection,
                                "SELECT id FROM characters WHERE posse_id = @pid ORDER BY id",
                                ("@pid", (int)posseId)))
                            {
                                while (reader.Read())
                                {
                                    uint memberCharacterId = checked((uint)reader.GetInt32(0));
                                    if (memberCharacterId != characterId && HasPendingFullSnapshot(memberCharacterId))
                                        return false;
                                }
                            }
                            GameDatabase.ExecuteNonQuery(connection,
                                "UPDATE characters SET posse_id = 0, posse_rank_id = 0 WHERE posse_id = @pid",
                                ("@pid", (int)posseId));
                            GameDatabase.ExecuteNonQuery(connection,
                                "DELETE FROM posses WHERE id = @pid",
                                ("@pid", (int)posseId));
                            Debug.LogError($"[DB-CHAR] action=disbandFoundedPosse posseId={posseId} characterId={characterId}");
                        }
                        else
                        {
                            GameDatabase.ExecuteNonQuery(connection,
                                "UPDATE characters SET posse_id = 0, posse_rank_id = 0 WHERE id = @id",
                                ("@id", (int)characterId));
                        }
                    }

                    if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection,
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='social_friends_v2'")) != 0)
                    {
                        GameDatabase.ExecuteNonQuery(connection,
                            "DELETE FROM social_friends_v2 WHERE character_name = @name OR friend_name = @name",
                            ("@name", characterName));
                    }
                    if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection,
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='social_ignores_v2'")) != 0)
                    {
                        GameDatabase.ExecuteNonQuery(connection,
                            "DELETE FROM social_ignores_v2 WHERE character_name = @name OR ignore_name = @name",
                            ("@name", characterName));
                    }

                    GameDatabase.ExecuteNonQuery(connection,
                        "UPDATE accounts SET current_character_id = 0 WHERE current_character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM character_modifiers WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM pending_item_grants WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM pending_admin_actions WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM dropped_items WHERE owner_character_id = @id",
                        ("@id", (int)characterId));
                    var pvpResultIds = new List<string>();
                    using (var reader = GameDatabase.ExecuteReader(
                        connection,
                        "SELECT DISTINCT result_id FROM pvp_participant_results WHERE character_id=@id ORDER BY result_id",
                        ("@id", (int)characterId)))
                    {
                        while (reader.Read())
                            pvpResultIds.Add(reader.GetString(0));
                    }
                    foreach (string resultId in pvpResultIds)
                    {
                        GameDatabase.ExecuteNonQuery(connection,
                            "DELETE FROM pvp_participant_results WHERE result_id=@resultId",
                            ("@resultId", resultId));
                        GameDatabase.ExecuteNonQuery(connection,
                            "DELETE FROM pvp_match_results WHERE result_id=@resultId",
                            ("@resultId", resultId));
                    }
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM character_equipment WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM character_inventory WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM character_skills WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM quest_objectives WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM character_quests WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM completed_quests WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM character_checkpoints WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM world_entity_activations WHERE character_id = @id",
                        ("@id", (int)characterId));
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM characters WHERE id = @id",
                        ("@id", (int)characterId));
                    int deleted = Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0);
                    if (deleted != 1)
                        throw new InvalidDataException($"Character {characterId} was not deleted exactly once");
                    EnsureCharacterRowsDeleted(connection, characterId);
                    transaction.Commit();
                    Debug.LogError($"[DB-CHAR] action=delete id={characterId} deleted={deleted}");
                    return deleted != 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] operation=Delete state=failed message='{ex.Message}'");
                return false;
            }
        }


        private static SavedCharacter ReadCharacterRow(SqliteDataReader r)
        {
            string className = GameDatabase.GetString(r, "class_name");
            if (string.IsNullOrWhiteSpace(className))
                throw new InvalidDataException("Character row is missing authored class_name.");
            string currentZoneName = GameDatabase.GetString(r, "current_zone");
            if (string.IsNullOrWhiteSpace(currentZoneName))
                throw new InvalidDataException("Character row is missing authored current_zone.");
            var character = new SavedCharacter
            {
                id = (uint)GameDatabase.GetInt(r, "id"),
                accountId = (uint)GameDatabase.GetInt(r, "account_id"),
                name = GameDatabase.GetString(r, "name"),
                className = className,
                avatarClass = GameDatabase.GetString(r, "avatar_class"),
                level = (byte)GameDatabase.GetInt(r, "level", 1),
                experience = (uint)GameDatabase.GetInt(r, "experience"),
                gold = (uint)GameDatabase.GetInt(r, "gold", 100),
                skin = (byte)GameDatabase.GetInt(r, "skin"),
                face = (byte)GameDatabase.GetInt(r, "face"),
                faceFeature = (byte)GameDatabase.GetInt(r, "face_feature"),
                hair = (byte)GameDatabase.GetInt(r, "hair"),
                hairColor = (byte)GameDatabase.GetInt(r, "hair_color"),
                zoneId = GameDatabase.GetInt(r, "zone_id"),
                currentZoneName = currentZoneName,
                positionFixedX = GameDatabase.GetInt(r, "position_fixed_x"),
                positionFixedY = GameDatabase.GetInt(r, "position_fixed_y"),
                positionFixedZ = GameDatabase.GetInt(r, "position_fixed_z"),
                currentHP = (uint)GameDatabase.GetInt(r, "current_hp"),
                currentMana = (uint)GameDatabase.GetInt(r, "current_mana"),
                maxHP = GameDatabase.GetInt(r, "max_hp"),
                maxMana = GameDatabase.GetInt(r, "max_mana"),
                tpZone = GameDatabase.GetString(r, "tp_zone", ""),
                tpZoneId = GameDatabase.GetInt(r, "tp_zone_id"),
                tpTargetZone = GameDatabase.GetString(r, "tp_target_zone", ""),
                tpPosFixedX = GameDatabase.GetInt(r, "tp_pos_fixed_x"),
                tpPosFixedY = GameDatabase.GetInt(r, "tp_pos_fixed_y"),
                tpPosFixedZ = GameDatabase.GetInt(r, "tp_pos_fixed_z"),
                statStrength = GameDatabase.GetInt(r, "stat_strength"),
                statAgility = GameDatabase.GetInt(r, "stat_agility"),
                statIntellect = GameDatabase.GetInt(r, "stat_intellect"),
                statEndurance = GameDatabase.GetInt(r, "stat_endurance"),
                lastRespecTime = GameDatabase.GetInt(r, "last_respec_time"),
                respecCount = GameDatabase.GetInt(r, "respec_count"),
                pvpWins = GameDatabase.GetInt(r, "pvp_wins"),
                pvpRating = GameDatabase.GetInt(r, "pvp_rating"),
                hasPvpRating = GameDatabase.GetInt(r, "has_pvp_rating") != 0,
                monsterDifficulty = checked((byte)GameDatabase.GetInt(r, "monster_difficulty")),
                minimumItemQuality = GameDatabase.GetInt(r, "minimum_item_quality", 1),
                posseId = GameDatabase.GetUInt(r, "posse_id"),
                posseJoinCooldown = GameDatabase.GetInt(r, "posse_join_cooldown"),
                posseRankId = GameDatabase.GetInt(r, "posse_rank_id"),
            };
            return character;
        }

        private static SavedItemRuntimeState GetEquipmentState(StartingEquipment equipment, string slot)
        {
            if (equipment?.slotItemState != null && equipment.slotItemState.TryGetValue(slot, out SavedItemRuntimeState state) && state != null)
                return state;
            return new SavedItemRuntimeState();
        }

        private static void InsertEquipment(SqliteConnection connection, int charId, string slot, string gcClass, int rarity = -1, int storedLevel = -1, SavedItemRuntimeState itemState = null)
        {
            if (string.IsNullOrWhiteSpace(gcClass))
                return;
            itemState ??= new SavedItemRuntimeState();
            GameDatabase.ExecuteNonQuery(connection,
                "INSERT INTO character_equipment (character_id, slot, gc_class, rarity, stored_level, preset_scale_mod, has_generated_item_state, rolled_requires_membership, generated_requires_membership, soul_bound, no_sell, soul_bound_countdown, generated_item_modifiers) VALUES (@cid, @s, @gc, @r, @sl, @psm, @hgs, @rrm, @grm, @sb, @ns, @sbc, @gim)",
                ("@cid", charId), ("@s", slot), ("@gc", gcClass ?? ""), ("@r", rarity), ("@sl", storedLevel),
                ("@psm", itemState.presetScaleMod ?? ""), ("@hgs", itemState.hasGeneratedItemState ? 1 : 0), ("@rrm", itemState.rolledRequiresMembership ? 1 : 0), ("@grm", itemState.generatedRequiresMembership ? 1 : 0), ("@sb", itemState.soulBound ? 1 : 0), ("@ns", itemState.noSell ? 1 : 0), ("@sbc", itemState.soulBoundCountdown), ("@gim", itemState.SerializeModifiers()));
        }

        private static void InsertStartingEquipment(SqliteConnection connection, int charId, StartingEquipment equipment, string slot, string gcClass)
        {
            InsertEquipment(connection, charId, slot, gcClass ?? "", -1, StartingSlotLevel(equipment, slot));
        }

        private static StartingEquipment LoadEquipment(SqliteConnection connection, int charId)
        {
            var equipment = new StartingEquipment();
            using (var reader = GameDatabase.ExecuteReader(connection,
                "SELECT slot, gc_class, COALESCE(rarity, -1), COALESCE(stored_level, -1), COALESCE(preset_scale_mod, ''), COALESCE(has_generated_item_state, 0), COALESCE(rolled_requires_membership, 0), COALESCE(generated_requires_membership, 0), COALESCE(soul_bound, 0), COALESCE(no_sell, 0), COALESCE(soul_bound_countdown, 65535), COALESCE(generated_item_modifiers, '[]') FROM character_equipment WHERE character_id = @cid",
                ("@cid", charId)))
            {
                while (reader.Read())
                {
                    string slot = reader.GetString(0);
                    string gcClass = reader.GetString(1);
                    int rarity = reader.GetInt32(2);
                    int storedLevel = reader.GetInt32(3);
                    string presetScaleMod = reader.GetString(4);
                    int hasGeneratedItemState = reader.GetInt32(5);
                    int rolledRequiresMembership = reader.GetInt32(6);
                    int generatedRequiresMembership = reader.GetInt32(7);
                    int soulBound = reader.GetInt32(8);
                    int noSell = reader.GetInt32(9);
                    int soulBoundCountdown = reader.GetInt32(10);
                    string generatedItemModifiers = reader.GetString(11);

                    if (!SavedItemRuntimeState.TryRestore(presetScaleMod, hasGeneratedItemState, rolledRequiresMembership, generatedRequiresMembership, soulBound, noSell, soulBoundCountdown, generatedItemModifiers, out SavedItemRuntimeState itemState, out string stateReason))
                        throw new InvalidDataException($"Character equipment item state is invalid characterId={charId} slot={slot} reason={stateReason}");

                    bool validSlot;
                    switch (slot)
                    {
                        case "weapon":
                        case "armor":
                        case "helmet":
                        case "gloves":
                        case "boots":
                        case "shoulders":
                        case "shield":
                        case "ring1":
                        case "ring2":
                        case "amulet":
                            validSlot = true;
                            break;
                        default:
                            validSlot = false;
                            break;
                    }
                    if (!validSlot || string.IsNullOrWhiteSpace(gcClass)
                        || rarity < -1 || rarity > 5
                        || (storedLevel != -1 && (storedLevel < 1 || storedLevel > 120)))
                        throw new InvalidDataException($"Character equipment row is invalid characterId={charId} slot={slot}");

                    equipment.slotRarity[slot] = rarity;
                    equipment.slotLevel[slot] = storedLevel;
                    equipment.slotItemState[slot] = itemState;
                    switch (slot)
                    {
                        case "weapon": equipment.weapon = gcClass; break;
                        case "armor": equipment.armor = gcClass; break;
                        case "helmet": equipment.helmet = gcClass; break;
                        case "gloves": equipment.gloves = gcClass; break;
                        case "boots": equipment.boots = gcClass; break;
                        case "shoulders": equipment.shoulders = gcClass; break;
                        case "shield": equipment.shield = gcClass; break;
                        case "ring1": equipment.ring1 = gcClass; break;
                        case "ring2": equipment.ring2 = gcClass; break;
                        case "amulet": equipment.amulet = gcClass; break;
                    }
                }
            }
            return equipment;
        }

        private static void ApplyStartingEquipmentLevels(SavedCharacter character)
        {
            if (character == null || character.equipment == null) return;

            string classKey = ResolveClassConfigKey(character);
            if (string.IsNullOrEmpty(classKey)) return;

            var classDef = ClassConfig.GetClassDefinition(classKey);
            var starting = classDef?.startingEquipment;
            if (starting == null) return;

            if (character.equipment.slotLevel == null)
                character.equipment.slotLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            ApplyStartingSlotLevel(character, starting, "weapon");
            ApplyStartingSlotLevel(character, starting, "armor");
            ApplyStartingSlotLevel(character, starting, "helmet");
            ApplyStartingSlotLevel(character, starting, "gloves");
            ApplyStartingSlotLevel(character, starting, "boots");
            ApplyStartingSlotLevel(character, starting, "shoulders");
            ApplyStartingSlotLevel(character, starting, "shield");
            ApplyStartingSlotLevel(character, starting, "ring1");
            ApplyStartingSlotLevel(character, starting, "ring2");
            ApplyStartingSlotLevel(character, starting, "amulet");
        }

        private static void ApplyStartingSlotLevel(SavedCharacter character, StartingEquipment starting, string slot)
        {
            int level = StartingSlotLevel(starting, slot);
            if (level <= 0) return;

            string currentGc = GetEquipmentSlot(character.equipment, slot);
            string startingGc = GetEquipmentSlot(starting, slot);
            if (string.IsNullOrWhiteSpace(currentGc) || !SameGcClass(currentGc, startingGc))
                return;

            if (character.equipment.slotLevel.TryGetValue(slot, out int currentLevel) && currentLevel > 0)
                return;

            character.equipment.slotLevel[slot] = level;
            Debug.LogError($"[EQUIP-CLIENT-LEVEL] char={character.id} class={character.className} slot={slot} gc={currentGc} stored_level={level} source=PKG-starting-equipment");
        }

        private static int StartingSlotLevel(StartingEquipment equipment, string slot)
        {
            if (equipment?.slotLevel != null && equipment.slotLevel.TryGetValue(slot, out int level) && level > 0)
                return level;
            return -1;
        }

        private static string ResolveClassConfigKey(SavedCharacter character)
        {
            string combined = $"{character.className} {character.avatarClass}".ToLowerInvariant();
            if (combined.Contains("ranger")) return "Ranger";
            if (combined.Contains("mage") || combined.Contains("warlock")) return "Mage";
            if (combined.Contains("fighter") || combined.Contains("warrior")) return "Fighter";
            return character.className;
        }

        private static string GetEquipmentSlot(StartingEquipment equipment, string slot)
        {
            if (equipment == null) return "";
            switch (slot)
            {
                case "weapon": return equipment.weapon;
                case "armor": return equipment.armor;
                case "helmet": return equipment.helmet;
                case "gloves": return equipment.gloves;
                case "boots": return equipment.boots;
                case "shoulders": return equipment.shoulders;
                case "shield": return equipment.shield;
                case "ring1": return equipment.ring1;
                case "ring2": return equipment.ring2;
                case "amulet": return equipment.amulet;
                default: return "";
            }
        }

        private static bool SameGcClass(string left, string right)
        {
            return string.Equals(NormalizeGcClass(left), NormalizeGcClass(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeGcClass(string gcClass)
        {
            return (gcClass ?? "").Trim().Replace('\\', '.').Replace('/', '.');
        }

        private static List<string> LoadSkillList(SqliteConnection connection, int charId)
        {
            var skills = new List<string>();
            var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = GameDatabase.ExecuteReader(connection,
                "SELECT skill_gc_class FROM character_skills WHERE character_id = @cid ORDER BY id",
                ("@cid", charId)))
            {
                while (reader.Read())
                {
                    string skill = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(skill) || !skillNames.Add(skill))
                        throw new InvalidDataException($"Character skill row is invalid characterId={charId}");
                    skills.Add(skill);
                }
            }
            return skills;
        }

        private static List<SkillLevelEntry> LoadSkillLevels(SqliteConnection connection, int charId)
        {
            var levels = new List<SkillLevelEntry>();
            var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = GameDatabase.ExecuteReader(connection,
                "SELECT skill_gc_class, level FROM character_skills WHERE character_id = @cid ORDER BY id",
                ("@cid", charId)))
            {
                while (reader.Read())
                {
                    string skill = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    int level = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    if (string.IsNullOrWhiteSpace(skill) || level < 1 || level > byte.MaxValue || !skillNames.Add(skill))
                        throw new InvalidDataException($"Character skill level row is invalid characterId={charId}");
                    levels.Add(new SkillLevelEntry { skill = skill, level = level });
                }
            }
            return levels;
        }

        private static List<HotbarSlotEntry> LoadHotbarSlots(SqliteConnection connection, int charId)
        {
            var slots = new List<HotbarSlotEntry>();
            var hotbarSlots = new HashSet<int>();
            using (var reader = GameDatabase.ExecuteReader(connection,
                "SELECT skill_gc_class, hotbar_slot FROM character_skills WHERE character_id = @cid AND hotbar_slot >= 0 ORDER BY id",
                ("@cid", charId)))
            {
                while (reader.Read())
                {
                    string skill = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    int hotbarSlot = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);
                    if (string.IsNullOrWhiteSpace(skill) || hotbarSlot < 0 || hotbarSlot > 0x0FFF || !hotbarSlots.Add(hotbarSlot))
                        throw new InvalidDataException($"Character hotbar row is invalid characterId={charId}");
                    slots.Add(new HotbarSlotEntry { skill = skill, slot = checked((uint)hotbarSlot) });
                }
            }
            return slots;
        }

        private static List<SavedInventoryItem> LoadInventory(SqliteConnection connection, int charId)
        {
            var items = new List<SavedInventoryItem>();
            using (var reader = GameDatabase.ExecuteReader(connection,
                "SELECT gc_class, slot_x, slot_y, count, COALESCE(buy_price, 0), COALESCE(rarity, -1), COALESCE(stored_level, -1), COALESCE(container_id, 11), COALESCE(item_order, 0), COALESCE(preset_scale_mod, ''), COALESCE(has_generated_item_state, 0), COALESCE(rolled_requires_membership, 0), COALESCE(generated_requires_membership, 0), COALESCE(soul_bound, 0), COALESCE(no_sell, 0), COALESCE(soul_bound_countdown, 65535), COALESCE(generated_item_modifiers, '[]') FROM character_inventory WHERE character_id = @cid ORDER BY container_id, item_order, id",
                ("@cid", charId)))
            {
                while (reader.Read())
                {
                    string gcClass = reader.GetString(0);
                    int slotX = reader.GetInt32(1);
                    int slotY = reader.GetInt32(2);
                    int count = reader.GetInt32(3);
                    int buyPrice = reader.GetInt32(4);
                    int rarity = reader.GetInt32(5);
                    int storedLevel = reader.GetInt32(6);
                    int containerId = reader.GetInt32(7);
                    int itemOrder = reader.GetInt32(8);
                    string presetScaleMod = reader.GetString(9);
                    int hasGeneratedItemState = reader.GetInt32(10);
                    int rolledRequiresMembership = reader.GetInt32(11);
                    int generatedRequiresMembership = reader.GetInt32(12);
                    int soulBound = reader.GetInt32(13);
                    int noSell = reader.GetInt32(14);
                    int soulBoundCountdown = reader.GetInt32(15);
                    string generatedItemModifiers = reader.GetString(16);

                    if (!SavedItemRuntimeState.TryRestore(presetScaleMod, hasGeneratedItemState, rolledRequiresMembership, generatedRequiresMembership, soulBound, noSell, soulBoundCountdown, generatedItemModifiers, out SavedItemRuntimeState itemState, out string stateReason))
                        throw new InvalidDataException($"Character inventory item state is invalid characterId={charId} containerId={containerId} itemOrder={itemOrder} reason={stateReason}");

                    bool isInventory = containerId == 0x0B;
                    bool isBank = containerId == 0x0C || (containerId >= 0x0E && containerId <= 0x13);
                    int maxY = isBank ? 13 : 7;
                    if ((!isInventory && !isBank)
                        || string.IsNullOrWhiteSpace(gcClass)
                        || slotX < 0 || slotX > 9
                        || slotY < 0 || slotY > maxY
                        || count < 1 || count > byte.MaxValue
                        || buyPrice < 0
                        || rarity < -1 || rarity > 5
                        || (storedLevel != -1 && (storedLevel < 1 || storedLevel > 120))
                        || itemOrder < 0)
                        throw new InvalidDataException($"Character inventory row is invalid characterId={charId} containerId={containerId} itemOrder={itemOrder}");

                    items.Add(new SavedInventoryItem
                    {
                        gcClass = gcClass,
                        x = (byte)slotX,
                        y = (byte)slotY,
                        count = count,
                        buyPrice = (uint)buyPrice,
                        rarity = rarity,
                        storedLevel = storedLevel,
                        containerId = (byte)containerId,
                        itemOrder = itemOrder,
                        itemState = itemState
                    });
                }
            }
            return items;
        }

        private static List<SavedQuest> LoadActiveQuests(SqliteConnection connection, int charId)
        {
            var quests = new List<SavedQuest>();
            using (var reader = GameDatabase.ExecuteReader(connection,
                "SELECT quest_id, quest_giver_id, accepted_at FROM character_quests WHERE character_id = @cid AND status = 'active' ORDER BY id",
                ("@cid", charId)))
            {
                while (reader.Read())
                {
                    var quest = new SavedQuest
                    {
                        questId = reader.GetString(0),
                        questGiverId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        acceptedAt = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        objectives = new List<SavedQuestObjective>()
                    };
                    quests.Add(quest);
                }
            }

            foreach (var quest in quests)
            {
                using (var reader = GameDatabase.ExecuteReader(connection,
                    "SELECT objective_name, type, target, label, required, current FROM quest_objectives WHERE character_id = @cid AND quest_id = @qid ORDER BY id",
                    ("@cid", charId), ("@qid", quest.questId)))
                {
                    while (reader.Read())
                    {
                        quest.objectives.Add(new SavedQuestObjective
                        {
                            objectiveName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                            type = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            target = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            label = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            required = reader.GetInt32(4),
                            current = reader.GetInt32(5)
                        });
                    }
                }
            }

            return quests;
        }

        private static List<string> LoadCompletedQuests(SqliteConnection connection, int charId, Dictionary<string, long> timestamps)
        {
            var quests = new List<string>();
            using (var reader = GameDatabase.ExecuteReader(connection,
                "SELECT quest_id, CAST(strftime('%s', completed_at) AS INTEGER) FROM completed_quests WHERE character_id = @cid ORDER BY id",
                ("@cid", charId)))
            {
                while (reader.Read())
                {
                    string questId = reader.GetString(0);
                    quests.Add(questId);
                    timestamps[questId] = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                }
            }
            return quests;
        }

        private static List<string> LoadCheckpoints(SqliteConnection connection, int charId)
        {
            var checkpoints = new List<string>();
            using (var reader = GameDatabase.ExecuteReader(connection,
                "SELECT checkpoint_id FROM character_checkpoints WHERE character_id = @cid ORDER BY id",
                ("@cid", charId)))
            {
                while (reader.Read())
                {
                    string checkpointId = reader.GetString(0);
                    if (!checkpointId.StartsWith("world.checkpoints.", System.StringComparison.OrdinalIgnoreCase))
                        checkpointId = "world.checkpoints." + checkpointId;
                    checkpoints.Add(checkpointId);
                }
            }
            return checkpoints;
        }

        private static int ToFixed8(float value)
        {
            string text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (!GCNode.TryParseFixed32(text, out int fixedValue))
                throw new InvalidOperationException($"Invalid persisted position value '{text}'");
            return fixedValue;
        }

    }
}
