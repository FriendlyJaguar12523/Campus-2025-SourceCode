using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Campus_live_rewrite.Controllers
{
    // The "Campus Net" web portal (/campus_net/*: login, home, online_players, account) was a
    // legacy fourth web surface and has been REMOVED - only three surfaces exist now: the game
    // client, the website (camprec.net), and the admin panel. Its login mint was already
    // commented out; its pages are no longer routed.
    //
    // The one endpoint kept here is the GLOBAL photo feed, which is a general game/website API
    // and was never Campus-Net-specific - it just happened to live in this controller.
    public class CampusNetController : Controller
    {
        [HttpGet("/api/images/v3/feed/global")]
        public ContentResult GetGlobalFeedImages()
        {
            var images = ImageMetadataDB.GetGlobalImages();
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(images),
                ContentType = "application/json",
                StatusCode = 200
            };
        }
    }
}
