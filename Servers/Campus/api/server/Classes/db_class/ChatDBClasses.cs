namespace Campus_live_rewrite.api.server.Classes.db_class
{
    public class ChatDBClasses
    {
        public class ChatThread
        {
            public List<ChatMessage> Messages { get; set; }
            public long ChatThreadId { get; set; }
            public List<ulong> PlayerIds { get; set; }
            public long LastReadMessageId { get; set; }
            public string ChatThreadName { get; set; }
            public DateTime? SnoozedUntil { get; set; }
            public bool IsFavorited { get; set; }

            // 0 = a normal DM / group thread, 1 = a PARTY thread (the party's chat; the client
            // finds it through /chat/thread/party and shows it in-room).
            public int ChatThreadType { get; set; } = 0;
            // Per-member read pointer / snooze / favorite. These are PER PLAYER in the real
            // service; the three thread-level fields above are the pre-existing shared ones and
            // are only read when a player has no entry here yet.
            public Dictionary<string, ChatMemberState> MemberStates { get; set; } = new();
            // Who created the party thread (party leader) - only meaningful for ChatThreadType 1.
            public ulong CreatorPlayerId { get; set; }
            // Nullable on purpose: a thread stored before these existed deserialises with null
            // (an initialiser would stamp "now" on every old thread and sort them above new ones).
            public DateTime? CreatedAt { get; set; }
            public DateTime? LastActivityAt { get; set; }
        }

        public class ChatMemberState
        {
            public long LastReadMessageId { get; set; }
            public DateTime? SnoozedUntil { get; set; }
            public bool IsFavorited { get; set; }
        }

        public class ChatMessage
        {
            public long ChatMessageId { get; set; }
            public long ChatThreadId { get; set; }
            public int SenderPlayerId { get; set; }
            public DateTime TimeSent {  get; set; }
            public string Contents { get; set; }
            public int ModerationState { get; set; } // todo find the enum
        }

        public class MessageJson
        {
            public MessageContentType Type { get; set; }
            public int Version { get; set; } // default ver is 2
            public string Data { get; set; }
        }

        public struct SendMessageResponse
        {
            public ChatResults ChatResult { get; set; }
            public ChatThread ChatThread { get; set; }
        }

        public class ChatPrivacySettings
        {
            public ulong playerId { get; set; }
            public ChatPrivacy directMessagePrivacySetting { get; set; } = ChatPrivacy.Friends;
            public ChatPrivacy groupChatPrivacySetting { get; set; } = ChatPrivacy.Friends;
        }

        public enum ChatResults
        {
            Success,
            InvalidArguments,
            ThreadNotFound,
            MembershipNotFound,
            PlayerAlreadyOnThread,
            CannotMessagePlayer,
            InvalidCharacters,
            RecentlyLeftThread,
            ThreadTooLarge
        }

        public enum ModerationState : byte
        {
            Active,
            Junior_Pending = 11,
            Moderation_Pending = 100,
            Moderation_Closed,
            Moderation_Banned,
            MarkedForDelete = 255
        }

        public enum QueryMode
        {
            Latest,
            NewerThan,
            OlderThan
        }

        public enum MessageContentType
        {
            Text,
            PartyInvite,
            Photo
        }

        public enum ChatPrivacy
        {
            Friends,
            Favorites,
            NoOne
        }
    }
}
