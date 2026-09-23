using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using DungeonRunners.Engine;
using DungeonRunners.Core;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public class AuthServer : MonoBehaviour
    {
        [SerializeField] private ServerConfig config;

        private ServerTcpListener _listener;
        private ServerTcpListener _queueListener;
        private List<AuthConnection> _connections = new List<AuthConnection>();

        private static string _pendingQueueUser = null;
        private static uint _pendingQueueToken = 0;
        private static string _pendingQueueAdvertisedServerIP = null;

        void Start()
        {
            if (config == null)
            {
                StartupLog.Failure("Auth", "ServerConfig is missing");
                return;
            }

            config.authServerIP = ServerSettings.GetString("authIP", config.authServerIP);
            config.authServerPort = ServerSettings.Get("authPort", config.authServerPort);
            config.gameServerIP = ServerSettings.GetString("gameIP", config.gameServerIP);
            config.gameServerPort = ServerSettings.Get("gamePort", config.gameServerPort);
            int queuePort = ServerSettings.Get("queuePort", 2606);

            _listener = new ServerTcpListener();
            _listener.OnClientConnected += OnClientConnected;
            _listener.Start(config.authServerIP, config.authServerPort);

            StartupLog.Service("Authentication", $"{config.authServerIP}:{config.authServerPort}");

            _queueListener = new ServerTcpListener();
            _queueListener.OnClientConnected += OnQueueClientConnected;
            _queueListener.Start(config.authServerIP, queuePort);
            StartupLog.Service("Queue", $"{config.authServerIP}:{queuePort}");
        }

        void OnDestroy()
        {
            _listener?.Stop();
            _queueListener?.Stop();

            foreach (var conn in _connections)
            {
                conn.Disconnect();
            }
            _connections.Clear();
        }

        void Update()
        {
        }

        public static void SetPendingQueueUser(string username, uint token = 0, string advertisedServerIP = null)
        {
            _pendingQueueUser = username;
            _pendingQueueToken = token;
            _pendingQueueAdvertisedServerIP = advertisedServerIP;
        }

        private void OnClientConnected(TcpClient client)
        {
            Debug.Log($"[AUTH] client connected {client.Client.RemoteEndPoint}");

            var connection = new AuthConnection(client, config);
            connection.OnDisconnected += () => OnConnectionDisconnected(connection);
            connection.StartReceiving(this);

            _connections.Add(connection);
        }

        private void OnConnectionDisconnected(AuthConnection connection)
        {
            Debug.Log("Auth client disconnected");
            _connections.Remove(connection);
        }

        private void OnQueueClientConnected(TcpClient client)
        {
            if (string.IsNullOrWhiteSpace(_pendingQueueUser))
            {
                Debug.LogError($"[QUEUE] Rejected unexpected queue connection from {client.Client.RemoteEndPoint}");
                try { client.Close(); } catch { }
                return;
            }

            string queueUser = _pendingQueueUser ?? "UNKNOWN";
            uint queueToken = _pendingQueueToken;
            string queueAdvertisedServerIP = _pendingQueueAdvertisedServerIP;
            _pendingQueueUser = null;
            _pendingQueueToken = 0;
            _pendingQueueAdvertisedServerIP = null;
            Debug.LogError($"[QUEUE] connection port=2606 user='{queueUser}' remote={client.Client.RemoteEndPoint}");

            var queueConn = new AuthConnection(client, config);
            queueConn.SetSessionToken(queueToken);
            queueConn.SetAdvertisedServerIP(queueAdvertisedServerIP);
            queueConn.StartQueueMode(this, queueUser);
            _connections.Add(queueConn);
        }
    }

    public class AuthConnection
    {
        private ClientConnection _client;
        private ServerConfig _config;
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private string _username;
        private uint _sessionToken;
        private string _advertisedServerIP;

        public void SetSessionToken(uint token)
        {
            _sessionToken = token;
        }

        public void SetAdvertisedServerIP(string advertisedServerIP)
        {
            if (!string.IsNullOrWhiteSpace(advertisedServerIP))
                _advertisedServerIP = advertisedServerIP;
        }
        private MonoBehaviour _coroutineRunner;
        private bool _handoffSent = false;

        public event Action OnDisconnected;

        public AuthConnection(TcpClient client, ServerConfig config)
        {
            _tcpClient = client;
            _stream = client.GetStream();
            _config = config;
            _client = new ClientConnection(client);
            _advertisedServerIP = ResolveAdvertisedServerIP(client, config.gameServerIP);
            Debug.LogError($"[AUTH-NET] remote={GetEndpointIP(client?.Client?.RemoteEndPoint) ?? "unknown"} local={GetEndpointIP(client?.Client?.LocalEndPoint) ?? "unknown"} configured={config.gameServerIP} advertised={_advertisedServerIP}");

            _client.OnDisconnected += () => OnDisconnected?.Invoke();
        }

        public void StartReceiving(MonoBehaviour coroutineRunner)
        {
            _coroutineRunner = coroutineRunner;

            SendWelcomePacket();

            coroutineRunner.StartCoroutine(ReceiveLoop());
        }

        public void Disconnect()
        {
            _client.Disconnect();
        }

        private void SendWelcomePacket()
        {
            byte[] welcomePacket = new byte[] { 3, 0, 0 };
            Debug.Log("[AUTH-TX] welcome 03-00-00");
            _stream.Write(welcomePacket, 0, welcomePacket.Length);
        }

        private IEnumerator ReceiveLoop()
        {
            byte[] buffer = new byte[4096];
            bool hasError = false;

            while (_tcpClient.Connected && !hasError)
            {
                int headerBytesRead = 0;
                while (headerBytesRead < 2 && _tcpClient.Connected)
                {
                    if (!_stream.DataAvailable)
                    {
                        yield return new WaitForSeconds(0.01f);
                        continue;
                    }

                    int bytesRead = _stream.Read(buffer, headerBytesRead, 2 - headerBytesRead);
                    if (bytesRead == 0)
                    {
                        hasError = true;
                        break;
                    }
                    headerBytesRead += bytesRead;
                }

                if (hasError || headerBytesRead < 2) break;

                ushort totalLen = (ushort)(buffer[0] | (buffer[1] << 8));
                if (totalLen < 4)
                {
                    Debug.LogWarning($"Bad frame length: {totalLen}");
                    break;
                }
                if (totalLen > buffer.Length)
                {
                    Debug.LogWarning($"Bad frame length too large: {totalLen}");
                    break;
                }

                int bytesToRead = totalLen - 2;
                int bytesReadTotal = 0;

                while (bytesReadTotal < bytesToRead && _tcpClient.Connected)
                {
                    if (!_stream.DataAvailable)
                    {
                        yield return new WaitForSeconds(0.01f);
                        continue;
                    }

                    int bytesRead = _stream.Read(buffer, 2 + bytesReadTotal, bytesToRead - bytesReadTotal);
                    if (bytesRead == 0)
                    {
                        hasError = true;
                        break;
                    }
                    bytesReadTotal += bytesRead;
                }

                if (hasError || bytesReadTotal < bytesToRead) break;

                byte[] rest = new byte[bytesReadTotal];
                Array.Copy(buffer, 2, rest, 0, bytesReadTotal);

                try
                {
                    byte[] plainBody = DecryptBlowfishEndian(rest, bytesReadTotal);

                    if (plainBody.Length > 0)
                    {
                        byte msgType = plainBody[0];
                        Debug.Log($"[AUTH-RX] type=0x{msgType:X2} bodyLen={plainBody.Length - 1}");

                        ProcessMessage(msgType, plainBody);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AUTH] process state=failed message='{ex.Message}'");
                    hasError = true;
                }
            }

            Debug.Log("Receive loop ended");
            OnDisconnected?.Invoke();
        }

        private byte[] DecryptBlowfishEndian(byte[] encryptedData, int encryptedSize)
        {
            try
            {
                int numBlocks = encryptedSize / 8;
                byte[] decrypted = new byte[encryptedSize];

                var blowfish = new BlowfishEncryption(_config.blowfishKey);

                for (int blockIndex = 0; blockIndex < numBlocks; blockIndex++)
                {
                    int start = blockIndex * 8;
                    byte[] block = new byte[8];
                    Array.Copy(encryptedData, start, block, 0, 8);

                    uint v1 = BitConverter.ToUInt32(block, 0);
                    uint v2 = BitConverter.ToUInt32(block, 4);

                    byte[] bigEndianBlock = new byte[8];
                    bigEndianBlock[0] = (byte)((v1 >> 24) & 0xFF);
                    bigEndianBlock[1] = (byte)((v1 >> 16) & 0xFF);
                    bigEndianBlock[2] = (byte)((v1 >> 8) & 0xFF);
                    bigEndianBlock[3] = (byte)(v1 & 0xFF);
                    bigEndianBlock[4] = (byte)((v2 >> 24) & 0xFF);
                    bigEndianBlock[5] = (byte)((v2 >> 16) & 0xFF);
                    bigEndianBlock[6] = (byte)((v2 >> 8) & 0xFF);
                    bigEndianBlock[7] = (byte)(v2 & 0xFF);

                    byte[] decryptedBlock = blowfish.Decrypt(bigEndianBlock);

                    uint ev1 = (uint)((decryptedBlock[0] << 24) | (decryptedBlock[1] << 16) | (decryptedBlock[2] << 8) | decryptedBlock[3]);
                    uint ev2 = (uint)((decryptedBlock[4] << 24) | (decryptedBlock[5] << 16) | (decryptedBlock[6] << 8) | decryptedBlock[7]);

                    byte[] littleEndianBlock = new byte[8];
                    littleEndianBlock[0] = (byte)(ev1 & 0xFF);
                    littleEndianBlock[1] = (byte)((ev1 >> 8) & 0xFF);
                    littleEndianBlock[2] = (byte)((ev1 >> 16) & 0xFF);
                    littleEndianBlock[3] = (byte)(ev1 >> 24);
                    littleEndianBlock[4] = (byte)(ev2 & 0xFF);
                    littleEndianBlock[5] = (byte)((ev2 >> 8) & 0xFF);
                    littleEndianBlock[6] = (byte)((ev2 >> 16) & 0xFF);
                    littleEndianBlock[7] = (byte)(ev2 >> 24);

                    Array.Copy(littleEndianBlock, 0, decrypted, start, 8);
                }

                return decrypted;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AUTH] decrypt state=failed message='{ex.Message}'");
                return new byte[0];
            }
        }

        private void ProcessMessage(byte msgType, byte[] data)
        {
            try
            {
                switch (msgType)
                {
                    case 0x00:
                        HandleLogin(data);
                        break;

                    case 0x02:
                        HandleAboutToPlay(data);
                        break;

                    case 0x05:
                        HandleServerListExt();
                        break;

                    default:
                        Debug.LogWarning($"Unknown message: 0x{msgType:X2}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AUTH] processMessage state=failed message='{ex.Message}'");
            }
        }

        private void HandleLogin(byte[] data)
        {
            try
            {
                if (data.Length < 31)
                {
                    Debug.LogWarning("Login data too short");
                    return;
                }

                var reader = new ByteReader(data);
                reader.ReadByte();

                byte[] loginBlock = reader.ReadBytes(24);
                byte[] tail6 = reader.ReadBytes(6);

                (string username, _) = DecodeLogin(loginBlock, tail6);

                Debug.Log($"[AUTH] login user={username}");

                string remoteIP = GetEndpointIP(_tcpClient?.Client?.RemoteEndPoint);
                if (!IsLoopbackIPv4(remoteIP))
                {
                    Debug.LogError($"[AUTH] passwordless local login rejected remote={remoteIP ?? "unknown"} user='{username}'");
                    SendLoginFail(0x02);
                    return;
                }

                Database.GameDatabase.Initialize();
                uint accountId = Database.AccountRepository.GetAccountId(username);
                if (accountId == 0)
                {
                    accountId = Database.AccountRepository.CreateLocalAccount(username);
                    Debug.Log($"[AUTH] local account created user='{username}' id={accountId}");
                }
                else
                {
                    Debug.Log($"[AUTH] local account found user='{username}' id={accountId}");
                }

                if (accountId == 0)
                {
                    Debug.LogError($"[AUTH] local account unavailable user='{username}'");
                    SendLoginFail(0x02);
                    return;
                }

                if (IsAccountBanned(username))
                {
                    Debug.LogError($"[AUTH] account banned user='{username}' send=BlockedAccount");
                    SendBlockedAccount();
                    return;
                }

                _username = username;
                _advertisedServerIP = ResolveAdvertisedServerIP(_tcpClient, _config.gameServerIP);
                SendLoginOk(accountId);
                SendServerList();
                Debug.Log($"[AUTH-TX] ServerListEx endpoint={_advertisedServerIP}:{_config.gameServerPort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AUTH] login state=failed message='{ex}'");
            }
        }

        private void HandleAboutToPlay(byte[] data)
        {
            var reader = new ByteReader(data);
            reader.ReadByte();

            uint lo = reader.ReadUInt32();
            uint hi = reader.ReadUInt32();
            byte serverId = reader.ReadByte();

            Debug.Log($"[AUTH] AboutToPlay lo=0x{lo:X8} hi=0x{hi:X8} serverId={serverId}");

            uint sessionToken = (uint)DateTime.Now.Ticks ^ 0x12345678;
            _sessionToken = sessionToken;
            Core.GlobalSessions.Store(sessionToken, _username ?? "unknown");

            Debug.Log($"[AUTH] session token=0x{sessionToken:X8} user='{_username}'");

            AuthServer.SetPendingQueueUser(_username, sessionToken, _advertisedServerIP);
            Debug.Log($"[QUEUE] username set user='{_username}'");

            SendHandoffToQueue(sessionToken, serverId);
            Debug.Log($"[AUTH-TX] HandoffToQueue user='{_username}' serverId={serverId}");
        }

        private void HandleServerListExt()
        {
            SendServerList();
        }

        private void SendLoginOk(uint accountId)
        {
            var writer = new ByteWriter();
            writer.WriteUInt32(0xFFEEFFEE);
            writer.WriteUInt32(0xAABBAABB);
            writer.WriteUInt32(0xDDCCDDCC);
            writer.WriteUInt32(0xBBCCBBCC);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0xFFFFFFFF);
            writer.WriteUInt32(0xFFFFFFFF);
            writer.WriteUInt32(0x00000000);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x01);
            writer.WriteByte(0x01);
            writer.WriteByte(0x01);
            Debug.Log($"[AUTH-TX] LoginOk payload={BitConverter.ToString(writer.ToArray())}");
            WriteAuthMessage(0x03, writer.ToArray());
            Debug.Log($"[AUTH-TX] LoginOk accountId={accountId}");
        }

        private bool IsAccountBanned(string username)
        {
            try
            {
                using (var connection = Database.GameDatabase.GetConnection())
                {
                    object result = Database.GameDatabase.ExecuteScalar(connection,
                        "SELECT is_banned FROM accounts WHERE username = @u",
                        ("@u", username));
                    if (result != null && result != DBNull.Value)
                        return Convert.ToInt32(result) != 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AUTH] banCheck state=failed message='{ex.Message}'");
            }
            return false;
        }

        private void SendBlockedAccount()
        {
            var writer = new ByteWriter();
            writer.WriteByte(0x00);
            WriteAuthMessage(0x02, writer.ToArray());
            Debug.LogError("[AUTH-TX] BlockedAccount type=0x02");
        }

        private void SendLoginFail(byte reason)
        {
            var writer = new ByteWriter();
            writer.WriteByte(reason);
            WriteAuthMessage(0x02, writer.ToArray());
            Debug.LogError($"[AUTH-TX] LoginFail reason=0x{reason:X2}");
        }

        private void SendServerList()
        {
            string advertisedIP = string.IsNullOrWhiteSpace(_advertisedServerIP)
                ? ResolveAdvertisedServerIP(_tcpClient, _config.gameServerIP)
                : _advertisedServerIP;
            uint ipInt = IPToUInt32(advertisedIP);

            var writer = new ByteWriter();
            byte serverId = 0x01;
            writer.WriteByte(0x01);
            writer.WriteByte(serverId);

            uint queuePort = (uint)ServerSettings.Get("queuePort", 2606);
            writer.WriteByte(serverId);
            writer.WriteUInt32(ipInt);
            writer.WriteUInt32(queuePort);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt16((ushort)Math.Max(0, QueueConnection.CurrentPlayers));
            writer.WriteUInt16((ushort)Math.Max(1, QueueConnection.MaxPlayers));
            writer.WriteByte(0x01);

            WriteAuthMessage(0x04, writer.ToArray());
            Debug.Log($"[AUTH-TX] ServerList count=1 id=1 endpoint={advertisedIP}:{queuePort}");
        }

        private void SendHandoffToQueue(uint playToken, byte serverId)
        {

            var writer = new ByteWriter();
            writer.WriteUInt32(playToken);
            writer.WriteUInt32(0x5678DEFA);
            writer.WriteByte(serverId);

            byte[] payload = writer.ToArray();
            Debug.Log($"[AUTH-TX] HandoffToQueue payload={BitConverter.ToString(payload)}");

            WriteAuthMessage(0x0C, payload);
            Debug.Log($"[AUTH-TX] HandoffToQueue serverId={serverId} user='{_username}'");
        }

        public void StartQueueMode(MonoBehaviour coroutineRunner, string username)
        {
            _username = username;
            if (string.IsNullOrWhiteSpace(_advertisedServerIP))
                _advertisedServerIP = ResolveAdvertisedServerIP(_tcpClient, _config.gameServerIP);
            coroutineRunner.StartCoroutine(QueueReceiveAndHandoff());
        }

        private uint _queuePeerId = 0;

        private IEnumerator QueueReceiveAndHandoff()
        {
            byte[] buffer = new byte[4096];

            try
            {
                _tcpClient.NoDelay = true;
                Debug.LogError($"[QUEUE] Connection ready, starting protocol...");
            }
            catch (Exception ex) { Debug.LogError($"[QUEUE] socketConfig state=failed message='{ex.Message}'"); }

            Debug.LogError($"[QUEUE] LAYER 1: Sending crypto init (server sends first)...");
            {
                var payload = new ByteWriter();
                payload.WriteUInt32(8);
                payload.WriteUInt32(8);
                payload.WriteUInt32(8);
                payload.WriteByte(0x53); payload.WriteByte(0x52);
                payload.WriteByte(0x56); payload.WriteByte(0x4B);
                payload.WriteByte(0x45); payload.WriteByte(0x59);
                payload.WriteByte(0x30); payload.WriteByte(0x31);
                payload.WriteByte(0x50); payload.WriteByte(0x55);
                payload.WriteByte(0x42); payload.WriteByte(0x4B);
                payload.WriteByte(0x45); payload.WriteByte(0x59);
                payload.WriteByte(0x53); payload.WriteByte(0x56);
                payload.WriteByte(0x53); payload.WriteByte(0x45);
                payload.WriteByte(0x43); payload.WriteByte(0x4B);
                payload.WriteByte(0x45); payload.WriteByte(0x59);
                payload.WriteByte(0x53); payload.WriteByte(0x56);
                byte[] payloadBytes = payload.ToArray();

                var frame = new ByteWriter();
                frame.WriteUInt32((uint)payloadBytes.Length);
                for (int byteIndex = 0; byteIndex < payloadBytes.Length; byteIndex++)
                    frame.WriteByte(payloadBytes[byteIndex]);
                byte[] frameBytes = frame.ToArray();

                _stream.Write(frameBytes, 0, frameBytes.Length);
                _stream.Flush();
                Debug.LogError($"[QUEUE-TX] Crypto init ({frameBytes.Length}b): {BitConverter.ToString(frameBytes)}");
            }

            float elapsed = 0f;
            bool gotCrypto = false;
            while (elapsed < 10f && !gotCrypto)
            {
                bool hasData = false;
                try { hasData = _stream != null && _tcpClient.Connected && _stream.DataAvailable; } catch { }

                if (hasData)
                {
                    int bytesRead = 0;
                    try { bytesRead = _stream.Read(buffer, 0, buffer.Length); } catch { }

                    if (bytesRead > 0)
                    {
                        byte[] raw = new byte[bytesRead];
                        Array.Copy(buffer, raw, bytesRead);
                        Debug.LogError($"[QUEUE-RX] Crypto response: {bytesRead}b: {BitConverter.ToString(raw)}");
                        Debug.LogError($"[QUEUE] Crypto exchange complete");

                        yield return new DungeonRunners.Engine.WaitForSeconds(0.1f);
                        {
                            byte[] encOk = System.Text.Encoding.ASCII.GetBytes("ENC OK");
                            var confirmFrame = new ByteWriter();
                            confirmFrame.WriteUInt32((uint)encOk.Length);
                            for (int byteIndex = 0; byteIndex < encOk.Length; byteIndex++)
                                confirmFrame.WriteByte(encOk[byteIndex]);
                            byte[] confirmBytes = confirmFrame.ToArray();
                            _stream.Write(confirmBytes, 0, confirmBytes.Length);
                            _stream.Flush();
                            Debug.LogError($"[QUEUE-TX] ENC OK ({confirmBytes.Length}b): {BitConverter.ToString(confirmBytes)}");
                        }

                        gotCrypto = true;
                    }
                }

                if (!gotCrypto)
                {
                    if ((int)(elapsed * 10) % 50 == 0 && elapsed > 0)
                        Debug.LogError($"[QUEUE] Still waiting for crypto response... {elapsed:F0}s");
                    elapsed += 0.05f;
                    yield return new DungeonRunners.Engine.WaitForSeconds(0.05f);
                }
            }

            if (!gotCrypto)
            {
                Debug.LogError($"[QUEUE] TIMEOUT waiting for crypto response");
                yield break;
            }


            Debug.LogError($"[QUEUE] LAYER 2: Waiting for queue login...");
            elapsed = 0f;
            bool gotLogin = false;
            while (elapsed < 10f && !gotLogin)
            {
                bool hasData = false;
                try { hasData = _stream != null && _tcpClient.Connected && _stream.DataAvailable; } catch { }

                if (hasData)
                {
                    int bytesRead = 0;
                    try { bytesRead = _stream.Read(buffer, 0, buffer.Length); } catch { }

                    if (bytesRead > 0)
                    {
                        byte[] raw = new byte[bytesRead];
                        Array.Copy(buffer, raw, bytesRead);
                        Debug.LogError($"[QUEUE-RX] Login: {bytesRead}b: {BitConverter.ToString(raw)}");

                        if (bytesRead >= 5)
                        {
                            uint frameLen = BitConverter.ToUInt32(raw, 0);
                            byte msgType = raw[4];
                            Debug.LogError($"[QUEUE-RX] frameLen={frameLen} msgType=0x{msgType:X2}");
                            if (msgType == 0x07 && bytesRead >= 13)
                            {
                                uint token = BitConverter.ToUInt32(raw, 5);
                                uint uid = BitConverter.ToUInt32(raw, 9);
                                Debug.LogError($"[QUEUE-RX] Queue login OK: token=0x{token:X8} uid=0x{uid:X8}");
                            }
                        }
                        gotLogin = true;
                    }
                }

                if (!gotLogin)
                {
                    elapsed += 0.05f;
                    yield return new DungeonRunners.Engine.WaitForSeconds(0.05f);
                }
            }

            if (!gotLogin)
                Debug.LogError("[QUEUE] no login received - continuing");

            yield return new DungeonRunners.Engine.WaitForSeconds(0.1f);
            float waitTime = 0f;
            while (true)
            {
                if (QueueConnection.HasCapacity)
                {
                    QueueConnection.PlayerConnected();
                    SendQueuePosition(1, 0);
                    Debug.LogError($"[QUEUE] capacity {QueueConnection.CurrentPlayers}/{QueueConnection.MaxPlayers} - handoff {_username}");
                    break;
                }

                SendQueuePosition(1, (uint)(waitTime * 1000));
                Debug.LogError($"[QUEUE] full {QueueConnection.CurrentPlayers}/{QueueConnection.MaxPlayers} - user={_username} wait={waitTime:F0}s");

                for (int w = 0; w < 20; w++)
                {
                    yield return new DungeonRunners.Engine.WaitForSeconds(0.1f);
                    waitTime += 0.1f;

                    try { if (!_tcpClient.Connected) yield break; } catch { yield break; }

                    if (QueueConnection.HasCapacity)
                        break;
                }
            }

            yield return new DungeonRunners.Engine.WaitForSeconds(0.3f);
            {
                try
                {
                    var ep = (System.Net.IPEndPoint)_tcpClient.Client.RemoteEndPoint;
                    string clientIP = ep.Address.IsIPv4MappedToIPv6
                        ? ep.Address.MapToIPv4().ToString()
                        : ep.Address.ToString();
                    QueueConnection.ExpectQueueFromIP(clientIP, _username);
                    Debug.LogError($"[QUEUE] Flagged IP {clientIP} for queue user {_username}");
                }
                catch (Exception ex) { Debug.LogError($"[QUEUE] flagIp state=failed message='{ex.Message}'"); }

                string advertisedIP = string.IsNullOrWhiteSpace(_advertisedServerIP)
                    ? ResolveAdvertisedServerIP(_tcpClient, _config.gameServerIP)
                    : _advertisedServerIP;
                uint ipLE = IPToUInt32(advertisedIP);
                uint port = (uint)_config.gameServerPort;

                var inner = new ByteWriter();
                inner.WriteByte(0x0E);
                inner.WriteUInt32(ipLE);
                inner.WriteUInt32(port);
                inner.WriteUInt32(_sessionToken);
                Debug.LogError($"[QUEUE-TX] HandoffToGame token=0x{_sessionToken:X8}");
                inner.WriteUInt32(0);
                byte[] innerBytes = inner.ToArray();
                var frame = new ByteWriter();
                frame.WriteUInt32((uint)innerBytes.Length);
                for (int byteIndex = 0; byteIndex < innerBytes.Length; byteIndex++)
                    frame.WriteByte(innerBytes[byteIndex]);
                byte[] frameBytes = frame.ToArray();
                _stream.Write(frameBytes, 0, frameBytes.Length);
                _stream.Flush();
                Debug.LogError($"[QUEUE-TX] HandoffToGame bytes={frameBytes.Length} payload={BitConverter.ToString(frameBytes)} endpoint={advertisedIP}:{_config.gameServerPort}");
            }

            yield return new DungeonRunners.Engine.WaitForSeconds(1.0f);
            try
            {
                if (_stream != null && _tcpClient.Connected && _stream.DataAvailable)
                {
                    int extra = _stream.Read(buffer, 0, buffer.Length);
                    if (extra > 0)
                    {
                        byte[] raw = new byte[extra];
                        Array.Copy(buffer, raw, extra);
                        Debug.LogError($"[QUEUE] Post-handoff trailing data ({extra}b): {BitConverter.ToString(raw, 0, Math.Min(extra, 40))}");
                    }
                }
            }
            catch (Exception ex) { Debug.LogError($"[QUEUE] postHandoffRead state=failed message='{ex.Message}'"); }
            Debug.LogError($"[QUEUE] Queue connection complete for {_username}");
        }

        private void SendQueueCompressedA(byte queueMessageType, byte[] innerData)
        {
            try
            {
                byte[] compressed = DungeonRunners.Utilities.ZlibUtil.Deflate(innerData);

                var writer = new ByteWriter();
                writer.WriteByte(0x0A);
                writer.WriteByte((byte)(_queuePeerId & 0xFF));
                writer.WriteByte((byte)((_queuePeerId >> 8) & 0xFF));
                writer.WriteByte((byte)((_queuePeerId >> 16) & 0xFF));
                writer.WriteUInt32((uint)(compressed.Length + 7));
                writer.WriteByte(0x00);
                writer.WriteByte(queueMessageType);
                writer.WriteByte(0x00);
                writer.WriteUInt32((uint)innerData.Length);
                for (int byteIndex = 0; byteIndex < compressed.Length; byteIndex++)
                    writer.WriteByte(compressed[byteIndex]);

                byte[] frame = writer.ToArray();
                Debug.LogError($"[QUEUE-TX] CompressedA msgType={queueMessageType} frame ({frame.Length}b): {BitConverter.ToString(frame)}");
                _stream.Write(frame, 0, frame.Length);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QUEUE-TX] state=failed message='{ex.Message}'");
            }
        }



        private void WriteAuthMessage(byte serverMsgType, byte[] payload)
        {
            byte[] message = new byte[payload.Length + 1];
            message[0] = serverMsgType;
            Array.Copy(payload, 0, message, 1, payload.Length);

            int length = message.Length;
            int remainder = length % 8;
            if (remainder != 0)
            {
                int padding = 8 - remainder;
                byte[] padded = new byte[length + padding];
                Array.Copy(message, padded, length);
                message = padded;
                length += padding;
            }

            uint checksum = 0;
            for (int byteOffset = 0; byteOffset < length; byteOffset += 4)
            {
                uint value = (uint)(message[byteOffset] | (message[byteOffset + 1] << 8) | (message[byteOffset + 2] << 16) | (message[byteOffset + 3] << 24));
                checksum ^= value;
            }

            byte[] finalData = new byte[length + 8];
            Array.Copy(message, finalData, length);

            finalData[length] = (byte)(checksum & 0xFF);
            finalData[length + 1] = (byte)((checksum >> 8) & 0xFF);
            finalData[length + 2] = (byte)((checksum >> 16) & 0xFF);
            finalData[length + 3] = (byte)((checksum >> 24) & 0xFF);

            byte[] encrypted = EncryptBlowfishEndian(finalData, finalData.Length);

            int packetLength = encrypted.Length + 2;
            byte[] frame = new byte[packetLength];
            frame[0] = (byte)(packetLength & 0xFF);
            frame[1] = (byte)((packetLength >> 8) & 0xFF);
            Array.Copy(encrypted, 0, frame, 2, encrypted.Length);

            Debug.Log($"[AUTH-TX] encrypted type=0x{serverMsgType:X2} frameLen={frame.Length}");
            _stream.Write(frame, 0, frame.Length);
        }

        private byte[] EncryptBlowfishEndian(byte[] data, int length)
        {
            try
            {
                int numBlocks = length / 8;
                byte[] encrypted = new byte[length];

                var blowfish = new BlowfishEncryption(_config.blowfishKey);

                for (int blockIndex = 0; blockIndex < numBlocks; blockIndex++)
                {
                    int start = blockIndex * 8;
                    byte[] block = new byte[8];
                    Array.Copy(data, start, block, 0, 8);

                    uint v1 = BitConverter.ToUInt32(block, 0);
                    uint v2 = BitConverter.ToUInt32(block, 4);

                    byte[] bigEndianBlock = new byte[8];
                    bigEndianBlock[0] = (byte)((v1 >> 24) & 0xFF);
                    bigEndianBlock[1] = (byte)((v1 >> 16) & 0xFF);
                    bigEndianBlock[2] = (byte)((v1 >> 8) & 0xFF);
                    bigEndianBlock[3] = (byte)(v1 & 0xFF);
                    bigEndianBlock[4] = (byte)((v2 >> 24) & 0xFF);
                    bigEndianBlock[5] = (byte)((v2 >> 16) & 0xFF);
                    bigEndianBlock[6] = (byte)((v2 >> 8) & 0xFF);
                    bigEndianBlock[7] = (byte)(v2 & 0xFF);

                    byte[] encryptedBlock = blowfish.Encrypt(bigEndianBlock);

                    uint ev1 = (uint)((encryptedBlock[0] << 24) | (encryptedBlock[1] << 16) | (encryptedBlock[2] << 8) | encryptedBlock[3]);
                    uint ev2 = (uint)((encryptedBlock[4] << 24) | (encryptedBlock[5] << 16) | (encryptedBlock[6] << 8) | encryptedBlock[7]);

                    byte[] littleEndianBlock = new byte[8];
                    littleEndianBlock[0] = (byte)(ev1 & 0xFF);
                    littleEndianBlock[1] = (byte)((ev1 >> 8) & 0xFF);
                    littleEndianBlock[2] = (byte)((ev1 >> 16) & 0xFF);
                    littleEndianBlock[3] = (byte)(ev1 >> 24);
                    littleEndianBlock[4] = (byte)(ev2 & 0xFF);
                    littleEndianBlock[5] = (byte)((ev2 >> 8) & 0xFF);
                    littleEndianBlock[6] = (byte)((ev2 >> 16) & 0xFF);
                    littleEndianBlock[7] = (byte)(ev2 >> 24);

                    Array.Copy(littleEndianBlock, 0, encrypted, start, 8);
                }

                return encrypted;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AUTH] encrypt state=failed message='{ex.Message}'");
                return new byte[0];
            }
        }

        private (string user, string pass) DecodeLogin(byte[] block24, byte[] tail6)
        {
            using var des = new DESEncryption(_config.desKey);
            byte[] outbuf = des.Decrypt(block24);

            byte[] all = new byte[24 + 6];
            Array.Copy(outbuf, 0, all, 0, Math.Min(outbuf.Length, 24));
            Array.Copy(tail6, 0, all, 24, 6);

            string user = Encoding.ASCII.GetString(all, 0, 14).TrimEnd('\0');
            string pass = Encoding.ASCII.GetString(all, 14, 16).TrimEnd('\0');
            return (user, pass);
        }

        private void SendQueuePosition(uint position, uint waitMs)
        {
            try
            {
                var inner = new ByteWriter();
                inner.WriteByte(0x0D);
                inner.WriteUInt32(position);
                inner.WriteUInt32(waitMs);
                byte[] innerBytes = inner.ToArray();
                var frame = new ByteWriter();
                frame.WriteUInt32((uint)innerBytes.Length);
                for (int byteIndex = 0; byteIndex < innerBytes.Length; byteIndex++)
                    frame.WriteByte(innerBytes[byteIndex]);
                byte[] frameBytes = frame.ToArray();
                _stream.Write(frameBytes, 0, frameBytes.Length);
                _stream.Flush();
                Debug.LogError($"[QUEUE-TX] PositionInQueue pos={position} wait={waitMs}ms");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QUEUE-TX] positionInQueue state=failed message='{ex.Message}'");
            }
        }

        private static uint IPToUInt32(string ip)
        {
            var octets = ip.Split('.');
            return (uint)(byte.Parse(octets[0]) | (byte.Parse(octets[1]) << 8) | (byte.Parse(octets[2]) << 16) | (byte.Parse(octets[3]) << 24));
        }

        private static string ResolveAdvertisedServerIP(TcpClient client, string configuredIP)
        {
            string remoteIP = GetEndpointIP(client?.Client?.RemoteEndPoint);
            if (IsLoopbackIPv4(remoteIP))
                return "127.0.0.1";

            string configured = NormalizeIPv4(configuredIP);
            if (IsConcreteIPv4(configured))
            {
                if (!IsPrivateIPv4(configured) || IsAssignedLocalIPv4(configured))
                    return configured;
            }

            string localIP = GetEndpointIP(client?.Client?.LocalEndPoint);
            if (IsConcreteIPv4(localIP))
                return localIP;

            if (IsConcreteIPv4(configured))
                return configured;

            return "127.0.0.1";
        }

        private static string GetEndpointIP(System.Net.EndPoint endpoint)
        {
            try
            {
                var ep = endpoint as System.Net.IPEndPoint;
                if (ep == null)
                    return null;
                var address = ep.Address;
                if (address.IsIPv4MappedToIPv6)
                    address = address.MapToIPv4();
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    return address.ToString();
                if (System.Net.IPAddress.IsLoopback(address))
                    return "127.0.0.1";
            }
            catch { }
            return null;
        }

        private static string NormalizeIPv4(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return null;
            if (!System.Net.IPAddress.TryParse(ip.Trim(), out var address))
                return null;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return null;
            return address.ToString();
        }

        private static bool IsConcreteIPv4(string ip)
        {
            return !string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0";
        }

        private static bool IsLoopbackIPv4(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return false;
            return ip == "127.0.0.1" || ip.StartsWith("127.", StringComparison.Ordinal);
        }

        private static bool IsPrivateIPv4(string ip)
        {
            if (!System.Net.IPAddress.TryParse(ip, out var address))
                return false;
            byte[] b = address.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
        }

        private static bool IsAssignedLocalIPv4(string ip)
        {
            try
            {
                foreach (var address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                {
                    var a = address;
                    if (a.IsIPv4MappedToIPv6)
                        a = a.MapToIPv4();
                    if (a.AddressFamily == AddressFamily.InterNetwork && string.Equals(a.ToString(), ip, StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }
            return IsLoopbackIPv4(ip);
        }
    }
}
