using LiteDB;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Creator earnings. When a player buys something another player MADE - an invention, a UGC
    /// clothing item, a room key, room currency, a room consumable - the maker gets
    /// ConfigCampus.CreatorEarningsPercent (30%) of what the buyer spent, in the same currency.
    ///
    /// It is not paid on the spot: an earning PENDS for ConfigCampus.CreatorEarningsPendDays (14)
    /// and is then paid on the first SUNDAY (00:00 UTC) on or after that. The Coach / system
    /// accounts and the room importer never earn. Every payout is logged and pushes the balance,
    /// so the number in the corner moves when it lands.
    /// </summary>
    public static class EarningsDB
    {
        private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(Path.Combine("Data", "DBs", "Earnings.db")));

        public class Entry
        {
            [BsonId] public int Id { get; set; }
            public ulong SellerId { get; set; }
            public ulong BuyerId { get; set; }
            public string Kind { get; set; } = "";      // invention / ugc / roomkey / roomcurrency / roomconsumable
            public string ItemId { get; set; } = "";
            public string ItemName { get; set; } = "";
            public int CurrencyType { get; set; } = 2;
            public long Spent { get; set; }
            public long Share { get; set; }
            public DateTime BoughtAt { get; set; } = DateTime.UtcNow;
            public DateTime AvailableAt { get; set; }
            public DateTime PayoutAt { get; set; }
            public DateTime? PaidAt { get; set; }
        }

        private static ILiteCollection<Entry> Col()
        {
            var c = _db.Value.GetCollection<Entry>("earnings");
            c.EnsureIndex(x => x.SellerId);
            c.EnsureIndex(x => x.PaidAt);
            return c;
        }

        /// <summary>The first Sunday 00:00 UTC on or after <paramref name="t"/>.</summary>
        public static DateTime NextSunday(DateTime t)
        {
            var day = new DateTime(t.Year, t.Month, t.Day, 0, 0, 0, DateTimeKind.Utc);
            int add = ((int)DayOfWeek.Sunday - (int)day.DayOfWeek + 7) % 7;
            if (add == 0 && day < t) add = 7;
            return day.AddDays(add);
        }

        private static bool CanEarn(ulong seller)
        {
            if (seller == 0) return false;
            var acc = PlayerDB.GetAccountById(seller);
            if (acc == null) return false;
            foreach (var n in ConfigCampus.NoEarningsUsernames)
                if (string.Equals(acc.Username, n, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>Record a sale. Returns the entry, or null when the seller does not earn (buying your own, Coach, unknown).</summary>
        public static Entry? Record(ulong seller, ulong buyer, string kind, string itemId, string itemName, int currencyType, long spent)
        {
            if (spent <= 0 || seller == buyer || !CanEarn(seller)) return null;
            long share = (long)Math.Floor(spent * ConfigCampus.CreatorEarningsPercent / 100.0);
            if (share <= 0) return null;
            var now = DateTime.UtcNow;
            var available = now.AddDays(ConfigCampus.CreatorEarningsPendDays);
            var e = new Entry
            {
                SellerId = seller, BuyerId = buyer, Kind = kind, ItemId = itemId, ItemName = itemName,
                CurrencyType = currencyType, Spent = spent, Share = share,
                BoughtAt = now, AvailableAt = available, PayoutAt = NextSunday(available)
            };
            try { Col().Insert(e); }
            catch (Exception ex) { Console.WriteLine($"[earnings] record failed: {ex.Message}"); return null; }
            Console.WriteLine($"[earnings] {seller} earns {share} of {spent} (currency {currencyType}) from {buyer} buying {kind} '{itemName}' - pays out {e.PayoutAt:yyyy-MM-dd}");
            return e;
        }

        /// <summary>Pay every earning whose Sunday has come. Called by the timer and on demand.</summary>
        public static int PayDue()
        {
            int paid = 0;
            try
            {
                var col = Col();
                var now = DateTime.UtcNow;
                var due = col.Find(x => x.PaidAt == null).Where(x => x.PayoutAt <= now).ToList();
                foreach (var group in due.GroupBy(x => (x.SellerId, x.CurrencyType)))
                {
                    long total = group.Sum(x => x.Share);
                    if (total <= 0) continue;
                    long balance = PlayerDB.AddBalance(group.Key.SellerId, group.Key.CurrencyType, total);
                    foreach (var e in group) { e.PaidAt = now; col.Update(e); paid++; }
                    GiftBoxes.PushBalance(group.Key.SellerId, group.Key.CurrencyType, balance, "creator earnings");
                    Console.WriteLine($"[earnings] paid {total} (currency {group.Key.CurrencyType}) to {group.Key.SellerId} for {group.Count()} sale(s) -> balance {balance}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[earnings] payout failed: {ex.Message}"); }
            return paid;
        }

        public static List<Entry> ForPlayer(ulong seller, int take = 200) =>
            Col().Find(x => x.SellerId == seller).OrderByDescending(x => x.BoughtAt).Take(take).ToList();

        public static List<Entry> Recent(int take = 200) =>
            Col().FindAll().OrderByDescending(x => x.BoughtAt).Take(take).ToList();

        public static (long pending, long paid, DateTime? nextPayout) Summary(ulong seller)
        {
            var all = Col().Find(x => x.SellerId == seller).ToList();
            long pending = all.Where(x => x.PaidAt == null).Sum(x => x.Share);
            long paidTotal = all.Where(x => x.PaidAt != null).Sum(x => x.Share);
            DateTime? next = all.Where(x => x.PaidAt == null).Select(x => (DateTime?)x.PayoutAt).Min();
            return (pending, paidTotal, next);
        }

        private static System.Threading.Timer? _timer;
        /// <summary>Check for due payouts every 15 minutes (and once at boot).</summary>
        public static void Start()
        {
            _timer ??= new System.Threading.Timer(_ => PayDue(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
        }
    }
}
