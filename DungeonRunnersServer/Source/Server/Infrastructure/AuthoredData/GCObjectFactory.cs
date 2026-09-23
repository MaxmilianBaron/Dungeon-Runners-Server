using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using Org.BouncyCastle.Utilities;
using System.Linq;

namespace DungeonRunners.Data
{
    public static class GCObjectFactory
    {
        public static GCObject NewPlayer(string name, uint charSqlId = 0, uint groupId = 0, SavedCharacter savedCharacter = null)
        {
            Debug.LogError($"[GC-OBJECT-FACTORY] createPlayer name='{name}' charSqlId=0x{charSqlId:X8} groupId={groupId}");
            var player = new GCObject
            {
                DFCClass = "Player",
                GCClass = "Player",
                Name = name,
                PlayerGroupId = groupId,
                PlayerPvpWins = checked((uint)(savedCharacter?.pvpWins ?? 0)),
                PlayerHasPvpRating = savedCharacter?.hasPvpRating ?? false,
                PlayerMinimumItemQuality = savedCharacter?.minimumItemQuality ?? 1,
                PlayerMonsterDifficulty = savedCharacter?.monsterDifficulty ?? 0,
                Properties = new List<GCObjectProperty>()
            };

            player.Properties.Add(new StringProperty { Name = "Name", Value = name });

            return player;
        }

        private static string GetWeaponClass(string gcClass)
        {
            string lower = gcClass.ToLower();

            if (lower.Contains("shield"))
            {
                Debug.LogError($"[WEAPON-CLASS] gcClass='{gcClass}' result=Armor reason=shield");
                return "Armor";
            }

            if (lower.Contains("gun") ||
                lower.Contains("bow") ||
                lower.Contains("crossbow") ||
                lower.Contains("rifle") ||
                lower.Contains("pistol") ||
                lower.Contains("blaster") ||
                lower.Contains("cannon") ||
                lower.Contains("launcher"))
            {
                Debug.LogError($"[WEAPON-CLASS] gcClass='{gcClass}' result=RangedWeapon");
                return "RangedWeapon";
            }

            Debug.LogError($"[WEAPON-CLASS] gcClass='{gcClass}' result=MeleeWeapon");
            return "MeleeWeapon";
        }

        private static string GetSkillClass(string skillId)
        {
            if (skillId.ToLower().Contains("passive") || skillId.ToLower().Contains("trait"))
                return "PassiveSkill";
            return "ActiveSkill";
        }

