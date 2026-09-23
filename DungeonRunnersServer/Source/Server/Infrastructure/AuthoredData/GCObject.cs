using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Data
{
    public class GCObject
    {
        public static readonly byte DFC_VERSION = 0x2D;

        public uint Id { get; set; }
        public uint PlayerGroupId { get; set; }
        public uint PlayerPvpWins { get; set; }
        public int PlayerMinimumItemQuality { get; set; } = 1;
        public byte PlayerMonsterDifficulty { get; set; }
        public bool PlayerHasPvpRating { get; set; }
        public string Name { get; set; } = "";
        public string DFCClass { get; set; } = "";
        public string GCClass { get; set; } = "";

        private static readonly HashSet<string> _itemsPalNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "1haxepal", "1hmacepal", "1hstaffpal", "1hswordpal",
            "fighterbodypal", "fighterbootspal", "fighterglovespal", "fighterhelmpal",
            "fightershieldpal", "fightershoulderspal",
            "itempackpal", "itempackvisuals",
            "magebodypal", "magebootspal", "mageglovespal", "magehelmpal",
            "mageshieldpal", "mageshoulderspal",
            "rangerbodypal", "rangerbootspal", "rangerglovespal", "rangerhelmpal",
            "rangershoulderspal",
            "shieldvisuals", "voucherpal",
        };

        public string GetPacketGCClass() => GetPacketGCClassFor(GCClass);

        public static string GetPacketGCClassFor(string gcClass)
        {
            if (string.IsNullOrEmpty(gcClass)) return gcClass ?? string.Empty;
            string lower = gcClass.ToLowerInvariant();
            if (lower.StartsWith("items.pal."))
                return lower;
            int dot = lower.IndexOf('.');
            if (dot > 0)
            {
                string ns = lower.Substring(0, dot);
                if (_itemsPalNamespaces.Contains(ns))
                    return "items.pal." + lower;
            }
            return lower;
        }
        public List<GCObjectProperty> Properties { get; set; } = new List<GCObjectProperty>();
        public List<GCObject> Children { get; set; } = new List<GCObject>();
        public byte[] ExtraData { get; set; } = Array.Empty<byte>();
        public uint? TargetSlot { get; set; } = null;
        public string PresetScaleMod { get; set; } = null;
        public int StoredRarity { get; set; } = -1;
        public int StoredLevel { get; set; } = -1;
        public bool HasGeneratedItemState { get; set; }
        public bool RolledRequiresMembership { get; set; }
        public bool GeneratedRequiresMembership { get; set; }
        public List<string> GeneratedItemModifiers { get; set; } = new List<string>();

        public static int DetectRarityFromGCClass(string gcClass)
        {
            if (string.IsNullOrEmpty(gcClass)) return 0;
            string lower = gcClass.ToLowerInvariant();
            if (lower.Contains("mythicpal")) return 5;
            if (lower.Contains("mythic")) return 5;
            if (lower.Contains("unique")) return 4;
            if (lower.Contains("rare")) return 3;
            if (lower.Contains("magical") || lower.Contains("magic")) return 2;
            if (lower.Contains("superior")) return 1;
            return 0;
        }

        public int GetEffectiveRarity()
        {
            if (StoredRarity >= 0) return StoredRarity;
            return DetectRarityFromGCClass(GCClass);
        }

        public bool GetRequiresMembership()
        {
            if (HasGeneratedItemState) return GeneratedRequiresMembership;
            GCNode description = ResolveItemDescription(GCClass);
            if (description?.GetBool("ForceRequiresMembership", false) == true) return true;
            if (description?.GetBool("ForceNotRequiresMembership", false) == true) return false;
            return description?.GetBool("RequiresMembership", false) == true;
        }

        private List<(int Slot, string ModRef)> ResolveItemWireMods()
        {
            return DungeonRunners.Data.ItemStatDatabase.Instance.GetDynamicItemWireMods(this);
        }

        private string GetModifierGCClass()
        {
            if (!string.IsNullOrEmpty(PresetScaleMod))
            {
                Debug.LogError($"[MODIFIER] Item '{GCClass}' -> Using PresetScaleMod '{PresetScaleMod}'");
                return PresetScaleMod;
            }

            string armorType = "Scale";
            string gcLower = GCClass.ToLower();

            if (gcLower.Contains("plate"))
                armorType = "Plate";
            else if (gcLower.Contains("crystal"))
                armorType = "Crystal";
            else if (gcLower.Contains("chain"))
                armorType = "Chain";
            else if (gcLower.Contains("leather"))
                armorType = "Leather";
            else if (gcLower.Contains("cloth"))
                armorType = "Cloth";
            else if (gcLower.Contains("scale"))
                armorType = "Scale";

            int effective = GetEffectiveRarity();
            var itemRarity = effective >= (int)DungeonRunners.Gameplay.ItemRarity.Normal
                && effective <= (int)DungeonRunners.Gameplay.ItemRarity.Mythic
                ? (DungeonRunners.Gameplay.ItemRarity)effective
                : DungeonRunners.Gameplay.RPGSettings.ResolveItemRarity(GCClass);
            string scaleMod = DungeonRunners.Gameplay.RPGSettings.GetDeterministicScaleMod(GCClass, itemRarity);

            Debug.LogError($"[MODIFIER] Item '{GCClass}' -> ArmorType '{armorType}' -> rarity={itemRarity} -> ScaleMod '{scaleMod}'");
            return scaleMod;
        }

        public void AddProperty(GCObjectProperty property)
        {
            Properties.Add(property);
        }

        public void AddChild(GCObject child)
        {
            Children.Add(child);
        }

        public void WriteFullGCObject(LEWriter writer)
        {
            WriteObject(writer, new GCObjectWriteContext());
        }

        private void WriteObject(LEWriter writer, GCObjectWriteContext context)
        {
            if (!context.Written.Add(this))
            {
                writer.WriteByte(0x02);
                writer.WriteUInt32(Id);
                return;
            }

            Debug.Log($"[GC-OBJECT] writeDfc id={Id} dfcClass='{DFCClass}' gcClass='{GCClass}' props={Properties.Count} children={Children.Count}");

            writer.WriteByte(DFC_VERSION);

            uint dfcHash = HashDjb2(DFCClass);
            writer.WriteUInt32(dfcHash);
            Debug.Log($"[GC-OBJECT] dfcClass='{DFCClass}' hash=0x{dfcHash:X8}");

            writer.WriteUInt32(Id);

            writer.WriteCString(Name);

            var children = Children?.Where(child => child != null).ToList() ?? new List<GCObject>();
            writer.WriteUInt32((uint)children.Count);

            foreach (var child in children)
            {
                child.WriteObject(writer, context);
            }

            string gcForHash = GetPacketGCClassFor(GCClass);
            uint gcHash = HashDjb2(gcForHash);
            writer.WriteUInt32(gcHash);
            Debug.Log($"[GC-OBJECT] gcClass='{GCClass}' hash=0x{gcHash:X8}");
            if (DFCClass == "Avatar")
            {
                Debug.LogError($"[CHARLIST-AVATAR] Avatar GCClass='{GCClass}' -> hash 0x{gcHash:X8}");
            }
            foreach (var prop in Properties)
            {
                prop.WriteDFC(writer);
            }

            writer.WriteUInt32(0);

            if (DFCClass == "Player")
            {
                WritePlayerObjectTail(writer, context);
            }
            else if (DFCClass == "UnitContainer")
            {
                writer.WriteByte(0x00);
            }
            else if (ExtraData.Length > 0)
            {
                writer.WriteBytes(ExtraData);
                Debug.Log($"[GC-OBJECT] extraDataBytes={ExtraData.Length}");
            }
        }

        private void WritePlayerObjectTail(LEWriter writer, GCObjectWriteContext context)
        {
            writer.WriteCString(Name);
            writer.WriteCString(string.Empty);
            writer.WriteUInt32(PlayerPvpWins);
            writer.WriteUInt32(PlayerHasPvpRating ? 1u : 0u);

            GCObject avatar = Children.FirstOrDefault(child => child != null && child.DFCClass == "Avatar");
            if (avatar == null)
            {
                writer.WriteByte(0x00);
            }
            else
            {
                avatar.WriteObject(writer, context);
            }

            writer.WriteByte(0x00);
            writer.WriteByte(0x0C);
            writer.WriteCString(PlayerOptions.DifficultyName(PlayerMonsterDifficulty));
            writer.WriteByte(0x01);
            writer.WriteByte(0x00);
            writer.WriteInt32(PlayerMinimumItemQuality);
        }

        private sealed class GCObjectWriteContext
        {
            public HashSet<GCObject> Written { get; } = new HashSet<GCObject>();
        }



        public bool SoulBound { get; set; }
        public bool NoSell { get; set; }
        public ushort SoulBoundCountdown { get; set; } = ushort.MaxValue;

        public void WriteItemData(
            LEWriter writer,
            uint containerItemId,
            byte inventoryX,
            byte inventoryY,
            byte quantity,
            int itemLevel,
            bool includeType = true)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (string.IsNullOrWhiteSpace(GCClass))
                throw new InvalidOperationException("Item GCClass is required.");
            if (itemLevel < 0 || itemLevel > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(itemLevel));

            if (includeType)
            {
                writer.WriteByte(0xFF);
                writer.WriteCString(GetPacketGCClass());
            }
            writer.WriteUInt32(containerItemId);
            writer.WriteByte(inventoryX);
            writer.WriteByte(inventoryY);
            writer.WriteByte(quantity);
            writer.WriteByte((byte)itemLevel);

            byte flags = 0;
            if (SoulBound)
                flags |= 0x01;
            if (NoSell)
                flags |= 0x02;
            if (SoulBoundCountdown != ushort.MaxValue)
                flags |= 0x04;
            if (GetRequiresMembership())
                flags |= 0x08;
            writer.WriteByte(flags);
            if ((flags & 0x04) != 0)
                writer.WriteUInt16(SoulBoundCountdown);

            var itemStats = DungeonRunners.Data.ItemStatDatabase.Instance;
            if (!itemStats.TryGetItemReadDataSlotCount(GCClass, out int itemReadDataSlotCount))
                throw new InvalidDataException($"Unresolved item readData layout for '{GCClass}'.");
            int staticModifierCount = itemReadDataSlotCount - 1;
            for (int staticModifierIndex = 0; staticModifierIndex < staticModifierCount; staticModifierIndex++)
                writer.WriteByte(0x00);

            List<(int Slot, string ModRef)> modifiers = ResolveItemWireMods();
            WriteChildCount(writer, modifiers.Count);
            foreach ((int _, string modifierRef) in modifiers)
            {
                uint modifierTypeId = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modifierRef);
                if (modifierTypeId == 0)
                    throw new InvalidOperationException($"Unresolved item modifier '{modifierRef}' for '{GCClass}'.");

                writer.WriteByte(0x04);
                writer.WriteUInt32(modifierTypeId);

                byte modifierLevel = (byte)itemLevel;
                byte modifierFlags = modifierLevel == 0 ? (byte)0 : (byte)0x01;
                writer.WriteByte(modifierFlags);
                if ((modifierFlags & 0x01) != 0)
                    writer.WriteByte(modifierLevel);
            }
        }

        public void WriteInitForDroppedItem(LEWriter writer, int playerLevel, int quantity = 1)
        {
            if (quantity < 1 || quantity > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(quantity));
            WriteItemData(writer, ResolveContainerItemId(), 0, 0, (byte)quantity, ResolveWireItemLevel(playerLevel));
        }

        public void WriteInitForInventory(
            LEWriter writer,
            byte posX,
            byte posY,
            uint inventorySlot,
            int playerLevel,
            byte quantity = 1)
        {
            WriteItemData(writer, inventorySlot, posX, posY, quantity, ResolveWireItemLevel(playerLevel));
        }

        private uint ResolveContainerItemId()
        {
            uint propertyValue = GetPropertyUInt32("ContainerItemID");
            if (propertyValue != 0)
                return propertyValue;
            propertyValue = GetPropertyUInt32("Slot");
            if (propertyValue != 0)
                return propertyValue;
            return TargetSlot ?? GetEquipmentSlotFromGCClass();
        }

        public int ResolveNativeItemLevel(int observerLevel)
        {
            return ResolveWireItemLevel(observerLevel);
        }

        private int ResolveWireItemLevel(int fallbackLevel)
        {
            if (StoredLevel >= 0)
                return StoredLevel;
            int requiredLevel = GetItemRequiredLevel();
            if (requiredLevel > 0)
                return requiredLevel;
            return Math.Max(1, fallbackLevel);
        }

        internal static void WriteChildCount(LEWriter writer, int count)
        {
            if (count < 0 || count > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count < byte.MaxValue)
            {
                writer.WriteByte((byte)count);
                return;
            }
            writer.WriteByte(byte.MaxValue);
            writer.WriteUInt16((ushort)count);
        }

        private uint GetPropertyUInt32(string propertyName)
        {
            var prop = Properties?.FirstOrDefault(p => p.Name == propertyName);
            if (prop is UInt32Property uintProp)
            {
                return uintProp.Value;
            }
            return 0;
        }

        public uint GetEquipmentSlotFromGCClass()
        {
            if (TargetSlot.HasValue)
                return TargetSlot.Value;

            if (TryResolveEquipmentSlotType(GCClass, out uint slotType))
                return slotType;
            throw new InvalidDataException($"Equipment SlotType is missing for authored item '{GCClass ?? ""}'.");
        }

        public static bool TryResolveEquipmentSlotType(string gcClass, out uint slotType)
        {
            slotType = 0;
            GCNode desc = ResolveItemDescription(gcClass);
            if (desc == null) return false;
            if (!desc.HasProperty("SlotType")) return false;
            int value = desc.GetInt("SlotType", 0);
            if (value <= 0) return false;
            slotType = (uint)value;
            return true;
        }

        public static bool TryResolveWeaponClass(string gcClass, out string weaponClass)
        {
            weaponClass = string.Empty;
            GCNode desc = ResolveItemDescription(gcClass);
            if (desc == null) return false;
            string value = desc.GetString("WeaponClass", string.Empty);
            if (string.IsNullOrWhiteSpace(value)) return false;
            weaponClass = value.Trim();
            return true;
        }

        public static GCNode ResolveItemDescription(string gcClass)
        {
            if (string.IsNullOrWhiteSpace(gcClass)) return null;
            if (GCDatabase.Instance == null || !GCDatabase.Instance.IsLoaded) return null;
            GCNode node = GCDatabase.Instance.ResolveWithInheritance(gcClass);
            if (node == null)
                node = GCDatabase.Instance.ResolveWithInheritance(GetPacketGCClassFor(gcClass));
            if (node == null) return null;
            return node.GetChild("Description") ?? node;
        }

        public int GetItemRequiredLevel()
        {
            return ItemData.GetRequiredLevelFromGCClass(GCClass);
        }

        public static uint HashDjb2(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return 5381;
            }

            uint hash = 5381;
            string lower = s.ToLowerInvariant();

            foreach (char c in lower)
            {
                hash = ((hash << 5) + hash) + (uint)c;
            }

            return hash & 0xFFFFFFFF;
        }

    }

    public abstract class GCObjectProperty
    {
        public string Name { get; set; } = "";
        public abstract void Serialize(System.IO.BinaryWriter writer);
        public abstract void WriteDFC(LEWriter writer);
    }

    public class StringProperty : GCObjectProperty
    {
        public string Value { get; set; } = "";

        public override void Serialize(System.IO.BinaryWriter writer)
        {
            foreach (var b in Encoding.UTF8.GetBytes(Name))
                writer.Write(b);
            writer.Write((byte)0);
            writer.Write((byte)1);
            foreach (var b in Encoding.UTF8.GetBytes(Value))
                writer.Write(b);
            writer.Write((byte)0);
        }

        public override void WriteDFC(LEWriter writer)
        {
            uint nameHash = GCObject.HashDjb2(Name);
            writer.WriteUInt32(nameHash);
            writer.WriteCString(Value);
        }
    }

    public class UInt32Property : GCObjectProperty
    {
        public uint Value { get; set; }

        public override void Serialize(System.IO.BinaryWriter writer)
        {
            foreach (var b in Encoding.UTF8.GetBytes(Name))
                writer.Write(b);
            writer.Write((byte)0);
            writer.Write((byte)2);
            writer.Write(Value);
        }

        public override void WriteDFC(LEWriter writer)
        {
            uint nameHash = GCObject.HashDjb2(Name);
            writer.WriteUInt32(nameHash);
            writer.WriteUInt32(Value);
        }
    }
}
