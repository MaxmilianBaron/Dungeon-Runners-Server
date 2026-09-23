using System;
using System.Collections.Generic;

[Serializable]
public sealed class DungeonSpawnData
{
    public string zoneName;
    public string gcType;
    public string spawnGcTypeOverride;
    public int PosFixedX;
    public int PosFixedY;
    public int PosFixedZ;
    public int HeadingFixed;
    public string encounterGroupKey;
    public int encounterDifficultyF32 = -1;
    public int encounterLevelOffset;
    public int gridX = -1;
    public int gridY = -1;
    public string tileType;
    public int WorldOriginFixedX;
    public int WorldOriginFixedY;
    public int LocalFixedX;
    public int LocalFixedY;
    public int LocalFixedZ;
    public string placementRole;
    public string placeholderSource;
    public int placeholderIndex = -1;
    public int PlaceholderSizeFixedX;
    public int PlaceholderSizeFixedY;
    public int encounterChoiceIndex = -1;
    public bool snapApplied;
}

public sealed class QuestKillDropEntry
{
    public string SourcePath;
    public string QuestId;
    public string ObjectiveSourcePath;
    public int ObjectiveIndex = -1;
    public string ItemGcType;
    public string TreasureGenerator;
    public int Chance;
    public int ChanceCount = 1;
    public int MinLevel = 1;
    public int MaxLevel = 200;
}

public sealed class QuestActivateDropEntry
{
    public string SourcePath;
    public string QuestId;
    public string ObjectiveSourcePath;
    public int ObjectiveIndex = -1;
    public string EntityGcType;
    public string ItemGcType;
    public int Chance = 100;
    public bool RemoveEntity;
}

[Serializable]
public sealed class AuthoredZoneData
{
    public uint Id;
    public string Name;
    public string GcType;
    public string Label;
    public bool Private;
    public int SpawnFixedX;
    public int SpawnFixedY;
    public int SpawnFixedZ;
    public int SpawnHeadingFixed;
    public string RespawnZone;
    public string RespawnSpawnPoint;
    public int ExploredBitCount;
}

[Serializable]
public class GeneralItemData
{
    public string gcType;
    public string baseType;
    public string Label;
    public string InventoryIcon;
    public string GroundObject;
    public bool Stackable;
    public int InventoryWidth = 1;
    public int InventoryHeight = 1;
    public int DropLevel;
    public int LevelReq;
    public int modCount;
    public int GoldValueF32;
    public int GcGoldValueF32;
}

[Serializable]
public class ItemData
{
    public string gcType;
    public string name;
    public string description;
    public int GoldValueF32;
    public int GcGoldValueF32;
    public int range;
    public string slotType;
    public string weaponClass;
    public int inventoryWidth;
    public int inventoryHeight;
    public string inventoryIcon;
    public string groundObject;
    public bool equipable;
    public int modCount = 3;
    public int requiredLevel = 1;

    public static int GetRequiredLevelFromGCClass(string gcClass)
    {
        if (string.IsNullOrEmpty(gcClass)) return 1;
        int palIdx = gcClass.LastIndexOf("PAL", StringComparison.OrdinalIgnoreCase);
        if (palIdx > 0)
        {
            int numEnd = palIdx;
            int numStart = numEnd - 1;
            while (numStart >= 0 && char.IsDigit(gcClass[numStart]))
                numStart--;
            numStart++;
            if (numStart < numEnd)
            {
                string tierStr = gcClass.Substring(numStart, numEnd - numStart);
                if (int.TryParse(tierStr, out int palTier))
                {
                    if (palTier <= 1) return 1;
                    return (palTier - 1) * 10 + 1;
                }
            }
        }
        return 1;
    }
}

[Serializable]
public class SkillData
{
    public string id;
    public string name;
    public string description;
    public int level;
    public int experience;
    public int maxLevel;
    public SkillAttributes attributes;
}

[Serializable]
public class SkillAttributes
{
    public int strength;
    public int dexterity;
    public int vitality;
    public int intelligence;
    public int wisdom;
    public int spirit;
    public int perception;
    public int agility;
}

[Serializable]
public class QuestData
{
    public string id;
    public string name;
    public string summary;
    public string description;
    public int level;
    public int minLevel;
    public int maxLevel;
    public List<string> npcs = new List<string>();
    public List<string> offeringNpcs = new List<string>();
    public string onAcceptItem;
    public string baseClass;
    public int tokenReward;
    public int cashRewardF32;
    public float cashReward => cashRewardF32 / 256f;
    public bool grantXPBuff;
    public bool repeatable;
    public List<string> requiredQuests = new List<string>();
    public string followupQuest;
    public string uiZoneInfo;
    public List<QuestObjective> objectives;
    public QuestRewards rewards;
    public uint hash;
    public int numRewardItems;
    public List<QuestRewardChoice> rewardChoices = new List<QuestRewardChoice>();
    public bool rewardItemsSoulBound;
    public bool rewardItemsNoSell;
    public bool rewardItemsDropped;
    public bool rewardItemsRequireMembership;
    public bool onAcceptItemsSoulBound;
    public bool onAcceptItemsNoSell;
    public int minRepeatTimeSeconds;
    public bool addOnAccept;
    public bool autoAcceptOnQuery;
    public bool permanentAbandon;
    public bool temporary;
    public string requiredClass;
    public string modToAddOnComplete;
    public string rewardText;
    public string abandonOnZone;
}

