using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Which picture a skin / consumable / plain gift-drop box (the N-Star Unique Boxes) and its
    /// store tile carry.
    ///
    /// The real-store import (2026-09-17..22) gave those store rows Rec Room's own thumbnail names,
    /// and bought boxes copy them. Almost none of those files exist on this server (0 of 88 skin
    /// thumbnails, 30 of 168 consumable ones, 0 of 13 box ones) and img.rec.net is gone, so every
    /// one of them was answered with the fallback room picture - where, with NO thumbnail name,
    /// the client draws the item's own picture, which is what they showed before the import.
    ///
    /// So: a consumable uses the image from the consumables catalog (items/Consumables.json /
    /// AllUnlockableConsumables.json - 160 of those files are here) when there is one; otherwise
    /// a thumbnail name is only sent when the file really exists, and is left EMPTY when it does
    /// not, so the client falls back to the item itself. Avatar items, tokens and XP boxes are
    /// left alone.
    /// </summary>
    public static class ItemImages
    {
        private static readonly string ImagesRoot = Path.Combine(Environment.CurrentDirectory, "Data", "Images");
        // The same folders the /img route looks in (ImagesController.ImgServer).
        private static readonly string[] Folders =
            { "", "rro_images", Path.Combine("rro_images", "base_rros"), "player_images", "polaroid_images",
              "DefaultImgs", "rec_net", "custom_shirt_imgs", "LoadingScreenTips" };

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool ok, DateTime at)> _exists = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan ExistsTtl = TimeSpan.FromMinutes(10);

        /// <summary>Is there a file of this name that /img will serve?</summary>
        public static bool Exists(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains("..")) return false;
            if (_exists.TryGetValue(name, out var hit) && DateTime.UtcNow - hit.at < ExistsTtl) return hit.ok;
            bool ok = false;
            try { ok = Folders.Any(f => File.Exists(Path.Combine(ImagesRoot, f, name))); } catch { }
            _exists[name] = (ok, DateTime.UtcNow);
            return ok;
        }

        // ---- consumables catalog -------------------------------------------------------------
        private static Dictionary<string, string>? _consumableImages;
        private static readonly object _lock = new();

        private static Dictionary<string, string> ConsumableImages()
        {
            lock (_lock)
            {
                if (_consumableImages != null) return _consumableImages;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in new[] { "Consumables.json", "AllUnlockableConsumables.json" })
                {
                    try
                    {
                        string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", file);
                        if (!File.Exists(path)) continue;
                        foreach (var e in JArray.Parse(File.ReadAllText(path)).OfType<JObject>())
                        {
                            string key = PlayerDB.NormalizeConsumableDesc((string?)e["ConsumableItemDesc"]);
                            string? img = (string?)e["ThumbnailImageName"];
                            if (key.Length > 0 && !string.IsNullOrWhiteSpace(img) && !map.ContainsKey(key)) map[key] = img!;
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"[item-images] {file} unreadable: {ex.Message}"); }
                }
                return _consumableImages = map;
            }
        }

        /// <summary>The consumables catalog's image for this consumable, if that file is here.</summary>
        public static string? ConsumableImage(string? consumableItemDesc)
        {
            string key = PlayerDB.NormalizeConsumableDesc(consumableItemDesc);
            return key.Length > 0 && ConsumableImages().TryGetValue(key, out var img) && Exists(img) ? img : null;
        }

        /// <summary>
        /// The thumbnail to send for an item. <paramref name="stored"/> is what the box / row
        /// carries. Only skins, consumables and item-less gift drops are touched.
        /// </summary>
        public static string ThumbFor(string? stored, string? consumableDesc, string? equipmentPrefab, string? equipmentGuid,
                                      string? avatarDesc, string? customAvatarItemId, bool isCurrencyOrXp)
        {
            stored ??= "";
            bool avatar = !string.IsNullOrWhiteSpace(avatarDesc) || !string.IsNullOrWhiteSpace(customAvatarItemId);
            if (avatar || isCurrencyOrXp) return stored;
            if (!string.IsNullOrWhiteSpace(consumableDesc))
                return ConsumableImage(consumableDesc) ?? (Exists(stored) ? stored : "");
            // A skin, or a gift drop with no item of its own (the N-Star Unique Boxes).
            return Exists(stored) ? stored : "";
        }

        public static string ThumbFor(PendingGiftBox g) =>
            ThumbFor(g.ThumbnailImageName, g.ConsumableItemDesc, g.EquipmentPrefabName, g.EquipmentModificationGuid,
                     g.AvatarItemDesc, g.CustomAvatarItemId,
                     (g.Currency > 0 && g.CurrencyType != 0) || GiftBoxes.IsXpOnly(g));

        /// <summary>Fix a store row's GiftDrop.ThumbnailImageName in place (served stores).</summary>
        public static bool FixStoreRow(JObject row)
        {
            if (row["GiftDrop"] is not JObject gd) return false;
            string? before = (string?)gd["ThumbnailImageName"];
            bool currency = (((int?)gd["CurrencyType"]) ?? 0) != 0 && (((long?)gd["Currency"]) ?? 0) > 0;
            string after = ThumbFor(before, (string?)gd["ConsumableItemDesc"], (string?)gd["EquipmentPrefabName"],
                                    (string?)gd["EquipmentModificationGuid"], (string?)gd["AvatarItemDesc"],
                                    (string?)gd["CustomAvatarItemId"], currency);
            if (after == (before ?? "")) return false;
            gd["ThumbnailImageName"] = after.Length > 0 ? after : null;
            return true;
        }
    }
}
