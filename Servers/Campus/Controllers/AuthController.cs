using System.Text;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
using Newtonsoft.Json.Linq;
using Discord.Net;
using System.Security.Principal;

namespace Campus_live_rewrite.Controllers
{
    public class AuthController : ControllerBase
    {
        // Translate an account's stored DevFlag bitmask into the JWT `role` strings the client
        // uses to decide dev / moderator / screenshare permissions. The DB is updated correctly
        // by the admin panel (via PlayerDB.SetPlayerDev / SetRoleFlag), but the login path
        // below was HARDCODING roles = ["gameclient"] and dropping every flag on the floor - so
        // toggling "dev" in the admin panel changed the DB but the client's next token still
        // said only gameclient, and the player wasn't dev in-game.
        //
        // Every player still gets "gameclient" as the base role; dev/mod/screenshare are added
        // on top when the corresponding flag bit is set. Owner is treated as full dev+mod for
        // parity with how the 2023 server did it.
        private static List<string> RolesForAccount(player_data account)
        {
            var roles = new List<string> { "gameclient" };
            if (account == null) return roles;
            var f = account.dev_Flag;
            bool isDev         = (f & DevFlag.dev)         == DevFlag.dev;
            bool isMod         = (f & DevFlag.mod)         == DevFlag.mod;
            bool isScreenshare = (f & DevFlag.screenshare) == DevFlag.screenshare;
            bool isOwner       = (f & DevFlag.owner)       == DevFlag.owner;
            if (isDev || isOwner)         roles.Add("developer");
            if (isMod || isOwner || isDev) roles.Add("moderator");
            if (isScreenshare || isDev || isOwner) roles.Add("screenshare");
            return roles;
        }

        [HttpGet("/Auth/eac/challenge")]
        public ContentResult EACChallenge()
        {
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject("test"),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // GET or POST: older clients GET this; the 2025 build POSTs it with a deviceId/platformAuth
        // form body that is read by nothing here (reference: recflare auth). Same answer either way.
        // Make the account of a brand-new player BEFORE the identity steps (Rec Room does too): started, unfinished, marked Campus.ShellAccount.
        private AccountLogin? MakeShellAccount(Platforms platformEnum, ulong platform_id, string ip)
        {
            string? dev = null;
            try { if (HttpContext.Request.HasFormContentType) dev = HttpContext.Request.Form["deviceId"].ToString(); } catch { }
            if (string.IsNullOrWhiteSpace(dev)) dev = "local-" + platform_id;
            if (!PlayerDB.CreateAccount(platformEnum, platform_id, dev, 0, out account_data shell) || shell == null) return null;
            PlayerDB.RecordSignup(ip, shell.accountId);
            PlayerDB.BindDevice(dev, platformEnum, shell.accountId);
            Campus_live_rewrite.api.server.NewPlayerBootstrap.Start(shell.accountId, platformEnum, true);
            SettingsDB.SetPlayerSetting("Campus.ShellAccount", "1", shell.accountId);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(shell.accountId), "new-account push");
            PlayerDB.GetLogins(platformEnum, platform_id, out List<AccountLogin>? all);
            return (all ?? new List<AccountLogin>()).FirstOrDefault(a => a.accountId == shell.accountId);
        }

        // The creation test switch is ONE-SHOT: once the picker has handed out (or made) the unfinished account, the switch file is renamed
        // to .off so the next picker request lists the player's real accounts - with their name and picture and a plain PLAY once finished.
        private static void ConsumeTestSwitch()
        {
            try
            {
                string f = System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "admin_test_newaccount.txt");
                if (System.IO.File.Exists(f)) { System.IO.File.Move(f, f + ".off", true); Console.WriteLine("[cachedlogin] TEST switch consumed (renamed to .off) - the next picker lists the real accounts"); }
            }
            catch (Exception ex) { Console.WriteLine($"[cachedlogin] could not consume the TEST switch: {ex.Message}"); }
        }

