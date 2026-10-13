using System.Collections.Concurrent;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Middleware
{
    /// <summary>
    /// Which host name may show what. Every Campus host name reaches the SAME app (Caddy just proxies it), so without this the
    /// website, the admin panel and the game API would all be visible on every one of them.
    ///
    ///   camprec.net          the website, and the game API (older launchers / patches / Quest builds still use this host for the API)
    ///   admin.camprec.net    the admin panel and the game API it calls for its own data - no website
    ///   api.camprec.net      the game API (what /ns hands the game) - no website, no admin
    ///   ns.camprec.net       the name-server answer (/ and /ns) only
    ///   cdn.camprec.net      /cdn only
    ///   gameauth.ddns.net    the old host: the game API for builds installed before the swap, the college portfolio (/collegeportfolio)
    ///   (and gameauthcdn...) and the sign-in callbacks registered with Discord / Spotify / the social apps. No website, no admin.
    ///
    /// A website page asked for on the wrong host is sent to camprec.net (a link or a bookmark still lands somewhere);
    /// the admin panel on the wrong host is a plain 404 (apart from a GET of /admin itself, which points at admin.camprec.net).
    /// localhost / 127.0.0.1 (the SSH tunnel, a developer's own copy) is never restricted: that is how the panel is reached
    /// without the internet. ConfigCampus.HostRouting = false switches all of this off.
    /// </summary>
    public class HostRoutingMiddleware
    {
        private readonly RequestDelegate _next;

        public const string SiteHost = "camprec.net";
        public const string AdminHost = "admin.camprec.net";
        public const string ApiHost = "api.camprec.net";
        public const string NsHost = "ns.camprec.net";
        public const string CdnHost = "cdn.camprec.net";

        private enum HostKind { Local, Site, Admin, Api, Ns, Cdn, Legacy, Other }
        private enum PathKind { Admin, AdminShared, Site, SiteApi, Callback, Portfolio, Root, Cdn, Game }

        // Pages / files that make up the website (WebController + the Website folder). Matched exactly or by prefix at the ROOT only.
        private static readonly string[] SiteExact =
        {
            "/home", "/site", "/profile", "/news", "/rooms", "/patch-notes", "/support", "/weekly", "/challenges", "/settings",
            "/stats", "/login", "/download", "/accessibility", "/signup", "/campus.exe", "/messages",
        };
        private static readonly string[] SitePrefix = { "/user/", "/room/", "/event/", "/image/", "/support/" };
        // The names inside the Website folder (app.js, style.css, assets/ ...): served at the root, so they are website too.
        private static readonly HashSet<string> SiteFiles = LoadSiteFiles();

        private static HashSet<string> LoadSiteFiles()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = Path.Combine(Environment.CurrentDirectory, "Website");
                if (Directory.Exists(dir))
                    foreach (var e in Directory.EnumerateFileSystemEntries(dir)) set.Add(Path.GetFileName(e));
            }
            catch { }
            return set;
        }

        public HostRoutingMiddleware(RequestDelegate next) { _next = next; }

        public async Task InvokeAsync(HttpContext ctx)
        {
            if (!ConfigCampus.HostRouting) { await _next(ctx); return; }

            var kind = KindOf(ctx);
            if (kind == HostKind.Local) { await _next(ctx); return; }

            string path = ctx.Request.Path.Value ?? "/";
            var pk = Classify(path);
            bool get = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method);

            // "/" is the name server for the game and the home page for a browser
            if (pk == PathKind.Root)
            {
                if (WantsHtml(ctx.Request))
                {
                    if (kind == HostKind.Site) { await _next(ctx); return; }
                    ctx.Response.Redirect(kind == HostKind.Admin ? "/admin" : "https://" + SiteHost + "/", permanent: false);
                    return;
                }
                if (kind == HostKind.Cdn) { NotFound(ctx); return; }
                await _next(ctx);       // the game asking for the name server
                return;
            }

            bool allowed = Allowed(kind, pk, path);
            if (allowed) { await _next(ctx); return; }

            // a website page asked for on the wrong host: point at the real one
            if (get && (pk == PathKind.Site || pk == PathKind.Portfolio) && kind != HostKind.Ns && kind != HostKind.Cdn)
            {
                ctx.Response.Redirect("https://" + SiteHost + path + ctx.Request.QueryString, permanent: false);
                return;
            }
            // a bare GET of the panel's own address on the wrong host
            if (get && pk == PathKind.Admin && path.TrimEnd('/').Equals("/admin", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Redirect("https://" + AdminHost + "/admin", permanent: false);
                return;
            }
            NotFound(ctx);
        }

        private static readonly bool Debug = Environment.GetEnvironmentVariable("CAMPUS_HOSTROUTING_DEBUG") == "1";   // tests: say which 404s are ours

        private static void NotFound(HttpContext ctx)
        {
            if (Debug) ctx.Response.Headers["X-Host-Routing"] = "blocked";
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        }

        private static bool Allowed(HostKind host, PathKind pk, string path)
        {
            switch (host)
            {
                case HostKind.Site:
                    return pk != PathKind.Admin;                       // everything but the admin panel (its assets are shared with the website's own images)
                case HostKind.Admin:
                    // the panel itself, and the game API it calls for its own data (/Accounts/account/me, rooms, images ...): everything except the
                    // website's pages and API, the college portfolio and the sign-in callbacks
                    return pk == PathKind.Admin || pk == PathKind.AdminShared || pk == PathKind.Game || pk == PathKind.Cdn
                        || path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase);   // the Discord / password sign-in the panel uses
                case HostKind.Ns:
                    return false;                                      // only "/" (handled above) and /ns
                case HostKind.Cdn:
                    return pk == PathKind.Cdn;
                case HostKind.Legacy:
                    return pk == PathKind.Game || pk == PathKind.Cdn || pk == PathKind.AdminShared || pk == PathKind.Callback || pk == PathKind.Portfolio;
                default:                                               // api.camprec.net and any other name that reaches us
                    return pk == PathKind.Game || pk == PathKind.Cdn || pk == PathKind.AdminShared;
            }
        }

        private static PathKind Classify(string p)
        {
            if (p == "/" || p.Length == 0) return PathKind.Root;
            if (p.Equals("/ns", StringComparison.OrdinalIgnoreCase)) return PathKind.Root;      // the name server: handled like "/"
            if (p.StartsWith("/admin-assets", StringComparison.OrdinalIgnoreCase)) return PathKind.Admin;          // the panel's own branding images
            if (p.Equals("/admin", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/api/web/admin", StringComparison.OrdinalIgnoreCase)) return PathKind.Admin;
            if (p.StartsWith("/collegeportfolio", StringComparison.OrdinalIgnoreCase)) return PathKind.Portfolio;
            // sign-in callbacks registered with Discord / Spotify / the social apps (they land on gameauth.ddns.net)
            if (p.StartsWith("/api/auth/discord/", StringComparison.OrdinalIgnoreCase)
                || p.Equals("/api/site/spotify/callback", StringComparison.OrdinalIgnoreCase)
                || (p.StartsWith("/api/site/social/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("/callback", StringComparison.OrdinalIgnoreCase)))
                return PathKind.Callback;
            if (p.StartsWith("/api/site/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/api/search/", StringComparison.OrdinalIgnoreCase)
                || p.Equals("/api/profile-image", StringComparison.OrdinalIgnoreCase)) return PathKind.SiteApi;
            if (p.StartsWith("/img/", StringComparison.OrdinalIgnoreCase) || p.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                || p.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase)) return PathKind.AdminShared;
            if (p.StartsWith("/cdn/", StringComparison.OrdinalIgnoreCase) || p.Equals("/cdn", StringComparison.OrdinalIgnoreCase)) return PathKind.Cdn;
            foreach (var e in SiteExact) if (p.Equals(e, StringComparison.OrdinalIgnoreCase)) return PathKind.Site;
            foreach (var e in SitePrefix) if (p.StartsWith(e, StringComparison.OrdinalIgnoreCase)) return PathKind.Site;
            if (p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', 1) < 0) return PathKind.Site;     // /{page}.html
            int slash = p.IndexOf('/', 1);
            string first = slash < 0 ? p.Substring(1) : p.Substring(1, slash - 1);
            if (SiteFiles.Contains(first)) return PathKind.Site;                                                         // app.js, style.css, assets/...
            return PathKind.Game;
        }

        private static HostKind KindOf(HttpContext ctx)
        {
            string host = ctx.Request.Host.Host.ToLowerInvariant();
            // the SSH tunnel / a developer's own copy: a localhost Host that did NOT come through Caddy (Caddy adds X-Forwarded-For)
            if ((host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]")
                && !ctx.Request.Headers.ContainsKey("X-Forwarded-For")) return HostKind.Local;
            switch (host)
            {
                case SiteHost: case "www." + SiteHost: return HostKind.Site;
                case AdminHost: return HostKind.Admin;
                case ApiHost: return HostKind.Api;
                case NsHost: return HostKind.Ns;
                case CdnHost: return HostKind.Cdn;
                case "gameauth.ddns.net": case "gameauthcdn.ddns.net": return HostKind.Legacy;
                default: return HostKind.Other;
            }
        }

        // same test WebController.NsRoot uses: a browser, not the game
        private static bool WantsHtml(HttpRequest req)
        {
            if (!HttpMethods.IsGet(req.Method)) return false;
            if (req.Headers.UserAgent.ToString().Contains("BestHTTP", StringComparison.OrdinalIgnoreCase)) return false;
            return req.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The Discord admin sign-in lands on gameauth.ddns.net (the address registered with Discord), but the panel lives on
    /// admin.camprec.net, and a cookie set on one host never reaches the other. So the callback leaves the signed token here
    /// under a one-time ticket (60 s) and sends the browser to admin.camprec.net/admin/discord-finish?t=..., which takes it
    /// and sets the cookie on the panel's own host.
    /// </summary>
    public static class AdminHandoff
    {
        private static readonly ConcurrentDictionary<string, (string jwt, DateTime until)> _t = new();

        public static string Create(string jwt)
        {
            foreach (var kv in _t) if (kv.Value.until < DateTime.UtcNow) _t.TryRemove(kv.Key, out _);
            string id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _t[id] = (jwt, DateTime.UtcNow.AddSeconds(60));
            return id;
        }

        public static string? Take(string? id)
        {
            if (string.IsNullOrEmpty(id) || !_t.TryRemove(id, out var v)) return null;
            return v.until >= DateTime.UtcNow ? v.jwt : null;
        }
    }
}
