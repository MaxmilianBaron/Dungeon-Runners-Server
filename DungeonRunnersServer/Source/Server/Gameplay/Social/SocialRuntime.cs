using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Database;
using DungeonRunners.Gameplay;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public class SocialRuntime
    {
        private static SocialRuntime _instance;
        public static SocialRuntime Instance => _instance ??= new SocialRuntime();

        private Dictionary<string, List<string>> _friends = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> _ignores = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _busyFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _publicityFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _charNameToLogin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, RRConnection> _onlineUsers = new Dictionary<string, RRConnection>(StringComparer.OrdinalIgnoreCase);

        public SocialRuntime()
        {
            EnsureSocialTables();
            LoadSocialData();
        }


        public void PlayerOnline(string loginName, string characterName, RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed = null)
        {
            _onlineUsers[loginName] = conn;
            _charNameToLogin[characterName] = loginName;

            if (!_friends.ContainsKey(characterName))
                _friends[characterName] = new List<string>();
            if (!_ignores.ContainsKey(characterName))
                _ignores[characterName] = new List<string>();

            DRLog.Social($"Player online: {characterName} ({loginName}) total={_onlineUsers.Count}");

            if (sendCompressed != null)
            {
                NotifyFriendsOfStatus(loginName, characterName, 3, sendCompressed);

                foreach (var onlineUserEntry in _onlineUsers)
                {
                    if (onlineUserEntry.Value != null && onlineUserEntry.Value.IsConnected && onlineUserEntry.Value.ConnId != conn.ConnId)
                    {
                        DRLog.Social($"  Auto-pushing who list to {onlineUserEntry.Key} (new player joined)");
                        SendWhoList(onlineUserEntry.Value, sendCompressed);
                    }
                }
            }
        }

        public void PlayerOffline(string loginName, string characterName, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            DRLog.Social($"PlayerOffline called: login='{loginName}' char='{characterName}' onlineUsers.Count={_onlineUsers.Count} containsKey={_onlineUsers.ContainsKey(loginName)}");

            NotifyFriendsOfStatus(loginName, characterName, 4, sendCompressed);

            bool removed = _onlineUsers.Remove(loginName);
            bool charRemoved = _charNameToLogin.Remove(characterName);

            DRLog.Social($"Player offline: {characterName} ({loginName}) removed={removed} charRemoved={charRemoved} remaining={_onlineUsers.Count}");

            foreach (var onlineUserEntry in _onlineUsers)
                DRLog.Social($"  Still online: '{onlineUserEntry.Key}' connected={onlineUserEntry.Value?.IsConnected}");

            foreach (var onlineUserEntry in _onlineUsers)
            {
                if (onlineUserEntry.Value != null && onlineUserEntry.Value.IsConnected)
                {
                    DRLog.Social($"  Auto-pushing who list to {onlineUserEntry.Key}");
                    SendWhoList(onlineUserEntry.Value, sendCompressed);
                }
            }
        }

        public void ForceRemoveOnline(string loginName)
        {
            DRLog.Social($"ForceRemoveOnline: login='{loginName}' containsKey={_onlineUsers.ContainsKey(loginName)}");

            _onlineUsers.Remove(loginName);

            string charToRemove = null;
            foreach (var characterLoginEntry in _charNameToLogin)
            {
                if (characterLoginEntry.Value.Equals(loginName, StringComparison.OrdinalIgnoreCase))
                {
                    charToRemove = characterLoginEntry.Key;
                    break;
                }
            }
            if (charToRemove != null)
                _charNameToLogin.Remove(charToRemove);

            DRLog.Social($"Force removed online user: {loginName} remaining={_onlineUsers.Count}");
        }

        public void HandleMessage(RRConnection conn, byte messageType, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string hex = data != null ? BitConverter.ToString(data, 0, Math.Min(data.Length, 40)) : "null";
            DRLog.Social($"== INCOMING ch=0x0C type=0x{messageType:X2} len={data?.Length ?? 0} from={conn.LoginName} hex={hex}");

            switch (messageType)
            {
                case 0x00:
                    DRLog.Social("-> Client sent CONNECT (0x00)");
                    HandleConnect(conn, sendCompressed);
                    break;
                case 0x01:
                    DRLog.Social("-> Client sent REQUEST_ROSTERS (0x01)");
                    HandleRequestRosters(conn, sendCompressed);
                    break;
                case 0x03:
                    DRLog.Social($"-> Client sent ADD_CONTACT (0x03)");
                    HandleAddContact(conn, data, sendCompressed);
                    break;
                case 0x04:
                    DRLog.Social($"-> Client sent REMOVE_CONTACT (0x04)");
                    HandleRemoveContact(conn, data, sendCompressed);
                    break;
                case 0x05:
                    DRLog.Social($"-> Client sent ADD_IGNORE (0x05)");
                    HandleAddIgnore(conn, data, sendCompressed);
                    break;
                case 0x06:
                    DRLog.Social($"-> Client sent REMOVE_IGNORE (0x06)");
                    HandleRemoveIgnore(conn, data, sendCompressed);
                    break;
                case 0x07:
                    DRLog.Social("-> Client sent REQUEST_WHO (0x07)");
                    HandleRequestUserList(conn, data, sendCompressed);
                    break;
                case 0x08:
                    DRLog.Social("-> Client sent CANCEL_USER_LIST (0x08) - acknowledged");
                    break;
                case 0x09:
                    DRLog.Social("-> Client sent SET_BUSY (0x09)");
                    HandleSetBusy(conn, data, sendCompressed);
                    break;
                case 0x0A:
                    DRLog.Social("-> Client sent JOIN_GROUP (0x0A)");
                    HandleRequestJoinGroup(conn, data, sendCompressed);
                    break;
                case 0x0B:
                    DRLog.Social("-> Client sent SET_PUBLICITY (0x0B)");
                    HandleSetFriendsPublicity(conn, data, sendCompressed);
                    break;
                default:
                    DRLog.Social($"-> UNHANDLED type 0x{messageType:X2}");
                    break;
            }
        }

        public void SendLoginSocialInit(RRConnection conn, string characterName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            SendConnectedMessage(conn, characterName, sendCompressed);

            string login = conn.LoginName ?? "";
            var friends = GetFriends(characterName);
            var ignores = GetIgnores(characterName);
            bool publicity = _publicityFlags.ContainsKey(login) && _publicityFlags[login];
            SendRosterMessage(conn, friends, ignores, publicity, sendCompressed);

            DRLog.Social($"Sent connected + roster for {characterName} (friends={friends.Count})");
        }


        private void HandleConnect(RRConnection conn, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            DRLog.Social($"HandleConnect: skipped (SendLoginSocialInit handles it)");
        }

        private void HandleRequestRosters(RRConnection conn, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string login = conn.LoginName ?? "";
            string charName = GetCharacterNameForLogin(login);
            var friends = GetFriends(charName);
            var ignores = GetIgnores(charName);
            bool publicity = _publicityFlags.ContainsKey(login) && _publicityFlags[login];

            SendRosterMessage(conn, friends, ignores, publicity, sendCompressed);
            DRLog.Social($"Sent roster: {friends.Count} friends, {ignores.Count} ignores");

            foreach (var friendName in friends)
            {
                string friendLogin = null;
                bool isOnline = _charNameToLogin.TryGetValue(friendName, out friendLogin)
                    && _onlineUsers.ContainsKey(friendLogin);

                SendRosterNotify(conn, friendName, 3, sendCompressed);

                if (isOnline)
                {
                    RRConnection friendConn = _onlineUsers[friendLogin];
                    SendRosterPropertyChanged(conn, friendName,
                        friendConn.CurrentZoneName ?? "unknown", friendConn.PlayerLevel, sendCompressed);
                    DRLog.Social($"  -> Friend '{friendName}' ONLINE, sent reason=3 + properties");
                }
                else
                {
                    SendRosterNotify(conn, friendName, 4, sendCompressed);
                    DRLog.Social($"  -> Friend '{friendName}' OFFLINE, sent reason=3 then reason=4");
                }
            }
        }

        private void HandleAddContact(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string friendName = ReadNameFromPayload(data);
            if (string.IsNullOrEmpty(friendName))
            {
                SendAddContactResponse(conn, friendName ?? "", 1, sendCompressed);
                return;
            }

            string login = conn.LoginName ?? "";
            string ownCharName = GetCharacterNameForLogin(login);
            var friends = GetFriends(ownCharName);
            if (!string.IsNullOrEmpty(ownCharName) && ownCharName.Equals(friendName, StringComparison.OrdinalIgnoreCase))
            {
                SendAddContactResponse(conn, friendName, 1, sendCompressed);
                return;
            }

            if (friends.Any(f => f.Equals(friendName, StringComparison.OrdinalIgnoreCase)))
            {
                SendAddContactResponse(conn, friendName, 2, sendCompressed);
                return;
            }

            friends.Add(friendName);
            SaveAddFriend(ownCharName, friendName);

            var ignoreList = GetIgnores(ownCharName);
            if (ignoreList.RemoveAll(i => i.Equals(friendName, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SaveRemoveIgnore(ownCharName, friendName);
                DRLog.Social($"  Also removed '{friendName}' from ignore list");
            }

            SendAddContactResponse(conn, friendName, 0, sendCompressed);
            DRLog.Social($"{conn.LoginName} added friend: {friendName}");

            string friendLogin = null;
            if (_charNameToLogin.TryGetValue(friendName, out friendLogin) && _onlineUsers.ContainsKey(friendLogin))
            {
                SendRosterNotify(conn, friendName, 3, sendCompressed);
                RRConnection friendConn = _onlineUsers[friendLogin];
                SendRosterPropertyChanged(conn, friendName,
                    friendConn.CurrentZoneName ?? "unknown", friendConn.PlayerLevel, sendCompressed);
                DRLog.Social($"  -> Friend {friendName} is online, sent reason=3 + properties");
            }
        }

        private void HandleRemoveContact(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string friendName = ReadNameFromPayload(data);
            if (string.IsNullOrEmpty(friendName))
            {
                SendRemoveContactResponse(conn, friendName ?? "", 1, sendCompressed);
                return;
            }

            string login = conn.LoginName ?? "";
            string charName = GetCharacterNameForLogin(login);
            var friends = GetFriends(charName);
            friends.RemoveAll(f => f.Equals(friendName, StringComparison.OrdinalIgnoreCase));
            SaveRemoveFriend(charName, friendName);

            SendRemoveContactResponse(conn, friendName, 0, sendCompressed);
            DRLog.Social($"{conn.LoginName} ({charName}) removed friend: {friendName}");
        }

        private void HandleAddIgnore(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string ignoreName = ReadNameFromPayload(data);
            if (string.IsNullOrEmpty(ignoreName))
            {
                SendAddIgnoreResponse(conn, ignoreName ?? "", 1, sendCompressed);
                return;
            }

            string login = conn.LoginName ?? "";
            string charName = GetCharacterNameForLogin(login);
            var ignores = GetIgnores(charName);

            if (ignores.Any(i => i.Equals(ignoreName, StringComparison.OrdinalIgnoreCase)))
            {
                SendAddIgnoreResponse(conn, ignoreName, 2, sendCompressed);
                return;
            }

            ignores.Add(ignoreName);
            SaveAddIgnore(charName, ignoreName);

            var friends = GetFriends(charName);
            if (friends.RemoveAll(f => f.Equals(ignoreName, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SaveRemoveFriend(charName, ignoreName);
                DRLog.Social($"  Also removed '{ignoreName}' from friends list");
            }

            SendAddIgnoreResponse(conn, ignoreName, 0, sendCompressed);
            DRLog.Social($"{conn.LoginName} ignored: {ignoreName}");
        }

        private void HandleRemoveIgnore(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string ignoreName = ReadNameFromPayload(data);
            if (string.IsNullOrEmpty(ignoreName))
            {
                SendRemoveIgnoreResponse(conn, ignoreName ?? "", 1, sendCompressed);
                return;
            }

            string login = conn.LoginName ?? "";
            string charName = GetCharacterNameForLogin(login);
            var ignores = GetIgnores(charName);
            ignores.RemoveAll(i => i.Equals(ignoreName, StringComparison.OrdinalIgnoreCase));
            SaveRemoveIgnore(charName, ignoreName);

            SendRemoveIgnoreResponse(conn, ignoreName, 0, sendCompressed);
            DRLog.Social($"{conn.LoginName} ({charName}) unignored: {ignoreName}");
        }

        private void HandleRequestUserList(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            SendWhoList(conn, sendCompressed);
            DRLog.Social($"Sent who list to {conn.LoginName}");
        }

        private void HandleSetBusy(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            bool busy = data != null && data.Length >= 1 && data[0] != 0;
            string login = conn.LoginName ?? "";
            _busyFlags[login] = busy;

            SendSetBusyResponse(conn, busy, sendCompressed);
            DRLog.Social($"{conn.LoginName} busy={busy}");
        }

        private void HandleRequestJoinGroup(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            if (data != null && data.Length >= 4)
            {
                var reader = new LEReader(data);
                uint groupId = reader.ReadUInt32();
                DRLog.Social($"{conn.LoginName} requested join group {groupId} - forwarding to GroupDirectory");
            }
        }

        private void HandleSetFriendsPublicity(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            bool pub = data != null && data.Length >= 1 && data[0] != 0;
            string login = conn.LoginName ?? "";
            _publicityFlags[login] = pub;

            SendFriendsPublicityResponse(conn, pub, sendCompressed);
            DRLog.Social($"{conn.LoginName} publicity={pub}");
        }


        private void SendConnectedMessage(RRConnection conn, string characterName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x00);
            writer.WriteCString(characterName);
            byte[] packet = writer.ToArray();
            DRLog.Social($"send=Connected opcode=0x00 name='{characterName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        private void SendRosterMessage(RRConnection conn, List<string> friends, List<string> ignores,
            bool publicity, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x01);

            writer.WriteByte(0x01);
            writer.WriteByte(0x01);

            writer.WriteUInt32((uint)ignores.Count);
            foreach (var name in ignores)
                writer.WriteCString(name);

            writer.WriteUInt32(0);

            byte[] packet = writer.ToArray();
            DRLog.Social($"send=Roster opcode=0x01 ignores={ignores.Count} hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        private void SendAddContactResponse(RRConnection conn, string name, uint status,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x02);
            writer.WriteCString(name);
            writer.WriteUInt32(status);
            sendCompressed(conn, 0x01, 0x0F, writer.ToArray());
        }

        private void SendRemoveContactResponse(RRConnection conn, string name, uint status,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x03);
            writer.WriteCString(name);
            writer.WriteUInt32(status);
            sendCompressed(conn, 0x01, 0x0F, writer.ToArray());
        }

        private void SendAddIgnoreResponse(RRConnection conn, string name, uint status,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x04);
            writer.WriteCString(name);
            writer.WriteUInt32(status);
            sendCompressed(conn, 0x01, 0x0F, writer.ToArray());
        }

        private void SendRemoveIgnoreResponse(RRConnection conn, string name, uint status,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x05);
            writer.WriteCString(name);
            writer.WriteUInt32(status);
            sendCompressed(conn, 0x01, 0x0F, writer.ToArray());
        }

        private void SendRosterNotify(RRConnection conn, string friendName, uint reason,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x06);
            writer.WriteCString(friendName);
            writer.WriteUInt32(reason);
            sendCompressed(conn, 0x01, 0x0F, writer.ToArray());
        }

        private void SendRosterPropertyChanged(RRConnection conn, string friendName,
            string zoneName, int level,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x07);
            writer.WriteCString(friendName);
            writer.WriteUInt32(2);
            writer.WriteCString("Level");
            writer.WriteCString(level.ToString());
            writer.WriteCString("World");
            writer.WriteCString(zoneName);
            byte[] packet = writer.ToArray();
            DRLog.Social($"send=PropertyChanged opcode=0x07 friend={friendName} level={level} world={zoneName}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        private void SendFriendsPublicityResponse(RRConnection conn, bool publicity,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x08);
            writer.WriteByte(publicity ? (byte)0x01 : (byte)0x00);
            sendCompressed(conn, 0x01, 0x0F, writer.ToArray());
        }

        private void SendWhoList(RRConnection conn, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var staleLogins = new List<string>();
            foreach (var onlineUserEntry in _onlineUsers)
            {
                if (onlineUserEntry.Value == null || !onlineUserEntry.Value.IsConnected)
                    staleLogins.Add(onlineUserEntry.Key);
            }
            foreach (var stale in staleLogins)
            {
                _onlineUsers.Remove(stale);
                string charToRemove = null;
                foreach (var characterLoginEntry in _charNameToLogin)
                {
                    if (characterLoginEntry.Value.Equals(stale, StringComparison.OrdinalIgnoreCase))
                    { charToRemove = characterLoginEntry.Key; break; }
                }
                if (charToRemove != null)
                    _charNameToLogin.Remove(charToRemove);
            }

            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x09);

            int entryCount = 0;
            var openGroups = new List<Group>();

            RRConnection deferredLeaderConn = null;
            string deferredLeaderName = null;
            uint deferredLeaderGroupId = 0;

            foreach (var onlineUserEntry in _onlineUsers)
            {
                string login = onlineUserEntry.Key;
                RRConnection otherConn = onlineUserEntry.Value;
                if (otherConn.ConnId == conn.ConnId) continue;
                if (!otherConn.IsConnected) continue;

                string charName = GetCharacterNameForLogin(login);
                if (string.IsNullOrEmpty(charName)) continue;

                string zoneName = otherConn.CurrentZoneName ?? "unknown";
                int level = otherConn.PlayerLevel;
                uint charSqlId = otherConn.CharSqlId;
                if (charSqlId == 0) charSqlId = (uint)(otherConn.ConnId + 1);

                uint groupId = 0;
                var playerGroup = GroupDirectory.Instance.GetGroupForConn(otherConn.ConnId);
                if (playerGroup != null && playerGroup.IsOpen)
                {
                    groupId = playerGroup.GroupId;
                    if (!openGroups.Contains(playerGroup))
                        openGroups.Add(playerGroup);

                    if (otherConn.ConnId == playerGroup.LeaderConnId)
                    {
                        deferredLeaderConn = otherConn;
                        deferredLeaderName = charName;
                        deferredLeaderGroupId = groupId;
                        continue;
                    }
                }

                writer.WriteUInt32(charSqlId);
                writer.WriteUInt32(charSqlId);
                writer.WriteCString(charName);
                writer.WriteCString(zoneName);
                writer.WriteUInt16((ushort)level);
                writer.WriteUInt32(groupId);
                writer.WriteByte(0x01);
                entryCount++;
            }

            {
                var selfGroup = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
                if (selfGroup != null && selfGroup.IsOpen)
                {
                    string selfName = GetCharacterNameForLogin(conn.LoginName ?? "");
                    uint selfSqlId = conn.CharSqlId;
                    if (selfSqlId == 0) selfSqlId = (uint)(conn.ConnId + 1);
                    if (!string.IsNullOrEmpty(selfName))
                    {
                        if (selfGroup.LeaderConnId == conn.ConnId)
                        {
                        }

                        writer.WriteUInt32(selfSqlId);
                        writer.WriteUInt32(selfSqlId);
                        writer.WriteCString(selfName);
                        writer.WriteCString(conn.CurrentZoneName ?? "unknown");
                        writer.WriteUInt16((ushort)conn.PlayerLevel);
                        writer.WriteUInt32(selfGroup.GroupId);
                        writer.WriteByte(0x01);
                        entryCount++;
                    }
                    if (!openGroups.Contains(selfGroup))
                        openGroups.Add(selfGroup);
                }
            }

            if (deferredLeaderConn != null)
            {
                uint lSqlId = deferredLeaderConn.CharSqlId;
                if (lSqlId == 0) lSqlId = (uint)(deferredLeaderConn.ConnId + 1);
                writer.WriteUInt32(lSqlId);
                writer.WriteUInt32(lSqlId);
                writer.WriteCString(deferredLeaderName);
                writer.WriteCString(deferredLeaderConn.CurrentZoneName ?? "unknown");
                writer.WriteUInt16((ushort)deferredLeaderConn.PlayerLevel);
                writer.WriteUInt32(deferredLeaderGroupId);
                writer.WriteByte(0x01);
                entryCount++;
            }

            writer.WriteUInt32(0x00000000);

            foreach (var group in openGroups)
            {
                writer.WriteUInt32(group.GroupId);
                writer.WriteByte(0x01);
                var leaderMember = group.Members.Find(member => member.ConnId == group.LeaderConnId);
                if (leaderMember != null)
                {
                    var leaderConnection = FindOnlineConnection(leaderMember.ConnId);
                    if (leaderConnection != null)
                    {
                        uint leaderSqlId = leaderConnection.CharSqlId;
                        if (leaderSqlId == 0) leaderSqlId = (uint)(leaderConnection.ConnId + 1);
                        writer.WriteUInt32(leaderSqlId);
                    }
                }
                foreach (var member in group.Members)
                {
                    if (member.ConnId == group.LeaderConnId) continue;
                    var memberConnection = FindOnlineConnection(member.ConnId);
                    if (memberConnection != null)
                    {
                        uint memberSqlId = memberConnection.CharSqlId;
                        if (memberSqlId == 0) memberSqlId = (uint)(memberConnection.ConnId + 1);
                        writer.WriteUInt32(memberSqlId);
                    }
                }
                writer.WriteUInt32(0x00000000);
            }
            writer.WriteUInt32(0x00000000);

            byte[] packet = writer.ToArray();
            DRLog.Social($"Who list: {entryCount} entries, {openGroups.Count} open groups, {packet.Length} bytes");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        private RRConnection FindOnlineConnection(int connId)
        {
            foreach (var onlineUserEntry in _onlineUsers)
            {
                if (onlineUserEntry.Value != null && onlineUserEntry.Value.ConnId == connId && onlineUserEntry.Value.IsConnected)
                    return onlineUserEntry.Value;
            }
            return null;
        }

        private void SendSetBusyResponse(RRConnection conn, bool busy,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x03);
            writer.WriteByte(0x0B);
            writer.WriteByte(busy ? (byte)0x01 : (byte)0x00);
            sendCompressed(conn, 0x01, 0x0F, writer.ToArray());
        }


        private void NotifyFriendsOfStatus(string loginName, string characterName, uint reason,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var friends = GetFriends(characterName);
            RRConnection playerConn = null;
            if (reason == 3)
                _onlineUsers.TryGetValue(loginName, out playerConn);

            foreach (var friendCharName in friends)
            {
                if (_charNameToLogin.TryGetValue(friendCharName, out string friendLogin))
                {
                    if (_onlineUsers.TryGetValue(friendLogin, out RRConnection friendConn))
                    {
                        if (friendConn.IsConnected)
                        {
                            var theirFriends = GetFriends(friendCharName);
                            if (theirFriends.Any(f => f.Equals(characterName, StringComparison.OrdinalIgnoreCase)))
                            {
                                SendRosterNotify(friendConn, characterName, reason, sendCompressed);
                                if (reason == 3 && playerConn != null)
                                {
                                    SendRosterPropertyChanged(friendConn, characterName,
                                        playerConn.CurrentZoneName ?? "unknown", playerConn.PlayerLevel, sendCompressed);
                                }
                                DRLog.Social($"Notified {friendCharName} that {characterName} is {(reason == 3 ? "online" : "offline")}");
                            }
                        }
                    }
                }
            }
        }

        public void NotifyFriendsZoneChange(string loginName, string characterName, string newZone,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            DRLog.Social($"NotifyFriendsZoneChange: {characterName} -> {newZone}");
            NotifyFriendsOfStatus(loginName, characterName, 3, sendCompressed);
        }

        public void PushWhoListToAll(Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            foreach (var onlineUserEntry in _onlineUsers)
            {
                if (onlineUserEntry.Value != null && onlineUserEntry.Value.IsConnected)
                    SendWhoList(onlineUserEntry.Value, sendCompressed);
            }
        }


        private List<string> GetFriends(string characterName)
        {
            if (!_friends.TryGetValue(characterName, out var list))
            {
                list = new List<string>();
                _friends[characterName] = list;
            }
            return list;
        }

        private List<string> GetIgnores(string characterName)
        {
            if (!_ignores.TryGetValue(characterName, out var list))
            {
                list = new List<string>();
                _ignores[characterName] = list;
            }
            return list;
        }

        private string ReadNameFromPayload(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                var reader = new LEReader(data);
                return reader.ReadCString();
            }
            catch
            {
                return null;
            }
        }

        private string GetCharacterName(RRConnection conn)
        {
            foreach (var characterLoginEntry in _charNameToLogin)
            {
                if (characterLoginEntry.Value.Equals(conn.LoginName, StringComparison.OrdinalIgnoreCase))
                    return characterLoginEntry.Key;
            }
            return conn.LoginName ?? "Unknown";
        }

        private string GetCharacterNameForLogin(string loginName)
        {
            if (string.IsNullOrEmpty(loginName)) return loginName ?? "";
            foreach (var characterLoginEntry in _charNameToLogin)
            {
                if (characterLoginEntry.Value.Equals(loginName, StringComparison.OrdinalIgnoreCase))
                    return characterLoginEntry.Key;
            }
            return loginName;
        }

        public void UpdateConnections(Dictionary<int, RRConnection> connections)
        {
            var stale = new List<string>();
            foreach (var onlineUserEntry in _onlineUsers)
            {
                if (!onlineUserEntry.Value.IsConnected)
                    stale.Add(onlineUserEntry.Key);
            }
            foreach (var key in stale)
                _onlineUsers.Remove(key);
        }


        private void EnsureSocialTables()
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS social_friends_v2 (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        character_name TEXT NOT NULL COLLATE NOCASE,
                        friend_name TEXT NOT NULL COLLATE NOCASE,
                        UNIQUE(character_name, friend_name))");
                    GameDatabase.ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS social_ignores_v2 (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        character_name TEXT NOT NULL COLLATE NOCASE,
                        ignore_name TEXT NOT NULL COLLATE NOCASE,
                        UNIQUE(character_name, ignore_name))");
                }
                DRLog.Social("Social tables ensured in SQLite");
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to create social tables: {ex.Message}");
            }
        }

        private void SaveAddFriend(string characterName, string friendName)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "INSERT OR IGNORE INTO social_friends_v2 (character_name, friend_name) VALUES (@char, @friend)",
                        ("@char", characterName), ("@friend", friendName));
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to save friend: {ex.Message}");
            }
        }

        private void SaveRemoveFriend(string characterName, string friendName)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM social_friends_v2 WHERE character_name = @char AND friend_name = @friend",
                        ("@char", characterName), ("@friend", friendName));
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to remove friend: {ex.Message}");
            }
        }

        private void SaveAddIgnore(string characterName, string ignoreName)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "INSERT OR IGNORE INTO social_ignores_v2 (character_name, ignore_name) VALUES (@char, @ignore)",
                        ("@char", characterName), ("@ignore", ignoreName));
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to save ignore: {ex.Message}");
            }
        }

        private void SaveRemoveIgnore(string characterName, string ignoreName)
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(connection,
                        "DELETE FROM social_ignores_v2 WHERE character_name = @char AND ignore_name = @ignore",
                        ("@char", characterName), ("@ignore", ignoreName));
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to remove ignore: {ex.Message}");
            }
        }

        private void LoadSocialData()
        {
            try
            {
                using (var connection = GameDatabase.GetConnection())
                {
                    using (var reader = GameDatabase.ExecuteReader(connection, "SELECT character_name, friend_name FROM social_friends_v2 ORDER BY character_name, friend_name"))
                    {
                        int count = 0;
                        while (reader.Read())
                        {
                            string charName = GameDatabase.GetString(reader, "character_name");
                            string friend = GameDatabase.GetString(reader, "friend_name");
                            if (!_friends.ContainsKey(charName))
                                _friends[charName] = new List<string>();
                            if (!_friends[charName].Any(f => f.Equals(friend, StringComparison.OrdinalIgnoreCase)))
                                _friends[charName].Add(friend);
                            count++;
                        }
                        DRLog.Social($"Loaded {count} friend entries from SQLite");
                    }

                    using (var reader = GameDatabase.ExecuteReader(connection, "SELECT character_name, ignore_name FROM social_ignores_v2 ORDER BY character_name, ignore_name"))
                    {
                        int count = 0;
                        while (reader.Read())
                        {
                            string charName = GameDatabase.GetString(reader, "character_name");
                            string ignore = GameDatabase.GetString(reader, "ignore_name");
                            if (!_ignores.ContainsKey(charName))
                                _ignores[charName] = new List<string>();
                            if (!_ignores[charName].Any(i => i.Equals(ignore, StringComparison.OrdinalIgnoreCase)))
                                _ignores[charName].Add(ignore);
                            count++;
                        }
                        DRLog.Social($"Loaded {count} ignore entries from SQLite");
                    }
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to load social data: {ex.Message}");
            }
        }
    }
}