        public static GCObject LoadAvatar(SavedCharacter character)
        {
            int runtimeLevel = SavedCharacterLevel.ResolveRuntimeLevel(character);
            Debug.LogError("[GC-OBJECT-FACTORY] loadAvatar phase=start");
            Debug.LogError($"[GC-OBJECT-FACTORY] character={character.name} class={character.className}");
            Debug.LogError($"[GC-OBJECT-FACTORY] characterId={character.id} persistedLevel={character.level} runtimeLevel={runtimeLevel}");
            Debug.LogError("[GC-OBJECT-FACTORY] equipmentTargets=Manipulators,Equipment");

            string avatarGCClass;
            if (!string.IsNullOrEmpty(character.avatarClass))
            {
                avatarGCClass = character.avatarClass;
                Debug.LogError($"[GC-OBJECT-FACTORY] avatarClass={avatarGCClass} source=saved");
            }
            else
            {
                avatarGCClass = GetAvatarGCClass(character.className);
                Debug.LogError($"[GC-OBJECT-FACTORY] avatarClass={avatarGCClass} source=className");
            }

            var avatar = new GCObject
            {
                DFCClass = "Avatar",
                GCClass = avatarGCClass,
                Name = "avatar",
                Properties = new List<GCObjectProperty>
                {
                    new UInt32Property { Name = "Skin", Value = character.skin },
                    new UInt32Property { Name = "Face", Value = character.face },
                    new UInt32Property { Name = "FaceFeature", Value = character.faceFeature },
                    new UInt32Property { Name = "Hair", Value = character.hair },
                    new UInt32Property { Name = "HairColor", Value = character.hairColor },
                    new UInt32Property { Name = "TotalWorldTime", Value = 0 },
                    new UInt32Property { Name = "LastKnownQueueLevel", Value = 0 },
                    new UInt32Property { Name = "HasBlingGnome", Value = 0 },
                    new UInt32Property { Name = "Level", Value = (uint)runtimeLevel },
                    new UInt32Property { Name = "HitPoints", Value = character.currentHP },
                    new UInt32Property { Name = "ManaPoints", Value = character.currentMana },
                    new UInt32Property { Name = "Experience", Value = character.experience },
                    new UInt32Property { Name = "AttributePoints", Value = 0 },
                    new UInt32Property { Name = "ReSpecTimer", Value = 0 },
                    new UInt32Property { Name = "StrengthPoints", Value = (uint)Math.Max(0, character.statStrength) },
                    new UInt32Property { Name = "AgilityPoints", Value = (uint)Math.Max(0, character.statAgility) },
                    new UInt32Property { Name = "ToughnessPoints", Value = (uint)Math.Max(0, character.statEndurance) },
                    new UInt32Property { Name = "PowerPoints", Value = (uint)Math.Max(0, character.statIntellect) },
                    new UInt32Property { Name = "MaxTotalAttributePool", Value = 0 },
                    new UInt32Property { Name = "PVPRating", Value = character.GetPvpRatingFixed32() },
                }
            };

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=Modifiers");
            avatar.AddChild(NewModifiers());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=Manipulators");
            var manipulators = NewManipulators();
            avatar.AddChild(manipulators);

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=UnitBehavior");
            avatar.AddChild(NewUnitBehavior());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=Skills");
            avatar.AddChild(NewSkills());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=Equipment");
            var equipment = NewEquipment();
            avatar.AddChild(equipment);

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=UnitContainer children=7");
            avatar.AddChild(NewUnitContainerWithSevenChildren());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=AvatarMetrics");
            avatar.AddChild(NewAvatarMetrics());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=DialogManager");
            avatar.AddChild(CreateDialogManager());

            Debug.LogError("[GC-OBJECT-FACTORY] equipmentLoad phase=start");
            Debug.LogError($"[GC-OBJECT-FACTORY] equipmentClass={character.className}");

            int equipmentCount = PopulateEquipmentFromCharacter(equipment, manipulators, character);
            Debug.LogError($"[GC-OBJECT-FACTORY] equipmentItems={equipmentCount}");
            Debug.LogError("[GC-OBJECT-FACTORY] skillLoad phase=start");
            int skillCount = PopulateSkillsFromCharacter(manipulators, character);
            Debug.LogError($"[GC-OBJECT-FACTORY] skills={skillCount}");
            Debug.LogError($"[GC-OBJECT-FACTORY] manipulatorsChildren={manipulators.Children.Count}");
            Debug.LogError($"[GC-OBJECT-FACTORY] equipmentChildren={equipment.Children.Count}");
            Debug.LogError($"[GC-OBJECT-FACTORY] avatarChildren={avatar.Children.Count}");
            Debug.LogError($"[GC-OBJECT-FACTORY] character={character.name} class={character.className}");
            Debug.LogError("[GC-OBJECT-FACTORY] loadAvatar phase=end");

            return avatar;
        }

        public static GCObject LoadCharacterSelectionAvatar(SavedCharacter character)
        {
            var avatar = LoadAvatar(character);
            var manipulators = avatar.Children.Single(child => child.DFCClass == "Manipulators");
            var equipment = avatar.Children.Single(child => child.DFCClass == "Equipment");
            foreach (var item in equipment.Children)
            {
                int index = manipulators.Children.IndexOf(item);
                if (index < 0)
                    continue;

                var activeItem = CreateEquipmentItem(item.GCClass, item.StoredRarity, item.StoredLevel, SavedItemRuntimeState.Capture(item));
                activeItem.Name = item.Name;
                activeItem.TargetSlot = item.TargetSlot;
                activeItem.Properties = item.Properties.Select(property => property switch
                {
                    UInt32Property value => (GCObjectProperty)new UInt32Property { Name = value.Name, Value = value.Value },
                    StringProperty value => new StringProperty { Name = value.Name, Value = value.Value },
                    _ => throw new InvalidDataException($"Unsupported equipment property '{property.Name}'.")
                }).ToList();
                manipulators.Children[index] = activeItem;
            }
            return avatar;
        }

