using LiteDB;
using Campus_live_rewrite.api.server;
using static Campus_live_rewrite.api.server.Classes.db_class.LeaderboardDBClasses;

namespace Campus_live_rewrite.api.dbs
{
    public class LeaderboardDB
    {
        private static readonly LiteDatabase DB = new LiteDatabase(
            Environment.CurrentDirectory + "/Data/DBs/Leaderboard.db"
        );

        // Merge this store's write-ahead log back into the data file.
        // See DbMaintenance for why this must be called explicitly.
        public static void Checkpoint() => DB.Checkpoint();

        private static ILiteCollection<Entry> Entries =>
            DB.GetCollection<Entry>("leaderboard");

        static LeaderboardDB()
        {
            // Short index names to avoid LiteDB 32-char name limit
            Entries.EnsureIndex("pid", x => x.PlayerId);
            Entries.EnsureIndex("sc", x => x.StatChannel);
            Entries.EnsureIndex("rid", x => x.RoomId);

            // Score index for sorting
            Entries.EnsureIndex("score", x => x.Score);
        }


        // ------------------------------------------
        // Add or Update Stat
        // ------------------------------------------
        // `expected` is the client's CurrentStatValue. The reference honours it as an optimistic
        // compare-and-set (only overwrite if the row still holds that value), but that silently
        // DROPPED real updates here: a returning player's client sends CurrentStatValue 0 (fresh
        // session, hasn't re-read the board) while the server still holds their old score, so the
        // compare failed and their new score got STUCK. We take the value UNCONDITIONALLY instead
        // (the reference's CurrentStatValue=null path) so a score always records; `expected` is only
        // logged when it disagreed. The endpoint answers 0 either way.
        public static Entry? GetEntry(ulong playerId, int statChannel, ulong roomId) =>
            Entries.FindOne(x => x.PlayerId == (int)playerId && x.StatChannel == statChannel && x.RoomId == (long)roomId);

        public static RequestResult SetStat(ulong playerId, int statChannel, ulong roomId, int newValue, int? expected = null)
        {
            try
            {
                var entry = GetEntry(playerId, statChannel, roomId);
                var now = DateTime.UtcNow;

                if (entry == null)
                {
                    entry = new Entry
                    {
                        PlayerId = (int)playerId,
                        StatChannel = statChannel,
                        RoomId = (long)roomId,
                        Score = newValue,
                        UpdatedAt = now,
                        DayGainDate = now.Date,
                        DayGain = Math.Abs((long)newValue)
                    };
                    Entries.Insert(entry);
                }
                else
                {
                    // A race-time board keeps the player's BEST (lowest) time: a slower run never
                    // overwrites it, whatever the client sends.
                    if (ConfigCampus.LeaderboardLowestWinsRooms.Contains(roomId) && entry.Score > 0 && newValue >= entry.Score)
                    {
                        Console.WriteLine($"[leaderboard] {playerId} sc{statChannel} room{roomId}: time {newValue} ms is not better than stored {entry.Score} ms - kept");
                        return RequestResult.Success;
                    }
                    if (expected.HasValue && entry.Score != expected.Value)
                        Console.WriteLine($"[leaderboard] {playerId} sc{statChannel} room{roomId}: CurrentStatValue {expected} != stored {entry.Score} - writing {newValue} anyway (would have stuck otherwise)");
                    long gain = Math.Abs((long)newValue - entry.Score);
                    if (entry.DayGainDate != now.Date) { entry.DayGainDate = now.Date; entry.DayGain = 0; }
                    entry.DayGain += gain;
                    entry.UpdatedAt = now;
                    entry.Score = newValue;
                    Entries.Update(entry);
                }

                // Rank is computed at READ time now (see OrderedBoard), so there is nothing to
                // recompute on write - and doing it descending-only here was the bug.
                return RequestResult.Success;
            }
            catch
            {
                return RequestResult.InvalidStat;
            }
        }

