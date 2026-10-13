using LiteDB;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using static Campus_live_rewrite.api.server.Classes.db_class.ChatDBClasses;

namespace Campus_live_rewrite.api.dbs
{
    /// <summary>
    /// Chat threads + messages (Data/DBs/Chat.db). One thread per exact member set for DMs and
    /// groups (fetch-or-create, like the reference's GetChatBetweenPlayers), party threads as
    /// type-1 threads, and per-member read / snooze / favorite state. Every write that another
    /// member should see is pushed over the notifications hub as ChatMessageReceived so the
    /// conversation (and the in-room bubble) updates without polling.
    /// </summary>
    public class ChatDB
    {
        public const int SystemSenderId = -5;
        // The client's ChatThreadType: 0 Player (DM / group), 1 Club, 2 Party. Party threads were
        // stored and served as 1 - a CLUB thread to the client, which is why the party's chat
        // rendered as an ordinary conversation named after the other member.
        public const int ThreadTypeUser = 0;
        public const int ThreadTypeClub = 1;
        public const int ThreadTypeParty = 2;
        public const int MaxThreadMembers = 50;

        public static LiteDatabase ChatDBFile = new LiteDatabase(Environment.CurrentDirectory + "/Data/DBs/Chat.db");
        private static ILiteCollection<ChatThread> Threads => ChatDBFile.GetCollection<ChatThread>("threads");
        private static ILiteCollection<ChatMessage> Messages => ChatDBFile.GetCollection<ChatMessage>("messages");
        private static readonly object _idLock = new();

        static ChatDB()
        {
            Threads.EnsureIndex(x => x.ChatThreadId, unique: true);
            Threads.EnsureIndex(x => x.PlayerIds);
            Messages.EnsureIndex(x => x.ChatThreadId);
            Messages.EnsureIndex(x => x.ChatMessageId, unique: true);

            // One-time migration: party threads created while the type was mis-numbered (1) become 2.
            // No club threads exist in this store (clubs chat through their own service), so every
            // type-1 thread here is a party.
            try
            {
                int n = 0;
                foreach (var t in Threads.Find(x => x.ChatThreadType == ThreadTypeClub).ToList())
                { t.ChatThreadType = ThreadTypeParty; Threads.Update(t); n++; }
                if (n > 0) Console.WriteLine($"[chat] migrated {n} party thread(s) to ChatThreadType {ThreadTypeParty}");
            }
            catch (Exception ex) { Console.WriteLine($"[chat] party type migration failed: {ex.Message}"); }

            // One party per player. Opening a party thread used to join it, and the "left" notice
            // that pushed back to the old party made the client open THAT one and rejoin it - a
            // ping-pong (26,000 joins in 3 days) that left players on several parties at once, so
            // whoever changed room dragged everyone on them along. Keep each player's newest party.
            try
            {
                int n = 0;
                var parties = Threads.Find(x => x.ChatThreadType == ThreadTypeParty).ToList()
                    .OrderByDescending(x => x.LastActivityAt ?? DateTime.MinValue).ThenByDescending(x => x.ChatThreadId).ToList();
                var seen = new HashSet<ulong>();
                foreach (var t in parties)
                {
                    var keep = (t.PlayerIds ?? new()).Where(seen.Add).ToList();
                    if (keep.Count == (t.PlayerIds?.Count ?? 0)) continue;
                    n += t.PlayerIds!.Count - keep.Count;
                    t.PlayerIds = keep;
                    Threads.Update(t);
                }
                if (n > 0) Console.WriteLine($"[chat] removed {n} duplicate party membership(s) - one party per player");
            }
            catch (Exception ex) { Console.WriteLine($"[chat] party dedupe failed: {ex.Message}"); }
        }

        // (player, party thread) -> when they came off it. Opening a party thread you just left
        // must not put you back on it (see ChatController.AdmitToParty).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, long), DateTime> _leftParty = new();

        public static bool LeftPartyRecently(ulong playerId, long threadId, TimeSpan window)
            => _leftParty.TryGetValue((playerId, threadId), out var at) && DateTime.UtcNow - at < window;

        public static void ForgetPartyLeave(ulong playerId, long threadId) => _leftParty.TryRemove((playerId, threadId), out _);

