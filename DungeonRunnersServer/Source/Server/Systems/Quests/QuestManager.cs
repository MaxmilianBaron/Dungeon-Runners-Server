using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Networking;
using DungeonRunners.Data;

namespace DungeonRunners.Gameplay
{
    public class QuestManager
    {
        private static QuestManager _instance;
        public static QuestManager Instance => _instance ??= new QuestManager();

        private Dictionary<string, PlayerQuestState> _playerQuests = new Dictionary<string, PlayerQuestState>();
        private Action<RRConnection, byte, byte, byte[]> _sendPacket;
        private Func<RRConnection, LEWriter, bool> _writeEntitySynch;
        private Func<string, PlayerState> _getPlayerContext;
        private Func<RRConnection, IEnumerable<string>> _getZoneQuestGivers;
        private Func<string, QuestObjective, int> _countItems;
        private Func<string, QuestObjective, int> _requiredItems;

        public static QuestData FindDefinition(string questId)
        {
            if (string.IsNullOrEmpty(questId))
                return null;
            return AuthoredGameplayCatalog.QuestsByHash.TryGetValue(AuthoredGameplayCatalog.ComputeDJB2Hash(questId), out QuestData quest)
                && string.Equals(quest.id, questId, StringComparison.OrdinalIgnoreCase) ? quest : null;
        }

        public void SetItemObjectiveCallbacks(Func<string, QuestObjective, int> countItems, Func<string, QuestObjective, int> requiredItems)
        {
            _countItems = countItems;
            _requiredItems = requiredItems;
        }

        private int RequiredItems(string connId, QuestObjective objective)
        {
            return _requiredItems?.Invoke(connId, objective) ?? objective.count;
        }

