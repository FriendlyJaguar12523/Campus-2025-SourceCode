using LiteDB;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Daily login streak. The first login of each day extends the player's streak (a missed day starts it over at day 1) and
    /// pays a token gift box that grows with the streak: a 7-day table that repeats, plus a bonus for every full week, capped.
    /// The box is the same pay-on-open kind level-ups use, so it arrives in the player's notifications and pays when opened.
    ///
    /// "Day" is the server's calendar day shifted by <see cref="ConfigCampus.StreakDayOffsetHours"/>. A login is credited once
    /// per day however many times the client re-posts player/login (it does on every reconnect), and the day is only recorded
    /// after the box is safely stored, so a failed store is retried by the next login instead of losing the day.
    /// </summary>
    public static class LoginStreaks
    {
        public class Streak
        {
            [BsonId] public ulong AccountId { get; set; }
            public string LastDay { get; set; } = "";   // yyyy-MM-dd of the last credited day
            public int Current { get; set; }
            public int Best { get; set; }
            public int TotalDays { get; set; }
        }

        private static readonly Lazy<LiteDatabase> _db = new(() =>
            new LiteDatabase(Path.Combine(Environment.CurrentDirectory, "Data", "DBs", "LoginStreaks.db")));
        private static ILiteCollection<Streak> Col() => _db.Value.GetCollection<Streak>("streaks");
        private static readonly object _lock = new();

        /// <summary>The clock (replaceable so a test can move between days).</summary>
        public static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        public static string Today() => UtcNow().AddHours(ConfigCampus.StreakDayOffsetHours).ToString("yyyy-MM-dd");

        /// <summary>Tokens for streak day N (1-based): the table entry for the day within its week, plus the capped weekly bonus.</summary>
        public static long RewardFor(int day)
        {
            var table = ConfigCampus.StreakRewards;
            if (day < 1 || table.Length == 0) return 0;
            long baseTokens = table[(day - 1) % table.Length];
            int weeks = Math.Min((day - 1) / table.Length, ConfigCampus.StreakBonusMaxWeeks);
            return baseTokens + (long)weeks * ConfigCampus.StreakBonusPerWeek;
        }

        /// <summary>Called on every player/login. Never throws: a streak problem must not break a login.</summary>
        public static void OnLogin(ulong accountId)
        {
            try
            {
                if (!ConfigCampus.StreaksEnabled || accountId == 0) return;
                if (PlayerDB.HasActiveModerationBlock(accountId)) return;   // banned / timed out players earn nothing

                PendingGiftBox box;
                int day;
                lock (_lock)
                {
                    string today = Today();
                    var s = Col().FindById(accountId) ?? new Streak { AccountId = accountId };
                    if (s.LastDay == today) return;   // already credited today

                    int gap = DaysBetween(s.LastDay, today);   // 1 = consecutive; int.MaxValue = never logged in
                    day = (gap >= 1 && gap <= 1 + ConfigCampus.StreakGraceDays) ? s.Current + 1 : 1;
                    long tokens = RewardFor(day);

                    box = new PendingGiftBox
                    {
                        GiftContext = QuestRewards.GiftContextGameRewardTokens,
                        Message = day == 1 ? "Day 1 of your login streak! Come back tomorrow for a bigger reward."
                                           : $"Day {day} of your login streak! Come back tomorrow to keep it going.",
                        FromAccountId = 0,
                        FriendlyName = $"Day {day} Streak Box",
                        ThumbnailImageName = QuestRewards.TokenThumbnail,
                        CurrencyType = (int)CurrencyType.RecCenterTokens,
                        Currency = tokens,
                        Rarity = QuestRewards.TokenRarity(tokens),
                        PayOnOpen = true
                    };
                    if (!PlayerDB.AddPendingGiftBoxFull(accountId, box))
                    {
                        Console.WriteLine($"[streak] {accountId}: could not store the day {day} box - will retry on the next login");
                        return;
                    }
                    s.LastDay = today;
                    s.Current = day;
                    s.Best = Math.Max(s.Best, day);
                    s.TotalDays++;
                    Col().Upsert(s);
                    Console.WriteLine($"[streak] {accountId} day {day} (best {s.Best}): {tokens} tokens in box {box.NumericId}");
                }

                // The client is still loading when player/login arrives; a box pushed that early is dropped. It is already stored,
                // so a missed push only means it shows at the next login - the delay just makes the live notification land.
                var pushBox = box;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ConfigCampus.StreakPushDelaySeconds));
                        GiftBoxes.Push(accountId, pushBox, GiftBoxes.FrameReceivedImmediate, $"login streak day {day}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[streak] {accountId} push failed: {ex.Message}"); }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[streak] {accountId} failed: {ex.Message}");
            }
        }

        public static Streak? Get(ulong accountId) => Col().FindById(accountId);

        private static int DaysBetween(string from, string to)
        {
            if (!DateTime.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var a)) return int.MaxValue;
            if (!DateTime.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var b)) return int.MaxValue;
            return (int)(b.Date - a.Date).TotalDays;
        }
    }
}
