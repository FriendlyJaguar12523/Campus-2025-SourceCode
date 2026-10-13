using System.Net;
using System.Net.Sockets;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Which CDN base /ns hands the client: ConfigCampus.CdnBaseUrl (its own host - needed for
    /// avatar bundles to load, see the comment there) once that host is proven to work, else
    /// ConfigCampus.CdnFallbackUrl. A CDN URL the client cannot use stops the game at "Launch
    /// validation failed" (it fetches /cdn/config/... during boot), so the switch is never
    /// taken on trust: every 5 minutes the host must (1) resolve in DNS to one of the API
    /// host's addresses and (2) answer a /cdn request with 200 over https with a certificate
    /// that validates for that name. The https check is dialled to the local Caddy (127.0.0.1)
    /// with the real hostname as SNI/Host, so it does not depend on the VPS reaching its own
    /// public IP.
    /// </summary>
    public static class CdnHost
    {
        private static volatile string _current = ConfigCampus.CdnFallbackUrl;
        private static string? _lastState;

        public static string Current => _current;

        public static void Start()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try { await Probe(); }
                    catch (Exception ex) { Report($"probe error: {ex.Message}", ConfigCampus.CdnFallbackUrl); }
                    await Task.Delay(TimeSpan.FromMinutes(5));
                }
            });
        }

        private static async Task Probe()
        {
            var cdn = new Uri(ConfigCampus.CdnBaseUrl);
            var api = new Uri(ConfigCampus.CdnFallbackUrl);
            if (cdn.Scheme != Uri.UriSchemeHttps) { Report("CdnBaseUrl is not https (the client rejects it)", api.ToString()); return; }
            if (string.Equals(cdn.Host, api.Host, StringComparison.OrdinalIgnoreCase)) { Report("CdnBaseUrl is on the API host", ConfigCampus.CdnBaseUrl); return; }

            IPAddress[] cdnIps, apiIps;
            try { cdnIps = await Dns.GetHostAddressesAsync(cdn.Host); }
            catch (Exception ex) { Report($"{cdn.Host} does not resolve ({ex.Message.Trim()})", ConfigCampus.CdnFallbackUrl); return; }
            apiIps = await Dns.GetHostAddressesAsync(api.Host);
            if (!cdnIps.Any(a => apiIps.Contains(a)))
            {
                Report($"{cdn.Host} resolves to {string.Join(",", cdnIps.Select(a => a.ToString()))}, not this server", ConfigCampus.CdnFallbackUrl);
                return;
            }

            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await s.ConnectAsync(IPAddress.Loopback, cdn.Port, ct); return new NetworkStream(s, ownsSocket: true); }
                    catch { s.Dispose(); throw; }
                },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var probe = new Uri(cdn, cdn.AbsolutePath.TrimEnd('/') + "/config/LoadingScreenTipData");
            try
            {
                using var resp = await http.GetAsync(probe, HttpCompletionOption.ResponseHeadersRead);
                if (resp.StatusCode != HttpStatusCode.OK) { Report($"{probe} answered {(int)resp.StatusCode}", ConfigCampus.CdnFallbackUrl); return; }
            }
            catch (Exception ex)
            {
                Report($"{probe} failed over https ({(ex.InnerException ?? ex).Message.Trim()})", ConfigCampus.CdnFallbackUrl);
                return;
            }
            Report($"{cdn.Host} is live", ConfigCampus.CdnBaseUrl);
        }

        private static void Report(string why, string url)
        {
            _current = url;
            string state = url + " | " + why;
            if (state == _lastState) return;
            _lastState = state;
            Console.WriteLine($"[cdn-host] advertising {url} - {why}");
        }
    }
}
