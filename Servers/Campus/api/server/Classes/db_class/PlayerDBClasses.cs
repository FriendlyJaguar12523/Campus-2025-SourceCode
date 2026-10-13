using System;
using System.Text.Json.Serialization;
using LiteDB;

namespace Campus_live_rewrite.api.server.Classes.db_class
{
    public class account_data
    {
        [JsonPropertyOrder(1)]
        public ulong accountId { get; set; }
        [JsonPropertyOrder(2)]
        public string? username { get; set; }
        [JsonPropertyOrder(3)]
        public string? displayName { get; set; }
        [JsonPropertyOrder(4)]
        public string? profileImage { get; set; }
        [JsonPropertyOrder(5)]
        public bool? isJunior { get; set; }
        [JsonPropertyOrder(6)]
        public long platforms { get; set; }
        [JsonPropertyOrder(7)]
        public long personalPronouns { get; set; }
        [JsonPropertyOrder(8)]
        public long identityFlags { get; set; }
        [JsonPropertyOrder(9)]
        public DateTime createdAt { get; set; }
        [JsonPropertyOrder(10)]
        public bool isMetaPlatformBlocked { get; set; } = false;
    }
    public class setting_data
    {
        public ulong accountId { get; set; }
        public string Key { get; set; }
        public string Val { get; set; }
    }

    public class player_data
    {
        public ulong id { get; set; }
        public ulong playerid { get; set; }
        public Platforms platform { get; set; }
        public List<string>? deviceId { get; set; }
        // Device bindings WITH the platform each was seen on. The flat deviceId list above stays
        // the source of truth for the anti-alt "one account per device" check; this parallel list
        // adds the platform so the account lock can enforce "one PC + one Quest headset" (see
        // AccountLock.TryEnforceAndBind). Null on legacy rows; populated lazily on next login.
        public List<mDeviceBind>? DeviceBindings { get; set; }
        // Where the account was made: true = in-game (the client's first-launch create_account),
        // false = the website. Null on rows older than the flag - see PlayerDB.OriginOf for how
        // those are classified. Drives the one-game-account-plus-one-website-account-per-device
        // rule (ConfigCampus.MaxAccountsPerDevice / AccountLock.CanShareSlot).
        public bool? CreatedInGame { get; set; }
        public string? authtoken { get; set; }
        public string? password { get; set; } 
        public player_info? player { get; set; }
        public player_extra? player_Extra { get; set; }
        public DevFlag dev_Flag { get; set; }
    }

    public enum Platforms
    {
        All = -1,
        Steam,
        Oculus,
        PlayStation,
        Xbox,
        RecNet,
        IOS,
        GooglePlay,
        Standalone
    }

    public class player_extra
    {
        // Equipment skins this player owns, as "PrefabName|ModificationGuid". Null = none (the
        // tools' default looks). Devs ignore this and get the whole catalog (the dev locker).
        public List<string>? EquipmentSkins { get; set; }

        public ulong? DormRoomID { get; set; }
        public List<string>? IpAddresses { get; set; }
        public ulong DiscordId { get; set; }
        // The linked Discord user's tag/username, stored purely so the website Settings > Discord
        // tab can show "Linked as <name>" instead of a bare numeric id. Set by the /link bot flow.
        public string? DiscordUsername { get; set; }
        // Name lock (admin panel > Edit Player). While true the player cannot change their own
        // @name or display name - in-game, on the website, or through the first-run onboarding
        // path. Admin edits still go through. Absent/false on every existing record.
        public bool NameLocked { get; set; }
        // Moderator locks on the rest of the profile (admin panel, Edit Player): the player can't
        // change it themselves - in-game, on the website or through the Discord bot. Admin edits
        // are never gated. See PlayerDB.IsProfileLocked.
        public bool BioLocked { get; set; }
        public bool PictureLocked { get; set; }
        public bool BannerLocked { get; set; }
        public string? Bio { get; set; }
        public string? avatar { get; set; }
        public List<avatar_data_saved>? SavedOutfits { get; set; }
        public Moderation_Detail.ModerationBlockDetails? ModerationBlockDetails { get; set; }
        // MIC BAN (token claim rn.privilege BanVChat - see PlayerDB "MIC BAN / ROOM-CHAT BAN").
        // Kept apart from ModerationBlockDetails so it never overwrites a real ban/kick (or the
        // reverse) and never counts as "banned" for login/matchmaking.
        public Moderation_Detail.ModerationBlockDetails? VoiceBan { get; set; }
        public List<ulong>? CheeredRooms { get; set; }
        public List<ulong>? FavoritedRooms { get; set; }
        // Last time each once-per-day game reward was claimed, keyed by reward type name.
        // Stored as a UTC date string (yyyy-MM-dd) so "already claimed today" is a plain
        // string compare and cannot drift with timezones.
        public Dictionary<string, string> DailyRewardClaims { get; set; } = new();

