using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    // Comments pinned in a room's scene - the little notes players leave stuck to a spot in
    // a room. Ported from RecFlare's `roomcomments` service, which the 2025 client talks to
    // on the `/RoomComments` base URL (see WebController's service map). There were no
    // handlers for it, so reading a room's notes and leaving one both 404'd.
    //
    // Stored as a collection INSIDE Rooms.db so it rides RoomDB's existing checkpoint
    // (DbMaintenance) instead of adding another write-ahead log that never merges.
    public static class RoomCommentsDB
    {
        public const int MaxCommentLength = 1000;
        public const int DefaultCount = 100;
        public const int MaxCount = 500;

        // Matches RecFlare's RoomCommentEntry field-for-field (PascalCase, the client's shape).
        public class RoomCommentEntry
        {
            [BsonId]
            public int CommentId { get; set; }   // autoincrement; also the read cursor (minId)
            public long RoomId { get; set; }
            public long SubRoomId { get; set; }
            public ulong AccountId { get; set; }  // who wrote it
            public string CreatedAt { get; set; } = "";   // ISO-8601 UTC
            public string Message { get; set; } = "";
            public int Style { get; set; }
            public bool Unread { get; set; } = true;       // always true - read state, not per-viewer
            public double PositionX { get; set; }
            public double PositionY { get; set; }
            public double PositionZ { get; set; }
        }

        private static readonly ILiteCollection<RoomCommentEntry> _col =
            RoomDB.rooms_db.GetCollection<RoomCommentEntry>("room_comments");

        static RoomCommentsDB()
        {
            _col.EnsureIndex(x => x.RoomId);
        }

        /// <summary>
        /// A room's comments, NEWEST FIRST. minId is an EXCLUSIVE cursor (-1 = give me
        /// everything), so a client holding up to id N polls with minId=N and gets only what
        /// was written since. count is clamped 1..500. An unknown room simply has none.
        /// </summary>
        public static List<RoomCommentEntry> Get(long roomId, int? count, int? minId, int? subRoomId)
        {
            int take = count.HasValue ? Math.Clamp(count.Value, 1, MaxCount) : DefaultCount;
            int cursor = minId ?? -1;

            var q = _col.Query().Where(x => x.RoomId == roomId);
            // subRoomId omitted -> the whole room; given -> narrow to that subroom.
            if (subRoomId.HasValue)
            {
                int sr = subRoomId.Value;
                q = q.Where(x => x.SubRoomId == sr);
            }

            return q.ToEnumerable()
                    .Where(x => x.CommentId > cursor)          // exclusive
                    .OrderByDescending(x => x.CommentId)       // newest first
                    .Take(take)
                    .ToList();
        }

        /// <summary>
        /// Pins a comment in a subroom's scene. Author is the caller (never trusted from the
        /// body). Message is truncated to MaxCommentLength; a blank message or missing subroom
        /// is the caller's responsibility to reject (returns null on a blank message here as a
        /// safety net). Returns the stored comment so the client can draw the bubble it placed.
        /// </summary>
        public static RoomCommentEntry? Create(long roomId, ulong accountId, long subRoomId,
            string message, int style, double x, double y, double z)
        {
            message = (message ?? "").Trim();
            if (message.Length > MaxCommentLength) message = message.Substring(0, MaxCommentLength);
            if (message.Length == 0) return null;

            int nextId = (_col.Query().OrderByDescending(c => c.CommentId).FirstOrDefault()?.CommentId ?? 0) + 1;

            var entry = new RoomCommentEntry
            {
                CommentId = nextId,
                RoomId = roomId,
                SubRoomId = subRoomId,
                AccountId = accountId,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Message = message,
                Style = style,
                Unread = true,
                PositionX = x,
                PositionY = y,
                PositionZ = z
            };
            _col.Insert(entry);
            Console.WriteLine($"[roomcomments] {accountId} pinned a comment in room {roomId}/{subRoomId} (id {nextId})");
            return entry;
        }

        /// <summary>Deletes one comment - for admin moderation.</summary>
        public static bool Delete(int commentId) => _col.Delete(commentId);

        /// <summary>Every comment across all rooms, newest first - for the admin panel.</summary>
        public static List<RoomCommentEntry> GetAll()
            => _col.Query().OrderByDescending(x => x.CommentId).ToList();
    }
}
