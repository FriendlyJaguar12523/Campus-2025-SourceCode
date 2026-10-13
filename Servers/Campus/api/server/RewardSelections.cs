using System.Collections.Concurrent;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The three-choice reward the client shows on the watch after a game/quest. POST
    /// /api/gamerewards/v1/request mints one (three offered drops) and pushes it over the
    /// notifications hub as "RewardSelectionReceived"; POST /api/gamerewards/v1/select consumes it
    /// and grants the chosen drop. The offered drops are recorded so select can verify the player
    /// is claiming one they were actually offered, and Consumed makes it single-use.
    ///
    /// In-memory + auto-increment id + a TTL, mirroring RoomInvites - a selection is picked within
    /// seconds of being offered, so it needn't survive a restart (the player just re-triggers it).
    /// </summary>
    public static class RewardSelections
    {
        public sealed class Selection
        {
            public int RewardSelectionId;
            public ulong AccountId;
            public string Message = "";
            public int GiftContext;
            public int RewardType;
            public List<QuestRewards.Reward> Drops = new();   // the three offered, in order
            public bool Consumed;
            /// <summary>The GiftDropId the player picked (0 for selections consumed before this existed).</summary>
            public int ChosenDropId;
            public DateTime CreatedAt = DateTime.UtcNow;
        }

        private static int _next = 0;
        private static readonly ConcurrentDictionary<int, Selection> _store = new();

        // Persisted: a selection offered right before a redeploy/restart used to vanish, so the
        // pick the player made seconds later was a 403 ("Failed to consume reward N"). Rows are
        // the Selection as JSON keyed by id; loaded once, written on create and on consume.
        public sealed class Row { [LiteDB.BsonId] public int Id { get; set; } public string Json { get; set; } = ""; public DateTime CreatedAt { get; set; } }
        private static readonly Lazy<LiteDB.LiteDatabase> _db = new(() => new LiteDB.LiteDatabase(Path.Combine("Data", "DBs", "RewardSelections.db")));
        private static bool _loaded;
        private static readonly object _loadLock = new();

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_loadLock)
            {
                if (_loaded) return;
                try
                {
                    var col = _db.Value.GetCollection<Row>("selections");
                    int max = 0, n = 0;
                    foreach (var r in col.FindAll())
                    {
                        var s = Newtonsoft.Json.JsonConvert.DeserializeObject<Selection>(r.Json);
                        if (s == null) continue;
                        _store[s.RewardSelectionId] = s;
                        if (s.RewardSelectionId > max) max = s.RewardSelectionId;
                        n++;
                    }
                    if (max > _next) _next = max;
                    if (n > 0) Console.WriteLine($"[gamereward] restored {n} reward selection(s), next id {_next + 1}");
                }
                catch (Exception ex) { Console.WriteLine($"[gamereward] could not restore selections: {ex.Message}"); }
                _loaded = true;
            }
        }

        private static void Persist(Selection s)
        {
            try
            {
                _db.Value.GetCollection<Row>("selections").Upsert(new Row { Id = s.RewardSelectionId, Json = Newtonsoft.Json.JsonConvert.SerializeObject(s), CreatedAt = s.CreatedAt });
            }
            catch (Exception ex) { Console.WriteLine($"[gamereward] could not persist selection {s.RewardSelectionId}: {ex.Message}"); }
        }
        // Matches the served Rewards.SelectionTimeout (48 h). The client runs an expiry timer of
        // (CreatedAt + timeout - its own server-time estimate) and AUTO-PICKS card 1 + closes the
        // watch when it fires; a PC whose clock/zone put "server time" 30+ min ahead of CreatedAt
        // saw the old 30-min window as already expired and had the reward chosen for it within
        // seconds of the offer. The window now outlives any timezone skew, so a selection must stay
        // redeemable here for as long as the client thinks it is open.
        private static readonly TimeSpan TTL = TimeSpan.FromHours(48);

        public static Selection Create(ulong accountId, string message, int giftContext, int rewardType, List<QuestRewards.Reward> drops)
        {
            EnsureLoaded();
            Prune();
            int id = System.Threading.Interlocked.Increment(ref _next);
            var s = new Selection
            {
                RewardSelectionId = id,
                AccountId = accountId,
                Message = message ?? "",
                GiftContext = giftContext,
                RewardType = rewardType,
                Drops = drops
            };
            _store[id] = s;
            Persist(s);
            return s;
        }

        public static Selection? Get(int id) { EnsureLoaded(); return _store.TryGetValue(id, out var s) ? s : null; }

        /// <summary>
        /// Mint a three-card reward selection for a player and push it to their watch - the
        /// same thing POST /api/gamerewards/v1/request does after a game. Tokens (10..1000) in the
        /// activity's box unless item rewards are enabled and the table knows the context.
        /// Returns the selection, or null when nothing could be offered.
        /// </summary>
        public static Selection? Offer(ulong accountId, int giftContext, string? modeName, string? message)
        {
            var owned = PlayerDB.GetOwnedAvatarItemKeys(accountId);
            bool known = QuestRewards.HasRewardsFor(giftContext, modeName);
            List<QuestRewards.Reward> drops;
            if (ConfigCampus.RewardItemsEnabled && known)
                drops = QuestRewards.PickDistinct(giftContext, modeName, 3, owned);
            else
            {
                // Watch-store clothing (2-4 stars) the player doesn't own, then tokens for the rest.
                int want = Math.Clamp(ConfigCampus.RewardWatchClothingCards, 0, 3);
                drops = QuestRewards.WatchClothingDrops(giftContext, want, owned);
                if (drops.Count < 3)
                    drops.AddRange(QuestRewards.TokenDrops(giftContext != 0 ? giftContext : QuestRewards.GiftContextGameRewardTokens)
                                               .OrderBy(_ => Random.Shared.Next()).Take(3 - drops.Count));
            }
            if (drops.Count != 3) return null;

            string selMsg = string.IsNullOrWhiteSpace(message) ? "Activity completed!" : message!;
            var sel = Create(accountId, selMsg, giftContext, 0, drops);
            var frame = Newtonsoft.Json.JsonConvert.SerializeObject(new WebsocketEvents.Reponse { Id = "RewardSelectionReceived", Msg = ToPayload(sel) });
            double delay = ConfigCampus.RewardSelectionPushDelaySeconds;
            Notifications.FireAndForget(System.Threading.Tasks.Task.Run(async () =>
            {
                if (delay > 0) await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(delay));
                await Notifications.SendToPlayer(accountId, frame);
            }), "RewardSelectionReceived");
            Console.WriteLine($"[gamereward] {accountId} offered reward selection {sel.RewardSelectionId} " +
                              $"ctx={giftContext} room='{modeName}' drops=[{string.Join(",", drops.Select(d => d.Id))}] in box {drops[0].Context}");
            return sel;
        }

        /// <summary>The caller's still-unclaimed selections, oldest first (GET /gamerewards/v1/pending).</summary>
        public static List<Selection> PendingFor(ulong accountId)
        {
            EnsureLoaded();
            Prune();
            return _store.Values.Where(s => s.AccountId == accountId && !s.Consumed)
                                .OrderBy(s => s.RewardSelectionId).ToList();
        }

        /// <summary>The selection as the client reads it - the RewardSelectionReceived frame's Msg
        /// and one entry of GET /gamerewards/v1/pending are this same shape.</summary>
        public static object ToPayload(Selection sel) => new
        {
            RewardSelectionId    = sel.RewardSelectionId,
            RewardType           = sel.RewardType,
            Message              = sel.Message,
            GiftContext          = sel.GiftContext,
            GiftDrop1            = QuestRewards.ToDrop(sel.Drops[0]),
            GiftDrop2            = QuestRewards.ToDrop(sel.Drops[1]),
            GiftDrop3            = QuestRewards.ToDrop(sel.Drops[2]),
            // The reference sends the third drop again under the subscriber key; with no
            // subscriber-only pool the two are the same drop.
            Subscriber_GiftDrop3 = QuestRewards.ToDrop(sel.Drops[2]),
            CreatedAt            = sel.CreatedAt.AddSeconds(ConfigCampus.RewardSelectionCreatedAtOffsetSeconds).ToString("o")
        };

        /// <summary>Mark consumed. Returns false if it was already consumed (single-use, race-safe).</summary>
        public static bool Consume(int id, int chosenDropId = 0)
        {
            if (!_store.TryGetValue(id, out var s)) return false;
            lock (s)
            {
                if (s.Consumed) return false;
                s.Consumed = true;
                s.ChosenDropId = chosenDropId;
            }
            Persist(s);
            return true;
        }

        private static void Prune()
        {
            var cutoff = DateTime.UtcNow - TTL;
            foreach (var kv in _store)
                if (kv.Value.CreatedAt < cutoff)
                {
                    _store.TryRemove(kv.Key, out _);
                    try { _db.Value.GetCollection<Row>("selections").Delete(kv.Key); } catch { }
                }
        }
    }
}
