using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    // Room comments - the notes players pin in a room's scene. The client talks to this on the
    // /RoomComments base URL (WebController's service map). Ported from RecFlare's roomcomments
    // service; there were no handlers before, so both routes 404'd.
    [ApiController]
    public class RoomCommentsController : ControllerBase
    {
        // A room's comments, newest first. PUBLIC - a comment is a fixture of the room and the
        // client fetches the list on load, so no token is needed. An unknown room simply has
        // none ([], not a 404).
        [HttpGet("/RoomComments/comments/get/{roomId}")]
        public IActionResult GetComments(long roomId,
            [FromQuery] int? count = null, [FromQuery] int? minId = null, [FromQuery] int? subRoomId = null)
        {
            var comments = RoomCommentsDB.Get(roomId, count, minId, subRoomId);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(comments),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Leave a comment in a room. Auth-gated: the author is the bearer token's account, never
        // trusted from the body. Blank message or missing subRoomId -> 400. Message over 1000
        // chars is truncated, not rejected. Answers the created comment so the client can draw
        // the bubble it just placed without re-fetching the whole list.
        [HttpPost("/RoomComments/comments/create/{roomId}")]
        public IActionResult CreateComment(long roomId,
            [FromForm] string? message, [FromForm] string? subRoomId, [FromForm] string? style,
            [FromForm] string? positionX, [FromForm] string? positionY, [FromForm] string? positionZ)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            if (!long.TryParse(subRoomId, out long sr))
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 400 };

            double F(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0d;
            int I(string? s) => int.TryParse(s, out var v) ? v : 0;

            var comment = RoomCommentsDB.Create(roomId, account.Id, sr, message ?? "", I(style),
                F(positionX), F(positionY), F(positionZ));

            if (comment == null)   // blank message
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 400 };

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(comment),
                ContentType = "application/json",
                StatusCode = 200
            };
        }
    }
}
