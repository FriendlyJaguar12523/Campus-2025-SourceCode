using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Verification of a Meta (Oculus) login's platform_auth nonce against Meta's graph API.
    /// Port of the reference recflare server's apps/auth/src/meta-nonce.ts.
    ///
    /// A Meta login posts
    ///     platform_auth = {"Nonce":"&lt;64 chars&gt;","AppId":"1295523863648168","Source":"logged in user"}
    ///     platform_id   = &lt;the Meta user id&gt;
    /// The nonce means nothing on its own; asking Meta "was this nonce issued to this user, for
    /// this app?" is what binds the two together. So the posted platform_id is an INPUT here, and a
    /// spoofed one fails validation instead of being trusted. Only an id that comes back verified
    /// is ever bound or linked to an account.
    ///
    /// Needs the app secret (ConfigCampus.MetaAppSecret): the access token is OC|appId|secret,
    /// which authenticates the APP - which is why the secret never leaves the server.
    /// </summary>
    public static class MetaNonce
    {
        private const string NonceValidateUrl = "https://graph.oculus.com/user_nonce_validate";
        private const int MaxAttempts = 3;
        private static readonly HashSet<int> TransientErrorCodes = new() { 1, 2 };
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

        public readonly record struct Result(bool Ok, string Reason, ulong UserId, string AppId);

        /// <summary>The {Nonce, AppId} a Meta platform_auth carries, or null when it isn't one.</summary>
        public static (string nonce, string appId)? Parse(string? platformAuth)
        {
            if (string.IsNullOrEmpty(platformAuth)) return null;
            JObject o;
            try { o = JObject.Parse(System.Net.WebUtility.UrlDecode(platformAuth)); }
            catch { try { o = JObject.Parse(platformAuth); } catch { return null; } }
            string? nonce = o["Nonce"]?.Type == JTokenType.String ? o["Nonce"]!.Value<string>() : null;
            string? appId = o["AppId"]?.Type == JTokenType.String ? o["AppId"]!.Value<string>() : null;
            if (string.IsNullOrEmpty(nonce)) return null;
            // AppId is interpolated into the access token below - keep a non-numeric string out of it.
            if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit)) return null;
            return (nonce, appId);
        }

        /// <summary>
        /// Verify platform_auth against the user id it is claimed for. Only succeeds for a nonce Meta
        /// itself confirms was issued to that user for that app. Reasons are for the server log; the
        /// client is only ever told the proof was rejected.
        /// </summary>
        public static async Task<Result> VerifyAsync(string? platformAuth, ulong userId, string appSecret)
        {
            if (string.IsNullOrEmpty(appSecret)) return new(false, "no app secret configured", 0, "");
            if (userId == 0) return new(false, "missing platform_id", 0, "");
            var parsed = Parse(platformAuth);
            if (parsed == null) return new(false, "malformed platform_auth payload", 0, "");
            var (nonce, appId) = parsed.Value;

            var form = new Dictionary<string, string>
            {
                ["nonce"] = nonce,
                ["user_id"] = userId.ToString(CultureInfo.InvariantCulture),
                ["access_token"] = $"OC|{appId}|{appSecret}",
            };

            string reason = "not attempted";
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var (ok, retryable, why) = await ValidateOnce(form);
                if (ok) return new(true, "", userId, appId);
                reason = why;
                if (!retryable || attempt == MaxAttempts) break;
                await Task.Delay(attempt * attempt * 250);
            }
            return new(false, reason, 0, appId);
        }

        private static async Task<(bool ok, bool retryable, string reason)> ValidateOnce(Dictionary<string, string> form)
        {
            HttpResponseMessage res;
            try
            {
                res = await Http.PostAsync(NonceValidateUrl, new FormUrlEncodedContent(form));
            }
            catch (Exception ex)
            {
                return (false, true, $"request failed: {ex.Message}");
            }

            JObject body;
            try { body = JObject.Parse(await res.Content.ReadAsStringAsync()); }
            catch
            {
                // A non-JSON body is Meta's edge (a 5xx page, a rate-limit page), not a verdict.
                return (false, true, $"HTTP {(int)res.StatusCode} with a non-JSON body");
            }

            if (body["error"] is JObject err)
            {
                int? code = err["code"]?.Type == JTokenType.Integer ? err["code"]!.Value<int>() : null;
                bool transient = err["is_transient"]?.Type == JTokenType.Boolean && err["is_transient"]!.Value<bool>();
                bool retry = transient || (code.HasValue && TransientErrorCodes.Contains(code.Value));
                return (false, retry, $"graph error {(code?.ToString() ?? "?")}: {err["message"]?.ToString() ?? "no message"}");
            }
            if (body["is_valid"]?.Type != JTokenType.Boolean || !body["is_valid"]!.Value<bool>())
                return (false, false, "nonce rejected");
            return (true, false, "");
        }
    }
}
