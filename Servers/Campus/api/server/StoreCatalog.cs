using Campus_live_rewrite.api.server.Classes;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Reads the gift-drop store catalogs in Data/APIS/GiftDropStores/{StorefrontType}.json.
    ///
    /// Ported from the 2023 Campus server (D:\Puck\Campus-Server), whose StoreController had
    /// a working purchase flow. The comments here record what that server learned the hard
    /// way - they are not guesses.
    /// </summary>
    public static class StoreCatalog
    {
        public static string StoreDirectory =>
            Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "GiftDropStores");

        /// <summary>One purchasable item, flattened out of a store file.</summary>
        public class GiftDropInfo
        {
            public int Cost;
            public int Currency = (int)CurrencyType.RecCenterTokens;  // currency the buyer pays in
            public int SubscriberCost = -1;      // from SubscriberPrices; -1 = none listed
            public int SubscriberCurrency = -1;
            public string Reward = "";           // GiftDrop.AvatarItemDesc, RAW (see PendingGiftBox)
            public string Name = "";
            public int Rarity;
            public int GiftDropId;
            public string ConsumableItemDesc = "";
            public int ItemCount = 1;            // GiftDrop.ItemCount - how many of a consumable one purchase gives
            public int AvatarItemType;
            public int GdCurrencyType;           // GiftDrop.CurrencyType
            public int GdCurrency;               // GiftDrop.Currency
            public string EquipmentPrefabName = "";
            public string EquipmentModificationGuid = "";
            /// <summary>A custom (downloadable) avatar item the row sells - GiftDrop.CustomAvatarItemId.</summary>
            public string CustomAvatarItemId = "";
            public string ThumbnailImageName = "";
            public int EquipmentItemId;
            public int AvatarItemId;
            public string StoreFile = "";
            public int PurchasableItemId;
            public int StorefrontType;

            /// <summary>Does the row put an AVATAR item in the box (legacy descriptor or custom item)?</summary>
            public bool IsAvatarItem => !string.IsNullOrWhiteSpace(Reward) || !string.IsNullOrWhiteSpace(CustomAvatarItemId);
        }

        /// <summary>
        /// AvatarItemType as the box / package must carry it: the type for an avatar item, JSON NULL
        /// for anything else. The client decides what a package holds by NULL checks - a skin or a
        /// consumable sent with AvatarItemType 0 is read as an avatar item with nothing to load
        /// ("Can't load item. It's saved in your Dorm mirror."). Every real storefront row of a skin
        /// or consumable carries null here (Watch_EnumValue_3.json), so this is the wire's rule.
        /// </summary>
        public static int? AvatarTypeFor(GiftDropInfo i) => i.IsAvatarItem ? i.AvatarItemType : (int?)null;
        /// <summary>The descriptor for the package: as written when there is one, else null (same reason).</summary>
        public static string? AvatarDescFor(GiftDropInfo i) => string.IsNullOrWhiteSpace(i.Reward) ? null : i.Reward;

        /// <summary>The catalog file backing a storefront id, or "" if it has none.</summary>
        public static string StoreFileForId(int id)
        {
            string specific = Path.Combine(StoreDirectory, $"{id}.json");
            return File.Exists(specific) ? specific : "";
        }

        public static string StorefrontName(int id) =>
            Enum.IsDefined(typeof(StorefrontType), id)
                ? ((StorefrontType)id).ToString()
                : $"Unknown({id})";

        /// <summary>
        /// Look up an item by PurchasableItemId.
        ///
        /// PurchasableItemIds are only unique WITHIN a storefront, not across them - id 487 is
        /// "Drum Major Hat (Yellow)" on the Watch store and "RECS Shirt (Green)" on Laser Tag,
        /// and the Isle/Crescendo catalogs collide on 971/972. So search the storefront the
        /// client said it was buying from FIRST, and only scan the rest when that store has no
        /// such id (or the client did not say which store).
        /// </summary>
        public static GiftDropInfo? FindStoreItem(int purchasableItemId, int storefrontType = -1)
        {
            // A catalog id (10000+) is a GENERATED Watch-store row - it exists in no file. The
            // same object the store was served from prices the purchase, which is the reference's
            // whole point: one table, both sides, or "Price has changed" on every buy.
            // Admin edits (Stores tab, StoreEdits) come first: an added row exists nowhere else,
            // and an edited one carries the price the store page now shows.
            if (storefrontType >= 0)
            {
                var own = StoreEdits.RowFor(storefrontType, purchasableItemId);
                if (own != null) return ParseRow(own, purchasableItemId, storefrontType);
                if (StoreEdits.IsRemoved(storefrontType, purchasableItemId)) return null;
            }
            else
            {
                var any = StoreEdits.RowsAnywhere(purchasableItemId);
                if (any.Count > 0) return ParseRow(any[0].row, purchasableItemId, any[0].store);
            }
            var gen = StoreGenerator.FindGenerated(purchasableItemId);
            if (gen != null)
                return StoreEdits.IsRemoved(StoreGenerator.WatchStorefront, purchasableItemId) ? null : gen;
            return FindStoreItemInFiles(purchasableItemId, storefrontType);
        }

        private static GiftDropInfo? FindStoreItemInFiles(int purchasableItemId, int storefrontType = -1)
        {
            if (!Directory.Exists(StoreDirectory)) return null;

            var files = new List<string>();
            if (storefrontType >= 0)
            {
                string preferred = StoreFileForId(storefrontType);
                if (!string.IsNullOrEmpty(preferred)) files.Add(preferred);
            }
            foreach (var f in Directory.GetFiles(StoreDirectory, "*.json"))
                if (!files.Contains(f)) files.Add(f);

            foreach (var file in files)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("StoreItems", out var items)) continue;

                    foreach (var it in items.EnumerateArray())
                    {
                        if (!it.TryGetProperty("PurchasableItemId", out var pid)
                            || pid.ValueKind != System.Text.Json.JsonValueKind.Number
                            || pid.GetInt32() != purchasableItemId) continue;

                        return ParseRow(it, purchasableItemId, Path.GetFileName(file), StorefrontTypeFromFile(file));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[store] could not read {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>One store row (the client's StoreItem shape) as the purchase paths price it.</summary>
        private static GiftDropInfo ParseRow(System.Text.Json.JsonElement it, int purchasableItemId, string storeFile, int storefront)
        {
            var info = new GiftDropInfo();

            if (it.TryGetProperty("Prices", out var prices)
                && prices.ValueKind == System.Text.Json.JsonValueKind.Array
                && prices.GetArrayLength() > 0)
            {
                var p0 = prices[0];
                if (p0.TryGetProperty("Price", out var pr) && pr.ValueKind == System.Text.Json.JsonValueKind.Number)
                    info.Cost = pr.GetInt32();
                if (p0.TryGetProperty("CurrencyType", out var cc) && cc.ValueKind == System.Text.Json.JsonValueKind.Number)
                    info.Currency = cc.GetInt32();
            }

            // Rec Room+ price. The client shows this to a subscriber and sends it back
            // as RequestedPrice. Ignoring it means RR+ members (everyone here) silently
            // overpay on every purchase.
            if (it.TryGetProperty("SubscriberPrices", out var sp)
                && sp.ValueKind == System.Text.Json.JsonValueKind.Array
                && sp.GetArrayLength() > 0)
            {
                var s0 = sp[0];
                if (s0.TryGetProperty("Price", out var spr) && spr.ValueKind == System.Text.Json.JsonValueKind.Number)
                    info.SubscriberCost = spr.GetInt32();
                if (s0.TryGetProperty("CurrencyType", out var scc) && scc.ValueKind == System.Text.Json.JsonValueKind.Number)
                    info.SubscriberCurrency = scc.GetInt32();
            }

            if (it.TryGetProperty("GiftDrop", out var gd))
            {
                string S(string k) => gd.TryGetProperty(k, out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                int I(string k) => gd.TryGetProperty(k, out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0;

                info.Reward = S("AvatarItemDesc");
                info.Name = S("FriendlyName");
                info.Rarity = I("Rarity");
                info.GiftDropId = I("GiftDropId");
                info.ConsumableItemDesc = S("ConsumableItemDesc");
                info.ItemCount = Math.Max(1, I("ItemCount"));
                info.AvatarItemType = I("AvatarItemType");
                info.GdCurrencyType = I("CurrencyType");
                info.GdCurrency = I("Currency");
                info.EquipmentPrefabName = S("EquipmentPrefabName");
                // UUID form, always - the client's equipment catalog is UUIDs and a
                // short-guid box could not resolve its skin (see PlayerDB.EquipmentSkinKey).
                info.EquipmentModificationGuid = Classes.ItemGuids.NormalizeGuid(S("EquipmentModificationGuid")) ?? S("EquipmentModificationGuid");
                info.CustomAvatarItemId = S("CustomAvatarItemId");
                info.ThumbnailImageName = S("ThumbnailImageName");
                info.EquipmentItemId = I("EquipmentItemId");
                info.AvatarItemId = I("AvatarItemId");
            }

            info.StoreFile = storeFile;
            info.PurchasableItemId = purchasableItemId;
            info.StorefrontType = storefront;

            // Hair dye is free (ConfigCampus.FreeHairDyes). Zeroed HERE, on the one
            // object every purchase path prices from - buyItem and bulkpurchase both
            // take their cost off this - so the store page and the till can never
            // disagree about what a colour costs.
            if (HairGrants.IsHairDye(info.Name))
            {
                info.Cost = 0;
                info.SubscriberCost = 0;
            }
            return info;
        }

        /// <summary>An admin-edited row (StoreEdits) as the purchase paths price it.</summary>
        private static GiftDropInfo ParseRow(Newtonsoft.Json.Linq.JObject row, int purchasableItemId, int storefront)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(row.ToString(Newtonsoft.Json.Formatting.None));
            return ParseRow(doc.RootElement, purchasableItemId, "store-edits", storefront);
        }

        // The storefront id is the file's basename ("100.json" -> 100).
        private static int StorefrontTypeFromFile(string file)
            => int.TryParse(Path.GetFileNameWithoutExtension(file), out var t) ? t : -1;

        /// <summary>
        /// Search every storefront for items whose FriendlyName or descriptor contains the query,
        /// newest storefronts first, capped at <paramref name="max"/>. Used by the admin gift page
        /// to pick an item to give. Returns fully-resolved GiftDropInfo (with PurchasableItemId /
        /// StorefrontType) so the giver can re-look-up and hand out the exact box content.
        /// </summary>
        public static List<GiftDropInfo> SearchItems(string query, int max = 40)
        {
            var results = new List<GiftDropInfo>();
            if (!Directory.Exists(StoreDirectory) || string.IsNullOrWhiteSpace(query)) return results;
            string q = query.Trim().ToLowerInvariant();

            foreach (var file in Directory.GetFiles(StoreDirectory, "*.json"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("StoreItems", out var items)) continue;
                    int storefront = StorefrontTypeFromFile(file);

                    foreach (var it in items.EnumerateArray())
                    {
                        if (!it.TryGetProperty("PurchasableItemId", out var pid)
                            || pid.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                        if (!it.TryGetProperty("GiftDrop", out var gd)) continue;

                        string S(string k) => gd.TryGetProperty(k, out var v)
                            && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                        int I(string k) => gd.TryGetProperty(k, out var v)
                            && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0;

                        string name = S("FriendlyName");
                        string desc = S("AvatarItemDesc");
                        if (!name.ToLowerInvariant().Contains(q) && !desc.ToLowerInvariant().Contains(q))
                            continue;

                        results.Add(new GiftDropInfo
                        {
                            PurchasableItemId = pid.GetInt32(),
                            StorefrontType = storefront,
                            Name = name,
                            Reward = desc,
                            Rarity = I("Rarity"),
                            GiftDropId = I("GiftDropId"),
                            ConsumableItemDesc = S("ConsumableItemDesc"),
                            ItemCount = Math.Max(1, I("ItemCount")),
                            AvatarItemType = I("AvatarItemType"),
                            EquipmentPrefabName = S("EquipmentPrefabName"),
                            EquipmentModificationGuid = Classes.ItemGuids.NormalizeGuid(S("EquipmentModificationGuid")) ?? S("EquipmentModificationGuid"),
                            CustomAvatarItemId = S("CustomAvatarItemId"),
                            ThumbnailImageName = S("ThumbnailImageName"),
                            StoreFile = Path.GetFileName(file)
                        });

                        if (results.Count >= max) return results;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[store] search could not read {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            return results;
        }

        // The RR+ discount the CLIENT applies. Measured by the 2023 server from what the client
        // sends as RequestedPrice: 3000 -> 2700, 2500 -> 2250, 1500 -> 1350 - all exactly 10%.
        //
        // It is NOT the store file's SubscriberDiscountPercent: the Watch store sets that to 0
        // and its SubscriberPrices equal the full price, yet the client still asks to pay 10%
        // less. The rate lives in the client, and 10 is the observed value.
        public const int RRPlusDiscountPercent = 10;

        /// <summary>
        /// What an RR+ member actually pays: the better of the store's explicit SubscriberPrice
        /// and the flat RR+ discount. Never more than the base price.
        /// Everyone on this server is a subscriber - UpdateAndGetSubscription always reports active.
        /// </summary>
        public static int SubscriberPriceFor(GiftDropInfo item)
        {
            int best = item.Cost;

            if (item.SubscriberCost >= 0
                && (item.SubscriberCurrency < 0 || item.SubscriberCurrency == item.Currency)
                && item.SubscriberCost < best)
                best = item.SubscriberCost;

            int pct = (int)Math.Floor(item.Cost * (100 - RRPlusDiscountPercent) / 100.0);
            if (pct < best) best = pct;

            return Math.Max(0, best);
        }

        // Gift id for the purchase-reveal payload and the stored box. Was a process counter from
        // 5000 that restarted on every deploy, so every player's first purchase after a restart
        // was box 5001 - the collision PlayerDB.NextGiftPackageId explains. int32, see
        // PendingGiftBox.NumericId.
        public static int NextPurchaseGiftId() => Campus_live_rewrite.api.dbs.PlayerDB.NextGiftPackageId();

        /// <summary>Only items sold for Rec Center Tokens can be gifted (never a room-currency / ticket item).</summary>
        public static bool PricedInTokens(Newtonsoft.Json.Linq.JObject row)
        {
            var prices = row["Prices"] as Newtonsoft.Json.Linq.JArray;
            if (prices == null || prices.Count == 0) return false;
            return prices.Any(p => ((int?)p["CurrencyType"] ?? 0) == (int)Classes.CurrencyType.RecCenterTokens);
        }

        // A file storefront with CanBeGifted set on every row (the captures never carried it, so
        // the store's gift button never showed). Cached per file + mtime.
        private static readonly Dictionary<string, (long ticks, string json)> _giftable = new();
        public static string? StoreJsonGiftable(string path, string? baseJson = null)
        {
            try
            {
                long ticks = File.GetLastWriteTimeUtc(path).Ticks;
                string key = path + (baseJson != null ? "#free" : "");
                lock (_giftable)
                {
                    if (_giftable.TryGetValue(key, out var hit) && hit.ticks == ticks) return hit.json;
                }
                var root = Newtonsoft.Json.Linq.JObject.Parse(baseJson ?? File.ReadAllText(path));
                foreach (var it in root["StoreItems"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                    if (it is Newtonsoft.Json.Linq.JObject o) { StoreGenerator.ShapeRow(o); o["CanBeGifted"] = PricedInTokens(o); }
                string json = root.ToString(Newtonsoft.Json.Formatting.None);
                lock (_giftable) _giftable[key] = (ticks, json);
                return json;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[store] could not mark {Path.GetFileName(path)} giftable: {ex.Message}");
                return null;
            }
        }
    }
}
