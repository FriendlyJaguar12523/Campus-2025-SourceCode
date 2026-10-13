using Newtonsoft.Json;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The admin panel's "Featured" tab: the featured PLAYERS (the community board's Featured
    /// Creator tile, the Play tab's Featured Creator) and the board's announcement. The featured
    /// ROOMS stay in FeaturedRoomsStore (the game, the website and the panel all read that one
    /// list). Persisted to Data/featured.json so it survives a restart and a redeploy.
    /// </summary>
    public static class FeaturedStore
    {
        private static readonly string FilePath = Path.Combine(Environment.CurrentDirectory, "Data", "featured.json");
        private static readonly object Lock = new();

        public class State
        {
            /// <summary>Featured players, in display order; the first is THE featured creator.</summary>
            public List<ulong> PlayerIds { get; set; } = new();
            public string? Announcement { get; set; }
            public string? AnnouncementUrl { get; set; }
        }

        private static State? _cache;

        public static State Get()
        {
            lock (Lock)
            {
                if (_cache != null) return _cache;
                try
                {
                    if (File.Exists(FilePath))
                        _cache = JsonConvert.DeserializeObject<State>(File.ReadAllText(FilePath));
                }
                catch (Exception ex) { Console.WriteLine($"[featured] read failed: {ex.Message}"); }
                return _cache ??= new State();
            }
        }

        public static void Set(State s)
        {
            var clean = new List<ulong>();
            foreach (var id in s.PlayerIds ?? new()) if (id != 0 && !clean.Contains(id)) clean.Add(id);
            s.PlayerIds = clean;
            lock (Lock)
            {
                _cache = s;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonConvert.SerializeObject(s, Formatting.Indented));
                }
                catch (Exception ex) { Console.WriteLine($"[featured] write failed: {ex.Message}"); }
            }
        }

        /// <summary>The featured creator: the first featured player the admin set, or 0.</summary>
        public static ulong FeaturedPlayer() => Get().PlayerIds.FirstOrDefault();
    }
}
