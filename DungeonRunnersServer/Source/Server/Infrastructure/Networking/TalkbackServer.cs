using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DungeonRunners.Core;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Talkback
{
    public class TalkbackServer
    {
        private static TalkbackServer _instance;
        public static TalkbackServer Instance => _instance ??= new TalkbackServer();

        public const int Port = 2604;
        public const byte MaxPlayers = 8;

        private TcpListener _listener;
        private readonly object _lock = new object();
        private readonly Dictionary<uint, List<TalkbackMember>> _sessions = new Dictionary<uint, List<TalkbackMember>>();

        private sealed class TalkbackMember
        {
            public ulong UserId;
            public byte Slot;
            public uint TalkGroupId;
            public NetworkStream Stream;
            public bool MemberFlag;
        }

        public Func<ulong, bool> ResolveMemberFlag;

        public void Start()
        {
            if (_listener != null) return;
            try
            {
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
                _listener.BeginAcceptTcpClient(OnAccept, null);
                StartupLog.Service("Talkback", $"0.0.0.0:{Port}");
            }
            catch (Exception ex)
            {
                StartupLog.Warning("Talkback", $"0.0.0.0:{Port} unavailable: {ex.Message}");
                _listener = null;
            }
        }

        private void OnAccept(IAsyncResult ar)
        {
            TcpClient client = null;
            try { client = _listener.EndAcceptTcpClient(ar); } catch { return; }
            try { _listener.BeginAcceptTcpClient(OnAccept, null); } catch { }
            if (client != null)
                new Thread(() => ServeClient(client)) { IsBackground = true, Name = "TalkbackSession" }.Start();
        }

        private void ServeClient(TcpClient client)
        {
            TalkbackMember member = null;
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                SendFrame(stream, BuildKeyMsg());
                if (ReadFrame(stream) == null) return;
                SendFrame(stream, Encoding.ASCII.GetBytes("ENC OK"));
                while (true)
                {
                    byte[] msg = ReadFrame(stream);
                    if (msg == null || msg.Length == 0) return;
                    switch (msg[0])
                    {
                        case 0x0A:
                            member = HandleLogin(stream, msg);
                            break;
                        case 0x0B:
                            if (member != null) RelayToOthers(member, Prefix(0x15, member.Slot, msg, 1));
                            break;
                        case 0x0F:
                            if (member != null) RelayToOthers(member, Prefix(0x1B, member.Slot, msg, 1));
                            break;
                        case 0x0D:
                            if (member != null) RelayToOthers(member, new byte[] { 0x19, member.Slot });
                            break;
                        case 0x0C:
                            if (member != null) RelayToOthers(member, new byte[] { 0x18, member.Slot, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
                            break;
                        case 0x0E:
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TALKBACK] session end message='{ex.Message}'");
            }
            finally
            {
                if (member != null) RemoveMember(member);
                try { client.Close(); } catch { }
            }
        }

        private TalkbackMember HandleLogin(NetworkStream stream, byte[] msg)
        {
            ParseLoginMsg(msg, out ulong userId, out uint talkGroupId);
            lock (_lock)
            {
                if (!_sessions.TryGetValue(talkGroupId, out var members))
                {
                    members = new List<TalkbackMember>();
                    _sessions[talkGroupId] = members;
                }
                members.RemoveAll(m => m.UserId == userId);
                byte slot = 0;
                while (members.Any(m => m.Slot == slot)) slot++;
                bool memberFlag = ResolveMemberFlag?.Invoke(userId) ?? true;
                var member = new TalkbackMember { UserId = userId, Slot = slot, TalkGroupId = talkGroupId, Stream = stream, MemberFlag = memberFlag };
                members.Add(member);

                SendFrame(stream, BuildLoginMsgAck(talkGroupId, slot, memberFlag));
                foreach (var existing in members)
                    SendFrame(stream, BuildAddPlayerMsg(existing.UserId, existing.Slot, existing.MemberFlag));
                foreach (var other in members)
                {
                    if (other == member) continue;
                    TrySendFrame(other.Stream, BuildAddPlayerMsg(member.UserId, member.Slot, member.MemberFlag));
                }
                Debug.LogError($"[TALKBACK] login userId=0x{userId:X8} group={talkGroupId} slot={slot} members={members.Count}");
                return member;
            }
        }

        private void RemoveMember(TalkbackMember member)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(member.TalkGroupId, out var members)) return;
                members.Remove(member);
                if (members.Count == 0)
                {
                    _sessions.Remove(member.TalkGroupId);
                    return;
                }
                var remove = BuildRemovePlayerMsg(member.UserId, member.Slot);
                foreach (var other in members)
                    TrySendFrame(other.Stream, remove);
                Debug.LogError($"[TALKBACK] remove userId=0x{member.UserId:X8} group={member.TalkGroupId} slot={member.Slot}");
            }
        }

        private void RelayToOthers(TalkbackMember sender, byte[] payload)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sender.TalkGroupId, out var members)) return;
                foreach (var other in members)
                {
                    if (other == sender) continue;
                    TrySendFrame(other.Stream, payload);
                }
            }
        }

        private static byte[] Prefix(byte msgId, byte slot, byte[] source, int sourceOffset)
        {
            var payload = new byte[source.Length - sourceOffset + 2];
            payload[0] = msgId;
            payload[1] = slot;
            Buffer.BlockCopy(source, sourceOffset, payload, 2, source.Length - sourceOffset);
            return payload;
        }

        private static void TrySendFrame(NetworkStream stream, byte[] payload)
        {
            try { SendFrame(stream, payload); } catch { }
        }

        private static void SendFrame(NetworkStream stream, byte[] payload)
        {
            var frame = new byte[payload.Length + 4];
            frame[0] = (byte)(payload.Length & 0xFF);
            frame[1] = (byte)((payload.Length >> 8) & 0xFF);
            frame[2] = (byte)((payload.Length >> 16) & 0xFF);
            frame[3] = (byte)((payload.Length >> 24) & 0xFF);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            lock (stream)
            {
                stream.Write(frame, 0, frame.Length);
            }
        }

        private static byte[] ReadFrame(NetworkStream stream)
        {
            var header = ReadExact(stream, 4);
            if (header == null) return null;
            int length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            if (length < 0 || length > 0xFFFFFF) return null;
            return ReadExact(stream, length);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        private static byte[] BuildKeyMsg()
        {
            var writer = new LEWriter();
            writer.WriteUInt32(8);
            writer.WriteUInt32(8);
            writer.WriteUInt32(8);
            for (int i = 0; i < 24; i++) writer.WriteByte(0x01);
            return writer.ToArray();
        }

        private static byte[] BuildLoginMsgAck(uint talkGroupId, byte ownSlot, bool memberFlag)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x14);
            writer.WriteByte(0x00);
            writer.WriteUInt32(talkGroupId);
            writer.WriteByte(ownSlot);
            writer.WriteByte(MaxPlayers);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0x00);
            writer.WriteUInt16(0);
            writer.WriteByte(memberFlag ? (byte)0x01 : (byte)0x00);
            return writer.ToArray();
        }

        private static byte[] BuildAddPlayerMsg(ulong userId, byte slot, bool memberFlag)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x16);
            writer.WriteUInt32(0);
            writer.WriteUInt32((uint)(userId & 0xFFFFFFFF));
            writer.WriteUInt32((uint)(userId >> 32));
            writer.WriteByte(slot);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt16(0);
            writer.WriteByte(memberFlag ? (byte)0x01 : (byte)0x00);
            return writer.ToArray();
        }

        private static byte[] BuildRemovePlayerMsg(ulong userId, byte slot)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x17);
            writer.WriteUInt32(0);
            writer.WriteUInt32((uint)(userId & 0xFFFFFFFF));
            writer.WriteUInt32((uint)(userId >> 32));
            writer.WriteByte(slot);
            return writer.ToArray();
        }

        private static void ParseLoginMsg(byte[] msg, out ulong userId, out uint talkGroupId)
        {
            userId = 0;
            talkGroupId = 0;
            int pos = 1;
            if (msg.Length < pos + 2) return;
            int codecLen = msg[pos] | (msg[pos + 1] << 8);
            pos += 2 + codecLen;
            pos += 4;
            if (msg.Length < pos + 8) return;
            for (int i = 7; i >= 0; i--) userId = (userId << 8) | msg[pos + i];
            pos += 8;
            if (msg.Length < pos + 4) return;
            talkGroupId = (uint)(msg[pos] | (msg[pos + 1] << 8) | (msg[pos + 2] << 16) | (msg[pos + 3] << 24));
        }
    }
}
