using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Networking;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Combat;
namespace DungeonRunners.Gameplay

{
    public static class MerchantRuntime
    {
        private static Dictionary<string, MerchantData> _merchants = new Dictionary<string, MerchantData>(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, MerchantRuntimeData> _runtimeMerchants = new Dictionary<string, MerchantRuntimeData>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<PendingMerchantRefreshAdd> _pendingMerchantRefreshAdds = new List<PendingMerchantRefreshAdd>();
        private static readonly object _merchantRandomLock = new object();
        private static readonly System.Random _merchantRandom = new System.Random();
        private static bool VerboseMerchantItemLogging => ServerDiagnostics.IsEnabled("verboseMerchantItemLogging");

        private static List<SellableItem> _sellableItems = new List<SellableItem>();

        public static readonly Dictionary<string, uint> _buyPrices = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        public static void SetBuyPrice(string connId, string gcClass, uint price)
        {
            string key = connId + ":" + gcClass.ToLowerInvariant();
            _buyPrices[key] = price;
        }

        public static uint GetBuyPrice(string connId, string gcClass)
        {
            string key = connId + ":" + gcClass.ToLowerInvariant();
            return _buyPrices.TryGetValue(key, out uint p) ? p : 0;
        }
        private static Dictionary<string, ItemDimensions> _itemDimensions = new Dictionary<string, ItemDimensions>(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized = false;

        private const ushort MERCHANT_REFRESH_TIMER_TICKS = 0x2328;
        private const ushort MERCHANT_REFRESH_EVENT_ID = 0x000F;
        private const ushort MERCHANT_REFRESH_SEND_DELAY_TICKS = 0;

        public static void Initialize()
        {
            if (_initialized) return;

            Debug.LogError("[MERCHANT-RUNTIME] init");

            LoadItemDimensions();
            LogAuthoredModSlotCoverage();
            LoadSellableItems();
            LoadMerchants();
            InitializeRuntimeMerchants();

            _initialized = true;
        }

        public static void ValidateAllSellableItems()
        {
            Debug.LogError("[MERCHANT-RUNTIME] validateSellableItems start");
            int bad = 0;
            int notInDb = 0;
            foreach (var item in _sellableItems)
            {
                string lookup = item.gcType.ToLowerInvariant();
                if (lookup.StartsWith("items.pal."))
                    lookup = lookup.Substring(10);

                var data = AuthoredGameplayCatalog.FindItem(lookup);
                if (data == null)
                {
                    Debug.LogError($"[MERCHANT-VALIDATE] item={item.gcType} lookup={lookup} reason=missing-db");
                    notInDb++;
                    bad++;
                    continue;
                }

                if (data.modCount < 0 || data.modCount > 10)
                {
                    Debug.LogError($"[MERCHANT-VALIDATE] item={item.gcType} modCount={data.modCount} reason=mod-count");
                    bad++;
                }

                if (data.inventoryWidth <= 0 || data.inventoryHeight <= 0)
                {
                    Debug.LogError($"[MERCHANT-VALIDATE] item={item.gcType} dimensions={data.inventoryWidth}x{data.inventoryHeight} reason=dimensions");
                    bad++;
                }

                string lower = item.gcType.ToLowerInvariant();
                if (lower.Contains("shield") || lower.Contains("buckler") || lower.Contains("shoulder") || lower.Contains("pauldron"))
                {
                    Debug.LogError($"[MERCHANT-VALIDATE] item={item.gcType} modCount={data.modCount} dimensions={data.inventoryWidth}x{data.inventoryHeight} slot={data.slotType ?? "NULL"} class=shield-shoulder");
                }
            }
            Debug.LogError($"[MERCHANT-RUNTIME] validateSellableItems items={_sellableItems.Count} problems={bad} missingDb={notInDb}");
        }



        private static void LoadSellableItems()
        {
            if (!AuthoredGameplayCatalog.IsLoaded)
                throw new InvalidOperationException("Authored gameplay catalog must be loaded before merchant items");
            _sellableItems = new List<SellableItem>();
            foreach (GeneralItemData item in AuthoredGameplayCatalog.Items)
            {
                int authoredGoldValueF32 = item.GoldValueF32 > 0 ? item.GoldValueF32 : item.GcGoldValueF32;
                if (authoredGoldValueF32 <= 0)
                    continue;
                float authoredGoldValue = authoredGoldValueF32 / 256f;
                _sellableItems.Add(new SellableItem
                {
                    gcType = GCObject.GetPacketGCClassFor(item.gcType),
                    name = item.Label,
                    goldValue = authoredGoldValue,
                    gcGoldValue = authoredGoldValue
                });
            }
            Debug.LogError($"[MERCHANT-RUNTIME] sellableItems={_sellableItems.Count} source=authored-gameplay-catalog");
        }

        private static void LoadItemDimensions()
        {
            if (!AuthoredGameplayCatalog.IsLoaded)
                throw new InvalidOperationException("Authored gameplay catalog must be loaded before item dimensions");
            _itemDimensions.Clear();
            foreach (GeneralItemData item in AuthoredGameplayCatalog.Items)
            {
                if (string.IsNullOrWhiteSpace(item.gcType) || item.InventoryWidth <= 0 || item.InventoryHeight <= 0)
                    throw new InvalidDataException($"Invalid authored item dimensions gc_type='{item.gcType}' width={item.InventoryWidth} height={item.InventoryHeight}");
                _itemDimensions.Add(item.gcType, new ItemDimensions
                {
                    width = item.InventoryWidth,
                    height = item.InventoryHeight
                });
                string packetType = GCObject.GetPacketGCClassFor(item.gcType);
                if (!string.Equals(packetType, item.gcType, StringComparison.OrdinalIgnoreCase))
                    _itemDimensions.TryAdd(packetType, new ItemDimensions
                    {
                        width = item.InventoryWidth,
                        height = item.InventoryHeight
                    });
            }
            Debug.LogError($"[MERCHANT-RUNTIME] itemDimensions={_itemDimensions.Count} source=authored-gameplay-catalog");
        }

        private static void LoadMerchants()
        {
            _merchants.Clear();

            if (AuthoredGameplayCatalog.Merchants == null || AuthoredGameplayCatalog.Merchants.Count == 0)
            {
                Debug.LogError("[MERCHANT-RUNTIME] merchants=0 source=pki-pkg-graph");
                return;
            }

            foreach (var merchantData in AuthoredGameplayCatalog.Merchants)
            {
                _merchants[merchantData.npcGcType] = merchantData;
                Debug.LogError($"[MERCHANT-RUNTIME] merchant={merchantData.npcGcType} loaded=True");

                foreach (var inv in merchantData.inventories)
                {
                    string invType = inv.staticContents ? "STATIC" : $"DYNAMIC (timer={MERCHANT_REFRESH_TIMER_TICKS})";
                    Debug.LogError($"[MERCHANT-RUNTIME] inventory='{inv.name}' id={inv.id} type={invType} items={inv.items.Count}");
                }
            }

            Debug.LogError($"[MERCHANT-RUNTIME] merchants={_merchants.Count}");
        }

        private static void InitializeRuntimeMerchants()
        {
            _runtimeMerchants.Clear();

            foreach (var merchantEntry in _merchants)
            {
                var merchantData = merchantEntry.Value;
                var runtimeMerchant = CreateRuntimeMerchant(merchantData);
                _runtimeMerchants[merchantEntry.Key] = runtimeMerchant;
            }

            Debug.LogError($"[MERCHANT-RUNTIME] runtimeMerchants={_runtimeMerchants.Count}");
        }

        private static MerchantRuntimeData CreateRuntimeMerchant(MerchantData merchantData)
        {
            var runtime = new MerchantRuntimeData
            {
                npcGcType = merchantData.npcGcType,
                merchantGcType = merchantData.merchantGcType,
                refreshDueTick = unchecked(SimulationClock.SimulationTick + MERCHANT_REFRESH_TIMER_TICKS),
                refreshTimerInitialized = true,
                inventories = new List<MerchantInventoryRuntimeData>()
            };
            int nextAttachedItemId = 0xFF;
            runtime.nextItemId = checked(nextAttachedItemId + merchantData.inventories.Sum(inventory => inventory.items.Count));
            foreach (var invData in merchantData.inventories)
            {
                int nextInventoryItemId = nextAttachedItemId;
                nextAttachedItemId = checked(nextAttachedItemId + invData.items.Count);
                var runtimeInv = new MerchantInventoryRuntimeData
                {
                    name = invData.name,
                    gcType = invData.gcType,
                    authoredOrdinal = invData.authoredOrdinal,
                    authoredItemCount = invData.items.Count,
                    id = invData.id,
                    label = invData.label,
                    width = invData.width,
                    height = invData.height,
                    staticContents = invData.staticContents,
                    autoGenerateItems = invData.autoGenerateItems,
                    itemGenerator = invData.itemGenerator,
                    minItemLevel = invData.minItemLevel,
                    maxItemLevel = invData.maxItemLevel,
                    items = new List<MerchantItemRuntimeData>()
                };
                if (invData.staticContents)
                {
                    foreach (var item in invData.items)
                    {
                        int attachedItemId = nextInventoryItemId++;
                        var itemRarity = RPGSettings.ResolveItemRarity(item.gcType);

                        if (RPGSettings.IsMythicPALItem(item.gcType) || IsEnabledMythicItem(item.gcType))
                            itemRarity = ItemRarity.Mythic;

                        int level = RPGSettings.GetItemLevel(item.gcType);
                        bool hasResolvedPrice = true;
                        if (!RPGSettings.TryGetBaseGoldValue(item.gcType, out float baseGoldValue))
                        {
                            if (item.gcType.IndexOf("questitem", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                baseGoldValue = 1f;
                                Debug.LogError($"[MERCHANT-RUNTIME] inventory='{runtimeInv.name}' item={item.gcType} goldValue=1 source=quest-item-default");
                            }
                            else
                            {
                                hasResolvedPrice = false;
                                Debug.LogError($"[MERCHANT-RUNTIME] inventory='{runtimeInv.name}' item={item.gcType} reason=missing-authored-gold-value state=retained");
                            }
                        }
                        if (_sellableItems != null)
                        {
                            string lookupType = item.gcType.ToLowerInvariant();
                            if (lookupType.StartsWith("items.pal."))
                                lookupType = lookupType.Substring(10);
                            var sellable = _sellableItems.FirstOrDefault(s =>
                                string.Equals(s.gcType, lookupType, StringComparison.OrdinalIgnoreCase) ||
                                s.gcType.EndsWith(lookupType, StringComparison.OrdinalIgnoreCase));
                            if (sellable != null && sellable.gcGoldValue > 0)
                                baseGoldValue = sellable.gcGoldValue;
                        }

                        uint itemPrice = hasResolvedPrice ? RPGSettings.CalculateBuyPrice(level, itemRarity, baseGoldValue) : 0;

                        runtimeInv.items.Add(new MerchantItemRuntimeData
                        {
                            gcType = item.gcType,
                            inventoryX = item.inventoryX,
                            inventoryY = item.inventoryY,
                            id = attachedItemId,
                            quantity = item.quantity > 0 ? item.quantity : 1,
                            level = level,
                            rarity = itemRarity,
                            price = itemPrice,
                            hasResolvedPrice = hasResolvedPrice,
                            goldValue = baseGoldValue
                        });
                    }
                }
                else if (invData.autoGenerateItems)
                {
                    GenerateInventoryItems(runtime, runtimeInv, invData);
                }
                runtime.inventories.Add(runtimeInv);
            }
            return runtime;
        }



        public static int GetTicksUntilRegeneration(string npcGcType)
        {
            if (!_runtimeMerchants.TryGetValue(npcGcType, out var runtimeMerchant))
                return 0;
            if (!runtimeMerchant.refreshTimerInitialized)
                return 0;
            uint currentTick = SimulationClock.SimulationTick;
            int remainingTicks = unchecked((int)(runtimeMerchant.refreshDueTick - currentTick));
            return remainingTicks > 0 ? remainingTicks : 0;
        }

        private static MerchantInventoryData RequireAuthoredInventory(MerchantData merchant, MerchantInventoryRuntimeData runtimeInventory)
        {
            if (merchant == null)
                throw new ArgumentNullException(nameof(merchant));
            if (runtimeInventory == null)
                throw new ArgumentNullException(nameof(runtimeInventory));
            if (runtimeInventory.authoredOrdinal < 0 || runtimeInventory.authoredOrdinal >= merchant.inventories.Count)
                throw new InvalidDataException($"Merchant inventory ordinal is invalid merchant='{merchant.npcGcType}' ordinal={runtimeInventory.authoredOrdinal}");
            MerchantInventoryData authoredInventory = merchant.inventories[runtimeInventory.authoredOrdinal];
            if (authoredInventory.authoredOrdinal != runtimeInventory.authoredOrdinal
                || authoredInventory.id != runtimeInventory.id
                || !string.Equals(authoredInventory.gcType, runtimeInventory.gcType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Merchant inventory identity mismatch merchant='{merchant.npcGcType}' ordinal={runtimeInventory.authoredOrdinal} runtimeId={runtimeInventory.id} authoredId={authoredInventory.id} runtimeType='{runtimeInventory.gcType}' authoredType='{authoredInventory.gcType}'");
            return authoredInventory;
        }


        public static (int width, int height) GetItemDimensions(string gcType)
        {
            if (string.IsNullOrWhiteSpace(gcType))
                return (0, 0);

            string lookupType = gcType.Trim();
            string packetType = GCObject.GetPacketGCClassFor(lookupType);
            if (!packetType.StartsWith("items.", StringComparison.OrdinalIgnoreCase))
                packetType = "items.pal." + packetType;

            if (_itemDimensions.TryGetValue(packetType, out ItemDimensions authoredDimensions) &&
                authoredDimensions.width > 0 && authoredDimensions.height > 0)
                return (authoredDimensions.width, authoredDimensions.height);

            if (_itemDimensions.TryGetValue(lookupType, out authoredDimensions) &&
                authoredDimensions.width > 0 && authoredDimensions.height > 0)
                return (authoredDimensions.width, authoredDimensions.height);

            string lookupKey = lookupType;
            if (lookupKey.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                lookupKey = lookupKey.Substring(10);
            else if (lookupKey.StartsWith("items.consumables.", StringComparison.OrdinalIgnoreCase))
                lookupKey = lookupKey.Substring(18);

            var itemData = AuthoredGameplayCatalog.FindGeneralItem(lookupKey);
            if (itemData != null && itemData.InventoryWidth > 0 && itemData.InventoryHeight > 0)
            {
                return (itemData.InventoryWidth, itemData.InventoryHeight);
            }

            Debug.LogError($"[MERCHANT-DIMS] item={gcType} reason=missing-authored-dimensions packetKey={packetType} lookupKey={lookupKey} dbFound={itemData != null}");
            return (0, 0);
        }

        private static (int x, int y) FindSpot(bool[,] occupied, int gridWidth, int gridHeight, int itemWidth, int itemHeight)
        {
            for (int y = 0; y <= gridHeight - itemHeight; y++)
            {
                for (int x = 0; x <= gridWidth - itemWidth; x++)
                {
                    bool canPlace = true;
                    for (int dx = 0; dx < itemWidth && canPlace; dx++)
                    {
                        for (int dy = 0; dy < itemHeight && canPlace; dy++)
                        {
                            if (occupied[x + dx, y + dy])
                                canPlace = false;
                        }
                    }
                    if (canPlace)
                        return (x, y);
                }
            }
            return (-1, -1);
        }

        public static int GetOP5ModCount(string gcType)
        {
            if (!TryGetAuthoredMerchantModSlots(gcType, out int merchantModSlots))
                return -1;

            int op5ModCount = merchantModSlots - 1;
            return op5ModCount < 0 ? 0 : op5ModCount;
        }

        public static bool HasAuthoredMerchantModSlots(string gcType)
        {
            return TryGetAuthoredMerchantModSlots(gcType, out _);
        }

        public static bool TryGetAuthoredMerchantModSlots(string gcType, out int merchantModSlots)
        {
            merchantModSlots = 0;
            string key = NormalizeAuthoredModSlotKey(gcType);
            if (string.IsNullOrEmpty(key))
                return false;

            if (!IsSpecialAuthoredSlotCandidate(key))
                return false;

            if (DungeonRunners.Data.ItemStatDatabase.Instance.TryGetItemReadDataSlotCount(key, out int authoredSlots))
            {
                merchantModSlots = authoredSlots;
                return true;
            }

            return false;
        }

        private static string NormalizeAuthoredModSlotKey(string gcType)
        {
            if (string.IsNullOrWhiteSpace(gcType))
                return "";
            string key = gcType.Replace('\\', '.').Replace('/', '.').Trim().ToLowerInvariant();
            if (key.StartsWith("items.pal."))
                key = key.Substring("items.pal.".Length);
            return key;
        }

        private static bool IsSpecialAuthoredSlotCandidate(string key)
        {
            return key.IndexOf("mythic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.IndexOf("prebuilt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.IndexOf("partialbuilt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.IndexOf("generated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.IndexOf("seasonal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.IndexOf("wishingwell", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LogAuthoredModSlotCoverage()
        {
            var keys = LoadAuthoredModSlotAuditKeys();
            int authored = 0;
            var misses = new List<string>();

            foreach (string key in keys)
            {
                if (DungeonRunners.Data.ItemStatDatabase.Instance.TryGetItemReadDataSlotCount(key, out _))
                {
                    authored++;
                }
                else if (misses.Count < 8)
                {
                    misses.Add(key);
                }
            }

            string samples = misses.Count == 0 ? "" : $" samples=[{string.Join(", ", misses)}]";
            Debug.LogError($"[MERCHANT-RUNTIME] authoredModSlots source=GC-DATABASE+PACKAGE-CATALOG special={authored}/{keys.Count} missing={keys.Count - authored}{samples}");
        }

        private static SortedSet<string> LoadAuthoredModSlotAuditKeys()
        {
            var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GeneralItemData item in AuthoredGameplayCatalog.Items)
            {
                string key = NormalizeAuthoredModSlotKey(item.gcType);
                if (IsSpecialAuthoredSlotCandidate(key))
                    keys.Add(key);
            }
            return keys;
        }


        private static readonly bool DISABLE_MYTHICPAL = false;
        private static readonly bool ENABLE_1H_WEAPON_PREBUILT = true;
        private static readonly bool ENABLE_FIGHTER_CLASS_ARMOR = true;
        private static readonly bool ENABLE_MAGE_CLASS_ARMOR = true;
        private static readonly bool ENABLE_RANGER_CLASS_ARMOR = true;

        private static bool IsEnabledMythicItem(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            string lower = gcType.ToLowerInvariant();
            if (lower.StartsWith("items.pal.")) lower = lower.Substring(10);
            if (lower.Contains("mythicpal")) return !DISABLE_MYTHICPAL;
            if (!HasAuthoredMerchantModSlots(lower)) return false;
            string pal = lower.Split('.')[0];
            if (ENABLE_1H_WEAPON_PREBUILT && (pal == "1haxepal" || pal == "1hmacepal" || pal == "1hpickpal" || pal == "1hstaffpal" || pal == "1hswordpal"))
                return true;
            if (ENABLE_FIGHTER_CLASS_ARMOR && pal.StartsWith("fighter"))
                return true;
            if (ENABLE_MAGE_CLASS_ARMOR && pal.StartsWith("mage"))
                return true;
            if (ENABLE_RANGER_CLASS_ARMOR && pal.StartsWith("ranger"))
                return true;
            return false;
        }

        private static bool IsWeaponType(string gcType)
        {
            string lower = gcType.ToLowerInvariant();
            return lower.Contains("axe") || lower.Contains("sword") ||
                   lower.Contains("mace") || lower.Contains("pick") ||
                   lower.Contains("staff") || lower.Contains("crossbow") ||
                   lower.Contains("gun") || lower.Contains("cannon");
        }

        private static bool IsArmorType(string gcType)
        {
            string lower = gcType.ToLowerInvariant();
            return lower.Contains("armor") || lower.Contains("boot") ||
                   lower.Contains("glove") || lower.Contains("helm") ||
                   lower.Contains("shoulder") || lower.Contains("pauldron") ||
                   lower.Contains("shield") || lower.Contains("buckler") ||
                   lower.Contains("body") || lower.Contains("chest");
        }

        private static bool IsJewelryType(string gcType)
        {
            string lower = gcType.ToLowerInvariant();
            return lower.Contains("ring") || lower.Contains("amulet");
        }

        private static int RollMerchantStockLevel(MerchantInventoryRuntimeData runtimeInv, System.Random random)
        {
            int minLevel = runtimeInv.minItemLevel > 0 ? runtimeInv.minItemLevel : 1;
            int maxLevel = runtimeInv.maxItemLevel > 0 ? runtimeInv.maxItemLevel : minLevel;
            if (maxLevel < minLevel)
                maxLevel = minLevel;
            if (maxLevel == minLevel)
                return minLevel;
            return random.Next(minLevel, maxLevel + 1);
        }

        private static void GenerateInventoryItems(MerchantRuntimeData merchant, MerchantInventoryRuntimeData runtimeInv, MerchantInventoryData invData)
        {
            runtimeInv.items.Clear();

            var safeItems = _sellableItems.Where(sellableItem =>
                (System.Text.RegularExpressions.Regex.IsMatch(sellableItem.gcType, @"-\d+$") || IsEnabledMythicItem(sellableItem.gcType)) &&
                sellableItem.gcType.IndexOf("PreBuilt", StringComparison.OrdinalIgnoreCase) < 0 &&
                sellableItem.gcType.IndexOf("PartialBuilt", StringComparison.OrdinalIgnoreCase) < 0 &&
                sellableItem.gcType.IndexOf("Seasonal", StringComparison.OrdinalIgnoreCase) < 0 &&
                sellableItem.gcType.IndexOf("WishingWell", StringComparison.OrdinalIgnoreCase) < 0 &&
                sellableItem.gcType.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) < 0 &&
                sellableItem.gcType.IndexOf("Generated", StringComparison.OrdinalIgnoreCase) < 0 &&
                !sellableItem.gcType.EndsWith(".Visual", StringComparison.OrdinalIgnoreCase) &&
                !sellableItem.gcType.Contains("test", StringComparison.OrdinalIgnoreCase) &&
                (IsWeaponType(sellableItem.gcType) || IsArmorType(sellableItem.gcType))
            );

            int authoredMinItemLevel = runtimeInv.minItemLevel > 0 ? runtimeInv.minItemLevel : 1;
            int authoredMaxItemLevel = runtimeInv.maxItemLevel > 0 ? runtimeInv.maxItemLevel : authoredMinItemLevel;
            if (authoredMaxItemLevel < authoredMinItemLevel)
                authoredMaxItemLevel = authoredMinItemLevel;
            safeItems = safeItems.Where(sellableItem =>
            {
                int itemLevel = RPGSettings.GetItemLevel(sellableItem.gcType);
                return itemLevel <= authoredMaxItemLevel;
            });

            string itemGeneratorType = runtimeInv.itemGenerator ?? invData.itemGenerator ?? "";
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-RUNTIME] itemGeneratorFilter inv='{runtimeInv.name}' itemGenerator='{itemGeneratorType}'");
            if (!string.IsNullOrWhiteSpace(itemGeneratorType))
            {
                GenerateAuthoredInventoryItems(merchant, runtimeInv, itemGeneratorType);
                return;
            }
            System.Func<string, bool> isMythicItem = (gcType) => IsEnabledMythicItem(gcType);
            var random = CreateMerchantRandom();

            if (itemGeneratorType.Equals("MerchantWeaponIG", StringComparison.OrdinalIgnoreCase))
            {
                safeItems = safeItems.Where(sellableItem =>
                {
                    if (isMythicItem(sellableItem.gcType)) return false;
                    if (!IsWeaponType(sellableItem.gcType)) return false;
                    int suffix = RPGSettings.GetTierFromGcType(sellableItem.gcType);
                    if (suffix == 4) return true;
                    if (suffix == 5) return random.Next(20) == 0;
                    return false;
                });
            }
            else if (itemGeneratorType.Equals("MerchantArmorIG", StringComparison.OrdinalIgnoreCase))
            {
                safeItems = safeItems.Where(sellableItem =>
                {
                    if (isMythicItem(sellableItem.gcType)) return false;
                    if (!IsArmorType(sellableItem.gcType)) return false;
                    int suffix = RPGSettings.GetTierFromGcType(sellableItem.gcType);
                    if (suffix == 4) return true;
                    if (suffix == 5) return random.Next(20) == 0;
                    return false;
                });
            }
            else if (itemGeneratorType.Equals("MerchantTrashIG", StringComparison.OrdinalIgnoreCase))
            {
                safeItems = safeItems.Where(sellableItem =>
                {
                    if (isMythicItem(sellableItem.gcType)) return false;
                    int suffix = RPGSettings.GetTierFromGcType(sellableItem.gcType);
                    return suffix >= 2 && suffix <= 3;
                });
            }
            else if (itemGeneratorType.Equals("MerchantSuperiorIG", StringComparison.OrdinalIgnoreCase))
            {
                safeItems = safeItems.Where(sellableItem =>
                {
                    if (isMythicItem(sellableItem.gcType)) return false;
                    int suffix = RPGSettings.GetTierFromGcType(sellableItem.gcType);
                    return suffix == 2;
                });
            }
            else if (itemGeneratorType.Equals("MerchantSpecialEvent01IG", StringComparison.OrdinalIgnoreCase))
            {
                safeItems = safeItems.Where(sellableItem =>
                {
                    if (isMythicItem(sellableItem.gcType))
                    {
                        string mythicKey = sellableItem.gcType.ToLowerInvariant();
                        if (mythicKey.StartsWith("items.pal.")) mythicKey = mythicKey.Substring(10);
                        if (!TryGetAuthoredMerchantModSlots(mythicKey, out int mythicSlots)) return false;
                        if (mythicSlots < 3 && DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(mythicKey).Count == 0)
                            return false;
                        return random.Next(10) == 0;
                    }
                    int suffix = RPGSettings.GetTierFromGcType(sellableItem.gcType);
                    if (suffix == 4) return true;
                    if (suffix == 5) return random.Next(4) == 0;
                    return false;
                });
            }

            safeItems = safeItems.Where(sellableItem =>
            {
                if (!IsEnabledMythicItem(sellableItem.gcType)) return true;
                if (sellableItem.gcType.IndexOf("MythicPAL", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string key = sellableItem.gcType.ToLowerInvariant();
                if (key.StartsWith("items.pal.")) key = key.Substring(10);
                if (!TryGetAuthoredMerchantModSlots(key, out int slots)) return false;
                if (slots <= 0) return false;
                return true;
            });

            var safeList = safeItems.ToList();
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-DETAIL] candidates inv='{runtimeInv.name}' itemGenerator={itemGeneratorType} count={safeList.Count} maxLvl={authoredMaxItemLevel}");
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-DETAIL] filtered inv='{runtimeInv.name}' itemGenerator={itemGeneratorType} count={safeList.Count}");
            foreach (var dbgItem in safeList)
            {
                if (IsEnabledMythicItem(dbgItem.gcType))
                {
                    string mythicKey = dbgItem.gcType.ToLowerInvariant();
                    if (mythicKey.StartsWith("items.pal.")) mythicKey = mythicKey.Substring(10);
                    int authoredSlots = TryGetAuthoredMerchantModSlots(mythicKey, out int modSlots) ? modSlots : -1;
                    bool isWeapon = IsWeaponType(dbgItem.gcType);
                    bool isArmor = IsArmorType(dbgItem.gcType);
                    if (VerboseMerchantItemLogging)
                        Debug.LogError($"[MERCHANT-MYTHIC] tab='{runtimeInv.name}' itemGenerator='{itemGeneratorType}' item={dbgItem.gcType} weapon={isWeapon} armor={isArmor} modSlots={authoredSlots}");
                }
            }
            if (safeList.Count == 0) return;
            var available = safeList.OrderBy(x => random.Next()).ToList();
            bool[,] grid = new bool[invData.width, invData.height];
            foreach (var itemData in available)
            {
                var (w, h) = GetItemDimensions(itemData.gcType);
                if (w <= 0 || h <= 0)
                {
                    Debug.LogError($"[MERCHANT-GENERATE] item={itemData.gcType} reason=missing-authored-dimensions state=skipped");
                    continue;
                }
                int placeX = -1, placeY = -1;
                for (int x = 0; x <= invData.width - w && placeX < 0; x++)
                {
                    for (int y = 0; y <= invData.height - h && placeX < 0; y++)
                    {
                        bool canPlace = true;
                        for (int dx = 0; dx < w && canPlace; dx++)
                            for (int dy = 0; dy < h && canPlace; dy++)
                                if (grid[x + dx, y + dy]) canPlace = false;
                        if (canPlace) { placeX = x; placeY = y; }
                    }
                }
                if (placeX >= 0)
                {
                    for (int dx = 0; dx < w; dx++)
                        for (int dy = 0; dy < h; dy++)
                            grid[placeX + dx, placeY + dy] = true;

                    bool isMythicGen = IsEnabledMythicItem(itemData.gcType);
                    var itemRarity = RPGSettings.ResolveMerchantInventoryRarity(itemGeneratorType, random, itemData.gcType);

                    if (RPGSettings.IsMythicPALItem(itemData.gcType) || isMythicGen)
                    {
                        Debug.LogError($"[MERCHANT-MYTHIC] item={itemData.gcType} rarity={itemRarity} resolvedRarity=Mythic");
                        itemRarity = ItemRarity.Mythic;
                    }

                    int level;
                    if (isMythicGen)
                    {
                        int rolledLevel = RollMerchantStockLevel(runtimeInv, random);
                        int mythicMin = DungeonRunners.Core.ServerSettings.Get("mythicMinLevel", 15);
                        int mythicMax = DungeonRunners.Core.ServerSettings.Get("mythicMaxLevel", 100);
                        level = Math.Max(mythicMin, Math.Min(mythicMax, rolledLevel));
                    }
                    else
                    {
                        level = RollMerchantStockLevel(runtimeInv, random);
                    }

                    float baseGoldValue = itemData.gcGoldValue > 0 ? itemData.gcGoldValue : RPGSettings.GetBaseGoldValue(itemData.gcType);
                    uint finalPrice = RPGSettings.CalculateBuyPrice(level, itemRarity, baseGoldValue);

                    runtimeInv.items.Add(new MerchantItemRuntimeData
                    {
                        gcType = itemData.gcType,
                        id = merchant.nextItemId++,
                        inventoryX = placeX,
                        inventoryY = placeY,
                        quantity = 1,
                        level = level,
                        rarity = itemRarity,
                        price = finalPrice,
                        goldValue = baseGoldValue,
                        scaleMod = itemRarity == ItemRarity.Mythic ? null : RPGSettings.GetRandomScaleMod(itemRarity)
                    });
                    if (isMythicGen)
                    {
                        string palItemKey = itemData.gcType.ToLowerInvariant();
                        if (palItemKey.StartsWith("items.pal.")) palItemKey = palItemKey.Substring(10);
                        int palModSlots = TryGetAuthoredMerchantModSlots(palItemKey, out int authoredModSlots) ? authoredModSlots : -1;
                        if (VerboseMerchantItemLogging)
                            Debug.LogError($"[MERCHANT-MYTHIC] placed tab='{runtimeInv.name}' item={itemData.gcType} modSlots={palModSlots} pos=({placeX},{placeY})");
                    }
                }
            }
            Debug.LogError($"[MERCHANT-RUNTIME] generated inv='{runtimeInv.name}' items={runtimeInv.items.Count}");
        }

        private static void GenerateAuthoredInventoryItems(MerchantRuntimeData merchant, MerchantInventoryRuntimeData runtimeInv, string itemGeneratorType)
        {
            if (!GCObjectGeneratorTable.Instance.CanResolveAuthoredGenerator(itemGeneratorType))
            {
                Debug.LogError($"[MERCHANT-RUNTIME] inventory='{runtimeInv.name}' generator='{itemGeneratorType}' reason=unresolved-authored-generator state=empty");
                return;
            }

            int gridWidth = runtimeInv.width;
            int gridHeight = runtimeInv.height;
            if (gridWidth <= 0 || gridHeight <= 0)
            {
                Debug.LogError($"[MERCHANT-RUNTIME] inventory='{runtimeInv.name}' generator='{itemGeneratorType}' reason=invalid-grid state=empty");
                return;
            }

            bool[,] occupied = new bool[gridWidth, gridHeight];
            var random = CreateMerchantRandom();
            int placementAttempts = Math.Max(1, checked(gridWidth * gridHeight * 16 + 64));
            for (int attempt = 0; attempt < placementAttempts; attempt++)
            {
                int stockLevel = RollMerchantStockLevel(runtimeInv, random);
                List<LootDrop> drops = GCObjectGeneratorTable.Instance.GenerateAuthoredGeneratorLoot(
                    itemGeneratorType,
                    1,
                    stockLevel,
                    false,
                    $"merchant:{runtimeInv.name}",
                    "avatar.base.Avatar");
                LootDrop drop = drops.FirstOrDefault(candidate => candidate != null && candidate.IsItem && !string.IsNullOrWhiteSpace(candidate.GCType));
                if (drop == null)
                    continue;

                int minStockLevel = runtimeInv.minItemLevel > 0 ? runtimeInv.minItemLevel : 1;
                int maxStockLevel = runtimeInv.maxItemLevel > 0 ? runtimeInv.maxItemLevel : minStockLevel;
                if (maxStockLevel < minStockLevel)
                    maxStockLevel = minStockLevel;
                int itemLevel = drop.ItemLevel > 0 ? drop.ItemLevel : stockLevel;
                itemLevel = Math.Max(minStockLevel, Math.Min(maxStockLevel, itemLevel));
                var dimensions = GetItemDimensions(drop.GCType);
                if (dimensions.width <= 0 || dimensions.height <= 0)
                {
                    Debug.LogError($"[MERCHANT-GENERATE] generator='{itemGeneratorType}' item={drop.GCType} reason=missing-authored-dimensions state=skipped");
                    continue;
                }

                var spot = FindSpot(occupied, gridWidth, gridHeight, dimensions.width, dimensions.height);
                if (spot.x < 0)
                    break;

                if (!RPGSettings.TryGetBaseGoldValue(drop.GCType, out float baseGoldValue))
                {
                    Debug.LogError($"[MERCHANT-GENERATE] generator='{itemGeneratorType}' item={drop.GCType} reason=missing-authored-gold-value state=skipped");
                    continue;
                }

                for (int dx = 0; dx < dimensions.width; dx++)
                    for (int dy = 0; dy < dimensions.height; dy++)
                        occupied[spot.x + dx, spot.y + dy] = true;

                List<string> modifiers = drop.ItemModifiers == null
                    ? new List<string>()
                    : new List<string>(drop.ItemModifiers);
                bool generatedState = drop.HasGeneratedItemState;
                runtimeInv.items.Add(new MerchantItemRuntimeData
                {
                    gcType = drop.GCType,
                    id = merchant.nextItemId++,
                    inventoryX = spot.x,
                    inventoryY = spot.y,
                    quantity = 1,
                    level = itemLevel,
                    rarity = drop.Rarity,
                    price = RPGSettings.CalculateBuyPrice(itemLevel, drop.Rarity, baseGoldValue),
                    goldValue = baseGoldValue,
                    scaleMod = generatedState ? null : drop.ScaleMod,
                    hasGeneratedItemState = generatedState,
                    rolledRequiresMembership = drop.RolledRequiresMembership,
                    generatedRequiresMembership = drop.RequiresMembership,
                    itemModifiers = modifiers
                });
            }

            Debug.LogError($"[MERCHANT-RUNTIME] generated authored inv='{runtimeInv.name}' generator='{itemGeneratorType}' items={runtimeInv.items.Count}");
        }

        private static System.Random CreateMerchantRandom()
        {
            lock (_merchantRandomLock)
                return new System.Random(_merchantRandom.Next());
        }






        public static bool IsMerchant(string npcGcType)
        {
            if (!_initialized) Initialize();
            return _merchants.ContainsKey(npcGcType);
        }

        public static MerchantData GetMerchant(string npcGcType)
        {
            if (!_initialized) Initialize();
            _merchants.TryGetValue(npcGcType, out var merchant);
            return merchant;
        }

        public static MerchantRuntimeData GetRuntimeMerchant(string npcGcType)
        {
            if (!_initialized) Initialize();
            _runtimeMerchants.TryGetValue(npcGcType, out var merchant);
            return merchant;
        }



        public static void WriteMerchantComponent(LEWriter writer, string npcGcType, ushort npcId, ushort merchantId)
        {
            if (!_initialized) Initialize();


            var runtimeMerchant = GetRuntimeMerchant(npcGcType);
            if (runtimeMerchant == null)
            {
                Debug.LogError($"[MERCHANT-RUNTIME] reason=runtime-missing npc={npcGcType}");
                return;
            }

            if (VerboseMerchantItemLogging)
            {
                Debug.LogError($"[MERCHANT-RUNTIME] writeComponent npc={npcGcType}");
            }

            int merchantCompStart = writer.Position;
            writer.WriteByte(0x32);
            writer.WriteUInt16(npcId);
            writer.WriteUInt16(merchantId);

            writer.WriteByte(0xFF);
            writer.WriteCString("merchant");

            writer.WriteByte(0x01);

            WriteMerchantInitPayload(writer, npcGcType, runtimeMerchant);

            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-RUNTIME] componentBytes={writer.Position - merchantCompStart} start={merchantCompStart} end={writer.Position}");
        }

        private static void WriteMerchantInitPayload(LEWriter writer, string npcGcType, MerchantRuntimeData runtimeMerchant, bool includeDynamicItems = true)
        {
            writer.WriteUInt32(checked((uint)runtimeMerchant.nextItemId));
            writer.WriteUInt32(0x00000004);

            int invCount = runtimeMerchant.inventories.Count;
            GCObject.WriteChildCount(writer, invCount);
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-RUNTIME] writeInventories count={invCount}");

            foreach (var inventory in runtimeMerchant.inventories)
            {
                int invStartPos = writer.Position;
                WriteInventory(writer, inventory, npcGcType, includeDynamicItems);
                int invEndPos = writer.Position;
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MERCHANT-RUNTIME] writeInventory inv='{inventory.name}' id={inventory.id} gcType='{inventory.gcType}' bytes={invEndPos - invStartPos} start={invStartPos} end={invEndPos}");
            }

            int resetTimeTicks = 0;
            foreach (var inv in runtimeMerchant.inventories)
            {
                if (!inv.staticContents)
                {
                    resetTimeTicks = GetTicksUntilRegeneration(npcGcType);
                    if (VerboseMerchantItemLogging)
                        Debug.LogError($"[MERCHANT-RUNTIME] timer ticks={resetTimeTicks}");
                    break;
                }
            }

            if (resetTimeTicks > 0xFFFF) resetTimeTicks = 0xFFFF;

            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)resetTimeTicks);
            writer.WriteUInt16(MERCHANT_REFRESH_EVENT_ID);

            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-RUNTIME] resetTime ticks={resetTimeTicks}");
        }

        private static void WriteInventory(LEWriter writer, MerchantInventoryRuntimeData inventory, string npcGcType, bool includeDynamicItems = true)
        {
            int authoredCount = inventory.authoredItemCount;
            if (authoredCount < 0 || authoredCount > inventory.items.Count)
                throw new InvalidDataException($"Missing authored merchant children inventory='{inventory.gcType}' expected={authoredCount} actual={inventory.items.Count}");
            int dynamicCount = includeDynamicItems || !inventory.autoGenerateItems
                ? inventory.items.Count - authoredCount
                : 0;
            int invStart = writer.Position;
            writer.WriteByte(0xFF);
            writer.WriteCString(inventory.gcType.ToLowerInvariant());

            writer.WriteByte((byte)inventory.id);
            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-WRITE-INVENTORY] start inv='{inventory.name}' gcType='{inventory.gcType}' id={inventory.id} static={inventory.staticContents} items={inventory.items.Count} pos={invStart}");

            if (authoredCount == 0 && dynamicCount == 0)
            {
                writer.WriteByte(0x00);
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MERCHANT-RUNTIME] inventory inv='{inventory.name}' id={inventory.id} mode=client items={inventory.items.Count}");
            }
            else
            {
                writer.WriteByte(0x01);
                int itemCount = authoredCount + dynamicCount;
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MERCHANT-RUNTIME] writeInventory inv='{inventory.name}' items={itemCount}");

