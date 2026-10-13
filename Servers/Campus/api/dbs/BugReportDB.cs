using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// In-game bug reports (the watch's "Report a bug" posts to /ado/bug - Azure DevOps on the real
    /// service). Everything the client sent is kept verbatim (fields, plus any attached files under
    /// Data/BugReports) and shown on the admin panel's Reports tab. The exact field names are not
    /// pinned, so the raw dump is the source of truth and Title/Description are best-effort picks.
    /// </summary>
    public static class BugReportDB
    {
        private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(Path.Combine("Data", "DBs", "BugReports.db")));

        public class Report
        {
            [BsonId] public int Id { get; set; }
            public ulong AccountId { get; set; }
            public string Username { get; set; } = "";
            public string Title { get; set; } = "";
            public string Description { get; set; } = "";
            public string Category { get; set; } = "";
            public string Room { get; set; } = "";
            public string Raw { get; set; } = "";
            public List<string> Files { get; set; } = new();
            public string Platform { get; set; } = "";
            public DateTime At { get; set; } = DateTime.UtcNow;
            public bool Resolved { get; set; }
            public string? ResolvedBy { get; set; }
            public string? Note { get; set; }
        }

        private static ILiteCollection<Report> Col()
        {
            var c = _db.Value.GetCollection<Report>("reports");
            c.EnsureIndex(x => x.At);
            return c;
        }

        public static Report Add(Report r) { Col().Insert(r); return r; }

        public static List<Report> Recent(bool includeResolved, int take = 200) =>
            Col().FindAll().Where(r => includeResolved || !r.Resolved).OrderByDescending(r => r.At).Take(take).ToList();

        public static int OpenCount() => Col().Count(r => !r.Resolved);

        public static bool Resolve(int id, string by, string? note, bool resolved = true)
        {
            var col = Col();
            var r = col.FindById(id);
            if (r == null) return false;
            r.Resolved = resolved; r.ResolvedBy = by; r.Note = note;
            col.Update(r);
            return true;
        }
    }
}
