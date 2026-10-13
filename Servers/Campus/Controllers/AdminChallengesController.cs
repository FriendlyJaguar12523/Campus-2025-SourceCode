using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Admin panel > Challenges: the daily challenge set (api/server/DailyChallenges), its reward,
    /// today's completion numbers, and per-player progress (look up / reset). Developer-only, like
    /// every /admin/api route.
    /// </summary>
    public class AdminChallengesController : Controller
    {
        private ContentResult J(object o, int status = 200) => new ContentResult
        {
            Content = Newtonsoft.Json.JsonConvert.SerializeObject(o),
            ContentType = "application/json",
            StatusCode = status
        };

        private static object Describe(DailyObjective d) => new
        {
            type = d.Type,
            score = d.Score,
            name = DailyChallenges.Types.TryGetValue(d.Type, out var n) ? n : $"Type {d.Type}",
            tutorialOnly = DailyChallenges.TutorialOnly.Contains(d.Type)
        };

        [HttpGet("/admin/api/challenges")]
        public ContentResult Get()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var sets = DailyChallenges.Sets;
            bool sameEveryDay = sets.All(s => s.Select(d => (d.Type, d.Score)).SequenceEqual(sets[0].Select(d => (d.Type, d.Score))));

            // Today's numbers, from every player's stored progress (UTC day).
            int started = 0, finished = 0, objectivesDone = 0;
            try
            {
                foreach (var p in PlayerDB.PlayerDBFile.GetCollection<player_data>("players").FindAll())
                {
                    var ex = p.player_Extra;
                    if (ex?.ObjectiveGroups == null) continue;
                    var today = ex.ObjectiveGroups.Where(g => !DailyChallenges.IsStale(g.ClearedAt)).Select(g => g.Group).ToHashSet();
                    if (today.Count == 0) continue;
                    int done = ex.Objectives?.Count(o => today.Contains(o.Group) && o.IsCompleted) ?? 0;
                    objectivesDone += done;
                    if (done > 0) started++;
                    if (ex.ObjectiveGroups.Any(g => today.Contains(g.Group) && g.IsCompleted)) finished++;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[admin-challenges] stats scan failed: {ex.Message}"); }

            var dayNames = new[] { "Day 1", "Day 2", "Day 3", "Day 4", "Day 5", "Day 6", "Day 7" };
            return J(new
            {
                success = true,
                source = DailyChallenges.Source,
                rewardEnabled = DailyChallenges.RewardEnabled,
                xpPerObjective = ConfigCampus.ObjectiveCompletionXp,
                sameEveryDay,
                sets = sets.Select((s, i) => new { day = dayNames[i], objectives = s.Select(Describe) }),
                resetsAtUtc = DailyChallenges.PeriodStartUtc.AddDays(1).ToString("o"),
                today = new { started, finished, objectivesDone },
                types = DailyChallenges.Types.Select(kv => new { type = kv.Key, name = kv.Value, tutorialOnly = DailyChallenges.TutorialOnly.Contains(kv.Key) }),
                weekly = new
                {
                    inThisBuild = false,
                    note = "This client build has no weekly challenge screen and never requests one (no /api/challenge route in the game)."
                }
            });
        }

        /// <summary>
        /// Body: { rewardEnabled, xpPerObjective, sets: [[{type,score},...], ...] } - one set (used
        /// every day) or up to seven (one per day). 1-6 objectives per set.
        /// </summary>
        [HttpPost("/admin/api/challenges/save")]
        public async Task<ContentResult> Save()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            JObject body;
            try { using var r = new StreamReader(Request.Body); body = JObject.Parse(await r.ReadToEndAsync()); }
            catch { return J(new { success = false, message = "Unreadable request." }); }

            bool rewardEnabled = body.Value<bool?>("rewardEnabled") ?? true;
            int xp = Math.Clamp(body.Value<int?>("xpPerObjective") ?? ConfigCampus.ObjectiveCompletionXp, 0, 100000);
            if (body["sets"] is not JArray arr || arr.Count == 0 || arr.Count > 7)
                return J(new { success = false, message = "Give between 1 and 7 sets." });

            var sets = new List<List<DailyObjective>>();
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JArray set || set.Count == 0 || set.Count > 6)
                    return J(new { success = false, message = $"Set {i + 1} needs 1 to 6 challenges." });
                var list = new List<DailyObjective>();
                foreach (var o in set.OfType<JObject>())
                {
                    int type = o.Value<int?>("type") ?? 0, score = o.Value<int?>("score") ?? 1;
                    if (!DailyChallenges.Types.ContainsKey(type))
                        return J(new { success = false, message = $"Set {i + 1}: unknown challenge type {type}." });
                    if (score < 1 || score > 1000)
                        return J(new { success = false, message = $"Set {i + 1}: the target must be 1 to 1000." });
                    list.Add(new DailyObjective { Type = type, Score = score });
                }
                sets.Add(list);
            }
            // Every day must have the SAME number of challenges: the client reports progress as
            // group 0 whatever the day, so "all done" is counted against one size for every day.
            if (sets.Select(x => x.Count).Distinct().Count() > 1)
                return J(new { success = false, message = "Every day needs the same number of challenges." });

            DailyChallenges.Save(sets, rewardEnabled, xp);
            Console.WriteLine($"[admin-challenges] {account.Id} saved {sets.Count} daily set(s), reward {(rewardEnabled ? "on" : "off")}, {xp} XP each: " +
                              string.Join(" | ", sets.Select(s => string.Join(",", s.Select(d => $"{d.Type}x{d.Score}")))));
            return J(new { success = true, message = "Saved. Players get the new challenges on their next login." });
        }

        [HttpPost("/admin/api/challenges/reset_defaults")]
        public ContentResult ResetDefaults()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            DailyChallenges.ResetToDefaults();
            Console.WriteLine($"[admin-challenges] {account.Id} reset the daily challenges to the defaults");
            return J(new { success = true, message = "Back to the defaults (Rec Center, custom room, finish an activity)." });
        }

        private static ulong ResolvePlayer(string? p)
        {
            p = (p ?? "").Trim().TrimStart('@');
            if (p.Length == 0) return 0;
            return ulong.TryParse(p, out var id) ? id : PlayerDB.GetPlayerIdByName(p);
        }

        /// <summary>One player's progress today: which challenges are done and whether the group is complete.</summary>
        [HttpGet("/admin/api/challenges/player")]
        public ContentResult PlayerProgress()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            ulong id = ResolvePlayer(Request.Query["player"]);
            if (id == 0) return J(new { success = false, message = "No such player." });

            var (objectives, groups) = PlayerDB.GetObjectivesProgress(id);
            var set = DailyChallenges.Sets[0];
            var g = groups.FirstOrDefault(x => x.Group == 0);
            return J(new
            {
                success = true,
                id,
                name = PlayerDB.GetPlayerNameById(id),
                completedToday = g?.IsCompleted ?? false,
                objectives = set.Select((d, i) =>
                {
                    var o = objectives.FirstOrDefault(x => x.Group == 0 && x.Index == i);
                    return new { name = DailyChallenges.Types.TryGetValue(d.Type, out var n) ? n : $"Type {d.Type}", target = d.Score, progress = o?.Progress ?? 0, done = o?.IsCompleted ?? false };
                })
            });
        }

        // ================= Limited-time events (ProgressionEvents) =================

        private static string Status(ProgressionEvents.EventDef e)
        {
            var now = DateTime.UtcNow;
            if (!e.Enabled) return "off";
            if (now < e.StartTime) return "scheduled";
            if (now <= e.EndTime) return e.DevelopersOnly ? "live (developers only)" : "live";
            if (now <= (e.CollectionEndTime ?? e.EndTime)) return "collecting only";
            return "ended";
        }

        [HttpGet("/admin/api/progression_events")]
        public ContentResult EventsList()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var (minutes, xp, cap) = ProgressionEvents.Rule();
            return J(new
            {
                success = true,
                rule = new { minutes, xp, cap },
                events = ProgressionEvents.All().Select(e => new { def = e, status = Status(e), participants = ProgressionEvents.Participants(e.Id) })
            });
        }

        /// <summary>Create or replace one event (matched by Id). Body: the EventDef JSON.</summary>
        [HttpPost("/admin/api/progression_events/save")]
        public async Task<ContentResult> EventsSave()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            ProgressionEvents.EventDef? def;
            try { using var r = new StreamReader(Request.Body); def = Newtonsoft.Json.JsonConvert.DeserializeObject<ProgressionEvents.EventDef>(await r.ReadToEndAsync()); }
            catch (Exception ex) { return J(new { success = false, message = "Unreadable event: " + ex.Message }); }
            if (def == null) return J(new { success = false, message = "Unreadable event." });

            def.Name = (def.Name ?? "").Trim();
            if (def.Name.Length == 0 || def.Name.Length > 60) return J(new { success = false, message = "Give the event a name (up to 60 characters)." });
            if (!Guid.TryParse(def.Id, out _)) def.Id = Guid.NewGuid().ToString();
            def.StartTime = DateTime.SpecifyKind(def.StartTime, DateTimeKind.Utc);
            def.EndTime = DateTime.SpecifyKind(def.EndTime, DateTimeKind.Utc);
            if (def.CollectionEndTime is { } ce) def.CollectionEndTime = DateTime.SpecifyKind(ce, DateTimeKind.Utc);
            if (def.EndTime <= def.StartTime) return J(new { success = false, message = "The end must be after the start." });
            if (def.CollectionEndTime is { } c && c < def.EndTime) return J(new { success = false, message = "Collection must end on or after the event end." });
            if (def.Steps.Count == 0 || def.Steps.Count > 60) return J(new { success = false, message = "The track needs 1 to 60 steps." });
            var items = ProgressionEvents.StoreItems().ToDictionary(i => i.GiftDropId);
            for (int i = 0; i < def.Steps.Count; i++)
            {
                var s = def.Steps[i];
                if (s.Xp < 1 || s.Xp > 100000) return J(new { success = false, message = $"Step {i + 1}: XP must be 1 to 100000." });
                foreach (var rw in new[] { s.Free, s.Premium })
                {
                    if (rw == null) continue;
                    if (!Guid.TryParse(rw.Id, out _)) rw.Id = Guid.NewGuid().ToString();
                    if (rw.Kind == "tokens" && (rw.Tokens < 1 || rw.Tokens > 100000)) return J(new { success = false, message = $"Step {i + 1}: tokens must be 1 to 100000." });
                    if (rw.Kind == "item")
                    {
                        if (!items.TryGetValue(rw.GiftDropId, out var it)) return J(new { success = false, message = $"Step {i + 1}: pick an item from the store." });
                        rw.AvatarItemDesc = it.AvatarItemDesc; rw.AvatarItemType = it.AvatarItemType;
                        rw.FriendlyName = it.FriendlyName; rw.Rarity = it.Rarity;
                        if (string.IsNullOrWhiteSpace(rw.ImageName)) rw.ImageName = it.ThumbnailImageName;
                    }
                    if (rw.Kind != "tokens" && rw.Kind != "item") rw.Kind = "none";
                }
            }
            var all = ProgressionEvents.All().ToList();
            int idx = all.FindIndex(e => e.Id == def.Id);
            if (idx >= 0) all[idx] = def; else all.Add(def);
            ProgressionEvents.SaveAll(all);
            Console.WriteLine($"[admin-events] {account.Id} saved event '{def.Name}' ({def.Id}): {Status(def)}, {def.Steps.Count} steps");
            return J(new { success = true, id = def.Id, message = $"Saved '{def.Name}' ({Status(def)})." });
        }

        [HttpPost("/admin/api/progression_events/delete")]
        public ContentResult EventsDelete()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string id = Request.HasFormContentType ? Request.Form["id"].ToString() : Request.Query["id"].ToString();
            var all = ProgressionEvents.All().ToList();
            int n = all.RemoveAll(e => e.Id == id);
            ProgressionEvents.SaveAll(all);
            Console.WriteLine($"[admin-events] {account.Id} deleted event {id} ({n})");
            return J(new { success = n > 0, message = n > 0 ? "Deleted." : "No such event." });
        }

        /// <summary>Store items to pick a reward from (name search).</summary>
        [HttpGet("/admin/api/progression_events/items")]
        public ContentResult EventsItems()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            string q = (Request.Query["q"].ToString() ?? "").Trim();
            var hits = ProgressionEvents.StoreItems()
                .Where(i => q.Length == 0 || i.FriendlyName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.FriendlyName).Take(40)
                .Select(i => new { i.GiftDropId, i.FriendlyName, i.Rarity, i.ThumbnailImageName });
            return J(new { success = true, items = hits });
        }

        /// <summary>A player's record on an event. ?id=event&amp;player=</summary>
        [HttpGet("/admin/api/progression_events/player")]
        public ContentResult EventsPlayer()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var e = ProgressionEvents.Find(Request.Query["id"]);
            ulong pid = ResolvePlayer(Request.Query["player"]);
            if (e == null || pid == 0) return J(new { success = false, message = e == null ? "No such event." : "No such player." });
            var r = ProgressionEvents.GetRecord(e.Id, pid);
            return J(new { success = true, name = PlayerDB.GetPlayerNameById(pid), id = pid, xp = r.Xp, xpToday = r.XpToday, minutesToday = (int)(r.SecondsToday / 60), collected = r.Collected.Count });
        }

        /// <summary>Testing aids: give a player event XP, or wipe their record. Form: id, player, xp / reset=1.</summary>
        [HttpPost("/admin/api/progression_events/player")]
        public ContentResult EventsPlayerEdit()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var e = ProgressionEvents.Find(Request.Form["id"]);
            ulong pid = ResolvePlayer(Request.Form["player"]);
            if (e == null || pid == 0) return J(new { success = false, message = e == null ? "No such event." : "No such player." });
            if (Request.Form["reset"] == "1")
            {
                ProgressionEvents.ResetPlayer(e.Id, pid);
                Console.WriteLine($"[admin-events] {account.Id} reset {pid}'s record on '{e.Name}'");
                return J(new { success = true, message = "Record reset." });
            }
            if (!int.TryParse(Request.Form["xp"], out int xp) || xp == 0 || Math.Abs(xp) > 100000) return J(new { success = false, message = "XP must be a non-zero number." });
            var r = ProgressionEvents.GetRecord(e.Id, pid);
            r.Xp = Math.Max(0, r.Xp + xp);
            ProgressionEvents.SaveRecord(r);
            ProgressionEvents.PushRecord(e, r);
            Console.WriteLine($"[admin-events] {account.Id} gave {pid} {xp} XP on '{e.Name}' -> {r.Xp}");
            return J(new { success = true, message = $"@{PlayerDB.GetPlayerNameById(pid)} now has {r.Xp} XP." });
        }

        /// <summary>Wipe a player's daily progress so today's challenges (and their reward) can be done again.</summary>
        [HttpPost("/admin/api/challenges/reset_player")]
        public ContentResult ResetPlayer()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            ulong id = ResolvePlayer(Request.HasFormContentType ? Request.Form["player"].ToString() : Request.Query["player"].ToString());
            if (id == 0) return J(new { success = false, message = "No such player." });

            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            var rec = col.FindOne(x => x.playerid == id);
            if (rec?.player_Extra == null) return J(new { success = false, message = "No such player." });
            rec.player_Extra.Objectives = new List<ObjectiveProgress>();
            rec.player_Extra.ObjectiveGroups = new List<ObjectiveGroupProgress>();
            col.Update(rec);
            Console.WriteLine($"[admin-challenges] {account.Id} reset {id}'s daily challenge progress");
            return J(new { success = true, message = $"Reset @{PlayerDB.GetPlayerNameById(id)}'s challenges. They show fresh on their next login." });
        }
    }
}
