using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.Transport
{
    public class RouterHttpClient : IRouterClient
    {
        private readonly HttpClient _http = new();

        public async Task<RouterRegistrationResult> RegisterHostAsync(string routerHost, int routerPort, string hostEndpoint)
        {
            var body = new { hostId = Guid.NewGuid().ToString(), hostName = Environment.MachineName, hostEndpoint };
            var json = JsonSerializer.Serialize(body);
            var response = await _http.PostAsync(
                $"http://{routerHost}:{routerPort}/register",
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
                $"http://{routerHost}:{routerPort}/connect",
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
