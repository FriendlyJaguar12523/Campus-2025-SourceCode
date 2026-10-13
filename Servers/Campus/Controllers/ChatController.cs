using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
using CampusLiveSource.api.admin;
using static Campus_live_rewrite.api.server.Classes.db_class.ChatDBClasses;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// The chat service (chat.rec.net): DMs, group chats and party threads. Contracts follow the
    /// reference chat app (apps/chat) - a reverse-engineered protocol, so the shapes matter:
    ///
    ///   GET  thread                       the caller's threads, each with its latestMessage
    ///   POST thread                       create with ids + messageContents -> { chatThread, chatResult }
    ///   POST thread/withmembers           FETCH-OR-CREATE the thread with exactly these members -> thread + messages
    ///   GET  thread/{id}                  thread + a page of messages (object)
    ///   GET  thread/{id}/message          a page of messages (bare array)
    ///   POST thread/{id} | /message       send -> { ChatMessage, ChatResult, chatResult, chatThread }
    ///   POST thread/{id}/member/{pid}     add someone to a group (bare ChatResult)
    ///   POST thread/{id}/leave | rename | snooze | favorite | read | message/{mid}/read   (bare ChatResult)
    ///   GET/POST thread/party             the caller's party thread / create one
    ///
    /// Read pointer, snooze and favorite are PER MEMBER. Every message (and every join/leave/rename
    /// notice) is pushed to the members as ChatMessageReceived over the hub, which is what draws
    /// the in-room bubble and updates an open conversation.
    /// </summary>
    [Route("chat")]
    public class ChatController : ControllerBase
    {
        // ---------------- helpers ----------------

        private static ContentResult Json(object o, int status = 200) => new()
        {
            Content = JsonConvert.SerializeObject(o),
            ContentType = "application/json",
            StatusCode = status
        };
        private static ContentResult Raw(string body, int status = 200) => new() { Content = body, ContentType = "application/json", StatusCode = status };
        private static ContentResult Result(ChatResults r) => Raw(((int)r).ToString());

        // The ChatResult values beyond the local enum that the client branches on.
        private const int ResultCannotMessageDueToRemotePrivacy = 16;
        private const int ResultCannotAddDueToPrivacy = 18;

        private string? Field(string name)
        {
            if (Request.HasFormContentType)
                foreach (var k in Request.Form.Keys)
                    if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return Request.Form[k].ToString();
            foreach (var k in Request.Query.Keys)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return Request.Query[k].ToString();
            return null;
        }

        private List<ulong> Ids(string name = "ids")
        {
            var raw = new List<string>();
            if (Request.HasFormContentType)
                foreach (var k in Request.Form.Keys)
                    if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) raw.AddRange(Request.Form[k].ToArray()!);
            foreach (var k in Request.Query.Keys)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) raw.AddRange(Request.Query[k].ToArray()!);
            return raw.SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                      .Select(v => ulong.TryParse(v.Trim(), out var id) ? id : 0UL)
                      .Where(v => v != 0).Distinct().ToList();
        }

        private static bool FormBool(string? v) =>
            v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

        // The raw body (any content type), for logging what an unknown call actually sends.
        private async Task<string> RawBody(int max = 2048)
        {
            try
            {
                Request.EnableBuffering();
                Request.Body.Position = 0;
                using var sr = new StreamReader(Request.Body, leaveOpen: true);
                string body = await sr.ReadToEndAsync();
                Request.Body.Position = 0;
                return body.Length > max ? body.Substring(0, max) + "..." : body;
            }
            catch { return "(unreadable)"; }
        }

        private int Count(string name, int fallback, int cap = 100)
        {
            if (int.TryParse(Field(name), out var n) && n > 0) return Math.Min(n, cap);
            return fallback;
        }

        internal static object ThreadListDto(ChatThread t, ulong viewer)
        {
            var st = ChatDB.StateFor(t, viewer);
            var latest = t.Messages != null && t.Messages.Count > 0 ? t.Messages.OrderByDescending(m => m.ChatMessageId).First() : null;
            return new
            {
                latestMessage = latest != null ? ChatDB.MessageDto(latest) : null,
                chatThreadId = t.ChatThreadId,
                playerIds = t.PlayerIds.OrderBy(x => x).ToList(),
                lastReadMessageId = st.LastReadMessageId,
                chatThreadName = t.ChatThreadName ?? "",
                chatThreadType = t.ChatThreadType,
                snoozedUntil = st.SnoozedUntil,
                isFavorited = st.IsFavorited
            };
        }

        internal static object ThreadWithMessagesDto(ChatThread t, ulong viewer, int count)
        {
            var st = ChatDB.StateFor(t, viewer);
            return new
            {
                chatThreadId = t.ChatThreadId,
                playerIds = t.PlayerIds.OrderBy(x => x).ToList(),
                lastReadMessageId = st.LastReadMessageId,
                chatThreadName = t.ChatThreadName ?? "",
                chatThreadType = t.ChatThreadType,
                snoozedUntil = st.SnoozedUntil,
                isFavorited = st.IsFavorited,
                messages = (t.Messages ?? new()).OrderByDescending(m => m.ChatMessageId).Take(count).Select(ChatDB.MessageDto).ToList()
            };
        }

        internal static object SentMessageDto(ChatMessage m) => new
        {
            ChatMessageId = m.ChatMessageId,
            ChatThreadId = m.ChatThreadId,
            SenderPlayerId = m.SenderPlayerId,
            TimeSent = m.TimeSent.ToUniversalTime(),
            Contents = m.Contents,
            ModerationState = m.ModerationState
        };

        // The stored envelope, with profanity masked in a Text message when the server is set to.
        internal static string CleanContents(string contents)
        {
            if (ConfigCampus.AllowSwearsInChatThreads) return contents;
            try
            {
                var mj = JsonConvert.DeserializeObject<MessageJson>(contents);
                if (mj != null && mj.Type == MessageContentType.Text && mj.Data != null)
                {
                    mj.Data = Sanitize.SanitizeMessage(mj.Data);
                    return JsonConvert.SerializeObject(mj);
                }
            }
            catch { /* not an envelope - store as sent */ }
            return contents;
        }

        // ---------------- privacy ----------------

        private const string DmPrivacyKey = "directMessagePrivacySetting";
        private const string GroupPrivacyKey = "groupChatPrivacySetting";

        private static ChatPrivacy? ParseChatPrivacy(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (Enum.TryParse<ChatPrivacy>(v.Trim(), ignoreCase: true, out var byName)) return byName;
            if (int.TryParse(v.Trim(), out var n) && Enum.IsDefined(typeof(ChatPrivacy), n)) return (ChatPrivacy)n;
            return null;
        }

        internal static ChatPrivacySettings ReadChatPrivacy(ulong playerId)
        {
            var dm = ParseChatPrivacy(SettingsDB.GetPlayerSettingValue(DmPrivacyKey, playerId)) ?? ChatPrivacy.Friends;
            var grp = ParseChatPrivacy(SettingsDB.GetPlayerSettingValue(GroupPrivacyKey, playerId)) ?? ChatPrivacy.Friends;
            return new ChatPrivacySettings { playerId = playerId, directMessagePrivacySetting = dm, groupChatPrivacySetting = grp };
        }

        internal static bool AreFriends(ulong a, ulong b)
        {
            if (a == b) return true;
            try
            {
                return (FriendsDB.GetRelationships(a) ?? new List<RelationshipDetail>())
                    .Any(r => r.PlayerID == b && r.RelationshipType == RelationshipType.Friend);
            }
            catch { return false; }
        }

        internal static bool IsFavoriteOf(ulong owner, ulong other)
        {
            try
            {
                return (FriendsDB.GetRelationships(owner) ?? new List<RelationshipDetail>())
                    .Any(r => r.PlayerID == other && r.RelationshipType == RelationshipType.Friend
                              && (r.Favorited == ReciprocalStatus.Local || r.Favorited == ReciprocalStatus.Mutual));
            }
            catch { return false; }
        }

        /// <summary>May `sender` open a DM (group=false) with / add to a group (group=true) `target`, per the target's privacy setting? Devs always may.</summary>
        internal static bool PrivacyAllows(ulong sender, ulong target, bool group)
        {
            if (sender == target) return true;
            if (PlayerDB.CheckDevFlag(sender)) return true;
            var p = ReadChatPrivacy(target);
            var setting = group ? p.groupChatPrivacySetting : p.directMessagePrivacySetting;
            return setting switch
            {
                ChatPrivacy.NoOne => false,
                ChatPrivacy.Favorites => IsFavoriteOf(target, sender),
                _ => AreFriends(sender, target),
            };
        }

        [HttpGet("thread/chatPrivacySetting")]
        public IActionResult GetChatPrivacySetting()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Ok(ReadChatPrivacy(account.Id));
        }

        // One field per call, by name ("Favorites") or ordinal; an absent field is left as it was.
        [HttpPut("thread/chatPrivacySetting")]
        public IActionResult SetChatPrivacySetting()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var dm = ParseChatPrivacy(Field(DmPrivacyKey));
            var grp = ParseChatPrivacy(Field(GroupPrivacyKey));
            if (dm != null) SettingsDB.SetPlayerSetting(DmPrivacyKey, ((int)dm.Value).ToString(), account.Id);
            if (grp != null) SettingsDB.SetPlayerSetting(GroupPrivacyKey, ((int)grp.Value).ToString(), account.Id);
            if (dm != null || grp != null)
                Console.WriteLine($"[chat] {account.Id} set privacy dm={dm?.ToString() ?? "(unchanged)"} group={grp?.ToString() ?? "(unchanged)"}");
            return Ok(ReadChatPrivacy(account.Id));
        }

        // Asked before the client opens a new DM (receivingPlayerId=...). Bare ChatResult.
        [HttpGet("thread/checkCanSendDirectMessageWithPrivacySetting")]
        public IActionResult CheckCanSendDirectMessage()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            ulong target = ulong.TryParse(Field("receivingPlayerId") ?? Field("playerId"), out var t) ? t : 0;
            if (target == 0) return Result(ChatResults.Success);
            return Raw(PrivacyAllows(account.Id, target, group: false) ? "0" : ResultCannotMessageDueToRemotePrivacy.ToString());
        }

        // How long a party invite link lives (bare single-key object, reference value).
        [HttpGet("settings/partyinvite")]
        public IActionResult PartyInviteSettings() => Json(new { InviteLinkLifetimeInMinutes = 60 });

        // ---------------- listing ----------------

        [HttpGet("thread")]
        public ContentResult GetThreads([FromQuery] int maxCount = 50, int mode = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var threads = ChatDB.GetThreadsForPlayer(account.Id, Math.Clamp(maxCount, 1, 200));
            return Json(threads.Select(t => ThreadListDto(t, account.Id)).ToList());
        }

        // The caller's party thread, or `{}` when they're not in one (the reference's answer: a
        // thread with every field at its default, which the client reads as "no party"). JSON
        // null failed the client's reader - "Response was empty" in Player.log on every login
        // and every room change, twice with its retries.
        [HttpGet("thread/party")]
        public ContentResult GetPartyThread([FromQuery] int maxCount = 1, [FromQuery] int mode = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var party = ChatDB.GetPartyThreadFor(account.Id);
            if (party == null) return Raw("{}");
            return Json(PartyThreadDto(party, account.Id));
        }

        // A party's chat membership follows the party itself: the party forms between the clients
        // over Photon (a shared PlayerPartyID), then each member's client simply opens the party
        // thread BY ID (GET /chat/thread/{id}) - it never asks to be added. So a party thread admits
        // a player who opens it when they're in NO party. Without this the second player got a 404,
        // the Party tab listed no members and party chat never opened.
        //
        // Only when they're in no party, and not straight back into one they just left. This used
        // to move them out of their current party too, posting "left" there - and that notice is
        // pushed to the leaver, whose client opens the old thread and moved back, posting "left"
        // in the new one... Players flipped between two parties dozens of times a second, ending
        // up on whichever won and merging parties together, so one player changing room pulled
        // people who had never partied with them into it. Switching party is the explicit
        // joinpartychat below.
        private static readonly TimeSpan RejoinAfterLeaving = TimeSpan.FromMinutes(2);

        private static ChatThread AdmitToParty(ChatThread thread, ulong playerId)
        {
            if (thread.ChatThreadType != ChatDB.ThreadTypeParty || thread.PlayerIds.Contains(playerId)) return thread;
            if (ChatDB.GetPartyThreadFor(playerId) != null) return thread;
            if (ChatDB.LeftPartyRecently(playerId, thread.ChatThreadId, RejoinAfterLeaving)) return thread;
            ChatDB.AddMember(thread.ChatThreadId, playerId);
            ChatDB.SystemNotice(thread.ChatThreadId, $"Player <@U{playerId}> joined the party");
            Console.WriteLine($"[chat] {playerId} joined party thread {thread.ChatThreadId} (opened it)");
            return ChatDB.GetThread(thread.ChatThreadId)!;
        }

        // Party thread DTO: the list shape (latestMessage) AND a messages page in one object, so
        // whichever way the client parses a thread it finds what it needs.
        private static object PartyThreadDto(ChatThread t, ulong viewer)
        {
            var st = ChatDB.StateFor(t, viewer);
            var ordered = (t.Messages ?? new()).OrderByDescending(m => m.ChatMessageId).ToList();
            return new
            {
                latestMessage = ordered.Count > 0 ? ChatDB.MessageDto(ordered[0]) : null,
                chatThreadId = t.ChatThreadId,
                playerIds = t.PlayerIds.OrderBy(x => x).ToList(),
                lastReadMessageId = st.LastReadMessageId,
                chatThreadName = t.ChatThreadName ?? "",
                chatThreadType = t.ChatThreadType,
                snoozedUntil = st.SnoozedUntil,
                isFavorited = st.IsFavorited,
                messages = ordered.Take(50).Select(ChatDB.MessageDto).ToList()
            };
        }

        // POST /chat/thread/party - "make me a party thread" (the client sends NO body: the thread
        // id it gets back is the party id it then shares with the players it parties up with over
        // Photon). Answers the THREAD itself; the create envelope this used to answer was not what
        // the client parses, so every Join Party click re-posted and no party ever formed. A caller
        // already in a party thread gets that one back. Optional ids seed members, optional
        // messageContents posts a first message.
        [HttpPost("thread/party")]
        public async Task<ContentResult> CreatePartyThread()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            Console.WriteLine($"[chat] {account.Id} POST thread/party ct='{Request.ContentType}' fields=[{(Request.HasFormContentType ? string.Join(",", Request.Form.Keys) : "")}] query={Request.QueryString} body={await RawBody()} headers=[{string.Join("; ", Request.Headers.Where(h => !string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Select(h => h.Key + "=" + h.Value))}]");

            var party = ChatDB.GetPartyThreadFor(account.Id);
            if (party == null)
            {
                var members = new List<ulong> { account.Id };
                members.AddRange(Ids().Where(id => id != account.Id));
                party = ChatDB.CreateThread(members, account.Id, "", ChatDB.ThreadTypeParty);
                Console.WriteLine($"[chat] {account.Id} created party thread {party.ChatThreadId} with [{string.Join(",", members)}]");
            }
            ChatMessage? first = null;
            string? contents = Field("messageContents");
            if (!string.IsNullOrWhiteSpace(contents))
            {
                first = ChatDB.AddMessage(party.ChatThreadId, (int)account.Id, CleanContents(contents));
                party = ChatDB.GetThread(party.ChatThreadId)!;
                if (first != null) ChatDB.Push(party, first);
            }
            // The client's create-party call is its CreateThread(messageContents) with nothing to
            // post (BestHTTP sends an empty form for that). It only proceeds on chatResult 0, so an
            // empty first message must NOT report InvalidArguments here (the reference's rule for a
            // plain /thread create) - that "1" is what left every Join Party click re-creating.
            return Json(new
            {
                ChatMessage = first != null ? SentMessageDto(first) : null,
                ChatResult = 0,
                chatResult = 0,
                chatThread = PartyThreadDto(party, account.Id)
            });
        }

        // Join another player's party thread (the "Join Party" side): /chat/thread/party/{threadId}
        // and /chat/thread/party/join?... are both accepted. Adds the caller, tells the members.
        // `thread/{id}/joinpartychat` is the spelling THIS client uses (it posted it 148 times and
        // got a 404 every time, which is why the party list flickered between 0 and 2 people and
        // the thread filled with joined/left notices - each refusal made the client retry from
        // scratch). The other two spellings are kept for older clients.
        [HttpPost("thread/{threadId:long}/joinpartychat")]
        [HttpPut("thread/{threadId:long}/joinpartychat")]
        [HttpPost("thread/party/{threadId:long}")]
        [HttpPut("thread/party/{threadId:long}")]
        public ContentResult JoinPartyThread(long threadId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var party = ChatDB.GetThread(threadId);
            if (party == null || party.ChatThreadType != ChatDB.ThreadTypeParty) return Result(ChatResults.ThreadNotFound);
            if (!party.PlayerIds.Contains(account.Id))
            {
                // Leave any previous party first - one party at a time. (Membership is dropped
                // BEFORE the notice, so the leaver isn't pushed the old party's "left" message.)
                foreach (var oldId in ChatDB.PartyThreadIdsFor(account.Id).Where(t => t != threadId))
                {
                    ChatDB.RemoveMember(oldId, account.Id);
                    ChatDB.SystemNotice(oldId, $"Player <@U{account.Id}> left");
                }
                ChatDB.ForgetPartyLeave(account.Id, threadId);
                ChatDB.AddMember(threadId, account.Id);
                // One notice per real join. A client that re-posts the same join (reconnect, room
                // change, a retry after a 404) lands in the `already a member` branch above and
                // says nothing - that repetition is what filled the thread with joined/left pairs.
                ChatDB.SystemNotice(threadId, $"Player <@U{account.Id}> joined the party");
                Console.WriteLine($"[chat] {account.Id} joined party thread {threadId}");
            }
            return Json(PartyThreadDto(ChatDB.GetThread(threadId)!, account.Id));
        }

        // Leave the party (drop out of the party thread).
        [HttpPost("thread/party/leave")]
        [HttpDelete("thread/party")]
        public ContentResult LeavePartyThread()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var party = ChatDB.GetPartyThreadFor(account.Id);
            if (party == null) return Result(ChatResults.MembershipNotFound);
            ChatDB.SystemNotice(party.ChatThreadId, $"Player <@U{account.Id}> left");
            ChatDB.RemoveMember(party.ChatThreadId, account.Id);
            Console.WriteLine($"[chat] {account.Id} left party thread {party.ChatThreadId}");
            return Result(ChatResults.Success);
        }

        // ---------------- creating / opening ----------------

        // POST /chat/thread: ids (repeated or comma-joined, the caller is added) + messageContents.
        // Reuses the thread with exactly these members if one exists (one conversation per
        // pair/group) and posts the first message. Answers the reference's CreateThreadResponse.
        [HttpPost("thread")]
        public ContentResult CreateThread()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var members = Ids().Where(id => id != account.Id).ToList();
            if (members.Count == 0 || members.Count + 1 > ChatDB.MaxThreadMembers) return Raw("", 400);

            bool group = members.Count > 1;
            var blocked = members.Where(m => !PrivacyAllows(account.Id, m, group)).ToList();
            if (blocked.Count == members.Count)
                return Json(new { ChatMessage = (object?)null, ChatResult = group ? ResultCannotAddDueToPrivacy : ResultCannotMessageDueToRemotePrivacy,
                                  chatResult = group ? ResultCannotAddDueToPrivacy : ResultCannotMessageDueToRemotePrivacy, chatThread = (object?)null });
            members = members.Except(blocked).ToList();

            var all = new List<ulong> { account.Id }; all.AddRange(members);
            var thread = ChatDB.GetOrCreateThreadWithMembers(all, account.Id);

            ChatMessage? first = null;
            string? contents = Field("messageContents");
            if (!string.IsNullOrWhiteSpace(contents))
            {
                first = ChatDB.AddMessage(thread.ChatThreadId, (int)account.Id, CleanContents(contents));
                thread = ChatDB.GetThread(thread.ChatThreadId)!;
                if (first != null) ChatDB.Push(thread, first);
            }
            int result = first != null ? (blocked.Count > 0 ? 17 : 0) : 1;
            Console.WriteLine($"[chat] {account.Id} opened thread {thread.ChatThreadId} with [{string.Join(",", members)}] (msg: {(first != null ? "yes" : "no")})");
            return Json(new { ChatMessage = first != null ? SentMessageDto(first) : null, ChatResult = result, chatResult = result, chatThread = ThreadListDto(thread, account.Id) });
        }

        // POST /chat/thread/withmembers: the client's GetChatBetweenPlayers - FETCH-OR-CREATE the
        // thread with exactly these members and answer it WITH its messages (an object, not the
        // create envelope). Minting a fresh thread on every call is what produced a new empty
        // conversation each time you opened someone's DM.
        [HttpPost("thread/withmembers")]
        public ContentResult ThreadWithMembers()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var members = Ids().Where(id => id != account.Id).ToList();
            if (members.Count == 0 || members.Count + 1 > ChatDB.MaxThreadMembers) return Raw("", 400);

            var all = new List<ulong> { account.Id }; all.AddRange(members);
            var existing = ChatDB.FindThreadWithExactMembers(all);
            if (existing == null)
            {
                bool group = members.Count > 1;
                if (members.Any(m => !PrivacyAllows(account.Id, m, group)))
                    return Raw((group ? ResultCannotAddDueToPrivacy : ResultCannotMessageDueToRemotePrivacy).ToString());
                existing = ChatDB.CreateThread(all, account.Id);
                var notice = existing.Messages.LastOrDefault();
                if (notice != null) ChatDB.Push(existing, notice);
                Console.WriteLine($"[chat] {account.Id} opened thread {existing.ChatThreadId} with [{string.Join(",", members)}]");
            }
            int count = Count("messageCount", 50);
            return Json(ThreadWithMessagesDto(existing, account.Id, count));
        }

        [HttpGet("thread/{id:long}")]
        public ContentResult GetThreadById(long id, [FromQuery] int maxCount = 50, [FromQuery] int messageCount = 0, [FromQuery] int mode = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var thread = ChatDB.GetThread(id);
            if (thread != null) thread = AdmitToParty(thread, account.Id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return Raw("", 404);
            int count = messageCount > 0 ? Math.Min(messageCount, 100) : Math.Clamp(maxCount, 1, 100);
            if (thread.ChatThreadType == ChatDB.ThreadTypeParty) return Json(PartyThreadDto(thread, account.Id));
            return Json(ThreadWithMessagesDto(thread, account.Id, count));
        }

        // A page of messages - a bare array. Latest / newer-than / older-than a reference id.
        [HttpGet("thread/{id:long}/message")]
        public ContentResult GetThreadMessages(long id, [FromQuery] int messageCount = 50, [FromQuery] int mode = 0, [FromQuery] long referenceMessageId = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var thread = ChatDB.GetThread(id);
            if (thread != null) thread = AdmitToParty(thread, account.Id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return Raw("", 404);
            int count = Math.Clamp(messageCount, 1, 200);
            IEnumerable<ChatMessage> messages = thread.Messages;
            messages = (QueryMode)mode switch
            {
                QueryMode.NewerThan => messages.Where(m => m.ChatMessageId > referenceMessageId).OrderBy(m => m.ChatMessageId).Take(count).OrderByDescending(m => m.ChatMessageId),
                QueryMode.OlderThan => messages.Where(m => m.ChatMessageId < referenceMessageId).OrderByDescending(m => m.ChatMessageId).Take(count),
                _ => messages.OrderByDescending(m => m.ChatMessageId).Take(count),
            };
            return Json(messages.Select(ChatDB.MessageDto).ToList());
        }

        // ---------------- sending ----------------

        [HttpPost("thread/{id:long}")]
        [HttpPost("thread/{id:long}/message")]
        public async Task<ContentResult> SendMessageToThread(long id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id))
                return Json(new { ChatMessage = (object?)null, ChatResult = 3, chatResult = 3, chatThread = (object?)null });

            // Members can be added in the same call (ids=...): a group grows as you message it.
            var extra = Ids().Where(pid => !thread.PlayerIds.Contains(pid)).ToList();
            foreach (var pid in extra)
            {
                if (!PrivacyAllows(account.Id, pid, group: true)) continue;
                ChatDB.AddMember(id, pid);
                ChatDB.SystemNotice(id, $"Player <@U{account.Id}> added <@U{pid}>");
            }
            if (extra.Count > 0) thread = ChatDB.GetThread(id)!;

            string? contents = Field("messageContents");
            if (string.IsNullOrWhiteSpace(contents))
                return Json(new { ChatMessage = (object?)null, ChatResult = 1, chatResult = 1, chatThread = ThreadWithMessagesDto(thread, account.Id, Count("messageCount", 50)) });

            string actualMessage = "";
            try { actualMessage = JObject.Parse(contents)?["Data"]?.ToString() ?? ""; } catch { }

            var msg = ChatDB.AddMessage(id, (int)account.Id, CleanContents(contents));
            if (msg == null)
                return Json(new { ChatMessage = (object?)null, ChatResult = 5, chatResult = 5, chatThread = (object?)null });
            thread = ChatDB.GetThread(id)!;

            // Everyone on the thread, sender included (the client doesn't fold its own send in).
            await Notifications.SendToPlayers(thread.PlayerIds.ToList(), JsonConvert.SerializeObject(new { Id = "ChatMessageReceived", Msg = ChatDB.MessageDto(msg) }));

            // Dev chat commands ("<=>cmd") answered by Coach (1) when a dev talks to Coach.
            if (actualMessage.StartsWith("<=>") && PlayerDB.CheckDevFlag(account.Id) && thread.PlayerIds.Contains(1))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(250);
                        string? result = DevChatCommands.ParseCommand(actualMessage.Substring(3), "ChatThread", account.Id);
                        if (string.IsNullOrWhiteSpace(result)) return;
                        var bot = ChatDB.AddMessage(id, 1, JsonConvert.SerializeObject(new { Type = 0, Version = 2, Data = "<=>" + result }));
                        if (bot != null) ChatDB.Push(ChatDB.GetThread(id)!, bot);
                    }
                    catch (Exception ex) { Console.WriteLine($"DevChat error: {ex}"); }
                });
            }

            return Json(new
            {
                ChatMessage = SentMessageDto(msg),
                ChatResult = 0,
                chatResult = 0,
                chatThread = ThreadWithMessagesDto(thread, account.Id, Count("messageCount", 50))
            });
        }

        // ---------------- membership ----------------

        // Add a player to a thread you're on. Bare ChatResult: 0 ok, 3 not a member, 4 already on it, 18 their privacy forbids it.
        [HttpPost("thread/{id:long}/member/{playerId:long}")]
        [HttpPut("thread/{id:long}/member/{playerId:long}")]
        public ContentResult AddMember(long id, long playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return Result(ChatResults.MembershipNotFound);
            ulong pid = (ulong)playerId;
            if (thread.PlayerIds.Contains(pid)) return Result(ChatResults.PlayerAlreadyOnThread);
            if (!PrivacyAllows(account.Id, pid, group: true)) return Raw(ResultCannotAddDueToPrivacy.ToString());
            if (thread.PlayerIds.Count + 1 > ChatDB.MaxThreadMembers) return Result(ChatResults.ThreadTooLarge);
            ChatDB.AddMember(id, pid);
            ChatDB.SystemNotice(id, $"Player <@U{account.Id}> added <@U{pid}>");
            Console.WriteLine($"[chat] {account.Id} added {pid} to thread {id}");
            return Result(ChatResults.Success);
        }

        // Remove a player (yourself, or anyone if you started the thread).
        [HttpDelete("thread/{id:long}/member/{playerId:long}")]
        public ContentResult RemoveMember(long id, long playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return Result(ChatResults.MembershipNotFound);
            ulong pid = (ulong)playerId;
            if (!thread.PlayerIds.Contains(pid)) return Result(ChatResults.MembershipNotFound);
            if (pid != account.Id && thread.CreatorPlayerId != account.Id) return Raw("9");   // InsufficientPermission
            ChatDB.SystemNotice(id, pid == account.Id ? $"Player <@U{pid}> left" : $"Player <@U{account.Id}> removed <@U{pid}>");
            ChatDB.RemoveMember(id, pid);
            return Result(ChatResults.Success);
        }

        // Leave: notice first (the leaver still gets the push that tells their client the thread
        // is gone), then drop the membership. The thread survives for the others.
        [HttpPost("thread/{id:long}/leave")]
        [HttpDelete("thread/{id:long}/leave")]
        public ContentResult LeaveThread(long id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return Result(ChatResults.MembershipNotFound);
            var notice = ChatDB.AddMessage(id, ChatDB.SystemSenderId,
                JsonConvert.SerializeObject(new MessageJson { Type = MessageContentType.Text, Version = 1, Data = $"Player <@U{account.Id}> left" }));
            if (notice != null)
            {
                var t = ChatDB.GetThread(id)!;
                ChatDB.Push(t, notice);
                ChatDB.Push(t, notice, "PlayerLeftChat");
            }
            ChatDB.RemoveMember(id, account.Id);
            Console.WriteLine($"[chat] {account.Id} left thread {id}");
            return Result(ChatResults.Success);
        }

        [HttpPost("thread/{id:long}/rename")]
        [HttpPut("thread/{id:long}/rename")]
        public ContentResult RenameThread(long id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return Result(ChatResults.MembershipNotFound);
            string name = Uri.UnescapeDataString(Field("name") ?? "").Trim();
            if (name.Length > 128) name = name.Substring(0, 128);
            ChatDB.SetName(id, name);
            ChatDB.SystemNotice(id, $"Player <@U{account.Id}> renamed the chat");
            return Result(ChatResults.Success);
        }

        // ---------------- per-member state ----------------

        [HttpPost("thread/{id:long}/snooze")]
        [HttpPut("thread/{id:long}/snooze")]
        public ContentResult SnoozeThread(long id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (!ChatDB.IsMember(id, account.Id)) return Result(ChatResults.MembershipNotFound);
            bool on = FormBool(Field("snooze"));
            ChatDB.UpdateState(id, account.Id, s => s.SnoozedUntil = on ? new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc) : null);
            return Result(ChatResults.Success);
        }

        [HttpPost("thread/{id:long}/favorite")]
        [HttpPut("thread/{id:long}/favorite")]
        public async Task<ContentResult> FavoriteThread(long id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (!ChatDB.IsMember(id, account.Id)) return Result(ChatResults.MembershipNotFound);

            // What the client sends for the star was never captured, and reading only a form field "favorite"
            // meant any other spelling (a JSON body, a bare true/false, another name) read as "false" - the star
            // un-favourited on every press and the thread list and the open thread disagreed. Look in every
            // place, and when nothing says on/off TOGGLE the current state; log the raw call either way.
            string raw = await RawBody(400);
            bool? want = null;
            foreach (var n in new[] { "favorite", "favorited", "isFavorited", "isFavorite", "isFavourite", "favourite", "value" })
            {
                var v = Field(n);
                if (!string.IsNullOrWhiteSpace(v)) { want = FormBool(v); break; }
            }
            if (want == null && !string.IsNullOrWhiteSpace(raw))
            {
                string t = raw.Trim();
                if (t.StartsWith("{"))
                {
                    try
                    {
                        foreach (var pr in Newtonsoft.Json.Linq.JObject.Parse(t).Properties())
                            if (pr.Name.Contains("avo", StringComparison.OrdinalIgnoreCase) || pr.Name.Equals("value", StringComparison.OrdinalIgnoreCase)) { want = FormBool(pr.Value.ToString()); break; }
                    }
                    catch { }
                }
                else if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "1" || t == "0")
                    want = FormBool(t);
            }
            var thread = ChatDB.GetThread(id);
            bool current = thread != null && ChatDB.StateFor(thread, account.Id).IsFavorited;
            bool on = want ?? !current;
            ChatDB.UpdateState(id, account.Id, s => s.IsFavorited = on);
            Console.WriteLine($"[chat] {account.Id} {Request.Method} thread/{id}/favorite ct='{Request.ContentType}' query='{Request.QueryString}' body='{raw}' -> " +
                              $"{(want == null ? "no flag sent, toggled" : "flag " + want)} {current} -> {on}");
            return Result(ChatResults.Success);
        }

        [HttpPost("thread/{id:long}/read")]
        [HttpPut("thread/{id:long}/read")]
        public ContentResult MarkThreadRead(long id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (!ChatDB.IsMember(id, account.Id)) return Raw("", 404);
            ChatDB.MarkRead(id, account.Id);
            return Result(ChatResults.Success);
        }

        [HttpPost("thread/{id:long}/message/{messageId:long}/read")]
        [HttpPut("thread/{id:long}/message/{messageId:long}/read")]
        public ContentResult MarkMessageRead(long id, long messageId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (!ChatDB.IsMember(id, account.Id)) return Raw("", 404);
            ChatDB.MarkRead(id, account.Id, messageId);
            return Result(ChatResults.Success);
        }

        // ---------------- anything else the client asks the chat service ----------------

        // Logged so a route this build calls that isn't implemented yet names itself (method,
        // path, query, form keys) instead of vanishing as a silent 404.
        [Route("{*rest}")]
        [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH")]
        public async Task<ContentResult> Unhandled(string? rest)
        {
            string fields = Request.HasFormContentType ? string.Join(",", Request.Form.Keys) : "";
            Console.WriteLine($"[chat] UNHANDLED {Request.Method} /chat/{rest}{Request.QueryString} ct='{Request.ContentType}' fields=[{fields}] body={await RawBody()}");
            return Raw("", 404);
        }
    }
}
