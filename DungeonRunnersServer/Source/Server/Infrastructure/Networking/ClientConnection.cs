using System;
using System.Collections;
using System.Net.Sockets;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public class ClientConnection
    {
        public TcpClient Client { get; private set; }
        public NetworkStream Stream { get; private set; }
        public bool IsConnected => Client?.Connected ?? false;
        public string RemoteEndPoint { get; private set; }

        private byte[] _receiveBuffer = new byte[8192];
        private bool _isReading;

        public event Action<byte[]> OnDataReceived;
        public event Action OnDisconnected;

        public ClientConnection(TcpClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Stream = client.GetStream();
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
        }

        public void StartReceiving(MonoBehaviour coroutineRunner)
        {
            if (_isReading) return;
            _isReading = true;
            coroutineRunner.StartCoroutine(ReceiveLoop());
        }

        private IEnumerator ReceiveLoop()
        {
            while (_isReading && IsConnected)
            {
                bool dataAvailable = false;
                byte[] receivedData = null;

                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (Stream.DataAvailable)
                        {
                            int bytesRead = Stream.Read(_receiveBuffer, 0, _receiveBuffer.Length);
                            if (bytesRead > 0)
                            {
                                receivedData = new byte[bytesRead];
                                Array.Copy(_receiveBuffer, receivedData, bytesRead);
                                dataAvailable = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[CLIENT-CONNECTION] read state=failed message='{ex}'");
                        _isReading = false;
                    }
                });

                yield return null;

                if (dataAvailable && receivedData != null)
                {
                    OnDataReceived?.Invoke(receivedData);
                }

                yield return new WaitForSeconds(0.01f);
            }

            OnDisconnected?.Invoke();
        }

        public void Send(byte[] data)
        {
            if (!IsConnected || data == null || data.Length == 0)
                return;

            try
            {
                Stream.Write(data, 0, data.Length);
                Stream.Flush();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CLIENT-CONNECTION] send state=failed message='{ex}'");
                Disconnect();
            }
        }

        public void Disconnect()
        {
            _isReading = false;
            
            try
            {
                Stream?.Close();
                Client?.Close();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CLIENT-CONNECTION] disconnect state=failed message='{ex}'");
            }
        }
    }
}
