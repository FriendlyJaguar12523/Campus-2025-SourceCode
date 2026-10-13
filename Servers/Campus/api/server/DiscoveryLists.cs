using System;
using System.Collections.Generic;
using System.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The room lists behind the Play tab, built from THIS server's rooms.
    ///
    /// The Play page (Data/APIS/WatchPages/PlayHighlight.json) was captured from live Rec Room and
    /// names its carousels by algorithmic-list endpoint: foryou, HotList, staffpicks, new,
    /// inkshowcase, recentlyupdated and the genre rows (battle_algoendpoint, horror_algoendpoint,
    /// ...). GET /lists/algorithmiclists/{name}?type=1 answered each from a file under
    /// Data/APIS/AlgorithmicLists, and only two files existed - so "Recommended For You",
    /// "Featured This Week", "Trending", "Rooms We Love" and every genre row were blank. Now each
    /// name is a query over RoomDB, cached for a minute:
    ///
    ///   HotList / foryou      live player count first, then cheers + visits (foryou reshuffles
    ///                         hourly so the row isn't the same as Trending)
    ///   staffpicks            the admin panel's featured rooms, padded from the top cheered
    ///   new                   newest published rooms
    ///   recentlyupdated       most recently changed
    ///   inkshowcase           player-made rooms with a picture, seeded shuffle
    ///   &lt;genre&gt;_algoendpoint  rooms tagged with that genre (see GenreTags), padded from hot
    ///
    /// Only rooms IsRoomListed says are public are ever listed; dorms never are.
    /// </summary>
    public static class DiscoveryLists
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, (DateTime at, List<string> ids)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private const int CacheSeconds = 60;
        private const int PageSize = 30;

        private static readonly Dictionary<string, string[]> GenreTags = new(StringComparer.OrdinalIgnoreCase)
        {
            ["battle"]   = new[] { "battle", "pvp", "shooter", "fps", "combat", "war", "gunfight", "paintball" },
            ["roleplay"] = new[] { "roleplay", "rp", "role-play", "story" },
            ["quests"]   = new[] { "quest", "quests", "adventure", "rpg", "coop", "co-op" },
            ["horror"]   = new[] { "horror", "scary", "spooky", "creepy", "backrooms" },
            ["hangout"]  = new[] { "hangout", "hang out", "social", "chill", "chat", "lounge", "hangouts" },
            ["casual"]   = new[] { "casual", "party", "minigame", "minigames", "fun", "game" },
            ["explore"]  = new[] { "explore", "exploration", "showcase", "art", "museum", "parkour", "obby" },
        };

        /// <summary>Whether this list name is one the server builds (vs a file / store row).</summary>
        public static bool Handles(string name)
        {
            string n = name.ToLowerInvariant();
            return n is "hotlist" or "foryou" or "staffpicks" or "new" or "recentlyupdated" or "inkshowcase"
                || n.EndsWith("_algoendpoint");
        }

        public static List<string> RoomIds(string name)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(name, out var hit) && (DateTime.UtcNow - hit.at).TotalSeconds < CacheSeconds)
                    return hit.ids;
            }
            var ids = Build(name.ToLowerInvariant());
            lock (_lock) _cache[name] = (DateTime.UtcNow, ids);
            return ids;
        }

        private static List<RoomDBClasses.RoomRoot> Listed() =>
            RoomDB.GetHotRooms().Where(r => !r.IsDorm && RoomDB.IsRoomListed(r) && !r.ExcludeFromLists).ToList();

        private static long Score(RoomDBClasses.RoomRoot r, Dictionary<long, int> live)
        {
            long ccu = live.TryGetValue((long)r.RoomId, out var n) ? n : 0;
            long cheers = r.Stats?.CheerCount ?? 0;
            long visits = r.Stats?.VisitCount ?? 0;
            return ccu * 100_000 + cheers * 50 + visits;
        }

        private static List<string> Ids(IEnumerable<RoomDBClasses.RoomRoot> rooms) =>
            rooms.Select(r => r.RoomId.ToString()).Distinct().Take(PageSize).ToList();

        private static List<string> Build(string name)
        {
            var rooms = Listed();
            var live = HeartbeatDB.LivePlayerCountsByRoom();
            var hot = rooms.OrderByDescending(r => Score(r, live)).ThenByDescending(r => r.CreatedAt).ToList();
            int hourSeed = HashCode.Combine(name, DateTime.UtcNow.Year, DateTime.UtcNow.DayOfYear, DateTime.UtcNow.Hour);

            switch (name)
            {
                case "hotlist":
                    return Ids(hot);

                case "foryou":
                {
                    // The top of the hot list in a per-hour order, then the rest shuffled in, so
                    // it reads as a different row from Trending without being random junk.
                    var rng = new Random(hourSeed);
                    var top = hot.Take(12).OrderBy(_ => rng.Next());
                    var rest = hot.Skip(12).OrderBy(_ => rng.Next());
                    return Ids(top.Concat(rest));
                }

                case "staffpicks":
                {
                    var picked = new List<RoomDBClasses.RoomRoot>();
                    foreach (var rid in FeaturedRoomsStore.GetIds())
                    {
                        var r = RoomDB.GetRoom(rid);
                        if (r != null && !r.IsDorm) picked.Add(r);
                    }
                    var pad = hot.Where(r => !RoomDB.IsRecRoomOriginal(r)).OrderByDescending(r => r.Stats?.CheerCount ?? 0);
                    return Ids(picked.Concat(pad));
                }

                case "new":
                    return Ids(rooms.Where(r => !RoomDB.IsRecRoomOriginal(r)).OrderByDescending(r => r.CreatedAt));

                case "recentlyupdated":
                    return Ids(rooms.Where(r => !RoomDB.IsRecRoomOriginal(r))
                                    .OrderByDescending(r => r.PublishedAt > r.CreatedAt ? r.PublishedAt : r.CreatedAt));

                case "inkshowcase":
                {
                    var rng = new Random(hourSeed);
                    var made = rooms.Where(r => !RoomDB.IsRecRoomOriginal(r) && !string.IsNullOrEmpty(r.ImageName)).OrderBy(_ => rng.Next());
                    return Ids(made.Concat(hot));
                }
            }

            if (name.EndsWith("_algoendpoint"))
            {
                string genre = name[..^"_algoendpoint".Length];
                var tags = GenreTags.TryGetValue(genre, out var t) ? t : new[] { genre };
                var tagged = hot.Where(r => tags.Any(tag => RoomDB.RoomHasTag(r, tag))
                                            || tags.Any(tag => (r.Name ?? "").Contains(tag, StringComparison.OrdinalIgnoreCase)));
                return Ids(tagged.Concat(hot));
            }

            return Ids(hot);
        }

        /// <summary>
        /// The Play page's "Featured Creator" tile: the captured page names a live-Rec-Room account
        /// (730029). The local creator with the most public, player-made rooms takes its place -
        /// never the system accounts (1-3) or the room importer.
        /// </summary>
        private static (DateTime at, ulong id) _creator;
        public static ulong FeaturedCreator()
        {
            lock (_lock)
            {
                if (_creator.id != 0 && (DateTime.UtcNow - _creator.at).TotalSeconds < 600) return _creator.id;
            }
            ulong best = 0;
            // The admin panel's Featured tab decides first; then whatever the community board
            // file names; only then the most-rooms heuristic.
            best = FeaturedStore.FeaturedPlayer();
            if (best == 0)
            {
                try
                {
                    string cb = System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "communityboard.json");
                    if (System.IO.File.Exists(cb))
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(cb));
                        ulong fp = (ulong?)jo["FeaturedPlayer"]?["Id"] ?? 0;
                        if (fp != 0 && PlayerDB.GetAccountById(fp) != null) best = fp;
                    }
                }
                catch { }
            }
            if (best != 0) { lock (_lock) _creator = (DateTime.UtcNow, best); return best; }
            try
            {
                var byCreator = Listed()
                    .Where(r => !RoomDB.IsRecRoomOriginal(r) && r.CreatorAccountId > 3)
                    .GroupBy(r => r.CreatorAccountId)
                    .Select(g => (id: g.Key, n: g.Count(), cheers: g.Sum(r => r.Stats?.CheerCount ?? 0)))
                    .Where(x =>
                    {
                        var acc = PlayerDB.GetAccountById(x.id);
                        return acc != null && !string.Equals(acc.Username, "Importer", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(x => x.n).ThenByDescending(x => x.cheers)
                    .FirstOrDefault();
                best = byCreator.id;
            }
            catch { }
            lock (_lock) _creator = (DateTime.UtcNow, best);
            return best;
        }

        /// <summary>The room the Play page's banner shows: the first admin-featured room, else the hottest.</summary>
        public static ulong BannerRoom()
        {
            foreach (var rid in FeaturedRoomsStore.GetIds())
            {
                var r = RoomDB.GetRoom(rid);
                if (r != null && !r.IsDorm) return rid;
            }
            var top = RoomIds("hotlist").FirstOrDefault();
            return ulong.TryParse(top, out var id) ? id : 0;
        }
    }
}
