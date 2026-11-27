using System;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private class TradeSession
        {
            public RRConnection Requester;
            public RRConnection Target;
            public bool Initiated;
            public bool RequesterAccepted;
            public bool TargetAccepted;
        }

        private readonly Dictionary<int, TradeSession> _tradeSessionsByConn = new Dictionary<int, TradeSession>();

        private string GetTradeCharacterName(RRConnection conn)
        {
            var savedChar = GetActiveCharacter(conn);
            return savedChar?.name ?? conn.LoginName;
        }

        private void EndTradeSession(TradeSession session, byte resultCode, RRConnection actor)
        {
            uint actorCharId = actor != null ? GetCharSqlId(actor) : 0;
            byte[] cancelled = TradePackets.BuildTradeCancelled(resultCode, actorCharId);
            if (session.Requester != null && session.Requester.IsConnected)
                SendToClient(session.Requester, cancelled);
            if (session.Target != null && session.Target.IsConnected)
                SendToClient(session.Target, cancelled);
            _tradeSessionsByConn.Remove(session.Requester.ConnId);
            _tradeSessionsByConn.Remove(session.Target.ConnId);
            Debug.LogError($"[TRADE] cancelled requester={session.Requester?.LoginName} target={session.Target?.LoginName} reason=0x{resultCode:X2} actor={actor?.LoginName}");
        }

        private void HandleTradeChannel(RRConnection conn, byte messageType, byte[] data)
        {
            var reader = (data != null && data.Length > 0) ? new LEReader(data) : null;

            switch (messageType)
            {
                case 0x00:
                    {
                        if (reader == null) break;
                        string targetName = reader.ReadCString();
                        var target = FindConnectionByName(targetName);
                        if (target == null)
                        {
                            foreach (var candidate in GetConnectionInsertionOrderSnapshot())
                            {
                                if (candidate == null || !candidate.IsConnected) continue;
                                if (string.Equals(GetTradeCharacterName(candidate), targetName, StringComparison.OrdinalIgnoreCase))
                                {
                                    target = candidate;
                                    break;
                                }
                            }
                        }
                        if (target == null || target == conn || !target.IsSpawned
                            || target.CurrentZoneGcType != conn.CurrentZoneGcType
                            || target.InstanceId != conn.InstanceId
                            || _tradeSessionsByConn.ContainsKey(conn.ConnId)
                            || _tradeSessionsByConn.ContainsKey(target.ConnId))
                        {
                            SendToClient(conn, TradePackets.BuildTradeRequestFailed(0x01));
                            Debug.LogError($"[TRADE] request rejected from={conn.LoginName} target='{targetName}' found={target != null}");
                            break;
                        }

                        var session = new TradeSession { Requester = conn, Target = target };
                        _tradeSessionsByConn[conn.ConnId] = session;
                        _tradeSessionsByConn[target.ConnId] = session;

                        uint requesterCharId = GetCharSqlId(conn);
                        uint targetCharId = GetCharSqlId(target);
                        string requesterName = GetTradeCharacterName(conn);
                        string resolvedTargetName = GetTradeCharacterName(target);
                        byte[] requested = TradePackets.BuildTradeRequested(requesterCharId, requesterName, targetCharId, resolvedTargetName);
                        SendToClient(conn, requested);
                        SendToClient(target, requested);
                        Debug.LogError($"[TRADE] requested from={requesterName}(0x{requesterCharId:X8}) target={resolvedTargetName}(0x{targetCharId:X8})");
                        break;
                    }

                case 0x01:
                    {
                        if (!_tradeSessionsByConn.TryGetValue(conn.ConnId, out var session) || session.Target != conn || session.Initiated)
                            break;
                        session.Initiated = true;
                        uint requesterCharId = GetCharSqlId(session.Requester);
                        uint targetCharId = GetCharSqlId(session.Target);
                        SendToClient(session.Requester, TradePackets.BuildTradeInitiated(targetCharId, GetTradeCharacterName(session.Target)));
                        SendToClient(session.Target, TradePackets.BuildTradeInitiated(requesterCharId, GetTradeCharacterName(session.Requester)));
                        Debug.LogError($"[TRADE] initiated requester={session.Requester.LoginName} target={session.Target.LoginName}");
                        break;
                    }

                case 0x02:
                case 0x03:
                    {
                        if (!_tradeSessionsByConn.TryGetValue(conn.ConnId, out var session))
                            break;
                        byte resultCode = reader != null && reader.Remaining >= 1 ? reader.ReadByte() : (byte)0x00;
                        EndTradeSession(session, resultCode, conn);
                        break;
                    }

                case 0x04:
                    {
                        if (!_tradeSessionsByConn.TryGetValue(conn.ConnId, out var session) || !session.Initiated)
                            break;
                        bool accepted = reader != null && reader.Remaining >= 1 && reader.ReadByte() != 0;
                        if (conn == session.Requester) session.RequesterAccepted = accepted;
                        else session.TargetAccepted = accepted;

                        byte[] acceptPacket = TradePackets.BuildTradeAccepted(GetCharSqlId(conn), accepted);
                        SendToClient(session.Requester, acceptPacket);
                        SendToClient(session.Target, acceptPacket);
                        Debug.LogError($"[TRADE] accept from={conn.LoginName} value={accepted} requesterAccepted={session.RequesterAccepted} targetAccepted={session.TargetAccepted}");

                        if (session.RequesterAccepted && session.TargetAccepted)
                        {
                            SendToClient(session.Requester, TradePackets.BuildTradeComplete(GetCharSqlId(session.Target)));
                            SendToClient(session.Target, TradePackets.BuildTradeComplete(GetCharSqlId(session.Requester)));
                            _tradeSessionsByConn.Remove(session.Requester.ConnId);
                            _tradeSessionsByConn.Remove(session.Target.ConnId);
                            Debug.LogError($"[TRADE] complete requester={session.Requester.LoginName} target={session.Target.LoginName}");
                        }
                        break;
                    }

                default:
                    {
                        string hex = data != null && data.Length > 0 ? BitConverter.ToString(data, 0, Math.Min(data.Length, 60)) : "";
                        Debug.LogError($"[TRADE] unhandled type=0x{messageType:X2} from={conn.LoginName} len={data?.Length ?? 0} hex={hex}");
                        break;
                    }
            }
        }

        private void CancelTradeOnDisconnect(RRConnection conn)
        {
            if (_tradeSessionsByConn.TryGetValue(conn.ConnId, out var session))
                EndTradeSession(session, 0x0D, conn);
        }
    }
}
