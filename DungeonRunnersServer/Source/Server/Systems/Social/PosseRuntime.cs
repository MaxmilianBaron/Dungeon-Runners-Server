using System;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Database;

namespace DungeonRunners.Networking
{
    public class PosseRuntime
    {
        private static PosseRuntime _instance;
        public static PosseRuntime Instance => _instance ??= new PosseRuntime();

        private readonly System.Collections.Generic.Dictionary<uint, uint> _pendingInvites
            = new System.Collections.Generic.Dictionary<uint, uint>();


        public const byte CHANNEL = 0x0F;
        private const byte MSG_PROCESS_GET_INFO                 = 0x00;
        private const byte MSG_PROCESS_INVITE                   = 0x01;
        private const byte MSG_PROCESS_KICK                     = 0x02;
        private const byte MSG_PROCESS_PROMOTE                  = 0x03;
        private const byte MSG_PROCESS_DEMOTE                   = 0x04;
        private const byte MSG_PROCESS_GET_ROSTER               = 0x05;
        private const byte MSG_PROCESS_CHANGE_LEADER            = 0x06;
        private const byte MSG_PROCESS_LEADER_CHANGED           = 0x07;
        private const byte MSG_PROCESS_DISBAND                  = 0x08;
        private const byte MSG_PROCESS_JOIN                     = 0x0B;
        private const byte MSG_PROCESS_LEAVE                    = 0x0C;
        private const byte MSG_PROCESS_UPDATE_CACHED_POSSE      = 0x0D;
        private const byte MSG_PROCESS_CONNECTION_NOTIFICATION  = 0x0E;
        private const byte MSG_PROCESS_PROPERTY_CHANGED         = 0x0F;
        private const byte MSG_PROCESS_MEMBER_PROPERTY_CHANGED  = 0x10;
        private const byte MSG_PROCESS_INVITATION_NOTIFICATION  = 0x11;
        private const byte MSG_PROCESS_INVITE_REQUEST_RESULT    = 0x12;
        private const byte MSG_PROCESS_INVITE_RESPONSE_RESULT   = 0x13;
        private const byte MSG_PROCESS_CREATE                   = 0x14;
        private const byte MSG_CHAT_COMMAND        = 0x01;
        private const byte MSG_INBOUND_INVITE      = 0x03;
        private const byte MSG_INBOUND_KICK        = 0x04;
        private const byte MSG_INBOUND_PROMOTE     = 0x05;
        private const byte MSG_INBOUND_DEMOTE      = 0x06;
        private const byte MSG_INBOUND_SET_LEADER  = 0x08;
        private const byte MSG_INBOUND_DISBAND     = 0x09;
        private const byte MSG_INBOUND_ACCEPT_INVITE = 0x10;
        private const byte MSG_INBOUND_DECLINE_INVITE = 0x11;
        private const byte MSG_INBOUND_LEAVE       = 0x0D;
        private const byte MSG_INBOUND_CREATE      = 0x0E;
        private const byte MSG_INBOUND_SET_PROP    = 0x0F;
        private const string EMPTY_STRING_SENTINEL = "EmptyString_Placeholder";

        private const uint STATUS_SUCCESS         = 0x00;
        private const uint STATUS_NAME_COLLISION  = 0x02;
        private const uint STATUS_ALREADY_MEMBER  = 0x06;
        private const uint STATUS_MUST_WAIT       = 0x18;
        private const uint STATUS_UNAVAILABLE     = 0x63;
        private const uint STATUS_NOT_ENOUGH_GOLD = 0xec;
        private const uint STATUS_NAME_FILTER     = 0xee;

        private const int MIN_LEVEL_TO_CREATE      = 15;
        private const uint GOLD_COST_TO_CREATE     = 1000000;
        private const int POST_LEAVE_COOLDOWN_SECS = 30;

        public PosseRuntime() { }

