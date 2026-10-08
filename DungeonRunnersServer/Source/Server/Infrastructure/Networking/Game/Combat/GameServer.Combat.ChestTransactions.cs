using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Database;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private enum WorldChestCommitResult
        {
            Failed,
            Committed,
            AlreadyCommitted
        }

        private sealed class WorldChestDropPlan
        {
            public ushort EntityId;
            public DroppedItemInfo Info;
            public LootDrop Drop;
            public bool QuestActivateDrop;
        }

        private sealed class WorldChestActivationPlan
        {
            public string ActivationKey;
            public int EntityId;
            public QuestProgressMutation QuestMutation;
            public uint Gold;
            public bool RemoveEntity;
            public readonly List<WorldChestDropPlan> Drops = new List<WorldChestDropPlan>();
        }

        private DroppedItemInfo CreateWorldChestDroppedItemInfo(
            RRConnection conn,
            GCObject item,
            int posFixedX,
            int posFixedY,
            int posFixedZ,
            int playerLevel,
            int headingFixed,
            string questBindingKey)
        {
            return new DroppedItemInfo
            {
                Item = item,
                DbId = 0,
                Zone = conn.CurrentZoneName ?? "",
                ZoneId = conn.CurrentZoneId,
                InstanceId = conn.InstanceId,
                RuntimeInstanceKey = conn.RuntimeInstanceKey ?? "",
                PosFixedX = posFixedX,
                PosFixedY = posFixedY,
                PosFixedZ = posFixedZ,
                HeadingFixed = headingFixed,
                PlayerLevel = playerLevel,
                DroppedBy = conn.LoginName ?? "",
                OwnerCharacterId = GetCharSqlId(conn),
                OwnerGroupId = string.IsNullOrWhiteSpace(questBindingKey)
                    ? GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 0u
                    : 0u,
                OwnerName = ResolveCharacterName(conn),
                QuestBindingKey = questBindingKey ?? "",
                Quantity = 1,
                IsQuestItem = !string.IsNullOrEmpty(questBindingKey)
            };
        }

        private bool TryStageWorldChestQuestDrops(
            RRConnection conn,
            WorldEntityData chest,
            List<string> entityCandidates,
            WorldChestActivationPlan plan)
        {
            PlayerQuestState questState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
            if (questState?.ActiveQuests == null || questState.ActiveQuests.Count == 0)
                return true;
            var candidateSet = new HashSet<string>(entityCandidates, StringComparer.OrdinalIgnoreCase);
            foreach (ActiveQuest activeQuest in questState.ActiveQuests)
            {
                if (activeQuest == null
                    || !AuthoredGameplayCatalog.QuestActivateDropsByQuest.TryGetValue(activeQuest.QuestId, out List<QuestActivateDropEntry> rules))
                    continue;
                foreach (QuestActivateDropEntry rule in rules)
                {
                    if (rule == null
                        || !candidateSet.Contains(rule.EntityGcType)
                        || rule.ObjectiveIndex < 0
                        || activeQuest.Objectives == null
                        || rule.ObjectiveIndex >= activeQuest.Objectives.Count
                        || activeQuest.Objectives[rule.ObjectiveIndex].IsComplete)
                        continue;
                    string bindingKey = GetQuestActivateDropBindingKey(conn, chest, rule);
                    if (string.IsNullOrEmpty(bindingKey))
                        return false;
                    uint raw = RandomStreams.GenerateGlobalStatic(
                        "ActivateDropTrigger::doEvent.chance",
                        $"{rule.SourcePath}:{chest.Id}");
                    uint roll = raw % 100u + 1u;
                    bool passed = roll <= (uint)rule.Chance;
                    if (QuestLootTracking)
                        Debug.LogError($"[QUEST-LOOT-TRACK] source=activate-drop phase=roll characterId={GetCharSqlId(conn)} quest='{rule.QuestId}' objective={rule.ObjectiveIndex} rule='{rule.SourcePath}' entity={chest.Id} gc='{chest.GCType}' rngRaw={raw} roll={roll} chance={rule.Chance} pass={passed} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                    plan.RemoveEntity |= rule.RemoveEntity;
                    if (!passed)
                        continue;
                    string dropOwner = $"activatedrop:{chest.Id}:{rule.SourcePath}";
                    ItemDropPlacement placement = ResolveItemDropPlacement(
                        conn,
                        !string.IsNullOrWhiteSpace(chest.Zone) ? chest.Zone : conn.CurrentZoneName,
                        conn.InstanceId,
                        chest.PosFixedX,
                        chest.PosFixedY,
                        chest.PosFixedZ,
                        chest.HeadingFixed,
                        dropOwner);
                    if (!placement.Success)
                        return false;
                    int headingFixed = ConsumeItemAddToWorldHeading(dropOwner);
                    var item = new GCObject
                    {
                        GCClass = rule.ItemGcType,
                        DFCClass = ResolveAuthoredItemClass(rule.ItemGcType),
                        StoredLevel = 1
                    };
                    plan.Drops.Add(new WorldChestDropPlan
                    {
                        EntityId = 0,
                        Info = CreateWorldChestDroppedItemInfo(conn, item, placement.FixedX, placement.FixedY, placement.FixedZ, 1, headingFixed, bindingKey),
                        QuestActivateDrop = true
                    });
                }
            }
            return true;
        }

        private bool TryPrepareWorldChestActivation(
            RRConnection conn,
            WorldEntityData chest,
            out WorldChestActivationPlan plan)
        {
            plan = new WorldChestActivationPlan
            {
                ActivationKey = GetWorldEntityActivationStateKey(conn, chest),
                EntityId = chest.Id
            };
            if (string.IsNullOrEmpty(plan.ActivationKey))
                return false;
            List<string> candidates = GetWorldEntityQuestCandidates(chest);
            if (!TryStageWorldChestQuestDrops(conn, chest, candidates, plan))
                return false;
            plan.QuestMutation = QuestManager.Instance.StageEntityActivation(conn, candidates);
            PlayerState chestState = GetPlayerState(conn.ConnId.ToString());
            int chestLevel = chestState?.Level ?? 1;
            var chestDrops = new List<LootDrop>();
            foreach (var (generator, count, slot) in chest.GetChestGenerators())
            {
                List<LootDrop> slotDrops = GCObjectGeneratorTable.Instance.GenerateChestLoot(generator, count, chestLevel, !IsPlayerFree(conn.LoginName), conn.AvatarGcType);
                chestDrops.AddRange(slotDrops);
                Debug.LogError($"[CHEST-WE] {chest.Label}: slot={slot} generator={generator} count={count} drops={slotDrops.Count} sourceFunction=NonCombatInteractiveDesc-ItemGenerator1-5");
            }
            ulong totalGold = 0;
            foreach (LootDrop drop in chestDrops)
            {
                if (drop == null)
                    continue;
                if (drop.IsGold)
                {
                    if (drop.GoldAmount > 0)
                    {
                        totalGold += (uint)drop.GoldAmount;
                        if (totalGold > uint.MaxValue)
                        {
                            QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                            return false;
                        }
                    }
                    continue;
                }
                if (string.IsNullOrEmpty(drop.GCType))
                {
                    QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                    return false;
                }
                var item = new GCObject
                {
                    GCClass = drop.GCType,
                    DFCClass = ResolveAuthoredItemClass(drop.GCType),
                    PresetScaleMod = drop.ScaleMod,
                    StoredRarity = (int)drop.Rarity,
                    StoredLevel = drop.ItemLevel,
                    HasGeneratedItemState = drop.HasGeneratedItemState,
                    RolledRequiresMembership = drop.RolledRequiresMembership,
                    GeneratedRequiresMembership = drop.RequiresMembership,
                    GeneratedItemModifiers = drop.ItemModifiers == null ? new List<string>() : new List<string>(drop.ItemModifiers)
                };
                string dropOwner = $"worldentity-chest:{chest.Label}:{drop.GCType}";
                ItemDropPlacement placement = ResolveItemDropPlacement(
                    conn,
                    !string.IsNullOrWhiteSpace(chest.Zone) ? chest.Zone : conn.CurrentZoneName,
                    conn.InstanceId,
                    chest.PosFixedX,
                    chest.PosFixedY,
                    chest.PosFixedZ,
                    chest.HeadingFixed,
                    dropOwner);
                if (!placement.Success)
                {
                    QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                    return false;
                }
                int headingFixed = ConsumeItemAddToWorldHeading(dropOwner);
                plan.Drops.Add(new WorldChestDropPlan
                {
                    EntityId = 0,
                    Info = CreateWorldChestDroppedItemInfo(conn, item, placement.FixedX, placement.FixedY, placement.FixedZ, chestLevel, headingFixed, ""),
                    Drop = drop,
                    QuestActivateDrop = false
                });
            }
            plan.Gold = (uint)totalGold;
            Debug.LogError($"[CHEST-WE] {chest.Label}: totalDrops={chestDrops.Count}");
            return true;
        }

        private static string SerializeWorldChestOutcome(WorldChestActivationPlan plan)
        {
            return JsonSerializer.Serialize(new
            {
                version = 1,
                gold = plan.Gold,
                drops = plan.Drops.Select(drop => new
                {
                    gcClass = drop.Info.Item.GCClass,
                    quantity = drop.Info.Quantity,
                    posFixedX = drop.Info.PosFixedX,
                    posFixedY = drop.Info.PosFixedY,
                    posFixedZ = drop.Info.PosFixedZ,
                    headingFixed = drop.Info.HeadingFixed,
                    playerLevel = drop.Info.PlayerLevel,
                    questBindingKey = drop.Info.QuestBindingKey ?? ""
                }).ToArray()
            });
        }

        private bool TryApplyWorldChestGold(RRConnection conn, uint amount, out PlayerState playerState, out uint previousGold)
        {
            playerState = GetPlayerState(conn.ConnId.ToString());
            previousGold = playerState?.Gold ?? 0;
            if (playerState == null || amount > int.MaxValue || previousGold > (uint)int.MaxValue - amount)
                return false;
            playerState.Gold = previousGold + amount;
            return true;
        }

        private void StoreCommittedWorldChestDrops(RRConnection conn, WorldChestActivationPlan plan, IReadOnlyList<long> droppedItemIds)
        {
            bool publicZone = IsPublicZone(conn.CurrentZoneName);
            for (int index = 0; index < plan.Drops.Count; index++)
            {
                WorldChestDropPlan drop = plan.Drops[index];
                if (publicZone)
                {
                    if (droppedItemIds == null || index >= droppedItemIds.Count || droppedItemIds[index] <= 0)
                        throw new InvalidOperationException($"committed world chest drop identity is missing at index {index}");
                    drop.Info.DbId = droppedItemIds[index];
                }
                StoreDroppedItemWithLifetime(drop.EntityId, drop.Info);
                if (drop.Info.DbId > 0)
                    _dbIdToEntityId[drop.Info.DbId] = drop.EntityId;
                if (!string.IsNullOrEmpty(drop.Info.QuestBindingKey))
                    RegisterQuestActivateDropBinding(drop.EntityId, drop.Info.QuestBindingKey);
            }
        }

        private WorldChestCommitResult CommitWorldChestActivationPlan(RRConnection conn, WorldChestActivationPlan plan)
        {
            PlayerState playerState = null;
            uint previousGold = 0;
            bool durableCommitted = false;
            try
            {
                if (!TryApplyWorldChestGold(conn, plan.Gold, out playerState, out previousGold))
                {
                    QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                    return WorldChestCommitResult.Failed;
                }
                if (!TryCaptureFullCharacterSnapshot(conn, "world-chest-activation", out SavedCharacter savedCharacter))
                {
                    playerState.Gold = previousGold;
                    QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                    return WorldChestCommitResult.Failed;
                }
                if (!_selectedCharacter.TryGetValue(conn.LoginName, out GCObject selected)
                    || selected == null || selected.Id != savedCharacter.id)
                {
                    playerState.Gold = previousGold;
                    QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                    return WorldChestCommitResult.Failed;
                }

                IReadOnlyList<long> droppedItemIds = Array.Empty<long>();
                if (IsPublicZone(conn.CurrentZoneName))
                {
                    var writes = new List<CharacterRepository.WorldActivationDropWrite>(plan.Drops.Count);
                    for (int index = 0; index < plan.Drops.Count; index++)
                    {
                        DroppedItemInfo info = plan.Drops[index].Info;
                        writes.Add(new CharacterRepository.WorldActivationDropWrite
                        {
                            Zone = info.Zone,
                            ZoneId = info.ZoneId,
                            InstanceId = info.InstanceId,
                            Item = info.Item,
                            PosFixedX = info.PosFixedX,
                            PosFixedY = info.PosFixedY,
                            PosFixedZ = info.PosFixedZ,
                            HeadingFixed = info.HeadingFixed,
                            PlayerLevel = info.PlayerLevel,
                            Quantity = info.Quantity,
                            DroppedBy = info.DroppedBy,
                            OwnerCharacterId = info.OwnerCharacterId,
                            OwnerGroupId = info.OwnerGroupId,
                            OwnerName = info.OwnerName,
                            QuestBindingKey = info.QuestBindingKey ?? ""
                        });
                    }
                    if (!CharacterRepository.TrySaveCharacterCreatingWorldActivation(
                            savedCharacter,
                            plan.ActivationKey,
                            conn.CurrentZoneName,
                            conn.CurrentZoneId,
                            conn.InstanceId,
                            plan.EntityId,
                            SerializeWorldChestOutcome(plan),
                            writes,
                            out List<long> committedDropIds,
                            out bool alreadyCommitted,
                            "world-chest-activation"))
                    {
                        playerState.Gold = previousGold;
                        QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                        return WorldChestCommitResult.Failed;
                    }
                    if (alreadyCommitted)
                    {
                        playerState.Gold = previousGold;
                        QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                        return WorldChestCommitResult.AlreadyCommitted;
                    }
                    droppedItemIds = committedDropIds;
                }
                else if (!CharacterRepository.TrySaveCharacter(savedCharacter, "world-chest-activation-room"))
                {
                    playerState.Gold = previousGold;
                    QuestManager.Instance.RollbackProgress(plan.QuestMutation);
                    return WorldChestCommitResult.Failed;
                }

                durableCommitted = true;
                if (IsPublicZone(conn.CurrentZoneName)
                    && (droppedItemIds == null || droppedItemIds.Count != plan.Drops.Count))
                    throw new InvalidOperationException("world chest drop identity count mismatch");
                for (int dropIndex = 0; dropIndex < plan.Drops.Count; dropIndex++)
                {
                    if (IsPublicZone(conn.CurrentZoneName))
                    {
                        if (droppedItemIds[dropIndex] <= 0)
                            throw new InvalidOperationException("world chest drop identity is invalid");
                        plan.Drops[dropIndex].Info.DbId = droppedItemIds[dropIndex];
                    }
                    plan.Drops[dropIndex].EntityId = GetNextLootEntityId();
                }
                _activeCharacter[conn.LoginName] = savedCharacter;
                playerState.Gold = savedCharacter.gold;
                StoreCommittedWorldChestDrops(conn, plan, droppedItemIds);
                return WorldChestCommitResult.Committed;
            }
            catch (Exception ex)
            {
                if (durableCommitted)
                {
                    QuarantineCommittedPersistenceSyncFailure(conn, "world-chest-activation", ex);
                    return WorldChestCommitResult.Committed;
                }
                if (playerState != null)
                    playerState.Gold = previousGold;
                QuestManager.Instance.RollbackProgress(plan?.QuestMutation);
                Debug.LogError($"[CHEST-WE] state=failed phase=transaction-exception entity={plan?.EntityId ?? 0} message='{ex.Message}'");
                return WorldChestCommitResult.Failed;
            }
        }

        private void SendCommittedWorldChestGold(RRConnection conn, uint amount, string source)
        {
            if (amount == 0 || conn.UnitContainerId == 0)
                return;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.UnitContainerId);
            writer.WriteByte(0x20);
            writer.WriteUInt32(amount);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x01);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            SendToClient(conn, writer.ToArray());
            Debug.LogError($"[GIVE-GOLD] source={source ?? "world-chest"} +{amount} gold sent to client");
        }

        private void QueueCommittedWorldChestActivationPackets(
            RRConnection conn,
            ushort componentId,
            ushort targetEntityId,
            byte responseId,
            byte sessionId,
            WorldEntityData chest)
        {
            var ackMessage = new LEWriter();
            ackMessage.WriteByte(0x35);
            ackMessage.WriteUInt16(componentId);
            ackMessage.WriteByte(0x01);
            ackMessage.WriteByte(responseId);
            ackMessage.WriteByte(0x06);
            ackMessage.WriteByte(sessionId);
            ackMessage.WriteUInt16(targetEntityId);
            WritePlayerEntitySynch(conn, ackMessage);
            conn.MessageQueue.Enqueue(ackMessage.ToArray());

            var nonCombatInteractiveMessage = new LEWriter();
            nonCombatInteractiveMessage.WriteByte(0x03);
            nonCombatInteractiveMessage.WriteUInt16(targetEntityId);
            nonCombatInteractiveMessage.WriteByte(0x0A);
            nonCombatInteractiveMessage.WriteUInt32(0x00000000);
            WriteNonCombatInteractiveEntitySynchInfo(nonCombatInteractiveMessage, chest.GCType);
            conn.MessageQueue.Enqueue(nonCombatInteractiveMessage.ToArray());
        }

        private void SendCommittedWorldChestDrops(RRConnection conn, WorldChestActivationPlan plan, bool questDrops)
        {
            for (int index = 0; index < plan.Drops.Count; index++)
            {
                WorldChestDropPlan drop = plan.Drops[index];
                if (drop.QuestActivateDrop != questDrops || !_droppedItems.TryGetValue(drop.EntityId, out DroppedItemInfo info))
                    continue;
                BroadcastDroppedItemSpawnPacket(conn, drop.EntityId, info);
                if (!questDrops && drop.Drop != null)
                    Debug.LogError($"[CHEST-WE] {drop.Drop.Label} ({drop.Drop.Rarity}) from committed chest");
            }
        }

        private void ExecuteWorldEntityChestActivationTransaction(
            RRConnection conn,
            ushort componentId,
            ushort targetEntityId,
            byte responseId,
            byte sessionId,
            WorldEntityData chest)
        {
            if (IsPublicZone(conn.CurrentZoneName) && chest.AllowMultiple)
            {
                Debug.LogError($"[CHEST-WE] state=blocked phase=idempotency reason=repeatable-public-activation-unresolved entity={chest.Id} gc='{chest.GCType}' instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                return;
            }
            string activationKey = GetWorldEntityActivationStateKey(conn, chest);
            if (string.IsNullOrEmpty(activationKey))
                return;
            if (IsPublicZone(conn.CurrentZoneName))
            {
                if (!CharacterRepository.TryIsWorldEntityActivationCommitted(activationKey, out bool alreadyCommitted))
                {
                    Debug.LogError($"[CHEST-WE] state=blocked phase=idempotency-read activation='{activationKey}'");
                    return;
                }
                if (alreadyCommitted)
                {
                    AddOrderedWorldEntityState(_activatedWorldEntities, _activatedWorldEntityOrder, activationKey);
                    QueueCommittedWorldChestActivationPackets(conn, componentId, targetEntityId, responseId, sessionId, chest);
                    if (!chest.AllowMultiple)
                        DespawnWorldEntityForInstance(conn, chest);
                    return;
                }
            }
            RandomStreams.GlobalStaticTransaction rngTransaction;
            try
            {
                rngTransaction = RandomStreams.BeginGlobalStaticTransaction("world-chest-activation");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHEST-WE] state=blocked phase=rng-transaction-begin entity={chest.Id} errorType={ex.GetType().Name} message='{ex.Message}'");
                return;
            }
            try
            {
                if (!TryReserveWorldEntityActivation(conn, chest))
                {
                    if (QuestLootTracking)
                        Debug.LogError($"[QUEST-LOOT-TRACK] source=world-chest phase=rejected reason=consumed-or-reserved characterId={GetCharSqlId(conn)} entity={chest.Id} gc='{chest.GCType}' instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                    return;
                }
                WorldChestActivationPlan plan = null;
                try
                {
                    if (!TryPrepareWorldChestActivation(conn, chest, out plan))
                    {
                        QuestManager.Instance.RollbackProgress(plan?.QuestMutation);
                        ReleaseWorldEntityActivation(conn, chest);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    QuestManager.Instance.RollbackProgress(plan?.QuestMutation);
                    ReleaseWorldEntityActivation(conn, chest);
                    Debug.LogError($"[CHEST-WE] state=failed phase=prepare-exception entity={chest.Id} message='{ex.Message}'");
                    return;
                }
                WorldChestCommitResult result = CommitWorldChestActivationPlan(conn, plan);
                if (result == WorldChestCommitResult.Failed)
                {
                    ReleaseWorldEntityActivation(conn, chest);
                    return;
                }
                if (result == WorldChestCommitResult.AlreadyCommitted)
                {
                    CommitWorldEntityActivation(conn, chest);
                    QueueCommittedWorldChestActivationPackets(conn, componentId, targetEntityId, responseId, sessionId, chest);
                    if (!chest.AllowMultiple)
                        DespawnWorldEntityForInstance(conn, chest);
                    return;
                }
                rngTransaction.Commit();
                if (!CommitWorldEntityActivation(conn, chest))
                    AddOrderedWorldEntityState(_activatedWorldEntities, _activatedWorldEntityOrder, activationKey);

                SendCommittedWorldChestDrops(conn, plan, true);
                QuestManager.Instance.CommitProgress(conn, plan.QuestMutation);
                if (plan.QuestMutation.Updates.Count > 0 && QuestLootTracking)
                    Debug.LogError($"[QUEST-LOOT-TRACK] source=entity-activate characterId={GetCharSqlId(conn)} entity={chest.Id} gc='{chest.GCType}' updates={plan.QuestMutation.Updates.Count} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' dbCommit=true");
                SendCommittedWorldChestGold(conn, plan.Gold, $"worldentity-chest:{chest.GCType}");
                if (QuestLootTracking)
                    Debug.LogError($"[QUEST-LOOT-TRACK] source=world-chest-gold characterId={GetCharSqlId(conn)} entity={chest.Id} gc='{chest.GCType}' quantity={plan.Gold} dbCommit=true instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                QueueCommittedWorldChestActivationPackets(conn, componentId, targetEntityId, responseId, sessionId, chest);
                SendCommittedWorldChestDrops(conn, plan, false);
                if (!chest.AllowMultiple || plan.RemoveEntity)
                    DespawnWorldEntityForInstance(conn, chest);
                Debug.LogError($"[WORLD-ENTITY] Sent NCI activate (0x03/0x0A) for {chest.EntityType}: {chest.Label}");
            }
            finally
            {
                rngTransaction.Dispose();
            }
        }
    }
}