                if (VerboseMerchantItemLogging)
                {
                    for (int v = 0; v < itemCount; v++)
                    {
                        var vi = inventory.items[v];
                        string vk = vi.gcType.ToLowerInvariant();
                        if (vk.StartsWith("items.pal.")) vk = vk.Substring(10);
                        int vSlots;
                        if (TryGetAuthoredMerchantModSlots(vk, out int vs)) vSlots = vs;
                        else
                        {
                            var vd = AuthoredGameplayCatalog.FindItem(vk);
                            if (vd != null) vSlots = vd.modCount;
                            else vSlots = IsWeaponType(vk) ? 1 : 2;
                        }
                        if (vk.StartsWith("chain") && !vk.Contains("shield") && !vk.Contains("mythic"))
                            vSlots = 1;
                        Debug.LogError($"[MERCHANT-PRE-WRITE] index={v} item={vi.gcType} modSlots={vSlots} id={vi.id}");
                    }
                    Debug.LogError("[MERCHANT-PRE-WRITE] end");
                }

                for (int itemIndex = 0; itemIndex < authoredCount; itemIndex++)
                    WriteItem(writer, inventory.items[itemIndex], includeType: false);

                GCObject.WriteChildCount(writer, dynamicCount);
                for (int itemIndex = authoredCount; itemIndex < itemCount; itemIndex++)
                {
                    WriteItem(writer, inventory.items[itemIndex]);
                }

                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MERCHANT-RUNTIME] inventory inv='{inventory.name}' items={itemCount} resetTicks={GetTicksUntilRegeneration(npcGcType)}");
            }
        }
        public static void ProcessRefreshes(
            IReadOnlyList<GameServer.Zone> zoneOrder,
            Dictionary<uint, List<GameServer.ZoneNPC>> zoneNPCs,
            IReadOnlyList<RRConnection> connections,
            Action<RRConnection, byte, byte, byte[]> sendPacket,
            uint simulationTick)
        {
            FlushPendingMerchantRefreshAdds(connections, sendPacket, simulationTick);
            var activeZoneIds = new HashSet<uint>(connections.Where(conn => conn.IsConnected).Select(conn => conn.CurrentZoneId));
            var refreshedMerchants = new Dictionary<string, MerchantRefreshResult>(StringComparer.OrdinalIgnoreCase);

            foreach (GameServer.Zone zone in zoneOrder)
            {
                uint zoneId = zone.id;
                if (!activeZoneIds.Contains(zoneId))
                    continue;
                if (!zoneNPCs.TryGetValue(zoneId, out List<GameServer.ZoneNPC> zoneNpcs))
                    continue;

                foreach (var npc in zoneNpcs)
                {
                    if (!npc.IsMerchant) continue;

                    if (!_runtimeMerchants.TryGetValue(npc.GCClass, out var runtimeMerchant))
                        continue;
                    if (!_merchants.TryGetValue(npc.GCClass, out var merchantData))
                        continue;

                    if (!refreshedMerchants.TryGetValue(npc.GCClass, out MerchantRefreshResult refresh))
                    {
                        if (!runtimeMerchant.refreshTimerInitialized)
                        {
                            runtimeMerchant.refreshDueTick = unchecked(simulationTick + MERCHANT_REFRESH_TIMER_TICKS);
                            runtimeMerchant.refreshTimerInitialized = true;
                            continue;
                        }
                        if (!HasReachedSimulationTick(simulationTick, runtimeMerchant.refreshDueTick))
                            continue;

                        refresh = RegenerateInventories(runtimeMerchant, merchantData);
                        runtimeMerchant.refreshDueTick = unchecked(simulationTick + MERCHANT_REFRESH_TIMER_TICKS);
                        refreshedMerchants.Add(npc.GCClass, refresh);
                        Debug.LogError($"[MERCHANT-RUNTIME] regenerated npc={npc.GCClass} inventories={refresh.InventoryCount} items={refresh.ItemCount} tick={simulationTick}");
                    }

                    if (refresh.InventoryCount == 0)
                        continue;
                    QueueMerchantRefreshAdd(zoneId, npc.GCClass, (ushort)npc.MerchantId, simulationTick, refresh.RemovedItemIds);
                }
            }
        }

        private static bool HasReachedSimulationTick(uint currentTick, uint dueTick)
        {
            return unchecked((int)(currentTick - dueTick)) >= 0;
        }

        private static void QueueMerchantRefreshAdd(uint zoneId, string npcGcClass, ushort componentId, uint simulationTick, IReadOnlyList<uint> removedItemIds)
        {
            _pendingMerchantRefreshAdds.RemoveAll(p => p.zoneId == zoneId && p.componentId == componentId);
            _pendingMerchantRefreshAdds.Add(new PendingMerchantRefreshAdd
            {
                zoneId = zoneId,
                npcGcClass = npcGcClass,
                componentId = componentId,
                removedItemIds = removedItemIds != null ? new List<uint>(removedItemIds) : new List<uint>(),
                sendAfterTick = unchecked(simulationTick + MERCHANT_REFRESH_SEND_DELAY_TICKS)
            });
            Debug.LogError($"[MERCHANT-RUNTIME] queuedRefresh npc={npcGcClass} dueTick={unchecked(simulationTick + MERCHANT_REFRESH_SEND_DELAY_TICKS)}");
        }

        private static void FlushPendingMerchantRefreshAdds(
            IReadOnlyList<RRConnection> connections,
            Action<RRConnection, byte, byte, byte[]> sendPacket,
            uint simulationTick)
        {
            int pendingIndex = 0;
            while (pendingIndex < _pendingMerchantRefreshAdds.Count)
            {
                var pending = _pendingMerchantRefreshAdds[pendingIndex];
                if (!HasReachedSimulationTick(simulationTick, pending.sendAfterTick))
                {
                    pendingIndex++;
                    continue;
                }

                _pendingMerchantRefreshAdds.RemoveAt(pendingIndex);

                if (!_runtimeMerchants.TryGetValue(pending.npcGcClass, out var runtimeMerchant))
                    continue;

                byte[] data = BuildMerchantInventoryRefreshPacket(pending.componentId, runtimeMerchant, pending.removedItemIds);
                if (data.Length <= 2)
                    continue;

                int sent = 0;
                foreach (var conn in connections)
                {
                    if (conn.CurrentZoneId != pending.zoneId || !conn.IsConnected || !conn.IsSpawned || !conn.AllowFlush)
                        continue;

                    sendPacket(conn, 0x01, 0x0F, data);
                    sent++;
                }

                Debug.LogError($"[MERCHANT-RUNTIME] sentRefresh npc={pending.npcGcClass} bytes={data.Length} clients={sent}");
                if (VerboseMerchantItemLogging)
                {
                    int totalInv = runtimeMerchant.inventories.Count(inv => !inv.staticContents);
                    int totalItems = runtimeMerchant.inventories.Where(inv => !inv.staticContents).Sum(inv => inv.items.Count);
                    Debug.LogError($"[MERCHANT-DETAIL] flushA npc={pending.npcGcClass} compId=0x{pending.componentId:X4} dataLen={data.Length} removed={pending.removedItemIds.Count} dynInv={totalInv} dynItems={totalItems} sentTo={sent}");
                }
            }
        }

        private static MerchantRefreshResult RegenerateInventories(MerchantRuntimeData runtimeMerchant, MerchantData merchantData)
        {
            var result = new MerchantRefreshResult();

            foreach (var runtimeInv in runtimeMerchant.inventories)
            {
                if (!runtimeInv.autoGenerateItems)
                    continue;

                MerchantInventoryData invData = RequireAuthoredInventory(merchantData, runtimeInv);
                int preItemCount = runtimeInv.items.Count;
                Debug.LogError($"[MERCHANT-RUNTIME] refreshInventory npc={merchantData.npcGcType} inv='{runtimeInv.name}' ordinal={runtimeInv.authoredOrdinal}");
                foreach (var item in runtimeInv.items)
                {
                    if (item.id >= 0)
                        result.RemovedItemIds.Add((uint)item.id);
                }
                GenerateInventoryItems(runtimeMerchant, runtimeInv, invData);
                if (VerboseMerchantItemLogging)
                    Debug.LogError($"[MERCHANT-DETAIL] regenerate npc={merchantData.npcGcType} inv={runtimeInv.id} ordinal={runtimeInv.authoredOrdinal} generator={runtimeInv.itemGenerator} itemsBefore={preItemCount} itemsAfter={runtimeInv.items.Count}");
                result.InventoryCount++;
                result.ItemCount += runtimeInv.items.Count;
            }

            return result;
        }

        private static byte[] BuildMerchantInventoryRefreshPacket(ushort componentId, MerchantRuntimeData runtimeMerchant, IEnumerable<uint> removedItemIds)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            WriteDynamicInventoryRemoveUpdates(writer, componentId, removedItemIds);
            WriteDynamicInventoryAddUpdates(writer, componentId, runtimeMerchant);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private static void WriteDynamicInventoryRemoveUpdates(LEWriter writer, ushort componentId, IEnumerable<uint> itemIds)
        {
            if (itemIds == null)
                return;

            foreach (var itemId in itemIds.Distinct())
            {
                writer.WriteByte(0x35);
                writer.WriteUInt16(componentId);
                writer.WriteByte(0x1F);
                writer.WriteUInt32(itemId);
                writer.WriteByte(0x02);
                writer.WriteUInt32(0x00000000);
            }
        }

        private static void WriteDynamicInventoryAddUpdates(LEWriter writer, ushort componentId, MerchantRuntimeData runtimeMerchant)
        {
            foreach (var runtimeInv in runtimeMerchant.inventories)
            {
                if (runtimeInv.staticContents || !runtimeInv.autoGenerateItems || runtimeInv.items.Count == 0)
                    continue;

                foreach (var item in runtimeInv.items)
                {
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(componentId);
                    writer.WriteByte(0x1E);
                    writer.WriteByte((byte)runtimeInv.id);
                    WriteItem(writer, item);
                    writer.WriteByte(0x02);
                    writer.WriteUInt32(0x00000000);
                }
            }
        }

        public static void HandleBuyItem(RRConnection conn, ushort componentId, uint itemId,
        GameServer.ZoneNPC npc,
        Dictionary<string, DungeonRunners.Data.GCObject> selectedCharacters,
        Action<RRConnection, byte, byte, byte[]> sendPacket,
        GameServer server, ushort buyerEntityId, bool isFree = false)
        {
            uint targetId = itemId;
            if (conn?.Avatar == null || conn.Avatar.Id != buyerEntityId)
                return;
            Debug.LogError($"[MERCHANT-BUY] request itemId={targetId} buyerEntityId={buyerEntityId}");

            if (npc == null || !npc.IsMerchant || npc.MerchantId != componentId)
            {
                Debug.LogError($"[MERCHANT-BUY] reason=not-found itemId={targetId}");
                return;
            }
            if (!_runtimeMerchants.TryGetValue(npc.GCClass, out var runtimeMerchant))
            {
                Debug.LogError($"[MERCHANT-BUY] reason=missing-runtime npc='{npc.GCClass}'");
                return;
            }

            foreach (var inv in runtimeMerchant.inventories)
            {
                MerchantItemRuntimeData item = inv.items.FirstOrDefault(i => (uint)i.id == targetId);
                if (item == null) continue;
                if (!item.hasResolvedPrice)
                {
                    Debug.LogError($"[MERCHANT-BUY] reason=unresolved-price item={item.gcType}");
                    return;
                }

                var inv_final = inv;
                var item_final = item;

                Debug.LogError($"[MERCHANT-BUY] found item={item_final.gcType} inv='{inv_final.name}' invId={inv_final.id}");

                        if (!selectedCharacters.TryGetValue(conn.LoginName, out var gcObj))
                        {
                            Debug.LogError($"[MERCHANT-BUY] reason=no-character login={conn.LoginName}");
                            return;
                        }

                        var savedChar = server.GetSavedCharacterForConn(conn);
                        if (savedChar == null)
                        {
                            Debug.LogError("[MERCHANT-BUY] reason=no-saved-character");
                            return;
                        }

                        string memberPrefix = isFree ? "free_" : "member_";
                        uint price = item_final.price;
                        string gcLowerBuy = item_final.gcType.ToLowerInvariant();
                        bool isConsumableBuy = gcLowerBuy.Contains("consumable") || gcLowerBuy.Contains("potion")
                                            || gcLowerBuy.Contains("townportal");

                        if (isFree)
                        {
                            bool isMajorPotion = gcLowerBuy.Contains("majorhealthpotion") || gcLowerBuy.Contains("majormanapotion");
                            bool isMemberEquip = !isConsumableBuy && (
                                item_final.rarity == ItemRarity.Rare ||
                                item_final.rarity == ItemRarity.Unique ||
                                item_final.rarity == ItemRarity.Mythic);

                            if (isMajorPotion || isMemberEquip)
                            {
                                Debug.LogError($"[MERCHANT-BUY] reason=membership-required login={conn.LoginName} item={item_final.gcType} rarity={item_final.rarity}");
                                return;
                            }
                        }
                        if (isConsumableBuy)
                        {
                            var playerState = server.GetPlayerState(conn.ConnId.ToString());
                            int playerLevel = playerState != null && playerState.Level > 0 ? playerState.Level : (int)savedChar.level;
                            if (playerLevel < 1) playerLevel = 1;

                            float gcGold = 0.175f;
                            bool scaleToObserverLevel = true;

                            if (gcLowerBuy.Contains("majorhealthpotion"))
                                gcGold = 0.2f;
                            else if (gcLowerBuy.Contains("healthpotion"))
                                gcGold = 0.175f;
                            else if (gcLowerBuy.Contains("majormanapotion"))
                                gcGold = 0.2f;
                            else if (gcLowerBuy.Contains("manapotion"))
                                gcGold = 0.175f;
                            else if (gcLowerBuy.Contains("townportal"))
                            {
                                gcGold = 2.0f;
                                scaleToObserverLevel = false;
                            }

                            int effectiveLevel = scaleToObserverLevel ? Math.Max(playerLevel, 3) : 1;

                            string goldValuePerLevelText = ServerSettings.GetString(memberPrefix + "itemGoldValuePerLevel", null)
                                         ?? ServerSettings.GetString("itemGoldValuePerLevel", null);
                            float goldPerLevel = goldValuePerLevelText != null && float.TryParse(goldValuePerLevelText,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float goldValuePerLevelParsed)
                                ? goldValuePerLevelParsed : 50f;

                            string buyModifierText = ServerSettings.GetString(memberPrefix + "itemBuyValueModifier", null)
                                        ?? ServerSettings.GetString("itemBuyValueModifier", null);
                            float buyMod = buyModifierText != null && float.TryParse(buyModifierText,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float buyModifierParsed)
                                ? buyModifierParsed : 1.0f;

                            int qty = item_final.quantity > 0 ? item_final.quantity : 1;
                            double rawUnitPrice = (double)goldPerLevel * effectiveLevel * gcGold * buyMod;
                            if (double.IsNaN(rawUnitPrice) || double.IsInfinity(rawUnitPrice) || rawUnitPrice < 1 || rawUnitPrice > int.MaxValue)
                            {
                                Debug.LogError($"[MERCHANT-BUY] reason=invalid-price item={item_final.gcType} unit={rawUnitPrice} qty={qty}");
                                return;
                            }
                            long unitPrice = (long)rawUnitPrice;
                            long computedPrice = unitPrice * qty;
                            if (computedPrice < 1 || computedPrice > int.MaxValue)
                            {
                                Debug.LogError($"[MERCHANT-BUY] reason=price-overflow item={item_final.gcType} unit={unitPrice} qty={qty} price={computedPrice}");
                                return;
                            }
                            price = (uint)computedPrice;

                            Debug.LogError($"[MERCHANT-BUY] consumablePrice account={memberPrefix} playerStateLevel={playerState?.Level} dbLevel={savedChar.level} effectiveLevel={effectiveLevel} gcGold={gcGold} goldPerLevel={goldPerLevel} buyMod={buyMod} unit={unitPrice} qty={qty} price={price}");
                        }
                        else if (gcLowerBuy.Contains("questitem"))
                        {
                            price = 1;
                        }
                        else
                        {
                            if (item_final.goldValue > 0)
                            {
                                price = RPGSettings.CalculatePriceWithGoldValue(
                                    item_final.level, item_final.rarity, item_final.goldValue, memberPrefix);
                                Debug.LogError($"[MERCHANT-BUY] equipmentPrice account={memberPrefix} level={item_final.level} rarity={item_final.rarity} goldValue={item_final.goldValue} price={price}");
                            }
                        }
                        Debug.LogError($"[MERCHANT-BUY] item={item_final.gcType} price={price} rarity={item_final.rarity} level={item_final.level}");
                        if (RPGSettings.IsMythicPALItem(item_final.gcType))
                            Debug.LogError($"[MERCHANT-BUY] mythic item={item_final.gcType} price={price} rarity={item_final.rarity} level={item_final.level}");

                        Debug.LogError($"[MERCHANT-BUY] gold player={savedChar.gold} price={price}");
                        if (savedChar.gold < price)
                        {
                            Debug.LogError("[MERCHANT-BUY] reason=gold");
                            return;
                        }
                        {
                            string buyGcType = item.gcType;
                            if (buyGcType.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                                buyGcType = buyGcType.Substring(10);
                            buyGcType = MapConsumableGcType(buyGcType);
                            buyGcType = buyGcType.ToLowerInvariant();

                            string buyPacketGcType = buyGcType;
                            if (HasAuthoredMerchantModSlots(buyGcType) && !buyGcType.Contains("mythicpal"))
                                buyPacketGcType = "items.pal." + buyGcType;
                            buyPacketGcType = DungeonRunners.Data.GCObject.GetPacketGCClassFor(buyPacketGcType);

                            var dims = GetItemDimensions(item_final.gcType);
                            int itemWidth = dims.width, itemHeight = dims.height;
                            if (itemWidth <= 0 || itemHeight <= 0)
                            {
                                Debug.LogError($"[MERCHANT-BUY] item={item_final.gcType} reason=missing-authored-dimensions");
                                return;
                            }

                            string connId = conn.ConnId.ToString();

                            string gcLower2 = buyGcType.ToLower();
                            string dfcClass = "Armor";
                            if (gcLower2.Contains("consumable") || gcLower2.Contains("questitem") || gcLower2.Contains("ring") || gcLower2.Contains("amulet") || gcLower2.Contains("scroll") || gcLower2.Contains("potion"))
                                dfcClass = "Item";
                            else if (gcLower2.Contains("sword") || gcLower2.Contains("axe") || gcLower2.Contains("mace") || gcLower2.Contains("dagger") || gcLower2.Contains("hammer") || gcLower2.Contains("staff") || gcLower2.Contains("spear"))
                                dfcClass = "MeleeWeapon";
                            else if (gcLower2.Contains("bow") || gcLower2.Contains("gun") || gcLower2.Contains("crossbow"))
                                dfcClass = "RangedWeapon";

                            bool isQuestItem2 = buyGcType.Contains("questitem");

                            int maxStack = ItemStackRules.GetLimit(buyPacketGcType);
                            bool isStackable = maxStack > 1;

                            int buyQty = item.quantity > 0 ? item.quantity : 1;
                            if (buyQty > byte.MaxValue)
                            {
                                Debug.LogError($"[MERCHANT-BUY] reason=quantity-out-of-range item={buyGcType} quantity={buyQty}");
                                return;
                            }
                            byte[] purchasedItemPacket = null;
                            Action rollbackInventory = null;
                            int notifyQuestQuantity = 0;

                            if (isQuestItem2 && !isStackable)
                            {
                                byte slotX = 0, slotY = 0;
                                bool foundSlot = false;
                                for (byte row = 0; row < 8 && !foundSlot; row++)
                                    for (byte col = 0; col < 10 && !foundSlot; col++)
                                        if (!server.IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                                        { slotX = col; slotY = row; foundSlot = true; }

                                if (!foundSlot)
                                {
                                    Debug.LogError("[MERCHANT-BUY] reason=inventory-full");
                                    return;
                                }

                                uint slot = server.GetNextInventorySlot(connId);

                                var itemWriter = new LEWriter();
                                itemWriter.WriteByte(0x07);
                                itemWriter.WriteByte(0x35);
                                itemWriter.WriteUInt16(conn.UnitContainerId);
                                itemWriter.WriteByte(0x1E);
                                itemWriter.WriteByte(0x0B);
                                var newItem = new DungeonRunners.Data.GCObject
                                {
                                    GCClass = buyPacketGcType,
                                    DFCClass = "Item",
                                    StoredLevel = Math.Max(1, item_final.level)
                                };
                                newItem.WriteInitForInventory(itemWriter, slotX, slotY, slot, newItem.StoredLevel, (byte)Math.Clamp(buyQty, 1, byte.MaxValue));
                                server.WritePlayerEntitySynch(conn, itemWriter);
                                itemWriter.WriteByte(0x06);
                                purchasedItemPacket = itemWriter.ToArray();

                                server.TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                                server.OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                server.SetStackCount(connId, slot, buyQty);
                                rollbackInventory = () =>
                                {
                                    server.RemoveInventoryItemBySlot(connId, slot);
                                    server.FreeInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                };
                                notifyQuestQuantity = buyQty;

                                Debug.LogError($"[MERCHANT-BUY] questItem item={buyGcType} pos=({slotX},{slotY})");
                            }
                            else if (isStackable)
                            {
                                notifyQuestQuantity = isQuestItem2 ? buyQty : 0;
                                if (buyQty > maxStack)
                                {
                                    Debug.LogError($"[MERCHANT-BUY] reason=quantity-exceeds-stack item={buyGcType} quantity={buyQty} max={maxStack}");
                                    return;
                                }

                                uint partialSlot = 0;
                                byte partialX = 0, partialY = 0;
                                int partialCount = 0;
                                DungeonRunners.Data.GCObject partialItem = null;
                                bool foundPartial = false;

                                var allInv = server.GetAllInventoryItems(connId);
                                if (allInv != null)
                                {
                                    string searchGc = buyPacketGcType.ToLowerInvariant();
                                    Debug.LogError($"[MERCHANT-STACK] search gc='{searchGc}' max={maxStack} items={allInv.Count}");
                                    var incomingItem = new DungeonRunners.Data.GCObject { GCClass = buyPacketGcType, DFCClass = "Item" };
                                    foreach (var inventoryEntry in server.GetOrderedInventoryItems(connId))
                                    {
                                        string storedGc = inventoryEntry.Value.Item1.GCClass.ToLowerInvariant();
                                        int storedCount = server.GetStackCount(connId, inventoryEntry.Key);
                                        Debug.LogError($"[MERCHANT-STACK] slot={inventoryEntry.Key} gc='{storedGc}' count={storedCount}");
                                        if (ItemStackRules.CanMergeAutomatically(inventoryEntry.Value.item, storedCount, incomingItem, buyQty))
                                        {
                                            partialSlot = inventoryEntry.Key;
                                            partialX = inventoryEntry.Value.Item2;
                                            partialY = inventoryEntry.Value.Item3;
                                            partialCount = storedCount;
                                            partialItem = inventoryEntry.Value.Item1;
                                            foundPartial = true;
                                            Debug.LogError($"[MERCHANT-STACK] found slot={partialSlot} count={storedCount}");
                                            break;
                                        }
                                    }
                                    if (!foundPartial)
                                        Debug.LogError("[MERCHANT-STACK] result=new-slot");
                                }

                                bool addedToExisting = false;

                                if (foundPartial)
                                {
                                    int newCount = partialCount + buyQty;

                                    var itemWriter = new LEWriter();
                                    itemWriter.WriteByte(0x07);
                                    itemWriter.WriteByte(0x35);
                                    itemWriter.WriteUInt16(conn.UnitContainerId);
                                    itemWriter.WriteByte(0x22);
                                    itemWriter.WriteUInt32(partialSlot);
                                    itemWriter.WriteByte((byte)newCount);
                                    server.WritePlayerEntitySynch(conn, itemWriter);
                                    itemWriter.WriteByte(0x06);
                                    purchasedItemPacket = itemWriter.ToArray();

                                    server.SetStackCount(connId, partialSlot, newCount);
                                    rollbackInventory = () => server.SetStackCount(connId, partialSlot, partialCount);
                                    Debug.LogError($"[MERCHANT-BUY] stack item={buyGcType} old={partialCount} add={buyQty} count={newCount} pos=({partialX},{partialY}) slot={partialSlot}");
                                    addedToExisting = true;
                                }

                                if (!addedToExisting)
                                {
                                    byte slotX = 0, slotY = 0;
                                    bool foundSlot = false;
                                    for (byte row = 0; row < 8 && !foundSlot; row++)
                                        for (byte col = 0; col < 10 && !foundSlot; col++)
                                            if (!server.IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                                            { slotX = col; slotY = row; foundSlot = true; }

                                    if (!foundSlot)
                                    {
                                        Debug.LogError("[MERCHANT-BUY] reason=inventory-full");
                                        return;
                                    }

                                    uint slot = server.GetNextInventorySlot(connId);

                                    var itemWriter = new LEWriter();
                                    itemWriter.WriteByte(0x07);
                                    itemWriter.WriteByte(0x35);
                                    itemWriter.WriteUInt16(conn.UnitContainerId);
                                    itemWriter.WriteByte(0x1E);
                                    itemWriter.WriteByte(0x0B);
                                    var newItem = new DungeonRunners.Data.GCObject
                                    {
                                        GCClass = buyPacketGcType,
                                        DFCClass = "Item",
                                        StoredLevel = Math.Max(1, item_final.level)
                                    };
                                    newItem.WriteInitForInventory(itemWriter, slotX, slotY, slot, newItem.StoredLevel, (byte)Math.Min(buyQty, byte.MaxValue));
                                    server.WritePlayerEntitySynch(conn, itemWriter);
                                    itemWriter.WriteByte(0x06);
                                    purchasedItemPacket = itemWriter.ToArray();

                                    server.TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                                    server.OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                    server.SetStackCount(connId, slot, buyQty);
                                    rollbackInventory = () =>
                                    {
                                        server.RemoveInventoryItemBySlot(connId, slot);
                                        server.FreeInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                    };

                                    Debug.LogError($"[MERCHANT-BUY] newStack item={buyGcType} qty={buyQty} pos=({slotX},{slotY})");
                                }
                            }
                            else
                            {
                                byte slotX = 0, slotY = 0;
                                bool foundSlot = false;
                                for (byte row = 0; row < 8 && !foundSlot; row++)
                                    for (byte col = 0; col < 10 && !foundSlot; col++)
                                        if (!server.IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                                        { slotX = col; slotY = row; foundSlot = true; }

                                if (!foundSlot)
                                {
                                    Debug.LogError("[MERCHANT-BUY] reason=inventory-full");
                                    return;
                                }

                                uint slot = server.GetNextInventorySlot(connId);
                                var newItem = new DungeonRunners.Data.GCObject { GCClass = buyPacketGcType, DFCClass = dfcClass };
                                newItem.PresetScaleMod = item_final.hasGeneratedItemState ? null : item_final.scaleMod;
                                newItem.StoredRarity = (int)item_final.rarity;
                                newItem.StoredLevel = item_final.level > 0 ? item_final.level : RPGSettings.GetItemLevel(buyGcType);
                                newItem.HasGeneratedItemState = item_final.hasGeneratedItemState;
                                newItem.RolledRequiresMembership = item_final.rolledRequiresMembership;
                                newItem.GeneratedRequiresMembership = item_final.generatedRequiresMembership;
                                newItem.GeneratedItemModifiers = item_final.itemModifiers == null
                                    ? new List<string>()
                                    : new List<string>(item_final.itemModifiers);

                                var itemWriter = new LEWriter();
                                itemWriter.WriteByte(0x07);
                                itemWriter.WriteByte(0x35);
                                itemWriter.WriteUInt16(conn.UnitContainerId);
                                itemWriter.WriteByte(0x1E);
                                itemWriter.WriteByte(0x0B);
                                int actualItemLevel = item_final.level > 0 ? item_final.level : RPGSettings.GetItemLevel(buyGcType);
                                newItem.WriteInitForInventory(itemWriter, slotX, slotY, slot, actualItemLevel, (byte)Math.Min(buyQty, byte.MaxValue));
                                server.WritePlayerEntitySynch(conn, itemWriter);
                                itemWriter.WriteByte(0x06);
                                purchasedItemPacket = itemWriter.ToArray();

                                server.TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                                server.OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                server.SetStackCount(connId, slot, buyQty);
                                rollbackInventory = () =>
                                {
                                    server.RemoveInventoryItemBySlot(connId, slot);
                                    server.FreeInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                                };

                                Debug.LogError($"[MERCHANT-BUY] equipment item={buyGcType} pos=({slotX},{slotY})");
                            }

                            if (rollbackInventory == null || purchasedItemPacket == null)
                            {
                                Debug.LogError($"[MERCHANT-BUY] reason=inventory-mutation-missing item={buyGcType}");
                                return;
                            }

                            uint oldGold = savedChar.gold;
                            uint oldPlayerGold = server.GetPlayerState(conn.ConnId.ToString())?.Gold ?? oldGold;
                            uint oldBuyPrice = GetBuyPrice(conn.ConnId.ToString(), buyGcType);
                            SetBuyPrice(conn.ConnId.ToString(), buyGcType, price);
                            savedChar.gold -= price;
                            PlayerState purchasePlayerState = server.GetPlayerState(conn.ConnId.ToString());
                            if (purchasePlayerState != null) purchasePlayerState.Gold = savedChar.gold;
                            QuestProgressMutation purchaseQuestMutation = notifyQuestQuantity > 0
                                ? server.StageQuestItemAcquisition(conn, buyGcType, notifyQuestQuantity)
                                : null;
                            if (!server.SavePlayerInventoryPublic(conn))
                            {
                                server.RollbackQuestItemAcquisition(purchaseQuestMutation);
                                savedChar.gold = oldGold;
                                if (purchasePlayerState != null) purchasePlayerState.Gold = oldPlayerGold;
                                SetBuyPrice(conn.ConnId.ToString(), buyGcType, oldBuyPrice);
                                rollbackInventory();
                                Debug.LogError("[MERCHANT-BUY] reason=database-save-failed");
                                return;
                            }

                            if (!inv_final.staticContents)
                            {
                                int itemIdToRemove = item_final.id;
                                if (!inv_final.items.Remove(item_final))
                                    Debug.LogError($"[MERCHANT-BUY] state=committed phase=stock-remove-mismatch itemId={itemIdToRemove}");
                                var removeWriter = new LEWriter();
                                removeWriter.WriteByte(0x07);
                                removeWriter.WriteByte(0x35);
                                removeWriter.WriteUInt16(componentId);
                                removeWriter.WriteByte(0x1F);
                                removeWriter.WriteUInt32((uint)itemIdToRemove);
                                removeWriter.WriteByte(0x02);
                                removeWriter.WriteUInt32(0x00000000);
                                removeWriter.WriteByte(0x06);
                                byte[] removePacket = removeWriter.ToArray();
                                Debug.LogError($"[MERCHANT-BUY] removePacket bytes={BitConverter.ToString(removePacket)}");
                                Debug.LogError($"[MERCHANT-BUY] remove invId={inv.id} inv='{inv.name}' itemId={itemIdToRemove}");
                                sendPacket(conn, 0x01, 0x0F, removePacket);
                            }
                            else
                                Debug.LogError($"[MERCHANT-BUY] staticItem item={item_final.gcType}");

                            if (conn.UnitContainerId != 0)
                            {
                                var goldWriter = new LEWriter();
                                goldWriter.WriteByte(0x07);
                                goldWriter.WriteByte(0x35);
                                goldWriter.WriteUInt16(conn.UnitContainerId);
                                goldWriter.WriteByte(0x20);
                                goldWriter.WriteInt32(-(int)price);
                                goldWriter.WriteByte(0x00);
                                goldWriter.WriteUInt32(0x00000000);
                                goldWriter.WriteByte(0x01);
                                server.WritePlayerEntitySynch(conn, goldWriter);
                                goldWriter.WriteByte(0x06);
                                byte[] goldPacket = goldWriter.ToArray();
                                Debug.LogError($"[MERCHANT-BUY] goldUpdate amount=-{price} unitContainer=0x{conn.UnitContainerId:X4}");
                                Debug.LogError($"[MERCHANT-BUY] goldPacket bytes={BitConverter.ToString(goldPacket)}");
                                sendPacket(conn, 0x01, 0x0F, goldPacket);
                            }

                            sendPacket(conn, 0x01, 0x0F, purchasedItemPacket);
                            Debug.LogError($"[MERCHANT-BUY] buyPriceStored item={buyGcType} price={price}");
                            Debug.LogError($"[MERCHANT-BUY] goldDeducted amount={price} balance={savedChar.gold}");
                            if (notifyQuestQuantity > 0)
                                server.CommitQuestItemAcquisition(conn, purchaseQuestMutation, buyGcType, notifyQuestQuantity, "merchant-buy");
                        }

                        Debug.LogError($"[MERCHANT-BUY] complete item={item.gcType} price={price}");
                        return;
            }
            Debug.LogError($"[MERCHANT-BUY] reason=not-found itemId={targetId}");
        }

        public static void HandleSellItem(RRConnection conn, ushort componentId, ushort itemId, ushort entityRef,
      Dictionary<string, DungeonRunners.Data.GCObject> selectedCharacters,
      Action<RRConnection, byte, byte, byte[]> sendPacket,
      PlayerState playerState,
      GameServer server,
      uint synchValue)
        {
            Debug.LogError($"[MERCHANT-SELL] itemId={itemId} entityRef=0x{entityRef:X4} componentId=0x{componentId:X4}");
            Debug.LogError($"[MERCHANT-SELL] unitContainer=0x{conn.UnitContainerId:X4} synch=0x{synchValue:X8}");

            var activeItem = playerState?.ActiveItem;
            bool isShiftClick = (activeItem == null);
            DungeonRunners.Data.GCObject sellItem = activeItem;
            byte removeX = 0, removeY = 0;

            if (isShiftClick)
            {
                var invItem = server.GetInventoryItemBySlot(conn.ConnId.ToString(), (uint)itemId);
                if (invItem == null)
                {
                    Debug.LogError($"[MERCHANT-SELL] reason=no-slot-item slot={itemId}");
                    return;
                }
                sellItem = invItem.Value.item;
                removeX = invItem.Value.x;
                removeY = invItem.Value.y;
                Debug.LogError($"[MERCHANT-SELL] mode=shift item={sellItem.GCClass} slot={itemId} pos=({removeX},{removeY})");
            }
            else
            {
                Debug.LogError($"[MERCHANT-SELL] mode=cursor item={activeItem.GCClass}");
            }

            GCNode sellDescription = GCDatabase.Instance.ResolveWithInheritance(sellItem.GCClass)?.GetChild("Description");
            if (sellItem.NoSell || sellDescription?.GetBool("NoSell", false) == true)
                return;

            uint sellPrice = 1;
            string sellGcType = sellItem.GCClass.ToLowerInvariant();
            if (sellGcType.StartsWith("items.pal."))
                sellGcType = sellGcType.Substring(10);
            else if (sellGcType.StartsWith("items.consumables."))
                sellGcType = sellGcType.Substring(18);
            {
                int itemLevel = RPGSettings.GetItemLevel(sellItem.GCClass);
                if (itemLevel < 1) itemLevel = 1;

                float goldValue = 1.0f;
                if (_sellableItems != null)
                {
                    var sellable = _sellableItems.FirstOrDefault(s =>
                        string.Equals(s.gcType, sellGcType, StringComparison.OrdinalIgnoreCase));
                    if (sellable == null)
                        sellable = _sellableItems.FirstOrDefault(s =>
                            s.gcType.ToLower().Contains(sellGcType) || sellGcType.Contains(s.gcType.ToLower()));
                    if (sellable != null && sellable.gcGoldValue > 0)
                        goldValue = sellable.gcGoldValue;
                    else if (!RPGSettings.TryGetBaseGoldValue(sellGcType, out goldValue))
                    {
                        Debug.LogError($"[MERCHANT-SELL] item={sellGcType} reason=missing-authored-gold-value state=rejected");
                        return;
                    }
                }
                else if (!RPGSettings.TryGetBaseGoldValue(sellGcType, out goldValue))
                {
                    Debug.LogError($"[MERCHANT-SELL] item={sellGcType} reason=missing-authored-gold-value state=rejected");
                    return;
                }

                int storedRarity = sellItem.GetEffectiveRarity();
                var itemRarity = storedRarity >= (int)ItemRarity.Normal && storedRarity <= (int)ItemRarity.Mythic
                    ? (ItemRarity)storedRarity
                    : RPGSettings.ResolveItemRarity(sellGcType);

                bool isMythicSell = RPGSettings.IsMythicPALItem(sellGcType);
                int pLevel = (playerState != null && playerState.Level > 0) ? playerState.Level : 0;
                if (isMythicSell)
                {
                    itemRarity = ItemRarity.Mythic;
                    if (pLevel > 0)
                        itemLevel = pLevel + 3;
                    Debug.LogError($"[MERCHANT-SELL] mythic item={sellGcType} rarity=Mythic level={itemLevel} playerLevel={pLevel}");
                }

                int adjustedLevel = RPGSettings.GetEquipRequiredLevel(itemLevel, itemRarity);

                sellPrice = RPGSettings.CalculateSellPrice(adjustedLevel, goldValue, itemRarity, isMythicSell, pLevel);

                uint buyPrice = RPGSettings.CalculatePriceWithGoldValue(adjustedLevel, itemRarity, goldValue);
                if (sellPrice > buyPrice)
                {
                    Debug.LogError($"[MERCHANT-SELL] priceCap sell={sellPrice} buy={buyPrice} level={itemLevel} adjustedLevel={adjustedLevel} rarity={itemRarity} goldValue={goldValue}");
                    sellPrice = buyPrice;
                }

                Debug.LogError($"[MERCHANT-SELL] price level={itemLevel} adjustedLevel={adjustedLevel} goldValue={goldValue} rarity={itemRarity} mythic={isMythicSell} playerLevel={pLevel} sell={sellPrice} buy={buyPrice}");
            }

            if (!selectedCharacters.TryGetValue(conn.LoginName, out var gcObj)) return;
            var savedChar = server.GetSavedCharacterForConn(conn);
            if (savedChar == null) return;

            uint oldGold = savedChar.gold;
            if (sellPrice > int.MaxValue || savedChar.gold > (uint)int.MaxValue - sellPrice)
            {
                Debug.LogError("[MERCHANT-SELL] reason=gold-overflow");
                return;
            }
            string connId = conn.ConnId.ToString();
            int previousCursorCount = server.GetStackCount(connId, 0xFFFFFFFF);
            int removedStackCount = 0;
            int removedWidth = 0;
            int removedHeight = 0;
            (DungeonRunners.Data.GCObject item, byte x, byte y)? removed = null;
            if (isShiftClick)
            {
                var removedDimensions = GetItemDimensions(sellItem.GCClass);
                removedWidth = removedDimensions.width;
                removedHeight = removedDimensions.height;
                if (removedWidth <= 0 || removedHeight <= 0)
                {
                    Debug.LogError($"[MERCHANT-SELL] reason=missing-authored-dimensions item={sellItem.GCClass}");
                    return;
                }
                removedStackCount = server.GetStackCount(connId, (uint)itemId);
                removed = server.GetAndRemoveInventoryItem(connId, (uint)itemId);
                if (removed == null)
                {
                    Debug.LogError($"[MERCHANT-SELL] reason=not-tracked slot={itemId}");
                    return;
                }
            }
            else
            {
                playerState.ActiveItem = null;
                server.SetStackCount(connId, 0xFFFFFFFF, 0);
            }

            uint oldPlayerGold = playerState?.Gold ?? oldGold;
            savedChar.gold += sellPrice;
            if (playerState != null) playerState.Gold = savedChar.gold;
            if (!server.SavePlayerInventoryPublic(conn))
            {
                savedChar.gold = oldGold;
                if (playerState != null) playerState.Gold = oldPlayerGold;
                if (removed != null)
                {
                    server.TrackInventoryItem(connId, (uint)itemId, removed.Value.item, removed.Value.x, removed.Value.y);
                    server.OccupyInventorySlots(connId, removed.Value.x, removed.Value.y, removedWidth, removedHeight);
                    server.SetStackCount(connId, (uint)itemId, Math.Max(1, removedStackCount));
                }
                else
                {
                    playerState.ActiveItem = activeItem;
                    server.SetStackCount(connId, 0xFFFFFFFF, Math.Max(1, previousCursorCount));
                }
                Debug.LogError("[MERCHANT-SELL] reason=database-save-failed");
                return;
            }
            Debug.LogError($"[MERCHANT-SELL] gold old={oldGold} new={savedChar.gold} delta={sellPrice}");

            if (conn.UnitContainerId != 0)
            {
                var writer = new LEWriter();
                writer.WriteByte(0x07);

                if (isShiftClick)
                {
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(conn.UnitContainerId);
                    writer.WriteByte(0x1F);
                    writer.WriteUInt32((uint)itemId);
                    server.WritePlayerEntitySynch(conn, writer);
                }
                else
                {
                    writer.WriteByte(0x35);
                    writer.WriteUInt16(conn.UnitContainerId);
                    writer.WriteByte(0x29);
                    server.WritePlayerEntitySynch(conn, writer);
                }

                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x20);
                writer.WriteUInt32(sellPrice);
                writer.WriteByte(0x00);
                writer.WriteUInt32(0x00000000);
                writer.WriteByte(0x01);
                server.WritePlayerEntitySynch(conn, writer);

                writer.WriteByte(0x06);

                byte[] sellPacket = writer.ToArray();
                Debug.LogError($"[MERCHANT-SELL] packet bytes={sellPacket.Length} data={BitConverter.ToString(sellPacket)}");
                sendPacket(conn, 0x01, 0x0F, sellPacket);
            }

            if (removed != null)
                Debug.LogError($"[MERCHANT-SELL] removed slot={itemId} item={removed.Value.item.GCClass}");

            Debug.LogError($"[MERCHANT-SELL] complete item={sellItem.GCClass} price={sellPrice} gold={savedChar.gold}");
        }








        private static string MapConsumableGcType(string gcType)
        {
            string lower = gcType.ToLowerInvariant();
            if (lower == "items.consumables.consumable_majorhealthpotion" || lower == "consumable_majorhealthpotion")
                return "items.consumables.Consumable_MajorHealthPotion";
            if (lower == "items.consumables.consumable_minorhealthpotion" || lower == "consumable_minorhealthpotion")
                return "items.consumables.Consumable_MinorHealthPotion";
            if (lower == "items.consumables.consumable_majormanapotion" || lower == "consumable_majormanapotion")
                return "items.consumables.Consumable_MajorManaPotion";
            if (lower == "items.consumables.consumable_minormanapotion" || lower == "consumable_minormanapotion")
                return "items.consumables.Consumable_MinorManaPotion";
            if (lower == "items.consumables.consumable_townportal" || lower == "consumable_townportal")
                return "items.consumables.Consumable_TownPortal";
            return gcType;
        }


        private static void WriteItem(LEWriter writer, MerchantItemRuntimeData item, bool includeType = true)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            string gcType = item.gcType;
            if (HasAuthoredMerchantModSlots(gcType) && !gcType.Contains("mythicpal", StringComparison.OrdinalIgnoreCase)
                && !gcType.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                gcType = "items.pal." + gcType;

            int level = item.level > 0 ? item.level : Math.Max(1, RPGSettings.GetItemLevel(item.gcType));
            var wireItem = new DungeonRunners.Data.GCObject
            {
                GCClass = gcType,
                DFCClass = "Item",
                PresetScaleMod = item.hasGeneratedItemState || string.IsNullOrWhiteSpace(item.scaleMod) ? null : item.scaleMod,
                StoredRarity = (int)item.rarity,
                StoredLevel = level,
                HasGeneratedItemState = item.hasGeneratedItemState,
                RolledRequiresMembership = item.rolledRequiresMembership,
                GeneratedRequiresMembership = item.generatedRequiresMembership,
                GeneratedItemModifiers = item.itemModifiers == null ? new List<string>() : new List<string>(item.itemModifiers)
            };
            wireItem.WriteItemData(
                writer,
                checked((uint)item.id),
                checked((byte)item.inventoryX),
                checked((byte)item.inventoryY),
                checked((byte)item.quantity),
                level,
                includeType);

            if (VerboseMerchantItemLogging)
                Debug.LogError($"[MERCHANT-WRITE-ITEM] gc={gcType} id={item.id} level={level} quantity={item.quantity} bytes=native-item-data");
        }

    }


    [Serializable]
    public class SellableItem
    {
        public string gcType;
        public string name;
        public float goldValue;
        public float gcGoldValue;
        public string rarity;
    }

    [Serializable]
    public class ItemDimensions
    {
        public int width;
        public int height;
    }

    public class PendingMerchantRefreshAdd
    {
        public uint zoneId;
        public string npcGcClass;
        public ushort componentId;
        public List<uint> removedItemIds = new List<uint>();
        public uint sendAfterTick;
    }

    public class MerchantRefreshResult
    {
        public int InventoryCount;
        public int ItemCount;
        public List<uint> RemovedItemIds = new List<uint>();
    }

    [Serializable]
    public class MerchantData
    {
        public string npcGcType;
        public string merchantGcType;
        public List<MerchantInventoryData> inventories = new List<MerchantInventoryData>();
    }

    [Serializable]
    public class MerchantInventoryData
    {
        public string name;
        public string gcType;
        public int authoredOrdinal;
        public int id;
        public bool staticContents;
        public bool autoGenerateItems;
        public string itemGenerator;
        public int minItemLevel;
        public int maxItemLevel;
        public string label;
        public int width;
        public int height;
        public List<MerchantItemData> items = new List<MerchantItemData>();
    }

    [Serializable]
    public class MerchantItemData
    {
        public string gcType;
        public int inventoryX;
        public int inventoryY;
        public int id;
        public int quantity = 1;
    }

    public class MerchantRuntimeData
    {
        public string npcGcType;
        public string merchantGcType;
        public int nextItemId = 0;
        public uint refreshDueTick;
        public bool refreshTimerInitialized;
        public List<MerchantInventoryRuntimeData> inventories = new List<MerchantInventoryRuntimeData>();
    }

    public class MerchantInventoryRuntimeData
    {
        public string name;
        public string gcType;
        public int authoredOrdinal;
        public int authoredItemCount;
        public int id;
        public string label;
        public int width;
        public int height;
        public bool staticContents;
        public bool autoGenerateItems;
        public string itemGenerator;
        public int minItemLevel;
        public int maxItemLevel;
        public List<MerchantItemRuntimeData> items = new List<MerchantItemRuntimeData>();
    }

    public class MerchantItemRuntimeData
    {
        public string gcType;
        public int inventoryX;
        public int inventoryY;
        public int id;
        public int quantity;
        public int level;
        public ItemRarity rarity = ItemRarity.Rare;
        public uint price;
        public bool hasResolvedPrice = true;
        public float goldValue;
        public string scaleMod;
        public bool hasGeneratedItemState;
        public bool rolledRequiresMembership;
        public bool generatedRequiresMembership;
        public List<string> itemModifiers = new List<string>();
    }
    public enum ItemRarity
    {
        Normal,
        Superior,
        Magical,
        Rare,
        Unique,
        Mythic
    }


    public static class RPGSettings
    {
        private static readonly Dictionary<ItemRarity, string[]> AuthoredScaleMods = new Dictionary<ItemRarity, string[]>();

        private static readonly (ItemRarity rarity, int weight)[] RarityWeights = new[]
        {
        (ItemRarity.Normal, 15),
        (ItemRarity.Superior, 25),
        (ItemRarity.Magical, 30),
        (ItemRarity.Rare, 20),
        (ItemRarity.Unique, 8),
        (ItemRarity.Mythic, 2)
    };

        private static System.Random _random = new System.Random();

        public static float GetPriceModifier(ItemRarity rarity)
        {
            return GetQualityModFixed32(rarity) / 256f;
        }

        public static int GetEquipRequiredLevel(int itemLevel, ItemRarity rarity)
        {
            int delta;
            switch (rarity)
            {
                case ItemRarity.Normal: delta = GCDatabase.Instance.GetRequiredKnobInt("ItemLevelDeltaNormal"); break;
                case ItemRarity.Superior: delta = GCDatabase.Instance.GetRequiredKnobInt("ItemLevelDeltaSuperior"); break;
                case ItemRarity.Magical: delta = GCDatabase.Instance.GetRequiredKnobInt("ItemLevelDeltaMagical"); break;
                case ItemRarity.Rare: delta = GCDatabase.Instance.GetRequiredKnobInt("ItemLevelDeltaRare"); break;
                case ItemRarity.Unique: delta = GCDatabase.Instance.GetRequiredKnobInt("ItemLevelDeltaUnique"); break;
                case ItemRarity.Mythic: delta = GCDatabase.Instance.GetRequiredKnobInt("ItemLevelDeltaMythic"); break;
                default: throw new InvalidDataException($"No client ItemLevelDelta key for rarity {rarity}");
            }
            return Math.Max(1, itemLevel + delta);
        }

        public static int ResolveNativeItemQuality(GCNode description)
        {
            if (description == null)
                throw new InvalidDataException("Item description is required for native quality resolution");
            if (!description.HasProperty("Quality"))
                return 1;
            string raw = description.GetString("Quality", string.Empty).Trim();
            if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int numeric))
            {
                if (numeric < 0 || numeric > 7)
                    throw new InvalidDataException($"Item quality is outside the native range: '{raw}'");
                return numeric;
            }
            return raw.ToUpperInvariant() switch
            {
                "QUEST" => 0,
                "NORMAL" => 1,
                "SUPERIOR" => 2,
                "MAGICAL" => 3,
                "RARE" => 4,
                "UNIQUE" => 5,
                "RELIC" => 6,
                "COLLECTION" => 6,
                "MYTHIC" => 7,
                _ => throw new InvalidDataException($"Unknown native item quality '{raw}'")
            };
        }

        public static int ResolveNativeItemAttributeLevel(GCObject item, int observerLevel)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            GCNode description = GCObject.ResolveItemDescription(item.GCClass)
                ?? throw new InvalidDataException($"Missing item description for '{item.GCClass}'");
            int wireLevel = item.ResolveNativeItemLevel(observerLevel);
            if (wireLevel < 0 || wireLevel > byte.MaxValue)
                throw new InvalidDataException($"Item '{item.GCClass}' has invalid wire level {wireLevel}");
            int quality = ResolveBestNativeItemQuality(item, description);
            int adjustedLevel = unchecked(wireLevel + GetNativeItemLevelDelta(quality));
            return unchecked((byte)Math.Max(1, adjustedLevel));
        }

        public static uint CalculateNativeItemValue(GCObject item, int observerLevel, bool capToObserverLevel)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            GCNode description = GCObject.ResolveItemDescription(item.GCClass)
                ?? throw new InvalidDataException($"Missing item description for '{item.GCClass}'");
            int levelOverride = RequireNativeIntProperty(description, "LevelOverride", 0);
            if (levelOverride < 0 || levelOverride > byte.MaxValue)
                throw new InvalidDataException($"Item '{item.GCClass}' has invalid LevelOverride {levelOverride}");
            bool scaleToObserverLevel = RequireNativeBoolProperty(description, "ScaleToObserverLevel", false);
            int bestQuality = ResolveBestNativeItemQuality(item, description);
            int valueLevel;
            if (scaleToObserverLevel)
            {
                valueLevel = observerLevel;
            }
            else if (levelOverride != 0)
            {
                valueLevel = levelOverride;
            }
            else
            {
                int wireLevel = item.ResolveNativeItemLevel(observerLevel);
                if (wireLevel < 0 || wireLevel > byte.MaxValue)
                    throw new InvalidDataException($"Item '{item.GCClass}' has invalid wire level {wireLevel}");
                int adjustedLevel = checked(wireLevel + GetNativeItemLevelDelta(bestQuality));
                if (adjustedLevel < 1)
                    adjustedLevel = 1;
                valueLevel = adjustedLevel & byte.MaxValue;
            }
            if (valueLevel < 1)
                valueLevel = 1;
            if (capToObserverLevel && observerLevel > 0)
                valueLevel = Math.Min(valueLevel, checked(observerLevel + 5));
            int goldValueFixed = RequireNativeFixed32Property(description, "GoldValue", 0x100);
            if (goldValueFixed < 0)
                goldValueFixed = 0;
            if (levelOverride == 0)
                goldValueFixed = checked((int)(((long)goldValueFixed * GetNativeItemPriceModifierFixed32(bestQuality)) >> 8));
            int goldPerLevelFixed = GCDatabase.Instance.GetRequiredKnobFixed32("ItemGoldValuePerLevel");
            long levelValueFixed = checked(((long)goldPerLevelFixed * checked(valueLevel * 0x100)) >> 8);
            long result = checked((levelValueFixed * goldValueFixed) >> 16);
            if (result < 1)
                result = 1;
            if (result > uint.MaxValue)
                throw new OverflowException($"Native item value exceeds UInt32 for '{item.GCClass}'");
            return (uint)result;
        }

        public static int ResolveBestNativeItemQuality(GCObject item, GCNode description)
        {
            int descriptionQuality = ResolveNativeItemQuality(description);
            if (descriptionQuality != 1)
                return descriptionQuality;
            ItemStatDatabase itemStats = ItemStatDatabase.Instance;
            if (!itemStats.IsLoaded)
                throw new InvalidDataException("Item stat database is not loaded");
            int bestQuality = 1;
            foreach (GCNode modifierDescription in itemStats.GetItemModifierDescriptions(item))
            {
                int modifierQuality = ResolveNativeItemQuality(modifierDescription);
                if (modifierQuality > bestQuality)
                    bestQuality = modifierQuality;
            }
            return bestQuality;
        }

        private static int GetNativeItemLevelDelta(int quality)
        {
            string key = quality switch
            {
                0 => "ItemLevelDeltaQuest",
                1 => "ItemLevelDeltaNormal",
                2 => "ItemLevelDeltaSuperior",
                3 => "ItemLevelDeltaMagical",
                4 => "ItemLevelDeltaRare",
                5 => "ItemLevelDeltaUnique",
                6 => "ItemLevelDeltaCollection",
                7 => "ItemLevelDeltaMythic",
                _ => throw new InvalidDataException($"Native item quality is outside the level-delta table: {quality}")
            };
            return GCDatabase.Instance.GetRequiredKnobInt(key);
        }

        private static int GetNativeItemPriceModifierFixed32(int quality)
        {
            string key = quality switch
            {
                0 => "ItemPriceModifierQuest",
                1 => "ItemPriceModifierNormal",
                2 => "ItemPriceModifierSuperior",
                3 => "ItemPriceModifierMagical",
                4 => "ItemPriceModifierRare",
                5 => "ItemPriceModifierUnique",
                6 => "ItemPriceModifierCollection",
                7 => "ItemPriceModifierMythic",
                _ => throw new InvalidDataException($"Native item quality is outside the price-modifier table: {quality}")
            };
            return GCDatabase.Instance.GetRequiredKnobFixed32(key);
        }

        private static int RequireNativeFixed32Property(GCNode node, string property, int nativeDefault)
        {
            if (!node.HasProperty(property))
                return nativeDefault;
            string raw = node.GetString(property, string.Empty);
            if (!GCNode.TryParseFixed32(raw, out int value))
                throw new InvalidDataException($"{node.CanonicalPath}.{property} is not a valid fixed32 value: '{raw}'");
            return value;
        }

        private static int RequireNativeIntProperty(GCNode node, string property, int nativeDefault)
        {
            if (!node.HasProperty(property))
                return nativeDefault;
            string raw = node.GetString(property, string.Empty).Trim();
            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
                throw new InvalidDataException($"{node.CanonicalPath}.{property} is not a valid integer: '{raw}'");
            return value;
        }

        private static bool RequireNativeBoolProperty(GCNode node, string property, bool nativeDefault)
        {
            if (!node.HasProperty(property))
                return nativeDefault;
            string raw = node.GetString(property, string.Empty).Trim();
            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1")
                return true;
            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0")
                return false;
            throw new InvalidDataException($"{node.CanonicalPath}.{property} is not a valid boolean: '{raw}'");
        }

        public static string GetRandomScaleMod(ItemRarity rarity)
        {
            string[] mods = GetAuthoredScaleMods(rarity);
            return mods[_random.Next(mods.Length)];
        }

        public static string GetDeterministicScaleMod(string gcClass, ItemRarity rarity)
        {
            string[] mods = GetAuthoredScaleMods(rarity);
            uint h = 5381;
            if (!string.IsNullOrEmpty(gcClass))
                foreach (char c in gcClass.ToLowerInvariant()) h = h * 33u + (uint)c;
            return mods[(int)(h % (uint)mods.Length)];
        }

        private static string[] GetAuthoredScaleMods(ItemRarity rarity)
        {
            lock (AuthoredScaleMods)
            {
                if (AuthoredScaleMods.TryGetValue(rarity, out string[] cached))
                    return cached;
            }

            string group = rarity switch
            {
                ItemRarity.Normal => "Binder",
                ItemRarity.Superior => "Superior",
                ItemRarity.Magical => "Magic",
                ItemRarity.Rare => "Rare",
                ItemRarity.Unique => "Unique",
                _ => throw new InvalidDataException($"No generic authored ScaleModPAL group for rarity {rarity}")
            };
            GCNode node = GCDatabase.Instance.Resolve($"ScaleModPAL.{group}");
            if (node == null)
                throw new InvalidDataException($"ScaleModPAL.{group} is not available in authored data");
            string[] resolved = node.EnumerateChildrenInOrder()
                .Where(child => child != null && !child.IsAnonymous && (child.Name ?? string.Empty).StartsWith("Mod", StringComparison.OrdinalIgnoreCase))
                .Select(child => $"ScaleModPAL.{group}.{child.Name}")
                .ToArray();
            if (resolved.Length == 0)
                throw new InvalidDataException($"ScaleModPAL.{group} has no authored modifier children");
            lock (AuthoredScaleMods)
            {
                AuthoredScaleMods[rarity] = resolved;
                return resolved;
            }
        }

        public static ItemRarity GetRandomRarity()
        {
            int totalWeight = 0;
            foreach (var (_, weight) in RarityWeights)
                totalWeight += weight;

            int roll = _random.Next(totalWeight);
            int cumulative = 0;

            foreach (var (rarity, weight) in RarityWeights)
            {
                cumulative += weight;
                if (roll < cumulative)
                    return rarity;
            }
            return ItemRarity.Rare;
        }

        public static ItemRarity GetRarityFromTier(int tier)
        {
            return tier switch
            {
                1 => ItemRarity.Normal,
                2 => ItemRarity.Superior,
                3 => ItemRarity.Magical,
                4 => ItemRarity.Rare,
                5 => ItemRarity.Unique,
                _ => throw new InvalidDataException($"Unknown authored item tier {tier}")
            };
        }

        public static ItemRarity ResolveItemRarity(string gcType)
        {
            GCNode description = GCObject.ResolveItemDescription(gcType)
                ?? throw new InvalidDataException($"Missing authored item description for '{gcType}'");
            return ResolveNativeItemQuality(description) switch
            {
                0 => ItemRarity.Normal,
                1 => ItemRarity.Normal,
                2 => ItemRarity.Superior,
                3 => ItemRarity.Magical,
                4 => ItemRarity.Rare,
                5 => ItemRarity.Unique,
                6 => ItemRarity.Unique,
                7 => ItemRarity.Mythic,
                _ => throw new InvalidDataException($"Unknown authored item quality for '{gcType}'")
            };
        }

        public static ItemRarity ResolveMerchantInventoryRarity(string generatorType, System.Random random, string gcType)
        {
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            if (generatorType.Equals("MerchantSuperiorIG", StringComparison.OrdinalIgnoreCase))
                return ItemRarity.Superior;
            if (generatorType.Equals("MerchantTrashIG", StringComparison.OrdinalIgnoreCase))
                return random.Next(2) == 0 ? ItemRarity.Superior : ItemRarity.Magical;
            if (generatorType.Equals("MerchantWeaponIG", StringComparison.OrdinalIgnoreCase)
                || generatorType.Equals("MerchantArmorIG", StringComparison.OrdinalIgnoreCase))
                return random.Next(20) == 0 ? ItemRarity.Unique : ItemRarity.Rare;
            if (generatorType.Equals("MerchantRandomIG", StringComparison.OrdinalIgnoreCase))
                return random.Next(2) == 0 ? ItemRarity.Unique : ItemRarity.Rare;
            if (generatorType.Equals("MerchantSpecialEvent01IG", StringComparison.OrdinalIgnoreCase))
            {
                int roll = random.Next(500);
                if (roll == 0)
                    return ItemRarity.Mythic;
                return roll < 100 ? ItemRarity.Unique : ItemRarity.Rare;
            }
            return ResolveItemRarity(gcType);
        }

        public static bool IsMythicPALItem(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            return gcType.IndexOf("mythicpal", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int GetTierFromGcType(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return 1;
            int dashIdx = gcType.LastIndexOf('-');
            if (dashIdx > 0 && dashIdx < gcType.Length - 1)
            {
                if (int.TryParse(gcType.Substring(dashIdx + 1), out int tier))
                    return tier;
            }
            return 1;
        }

        public static int GetItemLevel(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return 1;
            int palIdx = gcType.LastIndexOf("PAL", StringComparison.OrdinalIgnoreCase);
            if (palIdx > 0)
            {
                int numEnd = palIdx;
                int numStart = numEnd - 1;
                while (numStart >= 0 && char.IsDigit(gcType[numStart]))
                    numStart--;
                numStart++;
                if (numStart < numEnd)
                {
                    string tierStr = gcType.Substring(numStart, numEnd - numStart);
                    if (int.TryParse(tierStr, out int palTier))
                    {
                        if (palTier <= 1) return 1;
                        return (palTier - 1) * 10 + 1;
                    }
                }
            }
            return 1;
        }

        public static float GetBaseGoldValue(string gcType)
        {
            if (TryGetBaseGoldValue(gcType, out float goldValue))
                return goldValue;
            throw new InvalidDataException($"Missing authored GoldValue for item class '{gcType}'");
        }

        public static bool TryGetBaseGoldValue(string gcType, out float goldValue)
        {
            goldValue = 0;
            if (string.IsNullOrWhiteSpace(gcType))
                return false;
            var generalItem = AuthoredGameplayCatalog.FindGeneralItem(gcType);
            if (generalItem != null)
            {
                if (generalItem.GoldValueF32 > 0)
                {
                    goldValue = generalItem.GoldValueF32 / 256f;
                    return true;
                }
                if (generalItem.GcGoldValueF32 > 0)
                {
                    goldValue = generalItem.GcGoldValueF32 / 256f;
                    return true;
                }
            }
            var equipment = AuthoredGameplayCatalog.FindItem(gcType);
            if (equipment != null)
            {
                if (equipment.GoldValueF32 > 0)
                {
                    goldValue = equipment.GoldValueF32 / 256f;
                    return true;
                }
                if (equipment.GcGoldValueF32 > 0)
                {
                    goldValue = equipment.GcGoldValueF32 / 256f;
                    return true;
                }
            }
            return false;
        }

        public static uint CalculatePrice(int tier, ItemRarity rarity)
        {
            int level = tier <= 1 ? 1 : (tier - 1) * 10 + 1;
            return CalculatePriceWithGoldValue(level, rarity, 1.0f);
        }

        public static uint CalculatePriceWithGoldValue(int level, ItemRarity rarity, float goldValue, string prefix = "")
        {
            int adjustedLevel = GetEquipRequiredLevel(level, rarity);

            string goldValuePerLevelText = (!string.IsNullOrEmpty(prefix))
                ? (ServerSettings.GetString(prefix + "itemGoldValuePerLevel", null) ?? ServerSettings.GetString("itemGoldValuePerLevel", null))
                : ServerSettings.GetString("itemGoldValuePerLevel", null);
            int goldPerLevel = goldValuePerLevelText != null && float.TryParse(goldValuePerLevelText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float goldValuePerLevelParsed)
                ? (int)goldValuePerLevelParsed
                : GCDatabase.Instance.GetRequiredKnobInt("ItemGoldValuePerLevel");

            int qualityModFixed32 = GetQualityModFixed32(rarity, prefix);

            string buyModifierText = (!string.IsNullOrEmpty(prefix))
                ? (ServerSettings.GetString(prefix + "itemBuyValueModifier", null) ?? ServerSettings.GetString("itemBuyValueModifier", null))
                : ServerSettings.GetString("itemBuyValueModifier", null);
            float buyMod = buyModifierText != null && float.TryParse(buyModifierText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float buyModifierParsed)
                ? buyModifierParsed
                : GCDatabase.Instance.GetRequiredKnobFixed32("ItemBuyValueModifier") / 256f;

            int goldValueFixed32 = (int)Math.Round(goldValue * 256);
            int buyModFixed32 = (int)Math.Round(buyMod * 256);

            long numerator = (long)goldPerLevel * adjustedLevel * goldValueFixed32 * qualityModFixed32;
            long price = numerator / 65536;

            price = (price * buyModFixed32) / 256;

            return (uint)Math.Max(1, price);
        }

        private static int GetQualityModFixed32(ItemRarity rarity, string prefix = "")
        {
            string key = null;
            switch (rarity)
            {
                case ItemRarity.Normal: key = "itemPriceModifierNormal"; break;
                case ItemRarity.Superior: key = "itemPriceModifierSuperior"; break;
                case ItemRarity.Magical: key = "itemPriceModifierMagical"; break;
                case ItemRarity.Rare: key = "itemPriceModifierRare"; break;
                case ItemRarity.Unique: key = "itemPriceModifierUnique"; break;
                case ItemRarity.Mythic: key = "itemPriceModifierMythic"; break;
            }

            if (key == null)
                throw new InvalidDataException($"No client ItemPriceModifier key for rarity {rarity}");

            var globalKnobs = GCDatabase.Instance.GlobalKnobs;
            if (globalKnobs == null || !globalKnobs.HasProperty(key))
                throw new InvalidDataException($"GlobalKnobs missing client RPGSettings field {key}");

            return GCDatabase.Instance.GetRequiredKnobFixed32(key);
        }

        public static uint CalculateBuyPrice(int level, ItemRarity rarity, float goldValue)
        {
            return CalculatePriceWithGoldValue(level, rarity, goldValue);
        }

        public static uint CalculateSellPrice(int level, float goldValue, ItemRarity rarity = ItemRarity.Normal, bool isMythicPAL = false, int playerLevel = 0)
        {
            if (level < 1) level = 1;

            int sellLevel = level;
            if (playerLevel > 0)
            {
                int cap = playerLevel + 5;
                if (sellLevel > cap) sellLevel = cap;
            }
            if (isMythicPAL && playerLevel > 0)
                sellLevel = playerLevel + 5;

            int goldPerLevel = GCDatabase.Instance.GetRequiredKnobInt("ItemGoldValuePerLevel");
            string goldPerLevelText = ServerSettings.GetString("itemGoldValuePerLevel", null);
            if (goldPerLevelText != null && float.TryParse(goldPerLevelText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float configuredGoldPerLevel))
                goldPerLevel = checked((int)configuredGoldPerLevel);
            int qualityModFixed32 = GetQualityModFixed32(rarity);
            int goldValueFixed32 = (int)Math.Round(goldValue * 256);

            int gpl_Q8 = goldPerLevel * 256;
            int level_Q8 = sellLevel * 256;
            long step1 = ((long)gpl_Q8 * level_Q8) / 256;

            int modifiedGV = (int)((long)goldValueFixed32 * qualityModFixed32 / 256);
            long step2 = (step1 * modifiedGV) / 256;

            int getValueResult = (int)(step2 >> 8);
            if (getValueResult < 1) getValueResult = 1;

            long valueQ8 = (long)getValueResult * 256;

            int sellModFixed32 = GCDatabase.Instance.GetRequiredKnobFixed32("ItemSellValueModifier");
            long sellQ8 = (valueQ8 * sellModFixed32) / 256;

            int sellPrice = (int)(sellQ8 >> 8);
            return (uint)Math.Max(1, sellPrice);
        }
    }
}
