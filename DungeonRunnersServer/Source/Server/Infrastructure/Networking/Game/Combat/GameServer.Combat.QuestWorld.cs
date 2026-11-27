using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Core;
using DungeonRunners.Data;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;
using DungeonRunners.Combat;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private static bool QuestLootTracking => ServerDiagnostics.IsEnabled("questLootTracking");

        private string GetWorldEntityActivationStateKey(RRConnection conn, WorldEntityData entity)
        {
            if (conn == null || entity == null)
                return string.Empty;
            string instanceKey = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn));
            return $"{instanceKey}|{entity.Zone}|{entity.Id}";
        }

        private bool IsWorldEntityActivationConsumed(RRConnection conn, WorldEntityData entity)
        {
            string key = GetWorldEntityActivationStateKey(conn, entity);
            return !string.IsNullOrEmpty(key) && _activatedWorldEntities.Contains(key);
        }

        private static bool AddOrderedWorldEntityState(HashSet<string> values, List<string> order, string key)
        {
            if (!values.Add(key))
                return false;
            order.Add(key);
            return true;
        }

        private static bool RemoveOrderedWorldEntityState(HashSet<string> values, List<string> order, string key)
        {
            if (!values.Remove(key))
                return false;
            order.Remove(key);
            return true;
        }

        private bool TryReserveWorldEntityActivation(RRConnection conn, WorldEntityData entity)
        {
            if (entity == null || entity.AllowMultiple)
                return true;
            string key = GetWorldEntityActivationStateKey(conn, entity);
            return !string.IsNullOrEmpty(key)
                && !_activatedWorldEntities.Contains(key)
                && AddOrderedWorldEntityState(_reservedWorldEntityActivations, _reservedWorldEntityActivationOrder, key);
        }

        private bool CommitWorldEntityActivation(RRConnection conn, WorldEntityData entity)
        {
            if (entity == null || entity.AllowMultiple)
                return true;
            string key = GetWorldEntityActivationStateKey(conn, entity);
            if (string.IsNullOrEmpty(key))
                return false;
            RemoveOrderedWorldEntityState(_reservedWorldEntityActivations, _reservedWorldEntityActivationOrder, key);
            AddOrderedWorldEntityState(_activatedWorldEntities, _activatedWorldEntityOrder, key);
            return true;
        }

        private void ReleaseWorldEntityActivation(RRConnection conn, WorldEntityData entity)
        {
            if (entity == null || entity.AllowMultiple)
                return;
            string key = GetWorldEntityActivationStateKey(conn, entity);
            if (!string.IsNullOrEmpty(key))
                RemoveOrderedWorldEntityState(_reservedWorldEntityActivations, _reservedWorldEntityActivationOrder, key);
        }

        private string GetQuestActivateDropBindingKey(RRConnection conn, WorldEntityData entity, QuestActivateDropEntry rule)
        {
            uint characterId = GetCharSqlId(conn);
            if (characterId == 0 || entity == null || rule == null)
                return string.Empty;
            string instanceKey = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn));
            return $"{instanceKey}|{characterId}|{rule.QuestId}|{rule.ObjectiveIndex}|{entity.Zone}|{entity.Id}|{rule.SourcePath}";
        }

        private void RegisterQuestActivateDropBinding(ushort dropEntityId, string bindingKey)
        {
            ReleaseQuestActivateDropBinding(dropEntityId);
            if (string.IsNullOrEmpty(bindingKey))
                return;
            AddOrderedWorldEntityState(_outstandingQuestActivateDrops, _outstandingQuestActivateDropOrder, bindingKey);
            _questActivateDropBindings[dropEntityId] = bindingKey;
            _questActivateDropBindingOrder.Add(dropEntityId);
        }

        private void ReleaseQuestActivateDropBinding(ushort dropEntityId)
        {
            if (!_questActivateDropBindings.TryGetValue(dropEntityId, out string bindingKey))
                return;
            _questActivateDropBindings.Remove(dropEntityId);
            _questActivateDropBindingOrder.Remove(dropEntityId);
            bool stillOutstanding = false;
            for (int index = 0; index < _questActivateDropBindingOrder.Count; index++)
            {
                ushort otherEntityId = _questActivateDropBindingOrder[index];
                if (_questActivateDropBindings.TryGetValue(otherEntityId, out string otherBindingKey)
                    && string.Equals(bindingKey, otherBindingKey, StringComparison.OrdinalIgnoreCase))
                {
                    stillOutstanding = true;
                    break;
                }
            }
            if (!stillOutstanding)
                RemoveOrderedWorldEntityState(_outstandingQuestActivateDrops, _outstandingQuestActivateDropOrder, bindingKey);
        }

        private void ClearWorldEntityActivationStateForInstance(string instanceKey)
        {
            string normalized = RoomRuntime.NormalizeInstanceKey(instanceKey);
            if (string.IsNullOrEmpty(normalized))
                return;
            string prefix = normalized + "|";
            for (int index = _activatedWorldEntityOrder.Count - 1; index >= 0; index--)
            {
                string key = _activatedWorldEntityOrder[index];
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    RemoveOrderedWorldEntityState(_activatedWorldEntities, _activatedWorldEntityOrder, key);
            }
            for (int index = _reservedWorldEntityActivationOrder.Count - 1; index >= 0; index--)
            {
                string key = _reservedWorldEntityActivationOrder[index];
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    RemoveOrderedWorldEntityState(_reservedWorldEntityActivations, _reservedWorldEntityActivationOrder, key);
            }
            for (int index = _questActivateDropBindingOrder.Count - 1; index >= 0; index--)
            {
                ushort dropEntityId = _questActivateDropBindingOrder[index];
                if (_questActivateDropBindings.TryGetValue(dropEntityId, out string key)
                    && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    ReleaseQuestActivateDropBinding(dropEntityId);
            }
            for (int index = _outstandingQuestActivateDropOrder.Count - 1; index >= 0; index--)
            {
                string key = _outstandingQuestActivateDropOrder[index];
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    RemoveOrderedWorldEntityState(_outstandingQuestActivateDrops, _outstandingQuestActivateDropOrder, key);
            }
        }

        private void DespawnWorldEntityForInstance(RRConnection source, WorldEntityData entity)
        {
            if (source == null || entity == null)
                return;
            string sourceInstance = RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(source));
            foreach (RRConnection viewer in GetConnectionInsertionOrderSnapshot())
            {
                if (viewer == null || !viewer.IsSpawned)
                    continue;
                if (!string.Equals(sourceInstance, RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(viewer)), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!_worldEntityIdsByConn.TryGetValue(viewer.ConnId, out Dictionary<int, ushort> ids)
                    || !ids.TryGetValue(entity.Id, out ushort runtimeEntityId))
                    continue;
                SendDespawnEntity(viewer, runtimeEntityId);
                ids.Remove(entity.Id);
                if (_worldEntityRuntimeOrderByConn.TryGetValue(viewer.ConnId, out List<ushort> runtimeOrder))
                    runtimeOrder.Remove(runtimeEntityId);
                WorldEntitySpawner.Instance.RemoveEntity(runtimeEntityId);
            }
        }

        private List<string> GetWorldEntityQuestCandidates(WorldEntityData entity)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string value)
            {
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                    candidates.Add(value);
            }

            Add(entity?.GCType);
            if (!string.IsNullOrWhiteSpace(entity?.GCType))
            {
                foreach (string path in GCDatabase.Instance.GetInheritanceChainPaths(entity.GCType))
                    Add(path);
            }
            return candidates;
        }

        private bool HandleQuestWorldEntityActivation(RRConnection conn, WorldEntityData entity, bool deferEntityRemoval = false)
        {
            if (conn == null || entity == null)
                return false;
            List<string> candidates = GetWorldEntityQuestCandidates(entity);
            bool removeEntity = ExecuteQuestActivateDrops(conn, entity, candidates, out List<ushort> spawnedDropEntityIds);
            QuestProgressMutation mutation = QuestManager.Instance.StageEntityActivation(conn, candidates);
            if (mutation.Updates.Count > 0 && !SavePlayerQuests(conn))
            {
                QuestManager.Instance.RollbackProgress(mutation);
                for (int spawnedIndex = 0; spawnedIndex < spawnedDropEntityIds.Count; spawnedIndex++)
                {
                    ushort spawnedEntityId = spawnedDropEntityIds[spawnedIndex];
                    if (!RollbackTrackedDroppedItem(spawnedEntityId, "quest-entity-activation")
                        && _droppedItems.TryGetValue(spawnedEntityId, out DroppedItemInfo retainedInfo))
                        BroadcastDroppedItemSpawnPacket(conn, spawnedEntityId, retainedInfo);
                }
                if (QuestLootTracking)
                    Debug.LogError($"[QUEST-LOOT-TRACK] source=entity-activate characterId={GetCharSqlId(conn)} entity={entity.Id} gc='{entity.GCType}' updates={mutation.Updates.Count} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' dbCommit=false");
                return false;
            }
            for (int spawnedIndex = 0; spawnedIndex < spawnedDropEntityIds.Count; spawnedIndex++)
            {
                ushort spawnedEntityId = spawnedDropEntityIds[spawnedIndex];
                if (_droppedItems.TryGetValue(spawnedEntityId, out DroppedItemInfo info))
                    BroadcastDroppedItemSpawnPacket(conn, spawnedEntityId, info);
            }
            QuestManager.Instance.CommitProgress(conn, mutation);
            if (mutation.Updates.Count > 0 && QuestLootTracking)
                Debug.LogError($"[QUEST-LOOT-TRACK] source=entity-activate characterId={GetCharSqlId(conn)} entity={entity.Id} gc='{entity.GCType}' updates={mutation.Updates.Count} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}' dbCommit=true");
            if (removeEntity && !deferEntityRemoval)
            {
                string activationKey = GetWorldEntityActivationStateKey(conn, entity);
                if (!string.IsNullOrEmpty(activationKey))
                    AddOrderedWorldEntityState(_activatedWorldEntities, _activatedWorldEntityOrder, activationKey);
                DespawnWorldEntityForInstance(conn, entity);
            }
            return true;
        }

        private bool ExecuteQuestActivateDrops(RRConnection conn, WorldEntityData entity, List<string> entityCandidates, out List<ushort> spawnedDropEntityIds)
        {
            spawnedDropEntityIds = new List<ushort>();
            PlayerQuestState questState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
            if (questState?.ActiveQuests == null || questState.ActiveQuests.Count == 0)
                return false;
            var candidateSet = new HashSet<string>(entityCandidates, StringComparer.OrdinalIgnoreCase);
            bool removeEntity = false;
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
                    string bindingKey = GetQuestActivateDropBindingKey(conn, entity, rule);
                    if (string.IsNullOrEmpty(bindingKey))
                    {
                        Debug.LogError($"[QUEST-ACTIVATE-DROP] state=blocked reason=missing-character-id quest='{rule.QuestId}' rule='{rule.SourcePath}' entity={entity.Id}");
                        continue;
                    }
                    uint raw = RandomStreams.GenerateGlobalStatic(
                        "ActivateDropTrigger::doEvent.chance",
                        $"{rule.SourcePath}:{entity.Id}");
                    uint roll = raw % 100u + 1u;
                    bool passed = roll <= (uint)rule.Chance;
                    if (QuestLootTracking)
                        Debug.LogError($"[QUEST-LOOT-TRACK] source=activate-drop phase=roll characterId={GetCharSqlId(conn)} quest='{rule.QuestId}' objective={rule.ObjectiveIndex} rule='{rule.SourcePath}' entity={entity.Id} gc='{entity.GCType}' rngRaw={raw} roll={roll} chance={rule.Chance} pass={passed} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                    removeEntity |= rule.RemoveEntity;
                    if (!passed)
                        continue;
                    string dropOwner = $"activatedrop:{entity.Id}:{rule.SourcePath}";
                    var placement = ResolveItemDropPlacement(
                        conn,
                        !string.IsNullOrWhiteSpace(entity.Zone) ? entity.Zone : conn.CurrentZoneName,
                        conn.InstanceId,
                        entity.PosFixedX,
                        entity.PosFixedY,
                        entity.PosFixedZ,
                        entity.HeadingFixed,
                        dropOwner);
                    if (!placement.Success)
                    {
                        Debug.LogError($"[QUEST-ACTIVATE-DROP] state=blocked reason=missing-pathmap quest='{rule.QuestId}' rule='{rule.SourcePath}' entity={entity.Id}");
                        continue;
                    }
                    int headingFixed = ConsumeItemAddToWorldHeading(dropOwner);
                    var item = new GCObject
                    {
                        GCClass = rule.ItemGcType,
                        DFCClass = ResolveAuthoredItemClass(rule.ItemGcType),
                        StoredLevel = 1
                    };
                    ushort entityId = GetNextLootEntityId();
                    if (!TrackDroppedItem(entityId, item, conn, 1, placement.FixedX, placement.FixedY, placement.FixedZ, 1, headingFixed, requirePublicPersistence: true, questBindingKey: bindingKey))
                    {
                        Debug.LogError($"[QUEST-ACTIVATE-DROP] state=blocked reason=drop-persistence quest='{rule.QuestId}' rule='{rule.SourcePath}' entity={entity.Id}");
                        continue;
                    }
                    if (!_droppedItems.TryGetValue(entityId, out DroppedItemInfo info))
                        throw new InvalidDataException($"Quest activate drop entity {entityId} was not tracked for '{rule.SourcePath}'");
                    info.IsQuestItem = true;
                    RegisterQuestActivateDropBinding(entityId, bindingKey);
                    spawnedDropEntityIds.Add(entityId);
                    if (QuestLootTracking)
                        Debug.LogError($"[QUEST-LOOT-TRACK] source=activate-drop phase=spawn characterId={GetCharSqlId(conn)} quest='{rule.QuestId}' objective={rule.ObjectiveIndex} rule='{rule.SourcePath}' item='{rule.ItemGcType}' quantity=1 ownerCharacter={info.OwnerCharacterId} dropEntity={entityId} removeEntity={rule.RemoveEntity} instance='{RoomRuntime.NormalizeInstanceKey(GetInstanceZoneKey(conn))}'");
                }
            }
            return removeEntity;
        }
    }
}
