using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// CampusNet's stuck-load report (CampusNet Services/StuckLoad.cs): a player joined a Photon
    /// room and after 90 s has neither streamed a transform nor asked the master client for the
    /// room - they are on the loading screen for good (25%, "Waiting to receive buffered
    /// message"). The instance is marked broken, so matchmaking stops putting anyone new in it
    /// (HeartbeatDB.IsInstanceBroken), and the player is moved out of it (the same frame a room
    /// kick sends, presence rewritten to their dorm) with a message saying why, instead of
    /// loading forever. Local callers only, like /photon/placement.
    /// </summary>
    public class StuckLoadController : ControllerBase
    {
        [HttpPost("/photon/stuckload")]
        public IActionResult Report()
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            bool local = ip != null && System.Net.IPAddress.IsLoopback(ip)
                         && string.IsNullOrEmpty(Request.Headers["X-Forwarded-For"].ToString());
            if (!local) return NotFound();

            string id = "", room = "", secs = "";
            try
            {
                if (Request.HasFormContentType)
                {
                    id = Request.Form["id"].ToString();
                    room = Request.Form["room"].ToString();
                    secs = Request.Form["seconds"].ToString();
                }
            }
            catch { }
            if (!ulong.TryParse(id, out var playerId)) return Content("ok", "text/plain");

            var inst = HeartbeatDB.FindInstanceByPhotonName(room);
            if (inst != null && !HeartbeatDB.IsInstanceBroken(inst))
            {
                HeartbeatDB.MarkInstanceBroken(inst.roomInstanceId);
                Console.WriteLine($"[stuck-load] instance {inst.roomInstanceId} of room {inst.roomId} ('{room}') marked broken - no new players will be matchmade into it for {HeartbeatDB.BrokenInstanceWindow.TotalMinutes:0} min");
            }
            Console.WriteLine($"[stuck-load] {playerId} stuck loading '{room}' for {secs}s - moving them out");
            RoomModeration.RemoveFromRoom(playerId, (ulong)(inst?.roomId ?? 0), inst?.roomInstanceId ?? 0,
                "That room instance didn't finish loading, so you've been moved out of it. Join again to get a fresh instance.",
                0, false, false);
            return Content("ok", "text/plain");
        }
    }
}
