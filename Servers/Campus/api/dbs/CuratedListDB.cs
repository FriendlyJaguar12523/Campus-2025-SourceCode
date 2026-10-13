using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// A player's own curated lists (the Lists service's `list` table in the reference): the
    /// Play menu's "Saved for Later" row is `__SavedForLater_Rooms` (type 1, room ids), created by
    /// the client itself through PUT /lists/curatedlists/{name}/items/{itemId}/createlistifneeded.
    /// One list per (owner, type, name).
    /// </summary>
    public static class CuratedListDB
    {
        private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(Path.Combine("Data", "DBs", "CuratedLists.db")));

        public class PlayerList
        {
            [BsonId] public long ListId { get; set; }
            public ulong CreatorAccountId { get; set; }
            public string Name { get; set; } = "";
            public int Type { get; set; } = 1;
            public int Accessibility { get; set; } = 0;
            public List<string> ItemIds { get; set; } = new();
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        private static ILiteCollection<PlayerList> Col()
        {
            var c = _db.Value.GetCollection<PlayerList>("lists");
            c.EnsureIndex(x => x.CreatorAccountId);
            return c;
        }

        /// <summary>
        /// A stable id for (owner, type, name) - the same list has the same id before and after it
        /// is created, so a read that answers a not-yet-created reserved list (`__SavedForLater_Rooms`)
        /// hands out the id the later write will use. Positive, 63-bit.
        /// </summary>
        public static long IdFor(ulong owner, int type, string name)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                foreach (char ch in (name ?? "").ToLowerInvariant()) { h ^= ch; h *= 1099511628211UL; }
                h ^= (ulong)type; h *= 1099511628211UL;
                h ^= owner; h *= 1099511628211UL;
                return (long)(h & 0x7FFF_FFFF_FFFF_FFFFUL) | 1L;
            }
        }

        public static PlayerList? Get(ulong owner, int type, string name) =>
            Col().FindById(IdFor(owner, type, name));

        public static PlayerList? GetById(long listId) => Col().FindById(listId);

        public static PlayerList AddItem(ulong owner, int type, string name, int accessibility, string itemId)
        {
            var col = Col();
            long id = IdFor(owner, type, name);
            var list = col.FindById(id) ?? new PlayerList
            {
                ListId = id, CreatorAccountId = owner, Name = name, Type = type, Accessibility = accessibility, CreatedAt = DateTime.UtcNow
            };
            if (!list.ItemIds.Contains(itemId)) list.ItemIds.Add(itemId);
            col.Upsert(list);
            return list;
        }

        public static PlayerList? RemoveItem(ulong owner, int type, string name, string itemId)
        {
            var col = Col();
            var list = col.FindById(IdFor(owner, type, name));
            if (list == null) return null;
            list.ItemIds.RemoveAll(x => x == itemId);
            col.Update(list);
            return list;
        }
    }
}
