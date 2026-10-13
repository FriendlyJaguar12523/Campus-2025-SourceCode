using System.Collections.Concurrent;

// Hardening for the ADMIN surface (/admin/* and /api/web/admin/*) on top of the auth the other
// layers already enforce (dev token + admin session cookie). The admin endpoints are the most
// dangerous surface on the server - they mutate players, economy, rooms, bans - so they get:
//
//   * Security headers on every response: no MIME sniffing, no framing (clickjacking), no
//     referrer leakage, no indexing, no caching, a locked-down Permissions-Policy, and a CSP on
//     the admin HTML pages.
//   * A per-IP rate limit: a generous window for normal panel use (which polls the log/players),
//     and a tight window on the dev-login endpoint to blunt brute force.
//
// Runs after BannedIpMiddleware and PatchGateMiddleware, so a banned IP never reaches it and the
// login page stays reachable to be throttled.
public class AdminProtectionMiddleware
{
    private readonly RequestDelegate _next;
    public AdminProtectionMiddleware(RequestDelegate next) => _next = next;

    // Fixed-window per-IP counters: key -> (count, window-start unix seconds).
    private static readonly ConcurrentDictionary<string, (int count, long windowStart)> _hits = new();

    // Master switch, in case the throttle ever needs disabling without a redeploy of logic.
    public static bool RateLimitEnabled = true;

    private const int WindowSeconds = 15;
    private const int MaxPerWindow = 300;          // generous: the Log tab polls every 3s, lists burst on load
    private const int LoginWindowSeconds = 60;
    private const int MaxLoginPerWindow = 10;      // brute-force guard on /api/web/admin/login

    // ADMIN-ONLY IP allowlist (Data/admin_allowlist.txt). If the file exists AND lists at least one
    // IP, the admin surface is reachable ONLY from those IPs (plus loopback, so the SSH tunnel /
    // localhost can never lock the operator out); everyone else gets 404 - the panel simply looks
    // like it isn't there. Empty/missing file = OFF (admin still gated by token + dev login). This
    // is scoped to /admin + /api/web/admin ONLY, so it never affects game clients. One IP per line;
    // blank lines and lines starting with '#' are ignored, re-read every few seconds.
    private const int AllowlistCacheSeconds = 5;
    private static readonly object _allowLock = new();
    private static HashSet<string>? _allow;
    private static long _allowLoadedAt;

    private static HashSet<string>? CurrentAllowlist()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_allowLock)
        {
            if (_allow != null && now - _allowLoadedAt < AllowlistCacheSeconds) return _allow.Count == 0 ? null : _allow;
            _allowLoadedAt = now;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(Environment.CurrentDirectory, "Data", "admin_allowlist.txt");
                if (File.Exists(path))
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        set.Add(line);
                    }
            }
            catch { /* unreadable file -> treat as OFF, never lock out on an IO hiccup */ }
            _allow = set;
            return set.Count == 0 ? null : set;
        }
    }

    /// <summary>Is this IP in Data/admin_allowlist.txt (or loopback)?</summary>
    public static bool IsAllowlistedIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        if (ip == "127.0.0.1" || ip == "::1" || ip.StartsWith("::ffff:127.")) return true;
        var allow = Campus_live_rewrite.api.server.ConfigCampus.AdminIpAllowlistEnabled ? CurrentAllowlist() : null;
        return allow != null && allow.Contains(ip.Trim());
    }

    /// <summary>
    /// Could this IP reach the admin panel at all: loopback, a listed IP, or any IP while the
    /// allowlist is off. The website's quick-admin popup (SiteAdminController) uses the same rule,
    /// so it never works from somewhere the panel itself would answer 404.
    /// </summary>
    public static bool AdminReachableFrom(string? ip) => !Campus_live_rewrite.api.server.ConfigCampus.AdminIpAllowlistEnabled || IsAllowlistedIp(ip) || CurrentAllowlist() == null;

    /// <summary>An allowlisted admin IP that is exempt from the anti-abuse limits (ConfigCampus.AdminRateLimitEnabled = false).</summary>
    public static bool IsLimitExempt(string? ip) =>
        !Campus_live_rewrite.api.server.ConfigCampus.AdminRateLimitEnabled && IsAllowlistedIp(ip);

    private static bool IsAdmin(string p) =>
        p.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/api/web/admin", StringComparison.OrdinalIgnoreCase);

    private static bool IsLogin(string p) =>
        p.StartsWith("/api/web/admin/login", StringComparison.OrdinalIgnoreCase);

    private static bool IsApi(string p) =>
        p.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/api/web/admin", StringComparison.OrdinalIgnoreCase);

    public async Task Invoke(HttpContext context)
    {
        string path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        if (!IsAdmin(path))
        {
            await _next(context);
            return;
        }

        // Admin-only IP allowlist: if configured, non-listed IPs get 404 (panel "doesn't exist").
        // Loopback is always allowed so the operator can't be locked out over the SSH tunnel.
        var allow = Campus_live_rewrite.api.server.ConfigCampus.AdminIpAllowlistEnabled ? CurrentAllowlist() : null;
        if (allow != null)
        {
            string aip = PatchGateMiddleware.ClientIp(context);
            bool loop = System.Net.IPAddress.TryParse(aip, out var pip) && System.Net.IPAddress.IsLoopback(pip);
            if (!loop && !allow.Contains(aip))
            {
                Console.WriteLine($"[adminprotect] IP {aip} not on admin allowlist -> 404 {path}");
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("");
                return;
            }
        }

        // Stamp the security headers just before the response flushes, so they win over anything a
        // controller/static-file handler set.
        context.Response.OnStarting(() =>
        {
            ApplySecurityHeaders(context.Response.Headers, path);
            return Task.CompletedTask;
        });

        if (RateLimitEnabled)
        {
            bool login = IsLogin(path);
            string ip = PatchGateMiddleware.ClientIp(context);
            string key = (login ? "L:" : "A:") + ip;
            int cap = login ? MaxLoginPerWindow : MaxPerWindow;
            int win = login ? LoginWindowSeconds : WindowSeconds;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var entry = _hits.AddOrUpdate(
                key,
                _ => (1, now),
                (_, cur) => (now - cur.windowStart >= win) ? (1, now) : (cur.count + 1, cur.windowStart));

            if (entry.count > cap)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = win.ToString();
                Console.WriteLine($"[adminprotect] 429 {context.Request.Method} {path} from {ip} ({entry.count} in {win}s)");
                await context.Response.WriteAsync("Too many requests.");
                return;
            }
        }

        await _next(context);
    }

    private static void ApplySecurityHeaders(IHeaderDictionary h, string path)
    {
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Robots-Tag"] = "noindex, nofollow";
        h["Cache-Control"] = "no-store, no-cache, must-revalidate";
        h["Pragma"] = "no-cache";
        h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=(), payment=()";

        // CSP only on the admin HTML PAGES (not the JSON APIs). The panel uses inline scripts/styles,
        // Font Awesome + Google Fonts over CDN, /img thumbnails, and same-origin fetch; block the rest
        // and forbid being framed anywhere.
        if (!IsApi(path))
        {
            h["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
                "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com https://fonts.googleapis.com; " +
                "font-src 'self' https://cdnjs.cloudflare.com https://fonts.gstatic.com data:; " +
                "img-src 'self' data: https:; " +
                "connect-src 'self'; " +
                "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        }
    }
}
