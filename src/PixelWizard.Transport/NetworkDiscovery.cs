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
                    if (!msg.StartsWith(Prefix)) continue;
                    var parts   = msg.Split('|');
                    if (parts.Length < 3) continue;
                    string ip   = result.RemoteEndPoint.Address.ToString();
                    string port = parts[2];
                    string key  = $"{ip}:{port}";
                    if (seen.Add(key)) onHost(key);
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore receive errors */ }
            }
        }
    }
}
