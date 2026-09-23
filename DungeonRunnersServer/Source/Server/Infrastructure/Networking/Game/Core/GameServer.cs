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
    public enum EntitySynchInfoContext
    {
        Unknown,
        WorldInterval,
        BaselineReplay,
        RecoveryReplay,
        RepeatReplay,
        InventoryReplay,
        EquipmentReplay,
        LateArmorReplay,
        ControlGrant,
        ControlAck,
        MoverAck,
        PlayerActionResponse,
        PlayerBasicAttackResponse,
        MonsterAction,
        MonsterMove,
        MonsterDamage,
        EntityInitPrimer
    }

    public partial class GameServer : MonoBehaviour
    {
        private const uint MSG_DEST = 0x000F01;
        private const uint MSG_SOURCE = 0x000F01;
        private static bool VerbosePacketLogging => ServerDiagnostics.IsEnabled("verbosePacketLogging");
        private bool _allowFlush = false;
        private bool _isRunning;
        private int _nextConnId = 1;
        private Dictionary<int, string> _users = new Dictionary<int, string>();
        private Dictionary<int, uint> _peerId24 = new Dictionary<int, uint>();
        private Dictionary<string, List<GCObject>> _persistentCharacters = new Dictionary<string, List<GCObject>>();
        private Dictionary<int, bool> _charListSent = new Dictionary<int, bool>();
        private Dictionary<string, GCObject> _selectedCharacter = new Dictionary<string, GCObject>();
        private readonly Dictionary<string, SavedCharacter> _activeCharacter = new Dictionary<string, SavedCharacter>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _persistenceSaveSuppressedConnections = new HashSet<int>();
        private const ushort GENERAL_ENTITY_ID_MIN = 170;
        private const ushort GENERAL_ENTITY_ID_MAX = 0x5FFF;
        private const ushort LOOT_ID_MIN = 0xC000;
        private const ushort LOOT_ID_MAX = 0xFDFF;
        private const ushort PORTAL_ID_MIN = 0xFE00;
        private const ushort PORTAL_ID_MAX = 0xFEFF;
        private readonly Dictionary<string, ushort> _freePlayerModifierComponentIds = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        private ActiveModifiers _activeModifiers = new ActiveModifiers();

        private Dictionary<string, bool> _playerIsFree = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _playerIsAdmin = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Dictionary<string, ushort>> _remoteBehaviorIds = new Dictionary<string, Dictionary<string, ushort>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Dictionary<string, ushort>> _remoteAvatarIds = new Dictionary<string, Dictionary<string, ushort>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Dictionary<string, ushort>> _remotePlayerIds = new Dictionary<string, Dictionary<string, ushort>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (ushort AvatarId, uint HPWire, uint ManaWire)> _remoteAvatarResourceState = new Dictionary<string, (ushort, uint, uint)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, byte> _groupMemberHealthManaState = new Dictionary<uint, byte>();
        private Dictionary<ushort, ChestSpawnData> _chestEntities = new Dictionary<ushort, ChestSpawnData>();

        private AdminCommands _adminCommands;
        private const int CLIENT_CONTACT_RANGE_EPSILON_FIXED = 0x10;
        private const int StatPointsPerLevel = 5;
        private Dictionary<uint, uint> _monsterBehaviorIds = new Dictionary<uint, uint>();
        private List<PendingMonsterBehaviorUpdate> _pendingMonsterBehaviorUpdates = new List<PendingMonsterBehaviorUpdate>();
        private readonly List<PendingPlayerUseTargetActionInput> _pendingPlayerUseTargetActionInputs = new List<PendingPlayerUseTargetActionInput>();
        private readonly List<PendingPlayerCancelActionInput> _pendingPlayerCancelActionInputs = new List<PendingPlayerCancelActionInput>();
        private readonly List<PendingPlayerUsePositionWeaponActionInput> _pendingPlayerUsePositionWeaponActionInputs = new List<PendingPlayerUsePositionWeaponActionInput>();
        private readonly List<PendingPlayerUsePositionBehaviorAction> _pendingPlayerUsePositionBehaviorActions = new List<PendingPlayerUsePositionBehaviorAction>();
        private readonly List<PendingOwnerClientControlInput> _pendingOwnerClientControlInputs = new List<PendingOwnerClientControlInput>();
        private readonly List<PendingHotbarPassiveAdmission> _pendingHotbarPassiveAdmissions = new List<PendingHotbarPassiveAdmission>();
        private readonly object _pendingHotbarPassiveAdmissionLock = new object();
        private long _nextHotbarPassiveAdmissionSequence;
        private long _nextPlayerUsePositionAdmissionSequence;
        private long _nextPlayerUseTargetAdmissionSequence;
        private UnitContainer _unitContainer;
        private Equipment _equipment;
        private Dictionary<string, PlayerState> _playerStates = new Dictionary<string, PlayerState>();
        private Dictionary<string, Dictionary<uint, GCObject>> _playerEquippedItems = new Dictionary<string, Dictionary<uint, GCObject>>();
        private Dictionary<string, ushort> _playerManipulatorsIds = new Dictionary<string, ushort>();
        private const uint DROPPED_ITEM_CLEANUP_INTERVAL_TICKS = 1800u;
        private const int DROPPED_ITEM_EXPIRE_MINUTES = 15;
        private Dictionary<string, Dictionary<uint, (GCObject item, byte x, byte y)>> _playerInventoryItems = new Dictionary<string, Dictionary<uint, (GCObject, byte, byte)>>();
        private Dictionary<string, List<uint>> _playerInventoryOrder = new Dictionary<string, List<uint>>();
        private Dictionary<string, HashSet<int>> _occupiedInventorySlots = new Dictionary<string, HashSet<int>>();
        private Dictionary<string, uint> _inventorySlotCounters = new Dictionary<string, uint>();
        private const uint AUTO_SAVE_INTERVAL_TICKS = 900u;
        private const uint MERCHANT_REFRESH_INTERVAL_TICKS = 3u;
        private const uint GROUP_HEALTH_BROADCAST_INTERVAL_TICKS = 30u;
        private bool _maintenanceScheduleInitialized;
        private uint _nextMerchantRefreshTick;
        private uint _nextDroppedItemCleanupTick;
        private uint _nextGroupHealthBroadcastTick;
        private uint _nextAutoSaveTick;
        private readonly List<RRConnection> _pendingAutoSaveConnections = new List<RRConnection>();
        private int _pendingAutoSaveConnectionIndex;

        private static bool IsBankContainer(byte containerId)
            => containerId == 0x0C || (containerId >= 0x0E && containerId <= 0x13);

        private static bool IsPersistedInventoryContainer(byte containerId)
            => containerId == 0x0B || IsBankContainer(containerId);

        private static int ContainerHeight(byte containerId) => IsBankContainer(containerId) ? 14 : 8;
        private const int CONTAINER_WIDTH = 10;

        public bool IsInventoryPlacementInBounds(byte x, byte y, int width, int height, byte containerId)
        {
            return IsPersistedInventoryContainer(containerId) && width > 0 && height > 0
                && x + width <= CONTAINER_WIDTH && y + height <= ContainerHeight(containerId);
        }

        public IEnumerable<KeyValuePair<uint, (GCObject item, byte x, byte y)>> GetOrderedInventoryItems(string connId, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_playerInventoryItems.TryGetValue(key, out var inventory))
                yield break;
            if (!_playerInventoryOrder.TryGetValue(key, out List<uint> order))
                throw new InvalidOperationException($"missing-inventory-order:{key}");
            foreach (uint slot in order)
            {
                if (!inventory.TryGetValue(slot, out var entry))
                    throw new InvalidOperationException($"stale-inventory-order:{key}:{slot}");
                yield return new KeyValuePair<uint, (GCObject item, byte x, byte y)>(slot, entry);
            }
        }

        public void TrackInventoryItem(string connId, uint index, GCObject item, byte x, byte y, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_playerInventoryItems.ContainsKey(key))
                _playerInventoryItems[key] = new Dictionary<uint, (GCObject, byte, byte)>();
            if (!_playerInventoryOrder.ContainsKey(key))
                _playerInventoryOrder[key] = new List<uint>();
            if (!_playerInventoryItems[key].ContainsKey(index))
                _playerInventoryOrder[key].Add(index);
            _playerInventoryItems[key][index] = (item, x, y);
            Debug.LogError($"[INV-TRACK] Player {connId} container=0x{containerId:X2}: Index {index} = {item.GCClass} at ({x}, {y})");
        }

        public (int x, int y) FindNextFreeInventorySlot(string connId, int width, int height, byte containerId = 0x0B)
        {
            int invHeight = ContainerHeight(containerId);

            for (byte y = 0; y <= invHeight - height; y++)
            {
                for (byte x = 0; x <= CONTAINER_WIDTH - width; x++)
                {
                    if (!IsInventorySlotOccupied(connId, x, y, width, height, containerId))
                    {
                        return (x, y);
                    }
                }
            }
            return (-1, -1);
        }


        private Dictionary<string, Dictionary<uint, int>> _inventoryStackCounts = new Dictionary<string, Dictionary<uint, int>>();

        public int GetStackCount(string connId, uint slot, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_inventoryStackCounts.ContainsKey(key) && _inventoryStackCounts[key].ContainsKey(slot))
                return _inventoryStackCounts[key][slot];
            return 1;
        }

        public void SetStackCount(string connId, uint slot, int count, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_inventoryStackCounts.ContainsKey(key))
                _inventoryStackCounts[key] = new Dictionary<uint, int>();
            _inventoryStackCounts[key][slot] = count;
        }

        public void RemoveEquippedItem(string connId, uint slot)
        {
            if (_playerEquippedItems.ContainsKey(connId) && _playerEquippedItems[connId].ContainsKey(slot))
            {
                _playerEquippedItems[connId].Remove(slot);
                Debug.LogError($"[EQUIP-TRACK] Removed item from slot {slot}");
            }
        }

        public uint GetNextInventorySlot(string playerId)
        {
            if (!_inventorySlotCounters.ContainsKey(playerId))
            {
                _inventorySlotCounters[playerId] = 100;
            }

            uint slot = _inventorySlotCounters[playerId];
            _inventorySlotCounters[playerId]++;

            Debug.LogError($"[INV-SLOT] Generated new inventory slot {slot} for player {playerId}");
            return slot;
        }







        public (GCObject item, byte x, byte y)? GetAndRemoveInventoryItem(string connId, uint index, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_playerInventoryItems.ContainsKey(key) && _playerInventoryItems[key].ContainsKey(index))
            {
                var data = _playerInventoryItems[key][index];
                _playerInventoryItems[key].Remove(index);
                if (_playerInventoryOrder.ContainsKey(key))
                    _playerInventoryOrder[key].Remove(index);

                ItemData itemData = AuthoredGameplayCatalog.FindItem(data.item.GCClass);
                int width = itemData?.inventoryWidth ?? 1;
                int height = itemData?.inventoryHeight ?? 1;

                FreeInventorySlots(connId, data.x, data.y, width, height, containerId);
                return data;
            }
            Debug.LogError($"[INV-TRACK]  No item at index {index} in container 0x{containerId:X2}!");
            return null;
        }





        public bool IsInventorySlotOccupied(string connId, byte x, byte y, int width, int height, byte containerId = 0x0B)
        {
            int invHeight = ContainerHeight(containerId);
            if (x + width > CONTAINER_WIDTH || y + height > invHeight)
                return true;

            string key = InvKey(connId, containerId);
            if (!_occupiedInventorySlots.ContainsKey(key))
                return false;

            for (int dx = 0; dx < width; dx++)
            {
                for (int dy = 0; dy < height; dy++)
                {
                    int slotIndex = (y + dy) * CONTAINER_WIDTH + (x + dx);
                    if (_occupiedInventorySlots[key].Contains(slotIndex))
                    {
                        Debug.LogError($"[INV-TRACK]  Cell ({x + dx}, {y + dy}) in container 0x{containerId:X2} is already occupied!");
                        return true;
                    }
                }
            }
            return false;
        }

        public void OccupyInventorySlots(string connId, byte x, byte y, int width, int height, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_occupiedInventorySlots.ContainsKey(key))
                _occupiedInventorySlots[key] = new HashSet<int>();

            for (int dx = 0; dx < width; dx++)
            {
                for (int dy = 0; dy < height; dy++)
                {
                    int slotIndex = (y + dy) * CONTAINER_WIDTH + (x + dx);
                    _occupiedInventorySlots[key].Add(slotIndex);
                }
            }
            Debug.LogError($"[INV-TRACK]  Occupied {width}x{height} slots starting at ({x}, {y}) in container 0x{containerId:X2}");
        }

        public void FreeInventorySlots(string connId, byte x, byte y, int width, int height, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_occupiedInventorySlots.ContainsKey(key))
                return;

            for (int dx = 0; dx < width; dx++)
            {
                for (int dy = 0; dy < height; dy++)
                {
                    int slotIndex = (y + dy) * CONTAINER_WIDTH + (x + dx);
                    _occupiedInventorySlots[key].Remove(slotIndex);
                }
            }
            Debug.LogError($"[INV-TRACK]  Freed {width}x{height} slots starting at ({x}, {y}) in container 0x{containerId:X2}");
        }

        public (uint slot, GCObject item, byte x, byte y)? FindInventoryItemByGCClass(string connId, string gcClass, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_playerInventoryItems.ContainsKey(key)) return null;
            if (!_playerInventoryOrder.TryGetValue(key, out List<uint> inventoryOrder))
                throw new InvalidOperationException($"missing-inventory-order:{key}");
            string gcLower = gcClass.ToLower();
            foreach (uint inventoryIndex in inventoryOrder)
            {
                if (!_playerInventoryItems[key].TryGetValue(inventoryIndex, out var inventoryEntry))
                    throw new InvalidOperationException($"stale-inventory-order:{key}:{inventoryIndex}");
                if (inventoryEntry.Item1.GCClass.ToLower() == gcLower)
                    return (inventoryIndex, inventoryEntry.Item1, inventoryEntry.Item2, inventoryEntry.Item3);
            }
            return null;
        }

        private static string CaptureSavedEquipmentSlot(StartingEquipment equipment, Dictionary<uint, GCObject> equippedItems, uint runtimeSlot, string savedSlot)
        {
            equipment.slotRarity ??= new Dictionary<string, int>();
            equipment.slotLevel ??= new Dictionary<string, int>();
            equipment.slotItemState ??= new Dictionary<string, SavedItemRuntimeState>();
            if (equippedItems == null || !equippedItems.TryGetValue(runtimeSlot, out GCObject item) || item == null)
            {
                equipment.slotRarity.Remove(savedSlot);
                equipment.slotLevel.Remove(savedSlot);
                equipment.slotItemState.Remove(savedSlot);
                return "";
            }
            equipment.slotRarity[savedSlot] = item.GetEffectiveRarity();
            equipment.slotLevel[savedSlot] = item.StoredLevel;
            equipment.slotItemState[savedSlot] = SavedItemRuntimeState.Capture(item);
            return item.GCClass ?? "";
        }

        private bool CaptureInventoryAndEquipment(RRConnection conn, SavedCharacter savedChar, out string failure)
        {
            failure = "";
            string connId = conn.ConnId.ToString();
            if (!_inventorySlotCounters.ContainsKey(connId))
            {
                failure = $"inventory-runtime-not-initialized:{connId}";
                return false;
            }
            savedChar.inventory ??= new List<SavedInventoryItem>();
            savedChar.inventory.Clear();
            byte[] saveContainers = { 0x0B, 0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13 };
            foreach (byte containerId in saveContainers)
            {
                string dictKey = InvKey(connId, containerId);
                if (!_playerInventoryItems.TryGetValue(dictKey, out var inventoryItems))
                    continue;
                if (!_playerInventoryOrder.TryGetValue(dictKey, out List<uint> inventoryOrder))
                {
                    failure = $"missing-inventory-order:{dictKey}";
                    return false;
                }
                int savedItemOrder = 0;
                foreach (uint inventoryIndex in inventoryOrder)
                {
                    if (!inventoryItems.TryGetValue(inventoryIndex, out var inventoryEntryValue))
                    {
                        failure = $"stale-inventory-order:{dictKey}:{inventoryIndex}";
                        return false;
                    }
                    GCObject item = inventoryEntryValue.Item1;
                    int count = GetStackCount(connId, inventoryIndex, containerId);
                    if (item == null || string.IsNullOrWhiteSpace(item.GCClass) || count <= 0 || count > byte.MaxValue)
                    {
                        failure = $"invalid-inventory-item:{dictKey}:{inventoryIndex}";
                        return false;
                    }
                    uint buyPrice = DungeonRunners.Gameplay.MerchantRuntime.GetBuyPrice(connId, item.GCClass);
                    int rarity = item.GetEffectiveRarity();
                    int storedLevel = item.StoredLevel;
                    var dimensions = DungeonRunners.Gameplay.MerchantRuntime.GetItemDimensions(item.GCClass);
                    int slotX = inventoryEntryValue.Item2;
                    int slotY = inventoryEntryValue.Item3;
                    if (!IsPersistedInventoryContainer(containerId)
                        || buyPrice > int.MaxValue
                        || rarity < -1 || rarity > 5
                        || (storedLevel != -1 && (storedLevel < 1 || storedLevel > 120))
                        || dimensions.width <= 0 || dimensions.height <= 0
                        || slotX < 0 || slotX + dimensions.width > 10
                        || slotY < 0 || slotY + dimensions.height > ContainerHeight(containerId))
                    {
                        failure = $"invalid-inventory-wire-state:{dictKey}:{inventoryIndex}";
                        return false;
                    }
                    savedChar.inventory.Add(new SavedInventoryItem
                    {
                        gcClass = item.GCClass,
                        x = inventoryEntryValue.Item2,
                        y = inventoryEntryValue.Item3,
                        count = count,
                        buyPrice = buyPrice,
                        rarity = rarity,
                        storedLevel = storedLevel,
                        containerId = containerId,
                        itemOrder = savedItemOrder++,
                        itemState = SavedItemRuntimeState.Capture(item)
                    });
                }
            }

            PlayerState playerState = GetPlayerState(connId);
            if (playerState?.ActiveItem != null)
            {
                GCObject activeItem = playerState.ActiveItem;
                var dimensions = DungeonRunners.Gameplay.MerchantRuntime.GetItemDimensions(activeItem.GCClass);
                if (dimensions.width <= 0 || dimensions.height <= 0)
                {
                    failure = $"missing-cursor-item-dimensions:{activeItem.GCClass}";
                    return false;
                }
                byte activeSlotX = 0;
                byte activeSlotY = 0;
                bool foundActive = false;
                for (byte rowY = 0; rowY < 8 && !foundActive; rowY++)
                {
                    for (byte columnX = 0; columnX < 10 && !foundActive; columnX++)
                    {
                        if (IsInventorySlotOccupied(connId, columnX, rowY, dimensions.width, dimensions.height))
                            continue;
                        activeSlotX = columnX;
                        activeSlotY = rowY;
                        foundActive = true;
                    }
                }
                if (!foundActive)
                {
                    failure = $"cursor-item-has-no-persistable-slot:{activeItem.GCClass}";
                    return false;
                }
                int activeCount = Math.Max(1, GetStackCount(connId, 0xFFFFFFFF));
                uint activeBuyPrice = DungeonRunners.Gameplay.MerchantRuntime.GetBuyPrice(connId, activeItem.GCClass);
                int activeRarity = activeItem.GetEffectiveRarity();
                int activeStoredLevel = activeItem.StoredLevel;
                if (activeCount > byte.MaxValue || activeBuyPrice > int.MaxValue
                    || activeRarity < -1 || activeRarity > 5
                    || (activeStoredLevel != -1 && (activeStoredLevel < 1 || activeStoredLevel > 120))
                    || activeSlotX + dimensions.width > 10 || activeSlotY + dimensions.height > 8)
                {
                    failure = $"invalid-cursor-item-wire-state:{activeItem.GCClass}";
                    return false;
                }
                savedChar.inventory.Add(new SavedInventoryItem
                {
                    gcClass = activeItem.GCClass,
                    x = activeSlotX,
                    y = activeSlotY,
                    count = activeCount,
                    buyPrice = activeBuyPrice,
                    rarity = activeRarity,
                    storedLevel = activeStoredLevel,
                    containerId = 0x0B,
                    itemOrder = savedChar.inventory.Where(saved => saved.containerId == 0x0B).Select(saved => saved.itemOrder).DefaultIfEmpty(-1).Max() + 1,
                    itemState = SavedItemRuntimeState.Capture(activeItem)
                });
            }

            _playerEquippedItems.TryGetValue(connId, out var equippedItems);
            savedChar.equipment ??= new StartingEquipment();
            savedChar.equipment.weapon = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 10, "weapon");
            savedChar.equipment.armor = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 6, "armor");
            savedChar.equipment.helmet = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 5, "helmet");
            savedChar.equipment.gloves = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 2, "gloves");
            savedChar.equipment.boots = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 7, "boots");
            savedChar.equipment.shoulders = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 8, "shoulders");
            savedChar.equipment.shield = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 11, "shield");
            savedChar.equipment.ring1 = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 3, "ring1");
            savedChar.equipment.ring2 = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 4, "ring2");
            savedChar.equipment.amulet = CaptureSavedEquipmentSlot(savedChar.equipment, equippedItems, 1, "amulet");
            return true;
        }

        private void CaptureQuestState(RRConnection conn, SavedCharacter savedChar)
        {
            var questState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
            if (questState == null)
                return;
            savedChar.activeQuests = questState.ActiveQuests.Where(quest => QuestManager.FindDefinition(quest.QuestId)?.temporary != true).Select(quest => new SavedQuest
            {
                questId = quest.QuestId,
                questGiverId = quest.QuestGiverId,
                acceptedAt = quest.AcceptedAt.ToString("o"),
                objectives = quest.Objectives.Select(objective => new SavedQuestObjective
                {
                    objectiveName = objective.ObjectiveName,
                    type = objective.Type,
                    target = objective.Target,
                    label = objective.Label,
                    required = objective.Required,
                    current = objective.Current
                }).ToList()
            }).ToList();
            savedChar.completedQuests = new List<string>(questState.CompletedQuests);
            savedChar.questCompletionTimes = new Dictionary<string, long>(questState.CompletedAtUnixSeconds, StringComparer.OrdinalIgnoreCase);
            savedChar.unlockedCheckpoints = new List<string>(questState.UnlockedCheckpoints);
        }

        private bool TryCaptureFullCharacterSnapshot(
            RRConnection conn,
            string reason,
            out SavedCharacter savedChar,
            int? positionFixedX = null,
            int? positionFixedY = null,
            int? positionFixedZ = null,
            bool persistConnectionRuntime = true)
        {
            savedChar = null;
            if (conn == null || string.IsNullOrEmpty(conn.LoginName) || !_selectedCharacter.ContainsKey(conn.LoginName))
                return false;
            if (persistConnectionRuntime && _unitContainer != null && !_unitContainer.TryPersistConnectionRuntime(conn, reason))
                return false;
            SavedCharacter activeCharacter = GetActiveCharacter(conn);
            if (activeCharacter == null)
                return false;
            return TryCaptureCharacterSnapshot(conn, reason, activeCharacter, activeCharacter, out savedChar, positionFixedX, positionFixedY, positionFixedZ);
        }

        private bool TryCaptureCharacterSnapshot(RRConnection conn, string reason, SavedCharacter sourceCharacter, SavedCharacter baselineCharacter, out SavedCharacter savedChar, int? positionFixedX = null, int? positionFixedY = null, int? positionFixedZ = null)
        {
            savedChar = null;
            if (conn == null || _persistenceSaveSuppressedConnections.Contains(conn.ConnId)
                || sourceCharacter == null || baselineCharacter == null || sourceCharacter.id != baselineCharacter.id)
                return false;
            if (HasPendingDisconnectedCharacterSaveForCharacter(sourceCharacter.id))
                return false;
            bool preserveLevel = sourceCharacter.level != baselineCharacter.level;
            bool preserveExperience = sourceCharacter.experience != baselineCharacter.experience;
            bool preserveGold = sourceCharacter.gold != baselineCharacter.gold;
            bool preserveCurrentHP = sourceCharacter.currentHP != baselineCharacter.currentHP;
            bool preserveCurrentMana = sourceCharacter.currentMana != baselineCharacter.currentMana;
            bool preserveMaxHP = sourceCharacter.maxHP != baselineCharacter.maxHP;
            bool preserveMaxMana = sourceCharacter.maxMana != baselineCharacter.maxMana;
            bool preserveZone = sourceCharacter.zoneId != baselineCharacter.zoneId
                || !string.Equals(sourceCharacter.currentZoneName, baselineCharacter.currentZoneName, StringComparison.OrdinalIgnoreCase);
            bool preservePositionX = sourceCharacter.positionFixedX != baselineCharacter.positionFixedX;
            bool preservePositionY = sourceCharacter.positionFixedY != baselineCharacter.positionFixedY;
            bool preservePositionZ = sourceCharacter.positionFixedZ != baselineCharacter.positionFixedZ;
            savedChar = sourceCharacter.DeepClone();
            if (!CaptureInventoryAndEquipment(conn, savedChar, out string captureFailure))
            {
                Debug.LogError($"[SAVE] state=failed phase=capture characterId={savedChar.id} reason={reason ?? "unknown"} detail={captureFailure}");
                return false;
            }
            CaptureQuestState(conn, savedChar);
            string connId = conn.ConnId.ToString();
            PlayerState playerState = GetPlayerState(connId);
            bool playerStateLooksUninitialized = playerState != null
                && playerState.Level <= 1
                && playerState.Experience == 0
                && (savedChar.level > 0 || savedChar.experience > 0);
            if (playerState != null && !playerStateLooksUninitialized && playerState.MaxHPWire != 0 && playerState.MaxManaWire != 0)
            {
                if (!preserveLevel) savedChar.level = SavedCharacterLevel.ResolvePersistedLevel(playerState.Level);
                if (!preserveExperience) savedChar.experience = playerState.Experience;
                if (!preserveGold) savedChar.gold = playerState.Gold;
                if (!preserveCurrentHP) savedChar.currentHP = playerState.CurrentHPWire;
                if (!preserveCurrentMana) savedChar.currentMana = playerState.CurrentManaWire;
                if (!preserveMaxHP) savedChar.maxHP = checked((int)(playerState.MaxHPWire / 256));
                if (!preserveMaxMana) savedChar.maxMana = checked((int)(playerState.MaxManaWire / 256));
            }
            if (_playerSkillLevels.TryGetValue(connId, out var skillLevels) && savedChar.skills != null)
            {
                foreach (string skillGcClass in savedChar.skills)
                {
                    bool preserveSkillLevel = sourceCharacter.GetSkillLevel(skillGcClass) != baselineCharacter.GetSkillLevel(skillGcClass)
                        || baselineCharacter.skills == null
                        || !baselineCharacter.skills.Any(skill => string.Equals(skill, skillGcClass, StringComparison.OrdinalIgnoreCase));
                    if (!preserveSkillLevel && skillLevels.TryGetValue(skillGcClass, out int skillLevel))
                        savedChar.SetSkillLevel(skillGcClass, skillLevel);
                }
            }
            if (!preserveZone)
            {
                savedChar.currentZoneName = string.IsNullOrWhiteSpace(conn.CurrentZoneName) ? "tutorial" : conn.CurrentZoneName;
                savedChar.zoneId = unchecked((int)conn.CurrentZoneId);
            }
            savedChar.positionFixedX = positionFixedX ?? (preservePositionX ? sourceCharacter.positionFixedX : conn.PlayerPosFixedX);
            savedChar.positionFixedY = positionFixedY ?? (preservePositionY ? sourceCharacter.positionFixedY : conn.PlayerPosFixedY);
            savedChar.positionFixedZ = positionFixedZ ?? (preservePositionZ ? sourceCharacter.positionFixedZ : conn.PlayerPosFixedZ);
            return true;
        }

        private bool TrySaveFullCharacterSnapshot(RRConnection conn, string reason, int? positionFixedX = null, int? positionFixedY = null, int? positionFixedZ = null)
        {
            if (!TryCaptureFullCharacterSnapshot(conn, reason, out SavedCharacter savedChar, positionFixedX, positionFixedY, positionFixedZ))
                return false;
            if (!TryCommitCharacterSnapshotForConn(conn, savedChar, reason))
                return false;
            Debug.LogError($"[SAVE] state=committed characterId={savedChar.id} reason={reason ?? "unknown"} inventory={savedChar.inventory?.Count ?? 0} activeQuests={savedChar.activeQuests?.Count ?? 0} completedQuests={savedChar.completedQuests?.Count ?? 0}");
            return true;
        }

        private bool TrySaveZoneTransitionSnapshot(RRConnection conn, Zone zone, int positionFixedX, int positionFixedY, int positionFixedZ, bool restoreFullHealth, string reason)
        {
            if (zone == null || !TryCaptureFullCharacterSnapshot(conn, reason, out SavedCharacter savedChar, positionFixedX, positionFixedY, positionFixedZ))
                return false;
            savedChar.currentZoneName = zone.name;
            savedChar.zoneId = unchecked((int)zone.id);
            savedChar.positionFixedX = positionFixedX;
            savedChar.positionFixedY = positionFixedY;
            savedChar.positionFixedZ = positionFixedZ;
            if (restoreFullHealth)
            {
                PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
                if (playerState == null || playerState.MaxHPWire == 0)
                    return false;
                savedChar.currentHP = playerState.MaxHPWire;
                savedChar.currentMana = playerState.MaxManaWire;
            }
            if (!TryCommitCharacterSnapshotForConn(conn, savedChar, reason))
                return false;
            Debug.LogError($"[SAVE] state=committed characterId={savedChar.id} reason={reason ?? "zone-transition"} zone={savedChar.currentZoneName} positionFixed=({savedChar.positionFixedX},{savedChar.positionFixedY},{savedChar.positionFixedZ}) hp={savedChar.currentHP} mana={savedChar.currentMana}");
            return true;
        }

        private bool TrySaveFullCharacterSnapshotConsumingDrop(RRConnection conn, long droppedItemId, string reason)
        {
            if (droppedItemId < 0 || !TryCaptureFullCharacterSnapshot(conn, reason, out SavedCharacter savedChar))
                return false;
            if (conn == null || string.IsNullOrEmpty(conn.LoginName))
                return false;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selected) || selected == null || selected.Id != savedChar.id)
                return false;
            if (!CharacterRepository.TrySaveCharacterConsumingDrop(savedChar, droppedItemId, reason))
                return false;
            _activeCharacter[conn.LoginName] = savedChar;
            Debug.LogError($"[SAVE] state=committed characterId={savedChar.id} reason={reason ?? "unknown"} consumedDropId={droppedItemId} inventory={savedChar.inventory?.Count ?? 0} activeQuests={savedChar.activeQuests?.Count ?? 0} completedQuests={savedChar.completedQuests?.Count ?? 0}");
            return true;
        }

        private bool TrySaveFullCharacterSnapshotConsumingGrant(RRConnection conn, long pendingGrantId, string reason)
        {
            if (pendingGrantId <= 0 || !TryCaptureFullCharacterSnapshot(conn, reason, out SavedCharacter savedChar))
                return false;
            if (conn == null || string.IsNullOrEmpty(conn.LoginName))
                return false;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selected) || selected == null || selected.Id != savedChar.id)
                return false;
            if (!CharacterRepository.TrySaveCharacterConsumingGrant(savedChar, pendingGrantId, reason))
                return false;
            _activeCharacter[conn.LoginName] = savedChar;
            Debug.LogError($"[SAVE] state=committed characterId={savedChar.id} reason={reason ?? "unknown"} consumedGrantId={pendingGrantId} inventory={savedChar.inventory?.Count ?? 0}");
            return true;
        }

        private bool TrySaveFullCharacterSnapshotCreatingDrop(RRConnection conn, DroppedItemInfo drop, string reason, out long droppedItemId)
        {
            droppedItemId = 0;
            if (drop == null || !TryCaptureFullCharacterSnapshot(conn, reason, out SavedCharacter savedChar))
                return false;
            if (conn == null || string.IsNullOrEmpty(conn.LoginName))
                return false;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selected) || selected == null || selected.Id != savedChar.id)
                return false;
            if (!CharacterRepository.TrySaveCharacterCreatingDrop(
                    savedChar,
                    drop.Zone,
                    drop.ZoneId,
                    drop.InstanceId,
                    drop.Item,
                    drop.PosFixedX,
                    drop.PosFixedY,
                    drop.PosFixedZ,
                    drop.PlayerLevel,
                    drop.Quantity,
                    drop.DroppedBy,
                    drop.OwnerCharacterId,
                    drop.OwnerGroupId,
                    drop.OwnerName,
                    out droppedItemId,
                    reason))
                return false;
            _activeCharacter[conn.LoginName] = savedChar;
            Debug.LogError($"[SAVE] state=committed characterId={savedChar.id} reason={reason ?? "unknown"} createdDropId={droppedItemId} inventory={savedChar.inventory?.Count ?? 0} activeQuests={savedChar.activeQuests?.Count ?? 0} completedQuests={savedChar.completedQuests?.Count ?? 0}");
            return true;
        }

        public bool TrySaveCharacterForConn(RRConnection conn, SavedCharacter savedChar, string reason)
        {
            if (conn == null || savedChar == null || string.IsNullOrEmpty(conn.LoginName))
                return false;
            SavedCharacter activeCharacter = GetActiveCharacter(conn);
            if (activeCharacter == null || ReferenceEquals(savedChar, activeCharacter))
            {
                Debug.LogError($"[SAVE] state=failed phase=working-copy reason={reason ?? "unknown"}");
                return false;
            }
            if (!TryCaptureCharacterSnapshot(conn, reason, savedChar, activeCharacter, out SavedCharacter mergedCharacter))
                return false;
            return TryCommitCharacterSnapshotForConn(conn, mergedCharacter, reason);
        }

        private bool TrySaveCharacterForConnCreatingGrant(RRConnection conn, SavedCharacter savedChar, string gcClass, int count, int width, int height, int rarity, string reason, out long pendingGrantId)
        {
            pendingGrantId = 0;
            if (conn == null || savedChar == null || string.IsNullOrEmpty(conn.LoginName))
                return false;
            SavedCharacter activeCharacter = GetActiveCharacter(conn);
            if (activeCharacter == null || ReferenceEquals(savedChar, activeCharacter))
                return false;
            if (!TryCaptureCharacterSnapshot(conn, reason, savedChar, activeCharacter, out SavedCharacter mergedCharacter))
                return false;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selected) || selected == null || selected.Id != mergedCharacter.id)
                return false;
            if (!CharacterRepository.TrySaveCharacterCreatingGrant(mergedCharacter, gcClass, count, width, height, rarity, out pendingGrantId, reason))
                return false;
            _activeCharacter[conn.LoginName] = mergedCharacter;
            return true;
        }

        private bool TryCommitCharacterSnapshotForConn(RRConnection conn, SavedCharacter savedChar, string reason)
        {
            if (conn == null || savedChar == null || string.IsNullOrEmpty(conn.LoginName))
                return false;
            if (HasPendingDisconnectedCharacterSaveForCharacter(savedChar.id))
                return false;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selected) || selected == null || selected.Id != savedChar.id)
                return false;
            if (!CharacterRepository.TrySaveCharacter(savedChar, reason))
                return false;
            _activeCharacter[conn.LoginName] = savedChar;
            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            try
            {
                if (playerState != null)
                {
                    int committedRuntimeLevel = SavedCharacterLevel.ResolveRuntimeLevel(savedChar);
                    if (playerState.Level != committedRuntimeLevel)
                    {
                        playerState.InitializeStats(savedChar.className, committedRuntimeLevel, preserveAttributeModifiers: true);
                        playerState.ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
                    }
                    playerState.Experience = savedChar.experience;
                    playerState.Gold = savedChar.gold;
                    if (conn.Avatar != null)
                        ApplyAvatarRuntimeProperties(conn.Avatar, savedChar, playerState);
                }
            }
            catch (Exception ex) { QuarantineCommittedPersistenceSyncFailure(conn, reason, ex); }
            return true;
        }

        private void QuarantineCommittedPersistenceSyncFailure(RRConnection conn, string reason, Exception exception)
        {
            if (conn == null)
                return;
            _persistenceSaveSuppressedConnections.Add(conn.ConnId);
            Debug.LogError($"[SAVE] state=committed phase=runtime-sync-failed conn={conn.ConnId} reason={reason ?? "unknown"} errorType={exception?.GetType().Name ?? "unknown"} message='{exception?.Message ?? "unknown"}'");
            conn.Disconnect();
        }

        public void QuarantineCommittedPersistenceSyncFailurePublic(RRConnection conn, string reason, Exception exception)
        {
            QuarantineCommittedPersistenceSyncFailure(conn, reason, exception);
        }

        public bool TryAddGoldForConn(RRConnection conn, uint amount, string reason, out uint balance)
        {
            balance = 0;
            SavedCharacter savedChar = GetActiveCharacter(conn)?.DeepClone();
            if (savedChar == null || amount > int.MaxValue || savedChar.gold > (uint)int.MaxValue - amount)
                return false;
            uint previousGold = savedChar.gold;
            savedChar.gold += amount;
            if (!TrySaveCharacterForConn(conn, savedChar, reason))
            {
                savedChar.gold = previousGold;
                return false;
            }
            balance = savedChar.gold;
            return true;
        }

        private bool SavePlayerInventory(RRConnection conn)
        {
            QuestProgressMutation mutation = QuestManager.Instance.StageItemObjectives(conn);
            if (!TrySaveFullCharacterSnapshot(conn, "inventory"))
            {
                QuestManager.Instance.RollbackProgress(mutation);
                return false;
            }
            QuestManager.Instance.CommitProgress(conn, mutation);
            return true;
        }

        public bool SavePlayerInventoryPublic(RRConnection conn)
        {
            return SavePlayerInventory(conn);
        }

        public bool SavePlayerInventoryWithModifierPublic(
            RRConnection conn,
            string gcType,
            uint modifierId,
            byte level,
            uint powerLevel,
            uint durationRemaining,
            byte sourceIsSelf,
            bool replaceSameType,
            IReadOnlyList<string> replaceGcTypes)
        {
            if (!TryCaptureFullCharacterSnapshot(conn, "inventory-consumable-modifier", out SavedCharacter savedChar)
                || !_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selected)
                || selected == null || selected.Id != savedChar.id
                || !CharacterRepository.TrySaveCharacterWithModifier(
                    savedChar,
                    gcType,
                    modifierId,
                    level,
                    powerLevel,
                    durationRemaining,
                    sourceIsSelf,
                    "inventory-consumable-modifier",
                    replaceSameType,
                    replaceGcTypes))
                return false;
            _activeCharacter[conn.LoginName] = savedChar;
            return true;
        }

        public bool SavePlayerLevelPublic(RRConnection conn)
        {
            return SavePlayerLevel(conn);
        }

        private sealed class QuestItemRemovalChange
        {
            public uint Slot;
            public GCObject Item;
            public byte X;
            public byte Y;
            public int Width;
            public int Height;
            public int PreviousCount;
            public int OrderIndex;
            public bool Removed;
        }

        private sealed class QuestItemRemovalMutation
        {
            public readonly List<QuestItemRemovalChange> Changes = new List<QuestItemRemovalChange>();
            public readonly List<byte[]> Packets = new List<byte[]>();
        }

        private void RollbackQuestItemRemoval(string connId, QuestItemRemovalMutation mutation)
        {
            if (mutation == null)
                return;
            for (int index = mutation.Changes.Count - 1; index >= 0; index--)
            {
                QuestItemRemovalChange change = mutation.Changes[index];
                if (!change.Removed)
                {
                    SetStackCount(connId, change.Slot, change.PreviousCount);
                    continue;
                }
                TrackInventoryItem(connId, change.Slot, change.Item, change.X, change.Y);
                OccupyInventorySlots(connId, change.X, change.Y, change.Width, change.Height);
                SetStackCount(connId, change.Slot, change.PreviousCount);
                if (_playerInventoryOrder.TryGetValue(connId, out List<uint> order))
                {
                    order.Remove(change.Slot);
                    order.Insert(Math.Clamp(change.OrderIndex, 0, order.Count), change.Slot);
                }
            }
            mutation.Changes.Clear();
            mutation.Packets.Clear();
        }

        private bool TryStageQuestItemRemoval(RRConnection conn, ActiveQuest completingQuest, out QuestItemRemovalMutation mutation)
        {
            mutation = new QuestItemRemovalMutation();
            if (conn == null || completingQuest == null)
                return false;
            string connId = conn.ConnId.ToString();
            QuestData definition = QuestManager.FindDefinition(completingQuest.QuestId);
            if (definition == null || definition.objectives.Count != completingQuest.Objectives.Count)
                return false;
            for (int objectiveIndex = 0; objectiveIndex < definition.objectives.Count; objectiveIndex++)
            {
                QuestObjective authored = definition.objectives[objectiveIndex];
                QuestProgress objective = completingQuest.Objectives[objectiveIndex];
                if (authored.type != "item" || !authored.removeOnFinalize)
                    continue;
                if (!_playerInventoryItems.TryGetValue(connId, out Dictionary<uint, (GCObject item, byte x, byte y)> inventory)
                    || !_playerInventoryOrder.TryGetValue(connId, out List<uint> inventoryOrder))
                {
                    RollbackQuestItemRemoval(connId, mutation);
                    return false;
                }
                int required = GetQuestItemRequiredQuantity(connId, authored);
                int available = 0;
                var matchingSlots = new List<uint>();
                foreach (uint slot in inventoryOrder)
                {
                    if (!inventory.TryGetValue(slot, out var entry))
                        throw new InvalidOperationException($"stale-inventory-order:{connId}:{slot}");
                    if (!QuestItemMatches(entry.item, authored))
                        continue;
                    matchingSlots.Add(slot);
                    available = checked(available + Math.Max(1, GetStackCount(connId, slot)));
                }
                if (available < required)
                {
                    RollbackQuestItemRemoval(connId, mutation);
                    Debug.LogError($"[QUEST-ITEM-REMOVE] state=blocked reason=underflow item={objective.Target} required={required} available={available}");
                    return false;
                }
                int remaining = required;
                foreach (uint slot in matchingSlots)
                {
                    if (remaining <= 0)
                        break;
                    var entry = inventory[slot];
                    int stack = Math.Max(1, GetStackCount(connId, slot));
                    ItemData itemData = AuthoredGameplayCatalog.FindItem(entry.item.GCClass);
                    int width = itemData?.inventoryWidth ?? 1;
                    int height = itemData?.inventoryHeight ?? 1;
                    int orderIndex = inventoryOrder.IndexOf(slot);
                    bool removeStack = stack <= remaining;
                    mutation.Changes.Add(new QuestItemRemovalChange
                    {
                        Slot = slot,
                        Item = entry.item,
                        X = entry.x,
                        Y = entry.y,
                        Width = width,
                        Height = height,
                        PreviousCount = stack,
                        OrderIndex = orderIndex,
                        Removed = removeStack
                    });
                    if (removeStack)
                    {
                        remaining -= stack;
                        if (GetAndRemoveInventoryItem(connId, slot) == null)
                        {
                            RollbackQuestItemRemoval(connId, mutation);
                            return false;
                        }
                        if (_inventoryStackCounts.TryGetValue(connId, out Dictionary<uint, int> stackCounts))
                            stackCounts.Remove(slot);
                        if (conn.UnitContainerId != 0)
                        {
                            var writer = new LEWriter();
                            writer.WriteByte(0x07);
                            writer.WriteByte(0x35);
                            writer.WriteUInt16(conn.UnitContainerId);
                            writer.WriteByte(0x1F);
                            writer.WriteUInt32(slot);
                            if (!TryWriteEntitySynchForComponent(conn, writer, conn.UnitContainerId, 0x1F, EntitySynchInfoContext.PlayerActionResponse, "QUEST-ITEM-REMOVE"))
                            {
                                RollbackQuestItemRemoval(connId, mutation);
                                return false;
                            }
                            writer.WriteByte(0x06);
                            mutation.Packets.Add(writer.ToArray());
                        }
                    }
                    else
                    {
                        int newCount = stack - remaining;
                        remaining = 0;
                        SetStackCount(connId, slot, newCount);
                        if (conn.UnitContainerId != 0)
                        {
                            var writer = new LEWriter();
                            writer.WriteByte(0x07);
                            writer.WriteByte(0x35);
                            writer.WriteUInt16(conn.UnitContainerId);
                            writer.WriteByte(0x22);
                            writer.WriteUInt32(slot);
                            writer.WriteByte(checked((byte)newCount));
                            WritePlayerEntitySynch(conn, writer);
                            writer.WriteByte(0x06);
                            mutation.Packets.Add(writer.ToArray());
                        }
                    }
                }
            }
            return true;
        }

        private bool TryCommitQuestTurnIn(RRConnection conn, uint instanceId, bool recordCompletion, QuestData questData, int rewardChoiceIndex, out ActiveQuest completingQuest, ZoneNPC turnInNpc = null)
        {
            completingQuest = null;
            PlayerQuestState questState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
            if (questState == null)
                return false;
            int activeIndex = questState.ActiveQuests.FindIndex(quest => quest.InstanceId == instanceId);
            if (activeIndex < 0)
                return false;
            ActiveQuest active = questState.ActiveQuests[activeIndex];
            if (questData == null || !string.Equals(questData.id, active.QuestId, StringComparison.OrdinalIgnoreCase)
                || !QuestManager.Instance.CanQueryComplete(conn.ConnId.ToString(), active))
                return false;
            var previousCompletions = new List<string>(questState.CompletedQuests);
            var previousTimestamps = new Dictionary<string, long>(questState.CompletedAtUnixSeconds, StringComparer.OrdinalIgnoreCase);
            QuestItemRemovalMutation itemMutation = null;
            QuestProgressMutation itemProgress = null;
            QuestRewardMutation rewardMutation = null;
            bool durableCommitted = false;
            using (RandomStreams.GlobalStaticTransaction rngTransaction = RandomStreams.BeginGlobalStaticTransaction("quest-turn-in"))
            {
                try
                {
                    if (!TryStageQuestRewards(conn, active, questData, rewardChoiceIndex, out rewardMutation))
                        return false;
                    if (!TryStageQuestItemRemoval(conn, active, out itemMutation))
                        return false;
                    questState.ActiveQuests.RemoveAt(activeIndex);
                    if (recordCompletion)
                    {
                        questState.CompletedQuests.Add(active.QuestId);
                        questState.CompletedAtUnixSeconds[active.QuestId] = new DateTimeOffset(active.AcceptedAt).ToUnixTimeSeconds();
                    }
                    else
                    {
                        questState.CompletedQuests.RemoveAll(id => string.Equals(id, active.QuestId, StringComparison.OrdinalIgnoreCase));
                        questState.CompletedAtUnixSeconds.Remove(active.QuestId);
                    }
                    itemProgress = QuestManager.Instance.StageItemObjectives(conn);
                    QuestRewardSnapshotCommitResult rewardCommit = TryCommitQuestRewardSnapshot(conn, rewardMutation);
                    if (rewardCommit == QuestRewardSnapshotCommitResult.Failed)
                        return false;
                    if (rewardCommit == QuestRewardSnapshotCommitResult.AlreadyCommitted)
                    {
                        QuarantineCommittedPersistenceSyncFailure(conn, "quest-reward-idempotency", new InvalidOperationException("quest reward activation was already committed"));
                        return false;
                    }
                    durableCommitted = true;
                    rngTransaction.Commit();
                    CommitQuestRewards(conn, rewardMutation);
                    QuestManager.Instance.SendFinalizePacket(conn, instanceId);
                    ClearPendingQuest(conn);
                    QuestData followup = QuestManager.FindDefinition(questData.followupQuest);
                    if (turnInNpc != null && followup != null
                        && followup.offeringNpcs.Contains(turnInNpc.GCClass, StringComparer.OrdinalIgnoreCase))
                        QueryQuestFromGiver(conn, followup.hash, turnInNpc);
                    foreach (byte[] packet in itemMutation.Packets)
                        SendCompressedA(conn, 0x01, 0x0F, packet);
                    QuestManager.Instance.CommitProgress(conn, itemProgress);
                    QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);
                    completingQuest = active;
                    return true;
                }
                catch (Exception ex)
                {
                    if (durableCommitted)
                        QuarantineCommittedPersistenceSyncFailure(conn, "quest-reward-materialization", ex);
                    else
                        Debug.LogError($"[QUEST-REWARDS] state=blocked phase=turn-in errorType={ex.GetType().Name}");
                    return false;
                }
                finally
                {
                    if (!durableCommitted)
                    {
                        QuestManager.Instance.RollbackProgress(itemProgress);
                        RollbackQuestItemRemoval(conn.ConnId.ToString(), itemMutation);
                        RollbackQuestRewards(conn, rewardMutation);
                        questState.CompletedQuests = previousCompletions;
                        questState.CompletedAtUnixSeconds = previousTimestamps;
                        if (!questState.ActiveQuests.Contains(active))
                            questState.ActiveQuests.Insert(activeIndex, active);
                    }
                }
            }
        }

        private string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    string ipStr = ip.ToString();
                    if (ipStr.StartsWith("10."))
                    {
                        return ipStr;
                    }
                }
            }
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }

        private bool StopServer()
        {
            List<RRConnection> shutdownConnections = GetConnectionInsertionOrderSnapshot();
            DrainPendingDisconnectedCharacterSaves();
            foreach (RRConnection conn in shutdownConnections)
            {
                if (conn == null || !conn.LoginAdmitted)
                    continue;
                bool hasSelectedCharacter = conn.CharSqlId != 0
                    || (!string.IsNullOrWhiteSpace(conn.LoginName) && _activeCharacter.ContainsKey(conn.LoginName));
                if (!hasSelectedCharacter || HasPendingDisconnectedCharacterSaveForLogin(conn.LoginName))
                    continue;
                if (!TryCaptureDisconnectCharacterSnapshot(conn, out PendingDisconnectedCharacterSave pendingSave)
                    || !EnqueuePendingDisconnectedCharacterSave(pendingSave))
                    MarkDisconnectCaptureFailure(conn);
            }
            for (int retry = 0; retry < 3
                && (_pendingDisconnectedCharacterSaves.Count > 0 || HasUndurableDisconnectCaptureFailure()); retry++)
            {
                TryPersistPendingDisconnectedCharacterSpool();
                DrainPendingDisconnectedCharacterSaves();
            }
            bool pendingRecoveryDurable = _pendingDisconnectedCharacterSaves.All(IsPendingDisconnectedCharacterSaveDurable)
                && !HasUndurableDisconnectCaptureFailure();
            bool saved = _pendingDisconnectedCharacterSaves.Count == 0 && _failedDisconnectCaptureFences.Count == 0;
            bool selectionsCleared = saved;
            _isRunning = false;
            _listener?.Stop();
            foreach (RRConnection conn in shutdownConnections)
                conn.Disconnect();
            _connections.Clear();
            if (!saved || !selectionsCleared)
            {
                DungeonRunners.Runtime.EngineRuntime.SetExitCode(Math.Max(1, DungeonRunners.Runtime.EngineRuntime.ExitCode));
                Debug.LogError($"[SHUTDOWN] state=failed characterSave={saved} selectionClear={selectionsCleared} pendingRecoveryDurable={pendingRecoveryDurable}");
                return false;
            }
            Debug.Log("Game Server stopped");
            return true;
        }

        private static void ClearDisconnectTransientCharacterState(SavedCharacter character)
        {
            if (character == null)
                return;
            character.tpZone = "";
            character.tpZoneId = 0;
            character.tpTargetZone = "";
            character.tpPosFixedX = 0;
            character.tpPosFixedY = 0;
            character.tpPosFixedZ = 0;
        }

        private IEnumerator PollPendingItemGrants()
        {
            yield return ServerRuntime.DelaySeconds(5);
            while (_isRunning)
            {
                try { DrainPendingDisconnectedCharacterSaves(); }
                catch (Exception ex) { Debug.LogError($"[SAVE] state=failed phase=disconnect-retry-pump message='{ex.Message}'"); }
                try { ProcessPendingGrants(); }
                catch (Exception ex) { Debug.LogError($"[GRANTS] state=failed message='{ex.Message}'"); }
                try { ProcessPendingAdminActions(); }
                catch (Exception ex) { Debug.LogError($"[ADMIN-ACT] state=failed message='{ex.Message}'"); }
                yield return ServerRuntime.DelaySeconds(5);
            }
        }

        private void ProcessPendingAdminActions()
        {
            using (var connection = GameDatabase.GetConnection())
            {
                object tbl = GameDatabase.ExecuteScalar(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='pending_admin_actions'");
                if (tbl == null) return;

                var actions = new List<(int id, int charId, string actionType, int value)>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, character_id, action_type, value FROM pending_admin_actions ORDER BY id";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            actions.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3)));
                }
                if (actions.Count == 0) return;

                foreach (var action in actions)
                {
                    try
                    {
                        if (HasPendingDisconnectedCharacterSaveForCharacter(checked((uint)action.charId)))
                        {
                            Debug.LogError($"[ADMIN-ACT] state=pending action={action.id} characterId={action.charId} reason=disconnect-writer-fence");
                            continue;
                        }
                        if (action.actionType != "gold" && action.actionType != "level" && action.actionType != "xp")
                        {
                            Debug.LogError($"[ADMIN-ACT] state=pending action={action.id} reason=unknown-type type='{action.actionType}'");
                            continue;
                        }
                        if (action.actionType == "xp" && action.value < 0)
                        {
                            Debug.LogError($"[ADMIN-ACT] state=pending action={action.id} reason=negative-xp value={action.value}");
                            continue;
                        }
                        if (action.actionType == "level" && (action.value < 1 || action.value > 100))
                        {
                            Debug.LogError($"[ADMIN-ACT] state=pending action={action.id} reason=level-out-of-range value={action.value}");
                            continue;
                        }

                        uint committedGold = 0;
                        byte committedLevel = 0;
                        uint committedExperience = 0;
                        using (var transaction = connection.BeginTransaction())
                        {
                            if (action.actionType == "gold")
                            {
                                GameDatabase.ExecuteNonQuery(connection,
                                    "UPDATE characters SET gold = MAX(0, gold + @amt) WHERE id = @id AND (@amt <= 0 OR gold <= @maxCurrent)",
                                    ("@amt", action.value), ("@id", action.charId), ("@maxCurrent", int.MaxValue - Math.Max(0, action.value)));
                                if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                                    throw new InvalidDataException($"Gold action {action.id} was not applied exactly once");
                                object goldValue = GameDatabase.ExecuteScalar(connection, "SELECT gold FROM characters WHERE id=@id", ("@id", action.charId));
                                if (goldValue == null || goldValue == DBNull.Value)
                                    throw new InvalidDataException($"Gold action {action.id} target is missing");
                                committedGold = checked((uint)Convert.ToInt32(goldValue));
                            }
                            else if (action.actionType == "level")
                            {
                                committedLevel = SavedCharacterLevel.ResolvePersistedLevel(action.value);
                                GameDatabase.ExecuteNonQuery(connection,
                                    "UPDATE characters SET level=@lv, experience=0 WHERE id=@id",
                                    ("@lv", committedLevel), ("@id", action.charId));
                                if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                                    throw new InvalidDataException($"Level action {action.id} was not applied exactly once");
                            }
                            else
                            {
                                committedExperience = checked((uint)action.value);
                                GameDatabase.ExecuteNonQuery(connection,
                                    "UPDATE characters SET experience=@xp WHERE id=@id",
                                    ("@xp", action.value), ("@id", action.charId));
                                if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                                    throw new InvalidDataException($"XP action {action.id} was not applied exactly once");
                            }

                            GameDatabase.ExecuteNonQuery(connection, "DELETE FROM pending_admin_actions WHERE id=@id", ("@id", action.id));
                            if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                                throw new InvalidDataException($"Admin action {action.id} was not consumed exactly once");
                            transaction.Commit();
                        }

                        RRConnection onlineConn = FindConnectionByCharacterId(action.charId);
                        SavedCharacter savedChar = onlineConn != null ? GetSavedCharacterForConn(onlineConn) : null;
                        PlayerState playerState = onlineConn != null ? GetPlayerState(onlineConn.ConnId.ToString()) : null;
                        if (action.actionType == "gold")
                        {
                            if (savedChar != null) savedChar.gold = committedGold;
                            if (playerState != null) playerState.Gold = committedGold;
                            if (onlineConn != null && onlineConn.UnitContainerId != 0 && action.value != 0)
                            {
                                try
                                {
                                    var goldMessage = new LEWriter();
                                    goldMessage.WriteByte(0x07);
                                    goldMessage.WriteByte(0x35);
                                    goldMessage.WriteUInt16(onlineConn.UnitContainerId);
                                    goldMessage.WriteByte(0x20);
                                    goldMessage.WriteInt32(action.value);
                                    goldMessage.WriteByte(0x00);
                                    goldMessage.WriteUInt32(0x00000000);
                                    goldMessage.WriteByte(0x01);
                                    WritePlayerEntitySynch(onlineConn, goldMessage);
                                    goldMessage.WriteByte(0x06);
                                    SendCompressedA(onlineConn, 0x01, 0x0F, goldMessage.ToArray());
                                }
                                catch (Exception ex) { Debug.LogError($"[ADMIN-ACT] state=committed phase=notify-failed action={action.id} type=gold message='{ex.Message}'"); }
                            }
                            Debug.LogError($"[ADMIN-ACT] state=committed action={action.id} type=gold characterId={action.charId} value={action.value} balance={committedGold}");
                        }
                        else if (action.actionType == "level")
                        {
                            int committedRuntimeLevel = SavedCharacterLevel.ResolveRuntimeLevel(committedLevel);
                            int oldLevel = playerState?.Level ?? (savedChar != null ? SavedCharacterLevel.ResolveRuntimeLevel(savedChar) : committedRuntimeLevel);
                            if (savedChar != null)
                            {
                                savedChar.level = committedLevel;
                                savedChar.experience = 0;
                            }
                            if (playerState != null)
                            {
                                try
                                {
                                    playerState.InitializeStats(savedChar?.className ?? "", committedRuntimeLevel, preserveAttributeModifiers: true);
                                    if (savedChar != null)
                                        playerState.ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
                                    playerState.Experience = 0;
                                    if (onlineConn.Avatar != null)
                                        CalculateEquipmentBonuses(onlineConn.ConnId.ToString(), onlineConn.Avatar);
                                    playerState.RestoreToFull();
                                }
                                catch (Exception ex)
                                {
                                    QuarantineCommittedPersistenceSyncFailure(onlineConn, $"pending-admin-level-{action.id}", ex);
                                    continue;
                                }
                                try
                                {
                                    if (committedRuntimeLevel > oldLevel)
                                    {
                                        for (int level = oldLevel; level < committedRuntimeLevel; level++)
                                        {
                                            uint threshold = PlayerState.GetClientThreshold(level + 1);
                                            uint packetExperience = threshold * 256 / 5 + 100;
                                            SendAdminXPUpdate(onlineConn, packetExperience, (uint)level);
                                        }
                                    }
                                    SendAdminEntitySynchInfoHP(onlineConn, playerState);
                                }
                                catch (Exception ex) { Debug.LogError($"[ADMIN-ACT] state=committed phase=notify-failed action={action.id} type=level message='{ex.Message}'"); }
                            }
                            Debug.LogError($"[ADMIN-ACT] state=committed action={action.id} type=level characterId={action.charId} persisted={committedLevel} runtime={committedRuntimeLevel}");
                        }
                        else
                        {
                            if (savedChar != null) savedChar.experience = committedExperience;
                            if (playerState != null) playerState.Experience = committedExperience;
                            Debug.LogError($"[ADMIN-ACT] state=committed action={action.id} type=xp characterId={action.charId} value={committedExperience}");
                        }
                    }
                    catch (Exception ex) { Debug.LogError($"[ADMIN-ACT] Failed action {action.id}: {ex.Message}"); }
                }
            }
        }

        private void ProcessPendingGrants()
        {
            using (var connection = GameDatabase.GetConnection())
            {
                var grants = new List<(int id, int charId, string gcClass, int count, int width, int height, int rarity)>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, character_id, gc_class, count, width, height, rarity FROM pending_item_grants ORDER BY id";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            grants.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
                    }
                }
                if (grants.Count == 0) return;

                foreach (var grant in grants)
                {
                    try
                    {
                        if (HasPendingDisconnectedCharacterSaveForCharacter(checked((uint)grant.charId)))
                        {
                            Debug.LogError($"[GRANTS] state=pending grant={grant.id} characterId={grant.charId} reason=disconnect-writer-fence");
                            continue;
                        }
                        if (grant.count <= 0 || grant.count > byte.MaxValue
                            || grant.width <= 0 || grant.width > 10
                            || grant.height <= 0 || grant.height > 8
                            || grant.rarity < -1 || grant.rarity > 5)
                        {
                            Debug.LogError($"[GRANTS] state=pending grant={grant.id} characterId={grant.charId} reason=invalid-row count={grant.count} width={grant.width} height={grant.height} rarity={grant.rarity}");
                            continue;
                        }
                        RRConnection onlineConn = FindConnectionByCharacterId(grant.charId);

                        if (onlineConn != null && onlineConn.UnitContainerId != 0)
                        {
                            string connId = onlineConn.ConnId.ToString();
                            GeneralItemData authoredItem = AuthoredGameplayCatalog.FindGeneralItem(grant.gcClass);
                            if (authoredItem == null || authoredItem.InventoryWidth <= 0 || authoredItem.InventoryHeight <= 0)
                                throw new InvalidDataException($"Pending grant references unknown authored item '{grant.gcClass}'");
                            if (grant.width != authoredItem.InventoryWidth || grant.height != authoredItem.InventoryHeight)
                            {
                                Debug.LogError($"[GRANTS] state=pending grant={grant.id} characterId={grant.charId} reason=authored-dimensions row={grant.width}x{grant.height} authored={authoredItem.InventoryWidth}x{authoredItem.InventoryHeight}");
                                continue;
                            }
                            string gcTypeFull = GCObject.GetPacketGCClassFor(authoredItem.gcType);
                            int itemWidth = authoredItem.InventoryWidth;
                            int itemHeight = authoredItem.InventoryHeight;

                            string packetGcType = gcTypeFull.ToLowerInvariant();
                            if (packetGcType.StartsWith("items.pal.")) packetGcType = packetGcType.Substring(10);

                            byte slotX = 0, slotY = 0; bool foundSlot = false;
                            for (byte rowY = 0; rowY < 8 && !foundSlot; rowY++)
                                for (byte columnX = 0; columnX < 10 && !foundSlot; columnX++)
                                    if (!IsInventorySlotOccupied(connId, columnX, rowY, itemWidth, itemHeight))
                                    { slotX = columnX; slotY = rowY; foundSlot = true; }
                            if (!foundSlot) { Debug.LogError($"[GRANTS] Inventory full char {grant.charId}"); continue; }

                            int modSlots = 1;
                            if (DungeonRunners.Gameplay.MerchantRuntime.TryGetAuthoredMerchantModSlots(packetGcType, out int authoredModSlots))
                                modSlots = authoredModSlots;

                            uint slot = GetNextInventorySlot(connId);
                            PlayerState playerState = GetPlayerState(connId);

                            byte itemLevel = playerState != null ? (byte)Math.Max(1, Math.Min(playerState.Level, 100)) : (byte)1;

                            var grantItemMessage = new LEWriter();
                            grantItemMessage.WriteByte(0x07);
                            grantItemMessage.WriteByte(0x35);
                            grantItemMessage.WriteUInt16(onlineConn.UnitContainerId);
                            grantItemMessage.WriteByte(0x1E);
                            grantItemMessage.WriteByte(0x0B);
                            string packetGcTypeLower = packetGcType.ToLowerInvariant();
                            string dfcClass = "Armor";
                            if (packetGcTypeLower.Contains("consumable") || packetGcTypeLower.Contains("questitem") || packetGcTypeLower.Contains("ring") || packetGcTypeLower.Contains("amulet") || packetGcTypeLower.Contains("scroll") || packetGcTypeLower.Contains("potion") || packetGcTypeLower.Contains("skillbook") || packetGcTypeLower.Contains("voucher"))
                                dfcClass = "Item";
                            else if (packetGcTypeLower.Contains("sword") || packetGcTypeLower.Contains("axe") || packetGcTypeLower.Contains("mace") || packetGcTypeLower.Contains("dagger") || packetGcTypeLower.Contains("hammer") || packetGcTypeLower.Contains("staff") || packetGcTypeLower.Contains("spear") || packetGcTypeLower.Contains("pick") || packetGcTypeLower.Contains("club") || packetGcTypeLower.Contains("scepter") || packetGcTypeLower.Contains("wand"))
                                dfcClass = "MeleeWeapon";
                            else if (packetGcTypeLower.Contains("bow") || packetGcTypeLower.Contains("cannon") || packetGcTypeLower.Contains("crossbow") || packetGcTypeLower.Contains("xbow") || packetGcTypeLower.Contains("gun"))
                                dfcClass = "RangedWeapon";
                            var newItem = new DungeonRunners.Data.GCObject
                            {
                                GCClass = packetGcType,
                                DFCClass = dfcClass,
                                StoredRarity = grant.rarity,
                                StoredLevel = itemLevel
                            };
                            newItem.WriteInitForInventory(grantItemMessage, slotX, slotY, slot, itemLevel, (byte)grant.count);
                            WritePlayerEntitySynch(onlineConn, grantItemMessage);
                            grantItemMessage.WriteByte(0x06);

                            Debug.LogError($"[GRANTS] Sending: gc={packetGcType} modSlots={modSlots} slot={slot} pos=({slotX},{slotY})");
                            TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                            OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                            SetStackCount(connId, slot, grant.count);
                            QuestProgressMutation questMutation = StageQuestItemAcquisition(onlineConn, newItem.GCClass, grant.count);
                            if (!TrySaveFullCharacterSnapshotConsumingGrant(onlineConn, grant.id, "pending-item-grant"))
                            {
                                RollbackQuestItemAcquisition(questMutation);
                                RemoveInventoryItemBySlot(connId, slot);
                                FreeInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                Debug.LogError($"[GRANTS] state=failed phase=commit grant={grant.id} characterId={grant.charId}");
                                continue;
                            }

                            SendToClient(onlineConn, grantItemMessage.ToArray());
                            CommitQuestItemAcquisition(onlineConn, questMutation, newItem.GCClass, grant.count, "pending-item-grant");
                            if (string.Equals(newItem.GCClass, "QuestItemPAL.Token", StringComparison.OrdinalIgnoreCase)
                                || newItem.GCClass.EndsWith(".Token", StringComparison.OrdinalIgnoreCase))
                                SendSystemMessage(onlineConn, $"You received {grant.count} King's Coin{(grant.count == 1 ? "" : "s")}.");
                            Debug.LogError($"[GRANTS]  LIVE delivered {packetGcType} x{grant.count} modSlots={modSlots}");
                            continue;
                        }

                        {
                            GeneralItemData offlineAuthoredItem = AuthoredGameplayCatalog.FindGeneralItem(grant.gcClass);
                            if (offlineAuthoredItem == null || offlineAuthoredItem.InventoryWidth <= 0 || offlineAuthoredItem.InventoryHeight <= 0)
                                throw new InvalidDataException($"Pending grant references unknown authored item '{grant.gcClass}'");
                            if (grant.width != offlineAuthoredItem.InventoryWidth || grant.height != offlineAuthoredItem.InventoryHeight)
                            {
                                Debug.LogError($"[GRANTS] state=pending grant={grant.id} characterId={grant.charId} reason=authored-dimensions row={grant.width}x{grant.height} authored={offlineAuthoredItem.InventoryWidth}x{offlineAuthoredItem.InventoryHeight}");
                                continue;
                            }
                            string offlineGcType = GCObject.GetPacketGCClassFor(offlineAuthoredItem.gcType);
                            int offlineWidth = offlineAuthoredItem.InventoryWidth;
                            int offlineHeight = offlineAuthoredItem.InventoryHeight;

                            using (var transaction = connection.BeginTransaction())
                            {
                                var occupied = new HashSet<string>();
                                using (var inventoryCommand = connection.CreateCommand())
                                {
                                    inventoryCommand.Transaction = transaction;
                                    inventoryCommand.CommandText = "SELECT gc_class, slot_x, slot_y FROM character_inventory WHERE character_id=@cid AND container_id=11 ORDER BY item_order, id";
                                    inventoryCommand.Parameters.AddWithValue("@cid", grant.charId);
                                    using (var reader = inventoryCommand.ExecuteReader())
                                    {
                                        while (reader.Read())
                                        {
                                            string existingGcType = reader.GetString(0);
                                            int existingX = reader.GetInt32(1);
                                            int existingY = reader.GetInt32(2);
                                            GeneralItemData existingAuthoredItem = AuthoredGameplayCatalog.FindGeneralItem(existingGcType);
                                            if (existingAuthoredItem == null || existingAuthoredItem.InventoryWidth <= 0 || existingAuthoredItem.InventoryHeight <= 0)
                                                throw new InvalidDataException($"Character {grant.charId} inventory references unknown authored item '{existingGcType}'");
                                            for (int widthOffset = 0; widthOffset < existingAuthoredItem.InventoryWidth; widthOffset++)
                                                for (int heightOffset = 0; heightOffset < existingAuthoredItem.InventoryHeight; heightOffset++)
                                                    occupied.Add($"{existingX + widthOffset},{existingY + heightOffset}");
                                        }
                                    }
                                }

                                int freeSlotX = -1, freeSlotY = -1;
                                for (int slotY = 0; slotY <= 8 - offlineHeight && freeSlotY < 0; slotY++)
                                    for (int slotX = 0; slotX <= 10 - offlineWidth && freeSlotX < 0; slotX++)
                                    {
                                        bool fits = true;
                                        for (int widthOffset = 0; widthOffset < offlineWidth && fits; widthOffset++)
                                            for (int heightOffset = 0; heightOffset < offlineHeight && fits; heightOffset++)
                                                if (occupied.Contains($"{slotX + widthOffset},{slotY + heightOffset}")) fits = false;
                                        if (fits) { freeSlotX = slotX; freeSlotY = slotY; }
                                    }

                                if (freeSlotX < 0) { Debug.LogError($"[GRANTS] Inventory full (offline) char {grant.charId}"); continue; }

                                int itemOrder = Convert.ToInt32(GameDatabase.ExecuteScalar(connection,
                                    "SELECT COALESCE(MAX(item_order), -1) + 1 FROM character_inventory WHERE character_id=@cid AND container_id=11",
                                    ("@cid", grant.charId)) ?? 0);
                                GameDatabase.ExecuteNonQuery(connection,
                                    "INSERT INTO character_inventory (character_id,gc_class,slot_x,slot_y,count,rarity,stored_level,container_id,item_order) VALUES(@cid,@gc,@x,@y,@n,@r,-1,11,@ord)",
                                    ("@cid", grant.charId), ("@gc", offlineGcType), ("@x", freeSlotX), ("@y", freeSlotY), ("@n", grant.count), ("@r", grant.rarity), ("@ord", itemOrder));
                                GameDatabase.ExecuteNonQuery(connection, "DELETE FROM pending_item_grants WHERE id=@id AND character_id=@cid", ("@id", grant.id), ("@cid", grant.charId));
                                if (Convert.ToInt32(GameDatabase.ExecuteScalar(connection, "SELECT changes()") ?? 0) != 1)
                                    throw new InvalidDataException($"Pending item grant {grant.id} was not consumed exactly once");
                                transaction.Commit();
                                Debug.LogError($"[GRANTS]  OFFLINE inserted {offlineGcType} x{grant.count} to char {grant.charId} at ({freeSlotX},{freeSlotY})");
                            }
                        }
                    }
                    catch (Exception ex) { Debug.LogError($"[GRANTS] Failed grant {grant.id}: {ex.Message}"); }
                }
            }
        }

        private IEnumerator AcceptClientsCoroutine()
        {
            while (_isRunning)
            {
                if (_listener.Pending())
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    var endpoint = (System.Net.IPEndPoint)client.Client.RemoteEndPoint;
                    string remoteIP = endpoint.Address.IsIPv4MappedToIPv6
                        ? endpoint.Address.MapToIPv4().ToString()
                        : endpoint.Address.ToString();
                    Debug.LogError($"[GAME-PORT] Connection from {remoteIP}:{endpoint.Port}");

                    string queueUser = QueueConnection.CheckAndConsumeQueueIP(remoteIP);
                    if (queueUser != null)
                    {
                        Debug.LogError($"[QUEUE]  Queue connection from {remoteIP} for {queueUser} on GAME port");
                    }

                    int connId = _nextConnId++;
                    NetworkStream stream = client.GetStream();
                    var conn = new RRConnection(connId, client, stream);
                    if (queueUser != null)
                        conn.LoginName = queueUser;
                    _connections[connId] = conn;
                    Debug.Log($"Client {connId} connected from {client.Client.RemoteEndPoint}");
                    ServerRuntime.RunRoutine(this, HandleClientCoroutine(conn));
                }
                yield return null;
            }
        }

        private IEnumerator HandleClientCoroutine(RRConnection conn)
        {
            if (conn.LoginName != null)
            {
                LoadAccountFlags(conn.LoginName);
            }

            while (_isRunning && conn.IsConnected && conn.Client.Connected)
            {
                try
                {
                    if (conn.Client.Client.Poll(0, System.Net.Sockets.SelectMode.SelectRead) && !conn.Stream.DataAvailable)
                    {
                        Debug.LogError($"[CLIENT] Connection {conn.ConnId} dead (poll detected)");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[GAME-SERVER] handleClient conn={conn.ConnId} state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
                }
                yield return null;
            }
            Debug.Log($"Client {conn.ConnId} disconnected");
            QueueConnection.PlayerDisconnected();
            bool disconnectSnapshotOwned = false;
            uint disconnectCharacterId = conn.CharSqlId;
            if (conn.LoginAdmitted)
            {
                bool hasSelectedCharacter = disconnectCharacterId != 0
                    || (!string.IsNullOrWhiteSpace(conn.LoginName) && _activeCharacter.ContainsKey(conn.LoginName));
                if (!hasSelectedCharacter)
                {
                    disconnectSnapshotOwned = true;
                }
                else if (TryCaptureDisconnectCharacterSnapshot(conn, out PendingDisconnectedCharacterSave pendingSave)
                    && EnqueuePendingDisconnectedCharacterSave(pendingSave))
                {
                    disconnectSnapshotOwned = true;
                    disconnectCharacterId = pendingSave.CharacterId;
                    DrainPendingDisconnectedCharacterSaves();
                    while (_isRunning && !IsPendingDisconnectedCharacterSaveDurable(pendingSave))
                    {
                        TryPersistPendingDisconnectedCharacterSpool();
                        DrainPendingDisconnectedCharacterSaves();
                        if (!IsPendingDisconnectedCharacterSaveDurable(pendingSave))
                            yield return ServerRuntime.DelaySeconds(1);
                    }
                }
                else
                {
                    PendingDisconnectCaptureFailure captureFailure = MarkDisconnectCaptureFailure(conn);
                    while (!IsFailedDisconnectCaptureFenceDurable(captureFailure))
                    {
                        TryPersistPendingDisconnectedCharacterSpool();
                        if (!IsFailedDisconnectCaptureFenceDurable(captureFailure))
                        {
                            if (!_isRunning)
                                yield break;
                            yield return ServerRuntime.DelaySeconds(1);
                        }
                    }
                }
                TouchSoloDungeonInstance(conn, "disconnect");
            }

            CancelPendingRoomClientEpoch(conn);
            BlingGnomeRuntime.Instance.OnPlayerDisconnect(conn.ConnId);
            _pendingBlingGnomeSkillEffects.RemoveAll(effect => ReferenceEquals(effect.Owner, conn));

            if (conn.LoginAdmitted && conn.LoginName != null)
                _activeModifiers.Clear(conn.LoginName);

            if (conn.LoginAdmitted && conn.LoginName != null)
                _debuffCooldowns.Remove(conn.LoginName);

            if (conn.LoginAdmitted && conn.LoginName != null)
            {
                _freePlayerModifierComponentIds.Remove(conn.LoginName);
            }

            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group != null)
            {
                var groupMember = group.Members.Find(member => member.ConnId == conn.ConnId);
                if (groupMember != null)
                {
                    groupMember.CharSqlId = GetCharSqlId(conn);
                    groupMember.AvatarEntityId = GetPlayerAvatarId(conn.LoginName);
                    if (_selectedCharacter.TryGetValue(conn.LoginName, out var selectedCharacter))
                        groupMember.Name = selectedCharacter.Name ?? groupMember.Name;
                }

                uint disconnectedCharId = GetCharSqlId(conn);

                if (GroupDirectory.Instance.DisconnectMember(conn.ConnId))
                {
                    if (group.Members.Any(member => member.IsOnline))
                    {
                        byte[] disconnectPacket = GroupPackets.BuildMemberDisconnected(group.GroupId, disconnectedCharId);
                        foreach (var remainingMember in group.Members)
                        {
                            if (!remainingMember.IsOnline) continue;
                            var remainingMemberConnection = FindConnectionById(remainingMember.ConnId);
                            if (remainingMemberConnection != null)
                                SendToClient(remainingMemberConnection, disconnectPacket);
                        }

                        Debug.LogError($"[GROUP] Sent 0x49 for disconnect: {conn.LoginName} charId=0x{disconnectedCharId:X8}");
                    }
                    else if (conn.ReplacementConnectionId == 0)
                    {
                        foreach (var member in group.Members.ToList())
                            GroupDirectory.Instance.LeaveGroup(member.ConnId);
                    }
                }
                conn.GroupConnectedSent = false;
            }

            if (conn.IsSpawned)
            {
                BroadcastEntityRemove(conn, conn.CurrentZoneGcType);
                conn.IsSpawned = false;
            }
            _monsterSpawnSentByConn.Remove(conn.ConnId);
            _encounterObjectSentByConn.Remove(conn.ConnId);
            ReassignMonsterOwnership(conn);

            string disconnKey = conn.ConnId.ToString();
            ClearRemoteActionRelayState(conn);
            ClearPvpConnectionRuntime(conn, "disconnect", true);
            ClearUseTarget(conn);
            CancelPendingCombatForConnection(conn);
            ClearActiveSkillCooldownRuntimeForConnection(conn);
            Combat.WeaponUseRuntime.Instance.ClearConnection(disconnKey);
            if (conn.Avatar != null)
                CombatRuntime.Instance.UnregisterPlayer((uint)conn.Avatar.Id);
            else if (_playerAvatarEntityId.TryGetValue(disconnKey, out uint oldAvatarId))
                CombatRuntime.Instance.UnregisterPlayer(oldAvatarId);
            Debug.LogError($"[COMBAT-LIFECYCLE] disconnect cleared combat state conn={conn.ConnId} avatar={(conn.Avatar != null ? conn.Avatar.Id : 0)}");
            _playerAvatarEntityId.Remove(disconnKey);
            _playerNextSkillEntityId.Remove(disconnKey);
            _playerSkillsComponentId.Remove(disconnKey);
            _playerSkillSlots.Remove(disconnKey);
            _encounterObjectSentByConn.Remove(conn.ConnId);
            _monsterSpawnSentByConn.Remove(conn.ConnId);
            CancelTradeOnDisconnect(conn);
            _users.Remove(conn.ConnId);
            _connections.Remove(conn.ConnId);

            if (conn.LoginAdmitted)
            {
                try
                {
                    var forfeited = Gameplay.PVPMatchmaking.Instance.HandleDisconnect(conn.LoginName, _combatTick);
                    if (forfeited != null) FinalizeMatchResults(forfeited);
                }
                catch (Exception ex) { Debug.LogError($"[PVP] disconnect cleanup: {ex.Message}"); }

                try
                {
                    var forfeitedDuel = _duelRuntime.HandleDisconnect(conn.LoginName, _combatTick);
                    if (forfeitedDuel != null)
                    {
                        string remainingLogin = string.Equals(forfeitedDuel.ChallengerLogin, conn.LoginName, StringComparison.OrdinalIgnoreCase)
                            ? forfeitedDuel.TargetLogin
                            : forfeitedDuel.ChallengerLogin;
                        if (forfeitedDuel.State == Gameplay.DuelRuntime.DuelState.Active)
                        {
                            SendDuelEndPackets(remainingLogin, conn.LoginName, forfeitedDuel);
                        }
                        else
                        {
                            RRConnection remaining = FindConnectionByLogin(remainingLogin);
                            if (remaining != null)
                            {
                                uint disconnectedCharacterId = string.Equals(forfeitedDuel.ChallengerLogin, conn.LoginName, StringComparison.OrdinalIgnoreCase)
                                    ? forfeitedDuel.ChallengerCharSqlId
                                    : forfeitedDuel.TargetCharSqlId;
                                SendToClient(remaining, PVPPackets.BuildDuelStatus(PVPPackets.DuelStatusType.Cancelled, disconnectedCharacterId, 0, 0));
                            }
                        }
                    }
                }
                catch (Exception ex) { Debug.LogError($"[PVP-DUEL] disconnect cleanup: {ex.Message}"); }
            }

            if (conn.LoginAdmitted && conn.LoginName != null && _selectedCharacter.TryGetValue(conn.LoginName, out var disconnChar))
            {
                SocialRuntime.Instance.PlayerOffline(conn.LoginName, disconnChar.Name, SendSocialViaAuth);
                try { PosseRuntime.Instance.NotifyMemberStateChange(disconnChar.Id, this); }
                catch (Exception ex) { Debug.LogError($"[POSSE] disconnect notify failed: {ex.Message}"); }
            }
            else if (conn.LoginAdmitted && conn.LoginName != null)
            {
                SocialRuntime.Instance.ForceRemoveOnline(conn.LoginName);
            }
            if (conn.LoginAdmitted && disconnectSnapshotOwned && disconnectCharacterId != 0)
                InvalidateCharacterRuntimeState(conn, disconnectCharacterId);
            if (conn.LoginAdmitted && conn.ReplacementConnectionId == 0)
                QueueConnection.RemoveQueueStream(conn.LoginName);

            conn.LoginAdmissionPending = false;
            _persistenceSaveSuppressedConnections.Remove(conn.ConnId);
            conn.Disconnect();
        }

        private void DrainLiveClientInputBeforeNativeServerTick()
        {
            DrainPendingDisconnectedCharacterSaves();
            DrainPendingInitialLoginAdmissions();
            if (_connections == null || _connections.Count == 0)
                return;

            byte[] buffer = new byte[8192];
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsConnected || conn.Client == null || conn.Stream == null)
                    continue;
                if (!conn.Client.Connected)
                    continue;

                try
                {
                    while (conn.Stream.DataAvailable)
                    {
                        int bytesRead = conn.Stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead <= 0)
                            break;

                        byte[] data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);
                        ProcessReceivedBytes(conn, data);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[GAME-INPUT] drain conn={conn.ConnId} state=failed message='{ex.Message}'");
                }
            }
        }

        private void ProcessMessage(RRConnection conn, byte[] data)
        {
            ProcessReceivedBytes(conn, data);
        }

        private void ProcessReceivedBytes(RRConnection conn, byte[] data)
        {
            if (conn == null || data == null)
                return;
            if (data.Length > 0)
                conn.InboundGameBytes.AddRange(data);

            while (conn.InboundGameBytes.Count > 0)
            {
                int messageLen = CalculateBufferedMessageLength(conn.InboundGameBytes, 0);
                if (messageLen == 0)
                    break;
                if (messageLen < 0 || messageLen > conn.InboundGameBytes.Count)
                {
                    byte badType = conn.InboundGameBytes[0];
                    conn.InboundGameBytes.RemoveAt(0);
                    Debug.LogError($"[GAME-INPUT] Dropped invalid top-level byte 0x{badType:X2}; buffered={conn.InboundGameBytes.Count}");
                    continue;
                }

                byte[] singleMessage = new byte[messageLen];
                conn.InboundGameBytes.CopyTo(0, singleMessage, 0, messageLen);
                conn.InboundGameBytes.RemoveRange(0, messageLen);
                ProcessSingleMessage(conn, singleMessage);
                if (conn.LoginAdmissionPending)
                    break;
            }
        }

        private int CalculateBufferedMessageLength(List<byte> data, int offset)
        {
            int remaining = data.Count - offset;
            if (remaining < 1)
                return 0;

            byte messageType = data[offset];
            switch (messageType)
            {
                case 0x02:
                    return 1;
                case 0x03:
                    return remaining >= 4 ? 4 : 0;
                case 0x0A:
                    if (remaining < 8) return 0;
                    uint bodyLen = ReadBufferedUInt32(data, offset + 4);
                    return CompleteBufferedLength(remaining, bodyLen, 8);
                case 0x0E:
                    if (remaining < 8) return 0;
                    uint eBodyLen = ReadBufferedUInt24(data, offset + 4);
                    return CompleteBufferedLength(remaining, eBodyLen, 8);
                case 0x10:
                    if (remaining < 8) return 0;
                    uint directBodyLen = ReadBufferedUInt24(data, offset + 4);
                    return CompleteBufferedLength(remaining, directBodyLen, 8);
                default:
                    return 1;
            }
        }

        private static int CompleteBufferedLength(int remaining, uint bodyLen, int headerLen)
        {
            if (bodyLen > 1024 * 1024)
                return -1;
            int total = headerLen + (int)bodyLen;
            return remaining >= total ? total : 0;
        }

        private static uint ReadBufferedUInt24(List<byte> data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));
        }

        private static uint ReadBufferedUInt32(List<byte> data, int offset)
        {
            return (uint)(data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));
        }

        private void ProcessBufferedMessageBlock(RRConnection conn, byte[] data)
        {
            int offset = 0;

            while (offset < data.Length)
            {
                if (data.Length - offset < 1) break;

                byte messageType = data[offset];
                int messageLen = CalculateMessageLength(data, offset, messageType);

                if (messageLen <= 0 || offset + messageLen > data.Length)
                {
                    Debug.LogError($"[MSG-LOOP] Invalid message length {messageLen} at offset {offset}, remaining {data.Length - offset}");
                    break;
                }

                byte[] singleMessage = new byte[messageLen];
                Array.Copy(data, offset, singleMessage, 0, messageLen);

                ProcessSingleMessage(conn, singleMessage);

                offset += messageLen;
            }
        }

        private int CalculateMessageLength(byte[] data, int offset, byte messageType)
        {
            switch (messageType)
            {
                case 0x02:
                    return 1;
                case 0x03:
                    return 4;
                case 0x0A:
                    if (data.Length - offset < 8) return -1;
                    uint bodyLen = BitConverter.ToUInt32(data, offset + 4);
                    return 8 + (int)bodyLen;
                case 0x0E:
                    if (data.Length - offset < 8) return -1;
                    uint eBodyLen = (uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16));
                    return 8 + (int)eBodyLen;
                case 0x10:
                    if (data.Length - offset < 8) return -1;
                    uint directBodyLen = (uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16));
                    return 8 + (int)directBodyLen;
                default:
                    return 1;
            }
        }

        private void ProcessSingleMessage(RRConnection conn, byte[] data)
        {
            WirePacketTally.OnRawTCP(data);
            if (data.Length < 1) return;

            byte messageType = data[0];

            switch (messageType)
            {
                case 0x02:
                    HandlePing(conn, data);
                    break;
                case 0x03:
                    HandleConnect(conn, data);
                    break;
                case 0x0A:
                    HandleCompressedA(conn, data);
                    break;
                case 0x0E:
                    HandleCompressedE(conn, data);
                    break;
                case 0x10:
                    HandleDirectMessage(conn, data);
                    break;
                default:
                    Debug.LogError($"[RAW-IN] UNKNOWN message type: 0x{messageType:X2}");
                    break;
            }
        }

        private void HandlePing(RRConnection conn, byte[] data)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x02);
            if (data.Length > 1)
            {
                byte[] pingData = new byte[data.Length - 1];
                Array.Copy(data, 1, pingData, 0, data.Length - 1);
                writer.WriteBytes(pingData);
            }
            byte[] response = writer.ToArray();
            lock (conn.SendLock)
            {
                conn.Stream.Write(response, 0, response.Length);
            }
        }

        private void HandleConnect(RRConnection conn, byte[] data)
        {
            Debug.Log($"Client {conn.ConnId} sent connect message");

            var reader = new LEReader(data);
            reader.ReadByte();
            uint clientId = reader.ReadUInt24();
            _peerId24[conn.ConnId] = clientId;

            Debug.Log($"[CLIENT] peerId=0x{clientId:X6}");

            var writer = new LEWriter();
            writer.WriteByte(0x04);
            writer.WriteUInt24((int)clientId);
            writer.WriteUInt32(0);

            byte[] response = writer.ToArray();
            lock (conn.SendLock)
            {
                conn.Stream.Write(response, 0, response.Length);
            }
            Debug.Log($"Sent connect response to client {conn.ConnId}");
        }

        private void HandleCompressedA(RRConnection conn, byte[] data)
        {
            try
            {
                var reader = new LEReader(data);
                reader.ReadByte();
                uint peerId = reader.ReadUInt24();
                uint bodyLen = reader.ReadUInt32();
                byte channel = reader.ReadByte();
                byte messageType = reader.ReadByte();
                reader.ReadByte();
                uint uncompressedLen = reader.ReadUInt32();

                byte[] compressed = reader.ReadBytes((int)(bodyLen - 7));
                byte[] payload = ZlibUtil.Inflate(compressed);

                HandleChannelMessage(conn, channel, messageType, payload);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GAME-SERVER] handleCompressedA state=failed message='{ex.Message}'");
            }
        }

        private void HandleCompressedE(RRConnection conn, byte[] data)
        {
            try
            {
                var reader = new LEReader(data);
                reader.ReadByte();
                uint dest = reader.ReadUInt24();
                uint bodyLen = reader.ReadUInt24();
                reader.ReadByte();
                uint source = reader.ReadUInt24();
                reader.ReadBytes(5);
                uint uncompressedLen = reader.ReadUInt32();

                byte[] compressed = reader.ReadBytes((int)(bodyLen - 12));

                byte[] payload = ZlibUtil.Inflate(compressed);

                if (payload.Length >= 2)
                {
                    byte channel = payload[0];
                    byte messageType = payload[1];
                    byte[] innerData = new byte[payload.Length - 2];
                    Array.Copy(payload, 2, innerData, 0, innerData.Length);

                    HandleChannelMessage(conn, channel, messageType, innerData);
                }
                else
                {
                    Debug.LogError($"[E-LANE] Payload too short: {payload.Length} bytes");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[E-LANE] state=failed message='{ex.Message}'");
            }
        }

        private void HandleDirectMessage(RRConnection conn, byte[] data)
        {
            try
            {
                var reader = new LEReader(data);
                reader.ReadByte();
                uint peerId = reader.ReadUInt24();
                uint bodyLen = reader.ReadUInt24();
                byte channel = reader.ReadByte();
                byte[] payload = reader.ReadBytes((int)bodyLen);

                Debug.Log($"Direct message: peer=0x{peerId:X6}, channel={channel}");
                HandleChannelMessage(conn, channel, 0, payload);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GAME-SERVER] handleDirectMessage state=failed message='{ex.Message}'");
            }
        }

        private void HandleChannelMessage(RRConnection conn, byte channel, byte messageType, byte[] data)
        {
            WirePacketTally.OnChannel(channel, messageType, data);
            if (channel != 0 && (conn == null || !conn.LoginAdmitted || conn.LoginAdmissionPending))
            {
                Debug.LogError($"[LOGIN] state=rejected phase=pre-admission-channel conn={conn?.ConnId ?? 0} channel={channel} type={messageType}");
                conn?.Disconnect();
                return;
            }

            switch (channel)
            {
                case 0:
                    HandleInitialConnection(conn, messageType, data);
                    break;
                case 3:
                    SocialRuntime.Instance.HandleMessage(conn, messageType, data, SendSocialViaAuth);
                    break;
                case 4:
                    HandleCharacterChannel(conn, messageType, data);
                    break;
                case 6:
                    Debug.LogError($"[CHAT-CH6] type=0x{messageType:X2} len={data?.Length ?? 0} hex={(data != null && data.Length > 0 ? BitConverter.ToString(data, 0, Math.Min(data.Length, 48)) : "")}");
                    if (_adminCommands == null)
                    {
                        _adminCommands = new AdminCommands();
                        _adminCommands.SetServerCallbacks(
                            (connection, zone) => ChatChangeZone(connection, zone),
                            (connection) => GetActiveCharacter(connection)?.DeepClone(),
                            (connection, character, reason) => TrySaveCharacterForConn(connection, character, reason),
                            HandleAdminLevelUp,
                            (login, gcType, modId) => _activeModifiers.AddOrReplace(login, new ActiveModifier
                            {
                                GCType = gcType,
                                Id = modId,
                                Level = 0,
                                PowerLevel = 0,
                                Duration = 0,
                                SourceIsSelf = 0,
                                AddedSimulationTick = _combatTick
                            }),
                            (login, modId) => _activeModifiers.RemoveById(login, modId),
                            AdminCompleteQuest,
                            (c, w) => { WritePlayerEntitySynch(c, w); }
                        );
                    }
                    {
                        PlayerState adminPs = GetPlayerState(conn.ConnId.ToString());
                        if (IsPlayerAdmin(conn.LoginName) && _adminCommands.TryExecute(conn, messageType, data, adminPs, SendSystemMessage, SendToClient))
                            break;
                    }
                    {
                        bool handledByGnome = false;
                        try
                        {
                            var peekReader = new LEReader(data);
                            string peekMessage = peekReader.ReadCString();
                            if (peekMessage.StartsWith("@"))
                            {
                                string command = peekMessage.Substring(1).Trim().ToLower();
                                if (command == "gnome" || command == "bling" || command == "blinggnome" || command == "gs" || command == "gnomestatus")
                                {
                                    BlingGnomeRuntime.Instance.SetServer(this);
                                    BlingGnomeRuntime.Instance.TryExecute(conn, data, SendCompressedA, SendSystemMessage);
                                    handledByGnome = true;
                                }
                            }
                        }
                        catch { }
                        if (handledByGnome) break;
                    }
                    if (_chatCommands == null) _chatCommands = new ChatCommands(this);
                    if (!_chatCommands.TryExecute(conn, data, SendSystemMessage))
                    {
                    }
                    {
                        try
                        {
                            var chatReader = new LEReader(data);
                            string chatText = chatReader.ReadCString();
                            if (!string.IsNullOrEmpty(chatText) && !chatText.StartsWith("/") && !chatText.StartsWith("@"))
                            {
                                HandleChatMessage(conn, messageType, chatText);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[CHAT-RELAY] state=failed message='{ex.Message}'");
                        }
                    }
                    break;
                case 7:
                    HandleClientEntityChannel(conn, messageType, data);
                    break;
                case 9:
                    HandleGroupChannel(conn, messageType, data);
                    break;
                case 10:
                    HandleTradeChannel(conn, messageType, data);
                    break;
                case 12:
                    SocialRuntime.Instance.HandleMessage(conn, messageType, data, SendSocialViaAuth);
                    break;
                case 13:
                    Debug.LogError($"[CH13]  type=0x{messageType:X2} dataLen={data?.Length ?? 0} hex={(data != null && data.Length > 0 ? BitConverter.ToString(data, 0, Math.Min(30, data.Length)) : "EMPTY")} ");
                    if (messageType == 0x07)
                    {
                        Debug.LogError($"[CH13] -> Routing to goToCheckpoint (0x07)");
                        HandleCheckpointTeleportRequest(conn, data);
                    }
                    else if (messageType == 0x0C)
                    {
                        Debug.LogError($"[CH13] -> Routing to Obelisk (0x0C)");
                        HandleObeliskTeleport(conn);
                    }
                    else if (messageType == 0x0B)
                    {
                        Debug.LogError($"[CH13] -> Routing to SavedPlace (0x0B)");
                        HandleSavedPlaceTeleport(conn);
                    }
                    else
                    {
                        Debug.LogError($"[CH13] -> UNHANDLED type 0x{messageType:X2} - routing to HandleZoneChannel");
                        HandleZoneChannel(conn, data);
                    }
                    break;
                case 11:
                    HandleGroupClientChannel(conn, messageType, data);
                    break;
                case 15:
                    PosseRuntime.Instance.HandleMessage(conn, messageType, data, SendSocialViaAuth, this);
                    break;
                default:
                    {
                        string hex = (data != null && data.Length > 0)
                            ? BitConverter.ToString(data, 0, Math.Min(48, data.Length))
                            : "EMPTY";
                        Debug.LogError($"[POSSE] unhandled channel={channel} type=0x{messageType:X2} dataLen={data?.Length ?? 0} hex={hex}");
                    }
                    break;
            }
        }

        private void HandleCheckpointTeleportRequest(RRConnection conn, byte[] data)
        {
            Debug.LogError($"[CP-TELEPORT] ");
            Debug.LogError($"[CP-TELEPORT] Data hex: {BitConverter.ToString(data ?? new byte[0])}");

            try
            {
                if (data == null || data.Length < 1)
                {
                    Debug.LogError($"[CP-TELEPORT]  Empty data");
                    return;
                }

                byte tag = data[0];
                string checkpointName = null;

                if (tag == 0xFF && data.Length > 2)
                {
                    int end = System.Array.IndexOf(data, (byte)0x00, 1);
                    if (end > 1)
                        checkpointName = System.Text.Encoding.ASCII.GetString(data, 1, end - 1);
                    Debug.LogError($"[CP-TELEPORT] Tag 0xFF -> checkpoint name: '{checkpointName}'");
                }
                else if (tag == 0x00)
                {
                    Debug.LogError($"[CP-TELEPORT] Tag 0x00 = null ref - using obelisk rotator");
                    HandleObeliskTeleport(conn);
                    return;
                }
                else
                {
                    Debug.LogError($"[CP-TELEPORT]  Unknown tag: 0x{tag:X2}");
                    return;
                }

                if (string.IsNullOrEmpty(checkpointName))
                {
                    Debug.LogError($"[CP-TELEPORT]  Empty checkpoint name");
                    return;
                }

                var checkpoint = AuthoredGameplayCatalog.Checkpoints.FirstOrDefault(c =>
                    c.id.Equals(checkpointName, StringComparison.OrdinalIgnoreCase));

                if (checkpoint == null)
                {
                    Debug.LogError($"[CP-TELEPORT]  Checkpoint not in database: '{checkpointName}'");
                    return;
                }

                string destinationZone = checkpoint.zone;
                Debug.LogError($"[CP-TELEPORT]  Teleporting to '{destinationZone}' via checkpoint '{checkpointName}'");

                TryFindZoneByName(destinationZone, out Zone destZone);

                if (destZone != null)
                {
                    conn.PendingSpawnFixedX = destZone.SpawnFixedX;
                    conn.PendingSpawnFixedY = destZone.SpawnFixedY;
                    conn.PendingSpawnFixedZ = destZone.SpawnFixedZ;
                }

                ChangeZone(conn, destinationZone, "");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CP-TELEPORT] state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
            }
        }

        private HashSet<uint> _finalizedMonsterKills = new HashSet<uint>();
        private int _mpDiagCounter = 0;


        public int GetDifficultyForConn(RRConnection conn)
        {
            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group != null)
                return Math.Max(0, Math.Min(3, (int)group.MonsterDifficulty));
            return GetPersonalDifficultyForConn(conn);
        }

        private void ApplyDifficultyToMonsters(RRConnection conn, string instanceKey)
        {
            foreach (Monster monster in CombatRuntime.Instance.GetMonstersInZone(instanceKey))
                ApplyServerReconstructionScaling(monster);
        }

        private void ApplyServerReconstructionScaling(Monster monster)
        {
            if (monster == null || monster.ServerReconstructionScalingApplied)
                return;

            int partySize = 1;
            RRConnection anchor = null;
            string monsterInstanceKey = RoomRuntime.NormalizeInstanceKey(monster.InstanceKey);
            if (!IsPublicZone(monster.ZoneName))
            {
                foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
                {
                    if (candidate == null || !candidate.IsConnected || !candidate.IsSpawned)
                        continue;
                    if (!string.Equals(RoomRuntime.NormalizeInstanceKey(candidate.RuntimeInstanceKey), monsterInstanceKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    anchor = candidate;
                    break;
                }

                Group group = anchor == null ? null : GroupDirectory.Instance.GetGroupForConn(anchor.ConnId);
                if (group != null)
                {
                    int presentMembers = 0;
                    foreach (GroupMember member in group.Members)
                    {
                        if (!member.IsOnline)
                            continue;
                        RRConnection memberConnection = FindConnectionById(member.ConnId);
                        if (memberConnection == null || !memberConnection.IsConnected || !memberConnection.IsSpawned)
                            continue;
                        if (!string.Equals(RoomRuntime.NormalizeInstanceKey(memberConnection.RuntimeInstanceKey), monsterInstanceKey, StringComparison.OrdinalIgnoreCase))
                            continue;
                        presentMembers++;
                    }
                    partySize = Math.Clamp(presentMembers, 1, 5);
                }
            }

            monster.ServerReconstructionPartySize = (byte)partySize;
            monster.ServerReconstructionGoldPercent = 100;
            monster.ServerReconstructionItemLevelBonus = 0;
            monster.ServerReconstructionScalingApplied = true;
            Debug.LogError($"[SERVER-RECONSTRUCTION] area=spawn instance='{monsterInstanceKey}' monster={monster.Name}#{monster.EntityId} partySize={partySize} difficulty={monster.ServerReconstructionDifficulty} hpWire={monster.MaxHPWire} healthFactorF32={monster.HealthFactorF32} damageFactorF32={monster.DamageFactorF32} lootFactorF32={monster.LootFactorF32} policy=native-difficulty");
        }

        public void SendMonsterToGroupInZone(string zoneName, uint instanceId, Monster monster)
        {
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (!conn.IsConnected || !conn.IsSpawned) continue;
                if (!string.Equals(conn.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)) continue;
                if (conn.InstanceId != instanceId) continue;

                SendMonsterToClient(conn, monster);
                Debug.LogError($"[GROUP-SPAWN] Sent {monster.Name} to {conn.LoginName} in {zoneName} inst={instanceId}");
            }
        }

        public void BroadcastMonsterDespawnToZone(string zoneName, uint instanceId, uint entityId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x05);
            writer.WriteUInt16((ushort)entityId);
            writer.WriteByte(0x06);
            byte[] despawnPacket = writer.ToArray();

            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (!conn.IsConnected || !conn.IsSpawned) continue;
                if (!string.Equals(conn.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)) continue;
                if (conn.InstanceId != instanceId) continue;

                SendToClient(conn, despawnPacket);
                Debug.LogError($"[GROUP-KILL] Sent despawn for entity {entityId} to {conn.LoginName}");
            }
        }

        public void BroadcastRNGSeedToZone(string zoneName, uint instanceId, uint seed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x0C);
            writer.WriteUInt32(seed);
            writer.WriteByte(0x06);
            byte[] seedPacket = writer.ToArray();

            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (!conn.IsConnected || !conn.IsSpawned) continue;
                if (!string.Equals(conn.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)) continue;
                if (conn.InstanceId != instanceId) continue;

                SendToClient(conn, seedPacket);
            }
            Debug.LogError($"[RNG-BROADCAST] Seed 0x{seed:X8} sent to inst={instanceId} in {zoneName}");
        }

        private void BroadcastRNGSeedToOthersInZone(RRConnection sender, uint seed)
        {
            if (string.IsNullOrEmpty(sender.CurrentZoneName)) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x0C);
            writer.WriteUInt32(seed);
            writer.WriteByte(0x06);
            byte[] seedPacket = writer.ToArray();

            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == sender) continue;
                if (!conn.IsConnected || !conn.IsSpawned) continue;
                if (!string.Equals(conn.CurrentZoneName, sender.CurrentZoneName, StringComparison.OrdinalIgnoreCase)) continue;

                SendToClient(conn, seedPacket);
            }
        }


        public static bool IsPublicZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return true;
            string lower = zoneName.ToLower();
            if (lower.Contains("town")) return true;
            if (lower.Contains("tutorial")) return true;
            if (lower.Contains("dew") || lower.Contains("valley")) return true;
            if (lower == "pvp_start" || lower == "pvp_hub") return true;
            if (lower == "test_pvp1" || lower == "test_pvp2") return true;
            return false;
        }

        private void AssignInstanceId(RRConnection conn)
        {
            if (IsPublicZone(conn.CurrentZoneName))
            {
                conn.InstanceId = 0;
                StampRuntimeInstanceKey(conn, "public-zone");
                Debug.LogError($"[INSTANCE] {conn.LoginName} -> PUBLIC zone '{conn.CurrentZoneName}' (instance 0)");
                return;
            }


            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);

            if (group != null)
            {
                foreach (var member in group.Members)
                {
                    if (member.ConnId == conn.ConnId) continue;
                    var memberConn = FindConnectionById(member.ConnId);
                    if (memberConn == null) continue;
                    if (memberConn.CurrentZoneName != conn.CurrentZoneName) continue;
                    if (memberConn.InstanceId == 0) continue;

                    conn.InstanceId = memberConn.InstanceId;
                    StampRuntimeInstanceKey(conn, "group-member");
                    Debug.LogError($"[INSTANCE] {conn.LoginName} -> DUNGEON '{conn.CurrentZoneName}' (joined group member {memberConn.LoginName}'s instance {conn.InstanceId:X8})");
                    return;
                }

                conn.InstanceId = group.GroupId;
                StampRuntimeInstanceKey(conn, "group");
                Debug.LogError($"[INSTANCE] {conn.LoginName} -> DUNGEON '{conn.CurrentZoneName}' (group instance {group.GroupId})");
                return;
            }

            if (!string.IsNullOrEmpty(conn.LoginName))
            {
                foreach (var other in GetConnectionInsertionOrderSnapshot())
                {
                    if (other == conn) continue;
                    if (other.CurrentZoneName != conn.CurrentZoneName) continue;
                    if (other.InstanceId == 0) continue;

                    var otherGroup = GroupDirectory.Instance.GetGroupForConn(other.ConnId);
                    if (otherGroup == null) continue;

                    bool weAreMember = otherGroup.Members.Any(m =>
                        string.Equals(m.LoginName, conn.LoginName, System.StringComparison.OrdinalIgnoreCase));
                    if (!weAreMember) continue;

                    conn.InstanceId = other.InstanceId;
                    StampRuntimeInstanceKey(conn, "stale-group-recovery");
                    Debug.LogError($"[INSTANCE] {conn.LoginName} -> DUNGEON '{conn.CurrentZoneName}' (joined via stale-group-recovery, latched onto {other.LoginName}'s instance {conn.InstanceId:X8})");
                    return;
                }
            }

            conn.InstanceId = AllocateSoloDungeonInstanceId(conn, conn.CurrentZoneName);
            StampRuntimeInstanceKey(conn, "solo");
            Debug.LogError($"[INSTANCE] {conn.LoginName} -> DUNGEON '{conn.CurrentZoneName}' (SOLO instance {conn.InstanceId:X8}, owner {GetSoloDungeonInstanceOwnerKey(conn, conn.CurrentZoneName)})");
        }

        public bool CanSeePlayer(RRConnection a, RRConnection b)
        {
            if (a == b) return false;
            if (!a.IsConnected || !b.IsConnected) return false;
            if (!a.IsSpawned || !b.IsSpawned) return false;
            if (a.CurrentZoneGcType != b.CurrentZoneGcType) return false;
            if (a.InstanceId != b.InstanceId) return false;
            return true;
        }

        public string GetInstanceZoneKey(RRConnection conn)
        {
            if (conn == null)
                return "";
            string computed = BuildRuntimeInstanceKey(conn);
            if (string.Equals(conn.RuntimeInstanceKey, computed, StringComparison.OrdinalIgnoreCase))
                return conn.RuntimeInstanceKey;
            if (!string.IsNullOrWhiteSpace(conn.RuntimeInstanceKey))
                conn.PreviousRuntimeInstanceKey = conn.RuntimeInstanceKey;
            conn.RuntimeInstanceKey = computed;
            return computed;
        }

        private string BuildRuntimeInstanceKey(RRConnection conn)
        {
            if (conn == null)
                return "";
            if (IsPublicZone(conn.CurrentZoneName))
                return conn.CurrentZoneName ?? "";
            return $"{conn.CurrentZoneName}_inst{conn.InstanceId}";
        }

        private void StampRuntimeInstanceKey(RRConnection conn, string source)
        {
            if (conn == null)
                return;
            string computed = BuildRuntimeInstanceKey(conn);
            if (!string.Equals(conn.RuntimeInstanceKey, computed, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(conn.RuntimeInstanceKey))
                conn.PreviousRuntimeInstanceKey = conn.RuntimeInstanceKey;
            conn.RuntimeInstanceKey = computed;
            Debug.LogError($"[INSTANCE] runtimeKey='{conn.RuntimeInstanceKey}' source={source ?? "unknown"} zone='{conn.CurrentZoneName}' instance={conn.InstanceId:X8}");
        }


        private GroupMemberInfo BuildGroupMemberInfo(RRConnection conn)
        {
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            string charName = conn.LoginName ?? "Unknown";
            uint charSqlId = GetCharSqlId(conn);

            if (_selectedCharacter.TryGetValue(conn.LoginName, out var selectedCharacter))
                charName = selectedCharacter.Name ?? charName;

            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group != null)
            {
                var groupMember = group.Members.Find(member => member.ConnId == conn.ConnId);
                if (groupMember != null)
                {
                    groupMember.CharSqlId = charSqlId;
                    groupMember.AvatarEntityId = avatarId;
                    groupMember.Name = charName;
                }
            }

            return new GroupMemberInfo
            {
                CharSQLID = charSqlId,
                Name = charName,
                AvatarEntityId = avatarId,
                IsOnline = true
            };
        }

        private GroupMemberInfo BuildGroupMemberInfoFromCache(Gameplay.GroupMember groupMember)
        {
            return new GroupMemberInfo
            {
                CharSQLID = groupMember.CharSqlId,
                Name = groupMember.Name ?? "Unknown",
                AvatarEntityId = groupMember.AvatarEntityId,
                IsOnline = groupMember.IsOnline
            };
        }

        private uint GetCharSqlId(RRConnection conn)
        {
            if (conn == null) return 0;
            if (!string.IsNullOrEmpty(conn.LoginName)
                && _selectedCharacter.TryGetValue(conn.LoginName, out var selectedCharacter)
                && selectedCharacter.Id != 0)
                return (uint)selectedCharacter.Id;
            if (conn.CharSqlId != 0) return conn.CharSqlId;
            return (uint)(conn.ConnId + 1);
        }

        private RRConnection FindGroupMemberByCharSqlId(Gameplay.Group group, uint charSqlId)
        {
            foreach (var member in group.Members)
            {
                var memberConnection = FindConnectionById(member.ConnId);
                if (memberConnection != null && GetCharSqlId(memberConnection) == charSqlId) return memberConnection;
            }
            return null;
        }
        public void SpawnAdminShop(RRConnection conn, Action<RRConnection, string> sendMessage)
        {
            DespawnAdminShop(conn, sendMessage);
            string[] adminMerchants = {
                "world.town.npc.AdminWeaponVendor",
                "world.town.npc.AdminArmorVendor",
                "world.town.npc.AdminMiscVendor"
            };
            string[] displayTypes = {
                "world.town.npc.VendorWeapon1",
                "world.town.npc.VendorWeapon2",
                "world.town.npc.VendorWeapon3"
            };
            int baseFixedX = conn.PlayerPosFixedX + 3 * UnitMover.Fixed;
            int baseFixedY = conn.PlayerPosFixedY;
            int baseFixedZ = conn.PlayerPosFixedZ;
            var shopNPCs = new List<ZoneNPC>();
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            for (int n = 0; n < adminMerchants.Length; n++)
            {
                string gcClass = adminMerchants[n];
                string name = gcClass.Split('.')[gcClass.Split('.').Length - 1];
                int npcFixedX = baseFixedX + n * 40 * UnitMover.Fixed;
                int npcFixedY = baseFixedY;
                ushort npcId = (ushort)AllocateGeneralEntityId();
                ushort behaviorId = (ushort)AllocateGeneralEntityId();
                ushort skillsId = (ushort)AllocateGeneralEntityId();
                ushort manipulatorsId = (ushort)AllocateGeneralEntityId();
                ushort modifiersId = (ushort)AllocateGeneralEntityId();
                ushort merchantId = (ushort)AllocateGeneralEntityId();
                uint npcHPWire = ResolveAuthoredUnitMaxHealthWire(displayTypes[n]);
                var npc = new ZoneNPC
                {
                    Id = npcId,
                    UnitBehaviorId = behaviorId,
                    GCClass = displayTypes[n],

                    Name = name,
                    PosFixedX = npcFixedX,
                    PosFixedY = npcFixedY,
                    PosFixedZ = baseFixedZ,
                    HeadingFixed = 0,
                    HasFixedPosition = true,
                    IsMerchant = true,
                    MerchantId = merchantId,
                    IsAdminMerchant = true,
                    IsTrainer = false,
                    TrainerId = 0
                };
                if (!_zoneNPCs.ContainsKey(conn.CurrentZoneId))
                    _zoneNPCs[conn.CurrentZoneId] = new List<ZoneNPC>();
                _zoneNPCs[conn.CurrentZoneId].Add(npc);
                shopNPCs.Add(npc);
                writer.WriteByte(0x01);
                writer.WriteUInt16(npcId);
                WriteGCType(writer, displayTypes[n], preserveCase: true);
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(behaviorId);
                WriteGCType(writer, "npc.base.behavior", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteByte(0xFF); writer.WriteByte(0x00); writer.WriteByte(0x00); writer.WriteByte(0x01);
                writer.WriteByte(0x85); writer.WriteByte(0x00);
                for (int zeroIndex = 0; zeroIndex < 5; zeroIndex++) writer.WriteUInt32(0);
                writer.WriteByte(0x00);
                writer.WriteByte(0xFF); writer.WriteByte(0x00); writer.WriteByte(0x00);
                writer.WriteByte(0x00); writer.WriteByte(0x00);
                writer.WriteUInt32(0); writer.WriteUInt32(0);
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(skillsId);
                WriteGCType(writer, "skills", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteByte(0xFF); writer.WriteByte(0xFF); writer.WriteByte(0xFF); writer.WriteByte(0xFF);
                writer.WriteByte(0x00); writer.WriteByte(0x01);
                WriteGCType(writer, "skills.professions.Warrior", preserveCase: true);
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(manipulatorsId);
                WriteGCType(writer, "manipulators", preserveCase: false);
                writer.WriteByte(0x01); writer.WriteByte(0x00);
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(modifiersId);
                WriteGCType(writer, "modifiers", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteUInt32(0); writer.WriteByte(0x00); writer.WriteUInt32(0);
                MerchantRuntime.WriteMerchantComponent(writer, displayTypes[n], npcId, merchantId);
                writer.WriteByte(0x02);
                writer.WriteUInt16(npcId);
                writer.WriteUInt32(0x06);
                writer.WriteInt32(npcFixedX);
                writer.WriteInt32(npcFixedY);
                writer.WriteInt32(baseFixedZ);
                writer.WriteInt32(0);
                writer.WriteByte(0x00); writer.WriteByte(0x01);
                for (int zeroIndex = 0; zeroIndex < 8; zeroIndex++) writer.WriteUInt32(0);
                writer.WriteByte(0x35);
                writer.WriteUInt16(behaviorId);
                writer.WriteByte(0x04); writer.WriteByte(0x11); writer.WriteByte(0x00);
                writer.WriteInt32(npcFixedX);
                writer.WriteInt32(npcFixedY);
                writer.WriteInt32(baseFixedZ);
                writer.WriteByte(0x02);
                writer.WriteUInt32(npcHPWire);
            }
            writer.WriteByte(0x06);
            byte[] adminPacket = writer.ToArray();
            Debug.LogError($"[ADMIN-SHOP] packetBytes={adminPacket.Length}");
            SendToClient(conn, adminPacket);
            _adminShopNPCs[conn.ConnId] = shopNPCs;
            sendMessage(conn, "[SHOP] vendors=3 state=spawned action=@shop close");
        }

        public void DespawnAdminShop(RRConnection conn, Action<RRConnection, string> sendMessage)
        {
            if (!_adminShopNPCs.TryGetValue(conn.ConnId, out var npcs) || npcs.Count == 0)
                return;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            foreach (var npc in npcs)
            {
                writer.WriteByte(0x05);
                writer.WriteUInt16((ushort)npc.Id);
                if (TryGetZoneNpcsForConnection(conn, out var zoneList))
                    zoneList.Remove(npc);
            }
            writer.WriteByte(0x06);
            SendToClient(conn, writer.ToArray());
            _adminShopNPCs.Remove(conn.ConnId);
            sendMessage(conn, "[SHOP] state=despawned");
        }
        private void SendGroupRemoveUser(RRConnection conn)
        {
            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group == null) return;

            uint charSqlId = GetCharSqlId(conn);
            byte[] removePacket = GroupPackets.BuildProcessRemoveUser(group.GroupId, charSqlId);

            foreach (var member in group.Members)
            {
                if (member.ConnId == conn.ConnId) continue;
                var memberConn = FindConnectionById(member.ConnId);
                if (memberConn != null)
                    SendToClient(memberConn, removePacket);
            }
            Debug.LogError($"[GROUP] Sent processRemoveUser for {conn.LoginName}");
        }

    }

    public class ActiveModifier
    {
        public string GCType;
        public uint Id;
        public byte Level;
        public uint PowerLevel;
        public uint Duration;
        public byte SourceIsSelf;
        public uint AddedSimulationTick;
    }

    public class ActiveModifiers
    {
        private const uint FirstDynamicModifierId = 4;
        private const uint FirstPassiveModifierId = 0x0000F000;
        private const uint LastPassiveModifierId = 0x0000FFFF;
        private const uint FirstAdminModifierId = 90000;
        private const uint LastAdminModifierId = 0x000FFFFF;
        private const uint LastDynamicModifierId = 0x0FFFFFFF;
        private readonly Dictionary<string, List<ActiveModifier>> _playerModifiers
            = new Dictionary<string, List<ActiveModifier>>(StringComparer.OrdinalIgnoreCase);

        private uint _nextModifierId = FirstDynamicModifierId;

        public bool TryNextId(out uint modifierId)
        {
            modifierId = 0;
            if (_nextModifierId == FirstPassiveModifierId)
                _nextModifierId = LastPassiveModifierId + 1u;
            if (_nextModifierId == FirstAdminModifierId)
                _nextModifierId = LastAdminModifierId + 1u;
            if (_nextModifierId < FirstDynamicModifierId || _nextModifierId > LastDynamicModifierId)
                return false;
            modifierId = _nextModifierId++;
            return true;
        }

        public void AddOrReplace(string loginName, ActiveModifier mod)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list))
            {
                list = new List<ActiveModifier>();
                _playerModifiers[loginName] = list;
            }
            list.RemoveAll(m => string.Equals(m.GCType, mod.GCType, StringComparison.OrdinalIgnoreCase));
            list.Add(mod);
            Debug.LogError($"[ACTIVE-MODIFIERS] Tracked '{mod.GCType}' id={mod.Id} for {loginName} (total={list.Count})");
        }

        public bool Remove(string loginName, string gcType)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list)) return false;
            int removed = list.RemoveAll(m => string.Equals(m.GCType, gcType, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                Debug.LogError($"[ACTIVE-MODIFIERS] Removed '{gcType}' from {loginName} (remaining={list.Count})");
            return removed > 0;
        }

        public bool RemoveById(string loginName, uint modId)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list)) return false;
            int removed = list.RemoveAll(m => m.Id == modId);
            if (removed > 0)
                Debug.LogError($"[ACTIVE-MODIFIERS] Removed modId={modId} from {loginName} (remaining={list.Count})");
            return removed > 0;
        }

        public List<ActiveModifier> ListFor(string loginName, uint currentSimulationTick)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list))
                return new List<ActiveModifier>();

            var result = new List<ActiveModifier>();
            foreach (var mod in list)
            {
                if (mod.Duration == 0)
                {
                    result.Add(mod);
                }
                else
                {
                    uint elapsedSimulationTicks = currentSimulationTick >= mod.AddedSimulationTick
                        ? currentSimulationTick - mod.AddedSimulationTick
                        : 0;
                    if (elapsedSimulationTicks < mod.Duration)
                    {
                        result.Add(new ActiveModifier
                        {
                            GCType = mod.GCType,
                            Id = mod.Id,
                            Level = mod.Level,
                            PowerLevel = mod.PowerLevel,
                            Duration = mod.Duration - elapsedSimulationTicks,
                            SourceIsSelf = mod.SourceIsSelf,
                            AddedSimulationTick = mod.AddedSimulationTick
                        });
                    }
                }
            }
            list.RemoveAll(m =>
            {
                if (m.Duration == 0)
                    return false;
                uint elapsedSimulationTicks = currentSimulationTick >= m.AddedSimulationTick
                    ? currentSimulationTick - m.AddedSimulationTick
                    : 0;
                return elapsedSimulationTicks >= m.Duration;
            });
            return result;
        }

        public void ReconcileRuntimeDurations(string loginName, IReadOnlyList<PlayerState.AttributeModifierSnapshot> snapshots, uint currentSimulationTick)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list) || snapshots == null)
                return;
            foreach (ActiveModifier modifier in list)
            {
                if (modifier == null || modifier.Duration == 0 || string.IsNullOrWhiteSpace(modifier.GCType))
                    continue;
                PlayerState.AttributeModifierSnapshot match = null;
                int matches = 0;
                for (int index = 0; index < snapshots.Count; index++)
                {
                    PlayerState.AttributeModifierSnapshot snapshot = snapshots[index];
                    if (snapshot == null || !string.Equals(snapshot.ModifierType, modifier.GCType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    match = snapshot;
                    matches++;
                }
                if (matches != 1 || match == null || match.RemainingTicks == 0)
                    continue;
                modifier.Duration = match.RemainingTicks;
                modifier.AddedSimulationTick = currentSimulationTick;
            }
        }

        public void Clear(string loginName)
        {
            _playerModifiers.Remove(loginName);
        }

    }


}
