using System.Collections.Concurrent;
using System.Net;
using Campus_live_rewrite.api.server;

// Per-IP flood / brute-force guard. Sits EARLY in the pipeline (right after the banned-IP
// check) so a flood is dropped before it touches routing, controllers, LiteDB or the access
// log - the cheapest possible rejection.
//
// Two fixed-window buckets, both keyed on the TRUSTWORTHY client IP:
//
//   * GENERAL: a high ceiling that only an actual request flood crosses (see
//     ConfigCampus.FloodMaxPerWindow). This is the anti-DoS ceiling - it keeps one address
//     from driving the box to max memory by hammering it.
//   * AUTH: a tight ceiling on the credential endpoints (/Auth/connect/token, /api/site/login).
//     This is the brute-force / token-farming guard the operator asked for: you cannot sit and
//     hammer the token endpoint guessing passwords or minting bearer tokens.
//
// WHY the IP must be the LAST X-Forwarded-For entry: behind Caddy every request's socket peer
// is 127.0.0.1, so RemoteIpAddress is useless for per-IP limiting, and the FIRST XFF entry is
// whatever the client sent - a spoof. Caddy APPENDS the real peer as the last entry, so that
// entry is authoritative and cannot be rotated by a client sending its own XFF. Same rule the
// website signup limiter uses. CF-Connecting-IP is deliberately NOT trusted (no Cloudflare in
// front - it would be a free spoof).
public class TrafficGuardMiddleware
{
    private readonly RequestDelegate _next;
    public TrafficGuardMiddleware(RequestDelegate next) => _next = next;

    // key -> (count, window-start unix seconds). Separate namespaces ("G:" / "A:") per bucket.
    private static readonly ConcurrentDictionary<string, (int count, long windowStart)> _hits = new();

    // The dictionary would otherwise grow one entry per (bucket, IP) forever. Sweep stale
    // entries occasionally - cheap, amortised, and bounded.
    private static long _lastSweep;
    private const int SweepEverySeconds = 120;

    // Escalation: address -> (rejections in the current window, window start); address -> blocked-until (unix seconds).
    private static readonly ConcurrentDictionary<string, (int count, long windowStart)> _strikes = new();
    private static readonly ConcurrentDictionary<string, long> _blocked = new();
    private static readonly ConcurrentDictionary<string, int> _inflight = new();
    private static readonly ConcurrentDictionary<string, long> _lastLog = new();

