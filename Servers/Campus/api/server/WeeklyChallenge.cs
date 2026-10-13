using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;
using LiteDB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The weekly challenges, shown on the WEBSITE (/challenges) and tracked by the server.
    ///
    /// The 2025 client has no weekly challenge screen and never asks for one, so unlike RecFlare
    /// (whose 2023 client evaluates rule trees and reports back) nothing in the game tracks these.
    /// The server does it from what it does see: every finished game posts a PostGameActivity
    /// reward request (APIController.RequestGameReward), and the player's presence says which
    /// room and subroom it was in. So every challenge is "finish N games in X" - wins and enemies
    /// defeated aren't visible to the server.
    ///
    /// The week (Wednesday 21:00 UTC to Wednesday) picks five challenges from <see cref="Pool"/>
    /// with a seeded PRNG (RecFlare's mulberry32 + Knuth spread): the same five for everyone,
    /// recomputed identically after a restart. Finishing three of them sends the week's reward as
    /// an in-game gift box: the item chosen in the admin panel (Challenges tab - any store skin or
    /// clothing item), else a store skin rolled by the week's seed. A player who already owns it
    /// gets the fallback box (tokens) instead. Progress lives in Data/DBs/WeeklyChallenges.db;
    /// admin choices in Data/weekly-rewards.json.
    /// </summary>
    public static class WeeklyChallenge
    {
        private static readonly DateTime Epoch = new(2020, 1, 1, 21, 0, 0, DateTimeKind.Utc);   // a Wednesday
        private static readonly TimeSpan Week = TimeSpan.FromDays(7);
        private const int MapIdBase = 1000;
        private const int PerWeek = 5;
        private const int MaxQuests = 2;
        public const int RequiredForGift = 3;
        private const long FallbackTokens = 500;
        private const string FallbackName = "4-Star Box";
        private const string GiftMessage = "Weekly challenge complete!";

        /// <summary>
        /// One challenge: finish <see cref="Target"/> games in any of <see cref="Rooms"/> (room ids;
        /// empty = anywhere), optionally only in <see cref="SubRooms"/>. <see cref="Group"/> keeps a
        /// week from asking for the same activity twice. The id is the position in the pool, so add
        /// new ones at the END.
        /// </summary>
        public sealed record Def(string Key, string Text, int Target, long[] Rooms, long[] SubRooms, string Group, long LinkRoom, bool Quest = false);

        private static Def G(string key, string text, int target, string group, long[] rooms, long[]? subs = null, bool quest = false)
            => new(key, text, target, rooms, subs ?? Array.Empty<long>(), group, rooms.Length > 0 ? rooms[0] : 0, quest);

        // Room / subroom ids are the stock Rec Room Originals (Data/exported_rooms.json).
        public static readonly Def[] Pool =
        {
            G("Paintball",         "Finish {0} games of Paintball",               5, "paintball", new long[] { 10, 11 }),
            G("PaintballRiver",    "Finish {0} Paintball games on River",         3, "paintball", new long[] { 10, 11 }, new long[] { 10, 16 }),
            G("PaintballHomestead","Finish {0} Paintball games on Homestead",     3, "paintball", new long[] { 10, 11 }, new long[] { 11, 17 }),
            G("PaintballQuarry",   "Finish {0} Paintball games on Quarry",        3, "paintball", new long[] { 10, 11 }, new long[] { 12, 18 }),
            G("PaintballClearcut", "Finish {0} Paintball games on Clear Cut",     3, "paintball", new long[] { 10, 11 }, new long[] { 13, 19 }),
            G("PaintballSpillway", "Finish {0} Paintball games on Spillway",      3, "paintball", new long[] { 10, 11 }, new long[] { 14, 20 }),
            G("PaintballDriveIn",  "Finish {0} Paintball games on Drive-in",      3, "paintball", new long[] { 10, 11 }, new long[] { 15, 21 }),
            G("Dodgeball",         "Finish {0} games of Dodgeball",               5, "dodgeball", new long[] { 8, 36 }),
            G("Soccer",            "Finish {0} games of Soccer",                  3, "soccer",    new long[] { 17 }),
            G("LaserTag",          "Finish {0} games of Laser Tag",               5, "lasertag",  new long[] { 18 }),
            G("LaserTagHangar",    "Finish {0} Laser Tag games in Hangar",        3, "lasertag",  new long[] { 18 }, new long[] { 28 }),
            G("LaserTagCyberJunk", "Finish {0} Laser Tag games in CyberJunk City",3, "lasertag",  new long[] { 18 }, new long[] { 29 }),
            G("Paddleball",        "Finish {0} games of Paddleball",              3, "paddleball",new long[] { 9 }),
            G("Bowling",           "Finish {0} games of Bowling",                 2, "bowling",   new long[] { 38 }),
            G("DiscGolfLake",      "Finish a round of Disc Golf: Lake",           1, "discgolf",  new long[] { 6 }),
            G("DiscGolfPropulsion","Finish a round of Disc Golf: Propulsion",     1, "discgolf",  new long[] { 7 }),
            G("Charades",          "Finish {0} games of 3D Charades",             2, "charades",  new long[] { 3, 4 }),
            G("StuntRunner",       "Finish {0} runs of Stunt Runner",             3, "stuntrunner",new long[] { 40 }),
            G("RecRoyaleSquads",   "Finish {0} games of Rec Royale: Squads",      2, "royale",    new long[] { 19 }),
            G("RecRoyaleSolos",    "Finish {0} games of Rec Royale: Solos",       2, "royale",    new long[] { 20 }),
            G("GoldenTrophy",      "Beat the Quest for the Golden Trophy",      1, "q-golden",  new long[] { 12 }, quest: true),
            G("Jumbotron",         "Beat The Rise of Jumbotron",                1, "q-jumbo",   new long[] { 14 }, quest: true),
            G("CrimsonCauldron",   "Beat the Curse of the Crimson Cauldron",    1, "q-crimson", new long[] { 15 }, quest: true),
            G("IsleOfLostSkulls",  "Beat The Isle of Lost Skulls",              1, "q-isle",    new long[] { 16 }, quest: true),
            G("Crescendo",         "Beat Crescendo of the Blood Moon",          1, "q-crescendo",new long[] { 37 }, quest: true),
            G("AnyGames",          "Finish {0} games of anything",               10, "any",       Array.Empty<long>()),
        };

        public static string TextOf(Def d) => string.Format(d.Text, d.Target);

        // ---- the week ---------------------------------------------------------------------------
        public static int WeekIndex(DateTime now) => (int)Math.Floor((now - Epoch).TotalMilliseconds / Week.TotalMilliseconds);
        public static int MapIdFor(int weekIndex) => MapIdBase + weekIndex;
        public static int CurrentMapId(DateTime now) => MapIdFor(WeekIndex(now));
        public static DateTime StartOf(int mapId) => Epoch + TimeSpan.FromTicks(Week.Ticks * (mapId - MapIdBase));
        public static DateTime EndOf(int mapId) => StartOf(mapId) + Week;

        private static Func<double> Mulberry32(uint seed)
        {
            uint a = seed;
            return () =>
            {
                unchecked
                {
                    a += 0x6D2B79F5;
                    uint t = a;
                    t = (t ^ (t >> 15)) * (t | 1);
                    t ^= t + (t ^ (t >> 7)) * (t | 61);
                    return (t ^ (t >> 14)) / 4294967296.0;
                }
            };
        }
        private static uint SeedFor(int mapId, uint salt) => unchecked(((uint)mapId ^ salt) * 2654435761u);

        /// <summary>The week's five (challenge id, definition) - one per activity group, at most two quests.</summary>
        public static List<(int Id, Def Def)> ChallengesFor(int mapId)
        {
            var random = Mulberry32(SeedFor(mapId, 0x5EED));
            var all = Pool.Select((d, i) => (Id: i + 1, Def: d)).ToList();
            for (int i = all.Count - 1; i > 0; i--)
            {
                int j = (int)Math.Floor(random() * (i + 1));
                (all[i], all[j]) = (all[j], all[i]);
            }
            var picked = new List<(int, Def)>();
            var groups = new HashSet<string>();
            int quests = 0;
            foreach (var c in all)
            {
                if (picked.Count == PerWeek) break;
                if (groups.Contains(c.Def.Group) || (c.Def.Quest && quests >= MaxQuests)) continue;
                picked.Add(c);
                groups.Add(c.Def.Group);
                if (c.Def.Quest) quests++;
            }
            return picked;
        }

        // ---- reward items: the general store's skins and clothing ---------------------------------
        public sealed class RewardItem
        {
            public int GiftDropId;
            public string FriendlyName = "";
            public string Kind = "";            // "skin" or "clothing"
            public string ThumbnailImageName = "";
            public int Rarity;
            public string Prefab = "", Guid = "";
            public string AvatarItemDesc = "";
            public int AvatarItemType;
        }

        private static List<RewardItem>? _items;
        private static readonly object _lock = new();

        /// <summary>
        /// Name -> picture of the first-party custom items (FirstPartyUGC.json). The store rows still
        /// carry rec.net's old thumbnail names, whose files are gone, but almost every store clothing
        /// item is also a first-party item with its OWN render on this server (2,249 of 2,403).
        /// Skins have none - the game draws those itself.
        /// </summary>
        private static Dictionary<string, string> FirstPartyPictures()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string f = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "FirstPartyUGC.json");
                if (!File.Exists(f)) return map;
                var root = JToken.Parse(File.ReadAllText(f));
                var arr = root as JArray ?? root["Results"] as JArray ?? new JArray();
                foreach (var it in arr.OfType<JObject>())
                {
                    string name = ((string?)it["Name"] ?? "").Trim();
                    if (name.Length == 0 || map.ContainsKey(name)) continue;
                    var pics = new List<string?> { (string?)it["ThumbnailImageFilename"] };
                    foreach (var s in it["CurrentSaves"] as JArray ?? new JArray()) pics.Add((string?)s["ThumbnailFileName"]);
                    string? pic = pics.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && ItemImages.Exists(p));
                    if (pic != null) map[name] = pic;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[weeklychallenge] FirstPartyUGC.json unreadable: {ex.Message}"); }
            return map;
        }

        /// <summary>Every skin and clothing item the general store (3.json) sells - what a reward can be.</summary>
        public static List<RewardItem> RewardItems()
        {
            lock (_lock)
            {
                if (_items != null) return _items;
                var list = new List<RewardItem>();
                var pictures = FirstPartyPictures();
                try
                {
                    string f = Path.Combine(StoreCatalog.StoreDirectory, "3.json");
                    foreach (var it in JObject.Parse(File.ReadAllText(f))["StoreItems"] as JArray ?? new JArray())
                    {
                        if (it["GiftDrop"] is not JObject gd || (bool?)gd["IsQuery"] == true) continue;
                        int id = (int?)gd["GiftDropId"] ?? 0;
                        if (id == 0 || list.Any(x => x.GiftDropId == id)) continue;
                        string guid = (string?)gd["EquipmentModificationGuid"] ?? "", prefab = (string?)gd["EquipmentPrefabName"] ?? "";
                        string desc = (string?)gd["AvatarItemDesc"] ?? "";
                        var item = new RewardItem
                        {
                            GiftDropId = id,
                            FriendlyName = (string?)gd["FriendlyName"] ?? "",
                            ThumbnailImageName = (string?)gd["ThumbnailImageName"] ?? "",
                            Rarity = (int?)gd["Rarity"] ?? 0,
                        };
                        if (guid.Length > 0 && prefab.Length > 0)
                        {
                            if (prefab.StartsWith("[Sandbox_")) continue;   // die recolours
                            item.Kind = "skin"; item.Prefab = prefab; item.Guid = SkinOwnership.CanonicalGuid(prefab, guid) ?? guid;
                        }
                        else if (desc.Length > 0)
                        {
                            item.Kind = "clothing"; item.AvatarItemDesc = desc; item.AvatarItemType = (int?)gd["AvatarItemType"] ?? 0;
                        }
                        else continue;
                        // A picture that is really here: the store's own (rarely), else the item's
                        // first-party render; none for a skin (the website shows a plain tile).
                        if (!ItemImages.Exists(item.ThumbnailImageName))
                            item.ThumbnailImageName = pictures.TryGetValue(item.FriendlyName.Trim(), out var pic) ? pic : "";
                        list.Add(item);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[weeklychallenge] could not read the store: {ex.Message}"); }
                if (list.Count > 0) _items = list;   // a failed read is retried, not pinned empty
                return list;
            }
        }

        public static RewardItem? FindItem(int giftDropId) => RewardItems().FirstOrDefault(i => i.GiftDropId == giftDropId);

        // ---- admin-chosen rewards: Data/weekly-rewards.json { "<mapId>": giftDropId } -------------
        private static string ChoicesPath => Path.Combine(Environment.CurrentDirectory, "Data", "weekly-rewards.json");

        private static Dictionary<string, int> LoadChoices()
        {
            try { return File.Exists(ChoicesPath) ? JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(ChoicesPath)) ?? new() : new(); }
            catch (Exception ex) { Console.WriteLine($"[weeklychallenge] {ChoicesPath} unreadable: {ex.Message}"); return new(); }
        }

        /// <summary>Set (giftDropId &gt; 0) or clear (0) the reward chosen for a week.</summary>
        public static void SetChoice(int mapId, int giftDropId)
        {
            lock (_lock)
            {
                var c = LoadChoices();
                if (giftDropId > 0) c[mapId.ToString()] = giftDropId; else c.Remove(mapId.ToString());
                // Old weeks are dropped so the file doesn't grow forever.
                foreach (var k in c.Keys.ToList()) if (int.TryParse(k, out int m) && m < CurrentMapId(DateTime.UtcNow) - 4) c.Remove(k);
                File.WriteAllText(ChoicesPath, JsonConvert.SerializeObject(c, Formatting.Indented));
            }
        }

        public static bool IsChosen(int mapId) => LoadChoices().ContainsKey(mapId.ToString());

        /// <summary>The week's reward: the admin's choice, else a store skin picked by the week's seed.</summary>
        public static RewardItem? RewardFor(int mapId)
        {
            if (LoadChoices().TryGetValue(mapId.ToString(), out int chosen) && FindItem(chosen) is RewardItem it) return it;
            var skins = RewardItems().Where(i => i.Kind == "skin").ToList();
            if (skins.Count == 0) return null;
            return skins[(int)Math.Floor(Mulberry32(SeedFor(mapId, 0x9E3779B9))() * skins.Count)];
        }

        // ---- progress: Data/DBs/WeeklyChallenges.db ------------------------------------------------
        public class Progress
        {
            [BsonId] public string Id { get; set; } = "";          // "{player}|{mapId}"
            public ulong PlayerId { get; set; }
            public int MapId { get; set; }
            public Dictionary<string, int> Counts { get; set; } = new();
            public DateTime? RewardSentAt { get; set; }
            public string RewardName { get; set; } = "";
        }

        private static readonly LiteDatabase _db = new(Path.Combine(Environment.CurrentDirectory, "Data", "DBs", "WeeklyChallenges.db"));
        private static ILiteCollection<Progress> Col => _db.GetCollection<Progress>("progress");

        public static Progress ProgressFor(ulong playerId, int mapId)
            => Col.FindById($"{playerId}|{mapId}") ?? new Progress { Id = $"{playerId}|{mapId}", PlayerId = playerId, MapId = mapId };

        public static int CountOf(Progress p, int challengeId) => p.Counts.TryGetValue(challengeId.ToString(), out int n) ? n : 0;

        public static int Completed(Progress p, List<(int Id, Def Def)> week) => week.Count(c => CountOf(p, c.Id) >= c.Def.Target);

        /// <summary>Every player's progress on a week (admin numbers).</summary>
        public static List<Progress> AllFor(int mapId) => Col.Find(x => x.MapId == mapId).ToList();

        public static void Reset(ulong playerId, int mapId) => Col.Delete($"{playerId}|{mapId}");

        /// <summary>
        /// A game just finished for this player (a PostGameActivity that passed the reward rate
        /// limit). Counts it toward every matching challenge of the week, and sends the reward the
        /// moment the third challenge completes. Never throws - the reward request must succeed.
        /// </summary>
        public static void RecordGame(ulong playerId)
        {
            try
            {
                var inst = HeartbeatDB.GetPlayerHeartbeat(playerId)?.roomInstance;
                if (inst == null || inst.roomId == 0) return;
                int mapId = CurrentMapId(DateTime.UtcNow);
                var week = ChallengesFor(mapId);
                lock (_lock)
                {
                    var p = ProgressFor(playerId, mapId);
                    var moved = new List<string>();
                    foreach (var (id, d) in week)
                    {
                        if (d.Rooms.Length > 0 && !d.Rooms.Contains(inst.roomId)) continue;
                        if (d.SubRooms.Length > 0 && !d.SubRooms.Contains(inst.subRoomId)) continue;
                        int n = CountOf(p, id);
                        if (n >= d.Target) continue;
                        p.Counts[id.ToString()] = n + 1;
                        moved.Add($"{d.Key} {n + 1}/{d.Target}");
                    }
                    if (moved.Count == 0) return;
                    Col.Upsert(p);
                    Console.WriteLine($"[weeklychallenge] {playerId} finished a game in room {inst.roomId}/{inst.subRoomId}: {string.Join(", ", moved)}");
                    if (p.RewardSentAt == null && Completed(p, week) >= RequiredForGift) SendReward(p, mapId);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[weeklychallenge] {playerId}: counting a game failed: {ex.Message}"); }
        }

        /// <summary>Hand over the week's reward (or the fallback box if they own it) as a gift box.</summary>
        private static void SendReward(Progress p, int mapId)
        {
            var item = RewardFor(mapId);
            ulong player = p.PlayerId;
            bool duplicate = item == null
                || (item.Kind == "skin" && PlayerDB.OwnedEquipmentSkins(player).Contains(PlayerDB.EquipmentSkinKey(item.Prefab, item.Guid)))
                || (item.Kind == "clothing" && PlayerDB.GetOwnedAvatarItemKeys(player).Contains(PlayerDB.NormalizeAvatarItemDesc(item.AvatarItemDesc)));

            PendingGiftBox box = !duplicate
                ? new PendingGiftBox
                {
                    GiftDropId = item!.GiftDropId,
                    FriendlyName = item.FriendlyName,
                    Rarity = item.Rarity,
                    AvatarItemDesc = item.Kind == "clothing" ? item.AvatarItemDesc : null,
                    AvatarItemType = item.AvatarItemType,
                    EquipmentPrefabName = item.Kind == "skin" ? item.Prefab : null,
                    EquipmentModificationGuid = item.Kind == "skin" ? item.Guid : null,
                    Message = GiftMessage,
                    FromAccountId = 0,
                }
                : new PendingGiftBox
                {
                    FriendlyName = FallbackName,
                    ThumbnailImageName = ConfigCampus.UseClientProgressionIcons ? "" : QuestRewards.TokenThumbnail,
                    CurrencyType = (int)Classes.CurrencyType.RecCenterTokens,
                    Currency = FallbackTokens,
                    Rarity = QuestRewards.TokenRarity(FallbackTokens),
                    PayOnOpen = true,
                    Message = GiftMessage,
                    FromAccountId = 0,
                };

            // The reward is EARNED, so a skin unlocks (the exception to "skins come from the store
            // or an admin gift"); clothing enters the wardrobe in the same write.
            if (!PlayerDB.AddPendingGiftBoxFull(player, box, grantSkin: !duplicate && item!.Kind == "skin"))
            {
                Console.WriteLine($"[weeklychallenge] {player}: could not store the week {mapId} reward");
                return;
            }
            p.RewardSentAt = DateTime.UtcNow;
            p.RewardName = box.FriendlyName ?? "";
            Col.Upsert(p);
            GiftBoxes.Push(player, box, GiftBoxes.FrameReceived, $"weekly challenge {mapId} reward");
            Console.WriteLine($"[weeklychallenge] {player} completed week {mapId} -> " +
                              (duplicate ? $"already owns '{item?.FriendlyName}', sent the {FallbackName} ({FallbackTokens} tokens)" : $"'{box.FriendlyName}' ({item!.Kind})"));
        }

        public static void Load()
        {
            int map = CurrentMapId(DateTime.UtcNow);
            var r = RewardFor(map);
            Console.WriteLine($"[weeklychallenge] week {map} ({StartOf(map):yyyy-MM-dd HH:mm} - {EndOf(map):yyyy-MM-dd HH:mm} UTC): " +
                              string.Join(", ", ChallengesFor(map).Select(c => c.Def.Key)) +
                              $"; reward '{r?.FriendlyName}'{(IsChosen(map) ? " (chosen)" : "")}");
        }

        // ---- the game's own weekly-challenge routes (the 2025 client never calls them) -------------
        public static JObject Build(ulong playerId)
        {
            int map = CurrentMapId(DateTime.UtcNow);
            var week = ChallengesFor(map);
            var p = ProgressFor(playerId, map);
            var r = RewardFor(map);
            return JObject.FromObject(new
            {
                ChallengeMapId = map,
                CompletedRequired = false,
                StartAt = StartOf(map).ToString("yyyy-MM-ddTHH:mm:ss"),
                EndAt = EndOf(map).ToString("yyyy-MM-ddTHH:mm:ss"),
                ServerTime = DateTime.UtcNow.ToString("o"),
                Challenges = week.Select(c => new { ChallengeId = c.Id, Name = c.Def.Key, Config = "", Description = TextOf(c.Def), Tooltip = "", Complete = CountOf(p, c.Id) >= c.Def.Target }),
                Gift = new
                {
                    GiftDropId = r?.GiftDropId ?? 0, AvatarItemDesc = r?.AvatarItemDesc ?? "", AvatarItemType = r?.AvatarItemType ?? 0,
                    ConsumableItemDesc = "", EquipmentPrefabName = r?.Prefab ?? "", EquipmentModificationGuid = r?.Guid ?? "",
                    StorefrontType = 0, Xp = 0, Level = 0, GiftContext = 0, GiftRarity = r?.Rarity ?? 0
                },
                FallbackGiftName = FallbackName,
                ChallengeThemeString = r?.FriendlyName ?? ""
            });
        }
    }
}
