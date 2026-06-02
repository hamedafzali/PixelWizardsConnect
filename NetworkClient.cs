using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenAutomationApp
{
    public class NetworkClient : IDisposable
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cancellationToken;
        private bool _isConnected = false;

        public event Action<NetworkMessage>? MessageReceived;
        public event Action? Connected;
        public event Action? Disconnected;
        public event Action<Exception>? Error;
        public event Action<int>? BytesReceived;
        public event Action<int>? BytesSent;

        public bool IsConnected => _isConnected && _tcpClient?.Connected == true;

        public async Task ConnectAsync(string host, int port)
        {
            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(host, port);
                _stream = _tcpClient.GetStream();
                _isConnected = true;
                _cancellationToken = new CancellationTokenSource();

                Connected?.Invoke();
                StartReceiving();
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                Disconnect();
            }
        }

        public async Task StartServerAsync(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                
                _tcpClient = await listener.AcceptTcpClientAsync();
                listener.Stop();
                
                _stream = _tcpClient.GetStream();
                _isConnected = true;
                _cancellationToken = new CancellationTokenSource();

                Connected?.Invoke();
                StartReceiving();
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                Disconnect();
            }
        }

        private void StartReceiving()
        {
            Task.Run(() => ReceiveLoop(_cancellationToken!.Token));
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] lengthBuffer = new byte[4];

            while (!token.IsCancellationRequested && IsConnected)
            {
                try
                {
                    bool hasLength = await ReadExactAsync(lengthBuffer, 4, token);
                    if (!hasLength)
                    {
                        break;
                    }

                    int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                    if (messageLength <= 0)
                    {
                        break;
                    }

                    byte[] messageBuffer = new byte[messageLength];

                    bool hasMessage = await ReadExactAsync(messageBuffer, messageLength, token);
                    if (!hasMessage)
                    {
                        break;
                    }

                    var message = NetworkMessage.Deserialize(messageBuffer);
                    BytesReceived?.Invoke(4 + messageLength);
                    MessageReceived?.Invoke(message);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex);
                    break;
                }
            }

            Disconnect();
        }

        private async Task<bool> ReadExactAsync(byte[] buffer, int length, CancellationToken token)
        {
            if (_stream == null)
                return false;

            int totalRead = 0;
            while (totalRead < length)
            {
                int bytesRead = await _stream.ReadAsync(buffer, totalRead, length - totalRead, token);
                if (bytesRead == 0)
                {
                    return false;
                }

                totalRead += bytesRead;
            }

            return true;
        }

        public async Task SendMessageAsync(NetworkMessage message)
        {
            if (!IsConnected || _stream == null)
                return;

            try
            {
                byte[] messageData = message.Serialize();
                byte[] lengthPrefix = BitConverter.GetBytes(messageData.Length);
                
                await _stream.WriteAsync(lengthPrefix, 0, 4);
                await _stream.WriteAsync(messageData, 0, messageData.Length);
                await _stream.FlushAsync();
                BytesSent?.Invoke(4 + messageData.Length);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                Disconnect();
            }
        }

        public void Disconnect()
        {
            _isConnected = false;
            _cancellationToken?.Cancel();
            
            _stream?.Close();
            _tcpClient?.Close();
            
            Disconnected?.Invoke();
        }

        public void Dispose()
        {
            Disconnect();
            _cancellationToken?.Dispose();
            _tcpClient?.Dispose();
            _stream?.Dispose();
        }
    }
}
