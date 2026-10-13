using System.Collections.Concurrent;
using System.Net;
using Campus_live_rewrite.api.dbs;

/// <summary>
/// Refuses EVERY request from a banned IP with 502, not just the login call.
///
/// The IP ban itself is enforced in AuthController, but that only covers
/// /Auth/connect/token. A banned address could still reach every other endpoint -
/// images, rooms, chat, the admin login page - and anything holding a valid token kept
/// working until it expired. This closes the whole domain.
///
/// 502 is deliberate (Kyle's call): to the client it looks like the site is down rather
/// than like a ban, so there is nothing to probe at.
/// </summary>
public class BannedIpMiddleware
{
    private readonly RequestDelegate _next;

    // BanDB.GetIpBan does an AES encrypt plus a LiteDB lookup. Doing that on every single
    // request (images alone are hundreds per session) would be wasteful, so decisions are
    // cached briefly. A ban therefore takes up to CacheSeconds to bite, and an unban up to
    // CacheSeconds to release - acceptable for a moderation action, and it keeps the hot
    // path to a dictionary probe.
    private const int CacheSeconds = 20;

    private static readonly ConcurrentDictionary<string, (bool Banned, DateTime Checked)> _cache = new();

    public BannedIpMiddleware(RequestDelegate next) => _next = next;

    /// <summary>Drops a cached decision so a ban/unban applies immediately.</summary>
    public static void Forget(string? ip)
    {
        if (!string.IsNullOrWhiteSpace(ip)) _cache.TryRemove(ip!, out _);
    }

    public static void ForgetAll() => _cache.Clear();

    public async Task Invoke(HttpContext context)
    {
        string? ip = ClientIp(context);

        // Never block loopback. Caddy proxies from the same box, and the admin panel is
        // reached over an SSH tunnel to localhost - banning a shared/CGNAT address must not
        // be able to lock the operator out of their own server.
        if (string.IsNullOrWhiteSpace(ip) || IsLoopback(ip))
        {
            await _next(context);
            return;
        }

        // ALLOWLIST MODE (private-server lockdown).
        //
        // When Data/allowlist.txt exists AND has at least one address, ONLY those addresses
        // (plus loopback, exempted above) may reach the server - everything else gets 502,
        // so the whole domain looks down to anyone not on the list. This is how the server is
        // pinned to the operator's own IP.
        //
        // Safe by construction:
        //   - no file, or an empty file  -> mode is OFF, server behaves normally. Deleting or
        //     blanking the file instantly reopens the server, no rebuild.
        //   - loopback is always allowed (checked above), so the admin panel over the SSH
        //     tunnel keeps working even when your own public IP is not listed.
        //   - the file is re-read when it changes, so editing it takes effect within seconds.
        //
        // One IP per line; blank lines and lines starting with '#' are ignored.
        if (!IsAllowed(ip!))
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("502 Bad Gateway");
            return;
        }

        if (IsBanned(ip!))
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("502 Bad Gateway");
            return;
        }

        await _next(context);
    }

    private static readonly string AllowlistPath =
        Path.Combine(Environment.CurrentDirectory, "Data", "allowlist.txt");

    private static HashSet<string>? _allow;
    private static DateTime _allowLoadedAt = DateTime.MinValue;
    private static DateTime _allowFileStamp = DateTime.MinValue;
    private static readonly object _allowLock = new();

    /// <summary>
    /// True unless allowlist mode is active and this IP is not on the list. An empty or
    /// missing list means "mode off" - allow everyone.
    /// </summary>
    private static bool IsAllowed(string ip)
    {
        var list = LoadAllowlist();
        if (list.Count == 0) return true;      // mode off
        return list.Contains(ip);
    }

    private static HashSet<string> LoadAllowlist()
    {
        // Cheap re-check: only touch the disk every few seconds, and only reparse when the
        // file's timestamp actually moved.
        var now = DateTime.UtcNow;
        if (_allow != null && (now - _allowLoadedAt).TotalSeconds < 5)
            return _allow;

        lock (_allowLock)
        {
            if (_allow != null && (now - _allowLoadedAt).TotalSeconds < 5)
                return _allow;

            _allowLoadedAt = now;
            try
            {
                if (!File.Exists(AllowlistPath))
                {
                    _allow = new HashSet<string>();
                    return _allow;
                }

                var stamp = File.GetLastWriteTimeUtc(AllowlistPath);
                if (_allow != null && stamp == _allowFileStamp)
                    return _allow;
                _allowFileStamp = stamp;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(AllowlistPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    set.Add(line);
                }

                if (set.Count > 0)
                    Console.WriteLine($"[allowlist] active - {set.Count} address(es) may reach the server; all others get 502");
                else
                    Console.WriteLine("[allowlist] file present but empty - allowlist mode OFF");

                _allow = set;
            }
            catch (Exception ex)
            {
                // Never lock everyone out over a read error - fail OPEN.
                Console.WriteLine($"[allowlist] could not read {AllowlistPath} ({ex.Message}) - allowing all");
                _allow = new HashSet<string>();
            }

            return _allow;
        }
    }

    private static bool IsBanned(string ip)
    {
        var now = DateTime.UtcNow;

        if (_cache.TryGetValue(ip, out var hit) && (now - hit.Checked).TotalSeconds < CacheSeconds)
            return hit.Banned;

        bool banned;
        try
        {
            banned = BanDB.IsIpBanned(ip);
        }
        catch (Exception ex)
        {
            // Never take the server down over a ban lookup - fail open.
            Console.WriteLine($"[banip] lookup failed for {ip}: {ex.Message}");
            banned = false;
        }

        _cache[ip] = (banned, now);

        if (banned)
            Console.WriteLine($"[banip] 502 for banned IP {ip}");

        return banned;
    }

    private static string? ClientIp(HttpContext context)
    {
        // Same order the rest of the server uses: Cloudflare, then a generic proxy header,
        // then the socket. X-Forwarded-For can be a list - the client is the first entry.
        string? ip = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(ip))
        {
            string? xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(xff))
                ip = xff.Split(',')[0].Trim();
        }

        if (string.IsNullOrWhiteSpace(ip))
            ip = context.Connection.RemoteIpAddress?.ToString();

        return string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
    }

    private static bool IsLoopback(string ip)
    {
        if (ip == "::1" || ip.StartsWith("127.")) return true;
        return IPAddress.TryParse(ip, out var parsed) && IPAddress.IsLoopback(parsed);
    }
}