        /// <summary>Every party thread the player is on (normally one).</summary>
        public static List<long> PartyThreadIdsFor(ulong playerId)
            => Threads.Find(x => x.PlayerIds.Contains(playerId) && x.ChatThreadType == ThreadTypeParty).Select(x => x.ChatThreadId).ToList();

        private static long NextThreadId()
        {
            var last = Threads.Query().OrderByDescending(x => x.ChatThreadId).Limit(1).FirstOrDefault();
            return (last?.ChatThreadId ?? 0) + 1;
        }
        private static long NextMessageId()
        {
            var last = Messages.Query().OrderByDescending(x => x.ChatMessageId).Limit(1).FirstOrDefault();
            return (last?.ChatMessageId ?? 0) + 1;
        }

        public static string StartedChatContents(ulong by) =>
            JsonConvert.SerializeObject(new MessageJson { Type = MessageContentType.Text, Version = 1, Data = $"Player <@U{by}> started a chat" });

        // ---------------- threads ----------------

        /// <summary>Create a thread. type 0 = DM/group (posts the "started a chat" notice), 1 = party.</summary>
        public static ChatThread CreateThread(List<ulong> memberIds, ulong creatorid, string? name = null, int type = ThreadTypeUser)
        {
            ChatThread thread;
            lock (_idLock)
            {
                thread = new ChatThread
                {
                    ChatThreadId = NextThreadId(),
                    PlayerIds = memberIds.Distinct().ToList(),
                    ChatThreadName = name ?? "",
                    ChatThreadType = type,
                    CreatorPlayerId = creatorid,
                    CreatedAt = DateTime.UtcNow,
                    LastActivityAt = DateTime.UtcNow,
                    IsFavorited = false,
                    SnoozedUntil = null,
                    LastReadMessageId = 0,
                    Messages = new List<ChatMessage>()
                };
                Threads.Insert(thread);
            }
            if (type == ThreadTypeUser)
                AddMessage(thread.ChatThreadId, SystemSenderId, StartedChatContents(creatorid));
            return GetThread(thread.ChatThreadId)!;
        }

        /// <summary>
        /// The existing type-0 thread whose members are EXACTLY this set, else null. Keeps one
        /// conversation per pair/group: every "message X" used to mint a brand-new thread.
        /// </summary>
        public static ChatThread? FindThreadWithExactMembers(List<ulong> memberIds)
        {
            var want = memberIds.Distinct().OrderBy(x => x).ToList();
            if (want.Count == 0) return null;
            ulong probe = want[0];
            var candidates = Threads.Find(x => x.PlayerIds.Contains(probe) && x.ChatThreadType == ThreadTypeUser).ToList();
            var hit = candidates
                .Where(t => t.PlayerIds != null && t.PlayerIds.Distinct().OrderBy(x => x).SequenceEqual(want))
                .OrderByDescending(t => t.LastActivityAt ?? DateTime.MinValue).ThenByDescending(t => t.ChatThreadId)
                .FirstOrDefault();
            return hit == null ? null : GetThread(hit.ChatThreadId);
        }

        public static ChatThread GetOrCreateThreadWithMembers(List<ulong> memberIds, ulong creatorId)
            => FindThreadWithExactMembers(memberIds) ?? CreateThread(memberIds, creatorId);

        /// <summary>The caller's party thread (type 1, most recent) or null.</summary>
        public static ChatThread? GetPartyThreadFor(ulong playerId)
        {
            var t = Threads.Find(x => x.PlayerIds.Contains(playerId) && x.ChatThreadType == ThreadTypeParty)
                           .OrderByDescending(x => x.LastActivityAt ?? DateTime.MinValue).ThenByDescending(x => x.ChatThreadId).FirstOrDefault();
            return t == null ? null : GetThread(t.ChatThreadId);
        }

        public static ChatThread? GetThread(long threadId)
        {
            var thread = Threads.FindOne(x => x.ChatThreadId == threadId);
            if (thread == null) return null;
            thread.Messages = Messages.Find(x => x.ChatThreadId == threadId).OrderBy(x => x.ChatMessageId).ToList();
            thread.MemberStates ??= new();
            thread.PlayerIds ??= new();
            return thread;
        }

