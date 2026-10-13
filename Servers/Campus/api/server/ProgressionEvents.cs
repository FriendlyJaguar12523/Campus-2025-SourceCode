using LiteDB;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Limited-time progression events - the watch's "Limited Time Challenge" (Summer Party and
    /// friends): a reward track with a free row and an RR+ row, filled by XP earned from time spent
    /// in public rooms, capped per day. The client has the whole feature (RRUI.Data.
    /// ProgressionEventsModel); Rec Room's servers supplied the events, so Campus defines them in
    /// the admin panel (Challenges > Limited Time Events), stored in Data/APIS/progression-events.json.
    ///
    /// The wire shapes were read out of the running 2025 client (the DTO formatters' name tables):
    /// RecNet.ProgressionEventDTO / ProgressionEventRewardDTO / ProgressionEventRecordDTO. Which DTO
    /// owns which name is only partly resolved, so each object is sent as a SUPERSET of the
    /// candidate names - the client's decoder drops unknown keys silently. Every other call the
    /// client makes under /api/progressionEvents is logged (ProgressionEventsController) so the
    /// exact routes name themselves on the first test. An event can be set Developers-only so a
    /// wrong guess only ever reaches staff.
    ///
    /// XP rule (the client's own gameconfig: ProgressionEvents.* keys in Data/APIS/Gameconfigs.json):
    /// every XpMinutes minutes in a public room pays XpPerConversion XP, at most MaxDailyXp per UTC day.
    /// </summary>
    public static class ProgressionEvents
    {
        // ---------------- definitions ----------------

        public sealed class Reward
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            /// <summary>"tokens", "item" or "none".</summary>
            public string Kind { get; set; } = "tokens";
            public long Tokens { get; set; } = 50;
            public int GiftDropId { get; set; }            // store gift drop, for "item"
            public string? AvatarItemDesc { get; set; }
            public int AvatarItemType { get; set; }
            public string? FriendlyName { get; set; }
            public int Rarity { get; set; }
            public string? ImageName { get; set; }
        }

        /// <summary>One column of the track: the XP to reach it, the free reward and the RR+ reward.</summary>
        public sealed class Step
        {
            public int Xp { get; set; } = 100;
            public Reward? Free { get; set; }
            public Reward? Premium { get; set; }
        }

        public sealed class EventDef
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            /// <summary>
            /// The id the GAME uses. The client reads GET /api/progressionEvents/active as a NUMBER
            /// (Player.log: "expected:'Number Token', actual:'['" for both our [] and our event
            /// list), and its reward model is Set(long, int) - so events (and rewards) are
            /// addressed by small integers, starting at 1. Assigned on save, never reused.
            /// </summary>
            public long NumericId { get; set; }
            public string Name { get; set; } = "Summer Party!";
            public DateTime StartTime { get; set; } = DateTime.UtcNow.Date;
            public DateTime EndTime { get; set; } = DateTime.UtcNow.Date.AddDays(14);
            /// <summary>Rewards can still be collected (no more XP) until this; null = EndTime.</summary>
            public DateTime? CollectionEndTime { get; set; }
            public bool Enabled { get; set; }
            /// <summary>Only players with the developer flag see it - for testing a new event.</summary>
            public bool DevelopersOnly { get; set; } = true;
            /// <summary>RR+ row collectable by everyone (Campus has no RR+ purchase).</summary>
            public bool PremiumForEveryone { get; set; } = true;
            public string? ChallengesIconImageName { get; set; }
            public string? EventInfoImageName { get; set; }
            public List<Step> Steps { get; set; } = new();
        }

        private static readonly object _lock = new();
        private static readonly string File1 = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "progression-events.json");
        private static List<EventDef>? _defs;

        public static List<EventDef> All()
        {
            lock (_lock)
            {
                if (_defs != null) return _defs;
                try
                {
                    _defs = File.Exists(File1)
                        ? Newtonsoft.Json.JsonConvert.DeserializeObject<List<EventDef>>(File.ReadAllText(File1)) ?? new()
                        : new();
                }
                catch (Exception ex) { Console.WriteLine($"[progression-events] could not read progression-events.json: {ex.Message}"); _defs = new(); }
                AssignNumericIds(_defs);
                return _defs;
            }
        }

        /// <summary>Give every event without one a numeric id (max + 1, starting at 1).</summary>
        private static void AssignNumericIds(List<EventDef> defs)
        {
            long next = Math.Max(defs.Count == 0 ? 0 : defs.Max(d => d.NumericId), _highestNumericId) + 1;
            foreach (var d in defs.Where(d => d.NumericId <= 0)) d.NumericId = next++;
            _highestNumericId = Math.Max(_highestNumericId, next - 1);
        }
        private static long _highestNumericId;

        public static EventDef? FindNumeric(long id) => All().FirstOrDefault(e => e.NumericId == id);

        /// <summary>A reward's game-facing id: event id * 1000 + its position in the track (free, RR+ per step).</summary>
        public static long RewardNumericId(EventDef e, int stepIndex, bool premium) => e.NumericId * 1000 + stepIndex * 2 + (premium ? 1 : 0);

        public static void SaveAll(List<EventDef> defs)
        {
            lock (_lock)
            {
                AssignNumericIds(defs);
                Directory.CreateDirectory(Path.GetDirectoryName(File1)!);
                File.WriteAllText(File1, Newtonsoft.Json.JsonConvert.SerializeObject(defs, Newtonsoft.Json.Formatting.Indented));
                _defs = defs;
            }
        }

        public static EventDef? Find(string? id) =>
            id == null ? null : All().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

        private static DateTime CollectEnd(EventDef e) => e.CollectionEndTime ?? e.EndTime;

        /// <summary>Events this player is shown right now (running, or in their collection window).</summary>
        public static List<EventDef> VisibleTo(ulong accountId)
        {
            var now = DateTime.UtcNow;
            bool dev = false; bool devChecked = false;
            var list = new List<EventDef>();
            foreach (var e in All())
            {
                if (!e.Enabled || now < e.StartTime || now > CollectEnd(e)) continue;
                if (e.DevelopersOnly)
                {
                    if (!devChecked) { dev = accountId != 0 && PlayerDB.CheckDevFlag(accountId); devChecked = true; }
                    if (!dev) continue;
                }
                list.Add(e);
            }
            return list;
        }

        // ---------------- game config (the client's own numbers) ----------------

        private static (int minutes, int xp, int cap)? _rule;
        public static (int minutes, int xp, int cap) Rule()
        {
            if (_rule is { } r) return r;
            int minutes = 5, xp = 10, cap = 120;
            try
            {
                var arr = JArray.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "Gameconfigs.json")));
                string? V(string key) => arr.OfType<JObject>().FirstOrDefault(o => (string?)o["Key"] == key)?["Value"]?.ToString();
                if (int.TryParse(V("ProgressionEvents.Game minutes for xp conversion"), out var m) && m > 0) minutes = m;
                if (int.TryParse(V("ProgressionEvents.Xp amount per conversion"), out var x) && x > 0) xp = x;
                if (int.TryParse(V("ProgressionEvents.Max daily Xp"), out var c) && c > 0) cap = c;
            }
            catch { }
            _rule = (minutes, xp, cap);
            return _rule.Value;
        }

        // ---------------- per-player records ----------------

        public sealed class Record
        {
            [BsonId] public string Id { get; set; } = "";      // "{eventId}:{accountId}"
            public string EventId { get; set; } = "";
            public long AccountId { get; set; }
            public int Xp { get; set; }
            public DateTime Day { get; set; }                   // UTC date XpToday / GameMinutesToday belong to
            public int XpToday { get; set; }
            public double SecondsToday { get; set; }
            public double SecondsUnconverted { get; set; }
            public List<string> Collected { get; set; } = new();
            public DateTime UpdatedAt { get; set; }
        }

        private static readonly Lazy<LiteDatabase> _db = new(() =>
        {
            Directory.CreateDirectory(Path.Combine("Data", "DBs"));
            return new LiteDatabase(Path.Combine("Data", "DBs", "ProgressionEvents.db"));
        });
        private static ILiteCollection<Record> Records => _db.Value.GetCollection<Record>("records");

        public static Record GetRecord(string eventId, ulong accountId)
        {
            string id = $"{eventId}:{accountId}";
            var r = Records.FindById(id) ?? new Record { Id = id, EventId = eventId, AccountId = (long)accountId, Day = DateTime.UtcNow.Date };
            if (r.Day != DateTime.UtcNow.Date) { r.Day = DateTime.UtcNow.Date; r.XpToday = 0; r.SecondsToday = 0; }
            return r;
        }

        private static void Put(Record r) { r.UpdatedAt = DateTime.UtcNow; Records.Upsert(r); }
        public static void SaveRecord(Record r) => Put(r);

        public static int Participants(string eventId) => Records.Count(x => x.EventId == eventId);

        public static void ResetPlayer(string eventId, ulong accountId) => Records.Delete($"{eventId}:{accountId}");

        // ---------------- XP from time in public rooms ----------------

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTime> _lastBeat = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTime> _lastSave = new();

        /// <summary>
        /// Called from every presence heartbeat. Time between two heartbeats (capped, so a gap or a
        /// sleeping client never pays) spent in a PUBLIC room counts toward every running event.
        /// </summary>
        public static void OnHeartbeat(ulong accountId)
        {
            try
            {
                var now = DateTime.UtcNow;
                var prev = _lastBeat.TryGetValue(accountId, out var p) ? p : (DateTime?)null;
                _lastBeat[accountId] = now;
                if (prev == null) return;
                double secs = (now - prev.Value).TotalSeconds;
                if (secs <= 0 || secs > 90) return;

                var events = VisibleTo(accountId).Where(e => now <= e.EndTime).ToList();
                if (events.Count == 0) return;

                var inst = HeartbeatDB.GetPlayerHeartbeat(accountId)?.roomInstance;
                if (inst == null || inst.isPrivate || inst.roomId <= 0) return;
                var room = RoomDB.GetRoom((ulong)inst.roomId);
                if (room == null || room.IsDorm) return;

                var (minutes, perConv, cap) = Rule();
                foreach (var e in events)
                {
                    var r = GetRecord(e.Id, accountId);
                    r.SecondsToday += secs;
                    bool gained = false;
                    if (r.XpToday < cap)
                    {
                        r.SecondsUnconverted += secs;
                        while (r.SecondsUnconverted >= minutes * 60 && r.XpToday < cap)
                        {
                            r.SecondsUnconverted -= minutes * 60;
                            int add = Math.Min(perConv, cap - r.XpToday);
                            r.Xp += add; r.XpToday += add; gained = true;
                        }
                    }
                    // Persist on every XP gain, otherwise at most once a minute per player.
                    bool due = !_lastSave.TryGetValue(accountId, out var ls) || (now - ls).TotalSeconds >= 60;
                    if (gained || due) { Put(r); _lastSave[accountId] = now; }
                    if (gained) PushRecord(e, r);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[progression-events] heartbeat {accountId}: {ex.Message}"); }
        }

        // ---------------- collecting ----------------

        private static IEnumerable<(int index, int cumXp, Reward reward, bool premium)> Track(EventDef e)
        {
            int cum = 0;
            for (int i = 0; i < e.Steps.Count; i++)
            {
                cum += Math.Max(0, e.Steps[i].Xp);
                if (e.Steps[i].Free is { } f && f.Kind != "none") yield return (i, cum, f, false);
                if (e.Steps[i].Premium is { } pr && pr.Kind != "none") yield return (i, cum, pr, true);
            }
        }

        /// <summary>
        /// Collect rewards the player has reached. <paramref name="rewardId"/> = one reward (when
        /// the client names it), else every reached, uncollected reward. Returns how many were paid.
        /// </summary>
        public static int Collect(EventDef e, ulong accountId, string? rewardId)
        {
            if (DateTime.UtcNow > CollectEnd(e)) return 0;
            var r = GetRecord(e.Id, accountId);
            int paid = 0;
            foreach (var (index, cum, reward, premium) in Track(e))
            {
                if (rewardId != null && !string.Equals(reward.Id, rewardId, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Collected.Contains(reward.Id) || r.Xp < cum) continue;
                if (premium && !e.PremiumForEveryone) continue;
                if (!Grant(accountId, e, reward, index)) continue;
                r.Collected.Add(reward.Id); paid++;
            }
            if (paid > 0) { Put(r); PushRecord(e, r); }
            Console.WriteLine($"[progression-events] {accountId} collect on '{e.Name}'{(rewardId != null ? $" reward {rewardId}" : "")}: {paid} reward(s) paid (xp {r.Xp})");
            return paid;
        }

        private static bool Grant(ulong accountId, EventDef e, Reward reward, int index)
        {
            string msg = $"{e.Name} - reward {index + 1}";
            PendingGiftBox box = reward.Kind == "item" && !string.IsNullOrWhiteSpace(reward.AvatarItemDesc)
                ? new PendingGiftBox
                {
                    GiftContext = GiftContextLimitedTimeEvent, Message = msg, FromAccountId = 0,
                    AvatarItemDesc = reward.AvatarItemDesc, AvatarItemType = reward.AvatarItemType,
                    FriendlyName = reward.FriendlyName, GiftDropId = reward.GiftDropId, Rarity = reward.Rarity
                }
                : new PendingGiftBox
                {
                    GiftContext = GiftContextLimitedTimeEvent, Message = msg, FromAccountId = 0,
                    FriendlyName = $"{reward.Tokens} Tokens",
                    ThumbnailImageName = QuestRewards.TokenThumbnail,
                    CurrencyType = (int)CurrencyType.RecCenterTokens, Currency = reward.Tokens,
                    Rarity = QuestRewards.TokenRarity(reward.Tokens), PayOnOpen = true
                };
            if (!PlayerDB.AddPendingGiftBoxFull(accountId, box)) return false;
            GiftBoxes.Push(accountId, box, GiftBoxes.FrameReceivedImmediate, $"event '{e.Name}' reward");
            return true;
        }

        // GiftContext "LimitedTimeEvent": the client's enum lists it (read from the live metadata)
        // but its number is unverified, so boxes go out under the plain token-rewards context,
        // which is known to draw.
        public const int GiftContextLimitedTimeEvent = QuestRewards.GiftContextGameRewardTokens;

        // ---------------- wire shapes (superset of the candidate names) ----------------

        private static string Iso(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("o");

        public static JObject RewardDto(EventDef e, Step step, Reward reward, int index, bool premium) => new()
        {
            ["ProgressionEventRewardId"] = RewardNumericId(e, index, premium),
            ["ProgressionEventId"] = e.NumericId,
            ["RewardIndex"] = index,
            ["Index"] = index,
            ["Xp"] = step.Xp,
            ["RequiredXp"] = step.Xp,
            ["GiftDropId"] = reward.Kind == "item" ? reward.GiftDropId : TokenGiftDropBase + index * 2 + (premium ? 1 : 0),
            ["ImageName"] = string.IsNullOrWhiteSpace(reward.ImageName)
                ? (reward.Kind == "item" ? "" : ConfigCampus.LevelRewardImage) : reward.ImageName,
            ["IsRRPlusExclusive"] = premium,
            ["IsBonus"] = false
        };

        /// <summary>Synthetic gift drop ids for token rewards (items use their real store id).</summary>
        public const int TokenGiftDropBase = 8_800_000;

        public static JObject EventDto(EventDef e)
        {
            var rewards = new JArray();
            for (int i = 0; i < e.Steps.Count; i++)
            {
                var s = e.Steps[i];
                if (s.Free is { } f && f.Kind != "none") rewards.Add(RewardDto(e, s, f, i, false));
                if (s.Premium is { } p && p.Kind != "none") rewards.Add(RewardDto(e, s, p, i, true));
            }
            var (minutes, perConv, cap) = Rule();
            return new JObject
            {
                ["ProgressionEventId"] = e.NumericId,
                ["Id"] = e.NumericId,
                ["Name"] = e.Name,
                ["StartTime"] = Iso(e.StartTime),
                ["EndTime"] = Iso(e.EndTime),
                ["CollectionEndTime"] = Iso(CollectEnd(e)),
                ["Rewards"] = rewards,
                ["ActiveExperiments"] = new JArray(),
                ["KeepsakeRooms"] = new JArray(),
                ["KeepsakeRoomLists"] = new JArray(),
                ["CollectionRecords"] = new JArray(),
                ["UsesBoost"] = false,
                ["BoostXpMultiplier"] = 1,
                ["BoostDailyGameplayMinutesLimit"] = 0,
                ["ProgressionEventPurchasableXpBoostId"] = null,
                ["ChallengesIconImageName"] = e.ChallengesIconImageName ?? "",
                ["EventInfoImageName"] = e.EventInfoImageName ?? "",
                ["XpCap"] = cap,
                ["DailyLimit"] = cap
            };
        }

        public static JObject RecordDto(EventDef e, Record r)
        {
            int free = 0, premium = 0;
            foreach (var (_, _, reward, prem) in Track(e))
                if (r.Collected.Contains(reward.Id)) { if (prem) premium++; else free++; }
            return new JObject
            {
                ["ProgressionEventId"] = e.NumericId,
                ["AccountId"] = r.AccountId,
                ["Xp"] = r.Xp,
                ["TotalXp"] = r.Xp,
                ["GameMinutesToday"] = (int)(r.SecondsToday / 60),
                ["RewardsCollected"] = free,
                ["PremiumRewardsCollected"] = premium,
                ["BonusRewardsCollected"] = 0,
                ["XpBoostLastPurchasedAt"] = null
            };
        }

        public static void PushRecord(EventDef e, Record r)
        {
            try
            {
                var frame = Newtonsoft.Json.JsonConvert.SerializeObject(new WebsocketEvents.Reponse { Id = "ProgressionEventsRecordUpdate", Msg = RecordDto(e, r) });
                Notifications.FireAndForget(Notifications.SendToPlayer((ulong)r.AccountId, frame), "progression record");
            }
            catch { }
        }

        // ---------------- store items for the admin picker ----------------

        public sealed class StoreItem
        {
            public int GiftDropId; public string FriendlyName = ""; public string AvatarItemDesc = "";
            public int AvatarItemType; public int Rarity; public string ThumbnailImageName = "";
        }
        private static List<StoreItem>? _store;
        public static List<StoreItem> StoreItems()
        {
            if (_store != null) return _store;
            var list = new List<StoreItem>();
            try
            {
                string file = Path.Combine(StoreCatalog.StoreDirectory, "3.json");
                foreach (var it in JObject.Parse(File.ReadAllText(file))["StoreItems"] as JArray ?? new JArray())
                {
                    if (it["GiftDrop"] is not JObject gd || (bool?)gd["IsQuery"] == true) continue;
                    string desc = (string?)gd["AvatarItemDesc"] ?? "";
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    list.Add(new StoreItem
                    {
                        GiftDropId = (int?)gd["GiftDropId"] ?? 0, FriendlyName = (string?)gd["FriendlyName"] ?? "",
                        AvatarItemDesc = desc, AvatarItemType = (int?)gd["AvatarItemType"] ?? 0,
                        Rarity = (int?)gd["Rarity"] ?? 0, ThumbnailImageName = (string?)gd["ThumbnailImageName"] ?? ""
                    });
                }
            }
            catch (Exception ex) { Console.WriteLine($"[progression-events] store read failed: {ex.Message}"); }
            _store = list;
            return list;
        }
    }
}
