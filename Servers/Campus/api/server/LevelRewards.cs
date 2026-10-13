using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// What REACHING each level pays, and the grant of it. Ported from the reference's
    /// progression-db.ts LEVEL_REWARDS + econ.app.ts grantLevelUpGifts: one gift box per level
    /// crossed, announced like any other unasked-for gift. Nothing here was doing this before -
    /// a level-up moved the crown and paid nothing.
    ///
    /// The table is Rec Room's published level-reward ladder, in the star ratings it uses
    /// (2-Star = rarity 10, 3-Star 20, 4-Star 30, 5-Star 50). The reference pays a CONSUMABLE on
    /// the early levels the table marks that way; consumables are a static catalog on this server
    /// (nothing is unlocked per player), so those levels pay a token box instead - a real reward
    /// rather than a cosmetic one. Clothing levels roll a random avatar item of the level's
    /// rarity from the general store (3.json, the only catalog with a pool at every tier) that
    /// the player does NOT already own; when that pool is exhausted the level pays tokens too.
    /// </summary>
    public static class LevelRewards
    {
        private const int TOKENS = -1;   // the reference's CONSUMABLE_REWARD slot

        /// <summary>GiftContext.LevelUp - the client's level-up box look.</summary>
        public const int GiftContextLevelUp = 100;

        // Indexed BY level; level 0 is unreachable.
        private static readonly int[] LEVEL_REWARDS =
        {
            0,
            // 1-10: token boxes interleaved with the first clothing drops.
            TOKENS, 10, TOKENS, 10, TOKENS,
            TOKENS, TOKENS, 10, TOKENS, 10,
            // 11-20: 2-Star clothing all the way.
            10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
            // 21-30: 2-Star alternating with 3-Star.
            10, 20, 10, 20, 10, 20, 10, 20, 10, 20,
            // 31-40: 3-Star with a 4-Star every few levels.
            30, 20, 20, 20, 30, 20, 20, 20, 20, 30,
            // 41-50: 4-Star to the top, then the game's only 5-Star.
            30, 30, 30, 30, 30, 30, 30, 30, 30, 50,
        };

        /// <summary>Tokens a level's token box pays: scales with the level so a late token level
        /// is not a consolation prize.</summary>
        public static long TokensForLevel(int level) => level switch
        {
            <= 5  => 50,
            <= 10 => 100,
            <= 20 => 150,
            <= 30 => 200,
            <= 40 => 300,
            < 50  => 500,
            _     => 1000
        };

        public static int RewardFor(int level) =>
            level > 0 && level < LEVEL_REWARDS.Length ? LEVEL_REWARDS[level] : (level >= LEVEL_REWARDS.Length ? 30 : 0);

        // ---- The roll pool: the general store's avatar items, by rarity --------------------------
        public sealed class PoolItem
        {
            public string AvatarItemDesc = "";
            public string FriendlyName = "";
            public int Rarity;
            public int AvatarItemType;
            public int GiftDropId;
        }

        private static readonly object _lock = new();
        private static List<PoolItem>? _pool;
        private static readonly Random _rng = new();

        private static List<PoolItem> Pool()
        {
            lock (_lock)
            {
                if (_pool != null) return _pool;
                var list = new List<PoolItem>();
                try
                {
                    string file = System.IO.Path.Combine(StoreCatalog.StoreDirectory, "3.json");
                    if (System.IO.File.Exists(file))
                    {
                        var root = JObject.Parse(System.IO.File.ReadAllText(file));
                        foreach (var it in root["StoreItems"] as JArray ?? new JArray())
                        {
                            var gd = it["GiftDrop"] as JObject;
                            if (gd == null) continue;
                            if ((bool?)gd["IsQuery"] == true) continue;
                            string desc = (string?)gd["AvatarItemDesc"] ?? "";
                            if (string.IsNullOrWhiteSpace(desc)) continue;
                            int rarity = (int?)gd["Rarity"] ?? 0;
                            if (rarity <= 0) continue;
                            list.Add(new PoolItem
                            {
                                AvatarItemDesc = desc,
                                FriendlyName = (string?)gd["FriendlyName"] ?? "",
                                Rarity = rarity,
                                AvatarItemType = (int?)gd["AvatarItemType"] ?? 0,
                                GiftDropId = (int?)gd["GiftDropId"] ?? 0
                            });
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[levelrewards] could not read the roll pool: {ex.Message}"); }
                Console.WriteLine($"[levelrewards] roll pool: {list.Count} avatar items " +
                                  $"({string.Join(", ", list.GroupBy(p => p.Rarity).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))})");
                _pool = list;
                return _pool;
            }
        }

        /// <summary>A random avatar item of the rarity the player doesn't own, or null when none is left.</summary>
        public static PoolItem? RollClothing(int rarity, HashSet<string> ownedKeys)
        {
            var candidates = Pool().Where(p => p.Rarity == rarity
                                              && !ownedKeys.Contains(PlayerDB.NormalizeAvatarItemDesc(p.AvatarItemDesc))).ToList();
            if (candidates.Count == 0) return null;
            lock (_lock) return candidates[_rng.Next(candidates.Count)];
        }

        /// <summary>
        /// Grant the reward for every level in (fromLevel, toLevel]. Best-effort per level: the XP
        /// and the level are already stored, so a failed roll costs a prize, not the level.
        /// </summary>
        public static void GrantLevelUps(ulong playerId, int fromLevel, int toLevel)
        {
            if (toLevel <= fromLevel) return;
            HashSet<string>? owned = null;
            for (int level = fromLevel + 1; level <= toLevel; level++)
            {
                try
                {
                    int reward = RewardFor(level);
                    if (reward == 0) continue;
                    // The box the real game hands over: the LevelUp box (GiftContext 100), named
                    // "Level N Box", with "Congrats on getting to level N!" written on it.
                    string message = $"Congrats on getting to level {level}!";
                    PendingGiftBox box;

                    PoolItem? item = null;
                    // Items only when the item tables are switched on (their bundles are missing
                    // from the CDN, so they render blank); otherwise every level pays tokens.
                    if (reward > 0 && ConfigCampus.RewardItemsEnabled)
                    {
                        owned ??= PlayerDB.GetOwnedAvatarItemKeys(playerId);
                        item = RollClothing(reward, owned);
                        if (item != null) owned.Add(PlayerDB.NormalizeAvatarItemDesc(item.AvatarItemDesc));
                        else Console.WriteLine($"[levelrewards] {playerId} owns every rarity-{reward} item - level {level} pays tokens");
                    }

                    if (item != null)
                    {
                        box = new PendingGiftBox
                        {
                            GiftContext = GiftContextLevelUp,
                            Message = message,
                            FromAccountId = 0,
                            AvatarItemDesc = item.AvatarItemDesc,
                            AvatarItemType = item.AvatarItemType,
                            FriendlyName = item.FriendlyName,
                            GiftDropId = item.GiftDropId,
                            Rarity = item.Rarity
                        };
                    }
                    else
                    {
                        long tokens = TokensForLevel(level);
                        box = new PendingGiftBox
                        {
                            GiftContext = GiftContextLevelUp,
                            Message = message,
                            FromAccountId = 0,
                            FriendlyName = $"Level {level} Box",   // the box's name, as the real game labels it
                            // Empty = the client's own progression icon (see
                            // ConfigCampus.UseClientProgressionIcons).
                            ThumbnailImageName = ConfigCampus.UseClientProgressionIcons ? "" : QuestRewards.TokenThumbnail,
                            CurrencyType = (int)CurrencyType.RecCenterTokens,
                            Currency = tokens,
                            Rarity = QuestRewards.TokenRarity(tokens),
                            PayOnOpen = true
                        };
                    }

                    if (!PlayerDB.AddPendingGiftBoxFull(playerId, box))
                    {
                        Console.WriteLine($"[levelrewards] {playerId}: could not store the level {level} box");
                        continue;
                    }
                    GiftBoxes.Push(playerId, box, GiftBoxes.FrameReceivedImmediate, $"level {level} gift");
                    Console.WriteLine($"[levelrewards] {playerId} reached level {level}: " +
                                      (item != null ? $"'{item.FriendlyName}' (rarity {item.Rarity})" : $"{box.Currency} tokens") +
                                      $" in box {box.NumericId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[levelrewards] {playerId} level {level} grant failed: {ex.Message}");
                }
            }
        }
    }
}
