using LiteDB;

namespace Campus_live_rewrite.api.dbs
{
    // LiteDB v5 writes through a write-ahead log (`<name>-log.db`) and only merges
    // it back into the main data file on Checkpoint() - which happens automatically
    // ONLY on LiteDatabase.Dispose(), or once the log passes CheckpointSize
    // (default 1000 pages / ~8MB).
    //
    // Every LiteDatabase in this project is a `static` field that is never disposed,
    // and `systemctl stop campus` kills the process without unwinding statics. So no
    // checkpoint ever ran, and the logs sat just under the 1000-page auto-threshold
    // (573-825 pages) and never tripped it either.
    //
    // The result, as of 2026-08-21: Heartbeats.db and Settings.db were 8192 bytes -
    // a bare header page - while Heartbeats-log.db was 5.6MB and Settings-log.db
    // 6.4MB. 100% of that data lived in the WAL and none of it in the data file.
    //
    // That matters beyond tidiness because install.sh deliberately preserves
    // Data/DBs/* across deploys, so those logs were copied forward verbatim into
    // every new build. Deploying a known-good server could not fix a data-level
    // problem, because the data came along with it. That is why swapping in a
    // working server still broke.
    public static class DbMaintenance
    {
        // Checkpoint every store. Each is wrapped separately so one bad file can't
        // stop the rest from flushing.
        private static Timer? _autoCheckpoint;

        // Checkpoint on a schedule, not just at shutdown.
        //
        // Writes land in <name>-log.db and are only merged into <name>.db by a
        // checkpoint. Until then the data is recoverable ONLY by replaying that log.
        // Measured 2026-08-23: a setting written and read back survived a restart, but
        // Settings.db was still 8,192 bytes (untouched since 12 Aug) while
        // Settings-log.db had grown to 7.37 MB - i.e. every setting the server has ever
        // stored lived in the log alone.
        //
        // That works until the log is lost or truncated, at which point everything
        // written since the last checkpoint disappears at once. Shutdown-only
        // checkpointing means a hard kill (crash, SIGKILL, power loss) never merges.
        //
        // A periodic checkpoint bounds that exposure to `minutes`. It only flushes data
        // already committed - it cannot change any response or alter stored values.
        public static void StartAutoCheckpoint(int minutes = 5)
        {
            if (_autoCheckpoint != null) return;
            var period = TimeSpan.FromMinutes(minutes);
            _autoCheckpoint = new Timer(_ =>
            {
                try { CheckpointAll(quiet: true); }
                catch (Exception ex) { Console.WriteLine($"[DbMaintenance] auto-checkpoint failed: {ex.Message}"); }
            }, null, period, period);
            Console.WriteLine($"[DbMaintenance] auto-checkpoint every {minutes} min");
        }

        public static void StopAutoCheckpoint()
        {
            _autoCheckpoint?.Dispose();
            _autoCheckpoint = null;
        }

        public static void CheckpointAll(bool quiet = false)
        {
            Flush("Players", () => PlayerDB.PlayerDBFile.Checkpoint(), quiet);
            Flush("Rooms", () => RoomDB.rooms_db.Checkpoint(), quiet);
            Flush("Chat", () => ChatDB.ChatDBFile.Checkpoint(), quiet);
            Flush("Events", () => EventDB.EventDBFile.Checkpoint(), quiet);
            Flush("PlayersFriends", () => FriendsDB.PlayerFriendDBFile.Checkpoint(), quiet);
            Flush("Heartbeats", HeartbeatDB.Checkpoint, quiet);
            Flush("Settings", SettingsDB.Checkpoint, quiet);
            Flush("Leaderboard", LeaderboardDB.Checkpoint, quiet);
            Flush("Support", SupportDB.Checkpoint, quiet);
            Flush("Clubs", ClubDB.Checkpoint, quiet);
            Flush("Inventions", InventionDB.Checkpoint, quiet);
        }

        private static void Flush(string name, Action action, bool quiet = false)
        {
            try
            {
                action();
                if (!quiet) Console.WriteLine($"[DbMaintenance] checkpointed {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DbMaintenance] FAILED to checkpoint {name}: {ex.Message}");
            }
        }

        // Print the data-file / log-file split at startup. A main file stuck at 8192
        // bytes with a multi-megabyte log means the checkpoint path is broken again.
        public static void ReportWalState()
        {
            string dir = Path.Combine(Environment.CurrentDirectory, "Data", "DBs");
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"[DbMaintenance] no DB directory at {dir}");
                return;
            }

            foreach (var db in Directory.GetFiles(dir, "*.db").OrderBy(f => f))
            {
                if (db.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase)) continue;

                var main = new FileInfo(db);
                var log = new FileInfo(db[..^3] + "-log.db");
                long logLen = log.Exists ? log.Length : 0;

                string flag = (main.Length <= 8192 && logLen > 1024 * 1024)
                    ? "  <-- ALL DATA IS IN THE LOG, main file is empty"
                    : "";

                Console.WriteLine(
                    $"[DbMaintenance] {main.Name,-24} data={main.Length,10:n0}  log={logLen,12:n0}{flag}");
            }
        }
    }
}
