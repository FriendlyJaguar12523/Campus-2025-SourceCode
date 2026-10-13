using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Hair is free for everybody.
    ///
    /// Two halves to "every hair and hair colour", and only one of them needed code:
    ///
    ///   HAIR ITEMS - the styles themselves - are ordinary avatar items in
    ///   Data/APIS/items/avatar_items.json, and ownership gates them (APIController's
    ///   /api/avatar/v4/items filters the catalog through PlayerDB.OwnedAvatarItemDescs).
    ///   DefaultAvatarItems.json shipped only 39 of the 131 hair guids in the catalog, so the
    ///   rest had to be earned. This class supplies the other 92.
    ///
    ///   HAIR COLOUR is a storefront price, not an ownership flag. The client keeps its palette
    ///   in HairColorVault, split into NaturalColors (always available) and DyeColors, and
    ///   LinkDyesToStorefront ties each dye swatch to a STORE ITEM - so the 250-token badges on
    ///   the Modify Hair swatches come straight off Data/APIS/GiftDropStores/3.json, where 19
    ///   "Permanent hair dye (...)" entries are priced at 250. (The 22 "... Hair Dye" entries in
    ///   Consumables.json are a different, already-unlocked thing and were a red herring.)
    ///   IsHairDye + StoreJsonWithFreeDyes below zero that price on the way out, and StoreCatalog
    ///   zeroes it at the till, so a colour costs nothing but still goes through the normal
    ///   purchase that unlocks it for good.
    ///
    /// The set is unioned into a player's ownership on READ (see PlayerDB.OwnedAvatarItemDescs)
    /// rather than written into their wardrobe row: existing accounts get it with no migration,
    /// and it cannot drift out of sync with the catalog.
    /// </summary>
    public static class HairGrants
    {
        private static HashSet<string>? _hairKeys;
        private static readonly object _lock = new();

        /// <summary>
        /// Every catalog item whose name carries the word "hair", as normalised ownership keys.
        /// Empty when ConfigCampus.EveryoneGetsAllHair is off.
        ///
        /// Matching on the whole word keeps "Chair" and "Mohair" out while still catching the
        /// handful of hair ACCESSORIES named for it - Bat Hair Bow, Flower Hair Clip, Anime Hair
        /// Hat. Those are deliberately included: they are hair pieces, and eight free accessories
        /// is a better outcome than a rule nobody can predict from the item's name.
        /// </summary>
        public static HashSet<string> FreeHairKeys()
        {
            if (!ConfigCampus.EveryoneGetsAllHair)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_hairKeys != null) return _hairKeys;

            lock (_lock)
            {
                if (_hairKeys != null) return _hairKeys;

                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "avatar_items.json");
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"[hair] {path} not found - no free hair granted");
                        _hairKeys = keys;
                        return _hairKeys;
                    }

                    var rx = new System.Text.RegularExpressions.Regex(
                        @"\bhair\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    foreach (var e in JArray.Parse(File.ReadAllText(path)))
                    {
                        string name = (string?)e["FriendlyName"] ?? "";
                        if (!rx.IsMatch(name)) continue;

                        // Grant the descriptor exactly as the catalog writes it, normalised the
                        // same way ownership is compared - a coloured variant is its own entry
                        // (base guid + combination), so every variant of a hair item lands too.
                        string key = dbs.PlayerDB.NormalizeAvatarItemDesc((string?)e["AvatarItemDesc"]);
                        if (key.Length > 0) keys.Add(key);
                    }

                    // The dye colours are owned too - same set, so a purchase check, a wardrobe
                    // read and the store's IsItemOwned all answer the same way.
                    foreach (var k in FreeHairDyeKeys()) keys.Add(k);

                    Console.WriteLine($"[hair] every account owns {keys.Count} hair item(s) (ConfigCampus.EveryoneGetsAllHair)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[hair] could not build the free-hair set: {ex.Message}");
                }

                _hairKeys = keys;
                return _hairKeys;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex _dyeRx =
            new(@"hair\s*dye", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Is this store item a hair dye? Matches "Permanent hair dye (Bucket Orange)".</summary>
        public static bool IsHairDye(string? friendlyName) =>
            ConfigCampus.FreeHairDyes
            && !string.IsNullOrEmpty(friendlyName)
            && _dyeRx.IsMatch(friendlyName);

        // One cached rewrite per store file, keyed on path + last-write time so an edited or
        // redeployed catalog is picked up without a restart.
        private static readonly Dictionary<string, (DateTime Stamp, string Json)> _freeStores = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A gift-drop store file with every hair dye priced at 0, or null when the file holds no
        /// dyes (the caller then serves the original off disk and nothing is copied into memory).
        /// </summary>
        /// <summary>Zero every hair dye's price in a store object, in place. Returns how many.</summary>
        public static int ZeroDyePrices(JObject root)
        {
            if (!ConfigCampus.FreeHairDyes) return 0;
            int changed = 0;
            foreach (var it in root["StoreItems"] as JArray ?? new JArray())
            {
                if (it is not JObject o) continue;
                if (!IsHairDye((string?)o["GiftDrop"]?["FriendlyName"])) continue;
                foreach (var key in new[] { "Prices", "SubscriberPrices" })
                    foreach (var pr in o[key] as JArray ?? new JArray())
                        if (pr is JObject pj) pj["Price"] = 0;
                changed++;
            }
            return changed;
        }

        /// <summary>Drop rows HiddenItemsStorefronts says the client cannot draw. Returns how many.</summary>
        public static int HideUnloadableItems(JObject root)
        {
            if (!ConfigCampus.HiddenItemsStorefronts) return 0;
            if (root["StoreItems"] is not JArray arr) return 0;
            int n = 0;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                string? desc = (string?)arr[i]?["GiftDrop"]?["AvatarItemDesc"];
                if (!Classes.ItemGuids.StoreCanList(desc)) { arr.RemoveAt(i); n++; }
            }
            return n;
        }

        public static string? StoreJsonWithFreeDyes(string path)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                lock (_lock)
                {
                    if (_freeStores.TryGetValue(path, out var hit) && hit.Stamp == stamp)
                        return hit.Json;
                }

                var root = JObject.Parse(File.ReadAllText(path));
                int changed = 0;
                foreach (var it in root["StoreItems"] as JArray ?? new JArray())
                {
                    if (it is not JObject o) continue;
                    if (!IsHairDye((string?)o["GiftDrop"]?["FriendlyName"])) continue;

                    foreach (var key in new[] { "Prices", "SubscriberPrices" })
                        foreach (var pr in o[key] as JArray ?? new JArray())
                            if (pr is JObject pj) pj["Price"] = 0;
                    changed++;
                }

                changed += HideUnloadableItems(root);   // HiddenItemsStorefronts applies to every store
                if (changed == 0) return null;   // nothing to rewrite - let the caller stream the file

                string json = root.ToString(Newtonsoft.Json.Formatting.None);
                lock (_lock) _freeStores[path] = (stamp, json);
                Console.WriteLine($"[hair] {Path.GetFileName(path)}: {changed} hair dye(s) served at 0 (ConfigCampus.FreeHairDyes)");
                return json;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[hair] could not free the dyes in {Path.GetFileName(path)}: {ex.Message}");
                return null;   // fall back to the untouched file
            }
        }

        // ---- hair dye as OWNED INVENTORY -------------------------------------------------
        //
        // Pricing the dyes at 0 made the swatches read a0 instead of a250 - still a price tag,
        // still a purchase. A dye the player OWNS has no badge at all, and ownership is what the
        // client actually tests: StoreItemModel.IsItemOwned, resolved against the avatar-item
        // inventory, because a dye's GiftDrop is an avatar item (AvatarItemType 1, an
        // AvatarItemDesc, and an empty ConsumableItemDesc).
        //
        // The catch is that the 19 dye descriptors are NOT in avatar_items.json, so
        // /api/avatar/v4/items - which filters the CATALOG by what you own - could never emit
        // them however they were marked owned. So the records are synthesised here from the store
        // entries and appended to that response, and their descs join FreeHairKeys() so every
        // other ownership check agrees.
        private static JArray? _dyeItems;

        // Catalog AvatarItemIds run to 9503, and the dyes' own GiftDropIds (2554-2572) are ALREADY
        // taken by real catalog items - so a synthetic range well clear of both. Reusing GiftDropId
        // here would have made 19 items collide with 19 existing ones.
        private const int DyeAvatarItemIdBase = 9_600_000;

        /// <summary>
        /// Every hair dye as an avatar-item record, shaped like an avatar_items.json entry.
        /// Empty when ConfigCampus.FreeHairDyes is off.
        /// </summary>
        public static JArray FreeHairDyeItems()
        {
            if (!ConfigCampus.FreeHairDyes) return new JArray();
            if (_dyeItems != null) return _dyeItems;

            lock (_lock)
            {
                if (_dyeItems != null) return _dyeItems;

                var arr = new JArray();
                try
                {
                    string dir = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "GiftDropStores");
                    if (Directory.Exists(dir))
                    {
                        int n = 0;
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                        {
                            JObject root;
                            try { root = JObject.Parse(File.ReadAllText(file)); }
                            catch { continue; }

                            foreach (var it in root["StoreItems"] as JArray ?? new JArray())
                            {
                                var gd = it?["GiftDrop"] as JObject;
                                if (gd == null) continue;

                                string name = (string?)gd["FriendlyName"] ?? "";
                                if (!IsHairDye(name)) continue;

                                string desc = (string?)gd["AvatarItemDesc"] ?? "";
                                if (string.IsNullOrWhiteSpace(desc)) continue;      // nothing to own
                                if (!seen.Add(desc)) continue;                      // same dye in two stores

                                arr.Add(new JObject
                                {
                                    ["AvatarItemDesc"]    = desc,
                                    ["AvatarItemType"]    = (int?)gd["AvatarItemType"] ?? 1,
                                    ["PlatformMask"]      = -1,
                                    ["FriendlyName"]      = name,
                                    ["Tooltip"]           = null,
                                    ["Rarity"]            = (int?)gd["Rarity"] ?? -1,
                                    ["TagList"]           = null,
                                    ["AvatarItemId"]      = DyeAvatarItemIdBase + n,
                                    ["IsBaseAvatarItem"]  = false,
                                    ["CreatedAt"]         = "2018-01-01T00:00:00.000Z",
                                    ["ThumbnailImage"]    = null
                                });
                                n++;
                            }
                        }
                        if (n > 0) Console.WriteLine($"[hair] every account owns {n} hair dye colour(s) (ConfigCampus.FreeHairDyes)");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[hair] could not build the free-dye set: {ex.Message}");
                }

                _dyeItems = arr;
                return _dyeItems;
            }
        }

        /// <summary>The dye descriptors, as ownership keys.</summary>
        public static IEnumerable<string> FreeHairDyeKeys() =>
            FreeHairDyeItems()
                .Select(e => dbs.PlayerDB.NormalizeAvatarItemDesc((string?)e["AvatarItemDesc"]))
                .Where(k => k.Length > 0);
    }
}
