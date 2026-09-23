using System;
using System.Linq;
using DungeonRunners.Core;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Utilities;
using DungeonRunners.Networking.EntitySynchInfo;
using Org.BouncyCastle.Bcpg.Sig;

namespace DungeonRunners.Combat
{
    public static class CombatPackets
    {
        private const byte UnitMoverApplyMovementAnimationsFlag = 0x40;
        private const byte UnitMoverApplyMovementToEntityObjectFlag = 0x20;
        private const byte MonsterFreshUnitMoverFlags = 0xC5;
        private const byte UnitMoverIdleState = 0x01;

        private static void WriteEntitySynchInfo(LEWriter writer, string packetName, string owner, uint ownerEntityId, uint componentId, byte subtype, byte entitySynchInfoFlags, uint entitySynchInfoHPWire, bool requireHP)
        {
            if (requireHP)
                entitySynchInfoFlags = 0x02;

            writer.WriteByte(entitySynchInfoFlags);
            if ((entitySynchInfoFlags & 0x02) != 0)
                writer.WriteUInt32(entitySynchInfoHPWire);

            string hpText = (entitySynchInfoFlags & 0x02) != 0 ? entitySynchInfoHPWire.ToString() : "none";
            Debug.LogError($"[ENTITY-SYNCH-INFO] packet={packetName} owner={owner} entity={ownerEntityId} component={componentId} sub=0x{subtype:X2} flags=0x{entitySynchInfoFlags:X2} hp={hpText}");
        }

        private static void RejectRawAliveHPSuffix(string packetName, byte entitySynchInfoFlags)
        {
            if ((entitySynchInfoFlags & 0x02) != 0)
                throw new InvalidOperationException($"{packetName} raw HP suffix is quarantined; use a resolved EntitySynchInfo payload");
        }

        private static void WriteResolvedEntitySynchInfo(LEWriter writer, string packetName, string owner, ResolvedEntitySynchInfo entitySynchInfo, bool requireHP)
        {
            if (requireHP && !entitySynchInfo.HasHP)
                throw new InvalidOperationException($"{packetName} requires a resolved HP EntitySynchInfo payload");

            entitySynchInfo.Payload.Write(writer);
            string hpText = entitySynchInfo.HasHP ? entitySynchInfo.HPWire.ToString() : "none";
            Debug.LogError($"[ENTITY-SYNCH-INFO] packet={packetName} owner={owner} entity={entitySynchInfo.OwnerEntityId} component={entitySynchInfo.ComponentId} sub=0x{entitySynchInfo.Subtype:X2} flags=0x{entitySynchInfo.Flags:X2} hp={hpText} cutoffTick={entitySynchInfo.ValidationCutoffTick} reason={entitySynchInfo.Reason} provenance={entitySynchInfo.Provenance}");
        }

        private static void WriteUnitReadInit(
            LEWriter writer,
            byte level,
            uint currentHPWire,
            uint maxHPWire,
            uint currentManaWire,
            uint maxManaWire,
            ushort healthRegenCooldownTicks,
            ushort manaRegenCooldownTicks,
            uint encounterObjectId,
            byte state31B,
            ushort ownerPlayerEntityId = 0,
            ushort healthFactorF32 = 0x100,
            ushort damageFactorF32 = 0x100,
            ushort lootFactorF32 = 0x100,
            byte lootPlayerLimit = 1)
        {
            byte unitFlags = 0;
            unitFlags |= 0x02;
            if (ownerPlayerEntityId != 0)
                unitFlags |= 0x01;
            if (currentManaWire != maxManaWire)
                unitFlags |= 0x04;
            if (encounterObjectId != 0)
                unitFlags |= 0x20;
            if (healthFactorF32 != 0x100 || damageFactorF32 != 0x100 || lootFactorF32 != 0x100)
                unitFlags |= 0x40;
            if (state31B != 0)
                unitFlags |= 0x80;
            writer.WriteByte(unitFlags);
            writer.WriteByte(level);
            writer.WriteUInt16(healthRegenCooldownTicks);
            writer.WriteUInt16(manaRegenCooldownTicks);
            if ((unitFlags & 0x01) != 0)
                writer.WriteUInt16(ownerPlayerEntityId);
            if ((unitFlags & 0x02) != 0)
                writer.WriteUInt32(currentHPWire);
            if ((unitFlags & 0x04) != 0)
                writer.WriteUInt32(currentManaWire);
            if ((unitFlags & 0x20) != 0)
                writer.WriteUInt16((ushort)encounterObjectId);
            if ((unitFlags & 0x40) != 0)
            {
                writer.WriteUInt16(healthFactorF32);
                writer.WriteUInt16(damageFactorF32);
                writer.WriteUInt16(lootFactorF32);
                writer.WriteByte(lootPlayerLimit);
            }
            if ((unitFlags & 0x80) != 0)
                writer.WriteByte(state31B);
        }

