using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using DungeonRunners.Engine;

namespace DungeonRunners.Gameplay
{
    public class GroupDirectory
    {
        private static GroupDirectory _instance;
        public static GroupDirectory Instance => _instance ??= new GroupDirectory();

        private Dictionary<uint, Group> _groups = new Dictionary<uint, Group>();
        private readonly List<uint> _groupOrder = new List<uint>();
        private Dictionary<int, uint> _connToGroup = new Dictionary<int, uint>();
        private Dictionary<int, uint> _pendingInvites = new Dictionary<int, uint>();
        private Dictionary<string, uint> _loginToGroup = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private uint _nextGroupId = 1;


        public Group CreateGroup(int leaderConnId, string leaderName, string loginName = null)
        {
            LeaveGroup(leaderConnId);

            var group = new Group
            {
                GroupId = _nextGroupId++,
                LeaderConnId = leaderConnId,
                MonsterDifficulty = 0,
                InstanceSeed = GenerateInstanceSeed(),
                EntityManagerSeed = GenerateEntityManagerSeed(),
            };
            group.Members.Add(new GroupMember
            {
                ConnId = leaderConnId,
                Name = leaderName,
                LoginName = loginName ?? "",
                IsOnline = true,
                CurrentZoneName = "",
                PersonalMonsterDifficulty = group.MonsterDifficulty
            });

            _groups[group.GroupId] = group;
            _groupOrder.Add(group.GroupId);
            _connToGroup[leaderConnId] = group.GroupId;
            if (!string.IsNullOrEmpty(loginName))
                _loginToGroup[loginName] = group.GroupId;

            Debug.LogError($"[GROUP] Created group {group.GroupId}, leader={leaderName} (conn={leaderConnId}), layoutSeed=0x{group.InstanceSeed:X8} entityManagerSeed=0x{group.EntityManagerSeed:X8}");
            return group;
        }


        public bool InvitePlayer(int inviterConnId, int inviteeConnId)
        {
            var group = GetGroupForConn(inviterConnId);
            if (group == null)
            {
                Debug.LogError($"[GROUP] InvitePlayer: inviter {inviterConnId} not in a group");
                return false;
            }
            if (group.LeaderConnId != inviterConnId)
            {
                Debug.LogError($"[GROUP] InvitePlayer: {inviterConnId} is not leader");
                return false;
            }
            if (IsInGroup(inviteeConnId))
            {
                Debug.LogError($"[GROUP] InvitePlayer: {inviteeConnId} already in a group");
                return false;
            }
            if (group.Members.Count >= 5)
            {
                Debug.LogError($"[GROUP] InvitePlayer: group {group.GroupId} is full (5 max)");
                return false;
            }

            _pendingInvites[inviteeConnId] = group.GroupId;
            Debug.LogError($"[GROUP] Invite sent: group {group.GroupId} -> conn {inviteeConnId}");
            return true;
        }

        public Group AcceptInvite(int inviteeConnId, string inviteeName, string loginName = null)
        {
            if (!_pendingInvites.TryGetValue(inviteeConnId, out uint groupId))
            {
                Debug.LogError($"[GROUP] AcceptInvite: no pending invite for {inviteeConnId}");
                return null;
            }
            _pendingInvites.Remove(inviteeConnId);

            if (!_groups.TryGetValue(groupId, out var group))
            {
                Debug.LogError($"[GROUP] AcceptInvite: group {groupId} no longer exists");
                return null;
            }

            LeaveGroup(inviteeConnId);

            group.Members.Add(new GroupMember
            {
                ConnId = inviteeConnId,
                Name = inviteeName,
                LoginName = loginName ?? "",
                IsOnline = true,
                CurrentZoneName = "",
                PersonalMonsterDifficulty = group.MonsterDifficulty
            });
            _connToGroup[inviteeConnId] = groupId;
            if (!string.IsNullOrEmpty(loginName))
                _loginToGroup[loginName] = groupId;

            Debug.LogError($"[GROUP] {inviteeName} joined group {groupId} (now {group.Members.Count} members)");
            return group;
        }

        public void DeclineInvite(int inviteeConnId)
        {
            _pendingInvites.Remove(inviteeConnId);
            Debug.LogError($"[GROUP] Invite declined by conn {inviteeConnId}");
        }


