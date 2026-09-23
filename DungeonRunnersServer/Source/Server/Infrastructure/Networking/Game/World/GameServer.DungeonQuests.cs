using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Gameplay;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private void PrepareDungeonQuestRooms(RRConnection conn, string zoneName)
        {
            if (conn == null || (!DungeonMazeSpawner.IsProceduralZone(zoneName) && !DungeonMazeSpawner.HasStaticEncounterData(zoneName))) return;
            var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var members = new List<RRConnection> { conn };
            var group = GroupDirectory.Instance.GetGroupForConn(conn.ConnId);
            if (group != null && conn.InstanceId == group.GroupId)
                foreach (var member in group.Members)
                {
                    RRConnection other = FindConnectionById(member.ConnId);
                    if (other != null && other.IsConnected && other.ConnId != conn.ConnId) members.Add(other);
                }
            foreach (RRConnection member in members)
            {
                var state = QuestManager.Instance.GetPlayerState(member.ConnId.ToString());
                IEnumerable<string> active = state?.ActiveQuests?.Select(quest => quest.QuestId)
                    ?? GetActiveCharacter(member)?.activeQuests?.Select(quest => quest.questId)
                    ?? Enumerable.Empty<string>();
                foreach (string quest in active) eligible.Add(quest);
            }
            var required = AuthoredWorldLayout.FindWorld(zoneName).Rooms.Select(room => room.GetString("RequiredQuest", ""))
                .Where(quest => !string.IsNullOrWhiteSpace(quest) && eligible.Contains(quest));
            ZoneSpawner.Instance.FreezeLayoutQuests(GetDungeonLayoutSeedKey(conn, zoneName), required, (byte)Math.Clamp((int)(GetActiveCharacter(conn)?.level ?? 1), 1, 110));
        }

        private void WriteDungeonQuestRooms(LEWriter writer, RRConnection conn, string zoneName)
        {
            IReadOnlyList<string> quests = ZoneSpawner.Instance.GetLayoutQuests(GetDungeonLayoutSeedKey(conn, zoneName));
            writer.WriteByte(checked((byte)quests.Count));
            foreach (string quest in quests)
            {
                writer.WriteByte(0xFF);
                writer.WriteCString(quest);
            }
        }
    }
}