        private static void WriteWorldEntityReadInit(
            LEWriter writer,
            uint worldEntityFlags,
            int posX,
            int posY,
            int posZ,
            int heading,
            byte animationState,
            ushort animationId,
            uint animationPlayTime,
            uint animationSpeed)
        {
            writer.WriteUInt32(worldEntityFlags);
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            writer.WriteInt32(heading);
            byte animationFlags = 0;
            if (animationId != 0)
                animationFlags |= 0x01;
            if (animationState != 0)
                animationFlags |= 0x02;
            if (animationPlayTime != 0)
                animationFlags |= 0x04;
            if (animationSpeed != 0x100)
                animationFlags |= 0x08;
            writer.WriteByte(animationFlags);
            if ((animationFlags & 0x01) != 0)
                writer.WriteUInt16(animationId);
            if ((animationFlags & 0x02) != 0)
                writer.WriteByte(animationState);
            if ((animationFlags & 0x04) != 0)
                writer.WriteUInt32(animationPlayTime);
            if ((animationFlags & 0x08) != 0)
                writer.WriteUInt32(animationSpeed);
        }

        private static void WriteStockUnitReadInit(
            LEWriter writer,
            int posX,
            int posY,
            int posZ,
            int heading,
            uint worldEntityFlags,
            byte worldEntityAnimationState,
            ushort worldEntityAnimationId,
            uint worldEntityAnimationPlayTime,
            uint worldEntityAnimationSpeed,
            byte level,
            uint currentHPWire,
            uint maxHPWire,
            uint currentManaWire,
            uint maxManaWire,
            ushort healthRegenCooldownTicks,
            ushort manaRegenCooldownTicks,
            uint encounterObjectId,
            byte state31B,
            byte state,
            ushort stateTicksRemaining,
            ushort respawnRateTicks,
            byte runtimePercent,
            ushort lifespanTicksRemaining,
            uint runtimeFlags,
            byte runtimeState,
            int savedPosX,
            int savedPosY,
            int savedPosZ,
            ushort ownerPlayerEntityId = 0,
            ushort healthFactorF32 = 0x100,
            ushort damageFactorF32 = 0x100,
            ushort lootFactorF32 = 0x100,
            byte lootPlayerLimit = 1)
        {
            WriteWorldEntityReadInit(
                writer,
                worldEntityFlags,
                posX,
                posY,
                posZ,
                heading,
                worldEntityAnimationState,
                worldEntityAnimationId,
                worldEntityAnimationPlayTime,
                worldEntityAnimationSpeed);
            WriteUnitReadInit(
                writer,
                level,
                currentHPWire,
                maxHPWire,
                currentManaWire,
                maxManaWire,
                healthRegenCooldownTicks,
                manaRegenCooldownTicks,
                encounterObjectId,
                state31B,
                ownerPlayerEntityId,
                healthFactorF32,
                damageFactorF32,
                lootFactorF32,
                lootPlayerLimit);
            writer.WriteByte(state);
            writer.WriteUInt16(stateTicksRemaining);
            writer.WriteUInt16(respawnRateTicks);
            writer.WriteByte(runtimePercent);
            writer.WriteUInt16(lifespanTicksRemaining);
            writer.WriteUInt32(runtimeFlags);
            writer.WriteByte(runtimeState);
            writer.WriteInt32(savedPosX);
            writer.WriteInt32(savedPosY);
            writer.WriteInt32(savedPosZ);
        }

        private static void WriteBehaviorReadInitNoActions(LEWriter writer, byte endByte)
        {
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(endByte);
        }

        private static void WriteMonsterUnitMoverReadInit(LEWriter writer)
        {
            writer.WriteByte(MonsterFreshUnitMoverFlags);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(UnitMoverIdleState);
        }

        private static void WriteUnitBehaviorReadInitNoClientControl(LEWriter writer)
        {
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
        }

        private static void WriteStateMachineReadMessageHeader(
            LEWriter writer,
            byte flags,
            ushort field10 = 0,
            ushort field12 = 0,
            ushort field14 = 0)
        {
            writer.WriteByte(flags);
            if ((flags & 0x02) != 0)
                writer.WriteUInt16(field10);
            if ((flags & 0x04) != 0)
                writer.WriteUInt16(field12);
            if ((flags & 0x08) != 0)
                writer.WriteUInt16(field14);
        }

