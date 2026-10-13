using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Campus_live_rewrite.api.server;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.Middleware
{
    /// <summary>
    /// Serves Quest clients the Android builds of first-party clothing, the way RecFlare does.
    ///
    /// The first-party bundles (FirstPartyUGC.json saves: UnityAsset = meshes, UnityAsset2 =
    /// textures/material) are Windows builds. RecFlare converted every one for Android and hosts
    /// the result under avatar/quest/&lt;same name&gt;; its API hands a client that sends
    /// unityAssetTarget=2 (Android/Quest) every bundle name as "quest/&lt;name&gt;", and any other
    /// target the plain name (verified against api.recflare.net 2026-09-26). The HASHES must change
    /// too: UnityAssetHash / UnityAsset2Hash are the sha256 of the file and the client rejects a
    /// download that doesn't match (seen live: each quest/ bundle fetched 3x, then dropped), so
    /// each gets the quest file's hash (BundleHashes.OfQuest). The files are mirrored to Data/CDN/AvatarBlobs/quest/ (CDNController fetches
    /// a missing one from cdn.recflare.net/avatar/quest/ on demand).
    ///
    /// Only some calls carry unityAssetTarget in the query (owned, search); the busiest one,
    /// POST /api/customAvatarItems/v1/bulk, does not - and it is the FIRST clothing call after a
    /// login (the worn outfit). So the target is remembered per account AND login platform (the
    /// token's "platform" claim: Steam 0, Oculus 1...), and until that device session has sent
    /// one, the platform decides: Oculus = Quest bundles, anything else = PC. Keyed per account
    /// alone, a player who switched PC -> Quest got their worn items as PC bundles until they
    /// logged in again (2026-09-26).
    /// </summary>
    public class QuestBundleMiddleware
    {
        public const int QuestTarget = 2;

        private static readonly string[] Paths =
        {
            "/api/customAvatarItems", "/econ/customAvatarItems", "/api/items/bulkpurchase", "/outfits",
        };

        private static readonly Regex BundleName = new(
            @"""(UnityAsset2?)""\s*:\s*""(?!quest/)([^""/]+\.assetbundle)""", RegexOptions.Compiled);

        private const int OculusPlatform = 1;   // Platforms.Oculus

        private static readonly ConcurrentDictionary<(ulong account, int platform), int> _targetBySession = new();

        /// <summary>The login platform the token was issued for (its "platform" claim), -1 if unreadable.
        /// Read without re-verifying: AuthHelper.ValidateToken has already checked the signature.</summary>
        private static int TokenPlatform(HttpContext ctx)
        {
            try
            {
                string token = ctx.Request.Headers["Authorization"].ToString();
                if (string.IsNullOrEmpty(token)) token = ctx.Request.Cookies["Authorization"] ?? "";
                token = token.Replace("Bearer ", "").Trim();
                // Our game tokens have more than the usual three segments, so find the one whose
                // JSON carries the claim instead of assuming it is the second.
                foreach (var part in token.Split('.'))
                {
                    JObject payload;
                    try
                    {
                        string b64 = part.Replace('-', '+').Replace('_', '/');
                        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                        payload = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                    }
                    catch { continue; }
                    var pl = payload["platform"];
                    if (pl == null) continue;
                    if (pl.Type == JTokenType.Integer) return (int)pl;
                    return Enum.TryParse<Campus_live_rewrite.api.server.Classes.db_class.Platforms>((string?)pl, true, out var e) ? (int)e : -1;
                }
                return -1;
            }
            catch { return -1; }
        }

        private readonly RequestDelegate _next;
        public QuestBundleMiddleware(RequestDelegate next) => _next = next;

        /// <summary>Point every save at the quest/ conversion, with that file's hash.</summary>
        private static string RewriteJson(string body)
        {
            var root = JToken.Parse(body);
            var objects = root is JContainer c ? c.DescendantsAndSelf().OfType<JObject>().ToList() : new List<JObject>();
            foreach (var o in objects)
            {
                Swap(o, "UnityAsset", "UnityAssetHash");
                Swap(o, "UnityAsset2", "UnityAsset2Hash");
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void Swap(JObject save, string nameKey, string hashKey)
        {
            if (save[nameKey] is not JValue { Type: JTokenType.String } v) return;
            string name = (string)v!;
            if (!name.EndsWith(".assetbundle", StringComparison.OrdinalIgnoreCase) || name.Contains('/')) return;
            save[nameKey] = "quest/" + name;
            var h = BundleHashes.OfQuest(name);
            if (h != null) save[hashKey] = h;
        }

        public async Task InvokeAsync(HttpContext ctx)
        {
            string path = ctx.Request.Path.Value ?? "";
            if (!Paths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                await _next(ctx);
                return;
            }

            int? target = int.TryParse(ctx.Request.Query["unityAssetTarget"].ToString(), out var t) ? t : null;
            ulong? account = null;
            try { var (acc, err) = AuthHelper.ValidateToken(ctx); if (err == null && acc != null) account = acc.Id; }
            catch { }
            int platform = account.HasValue ? TokenPlatform(ctx) : -1;
            if (account is ulong id && target is int tt)
            {
                var key = (id, platform);
                int prev = _targetBySession.TryGetValue(key, out var p) ? p : (platform == OculusPlatform ? QuestTarget : 0);
                _targetBySession[key] = tt;
                if (prev != tt)
                    Console.WriteLine($"[quest-bundles] account {id} (login platform {platform}) unityAssetTarget={tt} - {(tt == QuestTarget ? "serving Quest (Android) clothing bundles" : "serving PC bundles")}");
            }
            bool quest;
            if (target is int q) quest = q == QuestTarget;
            else if (account is ulong a && _targetBySession.TryGetValue((a, platform), out var r)) quest = r == QuestTarget;
            else quest = platform == OculusPlatform;   // nothing sent yet this device: the login platform decides
            if (!quest)
            {
                await _next(ctx);
                return;
            }

            var original = ctx.Response.Body;
            using var buffer = new MemoryStream();
            ctx.Response.Body = buffer;
            try
            {
                await _next(ctx);
            }
            finally
            {
                ctx.Response.Body = original;
            }
            buffer.Position = 0;
            string ct = ctx.Response.ContentType ?? "";
            if (ct.Contains("json", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                string body = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();
                string rewritten;
                try { rewritten = RewriteJson(body); }
                catch { rewritten = BundleName.Replace(body, "\"$1\":\"quest/$2\""); }
                byte[] bytes = Encoding.UTF8.GetBytes(rewritten);
                ctx.Response.ContentLength = bytes.Length;
                await original.WriteAsync(bytes);
            }
            else
            {
                await buffer.CopyToAsync(original);
            }
        }
    }
}
