using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Campus_live_rewrite.api.server;

// Two gates in one place - the server is the gatekeeper for BOTH ways in:
//
//  1. GAME clients (the DLL): the patch stamps the shared-secret header X-Campus-Patch on EVERY
//     request (its SendRequest hook). The server checks that header on EVERY request here - a
//     match passes, anything else is 403'd. There is NO whitelist and NO time window: an old/no/
//     tampered patch that doesn't stamp the exact secret is denied on every single call, every
//     time, so there's nothing to "ride" after validating once.
//
//  2. The ADMIN panel (a browser, no DLL): can't stamp the header, so it is gated by a short-lived
//     WEB SESSION TOKEN instead. You do NOT type it - a correct username/password login MINTS a
//     signed, self-expiring token (AdminGate) into a cookie. The rest of the admin surface (home +
//     /admin/api/*) requires a live token; when it expires you're bounced back to the login to
//     mint a fresh one. The login page/API stay reachable so you can log in.

// The admin web session token. You do NOT type this - it is MINTED when you log in (correct
// username/password) and expires after a short period, like a bearer token. It's a signed,
// self-expiring value: "<expiryUnix>.<HMAC(expiryUnix)>", keyed by ConfigCampus.AdminWebToken.
// The server can validate it (signature + not-expired) without storing any session state, and
// once it lapses the browser is bounced back to the login to mint a fresh one.
public static class AdminGate
{
    public const string CookieName = "admin_session";

    // Short lifetime - a stale tab can't sit logged into admin indefinitely, and the whole admin
    // web session resets every hour (re-login required). Change here if needed.
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private static string Sign(string payload)
    {
        var key = Encoding.UTF8.GetBytes(ConfigCampus.AdminWebToken ?? "");
        using var h = new HMACSHA256(key);
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    // Mint a fresh token at login.
    public static string Issue()
    {
        string payload = DateTimeOffset.UtcNow.Add(Ttl).ToUnixTimeSeconds().ToString();
        return payload + "." + Sign(payload);
    }

    public static bool IsValid(HttpContext ctx)
    {
        string? c = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(c) || string.IsNullOrEmpty(ConfigCampus.AdminWebToken)) return false;

        int dot = c.IndexOf('.');
        if (dot <= 0 || dot >= c.Length - 1) return false;
        string payload = c.Substring(0, dot);
        string sig = c.Substring(dot + 1);

        // Signature must match (constant-time) ...
        var a = Encoding.UTF8.GetBytes(sig);
        var b = Encoding.UTF8.GetBytes(Sign(payload));
        if (!CryptographicOperations.FixedTimeEquals(a, b)) return false;

        // ... and the embedded expiry must still be in the future.
        if (!long.TryParse(payload, out long exp)) return false;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp;
    }
}

public class PatchGateMiddleware
{
    private readonly RequestDelegate _next;

    // Master switch for the GAME (patch) gate. The admin web-token gate is always on.
    //
    // TURNED OFF 2026-09-01, and it must stay off until the clients stamp a header again.
    //
    // Both patches used to call BestHTTP HTTPRequest.SetHeader on every request to stamp
    // X-Campus-Patch. That stamping has been removed from the PC patch (SendRequest.h) and
    // the Quest patch (campus_hook.cpp), so NO client sends the header any more. With
    // Enable = true, this middleware would 403 every single game call and nobody could log in.
    //
    // WHAT THIS COSTS: the server can no longer distinguish a patched client from an old,
    // absent or tampered one. The game API is open to anything that can reach it. IP bans
    // (BannedIpMiddleware) and per-endpoint JWT validation still apply - this only removes
    // the "must be running our DLL" check.
    //
    // The startup check (POST /api/patch/verify) still works and is still enforced client-side:
    // both patches build that request with their own HTTP client and write the header by hand,
    // which needs no game offsets. It is a startup gate, not per-request enforcement.
    public static bool Enable = false;

    // Header the patch stamps on every request (matches CampusPatch SendRequest.h).
    public const string PatchHeaderName = "X-Campus-Patch";

    public PatchGateMiddleware(RequestDelegate next) => _next = next;

    // The admin surface that the web token protects: the panel, and its dev-login API.
    private static bool IsAdminSurface(string p) =>
        p.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/api/web/admin", StringComparison.OrdinalIgnoreCase);

