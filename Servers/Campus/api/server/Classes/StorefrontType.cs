namespace Campus_live_rewrite.api.server.Classes
{
    /// <summary>
    /// Storefront ids, taken from the 2025 client's own enum (PNOBDCBDLNP, build 20250718.01).
    /// /api/storefronts/v3/giftdropstore/{id} uses these, and buyItem sends "StorefrontType": N.
    ///
    /// This used to be the 2023 client's list, which numbers everything from 1400 up
    /// differently: 2000-2004 were called PopUpShop_1..5 there, but in 2025 they are the
    /// PROGRESSION-EVENT pop-up shops, and the real pop-up shops are 1700 up. (Source:
    /// skills-main wire-shapes giftdropstore-storefront-type.md, carved from the 2025 metadata.)
    /// </summary>
    public enum StorefrontType
    {
        None = 0,
        LaserTag = 1,
        RecCenter = 2,
        Watch = 3,                      // the main token store

        Quest_LostSkulls = 100,         // Isle
        Quest_Dracula = 101,            // Crescendo
        Quest_GoldenTrophy = 102,       // Crimson
        Quest_CrimsonCauldron = 103,

        RecRoyale = 200,
        Cafe = 300,

        Paintball = 400,
        Paintball_River = 401,
        Paintball_Homestead = 402,
        Paintball_Quarry = 403,
        Paintball_ClearCut = 404,
        Paintball_Spillway = 405,
        Paintball_SunsetDriveIn = 406,

        Bowling = 500,
        StuntRunner = 600,
        DormMirror = 700,
        InventionStore = 800,
        RoomKeys = 900,

        Player_Profile = 1000,
        Room_Save = 1100,
        RoomCurrencies = 1200,
        Wishlist = 1300,
        Friendotron = 1400,
        RoomConsumables = 1500,
        RecRally = 1600,

        // The pop-up shop kiosks a room can place. A room asks for each one it has while
        // loading, so an unstocked one must answer promptly with an empty store.
        PopUpShop_1 = 1700,
        PopUpShop_2 = 1701,
        PopUpShop_3 = 1702,
        PopUpShop_4 = 1703,
        PopUpShop_5 = 1704,
        PopUpShop_6 = 1705,
        PopUpShop_7 = 1706,
        PopUpShop_8 = 1707,
        PopUpShop_9 = 1708,
        PopUpShop_10 = 1709,
        PopUpShop_11 = 1710,
        PopUpShop_12 = 1711,
        PopUpShop_13 = 1712,
        PopUpShop_14 = 1713,
        PopUpShop_15 = 1714,
        PopUpShop_16 = 1715,
        PopUpShop_17 = 1716,
        PopUpShop_18 = 1717,
        PopUpShop_19 = 1718,
        PopUpShop_20 = 1719,

        CustomAvatarItems = 1800,
        ProgressionEventPurchasableXpBoosts = 1900,

        // The Limited Time Challenge's shops (the Dorm Mirror customize screen asks for these).
        PopUpShop_ProgressionEvent_1 = 2000,
        PopUpShop_ProgressionEvent_2 = 2001,
        PopUpShop_ProgressionEvent_3 = 2002,
        PopUpShop_ProgressionEvent_4 = 2003,
        PopUpShop_ProgressionEvent_5 = 2004,

        RoomOffers = 2100,
        MyLittleMonsters = 2200,
        Destiny = 2201,
        RunTheBlock = 2202,
    }
}