        private List<QuestProgress> RestoreObjectives(string connId, QuestData definition, List<QuestProgress> previous,
            Func<QuestObjective, int> initialItemCounts = null)
        {
            var result = new List<QuestProgress>(definition.objectives.Count);
            for (int index = 0; index < definition.objectives.Count; index++)
            {
                QuestObjective objective = definition.objectives[index];
                QuestProgress saved = previous != null && index < previous.Count ? previous[index] : null;
                int required = objective.type == "item" ? RequiredItems(connId, objective) : objective.count;
                int current = saved != null && string.Equals(saved.Type, objective.type, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(saved.Target, objective.target, StringComparison.OrdinalIgnoreCase) ? saved.Current : 0;
                if (objective.type == "item")
                    current = initialItemCounts?.Invoke(objective) ?? _countItems?.Invoke(connId, objective) ?? 0;
                result.Add(new QuestProgress
                {
                    ObjectiveName = objective.name ?? string.Empty,
                    Type = objective.type,
                    Target = objective.target,
                    Label = objective.label,
                    Required = required,
                    Current = Math.Clamp(current, 0, required)
                });
            }
            return result;
        }


        public void SetPlayerContextCallback(Func<string, PlayerState> callback)
        {
            _getPlayerContext = callback;
        }

        public void SetZoneQuestGiversCallback(Func<RRConnection, IEnumerable<string>> callback)
        {
            _getZoneQuestGivers = callback;
        }

        public bool CanOfferQuest(string connId, QuestData quest)
        {
            PlayerQuestState state = GetPlayerState(connId);
            if (state == null || quest == null || state.ActiveQuests.Any(active =>
                string.Equals(active.QuestId, quest.id, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (state.CompletedQuests.Any(completed => string.Equals(completed, quest.id, StringComparison.OrdinalIgnoreCase)))
                return false;
            PlayerState live = _getPlayerContext?.Invoke(connId);
            int level = live?.Level ?? state.Level;
            if (level < quest.minLevel || level > quest.maxLevel)
                return false;
            string avatar = live?.AvatarGcType ?? state.AvatarGcType;
            if (!string.IsNullOrWhiteSpace(quest.requiredClass) && !QuestRules.IsGcType(avatar, quest.requiredClass))
                return false;
            return quest.requiredQuests.All(required => state.CompletedQuests.Any(completed =>
                string.Equals(completed, required, StringComparison.OrdinalIgnoreCase)));
        }

        private QuestManager()
        {
            Debug.Log("[QUEST-MANAGER] state=initialized");
        }

        public void SetSendCallback(Action<RRConnection, byte, byte, byte[]> sendCallback)
        {
            _sendPacket = sendCallback;
        }

        public void SetEntitySynchCallback(Func<RRConnection, LEWriter, bool> writeEntitySynch)
        {
            _writeEntitySynch = writeEntitySynch;
        }

        private bool WriteEntitySynchAndEnd(RRConnection conn, LEWriter writer, string packetName)
        {
            if (_writeEntitySynch == null)
            {
                Debug.LogError($"[{packetName}] state=missing target=entitySynchCallback");
                return false;
            }
            if (!_writeEntitySynch(conn, writer))
            {
                Debug.LogError($"[{packetName}] state=unavailable target=entitySynchWrite");
                return false;
            }
            writer.WriteByte(0x06);
            return true;
        }

        public bool CanQueryComplete(ActiveQuest activeQuest)
        {
            if (activeQuest?.Objectives == null)
                return false;
            QuestData definition = AuthoredGameplayCatalog.Quests.FirstOrDefault(quest =>
                string.Equals(quest.id, activeQuest.QuestId, StringComparison.OrdinalIgnoreCase));
            return definition != null
                && activeQuest.Objectives.Count == definition.objectives.Count
                && activeQuest.Objectives.All(objective => objective != null && objective.IsComplete);
        }

        public bool CanQueryComplete(string connId, ActiveQuest quest)
        {
            if (!CanQueryComplete(quest))
                return false;
            QuestData definition = FindDefinition(quest.QuestId);
            foreach (QuestObjective objective in definition.objectives)
                if (objective.type == "item" && (_countItems?.Invoke(connId, objective) ?? 0) < RequiredItems(connId, objective))
                    return false;
            return true;
        }

        public void InitializePlayer(string connId, List<ActiveQuest> activeQuests,
                                        List<string> completedQuests, List<string> unlockedCheckpoints,
                                        int playerLevel = 1, string avatarGcType = null,
                                        Dictionary<string, long> completionTimes = null,
                                        Func<QuestObjective, int> initialItemCounts = null,
                                        bool preserveTemporary = false)
        {
            var state = new PlayerQuestState
            {
                ConnId = connId,
                Level = playerLevel,
                AvatarGcType = avatarGcType ?? string.Empty,
                CompletedAtUnixSeconds = completionTimes != null
                    ? new Dictionary<string, long>(completionTimes, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
                ActiveQuests = activeQuests ?? new List<ActiveQuest>(),
                CompletedQuests = completedQuests ?? new List<string>(),
                UnlockedCheckpoints = unlockedCheckpoints ?? new List<string>()
            };
            state.ActiveQuests = state.ActiveQuests.Where(quest => quest != null
                && FindDefinition(quest.QuestId) is QuestData definition && (preserveTemporary || !definition.temporary)).ToList();
            foreach (ActiveQuest quest in state.ActiveQuests)
                quest.Objectives = RestoreObjectives(connId, FindDefinition(quest.QuestId), quest.Objectives, initialItemCounts);
            _playerQuests[connId] = state;
            Debug.Log($"[QUEST-MANAGER] conn={connId} level={playerLevel} active={state.ActiveQuests.Count} completed={state.CompletedQuests.Count}");
        }

        public PlayerQuestState GetPlayerState(string connId)
        {
            if (_playerQuests.TryGetValue(connId, out var state))
                return state;
            return null;
        }

        public void RemovePlayer(string connId)
        {
            _playerQuests.Remove(connId);
        }

        public ActiveQuest GetQuestByInstanceId(string connId, uint instanceId)
        {
            var state = GetPlayerState(connId);
            return state?.ActiveQuests.FirstOrDefault(q => q.InstanceId == instanceId);
        }

        public bool IsObjectiveIncomplete(RRConnection conn, string questId, int objectiveIndex)
        {
            if (conn == null || string.IsNullOrWhiteSpace(questId) || objectiveIndex < 0)
                return false;
            ActiveQuest quest = GetPlayerState(conn.ConnId.ToString())?.ActiveQuests.FirstOrDefault(candidate =>
                string.Equals(candidate.QuestId, questId, StringComparison.OrdinalIgnoreCase));
            return quest != null
                && quest.Objectives != null
                && objectiveIndex < quest.Objectives.Count
                && !quest.Objectives[objectiveIndex].IsComplete;
        }

        public bool RemoveQuestByInstanceId(string connId, uint instanceId)
        {
            var state = GetPlayerState(connId);
            if (state == null) return false;
            return state.ActiveQuests.RemoveAll(q => q.InstanceId == instanceId) > 0;
        }

        public QuestAcceptResult AcceptQuest(string connId, string questId, string npcId)
        {
            Debug.LogError($"[QUEST-MANAGER] action=accept quest={questId}");
            var result = new QuestAcceptResult { Success = false };

            var playerState = GetPlayerState(connId);
            if (playerState == null) return result;

            if (playerState.ActiveQuests.Any(q => q.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase)))
                return result;

            var questData = AuthoredGameplayCatalog.Quests.FirstOrDefault(q =>
                q.id.Equals(questId, StringComparison.OrdinalIgnoreCase));
            if (questData == null) return result;

            if (!CanOfferQuest(connId, questData)
                || !questData.offeringNpcs.Contains(npcId, StringComparer.OrdinalIgnoreCase))
                return result;

            var activeQuest = new ActiveQuest
            {
                QuestId = questId,
                QuestGiverId = npcId,
                AcceptedAt = DateTime.UtcNow,
                Objectives = new List<QuestProgress>()
            };

            activeQuest.Objectives = RestoreObjectives(connId, questData, null);

            playerState.ActiveQuests.Add(activeQuest);
            result.Success = true;
            result.Quest = activeQuest;
            result.QuestData = questData;
            return result;
        }

        public void SendAddPacket(RRConnection conn, QuestData questData, ActiveQuest activeQuest)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.QuestManagerId);
            writer.WriteByte(0x01);

            writer.WriteByte(0x04);
            writer.WriteUInt32(AuthoredGameplayCatalog.ComputeDJB2Hash(questData.id));

            writer.WriteUInt32(activeQuest.InstanceId);

            var objectives = activeQuest.Objectives ?? new List<QuestProgress>();

            bool allComplete = CanQueryComplete(activeQuest);
            writer.WriteByte(allComplete ? (byte)0x01 : (byte)0x00);

            writer.WriteByte((byte)objectives.Count);

            foreach (var obj in objectives)
            {
                byte flags = (byte)(0x02 | (obj.IsComplete ? 0x01 : 0x00));
                writer.WriteByte(flags);
                string addLabel = $"{obj.Label ?? "Objective"}: {obj.Current} / {(obj.Required > 0 ? obj.Required : 1)}";
                writer.WriteCString(addLabel);
                writer.WriteUInt16((ushort)(obj.Required > 0 ? obj.Required : 1));
            }

            if (!WriteEntitySynchAndEnd(conn, writer, "QUEST-ADD")) return;
            Debug.LogError($"[QUEST-ADD] hex={BitConverter.ToString(writer.ToArray()).Replace("-", " ")}");
            Debug.LogError($"[QUEST-ADD] Sending {questData.id} InstanceId={activeQuest.InstanceId} allComplete={allComplete}");
            _sendPacket?.Invoke(conn, 0x01, 0x0F, writer.ToArray());
        }

        public void SendRemovePacket(RRConnection conn, uint instanceId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);

            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.QuestManagerId);
            writer.WriteByte(0x02);

            writer.WriteUInt32(instanceId);

            if (!WriteEntitySynchAndEnd(conn, writer, "QUEST-REMOVE")) return;
            Debug.LogError($"[QUEST-ADD] hex={BitConverter.ToString(writer.ToArray()).Replace("-", " ")}");
            Debug.LogError($"[QUEST-REMOVE] InstanceId={instanceId}");
            _sendPacket?.Invoke(conn, 0x01, 0x0F, writer.ToArray());
        }