        public void HandleMessage(RRConnection conn, byte messageType, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            string hex = (data != null && data.Length > 0)
                ? BitConverter.ToString(data, 0, Math.Min(48, data.Length))
                : "EMPTY";
            Debug.LogError($"[POSSE] inbound type=0x{messageType:X2} dataLen={data?.Length ?? 0} hex={hex}");

            switch (messageType)
            {
                case MSG_CHAT_COMMAND:
                    HandleChatCommand(conn, data, sendCompressed, server);
                    break;
                case MSG_INBOUND_INVITE:
                    HandleInboundInvite(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_KICK:
                    HandleInboundKick(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_PROMOTE:
                    HandleInboundPromote(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_DEMOTE:
                    HandleInboundDemote(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_SET_LEADER:
                    HandleInboundSetLeader(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_DISBAND:
                    HandleDisband(conn, sendCompressed, server);
                    break;
                case MSG_INBOUND_ACCEPT_INVITE:
                    HandleInboundAcceptInvite(conn, sendCompressed, server);
                    break;
                case MSG_INBOUND_DECLINE_INVITE:
                    HandleInboundDeclineInvite(conn, sendCompressed, server);
                    break;
                case MSG_INBOUND_LEAVE:
                    HandleLeave(conn, sendCompressed, server);
                    break;
                case MSG_INBOUND_CREATE:
                    HandleInboundCreate(conn, data, sendCompressed, server);
                    break;
                case MSG_INBOUND_SET_PROP:
                    HandleInboundSetProperty(conn, data, sendCompressed, server);
                    break;
                default:
                    Debug.LogError($"[POSSE] no handler for inbound type=0x{messageType:X2}");
                    break;
            }
        }

        private void HandleInboundCreate(RRConnection conn, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            string name = "";
            if (data != null && data.Length > 0)
            {
                try { name = new LEReader(data).ReadCString(); }
                catch { Debug.LogError("[POSSE] inbound-create payload not null-terminated"); }
            }
            Debug.LogError($"[POSSE] inbound-create form submit name='{name}'");
            HandleCreate(conn, name, sendCompressed, server);
        }

        private static string ReadFirstCString(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            try { return new LEReader(data).ReadCString(); }
            catch { return ""; }
        }

        private void HandleInboundInvite(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            Debug.LogError($"[POSSE-INVITE] {conn.LoginName} invites '{targetName}'");
            var (posse, savedChar) = RequireRank(conn, server, 8);
            if (posse == null) return;
            if (string.IsNullOrWhiteSpace(targetName))
            {
                server.SendSystemMessage(conn, "[POSSE-INVITE] usage=invite <player>");
                return;
            }

            var targetCharRow = LookupCharacterByName(targetName, server);
            if (targetCharRow == null)
            {
                server.SendSystemMessage(conn, $"No character named '{targetName}'.");
                return;
            }
            if (targetCharRow.posseId != 0)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is already in a posse.");
                return;
            }

            RRConnection targetConn = null;
            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc != null && gc.Id == targetCharRow.id) { targetConn = memberConn; break; }
            }
            if (targetConn == null)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not online.");
                return;
            }

            SendInvitationNotification(targetConn, savedChar.name, posse.Name,
                (c, dest, mt, data) => server.SendSocialViaAuthPublic(c, dest, mt, data));
            _pendingInvites[targetCharRow.id] = posse.Id;
            Debug.LogError($"[POSSE-INVITE] tracking pending invite: char={targetCharRow.id} ('{targetName}') -> posse={posse.Id} ('{posse.Name}')");
            server.SendSystemMessage(conn, $"Invited '{targetName}' to '{posse.Name}'.");
        }

        private void HandleInboundAcceptInvite(RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null)
            {
                Debug.LogError($"[POSSE-ACCEPT] no selected character for {conn.LoginName}");
                return;
            }
            if (!_pendingInvites.TryGetValue(savedChar.id, out uint posseId))
            {
                Debug.LogError($"[POSSE-ACCEPT] {savedChar.name} accepted but no pending invite found");
                return;
            }
            var posse = PosseRepository.GetPosse(posseId);
            if (posse == null)
            {
                _pendingInvites.Remove(savedChar.id);
                Debug.LogError($"[POSSE-ACCEPT] pending posse {posseId} no longer exists for {savedChar.name}");
                server.SendSystemMessage(conn, "That posse no longer exists.");
                return;
            }
            if (savedChar.posseId != 0)
            {
                _pendingInvites.Remove(savedChar.id);
                Debug.LogError($"[POSSE-ACCEPT] {savedChar.name} already in posse {savedChar.posseId}");
                server.SendSystemMessage(conn, "You are already in a posse.");
                return;
            }
            if (!PosseRepository.TryJoinPosse(savedChar.id, posseId, 1))
            {
                server.SendSystemMessage(conn, "Joining that posse failed. The invitation is still pending.");
                return;
            }
            _pendingInvites.Remove(savedChar.id);
            savedChar.posseId = posseId;
            savedChar.posseName = posse.Name;
            savedChar.posseRankId = 1;
            Debug.LogError($"[POSSE-ACCEPT] {savedChar.name} joined '{posse.Name}' (id={posseId})");

            var members = new System.Collections.Generic.List<(uint charId, string name, bool isFounder)>();
            using (var dbConnection = GameDatabase.GetConnection())
            using (var reader = GameDatabase.ExecuteReader(dbConnection,
                "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                ("@pid", (int)posseId)))
            {
                while (reader.Read())
                {
                    uint characterId = (uint)reader.GetInt32(0);
                    string characterName = reader.GetString(1);
                    members.Add((characterId, characterName, characterId == posse.FounderCharacterId));
                }
            }

            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc == null) continue;
                bool inThisPosse = members.Exists(member => member.charId == gc.Id);
                if (!inThisPosse) continue;
                SendCachedPosseFull(memberConn, gc.Id, posse, members,
                    (c, dest, mt, data) => server.SendSocialViaAuthPublic(c, dest, mt, data), server);
            }

            server.SendSystemMessage(conn, $"You joined '{posse.Name}'.");
        }

        private void HandleInboundDeclineInvite(RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null) return;
            if (!_pendingInvites.TryGetValue(savedChar.id, out uint posseId))
            {
                Debug.LogError($"[POSSE-DECLINE] {savedChar.name} declined but no pending invite tracked");
                return;
            }
            _pendingInvites.Remove(savedChar.id);
            var posse = PosseRepository.GetPosse(posseId);
            string posseName = posse?.Name ?? $"posse {posseId}";
            Debug.LogError($"[POSSE-DECLINE] {savedChar.name} declined invite to '{posseName}'");

            if (posse != null)
            {
                foreach (var memberConn in server.AllConnectedConnections())
                {
                    var gc = server.GetSelectedCharacter(memberConn.LoginName);
                    if (gc != null && gc.Id == posse.FounderCharacterId)
                    {
                        SendInviteRequestResult(memberConn, INVITE_RESULT_DECLINED,
                            posseId, savedChar.name, server);
                        break;
                    }
                }
            }
        }

        private const uint INVITE_RESULT_TARGET_BUSY     = 0xEFu;
        private const uint INVITE_RESULT_TIMED_OUT       = 0xF0u;
        private const uint INVITE_RESULT_TARGET_OFFLINE  = 0xF1u;
        private const uint INVITE_RESULT_DECLINED        = 0xF2u;

