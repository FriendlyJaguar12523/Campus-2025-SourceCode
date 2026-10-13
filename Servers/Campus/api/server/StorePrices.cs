using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.server.Classes;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// One Rec Center Token price per item across every store (2026-09-27 request: Thriller Suit
    /// (Orange) was 4000 in the Watch and 4500 in Pop Up Shop 1).
    ///
    /// The price an item sells for is the one the admin most recently SET for it in the Stores tab,
    /// in whichever store (rows carry "PriceSetAt" from then on; older edits count as set before any
    /// stamped one, newest admin row first among them). An item never priced there keeps the Watch
    /// store's price. Every served store row priced in tokens is rewritten to it (subscriber price
    /// = the usual 10% off), and a purchase is charged it. Items priced in room currencies or other
    /// currencies are left alone.
    ///
    /// An item is its GiftDropId plus the pack size (ItemCount), so a 1-pack and a 5-pack of the
    /// same consumable keep their own prices.
    /// </summary>
    public static class StorePrices
    {
        public const string PriceSetAtKey = "PriceSetAt";
        private const int Tokens = (int)CurrencyType.RecCenterTokens;

        public static string? ItemKey(JToken row)
        {
            var gd = row["GiftDrop"];
            int drop = (int?)gd?["GiftDropId"] ?? 0;
            if (drop == 0) return null;
            int count = Math.Max(1, (int?)gd?["ItemCount"] ?? 1);
            return $"{drop}x{count}";
        }

        /// <summary>The row's token price, or null when it is not sold for tokens.</summary>
        public static int? TokenPrice(JToken row)
        {
            foreach (var p in row["Prices"] as JArray ?? new JArray())
                if (((int?)p["CurrencyType"] ?? 0) == Tokens) return (int?)p["Price"];
            return null;
        }

        // ---- the canonical price table ------------------------------------------------------------

        private static readonly object _lock = new();
        private static string _key = "";
        private static Dictionary<string, int> _prices = new();
        private static Dictionary<string, int> _watchBase = new();
        private static string _watchKey = "";

        /// <summary>Called while the Watch store is built, BEFORE unification: its own prices are the fallback.</summary>
        public static void SetWatchBase(JObject watchRoot, string watchKey)
        {
            var map = new Dictionary<string, int>();
            foreach (var r in watchRoot["StoreItems"] as JArray ?? new JArray())
                if (ItemKey(r) is { } k && TokenPrice(r) is { } p && !map.ContainsKey(k)) map[k] = p;
            lock (_lock) { _watchBase = map; _watchKey = watchKey; }
        }

        public static string WatchKey { get { lock (_lock) return _watchKey; } }

        private static Dictionary<string, int> Table()
        {
            string key = $"{StoreEdits.Version}|{WatchKey}";
            lock (_lock)
            {
                if (key == _key) return _prices;
                // Every admin price, newest first: stamped rows by stamp, unstamped (older) rows by
                // admin id (a higher id was added later).
                var edits = new List<(string item, int price, DateTime at, int pid)>();
                foreach (int store in Enum.GetValues(typeof(StorefrontType)).Cast<int>().Distinct())
                {
                    if (StoreServe.IsPerPlayer(store)) continue;
                    foreach (var r in StoreEdits.Rows(store))
                    {
                        if (ItemKey(r) is not { } item || TokenPrice(r) is not { } price) continue;
                        DateTime at = DateTime.TryParse((string?)r[PriceSetAtKey], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;
                        edits.Add((item, price, at, (int?)r["PurchasableItemId"] ?? 0));
                    }
                }
                var table = new Dictionary<string, int>(_watchBase);
                foreach (var e in edits.OrderBy(e => e.at).ThenBy(e => e.pid))
                    table[e.item] = e.price;   // later wins
                _prices = table;
                _key = key;
                return table;
            }
        }

        /// <summary>The one token price this item sells for, or null when no store has it for tokens.</summary>
        public static int? PriceFor(JToken row) =>
            ItemKey(row) is { } k && Table().TryGetValue(k, out var p) ? p : null;

        /// <summary>Rewrite every token-priced row of a served store to the item's one price. Returns how many changed.</summary>
        public static int Apply(JObject root)
        {
            int changed = 0;
            foreach (var r in (root["StoreItems"] as JArray ?? new JArray()).OfType<JObject>())
            {
                r.Remove(PriceSetAtKey);   // bookkeeping only - never sent to the client
                if (TokenPrice(r) is not { } now || PriceFor(r) is not { } want || want == now) continue;
                foreach (var p in r["Prices"] as JArray ?? new JArray())
                    if (((int?)p["CurrencyType"] ?? 0) == Tokens) p["Price"] = want;
                foreach (var p in r["SubscriberPrices"] as JArray ?? new JArray())
                    if (((int?)p["CurrencyType"] ?? 0) == Tokens) p["Price"] = StoreGenerator.SubscriberPriceFor(want);
                changed++;
            }
            return changed;
        }

        /// <summary>Charge the served (unified) price for a purchase priced in tokens.</summary>
        public static void ApplyTo(StoreCatalog.GiftDropInfo item, JObject? servedRow)
        {
            if (servedRow == null || item.Currency != Tokens || TokenPrice(servedRow) is not { } price) return;
            item.Cost = price;
            if (item.SubscriberCurrency < 0 || item.SubscriberCurrency == Tokens)
            {
                item.SubscriberCost = StoreGenerator.SubscriberPriceFor(price);
                item.SubscriberCurrency = Tokens;
            }
        }
    }
}