        public void SendProgressPacket(RRConnection conn, uint instanceId, ActiveQuest quest)
        {
            var objectives = quest.Objectives ?? new System.Collections.Generic.List<QuestProgress>();
            bool allComplete = CanQueryComplete(quest);

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.QuestManagerId);
            writer.WriteByte(0x03);

            writer.WriteUInt32(instanceId);
            writer.WriteByte(0x01);

            writer.WriteByte((byte)objectives.Count);
            foreach (var obj in objectives)
            {
                byte flags = (byte)(0x02 | (obj.IsComplete ? 0x01 : 0x00));
                writer.WriteByte(flags);
                string progressLabel = $"{obj.Label ?? "Objective"}: {obj.Current} / {(obj.Required > 0 ? obj.Required : 1)}";
                writer.WriteCString(progressLabel);
                writer.WriteUInt16((ushort)(obj.Required > 0 ? obj.Required : 1));
            }

            if (!WriteEntitySynchAndEnd(conn, writer, "QUEST-PROGRESS")) return;
            Debug.LogError($"[QUEST-PROGRESS] Objectives packet: {BitConverter.ToString(writer.ToArray()).Replace("-", " ")}");
            _sendPacket?.Invoke(conn, 0x01, 0x0F, writer.ToArray());

            var flagWriter = new LEWriter();
            flagWriter.WriteByte(0x07);
            flagWriter.WriteByte(0x35);
            flagWriter.WriteUInt16(conn.QuestManagerId);
            flagWriter.WriteByte(0x03);

