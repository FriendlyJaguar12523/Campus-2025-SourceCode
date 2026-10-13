using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Brute-force lockout for the credential endpoints: after ConfigCampus.LoginFailBlockThreshold
    /// FAILED logins from one IP inside LoginFailWindowSeconds, that IP is locked out of the login
    /// endpoint for LoginFailBlockMinutes. Unlike the TrafficGuard rate limiter (which counts EVERY
    /// auth request), this only counts FAILURES - a legit client logging in successfully never trips
    /// it, and one success clears the counter.
    ///
    /// The lockout is enforced in TrafficGuardMiddleware (before the controller) and the failures are
    /// recorded by AuthController on a bad password. Keyed on the trustworthy LAST X-Forwarded-For IP
    /// (Caddy-appended; a client-sent XFF can't rotate it). Loopback is exempt.
    /// </summary>
    public static class LoginGuard
    {
        private sealed class State
        {
            public int Count;
            public long WindowStart;   // unix seconds
            public long LockedUntil;   // unix seconds, 0 = not locked
        }

        private static readonly ConcurrentDictionary<string, State> _byIp = new();
        private static long _lastSweep;

        public static bool IsLockedOut(string ip, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (!ConfigCampus.LoginFailBlockEnabled || string.IsNullOrWhiteSpace(ip) || IsLoopback(ip))
                return false;
            if (AdminProtectionMiddleware.IsLimitExempt(ip)) return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_byIp.TryGetValue(ip, out var s) && s.LockedUntil > now)
            {
                retryAfterSeconds = (int)(s.LockedUntil - now);
                return true;
            }
            return false;
        }

        /// <summary>Record a FAILED login. Trips the lockout once the threshold is reached.</summary>
        public static void RecordFailure(string ip)
        {
            if (!ConfigCampus.LoginFailBlockEnabled || string.IsNullOrWhiteSpace(ip) || IsLoopback(ip))
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Sweep(now);

            var s = _byIp.AddOrUpdate(ip,
                _ => new State { Count = 1, WindowStart = now },
                (_, cur) =>
                {
                    // A fresh counting window starts when the old one elapsed AND we aren't mid-lockout.
                    if (cur.LockedUntil <= now && now - cur.WindowStart >= ConfigCampus.LoginFailWindowSeconds)
                    {
                        cur.Count = 1;
                        cur.WindowStart = now;
                    }
                    else
                    {
                        cur.Count++;
                    }
                    return cur;
                });

            if (s.LockedUntil <= now && s.Count >= ConfigCampus.LoginFailBlockThreshold)
            {
                s.LockedUntil = now + (long)ConfigCampus.LoginFailBlockMinutes * 60;
                Console.WriteLine($"[loginguard] {ip} LOCKED OUT for {ConfigCampus.LoginFailBlockMinutes} min after {s.Count} failed logins");
            }
        }

        /// <summary>A successful login clears that IP's failure counter and any lockout.</summary>
        public static void RecordSuccess(string ip)
        {
            if (!string.IsNullOrWhiteSpace(ip)) _byIp.TryRemove(ip, out _);
        }

        /// <summary>Trustworthy client IP = the LAST X-Forwarded-For entry (Caddy-appended), else the socket.</summary>
        public static string ClientIp(HttpContext ctx)
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

        private static void Sweep(long now)
        {
            if (now - Interlocked.Read(ref _lastSweep) < 300) return;
            Interlocked.Exchange(ref _lastSweep, now);
            foreach (var kv in _byIp)
                if (kv.Value.LockedUntil <= now && now - kv.Value.WindowStart > ConfigCampus.LoginFailWindowSeconds + 60)
                    _byIp.TryRemove(kv.Key, out _);
        }
    }
}
