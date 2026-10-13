using Campus_live_rewrite.api.dbs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The end-of-game / quest reward pools, loaded once from Data/APIS/quest-rewards.json.
    ///
    /// The file is keyed by game-mode name (Dodgeball, Quest_Goblin_S, Soccer, ...); each value
    /// is a list of reward entries a play session can roll. Every entry carries a GiftContext
    /// (the "Context" field) - the same id the client sends to POST /api/gamerewards/v1/request -
    /// so a reward is picked by matching the requested GiftContext, falling back to the mode name.
    ///
    /// Content only: the entry describes WHAT to grant (avatar item / consumable / equipment +
    /// rarity + XP). The actual grant is a gift box (PlayerDB.AddPendingGiftBoxFull), which also
    /// puts the item in the wardrobe, plus any XP.
    /// </summary>
    public static class QuestRewards
    {
        public sealed class Reward
        {
            public int Id;
            public string? AvatarItemDesc;
            public string? ConsumableItemDesc;
            public string? EquipmentPrefabName;
            public string? EquipmentModificationGuid;
            public int CurrencyType;
            public long Currency;
            public int Xp;
            public int Level;
            public int GiftRarity;
            public string? Message;
            public int Context;
            public string Mode = "";
            /// <summary>The store row's picture for a Watch-clothing drop; sent only when that file exists here.</summary>
            public string? Thumbnail;
        }

        // ---- Watch-store clothing offered as a reward -----------------------------------------------
        // Rarity 10/20/30 = 2/3/4 stars (same scale as TokenRarity). Cached against the served JSON string.
        private static string? _wcJson;
        private static List<Newtonsoft.Json.Linq.JObject> _wc = new();

        private static List<Newtonsoft.Json.Linq.JObject> WatchClothing()
        {
            string? json = Campus_live_rewrite.api.server.StoreServe.ServedJson(Campus_live_rewrite.api.server.StoreGenerator.WatchStorefront);
            lock (_lock)
            {
                if (json != null && !ReferenceEquals(json, _wcJson))
                {
                    var list = new List<Newtonsoft.Json.Linq.JObject>();
                    foreach (var it in (Newtonsoft.Json.Linq.JObject.Parse(json)["StoreItems"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray())
                    {
                        if (it is not Newtonsoft.Json.Linq.JObject row || row["GiftDrop"] is not Newtonsoft.Json.Linq.JObject gd) continue;
                        int rar = (int?)gd["Rarity"] ?? -1;
                        if (rar != 10 && rar != 20 && rar != 30) continue;
                        if (string.IsNullOrWhiteSpace((string?)gd["AvatarItemDesc"])) continue;
                        if (!string.IsNullOrWhiteSpace((string?)gd["CustomAvatarItemId"]) || !string.IsNullOrWhiteSpace((string?)gd["EquipmentPrefabName"])
                            || !string.IsNullOrWhiteSpace((string?)gd["ConsumableItemDesc"])) continue;
                        if (((int?)gd["GiftDropId"] ?? 0) <= 0) continue;
                        list.Add(row);
                    }
                    _wc = list;
                    _wcJson = json;
                    Console.WriteLine($"[questrewards] {list.Count} Watch-store clothing rows (2-4 stars) can be offered as rewards");
                }
                return _wc;
            }
        }

        /// <summary>
        /// Up to <paramref name="count"/> distinct Watch-store clothing rewards (2-4 stars) the player does
        /// not already own. Fewer (or none) when they own nearly everything - the caller fills with tokens.
        /// </summary>
        public static List<Reward> WatchClothingDrops(int giftContext, int count, HashSet<string>? ownedKeys)
        {
            var result = new List<Reward>();
            if (count <= 0) return result;
            List<Newtonsoft.Json.Linq.JObject> pool;
            lock (_lock) pool = WatchClothing().ToList();
            var seen = new HashSet<string>();
            int guard = 0;
            while (result.Count < count && pool.Count > 0 && guard++ < 400)
            {
                Newtonsoft.Json.Linq.JObject row;
                lock (_lock) row = pool[_rng.Next(pool.Count)];
                var gd = (Newtonsoft.Json.Linq.JObject)row["GiftDrop"]!;
                string desc = (string?)gd["AvatarItemDesc"] ?? "";
                string key = PlayerDB.NormalizeAvatarItemDesc(desc);
                if (key.Length == 0 || !seen.Add(key)) continue;
                if (ownedKeys != null && ownedKeys.Contains(key)) continue;
                result.Add(new Reward
                {
                    Id = (int?)gd["GiftDropId"] ?? 0,
                    AvatarItemDesc = desc,
                    GiftRarity = (int?)gd["Rarity"] ?? 10,
                    Message = (string?)gd["FriendlyName"],
                    Xp = Campus_live_rewrite.api.server.Classes.Progress.GAME_REWARD_XP,
                    Context = giftContext != 0 ? giftContext : GiftContextGameRewards,
                    Mode = "WatchClothing",
                    Thumbnail = (string?)gd["ThumbnailImageName"]
                });
            }
            return result;
        }

        // GiftContext.GameRewards (50) is the XP box; 51 is its TOKENS variant - "for when a
        // reward pays currency instead of XP" (reference econ.app.ts). The client picks the box
        // art and the open sound off the context, so a token drop filed under 50 played the
        // plain XP box; that was the "wrong box sound" on token rewards.
        public const int GiftContextGameRewards = 50;
        public const int GiftContextGameRewardTokens = 51;

        // The picture a token drop / token box names. Empty by default (ConfigCampus.UseClientCurrencyImages):
        // the reference names none, and the client then draws its OWN token art - the picker card's
        // model has a built-in CurrencyImage. Naming our campus_tokens.png overrode that with a flat
        // custom PNG. (The old "blank without it" note dates from when token drops carried Currency 0.)
        public static string TokenThumbnail => ConfigCampus.UseClientCurrencyImages ? "" : ConfigCampus.LevelRewardImage;

        /// <summary>The box tier (star rarity) a token amount is presented at: 25 = 2-star ... 300 = 5-star.</summary>
        public static int TokenRarity(long amount) => amount switch
        {
            < 50  => 10,   // 2-star box
            < 150 => 20,   // 3-star
            < 500 => 30,   // 4-star
            _     => 50    // 5-star: 500+
        };

        public static bool IsTokens(Reward r) => r.Currency > 0 && r.CurrencyType != 0 && !IsItem(r);

        private static readonly object _lock = new();
        private static readonly Dictionary<string, List<Reward>> _byMode =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, List<Reward>> _byContext = new();
        private static readonly List<Reward> _all = new();
        private static readonly Random _rng = new();
        public static bool Loaded { get; private set; }

        private static readonly string File1 =
            System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "quest-rewards.json");

        public static void Load()
        {
            try
            {
                if (!System.IO.File.Exists(File1))
                {
                    Console.WriteLine($"[questrewards] {File1} not found - no game rewards loaded");
                    return;
                }

                var root = JObject.Parse(System.IO.File.ReadAllText(File1));
                int count = 0;
                foreach (var modeProp in root.Properties())
                {
                    if (modeProp.Value is not JArray arr) continue;
                    var list = new List<Reward>();
                    foreach (var e in arr.OfType<JObject>())
                    {
                        var r = new Reward
                        {
                            Id                        = (int?)e["Id"] ?? 0,
                            AvatarItemDesc            = (string?)e["AvatarItemDesc"],
                            ConsumableItemDesc        = (string?)e["ConsumableItemDesc"],
                            EquipmentPrefabName       = (string?)e["EquipmentPrefabName"],
                            EquipmentModificationGuid = (string?)e["EquipmentModificationGuid"],
                            CurrencyType              = (int?)e["CurrencyType"] ?? 0,
                            Currency                  = (long?)e["Currency"] ?? 0,
                            Xp                        = (int?)e["Xp"] ?? 0,
                            Level                     = (int?)e["Level"] ?? 0,
                            GiftRarity                = (int?)e["GiftRarity"] ?? 0,
                            Message                   = (string?)e["Message"],
                            Context                   = (int?)e["Context"] ?? 0,
                            Mode                      = modeProp.Name
                        };
                        list.Add(r);
                        _all.Add(r);
                        if (r.Context != 0)
                        {
                            if (!_byContext.TryGetValue(r.Context, out var cl))
                                _byContext[r.Context] = cl = new List<Reward>();
                            cl.Add(r);
                        }
                        count++;
                    }
                    _byMode[modeProp.Name] = list;
                }

                Loaded = true;
                Console.WriteLine($"[questrewards] loaded {count} reward entries across {_byMode.Count} modes " +
                                  $"({_byContext.Count} distinct gift contexts)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[questrewards] failed to load: {ex.Message}");
            }
        }

        private static readonly Dictionary<ulong, DateTime> _lastGift = new();

        /// <summary>
        /// At-most-once-per-cooldown gate for delivering a game-reward gift box, so a client that
        /// fires the reward request repeatedly cannot flood the gift inbox. Returns true (and marks
        /// the clock) when a box may be delivered now.
        /// </summary>
        public static bool TryClaimGiftCooldown(ulong playerId)
        {
            int cd = ConfigCampus.GameRewardGiftCooldownSeconds;
            if (cd <= 0) return true;
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (_lastGift.TryGetValue(playerId, out var last) && (now - last).TotalSeconds < cd)
                    return false;
                _lastGift[playerId] = now;
                return true;
            }
        }

        /// <summary>
        /// Roll up to <paramref name="n"/> DISTINCT rewards (by Id) for the three-choice reward
        /// selection. Draws from the GiftContext pool, then the room's mode pool, then the whole
        /// catalog; if fewer than n distinct entries exist it pads with repeats so the client always
        /// gets a full set. Empty only when nothing is loaded at all.
        /// </summary>
        // What a DUPLICATE item is worth when it is offered as tokens instead. Rec Room's own box
        // converts an item you already own into tokens; the client renders such a drop from its
        // Currency/CurrencyType, so a duplicate handed over with Currency=0 read as "0 Tokens" -
        // the "0, 0, 0 tokens" gift box players who already owned an activity's rewards kept seeing.
        public static long DuplicateTokenValue(int giftRarity) => giftRarity switch
        {
            <= 10 => 25,
            <= 20 => 50,
            <= 30 => 100,
            <= 40 => 200,
            _     => 300
        };

        /// <summary>A token drop standing in for an item the player already owns. Keeps the row's id
        /// so the selection can still be redeemed by it.</summary>
        public static Reward AsDuplicateTokens(Reward r) => new Reward
        {
            Id = r.Id,
            CurrencyType = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens,
            Currency = DuplicateTokenValue(r.GiftRarity),
            Xp = r.Xp,
            Level = r.Level,
            GiftRarity = TokenRarity(DuplicateTokenValue(r.GiftRarity)),
            Message = "Tokens",
            Context = GiftContextGameRewardTokens,
            Mode = r.Mode
        };

        /// <summary>Does the reward table know this activity (by gift context or by room/mode name)?</summary>
        public static bool HasRewardsFor(int giftContext, string? modeName)
        {
            lock (_lock)
            {
                if (giftContext != 0 && _byContext.TryGetValue(giftContext, out var c) && c.Count > 0) return true;
                if (!string.IsNullOrWhiteSpace(modeName))
                {
                    string n = modeName.TrimStart('^').Trim();
                    if (_byMode.TryGetValue(n, out var m) && m.Count > 0) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Three token choices, for an activity the reward table doesn't know (GiftContext 0 /
        /// unknown room). Drawing random items from EVERY activity's table here produced cards
        /// the client couldn't render (blank image, "Reward") - a Game Complete box with nothing
        /// readable in it. Tokens always render and always pay. Ids sit far above the table's.
        /// </summary>
        // The token ladder a card can land on, 10..1000. Weighted so the big payouts are rare:
        // the draw is skewed toward the low end (cube of a uniform roll), then snapped to a step
        // so cards read cleanly (10, 25, 50 ... 750, 1000). Three DISTINCT amounts per offer.
        private static readonly long[] TokenLadder = { 10, 15, 20, 25, 30, 40, 50, 60, 75, 100, 125, 150, 200, 250, 300, 400, 500, 750, 1000 };

        public static long RollTokenAmount()
        {
            double u;
            lock (_lock) u = _rng.NextDouble();
            // u^3 keeps ~87% of rolls in the bottom half of the ladder; 1000 lands ~1 in 60.
            int idx = (int)Math.Floor(Math.Pow(u, 3) * TokenLadder.Length);
            return TokenLadder[Math.Clamp(idx, 0, TokenLadder.Length - 1)];
        }

        /// <param name="boxContext">The GiftContext the box is filed under: an activity's own id
        /// (4206 Crescendo, 4003 Golden, ...) so the client draws THAT box, else the tokens box.</param>
        public static List<Reward> TokenDrops(int boxContext = GiftContextGameRewardTokens)
        {
            var picked = new HashSet<long>();
            int guard = 0;
            while (picked.Count < 3 && guard++ < 100) picked.Add(RollTokenAmount());
            while (picked.Count < 3) foreach (var l in TokenLadder) { if (picked.Count >= 3) break; picked.Add(l); }
            var amounts = picked.OrderBy(v => v).ToArray();
            var list = new List<Reward>();
            for (int i = 0; i < amounts.Length; i++)
                list.Add(new Reward
                {
                    // Ids encode the amount so a selection can be redeemed by id after a restart.
                    Id = 1_000_000 + (int)amounts[i],
                    CurrencyType = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens,
                    Currency = amounts[i],
                    Xp = Campus_live_rewrite.api.server.Classes.Progress.GAME_REWARD_XP,
                    GiftRarity = TokenRarity(amounts[i]),
                    Message = "Tokens",
                    Context = boxContext
                });
            return list;
        }

        public static bool IsItem(Reward r) =>
            !string.IsNullOrWhiteSpace(r.AvatarItemDesc) || !string.IsNullOrWhiteSpace(r.ConsumableItemDesc)
            || !string.IsNullOrWhiteSpace(r.EquipmentPrefabName) || !string.IsNullOrWhiteSpace(r.EquipmentModificationGuid);

        /// <summary>
        /// Pick up to <paramref name="n"/> distinct rewards for a context, preferring items the player
        /// does NOT own (reference: pickQuestReward draws "among the rows the player DOESN'T ALREADY
        /// OWN"). Only when the unowned pool runs dry are owned items offered - as token drops.
        /// </summary>
        public static List<Reward> PickDistinct(int giftContext, string? modeName, int n, HashSet<string>? ownedKeys)
        {
            var picked = PickDistinct(giftContext, modeName, n * 4);
            if (ownedKeys == null) return picked.Take(n).ToList();
            var fresh = new List<Reward>();
            var dupes = new List<Reward>();
            foreach (var r in picked)
            {
                bool owned = !string.IsNullOrWhiteSpace(r.AvatarItemDesc)
                             && ownedKeys.Contains(PlayerDB.NormalizeAvatarItemDesc(r.AvatarItemDesc));
                if (owned) dupes.Add(AsDuplicateTokens(r)); else fresh.Add(r);
            }
            var result = fresh.Take(n).ToList();
            foreach (var d in dupes) { if (result.Count >= n) break; result.Add(d); }
            return result;
        }

        public static List<Reward> PickDistinct(int giftContext, string? modeName, int n)
        {
            lock (_lock)
            {
                var pool = new List<Reward>();
                bool hintGiven = giftContext != 0 || !string.IsNullOrWhiteSpace(modeName);
                if (giftContext != 0 && _byContext.TryGetValue(giftContext, out var byCtx)) pool.AddRange(byCtx);
                if (!string.IsNullOrWhiteSpace(modeName))
                {
                    string nm = modeName.TrimStart('^').Trim();
                    if (_byMode.TryGetValue(nm, out var byMode)) pool.AddRange(byMode);
                }
                // Prefer the RIGHT mode's/context's pool, but if the hint didn't resolve to anything
                // (the client often sends giftContext=0 and a room name we don't have a pool for, e.g.
                // Rec Royale post-game), fall back to the whole catalog rather than offering NOTHING -
                // no reward at all is what made "the game-complete reward does nothing". A generic gift
                // beats an empty hand.
                if (pool.Count == 0) pool.AddRange(_all);
                _ = hintGiven;

                var result = new List<Reward>();
                if (pool.Count == 0) return result;

                var seen = new HashSet<int>();
                var shuffled = pool.OrderBy(_ => _rng.Next()).ToList();
                foreach (var r in shuffled)
                {
                    if (result.Count >= n) break;
                    if (seen.Add(r.Id)) result.Add(r);
                }
                // Pad with repeats if the pool had fewer than n distinct entries.
                for (int i = 0; result.Count < n && pool.Count > 0; i++)
                    result.Add(pool[i % pool.Count]);

                return result;
            }
        }

        /// <summary>An item drop's picture: the store row's own thumbnail when that file is hosted here, else ""
        /// (an avatar item with no name makes the client draw the item itself; a name that 404s draws nothing).</summary>
        public static string ItemThumbnail(Reward r) =>
            !string.IsNullOrWhiteSpace(r.Thumbnail) && ItemImages.Exists(r.Thumbnail) ? r.Thumbnail! : "";

        /// <summary>The client's GiftDrop wire shape for one offered reward choice (GameRewardDrop).</summary>
        // The drop as the client's reward picker / gift box reads it. Currency, CurrencyType and Xp
        // come off the row (reference: toQuestRewardDrop) - they used to be hardcoded 0 / absent,
        // which is what made every non-item drop show as "0 Tokens" and no box show its XP.
        public static object ToDrop(Reward r) => new
        {
            GiftDropId                = r.Id,
            FriendlyName              = IsTokens(r) ? $"{r.Currency} Tokens"
                                        : string.IsNullOrWhiteSpace(r.Message) ? "Reward" : r.Message,
            Tooltip                   = IsTokens(r) ? $"{r.Currency} Rec Center Tokens" : "",
            // A token drop names NO picture (null, as the real store's "60 Tokens" drop does): the
            // card then renders the game's own 3D token pile. Our PNG only while
            // UseClientCurrencyImages is off. An item drop draws its own thumbnail.
            ThumbnailImageName        = IsTokens(r) ? (string.IsNullOrEmpty(TokenThumbnail) ? null : TokenThumbnail)
                                        : ItemThumbnail(r),
            ConsumableItemDesc        = r.ConsumableItemDesc ?? "",
            AvatarItemDesc            = r.AvatarItemDesc ?? "",
            // NULL unless the drop IS an avatar item: the client routes a box on this field first,
            // and a 0 on a token / consumable / skin drop sends it looking for "avatar item type 0"
            // ("can't find avatar item"). Same rule as GiftBoxes / StoreCatalog.AvatarTypeFor.
            AvatarItemType            = string.IsNullOrWhiteSpace(r.AvatarItemDesc) ? (int?)null : 0,
            EquipmentPrefabName       = r.EquipmentPrefabName ?? "",
            EquipmentModificationGuid = r.EquipmentModificationGuid ?? "",
            IsQuery                   = false,
            Unique                    = false,
            SubscribersOnly           = false,
            Rarity                    = r.GiftRarity,
            // Items + XP only: tokens/currency are server/admin (see GameCurrency.IsPlayerGrantable).
            CurrencyType              = r.CurrencyType,
            Currency                  = r.Currency,
            Xp                        = r.Xp > 0 ? r.Xp : Campus_live_rewrite.api.server.Classes.Progress.GAME_REWARD_XP,
            Context                   = r.Context,
            // The rest of the real GiftDrop shape (store dump, GiftDropId 4014 "60 Tokens").
            // ItemSetId was 1 here - a set id sends the card's imposter renderer after "item set 1",
            // which doesn't exist, so a token card with no named picture drew nothing (blank tiles).
            ItemSetId                 = (int?)null,
            ItemSetFriendlyName       = "",
            ItemCount                 = 1,
            AvatarItemId              = 0,
            EquipmentItemId           = 0,
            CustomAvatarItemId        = (string?)null,
            TagList                   = ""
        };

        /// <summary>Roll a random reward for a GiftContext, else for a mode name, else null.</summary>
        public static Reward? Pick(int giftContext, string? modeName = null)
        {
            lock (_lock)
            {
                if (giftContext != 0 && _byContext.TryGetValue(giftContext, out var byCtx) && byCtx.Count > 0)
                    return byCtx[_rng.Next(byCtx.Count)];

                if (!string.IsNullOrWhiteSpace(modeName))
                {
                    string n = modeName.TrimStart('^').Trim();
                    if (_byMode.TryGetValue(n, out var byMode) && byMode.Count > 0)
                        return byMode[_rng.Next(byMode.Count)];
                }
                return null;
            }
        }
    }
}
