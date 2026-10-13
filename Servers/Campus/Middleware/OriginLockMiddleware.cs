using System.Net;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Middleware
{
    /// <summary>
    /// Only the reverse proxy may talk to this server.
    ///
    /// Every real request reaches Kestrel through Caddy, so its socket peer is this machine (127.0.0.1). The app nevertheless listens on
    /// 0.0.0.0:8080 (the systemd unit's ASPNETCORE_URLS), which made http://&lt;vps ip&gt;:8080/ answer from the open internet:
    ///   * it bypassed Caddy entirely (TLS, its timeouts, anything added there later), and
    ///   * every per-IP defence here (TrafficGuard, bans, the IP allowlist, login lockout) keys on the LAST X-Forwarded-For entry, which Caddy
    ///     appends - a client talking to :8080 directly can send any X-Forwarded-For it likes and walk straight past all of them.
    /// A connection from anywhere else is dropped without an answer. A peer that is this machine's own address (a local service reaching the
    /// app by the public IP) is allowed as well. CAMPUS_ORIGIN_OPEN=1 (or ConfigCampus.OriginLock = false) turns the lock off, for running the
    /// server on a LAN without a proxy.
    /// </summary>
    public class OriginLockMiddleware
    {
        private readonly RequestDelegate _next;
        private static readonly bool EnvOpen = Environment.GetEnvironmentVariable("CAMPUS_ORIGIN_OPEN") == "1";
        private static long _dropped;

        public OriginLockMiddleware(RequestDelegate next) => _next = next;

        public async Task Invoke(HttpContext context)
        {
            if (!ConfigCampus.OriginLock || EnvOpen) { await _next(context); return; }
            var remote = context.Connection.RemoteIpAddress;
            if (remote == null || IPAddress.IsLoopback(remote) || (context.Connection.LocalIpAddress != null && remote.Equals(context.Connection.LocalIpAddress)))
            {
                await _next(context);
                return;
            }
            long n = Interlocked.Increment(ref _dropped);
            if (n <= 3 || n % 1000 == 0) Console.WriteLine($"[originlock] dropped a direct connection from {remote} ({n} so far) - only the proxy may reach this port");
            context.Abort();
        }
    }
}
