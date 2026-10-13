using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.dbs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Two player toggles that were acked and forgotten, so they flipped back:
    ///   - "avoid juniors" (/Matchmaking/player/avoidjuniors) - stored in the player's settings
    ///     under avoidJuniors, like RecFlare. Like RecFlare, matchmaking doesn't act on it yet.
    ///   - "show my recent rooms" (/Accounts/accountprivacysettings/recenthistoryvisibility) -
    ///     stored on the account; the profile's visited-rooms list already honours it.
    /// </summary>
    public class PlayerPrefsController : ControllerBase
    {
        private const string AvoidJuniorsKey = "avoidJuniors";
        private static ContentResult Json(object o) => new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = 200 };

        /// <summary>"true"/"false", "1"/"0", "yes"/"no", "on"/"off"; null when it's none of them.</summary>
        private static bool? ParseBool(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => null
        };

        private static string NormKey(string k) => new string(k.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>The posted fields - form, query or a JSON object body - as one map (keys normalised).</summary>
        private async Task<Dictionary<string, string>> Fields()
        {
            var d = new Dictionary<string, string>();
            foreach (var q in Request.Query) d[NormKey(q.Key)] = q.Value.ToString();
            if (Request.HasFormContentType)
                foreach (var f in Request.Form) d[NormKey(f.Key)] = f.Value.ToString();
            else
            {
                try
                {
                    using var r = new StreamReader(Request.Body);
                    string raw = (await r.ReadToEndAsync()).Trim();
                    if (raw.StartsWith("{"))
                        foreach (var p in JObject.Parse(raw).Properties()) d[NormKey(p.Name)] = p.Value.ToString();
                    else if (raw.Length > 0) d[""] = raw.Trim('"');   // a bare value
                }
                catch { }
            }
            return d;
        }

        private static bool ReadAvoidJuniors(ulong playerId)
        {
            // The client may write it itself through player settings, under any spelling.
            var s = SettingsDB.LoadSettings(playerId).FirstOrDefault(x => NormKey(x.Key ?? "") == NormKey(AvoidJuniorsKey));
            return ParseBool(s?.Value) ?? false;
        }

        [HttpGet("/Matchmaking/player/avoidjuniors")]
        public ContentResult GetAvoidJuniors()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Json(ReadAvoidJuniors(account.Id));
        }

        // Answers the RESULTING value (a bare boolean), so the toggle reads back what was stored.
        // No readable value leaves it as it was.
        [HttpPut("/Matchmaking/player/avoidjuniors")]
        [HttpPost("/Matchmaking/player/avoidjuniors")]
        public async Task<ContentResult> SetAvoidJuniors()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = await Fields();
            bool? v = ParseBool(f.GetValueOrDefault("avoidjuniors") ?? f.GetValueOrDefault("value") ?? f.GetValueOrDefault(""));
            if (v != null)
            {
                SettingsDB.SetPlayerSetting(AvoidJuniorsKey, v.Value ? "True" : "False", account.Id);
                Console.WriteLine($"[prefs] {account.Id} avoid juniors = {v.Value}");
            }
            return Json(ReadAvoidJuniors(account.Id));
        }

        private static int _historyLogged;

        [HttpPut("/Accounts/accountprivacysettings/recenthistoryvisibility")]
        [HttpPost("/Accounts/accountprivacysettings/recenthistoryvisibility")]
        public async Task<ContentResult> SetRecentHistoryVisibility()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = await Fields();
            // The field name was never captured: the first few calls log what came in.
            if (Interlocked.Increment(ref _historyLogged) <= 5)
                Console.WriteLine($"[prefs] recenthistoryvisibility from {account.Id}: " +
                                  string.Join(", ", f.Select(kv => $"{kv.Key}={kv.Value}")));
            bool? v = null;
            foreach (var key in new[] { "isrecenthistoryvisible", "recenthistoryvisible", "isvisible", "visible", "recenthistoryvisibility", "visibility", "value", "" })
                if ((v = ParseBool(f.GetValueOrDefault(key))) != null) break;
            v ??= f.Count == 1 ? ParseBool(f.Values.First()) : null;
            if (v != null)
            {
                PlayerDB.SetRecentRoomHistoryVisible(account.Id, v.Value);
                Console.WriteLine($"[prefs] {account.Id} recent room history visible = {v.Value}");
            }
            bool now = PlayerDB.GetFullAccount(account.Id)?.player_Extra?.IsRecentRoomHistoryVisible ?? true;
            return Json(new { error = "", success = true, value = (object?)null, accountId = account.Id, isRecentHistoryVisible = now });
        }
    }
}
