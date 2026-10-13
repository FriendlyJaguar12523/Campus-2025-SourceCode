using Newtonsoft.Json;

namespace Campus_live_rewrite.api.server
{
    public class Moderation_Detail
    {
        public class ModerationBlockDetails
        {
            public Moderation_Detail.ReportCategory ReportCategory { get; set; } = Moderation_Detail.ReportCategory.Unknown;
            public int Duration { get; set; } = 0; 
            public long GameSessionId { get; set; } = 0;
            public bool? IsHostKick { get; set; } = false;
            public string? Message { get; set; } = "";
            public ulong? PlayerIdReporter { get; set; } = null;
            public bool? IsBan { get; set; } = false;
            // The rest of the 16-key 2025 DTO (wire-shapes: playerreporting-moderationblockdetails).
            // IsWarning is what turns the kick frame into a warning dialog instead of a removal.
            public bool IsVoiceModAutoban { get; set; } = false;
            public bool IsDeviceBan { get; set; } = false;
            public bool IsWarning { get; set; } = false;
            public string? VoteKickReason { get; set; } = null;
            public DateTime? TimeoutStartedAt { get; set; } = null;
            [JsonIgnore]
            public long ModerationSetUnixTime { get; set; } = 0;
            [JsonIgnore]
            public ulong BannedByPlayerId { get; set; } = 0;
        }

        public enum ReportCategory
        {
            Moderator = -1,
            Unknown,
            DEPRECATED_MicrophoneAbuse,
            Harassment,
            Cheating,
            DEPRECATED_ImmatureBehavior,
            AFK,
            Misc,
            Underage,
            VoteKick = 10,
            MisleadingPurchases = 11,
            CoC_Underage = 100,
            CoC_Sexual,
            CoC_Discrimination,
            CoC_Trolling,
            CoC_NameOrProfile,
            InappropriateClothing = 200,
            IssuingInaccurateReports = 1000,
            RoomInventoryItems = 1100,
            InappropriateRooms = 1101
        }
    }
}
