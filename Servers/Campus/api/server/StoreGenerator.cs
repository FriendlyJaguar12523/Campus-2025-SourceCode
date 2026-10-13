using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The Watch store (storefront 3) as the 2025 client expects it - ported from the reference
    /// server's generated `sf3-2025.json` (econ/src/catalog-load.ts + catalogStoreItems).
    ///
    /// The reference serves builds after 20230414 a MERGED store: the captured catalog's rows
    /// that have no avatar item behind them (consumables, the random boxes) plus ONE GENERATED
    /// ROW PER SELLABLE AVATAR ITEM in the item catalog, numbered from 10000 upward so nothing
    /// can collide with a captured PurchasableItemId (every real one is 2764 or below).
    ///
    /// Why it matters here, and why the store looked broken without it:
    ///
    ///   * The store pages are algorithmic-list rows that answer BARE PurchasableItemIds, and the
    ///     client resolves each one against the storefront it loaded. Our 3.json was a 1161-row
    ///     2023 capture; the reference's is 2950 rows in a different id space. Any row that named
    ///     an id our file didn't hold rendered nothing - and an id our file held under a
    ///     DIFFERENT item showed that item when clicked. Generating from the catalog makes the
    ///     ids and the storefront one thing, so a click shows what was clicked.
    ///   * Only 1161 of ~3300 sellable items were for sale at all.
    ///   * Prices: a generated row is priced by rarity from ONE table (PRICE_BY_RARITY), which is
    ///     the same table the purchase check uses - the reference's point being that a store which
    ///     prices a buy differently from the file it listed refuses every purchase as "Price has
    ///     changed".
    ///
    /// One deliberate difference from the reference: captured avatar-item rows whose descriptor is
    /// NOT in avatar_items.json are KEPT rather than dropped. That is the 19 permanent hair dyes,
    /// which are what unlock hair colours (see HairGrants) - the reference loses them.
    ///
    /// Ids are assigned by catalog ORDER, so they are stable across restarts for a given
    /// avatar_items.json and change only if the catalog itself is re-ordered - the same
    /// load-order surrogate the reference uses. Nothing may persist one.
    /// </summary>
    public static class StoreGenerator
    {
        public const int WatchStorefront = 3;

        /// <summary>First generated id. Above every captured PurchasableItemId (the real Watch store
        /// runs to 33691 - ImportRealStore.py; the old synthetic file topped out at 2764).</summary>
        public const int CatalogIdBase = 100_000;

        /// <summary>The developer / unreleased tier. In the catalog, never in a store.</summary>
        public static bool IsSellableRarity(int rarity) => rarity != -1;

        // Reference PRICE_BY_RARITY: 0/10/30/50 were specified; 20 sits between its neighbours.
        // A rarity nobody priced costs the floor rather than vanishing - an unpriced item
        // silently missing from the store is harder to notice than one that turns up cheap.
        private static readonly Dictionary<int, int> PriceByRarity = new()
        {
            [0] = 500, [10] = 600, [20] = 700, [30] = 800, [50] = 3000   // clothing never under 500 (owner rule)
        };
        public const int DefaultPrice = 500;
        public static int PriceForRarity(int rarity) => PriceByRarity.TryGetValue(rarity, out var p) ? p : DefaultPrice;

        /// <summary>What Rec Room Plus pays: 10% off, floored - the same band StoreCatalog.SubscriberPriceFor allows.</summary>
        public static int SubscriberPriceFor(int price) => (int)Math.Floor(price * (100 - StoreCatalog.RRPlusDiscountPercent) / 100.0);

        private static readonly object _lock = new();
        private static JObject? _merged;
        private static Dictionary<int, StoreCatalog.GiftDropInfo>? _generated;
        // The downloadable twin a captured row NAMED (AvatarItemInfo.DownloadableAvatarItemId),
        // by PurchasableItemId - kept only so OutfitTypeOf can still read the twin's wardrobe slot
        // after ShapeRow has taken AvatarItemInfo off the wire (see ShapeRow for why it must go).
        private static Dictionary<int, string> _twinByPurchasable = new();
        private static string _stamp = "";

        private static string StorePath => Path.Combine(StoreCatalog.StoreDirectory, $"{WatchStorefront}.json");
        private static string CatalogPath => Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "avatar_items.json");
        private static string EquipmentPath => Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "Equipments.json");

        /// <summary>First generated SKIN id - its own range above the avatar-item rows.</summary>
        public const int SkinIdBase = 140_000;

        /// <summary>PurchasableItemIds of every skin row in the Watch store (captured + generated), for the Skins carousel.</summary>
        public static List<int> SkinPurchasableIds()
        {
            var store = MergedWatchStore()["StoreItems"] as JArray ?? new JArray();
            return store.Where(it => !string.IsNullOrEmpty((string?)it["GiftDrop"]?["EquipmentPrefabName"]))
                        .Select(it => (int?)it["PurchasableItemId"] ?? 0).Where(i => i > 0).ToList();
        }

        private static string Stamp()
        {
            static string T(string p) => File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks.ToString() : "-";
            string shown = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "ShownItems.json");
            string defaults = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "DefaultAvatarItems.json");
            return T(StorePath) + "|" + T(CatalogPath) + "|" + T(EquipmentPath) + "|" + (ConfigCampus.HiddenItemsStorefronts ? "H" + T(shown) + T(defaults) : "-");
        }

        /// <summary>The merged Watch store, rebuilt when either source file changes on disk.</summary>
        public static JObject MergedWatchStore()
        {
            string stamp = Stamp();
            if (_merged != null && stamp == _stamp) return _merged;

            lock (_lock)
            {
                if (_merged != null && stamp == _stamp) return _merged;
                Build();
                _stamp = stamp;
                return _merged!;
            }
        }

        public static string MergedWatchStoreJson() => MergedWatchStore().ToString(Newtonsoft.Json.Formatting.None);

        /// <summary>A generated row by its PurchasableItemId (10000+), or null. What buyItem resolves against.</summary>
        public static StoreCatalog.GiftDropInfo? FindGenerated(int purchasableItemId)
        {
            if (purchasableItemId < CatalogIdBase) return null;
            MergedWatchStore();
            return _generated != null && _generated.TryGetValue(purchasableItemId, out var info) ? info : null;
        }

        /// <summary>
        /// Bring a captured row up to the wire's rules whatever capture it came from: AvatarItemType
        /// is NULL unless the row sells an avatar item (a skin / consumable / box with 0 there is
        /// opened as an avatar item with nothing to load), and the modern keys exist.
        ///
        /// AvatarItemInfo is ALWAYS null on the wire. A real capture (Watch_EnumValue_3.json) carries
        /// { LegacyAvatarItemDesc, DownloadableAvatarItemId } on 2,300+ legacy rows, and that twin id
        /// is what makes the 20250718 client fetch the item's CAI assetbundle from this server
        /// instead of drawing the legacy item from its own built-in db - and every such download
        /// dies inside the client's bundle loader (RemoteProviderException / NullReferenceException
        /// in CBACIMLIBPF.PNKGBHGAHHH, identical bytes and headers to the reference CDN), so the
        /// item comes up "Can't load item". The reference server never emits AvatarItemInfo at all:
        /// its 2025 store (sf3-2025.json, `runx storefront build`) is generated from the legacy item
        /// catalog with AvatarItemDesc only, and its legacy-saves endpoint answers an empty map -
        /// the client renders every store item from what it shipped with. Same here.
        /// </summary>
        public static void ShapeRow(JObject row)
        {
            if (row["GiftDrop"] is not JObject gd) return;
            bool avatar = !string.IsNullOrWhiteSpace((string?)gd["AvatarItemDesc"]) || !string.IsNullOrWhiteSpace((string?)gd["CustomAvatarItemId"]);
            if (!avatar) gd["AvatarItemType"] = null;
            else if (gd["AvatarItemType"] == null || gd["AvatarItemType"]!.Type == JTokenType.Null) gd["AvatarItemType"] = 0;
            gd["AvatarItemInfo"] = null;
            gd["CustomAvatarItemId"] ??= null;
            gd["ThumbnailImageName"] ??= null;
            gd["TagList"] ??= null;
            if (gd["AvatarItemId"] == null) gd["AvatarItemId"] = 0;
            if (gd["EquipmentItemId"] == null) gd["EquipmentItemId"] = 0;
            if (gd["ItemCount"] == null) gd["ItemCount"] = 1;
            if (row["OnlyAvailableThroughCv2"] == null) row["OnlyAvailableThroughCv2"] = false;
        }

        /// <summary>
        /// The wardrobe slot (RecRoom.Avatars.OutfitType) a Watch row sells into, or -1 when the
        /// catalog does not record it. A custom item records its OutfitType; a legacy row that
        /// carries AvatarItemInfo names its downloadable twin, which records it too. This is what
        /// the store's category carousels sort by (APIController.GetAlgorithmicList); rows without
        /// a slot fall back to the name needles.
        /// </summary>
        public static int OutfitTypeOf(JToken row)
        {
            var gd = row["GiftDrop"];
            if (gd == null) return -1;
            string? caid = (string?)gd["CustomAvatarItemId"];
            if (string.IsNullOrWhiteSpace(caid) && gd["AvatarItemInfo"] is JObject info) caid = (string?)info["DownloadableAvatarItemId"];
            if (string.IsNullOrWhiteSpace(caid) && (int?)row["PurchasableItemId"] is int pid)
            {
                MergedWatchStore();
                _twinByPurchasable.TryGetValue(pid, out caid);
            }
            if (string.IsNullOrWhiteSpace(caid)) return -1;
            return CustomItemsCache.GetRec(caid)?.OutfitType ?? -1;
        }

        private static void Build()
        {
            var root = new JObject
            {
                ["NewUntil"] = null,
                ["NextUpdate"] = "2222-02-22T22:22:00Z",
                ["StoreItems"] = new JArray(),
                ["StorefrontName"] = null,
                ["StorefrontType"] = WatchStorefront,
                ["SubscriberDiscountPercent"] = StoreCatalog.RRPlusDiscountPercent
            };
            var items = (JArray)root["StoreItems"]!;
            var generated = new Dictionary<int, StoreCatalog.GiftDropInfo>();
            var twins = new Dictionary<int, string>();

            try
            {
                // ---- the catalog: every avatar item, in file order ------------------------------
                var catalog = File.Exists(CatalogPath) ? JArray.Parse(File.ReadAllText(CatalogPath)) : new JArray();
                var inCatalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in catalog)
                {
                    string k = dbs.PlayerDB.NormalizeAvatarItemDesc((string?)e["AvatarItemDesc"]);
                    if (k.Length > 0) inCatalog.Add(k);
                }

                // ---- captured rows worth keeping -----------------------------------------------
                // Kept: anything with no avatar item behind it (consumables, IsQuery random boxes),
                // and an avatar item the catalog does not know (the hair dyes). Dropped: an avatar
                // item the catalog DOES know - it is regenerated below under a catalog id, so the
                // same item cannot be listed twice under two ids at two prices.
                int kept = 0, dropped = 0, hidden = 0;
                var capturedSkins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Descriptors the captured store already sells. A REAL capture (ImportRealStore.py)
                // carries the real PurchasableItemId, price, thumbnail and AvatarItemInfo for an
                // item, which beats a row generated from the catalog - so those rows are KEPT and
                // the catalog only fills in what the capture does not sell. (The old synthetic
                // 3.json was the other way round: its rows were dropped and regenerated.)
                var capturedDescs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(StorePath))
                {
                    var captured = JObject.Parse(File.ReadAllText(StorePath));
                    if (root["NewUntil"] != null) root["NewUntil"] = captured["NewUntil"];
                    foreach (var it in captured["StoreItems"] as JArray ?? new JArray())
                    {
                        string rawDesc = (string?)it["GiftDrop"]?["AvatarItemDesc"];
                        string desc = dbs.PlayerDB.NormalizeAvatarItemDesc(rawDesc);
                        if (desc.Length > 0 && inCatalog.Contains(desc))
                        {
                            if (!ConfigCampus.PreferCapturedStoreRows || !capturedDescs.Add(desc)) { dropped++; continue; }
                        }
                        if (!Classes.ItemGuids.StoreCanList(rawDesc)) { hidden++; continue; }
                        // A custom-item row is art the client fetches from this server too - the
                        // same TEMPORARY gate as the descriptor rows (ConfigCampus.HiddenItemsStorefronts).
                        if (ConfigCampus.HiddenItemsStorefronts && !string.IsNullOrWhiteSpace((string?)it["GiftDrop"]?["CustomAvatarItemId"])) { hidden++; continue; }
                        var row = (JObject)it.DeepClone();
                        // Remember the twin BEFORE ShapeRow drops it from the wire (OutfitTypeOf).
                        if (it["GiftDrop"]?["AvatarItemInfo"] is JObject twinInfo && (int?)it["PurchasableItemId"] is int twinPid
                            && !string.IsNullOrWhiteSpace((string?)twinInfo["DownloadableAvatarItemId"]))
                            twins[twinPid] = (string)twinInfo["DownloadableAvatarItemId"]!;
                        ShapeRow(row);
                        // Skin rows: spell the guid as the UUID Equipments.json uses, so the client's
                        // owned check and the purchase both resolve the same skin.
                        if (row["GiftDrop"] is JObject gdRow)
                        {
                            string? eg = (string?)gdRow["EquipmentModificationGuid"];
                            if (!string.IsNullOrWhiteSpace(eg))
                            {
                                gdRow["EquipmentModificationGuid"] = Classes.ItemGuids.NormalizeGuid(eg) ?? eg;
                                capturedSkins.Add(((string?)gdRow["EquipmentModificationGuid"] ?? "").ToLowerInvariant());
                            }
                        }
                        // Token-priced rows can be sent as a gift (the store's gift button reads
                        // this; the level gate is at the till - ConfigCampus.GiftMinLevel).
                        row["CanBeGifted"] = StoreCatalog.PricedInTokens(row);
                        items.Add(row);
                        kept++;
                    }
                }

                // ---- one generated row per sellable catalog item ---------------------------------
                // Numbered by catalog POSITION (base + index over every row, sellable or not) so an
                // item's id does not shift when a neighbour's rarity changes.
                int made = 0;
                for (int i = 0; i < catalog.Count; i++)
                {
                    var e = catalog[i];
                    string desc = (string?)e["AvatarItemDesc"] ?? "";
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    int rarity = (int?)e["Rarity"] ?? 0;
                    if (!IsSellableRarity(rarity)) continue;
                    if (capturedDescs.Contains(dbs.PlayerDB.NormalizeAvatarItemDesc(desc))) continue;   // the capture sells it
                    if (!Classes.ItemGuids.StoreCanList(desc)) { hidden++; continue; }   // HiddenItemsStorefronts

                    int id = CatalogIdBase + i;
                    int price = PriceForRarity(rarity);
                    int sub = SubscriberPriceFor(price);
                    string name = (string?)e["FriendlyName"] ?? "";
                    int type = (int?)e["AvatarItemType"] ?? 0;

                    items.Add(new JObject
                    {
                        ["GiftDrop"] = new JObject
                        {
                            ["AvatarItemDesc"] = desc,               // RAW - the client parses this
                            ["AvatarItemType"] = type,
                            ["AvatarItemId"] = (int?)e["AvatarItemId"] ?? 0,
                            ["AvatarItemInfo"] = null,               // legacy path: the client's own item db
                            ["ConsumableItemDesc"] = "",
                            ["Context"] = 0,
                            ["Currency"] = 0,
                            ["CurrencyType"] = 0,
                            ["CustomAvatarItemId"] = null,
                            ["EquipmentItemId"] = 0,
                            ["EquipmentModificationGuid"] = "",
                            ["EquipmentPrefabName"] = "",
                            ["FriendlyName"] = name,
                            ["GiftDropId"] = id,
                            ["IsQuery"] = false,
                            ["ItemCount"] = 1,
                            ["ItemSetFriendlyName"] = "",
                            ["ItemSetId"] = null,
                            ["QueryRedirectContext"] = null,
                            ["QueryRedirectRarity"] = null,
                            ["QueryRedirectTag"] = null,
                            ["Rarity"] = rarity,
                            ["SubscribersOnly"] = false,
                            ["TagList"] = null,
                            ["ThumbnailImageName"] = (string?)e["ThumbnailImage"],
                            ["Tooltip"] = (string?)e["Tooltip"] ?? "",
                            ["Unique"] = false
                        },
                        ["IsFeatured"] = false,
                        ["CanBeGifted"] = true,
                        ["Prices"] = new JArray { new JObject { ["CurrencyType"] = (int)Classes.CurrencyType.RecCenterTokens, ["Price"] = price, ["StorefrontSaleData"] = null, ["Type"] = 0 } },
                        ["PurchasableItemId"] = id,
                        ["SubscriberPrices"] = new JArray { new JObject { ["CurrencyType"] = (int)Classes.CurrencyType.RecCenterTokens, ["Price"] = sub, ["StorefrontSaleData"] = null, ["Type"] = 0 } },
                        ["Type"] = 0
                    });

                    generated[id] = new StoreCatalog.GiftDropInfo
                    {
                        Cost = price,
                        Currency = (int)Classes.CurrencyType.RecCenterTokens,
                        SubscriberCost = sub,
                        SubscriberCurrency = (int)Classes.CurrencyType.RecCenterTokens,
                        Reward = desc,
                        Name = name,
                        Rarity = rarity,
                        GiftDropId = id,
                        ConsumableItemDesc = "",
                        AvatarItemType = type,
                        GdCurrencyType = 0,
                        GdCurrency = 0,
                        EquipmentPrefabName = "",
                        EquipmentModificationGuid = "",
                        ThumbnailImageName = (string?)e["ThumbnailImage"] ?? "",
                        AvatarItemId = (int?)e["AvatarItemId"] ?? 0,
                        StoreFile = "generated",
                        PurchasableItemId = id,
                        StorefrontType = WatchStorefront
                    };
                    made++;
                }

                // ---- one generated row per equipment SKIN the captured store does not sell ------
                // Equipments.json is the whole skin inventory (340); the captured Watch store only
                // sells 187 of them. The rest are listed here under SkinIdBase + index, priced by
                // rarity like the avatar items, so the Skins carousel and the locker agree.
                int skins = 0;
                if (File.Exists(EquipmentPath))
                {
                    var equip = JArray.Parse(File.ReadAllText(EquipmentPath));
                    for (int i = 0; i < equip.Count; i++)
                    {
                        var e = equip[i];
                        string prefab = (string?)e["PrefabName"] ?? "";
                        string guid = Classes.ItemGuids.NormalizeGuid((string?)e["ModificationGuid"]) ?? ((string?)e["ModificationGuid"] ?? "");
                        if (prefab.Length == 0 || guid.Length == 0) continue;
                        if (capturedSkins.Contains(guid)) continue;
                        int rarity = (int?)e["Rarity"] ?? 0;
                        if (!IsSellableRarity(rarity)) continue;
                        string name = (string?)e["FriendlyName"] ?? prefab;
                        int id = SkinIdBase + i;
                        int price = PriceForRarity(rarity);
                        int sub = SubscriberPriceFor(price);

                        items.Add(new JObject
                        {
                            ["GiftDrop"] = new JObject
                            {
                                ["AvatarItemDesc"] = "",
                                ["AvatarItemType"] = null,           // a skin is NOT an avatar item (see StoreCatalog.AvatarTypeFor)
                                ["AvatarItemId"] = 0,
                                ["AvatarItemInfo"] = null,
                                ["ConsumableItemDesc"] = "",
                                ["Context"] = 0,
                                ["Currency"] = 0,
                                ["CurrencyType"] = 0,
                                ["CustomAvatarItemId"] = null,
                                ["EquipmentItemId"] = 0,
                                ["EquipmentModificationGuid"] = guid,
                                ["EquipmentPrefabName"] = prefab,
                                ["FriendlyName"] = name,
                                ["GiftDropId"] = id,
                                ["IsQuery"] = false,
                                ["ItemCount"] = 1,
                                ["ItemSetFriendlyName"] = "",
                                ["ItemSetId"] = null,
                                ["QueryRedirectContext"] = null,
                                ["QueryRedirectRarity"] = null,
                                ["QueryRedirectTag"] = null,
                                ["Rarity"] = rarity,
                                ["SubscribersOnly"] = false,
                                ["TagList"] = "",
                                ["ThumbnailImageName"] = null,
                                ["Tooltip"] = (string?)e["Tooltip"] ?? "",
                                ["Unique"] = false
                            },
                            ["IsFeatured"] = false,
                            ["CanBeGifted"] = true,
                            ["Prices"] = new JArray { new JObject { ["CurrencyType"] = (int)Classes.CurrencyType.RecCenterTokens, ["Price"] = price, ["StorefrontSaleData"] = null, ["Type"] = 0 } },
                            ["PurchasableItemId"] = id,
                            ["SubscriberPrices"] = new JArray { new JObject { ["CurrencyType"] = (int)Classes.CurrencyType.RecCenterTokens, ["Price"] = sub, ["StorefrontSaleData"] = null, ["Type"] = 0 } },
                            ["Type"] = 0
                        });
                        generated[id] = new StoreCatalog.GiftDropInfo
                        {
                            Cost = price,
                            Currency = (int)Classes.CurrencyType.RecCenterTokens,
                            SubscriberCost = sub,
                            SubscriberCurrency = (int)Classes.CurrencyType.RecCenterTokens,
                            Reward = "",
                            Name = name,
                            Rarity = rarity,
                            GiftDropId = id,
                            ConsumableItemDesc = "",
                            AvatarItemType = 0,
                            GdCurrencyType = 0,
                            GdCurrency = 0,
                            EquipmentPrefabName = prefab,
                            EquipmentModificationGuid = guid,
                            StoreFile = "generated-skin",
                            PurchasableItemId = id,
                            StorefrontType = WatchStorefront
                        };
                        skins++;
                    }
                }

                Console.WriteLine($"[store] Watch store: {kept} captured row(s) kept, {dropped} replaced by catalog rows, {made} generated from the catalog, {skins} skin(s) from Equipments.json" +
                                  (hidden > 0 ? $", {hidden} hidden (HiddenItemsStorefronts)" : "") + $" -> {items.Count} for sale");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[store] could not generate the Watch store ({ex.Message}) - serving the captured file as-is");
                try
                {
                    if (File.Exists(StorePath))
                    {
                        root = JObject.Parse(File.ReadAllText(StorePath));
                        generated.Clear();
                    }
                }
                catch { }
            }

            // Hair dye is free (HairGrants) - applied to the merged object so the badge and the
            // till agree here exactly as they do for the other store files.
            // LOCAL EXPERIMENT (HoldNewAccountCreation): the client's starter avatar wears two custom items we have no record of and logs
            // "[Economy.Store] Can't get purchase info for item <id>" for each - the exception that follows stops the creation screen's
            // clothing tabs and its DONE button. A free store row each gives the Store something to answer with.
            if (ConfigCampus.HoldNewAccountCreation)
            {
                int pid = 9900001;
                foreach (var cid in Campus_live_rewrite.api.Controllers.APIController.StarterCustomItemIds)
                {
                    items.Add(new JObject
                    {
                        ["GiftDrop"] = new JObject
                        {
                            ["GiftDropId"] = pid, ["FriendlyName"] = "(Starter item)", ["Tooltip"] = "", ["TagList"] = "", ["ConsumableItemDesc"] = "",
                            ["AvatarItemDesc"] = "", ["CustomAvatarItemId"] = cid, ["AvatarItemType"] = 0, ["EquipmentPrefabName"] = "", ["EquipmentModificationGuid"] = "",
                            ["IsQuery"] = false, ["QueryRedirectContext"] = null, ["QueryRedirectTag"] = null, ["QueryRedirectRarity"] = null, ["Unique"] = false,
                            ["SubscribersOnly"] = false, ["Rarity"] = 0, ["Context"] = 1003, ["Currency"] = 0, ["CurrencyType"] = 0, ["ItemCount"] = 1, ["ItemSetId"] = null,
                            ["ItemSetFriendlyName"] = "", ["AvatarItemId"] = 0, ["EquipmentItemId"] = 0, ["ThumbnailImageName"] = null, ["AvatarItemInfo"] = null
                        },
                        ["PurchasableItemId"] = pid, ["Type"] = 0,
                        ["Prices"] = new JArray(new JObject { ["CurrencyType"] = 2, ["Price"] = 0, ["StorefrontSaleData"] = null }),
                        ["SubscriberPrices"] = new JArray(new JObject { ["CurrencyType"] = 2, ["Price"] = 0, ["StorefrontSaleData"] = null }),
                        ["IsFeatured"] = false, ["NewUntil"] = "2020-01-01T00:00:00Z", ["AvailableAt"] = "2020-01-01T00:00:00Z", ["AvailableUntil"] = null,
                        ["CanBeGifted"] = false, ["OnlyAvailableThroughCv2"] = false
                    });
                    pid++;
                }
                Console.WriteLine("[store] local experiment: 2 free starter-item rows added for the creation screen");
            }

            HairGrants.ZeroDyePrices(root);

            _merged = root;
            _generated = generated;
            _twinByPurchasable = twins;
        }
    }
}
