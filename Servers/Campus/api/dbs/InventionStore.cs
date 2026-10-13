using LiteDB;

// The invention browse / store side of InventionDB: search, feeds, ownership (bought
// inventions), prices, cheers, deletes. Kept next to InventionDB (same LiteDB file) so one
// checkpoint covers both. Everything the client browses is PUBLISHED inventions only; a
// creator always sees their own.
public static class InventionStore
{
    private static ILiteCollection<InventionDB.Invention> Col() => InventionDB.Collection();

    public class Owned
    {
        [BsonId] public string Key { get; set; } = "";     // "{player}|{invention}"
        public ulong PlayerId { get; set; }
        public int InventionId { get; set; }
        public int PricePaid { get; set; }
        public DateTime At { get; set; } = DateTime.UtcNow;
    }

    public class Cheer
    {
        [BsonId] public string Key { get; set; } = "";     // "{player}|{invention}"
        public ulong PlayerId { get; set; }
        public int InventionId { get; set; }
    }

    private static ILiteCollection<Owned> OwnedCol()
    {
        var c = InventionDB.Db.GetCollection<Owned>("owned");
        c.EnsureIndex(x => x.PlayerId);
        c.EnsureIndex(x => x.InventionId);
        return c;
    }

    private static ILiteCollection<Cheer> CheerCol()
    {
        var c = InventionDB.Db.GetCollection<Cheer>("cheers");
        c.EnsureIndex(x => x.InventionId);
        return c;
    }

    // ---- reads --------------------------------------------------------------------------

    public static List<InventionDB.Invention> Published() =>
        Col().Query().Where(x => x.IsPublished).ToList();

    public static List<InventionDB.Invention> GetByIds(IEnumerable<int> ids)
    {
        var set = new HashSet<int>(ids);
        if (set.Count == 0) return new();
        var col = Col();
        return set.Select(id => col.FindById(id)).Where(i => i != null).Select(i => i!).ToList();
    }

    public static List<InventionDB.Invention> ByRoom(long roomId) =>
        Col().Query().Where(x => x.CreationRoomId == (int)roomId && x.IsPublished)
            .ToList().OrderByDescending(x => x.CreatedAt).ToList();

