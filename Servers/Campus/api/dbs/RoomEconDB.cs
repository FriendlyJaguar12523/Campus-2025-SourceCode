using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// The room economy a creator sets up in Room Settings: room KEYS (a one-off pass sold for
    /// tokens or for a room currency), room CURRENCIES (with the purchase offers that sell them
    /// for tokens), and room CONSUMABLES (stackable items sold for tokens or a room currency).
    /// Plus who owns what: keys per player, currency balances per player, consumable counts per
    /// player. Every route that used to answer "[]" or an ack reads and writes this.
    ///
    /// DTO shapes follow the wire-shape notes (roomkeys-v1-room, roomcurrencies-createcurrency,
    /// roomcurrencies-purchaseoffersbatch, roomconsumables-by-room).
    /// </summary>
    public static class RoomEconDB
    {
        private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(Path.Combine("Data", "DBs", "RoomEcon.db")));

        public class RoomKey
        {
            [BsonId] public long RoomKeyId { get; set; }
            public Guid ReplicationId { get; set; } = Guid.NewGuid();
            public long RoomId { get; set; }
            public ulong CreatorId { get; set; }
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public int Price { get; set; }
            public Guid? PurchaseCurrencyId { get; set; }
            public string? ImageName { get; set; }
            public int Type { get; set; }
            public bool Deleted { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        public class RoomCurrency
        {
            [BsonId] public Guid CurrencyId { get; set; } = Guid.NewGuid();
            public long RoomId { get; set; }
            public ulong CreatorId { get; set; }
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public long Limit { get; set; } = 1_000_000;
            public int Shape { get; set; }
            public int Color { get; set; } = -1;
            public string? ImageName { get; set; }
            public bool Deleted { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
        }

        public class CurrencyOffer
        {
            [BsonId] public Guid CurrencyPurchaseOfferId { get; set; } = Guid.NewGuid();
            public Guid CurrencyId { get; set; }
            public long RoomId { get; set; }
            public int Order { get; set; }
            public string Name { get; set; } = "";
            public long CurrencyAmount { get; set; }
            public long Price { get; set; }
            public bool Deleted { get; set; }
            public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
        }

        public class RoomConsumable
        {
            [BsonId] public Guid RoomConsumableId { get; set; } = Guid.NewGuid();
            public long RoomId { get; set; }
            public ulong CreatorId { get; set; }
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string? ImageName { get; set; }
            public long Price { get; set; }
            public Guid? PurchaseCurrencyId { get; set; }
            public int MaximumCountPerPurchase { get; set; } = 10;
            public bool Deleted { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
        }

        public class KeyOwnership
        {
            [BsonId] public string Key { get; set; } = "";    // "{player}|{roomKeyId}"
            public ulong PlayerId { get; set; }
            public long RoomKeyId { get; set; }
            public long RoomId { get; set; }
            public DateTime At { get; set; } = DateTime.UtcNow;
        }

        public class CurrencyBalance
        {
            [BsonId] public string Key { get; set; } = "";    // "{player}|{currencyId}"
            public ulong PlayerId { get; set; }
            public Guid CurrencyId { get; set; }
            public long RoomId { get; set; }
            public long Balance { get; set; }
        }

        public class ConsumableOwnership
        {
            [BsonId] public string Key { get; set; } = "";    // "{player}|{consumableId}"
            public ulong PlayerId { get; set; }
            public Guid RoomConsumableId { get; set; }
            public long RoomId { get; set; }
            public int Count { get; set; }
            public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
        }

        private static ILiteCollection<RoomKey> Keys() { var c = _db.Value.GetCollection<RoomKey>("keys"); c.EnsureIndex(x => x.RoomId); return c; }
        private static ILiteCollection<RoomCurrency> Currencies() { var c = _db.Value.GetCollection<RoomCurrency>("currencies"); c.EnsureIndex(x => x.RoomId); return c; }
        private static ILiteCollection<CurrencyOffer> Offers() { var c = _db.Value.GetCollection<CurrencyOffer>("offers"); c.EnsureIndex(x => x.CurrencyId); return c; }
        private static ILiteCollection<RoomConsumable> Consumables() { var c = _db.Value.GetCollection<RoomConsumable>("consumables"); c.EnsureIndex(x => x.RoomId); return c; }
        private static ILiteCollection<KeyOwnership> KeysOwned() { var c = _db.Value.GetCollection<KeyOwnership>("keys_owned"); c.EnsureIndex(x => x.PlayerId); c.EnsureIndex(x => x.RoomKeyId); return c; }
        private static ILiteCollection<CurrencyBalance> Balances() { var c = _db.Value.GetCollection<CurrencyBalance>("balances"); c.EnsureIndex(x => x.PlayerId); return c; }
        private static ILiteCollection<ConsumableOwnership> ConsOwned() { var c = _db.Value.GetCollection<ConsumableOwnership>("consumables_owned"); c.EnsureIndex(x => x.PlayerId); c.EnsureIndex(x => x.RoomConsumableId); return c; }

        // ---- keys ----------------------------------------------------------------------------

        public static RoomKey CreateKey(RoomKey k)
        {
            var col = Keys();
            if (k.RoomKeyId == 0)
            {
                var last = col.Query().OrderByDescending(x => x.RoomKeyId).Limit(1).FirstOrDefault();
                k.RoomKeyId = Math.Max(900_000, (last?.RoomKeyId ?? 0) + 1);
            }
            col.Insert(k);
            return k;
        }
        public static RoomKey? GetKey(long id) { var k = Keys().FindById(id); return k == null || k.Deleted ? null : k; }
        public static List<RoomKey> KeysForRoom(long roomId) => Keys().Query().Where(x => x.RoomId == roomId).ToList().Where(x => !x.Deleted).OrderBy(x => x.CreatedAt).ToList();
        public static RoomKey? UpdateKey(long id, Action<RoomKey> edit) { var col = Keys(); var k = col.FindById(id); if (k == null) return null; edit(k); col.Update(k); return k; }
        public static bool OwnsKey(ulong player, long keyId) => KeysOwned().FindById($"{player}|{keyId}") != null;
        public static bool GrantKey(ulong player, RoomKey k)
        {
            var col = KeysOwned();
            string key = $"{player}|{k.RoomKeyId}";
            if (col.FindById(key) != null) return false;
            col.Insert(new KeyOwnership { Key = key, PlayerId = player, RoomKeyId = k.RoomKeyId, RoomId = k.RoomId });
            return true;
        }
        public static bool RevokeKey(ulong player, long keyId) => KeysOwned().Delete($"{player}|{keyId}");
        public static List<RoomKey> KeysOwnedBy(ulong player)
        {
            var ids = KeysOwned().Find(x => x.PlayerId == player).Select(x => x.RoomKeyId).ToList();
            return ids.Select(GetKey).Where(k => k != null).Select(k => k!).ToList();
        }
        public static int KeyOwnerCount(long keyId) => KeysOwned().Count(x => x.RoomKeyId == keyId);

        // ---- currencies ----------------------------------------------------------------------

        public static RoomCurrency CreateCurrency(RoomCurrency c) { Currencies().Insert(c); return c; }
        public static RoomCurrency? GetCurrency(Guid id) { var c = Currencies().FindById(id); return c == null || c.Deleted ? null : c; }
        public static List<RoomCurrency> CurrenciesForRoom(long roomId) => Currencies().Query().Where(x => x.RoomId == roomId).ToList().Where(x => !x.Deleted).OrderBy(x => x.CreatedAt).ToList();
        public static RoomCurrency? UpdateCurrency(Guid id, Action<RoomCurrency> edit) { var col = Currencies(); var c = col.FindById(id); if (c == null) return null; edit(c); c.ModifiedAt = DateTime.UtcNow; col.Update(c); return c; }

        public static CurrencyOffer CreateOffer(CurrencyOffer o) { Offers().Insert(o); return o; }
        public static CurrencyOffer? GetOffer(Guid id) { var o = Offers().FindById(id); return o == null || o.Deleted ? null : o; }
        public static List<CurrencyOffer> OffersFor(Guid currencyId) => Offers().Query().Where(x => x.CurrencyId == currencyId).ToList().Where(x => !x.Deleted).OrderBy(x => x.Order).ThenBy(x => x.Price).ToList();
        public static CurrencyOffer? UpdateOffer(Guid id, Action<CurrencyOffer> edit) { var col = Offers(); var o = col.FindById(id); if (o == null) return null; edit(o); o.ModifiedAt = DateTime.UtcNow; col.Update(o); return o; }

        public static long GetCurrencyBalance(ulong player, Guid currencyId) => Balances().FindById($"{player}|{currencyId}")?.Balance ?? 0;
        public static List<CurrencyBalance> BalancesForRoom(ulong player, long roomId) => Balances().Find(x => x.PlayerId == player && x.RoomId == roomId).ToList();
        /// <summary>Add (or take, negative) room currency. Returns the new balance, or null when it would go negative / over the limit.</summary>
        public static long? AddCurrency(ulong player, RoomCurrency cur, long delta)
        {
            var col = Balances();
            string key = $"{player}|{cur.CurrencyId}";
            var b = col.FindById(key) ?? new CurrencyBalance { Key = key, PlayerId = player, CurrencyId = cur.CurrencyId, RoomId = cur.RoomId, Balance = 0 };
            long next = b.Balance + delta;
            if (next < 0) return null;
            if (cur.Limit > 0 && next > cur.Limit) next = cur.Limit;
            b.Balance = next;
            col.Upsert(b);
            return next;
        }

        // ---- consumables ---------------------------------------------------------------------

        public static RoomConsumable CreateConsumable(RoomConsumable c) { Consumables().Insert(c); return c; }
        public static RoomConsumable? GetConsumable(Guid id) { var c = Consumables().FindById(id); return c == null || c.Deleted ? null : c; }
        public static List<RoomConsumable> ConsumablesForRoom(long roomId) => Consumables().Query().Where(x => x.RoomId == roomId).ToList().Where(x => !x.Deleted).OrderBy(x => x.CreatedAt).ToList();
        public static RoomConsumable? UpdateConsumable(Guid id, Action<RoomConsumable> edit) { var col = Consumables(); var c = col.FindById(id); if (c == null) return null; edit(c); c.ModifiedAt = DateTime.UtcNow; col.Update(c); return c; }

        public static int ConsumableCount(ulong player, Guid id) => ConsOwned().FindById($"{player}|{id}")?.Count ?? 0;
        public static List<ConsumableOwnership> ConsumablesOwnedInRoom(ulong player, long roomId) => ConsOwned().Find(x => x.PlayerId == player && x.RoomId == roomId).Where(x => x.Count > 0).ToList();
        /// <summary>Add (or consume, negative) copies. Returns the new count, or null when there were not enough.</summary>
        public static int? AddConsumable(ulong player, RoomConsumable c, int delta)
        {
            var col = ConsOwned();
            string key = $"{player}|{c.RoomConsumableId}";
            var o = col.FindById(key) ?? new ConsumableOwnership { Key = key, PlayerId = player, RoomConsumableId = c.RoomConsumableId, RoomId = c.RoomId, Count = 0 };
            int next = o.Count + delta;
            if (next < 0) return null;
            o.Count = next; o.ModifiedAt = DateTime.UtcNow;
            col.Upsert(o);
            return next;
        }
        public static int ConsumableSoldCount(Guid id) => ConsOwned().Find(x => x.RoomConsumableId == id).Sum(x => x.Count);

        // ---- DTOs ----------------------------------------------------------------------------

        public static object KeyDto(RoomKey k) => new
        {
            k.RoomKeyId, k.ReplicationId, k.RoomId, k.Name, k.Description, k.Price, k.PurchaseCurrencyId,
            CreatedAt = k.CreatedAt.ToString("o"), ImageName = k.ImageName ?? "", k.Type
        };
        public static object CurrencyDto(RoomCurrency c) => new
        {
            c.CurrencyId, RoomId = (long?)c.RoomId, c.Name, c.Description, CurrencyType = 300, c.Limit, c.Shape, c.Color,
            ImageName = c.ImageName ?? "", CreatedAt = c.CreatedAt.ToString("o"), ModifiedAt = c.ModifiedAt.ToString("o")
        };
        public static object OfferDto(CurrencyOffer o) => new
        {
            o.CurrencyPurchaseOfferId, o.CurrencyId, o.Order, o.Name, o.CurrencyAmount, o.Price, ModifiedAt = o.ModifiedAt.ToString("o")
        };
        public static object ConsumableDto(RoomConsumable c) => new
        {
            c.RoomConsumableId, c.RoomId, c.Name, c.Description, ImageName = c.ImageName ?? "", c.Price, c.PurchaseCurrencyId,
            ModifiedAt = c.ModifiedAt.ToString("o"), c.MaximumCountPerPurchase, CreatedAt = c.CreatedAt.ToString("o")
        };
    }
}
