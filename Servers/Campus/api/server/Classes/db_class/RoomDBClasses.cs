using LiteDB;
using Newtonsoft.Json;

namespace Campus_live_rewrite.api.server.Classes.db_class
{
    public class RoomDBClasses
    {
        public class Resultslist<T>
        {
            public List<T>? Results { get; set; }
            public long TotalResults { get; set; }
        }

        [Flags]
        public enum WarningMaskType
        {
            None = 0,
            Scary = 1,
            Mature = 2,
            FlashingLights = 4,
            IntenseMotion = 8,
            Violence = 16,
            Custom = 32,
            Reports = 64
        }

        /// <summary>Writes JSON null whatever the value is (reads normally).</summary>
        public class NeverSentToClient : Newtonsoft.Json.JsonConverter
        {
            public override bool CanRead => false;
            public override bool CanConvert(Type objectType) => true;
            public override void WriteJson(Newtonsoft.Json.JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer) => writer.WriteNull();
            public override object? ReadJson(Newtonsoft.Json.JsonReader reader, Type objectType, object? existingValue, Newtonsoft.Json.JsonSerializer serializer) => throw new NotSupportedException();
        }

        public class RoomRoot
        {
            // The UI shows FriendlyName; `Name` is the internal slug (devin1s_MakerRoom_0601).
            // It is a real key on the client's 44-key Room DTO and was simply absent from ours
            // (wire-shapes: rooms-contributedby). Computed, never stored.
            [LiteDB.BsonIgnore]
            public string FriendlyName => Name ?? "";

            // Live player count, shown on room cards. Nullable on the wire and filled only by the
            // list endpoints that can afford one presence pass; null = "unknown", which the client
            // renders as no count rather than zero.
            [LiteDB.BsonIgnore]
            public int? CCU { get; set; }

            public RoomDBClasses.RoomAccessibility Accessibility { get; set; }
            public int AgeRating { get; set; }
            public bool AutoLocalizeRoom { get; set; }
            public DateTime? BecameRRStudioRoomAt { get; set; }
            public bool CloningAllowed { get; set; }
            public DateTime CreatedAt { get; set; }
            public ulong CreatorAccountId { get; set; }
            public string? CurrentSnapshotId { get; set; }
            public bool NeedsSnapshotId { get; set; }
            public string CustomWarning { get; set; }
            public string DataBlob { get; set; }
           // public string DataBlobHash { get; set; }
            public string Description { get; set; }
            public bool DisableMicAutoMute { get; set; }
            public bool DisableRoomComments { get; set; }
            public bool EncryptVoiceChat { get; set; }
            public bool ExcludeFromLists { get; set; }
            public bool ExcludeFromSearch { get; set; }
            public string ImageName { get; set; }
            public bool IsDeveloperOwned { get; set; }
            // Set from the admin panel's "Dev room" tick (RoomDB.SetDevRoom): it is what keeps IsDeveloperOwned on
            // a player-made room, because FixPlayerRoomFlags clears that flag at every start. Server-side only.
            [Newtonsoft.Json.JsonIgnore]
            public bool IsDevRoom { get; set; }
            public bool IsDorm { get; set; }
            public bool IsJuniorCreated { get; set; }
            public bool IsPlacePlay { get; set; }
            public bool IsRRO { get; set; }
            public bool IsRecRoomApproved { get; set; }
            public bool LoadScreenLocked { get; set; }
            public List<LoadScreen> LoadScreens { get; set; }
            public LocalizationContext LocalizationContext { get; set; }
            public int MaxPlayerCalculationMode { get; set; }
            public int MaxPlayers { get; set; }
            public int MinLevel { get; set; }
            public int? MinUgcSubVersion { get; set; }
            public string Name { get; set; }
            public int? PersistenceVersion { get; set; }
            public List<string> PromoExternalContent { get; set; }
            public List<string> PromoImages { get; set; }
            public int PublishState { get; set; }
            public DateTime PublishedAt { get; set; }
            public string RankedEntityId { get; set; }
            public object RankingContext { get; set; }
            public List<string> RestrictedCircuitsAllowListNames { get; set; }
            public List<RoleClass> Roles { get; set; }
            // The room-wide "last ban" record, kept in the db for the admin tools but NEVER sent to the game. The
            // client reads this key as THE VIEWER's own ban on the room and shows "Unable to Join Room - you have
            // been banned" from it; sent to everyone, one ban made the room look banned to every player who opened
            // it (and any room with a ban in its history, which is why it seemed to happen everywhere). The ban
            // itself is enforced at join (BannedFromRoom), so nothing is lost by sending null.
            [Newtonsoft.Json.JsonConverter(typeof(NeverSentToClient))]
            public RoomBanDetails RoomBanDetails { get; set; }
            // Every ban ever issued on this room (active, elapsed and lifted). RoomBanDetails above
            // is the single "current" record older code reads; this is the real list. Not part of
            // the room DTO the client gets.
            [Newtonsoft.Json.JsonIgnore]
            public List<RoomBanDetails>? RoomBans { get; set; }
            [BsonId]
            public ulong RoomId { get; set; }
            public RoomState State { get; set; }
            public Stats Stats { get; set; }
            public List<SubRoom> SubRooms { get; set; }
            public bool SupportsJuniors { get; set; }
            public bool SupportsLevelVoting { get; set; }
            public bool SupportsMobile { get; set; }
            public bool SupportsQuest2 { get; set; }
            public bool SupportsScreens { get; set; }
            public bool SupportsTeleportVR { get; set; }
            public bool SupportsVRLow { get; set; }
            public bool SupportsWalkVR { get; set; }
            public List<TagClass> Tags { get; set; }
            public bool ToxmodEnabled { get; set; }
            public int? UgcSubVersion { get; set; }
            public int? UgcVersion { get; set; }
            public WarningMaskType WarningMask { get; set; }
        }

