using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;
using Newtonsoft.Json;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// The gift box as the client reads it - ONE shape for the GET /api/avatar/v2/gifts list and
    /// for the hub frames that announce a box (GiftPackageReceived 30 / ReceivedImmediate 31 /
    /// RewardSelectionReceived 32, which all carry the same GiftPackagePayload). Built off the
    /// stored PendingGiftBox so the list and the pop-up can never disagree about what a box holds.
    /// </summary>
    public static class GiftBoxes
    {
        public const string FrameReceived          = "30";
        public const string FrameReceivedImmediate = "31";
        public const string FrameRewardSelection   = "32";

        /// <summary>Does the box hold anything the client can show? Currency and XP count: a
        /// token box or a plain XP box has no item and is still a real reward.</summary>
        public static bool HasContent(PendingGiftBox g) =>
            g.GiftDropId != 0
            || !string.IsNullOrWhiteSpace(g.CustomAvatarItemId)
            || !string.IsNullOrWhiteSpace(g.AvatarItemDesc)
            || !string.IsNullOrWhiteSpace(g.ConsumableItemDesc)
            || !string.IsNullOrWhiteSpace(g.EquipmentPrefabName)
            || !string.IsNullOrWhiteSpace(g.EquipmentModificationGuid)
            || (g.Currency > 0 && g.CurrencyType != 0)
            || g.Xp > 0;

        /// <summary>
        /// An XP-only box: no item, no currency. The client classifies a box in this order -
        /// currency, equipment, consumable, AVATAR ITEM, else XP - and the avatar-item test is a
        /// null check, not an empty check: an XP box sent with AvatarItemDesc "" was read as an
        /// outfit item with nothing to load ("Can't load item. It's saved in your Dorm mirror.").
        /// So an XP box's AvatarItemDesc goes out as JSON null.
        /// </summary>
        public static bool IsXpOnly(PendingGiftBox g) =>
            g.Xp > 0
            && string.IsNullOrWhiteSpace(g.AvatarItemDesc) && string.IsNullOrWhiteSpace(g.ConsumableItemDesc)
            && string.IsNullOrWhiteSpace(g.EquipmentPrefabName) && string.IsNullOrWhiteSpace(g.EquipmentModificationGuid)
            && !(g.Currency > 0 && g.CurrencyType != 0);

        /// <summary>Does the box hold an AVATAR item (legacy descriptor or custom item)?</summary>
        public static bool IsAvatarContent(PendingGiftBox g) =>
            !string.IsNullOrWhiteSpace(g.AvatarItemDesc) || !string.IsNullOrWhiteSpace(g.CustomAvatarItemId);

        // The same null rule for every non-avatar box, not only the XP one: a skin / consumable /
        // token box with AvatarItemDesc "" and AvatarItemType 0 was read as an avatar item with
        // nothing to load ("Can't load item. It's saved in your Dorm mirror.") - the real store
        // rows for those carry AvatarItemType null (see StoreCatalog.AvatarTypeFor).
        private static object? AvatarDescFor(PendingGiftBox g) => string.IsNullOrWhiteSpace(g.AvatarItemDesc) ? null : g.AvatarItemDesc;
        private static int? AvatarTypeFor(PendingGiftBox g) => IsAvatarContent(g) ? g.AvatarItemType : (int?)null;

        // ---- package signature -------------------------------------------------------------
        // A box spawned into a shared room is handed to the OTHER clients over Photon, and they
        // judge it by its signature before showing what it holds ("Bad signature for remote gift
        // package" in their logs, and a blank box). The reference left Signature null / false,
        // which is exactly what fails that check. Sign every package the server hands out:
        // HMAC-SHA256 over the fields that identify the box, base64 like the real one, and
        // IsSignatureValid true so a remote client treats it as server-vouched.
        private static readonly byte[] SigKey = System.Text.Encoding.UTF8.GetBytes(
            "campus-gift-" + (Environment.GetEnvironmentVariable("CAMPUS_GIFT_SIG_KEY") ?? "9f1c3e7a-box"));
        public static string SignatureFor(object id, ulong playerId, ulong fromPlayerId, string? friendly, string? message, long currency, int currencyType)
        {
            string canon = $"{id}|{playerId}|{fromPlayerId}|{friendly}|{message}|{currency}|{currencyType}";
            using var h = new System.Security.Cryptography.HMACSHA256(SigKey);
            return Convert.ToBase64String(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canon)));
        }
        public static string SignatureFor(PendingGiftBox g, ulong ownerId) =>
            SignatureFor(g.NumericId != 0 ? (object)g.NumericId : g.Id, ownerId, g.FromAccountId, g.FriendlyName, g.Message, ClampCurrency(g.Currency), g.CurrencyType);

        // The client reads Currency as a 32-bit int: a box minted with 9999999999999 tokens made the
        // WHOLE gifts list unparseable ("Malformed Response" on GET /api/avatar/v2/gifts) and no box
        // showed at all. Never serialize more than fits.
        public static long ClampCurrency(long c) => c > int.MaxValue ? int.MaxValue : (c < 0 ? 0 : c);

        /// <summary>The GET /gifts entry (reference: toGiftContent + the stored id/context).</summary>
        public static object ToGiftContent(PendingGiftBox g, ulong ownerId = 0) => new
        {
            Id = g.NumericId != 0 ? (object)g.NumericId : g.Id,
            // Whose box it is. The reference's GiftPackage carries PlayerId (with a null
            // Signature / false IsSignatureValid - a server-minted box is never signed for
            // peer transfer), and a box spawned into a shared room is judged local-or-remote by
            // it. Without it every box in the room looked like the viewer's own.
            PlayerId = ownerId,
            Signature = SignatureFor(g, ownerId),
            IsSignatureValid = true,
            CustomAvatarItemId = string.IsNullOrWhiteSpace(g.CustomAvatarItemId) ? null : g.CustomAvatarItemId,
            GiftDropId = g.GiftDropId,
            GiftContext = g.GiftContext,
            Message = g.Message ?? "",
            FromPlayerId = g.FromAccountId,
            FromAccountId = g.FromAccountId,
            AvatarItemDesc = AvatarDescFor(g),
            ConsumableItemDesc = g.ConsumableItemDesc ?? "",
            ConsumableCount = string.IsNullOrWhiteSpace(g.ConsumableItemDesc) ? 0 : 1,
            ConsumableMappingId = 0,
            ConsumablePreExistingCount = 0,
            AvatarItemType = AvatarTypeFor(g),
            CurrencyType = g.CurrencyType,
            Currency = ClampCurrency(g.Currency),
            FriendlyName = g.FriendlyName ?? "",
            ThumbnailImageName = ItemImages.ThumbFor(g),   // the item's own picture, never a missing file (ItemImages)
            GiftRarity = g.Rarity,
            EquipmentPrefabName = g.EquipmentPrefabName ?? "",
            EquipmentModificationGuid = SkinOwnership.CanonicalGuid(g.EquipmentPrefabName, g.EquipmentModificationGuid) ?? "",   // the locker's spelling - one skin, not two
            Xp = g.Xp,
            PackageType = 0,
            Platform = -1,
            PlatformsToSpawnOn = -1,
            BalanceType = (int?)null,
            CreatedAt = g.CreatedAt
        };

        /// <summary>The hub payload announcing a box (reference: pushGiftReceived).</summary>
        public static object ToPackagePayload(PendingGiftBox g, ulong ownerId = 0) => new
        {
            Id = g.NumericId != 0 ? (object)g.NumericId : g.Id,
            PlayerId = ownerId,
            Signature = SignatureFor(g, ownerId),
            IsSignatureValid = true,
            CustomAvatarItemId = string.IsNullOrWhiteSpace(g.CustomAvatarItemId) ? null : g.CustomAvatarItemId,
            FromGiftDropId = 0,
            FromPlayerId = g.FromAccountId,
            ConsumableItemDesc = g.ConsumableItemDesc ?? "",
            AvatarItemDesc = AvatarDescFor(g),
            AvatarItemType = AvatarTypeFor(g),
            EquipmentPrefabName = g.EquipmentPrefabName ?? "",
            EquipmentModificationGuid = SkinOwnership.CanonicalGuid(g.EquipmentPrefabName, g.EquipmentModificationGuid) ?? "",   // the locker's spelling - one skin, not two
            CurrencyType = g.CurrencyType,
            Currency = ClampCurrency(g.Currency),
            Xp = g.Xp,
            Level = 0,
            Platform = -1,
            PlatformsToSpawnOn = -1,
            BalanceType = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket,
            GiftContext = g.GiftContext,
            GiftRarity = g.Rarity,
            FriendlyName = g.FriendlyName ?? "",
            ThumbnailImageName = ItemImages.ThumbFor(g),   // the item's own picture, never a missing file (ItemImages)
            Message = g.Message ?? ""
        };

        /// <summary>Announce a box over the hub so it pops without waiting for the next gifts poll.</summary>
        public static void Push(ulong playerId, PendingGiftBox box, string frameId, string why)
        {
            try
            {
                var frame = JsonConvert.SerializeObject(new WebsocketEvents.Reponse { Id = frameId, Msg = ToPackagePayload(box, playerId) });
                Notifications.FireAndForget(Notifications.SendToPlayer(playerId, frame), why);
            }
            catch (Exception ex) { Console.WriteLine($"[gifts] push {why} to {playerId} failed: {ex.Message}"); }
        }

        /// <summary>
        /// Credit a box's currency (if any) and push the new balance so the corner counter moves
        /// now (reference: creditCurrency + pushBalanceUpdate). The box only DISPLAYS the amount;
        /// this is where it is paid. Returns the resulting balance, or -1 when nothing was paid.
        /// </summary>
        public static long CreditCurrency(ulong playerId, PendingGiftBox box, string why)
        {
            if (box.Currency <= 0 || box.CurrencyType == 0) return -1;
            long balance = PlayerDB.AddBalance(playerId, box.CurrencyType, box.Currency);
            PushBalance(playerId, box.CurrencyType, balance, why);
            Console.WriteLine($"[{why}] {playerId} +{box.Currency} currency {box.CurrencyType} -> {balance}");
            return balance;
        }

        /// <summary>
        /// A box was just opened: pay its currency now if it was minted pay-on-open. Idempotent -
        /// the flag is cleared on the stored box so a replayed open can't pay twice.
        /// </summary>
        public static void PayOnOpen(ulong playerId, PendingGiftBox box)
        {
            bool payCurrency = box.PayOnOpen && box.Currency > 0 && box.CurrencyType != 0;
            bool payXp = box.XpOnOpen && box.Xp > 0;
            if (!payCurrency && !payXp) return;
            if (!PlayerDB.ClearGiftBoxPayOnOpen(playerId, box)) return;   // someone else got here first
            if (payCurrency)
            {
                long balance = PlayerDB.AddBalance(playerId, box.CurrencyType, box.Currency);
                PushBalance(playerId, box.CurrencyType, balance, "gift-open");
                Console.WriteLine($"[gifts] {playerId} opened box {box.NumericId}: +{box.Currency} currency {box.CurrencyType} -> {balance}");
            }
            if (payXp)
            {
                // AddXp pushes the level/XP bar live and hands out any level-up boxes crossed.
                var r = PlayerDB.AddXp(playerId, box.Xp);
                Console.WriteLine($"[gifts] {playerId} opened box {box.NumericId}: +{box.Xp} XP -> level {r?.Level} (+{r?.LevelsGained})");
            }
        }

        public static void PushBalance(ulong playerId, int currencyType, long balance, string why)
        {
            try
            {
                Notifications.FireAndForget(
                    Notifications.SendToPlayer(playerId,
                        JsonConvert.SerializeObject(WebsocketEvents.createResponse_balanceupdate(currencyType, balance))),
                    $"{why} balance update");
            }
            catch (Exception ex) { Console.WriteLine($"[{why}] balance push to {playerId} failed: {ex.Message}"); }
        }
    }
}