        /// <summary>Threads the player is on, most recently active first, each with only its latest message.</summary>
        public static List<ChatThread> GetThreadsForPlayer(ulong playerId, int maxCount)
        {
            // Most recently active first; threads from before LastActivityAt existed sort by id.
            var list = Threads.Find(x => x.PlayerIds.Contains(playerId))
                              .OrderByDescending(x => x.LastActivityAt ?? x.CreatedAt ?? DateTime.MinValue)
                              .ThenByDescending(x => x.ChatThreadId)
                              .Take(maxCount).ToList();
            foreach (var t in list)
            {
                t.Messages = Messages.Find(x => x.ChatThreadId == t.ChatThreadId).OrderByDescending(x => x.ChatMessageId).Take(1).ToList();
                t.MemberStates ??= new();
                t.PlayerIds ??= new();
            }
            return list;
        }

        /// <summary>Every thread that exists (admin moderation only - see AdminChatController). Each comes back with just its
        /// latest message loaded, same as GetThreadsForPlayer; call GetThread(id) for the full transcript.</summary>
        public static List<ChatThread> AllThreads()
        {
            var list = Threads.FindAll().ToList();
            foreach (var t in list)
            {
                t.Messages = Messages.Find(x => x.ChatThreadId == t.ChatThreadId).OrderByDescending(x => x.ChatMessageId).Take(1).ToList();
                t.MemberStates ??= new();
                t.PlayerIds ??= new();
            }
            return list;
        }

        public static int MessageCount(long threadId) => Messages.Count(x => x.ChatThreadId == threadId);

        /// <summary>Messages whose stored contents (the raw JSON envelope, so this also matches a Text message's words) contain
        /// the needle. A scan - fine for an admin search, never called from a player-facing route.</summary>
        public static List<ChatMessage> SearchMessageContents(string needleLower, int cap)
            => Messages.FindAll().Where(m => (m.Contents ?? "").ToLowerInvariant().Contains(needleLower)).Take(cap).ToList();

        public static bool IsMember(long threadId, ulong playerId)
        {
            var t = Threads.FindOne(x => x.ChatThreadId == threadId);
            return t?.PlayerIds != null && t.PlayerIds.Contains(playerId);
        }

        public static ChatThread? AddMember(long threadId, ulong playerId)
        {
            var t = Threads.FindOne(x => x.ChatThreadId == threadId);
            if (t == null) return null;
            t.PlayerIds ??= new();
            if (!t.PlayerIds.Contains(playerId)) { t.PlayerIds.Add(playerId); t.LastActivityAt = DateTime.UtcNow; Threads.Update(t); }
            return GetThread(threadId);
        }

        public static ChatThread? RemoveMember(long threadId, ulong playerId)
        {
            var t = Threads.FindOne(x => x.ChatThreadId == threadId);
            if (t == null) return null;
            t.PlayerIds ??= new();
            if (t.PlayerIds.Remove(playerId))
            {
                t.LastActivityAt = DateTime.UtcNow;
                Threads.Update(t);
                if (t.ChatThreadType == ThreadTypeParty)
                {
                    var now = DateTime.UtcNow;
                    _leftParty[(playerId, threadId)] = now;
                    foreach (var kv in _leftParty) if (now - kv.Value > TimeSpan.FromHours(1)) _leftParty.TryRemove(kv.Key, out _);
                }
            }
            return GetThread(threadId);
        }

        public static ChatThread? SetName(long threadId, string name)
        {
            var t = Threads.FindOne(x => x.ChatThreadId == threadId);
            if (t == null) return null;
            t.ChatThreadName = name ?? "";
            Threads.Update(t);
            return GetThread(threadId);
        }

        // ---------------- per-member state ----------------

        public static ChatMemberState StateFor(ChatThread t, ulong playerId)
        {
            t.MemberStates ??= new();
            if (t.MemberStates.TryGetValue(playerId.ToString(), out var s) && s != null) return s;
            // No entry yet: fall back to the old shared thread-level values.
            return new ChatMemberState { LastReadMessageId = t.LastReadMessageId, SnoozedUntil = t.SnoozedUntil, IsFavorited = t.IsFavorited };
        }

