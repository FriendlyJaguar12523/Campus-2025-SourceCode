using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Personal quick chat, edited on the website. The phrases live in SitePrefsDB and are swapped into THIS player's copy of the game
    /// configs (see QuickChat.ApplyToConfigs). Auth is the WEBSITE session (campus_site cookie), never a game token - the same
    /// separation SiteChatController keeps.
    /// </summary>
    [Route("api/site/quickchat")]
    public class SiteQuickChatController : ControllerBase
    {
        private const string SiteCookie = "campus_site";

        private player_info? CurrentUser()
        {
            string token = Request.Cookies[SiteCookie] ?? "";
            if (string.IsNullOrEmpty(token)) return null;
            string playerData = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(playerData) || playerData.StartsWith("error_")) return null;
            try { return JsonConvert.DeserializeObject<player_info>(playerData); } catch { return null; }
        }

        private static ContentResult J(object o, int status = 200) =>
            new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };

        public class QuickChatBody
        {
            public string? key { get; set; }
            public List<string>? phrases { get; set; }
        }

        private static List<string> Padded(IReadOnlyList<string>? mine, int slots)
        {
            var list = new List<string>();
            for (int i = 0; i < slots; i++) list.Add(mine != null && i < mine.Count ? mine[i] ?? "" : "");
            return list;
        }

        // GET /api/site/quickchat - the player's four sets: the stock phrases and what they have set (empty = stock)
        [HttpGet("")]
        public ContentResult Get()
        {
            var me = CurrentUser();
            if (me == null) return J(new { ok = false, error = "not signed in" }, 401);
            var saved = SitePrefsDB.Get(me.Id).QuickChat ?? new();
            return J(new
            {
                ok = true,
                maxLength = QuickChat.MaxPhraseLength,
                sets = QuickChat.Sets.Select(s => new
                {
                    key = s.Key,
                    label = s.Label,
                    defaults = s.Defaults,
                    mine = Padded(saved.TryGetValue(s.Key, out var m) ? m : null, s.Defaults.Length)
                })
            });
        }

        // POST /api/site/quickchat {key, phrases:[5]} - save one set. Every phrase is checked; one bad phrase refuses the whole save
        // and says which and why, so nothing is half-saved or quietly changed.
        [HttpPost("")]
        public ContentResult Save([FromBody] QuickChatBody? body)
        {
            var me = CurrentUser();
            if (me == null) return J(new { ok = false, error = "not signed in" }, 401);
            var set = QuickChat.Find(body?.key);
            if (set == null || body?.phrases == null) return J(new { ok = false, error = "unknown set" }, 400);

            var cleaned = new List<string>();
            for (int i = 0; i < set.Defaults.Length; i++)
            {
                string? raw = i < body.phrases.Count ? body.phrases[i] : "";
                string? c = QuickChat.Clean(raw, out string? problem);
                if (c == null) return J(new { ok = false, error = $"Phrase {i + 1}: {problem}", slot = i }, 400);
                cleaned.Add(c);
            }
            SitePrefsDB.SetQuickChat(me.Id, set.Key, cleaned);
            Console.WriteLine($"[quickchat] {me.Id} saved {set.Key}: {cleaned.Count(x => x.Length > 0)} custom phrase(s)");
            return J(new { ok = true, key = set.Key, mine = Padded(cleaned, set.Defaults.Length), note = "It shows the next time you start the game." });
        }

        // POST /api/site/quickchat/reset {key} - back to the stock phrases
        [HttpPost("reset")]
        public ContentResult Reset([FromBody] QuickChatBody? body)
        {
            var me = CurrentUser();
            if (me == null) return J(new { ok = false, error = "not signed in" }, 401);
            var set = QuickChat.Find(body?.key);
            if (set == null) return J(new { ok = false, error = "unknown set" }, 400);
            SitePrefsDB.SetQuickChat(me.Id, set.Key, new List<string>());
            return J(new { ok = true, key = set.Key, mine = Padded(null, set.Defaults.Length) });
        }
    }
}