        // Gift boxes waiting to be opened. Served by /api/avatar/v2/gifts.
        public List<PendingGiftBox> PendingGiftBoxes { get; set; } = new();

        // Avatar items this account actually OWNS, as raw AvatarItemDesc strings.
        //
        // Until this existed /api/avatar/v4/items handed the entire 3061-item catalog to
        // everyone, so every account owned every item. Non-developers now start from
        // Data/APIS/items/DefaultAvatarItems.json and earn the rest; developers keep the
        // full catalog (the dev locker). See PlayerDB.OwnedAvatarItemDescs.
        //
        // null means "never granted the starter set" - distinct from an empty list, which
        // would mean a player who somehow owns nothing. The grant is lazy on first read so
        // existing accounts do not need a migration pass.
        public List<string>? AvatarItems { get; set; }
        /// <summary>Custom avatar items (UGC) this player bought or was gifted - the ids.</summary>
        public List<string>? CustomItems { get; set; }
        // Consumables the player owns: ConsumableItemDesc -> count left. Filled by gift boxes /
        // store buys; /api/consumables/v2/getUnlocked lists only these (devs keep the whole file).
        public Dictionary<string, int>? Consumables { get; set; }

        // Per-currency balances, keyed by CurrencyType (see api/server/Classes/CurrencyType.cs):
        // 1=LaserTagTickets, 2=RecCenterTokens, 100=LostSkullsGold, 101=DraculaSilver,
        // 300=RoomCurrency, 500=RoomieCredits, etc. Value is the amount held.
        // Persisted with the rest of player_extra, so balances survive restarts.
        public Dictionary<int, long> Balances { get; set; } = new();

        // ---- Quests / rewards progress (see APIController objectives + challenge, RewardsDB) ----
        // Daily objectives: the client owns the objective CATALOG (what Group/Index means, its
        // target and reward); the server only stores per-(Group,Index) progress numbers and a
        // once-only reward latch. Weekly challenges: only completion flags per (map,challenge),
        // plus which challenge maps have already paid their gift.
        public List<ObjectiveProgress> Objectives { get; set; } = new();
        public List<ObjectiveGroupProgress> ObjectiveGroups { get; set; } = new();
        public List<ChallengeStatus> ChallengeStatuses { get; set; } = new();
        public List<int> ClaimedChallengeMaps { get; set; } = new();

        public int sub_count { get; set; } = 0;
        public List<ulong> SubbedTo { get; set; } = new();
        public bool IsRecentRoomHistoryVisible { get; set; } = true;
        public Influencer? Influencer { get; set; }
        public CustomAvatarItems CustomAvatarItems { get; set; } = new CustomAvatarItems();
        public MyRoomData MyRoomData { get; set; } = new MyRoomData();
        public List<MessageData>? Messages { get; set; }
        public PlayerPhotoTaggingSetting PlayerPhotoTaggingSetting { get; set; } = PlayerPhotoTaggingSetting.Anyone;

    }

    public enum PlayerPhotoTaggingSetting
    {
        Anyone,
        Friends,
        NoOne
    }

    public class SetPlayerPhotoTaggingSettingRequest
    {
        public PlayerPhotoTaggingSetting Setting { get; set; }
    }

    public class MyRoomData
    {
        public List<string> owned_room_keys { get; set; } = new List<string>();
        public List<PlayerData> PlayerData { get; set; } = new List<PlayerData>();
        public List<My_Room_currencies> Roomcurrencies { get; set; } = new List<My_Room_currencies>();
    }

    public class PlayerData
    {
        public ulong RoomId { get; set; }
        public string Data { get; set; }
    }

    public class My_Room_currencies
    {
        public long RoomId { get; set; }
        public string Id { get; set; }
        public long Value { get; set; }
    }

    public class SubscribedToEntry
    {
        public ulong PlayerId { get; set; }
    }

    public class avatar_data_saved
    {
        public ulong Slot { get; set; }
        public string? Avatar { get; set; }
        public string? ImageName { get; set; }

        public List<CustomAvatarItemEntry>? CustomAvatarItems { get; set; }