        private void SendInviteRequestResult(RRConnection conn, uint statusCode,
            uint posseId, string targetName, GameServer server)
        {
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_INVITE_REQUEST_RESULT);
            writer.WriteUInt32(statusCode);
            writer.WriteUInt32(posseId);
            writer.WriteCString(targetName ?? "");
            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=InviteRequestResult status=0x{statusCode:X2} posseId={posseId} target='{targetName}' hex={BitConverter.ToString(packet)}");
            server.SendSocialViaAuthPublic(conn, 0x01, 0x0F, packet);
        }

        private void HandleInboundKick(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var (posse, savedChar) = RequireRank(conn, server, 8);
            if (posse == null) return;
            if (string.IsNullOrWhiteSpace(targetName) || string.Equals(targetName, savedChar.name, StringComparison.OrdinalIgnoreCase))
            {
                server.SendSystemMessage(conn, "You can't kick yourself - use /posse disband instead.");
                return;
            }
            var target = LookupCharacterByName(targetName, server);
            if (target == null || target.posseId != posse.Id)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not a member of your posse.");
                return;
            }
            if (target.id == posse.FounderCharacterId)
            {
                server.SendSystemMessage(conn, "You cannot kick the posse founder.");
                return;
            }
            if (posse.FounderCharacterId != savedChar.id && target.posseRankId >= savedChar.posseRankId)
            {
                server.SendSystemMessage(conn, $"You cannot kick '{target.name}' - their rank is equal or higher than yours.");
                return;
            }
            int cooldownExpires = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + POST_LEAVE_COOLDOWN_SECS;
            if (!PosseRepository.ClearCharacterMembership(target.id, cooldownExpires))
            {
                server.SendSystemMessage(conn, $"Failed to kick '{target.name}'.");
                return;
            }
            target.posseId = 0;
            target.posseName = "";
            target.posseRankId = 0;
            target.posseJoinCooldown = cooldownExpires;
            Debug.LogError($"[POSSE-KICK] {savedChar.name} kicked {target.name} from '{posse.Name}'");

            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc != null && gc.Id == target.id)
                {
                    var writer = new LEWriter();
                    writer.WriteByte(CHANNEL);
                    writer.WriteByte(MSG_PROCESS_KICK);
                    writer.WriteUInt32(target.id);
                    writer.WriteUInt32(target.id);
                    writer.WriteUInt32(STATUS_SUCCESS);
                    server.SendSocialViaAuthPublic(memberConn, 0x01, 0x0F, writer.ToArray());
                    Debug.LogError($"[POSSE-KICK] Notified victim {target.name} via processKick");
                    break;
                }
            }

            BroadcastCachedPosseFull(posse, server);
        }

        private void HandleInboundPromote(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var (posse, savedChar) = RequireFounder(conn, server);
            if (posse == null) return;
            var target = LookupCharacterByName(targetName, server);
            if (target == null || target.posseId != posse.Id)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not a member of your posse.");
                return;
            }
            if (target.id == posse.FounderCharacterId)
            {
                server.SendSystemMessage(conn, "You cannot promote the posse founder.");
                return;
            }
            int oldRank = target.posseRankId;
            int newRank = oldRank + 1;
            if (newRank > 9)
            {
                server.SendSystemMessage(conn, $"'{target.name}' is already at the highest non-founder rank (9). Use /posse setleader to transfer founder.");
                return;
            }
            if (!PosseRepository.TrySetCharacterRank(target.id, posse.Id, newRank))
            {
                server.SendSystemMessage(conn, $"Failed to promote '{target.name}'.");
                return;
            }
            target.posseRankId = newRank;
            Debug.LogError($"[POSSE-PROMOTE] {savedChar.name} promoted {target.name} from rank {oldRank} -> {newRank} in '{posse.Name}'");
            BroadcastProcessPromote(posse, savedChar.id, target.id, (uint)newRank, server);
            BroadcastCachedPosseFull(posse, server);
        }

        private void HandleInboundDemote(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var (posse, savedChar) = RequireFounder(conn, server);
            if (posse == null) return;
            var target = LookupCharacterByName(targetName, server);
            if (target == null || target.posseId != posse.Id)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not a member of your posse.");
                return;
            }
            if (target.id == posse.FounderCharacterId)
            {
                server.SendSystemMessage(conn, "You cannot demote the posse founder.");
                return;
            }
            int oldRank = target.posseRankId;
            int newRank = oldRank - 1;
            if (newRank < 1)
            {
                server.SendSystemMessage(conn, $"'{target.name}' is already at the lowest rank (1).");
                return;
            }
            if (!PosseRepository.TrySetCharacterRank(target.id, posse.Id, newRank))
            {
                server.SendSystemMessage(conn, $"Failed to demote '{target.name}'.");
                return;
            }
            target.posseRankId = newRank;
            Debug.LogError($"[POSSE-DEMOTE] {savedChar.name} demoted {target.name} from rank {oldRank} -> {newRank} in '{posse.Name}'");
            BroadcastProcessDemote(posse, savedChar.id, target.id, (uint)newRank, server);
            BroadcastCachedPosseFull(posse, server);
        }

        private void BroadcastProcessPromote(PosseRecord posse, uint promoterCharId, uint promotedCharId, uint newRank, GameServer server)
        {
            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc == null) continue;
                var memberCharacter = CharacterRepository.GetCharacter(gc.Id);
                if (memberCharacter == null || memberCharacter.posseId != posse.Id) continue;
                var writer = new LEWriter();
                writer.WriteByte(CHANNEL);
                writer.WriteByte(MSG_PROCESS_PROMOTE);
                writer.WriteUInt32(STATUS_SUCCESS);
                writer.WriteUInt32(promoterCharId);
                writer.WriteUInt32(promotedCharId);
                writer.WriteUInt32(newRank);
                server.SendSocialViaAuthPublic(memberConn, 0x01, 0x0F, writer.ToArray());
            }
            Debug.LogError($"[POSSE-PROMOTE] Notified posse '{posse.Name}' members (promoter={promoterCharId}, promoted={promotedCharId}, newRank={newRank})");
        }

        private void BroadcastProcessDemote(PosseRecord posse, uint demoterCharId, uint demotedCharId, uint newRank, GameServer server)
        {
            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc == null) continue;
                var memberCharacter = CharacterRepository.GetCharacter(gc.Id);
                if (memberCharacter == null || memberCharacter.posseId != posse.Id) continue;
                var writer = new LEWriter();
                writer.WriteByte(CHANNEL);
                writer.WriteByte(MSG_PROCESS_DEMOTE);
                writer.WriteUInt32(STATUS_SUCCESS);
                writer.WriteUInt32(demoterCharId);
                writer.WriteUInt32(demotedCharId);
                writer.WriteUInt32(newRank);
                server.SendSocialViaAuthPublic(memberConn, 0x01, 0x0F, writer.ToArray());
            }
            Debug.LogError($"[POSSE-DEMOTE] Notified posse '{posse.Name}' members (demoter={demoterCharId}, demoted={demotedCharId}, newRank={newRank})");
        }

        private void HandleInboundSetLeader(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var (posse, savedChar) = RequireFounder(conn, server);
            if (posse == null) return;
            var target = LookupCharacterByName(targetName, server);
            if (target == null || target.posseId != posse.Id)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not a member of your posse.");
                return;
            }
            if (target.id == savedChar.id)
            {
                server.SendSystemMessage(conn, "You are already the founder.");
                return;
            }
            uint oldLeaderId = savedChar.id;
            uint newLeaderId = target.id;
            if (!PosseRepository.TryTransferLeadership(posse.Id, oldLeaderId, newLeaderId))
            {
                server.SendSystemMessage(conn, "Failed to transfer posse leadership.");
                return;
            }
            posse.FounderCharacterId = newLeaderId;
            savedChar.posseRankId = 9;
            target.posseRankId = 10;
            Debug.LogError($"[POSSE-LEADER] {savedChar.name} transferred leader of '{posse.Name}' to {target.name}");
            BroadcastProcessChangeLeader(posse, oldLeaderId, newLeaderId, server);
            BroadcastCachedPosseFull(posse, server);
        }

        private void BroadcastProcessChangeLeader(PosseRecord posse, uint oldLeaderId, uint newLeaderId,
            GameServer server)
        {
            foreach (var memberConn in server.GetConnectedMemberConnsForPosse(posse.Id))
            {
                var writer = new LEWriter();
                writer.WriteByte(CHANNEL);
                writer.WriteByte(MSG_PROCESS_CHANGE_LEADER);
                writer.WriteUInt32(STATUS_SUCCESS);
                writer.WriteUInt32(oldLeaderId);
                writer.WriteUInt32(1u);
                writer.WriteUInt32(newLeaderId);
                writer.WriteUInt32(1u);
                server.SendSocialViaAuthPublic(memberConn, 0x01, 0x0F, writer.ToArray());
            }
            Debug.LogError($"[POSSE-LEADER] Broadcast ChangeLeader to posse '{posse.Name}' members (old={oldLeaderId}, new={newLeaderId})");
        }

        private void HandleInboundSetProperty(RRConnection conn, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            string payload = "";
            if (data != null && data.Length > 0)
            {
                try { payload = new LEReader(data).ReadCString(); }
                catch { Debug.LogError("[POSSE] set-property payload not null-terminated"); return; }
            }
            Debug.LogError($"[POSSE] inbound set-property payload='{payload}'");

            string[] parts = payload.Split(';');
            uint propertyId = 0;
            if (parts.Length >= 2) uint.TryParse(parts[1], out propertyId);
            string newValue = parts.Length >= 3 ? parts[2] : "";

            var (posse, savedChar) = RequireRank(conn, server, 8);
            if (posse == null) return;

            string storedValue = newValue == EMPTY_STRING_SENTINEL ? "" : newValue;
            bool ok = propertyId == 1
                ? PosseRepository.SetMotd(posse.Id, storedValue)
                : PosseRepository.SetDescription(posse.Id, storedValue);
            if (!ok)
            {
                server.SendSystemMessage(conn, "Failed to update posse property.");
                return;
            }
            if (propertyId == 1) posse.Motd = storedValue; else posse.Description = storedValue;
            Debug.LogError($"[POSSE-PROP] {savedChar.name} set property id={propertyId} on posse {posse.Id}: payload='{newValue}' stored='{storedValue}'");

            foreach (var memberConn in server.GetConnectedMemberConnsForPosse(posse.Id))
            {
                if (memberConn == conn) continue;
                var gcObj = server.GetSelectedCharacter(memberConn.LoginName);
                if (gcObj == null) continue;
                var memberList = new System.Collections.Generic.List<(uint, string, bool)>();
                using (var dbConnection = GameDatabase.GetConnection())
                using (var reader = GameDatabase.ExecuteReader(dbConnection,
                    "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                    ("@pid", (int)posse.Id)))
                {
                    while (reader.Read())
                    {
                        uint characterId = (uint)reader.GetInt32(0);
                        string characterName = reader.GetString(1);
                        memberList.Add((characterId, characterName, characterId == posse.FounderCharacterId));
                    }
                }
                SendCachedPosseFull(memberConn, gcObj.Id, posse, memberList,
                    (c, dest, mt, d) => server.SendSocialViaAuthPublic(c, dest, mt, d), server);
            }
        }

        public void SendPropertyChanged(RRConnection conn, uint propertyId, string newValue,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_PROPERTY_CHANGED);
            writer.WriteUInt32(propertyId);
            writer.WriteCString(newValue ?? "");
            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=PropertyChanged id={propertyId} value='{newValue}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        private void HandleChatCommand(RRConnection conn, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            if (data == null || data.Length == 0) return;
            string commandLine;
            try { commandLine = new LEReader(data).ReadCString(); }
            catch { Debug.LogError("[POSSE] chat-cmd payload not null-terminated"); return; }

            int spaceIdx = commandLine.IndexOf(' ');
            string verb = spaceIdx < 0 ? commandLine : commandLine.Substring(0, spaceIdx);
            string args = spaceIdx < 0 ? "" : commandLine.Substring(spaceIdx + 1);
            Debug.LogError($"[POSSE] chat-cmd verb='{verb}' args='{args}'");

            switch (verb.ToLowerInvariant())
            {
                case "create":
                    HandleCreate(conn, args, sendCompressed, server);
                    break;
                case "leave":
                    HandleLeave(conn, sendCompressed, server);
                    break;
                case "disband":
                    HandleDisband(conn, sendCompressed, server);
                    break;
                case "rename":
                    HandleRename(conn, args, sendCompressed, server);
                    break;
                case "motd":
                    HandleSetMotd(conn, args, sendCompressed, server);
                    break;
                case "desc":
                case "description":
                    HandleSetDescription(conn, args, sendCompressed, server);
                    break;
                case "info":
                case "status":
                    HandleInfo(conn, server);
                    break;
                case "members":
                case "roster":
                case "who":
                    HandleMembers(conn, server);
                    break;
                case "help":
                case "?":
                    HandleHelp(conn, server);
                    break;
                case "invite":
                    HandleInboundInvite(conn, args, sendCompressed, server);
                    break;
                case "kick":
                    HandleInboundKick(conn, args, sendCompressed, server);
                    break;
                case "promote":
                    HandleInboundPromote(conn, args, sendCompressed, server);
                    break;
                case "demote":
                    HandleInboundDemote(conn, args, sendCompressed, server);
                    break;
                case "setleader":
                case "leader":
                    HandleInboundSetLeader(conn, args, sendCompressed, server);
                    break;
                default:
                    Debug.LogError($"[POSSE] unhandled chat verb '{verb}'");
                    server.SendSystemMessage(conn, $"Unknown /posse command '{verb}'. Try /posse help.");
                    break;
            }
        }

        private void HandleCreate(RRConnection conn, string rawName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            string name = (rawName ?? "").Trim();

            var gcObj = server.GetSelectedCharacter(conn.LoginName);
            if (gcObj == null)
            {
                Debug.LogError($"[POSSE-CREATE] {conn.LoginName} has no selected character");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }

            var savedChar = server.GetSavedCharacterForConn(conn);
            if (savedChar == null)
            {
                Debug.LogError($"[POSSE-CREATE] character id={gcObj.Id} not found in DB");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }
            PlayerState playerState = server.GetPlayerState(conn.ConnId.ToString());
            if (playerState == null)
            {
                Debug.LogError($"[POSSE-CREATE] player state missing characterId={savedChar.id}");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }

            if (savedChar.posseId != 0)
            {
                Debug.LogError($"[POSSE-CREATE] {savedChar.name} already in posse id={savedChar.posseId}");
                SendCreateResponse(conn, STATUS_ALREADY_MEMBER, name, sendCompressed);
                return;
            }

            if (name.Length < 2 || name.Length > 32 || HasIllegalNameChars(name))
            {
                Debug.LogError($"[POSSE-CREATE] name '{name}' failed length/char filter");
                SendCreateResponse(conn, STATUS_NAME_FILTER, name, sendCompressed);
                return;
            }

            if (savedChar.level < MIN_LEVEL_TO_CREATE)
            {
                Debug.LogError($"[POSSE-CREATE] {savedChar.name} level {savedChar.level} < {MIN_LEVEL_TO_CREATE}");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }

            int nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (savedChar.posseJoinCooldown > nowUnix)
            {
                Debug.LogError($"[POSSE-CREATE] {savedChar.name} cooldown until {savedChar.posseJoinCooldown}, now {nowUnix}");
                SendCreateResponse(conn, STATUS_MUST_WAIT, name, sendCompressed);
                return;
            }

            if (savedChar.gold < GOLD_COST_TO_CREATE)
            {
                Debug.LogError($"[POSSE-CREATE] {savedChar.name} gold {savedChar.gold} < {GOLD_COST_TO_CREATE}");
                SendCreateResponse(conn, STATUS_NOT_ENOUGH_GOLD, name, sendCompressed);
                return;
            }

            if (PosseRepository.PosseNameExists(name))
            {
                Debug.LogError($"[POSSE-CREATE] name '{name}' already taken");
                SendCreateResponse(conn, STATUS_NAME_COLLISION, name, sendCompressed);
                return;
            }

            var posse = PosseRepository.CreatePosse(name, savedChar.id, GOLD_COST_TO_CREATE);
            if (posse == null)
            {
                Debug.LogError($"[POSSE-CREATE] DB insert failed for '{name}'");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }

            savedChar.gold -= GOLD_COST_TO_CREATE;
            savedChar.posseId = posse.Id;
            savedChar.posseName = posse.Name;
            savedChar.posseRankId = 10;
            playerState.Gold = savedChar.gold;
            Debug.LogError($"[POSSE-CREATE]  {savedChar.name} founded '{posse.Name}' id={posse.Id} gold {savedChar.gold + GOLD_COST_TO_CREATE} -> {savedChar.gold}");

            SendGoldDeduction(conn, GOLD_COST_TO_CREATE, sendCompressed, server);

            var members = new System.Collections.Generic.List<(uint, string, bool)>
            {
                (savedChar.id, savedChar.name, true),
            };
            SendCachedPosseFull(conn, savedChar.id, posse, members, sendCompressed, server);
            SendCreateResponse(conn, STATUS_SUCCESS, posse.Name, sendCompressed);
        }

        private void HandleLeave(RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return;
            }
            if (savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return;
            }

            uint leftPosseId = savedChar.posseId;
            string leftPosseName = savedChar.posseName ?? "";
            int cooldownExpires = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + POST_LEAVE_COOLDOWN_SECS;

            int remainingMembers = PosseRepository.MemberCount(leftPosseId) - 1;
            if (remainingMembers <= 0)
            {
                if (!PosseRepository.TryDisbandPosse(leftPosseId, cooldownExpires, out _))
                {
                    server.SendSystemMessage(conn, "Failed to leave the posse.");
                    return;
                }
                ClearOnlinePosseMembership(leftPosseId, cooldownExpires, server);
                Debug.LogError($"[POSSE-LEAVE] {savedChar.name} was last member of '{leftPosseName}' (id={leftPosseId}) - posse dissolved");
            }
            else
            {
                if (!PosseRepository.ClearCharacterMembership(savedChar.id, cooldownExpires))
                {
                    server.SendSystemMessage(conn, "Failed to leave the posse.");
                    return;
                }
                savedChar.posseId = 0;
                savedChar.posseName = "";
                savedChar.posseRankId = 0;
                savedChar.posseJoinCooldown = cooldownExpires;
                Debug.LogError($"[POSSE-LEAVE] {savedChar.name} left '{leftPosseName}' (id={leftPosseId}, {remainingMembers} remain)");
            }

            SendProcessLeave(conn, savedChar.id, STATUS_SUCCESS, leftPosseName, sendCompressed);
        }

        private void HandleDisband(RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return;
            }
            var posse = PosseRepository.GetPosse(savedChar.posseId);
            if (posse == null)
            {
                server.SendSystemMessage(conn, "Your posse no longer exists.");
                return;
            }
            if (posse.FounderCharacterId != savedChar.id)
            {
                server.SendSystemMessage(conn, "Only the posse founder can disband. Try /posse leave instead.");
                return;
            }

            int cooldownExpires = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + POST_LEAVE_COOLDOWN_SECS;
            string posseName = posse.Name;
            if (!PosseRepository.TryDisbandPosse(savedChar.posseId, cooldownExpires, out var affected))
            {
                server.SendSystemMessage(conn, "Failed to disband the posse.");
                return;
            }
            ClearOnlinePosseMembership(posse.Id, cooldownExpires, server);
            Debug.LogError($"[POSSE-DISBAND] {savedChar.name} disbanded '{posseName}' (id={posse.Id}, {affected.Count} members affected)");

            SendProcessDisband(conn, STATUS_SUCCESS, posseName, sendCompressed);
        }

        private void HandleInfo(RRConnection conn, GameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse. Use /posse create <name> to start one.");
                return;
            }
            var posse = PosseRepository.GetPosse(savedChar.posseId);
            int count = PosseRepository.MemberCount(savedChar.posseId);
            string foundedAt = posse?.FoundedAt ?? "?";
            string founderTag = posse != null && posse.FounderCharacterId == savedChar.id ? " (you founded it)" : "";
            server.SendSystemMessage(conn, $"Posse '{savedChar.posseName}' - {count} member(s), founded {foundedAt}{founderTag}.");
            if (posse != null)
            {
                if (!string.IsNullOrEmpty(posse.Description))
                    server.SendSystemMessage(conn, $"Description: {posse.Description}");
                if (!string.IsNullOrEmpty(posse.Motd))
                    server.SendSystemMessage(conn, $"MOTD: {posse.Motd}");
            }
        }

        private void HandleMembers(RRConnection conn, GameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return;
            }
            var names = PosseRepository.MemberNames(savedChar.posseId);
            if (names.Count == 0)
            {
                server.SendSystemMessage(conn, $"Posse '{savedChar.posseName}' has no members? (DB inconsistency)");
                return;
            }
            server.SendSystemMessage(conn, $"Posse '{savedChar.posseName}' members ({names.Count}): {string.Join(", ", names)}");
        }

        private void HandleHelp(RRConnection conn, GameServer server)
        {
            server.SendSystemMessage(conn, "Posse commands:");
            server.SendSystemMessage(conn, "  /posse create <name>      - found a posse (level 15+, 1,000,000 gold)");
            server.SendSystemMessage(conn, "  /posse info               - show your current posse");
            server.SendSystemMessage(conn, "  /posse members            - list members");
            server.SendSystemMessage(conn, "  /posse rename <name>      - founder-only: rename");
            server.SendSystemMessage(conn, "  /posse motd <text>        - founder-only: set MOTD (blank to clear)");
            server.SendSystemMessage(conn, "  /posse desc <text>        - founder-only: set description");
            server.SendSystemMessage(conn, "  /posse leave              - leave your posse");
            server.SendSystemMessage(conn, "  /posse disband            - founder-only: delete the posse");
            server.SendSystemMessage(conn, "  /posse invite <name>      - rank 8+: invite a player");
            server.SendSystemMessage(conn, "  /posse kick <name>        - rank 8+: kick a lower-ranked member");
            server.SendSystemMessage(conn, "  /posse promote <name>     - founder-only: bump a member's rank by 1 (max 9)");
            server.SendSystemMessage(conn, "  /posse demote <name>      - founder-only: drop a member's rank by 1 (min 1)");
            server.SendSystemMessage(conn, "  /posse setleader <name>   - founder-only: transfer founder role");
            server.SendSystemMessage(conn, "(Ranks: 1 = new joiner, 10 = founder. Rank 8+ can edit MOTD/Description and invite/kick.)");
        }

        private void HandleRename(RRConnection conn, string rawName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            string newName = (rawName ?? "").Trim();
            var (posse, savedChar) = RequireFounder(conn, server);
            if (posse == null) return;
            if (newName.Length < 2 || newName.Length > 32 || HasIllegalNameChars(newName))
            {
                server.SendSystemMessage(conn, "Posse name failed validation.");
                return;
            }
            if (PosseRepository.PosseNameExists(newName))
            {
                server.SendSystemMessage(conn, $"A posse named '{newName}' already exists.");
                return;
            }
            string oldName = posse.Name;
            if (!PosseRepository.Rename(posse.Id, newName))
            {
                server.SendSystemMessage(conn, "Rename failed.");
                return;
            }
            Debug.LogError($"[POSSE-RENAME] {savedChar.name} renamed posse {posse.Id}: '{oldName}' -> '{newName}'");
            posse.Name = newName;
            BroadcastCachedPosseFull(posse, server);
            server.SendSystemMessage(conn, $"Posse renamed: '{oldName}' -> '{newName}'.");
        }

        private void HandleSetMotd(RRConnection conn, string motd,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var (posse, savedChar) = RequireRank(conn, server, 8);
            if (posse == null) return;
            motd = (motd ?? "").Trim();
            if (motd.Length > 256) { server.SendSystemMessage(conn, "MOTD must be 256 characters or fewer."); return; }
            if (!PosseRepository.SetMotd(posse.Id, motd))
            {
                server.SendSystemMessage(conn, "Failed to set MOTD.");
                return;
            }
            Debug.LogError($"[POSSE-MOTD] {savedChar.name} set MOTD on posse {posse.Id}: '{motd}'");
            posse.Motd = motd;
            BroadcastCachedPosseFull(posse, server);
            server.SendSystemMessage(conn, string.IsNullOrEmpty(motd) ? "MOTD cleared." : $"MOTD set: {motd}");
        }

        private void HandleSetDescription(RRConnection conn, string desc,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            var (posse, savedChar) = RequireRank(conn, server, 8);
            if (posse == null) return;
            desc = (desc ?? "").Trim();
            if (desc.Length > 512) { server.SendSystemMessage(conn, "Description must be 512 characters or fewer."); return; }
            if (!PosseRepository.SetDescription(posse.Id, desc))
            {
                server.SendSystemMessage(conn, "Failed to set description.");
                return;
            }
            Debug.LogError($"[POSSE-DESC] {savedChar.name} set description on posse {posse.Id}: '{desc}'");
            posse.Description = desc;
            BroadcastCachedPosseFull(posse, server);
            server.SendSystemMessage(conn, string.IsNullOrEmpty(desc) ? "Description cleared." : $"Description set: {desc}");
        }

        private (PosseRecord, SavedCharacter) RequireFounder(RRConnection conn, GameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return (null, null);
            }
            var posse = PosseRepository.GetPosse(savedChar.posseId);
            if (posse == null)
            {
                server.SendSystemMessage(conn, "Your posse record was not found.");
                return (null, null);
            }
            if (posse.FounderCharacterId != savedChar.id)
            {
                server.SendSystemMessage(conn, "Only the posse founder can do that.");
                return (null, null);
            }
            return (posse, savedChar);
        }

        private (PosseRecord, SavedCharacter) RequireRank(RRConnection conn, GameServer server, int minRank)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return (null, null);
            }
            var posse = PosseRepository.GetPosse(savedChar.posseId);
            if (posse == null)
            {
                server.SendSystemMessage(conn, "Your posse record was not found.");
                return (null, null);
            }
            if (posse.FounderCharacterId != savedChar.id && savedChar.posseRankId < minRank)
            {
                server.SendSystemMessage(conn, $"Your posse rank ({savedChar.posseRankId}) is too low - requires rank {minRank} or higher.");
                return (null, null);
            }
            return (posse, savedChar);
        }

        public void NotifyMemberStateChange(uint charId, GameServer server)
        {
            if (server == null) return;
            var savedCharacter = CharacterRepository.GetCharacter(charId);
            if (savedCharacter == null || savedCharacter.posseId == 0) return;
            var posse = PosseRepository.GetPosse(savedCharacter.posseId);
            if (posse == null) return;
            BroadcastCachedPosseFull(posse, server);
        }

        private void BroadcastCachedPosseFull(PosseRecord posse, GameServer server)
        {
            var members = new System.Collections.Generic.List<(uint, string, bool)>();
            using (var dbConnection = GameDatabase.GetConnection())
            using (var reader = GameDatabase.ExecuteReader(dbConnection,
                "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                ("@pid", (int)posse.Id)))
            {
                while (reader.Read())
                {
                    uint characterId = (uint)reader.GetInt32(0);
                    string characterName = reader.GetString(1);
                    members.Add((characterId, characterName, characterId == posse.FounderCharacterId));
                }
            }
            foreach (var memberConn in server.GetConnectedMemberConnsForPosse(posse.Id))
            {
                var gcObj = server.GetSelectedCharacter(memberConn.LoginName);
                if (gcObj == null) continue;
                SendCachedPosseFull(memberConn, gcObj.Id, posse, members, (c, dest, mt, data) =>
                    server.SendSocialViaAuthPublic(c, dest, mt, data), server);
            }
        }

        private SavedCharacter LookupSavedChar(RRConnection conn, GameServer server)
        {
            return server.GetSavedCharacterForConn(conn);
        }

        private SavedCharacter LookupCharacterByName(string characterName, GameServer server)
        {
            SavedCharacter persistedCharacter = CharacterRepository.GetCharacterByName(characterName);
            if (persistedCharacter == null || server == null)
                return persistedCharacter;
            foreach (RRConnection candidate in server.AllConnectedConnections())
            {
                GCObject selected = server.GetSelectedCharacter(candidate.LoginName);
                if (selected == null || selected.Id != persistedCharacter.id)
                    continue;
                return server.GetSavedCharacterForConn(candidate) ?? persistedCharacter;
            }
            return persistedCharacter;
        }

        private void ClearOnlinePosseMembership(uint posseId, int cooldownExpires, GameServer server)
        {
            if (server == null || posseId == 0)
                return;
            foreach (RRConnection candidate in server.AllConnectedConnections())
            {
                SavedCharacter activeCharacter = server.GetSavedCharacterForConn(candidate);
                if (activeCharacter == null || activeCharacter.posseId != posseId)
                    continue;
                activeCharacter.posseId = 0;
                activeCharacter.posseName = "";
                activeCharacter.posseRankId = 0;
                activeCharacter.posseJoinCooldown = cooldownExpires;
            }
        }

        private static bool HasIllegalNameChars(string s)
        {
            foreach (char c in s)
            {
                if (c < 0x20) return true;
                if (c == '<' || c == '>') return true;
                if (c == '"') return true;
            }
            return false;
        }

        private void SendGoldDeduction(RRConnection conn, uint amount,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, GameServer server)
        {
            if (conn.UnitContainerId == 0) { Debug.LogError("[POSSE-CREATE] skip gold packet (UnitContainerId=0)"); return; }
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.UnitContainerId);
            writer.WriteByte(0x20);
            writer.WriteInt32(-(int)amount);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x01);
            server.WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            sendCompressed(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[POSSE-CREATE] Sent RemoveCurrency {amount}");
        }

        public void SendProcessLeave(RRConnection conn, uint leaverCharId, uint status, string posseName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_LEAVE);
            writer.WriteUInt32(status);
            writer.WriteUInt32(leaverCharId);
            writer.WriteCString(posseName ?? "");
            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=ProcessLeave status=0x{status:X2} leaver={leaverCharId} name='{posseName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        public void SendProcessDisband(RRConnection conn, uint status, string posseName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_DISBAND);
            writer.WriteUInt32(status);
            writer.WriteCString(posseName ?? "");
            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=ProcessDisband status=0x{status:X2} name='{posseName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        public void SendInvitationNotification(RRConnection conn, string inviterName, string posseName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_INVITATION_NOTIFICATION);
            writer.WriteCString(inviterName ?? "");
            writer.WriteCString(posseName ?? "");
            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=InvitationNotification inviter='{inviterName}' posse='{posseName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        public void SendCreateResponse(RRConnection conn, uint status, string posseName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_CREATE);
            writer.WriteUInt32(status);
            writer.WriteCString(posseName ?? "");
            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=CreateResponse status=0x{status:X2} name='{posseName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        public void SendConnectionNotification(RRConnection conn, bool connected,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_CONNECTION_NOTIFICATION);
            writer.WriteByte((byte)(connected ? 0x01 : 0x00));
            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=ConnectionNotification connected={connected} hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        private const byte DIRTY_BASIC      = 0x02;
        private const byte DIRTY_RANKS      = 0x04;
        private const byte DIRTY_MEMBERS    = 0x08;
        private const byte DIRTY_PROPERTIES = 0x10;
        private const byte DIRTY_ACHIEVEMENTS = 0x20;

        public void SendCachedPosseUpdate(RRConnection conn, uint localPlayerCharId, byte dirtyFlags,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_UPDATE_CACHED_POSSE);
            writer.WriteUInt32(localPlayerCharId);
            writer.WriteByte(dirtyFlags);
            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=UpdateCachedPosse charId={localPlayerCharId} flags=0x{dirtyFlags:X2} hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        public void SendCachedPosseFull(RRConnection conn, uint localPlayerCharId, PosseRecord posse,
            System.Collections.Generic.List<(uint charId, string name, bool isFounder)> members,
            Action<RRConnection, byte, byte, byte[]> sendCompressed,
            GameServer server = null)
        {
            var onlineCharIds = new System.Collections.Generic.HashSet<uint>();
            if (server != null)
            {
                foreach (var memberConn in server.AllConnectedConnections())
                {
                    var gc = server.GetSelectedCharacter(memberConn.LoginName);
                    if (gc != null) onlineCharIds.Add(gc.Id);
                }
            }
            var writer = new LEWriter();
            writer.WriteByte(CHANNEL);
            writer.WriteByte(MSG_PROCESS_UPDATE_CACHED_POSSE);
            writer.WriteUInt32(localPlayerCharId);

            var props = new System.Collections.Generic.List<(string key, string val)>();
            props.Add(("0", posse.Description ?? ""));
            props.Add(("1", posse.Motd ?? ""));

            byte flags = (byte)(DIRTY_BASIC | DIRTY_RANKS | DIRTY_MEMBERS);
            if (props.Count > 0) flags |= DIRTY_PROPERTIES;
            writer.WriteByte(flags);

            writer.WriteUInt64(posse.Id);
            writer.WriteUInt32((uint)members.Count);
            writer.WriteCString(posse.Name ?? "");
            writer.WriteUInt64(posse.FounderCharacterId);
            writer.WriteByte(0x01);

            const ulong HIGH_RANK_PERMS = 0xFFFFFFFFFFFFFFFFUL;
            const ulong LOW_RANK_PERMS  = 0x0000000000000000UL;
            writer.WriteUInt32(10);
            for (uint rid = 1; rid <= 10; rid++)
            {
                writer.WriteUInt32(rid);
                writer.WriteCString("");
                writer.WriteUInt64(rid >= 8 ? HIGH_RANK_PERMS : LOW_RANK_PERMS);
            }

            writer.WriteUInt32((uint)members.Count);
            foreach (var member in members)
            {
                var memberSaved = CharacterRepository.GetCharacter(member.charId);
                uint memberRankId = member.isFounder ? 10u
                    : (memberSaved != null && memberSaved.posseRankId >= 1 && memberSaved.posseRankId <= 9
                        ? (uint)memberSaved.posseRankId : 1u);

                writer.WriteByte(0x01);
                writer.WriteUInt64(member.charId);
                writer.WriteUInt32(memberRankId);
                writer.WriteCString(member.name ?? "");
                writer.WriteUInt64(member.isFounder ? 0xFFFFFFFFFFFFFFFFUL : 0UL);

                var memberProps = new System.Collections.Generic.List<(string key, string val)>();
                memberProps.Add(("5", member.name ?? ""));
                bool memberIsOnline = server == null ? true : onlineCharIds.Contains(member.charId);
                memberProps.Add(("0", memberIsOnline ? "1" : "0"));
                if (memberSaved != null)
                {
                    memberProps.Add(("4", memberSaved.level.ToString()));
                    string zone = memberSaved.currentZoneName ?? "";
                    memberProps.Add(("2", zone));
                    memberProps.Add(("3", zone));
                }

                writer.WriteUInt32((uint)memberProps.Count);
                foreach (var propertyEntry in memberProps)
                {
                    writer.WriteCString(propertyEntry.key);
                    writer.WriteCString(propertyEntry.val);
                }
            }

            if (props.Count > 0)
            {
                writer.WriteUInt32((uint)props.Count);
                foreach (var propertyEntry in props)
                {
                    writer.WriteCString(propertyEntry.key);
                    writer.WriteCString(propertyEntry.val);
                }
            }

            byte[] packet = writer.ToArray();
            Debug.LogError($"[POSSE] send=CachedPosseFull posseId={posse.Id} name='{posse.Name}' members={members.Count} props={props.Count} hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }
    }
}
