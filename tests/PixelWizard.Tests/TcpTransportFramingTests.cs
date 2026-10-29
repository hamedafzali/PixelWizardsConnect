using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PixelWizard.Protocol;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests
{
    /// <summary>
    /// End-to-end loopback tests proving the two "something's wrong with this frame" outcomes
    /// stay distinguishable: an unrecognized (but well-framed) MessageType is skipped and the
    /// session survives, while a corrupt outer length is unrecoverable and disconnects with a
    /// FramingException. Collapsing these into one code path was the bug this guards against.
    /// </summary>
    public class TcpTransportFramingTests
    {
        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<TcpTransport> StartServerAsync(int port)
        {
            var server = new TcpTransport();
            _ = server.StartServerAsync(port, useTls: false);
            await Task.Delay(50); // give the listener a moment to bind before connecting
            return server;
        }

        // Polls instead of sleeping a fixed time: a loaded CI runner can take longer than any fixed
        // delay to deliver a frame. Returns on timeout too, so the test's own assertions report it.
        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);
        }

        [Fact]
        public async Task UnknownMessageType_IsSkipped_SessionSurvives_NextValidMessageProcessed()
        {
            int port = GetFreePort();
            using var server = await StartServerAsync(port);

            var skipped = new List<byte>();
            var received = new List<NetworkMessage>();
            bool disconnected = false;
            server.UnknownMessageTypeSkipped += b => skipped.Add(b);
            server.MessageReceived += m => received.Add(m);
            server.Disconnected += () => disconnected = true;

            using var client = new TcpTransport();
            await client.ConnectAsync("127.0.0.1", port, useTls: false);
            await WaitUntilAsync(() => server.IsConnected);

            await client.SendMessageAsync(new NetworkMessage { Type = (MessageType)250, Data = new byte[] { 9, 9 } });
            await WaitUntilAsync(() => skipped.Count == 1);

            await client.SendMessageAsync(new NetworkMessage { Type = MessageType.Ping, Data = Array.Empty<byte>() });
            await WaitUntilAsync(() => received.Count == 1);

            Assert.Equal(new byte[] { 250 }, skipped);
            Assert.Single(received);
            Assert.Equal(MessageType.Ping, received[0].Type);
            Assert.False(disconnected);
            Assert.True(server.IsConnected);
        }

        [Fact]
        public async Task MalformedOuterLength_Disconnects_DistinguishableFromUnknownTypeSkip()
        {
            int port = GetFreePort();
            using var server = await StartServerAsync(port);

            var skipped = new List<byte>();
            Exception? error = null;
            bool disconnected = false;
            server.UnknownMessageTypeSkipped += b => skipped.Add(b);
            server.Error += ex => error = ex;
            server.Disconnected += () => disconnected = true;

            using var raw = new TcpClient();
            await raw.ConnectAsync("127.0.0.1", port);
            await raw.GetStream().WriteAsync(BitConverter.GetBytes(0)); // zero outer length: unrecoverable
            await WaitUntilAsync(() => disconnected && error != null);

            Assert.Empty(skipped);
            Assert.IsType<FramingException>(error);
            Assert.True(disconnected);
            Assert.False(server.IsConnected);
        }

        [Fact]
        public async Task HandlerThrowsOnce_SessionSurvives_NextMessageStillDispatched()
        {
            int port = GetFreePort();
            using var server = await StartServerAsync(port);

            var handlerErrors = new List<Exception>();
            var received = new List<NetworkMessage>();
            bool disconnected = false;
            server.HandlerError += ex => handlerErrors.Add(ex);
            server.Disconnected += () => disconnected = true;
            server.MessageReceived += m =>
            {
                received.Add(m);
                if (received.Count == 1) throw new InvalidOperationException("boom");
            };

            using var client = new TcpTransport();
            await client.ConnectAsync("127.0.0.1", port, useTls: false);
            await WaitUntilAsync(() => server.IsConnected);

            await client.SendMessageAsync(new NetworkMessage { Type = MessageType.Ping, Data = Array.Empty<byte>() });
            await WaitUntilAsync(() => received.Count == 1);
            await client.SendMessageAsync(new NetworkMessage { Type = MessageType.Ping, Data = Array.Empty<byte>() });
            await WaitUntilAsync(() => received.Count == 2);

            Assert.Single(handlerErrors);
            Assert.IsType<InvalidOperationException>(handlerErrors[0]);
            Assert.Equal(2, received.Count);
            Assert.False(disconnected);
            Assert.True(server.IsConnected);
        }

        [Fact]
        public async Task HandlerFailsRepeatedly_EscalatesToTransportError_Disconnects()
        {
            int port = GetFreePort();
            using var server = await StartServerAsync(port);

            var handlerErrors = new List<Exception>();
            Exception? error = null;
            bool disconnected = false;
            server.HandlerError += ex => handlerErrors.Add(ex);
            server.Error += ex => error = ex;
            server.Disconnected += () => disconnected = true;
            server.MessageReceived += _ => throw new InvalidOperationException("always fails");

            using var client = new TcpTransport();
            await client.ConnectAsync("127.0.0.1", port, useTls: false);
            await WaitUntilAsync(() => server.IsConnected);

            for (int i = 0; i < TcpTransport.MaxConsecutiveHandlerFailures; i++)
            {
                await client.SendMessageAsync(new NetworkMessage { Type = MessageType.Ping, Data = Array.Empty<byte>() });
                int expected = i + 1;
                await WaitUntilAsync(() => handlerErrors.Count == expected);
            }
            await WaitUntilAsync(() => disconnected && error != null);

            Assert.Equal(TcpTransport.MaxConsecutiveHandlerFailures, handlerErrors.Count);
            Assert.IsType<RepeatedHandlerFailureException>(error);
            Assert.True(disconnected);
            Assert.False(server.IsConnected);
        }
    }
}