        public string? FaceFeatures { get; set; }
        public string? HairColor { get; set; }
        public string? Name { get; set; }
        public string? SkinColor { get; set; }
        public string? OutfitSelections { get; set; }
        public string? OutfitSelectionsV2 { get; set; }
        public string? PreviewImageName { get; set; }
    }

    public class CustomAvatarItemEntry
    {
        public string? CustomAvatarItemId { get; set; }
        public int BodyPart { get; set; }
    }

    public class avatar_data
    {
        public string? OutfitSelections { get; set; }
        public string? FaceFeatures { get; set; }
        public string? SkinColor { get; set; }
        public string? HairColor { get; set; }

    }

    /// <summary>
    /// A gift box granted to a player but not yet opened.
    /// GiftContext identifies which box (4003 Golden, 4503 Jumbotron, ... see
    /// api/server/Classes/GameRewardType.cs -> GiftContextIds).
    /// </summary>
    public class PendingGiftBox
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        /// <summary>How many of a consumable the box holds (the store row's ItemCount; 1 when unknown).</summary>
        public int ConsumableCount { get; set; }
        public int GiftContext { get; set; }
        public string? Message { get; set; }
        public ulong FromAccountId { get; set; }
        public System.DateTime CreatedAt { get; set; } = System.DateTime.UtcNow;
        public bool Opened { get; set; }

        // Int32-safe numeric id. The client's gift package field is a 32-bit int: a
        // 13-digit millisecond timestamp overflows it, the package fails to deserialize,
        // and the box renders the "Update your game" placeholder while the item never
        // lands live. (Learned by the 2023 server - D:\Puck\Campus-Server.)
        public int NumericId { get; set; }

        // What the box actually contains, when it came from a store purchase.
        //
        // AvatarItemDesc is stored RAW - exactly as the store file writes it, e.g.
        // "4ee2133d-...,,,". The client parses this as guid,combination,combination,
        // combination; hand it a bare guid with the empty fields stripped and it cannot
        // read it, so the item never reaches the locker. 746 of the 968 Watch items use
        // that trailing-comma form, which is why Laser Tag purchases always worked and
        // Watch ones did not.
        public string? AvatarItemDesc { get; set; }
        public string? ConsumableItemDesc { get; set; }
        public int AvatarItemType { get; set; }
        public int GiftDropId { get; set; }
        public string? FriendlyName { get; set; }
        public int Rarity { get; set; }
        public string? EquipmentPrefabName { get; set; }
        public string? EquipmentModificationGuid { get; set; }
        /// <summary>A UGC item (custom avatar item id) the box carries - a bought/gifted custom clothing item.</summary>
        public string? CustomAvatarItemId { get; set; }
        public bool Anonymous { get; set; }

