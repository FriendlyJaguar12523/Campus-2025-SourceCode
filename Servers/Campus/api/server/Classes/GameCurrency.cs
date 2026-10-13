namespace Campus_live_rewrite.api.server.Classes
{
    /// <summary>
    /// Which currency a game mode pays out in.
    ///
    /// The client asks /api/storefronts/v2/balance to add currency and NAMES the currency
    /// itself. Trusting that is how a completed Golden Trophy match paid out 1000 Dracula
    /// Silver - Crescendo's currency. The room the player is actually in decides now; the
    /// client's CurrencyType is only a fallback when the room is unknown.
    ///
    /// Room names come from the live server (Room_server/rooms/search).
    /// </summary>
    public static class GameCurrency
    {
        /// <summary>
        /// The one balance bucket this server keeps per currency, on the wire as "Platform"
        /// (BalancePlatform.NonPurchasedNotUsableInP2P = -2, the reference's ALL_PLATFORMS). The
        /// client holds a balance per (CurrencyType, Platform) and SUMS them, so every balance
        /// frame - the GET, a purchase response, a balance add, the 61 push - has to name this
        /// same bucket; one that says 0 (SteamPurchased) makes a phantom bucket that is added to
        /// the real one for the rest of the session.
        /// </summary>
        public const int AllPlatformsBucket = -2;

        /// <summary>Currency for a room name, or null when the room has no special currency.</summary>
        public static CurrencyType? ForRoomName(string? roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName)) return null;

            // Room instance names arrive as "^RoomName".
            string n = roomName.TrimStart('^').Trim();

            if (n.Equals("Crescendo", StringComparison.OrdinalIgnoreCase))
                return CurrencyType.DraculaSilver;

            if (n.Equals("IsleOfLostSkulls", StringComparison.OrdinalIgnoreCase))
                return CurrencyType.LostSkullsGold;

            if (n.StartsWith("RecRoyale", StringComparison.OrdinalIgnoreCase))
                return CurrencyType.RecRoyale_Season1;

            if (n.StartsWith("Paintball", StringComparison.OrdinalIgnoreCase))
                return CurrencyType.PaintballPaint;

            if (n.Equals("LaserTag", StringComparison.OrdinalIgnoreCase))
                return CurrencyType.LaserTagTickets;

            // GoldenTrophy and everything else pay in Rec Center Tokens - which is what the
            // Golden Trophy storefront (StorefrontType 102) actually prices its items in.
            return CurrencyType.RecCenterTokens;
        }

        /// <summary>
        /// STRICT balance-room whitelist: the ONLY rooms in which POST v2/balance may mint
        /// currency, and which currency each mints. Anything not listed returns null and mints
        /// nothing. Rec Center Tokens are deliberately absent - they are server/admin only.
        ///
        ///   Crescendo         -> DraculaSilver   (101)
        ///   IsleOfLostSkulls  -> LostSkullsGold  (100)
        ///   LaserTag          -> LaserTagTickets (1)
        /// </summary>
        public static CurrencyType? ForBalanceRoom(string? roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName)) return null;

            // Room instance names arrive as "^RoomName".
            string n = roomName.TrimStart('^').Trim();

            if (n.Equals("Crescendo", StringComparison.OrdinalIgnoreCase))
                return CurrencyType.DraculaSilver;

            if (n.Equals("IsleOfLostSkulls", StringComparison.OrdinalIgnoreCase))
                return CurrencyType.LostSkullsGold;

            if (n.Equals("LaserTag", StringComparison.OrdinalIgnoreCase))
                return CurrencyType.LaserTagTickets;

            return null;
        }

        /// <summary>
        /// Whether a currency may be minted by a PLAYER-facing endpoint (v2/balance, game rewards).
        /// Only the three room currencies qualify. Rec Center Tokens (and every other bucket) are
        /// NOT player-grantable - they can only be granted server-side / from the admin panel.
        /// </summary>
        public static bool IsPlayerGrantable(int currencyType)
            => currencyType == (int)CurrencyType.DraculaSilver
            || currencyType == (int)CurrencyType.LostSkullsGold
            || currencyType == (int)CurrencyType.LaserTagTickets;

        /// <summary>
        /// Most a single award may grant, per currency. 0 = no cap.
        ///
        /// Crescendo pays per CHECKPOINT, and a checkpoint is worth 0..30,000 Silver -
        /// five checkpoints, so 150,000 is a full game. The cap is enforced per request so
        /// a bad (or forged) amount cannot mint a fortune in one call.
        /// </summary>
        /// <summary>
        /// Whether a currency is an account balance a purchase can debit. Invalid (0) never is;
        /// everything else the enum defines is a real balance bucket. Used by bulkpurchase to
        /// reject a bag posted against a currency there is no wallet to spend from (400).
        /// </summary>
        public static bool IsSpendable(int currencyType)
            => currencyType != (int)CurrencyType.Invalid
               && Enum.IsDefined(typeof(CurrencyType), currencyType);

        public static long MaxPerAward(CurrencyType currency)
        {
            switch (currency)
            {
                case CurrencyType.DraculaSilver: return 30000;   // per Crescendo checkpoint
                default: return 0;                               // uncapped
            }
        }

        /// <summary>Clamps an award to 0..MaxPerAward for that currency.</summary>
        public static long ClampAward(CurrencyType currency, long amount)
        {
            if (amount < 0) return 0;

            long max = MaxPerAward(currency);
            return (max > 0 && amount > max) ? max : amount;
        }
    }
}
