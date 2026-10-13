using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Admin panel Stores tab: pick a storefront, see its items grouped by set (GiftDrop.ItemSetId
    /// / ItemSetFriendlyName - the sets the in-game store shows), add any item, change prices,
    /// sets and the featured flag, take items off sale. Every change is an overlay in
    /// Data/APIS/store-edits.json (StoreEdits) - the store files are never written.
    /// </summary>
    public class AdminStoresController : Controller
    {
        private ContentResult J(object o, int status = 200) => new ContentResult
        {
            Content = Newtonsoft.Json.JsonConvert.SerializeObject(o),
            ContentType = "application/json",
            StatusCode = status
        };

        // Storefronts whose contents the server builds per player/room - not editable stock.
        private static readonly HashSet<int> NotStock = new()
        {
            (int)StorefrontType.None, (int)StorefrontType.Wishlist, (int)StorefrontType.RoomKeys,
            (int)StorefrontType.Player_Profile, (int)StorefrontType.Room_Save, (int)StorefrontType.RoomCurrencies,
            (int)StorefrontType.RoomConsumables, (int)StorefrontType.CustomAvatarItems,
            (int)StorefrontType.ProgressionEventPurchasableXpBoosts, (int)StorefrontType.RoomOffers,
        };

        /// <summary>The store's own rows before edits (the Watch store as generated). Do not mutate the Watch rows.</summary>
        private static JArray BaseRows(int store)
        {
            try
            {
                // Minus the items no store sells (StoreServe.Excluded), so the tab lists what
                // players actually see.
                if (store == StoreGenerator.WatchStorefront)
                    return new JArray((StoreGenerator.MergedWatchStore()["StoreItems"] as JArray ?? new JArray()).Where(r => !StoreServe.Excluded(r)));
                var f = StoreCatalog.StoreFileForId(store);
                if (f.Length == 0) return new JArray();
                var arr = JObject.Parse(System.IO.File.ReadAllText(f))["StoreItems"] as JArray ?? new JArray();
                foreach (var r in arr.OfType<JObject>()) StoreGenerator.ShapeRow(r);
                return new JArray(arr.Where(r => !StoreServe.Excluded(r)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[admin-stores] could not read store {store}: {ex.Message}");
                return new JArray();
            }
        }

        private static int Pid(JToken r) => (int?)r["PurchasableItemId"] ?? 0;

        /// <summary>The row as the store now sells it (edit first, else base), cloned; null if neither.</summary>
        private static JObject? Effective(int store, int pid, JArray? baseRows = null)
        {
            var e = StoreEdits.RowFor(store, pid);
            if (e != null) return e;
            var b = (baseRows ?? BaseRows(store)).FirstOrDefault(r => Pid(r) == pid);
            return b?.DeepClone() as JObject;
        }

        private static string Kind(JObject gd)
        {
            if ((bool?)gd["IsQuery"] == true) return "random box";
            if (!string.IsNullOrWhiteSpace((string?)gd["CustomAvatarItemId"])) return "custom item";
            if (!string.IsNullOrWhiteSpace((string?)gd["AvatarItemDesc"])) return "avatar item";
            if (!string.IsNullOrWhiteSpace((string?)gd["EquipmentModificationGuid"])) return "skin";
            if (!string.IsNullOrWhiteSpace((string?)gd["ConsumableItemDesc"])) return "consumable";
            return "other";
        }

        private static object Dto(JObject r, string state)
        {
            var gd = r["GiftDrop"] as JObject ?? new JObject();
            var p0 = (r["Prices"] as JArray)?.FirstOrDefault();
            var s0 = (r["SubscriberPrices"] as JArray)?.FirstOrDefault();
            return new
            {
                pid = Pid(r),
                giftDropId = (int?)gd["GiftDropId"] ?? 0,
                name = (string?)gd["FriendlyName"] ?? "",
                kind = Kind(gd),
                desc = (string?)gd["AvatarItemDesc"] ?? "",
                rarity = (int?)gd["Rarity"] ?? 0,
                price = (int?)p0?["Price"] ?? 0,
                currency = (int?)p0?["CurrencyType"] ?? 0,
                subPrice = (int?)s0?["Price"],
                setId = gd["ItemSetId"]?.Type == JTokenType.Integer ? (int?)gd["ItemSetId"] : null,
                setName = (string?)gd["ItemSetFriendlyName"] ?? "",
                featured = (bool?)r["IsFeatured"] ?? false,
                thumb = (string?)gd["ThumbnailImageName"] ?? "",
                state
            };
        }

        private static string StoreName(int id) => Enum.IsDefined(typeof(StorefrontType), id) ? ((StorefrontType)id).ToString() : $"Store {id}";

        private static int MainCurrency(JArray rows)
        {
            var c = rows.Select(r => (int?)(r["Prices"] as JArray)?.FirstOrDefault()?["CurrencyType"] ?? 0).Where(x => x > 0)
                        .GroupBy(x => x).OrderByDescending(g => g.Count()).FirstOrDefault();
            return c?.Key ?? (int)CurrencyType.RecCenterTokens;
        }

        private async Task<JObject> Body()
        {
            using var r = new StreamReader(Request.Body);
            var s = await r.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(s) ? new JObject() : JObject.Parse(s);
        }

        private static void SetPrices(JObject row, int price, int currency, int? subPrice)
        {
            int sub = subPrice ?? StoreGenerator.SubscriberPriceFor(price);
            row["Prices"] = new JArray { new JObject { ["CurrencyType"] = currency, ["Price"] = price, ["StorefrontSaleData"] = null } };
            row["SubscriberPrices"] = new JArray { new JObject { ["CurrencyType"] = currency, ["Price"] = sub, ["StorefrontSaleData"] = null } };
            // When the price was set: the latest price set for an item, in any store, is what every
            // store sells it for (StorePrices). Stripped before the row reaches the client.
            row[StorePrices.PriceSetAtKey] = DateTime.UtcNow.ToString("o");
        }

        private static void SetSet(JObject row, int? setId, string? setName)
        {
            var gd = (JObject)(row["GiftDrop"] ??= new JObject());
            gd["ItemSetId"] = setId.HasValue ? setId.Value : JValue.CreateNull();
            gd["ItemSetFriendlyName"] = setId.HasValue ? (setName ?? (string?)gd["ItemSetFriendlyName"] ?? "") : "";
        }

        // ---- reads ----------------------------------------------------------------------------

        [HttpGet("/admin/api/stores")]
        public ContentResult List()
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var stores = new List<object>();
            foreach (int id in Enum.GetValues(typeof(StorefrontType)).Cast<int>().Distinct().OrderBy(x => x))
            {
                if (NotStock.Contains(id)) continue;
                // The game offers 10 pop-up shops; the enum reserves 20. 11-20 only show once stocked.
                if (id >= (int)StorefrontType.PopUpShop_11 && id <= (int)StorefrontType.PopUpShop_20 && !StoreEdits.HasEdits(id)) continue;
                bool hasFile = id == StoreGenerator.WatchStorefront || StoreCatalog.StoreFileForId(id).Length > 0;
                var (added, edited, removed) = StoreEdits.Counts(id);
                stores.Add(new { id, name = StoreName(id), hasFile, added, edited, removed });
            }
            var currencies = Enum.GetValues(typeof(CurrencyType)).Cast<CurrencyType>()
                .Where(c => (int)c > 0).Select(c => new { id = (int)c, name = c.ToString() });
            return J(new { success = true, stores, currencies });
        }

        [HttpGet("/admin/api/stores/{store:int}")]
        public ContentResult Get(int store)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;

            var baseRows = BaseRows(store);
            var removed = StoreEdits.RemovedIds(store);
            var edits = StoreEdits.Rows(store).GroupBy(Pid).ToDictionary(g => g.Key, g => g.Last());
            var items = new List<object>();
            var basePids = new HashSet<int>();
            foreach (var b in baseRows.OfType<JObject>())
            {
                int pid = Pid(b);
                basePids.Add(pid);
                if (removed.Contains(pid)) items.Add(Dto(b, "removed"));
                else if (edits.TryGetValue(pid, out var e)) items.Add(Dto(e, "edited"));
                else items.Add(Dto(b, "base"));
            }
            foreach (var e in edits.Values)
                if (!basePids.Contains(Pid(e))) items.Add(Dto(e, "added"));

            // Sets: every ItemSetId in use, with its name and size.
            var effective = baseRows.OfType<JObject>().Where(b => !removed.Contains(Pid(b)))
                .Select(b => edits.TryGetValue(Pid(b), out var e) ? e : b)
                .Concat(edits.Values.Where(e => !basePids.Contains(Pid(e)))).ToList();
            var sets = effective.Select(r => r["GiftDrop"] as JObject).Where(g => g?["ItemSetId"]?.Type == JTokenType.Integer)
                .GroupBy(g => (int)g!["ItemSetId"]!)
                .Select(g => new { setId = g.Key, name = g.Select(x => (string?)x!["ItemSetFriendlyName"]).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "", count = g.Count() })
                .OrderBy(s => s.setId).ToList();
            int nextSetId = Math.Max(1, (sets.Count > 0 ? sets.Max(s => s.setId) : 0) + 1);

            return J(new
            {
                success = true,
                store,
                name = StoreName(store),
                currency = MainCurrency(baseRows.Count > 0 ? baseRows : new JArray(effective)),
                items,
                sets,
                nextSetId
            });
        }

        // ---- writes ---------------------------------------------------------------------------

        /// <summary>
        /// The row to copy for "add an item that another store sells", cloned. The item picker
        /// lists every store file raw, but the Watch store is SERVED generated: it drops captured
        /// rows whose item the catalog also sells and hides some items (StoreGenerator), so a
        /// picked row may not be in Effective(). Search wider rather than refuse: the store as
        /// served, then its raw file, then any admin row with that id, then every other store file.
        /// </summary>
        private static JObject? FindSourceRow(int store, int pid)
        {
            var row = Effective(store, pid);
            if (row != null) return row;
            JObject? FromFile(string f)
            {
                try
                {
                    var hit = (JObject.Parse(System.IO.File.ReadAllText(f))["StoreItems"] as JArray)?
                        .OfType<JObject>().FirstOrDefault(r => Pid(r) == pid);
                    if (hit != null) StoreGenerator.ShapeRow(hit);
                    return hit;
                }
                catch { return null; }
            }
            var own = StoreCatalog.StoreFileForId(store);
            if (own.Length > 0 && (row = FromFile(own)) != null) return row;
            var anyEdit = StoreEdits.RowsAnywhere(pid).Select(x => x.row).FirstOrDefault();
            if (anyEdit != null) return (JObject)anyEdit.DeepClone();
            try
            {
                if (Directory.Exists(StoreCatalog.StoreDirectory))
                    foreach (var f in Directory.GetFiles(StoreCatalog.StoreDirectory, "*.json"))
                        if (f != own && (row = FromFile(f)) != null) return row;
            }
            catch { }
            return null;
        }

        [HttpPost("/admin/api/stores/{store:int}/add")]
        public async Task<ContentResult> Add(int store)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (NotStock.Contains(store)) return J(new { success = false, message = "That storefront is built per player - it has no stock to edit." }, 400);

            var b = await Body();
            int? sourceStore = (int?)b["sourceStore"];
            int? sourcePid = (int?)b["sourcePid"];
            string desc = ((string?)b["avatarItemDesc"] ?? "").Trim();
            int price = Math.Max(0, (int?)b["price"] ?? 0);
            int currency = (int?)b["currency"] ?? (int)CurrencyType.RecCenterTokens;
            int? subPrice = (int?)b["subPrice"];
            int? setId = b["setId"]?.Type == JTokenType.Integer ? (int?)b["setId"] : null;
            string? setName = (string?)b["setName"];
            bool featured = (bool?)b["featured"] ?? false;
            bool allowDuplicate = (bool?)b["allowDuplicate"] ?? false;

            JObject row;
            bool keepGiftDropId = false;
            if (sourceStore is int ss && ss >= 0 && sourcePid is int sp && sp > 0)
            {
                // A row another store already sells (consumables, skins, boxes, captured items):
                // copied whole, so it opens exactly as it does there - including its GiftDropId,
                // which is what the gift box draws its art from.
                var src = FindSourceRow(ss, sp);
                if (src == null) return J(new { success = false, message = $"Store {ss} has no item {sp}." }, 404);
                row = src;
                keepGiftDropId = true;
            }
            else if (desc.Length > 0)
            {
                var e = AvatarCatalog.ByDesc(desc);
                row = new JObject
                {
                    ["GiftDrop"] = new JObject
                    {
                        ["AvatarItemDesc"] = e?.Desc ?? desc,
                        ["AvatarItemType"] = e?.AvatarItemType ?? 0,
                        ["AvatarItemId"] = e?.AvatarItemId ?? 0,
                        ["AvatarItemInfo"] = null,
                        ["ConsumableItemDesc"] = "",
                        ["Context"] = 0,
                        ["Currency"] = 0,
                        ["CurrencyType"] = 0,
                        ["CustomAvatarItemId"] = null,
                        ["EquipmentItemId"] = 0,
                        ["EquipmentModificationGuid"] = "",
                        ["EquipmentPrefabName"] = "",
                        ["FriendlyName"] = e?.Name ?? "Item",
                        ["IsQuery"] = false,
                        ["ItemCount"] = 1,
                        ["QueryRedirectContext"] = null,
                        ["QueryRedirectRarity"] = null,
                        ["QueryRedirectTag"] = null,
                        ["Rarity"] = e?.Rarity ?? 0,
                        ["SubscribersOnly"] = false,
                        ["TagList"] = null,
                        ["ThumbnailImageName"] = e?.Thumbnail,
                        ["Tooltip"] = "",
                        ["Unique"] = false
                    },
                    ["Type"] = 0
                };
            }
            else return J(new { success = false, message = "Pick an item to add." }, 400);

            var gd = (JObject)(row["GiftDrop"] ??= new JObject());

            // One item, one row per store (unless asked): the same avatar item twice at two prices
            // is what the generated Watch store exists to avoid.
            if (!allowDuplicate)
            {
                string key(JObject g) => PlayerDB.NormalizeAvatarItemDesc((string?)g["AvatarItemDesc"]) + "|" + ((string?)g["ConsumableItemDesc"] ?? "") + "|" + ((string?)g["EquipmentModificationGuid"] ?? "") + "|" + ((string?)g["CustomAvatarItemId"] ?? "");
                string mine = key(gd);
                if (mine.Trim('|').Length > 0)
                {
                    var removed = StoreEdits.RemovedIds(store);
                    var edits = StoreEdits.Rows(store);
                    var existing = BaseRows(store).OfType<JObject>().Where(r => !removed.Contains(Pid(r)))
                        .Select(r => edits.FirstOrDefault(e => Pid(e) == Pid(r)) ?? r)
                        .Concat(edits.Where(e => Pid(e) >= StoreEdits.AdminIdBase))
                        .FirstOrDefault(r => r["GiftDrop"] is JObject g && key(g) == mine);
                    if (existing != null)
                        return J(new { success = false, duplicate = true, pid = Pid(existing), message = $"This store already sells it (item {Pid(existing)}). Edit that one, or add anyway." }, 409);
                }
            }

            int id = StoreEdits.NewId();
            row["PurchasableItemId"] = id;
            if (!keepGiftDropId || (int?)gd["GiftDropId"] is null or 0) gd["GiftDropId"] = id;
            row["IsFeatured"] = featured;
            row["NewUntil"] = null;
            SetPrices(row, price, currency, subPrice);
            SetSet(row, setId, setName);

            StoreEdits.Put(store, row);
            Console.WriteLine($"[admin-stores] {account!.Username} added '{(string?)gd["FriendlyName"]}' to store {store} ({StoreName(store)}) as item {id}: {price} x{currency}, set {setId?.ToString() ?? "-"}");
            return J(new { success = true, pid = id, message = $"Added \"{(string?)gd["FriendlyName"]}\" to {StoreName(store)}." });
        }

        [HttpPost("/admin/api/stores/{store:int}/update")]
        public async Task<ContentResult> Update(int store)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var b = await Body();
            int pid = (int?)b["pid"] ?? 0;
            var row = Effective(store, pid);
            if (row == null) return J(new { success = false, message = $"No item {pid} in this store." }, 404);
            var gd = (JObject)(row["GiftDrop"] ??= new JObject());

            var p0 = (row["Prices"] as JArray)?.FirstOrDefault();
            int price = (int?)b["price"] ?? (int?)p0?["Price"] ?? 0;
            int currency = (int?)b["currency"] ?? (int?)p0?["CurrencyType"] ?? (int)CurrencyType.RecCenterTokens;
            bool priceChanged = b["price"] != null || b["currency"] != null || b["subPrice"] != null;
            if (priceChanged) SetPrices(row, Math.Max(0, price), currency, (int?)b["subPrice"]);
            if (b.ContainsKey("setId"))
                SetSet(row, b["setId"]?.Type == JTokenType.Integer ? (int?)b["setId"] : null, (string?)b["setName"]);
            else if (b["setName"] != null && gd["ItemSetId"]?.Type == JTokenType.Integer)
                gd["ItemSetFriendlyName"] = (string?)b["setName"] ?? "";
            if (b["featured"] != null) row["IsFeatured"] = (bool?)b["featured"] ?? false;
            if (!string.IsNullOrWhiteSpace((string?)b["name"])) gd["FriendlyName"] = ((string)b["name"]!).Trim();

            StoreEdits.Put(store, row);
            Console.WriteLine($"[admin-stores] {account!.Username} edited item {pid} in store {store}: {b.ToString(Newtonsoft.Json.Formatting.None)}");
            return J(new { success = true, message = "Saved." });
        }

        [HttpPost("/admin/api/stores/{store:int}/remove")]
        public async Task<ContentResult> Remove(int store)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            int pid = (int?)(await Body())["pid"] ?? 0;
            StoreEdits.Remove(store, pid);
            Console.WriteLine($"[admin-stores] {account!.Username} removed item {pid} from store {store}");
            return J(new { success = true, message = pid >= StoreEdits.AdminIdBase ? "Deleted." : "Taken off sale (Restore puts it back)." });
        }

        [HttpPost("/admin/api/stores/{store:int}/restore")]
        public async Task<ContentResult> Restore(int store)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            int pid = (int?)(await Body())["pid"] ?? 0;
            StoreEdits.Restore(store, pid);
            Console.WriteLine($"[admin-stores] {account!.Username} restored item {pid} in store {store}");
            return J(new { success = true, message = "Restored to the original." });
        }

        /// <summary>Put items in a set by its id, and/or rename that set everywhere in the store.</summary>
        [HttpPost("/admin/api/stores/{store:int}/set")]
        public async Task<ContentResult> Set(int store)
        {
            var (account, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var b = await Body();
            if (b["setId"]?.Type != JTokenType.Integer) return J(new { success = false, message = "Give the set id number." }, 400);
            int setId = (int)b["setId"]!;
            string? setName = (string?)b["setName"];
            var pids = (b["pids"] as JArray ?? new JArray()).Select(x => (int?)x ?? 0).Where(x => x > 0).ToHashSet();

            var baseRows = BaseRows(store);
            var removed = StoreEdits.RemovedIds(store);
            var changed = new List<JObject>();
            // the chosen items into the set
            foreach (int pid in pids)
            {
                var row = Effective(store, pid, baseRows);
                if (row == null) continue;
                SetSet(row, setId, setName);
                changed.Add(row);
            }
            // rename: every other item already in the set gets the new name
            if (!string.IsNullOrWhiteSpace(setName))
            {
                var edits = StoreEdits.Rows(store);
                var all = baseRows.OfType<JObject>().Where(r => !removed.Contains(Pid(r))).Select(r => Pid(r))
                    .Concat(edits.Where(e => Pid(e) >= StoreEdits.AdminIdBase).Select(Pid)).Distinct();
                foreach (int pid in all)
                {
                    if (pids.Contains(pid)) continue;
                    var row = Effective(store, pid, baseRows);
                    if (row?["GiftDrop"] is JObject g && (int?)g["ItemSetId"] == setId && (string?)g["ItemSetFriendlyName"] != setName)
                    {
                        g["ItemSetFriendlyName"] = setName;
                        changed.Add(row);
                    }
                }
            }
            if (changed.Count > 0) StoreEdits.PutMany(store, changed);
            Console.WriteLine($"[admin-stores] {account!.Username} set {setId} '{setName}' in store {store}: {changed.Count} item(s)");
            return J(new { success = true, message = $"Set {setId}: {changed.Count} item(s) updated." });
        }
    }
}
