using System.Collections.Concurrent;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Avatar-bundle download diagnostics ("[cdn-diag]" in the journal). Answers "does clothing
    /// load on platform X?" without a device log: a client that loads a bundle caches it
    /// (BestHTTP disk cache) and downloads it ONCE; a client whose load fails re-downloads the
    /// same bundle over and over (133 times an hour before the CDN-host fix, see
    /// ConfigCampus.CdnBaseUrl). Bundle requests carry no auth, so the player is recovered from
    /// the client IP their heartbeat last came from.
    ///
    /// Logged per (client IP, bundle): the first download, then repeats 2, 3, 5, 10, 25, 50...
    /// A repeat line is the failure signal. 304s (revalidation of a cached copy) are success.
    /// </summary>
    public static class CdnDiag
    {
        private sealed record Client(ulong Id, string Name, string Platform, string Device, DateTime Seen);

        private static readonly ConcurrentDictionary<string, Client> _byIp = new();
        private static readonly ConcurrentDictionary<string, int> _counts = new();

        public static void NoteClient(HttpContext ctx, player_info account, int platform, int deviceClass)
        {
            try
            {
                string ip = LoginGuard.ClientIp(ctx);
                if (string.IsNullOrEmpty(ip)) return;
                string name = account.Username ?? account.DisplayName ?? account.Id.ToString();
                string plat = Enum.IsDefined(typeof(Platforms), platform) ? ((Platforms)platform).ToString() : platform.ToString();
                string dev = Enum.IsDefined(typeof(DeviceClasses), deviceClass) ? ((DeviceClasses)deviceClass).ToString() : deviceClass.ToString();
                _byIp[ip] = new Client(account.Id, name, plat, dev, DateTime.UtcNow);
            }
            catch { }
        }

        public static void OnBundle(HttpContext ctx, string bundle, bool notModified)
        {
            try
            {
                string ip = LoginGuard.ClientIp(ctx);
                int n = _counts.AddOrUpdate(ip + "|" + bundle, 1, (_, c) => c + 1);
                if (!(n <= 3 || n == 5 || n == 10 || n == 25 || n % 50 == 0)) return;

                string who = _byIp.TryGetValue(ip, out var c) && DateTime.UtcNow - c.Seen < TimeSpan.FromMinutes(10)
                    ? $"{c.Name}#{c.Id} platform={c.Platform} device={c.Device}"
                    : "unknown player (no recent heartbeat from this IP)";
                string ua = ctx.Request.Headers.UserAgent.ToString();
                string range = ctx.Request.Headers.Range.ToString();
                string verdict = notModified ? "304 cached copy revalidated (OK)"
                               : n == 1 ? "first download"
                               : $"REPEAT #{n} - this client is not keeping the bundle (load likely failing)";
                Console.WriteLine($"[cdn-diag] {who} host={ctx.Request.Host.Host} bundle={bundle} {verdict}" +
                                  (range.Length > 0 ? $" range={range}" : "") + (ua.Length > 0 ? $" ua=\"{ua}\"" : ""));
            }
            catch { }
        }
    }
}
