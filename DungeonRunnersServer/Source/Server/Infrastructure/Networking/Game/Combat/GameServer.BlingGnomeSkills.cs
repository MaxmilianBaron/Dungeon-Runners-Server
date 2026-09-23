using System;
using System.Collections.Generic;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Gameplay;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private sealed class PendingBlingGnomeSkillEffect
        {
            public RRConnection Owner;
            public uint PlayerEntityId;
            public PlayerState State;
            public string InstanceKey;
            public ushort GnomeEntityId;
            public uint DueTick;
        }

        private readonly List<PendingBlingGnomeSkillEffect> _pendingBlingGnomeSkillEffects = new List<PendingBlingGnomeSkillEffect>();

        private bool IsBlingGnomeSkillTargetAvailable(RRConnection conn, byte slot, ushort targetId)
        {
            if (conn?.Avatar == null || !conn.IsConnected || !conn.IsSpawned || conn.Avatar.Id <= 0 || slot < 100)
                return false;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            SpellData spell = state != null ? ResolveActionSpell(conn, state, slot) : null;
            return state != null
                && state.CurrentHPWire != 0
                && SpellDatabase.TryValidatePlayerBlingGnomeRuntime(spell, out _)
                && BlingGnomeRuntime.Instance.TryResolveGnomeTarget(conn, targetId, out uint entityId, out _, out bool created, out _)
                && entityId == targetId
                && created
                && BlingGnomeLockstepRuntime.Instance.TryGetSnapshot(entityId, out BlingGnomeLockstepSnapshot snapshot)
                && snapshot.Initialized
                && string.Equals(snapshot.InstanceKey, RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey), StringComparison.OrdinalIgnoreCase);
        }

        private bool QueuePendingBlingGnomeActionInput(RRConnection conn, ushort componentId, byte responseId,
            byte sessionId, byte slot, ushort targetId)
        {
            if (!IsAvatarOrAvatarComponentId(conn, componentId) || !IsBlingGnomeSkillTargetAvailable(conn, slot, targetId))
                return false;
            _pendingPlayerUseTargetActionInputs.Add(new PendingPlayerUseTargetActionInput
            {
                ConnId = conn.ConnId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(conn)),
                ComponentId = componentId,
                ResponseId = responseId,
                ManipulatorId = sessionId,
                UseFlags = slot,
                TargetId = targetId,
                GnomeEntityId = targetId,
                ReceivedTick = _combatTick,
                AdmissionSequence = ReservePlayerUseTargetAction(conn)
            });
            return true;
        }

        private void HandlePlayerUseBlingGnome(RRConnection conn, ushort componentId, byte responseId,
            byte sessionId, byte slot, ushort targetId, bool behaviorChildStart = false,
            ulong unitFollowClientWaitThroughOverride = ulong.MaxValue)
        {
            if (!IsBlingGnomeSkillTargetAvailable(conn, slot, targetId))
                return;
            if (HasCurrentPlayerBehaviorSlot(conn))
            {
                DiscardPendingPlayerUsePositionWeaponBehaviorAction(conn, "UseTarget-replaces-pending-UsePosition");
                DiscardPendingPlayerSpellBehaviorAction(conn, 0, "UseTarget-replaces-pending-UsePosition");
                DiscardPendingSelfCastBehaviorAction(conn);
                ClearPendingUseTargetBehaviorAction(conn);
                conn.HasPendingUseTargetAction = true;
                conn.PendingUseTargetActionId = targetId;
                conn.PendingUseTargetFlags = slot;
                conn.PendingUseTargetComponentId = componentId;
                conn.PendingUseTargetSessionId = sessionId;
                conn.PendingUseTargetResponseId = responseId;
                conn.PendingUseTargetReceivedTick = _combatTick;
                conn.PendingUseTargetActionSequence = conn.LastPlayerUseTargetActionSequence;
                conn.PendingUseTargetIsRedundant = conn.HasActiveUseTarget
                    && IsUseTargetActionRedundant(conn.ActiveUseTargetFlags, conn.ActiveUseTargetId, slot, targetId);
                return;
            }
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            ActivateUseTarget(conn, targetId, slot, componentId, sessionId, responseId: responseId);
            ResolveAuthoritativePlayerPositionFixed(conn, out int x, out int y, out int z);
            if (EvaluateBlingGnomeTargetIsClear(conn, state, x, y, z))
            {
                CancelUseTargetMoving(conn, "use-ready");
                conn.ActiveUseTargetInitUsePassed = true;
                if (!TryBeginBlingGnomeSkill(conn, _combatTick, behaviorChildStart))
                    StopPlayerUseTargetActionSlot(conn, "BlingGnome-validateUse-failed");
            }
            else if (BlingGnomeRuntime.Instance.TryGetGnomeFixedSnapshot(conn.ConnId, out _, out int targetX, out int targetY, out _, out _))
            {
                StartUseTargetMoving(conn, targetX, targetY, entityId: targetId, mirrorWithAction: true,
                    admissionTicks: 0, includeQueuedUnitFollowClientRecords: false,
                    unitFollowClientWaitThroughOverride: unitFollowClientWaitThroughOverride);
            }
        }

        private bool TryBeginBlingGnomeSkill(RRConnection conn, uint tick, bool startsAfterSkillsChild)
        {
            if (!IsBlingGnomeSkillTargetAvailable(conn, conn.ActiveUseTargetFlags, conn.ActiveUseTargetId)
                || !BlingGnomeLockstepRuntime.Instance.CanStartConvertItemsToGold(conn, conn.ActiveUseTargetId))
                return false;
            foreach (PendingBlingGnomeSkillEffect pending in _pendingBlingGnomeSkillEffects)
                if (pending.Owner == conn)
                    return false;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            SpellData spell = ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags);
            if (!CommitActiveSkillUse(conn, state, spell, conn.ActiveUseTargetComponentId, conn.ActiveUseTargetFlags,
                GetPlayerSkillLevel(conn, spell), unchecked((int)tick), startsAfterSkillsChild))
                return false;
            _pendingBlingGnomeSkillEffects.Add(new PendingBlingGnomeSkillEffect
            {
                Owner = conn,
                PlayerEntityId = (uint)conn.Avatar.Id,
                State = state,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey),
                GnomeEntityId = conn.ActiveUseTargetId,
                DueTick = unchecked((uint)(startsAfterSkillsChild
                    ? ResolveActiveSkillEffectTickAfterSkillsChild(tick, spell, state)
                    : ResolveActiveSkillEffectTickBeforeSkillsChild(tick, spell, state)))
            });
            return true;
        }

        private void ProcessPendingBlingGnomeSkillEffects(uint playerEntityId, uint tick)
        {
            for (int index = 0; index < _pendingBlingGnomeSkillEffects.Count;)
            {
                PendingBlingGnomeSkillEffect pending = _pendingBlingGnomeSkillEffects[index];
                RRConnection conn = pending.Owner;
                bool current = conn != null && conn.IsConnected && conn.IsSpawned
                    && conn.Avatar?.Id == pending.PlayerEntityId
                    && ReferenceEquals(GetPlayerState(conn.ConnId.ToString()), pending.State)
                    && string.Equals(RoomRuntime.NormalizeInstanceKey(conn.RuntimeInstanceKey), pending.InstanceKey, StringComparison.OrdinalIgnoreCase);
                if (!current)
                {
                    _pendingBlingGnomeSkillEffects.RemoveAt(index);
                    continue;
                }
                if (pending.PlayerEntityId != playerEntityId || unchecked((int)(tick - pending.DueTick)) < 0)
                {
                    index++;
                    continue;
                }
                _pendingBlingGnomeSkillEffects.RemoveAt(index);
                BlingGnomeRuntime.Instance.SetServer(this);
                BlingGnomeRuntime.Instance.ApplyConversionSkillEffect(conn, pending.GnomeEntityId, tick);
            }
        }

        private bool EvaluateBlingGnomeInitUse(RRConnection conn, PlayerState state)
        {
            if (!IsBlingGnomeSkillTargetAvailable(conn, conn.ActiveUseTargetFlags, conn.ActiveUseTargetId)
                || !BlingGnomeRuntime.Instance.TryGetGnomeFixedSnapshot(conn.ConnId, out _, out int tx, out int ty, out int tz, out _))
                return false;
            SpellData spell = ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags);
            ResolveAuthoritativePlayerPositionFixed(conn, out int x, out int y, out int z);
            bool passed = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(x, y, z, tx, ty, tz,
                spell.InitUseRangeF32 > 0 ? spell.InitUseRangeF32 : 64000, Math.Max(0, spell.ClientSyncToleranceF32),
                out _, out _, out _);
            conn.UseTargetMovingInitUseInitialized = passed;
            return passed;
        }

        private bool EvaluateBlingGnomeTargetIsClear(RRConnection conn, PlayerState state, int x, int y, int z)
        {
            if (!IsBlingGnomeSkillTargetAvailable(conn, conn.ActiveUseTargetFlags, conn.ActiveUseTargetId)
                || !BlingGnomeRuntime.Instance.TryGetGnomeFixedSnapshot(conn.ConnId, out _, out int tx, out int ty, out int tz, out _))
                return false;
            SpellData spell = ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags);
            CombatPlayer player = CombatRuntime.Instance.GetPlayer((uint)conn.Avatar.Id);
            if (player != null)
            {
                x = player.PredictedLocation2DFixedX;
                y = player.PredictedLocation2DFixedY;
                PathMap map = ResolveUseTargetMovingPathMap(conn);
                if (map != null)
                    z = map.GetHeightAtFixed(x, y, z);
            }
            int range = CombatRuntime.Instance.ResolvePlayerBlingGnomeSkillGeometry(spell, x, y, z, tx, ty, tz,
                out int ax, out int ay, out int az, out int bx, out int by, out int bz);
            CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(ax, ay, az, bx, by, bz, range, 0,
                out int distance, out long distanceSquared, out long thresholdSquared);
            conn.ActiveUseTargetInitUseRangeF32 = range;
            conn.ActiveUseTargetInitUseDistanceF32 = distance;
            return thresholdSquared > 0 && distanceSquared < thresholdSquared
                && !WorldCollision.Instance.TrySegmentHitFixed(conn.CurrentZoneName, conn.RuntimeInstanceKey,
                    ax, ay, az, bx, by, bz, UnitMover.Fixed, out _);
        }
    }
}
