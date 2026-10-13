using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using static Campus_live_rewrite.api.server.Classes.db_class.ChatDBClasses;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// The same DMs / group chats as in-game (ChatController, ChatDB) - reachable from the website too, so a message sent from
    /// camprec.net shows up in the game and vice versa. Auth is the WEBSITE session (campus_site cookie), never a game token -
    /// same separation every other Site* controller keeps (see AuthHelper.ValidateToken's comment on rejected surfaces).
    /// Privacy, sanitising and the thread/message model are all the real ones (ChatController's helpers, widened to internal
    /// rather than duplicated, so the rule never drifts between the two surfaces).
    /// </summary>
    [Route("api/site/chat")]
    public class SiteChatController : ControllerBase
    {
        private const string SiteCookie = "campus_site";

        private player_info? CurrentUser()
        {
            string token = Request.Cookies[SiteCookie] ?? "";
            if (string.IsNullOrEmpty(token)) return null;
            string playerData = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(playerData) || playerData.StartsWith("error_")) return null;
            try { return JsonConvert.DeserializeObject<player_info>(playerData); } catch { return null; }
        }

        private static ContentResult J(object o, int status = 200) => new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };
        private static ContentResult Unauthed() => J(new { ok = false, error = "not signed in" }, 401);

        // ---- small public-info projection, matching SiteController.PublicUser's field names so the website's existing avatar/name code works unchanged ----
        private static object? Basic(ulong id)
        {
            var full = PlayerDB.GetFullAccount(id);
            if (full == null) return null;
            return new
            {
                id = full.playerid,
                username = full.player?.Username ?? "",
                display_name = full.player?.DisplayName ?? full.player?.Username ?? "",
                profile_image = full.player?.profileImage ?? "",
            };
        }

        /// <summary>The message's plain text, or a bracketed placeholder for a content type the website doesn't render inline.</summary>
        private static string PreviewText(string contents)
        {
            try
            {
                var mj = JsonConvert.DeserializeObject<MessageJson>(contents);
                if (mj == null) return "";
                return mj.Type switch { MessageContentType.Text => mj.Data ?? "", MessageContentType.Photo => "[Photo]", MessageContentType.PartyInvite => "[Party invite]", _ => "" };
            }
            catch { return ""; }
        }

        private static object ThreadRow(ChatThread t, ulong viewer)
        {
            var others = t.PlayerIds.Where(id => id != viewer).ToList();
            bool group = t.PlayerIds.Count > 2;
            var latest = (t.Messages ?? new()).OrderByDescending(m => m.ChatMessageId).FirstOrDefault();
            var st = ChatDB.StateFor(t, viewer);
            return new
            {
                id = t.ChatThreadId,
                isGroup = group,
                name = group ? (string.IsNullOrWhiteSpace(t.ChatThreadName) ? "Group chat" : t.ChatThreadName) : null,
                members = others.Select(Basic).Where(b => b != null).ToList(),
                latestText = latest != null ? PreviewText(latest.Contents) : "",
                latestAt = latest?.TimeSent.ToUniversalTime(),
                latestSenderId = latest?.SenderPlayerId,
                unread = latest != null && latest.ChatMessageId > st.LastReadMessageId,
            };
        }

        private static object MessageRow(ChatMessage m) => new
        {
            id = m.ChatMessageId,
            threadId = m.ChatThreadId,
            senderId = m.SenderPlayerId,
            sender = m.SenderPlayerId == ChatDB.SystemSenderId ? null : Basic((ulong)m.SenderPlayerId),
            system = m.SenderPlayerId == ChatDB.SystemSenderId,
            at = m.TimeSent.ToUniversalTime(),
            text = PreviewText(m.Contents),
        };

        // ---- listing ----

        [HttpGet("thread")]
        public ContentResult ListThreads()
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            var threads = ChatDB.GetThreadsForPlayer(account.Id, 100)
                .Where(t => t.ChatThreadType == ChatDB.ThreadTypeUser)   // the website shows DMs/groups only, not the in-room party thread
                .OrderByDescending(t => (t.Messages ?? new()).Count > 0 ? t.Messages.Max(m => m.ChatMessageId) : 0)
                .ToList();
            return J(new { ok = true, threads = threads.Select(t => ThreadRow(t, account.Id)).ToList() });
        }

        [HttpGet("thread/{id:long}")]
        public ContentResult GetThread(long id, [FromQuery] int count = 60)
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return J(new { ok = false, error = "not found" }, 404);
            ChatDB.MarkRead(id, account.Id);
            var messages = (thread.Messages ?? new()).OrderByDescending(m => m.ChatMessageId).Take(Math.Clamp(count, 1, 200)).OrderBy(m => m.ChatMessageId).Select(MessageRow).ToList();
            return J(new { ok = true, thread = ThreadRow(thread, account.Id), messages });
        }

        // Poll for anything newer than `after` - the website has no live socket, so the page calls this every few seconds
        // while a conversation is open. Cheap: LiteDB reads already in memory, same data ChatDB.Push sends to the game.
        [HttpGet("thread/{id:long}/since/{after:long}")]
        public ContentResult SinceMessage(long id, long after)
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return J(new { ok = false, error = "not found" }, 404);
            var messages = (thread.Messages ?? new()).Where(m => m.ChatMessageId > after).OrderBy(m => m.ChatMessageId).Select(MessageRow).ToList();
            if (messages.Count > 0) ChatDB.MarkRead(id, account.Id);
            return J(new { ok = true, messages });
        }

        // ---- who to message ----

        [HttpGet("friends")]
        public ContentResult Friends()
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            var ids = FriendsDB.GetRelationships(account.Id).Where(r => r.RelationshipType == RelationshipType.Friend && r.OtherPlayerID != account.Id).Select(r => r.OtherPlayerID);
            return J(new { ok = true, friends = ids.Select(Basic).Where(b => b != null).ToList() });
        }

        [HttpGet("lookup")]
        public ContentResult Lookup([FromQuery] string username)
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            username = (username ?? "").Trim().TrimStart('@');
            if (username.Length == 0) return J(new { ok = true, user = (object?)null });
            ulong id = PlayerDB.GetAccountIdByUsernameCI(username);
            return J(new { ok = true, user = id == 0 || id == account.Id ? null : Basic(id) });
        }

        // ---- creating / sending ----

        public sealed class CreateBody { public List<ulong> Ids { get; set; } = new(); public string? Name { get; set; } public string? Message { get; set; } }

        [HttpPost("thread")]
        public ContentResult CreateThread([FromBody] CreateBody b)
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            var members = (b?.Ids ?? new()).Where(id => id != account.Id).Distinct().ToList();
            if (members.Count == 0) return J(new { ok = false, error = "pick at least one person" }, 400);
            if (members.Count + 1 > ChatDB.MaxThreadMembers) return J(new { ok = false, error = $"a thread can have at most {ChatDB.MaxThreadMembers} people" }, 400);

            // Unlike the game client (which only ever sends ids it got from a friends list or a search result), a raw HTTP call here
            // could hand us any number - PrivacyAllows doesn't check existence (a dev sender bypasses it entirely), and ChatDB would
            // happily store a non-existent id as a permanent thread member forever. Drop anything that isn't a real account first.
            var noSuchAccount = members.Where(m => PlayerDB.GetFullAccount(m) == null).ToList();
            members = members.Except(noSuchAccount).ToList();
            if (members.Count == 0) return J(new { ok = false, error = "no such player" }, 404);

            bool group = members.Count > 1;
            var blocked = members.Where(m => !ChatController.PrivacyAllows(account.Id, m, group)).ToList();
            if (blocked.Count == members.Count)
                return J(new { ok = false, error = group ? "none of those people can be added to a group by you" : "that player's chat privacy setting does not allow this" }, 403);
            members = members.Except(blocked).ToList();

            var all = new List<ulong> { account.Id }; all.AddRange(members);
            var thread = ChatDB.GetOrCreateThreadWithMembers(all, account.Id);
            if (group && !string.IsNullOrWhiteSpace(b!.Name)) thread = ChatDB.RenameThread(thread.ChatThreadId, account.Id, b.Name.Trim()) ?? thread;

            string? text = b?.Message;
            if (!string.IsNullOrWhiteSpace(text))
            {
                string contents = JsonConvert.SerializeObject(new MessageJson { Type = MessageContentType.Text, Version = 1, Data = text.Trim() });
                contents = ChatController.CleanContents(contents);
                var msg = ChatDB.AddMessage(thread.ChatThreadId, (int)account.Id, contents);
                thread = ChatDB.GetThread(thread.ChatThreadId)!;
                if (msg != null) ChatDB.Push(thread, msg);
            }
            Console.WriteLine($"[sitechat] {account.Id} opened thread {thread.ChatThreadId} with [{string.Join(",", members)}] from the website");
            return J(new { ok = true, thread = ThreadRow(thread, account.Id) });
        }

        public sealed class MessageBody { public string Text { get; set; } = ""; }

        [HttpPost("thread/{id:long}/message")]
        public ContentResult SendMessage(long id, [FromBody] MessageBody b)
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            string text = (b?.Text ?? "").Trim();
            if (text.Length == 0) return J(new { ok = false, error = "empty message" }, 400);
            if (text.Length > 1000) return J(new { ok = false, error = "too long" }, 400);
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return J(new { ok = false, error = "not found" }, 404);

            string contents = JsonConvert.SerializeObject(new MessageJson { Type = MessageContentType.Text, Version = 1, Data = text });
            contents = ChatController.CleanContents(contents);
            var msg = ChatDB.AddMessage(id, (int)account.Id, contents);
            if (msg == null) return J(new { ok = false, error = "could not send" }, 500);
            var fresh = ChatDB.GetThread(id)!;
            ChatDB.Push(fresh, msg);   // the same push the game client gets - a web message shows up in-room live
            return J(new { ok = true, message = MessageRow(msg) });
        }

        [HttpPost("thread/{id:long}/rename")]
        public ContentResult Rename(long id, [FromBody] CreateBody b)
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            string name = (b?.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 100) return J(new { ok = false, error = "bad name" }, 400);
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return J(new { ok = false, error = "not found" }, 404);
            var renamed = ChatDB.RenameThread(id, account.Id, name);
            if (renamed == null) return J(new { ok = false, error = "could not rename" }, 400);
            return J(new { ok = true, thread = ThreadRow(renamed, account.Id) });
        }

        public sealed class MemberBody { public ulong PlayerId { get; set; } }

        [HttpPost("thread/{id:long}/member")]
        public ContentResult AddMember(long id, [FromBody] MemberBody b)
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            var thread = ChatDB.GetThread(id);
            if (thread == null || !thread.PlayerIds.Contains(account.Id)) return J(new { ok = false, error = "not found" }, 404);
            if (b == null || b.PlayerId == 0 || thread.PlayerIds.Contains(b.PlayerId)) return J(new { ok = false, error = "bad player" }, 400);
            if (PlayerDB.GetFullAccount(b.PlayerId) == null) return J(new { ok = false, error = "no such player" }, 404);
            if (!ChatController.PrivacyAllows(account.Id, b.PlayerId, group: true)) return J(new { ok = false, error = "that player's chat privacy setting does not allow this" }, 403);
            var updated = ChatDB.AddMember(id, b.PlayerId);
            if (updated == null) return J(new { ok = false, error = "could not add" }, 400);
            return J(new { ok = true, thread = ThreadRow(updated, account.Id) });
        }

        [HttpPost("thread/{id:long}/leave")]
        public ContentResult Leave(long id)
        {
            var account = CurrentUser();
            if (account == null) return Unauthed();
            if (!ChatDB.IsMember(id, account.Id)) return J(new { ok = false, error = "not found" }, 404);
            ChatDB.LeaveThread(id, account.Id);
            return J(new { ok = true });
        }
    }
}
