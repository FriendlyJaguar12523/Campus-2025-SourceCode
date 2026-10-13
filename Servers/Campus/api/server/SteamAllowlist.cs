using System.Globalization;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Steam-only connection allowlist, backed by Data/steam_allowlist.txt.
    ///
    /// Enforced at /Auth/connect/token: a client authenticating on Steam must have its
    /// SteamID64 on the list or it is refused. Every other platform is untouched -
    /// Quest/Meta (Oculus) in particular is explicitly excluded, so headset players are
    /// never gated by this.
    ///
    /// Conventions match Data/allowlist.txt in BannedIpMiddleware, deliberately, so
    /// operators only have to learn one behaviour:
    ///   - missing OR empty file = mode OFF, everyone allowed. It is an ALLOWLIST, so
    ///     an empty one must not mean "nobody may connect" - that would lock the server
    ///     the moment the file is created.
    ///   - '#' starts a comment; blank lines ignored.
    ///   - a read error fails OPEN. Losing the file must not lock out the playerbase.
    ///   - re-read at most every 5s, and only reparsed when the timestamp moves, so
    ///     edits apply live without a restart and without hammering the disk.
    /// </summary>
    public static class SteamAllowlist
    {
        private static string Path_ =>
            System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "steam_allowlist.txt");

        private static HashSet<ulong>? _ids;
        private static DateTime _loadedAt = DateTime.MinValue;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static readonly object _lock = new();

        /// <summary>True when the file exists and lists at least one id.</summary>
        public static bool IsActive => Load().Count > 0;

        /// <summary>
        /// May this Steam id connect? Always true when the list is empty (mode off).
        /// Callers must only ask this for Steam logins - see ShouldCheck.
        /// </summary>
        public static bool IsAllowed(ulong steamId)
        {
            var set = Load();
            if (set.Count == 0) return true;      // mode off
            return set.Contains(steamId);
        }

        /// <summary>
        /// Does this login need checking at all?
        ///
        /// Steam only. A steamId of 0 is treated as "not a Steam login" rather than as a
        /// blocked one: the field is not always populated, and refusing on it would take
        /// out non-Steam clients that leave it blank.
        /// </summary>
        public static bool ShouldCheck(Classes.db_class.Platforms platform, ulong steamId) =>
            platform == Classes.db_class.Platforms.Steam && steamId != 0;

        public static IReadOnlyCollection<ulong> All() => Load();

        // ---- mutation (admin panel) --------------------------------------------------

        public static bool Add(ulong steamId)
        {
            if (steamId == 0) return false;
            var set = new HashSet<ulong>(Load());
            if (!set.Add(steamId)) return false;   // already listed
            Save(set);
            Console.WriteLine($"[steam-allowlist] added {steamId} ({set.Count} total)");
            return true;
        }

        public static bool Remove(ulong steamId)
        {
            var set = new HashSet<ulong>(Load());
            if (!set.Remove(steamId)) return false;
            Save(set);
            Console.WriteLine($"[steam-allowlist] removed {steamId} ({set.Count} remaining)");
            return true;
        }

        /// <summary>Replaces the whole list. An empty set turns the mode off.</summary>
        public static void Replace(IEnumerable<ulong> ids)
        {
            var set = new HashSet<ulong>(ids.Where(i => i != 0));
            Save(set);
            Console.WriteLine($"[steam-allowlist] replaced - {set.Count} id(s)"
                            + (set.Count == 0 ? " (mode OFF - all Steam users allowed)" : ""));
        }

        private static void Save(HashSet<ulong> set)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Path_)!;
                Directory.CreateDirectory(dir);

                var lines = new List<string>
                {
                    "# Campus Steam allowlist.",
                    "# One SteamID64 per line. '#' starts a comment.",
                    "# An EMPTY list means the check is OFF and every Steam user may connect.",
                    "# Quest / Meta and all other platforms are never affected by this file.",
                };
                lines.AddRange(set.OrderBy(i => i).Select(i => i.ToString(CultureInfo.InvariantCulture)));

                File.WriteAllLines(Path_, lines);

                // Force the next Load() to reparse instead of waiting out the 5s window,
                // so the admin panel reflects the change immediately.
                lock (_lock) { _ids = null; _loadedAt = DateTime.MinValue; }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[steam-allowlist] could not write {Path_}: {ex.Message}");
            }
        }

        private static HashSet<ulong> Load()
        {
            var now = DateTime.UtcNow;
            if (_ids != null && (now - _loadedAt).TotalSeconds < 5) return _ids;

            lock (_lock)
            {
                if (_ids != null && (now - _loadedAt).TotalSeconds < 5) return _ids;
                _loadedAt = now;

                try
                {
                    if (!File.Exists(Path_))
                    {
                        _ids = new HashSet<ulong>();
                        return _ids;
                    }

                    var stamp = File.GetLastWriteTimeUtc(Path_);
                    if (_ids != null && stamp == _fileStamp) return _ids;
                    _fileStamp = stamp;

                    var set = new HashSet<ulong>();
                    foreach (var raw in File.ReadAllLines(Path_))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;

                        // Tolerate a trailing comment: "76561198... # kyle"
                        int hash = line.IndexOf('#');
                        if (hash >= 0) line = line.Substring(0, hash).Trim();
                        if (line.Length == 0) continue;

                        if (ulong.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0)
                            set.Add(id);
                        else
                            Console.WriteLine($"[steam-allowlist] ignoring unparseable line: '{raw.Trim()}'");
                    }

                    Console.WriteLine(set.Count > 0
                        ? $"[steam-allowlist] active - {set.Count} Steam id(s) may connect; other Steam users refused"
                        : "[steam-allowlist] file present but empty - check OFF");

                    _ids = set;
                }
                catch (Exception ex)
                {
                    // Fail OPEN, same as the IP allowlist. A read error must not lock the
                    // playerbase out of the server.
                    Console.WriteLine($"[steam-allowlist] could not read {Path_} ({ex.Message}) - allowing all Steam users");
                    _ids = new HashSet<ulong>();
                }

                return _ids;
            }
        }
    }
}