        private static string GetAvatarGCClass(string className)
        {
            switch (className)
            {
                case "Fighter":
                    return "avatar.classes.FighterFemale";
                case "Mage":
                    return "avatar.classes.WarlockFemale";
                case "Ranger":
                    return "avatar.classes.RangerFemale";
                default:
                    throw new InvalidDataException($"Authored avatar class '{className ?? "<null>"}' is not supported.");
            }
        }

        private static int PopulateEquipmentFromCharacter(GCObject equipment, GCObject manipulators, SavedCharacter character)
        {
            Debug.LogError("[EQUIP-CHAR] phase=start");
            Debug.LogError($"[EQUIP-CHAR] character={character.name} id={character.id}");
            Debug.LogError($"[EQUIP-CHAR] class={character.className}");
            Debug.LogError($"[EQUIP-CHAR] equipmentData=true");
            Debug.LogError($"[EQUIP-CHAR] slot=weapon gcClass='{character.equipment?.weapon ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=armor gcClass='{character.equipment?.armor ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=helmet gcClass='{character.equipment?.helmet ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=gloves gcClass='{character.equipment?.gloves ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=boots gcClass='{character.equipment?.boots ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=shoulders gcClass='{character.equipment?.shoulders ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=shield gcClass='{character.equipment?.shield ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=ring1 gcClass='{character.equipment?.ring1 ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=ring2 gcClass='{character.equipment?.ring2 ?? "null"}'");
            Debug.LogError($"[EQUIP-CHAR] slot=amulet gcClass='{character.equipment?.amulet ?? "null"}'");
            Debug.LogError("[EQUIP-CHAR] inputPhase=end");
            int count = 0;

            int RarityOf(string slot) {
                if (character.equipment?.slotRarity != null
                    && character.equipment.slotRarity.TryGetValue(slot, out int r)) return r;
                return -1;
            }
            int LevelOf(string slot) {
                if (character.equipment?.slotLevel != null
                    && character.equipment.slotLevel.TryGetValue(slot, out int l)) return l;
                return -1;
            }
            SavedItemRuntimeState StateOf(string slot) {
                if (character.equipment?.slotItemState != null
                    && character.equipment.slotItemState.TryGetValue(slot, out SavedItemRuntimeState state)) return state;
                return null;
            }

            if (!string.IsNullOrEmpty(character.equipment.weapon))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=weapon gcClass={character.equipment.weapon}");
                var weapon = CreateEquipmentItem(character.equipment.weapon, RarityOf("weapon"), LevelOf("weapon"), StateOf("weapon"));
                if (weapon != null)
                {
                    weapon.Properties.Add(new UInt32Property { Name = "ID", Value = 10 });
                    equipment.AddChild(weapon);
                    manipulators.AddChild(weapon);
                    Debug.LogError($"[EQUIP-CHAR] slot=10 target=Manipulators,Equipment gcClass={weapon.GCClass} dfcClass={weapon.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.armor))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=armor gcClass={character.equipment.armor}");
                var armor = CreateEquipmentItem(character.equipment.armor, RarityOf("armor"), LevelOf("armor"), StateOf("armor"));
                if (armor != null)
                {
                    armor.Properties.Add(new UInt32Property { Name = "ID", Value = 6 });
                    equipment.AddChild(armor);
                    manipulators.AddChild(armor);
                    Debug.LogError($"[EQUIP-CHAR] slot=6 target=Manipulators,Equipment gcClass={armor.GCClass} dfcClass={armor.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.helmet))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=helmet gcClass={character.equipment.helmet}");
                var helmet = CreateEquipmentItem(character.equipment.helmet, RarityOf("helmet"), LevelOf("helmet"), StateOf("helmet"));
                if (helmet != null)
                {
                    helmet.Properties.Add(new UInt32Property { Name = "ID", Value = 5 });
                    equipment.AddChild(helmet);
                    manipulators.AddChild(helmet);
                    Debug.LogError($"[EQUIP-CHAR] slot=5 target=Manipulators,Equipment gcClass={helmet.GCClass} dfcClass={helmet.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.gloves))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=gloves gcClass={character.equipment.gloves}");
                var gloves = CreateEquipmentItem(character.equipment.gloves, RarityOf("gloves"), LevelOf("gloves"), StateOf("gloves"));
                if (gloves != null)
                {
                    gloves.Properties.Add(new UInt32Property { Name = "ID", Value = 2 });
                    equipment.AddChild(gloves);
                    manipulators.AddChild(gloves);
                    Debug.LogError($"[EQUIP-CHAR] slot=2 target=Manipulators,Equipment gcClass={gloves.GCClass} dfcClass={gloves.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.boots))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=boots gcClass={character.equipment.boots}");
                var boots = CreateEquipmentItem(character.equipment.boots, RarityOf("boots"), LevelOf("boots"), StateOf("boots"));
                if (boots != null)
                {
                    boots.Properties.Add(new UInt32Property { Name = "ID", Value = 7 });
                    equipment.AddChild(boots);
                    manipulators.AddChild(boots);
                    Debug.LogError($"[EQUIP-CHAR] slot=7 target=Manipulators,Equipment gcClass={boots.GCClass} dfcClass={boots.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.shoulders))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=shoulders gcClass={character.equipment.shoulders}");
                var shoulders = CreateEquipmentItem(character.equipment.shoulders, RarityOf("shoulders"), LevelOf("shoulders"), StateOf("shoulders"));
                if (shoulders != null)
                {
                    shoulders.Properties.Add(new UInt32Property { Name = "ID", Value = 12 });
                    equipment.AddChild(shoulders);
                    manipulators.AddChild(shoulders);
                    Debug.LogError($"[EQUIP-CHAR] slot=12 target=Manipulators,Equipment gcClass={shoulders.GCClass} dfcClass={shoulders.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.shield))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=shield gcClass={character.equipment.shield}");
                var shield = CreateEquipmentItem(character.equipment.shield, RarityOf("shield"), LevelOf("shield"), StateOf("shield"));
                if (shield != null)
                {
                    if (shield.DFCClass == "MeleeWeapon" || shield.DFCClass == "RangedWeapon")
                    {
                        shield.TargetSlot = 11;
                        Debug.LogError($"[EQUIP-CHAR] offhandWeapon gcClass={shield.GCClass} targetSlot=11");
                    }
                    shield.Properties.Add(new UInt32Property { Name = "ID", Value = 11 });
                    equipment.AddChild(shield);
                    manipulators.AddChild(shield);
                    Debug.LogError($"[EQUIP-CHAR] slot=11 target=Manipulators,Equipment gcClass={shield.GCClass} dfcClass={shield.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.ring1))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=ring1 gcClass={character.equipment.ring1}");
                var ring1ForEquip = CreateEquipmentItem(character.equipment.ring1, RarityOf("ring1"), LevelOf("ring1"), StateOf("ring1"));
                var ring1ForManip = CreateEquipmentItem(character.equipment.ring1, RarityOf("ring1"), LevelOf("ring1"), StateOf("ring1"));
                if (ring1ForEquip != null && ring1ForManip != null)
                {
                    ring1ForEquip.Properties.Add(new UInt32Property { Name = "ID", Value = 3 });
                    ring1ForManip.Properties.Add(new UInt32Property { Name = "ID", Value = 3 });
                    equipment.AddChild(ring1ForEquip);
                    manipulators.AddChild(ring1ForManip);
                    Debug.LogError($"[EQUIP-CHAR] slot=3 target=Manipulators,Equipment gcClass={ring1ForEquip.GCClass} dfcClass={ring1ForEquip.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.ring2))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=ring2 gcClass={character.equipment.ring2}");
                var ring2ForEquip = CreateEquipmentItem(character.equipment.ring2, RarityOf("ring2"), LevelOf("ring2"), StateOf("ring2"));
                var ring2ForManip = CreateEquipmentItem(character.equipment.ring2, RarityOf("ring2"), LevelOf("ring2"), StateOf("ring2"));
                if (ring2ForEquip != null && ring2ForManip != null)
                {
                    ring2ForEquip.TargetSlot = 4;
                    ring2ForManip.TargetSlot = 4;
                    ring2ForEquip.Properties.Add(new UInt32Property { Name = "ID", Value = 4 });
                    ring2ForManip.Properties.Add(new UInt32Property { Name = "ID", Value = 4 });

                    equipment.AddChild(ring2ForEquip);
                    manipulators.AddChild(ring2ForManip);
                    Debug.LogError($"[EQUIP-CHAR] slot=4 target=Manipulators,Equipment gcClass={ring2ForEquip.GCClass} dfcClass={ring2ForEquip.DFCClass}");
                    count++;
                }
            }

            if (!string.IsNullOrEmpty(character.equipment.amulet))
            {
                Debug.LogError($"[EQUIP-CHAR] create slot=amulet gcClass={character.equipment.amulet}");
                var amuletForEquip = CreateEquipmentItem(character.equipment.amulet, RarityOf("amulet"), LevelOf("amulet"), StateOf("amulet"));
                var amuletForManip = CreateEquipmentItem(character.equipment.amulet, RarityOf("amulet"), LevelOf("amulet"), StateOf("amulet"));
                if (amuletForEquip != null && amuletForManip != null)
                {
                    amuletForEquip.Properties.Add(new UInt32Property { Name = "ID", Value = 1 });
                    amuletForManip.Properties.Add(new UInt32Property { Name = "ID", Value = 1 });

                    equipment.AddChild(amuletForEquip);
                    manipulators.AddChild(amuletForManip);
                    Debug.LogError($"[EQUIP-CHAR] slot=1 target=Manipulators,Equipment gcClass={amuletForEquip.GCClass} dfcClass={amuletForEquip.DFCClass}");
                    count++;
                }
            }

            Debug.LogError($"[EQUIP-CHAR] equipmentItems={count}/10 target=Manipulators,Equipment");
            Debug.LogError("[EQUIP-CHAR] phase=end");
            return count;
        }

        private static int PopulateSkillsFromCharacter(GCObject manipulators, SavedCharacter character)
        {
            Debug.LogError("[SKILLS-CHAR] phase=start");
            Debug.LogError($"[SKILLS-CHAR] class={character.className}");
            Debug.LogError($"[SKILLS-CHAR] inputSkills={character.skills.Count}");
            Debug.LogError("[SKILLS-CHAR] inputPhase=end");

            int count = 0;

            foreach (var skillId in character.skills)
            {
                if (skillId != null && skillId.ToLower().StartsWith("skills.professions."))
                {
                    Debug.LogError($"[SKILLS-CHAR] skip=profession skill={skillId}");
                    continue;
                }
                string dfcClass = GetSkillClass(skillId);
                if (dfcClass == "PassiveSkill")
                {
                    Debug.LogError($"[SKILLS-CHAR] skip=passiveSkill skill={skillId}");
                    continue;
                }
                Debug.LogError($"[SKILLS-CHAR] create skill={skillId}");
                var skillObj = new GCObject
                {
                    DFCClass = dfcClass,
                    GCClass = skillId,
                    Name = null
                };
                manipulators.AddChild(skillObj);
                Debug.LogError($"[SKILLS-CHAR] skill={skillId}");
                count++;
            }

            Debug.LogError($"[SKILLS-CHAR] skills={count}");
            Debug.LogError("[SKILLS-CHAR] phase=end");
            return count;
        }

        private static GCObject CreateEquipmentItem(string gcClass, int storedRarity = -1, int storedLevel = -1, SavedItemRuntimeState itemState = null)
        {
            if (string.IsNullOrEmpty(gcClass))
            {
                Debug.LogError("[CREATE-ITEM] gcClass=null");
                return null;
            }
            Debug.LogError($"[CREATE-ITEM] create gcClass={gcClass}");
            string dfcClass = "Armor";
            string gcLower = gcClass.ToLower();

            if (gcLower.Contains("ring"))
            {
                dfcClass = "Item";
                Debug.LogError($"[CREATE-ITEM] dfcClass=Item reason=ring");
            }
            else if (gcLower.Contains("amulet"))
            {
                dfcClass = "Item";
                Debug.LogError($"[CREATE-ITEM] dfcClass=Item reason=amulet");
            }
            else if (gcLower.Contains("sword") || gcLower.Contains("axe") || gcLower.Contains("mace") ||
                gcLower.Contains("staff") || gcLower.Contains("wand") || gcLower.Contains("dagger") ||
                gcLower.Contains("pick") || gcLower.Contains("club") || gcLower.Contains("katana") ||
                gcLower.Contains("polearm"))
            {
                dfcClass = "MeleeWeapon";
                Debug.LogError($"[CREATE-ITEM] dfcClass=MeleeWeapon");
            }
            else if (gcLower.Contains("bow") || gcLower.Contains("gun") || gcLower.Contains("crossbow") || gcLower.Contains("cannon"))
            {
                dfcClass = "RangedWeapon";
                Debug.LogError($"[CREATE-ITEM] dfcClass=RangedWeapon");
            }
            else
            {
                Debug.LogError($"[CREATE-ITEM] dfcClass=Armor");
            }
            if (dfcClass == "Item")
            {
                var generalItem = AuthoredGameplayCatalog.FindGeneralItem(gcClass);
                if (generalItem == null)
                {
                    Debug.LogWarning($"[CREATE-ITEM] generalItemMissing gcClass={gcClass}");
                }
                else
                {
                    Debug.LogError($"[CREATE-ITEM] generalItem={generalItem.Label}");
                }
            }
            else
            {
                var itemData = AuthoredGameplayCatalog.FindItem(gcClass);
                if (itemData == null)
                {
                    Debug.LogWarning($"[CREATE-ITEM] itemMissing gcClass={gcClass} action=create");
                }
                else
                {
                    Debug.LogError($"[CREATE-ITEM] item={itemData.name}");
                }
            }
            var result = new GCObject
            {
                DFCClass = dfcClass,
                GCClass = gcClass,
                Name = null,
                Properties = new List<GCObjectProperty>(),
                StoredRarity = storedRarity,
                StoredLevel = storedLevel
            };
            itemState?.ApplyTo(result);
            Debug.LogError($"[CREATE-ITEM] gcClass={result.GCClass} dfcClass={result.DFCClass} storedRarity={storedRarity} storedLevel={storedLevel}");
            return result;
        }

        public static GCObject NewModifiers()
        {
            return new GCObject
            {
                DFCClass = "Modifiers",
                GCClass = "Modifiers",
                Name = null
            };
        }

        public static GCObject NewManipulators()
        {
            return new GCObject
            {
                DFCClass = "Manipulators",
                GCClass = "Manipulators",
                Name = null
            };
        }

        public static GCObject NewUnitBehavior()
        {
            return new GCObject
            {
                DFCClass = "UnitBehavior",
                GCClass = "avatar.base.UnitBehavior",
                Name = null
            };
        }

        public static GCObject NewSkills()
        {
            return new GCObject
            {
                DFCClass = "Skills",
                GCClass = "avatar.base.skills",
                Name = null
            };
        }

        public static GCObject NewEquipment()
        {
            Debug.LogError("[GC-OBJECT-FACTORY] createEquipment children=0 source=character");
            var equipment = new GCObject
            {
                DFCClass = "Equipment",
                GCClass = "avatar.base.Equipment",
                Name = null
            };

            Debug.LogError("[GC-OBJECT-FACTORY] equipmentChildren=0 source=character");
            return equipment;
        }

        public static GCObject NewUnitContainerWithSevenChildren()
        {
            var unitContainer = new GCObject
            {
                DFCClass = "UnitContainer",
                GCClass = "UnitContainer",
                Name = null
            };

            var childGCClasses = new[] { "avatar.base.Inventory", "avatar.base.TradeInventory", "avatar.base.Bank", "avatar.base.Bank2", "avatar.base.Bank3", "avatar.base.Bank4", "avatar.base.Bank5", "avatar.base.Bank6", "avatar.base.Bank7" };

            for (int childIndex = 0; childIndex < 9; childIndex++)
            {
                var child = new GCObject
                {
                    DFCClass = "Inventory",
                    GCClass = childGCClasses[childIndex],
                    Name = null
                };
                unitContainer.AddChild(child);
            }

            return unitContainer;
        }

        public static GCObject NewAvatarMetrics()
        {
            var avatarMetrics = new GCObject
            {
                DFCClass = "AvatarMetrics",
                GCClass = "AvatarMetrics",
                Name = null
            };

            var extraData = new List<byte>();

            for (int metricIndex = 0; metricIndex < 5; metricIndex++)
                extraData.AddRange(BitConverter.GetBytes((uint)0));

            extraData.AddRange(BitConverter.GetBytes((uint)0));
            extraData.AddRange(BitConverter.GetBytes((uint)0));

            for (int metricIndex = 0; metricIndex < 5; metricIndex++)
            {
                extraData.AddRange(BitConverter.GetBytes((ulong)0));
            }

            extraData.AddRange(BitConverter.GetBytes((uint)0));
            extraData.AddRange(BitConverter.GetBytes((uint)0));
            extraData.AddRange(BitConverter.GetBytes((uint)0));
            extraData.AddRange(BitConverter.GetBytes((uint)0));

            avatarMetrics.ExtraData = extraData.ToArray();

            return avatarMetrics;
        }

        public static GCObject LoadMinimalAvatar()
        {
            Debug.LogError("[GC-OBJECT-FACTORY] loadMinimalAvatar phase=start");
            Debug.LogError("[GC-OBJECT-FACTORY] minimalAvatar mode=creationUI equipment=0");

            var avatar = new GCObject
            {
                DFCClass = "Avatar",
                GCClass = "avatar.classes.FighterFemale",
                Name = "avatar",
                Properties = new List<GCObjectProperty>
                {
                    new UInt32Property { Name = "Skin", Value = 0 },
                    new UInt32Property { Name = "Face", Value = 0 },
                    new UInt32Property { Name = "FaceFeature", Value = 0 },
                    new UInt32Property { Name = "Hair", Value = 0 },
                    new UInt32Property { Name = "HairColor", Value = 0 },
                    new UInt32Property { Name = "TotalWorldTime", Value = 0 },
                    new UInt32Property { Name = "LastKnownQueueLevel", Value = 0 },
                    new UInt32Property { Name = "HasBlingGnome", Value = 0 },
                    new UInt32Property { Name = "Level", Value = 1 },
                    new UInt32Property { Name = "HitPoints", Value = 100 },
                    new UInt32Property { Name = "ManaPoints", Value = 100 },
                    new UInt32Property { Name = "Experience", Value = 0 },
                    new UInt32Property { Name = "AttributePoints", Value = 0 },
                    new UInt32Property { Name = "ReSpecTimer", Value = 0 },
                    new UInt32Property { Name = "StrengthPoints", Value = 0 },
                    new UInt32Property { Name = "AgilityPoints", Value = 0 },
                    new UInt32Property { Name = "ToughnessPoints", Value = 0 },
                    new UInt32Property { Name = "PowerPoints", Value = 0 },
                    new UInt32Property { Name = "MaxTotalAttributePool", Value = 0 },
                    new UInt32Property { Name = "PVPRating", Value = SavedCharacter.DefaultPvpRating * 256u },
                }
            };

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=Modifiers");
            avatar.AddChild(NewModifiers());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=Manipulators children=0");
            avatar.AddChild(NewManipulators());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=UnitBehavior");
            avatar.AddChild(NewUnitBehavior());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=Skills");
            avatar.AddChild(NewSkills());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=Equipment children=0");
            avatar.AddChild(NewEquipment());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=UnitContainer");
            avatar.AddChild(NewUnitContainerWithSevenChildren());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=AvatarMetrics");
            avatar.AddChild(NewAvatarMetrics());

            Debug.LogError("[GC-OBJECT-FACTORY] addChild=DialogManager");
            avatar.AddChild(CreateDialogManager());

            Debug.LogError($"[GC-OBJECT-FACTORY] minimalAvatar children={avatar.Children.Count} equipment=0");
            Debug.LogError("[GC-OBJECT-FACTORY] minimalAvatar phase=end");

            return avatar;
        }

        public static GCObject CreateDialogManager()
        {
            return new GCObject
            {
                DFCClass = "DialogManager",
                GCClass = "DialogManager",
                Name = null
            };
        }

        public static GCObject NewProcModifier()
        {
            return new GCObject
            {
                DFCClass = "ProcModifier",
                GCClass = "ProcModifier",
                Name = null
            };
        }
    }
}
