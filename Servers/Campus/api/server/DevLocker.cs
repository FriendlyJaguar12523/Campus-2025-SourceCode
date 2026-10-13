using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The developer locker's extra avatar items: every avatar item any storefront sells (the
    /// Watch store as generated AND its raw file - the generator hides some captured rows - every
    /// other store file, admin-added rows) that avatar_items.json does
    /// NOT list - 136 on 2026-09-26, mostly captured Watch rows ("Masquerade Peacock Gown", "UFO
    /// Hat"...). /api/avatar/v4/items gives a developer the whole catalog; these are appended so a
    /// developer owns every single item in the game. Shaped like avatar_items.json entries and
    /// rebuilt at most every 10 minutes.
    /// </summary>
    public static class DevLocker
    {
        private static List<JObject>? _extra;
        private static DateTime _builtAt;
        private static readonly object _lock = new();

        public static List<JObject> StoreOnlyAvatarItems(JArray catalog)
        {
            lock (_lock)
            {
                if (_extra != null && DateTime.UtcNow - _builtAt < TimeSpan.FromMinutes(10))
                    return _extra.Select(e => (JObject)e.DeepClone()).ToList();

                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in catalog)
                {
                    string k = PlayerDB.NormalizeAvatarItemDesc((string?)e["AvatarItemDesc"]);
                    if (k.Length > 0) known.Add(k);
                }

                var rows = new List<JToken>();
                try { rows.AddRange(StoreGenerator.MergedWatchStore()["StoreItems"] as JArray ?? new JArray()); } catch { }
                try
                {
                    if (Directory.Exists(StoreCatalog.StoreDirectory))
                        foreach (var f in Directory.GetFiles(StoreCatalog.StoreDirectory, "*.json"))
                        {
                            try { rows.AddRange(JObject.Parse(File.ReadAllText(f))["StoreItems"] as JArray ?? new JArray()); } catch { }
                        }
                } catch { }
                foreach (int store in Enum.GetValues(typeof(Classes.StorefrontType)).Cast<int>().Distinct())
                    rows.AddRange(StoreEdits.Rows(store));

                var extra = new List<JObject>();
                foreach (var r in rows)
                {
                    if (r["GiftDrop"] is not JObject gd) continue;
                    string desc = (string?)gd["AvatarItemDesc"] ?? "";
                    string key = PlayerDB.NormalizeAvatarItemDesc(desc);
                    if (key.Length == 0 || !known.Add(key)) continue;
                    extra.Add(new JObject
                    {
                        ["AvatarItemDesc"] = desc,
                        ["AvatarItemType"] = (int?)gd["AvatarItemType"] ?? 0,
                        ["PlatformMask"] = -1,
                        ["FriendlyName"] = (string?)gd["FriendlyName"] ?? "",
                        ["Tooltip"] = (string?)gd["Tooltip"] ?? "",
                        ["Rarity"] = (int?)gd["Rarity"] ?? 0,
                        ["TagList"] = gd["TagList"]?.DeepClone(),
                        ["AvatarItemId"] = (int?)gd["AvatarItemId"] ?? 0,
                        ["IsBaseAvatarItem"] = false,
                        ["CreatedAt"] = "2020-01-01T00:00:00Z",
                        ["ThumbnailImage"] = (string?)gd["ThumbnailImageName"]
                    });
                }
                Console.WriteLine($"[items] dev locker: {extra.Count} store-only avatar item(s) added to the catalog's {catalog.Count}");
                _extra = extra; _builtAt = DateTime.UtcNow;
                return _extra.Select(e => (JObject)e.DeepClone()).ToList();
            }
        }
    }
}
