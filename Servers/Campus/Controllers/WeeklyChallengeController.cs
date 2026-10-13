using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// The weekly challenges (api/server/WeeklyChallenge): the website's /challenges data, and the
    /// admin panel's Challenges tab section for picking each week's reward.
    /// </summary>
    public class WeeklyChallengeController : ControllerBase
    {
        private const string SiteCookie = "campus_site";
        private static ContentResult J(object o) => new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = 200 };

        private player_info? SiteUser()
        {
            string token = Request.Cookies[SiteCookie] ?? "";
            if (string.IsNullOrEmpty(token)) return null;
            string data = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_")) return null;
            try { return JsonConvert.DeserializeObject<player_info>(data); } catch { return null; }
        }

        private static object? ItemDto(WeeklyChallenge.RewardItem? i) => i == null ? null : new
        {
            giftDropId = i.GiftDropId, name = i.FriendlyName, kind = i.Kind, rarity = i.Rarity,
            image = string.IsNullOrEmpty(i.ThumbnailImageName) ? "" : "/img/" + i.ThumbnailImageName
        };

        /// <summary>
        /// GET /api/site/weekly - this week's challenges and reward for the website. Signed in, each
        /// challenge carries the player's progress, and whether their reward has been sent.
        /// </summary>
        [HttpGet("/api/site/weekly")]
        public ContentResult SiteWeekly()
        {
            var now = DateTime.UtcNow;
            int map = WeeklyChallenge.CurrentMapId(now);
            var week = WeeklyChallenge.ChallengesFor(map);
            var me = SiteUser();
            var p = me != null ? WeeklyChallenge.ProgressFor(me.Id, map) : null;
            return J(new
            {
                ok = true,
                week = map,
                startsAt = WeeklyChallenge.StartOf(map).ToString("o"),
                endsAt = WeeklyChallenge.EndOf(map).ToString("o"),
                required = WeeklyChallenge.RequiredForGift,
                signedIn = me != null,
                challenges = week.Select(c => new
                {
                    id = c.Id,
                    text = WeeklyChallenge.TextOf(c.Def),
                    target = c.Def.Target,
                    roomId = c.Def.LinkRoom,
                    roomName = c.Def.LinkRoom > 0 ? RoomDB.GetRoom((ulong)c.Def.LinkRoom)?.Name ?? "" : "",
                    progress = p != null ? Math.Min(WeeklyChallenge.CountOf(p, c.Id), c.Def.Target) : 0,
                    group = c.Def.Quest ? "quest" : c.Def.Group,   // the page's activity icon
                }),
                completed = p != null ? WeeklyChallenge.Completed(p, week) : 0,
                rewardSent = p?.RewardSentAt != null,
                rewardSentName = p?.RewardName ?? "",
                reward = ItemDto(WeeklyChallenge.RewardFor(map)),
            });
        }

        // ---------------- admin ----------------

        private static object WeekDto(int map)
        {
            var week = WeeklyChallenge.ChallengesFor(map);
            var all = WeeklyChallenge.AllFor(map);
            return new
            {
                map,
                startsAt = WeeklyChallenge.StartOf(map).ToString("o"),
                endsAt = WeeklyChallenge.EndOf(map).ToString("o"),
                challenges = week.Select(c => new
                {
                    id = c.Id, text = WeeklyChallenge.TextOf(c.Def), target = c.Def.Target,
                    done = all.Count(p => WeeklyChallenge.CountOf(p, c.Id) >= c.Def.Target)
                }),
                reward = ItemDto(WeeklyChallenge.RewardFor(map)),
                chosen = WeeklyChallenge.IsChosen(map),
                started = all.Count,
                rewarded = all.Count(p => p.RewardSentAt != null),
            };
        }

        /// <summary>This week and next: challenges, reward (chosen or random) and numbers.</summary>
        [HttpGet("/admin/api/weekly")]
        public ContentResult AdminWeekly()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            int map = WeeklyChallenge.CurrentMapId(DateTime.UtcNow);
            return J(new { success = true, required = WeeklyChallenge.RequiredForGift, current = WeekDto(map), next = WeekDto(map + 1) });
        }

        /// <summary>Store skins and clothing to pick a reward from. ?q=name&amp;kind=skin|clothing</summary>
        [HttpGet("/admin/api/weekly/items")]
        public ContentResult AdminWeeklyItems()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string q = Request.Query["q"].ToString().Trim(), kind = Request.Query["kind"].ToString().Trim().ToLowerInvariant();
            var hits = WeeklyChallenge.RewardItems()
                .Where(i => (kind.Length == 0 || i.Kind == kind) && (q.Length == 0 || i.FriendlyName.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(i => i.FriendlyName).Take(60).Select(ItemDto);
            return J(new { success = true, items = hits });
        }

        /// <summary>Choose a week's reward. Form: map, giftDropId (0 = back to a random store skin).</summary>
        [HttpPost("/admin/api/weekly/reward")]
        public ContentResult AdminWeeklyReward()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            int current = WeeklyChallenge.CurrentMapId(DateTime.UtcNow);
            if (!int.TryParse(Request.Form["map"], out int map) || map < current || map > current + 1)
                return J(new { success = false, message = "Pick this week or next week." });
            int.TryParse(Request.Form["giftDropId"], out int id);
            if (id > 0 && WeeklyChallenge.FindItem(id) == null) return J(new { success = false, message = "That isn't a store skin or clothing item." });
            WeeklyChallenge.SetChoice(map, id);
            var r = WeeklyChallenge.RewardFor(map);
            Console.WriteLine($"[admin-weekly] {account!.Username} ({account.Id}) set week {map}'s reward to " + (id > 0 ? $"'{r?.FriendlyName}'" : "random"));
            return J(new { success = true, message = id > 0 ? $"Reward set to {r?.FriendlyName}." : $"Back to a random skin ({r?.FriendlyName})." });
        }

        /// <summary>A player's progress this week. ?player=@name or id</summary>
        [HttpGet("/admin/api/weekly/player")]
        public ContentResult AdminWeeklyPlayer()
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            ulong pid = ResolvePlayer(Request.Query["player"]);
            if (pid == 0) return J(new { success = false, message = "No such player." });
            int map = WeeklyChallenge.CurrentMapId(DateTime.UtcNow);
            var week = WeeklyChallenge.ChallengesFor(map);
            var p = WeeklyChallenge.ProgressFor(pid, map);
            return J(new
            {
                success = true, id = pid, name = PlayerDB.GetPlayerNameById(pid),
                challenges = week.Select(c => new { text = WeeklyChallenge.TextOf(c.Def), progress = Math.Min(WeeklyChallenge.CountOf(p, c.Id), c.Def.Target), target = c.Def.Target }),
                rewardSent = p.RewardSentAt, rewardName = p.RewardName
            });
        }

        private static ulong ResolvePlayer(string? input)
        {
            string s = (input ?? "").Trim().TrimStart('@');
            if (s.Length == 0) return 0;
            if (ulong.TryParse(s, out var id)) return PlayerDB.GetAccountById(id) != null ? id : 0;
            return PlayerDB.GetAccountIdByUsernameCI(s);
        }
    }
}
