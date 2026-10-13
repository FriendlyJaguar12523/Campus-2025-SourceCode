using LiteDB;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Item wishlists (POST /api/itemWishlists/v1/wishlist/add|remove). The client's add 405'd
    /// (only the reads existed); the row is recorded here so the add answers and the panel can
    /// read it back. The wishlist DTO the client renders is not pinned yet, so the reads stay
    /// empty until it is (see the wishlist routes) - nothing is lost, the rows are here.
    /// </summary>
    public static class WishlistDB
    {
        private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(Path.Combine("Data", "DBs", "Wishlists.db")));

        public class Row
        {
            [BsonId] public string Key { get; set; } = "";   // "{account}|{storefront}|{item}"
            public ulong AccountId { get; set; }
            public int StorefrontType { get; set; }
            public int PurchasableItemId { get; set; }
            // A UGC (custom avatar item) wish: PurchasableItemId is -1 and this is the item's GUID.
            public string? CustomAvatarItemId { get; set; }
            public string RawBody { get; set; } = "";
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        private static ILiteCollection<Row> Col()
        {
            var c = _db.Value.GetCollection<Row>("items");
            c.EnsureIndex(x => x.AccountId);
            return c;
        }

        private static string KeyFor(ulong account, int item, string? custom) =>
            string.IsNullOrWhiteSpace(custom) ? $"{account}|{item}" : $"{account}|ugc|{custom.ToLowerInvariant()}";

        /// <summary>Record a wish; returns the row (existing or new) so the add can answer it.</summary>
        public static Row Add(ulong account, int storefront, int item, string? custom, string raw)
        {
            var key = KeyFor(account, item, custom);
            try
            {
                var existing = Col().FindById(key);
                if (existing != null) return existing;
                var row = new Row { Key = key, AccountId = account, StorefrontType = storefront, PurchasableItemId = item, CustomAvatarItemId = string.IsNullOrWhiteSpace(custom) ? null : custom, RawBody = raw };
                Col().Upsert(row);
                return row;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[wishlist] add failed: {ex.Message}");
                return new Row { Key = key, AccountId = account, StorefrontType = storefront, PurchasableItemId = item, CustomAvatarItemId = custom };
            }
        }

        public static void Remove(ulong account, int item, string? custom = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    string c = custom.ToLowerInvariant();
                    Col().DeleteMany(x => x.AccountId == account && x.CustomAvatarItemId != null && x.CustomAvatarItemId.ToLower() == c);
                }
                else Col().DeleteMany(x => x.AccountId == account && x.PurchasableItemId == item && x.CustomAvatarItemId == null);
            }
            catch (Exception ex) { Console.WriteLine($"[wishlist] remove failed: {ex.Message}"); }
        }

        /// <summary>The wire entry (real RecNet: WishlistItemId guid, AccountId, PurchasableItemId, CreatedAt;
        /// the 2025 client adds CustomAvatarItemId for UGC wishes). The guid is derived from the key so it is stable.</summary>
        public static Guid WireId(Row r)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("wish|" + r.Key)));
        }

        public static object ToWire(Row r)
        {
            Guid id = WireId(r);
            return new
            {
                WishlistItemId = id,
                AccountId = r.AccountId,
                PurchasableItemId = r.PurchasableItemId,
                CustomAvatarItemId = r.CustomAvatarItemId,
                CreatedAt = r.CreatedAt
            };
        }
        public static List<object> WireFor(ulong account) => For(account).Select(ToWire).ToList();

        /// <summary>
        /// The player now HAS this item (bought, gifted, a gift box, a UGC grant): take it off their
        /// wishlist. Matches a UGC wish by its guid, a store wish by its PurchasableItemId, or - for
        /// a box that only knows its gift drop - by the gift drop of the store row the wish names.
        /// Returns how many wishes were removed.
        /// </summary>
        public static int RemoveAcquired(ulong account, int purchasableItemId = 0, string? customAvatarItemId = null, int giftDropId = 0,
                                         string? avatarItemDesc = null, string? consumableDesc = null, string? equipPrefab = null, string? equipGuid = null)
        {
            try
            {
                var rows = For(account);
                if (rows.Count == 0) return 0;
                string? ugc = string.IsNullOrWhiteSpace(customAvatarItemId) ? null : customAvatarItemId.ToLowerInvariant();
                string avKey = PlayerDB.NormalizeAvatarItemDesc(avatarItemDesc);
                string coKey = PlayerDB.NormalizeConsumableDesc(consumableDesc);
                string eqKey = string.IsNullOrWhiteSpace(equipPrefab) || string.IsNullOrWhiteSpace(equipGuid) ? "" : PlayerDB.EquipmentSkinKey(equipPrefab, equipGuid);
                int removed = 0;
                foreach (var r in rows)
                {
                    bool hit = (ugc != null && r.CustomAvatarItemId != null && r.CustomAvatarItemId.ToLowerInvariant() == ugc)
                            || (purchasableItemId > 0 && r.CustomAvatarItemId == null && r.PurchasableItemId == purchasableItemId);
                    // A grant that only knows WHAT the item is (gift drop, avatar item, consumable, skin), not
                    // which store row it came from: match it through the GiftDrop of the row the wish names.
                    if (!hit && r.CustomAvatarItemId == null && r.PurchasableItemId > 0
                        && (giftDropId != 0 || avKey.Length > 0 || coKey.Length > 0 || eqKey.Length > 0)
                        && RowFor(r.PurchasableItemId)?["GiftDrop"] is JObject gd)
                    {
                        hit = (giftDropId != 0 && ((int?)gd["GiftDropId"] ?? 0) == giftDropId)
                           || (avKey.Length > 0 && PlayerDB.NormalizeAvatarItemDesc((string?)gd["AvatarItemDesc"]).Equals(avKey, StringComparison.OrdinalIgnoreCase))
                           || (coKey.Length > 0 && PlayerDB.NormalizeConsumableDesc((string?)gd["ConsumableItemDesc"]) == coKey)
                           || (eqKey.Length > 0 && !string.IsNullOrWhiteSpace((string?)gd["EquipmentPrefabName"])
                               && PlayerDB.EquipmentSkinKey((string?)gd["EquipmentPrefabName"], (string?)gd["EquipmentModificationGuid"]) == eqKey);
                    }
                    if (hit && Col().Delete(r.Key)) removed++;
                }
                if (removed > 0) Console.WriteLine($"[wishlist] {account} got an item they wished for - removed {removed} wish(es)");
                return removed;
            }
            catch (Exception ex) { Console.WriteLine($"[wishlist] acquired-removal for {account} failed: {ex.Message}"); return 0; }
        }

        public static List<Row> For(ulong account) => Col().Find(x => x.AccountId == account).OrderBy(x => x.CreatedAt).ToList();

        /// <summary>The store row a wished PurchasableItemId names: a token-priced row first, then any stocked store's.</summary>
        private static JObject? RowFor(int pid)
        {
            var hit = Campus_live_rewrite.api.server.StoreServe.FindTokenRow(pid);
            if (hit != null) return hit.Value.row;
            foreach (int id in Campus_live_rewrite.api.server.StoreServe.StockedStores())
                if (Campus_live_rewrite.api.server.StoreServe.ServedRow(id, pid) is { } r) return r;
            return null;
        }

        /// <summary>Is this item on the account's wishlist? A UGC wish is matched by its GUID, a store wish by its PurchasableItemId.</summary>
        public static bool Has(ulong account, long purchasableItemId, string? customAvatarItemId)
        {
            if (account == 0) return false;
            try
            {
                if (!string.IsNullOrWhiteSpace(customAvatarItemId))
                {
                    string c = customAvatarItemId.ToLowerInvariant();
                    return Col().Exists(x => x.AccountId == account && x.CustomAvatarItemId != null && x.CustomAvatarItemId.ToLower() == c);
                }
                return purchasableItemId > 0 && Col().Exists(x => x.AccountId == account && x.CustomAvatarItemId == null && x.PurchasableItemId == (int)purchasableItemId);
            }
            catch (Exception ex) { Console.WriteLine($"[wishlist] has-check failed: {ex.Message}"); return false; }
        }
    }
}