        public void LeaveGroup(int connId)
        {
            if (!_connToGroup.TryGetValue(connId, out uint groupId)) return;
            if (!_groups.TryGetValue(groupId, out var group)) return;

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member != null && !string.IsNullOrEmpty(member.LoginName))
                _loginToGroup.Remove(member.LoginName);

            group.Members.RemoveAll(m => m.ConnId == connId);
            _connToGroup.Remove(connId);

            Debug.LogError($"[GROUP] Conn {connId} left group {groupId} ({group.Members.Count} remaining)");

            if (group.Members.Count == 0)
            {
                _groups.Remove(groupId);
                _groupOrder.Remove(groupId);
                Debug.LogError($"[GROUP] Group {groupId} disbanded (empty)");
            }
            else if (group.LeaderConnId == connId)
            {
                var newLeader = group.Members.Find(m => m.IsOnline) ?? group.Members[0];
                group.LeaderConnId = newLeader.ConnId;
                group.MonsterDifficulty = newLeader.PersonalMonsterDifficulty;
                Debug.LogError($"[GROUP] Leadership transferred to {newLeader.Name} (conn={group.LeaderConnId})");
            }
        }

        public bool DisconnectMember(int connId)
        {
            if (!_connToGroup.TryGetValue(connId, out uint groupId)) return false;
            if (!_groups.TryGetValue(groupId, out var group)) return false;

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member == null) return false;

            member.IsOnline = false;
            _connToGroup.Remove(connId);

