using LiteDB;

// Invention persistence for the Maker Pen "save selection as invention" flow.
// Minimal but real: stores the metadata the client's v6/v9 save posts (the circuit blob
// itself is uploaded separately and referenced by name), assigns an id + replication id,
// and lists a creator's own inventions. Global namespace like ImageMetadataDB/ClubDB.
public static class InventionDB
{
    private static readonly string DbPath = Path.Combine("Data", "DBs", "Inventions.db");
    private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(DbPath));
    public static LiteDatabase Db => _db.Value;
    public static ILiteCollection<Invention> Collection() => Col();

    public class OldVersion
    {
        public int VersionNumber { get; set; }
        public string BlobName { get; set; } = "";
        public int InstantiationCost { get; set; }
        public int LightsCost { get; set; }
        public int ChipsCost { get; set; }
        public int CloudVariablesCost { get; set; }
        public int AICost { get; set; }
        public bool HasBetaContent { get; set; }
    }

    public class Invention
    {
        public int InventionId { get; set; }
        public string ReplicationId { get; set; } = "";
        public ulong CreatorPlayerId { get; set; }
        public string Name { get; set; } = "Untitled";
        public string Description { get; set; } = "No description yet";
        public string ImageName { get; set; } = "";
        public string BlobName { get; set; } = "";     // inventionDataFilename (the .inv blob)
        public int CurrentVersionNumber { get; set; } = 1;
        public int InstantiationCost { get; set; }
        public int LightsCost { get; set; }
        public int ChipsCost { get; set; }
        public int CloudVariablesCost { get; set; }
        public int AICost { get; set; }
        public int Accessibility { get; set; } = 0;    // 0 private until published
        public bool IsPublished { get; set; } = false;
        public int CreationRoomId { get; set; }
        public int Price { get; set; }
        public int UgcVersion { get; set; }
        public bool HasBetaContent { get; set; }
        public string? LongDescription { get; set; }
        public string? DisplayMetadataJson { get; set; }
        public List<int> ReferencedInventions { get; set; } = new();
        public List<string> ReferencedUnityAssetIds { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        // Earlier versions (addversion): a room saved with an older version of the invention
        // still asks for THAT version number, and must get that version's blob.
        public List<OldVersion> VersionHistory { get; set; } = new();
        public bool IsFeatured { get; set; } = false;      // admin-curated (InventionStore.SetFeatured)
        public int NumDownloads { get; set; } = 0;         // purchases / pickups
        public int CheerCount { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FirstPublishedAt { get; set; } = null;
    }

    private static ILiteCollection<Invention> Col()
    {
        var c = Db.GetCollection<Invention>("inventions");
        c.EnsureIndex(x => x.InventionId, true);
        c.EnsureIndex(x => x.CreatorPlayerId);
        return c;
    }

    public static Invention Create(Invention inv)
    {
        var col = Col();
        if (inv.InventionId == 0)
        {
            var last = col.Query().OrderByDescending(x => x.InventionId).Limit(1).FirstOrDefault();
            inv.InventionId = (last?.InventionId ?? 0) + 1;
        }
        if (string.IsNullOrEmpty(inv.ReplicationId)) inv.ReplicationId = Guid.NewGuid().ToString("N");
        inv.CreatedAt = inv.ModifiedAt = DateTime.UtcNow;
        col.Insert(inv);
        return inv;
    }

    public static Invention? GetById(int id) => Col().FindById(id);

    public static List<Invention> GetByCreator(ulong creator) =>
        Col().Query().Where(x => x.CreatorPlayerId == creator).OrderByDescending(x => x.InventionId).ToList();

    public static Invention? Publish(int id, ulong creator, bool published)
    {
        var col = Col();
        var inv = col.FindById(id);
        if (inv == null || inv.CreatorPlayerId != creator) return null;
        inv.IsPublished = published;
        inv.Accessibility = published ? 1 : 0;
        if (published && inv.FirstPublishedAt == null) inv.FirstPublishedAt = DateTime.UtcNow;
        inv.ModifiedAt = DateTime.UtcNow;
        col.Update(inv);
        return inv;
    }

    public static void Checkpoint() { try { Db.Checkpoint(); } catch { } }
}