        private static void WriteMonsterBehavior2ReadInit(
            LEWriter writer,
            byte flags,
            int defaultPositionFixedX,
            int defaultPositionFixedY,
            ushort primaryTargetId = 0,
            ushort secondaryTargetId = 0,
            ushort targetFilter = 0)
        {
            writer.WriteByte(flags);
            writer.WriteInt32(defaultPositionFixedX);
            writer.WriteInt32(defaultPositionFixedY);
            if ((flags & 0x04) != 0)
                writer.WriteUInt16(primaryTargetId);
            if ((flags & 0x08) != 0)
                writer.WriteUInt16(secondaryTargetId);
            if ((flags & 0x10) != 0)
                writer.WriteUInt16(targetFilter);
        }

        internal static void WriteStateMachineSnapshot(
            LEWriter writer,
            ushort previousState,
            ushort currentState,
            ushort nextState,
            Behavior.StateMachineSnapshot snapshot,
            bool transitionPending = false)
        {
            byte flags = 0;
            if (transitionPending)
                flags |= 0x01;
            if (previousState != 0xFFFF)
                flags |= 0x02;
            if (currentState != 0xFFFF)
                flags |= 0x04;
            if (nextState != 0xFFFF)
                flags |= 0x08;
            if (snapshot != null && snapshot.Messages.Count != 0)
                flags |= 0x10;
            if (snapshot != null && snapshot.Clock != 0)
                flags |= 0x20;
            writer.WriteByte(flags);
            if ((flags & 0x02) != 0)
                writer.WriteUInt16(previousState);
            if ((flags & 0x04) != 0)
                writer.WriteUInt16(currentState);
            if ((flags & 0x08) != 0)
                writer.WriteUInt16(nextState);
            if ((flags & 0x20) != 0)
                writer.WriteUInt32(unchecked((uint)snapshot.Clock));
            if ((flags & 0x10) == 0)
                return;
            int messageCount = Math.Min(ushort.MaxValue, snapshot.Messages.Count);
            writer.WriteUInt16((ushort)messageCount);
            for (int messageIndex = 0; messageIndex < messageCount; messageIndex++)
            {
                Behavior.StateMachineMessageSnapshot message = snapshot.Messages[messageIndex];
                byte messageFlags = 0;
                if (message.Param != 0xFFFF)
                    messageFlags |= 0x01;
                if (message.Interval != 0)
                    messageFlags |= 0x04;
                if (message.Consumed)
                    messageFlags |= 0x08;
                writer.WriteByte(messageFlags);
                writer.WriteUInt16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, message.Id)));
                writer.WriteUInt32(unchecked((uint)message.DueTick));
                if ((messageFlags & 0x01) != 0)
                    writer.WriteUInt16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, message.Param)));
                if ((messageFlags & 0x04) != 0)
                    writer.WriteUInt16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, message.Interval)));
            }
        }

        private static void WriteMonsterLiveBehaviorReadInit(LEWriter writer, Monster monster, MonsterLiveSnapshot snapshot)
        {
            writer.WriteByte(0x00);
            if (snapshot.ActionKind == MonsterLiveActionKind.Spawn)
            {
                writer.WriteByte(0x04);
                writer.WriteByte(0x00);
                writer.WriteInt32(snapshot.FixedX);
                writer.WriteInt32(snapshot.FixedY);
                writer.WriteInt32(snapshot.FixedZ);
                writer.WriteUInt16(0);
                writer.WriteByte(snapshot.SpawnPhase);
                writer.WriteByte(snapshot.SpawnCounter);
            }
            else if (snapshot.ActionKind == MonsterLiveActionKind.Wander)
            {
                writer.WriteByte(0x15);
                writer.WriteByte(0x00);
                writer.WriteUInt16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, GCDatabase.RoundFixed32ToInt(monster.WanderRangeF32))));
                writer.WriteByte(snapshot.Wander.State);
                writer.WriteUInt16(snapshot.Wander.Timer);
                writer.WriteByte(snapshot.Wander.CanWander ? (byte)1 : (byte)0);
            }
            else if (snapshot.ActionKind == MonsterLiveActionKind.Follow)
            {
                writer.WriteByte(0x16);
                writer.WriteByte(0x00);
                writer.WriteUInt16(snapshot.ActionTargetEntityId);
                WriteStateMachineSnapshot(
                    writer,
                    snapshot.FollowPreviousState,
                    snapshot.FollowCurrentState,
                    snapshot.FollowNextState,
                    snapshot.FollowStateMachine);
                writer.WriteUInt16(100);
                writer.WriteUInt16(100);
            }
            else if (snapshot.ActionKind == MonsterLiveActionKind.MoveTo)
            {
                WriteGCType(writer, "MoveTo", true);
                writer.WriteByte(0x00);
                writer.WriteInt32(snapshot.TargetFixedX);
                writer.WriteInt32(snapshot.TargetFixedY);
                writer.WriteByte(monster.ReturnMoveActive ? (byte)1 : (byte)100);
            }
            else
            {
                writer.WriteByte(0x00);
            }
            if (snapshot.PendingActionKind == MonsterLiveActionKind.Wander)
            {
                writer.WriteByte(0x15);
                writer.WriteByte(0x00);
                writer.WriteUInt16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, GCDatabase.RoundFixed32ToInt(monster.WanderRangeF32))));
            }
            else if (snapshot.PendingActionKind == MonsterLiveActionKind.MoveTo)
            {
                WriteGCType(writer, "MoveTo", true);
                writer.WriteByte(0x00);
                writer.WriteInt32(monster.SpawnPosFixedX);
                writer.WriteInt32(monster.SpawnPosFixedY);
            }
            else
            {
                writer.WriteByte(0x00);
            }
            writer.WriteByte(snapshot.ActionGeneration);

            writer.WriteByte(snapshot.Moving
                ? (byte)(UnitMoverApplyMovementAnimationsFlag | UnitMoverApplyMovementToEntityObjectFlag)
                : UnitMoverApplyMovementAnimationsFlag);
            writer.WriteInt32(snapshot.HeadingFixed);
            writer.WriteInt32(snapshot.HeadingFixed);
            writer.WriteByte(snapshot.Moving ? (byte)2 : (byte)1);
            if (snapshot.Moving)
            {
                writer.WriteUInt16(1);
                writer.WriteInt32(snapshot.TargetFixedX);
                writer.WriteInt32(snapshot.TargetFixedY);
            }
            WriteUnitBehaviorReadInitNoClientControl(writer);
            WriteStateMachineSnapshot(
                writer,
                snapshot.TopPreviousState,
                snapshot.TopCurrentState,
                snapshot.TopNextState,
                snapshot.TopStateMachine);

            byte flags = 0;
            if (monster.ReturnRemovalPending)
                flags |= 0x02;
            if (snapshot.PrimaryTargetEntityId != 0)
                flags |= 0x04;
            if (snapshot.SecondaryTargetEntityId != 0)
                flags |= 0x08;
            if (snapshot.CombatTimer != 0)
                flags |= 0x10;
            WriteMonsterBehavior2ReadInit(
                writer,
                flags,
                monster.SpawnPosFixedX,
                monster.SpawnPosFixedY,
                snapshot.PrimaryTargetEntityId,
                snapshot.SecondaryTargetEntityId,
                snapshot.CombatTimer);
        }

        public static byte[] BuildMonsterLiveSnapshotPacket(
            Monster monster,
            uint behaviorId,
            uint skillsId,
            uint manipulatorsId,
            uint modifiersId,
            MonsterLiveSnapshot snapshot,
            ResolvedEntitySynchInfo entitySynchInfo,
            ushort ownerPlayerEntityId = 0)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            return BuildMonsterPacket(monster, behaviorId, skillsId, manipulatorsId, modifiersId, 0, ownerPlayerEntityId, snapshot, entitySynchInfo);
        }

        public static byte[] BuildMonsterSpawnPacket(
     Monster monster,
     uint behaviorId,
     uint skillsId,
     uint manipulatorsId,
     uint modifiersId,
     ushort targetEntityId,
      ushort playerEntityId,
      ResolvedEntitySynchInfo entitySynchInfo)
        {
            return BuildMonsterPacket(monster, behaviorId, skillsId, manipulatorsId, modifiersId, targetEntityId, playerEntityId, null, entitySynchInfo);
        }

        private static byte[] BuildMonsterPacket(
     Monster monster,
     uint behaviorId,
     uint skillsId,
     uint manipulatorsId,
     uint modifiersId,
     ushort targetEntityId,
     ushort playerEntityId,
     MonsterLiveSnapshot liveSnapshot,
     ResolvedEntitySynchInfo entitySynchInfo)
        {
            if (monster.Summoner != null && playerEntityId == 0)
                throw new InvalidOperationException("A combat summon requires its viewer-specific Player parent");
            var writer = new LEWriter();
            int posX = liveSnapshot?.FixedX ?? monster.PosFixedX;
            int posY = liveSnapshot?.FixedY ?? monster.PosFixedY;
            int posZ = liveSnapshot?.FixedZ ?? monster.PosFixedZ;
            int heading = liveSnapshot?.HeadingFixed ?? monster.HeadingFixed;
            byte lvl = monster.Level;
            if (lvl == 0) lvl = 1;
            if (monster.IsAlive && !entitySynchInfo.HasHP)
                throw new InvalidOperationException($"MON-SPAWN requires resolved HP for alive monster {monster.Name}#{monster.EntityId}");
            uint resolvedHPWire = entitySynchInfo.HPWire;
            if (resolvedHPWire > monster.MaxHPWire) resolvedHPWire = monster.MaxHPWire;
            if (entitySynchInfo.HasHP && resolvedHPWire != entitySynchInfo.HPWire)
            {
                entitySynchInfo = new ResolvedEntitySynchInfo(
                    EntitySynchInfoPayload.FromHP(resolvedHPWire),
                    entitySynchInfo.OwnerEntityId,
                    entitySynchInfo.ComponentId,
                    entitySynchInfo.Subtype,
                    entitySynchInfo.Reason,
                    entitySynchInfo.Provenance,
                    entitySynchInfo.ValidationCutoffTick,
                    entitySynchInfo.RuntimeInstanceKey,
                    entitySynchInfo.SchedulerTick,
                    entitySynchInfo.SubEntityPhase,
                    entitySynchInfo.RngPos,
                    entitySynchInfo.HpMutationSource);
            }
            uint resolvedManaWire = monster.MaxManaWire > 0 ? Math.Min(monster.CurrentManaWire, monster.MaxManaWire) : monster.CurrentManaWire;

            writer.WriteByte(0x07);

            writer.WriteByte(0x08);
            writer.WriteUInt16((ushort)monster.EntityId);
            string entityGCType = MapToBaseGCType(monster.SpawnGCType ?? monster.GCType);
            Debug.LogError($"[SPAWN-PKT] Monster {monster.Name} entityGCType='{entityGCType}' spawnGCType='{monster.SpawnGCType}' baseGCType='{monster.GCType}' fixed=({posX},{posY},{posZ}) headingFixed={heading} level={lvl} unitInitHPWire={resolvedHPWire}/{monster.MaxHPWire} suffixHPWire={entitySynchInfo.HPWire}/{monster.MaxHPWire} manaWire={resolvedManaWire}/{monster.MaxManaWire} aggroRangeF32={monster.AggroRangeF32} attackRangeF32={monster.AttackRangeF32}");
            WriteGCType(writer, entityGCType, true);
            CombatRuntime.Instance.GetMonsterRegenCooldownTicks(monster.EntityId, out ushort healthRegenCooldownTicks, out ushort manaRegenCooldownTicks);
            bool liveInit = liveSnapshot != null;
            uint stockUnitWorldEntityFlags = liveInit ? monster.UnitFlags : monster.UnitFlags & ~0x400u;
            byte stockUnitState = liveInit ? monster.StockUnitState : (byte)0x00;
            ushort stockUnitStateTicksRemaining = liveInit ? monster.StockUnitStateTicksRemaining : (ushort)0;
            ushort stockUnitLifespanTicksRemaining = liveInit ? monster.StockUnitLifespanTicksRemaining : (ushort)0;
            byte worldEntityAnimationState = liveSnapshot?.WorldEntityAnimationState ?? 0;
            ushort worldEntityAnimationId = liveSnapshot?.WorldEntityAnimationId ?? 0;
            uint worldEntityAnimationPlayTime = liveSnapshot?.WorldEntityAnimationPlayTime ?? 0;
            uint worldEntityAnimationSpeed = liveSnapshot?.WorldEntityAnimationSpeed ?? 0x100;
            WriteStockUnitReadInit(
                writer,
                posX,
                posY,
                posZ,
                heading,
                stockUnitWorldEntityFlags,
                worldEntityAnimationState,
                worldEntityAnimationId,
                worldEntityAnimationPlayTime,
                worldEntityAnimationSpeed,
                lvl,
                resolvedHPWire,
                monster.MaxHPWire,
                resolvedManaWire,
                monster.MaxManaWire,
                healthRegenCooldownTicks,
                manaRegenCooldownTicks,
                monster.EncounterObjectEntityId,
                monster.UnitState31B,
                stockUnitState,
                stockUnitStateTicksRemaining,
                monster.RespawnRateTicks,
                monster.StockUnitRuntimePercent,
                stockUnitLifespanTicksRemaining,
                monster.StockUnitRuntimeFlags,
                monster.StockUnitRuntimeState,
                monster.SpawnPosFixedX,
                monster.SpawnPosFixedY,
                monster.SpawnPosFixedZ,
                monster.Summoner != null ? playerEntityId : (ushort)0,
                monster.HealthFactorF32,
                monster.DamageFactorF32,
                monster.LootFactorF32,
                monster.LootPlayerLimit);

            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)behaviorId);
            string behaviorType = monster.SpawnBehaviourType ?? monster.BehaviourType;
            Debug.LogError($"[SPAWN-PKT] Monster {monster.Name} behaviorType='{behaviorType}' (SpawnOverride='{monster.SpawnBehaviourType}' Default='{monster.BehaviourType}')");
            WriteGCType(writer, behaviorType, false);
            writer.WriteByte(0x01);

            if (liveSnapshot != null)
            {
                WriteMonsterLiveBehaviorReadInit(writer, monster, liveSnapshot);
            }
            else
            {
                WriteBehaviorReadInitNoActions(writer, 0x00);
                WriteMonsterUnitMoverReadInit(writer);
                WriteUnitBehaviorReadInitNoClientControl(writer);
                WriteStateMachineReadMessageHeader(writer, 0x0F, 0xFFFF, 0xFFFF, 0xFFFF);
                WriteMonsterBehavior2ReadInit(writer, 0x00, monster.SpawnPosFixedX, monster.SpawnPosFixedY);
            }

            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)skillsId);
            WriteGCType(writer, "skills", false);
            writer.WriteByte(0x01);
            writer.WriteByte(0xFF);
            writer.WriteByte(0xFF);
            writer.WriteByte(0xFF);
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)manipulatorsId);
            WriteGCType(writer, "manipulators", false);
            writer.WriteByte(0x01);

            var manipulatorsToSend = new System.Collections.Generic.List<ManipulatorEntry>();

            if (monster.Manipulators != null)
            {
                for (int skillIndex = 1; monster.Manipulators.TryGetValue("skill" + skillIndex, out var skill); skillIndex++)
                {
                    uint id = GetManipulatorUInt(skill, "ID");
                    manipulatorsToSend.Add(new ManipulatorEntry(skill.gcType, id, ManipulatorType.ActiveSkill));
                }

                if (monster.Manipulators.TryGetValue("primaryweapon", out var weapon))
                {
                    uint id = GetManipulatorUInt(weapon, "ID");
                    var weaponType = IsRangedManipulator(weapon) ? ManipulatorType.RangedWeapon : ManipulatorType.MeleeWeapon;
                    manipulatorsToSend.Add(new ManipulatorEntry(weapon.gcType, id, weaponType));
                }
            }

            writer.WriteByte((byte)manipulatorsToSend.Count);
            Debug.LogError($"[SPAWN-PACKET] Writing {manipulatorsToSend.Count} manipulators");

            foreach (var manip in manipulatorsToSend)
            {
                WriteGCType(writer, manip.GCType, true);
                WriteMonsterManipulatorDataInit(writer, manip);
                Debug.LogError($"[SPAWN-PACKET]   Manip: {manip.GCType} type={manip.Type} authoredId={manip.Id} clientDataInit=0x{GetMonsterManipulatorDataInitShape(manip)}");
            }

            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)monster.EntityId);
            writer.WriteUInt16((ushort)modifiersId);
            WriteGCType(writer, "modifiers", false);
            writer.WriteByte(0x01);
            WriteMonsterAttributeModifiersReadInit(writer, monster, liveSnapshot != null);

            if (liveSnapshot == null)
            {
                writer.WriteByte(0x35);
                writer.WriteUInt16((ushort)behaviorId);
                writer.WriteByte(0x04);
                writer.WriteByte(0x04);
                writer.WriteByte(0x00);
                writer.WriteInt32(posX);
                writer.WriteInt32(posY);
                writer.WriteInt32(posZ);
                writer.WriteUInt16(0);
                WriteResolvedEntitySynchInfo(writer, "MON-SPAWN-ACTION", "Monster", entitySynchInfo, true);
            }

            writer.WriteByte(0x06);


            byte[] packet = writer.ToArray();
            Debug.LogError($"[MONSTER-SPAWN-HEX] mode={(liveSnapshot == null ? "birth" : "live")} size={packet.Length} hex={BitConverter.ToString(packet)}");
            return packet;
        }

        private static void WriteMonsterAttributeModifiersReadInit(LEWriter writer, Monster monster, bool live)
        {
            writer.WriteInt32(live ? monster.ModifierLocalIdGenerator : 0);
            writer.WriteInt32(0);
            var modifiers = live
                ? monster.AttributeModifiers?.Where(modifier => modifier != null && modifier.LocalId != 0 && !string.IsNullOrWhiteSpace(modifier.ModifierPath)).ToList()
                : null;
            int count = modifiers?.Count ?? 0;
            if (count >= 255)
            {
                writer.WriteByte(255);
                writer.WriteUInt16(checked((ushort)count));
            }
            else
                writer.WriteByte((byte)count);
            if (modifiers == null)
                return;
            foreach (MonsterAttributeModifier modifier in modifiers)
            {
                WriteGCType(writer, modifier.ModifierPath, false);
                writer.WriteUInt32(modifier.LocalId);
                writer.WriteByte(modifier.SkillLevel);
                writer.WriteUInt32(modifier.PowerLevel);
                writer.WriteUInt32(modifier.Permanent ? 0u : (uint)Math.Max(0, modifier.RemainingTicks));
                writer.WriteByte(modifier.SourceEntityId == monster.EntityId ? (byte)1 : (byte)0);
            }
            foreach (MonsterAttributeModifier modifier in modifiers)
            {
                bool sourceIsSelf = modifier.SourceEntityId == monster.EntityId;
                writer.WriteByte(sourceIsSelf ? (byte)1 : (byte)0);
                if (!sourceIsSelf)
                    writer.WriteUInt16(unchecked((ushort)modifier.SourceEntityId));
            }
        }

        public static byte[] BuildEncounterObjectSpawnPacket(uint encounterObjectId, int posFixedX, int posFixedY, int posFixedZ, int headingFixed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)encounterObjectId);
            WriteGCType(writer, "EncounterObject", true);
            writer.WriteByte(0x02);
            writer.WriteUInt16((ushort)encounterObjectId);
            writer.WriteUInt32(0x06);
            writer.WriteInt32(posFixedX);
            writer.WriteInt32(posFixedY);
            writer.WriteInt32(posFixedZ);
            writer.WriteInt32(headingFixed);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        public static byte[] BuildIntervalPacket(uint updateNumber, uint entityUpdateNum,
            ushort nodeCountA, ushort nodeCountB)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x0D);
            writer.WriteUInt32(updateNumber);
            writer.WriteUInt32(updateNumber);
            writer.WriteUInt32(0);
            writer.WriteUInt32(entityUpdateNum);
            writer.WriteUInt16(nodeCountA);
            writer.WriteUInt16(nodeCountB);
            return writer.ToArray();
        }
        public static byte[] BuildMonsterDespawnPacket(uint entityId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x05);
            writer.WriteUInt16((ushort)entityId);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }



        private static void WriteGCType(LEWriter writer, string gcType, bool preserveCase)
        {
            string safeTypeName = preserveCase ? gcType : gcType.ToLower();
            writer.WriteByte(0xFF);
            writer.WriteCString(safeTypeName);
        }

        private static uint GetManipulatorUInt(ManipulatorData manipulator, string property)
        {
            if (manipulator?.properties == null) return 0;
            if (!manipulator.properties.TryGetValue(property, out string value)) return 0;
            if (uint.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint result))
                return result;
            return 0;
        }

        private static bool IsRangedManipulator(ManipulatorData manipulator)
        {
            if (manipulator?.properties == null) return false;
            return manipulator.properties.ContainsKey("ShotType")
                || manipulator.properties.ContainsKey("UseProjectile")
                || manipulator.properties.ContainsKey("ProjectileSpeed")
                || manipulator.properties.ContainsKey("ProjectileSize");
        }

        private static void WriteMonsterManipulatorDataInit(LEWriter writer, ManipulatorEntry manip)
        {
            if (manip.Type == ManipulatorType.ActiveSkill)
            {
                writer.WriteUInt32(manip.Id);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                return;
            }

            WriteMonsterItemReadDataNoModifiers(writer, manip.Id);
            writer.WriteUInt16(0x0000);

            if (manip.Type == ManipulatorType.MeleeWeapon)
                writer.WriteByte(0x00);

            writer.WriteUInt16(0x0000);
        }

        private static void WriteMonsterItemReadDataNoModifiers(LEWriter writer, uint itemId)
        {
            writer.WriteUInt32(itemId);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
        }

        private static string GetMonsterManipulatorDataInitShape(ManipulatorEntry manip)
        {
            switch (manip.Type)
            {
                case ManipulatorType.ActiveSkill:
                    return UInt32LEHex(manip.Id) + "0000";
                case ManipulatorType.MeleeWeapon:
                    return UInt32LEHex(manip.Id) + "0000000000000000000000";
                case ManipulatorType.RangedWeapon:
                    return UInt32LEHex(manip.Id) + "00000000000000000000";
                default:
                    return "";
            }
        }

        private static string UInt32LEHex(uint value)
        {
            return $"{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}{(value >> 24) & 0xFF:X2}";
        }

        private static string MapToBaseGCType(string gcType)
        {
            switch (gcType.ToLower())
            {
                case "creatures.forestcreatures.warg.basic.pup":
                    return "world.dungeon00.mob.melee01.rank1";
                case "creatures.forestcreatures.warg.basic.grunt":
                    return "world.dungeon00.mob.melee02.rank1";
                case "creatures.whiskers.broodling.basic.grunt":
                    return "world.dungeon00.mob.melee03.rank1";
                case "creatures.whiskers.blademaster.basic.grunt":
                    return "world.dungeon00.mob.melee04.rank1";
                case "creatures.whiskers.broodling.basic.champion":
                    return "world.dungeon00.mob.boss";
                case "world.objects.barrel.breakable":
                case "world.objects.barrel.breakable.02":
                case "world.objects.barrel.breakable.03":
                    return "world.dungeon00.mob.CreatureBarrel";
                default:
                    return gcType;
            }
        }

        public static byte[] BuildMonsterFollowPacket(uint monsterBehaviorId, ushort targetPlayerId, ResolvedEntitySynchInfo entitySynchInfo)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)monsterBehaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x16);
            writer.WriteByte(0x00);
            writer.WriteUInt16(targetPlayerId);
            WriteResolvedEntitySynchInfo(writer, "MON-FOLLOW", "Monster", entitySynchInfo, true);
            return writer.ToArray();
        }

        public static byte[] BuildMonsterAttackPacket(uint monsterEntityId, uint monsterBehaviorId, ushort targetPlayerId, byte useFlags, ResolvedEntitySynchInfo entitySynchInfo, bool useTargetAction, bool beginEndStream = true)
        {
            var writer = new LEWriter();
            if (beginEndStream)
                writer.WriteByte(0x07);

            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)monsterBehaviorId);
            writer.WriteByte(0x04);
            if (useTargetAction)
            {
                writer.WriteByte(0x50);
                writer.WriteByte(0x00);
                writer.WriteByte(useFlags);
            }
            else
            {
                writer.WriteByte(0xF0);
                writer.WriteByte(useFlags);
            }
            writer.WriteUInt16(targetPlayerId);
            WriteResolvedEntitySynchInfo(writer, "MON-ATTACK", "Monster", entitySynchInfo, true);

            if (beginEndStream)
                writer.WriteByte(0x06);

            return writer.ToArray();
        }

        public static byte[] BuildMonsterActionStopPacket(uint monsterBehaviorId, ResolvedEntitySynchInfo entitySynchInfo)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16((ushort)monsterBehaviorId);
            writer.WriteByte(0x05);
            WriteResolvedEntitySynchInfo(writer, "MON-ATTACK-STOP", "Monster", entitySynchInfo, true);
            return writer.ToArray();
        }

        public static byte[] BuildPlayerStunActionPacket(ushort behaviorId, byte actionClassId, ushort headingWire, ushort strengthWire, ResolvedEntitySynchInfo entitySynchInfo)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(behaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(actionClassId);
            writer.WriteByte(0x00);
            if (actionClassId == 0x0A || actionClassId == 0x0B)
            {
                writer.WriteUInt16(headingWire);
                writer.WriteUInt16(strengthWire);
            }
            WriteResolvedEntitySynchInfo(writer, "PLAYER-STUN-ACTION", "Avatar", entitySynchInfo, true);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        public static byte[] BuildPlayerModifierAddPacket(ushort modifiersId, string gcType, uint modifierId, byte level, uint powerLevel, uint durationTicks, byte sourceIsSelf, ResolvedEntitySynchInfo entitySynchInfo)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(modifiersId);
            writer.WriteByte(0x00);
            WriteGCType(writer, gcType, true);
            writer.WriteUInt32(modifierId);
            writer.WriteByte(level);
            writer.WriteUInt32(powerLevel);
            writer.WriteUInt32(durationTicks);
            writer.WriteByte(sourceIsSelf);
            WriteResolvedEntitySynchInfo(writer, "PLAYER-MODIFIER-ADD", "Avatar", entitySynchInfo, true);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        public static byte[] BuildPlayerModifierRemovePacket(ushort modifiersId, uint modifierId, ResolvedEntitySynchInfo entitySynchInfo)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(modifiersId);
            writer.WriteByte(0x01);
            writer.WriteUInt32(modifierId);
            WriteResolvedEntitySynchInfo(writer, "PLAYER-MODIFIER-REMOVE", "Avatar", entitySynchInfo, true);
            writer.WriteByte(0x06);
            return writer.ToArray();
        }

        private struct ManipulatorEntry
        {
            public string GCType;
            public uint Id;
            public ManipulatorType Type;

            public ManipulatorEntry(string gcType, uint id, ManipulatorType type)
            {
                GCType = gcType;
                Id = id;
                Type = type;
            }
        }

        private enum ManipulatorType
        {
            MeleeWeapon,
            RangedWeapon,
            ActiveSkill
        }
    }
}
