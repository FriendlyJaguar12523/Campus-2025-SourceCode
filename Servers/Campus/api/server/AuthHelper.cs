using System.Security.Principal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

public static class AuthHelper
{
    public static (player_info? account, ContentResult? error) ValidateToken(HttpContext httpContext)
    {
        // Accept the JWT from EITHER place, so the same game endpoints serve both callers:
        //   - IN-GAME: the client sends it in the Authorization HEADER.
        //   - WEB: the admin panel is a browser and holds it in the Authorization COOKIE (set at
        //     admin login). Without this fallback, panel calls to game endpoints like
        //     /Accounts/account/me would 401 even after passing the patch gate.
        // The patch gate already ensures only the game client or a logged-in admin reaches here.
        string token = httpContext.Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(token))
            token = httpContext.Request.Cookies["Authorization"] ?? "";
        // Reject WEBSITE tokens on game endpoints: a camprec.net site session must not be
        // usable in-game. Admin tokens are intentionally allowed here - the admin panel is a
        // browser that legitimately calls game endpoints (e.g. /Accounts/account/me) with its
        // cookie. Game and legacy (no-surface) tokens pass.
        string playerData = ClientSecurity.DecodeToken(token, rejectSurfaces: new[] { "website" });

        if (string.IsNullOrEmpty(playerData) || playerData.StartsWith("error_"))
        {
            return (null, new ContentResult
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 401
            });
        }


        player_info account = JsonConvert.DeserializeObject<player_info>(playerData);
        return (account, null);
    }

    /// <summary>The caller's account id when the request carries a valid GAME token, otherwise 0. Never an error:
    /// the account lookups are open to anyone, and knowing the viewer only changes WHAT they are shown (their own
    /// friend nicknames), never whether they are answered.</summary>
    public static ulong OptionalViewerId(HttpContext httpContext)
    {
        try { var (a, err) = ValidateToken(httpContext); return err == null && a != null ? (ulong)a.Id : 0UL; }
        catch { return 0UL; }
    }

    public static (player_info? account, ContentResult? error) ValidateTokenDev(HttpContext httpContext)
    {
        string? temp2 = httpContext.Request.Cookies["Authorization"];
        // Admin endpoints accept ONLY admin (or legacy/launcher no-surface) tokens: reject both
        // website tokens and plain in-game tokens so neither can reach the admin panel. The
        // dev-flag check below still applies on top.
        string playerData = ClientSecurity.DecodeToken(temp2, rejectSurfaces: new[] { "website", "game" });
        if (string.IsNullOrEmpty(playerData) || playerData.StartsWith("error_"))
        {
            return (null, new ContentResult()
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 401
            });
        }

        player_info account = JsonConvert.DeserializeObject<player_info>(playerData);

        bool flag = PlayerDB.CheckDevFlag(account.Id);
        if (!flag)
            return (null, new ContentResult()
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 401
            });


        return (account, null);
    }

    /// <summary>Like ValidateTokenDev, but also accepts a mod-flagged (not just dev-flagged) account. Use
    /// this ONLY on the specific endpoints mods are meant to reach (the admin page shell/script, Dashboard,
    /// Players view, Reports, Ban Logs/moderation actions, Chat view) - every other admin endpoint keeps
    /// calling ValidateTokenDev so a mod account gets exactly that slice, not the whole panel.</summary>
    public static (player_info? account, ContentResult? error) ValidateTokenDevOrMod(HttpContext httpContext)
    {
        string? temp2 = httpContext.Request.Cookies["Authorization"];
        string playerData = ClientSecurity.DecodeToken(temp2, rejectSurfaces: new[] { "website", "game" });
        if (string.IsNullOrEmpty(playerData) || playerData.StartsWith("error_"))
        {
            return (null, new ContentResult()
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 401
            });
        }

        player_info account = JsonConvert.DeserializeObject<player_info>(playerData);

        if (!PlayerDB.CheckDevOrModFlag(account.Id))
            return (null, new ContentResult()
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 401
            });

        return (account, null);
    }
}