        public static void UpdateState(long threadId, ulong playerId, Action<ChatMemberState> change)
        {
            var t = Threads.FindOne(x => x.ChatThreadId == threadId);
            if (t == null) return;
            t.MemberStates ??= new();
            string key = playerId.ToString();
            if (!t.MemberStates.TryGetValue(key, out var s) || s == null)
                s = new ChatMemberState { LastReadMessageId = t.LastReadMessageId, SnoozedUntil = t.SnoozedUntil, IsFavorited = t.IsFavorited };
            change(s);
            t.MemberStates[key] = s;
            Threads.Update(t);
        }

        /// <summary>Move the read pointer forward (never back, never past the latest message).</summary>
        public static void MarkRead(long threadId, ulong playerId, long? upToMessageId = null)
        {
            var t = GetThread(threadId);
            if (t == null) return;
            long latest = t.Messages.Count > 0 ? t.Messages[^1].ChatMessageId : 0;
            long target = Math.Min(upToMessageId ?? latest, latest);
            UpdateState(threadId, playerId, s => { if (target > s.LastReadMessageId) s.LastReadMessageId = target; });
        }

        // ---------------- messages ----------------

        public static ChatMessage? GetMessage(long messageId) => Messages.FindOne(x => x.ChatMessageId == messageId);

        public static ChatMessage? AddMessage(long threadId, int senderId, string jsonContents)
        {
            var thread = Threads.FindOne(x => x.ChatThreadId == threadId);
            if (thread == null) return null;

            ChatMessage msg;
            lock (_idLock)
            {
                msg = new ChatMessage
                {
                    ChatMessageId = NextMessageId(),
                    ChatThreadId = threadId,
                    SenderPlayerId = senderId,
                    TimeSent = DateTime.UtcNow,
                    Contents = jsonContents,
                    ModerationState = 0
                };
                Messages.Insert(msg);
            }
            thread.LastActivityAt = msg.TimeSent;
            // The sender has read their own message.
            if (senderId > 0)
            {
                thread.MemberStates ??= new();
                string key = ((ulong)senderId).ToString();
                if (!thread.MemberStates.TryGetValue(key, out var s) || s == null) s = new ChatMemberState();
                s.LastReadMessageId = msg.ChatMessageId;
                thread.MemberStates[key] = s;
            }
            Threads.Update(thread);
            return msg;
        }

        public static object MessageDto(ChatMessage m) => new
        {
            chatMessageId = m.ChatMessageId,
            chatThreadId = m.ChatThreadId,
            senderPlayerId = m.SenderPlayerId,
            timeSent = m.TimeSent.ToUniversalTime(),   // LiteDB hands back local time; the client wants a UTC instant
            contents = m.Contents,
            moderationState = m.ModerationState
        };

        /// <summary>Push a message to every member of its thread (ChatMessageReceived, or another frame id).</summary>
        public static void Push(ChatThread thread, ChatMessage m, string frameId = "ChatMessageReceived")
        {
            var payload = JsonConvert.SerializeObject(new { Id = frameId, Msg = MessageDto(m) });
            Notifications.FireAndForget(Notifications.SendToPlayers(thread.PlayerIds.ToList(), payload), $"chat push {frameId}");
        }

        /// <summary>Post a system (-5) notice into a thread and push it.</summary>
        public static ChatMessage? SystemNotice(long threadId, string text)
        {
            var contents = JsonConvert.SerializeObject(new MessageJson { Type = MessageContentType.Text, Version = 1, Data = text });
            var m = AddMessage(threadId, SystemSenderId, contents);
            if (m == null) return null;
            var t = GetThread(threadId);
            if (t != null) Push(t, m);
            return m;
        }

        // Kept for older callers.
        public static ChatThread? RenameThread(long threadId, ulong senderId, string newThreadName)
        {
            var t = SetName(threadId, newThreadName);
            if (t != null) SystemNotice(threadId, $"Player <@U{senderId}> renamed the chat");
            return GetThread(threadId);
        }

        public static ChatThread? LeaveThread(long threadId, ulong senderId)
        {
            // Notice first (the leaver is still a member and gets the push that tells their
            // client the thread is gone), then drop the membership.
            SystemNotice(threadId, $"Player <@U{senderId}> left");
            return RemoveMember(threadId, senderId);
        }
    }
}
