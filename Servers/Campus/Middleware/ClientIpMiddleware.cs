using System.Net;

namespace Campus_live_rewrite.Middleware
{
    /// <summary>
    /// The player's real address, now that Cloudflare sits in front of Caddy.
    ///
    /// Every per-IP defence in this server (TrafficGuard, LoginGuard, the website sign-up limiter, bans, the IP allowlist) keys on the LAST
    /// X-Forwarded-For entry, which used to be the player because Caddy appended the connecting peer. Behind Cloudflare the connecting peer is
    /// a Cloudflare edge address, so every player on that edge would share one address: one abuser rate-limits, locks out or IP-bans thousands.
    ///
    /// Caddy (Deploy/harden-vps.sh, "cloudflare" mode) works the real address out itself - it only believes Cloudflare's CF-Connecting-IP
    /// when the connection really comes from a Cloudflare range - and hands it to this server in <c>X-Campus-Client-IP</c>, REPLACING any value a
    /// client sent. This middleware, first in the pipeline, copies that value into X-Forwarded-For (and CF-Connecting-IP, which some older code
    /// reads first), so every existing check sees the real player without being touched.
    ///
    /// Trust: only a request whose socket peer is this machine (the proxy) is believed, and the header is only meaningful because Caddy
    /// overwrites it on every request it forwards. Without that Caddy configuration the header is absent and nothing changes.
    /// </summary>
    public class ClientIpMiddleware
    {
        public const string Header = "X-Campus-Client-IP";
        private readonly RequestDelegate _next;

        public ClientIpMiddleware(RequestDelegate next) => _next = next;

        public Task Invoke(HttpContext context)
        {
            var remote = context.Connection.RemoteIpAddress;
            bool fromProxy = remote == null || IPAddress.IsLoopback(remote)
                || (context.Connection.LocalIpAddress != null && remote.Equals(context.Connection.LocalIpAddress));
            if (fromProxy && context.Request.Headers.TryGetValue(Header, out var v))
            {
                string ip = v.ToString().Trim();
                if (IPAddress.TryParse(ip, out var parsed))
                {
                    ip = parsed.ToString();
                    context.Request.Headers["X-Forwarded-For"] = ip;
                    context.Request.Headers["CF-Connecting-IP"] = ip;
                }
            }
            return _next(context);
        }
    }
}