    /// <summary>
    /// The store's search. `value` is the text the player typed; "#something" is a tag or a
    /// creator's @name (the store's creator filter posts it that way). No text = every published
    /// invention, newest first. Terms match the name, the description and the creator's name.
    /// </summary>
    public static List<InventionDB.Invention> Search(string? value, int skip, int take)
    {
        var all = Published();
        string q = (value ?? "").Trim();
        IEnumerable<InventionDB.Invention> hits = all;
        if (q.StartsWith("#") && q.Length > 1)
        {
            string tag = q[1..].Trim();
            ulong creator = Campus_live_rewrite.api.dbs.PlayerDB.GetAccountIdByUsernameCI(tag);
            hits = all.Where(i =>
                (creator != 0 && i.CreatorPlayerId == creator)
                || (i.Tags ?? new()).Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
        }
        else if (q.Length > 0)
        {
            var terms = q.ToLowerInvariant().Split(new[] { ' ', '+', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var names = new Dictionary<ulong, string>();
            string CreatorName(ulong id)
            {
                if (!names.TryGetValue(id, out var n))
                {
                    n = Campus_live_rewrite.api.dbs.PlayerDB.GetAccountById(id)?.Username ?? "";
                    names[id] = n;
                }
                return n;
            }
            hits = all.Where(i =>
            {
                string hay = ((i.Name ?? "") + " " + (i.Description ?? "") + " " + string.Join(" ", i.Tags ?? new()) + " " + CreatorName(i.CreatorPlayerId)).ToLowerInvariant();
                return terms.All(t => hay.Contains(t));
            });
        }
        return hits.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.InventionId)
                   .Skip(Math.Max(0, skip)).Take(take <= 0 ? 100 : take).ToList();
    }

    /// <summary>Featured / top today: the admin-featured ones first, then by downloads and cheers.</summary>
    public static List<InventionDB.Invention> Top(int skip, int take) =>
        Published().OrderByDescending(i => i.IsFeatured).ThenByDescending(i => i.NumDownloads + i.CheerCount * 3)
                   .ThenByDescending(i => i.CreatedAt).Skip(Math.Max(0, skip)).Take(take <= 0 ? 50 : take).ToList();

    // ---- ownership ------------------------------------------------------------------------

    public static HashSet<int> OwnedIds(ulong player) =>
        OwnedCol().Find(x => x.PlayerId == player).Select(x => x.InventionId).ToHashSet();

    public static bool Owns(ulong player, int inventionId) =>
        OwnedCol().FindById($"{player}|{inventionId}") != null;

    public static bool AddOwned(ulong player, int inventionId, int pricePaid)
    {
        var col = OwnedCol();
        string key = $"{player}|{inventionId}";
        if (col.FindById(key) != null) return false;
        // Two buy requests for the same invention can both pass the check above (a double tap): the second insert
        // hits the unique key. That is "already owned", not a crash - and the caller must not charge for it.
        try { col.Insert(new Owned { Key = key, PlayerId = player, InventionId = inventionId, PricePaid = pricePaid }); }
        catch (LiteDB.LiteException) { return false; }
        var c = Col();
        var inv = c.FindById(inventionId);
        if (inv != null) { inv.NumDownloads++; c.Update(inv); }
        return true;
    }

    /// <summary>Created + bought, newest first (the maker pen's Inventions tab).</summary>
    public static List<InventionDB.Invention> Mine(ulong player)
    {
        var mine = InventionDB.GetByCreator(player);
        var bought = GetByIds(OwnedIds(player));
        var byId = new Dictionary<int, InventionDB.Invention>();
        foreach (var i in mine.Concat(bought)) byId[i.InventionId] = i;
        return byId.Values.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.InventionId).ToList();
    }

    public static bool OwnsAll(ulong player, IEnumerable<int> ids)
    {
        var owned = OwnedIds(player);
        var col = Col();
        foreach (int id in ids)
        {
            var inv = col.FindById(id);
            if (inv == null) return false;
            if (inv.CreatorPlayerId == player || owned.Contains(id)) continue;
            return false;
        }
        return true;
    }

    // ---- cheers ---------------------------------------------------------------------------

    public static bool IsCheering(ulong player, int inventionId) => CheerCol().FindById($"{player}|{inventionId}") != null;

    public static InventionDB.Invention? SetCheer(ulong player, int inventionId, bool cheer)
    {
        var col = Col();
        var inv = col.FindById(inventionId);
        if (inv == null) return null;
        var cc = CheerCol();
        string key = $"{player}|{inventionId}";
        bool has = cc.FindById(key) != null;
        if (cheer && !has) { cc.Insert(new Cheer { Key = key, PlayerId = player, InventionId = inventionId }); inv.CheerCount++; }
        else if (!cheer && has) { cc.Delete(key); inv.CheerCount = Math.Max(0, inv.CheerCount - 1); }
        col.Update(inv);
        return inv;
    }

    // ---- creator writes -------------------------------------------------------------------

    /// <summary>Edit the creator's own invention; null when it is not theirs (or does not exist).</summary>
    public static InventionDB.Invention? Edit(int id, ulong creator, Action<InventionDB.Invention> edit, bool staffOverride = false)
    {
        var col = Col();
        var inv = col.FindById(id);
        if (inv == null || (inv.CreatorPlayerId != creator && !staffOverride)) return null;
        edit(inv);
        inv.ModifiedAt = DateTime.UtcNow;
        col.Update(inv);
        return inv;
    }

    public static bool Delete(int id, ulong creator, bool staffOverride = false)
    {
        var col = Col();
        var inv = col.FindById(id);
        if (inv == null || (inv.CreatorPlayerId != creator && !staffOverride)) return false;
        col.Delete(id);
        return true;
    }

    public static void SetFeatured(int id, bool featured)
    {
        var col = Col();
        var inv = col.FindById(id);
        if (inv == null) return;
        inv.IsFeatured = featured;
        col.Update(inv);
    }
}