            flagWriter.WriteUInt32(instanceId);
            flagWriter.WriteByte(0x00);
            flagWriter.WriteByte(allComplete ? (byte)0x01 : (byte)0x00);

            if (!WriteEntitySynchAndEnd(conn, flagWriter, "QUEST-PROGRESS-FLAG")) return;
            Debug.LogError($"[QUEST-PROGRESS] Complete flag packet: allComplete={allComplete}");
            _sendPacket?.Invoke(conn, 0x01, 0x0F, flagWriter.ToArray());

            Debug.LogError($"[QUEST-PROGRESS] InstanceId={instanceId} allComplete={allComplete}");
        }

        public void SendAvailableQuestUpdateForZone(RRConnection conn)
        {
            Debug.LogError($"[QUEST-AVAILABLE] zone={conn.CurrentZoneGcType} state=called");

            var playerState = GetPlayerState(conn.ConnId.ToString());
            if (playerState == null)
            {
                Debug.LogError($"[QUEST-AVAILABLE] No player state for {conn.ConnId}");
                return;
            }

            var availableByNpc = new Dictionary<string, List<QuestData>>(StringComparer.OrdinalIgnoreCase);
            foreach (string giver in _getZoneQuestGivers?.Invoke(conn) ?? Enumerable.Empty<string>())
            {
                if (availableByNpc.ContainsKey(giver)
                    || !AuthoredGameplayCatalog.QuestOffersByNpc.TryGetValue(giver, out List<QuestData> offers))
                    continue;
                List<QuestData> available = offers
                    .Where(quest => CanOfferQuest(playerState.ConnId, quest))
                    .OrderBy(quest => string.Equals(quest.id, "world.dungeon00.quest.Q11_a1", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ToList();
                if (available.Count > 0)
                    availableByNpc.Add(giver, available);
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.QuestManagerId);
            writer.WriteByte(0x07);

            writer.WriteByte(checked((byte)availableByNpc.Count));

            foreach (var npcQuestEntry in availableByNpc)
            {
                string npcGcType = npcQuestEntry.Key;
                var quests = npcQuestEntry.Value;

                byte[] npcBytes = System.Text.Encoding.UTF8.GetBytes(npcGcType);
                writer.WriteBytes(npcBytes);
                writer.WriteByte(0x00);

                writer.WriteByte(checked((byte)quests.Count));

                foreach (var quest in quests)
                {
                    uint hash = AuthoredGameplayCatalog.ComputeDJB2Hash(quest.id);
                    writer.WriteByte(0x04);
                    writer.WriteUInt32(hash);
                    Debug.LogError($"[QUEST-AVAILABLE]   Quest: {quest.id} -> 0x{hash:X8}");
                }
            }

            if (!WriteEntitySynchAndEnd(conn, writer, "QUEST-AVAILABLE")) return;

            var packet = writer.ToArray();
            Debug.LogError($"[QUEST-AVAILABLE] Sending packet: {packet.Length} bytes");
            Debug.LogError($"[QUEST-AVAILABLE] hex={BitConverter.ToString(packet).Replace("-", " ")}");
            _sendPacket?.Invoke(conn, 0x01, 0x0F, packet);
        }

        public void SendQueryResponse(RRConnection conn, uint questHash, uint npcEntityId)
        {
            if (!AuthoredGameplayCatalog.QuestsByHash.TryGetValue(questHash, out QuestData quest)
                || !CanOfferQuest(conn.ConnId.ToString(), quest))
                return;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.QuestManagerId);
            writer.WriteByte(0x04);
            GCTypeCodec.WriteTypeId(writer, quest.id);
            if (!WriteEntitySynchAndEnd(conn, writer, "QUEST-QUERY"))
                return;
            conn.PendingQuestHash = questHash;
            conn.PendingQuestNpcEntityId = npcEntityId;
            conn.PendingTurnInInstanceId = 0;
            _sendPacket?.Invoke(conn, 0x01, 0x0F, writer.ToArray());
        }

        public bool SendClearQueryResponse(RRConnection conn)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.QuestManagerId);
            writer.WriteByte(0x05);
            if (!WriteEntitySynchAndEnd(conn, writer, "QUEST-CLEAR-QUERY"))
                return false;
            _sendPacket?.Invoke(conn, 0x01, 0x0F, writer.ToArray());
            return true;
        }

        public void SendFinalizePacket(RRConnection conn, uint instanceId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);

            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.QuestManagerId);
            writer.WriteByte(0x08);

            writer.WriteUInt32(instanceId);

            if (!WriteEntitySynchAndEnd(conn, writer, "QUEST-FINALIZE")) return;
            Debug.LogError($"[QUEST-ADD] hex={BitConverter.ToString(writer.ToArray()).Replace("-", " ")}");
            Debug.LogError($"[QUEST-FINALIZE] InstanceId={instanceId}");
            _sendPacket?.Invoke(conn, 0x01, 0x0F, writer.ToArray());
        }

        public QuestProgressMutation StageCreatureKill(RRConnection conn, List<string> candidateGcTypes)
        {
            var mutation = new QuestProgressMutation();
            if (conn == null || candidateGcTypes == null || candidateGcTypes.Count == 0)
                return mutation;
            mutation.Updates.AddRange(UpdateProgress(
                conn.ConnId.ToString(),
                "kill",
                candidateGcTypes,
                1,
                mutation));
            return mutation;
        }

        public List<QuestProgressUpdate> OnItemPickedUp(RRConnection conn, string itemGcType, int quantity = 1)
        {
            QuestProgressMutation mutation = StageItemPickup(conn, itemGcType, quantity);
            CommitProgress(conn, mutation);
            return mutation.Updates;
        }

        public List<QuestProgressUpdate> OnEntityActivated(RRConnection conn, List<string> candidateGcTypes)
        {
            QuestProgressMutation mutation = StageEntityActivation(conn, candidateGcTypes);
            CommitProgress(conn, mutation);
            return mutation.Updates;
        }

        public QuestProgressMutation StageItemPickup(RRConnection conn, string itemGcType, int quantity = 1)
        {
            return StageItemObjectives(conn);
        }

        public QuestProgressMutation StageItemObjectives(RRConnection conn)
        {
            var mutation = new QuestProgressMutation();
            PlayerQuestState state = conn != null ? GetPlayerState(conn.ConnId.ToString()) : null;
            if (state == null || _countItems == null)
                return mutation;
            try
            {
                foreach (ActiveQuest quest in state.ActiveQuests)
                {
                    QuestData definition = FindDefinition(quest.QuestId);
                    if (definition == null || quest.Objectives.Count != definition.objectives.Count)
                        continue;
                    for (int index = 0; index < definition.objectives.Count; index++)
                    {
                        QuestObjective objective = definition.objectives[index];
                        if (objective.type != "item")
                            continue;
                        QuestProgress progress = quest.Objectives[index];
                        int required = RequiredItems(state.ConnId, objective);
                        int current = Math.Clamp(_countItems(state.ConnId, objective), 0, required);
                        if (progress.Current == current && progress.Required == required)
                            continue;
                        mutation.Rollbacks.Add(new QuestProgressRollback
                        {
                            Objective = progress,
                            PreviousCurrent = progress.Current,
                            PreviousRequired = progress.Required
                        });
                        progress.Required = required;
                        progress.Current = current;
                        mutation.Updates.Add(new QuestProgressUpdate
                        {
                            QuestId = quest.QuestId,
                            ObjectiveName = progress.ObjectiveName,
                            Current = current,
                            Required = required,
                            IsComplete = progress.IsComplete,
                            Label = progress.Label
                        });
                    }
                }
                return mutation;
            }
            catch
            {
                RollbackProgress(mutation);
                throw;
            }
        }

        public QuestProgressMutation StageEntityActivation(RRConnection conn, List<string> candidateGcTypes)
        {
            var mutation = new QuestProgressMutation();
            if (conn == null || candidateGcTypes == null || candidateGcTypes.Count == 0)
                return mutation;
            mutation.Updates.AddRange(UpdateProgress(
                conn.ConnId.ToString(),
                "activate",
                candidateGcTypes,
                1,
                mutation));
            return mutation;
        }

        public void CommitProgress(RRConnection conn, QuestProgressMutation mutation)
        {
            if (mutation == null || mutation.Finalized)
                return;
            mutation.Finalized = true;
            SendProgressUpdates(conn, mutation.Updates);
            mutation.Rollbacks.Clear();
        }

        public void RollbackProgress(QuestProgressMutation mutation)
        {
            if (mutation == null || mutation.Finalized)
                return;
            for (int rollbackIndex = mutation.Rollbacks.Count - 1; rollbackIndex >= 0; rollbackIndex--)
            {
                QuestProgressRollback rollback = mutation.Rollbacks[rollbackIndex];
                rollback.Objective.Current = rollback.PreviousCurrent;
                rollback.Objective.Required = rollback.PreviousRequired;
            }
            mutation.Finalized = true;
            mutation.Rollbacks.Clear();
        }

        private void SendProgressUpdates(RRConnection conn, List<QuestProgressUpdate> updates)
        {
            if (conn == null || updates == null || updates.Count == 0)
                return;

            var sentQuestIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var update in updates)
            {
                if (!sentQuestIds.Add(update.QuestId)) continue;
                var quest = GetPlayerState(conn.ConnId.ToString())?.ActiveQuests
                    .FirstOrDefault(q => string.Equals(q.QuestId, update.QuestId, StringComparison.OrdinalIgnoreCase));
                if (quest != null)
                    SendProgressPacket(conn, quest.InstanceId, quest);
            }
        }

        private List<QuestProgressUpdate> UpdateProgress(string connId, string eventType, string target)
        {
            return UpdateProgress(connId, eventType, new List<string> { target }, 1);
        }

        private List<QuestProgressUpdate> UpdateProgress(string connId, string eventType, List<string> candidateTargets)
        {
            return UpdateProgress(connId, eventType, candidateTargets, 1);
        }

        private List<QuestProgressUpdate> UpdateProgress(string connId, string eventType, List<string> candidateTargets, int quantity, QuestProgressMutation mutation = null)
        {
            var updates = new List<QuestProgressUpdate>();
            var playerState = GetPlayerState(connId);
            if (playerState == null || quantity <= 0) return updates;


            foreach (var quest in playerState.ActiveQuests)
            {
                QuestData authoredQuest = AuthoredGameplayCatalog.Quests.FirstOrDefault(candidate =>
                    string.Equals(candidate.id, quest.QuestId, StringComparison.OrdinalIgnoreCase));
                for (int objectiveIndex = 0; objectiveIndex < quest.Objectives.Count; objectiveIndex++)
                {
                    QuestProgress objective = quest.Objectives[objectiveIndex];
                    if (objective.IsComplete)
                        continue;
                    if (!objective.Type.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool anyMatch = false;
                    IEnumerable<string> authoredTargets = authoredQuest?.objectives != null && objectiveIndex < authoredQuest.objectives.Count
                        ? authoredQuest.objectives[objectiveIndex].targets
                        : null;
                    IEnumerable<string> objectiveTargets = authoredTargets != null && authoredTargets.Any(target => !string.IsNullOrWhiteSpace(target))
                        ? authoredTargets
                        : new[] { objective.Target };
                    foreach (string objectiveTarget in objectiveTargets)
                    {
                        if (string.IsNullOrWhiteSpace(objectiveTarget))
                            continue;
                        foreach (var candidate in candidateTargets)
                        {
                            if (string.IsNullOrEmpty(candidate)) continue;
                            if (objectiveTarget.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                            {
                                anyMatch = true;
                                break;
                            }
                        }
                        if (anyMatch)
                            break;
                    }
                    if (!anyMatch) continue;

                    int appliedQuantity = Math.Min(quantity, Math.Max(0, objective.Required - objective.Current));
                    if (appliedQuantity <= 0)
                        continue;
                    mutation?.Rollbacks.Add(new QuestProgressRollback
                    {
                        Objective = objective,
                        PreviousCurrent = objective.Current,
                        PreviousRequired = objective.Required
                    });
                    objective.Current += appliedQuantity;
                    updates.Add(new QuestProgressUpdate
                    {
                        QuestId = quest.QuestId,
                        ObjectiveName = objective.ObjectiveName,
                        Current = objective.Current,
                        Required = objective.Required,
                        IsComplete = objective.IsComplete,
                        Label = objective.Label
                    });
                    Debug.LogError($"[QUEST-MANAGER] action=progress label='{objective.Label}' applied={appliedQuantity} current={objective.Current} required={objective.Required}");
                }
            }
            return updates;
        }

        public void SendTurnInDialog(RRConnection conn, uint instanceId)
        {
            var playerState = GetPlayerState(conn.ConnId.ToString());
            var activeQuest = playerState?.ActiveQuests.FirstOrDefault(q => q.InstanceId == instanceId);

            if (activeQuest == null)
            {
                Debug.LogError($"[QUEST-TURNIN] instanceId={instanceId} state=notFound");
                return;
            }

            uint questHash = AuthoredGameplayCatalog.ComputeDJB2Hash(activeQuest.QuestId);
            Debug.LogError($"[QUEST-TURNIN] action=dialog quest={activeQuest.QuestId} hash=0x{questHash:X8}");

            conn.PendingTurnInInstanceId = instanceId;
            conn.PendingQuestHash = 0;

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.QuestManagerId);
            writer.WriteByte(0x06);
            writer.WriteUInt32(instanceId);
            if (!WriteEntitySynchAndEnd(conn, writer, "QUEST-TURNIN")) return;

            var packet = writer.ToArray();
            Debug.LogError($"[QUEST-TURNIN] hex={BitConverter.ToString(packet).Replace("-", " ")}");
            _sendPacket?.Invoke(conn, 0x01, 0x0F, packet);
        }

        public bool UnlockCheckpoint(string connId, string checkpointId)
        {
            var playerState = GetPlayerState(connId);
            if (playerState == null) return false;
            if (playerState.UnlockedCheckpoints.Contains(checkpointId)) return false;
            playerState.UnlockedCheckpoints.Add(checkpointId);
            return true;
        }

        public void WriteQuestManagerComponent(LEWriter writer, string connId, ushort playerId,
                                                ushort questManagerId, Action<LEWriter, string, bool> writeGcType,
                                                RRConnection conn = null)
        {
            var playerState = GetPlayerState(connId);
            var activeQuests = playerState?.ActiveQuests ?? new List<ActiveQuest>();
            var checkpoints = playerState?.UnlockedCheckpoints ?? new List<string>();

            writer.WriteByte(0x32);
            writer.WriteUInt16(playerId);
            writer.WriteUInt16(questManagerId);
            writeGcType(writer, "QuestManager", false);
            writer.WriteByte(0x01);

            writer.WriteUInt32(conn?.NextQuestInstanceId ?? (activeQuests.Count == 0 ? 1u : unchecked(activeQuests.Max(quest => quest.InstanceId) + 1u)));

            if (conn != null && conn.HasSavedTownPortal)
            {
                writer.WriteByte(0x01);
                writer.WriteCString(conn.TownPortalZoneName);
                writer.WriteCString("");
                writer.WriteUInt32(conn.TownPortalZoneId);
                Debug.LogError($"[QM-INIT] Town portal saved: zone={conn.TownPortalZoneName} guid={conn.TownPortalZoneId}");
            }
            else
            {
                writer.WriteByte(0x00);
                writer.WriteCString("Hello");
                writer.WriteCString("HelloAgain");
                writer.WriteUInt32(0x00);
            }

            writer.WriteByte(0x00);
            writer.WriteCString("");
            writer.WriteCString("");
            writer.WriteUInt32(0x00);
            writer.WriteCString(conn?.ZonePortalSource ?? "");
            writer.WriteCString("");
            writer.WriteCString("");

            writer.WriteByte(0x00);

            ushort activeQuestCount = (ushort)activeQuests.Count;
            writer.WriteUInt16(activeQuestCount);

            foreach (var quest in activeQuests)
            {
                writeGcType(writer, quest.QuestId, true);
                writer.WriteUInt32(quest.InstanceId);
                bool allDone = CanQueryComplete(quest);
                writer.WriteByte(allDone ? (byte)0x01 : (byte)0x00);
                writer.WriteByte((byte)quest.Objectives.Count);
                foreach (var obj in quest.Objectives)
                {
                    byte flags = (byte)(0x02 | (obj.IsComplete ? 0x01 : 0x00));
                    writer.WriteByte(flags);
                    string initLabel = $"{obj.Label ?? "Objective"}: {obj.Current} / {(obj.Required > 0 ? obj.Required : 1)}";
                    writer.WriteCString(initLabel);
                    writer.WriteUInt16((ushort)(obj.Required > 0 ? obj.Required : 1));
                }
            }

            ushort checkpointCount = (ushort)checkpoints.Count;
            writer.WriteUInt16(checkpointCount);
            foreach (var cp in checkpoints)
            {
                writeGcType(writer, cp, true);
            }
        }
    }

    public class PlayerQuestState
    {
        public string ConnId;
        public int Level = 1;
        public string AvatarGcType = string.Empty;
        public Dictionary<string, long> CompletedAtUnixSeconds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public List<ActiveQuest> ActiveQuests = new List<ActiveQuest>();
        public List<string> CompletedQuests = new List<string>();
        public List<string> UnlockedCheckpoints = new List<string>();
    }

    [Serializable]
    public class ActiveQuest
    {
        public string QuestId;
        public string QuestGiverId;
        public DateTime AcceptedAt;
        public List<QuestProgress> Objectives = new List<QuestProgress>();
        public uint InstanceId;
    }

    [Serializable]
    public class QuestProgress
    {
        public string ObjectiveName;
        public string Type;
        public string Target;
        public string Label;
        public int Required;
        public int Current;
        public ushort GoToCounter;
        public bool GoToRegistered;
        public bool IsComplete => Current >= Required;
    }

    public class QuestAcceptResult
    {
        public bool Success;
        public string ErrorMessage;
        public ActiveQuest Quest;
        public QuestData QuestData;
    }

    public class QuestProgressUpdate
    {
        public string QuestId;
        public string ObjectiveName;
        public string Label;
        public int Current;
        public int Required;
        public bool IsComplete;
    }

    public sealed class QuestProgressMutation
    {
        internal readonly List<QuestProgressRollback> Rollbacks = new List<QuestProgressRollback>();
        internal bool Finalized;
        public List<QuestProgressUpdate> Updates { get; } = new List<QuestProgressUpdate>();
    }

    internal sealed class QuestProgressRollback
    {
        public QuestProgress Objective;
        public int PreviousCurrent;
        public int PreviousRequired;
    }
}
