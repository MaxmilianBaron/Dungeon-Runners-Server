using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using DungeonRunners.Engine;

namespace DungeonRunners.Networking
{
    public class ServerTcpListener
    {
        private TcpListener _listener;
        private bool _isRunning;
        private readonly Queue<TcpClient> _pendingClients = new Queue<TcpClient>();
        private readonly object _lock = new object();

        public event Action<TcpClient> OnClientConnected;

        public void Start(string ipAddress, int port)
        {
            if (_isRunning)
            {
                Debug.LogWarning("TCP Listener already running");
                return;
            }

            try
            {
                IPAddress ip = IPAddress.Parse(ipAddress);
                _listener = new TcpListener(ip, port);
                _listener.Start();
                _isRunning = true;

                Debug.Log($"TCP Listener started on {ipAddress}:{port}");

                System.Threading.ThreadPool.QueueUserWorkItem(_ => AcceptClientsLoop());
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start TCP listener: {ex}");
                throw;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _listener?.Stop();
            Debug.Log("TCP Listener stopped");
        }

        private void AcceptClientsLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (_listener.Pending())
                    {
                        TcpClient client = _listener.AcceptTcpClient();
                        
                        lock (_lock)
                        {
                            _pendingClients.Enqueue(client);
                        }

                        Core.MainThreadDispatcher.Enqueue(() =>
                        {
                            TcpClient pendingClient;
                            lock (_lock)
                            {
                                if (_pendingClients.Count > 0)
                                {
                                    pendingClient = _pendingClients.Dequeue();
                                    OnClientConnected?.Invoke(pendingClient);
                                }
                            }
                        });
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        Debug.LogError($"[TCP-LISTENER] accept state=failed message='{ex}'");
                    }
                }
            }
        }
    }
}