[Serializable]
public class QuestRewardChoice
{
    public string generator;
    public string description;
    public string icon;
}

[Serializable]
public class QuestObjective
{
    public string sourcePath;
    public string type;
    public string target;
    public List<string> targets = new List<string>();
    public int count;
    public bool removeOnFinalize;
    public byte itemLevel = byte.MaxValue;
    public string targetZone;
    public string targetEntity;
    public int range;
    public bool completed;
    public string label;
    public string name;
}

[Serializable]
public class QuestRewards
{
    public int experience;
}

[Serializable]
public class CheckpointData
{
    public string id;
    public string name;
    public string description;
    public PositionData position;
    public PositionData rotation;
    public string mapId;
    public string zone;
    public int order;
    public string spawnPoint;
    public bool isActive;
    public int levelRequirement;
    public string unlockQuest;
    public string image;
}

[Serializable]
public class NPCData
{
    public string name;
    public string gcType;
    public int PosFixedX;
    public int PosFixedY;
    public int PosFixedZ;
    public int HeadingFixed;
    public int hitPoints;
    public int manaPoints;
}

[Serializable]
public class PositionData
{
    public float x;
    public float y;
    public float z;
}

public static class MonsterHealthTable
{
    public static int GetDifficultyModifierF32(string difficulty)
    {
        return DungeonRunners.Gameplay.ServerReconstructionPolicy.ResolveTierHealthModifierF32(difficulty);
    }

    public static uint CalculateHPWireFixed32(int level, int difficultyF32, int modF32)
    {
        int baseF32 = DungeonRunners.Data.GCDatabase.Instance.RequireCurveValueFixed32("MonsterHealth", Math.Max(1, Math.Min(110, level)));
        long hpF32 = ((long)baseF32 * Math.Max(0, modF32)) >> 8;
        hpF32 = (hpF32 * Math.Max(0, difficultyF32)) >> 8;
        long hpUnits = Math.Max(1L, hpF32 >> 8);
        return hpUnits >= uint.MaxValue / 256u ? uint.MaxValue : (uint)hpUnits * 256u;
    }
}

[Serializable]
public class CreatureData
{
    public string gcType;
    public string behaviourType;
    public string name;
    public string faction;
    public string creatureType;
    public string element;
    public string creatureDifficulty;
    public string tier => creatureDifficulty;
    public string speed;
    public string walkSpeed;
    public string attackRange;
    public int hitPoints;
    public int manaPoints;
    public int baseDamage;
    public string attackRating;
    public string defenseRating;
    public string damageMod;
    public string criticalChance;
    public string divineResist;
    public string fireResist;
    public string iceResist;
    public string poisonResist;
    public string shadowResist;
    [NonSerialized] public Dictionary<string, ManipulatorData> manipulators;

    public int GetFixed32(string value, int fallbackF32 = 0)
    {
        return DungeonRunners.Data.GCNode.TryParseFixed32(value, out int result) ? result : fallbackF32;
    }

    public int AttackRatingF32 => GetFixed32(attackRating, 0x100);
    public int DefenseRatingF32 => GetFixed32(defenseRating, 0x100);
    public int DamageModF32 => GetFixed32(damageMod, 0x100);
    public int CritChanceF32 => GetFixed32(criticalChance, 0);
    public int DivineResistF32 => GetFixed32(divineResist, 0);
    public int FireResistF32 => GetFixed32(fireResist, 0);
    public int IceResistF32 => GetFixed32(iceResist, 0);
    public int PoisonResistF32 => GetFixed32(poisonResist, 0);
    public int ShadowResistF32 => GetFixed32(shadowResist, 0);
}

[Serializable]
public class ManipulatorData
{
    public string gcType;
    public Dictionary<string, string> properties;

    public ManipulatorData()
    {
        properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

[Serializable]
public class SummonedUnitData
{
    public CreatureData unit;
    public string gcType;
    public string behaviourType;
    public string name;
    public int maxHealthFixed32;
    public int maxManaFixed32;
    public int lifespanFixed32;
    public int lifespanIncrementFixed32;
    public bool useSummonerAsSource;
    public string element;
    public string description;
}

[Serializable]
public class ZonePortalData
{
    public int id;
    public string zone;
    public string name;
    public string gcType;
    public int PosFixedX;
    public int PosFixedY;
    public int PosFixedZ;
    public int HeadingFixed;
    public int width;
    public int height;
    public string targetZone;
    public string spawnPoint;
    public uint color;
}

[Serializable]
public class ZoneWaypointData
{
    public int id;
    public string zone;
    public string name;
    public int PosFixedX;
    public int PosFixedY;
    public int PosFixedZ;
    public int HeadingFixed;
}

[Serializable]
public class ZoneCheckpointData
{
    public int id;
    public string zone;
    public string name;
    public string gcType;
    public string entityGcType;
    public int PosFixedX;
    public int PosFixedY;
    public int PosFixedZ;
    public int HeadingFixed;
}

[Serializable]
public class DRClassChildGroup
{
    public string name;
    public DRChildEntity[] entities;
    public string gcType;
}

[Serializable]
public class DRChildEntity
{
    public string @extends;
    public Dictionary<string, string> properties;
    public Dictionary<string, DRChildGroup> children;
}

[Serializable]
public class DRChildGroup
{
    public DRChildEntity[] entities;
}
