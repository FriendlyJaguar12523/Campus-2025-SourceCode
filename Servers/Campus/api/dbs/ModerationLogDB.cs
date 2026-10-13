using LiteDB;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Append-only record of moderation actions (warnings, kicks, room bans and unbans): who did
    /// what to whom, where and why. The reference server keeps `warning` / `report` tables for
    /// this; before now a warning here left no trace once its (0-second) block was overwritten.
    /// Read by the admin panel's player view; never by the client.
    /// </summary>
    public static class ModerationLogDB
    {
        private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(Path.Combine("Data", "DBs", "ModerationLog.db")));

        public class Entry
        {
            [BsonId] public int Id { get; set; }
            public string Kind { get; set; } = "";          // warning / modkick / kick / roomban / roomunban
            public ulong TargetId { get; set; }
            public ulong ByAccountId { get; set; }
            public ulong RoomId { get; set; }
            public long GameSessionId { get; set; }
            public long DurationSeconds { get; set; }
            public int Category { get; set; }
            public string Message { get; set; } = "";
            public string Note { get; set; } = "";
            public DateTime At { get; set; } = DateTime.UtcNow;
        }

        private static ILiteCollection<Entry> Col()
        {
            var c = _db.Value.GetCollection<Entry>("actions");
            c.EnsureIndex(x => x.TargetId);
            return c;
        }

        public static void Record(string kind, ulong target, ulong by, ulong roomId, long gameSessionId, long durationSeconds,
                                  Moderation_Detail.ReportCategory category, string? message, string? note = null)
        {
            try
            {
                Col().Insert(new Entry
                {
                    Kind = kind, TargetId = target, ByAccountId = by, RoomId = roomId, GameSessionId = gameSessionId,
                    DurationSeconds = durationSeconds, Category = (int)category, Message = message ?? "", Note = note ?? ""
                });
            }
            catch (Exception ex) { Console.WriteLine($"[moderation] log write failed: {ex.Message}"); }
        }

        public static List<Entry> ForPlayer(ulong target, int take = 50) =>
            Col().Find(x => x.TargetId == target).OrderByDescending(x => x.At).Take(take).ToList();

        public static List<Entry> Recent(int take = 100) =>
            Col().FindAll().OrderByDescending(x => x.At).Take(take).ToList();

        /// <summary>
        /// Admin panel Ban Logs: newest first, optionally only some kinds (e.g. "ban,report-ban")
        /// and/or only actions against or BY one account. Returns the page plus the total match count.
        /// </summary>
        public static (List<Entry> Rows, int Total) Query(IEnumerable<string>? kinds, ulong? playerId, int skip, int take)
        {
            var set = kinds?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToHashSet();
            IEnumerable<Entry> q = Col().FindAll();
            if (set != null && set.Count > 0) q = q.Where(x => set.Contains(x.Kind));
            if (playerId.HasValue) q = q.Where(x => x.TargetId == playerId.Value || x.ByAccountId == playerId.Value);
            var all = q.OrderByDescending(x => x.At).ToList();
            return (all.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).ToList(), all.Count);
        }
    }
}
