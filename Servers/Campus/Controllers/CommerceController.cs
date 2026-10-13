using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    [Route("Commerce")]
    public class CommerceController : Controller
    {
        [HttpGet("purchasecampaign/allcurrent/v2")]
        public IActionResult GetAllCurrentPurchaseCampaignsV2()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpPost("purchase/v1/cleanuppending")]
        public IActionResult CleanUpPendingPurchases()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpGet("api/catalog/v1/all")]
        public async Task<IActionResult> GetAllCatalogItemsV1(bool onlyAvailableSkus = true)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "AllCatalog.json");
            string content = await System.IO.File.ReadAllTextAsync(path);
            // Was: Ok(<string>). ASP.NET's StringOutputFormatter writes a raw string as
            // Content-Type: text/plain, so these JSON payloads were served mislabelled
            // and the client would not parse them. Verified live: /outfits/me returned
            // text/plain while /api/avatar/v2 returned application/json. Same bytes,
            // correct content type.
            return new ContentResult { Content = content, ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("reminder/currentTokenBundles/v2")]
        public IActionResult GetCurrentTokenBundlesV2()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpGet("purchaseRestriction/isplayerrestricted")]
        public IActionResult IsPlayerRestricted()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Content("false", "application/json");
        }
    }
}
