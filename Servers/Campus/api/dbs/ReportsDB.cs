using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Player reports - the ones raised from the in-game report UI.
    ///
    /// The client files them with POST /api/PlayerReporting/v3/create (form-encoded; the reporter
    /// is the bearer token, never a body field). That route did not exist here at all, so every
    /// report a player made 404'd and was lost - which is why reporting "did nothing" and why the
    /// admin panel had nothing to show. Reports now land in this store and the admin panel's
    /// Reports section reads and acts on them.
    ///
    /// A report is filed OPEN. A moderator resolves it, and resolving is the only thing that
    /// changes its state - the ban / kick an action performs goes through the normal ban path so
    /// there is one ban mechanism, not two.
    /// </summary>
    public static class ReportsDB
    {
        private static readonly LiteDatabase _db =
            new(System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "DBs", "Reports.db"));

        /// <summary>Merge this store's write-ahead log back into the data file (see DbMaintenance).</summary>
        public static void Checkpoint() => _db.Checkpoint();

        public class Report
        {
            [BsonId]
            public long Id { get; set; }

            public ulong ReporterPlayerId { get; set; }
            public ulong ReportedPlayerId { get; set; }

            /// <summary>Stored verbatim, unmapped - Moderation_Detail.ReportCategory names it for display.</summary>
            public int ReportCategory { get; set; }

            public string? Details { get; set; }
            public double? HeightReporter { get; set; }
            public double? HeightReported { get; set; }

            /// <summary>Null when the report was raised outside a room (the client omits it).</summary>
            public ulong? RoomId { get; set; }
            public string? RoomInstanceType { get; set; }

            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

            // ---- website reports -------------------------------------------------------
            // Filed from the site's Report buttons (POST /api/site/report). Source is "website";
            // Subject says what was reported (player / photo / comment), SubjectId which one,
            // SubjectLink is the site page a moderator can open, Reason the label the reporter
            // picked. In-game reports leave these null and keep using ReportCategory.
            public string? Source { get; set; }
            public string? Subject { get; set; }
            public long SubjectId { get; set; }
            public string? SubjectLink { get; set; }
            public string? Reason { get; set; }

            // ---- moderation state -------------------------------------------------------
            public bool Resolved { get; set; }
            public string? Action { get; set; }          // what was done: dismissed / warned / banned / ...
            public ulong ResolvedByPlayerId { get; set; }
            public DateTime? ResolvedAt { get; set; }
            public string? ModeratorNote { get; set; }
        }

        private static ILiteCollection<Report> Col
        {
            get
            {
                var c = _db.GetCollection<Report>("reports");
                c.EnsureIndex(x => x.ReportedPlayerId);
                c.EnsureIndex(x => x.CreatedAt);
                c.EnsureIndex(x => x.Resolved);
                return c;
            }
        }

        public static Report Create(Report r)
        {
            r.CreatedAt = DateTime.UtcNow;
            r.Resolved = false;
            Col.Insert(r);
            Console.WriteLine($"[report] #{r.Id}: {r.ReporterPlayerId} reported {r.ReportedPlayerId} " +
                              $"(category {r.ReportCategory}{(r.RoomId != null ? $", room {r.RoomId}" : "")})");
            return r;
        }

        public static Report? Get(long id) => Col.FindById(id);

        /// <summary>Newest first. openOnly leaves resolved reports out of the queue.</summary>
        public static List<Report> List(bool openOnly, int skip, int take, ulong reportedPlayerId = 0)
        {
            var q = Col.FindAll().AsEnumerable();
            if (openOnly) q = q.Where(x => !x.Resolved);
            if (reportedPlayerId != 0) q = q.Where(x => x.ReportedPlayerId == reportedPlayerId);
            return q.OrderByDescending(x => x.CreatedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).ToList();
        }

        public static int CountOpen() => Col.Count(x => !x.Resolved);

        /// <summary>An OPEN report by this reporter about the same thing already exists.</summary>
        public static bool HasOpenDuplicate(ulong reporter, string subject, long subjectId, ulong reported) =>
            Col.Exists(x => !x.Resolved && x.ReporterPlayerId == reporter && x.Subject == subject
                            && x.SubjectId == subjectId && x.ReportedPlayerId == reported);

        /// <summary>How many reports this player has had filed against them, ever.</summary>
        public static int CountAgainst(ulong playerId) => Col.Count(x => x.ReportedPlayerId == playerId);

        public static bool Resolve(long id, ulong byPlayerId, string action, string? note)
        {
            var r = Col.FindById(id);
            if (r == null) return false;

            r.Resolved = true;
            r.Action = action;
            r.ResolvedByPlayerId = byPlayerId;
            r.ResolvedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(note)) r.ModeratorNote = note;
            Col.Update(r);

            Console.WriteLine($"[report] #{id} resolved by {byPlayerId}: {action}");
            return true;
        }

        /// <summary>
        /// Resolve every OPEN report against one player at once. Used when an action is taken on a
        /// player rather than on a single report - banning someone should clear their queue, not
        /// leave nine duplicates of the thing you just acted on.
        /// </summary>
        public static int ResolveAllAgainst(ulong reportedPlayerId, ulong byPlayerId, string action, string? note)
        {
            int n = 0;
            foreach (var r in Col.Find(x => x.ReportedPlayerId == reportedPlayerId && !x.Resolved).ToList())
                if (Resolve(r.Id, byPlayerId, action, note)) n++;
            return n;
        }
    }
}
