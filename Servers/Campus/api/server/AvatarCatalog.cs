using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// A searchable in-memory index of Data/APIS/items/avatar_items.json - every avatar item that
    /// exists, ~3.6k of them.
    ///
    /// The admin Gifts search only ever looked at the STOREFRONT catalogs
    /// (Data/APIS/GiftDropStores), which between them list a few thousand PURCHASABLE drops. Most
    /// of the avatar catalog is not sold in any storefront, so those items simply could not be
    /// found - searching "thief hat" returned the three that happened to be on the Watch store and
    /// nothing else. This index is what makes the rest giftable.
    ///
    /// Loaded once, lazily, and re-read if the file changes on disk (a Data deploy replaces it).
    /// The file is ~1.5 MB; parsing it per keystroke would be absurd once the search box is live.
    /// </summary>
    public static class AvatarCatalog
    {
        public sealed class Entry
        {
            public string Desc = "";          // AvatarItemDesc, RAW - the client parses this
            public string Name = "";
            public int AvatarItemId;
            public int AvatarItemType;
            public int Rarity;
            public string? Thumbnail;
        }

        private static List<Entry>? _items;
        private static DateTime _stamp;
        private static readonly object _lock = new();

        private static string Path_ =>
            Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "avatar_items.json");

        public static IReadOnlyList<Entry> All()
        {
            try
            {
                string path = Path_;
                if (!File.Exists(path))
                {
                    if (_items == null) Console.WriteLine($"[catalog] {path} not found - gift search falls back to storefronts only");
                    return _items ?? (IReadOnlyList<Entry>)Array.Empty<Entry>();
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (_items != null && stamp == _stamp) return _items;

                lock (_lock)
                {
                    if (_items != null && stamp == _stamp) return _items;

                    var list = new List<Entry>();
                    foreach (var e in JArray.Parse(File.ReadAllText(path)))
                    {
                        string desc = (string?)e["AvatarItemDesc"] ?? "";
                        if (string.IsNullOrWhiteSpace(desc)) continue;
                        list.Add(new Entry
                        {
                            Desc = desc,
                            Name = (string?)e["FriendlyName"] ?? "",
                            AvatarItemId = (int?)e["AvatarItemId"] ?? 0,
                            AvatarItemType = (int?)e["AvatarItemType"] ?? 0,
                            Rarity = (int?)e["Rarity"] ?? 0,
                            Thumbnail = (string?)e["ThumbnailImage"]
                        });
                    }

                    _items = list;
                    _stamp = stamp;
                    Console.WriteLine($"[catalog] indexed {list.Count} avatar item(s) for the admin gift search");
                    return _items;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[catalog] index failed: {ex.Message}");
                return _items ?? (IReadOnlyList<Entry>)Array.Empty<Entry>();
            }
        }

        /// <summary>
        /// Name or descriptor match, case-insensitive. Items whose name STARTS with the query come
        /// first - typing "thief" should lead with "Thief Hat", not with everything that merely
        /// contains the word - then the rest alphabetically so the order is stable between
        /// keystrokes rather than jumping around as results stream in.
        /// </summary>
        public static List<Entry> Search(string query, int max)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<Entry>();
            string q = query.Trim().ToLowerInvariant();

            return All()
                .Where(e => e.Name.ToLowerInvariant().Contains(q) || e.Desc.ToLowerInvariant().Contains(q))
                .OrderBy(e => e.Name.ToLowerInvariant().StartsWith(q) ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(max, 1, 500))
                .ToList();
        }

        /// <summary>How many items match, ignoring the page cap - so the UI can say "showing 60 of 214".</summary>
        public static int CountMatches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;
            string q = query.Trim().ToLowerInvariant();
            return All().Count(e => e.Name.ToLowerInvariant().Contains(q) || e.Desc.ToLowerInvariant().Contains(q));
        }

        /// <summary>Exact descriptor lookup (the id the gift form sends back for a catalog item).</summary>
        public static Entry? ByDesc(string? desc)
        {
            if (string.IsNullOrWhiteSpace(desc)) return null;
            string key = dbs.PlayerDB.NormalizeAvatarItemDesc(desc);
            return All().FirstOrDefault(e =>
                string.Equals(dbs.PlayerDB.NormalizeAvatarItemDesc(e.Desc), key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
