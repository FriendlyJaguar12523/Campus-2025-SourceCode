using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The website takes PLAIN TEXT only (2026-09-27 request): no "fancy font" letters (𝓯𝓸𝓷𝓽,
/// 𝕗𝕠𝕟𝕥, ｆｏｎｔ - Unicode look-alikes that dodge the word filter and impersonate names), no
/// zalgo marks, no invisible characters, no emoji. One gate over every JSON write to /api/site/*,
/// so a text field added later is covered too - names, bio, comments, photo captions, reports,
/// support tickets, friend nicknames, email.
///
/// What a phone keyboard inserts on its own is converted, not refused: curly quotes, dashes,
/// the ellipsis and non-breaking spaces become their plain ASCII forms. Anything else outside
/// printable ASCII (plus new lines and tabs) refuses the request with a 400 the site shows.
/// Passwords are never inspected. File uploads (multipart) are not JSON and pass untouched.
/// </summary>
public class SitePlainTextMiddleware
{
    private readonly RequestDelegate _next;
    public SitePlainTextMiddleware(RequestDelegate next) => _next = next;

    public const string Message = "Only plain text is allowed - no special fonts, symbols or emoji.";

    private static readonly HashSet<string> SkipKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "confirm_password", "current_password", "new_password", "old_password",
    };

    private static readonly Dictionary<char, string> Convert = new()
    {
        ['‘'] = "'", ['’'] = "'", ['‚'] = "'", ['′'] = "'",
        ['“'] = "\"", ['”'] = "\"", ['„'] = "\"", ['″'] = "\"",
        ['–'] = "-", ['—'] = "-", ['−'] = "-",
        ['…'] = "...",
        [' '] = " ", [' '] = " ",
    };

    public static bool IsPlainChar(char c) => (c >= ' ' && c <= '~') || c == '\n' || c == '\r' || c == '\t';

    /// <summary>The text with keyboard punctuation made plain, or null when something else is left.</summary>
    public static string? Plain(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (IsPlainChar(c)) { sb?.Append(c); continue; }
            if (!Convert.TryGetValue(c, out var rep)) return null;
            sb ??= new StringBuilder(s, 0, i, s.Length + 8);
            sb.Append(rep);
        }
        return sb?.ToString() ?? s;
    }

    public async Task Invoke(HttpContext context)
    {
        var req = context.Request;
        string path = req.Path.Value ?? "";
        bool write = HttpMethods.IsPost(req.Method) || HttpMethods.IsPut(req.Method) || HttpMethods.IsPatch(req.Method);
        // Any body that isn't a file upload or a form: some handlers read the raw body whatever the
        // Content-Type says, so a JSON body labelled text/plain must not slip past.
        string ct = req.ContentType ?? "";
        bool json = !ct.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                    && !ct.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        if (!write || !json || !path.StartsWith("/api/site/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        string body;
        using (var reader = new StreamReader(req.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        JsonNode? root = null;
        try { if (!string.IsNullOrWhiteSpace(body)) root = JsonNode.Parse(body); } catch { }

        if (root != null)
        {
            bool changed = false;
            if (!Clean(root, null, ref changed))
            {
                Console.WriteLine($"[plaintext] refused {req.Method} {path}: non-plain characters");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { ok = false, error = Message });
                return;
            }
            if (changed) body = root.ToJsonString();
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        req.Body = new MemoryStream(bytes);
        req.ContentLength = bytes.Length;
        await _next(context);
    }

    // Walks every string value (not property names); false = a value has a non-plain character.
    private static bool Clean(JsonNode node, string? key, ref bool changed)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o.ToList())
                {
                    if (kv.Value == null || SkipKeys.Contains(kv.Key)) continue;
                    if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var p = Plain(s);
                        if (p == null) return false;
                        if (!ReferenceEquals(p, s)) { o[kv.Key] = p; changed = true; }
                    }
                    else if (!Clean(kv.Value, kv.Key, ref changed)) return false;
                }
                return true;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    var item = a[i];
                    if (item == null) continue;
                    if (item is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var p = Plain(s);
                        if (p == null) return false;
                        if (!ReferenceEquals(p, s)) { a[i] = p; changed = true; }
                    }
                    else if (!Clean(item, key, ref changed)) return false;
                }
                return true;
            default:
                return true;
        }
    }
}
