using System;
using System.Collections.Generic;
using System.Net.Sockets;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public static class QueueConnection
    {
        private static readonly Dictionary<string, NetworkStream> _queueStreams
            = new Dictionary<string, NetworkStream>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _pendingQueueIPs
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Queue<(string username, TcpClient client)> _pendingQueueClients
            = new Queue<(string, TcpClient)>();

        private static readonly object _lock = new object();

        public static event Action<string> OnQueueStreamRegistered;

        public static int MaxPlayers = 1;
        private static int _currentPlayers = 0;

        public static int CurrentPlayers
        {
            get { lock (_lock) { return _currentPlayers; } }
        }

        public static bool HasCapacity
        {
            get { lock (_lock) { return _currentPlayers < MaxPlayers; } }
        }

        public static void PlayerConnected()
        {
            lock (_lock) { _currentPlayers++; }
            Debug.Log($"[QUEUE-CONNECTION] Player connected. Now: {_currentPlayers}/{MaxPlayers}");
        }

        public static void PlayerDisconnected()
        {
            lock (_lock) { if (_currentPlayers > 0) _currentPlayers--; }
            Debug.Log($"[QUEUE-CONNECTION] Player disconnected. Now: {_currentPlayers}/{MaxPlayers}");
        }

        public static void ExpectQueueFromIP(string ip, string username)
        {
            lock (_lock)
            {
                _pendingQueueIPs[ip] = username;
                Debug.Log($"[QUEUE-CONNECTION] Expecting queue from IP {ip} for {username}");
            }
        }

        public static string CheckAndConsumeQueueIP(string ip)
        {
            lock (_lock)
            {
                if (_pendingQueueIPs.TryGetValue(ip, out string username))
                {
                    _pendingQueueIPs.Remove(ip);
                    return username;
                }
                return null;
            }
        }

        public static List<string> GetPendingIPs()
        {
            lock (_lock)
            {
                return new List<string>(_pendingQueueIPs.Keys);
            }
        }

        private static readonly Dictionary<string, uint> _queuePeerIds
            = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        private static readonly Queue<(string username, byte messageType, byte[] data)> _incomingSocialQueue
            = new Queue<(string, byte, byte[])>();

        public static void RegisterSocialStream(string username, NetworkStream stream, uint peerIdFromClient)
        {
            lock (_lock)
            {
                _queueStreams[username] = stream;
                _queuePeerIds[username] = peerIdFromClient;
                Debug.LogError($"[QUEUE-CONNECTION] Registered social stream for {username} peerId=0x{peerIdFromClient:X6}");
            }
            OnQueueStreamRegistered?.Invoke(username);
        }

        public static void EnqueueIncomingSocial(string username, byte messageType, byte[] data)
        {
            lock (_lock)
            {
                _incomingSocialQueue.Enqueue((username, messageType, data));
                Debug.LogError($"[QUEUE-CONNECTION] Enqueued incoming social for {username} type=0x{messageType:X2} len={data?.Length ?? 0}");
            }
        }

        public static List<(string username, byte messageType, byte[] data)> DrainIncomingSocial()
        {
            lock (_lock)
            {
                if (_incomingSocialQueue.Count == 0)
                    return null;

                var list = new List<(string, byte, byte[])>();
                while (_incomingSocialQueue.Count > 0)
                    list.Add(_incomingSocialQueue.Dequeue());
                return list;
            }
        }

        public static void SendOnQueueStream(string username, byte dest, byte messageType, byte[] innerData)
        {
            lock (_lock)
            {
                if (!_queueStreams.TryGetValue(username, out var stream))
                {
                    Debug.LogError($"[QUEUE-TX-SOCIAL] No queue stream for {username} - cannot send social");
                    return;
                }
                if (!_queuePeerIds.TryGetValue(username, out var peerId))
                {
                    Debug.LogError($"[QUEUE-TX-SOCIAL] No peerId for {username} - cannot send social");
                    return;
                }

                try
                {
                    byte[] compressed = ZlibUtil.Deflate(innerData);

                    var packet = new ByteWriter();
                    packet.WriteByte(0x0A);
                    packet.WriteByte((byte)(peerId & 0xFF));
                    packet.WriteByte((byte)((peerId >> 8) & 0xFF));
                    packet.WriteByte((byte)((peerId >> 16) & 0xFF));
                    packet.WriteUInt32((uint)(compressed.Length + 7));
                    packet.WriteByte(dest);
                    packet.WriteByte(messageType);
                    packet.WriteByte(0x00);
                    packet.WriteUInt32((uint)innerData.Length);
                    for (int byteIndex = 0; byteIndex < compressed.Length; byteIndex++)
                        packet.WriteByte(compressed[byteIndex]);
                    byte[] compressedA = packet.ToArray();

                    var frame = new ByteWriter();
                    frame.WriteUInt32((uint)compressedA.Length);
                    for (int byteIndex = 0; byteIndex < compressedA.Length; byteIndex++)
                        frame.WriteByte(compressedA[byteIndex]);
                    byte[] frameBytes = frame.ToArray();

                    stream.Write(frameBytes, 0, frameBytes.Length);
                    stream.Flush();
                    Debug.LogError($"[QUEUE-TX-SOCIAL] Sent to {username}: inner={innerData.Length}b compressed={compressed.Length}b frame={frameBytes.Length}b");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[QUEUE-TX-SOCIAL] send user='{username}' state=failed message='{ex.Message}'");
                    _queueStreams.Remove(username);
                    _queuePeerIds.Remove(username);
                }
            }
        }

        public static void RegisterQueueStream(string username, NetworkStream stream)
        {
            lock (_lock)
            {
                _queueStreams[username] = stream;
                Debug.Log($"[QUEUE-CONNECTION] Registered queue stream for {username}");
            }
            OnQueueStreamRegistered?.Invoke(username);
        }

        public static NetworkStream GetQueueStream(string username)
        {
            lock (_lock)
            {
                if (username != null && _queueStreams.TryGetValue(username, out var stream))
                    return stream;
                return null;
            }
        }

        public static bool HasQueueStream(string username)
        {
            lock (_lock)
            {
                return username != null && _queueStreams.ContainsKey(username);
            }
        }

        public static void RemoveQueueStream(string username)
        {
            lock (_lock)
            {
                if (username != null)
                {
                    if (_queueStreams.Remove(username))
                        Debug.Log($"[QUEUE-CONNECTION] Removed queue stream for {username}");
                    _queuePeerIds.Remove(username);
                }
            }
        }

        public static void EnqueueForAuthHandling(string username, TcpClient client)
        {
            lock (_lock)
            {
                _pendingQueueClients.Enqueue((username, client));
                Debug.Log($"[QUEUE-CONNECTION] Queued TcpClient for auth handling: {username}");
            }
        }

        public static (string username, TcpClient client)? DequeueForAuthHandling()
        {
            lock (_lock)
            {
                if (_pendingQueueClients.Count > 0)
                    return _pendingQueueClients.Dequeue();
                return null;
            }
        }
    }
}
