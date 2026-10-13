using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    // Photon Cloud "Custom Authentication" webhook.
    //
    // In the Photon dashboard for each app (Realtime / Voice / Chat), set:
    //   Authentication -> Custom Server -> Custom -> AuthUrl:
    //     https://camprec.net/photon/auth
    //
    // Photon will call this endpoint on every client connect, passing whatever token the client
    // presented in AuthValues (the client hands out photon_access_token via RoomController's
    // /Room_server/photon_access_token endpoint - see Createphotonacesstoken in ClientSecurity).
    // We validate it as our own JWT, and echo back the real account id as the Photon UserId -
    // which the client CANNOT forge at the Photon protocol level (matches the pattern the 2023
    // Campus-Server used, see PhotonAuthController.cs there).
    //
    // Contract: reply 200 with JSON { ResultCode, UserId, Nickname }.
    //   ResultCode 1 = authenticated, 2 = failed / unauthorized, 3 = required params missing.
    // If custom auth isn't configured on the Photon apps this endpoint just never gets called -
    // it costs nothing to ship, and makes the server ready the moment custom auth is turned on.
    [ApiController]
    public class PhotonAuthController : ControllerBase
    {
        [HttpGet("/photon/auth")]
        [HttpPost("/photon/auth")]
        public async Task<IActionResult> Authenticate([FromQuery] string? token)
        {
            // The token can arrive as a query param (AuthGetParameters), form/body (AuthPostData),
            // or as the raw body - accept all shapes so either GET or POST configuration works.
            string? t = token;
            if (string.IsNullOrEmpty(t))
                foreach (var k in new[] { "token", "access_token", "t", "sig", "authToken" })
                    if (Request.Query.TryGetValue(k, out var q) && !string.IsNullOrEmpty(q)) { t = q; break; }

            if (string.IsNullOrEmpty(t) && Request.HasFormContentType)
                foreach (var k in new[] { "token", "access_token", "t", "authToken" })
                    if (Request.Form.TryGetValue(k, out var f) && !string.IsNullOrEmpty(f)) { t = f; break; }

            if (string.IsNullOrEmpty(t))
            {
                try
                {
                    using var r = new StreamReader(Request.Body);
                    var body = (await r.ReadToEndAsync()).Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(body) && body.Count(c => c == '.') == 2) t = body;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(t))
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} [photon-auth] no token in request ({Request.QueryString}) -> ResultCode 3");
                return Ok(new { ResultCode = 3, Message = "missing token" });
            }

            string decoded = ClientSecurity.DecodeToken(t);
            if (string.IsNullOrEmpty(decoded) || decoded.StartsWith("error_"))
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} [photon-auth] token failed validation ({decoded}) -> ResultCode 2");
                return Ok(new { ResultCode = 2, Message = "invalid token" });
            }

            player_info? account = null;
            try { account = JsonConvert.DeserializeObject<player_info>(decoded); } catch { }

            if (account == null)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} [photon-auth] token decoded but no player_info -> ResultCode 2");
                return Ok(new { ResultCode = 2, Message = "no account" });
            }

            string nickname = account.Username ?? account.Id.ToString();
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} [photon-auth] authenticated @{nickname} as UserId {account.Id}");

            return Ok(new
            {
                ResultCode = 1,
                UserId = account.Id.ToString(),
                Nickname = nickname
            });
        }
    }
}
