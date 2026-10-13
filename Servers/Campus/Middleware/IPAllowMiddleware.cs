using Campus_live_rewrite.api.server;

public class IPAllowMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly HashSet<string> _allowedIPs = new()
    {
        "47.198.33.69",
        "50.114.179.17"
        //"96.27.234.110"
    };

    // Disabled: public Rec Room game server, not a staging box. Reference server's whitelist
    // was E12354's own dev IP (47.198.33.69). Leave the plumbing so a future operator can
    // toggle back on + populate _allowedIPs, but OFF by default so every player can reach us.
    public static bool IPAllowMiddleware_enable = false;

    public IPAllowMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!IPAllowMiddleware_enable)
        {
            await _next(context);
            return;
        }

        string? remoteIpString = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? context.Connection.RemoteIpAddress?.ToString();

        string? useragent = context.Request.Headers["User-Agent"].FirstOrDefault();

        if (string.IsNullOrEmpty(remoteIpString))
        {
            Deny(context, remoteIpString, useragent);
            return;
        }

        if (!System.Net.IPAddress.TryParse(remoteIpString, out var remoteIp))
        {
            Deny(context, remoteIpString, useragent);
            return;
        }

        if (System.Net.IPAddress.IsLoopback(remoteIp))
        {
            await _next(context);
            return;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
        {
            remoteIp = remoteIp.MapToIPv4();
        }

        if (!_allowedIPs.Contains(remoteIp.ToString()))
        {
            await Deny(context, remoteIp.ToString(), useragent);
            return;
        }

        await _next(context);
    }

    private static async Task Deny(HttpContext context, string? ip, string? useragent)
    {
        string msg = $"Forbidden request from IP: {ip} and user agent {useragent}";
        Console.WriteLine($"[IPAllowMiddleware] {msg}");
        Webhook.Send(1, "gaylog", "uh oh!", msg, 1);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(
                ""
            );
        }
    }
}