    private static bool IsAuthPath(string p) =>
        p.StartsWith("/Auth/connect/token", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/api/site/login", StringComparison.OrdinalIgnoreCase);

    public async Task Invoke(HttpContext context)
    {
        if (!ConfigCampus.TrafficGuardEnabled)
        {
            await _next(context);
            return;
        }

        string ip = ClientIp(context);

        // Never throttle loopback: Caddy, health checks and the admin SSH tunnel all originate
        // there, and an empty/unparseable IP is treated as loopback so we never lock everyone
        // out over a missing header.
        if (string.IsNullOrWhiteSpace(ip) || IsLoopback(ip) || AdminProtectionMiddleware.IsLimitExempt(ip))
        {
            await _next(context);
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MaybeSweep(now);

        // Cut off for a while after sustained abuse: answered with the cheapest possible 429 - no logging, no routing, nothing else.
        if (_blocked.TryGetValue(ip, out long until))
        {
            if (now < until)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = Math.Max(1, until - now).ToString();
                return;
            }
            _blocked.TryRemove(ip, out _);
        }

        string path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        bool auth = IsAuthPath(path);

        // Brute-force lockout: an IP that failed login too many times is blocked from the credential
        // endpoints for a few minutes (see LoginGuard). Checked BEFORE the rate limiter and the
        // controller, so a locked-out address can't keep guessing.
        if (auth && LoginGuard.IsLockedOut(ip, out int lockRetry))
        {
            await Reject(context, lockRetry, path, ip, "login-lockout");
            return;
        }

        // Auth endpoints are checked against BOTH ceilings; everything else only the general one.
        if (auth && Throttled("A:" + ip, ConfigCampus.AuthMaxPerWindow, ConfigCampus.AuthWindowSeconds, now, out int aRetry))
        {
            await Reject(context, aRetry, path, ip, "auth");
            return;
        }
        if (Throttled("G:" + ip, ConfigCampus.FloodMaxPerWindow, ConfigCampus.FloodWindowSeconds, now, out int gRetry))
        {
            await Reject(context, gRetry, path, ip, "flood");
            return;
        }
        // An address that posted uploads the game didn't make is held to a much lower ceiling on
        // EVERYTHING for the strike window (UploadGuard) - the "everything rate-limits them" half
        // of the upload rule, on top of the ban that lands at the strike count.
        if (UploadGuard.IsPenalisedIp(ip)
            && Throttled("P:" + ip, ConfigCampus.UploadPenaltyMaxPerWindow, ConfigCampus.FloodWindowSeconds, now, out int pRetry))
        {
            await Reject(context, pRetry, path, ip, "upload-penalty");
            return;
        }

        // Too many requests parked at once from one address (slow-read / slow-response hoarding). Long-lived WebSocket requests are not counted.
        bool counted = !context.WebSockets.IsWebSocketRequest;
        if (counted)
        {
            int now_in = _inflight.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (now_in > ConfigCampus.MaxInflightPerIp)
            {
                _inflight.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
                await Reject(context, 5, path, ip, "inflight");
                return;
            }
        }
        try { await _next(context); }
        finally { if (counted) _inflight.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1)); }
    }

    // Fixed-window counter. Returns true (and a Retry-After) once the window count exceeds cap.
    //
    // A REJECTED request no longer counts: the client retries /connect/token every few seconds
    // after a 429, and with every retry counted the window never drained - 317 refusals in one
    // hour from one address, a player who could only get in by changing IP (a VPN). Now the
    // window fills, refuses until it ends, and then admits again.
    private static bool Throttled(string key, int cap, int windowSeconds, long now, out int retryAfter)
    {
        var entry = _hits.AddOrUpdate(
            key,
            _ => (1, now),
            (_, cur) => (now - cur.windowStart >= windowSeconds) ? (1, now)
                      : (cur.count > cap ? cur.count : cur.count + 1, cur.windowStart));

        if (entry.count > cap)
        {
            retryAfter = (int)Math.Max(1, windowSeconds - (now - entry.windowStart));
            return true;
        }
        retryAfter = 0;
        return false;
    }

    private static async Task Reject(HttpContext context, int retryAfter, string path, string ip, string kind)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = retryAfter.ToString();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // One log line per address per minute: under a flood a line per refusal would fill the journal and the disk.
        if (now - _lastLog.GetOrAdd(ip, 0) >= 60)
        {
            _lastLog[ip] = now;
            Console.WriteLine($"[trafficguard] 429 ({kind}) {context.Request.Method} {path} from {ip} - retry in {retryAfter}s");
        }

        // Strikes: a client that backs off after a 429 never accumulates these; a bot hammering through them gets cut off.
        var s = _strikes.AddOrUpdate(ip, _ => (1, now),
            (_, cur) => (now - cur.windowStart >= ConfigCampus.AbuseStrikeWindowSeconds) ? (1, now) : (cur.count + 1, cur.windowStart));
        if (s.count >= ConfigCampus.AbuseStrikesToBlock)
        {
            _blocked[ip] = now + ConfigCampus.AbuseBlockMinutes * 60L;
            _strikes.TryRemove(ip, out _);
            Console.WriteLine($"[trafficguard] {ip} kept going after {s.count} refusals - cut off for {ConfigCampus.AbuseBlockMinutes} min");
        }
        await context.Response.WriteAsync("Too many requests.");
    }

    private static void MaybeSweep(long now)
    {
        if (now - Interlocked.Read(ref _lastSweep) < SweepEverySeconds) return;
        Interlocked.Exchange(ref _lastSweep, now);
        // Drop any entry whose window is well past (older than the widest window we use). Cheap
        // enough to do inline every couple of minutes.
        long maxWindow = Math.Max(ConfigCampus.FloodWindowSeconds, ConfigCampus.AuthWindowSeconds);
        foreach (var kv in _hits)
            if (now - kv.Value.windowStart > maxWindow + 60)
                _hits.TryRemove(kv.Key, out _);
        foreach (var kv in _strikes)
            if (now - kv.Value.windowStart > ConfigCampus.AbuseStrikeWindowSeconds + 60)
                _strikes.TryRemove(kv.Key, out _);
        foreach (var kv in _blocked)
            if (now >= kv.Value) _blocked.TryRemove(kv.Key, out _);
        foreach (var kv in _lastLog)
            if (now - kv.Value > 300) _lastLog.TryRemove(kv.Key, out _);
        foreach (var kv in _inflight)
            if (kv.Value <= 0) _inflight.TryRemove(kv.Key, out _);
    }

    // Trustworthy client IP = the LAST X-Forwarded-For entry (Caddy-appended), else the socket.
    private static string ClientIp(HttpContext ctx)
    {
        string xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(xff))
        {
            var parts = xff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) return parts[parts.Length - 1];
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    private static bool IsLoopback(string ip)
    {
        if (ip == "::1" || ip.StartsWith("127.")) return true;
        return IPAddress.TryParse(ip, out var parsed) && IPAddress.IsLoopback(parsed);
    }
}