    // The login entry point: the login PAGE (where the token box lives) and the login API (which
    // checks the token itself). These must be reachable WITHOUT the gate cookie - otherwise there'd
    // be nowhere to enter the token. Bare /admin roots just redirect to the login, so allow them
    // too. Everything else on the admin surface requires the gate cookie a good login stamps.
    private static bool IsAdminLoginEntry(string p) =>
        p.StartsWith("/api/web/admin/login", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith("/login", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/admin", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/admin/", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/admin/home", StringComparison.OrdinalIgnoreCase);

    // Reachable with NO patch header at all - the public browser surface. This is the WEBSITE
    // base (landing page + the Campus_Net site and its login), which is meant for everyone, not
    // just the game client. IP bans still apply to all of it because BannedIpMiddleware runs
    // BEFORE this. Only the actual game API (everything not listed here or under the admin gate)
    // requires the patch header.
    private static bool IsOpen(string p) =>
        p.StartsWith("/api/patch/verify", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/api/patch/proof", StringComparison.OrdinalIgnoreCase)   // the login gate's proof (PatchProofs)
        || p.Equals("/", StringComparison.OrdinalIgnoreCase)                     // website base / landing page
        || p.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/img", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/cdn", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/Web", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/favicon.png", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/DefaultProfileImage.jpg", StringComparison.OrdinalIgnoreCase)
        // ---- Campus website (ContentRoot/Website), served by Kestrel since the Flask
        // backend was retired. A browser cannot stamp X-Campus-Patch, so every asset and
        // every site API call has to be open or the whole site 403s. This is the PUBLIC
        // site only: /api/site/* is account signup/login/me for the website and reads the
        // same PlayerDB the game does, but it exposes nothing the game API does.
        || IsSiteAsset(p)
        || p.StartsWith("/api/site/", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/api/search/", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
        // Tachyon voice server's room lookup (GET /tachyon?id=<AccountId>). Server-to-server,
        // no patch header - must stay reachable even if the patch gate is switched on.
        || p.Equals("/tachyon", StringComparison.OrdinalIgnoreCase)
        // Public website clean URLs (WebController) + the launcher download. Browser pages, no
        // patch header - keep them open like the rest of the site.
        || p.Equals("/campus.exe", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/profile", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/news", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/rooms", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/patch-notes", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/weekly", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/challenges", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/settings", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/stats", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/messages", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/download", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/accessibility", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/user/", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/room/", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("/image/", StringComparison.OrdinalIgnoreCase);

    // Static files that make up the website. Matched by extension rather than by listing every
    // page, so adding a page to Website/ does not silently 403 until someone remembers to come
    // back here. Extensions are deliberately narrow - no .py/.bat/.json - so a stray file
    // dropped in the web root is not served just because it is in the folder.
    private static bool IsSiteAsset(string p)
    {
        // IsOpen is evaluated BEFORE IsAdminSurface in Invoke, so a bare extension match here
        // would hand out the admin panel to anyone who asked for /admin/<something>.html.
        // The admin surface is never a public site asset.
        if (IsAdminSurface(p)) return false;

        if (p.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var ext in SiteAssetExtensions)
            if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static readonly string[] SiteAssetExtensions =
        { ".html", ".css", ".js", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico", ".woff", ".woff2" };

    public static string ClientIp(HttpContext ctx) =>
        ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
        ?? ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()
        ?? ctx.Connection.RemoteIpAddress?.ToString()
        ?? "";

    public async Task Invoke(HttpContext context)
    {
        string path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";

        if (IsOpen(path))
        {
            await _next(context);
            return;
        }

        // ---- Admin surface: gated by the WEB TOKEN (browser, no DLL) ----
        if (IsAdminSurface(path))
        {
            // The login page/API is where the token is entered, so it's always reachable.
            if (IsAdminLoginEntry(path))
            {
                await _next(context);
                return;
            }
            // Rest of the panel (home + /admin/api/*) needs a live session token from login.
            if (AdminGate.IsValid(context))
            {
                await _next(context);
                return;
            }
            // No gate cookie: APIs get a clean 403; pages fall back to the login screen.
            if (path.StartsWith("/api/web/admin", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("");
            }
            else
            {
                context.Response.Redirect("/admin/v4/login");
            }
            return;
        }

        // ---- Everything else: the GAME patch gate (per-request header) ----
        if (!Enable)
        {
            await _next(context);
            return;
        }

        // Accept EITHER form of auth on a game endpoint:
        //
        //  (a) IN-GAME auth: the patch signature header, stamped by the DLL on every request.
        //  (b) WEB auth: a live admin session token. The admin panel is a browser (no DLL, so no
        //      patch header), but its dashboard legitimately calls game endpoints like
        //      /Accounts/account/me. A valid admin session proves an authenticated operator, so
        //      let those calls through too - otherwise the panel 403s on its own data.
        //
        // So: game client OR logged-in admin passes; a random browser with neither is denied.
        string sent = context.Request.Headers[PatchHeaderName].FirstOrDefault() ?? "";
        string expected = ConfigCampus.PatchSignature ?? "";
        bool inGameAuth = !string.IsNullOrEmpty(expected)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(expected));

        if (inGameAuth || AdminGate.IsValid(context))
        {
            await _next(context);
            return;
        }

        // Genuine server-internal calls (health checks, loopback) don't come through the proxy and
        // carry no forwarded IP, so they can't stamp the header - let those through. Real clients
        // always arrive with a forwarded IP, so this never opens the gate for them.
        string clientIp = ClientIp(context);
        if (System.Net.IPAddress.TryParse(clientIp, out var parsed) && System.Net.IPAddress.IsLoopback(parsed))
        {
            await _next(context);
            return;
        }

        var ua = context.Request.Headers["User-Agent"].FirstOrDefault();
        Console.WriteLine($"[patchgate] DENIED {context.Request.Method} {path} from {clientIp} - patch header {(string.IsNullOrEmpty(sent) ? "absent" : "mismatch")} (UA={ua})");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("");
    }
}
