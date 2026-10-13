namespace Campus_live_rewrite.api.server.Classes
{
    /// <summary>
    /// RewardType values the client sends to /api/gamerewards/v1/request and
    /// /api/avatar/v3/gifts/generate (observed in real client traffic, 2026-08).
    /// Sent as the STRING name in the form body, e.g. "RewardType=PostGameActivity".
    /// This is a partial list - extend as more are observed.
    /// </summary>
    public enum GameRewardType
    {
        Unknown = 0,
        PostGameActivity,     // end-of-game gift box
        ReferralReward,       // referral / gift generate
        FirstActivityOfDay,   // observed live: "rewardType=FirstActivityOfDay&Message=First Game of the Day"
    }

    /// <summary>
    /// What each reward type pays, and whether it is limited to once per day.
    ///
    /// The real grant table was never captured, so these amounts are our own - but the
    /// DAILY LIMIT is not cosmetic: FirstActivityOfDay is named for a once-a-day event
    /// and the client will happily request it again on the next activity. Without a gate
    /// it pays out every single time and is trivially farmed.
    /// </summary>
    public static class GameRewardRules
    {
        public static GameRewardType Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return GameRewardType.Unknown;
            return Enum.TryParse<GameRewardType>(raw, ignoreCase: true, out var t)
                ? t
                : GameRewardType.Unknown;
        }

        /// <summary>Amount awarded, in the currency returned by CurrencyFor.</summary>
        public static long AmountFor(GameRewardType type)
        {
            switch (type)
            {
                case GameRewardType.FirstActivityOfDay: return 100;
                case GameRewardType.PostGameActivity:   return 25;
                case GameRewardType.ReferralReward:     return 250;
                default:                                return 10;
            }
        }

        public static CurrencyType CurrencyFor(GameRewardType type) => CurrencyType.RecCenterTokens;

        /// <summary>True when this reward may only be claimed once per calendar day (UTC).</summary>
        public static bool IsOncePerDay(GameRewardType type)
            => type == GameRewardType.FirstActivityOfDay;
    }

    /// <summary>
    /// GiftContext ids seen in client traffic. These identify WHICH gift box the reward
    /// is for; the id is game/activity specific. Kept for reference - the reward request
    /// carries the raw int, not this enum.
    ///   4003 Golden      4503 Jumbotron   4013 Crimson     4103 Isle       4206 Crescendo
    ///   5001 Charades   10001 Bowling     1200 RecRoyale  12000 RecRally  12100 Showdown
    ///  12200 MyLittleMonster  12300 RunTheBlock  11007 StuntRunner
    ///   3010/3011 DiscGolf   2010-2016 Paintball maps
    /// </summary>
    public static class GiftContextIds
    {
        public const int GoldenGiftBox = 4003;
        public const int JumbotronGiftBox = 4503;
        public const int CrimsonGiftBox = 4013;
        public const int IsleGiftBox = 4103;
        public const int CrescendoGiftBox = 4206;

        /// <summary>
        /// Which currency a gift box pays out in.
        ///
        /// Each box belongs to a game, and those games have their own currency: the Isle
        /// boxes pay Lost Skulls Gold, Crescendo pays Dracula Silver, Paintball pays
        /// Paint, and so on. Anything without a game-specific currency falls back to
        /// Rec Center Tokens.
        ///
        /// This is a reasonable mapping rather than a captured one - rec.net's real
        /// payout table was never observed. It is easy to change: the amount is chosen
        /// in the admin panel, and this only decides WHICH currency it lands in.
        /// </summary>
        public static CurrencyType CurrencyForGiftBox(int giftContext)
        {
            switch (giftContext)
            {
                case 4103:                      // Isle
                    return CurrencyType.LostSkullsGold;

                case 4206:                      // Crescendo
                    return CurrencyType.DraculaSilver;

                case 1200:                      // Rec Royale
                    return CurrencyType.RecRoyale_Season1;

                case 2010: case 2011: case 2012: // Paintball maps
                case 2013: case 2014: case 2015:
                case 2016:
                    return CurrencyType.PaintballPaint;

                default:
                    return CurrencyType.RecCenterTokens;
            }
        }
    }
}
