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
    {
        private const byte CharacterManagerErrorNameAlreadyInUse = 2;
        private const byte CharacterManagerErrorOperationUnavailable = 6;
        private readonly List<(RRConnection Connection, string User)> _pendingInitialLoginAdmissions = new List<(RRConnection, string)>();

        private void HandleObeliskTeleport(RRConnection conn)
        {
            try
            {
                string connId = conn.ConnId.ToString();
                var playerState = QuestManager.Instance.GetPlayerState(connId);
                if (playerState == null) { Debug.LogError("[OBELISK] No player state"); return; }

                string currentZone = "";
                if (_zones.TryGetValue(conn.CurrentZoneId, out Zone cz))
                    currentZone = cz.name;

                var available = new List<(string id, int order, string zone)>();
                foreach (var cpId in playerState.UnlockedCheckpoints)
                {
                    var cp = AuthoredGameplayCatalog.Checkpoints.FirstOrDefault(c =>
                        c.id.Equals(cpId, StringComparison.OrdinalIgnoreCase));
                    if (cp == null) continue;
                    if (cp.zone.Equals(currentZone, StringComparison.OrdinalIgnoreCase)) continue;
                    available.Add((cpId, cp.order, cp.zone));
                }
                available.Sort((a, b) => a.order.CompareTo(b.order));

                if (available.Count == 0)
                {
                    Debug.LogError("[OBELISK] No available checkpoints");
                    return;
                }

                int checkpointIndex = conn.ObeliskClickIndex % available.Count;
                conn.ObeliskClickIndex = checkpointIndex + 1;

                var selected = available[checkpointIndex];
                Debug.LogError($"[OBELISK] Teleporting to '{selected.zone}' (checkpoint {selected.id}, index {checkpointIndex}/{available.Count})");

                var checkpoint = AuthoredGameplayCatalog.Checkpoints.FirstOrDefault(c =>
                    c.id.Equals(selected.id, StringComparison.OrdinalIgnoreCase));
                if (checkpoint == null) return;

                TryFindZoneByName(checkpoint.zone, out Zone destZone);
                if (destZone != null)
                {
                    conn.PendingSpawnFixedX = destZone.SpawnFixedX;
                    conn.PendingSpawnFixedY = destZone.SpawnFixedY;
                    conn.PendingSpawnFixedZ = destZone.SpawnFixedZ;
                }

                ChangeZone(conn, checkpoint.zone, "");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OBELISK] state=failed message='{ex.Message}'");
            }
        }

        private void HandleSavedPlaceTeleport(RRConnection conn)
        {
            try
            {
                if (conn.HasSavedTownPortal && !string.IsNullOrEmpty(conn.TownPortalZoneName))
                {
                    Debug.LogError($"[SAVED-PLACE] Teleporting to saved portal: {conn.TownPortalZoneName}");
                    ChangeZone(conn, conn.TownPortalZoneName, "");
                }
                else
                {
                    Debug.LogError("[SAVED-PLACE] No saved town portal - defaulting to town");
                    ChangeZone(conn, "town", "");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SAVED-PLACE] state=failed message='{ex.Message}'");
            }
        }




        public void SendSystemMessage(RRConnection conn, string message)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x06);
            writer.WriteByte(0x00);
            writer.WriteByte(0x0D);
            foreach (char c in message)
            {
                writer.WriteByte((byte)c);
            }
            writer.WriteByte(0x00);
            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
        }

        private void SendBossGatePopup(RRConnection conn)
        {
            if (conn == null || !conn.IsConnected || conn.QuestManagerId == 0) return;

            const uint popupInstanceId = 0xFFFFFF00u;
            const string popupText = "<font color=white effect=glow>The boss gate is open!</font>";

            var q02a2 = AuthoredGameplayCatalog.Quests.FirstOrDefault(q =>
                string.Equals(q.id, "world.dungeon00.quest.Q02_a2", StringComparison.OrdinalIgnoreCase));
            if (q02a2 == null)
            {
                Debug.LogError("[BOSS-POPUP] Q02_a2 quest not found in database; cannot send popup");
                return;
            }
            uint typeHash = q02a2.hash != 0 ? q02a2.hash : AuthoredGameplayCatalog.ComputeDJB2Hash(q02a2.id);

            var addWriter = new LEWriter();
            addWriter.WriteByte(0x07);
            addWriter.WriteByte(0x35);
            addWriter.WriteUInt16(conn.QuestManagerId);
            addWriter.WriteByte(0x01);
            addWriter.WriteByte(0x04);
            addWriter.WriteUInt32(typeHash);
            addWriter.WriteUInt32(popupInstanceId);
            addWriter.WriteByte(0x00);
            addWriter.WriteByte(0x01);
            addWriter.WriteByte(0x02);
            foreach (char popupChar in popupText) addWriter.WriteByte((byte)popupChar);
            addWriter.WriteByte(0x00);
            addWriter.WriteUInt16(0x0002);
            if (!WritePlayerEntitySynch(conn, addWriter)) return;
            addWriter.WriteByte(0x06);
            SendCompressedA(conn, 0x01, 0x0F, addWriter.ToArray());

            var progressWriter = new LEWriter();
            progressWriter.WriteByte(0x07);
            progressWriter.WriteByte(0x35);
            progressWriter.WriteUInt16(conn.QuestManagerId);
            progressWriter.WriteByte(0x03);
            progressWriter.WriteUInt32(popupInstanceId);
            progressWriter.WriteByte(0x01);
            progressWriter.WriteByte(0x01);
            progressWriter.WriteByte(0x02);
            foreach (char popupChar in popupText) progressWriter.WriteByte((byte)popupChar);
            progressWriter.WriteByte(0x00);
            progressWriter.WriteUInt16(0x0001);
            if (!WritePlayerEntitySynch(conn, progressWriter)) return;
            progressWriter.WriteByte(0x06);
            SendCompressedA(conn, 0x01, 0x0F, progressWriter.ToArray());

            var removeWriter = new LEWriter();
            removeWriter.WriteByte(0x07);
            removeWriter.WriteByte(0x35);
            removeWriter.WriteUInt16(conn.QuestManagerId);
            removeWriter.WriteByte(0x02);
            removeWriter.WriteUInt32(popupInstanceId);
            if (!WritePlayerEntitySynch(conn, removeWriter)) return;
            removeWriter.WriteByte(0x06);
            SendCompressedA(conn, 0x01, 0x0F, removeWriter.ToArray());

            Debug.LogError($"[BOSS-POPUP] sent Add+Progress+Remove popup sequence to {conn.LoginName} instance=0x{popupInstanceId:X8} hash=0x{typeHash:X8}");
        }

        public static string WrapChatColor(string message, string color, string effect)
        {
            if (string.IsNullOrEmpty(message)) return message;
            if (message.Contains("<font")) return message;

            bool hasColor = !string.IsNullOrEmpty(color);
            bool hasEffect = !string.IsNullOrEmpty(effect) && effect != "none";

            if (!hasColor && !hasEffect) return message;

            string attrs = "";
            if (hasColor) attrs += $" color={color}";
            if (hasEffect) attrs += $" effect={effect}";

            return $"<font{attrs}>{message}</font>";
        }

        private bool LoadAccountFlags(string loginName)
        {
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                {
                    using (var reader = Database.GameDatabase.ExecuteReader(db,
                        "SELECT is_member, is_banned, is_admin FROM accounts WHERE username = @name",
                        ("@name", loginName)))
                    {
                        if (reader.Read())
                        {
                            int isMember = reader.GetInt32(0);
                            int isBanned = reader.GetInt32(1);
                            int isAdmin = reader.GetInt32(2);

                            _playerIsFree[loginName] = (isMember == 0);
                            _playerIsAdmin[loginName] = (isAdmin != 0);

                            Debug.LogError($"[ACCOUNT] {loginName}: member={isMember} banned={isBanned} admin={isAdmin}");

                            if (isBanned != 0)
                            {
                                Debug.LogError($"[ACCOUNT]  {loginName} is BANNED - rejecting connection");
                                return false;
                            }
                        }
                    }

                    Database.GameDatabase.ExecuteNonQuery(db,
                        "UPDATE accounts SET last_login = datetime('now') WHERE username = @name",
                        ("@name", loginName));
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ACCOUNT] loadFlags login='{loginName}' state=failed message='{ex.Message}'");
            }
            return true;
        }

        public bool IsPlayerAdmin(string loginName)
        {
            if (_playerIsAdmin.TryGetValue(loginName, out bool cached))
                return cached;
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                {
                    object result = Database.GameDatabase.ExecuteScalar(db,
                        "SELECT is_admin FROM accounts WHERE username = @name",
                        ("@name", loginName));
                    if (result != null && result != System.DBNull.Value)
                    {
                        bool isAdmin = System.Convert.ToInt32(result) != 0;
                        _playerIsAdmin[loginName] = isAdmin;
                        return isAdmin;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ACCOUNT] Admin check error for {loginName}: {ex.Message}");
            }
            return false;
        }

        private void SetPlayerAdmin(string loginName, bool isAdmin)
        {
            _playerIsAdmin[loginName] = isAdmin;
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                {
                    Database.GameDatabase.ExecuteNonQuery(db,
                        "UPDATE accounts SET is_admin = @admin WHERE username = @name",
                        ("@name", loginName), ("@admin", isAdmin ? 1 : 0));
                    if (isAdmin)
                    {
                        Database.GameDatabase.ExecuteNonQuery(db,
                            "UPDATE accounts SET is_member = 1 WHERE username = @name",
                            ("@name", loginName));
                        Debug.LogError($"[ACCOUNT] {loginName} admin=true, is_member=1 (admin->member)");
                    }
                    else
                        Debug.LogError($"[ACCOUNT] {loginName} admin={isAdmin}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ACCOUNT] setAdmin state=failed message='{ex.Message}'");
            }
        }

        private void SetPlayerBanned(string loginName, bool isBanned)
        {
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                    Database.GameDatabase.ExecuteNonQuery(db,
                        "UPDATE accounts SET is_banned = @ban WHERE username = @name",
                        ("@name", loginName), ("@ban", isBanned ? 1 : 0));
                Debug.LogError($"[ACCOUNT] {loginName} banned={isBanned}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ACCOUNT] setBanned state=failed message='{ex.Message}'");
            }
        }

        public void SetPlayerAdminPublic(string loginName, bool isAdmin) { SetPlayerAdmin(loginName, isAdmin); }
        public void SetPlayerBannedPublic(string loginName, bool isBanned) { SetPlayerBanned(loginName, isBanned); }

        private IEnumerator DelayedBanDisconnect(RRConnection conn)
        {
            yield return ServerRuntime.DelaySeconds(3);
            try { SendSystemMessage(conn, "Your account has been banned. Contact an administrator."); } catch { }
            yield return ServerRuntime.DelaySeconds(2);
            Debug.LogError($"[ACCOUNT] Disconnecting banned user: {conn.LoginName}");
            try { conn.Client.Close(); } catch { }
        }
        private bool IsPlayerFree(string loginName)
        {
            if (IsPlayerAdmin(loginName)) return false;

            if (_playerIsFree.TryGetValue(loginName, out bool cached))
                return cached;

            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                {
                    object result = Database.GameDatabase.ExecuteScalar(db,
                        "SELECT is_member FROM accounts WHERE username = @name",
                        ("@name", loginName));
                    if (result != null && result != System.DBNull.Value)
                    {
                        bool isFree = System.Convert.ToInt32(result) == 0;
                        _playerIsFree[loginName] = isFree;
                        return isFree;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MEMBER] DB read error for {loginName}: {ex.Message}");
            }

            _playerIsFree[loginName] = false;
            Debug.LogError($"[MEMBER] Missing DB membership for {loginName}; defaulting to MEMBER");
            return false;
        }

        private void SetPlayerMembership(string loginName, bool isFree)
        {
            _playerIsFree[loginName] = isFree;
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                    Database.GameDatabase.ExecuteNonQuery(db,
                        "UPDATE accounts SET is_member = @member WHERE username = @name",
                        ("@name", loginName), ("@member", isFree ? 0 : 1));
                Debug.LogError($"[MEMBER] Saved {loginName} = {(isFree ? "FREE" : "MEMBER")}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MEMBER] dbSave state=failed message='{ex.Message}'");
            }
        }



        private void HandleInitialConnection(RRConnection conn, byte messageType, byte[] data)
        {
            if (VerbosePacketLogging)
                Debug.Log($"Initial connection message type: 0x{messageType:X2}, data length: {data.Length}");

            if (messageType == 0x02 && data.Length == 0)
            {
                if (VerbosePacketLogging)
                    Debug.Log("Ignoring empty channel 0 type 0x02 message (heartbeat/keep-alive)");
                return;
            }

            if (messageType == 0x00 && data.Length >= 5)
            {
                if (conn.LoginAdmissionPending || conn.LoginAdmitted)
                    return;

                var reader = new LEReader(data);
                byte subtype = reader.ReadByte();
                uint oneTimeKey = reader.ReadUInt32();

                Debug.Log($"OneTimeKey: 0x{oneTimeKey:X8}");

                if (!GlobalSessions.TryConsume(oneTimeKey, out var user) || string.IsNullOrEmpty(user))
                {
                    if (conn.LoginName != null)
                    {
                        user = conn.LoginName;
                        Debug.LogError($"[QUEUE] Accepting queue connection for {user} (no OneTimeKey needed)");
                    }
                    else
                    {
                        Debug.LogError($"Invalid OneTimeKey 0x{oneTimeKey:X8}");
                        return;
                    }
                }

                BeginInitialLoginAdmission(conn, user);
            }
            else
            {
                Debug.LogWarning($"Unhandled initial connection: type=0x{messageType:X2}, length={data.Length}");
            }
        }

        private void BeginInitialLoginAdmission(RRConnection conn, string user)
        {
            conn.LoginName = user;

            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == null || other == conn || other.ConnId <= conn.ConnId) continue;
                if (!string.Equals(other.LoginName, user, StringComparison.OrdinalIgnoreCase)) continue;
                if (!other.LoginAdmissionPending && !other.LoginAdmitted) continue;
                conn.ReplacementConnectionId = other.ConnId;
                conn.Disconnect();
                Debug.LogError($"[LOGIN] rejected conn={conn.ConnId} current={other.ConnId} user={user}");
                return;
            }

            conn.LoginAdmissionPending = true;
            if (HasFailedDisconnectCaptureFenceForLogin(user))
            {
                conn.LoginAdmissionPending = false;
                conn.Disconnect();
                Debug.LogError($"[LOGIN] rejected conn={conn.ConnId} user={user} reason=disconnect-capture-quarantine");
                return;
            }
            if (HasPendingDisconnectedCharacterSaveForLogin(user))
            {
                if (_pendingInitialLoginAdmissions.Any(pending =>
                        !ReferenceEquals(pending.Connection, conn)
                        && string.Equals(pending.User, user, StringComparison.OrdinalIgnoreCase)))
                {
                    conn.LoginAdmissionPending = false;
                    conn.Disconnect();
                    Debug.LogError($"[LOGIN] rejected conn={conn.ConnId} user={user} reason=disconnect-save-admission-pending");
                    return;
                }
                _pendingInitialLoginAdmissions.Add((conn, user));
                return;
            }
            bool retiring = false;
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == null || other == conn || other.ConnId >= conn.ConnId) continue;
                if (!string.Equals(other.LoginName, user, StringComparison.OrdinalIgnoreCase)) continue;
                other.ReplacementConnectionId = conn.ConnId;
                other.Disconnect();
                retiring = true;
                Debug.LogError($"[LOGIN] retire old={other.ConnId} new={conn.ConnId} user={user}");
            }

            if (retiring)
            {
                _pendingInitialLoginAdmissions.Add((conn, user));
                return;
            }

            CompleteInitialLogin(conn, user);
        }

        private void DrainPendingInitialLoginAdmissions()
        {
            for (int pendingIndex = 0; pendingIndex < _pendingInitialLoginAdmissions.Count;)
            {
                var pending = _pendingInitialLoginAdmissions[pendingIndex];
                RRConnection conn = pending.Connection;
                string user = pending.User;
                if (!_isRunning || conn == null || !conn.IsConnected || conn.ReplacementConnectionId != 0)
                {
                    _pendingInitialLoginAdmissions.RemoveAt(pendingIndex);
                    continue;
                }
                if (HasOlderLoginConnection(conn, user))
                {
                    pendingIndex++;
                    continue;
                }
                if (!_connections.TryGetValue(conn.ConnId, out RRConnection current) || current != conn)
                {
                    _pendingInitialLoginAdmissions.RemoveAt(pendingIndex);
                    continue;
                }
                if (HasFailedDisconnectCaptureFenceForLogin(user))
                {
                    _pendingInitialLoginAdmissions.RemoveAt(pendingIndex);
                    conn.LoginAdmissionPending = false;
                    conn.Disconnect();
                    Debug.LogError($"[LOGIN] rejected conn={conn.ConnId} user={user} reason=disconnect-capture-quarantine");
                    continue;
                }
                if (HasPendingDisconnectedCharacterSaveForLogin(user))
                {
                    TryFlushPendingDisconnectedCharacterSaveForLogin(user);
                    if (HasPendingDisconnectedCharacterSaveForLogin(user))
                    {
                        pendingIndex++;
                        continue;
                    }
                }

                _pendingInitialLoginAdmissions.RemoveAt(pendingIndex);
                CompleteInitialLogin(conn, user);
                if (conn.InboundGameBytes.Count > 0)
                    ProcessReceivedBytes(conn, Array.Empty<byte>());
            }
        }

        private bool HasOlderLoginConnection(RRConnection conn, string user)
        {
            foreach (RRConnection other in GetConnectionInsertionOrderSnapshot())
            {
                if (other == null || other == conn || other.ConnId >= conn.ConnId) continue;
                if (string.Equals(other.LoginName, user, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void CompleteInitialLogin(RRConnection conn, string user)
        {
            if (HasFailedDisconnectCaptureFenceForLogin(user))
            {
                conn.LoginAdmissionPending = false;
                conn.Disconnect();
                Debug.LogError($"[LOGIN] rejected conn={conn.ConnId} user={user} reason=disconnect-capture-quarantine");
                return;
            }
            if (HasPendingDisconnectedCharacterSaveForLogin(user)
                && !TryFlushPendingDisconnectedCharacterSaveForLogin(user))
            {
                conn.LoginAdmissionPending = true;
                if (!_pendingInitialLoginAdmissions.Any(pending => ReferenceEquals(pending.Connection, conn)))
                    _pendingInitialLoginAdmissions.Add((conn, user));
                return;
            }
            conn.LoginAdmissionPending = false;
            conn.LoginAdmitted = true;
            _users[conn.ConnId] = user;
            LoadAccountFlags(user);
            Debug.Log($"Initial login SUCCESS for user '{user}'");

            var ack = new LEWriter();
            ack.WriteByte(0x03);
            SendMessage0x10(conn, 0x0A, ack.ToArray());

            var advance = new LEWriter();
            advance.WriteUInt24(0x00B2B3B4);
            advance.WriteByte(0x00);
            SendCompressedA(conn, 0x00, 0x03, advance.ToArray());
            Debug.Log($"Sent advance message");

            StartCharacterFlow(conn);
        }

        private void StartCharacterFlow(RRConnection conn)
        {
            Debug.LogError($"[STARTFLOW] StartCharacterFlow for {conn.LoginName}");

            var savedCharacters = CharacterRepository.GetCharactersForAccount(conn.LoginName);
            if (savedCharacters == null)
            {
                _persistentCharacters.Remove(conn.LoginName);
                Debug.LogError($"[STARTFLOW] state=failed user='{conn.LoginName}' reason=character-list-load");
                return;
            }
            Debug.LogError($"[STARTFLOW] GetCharactersForAccount('{conn.LoginName}') returned {savedCharacters?.Count ?? 0} characters");

            _persistentCharacters[conn.LoginName] = new List<GCObject>();

            foreach (var savedChar in savedCharacters)
            {
                var gcObj = new GCObject
                {
                    Id = savedChar.id,
                    DFCClass = "Player",
                    GCClass = "Player",
                    Name = savedChar.name
                };
                _persistentCharacters[conn.LoginName].Add(gcObj);
                Debug.LogError($"[STARTFLOW] Added character: {savedChar.name} (ID={savedChar.id})");
            }

            Debug.LogError($"[STARTFLOW] Total characters loaded: {_persistentCharacters[conn.LoginName].Count}");
        }

        private void SendMessage0x10(RRConnection conn, byte channel, byte[] payload)
        {
            try
            {
                uint clientId = GetClientId24(conn.ConnId);
                uint bodyLen = (uint)(payload?.Length ?? 0);

                var writer = new LEWriter();
                writer.WriteByte(0x10);
                writer.WriteUInt24((int)clientId);
                writer.WriteUInt24((int)bodyLen);
                writer.WriteByte(channel);
                if (bodyLen > 0) writer.WriteBytes(payload);

                byte[] data = writer.ToArray();
                lock (conn.SendLock)
                {
                    conn.Stream.Write(data, 0, data.Length);
                }

                Debug.Log($"Sent 0x10: peer=0x{clientId:X6}, bodyLen={bodyLen}, channel=0x{channel:X2}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SEND-MESSAGE-0x10] state=failed message='{ex.Message}'");
            }
        }

        private void HandleCharacterChannel(RRConnection conn, byte messageType, byte[] data)
        {
            Debug.LogError($"[CHARACTER-CHANNEL] Received message type: 0x{messageType:X2}, data length: {data?.Length ?? 0}");
            if (conn == null || string.IsNullOrWhiteSpace(conn.LoginName)
                || HasPendingDisconnectedCharacterSaveForLogin(conn.LoginName))
            {
                Debug.LogError($"[CHARACTER-CHANNEL] state=rejected reason=disconnect-writer-fence conn={conn?.ConnId ?? 0}");
                conn?.Disconnect();
                return;
            }

            switch (messageType)
            {
                case 0:
                    Debug.Log($"Client sent 4/0 request - sending character connected response");
                    var connectionMessage = new LEWriter();
                    connectionMessage.WriteByte(4);
                    connectionMessage.WriteByte(0);
                    SendCompressedA(conn, 0x01, 0x0F, connectionMessage.ToArray());
                    Debug.Log($"Sent 4/0 character connected response");
                    break;

                case 1:
                    Debug.Log($"Client sent character disconnect (4/1) - sending ack");
                    var ack = new LEWriter();
                    ack.WriteByte(4);
                    ack.WriteByte(1);
                    SendCompressedA(conn, 0x01, 0x0F, ack.ToArray());
                    Debug.Log($"Sent 4/1 ack");
                    break;

                case 2:
                    Debug.LogError($"[CHAR-CREATE] *** RECEIVED CHARACTER CREATION REQUEST (4/2) ***");
                    Debug.LogError($"[CHAR-CREATE] Data: {BitConverter.ToString(data ?? new byte[0])}");
                    HandleCharacterCreate(conn, data);
                    break;

                case 3:
                    Debug.LogError($"[CHAR-CHANNEL] Case 3 - calling SendCharacterList NOW");
                    try
                    {
                        SendCharacterList(conn);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[CHAR-CHANNEL] EXCEPTION in SendCharacterList: {ex.Message}\n{ex.StackTrace}");
                    }
                    Debug.LogError($"[CHAR-CHANNEL] Case 3 - SendCharacterList returned");
                    break;

                case 4:
                    Debug.LogError($"[CHAR-DELETE] *** RECEIVED CHARACTER DELETE REQUEST (4/4) ***");
                    Debug.LogError($"[CHAR-DELETE] Data: {BitConverter.ToString(data ?? new byte[0])}");
                    HandleCharacterDelete(conn, data);
                    break;

                case 5:
                    HandleCharacterPlay(conn, data);
                    break;

                default:
                    Debug.LogWarning($"Unhandled character message: 0x{messageType:X2}");
                    Debug.LogWarning($"Data: {BitConverter.ToString(data ?? new byte[0])}");
                    break;
            }
        }
        private void HandleCharacterCreate(RRConnection conn, byte[] data)
        {
            Debug.LogError("[CHAR-CREATE] ");
            Debug.LogError($"[CHAR-CREATE] Data hex: {BitConverter.ToString(data)}");

            try
            {
                var reader = new LEReader(data);
                string characterName = reader.ReadCString();
                string avatarClass = reader.ReadCString();

                Debug.LogError($"[CHAR-CREATE] name='{characterName}' avatarClass='{avatarClass}'");

                byte skin = 0, face = 0, faceFeature = 0, hair = 0, hairColor = 0;
                if (reader.Remaining >= 5)
                {
                    skin = reader.ReadByte();
                    face = reader.ReadByte();
                    faceFeature = reader.ReadByte();
                    hair = reader.ReadByte();
                    hairColor = reader.ReadByte();
                    Debug.LogError($"[CHAR-CREATE] Appearance: Skin={skin}, Face={face}, FaceFeature={faceFeature}, Hair={hair}, HairColor={hairColor}");
                }

                string className = "Fighter";
                if (avatarClass.Contains("Fighter")) className = "Fighter";
                else if (avatarClass.Contains("Warlock") || avatarClass.Contains("Mage")) className = "Mage";
                else if (avatarClass.Contains("Ranger")) className = "Ranger";

                uint accountId = AccountRepository.GetAccountId(conn.LoginName);
                if (accountId == 0)
                {
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }
                if (CharacterRepository.CharacterNameExists(characterName))
                {
                    SendCharacterManagerError(conn, CharacterManagerErrorNameAlreadyInUse);
                    return;
                }

                var savedChar = CharacterRepository.CreateCharacter(characterName, className, accountId, conn.LoginName, avatarClass);
                if (savedChar == null)
                {
                    Debug.LogError("[CHAR-CREATE]  CharacterRepository.CreateCharacter returned null!");
                    if (CharacterRepository.CharacterNameExists(characterName))
                        SendCharacterManagerError(conn, CharacterManagerErrorNameAlreadyInUse);
                    else
                        SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }

                savedChar.avatarClass = avatarClass;
                savedChar.skin = skin;
                savedChar.face = face;
                savedChar.faceFeature = faceFeature;
                savedChar.hair = hair;
                savedChar.hairColor = hairColor;
                if (!CharacterRepository.TrySaveCharacter(savedChar, "character-create-appearance"))
                {
                    if (!CharacterRepository.DeleteCharacter(savedChar.id, accountId, characterName))
                        Debug.LogError($"[CHAR-CREATE] state=cleanup-failed id={savedChar.id} name='{characterName}'");
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }

                Debug.LogError($"[CHAR-CREATE]  Created saved character ID={savedChar.id}, AvatarClass={avatarClass}");

                var newCharacter = new GCObject
                {
                    Id = savedChar.id,
                    DFCClass = "Player",
                    GCClass = "Player",
                    Name = characterName
                };

                if (!_persistentCharacters.ContainsKey(conn.LoginName))
                {
                    _persistentCharacters[conn.LoginName] = new List<GCObject>();
                }
                _persistentCharacters[conn.LoginName].Add(newCharacter);

                GCObject playerObject = GCObjectFactory.NewPlayer(characterName, (uint)savedChar.id, GroupDirectory.Instance.GetGroupForConn(conn.ConnId)?.GroupId ?? 0, savedChar);
                playerObject.Id = savedChar.id;

                var avatar = GCObjectFactory.LoadCharacterSelectionAvatar(savedChar);
                avatar.Id = AllocateGeneralEntityId();

                foreach (var child in avatar.Children)
                {
                    child.Id = AllocateGeneralEntityId();
                    if (child.Children != null)
                    {
                        foreach (var grandchild in child.Children)
                        {
                            if (grandchild.Id == 0)
                                grandchild.Id = AllocateGeneralEntityId();
                        }
                    }
                }

                playerObject.AddChild(avatar);

                var procModifierObject = GCObjectFactory.NewProcModifier();
                procModifierObject.Id = AllocateGeneralEntityId();
                playerObject.AddChild(procModifierObject);

                var writer = new LEWriter();
                writer.WriteByte(4);
                writer.WriteByte(2);
                writer.WriteUInt32(savedChar.id);
                writer.WriteCString(characterName);
                playerObject.WriteFullGCObject(writer);

                SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
                Debug.LogError($"[CHAR-CREATE]  Sent create response with full Player object");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHAR-CREATE] state=failed message='{ex.Message}' stack='{ex.StackTrace}'");
                SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
            }
        }
        private void SendCharacterManagerError(RRConnection conn, byte errorCode)
        {
            var writer = new LEWriter();
            writer.WriteByte(4);
            writer.WriteByte(0xFF);
            writer.WriteByte(errorCode);
            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[CHAR-CREATE] Sent CharacterManager error={errorCode}");
        }
        private void HandleCharacterDelete(RRConnection conn, byte[] data)
        {
            Debug.LogError($"[CHAR-DELETE] ");
            try
            {
                if (data == null || data.Length == 0)
                {
                    Debug.LogError($"[CHAR-DELETE]  No data received!");
                    return;
                }

                var reader = new LEReader(data);
                string characterName = reader.ReadCString();
                uint characterId = reader.ReadUInt32();
                Debug.LogError($"[CHAR-DELETE] name='{characterName}' id={characterId}");

                SavedCharacter savedCharacter = CharacterRepository.GetCharacter(characterId);
                uint accountId = AccountRepository.GetAccountId(conn.LoginName);
                if (savedCharacter == null
                    || savedCharacter.accountId != accountId
                    || !string.Equals(savedCharacter.name, characterName, StringComparison.OrdinalIgnoreCase))
                {
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }

                bool deleted = CharacterRepository.DeleteCharacter(characterId, accountId, characterName);
                if (!deleted)
                {
                    Debug.LogError($"[CHAR-DELETE] Delete failed for ID={characterId}");
                    SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
                    return;
                }
                RemoveCharacterFromPersistentCaches(conn, characterId);
                Debug.LogError($"[CHAR-DELETE] Deleted from SQLite");
                InvalidateCharacterRuntimeState(conn, characterId);

                var writer = new LEWriter();
                writer.WriteByte(4);
                writer.WriteByte(4);
                writer.WriteUInt32(characterId);
                SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
                Debug.LogError($"[CHAR-DELETE]  Sent delete ack for ID={characterId}");
                Debug.LogError($"[CHAR-DELETE]  Done");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHAR-DELETE] state=failed message='{ex.Message}'");
                SendCharacterManagerError(conn, CharacterManagerErrorOperationUnavailable);
            }
        }



        private void HandleClientEntityChannel(RRConnection conn, byte messageType, byte[] data)
        {
            var reader = new LEReader(data);

            if (messageType == 0x07)
            {
                ParseEntityStream(conn, reader);
                return;
            }

            switch (messageType)
            {
                case 0x03: HandleOpcode_SendUpdate(conn, reader); break;
                case 0x04: HandleEntityRequest(conn, reader, data); break;
                case 0x08: HandleOpcode_CombatTick(conn, reader); break;
                case 0x09: HandleOpcode_Aggro(conn, reader); break;
                case 0x0C: HandleOpcode_RngSeed(conn, reader); break;
                case 0x34:
                case 0x35: HandleOpcode_ComponentUpdate(conn, reader, messageType); break;
                case 0x36: ProcessEntitySynchInfoHP(conn, reader); break;
                case 0x64: HandleOpcode_StateMachine(conn, reader); break;

                case 0x01: LogMissingOpcode(0x01, "BehaviorNotify", data, conn); break;
                case 0x0A: LogMissingOpcode(0x0A, "EntityChannel0A", data); break;
                case 0x0B: LogMissingOpcode(0x0B, "EntityChannel0B", data); break;
                case 0x23: LogMissingOpcode(0x23, "PathBehavior", data); break;
                case 0x25: LogMissingOpcode(0x25, "ActionCmd_25", data); break;
                case 0x26: LogMissingOpcode(0x26, "ActionCmd_26", data); break;
                case 0x27: LogMissingOpcode(0x27, "ActionCmd_27", data); break;
                case 0x28: LogMissingOpcode(0x28, "PathTarget_Simple", data); break;
                case 0x29: LogMissingOpcode(0x29, "PathTarget_Extended", data); break;
                case 0x32: HandleOpcode_ComponentUpdate(conn, reader, messageType); break;
                case 0x58: LogMissingOpcode(0x58, "EntityChannel58", data); break;

                default:
                    Debug.LogWarning($"[ENTITY-CH] Unknown type=0x{messageType:X2} len={data?.Length ?? 0}");
                    if (data != null && data.Length > 0)
                        LogMissingOpcode(messageType, $"Unknown_0x{messageType:X2}", data, conn);
                    break;
            }
        }

        private void ParseEntityStream(RRConnection conn, LEReader reader)
        {
            int loopCount = 0;
            while (reader.Remaining > 0)
            {
                byte subType = reader.ReadByte();
                if (subType == 0x06) break;
                loopCount++;

                switch (subType)
                {
                    case 0x03: HandleOpcode_SendUpdate(conn, reader); break;
                    case 0x04: HandleEntityRequest(conn, reader, null); break;
                    case 0x08: HandleOpcode_CombatTick(conn, reader); break;
                    case 0x09: HandleOpcode_Aggro(conn, reader); break;
                    case 0x0C: HandleOpcode_RngSeed(conn, reader); break;
                    case 0x32:
                    case 0x34:
                    case 0x35: HandleOpcode_ComponentUpdate(conn, reader, subType); break;
                    case 0x36: ProcessEntitySynchInfoHP(conn, reader); break;
                    case 0x64: HandleOpcode_StateMachine(conn, reader); break;
                    default:
                        byte[] leftover = reader.Remaining > 0 ? reader.PeekRemaining() : new byte[0];
                        Debug.LogWarning($"[ENTITY-STREAM] Unknown sub=0x{subType:X2} remain={reader.Remaining}");
                        if (leftover.Length > 0 && leftover.Length < 128)
                        {
                            byte[] streamPacket = new byte[leftover.Length + 1];
                            streamPacket[0] = subType;
                            Array.Copy(leftover, 0, streamPacket, 1, leftover.Length);
                            LogMissingOpcode(subType, $"StreamSub_0x{subType:X2}", streamPacket, conn);
                        }
                        reader.Skip(reader.Remaining);
                        break;
                }
            }
        }


        private void HandleOpcode_RngSeed(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 4) return;
            uint clientSeed = reader.ReadUInt32();
            string instanceKey = conn != null ? GetInstanceZoneKey(conn) : null;
            uint roomSeed = CombatRuntime.Instance.GetRoomSeedForInstance(instanceKey);
            bool ready = CombatRuntime.Instance.TryGetRoomRuntime(instanceKey, out var runtime) && runtime.Initialized;
            Debug.LogError($"[RNG-SEED] Ignored client seed: 0x{clientSeed:X8} instance='{instanceKey ?? ""}' current=0x{roomSeed:X8} ready={ready}");
        }

        private void ProcessEntitySynchInfoHP(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 3) return;
            ushort entitySynchInfoEntityId = reader.ReadUInt16();
            byte entitySynchInfoFlags = reader.ReadByte();
            Debug.LogError($"[ENTITY-SYNCH-INFO] opcode=0x36 entity={entitySynchInfoEntityId} flags=0x{entitySynchInfoFlags:X2} remaining={reader.Remaining}");
            if ((entitySynchInfoFlags & 0x02) != 0)
            {
                if (reader.Remaining >= 4)
                {
                    uint clientHP = reader.ReadUInt32();
                    Debug.LogError($"[ENTITY-SYNCH-INFO] entity={entitySynchInfoEntityId} hpWire={clientHP} hp={clientHP / 256}");
                    var monster = ResolveMonsterForConnectionComponent(conn, entitySynchInfoEntityId);
                    if (monster != null)
                    {
                        int serverActual = (int)(CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) / 256);
                        int clientActual = (int)(clientHP / 256);
                        int delta = serverActual - clientActual;
                        Debug.LogError($"[ENTITY-SYNCH-INFO] owner=monster name='{monster.Name}' entity={monster.EntityId} serverHP={serverActual} clientHP={clientActual} delta={delta}");
                        CombatRuntime.Instance.ObserveClientMonsterHP(monster, clientHP, "ENTITY-SYNCH-INFO-0x36");
                    }
                    else if (IsAvatarOrAvatarComponentId(conn, entitySynchInfoEntityId))
                    {
                        PlayerState state = GetPlayerState(conn.ConnId.ToString());
                        if (state != null)
                        {
                            int clientActual = (int)(clientHP / 256);
                            int serverMax = (int)(state.MaxHPWire / 256);
                            int serverCurrent = (int)(state.CurrentHPWire / 256);

                            Debug.LogError($"[ENTITY-SYNCH-INFO] owner=player serverHP={serverCurrent} clientHP={clientActual} serverMaxHP={serverMax}");
                            ObserveClientPlayerHP(conn, clientHP, "ENTITY-SYNCH-INFO-0x36");

                            if (reader.Remaining > 0)
                                reader.Skip(reader.Remaining);
                        }
                    }
                    else
                    {
                        Debug.LogError($"[ENTITY-SYNCH-INFO] entity={entitySynchInfoEntityId} hpWire={clientHP} state=ignored reason=non-avatar");
                        if (reader.Remaining > 0)
                            reader.Skip(reader.Remaining);
                    }
                }
                else
                {
                    Debug.LogError($"[ENTITY-SYNCH-INFO] flags=0x02 remaining={reader.Remaining} state=short");
                    if (reader.Remaining > 0)
                    {
                        byte[] leftover = reader.PeekRemaining();
                        Debug.LogError($"[ENTITY-SYNCH-INFO] leftover={BitConverter.ToString(leftover)}");
                    }
                }
            }
        }


        private bool IsAvatarEntityId(RRConnection conn, uint entityId)
        {
            return conn?.Avatar != null && entityId == (uint)conn.Avatar.Id;
        }

        private bool IsAvatarEntitySynchInfoComponentId(RRConnection conn, uint entityId)
        {
            if (IsAvatarEntityId(conn, entityId))
                return true;

            if (conn == null || entityId == 0)
                return false;

            return entityId == conn.UnitBehaviorId
                || entityId == conn.BehaviorComponentId
                || entityId == conn.SkillsComponentId
                || entityId == conn.ManipulatorsComponentId
                || entityId == conn.ModifiersId
                || entityId == conn.ModifiersComponentId
                || entityId == conn.UnitContainerId;
        }

        private bool IsAvatarOrAvatarComponentId(RRConnection conn, uint entityId)
        {
            return IsAvatarEntitySynchInfoComponentId(conn, entityId);
        }

        private Combat.Monster ResolveMonsterForConnectionComponent(RRConnection conn, uint entityOrComponentId)
        {
            Combat.Monster monster = CombatRuntime.Instance.GetMonster(entityOrComponentId)
                ?? CombatRuntime.Instance.GetMonsterByComponent(entityOrComponentId);
            return monster != null
                && CombatRuntime.Instance.MatchesInstance(monster, GetInstanceZoneKey(conn))
                ? monster
                : null;
        }

        private bool GiveGold(RRConnection conn, uint amount, string source)
        {
            if (conn == null || amount == 0)
                return false;

            if (!TryAddGoldForConn(conn, amount, source ?? "gold", out _))
            {
                Debug.LogError($"[GOLD] state=failed phase=save source={source ?? "unknown"}");
                return false;
            }

            if (conn.UnitContainerId == 0)
                return true;

            var goldPacket = new LEWriter();
            goldPacket.WriteByte(0x07);
            goldPacket.WriteByte(0x35);
            goldPacket.WriteUInt16(conn.UnitContainerId);
            goldPacket.WriteByte(0x20);
            goldPacket.WriteUInt32(amount);
            goldPacket.WriteByte(0x00);
            goldPacket.WriteUInt32(0x00000000);
            goldPacket.WriteByte(0x01);
            WritePlayerEntitySynch(conn, goldPacket);
            goldPacket.WriteByte(0x06);
            SendToClient(conn, goldPacket.ToArray());
            Debug.LogError($"[GIVE-GOLD] source={source ?? "unknown"} +{amount} gold sent to client");
            return true;
        }

        private bool IsNonUnitPlayerComponentId(RRConnection conn, uint entityId)
        {
            if (conn == null || entityId == 0)
                return false;

            return (conn.Player != null && entityId == (uint)conn.Player.Id)
                || entityId == conn.DialogManagerId
                || entityId == conn.QuestManagerId;
        }

        private bool IsPlayerOwnedComponentId(RRConnection conn, uint entityId)
        {
            return IsNonUnitPlayerComponentId(conn, entityId);
        }

        private void LogPlayerHPVisibleEvent(RRConnection conn, string reason)
        {
            if (conn == null) return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            Debug.LogError($"[PLAYER-HP-TRUTH] VISIBLE-EVENT player={conn.LoginName ?? conn.ConnId.ToString()} serverHP={(state != null ? state.CurrentHPWire / 256f : 0f):F2}/{(state != null ? state.MaxHPWire / 256f : 0f):F2} entitySynchInfoHP={(state != null ? state.EntitySynchInfoHP / 256f : 0f):F2} reason={reason ?? "monster attack"}");
        }

        private void RecordPlayerHPKnown(RRConnection conn, string source, uint acceptedHPWire)
        {
            if (conn == null) return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (playerEntityId != 0 && state != null)
                EntitySynchInfoAuthority.Instance.RecordPlayerOutboundHP(conn, state, playerEntityId, acceptedHPWire, source ?? "unknown");
            else
            {
                conn.LastOutboundHPWire = acceptedHPWire;
                conn.LastOutboundHPSource = source ?? "unknown";
            }
            Debug.LogError($"[PLAYER-HP-TRUTH] KNOWN source={source} player={conn.LoginName ?? conn.ConnId.ToString()} hp={acceptedHPWire / 256f:F2}");
        }

        private void CommitPlayerHPTruth(RRConnection conn, PlayerState state, string source, uint hpWire, bool updateRuntimeHP, bool applyDamageCooldown)
        {
            if (conn == null || state == null) return;
            uint beforeCurrent = state.CurrentHPWire;
            uint beforeEntitySynchInfoHP = state.EntitySynchInfoHP;
            uint beforeMax = state.MaxHPWire;
            if (updateRuntimeHP)
                state.SetCurrentHP(hpWire, applyDamageCooldown);
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (playerEntityId != 0)
                EntitySynchInfoAuthority.Instance.RegisterPlayer(conn, state, playerEntityId);
            RecordPlayerHPKnown(conn, source, state.EntitySynchInfoHP);
            Debug.LogError($"[PLAYER-HP-TRUTH] COMMIT source={source} player={conn.LoginName ?? conn.ConnId.ToString()} currentHP={beforeCurrent}->{state.CurrentHPWire}/{state.MaxHPWire} entitySynchInfoHP={beforeEntitySynchInfoHP}->{state.EntitySynchInfoHP} maxHP={beforeMax}->{state.MaxHPWire} runtimeUpdate={updateRuntimeHP}");
        }

        private bool CanSendPlayerEntitySynchInfoHP(RRConnection conn, string packetName)
        {
            if (conn == null) return false;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return false;
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (playerEntityId != 0)
                EntitySynchInfoAuthority.Instance.RegisterPlayer(conn, state, playerEntityId);
            return true;
        }

        private void RemoveCharacterFromPersistentCaches(RRConnection conn, uint characterId)
        {
            if (conn == null || characterId == 0)
                return;
            var logins = new List<string>();
            if (!string.IsNullOrEmpty(conn.LoginName))
                logins.Add(conn.LoginName);
            foreach (RRConnection candidate in GetConnectionInsertionOrderSnapshot())
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.LoginName) || logins.Contains(candidate.LoginName, StringComparer.OrdinalIgnoreCase))
                    continue;
                logins.Add(candidate.LoginName);
            }
            foreach (string loginName in logins)
                if (_persistentCharacters.TryGetValue(loginName, out List<GCObject> characters))
                    characters?.RemoveAll(character => character != null && character.Id == characterId);
        }

        private void InvalidateCharacterRuntimeState(RRConnection conn, uint characterId)
        {
            if (characterId == 0)
                return;
            var affectedLogins = new List<string>();
            List<RRConnection> connections = GetConnectionInsertionOrderSnapshot();
            if (conn != null && !string.IsNullOrEmpty(conn.LoginName)
                && (conn.CharSqlId == characterId || HasCharacterRuntimeState(conn.LoginName, characterId)))
                affectedLogins.Add(conn.LoginName);
            foreach (RRConnection candidate in connections)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.LoginName))
                    continue;
                if ((candidate.CharSqlId == characterId || HasCharacterRuntimeState(candidate.LoginName, characterId))
                    && !affectedLogins.Contains(candidate.LoginName, StringComparer.OrdinalIgnoreCase))
                    affectedLogins.Add(candidate.LoginName);
            }

            foreach (string loginName in affectedLogins)
            {
                if (_selectedCharacter.TryGetValue(loginName, out GCObject selectedCharacter)
                    && selectedCharacter != null && selectedCharacter.Id == characterId)
                    _selectedCharacter.Remove(loginName);
                if (_activeCharacter.TryGetValue(loginName, out SavedCharacter activeCharacter)
                    && activeCharacter != null && activeCharacter.id == characterId)
                    _activeCharacter.Remove(loginName);
                _playerStates.Remove(loginName);
            }

            foreach (RRConnection candidate in connections)
            {
                if (candidate == null || candidate.CharSqlId != characterId)
                    continue;
                string connKey = candidate.ConnId.ToString();
                _playerStates.Remove(connKey);
                _playerSpellSlots.Remove(connKey);
                _playerManipMap.Remove(connKey);
                _playerManipulatorOrder.Remove(connKey);
                _playerSkillLevels.Remove(connKey);
                _playerSkillSlots.Remove(connKey);
                _playerEquippedItems.Remove(connKey);
                _playerManipulatorsIds.Remove(connKey);
                candidate.CharSqlId = 0;
            }

            if (conn != null && conn.CharSqlId == characterId)
            {
                string connKey = conn.ConnId.ToString();
                _playerStates.Remove(connKey);
                _playerSpellSlots.Remove(connKey);
                _playerManipMap.Remove(connKey);
                _playerManipulatorOrder.Remove(connKey);
                _playerSkillLevels.Remove(connKey);
                _playerSkillSlots.Remove(connKey);
                _playerEquippedItems.Remove(connKey);
                _playerManipulatorsIds.Remove(connKey);
                conn.CharSqlId = 0;
            }
        }

        private bool HasCharacterRuntimeState(string loginName, uint characterId)
        {
            return !string.IsNullOrEmpty(loginName)
                && ((_selectedCharacter.TryGetValue(loginName, out GCObject selectedCharacter)
                    && selectedCharacter != null && selectedCharacter.Id == characterId)
                || (_activeCharacter.TryGetValue(loginName, out SavedCharacter activeCharacter)
                    && activeCharacter != null && activeCharacter.id == characterId));
        }

        private bool ObserveClientPlayerHP(RRConnection conn, uint clientHP, string source)
        {
            if (conn == null) return false;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return false;
            uint runtimeHPWire = state.EntitySynchInfoHP;
            bool matches = clientHP == runtimeHPWire;
            Debug.LogError($"[{source}] player HP diagnostic received={clientHP} runtime={runtimeHPWire} result={(matches ? "match" : "mismatch-non-authoritative")}");
            return matches;
        }

        private bool TryConsumeClientEntitySynchInfoSuffix(RRConnection conn, LEReader reader, string source)
        {
            return TryConsumeClientEntitySynchInfoSuffix(conn, reader, source, null);
        }

        private bool TryConsumeClientEntitySynchInfoSuffix(RRConnection conn, LEReader reader, string source, Monster targetMonster)
        {
            if (reader == null || reader.Remaining < 1) return false;

            byte entitySynchInfoFlags = reader.ReadByte();
            if ((entitySynchInfoFlags & 0x02) == 0)
            {
                Debug.LogError($"[{source}] entitySynchInfoFlags=0x{entitySynchInfoFlags:X2} (no HP)");
                return false;
            }

            if (reader.Remaining < 4)
            {
                Debug.LogError($"[{source}] entitySynchInfoFlags=0x{entitySynchInfoFlags:X2} but HP missing remaining={reader.Remaining}");
                return false;
            }

            uint entitySynchInfoHP = reader.ReadUInt32();
            Debug.LogError($"[{source}] entitySynchInfoFlags=0x{entitySynchInfoFlags:X2} HP={entitySynchInfoHP} ({entitySynchInfoHP / 256} actual)");
            if (targetMonster != null)
            {
                CombatRuntime.Instance.RecordMonsterHPObservation(targetMonster, entitySynchInfoHP, source);
                return true;
            }
            return ObserveClientPlayerHP(conn, entitySynchInfoHP, source);
        }

        private void HandleOpcode_CombatTick(RRConnection conn, LEReader reader)
        {
            Debug.LogError($"[COMBAT-TICK] 0x08 remain={reader.Remaining}");
            if (reader.Remaining < 7) return;

            byte combatSub = reader.ReadByte();
            ushort combatSize = reader.ReadUInt16();
            int combatDamage = reader.ReadInt32();
            Debug.LogError($"[COMBAT-TICK] sub={combatSub} size={combatSize} damage={combatDamage}");

            if (combatDamage > 0)
            {
                foreach (var m in CombatRuntime.Instance.GetActiveMonsters())
                {
                    if (m.State == MonsterState.Combat && m.IsAlive)
                    {
                        Debug.LogError($"[COMBAT-TICK] CLIENT_DMG={combatDamage} on {m.Name} (target ambiguous; waiting for EntitySynchInfo HP) SERVER_HP={CombatRuntime.Instance.PeekMonsterCurrentHPWire(m) / 256}");
                        break;
                    }
                }
            }

            if (reader.Remaining >= 4) reader.ReadUInt32();
        }

        private void HandleOpcode_Aggro(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 3) return;
            ushort aggroEntityId = reader.ReadUInt16();
            byte aggroLevel = reader.ReadByte();
            Debug.LogError($"[AGGRO] 0x09: entityId={aggroEntityId} level={aggroLevel}");
            var monster = ResolveMonsterForConnectionComponent(conn, aggroEntityId);
            if (monster != null && conn?.Avatar != null)
            {
                uint playerEntityId = (uint)conn.Avatar.Id;
                CombatRuntime.Instance.EngageMonsterFromClientAction(monster, playerEntityId);
            }
        }

        private void HandleOpcode_SendUpdate(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 3) return;
            ushort entityId = reader.ReadUInt16();
            byte updateType = reader.ReadByte();
            Debug.LogError($"[SEND-UPDATE] 0x03: entity={entityId} updateType=0x{updateType:X2}");

            if (reader.Remaining < 1) return;
            byte flags = reader.ReadByte();

            if ((flags & 0x02) != 0 && reader.Remaining >= 4)
            {
                uint hp = reader.ReadUInt32();
                Debug.LogError($"[SEND-UPDATE] HP: entity={entityId} HP={hp} ({hp / 256} actual)");

                var monster = ResolveMonsterForConnectionComponent(conn, entityId);
                if (monster != null)
                {
                    int serverActual = (int)(CombatRuntime.Instance.PeekMonsterCurrentHPWire(monster) / 256);
                    int clientActual = (int)(hp / 256);
                    Debug.LogError($"[SEND-UPDATE] Monster {monster.Name} SERVER_HP={serverActual} CLIENT_HP={clientActual} DELTA={serverActual - clientActual}");
                    CombatRuntime.Instance.ObserveClientMonsterHP(monster, hp, "SEND-UPDATE-0x03");
                }
                else if (IsAvatarOrAvatarComponentId(conn, entityId))
                {
                    PlayerState state = GetPlayerState(conn.ConnId.ToString());
                    if (state != null)
                    {
                        ObserveClientPlayerHP(conn, hp, "SEND-UPDATE");
                    }
                }
                else
                {
                    Debug.LogError($"[SEND-UPDATE] Ignored HP for non-avatar entity={entityId} HP={hp}");
                }
            }
        }

        private void HandleOpcode_StateMachine(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 1) return;
            byte stateFlags = reader.ReadByte();

            ushort stateType = 0xFFFF;
            ushort scope = 0xFFFF;
            ushort target = 0;
            uint stateValue = 0;

            if ((stateFlags & 0x02) != 0 && reader.Remaining >= 2)
                stateType = reader.ReadUInt16();
            if ((stateFlags & 0x04) != 0 && reader.Remaining >= 2)
                scope = reader.ReadUInt16();
            if ((stateFlags & 0x08) != 0 && reader.Remaining >= 2)
                target = reader.ReadUInt16();
            if ((stateFlags & 0x20) != 0 && reader.Remaining >= 4)
                stateValue = reader.ReadUInt32();
            if ((stateFlags & 0x10) != 0 && reader.Remaining >= 2)
            {
                ushort wCount = reader.ReadUInt16();
                for (int w = 0; w < wCount && reader.Remaining >= 2; w++)
                    reader.ReadUInt16();
            }

            Debug.LogError($"[STATE-MACHINE] 0x64: flags=0x{stateFlags:X2} type={stateType} scope={scope} target={target} value={stateValue}");
        }

        private void HandleOpcode_ComponentUpdate(RRConnection conn, LEReader reader, byte opcode)
        {
            if (reader.Remaining >= 2)
            {
                byte[] peek = reader.PeekRemaining();
                ushort cid = (ushort)(peek[0] | (peek[1] << 8));
                if (ResolveMonsterForConnectionComponent(conn, cid) != null)
                {
                    int posBefore = reader.Position;
                    HandleComponentUpdate(conn, reader);
                    int posAfter = reader.Position;
                    int consumed = posAfter - posBefore;
                    if (consumed > 0 && VerbosePacketLogging)
                        Debug.LogError($"[RELAY] Skipped client-origin monster component echo cid={cid} opcode=0x{opcode:X2} len={consumed}");
                    return;
                }
            }
            HandleComponentUpdate(conn, reader);
        }


        private void LogMissingOpcode(byte opcode, string name, byte[] data, RRConnection conn = null)
        {
            if (!VerbosePacketLogging) return;
            int len = data?.Length ?? 0;
            string hex = len > 0 ? BitConverter.ToString(data, 0, Math.Min(len, 256)) : "(empty)";
            Debug.LogError($"[DETAIL-0x{opcode:X2}] ");
            string playerPos = conn != null ? $"playerFixed=({conn.PlayerPosFixedX},{conn.PlayerPosFixedY})" : "player=?";
            Debug.LogError($"[DETAIL-0x{opcode:X2}] {name} len={len} {playerPos} raw={hex}");

            if (data == null || len < 2) return;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[DETAIL-0x{opcode:X2}] uint16 scan: ");
            for (int byteOffset = 0; byteOffset <= len - 2; byteOffset += 2)
            {
                ushort value = (ushort)(data[byteOffset] | (data[byteOffset + 1] << 8));
                string tag = "";
                var monster = CombatRuntime.Instance.GetMonsterByComponent(value);
                if (monster != null)
                {
                    tag = $"=MON:{monster.Name}";
                }
                sb.Append($"[{byteOffset}]=0x{value:X4}({value}){tag} ");
            }
            Debug.LogError(sb.ToString());

            if (len >= 4)
            {
                var sb2 = new System.Text.StringBuilder();
                sb2.Append($"[DETAIL-0x{opcode:X2}] int32/coord scan: ");
                for (int byteOffset = 0; byteOffset <= len - 4; byteOffset += 4)
                {
                    int value = data[byteOffset] | (data[byteOffset + 1] << 8) | (data[byteOffset + 2] << 16) | (data[byteOffset + 3] << 24);
                    string coord = FormatFixed8Diagnostic(value);
                    string tag = (value > -128000 && value < 256000 && value != 0) ? " <COORD?>" : "";
                    sb2.Append($"[{byteOffset}]={value}({coord}){tag} ");
                }
                Debug.LogError(sb2.ToString());
            }

            if (len >= 3)
            {
                ushort eid = (ushort)(data[0] | (data[1] << 8));
                byte sub = data[2];
                var monster = CombatRuntime.Instance.GetMonsterByComponent(eid);
                string monTag = monster != null ? $" MONSTER={monster.Name}(posFixed8={monster.PosFixedX},{monster.PosFixedY})" : "";
                Debug.LogError($"[DETAIL-0x{opcode:X2}] as [eid={eid}(0x{eid:X4}) sub=0x{sub:X2}]{monTag} remain={len - 3}b");
            }

            if (len >= 5)
            {
                ushort cid = (ushort)(data[0] | (data[1] << 8));
                var monster = CombatRuntime.Instance.GetMonsterByComponent(cid);
                if (monster != null)
                {
                    Debug.LogError($"[DETAIL-0x{opcode:X2}] monsterCidHit cid={cid} name={monster.Name} entity={monster.EntityId} behavior={monster.BehaviorId}");
                }
            }

            Debug.LogError($"[DETAIL-0x{opcode:X2}] ");
        }

        private static string FormatFixed8Diagnostic(int value)
        {
            long magnitude = value < 0 ? -(long)value : value;
            long tenths = (magnitude * 10 + 128) / 256;
            return $"{(value < 0 ? "-" : "")}{tenths / 10}.{tenths % 10}";
        }




        private static int _serverKillCount = 0;
    }
}
