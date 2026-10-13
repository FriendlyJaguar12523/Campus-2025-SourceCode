using System.Globalization;
using System.Text.RegularExpressions;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// LiteDB backup + restore for the admin "Database" tab.
    ///  • Snapshots every .db in Data/DBs three times a day (every 8h) into Data/Backups/backup_UTCstamp/
    ///  • Prunes snapshots older than 3 days.
    ///  • Restore is STAGED (a marker file) and applied at next startup BEFORE any DB is opened —
    ///    the only safe moment to overwrite the live files, which are otherwise held open by LiteDB.
    ///    The admin endpoint stages the restore then restarts the process (systemd Restart=always).
    /// </summary>
    public static class BackupService
    {
        private static readonly string DbDir       = Path.Combine("Data", "DBs");
        private static readonly string BackupRoot  = Path.Combine("Data", "Backups");
        private static readonly string RestoreMarker = Path.Combine(BackupRoot, ".restore");
        private const int RetentionDays = 3;
        private const int IntervalHours = 8;   // 3x/day

        private static Timer? _timer;

        // ── startup restore (call FIRST in Main, before any DB access) ──────────────────────
        public static void RestorePendingIfAny()
        {
            try
            {
                if (!File.Exists(RestoreMarker)) return;
                string name = SafeName(File.ReadAllText(RestoreMarker).Trim());
                string dir  = Path.Combine(BackupRoot, name);

                if (name.Length > 0 && Directory.Exists(dir))
                {
                    Directory.CreateDirectory(DbDir);
                    // Drop stale write-ahead logs so a restored .db is not overlaid by an old WAL.
                    foreach (var log in Directory.GetFiles(DbDir, "*-log.db"))
                        try { File.Delete(log); } catch { }
                    int n = 0;
                    foreach (var f in Directory.GetFiles(dir, "*.db"))
                    {
                        File.Copy(f, Path.Combine(DbDir, Path.GetFileName(f)), overwrite: true);
                        n++;
                    }
                    Console.WriteLine($"[backup] RESTORED {n} files from '{name}' before startup");
                }
                File.Delete(RestoreMarker);
            }
            catch (Exception ex) { Console.WriteLine($"[backup] restore-on-start failed: {ex.Message}"); }
        }

        // ── schedule ────────────────────────────────────────────────────────────────────────
        public static void Start()
        {
            try
            {
                Directory.CreateDirectory(BackupRoot);
                Prune();
                _timer = new Timer(_ =>
                {
                    try { CreateBackup(); Prune(); }
                    catch (Exception ex) { Console.WriteLine($"[backup] scheduled run failed: {ex.Message}"); }
                }, null, TimeSpan.FromHours(IntervalHours), TimeSpan.FromHours(IntervalHours));
                Console.WriteLine($"[backup] scheduled every {IntervalHours}h, keeping {RetentionDays} days");
            }
            catch (Exception ex) { Console.WriteLine($"[backup] start failed: {ex.Message}"); }
        }

        public static void Stop() { _timer?.Dispose(); _timer = null; }

        // ── create / prune / list ────────────────────────────────────────────────────────────
        public static string CreateBackup()
        {
            DbMaintenance.CheckpointAll(quiet: true);   // merge WAL into the .db files first
            Directory.CreateDirectory(BackupRoot);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string dir = Path.Combine(BackupRoot, "backup_" + stamp);
            Directory.CreateDirectory(dir);

            int n = 0;
            if (Directory.Exists(DbDir))
                foreach (var f in Directory.GetFiles(DbDir, "*.db"))
                {
                    if (f.EndsWith("-log.db")) continue;   // WAL already merged above
                    try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true); n++; }
                    catch (Exception ex) { Console.WriteLine($"[backup] copy {Path.GetFileName(f)} failed: {ex.Message}"); }
                }
            Console.WriteLine($"[backup] created backup_{stamp} ({n} files)");
            return "backup_" + stamp;
        }

        public static void Prune()
        {
            try
            {
                if (!Directory.Exists(BackupRoot)) return;
                var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
                foreach (var d in Directory.GetDirectories(BackupRoot))
                {
                    var ts = ParseStamp(Path.GetFileName(d));
                    if (ts.HasValue && ts.Value < cutoff)
                        try { Directory.Delete(d, true); Console.WriteLine($"[backup] pruned {Path.GetFileName(d)}"); } catch { }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[backup] prune failed: {ex.Message}"); }
        }

        public class BackupInfo
        {
            public string name { get; set; } = "";
            public string createdAt { get; set; } = "";
            public long sizeBytes { get; set; }
            public int fileCount { get; set; }
        }

        public static List<BackupInfo> List()
        {
            var outp = new List<BackupInfo>();
            if (!Directory.Exists(BackupRoot)) return outp;
            foreach (var d in Directory.GetDirectories(BackupRoot).OrderByDescending(x => x))
            {
                var files = Directory.GetFiles(d, "*.db");
                var ts = ParseStamp(Path.GetFileName(d));
                outp.Add(new BackupInfo
                {
                    name = Path.GetFileName(d),
                    createdAt = (ts ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    sizeBytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } }),
                    fileCount = files.Length
                });
            }
            return outp;
        }

        // Stage a restore for the next start. Returns false if the named backup doesn't exist.
        public static bool StageRestore(string name)
        {
            name = SafeName(name);
            if (name.Length == 0 || !Directory.Exists(Path.Combine(BackupRoot, name))) return false;
            Directory.CreateDirectory(BackupRoot);
            File.WriteAllText(RestoreMarker, name);
            Console.WriteLine($"[backup] staged restore of '{name}' — applies on next start");
            return true;
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────
        private static string SafeName(string? raw)   // no path traversal; a plain folder name only
            => Path.GetFileName((raw ?? "").Trim());

        private static DateTime? ParseStamp(string folderName)
        {
            var m = Regex.Match(folderName, @"backup_(\d{8})_(\d{6})");
            if (!m.Success) return null;
            return DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                ? dt : (DateTime?)null;
        }
    }
}
