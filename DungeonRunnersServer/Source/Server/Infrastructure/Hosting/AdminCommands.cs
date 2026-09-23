using System;
using System.Text;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Networking;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Database;
using System.Linq;

namespace DungeonRunners.Combat
{
    public class AdminCommands
    {
        private const uint INVINCIBLE_HP_WIRE = 5000 * 256;

        private static readonly HashSet<string> KnownAdminCommands = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Invulnerable", "NotInvulnerable",
            "Invincible", "NotInvincible",
            "Invisible", "NotInvisible",
            "SuperSpeed", "NoSuperSpeed",
            "LevelSelf",
            "GiveGoldSelf",
            "Location",
            "RemoveAllGold"
        };

        private static readonly string[] TransferCommands = new[]
        {
            "TransferSelf thehub start",
            "TransferSelf town start",
            "TransferSelf pvp_town start"
        };

        private static readonly Dictionary<string, string> TransferZoneMap = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            { "TransferSelf thehub start", "thehub" },
            { "TransferSelf town start", "town" },
            { "TransferSelf pvp_town start", "pvp_start" }
        };

        private readonly Dictionary<int, HashSet<string>> _activeModsByConn
             = new Dictionary<int, HashSet<string>>();

        private readonly Dictionary<int, bool> _regenInProgress = new Dictionary<int, bool>();
        private readonly Dictionary<int, DateTime> _lastLevelUpTime = new Dictionary<int, DateTime>();
        private const double LEVEL_COOLDOWN_MS = 2000;

        private const uint FirstAdminModifierId = 90000;
        private const uint LastAdminModifierId = 0x000FFFFF;
        private uint _nextModInstanceId = FirstAdminModifierId;

        private bool TryNextAdminModifierId(out uint modifierId)
        {
            modifierId = 0;
            if (_nextModInstanceId < FirstAdminModifierId || _nextModInstanceId > LastAdminModifierId)
                return false;
            modifierId = _nextModInstanceId++;
            return true;
        }

        private Action<RRConnection, string> _onZoneChange;
        private Func<RRConnection, SavedCharacter> _getSavedCharacter;
        private Func<RRConnection, SavedCharacter, string, bool> _saveCharacter;
        private Action<RRConnection, PlayerState, int, int> _onLevelUp;
        private Action<string, string, uint> _onModifierAdded;
        private Action<string, uint> _onModifierRemoved;
        private Action<RRConnection, uint> _onCompleteQuest;
        private Action<RRConnection, LEWriter> _writePlayerEntitySynch;

        public void SetServerCallbacks(
            Action<RRConnection, string> onZoneChange,
            Func<RRConnection, SavedCharacter> getSavedCharacter,
            Func<RRConnection, SavedCharacter, string, bool> saveCharacter,
            Action<RRConnection, PlayerState, int, int> onLevelUp = null,
            Action<string, string, uint> onModifierAdded = null,
            Action<string, uint> onModifierRemoved = null,
            Action<RRConnection, uint> onCompleteQuest = null,
            Action<RRConnection, LEWriter> writePlayerEntitySynch = null)
        {
            _onZoneChange = onZoneChange;
            _getSavedCharacter = getSavedCharacter;
            _saveCharacter = saveCharacter;
            _onLevelUp = onLevelUp;
            _onModifierAdded = onModifierAdded;
            _onModifierRemoved = onModifierRemoved;
            _onCompleteQuest = onCompleteQuest;
            _writePlayerEntitySynch = writePlayerEntitySynch;
        }

        public bool TryExecute(
            RRConnection conn,
            byte messageType,
            byte[] data,
            PlayerState playerState,
            Action<RRConnection, string> onStatusMessage,
            Action<RRConnection, byte[]> sendPacket)
        {
            if (data == null || data.Length == 0) return false;

            string raw = Encoding.ASCII.GetString(data).TrimEnd('\0');
            int cqIdx = raw.IndexOf("CompleteQuest ", StringComparison.OrdinalIgnoreCase);
            if (cqIdx >= 0)
            {
                string tail = raw.Substring(cqIdx + "CompleteQuest ".Length).TrimEnd('\0', ' ');
                int digitEnd = 0;
                while (digitEnd < tail.Length && char.IsDigit(tail[digitEnd])) digitEnd++;
                if (digitEnd > 0 && uint.TryParse(tail.Substring(0, digitEnd), out uint instanceId))
                {
                    Debug.LogError($"[ADMIN] CompleteQuest instanceId={instanceId} from conn={conn.ConnId}");
                    if (_onCompleteQuest != null)
                    {
                        _onCompleteQuest(conn, instanceId);
                        onStatusMessage?.Invoke(conn, $"Admin: Completing quest {instanceId}");
                    }
                    else
                    {
                        Debug.LogError("[ADMIN] CompleteQuest callback not wired up");
                        onStatusMessage?.Invoke(conn, "Admin: CompleteQuest not wired");
                    }
                    return true;
                }
                Debug.LogError($"[ADMIN] CompleteQuest could not parse instanceId from tail: \"{tail}\"");
            }

            string command = TryExtractCommandString(data);
            if (string.IsNullOrEmpty(command)) return false;

            Debug.LogError($"[ADMIN] Received: \"{command}\" from conn={conn.ConnId}");
            Execute(conn, command, playerState, onStatusMessage, sendPacket);
            return true;
        }

        private string TryExtractCommandString(byte[] data)
        {
            string full = ExtractString(data, 0);
            if (!string.IsNullOrEmpty(full) && KnownAdminCommands.Contains(full))
                return full;

            if (data.Length > 1)
            {
                string commandAtOffset = ExtractString(data, 1);
                if (!string.IsNullOrEmpty(commandAtOffset) && KnownAdminCommands.Contains(commandAtOffset))
                    return commandAtOffset;
            }

            if (data.Length > 2)
            {
                string commandAtOffset = ExtractString(data, 2);
                if (!string.IsNullOrEmpty(commandAtOffset) && KnownAdminCommands.Contains(commandAtOffset))
                    return commandAtOffset;
            }

            string entire = Encoding.ASCII.GetString(data).TrimEnd('\0');
            foreach (var transfer in TransferCommands)
            {
                if (entire.IndexOf(transfer, StringComparison.OrdinalIgnoreCase) >= 0)
                    return transfer;
            }


            Debug.LogError($"[ADMIN] Channel 9 not admin command: {BitConverter.ToString(data)}");
            return null;
        }

        private string ExtractString(byte[] data, int offset)
        {
            if (offset >= data.Length) return null;
            int end = offset;
            while (end < data.Length && data[end] != 0) end++;
            if (end == offset) return null;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private void Execute(
            RRConnection conn,
            string command,
            PlayerState playerState,
            Action<RRConnection, string> onStatusMessage,
            Action<RRConnection, byte[]> sendPacket)
        {
            if (!_activeModsByConn.ContainsKey(conn.ConnId))
                _activeModsByConn[conn.ConnId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (TransferZoneMap.TryGetValue(command, out string zoneName))
            {
                if (_onZoneChange != null)
                {
                    onStatusMessage?.Invoke(conn, $"Admin: Transferring to {zoneName}");
                    _onZoneChange(conn, zoneName);
                }
                else
                {
                    onStatusMessage?.Invoke(conn, "Admin: Zone transfer not available");
                }
                return;
            }

            switch (command)
            {
                case "Invulnerable":
                    playerState.IsDamageImmune = true;
                    SendAddModifier(conn, "Invulnerable", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: Invulnerable ON");
                    break;
                case "NotInvulnerable":
                    SendRemove(conn, "Invulnerable", playerState, sendPacket);
                    if (!HasActiveMod(conn.ConnId, "Invincible") && !HasActiveMod(conn.ConnId, "Invisible"))
                        playerState.IsDamageImmune = false;
                    onStatusMessage?.Invoke(conn, "Admin: Invulnerable OFF");
                    break;

                case "Invincible":
                    playerState.IsDamageImmune = true;
                    playerState.ApplyMaxHPModifier(INVINCIBLE_HP_WIRE);
                    _regenInProgress[conn.ConnId] = true;
                    SendAddModifier(conn, "Invincible", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: Invincible ON");
                    break;
                case "NotInvincible":
                    if (_regenInProgress.ContainsKey(conn.ConnId) && _regenInProgress[conn.ConnId])
                    {
                        onStatusMessage?.Invoke(conn, "Admin: Wait for EntitySynchInfo HP to complete");
                        break;
                    }
                    SendRemove(conn, "Invincible", playerState, sendPacket);
                    playerState.RemoveMaxHPModifier(INVINCIBLE_HP_WIRE);
                    if (!HasActiveMod(conn.ConnId, "Invulnerable") && !HasActiveMod(conn.ConnId, "Invisible"))
                        playerState.IsDamageImmune = false;
                    onStatusMessage?.Invoke(conn, "Admin: Invincible OFF");
                    break;

                case "Invisible":
                    playerState.IsDamageImmune = true;
                    playerState.IsInvisible = true;
                    SendAddModifier(conn, "Invisible", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: Invisible ON");
                    break;
                case "NotInvisible":
                    SendRemove(conn, "Invisible", playerState, sendPacket);
                    if (!HasActiveMod(conn.ConnId, "Invulnerable") && !HasActiveMod(conn.ConnId, "Invincible"))
                        playerState.IsDamageImmune = false;
                    playerState.IsInvisible = false;
                    onStatusMessage?.Invoke(conn, "Admin: Invisible OFF");
                    break;

                case "SuperSpeed":
                    SendAddModifier(conn, "SuperSpeed", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: SuperSpeed ON");
                    break;
                case "NoSuperSpeed":
                    SendRemove(conn, "SuperSpeed", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: SuperSpeed OFF");
                    break;

                case "LevelSelf":
                    {
                        if (_lastLevelUpTime.TryGetValue(conn.ConnId, out DateTime last)
                            && (DateTime.UtcNow - last).TotalMilliseconds < LEVEL_COOLDOWN_MS)
                        {
                            onStatusMessage?.Invoke(conn, "Admin: Wait a moment between levels");
                            break;
                        }

                        var savedChar = _getSavedCharacter?.Invoke(conn);
                        if (savedChar != null && playerState.Level < 100)
                        {
                            _lastLevelUpTime[conn.ConnId] = DateTime.UtcNow;
                            int oldLevel = playerState.Level;
                            int newLevel = Math.Min(100, oldLevel + 1);
                            byte oldPersistedLevel = savedChar.level;
                            savedChar.level = SavedCharacterLevel.ResolvePersistedLevel(newLevel);
                            if (_saveCharacter == null || !_saveCharacter(conn, savedChar, "admin-level-self"))
                            {
                                savedChar.level = oldPersistedLevel;
                                onStatusMessage?.Invoke(conn, "Admin: Save failed");
                                break;
                            }
                            playerState.InitializeStats(savedChar.className, newLevel, preserveAttributeModifiers: true);
                            playerState.Experience = savedChar.experience;
                            onStatusMessage?.Invoke(conn, $"Level: {newLevel}");
                            Debug.LogError($"[ADMIN] LevelSelf: {savedChar.name} persistedLevel={savedChar.level} clientLevel={newLevel}");

                            _onLevelUp?.Invoke(conn, playerState, oldLevel, newLevel);
                        }
                        else
                        {
                            onStatusMessage?.Invoke(conn, "Admin: Already max level");
                        }
                        break;
                    }

                case "GiveGoldSelf":
                    {
                        var savedChar = _getSavedCharacter?.Invoke(conn);
                        if (savedChar != null)
                        {
                            uint addAmount = 1000000;
                            if (savedChar.gold > (uint)int.MaxValue - addAmount)
                            {
                                onStatusMessage?.Invoke(conn, "Admin: Gold limit reached");
                                break;
                            }
                            savedChar.gold += addAmount;
                            if (_saveCharacter == null || !_saveCharacter(conn, savedChar, "admin-give-gold"))
                            {
                                savedChar.gold -= addAmount;
                                onStatusMessage?.Invoke(conn, "Admin: Save failed");
                                break;
                            }
                            onStatusMessage?.Invoke(conn, $"Admin: +1,000,000 gold (total: {savedChar.gold})");
                            Debug.LogError($"[ADMIN] GiveGoldSelf: {savedChar.name} gold={savedChar.gold}");

                            if (conn.UnitContainerId != 0)
                            {
                                var goldWriter = new LEWriter();
                                goldWriter.WriteByte(0x07);
                                goldWriter.WriteByte(0x35);
                                goldWriter.WriteUInt16(conn.UnitContainerId);
                                goldWriter.WriteByte(0x20);
                                goldWriter.WriteUInt32(addAmount);
                                goldWriter.WriteByte(0x00);
                                goldWriter.WriteUInt32(0x00000000);
                                goldWriter.WriteByte(0x01);
                                _writePlayerEntitySynch(conn, goldWriter);
                                goldWriter.WriteByte(0x06);
                                sendPacket?.Invoke(conn, goldWriter.ToArray());
                                Debug.LogError($"[ADMIN] Sent gold update +{addAmount} to UC 0x{conn.UnitContainerId:X4}");
                            }
                        }
                        break;
                    }

                case "RemoveAllGold":
                    {
                        var savedChar = _getSavedCharacter?.Invoke(conn);
                        if (savedChar != null)
                        {
                            uint oldGold = savedChar.gold;
                            savedChar.gold = 0;
                            if (_saveCharacter == null || !_saveCharacter(conn, savedChar, "admin-remove-gold"))
                            {
                                savedChar.gold = oldGold;
                                onStatusMessage?.Invoke(conn, "Admin: Save failed");
                                break;
                            }
                            onStatusMessage?.Invoke(conn, "Admin: All gold removed");
                            Debug.LogError($"[ADMIN] RemoveAllGold: {savedChar.name} gold=0");

                            if (conn.UnitContainerId != 0 && oldGold > 0)
                            {
                                var goldWriter = new LEWriter();
                                goldWriter.WriteByte(0x07);
                                goldWriter.WriteByte(0x35);
                                goldWriter.WriteUInt16(conn.UnitContainerId);
                                goldWriter.WriteByte(0x21);
                                goldWriter.WriteUInt32(oldGold);
                                goldWriter.WriteByte(0x00);
                                _writePlayerEntitySynch(conn, goldWriter);
                                goldWriter.WriteByte(0x06);
                                sendPacket?.Invoke(conn, goldWriter.ToArray());
                                Debug.LogError($"[ADMIN] Sent gold removal -{oldGold} to UC 0x{conn.UnitContainerId:X4}");
                            }
                        }
                        break;
                    }

                case "Location":
                    {
                        string locationMessage = $"PosFixed=({conn.PlayerPosFixedX}, {conn.PlayerPosFixedY}, {conn.PlayerPosFixedZ}) headingFixed={conn.PlayerHeadingFixed} zone={conn.CurrentZoneName ?? "?"}";
                        onStatusMessage?.Invoke(conn, "Admin: " + locationMessage);
                        Debug.LogError($"[LOC] {conn.LoginName ?? conn.ConnId.ToString()} {locationMessage}");
                    }
                    break;

                default:
                    Debug.LogError($"[ADMIN] Unhandled: \"{command}\"");
                    break;
            }
        }


        private readonly Dictionary<string, uint> _modInstanceIds = new Dictionary<string, uint>();

        private void SendAddModifier(RRConnection conn, string modName, PlayerState playerState, Action<RRConnection, byte[]> sendPacket)
        {
            var mods = _activeModsByConn[conn.ConnId];
            if (mods.Contains(modName)) return;
            string gcType = $"admin.Admin_{modName}_Mod";
            if (!TryNextAdminModifierId(out uint instanceId))
            {
                Debug.LogError($"[ADMIN] modifier add blocked reason=modifier-id-range-exhausted modifier={modName}");
                return;
            }
            mods.Add(modName);
            string key = $"{conn.ConnId}_{modName}";
            _modInstanceIds[key] = instanceId;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.ModifiersId);
            writer.WriteByte(0x00);
            writer.WriteByte(0xFF);
            writer.WriteCString(gcType);
            writer.WriteUInt32(instanceId);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0);
            _writePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            byte[] packet = writer.ToArray();
            Debug.LogError($"[ADMIN] ADD '{gcType}' instId={instanceId} packet={BitConverter.ToString(packet)}");
            sendPacket(conn, packet);
            _onModifierAdded?.Invoke(conn.LoginName, gcType, instanceId);
        }

        private void SendRemove(RRConnection conn, string modName, PlayerState playerState, Action<RRConnection, byte[]> sendPacket)
        {
            var mods = _activeModsByConn[conn.ConnId];
            if (!mods.Contains(modName)) return;
            string key = $"{conn.ConnId}_{modName}";
            if (!_modInstanceIds.TryGetValue(key, out uint instanceId)
                || instanceId < FirstAdminModifierId || instanceId > LastAdminModifierId)
            {
                Debug.LogError($"[ADMIN] REMOVE blocked modifier='{modName}' reason=missing-or-invalid-instance-id");
                return;
            }
            mods.Remove(modName);
            _modInstanceIds.Remove(key);
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.ModifiersId);
            writer.WriteByte(0x01);
            writer.WriteUInt32(instanceId);
            _writePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            byte[] packet = writer.ToArray();
            Debug.LogError($"[ADMIN] REMOVE '{modName}' instId={instanceId} packet={BitConverter.ToString(packet)}");
            sendPacket(conn, packet);
            _onModifierRemoved?.Invoke(conn.LoginName, instanceId);
        }

        public void ClearRegenFlag(int connId)
        {
            _regenInProgress[connId] = false;
        }
        public bool IsRegenActive(int connId)
        {
            return _regenInProgress.ContainsKey(connId) && _regenInProgress[connId];
        }
        public void OnPlayerDisconnected(int connId)
        {
            _activeModsByConn.Remove(connId);
            _regenInProgress.Remove(connId);
            _lastLevelUpTime.Remove(connId);
            string keyPrefix = connId + "_";
            foreach (string key in _modInstanceIds.Keys.Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList())
                _modInstanceIds.Remove(key);
        }

        public bool HasActiveMod(int connId, string modName)
        {
            return _activeModsByConn.TryGetValue(connId, out var mods) && mods.Contains(modName);
        }

    }
}
