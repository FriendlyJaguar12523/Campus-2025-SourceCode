using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.server.Classes;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Every gift-drop storefront exactly as players get it from
    /// GET /api/storefronts/v3/giftdropstore/{id}, and the purchase rules that follow from it:
    ///
    ///   - An item is only buyable if the store the client names SELLS it as served. buyItem and
    ///     bulkpurchase used to fall back to scanning every store file, so any id in any file -
    ///     hidden rows, rows the generated Watch store drops, another store's stock - could be
    ///     bought by naming the wrong store (seen 2026-09-22: Watch items bought "from RecCenter"
    ///     and "from LaserTag").
    ///   - A buy that names no real store (a player's profile / wishlist, or none) can only buy
    ///     something a store sells for TOKENS.
    ///
    /// Base stock minus <see cref="Excluded"/>, then the admin's Stores-tab edits on top, so an
    /// item the admin deliberately adds to a store is sold even if it matches an exclusion.
    /// </summary>
    public static class StoreServe
    {
        // ---- items taken off every store (2026-09-26 request) -----------------------------------

        private static readonly Regex ClassOfShirt = new(@"^class of \d{4}\b", RegexOptions.IgnoreCase);
        private static readonly Regex Contest = new(@"\bcontest\b", RegexOptions.IgnoreCase);
        private static readonly Regex Golden = new(@"\bgold(en)?\b", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Laser Tag Alpha Shirt",
            "Rec Room Hoodie (Gold)",
            "A Gold Hoodie",
            "Gold Hoodie",
            "Video Partner Hoodie",
        };

        /// <summary>
        /// Items no store sells: Class of 20XX shirts, anything from a contest (clothing, the
        /// Video Contest shirts, contest Maker Pens), golden equipment skins, the gold hoodies,
        /// the Laser Tag Alpha Shirt and the Video Partner Hoodie.
        /// </summary>
        public static bool Excluded(JToken row)
        {
            var gd = row["GiftDrop"];
            string name = ((string?)gd?["FriendlyName"] ?? "").Trim();
            if (name.Length == 0) return false;
            if (ExcludedNames.Contains(name) || ClassOfShirt.IsMatch(name) || Contest.IsMatch(name)) return true;
            bool isSkin = !string.IsNullOrWhiteSpace((string?)gd?["EquipmentPrefabName"]);
            return isSkin && Golden.IsMatch(name);
        }

        // ---- served stores ----------------------------------------------------------------------

        private sealed class Served
        {
            public string Key = "";
            public string Json = "";
            public Dictionary<int, JObject> ByPid = new();
        }

        private static readonly Dictionary<int, Served> _cache = new();
        private static readonly object _lock = new();

        /// <summary>Storefronts that are built per player/room, or never sell stock.</summary>
        public static bool IsPerPlayer(int id) =>
            id <= 0 || id == (int)StorefrontType.Wishlist || id == (int)StorefrontType.Player_Profile;

        /// <summary>The store JSON as served, or null when the storefront has no stock at all.</summary>
        public static string? ServedJson(int id) => Get(id)?.Json;

        /// <summary>The row this store serves under this id (do not mutate), or null.</summary>
        public static JObject? ServedRow(int id, int pid) =>
            Get(id) is { } s && s.ByPid.TryGetValue(pid, out var r) ? r : null;

        /// <summary>Every storefront that can have stock: the Watch store, each store file, each admin-stocked store.</summary>
        public static List<int> StockedStores()
        {
            var ids = new SortedSet<int> { StoreGenerator.WatchStorefront };
            try
            {
                if (Directory.Exists(StoreCatalog.StoreDirectory))
                    foreach (var f in Directory.GetFiles(StoreCatalog.StoreDirectory, "*.json"))
                        if (int.TryParse(Path.GetFileNameWithoutExtension(f), out var n)) ids.Add(n);
            }
            catch { }
            foreach (int id in Enum.GetValues(typeof(StorefrontType)).Cast<int>().Distinct())
                if (StoreEdits.HasEdits(id)) ids.Add(id);
            ids.RemoveWhere(IsPerPlayer);
            // The Watch store first: its ids are what the store pages and discovery hand out.
            return ids.OrderBy(i => i == StoreGenerator.WatchStorefront ? 0 : 1).ThenBy(i => i).ToList();
        }

        /// <summary>
        /// The first store (Watch first) that serves this id for TOKENS - what a profile,
        /// wishlist or store-less buy may purchase. Null when no store sells it for tokens.
        /// </summary>
        public static (int store, JObject row)? FindTokenRow(int pid)
        {
            foreach (int id in StockedStores())
                if (ServedRow(id, pid) is { } r && StoreCatalog.PricedInTokens(r)) return (id, r);
            return null;
        }

        /// <summary>
        /// The purchase check for buyItem / bulkpurchase. Returns the priced item, or null with
        /// the reason. <paramref name="storefrontType"/> is what the client sent.
        /// </summary>
        public static StoreCatalog.GiftDropInfo? ResolvePurchase(int pid, int storefrontType, out string why)
        {
            if (!IsPerPlayer(storefrontType))
            {
                if (ServedRow(storefrontType, pid) == null)
                {
                    // The basket checkout names the WATCH for everything in it, so every pop-up shop
                    // buy and gift was refused "not sold in Watch" (2026-09-27: items 900014-900029,
                    // PopUpShop_1). Take the store that does sell it - provided every store serving
                    // this id means the same item, so a buy can never deliver something other than
                    // what was on screen.
                    var elsewhere = StockedStores().Where(s => s != storefrontType)
                        .Select(s => (store: s, row: ServedRow(s, pid))).Where(x => x.row != null).ToList();
                    int drops = elsewhere.Select(x => (int?)x.row!["GiftDrop"]?["GiftDropId"] ?? 0).Distinct().Count();
                    if (elsewhere.Count == 0 || drops != 1)
                    {
                        why = $"not sold in {StoreCatalog.StorefrontName(storefrontType)}" +
                              (elsewhere.Count > 0 ? $" (and {elsewhere.Count} other stores disagree on what id {pid} is)" : "");
                        return null;
                    }
                    storefrontType = elsewhere[0].store;
                }
                var item = StoreCatalog.FindStoreItem(pid, storefrontType);
                if (item != null) StorePrices.ApplyTo(item, ServedRow(storefrontType, pid));
                why = item == null ? "sold, but its row could not be priced" : "";
                return item;
            }

            // A profile / wishlist / unspecified buy: only what a store sells for tokens.
            var hit = FindTokenRow(pid);
            if (hit == null)
            {
                why = "no store sells it for tokens";
                return null;
            }
            var found = StoreCatalog.FindStoreItem(pid, hit.Value.store);
            if (found != null) StorePrices.ApplyTo(found, hit.Value.row);
            if (found != null && found.Currency != (int)CurrencyType.RecCenterTokens)
            {
                why = "no store sells it for tokens";
                return null;
            }
            why = found == null ? "sold, but its row could not be priced" : $"sold in {StoreCatalog.StorefrontName(hit.Value.store)}";
            return found;
        }

        // ---- building ---------------------------------------------------------------------------

        private static Served? Get(int id)
        {
            if (IsPerPlayer(id)) return null;
            // Every store's token prices follow the Watch's (StorePrices), so build it first.
            if (id != StoreGenerator.WatchStorefront) Get(StoreGenerator.WatchStorefront);
            string key;
            string file = "";
            JObject? watch = null;
            if (id == StoreGenerator.WatchStorefront)
            {
                watch = StoreGenerator.MergedWatchStore();
                key = $"w{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(watch)}";
            }
            else if ((file = StoreCatalog.StoreFileForId(id)).Length > 0)
                key = $"f{File.GetLastWriteTimeUtc(file).Ticks}|{ConfigCampus.FreeHairDyes}|{ConfigCampus.HiddenItemsStorefronts}";
            else if (StoreEdits.HasEdits(id))
                key = "e";
            else
                return null;
            key += $"|{StoreEdits.Version}";
            if (id != StoreGenerator.WatchStorefront) key += $"|{StorePrices.WatchKey}";

            lock (_lock)
                if (_cache.TryGetValue(id, out var hit) && hit.Key == key) return hit;

            JObject root;
            try
            {
                if (watch != null)
                    root = (JObject)watch.DeepClone();
                else if (file.Length > 0)
                {
                    // Same forms giftdropstore always served: free hair dyes / hidden items, then
                    // every row giftable.
                    string? baseJson = null;
                    if (ConfigCampus.FreeHairDyes || ConfigCampus.HiddenItemsStorefronts)
                        baseJson = HairGrants.StoreJsonWithFreeDyes(file);
                    string json = StoreCatalog.StoreJsonGiftable(file, baseJson) ?? baseJson ?? File.ReadAllText(file);
                    root = JObject.Parse(json);
                }
                else
                {
                    root = new JObject
                    {
                        ["NewUntil"] = null,
                        ["NextUpdate"] = "2222-02-22T22:22:00Z",
                        ["StoreItems"] = new JArray(),
                        ["StorefrontName"] = StoreCatalog.StorefrontName(id),
                        ["StorefrontType"] = id,
                        ["SubscriberDiscountPercent"] = 10
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[store] could not build store {id}: {ex.Message}");
                return null;
            }

            int dropped = 0;
            if (root["StoreItems"] is JArray items)
            {
                var kept = new JArray();
                foreach (var it in items)
                {
                    if (Excluded(it)) { dropped++; continue; }
                    kept.Add(it);
                }
                root["StoreItems"] = kept;
            }
            StoreEdits.ApplyTo(id, root);
            if (id == StoreGenerator.WatchStorefront) StorePrices.SetWatchBase(root, key);

            // Index what this store SELLS before borrowing anything below.
            var byPid = new Dictionary<int, JObject>();
            foreach (var it in (root["StoreItems"] as JArray ?? new JArray()).OfType<JObject>())
            {
                int pid = (int?)it["PurchasableItemId"] ?? 0;
                if (pid != 0) byPid.TryAdd(pid, it);
            }

            // The client resolves an item's details page by PurchasableItemId against the WATCH
            // storefront it downloaded (its lookup awaits a "watchStorefrontTask"), whatever store
            // the item was clicked in. A Stores-tab item that only exists in a pop-up shop was
            // therefore unknown to it: "StoreItemModel: Could not find PurchasableGiftDrop with
            // ID 900005" and a blank item page (2026-09-26, Fae Horns in PopUpShop_1). So every
            // admin-added row of the other stores rides along in the Watch payload too. Admin ids
            // (900000+) are unique across stores, and the Watch store's browse rows come from the
            // store lists, which read the generated stock - so these are resolvable, not listed.
            // They stay out of byPid: the Watch store does not SELL them (a buy names the pop-up).
            if (id == StoreGenerator.WatchStorefront && root["StoreItems"] is JArray watchRows)
            {
                var carried = new HashSet<int>(byPid.Keys);
                int borrowed = 0;
                foreach (int other in Enum.GetValues(typeof(StorefrontType)).Cast<int>().Distinct())
                {
                    if (other == id || IsPerPlayer(other)) continue;
                    foreach (var r in StoreEdits.Rows(other))
                    {
                        int pid = (int?)r["PurchasableItemId"] ?? 0;
                        if (pid < StoreEdits.AdminIdBase || !carried.Add(pid)) continue;
                        StoreGenerator.ShapeRow(r);
                        r["CanBeGifted"] = StoreCatalog.PricedInTokens(r);
                        watchRows.Add(r);
                        borrowed++;
                    }
                }
                if (borrowed > 0) Console.WriteLine($"[store] Watch payload carries {borrowed} other-store item(s) so their item pages resolve");
            }

            // A skin row names its skin the way the locker list does, so the item the store sells and
            // the one in the locker are the same item to the client (not a short-guid twin).
            foreach (var r in (root["StoreItems"] as JArray ?? new JArray()).OfType<JObject>())
                if (r["GiftDrop"] is JObject sgd && !string.IsNullOrWhiteSpace((string?)sgd["EquipmentPrefabName"]))
                    sgd["EquipmentModificationGuid"] = SkinOwnership.CanonicalGuid((string?)sgd["EquipmentPrefabName"], (string?)sgd["EquipmentModificationGuid"]);

            // One token price per item in every store (the admin's latest, else the Watch's).
            int repriced = StorePrices.Apply(root);
            if (repriced > 0) Console.WriteLine($"[store] {StoreCatalog.StorefrontName(id)}: {repriced} token price(s) matched to the item's one price");

            // Skins / consumables / item-less boxes: the item's own picture, never a thumbnail
            // name whose file is not on this server (see ItemImages).
            int thumbsFixed = 0;
            foreach (var r in (root["StoreItems"] as JArray ?? new JArray()).OfType<JObject>())
                if (ItemImages.FixStoreRow(r)) thumbsFixed++;
            if (thumbsFixed > 0) Console.WriteLine($"[store] {StoreCatalog.StorefrontName(id)}: {thumbsFixed} skin/consumable/box thumbnail(s) pointed at the item's own picture");

            var served = new Served { Key = key, Json = root.ToString(Newtonsoft.Json.Formatting.None), ByPid = byPid };
            if (dropped > 0)
                Console.WriteLine($"[store] {StoreCatalog.StorefrontName(id)}: {dropped} excluded item(s) taken off sale, {served.ByPid.Count} served");
            lock (_lock) _cache[id] = served;
            return served;
        }
    }
}
