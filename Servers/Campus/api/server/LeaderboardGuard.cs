using System;
using System.Collections.Concurrent;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Plausibility gate on leaderboard writes in Rec Room ORIGINAL rooms only.
    ///
    /// A stat write (POST Leaderboard/leaderboard/CheckAndSetStat) is the client saying "my score
    /// is now N". Nothing verified N. In an RRO room the game modes are known and a real score
    /// cannot jump by a thousand in a second, so a write that does is a hacked client and is
    /// refused - not written, answered with the same bare 0 the client always gets, logged, and
    /// filed as a system report in the admin panel's Reports tab under Cheating.
    ///
    /// Player-made rooms are NOT gated: a creator's circuits can legitimately award anything at
    /// any rate, and policing that would break their game. RoomDB decides what counts as RRO
    /// (IsRRO, or the base / rro / recroomoriginal tags).
    ///
    /// Two limits, both per (player, channel, room):
    ///   MaxGainPerSecond - |new - stored| divided by the seconds since the last write. The very
    ///                      first write has no "since", so only the day cap applies to it.
    ///   MaxGainPerDay    - the sum of |gains| accepted in the current UTC day.
    /// The magnitude is used so a lowest-wins stat (a race time) is judged the same way.
    /// </summary>
    public static class LeaderboardGuard
    {
        public enum Verdict { Ok, NotOriginalRoom, TooFast, TooMuchToday }

        // One system report per (player, channel) per hour - the client retries a refused write,
        // and ten copies of the same complaint help nobody.
        private static readonly ConcurrentDictionary<string, DateTime> _reported = new();

        public static Verdict Check(ulong playerId, int statChannel, ulong roomId, int newValue,
                                    LeaderboardDBClasses.Entry? stored, out string why)
        {
            why = "";
            if (!ConfigCampus.LeaderboardGuardEnabled) return Verdict.Ok;

            var room = RoomDB.GetRoom(roomId);
            if (room == null || !RoomDB.IsRecRoomOriginal(room)) return Verdict.NotOriginalRoom;

            var now = DateTime.UtcNow;
            long gain = stored == null ? Math.Abs((long)newValue) : Math.Abs((long)newValue - stored.Score);
            if (gain == 0) return Verdict.Ok;

            // ---- rate: gain per second since the last accepted write -------------------------
            if (stored != null && stored.UpdatedAt != default)
            {
                double secs = Math.Max(1.0, (now - stored.UpdatedAt).TotalSeconds);
                double perSec = gain / secs;
                if (perSec > ConfigCampus.LeaderboardMaxGainPerSecond)
                {
                    why = $"+{gain} in {secs:0.#}s ({perSec:0} /s, cap {ConfigCampus.LeaderboardMaxGainPerSecond} /s)";
                    return Verdict.TooFast;
                }
            }

            // ---- volume: gain accepted today -------------------------------------------------
            long today = (stored != null && stored.DayGainDate == now.Date) ? stored.DayGain : 0;
            if (today + gain > ConfigCampus.LeaderboardMaxGainPerDay)
            {
                why = $"+{gain} would make {today + gain} today (cap {ConfigCampus.LeaderboardMaxGainPerDay} /day)";
                return Verdict.TooMuchToday;
            }

            return Verdict.Ok;
        }

        /// <summary>Refuse the write: log it, and file it once an hour as a system Cheating report.</summary>
        public static void Refuse(ulong playerId, string username, int statChannel, ulong roomId, string roomName, int newValue, Verdict verdict, string why)
        {
            Console.WriteLine($"[leaderboard][CHEAT] {playerId} @{username} sc{statChannel} in '{roomName}' ({roomId}): score {newValue} refused - {verdict}: {why}");

            string key = $"{playerId}:{statChannel}";
            var last = _reported.GetOrAdd(key, DateTime.MinValue);
            if ((DateTime.UtcNow - last).TotalHours < 1) return;
            _reported[key] = DateTime.UtcNow;

            try
            {
                ReportsDB.Create(new ReportsDB.Report
                {
                    ReporterPlayerId = 0,                       // the server, not a player
                    ReportedPlayerId = playerId,
                    ReportCategory = (int)Moderation_Detail.ReportCategory.Cheating,
                    Details = $"Leaderboard guard: posted score {newValue} on stat channel {statChannel} in {roomName} - {why}. Write refused.",
                    RoomId = roomId,
                    RoomInstanceType = "LeaderboardGuard"
                });
            }
            catch (Exception ex) { Console.WriteLine($"[leaderboard] could not file the cheat report: {ex.Message}"); }
        }
    }
}
