using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{
    [Serializable]
    public class StartingInventoryItem
    {
        public string gcClass;
        public byte x;
        public byte y;
        public int count = 1;
    }

    [Serializable]
    public class ClassDefinition
    {
        public string displayName;
        public string description;
        public StartingEquipment startingEquipment;
        public List<string> startingSkills = new List<string>();
        public List<StartingInventoryItem> startingInventory = new List<StartingInventoryItem>();
    }

    [Serializable]
    public class StartingEquipment
    {
        public string weapon;
        public string armor;
        public string helmet;
        public string gloves;
        public string boots;
        public string shoulders;
        public string shield;
        public string ring1;
        public string ring2;
        public string amulet;
        public Dictionary<string, int> slotRarity = new Dictionary<string, int>();
        public Dictionary<string, int> slotLevel = new Dictionary<string, int>();
        public Dictionary<string, SavedItemRuntimeState> slotItemState = new Dictionary<string, SavedItemRuntimeState>();

        public StartingEquipment DeepClone()
        {
            return new StartingEquipment
            {
                weapon = weapon,
                armor = armor,
                helmet = helmet,
                gloves = gloves,
                boots = boots,
                shoulders = shoulders,
                shield = shield,
                ring1 = ring1,
                ring2 = ring2,
                amulet = amulet,
                slotRarity = slotRarity != null
                    ? new Dictionary<string, int>(slotRarity, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                slotLevel = slotLevel != null
                    ? new Dictionary<string, int>(slotLevel, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                slotItemState = slotItemState != null
                    ? slotItemState.ToDictionary(entry => entry.Key, entry => entry.Value?.DeepClone(), StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, SavedItemRuntimeState>(StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    [Serializable]
    public class SavedItemRuntimeState
    {
        public string presetScaleMod = "";
        public bool hasGeneratedItemState;
        public bool rolledRequiresMembership;
        public bool generatedRequiresMembership;
        public bool soulBound;
        public bool noSell;
        public int soulBoundCountdown = ushort.MaxValue;
        public List<string> generatedItemModifiers = new List<string>();

        public SavedItemRuntimeState DeepClone()
        {
            return new SavedItemRuntimeState
            {
                presetScaleMod = presetScaleMod ?? "",
                hasGeneratedItemState = hasGeneratedItemState,
                rolledRequiresMembership = rolledRequiresMembership,
                generatedRequiresMembership = generatedRequiresMembership,
                soulBound = soulBound,
                noSell = noSell,
                soulBoundCountdown = soulBoundCountdown,
                generatedItemModifiers = generatedItemModifiers != null
                    ? new List<string>(generatedItemModifiers)
                    : new List<string>()
            };
        }

        public static SavedItemRuntimeState Capture(GCObject item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            var state = new SavedItemRuntimeState
            {
                presetScaleMod = item.PresetScaleMod ?? "",
                hasGeneratedItemState = item.HasGeneratedItemState,
                rolledRequiresMembership = item.RolledRequiresMembership,
                generatedRequiresMembership = item.GeneratedRequiresMembership,
                soulBound = item.SoulBound,
                noSell = item.NoSell,
                soulBoundCountdown = item.SoulBoundCountdown,
                generatedItemModifiers = new List<string>(item.GeneratedItemModifiers ?? new List<string>())
            };
            state.Validate();
            return state;
        }

        public void ApplyTo(GCObject item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            Validate();
            item.PresetScaleMod = string.IsNullOrEmpty(presetScaleMod) ? null : presetScaleMod;
            item.HasGeneratedItemState = hasGeneratedItemState;
            item.RolledRequiresMembership = rolledRequiresMembership;
            item.GeneratedRequiresMembership = generatedRequiresMembership;
            item.SoulBound = soulBound;
            item.NoSell = noSell;
            item.SoulBoundCountdown = (ushort)soulBoundCountdown;
            item.GeneratedItemModifiers = new List<string>(generatedItemModifiers);
        }

        public string SerializeModifiers()
        {
            Validate();
            return JsonSerializer.Serialize(generatedItemModifiers);
        }

        public static bool TryRestore(
            string presetScaleMod,
            int hasGeneratedItemState,
            int rolledRequiresMembership,
            int generatedRequiresMembership,
            int soulBound,
            int noSell,
            int soulBoundCountdown,
            string generatedItemModifiers,
            out SavedItemRuntimeState state,
            out string reason)
        {
            state = null;
            reason = "";
            if ((hasGeneratedItemState != 0 && hasGeneratedItemState != 1) ||
                (rolledRequiresMembership != 0 && rolledRequiresMembership != 1) ||
                (generatedRequiresMembership != 0 && generatedRequiresMembership != 1) ||
                (soulBound != 0 && soulBound != 1) ||
                (noSell != 0 && noSell != 1))
            {
                reason = "invalid-boolean";
                return false;
            }

            List<string> modifiers;
            try
            {
                modifiers = JsonSerializer.Deserialize<List<string>>(generatedItemModifiers ?? "") ?? throw new JsonException();
            }
            catch (JsonException)
            {
                reason = "invalid-modifier-json";
                return false;
            }

            state = new SavedItemRuntimeState
            {
                presetScaleMod = presetScaleMod ?? "",
                hasGeneratedItemState = hasGeneratedItemState == 1,
                rolledRequiresMembership = rolledRequiresMembership == 1,
                generatedRequiresMembership = generatedRequiresMembership == 1,
                soulBound = soulBound == 1,
                noSell = noSell == 1,
                soulBoundCountdown = soulBoundCountdown,
                generatedItemModifiers = modifiers
            };
            try
            {
                state.Validate();
                return true;
            }
            catch (InvalidOperationException ex)
            {
                reason = ex.Message;
                state = null;
                return false;
            }
        }

        private void Validate()
        {
            generatedItemModifiers ??= new List<string>();
            if (soulBoundCountdown < 0 || soulBoundCountdown > ushort.MaxValue)
                throw new InvalidOperationException("soulbound-countdown-out-of-range");
            if (!hasGeneratedItemState &&
                (rolledRequiresMembership || generatedRequiresMembership || generatedItemModifiers.Count != 0))
                throw new InvalidOperationException("generated-state-without-flag");
            if (generatedItemModifiers.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("blank-generated-modifier");
        }
    }

    [Serializable]
    public class ClassConfigData
    {
        public Dictionary<string, ClassDefinition> classes = new Dictionary<string, ClassDefinition>();
    }

    [Serializable]
    public class SavedInventoryItem
    {
        public string gcClass;
        public byte x;
        public byte y;
        public int count = 1;
        public uint buyPrice = 0;
        public int rarity = 0;
        public int storedLevel = -1;
        public byte containerId = 0x0B;
        public int itemOrder;
        public SavedItemRuntimeState itemState = new SavedItemRuntimeState();

        public SavedInventoryItem DeepClone()
        {
            return new SavedInventoryItem
            {
                gcClass = gcClass,
                x = x,
                y = y,
                count = count,
                buyPrice = buyPrice,
                rarity = rarity,
                storedLevel = storedLevel,
                containerId = containerId,
                itemOrder = itemOrder,
                itemState = itemState?.DeepClone() ?? new SavedItemRuntimeState()
            };
        }
    }

    [Serializable]
    public class SavedCharacter
    {
        public uint id;
        public string name;
        public uint accountId;
        public string accountName;
        public string className;
        public byte level;
        public uint experience;
        public uint gold;
        public StartingEquipment equipment;
        public List<string> skills = new List<string>();
        public List<SavedInventoryItem> inventory = new List<SavedInventoryItem>();
        public int positionFixedX;
        public int positionFixedY;
        public int positionFixedZ;
        public int zoneId;
        public int worldId;
        public string currentZoneName;
        public string avatarClass;
        public byte skin;
        public byte face;
        public byte faceFeature;
        public byte hair;
        public byte hairColor;

        public List<SavedQuest> activeQuests = new List<SavedQuest>();
        public List<string> completedQuests = new List<string>();
        public Dictionary<string, long> questCompletionTimes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public List<string> unlockedCheckpoints = new List<string>();

        public uint currentHP = 0;
        public uint currentMana = 0;

        public int maxHP = 0;
        public int maxMana = 0;
        public int statStrength = 0;
        public int statAgility = 0;
        public int statIntellect = 0;
        public int statEndurance = 0;
        public int lastRespecTime = 0;
        public int respecCount = 0;
        public int pvpWins = 0;
        public const int DefaultPvpRating = 1500;
        public int pvpRating = DefaultPvpRating;
        public bool hasPvpRating = false;
        public byte monsterDifficulty;
        public int minimumItemQuality = 1;

        public uint GetPvpRatingFixed32()
        {
            if (pvpRating < 0 || pvpRating > 3000)
                throw new ArgumentOutOfRangeException(nameof(pvpRating));
            return checked((uint)pvpRating << 8);
        }

        public string tpZone = "";
        public int tpZoneId = 0;
        public string tpTargetZone = "";
        public int tpPosFixedX = 0, tpPosFixedY = 0, tpPosFixedZ = 0;

        public uint posseId = 0;
        public string posseName = "";
        public int posseJoinCooldown = 0;
        public int posseRankId = 1;

        public List<SkillLevelEntry> skillLevels = new List<SkillLevelEntry>();

        public List<HotbarSlotEntry> hotbarSlots = new List<HotbarSlotEntry>();

        public SavedCharacter DeepClone()
        {
            return new SavedCharacter
            {
                id = id,
                name = name,
                accountId = accountId,
                accountName = accountName,
                className = className,
                level = level,
                experience = experience,
                gold = gold,
                equipment = equipment?.DeepClone() ?? new StartingEquipment(),
                skills = skills != null ? new List<string>(skills) : new List<string>(),
                inventory = inventory != null
                    ? inventory.Select(item => item?.DeepClone()).Where(item => item != null).ToList()
                    : new List<SavedInventoryItem>(),
                positionFixedX = positionFixedX,
                positionFixedY = positionFixedY,
                positionFixedZ = positionFixedZ,
                zoneId = zoneId,
                worldId = worldId,
                currentZoneName = currentZoneName,
                avatarClass = avatarClass,
                skin = skin,
                face = face,
                faceFeature = faceFeature,
                hair = hair,
                hairColor = hairColor,
                activeQuests = activeQuests != null
                    ? activeQuests.Select(quest => quest?.DeepClone()).Where(quest => quest != null).ToList()
                    : new List<SavedQuest>(),
                completedQuests = completedQuests != null ? new List<string>(completedQuests) : new List<string>(),
                questCompletionTimes = questCompletionTimes != null
                    ? new Dictionary<string, long>(questCompletionTimes, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                unlockedCheckpoints = unlockedCheckpoints != null ? new List<string>(unlockedCheckpoints) : new List<string>(),
                currentHP = currentHP,
                currentMana = currentMana,
                maxHP = maxHP,
                maxMana = maxMana,
                statStrength = statStrength,
                statAgility = statAgility,
                statIntellect = statIntellect,
                statEndurance = statEndurance,
                lastRespecTime = lastRespecTime,
                respecCount = respecCount,
                pvpWins = pvpWins,
                pvpRating = pvpRating,
                hasPvpRating = hasPvpRating,
                monsterDifficulty = monsterDifficulty,
                minimumItemQuality = minimumItemQuality,
                tpZone = tpZone,
                tpZoneId = tpZoneId,
                tpTargetZone = tpTargetZone,
                tpPosFixedX = tpPosFixedX,
                tpPosFixedY = tpPosFixedY,
                tpPosFixedZ = tpPosFixedZ,
                posseId = posseId,
                posseName = posseName,
                posseJoinCooldown = posseJoinCooldown,
                posseRankId = posseRankId,
                skillLevels = skillLevels != null
                    ? skillLevels.Select(entry => new SkillLevelEntry { skill = entry.skill, level = entry.level }).ToList()
                    : new List<SkillLevelEntry>(),
                hotbarSlots = hotbarSlots != null
                    ? hotbarSlots.Select(entry => new HotbarSlotEntry { slot = entry.slot, skill = entry.skill }).ToList()
                    : new List<HotbarSlotEntry>()
            };
        }

        public int GetSkillLevel(string skillGcClass)
        {
            for (int skillIndex = 0; skillIndex < skillLevels.Count; skillIndex++)
                if (string.Equals(skillLevels[skillIndex].skill, skillGcClass, StringComparison.OrdinalIgnoreCase))
                    return skillLevels[skillIndex].level;
            return 1;
        }

        public void SetSkillLevel(string skillGcClass, int level)
        {
            for (int skillIndex = 0; skillIndex < skillLevels.Count; skillIndex++)
            {
                if (string.Equals(skillLevels[skillIndex].skill, skillGcClass, StringComparison.OrdinalIgnoreCase))
                {
                    skillLevels[skillIndex] = new SkillLevelEntry { skill = skillGcClass, level = level };
                    return;
                }
            }
            skillLevels.Add(new SkillLevelEntry { skill = skillGcClass, level = level });
        }
    }

    public static class SavedCharacterLevel
    {
        public static int ResolveRuntimeLevel(SavedCharacter character)
        {
            return ResolveRuntimeLevel(character != null ? character.level : 1);
        }

        public static int ResolveRuntimeLevel(int persistedLevel)
        {
            int maxLevel = 100;
            try
            {
                if (GCDatabase.Instance != null)
                    maxLevel = Math.Max(1, GCDatabase.Instance.GetRequiredKnobInt("MaxLevel"));
            }
            catch
            {
                maxLevel = 100;
            }

            int persisted = Math.Max(0, persistedLevel);
            return Math.Max(1, Math.Min(maxLevel, persisted + 1));
        }

        public static byte ResolvePersistedLevel(int runtimeLevel)
        {
            int maxLevel = 100;
            try
            {
                if (GCDatabase.Instance != null)
                    maxLevel = Math.Max(1, GCDatabase.Instance.GetRequiredKnobInt("MaxLevel"));
            }
            catch
            {
                maxLevel = 100;
            }

            int persisted = Math.Max(0, Math.Min(maxLevel, runtimeLevel) - 1);
            return (byte)Math.Max(0, Math.Min(255, persisted));
        }
    }

    [Serializable]
    public class SkillLevelEntry
    {
        public string skill;
        public int level = 1;
    }

    [Serializable]
    public class HotbarSlotEntry
    {
        public uint slot;
        public string skill;
    }

    [Serializable]
    public class SavedQuest
    {
        public string questId;
        public string questGiverId;
        public string acceptedAt;
        public List<SavedQuestObjective> objectives = new List<SavedQuestObjective>();

        public SavedQuest DeepClone()
        {
            return new SavedQuest
            {
                questId = questId,
                questGiverId = questGiverId,
                acceptedAt = acceptedAt,
                objectives = objectives != null
                    ? objectives.Select(objective => objective?.DeepClone()).Where(objective => objective != null).ToList()
                    : new List<SavedQuestObjective>()
            };
        }
    }

    [Serializable]
    public class SavedQuestObjective
    {
        public string objectiveName;
        public string type;
        public string target;
        public string label;
        public int required;
        public int current;

        public SavedQuestObjective DeepClone()
        {
            return new SavedQuestObjective
            {
                objectiveName = objectiveName,
                type = type,
                target = target,
                label = label,
                required = required,
                current = current
            };
        }
    }

    public static class ClassConfig
    {
        private static ClassConfigData _classConfig;
        private static bool _isLoaded = false;

        public static void Load()
        {
            if (_isLoaded) return;
            if (!AuthoredGameplayCatalog.IsLoaded)
                throw new InvalidOperationException("Authored gameplay catalog must be loaded before class configuration");
            _classConfig = new ClassConfigData
            {
                classes = new Dictionary<string, ClassDefinition>(AuthoredGameplayCatalog.ClassDefinitions, StringComparer.OrdinalIgnoreCase)
            };
            if (_classConfig.classes.Count == 0)
                throw new InvalidOperationException("Authored gameplay catalog contains no class definitions");
            Debug.LogError($"[CLASS-CONFIG] loaded={_classConfig.classes.Count} source=authored-gameplay-catalog");
            _isLoaded = true;
        }

        public static ClassDefinition GetClassDefinition(string className)
        {
            if (!_isLoaded) Load();

            if (_classConfig.classes.TryGetValue(className, out var classDef))
            {
                return classDef;
            }

            Debug.LogError($"[CLASS-CONFIG] class='{className}' missing");
            return null;
        }
    }
}
