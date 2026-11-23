using System;

namespace PixelWizard.Signaling
{
    /// <summary>Derives the router's base URL from the host and port the user entered.</summary>
    internal static class RouterUrl
    {
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
    }
}
