using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using Newtonsoft.Json;

/// <summary>
/// A banned account can still sign in to the WEBSITE (every page shows the ban banner with the
/// time left), but it can't change anything. This refuses every write a banned website session
/// makes - profile picture, banner, bio, email, photos, friends, reports, social links - with a
/// 403 the site shows as a message. One gate for all of them, so a route added later is covered
/// without anyone remembering to add a check.
///
/// Still allowed while banned: signing in and out, registering, the support desk (tickets are
/// how a player appeals) and dismissing notifications. Reads (GET) are never touched.
/// </summary>
public class SiteBanGateMiddleware
{
    private readonly RequestDelegate _next;
    public SiteBanGateMiddleware(RequestDelegate next) => _next = next;

    private const string SiteCookie = "campus_site";

    private static readonly string[] AllowedWhileBanned =
    {
        "/api/site/login", "/api/site/logout", "/api/site/register",
        "/api/site/support/", "/api/site/notifications/",
    };

    private static bool IsWrite(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method) || HttpMethods.IsPatch(method);

    public async Task Invoke(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        bool siteWrite = IsWrite(context.Request.Method)
                         && (path.StartsWith("/api/site/", StringComparison.OrdinalIgnoreCase)
                             || path.Equals("/api/profile-image", StringComparison.OrdinalIgnoreCase));
        if (!siteWrite || AllowedWhileBanned.Any(a => path.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        ulong accountId = 0;
        string token = context.Request.Cookies[SiteCookie] ?? "";
        if (token.Length > 0)
        {
            string data = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (!string.IsNullOrEmpty(data) && !data.StartsWith("error_"))
            {
                try { accountId = JsonConvert.DeserializeObject<player_info>(data)?.Id ?? 0; } catch { }
            }
        }
        var ban = accountId == 0 ? null : PlayerDB.GetSiteBan(accountId);
        if (ban == null)
        {
            await _next(context);
            return;
        }

        string left = ban.RemainingSeconds == null ? "permanently" : "for " + Describe(ban.RemainingSeconds.Value);
        Console.WriteLine($"[site-ban] refused {context.Request.Method} {path} from banned account {accountId}");
        context.Response.StatusCode = 403;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonConvert.SerializeObject(new
        {
            ok = false,
            banned = true,
            error = $"This account is banned {left}. You can't change anything until the ban ends."
        }));
    }

    public static string Describe(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{t.Seconds}s";
    }
}
