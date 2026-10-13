using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.Controllers
{
    [ApiController]
    [Route("/")]
    public class AccountController : ControllerBase
    {
        [HttpGet("acc/account/bulk")]
        public IActionResult GetAccountsBulk([FromQuery] List<ulong> id)
        {
            // Was: Ok(<string>). ASP.NET's StringOutputFormatter writes a raw string as
            // Content-Type: text/plain, so these JSON payloads were served mislabelled
            // and the client would not parse them. Verified live: /outfits/me returned
            // text/plain while /api/avatar/v2 returned application/json. Same bytes,
            // correct content type.
            return new ContentResult { Content = PlayerDB.GetAccountsBulk(id, AuthHelper.OptionalViewerId(HttpContext)), ContentType = "application/json", StatusCode = 200 };
        }

        // Self-service username set, used during first-run account creation
        // onboarding. Flips HasChosenUsername so the client's onboarding flow
        // considers this step done.
        [HttpPut("acc/account/me/username")]
        public ContentResult PutMyUsername([FromForm] string username)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Same rules as every other name path (NameRules): shape, length, swear filter,
            // reserved / staff look-alikes, case-insensitive uniqueness. This path used to accept
            // any string at all.
            if (PlayerDB.HasActiveModerationBlock(account.Id))
                return Utils.Error("You cannot change your name at this time.");
            if (PlayerDB.IsNameLocked(account.Id))
                return Utils.Error("Your name has been locked by a moderator and cannot be changed.");
            string? nameErr = NameRules.CheckUsername(username, account.Id, out username);
            if (nameErr != null)
            {
                Console.WriteLine($"[names] {account.Id} first-run username refused: {nameErr}");
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { success = false, error = nameErr, value = (string?)null }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Player not found",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            player.player.Username = username;
            player.player.DisplayName = username;
            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            col.Update(player);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"account-update {account.Id}");

            // Set the account creation flag
            SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasChosenUsername", "True", account.Id);

            // Push a live self-refresh so the new @name/display shows in-game without a relog,
            // exactly like the website edit and the Accounts/account/me/username path do.
            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), "acc username refresh");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Self-service password set for onboarding. Password is hashed
        // before storage — never stored in plaintext.
        [HttpPut("acc/account/me/password")]
        public ContentResult PutMyPassword([FromForm] string password)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Password must be at least 4 characters!",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Player not found",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            player.password = ClientSecurity.HashPassword(password);
            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            col.Update(player);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"account-update {account.Id}");

            // Set the account creation flag
            SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasCreatedPassword", "True", account.Id);

            // Check if username has also been set
            var hasUsername = SettingsDB.GetPlayerSettingValue("Recroom.AccountCreation.HasChosenUsername", account.Id) == "True";
            if (hasUsername)
            {
                SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasFinished", "True", account.Id);
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("acc/account/me/profileimage")]
        public ContentResult PutMyProfileImage([FromForm] string imageName)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (string.IsNullOrWhiteSpace(imageName))
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Image name cannot be empty!",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Player not found",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (PlayerDB.IsProfileLocked(account.Id, PlayerDB.ProfileLock.Picture))
            {
                // Redraw their real picture - the client already shows the new one locally.
                Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"locked picture revert {account.Id}");
                return Utils.Error(PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Picture));
            }
            player.player.profileImage = imageName;
            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            col.Update(player);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"account-update {account.Id}");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("acc/account/me/bannerimage")]
        public ContentResult PutMyBannerImage([FromForm] string imageName)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (string.IsNullOrWhiteSpace(imageName))
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Image name cannot be empty!",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Player not found",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (PlayerDB.IsProfileLocked(account.Id, PlayerDB.ProfileLock.Banner))
            {
                Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"locked banner revert {account.Id}");
                return Utils.Error(PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Banner));
            }
            player.player.bannerImage = imageName;
            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            col.Update(player);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"account-update {account.Id}");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("acc/account/me/bio")]
        public ContentResult PutMyBio([FromForm] string? bio)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            bio = System.Net.WebUtility.UrlDecode(bio ?? "");
            if (bio.Length > 255)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Your bio must not exceed 255 characters!",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Player not found",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (PlayerDB.IsProfileLocked(account.Id, PlayerDB.ProfileLock.Bio))
                return Utils.Error(PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Bio));
            player.player_Extra.Bio = bio;
            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            col.Update(player);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"account-update {account.Id}");

            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), "acc bio refresh");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("acc/account/me/personalpronouns")]
        public ContentResult PutMyPronouns([FromForm] int pronounFlags)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Player not found",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            player.player.pronounFlags = pronounFlags;
            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            col.Update(player);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"account-update {account.Id}");

            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), "acc pronouns refresh");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("acc/account/me/identityflags")]
        public ContentResult PutMyIdentityFlags([FromForm] int identityFlags)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Player not found",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            player.player.identityFlags = identityFlags;
            var col = PlayerDB.PlayerDBFile.GetCollection<player_data>("players");
            col.Update(player);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"account-update {account.Id}");

            Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), "acc identity refresh");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("acc/account/{accountId}/bio")]
        public IActionResult GetAccountBio(ulong accountId)
        {
            var player = PlayerDB.GetFullAccount(accountId);

            return Ok(new
            {
                AccountId = accountId,
                Bio = player?.player_Extra?.Bio ?? ""
            });
        }

        [HttpGet("acc/account/me")]
        public IActionResult GetAccountMe()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var accountData = PlayerDB.GetAccountMeSafe(account.Id);
            return accountData != null ? Ok(accountData) : NotFound();
        }

        [HttpPost("acc/account/me/email")]
        [HttpPost("/Accounts/account/me/email")]   // the 2025 client's path (was a 404)
        [HttpPut("/Accounts/account/me/email")]
        public IActionResult AccAccountMeEmail()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Was a no-op that just returned success, so a linked email was thrown away. Store it
            // on the account (same field the website signup writes and account/me reads back) and
            // push a live self-refresh so it shows in-game immediately, like every other edit.
            string email = (Request.Form["email"].ToString() ?? "").Trim();
            if (email.Length > 0 &&
                (email.Length > 254 ||
                 !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")))
            {
                return Ok(new { success = false, error = "Enter a valid email address." });
            }

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player?.player != null)
            {
                player.player.Email = email.Length == 0 ? null : email;
                PlayerDB.PlayerDBFile.GetCollection<player_data>("players").Update(player);
            }

            Notifications.FireAndForget(Notifications.RefreshAccount(account.Id), "acc email refresh");

            return Ok(new { success = true });
        }

        [HttpPost("acc/account/me/skip-orientation")]
        public ContentResult SkipOrientation()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Mark the whole flow done - every Recroom.AccountCreation.* step + TUTORIAL_COMPLETE_MASK
            // + Recroom.OOBE. Setting only the tutorial mask (what this used to do) left the account
            // with Recroom.AccountCreation.HasFinished = "false", so the client re-ran Orientation
            // the moment the player tried to go to the Rec Center - the "stuck in Orientation" bug.
            SettingsDB.MarkOrientationComplete(account.Id);
            Console.WriteLine($"[orientation] {account.Username} ({account.Id}) finished orientation (self)");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The client derives role-badge checks from the service map's Auth base,
        // so it calls /auth/role/... — keep both paths registered.
        [HttpGet("role/moderator/{playerId}")]
        [HttpGet("/auth/role/moderator/{playerId}")]
        public IActionResult CheckModerator(ulong playerId)
        {
            var player = PlayerDB.GetFullAccount(playerId);
            if (player == null)
                return NotFound();

            var isModerator = PlayerDB.CheckModFlag(playerId);
            return Ok(isModerator.ToString().ToLower());
        }

        [HttpGet("role/developer/{playerId}")]
        [HttpGet("/auth/role/developer/{playerId}")]
        public IActionResult CheckDeveloper(ulong playerId)
        {
            var player = PlayerDB.GetFullAccount(playerId);
            if (player == null)
                return NotFound();

            var isDeveloper = PlayerDB.CheckDevFlag(playerId);
            return Ok(isDeveloper.ToString().ToLower());
        }

        // BULK query form: /Auth/role/developer?id=6&id=10&... — the client fetches this for
        // player lists (dev badges) and it was 404ing (only the /{playerId} path form existed).
        // Returns a JSON array of the requested ids that ARE developers.
        [HttpGet("role/developer")]
        [HttpGet("/auth/role/developer")]
        public IActionResult CheckDevelopersBulk()
        {
            var ids = Request.Query["id"].Select(s => ulong.TryParse(s, out var v) ? v : 0UL).Where(v => v != 0).Distinct();
            var devs = ids.Where(id => PlayerDB.GetFullAccount(id) != null && PlayerDB.CheckDevFlag(id)).ToList();
            return Ok(devs);
        }

        [HttpGet("role/moderator")]
        [HttpGet("/auth/role/moderator")]
        public IActionResult CheckModeratorsBulk()
        {
            var ids = Request.Query["id"].Select(s => ulong.TryParse(s, out var v) ? v : 0UL).Where(v => v != 0).Distinct();
            var mods = ids.Where(id => PlayerDB.GetFullAccount(id) != null && PlayerDB.CheckModFlag(id)).ToList();
            return Ok(mods);
        }

        // Desktop screen/audio sharing permission. The client asks /Auth/role/developer?id=X first
        // and, when that says no, /Auth/role/screenshare?id=X - both for the local player pressing
        // "claim" and for every viewer accepting a remote broadcaster. This route was missing (404 =
        // "no permission"), so only developers could ever share. Same rule as the token's
        // "screenshare" role (AuthController.RolesForAccount): screenshare flag, dev or owner.
        private static bool HasScreenshare(ulong id)
        {
            var f = PlayerDB.GetFullAccount(id)?.dev_Flag ?? DevFlag.none;
            return (f & (DevFlag.screenshare | DevFlag.dev | DevFlag.owner)) != DevFlag.none;
        }

        [HttpGet("role/screenshare/{playerId}")]
        [HttpGet("/auth/role/screenshare/{playerId}")]
        public IActionResult CheckScreenshare(ulong playerId)
        {
            if (PlayerDB.GetFullAccount(playerId) == null) return NotFound();
            return Ok(HasScreenshare(playerId).ToString().ToLower());
        }

        [HttpGet("role/screenshare")]
        [HttpGet("/auth/role/screenshare")]
        public IActionResult CheckScreenshareBulk()
        {
            var ids = Request.Query["id"].Select(s => ulong.TryParse(s, out var v) ? v : 0UL).Where(v => v != 0).Distinct();
            return Ok(ids.Where(HasScreenshare).ToList());
        }
    }
}
