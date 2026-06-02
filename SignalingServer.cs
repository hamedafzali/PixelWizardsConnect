using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenAutomationApp
{
    public class SignalingServer : IDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cancellationToken;
        private readonly ConcurrentDictionary<string, HostInfo> _hosts = new ConcurrentDictionary<string, HostInfo>();
        private readonly Random _random = new Random();

        public event Action<string>? HostRegistered;
        public event Action<string>? HostConnected;
        public event Action<Exception>? Error;

        public bool IsRunning { get; private set; }

        public async Task StartAsync(int port = 9999)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                IsRunning = true;
                _cancellationToken = new CancellationTokenSource();

                await Task.Run(() => AcceptConnections(_cancellationToken.Token));
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                Stop();
            }
        }

        private async Task AcceptConnections(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener!.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClient(client, token));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex);
                }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken token)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                try
                {
                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        byte[] lengthBuffer = new byte[4];
                        int bytesRead = await stream.ReadAsync(lengthBuffer, 0, 4, token);
                        if (bytesRead < 4) break;

                        int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                        byte[] messageBuffer = new byte[messageLength];
                        
                        int totalRead = 0;
                        while (totalRead < messageLength)
                        {
                            bytesRead = await stream.ReadAsync(messageBuffer, totalRead, messageLength - totalRead, token);
                            if (bytesRead == 0) break;
                            totalRead += bytesRead;
                        }

                        if (totalRead == messageLength)
                        {
                            var message = NetworkMessage.Deserialize(messageBuffer);
                            await ProcessMessage(message, stream);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex);
                }
            }
        }

        private async Task ProcessMessage(NetworkMessage message, NetworkStream stream)
        {
            try
            {
                switch (message.Type)
                {
                    case MessageType.RegisterHost:
                        await HandleRegisterHost(message, stream);
                        break;

                    case MessageType.ConnectToHost:
                        await HandleConnectToHost(message, stream);
                        break;

                    case MessageType.Disconnect:
                        await HandleDisconnect(message);
                        break;
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }

        private async Task HandleRegisterHost(NetworkMessage message, NetworkStream stream)
        {
            var registerMsg = DeserializeMessage<RegisterHostMessage>(message.Data);
            string connectionCode = GenerateConnectionCode();
            string hostId = Guid.NewGuid().ToString();

            var hostInfo = new HostInfo
            {
                HostId = hostId,
                HostName = registerMsg.HostName,
                ConnectionCode = connectionCode,
                ClientEndpoint = null,
                RegisteredAt = DateTime.UtcNow
            };

            _hosts[connectionCode] = hostInfo;

            var response = new NetworkMessage
            {
                Type = MessageType.HostRegistered,
                Data = SerializeMessage(new HostRegisteredMessage
                {
                    ConnectionCode = connectionCode,
                    HostId = hostId
                })
            };

            await SendMessage(stream, response);
            HostRegistered?.Invoke(connectionCode);
        }

        private async Task HandleConnectToHost(NetworkMessage message, NetworkStream stream)
        {
            var connectMsg = DeserializeMessage<ConnectToHostMessage>(message.Data);
            
            if (_hosts.TryGetValue(connectMsg.ConnectionCode, out var hostInfo))
            {
                if (hostInfo.ClientEndpoint == null)
                {
                    // First client connecting - store its endpoint
                    // In a real implementation, you'd need to get the actual endpoint
                    // For now, we'll use a placeholder
                    hostInfo.ClientEndpoint = "client_connected";
                    
                    var response = new NetworkMessage
                    {
                        Type = MessageType.HostConnected,
                        Data = SerializeMessage(new HostConnectedMessage
                        {
                            HostEndpoint = hostInfo.HostId, // In real impl, this would be actual endpoint
                            ClientId = connectMsg.ClientId
                        })
                    };

                    await SendMessage(stream, response);
                    HostConnected?.Invoke(connectMsg.ConnectionCode);
                }
                else
                {
                    var response = new NetworkMessage
                    {
                        Type = MessageType.ConnectionFailed,
                        Data = Encoding.UTF8.GetBytes("Host already connected")
                    };

                    await SendMessage(stream, response);
                }
            }
            else
            {
                var response = new NetworkMessage
                {
                    Type = MessageType.ConnectionFailed,
                    Data = Encoding.UTF8.GetBytes("Invalid connection code")
                };

                await SendMessage(stream, response);
            }
        }

        private async Task HandleDisconnect(NetworkMessage message)
        {
            // Remove host from registry
            foreach (var host in _hosts)
            {
                if (host.Value.HostId == Encoding.UTF8.GetString(message.Data))
                {
                    _hosts.TryRemove(host.Key, out _);
                    break;
                }
            }
        }

        private string GenerateConnectionCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            string code;
            do
            {
                code = new string(Enumerable.Repeat(chars, 6)
                    .Select(s => s[_random.Next(s.Length)]).ToArray());
            } while (_hosts.ContainsKey(code));

            return code;
        }

        private T DeserializeMessage<T>(byte[] data) where T : new()
        {
            // Simple deserialization - in production, use proper serialization
            using (var stream = new System.IO.MemoryStream(data))
            using (var reader = new System.IO.BinaryReader(stream))
            {
                var obj = new T();
                // This is a simplified version - you'd need proper serialization
                return obj;
            }
        }

        private byte[] SerializeMessage<T>(T obj)
        {
            // Simple serialization - in production, use proper serialization
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                // This is a simplified version - you'd need proper serialization
                return stream.ToArray();
            }
        }

        private async Task SendMessage(NetworkStream stream, NetworkMessage message)
        {
            byte[] messageData = message.Serialize();
            byte[] lengthPrefix = BitConverter.GetBytes(messageData.Length);
            
            await stream.WriteAsync(lengthPrefix, 0, 4);
            await stream.WriteAsync(messageData, 0, messageData.Length);
            await stream.FlushAsync();
        }

        public void Stop()
        {
            IsRunning = false;
            _cancellationToken?.Cancel();
            _listener?.Stop();
            _hosts.Clear();
        }

        public void Dispose()
        {
            Stop();
            _cancellationToken?.Dispose();
        }

        private class HostInfo
        {
            public string HostId { get; set; } = string.Empty;
            public string HostName { get; set; } = string.Empty;
            public string ConnectionCode { get; set; } = string.Empty;
            public string? ClientEndpoint { get; set; }
            public DateTime RegisteredAt { get; set; }
        }
    }
}
