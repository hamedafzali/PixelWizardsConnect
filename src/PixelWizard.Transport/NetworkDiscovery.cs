using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PixelWizard.Transport
{
    /// <summary>
    /// Lightweight LAN discovery using UDP broadcast.
    /// Host announces itself; viewer listens for announcements.
    /// Protocol: "PIXELWIZARD|{hostname}|{port}" on UDP port 5678.
    /// </summary>
    public static class NetworkDiscovery
    {
        private const int    Port    = 5678;
        private const string Prefix  = "PIXELWIZARD|";

        /// <summary>
        /// Broadcasts host presence every 2 s until cancellation.
        /// Call from the host side after starting to listen on <paramref name="tcpPort"/>.
        /// </summary>
        public static async Task AnnounceAsync(int tcpPort, CancellationToken ct)
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            var endpoint = new IPEndPoint(IPAddress.Broadcast, Port);
            string payload = $"{Prefix}{Dns.GetHostName()}|{tcpPort}";
            byte[] data    = Encoding.UTF8.GetBytes(payload);

            while (!ct.IsCancellationRequested)
            {
                try { await udp.SendAsync(data, data.Length, endpoint); }
                catch { /* ignore send errors */ }
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// Listens for host announcements.
        /// Calls <paramref name="onHost"/> with "ip:port" whenever a new host is found.
        /// </summary>
        public static async Task ListenAsync(Action<string> onHost, CancellationToken ct)
        {
            using var udp = new UdpClient(Port) { EnableBroadcast = true };
            var seen = new HashSet<string>();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result  = await udp.ReceiveAsync(ct);
                    string msg  = Encoding.UTF8.GetString(result.Buffer);
                    string ipAddr = result.RemoteEndPoint.Address.ToString();
                    var parsed = Parse(msg, ipAddr);
                    if (parsed is not { } p) continue;
                    string key  = $"{p.ip}:{p.port}";
                    if (seen.Add(key)) onHost(key);
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore receive errors */ }
            }
        }

        /// <summary>
        /// Parses a discovery announcement of the form "PIXELWIZARD|{hostname}|{port}".
        /// Returns the sender's <paramref name="remoteIp"/> paired with the announced port,
        /// or <c>null</c> if the message is not a well-formed announcement.
        /// </summary>
        internal static (string ip, string port)? Parse(string message, string remoteIp)
        {
            if (!message.StartsWith(Prefix)) return null;
            var parts = message.Split('|');
            if (parts.Length < 3) return null;
            return (remoteIp, parts[2]);
        }
    }
}