        // What a NON-item box pays: tokens (a duplicate reward converted, a level-up's token
        // tier) and/or XP. These are DISPLAY copies - the balance and the XP are banked when the
        // box is minted, not when it is opened - but the box has to carry them or the gifts list
        // and the open animation read "0 Tokens" / no XP, which is what players saw.
        public int CurrencyType { get; set; }
        public long Currency { get; set; }
        public int Xp { get; set; }
        // True = the Currency above is credited when the box is OPENED (consume), so the number
        // moves as the box pops rather than silently beforehand. False = already credited.
        public bool PayOnOpen { get; set; }
        // True = the Xp above is banked when the box is opened (an admin XP box). Game-reward boxes
        // carry Xp for display only (already banked at pick), so they leave this false.
        public bool XpOnOpen { get; set; }
        // Image the box / picker shows for a drop that has no item of its own (the token coin).
        // A plain file name under Data/Images, served by /img/<name> like a consumable's.
        public string? ThumbnailImageName { get; set; }
    }

    /// <summary>Per-player progress for one daily objective, keyed by (Group, Index).</summary>
    public class ObjectiveProgress
    {
        public long Group { get; set; }
        public int Index { get; set; }
        public double Progress { get; set; }
        public double VisualProgress { get; set; }
        public bool IsCompleted { get; set; }
        // Latches true the first time the objective completes and never unlatches, so a replayed
        // or double-fired completion only ever pays its reward once.
        public bool HasClaimedReward { get; set; }
    }

    /// <summary>Per-player state of one objective group.</summary>
    public class ObjectiveGroupProgress
    {
        public long Group { get; set; }
        public bool IsCompleted { get; set; }
        public string? ClearedAt { get; set; }
    }

    /// <summary>Per-player completion of one weekly challenge within a rotation (map).</summary>
    public class ChallengeStatus
    {
        public int ChallengeMapId { get; set; }
        public int ChallengeId { get; set; }
        public bool Complete { get; set; }
        public string? UpdatedAt { get; set; }
    }

    public class player_info
    {
        public ulong Id { get; set; }
        public string? Username { get; set; }
        public string? DisplayName { get; set; }

        public string? DisplayEmoji { get; set; }

        public string? Email { get; set; }
        // ISO-8601 date the player entered at website signup. Used to drive account_data_me.birthday
        // (the client requires a valid date) and to decide junior status at creation. Null on legacy
        // accounts, which fall back to the old fixed placeholder date.
        public string? Birthday { get; set; }
        public int XP { get; set; }
        public int Level { get; set; }
        public int pronounFlags { get; set; }
        public int identityFlags { get; set; }
        public bool IsJunior { get; set; }
        public bool AvoidJuniors { get; set; }
        public mPlayerReputation? PlayerReputation { get; set; }
        public List<mPlatformID>? PlatformIds { get; set; }
        public DateTime created_at { get; set; }
        public DateTime last_login_time { get; set; }
        public List<ulong> recent_rooms { get; set; } = new List<ulong>();
        public string? bannerImage { get; set; }
        public string? profileImage { get; set; }
    }

    public class AccountLogin
    {
        public DateTime createdAt { get; set; }
        public DateTime lastLoginTime { get; set; }
        public ulong accountId { get; set; }
        public Platforms platform { get; set; }
        public string? platformId { get; set; }
        // false = platform ownership is the credential -> the client cached-logs-in with no prompt.
        // true  = no account/link behind this entry -> the client shows the username/password login
        // screen for it (the reference's FAKE cached-login trick). See AuthController.CachedLogins.
        public bool requirePassword { get; set; } = false;
    }

    public class mPlatformID
    {
        public Platforms Platform { get; set; }
        public ulong PlatformId { get; set; }
    }

    // One device the account has signed in from, plus the platform it signed in on. Used by the
    // account lock to cap an account at one PC device and one Quest (Oculus) device.
    public class mDeviceBind
    {
        public string? DeviceId { get; set; }
        public Platforms Platform { get; set; }
        /// <summary>Last login from this device (UTC). Null on rows written before it was tracked.</summary>
        public DateTime? LastSeen { get; set; }
    }

    public class mPlayerReputation
    {
        public bool IsCheerful { get; set; }
        public int Noteriety { get; set; }
        public int CheerGeneral { get; set; }
        public int CheerHelpful { get; set; }
        public int CheerGreatHost { get; set; }
        public int CheerSportsman { get; set; }
        public int CheerCreative { get; set; }
        public int CheerCredit { get; set; }
        public int SubscriberCount { get; set; }
        public int SubscribedCount { get; set; }
        public CheerCategoryEnum SelectedCheer { get; set; }
    }

    public class DeleteMessagesRequest
    {
        public List<ulong> MessageIds { get; set; }
    }

    // POST /api/messages/v1/sendMultiple body: one message to several recipients.
    // JSON e.g. {"ToPlayerIds":[205],"Type":20,"Data":""}.
    public class SendMultipleMessagesRequest
    {
        public List<ulong>? ToPlayerIds { get; set; }
        public int Type { get; set; }
        public string? Data { get; set; }
    }


    public enum CheerCategoryEnum
    {
        None = -1,
        General = 0,
        Helpful = 10,
        Sportmanship = 20,
        GreatHost = 30,
        Creative = 40,
        RecRoomDeveloper = 9000
    }

    // The PUBLIC account DTO - the backbone of a player's profile.
    //
    // Every string here coalesces null -> "" via its setter, so the client NEVER receives a
    // null (or a missing key) for a string field. RecFlare's account DTO does exactly this on
    // purpose - it reads these as strings, and a null makes its parser throw, which is why a
    // profile with no banner / emoji / (rarely) display name would fail to load entirely even
    // though the account exists. Fixing it on the DTO covers every path that builds one
    // (account/{id}, account/bulk, account/me, admin) at once.
    public class account_data_web
    {
        private string _displayName = "";
        private string _displayEmoji = "";
        private string _profileImage = "DefaultProfileImage.jpg";
        private string _bannerImage = "";
        private string _username = "";

        public ulong accountId { get; set; }
        public DateTime createdAt { get; set; }
        public string displayName { get => _displayName; set => _displayName = value ?? ""; }
        public string displayEmoji { get => _displayEmoji; set => _displayEmoji = value ?? ""; }
        public long identityFlags { get; set; }
        public bool isJunior { get; set; }
        public long personalPronouns { get; set; }
        public long platforms { get; set; }
        // Unset profile image falls back to "DefaultProfileImage.jpg" (RecFlare's default), not ""
        // - the client renders whatever name it is handed, and an empty name is a broken image.
        public string profileImage { get => _profileImage; set => _profileImage = string.IsNullOrEmpty(value) ? "DefaultProfileImage.jpg" : value; }
        public string bannerImage { get => _bannerImage; set => _bannerImage = value ?? ""; }
        public string username { get => _username; set => _username = value ?? ""; }
    }

    // The /account/me (self) DTO. Matches RecFlare's toSelfAccountDto: the public account fields
    // PLUS email, birthday, availableUsernameChanges. Two rules the reference's tests spell out
    // and that a strict client parser enforces:
    //   * string fields are "" when unset, never null - a null makes the client's parser throw
    //     (which can leave a screen that reads the full account, like the profile, blank);
    //   * the client reads `availableUsernameChanges` (capital U) and `birthday`. The birthday
    //     just has to be PRESENT and a valid date - "the game client needs this to be set" - so a
    //     fixed old date stands in until real birthdays are stored.
    public class account_data_me
    {
        private string _bannerImage = "";
        private string _email = "";
        private string _displayName = "";
        private string _displayEmoji = "";
        private string _profileImage = "DefaultProfileImage.jpg";
        private string _username = "";

        public ulong accountId { get; set; }
        public int availableUsernameChanges { get; set; } = 3;
        public string bannerImage { get => _bannerImage; set => _bannerImage = value ?? ""; }
        public DateTime createdAt { get; set; }
        public string birthday { get; set; } = "1904-01-01T00:00:00.000Z";
        public string email { get => _email; set => _email = value ?? ""; }
        public string displayName { get => _displayName; set => _displayName = value ?? ""; }
        public string displayEmoji { get => _displayEmoji; set => _displayEmoji = value ?? ""; }
        public long identityFlags { get; set; }
        public bool hasBirthday { get; set; } = true;
        public bool isJunior { get; set; }
        public long personalPronouns { get; set; }
        public long platforms { get; set; }
        public string profileImage { get => _profileImage; set => _profileImage = string.IsNullOrEmpty(value) ? "DefaultProfileImage.jpg" : value; }
        public bool TreatAsJunior { get; set; } = false;
        public string username { get => _username; set => _username = value ?? ""; }
    }

    public class CustomAvatarItems
    {
        public bool isCreationAllowedForAccount { get; set; } = true;
        public bool isCreationEnabled { get; set; } = true;
        public bool isRenderingEnabled { get; set; } = true;

        public List<string> ownedItems { get; set; } = new List<string>();
    }

    public class Influencer
    {
        public string creatorCode { get; set; } = "";
        public bool isInfluencer { get; set; } = false;
        public ulong SupportingInfluencer { get; set; }
    }

    public class MessageData
    {
        /*
        [
              {
                "Id": 15909836337,
                "FromPlayerId": 64495891,
                "SentTime": "2025-05-27T11:39:25.12Z",
                "Type": 200,
                "Data": "58129955",
                "RoomId": null,
                "PlayerEventId": null
              }
        ]
        */
        public ulong Id { get; set; }
        public ulong FromPlayerId { get; set; }
        // The recipient - the reference's game-invite Message includes ToPlayerId; ours omitted it.
        // Harmless on chat/other messages (0 when unset), present on invites so the client's
        // invite-message parser has the field it expects.
        public ulong ToPlayerId { get; set; }
        public DateTime SentTime { get; set; }
        public WebsocketEvents.MessageType Type { get; set; }
        public string? Data { get; set; }
        public ulong? RoomId { get; set; }
        public ulong? PlayerEventId { get; set; }

    }

    public enum DeviceClasses
    {
        Unknown,
        VR,
        Screen,
        Mobile,
        VRLow,
        Quest2
    }

    public class account_login<T>
    {
        public bool success { get; set; }
        public string? error { get; set; }
        [JsonIgnore]
        public ulong accid { get; set; }
    }

    [Flags]
    public enum DevFlag
    {
        none = 0,
        screenshare = 1,
        dev = 2,
        mod = 4,
        owner = 8,
        junior = 16,
        keepsake = 32,
        newly_created_account = 64,
        restricted_account = 128,
        support = 256,   // website Support staff: can see & manage all support tickets (SiteController.IsStaff)
        studio = 512,    // CampusStudio: may publish recoloured items from the app (StudioController)
        unused_4 = 1024,
        unused_5 = 2048,
        unused_6 = 4096,
        unused_7 = 8192,
    }
}