        // ------------------------------------------
        // Overall board = the SUM of the course boards (ConfigCampus.LeaderboardTotalChannels).
        //
        // The room only posts its overall time when a player runs every course in one event, and
        // that is ONE run's total. The overall board is the player's best on each course added
        // together instead: recomputed whenever a course time is saved, written once the player
        // has a time on every course, and kept only when it improves (it can't get worse - every
        // part is a personal best). Returns the new total, or null when nothing was written.
        // ------------------------------------------
        public static int? RecomputeTotal(ulong playerId, ulong roomId)
        {
            if (!ConfigCampus.LeaderboardTotalChannels.TryGetValue(roomId, out var t)) return null;
            long sum = 0;
            foreach (int part in t.Parts)
            {
                var e = GetEntry(playerId, part, roomId);
                if (e == null || e.Score <= 0) return null;   // a course not run yet: no overall time
                sum += e.Score;
            }
            if (sum > int.MaxValue) return null;
            var cur = GetEntry(playerId, t.Total, roomId);
            if (cur != null && cur.Score > 0 && cur.Score <= sum) return null;
            SetStat(playerId, t.Total, roomId, (int)sum);
            Console.WriteLine($"[leaderboard] room {roomId} sc{t.Total} p{playerId}: overall = sum of best course times = {sum} ms (was {(cur == null ? "none" : cur.Score + " ms")})");
            return (int)sum;
        }

        // ------------------------------------------
        // The board's entries in RANK ORDER, computed for THIS read.
        //
        // Order is decided by the caller's SortAscending, exactly as RecFlare's leaderboard
        // does it: descending (highest first) by default, ascending (lowest first) for a
        // lowest-wins stat like a race time, with ties broken on the LOWER player id either
        // way so the order is stable. Rank (1-based) is assigned by position here, at READ
        // time - so the SAME stored board reads correctly in either direction.
        //
        // This replaced a Rank baked DESCENDING-ONLY into every row at write time: every
        // ascending stat (times, fewest-deaths) was ranked backwards - the WORST score shown
        // as #1 - and no read could get the right order because the direction was already
        // frozen the wrong way.
        // `only` (optional) restricts the board to those players - a FRIENDS read (FilterType 1)
        // passes the viewer and their friends, who are then ranked among themselves.
        private static List<Entry> OrderedBoard(int statChannel, ulong roomId, bool sortAscending, HashSet<int>? only = null)
        {
            var list = Entries.Find(x => x.StatChannel == statChannel && x.RoomId == (long)roomId).ToList();
            if (only != null) list = list.Where(x => only.Contains(x.PlayerId)).ToList();

            // Race-time rooms ask with SortAscending=false too; fastest first regardless.
            if (ConfigCampus.LeaderboardLowestWinsRooms.Contains(roomId)) sortAscending = true;

            var ordered = (sortAscending
                    ? list.OrderBy(x => x.Score)
                    : list.OrderByDescending(x => x.Score))
                .ThenBy(x => x.PlayerId)   // ties: lower id first, either direction
                .ToList();

            // Ranks are 0-BASED (RecFlare/live-service convention): the client ADDS ONE before it
            // draws, so Rank 0 renders as "#1". Assigning i+1 here made every board show everyone
            // one place too low (the winner as "#2").
            for (int i = 0; i < ordered.Count; i++) ordered[i].Rank = i;
            return ordered;
        }

        // ------------------------------------------
        // Get Player Rank
        // ------------------------------------------
        public static Entry? GetPlayerRank(ulong playerId, int statChannel, ulong roomId, bool sortAscending = false, HashSet<int>? only = null)
        {
            return OrderedBoard(statChannel, roomId, sortAscending, only)
                .FirstOrDefault(x => x.PlayerId == (int)playerId);
        }

        // ------------------------------------------
        // Nearby Scores Window - windowSize entries either side of the caller.
        // ------------------------------------------
        public static List<Entry> GetNearbyScores(
            ulong playerId,
            int statChannel,
            ulong roomId,
            int windowSize,
            bool sortAscending = false,
            HashSet<int>? only = null)
        {
            var board = OrderedBoard(statChannel, roomId, sortAscending, only);

            int myIndex = board.FindIndex(x => x.PlayerId == (int)playerId);
            if (myIndex < 0)
                return board.Take(windowSize * 2 + 1).ToList();   // caller not on the board yet: top slice

            int start = Math.Max(myIndex - windowSize, 0);
            int end = Math.Min(myIndex + windowSize, board.Count - 1);
            return board.GetRange(start, end - start + 1);
        }

        // ------------------------------------------
        // Range Get - GetRanks API. RankStart/RankEnd are 0-based, inclusive (the client
        // sends RankStart=0, RankEnd=9 for the top ten).
        // ------------------------------------------
        public static List<Entry> GetRanks(
            int statChannel,
            ulong roomId,
            int rankStart,
            int rankEnd,
            bool sortAscending = false,
            HashSet<int>? only = null)
        {
            var board = OrderedBoard(statChannel, roomId, sortAscending, only);
            int start = Math.Max(rankStart, 0);
            int count = Math.Max(rankEnd - start + 1, 0);
            return board.Skip(start).Take(count).ToList();
        }
    }
}
