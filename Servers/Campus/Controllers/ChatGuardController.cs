using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// CampusNet's room-chat check (CampusNet Services/ChatGuard.cs). Room chat crosses the game
    /// server as RPC event 36 carrying "&lt;noparse&gt;text&lt;/noparse&gt;"; a normal client has
    /// already run that text through POST /api/sanitize/v1, so it arrives masked. Text our filter
    /// WOULD still change therefore never went through it - chat-prompt text echoed into chat by a
    /// circuit, or a client that skips the filter (seen live: a slur in plain text, 2026-09-26).
    /// Answers "bypass" for such text (CampusNet then drops the message) and disconnects the sender
    /// with a timeout (AutoEnforce). Local callers only, like /photon/placement: loopback and no
    /// X-Forwarded-For, i.e. not a request the public proxy passed on.
    /// </summary>
    public class ChatGuardController : ControllerBase
    {
        [HttpPost("/photon/chatguard")]
        public IActionResult Check()
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            bool local = ip != null && System.Net.IPAddress.IsLoopback(ip)
                         && string.IsNullOrEmpty(Request.Headers["X-Forwarded-For"].ToString());
            if (!local) return NotFound();

            string id = "", room = "", text = "";
            try
            {
                if (Request.HasFormContentType)
                {
                    id = Request.Form["id"].ToString();
                    room = Request.Form["room"].ToString();
                    text = Request.Form["text"].ToString();
                }
            }
            catch { }
            if (!ulong.TryParse(id, out var playerId) || string.IsNullOrEmpty(text)) return Content("ok", "text/plain");
            if (ConfigCampus.AllowSwears || !ConfigCampus.ChatBypassEnforce) return Content("ok", "text/plain");

            // Would our own filter change it? A legit client's text is already filtered, so
            // filtering it again changes nothing.
            string filtered = Campus_live_rewrite.api.server.Classes.Sanitize.SanitizeMessage(text, '*');
            if (filtered == text) return Content("ok", "text/plain");

            Console.WriteLine($"[chat-guard] {playerId} sent unfiltered chat in '{room}': \"{text}\" (filtered: \"{filtered}\") - dropped");
            AutoEnforce.DisconnectAndTimeout(playerId, Moderation_Detail.ReportCategory.Harassment, ConfigCampus.ChatBypassTimeoutSeconds,
                "Disconnected and timed out: you sent chat that skipped the chat filter.",
                $"chat filter bypass in '{room}': \"{(text.Length > 200 ? text[..200] + "..." : text)}\"");
            return Content("bypass", "text/plain");
        }
    }
}
