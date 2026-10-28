namespace PixelWizard.Session;

/// <summary>
/// Parses what the user types into the viewer's direct-connect box: <c>host</c>,
/// <c>host:port</c> or <c>[ipv6]:port</c>. A bare host gets <see cref="DefaultPort"/>, so a
/// host listening on any other port is reachable by typing it -- the address the host
/// screen shows (<c>ip:port</c>) can be pasted as-is.
/// </summary>
public static class DirectAddress
{
    public const int DefaultPort = 8888;

    public static bool TryParse(string? input, out string host, out int port)
    {
        host = "";
        port = DefaultPort;
        string s = (input ?? "").Trim();
        if (s.Length == 0) return false;

        string? portText = null;
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close < 2) return false;
            host = s[1..close];
            string rest = s[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':') return false;
                portText = rest[1..];
            }
        }
        else if (s.IndexOf(':') is var colon and >= 0 && colon == s.LastIndexOf(':'))
        {
            host = s[..colon];
            portText = s[(colon + 1)..];
        }
        else
        {
            host = s; // no colon, or a bare IPv6 literal
        }

        if (host.Length == 0) return false;
        if (portText != null && !(int.TryParse(portText, out port) && port is > 0 and <= 65535))
            return false;
        return true;
    }
}
