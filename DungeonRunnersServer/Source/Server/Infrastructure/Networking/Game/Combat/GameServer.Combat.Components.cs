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
public partial class GameServer
    {        private void HandleComponentUpdate(RRConnection conn, LEReader reader)
        {
            try
            {
                if (reader.Remaining < 3)
                {
                    Debug.LogError($"[COMPONENT] state=short remaining={reader.Remaining}");
                    return;
                }
                ushort componentId = reader.ReadUInt16();
                byte subMessage = reader.ReadByte();
                Combat.Monster componentMonster = ResolveMonsterForConnectionComponent(conn, componentId);
                bool componentIsMonster = componentMonster != null;
                bool componentIsPlayerOwned = IsAvatarOrAvatarComponentId(conn, componentId);
                if (componentId == conn.QuestManagerId && subMessage >= 0x01 && subMessage <= 0x06)
                {
                    HandleQuestRequest(conn, subMessage, reader);
                    return;
                }


                if (VerbosePacketLogging && subMessage >= 0x30 && subMessage <= 0x3F)
                {
                    byte[] detailRaw = reader.PeekRemaining();
                    string detailHex = detailRaw.Length > 0 ? BitConverter.ToString(detailRaw) : "(empty)";
                    Debug.LogError($"[DETAIL-0x3x] cid=0x{componentId:X4} sub=0x{subMessage:X2} remaining={detailRaw.Length} hex={detailHex}");
                }
                if (subMessage == 0x64)
                {
                    if (componentIsMonster)
                    {
                        HandleMonsterStateMachineUpdate(conn, reader, componentId);
                        return;
                    }

                    bool componentIsBlingGnome = EntitySynchInfoAuthority.Instance.IsBlingGnomeComponent(componentId);
                    if (!componentIsBlingGnome && !componentIsPlayerOwned)
                    {
                        Debug.LogError($"[PLAYER-STATE] state=discarded reason=unknown-source-component component={componentId} conn={conn?.ConnId ?? 0}");
                        return;
                    }

                    if (reader.Remaining >= 1)
                    {
                        byte flags = reader.ReadByte();
                        ushort messageType = 0xFFFF;
                        ushort scope = 0xFFFF;
                        ushort target = 0;
                        uint value = 0;

                        if ((flags & 0x02) != 0 && reader.Remaining >= 2)
                            messageType = reader.ReadUInt16();
                        if ((flags & 0x04) != 0 && reader.Remaining >= 2)
                            scope = reader.ReadUInt16();
                        if ((flags & 0x08) != 0 && reader.Remaining >= 2)
                            target = reader.ReadUInt16();
                        if ((flags & 0x20) != 0 && reader.Remaining >= 4)
                            value = reader.ReadUInt32();
                        if ((flags & 0x10) != 0 && reader.Remaining >= 2)
                        {
                            ushort extraWordCount = reader.ReadUInt16();
                            for (int wordIndex = 0; wordIndex < extraWordCount && reader.Remaining >= 2; wordIndex++)
                                reader.ReadUInt16();
                        }

                        if (reader.Remaining >= 1)
                        {
                            byte entitySynchInfoFlags = reader.ReadByte();
                            if ((entitySynchInfoFlags & 0x02) != 0 && reader.Remaining >= 4)
                            {
                                uint entitySynchInfoHP = reader.ReadUInt32();
                                if (componentIsBlingGnome)
                                    EntitySynchInfoAuthority.Instance.ObserveClientBlingGnomeHP(conn, componentId, entitySynchInfoHP, "GNOME-SM-HP-0x64");
                                else
                                    ObserveClientPlayerHP(conn, entitySynchInfoHP, "PLAYER-STATE-ENTITY-SYNCH-INFO");
                            }
                        }

                        if (VerbosePacketLogging) Debug.LogError($"[PLAYER-STATE] cid={componentId} flags=0x{flags:X2} type={messageType} value={value}");

                        if (messageType == 0x1C)
                        {
                            var playerState = GetPlayerState(conn.ConnId.ToString());
                            if (playerState != null)
                            {
                                Debug.LogError($"[LEVEL-UP-0x1C] state=confirmed level={playerState.Level} xp={playerState.Experience}");
                            }
                        }
                    }
                    return;
                }

                if (subMessage == 0x65)
                {
                    if (componentIsMonster)
                    {
                        try
                        {
                            if (reader.Remaining > 0)
                            {
                                byte[] rawPeek = reader.PeekRemaining();
                                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-0x65] cid={componentId} remaining={reader.Remaining} raw={BitConverter.ToString(rawPeek)}");
                            }

                            var monster = CombatRuntime.Instance.GetMonsterByComponent(componentId);
                            int componentOffset = CombatRuntime.Instance.GetComponentOffset(componentId);
                            if (monster != null && componentOffset == 1 && reader.Remaining >= 2)
                            {
                                byte sessionId = reader.ReadByte();
                                byte moveCount = reader.ReadByte();
                                int applied = 0;
                                for (int moveIndex = 0; moveIndex < moveCount && reader.Remaining >= 13; moveIndex++)
                                {
                                    byte moveFlags = reader.ReadByte();
                                    int headingRaw = reader.ReadInt32();
                                    int posXRaw = reader.ReadInt32();
                                    int posYRaw = reader.ReadInt32();
                                    monster.SessionId = sessionId;
                                    monster.HeadingFixed = headingRaw;
                                    monster.PosFixedX = posXRaw;
                                    monster.PosFixedY = posYRaw;
                                    if (monster.EntityId <= ushort.MaxValue)
                                    applied++;
                                    if (VerbosePacketLogging) Debug.LogError($"[MONSTER-MOVE-0x65] name='{monster.Name}' cid={componentId} flags=0x{moveFlags:X2} session={sessionId} posFixed8=({monster.PosFixedX},{monster.PosFixedY}) headingFixed={monster.HeadingFixed}");
                                }
                                if (applied != moveCount)
                                    Debug.LogError($"[MONSTER-MOVE-0x65] name='{monster.Name}' cid={componentId} expected={moveCount} applied={applied} remaining={reader.Remaining}");
                            }

                            if (reader.Remaining >= 1)
                            {
                                byte entitySynchInfoFlags = reader.ReadByte();
                                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-0x65] cid={componentId} offset={componentOffset} entitySynchInfoFlags=0x{entitySynchInfoFlags:X2}");

                                if ((entitySynchInfoFlags & 0x02) != 0 && reader.Remaining >= 4)
                                {
                                    uint clientHP = reader.ReadUInt32();
                                    int clientActual = (int)(clientHP / 256);
                                    if (monster != null)
                                    {
                                        int serverActual = (int)(CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) / 256);
                                        if (VerbosePacketLogging) Debug.LogError($"[MONSTER-0x65] name='{monster.Name}' eid={monster.EntityId} clientHp={clientActual} serverHp={serverActual} delta={serverActual - clientActual}");

                                        CombatRuntime.Instance.ObserveClientMonsterHP(monster, clientHP, "MONSTER-MOVE-HP-0x65");
                                    }
                                    else
                                    {
                                        if (VerbosePacketLogging) Debug.LogError($"[MONSTER-0x65] cid={componentId} state=notFound target=combatRuntime");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[MONSTER-0x65] parse state=failed message='{ex.Message}'");
                        }
                        return;
                    }

                    if (EntitySynchInfoAuthority.Instance.IsBlingGnomeComponent(componentId))
                    {
                        try
                        {
                            byte sessionId = reader.Remaining >= 1 ? reader.ReadByte() : (byte)0;
                            byte moveCount = reader.Remaining >= 1 ? reader.ReadByte() : (byte)0;
                            for (int moveIndex = 0; moveIndex < moveCount && reader.Remaining >= 13; moveIndex++)
                            {
                                reader.ReadByte();
                                reader.ReadInt32();
                                reader.ReadInt32();
                                reader.ReadInt32();
                            }
                            if (reader.Remaining >= 1)
                            {
                                byte entitySynchInfoFlags = reader.ReadByte();
                                if ((entitySynchInfoFlags & 0x02) != 0 && reader.Remaining >= 4)
                                {
                                    uint clientHP = reader.ReadUInt32();
                                    EntitySynchInfoAuthority.Instance.ObserveClientBlingGnomeHP(conn, componentId, clientHP, "GNOME-MOVE-HP-0x65");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[GNOME-0x65] parse state=failed message='{ex.Message}'");
                        }
                        return;
                    }

                    HandleClientMove(conn, reader, componentId);
                    return;
                }
                else if (subMessage == 0x03)
                {
                    HandleCancelAction(conn, reader, componentId);
                }
                else if (subMessage == 0x0A)
                {
                    if (componentId == conn.QuestManagerId)
                    {
                        Debug.LogError("[QUEST-0x0A] action=townPortal source=obelisk");
                        if (conn.HasSavedTownPortal)
                        {
                            Debug.LogError($"[QUEST-0x0A] action=teleport zone={conn.TownPortalZoneName} posFixed=({conn.TownPortalPosFixedX},{conn.TownPortalPosFixedY})");
                            ChangeZoneToPosition(conn, conn.TownPortalZoneName,
                                conn.TownPortalPosFixedX, conn.TownPortalPosFixedY, conn.TownPortalPosFixedZ);
                        }
                        else
                        {
                            Debug.LogError("[QUEST-0x0A] state=noSavedTownPortal");
                        }
                    }
                }
                else if (subMessage == 0x01)
                {
                    if (!componentIsMonster && !componentIsPlayerOwned)
                    {
                        Debug.LogError($"[ACTION] state=discarded reason=unknown-source-component component={componentId} conn={conn?.ConnId ?? 0}");
                        return;
                    }
                    Debug.LogError($"[SUBMSG-0x01] state=questCheckPassed remaining={reader.Remaining}");

                    byte responseId = reader.ReadByte();
                    Debug.LogError($"[ACTION-READ] responseId={responseId}, remaining={reader.Remaining}");

                    byte actionType = reader.ReadByte();
                    Debug.LogError($"[ACTION-READ] actionType=0x{actionType:X2}, remaining={reader.Remaining}");

                    if (actionType == 0x52 && reader.Remaining <= 2)
                    {
                        byte spellSessionID = reader.Remaining >= 1 ? reader.ReadByte() : (byte)0;
                        byte slotID = reader.Remaining >= 1 ? reader.ReadByte() : (byte)0;
                        Debug.LogError($"[SPELL-0x52] action=selfCast sessionId={spellSessionID} slotId={slotID} componentId={componentId} remaining={reader.Remaining}");
                        ClearZoneSpawnInvulnerability(conn, $"ACTION-0x{actionType:X2}");
                        conn.MessageQueue.EnqueueDeferred(
                            () => BuildPlayerSelfCastActionResponse(conn, componentId, responseId, spellSessionID, slotID),
                            retryOnEmpty: true,
                            componentId: componentId);
                        Debug.LogError("[SPELL-0x52] action=enqueue target=actionResponse queue=MessageQueue");

                        if (componentIsPlayerOwned)
                            RelaySelfCastActionToPeers(conn, spellSessionID, slotID, "MP-SELFCAST");

                        if (componentIsPlayerOwned)
                        {
                            PlayerState state52 = GetPlayerState(conn.ConnId.ToString());
                            if (state52 != null)
                                QueuePendingSelfCastAction(conn, state52, slotID, componentId, responseId, spellSessionID);
                        }
                        return;
                    }

                    byte sessionID = reader.ReadByte();
                    Debug.LogError($"[ACTION-READ] sessionID={sessionID}, remaining={reader.Remaining}");

                    ushort targetEntityID = reader.ReadUInt16();
                    Debug.LogError($"[ACTION-READ] targetEntityID={targetEntityID}, remaining={reader.Remaining}");

                    Debug.LogError($"[ACTION] ActionType=0x{actionType:X2}, ResponseId={responseId}, SessionID={sessionID}, Target={targetEntityID}");
                    if (actionType != 0x50 && actionType != 0x51 && conn.HasActiveUseTarget)
                        ClearUseTargetAndReleaseControl(conn, $"ACTION-0x{actionType:X2}", componentId);
                    if (actionType == 0x06 || actionType == 0x50)
                    {
                        conn.ReflectedAvatarMovementStreamAdmitted = false;
                        conn.ReflectedAvatarMovementTerminalRestartPending = false;
                        conn.ReflectedAvatarMovementPostAdmissionBarrierPending = false;
                        conn.ReflectedAvatarMovementIdleBatchBarrierPending = false;
                    }

                    if (actionType == 0x06)
                    {
                        bool targetApproachStarted = false;
                        if (conn.PendingDroppedItemTargetEntityId != 0
                            && conn.PendingDroppedItemTargetEntityId != targetEntityID)
                        {
                            if (conn.UseTargetMovingActive)
                                CancelUseTargetMoving(conn, "dropped-item-new-action", false);
                            else
                                ClearPendingDroppedItemActivation(conn);
                        }
                        if (conn.PendingWorldEntityChestTargetEntityId != 0
                            && conn.PendingWorldEntityChestTargetEntityId != targetEntityID)
                        {
                            if (conn.UseTargetMovingActive)
                                CancelUseTargetMoving(conn, "world-entity-chest-new-action", false);
                            else
                                ClearPendingWorldEntityChestActivation(conn);
                        }
                        if (conn.PendingCheckpointTargetEntityId != 0
                            && conn.PendingCheckpointTargetEntityId != targetEntityID)
                        {
                            if (conn.UseTargetMovingActive)
                                CancelUseTargetMoving(conn, "checkpoint-new-action", false);
                            else
                                ClearPendingCheckpointActivation(conn);
                        }
                        if (IsDroppedItem(targetEntityID))
                        {
                            Debug.LogError($"[PICKUP] Player activated dropped item. Target={targetEntityID}");
                            targetApproachStarted = !BeginDroppedItemActivation(conn, componentId, targetEntityID, responseId, sessionID);
                        }
                        else if (IsPortal(targetEntityID, out var portal))
                        {
                            Debug.LogError($"[PORTAL] Player clicked portal! Target={targetEntityID} -> {portal.TargetZone}");
                            if (IsPortalActivationReached(conn, portal))
                            {
                                HandlePortalActivation(conn, componentId, targetEntityID, responseId, sessionID, portal);
                            }
                            else
                            {
                                conn.SessionID = sessionID;
                                QueuePortalActivationResponse(conn, componentId, targetEntityID, responseId, sessionID);
                                TrackPendingPortalActivation(conn, componentId, targetEntityID, responseId, sessionID, portal);
                                Debug.LogError($"[PORTAL] Target action relay owns portal approach target=0x{targetEntityID:X4}; pending activation uses current owner mover pose");
                            }
                        }
                        else if (IsChest(targetEntityID, out var chestData))
                        {
                            bool resolvedChestRadius = TryResolveWorldEntityActivationTargetRadiusFixed(chestData.GCType, out int chestTargetRadiusFixed);
                            long chestDistanceSqFixed = long.MaxValue;
                            int chestRangeFixed = 0;
                            bool chestInRange = resolvedChestRadius && IsWithinActivationRangeFixed(
                                conn,
                                chestData.PosFixedX,
                                chestData.PosFixedY,
                                chestTargetRadiusFixed,
                                out chestDistanceSqFixed,
                                out chestRangeFixed);
                            if (!chestInRange)
                            {
                                conn.SessionID = sessionID;
                                QueueTargetActionAck(conn, componentId, targetEntityID, responseId, sessionID, "CHEST");
                                Debug.LogError($"[CHEST] state=2 target=0x{targetEntityID:X4} label='{chestData.Label}' boundsResolved={resolvedChestRadius} distanceSq={chestDistanceSqFixed} range={chestRangeFixed} sourceFunction=Activate::States@0x00522300");
                            }
                            else
                            {
                                Debug.LogError($"[CHEST] Player clicked chest! Target={targetEntityID} ({chestData.Label})");
                                HandleChestActivation(conn, componentId, targetEntityID, responseId, sessionID, chestData);
                            }
                        }
                        else if (IsCheckpoint(targetEntityID, out var checkpoint))
                        {
                            Debug.LogError($"[CHECKPOINT] Player clicked checkpoint! Target={targetEntityID}");
                            targetApproachStarted = !BeginCheckpointActivation(conn, componentId, targetEntityID, responseId, sessionID, checkpoint);
                        }
                        else if (WorldEntitySpawner.Instance.TryGetEntity(targetEntityID, out var weData))
                        {
                            Debug.LogError($"[WORLD-ENTITY] Player clicked {weData.EntityType}: {weData.Label} (id=0x{targetEntityID:X4})");
                            if (weData.IsTeleporter)
                            {
                                HandleTeleporterActivation(conn, componentId, targetEntityID, responseId, sessionID, weData);
                            }
                            else if (weData.IsGate)
                            {
                                var ackMessage = new LEWriter();
                                ackMessage.WriteByte(0x35);
                                ackMessage.WriteUInt16(componentId);
                                ackMessage.WriteByte(0x01);
                                ackMessage.WriteByte(responseId);
                                ackMessage.WriteByte(0x06);
                                ackMessage.WriteByte(sessionID);
                                ackMessage.WriteUInt16(targetEntityID);
                                WritePlayerEntitySynch(conn, ackMessage);
                                conn.MessageQueue.Enqueue(ackMessage.ToArray());
                                HandleQuestWorldEntityActivation(conn, weData);

                                bool isPvpGate = weData.GCType != null &&
                                    weData.GCType.IndexOf("pvp", StringComparison.OrdinalIgnoreCase) >= 0;
                                 SendSystemMessage(conn, isPvpGate
                                     ? "This gate will open once the PVP match begins."
                                     : "The gate is sealed. Defeat the boss to open it.");
                                 Debug.LogError($"[WORLD-ENTITY] gate state=closed reason={(isPvpGate ? "pvp-setup" : "boss-alive")} label={weData.Label}");
                             }
                             else if (weData.IsChest || string.Equals(weData.EntityType, "boss_chest", StringComparison.OrdinalIgnoreCase))
                             {
                                 targetApproachStarted = !BeginWorldEntityChestActivation(
                                     conn,
                                     componentId,
                                     targetEntityID,
                                     responseId,
                                     sessionID,
                                     weData);
                             }
                             else
                             {
                                var ackMessage = new LEWriter();
                                ackMessage.WriteByte(0x35);
                                ackMessage.WriteUInt16(componentId);
                                ackMessage.WriteByte(0x01);
                                ackMessage.WriteByte(responseId);
                                ackMessage.WriteByte(0x06);
                                ackMessage.WriteByte(sessionID);
                                ackMessage.WriteUInt16(targetEntityID);
                                WritePlayerEntitySynch(conn, ackMessage);
                                conn.MessageQueue.Enqueue(ackMessage.ToArray());

                                var nonCombatInteractiveMessage = new LEWriter();
                                nonCombatInteractiveMessage.WriteByte(0x03);
                                nonCombatInteractiveMessage.WriteUInt16(targetEntityID);
                                nonCombatInteractiveMessage.WriteByte(0x0A);
                                nonCombatInteractiveMessage.WriteUInt32(0x00000000);
                                WriteNonCombatInteractiveEntitySynchInfo(nonCombatInteractiveMessage, weData.GCType);
                                conn.MessageQueue.Enqueue(nonCombatInteractiveMessage.ToArray());
                                Debug.LogError($"[WORLD-ENTITY] Sent NCI activate (0x03/0x0A) for {weData.EntityType}: {weData.Label}");
                                HandleQuestWorldEntityActivation(conn, weData);

                            }
                        }
                        else
                        {
                            Debug.LogError($"[NPC] Player clicked NPC! Target={targetEntityID}");
                            targetApproachStarted = HandleNPCClick(conn, componentId, targetEntityID, responseId, sessionID);
                        }
                        if (!targetApproachStarted && componentIsPlayerOwned && IsKnownTargetActionEntity(conn, targetEntityID))
                        {
                            RelayTargetActionToPeers(conn, responseId, actionType, sessionID, targetEntityID, "MP-ACTION-06");
                        }
                    }
                    else if (actionType == 0x52)
                    {
                        Debug.LogError($"[ACTION]  CHECKPOINT USE (0x52)! Target={targetEntityID}");
                        HandleCheckpointUse(conn, componentId, responseId, sessionID, reader);
                    }
                    else if (actionType == 0xA0)
                    {
                        Debug.LogError($"[ACTION] 0xA0 received (unexpected) target={targetEntityID}");
                    }
                    else if (actionType == 0x50)
                    {
                        byte manipulatorId = sessionID;
                        byte useFlags = (byte)(targetEntityID & 0xFF);
                        byte targetIdLow = (byte)((targetEntityID >> 8) & 0xFF);
                        byte targetIdHigh = reader.ReadByte();
                        ushort actualTargetId = (ushort)(targetIdLow | (targetIdHigh << 8));
                        var gnomeRuntime = BlingGnomeRuntime.Instance;
                        bool isBlingGnomeTarget = gnomeRuntime.TryResolveGnomeTarget(conn, actualTargetId,
                            out uint gnomeEntityId, out ushort gnomeBehaviorId, out bool gnomeBehaviorCreated, out string gnomeTargetReason);
                        Combat.Monster actionTargetMonster = isBlingGnomeTarget ? null : Combat.CombatRuntime.Instance.FindMonsterForTarget(actualTargetId, GetInstanceZoneKey(conn));
                        TryConsumeClientEntitySynchInfoSuffix(conn, reader, "ACTION-0x50-ENTITY-SYNCH-INFO", actionTargetMonster);

                        Debug.LogError($"[ATTACK] 0x50: componentId={componentId}, manipulatorId={manipulatorId}, flags={useFlags}, targetId={actualTargetId}, gnome={gnomeEntityId}, gnomeBehavior={gnomeBehaviorId}, gnomeBehaviorCreated={gnomeBehaviorCreated}, gnomeMatch={gnomeTargetReason}");
                        Combat.SpellData actionSkill = null;
                        string actionSkillRuntimeReason = null;
                        bool actionSkillRuntimeSupported = true;
                        if (componentIsPlayerOwned && useFlags >= 100)
                        {
                            PlayerState actionSkillState = GetPlayerState(conn.ConnId.ToString());
                            actionSkill = actionSkillState != null ? ResolveActionSpell(conn, actionSkillState, useFlags) : null;
                            actionSkillRuntimeSupported = Combat.SpellDatabase.TryValidatePlayerResourceCommitRuntime(actionSkill, out actionSkillRuntimeReason);
                        }
                        if (!actionSkillRuntimeSupported)
                        {
                            SkillEffectTracker.RecordPlayerGraph(conn.Avatar != null ? (uint)conn.Avatar.Id : 0u, actualTargetId, actionSkill?.SkillId, actionSkill?.TargetType, actionSkill?.OrderedEffects?.Count ?? 0, "rejected", actionSkillRuntimeReason, _combatTick);
                            SendUseTargetActionFailure(conn, componentId, responseId, actionSkillRuntimeReason ?? "unsupported-player-target-runtime");
                            Debug.LogError($"[SKILL-USE] action=target result=rejected manipId={useFlags} gc={actionSkill?.SkillId ?? "unresolved"} target={actualTargetId} reason={actionSkillRuntimeReason ?? "unsupported-player-target-runtime"} resourceCommit=False sourceFunction=ActiveSkill::validateUse@0x00538710");
                        }
                        else if (isBlingGnomeTarget)
                        {
                            if (!componentIsPlayerOwned || !QueuePendingBlingGnomeActionInput(conn, componentId, responseId, manipulatorId, useFlags, actualTargetId))
                                SendUseTargetActionFailure(conn, componentId, responseId, "invalid-bling-gnome-skill-target");
                        }
                        else if (componentIsMonster)
                        {
                            Debug.LogError($"[ATTACK] source=monster target=player component={componentId} targetId={actualTargetId}");
                            var attackingMonster = Combat.CombatRuntime.Instance.GetMonsterByComponent(componentId);
                            if (attackingMonster != null)
                                Debug.LogError($"[ATTACK] monster='{attackingMonster.Name}' targetPlayer={actualTargetId}");
                            else
                                Debug.LogError($"[ATTACK] no monster for component={componentId}");
                        }
                        else
                        {
                            if (componentId != 0)
                                conn.UnitBehaviorId = componentId;
                            Debug.LogError($"[ATTACK] source=player action=useTarget component={componentId} target={actualTargetId} manip={manipulatorId} flags={useFlags}");
                            long admissionSequence = ReservePlayerUseTargetAction(conn);
                            bool handled = false;
                            try
                            {
                                var monster = actionTargetMonster ?? Combat.CombatRuntime.Instance.FindMonsterForTarget(actualTargetId, GetInstanceZoneKey(conn));
                                if (monster != null)
                                {
                                    handled = QueuePendingPlayerUseTargetActionInput(conn, componentId, responseId, manipulatorId, useFlags, actualTargetId, monster, admissionSequence);
                                }
                                else
                                {
                                    RRConnection targetPlayer = ResolveAvatarTargetOwnerForClient(conn, actualTargetId, out _);
                                    if (targetPlayer != null)
                                    {
                                        if (TryAuthorizePvpUseTarget(conn, targetPlayer, useFlags, out string pvpReason))
                                        {
                                            handled = QueuePendingPlayerUseTargetActionInput(conn, componentId, responseId, manipulatorId, useFlags, actualTargetId, targetPlayer, admissionSequence);
                                        }
                                        else
                                        {
                                            SendUseTargetActionFailure(conn, componentId, responseId, pvpReason, admissionSequence);
                                            handled = true;
                                        }
                                    }
                                    else
                                    {
                                        Debug.LogError($"[ATTACK] target={actualTargetId} state=noMonsterOrPlayer");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"[ATTACK] error={ex.Message}\n{ex.StackTrace}");
                            }

                            if (!handled)
                            {
                                var unresolvedMonster = Combat.CombatRuntime.Instance.GetMonster(actualTargetId)
                                                       ?? Combat.CombatRuntime.Instance.GetMonsterByComponent(actualTargetId);
                                if (unresolvedMonster != null)
                                {
                                    uint unresolvedHP = Combat.CombatRuntime.Instance.PeekMonsterCurrentHPWire(unresolvedMonster);
                                    bool deadUseTarget = !unresolvedMonster.IsAlive || unresolvedMonster.CurrentHPWire == 0 || unresolvedHP == 0 || _finalizedMonsterKills.Contains((uint)actualTargetId);
                                    Debug.LogError($"[ATTACK] action=useTargetResolve target={actualTargetId} alive={unresolvedMonster.IsAlive} hp={unresolvedMonster.CurrentHPWire} resolvedHp={unresolvedHP} dead={deadUseTarget}");
                                    if (deadUseTarget)
                                    {
                                        RejectUnavailablePlayerUseTargetAction(
                                            conn,
                                            new PendingPlayerUseTargetActionInput
                                            {
                                                ConnId = conn.ConnId,
                                                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)),
                                                ComponentId = componentId,
                                                ResponseId = responseId,
                                                ManipulatorId = manipulatorId,
                                                UseFlags = useFlags,
                                                TargetId = actualTargetId,
                                                MonsterEntityId = unresolvedMonster.EntityId,
                                                ReceivedTick = _combatTick,
                                                AdmissionSequence = admissionSequence
                                            },
                                            unresolvedMonster,
                                            _combatTick,
                                            "dead-target");
                                    }
                                    else
                                    {
                                        var unresolvedLiveTargetMessage = new LEWriter();
                                        unresolvedLiveTargetMessage.WriteByte(0x35);
                                        unresolvedLiveTargetMessage.WriteUInt16(componentId);
                                        unresolvedLiveTargetMessage.WriteByte(0x01);
                                        unresolvedLiveTargetMessage.WriteByte(responseId);
                                        unresolvedLiveTargetMessage.WriteByte(0x50);
                                        unresolvedLiveTargetMessage.WriteByte(manipulatorId);
                                        unresolvedLiveTargetMessage.WriteByte(useFlags);
                                        unresolvedLiveTargetMessage.WriteUInt16(actualTargetId);
                                        WritePlayerEntitySynch(conn, unresolvedLiveTargetMessage);
                                        QueueClientEntityStream(conn, unresolvedLiveTargetMessage.ToArray());
                                        Debug.LogError($"[ATTACK] action=sendActionResponse reason=unresolvedLiveTarget target={actualTargetId} alive={unresolvedMonster.IsAlive} hp={unresolvedMonster.CurrentHPWire}");
                                        ClearUseTargetAndReleaseControl(conn, "ATTACK-unresolved-live-target", componentId);
                                    }
                                }
                                else
                                {
                                    if (_finalizedMonsterKills.Contains((uint)actualTargetId))
                                    {
                                        RejectUnavailablePlayerUseTargetAction(
                                            conn,
                                            new PendingPlayerUseTargetActionInput
                                            {
                                                ConnId = conn.ConnId,
                                                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)),
                                                ComponentId = componentId,
                                                ResponseId = responseId,
                                                ManipulatorId = manipulatorId,
                                                UseFlags = useFlags,
                                                TargetId = actualTargetId,
                                                ReceivedTick = _combatTick,
                                                AdmissionSequence = admissionSequence
                                            },
                                            null,
                                            _combatTick,
                                            "finalized-dead-target");
                                    }
                                    else
                                    {
                                        var missingTargetMessage = new LEWriter();
                                        missingTargetMessage.WriteByte(0x35);
                                        missingTargetMessage.WriteUInt16(componentId);
                                        missingTargetMessage.WriteByte(0x01);
                                        missingTargetMessage.WriteByte(responseId);
                                        missingTargetMessage.WriteByte(0x50);
                                        missingTargetMessage.WriteByte(manipulatorId);
                                        missingTargetMessage.WriteByte(useFlags);
                                        missingTargetMessage.WriteUInt16(actualTargetId);
                                        WritePlayerEntitySynch(conn, missingTargetMessage);
                                        QueueClientEntityStream(conn, missingTargetMessage.ToArray());
                                        Debug.LogError($"[ATTACK] action=sendActionResponse reason=fallbackTargetMissing target={actualTargetId}");
                                        ClearUseTargetAndReleaseControl(conn, "ATTACK-fallback-target-missing", componentId);

                                    }
                                }
                            }
                        }
                    }
                    else if (actionType == 0x51)
                    {
                        byte actionSessionId = sessionID;
                        byte manipulatorId = (byte)(targetEntityID & 0xFF);
                        byte posXByte0 = (byte)((targetEntityID >> 8) & 0xFF);
                        byte[] posRemain = reader.Remaining >= 11 ? reader.ReadBytes(11) : reader.ReadBytes(reader.Remaining);

                        int posX = 0, posY = 0, posZ = 0;
                        if (posRemain.Length >= 11)
                        {
                            posX = posXByte0 | (posRemain[0] << 8) | (posRemain[1] << 16) | (posRemain[2] << 24);
                            posY = posRemain[3] | (posRemain[4] << 8) | (posRemain[5] << 16) | (posRemain[6] << 24);
                            posZ = posRemain[7] | (posRemain[8] << 8) | (posRemain[9] << 16) | (posRemain[10] << 24);
                        }
                        Debug.LogError($"[SPELL-0x51] action=usePosition session={actionSessionId} manipulator={manipulatorId} posFixed=({posX},{posY},{posZ})");

                        PlayerState state = componentIsPlayerOwned ? GetPlayerState(conn.ConnId.ToString()) : null;
                        bool weaponUsePosition = IsBasicWeaponManipulatorId(manipulatorId);
                        var resolvedSpell = state != null && !weaponUsePosition ? ResolveActionSpell(conn, state, manipulatorId) : null;
                        string positionRuntimeReason = null;
                        bool positionRuntimeSupported = weaponUsePosition
                            || Combat.SpellDatabase.TryValidatePlayerUsePositionRuntime(resolvedSpell, out positionRuntimeReason);
                        if (!positionRuntimeSupported)
                        {
                            SkillEffectTracker.RecordPlayerGraph(conn.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, resolvedSpell?.SkillId, resolvedSpell?.TargetType ?? "POSITION", resolvedSpell?.OrderedEffects?.Count ?? 0, "rejected", positionRuntimeReason, _combatTick);
                            SendUsePositionActionFailure(conn, componentId, responseId, positionRuntimeReason ?? "unsupported-player-position-runtime");
                            Debug.LogError($"[SKILL-USE] action=position result=rejected manipId={manipulatorId} gc={resolvedSpell?.SkillId ?? "unresolved"} reason={positionRuntimeReason ?? "unsupported-player-position-runtime"} resourceCommit=False sourceFunction=UsePosition::validateManipulator@0x00547760->ActiveSkill::validateUse@0x00538710");
                        }
                        else
                        {
                            conn.MessageQueue.EnqueueDeferred(
                                () => BuildPlayerUsePositionActionResponse(conn, componentId, responseId, actionSessionId, manipulatorId, posX, posY, posZ),
                                retryOnEmpty: true,
                                componentId: componentId);
                            if (componentIsPlayerOwned)
                                RelayUsePositionActionToPeers(conn, actionSessionId, manipulatorId, posX, posY, posZ, "MP-SWING-POS");
                            Debug.LogError("[SPELL-0x51] action=enqueue target=actionResponse queue=MessageQueue");


                            if (componentIsPlayerOwned)
                            {
                                if (state != null)
                                {
                                    Debug.LogError($"[SPELL-0x51] manipulator={manipulatorId} spell={resolvedSpell?.DisplayName ?? "NOT RESOLVED"} session={actionSessionId} posFixed=({posX},{posY})");

                                if (weaponUsePosition)
                                {
                                    QueuePendingPlayerUsePositionWeaponActionInput(
                                        conn,
                                        componentId,
                                        responseId,
                                        actionSessionId,
                                        manipulatorId,
                                        posX,
                                        posY,
                                        posZ);
                                }
                                else if (resolvedSpell == null)
                                {
                                    Debug.LogError($"[SPELL-0x51] action=dropUnresolved manipulator={manipulatorId} session={actionSessionId} component={componentId} reason=no-manipulator sourceFunction=UsePosition::getManipulator@0x00547720");
                                }
                                else
                                {
                                    bool weaponProjectileRuntime = resolvedSpell != null
                                        && resolvedSpell.HasImmediateWeaponDamageEffect
                                        && state.WeaponUsesProjectile
                                        && state.WeaponProjectileSizeF32 > 0
                                        && state.WeaponProjectileSpeedF32 > 0;
                                    if (resolvedSpell != null
                                        && (resolvedSpell.ProjectileSizeF32 > 0 && resolvedSpell.ProjectileSpeedF32 > 0
                                            || weaponProjectileRuntime))
                                    {
                                        var pending = CreatePendingSpellProjectileFixed(
                                            conn,
                                            state,
                                            null,
                                            resolvedSpell,
                                            manipulatorId,
                                            manipulatorId,
                                            componentId,
                                            conn.PlayerPosFixedX,
                                            conn.PlayerPosFixedY,
                                            posX,
                                            posY,
                                            true,
                                            0,
                                            true,
                                            false,
                                            0,
                                            responseId,
                                            actionSessionId,
                                            posZ);
                                        _pendingSpells.Enqueue(pending);
                                        Debug.LogError($"[SPELL-0x51] action=queueProjectile spell={resolvedSpell?.DisplayName ?? resolvedSpell?.SkillId ?? "UNKNOWN"} manipulator={manipulatorId} initialTarget=none receivedTick={pending.ActionReceiveTick} fireTick=awaiting-response firstUpdateTick=awaiting-effect delayTicks={pending.ProjectileDelayTicks} hitHintF32={pending.ProjectileHitDistanceF32} speedF32={pending.ProjectileSpeedF32} stepF32={pending.StepDistanceF32} offsetF32={pending.ProjectileOffsetF32} initPreStepF32={pending.InitialDistanceF32} maxDistF32={pending.MaxDistanceF32} seq={pending.Sequence}");
                                    }
                                    else
                                    {
                                        var nearest = ResolvePositionSpellTargetFixed(conn, resolvedSpell, posX, posY, out int projectileHitDistanceF32);
                                        if (nearest != null && nearest.IsAlive)
                                        {
                                            nearest.UseTargetCount++;
                                            if (projectileHitDistanceF32 <= 0)
                                            {
                                                uint viewerId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0u;
                                                CombatRuntime.Instance.TryGetMonsterClientVisiblePositionFixed(nearest, viewerId, out int weaponAimFixedX, out int weaponAimFixedY);
                                                projectileHitDistanceF32 = UnitMover.IntSqrt(
                                                    (long)(weaponAimFixedX - conn.PlayerPosFixedX) * (weaponAimFixedX - conn.PlayerPosFixedX)
                                                    + (long)(weaponAimFixedY - conn.PlayerPosFixedY) * (weaponAimFixedY - conn.PlayerPosFixedY));
                                            }
                                        }
                                        projectileHitDistanceF32 = Math.Max(0, projectileHitDistanceF32);
                                        int projectileDelayTicks = ResolveProjectileImpactDelayTicksFixed32(resolvedSpell, state, projectileHitDistanceF32);
                                        _pendingSpells.Enqueue(new PendingSpell
                                        {
                                            Sequence = ++_nextPendingSpellProjectileSequence,
                                            Conn = conn,
                                            State = state,
                                            Monster = nearest,
                                            Spell = resolvedSpell,
                                            ManipId = manipulatorId,
                                            UseFlags = manipulatorId,
                                            ComponentId = componentId,
                                            StartFixedX = conn.PlayerPosFixedX,
                                            StartFixedY = conn.PlayerPosFixedY,
                                            AimFixedX = posX,
                                            AimFixedY = posY,
                                            InstanceKey = GetInstanceZoneKey(conn),
                                            DueTick = 0,
                                            ProjectileHitDistanceF32 = projectileHitDistanceF32,
                                            ProjectileDelayTicks = projectileDelayTicks,
                                            QueuedWithoutInitialTarget = nearest == null,
                                            AwaitingActionResponsePacket = true,
                                            ActionResponseId = responseId,
                                            ActionResponseSessionId = actionSessionId,
                                            ActionAimFixedZ = posZ,
                                            ActionReceiveTick = _combatTick,
                                            SkillUseTick = 0,
                                            StartsAfterSkillsChild = false,
                                            FireTick = 0,
                                            LastUpdateTick = 0
                                        });
                                        Debug.LogError($"[SPELL-0x51] action=queueDamage target='{nearest?.Name ?? "none"}' manipulator={manipulatorId} receivedTick={_combatTick} fireTick=awaiting-response hitDist={FormatFixed8Diagnostic(projectileHitDistanceF32)} speedF32={resolvedSpell?.ProjectileSpeedF32 ?? 0}");
                                    }
                                }
                                }
                            }
                        }
                    }
                    else
                    {
                        Debug.LogError($"[ACTION] Unhandled actionType=0x{actionType:X2} target={targetEntityID}");
                    }

                    if (reader.Remaining >= 1)
                    {
                        TryConsumeClientEntitySynchInfoSuffix(conn, reader, "ACTION-ENTITY-SYNCH-INFO");
                    }

                }
                else if (subMessage == 0x64)
                {
                    Debug.LogError($"[COMPONENT] Client sent 0x64 response - consuming data");
                    HandleClientControlResponse(conn, reader, componentId);
                }
                else if ((subMessage == 0x35 || subMessage == 0x36))
                {
                    string connKey = conn.ConnId.ToString();
                    SavedCharacter savedChar = GetActiveCharacter(conn)?.DeepClone();
                    if (savedChar == null) { while (reader.Remaining > 0) reader.ReadByte(); return; }
                    if (savedChar.hotbarSlots == null) savedChar.hotbarSlots = new List<HotbarSlotEntry>();
                    EnsurePlayerManipulatorMap(connKey);

                    if (subMessage == 0x36 && reader.Remaining >= 4)
                    {
                        List<HotbarSlotEntry> previousHotbarSlots = savedChar.hotbarSlots
                            .Select(entry => new HotbarSlotEntry { slot = entry.slot, skill = entry.skill })
                            .ToList();
                        Dictionary<uint, string> previousManipulatorMap = new Dictionary<uint, string>(_playerManipMap[connKey]);
                        uint slot = reader.ReadUInt32();
                        if (slot > 0x0FFF)
                        {
                            Debug.LogError($"[HOTBAR] REMOVE rejected slot={slot} reason=slot-out-of-range persistenceMutation=False");
                            return;
                        }
                        Debug.LogError($"[HOTBAR] REMOVE slot {slot}");
                        string removedSkill = null;
                        if (_playerManipMap[connKey].ContainsKey(slot))
                        {
                            removedSkill = _playerManipMap[connKey][slot];
                            RemovePlayerManipulator(connKey, slot);
                        }
                        var removedSlotEntry = savedChar.hotbarSlots.FirstOrDefault(h => h.slot == slot);
                        if (removedSkill == null && removedSlotEntry != null)
                            removedSkill = removedSlotEntry.skill;
                        savedChar.hotbarSlots.RemoveAll(h => h.slot == slot);
                        if (removedSkill != null)
                        {
                            List<uint> duplicateSlots = savedChar.hotbarSlots
                                .Where(h => string.Equals(h.skill, removedSkill, StringComparison.OrdinalIgnoreCase))
                                .Select(h => h.slot)
                                .ToList();
                            savedChar.hotbarSlots.RemoveAll(h => string.Equals(h.skill, removedSkill, StringComparison.OrdinalIgnoreCase));
                            for (int duplicateIndex = 0; duplicateIndex < duplicateSlots.Count; duplicateIndex++)
                                RemovePlayerManipulator(connKey, duplicateSlots[duplicateIndex]);
                        }
                        if (!TrySaveCharacterForConn(conn, savedChar, "hotbar-remove"))
                        {
                            savedChar.hotbarSlots = previousHotbarSlots;
                            _playerManipMap[connKey] = previousManipulatorMap;
                            return;
                        }
                        Debug.LogError($"[HOTBAR] Saved remove: slot {slot}, was '{removedSkill}'");

                        if (removedSkill != null &&
                            (removedSkill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             removedSkill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            bool stillOnBar = savedChar.hotbarSlots.Any(h =>
                                h.skill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                h.skill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!stillOnBar && BlingGnomeRuntime.Instance.HasGnome(conn.ConnId))
                            {
                                Debug.LogError($"[HOTBAR] BlingGnome removed from tray - despawning for {conn.LoginName}");
                                BlingGnomeRuntime.Instance.DespawnGnome(conn,
                                    (c, d, t, b) => SendCompressedA(c, d, t, b));
                            }
                        }

                        ushort manipulatorsComponentId = 0;
                        if (_playerManipulatorsIds.TryGetValue(connKey, out ushort resolvedManipulatorsComponentId))
                            manipulatorsComponentId = resolvedManipulatorsComponentId;
                        if (manipulatorsComponentId != 0 && removedSkill != null)
                        {
                            var hotbarRemoveMessage = new LEWriter();
                            hotbarRemoveMessage.WriteByte(0x35);
                            hotbarRemoveMessage.WriteUInt16(manipulatorsComponentId);
                            hotbarRemoveMessage.WriteByte(0x01);
                            hotbarRemoveMessage.WriteUInt32(slot);
                            bool hotbarRemoveQueued;
                            lock (conn.SendLock)
                            {
                                byte[] hotbarRemovePrefix = hotbarRemoveMessage.ToArray();
                                hotbarRemoveQueued = IsPassiveSkill(removedSkill)
                                    ? QueueHotbarPassiveResponse(conn, hotbarRemovePrefix, manipulatorsComponentId, savedChar)
                                    : QueueClientEntityStream(conn, BuildHotbarResponse(conn, hotbarRemovePrefix));
                            }
                            Debug.LogError($"[HOTBAR-MANIP] Queued={hotbarRemoveQueued} Remove slot={slot} '{removedSkill}'");
                        }
                        else if (IsPassiveSkill(removedSkill))
                            Debug.LogError($"[HOTBAR-MANIP] Queued=False Remove slot={slot} '{removedSkill}' reason=missing-manipulators-component");
                    }
                    else if (subMessage == 0x35 && reader.Remaining >= 9)
                    {
                        uint slot = reader.ReadUInt32();
                        byte typeFlag = reader.ReadByte();
                        uint gcHash = reader.ReadUInt32();
                        if (slot > 0x0FFF)
                        {
                            Debug.LogError($"[HOTBAR] PLACE rejected slot={slot} reason=slot-out-of-range persistenceMutation=False");
                            return;
                        }
                        Debug.LogError($"[HOTBAR] PLACE slot {slot} hash=0x{gcHash:X8}");

                        string skillGcClass = null;
                        if (_skillHashToGcClass.TryGetValue(gcHash, out string resolvedSkillGcClass))
                            skillGcClass = resolvedSkillGcClass;
                        if (skillGcClass != null)
                        {
                            if (IsPassiveSkill(skillGcClass)
                                && !PassiveAttributeModifiers.TryValidateRuntime(skillGcClass, out string passiveRuntimeReason))
                            {
                                SkillEffectTracker.RecordPlayerGraph(conn.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, skillGcClass, "PASSIVE", 0, "rejected", passiveRuntimeReason, _combatTick);
                                SendSystemMessage(conn, $"Skill nelze použít: {passiveRuntimeReason ?? "unsupported-passive-runtime"}");
                                Debug.LogError($"[HOTBAR] PLACE rejected slot={slot} skill={skillGcClass} reason={passiveRuntimeReason ?? "unsupported-passive-runtime"} persistenceMutation=False");
                                return;
                            }
                            List<HotbarSlotEntry> previousHotbarSlots = savedChar.hotbarSlots
                                .Select(entry => new HotbarSlotEntry { slot = entry.slot, skill = entry.skill })
                                .ToList();
                            Dictionary<uint, string> previousManipulatorMap = new Dictionary<uint, string>(_playerManipMap[connKey]);
                            string displacedSkill = null;
                            if (_playerManipMap[connKey].ContainsKey(slot))
                            {
                                string existing = _playerManipMap[connKey][slot];
                                if (!string.Equals(existing, skillGcClass, StringComparison.OrdinalIgnoreCase))
                                    displacedSkill = existing;
                            }
                            uint? oldSlot = FindPlayerManipulatorSlot(connKey, skillGcClass, slot);

                            if (oldSlot.HasValue) RemovePlayerManipulator(connKey, oldSlot.Value);
                            if (displacedSkill != null) RemovePlayerManipulator(connKey, slot);
                            SetPlayerManipulator(connKey, slot, skillGcClass);

                            savedChar.hotbarSlots.RemoveAll(h => h.slot == slot || string.Equals(h.skill, skillGcClass, StringComparison.OrdinalIgnoreCase));
                            if (displacedSkill != null)
                                savedChar.hotbarSlots.RemoveAll(h => string.Equals(h.skill, displacedSkill, StringComparison.OrdinalIgnoreCase));
                            savedChar.hotbarSlots.Add(new HotbarSlotEntry { slot = slot, skill = skillGcClass });
                            if (!TrySaveCharacterForConn(conn, savedChar, "hotbar-place"))
                            {
                                savedChar.hotbarSlots = previousHotbarSlots;
                                _playerManipMap[connKey] = previousManipulatorMap;
                                return;
                            }
                            Debug.LogError($"[HOTBAR] Saved: slot {slot} = '{skillGcClass}' displaced='{displacedSkill}' oldSlot={oldSlot}");

                            if (skillGcClass.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                skillGcClass.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                BlingGnomeRuntime.Instance.SetServer(this);
                                Debug.LogError($"[HOTBAR] BlingGnome placed on tray - spawning for {conn.LoginName}");
                                BlingGnomeRuntime.Instance.EnsureGnomeForHotbar(conn,
                                    (c, d, t, b) => SendCompressedA(c, d, t, b),
                                    (c, m) => SendSystemMessage(c, m));
                            }
                            else if (displacedSkill != null &&
                                (displacedSkill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 displacedSkill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                bool stillOnBar = savedChar.hotbarSlots.Any(h =>
                                    h.skill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    h.skill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0);
                                if (!stillOnBar && BlingGnomeRuntime.Instance.HasGnome(conn.ConnId))
                                {
                                    Debug.LogError($"[HOTBAR] BlingGnome displaced from tray - despawning for {conn.LoginName}");
                                    BlingGnomeRuntime.Instance.DespawnGnome(conn,
                                        (c, d, t, b) => SendCompressedA(c, d, t, b));
                                }
                            }

                            ushort manipulatorsComponentId = 0;
                            if (_playerManipulatorsIds.TryGetValue(connKey, out ushort resolvedManipulatorsComponentId))
                                manipulatorsComponentId = resolvedManipulatorsComponentId;

                            if (manipulatorsComponentId != 0)
                            {
                                byte skillLevel = (byte)Math.Min(byte.MaxValue, Math.Max(1, savedChar.GetSkillLevel(skillGcClass)));
                                bool skillIsPassive = IsPassiveSkill(skillGcClass);

                                var hotbarAddMessage = new LEWriter();
                                hotbarAddMessage.WriteByte(0x35);
                                hotbarAddMessage.WriteUInt16(manipulatorsComponentId);
                                hotbarAddMessage.WriteByte(0x00);
                                hotbarAddMessage.WriteByte(0xFF);
                                hotbarAddMessage.WriteCString(skillGcClass.ToLowerInvariant());
                                hotbarAddMessage.WriteUInt32(slot);
                                hotbarAddMessage.WriteByte(skillLevel);
                                bool hotbarAddQueued;
                                lock (conn.SendLock)
                                {
                                    byte[] hotbarAddPrefix = hotbarAddMessage.ToArray();
                                    hotbarAddQueued = skillIsPassive || IsPassiveSkill(displacedSkill)
                                        ? QueueHotbarPassiveResponse(conn, hotbarAddPrefix, manipulatorsComponentId, savedChar)
                                        : QueueClientEntityStream(conn, BuildHotbarResponse(conn, hotbarAddPrefix));
                                }
                                Debug.LogError($"[HOTBAR-MANIP] Queued={hotbarAddQueued} Add '{skillGcClass}' slot={slot} level={skillLevel} manipulatorsComponent=0x{manipulatorsComponentId:X4}");
                            }
                            else if (IsPassiveSkill(skillGcClass) || IsPassiveSkill(displacedSkill))
                                Debug.LogError($"[HOTBAR-MANIP] Queued=False Add '{skillGcClass}' slot={slot} reason=missing-manipulators-component");
                        }
                        else
                        {
                            Debug.LogError($"[HOTBAR] Could not resolve hash 0x{gcHash:X8} to any known skill");
                        }
                    }
                    else { while (reader.Remaining > 0) reader.ReadByte(); }
                }
                else if (subMessage == 0x21 || subMessage == 0x22 || subMessage == 0x23 || subMessage == 0x25 || subMessage == 0x26 || subMessage == 0x27 || subMessage == 0x28 || subMessage == 0x29)
                {
                    Debug.LogError($"[COMPONENT] Detected inventory/equipment operation 0x{subMessage:X2}");

                    string componentType = GetComponentType(conn.ConnId.ToString(), componentId);
                    Debug.LogError($"[COMPONENT-ROUTE] ComponentID 0x{componentId:X4} routed to: {componentType}");
                    if (componentType == "Equipment")
                    {
                        _equipment.ProcessRequest(conn, reader, componentId, subMessage);
                    }
                    else if (componentType == "UnitContainer")
                    {
                        _unitContainer.ProcessRequest(conn, reader, componentId, subMessage);
                    }
                    else
                    {
                        Debug.LogError($"[COMPONENT-ROUTE]  Unknown component type for ID 0x{componentId:X4}!");
                    }
                }
                else if (subMessage == 0x1E)
                {
                    ZoneNPC merchantNpc = TryGetZoneNpcsForConnection(conn, out List<ZoneNPC> buyZoneNpcs)
                        ? buyZoneNpcs.FirstOrDefault(npc => npc.IsMerchant && npc.MerchantId == componentId)
                        : null;
                    bool isMerchant = merchantNpc != null;

                    if (isMerchant && reader.Remaining == 6)
                    {
                        ushort buyerEntityId = reader.ReadUInt16();
                        uint itemId = reader.ReadUInt32();
                        MerchantRuntime.HandleBuyItem(conn, componentId, itemId, merchantNpc, _selectedCharacter, SendCompressedA, this, buyerEntityId, IsPlayerFree(conn.LoginName));
                    }
                }
                else if (subMessage == 0x1F)
                {
                    bool isMerchant = FindMerchantNpcByComponentId(conn, componentId) != null;

                    if (isMerchant)
                    {
                        byte[] sellPeek = reader.PeekRemaining();
                        Debug.LogError($"[MERCHANT] SELL RAW BYTES (remaining {sellPeek.Length}): {BitConverter.ToString(sellPeek)}");
                        ushort entityRef = reader.ReadUInt16();
                        uint itemId = reader.ReadUInt32();
                        Debug.LogError($"[MERCHANT]  SELL REQUEST! componentId=0x{componentId:X4}, entityRef=0x{entityRef:X4}, itemId={itemId}, remaining after read={reader.Remaining}");
                        MerchantRuntime.HandleSellItem(conn, componentId, (ushort)itemId, entityRef, _selectedCharacter, SendCompressedA, GetPlayerState(conn.ConnId.ToString()), this, 0);
                        if (reader.Remaining > 0)
                        {
                            int drained = 0;
                            while (reader.Remaining > 0) { reader.ReadByte(); drained++; }
                            Debug.LogError($"[MERCHANT] Drained {drained} leftover sell bytes");
                        }
                    }
                }
                else
                {
                    ZoneNPC trainerNpc = FindTrainerNpcByComponentId(conn, componentId);

                    if (trainerNpc != null)
                    {
                        byte[] trainerRaw = reader.PeekRemaining();
                        Debug.LogError($"[TRAINER] action=skillTrainerMessage npc='{trainerNpc.Name}' componentId=0x{componentId:X4} subMessage=0x{subMessage:X2} remaining={trainerRaw.Length} hex={BitConverter.ToString(trainerRaw)}");
                        HandleSkillTrainRequest(conn, reader, componentId, subMessage, trainerNpc);
                    }
                    else
                    {
                        string skillConnKey = conn.ConnId.ToString();
                        ushort playerSkillsCid = 0;
                        _playerSkillsComponentId.TryGetValue(skillConnKey, out playerSkillsCid);

                        if (playerSkillsCid != 0 && componentId == playerSkillsCid && subMessage == 0x39)
                        {
                            byte[] rawData = reader.PeekRemaining();
                            Debug.LogError($"[SKILL-EQUIP] 0x39 on Skills cid=0x{componentId:X4} remaining={rawData.Length} hex={BitConverter.ToString(rawData)}");

                            string skillGcClass = "";
                            byte slotByte = 0;
                            try
                            {
                                if (reader.Remaining > 0)
                                {
                                    byte refType = reader.ReadByte();
                                    if (refType == 0xFF && reader.Remaining > 0)
                                        skillGcClass = reader.ReadCString();
                                    else if (reader.Remaining >= 2)
                                    {
                                        ushort refId = reader.ReadUInt16();
                                        Debug.LogError($"[SKILL-EQUIP] entityIdRef={refId}");
                                    }
                                }
                                if (reader.Remaining > 0)
                                    slotByte = reader.ReadByte();

                                if (reader.Remaining > 0)
                                {
                                    byte entitySynchInfoFlags = reader.ReadByte();
                                    if ((entitySynchInfoFlags & 0x02) != 0 && reader.Remaining >= 4)
                                        reader.ReadUInt32();
                                }
                            }
                            catch (Exception parseEx)
                            {
                                Debug.LogError($"[SKILL-EQUIP] parse state=failed message='{parseEx.Message}'");
                                while (reader.Remaining > 0) reader.ReadByte();
                            }

                            Debug.LogError($"[SKILL-EQUIP] skill='{skillGcClass}' slot={slotByte}");

                            Debug.LogError($"[SKILL-EQUIP] state=accepted skill='{skillGcClass}' slot={slotByte} response=none source=clientAssigned");
                        }
                        else if (subMessage == 0x07 && componentId == conn.QuestManagerId)
                        {
                            uint cpHash = 0;
                            if (reader.Remaining >= 1)
                            {
                                byte tag = reader.ReadByte();
                                if (tag == 0x04 && reader.Remaining >= 4) cpHash = reader.ReadUInt32();
                                else if (tag == 0x02 && reader.Remaining >= 2) cpHash = reader.ReadUInt16();
                                else if (tag == 0x01 && reader.Remaining >= 1) cpHash = reader.ReadByte();
                            }
                            while (reader.Remaining > 0) reader.ReadByte();

                            string destZone = null;
                            string connId = conn.ConnId.ToString();
                            var playerState = QuestManager.Instance.GetPlayerState(connId);
                            if (playerState != null)
                            {
                                foreach (var cpId in playerState.UnlockedCheckpoints)
                                {
                                    if (AuthoredGameplayCatalog.ComputeDJB2Hash(cpId) == cpHash)
                                    {
                                        var cp = AuthoredGameplayCatalog.Checkpoints.FirstOrDefault(c =>
                                            c.id.Equals(cpId, StringComparison.OrdinalIgnoreCase));
                                        if (cp != null)
                                            destZone = cp.zone;
                                        else if (_checkpointZoneMap.TryGetValue(cpId, out string mz))
                                            destZone = mz;
                                        Debug.LogError($"[CP-TELEPORT] Hash 0x{cpHash:X8} -> '{cpId}' -> zone '{destZone}'");
                                        break;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(destZone))
                            {
                                TryFindZoneByName(destZone, out Zone zone);
                                if (zone != null)
                                {
                                    conn.PendingSpawnFixedX = zone.SpawnFixedX;
                                    conn.PendingSpawnFixedY = zone.SpawnFixedY;
                                    conn.PendingSpawnFixedZ = zone.SpawnFixedZ;
                                }
                                ChangeZone(conn, destZone, "");
                            }
                            else
                            {
                                Debug.LogError($"[CP-TELEPORT] hash=0x{cpHash:X8} state=notMatched action=rotatorFallback");
                                HandleObeliskTeleport(conn);
                            }
                        }
                        else if (subMessage == 0x0C && componentId == conn.QuestManagerId)
                        {
                            while (reader.Remaining > 0) reader.ReadByte();
                            if (!string.IsNullOrEmpty(conn.ZonePortalSource))
                            {
                                Debug.LogError($"[ZONE-PORTAL] action=teleport zone='{conn.ZonePortalSource}'");
                                TryFindZoneByName(conn.ZonePortalSource, out Zone zone);
                                if (zone != null)
                                {
                                    conn.PendingSpawnFixedX = zone.SpawnFixedX;
                                    conn.PendingSpawnFixedY = zone.SpawnFixedY;
                                    conn.PendingSpawnFixedZ = zone.SpawnFixedZ;
                                }
                                ChangeZone(conn, conn.ZonePortalSource, "");
                            }
                            else
                                Debug.LogError("[ZONE-PORTAL] state=missing source=zonePortal");
                        }
                        else
                        {
                            Debug.LogError($"[COMPONENT] sub=0x{subMessage:X2} cid=0x{componentId:X4} state=unknown");
                            int drained = 0;
                            while (reader.Remaining > 0) { reader.ReadByte(); drained++; }
                            if (drained > 0)
                                Debug.LogError($"[COMPONENT] drained={drained} reason=drainRemaining");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[COMPONENT] state=failed message='{ex.Message}'");
            }
        }

        private ZoneNPC FindZoneNpcByEntityId(RRConnection conn, uint entityId)
        {
            if (conn == null || !TryGetZoneNpcsForConnection(conn, out List<ZoneNPC> zoneNpcs))
                return null;
            return zoneNpcs.FirstOrDefault(npc => npc.Id == entityId);
        }

        private ZoneNPC FindMerchantNpcByComponentId(RRConnection conn, ushort componentId)
        {
            return TryGetZoneNpcsForConnection(conn, out var npcs)
                ? npcs.FirstOrDefault(npc => npc.IsMerchant && npc.MerchantId == componentId) : null;
        }

        private ZoneNPC FindTrainerNpcByComponentId(RRConnection conn, ushort componentId)
        {
            return TryGetZoneNpcsForConnection(conn, out var npcs)
                ? npcs.FirstOrDefault(npc => npc.IsTrainer && npc.TrainerId == componentId) : null;
        }

        private void HandleMonsterStateMachineUpdate(RRConnection conn, LEReader reader, ushort componentId)
        {
            if (reader.Remaining < 1) return;
            var monster = ResolveMonsterForConnectionComponent(conn, componentId);
            int componentOffset = CombatRuntime.Instance.GetComponentOffset(componentId);
            if (monster == null || componentOffset < 0)
                return;
            if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM-RAW] cid={componentId} offset={componentOffset} remaining={reader.Remaining} hex={BitConverter.ToString(reader.PeekRemaining())}");

            ushort messageType = 0xFFFF;
            ushort scope = 0xFFFF;
            ushort target = 0;
            uint value = 0;
            byte flags = 0;

            if (componentOffset == 2)
            {
                flags = reader.ReadByte();
                if ((flags & 0x02) != 0 && reader.Remaining >= 2)
                    messageType = reader.ReadUInt16();
                if ((flags & 0x04) != 0 && reader.Remaining >= 2)
                    scope = reader.ReadUInt16();
                if ((flags & 0x08) != 0 && reader.Remaining >= 2)
                    target = reader.ReadUInt16();
                if ((flags & 0x20) != 0 && reader.Remaining >= 4)
                    value = reader.ReadUInt32();
                if ((flags & 0x10) != 0 && reader.Remaining >= 2)
                {
                    ushort extraWordCount = reader.ReadUInt16();
                    for (int wordIndex = 0; wordIndex < extraWordCount && reader.Remaining >= 2; wordIndex++)
                        reader.ReadUInt16();
                }
            }
            else if (componentOffset == 1)
            {
                flags = reader.ReadByte();
            }
            else
            {
                flags = reader.ReadByte();
            }

            uint entitySynchInfoHP = 0;
            bool hasEntitySynchInfoHP = false;
            if (reader.Remaining >= 1)
            {
                byte entitySynchInfoFlags = reader.ReadByte();
                if (entitySynchInfoFlags != 0 && (entitySynchInfoFlags & 0x02) != 0 && reader.Remaining >= 4)
                {
                    entitySynchInfoHP = reader.ReadUInt32();
                    hasEntitySynchInfoHP = true;
                }
            }

            string messageName = messageType switch
            {
                0 => "Halt",
                1 => "GoToPrevious",
                2 => "Go",
                3 => "Arrive",
                4 => "CheckDest",
                5 => "Wait/LeaveCombat",
                6 => "Timer",
                7 => "ReturnHome",
                8 => "CombatTick",
                9 => "AGGRO",
                10 => "SecondaryTarget",
                11 => "Forget",
                12 => "Fidget/GOAGGRO",
                13 => "CombatAck/ServerAggro",
                0x925 => "DEAD (0x925)",
                0x9D5 => "DEAD (0x9D5)",
                _ => $"Unknown(0x{messageType:X4})"
            };

            string hpStr = hasEntitySynchInfoHP ? $" HP={entitySynchInfoHP}({entitySynchInfoHP / 256})" : "";
            if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM] cid={componentId} offset={componentOffset} flags=0x{flags:X2} message={messageType}({messageName}) scope={scope} target={target} value={value}{hpStr}");

            if (hasEntitySynchInfoHP)
            {
                uint clientHPWire = entitySynchInfoHP;
                CombatRuntime.Instance.ObserveClientMonsterHP(monster, clientHPWire, "MONSTER-SM-HP-0x64");
            }

            if (messageType == 0x9D5 || messageType == 0x925)
            {
                Debug.LogError($"[MONSTER-DEATH] entity={monster.EntityId} message=0x{messageType:X4} reporter={conn?.ConnId} serverAlive={monster.IsAlive} serverHPWire={CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster)} finalized={_finalizedMonsterKills.Contains(monster.EntityId)} authority=server-simulation");
                return;
            }

            if (messageType == 9)
            {
                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM] {monster.Name} AGGRO! value={value} target={target}");
                uint targetId = target != 0 ? target : (uint)(conn.Avatar?.Id ?? 0);
                if (targetId != 0)
                {
                    CombatRuntime.Instance.EngageMonsterFromClientAction(monster, targetId);
                }
            }
            else if (messageType == 13)
            {
                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM] CombatAck value={value} target={target}");
                monster.State = MonsterState.Combat;
                if (target != 0) monster.TargetId = target;
                string instanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
                uint roomSeed = CombatRuntime.Instance.GetRoomSeedForInstance(instanceKey);
                bool ready = CombatRuntime.Instance.TryGetRoomRuntime(instanceKey, out var runtime) && runtime.Initialized;
                Debug.LogError($"[RNG-SEED] Skipped CombatAck reseed for {monster.Name}#{monster.EntityId} target={target} instance='{instanceKey}' current=0x{roomSeed:X8} ready={ready}");
                try { ApplyMonsterDebuffs(conn, monster); } catch (Exception ex) { Debug.LogError($"[DEBUFF] state=failed message='{ex.Message}'"); }
            }
            else if (messageType == 10)
            {
                monster.State = MonsterState.Combat;
                if (target != 0) monster.TargetId = target;
                try { ApplyMonsterDebuffs(conn, monster); } catch (Exception ex) { Debug.LogError($"[DEBUFF] state=failed message='{ex.Message}'"); }
            }
            else if (messageType == 8)
            {
                try { ApplyMonsterDebuffs(conn, monster); } catch (Exception ex) { Debug.LogError($"[DEBUFF] state=failed message='{ex.Message}'"); }
            }
            else if (messageType == 12)
            {
                if (monster.TargetId == 0 && conn.Avatar != null)
                    monster.TargetId = (uint)conn.Avatar.Id;
                monster.State = MonsterState.Combat;
            }

        }


        public void SendDespawnEntity(RRConnection conn, ushort entityId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x05);
            writer.WriteUInt16(entityId);
            writer.WriteByte(0x06);
            SendToClient(conn, writer.ToArray());
            Debug.LogError($"[DESPAWN] Sent despawn for entity {entityId}");
        }



        private void SendClientControlReset(RRConnection conn, ushort componentId)
        {
            Debug.LogError($"[CONTROL] SendClientControlReset componentId=0x{componentId:X4}");

            conn.MessageQueue.EnqueueDeferred(() => BuildClientControlReset(conn, componentId), componentId: componentId, role: ClientEntityMessageRole.OwnerClientControlReset);
            ClearPendingClientControlReset(conn, componentId);
            Debug.LogError($"[CONTROL] Queued client control reset");
        }

        private byte[] BuildClientControlReset(RRConnection conn, ushort componentId)
        {
            if (conn == null || !conn.IsConnected || componentId == 0)
                return Array.Empty<byte>();

            uint playerEntityId = conn.Avatar != null && conn.Avatar.Id > 0
                ? (uint)conn.Avatar.Id
                : 0u;
            bool currentPlayerBehaviorAction = HasCurrentPlayerBehaviorAction(conn, playerEntityId);
            bool pendingPlayerActionResponse = HasPendingPlayerActionResponseAheadOfControlReset(conn);
            if (currentPlayerBehaviorAction || pendingPlayerActionResponse)
            {
                Debug.LogError($"[CONTROL] Dropped client control reset componentId=0x{componentId:X4} reason=action-admitted currentPlayerBehaviorAction={currentPlayerBehaviorAction} pendingPlayerActionResponse={pendingPlayerActionResponse} sourceFunction=UnitBehavior::onActionStopped@0x005205E0->UnitBehavior::FollowClient@0x005202F0");
                return Array.Empty<byte>();
            }

            var controlResetMessage = new LEWriter();
            if (!WriteClientControlUpdate(conn, controlResetMessage, componentId, false) ||
                !WriteClientControlUpdate(conn, controlResetMessage, componentId, true))
            {
                Debug.LogError($"[CONTROL] Dropped client control reset componentId=0x{componentId:X4}");
                ScheduleClientControlResetRetry(conn, componentId, "write-blocked");
                return Array.Empty<byte>();
            }

            Debug.LogError($"[CONTROL] Built client control reset componentId=0x{componentId:X4} writerTick={_combatTick}");
            return controlResetMessage.ToArray();
        }

        private void ScheduleClientControlResetRetry(RRConnection conn, ushort componentId, string reason)
        {
            if (conn == null || componentId == 0 || !conn.IsConnected) return;
            conn.PendingClientControlReset = true;
            conn.PendingClientControlResetComponentId = componentId;
            conn.PendingClientControlResetAttempts = (byte)Math.Min(255, conn.PendingClientControlResetAttempts + 1);
            uint delayTicks = conn.PendingClientControlResetAttempts <= 3 ? 3u : conn.PendingClientControlResetAttempts <= 10 ? 8u : 30u;
            ulong nextTick = (ulong)_combatTick + delayTicks;
            conn.PendingClientControlResetNextAttemptTick = nextTick >= uint.MaxValue ? uint.MaxValue : (uint)nextTick;
            Debug.LogError($"[CONTROL] Scheduled client control reset retry componentId=0x{componentId:X4} attempt={conn.PendingClientControlResetAttempts} reason={reason} delayTicks={delayTicks} nextTick={conn.PendingClientControlResetNextAttemptTick}");
        }

        private void ClearPendingClientControlReset(RRConnection conn, ushort componentId)
        {
            if (conn == null || !conn.PendingClientControlReset) return;
            if (componentId != 0 && conn.PendingClientControlResetComponentId != componentId) return;
            conn.PendingClientControlReset = false;
            conn.PendingClientControlResetComponentId = 0;
            conn.PendingClientControlResetNextAttemptTick = 0;
            conn.PendingClientControlResetAttempts = 0;
        }

        private void FlushPendingClientControlResets()
        {
            foreach (var conn in GetConnectionInsertionOrderSnapshot())
            {
                if (conn == null || !conn.PendingClientControlReset) continue;
                if (!conn.IsConnected)
                {
                    ClearPendingClientControlReset(conn, 0);
                    continue;
                }
                if (_combatTick < conn.PendingClientControlResetNextAttemptTick) continue;
                ushort componentId = conn.PendingClientControlResetComponentId;
                if (componentId == 0)
                {
                    ClearPendingClientControlReset(conn, 0);
                    continue;
                }
                Debug.LogError($"[CONTROL] Retrying pending client control reset componentId=0x{componentId:X4} attempt={conn.PendingClientControlResetAttempts}");
                SendClientControlReset(conn, componentId);
            }
        }

        private bool WriteClientControlUpdate(RRConnection conn, LEWriter writer, ushort componentId, bool followClient)
        {
            return WriteClientControlUpdate(conn, writer, componentId, followClient, "CLIENT-CONTROL", null);
        }

        private bool WriteClientControlUpdate(RRConnection conn, LEWriter writer, ushort componentId, bool followClient, string packetName, uint? forcedHPWire)
        {
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x64);
            writer.WriteByte(followClient ? (byte)0x01 : (byte)0x00);
            if (forcedHPWire.HasValue)
            {
                GetValidationCutoff(out uint validationCutoffTick);
                uint avatarEntityId = conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u;
                return TryWriteResolvedEntitySynchInfo(writer, componentId, 0x64, EntitySynchInfoContext.ControlAck, packetName, EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, forcedHPWire.Value, packetName, avatarEntityId, componentId, 0x64, $"forced-player-hp; validationCutoffTick={validationCutoffTick}", validationCutoffTick));
            }
            return TryWriteEntitySynchForComponent(conn, writer, componentId, 0x64, EntitySynchInfoContext.ControlAck, packetName);
        }



        private bool HandleNPCClick(RRConnection conn, ushort componentId, ushort targetEntityID, byte responseId, byte sessionID)
        {
            Debug.LogError($"[NPC] ");
            Debug.LogError($"[NPC] Activate target=0x{targetEntityID:X4} responseId={responseId} sessionID={sessionID}");
            conn.SessionID = sessionID;

            bool matchedReflectedTarget = false;
            bool hasActivateTargetPosition = false;
            int activateTargetFixedX = 0;
            int activateTargetFixedY = 0;
            int activateFollowConnId = 0;
            ushort activateMovingEntityId = 0;
            if (TryGetZoneNpcsForConnection(conn, out var npcs))
            {
                var npc = npcs.FirstOrDefault(n => n.Id == targetEntityID);
                if (npc != null)
                {
                    matchedReflectedTarget = true;
                    if (npc.HasFixedPosition)
                    {
                        hasActivateTargetPosition = true;
                        activateTargetFixedX = npc.PosFixedX;
                        activateTargetFixedY = npc.PosFixedY;
                    }
                    conn.CurrentDialogNpcId = npc.GCClass;
                    if (npc.IsPosseMagnate)
                    {
                        SendSystemMessage(conn, "Open the Posse tab in your menu, or type /posse create <name> to start a posse (level 15+, 1,000,000 gold). Type /posse help for the full list of commands.");
                        Debug.LogError($"[POSSE] Player clicked PosseMagnate {npc.GCClass} - chat hint sent");
                    }
                    Debug.LogError($"[NPC]  Set CurrentDialogNpcId = {conn.CurrentDialogNpcId}");
                    Debug.LogError($"[NPC] Activate movement target=0x{targetEntityID:X4} fixed=({activateTargetFixedX},{activateTargetFixedY}) available={hasActivateTargetPosition}");
                }
            }

            if (!matchedReflectedTarget)
            {
                var targetConn = ResolveAvatarTargetOwnerForClient(conn, targetEntityID, out _);
                if (targetConn != null && targetConn != conn && targetConn.IsSpawned)
                {
                    matchedReflectedTarget = true;
                    hasActivateTargetPosition = true;
                    activateTargetFixedX = targetConn.PlayerPosFixedX;
                    activateTargetFixedY = targetConn.PlayerPosFixedY;
                    activateFollowConnId = targetConn.ConnId;
                    Debug.LogError($"[NPC] Activate player movement target=0x{targetEntityID:X4} owner={targetConn.LoginName} fixed=({activateTargetFixedX},{activateTargetFixedY})");
                }
            }

            if (!matchedReflectedTarget && BlingGnomeRuntime.Instance.TryResolveGnomeTargetPositionFixed(conn, targetEntityID, out int gnomeTargetFixedX, out int gnomeTargetFixedY, out _))
            {
                matchedReflectedTarget = true;
                hasActivateTargetPosition = true;
                activateTargetFixedX = gnomeTargetFixedX;
                activateTargetFixedY = gnomeTargetFixedY;
                activateMovingEntityId = targetEntityID;
                Debug.LogError($"[NPC] Activate Bling gnome movement target=0x{targetEntityID:X4} fixed=({activateTargetFixedX},{activateTargetFixedY})");
            }

            if (matchedReflectedTarget && hasActivateTargetPosition)
                StartActivateMoving(conn, targetEntityID, activateTargetFixedX, activateTargetFixedY, ACTIVATE_UNIT_TARGET_RADIUS_FIXED, activateFollowConnId, activateMovingEntityId);

            var npcActivateResponseMessage = new LEWriter();
            npcActivateResponseMessage.WriteByte(0x35);
            npcActivateResponseMessage.WriteUInt16(componentId);
            npcActivateResponseMessage.WriteByte(0x01);
            npcActivateResponseMessage.WriteByte(responseId);
            npcActivateResponseMessage.WriteByte(0x06);
            npcActivateResponseMessage.WriteByte(sessionID);
            npcActivateResponseMessage.WriteUInt16(targetEntityID);
            WritePlayerEntitySynch(conn, npcActivateResponseMessage);
            QueueClientEntityStream(conn, npcActivateResponseMessage.ToArray());
            Debug.LogError($"[NPC]  Queued activate response for scheduled CEM block");

            if (BlingGnomeRuntime.Instance.HasGnome(conn.ConnId))
            {
                uint gnomeEntityId = BlingGnomeRuntime.Instance.GetGnomeEntityId(conn.ConnId);
                if (targetEntityID == gnomeEntityId)
                {
                    Debug.LogError("[NPC] blingGnome clicked");
                    Debug.LogError("[NPC] blingGnome greetingSound=120 asset=Bling_Gnomes_Summon_01-04");
                }
            }
            return false;
        }

        private bool IsKnownTargetActionEntity(RRConnection conn, ushort targetEntityID)
        {
            if (conn == null || targetEntityID == 0) return false;
            if (TryGetZoneNpcsForConnection(conn, out var npcs) && npcs.Any(n => n.Id == targetEntityID)) return true;
            if (ResolveAvatarTargetOwnerForClient(conn, targetEntityID, out _) != null) return true;
            if (Combat.CombatRuntime.Instance.IsKnownMonsterTarget(targetEntityID, GetInstanceZoneKey(conn))) return true;
            if (BlingGnomeRuntime.Instance.TryResolveGnomeTargetPositionFixed(conn, targetEntityID, out _, out _, out _)) return true;
            if (_portalEntities.ContainsKey(targetEntityID)) return true;
            if (_chestEntities.ContainsKey(targetEntityID)) return true;
            if (_checkpointEntities.ContainsKey(targetEntityID)) return true;
            if (_droppedItems.ContainsKey(targetEntityID)) return true;
            return WorldEntitySpawner.Instance != null && WorldEntitySpawner.Instance.TryGetEntity(targetEntityID, out _);
        }

        private void QueueTargetActionAck(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId, string tag)
        {
            if (conn == null || componentId == 0) return;
            var ackMessage = new LEWriter();
            ackMessage.WriteByte(0x35);
            ackMessage.WriteUInt16(componentId);
            ackMessage.WriteByte(0x01);
            ackMessage.WriteByte(responseId);
            ackMessage.WriteByte(0x06);
            ackMessage.WriteByte(sessionId);
            ackMessage.WriteUInt16(targetEntityId);
            WritePlayerEntitySynch(conn, ackMessage);
            QueueClientEntityStream(conn, ackMessage.ToArray());
            Debug.LogError($"[{tag}] Queued reflected target action ack in scheduled CEM block target=0x{targetEntityId:X4} session={sessionId}");
        }

        private void TrackPendingPortalActivation(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId, ZonePortal portal)
        {
            conn.PendingPortalComponentId = componentId;
            conn.PendingPortalTargetEntityId = targetEntityId;
            conn.PendingPortalResponseId = responseId;
            conn.PendingPortalSessionId = sessionId;
            conn.PendingPortalTargetFixedX = portal.PosFixedX;
            conn.PendingPortalTargetFixedY = portal.PosFixedY;

            if (IsPendingPortalReached(conn))
                ActivatePendingPortal(conn);
            else
                Debug.LogError($"[PORTAL] Pending activation target=0x{targetEntityId:X4} posFixed=({portal.PosFixedX},{portal.PosFixedY}) playerFixed=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY})");
        }

        private void CheckPendingPortalActivation(RRConnection conn)
        {
            if (conn == null || conn.PendingPortalTargetEntityId == 0)
                return;

            if (!IsPendingPortalReached(conn))
                return;

            ActivatePendingPortal(conn);
        }

        private bool IsPortalActivationReached(RRConnection conn, ZonePortal portal)
        {
            if (conn == null || portal == null)
                return false;

            ResolveAuthoritativePlayerPositionFixed(conn, out int playerFixedX, out int playerFixedY, out _);
            long distSq = DistanceSqFixed(playerFixedX, playerFixedY, portal.PosFixedX, portal.PosFixedY);
            return distSq <= PORTAL_ACTIVATION_DISTANCE_FIXED_SQ;
        }

        private bool IsPendingPortalReached(RRConnection conn)
        {
            ResolveAuthoritativePlayerPositionFixed(conn, out int playerFixedX, out int playerFixedY, out _);
            long distSq = DistanceSqFixed(playerFixedX, playerFixedY, conn.PendingPortalTargetFixedX, conn.PendingPortalTargetFixedY);
            return distSq <= PORTAL_ACTIVATION_DISTANCE_FIXED_SQ;
        }

        private void ActivatePendingPortal(RRConnection conn)
        {
            ushort componentId = conn.PendingPortalComponentId;
            ushort targetEntityId = conn.PendingPortalTargetEntityId;
            byte responseId = conn.PendingPortalResponseId;
            byte sessionId = conn.PendingPortalSessionId;

            ClearPendingPortalActivation(conn);

            if (!_portalEntities.TryGetValue(targetEntityId, out var portal))
            {
                Debug.LogError($"[PORTAL] Pending activation target=0x{targetEntityId:X4} state=missing");
                return;
            }

            Debug.LogError($"[PORTAL] Pending activation reached target=0x{targetEntityId:X4} playerFixed=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY})");
            HandlePortalActivation(conn, componentId, targetEntityId, responseId, sessionId, portal);
        }

        private static void ClearPendingPortalActivation(RRConnection conn)
        {
            if (conn == null)
                return;

            conn.PendingPortalComponentId = 0;
            conn.PendingPortalTargetEntityId = 0;
            conn.PendingPortalResponseId = 0;
            conn.PendingPortalSessionId = 0;
            conn.PendingPortalTargetFixedX = 0;
            conn.PendingPortalTargetFixedY = 0;
        }

        private bool SendUseTargetActionResponse(RRConnection conn, ushort componentId, byte responseId, byte manipulatorId, byte useFlags, ushort targetId, EntitySynchInfoContext actionResponseContext, string actionResponseEntitySynchInfoTag, string reason, long admissionSequence = 0)
        {
            if (conn == null || componentId == 0)
                return false;

            conn.MessageQueue.EnqueueDeferred(
                () => BuildUseTargetActionResponse(
                    conn,
                    componentId,
                    responseId,
                    manipulatorId,
                    useFlags,
                    targetId,
                    actionResponseContext,
                    actionResponseEntitySynchInfoTag,
                    reason,
                    admissionSequence),
                componentId: componentId);
            return true;
        }

        private byte[] BuildUseTargetActionResponse(RRConnection conn, ushort componentId, byte responseId, byte manipulatorId, byte useFlags, ushort targetId, EntitySynchInfoContext actionResponseContext, string actionResponseEntitySynchInfoTag, string reason, long admissionSequence = 0)
        {
            if (conn == null || !conn.IsConnected || componentId == 0)
                return Array.Empty<byte>();
            if (admissionSequence > 0 && conn.LastPlayerUseTargetActionSequence != admissionSequence)
                return Array.Empty<byte>();

            var actionResponseMessage = new LEWriter();
            actionResponseMessage.WriteByte(0x35);
            actionResponseMessage.WriteUInt16(componentId);
            actionResponseMessage.WriteByte(0x01);
            actionResponseMessage.WriteByte(responseId);
            actionResponseMessage.WriteByte(0x50);
            actionResponseMessage.WriteByte(manipulatorId);
            actionResponseMessage.WriteByte(useFlags);
            actionResponseMessage.WriteUInt16(targetId);

            if (!TryWriteEntitySynchForComponent(conn, actionResponseMessage, componentId, 0x01, actionResponseContext, actionResponseEntitySynchInfoTag))
                return Array.Empty<byte>();

            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            Debug.LogError($"[ATTACK] action=buildActionResponse reason={reason} target={targetId} component={componentId} responseId={responseId} manip={manipulatorId} flags={useFlags} playerHp={state?.EntitySynchInfoHP ?? 0}");
            return actionResponseMessage.ToArray();
        }

        private bool TryBuildUseTargetActionFailure(RRConnection conn, ushort componentId, byte responseId, out byte[] message, long admissionSequence = 0)
        {
            message = Array.Empty<byte>();
            if (conn == null || componentId == 0)
                return false;
            if (admissionSequence > 0 && conn.LastPlayerUseTargetActionSequence != admissionSequence)
                return false;

            var actionFailureMessage = new LEWriter();
            actionFailureMessage.WriteByte(0x35);
            actionFailureMessage.WriteUInt16(componentId);
            actionFailureMessage.WriteByte(0x03);
            actionFailureMessage.WriteByte(responseId);
            if (!TryWriteEntitySynchForComponent(conn, actionFailureMessage, componentId, 0x03, EntitySynchInfoContext.ControlAck, "USE-TARGET-FAILED"))
                return false;

            message = actionFailureMessage.ToArray();
            return true;
        }

        private bool SendUseTargetActionFailure(RRConnection conn, ushort componentId, byte responseId, string reason, long admissionSequence = 0)
        {
            if (conn == null || componentId == 0)
                return false;

            conn.MessageQueue.EnqueueDeferred(
                () => TryBuildUseTargetActionFailure(conn, componentId, responseId, out byte[] message, admissionSequence)
                    ? message
                    : Array.Empty<byte>(),
                componentId: componentId);
            Debug.LogError($"[ATTACK] action=enqueueActionFailure reason={reason} component={componentId} responseId={responseId} admissionSequence={admissionSequence} writerSnapshot=True");
            return true;
        }

        private bool TryBuildUsePositionActionFailure(RRConnection conn, ushort componentId, byte responseId, out byte[] message)
        {
            message = Array.Empty<byte>();
            if (conn == null || componentId == 0)
                return false;

            var actionFailureMessage = new LEWriter();
            actionFailureMessage.WriteByte(0x35);
            actionFailureMessage.WriteUInt16(componentId);
            actionFailureMessage.WriteByte(0x03);
            actionFailureMessage.WriteByte(responseId);
            if (!TryWriteEntitySynchForComponent(conn, actionFailureMessage, componentId, 0x03, EntitySynchInfoContext.ControlAck, "USE-POSITION-FAILED"))
                return false;

            message = actionFailureMessage.ToArray();
            return true;
        }

        private bool SendUsePositionActionFailure(RRConnection conn, ushort componentId, byte responseId, string reason)
        {
            if (conn == null || componentId == 0)
                return false;

            conn.MessageQueue.EnqueueDeferred(
                () => TryBuildUsePositionActionFailure(conn, componentId, responseId, out byte[] message)
                    ? message
                    : Array.Empty<byte>(),
                componentId: componentId);
            Debug.LogError($"[SPELL-0x51] action=enqueueActionFailure reason={reason} component={componentId} responseId={responseId} writerSnapshot=True");
            return true;
        }

        private bool QueuePlayerUseTargetActionResponse(RRConnection conn, PendingPlayerUseTargetActionInput input, Combat.Monster monster, RRConnection targetPlayer)
        {
            if (conn == null
                || input.ComponentId == 0
                || !IsAvatarOrAvatarComponentId(conn, input.ComponentId))
                return false;
            bool queuesPendingBehaviorAction = input.GnomeEntityId != 0
                ? HasCurrentPlayerBehaviorSlot(conn)
                : targetPlayer != null
                ? ShouldQueuePendingUseTargetBehaviorAction(conn, input.UseFlags, targetPlayer)
                : ShouldQueuePendingUseTargetBehaviorAction(conn, input.UseFlags, monster);
            bool isSkillAction = input.UseFlags >= 100;
            EntitySynchInfoContext actionResponseContext = isSkillAction
                ? EntitySynchInfoContext.PlayerActionResponse
                : EntitySynchInfoContext.PlayerBasicAttackResponse;
            string actionResponseEntitySynchInfoTag = isSkillAction
                ? "PlayerActionResponse"
                : "PlayerBasicAttackResponse";
            if (!SendUseTargetActionResponse(
                conn,
                input.ComponentId,
                input.ResponseId,
                input.ManipulatorId,
                input.UseFlags,
                input.TargetId,
                actionResponseContext,
                actionResponseEntitySynchInfoTag,
                "scheduled-useTarget",
                input.AdmissionSequence))
                return false;

            if (queuesPendingBehaviorAction)
            {
                bool peerPendingRelayed = RelayPendingUseTargetActionToPeers(conn, input.ManipulatorId, input.UseFlags, input.TargetId, "MP-SWING-PENDING");
                Debug.LogError($"[MP-SWING-DEFER] src={conn.LoginName} target={input.TargetId} component={input.ComponentId} manip={input.ManipulatorId} flags={input.UseFlags} tick={_combatTick} peerPendingRelayed={peerPendingRelayed} sourceFunction=Behavior::processUpdate@0x00515620 opcode=0x01->Behavior::doActionLocal@0x00515130 slot=Behavior+0x78");
                return true;
            }

            RelayUseTargetActionToPeers(conn, input.ResponseId, input.ManipulatorId, input.UseFlags, input.TargetId, "MP-SWING");
            return true;
        }

        private long ReservePlayerUseTargetAction(RRConnection conn)
        {
            if (conn == null)
                return 0;
            long admissionSequence = ++_nextPlayerUseTargetAdmissionSequence;
            conn.LastPlayerUseTargetActionSequence = admissionSequence;
            return admissionSequence;
        }

        private bool IsActivePlayerUseTargetInput(PendingPlayerUseTargetActionInput input, RRConnection conn)
        {
            return conn != null
                && conn.HasActiveUseTarget
                && conn.ActiveUseTargetId == input.TargetId
                && conn.ActiveUseTargetPlayerConnId == input.TargetPlayerConnId
                && conn.ActiveUseTargetComponentId == input.ComponentId
                && conn.ActiveUseTargetFlags == input.UseFlags
                && conn.ActiveUseTargetSessionId == input.ManipulatorId;
        }

        private bool IsActivePlayerUseTargetBusyForInput(PendingPlayerUseTargetActionInput input, RRConnection conn, Combat.Monster monster)
        {
            return IsActivePlayerUseTargetInput(input, conn)
                && input.TargetPlayerConnId == 0
                && monster != null
                && Combat.WeaponUseRuntime.Instance.IsWeaponBusyOnKilledTarget(conn, monster);
        }

        private static bool IsUnavailableMonsterWeaponAction(PendingPlayerUseTargetActionInput input)
        {
            return input.TargetPlayerConnId == 0 && IsBasicWeaponManipulatorId(input.UseFlags);
        }

        private void RejectUnavailablePlayerUseTargetAction(
            RRConnection conn,
            PendingPlayerUseTargetActionInput input,
            Combat.Monster monster,
            uint simulationTick,
            string reason,
            bool failureAlreadyQueued = false)
        {
            if (conn == null)
                return;
            if (input.AdmissionSequence > 0 && conn.LastPlayerUseTargetActionSequence != input.AdmissionSequence)
            {
                Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=UseTarget conn={conn.ConnId} target={input.TargetId} monster={input.MonsterEntityId} admissionSequence={input.AdmissionSequence} currentSequence={conn.LastPlayerUseTargetActionSequence} reason=superseded");
                return;
            }
            if (IsUnavailableMonsterWeaponAction(input))
            {
                bool actionResponseQueued = input.PacketSent
                    || (conn.IsConnected && SendUseTargetActionResponse(
                        conn,
                        input.ComponentId,
                        input.ResponseId,
                        input.ManipulatorId,
                        input.UseFlags,
                        input.TargetId,
                        EntitySynchInfoContext.PlayerBasicAttackResponse,
                        "PlayerBasicAttackResponse",
                        reason,
                        input.AdmissionSequence));
                Debug.LogError($"[PLAYER-ACTION-REJECT] action=UseTarget conn={conn.ConnId} component={input.ComponentId} responseId={input.ResponseId} manip={input.ManipulatorId} target={input.TargetId} monster={input.MonsterEntityId} admissionSequence={input.AdmissionSequence} responseQueued={actionResponseQueued} packetSent={input.PacketSent} reason={reason} sourceFunction=Behavior::doActionLocal@0x00515130->UseTarget::validate@0x00547EE0->Weapon::validateUse@0x00597CA0");
                return;
            }
            if (IsActivePlayerUseTargetBusyForInput(input, conn, monster))
            {
                DiscardPendingUseTargetBehaviorAction(conn, "UseTarget-unavailable-preserve-busy-active");
                Debug.LogError($"[PLAYER-ACTION-FAILURE] action=UseTarget conn={conn.ConnId} target={input.TargetId} monster={input.MonsterEntityId} admissionSequence={input.AdmissionSequence} reason=active-weapon-busy-preserved");
                return;
            }

            bool actionFailureQueued = failureAlreadyQueued
                || (conn.IsConnected && SendUseTargetActionFailure(conn, input.ComponentId, input.ResponseId, reason, input.AdmissionSequence));
            byte activeSessionId = conn.ActiveUseTargetSessionId;
            bool hadActiveUseTarget = conn.HasActiveUseTarget;
            DiscardPendingUseTargetBehaviorAction(conn, $"UseTarget-{reason}");
            ClearUseTargetAndReleaseControl(conn, $"ATTACK-{reason}", input.ComponentId, true, hadActiveUseTarget);
            if (hadActiveUseTarget)
            {
                MarkPeerUseTargetTerminated(conn, activeSessionId, simulationTick);
                conn.PlayerBehaviorActionStoppedTick = simulationTick;
            }
            else
            {
                ArmPeerUseTargetTermination(conn, input.ManipulatorId, unchecked(simulationTick + 1));
            }
            Debug.LogError($"[PLAYER-ACTION-REJECT] action=UseTarget conn={conn.ConnId} component={input.ComponentId} responseId={input.ResponseId} manip={input.ManipulatorId} target={input.TargetId} monster={input.MonsterEntityId} admissionSequence={input.AdmissionSequence} failureQueued={actionFailureQueued} hadActiveUseTarget={hadActiveUseTarget} reason={reason}");
        }

        private bool QueuePendingPlayerUseTargetActionInput(RRConnection conn, ushort componentId, byte responseId,
            byte manipulatorId, byte useFlags, ushort targetId, Combat.Monster monster, long admissionSequence = 0)
        {
            if (conn == null || monster == null || componentId == 0 || monster.EntityId == 0)
                return false;
            if (admissionSequence <= 0)
                admissionSequence = ReservePlayerUseTargetAction(conn);

            _pendingPlayerUseTargetActionInputs.Add(new PendingPlayerUseTargetActionInput
            {
                ConnId = conn.ConnId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)),
                ComponentId = componentId,
                ResponseId = responseId,
                ManipulatorId = manipulatorId,
                UseFlags = useFlags,
                TargetId = targetId,
                MonsterEntityId = monster.EntityId,
                ReceivedTick = _combatTick,
                AdmissionSequence = admissionSequence
            });
            Debug.LogError($"[PLAYER-ACTION-QUEUE] action=UseTarget conn={conn.ConnId} component={componentId} responseId={responseId} manip={manipulatorId} flags={useFlags} target={targetId} monster={monster.EntityId} admissionSequence={admissionSequence} receivedTick={_combatTick} phase=await-client-entity-writer");
            return true;
        }

        private bool QueuePendingPlayerUseTargetActionInput(RRConnection conn, ushort componentId, byte responseId,
            byte manipulatorId, byte useFlags, ushort targetId, RRConnection targetPlayer, long admissionSequence = 0)
        {
            if (conn == null || targetPlayer == null || componentId == 0 || targetPlayer.ConnId == 0)
                return false;
            if (admissionSequence <= 0)
                admissionSequence = ReservePlayerUseTargetAction(conn);

            _pendingPlayerUseTargetActionInputs.Add(new PendingPlayerUseTargetActionInput
            {
                ConnId = conn.ConnId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)),
                ComponentId = componentId,
                ResponseId = responseId,
                ManipulatorId = manipulatorId,
                UseFlags = useFlags,
                TargetId = targetId,
                TargetPlayerConnId = targetPlayer.ConnId,
                ReceivedTick = _combatTick,
                AdmissionSequence = admissionSequence
            });
            Debug.LogError($"[PLAYER-ACTION-QUEUE] action=UseTargetPvp conn={conn.ConnId} component={componentId} responseId={responseId} manip={manipulatorId} flags={useFlags} target={targetId} targetConn={targetPlayer.ConnId} admissionSequence={admissionSequence} receivedTick={_combatTick} phase=await-client-entity-writer");
            return true;
        }

        private void QueuePendingPlayerUsePositionWeaponActionInput(
            RRConnection conn,
            ushort componentId,
            byte responseId,
            byte sessionId,
            byte manipulatorId,
            int targetFixedX,
            int targetFixedY,
            int targetFixedZ)
        {
            if (conn == null || componentId == 0 || !IsBasicWeaponManipulatorId(manipulatorId))
                return;
            _pendingPlayerUsePositionWeaponActionInputs.Add(new PendingPlayerUsePositionWeaponActionInput
            {
                ConnId = conn.ConnId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)),
                ComponentId = componentId,
                ResponseId = responseId,
                SessionId = sessionId,
                ManipulatorId = manipulatorId,
                TargetFixedX = targetFixedX,
                TargetFixedY = targetFixedY,
                TargetFixedZ = targetFixedZ,
                ReceivedTick = _combatTick
            });
            Debug.LogError($"[PLAYER-ACTION-QUEUE] action=UsePositionWeapon conn={conn.ConnId} component={componentId} responseId={responseId} session={sessionId} manipulator={manipulatorId} targetFixed=({targetFixedX},{targetFixedY},{targetFixedZ}) receivedTick={_combatTick} phase=await-client-entity-writer sourceFunction=UsePosition::processUpdate@0x00546BB0");
        }

        private int DiscardPendingPlayerUsePositionWeaponBehaviorAction(RRConnection conn, string source)
        {
            if (conn == null)
                return 0;
            int removed = _pendingPlayerUsePositionBehaviorActions.RemoveAll(action =>
                action.ConnId == conn.ConnId && action.WeaponAction);
            if (removed > 0)
                Debug.LogError($"[PLAYER-ACTION-PENDING-CLEAR] action=UsePositionWeapon conn={conn.ConnId} removed={removed} source={source ?? "unknown"} slot=Behavior+0x78");
            return removed;
        }

        private int DiscardPendingPlayerSpellBehaviorAction(RRConnection conn, long preserveSequence, string source)
        {
            if (conn == null)
                return 0;
            int removed = _pendingPlayerUsePositionBehaviorActions.RemoveAll(action =>
                action.ConnId == conn.ConnId
                && !action.WeaponAction
                && (preserveSequence == 0 || action.SourceSequence != preserveSequence));
            if (removed > 0)
                Debug.LogError($"[PLAYER-ACTION-PENDING-CLEAR] action=UsePositionSpell conn={conn.ConnId} removed={removed} preserveSequence={preserveSequence} source={source ?? "unknown"} reason=4 slot=Behavior+0x78 sourceFunction=Behavior::doActionLocal@0x00515130->Action::onActionRemoved@vtable+0xAC");
            return removed;
        }

        private bool HasCurrentPlayerBehaviorSlot(RRConnection conn)
        {
            return conn != null
                && (conn.HasActiveUseTarget
                    || conn.UsePositionActionMirrored
                    || HasActiveSkillBehaviorSlot(conn));
        }

        private uint ResolveUsePositionBehaviorBusyUntilTick(
            RRConnection conn,
            PendingPlayerUsePositionBehaviorAction action,
            uint simulationTick)
        {
            if (action.WeaponAction)
            {
                uint playerEntityId = conn?.Avatar != null && conn.Avatar.Id > 0
                    ? (uint)conn.Avatar.Id
                    : 0u;
                return playerEntityId != 0
                    && Combat.WeaponUseRuntime.Instance.TryGetUsePositionNaturalTerminationTick(playerEntityId, out uint terminationTick)
                        ? terminationTick
                        : simulationTick;
            }
            return TryGetActiveSkillBusyUntilTick(conn, action.ComponentId, action.ManipulatorId, out uint busyUntilTick)
                ? busyUntilTick
                : simulationTick;
        }

        private void StartPlayerUsePositionBehaviorAction(
            RRConnection conn,
            PendingPlayerUsePositionBehaviorAction action,
            uint simulationTick)
        {
            uint busyUntilTick = ResolveUsePositionBehaviorBusyUntilTick(conn, action, simulationTick);
            conn.UsePositionActionMirrored = true;
            conn.UsePositionActionStoppedFollowClient = false;
            conn.UsePositionActionComponentId = action.ComponentId;
            conn.UsePositionManipulatorId = action.ManipulatorId;
            conn.UsePositionActionSessionId = action.SessionId;
            conn.UsePositionActionTargetFixedX = action.TargetFixedX;
            conn.UsePositionActionTargetFixedY = action.TargetFixedY;
            conn.UsePositionActionTargetFixedZ = action.TargetFixedZ;
            conn.UsePositionActionApplyTick = simulationTick;
            conn.UsePositionActionBusyUntilTick = action.WeaponAction ? uint.MaxValue : busyUntilTick;
            conn.UsePositionWeaponAction = action.WeaponAction;
            conn.UsePositionActionAdmissionFollowClientRecords = Math.Max(0, action.FollowClientRecordsAtAdmission);
            if (!action.WeaponAction)
                ArmPeerUsePositionTermination(conn, action.SessionId, busyUntilTick > 0 ? busyUntilTick - 1 : simulationTick);
            Debug.LogError($"[PLAYER-ACTION-APPLY] action=UsePosition conn={conn.ConnId} component={action.ComponentId} session={action.SessionId} manipulator={action.ManipulatorId} weapon={action.WeaponAction} receivedTick={action.ReceivedTick} packetWriterTick={action.PacketWriterTick} simulationApplyTick={simulationTick} messageIndex={action.WireMessageIndex} followClientRecords={conn.UsePositionActionAdmissionFollowClientRecords} busyUntilTick={conn.UsePositionActionBusyUntilTick} targetFixed=({action.TargetFixedX},{action.TargetFixedY},{action.TargetFixedZ}) sourceFunction=ClientEntityManager::update@0x005D9E30->Behavior::doActionLocal@0x00515130->UsePosition::start@0x00546F80");
        }

        private void AdmitPlayerUsePositionBehaviorAction(
            RRConnection conn,
            PendingPlayerUsePositionBehaviorAction action,
            uint simulationTick)
        {
            DiscardPendingUseTargetBehaviorAction(conn, "UsePosition-replaces-pending-UseTarget");
            DiscardPendingSelfCastBehaviorAction(conn);
            int replaced = _pendingPlayerUsePositionBehaviorActions.RemoveAll(pending => pending.ConnId == conn.ConnId);
            if (HasCurrentPlayerBehaviorSlot(conn))
            {
                if (action.WeaponAction && conn.UsePositionWeaponAction)
                {
                    StopPlayerUsePositionAction(
                        conn,
                        simulationTick,
                        false,
                        "Behavior::doInterruptLocal@0x00515290->UsePosition::terminate@0x00515CC0");
                    StartPlayerUsePositionBehaviorAction(conn, action, simulationTick);
                    return;
                }
                _pendingPlayerUsePositionBehaviorActions.Add(action);
                Debug.LogError($"[PLAYER-ACTION-PENDING] action=UsePosition conn={conn.ConnId} session={action.SessionId} manipulator={action.ManipulatorId} weapon={action.WeaponAction} replaced={replaced} packetWriterTick={action.PacketWriterTick} simulationApplyTick={simulationTick} messageIndex={action.WireMessageIndex} sourceFunction=Behavior::doActionLocal@0x00515130 slot=Behavior+0x78");
                return;
            }
            StartPlayerUsePositionBehaviorAction(conn, action, simulationTick);
        }

        private void PromotePendingPlayerUsePositionBehaviorActions(uint simulationTick)
        {
            for (int actionIndex = 0; actionIndex < _pendingPlayerUsePositionBehaviorActions.Count;)
            {
                PendingPlayerUsePositionBehaviorAction action = _pendingPlayerUsePositionBehaviorActions[actionIndex];
                if (!_connections.TryGetValue(action.ConnId, out RRConnection conn)
                    || conn == null
                    || !conn.IsConnected
                    || !conn.IsSpawned
                    || !IsPendingConnectionInstanceCurrent(conn, action.InstanceKey))
                {
                    _pendingPlayerUsePositionBehaviorActions.RemoveAt(actionIndex);
                    continue;
                }
                if (HasCurrentPlayerBehaviorSlot(conn))
                {
                    actionIndex++;
                    continue;
                }
                _pendingPlayerUsePositionBehaviorActions.RemoveAt(actionIndex);
                if (action.WeaponAction
                    && !TryRegisterPendingPlayerUsePositionWeaponAction(conn, action, simulationTick))
                    continue;
                StartPlayerUsePositionBehaviorAction(conn, action, simulationTick);
            }
        }

        private bool TryRegisterPendingPlayerUsePositionWeaponAction(
            RRConnection conn,
            PendingPlayerUsePositionBehaviorAction action,
            uint simulationTick)
        {
            if (conn == null || conn.Avatar == null || conn.Avatar.Id <= 0)
                return false;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null)
                return false;
            bool used = Combat.WeaponUseRuntime.Instance.RegisterUsePosition(
                conn.ConnId.ToString(),
                state,
                conn,
                action.ComponentId,
                action.SessionId,
                action.ManipulatorId,
                action.TargetFixedX,
                action.TargetFixedY,
                action.TargetFixedZ,
                simulationTick);
            if (!used)
            {
                Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=UsePositionWeapon conn={conn.ConnId} session={action.SessionId} manipulator={action.ManipulatorId} simulationApplyTick={simulationTick} reason=manipulator-use-failed sourceFunction=UsePosition::CheckInitUse@0x00547850->UsePosition::validateManipulator@0x00547760->MeleeWeapon::use@0x00591810");
                return false;
            }
            ClearZoneSpawnInvulnerability(conn, $"ACTION-0x51 manipulator={action.ManipulatorId}");
            return true;
        }

        private static void ClearPendingUseTargetBehaviorAction(RRConnection conn)
        {
            if (conn == null)
                return;
            conn.HasPendingUseTargetAction = false;
            conn.PendingUseTargetActionId = 0;
            conn.PendingUseTargetMonsterEntityId = 0;
            conn.PendingUseTargetPlayerConnId = 0;
            conn.PendingUseTargetFlags = 0;
            conn.PendingUseTargetComponentId = 0;
            conn.PendingUseTargetSessionId = 0;
            conn.PendingUseTargetResponseId = 0;
            conn.PendingUseTargetReceivedTick = 0;
            conn.PendingUseTargetActionSequence = 0;
            conn.PendingUseTargetIsRedundant = false;
        }

        private bool DiscardPendingUseTargetBehaviorAction(RRConnection conn, string source)
        {
            if (conn == null || !conn.HasPendingUseTargetAction)
                return false;
            ushort targetId = conn.PendingUseTargetActionId;
            uint monsterEntityId = conn.PendingUseTargetMonsterEntityId;
            byte sessionId = conn.PendingUseTargetSessionId;
            ClearPendingUseTargetBehaviorAction(conn);
            Debug.LogError($"[PLAYER-ACTION-PENDING-CLEAR] action=UseTarget conn={conn.ConnId} target={targetId} monster={monsterEntityId} session={sessionId} source={source ?? "unknown"} reason=4 slot=Behavior+0x78 sourceFunction=Behavior::doActionLocal@0x00515130->Action::onActionRemoved@vtable+0xAC");
            return true;
        }

        private static bool IsUseTargetActionRedundant(byte currentManipulatorId, ushort currentTargetId,
            byte incomingManipulatorId, ushort incomingTargetId)
        {
            bool currentWeapon = currentManipulatorId == 10 || currentManipulatorId == 11;
            bool incomingWeapon = incomingManipulatorId == 10 || incomingManipulatorId == 11;
            bool manipulatorEquivalent = (currentWeapon && incomingWeapon) || currentManipulatorId == incomingManipulatorId;
            return manipulatorEquivalent && currentTargetId == incomingTargetId;
        }

        private void QueuePendingUseTargetBehaviorAction(RRConnection conn, ushort componentId, byte responseId,
            byte manipulatorId, byte useFlags, ushort targetId, Combat.Monster monster)
        {
            if (conn == null || monster == null)
                return;
            DiscardPendingPlayerUsePositionWeaponBehaviorAction(conn, "UseTarget-replaces-pending-UsePosition");
            DiscardPendingPlayerSpellBehaviorAction(conn, 0, "UseTarget-replaces-pending-UsePosition");
            DiscardPendingSelfCastBehaviorAction(conn);
            bool replaced = conn.HasPendingUseTargetAction;
            ushort replacedTarget = conn.PendingUseTargetActionId;
            bool isRedundant = conn.HasActiveUseTarget
                && IsUseTargetActionRedundant(conn.ActiveUseTargetFlags, conn.ActiveUseTargetId, useFlags, targetId);
            conn.HasPendingUseTargetAction = true;
            conn.PendingUseTargetActionId = targetId;
            conn.PendingUseTargetMonsterEntityId = monster.EntityId;
            conn.PendingUseTargetFlags = useFlags;
            conn.PendingUseTargetComponentId = componentId;
            conn.PendingUseTargetSessionId = manipulatorId;
            conn.PendingUseTargetResponseId = responseId;
            conn.PendingUseTargetReceivedTick = _combatTick;
            conn.PendingUseTargetActionSequence = conn.LastPlayerUseTargetActionSequence;
            conn.PendingUseTargetIsRedundant = isRedundant;
            Debug.LogError($"[PLAYER-ACTION-PENDING] action=UseTarget conn={conn.ConnId} current={conn.ActiveUseTargetId} currentManipulator={conn.ActiveUseTargetFlags} target={targetId} incomingManipulator={useFlags} redundant={isRedundant} monster={monster.EntityId} replaced={replaced} replacedTarget={replacedTarget} tick={_combatTick} sourceFunction=Behavior::doActionLocal@0x00515130->UseTarget::IsRedundant@0x005482E0 slot=Behavior+0x78");
        }

        private void QueuePendingUseTargetBehaviorAction(RRConnection conn, ushort componentId, byte responseId,
            byte manipulatorId, byte useFlags, ushort targetId, RRConnection targetPlayer)
        {
            if (conn == null || targetPlayer == null)
                return;
            DiscardPendingPlayerUsePositionWeaponBehaviorAction(conn, "UseTarget-replaces-pending-UsePosition");
            DiscardPendingPlayerSpellBehaviorAction(conn, 0, "UseTarget-replaces-pending-UsePosition");
            DiscardPendingSelfCastBehaviorAction(conn);
            bool replaced = conn.HasPendingUseTargetAction;
            ushort replacedTarget = conn.PendingUseTargetActionId;
            bool isRedundant = conn.HasActiveUseTarget
                && conn.ActiveUseTargetPlayerConnId == targetPlayer.ConnId
                && IsUseTargetActionRedundant(conn.ActiveUseTargetFlags, conn.ActiveUseTargetId, useFlags, targetId);
            conn.HasPendingUseTargetAction = true;
            conn.PendingUseTargetActionId = targetId;
            conn.PendingUseTargetMonsterEntityId = 0;
            conn.PendingUseTargetPlayerConnId = targetPlayer.ConnId;
            conn.PendingUseTargetFlags = useFlags;
            conn.PendingUseTargetComponentId = componentId;
            conn.PendingUseTargetSessionId = manipulatorId;
            conn.PendingUseTargetResponseId = responseId;
            conn.PendingUseTargetReceivedTick = _combatTick;
            conn.PendingUseTargetActionSequence = conn.LastPlayerUseTargetActionSequence;
            conn.PendingUseTargetIsRedundant = isRedundant;
            Debug.LogError($"[PLAYER-ACTION-PENDING] action=UseTargetPvp conn={conn.ConnId} current={conn.ActiveUseTargetId} currentManipulator={conn.ActiveUseTargetFlags} target={targetId} targetConn={targetPlayer.ConnId} incomingManipulator={useFlags} redundant={isRedundant} replaced={replaced} replacedTarget={replacedTarget} tick={_combatTick}");
        }

        private bool ShouldQueuePendingUseTargetBehaviorAction(RRConnection conn, byte useFlags, Combat.Monster monster)
        {
            return conn != null
                && monster != null
                && monster.IsAlive
                && HasCurrentPlayerBehaviorSlot(conn);
        }

        private bool ShouldQueuePendingUseTargetBehaviorAction(RRConnection conn, byte useFlags, RRConnection targetPlayer)
        {
            return conn != null
                && targetPlayer != null
                && targetPlayer.IsConnected
                && targetPlayer.IsSpawned
                && HasCurrentPlayerBehaviorSlot(conn);
        }

        private void PromotePendingPlayerUseTargetBehaviorAction(uint playerEntityId, uint simulationTick)
        {
            RRConnection conn = FindConnectionByAvatarEntityId(playerEntityId);
            if (conn == null || !conn.HasPendingUseTargetAction)
                return;
            if (conn.PlayerBehaviorActionStoppedTick == simulationTick)
                return;
            if (HasCurrentPlayerBehaviorSlot(conn))
                return;
            ushort targetId = conn.PendingUseTargetActionId;
            uint monsterEntityId = conn.PendingUseTargetMonsterEntityId;
            int targetPlayerConnId = conn.PendingUseTargetPlayerConnId;
            byte useFlags = conn.PendingUseTargetFlags;
            ushort componentId = conn.PendingUseTargetComponentId;
            byte sessionId = conn.PendingUseTargetSessionId;
            byte responseId = conn.PendingUseTargetResponseId;
            uint receivedTick = conn.PendingUseTargetReceivedTick;
            long admissionSequence = conn.PendingUseTargetActionSequence;
            ClearPendingUseTargetBehaviorAction(conn);
            Combat.Monster monster = CombatRuntime.Instance.GetMonster(monsterEntityId);
            RRConnection targetPlayer = targetPlayerConnId != 0 && _connections.TryGetValue(targetPlayerConnId, out RRConnection resolvedTargetPlayer)
                ? resolvedTargetPlayer
                : null;
            bool monsterAvailable = monster != null && monster.IsAlive && CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) != 0;
            bool playerAvailable = targetPlayer != null && IsPvpActionTargetAvailable(conn, targetPlayer, useFlags);
            bool gnomeAvailable = monsterEntityId == 0 && targetPlayerConnId == 0 && IsBlingGnomeSkillTargetAvailable(conn, useFlags, targetId);
            if (!monsterAvailable && !playerAvailable && !gnomeAvailable)
            {
                bool sent = conn.IsConnected && SendUseTargetActionFailure(conn, componentId, responseId, "pending-target-unavailable", admissionSequence);
                ArmPeerUseTargetTermination(conn, sessionId, unchecked(simulationTick + 1));
                ushort controlComponentId = ResolveClientControlComponentId(conn, componentId);
                bool controlResetQueued = conn.IsConnected
                    && conn.IsSpawned
                    && controlComponentId != 0
                    && !HasCurrentPlayerBehaviorAction(conn, playerEntityId);
                if (controlResetQueued)
                    SendClientControlReset(conn, controlComponentId);
                Debug.LogError($"[PLAYER-ACTION-PROMOTE-SKIP] action=UseTarget conn={conn.ConnId} target={targetId} monster={monsterEntityId} targetConn={targetPlayerConnId} receivedTick={receivedTick} admissionSequence={admissionSequence} tick={simulationTick} actionFailureSent={sent} controlResetQueued={controlResetQueued} controlComponent={controlComponentId} reason=target-unavailable sourceFunction=Behavior::update@0x005154B0->UnitBehavior::onActionStopped@0x005205E0->UnitBehavior::RemarryClientToLogicalMovement@0x00520890");
                return;
            }
            if (gnomeAvailable)
                HandlePlayerUseBlingGnome(conn, componentId, responseId, sessionId, useFlags, targetId, behaviorChildStart: true);
            else if (targetPlayer != null)
                HandlePlayerAttackPlayer(conn, componentId, responseId, sessionId, useFlags, targetId, targetPlayer, behaviorChildStart: true);
            else
                HandlePlayerAttackMonster(conn, componentId, responseId, sessionId, useFlags, targetId, monster, behaviorChildStart: true);
            bool startedCurrentUseTarget = conn.HasActiveUseTarget
                && conn.ActiveUseTargetId == targetId
                && conn.ActiveUseTargetPlayerConnId == targetPlayerConnId
                && conn.ActiveUseTargetComponentId == componentId
                && conn.ActiveUseTargetFlags == useFlags
                && conn.ActiveUseTargetSessionId == sessionId;
            if (startedCurrentUseTarget)
                ProcessPlayerUseTargetStart(conn, simulationTick);
            Debug.LogError($"[PLAYER-ACTION-PROMOTE] action=UseTarget conn={conn.ConnId} target={targetId} monster={monsterEntityId} targetConn={targetPlayerConnId} receivedTick={receivedTick} tick={simulationTick} startedCurrent={startedCurrentUseTarget} peerAdmission=pending-response sourceFunction=Behavior::update@0x005154B0 slot=Behavior+0x78->Behavior+0x70");
        }

        private void ProcessPlayerUseTargetStart(RRConnection conn, uint simulationTick)
        {
            if (conn == null
                || !conn.HasActiveUseTarget
                || conn.ActiveUseTargetFlags >= 100
                || !conn.UseTargetMovingActive
                || !conn.UseTargetMovingActionMirrored)
                return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null
                || !TryResolveUseTargetMovingTargetFixedReadOnly(conn, out int targetFixedX, out int targetFixedY, out _))
                return;
            int actorFixedX = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedX : conn.PlayerPosFixedX;
            int actorFixedY = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedY : conn.PlayerPosFixedY;
            int actorFixedZ = conn.HasLivePlayerPosition ? conn.LivePlayerPosFixedZ : conn.PlayerPosFixedZ;
            if (conn.UseTargetMovingHasFixedState)
            {
                actorFixedX = conn.UseTargetMovingFixedX;
                actorFixedY = conn.UseTargetMovingFixedY;
                actorFixedZ = conn.UseTargetMovingFixedZ;
            }
            TryCompleteActionMirroredUseTargetMovingForUse(
                conn,
                state,
                simulationTick,
                targetFixedX,
                targetFixedY,
                actorFixedX,
                actorFixedY,
                actorFixedZ);
        }

        private void FlushPendingPlayerUseTargetActionInputs(uint writerTick)
        {
            if (_pendingPlayerUseTargetActionInputs.Count == 0)
                return;

            var pending = new List<PendingPlayerUseTargetActionInput>(_pendingPlayerUseTargetActionInputs.Count);
            pending.AddRange(_pendingPlayerUseTargetActionInputs);
            _pendingPlayerUseTargetActionInputs.Clear();
            var visitedConnections = new HashSet<int>();

            foreach (PendingPlayerUseTargetActionInput input in pending)
            {
                if (!_connections.TryGetValue(input.ConnId, out RRConnection conn)
                    || conn == null
                    || !conn.IsConnected
                    || !conn.IsSpawned)
                {
                    Debug.LogError($"[PLAYER-ACTION-FLUSH-SKIP] action=UseTarget conn={input.ConnId} target={input.TargetId} monster={input.MonsterEntityId} receivedTick={input.ReceivedTick} writerTick={writerTick} reason=connection-unavailable");
                    continue;
                }

                if (!IsPendingPlayerUseTargetInputAdmissionCurrent(input, conn))
                {
                    string reason = input.AdmissionSequence > 0 && conn.LastPlayerUseTargetActionSequence != input.AdmissionSequence
                        ? "superseded"
                        : "stale-instance";
                    Debug.LogError($"[PLAYER-ACTION-FLUSH-SKIP] action=UseTarget conn={input.ConnId} target={input.TargetId} monster={input.MonsterEntityId} admissionSequence={input.AdmissionSequence} currentSequence={conn.LastPlayerUseTargetActionSequence} instance='{input.InstanceKey}' receivedTick={input.ReceivedTick} writerTick={writerTick} reason={reason}");
                    continue;
                }

                if (input.ResponseQueued)
                {
                    _pendingPlayerUseTargetActionInputs.Add(input);
                    continue;
                }
                if (!visitedConnections.Add(input.ConnId))
                {
                    _pendingPlayerUseTargetActionInputs.Add(input);
                    continue;
                }

                Combat.Monster monster = CombatRuntime.Instance.GetMonster(input.MonsterEntityId);
                RRConnection targetPlayer = input.TargetPlayerConnId != 0 && _connections.TryGetValue(input.TargetPlayerConnId, out RRConnection resolvedTargetPlayer)
                    ? resolvedTargetPlayer
                    : null;
                bool monsterAvailable = monster != null && monster.IsAlive && CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) != 0;
                bool playerAvailable = targetPlayer != null && IsPvpActionTargetAvailable(conn, targetPlayer, input.UseFlags);
                bool gnomeAvailable = input.GnomeEntityId != 0 && IsBlingGnomeSkillTargetAvailable(conn, input.UseFlags, input.TargetId);
                if (!monsterAvailable && !playerAvailable && !gnomeAvailable)
                {
                    RejectUnavailablePlayerUseTargetAction(conn, input, monster, writerTick, "scheduled-target-unavailable");
                    continue;
                }

                try
                {
                    if (!QueuePlayerUseTargetActionResponse(conn, input, monster, targetPlayer))
                    {
                        Debug.LogError($"[PLAYER-ACTION-FLUSH-SKIP] action=UseTarget conn={input.ConnId} target={input.TargetId} monster={input.MonsterEntityId} receivedTick={input.ReceivedTick} writerTick={writerTick} reason=response-unavailable");
                        continue;
                    }
                    PendingPlayerUseTargetActionInput queuedInput = input;
                    queuedInput.ResponseQueued = true;
                    _pendingPlayerUseTargetActionInputs.Add(queuedInput);
                    Debug.LogError($"[PLAYER-ACTION-FLUSH] action=UseTarget conn={input.ConnId} component={input.ComponentId} responseId={input.ResponseId} manip={input.ManipulatorId} flags={input.UseFlags} target={input.TargetId} monster={input.MonsterEntityId} receivedTick={input.ReceivedTick} queueTick={writerTick} phase=client-entity-packet-queued");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[PLAYER-ACTION-FLUSH-ERROR] action=UseTarget conn={input.ConnId} target={input.TargetId} monster={input.MonsterEntityId} receivedTick={input.ReceivedTick} writerTick={writerTick} error={ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private void RewriteUnavailablePendingPlayerUseTargetActionMessages(
            RRConnection conn,
            List<byte[]> messages,
            uint packetWriterTick)
        {
            if (conn == null || messages == null || _pendingPlayerUseTargetActionInputs.Count == 0)
                return;

            for (int inputIndex = _pendingPlayerUseTargetActionInputs.Count - 1; inputIndex >= 0; inputIndex--)
            {
                PendingPlayerUseTargetActionInput input = _pendingPlayerUseTargetActionInputs[inputIndex];
                if (input.ConnId != conn.ConnId || !input.ResponseQueued || input.PacketSent)
                    continue;

                int messageIndex = -1;
                for (int candidateIndex = 0; candidateIndex < messages.Count; candidateIndex++)
                {
                    if (!IsPlayerUseTargetActionResponseMessage(messages[candidateIndex], input))
                        continue;
                    messageIndex = candidateIndex;
                    break;
                }
                if (messageIndex < 0)
                {
                    if (input.AdmissionSequence > 0 && conn.LastPlayerUseTargetActionSequence != input.AdmissionSequence)
                    {
                        _pendingPlayerUseTargetActionInputs.RemoveAt(inputIndex);
                        Debug.LogError($"[PLAYER-ACTION-PACKET-REJECT] action=UseTarget conn={input.ConnId} component={input.ComponentId} responseId={input.ResponseId} target={input.TargetId} admissionSequence={input.AdmissionSequence} currentSequence={conn.LastPlayerUseTargetActionSequence} reason=superseded");
                    }
                    continue;
                }

                if (input.AdmissionSequence > 0 && conn.LastPlayerUseTargetActionSequence != input.AdmissionSequence)
                {
                    messages.RemoveAt(messageIndex);
                    _pendingPlayerUseTargetActionInputs.RemoveAt(inputIndex);
                    Debug.LogError($"[PLAYER-ACTION-PACKET-REJECT] action=UseTarget conn={input.ConnId} component={input.ComponentId} responseId={input.ResponseId} target={input.TargetId} admissionSequence={input.AdmissionSequence} currentSequence={conn.LastPlayerUseTargetActionSequence} reason=superseded");
                    continue;
                }

                Combat.Monster monster = CombatRuntime.Instance.GetMonster(input.MonsterEntityId);
                RRConnection targetPlayer = input.TargetPlayerConnId != 0 && _connections.TryGetValue(input.TargetPlayerConnId, out RRConnection resolvedTargetPlayer)
                    ? resolvedTargetPlayer
                    : null;
                if ((monster != null && monster.IsAlive && CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) != 0)
                    || (targetPlayer != null && IsPvpActionTargetAvailable(conn, targetPlayer, input.UseFlags))
                    || (input.GnomeEntityId != 0 && IsBlingGnomeSkillTargetAvailable(conn, input.UseFlags, input.TargetId)))
                    continue;

                if (IsUnavailableMonsterWeaponAction(input))
                    continue;

                if (IsActivePlayerUseTargetBusyForInput(input, conn, monster))
                {
                    messages.RemoveAt(messageIndex);
                    _pendingPlayerUseTargetActionInputs.RemoveAt(inputIndex);
                    Debug.LogError($"[PLAYER-ACTION-PACKET-REJECT] action=UseTarget conn={input.ConnId} component={input.ComponentId} responseId={input.ResponseId} target={input.TargetId} admissionSequence={input.AdmissionSequence} reason=active-weapon-busy-preserved");
                    continue;
                }

                bool failureBuilt = TryBuildUseTargetActionFailure(
                    conn,
                    input.ComponentId,
                    input.ResponseId,
                    out byte[] actionFailureMessage,
                    input.AdmissionSequence);
                if (failureBuilt)
                    messages[messageIndex] = actionFailureMessage;
                else
                    messages.RemoveAt(messageIndex);
                _pendingPlayerUseTargetActionInputs.RemoveAt(inputIndex);
                RejectUnavailablePlayerUseTargetAction(conn, input, monster, packetWriterTick, "target-unavailable", failureBuilt);
                Debug.LogError($"[PLAYER-ACTION-PACKET-REJECT] action=UseTarget conn={input.ConnId} component={input.ComponentId} responseId={input.ResponseId} manip={input.ManipulatorId} target={input.TargetId} monster={input.MonsterEntityId} admissionSequence={input.AdmissionSequence} receivedTick={input.ReceivedTick} packetWriterTick={packetWriterTick} actionFailureBuilt={failureBuilt} reason=target-unavailable phase=client-entity-flush");
            }
        }

        private static bool IsPlayerUseTargetActionResponseMessage(byte[] message, PendingPlayerUseTargetActionInput input)
        {
            return message != null
                && message.Length >= 10
                && message[0] == 0x35
                && (ushort)(message[1] | (message[2] << 8)) == input.ComponentId
                && message[3] == 0x01
                && message[4] == input.ResponseId
                && message[5] == 0x50
                && message[6] == input.ManipulatorId
                && message[7] == input.UseFlags
                && (ushort)(message[8] | (message[9] << 8)) == input.TargetId;
        }

        private static bool IsPlayerCancelActionResponseMessage(byte[] message, PendingPlayerCancelActionInput input)
        {
            return message != null
                && message.Length >= 5
                && message[0] == 0x35
                && (ushort)(message[1] | (message[2] << 8)) == input.ComponentId
                && message[3] == 0x03
                && message[4] == input.SessionId;
        }

        private static uint ReadQueuedActionUInt32(byte[] message, int offset)
        {
            return (uint)(message[offset]
                | message[offset + 1] << 8
                | message[offset + 2] << 16
                | message[offset + 3] << 24);
        }

        private static bool IsPlayerUsePositionActionResponseMessage(byte[] message, PendingSpell pending)
        {
            return message != null
                && message.Length >= 20
                && message[0] == 0x35
                && (ushort)(message[1] | (message[2] << 8)) == pending.ComponentId
                && message[3] == 0x01
                && message[4] == pending.ActionResponseId
                && message[5] == 0x51
                && message[6] == pending.ActionResponseSessionId
                && message[7] == pending.UseFlags
                && ReadQueuedActionUInt32(message, 8) == unchecked((uint)pending.AimFixedX)
                && ReadQueuedActionUInt32(message, 12) == unchecked((uint)pending.AimFixedY)
                && ReadQueuedActionUInt32(message, 16) == unchecked((uint)pending.ActionAimFixedZ);
        }

        private static bool IsPlayerUsePositionWeaponActionResponseMessage(
            byte[] message,
            PendingPlayerUsePositionWeaponActionInput input)
        {
            return message != null
                && message.Length >= 20
                && message[0] == 0x35
                && (ushort)(message[1] | (message[2] << 8)) == input.ComponentId
                && message[3] == 0x01
                && message[4] == input.ResponseId
                && message[5] == 0x51
                && message[6] == input.SessionId
                && message[7] == input.ManipulatorId
                && ReadQueuedActionUInt32(message, 8) == unchecked((uint)input.TargetFixedX)
                && ReadQueuedActionUInt32(message, 12) == unchecked((uint)input.TargetFixedY)
                && ReadQueuedActionUInt32(message, 16) == unchecked((uint)input.TargetFixedZ);
        }

        private void MarkPendingPlayerUseTargetActionPacketsFlushed(
            RRConnection conn,
            IReadOnlyList<byte[]> messages,
            uint packetWriterTick,
            uint simulationApplyTick)
        {
            if (conn == null || messages == null || messages.Count == 0 || _pendingPlayerUseTargetActionInputs.Count == 0)
                return;

            ulong unitFollowClientOrdinal = conn.UnitFollowClientReceivedOrdinal;
            ushort unitBehaviorId = conn.UnitBehaviorId <= ushort.MaxValue
                ? (ushort)conn.UnitBehaviorId
                : (ushort)0;
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                for (int inputIndex = 0; inputIndex < _pendingPlayerUseTargetActionInputs.Count; inputIndex++)
                {
                    PendingPlayerUseTargetActionInput input = _pendingPlayerUseTargetActionInputs[inputIndex];
                    if (input.ConnId != conn.ConnId
                        || !input.ResponseQueued
                        || input.PacketSent
                        || !IsPlayerUseTargetActionResponseMessage(message, input))
                        continue;
                    input.PacketSent = true;
                    input.PacketWriterTick = packetWriterTick;
                    input.SimulationApplyTick = simulationApplyTick;
                    input.WireMessageIndex = messageIndex;
                    input.UnitFollowClientWaitThroughOrdinal = unitFollowClientOrdinal;
                    _pendingPlayerUseTargetActionInputs[inputIndex] = input;
                    Debug.LogError($"[PLAYER-ACTION-PACKET] action=UseTarget conn={input.ConnId} component={input.ComponentId} manip={input.ManipulatorId} target={input.TargetId} packetWriterTick={packetWriterTick} simulationApplyTick={simulationApplyTick} messageIndex={messageIndex} unitFollowClientWaitThroughOrdinal={unitFollowClientOrdinal} phase=client-entity-flush");
                    break;
                }
                if (unitBehaviorId != 0)
                    unitFollowClientOrdinal = checked(unitFollowClientOrdinal + (ulong)MessageQueue.CountUnitMoverRecords(message, unitBehaviorId));
            }
        }

        private static bool IsPlayerActivateActionResponseMessage(
            byte[] message,
            ushort componentId,
            ushort targetEntityId)
        {
            return message != null
                && message.Length >= 9
                && message[0] == 0x35
                && (ushort)(message[1] | (message[2] << 8)) == componentId
                && message[3] == 0x01
                && message[5] == 0x06
                && (ushort)(message[7] | (message[8] << 8)) == targetEntityId;
        }

        private static void MarkPendingPlayerActivateActionPacketFlushed(
            RRConnection conn,
            List<byte[]> messages,
            uint packetWriterTick)
        {
            if (conn == null
                || messages == null
                || messages.Count == 0
                || !conn.UseTargetMovingActive
                || !conn.UseTargetMovingActivate
                || conn.UseTargetMovingActivateTargetId == 0
                || conn.UnitBehaviorId == 0
                || conn.UnitBehaviorId > ushort.MaxValue)
                return;

            ushort unitBehaviorId = (ushort)conn.UnitBehaviorId;
            ushort targetEntityId = conn.UseTargetMovingActivateTargetId;
            ulong unitFollowClientOrdinal = conn.UnitFollowClientReceivedOrdinal;
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                if (IsPlayerActivateActionResponseMessage(message, unitBehaviorId, targetEntityId))
                {
                    conn.UseTargetMovingInputAdmitted = true;
                    conn.UseTargetMovingAdmissionTick = packetWriterTick;
                    conn.UseTargetMovingUnitFollowClientWaitThroughOrdinal = unitFollowClientOrdinal;
                    conn.UseTargetMovingUnitFollowClientWaitActive = conn.UnitFollowClientAppliedOrdinal < unitFollowClientOrdinal;
                    Debug.LogError($"[PLAYER-ACTION-PACKET] action=Activate conn={conn.ConnId} component={unitBehaviorId} target={targetEntityId} packetWriterTick={packetWriterTick} admissionTick={conn.UseTargetMovingAdmissionTick} messageIndex={messageIndex} unitFollowClientWaitThroughOrdinal={unitFollowClientOrdinal} unitFollowClientAppliedOrdinal={conn.UnitFollowClientAppliedOrdinal} waitActive={conn.UseTargetMovingUnitFollowClientWaitActive} phase=client-entity-flush sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                    return;
                }
                unitFollowClientOrdinal = checked(unitFollowClientOrdinal + (ulong)MessageQueue.CountUnitMoverRecords(message, unitBehaviorId));
            }
        }

        private void MarkPendingPlayerCancelActionPacketsFlushed(
            RRConnection conn,
            IReadOnlyList<byte[]> messages,
            uint packetWriterTick,
            uint simulationApplyTick)
        {
            if (conn == null || messages == null || messages.Count == 0 || _pendingPlayerCancelActionInputs.Count == 0)
                return;

            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                for (int inputIndex = 0; inputIndex < _pendingPlayerCancelActionInputs.Count; inputIndex++)
                {
                    PendingPlayerCancelActionInput input = _pendingPlayerCancelActionInputs[inputIndex];
                    if (input.ConnId != conn.ConnId
                        || input.PacketSent
                        || !IsPlayerCancelActionResponseMessage(message, input))
                        continue;
                    input.PacketSent = true;
                    input.PacketWriterTick = packetWriterTick;
                    input.SimulationApplyTick = simulationApplyTick;
                    input.MessageIndex = messageIndex;
                    _pendingPlayerCancelActionInputs[inputIndex] = input;
                    Debug.LogError($"[PLAYER-ACTION-PACKET] action=Cancel conn={input.ConnId} component={input.ComponentId} session={input.SessionId} receivedTick={input.ReceivedTick} packetWriterTick={packetWriterTick} simulationApplyTick={simulationApplyTick} messageIndex={messageIndex} phase=client-entity-flush");
                    break;
                }
            }
        }

        private void MarkPendingPlayerUsePositionActionPacketsFlushed(
            RRConnection conn,
            IReadOnlyList<byte[]> messages,
            uint packetWriterTick,
            uint simulationApplyTick)
        {
            if (conn == null || messages == null || messages.Count == 0)
                return;
            ushort unitBehaviorId = conn.UnitBehaviorId <= ushort.MaxValue
                ? (ushort)conn.UnitBehaviorId
                : (ushort)0;
            int followClientRecords = conn.UnitFollowClientMovementSamples.Count;
            for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                byte[] message = messages[messageIndex];
                bool matched = false;
                for (int inputIndex = 0; inputIndex < _pendingPlayerUsePositionWeaponActionInputs.Count; inputIndex++)
                {
                    PendingPlayerUsePositionWeaponActionInput input = _pendingPlayerUsePositionWeaponActionInputs[inputIndex];
                    if (input.ConnId != conn.ConnId
                        || input.PacketSent
                        || !IsPlayerUsePositionWeaponActionResponseMessage(message, input))
                        continue;
                    input.PacketSent = true;
                    input.PacketWriterTick = packetWriterTick;
                    input.SimulationApplyTick = simulationApplyTick;
                    input.WireMessageIndex = messageIndex;
                    input.AdmissionSequence = ++_nextPlayerUsePositionAdmissionSequence;
                    input.FollowClientRecordsAtAdmission = followClientRecords;
                    _pendingPlayerUsePositionWeaponActionInputs[inputIndex] = input;
                    matched = true;
                    Debug.LogError($"[PLAYER-ACTION-PACKET] action=UsePositionWeapon conn={input.ConnId} component={input.ComponentId} responseId={input.ResponseId} session={input.SessionId} manipulator={input.ManipulatorId} packetWriterTick={packetWriterTick} simulationApplyTick={simulationApplyTick} messageIndex={messageIndex} admissionSequence={input.AdmissionSequence} followClientRecords={followClientRecords} phase=client-entity-flush sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::doActionLocal@0x00515130");
                    break;
                }
                if (!matched)
                {
                    int pendingCount = _pendingSpells.Count;
                    for (int pendingIndex = 0; pendingIndex < pendingCount; pendingIndex++)
                    {
                        if (!_pendingSpells.TryDequeue(out PendingSpell pending))
                            break;
                        if (!matched
                            && pending.Conn?.ConnId == conn.ConnId
                            && pending.AwaitingActionResponsePacket
                            && IsPlayerUsePositionActionResponseMessage(message, pending))
                        {
                            pending.AwaitingActionResponsePacket = false;
                            pending.ActionResponseWriterTick = packetWriterTick;
                            pending.ActionResponseSimulationApplyTick = simulationApplyTick;
                            pending.ActionResponseWireMessageIndex = messageIndex;
                            pending.ActionAdmissionSequence = ++_nextPlayerUsePositionAdmissionSequence;
                            pending.FollowClientRecordsAtAdmission = followClientRecords;
                            matched = true;
                            Debug.LogError($"[SPELL-ACTION-PACKET] action=UsePosition conn={conn.ConnId} component={pending.ComponentId} responseId={pending.ActionResponseId} session={pending.ActionResponseSessionId} slot={pending.UseFlags} receivedTick={pending.ActionReceiveTick} packetWriterTick={packetWriterTick} simulationApplyTick={simulationApplyTick} messageIndex={messageIndex} admissionSequence={pending.ActionAdmissionSequence} phase=client-entity-flush sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::doActionLocal@0x00515130");
                        }
                        _pendingSpells.Enqueue(pending);
                    }
                }
                if (unitBehaviorId != 0)
                    followClientRecords = checked(followClientRecords + MessageQueue.CountUnitMoverRecords(message, unitBehaviorId));
            }
        }

        private static bool IsPlayerSelfCastActionResponseMessage(
            byte[] message,
            ushort componentId,
            byte responseId,
            byte sessionId,
            byte slotId)
        {
            return message != null
                && message.Length >= 8
                && message[0] == 0x35
                && (ushort)(message[1] | (message[2] << 8)) == componentId
                && message[3] == 0x01
                && message[4] == responseId
                && message[5] == 0x52
                && message[6] == sessionId
                && message[7] == slotId;
        }

        private byte[] BuildPlayerSelfCastActionResponse(
            RRConnection conn,
            ushort componentId,
            byte responseId,
            byte sessionId,
            byte slotId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x01);
            writer.WriteByte(responseId);
            writer.WriteByte(0x52);
            writer.WriteByte(sessionId);
            writer.WriteByte(slotId);
            if (!TryWriteEntitySynchForComponent(conn, writer, componentId, 0x01, EntitySynchInfoContext.PlayerActionResponse, "PlayerActionResponse"))
                return Array.Empty<byte>();
            return writer.ToArray();
        }

        private byte[] BuildPlayerUsePositionActionResponse(
            RRConnection conn,
            ushort componentId,
            byte responseId,
            byte sessionId,
            byte manipulatorId,
            int posX,
            int posY,
            int posZ)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x01);
            writer.WriteByte(responseId);
            writer.WriteByte(0x51);
            writer.WriteByte(sessionId);
            writer.WriteByte(manipulatorId);
            writer.WriteUInt32((uint)posX);
            writer.WriteUInt32((uint)posY);
            writer.WriteUInt32((uint)posZ);
            if (!TryWriteEntitySynchForComponent(conn, writer, componentId, 0x01, EntitySynchInfoContext.PlayerActionResponse, "PlayerActionResponse"))
                return Array.Empty<byte>();
            return writer.ToArray();
        }

        private void MarkPendingSelfCastActionResponsePacketsFlushed(
            RRConnection conn,
            IReadOnlyList<byte[]> messages,
            uint packetWriterTick,
            uint simulationApplyTick)
        {
            if (conn == null || messages == null || messages.Count == 0)
                return;

            lock (_pendingSelfCastActionLock)
            {
                if (_pendingSelfCastActions.Count == 0)
                    return;
                bool[] consumedMessages = new bool[messages.Count];
                for (int pendingIndex = 0; pendingIndex < _pendingSelfCastActions.Count; pendingIndex++)
                {
                    PendingSelfCastAction pending = _pendingSelfCastActions[pendingIndex];
                    if (pending.Conn?.ConnId != conn.ConnId || !pending.AwaitingActionResponsePacket)
                        continue;
                    for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
                    {
                        if (consumedMessages[messageIndex]
                            || !IsPlayerSelfCastActionResponseMessage(
                                messages[messageIndex],
                                pending.ComponentId,
                                pending.ActionResponseId,
                                pending.ActionResponseSessionId,
                                pending.SlotId))
                            continue;
                        pending.AwaitingActionResponsePacket = false;
                        pending.ActionResponseWriterTick = packetWriterTick;
                        pending.ActionResponseSimulationApplyTick = simulationApplyTick;
                        pending.ActionResponseWireMessageIndex = messageIndex;
                        _pendingSelfCastActions[pendingIndex] = pending;
                        consumedMessages[messageIndex] = true;
                        Debug.LogError($"[SPELL-ACTION-PACKET] action=SelfCast conn={conn.ConnId} component={pending.ComponentId} responseId={pending.ActionResponseId} session={pending.ActionResponseSessionId} slot={pending.SlotId} receivedTick={pending.ActionReceiveTick} packetWriterTick={packetWriterTick} simulationApplyTick={simulationApplyTick} messageIndex={messageIndex} phase=client-entity-flush sourceFunction=ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620->ActiveSkill::update@0x005392F0");
                        break;
                    }
                }
            }
        }

        private void ApplyPendingPlayerUseTargetActionInputs(uint simulationTick)
        {
            if (_pendingPlayerUseTargetActionInputs.Count == 0)
                return;

            var pending = new List<PendingPlayerUseTargetActionInput>(_pendingPlayerUseTargetActionInputs.Count);
            pending.AddRange(_pendingPlayerUseTargetActionInputs);
            _pendingPlayerUseTargetActionInputs.Clear();
            foreach (PendingPlayerUseTargetActionInput input in pending)
            {
                if (!IsPendingClientActionMessageSelected(input.ConnId, input.PacketWriterTick, input.WireMessageIndex)
                    || !input.PacketSent
                    || input.SimulationApplyTick == 0
                    || simulationTick < input.SimulationApplyTick)
                {
                    _pendingPlayerUseTargetActionInputs.Add(input);
                    continue;
                }
                if (!_connections.TryGetValue(input.ConnId, out RRConnection conn)
                    || conn == null
                    || !conn.IsConnected
                    || !conn.IsSpawned)
                {
                    Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=UseTarget conn={input.ConnId} target={input.TargetId} monster={input.MonsterEntityId} packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} reason=connection-unavailable");
                    continue;
                }
                if (!IsPendingPlayerUseTargetInputAdmissionCurrent(input, conn))
                {
                    string reason = input.AdmissionSequence > 0 && conn.LastPlayerUseTargetActionSequence != input.AdmissionSequence
                        ? "superseded"
                        : "stale-instance";
                    Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=UseTarget conn={input.ConnId} target={input.TargetId} monster={input.MonsterEntityId} admissionSequence={input.AdmissionSequence} currentSequence={conn.LastPlayerUseTargetActionSequence} instance='{input.InstanceKey}' packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} reason={reason}");
                    continue;
                }
                Combat.Monster monster = CombatRuntime.Instance.GetMonster(input.MonsterEntityId);
                RRConnection targetPlayer = input.TargetPlayerConnId != 0 && _connections.TryGetValue(input.TargetPlayerConnId, out RRConnection resolvedTargetPlayer)
                    ? resolvedTargetPlayer
                    : null;
                bool monsterAvailable = monster != null
                    && monster.IsAlive
                    && CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) != 0;
                bool playerAvailable = targetPlayer != null && IsPvpActionTargetAvailable(conn, targetPlayer, input.UseFlags);
                bool gnomeAvailable = input.GnomeEntityId != 0 && IsBlingGnomeSkillTargetAvailable(conn, input.UseFlags, input.TargetId);
                if (!monsterAvailable && !playerAvailable && !gnomeAvailable)
                {
                    RejectUnavailablePlayerUseTargetAction(conn, input, monster, simulationTick, "apply-target-unavailable");
                    continue;
                }
                try
                {
                    if (!HasCurrentPlayerBehaviorSlot(conn))
                    {
                        DiscardPendingUseTargetBehaviorAction(conn, "incoming-UseTarget-starts-current");
                        DiscardPendingPlayerUsePositionWeaponBehaviorAction(conn, "incoming-UseTarget-starts-current");
                        DiscardPendingPlayerSpellBehaviorAction(conn, 0, "incoming-UseTarget-starts-current");
                        DiscardPendingSelfCastBehaviorAction(conn);
                    }
                    bool hadActiveUseTarget = conn.HasActiveUseTarget;
                    int inputAdmissionTick = !hadActiveUseTarget
                        && input.UseFlags < 100
                        && input.PacketWriterTick > 0
                        && input.SimulationApplyTick == simulationTick
                        ? unchecked((int)input.PacketWriterTick)
                        : 0;
                    if (gnomeAvailable)
                    {
                        HandlePlayerUseBlingGnome(conn, input.ComponentId, input.ResponseId, input.ManipulatorId, input.UseFlags, input.TargetId,
                            unitFollowClientWaitThroughOverride: input.UnitFollowClientWaitThroughOrdinal);
                    }
                    else if (targetPlayer != null)
                    {
                        HandlePlayerAttackPlayer(
                            conn,
                            input.ComponentId,
                            input.ResponseId,
                            input.ManipulatorId,
                            input.UseFlags,
                            input.TargetId,
                            targetPlayer,
                            mirrorAdmissionTicks: 0,
                            unitFollowClientWaitThroughOverride: input.UnitFollowClientWaitThroughOrdinal,
                            inputAdmissionTick: inputAdmissionTick);
                    }
                    else
                    {
                        HandlePlayerAttackMonster(
                            conn,
                            input.ComponentId,
                            input.ResponseId,
                            input.ManipulatorId,
                            input.UseFlags,
                            input.TargetId,
                            monster,
                            mirrorAdmissionTicks: 0,
                            unitFollowClientWaitThroughOverride: input.UnitFollowClientWaitThroughOrdinal,
                            inputAdmissionTick: inputAdmissionTick);
                    }
                    bool startedCurrentUseTarget = !hadActiveUseTarget
                        && conn.HasActiveUseTarget
                        && conn.ActiveUseTargetId == input.TargetId
                        && conn.ActiveUseTargetPlayerConnId == input.TargetPlayerConnId
                        && conn.ActiveUseTargetComponentId == input.ComponentId
                        && conn.ActiveUseTargetFlags == input.UseFlags
                        && conn.ActiveUseTargetSessionId == input.ManipulatorId;
                    if (startedCurrentUseTarget)
                    {
                        ProcessPlayerUseTargetStart(conn, simulationTick);
                        if (input.UseFlags < 100 && conn.Avatar != null && conn.Avatar.Id > 0)
                            Combat.WeaponUseRuntime.Instance.ProcessPlayerUseTargetInput((uint)conn.Avatar.Id, simulationTick);
                    }
                    Debug.LogError($"[PLAYER-ACTION-APPLY] action=UseTarget conn={input.ConnId} component={input.ComponentId} manip={input.ManipulatorId} target={input.TargetId} receivedTick={input.ReceivedTick} packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} unitFollowClientWaitThroughOrdinal={input.UnitFollowClientWaitThroughOrdinal} sameProcessStart={startedCurrentUseTarget} phase=before-entities sourceFunction=ClientEntityManager::update@0x005D9E30->ClientEntityManager::processMessage@0x005DA460->Behavior::processUpdate@0x00515620");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[PLAYER-ACTION-APPLY-ERROR] action=UseTarget conn={input.ConnId} target={input.TargetId} monster={input.MonsterEntityId} packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} error={ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private bool HasCurrentPlayerBehaviorAction(RRConnection conn, uint playerEntityId)
        {
            return conn != null
                && (conn.HasActiveUseTarget
                    || conn.UsePositionActionMirrored
                    || HasPendingSelfCastBehaviorAction(conn)
                    || HasActiveSkillBehaviorSlot(conn)
                    || Combat.WeaponUseRuntime.Instance.HasActivePlayerWeaponAction(playerEntityId));
        }

        private bool ApplyPendingPlayerUsePositionWeaponActionInput(
            PendingPlayerUsePositionWeaponActionInput input,
            uint simulationTick)
        {
            if (!_connections.TryGetValue(input.ConnId, out RRConnection conn)
                || conn == null
                || !conn.IsConnected
                || !conn.IsSpawned)
            {
                Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=UsePositionWeapon conn={input.ConnId} session={input.SessionId} packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} reason=connection-unavailable");
                return false;
            }
            if (!IsPendingConnectionInstanceCurrent(conn, input.InstanceKey))
            {
                Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=UsePositionWeapon conn={input.ConnId} session={input.SessionId} instance='{input.InstanceKey}' packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} reason=stale-instance");
                return false;
            }
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null || conn.Avatar == null || conn.Avatar.Id <= 0)
                return false;
            bool behaviorSlotBusy = HasCurrentPlayerBehaviorSlot(conn)
                || Combat.WeaponUseRuntime.Instance.HasActivePlayerWeaponAction((uint)conn.Avatar.Id);
            if (behaviorSlotBusy)
            {
                ClearZoneSpawnInvulnerability(conn, $"ACTION-0x51 manipulator={input.ManipulatorId}");
                AdmitPlayerUsePositionBehaviorAction(
                    conn,
                    new PendingPlayerUsePositionBehaviorAction
                    {
                        ConnId = input.ConnId,
                        InstanceKey = input.InstanceKey,
                        ComponentId = input.ComponentId,
                        SessionId = input.SessionId,
                        ManipulatorId = input.ManipulatorId,
                        TargetFixedX = input.TargetFixedX,
                        TargetFixedY = input.TargetFixedY,
                        TargetFixedZ = input.TargetFixedZ,
                        ReceivedTick = input.ReceivedTick,
                        PacketWriterTick = input.PacketWriterTick,
                        WireMessageIndex = input.WireMessageIndex,
                        SourceSequence = input.AdmissionSequence,
                        FollowClientRecordsAtAdmission = input.FollowClientRecordsAtAdmission,
                        WeaponAction = true
                    },
                    simulationTick);
                return true;
            }
            bool used = Combat.WeaponUseRuntime.Instance.RegisterUsePosition(
                conn.ConnId.ToString(),
                state,
                conn,
                input.ComponentId,
                input.SessionId,
                input.ManipulatorId,
                input.TargetFixedX,
                input.TargetFixedY,
                input.TargetFixedZ,
                simulationTick);
            if (!used)
            {
                Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=UsePositionWeapon conn={input.ConnId} session={input.SessionId} manipulator={input.ManipulatorId} simulationApplyTick={simulationTick} reason=manipulator-use-failed sourceFunction=UsePosition::CheckInitUse@0x00547850->UsePosition::validateManipulator@0x00547760->MeleeWeapon::use@0x00591810");
                return false;
            }
            ClearZoneSpawnInvulnerability(conn, $"ACTION-0x51 manipulator={input.ManipulatorId}");
            AdmitPlayerUsePositionBehaviorAction(
                conn,
                new PendingPlayerUsePositionBehaviorAction
                {
                    ConnId = input.ConnId,
                    InstanceKey = input.InstanceKey,
                    ComponentId = input.ComponentId,
                    SessionId = input.SessionId,
                    ManipulatorId = input.ManipulatorId,
                    TargetFixedX = input.TargetFixedX,
                    TargetFixedY = input.TargetFixedY,
                    TargetFixedZ = input.TargetFixedZ,
                    ReceivedTick = input.ReceivedTick,
                    PacketWriterTick = input.PacketWriterTick,
                    WireMessageIndex = input.WireMessageIndex,
                    SourceSequence = input.AdmissionSequence,
                    FollowClientRecordsAtAdmission = input.FollowClientRecordsAtAdmission,
                    WeaponAction = true
                },
                simulationTick);
            return true;
        }

        private bool ApplyPendingPlayerUsePositionSpellActionInput(
            ref PendingSpell pending,
            uint simulationTick)
        {
            RRConnection conn = pending.Conn;
            if (conn == null
                || !conn.IsConnected
                || !conn.IsSpawned
                || pending.State == null
                || pending.Spell == null
                || !IsPendingConnectionInstanceCurrent(conn, pending.InstanceKey))
            {
                if (pending.ProjectileSubEntityRegistered)
                    CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey);
                Debug.LogError($"[SPELL-ACTION-APPLY-SKIP] conn={conn?.ConnId ?? 0} sequence={pending.Sequence} session={pending.ActionResponseSessionId} simulationApplyTick={simulationTick} reason=runtime-unavailable");
                return false;
            }
            if (!Combat.SpellDatabase.TryValidatePlayerUsePositionRuntime(pending.Spell, out string positionRuntimeReason))
            {
                if (pending.ProjectileSubEntityRegistered)
                    CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey);
                SkillEffectTracker.RecordPlayerGraph(conn.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, pending.Spell.SkillId, pending.Spell.TargetType, pending.Spell.OrderedEffects?.Count ?? 0, "rejected", positionRuntimeReason, simulationTick);
                SendUsePositionActionFailure(conn, pending.ComponentId, pending.ActionResponseId, positionRuntimeReason ?? "unsupported-player-position-runtime");
                Debug.LogError($"[SPELL-ACTION-APPLY-SKIP] conn={conn.ConnId} sequence={pending.Sequence} session={pending.ActionResponseSessionId} manipulator={pending.UseFlags} simulationApplyTick={simulationTick} reason={positionRuntimeReason ?? "unsupported-player-position-runtime"} resourceCommit=False sourceFunction=UsePosition::validateManipulator@0x00547760->ActiveSkill::validateUse@0x00538710");
                return false;
            }
            if (!IsSpellPositionWithinServerRangeFixed(
                    conn,
                    pending.Spell,
                    pending.AimFixedX,
                    pending.AimFixedY,
                    pending.ActionAimFixedZ,
                    out int distanceF32,
                    out int allowedRangeF32))
            {
                if (pending.ProjectileSubEntityRegistered)
                    CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerSpellProjectile, pending.Sequence, pending.InstanceKey);
                Debug.LogError($"[SPELL-ACTION-APPLY-SKIP] conn={conn.ConnId} sequence={pending.Sequence} session={pending.ActionResponseSessionId} manipulator={pending.UseFlags} simulationApplyTick={simulationTick} distanceF32={distanceF32} rangeF32={allowedRangeF32} reason=check-init-use-failed sourceFunction=UsePosition::CheckInitUse@0x00547850");
                return false;
            }
            int skillLevel = GetPlayerSkillLevel(conn, pending.Spell);
            if (!CommitActiveSkillUse(
                    conn,
                    pending.State,
                    pending.Spell,
                    pending.ComponentId,
                    pending.UseFlags,
                    skillLevel,
                    unchecked((int)simulationTick),
                    false))
            {
                Debug.LogError($"[SPELL-ACTION-APPLY-SKIP] conn={conn.ConnId} sequence={pending.Sequence} session={pending.ActionResponseSessionId} manipulator={pending.UseFlags} simulationApplyTick={simulationTick} reason=manipulator-use-failed sourceFunction=UsePosition::validateManipulator@0x00547760->ActiveSkill::use@0x00538DD0");
                return false;
            }
            pending.ActionAdmissionApplied = true;
            pending.SkillUseTick = simulationTick;
            pending.SkillUseCommitted = true;
            pending.StartsAfterSkillsChild = false;
            pending.FireTick = ResolveActiveSkillEffectTickBeforeSkillsChild(simulationTick, pending.Spell, pending.State);
            pending.LastUpdateTick = pending.FireTick;
            pending.DueTick = ResolvePendingSpellDueTick(pending.FireTick, pending.Spell, pending.ProjectileDelayTicks);
            ClearZoneSpawnInvulnerability(conn, $"ACTION-0x51 manipulator={pending.UseFlags}");
            if (conn.UsePositionActionMirrored
                && !conn.UsePositionWeaponAction
                && conn.UsePositionManipulatorId == pending.UseFlags
                && TryGetActiveSkillBusyUntilTick(conn, pending.ComponentId, pending.UseFlags, out uint currentBusyUntilTick))
            {
                conn.UsePositionActionBusyUntilTick = currentBusyUntilTick;
                ArmPeerUsePositionTermination(conn, conn.UsePositionActionSessionId, currentBusyUntilTick - 1);
            }
            AdmitPlayerUsePositionBehaviorAction(
                conn,
                new PendingPlayerUsePositionBehaviorAction
                {
                    ConnId = conn.ConnId,
                    InstanceKey = pending.InstanceKey,
                    ComponentId = pending.ComponentId,
                    SessionId = pending.ActionResponseSessionId,
                    ManipulatorId = pending.UseFlags,
                    TargetFixedX = pending.AimFixedX,
                    TargetFixedY = pending.AimFixedY,
                    TargetFixedZ = pending.ActionAimFixedZ,
                    ReceivedTick = pending.ActionReceiveTick,
                    PacketWriterTick = pending.ActionResponseWriterTick,
                    WireMessageIndex = pending.ActionResponseWireMessageIndex,
                    SourceSequence = pending.Sequence,
                    FollowClientRecordsAtAdmission = pending.FollowClientRecordsAtAdmission,
                    WeaponAction = false
                },
                simulationTick);
            Debug.LogError($"[SPELL-ACTION-APPLY] conn={conn.ConnId} sequence={pending.Sequence} session={pending.ActionResponseSessionId} manipulator={pending.UseFlags} packetWriterTick={pending.ActionResponseWriterTick} simulationApplyTick={simulationTick} messageIndex={pending.ActionResponseWireMessageIndex} admissionSequence={pending.ActionAdmissionSequence} fireTick={pending.FireTick} dueTick={pending.DueTick} sourceFunction=ClientEntityManager::update@0x005D9E30->UsePosition::CheckInitUse@0x00547850->ActiveSkill::use@0x00538DD0");
            return true;
        }

        private void ApplyPendingPlayerUsePositionActionInputs(uint simulationTick)
        {
            var admissions = new List<PendingPlayerUsePositionAdmission>();
            if (_pendingPlayerUsePositionWeaponActionInputs.Count > 0)
            {
                var weaponInputs = new List<PendingPlayerUsePositionWeaponActionInput>(_pendingPlayerUsePositionWeaponActionInputs);
                _pendingPlayerUsePositionWeaponActionInputs.Clear();
                for (int inputIndex = 0; inputIndex < weaponInputs.Count; inputIndex++)
                {
                    PendingPlayerUsePositionWeaponActionInput input = weaponInputs[inputIndex];
                    if (!IsPendingClientActionMessageSelected(input.ConnId, input.PacketWriterTick, input.WireMessageIndex)
                        || !input.PacketSent
                        || input.SimulationApplyTick == 0
                        || simulationTick < input.SimulationApplyTick)
                    {
                        _pendingPlayerUsePositionWeaponActionInputs.Add(input);
                        continue;
                    }
                    admissions.Add(new PendingPlayerUsePositionAdmission
                    {
                        Sequence = input.AdmissionSequence,
                        WeaponAction = true,
                        WeaponInput = input
                    });
                }
            }
            int pendingSpellCount = _pendingSpells.Count;
            for (int pendingIndex = 0; pendingIndex < pendingSpellCount; pendingIndex++)
            {
                if (!_pendingSpells.TryDequeue(out PendingSpell pending))
                    break;
                if (IsPendingClientActionMessageSelected(pending.Conn?.ConnId ?? 0, pending.ActionResponseWriterTick, pending.ActionResponseWireMessageIndex)
                    && !pending.ActionAdmissionApplied
                    && !pending.AwaitingActionResponsePacket
                    && pending.ActionAdmissionSequence > 0
                    && pending.ActionResponseSimulationApplyTick > 0
                    && simulationTick >= pending.ActionResponseSimulationApplyTick)
                {
                    admissions.Add(new PendingPlayerUsePositionAdmission
                    {
                        Sequence = pending.ActionAdmissionSequence,
                        WeaponAction = false,
                        SpellInput = pending
                    });
                    continue;
                }
                _pendingSpells.Enqueue(pending);
            }
            admissions.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            for (int admissionIndex = 0; admissionIndex < admissions.Count; admissionIndex++)
            {
                PendingPlayerUsePositionAdmission admission = admissions[admissionIndex];
                if (admission.WeaponAction)
                {
                    ApplyPendingPlayerUsePositionWeaponActionInput(admission.WeaponInput, simulationTick);
                    continue;
                }
                PendingSpell pending = admission.SpellInput;
                if (ApplyPendingPlayerUsePositionSpellActionInput(ref pending, simulationTick))
                    _pendingSpells.Enqueue(pending);
            }
        }

        private void ApplyPendingPlayerCancelActionInputs(uint simulationTick)
        {
            if (_pendingPlayerCancelActionInputs.Count == 0)
                return;

            var pending = new List<PendingPlayerCancelActionInput>(_pendingPlayerCancelActionInputs.Count);
            pending.AddRange(_pendingPlayerCancelActionInputs);
            _pendingPlayerCancelActionInputs.Clear();
            foreach (PendingPlayerCancelActionInput input in pending)
            {
                if (!IsPendingClientActionMessageSelected(input.ConnId, input.PacketWriterTick, input.MessageIndex))
                {
                    _pendingPlayerCancelActionInputs.Add(input);
                    continue;
                }
                if (!_connections.TryGetValue(input.ConnId, out RRConnection conn)
                    || conn == null
                    || !conn.IsConnected
                    || !conn.IsSpawned)
                {
                    Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=Cancel conn={input.ConnId} component={input.ComponentId} session={input.SessionId} receivedTick={input.ReceivedTick} packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} reason=connection-unavailable");
                    continue;
                }
                if (!IsPendingConnectionInstanceCurrent(conn, input.InstanceKey))
                {
                    Debug.LogError($"[PLAYER-ACTION-APPLY-SKIP] action=Cancel conn={input.ConnId} component={input.ComponentId} session={input.SessionId} instance='{input.InstanceKey}' receivedTick={input.ReceivedTick} packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} reason=stale-instance");
                    continue;
                }
                if (!input.PacketSent
                    || input.SimulationApplyTick == 0
                    || simulationTick < input.SimulationApplyTick)
                {
                    _pendingPlayerCancelActionInputs.Add(input);
                    continue;
                }

                bool hadPendingUseTarget = conn.HasPendingUseTargetAction;
                ushort pendingTargetId = conn.PendingUseTargetActionId;
                bool hadActiveUseTarget = conn.HasActiveUseTarget;
                bool hadActionMirroredMovement = ActionMirroredMoveToPointOwnsUnitMover(conn);
                bool hadActiveWeaponTarget = Combat.WeaponUseRuntime.Instance.GetActiveTarget(conn.ConnId.ToString()) != null;
                bool activeWeaponChild = conn.Avatar != null
                    && conn.Avatar.Id > 0
                    && Combat.WeaponUseRuntime.Instance.HasActivePlayerWeaponAction((uint)conn.Avatar.Id);
                int pendingUsePositionCleared = DiscardPendingPlayerUsePositionWeaponBehaviorAction(
                    conn,
                    "CANCEL-ACTION-process-update");
                DiscardPendingUseTargetBehaviorAction(conn, "CANCEL-ACTION-process-update");
                DiscardPendingSelfCastBehaviorAction(conn);
                int pendingSpellCleared = DiscardPendingPlayerSpellBehaviorAction(
                    conn,
                    0,
                    "CANCEL-ACTION-process-update");
                bool stoppedUsePosition = conn.UsePositionActionMirrored;
                byte stoppedUsePositionSession = conn.UsePositionActionSessionId;
                uint peerUsePositionTerminationTick = simulationTick;
                if (stoppedUsePosition
                    && conn.Avatar != null
                    && conn.Avatar.Id > 0
                    && Combat.WeaponUseRuntime.Instance.TryGetUsePositionNaturalTerminationTick(
                        (uint)conn.Avatar.Id,
                        out uint weaponTerminationTick)
                    && weaponTerminationTick > peerUsePositionTerminationTick)
                    peerUsePositionTerminationTick = weaponTerminationTick;
                if (stoppedUsePosition)
                {
                    StopPlayerUsePositionAction(
                        conn,
                        simulationTick,
                        true,
                        "CANCEL-ACTION-process-update",
                        peerCancelPacketOwnsTermination: true);
                    ArmPeerUsePositionTermination(conn, stoppedUsePositionSession, peerUsePositionTerminationTick);
                }
                if (activeWeaponChild && hadActiveUseTarget)
                {
                    Combat.WeaponUseRuntime.Instance.CancelConnectionUseTargetIntent(
                        conn.ConnId.ToString(),
                        "CANCEL-ACTION-process-update");
                }
                else if (hadActiveUseTarget || hadActiveWeaponTarget || hadActionMirroredMovement)
                {
                    ClearUseTargetAndReleaseControl(
                        conn,
                        "CANCEL-ACTION-process-update",
                        input.ComponentId,
                        sendClientControlReset: false,
                        preserveActiveWeaponChild: true);
                }
                Debug.LogError($"[PLAYER-ACTION-APPLY] action=Cancel conn={input.ConnId} component={input.ComponentId} session={input.SessionId} receivedTick={input.ReceivedTick} packetWriterTick={input.PacketWriterTick} simulationApplyTick={simulationTick} messageIndex={input.MessageIndex} pendingUseTargetCleared={hadPendingUseTarget} pendingUsePositionCleared={pendingUsePositionCleared} pendingSpellCleared={pendingSpellCleared} pendingTarget={pendingTargetId} activeUseTarget={hadActiveUseTarget} activeUsePositionStopped={stoppedUsePosition} actionMirroredMovement={hadActionMirroredMovement} activeWeaponTarget={hadActiveWeaponTarget} activeWeaponChild={activeWeaponChild} sourceFunction=ClientEntityManager::update@0x005D9E30->ClientEntityManager::processMessage@0x005DA460->UnitBehavior::processUpdate@0x00520020->Behavior::processUpdate@0x00515620 opcode=0x03 slot=Behavior+0x78");
            }
        }

    }
}
