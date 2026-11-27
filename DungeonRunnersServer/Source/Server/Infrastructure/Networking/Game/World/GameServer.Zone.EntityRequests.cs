using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private void HandleEntityRequest(RRConnection conn, LEReader reader, byte[] data)
        {
            if (reader.Remaining < 3)
            {
                reader.Skip(reader.Remaining);
                return;
            }

            ushort entityId = reader.ReadUInt16();
            byte requestType = reader.ReadByte();
            Debug.LogError($"[ENTITY-REQ] entity={entityId} type=0x{requestType:X2} remaining={reader.Remaining} from {conn.LoginName}");

            switch (requestType)
            {
                case 0x00:
                    HandleMinimumItemQualityRequest(conn, entityId, reader);
                    break;
                case 0x11:
                    HandleStatSpendRequest(conn, reader);
                    break;
                case 0x12:
                    HandleStatReturnRequest(conn, reader);
                    break;
                case 0x13:
                    HandleRespecRequest(conn);
                    break;
                case 0x15:
                    if (conn.Avatar != null && entityId == conn.Avatar.Id && (data == null || reader.Remaining == 0))
                        HandleClientRequestRespawn(conn);
                    break;
                default:
                    reader.Skip(reader.Remaining);
                    break;
            }
        }
    }
}
