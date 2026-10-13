namespace Campus_live_rewrite.api.server.Classes
{
    /// <summary>
    /// Currency identifiers, taken from the client's own enum (il2cpp dump, 2026-08-24).
    ///
    /// These are NOT arbitrary - the value is the path segment the client uses when
    /// asking for a balance, e.g. /api/storefronts/v4/balance/2 is RecCenterTokens and
    /// /api/storefronts/v4/balance/500 is RoomieCredits. Both appear in every session log.
    ///
    /// Kept as a real enum so reward/balance code stops passing bare ints around.
    /// NOTE: this is the BALANCES enum. Game rewards use a different one.
    /// </summary>
    public enum CurrencyType
    {
        Invalid            = 0,
        LaserTagTickets    = 1,
        RecCenterTokens    = 2,
        LostSkullsGold     = 100,
        DraculaSilver      = 101,
        RecRoyale_Season1  = 200,
        RoomCurrency       = 300,
        RoomInventoryItem  = 301,
        ProgressionEvent   = 400,
        RoomieCredits      = 500,
        PaintballPaint     = 600,
    }
}
