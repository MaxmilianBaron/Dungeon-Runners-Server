using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Combat;
using DungeonRunners.Core;
using DungeonRunners.Engine;
using DungeonRunners.Gameplay;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private enum PvpTargetRelation : byte
        {
            None,
            Self,
            Friend,
            Enemy
        }

        private sealed class PvpWeaponUseState
        {
            public RRConnection Source;
            public int TargetConnId;
            public string InstanceKey;
            public uint StartTick;
            public uint LastTick = uint.MaxValue;
            public int ElapsedTicks;
            public int TotalTicks;
            public int SingleCycleTicks;
            public int HitTick;
            public int SoundTick;
            public int BurstCount;
            public int HitEvents;
            public int SoundEvents;
            public bool Ranged;
            public bool Projectile;
            public int SourceOffsetFixedX;
            public int SourceOffsetFixedY;
            public int SourceOffsetFixedZ;
        }

        private sealed class PvpWeaponProjectileState
        {
            public long Sequence;
            public int SourceConnId;
            public int RequestedTargetConnId;
            public string InstanceKey;
            public string ZoneName;
            public int CurrentFixedX;
            public int CurrentFixedY;
            public int CurrentFixedZ;
            public int VelocityFixedX;
            public int VelocityFixedY;
            public int StepDistanceF32;
            public int CurrentDistanceF32;
            public int MaxDistanceF32;
            public int ProjectileSizeF32;
            public int MaxLifetimeTicks;
            public int UpdatesCompleted;
            public uint LastTick = uint.MaxValue;
        }

        private sealed class PendingPvpSkillCast
        {
            public long Sequence;
            public int SourceConnId;
            public int TargetConnId;
            public string InstanceKey;
            public SpellData Spell;
            public int SkillLevel;
            public ushort ComponentId;
            public byte ActionId;
            public uint UseTick;
            public uint DueTick;
        }

        private sealed class PendingPvpDamageModifier
        {
            public long Sequence;
            public int SourceConnId;
            public int TargetConnId;
            public string InstanceKey;
            public SpellData Spell;
            public int SkillLevel;
            public uint NextTick;
            public uint EndTick;
            public ushort FrequencyTicks;
        }

        private readonly Dictionary<uint, PvpWeaponUseState> _pvpWeaponUses = new Dictionary<uint, PvpWeaponUseState>();
        private readonly Dictionary<uint, uint> _pvpWeaponReadyTicks = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, byte> _pvpWeaponAnimationIndices = new Dictionary<uint, byte>();
        private readonly Dictionary<long, PvpWeaponProjectileState> _pvpWeaponProjectiles = new Dictionary<long, PvpWeaponProjectileState>();
        private readonly List<PendingPvpSkillCast> _pendingPvpSkillCasts = new List<PendingPvpSkillCast>();
        private readonly List<PendingPvpDamageModifier> _pendingPvpDamageModifiers = new List<PendingPvpDamageModifier>();
        private long _nextPvpWeaponProjectileSequence;
        private long _nextPvpSkillSequence;
        private long _nextPvpDamageModifierSequence;

        private bool TryAuthorizePvpUseTarget(RRConnection source, RRConnection target, byte useFlags, out string reason)
        {
            reason = null;
            if (!TryResolvePvpTargetRelation(source, target, out PvpTargetRelation relation, out reason))
                return false;
            if (useFlags < 100)
            {
                if (relation != PvpTargetRelation.Enemy)
                {
                    reason = relation == PvpTargetRelation.Self ? "self-attack" : "friendly-fire";
                    return false;
                }
                return true;
            }
            PlayerState state = GetPlayerState(source.ConnId.ToString());
            SpellData spell = ResolveActionSpell(source, state, useFlags);
            if (spell == null)
            {
                reason = "skill-unresolved";
                return false;
            }
            if (!IsPvpSkillTargetRelationAllowed(spell, relation))
            {
                reason = $"skill-target-type:{spell.TargetType ?? "missing"}:{relation}";
                return false;
            }
            if (!IsPvpSkillSupported(spell, out reason))
                return false;
            return true;
        }

        private bool IsPvpActionTargetAvailable(RRConnection source, RRConnection target, byte useFlags)
        {
            return TryAuthorizePvpUseTarget(source, target, useFlags, out _);
        }

        private bool TryResolvePvpTargetRelation(RRConnection source, RRConnection target, out PvpTargetRelation relation, out string reason)
        {
            relation = PvpTargetRelation.None;
            reason = null;
            if (source == null || target == null || !source.IsConnected || !target.IsConnected || !source.IsSpawned || !target.IsSpawned)
            {
                reason = "connection-unavailable";
                return false;
            }
            if (!string.Equals(RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)), RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(target)), StringComparison.OrdinalIgnoreCase))
            {
                reason = "cross-instance";
                return false;
            }
            CombatPlayer sourcePlayer = source.Avatar != null && source.Avatar.Id > 0
                ? CombatRuntime.Instance.GetPlayer((uint)source.Avatar.Id)
                : null;
            CombatPlayer targetPlayer = target.Avatar != null && target.Avatar.Id > 0
                ? CombatRuntime.Instance.GetPlayer((uint)target.Avatar.Id)
                : null;
            if (sourcePlayer?.PlayerState == null || targetPlayer?.PlayerState == null || !sourcePlayer.IsAlive || !targetPlayer.IsAlive
                || sourcePlayer.PlayerState.CurrentHPWire == 0 || targetPlayer.PlayerState.CurrentHPWire == 0)
            {
                reason = "dead-or-unregistered";
                return false;
            }

            bool self = source.ConnId == target.ConnId;
            DuelRuntime.DuelInfo sourceDuel = _duelRuntime.GetDuel(source.LoginName);
            if (sourceDuel != null && sourceDuel.State == DuelRuntime.DuelState.Active)
            {
                if (self)
                {
                    relation = PvpTargetRelation.Self;
                    return true;
                }
                if (_duelRuntime.AreInActiveDuel(source.LoginName, target.LoginName))
                {
                    relation = PvpTargetRelation.Enemy;
                    return true;
                }
                reason = "unrelated-duel-target";
                return false;
            }

            PVPMatchmaking.Match sourceMatch = PVPMatchmaking.Instance.GetMatchForPlayer(source.LoginName);
            PVPMatchmaking.Match targetMatch = PVPMatchmaking.Instance.GetMatchForPlayer(target.LoginName);
            if (sourceMatch == null || targetMatch == null
                || !string.Equals(sourceMatch.MatchId, targetMatch.MatchId, StringComparison.Ordinal)
                || sourceMatch.Phase != PVPMatchmaking.Match.MatchPhase.Combat)
            {
                reason = "pvp-combat-inactive";
                return false;
            }
            if (self)
            {
                relation = PvpTargetRelation.Self;
                return true;
            }
            if (sourceMatch.FreeForAll)
            {
                relation = PvpTargetRelation.Enemy;
                return true;
            }
            relation = sourceMatch.IsRed(source.LoginName) == sourceMatch.IsRed(target.LoginName)
                ? PvpTargetRelation.Friend
                : PvpTargetRelation.Enemy;
            return true;
        }

        private static bool IsPvpSkillTargetRelationAllowed(SpellData spell, PvpTargetRelation relation)
        {
            string targetType = (spell?.TargetType ?? string.Empty).Trim();
            if (int.TryParse(targetType, out int numericTargetType))
            {
                if (numericTargetType == 1) return relation == PvpTargetRelation.Friend;
                if (numericTargetType == 2) return relation == PvpTargetRelation.Enemy;
                if (numericTargetType == 11) return relation == PvpTargetRelation.Friend || relation == PvpTargetRelation.Self;
                if (numericTargetType == 0) return relation == PvpTargetRelation.Self;
                return false;
            }
            if (targetType.Equals("ENEMY", StringComparison.OrdinalIgnoreCase))
                return relation == PvpTargetRelation.Enemy;
            if (targetType.Equals("FRIEND", StringComparison.OrdinalIgnoreCase))
                return relation == PvpTargetRelation.Friend;
            if (targetType.Equals("FRIENDSELF", StringComparison.OrdinalIgnoreCase)
                || targetType.Equals("FRIEND_SELF", StringComparison.OrdinalIgnoreCase))
                return relation == PvpTargetRelation.Friend || relation == PvpTargetRelation.Self;
            if (targetType.Equals("SELF", StringComparison.OrdinalIgnoreCase))
                return relation == PvpTargetRelation.Self;
            return false;
        }

        private static bool IsPvpSkillSupported(SpellData spell, out string reason)
        {
            reason = null;
            if (spell == null)
            {
                reason = "skill-unresolved";
                return false;
            }
            if (!SpellDatabase.TryValidatePvpOrderedRuntime(spell, out reason))
                return false;
            if (spell.HasTeleportEffect || spell.SkillCategory == SkillCategory.Summon)
            {
                reason = "unsupported-pvp-teleport-or-summon";
                return false;
            }
            if (spell.HasSpellKnockDownEffect || spell.HasFear || spell.HasSlow)
            {
                reason = "unsupported-pvp-control-effect";
                return false;
            }
            if (spell.HasBurstEffect || spell.IsChainSpell && spell.NumForks > 1 || spell.IsChainSpell && spell.IsAoE)
            {
                reason = "unsupported-pvp-burst-or-fork-topology";
                return false;
            }
            if (spell.ModifierEffects != null)
            {
                foreach (SpellModifierData modifier in spell.ModifierEffects)
                {
                    if (modifier == null || !string.Equals(modifier.ModifierFamily, "AttributeModifier", StringComparison.Ordinal))
                    {
                        reason = $"unsupported-pvp-modifier-family:{modifier?.ModifierFamily ?? "unknown"}";
                        return false;
                    }
                    if (modifier.Attributes == null || modifier.Attributes.Count == 0)
                    {
                        reason = "unsupported-pvp-empty-modifier";
                        return false;
                    }
                    foreach (SpellAttributeModifierData attribute in modifier.Attributes)
                    {
                        if (attribute == null || string.IsNullOrWhiteSpace(attribute.Attribute))
                        {
                            reason = "unsupported-pvp-empty-modifier-attribute";
                            return false;
                        }
                        if (IsPvpModifierAttributeSupported(attribute.Attribute))
                            continue;
                        reason = $"unsupported-pvp-modifier-attribute:{attribute.Attribute}";
                        return false;
                    }
                }
            }
            bool hasModifiers = spell.ModifierEffects != null && spell.ModifierEffects.Any(modifier => modifier != null && !string.IsNullOrWhiteSpace(modifier.ModifierId ?? modifier.EffectId));
            if (!spell.HasDirectDamageEffect && !spell.HasImmediateWeaponDamageEffect && !spell.HasDeferredProjectileModifierDamage && !hasModifiers)
            {
                reason = "unsupported-pvp-effect-family";
                return false;
            }
            return true;
        }

        private static bool IsPvpModifierAttributeSupported(string attribute)
        {
            switch ((attribute ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant())
            {
                case "SPEEDMOD":
                case "SIZEMOD":
                case "STRENGTH":
                case "AGILITY":
                case "ENDURANCE":
                case "INTELLECT":
                case "HITPOINTREGENBONUS":
                case "MANAPOINTREGENBONUS":
                case "STUNRESIST":
                case "AGGROMOD":
                case "ATTACKSPEEDMOD":
                case "DAMAGEMOD":
                case "MELEEDAMAGEMOD":
                case "ATTACKRATINGMOD":
                case "CRUSHINGDAMAGERESIST":
                case "PIERCINGDAMAGERESIST":
                case "SLASHINGDAMAGERESIST":
                case "FIREDAMAGERESIST":
                case "ICEDAMAGERESIST":
                case "POISONDAMAGERESIST":
                case "SHADOWDAMAGERESIST":
                case "DIVINEDAMAGERESIST":
                    return true;
                default:
                    return false;
            }
        }

        private void HandlePlayerAttackPlayer(
            RRConnection conn,
            ushort componentId,
            byte responseId,
            byte manipulatorId,
            byte useFlags,
            ushort targetId,
            RRConnection targetPlayer,
            int mirrorAdmissionTicks = ClientUseTargetMirrorAdmissionTicks,
            ulong unitFollowClientWaitThroughOverride = ulong.MaxValue,
            int inputAdmissionTick = 0,
            bool behaviorChildStart = false)
        {
            if (conn == null || targetPlayer == null)
                return;
            if (ShouldQueuePendingUseTargetBehaviorAction(conn, useFlags, targetPlayer))
            {
                QueuePendingUseTargetBehaviorAction(conn, componentId, responseId, manipulatorId, useFlags, targetId, targetPlayer);
                return;
            }
            if (!TryAuthorizePvpUseTarget(conn, targetPlayer, useFlags, out string reason))
            {
                SendUseTargetActionFailure(conn, componentId, responseId, reason);
                Debug.LogError($"[PVP-ACTION] source={conn.LoginName} target={targetPlayer.LoginName} flags={useFlags} result=rejected reason={reason}");
                return;
            }
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null || conn.Avatar == null || targetPlayer.Avatar == null)
                return;
            if (IsZoneSpawnInvulnerabilityActive(conn))
                ClearZoneSpawnInvulnerability(conn, $"PVP-ACTION-0x50 flags={useFlags} target={targetId}");

            SpellData spell = useFlags >= 100 ? ResolveActionSpell(conn, state, useFlags) : null;
            ResolvePvpTargetPosition(conn, out int actorFixedX, out int actorFixedY, out int actorFixedZ);
            ResolvePvpTargetPosition(targetPlayer, out int targetFixedX, out int targetFixedY, out int targetFixedZ);
            int rangeF32 = spell != null
                ? Math.Max(1, spell.InitUseRangeF32)
                : CombatRuntime.Instance.ResolvePlayerPvpWeaponTargetClearRangeF32(state);
            int toleranceF32 = spell != null ? Math.Max(0, spell.ClientSyncToleranceF32) : 0;
            bool inRange = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                actorFixedX,
                actorFixedY,
                actorFixedZ,
                targetFixedX,
                targetFixedY,
                targetFixedZ,
                rangeF32,
                toleranceF32,
                out int distanceF32,
                out _,
                out _);
            ActivateUseTarget(conn, targetId, useFlags, componentId, manipulatorId, targetPlayer.ConnId, responseId);
            conn.ActiveUseTargetInitUsePassed = false;
            conn.ActiveUseTargetInitUseRangeF32 = rangeF32;
            conn.ActiveUseTargetInitUseDistanceF32 = distanceF32;
            conn.ActiveUseTargetClientToleranceF32 = toleranceF32;
            StartUseTargetMoving(
                conn,
                targetFixedX,
                targetFixedY,
                followConnId: targetPlayer.ConnId,
                mirrorWithAction: true,
                admissionTicks: mirrorAdmissionTicks,
                includeQueuedUnitFollowClientRecords: false,
                unitFollowClientWaitThroughOverride: unitFollowClientWaitThroughOverride);
            CombatRuntime.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, true, (uint)targetPlayer.Avatar.Id);
            Debug.LogError($"[PVP-ACTION] source={conn.LoginName}#{conn.Avatar.Id} target={targetPlayer.LoginName}#{targetPlayer.Avatar.Id} flags={useFlags} skill={spell?.SkillId ?? "weapon"} inRange={inRange} distanceF32={distanceF32} rangeF32={rangeF32} inputAdmissionTick={inputAdmissionTick} behaviorChildStart={behaviorChildStart} tick={_combatTick}");
        }

        private bool EvaluatePvpUseTargetInitUse(RRConnection conn, PlayerState state, uint tickIndex)
        {
            if (!TryGetActivePvpTarget(conn, out RRConnection target, out _, out _))
                return false;
            ResolvePvpTargetPosition(conn, out int actorFixedX, out int actorFixedY, out int actorFixedZ);
            ResolvePvpTargetPosition(target, out int targetFixedX, out int targetFixedY, out int targetFixedZ);
            SpellData spell = conn.ActiveUseTargetFlags >= 100 ? ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags) : null;
            int rangeF32 = spell != null ? Math.Max(1, spell.InitUseRangeF32) : CombatRuntime.Instance.ResolvePlayerPvpWeaponTargetClearRangeF32(state);
            int toleranceF32 = spell != null ? Math.Max(0, spell.ClientSyncToleranceF32) : 0;
            bool passed = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                actorFixedX, actorFixedY, actorFixedZ,
                targetFixedX, targetFixedY, targetFixedZ,
                rangeF32, toleranceF32,
                out int distanceF32, out _, out _);
            if (passed)
            {
                conn.UseTargetMovingInitUseInitialized = true;
                if (spell != null && spell.InitUseResetsCooldown && !conn.ActiveUseTargetInitUseCooldownStarted
                    && !IsActiveSkillCooldown(conn, spell, conn.ActiveUseTargetFlags, out _))
                {
                    int skillLevel = GetPlayerSkillLevel(conn, spell);
                    StartActiveSkillCooldown(conn, conn.ActiveUseTargetComponentId, conn.ActiveUseTargetFlags, spell, state, skillLevel, true);
                    conn.ActiveUseTargetInitUseCooldownStarted = true;
                    conn.ActiveUseTargetInitUseCooldownComponentId = conn.ActiveUseTargetComponentId;
                    conn.ActiveUseTargetInitUseCooldownActionId = conn.ActiveUseTargetFlags;
                }
            }
            Debug.LogError($"[PVP-USETARGET-INIT] source={conn.LoginName} target={target.LoginName} tick={tickIndex} distanceF32={distanceF32} rangeF32={rangeF32} toleranceF32={toleranceF32} passed={passed}");
            return passed;
        }

        private bool EvaluatePvpUseTargetTargetIsClear(RRConnection conn, PlayerState state, uint tickIndex, int actorFixedX, int actorFixedY, int actorFixedZ)
        {
            if (!TryGetActivePvpTarget(conn, out RRConnection target, out _, out _))
                return false;
            ResolvePvpTargetPosition(target, out int targetFixedX, out int targetFixedY, out int targetFixedZ);
            CombatPlayer sourcePlayer = conn.Avatar != null ? CombatRuntime.Instance.GetPlayer((uint)conn.Avatar.Id) : null;
            if (sourcePlayer != null)
            {
                actorFixedX = sourcePlayer.ClientSimulationPosFixedX;
                actorFixedY = sourcePlayer.ClientSimulationPosFixedY;
                actorFixedZ = sourcePlayer.ClientSimulationPosFixedZ;
            }
            bool skillAction = conn.ActiveUseTargetFlags >= 100;
            SpellData spell = skillAction ? ResolveActionSpell(conn, state, conn.ActiveUseTargetFlags) : null;
            if (skillAction && spell == null)
                return false;
            int rangeF32 = skillAction
                ? CombatRuntime.Instance.ResolvePlayerPvpSkillTargetClearRangeF32(spell)
                : CombatRuntime.Instance.ResolvePlayerPvpWeaponTargetClearRangeF32(state);
            bool passed = CombatRuntime.Instance.EvaluateUseTargetInitUseFixed3D(
                actorFixedX, actorFixedY, actorFixedZ,
                targetFixedX, targetFixedY, targetFixedZ,
                rangeF32, 0,
                out int distanceF32, out _, out _);
            bool pathClear;
            if (skillAction)
            {
                pathClear = !WorldCollision.Instance.TrySegmentHitFixed(
                    conn.CurrentZoneName,
                    conn.RuntimeInstanceKey,
                    actorFixedX,
                    actorFixedY,
                    actorFixedZ,
                    targetFixedX,
                    targetFixedY,
                    targetFixedZ,
                    UnitMover.Fixed,
                    out _);
            }
            else
            {
                PathMap pathMap = ResolveUseTargetMovingPathMap(conn);
                pathClear = pathMap != null && pathMap.CanReachPointFixed(actorFixedX, actorFixedY, targetFixedX, targetFixedY);
            }
            passed = passed && pathClear;
            conn.ActiveUseTargetInitUseEvaluationTick = tickIndex;
            conn.ActiveUseTargetInitUseEvaluationTargetId = conn.ActiveUseTargetId;
            conn.ActiveUseTargetInitUsePassed = passed;
            conn.ActiveUseTargetInitUseRangeF32 = rangeF32;
            conn.ActiveUseTargetInitUseDistanceF32 = distanceF32;
            conn.ActiveUseTargetClientToleranceF32 = 0;
            Debug.LogError($"[PVP-USETARGET-CLEAR] source={conn.LoginName} target={target.LoginName} tick={tickIndex} distanceF32={distanceF32} rangeF32={rangeF32} pathClear={pathClear} passed={passed}");
            return passed;
        }

        private bool TryGetActivePvpTarget(RRConnection source, out RRConnection target, out CombatPlayer sourcePlayer, out CombatPlayer targetPlayer)
        {
            target = null;
            sourcePlayer = null;
            targetPlayer = null;
            if (source == null || source.ActiveUseTargetPlayerConnId == 0
                || !_connections.TryGetValue(source.ActiveUseTargetPlayerConnId, out target)
                || !IsPvpActionTargetAvailable(source, target, source.ActiveUseTargetFlags))
                return false;
            sourcePlayer = source.Avatar != null && source.Avatar.Id > 0 ? CombatRuntime.Instance.GetPlayer((uint)source.Avatar.Id) : null;
            targetPlayer = target.Avatar != null && target.Avatar.Id > 0 ? CombatRuntime.Instance.GetPlayer((uint)target.Avatar.Id) : null;
            return sourcePlayer?.PlayerState != null && targetPlayer?.PlayerState != null;
        }

        private static void ResolvePvpTargetPosition(RRConnection target, out int fixedX, out int fixedY, out int fixedZ)
        {
            CombatPlayer player = target?.Avatar != null && target.Avatar.Id > 0
                ? CombatRuntime.Instance.GetPlayer((uint)target.Avatar.Id)
                : null;
            if (player != null)
            {
                fixedX = player.ClientSimulationPosFixedX;
                fixedY = player.ClientSimulationPosFixedY;
                fixedZ = player.ClientSimulationPosFixedZ;
                return;
            }
            fixedX = target != null && target.HasReflectedAvatarPosition ? target.ReflectedAvatarPosFixedX : target?.PlayerPosFixedX ?? 0;
            fixedY = target != null && target.HasReflectedAvatarPosition ? target.ReflectedAvatarPosFixedY : target?.PlayerPosFixedY ?? 0;
            fixedZ = target != null && target.HasReflectedAvatarPosition ? target.ReflectedAvatarPosFixedZ : target?.PlayerPosFixedZ ?? 0;
        }

        private bool BeginPvpWeaponUse(RRConnection source, uint simulationTick)
        {
            if (!TryGetActivePvpTarget(source, out RRConnection target, out CombatPlayer sourcePlayer, out _))
                return false;
            uint sourceEntityId = sourcePlayer.EntityId;
            if (_pvpWeaponUses.TryGetValue(sourceEntityId, out PvpWeaponUseState activeUse))
                return activeUse.TargetConnId == target.ConnId;
            if (_pvpWeaponReadyTicks.TryGetValue(sourceEntityId, out uint readyTick)
                && unchecked((int)(simulationTick - readyTick)) < 0)
            {
                Debug.LogError($"[PVP-WEAPON] source={source.LoginName} target={target.LoginName} tick={simulationTick} result=cooldown readyTick={readyTick}");
                CancelUseTargetMoving(source, "PVP-Weapon::validateUse-cooldown");
                return false;
            }
            PlayerState state = sourcePlayer.PlayerState;
            bool ranged = DamageResolver.IsRangedWeapon(state);
            MersenneTwister rng = CombatRuntime.Instance.GetRoomRngForInstance(RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)));
            if (rng == null)
            {
                Debug.LogError($"[PVP-WEAPON] source={source.LoginName} result=missing-room-rng");
                return false;
            }
            byte previousAnimationIndex = _pvpWeaponAnimationIndices.TryGetValue(sourceEntityId, out byte existingIndex) ? existingIndex : (byte)0;
            byte animationIndex = previousAnimationIndex;
            uint useRaw = 0;
            if (!ranged)
            {
                useRaw = RngLedger.Generate(rng, "room", "player-pvp-melee:MeleeWeapon::use", source.ConnId.ToString(), sourceEntityId);
                animationIndex = (byte)(((useRaw & 1u) + previousAnimationIndex + 1u) % 3u);
                _pvpWeaponAnimationIndices[sourceEntityId] = animationIndex;
            }
            int selector = ranged
                ? state.WeaponShotType != 0 ? 15 : 10
                : 10 + animationIndex + (state.WeaponEquipmentSlot == 11 ? 3 : 0);
            if (!SpellDatabase.TryResolvePlayerWeaponAnimation(
                selector,
                state,
                out int animationId,
                out int frames,
                out int hitFrame,
                out int soundFrame,
                out int sourceOffsetFixedX,
                out int sourceOffsetFixedY,
                out int sourceOffsetFixedZ))
            {
                Debug.LogError($"[PVP-WEAPON] source={source.LoginName} selector={selector} result=attack-timing-unresolved");
                return false;
            }
            int speed = DamageResolver.ResolveWeaponSpeedField(state);
            int singleTicks = Math.Max(1, frames * 100 / Math.Max(1, speed));
            int hitTick = hitFrame <= 0 ? 0 : Math.Max(1, hitFrame * 100 / Math.Max(1, speed));
            int soundTick = soundFrame <= 0 ? 0 : Math.Max(1, soundFrame * 100 / Math.Max(1, speed));
            int burstCount = ranged ? Math.Max(1, state.WeaponBurstCount) : 1;
            var use = new PvpWeaponUseState
            {
                Source = source,
                TargetConnId = target.ConnId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)),
                StartTick = simulationTick,
                LastTick = simulationTick,
                TotalTicks = ranged ? singleTicks * burstCount : singleTicks,
                SingleCycleTicks = singleTicks,
                HitTick = hitTick,
                SoundTick = soundTick,
                BurstCount = burstCount,
                Ranged = ranged,
                Projectile = ranged && DamageResolver.IsProjectileWeapon(state) && state.WeaponProjectileSpeedF32 > 0 && state.WeaponProjectileSizeF32 > 0,
                SourceOffsetFixedX = sourceOffsetFixedX,
                SourceOffsetFixedY = sourceOffsetFixedY,
                SourceOffsetFixedZ = sourceOffsetFixedZ
            };
            _pvpWeaponUses[sourceEntityId] = use;
            int cooldownTicks = DamageResolver.ResolveBasicAttackCooldownTicks(state);
            _pvpWeaponReadyTicks[sourceEntityId] = unchecked(simulationTick + (uint)Math.Max(0, cooldownTicks));
            source.ActiveUseTargetStartedWeaponUse = true;
            Debug.LogError($"[PVP-WEAPON] source={source.LoginName}#{sourceEntityId} target={target.LoginName} selector={selector} animation={animationId} frames={frames} hit={hitFrame}->{hitTick} sound={soundFrame}->{soundTick} speed={speed} burst={burstCount} projectile={use.Projectile} useRaw=0x{useRaw:X8} animationIndex={previousAnimationIndex}->{animationIndex} startTick={simulationTick} totalTicks={use.TotalTicks} cooldownTicks={cooldownTicks}");
            return true;
        }

        private bool IsPvpWeaponUseBusy(uint sourceEntityId)
        {
            return sourceEntityId != 0 && _pvpWeaponUses.ContainsKey(sourceEntityId);
        }

        private void TickPvpWeaponUse(uint sourceEntityId, uint simulationTick)
        {
            ProcessPendingPvpSkills(sourceEntityId, simulationTick);
            ProcessPendingPvpDamageModifiers(sourceEntityId, simulationTick);
            if (!_pvpWeaponUses.TryGetValue(sourceEntityId, out PvpWeaponUseState use) || use == null || use.LastTick == simulationTick)
                return;
            use.LastTick = simulationTick;
            use.ElapsedTicks++;
            int nextSoundTick = use.Ranged
                ? use.SoundEvents * use.SingleCycleTicks + use.SoundTick + 1
                : use.SoundTick;
            if (use.SoundEvents < use.BurstCount && use.ElapsedTicks >= nextSoundTick)
            {
                use.SoundEvents++;
                uint soundRaw = RandomStreams.GenerateGlobalSound("player-pvp-weapon:sound", use.Source.ConnId.ToString());
                Debug.LogError($"[PVP-WEAPON-SOUND] source={use.Source.LoginName} tick={simulationTick} elapsed={use.ElapsedTicks} burst={use.SoundEvents}/{use.BurstCount} raw=0x{soundRaw:X8}");
            }
            int nextHitTick = use.Ranged
                ? use.HitEvents * use.SingleCycleTicks + use.HitTick + 1
                : use.HitTick;
            if (use.HitEvents < use.BurstCount && use.ElapsedTicks >= nextHitTick)
            {
                use.HitEvents++;
                if (use.Projectile)
                    QueuePvpWeaponProjectile(use, simulationTick);
                else
                    ResolvePvpWeaponHit(use.Source, use.TargetConnId, simulationTick, "PVP-WEAPON-HIT");
            }
            if (use.ElapsedTicks < use.TotalTicks)
                return;
            _pvpWeaponUses.Remove(sourceEntityId);
            if (use.Source != null && use.Source.ActiveUseTargetPlayerConnId == use.TargetConnId)
                use.Source.ActiveUseTargetWeaponReleasePending = true;
            Debug.LogError($"[PVP-WEAPON] source={use.Source?.LoginName ?? sourceEntityId.ToString()} tick={simulationTick} result=cycle-complete hits={use.HitEvents}/{use.BurstCount}");
            if (use.Source != null
                && use.Source.HasActiveUseTarget
                && use.Source.ActiveUseTargetPlayerConnId != 0
                && use.Source.ActiveUseTargetPlayerConnId != use.TargetConnId
                && use.Source.ActiveUseTargetFlags < 100)
                BeginPvpWeaponUse(use.Source, simulationTick);
        }

        private void QueuePvpWeaponProjectile(PvpWeaponUseState use, uint simulationTick)
        {
            if (use?.Source == null || !_connections.TryGetValue(use.TargetConnId, out RRConnection target)
                || !TryAuthorizePvpUseTarget(use.Source, target, 10, out _))
                return;
            PlayerState state = GetPlayerState(use.Source.ConnId.ToString());
            ResolvePvpTargetPosition(use.Source, out int actorFixedX, out int actorFixedY, out int actorFixedZ);
            ResolvePvpTargetPosition(target, out int targetFixedX, out int targetFixedY, out _);
            int headingFixed = use.Source.HasLivePlayerPosition ? use.Source.LivePlayerHeadingFixed : use.Source.PlayerHeadingFixed;
            WeaponUseRuntime.Instance.TryResolvePlayerWeaponProjectileSourceFixed(
                use.Source,
                state,
                actorFixedX,
                actorFixedY,
                headingFixed,
                out int sourceFixedX,
                out int sourceFixedY,
                out _,
                out _,
                out int sourceOffsetFixedZ);
            if (!PathMap.TryBuildNativeRayDirectionFixed(
                targetFixedX - sourceFixedX,
                targetFixedY - sourceFixedY,
                out int directionFixedX,
                out int directionFixedY,
                out int pathDistanceF32))
                return;
            int speedF32 = Math.Max(0x100, state.WeaponProjectileSpeedF32);
            int stepDistanceF32 = WeaponUseRuntime.ProjectileStepDistanceF32(speedF32);
            int initialDistanceF32 = WeaponUseRuntime.ProjectileInitialDistanceF32(CombatRuntime.Instance.ResolveAvatarUnitBehaviorRadiusF32());
            int startFixedX = sourceFixedX + (int)(((long)directionFixedX * initialDistanceF32) >> 8);
            int startFixedY = sourceFixedY + (int)(((long)directionFixedY * initialDistanceF32) >> 8);
            int maxDistanceF32 = Math.Max(pathDistanceF32, CombatRuntime.Instance.ResolvePlayerPvpWeaponTargetClearRangeF32(state));
            var projectile = new PvpWeaponProjectileState
            {
                Sequence = ++_nextPvpWeaponProjectileSequence,
                SourceConnId = use.Source.ConnId,
                RequestedTargetConnId = target.ConnId,
                InstanceKey = use.InstanceKey,
                ZoneName = use.Source.CurrentZoneName,
                CurrentFixedX = startFixedX,
                CurrentFixedY = startFixedY,
                CurrentFixedZ = actorFixedZ + sourceOffsetFixedZ,
                VelocityFixedX = (int)(((long)directionFixedX * stepDistanceF32) >> 8),
                VelocityFixedY = (int)(((long)directionFixedY * stepDistanceF32) >> 8),
                StepDistanceF32 = stepDistanceF32,
                CurrentDistanceF32 = initialDistanceF32,
                MaxDistanceF32 = Math.Max(1, maxDistanceF32),
                ProjectileSizeF32 = Math.Max(0, state.WeaponProjectileSizeF32),
                MaxLifetimeTicks = WeaponUseRuntime.ProjectileLifetimeTicksFixed32(Math.Max(1, maxDistanceF32), speedF32),
                LastTick = simulationTick
            };
            _pvpWeaponProjectiles[projectile.Sequence] = projectile;
            CombatRuntime.Instance.RegisterClientSubEntity(ClientSubEntityKind.PlayerPvpWeaponProjectile, projectile.Sequence, projectile.InstanceKey, RemovePvpWeaponProjectileRuntime);
            Debug.LogError($"[PVP-PROJECTILE] source={use.Source.LoginName} target={target.LoginName} seq={projectile.Sequence} start=({projectile.CurrentFixedX},{projectile.CurrentFixedY},{projectile.CurrentFixedZ}) velocity=({projectile.VelocityFixedX},{projectile.VelocityFixedY}) speedF32={speedF32} sizeF32={projectile.ProjectileSizeF32} maxDistanceF32={projectile.MaxDistanceF32} maxLife={projectile.MaxLifetimeTicks} tick={simulationTick}");
        }

        private void TickPvpWeaponProjectile(long sequence, string instanceKey, uint simulationTick)
        {
            if (!_pvpWeaponProjectiles.TryGetValue(sequence, out PvpWeaponProjectileState projectile) || projectile == null)
            {
                CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerPvpWeaponProjectile, sequence, instanceKey);
                return;
            }
            if (projectile.LastTick == simulationTick)
                return;
            projectile.LastTick = simulationTick;
            int oldFixedX = projectile.CurrentFixedX;
            int oldFixedY = projectile.CurrentFixedY;
            int oldFixedZ = projectile.CurrentFixedZ;
            projectile.CurrentFixedX = unchecked(projectile.CurrentFixedX + projectile.VelocityFixedX);
            projectile.CurrentFixedY = unchecked(projectile.CurrentFixedY + projectile.VelocityFixedY);
            projectile.CurrentDistanceF32 = unchecked(projectile.CurrentDistanceF32 + projectile.StepDistanceF32);
            projectile.UpdatesCompleted++;
            int projectileRadiusF32 = WeaponUseRuntime.ProjectileRadiusFromAuthoredSizeF32(projectile.ProjectileSizeF32);
            bool staticBlocked = WorldCollision.Instance.TrySegmentHitFixed(
                projectile.ZoneName,
                projectile.InstanceKey,
                oldFixedX,
                oldFixedY,
                oldFixedZ,
                projectile.CurrentFixedX,
                projectile.CurrentFixedY,
                projectile.CurrentFixedZ,
                projectileRadiusF32,
                out WorldCollisionHit staticHit);
            RRConnection impactTarget = null;
            int impactDistanceFixed = int.MaxValue;
            if (_connections.TryGetValue(projectile.SourceConnId, out RRConnection source))
            {
                int avatarCollisionRadiusF32 = CombatRuntime.Instance.ResolveAvatarUnitBehaviorRadiusF32();
                int collisionRadiusF32 = WeaponUseRuntime.ProjectileCollisionRadiusF32(avatarCollisionRadiusF32, projectile.ProjectileSizeF32);
                int segmentDistanceFixed = UnitMover.IntSqrt(
                    ((long)projectile.CurrentFixedX - oldFixedX) * (projectile.CurrentFixedX - oldFixedX)
                    + ((long)projectile.CurrentFixedY - oldFixedY) * (projectile.CurrentFixedY - oldFixedY));
                RRConnection candidateTarget = null;
                int candidateImpactDistanceFixed = int.MaxValue;
                foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
                {
                    if (candidate == null || candidate.ConnId == projectile.SourceConnId
                        || !TryAuthorizePvpUseTarget(source, candidate, 10, out _))
                        continue;
                    ResolvePvpTargetPosition(candidate, out int candidateFixedX, out int candidateFixedY, out _);
                    if (!TryResolveProjectileSegmentUnitHitFixed(
                        oldFixedX,
                        oldFixedY,
                        projectile.CurrentFixedX,
                        projectile.CurrentFixedY,
                        segmentDistanceFixed,
                        candidateFixedX,
                        candidateFixedY,
                        collisionRadiusF32,
                        out int candidateDistanceFixed,
                        out _))
                        continue;
                    if (candidateDistanceFixed >= candidateImpactDistanceFixed)
                        continue;
                    candidateTarget = candidate;
                    candidateImpactDistanceFixed = candidateDistanceFixed;
                }
                if (candidateTarget != null
                    && (!staticBlocked || staticHit == null || candidateImpactDistanceFixed <= staticHit.DistanceFixed))
                {
                    impactTarget = candidateTarget;
                    impactDistanceFixed = candidateImpactDistanceFixed;
                    ResolvePvpWeaponHit(source, impactTarget.ConnId, simulationTick, $"PVP-PROJECTILE#{sequence}");
                }
            }
            bool expired = projectile.UpdatesCompleted >= projectile.MaxLifetimeTicks
                || projectile.CurrentDistanceF32 >= projectile.MaxDistanceF32;
            if (impactTarget != null || staticBlocked || expired)
            {
                Debug.LogError($"[PVP-PROJECTILE] seq={sequence} tick={simulationTick} result={(impactTarget != null ? "player-impact" : staticBlocked ? "world-impact" : "expired")} target={impactTarget?.LoginName ?? "none"} position=({projectile.CurrentFixedX},{projectile.CurrentFixedY},{projectile.CurrentFixedZ}) distanceF32={projectile.CurrentDistanceF32} impactDistanceFixed={(impactTarget != null ? impactDistanceFixed : -1)}");
                RemovePvpWeaponProjectile(sequence, projectile.InstanceKey);
            }
        }

        private void RemovePvpWeaponProjectileRuntime(long sequence)
        {
            _pvpWeaponProjectiles.Remove(sequence);
        }

        private void RemovePvpWeaponProjectile(long sequence, string instanceKey)
        {
            _pvpWeaponProjectiles.Remove(sequence);
            CombatRuntime.Instance.RemoveClientSubEntity(ClientSubEntityKind.PlayerPvpWeaponProjectile, sequence, instanceKey);
        }

        private void ResolvePvpWeaponHit(RRConnection source, int targetConnId, uint simulationTick, string sourceTag)
        {
            string reason = null;
            if (source == null || !_connections.TryGetValue(targetConnId, out RRConnection target)
                || !TryAuthorizePvpUseTarget(source, target, 10, out reason))
            {
                Debug.LogError($"[PVP-WEAPON-HIT] source={source?.LoginName ?? "none"} targetConn={targetConnId} tick={simulationTick} result=rejected reason={reason ?? "target-unavailable"}");
                return;
            }
            CombatPlayer attacker = source.Avatar != null ? CombatRuntime.Instance.GetPlayer((uint)source.Avatar.Id) : null;
            CombatPlayer defender = target.Avatar != null ? CombatRuntime.Instance.GetPlayer((uint)target.Avatar.Id) : null;
            PlayerState state = attacker?.PlayerState;
            if (attacker == null || defender?.PlayerState == null || state == null)
                return;
            MersenneTwister rng = CombatRuntime.Instance.GetRoomRngForInstance(RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)));
            if (rng == null)
                return;
            bool ranged = DamageResolver.IsRangedWeapon(state);
            int attackerLevel = Math.Max(0, state.Level);
            int defenderLevel = Math.Max(0, defender.PlayerState.Level);
            int defenseRating = PvpBalance.RemapDefenseRating(
                CombatRuntime.Instance.ResolveAvatarDefenseRating(defender.PlayerState, ranged),
                attackerLevel,
                defenderLevel);
            var damageInput = new WeaponDamageInput
            {
                Rng = rng,
                Source = sourceTag,
                AttackerEntityId = attacker.EntityId,
                DefenderEntityId = defender.EntityId,
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = DamageResolver.ResolveAvatarAttackRating(state),
                DefenseRating = defenseRating,
                BlockChance = CombatRuntime.Instance.ResolveAvatarBlockChance(defender.PlayerState),
                DamageLevel = DamageResolver.ResolveWeaponDamageLevel(state),
                DamageBonus = DamageResolver.ResolveWeaponDamageBonus(state),
                DamageMod = 100,
                WeaponClassId = DamageResolver.ResolveWeaponClassId(state),
                DamageTypeId = DamageResolver.ResolveDamageTypeId(state),
                WeaponDamageF32 = DamageResolver.GetWeaponBaseDamageF32(state),
                WeaponVolatilityF32 = DamageResolver.GetWeaponVolatilityF32(state),
                CritThreshold = DamageResolver.ResolveCriticalThreshold(state, defenderLevel),
                CritDamagePercent = DamageResolver.ResolveCriticalDamagePercent(state),
                AttackerState = state,
                IncludeWeaponDamageAdds = true
            };
            WeaponDamageResult damageResult = DamageResolver.ResolveWeaponDamage(damageInput);
            if (!damageResult.IsHit || damageResult.IsBlocked || damageResult.DamageWire == 0)
            {
                Debug.LogError($"[PVP-WEAPON-HIT] source={source.LoginName} target={target.LoginName} result={damageResult.ResultName} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} hitRoll={damageResult.HitRoll} hitThreshold={damageResult.HitThreshold} blockRoll={damageResult.BlockRoll} blockChance={damageInput.BlockChance} ar={damageInput.AttackRating} dr={damageInput.DefenseRating} levels={attackerLevel}->{defenderLevel} tick={simulationTick}");
                HandlePlayerDamageResolved(null, defender, false, defender.PlayerState.CurrentHPWire, sourceTag);
                return;
            }
            uint totalAppliedWire = 0;
            CombatRuntime.PvpDamageResolution baseDamage = CombatRuntime.Instance.ApplyPvpDamageQueryWire(
                attacker,
                defender,
                damageResult.DamageWire,
                DamageResolver.ResolveDamageMod(state),
                damageResult.DamageTypeId,
                ranged ? (byte)2 : (byte)1,
                rng,
                sourceTag,
                true,
                true);
            totalAppliedWire = SaturatingWireAdd(totalAppliedWire, baseDamage.AppliedDamageWire);
            if (!baseDamage.Died && damageResult.DamageAdds != null)
            {
                foreach (WeaponDamageEvent add in damageResult.DamageAdds)
                {
                    CombatRuntime.PvpDamageResolution addDamage = CombatRuntime.Instance.ApplyPvpDamageQueryWire(
                        attacker,
                        defender,
                        add.DamageWire,
                        100,
                        add.DamageTypeId,
                        3,
                        rng,
                        $"{sourceTag}-ADD-{add.Element}",
                        true,
                        false);
                    totalAppliedWire = SaturatingWireAdd(totalAppliedWire, addDamage.AppliedDamageWire);
                    if (addDamage.Died)
                        break;
                }
            }
            bool damaged = totalAppliedWire > 0;
            uint hpWire = defender.PlayerState.CurrentHPWire;
            HandlePlayerDamageResolved(null, defender, damaged, hpWire, sourceTag);
            Debug.LogError($"[PVP-WEAPON-HIT] source={source.LoginName}#{attacker.EntityId} target={target.LoginName}#{defender.EntityId} result={damageResult.ResultName} rawWire={damageResult.DamageWire} addCount={damageResult.DamageAdds?.Count ?? 0} appliedWire={totalAppliedWire} hp={baseDamage.OldHPWire}->{hpWire}/{defender.PlayerState.MaxHPWire} rngPos={rng.CallsSinceReseed} tick={simulationTick}");
            if (damaged && hpWire == 0)
                HandlePvpPlayerDeath(source, target, simulationTick, sourceTag);
        }

        private static uint SaturatingWireAdd(uint left, uint right)
        {
            ulong value = (ulong)left + right;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private bool TryBeginPvpTargetSkill(RRConnection source, uint simulationTick, bool startsAfterSkillsChild)
        {
            string reason = null;
            if (source == null || source.ActiveUseTargetPlayerConnId == 0
                || !_connections.TryGetValue(source.ActiveUseTargetPlayerConnId, out RRConnection target)
                || !TryAuthorizePvpUseTarget(source, target, source.ActiveUseTargetFlags, out reason))
            {
                Debug.LogError($"[PVP-SKILL] source={source?.LoginName ?? "none"} targetConn={source?.ActiveUseTargetPlayerConnId ?? 0} result=rejected reason={reason ?? "target-unavailable"}");
                return false;
            }
            PlayerState state = GetPlayerState(source.ConnId.ToString());
            SpellData spell = ResolveActionSpell(source, state, source.ActiveUseTargetFlags);
            if (spell == null || !IsPvpSkillSupported(spell, out reason))
            {
                SkillEffectTracker.RecordPlayerGraph(source?.Avatar != null ? (uint)source.Avatar.Id : 0u, target?.Avatar != null ? (uint)target.Avatar.Id : 0u, spell?.SkillId, spell?.TargetType, spell?.OrderedEffects?.Count ?? 0, "rejected", reason ?? "unsupported-pvp-skill", simulationTick);
                SendUseTargetActionFailure(source, source.ActiveUseTargetComponentId, 0, reason ?? "unsupported-pvp-skill");
                return false;
            }
            SkillEffectTracker.RecordPlayerGraph(source.Avatar != null ? (uint)source.Avatar.Id : 0u, target.Avatar != null ? (uint)target.Avatar.Id : 0u, spell.SkillId, spell.TargetType, spell.OrderedEffects.Count, "accepted", null, simulationTick);
            bool existing = _pendingPvpSkillCasts.Any(cast => cast.SourceConnId == source.ConnId
                && cast.TargetConnId == target.ConnId
                && cast.ComponentId == source.ActiveUseTargetComponentId
                && cast.ActionId == source.ActiveUseTargetFlags);
            if (existing)
                return true;
            int skillLevel = GetPlayerSkillLevel(source, spell);
            if (!CommitActiveSkillUse(
                source,
                state,
                spell,
                source.ActiveUseTargetComponentId,
                source.ActiveUseTargetFlags,
                skillLevel,
                unchecked((int)simulationTick),
                startsAfterSkillsChild))
                return false;
            int effectTick = startsAfterSkillsChild
                ? ResolveActiveSkillEffectTickAfterSkillsChild(simulationTick, spell, state)
                : ResolveActiveSkillEffectTickBeforeSkillsChild(simulationTick, spell, state);
            ResolvePvpTargetPosition(target, out int targetFixedX, out int targetFixedY, out _);
            ResolvePvpTargetPosition(source, out int actorFixedX, out int actorFixedY, out _);
            int distanceF32 = UnitMover.IntSqrt(
                (long)(targetFixedX - actorFixedX) * (targetFixedX - actorFixedX)
                + (long)(targetFixedY - actorFixedY) * (targetFixedY - actorFixedY));
            int projectileDelay = spell.ProjectileSpeedF32 > 0 && spell.ProjectileSizeF32 > 0
                ? WeaponUseRuntime.ProjectileImpactDelayTicksFixed32(distanceF32, spell.ProjectileSpeedF32)
                : 0;
            var cast = new PendingPvpSkillCast
            {
                Sequence = ++_nextPvpSkillSequence,
                SourceConnId = source.ConnId,
                TargetConnId = target.ConnId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)),
                Spell = spell,
                SkillLevel = skillLevel,
                ComponentId = source.ActiveUseTargetComponentId,
                ActionId = source.ActiveUseTargetFlags,
                UseTick = simulationTick,
                DueTick = unchecked((uint)Math.Max(0, effectTick + projectileDelay))
            };
            _pendingPvpSkillCasts.Add(cast);
            Debug.LogError($"[PVP-SKILL] source={source.LoginName} target={target.LoginName} spell={spell.SkillId} level={skillLevel} sequence={cast.Sequence} useTick={simulationTick} effectTick={effectTick} projectileDelay={projectileDelay} dueTick={cast.DueTick}");
            return true;
        }

        private void ProcessPendingPvpSkills(uint sourceEntityId, uint simulationTick)
        {
            RRConnection entityOwner = FindConnectionByAvatarEntityId(sourceEntityId);
            if (entityOwner == null)
                return;
            for (int castIndex = 0; castIndex < _pendingPvpSkillCasts.Count;)
            {
                PendingPvpSkillCast cast = _pendingPvpSkillCasts[castIndex];
                if (cast.SourceConnId != entityOwner.ConnId || unchecked((int)(simulationTick - cast.DueTick)) < 0)
                {
                    castIndex++;
                    continue;
                }
                _pendingPvpSkillCasts.RemoveAt(castIndex);
                string reason = null;
                if (!_connections.TryGetValue(cast.SourceConnId, out RRConnection source)
                    || !_connections.TryGetValue(cast.TargetConnId, out RRConnection target)
                    || !string.Equals(RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)), cast.InstanceKey, StringComparison.OrdinalIgnoreCase)
                    || !TryAuthorizePvpUseTarget(source, target, cast.ActionId, out reason))
                {
                    Debug.LogError($"[PVP-SKILL] sequence={cast.Sequence} tick={simulationTick} result=impact-rejected reason={reason ?? "target-unavailable"}");
                    continue;
                }
                ApplyPvpSkillCast(source, target, cast, simulationTick);
            }
        }

        private void ApplyPvpSkillCast(RRConnection source, RRConnection primaryTarget, PendingPvpSkillCast cast, uint simulationTick)
        {
            var targets = new List<RRConnection> { primaryTarget };
            if (cast.Spell.IsAoE)
            {
                int radiusF32 = cast.Spell.ResolveAoERadiusF32(cast.SkillLevel);
                int maxTargets = cast.Spell.ResolveNumTargets(cast.SkillLevel);
                ResolvePvpTargetPosition(primaryTarget, out int centerFixedX, out int centerFixedY, out _);
                List<RRConnection> eligible = GetConnectionInsertionOrderSnapshot()
                    .Where(candidate => candidate != null
                        && candidate.ConnId != primaryTarget.ConnId
                        && TryAuthorizePvpUseTarget(source, candidate, cast.ActionId, out _))
                    .Select(candidate => new
                    {
                        Connection = candidate,
                        Distance = ResolvePvpDistanceF32(candidate, centerFixedX, centerFixedY)
                    })
                    .Where(candidate => radiusF32 <= 0 || candidate.Distance <= radiusF32)
                    .OrderBy(candidate => candidate.Distance)
                    .ThenBy(candidate => candidate.Connection.Avatar?.Id ?? int.MaxValue)
                    .Select(candidate => candidate.Connection)
                    .ToList();
                foreach (RRConnection candidate in eligible)
                {
                    if (targets.Count >= maxTargets)
                        break;
                    targets.Add(candidate);
                }
            }
            else if (cast.Spell.IsChainSpell)
            {
                RRConnection current = primaryTarget;
                var used = new HashSet<int> { primaryTarget.ConnId };
                int chains = Math.Max(0, cast.Spell.NumChains);
                int chainRangeF32 = Math.Max(0, cast.Spell.ChainRange) * UnitMover.Fixed;
                for (int chainIndex = 0; chainIndex < chains; chainIndex++)
                {
                    ResolvePvpTargetPosition(current, out int centerFixedX, out int centerFixedY, out _);
                    RRConnection next = GetConnectionInsertionOrderSnapshot()
                        .Where(candidate => candidate != null && !used.Contains(candidate.ConnId) && TryAuthorizePvpUseTarget(source, candidate, cast.ActionId, out _))
                        .Select(candidate => new
                        {
                            Connection = candidate,
                            Distance = ResolvePvpDistanceF32(candidate, centerFixedX, centerFixedY)
                        })
                        .Where(candidate => chainRangeF32 <= 0 || candidate.Distance <= chainRangeF32)
                        .OrderBy(candidate => candidate.Distance)
                        .ThenBy(candidate => candidate.Connection.Avatar?.Id ?? int.MaxValue)
                        .Select(candidate => candidate.Connection)
                        .FirstOrDefault();
                    if (next == null)
                        break;
                    targets.Add(next);
                    used.Add(next.ConnId);
                    current = next;
                }
            }
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                ApplyPvpSkillToTarget(source, targets[targetIndex], cast.Spell, cast.SkillLevel, targetIndex > 0, simulationTick, cast.Sequence);
        }

        private static int ResolvePvpDistanceF32(RRConnection candidate, int centerFixedX, int centerFixedY)
        {
            ResolvePvpTargetPosition(candidate, out int fixedX, out int fixedY, out _);
            return UnitMover.IntSqrt(
                (long)(fixedX - centerFixedX) * (fixedX - centerFixedX)
                + (long)(fixedY - centerFixedY) * (fixedY - centerFixedY));
        }

        private void ApplyPvpSkillToTarget(RRConnection source, RRConnection target, SpellData spell, int skillLevel, bool secondaryTarget, uint simulationTick, long sequence)
        {
            CombatPlayer attacker = source.Avatar != null ? CombatRuntime.Instance.GetPlayer((uint)source.Avatar.Id) : null;
            CombatPlayer defender = target.Avatar != null ? CombatRuntime.Instance.GetPlayer((uint)target.Avatar.Id) : null;
            if (attacker?.PlayerState == null || defender?.PlayerState == null)
                return;
            MersenneTwister rng = CombatRuntime.Instance.GetRoomRngForInstance(RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)));
            if (rng == null)
                return;
            uint oldHPWire = defender.PlayerState.CurrentHPWire;
            uint totalAppliedWire = 0;
            string tag = $"PVP-SKILL:{spell.SkillId}:seq={sequence}";
            if (spell.HasImmediateWeaponDamageEffect)
                totalAppliedWire = SaturatingWireAdd(totalAppliedWire, ApplyPvpImmediateWeaponSkillDamage(attacker, defender, spell, skillLevel, rng, tag));
            if (defender.PlayerState.CurrentHPWire > 0 && spell.HasDirectDamageEffect)
            {
                SpellAttackResult spellDamage = DamageResolver.ProcessSpellAttack(
                    rng,
                    attacker.PlayerState.Level,
                    attacker.PlayerState.ClientSpellIntellect,
                    attacker.PlayerState.ClientSpellAgility,
                    attacker.PlayerState.ClientSpellStrength,
                    attacker.PlayerState.WeaponDamageF32,
                    attacker.PlayerState.WeaponDamageVolatilityF32,
                    spell,
                    null,
                    skillLevel,
                    secondaryTarget,
                    DamageResolver.ResolveCriticalDamagePercent(attacker.PlayerState),
                    attacker.PlayerState,
                    DamageResolver.ResolveSpellCriticalThreshold(attacker.PlayerState, null, spell),
                    false);
                if (spellDamage.Type != AttackResultType.Miss && spellDamage.DamageF32 > 0)
                {
                    CombatRuntime.PvpDamageResolution damage = CombatRuntime.Instance.ApplyPvpDamageQueryWire(
                        attacker,
                        defender,
                        (uint)spellDamage.DamageF32,
                        100,
                        spellDamage.DamageTypeId,
                        3,
                        rng,
                        tag,
                        true,
                        false);
                    totalAppliedWire = SaturatingWireAdd(totalAppliedWire, damage.AppliedDamageWire);
                }
            }
            if (defender.PlayerState.CurrentHPWire > 0 && spell.ModifierEffects != null)
            {
                foreach (SpellModifierData modifier in spell.ModifierEffects)
                    CombatRuntime.Instance.ApplyPvpAuthoredModifier(attacker, defender, spell, modifier, skillLevel, tag);
            }
            if (defender.PlayerState.CurrentHPWire > 0 && spell.HasDeferredProjectileModifierDamage)
                AttachPvpDamageModifier(source, target, spell, skillLevel, simulationTick);
            uint newHPWire = defender.PlayerState.CurrentHPWire;
            bool damaged = totalAppliedWire > 0;
            if (damaged)
                HandlePlayerDamageResolved(null, defender, true, newHPWire, tag);
            Debug.LogError($"[PVP-SKILL-IMPACT] source={source.LoginName} target={target.LoginName} spell={spell.SkillId} level={skillLevel} secondary={secondaryTarget} appliedWire={totalAppliedWire} hp={oldHPWire}->{newHPWire}/{defender.PlayerState.MaxHPWire} modifiers={spell.ModifierEffects?.Count ?? 0} dot={spell.HasDeferredProjectileModifierDamage} rngPos={rng.CallsSinceReseed} tick={simulationTick}");
            if (damaged && newHPWire == 0)
                HandlePvpPlayerDeath(source, target, simulationTick, tag);
        }

        private uint ApplyPvpImmediateWeaponSkillDamage(CombatPlayer attacker, CombatPlayer defender, SpellData spell, int skillLevel, MersenneTwister rng, string source)
        {
            PlayerState state = attacker.PlayerState;
            int arMod = ResolveSpellEffectPercent(spell.ARModMin, spell.ARModMax, spell.ARModInc, skillLevel, 100);
            int damageModRaw = ResolveSpellEffectRawMod(spell.WeaponEffectDamageModMin, spell.WeaponEffectDamageModMax, spell.WeaponEffectDamageModInc, skillLevel);
            int attackRating = (int)Math.Clamp((long)DamageResolver.ResolveAvatarAttackRating(state) * Math.Max(0, 100 + arMod) / 100L, 0L, 0xFFFFL);
            int remapDamageMod = DamageResolver.ResolveDamageMod(state, damageModRaw);
            bool ranged = DamageResolver.IsRangedWeapon(state);
            var input = new WeaponDamageInput
            {
                Rng = rng,
                Source = source + ":SpellWeaponDamageEffect",
                AttackerEntityId = attacker.EntityId,
                DefenderEntityId = defender.EntityId,
                AttackerLevel = Math.Max(0, state.Level),
                DefenderLevel = Math.Max(0, defender.PlayerState.Level),
                AttackRating = attackRating,
                DefenseRating = PvpBalance.RemapDefenseRating(
                    CombatRuntime.Instance.ResolveAvatarDefenseRating(defender.PlayerState, ranged),
                    state.Level,
                    defender.PlayerState.Level),
                BlockChance = CombatRuntime.Instance.ResolveAvatarBlockChance(defender.PlayerState),
                DamageLevel = DamageResolver.ResolveWeaponDamageLevel(state),
                DamageBonus = DamageResolver.ResolveWeaponDamageBonus(state),
                DamageMod = 100,
                WeaponClassId = DamageResolver.ResolveWeaponClassId(state),
                DamageTypeId = DamageResolver.ResolveDamageTypeId(state),
                WeaponDamageF32 = DamageResolver.GetWeaponBaseDamageF32(state),
                WeaponVolatilityF32 = DamageResolver.GetWeaponVolatilityF32(state),
                CritThreshold = DamageResolver.ResolveCriticalThreshold(state, defender.PlayerState.Level),
                CritDamagePercent = DamageResolver.ResolveCriticalDamagePercent(state),
                AttackerState = state,
                IncludeWeaponDamageAdds = true
            };
            WeaponDamageResult result = DamageResolver.ResolveWeaponDamage(input);
            if (!result.IsHit || result.IsBlocked || result.DamageWire == 0)
                return 0;
            CombatRuntime.PvpDamageResolution damage = CombatRuntime.Instance.ApplyPvpDamageQueryWire(
                attacker,
                defender,
                result.DamageWire,
                remapDamageMod,
                result.DamageTypeId,
                ranged ? (byte)2 : (byte)1,
                rng,
                input.Source,
                true,
                true);
            uint applied = damage.AppliedDamageWire;
            if (!damage.Died && result.DamageAdds != null)
            {
                foreach (WeaponDamageEvent add in result.DamageAdds)
                {
                    CombatRuntime.PvpDamageResolution addDamage = CombatRuntime.Instance.ApplyPvpDamageQueryWire(
                        attacker,
                        defender,
                        add.DamageWire,
                        100,
                        add.DamageTypeId,
                        3,
                        rng,
                        input.Source + ":ADD",
                        true,
                        false);
                    applied = SaturatingWireAdd(applied, addDamage.AppliedDamageWire);
                    if (addDamage.Died)
                        break;
                }
            }
            return applied;
        }

        private void AttachPvpDamageModifier(RRConnection source, RRConnection target, SpellData spell, int skillLevel, uint simulationTick)
        {
            ushort frequencyTicks = ResolvePvpEffectTicks(spell.ProjectileModifierFrequencyF32);
            ushort durationTicks = ResolvePvpEffectTicks(spell.ProjectileModifierDurationF32);
            if (frequencyTicks == 0 || durationTicks == 0)
                return;
            _pendingPvpDamageModifiers.RemoveAll(modifier => modifier.SourceConnId == source.ConnId
                && modifier.TargetConnId == target.ConnId
                && string.Equals(modifier.Spell?.SkillId, spell.SkillId, StringComparison.OrdinalIgnoreCase));
            var pending = new PendingPvpDamageModifier
            {
                Sequence = ++_nextPvpDamageModifierSequence,
                SourceConnId = source.ConnId,
                TargetConnId = target.ConnId,
                InstanceKey = RoomRuntime.NormalizeInstanceKey(ResolveConnectionInstanceKey(source)),
                Spell = spell,
                SkillLevel = skillLevel,
                FrequencyTicks = frequencyTicks,
                NextTick = unchecked(simulationTick + frequencyTicks),
                EndTick = unchecked(simulationTick + durationTicks)
            };
            _pendingPvpDamageModifiers.Add(pending);
            Debug.LogError($"[PVP-DOT] source={source.LoginName} target={target.LoginName} spell={spell.SkillId} sequence={pending.Sequence} frequencyTicks={frequencyTicks} durationTicks={durationTicks} nextTick={pending.NextTick} endTick={pending.EndTick}");
        }

        private static ushort ResolvePvpEffectTicks(int fixedSeconds)
        {
            long ticks = ((long)Math.Max(0, fixedSeconds) * SimulationClock.TicksPerSecond + 0x100L) >> 8;
            return ticks <= 0 ? (ushort)0 : ticks >= ushort.MaxValue ? ushort.MaxValue : (ushort)ticks;
        }

        private void ProcessPendingPvpDamageModifiers(uint sourceEntityId, uint simulationTick)
        {
            RRConnection entityOwner = FindConnectionByAvatarEntityId(sourceEntityId);
            if (entityOwner == null)
                return;
            for (int modifierIndex = 0; modifierIndex < _pendingPvpDamageModifiers.Count;)
            {
                PendingPvpDamageModifier modifier = _pendingPvpDamageModifiers[modifierIndex];
                if (modifier.SourceConnId != entityOwner.ConnId)
                {
                    modifierIndex++;
                    continue;
                }
                if (unchecked((int)(simulationTick - modifier.EndTick)) >= 0)
                {
                    _pendingPvpDamageModifiers.RemoveAt(modifierIndex);
                    continue;
                }
                if (unchecked((int)(simulationTick - modifier.NextTick)) < 0)
                {
                    modifierIndex++;
                    continue;
                }
                modifier.NextTick = unchecked(modifier.NextTick + modifier.FrequencyTicks);
                if (!_connections.TryGetValue(modifier.SourceConnId, out RRConnection source)
                    || !_connections.TryGetValue(modifier.TargetConnId, out RRConnection target)
                    || !TryAuthorizePvpUseTarget(source, target, 10, out _))
                {
                    _pendingPvpDamageModifiers.RemoveAt(modifierIndex);
                    continue;
                }
                ApplyPvpDamageModifierTick(source, target, modifier, simulationTick);
                int currentModifierIndex = _pendingPvpDamageModifiers.FindIndex(candidate => candidate.Sequence == modifier.Sequence);
                if (currentModifierIndex >= 0)
                {
                    PlayerState targetState = GetPlayerState(target.ConnId.ToString());
                    if (targetState == null || targetState.CurrentHPWire == 0)
                        _pendingPvpDamageModifiers.RemoveAt(currentModifierIndex);
                }
                int nextModifierIndex = _pendingPvpDamageModifiers.FindIndex(candidate => candidate.Sequence > modifier.Sequence);
                modifierIndex = nextModifierIndex >= 0 ? nextModifierIndex : _pendingPvpDamageModifiers.Count;
            }
        }

        private void ApplyPvpDamageModifierTick(RRConnection source, RRConnection target, PendingPvpDamageModifier modifier, uint simulationTick)
        {
            CombatPlayer attacker = source.Avatar != null ? CombatRuntime.Instance.GetPlayer((uint)source.Avatar.Id) : null;
            CombatPlayer defender = target.Avatar != null ? CombatRuntime.Instance.GetPlayer((uint)target.Avatar.Id) : null;
            MersenneTwister rng = CombatRuntime.Instance.GetRoomRngForInstance(modifier.InstanceKey);
            if (attacker?.PlayerState == null || defender?.PlayerState == null || rng == null)
                return;
            SpellAttackResult result = DamageResolver.ProcessProjectileModifierTick(
                rng,
                attacker.PlayerState.Level,
                attacker.PlayerState.ClientSpellIntellect,
                attacker.PlayerState.ClientSpellAgility,
                attacker.PlayerState.ClientSpellStrength,
                attacker.PlayerState.WeaponDamageF32,
                attacker.PlayerState.WeaponDamageVolatilityF32,
                modifier.Spell,
                null,
                modifier.SkillLevel,
                DamageResolver.ResolveCriticalDamagePercent(attacker.PlayerState),
                attacker.PlayerState,
                DamageResolver.ResolveSpellCriticalThreshold(attacker.PlayerState, null, modifier.Spell),
                false);
            if (result.Type == AttackResultType.Miss || result.DamageF32 <= 0)
                return;
            string tag = $"PVP-DOT:{modifier.Spell.SkillId}:seq={modifier.Sequence}";
            CombatRuntime.PvpDamageResolution damage = CombatRuntime.Instance.ApplyPvpDamageQueryWire(
                attacker,
                defender,
                (uint)result.DamageF32,
                100,
                result.DamageTypeId,
                3,
                rng,
                tag,
                true,
                false);
            if (damage.AppliedDamageWire > 0)
                HandlePlayerDamageResolved(null, defender, true, damage.NewHPWire, tag);
            Debug.LogError($"[PVP-DOT-TICK] source={source.LoginName} target={target.LoginName} spell={modifier.Spell.SkillId} sequence={modifier.Sequence} rawWire={result.DamageF32} appliedWire={damage.AppliedDamageWire} hp={damage.OldHPWire}->{damage.NewHPWire} tick={simulationTick}");
            if (damage.Died)
                HandlePvpPlayerDeath(source, target, simulationTick, tag);
        }

        private void HandlePvpPlayerDeath(RRConnection killer, RRConnection victim, uint simulationTick, string source)
        {
            if (killer == null || victim == null || string.IsNullOrWhiteSpace(killer.LoginName) || string.IsNullOrWhiteSpace(victim.LoginName))
                return;
            ClearPvpConnectionRuntime(victim, source ?? "PVP-DEATH", false);
            var duelResult = _duelRuntime.ReportKill(killer.LoginName, victim.LoginName, simulationTick);
            if (duelResult.duel != null)
            {
                SendDuelEndPackets(duelResult.winner, duelResult.loser, duelResult.duel);
                Debug.LogError($"[PVP-DEATH] killer={killer.LoginName} victim={victim.LoginName} result=duel-ended tick={simulationTick}");
                return;
            }
            PVPMatchmaking.Match match = PVPMatchmaking.Instance.GetMatchForPlayer(killer.LoginName);
            if (match == null || !match.ParticipantLogins.Contains(victim.LoginName, StringComparer.OrdinalIgnoreCase))
                return;
            bool immediateEnd = match.KeepScore && PVPMatchmaking.Instance.RecordKill(killer.LoginName, victim.LoginName);
            if (!immediateEnd && match.KeepScore && !match.AllowRespawn)
            {
                List<string> living = match.ParticipantLogins
                    .Where(login =>
                    {
                        RRConnection participant = FindConnectionByLogin(login);
                        if (participant?.Avatar == null)
                            return false;
                        CombatPlayer player = CombatRuntime.Instance.GetPlayer((uint)participant.Avatar.Id);
                        return player?.PlayerState != null && player.IsAlive && player.PlayerState.CurrentHPWire > 0;
                    })
                    .ToList();
                if (match.FreeForAll)
                {
                    if (living.Count <= 1)
                    {
                        match.SetIndividualWinner(living.FirstOrDefault() ?? killer.LoginName);
                        immediateEnd = true;
                    }
                }
                else
                {
                    bool victimRed = match.IsRed(victim.LoginName);
                    bool victimTeamAlive = living.Any(login => match.IsRed(login) == victimRed);
                    if (!victimTeamAlive)
                    {
                        string representative = living.FirstOrDefault(login => match.IsRed(login) != victimRed) ?? killer.LoginName;
                        match.SetWinningTeam(!victimRed, representative);
                        immediateEnd = true;
                    }
                }
            }
            if (immediateEnd)
            {
                PVPMatchmaking.Match ended = PVPMatchmaking.Instance.EndMatch(match.MatchId, "combat elimination", simulationTick);
                if (ended != null)
                    FinalizeMatchResults(ended);
            }
            Debug.LogError($"[PVP-DEATH] killer={killer.LoginName} victim={victim.LoginName} match={match.MatchId} allowRespawn={match.AllowRespawn} keepScore={match.KeepScore} ended={immediateEnd} tick={simulationTick}");
        }

        private void ClearPvpUseState(RRConnection conn, string source, bool preserveProjectiles)
        {
            if (conn?.Avatar == null || conn.Avatar.Id <= 0)
                return;
            uint entityId = (uint)conn.Avatar.Id;
            if (_pvpWeaponUses.Remove(entityId))
                Debug.LogError($"[PVP-RUNTIME-CLEAR] player={conn.LoginName} entity={entityId} source={source ?? "unknown"} weaponUse=True preserveProjectiles={preserveProjectiles}");
        }

        private void ClearPvpConnectionRuntime(RRConnection conn, string source, bool removeSourceProjectiles)
        {
            if (conn == null)
                return;
            ClearPvpUseState(conn, source, !removeSourceProjectiles);
            _pendingPvpSkillCasts.RemoveAll(cast => cast.SourceConnId == conn.ConnId || cast.TargetConnId == conn.ConnId);
            _pendingPvpDamageModifiers.RemoveAll(modifier => modifier.SourceConnId == conn.ConnId || modifier.TargetConnId == conn.ConnId);
            if (removeSourceProjectiles)
            {
                uint avatarEntityId = 0;
                if (conn.Avatar != null && conn.Avatar.Id > 0)
                    avatarEntityId = (uint)conn.Avatar.Id;
                if (avatarEntityId != 0)
                {
                    _pvpWeaponUses.Remove(avatarEntityId);
                    _pvpWeaponReadyTicks.Remove(avatarEntityId);
                    _pvpWeaponAnimationIndices.Remove(avatarEntityId);
                }
                if (_playerAvatarEntityId.TryGetValue(conn.ConnId.ToString(), out uint mappedEntityId)
                    && mappedEntityId != 0
                    && mappedEntityId != avatarEntityId)
                {
                    _pvpWeaponUses.Remove(mappedEntityId);
                    _pvpWeaponReadyTicks.Remove(mappedEntityId);
                    _pvpWeaponAnimationIndices.Remove(mappedEntityId);
                }
                PvpWeaponProjectileState[] projectiles = _pvpWeaponProjectiles.Values.ToArray();
                for (int projectileIndex = 0; projectileIndex < projectiles.Length; projectileIndex++)
                {
                    PvpWeaponProjectileState projectile = projectiles[projectileIndex];
                    if (projectile.SourceConnId == conn.ConnId)
                        RemovePvpWeaponProjectile(projectile.Sequence, projectile.InstanceKey);
                }
            }
        }
    }
}