        [HttpGet("/Auth/cachedlogin/forplatformid/{platform}/{platform_id}")]
        [HttpPost("/Auth/cachedlogin/forplatformid/{platform}/{platform_id}")]
        public ContentResult CachedLogins(int platform, ulong platform_id)
        {
            Platforms platformEnum;

            if (Enum.IsDefined(typeof(Platforms), platform))
            {
                platformEnum = (Platforms)platform;
            }
            else
            {
                platformEnum = Platforms.All;
            }
            List<AccountLogin>? accounts = null;
            PlayerDB.GetLogins(platformEnum, platform_id, out accounts);
            accounts ??= new List<AccountLogin>();
            // @Importer is the room-import system account (seeded at startup onto the owner's Steam id). It is never somebody's account to play, and offering it
            // made a Steam id with "no accounts" look like it had one - so a new player never got the new-player path.
            try { ulong impId = PlayerDB.GetAccountIdByUsernameCI("Importer"); if (impId != 0) accounts.RemoveAll(a => a.accountId == impId); } catch { }

            // Local-dev experiment: Data/admin_interrupt_account.txt holds an account id; each picker request puts that
            // account into the "account creation STARTED but not finished" state, which the client's cached-account route
            // (GoToCachedAccountStartAccountCreationFlow / InterruptedAccountCreation page) should answer with the
            // customize / username / password pages. Delete the file to stop.
            if (ConfigCampus.LocalDev)
            {
                try
                {
                    string intFile = System.IO.Path.Combine(Environment.CurrentDirectory, "Data", "admin_interrupt_account.txt");
                    if (System.IO.File.Exists(intFile) && ulong.TryParse(System.IO.File.ReadAllText(intFile).Trim(), out ulong intId) && intId != 0)
                    {
                        SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasStarted", "true", intId);
                        foreach (var k in new[] { "HasFinished", "HasChosenUsername", "HasCreatedPassword", "AcceptedCodeOfConduct", "FinishedAvatarCustomization" })
                            SettingsDB.SetPlayerSetting("Recroom.AccountCreation." + k, "false", intId);
                        Console.WriteLine($"[cachedlogin] interrupt experiment: account {intId} set to started / not finished");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[cachedlogin] interrupt experiment failed: {ex.Message}"); }
            }

            // Admin test switch: an allowlisted IP is shown an EMPTY picker so its client walks the
            // new-player path (create_account -> Orientation) even though this Steam / device
            // already has accounts. See ConfigCampus.AdminTestNewAccountFlow.
            string pickerIp = Campus_live_rewrite.api.server.LoginGuard.ClientIp(HttpContext);
            if (ConfigCampus.AdminTestNewAccountFlow && AdminProtectionMiddleware.IsAllowlistedIp(pickerIp))
            {
                Console.WriteLine($"[cachedlogin] {platformEnum} {platform_id} from admin IP {pickerIp}: TEST switch on - empty picker, client will create_account");
                ConsumeTestSwitch();   // one-shot: the next picker request lists the real accounts again
                ConsumeTestSwitch();   // one-shot: the next picker request lists the real accounts again
                return new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };
            }

            // The Quest no-Meta build sends the sentinel id 1 for EVERY headset (see
            // PlatformIdentity), so anything it matched belongs to some OTHER player - handing it
            // back would cached-log this headset into their account with no password. Drop the
            // matches and fall through to the password-login entry below. Nothing should ever be
            // bound to the sentinel in the first place (AccountLock refuses to record it); this is
            // the belt-and-braces half, and it also cleans up after any binding made before that.
            if (PlatformIdentity.IsSentinel(platformEnum, platform_id) && accounts.Count > 0)
            {
                Console.WriteLine($"[cachedlogin] Quest sentinel id {platform_id} matched {accounts.Count} "
                                + $"account(s) - ignoring them, sending to password login screen");
                accounts.Clear();
            }

            // Quest "remember me": with no verifiable platform identity, offer the accounts this
            // HEADSET (its deviceId, sent in the form body) has already signed in to with a password.
            // See ConfigCampus.QuestDeviceCachedLogin; the cached_login grant re-checks the binding.
            if (ConfigCampus.QuestDeviceCachedLogin && accounts.Count == 0 &&
                PlatformIdentity.IsSentinel(platformEnum, platform_id))
            {
                string? devId = null;
                try { if (HttpContext.Request.HasFormContentType) devId = HttpContext.Request.Form["deviceId"]; } catch { }
                var remembered = PlayerDB.GetDeviceLogins(devId, Platforms.Oculus, platform_id);
                if (remembered.Count > 0)
                {
                    Console.WriteLine($"[cachedlogin] Quest device '{devId}' remembered {remembered.Count} account(s) - offering cached login");
                    accounts = remembered;
                }
            }

            // A Steam id should map to ONE account. If a stray duplicate exists (two website accounts
            // both recorded the same Steam id, since the account-lock is per-DEVICE not per-Steam), the
            // client's cached-login picker could land on the WRONG one - that's the "logs into a random
            // account" bug. Fix: for a NORMAL player, offer only the most-recently-used account for this
            // Steam id. Owner/test accounts (Coach/Kyle/@Importer) intentionally share one Steam, so if
            // any exempt account is present we keep the full list (the deliberate account picker).
            // A Steam id may now carry the game-made + website pair (ConfigCampus.MaxAccountsPerDevice),
            // and the picker shows both so the player chooses; only a stray surplus beyond that is
            // trimmed to the most-recent ones.
            int pickerCap = Math.Max(1, ConfigCampus.MaxAccountsPerDevice);
            if (accounts.Count > pickerCap && platform_id != 0)
            {
                bool anyExempt = accounts.Any(a => AccountLock.IsExempt(a.accountId));
                if (!anyExempt)
                {
                    accounts = accounts.OrderByDescending(a => a.lastLoginTime).Take(pickerCap).ToList();
                    Console.WriteLine($"[cachedlogin] Steam {platform_id} had more than {pickerCap} accounts; offering the most-recent {pickerCap} (ids {string.Join(",", accounts.Select(a => a.accountId))}) to avoid a random login");
                }
            }

            if (accounts != null && accounts.Count > 0)
            {
                // A linked account: platform ownership is the credential, so cached-log-in with no
                // prompt (requirePassword defaults false).
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(accounts),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            // No account linked to this platform id yet. Hand back one FAKE cached-login entry with
            // requirePassword=true. That sends the client to the USERNAME/PASSWORD login screen -
            // where the player types the credentials they made on the website - instead of an empty
            // picker, which falls through to create_account (refused) and shows an error. Mirrors
            // the reference's fake cached-login trick.
            //
            // STEAM is gated on the allowlist (or the allowlist being off); non-allowlisted ids get
            // [] and are refused at the token endpoint anyway.
            //
            // QUEST/OCULUS is NOT gated - a headset player needs no platform verification to reach
            // the login screen. That matches how the rest of the server already treats Oculus
            // (SteamAllowlist.ShouldCheck is Steam-only, so the token endpoint never gates a
            // headset either). It grants nothing on its own: the entry carries accountId 0 and
            // requirePassword=true, so the player still has to type website credentials, and the
            // ban gates + LoginGuard lockout on /Auth/connect/token all still apply.
            bool steamId = (platformEnum == Platforms.Steam || platformEnum == Platforms.All) && platform_id != 0;
            bool questId = platformEnum == Platforms.Oculus;

            // In-game account creation ON: a platform id with no account gets an EMPTY picker, which
            // is what makes the client run its new-player flow (create_account -> Orientation:
            // choose a name, make a password). A returning website player still logs in through
            // the picker's + / "log in with existing account" (grant_type=password). The fake
            // "Sign in" tile below is the website-only mode.
            if (ConfigCampus.InGameAccountCreation && (questId || steamId))
            {
                Console.WriteLine($"[cachedlogin] {platformEnum} {platform_id}: no account - empty picker (in-game creation on)");
                return new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };
            }
            if (questId && !ConfigCampus.QuestLoginPlaceholder)
            {
                // No fake tile (see ConfigCampus.QuestLoginPlaceholder): an empty picker, and the
                // player adds their account with + . Everything a returning headset needs is above
                // (the device-remembered login), so this is only ever the very first launch.
                Console.WriteLine($"[cachedlogin] Oculus {platform_id}: no remembered account - empty picker (player logs in via +)");
                return new ContentResult()
                {
                    Content = "[]",
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            if (questId || (steamId && SteamGate.Allowed(platform_id)))
            {
                Platforms fakePlatform = questId ? Platforms.Oculus : Platforms.Steam;
                var fake = new List<AccountLogin>
                {
                    new AccountLogin
                    {
                        createdAt       = DateTime.UtcNow,
                        lastLoginTime   = DateTime.UtcNow,
                        accountId       = PlatformIdentity.LoginPlaceholderAccountId,
                        platform        = fakePlatform,
                        platformId      = platform_id.ToString(),
                        requirePassword = true
                    }
                };
                Console.WriteLine($"[cachedlogin] {fakePlatform} {platform_id} allowed, no account - sending to password login screen");
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(fake),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            Webhook.Send(1,
                "mod",
                "Reporing Info - No Cached Logins",
                $"A cached login was attempted for platform {platformEnum} with platform ID {platform_id}, but no matching accounts were found.",
                5898927
            );
            return new ContentResult()
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        private enum OculusProof { NotOculus, Verified, Sentinel, Unconfigured, Rejected }

        // What a login's platform_auth proves for a Meta/Oculus client (reference: recflare auth
        // verifyPlatformProof). The sentinel id the sideload build sends is refused before anything
        // is asked of Meta, so nothing downstream can treat it as an identity. Steam is left on the
        // server's existing (currently disabled) ticket path and is not touched here.
        private static async Task<(OculusProof status, ulong verifiedId, string reason)> VerifyOculusProof(
            Platforms platform, ulong platformId, string? platformAuth)
        {
            if (platform != Platforms.Oculus) return (OculusProof.NotOculus, 0, "");
            if (PlatformIdentity.IsSentinel(platform, platformId))
                return (OculusProof.Sentinel, 0, "sideload sentinel id is never an identity");
            string secret = ConfigCampus.MetaAppSecret;
            if (string.IsNullOrEmpty(secret)) return (OculusProof.Unconfigured, 0, "META_APP_SECRET not set");
            var r = await MetaNonce.VerifyAsync(platformAuth, platformId, secret);
            return r.Ok ? (OculusProof.Verified, r.UserId, "") : (OculusProof.Rejected, 0, r.Reason);
        }

        private static ContentResult GrantError(int status, string error, string description) => new()
        {
            Content = JsonConvert.SerializeObject(new { error, error_description = description }),
            ContentType = "application/json",
            StatusCode = status
        };

        [HttpPost("/Auth/connect/token")]
        public async Task<ContentResult> ConnectToken(int platform, ulong platform_id)
        {
            // The token endpoint is the GAME CLIENT only (BestHTTP). Reject anything else up
            // front - BEFORE any body parsing - so a REST tool / HTTP debugger cannot reach the
            // grant handlers to farm bearer tokens. This check used to sit AFTER an
            // int.Parse(Form["platform"]) that threw on a minimal body and was swallowed by a
            // catch, so a hand-crafted request skipped straight past it - which is exactly how a
            // token got minted from a REST client. NB: User-Agent is client-set and spoofable, so
            // this is defence-in-depth ON TOP OF credentials + the per-IP auth rate limit
            // (TrafficGuardMiddleware), not a standalone gate.
            string reqUa = HttpContext.Request.Headers["User-Agent"].ToString();
            if (ConfigCampus.RequireGameUserAgent && reqUa != "BestHTTP")
            {
                Console.WriteLine($"[connect/token] refused non-game User-Agent '{reqUa}'");
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        error = "invalid_client",
                        error_description = "This endpoint is for the Campus game client only."
                    }),
                    ContentType = "application/json",
                    StatusCode = 403
                };
            }

            // NOTE: the raw request body is NOT read/logged here any more. It used to be buffered
            // in full and Console.WriteLine'd every call, which (a) printed every login's PLAINTEXT
            // password into the journal and (b) pulled an unbounded body into memory. Form parsing
            // below reads the body on its own; Kestrel now caps the body size globally.
            string token = null;
            string? temp2 = HttpContext.Request.Headers["Authorization"];
            string? Ip = HttpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            Guid myuuid = Guid.NewGuid();
            string guid = myuuid.ToString();
            string? grant_type = "";

            try
            {
                grant_type = HttpContext.Request.Form["grant_type"];
            }
            catch
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            ulong accid = 0;
            string? device_id = "";
            string? username = "";
            string? password = "";
            string? platform_auth = HttpContext.Request.Form["platform_auth"];
            string? user_agent = "";
            string? ver = "";
            Platforms platformparsed = Platforms.All;
            bool steamVerified = false;          // platform_id is a SteamID Steam itself signed (SteamTicket)
            string steamWhy = "";

            try
            {
                device_id = HttpContext.Request.Form["device_id"];
                user_agent = HttpContext.Request.Headers["User-Agent"];
                // TryParse, not Parse: a missing / junk platform field used to THROW here, and the
                // catch below swallowed it - skipping the device ban, IP ban and allowlist gates that
                // follow. Leaving one field out walked a banned machine straight past them.
                if (int.TryParse(HttpContext.Request.Form["platform"], out int pf)) platform = pf;
                if (ulong.TryParse(HttpContext.Request.Form["platform_id"], out ulong pid)) platform_id = pid;
                ver = HttpContext.Request.Form["ver"];
                platform_auth = HttpContext.Request.Form["platform_auth"];
                platformparsed = Enum.IsDefined(typeof(Platforms), platform) ? (Platforms)platform : Platforms.All;

                // ---- Steam: the SteamID Steam signed, never the posted one ------------------
                // platform_id is whatever the client typed, and a SteamID64 is public. Only the
                // id inside a Steam-signed ticket is trusted (SteamTicket - offline, no Web API
                // key); unverified, a Steam login has NO platform identity: no cached login, no
                // link, no allowlist pass, no in-game account. A password login still works.
                // Reference: recflare auth verifyPlatformProof / steam-ticket.ts.
                if (platformparsed == Platforms.Steam)
                {
                    var proof = SteamTicket.Verify(platform_auth);
                    if (proof.Ok)
                    {
                        if (platform_id != 0 && platform_id != proof.SteamId)
                            Console.WriteLine($"[steam] {grant_type}: posted platform_id {platform_id} is not the ticket's {proof.SteamId} - using the ticket's");
                        platform_id = proof.SteamId;
                        steamVerified = true;
                    }
                    else
                    {
                        steamWhy = proof.Reason;
                        Console.WriteLine($"[steam] {grant_type}: Steam identity NOT verified (posted id {platform_id}): {proof.Reason}");
                        platform_id = 0;
                    }
                }

                // ---- device / IP ban gate ----------------------------------------
                // Checked HERE, before any account is looked up or created, so a banned
                // machine cannot simply register a fresh account and walk back in. An
                // account ban alone never achieved that.
                var deviceBan = Campus_live_rewrite.api.dbs.BanDB.GetDeviceBan(device_id);
                if (deviceBan != null)
                {
                    Console.WriteLine($"[ban] refused login: device '{device_id}' is banned " +
                                      $"(from account {deviceBan.PlayerId}, reason: {deviceBan.Reason})");
                    return BannedLoginResponse(deviceBan.Reason, "device");
                }

                var ipBan = Campus_live_rewrite.api.dbs.BanDB.GetIpBan(Ip);
                if (ipBan != null)
                {
                    Console.WriteLine($"[ban] refused login: IP is banned " +
                                      $"(from account {ipBan.PlayerId}, reason: {ipBan.Reason})");
                    return BannedLoginResponse(ipBan.Reason, "ip");
                }

                // ---- Login patch gate (PatchProofs) -------------------------------
                // A PC login needs a fresh proof from the CURRENT PC patch, from this IP or this
                // (Steam-verified) account. Quest/Meta (Oculus) logins are never checked, and neither
                // is refresh_token - only signing in, so nobody is cut off mid-session.
                if (platformparsed != Platforms.Oculus && grant_type != "refresh_token")
                {
                    string proofIp = PatchGateMiddleware.ClientIp(HttpContext);
                    ulong proofSteam = steamVerified ? platform_id : 0;
                    bool proven = PatchProofs.HasFresh(proofIp, proofSteam);
                    if (!proven)
                    {
                        Console.WriteLine($"[patchproof] {(ConfigCampus.RequirePatchAtLogin ? "refused" : "would refuse")} {grant_type} login " +
                                          $"from {proofIp} (platform {platformparsed}, steam {proofSteam}): no current-patch proof");
                        if (ConfigCampus.RequirePatchAtLogin)
                            return BannedLoginResponse(
                                "Start Campus from the Campus launcher. This copy of the game isn't running the current " +
                                "Campus patch (download the launcher at camprec.net/campus.exe).", "patch");
                    }

                    // ---- Launcher-launch gate (LauncherLaunchProofs) ---------------
                    // The patch above proved the current DLL is loaded; this proves the current
                    // Campus launcher STARTED the game. Patch proof fresh + launch proof missing =
                    // someone ran a current patch outside the launcher (a custom tool holding the
                    // DLL). The player's Steam id is yanked from the normal-player whitelist so
                    // they have to come back through the launcher to be re-added. If the patch
                    // proof itself is missing we don't yank - that's just an older client, the
                    // patch gate above has already told them to update.
                    if (proven && !LauncherLaunchProofs.HasFresh(proofIp, proofSteam))
                    {
                        Console.WriteLine($"[launchproof] {(ConfigCampus.RequireLauncherLaunch ? "refused" : "would refuse")} {grant_type} login " +
                                          $"from {proofIp} (platform {platformparsed}, steam {proofSteam}): patch proof OK but no launcher-launch proof");
                        if (ConfigCampus.RequireLauncherLaunch)
                        {
                            // Yank the whitelist on a Steam login. On refresh / Quest / unverified
                            // Steam we have nothing specific to remove, so skip the yank.
                            if (proofSteam != 0)
                            {
                                bool removed = LauncherPlayers.Remove(proofSteam);
                                if (removed) Console.WriteLine($"[launchproof] removed Steam {proofSteam} from LauncherPlayers (ran outside the launcher) - will be re-added on next launcher sign-in");
                            }
                            return BannedLoginResponse(
                                "Start Campus from the Campus launcher. This login didn't come from the " +
                                "current Campus launcher (download it at camprec.net/campus.exe and press PLAY there).",
                                "launcher-launch");
                        }
                    }
                }

                // ---- Steam allowlist ---------------------------------------------
                // STEAM ONLY. Quest/Meta (Oculus) and every other platform skip this
                // entirely - ShouldCheck returns false for them, so a headset player is
                // never gated here.
                //
                // Sits with the ban gates, before any account lookup or creation, so a
                // non-listed Steam user cannot register an account on the way past.
                //
                // Off by default: an absent or empty Data/steam_allowlist.txt means
                // "allow everyone". An allowlist that locked the server the instant the
                // file appeared would be a foot-gun.
                if (platformparsed == Platforms.Steam && !steamVerified && SteamGate.Active)
                {
                    Console.WriteLine($"[steam-allowlist] refused login: Steam identity not verified ({steamWhy})");
                    return BannedLoginResponse(
                        "Campus couldn't verify your Steam account. Make sure Steam is running and " +
                        "signed in, then try again.", "steam-allowlist");
                }
                if (SteamAllowlist.ShouldCheck(platformparsed, platform_id)
                    && !SteamGate.Allowed(platform_id))
                {
                    // NOT a brute-force failure, and it must not be recorded as one.
                    //
                    // This used to call LoginGuard.RecordFailure, which is the 3-strikes-per-IP
                    // brute-force counter. But an unlisted Steam id is a POLICY refusal against a
                    // verified identity - there is no secret being guessed, and the client retries
                    // on its own, so one un-allowlisted player burned all three strikes in seconds
                    // and locked the whole ADDRESS out of /Auth/connect/token for five minutes.
                    // Everyone behind that address - a household, a CGNAT range, allowlisted
                    // players signing in to their own accounts - then got "Unable to login" too.
                    // That is the half of the error-3 reports that came from people who were on
                    // the allowlist and had done nothing wrong.
                    //
                    // The allowlist still refuses the login; it just stops manufacturing lockouts
                    // for bystanders. Password guessing (below) is what feeds the lockout.
                    Console.WriteLine($"[steam-allowlist] refused login: SteamID {platform_id} is not whitelisted (launcher list or allowlist)");
                    return BannedLoginResponse(LauncherPlayers.Required
                        ? "This Steam account hasn't been through the Campus launcher yet. Download it from " +
                          "camprec.net/campus.exe, sign in, and press PLAY there."
                        : "This Steam account is not on the Campus allowlist. Ask an admin to add your SteamID64.",
                        "steam-allowlist");
                }

                // Only parse account_id for grants that actually carry one. create_account has
                // no account yet, and the password grant identifies the account by username -
                // parsing a missing account_id there would throw and skip the checks below.
                if (grant_type != "create_account" && grant_type != "password")
                {
                    ulong.TryParse(HttpContext.Request.Form["account_id"], out accid);
                }
                
                // (User-Agent is already enforced at the very top of this method, before any
                // parsing that could throw and skip it - see the RequireGameUserAgent gate.)
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            Console.WriteLine($"{grant_type} : {accid}");

            // The in-game "Enter Account details" login. Accounts are created on the WEBSITE
            // (SiteController /api/site/register); the game signs in to them by username +
            // password here. On success the account is bound 1:1 to this Steam id and device
            // (AccountLock) - that binding is what "locks the account to your Steam". After the
            // first login this account also appears in the platform's cached-login list, so
            // later launches sign in automatically.
            if (grant_type == "password")
            {
                string clientIp = Campus_live_rewrite.api.server.LoginGuard.ClientIp(HttpContext);

                string? Username = HttpContext.Request.Form["username"];
                string? Pass = HttpContext.Request.Form["password"];

                // Reject absurdly long credential fields up front (a bot posting garbage), before any
                // lookup. Counts as a failed attempt so it feeds the lockout too.
                if ((Username?.Length ?? 0) > ConfigCampus.MaxUsernameLength ||
                    (Pass?.Length ?? 0) > ConfigCampus.MaxPasswordLength)
                {
                    Campus_live_rewrite.api.server.LoginGuard.RecordFailure(clientIp);
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new { access_token = (string?)null, error = "invalid_grant", error_description = "Invalid username or password.", key = (string?)null, refresh_token = (string?)null }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                var loginResult = PlayerDB.GetAccountPassword((Username ?? "").Trim(), Pass, isAdminPanel: false);
                if (!loginResult.success)
                {
                    // Wrong username/password: count it toward the 3-strikes IP lockout.
                    Campus_live_rewrite.api.server.LoginGuard.RecordFailure(clientIp);
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            access_token = (string?)null,
                            error = "invalid_grant",
                            error_description = loginResult.error ?? "Incorrect username or password.",
                            key = (string?)null,
                            refresh_token = (string?)null
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                // Correct credentials: clear this IP's failure counter / lockout.
                Campus_live_rewrite.api.server.LoginGuard.RecordSuccess(clientIp);

                ulong loggedInId = loginResult.accid;

                // An ACCOUNT ban (no device / IP ban - those were refused above, before any account
                // lookup) no longer refuses the login: that showed as "error code 3". The player
                // signs in and lands in their own dorm; every other matchmake refuses them
                // (MatchmakingController, MatchmakingErrorCode.Banned), and the client shows the
                // ban notice from moderationBlockDetails.
                if (PlayerDB.HasActiveModerationBlock(loggedInId))
                    Console.WriteLine($"[auth] account {loggedInId} is banned - signed in, restricted to their dorm");

                // A password login has already proven who it is, so it is never gated on the platform
                // proof - but the platform id it RECORDS becomes a password-free way into the account
                // (cached login), so for Oculus only a Meta-verified id is linked. Unverified (or
                // unconfigured, or the sideload sentinel) links nothing: the login still succeeds, the
                // headset just types its password each launch. Reference: recflare auth, password grant.
                ulong linkPlatformId = platform_id;
                if (platformparsed == Platforms.Oculus)
                {
                    var (st, vid, why) = await VerifyOculusProof(platformparsed, platform_id, platform_auth);
                    if (st == OculusProof.Verified)
                    {
                        linkPlatformId = vid;
                        Console.WriteLine($"[meta] password login for account {loggedInId}: nonce verified, linking Meta id {vid}");
                    }
                    else
                    {
                        linkPlatformId = 0;
                        Console.WriteLine($"[meta] password login for account {loggedInId}: not linking headset ({st}: {why})");
                    }
                }

                // Enforce + apply the one-account-per-Steam / one-account-per-device lock.
                if (!AccountLock.TryEnforceAndBind(loggedInId, platformparsed, linkPlatformId, device_id, out string lockErr))
                {
                    return BannedLoginResponse(lockErr, "account-lock");
                }

                string pw_request_url = $"{Request.Scheme}://{Request.Host}";
                List<string> pw_roles = RolesForAccount(PlayerDB.GetFullAccount(loggedInId));
                string? pw_token = ClientSecurity.CreateToken(loggedInId, pw_roles, pw_request_url, platformparsed);
                if (string.IsNullOrEmpty(pw_token))
                    goto token_err;

                PlayerDB.SetLastLoginTime(loggedInId);
                PlayerDB.AddIpAddress(loggedInId, Ip);
                PlayerDB.ResetCheerCreditIfNewDay(loggedInId);

                Webhook.Send(loggedInId, "prod", "Matchmaking info",
                    $"({loggedInId}) signed in with credentials on **{platformparsed}**", 5898927);

                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        access_token = pw_token,
                        expires_in = ConfigCampus.GameTokenHours * 3600,
                        token_type = "Bearer",
                        refresh_token = Campus_live_rewrite.api.dbs.RefreshTokenDB.Issue(loggedInId),
                        scope = "offline_access rn.accounts rn.accounts.gc rn.api rn.auth rn.auth.gc rn.bugreporting rn.cards rn.chat rn.clubs rn.cms rn.commerce rn.data rn.data.gc rn.datacollection rn.datacollection.gc rn.discovery rn.gamelogs.gc rn.leaderboard rn.link rn.lists rn.match.read rn.match.write rn.moderation rn.notify rn.platformnotifications rn.playersettings rn.roomcomments rn.rooms rn.storage rn.strings rn.studio.gc",
                        key = ""
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (grant_type == "cached_login")
            {
                ulong steamidticket = 0;
                Console.WriteLine("account cached");
                // TEMPORARILY DISABLED STEAM AUTH CHECKS FOR DEBUGGING
                Console.WriteLine("[STEAM AUTH TEMPORARILY DISABLED]");
                /* 
                if (!string.IsNullOrEmpty(platform_auth))
                {
                    string unescaped = System.Net.WebUtility.UrlDecode(platform_auth);
                    Console.WriteLine("[PLATFORM AUTH (Decoded)]");
                    Console.WriteLine(unescaped);

                    var platformAuthJson = JsonConvert.DeserializeObject<Dictionary<string, string>>(unescaped);
                    if (platformAuthJson != null && platformAuthJson.TryGetValue("Ticket", out string ticket))
                    {
                        Console.WriteLine("[Parsed Ticket]");
                        Console.WriteLine(ticket);

                        using (var client = new HttpClient())
                        {
                            var url = $"https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1?key={ConfigCampus.SteamAPIKey}&appid=471710&ticket={ticket}";
                            var response = await client.GetAsync(url);
                            string result = await response.Content.ReadAsStringAsync();
                            Console.WriteLine("[Steam Auth Response]");
                            Console.WriteLine(result);

                            var steamAuthResponse = JsonConvert.DeserializeObject<SteamAuthResponse>(result);
                            if (steamAuthResponse != null && steamAuthResponse.response != null && steamAuthResponse.response.paramsData != null)
                            {
                                if (ulong.TryParse(steamAuthResponse.response.paramsData.steamid, out ulong steamIdFromTicket))
                                {
                                    steamidticket = steamIdFromTicket;
                                    Console.WriteLine("[Parsed SteamID from Ticket]");
                                    Console.WriteLine(steamidticket);
                                }
                                else
                                {
                                    return new ContentResult()
                                    {
                                        Content = "{\"access_token\":\"" + "\",\"error_description\":\"Cannot verify steam ticket\",\"error\":\"steam_ticket\",\"refresh_token\":\"" + "\",\"key\":\"\"}",
                                        ContentType = "application/json",
                                        StatusCode = 200
                                    };
                                }
                            }
                            else
                            {
                                return new ContentResult()
                                {
                                    Content = "{\"access_token\":\"" + "\",\"error_description\":\"Cannot verify steam ticket\",\"error\":\"steam_ticket\",\"refresh_token\":\"" + "\",\"key\":\"\"}",
                                    ContentType = "application/json",
                                    StatusCode = 200
                                };
                            }
                        }
                    }
                }
                */
                if (platformparsed == Platforms.Steam && !steamVerified)
                {
                    Console.WriteLine($"[cachedlogin] refused for account {accid}: Steam identity not verified ({steamWhy})");
                    return GrantError(400, "invalid_grant", "invalid or missing Steam ticket");
                }
                // Platform ownership is the ONLY credential a cached login has, so (1) for Oculus the
                // proof must verify with Meta, and (2) the account handed back must already be LINKED
                // to that identity - read from the same table the picker lists from, so an entry the
                // picker offered is always redeemable and nothing else is. Without (2), anyone could
                // post grant_type=cached_login&account_id=X and receive a token for X.
                // Reference: recflare auth, cached_login grant.
                {
                    var (st, vid, why) = await VerifyOculusProof(platformparsed, platform_id, platform_auth);
                    if (st == OculusProof.Unconfigured)
                    {
                        Console.WriteLine("[meta] refusing a cached Quest login: no app secret configured");
                        return GrantError(500, "server_error", "Meta platform verification is not configured");
                    }
                    bool deviceCached = false;
                    if (st == OculusProof.Sentinel && ConfigCampus.QuestDeviceCachedLogin &&
                        PlayerDB.IsDeviceBoundTo(accid, device_id, Platforms.Oculus))
                    {
                        // Quest "remember me": this headset already signed in to this account with a
                        // password (the device lock bound it then). Device ownership is the credential.
                        deviceCached = true;
                        Console.WriteLine($"[cachedlogin] Quest device '{device_id}' cached login into account {accid}");
                    }
                    else if (st == OculusProof.Sentinel || st == OculusProof.Rejected)
                    {
                        Console.WriteLine($"[meta] cached login refused for account {accid}: {why}");
                        return GrantError(400, "invalid_grant", "invalid or missing platform_auth");
                    }
                    if (st == OculusProof.Verified) platform_id = vid;

                    if (!deviceCached)
                    {
                        PlayerDB.GetLogins(platformparsed, platform_id, out var linked);
                        bool isLinked = platform_id != 0 && linked != null && linked.Any(l => l.accountId == accid);
                        if (!isLinked)
                        {
                            Console.WriteLine($"[cachedlogin] refused: account {accid} is not linked to {platformparsed} id {platform_id}");
                            return GrantError(400, "invalid_grant", "account is not linked to this platform identity");
                        }
                    }
                }

                var accountData = PlayerDB.GetFullAccount(accid);

                // Enforce + apply the one-account-per-Steam / one-account-per-device lock. The
                // cached list only offers accounts already bound to this Steam id, so this
                // mainly stamps/validates the device binding and blocks a second account being
                // signed in on the same machine. Exempt accounts (Coach/Kyle) pass through.
                if (!AccountLock.TryEnforceAndBind(accid, platformparsed, platform_id, device_id, out string cachedLockErr))
                {
                    return BannedLoginResponse(cachedLockErr, "account-lock");
                }

                string request_url = $"{Request.Scheme}://{Request.Host}";
                // Roles derived from the account's DevFlag so admin-panel toggles actually
                // reach the client on next login (see RolesForAccount at top of this file).
                List<string> roles = RolesForAccount(accountData);
                token = ClientSecurity.CreateToken(accid, roles, request_url, platformparsed);
                if (token == null)
                    goto token_err;
                if (string.IsNullOrEmpty(token))
                {
                    goto token_err;
                }

                Webhook.Send(accid,
                    "prod",
                    "Matchmaking info",
                    $"({accid}) is logging in to **{ver}** on the platform **{platformparsed.ToString()}**",
                    5898927
                );
                PlayerDB.SetPlayerDeviceId(device_id, accid);
                PlayerDB.SetLastLoginTime(accid);
                PlayerDB.AddIpAddress(accid, Ip);
                PlayerDB.ResetCheerCreditIfNewDay(accid);

                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        access_token = token,
                        expires_in = ConfigCampus.GameTokenHours * 3600,
                        token_type = "Bearer",
                        refresh_token = Campus_live_rewrite.api.dbs.RefreshTokenDB.Issue(accid),
                        scope = "offline_access rn.accounts rn.accounts.gc rn.api rn.auth rn.auth.gc rn.bugreporting rn.cards rn.chat rn.clubs rn.cms rn.commerce rn.data rn.data.gc rn.datacollection rn.datacollection.gc rn.discovery rn.gamelogs.gc rn.leaderboard rn.link rn.lists rn.match.read rn.match.write rn.moderation rn.notify rn.platformnotifications rn.playersettings rn.roomcomments rn.rooms rn.storage rn.strings rn.studio.gc",
                        key = ""
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            // Renew the session IN PLACE (reference: apps/auth refresh_token grant). The client
            // redeems the single-use refresh token it got at login (RefreshTokenDB) and gets a new
            // access token - roles and the rn.privilege claim (mic ban) re-read from the account -
            // plus a new refresh token. This is what a RefreshLogin push triggers. It used to answer
            // an empty body: the client logged "RecNet login returned a blank response" and fell
            // back to a full logout + relogin, so a mic ban only landed after that reload.
            if (grant_type == "refresh_token")
            {
                string? presented = HttpContext.Request.Form["refresh_token"];
                ulong? refreshed = Campus_live_rewrite.api.dbs.RefreshTokenDB.Consume(presented);
                var refreshAccount = refreshed == null ? null : PlayerDB.GetFullAccount(refreshed.Value);
                if (refreshed == null || refreshAccount?.player == null)
                {
                    Console.WriteLine($"[refresh] refused: refresh_token {(string.IsNullOrEmpty(presented) ? "absent" : "unknown or expired")}");
                    return GrantError(400, "invalid_grant", "refresh_token is invalid or expired");
                }
                accid = refreshed.Value;

                // The platform claim: the request's when it names one, else the one the player's
                // presence was recorded with.
                Platforms refreshPlatform = platformparsed;
                if (refreshPlatform == Platforms.All)
                {
                    try { refreshPlatform = (Platforms)HeartbeatDB.GetPlayerHeartbeat(accid).platform; } catch { }
                }

                string refresh_request_url = $"{Request.Scheme}://{Request.Host}";
                string? refreshedToken = ClientSecurity.CreateToken(accid, RolesForAccount(refreshAccount), refresh_request_url, refreshPlatform);
                if (string.IsNullOrEmpty(refreshedToken))
                    goto token_err;
                var (privileges, _) = PlayerDB.TokenPrivileges(accid);
                Console.WriteLine($"[refresh] {accid} renewed its token in place{(privileges.Count > 0 ? " - privileges [" + string.Join(",", privileges) + "]" : "")}");

                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        access_token = refreshedToken,
                        expires_in = ConfigCampus.GameTokenHours * 3600,
                        token_type = "Bearer",
                        refresh_token = Campus_live_rewrite.api.dbs.RefreshTokenDB.Issue(accid),
                        scope = "offline_access rn.accounts rn.accounts.gc rn.api rn.auth rn.auth.gc rn.bugreporting rn.cards rn.chat rn.clubs rn.cms rn.commerce rn.data rn.data.gc rn.datacollection rn.datacollection.gc rn.discovery rn.gamelogs.gc rn.leaderboard rn.link rn.lists rn.match.read rn.match.write rn.moderation rn.notify rn.platformnotifications rn.playersettings rn.roomcomments rn.rooms rn.storage rn.strings rn.studio.gc",
                        key = ""
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            bool questCreate = grant_type == "create_account"
                               && (ConfigCampus.InGameAccountCreation
                                   || (platformparsed == Platforms.Oculus && ConfigCampus.QuestInGameAccountCreation));
            if (grant_type == "create_account" && !questCreate)
            {
                // ACCOUNT CREATION IS WEBSITE-ONLY NOW (except a headset's first launch - see
                // ConfigCampus.QuestInGameAccountCreation and the questCreate block below).
                //
                // The game used to auto-create an account on first launch. It no longer does:
                // players make a Campus account on the website (choose @username, display name,
                // password), then sign in from the game with those credentials (grant_type=
                // password). Refusing here is what makes the client fall through to the "Enter
                // Account details" login instead of minting a throwaway account on first boot.
                Console.WriteLine("[create_account] REFUSED - account creation is website-only");
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        error = "account_creation_disabled",
                        error_description = "Accounts can't be created in-game. Make yours at camprec.net, "
                                          + "then come back and LOG IN with that username and password."
                    }),
                    ContentType = "application/json",
                    StatusCode = 403
                };
            }

            if (questCreate && platformparsed == Platforms.Steam && !steamVerified)
            {
                // A PC account made in-game is bound to its Steam id - which must be the one Steam
                // signed, or anyone could make accounts in someone else's name (and use up their
                // one-in-game-account slot).
                Console.WriteLine($"[create_account] REFUSED - Steam identity not verified ({steamWhy})");
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        error = "steam_ticket",
                        error_description = "Campus couldn't verify your Steam account. Make sure Steam is running and signed in, then try again."
                    }),
                    ContentType = "application/json",
                    StatusCode = 403
                };
            }

            if (questCreate)
            {
                Console.WriteLine($"[create_account] {platformparsed} (platform id {platform_id}, device '{device_id}') creating an account in-game");
                // What the client POSTs when it creates an account: field NAMES and value lengths only (never the values - the
                // platform ticket and any password are in there). Shows whether the title-screen flow sent a username /
                // password / birthday along, or this is the bare platform auto-create.
                try
                {
                    if (HttpContext.Request.HasFormContentType)
                        Console.WriteLine("[create_account] posted fields: " + string.Join(", ", HttpContext.Request.Form.Select(kv => $"{kv.Key}({kv.Value.ToString().Length})")));
                }
                catch { }

                // Same caps as the website signup form (ConfigCampus.SignupsPer*): a headset is not
                // a way around them.
                string signupIp = Campus_live_rewrite.api.server.LoginGuard.ClientIp(HttpContext);
                var limit = PlayerDB.CheckSignupLimit(signupIp, out DateTime nextAllowed);
                if (limit != PlayerDB.SignupLimit.None)
                {
                    // NOT a 429. On a headset, create_account is the client's FIRST move whenever
                    // its device is not yet bound - it fires before the player is ever offered
                    // "log in with an existing account". A 429 here therefore did not just refuse
                    // a new account: it showed Code 3 and dead-ended the flow, so a RETURNING
                    // player on a new or reinstalled headset could not reach their own account
                    // either. That is the other half of "can't log in to their own accounts".
                    //
                    // The 403 account_creation_disabled shape is the one the client already
                    // understands as "not here - go and log in": it falls through to the
                    // credentials screen (the same response the website-only mode uses). A
                    // returning player logs in; a genuinely new player on a capped address is
                    // told where to make their account instead of being shown an error code.
                    Console.WriteLine($"[create_account] refused ({limit}) IP {signupIp} - next allowed {nextAllowed:u} - sending the client to the login screen");
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            error = "account_creation_disabled",
                            error_description = limit == PlayerDB.SignupLimit.Global
                                ? "Lots of accounts are being created right now. If you already have a Campus account, log in with it below - otherwise please try again in a little while."
                                : "Several accounts were recently made from your network. If you already have a Campus account, log in with it below - otherwise make one at camprec.net and come back."
                        }),
                        ContentType = "application/json",
                        StatusCode = 403
                    };
                }

                // ---- one account per device -------------------------------------------------
                // A device that already belongs to an account cannot register another. Playing
                // on a second machine is fine - signing in there adds that device to the
                // account (PlayerDB.SetPlayerDeviceId) - but it cannot be used to make a NEW
                // one. This is the anti-alt rule; without it a single device could create
                // unlimited accounts, which is what the code did before.
                //
                // A missing device id is rejected outright rather than waved through: an
                // empty id is not bindable, not bannable, and would be the obvious way around
                // this check.
                //
                // Worth knowing: device_id is supplied by the CLIENT, so it is spoofable by a
                // modified one. This raises the cost of alt farming, it does not make it
                // impossible - especially with the patch gate currently disabled.
                if (string.IsNullOrWhiteSpace(device_id))
                {
                    Console.WriteLine("[create_account] REFUSED - no device id supplied");
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            error = "invalid_request",
                            error_description = "A device id is required to create an account."
                        }),
                        ContentType = "application/json",
                        StatusCode = 400
                    };
                }

                // An admin-allowlisted IP may create more accounts on its own device, to test the
                // in-game creation flow (ConfigCampus.AdminRateLimitEnabled = false).
                bool adminExempt = AdminProtectionMiddleware.IsLimitExempt(signupIp);
                if (adminExempt) Console.WriteLine($"[create_account] IP {signupIp} is admin-allowlisted - limits waived");
                if (!adminExempt && !PlayerDB.CanCreateAccountOnDevice(device_id, out ulong existingAcc))
                {
                    Console.WriteLine($"[create_account] REFUSED - device '{device_id}' already "
                                    + $"has its game-made account ({existingAcc}) or is full");
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            error = "device_already_registered",
                            error_description = "This device already has a Campus account made in-game. "
                                              + "Log in with it, or add the account you made on the website."
                        }),
                        ContentType = "application/json",
                        StatusCode = 403
                    };
                }

                // Create new account
                try
                {
                    account_data newAccount;
                    // The no-Meta / fallback headset sends the shared sentinel id - store NO platform
                    // id for it (see PlatformIdentity); the device binding below is what remembers it.
                    ulong storedPlatformId = PlatformIdentity.IsSentinel(platformparsed, platform_id) ? 0 : platform_id;
                    if (platformparsed == Platforms.Oculus && storedPlatformId != 0)
                    {
                        // A real Meta id is stored only once Meta vouches for it (as the password grant links).
                        var (mst, mvid, mwhy) = await VerifyOculusProof(platformparsed, platform_id, platform_auth);
                        storedPlatformId = mst == OculusProof.Verified ? mvid : 0;
                        if (mst != OculusProof.Verified) Console.WriteLine($"[create_account] Meta id {platform_id} not verified ({mst}: {mwhy}) - stored without it");
                    }
                    bool success = PlayerDB.CreateAccount(platformparsed, storedPlatformId, device_id, 0, out newAccount);
                    
                    if (!success || newAccount == null)
                    {
                        Console.WriteLine("Failed to create account");
                        return new ContentResult()
                        {
                            Content = JsonConvert.SerializeObject(new
                            {
                                error = "account_creation_failed",
                                error_description = "Failed to create account"
                            }),
                            ContentType = "application/json",
                            StatusCode = 200
                        };
                    }
                    
                    ulong newAccountId = newAccount.accountId;
                    Console.WriteLine($"[create_account] created account {newAccountId} @{newAccount.username} from IP {signupIp}");
                    PlayerDB.RecordSignup(signupIp, newAccountId);
                    // A password the client posted becomes the login credential (so the account can
                    // also be used on the website / another device). Without one, the headset's
                    // device-remembered login is how they get back in.
                    string? createPass = HttpContext.Request.HasFormContentType ? HttpContext.Request.Form["password"].ToString() : null;
                    if (!string.IsNullOrEmpty(createPass) && createPass.Length >= 6 && createPass.Length <= 64)
                        PlayerDB.SetPlayerPassword(createPass, newAccountId, false);
                    // Bind the device to the new account (one account per device, remembered login).
                    PlayerDB.BindDevice(device_id, platformparsed, newAccountId);
                    // The client's own identity flow (Orientation: choose username, create
                    // password, code of conduct) runs for this account - see NewPlayerBootstrap.
                    Campus_live_rewrite.api.server.NewPlayerBootstrap.Start(newAccountId, platformparsed, string.IsNullOrEmpty(createPass));
                    Notifications.FireAndForget(Notifications.PushAccountUpdate(newAccountId), "new-account push");

                    // Generate token for the new account
                    string request_url = $"{Request.Scheme}://{Request.Host}";
                    List<string> roles = new List<string> { "gameclient" };
                    token = ClientSecurity.CreateToken(newAccountId, roles, request_url, platformparsed);

                    if (string.IsNullOrEmpty(token))
                    {
                        Console.WriteLine("Failed to generate token for new account");
                        goto token_err;
                    }

                    Console.WriteLine($"Account created successfully: {newAccount.username}");
                    
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            access_token = token,
                            expires_in = ConfigCampus.GameTokenHours * 3600,
                            token_type = "Bearer",
                            refresh_token = Campus_live_rewrite.api.dbs.RefreshTokenDB.Issue(newAccountId),
                            scope = "offline_access rn.accounts rn.accounts.gc rn.api rn.auth rn.auth.gc rn.bugreporting rn.cards rn.chat rn.clubs rn.cms rn.commerce rn.data rn.data.gc rn.datacollection rn.datacollection.gc rn.discovery rn.gamelogs.gc rn.leaderboard rn.link rn.lists rn.match.read rn.match.write rn.moderation rn.notify rn.platformnotifications rn.playersettings rn.roomcomments rn.rooms rn.storage rn.strings rn.studio.gc",
                            key = ""
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error creating account: {ex.ToString()}");
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            error = "account_creation_failed",
                            error_description = "Failed to create account"
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }
            }

        token_err:
            // A grant this server does not accept. OAuth2 says invalid_grant is a 400 with a JSON
            // error body, and that is what the client expects: a 400 here makes it fall through to
            // its next login option (the reference Quest trace is "400 /connect/token" for the
            // refused platform login, then "200" for username/password). The previous empty 401
            // reads as "session dead" to an OAuth client rather than "try another grant".
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    error = "invalid_grant",
                    error_description = "This login method is not accepted. Sign in with your Campus username and password."
                }),
                ContentType = "application/json",
                StatusCode = 400
            };
        }

        // Quest/Oculus login step. After cached-login the headset client GETs this and expects a
        // JSON STRING nonce back (there is no RecNet "Nonce" DTO in the client metadata, so an
        // object would fail to deserialise). The client literal is "oculus/nonce", appended to the
        // Auth base URL from the nameserver -> /Auth/oculus/nonce. This server does not verify
        // Oculus proofs against Meta, so the nonce carries no meaning beyond letting the client's
        // flow continue to /Auth/connect/token, where the real credential check happens. Without
        // this route the headset saw a 404 here and stopped.
        [HttpGet("/Auth/oculus/nonce")]
        [HttpPost("/Auth/oculus/nonce")]
        public ContentResult OculusNonce()
        {
            string nonce = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            Console.WriteLine("[oculus] nonce issued to headset client");
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(nonce),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Client literal "cachedlogin/migrate" - no handler existed. Ack envelope, same shape the
        // gap stubs use, so a migrate call from a headset is a no-op rather than a 404.
        [HttpPost("/Auth/cachedlogin/migrate")]
        public IActionResult CachedLoginMigrate()
            => Ok(new { error = "", success = true, value = (object?)null });

        [HttpPost("/Auth/cachedlogin/forplatformids")]
        public IActionResult GetCachedLoginForPlatformIds()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }



        [HttpDelete("/Auth/cachedlogin/current")]
        public ContentResult DeleteCurrentCachedLogin()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "This ain't implemented yet.",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // NOTE: the single dev/moderator checks (/Auth/role/developer/{id} + /moderator/{id}) live in
        // AccountController now (with [HttpGet] + a NotFound for unknown ids + the bulk ?id= form).
        // The duplicate [Route] handlers that were here matched ALL verbs on the same path, which is a
        // latent ambiguous-route hazard - removed so there's exactly one handler per route.

        [Route("account/me/remoteauth")]
        public ContentResult RemoteAuth([FromForm] string code)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "This ain't implemented yet.",
                    value = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/Auth/account/me/changepassword")]
        public ContentResult ChangePassword([FromForm] string newPassword)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (string.IsNullOrEmpty(newPassword))
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Password cannot be empty!",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (newPassword.Length > 64)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Password must be fewer than 64 characters.",
                        value = (string?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            PlayerDB.SetPlayerPassword(newPassword, account.Id, log: false);
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
            /// <summary>
        /// Refusal returned to a login from a banned device or IP. 401 so the client
        /// treats it as a failed sign-in rather than a transport error.
        /// </summary>
        private static ContentResult BannedLoginResponse(string? reason, string kind)
        {
            return new ContentResult()
            {
                Content = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    error = "access_denied",
                    error_description = string.IsNullOrWhiteSpace(reason)
                        ? $"This {kind} is banned."
                        : reason
                }),
                ContentType = "application/json",
                StatusCode = 401
            };
        }

}
}
