using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// The game side of limited-time progression events (api/server/ProgressionEvents.cs).
    ///
    /// GET /api/progressionEvents/active is the one route whose shape is certain (the client asks
    /// on every room join). The client's API table also has record / collect / per-room / XP-boost
    /// calls whose exact paths are not pinned down yet ({0}/record/{1}, {0}/{1}/collect,
    /// {0}/event/{1}, {0}/events/{1}, {0}/room/{1}, {0}/{1}/xpboosts/{2}/previewEarnedXp), so
    /// everything else under /api/progressionEvents lands in one handler that LOGS the request
    /// ("[progression-events] CALL ...") and answers by what the path says: collect/claim pays
    /// out, record/event returns the player's record, room/xpboosts return empty.
    /// </summary>
    public class ProgressionEventsController : Controller
    {
        private static ContentResult J(JToken t) => new() { Content = t.ToString(Newtonsoft.Json.Formatting.None), ContentType = "application/json", StatusCode = 200 };

        // The client reads this as a NUMBER (Player.log: "expected:'Number Token', actual:'['",
        // which our old [] also produced on every room join): the running event's id, 0 = none.
        // It then asks for the event and the player's record by that id (handled below).
        [HttpGet("/api/progressionEvents/active")]
        public IActionResult Active()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var ev = ProgressionEvents.VisibleTo(account.Id).FirstOrDefault();
            if (ev == null) return Content("0", "application/json");
            if (_served.TryAdd(account.Id, 0))
                Console.WriteLine($"[progression-events] {account.Id} is told event {ev.NumericId} '{ev.Name}' is active");
            ProgressionEvents.PushRecord(ev, ProgressionEvents.GetRecord(ev.Id, account.Id));
            return Content(ev.NumericId.ToString(), "application/json");
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _served = new();

        private static readonly System.Text.RegularExpressions.Regex GuidRx =
            new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        [Route("/api/progressionEvents/{**rest}")]
        public async Task<IActionResult> Other(string rest)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            string body = "";
            try
            {
                if (Request.HasFormContentType) body = string.Join("&", Request.Form.Select(kv => kv.Key + "=" + kv.Value));
                else { using var r = new StreamReader(Request.Body); body = await r.ReadToEndAsync(); }
            }
            catch { }
            Console.WriteLine($"[progression-events] CALL {Request.Method} /api/progressionEvents/{rest}{Request.QueryString} by {account.Id} body='{(body.Length > 400 ? body[..400] + "..." : body)}'");

            string all = rest + " " + Request.QueryString + " " + body;
            var guids = GuidRx.Matches(all).Select(m => m.Value).ToList();
            var numbers = System.Text.RegularExpressions.Regex.Matches(rest + " " + Request.QueryString + " " + body, @"\b\d{1,12}\b")
                              .Select(m => long.Parse(m.Value)).ToList();
            var visible = ProgressionEvents.VisibleTo(account.Id);
            var ev = numbers.Select(ProgressionEvents.FindNumeric).FirstOrDefault(e => e != null && visible.Contains(e))
                     ?? guids.Select(ProgressionEvents.Find).FirstOrDefault(e => e != null && visible.Contains(e))
                     ?? visible.FirstOrDefault();
            string low = rest.ToLowerInvariant();

            if (low.Contains("xpboost"))
                return low.Contains("preview") ? Content("0", "application/json") : J(new JArray());
            if (low.StartsWith("room") || low.Contains("/room"))
                return J(new JArray());
            if (ev == null)
            {
                Console.WriteLine($"[progression-events]   -> no running event for {account.Id}; 404");
                return NotFound();
            }
            if (low.Contains("collect") || low.Contains("claim"))
            {
                // A reward named by its numeric id (event*1000 + slot) or its internal guid.
                string? rewardId = null;
                for (int i = 0; i < ev.Steps.Count && rewardId == null; i++)
                {
                    if (ev.Steps[i].Free is { } f && numbers.Contains(ProgressionEvents.RewardNumericId(ev, i, false))) rewardId = f.Id;
                    else if (ev.Steps[i].Premium is { } p && numbers.Contains(ProgressionEvents.RewardNumericId(ev, i, true))) rewardId = p.Id;
                }
                var rewardIds = ev.Steps.SelectMany(s => new[] { s.Free?.Id, s.Premium?.Id }).Where(x => x != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                rewardId ??= guids.FirstOrDefault(g => rewardIds.Contains(g));
                ProgressionEvents.Collect(ev, account.Id, rewardId);
                return J(ProgressionEvents.RecordDto(ev, ProgressionEvents.GetRecord(ev.Id, account.Id)));
            }
            // "event/{id}" (singular) is the event itself; record / events / anything else is the
            // player's record for it. Both are logged above, so a wrong guess names itself.
            if (low.StartsWith("event/") || low.Contains("/event/"))
                return J(ProgressionEvents.EventDto(ev));
            return J(ProgressionEvents.RecordDto(ev, ProgressionEvents.GetRecord(ev.Id, account.Id)));
        }
    }
}
