using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using static Campus_live_rewrite.api.server.Classes.db_class.ChatDBClasses;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// Admin panel "Chat" tab: every DM / group chat (in-game or from the website - same ChatDB), searchable by player, with a
    /// full transcript. Moderation only - nothing here can send a message or change a thread. Dev OR mod
    /// (ValidateTokenDevOrMod): mods get read access to this tab, per the user's explicit "Players + Chat (view only)"
    /// scope for the mod role.
    /// </summary>
    public class AdminChatController : ControllerBase
    {
        private static ContentResult J(object o, int status = 200) => new() { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = status };

        private static object? Basic(ulong id)
        {
            var full = PlayerDB.GetFullAccount(id);
            if (full == null) return new { id, username = $"(deleted #{id})", display_name = $"(deleted #{id})", profile_image = "" };
            return new { id = full.playerid, username = full.player?.Username ?? "", display_name = full.player?.DisplayName ?? full.player?.Username ?? "", profile_image = full.player?.profileImage ?? "" };
        }

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

        private static readonly Regex TagRe = new(@"<@U(\d+)>", RegexOptions.Compiled);
        private static string ResolveTags(string text)
        {
            return TagRe.Replace(text, m =>
            {
                var b = Basic(ulong.Parse(m.Groups[1].Value)) as dynamic;
                return b != null ? (string)b.display_name : "a player";
            });
        }

        // `t` must already carry its latest message (ChatDB.AllThreads / GetThread both do this) - no extra DB load here except
        // the one cheap Count for messageCount, so listing a page of threads stays fast even with a lot of chat history.
        private static object ThreadRow(ChatThread t)
        {
            bool group = t.ChatThreadType == ChatDB.ThreadTypeUser && t.PlayerIds.Count > 2;
            var latest = (t.Messages ?? new()).OrderByDescending(m => m.ChatMessageId).FirstOrDefault();
            return new
            {
                id = t.ChatThreadId,
                type = t.ChatThreadType,
                isGroup = group,
                name = t.ChatThreadName ?? "",
                members = t.PlayerIds.OrderBy(x => x).Select(Basic).ToList(),
                messageCount = ChatDB.MessageCount(t.ChatThreadId),
                latestText = latest != null ? PreviewText(latest.Contents) : "",
                latestAt = (latest?.TimeSent ?? t.LastActivityAt ?? t.CreatedAt)?.ToUniversalTime(),
                createdAt = t.CreatedAt?.ToUniversalTime(),
            };
        }

        /// <summary>All threads, newest activity first, optionally narrowed to one player (id or username) or a text search over message contents.</summary>
        [HttpGet("/admin/api/chat/threads")]
        public ContentResult Threads(string q = "", int skip = 0, int take = 50)
        {
            var (_, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            take = Math.Clamp(take, 1, 200);
            q = (q ?? "").Trim();

            IEnumerable<ChatThread> all = ChatDB.AllThreads();
            string matchedPlayer = "";
            if (q.Length > 0)
            {
                ulong pid = 0;
                if (ulong.TryParse(q, out var id)) pid = id;
                else { var found = PlayerDB.GetAccountIdByUsernameCI(q.TrimStart('@')); if (found != 0) pid = found; }

                if (pid != 0)
                {
                    matchedPlayer = $" (player {pid})";
                    all = all.Where(t => t.PlayerIds.Contains(pid));
                }
                else
                {
                    // No matching player - fall back to a text search over message contents. Capped: this walks the messages
                    // collection, fine for a moderation tool, not for a hot path.
                    string needle = q.ToLowerInvariant();
                    var hitThreadIds = ChatDB.SearchMessageContents(needle, 500).Select(m => m.ChatThreadId).ToHashSet();
                    all = all.Where(t => hitThreadIds.Contains(t.ChatThreadId));
                }
            }

            var ordered = all.OrderByDescending(t => t.LastActivityAt ?? t.CreatedAt ?? DateTime.MinValue).ToList();
            var page = ordered.Skip(Math.Max(0, skip)).Take(take).Select(ThreadRow).ToList();
            return J(new { success = true, total = ordered.Count, threads = page, note = matchedPlayer });
        }

        /// <summary>One thread's full transcript - every message, oldest first, sender resolved, system tags resolved.</summary>
        [HttpGet("/admin/api/chat/thread/{id:long}")]
        public ContentResult Transcript(long id)
        {
            var (_, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            var t = ChatDB.GetThread(id);
            if (t == null) return J(new { success = false, message = "No such thread." }, 404);
            var messages = (t.Messages ?? new()).OrderBy(m => m.ChatMessageId).Select(m => new
            {
                id = m.ChatMessageId,
                senderId = m.SenderPlayerId,
                sender = m.SenderPlayerId == ChatDB.SystemSenderId ? null : Basic((ulong)m.SenderPlayerId),
                system = m.SenderPlayerId == ChatDB.SystemSenderId,
                at = m.TimeSent.ToUniversalTime(),
                text = ResolveTags(PreviewText(m.Contents)),
                moderationState = m.ModerationState,
            }).ToList();
            return J(new { success = true, thread = ThreadRow(t), messages });
        }

        /// <summary>The same transcript as a plain-text file - for pasting into a report or saving.</summary>
        [HttpGet("/admin/api/chat/thread/{id:long}/transcript.txt")]
        public IActionResult TranscriptFile(long id)
        {
            var (admin, error) = AuthHelper.ValidateTokenDevOrMod(HttpContext);
            if (error != null) return error;
            var t = ChatDB.GetThread(id);
            if (t == null) return NotFound();

            var sb = new StringBuilder();
            var members = t.PlayerIds.OrderBy(x => x).Select(Basic).ToList();
            sb.AppendLine($"Chat transcript - thread {t.ChatThreadId}" + (string.IsNullOrWhiteSpace(t.ChatThreadName) ? "" : $" \"{t.ChatThreadName}\""));
            sb.AppendLine($"Members: {string.Join(", ", members.Select(m => ((dynamic)m!).username + " (#" + ((dynamic)m!).id + ")"))}");
            sb.AppendLine($"Exported {DateTime.UtcNow:u} by {admin!.Username}");
            sb.AppendLine(new string('-', 60));
            foreach (var m in (t.Messages ?? new()).OrderBy(x => x.ChatMessageId))
            {
                string who = m.SenderPlayerId == ChatDB.SystemSenderId ? "[system]" : ((dynamic?)Basic((ulong)m.SenderPlayerId))?.username ?? $"#{m.SenderPlayerId}";
                sb.AppendLine($"[{m.TimeSent:u}] {who}: {ResolveTags(PreviewText(m.Contents))}");
            }
            Console.WriteLine($"[admin] {admin.Username} ({admin.Id}) exported the transcript of thread {id}");
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain", $"chat-thread-{id}.txt");
        }
    }
}
