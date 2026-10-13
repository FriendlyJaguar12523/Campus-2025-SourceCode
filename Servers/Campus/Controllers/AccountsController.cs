using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
using static Campus_live_rewrite.Controllers.AccountsController;

namespace Campus_live_rewrite.Controllers
{
    [Route("Accounts")]
    public class AccountsController : ControllerBase
    {
        // GET ?id=1&id=2, and POST with the same ids as a FORM body - the client switches to
        // POST for long lists (its whole friends list, a page of item creators). POST was a 405,
        // so every account in such a batch never resolved: names stuck on "Loading..." (a game
        // invite's card among them). Reference accounts.app.ts: `id` repeats, and each value may
        // itself be a comma-separated list.
        // The caller (0 when anonymous), so a viewer's friend nicknames can replace display names in their own responses. The
        // answer then differs per viewer, so it must never be cached or shared.
        private ulong ViewerAndNoStore()
        {
            ulong viewer = AuthHelper.OptionalViewerId(HttpContext);
            if (viewer != 0) Response.Headers["Cache-Control"] = "private, no-store";
            return viewer;
        }

        [HttpGet("account/bulk")]
        [HttpPost("account/bulk")]
        public ContentResult AccountBulk()
        {
            var raw = new List<string>();
            raw.AddRange(Request.Query["id"].Select(v => v ?? ""));
            try { if (Request.HasFormContentType) raw.AddRange(Request.Form["id"].Select(v => v ?? "")); } catch { }
            var id = raw.SelectMany(v => v.Split(','))
                        .Select(v => ulong.TryParse(v.Trim(), out var n) ? n : 0)
                        .Where(n => n > 0).Distinct().ToList();
            return new ContentResult()
            {
                Content = PlayerDB.GetAccountsBulk(id, ViewerAndNoStore()),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("account/me")]
        public IActionResult AccountMe()
        {
            string authtoken = HttpContext.Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(authtoken))
            {
                authtoken = HttpContext.Request.Cookies["Authorization"];
            }

            if (string.IsNullOrEmpty(authtoken))
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 401
                };
            }
            string playerdata = ClientSecurity.DecodeToken(authtoken);
            if (playerdata == null)
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 401
                };
            if (playerdata.Contains("error_token"))
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 401
                };
            player_info? account = JsonConvert.DeserializeObject<player_info>(playerdata);
            var me = PlayerDB.GetAccountMeSafe(account.Id);
            // Return the account's REAL email (GetAccountMeSafe already fills it, "" when unset).
            // This used to be hardcoded to "example@example.com", which hid the linked email and
            // overwrote what the player entered.
            return Ok(me);
        }

        // The client GETs /Accounts/account/{id} whenever a profile is opened. Only
        // account/bulk existed, so this 404'd and the profile panel rendered blank.
        // Reuse GetAccountsBulk so the object is byte-for-byte the shape the client
        // already accepts from bulk - it returns a JSON ARRAY, so unwrap the one entry.
        //
        // The ":long" constraint keeps this from shadowing account/me, account/bulk and
        // account/search (literal segments win over parameters, but be explicit).
        [HttpGet("account/{id:long}")]
        public ContentResult AccountById(long id)
        {
            if (id < 0)
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };

            string json = PlayerDB.GetAccountsBulk(new List<ulong> { (ulong)id }, ViewerAndNoStore());

            try
            {
                var arr = Newtonsoft.Json.Linq.JArray.Parse(json);
                if (arr.Count == 0)
                    return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };

                return new ContentResult
                {
                    Content = arr[0].ToString(Newtonsoft.Json.Formatting.None),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch
            {
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
            }
        }

        // Prefix/contains search on username + display name. Returns the SAME account DTO shape as
        // /account/bulk and /account/{id} (account_data_web) - not the raw internal account_data,
        // which can emit null username/displayName/profileImage and lacks bannerImage/displayEmoji,
        // so the client's strict parser rejected it and the People search came back EMPTY even when
        // a match existed (e.g. "coach"). Route the matches through GetAccountsBulk so the shape is
        // byte-identical to what the client already accepts from bulk.
        [HttpGet("account/search")]
        public IActionResult AccountSearch(string name)
        {
            var matches = PlayerDB.Search(name);
            var ids = matches.Select(a => a.accountId).Distinct().ToList();
            if (ids.Count == 0)
                return new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };

            return new ContentResult
            {
                Content = PlayerDB.GetAccountsBulk(ids, ViewerAndNoStore()),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("account/{id}/bio")]
        public IActionResult GetBioForId(ulong id)
        {
            player_data? FullAccount = PlayerDB.GetFullAccount(id);
            string bio = FullAccount?.player_Extra?.Bio ?? "";

            return Ok(new
            {
                accountId = id,
                bio = bio
            });
        }

        [HttpGet("accountprivacysettings/{id}")]
        public ContentResult accountprivacysettings_for_userid(ulong id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            player_data? FullAccount = PlayerDB.GetFullAccount(id);
            bool IsRecentRoomHistoryVisible = FullAccount?.player_Extra?.IsRecentRoomHistoryVisible ?? false;

            var AccountPrivacySettings = JsonConvert.SerializeObject(new
            {
                accountId = id,
                isRecentHistoryVisible = IsRecentRoomHistoryVisible
            }); // Btw, the room history API can return [] if IsRecentRoomHistoryVisible is false like the Rec Room API does. - e12354

            return new ContentResult()
            {
                Content = AccountPrivacySettings,
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("parentalcontrol/me")]
        public ContentResult ParentalControlMe()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    accountId = account.Id,
                    disallowInAppPurchases = false,
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("account/me/username")]
        public async Task<ContentResult> ChangeUsername([FromForm] string username)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (PlayerDB.HasActiveModerationBlock(account.Id))
            {
                return Utils.Error("You cannot change your name at this time.");
            }
            if (PlayerDB.IsNameLocked(account.Id))
            {
                Console.WriteLine($"[names] {account.Id} name change refused: name lock");
                return Utils.Error("Your name has been locked by a moderator and cannot be changed.");
            }
            string? nameErr = NameRules.CheckUsername(username, account.Id, out username);
            if (nameErr != null)
            {
                Console.WriteLine($"[names] {account.Id} @name change refused: {nameErr}");
                return Utils.Error(nameErr);
            }

            bool usercheck = await Task.Run(() => PlayerDB.SetPlayerUsername(username, account.Id));
            if (!usercheck)
            {
                return Utils.Error("This username is already in use!");
            }

            await Task.Run(() => PlayerDB.SetDisplayName(username, account.Id));
            try
            {
                // Notify ONLY the owner. This used to also SendToAll an "AccountUpdate"
                // carrying this player's own account_data_me, which every other client
                // applied over its own state - error-kicking the whole server to their
                // dorms (and letting banned players rejoin). See Notifications.RefreshAccount.
                // PushAccountUpdate (AccountUpdate), NOT RefreshAccount (SelfAccountUpdate): the
                // self-update does NOT redraw the profile/name, which is why an in-game edit used
                // to need a relog. AccountUpdate is the redraw the WEBSITE edit sends, so this now
                // updates the @name/display name in real time in-game, exactly like the website.
                await Notifications.PushAccountUpdate(account.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send notification: {ex.Message}");
            }
            _ = Task.Run(() =>
            {
                try
                {
                    Webhook.Send(
                        account.Id,
                        "prod",
                        "Server info",
                        $"({account.Id}) @{account.Username} has changed their @ name to @{username}",
                        5898927
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Webhook] Failed to send username change webhook: {ex.Message}");
                }
            });
            return new ContentResult()
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

        [HttpPut("account/me/bio")]
        public async Task<ContentResult> ChangeBio([FromForm] string bio)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (bio.Length > 255)
            {
                return Utils.Error("Your bio must not exceed 255 characters!");
            }
            bool CheckSwears = !string.IsNullOrEmpty(bio) && Sanitize.ContainsSwears(bio);
            if (CheckSwears)
            {
                return Utils.Error("Your bio contains swear words!");
            }

            if (PlayerDB.IsProfileLocked(account.Id, PlayerDB.ProfileLock.Bio))
                return Utils.Error(PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Bio));

            string shortBio = bio.Length > 60 ? bio.Substring(0, 60) + "..." : bio;

            await Task.Run(() => PlayerDB.SetPlayerBio(bio, account.Id));
            _ = Task.Run(() =>
            {
                try
                {
                    Webhook.Send(
                        account.Id,
                        "prod",
                        "Server info",
                        $"({account.Id}) @{account.Username} has changed their bio! " + $"Preview: \"{shortBio}\"",
                        5898927
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Webhook] Failed to send bio change webhook: {ex.Message}");
                }
            });
            try
            {
                // Notify ONLY the owner. This used to also SendToAll an "AccountUpdate"
                // carrying this player's own account_data_me, which every other client
                // applied over its own state - error-kicking the whole server to their
                // dorms (and letting banned players rejoin). See Notifications.RefreshAccount.
                // PushAccountUpdate (AccountUpdate), NOT RefreshAccount (SelfAccountUpdate): the
                // self-update does NOT redraw the profile/name, which is why an in-game edit used
                // to need a relog. AccountUpdate is the redraw the WEBSITE edit sends, so this now
                // updates the @name/display name in real time in-game, exactly like the website.
                await Notifications.PushAccountUpdate(account.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Notifications] Failed to send bio update: {ex.Message}");
            }
            return new ContentResult()
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

        [HttpPut("account/me/displayname")]
        public async Task<ContentResult> ChangeDisplayName()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string NewDisplayName = HttpContext.Request.Form["displayName"].ToString();
            if (PlayerDB.HasActiveModerationBlock(account.Id))
            {
                return Utils.Error("You cannot change your name at this time.");
            }
            if (PlayerDB.IsNameLocked(account.Id))
            {
                Console.WriteLine($"[names] {account.Id} name change refused: name lock");
                return Utils.Error("Your name has been locked by a moderator and cannot be changed.");
            }
            string? dnErr = NameRules.CheckDisplayName(NewDisplayName, account.Id, out NewDisplayName);
            if (dnErr != null)
            {
                Console.WriteLine($"[names] {account.Id} display name refused: {dnErr}");
                return Utils.Error(dnErr);
            }

            await Task.Run(() => PlayerDB.SetDisplayName(NewDisplayName, account.Id));
            try
            {
                // Notify ONLY the owner. This used to also SendToAll an "AccountUpdate"
                // carrying this player's own account_data_me, which every other client
                // applied over its own state - error-kicking the whole server to their
                // dorms (and letting banned players rejoin). See Notifications.RefreshAccount.
                // PushAccountUpdate (AccountUpdate), NOT RefreshAccount (SelfAccountUpdate): the
                // self-update does NOT redraw the profile/name, which is why an in-game edit used
                // to need a relog. AccountUpdate is the redraw the WEBSITE edit sends, so this now
                // updates the @name/display name in real time in-game, exactly like the website.
                await Notifications.PushAccountUpdate(account.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send notification: {ex.Message}");
            }
            _ = Task.Run(() =>
            {
                try
                {
                    Webhook.Send(
                        account.Id,
                        "prod",
                        "Server info",
                        $"({account.Id}) @{account.Username} has changed their Display Name to \"{NewDisplayName}\"",
                        5898927
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Webhook] Failed to send Display Name change webhook: {ex.Message}");
                }
            });
            return new ContentResult()
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

        [HttpGet("emojiConfig/whitelistedEmojis")]
        public ContentResult GetWhitelistedEmojis()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(Emojis.emojis),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("account/me/emoji")]
        public async Task<ContentResult> ChangeEmoji()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string displayEmoji = HttpContext.Request.Form["displayEmoji"].ToString();
            displayEmoji = Uri.UnescapeDataString(displayEmoji ?? "");
            var whitelistedEmojis = Emojis.emojis;
            if (!whitelistedEmojis.Contains(displayEmoji))
            {
                return Utils.Error("Emoji not allowed!");
            }
            await Task.Run(() => PlayerDB.SetPlayerEmoji(displayEmoji, account.Id));
            try
            {
                // Notify ONLY the owner. This used to also SendToAll an "AccountUpdate"
                // carrying this player's own account_data_me, which every other client
                // applied over its own state - error-kicking the whole server to their
                // dorms (and letting banned players rejoin). See Notifications.RefreshAccount.
                // PushAccountUpdate (AccountUpdate), NOT RefreshAccount (SelfAccountUpdate): the
                // self-update does NOT redraw the profile/name, which is why an in-game edit used
                // to need a relog. AccountUpdate is the redraw the WEBSITE edit sends, so this now
                // updates the @name/display name in real time in-game, exactly like the website.
                await Notifications.PushAccountUpdate(account.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send notification: {ex.Message}");
            }
            _ = Task.Run(() =>
            {
                try
                {
                    Webhook.Send(
                        account.Id,
                        "prod",
                        "Server info",
                        $"({account.Id}) @{account.Username} has changed their Display Emoji to \"{displayEmoji}\"",
                        5898927
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Webhook] Failed to send Display Emoji change webhook: {ex.Message}");
                }
            });
            return new ContentResult()
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

        [HttpPut("account/me/personalpronouns")]
        public async Task<ContentResult> ChangePersonalPronouns([FromForm] int pronounFlags)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            await Task.Run(() => PlayerDB.SetPlayerPronounFlags(pronounFlags, account.Id));

            try
            {
                // Notify ONLY the owner. This used to also SendToAll an "AccountUpdate"
                // carrying this player's own account_data_me, which every other client
                // applied over its own state - error-kicking the whole server to their
                // dorms (and letting banned players rejoin). See Notifications.RefreshAccount.
                // PushAccountUpdate (AccountUpdate), NOT RefreshAccount (SelfAccountUpdate): the
                // self-update does NOT redraw the profile/name, which is why an in-game edit used
                // to need a relog. AccountUpdate is the redraw the WEBSITE edit sends, so this now
                // updates the @name/display name in real time in-game, exactly like the website.
                await Notifications.PushAccountUpdate(account.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send notification: {ex.Message}");
            }

            return new ContentResult()
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

        [HttpPut("account/me/identityflags")]
        public async Task<ContentResult> ChangeIdentityFlags([FromForm] int identityFlags)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;


            await Task.Run(() => PlayerDB.SetPlayerIdentityFlags(identityFlags, account.Id));
            // Real-time redraw (same as the website), not the self-only update that needed a relog.
            await Notifications.PushAccountUpdate(account.Id);
            return new ContentResult()
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

        [HttpPut("account/me/profileimage")]
        public async Task<IActionResult> ChangeProfileImage([FromForm] string imageName)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (string.IsNullOrEmpty(imageName))
            {
                return Ok(new
                {
                    success = false,
                    error = "Image name cannot be empty!",
                    value = (string?)null
                });
            }

            if (PlayerDB.IsProfileLocked(account.Id, PlayerDB.ProfileLock.Picture))
            {
                Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"locked picture revert {account.Id}");
                return Ok(new { success = false, error = PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Picture), value = (string?)null });
            }

            await Task.Run(() => PlayerDB.SetPlayerImageName(imageName, account.Id));

            // Refresh the client so the new picture shows WITHOUT a relog - but DELAYED.
            //
            // An IMMEDIATE SelfAccountUpdate here caused "Dropping to dormroom due to error
            // [385120]": the client's image loader keys in-flight loads by image id and completes
            // one TaskCompletionSource per id. Answering this request already starts a load of the
            // new image; a simultaneous push starts a SECOND load of the same id and double-
            // completes that promise (InvalidOperationException: Cannot complete an already
            // completed promise). Leaving it out avoided the crash but meant the picture only
            // appeared after a relog.
            //
            // Delaying the push past that first load removes the race: by the time it fires the
            // load is done (its promise cleared), so the refresh is a clean single reload and the
            // picture updates live. Fire-and-forget so the HTTP 200 returns immediately.
            ulong pid = account.Id;
            Notifications.FireAndForget(Task.Run(async () =>
            {
                await Task.Delay(3000);
                await Notifications.PushAccountUpdate(pid);   // AccountUpdate redraws the profile (owner + everyone)
            }), "profile image live refresh");

            return Ok(new
            {
                success = true,
                error = "",
                value = (string?)null
            });
        }

        [HttpPut("account/me/bannerimage")]
        public async Task<IActionResult> ChangeBannerImage([FromForm] string imageName)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (string.IsNullOrEmpty(imageName))
            {
                return Ok(new
                {
                    success = false,
                    error = "Image name cannot be empty!",
                    value = (string?)null
                });
            }

            if (PlayerDB.IsProfileLocked(account.Id, PlayerDB.ProfileLock.Banner))
            {
                Notifications.FireAndForget(Notifications.PushAccountUpdate(account.Id), $"locked banner revert {account.Id}");
                return Ok(new { success = false, error = PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Banner), value = (string?)null });
            }

            await Task.Run(() => PlayerDB.SetPlayerBanner(imageName, account.Id));

            // Delayed refresh so the new banner shows without a relog. See ChangeProfileImage above
            // for why it must be delayed: an immediate SelfAccountUpdate double-completes the
            // client's in-flight image-load promise and drops it to the dorm ([385120]). Firing it
            // after that load finishes makes it a clean single reload.
            ulong pid = account.Id;
            Notifications.FireAndForget(Task.Run(async () =>
            {
                await Task.Delay(3000);
                await Notifications.PushAccountUpdate(pid);   // AccountUpdate redraws the profile (owner + everyone)
            }), "banner image live refresh");

            return Ok(new
            {
                success = true,
                error = "",
                value = (string?)null
            });
        }

        [HttpPost("account/me/phone")]
        public IActionResult AddPhoneNumber([FromForm] string phone)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string phoneNumber = phone.Substring(2);
            Console.WriteLine($"[Phone] Player {account.Id} is trying to add phone number: {phoneNumber}. Full phone: {phone}");

            return Ok(new
            {
                success = false,
                error = "This ain't implemented yet.",
                value = (string?)null
            });        
        }

        [HttpGet("namegen/options")]
        public IActionResult NameGenOptions()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                nouns = Utils.Nouns,
                adjectives = Utils.Adjectives,
            });       
        }
    }
}
