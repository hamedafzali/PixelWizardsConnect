using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PixelWizard.Protocol;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests
{
    /// <summary>
    /// Both ends send from several threads at once: the viewer's ping timer, pointer and keys,
    /// and chat; the host's frames, pongs and chat. Each message must arrive whole, and the
    /// session must survive. Over TLS, a second write while one is pending throws, and the
    /// transport used to disconnect on it, so a chat message sent mid-ping ended the session.
    /// </summary>
    public class TcpTransportConcurrentSendTests : IDisposable
    {
        private const int Senders = 8;
        private const int MessagesPerSender = 200;

        private readonly string _pinStorePath =
            Path.Combine(Path.GetTempPath(), $"pixelwizard-pins-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            if (File.Exists(_pinStorePath)) File.Delete(_pinStorePath);
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);
        }

        // Sender id, sequence number, then a body long enough that a write takes a while.
        private static byte[] Payload(int sender, int seq)
        {
            var data = new byte[8 + 4096];
            BitConverter.GetBytes(sender).CopyTo(data, 0);
            BitConverter.GetBytes(seq).CopyTo(data, 4);
            for (int i = 8; i < data.Length; i++) data[i] = (byte)(sender + seq + i);
            return data;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConcurrentSends_AllArriveWhole_SessionSurvives(bool useTls)
        {
            int port = GetFreePort();
            using var server = new TcpTransport();
            _ = server.StartServerAsync(port, useTls);
            await Task.Delay(50); // give the listener a moment to bind before connecting

            var received = new List<NetworkMessage>();
            var errors = new List<Exception>();
            bool disconnected = false;
            server.MessageReceived += m => { lock (received) received.Add(m); };

            using var client = new TcpTransport(new CertificatePinStore(_pinStorePath));
            client.Error += ex => { lock (errors) errors.Add(ex); };
            client.Disconnected += () => disconnected = true;
            await client.ConnectAsync("127.0.0.1", port, useTls);
            await WaitUntilAsync(() => server.IsConnected);

            await Task.WhenAll(Enumerable.Range(0, Senders).Select(sender => Task.Run(async () =>
            {
                for (int seq = 0; seq < MessagesPerSender; seq++)
                    await client.SendMessageAsync(new NetworkMessage { Type = MessageType.ChatMessage, Data = Payload(sender, seq) });
            })));
            await WaitUntilAsync(() => { lock (received) return received.Count == Senders * MessagesPerSender; });

            lock (errors) Assert.Empty(errors);
            Assert.False(disconnected);
            Assert.True(client.IsConnected);
            lock (received)
            {
                Assert.Equal(Senders * MessagesPerSender, received.Count);
                // Each sender awaits its own sends, so its messages arrive in order.
                for (int sender = 0; sender < Senders; sender++)
                {
                    var mine = received.Where(m => BitConverter.ToInt32(m.Data, 0) == sender).ToList();
                    Assert.Equal(MessagesPerSender, mine.Count);
                    for (int seq = 0; seq < MessagesPerSender; seq++)
                        Assert.Equal(Payload(sender, seq), mine[seq].Data);
                }
            }
        }
    }
}