            Debug.LogError($"[GROUP] Member {member.Name} disconnected from group {groupId} (kept in group)");
            return true;
        }

        public Group ReconnectMember(string loginName, int newConnId)
        {
            if (string.IsNullOrEmpty(loginName)) return null;
            if (!_loginToGroup.TryGetValue(loginName, out uint groupId)) return null;
            if (!_groups.TryGetValue(groupId, out var group)) return null;

            var member = group.Members.Find(m =>
                m.LoginName.Equals(loginName, StringComparison.OrdinalIgnoreCase));
            if (member == null) return null;

            int previousConnId = member.ConnId;
            _connToGroup.Remove(previousConnId);
            member.ConnId = newConnId;
            member.IsOnline = true;
            _connToGroup[newConnId] = groupId;
            if (group.LeaderConnId == previousConnId)
                group.LeaderConnId = newConnId;

            Debug.LogError($"[GROUP] Member {member.Name} reconnected to group {groupId} (conn={newConnId})");
            return group;
        }

        public bool SetLeader(int requestorConnId, int newLeaderConnId)
        {
            var group = GetGroupForConn(requestorConnId);
            if (group == null || group.LeaderConnId != requestorConnId) return false;
            if (!group.Members.Any(m => m.ConnId == newLeaderConnId)) return false;

            group.LeaderConnId = newLeaderConnId;
            var newLeader = group.Members.Find(m => m.ConnId == newLeaderConnId);
            if (newLeader != null)
                group.MonsterDifficulty = newLeader.PersonalMonsterDifficulty;
            Debug.LogError($"[GROUP] Leader changed to conn {newLeaderConnId} in group {group.GroupId} difficulty={group.MonsterDifficulty}");
            return true;
        }

        public bool SetMonsterDifficulty(int connId, byte difficulty, out bool personalOnly)
        {
            personalOnly = false;
            var group = GetGroupForConn(connId);
            if (group == null) return false;
            if (difficulty >= 4)
            {
                Debug.LogError($"[GROUP] Monster difficulty rejected diff={difficulty} conn={connId} group={group.GroupId}");
                return false;
            }

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member == null) return false;

            member.PersonalMonsterDifficulty = difficulty;
            if (group.LeaderConnId == connId)
            {
                group.MonsterDifficulty = difficulty;
                Debug.LogError($"[GROUP] Group difficulty set to {difficulty} for group {group.GroupId} leader={connId}");
            }
            else
            {
                personalOnly = true;
                Debug.LogError($"[GROUP] Personal difficulty set to {difficulty} for conn {connId} group {group.GroupId}; not active until leader");
            }

            return true;
        }

        public void ResetInstances(int connId)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;
            if (group.LeaderConnId != connId)
            {
                Debug.LogError($"[GROUP] ResetInstances: conn {connId} is not leader");
                return;
            }

            group.InstanceSeed = GenerateInstanceSeed();
            group.EntityManagerSeed = GenerateEntityManagerSeed();

            foreach (string zoneName in group.SpawnedZoneOrder)
            {
                ZoneSpawner.Instance.ResetZone($"{zoneName}_{group.GroupId}");
            }
            group.SpawnedZones.Clear();
            group.SpawnedZoneOrder.Clear();

            Debug.LogError($"[GROUP] Instances reset for group {group.GroupId}, newLayoutSeed=0x{group.InstanceSeed:X8} newEntityManagerSeed=0x{group.EntityManagerSeed:X8}");
        }


        public void UpdateMemberZone(int connId, string zoneName)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member != null)
            {
                member.CurrentZoneName = zoneName;
                Debug.LogError($"[GROUP] Member conn {connId} zone updated to '{zoneName}' in group {group.GroupId}");
            }
        }

        public void SetMemberOnline(int connId, bool online)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member != null)
                member.IsOnline = online;
        }


        public Group GetGroupForConn(int connId)
        {
            if (!_connToGroup.TryGetValue(connId, out uint groupId)) return null;
            _groups.TryGetValue(groupId, out var group);
            return group;
        }

        public bool IsInGroup(int connId) => _connToGroup.ContainsKey(connId);

        public List<Group> AllGroups()
        {
            var groups = new List<Group>(_groupOrder.Count);
            foreach (uint groupId in _groupOrder)
                if (_groups.TryGetValue(groupId, out Group group))
                    groups.Add(group);
            return groups;
        }

        public bool HasPendingInvite(int connId) => _pendingInvites.ContainsKey(connId);

        public Group GetPendingInviteGroup(int connId)
        {
            if (!_pendingInvites.TryGetValue(connId, out uint groupId)) return null;
            _groups.TryGetValue(groupId, out var group);
            return group;
        }

        public List<GroupMember> GetMembersInZone(int connId, string zoneName)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return new List<GroupMember>();
            return group.Members.Where(m => m.IsOnline &&
                string.Equals(m.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public List<GroupMember> GetOtherMembersInZone(int connId, string zoneName)
        {
            return GetMembersInZone(connId, zoneName).Where(m => m.ConnId != connId).ToList();
        }

        public uint GetGroupSeed(int connId)
        {
            var group = GetGroupForConn(connId);
            if (group == null || group.InstanceSeed == 0)
                throw new InvalidOperationException($"Missing group seed for connection {connId}");
            return group.InstanceSeed;
        }

        public bool IsZoneSpawnedForGroup(int connId, string zoneName)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return false;
            return group.SpawnedZones.Contains(zoneName);
        }

        public void MarkZoneSpawned(int connId, string zoneName)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;
            if (group.SpawnedZones.Add(zoneName))
                group.SpawnedZoneOrder.Add(zoneName);
        }

        private uint GenerateInstanceSeed()
        {
            byte[] bytes = new byte[4];
            uint seed;
            do
            {
                RandomNumberGenerator.Fill(bytes);
                seed = BitConverter.ToUInt32(bytes, 0);
            }
            while (seed == 0);
            return seed;
        }

        private uint GenerateEntityManagerSeed()
        {
            return GenerateInstanceSeed();
        }
    }


    public class Group
    {
        public uint GroupId;
        public int LeaderConnId;
        public List<GroupMember> Members = new List<GroupMember>();
        public byte MonsterDifficulty;
        public uint InstanceSeed;
        public uint EntityManagerSeed;
        public ulong LootRecipientCursor;
        public HashSet<string> SpawnedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> SpawnedZoneOrder = new List<string>();
        public bool IsOpen;
        public byte InviteMode = 3;
    }

    public class GroupMember
    {
        public int ConnId;
        public string Name;
        public string LoginName;
        public bool IsOnline;
        public string CurrentZoneName;
        public uint CharSqlId;
        public uint AvatarEntityId;
        public byte PersonalMonsterDifficulty;
    }
}
