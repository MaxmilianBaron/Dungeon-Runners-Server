using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Combat;
using DungeonRunners.Database;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Gameplay;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private void HandleQuestRequest(RRConnection conn, byte request, LEReader reader)
        {
            string connId = conn.ConnId.ToString();
            switch (request)
            {
                case 0x01:
                    if (reader.Remaining != 0)
                        return;
                    uint acceptedHash = conn.PendingQuestHash;
                    uint acceptedNpc = conn.PendingQuestNpcEntityId;
                    if (TryResolveQuestGiver(conn, acceptedNpc, acceptedHash, out ZoneNPC giver))
                        TryAcceptQuestFromGiver(conn, acceptedHash, giver, true);
                    return;
                case 0x02:
                    if (reader.Remaining == 0 && conn.PendingQuestHash != 0
                        && QuestManager.Instance.SendClearQueryResponse(conn))
                        ClearPendingQuest(conn);
                    return;
                case 0x03:
                    if (reader.Remaining == 4)
                        TryAbandonQuest(conn, reader.ReadUInt32());
                    return;
                case 0x04:
                    if (reader.Remaining != 4)
                        return;
                    ActiveQuest query = QuestManager.Instance.GetQuestByInstanceId(connId, reader.ReadUInt32());
                    if (QuestManager.Instance.CanQueryComplete(connId, query)
                        && TryFindQuestTurnInNpc(conn, query, out _))
                        QuestManager.Instance.SendTurnInDialog(conn, query.InstanceId);
                    return;
                case 0x05:
                    if (reader.Remaining != 5)
                        return;
                    uint instanceId = reader.ReadUInt32();
                    byte rewardChoice = reader.ReadByte();
                    ActiveQuest active = QuestManager.Instance.GetQuestByInstanceId(connId, instanceId);
                    if (!QuestManager.Instance.CanQueryComplete(connId, active)
                        || !TryFindQuestTurnInNpc(conn, active, out ZoneNPC turnInNpc))
                        return;
                    QuestData definition = QuestManager.FindDefinition(active.QuestId);
                    bool retainCompletion = !definition.repeatable || definition.minRepeatTimeSeconds != 0;
                    TryCommitQuestTurnIn(conn, instanceId, retainCompletion, definition, rewardChoice, out _, turnInNpc);
                    return;
                case 0x06:
                    if (reader.Remaining < 5)
                        return;
                    uint entityId = reader.ReadUInt32();
                    uint questHash = GCTypeCodec.ReadTypeId(reader);
                    if (reader.Remaining == 0 && TryResolveQuestGiver(conn, entityId, questHash, out ZoneNPC questGiver))
                        QueryQuestFromGiver(conn, questHash, questGiver);
                    return;
            }
        }

        private static void ClearPendingQuest(RRConnection conn)
        {
            conn.PendingQuestHash = 0;
            conn.PendingQuestNpcEntityId = 0;
            conn.PendingTurnInInstanceId = 0;
        }

        private void QueryQuestFromGiver(RRConnection conn, uint questHash, ZoneNPC questGiver)
        {
            if (!AuthoredGameplayCatalog.QuestsByHash.TryGetValue(questHash, out QuestData quest)
                || !QuestManager.Instance.CanOfferQuest(conn.ConnId.ToString(), quest))
                return;
            ClearPendingQuest(conn);
            if (quest.autoAcceptOnQuery)
                TryAcceptQuestFromGiver(conn, questHash, questGiver, false);
            else
                QuestManager.Instance.SendQueryResponse(conn, questHash, questGiver.Id);
        }

        private bool TryResolveQuestGiver(RRConnection conn, uint entityId, uint questHash, out ZoneNPC questGiver)
        {
            questGiver = FindZoneNpcByEntityId(conn, entityId);
            if (questGiver == null || !questGiver.HasFixedPosition
                || !AuthoredGameplayCatalog.QuestsByHash.TryGetValue(questHash, out QuestData quest)
                || !quest.offeringNpcs.Contains(questGiver.GCClass, StringComparer.OrdinalIgnoreCase))
                return false;
            ResolveAuthoritativePlayerPositionFixed(conn, out int x, out int y, out _);
            return QuestRules.IsWithinNpcRange(x, y, 0, questGiver.PosFixedX, questGiver.PosFixedY, 0, false);
        }

        private bool TryFindQuestTurnInNpc(RRConnection conn, ActiveQuest quest, out ZoneNPC npc)
        {
            npc = null;
            QuestData definition = QuestManager.FindDefinition(quest?.QuestId);
            if (definition == null || !TryGetZoneNpcsForConnection(conn, out List<ZoneNPC> npcs))
                return false;
            ResolveAuthoritativePlayerPositionFixed(conn, out int x, out int y, out int z);
            foreach (string npcType in definition.npcs)
            {
                ZoneNPC candidate = npcs.FirstOrDefault(entry => QuestRules.IsGcType(entry.GCClass, npcType));
                if (candidate != null && candidate.HasFixedPosition
                    && QuestRules.IsWithinNpcRange(x, y, z, candidate.PosFixedX, candidate.PosFixedY, candidate.PosFixedZ, true))
                {
                    npc = candidate;
                    return true;
                }
            }
            return false;
        }

        private void TryAcceptQuestFromGiver(RRConnection conn, uint questHash, ZoneNPC giver, bool explicitAccept)
        {
            if (!AuthoredGameplayCatalog.QuestsByHash.TryGetValue(questHash, out QuestData definition)
                || !QuestManager.Instance.CanOfferQuest(conn.ConnId.ToString(), definition))
                return;
            PlayerQuestState state = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
            uint previousId = conn.NextQuestInstanceId;
            ActiveQuest active = null;
            bool durableCommitted = false;
            using (RandomStreams.GlobalStaticTransaction random = RandomStreams.BeginGlobalStaticTransaction("quest-accept"))
            {
                try
                {
                    if (!explicitAccept || definition.addOnAccept)
                    {
                        QuestAcceptResult accepted = QuestManager.Instance.AcceptQuest(state.ConnId, definition.id, giver.GCClass);
                        if (!accepted.Success)
                            return;
                        active = accepted.Quest;
                        active.InstanceId = conn.NextQuestInstanceId++;
                        InitializeQuestWorldObjectives(conn, active);
                    }
                    QuestAcceptItemCommit itemCommit = null;
                    bool committed = explicitAccept && !string.IsNullOrEmpty(definition.onAcceptItem)
                        ? TryCommitOnAcceptItem(conn, definition, out itemCommit)
                        : SavePlayerQuests(conn);
                    if (!committed)
                        return;
                    durableCommitted = true;
                    random.Commit();
                    try
                    {
                        if (explicitAccept && !QuestManager.Instance.SendClearQueryResponse(conn))
                            throw new InvalidOperationException("Committed quest accept could not clear the client query");
                        ClearPendingQuest(conn);
                        if (active != null)
                            QuestManager.Instance.SendAddPacket(conn, definition, active);
                        if (itemCommit != null)
                            MaterializeCommittedOnAcceptItem(conn, itemCommit);
                        QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);
                        if (definition.autoAcceptOnQuery && active != null
                            && QuestManager.Instance.CanQueryComplete(state.ConnId, active)
                            && TryFindQuestTurnInNpc(conn, active, out _))
                            QuestManager.Instance.SendTurnInDialog(conn, active.InstanceId);
                    }
                    catch (Exception ex)
                    {
                        QuarantineCommittedPersistenceSyncFailure(conn, "quest-accept-materialization", ex);
                    }
                }
                catch (Exception ex)
                {
                    if (durableCommitted)
                        QuarantineCommittedPersistenceSyncFailure(conn, "quest-accept", ex);
                    else
                        Debug.LogError($"[QUEST-ACCEPT-ITEM] state=failed phase=accept errorType={ex.GetType().Name}");
                }
                finally
                {
                    if (!durableCommitted)
                    {
                        if (active != null)
                            state.ActiveQuests.Remove(active);
                        conn.NextQuestInstanceId = previousId;
                    }
                }
            }
        }

        private void TryAbandonQuest(RRConnection conn, uint instanceId)
        {
            PlayerQuestState state = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
            int index = state?.ActiveQuests.FindIndex(quest => quest.InstanceId == instanceId) ?? -1;
            if (index < 0)
                return;
            ActiveQuest quest = state.ActiveQuests[index];
            QuestData definition = QuestManager.FindDefinition(quest.QuestId);
            bool permanent = definition?.permanentAbandon == true;
            state.ActiveQuests.RemoveAt(index);
            if (permanent)
            {
                state.CompletedQuests.Add(quest.QuestId);
                state.CompletedAtUnixSeconds[quest.QuestId] = new DateTimeOffset(quest.AcceptedAt).ToUnixTimeSeconds();
            }
            if (!SavePlayerQuests(conn))
            {
                state.ActiveQuests.Insert(index, quest);
                if (permanent)
                {
                    state.CompletedQuests.Remove(quest.QuestId);
                    state.CompletedAtUnixSeconds.Remove(quest.QuestId);
                }
                return;
            }
            ClearPendingQuest(conn);
            QuestManager.Instance.SendRemovePacket(conn, instanceId);
            QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);
        }

        private int CountQuestItems(string connId, QuestObjective objective)
        {
            if (!_playerInventoryItems.TryGetValue(connId, out var inventory)
                || !_playerInventoryOrder.TryGetValue(connId, out List<uint> order))
                return 0;
            int quantity = 0;
            foreach (uint slot in order)
                if (inventory.TryGetValue(slot, out var entry) && QuestItemMatches(entry.item, objective))
                    quantity = checked(quantity + Math.Max(1, GetStackCount(connId, slot)));
            return quantity;
        }

        private static bool QuestItemMatches(GCObject item, QuestObjective objective)
        {
            return item != null && QuestRules.IsGcType(item.GCClass, objective.target)
                && (objective.itemLevel == byte.MaxValue
                    || RPGSettings.ResolveBestNativeItemQuality(item, GCDatabase.Instance.ResolveWithInheritance(item.GCClass)?.GetChild("Description")) == objective.itemLevel);
        }

        private int GetQuestItemRequiredQuantity(string connId, QuestObjective objective)
        {
            if (!QuestRules.IsGcType(objective.target, "QuestItemPAL.Token"))
                return objective.count;
            RRConnection conn = int.TryParse(connId, out int id) && _connections.TryGetValue(id, out RRConnection connection) ? connection : null;
            return conn != null && IsPlayerFree(conn.LoginName)
                ? QuestRules.ScaleTokenRequirement(objective.count, GCDatabase.Instance.GetRequiredKnobFixed32("FreePlayerRequiredKingsCoinMult"))
                : objective.count;
        }

        private static int CountSavedQuestItems(SavedCharacter character, QuestObjective objective)
        {
            int quantity = 0;
            foreach (SavedInventoryItem saved in character.inventory ?? new List<SavedInventoryItem>())
            {
                if (saved.containerId != 0x0B || !QuestRules.IsGcType(saved.gcClass, objective.target))
                    continue;
                var item = new GCObject { GCClass = saved.gcClass, StoredRarity = saved.rarity, StoredLevel = saved.storedLevel };
                if (QuestItemMatches(item, objective))
                    quantity = checked(quantity + Math.Max(1, saved.count));
            }
            return quantity;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Determinism", "DR0004", Justification = "QuestManager::update at 005c5990 calls __time64 for repeat eligibility; see docs/reverse/QUEST-AUDIT-20260907.md.")]
        private void ExpireRepeatQuests(RRConnection conn)
        {
            PlayerQuestState state = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
            if (state == null || state.CompletedAtUnixSeconds.Count == 0)
                return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<(int Index, string QuestId, long AcceptedAt)> expired = null;
            for (int index = state.CompletedQuests.Count - 1; index >= 0; index--)
            {
                string questId = state.CompletedQuests[index];
                QuestData definition = QuestManager.FindDefinition(questId);
                if (definition?.repeatable != true || definition.minRepeatTimeSeconds == 0
                    || !state.CompletedAtUnixSeconds.TryGetValue(questId, out long acceptedAt)
                    || !QuestRules.RepeatWaitElapsed(now, acceptedAt, definition.minRepeatTimeSeconds))
                    continue;
                expired ??= new List<(int, string, long)>();
                expired.Add((index, questId, acceptedAt));
                state.CompletedQuests.RemoveAt(index);
                state.CompletedAtUnixSeconds.Remove(questId);
            }
            if (expired == null)
                return;
            if (!SavePlayerQuests(conn))
            {
                for (int index = expired.Count - 1; index >= 0; index--)
                {
                    var entry = expired[index];
                    state.CompletedQuests.Insert(entry.Index, entry.QuestId);
                    state.CompletedAtUnixSeconds[entry.QuestId] = entry.AcceptedAt;
                }
                return;
            }
            QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);
        }

        private void InitializeQuestWorldObjectives(RRConnection conn, ActiveQuest added = null)
        {
            PlayerQuestState state = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
            if (state == null)
                return;
            foreach (ActiveQuest quest in state.ActiveQuests)
            {
                if (added != null && !ReferenceEquals(quest, added))
                    continue;
                QuestData definition = QuestManager.FindDefinition(quest.QuestId);
                for (int index = 0; index < quest.Objectives.Count; index++)
                {
                    QuestProgress progress = quest.Objectives[index];
                    QuestObjective objective = definition.objectives[index];
                    progress.GoToRegistered = objective.type == "goto" && !progress.IsComplete
                        && QuestRules.MatchesTargetZone(conn.CurrentZoneName, objective.targetZone);
                    if (progress.GoToRegistered)
                        progress.GoToCounter = (ushort)(RandomStreams.GenerateGlobalStatic("GoToObjective::OnAddToWorld", objective.sourcePath) % 30u);
                }
            }
        }

        private void UpdateQuestManagers()
        {
            foreach (RRConnection conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.IsSpawned)
                    continue;
                ExpireRepeatQuests(conn);
                PlayerQuestState state = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
                if (state == null)
                    continue;
                foreach (ActiveQuest quest in state.ActiveQuests)
                {
                    QuestData definition = QuestManager.FindDefinition(quest.QuestId);
                    for (int index = 0; index < quest.Objectives.Count; index++)
                    {
                        QuestProgress progress = quest.Objectives[index];
                        if (!progress.GoToRegistered || progress.IsComplete || ++progress.GoToCounter != 30)
                            continue;
                        progress.GoToCounter = 0;
                        QuestObjective objective = definition.objectives[index];
                        if (!QuestRules.MatchesTargetZone(conn.CurrentZoneName, objective.targetZone))
                            continue;
                        if (string.IsNullOrEmpty(objective.targetEntity))
                        {
                            CompleteGotoObjective(conn, quest, progress);
                            continue;
                        }
                        if (!TryFindQuestTargetPosition(conn, objective.targetEntity, out int targetX, out int targetY, out int targetZ))
                            continue;
                        ResolveAuthoritativePlayerPositionFixed(conn, out int x, out int y, out int z);
                        if (QuestRules.IsWithinObjectiveRange(x, y, z, targetX, targetY, targetZ, objective.range))
                            CompleteGotoObjective(conn, quest, progress);
                    }
                }
            }
        }

        private bool TryFindQuestTargetPosition(RRConnection conn, string name, out int x, out int y, out int z)
        {
            x = y = z = 0;
            if (TryGetZoneNpcsForConnection(conn, out List<ZoneNPC> npcs))
            {
                ZoneNPC npc = npcs.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (npc?.HasFixedPosition == true)
                {
                    x = npc.PosFixedX;
                    y = npc.PosFixedY;
                    z = npc.PosFixedZ;
                    return true;
                }
            }
            if (AuthoredGameplayCatalog.QuestLocationsByZone.TryGetValue(conn.CurrentZoneName, out List<ZoneWaypointData> locations))
            {
                ZoneWaypointData location = locations.FirstOrDefault(candidate => string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase));
                if (location != null)
                {
                    x = location.PosFixedX;
                    y = location.PosFixedY;
                    z = location.PosFixedZ;
                    return true;
                }
            }
            if (!ZoneSpawner.Instance.TryGetProceduralSnapshot(GetDungeonLayoutSeedKey(conn, conn.CurrentZoneName), out var snapshot))
                return false;
            foreach (var cell in snapshot.WorldCells)
            {
                if (!AuthoredGameplayCatalog.QuestLocationsByTile.TryGetValue(cell.TileType, out List<ZoneWaypointData> tileLocations))
                    continue;
                ZoneWaypointData location = tileLocations.FirstOrDefault(candidate => string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase));
                if (location == null)
                    continue;
                x = checked(cell.WorldOriginFixedX + location.PosFixedX);
                y = checked(cell.WorldOriginFixedY + location.PosFixedY);
                z = location.PosFixedZ;
                return true;
            }
            return false;
        }
    }
}
