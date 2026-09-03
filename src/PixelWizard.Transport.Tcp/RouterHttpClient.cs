using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.Transport.Tcp
{
    public class RouterHttpClient : IRouterClient
    {
        private readonly HttpClient _http = new();

        /// <summary>
        /// Builds the router base URL. <paramref name="routerHost"/> may carry an explicit
        /// scheme ("https://router.example.com"); otherwise plain http is assumed. A standard
        /// port for the scheme (443/80) is omitted from the URL.
        /// </summary>
        internal static string BaseUrl(string routerHost, int routerPort)
        {
            string h = routerHost.Trim();
            string scheme = "http";
            if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { scheme = "https"; h = h.Substring(8); }
            else if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { h = h.Substring(7); }
            h = h.TrimEnd('/');
            int colon = h.IndexOf(':');
            if (colon >= 0) h = h.Substring(0, colon); // port comes from routerPort
            if (scheme == "https" && routerPort == 443) return $"https://{h}";
            if (scheme == "http"  && routerPort == 80)  return $"http://{h}";
            return $"{scheme}://{h}:{routerPort}";
        }

        public async Task<RouterRegistrationResult> RegisterHostAsync(string routerHost, int routerPort, string hostEndpoint)
        {
            var body = new { hostId = Guid.NewGuid().ToString(), hostName = Environment.MachineName, hostEndpoint };
            var json = JsonSerializer.Serialize(body);
            var response = await _http.PostAsync(
                $"{BaseUrl(routerHost, routerPort)}/register",
                new StringContent(json, Encoding.UTF8, "application/json"));

            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string code   = root.GetProperty("connectionCode").GetString()
                ?? throw new InvalidOperationException("Router did not return a connection code.");
            string secret = root.TryGetProperty("sessionSecret", out var s)
                ? (s.GetString() ?? "")
                : "";

            return new RouterRegistrationResult(code, secret);
        }

        public async Task<RouterConnectResult> ResolveEndpointAsync(string routerHost, int routerPort, string connectionCode)
        {
            var body = new { connectionCode, clientId = Guid.NewGuid().ToString() };
            var json = JsonSerializer.Serialize(body);
            var response = await _http.PostAsync(
                $"{BaseUrl(routerHost, routerPort)}/connect",
                new StringContent(json, Encoding.UTF8, "application/json"));

            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            bool success = root.TryGetProperty("success", out var sv) && sv.GetBoolean();
            if (!success)
            {
                string msg = root.TryGetProperty("message", out var m)
                    ? m.GetString() ?? "Router connection failed."
                    : "Router connection failed.";
                throw new InvalidOperationException(msg);
            }

            string endpoint = root.GetProperty("hostEndpoint").GetString()
                ?? throw new InvalidOperationException("Router did not return a host endpoint.");
            string secret = root.TryGetProperty("sessionSecret", out var sec)
                ? (sec.GetString() ?? "")
                : "";

            return new RouterConnectResult(endpoint, secret);
        }
    }
}
