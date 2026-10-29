using System;

namespace PixelWizard.AvaloniaClient.Services;

public static class RouterEndpoint
{
    // Parses "host", "host:port", "https://host", "https://host:port".
    // The scheme (if any) is preserved on the returned host so the router client
    // can honour https. Default port is 443 for https, otherwise 9000.
    public static (string host, int port) Parse(string addr)
    {
        addr = (addr ?? "").Trim();
        string scheme = "";
        if (addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { scheme = "https://"; addr = addr.Substring(8); }
        else if (addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { scheme = "http://"; addr = addr.Substring(7); }
        addr = addr.TrimEnd('/');
        var p = addr.Split(':');
        int defaultPort = scheme == "https://" ? 443 : 9000;
        int port = p.Length > 1 && int.TryParse(p[1], out var pp) ? pp : defaultPort;
        return (scheme + p[0], port);
    }
}
