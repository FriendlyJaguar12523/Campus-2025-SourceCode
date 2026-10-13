using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>Website Settings > Do not disturb (see DoNotDisturb). Website session only (campus_site cookie).</summary>
    [Route("api/site/dnd")]
    public class SiteDndController : ControllerBase
    {
        private player_info? CurrentUser()
        {
            string token = Request.Cookies["campus_site"] ?? "";
            if (string.IsNullOrEmpty(token)) return null;
            string data = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_")) return null;
            try { return JsonConvert.DeserializeObject<player_info>(data); } catch { return null; }
        }

        public class DndBody { public int minutes { get; set; } }

        private static object State(ulong id)
        {
            var until = DoNotDisturb.Until(id);
            return new
            {
                ok = true,
                active = until != null,
                until,
                minutesLeft = until == null ? 0 : (int)Math.Ceiling((until.Value - DateTime.UtcNow).TotalMinutes),
                maxMinutes = DoNotDisturb.MaxMinutes
            };
        }

        [HttpGet("")]
        public IActionResult Get()
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            return Ok(State(me.Id));
        }

        // POST /api/site/dnd {minutes}  - minutes > 0 turns it on for that long (capped at 24 h), 0 turns it off
        [HttpPost("")]
        public IActionResult Set([FromBody] DndBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null || body.minutes < 0) return BadRequest(new { ok = false, error = "invalid time" });
            var until = DoNotDisturb.Set(me.Id, body.minutes);
            Console.WriteLine($"[dnd] {me.Id} {(until == null ? "turned Do Not Disturb off" : $"on until {until:HH:mm}Z")}");
            return Ok(State(me.Id));
        }
    }
}
