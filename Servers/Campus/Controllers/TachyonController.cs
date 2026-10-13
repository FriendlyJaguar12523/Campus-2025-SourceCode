using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.Controllers
{
    // ---------------------------------------------------------------------------------
    // Room lookup for the Tachyon voice server.
    //
    // Tachyon has NO room id on its wire - the client's voice ConnectionData carries only
    // the AccountId (plaintext) plus opaque RSA blobs. So Tachyon resolves which room a
    // speaker is in by asking us: GET /tachyon?id=<AccountId>, expecting a BARE numeric
    // roomInstanceId in text/plain (see tachyon-server-master/src/room_lookup.cpp - it
    // strtoull's the body and treats 0 / non-200 / non-numeric as "isolate this client",
    // i.e. they hear no one). It scopes its Dissonance relay per returned room id.
    //
    // Set Tachyon's config  room_url = https://<host>/tachyon  (it appends ?id=<n>).
    //
    // Unauthenticated on purpose: room_lookup.cpp issues a plain GET with no token, exactly
    // as the RecFlare reference does. The only thing exposed is which room-instance a given
    // account is in, keyed on a numeric id we already hand out publicly; the id is validated
    // as an integer before use. Returns "0" (nowhere) for anything we can't positively place,
    // which fails safe: Tachyon isolates the client rather than leaking cross-room voice.
    // ---------------------------------------------------------------------------------
    [ApiController]
    public class TachyonController : ControllerBase
    {
        [HttpGet("/tachyon")]
        public IActionResult ResolveRoom([FromQuery] string? id)
        {
            // Local callers only: the voice server runs on this box (tachyon.conf room_url =
            // http://127.0.0.1:8080/tachyon). From outside, this answered "which room instance is
            // account N in" for ANY account with no login - a free player locator. Requests via
            // Caddy carry X-Forwarded-For, so a proxied (external) request is refused too.
            var ip = HttpContext.Connection.RemoteIpAddress;
            bool local = ip != null && System.Net.IPAddress.IsLoopback(ip)
                         && string.IsNullOrEmpty(Request.Headers["X-Forwarded-For"].ToString());
            if (!local) return NotFound();

            if (string.IsNullOrWhiteSpace(id) || !ulong.TryParse(id, out var accountId))
                return Content("0", "text/plain");

            // Stored placement, not PeekPresence: see HeartbeatDB.PlacementForVoice.
            long instance = HeartbeatDB.PlacementForVoice(accountId)?.roomInstanceId ?? 0;
            if (instance < 0) instance = 0;   // dorm/home (-1) never uses Tachyon

            return Content(instance.ToString(), "text/plain");
        }

        // CampusNet's join check (CampusNet Services/JoinCheck.cs): the Photon room this account
        // was matchmade into, "" when none. Local callers only - same rule as /tachyon.
        // CampusNet's kick enforcement (CampusNet Services/JoinBlocks.cs): every Photon room a
        // player is currently kicked from, one "accountId photonRoom" per line. Local callers only.
        [HttpGet("/photon/joinblocks")]
        public IActionResult PhotonJoinBlocks()
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            bool local = ip != null && System.Net.IPAddress.IsLoopback(ip)
                         && string.IsNullOrEmpty(Request.Headers["X-Forwarded-For"].ToString());
            if (!local) return NotFound();
            var lines = Campus_live_rewrite.api.server.InstanceBlocks.ActivePhotonBlocks().Select(b => $"{b.player} {b.photonRoom}");
            return Content(string.Join("\n", lines), "text/plain");
        }

        // CampusNet's AFK exemption (CampusNet Services/AfkExempt.cs): is this account staff
        // (developer / moderator flag) or an owner / co-owner of the room behind this Photon room?
        // Answers "staff", "owner", "co-owner" or "0". Local callers only.
        [HttpGet("/photon/afkexempt")]
        public IActionResult PhotonAfkExempt([FromQuery] string? id, [FromQuery] string? room)
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            bool local = ip != null && System.Net.IPAddress.IsLoopback(ip)
                         && string.IsNullOrEmpty(Request.Headers["X-Forwarded-For"].ToString());
            if (!local) return NotFound();
            if (string.IsNullOrWhiteSpace(id) || !ulong.TryParse(id, out var accountId)) return Content("0", "text/plain");
            if (PlayerDB.CheckDevFlag(accountId) || PlayerDB.CheckModFlag(accountId)) return Content("staff", "text/plain");
            var inst = string.IsNullOrWhiteSpace(room) ? null : HeartbeatDB.FindInstanceByPhotonName(room!);
            var r = inst != null ? RoomDB.GetRoom((ulong)inst.roomId) : null;
            if (r != null && r.CreatorAccountId == accountId) return Content("owner", "text/plain");
            if (r != null && RoomDB.IsCoOwner(r, accountId)) return Content("co-owner", "text/plain");
            return Content("0", "text/plain");
        }

        [HttpGet("/photon/placement")]
        public IActionResult PhotonPlacement([FromQuery] string? id)
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            bool local = ip != null && System.Net.IPAddress.IsLoopback(ip)
                         && string.IsNullOrEmpty(Request.Headers["X-Forwarded-For"].ToString());
            if (!local) return NotFound();
            if (string.IsNullOrWhiteSpace(id) || !ulong.TryParse(id, out var accountId)) return Content("", "text/plain");
            return Content(HeartbeatDB.PlacementForVoice(accountId)?.photonRoomIdYeee ?? "", "text/plain");
        }
    }
}