        public class LoadScreen
        {
            public string ImageName { get; set; }
            public string Subtitle { get; set; }
            public string Title { get; set; }
        }

        public class LocalizationContext
        {
            public List<string> LocalizedFields { get; set; }
            public string Scope { get; set; }
            public string TargetLocale { get; set; }
        }

        public class RoleClass
        {
            public long AccountId { get; set; }
            public int InvitedRole { get; set; }
            public long? LastChangedByAccountId { get; set; }
            public RoomDBClasses.Role_data Role { get; set; }
        }

        public class RoomBanDetails
        {
            public long AccountId { get; set; }
            public DateTime? BanEndTime { get; set; }
            public DateTime BanStartTime { get; set; }
            public long BannedByAccountId { get; set; }
            public string Reason { get; set; }
            public int Status { get; set; }
            public long? UnbannedByAccountId { get; set; }
        }

        public class Stats
        {
            public int CheerCount { get; set; }
            public int FavoriteCount { get; set; }
            public int VisitCount { get; set; }
            public int VisitorCount { get; set; }
        }

        public class SubRoom
        {
            [BsonId]
            [Newtonsoft.Json.JsonIgnore]
            [System.Text.Json.Serialization.JsonIgnore]
            public ObjectId? Id { get; set; }

            public CurrentSave? CurrentSave { get; set; }
            [JsonIgnore]
            public List<CurrentSave> AllSaves { get; set; } = new();
            public RoomAccessibility Accessibility { get; set; }
            public long? CreatorAccountId { get; set; }
            public bool IsSandbox { get; set; }
            public int LastModeratedSaveModerationState { get; set; }
            public int MaxPlayers { get; set; }
            public string Name { get; set; }
            public ulong RoomId { get; set; }
            public bool ShouldAutoStageSaves { get; set; }
            public long? StagedSubRoomDataSaveId { get; set; }
            public ulong SubRoomId { get; set; }
            public string UnitySceneId { get; set; }

            // The permission overrides a manager saved for THIS subroom (RoomPermissions). Not on
            // the wire - the client reads them merged into GET /photon_access_token, never here.
            [Newtonsoft.Json.JsonIgnore]
            [System.Text.Json.Serialization.JsonIgnore]
            public List<Campus_live_rewrite.api.server.RoomPermissions.Entry>? Permissions { get; set; }
        }

        public class CurrentSave
        {
            public DateTime CreatedAt { get; set; }
            public string DataBlob { get; set; }
            //public string DataBlobHash { get; set; }
            public string Description { get; set; }
            public int ModerationState { get; set; }
            public int OMVersion { get; set; }
            public int PersistenceVersion { get; set; }
            public List<string> ReferencedUnityAssetIds { get; set; }
            public ulong? SavedByAccountId { get; set; }
            public DeviceClasses? SavedOnDeviceClass { get; set; }
            public Platforms? SavedOnPlatform { get; set; }
            public ulong SubRoomDataSaveId { get; set; }
            public ulong SubRoomId { get; set; }
            public int UgcSubVersion { get; set; }
            public string UnityAssetId { get; set; } // optional, may be null
        }

        public class TagClass
        {
            public string Tag { get; set; }
            public int Type { get; set; }   // 0 owner-set, 1 client-derived (autoTag: limitsv2 / beta), 2 server-derived (rro)
            // Present ONLY on the room's primary genre tag (never false) - the client keys on presence.
            [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public bool? IsPrimaryGenre { get; set; }
        }

        public class SubroomSaveResults
        {
            public List<CurrentSave> Results { get; set; } = new();
            public int TotalCount { get; set; }
        }


        public class RoomFileData
        {
            public RoomData? RoomData { get; set; }
            public SubRoomData? SubRoomData { get; set; }
            public string? InventionUsage { get; set; }
            public int PersistenceVersion { get; set; }
            public int OMVersion { get; set; }
            public int UgcSubVersion { get; set; }
            public string? Description { get; set; }
            public bool AutoPublish { get; set; }
        }

        public class RoomData
        {
            public string? Filename { get; set; }
            public string? Hash { get; set; }
            public string? OwnershipProof { get; set; }
        }

        public class SubRoomData
        {
            public string? Filename { get; set; }
            public string? Hash { get; set; }
            public string? OwnershipProof { get; set; }
        }

        public enum RoomAccessibility
        {
            Private,
            Public,
            Unlisted,
            Dev_only,
            Dev_Unlisted,
        }

        public enum RoomState
        {
            Active,
            PendingJunior = 11,
            Moderation_PendingReview = 100,
            Moderation_Closed,
            MarkedForDelete = 1000
        }

        public class Tags
        {
            public string? Tag { get; set; }
            public int Type { get; set; }
        }
        public class LoadScreens
        {
            public string? ImageName { get; set; }
            public string? Title { get; set; }
            public string? Subtitle { get; set; }
        }

        public enum Role_data : byte
        {
            None,
            Banned,
            Host = 10,
            Moderator = 20,
            Contributor = 25,      // the client's value (was missing -> a contributor invite landed as CoOwner)
            CoOwner = 30,
            TemporaryCoOwner,
            Creator = 255
        }
    }
}
