using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Contracts;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography.Xml;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Discord;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Configuration.UserSecrets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
using CampusLiveSource.api.admin;
using CampusLiveSource.Controllers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using static ImageMetadataDB;
using static Campus_live_rewrite.api.server.Classes.db_class.EventDBClasses;
using static Campus_live_rewrite.api.server.Classes.db_class.RoomDBClasses;
using static Campus_live_rewrite.api.server.Classes.RRPlus;
using static Campus_live_rewrite.api.WebsocketEvents;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.IO;

namespace Campus_live_rewrite.api.Controllers
{
    [ApiController]
    public class APIController : ControllerBase
    {
        private static readonly string UPTIME = DateTime.UtcNow.ToString("o");


        [HttpGet("/api/versioncheck/v4")]
        public IActionResult VersionCheck([FromQuery(Name = "v"), Required] string appVersion, [FromQuery(Name = "p")] int? platform)   // p optional: a client that omits it got a 400 before any answer
        {
            /*if (appVersion == "20250731.01")
            {
                return Ok(new VersionCheck
                {
                    ValidVersion = VersionStatus.ValidForPlay,
                    VersionStatus = VersionStatus.ValidForPlay,
                    UpdateNotificationStage = UpdateNoti.None,
                    IsVersionIslanded = false,
                    IsCrossPlayDisabled = false
                });
            }

            return Ok(new VersionCheck
            {
                ValidVersion = VersionStatus.UpdateRequired,
                VersionStatus = VersionStatus.UpdateRequired,
                UpdateNotificationStage = UpdateNoti.Prompt,
                IsVersionIslanded = true,
                IsCrossPlayDisabled = false
            });*/
            return Ok(new VersionCheck
                {
                    ValidVersion = VersionStatus.ValidForPlay,
                    VersionStatus = VersionStatus.ValidForPlay,
                    UpdateNotificationStage = UpdateNoti.None,
                    IsVersionIslanded = false,
                    IsCrossPlayDisabled = false
                });
        }

        [HttpGet("/api/versioncheck/islandedversions")]
        public IActionResult IslandedVersions()
        {
            return Ok(ConfigCampus.Bracket);
        }

        [HttpGet("/api/gameconfigs/v1/all")]
        public IActionResult Gameconfigs()
        {
            string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "Gameconfigs.json");

            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine("Warning: Gameconfigs.json not found, returning []");
                return Ok(ConfigCampus.Bracket);
            }

            // The Level Rewards tiles read their picture from Lifecycle.PSF.Progression.RewardMap
            // (one entry per level) and UnknownRewardImage. Both are rewritten here to
            // ConfigCampus.LevelRewardImage, cached on the file's mtime, so the picture is one
            // config line rather than fifty JSON entries - and the file itself is left alone.
            string cfg = Campus_live_rewrite.api.server.GameConfigs.WithRewardImage(path);

            // PERSONAL QUICK CHAT: a player who edited their quick chat phrases on the website gets their own copy of the four
            // CannedChatOptions values (everyone else gets the shared cached copy untouched). The answer then differs per player,
            // so it is private and its ETag (below) is computed from the personal content.
            string? personal = Campus_live_rewrite.api.server.QuickChat.ApplyToConfigs(cfg, AuthHelper.OptionalViewerId(HttpContext));
            if (personal != null)
            {
                cfg = personal;
                Response.Headers["Cache-Control"] = "private, no-store";
            }

            // Keep the conditional GET PhysicalFile used to give for free: the client sends
            // If-None-Match on every launch and this file is ~300 KB, so a 304 is worth having.
            string etag = "\"" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(cfg))).ToLowerInvariant() + "\"";
            Response.Headers["ETag"] = etag;
            if (Request.Headers.TryGetValue("If-None-Match", out var inm) && inm.ToString() == etag)
                return new StatusCodeResult(304);

            return new ContentResult
            {
                Content = cfg,
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        // Voice-MODERATION config (the speech-moderation vendor creds), NOT a voice on/off switch.
        // RecFlare's moderation worker returns exactly { AccountId: "", ApiKey: "" }: both blank,
        // both present, both STRINGS. The client treats blank credentials as "voice moderation is
        // off" and continues setting up voice - but a MISSING key or a null trips its parser and
        // fails voice setup entirely, which is why the old { enabled:false, regions:[] } shape (no
        // AccountId/ApiKey at all) killed voice: players could open the mic but nobody could hear
        // them. AccountId is a string even though it reads as a number.
        [HttpGet("/voice/config")]
        public IActionResult VoiceConfig() => Ok(new
        {
            AccountId = "",
            ApiKey = ""
        });

        // The caller's moderation restrictions, as a bare array. RecFlare always answers [] (an
        // empty, unrestricted list) - and the client refills its restriction list from THIS: a
        // chat/voice restriction is a record being present here (with an EndDate). This route was
        // missing, so a 404 could leave the client on a stale or safe-default restricted state.
        // Empty array = nothing restricted = voice and room chat allowed.
        // svc 1 = Auth, so the client asks <auth base>/privileges/me/restrictions - the root-only
        // spelling 404'd every launch (wire-shapes: privileges-me-restrictions).
        [HttpGet("/Auth/privileges/me/restrictions")]
        [HttpGet("/privileges/me/restrictions")]
        public IActionResult MyRestrictions()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            // Active mic / room-chat bans as interventions (PlayerDB.RestrictionsFor) - the list the
            // in-game Moderation Notice shows with its countdown. The bans themselves are enforced
            // through the token's rn.privilege claim (PlayerDB.TokenPrivileges). [] = none.
            var list = PlayerDB.RestrictionsFor(account.Id);
            if (list.Count > 0) Console.WriteLine($"[restrictions] {account.Id} <- {JsonConvert.SerializeObject(list)}");
            return Content(JsonConvert.SerializeObject(list), "application/json");
        }

        // Azure Speech text-to-speech config — same story: we don't run TTS, return empty.
        [HttpGet("/api/config/v1/azurespeech")]
        public IActionResult AzureSpeechConfig() => Ok(new
        {
            enabled = false,
            regions = Array.Empty<object>(),
            voiceProfiles = Array.Empty<object>()
        });

        [HttpGet("/api/config/v1/amplitude")] // Get amplitude, analytics stuff - e12354
        public ContentResult Amplitude()
        {
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new Amplitude2023
                {
                    AmplitudeKey = "cb2fb2ecb9953512c29af5bca58f2b4a",
                    UseRudderStack = true,
                    RudderStackKey = "23NiJHIgu3koaGNCZIiuYvIQNCu",
                    UseStatSig = ConfigCampus.StatsigEnabled,
                    StatSigKey = "client-SBZkOrjD3r1Cat3f3W8K6sBd11WKlXZXIlCWj6l4Aje",
                    StatSigEnvironment = 0
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/avatar/v1/defaultunlocked")]
        public IActionResult DefaultUnlockeItems()
        {
            // EMPTY, deliberately - this is what the 2023 server returns and it is the only
            // avatar endpoint the 2025 server answered differently.
            //
            // It used to serve the whole 3061-item avatar_items.json, from a "everyone gets
            // everything for free" idea that the ownership model has since replaced. Two
            // problems with that:
            //
            //  - The client fetches this BEFORE it logs in (it sits between
            //    /api/versioncheck/islandedversions and /Auth/connect/token in the request
            //    log), so it was handing out the entire catalog unauthenticated.
            //  - The client then received the same 3061 items twice, once here and again
            //    from /api/avatar/v4/items, and registering an item from two sources is a
            //    good way to end up with entries it cannot file into a wardrobe category -
            //    the "None does not have an associated browsable avatar item filter"
            //    warning, which is exactly the symptom of items not appearing while
            //    remaining findable by search.
            //
            // LOCAL EXPERIMENT (ConfigCampus.HoldNewAccountCreation): the customize screen of the client's own account
            // creation showed EMPTY clothing tabs and DONE threw a NullReferenceException in AvatarCustomizationModel.
            // A brand-new account may be built from THIS list (the starter set), so serve just the starter items - the
            // same ones every account is granted - instead of nothing. Not the catalog: see the comment below.
            if (ConfigCampus.HoldNewAccountCreation)
            {
                try
                {
                    string cpath = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "avatar_items.json");
                    if (System.IO.File.Exists(cpath))
                    {
                        // The creation screen's tabs are built from this list, and DefaultAvatarItems.json alone has no bottoms (the dorm shows
                        // them because it lists what the account OWNS). A logged-in caller therefore gets the same set /api/avatar/v4/items
                        // gives it; an anonymous one (the pre-login fetch) gets exactly DefaultAvatarItems.json.
                        var cat = JArray.Parse(System.IO.File.ReadAllText(cpath));
                        JArray pick;
                        var (acc, accErr) = AuthHelper.ValidateToken(HttpContext);
                        if (accErr == null && acc != null)
                        {
                            var ownedSet = PlayerDB.OwnedAvatarItemDescs(acc.Id);
                            pick = new JArray(cat.Where(e => (bool?)e["IsBaseAvatarItem"] == true || ownedSet.Contains(PlayerDB.NormalizeAvatarItemDesc((string?)e["AvatarItemDesc"]))));
                        }
                        else
                        {
                            var starter = new HashSet<string>(PlayerDB.DefaultAvatarItemDescs().Select(d => d.Trim()), StringComparer.OrdinalIgnoreCase);
                            pick = new JArray(cat.Where(e => starter.Contains(((string?)e["AvatarItemDesc"] ?? "").Trim())));
                        }
                        Console.WriteLine($"[items] defaultunlocked (local experiment): {pick.Count} starter item(s)");
                        return new ContentResult { Content = pick.ToString(Newtonsoft.Json.Formatting.None), ContentType = "application/json", StatusCode = 200 };
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[items] defaultunlocked experiment failed: {ex.Message}"); }
            }

            // What a player owns is answered by /api/avatar/v4/items. This endpoint means
            // "unlocked for everyone regardless of account", and the answer is nothing.
            return new ContentResult
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/customAvatarItems/v1/isCreationAllowedForAccount")]
        public ContentResult IsCreationAllowedForAccount()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new SuccessResponse
                {
                    success = true,
                    value = null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/objectives/v1/myprogress")]
        public ContentResult MyObjectivesProgress()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var (objectives, groups) = PlayerDB.GetObjectivesProgress(account.Id);

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new MyObjectives
                {
                    // The client owns the objective catalog; we only report per-(Group,Index)
                    // progress. Field names match the client's Objective / ObjectiveGroup DTOs.
                    Objectives = objectives.Select(o => (object)new
                    {
                        o.Group,
                        o.Index,
                        o.Progress,
                        o.VisualProgress,
                        o.IsCompleted,
                        o.HasClaimedReward
                    }).ToList(),
                    ObjectiveGroups = groups.Select(g => (object)new
                    {
                        g.Group,
                        g.IsCompleted,
                        ClearedAt = g.ClearedAt
                    }).ToList()
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // A player report, raised from the in-game report UI.
        //
        // THIS ROUTE DID NOT EXIST. Every report a player filed got a 404 and was thrown away,
        // which is the whole of "reporting doesn't work" - the client shows its confirmation
        // either way, so it looked like it had worked. Form-encoded, and the reporter is the
        // BEARER TOKEN, never a body field (reference: POST /api/PlayerReporting/v3/create).
        // Only PlayerIdReported is required; the client omits whatever it has no value for, so a
        // report raised outside a room carries no RoomId. ReportCategory and RoomInstanceType are
        // stored verbatim - neither enum is mapped here.
        //
        // Answers the real service's { success, error } envelope, error being an EMPTY STRING and
        // not null, and the refusal uses the same shape so the client only parses one.
        [HttpPost("/api/PlayerReporting/v3/create")]
        [HttpPost("/api/PlayerReporting/v2/create")]
        [HttpPost("/api/PlayerReporting/v1/create")]
        public IActionResult CreatePlayerReport()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string F(string k) => Request.HasFormContentType ? (Request.Form[k].ToString() ?? "") : "";
            ulong U(string k) => ulong.TryParse(F(k), out var v) ? v : 0;
            int I(string k) => int.TryParse(F(k), out var v) ? v : 0;
            double? D(string k) => double.TryParse(F(k), System.Globalization.NumberStyles.Float,
                                                   System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (double?)null;

            ulong reported = U("PlayerIdReported");
            if (reported == 0)
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { success = false, error = "PlayerIdReported is required" }),
                    ContentType = "application/json",
                    StatusCode = 400
                };

            // Reporting yourself is not a thing, and it is the obvious way to spam the queue.
            if (reported == account.Id)
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { success = true, error = "" }),
                    ContentType = "application/json",
                    StatusCode = 200
                };

            // 0 / -1 are the client's "no room" values - store null rather than a bogus id.
            ulong roomId = U("RoomId");

            try
            {
                Campus_live_rewrite.api.dbs.ReportsDB.Create(new Campus_live_rewrite.api.dbs.ReportsDB.Report
                {
                    ReporterPlayerId = account.Id,
                    ReportedPlayerId = reported,
                    ReportCategory = I("ReportCategory"),
                    Details = string.IsNullOrWhiteSpace(F("Details")) ? null : F("Details"),
                    HeightReporter = D("HeightReporter"),
                    HeightReported = D("HeightReported"),
                    RoomId = roomId > 0 ? roomId : (ulong?)null,
                    RoomInstanceType = string.IsNullOrWhiteSpace(F("RoomInstanceType")) ? null : F("RoomInstanceType")
                });
            }
            catch (Exception ex)
            {
                // A store failure must not tell the reporter their report bounced - log it and
                // take the report, the same way the real service does.
                Console.WriteLine($"[report] could not store {account.Id} -> {reported}: {ex.Message}");
            }

            // A report filed by STAFF (developer or moderator) is acted on at once instead of
            // waiting in the queue:
            //   * Underage (7 / CoC_Underage 100) -> the player is made junior. Being young is not
            //     misconduct, so no timeout - junior mode is the remedy.
            //   * anything else -> a 1-hour timeout (ConfigCampus.StaffReportTimeoutSeconds),
            //     through the same moderation block an admin ban uses, so they are removed live.
            // Staff are never actioned by this (a moderator cannot time out a developer, and no
            // report from anyone times out staff), nor is the Coach (1).
            try
            {
                bool reporterIsStaff = PlayerDB.CheckDevFlag(account.Id) || PlayerDB.CheckModFlag(account.Id);
                bool targetIsStaff = PlayerDB.CheckDevFlag(reported) || PlayerDB.CheckModFlag(reported);
                if (reporterIsStaff && !targetIsStaff && reported != 1)
                {
                    int cat = I("ReportCategory");
                    if (cat == (int)Moderation_Detail.ReportCategory.Underage || cat == (int)Moderation_Detail.ReportCategory.CoC_Underage)
                    {
                        PlayerDB.SetJunior(reported, true, account.Id, $"underage report by staff @{account.Username}");
                    }
                    else
                    {
                        var category = Enum.IsDefined(typeof(Moderation_Detail.ReportCategory), cat)
                            ? (Moderation_Detail.ReportCategory)cat : Moderation_Detail.ReportCategory.Moderator;
                        int seconds = Math.Max(60, ConfigCampus.StaffReportTimeoutSeconds);
                        string details = F("Details").Trim();
                        PlayerDB.Set_moderation_block(
                            reported, category, seconds,
                            gameSessionId: -1, isHostKick: false,
                            message: $"You have been timed out for {Math.Max(1, seconds / 60)} minutes by a moderator." +
                                     (details.Length > 0 ? " Reason: " + details : ""),
                            playerIdReporter: account.Id, isBan: true,
                            bannedByPlayerId: account.Id, addAppealMessage: false,
                            logKind: "report-timeout", logNote: details);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[report] staff action on {reported} by {account.Id} failed: {ex.Message}");
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = true, error = "" }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/PlayerReporting/v1/moderationBlockDetails")]
        public IActionResult ModerationBlockDetails()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(PlayerDB.Get_moderation_block(account.Id));
        }

        // The client's own tamper/health reporting. We always answer "false" (not a
        // violation) - but the client ALSO acts locally, so a report here is a record of
        // something the client already did to itself, not a decision we get to make.
        //
        // Worth logging: "Dropping to dormroom due to error [385120]" is preceded, to the
        // second, by a Hile of Type=Time with an empty message. That is the client's clock
        // check firing - so the random dorm drop is a client-side anti-cheat trip, not a
        // server error. Recording Type and Message here is what identifies the next one
        // without having to correlate journald against Player.log by hand.
        [HttpPost("/api/PlayerReporting/v1/hile")]
        public IActionResult HileAPI()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Read by hand: a Type value the HileTypes enum doesn't list failed model binding and
            // the whole call was a 400 (210 of them today) - and a report that isn't recorded is
            // the one you wanted to see. Unknown values are logged as their raw text.
            string typeRaw = Request.HasFormContentType ? Request.Form["Type"].ToString() : Request.Query["Type"].ToString();
            string? Message = Request.HasFormContentType ? Request.Form["Message"].ToString() : Request.Query["Message"].ToString();
            string typeText = Enum.TryParse<Hile.HileTypes>(typeRaw, true, out var t) ? $"{t} ({(int)t})" : $"'{typeRaw}'";

            Console.WriteLine($"[hile] {account.Id} reported {typeText}" +
                              $"{(string.IsNullOrWhiteSpace(Message) ? " - no message" : $": {Message}")}");

            return Ok("false");
        }

        [HttpGet("/api/avatar/v2")]
        public IActionResult myAvatarV2()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            ulong targetId = account.Id;

            player_data player = PlayerDB.GetFullAccount(targetId);
            if (player == null)
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            if (string.IsNullOrEmpty(player.player_Extra.avatar))
            {
                return Ok(new
                {
                    LegacyData = new
                    {
                        SelectionsV1 = (string?)null,
                        SelectionsV2 = (string?)null,
                        FaceFeatures = (string?)null,
                        SkinColor = (string?)null,
                        HairColor = (string?)null
                    },
                    Selections = Array.Empty<object>(),
                    DataVersion = 9,
                    CustomizationSettings = (string?)null,
                    ThumbnailFileName = (string?)null,
                    Name = (string?)null,
                    Accessibility = 0,
                    Slot = 0
                });
            }

            return new ContentResult()
            {
                Content = player.player_Extra.avatar,
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/avatar/v2/{accid?}")]
        public ContentResult AvatarV2(ulong? accid = null)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            ulong targetId = accid ?? account.Id;

            player_data player = PlayerDB.GetFullAccount(targetId);
            if (player == null)
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            if (string.IsNullOrEmpty(player.player_Extra.avatar))
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        FaceFeatures = "",
                        HairColor = "",
                        OutfitSelections = "",
                        SkinColor = ""
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            return new ContentResult()
            {
                Content = player.player_Extra.avatar,
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/avatar/v4/items")]
        public async Task<IActionResult> GetAvatarItems()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string path = Path.Combine(
                Environment.CurrentDirectory,
                "Data",
                "APIS",
                "items",
                "avatar_items.json"
            );

            if (!System.IO.File.Exists(path))
                return Ok(ConfigCampus.Bracket);

            // avatar_items.json is the CATALOG - every item that exists - not a statement
            // that the caller owns them. Serving it wholesale is why every account owned
            // all 3061 items. Developers keep that (the dev locker); everyone else gets
            // only what they own, which starts at DefaultAvatarItems.json and grows with
            // purchases. See PlayerDB.OwnedAvatarItemDescs.
            //
            // Per-player from here on, so it must not be cached by URL.
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";

            bool isDev = PlayerDB.HasDevLocker(account.Id);
            var owned = isDev ? null : PlayerDB.OwnedAvatarItemDescs(account.Id);

            try
            {
                var catalog = JArray.Parse(await System.IO.File.ReadAllTextAsync(path));

                // Optional: drop items this client build has no asset for. Off unless
                // Data/APIS/items/ClientKnownGuids.json exists - see ItemGuids.
                int before = catalog.Count;
                var renderable = new JArray(catalog.Where(e =>
                    ItemGuids.ClientCanRender((string?)e["AvatarItemDesc"])));

                if (renderable.Count != before)
                    Console.WriteLine($"[items] filtered {before - renderable.Count} item(s) the client has no asset for");

                catalog = renderable;

                // Serve the items RAW, exactly as avatar_items.json stores them (PascalCase, and
                // whatever guid form the descriptor uses). Reshaping this - camelCase projection or
                // short->full guid conversion - was tried and either did nothing or dropped items,
                // so the raw record is what the client actually wants here.
                // Hair dyes are not in avatar_items.json, so they have to be APPENDED rather than
                // filtered in - see HairGrants.FreeHairDyeItems. Everyone owns them, so they go on
                // both the dev locker and the per-player list, and the Modify Hair swatches stop
                // showing a price because the client reads them as already owned.
                var dyes = Campus_live_rewrite.api.server.HairGrants.FreeHairDyeItems();

                if (isDev)
                {
                    var devAll = new JArray(catalog);
                    // + every avatar item a store sells that the catalog doesn't list (DevLocker).
                    foreach (var e in Campus_live_rewrite.api.server.DevLocker.StoreOnlyAvatarItems(catalog)) devAll.Add(e);
                    foreach (var d in dyes) devAll.Add(d.DeepClone());
                    return new ContentResult
                    {
                        Content = devAll.ToString(Newtonsoft.Json.Formatting.None),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                // Base items come free with the account. They are the structural UGC shirt
                // bases - (UGCTee_Shirt), (UGCPulloverHoodie_Shirt) - that custom shirts are
                // built on, so a player without them cannot wear ANY custom shirt. The 2023
                // server grants these to non-devs for the same reason.
                var mine = new JArray(catalog.Where(e =>
                    (bool?)e["IsBaseAvatarItem"] == true ||
                    owned.Contains(PlayerDB.NormalizeAvatarItemDesc((string?)e["AvatarItemDesc"]))));

                int fromCatalog = mine.Count;
                foreach (var d in dyes) mine.Add(d.DeepClone());

                Console.WriteLine($"[items] {account.Id} owns {fromCatalog} of {catalog.Count} avatar items" +
                                  (dyes.Count > 0 ? $" + {dyes.Count} hair dye colour(s)" : ""));

                return new ContentResult
                {
                    Content = mine.ToString(Newtonsoft.Json.Formatting.None),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                // A broken catalog must not leave the player with no wardrobe at all.
                Console.WriteLine($"[items] could not filter catalog for {account.Id}: {ex.Message}");
                return new ContentResult
                {
                    Content = await System.IO.File.ReadAllTextAsync(path),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
        }

        [HttpGet("/api/checklist/v1/current")]
        [HttpGet("/api/checklist/v2/current")]   // v1 and v2 serve the same list (RecFlare)
        public ContentResult CurrentChecklist()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // ---- Small missing endpoints ported from RecFlare (were 404s the client treats as ----
        // ---- failed loads). Shapes and defaults taken straight from the reference. ------------

        // Mark a NUX checklist row done. Stubbed like RecFlare: nothing records the completion and
        // nothing is granted (no reward ledger to make the 25-token grant once-only), but the
        // balance-update envelope is well-formed so the flow completes instead of erroring.
        [HttpPost("/api/checklist/v1/complete")]
        [HttpPost("/api/checklist/v2/complete")]
        public IActionResult CompleteChecklist()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Ok(new
            {
                BalanceUpdates = new[] { new { UpdateResponse = 303, Data = Array.Empty<object>() } }, // 303 = CHECKLIST_REWARD_CONTEXT
                Balance = 0,
                CurrencyType = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens,
                BalanceType = -2
            });
        }

        // Report one objective's progress. Persists per-(Group,Index) progress; the first time an
        // objective completes it grants a fixed XP reward exactly once (PlayerDB latches it), then
        // hands back the parent group's state in the client's shape.
        [HttpPost("/api/objectives/v1/updateobjective")]
        public IActionResult UpdateObjective()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            long group = 0;
            int index = 0;
            double progress = 0, visualProgress = 0;
            bool isCompleted = false;
            try
            {
                using var reader = new StreamReader(HttpContext.Request.Body);
                var body = reader.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(body);
                    long GetL(string k) => jo.TryGetValue(k, StringComparison.OrdinalIgnoreCase, out var v) && v.Type == Newtonsoft.Json.Linq.JTokenType.Integer ? v.Value<long>() : 0;
                    double GetD(string k) => jo.TryGetValue(k, StringComparison.OrdinalIgnoreCase, out var v) && (v.Type == Newtonsoft.Json.Linq.JTokenType.Integer || v.Type == Newtonsoft.Json.Linq.JTokenType.Float) ? v.Value<double>() : 0;
                    bool GetB(string k) => jo.TryGetValue(k, StringComparison.OrdinalIgnoreCase, out var v) && v.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && v.Value<bool>();
                    group          = GetL("Group");
                    index          = (int)GetL("Index");
                    progress       = GetD("Progress");
                    visualProgress = GetD("VisualProgress");
                    isCompleted    = GetB("IsCompleted");
                }
            }
            catch { }

            bool justCompleted = PlayerDB.UpdateObjective(account.Id, group, index, progress, visualProgress, isCompleted, out var groupState, out bool groupDone);
            if (justCompleted)
            {
                if (ConfigCampus.ObjectiveCompletionXp > 0) PlayerDB.AddXp(account.Id, ConfigCampus.ObjectiveCompletionXp);
                Console.WriteLine($"[objectives] {account.Id} completed objective (group {group}, index {index}) -> +{ConfigCampus.ObjectiveCompletionXp} XP");
            }
            // The last of today's challenges: a game request on the watch, once a day (the group
            // latch in PlayerDB.UpdateObjective).
            if (groupDone) Campus_live_rewrite.api.server.DailyChallenges.GrantReward(account.Id, group);

            return Ok(new
            {
                group = groupState.Group,
                isCompleted = groupState.IsCompleted,
                clearedAt = groupState.ClearedAt ?? DateTime.UtcNow.ToString("o")
            });
        }

        // Clear an objectives group - marks it completed and stamps the cleared time. Individual
        // objectives are left intact (the client re-renders them; new challenges overwrite by key).
        [HttpGet("/api/objectives/v1/cleargroup")]
        [HttpPost("/api/objectives/v1/cleargroup")]
        public ContentResult ClearObjectivesGroup()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };

            long group = 0;
            try
            {
                if (Request.Query.TryGetValue("group", out var qg) && long.TryParse(qg, out var gq)) group = gq;
                else if (Request.Query.TryGetValue("Group", out var qG) && long.TryParse(qG, out var gG)) group = gG;
                else
                {
                    using var reader = new StreamReader(HttpContext.Request.Body);
                    var body = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(body);
                        if (jo.TryGetValue("Group", StringComparison.OrdinalIgnoreCase, out var g) && g.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                            group = g.Value<long>();
                    }
                }
            }
            catch { }

            var g2 = PlayerDB.ClearObjectiveGroup(account.Id, group);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { group = g2.Group, isCompleted = g2.IsCompleted, clearedAt = g2.ClearedAt }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Minimum token price to publish a public custom avatar item (RecFlare: 100).
        [HttpGet("/api/customAvatarItems/v1/minPriceForPublicItem")]
        public IActionResult MinPriceForPublicItem() => Content("100", "application/json");

        // "Hot" custom avatar items - none stored, so [].
        [HttpGet("/api/customAvatarItems/v1/hot")]
        public ContentResult HotCustomAvatarItems() => new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };

        // Legacy custom-item current saves - an empty map (nothing stores custom items).
        [HttpGet("/api/customAvatarItems/GetCustomAvatarItemCurrentSavesForLegacyAvatarItems")]
        public IActionResult LegacyCustomItemSaves() => Ok(new { customAvatarItemSavesByAvatarItemDesc = new { } });

        // Store ad carousel - one placeholder banner (RecFlare's ad-carousel-items.json).
        [HttpGet("/api/storefronts/v1/adcarouselitems")]
        public IActionResult AdCarouselItems()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Ok(new[]
            {
                new { AdCarouselItemId = 1, Description = "No description.", ImageName = "AdCarouselItem.png", PurchasableItemIds = Array.Empty<int>(), Title = "Lorem ipsum" }
            });
        }

        // Bulk UGC purchasables - none, so []. The client POSTs this one (a body of ids, per the client's own request table); it was GET-only, so
        // every call answered 405 (49 in a day).
        [HttpGet("/api/ugcPurchasables/v1/items/bulk")]
        [HttpPost("/api/ugcPurchasables/v1/items/bulk")]
        public ContentResult UgcPurchasablesBulk() => new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };

        // The ids of a bulk lookup. The 2023 client GETs with repeated ?id=; the 2025 client POSTs a
        // form body Ids=1,2,3 (reference recflare parseFormIds: Ids / ids, comma-separated or
        // repeated). These routes were GET-only, so every 2025 client got 405 and profile cards /
        // lists never learned anyone's level or cheers. JSON {Ids:[...]} is taken too.
        private List<ulong> BulkIds(List<ulong>? queryIds)
        {
            var ids = new List<ulong>(queryIds ?? new List<ulong>());
            try
            {
                if (Request.HasFormContentType)
                {
                    // form keys are case-insensitive: "Ids" and "ids" are ONE field - read each key once
                    foreach (var key in Request.Form.Keys.Where(k => k.Equals("ids", StringComparison.OrdinalIgnoreCase) || k.Equals("id", StringComparison.OrdinalIgnoreCase)))
                        foreach (var v in Request.Form[key])
                            foreach (var part in (v ?? "").Split(','))
                                if (ulong.TryParse(part.Trim(), out var n)) ids.Add(n);
                }
                else if ((Request.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(Request.Body);
                    var tok = Newtonsoft.Json.Linq.JToken.Parse(reader.ReadToEndAsync().GetAwaiter().GetResult());
                    var arr = tok is Newtonsoft.Json.Linq.JArray a ? a : (tok["Ids"] ?? tok["ids"]) as Newtonsoft.Json.Linq.JArray;
                    if (arr != null) foreach (var t in arr) if (ulong.TryParse(t.ToString(), out var n)) ids.Add(n);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[bulk] could not read ids: {ex.Message}"); }
            return ids;
        }

        [HttpGet("/api/players/v2/progression/bulk")]
        [HttpGet("/api/players/v1/progression/bulk")]   // older clients call v1
        [HttpGet("/api/v1/progression/bulk")]           // and a legacy path
        [HttpPost("/api/players/v2/progression/bulk")]
        [HttpPost("/api/players/v1/progression/bulk")]
        [HttpPost("/api/v1/progression/bulk")]
        public ContentResult BulkPlayersProgression([FromQuery] List<ulong> id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            id = BulkIds(id);

            if (id == null || id.Count == 0)
                return new ContentResult()
                {
                    Content = "[]",
                    ContentType = "application/json",
                    StatusCode = 200
                };

            var result = id.Select(Progress.GetPlayerLevel);
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(result),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // A single player's level + XP - what the profile panel reads when it opens one person
        // (the People page uses the bulk form above). Only the bulk route existed, so this 404'd
        // on every profile open, and the client left the level/progress area of the profile
        // blank. Same { PlayerId, Level, XP } shape as one bulk entry. v1 is what the reference
        // exposes; v2 is aliased for safety.
        // The client polls this for the "XP earned today" display. It was 404 (matched the {id}
        // route with id="xpEarnedToday", which can't bind to ulong), spamming the log. Literal
        // segments win over the {id} parameter route, so this handles it. We don't track a per-day
        // XP total, so report 0 earned today.
        [HttpGet("/api/players/v1/progression/xpEarnedToday")]
        [HttpGet("/api/players/v2/progression/xpEarnedToday")]
        public ContentResult XpEarnedToday()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult { Content = "0", ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("/api/players/v1/progression/{id}")]
        [HttpGet("/api/players/v2/progression/{id}")]
        public ContentResult SinglePlayerProgression(ulong id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var prog = Progress.GetPlayerLevel(id);

            // Mirror the reference's HubSendProgressionUpdate: pushing the caller's OWN progression
            // back on this read keeps their crown + progress bar in sync with the server's stored
            // level/XP without a relog. Only for their own (not when opening someone else's
            // profile), so it doesn't spam a push per profile view.
            if (id == account.Id)
                Notifications.FireAndForget(
                    Notifications.SendToPlayer(id, JsonConvert.SerializeObject(
                        WebsocketEvents.createResponse_levelupdate(id, prog.Level, prog.XP))),
                    "progression self-sync");

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(prog),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/announcement/v1/get")]
        public ContentResult GetAnnouncements()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Equipping a skin. The client sends the full entry for the prefab it just
        // changed, e.g. [{"PrefabName":"[ShareCamera]","ModificationGuid":"48c97ace-...",
        // "Favorited":true,...}]. This route did not exist, so the POST 404'd and the
        // choice was lost at logout. Persist it and getUnlocked below replays it.
        [HttpPost("/api/equipment/v1/update")]
        public IActionResult UpdateEquipment([FromBody] List<EquipmentDB.EquippedItem>? items)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // A skin the player does not own (not bought from a store, not given by an admin) is
            // never stored, so it cannot come back at the next login.
            var mayUse = Campus_live_rewrite.api.server.SkinOwnership.CheckerFor(account.Id);
            if (mayUse != null && items != null)
            {
                var stolen = items.Where(i => i != null && i.Favorited && !mayUse(i.PrefabName, i.ModificationGuid)).ToList();
                if (stolen.Count > 0)
                {
                    items = items.Except(stolen).ToList();
                    string what = string.Join(", ", stolen.Select(i => $"{i.PrefabName} {i.FriendlyName ?? i.ModificationGuid}"));
                    Console.WriteLine($"[equipment] {account.Id} tried to equip {stolen.Count} skin(s) they do not own - not saved: {what}");
                    // The locker only ever lists skins the player owns (getUnlocked is owned-only),
                    // so equipping one they do not own means a modified client. Devs never get here
                    // (CheckerFor is null for the dev locker).
                    Campus_live_rewrite.api.server.SkinOwnership.BanForUnownedSkin(account.Id, what);
                    return StatusCode(403, JsonConvert.SerializeObject(new { success = false, error = "You do not own that skin." }));
                }
            }

            var loadout = EquipmentDB.Update(account.Id, items);

            foreach (var i in items ?? new List<EquipmentDB.EquippedItem>())
                Console.WriteLine($"[equipment] {account.Id} {(i.Favorited ? "EQUIPPED" : "cleared ")} " +
                                  $"{i.PrefabName} -> {i.FriendlyName} ({i.ModificationGuid})");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(loadout),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/equipment/v2/getUnlocked")]
        public IActionResult GetUnlockedEquipment()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string path = Path.Combine(
                Environment.CurrentDirectory,
                "Data",
                "APIS",
                "items",
                "Equipments.json"
            );

            if (!System.IO.File.Exists(path))
                return Ok(Array.Empty<object>());

            // This response is now PER PLAYER, so it must never be cached.
            //
            // The no-saved-loadout branch used to answer with PhysicalFile, which stamps an
            // ETag and Last-Modified from Equipments.json - a file that is identical for
            // everyone. The URL carries no account id either, so after switching accounts
            // the client could reuse another account's cached copy and show that account's
            // equipped skins. Switching away and back was exactly the case that "took the
            // skin off".
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";

            var saved = EquipmentDB.Get(account.Id);
            // A saved choice of a skin they no longer own (equipped back when every skin was
            // listed) is dropped, not replayed - the tool goes back to its default look.
            var mayUse = Campus_live_rewrite.api.server.SkinOwnership.CheckerFor(account.Id);
            if (mayUse != null)
                saved = saved.Where(i => mayUse(i.PrefabName, i.ModificationGuid)).ToList();

            // Replay the player's own choices over THEIR catalog. Skins are no longer unlocked for
            // everyone: the dev locker still gets every skin, everyone else gets what they own
            // (PlayerDB.OwnedEquipmentSkins - filled by purchases and admin gifts) and otherwise
            // the tools' default looks. The 187 skin rows in the Watch store are how they get more.
            try
            {
                var catalog = JArray.Parse(System.IO.File.ReadAllText(path));

                if (!ConfigCampus.SkinsForEveryone && !PlayerDB.HasDevLocker(account.Id))
                {
                    var owned = PlayerDB.OwnedEquipmentSkins(account.Id);
                    int before = catalog.Count;
                    if (ConfigCampus.ListUnownedSkinsMasked)
                    {
                        // Every skin stays in the list so a box / a friend's tool can resolve it;
                        // the ones not owned are masked off every platform (PlatformMask 0).
                        int ownedCount = 0;
                        foreach (var e in catalog)
                        {
                            bool has = owned.Contains(PlayerDB.EquipmentSkinKey((string?)e["PrefabName"], (string?)e["ModificationGuid"]));
                            e["PlatformMask"] = has ? -1 : 0;
                            if (has) ownedCount++;
                        }
                        Console.WriteLine($"[skins] {account.Id} owns {ownedCount} of {before} equipment skin(s) (rest masked)");
                    }
                    else
                    {
                        catalog = new JArray(catalog.Where(e =>
                            owned.Contains(PlayerDB.EquipmentSkinKey((string?)e["PrefabName"], (string?)e["ModificationGuid"]))));
                        Console.WriteLine($"[skins] {account.Id} owns {catalog.Count} of {before} equipment skin(s)");
                    }
                }

                if (saved.Count == 0)
                    return new ContentResult
                    {
                        Content = catalog.ToString(Newtonsoft.Json.Formatting.None),
                        ContentType = "application/json",
                        StatusCode = 200
                    };

                foreach (var item in saved)
                {
                    // Matched on the normalized key (a short-guid spelling of the same skin used to
                    // miss here and be appended below as a duplicate).
                    string itemKey = PlayerDB.EquipmentSkinKey(item.PrefabName, item.ModificationGuid);
                    var match = catalog.FirstOrDefault(e =>
                        string.Equals(PlayerDB.EquipmentSkinKey((string?)e["PrefabName"], (string?)e["ModificationGuid"]), itemKey, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        // Equipped something the catalog does not list - carry it anyway
                        // rather than silently dropping the player's choice.
                        catalog.Add(JObject.FromObject(item));
                        continue;
                    }

                    match["Favorited"] = item.Favorited;
                    if (!string.IsNullOrEmpty(item.ThumbnailImageName))
                        match["ThumbnailImageName"] = item.ThumbnailImageName;
                }

                return new ContentResult
                {
                    Content = catalog.ToString(Newtonsoft.Json.Formatting.None),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                // A bad catalog must not cost the player their equipment list.
                Console.WriteLine($"[equipment] could not merge saved loadout for {account.Id}: {ex.Message}");
                return new ContentResult
                {
                    Content = System.IO.File.ReadAllText(path),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
        }

        [HttpGet("/api/consumables/v2/getUnlocked")]
        public IActionResult GetUnlockedConsumables()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string path = Path.Combine(
                Environment.CurrentDirectory,
                "Data",
                "APIS",
                "items",
                "Consumables.json"
            );

            if (!System.IO.File.Exists(path))
                return Ok(Array.Empty<object>());

            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";

            // Consumables.json is the CATALOG (540 rows, infinite counts). Everyone used to get
            // all of it. Now: the dev locker keeps the whole file; everyone else sees only what
            // they own, with their real counts (see PlayerDB.OwnedConsumables / gift boxes).
            if (ConfigCampus.ConsumablesForEveryone || PlayerDB.HasDevLocker(account.Id))
                return PhysicalFile(path, "application/json");
            try
            {
                var catalog = JArray.Parse(System.IO.File.ReadAllText(path));
                var owned = PlayerDB.OwnedConsumables(account.Id);
                var mine = new JArray();
                foreach (var row in catalog.OfType<JObject>())
                {
                    string key = PlayerDB.NormalizeConsumableDesc((string?)row["ConsumableItemDesc"]);
                    if (key.Length == 0 || !owned.TryGetValue(key, out var count) || count <= 0) continue;
                    var copy = (JObject)row.DeepClone();
                    copy["Count"] = count;
                    copy["InitialCount"] = count;
                    mine.Add(copy);
                }
                Console.WriteLine($"[consumables] {account.Id} owns {mine.Count} of {catalog.Count} consumable(s)");
                return new ContentResult { Content = mine.ToString(Newtonsoft.Json.Formatting.None), ContentType = "application/json", StatusCode = 200 };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[consumables] could not filter for {account.Id}: {ex.Message}");
                return PhysicalFile(path, "application/json");
            }
        }

        [HttpGet("/api/playerReputation/v2/bulk")]
        [HttpGet("/api/playerReputation/v1/bulk")]   // older clients call v1 (same query shape)
        [HttpPost("/api/playerReputation/v2/bulk")]
        [HttpPost("/api/playerReputation/v1/bulk")]
        public ContentResult BulkPlayersReputation([FromQuery] List<ulong> id)
        {
            id = BulkIds(id);
            if (id == null || id.Count == 0)
                return new ContentResult()
                {
                    Content = "[]",
                    ContentType = "application/json",
                    StatusCode = 200
                };

            var reps = new List<Rep>();
            // One entry per REQUESTED id, in order - even for a player with no account row.
            //
            // This used to `continue` past a missing player, so the reputation list came back
            // SHORTER than the id list the client sent. The People page (Recent / Here / a
            // profile) lines reputation up against the parallel account and progression lists
            // by position; a short list slides every entry after the gap onto the wrong player,
            // which reads as profiles that half-load or show someone else's cheers. Presence
            // and progression already return one-per-id; reputation now matches, handing a
            // default rep for an id with no account instead of dropping it.
            foreach (var playerId in id)
            {
                var acc = PlayerDB.GetFullAccount(playerId);
                var rep = acc?.player?.PlayerReputation ?? new mPlayerReputation();

                reps.Add(new Rep
                {
                    AccountId = playerId,
                    IsCheerful = true,   // reference ALWAYS serves true; a stored/default false reads to the client as "not in good standing" and restricts club/room creation ("too many moderation incidents")
                    Noteriety = 0,
                    SelectedCheer = PlayerDB.EffectiveSelectedCheer(rep.SelectedCheer, playerId),
                    CheerCredit = rep.CheerCredit,
                    CheerGeneral = rep.CheerGeneral,
                    CheerHelpful = rep.CheerHelpful,
                    CheerCreative = rep.CheerCreative,
                    CheerGreatHost = rep.CheerGreatHost,
                    CheerSportsman = rep.CheerSportsman,
                    SubscriberCount = acc?.player_Extra?.sub_count ?? 0,
                    SubscribedCount = acc?.player_Extra?.SubbedTo?.Count ?? 0,
                });
            }


            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(reps),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // A single player's reputation (the cheer counters shown on their profile). Only the bulk
        // route existed, so this 404'd when a profile opened one person, leaving the cheers area
        // blank. Same per-player shape as one bulk entry; a player with no account reads back as a
        // default (all-zero) rep rather than 404ing. v1 is the reference route; v2 aliased.
        [HttpGet("/api/playerReputation/v1/{id}")]
        [HttpGet("/api/playerReputation/v2/{id}")]
        public ContentResult SinglePlayerReputation(ulong id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var acc = PlayerDB.GetFullAccount(id);
            var rep = acc?.player?.PlayerReputation ?? new mPlayerReputation();

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new Rep
                {
                    AccountId = id,
                    IsCheerful = true,   // reference ALWAYS serves true; a stored/default false reads to the client as "not in good standing" and restricts club/room creation ("too many moderation incidents")
                    Noteriety = 0,
                    SelectedCheer = PlayerDB.EffectiveSelectedCheer(rep.SelectedCheer, id),
                    CheerCredit = rep.CheerCredit,
                    CheerGeneral = rep.CheerGeneral,
                    CheerHelpful = rep.CheerHelpful,
                    CheerCreative = rep.CheerCreative,
                    CheerGreatHost = rep.CheerGreatHost,
                    CheerSportsman = rep.CheerSportsman,
                    SubscriberCount = acc?.player_Extra?.sub_count ?? 0,
                    SubscribedCount = acc?.player_Extra?.SubbedTo?.Count ?? 0,
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/avatar/v1/defaultbaseavataritems")]
        public ContentResult DefaultBaseAvatarItems()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Keep this narrow. `defaultbaseavataritems` = the BASE body templates (mannequin
            // forms the client uses when building an avatar), NOT the full inventory. Returning
            // the whole avatar_items.json here would tell the client every hat/gun/shirt is a
            // base body template and break the customization pipeline. ConfigCampus ships the
            // two correct UGC base templates (UGCTee_Shirt + UGCPulloverHoodie_Shirt).
            return new ContentResult()
            {
                Content = ConfigCampus.DefaultBaseAvatarItems,
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The client POSTs this with a JSON body {AvatarItemDescriptions:[...]} (wire-shapes:
        // avatar-lockeditems-bulk); GET-only answered 405 and the wardrobe never learned which
        // items are locked. /lockeditems (no /bulk) is the same answer for the whole account.
        [HttpGet("/api/avatar/v1/lockeditems")]
        [HttpPost("/api/avatar/v1/lockeditems")]
        public ContentResult GetLockedItems()
        {
            return new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("/api/avatar/v1/lockeditems/bulk")]
        [HttpPost("/api/avatar/v1/lockeditems/bulk")]
        public ContentResult GetLockedItemsBulk()
        {
            // Nothing is locked - everyone has every avatar item and equipment skin. Returning
            // an empty BARE ARRAY here means the client's wardrobe UI never draws a padlock overlay
            // and every item is freely equippable. (The request body is deliberately not read.)
            return new ContentResult
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/config/v1/backtrace")]
        public ContentResult Backtrace()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    ReportBudget = 0,
                    FilterType = 0,
                    SampleRate = 0.025,
                    LogLineCount = 50,
                    CaptureNativeCrashes = 1,
                    ANRThresholdMs = 0,
                    MessageCount = 1000,
                    MessageRegex = "^Cannot set the parent of the GameObject .* while its new parent|^\\>\\x2010x\\:\\x20|'LabelTheme' contains missing PaletteTheme reference on",
                    VersionRegex = ".*"
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/config/v2")]
        public ContentResult ConfigV2()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // The client renders its XP bar against this table, so it MUST be the same curve the
            // server levels players on (Progress.LEVEL_REQUIRED_XP) and it must cover every level a
            // player can reach. The old table was a made-up "RequiredXp = level + 1" that only went
            // to level 20 - so a player above 20 (e.g. Level 50) had no curve entry, the client
            // couldn't reconcile their Level with their XP, and the anti-cheat check dropped them
            // to their dorm. Now it sends the real curve, all the way to MAX_LEVEL.
            var levels = new List<LevelProgressionMap>();
            for (int i = 0; i <= Progress.MAX_LEVEL; i++)
            {
                levels.Add(new LevelProgressionMap
                {
                    Level = i,
                    RequiredXp = Progress.LEVEL_REQUIRED_XP[i],
                    // The Progress tab draws each level's reward via Lifecycle.PSF.Progression.RewardMap
                    // (GiftDropId -> image). Every level pays tokens here, so each gets a synthetic
                    // id that the map points at the token pile (see Gameconfigs.json).
                    GiftDropId = i == 0 ? (int?)null : Progress.LEVEL_REWARD_GIFTDROP_BASE + i
                });
            }

            // The daily challenges (see DailyChallenges: the old 20/21/22 were tutorial-only objectives
            // that never fire, so no challenge ever progressed).
            var dailyObjectives = Campus_live_rewrite.api.server.DailyChallenges.Sets;

            string url = $"{Request.Scheme}://{Request.Host}";

            var config = new Config
            {
                MessageOfTheDay = "Welcome to Campus live!",
                CdnBaseUri = $"{url}",
                ShareBaseUrl = $"{url}/share/{0}",
                LevelProgressionMaps = levels,
                MatchmakingParams = new MatchmakingParams
                {
                    PreferFullRoomsFrequency = 1.0,
                    PreferEmptyRoomsFrequency = 0.0
                },
                DailyObjectives = dailyObjectives,
                ServerMaintainence = new ServerMaintenance
                {
                    StartsInMinutes = 0
                },
                AutoMicMutingConfig = new AutoMicMutingConfig
                {
                    MicSpamVolumeThreshold = 10.0,
                    MicVolumeSampleInterval = 1.0,
                    MicVolumeSampleRollingWindowLength = 15.0,
                    MicSpamSamplePercentageForWarning = 100.0,
                    MicSpamSamplePercentageForWarningToEnd = 0.0,
                    MicSpamSamplePercentageForForceMute = 500.0,
                    MicSpamSamplePercentageForForceMuteToEnd = 0.0,
                    MicSpamWarningStateVolumeMultiplier = 10.0
                },
                StorefrontConfig = new StorefrontConfig
                {
                    MinPlayerLevelForGifting = 0
                },
                RoomKeyConfig = new RoomKeyConfig
                {
                    MaxKeysPerRoom = 100
                },
                RoomCurrencyConfig = new RoomCurrencyConfig
                {
                    AwardCurrencyCooldownSeconds = 10.0
                },
                ConfigTable = new List<ConfigTableEntry>
                {
                    new ConfigTableEntry { Key = "Gift.DropChance", Value = "1" },
                    new ConfigTableEntry { Key = "Gift.XP", Value = "0.5" }
                },
                PhotonConfig = new PhotonConfig
                {
                    CloudRegion = "us",
                    CrcCheckEnabled = false,
                    EnableServerTracingAfterDisconnect = false
                }
            };

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(config, Formatting.None),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Gift drop stores. Each store is a captured rec.net response saved under
        // Data/APIS/GiftDropStores/{StorefrontType}.json:
        //
        //     1.json    Laser Tag   40 items   pays in LaserTagTickets  (CurrencyType 1)
        //     3.json    Watch      968 items   pays in RecCenterTokens  (CurrencyType 2)
        //   100.json    Isle        22 items   pays in LostSkullsGold   (CurrencyType 100)
        //   101.json    Crescendo   15 items   pays in DraculaSilver    (CurrencyType 101)
        //   102.json    Crimson     24 items   pays in RecCenterTokens  (CurrencyType 2)
        //
        // The {StoreId} in the URL IS the StorefrontType (see Classes/StorefrontType.cs).
        // Drop another captured store into that folder named after its type and it is served
        // with no code change.
        [HttpGet("/api/storefronts/v3/giftdropstore/{StoreId}")]
        public IActionResult GetGiftdropStore(int StoreId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string dir = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "GiftDropStores");
            string specific = Path.Combine(dir, $"{StoreId}.json");

            string name = System.Enum.IsDefined(typeof(Campus_live_rewrite.api.server.Classes.StorefrontType), StoreId)
                ? ((Campus_live_rewrite.api.server.Classes.StorefrontType)StoreId).ToString()
                : $"Unknown({StoreId})";

            // The Watch store is GENERATED for this client (reference sf3-2025.json): the captured
            // rows with no avatar item behind them, plus one row per sellable catalog item under a
            // catalog id from 10000 up. See StoreGenerator for why the captured file on its own
            // showed the wrong item on click and sold a third of the catalog.
            // Wishlist (1300) is per player: the rows of the items they hearted, looked up in the
            // Watch store first (ids there are what the store page hands out), then the other
            // catalogs. Never a file.
            if (StoreId == (int)Campus_live_rewrite.api.server.Classes.StorefrontType.Wishlist)
            {
                ulong who = account.Id;
                foreach (var k in new[] { "accountId", "playerId", "id" })
                    if (ulong.TryParse(Request.Query[k], out var q) && q > 0) { who = q; break; }
                var wanted = WishlistDB.For(who).Select(r => r.PurchasableItemId).Distinct().ToList();
                // Only what a store actually sells for tokens (StoreServe): a wishlist sits on the
                // player's profile, and profile buys are token-only. Watch rows first.
                var rows = new JArray();
                foreach (int pid in wanted)
                {
                    var hit = Campus_live_rewrite.api.server.StoreServe.FindTokenRow(pid);
                    if (hit == null) continue;
                    var c = (JObject)hit.Value.row.DeepClone();
                    c["CanBeGifted"] = true;
                    rows.Add(c);
                }
                Console.WriteLine($"[gift] giftdropstore/1300 (Wishlist) for {who} -> {rows.Count}/{wanted.Count} item(s)");
                return new ContentResult
                {
                    Content = new JObject
                    {
                        ["NewUntil"] = null,
                        ["NextUpdate"] = "2222-02-22T22:22:00Z",
                        ["StoreItems"] = rows,
                        ["StorefrontName"] = "Wishlist",
                        ["StorefrontType"] = StoreId,
                        ["SubscriberDiscountPercent"] = 10
                    }.ToString(Newtonsoft.Json.Formatting.None),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            // Watch (generated), a store file, or a store stocked only from the admin panel - as
            // StoreServe builds it: base stock minus the excluded items, admin edits on top. The
            // purchase check reads the same object, so what is shown is exactly what is buyable.
            // A storefront with no stock serves an EMPTY store - never someone else's stock (the
            // 2023 server fell back to the Watch catalog for unknown ids and served all 968 Watch
            // items as Wishlist, Player_Profile, Room_Save...).
            var served = Campus_live_rewrite.api.server.StoreServe.ServedJson(StoreId);
            if (served != null)
            {
                Console.WriteLine($"[gift] giftdropstore/{StoreId} ({name}) -> " +
                    (StoreId == Campus_live_rewrite.api.server.StoreGenerator.WatchStorefront ? "generated Watch store"
                     : System.IO.File.Exists(specific) ? $"{StoreId}.json" : "admin-stocked"));
                return new ContentResult { Content = served, ContentType = "application/json", StatusCode = 200 };
            }
            Console.WriteLine($"[gift] giftdropstore/{StoreId} ({name}) -> empty (no {StoreId}.json)");
            return Ok(new
            {
                NewUntil = (string?)null,
                NextUpdate = "2222-02-22T22:22:00Z",
                StoreItems = Array.Empty<object>(),
                StorefrontName = (string?)null,
                StorefrontType = StoreId,
                SubscriberDiscountPercent = 10
            });
        }

        // Buy (or gift) a store item. Ported from the 2023 Campus server
        // (D:\Puck\Campus-Server, StoreController.BuyItem), which had this working.
        //
        // Real spend + delivery: parses the client's body
        //   { PurchasableItemId, CurrencyType, StorefrontType, RequestedPrice,
        //     Gift: { ToPlayerId, Message, Anonymous, GiftContext } }
        // charges the buyer, and delivers to the gift target if present, else the buyer.
        //
        // ONE DELIBERATE DIFFERENCE from 2023: no AlreadyOwned check. The 2023 server
        // tracked per-player ownership and refused to sell something you already had.
        // This server hands every player the entire 3061-item avatar catalog up front
        // (see /api/avatar/v1/defaultunlocked), so that check would reject EVERY purchase.
        // What a purchase buys here is the spend and the gift-box reveal, not exclusivity.
        [HttpPost("/api/storefronts/v2/buyItem")]
        public async Task<IActionResult> BuyItem()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string body = "";
            try
            {
                using var reader = new StreamReader(HttpContext.Request.Body);
                body = await reader.ReadToEndAsync();
            }
            catch { }

            int purchasableItemId = 0;
            int currencyType = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens;
            int giftContext = 0, storefrontType = -1, requestedPrice = -1;
            ulong toPlayerId = 0;
            string? giftMessage = null;
            bool giftAnonymous = false;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                int N(System.Text.Json.JsonElement e, string k, int fallback)
                    => e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : fallback;

                purchasableItemId = N(root, "PurchasableItemId", 0);
                currencyType      = N(root, "CurrencyType", currencyType);
                storefrontType    = N(root, "StorefrontType", -1);
                // What the client thinks it is paying (the RR+ price when subscribed).
                // Logged as a cross-check only - the real price always comes from the store file.
                requestedPrice    = N(root, "RequestedPrice", -1);

                if (root.TryGetProperty("Gift", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (g.TryGetProperty("ToPlayerId", out var tp) && tp.ValueKind == System.Text.Json.JsonValueKind.Number)
                        toPlayerId = (ulong)Math.Max(0, tp.GetInt64());
                    if (g.TryGetProperty("Message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String)
                        giftMessage = m.GetString();
                    if (g.TryGetProperty("Anonymous", out var an)
                        && (an.ValueKind == System.Text.Json.JsonValueKind.True || an.ValueKind == System.Text.Json.JsonValueKind.False))
                        giftAnonymous = an.GetBoolean();
                    giftContext = N(g, "GiftContext", 0);
                }
            }
            catch { }

            // Only what the named store serves (profile / wishlist / no store: a token row of any
            // store) - StoreServe.ResolvePurchase.
            var item = Campus_live_rewrite.api.server.StoreServe.ResolvePurchase(purchasableItemId, storefrontType, out var notBuyable);

            // The price is decided HERE, from the store file. RequestedPrice is the client
            // telling us what it expects to pay; trusting it would let anything set its own price.
            int cost = item?.Cost ?? 0;
            bool subDiscount = false;
            if (item != null)
            {
                int sub = Campus_live_rewrite.api.server.StoreCatalog.SubscriberPriceFor(item);
                if (sub < cost) { cost = sub; subDiscount = true; }
            }

            string storeName = storefrontType >= 0
                ? Campus_live_rewrite.api.server.StoreCatalog.StorefrontName(storefrontType)
                : "store not specified";

            Console.WriteLine($"[buy] item {purchasableItemId} from {storeName} -> " +
                (item == null
                    ? $"not buyable: {notBuyable}"
                    : $"'{item.Name}' ({item.StoreFile}, {cost} of currency {item.Currency}" +
                      (subDiscount ? $"; RR+ price, normally {item.Cost}" : "") + ")"));

            // If these drift apart the store UI is showing players the wrong price.
            if (item != null && requestedPrice > 0 && requestedPrice != cost)
                Console.WriteLine($"[buy]   note: client expected to pay {requestedPrice}, we charge {cost}");

            int currency = item?.Currency ?? currencyType;
            ulong recipient = toPlayerId > 0 ? toPlayerId : account.Id;
            long balance = PlayerDB.GetBalance(account.Id, currency);

            if (item == null)
                return Ok(new { Result = 4, Success = false, Error = "ItemNotFound", CurrencyType = currency, Balance = balance });

            // Gifting something the recipient already owns is refused (Result 3 = AlreadyOwned).
            if (recipient != account.Id && string.IsNullOrEmpty(item.ConsumableItemDesc) && RecipientAlreadyHas(recipient, item))
            {
                Console.WriteLine($"[buy] {account.Id} -> gift {purchasableItemId} to {recipient} refused: already owned");
                return Ok(new { Result = 3, Success = false, Error = "AlreadyOwned", CurrencyType = currency, Balance = balance });
            }

            // The item's currency is the store's, not the buyer's: a Laser Tag drop is priced in
            // Laser Tag Tickets and cannot be bought with tokens, whatever CurrencyType the
            // request says. The charge already reads item.Currency; this refuses the mismatch
            // outright instead of silently charging a currency the client did not offer.
            if (currencyType != 0 && currencyType != currency)
            {
                Console.WriteLine($"[buy] @{account.Username} offered currency {currencyType} for '{item.Name}', which is sold in {currency} - refused");
                return Ok(new { Result = 5, Success = false, Error = "WrongCurrency", CurrencyType = currency, Balance = balance });
            }

            // A gift to another player arrives in the player-gift box. The client sends the
            // context it wants in the Gift block; when it sends none (0) the box came up in the
            // plain default look rather than the wrapped gift. Logged so the value the client
            // actually sends is on record.
            if (toPlayerId > 0 && toPlayerId != account.Id)
            {
                // Only token-priced items can be gifted - never a room-currency / ticket item.
                if (currency != (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens)
                    return Ok(new { Result = 4, Success = false, Error = "Only items sold for tokens can be gifted.", CurrencyType = currency, Balance = balance });
                // Gifting is a Level 15+ thing (ConfigCampus.GiftMinLevel), like live Rec Room.
                int lvl = Campus_live_rewrite.api.server.Classes.Progress.GetPlayerLevel(account.Id)?.Level ?? 1;
                if (lvl < ConfigCampus.GiftMinLevel && !PlayerDB.CheckDevFlag(account.Id))
                {
                    Console.WriteLine($"[buy] @{account.Username} (level {lvl}) tried to gift '{item.Name}' to {toPlayerId} - gifting needs level {ConfigCampus.GiftMinLevel}");
                    return Ok(new { Result = 4, Success = false, Error = $"You need to be Level {ConfigCampus.GiftMinLevel} to send gifts.", CurrencyType = currency, Balance = balance });
                }
                if (PlayerDB.GetAccountById(toPlayerId) == null)
                    return Ok(new { Result = 4, Success = false, Error = "That player does not exist.", CurrencyType = currency, Balance = balance });
                Console.WriteLine($"[buy] gift to {toPlayerId}: client GiftContext={giftContext}" + (giftContext == 0 ? $" -> using {ConfigCampus.PlayerGiftContext}" : ""));
                if (giftContext == 0) giftContext = ConfigCampus.PlayerGiftContext;
            }

            if (cost > 0 && balance < cost)
                return Ok(new { Result = 4, Success = false, Error = "InsufficientFunds", CurrencyType = currency, Balance = balance });

            // Same gift to the same player within a minute (a double tap) - see GiftGuard.
            if (toPlayerId > 0 && toPlayerId != account.Id
                && !Campus_live_rewrite.api.server.GiftGuard.TryReserve(Campus_live_rewrite.api.server.GiftGuard.Key(account.Id, toPlayerId, "pid:" + purchasableItemId)))
            {
                Console.WriteLine($"[buy] @{account.Username} gift of '{item.Name}' to {toPlayerId} refused: sent the same gift under a minute ago");
                return Ok(new { Result = 4, Success = false, Error = Campus_live_rewrite.api.server.GiftGuard.Message, CurrencyType = currency, Balance = balance });
            }

            if (cost > 0)
                PlayerDB.AddBalance(account.Id, currency, -cost);
            WishlistDB.RemoveAcquired(recipient, purchasableItemId, item.CustomAvatarItemId, item.GiftDropId);

            bool isGiftToOther = toPlayerId > 0 && toPlayerId != account.Id;
            int giftId = Campus_live_rewrite.api.server.StoreCatalog.NextPurchaseGiftId();
            ulong fromPlayerIdResponse = 0;

            // The store's descriptor is handed to the client EXACTLY as written, trailing
            // commas and all - see PendingGiftBox.AvatarItemDesc for why stripping breaks it.
            string rawDesc = item.Reward ?? "";

            var box = new PendingGiftBox
            {
                NumericId = giftId,
                GiftContext = giftContext,
                Message = isGiftToOther ? (giftMessage ?? "") : "A Gift for you <3",
                FromAccountId = giftAnonymous ? 0 : account.Id,
                Anonymous = giftAnonymous,
                AvatarItemDesc = rawDesc,
                ConsumableItemDesc = item.ConsumableItemDesc,
                AvatarItemType = item.AvatarItemType,
                GiftDropId = item.GiftDropId,
                FriendlyName = item.Name,
                Rarity = item.Rarity > 0 ? item.Rarity : ConfigCampus.DefaultGiftRarity,
                EquipmentPrefabName = item.EquipmentPrefabName,
                EquipmentModificationGuid = Campus_live_rewrite.api.server.SkinOwnership.CanonicalGuid(item.EquipmentPrefabName, item.EquipmentModificationGuid),
                CustomAvatarItemId = string.IsNullOrWhiteSpace(item.CustomAvatarItemId) ? null : item.CustomAvatarItemId,
                ThumbnailImageName = string.IsNullOrWhiteSpace(item.ThumbnailImageName) ? null : item.ThumbnailImageName
            };

            if (isGiftToOther)
            {
                fromPlayerIdResponse = giftAnonymous ? 0 : account.Id;
                PlayerDB.AddPendingGiftBoxFull(recipient, box, grantSkin: true);
                // The box drops in front of the recipient now (GiftPackageReceivedImmediate).
                Campus_live_rewrite.api.server.GiftBoxes.Push(recipient, box, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "store gift");
                Notifications.FireAndForget(
                    Notifications.RefreshAccount(recipient), "gift refresh");
            }
            else
            {
                // Buying for yourself. The item is already unlocked for everyone here, so
                // there is nothing to grant - the gift payload below is what makes the box
                // reveal in the purchase UI. Still recorded so the box survives a relog.
                PlayerDB.AddPendingGiftBoxFull(account.Id, box, grantSkin: true);
            }

            long newBalance = PlayerDB.GetBalance(account.Id, currency);
            Console.WriteLine($"[buy] @{account.Username} item {purchasableItemId} ({item.Name}) cost {cost} currency {currency} -> " +
                              $"{(isGiftToOther ? $"gift box {giftId} for {recipient}" : "self")}; bal {balance}->{newBalance}");

            // The purchase response the client waits on: the reward rides in
            // BalanceUpdates[].Data[] (the gift-drop payload the client reveals + adds),
            // plus the updated Balance so the token counter settles and the spinner finishes.
            var giftData = new List<object>
            {
                new
                {
                    Id = giftId,
                    // The store's GiftDropId - the box needs it to resolve what to display,
                    // otherwise it falls back to the "Update your game" placeholder.
                    GiftDropId = item.GiftDropId,
                    FromPlayerId = fromPlayerIdResponse,
                    ConsumableItemDesc = item.ConsumableItemDesc,
                    // Null unless the box holds an avatar item - a skin with "" / 0 here opens
                    // as an avatar item with nothing to load (StoreCatalog.AvatarTypeFor).
                    AvatarItemDesc = Campus_live_rewrite.api.server.StoreCatalog.AvatarDescFor(item),
                    AvatarItemType = Campus_live_rewrite.api.server.StoreCatalog.AvatarTypeFor(item),
                    CustomAvatarItemId = string.IsNullOrWhiteSpace(item.CustomAvatarItemId) ? null : item.CustomAvatarItemId,
                    CurrencyType = item.GdCurrencyType,
                    Currency = item.GdCurrency,
                    Xp = 0,
                    Level = 0,
                    PackageType = 0,
                    Message = isGiftToOther ? (giftMessage ?? "") : "A Gift for you <3",
                    EquipmentPrefabName = item.EquipmentPrefabName,
                    EquipmentModificationGuid = Campus_live_rewrite.api.server.SkinOwnership.CanonicalGuid(item.EquipmentPrefabName, item.EquipmentModificationGuid),
                    GiftContext = giftContext,
                    GiftRarity = item.Rarity > 0 ? item.Rarity : ConfigCampus.DefaultGiftRarity,
                    Platform = -1,
                    PlatformsToSpawnOn = -1,
                    // -2 = the account-wide bucket (reference toBalanceUpdateData). Was null.
                    BalanceType = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket
                }
            };

            return Ok(new
            {
                Result = 0,
                Success = true,
                Error = (string?)null,
                CurrencyType = currency,
                Balance = newBalance,
                BalanceUpdates = new List<object>
                {
                    new
                    {
                        UpdateResponse = 0,
                        CurrencyType = currency,
                        Balance = newBalance,
                        Data = giftData
                    }
                }
            });
        }

        // Buy a bag of storefront items at once - the shopping bag. Ported from RecFlare's
        // econ.app.ts (POST /api/items/bulkpurchase). The client posts every line (an item id,
        // DuplicateItemCount copies, the price it rendered, an optional Gift), plus the one
        // StorefrontType and one CurrencyType they all share.
        //
        // The response is NOT buyItem's. It is { Success, Error, error_id, Value } - error_id
        // lowercase because the client renames that one member - wrapping:
        //   Value = { Balance = RESULTING total, CurrencyType, Platform = -2 (the account-wide
        //             balance bucket), BalanceUpdates = one entry per REQUESTED line in order }
        // Each entry: { UpdateResponse, Data = { GiftPackage, PurchasableItemId, CustomAvatarItem } }.
        //
        // UpdateResponse codes: 0 OK, 2 NotEnoughCredit, 4 NoItemAvailable, 5 CouponNotApplicable,
        // 7 RequestedAmountNotAllowed. (6 RequestedPriceDoesNotMatch is deliberately NOT used -
        // buyItem here charges the catalog price and never rejects on RequestedPrice, and one
        // purchase path refusing what the other allows would be worse than either. We charge
        // the same min(cost, RR+ price) buyItem does and log a mismatch.)
        //
        // Whole-bag rules: one catalog resolve, one atomic debit for the total, lines taken in
        // request order while the balance covers them. Without AllowPartialSuccess, one
        // unaffordable or unbuyable line refuses the whole bag and nothing is charged. Nothing
        // sold at all is a refusal ({ Success=false, Value=null }), not an empty success. 400
        // is reserved for a request that can't be evaluated (no lines, non-spendable currency,
        // over the 200-copy cap). Count above 1 is only for consumables (they stack); anything
        // owned once is refused with RequestedAmountNotAllowed rather than charged twice.
        [HttpPost("/api/items/bulkpurchase")]
        public async Task<IActionResult> BulkPurchase()
        {
            const int OK = 0, NotEnoughCredit = 2, NoItemAvailable = 4, CouponNotApplicable = 5, RequestedAmountNotAllowed = 7;
            const int BulkCap = 200;
            const int AllPlatformsBucket = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket;

            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            string body = "";
            try { using var reader = new StreamReader(HttpContext.Request.Body); body = await reader.ReadToEndAsync(); }
            catch { }

            IActionResult Refuse(string msg, int status = 200) => new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { Success = false, Error = msg, error_id = (string?)null, Value = (object?)null }),
                ContentType = "application/json",
                StatusCode = status
            };

            System.Text.Json.JsonElement root;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                root = doc.RootElement.Clone();
            }
            catch { return Refuse("Malformed request body", 400); }

            int RootInt(string k, int fb) => root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : fb;
            bool RootBool(string k) => root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

            if (!root.TryGetProperty("PurchaseItemRequests", out var linesEl) || linesEl.ValueKind != System.Text.Json.JsonValueKind.Array || linesEl.GetArrayLength() == 0)
                return Refuse("PurchaseItemRequests must be a non-empty array", 400);

            int storefrontType = RootInt("StorefrontType", -1);
            int currencyType = RootInt("CurrencyType", -1);
            if (storefrontType < 0 || currencyType < 0)
                return Refuse("StorefrontType and CurrencyType are required", 400);
            if (!Campus_live_rewrite.api.server.Classes.GameCurrency.IsSpendable(currencyType))
                return Refuse("Currency type is not spendable", 400);

            bool allowPartial = RootBool("AllowPartialSuccess");
            bool skipGiftBox = RootBool("BypassGiftPackages");

            // ---- resolve every line against the catalog ------------------------------------
            var resolved = new List<BulkLine>();
            foreach (var line in linesEl.EnumerateArray())
            {
                var bl = new BulkLine();
                if (line.TryGetProperty("ItemPurchaseMethodId", out var mid) && mid.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (mid.TryGetProperty("NumberId", out var nid) && nid.ValueKind == System.Text.Json.JsonValueKind.Number)
                        bl.PurchasableItemId = nid.GetInt32();
                    if (mid.TryGetProperty("Type", out var ty) && ty.ValueKind == System.Text.Json.JsonValueKind.Number)
                        bl.MethodType = ty.GetInt32();
                    if (mid.TryGetProperty("Guid", out var gd) && gd.ValueKind == System.Text.Json.JsonValueKind.String)
                        bl.Caid = gd.GetString();
                }
                bl.Count = line.TryGetProperty("DuplicateItemCount", out var dc) && dc.ValueKind == System.Text.Json.JsonValueKind.Number ? dc.GetInt32() : 1;
                bl.RequestedPrice = line.TryGetProperty("RequestedPrice", out var rp) && rp.ValueKind == System.Text.Json.JsonValueKind.Number ? rp.GetInt32() : -1;
                bl.HasCoupon = line.TryGetProperty("CouponConsumablePlayerMappingId", out var cc) &&
                    cc.ValueKind != System.Text.Json.JsonValueKind.Null && cc.ValueKind != System.Text.Json.JsonValueKind.Undefined;

                if (line.TryGetProperty("Gift", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (g.TryGetProperty("ToPlayerId", out var tp) && tp.ValueKind == System.Text.Json.JsonValueKind.Number)
                        bl.GiftTo = (ulong)Math.Max(0, tp.GetInt64());
                    if (g.TryGetProperty("Message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String)
                        bl.GiftMessage = m.GetString();
                    if (g.TryGetProperty("Anonymous", out var an) && an.ValueKind == System.Text.Json.JsonValueKind.True)
                        bl.GiftAnonymous = true;
                    if (g.TryGetProperty("GiftContext", out var gc) && gc.ValueKind == System.Text.Json.JsonValueKind.Number)
                        bl.GiftContext = gc.GetInt32();
                }

                // A guid id is a UGC item (Type 1): sold at its creator's price, in tokens, once.
                // These were refused as "not carried", so nobody could buy custom clothing.
                if (bl.MethodType != 0 && !string.IsNullOrEmpty(bl.Caid))
                {
                    var rec = CustomItemsCache.GetRec(bl.Caid);
                    if (rec == null || rec.Accessibility == 0) { bl.Code = NoItemAvailable; resolved.Add(bl); continue; }
                    // Exclusive (admin-gift only) items are never sold, to anyone, even if a client asks.
                    if (rec.AdminOnly)
                    {
                        Console.WriteLine($"[bulkbuy] @{account.Username} tried to buy admin-only '{rec.Name}' ({rec.Id}) - refused");
                        bl.Code = NoItemAvailable; resolved.Add(bl); continue;
                    }
                    if (bl.HasCoupon) { bl.Code = CouponNotApplicable; resolved.Add(bl); continue; }
                    if (bl.Count != 1) { bl.Code = RequestedAmountNotAllowed; resolved.Add(bl); continue; }
                    if (currencyType != (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens) { bl.Code = NoItemAvailable; resolved.Add(bl); continue; }
                    ulong owner = bl.GiftTo > 0 ? bl.GiftTo : account.Id;
                    if (rec.Creator == owner || PlayerDB.OwnsCustomItem(owner, rec.Id)) { bl.Code = 3; resolved.Add(bl); continue; }   // AlreadyOwned
                    bl.Ugc = rec;
                    bl.UnitPrice = rec.Price > 0 ? Math.Max(Campus_live_rewrite.api.server.StoreGenerator.DefaultPrice, rec.Price) : 0;   // clothing floor
                    if (bl.RequestedPrice > 0 && bl.RequestedPrice != bl.UnitPrice)
                        Console.WriteLine($"[bulkbuy] ugc {rec.Id}: client expected {bl.RequestedPrice}, charging {bl.UnitPrice}");
                    bl.Buyable = true;
                    resolved.Add(bl);
                    continue;
                }
                if (bl.MethodType != 0 || bl.PurchasableItemId == 0) { bl.Code = NoItemAvailable; resolved.Add(bl); continue; }
                if (bl.HasCoupon) { bl.Code = CouponNotApplicable; resolved.Add(bl); continue; }
                if (bl.Count < 1) { bl.Code = RequestedAmountNotAllowed; resolved.Add(bl); continue; }

                var item = Campus_live_rewrite.api.server.StoreServe.ResolvePurchase(bl.PurchasableItemId, storefrontType, out var notBuyable);
                if (item == null)
                {
                    Console.WriteLine($"[bulkbuy] {account.Id} item {bl.PurchasableItemId} from {Campus_live_rewrite.api.server.StoreCatalog.StorefrontName(storefrontType)}: not buyable - {notBuyable}");
                    bl.Code = NoItemAvailable; resolved.Add(bl); continue;
                }
                // The whole basket is paid in the request's CurrencyType, so an item priced in any
                // other currency is refused rather than sold for the same number of these.
                if (item.Currency != currencyType)
                {
                    Console.WriteLine($"[bulkbuy] {account.Id} item {bl.PurchasableItemId}: priced in currency {item.Currency}, basket pays {currencyType} - refused");
                    bl.Code = NoItemAvailable; resolved.Add(bl); continue;
                }

                bool isConsumable = !string.IsNullOrEmpty(item.ConsumableItemDesc);
                if (bl.Count > 1 && !isConsumable) { bl.Code = RequestedAmountNotAllowed; resolved.Add(bl); continue; }

                // A GIFT of something the recipient already owns (or already has waiting in a
                // box) is refused as AlreadyOwned - the buyer's tokens would only buy a duplicate
                // they can't use. Buying for yourself is unchanged (the reveal is the purchase).
                if (bl.GiftTo > 0 && bl.GiftTo != account.Id && !isConsumable && RecipientAlreadyHas(bl.GiftTo, item))
                {
                    Console.WriteLine($"[bulkbuy] {account.Id} -> gift {bl.PurchasableItemId} to {bl.GiftTo} refused: already owned");
                    bl.Code = 3; resolved.Add(bl); continue;   // AlreadyOwned
                }

                // Price: the catalog decides, same as buyItem (min of cost and the RR+ price).
                int cost = item.Cost;
                int sub = Campus_live_rewrite.api.server.StoreCatalog.SubscriberPriceFor(item);
                if (sub < cost) cost = sub;
                if (bl.RequestedPrice > 0 && bl.RequestedPrice != cost)
                    Console.WriteLine($"[bulkbuy] item {bl.PurchasableItemId}: client expected {bl.RequestedPrice}, charging {cost}");

                bl.Item = item;
                bl.UnitPrice = cost;
                bl.Buyable = true;
                resolved.Add(bl);
            }

            var buyable = resolved.Where(l => l.Buyable).ToList();
            int totalCopies = buyable.Sum(l => l.Count);
            if (totalCopies > BulkCap)
                return Refuse($"A bulk purchase is capped at {BulkCap} items", 400);

            var firstFailure = resolved.FirstOrDefault(l => !l.Buyable);
            if (!allowPartial && firstFailure != null)
                return Refuse(BulkFailureMessage(firstFailure.Code));

            // Gift recipients must exist, or the buyer pays and the box is stranded.
            foreach (var to in buyable.Where(l => l.GiftTo > 0 && l.GiftTo != account.Id).Select(l => l.GiftTo).Distinct())
                if (PlayerDB.GetFullAccount(to)?.player == null)
                    return Refuse("No such player to gift to", 404);
            if (buyable.Any(l => l.GiftTo > 0 && l.GiftTo != account.Id))
            {
                if (currencyType != (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens)
                    return Refuse("Only items sold for tokens can be gifted");
                int lvl = Campus_live_rewrite.api.server.Classes.Progress.GetPlayerLevel(account.Id)?.Level ?? 1;
                if (lvl < ConfigCampus.GiftMinLevel && !PlayerDB.CheckDevFlag(account.Id))
                    return Refuse($"You need to be Level {ConfigCampus.GiftMinLevel} to send gifts");
            }

            // Affordability, in request order, against the CURRENT balance for this currency.
            long balance = PlayerDB.GetBalance(account.Id, currencyType);
            var bought = new HashSet<BulkLine>();
            long total = 0;
            int cooled = 0;
            foreach (var l in buyable)
            {
                long lineCost = (long)l.UnitPrice * l.Count;
                if (total + lineCost > balance) continue;
                // The same gift to the same player again within a minute: an accidental double
                // tap. Reserved here, before anything is charged, so two requests can't both pass.
                if (l.GiftTo > 0 && l.GiftTo != account.Id)
                {
                    string key = Campus_live_rewrite.api.server.GiftGuard.Key(account.Id, l.GiftTo, l.Ugc != null ? "ugc:" + l.Ugc.Id : "pid:" + l.PurchasableItemId);
                    if (!Campus_live_rewrite.api.server.GiftGuard.TryReserve(key))
                    {
                        Console.WriteLine($"[bulkbuy] @{account.Username} gift of {(l.Ugc != null ? l.Ugc.Name : l.PurchasableItemId.ToString())} to {l.GiftTo} refused: sent the same gift under a minute ago");
                        cooled++;
                        continue;
                    }
                    l.GiftKey = key;
                }
                total += lineCost;
                bought.Add(l);
            }

            if ((!allowPartial && bought.Count != buyable.Count) || bought.Count == 0)
            {
                foreach (var l in bought) Campus_live_rewrite.api.server.GiftGuard.Release(l.GiftKey);
                if (cooled > 0) return Refuse(Campus_live_rewrite.api.server.GiftGuard.Message);
                if (bought.Count == 0)
                    return Refuse(firstFailure != null ? BulkFailureMessage(firstFailure.Code) : "Insufficient balance");
                return Refuse("Insufficient balance");
            }

            // One atomic debit for the whole bag.
            if (total > 0) PlayerDB.AddBalance(account.Id, currencyType, -total);

            // Grant what sold and keep each line's box for the response.
            foreach (var l in bought)
            {
                ulong recipient = l.GiftTo > 0 ? l.GiftTo : account.Id;
                bool giftToOther = recipient != account.Id;
                l.GiftId = Campus_live_rewrite.api.server.StoreCatalog.NextPurchaseGiftId();
                WishlistDB.RemoveAcquired(recipient, l.Ugc != null ? 0 : l.PurchasableItemId, l.Ugc?.Id ?? l.Item?.CustomAvatarItemId, l.Item?.GiftDropId ?? 0);

                // UGC: ownership, the creator's cut, and (for a gift) a box carrying the item.
                if (l.Ugc != null)
                {
                    PlayerDB.GrantCustomItem(recipient, l.Ugc.Id);
                    EarningsDB.Record(l.Ugc.Creator, account.Id, "ugc", l.Ugc.Id, l.Ugc.Name, currencyType, l.UnitPrice);
                    Console.WriteLine($"[bulkbuy] @{account.Username} bought UGC '{l.Ugc.Name}' ({l.Ugc.Id}) for {l.UnitPrice} -> {(giftToOther ? $"gift to {recipient}" : "self")}");
                    if (giftToOther && !skipGiftBox)
                    {
                        var ubox = new PendingGiftBox
                        {
                            NumericId = l.GiftId,
                            // Drawn like a store gift: the client sends GiftContext -1 for a UGC gift
                            // and custom items carry no rarity, and that pair drew a blank WHITE box
                            // where a store gift is the blue one (context 500, rarity 50).
                            GiftContext = l.GiftContext > 0 ? l.GiftContext : ConfigCampus.StoreGiftContext,
                            Message = l.GiftMessage ?? "",
                            FromAccountId = l.GiftAnonymous ? 0 : account.Id,
                            Anonymous = l.GiftAnonymous,
                            AvatarItemDesc = "",
                            CustomAvatarItemId = l.Ugc.Id,
                            FriendlyName = l.Ugc.Name,
                            Rarity = ConfigCampus.DefaultGiftRarity
                        };
                        PlayerDB.AddPendingGiftBoxFull(recipient, ubox);
                        Campus_live_rewrite.api.server.GiftBoxes.Push(recipient, ubox, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "ugc gift");
                    }
                    continue;
                }

                if (!skipGiftBox)
                {
                    var box = new PendingGiftBox
                    {
                        NumericId = l.GiftId,
                        GiftContext = l.GiftContext,
                        Message = giftToOther ? (l.GiftMessage ?? "") : "A Gift for you <3",
                        FromAccountId = l.GiftAnonymous ? 0 : account.Id,
                        Anonymous = l.GiftAnonymous,
                        AvatarItemDesc = l.Item!.Reward ?? "",
                        ConsumableItemDesc = l.Item.ConsumableItemDesc,
                        AvatarItemType = l.Item.AvatarItemType,
                        GiftDropId = l.Item.GiftDropId,
                        FriendlyName = l.Item.Name,
                        Rarity = l.Item.Rarity > 0 ? l.Item.Rarity : ConfigCampus.DefaultGiftRarity,   // rarity 0 = blank white box
                        EquipmentPrefabName = l.Item.EquipmentPrefabName,
                        EquipmentModificationGuid = Campus_live_rewrite.api.server.SkinOwnership.CanonicalGuid(l.Item.EquipmentPrefabName, l.Item.EquipmentModificationGuid),
                        ConsumableCount = l.Item.ItemCount * Math.Max(1, l.Count),
                        CustomAvatarItemId = string.IsNullOrWhiteSpace(l.Item.CustomAvatarItemId) ? null : l.Item.CustomAvatarItemId,
                        ThumbnailImageName = string.IsNullOrWhiteSpace(l.Item.ThumbnailImageName) ? null : l.Item.ThumbnailImageName
                    };
                    PlayerDB.AddPendingGiftBoxFull(recipient, box, grantSkin: true);
                    if (giftToOther)
                    {
                        Campus_live_rewrite.api.server.GiftBoxes.Push(recipient, box, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "store gift");
                        Notifications.FireAndForget(Notifications.RefreshAccount(recipient), "gift refresh");
                    }
                }
            }

            long newBalance = PlayerDB.GetBalance(account.Id, currencyType);
            Console.WriteLine($"[bulkbuy] @{account.Username}: {bought.Count}/{buyable.Count} line(s), {totalCopies} copies, " +
                              $"spent {total} of currency {currencyType}; bal {balance}->{newBalance}");

            // One entry per REQUESTED line, in order - failures included.
            var updates = resolved.Select(l =>
            {
                if (!l.Buyable)
                    return (object)new { UpdateResponse = l.Code, Data = new { GiftPackage = (object?)null, PurchasableItemId = (int?)l.PurchasableItemId, CustomAvatarItem = (object?)null } };

                bool sold = bought.Contains(l);
                // A UGC line has no storefront item behind it - answer it before the package is built.
                if (sold && l.Ugc != null)
                    return (object)new { UpdateResponse = OK, Data = new { GiftPackage = (object?)null, PurchasableItemId = (int?)null, CustomAvatarItem = (object?)l.Ugc.ToJObject() } };
                object? pkg = (sold && !skipGiftBox && l.Item != null) ? new
                {
                    Id = l.GiftId,
                    PlayerId = l.GiftTo > 0 ? l.GiftTo : account.Id,
                    FromPlayerId = l.GiftAnonymous ? 0 : account.Id,
                    ConsumableItemDesc = l.Item!.ConsumableItemDesc,
                    AvatarItemType = Campus_live_rewrite.api.server.StoreCatalog.AvatarTypeFor(l.Item),
                    AvatarItemDesc = Campus_live_rewrite.api.server.StoreCatalog.AvatarDescFor(l.Item),
                    CustomAvatarItemId = string.IsNullOrWhiteSpace(l.Item.CustomAvatarItemId) ? null : l.Item.CustomAvatarItemId,
                    EquipmentPrefabName = l.Item.EquipmentPrefabName,
                    EquipmentModificationGuid = Campus_live_rewrite.api.server.SkinOwnership.CanonicalGuid(l.Item.EquipmentPrefabName, l.Item.EquipmentModificationGuid),
                    CurrencyType = l.Item.GdCurrencyType,
                    Currency = l.Item.GdCurrency,
                    Xp = 0,
                    GiftContext = l.GiftContext,
                    GiftRarity = l.Item.Rarity > 0 ? l.Item.Rarity : ConfigCampus.DefaultGiftRarity,
                    Message = l.GiftTo > 0 && l.GiftTo != account.Id ? (l.GiftMessage ?? "") : "A Gift for you <3",
                    Signature = Campus_live_rewrite.api.server.GiftBoxes.SignatureFor(l.GiftId,
                        l.GiftTo > 0 ? l.GiftTo : account.Id, l.GiftAnonymous ? 0 : account.Id, l.Item.Name,
                        l.GiftTo > 0 && l.GiftTo != account.Id ? (l.GiftMessage ?? "") : "A Gift for you <3", l.Item.GdCurrency, l.Item.GdCurrencyType),
                    IsSignatureValid = true,
                    Platform = -1,
                    PlatformsToSpawnOn = -1,
                    BalanceType = AllPlatformsBucket
                } : null;

                return (object)new { UpdateResponse = sold ? OK : NotEnoughCredit, Data = new { GiftPackage = pkg, PurchasableItemId = (int?)l.PurchasableItemId, CustomAvatarItem = (object?)null } };
            }).ToList();

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Error = (string?)null,
                    error_id = (string?)null,
                    Value = new
                    {
                        Balance = newBalance,          // RESULTING total, not the change
                        CurrencyType = currencyType,
                        Platform = AllPlatformsBucket, // the account-wide bucket the client sums
                        BalanceUpdates = updates
                    }
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        private sealed class BulkLine
        {
            public int PurchasableItemId;
            public int MethodType;
            public int Count = 1;
            public int RequestedPrice = -1;
            public bool HasCoupon;
            public ulong GiftTo;
            public string? GiftMessage;
            public bool GiftAnonymous;
            public int GiftContext;
            public bool Buyable;
            public int Code;                 // UpdateResponse when not buyable
            public int UnitPrice;
            public int GiftId;
            public Campus_live_rewrite.api.server.StoreCatalog.GiftDropInfo? Item;
            // A UGC (custom avatar item) line: guid-keyed, priced by the item's creator.
            public string? Caid;
            public CustomItemsCache.Rec? Ugc;
            public string? GiftKey;          // GiftGuard reservation for a gift line
        }

        /// <summary>Does this player already own (or have boxed) what a store row delivers?</summary>
        private static bool RecipientAlreadyHas(ulong playerId, Campus_live_rewrite.api.server.StoreCatalog.GiftDropInfo item)
        {
            try
            {
                string? desc = Campus_live_rewrite.api.server.StoreCatalog.AvatarDescFor(item);
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    string key = PlayerDB.NormalizeAvatarItemDesc(desc);
                    if (key.Length > 0 && PlayerDB.GetOwnedAvatarItemKeys(playerId).Contains(key)) return true;
                }
                if (!string.IsNullOrWhiteSpace(item.CustomAvatarItemId) && PlayerDB.OwnsCustomItem(playerId, item.CustomAvatarItemId)) return true;
                if (!string.IsNullOrWhiteSpace(item.EquipmentPrefabName) && !string.IsNullOrWhiteSpace(item.EquipmentModificationGuid)
                    && PlayerDB.OwnedEquipmentSkins(playerId).Contains(PlayerDB.EquipmentSkinKey(item.EquipmentPrefabName, item.EquipmentModificationGuid))) return true;
                // Already waiting in an unopened box.
                foreach (var b in PlayerDB.GetPendingGiftBoxes(playerId))
                {
                    if (!string.IsNullOrWhiteSpace(desc) && string.Equals(PlayerDB.NormalizeAvatarItemDesc(b.AvatarItemDesc), PlayerDB.NormalizeAvatarItemDesc(desc), StringComparison.OrdinalIgnoreCase)) return true;
                    if (!string.IsNullOrWhiteSpace(item.CustomAvatarItemId) && string.Equals(b.CustomAvatarItemId, item.CustomAvatarItemId, StringComparison.OrdinalIgnoreCase)) return true;
                    if (!string.IsNullOrWhiteSpace(item.EquipmentModificationGuid) && string.Equals(b.EquipmentModificationGuid, item.EquipmentModificationGuid, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[bulkbuy] ownership check for {playerId} failed: {ex.Message}"); }
            return false;
        }

        private static string BulkFailureMessage(int code) => code switch
        {
            3 => "They already own that item",
            4 => "One or more items are not available",
            5 => "Coupons are not supported",
            7 => "An item was requested in a quantity that is not allowed",
            _ => "One or more items could not be purchased"
        };

        [HttpGet("/api/challenge/v2/getCurrent")]
        public ContentResult GetCurrentChallenges()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // This week's website challenges (WeeklyChallenge) in the game's shape - the 2025 client
            // never asks, but an older one would see the same week.
            return new ContentResult()
            {
                Content = Campus_live_rewrite.api.server.WeeklyChallenge.Build(account.Id).ToString(Newtonsoft.Json.Formatting.None),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Report weekly-challenge progress.
        //
        // The 2025 client never calls this (no weekly challenge screen); the weekly challenges
        // are tracked server-side from finished games and shown on the website (WeeklyChallenge).
        // Kept as a latch-and-echo for any client that does.
        [HttpPost("/api/challenge/v2/updateProgress")]
        public IActionResult UpdateChallengeProgress([FromBody] System.Text.Json.JsonElement body)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string GetStr(string k) => body.ValueKind == System.Text.Json.JsonValueKind.Object &&
                body.TryGetProperty(k, out var v) ? v.ToString() : "";
            int GetInt(string k) => int.TryParse(GetStr(k), out var n) ? n : 0;

            int mapId = GetInt("ChallengeMapId");
            int challengeId = GetInt("ChallengeId");
            bool complete = GetStr("Complete").Equals("true", StringComparison.OrdinalIgnoreCase);

            // Latch this challenge's completion (false -> true only, within the rotation).
            bool stored = PlayerDB.RecordChallengeProgress(account.Id, mapId, challengeId, complete);

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    ChallengeMapId = GetStr("ChallengeMapId"),
                    ChallengeId = GetStr("ChallengeId"),
                    Config = GetStr("Config"),
                    Complete = stored
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/PlayerReporting/v1/voteToKickReasons")]
        public ContentResult VoteToKickReasons()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var reasons = new[]
            {
                new { Reason = "Discriminatory language", ReportCategory = 102 },
                new { Reason = "Discriminatory behavior", ReportCategory = 102 },
                new { Reason = "Threats or encouraging suicide", ReportCategory = 102 },
                new { Reason = "Toxic behavior", ReportCategory = 102 },
                new { Reason = "Sexual behavior in public", ReportCategory = 101 },
                new { Reason = "Sexual language in public", ReportCategory = 101 },
                new { Reason = "Non-consensual sexual behavior", ReportCategory = 101 },
                new { Reason = "Player in walls or floor", ReportCategory = 103 },
                new { Reason = "Friendly fire", ReportCategory = 103 },
                new { Reason = "Microphone spam", ReportCategory = 103 },
                new { Reason = "Abusing bugs or exploits", ReportCategory = 103 },
                new { Reason = "Spawn camping", ReportCategory = 103 },
                new { Reason = "Inactive in games (AFK)", ReportCategory = 6 },
                new { Reason = "Prefab swapping", ReportCategory = 6 },
                new { Reason = "Not following game rules", ReportCategory = 6 }
            };

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(reasons),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/images/v2/named")]
        public ContentResult NamedImages()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            return new ContentResult()
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Gift boxes waiting for this player. Populated by the admin panel
        // (/admin/api/gift_giftbox) and by end-of-game rewards.
        //
        // CAVEAT: the exact element shape rec.net returns here was never captured, so the
        // fields below are our own. The client may not render them as real gift boxes
        // until that shape is known - the grant itself IS stored either way, so nothing
        // is lost when the correct shape is filled in.
        // Open (consume) a gift box.
        //
        // The item was already granted when the box was created (buyItem / rewards write it
        // into the wardrobe immediately), so opening is just the cosmetic reveal: mark the box
        // opened so it stops coming back in the pending list. This route did not exist, so the
        // client's POST 404'd, the box was never marked opened, and it reappeared every time.
        //
        // ALWAYS answers the { error, success, value } envelope with 200 - even with no token,
        // a zero id, or a box already gone - because the client parses it to finish the open
        // animation. Opening someone else's box is refused (their box is not in the caller's
        // list, so nothing is consumed). Trailing slash tolerated: the client posts
        // "/gifts/consume/".
        // ONE route only. ASP.NET Core's matcher already treats "/consume" and "/consume/" as the
        // same path, so declaring BOTH templates made the trailing-slash URL the client actually
        // posts ("/gifts/consume/") match two identical endpoints -> AmbiguousMatchException -> 500.
        // The consume then never ran, the box was never marked opened, and it reappeared forever.
        [HttpPost("/api/avatar/v2/gifts/consume")]
        public IActionResult ConsumeGift([FromForm] string? Id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);

            object envelope = new { error = "", success = true, value = (object?)null };

            if (error == null && account != null && !string.IsNullOrWhiteSpace(Id))
            {
                int numericId = int.TryParse(Id, out var n) ? n : 0;
                var opened = PlayerDB.ConsumeGiftBox(account.Id, Id, numericId);
                if (opened == null)
                {
                    Console.WriteLine($"[gifts] {account.Id} tried to open {Id} - no matching unopened box");
                    // Reference: a box that is another player's is 403 (empty body); the caller's
                    // own already-opened box is a harmless success. The client's Open All walks
                    // every box it can see in the room, so a foreign one must fail fast rather
                    // than be answered "success" and then throw "Gift is missing from the list".
                    if (numericId != 0 && PlayerDB.GiftBoxBelongsToSomeoneElse(account.Id, numericId))
                        return StatusCode(403, "");
                }
                else
                {
                    Campus_live_rewrite.api.server.GiftBoxes.PayOnOpen(account.Id, opened);   // tokens land as the box opens

                    // A consumable box: ConsumableMappingAdded (70) so the item shows up in the
                    // inventory without a refetch (reference pushConsumableAdded). Avatar-item and
                    // skin boxes carry no ConsumableItemDesc and get nothing here.
                    if (!string.IsNullOrWhiteSpace(opened.ConsumableItemDesc))
                    {
                        try
                        {
                            var addedFrame = JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                            {
                                Id = ((int)WebsocketEvents.ResponseResults.ConsumableMappingAdded).ToString(),
                                Msg = new
                                {
                                    Id = opened.NumericId,
                                    ConsumableItemDesc = opened.ConsumableItemDesc,
                                    CreatedAt = DateTime.UtcNow,
                                    Count = 1,
                                    InitialCount = 0,
                                    IsActive = false,
                                    ActiveDurationMinutes = 0,
                                    IsTransferable = false
                                }
                            });
                            Campus_live_rewrite.api.server.Notifications.FireAndForget(
                                Campus_live_rewrite.api.server.Notifications.SendToPlayer(account.Id, addedFrame), "consumable added");
                        }
                        catch { }
                    }
                    // gift.manualconsumed (reference notification-types): tells the client the
                    // package is gone so the opened box despawns instead of lingering.
                    try
                    {
                        var goneFrame = JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                        {
                            Id = "gift.manualconsumed",
                            Msg = new { GiftPackageId = opened.NumericId != 0 ? (object)opened.NumericId : opened.Id }
                        });
                        Campus_live_rewrite.api.server.Notifications.FireAndForget(
                            Campus_live_rewrite.api.server.Notifications.SendToPlayer(account.Id, goneFrame), "gift consumed");
                    }
                    catch { }
                }
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(envelope),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/avatar/v2/gifts")]
        public ContentResult Gifts()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Drop boxes that carry NOTHING - no avatar item, no consumable, no equipment
            // and no gift drop to resolve one from.
            //
            // The client rejects the ENTIRE array when it meets one of these, logging
            // "Malformed Response: '[{...\"AvatarItemDesc\":\"\",\"FriendlyName\":\"\"...}]'",
            // so a handful of contentless test gifts takes out every real gift with them.
            // They can never be opened either, so nothing is lost by not sending them.
            // Retire contentless boxes so they don't linger forever. They are filtered out of the
            // response below (the client rejects the whole array if one is empty), and if left
            // unopened they keep counting toward the gift badge as "random gifts" that can never
            // be opened. Marking them opened here clears them for good.
            int cleaned = PlayerDB.MarkEmptyGiftBoxesOpened(account.Id);
            if (cleaned > 0)
                Console.WriteLine($"[gifts] {account.Id}: retired {cleaned} empty gift box(es)");

            // Give every box a numeric Id (older reward/select boxes had NumericId 0 -> guid Id ->
            // client rejects the whole array as "Malformed Response"). Backfills + persists.
            PlayerDB.EnsureGiftBoxNumericIds(account.Id);

            var boxes = PlayerDB.GetPendingGiftBoxes(account.Id);
            var usable = boxes.Where(Campus_live_rewrite.api.server.GiftBoxes.HasContent).ToList();

            if (usable.Count != boxes.Count)
                Console.WriteLine($"[gifts] {account.Id}: skipped {boxes.Count - usable.Count} empty gift box(es) " +
                                  $"the client cannot parse, sent {usable.Count}");

            // One shape for the list and the hub frames - see GiftBoxes. The box's own currency,
            // XP and thumbnail ride along; they were hardcoded 0 / absent here, which is why a
            // token box read "0 Tokens" and no box showed its XP when opened from the list.
            var pending = usable.Select(b => Campus_live_rewrite.api.server.GiftBoxes.ToGiftContent(b, account.Id)).ToList();

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(pending),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The caller's reward selections that were offered and never picked, in the same shape
        // the RewardSelectionReceived push carries - so a client that re-reads its pending
        // selections (on login, or when it opens the picker from the watch) finds the one it was
        // just pushed instead of an empty list. Was a hardcoded "[]".
        [HttpGet("/api/gamerewards/v1/pending")]
        public ContentResult PendingGameRewards()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var pending = Campus_live_rewrite.api.server.RewardSelections.PendingFor(account.Id);
            Console.WriteLine($"[gamereward] {account.Id} read pending selections: {pending.Count}");
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(pending.Select(Campus_live_rewrite.api.server.RewardSelections.ToPayload).ToList()),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // End-of-game reward request. Real client sends form-urlencoded:
        //   GiftContext={int}&Message={string}&RewardType={PostGameActivity|...}
        // GiftContext identifies which gift box (4003 Golden, etc - see GiftContextIds).
        //
        // KNOWN GAP: the exact success RESPONSE econ.rec.net returns here was not captured
        // (the reference tools only send the request and print r.text). This returns a
        // minimal success envelope so the call does not error; if the client expects the
        // granted gift-box contents echoed back to render them, that shape still needs a
        // capture or a live probe. The request IS recorded, and a currency reward is
        // credited so the reward is tangible.
        [HttpPost("/api/gamerewards/v1/request")]
        public ContentResult RequestGameReward()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Read the fields MANUALLY, not via [FromForm] int GiftContext. The client sends
            // giftContext as the ACTIVITY NAME (e.g. "giftContext=Dodgeball"), which cannot bind to
            // an int - that model-bind failure was the 400 this endpoint kept returning. Keep both a
            // numeric GiftContext (when it happens to be a number) and the raw name for the reward
            // pool lookup.
            string? RewardType = null, Message = null, giftContextRaw = null;
            if (Request.HasFormContentType)
            {
                RewardType     = Request.Form["rewardType"].ToString();
                Message        = Request.Form["Message"].ToString();
                giftContextRaw = Request.Form["giftContext"].ToString();
            }
            int GiftContext = int.TryParse(giftContextRaw, out var gcNum) ? gcNum : 0;
            string? giftContextName = string.IsNullOrWhiteSpace(giftContextRaw) ? null : giftContextRaw;

            _ = Task.Run(() => Webhook.Send(
                account.Id, "prod", "Game reward",
                $"@{account.Username} ({account.Id}) requested reward - GiftContext={GiftContext}, Type={RewardType}",
                5898927, null));

            Console.WriteLine($"[gamereward] account {account.Id} GiftContext={GiftContext} RewardType={RewardType} Message='{Message}'");

            // Amount and currency depend on the reward type (see GameRewardRules).
            var rewardType = Campus_live_rewrite.api.server.Classes.GameRewardRules.Parse(RewardType);

            // Pay in the CURRENT ROOM's currency, same rule as /storefronts/v2/balance:
            // finishing a game in Crescendo pays Silver, Isle pays Gold, and everything
            // else pays Rec Center Tokens. Falls back to the reward type's default when
            // the room is unknown.
            var rewardInst = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance;
            string? rewardRoom = rewardInst?.name;
            // A DEV ROOM (admin ticked it) is where chips are tested: the request chip there must answer every
            // press, so the once-a-day and one-a-minute pacing below is skipped - for developer accounts only,
            // because the picker's cards can carry tokens and an unpaced request would farm them. Currency rules
            // are unchanged.
            bool devRoomReward = rewardInst != null && RoomDB.IsDevRoom((ulong)rewardInst.roomId) && PlayerDB.CheckDevFlag(account.Id);
            if (devRoomReward) Console.WriteLine($"[gamereward] {account.Id} is in dev room {rewardInst!.roomId} ('{rewardRoom}') - pacing skipped");
            var roomCur = Campus_live_rewrite.api.server.Classes.GameCurrency.ForRoomName(rewardRoom);
            int RewardCurrency = (int)(roomCur ?? Campus_live_rewrite.api.server.Classes.GameRewardRules.CurrencyFor(rewardType));
            long RewardAmount = Campus_live_rewrite.api.server.Classes.GameRewardRules.AmountFor(rewardType);

            // Rec Center Tokens (and any non-room currency) are server/admin only - a player
            // reward can never mint them. When a reward would pay one, pay 0 currency but still
            // grant XP below, so levelling from play keeps working while tokens stay locked down.
            // EXCEPTION: a once-per-day reward (FirstActivityOfDay - the daily first-game bonus) is
            // rate-limited to one claim per day, so it CANNOT be farmed even in Rec Center Tokens.
            // Without this it paid 0 in any token-currency room and the daily bonus "did nothing".
            if (!Campus_live_rewrite.api.server.Classes.GameCurrency.IsPlayerGrantable(RewardCurrency)
                && !Campus_live_rewrite.api.server.Classes.GameRewardRules.IsOncePerDay(rewardType))
            {
                Console.WriteLine($"[gamereward] {account.Id} currency {RewardCurrency} not player-grantable " +
                                  $"(server/admin only) - awarding 0 currency, XP only");
                RewardAmount = 0;
            }

            // Once-per-day rewards must actually be once per day. FirstActivityOfDay is
            // requested by the client on activity, not only the first of the day, so
            // without this gate it paid out on every single request and could be farmed
            // by simply replaying the call.
            if (!devRoomReward && Campus_live_rewrite.api.server.Classes.GameRewardRules.IsOncePerDay(rewardType))
            {
                if (!PlayerDB.TryClaimDailyReward(account.Id, rewardType.ToString()))
                {
                    Console.WriteLine($"[gamereward] {account.Id} already claimed {rewardType} today - awarding nothing");
                    // No selection to show: null, not a status object (see the end of this method).
                    return new ContentResult() { Content = "null", ContentType = "application/json", StatusCode = 200 };
                }
                // The day's first: the game complete that comes with it rides through the
                // per-minute limit below (GameRewardGate).
                Campus_live_rewrite.api.server.GameRewardGate.NoteDailyAccepted(account.Id);
            }

            // Game completes: one per minute per player, the whole payout - not just the box.
            // A replayed PostGameActivity used to pay currency + XP every time.
            string limited = "";
            if (!devRoomReward && !Campus_live_rewrite.api.server.GameRewardGate.Allow(account.Id, rewardType, out limited))
            {
                Console.WriteLine($"[gamereward] {account.Id} rate-limited: {limited} - awarding nothing");
                return new ContentResult() { Content = "null", ContentType = "application/json", StatusCode = 200 };
            }

            // A finished game counts toward the website's weekly challenges (the game has no weekly
            // screen of its own). Only past the rate limit, so replays can't farm them.
            if (!devRoomReward && rewardType == Campus_live_rewrite.api.server.Classes.GameRewardType.PostGameActivity)
                Campus_live_rewrite.api.server.WeeklyChallenge.RecordGame(account.Id);

            long newTotal = PlayerDB.AddBalance(account.Id, RewardCurrency, RewardAmount);
            // The corner counter moves now, not on the next relog (reference: pushBalanceUpdate).
            Campus_live_rewrite.api.server.GiftBoxes.PushBalance(account.Id, RewardCurrency, newTotal, "gamereward");

            // Grant XP too - a game reward pays XP as well as currency, and nothing was doing it,
            // so players never levelled up. GAME_REWARD_XP (5) per claim, level-ups applied on the
            // real curve (see Progress). The currency reward already gated once-per-day rewards
            // above, so we only reach here when a reward is actually being paid.
            var xpResult = PlayerDB.AddXp(account.Id, Progress.GAME_REWARD_XP);
            if (xpResult.HasValue && xpResult.Value.LevelsGained > 0)
                Console.WriteLine($"[progression] {account.Id} reached Level {xpResult.Value.Level} " +
                                  $"(+{xpResult.Value.LevelsGained}) from a {rewardType} reward");

            // Offer the three-choice reward SELECTION (the watch picker). Mint three distinct drops
            // for this GiftContext / current room, record them, and push "RewardSelectionReceived"
            // over the notifications hub - the client shows the watch and the player picks one with
            // POST /api/gamerewards/v1/select, which is what actually grants the item. Cooldown-gated
            // (QuestRewards.TryClaimGiftCooldown) so a spammy client can't spawn a selection per call.
            // Every activity pays TOKENS for now (ConfigCampus.RewardItemsEnabled): three random
            // amounts from the 10..1000 ladder, in the ACTIVITY's own box - a Crescendo win comes
            // out of the Crescendo box (its GiftContext), an unknown activity out of the plain
            // tokens box. RewardSelections.Offer is the same path the admin panel's "Send Game
            // Reward" uses.
            Campus_live_rewrite.api.server.RewardSelections.Selection? offered = null;
            // The request gate above (GameRewardGate) already paced this request; the older
            // box-only cooldown (QuestRewards.TryClaimGiftCooldown) is not applied on top of it,
            // or the game complete the daily grace let through would pay but offer no box.
            if (Campus_live_rewrite.api.server.QuestRewards.Loaded)
            {
                offered = Campus_live_rewrite.api.server.RewardSelections.Offer(account.Id, GiftContext, giftContextName ?? rewardRoom, Message);
            }

            // The RESPONSE is the selection itself, the same object the RewardSelectionReceived
            // frame carries. The client builds its watch picker from this response body directly
            // (the hub frame is the cross-device copy): the old {success, Awarded, Balance} answer
            // deserialised into a selection with no drops - three blank "Reward" cards showing
            // "0 Tokens", RewardSelectionId 0 and a default CreatedAt that the expiry timer read
            // as long past, which is the "picker opens blank / auto-picks card 1 and closes"
            // report. Nothing offered (cooldown, daily already claimed) answers null.
            return new ContentResult()
            {
                Content = offered != null
                    ? JsonConvert.SerializeObject(Campus_live_rewrite.api.server.RewardSelections.ToPayload(offered))
                    : "null",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Claim one of the three rewards an earlier selection offered (the watch picker's pick).
        // The selection (minted + pushed by /gamerewards/v1/request) must be the caller's, unconsumed,
        // and must actually contain the claimed drop - otherwise 403, so a player can't mint a reward
        // they were never offered or redeem one twice. On success the chosen item is granted as a gift
        // box (also entering the wardrobe) plus any XP it carries, and announced over the hub as
        // GiftPackageRewardSelectionReceived ("32"). Items + XP only (tokens are server/admin).
        [HttpPost("/api/gamerewards/v1/select")]
        public ContentResult SelectGameReward()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            int selId = 0, giftDropId = 0;
            try
            {
                if (Request.HasFormContentType)
                {
                    int.TryParse(Request.Form["rewardSelectionId"], out selId);
                    int.TryParse(Request.Form["giftDropId"], out giftDropId);
                }
                else
                {
                    using var reader = new StreamReader(HttpContext.Request.Body);
                    var body = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(body);
                        selId      = (int?)jo["rewardSelectionId"] ?? (int?)jo["RewardSelectionId"] ?? 0;
                        giftDropId = (int?)jo["giftDropId"]        ?? (int?)jo["GiftDropId"]        ?? 0;
                    }
                }
            }
            catch { }

            if (giftDropId == 0)
                return new ContentResult { Content = "{\"error\":\"giftDropId is required\"}", ContentType = "application/json", StatusCode = 400 };

            var sel = selId <= 0 ? null : Campus_live_rewrite.api.server.RewardSelections.Get(selId);
            var chosen = sel?.Drops.FirstOrDefault(d => d.Id == giftDropId);
            // The client re-sends the pick (a double tap, or a retry after the box popped). A
            // selection this account already claimed answers the SAME drop again with 200 - it
            // used to 403, which the client surfaced as "Failed to consume reward".
            if (sel != null && sel.AccountId == account.Id && sel.Consumed && chosen != null)
            {
                var already = sel.ChosenDropId != 0 ? sel.Drops.FirstOrDefault(d => d.Id == sel.ChosenDropId) ?? chosen : chosen;
                Console.WriteLine($"[gamereward-select] {account.Id} re-sent selection {selId}: already claimed drop {already.Id}, answering it again");
                return new ContentResult { Content = JsonConvert.SerializeObject(Campus_live_rewrite.api.server.QuestRewards.ToDrop(already)), ContentType = "application/json", StatusCode = 200 };
            }
            if (sel == null || sel.AccountId != account.Id || sel.Consumed || chosen == null)
            {
                Console.WriteLine($"[gamereward] {account.Id} select refused: selection {selId} " +
                                  (sel == null ? "unknown (expired, or minted before a restart)" : sel.AccountId != account.Id ? "belongs to someone else" : sel.Consumed ? "already claimed" : $"does not offer drop {giftDropId}"));
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 403 };
            }

            // Consume conditionally: two racing claims mean the second one loses.
            if (!Campus_live_rewrite.api.server.RewardSelections.Consume(sel.RewardSelectionId, giftDropId))
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 403 };

            bool tokens = Campus_live_rewrite.api.server.QuestRewards.IsTokens(chosen);
            int boxXp = chosen.Xp > 0 ? chosen.Xp : Campus_live_rewrite.api.server.Classes.Progress.GAME_REWARD_XP;
            var box = new Campus_live_rewrite.api.server.Classes.db_class.PendingGiftBox
            {
                // The box is filed under the DROP's context (reference: giftContext ?? drop.Context):
                // an activity's item under the activity's box, tokens under GameRewardTokens (51)
                // so the client draws the token box and plays its sound, not the XP box's.
                GiftContext               = chosen.Context != 0 ? chosen.Context : sel.GiftContext,
                Message                   = sel.Message,
                FromAccountId             = 0,
                GiftDropId                = chosen.Id,
                FriendlyName              = tokens ? $"{chosen.Currency} Tokens" : chosen.Message,
                ThumbnailImageName        = tokens ? Campus_live_rewrite.api.server.QuestRewards.TokenThumbnail
                                            : (Campus_live_rewrite.api.server.QuestRewards.ItemThumbnail(chosen) is { Length: > 0 } th ? th : null),
                AvatarItemDesc            = chosen.AvatarItemDesc,
                ConsumableItemDesc        = chosen.ConsumableItemDesc,
                EquipmentPrefabName       = chosen.EquipmentPrefabName,
                EquipmentModificationGuid = chosen.EquipmentModificationGuid,
                Rarity                    = chosen.GiftRarity,
                CurrencyType              = chosen.Currency > 0 ? chosen.CurrencyType : 0,
                Currency                  = chosen.Currency > 0 ? chosen.Currency : 0,
                Xp                        = boxXp
            };
            try { PlayerDB.AddPendingGiftBoxFull(account.Id, box); }
            catch (Exception ex) { Console.WriteLine($"[gamereward-select] {account.Id} grant failed: {ex.Message}"); }
            // A currency drop (a duplicate converted to tokens, or Laser Tag's tickets) is paid when
            // the player OPENS the box (GiftBoxes.PayOnOpen from /gifts/consume), so the counter
            // moves with the reveal rather than silently at pick time.
            box.PayOnOpen = box.Currency > 0 && box.CurrencyType != 0;
            if (box.PayOnOpen) PlayerDB.SetGiftBoxPayOnOpen(account.Id, box);
            // Announce the gift box over the hub (GiftPackageRewardSelectionReceived = 32) so it pops
            // without waiting for the next /api/avatar/v2/gifts poll. BEFORE the XP lands: a level-up
            // box announced by AddXp should arrive after the reward that earned it.
            Campus_live_rewrite.api.server.GiftBoxes.Push(account.Id, box,
                Campus_live_rewrite.api.server.GiftBoxes.FrameRewardSelection, "GiftPackageRewardSelectionReceived");
            if (boxXp > 0) PlayerDB.AddXp(account.Id, boxXp);

            Console.WriteLine($"[gamereward-select] {account.Id} claimed drop {giftDropId} from selection {sel.RewardSelectionId}");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(Campus_live_rewrite.api.server.QuestRewards.ToDrop(chosen)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Referral / gift generation. Same request shape as the reward request; same
        // captured-response gap. Records + acknowledges.
        [HttpPost("/api/avatar/v3/gifts/generate")]
        [HttpPost("/api/avatar/v2/gifts/generate")]   // older clients call v2
        public ContentResult GenerateAvatarGift([FromForm] int GiftContext, [FromForm] string? Message, [FromForm] string? RewardType)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            Console.WriteLine($"[giftgen] account {account.Id} GiftContext={GiftContext} RewardType={RewardType}");

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    error_id = "",
                    GiftContext = GiftContext,
                    RewardType = RewardType ?? ""
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Friendotron / free-gift button. The client posts MultiRecipientFreeGiftRequestDTO as
        // JSON: { ToPlayerIds: [int], Message, GiftContext (1300 Friendotron_Gift) }. It 404'd, so
        // the machine said "Failed to Send Gift" every time. Each recipient gets a token box in
        // the Friendotron look, from the sender, paid when opened, pushed live. The client
        // enforces its own once-a-day limit; the server caps recipients per call.
        [HttpPost("/api/freegifts/v1/sendmultiple")]
        public IActionResult SendFreeGifts()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var ids = new List<ulong>();
            string message = "";
            int giftContext = 1300;
            try
            {
                using var reader = new StreamReader(HttpContext.Request.Body);
                var body = reader.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(body);
                    var arr = jo["ToPlayerIds"] as Newtonsoft.Json.Linq.JArray ?? jo["toPlayerIds"] as Newtonsoft.Json.Linq.JArray
                              ?? jo["PlayerIds"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();
                    foreach (var t in arr) { if (long.TryParse(t.ToString(), out var v) && v > 0) ids.Add((ulong)v); }
                    message = (string?)jo["Message"] ?? (string?)jo["message"] ?? "";
                    giftContext = (int?)jo["GiftContext"] ?? (int?)jo["giftContext"] ?? 1300;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[freegift] {account.Id}: bad body - {ex.Message}"); }

            ids = ids.Distinct().Take(10).ToList();
            if (ids.Count == 0)
                return new ContentResult { Content = "{\"Result\":1,\"Success\":false,\"Error\":\"No recipients\"}", ContentType = "application/json", StatusCode = 400 };

            string boxMsg = string.IsNullOrWhiteSpace(message) ? $"A free gift from @{account.Username}!" : message.Trim();
            if (boxMsg.Length > 120) boxMsg = boxMsg.Substring(0, 120);
            int sent = 0;
            foreach (var pid in ids)
            {
                if (PlayerDB.GetFullAccount(pid)?.player == null) continue;
                long tokens = Campus_live_rewrite.api.server.QuestRewards.RollTokenAmount();
                var box = new Campus_live_rewrite.api.server.Classes.db_class.PendingGiftBox
                {
                    GiftContext        = giftContext != 0 ? giftContext : 1300,
                    Message            = boxMsg,
                    FromAccountId      = account.Id,
                    FriendlyName       = $"{tokens} Tokens",
                    ThumbnailImageName = Campus_live_rewrite.api.server.QuestRewards.TokenThumbnail,
                    CurrencyType       = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens,
                    Currency           = tokens,
                    Rarity             = Campus_live_rewrite.api.server.QuestRewards.TokenRarity(tokens),
                    PayOnOpen          = true
                };
                if (!PlayerDB.AddPendingGiftBoxFull(pid, box)) continue;
                Campus_live_rewrite.api.server.GiftBoxes.Push(pid, box, Campus_live_rewrite.api.server.GiftBoxes.FrameReceivedImmediate, "friendotron gift");
                sent++;
            }
            Console.WriteLine($"[freegift] @{account.Username} ({account.Id}) sent Friendotron gifts to {sent}/{ids.Count} player(s): [{string.Join(",", ids)}]");
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { Result = 0, Success = true, Sent = sent }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/communityboard/v2/current")]
        public ContentResult CurrentCommunityBoard()
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
            player_info account = JsonConvert.DeserializeObject<player_info>(playerdata);

            string filePath = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "communityboard.json");
            string content = System.IO.File.ReadAllText(filePath);
            // The board's featured player, featured rooms and announcement come from the admin
            // panel's Featured tab (FeaturedStore / FeaturedRoomsStore) when set there; the file
            // is the fallback for anything the panel leaves blank.
            try
            {
                var jo = JObject.Parse(content);
                var fs = Campus_live_rewrite.api.server.FeaturedStore.Get();
                ulong fp = Campus_live_rewrite.api.server.DiscoveryLists.FeaturedCreator();
                if (fp != 0)
                {
                    jo["FeaturedPlayer"] ??= new JObject();
                    jo["FeaturedPlayer"]!["Id"] = fp;
                }
                var rooms = new JArray();
                foreach (var rid in Campus_live_rewrite.api.server.FeaturedRoomsStore.GetIds())
                {
                    var r = RoomDB.GetRoom(rid);
                    if (r == null || r.IsDorm) continue;
                    rooms.Add(new JObject
                    {
                        ["RoomId"] = r.RoomId, ["RoomName"] = r.Name ?? "", ["ImageName"] = r.ImageName ?? "",
                        ["IsRecRoomApproved"] = r.IsRecRoomApproved, ["ExcludeFromLists"] = false, ["ExcludeFromSearch"] = false
                    });
                }
                if (rooms.Count > 0)
                {
                    jo["FeaturedRoomGroup"] ??= new JObject { ["FeaturedRoomGroupId"] = 1, ["Name"] = "Featured Rooms", ["StartAt"] = "2020-01-01T00:00:00Z", ["EndAt"] = "9999-12-31T00:00:00Z" };
                    jo["FeaturedRoomGroup"]!["Rooms"] = rooms;
                }
                if (!string.IsNullOrWhiteSpace(fs.Announcement))
                {
                    jo["CurrentAnnouncement"] ??= new JObject();
                    jo["CurrentAnnouncement"]!["Message"] = fs.Announcement;
                    jo["CurrentAnnouncement"]!["MoreInfoUrl"] = string.IsNullOrWhiteSpace(fs.AnnouncementUrl) ? null : fs.AnnouncementUrl;
                }
                content = jo.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex) { Console.WriteLine($"[communityboard] overlay failed: {ex.Message}"); }
            return new ContentResult()
            {
                Content = content,
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/storefronts/v4/balance/{balance_type}")]
        public ContentResult StorefrontBalance(int balance_type = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            long stored = PlayerDB.GetBalance(account.Id, balance_type);

            var balance = new[]
            {
                new
                {
                    Balance = stored,
                    BalanceType = balance_type,
                    CurrencyType = balance_type,
                    // The account-wide bucket. Was 0 (SteamPurchased) while the purchase response
                    // and the 61 push said -2, so a client that bought something summed two
                    // buckets and showed the wrong total until a relog.
                    Platform = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket
                }
            };

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(balance),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Add currency. Request (from the real client / farm tools):
        //   {"CurrencyType":N,"BalanceAdds":[{"Multiplier":1,"BalanceAddType":M}]}
        // Response shape mirrors econ.rec.net: a BalanceUpdates array, one per add, each
        // with an UpdateResponse (0 = OK) and a Data block the client reads for the award.
        //
        // NOTE: this is the same endpoint the currency farm tools abuse. On this private
        // server that is acceptable (it is also how the game legitimately awards currency,
        // e.g. laser tag tickets), but it applies the add for whoever is authenticated -
        // there is no server-side rate limit. Add one here if that becomes a problem.
        // The award DEFINITION the client fetches when a balance room loads, before any checkpoint:
        //   GET /api/storefronts/v1/balanceAddType/{currency}/{type}   (was a 404, four times a run)
        // It answers the same RewardBalanceModificationDTO shape a balance add returns, with the
        // base per unit and no total. The currency is decided by the room the caller is standing
        // in, never by the URL - a client asking for Silver from the Rec Center gets no award type.
        [HttpGet("/api/storefronts/v1/balanceAddType/{currencyType}/{addType}")]
        public ContentResult BalanceAddTypeDefinition(int currencyType, int addType)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var live = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance;
            var roomCur = Campus_live_rewrite.api.server.Classes.GameCurrency.ForBalanceRoom(live?.name);
            if (roomCur == null || (int)roomCur.Value != currencyType)
            {
                Console.WriteLine($"[balance] {account.Id} asked for add type {addType} of currency {currencyType} in '{live?.name ?? "no room"}' - not that room's currency");
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    BalanceAddType   = addType,
                    BalanceInGiftBox = false,   // a bool on the wire - "expected:'true | false', actual:'0'" (Player.log)
                    BaseAward        = addType <= 0 ? 1 : addType,
                    BonusAward       = 0,
                    CurrencyType     = currencyType,
                    CurrentCount     = 0,
                    Platform         = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket,
                    RateLimit        = 0,
                    Total            = PlayerDB.GetBalance(account.Id, currencyType)
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/storefronts/v2/balance")]
        public ContentResult AddStorefrontBalance()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string body;
            using (var reader = new StreamReader(HttpContext.Request.Body))
                body = reader.ReadToEndAsync().GetAwaiter().GetResult();

            int currencyType = 0;
            long bodyInstanceId = 0, bodyRoomId = 0;
            var adds = new List<(int addType, double multiplier)>();
            try
            {
                var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                currencyType = (int?)j["CurrencyType"] ?? 0;
                // Optional client-supplied room context. Only used to CROSS-CHECK the server's
                // authoritative heartbeat instance below - never trusted on its own.
                bodyInstanceId = (long?)j["RoomInstanceId"] ?? (long?)j["roomInstanceId"] ?? 0;
                bodyRoomId     = (long?)j["RoomId"]         ?? (long?)j["roomId"]         ?? 0;
                foreach (var a in j["BalanceAdds"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                {
                    // Multiplier is a DECIMAL and it is where the score lives - a real
                    // Crescendo checkpoint sends {"BalanceAddType":1,"Multiplier":999.90}.
                    // Reading it as an int truncated the score and threw away the fraction.
                    double mult = (double?)a["Multiplier"] ?? 1d;
                    int at      = (int?)a["BalanceAddType"] ?? 0;
                    adds.Add((at, mult <= 0d ? 1d : mult));
                }
            }
            catch
            {
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 400 };
            }

            if (adds.Count == 0)
                adds.Add((0, 1));

            // Empty award set = a 200 with no updates. Nothing to mint, nothing to police.
            ContentResult NoAward() => new ContentResult
            {
                // Same DTO as a real answer (see the end of this method) - a bare BalanceUpdates is
                // what the client refuses as malformed.
                Content = JsonConvert.SerializeObject(new
                {
                    BalanceUpdates = Array.Empty<object>(),
                    Balance = PlayerDB.GetBalance(account.Id, currencyType),
                    CurrencyType = currencyType,
                    Platform = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket
                }),
                ContentType = "application/json",
                StatusCode = 200
            };

            // ---- SERVER-SIDE room + instance gate ---------------------------------------
            //
            // The award is decided entirely from the player's LIVE room instance (the heartbeat),
            // never from the request body. This is what stops "generating balance they ain't meant
            // to have": you can only mint the currency of the room you are actually standing in.
            var liveInstance = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance;

            // (1) Must be in a real room instance - "need your RoomInstanceId and RoomId".
            if (liveInstance == null || liveInstance.roomInstanceId == 0 || liveInstance.roomId == 0)
            {
                Console.WriteLine($"[balance] {account.Id} DENY - not in a live room instance (no RoomInstanceId/RoomId)");
                return NoAward();
            }

            // If the client bothered to send its own ids, they must match the server's truth.
            if ((bodyInstanceId != 0 && bodyInstanceId != liveInstance.roomInstanceId) ||
                (bodyRoomId     != 0 && bodyRoomId     != liveInstance.roomId))
            {
                Console.WriteLine($"[balance] {account.Id} DENY - body instance/room ({bodyInstanceId}/{bodyRoomId}) " +
                                  $"!= live ({liveInstance.roomInstanceId}/{liveInstance.roomId})");
                return NoAward();
            }

            // (2) Only the whitelisted balance rooms mint anything, and the room decides the
            //     currency. Everything else (incl. Rec Center Token rooms) mints nothing here.
            var roomCurrency = Campus_live_rewrite.api.server.Classes.GameCurrency.ForBalanceRoom(liveInstance.name);
            if (roomCurrency == null)
            {
                Console.WriteLine($"[balance] {account.Id} in '{liveInstance.name}' DENY - not a balance room");
                return NoAward();
            }
            currencyType = (int)roomCurrency.Value;

            // (3) Rec Center Tokens (and any non-room currency) are server/admin only.
            if (!Campus_live_rewrite.api.server.Classes.GameCurrency.IsPlayerGrantable(currencyType))
            {
                Console.WriteLine($"[balance] {account.Id} DENY - currency {currencyType} is not player-grantable");
                return NoAward();
            }

            // ---- Anti-farm guard: 1-minute rate limit + long-session cap (deny, never ban) ----
            var guard = Campus_live_rewrite.api.server.BalanceGuard.Evaluate(account.Id, liveInstance.roomInstanceId);
            if (guard.decision == Campus_live_rewrite.api.server.BalanceGuard.Decision.Deny)
            {
                Console.WriteLine($"[balance][DENY] {account.Id} - {guard.reason}");
                return NoAward();
            }

            Console.WriteLine($"[balance] {account.Id} room='{liveInstance.name}' inst={liveInstance.roomInstanceId} " +
                              $"currency={currencyType} adds=[{string.Join(", ", adds.Select(a => $"base{a.addType}x{a.multiplier}"))}]");

            var updates = new List<object>();
            long lastTotal = PlayerDB.GetBalance(account.Id, currencyType);
            foreach (var (addType, multiplier) in adds)
            {
                // How much this award is worth.
                //
                // Real client request (Crescendo checkpoint):
                //     {"BalanceAdds":[{"BalanceAddType":1,"Multiplier":999.90}],"CurrencyType":101}
                //
                // BalanceAddType is the BASE per unit (1), not the amount. Multiplier is
                // the score. So the award is base x multiplier, rounded - 1 x 999.90 = 1000
                // Silver for that checkpoint.
                //
                // Clamped per currency: a Crescendo checkpoint is worth at most 30,000
                // Silver (150,000 over five), so a wrong or forged Multiplier cannot mint
                // more than a single checkpoint is worth.
                long baseAward = addType;
                long bonus     = 0;
                long raw       = (long)Math.Round(baseAward * multiplier, MidpointRounding.AwayFromZero);

                var cur = (Campus_live_rewrite.api.server.Classes.CurrencyType)currencyType;
                long awarded = Campus_live_rewrite.api.server.Classes.GameCurrency.ClampAward(cur, raw + bonus);

                if (awarded != raw + bonus)
                    Console.WriteLine($"[balance] {account.Id} award {raw + bonus} {cur} clamped to {awarded}");

                long total = PlayerDB.AddBalance(account.Id, currencyType, awarded);
                lastTotal = total;

                updates.Add(new
                {
                    UpdateResponse = 0, // 0 = success
                    // Data = the client's RewardBalanceModificationDTO, which it copies straight
                    // into a CurrencyAward { baseAward, bonusAward, currentCount, rateLimit,
                    // balanceAddType, currencyType, platformType } and then PLAYS - the coins that
                    // fly into the counter are baseAward + bonusAward. So BaseAward has to be the
                    // amount actually granted (base x score), not the base unit: with the unit here
                    // the player watched "+1" arrive while 1000 had been credited.
                    Data = new
                    {
                        BalanceAddType   = addType,
                        BalanceInGiftBox = false,   // a bool on the wire - "expected:'true | false', actual:'0'" (Player.log)
                        BaseAward        = awarded,
                        BonusAward       = 0,
                        CurrencyType     = currencyType,
                        CurrentCount     = 0,
                        Platform         = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket,
                        RateLimit        = 0,
                        Total            = total
                    }
                });
            }

            // The client deserialises this as BalanceUpdateResponseDTO<T>: BalanceUpdates PLUS the
            // BalanceResponseDTO base - Balance (the resulting total), CurrencyType and Platform
            // (BalanceType renamed on the wire). Sending BalanceUpdates alone was rejected by the
            // client as "Malformed Response" - the checkpoint silver was credited server-side and
            // then thrown away by the client, which is why a Crescendo checkpoint paid nothing
            // visible. (wire-shapes: econ-bulkpurchase, the same DTO.)
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    BalanceUpdates = updates,
                    Balance = lastTotal,
                    CurrencyType = currencyType,
                    Platform = Campus_live_rewrite.api.server.Classes.GameCurrency.AllPlatformsBucket
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/customAvatarItems/v1/isCreationEnabled")]
        public ContentResult IsCreationEnabled()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            player_data FullAccount = PlayerDB.GetFullAccount(account.Id);

            return new ContentResult()
            {
                Content = "true",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/customAvatarItems/v1/isRenderingEnabled")]
        public ContentResult IsRenderingEnabled()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            player_data FullAccount = PlayerDB.GetFullAccount(account.Id);

            return new ContentResult()
            {
                Content = "true",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/CampusCard/v1/UpdateAndGetSubscription")]
        public IActionResult RRPlus()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var subscription = new SubscriptionResponse
            {
                subscription = new Subscription
                {
                    subscriptionId = 507911,
                    recNetPlayerId = account.Id,
                    platformType = 0,
                    platformId = "76561198216660599",
                    platformPurchaseId = "4069635207998545083",
                    @event = null,
                    type = 0,
                    // SubscriptionLevel: Gold = 0, Platinum = 1 (see api/server/Classes/RRPlus.cs).
                    // Platinum is the top tier, so nothing gated on level stays locked.
                    level = (int)Campus_live_rewrite.api.server.Classes.RRPlus.SubscriptionLevel.Platinum,
                    // SubscriptionPeriod: Month = 0, Year = 1.
                    period = (int)Campus_live_rewrite.api.server.Classes.RRPlus.SubscriptionPeriod.Year,
                    // Was: 2024-03-20 - an expiry two years in the past, so the client
                    // correctly read the subscription as lapsed and showed the
                    // "Unlock Maker AI with RR+ / Subscribe Now" upsell. Far-future so
                    // it never lapses again.
                    expirationDate = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    isAutoRenewing = true,
                    createdAt = new DateTime(2024, 2, 18, 7, 15, 0, 284, DateTimeKind.Utc).AddTicks(5308),
                    modifiedAt = new DateTime(2024, 3, 20, 7, 1, 55, DateTimeKind.Utc).AddTicks(3454572)
                },
                platformAccountSubscribedPlayerId = null
            };

            return Content(System.Text.Json.JsonSerializer.Serialize(subscription), "application/json");
        }

        // The RR+ season running now. The 20250718 client reads ONE season object here (the
        // reference's `[]` was for a 2023 client): `[]` failed its parse and logged
        // "GetCurrentSeason failed" on every boot and every RR+ page. No season runs here, so the
        // answer is JSON null - "no current season", which the object reader accepts.
        [HttpGet("/api/subscriptionseasons/v1/seasons/current")]
        public ContentResult RRPlus2()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = "null",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The global slideshow feed: the most recent PUBLIC ShareCamera photos across all rooms,
        // newest first, each joined to its creator's username and room name. Ported from RecFlare
        // (getSlideshowImages); was a hardcoded "[]" so every slideshow surface sat empty.
        //
        // Deliberately PUBLIC (no token): it surfaces only already-public images and backs the
        // anonymous homepage slideshow. `take` defaults to 10, capped at 100 so an unauthenticated
        // caller can't ask for the whole image table. Answers { Images, ValidTill } where ValidTill
        // is a ~2-minute cache hint the client refreshes against.
        [HttpGet("/api/images/v1/slideshow")]
        public ContentResult Slideshow([FromQuery] int? take = null)
        {
            var rows = ImageMetadataDB.GetSlideshowImages(take ?? ImageMetadataDB.SlideshowLimit);

            // Bulk-resolve usernames and room names once, so the join is not a query per row.
            var names = new Dictionary<ulong, string>();
            foreach (var pid in rows.Select(r => r.PlayerId).Where(p => p != 0).Distinct())
                names[pid] = PlayerDB.GetFullAccount(pid)?.player?.Username ?? $"Player{pid}";

            var roomNames = new Dictionary<int, string?>();
            foreach (var rid in rows.Select(r => r.RoomId).Where(r => r != 0).Distinct())
                roomNames[rid] = RoomDB.GetRoom((ulong)rid)?.Name;

            var images = rows.Select(img => new
            {
                SavedImageId = img.Id,
                ImageName = img.ImageName,
                Username = img.PlayerId != 0 && names.TryGetValue(img.PlayerId, out var n) ? n : $"Player{img.PlayerId}",
                RoomName = img.RoomId != 0 && roomNames.TryGetValue(img.RoomId, out var rn) ? rn : null,
                RoomId = img.RoomId == 0 ? (int?)null : img.RoomId,
                SavedImageType = (int)img.Type,
                PlayerEventId = img.PlayerEventId == 0 ? (ulong?)null : img.PlayerEventId,
                Accessibility = (int)img.Accessibility,
                PlayerIds = img.TaggedPlayerIds ?? new List<ulong>()
            });

            var body = new
            {
                Images = images,
                ValidTill = DateTime.UtcNow.AddMinutes(2).ToString("o")
            };

            return new ContentResult()
            {
                Content = Newtonsoft.Json.JsonConvert.SerializeObject(body),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The item wishlist (the heart on a store item / the Wishlist page). The 2023 server's
        // working shape: the reads are a bare array of PurchasableItemIds, the Wishlist
        // storefront (giftdropstore/1300) is built per player from those ids. The 2025 client
        // posts add/remove to /wishlist/add and /wishlist/remove; the item id is read from the
        // body, the query or the route, whichever it used, and the raw fields are logged so the
        // spelling can be pinned.
        private static long WishlistItemId(Dictionary<string, string> f, long fromRoute = 0)
        {
            if (fromRoute > 0) return fromRoute;
            return RoomModeration.Long(f, "PurchasableItemId", "purchasableItemId", "itemId", "ItemId", "id", "Id", "item");
        }

        // The 2025 client posts { purchasableItemId, customAvatarItemId } - a UGC wish carries
        // purchasableItemId -1 and the item GUID. The client reads the wishlist as a LIST OF ENTRY
        // OBJECTS (real RecNet: WishlistItemId / AccountId / PurchasableItemId / CreatedAt); the
        // bare id array we answered before failed its parser the moment it was non-empty
        // ("Failed to add item to wishlist" / "Failed to get wishlist for player: An error occurred").
        private static string? WishlistCustomId(Dictionary<string, string> f) =>
            RoomModeration.Str(f, "customAvatarItemId", "CustomAvatarItemId", "customItemId") is { Length: > 0 } s && s != "null" ? s : null;

        [HttpPost("/api/itemWishlists/v1/wishlist/add")]
        [HttpPut("/api/itemWishlists/v1/wishlist/add")]
        [HttpPost("/api/itemWishlists/v1/wishlist/add/{itemId:long}")]
        [HttpPost("/api/itemWishlists/v1/wishlist/me/{itemId:long}")]
        [HttpPut("/api/itemWishlists/v1/wishlist/me/{itemId:long}")]
        [HttpPost("/api/itemWishlists/v1/wishlist/me")]
        [HttpPut("/api/itemWishlists/v1/wishlist/me")]
        public IActionResult WishlistAdd(long itemId = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = RoomModeration.Fields(Request);
            long item = WishlistItemId(f, itemId);
            string? custom = WishlistCustomId(f);
            int sf = (int)RoomModeration.Long(f, "StorefrontType", "storefrontType", "Storefront");
            Console.WriteLine($"[wishlist] {account.Id} add {Request.Path}: {RoomModeration.Dump(f)} -> item {item} ugc {custom ?? "-"}");
            if (item <= 0 && custom == null) return BadRequest(new { success = false, error = "no item id" });
            var row = WishlistDB.Add(account.Id, sf, item > 0 ? (int)item : -1, custom, RoomModeration.Dump(f));
            return Ok(WishlistDB.ToWire(row));
        }

        [HttpPost("/api/itemWishlists/v1/wishlist/remove")]
        [HttpDelete("/api/itemWishlists/v1/wishlist/remove")]
        [HttpPost("/api/itemWishlists/v1/wishlist/delete")]
        [HttpDelete("/api/itemWishlists/v1/wishlist/delete")]
        [HttpPost("/api/itemWishlists/v1/wishlist/remove/{itemId:long}")]
        [HttpDelete("/api/itemWishlists/v1/wishlist/remove/{itemId:long}")]
        [HttpDelete("/api/itemWishlists/v1/wishlist/me/{itemId:long}")]
        [HttpDelete("/api/itemWishlists/v1/wishlist/me")]
        [HttpDelete("/api/itemWishlists/v1/wishlist/add")]
        [HttpDelete("/api/itemWishlists/v1/wishlist/{itemId:long}")]
        [HttpDelete("/api/itemWishlists/v1/wishlist")]
        public IActionResult WishlistRemove(long itemId = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = RoomModeration.Fields(Request);
            long item = WishlistItemId(f, itemId);
            string? custom = WishlistCustomId(f);
            // Removal by the entry's WishlistItemId (the guid the list handed out) - route tail,
            // body or query - is resolved back to the row it names.
            string? wid = RoomModeration.Str(f, "wishlistItemId", "WishlistItemId", "id", "Id");
            string tail = Request.Path.Value?.TrimEnd('/').Split('/').LastOrDefault() ?? "";
            if (Guid.TryParse(wid, out var g1) || Guid.TryParse(tail, out g1))
            {
                var row = WishlistDB.For(account.Id).FirstOrDefault(r => WishlistDB.WireId(r) == g1);
                if (row != null) { item = row.PurchasableItemId; custom = row.CustomAvatarItemId; }
            }
            Console.WriteLine($"[wishlist] {account.Id} remove {Request.Path}: {RoomModeration.Dump(f)} -> item {item} ugc {custom ?? "-"}");
            if (item <= 0 && custom == null) return BadRequest(new { success = false, error = "no item id" });
            // Answered with the removed ENTRY - the client reads remove with the same response type
            // as add (same request helper instantiation in its wishlist service). The whole list we
            // used to send failed that parse, so the heart stayed filled and players pressed remove
            // again and again (57 of 192 removes repeated within 30 s, 2026-09-27).
            var gone = WishlistDB.For(account.Id).FirstOrDefault(r => custom != null
                ? string.Equals(r.CustomAvatarItemId, custom, StringComparison.OrdinalIgnoreCase)
                : r.CustomAvatarItemId == null && r.PurchasableItemId == (int)item)
                ?? new WishlistDB.Row { Key = custom != null ? $"{account.Id}|ugc|{custom.ToLowerInvariant()}" : $"{account.Id}|{item}", AccountId = account.Id, PurchasableItemId = custom != null ? -1 : (int)item, CustomAvatarItemId = custom };
            WishlistDB.Remove(account.Id, (int)item, custom);
            return Ok(WishlistDB.ToWire(gone));
        }

        // The caller's creator earnings: what is pending, what was paid, and when the next
        // Sunday payout lands.
        [HttpGet("/api/earnings/v1/mine")]
        [HttpGet("/api/site/earnings")]
        public IActionResult MyEarnings()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var (pending, paid, next) = EarningsDB.Summary(account.Id);
            var rows = EarningsDB.ForPlayer(account.Id).Select(e => new
            {
                e.Id, e.Kind, e.ItemName, e.Spent, e.Share, e.CurrencyType, e.BoughtAt, e.PayoutAt, e.PaidAt,
                Buyer = PlayerDB.GetAccountById(e.BuyerId)?.Username ?? e.BuyerId.ToString()
            }).ToList();
            return Ok(new { Pending = pending, Paid = paid, NextPayout = next, Percent = ConfigCampus.CreatorEarningsPercent, PendDays = ConfigCampus.CreatorEarningsPendDays, Sales = rows });
        }

        [HttpGet("/api/itemWishlists/v1/wishlist/me")]
        public IActionResult MyItemWishlists()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var mine = WishlistDB.WireFor(account.Id);
            // The star on a store item is drawn from what this read returns, so say what went out (which ids) - GETs are not
            // logged anywhere else. 2026-10-02: the Luau Hat star stayed empty although the item was on the wishlist.
            Console.WriteLine($"[wishlist] {account.Id} GET {Request.Path}{Request.QueryString} -> {mine.Count} entr{(mine.Count == 1 ? "y" : "ies")}: " +
                              string.Join(",", WishlistDB.For(account.Id).Take(40).Select(r => r.PurchasableItemId > 0 ? r.PurchasableItemId.ToString() : "ugc")));
            return Ok(mine);
        }

        [HttpGet("/api/itemWishlists/v1/wishlist/{playerId:long}")]
        public IActionResult GetWishlistForPlayer(ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var theirs = WishlistDB.WireFor(playerId);
            Console.WriteLine($"[wishlist] {account.Id} GET {Request.Path}{Request.QueryString} (player {playerId}) -> {theirs.Count} entr{(theirs.Count == 1 ? "y" : "ies")}");
            return Ok(theirs);
        }

        // The STAR on a store item. The 2025 client (isonwishlist/bulk, GameAssembly 0x7c91820) sends
        // { accountIds: [..], purchasableItemId, customAvatarItemId } - one ITEM, a list of ACCOUNTS -
        // and reads List<bool>: one answer per account id, in order, "is this item on THAT account's
        // wishlist". It used to be answered as one bool per item id, so the list came back empty, the
        // star never filled, and pressing it again sent another ADD instead of a REMOVE.
        [HttpPost("/api/itemWishlists/v1/isonwishlist/bulk")]
        [HttpGet("/api/itemWishlists/v1/isonwishlist/bulk")]
        public IActionResult IsOnWishlistBulk()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = RoomModeration.Fields(Request);
            long pid = RoomModeration.Long(f, "purchasableItemId", "PurchasableItemId");
            string? custom = WishlistCustomId(f);

            // Old spelling (a list of item ids, one answer per item) - kept only when the item form is absent.
            if (pid <= 0 && custom == null && RoomModeration.Str(f, "purchasableItemIds", "PurchasableItemIds", "itemIds") != null)
            {
                var items = RoomModeration.ULongs(f, "purchasableItemIds", "PurchasableItemIds", "itemIds");
                var mine = WishlistDB.For(account.Id).Where(r => r.PurchasableItemId > 0).Select(r => (ulong)r.PurchasableItemId).ToHashSet();
                return Ok(items.Select(i => mine.Contains(i)).ToList());
            }

            // Every account id, in the order sent (repeats kept: the answer is positional).
            var accounts = new List<ulong>();
            foreach (var n in new[] { "accountIds", "AccountIds", "accountId", "AccountId", "ids" })
            {
                if (!f.TryGetValue(n, out var v) || string.IsNullOrWhiteSpace(v)) continue;
                foreach (var part in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    accounts.Add(ulong.TryParse(part.Trim().Trim('[', ']', '"'), out var a) ? a : 0UL);
                if (accounts.Count > 0) break;
            }
            if (accounts.Count == 0) accounts.Add(account.Id);

            var answer = accounts.Select(a => WishlistDB.Has(a, pid, custom)).ToList();
            Console.WriteLine($"[wishlist] {account.Id} isonwishlist: {RoomModeration.Dump(f)} -> [{string.Join(",", answer.Select(b => b ? "1" : "0"))}]");
            return Ok(answer);
        }

        // Anything else under itemWishlists: logged by name so a new spelling is one line in the
        // journal instead of a silent 404. A guid in the path on a DELETE/POST is treated as a
        // removal by WishlistItemId (e.g. DELETE .../wishlist/{guid}).
        [Route("/api/itemWishlists/{**rest}")]
        public IActionResult WishlistFallback(string? rest)
        {
            var f = RoomModeration.Fields(Request);
            Console.WriteLine($"[wishlist] UNHANDLED {Request.Method} {Request.Path}{Request.QueryString} {RoomModeration.Dump(f)}");
            string tail = (rest ?? "").TrimEnd('/').Split('/').LastOrDefault() ?? "";
            if (Guid.TryParse(tail, out _) && !HttpMethods.IsGet(Request.Method)) return WishlistRemove(0);
            return Ok(Array.Empty<long>());
        }

        [HttpGet("/api/server/status")]
        public ContentResult status()
        {

            string key = HttpContext.Request.Headers["key"].ToString();

            if (key == ConfigCampus.VALID_STATUS_KEY)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        uptime = UPTIME,
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        message = "Forbidden!"
                    }),
                    ContentType = "application/json",
                    StatusCode = 403
                };
            }
        }

        [HttpPost("/api/sanitize/v1")]
        public IActionResult APISanitizeV1([FromBody] Sanitize request)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string value = request?.Value ?? "";

            if (ConfigCampus.AllowSwears == true)
            {
                return Content(JsonConvert.SerializeObject(value), "application/json");
            }
            // The client tells us what to mask with (ReplacementChar, "*" in practice).
            char mask = string.IsNullOrEmpty(request?.ReplacementChar) ? '*' : request.ReplacementChar[0];
            string sanitized = Sanitize.SanitizeMessage(value, mask);
            if (ConfigCampus.LogMessagesFromSanitize == true)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        Webhook.Send(account.Id, "mod", "Reporting Info - Sanitize", $"({account.Id}) @{account.Username} \n**Sanitize requested!**\n" +
                        $"**Current Room:** {HeartbeatDB.GetPlayerCurrentRoomName(account.Id)}\n" +
                        $"**Request Message:** \"{value}\"\n" +
                        $"**Sanitized Message:** \"{sanitized}\"\n" +
                        $"**Context:** \"{request.Context}\""
                        , 5898927);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex);
                    }
                });
            }
            return Content(JsonConvert.SerializeObject(sanitized), "application/json");
        }

        [HttpPost("/api/sanitize/v1/isPure")]
        public IActionResult ApiSanitizeV1IsPure([FromBody] Sanitize request)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var isPure = !Sanitize.ContainsSwears(request.Value);

            return Ok(new
            {
                IsPure = isPure
            });
        }

        [HttpGet("/api/customAvatarItems/v2/fromCreator/{PlayerId}")]
        public ContentResult CustomAvatarItemsFromCreator(ulong PlayerId, [FromQuery] int skip = 0, [FromQuery] int take = 1000)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (take > 1000) take = 1000;
            if (take < 0) take = 0;
            if (skip < 0) skip = 0;

            // The UGC this player created, newest first, from the in-memory index. Another
            // player's profile only shows what the creator PUBLISHED (Accessibility != 0) - the
            // creator sees their private / unpublished items too, the way the reference does.
            bool self = PlayerId == account.Id;
            var mine = CustomItemsCache.ByCreator(PlayerId)
                .Where(x => self || ((int?)x["Accessibility"] ?? 0) != 0)
                .OrderByDescending(x => ((string?)x["CreatedAt"]) ?? "")
                .ToList();
            // The Coach (account 1) also owns the first-party stock, as on the reference.
            if (PlayerId == CustomItemsCache.CoachAccountId && ConfigCampus.ServeFirstPartyClothing)
                mine.AddRange(CustomItemsCache.FirstPartyRecs().Select(r => r.ToJObject()));
            var page = mine.Skip(skip).Take(take).ToList();

            return new ContentResult()
            {
                Content = new JObject
                {
                    ["Results"] = new JArray(page),
                    ["TotalResults"] = mine.Count
                }.ToString(Newtonsoft.Json.Formatting.None),
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        [HttpGet("/api/influencerpartnerprogram/influencer")]
        public ContentResult influencerpartnerprogram(ulong accountId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Bare number, always 200 (a 404 here is the "404 blanks the profile" trap). The value
            // is the influencer THIS ACCOUNT SUPPORTS (0 = nobody) - the same thing /myinfluencer
            // answers for the caller. "Is X an influencer" is a different question, answered by
            // the /influencers id list the client badges creators from. Answering X's own id here
            // whenever X was an influencer made every influencer's profile read as supporting
            // themselves, whoever they had actually picked.
            string body = (PlayerDB.GetMyInfluencer(accountId) ?? 0).ToString();
            return new ContentResult()
            {
                Content = body,
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/avatar/v2/set")]
        public async Task<ContentResult> SetAvatar()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string jsonData = "";
            try
            {
                using var reader = new StreamReader(HttpContext.Request.Body);
                jsonData = await reader.ReadToEndAsync();
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

            if (string.IsNullOrWhiteSpace(jsonData))
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };

            try
            {
                Newtonsoft.Json.Linq.JToken.Parse(jsonData);
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


            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 401
                };

            try
            {
                PlayerDB.set_player_avatar(jsonData, player.playerid);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Avatar Set Error] {ex}");
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            return new ContentResult()
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        [HttpPost("/api/consumables/v1/consume")]
        public IActionResult ConsumeConsumable()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok("");
        }

        [HttpGet("/api/online_players")]
        public IActionResult OnlinePlayers()
        {
            var onlineList = new List<object>();

            foreach (var (playerId, socket) in NotificationsController.WebSockets)
            {
                onlineList.Add(new
                {
                    PlayerId = playerId,
                    CurrentRoom = HeartbeatDB.GetPlayerCurrentRoomName(playerId)
                });
            }

            return Ok(onlineList);
        }

        [HttpGet("/api/ping")]
        public ContentResult ServerPing()
        {
            return new ContentResult
            {
                Content = "true",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // todo check if it a real image file and return a 400 error if it not a image file - Waeboi69
        [HttpPost("/api/images/v4/uploadsaved")]
        public async Task<ContentResult> UploadImage()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            HttpContext.Request.EnableBuffering();

            byte[] array;
            using (var memoryStream = new MemoryStream(capacity: 4 * 1024 * 1024))
            {
                await HttpContext.Request.Body.CopyToAsync(memoryStream, bufferSize: 131072);
                array = memoryStream.ToArray();
            }

            HttpContext.Request.Body.Position = 0;

            // Only a photo the game's camera encoded gets stored (see UploadGuard). Checked
            // before anything touches disk.
            byte[]? photo = Utils.ParseData(array, "file.bin", out bool photoParsed);
            var refused = UploadGuard.Gate(HttpContext, account, UploadGuard.Kind.Image, photoParsed ? photo : null, "images/v4/uploadsaved");
            if (refused != null) return refused;

            var saveImageTask = Task.Run(() => Images.SaveImageFile(array));
            var formTask = HttpContext.Request.ReadFormAsync();

            var file = await saveImageTask.ConfigureAwait(false);
            var form = await formTask.ConfigureAwait(false);

            if (!form.TryGetValue("imgMeta", out var imgMetaValue) || string.IsNullOrEmpty(imgMetaValue))
            {
                return new ContentResult
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            ImageMetadataDB.ImageMetadata? metadata = null;

            if (!string.IsNullOrEmpty(imgMetaValue))
            {
                metadata = JsonConvert.DeserializeObject<ImageMetadataDB.ImageMetadata>(imgMetaValue!);
            }

            var newImage = new FullSavedImage
            {
                Id = 0,
                RoomId = metadata.roomId,
                PlayerId = account.Id,
                ImageName = file.rawfilename,
                Description = metadata.description,
                Accessibility = metadata.accessibility,
                TaggedPlayerIds = metadata.playerIds,
                CreatedAt = DateTime.UtcNow
            };

            if (metadata.savedImageType != SavedImageTypeEnum.OutfitThumbnail)
            {
                await Task.Run(() => ImageMetadataDB.InsertImage(newImage)).ConfigureAwait(false);

                if (metadata.accessibility == ImageAccessibility.Public)
                {
                    _ = Task.Run(() => Webhook.Send(
                        account.Id, "prod", "Server info",
                        $"({account.Id}) @{account.Username} has uploaded an image",
                        5898927, $"https://camprec.net/img/{file.rawfilename}"
                    ));
                }
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    ImageName = file.rawfilename
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        // The client expects ONE BARE 13-key image record here, never an array (wire-shapes:
        // api-images-v6). Both spellings are live: ?name= and the path form /api/images/v6/{id},
        // which used to 404. An unknown image answers a stub record rather than [] - the client's
        // post-deserialize hook only normalises null ids to 0, so a stub is safe and an array is a
        // Malformed Response.
        [HttpGet("/api/images/v6/{savedImageId:long}")]
        public ContentResult GetImageById(long savedImageId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var rec = ImageMetadataDB.GetImageV6ById(savedImageId);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(rec ?? ImageMetadataDB.EmptyImageV6("")),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/images/v6")]
        public ContentResult GetImageByImageName(string name)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            try
            {
                var image = ImageMetadataDB.GetImageV6ByName(name) ?? ImageMetadataDB.EmptyImageV6(name);

                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(image),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Image V6 Error] {ex}");
                return new ContentResult
                {
                    Content = "[]",
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
        }

        [HttpGet("/api/images/v5/cheered/bulk")]
        [HttpPost("/api/images/v5/cheered/bulk")]
        public ContentResult ImagesCheered([FromQuery] int[] id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // The 2025 client POSTs the ids (form or JSON "id"/"ids"/"imageIds") - 405'd as GET-only.
            if (id == null || id.Length == 0)
            {
                var f = RoomModeration.Fields(Request);
                id = RoomModeration.ULongs(f, "id", "ids", "imageIds", "savedImageIds", "SavedImageIds").Select(u => (int)u).ToArray();
            }

            var results = id.Select(imageId => new
            {
                SavedImageId = imageId,
                IsCheered = ImageMetadataDB.HasUserCheered(imageId, account.Id)
            }).ToList();

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(results),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/images/v4/room/{id}")]
        public ContentResult GetRoomImages(int id, int sort = 0, int filter = 0, int take = 50, int skip = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var images = ImageMetadataDB.GetRoomImages(account.Id, id, sort, filter, take, skip);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(images, Formatting.Indented),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/images/v1/cheer")]
        public ContentResult CheerImage([FromBody] CheerRequest request)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            bool isCheered = ImageMetadataDB.ToggleCheer(request.SavedImageId, account.Id, request.Cheer);
            int cheerCount = ImageMetadataDB.GetCheerCount(request.SavedImageId);

            ImageMetadataDB.UpdateImageCheerCount(request.SavedImageId, cheerCount);

            return new ContentResult
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/images/v5/player/{playerid}")]
        public IActionResult GetPlayerImages(ulong playerid, int sort = 0, int skip = 0, int take = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var playerImages = ImageMetadataDB.GetPlayerImages(account.Id, playerid, sort, skip, take);

            return Ok(playerImages);
        }

        // The photo tab on a profile: the images this player took PLUS the ones they're tagged in,
        // newest first (RecFlare /api/images/v3/feed/player). Only v3/feed/GLOBAL and v5/player
        // existed, so the profile's photo feed 404'd - a big reason a profile opened blank. Serves
        // the same ImagesPlayer projection (bare array) as the other player photo lists.
        [HttpGet("/api/images/v3/feed/player/{playerid}")]
        public IActionResult GetPlayerFeed(ulong playerid, [FromQuery] int skip = 0, [FromQuery] int take = 100, [FromQuery] int sort = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ImageMetadataDB.GetPlayerFeed(account.Id, playerid, skip, take, sort: sort));
        }

        // A player's own photos (RecFlare /api/images/v4/player) - the older sibling of v5/player,
        // same ImagesPlayer projection. Was missing, so a client that asks for v4 got a 404 and an
        // empty photo grid.
        [HttpGet("/api/images/v4/player/{playerid}")]
        public IActionResult GetPlayerImagesV4(ulong playerid, [FromQuery] int skip = 0, [FromQuery] int take = 100, [FromQuery] int sort = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ImageMetadataDB.GetPlayerImages(account.Id, playerid, sort, skip, take));
        }

        [HttpGet("/api/images/v5/bulk")]
        public ContentResult GetImagesByBulkIds()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var idsRaw = Request.Query["ids"];

            List<int> idList = idsRaw
                .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(x => x.Trim())
                .Where(x => int.TryParse(x, out _))
                .Select(int.Parse)
                .ToList();

            if (idList.Count == 0)
            {
                return new ContentResult
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            // SavedImageId / SavedImageType shape - see ImageMetadataDB.GetImagesV6ByIds.
            var images = ImageMetadataDB.GetImagesV6ByIds(idList);

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(images),
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        [HttpGet("/api/rooms/v1/filters")]
        public ContentResult GetRoomTags()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var roomTags = new
            {
                PinnedFilters = new[]
                {
                    "recroomoriginal", "community", "featured", "quest", "pvp", "hangout", "game",
                    "art", "store", "tutorial", "fandom", "performance", "action", "horror"
                },
                PopularFilters = new[]
                {
                    "pvp", "quest", "game", "hangout", "art"
                },
                TrendingFilters = new[]
                {
                    "roleplay", "nomp", "rp", "casual", "fun", "action", "military", "sports"
                }
            };

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(roomTags),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Whether the caller holds at least `role` in a room - the gate the client checks before
        // letting someone use the Maker Pen / room settings in-room (reference: verifyRole). A bare
        // true/false; every failure is false rather than an error status. The creator always
        // passes; anyone else needs a Roles entry at that level or higher. This answered "[]" for
        // everyone, so a co-owner (and anyone the client asked about) was told "no" - the "can't
        // do anything in a room I'm co-owner of" complaint. Params from the form body, falling
        // back to the query string; `context` (e.g. MakerPen) is accepted and ignored.
        [HttpPost("/api/rooms/v1/verifyRole")]
        public ContentResult VerifyRole()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return new ContentResult { Content = "false", ContentType = "application/json", StatusCode = 200 };

            string Param(string name)
            {
                try
                {
                    if (Request.HasFormContentType)
                    {
                        var v = Request.Form[name].ToString();
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
                catch { }
                return Request.Query[name].ToString();
            }
            ulong.TryParse(Param("roomId"), out var roomId);
            int.TryParse(Param("role"), out var role);
            if (roomId == 0) roomId = (ulong)Math.Max(0, long.TryParse(Param("RoomId"), out var r2) ? r2 : 0);

            var room = roomId != 0 ? RoomDB.GetRoom(roomId) : null;
            bool ok = room != null && RoomDB.PlayerHasRoleAtLeast(roomId, account.Id, role);
            // role 0 is the "an in-game role lets me use the pen" check, which the server can't
            // see and so answers yes to any visitor. Rec Room Originals (the Rec Center...) have
            // no such in-game roles: there it takes the creator, a real server role, or staff,
            // so a client claiming the pen in the Rec Center is told no.
            if (ok && role <= 0 && RoomDB.IsRecRoomOriginal(room) && room!.CreatorAccountId != account.Id
                && !AutoEnforce.IsStaff(account.Id)
                && !(room.Roles?.Any(r => (ulong)r.AccountId == account.Id && r.Role != RoomDBClasses.Role_data.None && r.Role != RoomDBClasses.Role_data.Banned) ?? false))
                ok = false;
            Console.WriteLine($"[verifyRole] {account.Id} room {roomId} role>={role} ctx='{Param("context")}' -> {ok}");

            // The game only asks when it already believes the player may use the tool, so a
            // refusal means a tampered client (or a player banned from the room still in it).
            // Only for a real room: roomId 0 / an unknown room is ambiguous and just answers no.
            if (!ok && room != null && ConfigCampus.VerifyRoleFailDisconnect)
                AutoEnforce.DisconnectAndTimeout(account.Id, Moderation_Detail.ReportCategory.Cheating, ConfigCampus.VerifyRoleFailTimeoutSeconds,
                    "Disconnected: you tried to use a tool you don't have permission for in this room.",
                    $"verifyRole refused: room {roomId} ('{room.Name}') role>={role} ctx='{Param("context")}'");
            return new ContentResult { Content = ok ? "true" : "false", ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("/api/avatar/v3/saved")]
        public ContentResult GetOutfit()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = PlayerDB.GetAvatarSaved(account.Id),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/avatar/v4/saved/set")]
        public async Task<ContentResult> SaveOutfit()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            using var reader = new StreamReader(HttpContext.Request.Body);
            string body = await reader.ReadToEndAsync();

            avatar_data_saved avatar;
            try
            {
                avatar = JsonConvert.DeserializeObject<avatar_data_saved>(body);
                if (avatar == null)
                    throw new Exception("Deserialization returned null");

                Console.WriteLine($"Slot: {avatar.Slot}, Name: {avatar.Name}, SkinColor: {avatar.SkinColor}");

                // Keep the raw document the client posted so a preset round-trips without losing
                // any field our structured type doesn't model.
                if (string.IsNullOrEmpty(avatar.Avatar)) avatar.Avatar = body;
                PlayerDB.SetAvatarSaved(account.Id, new List<avatar_data_saved> { avatar });
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to parse or save avatar JSON: " + ex.Message);
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Slot = avatar.Slot
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // v3 of the save-outfit route. SAME payload, table and slot-overwrite semantics as
        // /api/avatar/v4/saved/set above - the reference shares one handler for both and only the
        // RESPONSE differs (v3 echoes the whole saved outfit back; v4 answers { Success, Slot }).
        // This route did not exist, so a client that saves through v3 got a 404 and the outfit
        // never reached the database. Persists into the same store (PlayerDB.SetAvatarSaved ->
        // player_Extra.SavedOutfits) that /api/avatar/v3/saved and /outfits/me/saved read back.
        [HttpPost("/api/avatar/v3/saved/set")]
        public async Task<ContentResult> SaveOutfitV3()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            using var reader = new StreamReader(HttpContext.Request.Body);
            string body = await reader.ReadToEndAsync();

            avatar_data_saved avatar;
            try
            {
                avatar = JsonConvert.DeserializeObject<avatar_data_saved>(body);
                if (avatar == null)
                    throw new Exception("Deserialization returned null");

                Console.WriteLine($"[v3/saved/set] Slot: {avatar.Slot}, Name: {avatar.Name}");

                if (string.IsNullOrEmpty(avatar.Avatar)) avatar.Avatar = body;
                PlayerDB.SetAvatarSaved(account.Id, new List<avatar_data_saved> { avatar });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[v3/saved/set] failed to parse or save avatar JSON: " + ex.Message);
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 400 };
            }

            // v3 echoes the saved outfit back (v4 returns the lean ack instead).
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(avatar),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/PlayerCheer/v1/SetSelectedCheer")]
        public async Task<ContentResult> SetSelectedCheer()
        {
            try
            {
                var (account, error) = AuthHelper.ValidateToken(HttpContext);
                if (error != null)
                    return error;

                // Form, query or JSON - Request.Form threw on a non-form body and the catch below
                // turned every such call into a 400.
                var cf = Campus_live_rewrite.api.server.RoomModeration.Fields(Request);
                string? cheerCategoryStr = Campus_live_rewrite.api.server.RoomModeration.Str(cf, "CheerCategory", "cheerCategory", "category", "value");

                if (string.IsNullOrEmpty(cheerCategoryStr))
                {
                    return new ContentResult()
                    {
                        Content = "",
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                if (!Enum.TryParse<CheerCategoryEnum>(cheerCategoryStr, out var cheerCategory))
                {
                    return new ContentResult()
                    {
                        Content = "",
                        ContentType = "application/json",
                        StatusCode = 400
                    };
                }

                if (account == null)
                {
                    return new ContentResult()
                    {
                        Content = "",
                        ContentType = "application/json",
                        StatusCode = 401
                    };
                }
                // Enum.TryParse above accepts ANY whole number, so "4000" or "9000" (the Rec Room Developer badge) used to be stored for
                // anyone. A normal player may only pick none or the five real cheers (0/10/20/30/40); a developer may pick any badge.
                if (!PlayerDB.MaySelectCheer(cheerCategory, account.Id))
                {
                    Console.WriteLine($"[cheer] {account.Id} tried to select cheer {cheerCategoryStr} - refused");
                    return new ContentResult() { Content = "", ContentType = "application/json", StatusCode = 400 };
                }
                await Task.Run(() => PlayerDB.SetPlayerCheer(cheerCategory, account.Id));

                try
                {
                    var selfUpdate = JsonConvert.SerializeObject(WebsocketEvents.CreateResponseReputationUpdate(account.Id));
                    var globalUpdate = JsonConvert.SerializeObject(WebsocketEvents.CreateResponseReputationUpdate(account.Id));

                    var notifySelfTask = Notifications.SendToPlayer(account.Id, selfUpdate);
                    var notifyAllTask = Notifications.SendToAll(globalUpdate);

                    await Task.WhenAll(notifySelfTask, notifyAllTask);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to send notification: {ex.Message}");
                }

                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SetSelectedCheer Error: {ex}");
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }
        }

        [HttpGet("/api/incentivizedreferrals/progress")]
        public ContentResult get_incentivizedreferrals_incentivizedreferrals()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    value = new
                    {
                        ReferralsVerifiedCount = 0,
                        PlayerReferralRewards = new List<object>()
                    },
                    /*
                    [{
                        "UnlockedAt": null,
                        "ClaimedAt": null,
                        "Status": 0,
                        "ReferralRewardTier": {
                            "ReferralRewardTierId": 1,
                            "RequiredReferralCount": 1,
                            "RewardSelectionCount": 1
                        }
                    }]
                    */
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        //https://econ.rec.net/api/influencerpartnerprogram/influencer?accountId=1503269750 return a 404 or a influencer account id

        // /api/influencerpartnerprogram/support

        // /api/inventions/v1/fromcreators // todo: add this


        [HttpGet("/api/influencerpartnerprogram/influencers")]
        public ContentResult GetAllInfluencers()
        {
            // https://econ.rec.net/api/influencerpartnerprogram/influencers?take=1000
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    InfluencerIds = PlayerDB.GetInfluencerIds(),
                    ContinuationToken = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/influencerpartnerprogram/support")]
        public ContentResult SupportInfluencer()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string? SupportIdStr = HttpContext.Request.Form["influencerAccountId"].ToString();
            if (!ulong.TryParse(SupportIdStr, out ulong SupportId)) SupportId = 0;
            // you can only support someone ELSE, and only an actual partner-program influencer
            if (SupportId == account.Id || (SupportId != 0 && !PlayerDB.IsInfluencer(SupportId)))
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { success = false, error = SupportId == account.Id ? "You can't support yourself." : "That player isn't in the creator program.", value = (string?)null }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            PlayerDB.SupportInfluencer(account.Id, SupportId);

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

        [HttpPost("/api/influencerpartnerprogram/remove")]
        public ContentResult RemoveInfluencer()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            PlayerDB.SupportInfluencer(account.Id, 0);

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

        [HttpGet("/api/influencerpartnerprogram/myinfluencer")]
        public IActionResult MyInfluencer()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Bare number, always 200 - see the note on /influencer above. The reference returns 0
            // for everyone (no partner program); returning the stored tier (0 when none) matches
            // that and drops the 404 that a profile-open can choke on.
            var myInfluencer = PlayerDB.GetMyInfluencer(account.Id);
            return Content(myInfluencer.ToString(), "application/json");
        }

        // /api/relationships/v2/sendfriendrequest?id={playerid}

        // /api/relationships/v2/addfriend?id={playerid}

        // /api/relationships/v2/acceptfriendrequest?id={playerid}

        // Both of these 404'd on every call (bulkignoreplatformusers 13x in two days).
        // They are platform-friend plumbing: the client asks which Steam/Xbox users are
        // blocked, and which platform friends referred it. This server has no platform
        // friend graph, so the honest answer is an empty list - but it has to BE a list.
        // A 404 leaves the client's social panel waiting on a request that never resolves.
        [HttpPost("/api/relationships/v1/bulkignoreplatformusers")]
        [HttpGet("/api/relationships/v1/bulkignoreplatformusers")]
        public ContentResult BulkIgnorePlatformUsers()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/externalfriendinvite/v1/getplatformreferrers")]
        [HttpGet("/api/externalfriendinvite/v1/getplatformreferrers")]
        public ContentResult GetPlatformReferrers()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/relationships/v2/get")]
        public ContentResult GetRelationships()
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

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(
                    FriendsDB.GetRelationships(account.Id).Select(RelationshipDto).ToList()
                ),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The client's RelationshipResponse is EXACTLY these five fields (RecFlare's
        // RelationshipDto, asserted with `toEqual`). Our internal RelationshipDetail also carries
        // Id, OtherPlayerID and VoiceVolume, and Id is a ~1.79-trillion value that overflows an
        // int32 - emitting those made the client's strict parser drop the WHOLE array, so the
        // friends list came back empty (it never even fetched the account cards - no
        // /Accounts/account/bulk followed the relationships GET). Project to the exact shape,
        // flags as 0/1.
        private static object RelationshipDto(RelationshipDetail r) => new
        {
            PlayerID = r.PlayerID,
            RelationshipType = (int)r.RelationshipType,
            Favorited = r.Favorited == ReciprocalStatus.None ? 0 : 1,
            Ignored = FriendsDB.IgnoredWire(r.Ignored),   // 1 mine / 2 theirs / 3 both - see FriendsDB.IgnoredWire
            Muted = r.Muted == ReciprocalStatus.None ? 0 : 1,
            VoiceVolume = FriendsDB.VoiceVolumeWire(r.VoiceVolume)   // the per-player volume slider (0-200)
        };

        [HttpGet("/api/relationships/v2/sendfriendrequest")]
        [HttpPost("/api/relationships/v2/sendfriendrequest")]
        public ContentResult SendFriendRequest(ulong id) // player id
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Was: a dev-flagged sender who added Coach (id 1) had the request auto-accepted after
            // 1 s. The intent was a staff convenience, but it meant adding Coach "automatically
            // accepts your friend request" with no notification, no record, and no way for Coach
            // to decline. Removed - Coach now gets pending requests like every other account.
            RelationshipDetail relationship = FriendsDB.SendFriendRequest(account.Id, id);

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(RelationshipDto(relationship)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/relationships/v2/acceptfriendrequest")]
        [HttpPost("/api/relationships/v2/acceptfriendrequest")]
        public ContentResult AcceptFriendRequest(ulong id) // player id
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Todo: add rate limiting

            RelationshipDetail relationship = FriendsDB.AcceptFriendRequest(id, account.Id);

            if (relationship == null)
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(RelationshipDto(relationship)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/relationships/v2/addfriend")]
        [HttpPost("/api/relationships/v2/addfriend")]
        public ContentResult AddFriendViaHandshakeInGame(ulong id) // player id
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Befriend DIRECTLY, no pending-request step (reference: addfriend - "Become friends
            // with no pending-request step. Already being friends is a no-op."). This is the
            // in-game handshake: both players have already agreed physically. It used to only
            // ACCEPT an existing request, so a handshake between two players with no request
            // pending returned null, threw (500) and made no friends.
            if (id == 0 || id == account.Id)
                return new ContentResult { Content = "{\"error\":\"invalid player id\"}", ContentType = "application/json", StatusCode = 400 };
            RelationshipDetail? relationship = FriendsDB.AddFriendDirect(account.Id, id);
            if (relationship == null)
                return new ContentResult { Content = "{\"error\":\"could not add friend\"}", ContentType = "application/json", StatusCode = 400 };
            Console.WriteLine($"[handshake] {account.Id} and {id} are now friends");

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(RelationshipDto(relationship)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Per-player voice volume (the slider on a player's card, 0-200; body {PlayerId, VoiceVolume}).
        // Stored on the caller's own side of the relationship row. The client parses the RESPONSE as
        // that player's relationship and caches it - the volume it then applies to their voice is
        // read from that cache. It used to answer {success,error}, so the cached record came back
        // with no PlayerID / VoiceVolume and the slider never changed anyone's volume (2026-09-26).
        [HttpPost("/api/relationships/v2/updateVoiceVolume")]
        [HttpPut("/api/relationships/v2/updateVoiceVolume")]
        public IActionResult UpdateVoiceVolume()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = RoomModeration.Fields(Request);
            ulong target = RoomModeration.ULong(f, "PlayerId", "playerId", "id", "OtherPlayerId");
            long vol = RoomModeration.Long(f, "VoiceVolume", "voiceVolume", "Volume", "volume");
            if (target == 0 || target == account.Id) return BadRequest(new { error = "invalid player id" });
            var rel = FriendsDB.SetVoiceVolume(account.Id, target, (int)Math.Clamp(vol, 0, 200));
            Console.WriteLine($"[voice-volume] {account.Id} set player {target} to {Math.Clamp(vol, 0, 200)}%");
            return new ContentResult { Content = JsonConvert.SerializeObject(rel), ContentType = "application/json", StatusCode = 200 };
        }

        // Report one chat message (reference api POST /api/chatreport/createChatReport): the report
        // is filed against the message's SENDER, and only a member of the thread may file it.
        [HttpPost("/api/chatreport/createChatReport")]
        public IActionResult CreateChatReport()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = RoomModeration.Fields(Request);
            long messageId = RoomModeration.Long(f, "ChatMessageId", "chatMessageId", "MessageId");
            long threadId = RoomModeration.Long(f, "ChatThreadId", "chatThreadId", "ThreadId");
            string details = RoomModeration.Str(f, "ReportDescription", "Details", "Description") ?? "";
            var catRaw = RoomModeration.Str(f, "ReportCategory", "Category") ?? "";
            int category = int.TryParse(catRaw, out var cn) ? cn
                : Enum.TryParse<Moderation_Detail.ReportCategory>(catRaw, true, out var ce) ? (int)ce : 0;
            var msg = ChatDB.GetMessage(messageId);
            if (msg == null || !ChatDB.IsMember(msg.ChatThreadId, account.Id) || msg.SenderPlayerId <= 0)
                return Ok(new { success = false, error = "That message could not be found." });
            Campus_live_rewrite.api.dbs.ReportsDB.Create(new Campus_live_rewrite.api.dbs.ReportsDB.Report
            {
                ReporterPlayerId = account.Id,
                ReportedPlayerId = (ulong)msg.SenderPlayerId,
                ReportCategory = category,
                Details = $"[chat message {messageId} in thread {msg.ChatThreadId}] {(int.TryParse(catRaw, out _) ? "" : catRaw + ": ")}{details}".Trim(),
            });
            Console.WriteLine($"[report] {account.Id} reported chat message {messageId} by {msg.SenderPlayerId} ({catRaw})");
            return Ok(new { success = true, error = "" });
        }

        [HttpGet("/api/relationships/v2/removefriend")]
        [HttpPost("/api/relationships/v2/removefriend")]
        public ContentResult RemoveFriend(ulong id) // player id
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            //Todo: add rate limiting

            RelationshipDetail relationship = FriendsDB.RemoveFriend(id, account.Id);

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(RelationshipDto(relationship)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // ---------------------------------------------------------------------------------------
        // Per-side relationship flags: favorite / ignore / mute and their inverses. Ported from
        // RecFlare's social routes; none of these existed before, so favoriting ("liking") a
        // friend, ignoring, and muting all 404'd. The flag lands on the CALLER's own side of the
        // pair (FriendsDB.SetRelationshipFlag) - and can be set on a player you were never friends
        // with, which just creates a bare (None) row to hold it. The client calls these as a GET
        // with the target in ?id=; POST + a PlayerId form field is accepted too. Body is the
        // { Success, Message } ack RecFlare returns; the caller re-reads /relationships/v2/get to
        // see the new flag (favorited friends pin to the top of the list).
        // ---------------------------------------------------------------------------------------
        private ContentResult RelationshipFlagResult(FriendInfo flag, bool value)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            ulong target = 0;
            string q = Request.Query["id"].ToString();
            if (string.IsNullOrEmpty(q)) q = Request.Query["playerId"].ToString();
            if (string.IsNullOrEmpty(q) && Request.HasFormContentType)
            {
                q = Request.Form["PlayerId"].ToString();
                if (string.IsNullOrEmpty(q)) q = Request.Form["playerId"].ToString();
                if (string.IsNullOrEmpty(q)) q = Request.Form["Id"].ToString();
            }

            if (!ulong.TryParse(q, out target) || target == 0 || target == account.Id)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { error = "invalid player id" }),
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            // Ignore is BLOCK: two-sided, and only the blocker can lift it (FriendsDB.SetBlock).
            var rel = flag == FriendInfo.Ignored
                ? FriendsDB.SetBlock(account.Id, target, value)
                : FriendsDB.SetRelationshipFlag(account.Id, target, flag, value);

            // The real RecNet answers the updated relationship (research/RecNet API.postman:
            // /api/relationships/v1/favorite -> {Id, PlayerID, RelationshipType, Favorited, Muted,
            // Ignored}), which is what the client's star / mute toggles update themselves from.
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    Id = rel.Id,
                    PlayerID = rel.PlayerID,
                    RelationshipType = (int)rel.RelationshipType,
                    Favorited = rel.Favorited == ReciprocalStatus.None ? 0 : 1,
                    Muted = rel.Muted == ReciprocalStatus.None ? 0 : 1,
                    Ignored = FriendsDB.IgnoredWire(rel.Ignored)
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/relationships/v1/favorite")]
        [HttpPost("/api/relationships/v1/favorite")]
        public ContentResult FavoritePlayer() => RelationshipFlagResult(FriendInfo.Favorited, true);

        [HttpGet("/api/relationships/v1/unfavorite")]
        [HttpPost("/api/relationships/v1/unfavorite")]
        public ContentResult UnfavoritePlayer() => RelationshipFlagResult(FriendInfo.Favorited, false);

        [HttpGet("/api/relationships/v1/ignore")]
        [HttpPost("/api/relationships/v1/ignore")]
        public ContentResult IgnorePlayer() => RelationshipFlagResult(FriendInfo.Ignored, true);

        [HttpGet("/api/relationships/v1/unignore")]
        [HttpPost("/api/relationships/v1/unignore")]
        public ContentResult UnignorePlayer() => RelationshipFlagResult(FriendInfo.Ignored, false);

        [HttpGet("/api/relationships/v1/mute")]
        [HttpPost("/api/relationships/v1/mute")]
        public ContentResult MutePlayer() => RelationshipFlagResult(FriendInfo.Muted, true);

        [HttpGet("/api/relationships/v1/unmute")]
        [HttpPost("/api/relationships/v1/unmute")]
        public ContentResult UnmutePlayer() => RelationshipFlagResult(FriendInfo.Muted, false);

        // Presence for the caller's favorited friends. Like RecFlare, presence isn't joined here
        // yet, so this is an empty list - but it must answer 200 [] instead of 404ing, which the
        // client treats as an error on the friends panel.
        [HttpGet("/api/messages/v1/favoriteFriendOnlineStatus")]
        public ContentResult FavoriteFriendOnlineStatus()
        {
            return new ContentResult
            {
                Content = "[]",
                ContentType = "application/json",
                StatusCode = 200
            };
        }
        /*
        [HttpPost("/api/customAvatarItems/v1")]
        public async Task<ContentResult> CreateCustomAvatar()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            HttpContext.Request.EnableBuffering();

            byte[] array;
            using (var memoryStream = new MemoryStream(capacity: 4 * 1024 * 1024))
            {
                await HttpContext.Request.Body.CopyToAsync(memoryStream, bufferSize: 131072);
                array = memoryStream.ToArray();
            }

            HttpContext.Request.Body.Position = 0;

            // todo: get the desinflesti elif g

            var saveImageTask = Task.Run(() => Images.SaveImageFile(array));
            var formTask = HttpContext.Request.ReadFormAsync();

            var file = await saveImageTask.ConfigureAwait(false);
            var form = await formTask.ConfigureAwait(false);
            if (!form.TryGetValue("metadata", out var metadatavalue) || string.IsNullOrEmpty(metadatavalue))
            {
                return new ContentResult
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }
            CreateCustomAvatarItemResponse metadata = JsonConvert.DeserializeObject<CreateCustomAvatarItemResponse>(metadatavalue);
            if (metadata.Accessibility == 1)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Value = (string?)null,
                        ErrorId = "Rooms.DoesntExist",
                        Error = "Sorry, that option is not available yet. Switch to 'Just Me'"
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Value = metadata,
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }
        */

        // custom items the client's starter avatar wears that this server has no record of (seen 2026-10-13)
        internal static readonly string[] StarterCustomItemIds = { "32ffe4db-609c-4094-8ea0-0fc40a48f355", "77f44048-1c78-4f59-890f-cd0437153638" };
        private static JObject? _standInTemplate;
        internal static JObject? StandInCustomItem(string id)
        {
            try
            {
                if (_standInTemplate == null)
                {
                    string fp = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "FirstPartyUGC.json");
                    var results = JObject.Parse(System.IO.File.ReadAllText(fp))["Results"] as JArray;
                    _standInTemplate = results?.OfType<JObject>().FirstOrDefault(r => (int?)r["OutfitType"] == 0 && r["CurrentSaves"] is JArray sv && sv.Count > 0)
                                       ?? results?.OfType<JObject>().FirstOrDefault();
                }
                if (_standInTemplate == null) return null;
                var o = (JObject)_standInTemplate.DeepClone();
                o["CustomAvatarItemId"] = id;
                o["RankedEntityId"] = id;
                o["Name"] = "(Starter item)";
                o["Price"] = 0;
                o["Accessibility"] = 1;
                if (o["CurrentSaves"] is JArray saves) foreach (var sv in saves.OfType<JObject>()) sv["CustomAvatarItemId"] = id;
                return o;
            }
            catch (Exception ex) { Console.WriteLine($"[cai-bulk] stand-in failed: {ex.Message}"); return null; }
        }

        [HttpPost("/api/customAvatarItems/v1/bulk")]
        [HttpGet("/api/customAvatarItems/v1/bulk")]
        public IActionResult BulkCustomAvatarItems([FromForm] List<string>? customAvatarItemIds)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // The GET spelling (?customAvatarItemIds=a&customAvatarItemIds=b, or comma-joined) was
            // a 405 - it is how a client resolves an item it sees on SOMEONE ELSE's avatar.
            if (customAvatarItemIds == null || customAvatarItemIds.Count == 0)
            {
                customAvatarItemIds = new List<string>();
                foreach (var k in new[] { "customAvatarItemIds", "CustomAvatarItemIds", "ids", "id" })
                    foreach (var v in Request.Query[k])
                        foreach (var part in (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                            customAvatarItemIds.Add(part.Trim());
            }

            // Resolve the requested custom-item ids from the in-memory UGC index (was re-reading +
            // parsing customitems.json per call). This is the outfit-build lookup - an equipped UGC
            // item that resolves here renders instead of failing "customAvatarItemId not found".
            var items = CustomItemsCache.GetByIds(customAvatarItemIds ?? new List<string>());
            // LOCAL EXPERIMENT (HoldNewAccountCreation): the random avatar of a brand-new account names custom items this server has
            // no record of (not in UGC.json / FirstPartyUGC.json), the lookup misses, and the customize screen throws a
            // NullReferenceException on every retry. Answer each unknown id with a stand-in record so the load can finish.
            if (ConfigCampus.HoldNewAccountCreation && customAvatarItemIds != null && customAvatarItemIds.Count > 0 && customAvatarItemIds.Count < 12)
            {
                var have = new HashSet<string>(items.Select(i => ((string?)i["CustomAvatarItemId"] ?? "").ToLowerInvariant()));
                foreach (var missing in customAvatarItemIds.Where(i => !string.IsNullOrWhiteSpace(i) && !have.Contains(i.Trim().ToLowerInvariant())).Distinct())
                {
                    var stand = StandInCustomItem(missing.Trim());
                    if (stand != null) { items.Add(stand); Console.WriteLine($"[cai-bulk] {account.Id}: no record for {missing} - answered with a stand-in"); }
                }
            }
            if (ConfigCampus.LocalDev && (customAvatarItemIds?.Count ?? 0) < 12)
                Console.WriteLine($"[cai-bulk] {account.Id}: asked for {customAvatarItemIds?.Count ?? 0} id(s) [{string.Join(", ", customAvatarItemIds ?? new List<string>())}] -> {items.Count()} found");
            return new ContentResult
            {
                Content = new JArray(items).ToString(Newtonsoft.Json.Formatting.None),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Search published custom avatar items (reference: GET /api/customAvatarItems/v1/search).
        // Filters the custom-item catalog (customitems.json Results) and returns a BARE ARRAY,
        // newest first: searchQuery matches Name or Description (case-insensitive substring),
        // outfitTypes is a whitelist (none = no filter), minPrice/maxPrice bound Price inclusively,
        // skip/take page (take capped at 200), unpublished items (Accessibility 0) are left out.
        // Empty while no UGC items exist - the same answer the reference gives for an empty table.
        [HttpGet("/api/customAvatarItems/v1/search")]
        public IActionResult SearchCustomAvatarItems(
            [FromQuery] string? searchQuery,
            [FromQuery] List<int> outfitTypes,
            [FromQuery] int? minPrice,
            [FromQuery] int? maxPrice,
            [FromQuery] ulong? creatorAccountId,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50,
            [FromQuery] string? includeCoachItems = null,
            [FromQuery] int ordering = 0,
            [FromQuery] bool? isOwned = null,
            [FromQuery] bool? isFeatured = null)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            if (take > 1000) take = 1000;   // the creator-filtered path below; the broad search re-clamps to 200
            if (take < 0) take = 0;
            if (skip < 0) skip = 0;

            // creatorAccountId IS A REAL FILTER. It was not declared here at all, so ASP.NET
            // dropped it silently and every request that carried one got the WHOLE published
            // catalog back - 24k items. That is what put strangers' clothing on everybody's
            // profile: the Portfolio's Custom Clothing card is a StoreItemList bound to
            // PortfolioAccountId, and it asks for it with exactly this query:
            //
            //   /api/customAvatarItems/v1/search?creatorAccountId=295&includePurchaseInfos=True
            //       &includeCoachItems=False&skip=0&take=1000&...
            //
            // Answered from the SAME index as /v2/fromCreator/{id}, deliberately - not by
            // scanning All for a matching CreatorAccountId. The base catalog's creator ids are
            // the original RecNet ones (2830 of them literally say "1"), so a scan would hand
            // account 1 - and anyone whose id collided - thousands of items they never made.
            // CustomItemsCache only attributes items actually created on this server, and these
            // two routes must agree or the card and its "View More" page disagree.
            if (creatorAccountId.HasValue && creatorAccountId.Value != 0)
            {
                bool own = creatorAccountId.Value == account.Id;
                var byCreatorAll = CustomItemsCache.ByCreator(creatorAccountId.Value)
                    .Where(x => own || ((int?)x["Accessibility"] ?? 0) != 0)   // only the creator sees their unpublished drafts
                    .OrderByDescending(x => ((string?)x["CreatedAt"]) ?? "")
                    .ToList();
                if (creatorAccountId.Value == CustomItemsCache.CoachAccountId && ConfigCampus.ServeFirstPartyClothing)
                    byCreatorAll.AddRange(CustomItemsCache.FirstPartyRecs().Select(r => r.ToJObject()));
                var byCreator = byCreatorAll.Skip(skip).Take(take).ToList();

                return new ContentResult
                {
                    Content = new JArray(byCreator).ToString(),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            // Unfiltered store search. Left at the page size it has always served - the client
            // asks for 1000, but this one walks the whole 24k catalog and nothing needed the
            // bigger page before now.
            if (take > 200) take = 200;

            // From the in-memory UGC index (was re-parsing customitems.json every search).
            // Published only, and - while HiddenItemsStorefronts is on - no bundle-backed item:
            // those are the Clothing tab, and none of them load (CustomItemsCache.StoreListable).
            // Compact records all the way to the page: only the items actually returned are
            // materialised as JSON objects (CustomItemsCache.Rec).
            IEnumerable<CustomItemsCache.Rec> q = CustomItemsCache.AllRecs.Where(x => x.Accessibility != 0 && CustomItemsCache.StoreListable(x));

            // includeCoachItems picks a SIDE of the catalog, as on the reference: True (the store's
            // Clothing tab) = only the Coach's first-party stock, False (the Custom Clothing /
            // player-made tab) = only player-made items, absent = both. The client capitalises it.
            string coach = (includeCoachItems ?? "").Trim().ToLowerInvariant();
            if (coach == "true") q = q.Where(x => CustomItemsCache.IsFirstParty(x));
            else if (coach == "false") q = q.Where(x => !CustomItemsCache.IsFirstParty(x));

            if (outfitTypes != null && outfitTypes.Count > 0)
                q = q.Where(x => outfitTypes.Contains(x.OutfitType));
            if (minPrice.HasValue)
                q = q.Where(x => x.Price >= minPrice.Value);
            if (maxPrice.HasValue)
                q = q.Where(x => x.Price <= maxPrice.Value);
            if (isFeatured.HasValue)
                q = q.Where(x => x.Featured == isFeatured.Value);
            if (isOwned.HasValue)
            {
                // "Owned" is what /econ/customAvatarItems/v1/owned would list: the caller's own
                // creations, plus the imported set for a dev. The Filters dialog sends
                // isOwned=False to hide what the player already has.
                var owned = new HashSet<string>(CustomItemsCache.ByCreatorRecs(account.Id).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
                if (PlayerDB.CheckDevFlag(account.Id))
                    owned.UnionWith(CustomItemsCache.ImportedIds);
                q = q.Where(x => owned.Contains(x.Id) == isOwned.Value);
            }

            // Ranked: what was typed is matched word by word against the name (and, weakly, the
            // description) and the best matches come first - see CustomItemSearch. Was a raw
            // substring test in date order, which is why "eyepatches" found nothing and "eye"
            // led with a belt.
            var page = Campus_live_rewrite.api.server.CustomItemSearch.Search(q, searchQuery, ordering)
                .Skip(skip).Take(take)
                .Select(r => r.ToJObject())
                .ToList();

            return new ContentResult
            {
                Content = new JArray(page).ToString(),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Report a custom avatar item (reference: POST /api/customAvatarItems/v1/{id}/report).
        // Accepts the report and answers the {success,error} envelope. We don't persist UGC reports
        // yet (no UGC items exist); a moderator flow would file it like a player report.
        [HttpPost("/api/customAvatarItems/v1/{id}/report")]
        public IActionResult ReportCustomAvatarItem(string id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = true, error = "" }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // One Maker-Pen circuit-chip palette (reference: GET /api/CircuitChipLists/{list}).
        // Always empty - nothing records which chips a player has used/favourited - and an unknown
        // name is empty too rather than a 404 (a 404 renders as a palette that failed to load).
        [HttpGet("/api/CircuitChipLists/{list}")]
        public ContentResult CircuitChipList(string list)
            => new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };

        // Discord-presence room lookup. Returns a room's display NAME and whether it's PRIVATE -
        // the SAME source the admin panel uses (the live heartbeat instance: name + isPrivate),
        // falling back to the static room. The patch's presence calls this so it works for DORMS
        // (which the static rooms API doesn't describe the same way) and can label a private room
        // as [PRIVATE]. No JWT (the patch's presence fetch is unauthenticated) - it carries the
        // X-Campus-Patch header, so the patch gate lets it through, and it exposes nothing but a
        // room name + a private flag.
        // Discord-presence SELF lookup - everything the patch needs to decide what to show, from
        // the caller's OWN heartbeat: their current room (name + isPrivate instance flag), that
        // room's accessibility (to tell a dev/unpublished map apart from a normal room), and
        // whether they appear online to EVERYONE. If "Appear Online To" isn't All (statusVisibility
        // 1 Friends / 2 Favorites / 3 No One), appearOnlineToAll is false and the patch hides the
        // room. accessibility is -1 when the room isn't a known published room (dev/unpublished).
        // No JWT (the patch's presence fetch is unauthenticated) - it carries X-Campus-Patch.
        [HttpGet("/api/presence/self/{accountId}")]
        public IActionResult PresenceSelf(ulong accountId)
        {
            var (room, statusVisibility) = HeartbeatDB.GetSelfPresence(accountId);
            // Hide only for the explicit non-everyone choices; anything else (0 Everyone, or a
            // legacy/default value) counts as "appear to all".
            bool appearOnlineToAll = !(statusVisibility == 1 || statusVisibility == 2 || statusVisibility == 3);

            if (room == null)
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { found = false, appearOnlineToAll }),
                    ContentType = "application/json",
                    StatusCode = 200
                };

            int accessibility = -1;
            var r = RoomDB.GetRoom((ulong)room.roomId);
            if (r != null) accessibility = (int)r.Accessibility;

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    found = true,
                    name = room.name ?? "",
                    isPrivate = room.isPrivate,
                    accessibility,
                    appearOnlineToAll
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/presence/room/{roomId}")]
        public IActionResult PresenceRoom(ulong roomId)
        {
            var inst = HeartbeatDB.FindInstanceByRoomId((long)roomId);
            if (inst != null)
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { found = true, name = inst.name ?? "", isPrivate = inst.isPrivate }),
                    ContentType = "application/json",
                    StatusCode = 200
                };

            var room = RoomDB.GetRoom(roomId);
            if (room != null)
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { found = true, name = room.Name ?? "", isPrivate = room.Accessibility != RoomAccessibility.Public }),
                    ContentType = "application/json",
                    StatusCode = 200
                };

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { found = false, name = "", isPrivate = false }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Purchase info for a bag of items (reference: POST /api/items/purchaseInfos).
        // Resolves Ids[] ({itemType,itemId}) against the custom-item catalog and answers how each
        // may be bought. Only itemType 3 (custom avatar item) is served; other types and unknown
        // ids are dropped, so the response is one entry per RESOLVED id in request order.
        [HttpPost("/api/items/purchaseInfos")]
        public async Task<IActionResult> ItemPurchaseInfos()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            // Read the body ourselves: MVC's System.Text.Json formatter cannot bind a JObject
            // parameter (it threw on "$.Ids" -> a 500 on every purchase-info lookup).
            JObject? body = null;
            try
            {
                using var reader = new StreamReader(Request.Body);
                string raw = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(raw)) body = JObject.Parse(raw);
            }
            catch (Exception ex) { Console.WriteLine($"[purchaseInfos] {account.Id}: bad body - {ex.Message}"); }

            var outList = new JArray();
            var ids = body?["Ids"] as JArray;
            if (ids != null && ids.Count > 0)
            {

                foreach (var idRef in ids)
                {
                    int itemType = (int?)idRef["itemType"] ?? -1;
                    string itemId = ((string?)idRef["itemId"]) ?? "";
                    if (itemType != 3 || string.IsNullOrEmpty(itemId)) continue; // only custom avatar items
                    var item = CustomItemsCache.Get(itemId);
                    if (item == null) continue; // unknown id dropped
                    int price = (int?)item["Price"] ?? 0;
                    outList.Add(new JObject
                    {
                        ["ItemId"] = new JObject { ["itemType"] = itemType, ["itemId"] = itemId },
                        ["PurchaseMethodId"] = new JObject { ["Type"] = 1, ["NumberId"] = null, ["Guid"] = itemId },
                        ["Prices"] = new JArray { new JObject {
                            // 2 = RecCenterTokens. This said 1 with a "RecCenterTokens" comment,
                            // and 1 is LaserTagTickets (Classes/CurrencyType.cs) - so every custom
                            // item was priced in the wrong currency and a buy tried to debit a
                            // balance nobody has. Reference: toItemPurchaseInfo.
                            ["CurrencyType"] = (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens,
                            ["Price"] = price,
                            ["StorefrontSaleData"] = new JObject { ["SalePercent"] = 0, ["SaleStartDate"] = null, ["SaleEndDate"] = null }
                        }},
                        ["NewUntil"] = null,
                        ["AvailableAt"] = null,
                        ["AvailableUntil"] = null,
                        ["CanBeGifted"] = true,
                        ["CanApplySubscriberDiscount"] = false,
                        ["SubscribersOnly"] = false,
                        ["IsFeatured"] = false
                    });
                }
            }
            return new ContentResult { Content = outList.ToString(), ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("/api/customAvatarItems/v1/featured")]
        public IActionResult FeaturedCustomAvatarItems()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Featured UGC only, from the in-memory index (was streaming the ENTIRE catalog file -
            // fine when it was empty, an 87MB response now). Capped, published only, newest first.
            var featured = CustomItemsCache.AllRecs
                .Where(x => x.Featured && x.Accessibility != 0 && CustomItemsCache.StoreListable(x))
                .OrderByDescending(x => x.Created)
                .Take(100)
                .Select(r => r.ToJObject())
                .ToList();

            // BARE ARRAY - /v1/search, /v1/featured and /v1/bulk all deserialize into a
            // List<CustomAvatarItem>; a {Results,TotalResults} object raises the client's
            // Malformed Response throw and the featured row renders empty (wire-shapes:
            // customavataritems-routes).
            return new ContentResult
            {
                Content = new JArray(featured).ToString(Newtonsoft.Json.Formatting.None),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/testcasemanagement/v1/{route}")]
        public ContentResult TestCaseManagement(string route)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (route == "testplans")
            {
                return new ContentResult()
                {
                    Content = "[]", // todo db
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            if (route == "testpasssummary")
            {
                return new ContentResult()
                {
                    Content = "[]", // todo db
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            return new ContentResult()
            {
                Content = "[]", // todo db
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/playerevents/v1/searchlive")]
        public ContentResult SearchLivePlayerEvents()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Was a hardcoded "[]" marked "todo db", so the live-events list was always empty.
            // REVERT: return Content = "[]" here to restore that.
            var live = EventDB.GetLiveEvents();
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(live.Select(EventWire)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/messages/v2/get")]
        public ContentResult GetMessages()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Room-role invites that were already answered or withdrawn: drop their notices (they
            // used to stay in the list for good, Accept / Decline and all).
            try
            {
                var staleRooms = PlayerDB.GetMessages(account.Id)
                    .Where(m => m.Type == WebsocketEvents.MessageType.RoomCoOwnerInvited && m.RoomId.HasValue
                                && RoomDB.PendingRoleInvite(m.RoomId.Value, (long)account.Id) == RoomDBClasses.Role_data.None)
                    .Select(m => m.RoomId!.Value).Distinct().ToList();
                foreach (var r in staleRooms) PlayerDB.DeleteRoomRoleMessages(account.Id, r);
            }
            catch (Exception ex) { Console.WriteLine($"[messages] stale role-invite sweep failed: {ex.Message}"); }

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(PlayerDB.GetMessages(account.Id)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Send one message to another player - the "invite me!" / party-invite prompts the
        // client fires. Mirrors RecFlare's POST /api/messages/v2/send: FromPlayerId is the
        // token's subject (never a body field), the recipient gets a MessageReceived frame
        // (inner Id "2"), and the body is the { success, error } envelope. Type defaults to 0
        // and Data to "". An offline recipient simply isn't delivered (SendToPlayer no-ops);
        // that is still a success, since there's nothing to fail.
        [HttpPost("/api/messages/v2/send")]
        public async Task<IActionResult> SendMessageToPlayer([FromForm] ulong ToPlayerId, [FromForm] int Type = 0, [FromForm] string? Data = null)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (ToPlayerId == 0)
                return BadRequest(new { success = false, error = "ToPlayerId is required" });

            await PushPlayerMessage(account.Id, ToPlayerId, Type, Data ?? "");
            return Ok(new { success = true, error = "" });
        }

        // The bulk form: one message to several recipients (JSON body
        // {"ToPlayerIds":[..],"Type":..,"Data":".."}). Ids are de-duplicated and non-positive
        // ones dropped; each recipient gets its own frame. Mirrors RecFlare's
        // POST /api/messages/v1/sendMultiple.
        [HttpPost("/api/messages/v1/sendMultiple")]
        public async Task<IActionResult> SendMultipleMessages([FromBody] SendMultipleMessagesRequest? body)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var ids = (body?.ToPlayerIds ?? new List<ulong>())
                .Where(i => i > 0)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
                return BadRequest(new { success = false, error = "ToPlayerIds is required" });

            foreach (var toId in ids)
                await PushPlayerMessage(account.Id, toId, body!.Type, body.Data ?? "");

            return Ok(new { success = true, error = "" });
        }

        // Push a single MessageReceived frame from one player to another. Shared by the single
        // and bulk send endpoints. Not stored (unlike cheers) - the notification IS the delivery,
        // matching the reference; a hub hiccup is logged, not surfaced, since there's no store to
        // reconcile against.
        private static async Task PushPlayerMessage(ulong fromPlayerId, ulong toPlayerId, int type, string data)
        {
            var msg = new MessageData
            {
                Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FromPlayerId = fromPlayerId,
                SentTime = DateTime.UtcNow,
                Type = (WebsocketEvents.MessageType)type,
                Data = data,
                RoomId = null,
                PlayerEventId = null
            };
            try
            {
                var frame = JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(msg));
                await Notifications.SendToPlayer(toPlayerId, frame);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[messages/send] push to {toPlayerId} failed: {ex.Message}");
            }
        }

        // A cheer (wire-shapes playercheer-create; reference recflare api progression.ts).
        //  * The cheered player's counter goes up in the cheer's category - it never did before.
        //  * What PLAYS the cheer on their client is a Message of type 50 PlayerCheer (51
        //    PlayerCheerAnonymous, FromPlayerId 0 - an anonymous cheer used to carry the sender)
        //    with Data = the category; ReputationUpdate only refreshes the numbers. A cheer happens
        //    in front of people, so the cheered player's ReputationUpdate goes to everyone in the
        //    giver's room instance (from presence, not the body's RoomId), and the giver gets their
        //    own (their CheerCredit moved).
        //  * The reply is a BARE {Success, Message}, Message null on success - not the
        //    {success, error, value} envelope. Refusals are Success=false with a readable Message.
        [HttpPost("/api/PlayerCheer/v1/create")]
        public async Task<IActionResult> SendCheer([FromForm] ulong PlayerIdTo, [FromForm] int CheerCategory, [FromForm] ulong RoomId, [FromForm] bool Anonymous)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            IActionResult Result(string? why) =>
                Content(JsonConvert.SerializeObject(new { Success = why == null, Message = why }), "application/json");

            if (PlayerIdTo == account.Id) return Result("You cannot cheer yourself!");
            if (PlayerIdTo == 0 || PlayerDB.GetAccountById(PlayerIdTo) == null) return Result("That player couldn't be found.");
            // checked BEFORE the credit is spent: a category we can't count would take a cheer for nothing
            if (!PlayerDB.IsCheerCategory(CheerCategory)) return Result("That isn't a cheer.");
            if (account.PlayerReputation.CheerCredit <= 0) return Result("You have no more cheers today.");

            await Task.Run(() => PlayerDB.SetCheerCredit(account.PlayerReputation.CheerCredit - 1, account.Id));
            PlayerDB.AddCheer(PlayerIdTo, CheerCategory);

            var cheerType = Anonymous ? Campus_live_rewrite.api.WebsocketEvents.MessageType.PlayerCheerAnonymous
                                      : Campus_live_rewrite.api.WebsocketEvents.MessageType.PlayerCheer;
            var newMsg = PlayerDB.AddMessage(
                toPlayerId: PlayerIdTo,
                fromPlayerId: Anonymous ? 0 : account.Id,
                type: cheerType,
                data: CheerCategory.ToString(),
                roomId: RoomId,
                playerEventId: null
            );
            try
            {
                if (newMsg != null)
                    await Notifications.SendToPlayer(PlayerIdTo, JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(newMsg)));

                string cheered = JsonConvert.SerializeObject(WebsocketEvents.CreateResponseReputationUpdate(PlayerIdTo));
                var audience = new HashSet<ulong> { PlayerIdTo };
                long instanceId = HeartbeatDB.PeekPresence(account.Id).room?.roomInstanceId ?? 0;
                foreach (var id in HeartbeatDB.PlayerIdsInInstance(instanceId)) audience.Add(id);
                await Notifications.SendToPlayers(audience.ToList(), cheered);
                await Notifications.SendToPlayer(account.Id, JsonConvert.SerializeObject(WebsocketEvents.CreateResponseReputationUpdate(account.Id)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cheer] notify failed: {ex.Message}");
            }
            Console.WriteLine($"[cheer] {account.Id} -> {PlayerIdTo} category {CheerCategory}{(Anonymous ? " (anonymous)" : "")}");
            return Result(null);
        }

        // Body is {"MessageIds":[long,...]} (wire-shapes: messages-v3-delete); the answer is never
        // read. [FromBody] bound through MVC's formatter and answered 400 on every call (21 today),
        // so a message the player deleted came straight back on the next refetch. Read by hand.
        [HttpPost("/api/messages/v3/delete")]
        public async Task<IActionResult> DeleteMessages()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var ids = new List<ulong>();
            try
            {
                if (Request.HasFormContentType)
                {
                    foreach (var k in new[] { "MessageIds", "messageIds", "ids", "id" })
                        foreach (var v in Request.Form[k])
                            foreach (var part in (v ?? "").Split(','))
                                if (ulong.TryParse(part.Trim(), out var n)) ids.Add(n);
                }
                else
                {
                    using var reader = new StreamReader(Request.Body);
                    string raw = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var tok = JToken.Parse(raw);
                        var arr = tok is JArray ja ? ja : (tok["MessageIds"] ?? tok["messageIds"] ?? tok["ids"]) as JArray;
                        if (arr != null) foreach (var t in arr) if (ulong.TryParse(t.ToString(), out var n)) ids.Add(n);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[messages] delete: unreadable body - {ex.Message}"); }

            foreach (var id in ids) PlayerDB.DeleteMessage(account.Id, id);
            SiteNotifications.Notify(account.Id);   // game deleted -> website inbox refreshes
            if (ids.Count > 0) Console.WriteLine($"[messages] {account.Id} deleted {ids.Count} message(s)");
            return NoContent();
        }

        [HttpGet("/api/playerevents/v1/room/{RoomId}")]
        public ContentResult PlayerEventsForRoom(ulong RoomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Was a hardcoded "[]" marked "todo db", so a room never listed the events booked
            // in it. REVERT: return Content = "[]" here to restore that.
            var events = EventDB.GetEventsForRoom(RoomId);
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(events.Select(EventWire)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        /// <summary>
        /// The wire shape for a player event.
        ///
        /// Every event listing built its own anonymous object and they disagreed: none of them
        /// sent Tags - so the tag filters from /api/playerevents/v1/tagfilters had nothing to
        /// match on and filtering by tag always came back empty - and /all always sent an empty
        /// Responses list, so nobody's RSVP was ever visible. One shape, used everywhere.
        /// </summary>
        private static object EventWire(FullPlayerEvent e) => new
        {
            e.PlayerEventId,
            e.CreatorPlayerId,
            e.ImageName,
            e.RoomId,
            e.SubRoomId,
            e.ClubId,
            e.Name,
            e.Description,
            e.StartTime,
            e.EndTime,
            e.AttendeeCount,
            e.State,
            e.Accessibility,
            e.IsMultiInstance,
            e.SupportMultiInstanceRoomChat,
            e.DefaultBroadcastPermissions,
            e.CanRequestBroadcastPermissions,
            e.BroadcastingRoomInstanceId,
            e.RecurrenceSchedule,
            Tags = e.Tags ?? new List<FullTag>(),
            Responses = e.Responses ?? new List<FullEventResponses>()
        };

        // GET /api/playerevents/v1/bulk?id=1&id=2 - the events behind ids the client already
        // holds. This is what the EVENT DETAIL PAGE fetches when you open an event; it was
        // missing (only bulkInvite existed), so the detail page rendered blank. Bare array, in
        // request order, skipping ids with no event.
        [HttpGet("/api/playerevents/v1/bulk")]
        public ContentResult PlayerEventsBulk()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var ids = Request.Query["id"]
                .Select(s => ulong.TryParse(s, out var v) ? v : 0UL)
                .Where(v => v != 0)
                .ToList();

            var list = new List<object>();
            foreach (var id in ids)
            {
                var e = EventDB.GetFullEventById(id);
                if (e != null) list.Add(EventWire(e));
            }
            Console.WriteLine($"[events] bulk fetch {ids.Count} ids -> {list.Count} events");
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(list),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // GET /api/playerevents/v1/{EventId} - a SINGLE event (the event detail page fetches
        // this; it was 404ing, so the page rendered blank with only the RSVP bar). Bare event
        // object (EventWire), 404 if unknown. {EventId:long} so literal siblings (all/search/
        // clubs/bulk/respond/report) still win.
        [HttpGet("/api/playerevents/v1/{EventId:long}")]
        public ContentResult GetSinglePlayerEvent(ulong EventId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var ev = EventDB.GetFullEventById(EventId);
            if (ev == null)
                return new ContentResult { Content = "{}", ContentType = "application/json", StatusCode = 404 };
            return new ContentResult { Content = JsonConvert.SerializeObject(EventWire(ev)), ContentType = "application/json", StatusCode = 200 };
        }

        // GET /api/playerevents/v1/clubs - events across the caller's clubs (bare array, empty).
        [HttpGet("/api/playerevents/v1/clubs")]
        public ContentResult PlayerEventsForMyClubs() =>
            new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };

        // GET /api/playerevents/v1/club/{clubId} - one club's events (paged envelope, empty page).
        [HttpGet("/api/playerevents/v1/club/{clubId:long}")]
        public ContentResult PlayerEventsForClub(long clubId) =>
            new ContentResult { Content = "{\"ContinuationToken\":\"\",\"Events\":[]}", ContentType = "application/json", StatusCode = 200 };

        [HttpGet("/api/playerevents/v1/tagfilters")]
        public ContentResult PlayerEventsTagFilters()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var tagfilters = new
            {
                PinnedFilters = new[]
               {
                    "workshops",
                    "celebration",
                    "game",
                    "meetup",
                    "performance",
                    "coop",
                    "grandopening",
                    "class",
                    "competition"
                },
                PopularFilters = new[]
               {
                    "workshops",
                    "celebration",
                    "class",
                    "coop",
                    "competition",
                    "game",
                    "grandopening",
                    "meetup",
                    "performance"
                },
                TrendingFilters = (string?)null
            };

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(tagfilters),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/api/playerevents/v2")]
        public ContentResult CreatePlayerEvent([FromBody] EventDBClasses.CreateEventRequest request)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Event names and descriptions are player-authored text shown to everyone, so they
            // go through the same filter as room names, usernames and bios. They were the only
            // player-authored strings on the server with no check at all - which is how
            // "come rape childeren with me" ended up listed as a public event.
            //
            // REVERT: ConfigCampus.AllowSwears = true disables this along with every other
            // text filter (see Sanitize.ContainsSwears).
            if (!string.IsNullOrWhiteSpace(request?.Name) && Sanitize.ContainsSwears(request.Name))
            {
                Console.WriteLine($"[events] REFUSED create by {account.Id}: name failed the filter");
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { Result = EventResults.InappropriateName, TagModifyResult = (string?)null, PlayerEvent = (object?)null }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (!string.IsNullOrWhiteSpace(request?.Description) && Sanitize.ContainsSwears(request.Description))
            {
                Console.WriteLine($"[events] REFUSED create by {account.Id}: description failed the filter");
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { Result = EventResults.InappropriateDescription, TagModifyResult = (string?)null, PlayerEvent = (object?)null }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            FullPlayerEvent NewlyCreatedEvent = EventDB.CreateEvent(account.Id, request);
            Console.WriteLine($"[events] {account.Id} created event {NewlyCreatedEvent.PlayerEventId} " +
                              $"'{NewlyCreatedEvent.Name}' in room {NewlyCreatedEvent.RoomId}");
            // PlayerEventCreated (80) to the creator: the event appears on their screen without a refetch.
            PushEventFrame(new[] { account.Id }, WebsocketEvents.ResponseResults.PlayerEventCreated, EventNotification(NewlyCreatedEvent));

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    Result = EventResults.Success,
                    TagModifyResult = (string?)null,
                    PlayerEvent = new
                    {
                        Tags = NewlyCreatedEvent.Tags,
                        PlayerEventId = NewlyCreatedEvent.PlayerEventId,
                        CreatorPlayerId = NewlyCreatedEvent.CreatorPlayerId,
                        ImageName = NewlyCreatedEvent.ImageName,
                        RoomId = NewlyCreatedEvent.RoomId,
                        SubRoomId = NewlyCreatedEvent.SubRoomId,
                        ClubId = NewlyCreatedEvent.ClubId,
                        Name = NewlyCreatedEvent.Name,
                        Description = NewlyCreatedEvent.Description,
                        StartTime = NewlyCreatedEvent.StartTime,
                        EndTime = NewlyCreatedEvent.EndTime,
                        AttendeeCount = NewlyCreatedEvent.AttendeeCount,
                        State = NewlyCreatedEvent.State,
                        ImageAccessibility = NewlyCreatedEvent.Accessibility,
                        IsMultiInstance = NewlyCreatedEvent.IsMultiInstance,
                        SupportMultiInstanceRoomChat = NewlyCreatedEvent.SupportMultiInstanceRoomChat,
                        DefaultBroadcastPermissions = NewlyCreatedEvent.DefaultBroadcastPermissions,
                        CanRequestBroadcastPermissions = NewlyCreatedEvent.CanRequestBroadcastPermissions,
                        RecurrenceSchedule = NewlyCreatedEvent.RecurrenceSchedule
                    }
                }), // todo db
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/playerevents/v1/{EventId}/responses")]
        public ContentResult PlayerEventResponsesForEventId(ulong EventId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var playerEvent = EventDB.GetFullEventById(EventId);

            if (playerEvent == null)
            {
                return new ContentResult
                {
                    Content = "[]",
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(playerEvent.Responses ?? new List<FullEventResponses>()),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        public class EventRespondBody { public ulong PlayerEventId { get; set; } public int Type { get; set; } }

        // POST /api/playerevents/v1/respond  {PlayerEventId, Type}  (Type: 0 Going, 1 Interested,
        // 2 Can't go). RSVP to an event - the endpoint was missing, so "I'm going" did nothing
        // (EventDB.SetResponse already existed, nothing called it). One row per player per event;
        // only Going counts toward AttendeeCount. Answers the same envelope create does so the
        // client re-renders the event from it.
        [HttpPost("/api/playerevents/v1/respond")]
        public ContentResult RespondToPlayerEvent()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            // Form, query or JSON - the client's param helper posts a form body, and [FromBody]
            // answered that with a 415 before anything ran.
            var f = RoomModeration.Fields(Request);
            var body = new EventRespondBody
            {
                PlayerEventId = RoomModeration.ULong(f, "PlayerEventId", "EventId", "Id"),
                Type = (int)RoomModeration.Long(f, "Type", "Response", "ResponseType")
            };
            string typeText = RoomModeration.Str(f, "Type", "Response", "ResponseType") ?? "";
            if (body.Type == 0 && typeText.Length > 0 && !char.IsDigit(typeText[0]))
                body.Type = typeText.ToLowerInvariant() switch { "yes" or "going" or "attending" => 0, "interested" or "maybe" => 1, "no" or "notgoing" or "declined" => 2, _ => 0 };

            if (body.PlayerEventId == 0 || body.Type < 0 || body.Type > 2)
                return new ContentResult { Content = "{}", ContentType = "application/json", StatusCode = 400 };

            // client Type 0/1/2 == server enum Yes/Interested/No (0/1/2)
            var updated = EventDB.SetResponse(body.PlayerEventId, account.Id, (EventDBClasses.PlayerEventResponseType)body.Type, out var responseRow);
            if (updated == null)
                return new ContentResult { Content = "{}", ContentType = "application/json", StatusCode = 404 };
            // PlayerEventResponseChanged (83): what populates the event on the responder's client
            // (an invitee answering Pending -> Going included). BOTH nested objects are required -
            // the client's handlers dereference one level down with no null guard.
            if (responseRow != null)
                PushEventFrame(new[] { account.Id }, WebsocketEvents.ResponseResults.PlayerEventResponseChanged,
                    new { PlayerEvent = EventNotification(updated), PlayerEventResponse = EventResponseDto(responseRow) });

            Console.WriteLine($"[events] {account.Id} responded {body.Type} to event {body.PlayerEventId}");
            return EventEnvelope(updated);
        }

        // POST /api/playerevents/v1/bulkInvite {PlayerEventId, InvitedPlayerIds[]} - the "invite
        // friends" button on an event. Was a stub that acked and invited nobody. Now (reference):
        //   * each invitee gets a PENDING (3) response row - an open invitation, not an acceptance,
        //     so they don't count toward AttendeeCount until they answer through /respond;
        //   * an invite never overwrites an answer, and re-inviting is a no-op;
        //   * each NEWLY invited player is sent the invitation as a Message of type 81
        //     PlayerEventInvitation from the caller (stored in their inbox, pushed as
        //     MessageReceived), the event id on PlayerEventId and, as a string, in Data. No
        //     PlayerEventResponseChanged on invite - that frame is theirs once they answer;
        //   * the caller must be on the event (its creator, or anyone with a response row): 403
        //     otherwise. The caller and duplicate ids are skipped.
        // Answers the v2 envelope (attendee count unchanged by an invite).
        [HttpPost("/api/playerevents/v1/bulkInvite")]
        public async Task<ContentResult> BulkInviteToEvent()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            ulong eventId = 0; var invited = new List<ulong>();
            try
            {
                Request.EnableBuffering();
                string body;
                using (var sr = new StreamReader(Request.Body, leaveOpen: true)) body = await sr.ReadToEndAsync();
                Request.Body.Position = 0;
                if (body.TrimStart().StartsWith("{"))
                {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(body);
                    eventId = (ulong?)(j["PlayerEventId"] ?? j["playerEventId"]) ?? 0;
                    if ((j["InvitedPlayerIds"] ?? j["invitedPlayerIds"]) is Newtonsoft.Json.Linq.JArray arr)
                        foreach (var t in arr) if (ulong.TryParse(t.ToString(), out var id)) invited.Add(id);
                }
                else if (Request.HasFormContentType)
                {
                    var form = await Request.ReadFormAsync();
                    ulong.TryParse(form["PlayerEventId"].ToString(), out eventId);
                    foreach (var v in form["InvitedPlayerIds"].Concat(form["InvitedPlayerIds[]"]))
                        foreach (var part in (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (ulong.TryParse(part, out var id)) invited.Add(id);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[events] bulkInvite body unreadable: {ex.Message}"); }
            if (eventId == 0 || invited.Count == 0)
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 400 };

            var ev = EventDB.GetFullEventById(eventId);
            if (ev == null) return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
            bool onEvent = ev.CreatorPlayerId == account.Id || (ev.Responses?.Any(r => r.PlayerId == account.Id) ?? false);
            if (!onEvent) return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 403 };

            var updated = EventDB.InviteToEvent(eventId, invited.Where(id => id != account.Id), out var added)!;
            foreach (var pid in added)
            {
                try
                {
                    var msg = PlayerDB.AddMessage(pid, account.Id, WebsocketEvents.MessageType.PlayerEventInvitation, eventId.ToString(), null, eventId);
                    if (msg != null) Notifications.FireAndForget(Notifications.SendToPlayer(pid, JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(msg))), $"event invite -> {pid}");
                }
                catch (Exception ex) { Console.WriteLine($"[events] invitation message to {pid} failed: {ex.Message}"); }
            }
            Console.WriteLine($"[events] {account.Id} invited {added.Count} player(s) to event {eventId} ({invited.Count} asked)");
            return EventEnvelope(updated);
        }

        // POST /api/playerevents/v1/report  {PlayerEventId, ReportCategory, Details}
        // Files a report against an event (reported player = the event's creator). Answers the
        // {success,error} envelope. Was missing -> reporting an event 404'd.
        public class EventReportBody { public ulong PlayerEventId { get; set; } public int ReportCategory { get; set; } public string? Details { get; set; } }

        [HttpPost("/api/playerevents/v1/report")]
        public ContentResult ReportPlayerEvent([FromBody] EventReportBody? body)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            if (body == null || body.PlayerEventId == 0)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, error = "PlayerEventId is required" }), ContentType = "application/json", StatusCode = 400 };

            var ev = EventDB.GetFullEventById(body.PlayerEventId);
            if (ev == null)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { success = false, error = "No such event" }), ContentType = "application/json", StatusCode = 404 };

            Console.WriteLine($"[events] {account.Id} reported event {body.PlayerEventId} (creator {ev.CreatorPlayerId}, category {body.ReportCategory})");
            return new ContentResult { Content = JsonConvert.SerializeObject(new { success = true, error = "" }), ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("/api/playerevents/v1")]
        public ContentResult GetAllEvents()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var events = EventDB.SearchEventByQuery("");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(events.Select(e => new
                {
                    e.PlayerEventId,
                    e.CreatorPlayerId,
                    e.ImageName,
                    e.RoomId,
                    e.SubRoomId,
                    e.ClubId,
                    e.Name,
                    e.Description,
                    e.StartTime,
                    e.EndTime,
                    e.AttendeeCount,
                    e.State,
                    e.Accessibility,
                    e.IsMultiInstance,
                    e.SupportMultiInstanceRoomChat,
                    e.DefaultBroadcastPermissions,
                    e.CanRequestBroadcastPermissions,
                    e.BroadcastingRoomInstanceId,
                    e.RecurrenceSchedule
                })),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/playerevents/v1/all")]
        public ContentResult GetAllEvents2()
        {
            // Auth stays optional here (it was commented out and the client relies on that),
            // but the caller is resolved when a token IS present so their own RSVPs can be
            // returned. No token simply means no personal responses.
            ulong viewerId = 0;
            try
            {
                var (acc, err) = AuthHelper.ValidateToken(HttpContext);
                if (err == null && acc != null) viewerId = acc.Id;
            }
            catch { }

            var events = EventDB.SearchEventByQuery("");

            return new ContentResult
            {
                // Created now carries Tags (so the tag filters work) and each event's real
                // Responses. Responses at the top level is every response the caller has made,
                // which is what the client uses to show "you are attending".
                Content = JsonConvert.SerializeObject(new
                {
                    Created = events.Select(EventWire),
                    Responses = events
                        .SelectMany(e => e.Responses ?? new List<FullEventResponses>())
                        .Where(r => r.PlayerId == viewerId)
                        .ToList(),
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // GET /api/playerevents/v1/all/{playerId} - the same {Created, Responses} answer as /all, for ONE player (a profile's events): what they
        // created, and the RSVPs they made. The client asks for this when it opens someone's profile (35 x 404 in two days). The id is a player id
        // (the same parameter the client's ownership checks call "player").
        [HttpGet("/api/playerevents/v1/all/{playerId:long}")]
        public ContentResult GetEventsOfPlayer(long playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            ulong pid = playerId > 0 ? (ulong)playerId : 0UL;
            var created = pid == 0 ? new List<FullPlayerEvent>() : EventDB.GetEventsByCreator(pid);
            var responses = pid == 0 ? new List<FullEventResponses>() : EventDB.SearchEventByQuery("")
                .SelectMany(e => e.Responses ?? new List<FullEventResponses>())
                .Where(r => r.PlayerId == pid)
                .ToList();
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { Created = created.Select(EventWire), Responses = responses }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/api/playerevents/v1/search")]
        public ContentResult SearchEvents(string query = "", string sort = "Attendance", string scheduleFilter = "Upcoming")
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var events = EventDB.SearchEventByQuery(query);

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(events.Select(e => new
                {
                    e.PlayerEventId,
                    e.CreatorPlayerId,
                    e.ImageName,
                    e.RoomId,
                    e.SubRoomId,
                    e.ClubId,
                    e.Name,
                    e.Description,
                    e.StartTime,
                    e.EndTime,
                    e.AttendeeCount,
                    e.State,
                    e.Accessibility,
                    e.IsMultiInstance,
                    e.SupportMultiInstanceRoomChat,
                    e.DefaultBroadcastPermissions,
                    e.CanRequestBroadcastPermissions,
                    e.BroadcastingRoomInstanceId,
                    e.RecurrenceSchedule
                })),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("/api/playerevents/v2/{EventId}/name")]   // was missing the /api prefix -> client 404'd every rename
        public ContentResult SetEventName(ulong EventId, [FromForm] string name)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            FullPlayerEvent playerEvent = EventDB.GetFullEventById(EventId);

            if (playerEvent == null)
            {
                Utils.ErrorEvent(EventResults.DoesNotExist);
            }

            if (playerEvent.CreatorPlayerId != account.Id)
            {
                Utils.ErrorEvent(EventResults.DoesNotOwnEvent);
            }

            if (string.IsNullOrEmpty(name) || name.Length > 64)
            {
                return Utils.ErrorEvent(EventResults.InappropriateName);
            }

            if (Sanitize.ContainsSwears(name))
            {
                return Utils.ErrorEvent(EventResults.InappropriateName);
            }

            FullPlayerEvent UpdatedEvent = EventDB.SetEventName(EventId, name);
            if (UpdatedEvent != null)
                PushEventFrame(EventDB.GoingPlayerIds(UpdatedEvent), WebsocketEvents.ResponseResults.PlayerEventUpdated, EventNotification(UpdatedEvent));
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    Result = EventResults.Success,
                    TagModifyResult = (string?)null,
                    PlayerEvent = new
                    {
                        Tags = UpdatedEvent.Tags,
                        PlayerEventId = UpdatedEvent.PlayerEventId,
                        CreatorPlayerId = UpdatedEvent.CreatorPlayerId,
                        ImageName = UpdatedEvent.ImageName,
                        RoomId = UpdatedEvent.RoomId,
                        SubRoomId = UpdatedEvent.SubRoomId,
                        ClubId = UpdatedEvent.ClubId,
                        Name = UpdatedEvent.Name,
                        Description = UpdatedEvent.Description,
                        StartTime = UpdatedEvent.StartTime,
                        EndTime = UpdatedEvent.EndTime,
                        AttendeeCount = UpdatedEvent.AttendeeCount,
                        State = UpdatedEvent.State,
                        ImageAccessibility = UpdatedEvent.Accessibility,
                        IsMultiInstance = UpdatedEvent.IsMultiInstance,
                        SupportMultiInstanceRoomChat = UpdatedEvent.SupportMultiInstanceRoomChat,
                        DefaultBroadcastPermissions = UpdatedEvent.DefaultBroadcastPermissions,
                        CanRequestBroadcastPermissions = UpdatedEvent.CanRequestBroadcastPermissions,
                        RecurrenceSchedule = UpdatedEvent.RecurrenceSchedule
                    }
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // ---- live event frames (reference routes/events.ts) -------------------------------------
        // The camelCase projection the PlayerEventCreated (80) / PlayerEventUpdated (81) frames and
        // PlayerEventResponseChanged's `PlayerEvent` carry (reference toEventNotification) - NOT the
        // PascalCase record the HTTP responses serve; the client decoder accepts either casing.
        private static object EventNotification(FullPlayerEvent ev) => new
        {
            tags = (ev.Tags ?? new List<EventDBClasses.FullTag>()).Select(t => new { tag = t.Tag, type = t.Type }).ToList(),
            playerEventId = ev.PlayerEventId,
            creatorPlayerId = ev.CreatorPlayerId,
            roomId = ev.RoomId,
            subRoomId = ev.SubRoomId,
            clubId = ev.ClubId,
            name = ev.Name,
            description = ev.Description,
            imageName = ev.ImageName ?? "",
            startTime = ev.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            endTime = ev.EndTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            attendeeCount = ev.AttendeeCount,
            accessibility = ev.Accessibility,
            isMultiInstance = ev.IsMultiInstance,
            supportMultiInstanceRoomChat = ev.SupportMultiInstanceRoomChat,
            defaultBroadcastPermissions = ev.DefaultBroadcastPermissions,
            canRequestBroadcastPermissions = ev.CanRequestBroadcastPermissions,
            broadcastingRoomInstanceId = (ulong?)null
        };

        private static object EventResponseDto(EventDBClasses.FullEventResponses r) => new
        {
            PlayerEventResponseId = r.PlayerEventResponseId,
            PlayerEventId = r.PlayerEventId,
            PlayerId = r.PlayerId,
            CreatedAt = r.CreatedAt,
            Type = (int)r.Type
        };

        // One {Id, Msg} frame to each player; best-effort (the change is already stored).
        private static void PushEventFrame(IEnumerable<ulong> playerIds, WebsocketEvents.ResponseResults frame, object payload)
        {
            string json = JsonConvert.SerializeObject(new WebsocketEvents.Reponse { Id = ((int)frame).ToString(), Msg = payload });
            foreach (var pid in playerIds.Distinct())
                Notifications.FireAndForget(Notifications.SendToPlayer(pid, json), $"event frame {frame} -> {pid}");
        }

        // An edited event: PlayerEventUpdated (81) to everyone GOING (creator included), then the
        // same envelope as before. Every edit route answers through this.
        private ContentResult EditedEnvelope(FullPlayerEvent ev)
        {
            PushEventFrame(EventDB.GoingPlayerIds(ev), WebsocketEvents.ResponseResults.PlayerEventUpdated, EventNotification(ev));
            return EventEnvelope(ev);
        }

        // The v2 event envelope every edit answers - the updated event under Result/PlayerEvent.
        private ContentResult EventEnvelope(FullPlayerEvent ev) => new ContentResult
        {
            Content = JsonConvert.SerializeObject(new
            {
                Result = EventResults.Success,
                TagModifyResult = (string?)null,
                PlayerEvent = new
                {
                    Tags = ev.Tags,
                    PlayerEventId = ev.PlayerEventId,
                    CreatorPlayerId = ev.CreatorPlayerId,
                    ImageName = ev.ImageName,
                    RoomId = ev.RoomId,
                    SubRoomId = ev.SubRoomId,
                    ClubId = ev.ClubId,
                    Name = ev.Name,
                    Description = ev.Description,
                    StartTime = ev.StartTime,
                    EndTime = ev.EndTime,
                    AttendeeCount = ev.AttendeeCount,
                    State = ev.State,
                    ImageAccessibility = ev.Accessibility,
                    IsMultiInstance = ev.IsMultiInstance,
                    SupportMultiInstanceRoomChat = ev.SupportMultiInstanceRoomChat,
                    DefaultBroadcastPermissions = ev.DefaultBroadcastPermissions,
                    CanRequestBroadcastPermissions = ev.CanRequestBroadcastPermissions,
                    RecurrenceSchedule = ev.RecurrenceSchedule
                }
            }),
            ContentType = "application/json",
            StatusCode = 200
        };

        // Creator-only guard shared by the edits below. Returns the event when the caller may
        // edit it, else null with `err` set to the response to return.
        private FullPlayerEvent? EditableEvent(ulong eventId, out ContentResult? err)
        {
            err = null;
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) { err = error; return null; }
            var ev = EventDB.GetFullEventById(eventId);
            if (ev == null) { err = new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 }; return null; }
            if (ev.CreatorPlayerId != account.Id) { err = new ContentResult { Content = "", ContentType = "application/json", StatusCode = 403 }; return null; }
            return ev;
        }

        [HttpPut("/api/playerevents/v2/{EventId}/description")]
        public ContentResult SetEventDescription(ulong EventId, [FromForm] string? description)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            var updated = EventDB.SetEventDescription(EventId, (description ?? "").Trim());
            return EditedEnvelope(updated!);
        }

        [HttpPut("/api/playerevents/v2/{EventId}/time")]
        public ContentResult SetEventTime(ulong EventId, [FromForm] string? startTime, [FromForm] string? endTime)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            DateTime start = DateTime.TryParse(startTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var s) ? s : ev!.StartTime;
            DateTime end = DateTime.TryParse(endTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var e) ? e : ev!.EndTime;
            var updated = EventDB.SetEventTime(EventId, start, end);
            return EditedEnvelope(updated!);
        }

        // 400'd every time as [FromForm] int - the value is read by hand now (a number or the
        // enum name, from form, query or JSON), so an event can actually be made Public.
        [HttpPut("/api/playerevents/v2/{EventId}/accessibility")]
        [HttpPost("/api/playerevents/v2/{EventId}/accessibility")]
        public ContentResult SetEventAccessibility(ulong EventId)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            var f = RoomModeration.Fields(Request);
            string raw = RoomModeration.Str(f, "accessibility", "Accessibility", "playerEventAccessibility", "value") ?? "";
            if (raw.Length == 0 && f.Count == 1) raw = f.Values.First();
            EventDBClasses.PlayerEventAccessibility acc;
            if (int.TryParse(raw, out var n)) acc = (EventDBClasses.PlayerEventAccessibility)n;
            else if (!Enum.TryParse(raw, true, out acc))
            {
                Console.WriteLine($"[events] accessibility for {EventId}: could not read '{raw}' from {RoomModeration.Dump(f)}");
                return new ContentResult { Content = JsonConvert.SerializeObject(new { Result = EventResults.SomethingWentWrong, TagModifyResult = (object?)null, PlayerEvent = (object?)null }), ContentType = "application/json", StatusCode = 400 };
            }
            var updated = EventDB.SetEventAccessibility(EventId, acc);
            Console.WriteLine($"[events] {EventId} accessibility -> {acc}");
            return EditedEnvelope(updated!);
        }

        // POST api/playerevents/v2/{id} - the FULL update (the 12-parameter edit): the same JSON
        // body as create, every field present replacing the stored one. Was a 404, so "Save"
        // on the event editor failed while the per-field PUTs worked.
        [HttpPost("/api/playerevents/v2/{EventId:long}")]
        [HttpPut("/api/playerevents/v2/{EventId:long}")]
        public async Task<ContentResult> UpdatePlayerEvent(ulong EventId)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            Newtonsoft.Json.Linq.JObject b;
            try
            {
                using var reader = new StreamReader(HttpContext.Request.Body);
                string raw = await reader.ReadToEndAsync();
                b = string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith("{") ? new Newtonsoft.Json.Linq.JObject() : Newtonsoft.Json.Linq.JObject.Parse(raw);
            }
            catch { b = new Newtonsoft.Json.Linq.JObject(); }
            if (b.Count == 0)
                foreach (var kv in RoomModeration.Fields(Request)) b[kv.Key] = kv.Value;
            Newtonsoft.Json.Linq.JToken? T(params string[] keys) { foreach (var k in keys) { var t = b[k]; if (t != null && t.Type != Newtonsoft.Json.Linq.JTokenType.Null) return t; } return null; }
            string? name = (string?)T("Name", "name");
            string? desc = (string?)T("Description", "description");
            if (!string.IsNullOrWhiteSpace(name) && Sanitize.ContainsSwears(name))
                return new ContentResult { Content = JsonConvert.SerializeObject(new { Result = EventResults.InappropriateName, TagModifyResult = (object?)null, PlayerEvent = (object?)null }), ContentType = "application/json", StatusCode = 200 };
            if (!string.IsNullOrWhiteSpace(desc) && Sanitize.ContainsSwears(desc))
                return new ContentResult { Content = JsonConvert.SerializeObject(new { Result = EventResults.InappropriateDescription, TagModifyResult = (object?)null, PlayerEvent = (object?)null }), ContentType = "application/json", StatusCode = 200 };
            var updated = EventDB.Mutate(EventId, e =>
            {
                if (!string.IsNullOrWhiteSpace(name)) e.Name = name.Trim();
                if (desc != null) e.Description = desc.Trim();
                var img = (string?)T("ImageName", "imageName"); if (!string.IsNullOrWhiteSpace(img)) e.ImageName = img;
                var rid = (ulong?)T("RoomId", "roomId"); if (rid is > 0) e.RoomId = rid.Value;
                if (b.ContainsKey("SubRoomId") || b.ContainsKey("subRoomId")) e.SubRoomId = (ulong?)T("SubRoomId", "subRoomId");
                if (b.ContainsKey("ClubId") || b.ContainsKey("clubId")) e.ClubId = (ulong?)T("ClubId", "clubId");
                if (DateTime.TryParse((string?)T("StartTime", "startTime"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var st)) e.StartTime = st;
                if (DateTime.TryParse((string?)T("EndTime", "endTime"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var et)) e.EndTime = et;
                var acc = T("Accessibility", "accessibility");
                if (acc != null) { if (int.TryParse(acc.ToString(), out var an)) e.Accessibility = (EventDBClasses.PlayerEventAccessibility)an; else if (Enum.TryParse(acc.ToString(), true, out EventDBClasses.PlayerEventAccessibility ae)) e.Accessibility = ae; }
                var mi = (bool?)T("IsMultiInstance", "isMultiInstance"); if (mi != null) e.IsMultiInstance = mi.Value;
                var mc = (bool?)T("SupportMultiInstanceRoomChat", "supportMultiInstanceRoomChat"); if (mc != null) e.SupportMultiInstanceRoomChat = mc.Value;
                var dbp = (int?)T("DefaultBroadcastPermissions", "defaultBroadcastPermissions"); if (dbp != null) e.DefaultBroadcastPermissions = (EventDBClasses.BroadcastPermissionsEnum)dbp.Value;
                var crp = (int?)T("CanRequestBroadcastPermissions", "canRequestBroadcastPermissions"); if (crp != null) e.CanRequestBroadcastPermissions = (EventDBClasses.BroadcastPermissionsEnum)crp.Value;
                if (T("Tags", "tags") is Newtonsoft.Json.Linq.JArray ta)
                    e.Tags = ta.Select(x => x.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string?)x : (string?)x["Tag"]).Where(t => !string.IsNullOrWhiteSpace(t))
                              .Select(t => new EventDBClasses.FullTag { Tag = t!.Trim(), Type = EventDBClasses.TagType.Unknown1 }).ToList();
            });
            Console.WriteLine($"[events] {EventId} updated by the editor ({b.Count} field(s))");
            return EditedEnvelope(updated!);
        }

        [HttpPut("/api/playerevents/v2/{EventId}/image")]
        [HttpPost("/api/playerevents/v2/{EventId}/image")]
        public ContentResult SetEventImage(ulong EventId)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            var f = RoomModeration.Fields(Request);
            string img = RoomModeration.Str(f, "imageName", "ImageName", "image", "value") ?? "";
            if (img.Length == 0 && f.Count == 1) img = f.Values.First();
            return EditedEnvelope(EventDB.Mutate(EventId, e => { if (img.Length > 0) e.ImageName = img; })!);
        }

        [HttpPut("/api/playerevents/v2/{EventId}/room")]
        [HttpPost("/api/playerevents/v2/{EventId}/room")]
        public ContentResult SetEventRoom(ulong EventId)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            var f = RoomModeration.Fields(Request);
            long rid = RoomModeration.Long(f, "roomId", "RoomId");
            long sid = RoomModeration.Long(f, "subRoomId", "SubRoomId");
            return EditedEnvelope(EventDB.Mutate(EventId, e => { if (rid > 0) e.RoomId = (ulong)rid; e.SubRoomId = sid > 0 ? (ulong)sid : null; })!);
        }

        [HttpPut("/api/playerevents/v2/{EventId}/club")]
        [HttpPost("/api/playerevents/v2/{EventId}/club")]
        public ContentResult SetEventClub(ulong EventId)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            var f = RoomModeration.Fields(Request);
            long cid = RoomModeration.Long(f, "clubId", "ClubId");
            return EditedEnvelope(EventDB.Mutate(EventId, e => e.ClubId = cid > 0 ? (ulong)cid : null)!);
        }

        [HttpPut("/api/playerevents/v2/{EventId}/multiinstance")]
        [HttpPost("/api/playerevents/v2/{EventId}/multiinstance")]
        public ContentResult SetEventMultiInstance(ulong EventId)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            var f = RoomModeration.Fields(Request);
            bool B(string fallbackKeyless, params string[] keys) { string v = RoomModeration.Str(f, keys) ?? (f.Count == 1 ? f.Values.First() : ""); return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1"; }
            bool multi = B("", "isMultiInstance", "IsMultiInstance", "multiInstance", "value");
            bool chat = B("", "supportMultiInstanceRoomChat", "SupportMultiInstanceRoomChat");
            return EditedEnvelope(EventDB.Mutate(EventId, e => { e.IsMultiInstance = multi; e.SupportMultiInstanceRoomChat = chat; })!);
        }

        [HttpPut("/api/playerevents/v2/{EventId}/tags")]
        public ContentResult SetEventTags(ulong EventId, [FromForm] List<string>? tags)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            var updated = EventDB.SetEventTags(EventId, tags ?? new List<string>());
            return EditedEnvelope(updated!);
        }

        [HttpPost("/api/playerevents/v2/delete/{EventId}")]
        [HttpDelete("/api/playerevents/v2/delete/{EventId}")]
        public ContentResult DeleteEvent(ulong EventId)
        {
            var ev = EditableEvent(EventId, out var err); if (err != null) return err;
            var going = EventDB.GoingPlayerIds(ev!);   // read before the delete takes the RSVPs with it
            EventDB.DeleteEvent(EventId);
            PushEventFrame(going, WebsocketEvents.ResponseResults.PlayerEventDeleted, new { PlayerEventId = EventId });
            return new ContentResult { Content = JsonConvert.SerializeObject(new { Result = EventResults.Success }), ContentType = "application/json", StatusCode = 200 };
        }

        // A moderator's warning (the vmod menu's "Warn": WarnedPlayerId, ReportCategory,
        // DisplayReason, ModeratorNote). Staff only - moderators as well as developers; it used to
        // require the developer flag, so a vmod's warning 403'd. The warned player gets the
        // ModerationKick frame with IsWarning set (the dialog, not a removal). It is NOT stored as
        // the account's moderation block any more: that overwrote an active ban with a 0-second
        // record and lifted it.
        [HttpPost("/api/playerwarnings")]
        public IActionResult CreatePlayerWarning() // WarnedPlayerId=3&ReportCategory=103&DisplayReason=Intentional%20team%20killing&ModeratorNote=
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (!PlayerDB.CheckDevFlag(account.Id) && !PlayerDB.CheckModFlag(account.Id))
                return StatusCode(403, JsonConvert.SerializeObject(new { success = false, error = "Staff only" }));

            var f = RoomModeration.Fields(Request);
            ulong target = RoomModeration.ULong(f, "WarnedPlayerId", "PlayerId", "AccountId", "Id");
            if (target == 0)
                return BadRequest(new { success = false, error = "WarnedPlayerId is required" });
            var category = RoomModeration.Category(f, "ReportCategory", "Category");
            string reason = RoomModeration.Str(f, "DisplayReason", "Message", "Reason") ?? "";
            string note = RoomModeration.Str(f, "ModeratorNote", "Note") ?? "";

            var hb = HeartbeatDB.GetPlayerHeartbeat(target);
            var block = new Moderation_Detail.ModerationBlockDetails
            {
                ReportCategory = category,
                Duration = 0,
                // 0, never the instance they are in: the client reads a moderation frame naming
                // its current game session as a removal from that room - a warned player dropped
                // out of the instance within a second (491, 2026-09-27 04:45) and, in their own
                // dorm, could not get back into it. A warning is only the dialog.
                GameSessionId = 0,
                IsHostKick = false,
                Message = reason,
                PlayerIdReporter = account.Id,
                IsBan = false,
                IsWarning = true,
                TimeoutStartedAt = DateTime.UtcNow
            };
            try { Notifications.SendToPlayer(target, JsonConvert.SerializeObject(WebsocketEvents.CreateBan(block))); } catch { }
            // The client leaves the room on the warning dialog (it does even with GameSessionId 0),
            // then goes wherever its presence points - which was still the instance, so it
            // reconnected straight back in (495, 2026-09-27 06:02 -> back in 18 s). Send them home.
            // Not when they are in their OWN dorm already: that is home, and a fresh dorm instance
            // would only split them from their guests.
            if (hb?.roomInstance != null && hb.roomInstance.roomInstanceId > 0
                && (ulong)hb.roomInstance.roomId != PlayerDB.GetDormId(target))
                RoomModeration.SendHome(target, 0, 0);
            ModerationLogDB.Record("warning", target, account.Id, (ulong)(hb?.roomInstance?.roomId ?? 0), hb?.roomInstance?.roomInstanceId ?? 0, 0, category, reason, note);
            Console.WriteLine($"[moderation] {account.Id} warned {target}: {category} \"{reason}\"{(note.Length > 0 ? $" (note: {note})" : "")}");

            return Ok(new
            {
                success = true,
                error = "",
                value = (string?)null
            });
        }

        /// <summary>
        /// The vmod menu's kick (modKick / roomModKick) - the "restrict from room" that follows a
        /// warning. Fields are read by name from form, query or JSON (the exact names the 2025
        /// client sends are logged the first times so they can be pinned): the player, the game
        /// session, a duration in seconds, a category and a message.
        ///
        /// What it does:
        ///   - the player is removed from the instance (ModerationKick frame + presence cleared)
        ///   - they are kept out of THAT INSTANCE for ConfigCampus.RoomKickInstanceBlockSeconds
        ///     (8 h): the matchmaker never pools them back into it and a join-player / invite
        ///     into it is refused. Any other instance of the room is still open to them.
        ///
        /// A kick is NOT a room ban. It used to be stored as a permanent room ban ("Kicked by X"),
        /// so a moderator's kick in ^RecCenter locked the player out of the whole map until
        /// somebody found and lifted it. Banning is the separate room ban route
        /// (POST rooms/{id}/bans), which the room's owners lift from Room Settings.
        ///
        /// Who: staff (developer / moderator flag), or a moderator-or-better on the room the
        /// player is in. Room owners and staff cannot be kicked by non-staff.
        /// </summary>
        [HttpPost("/api/PlayerReporting/v1/modKick")]
        [HttpPost("/api/PlayerReporting/v1/roomModKick")]
        public IActionResult ModKick()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var f = RoomModeration.Fields(Request);
            Console.WriteLine($"[modkick] {Request.Path} by {account.Id}: {RoomModeration.Dump(f)}");

            ulong target = RoomModeration.ULong(f, "PlayerId", "KickedPlayerId", "TargetPlayerId", "AccountId", "Id", "PlayerIds");
            if (target == 0)
                return Ok(new { success = false, error = "No player given" });
            if (target == account.Id)
                return Ok(new { success = false, error = "You cannot kick yourself" });

            long session = RoomModeration.Long(f, "GameSessionId", "RoomInstanceId", "SessionId");
            long seconds = RoomModeration.Long(f, "Duration", "DurationSeconds", "TimeoutDuration", "TimeoutSeconds");
            if (seconds <= 0) seconds = RoomModeration.Long(f, "DurationMinutes", "Minutes") * 60;
            var category = RoomModeration.Category(f, "ReportCategory", "Category");
            string message = RoomModeration.Str(f, "Message", "DisplayReason", "Reason", "KickMessage") ?? "";
            string note = RoomModeration.Str(f, "ModeratorNote", "Note") ?? "";

            // Which room: where the player is now, else the instance named, else a RoomId field.
            var hb = HeartbeatDB.GetPlayerHeartbeat(target);
            var inst = hb?.roomInstance ?? (session != 0 ? HeartbeatDB.FindInstanceById(session) : null);
            ulong roomId = inst != null ? (ulong)inst.roomId : RoomModeration.ULong(f, "RoomId");
            if (session == 0 && inst != null) session = inst.roomInstanceId;
            var room = roomId != 0 ? RoomDB.GetRoom(roomId) : null;

            bool staff = PlayerDB.CheckDevFlag(account.Id) || PlayerDB.CheckModFlag(account.Id);
            bool roomMod = room != null && RoomDB.CanModerateRoom(room, account.Id);
            if (!staff && !roomMod)
            {
                Console.WriteLine($"[modkick] REFUSED {account.Id} -> {target}: not staff and not a moderator of room {roomId}");
                return StatusCode(403, JsonConvert.SerializeObject(new { success = false, error = "You cannot moderate this room" }));
            }
            if (!PlayerDB.CheckDevFlag(account.Id) && PlayerDB.CheckDevFlag(target))
                return Ok(new { success = false, error = "You cannot kick a staff member" });
            if (!staff && room != null && (room.CreatorAccountId == target || RoomDB.IsCoOwner(room, target)))
                return Ok(new { success = false, error = "You cannot kick an owner of this room" });

            string shown = message.Length > 0 ? message : (room != null ? $"You have been removed from {room.Name}." : "You have been removed from the room.");

            // 1. out of the instance now (the ModerationKick frame their client leaves on, and
            //    their presence cleared). Nothing account-wide is stored: a room kick is not a
            //    timeout, whatever duration field the client may send.
            RoomModeration.RemoveFromRoom(target, roomId, session, shown, account.Id, isBan: false, isHostKick: !staff);

            // 2. and out of THAT instance for the next 8 hours. Other instances of the room stay
            //    open, and nothing is written to the room's ban list.
            int blockSeconds = Math.Max(60, ConfigCampus.RoomKickInstanceBlockSeconds);
            if (inst != null && room != null && room.CreatorAccountId != target)
                InstanceBlocks.Add(target, inst, account.Id, TimeSpan.FromSeconds(blockSeconds), message.Length > 0 ? message : $"Kicked by {account.Username}");
            else if (inst == null)
                Console.WriteLine($"[modkick] {target} is not in a live instance - removed only, nothing to block");

            ModerationLogDB.Record("modkick", target, account.Id, roomId, session, blockSeconds, category, message, note);
            return Ok(new { success = true, error = "" });
        }

        // Vote to kick (form: PlayerId, Response, Reason, GameSessionId). Was an ack that did
        // nothing, so a vote never reached anyone. Now: the caller and the target must both be
        // in that instance; everyone else there gets the MessageReceived type-5 (VoteToKick)
        // frame their client raises the prompt from; each Response is tallied per
        // (instance, target), and once more than half of the OTHER players have said yes the
        // target is removed and kept out of that INSTANCE for ConfigCampus.VoteKickBanSeconds.
        // Answers the {success,error} envelope.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Dictionary<ulong, bool> votes, DateTime started)> _voteKicks = new();

        [HttpPost("/api/PlayerReporting/v3/voteToKick")]
        [HttpPost("/api/PlayerReporting/v2/voteToKick")]
        public IActionResult VoteToKick()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var f = RoomModeration.Fields(Request);
            ulong target = RoomModeration.ULong(f, "PlayerId", "playerId", "TargetPlayerId", "Id");
            long session = RoomModeration.Long(f, "GameSessionId", "gameSessionId", "RoomInstanceId");
            string responseRaw = RoomModeration.Str(f, "Response", "response", "Vote") ?? "";
            string reason = RoomModeration.Str(f, "Reason", "reason") ?? "";
            if (target == 0) return BadRequest(new { success = false, error = "PlayerId is required" });

            var myHb = HeartbeatDB.GetPlayerHeartbeat(account.Id);
            if (session == 0) session = myHb?.roomInstance?.roomInstanceId ?? 0;
            if (session == 0) return BadRequest(new { success = false, error = "GameSessionId is required" });
            if (myHb?.roomInstance?.roomInstanceId != session)
                return StatusCode(403, JsonConvert.SerializeObject(new { success = false, error = "You are not in that game session!" }));
            var targetHb = HeartbeatDB.GetPlayerHeartbeat(target);
            if (targetHb?.roomInstance?.roomInstanceId != session)
                return StatusCode(403, JsonConvert.SerializeObject(new { success = false, error = "That player is not in that game session!" }));
            var room = RoomDB.GetRoom((ulong)myHb.roomInstance.roomId);
            if (RoomDB.CanModerateRoom(room, target) && !RoomDB.CanModerateRoom(room, account.Id))
                return Ok(new { success = false, error = "You cannot vote to kick a moderator of this room" });

            // Everyone in the instance except the caller.
            var audience = RoomPermissions.PlayersInRoom((ulong)myHb.roomInstance.roomId)
                .Where(pid => pid != account.Id && HeartbeatDB.GetPlayerHeartbeat(pid)?.roomInstance?.roomInstanceId == session)
                .ToList();

            string key = $"{session}|{target}";
            var entry = _voteKicks.GetOrAdd(key, _ => (new Dictionary<ulong, bool>(), DateTime.UtcNow));
            if ((DateTime.UtcNow - entry.started).TotalMinutes > 3) { entry = (new Dictionary<ulong, bool>(), DateTime.UtcNow); _voteKicks[key] = entry; }
            bool newVote = entry.votes.Count == 0;
            bool yes = responseRaw.Length == 0 || responseRaw.Equals("true", StringComparison.OrdinalIgnoreCase) || responseRaw == "1" || responseRaw.Equals("yes", StringComparison.OrdinalIgnoreCase);
            lock (entry.votes) entry.votes[account.Id] = yes;

            // A NEW vote is put to the room; a later Response is just tallied.
            if (newVote)
            {
                // The frame the client raises its "Vote kick X?" prompt from (reference: the
                // vote-to-kick Message). Two things it reads literally:
                //   * FromPlayerId is the player being VOTED ON - the prompt is about whoever this
                //     names; the caller is named nowhere on the frame (and needn't be).
                //   * Data is the REASON as plain text ("Abusing bugs or exploits") - it's a string
                //     on the wire like every Message's Data, and the prompt prints it verbatim. It
                //     used to be an escaped {PlayerId, Response, GameSessionId, Reason} object, so
                //     players saw that JSON where the reason belongs.
                string data = reason;
                foreach (var pid in audience)
                {
                    var msg = new MessageData
                    {
                        Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        FromPlayerId = target, ToPlayerId = pid, SentTime = DateTime.UtcNow,
                        Type = WebsocketEvents.MessageType.VoteToKick, Data = data
                    };
                    Notifications.FireAndForget(Notifications.SendToPlayer(pid, JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(msg))), "vote-kick");
                }
                Console.WriteLine($"[votekick] {account.Id} called a vote on {target} in instance {session} ({audience.Count} asked){(reason.Length > 0 ? $" - {reason}" : "")}");
            }

            // Tally: yes votes among everyone other than the target.
            int voters = Math.Max(1, audience.Count(p => p != target) + 1);   // + the caller
            int yesVotes; lock (entry.votes) yesVotes = entry.votes.Where(kv => kv.Key != target && kv.Value).Count();
            Console.WriteLine($"[votekick] instance {session} target {target}: {yesVotes}/{voters} yes");
            if (yesVotes > voters * ConfigCampus.VoteKickMajority || (voters == 1 && yesVotes >= 1 && audience.Count(p => p != target) == 0))
            {
                _voteKicks.TryRemove(key, out _);
                // A plain removal from the room - the same frame a host kick sends (Duration 0). It
                // used to go out as a VoteKick block with Duration = VoteKickBanSeconds, which the
                // client shows as a 15-minute account TIMEOUT. Being voted out of one room is not a
                // timeout: they only stay out of THIS instance (the instance block below).
                string shown = reason.Length > 0 ? $"The room voted to remove you: {reason}" : "The room voted to remove you.";
                RoomModeration.RemoveFromRoom(target, (ulong)myHb.roomInstance.roomId, session, shown, account.Id, isBan: false, isHostKick: true);
                if (room != null && room.CreatorAccountId != target)
                    InstanceBlocks.Add(target, myHb.roomInstance, account.Id, TimeSpan.FromSeconds(Math.Max(60, ConfigCampus.VoteKickBanSeconds)), "Vote kicked" + (reason.Length > 0 ? $": {reason}" : ""));
                ModerationLogDB.Record("votekick", target, account.Id, room?.RoomId ?? 0, session, ConfigCampus.VoteKickBanSeconds, Moderation_Detail.ReportCategory.VoteKick, reason);
                Console.WriteLine($"[votekick] {target} was vote-kicked from instance {session}");
            }
            return Ok(new { success = true, error = "" });
        }

        // In-game bug report ("Report a bug" on the watch posts to /ado/bug - Azure DevOps on the
        // real service). Was a 404. Whatever the client sends - form, JSON or multipart with
        // screenshots / logs - is kept and shown on the admin panel's Reports tab.
        [HttpPost("/ado/bug")]
        [HttpPost("/ado/bug/{**rest}")]
        [HttpPost("/ado/file")]
        [HttpPost("/ado/file/{**rest}")]
        [HttpPost("/api/bugreport/v1")]
        public async Task<IActionResult> BugReport(string? rest = null)
        {
            var (account, _) = AuthHelper.ValidateToken(HttpContext);
            var r = new BugReportDB.Report { AccountId = account?.Id ?? 0, Username = account?.Username ?? "" };
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Request.HasFormContentType)
                {
                    var form = await Request.ReadFormAsync();
                    foreach (var kv in form) fields[kv.Key] = string.Join(",", kv.Value.ToArray());
                    string dir = Path.Combine(Environment.CurrentDirectory, "Data", "BugReports");
                    Directory.CreateDirectory(dir);
                    foreach (var file in form.Files)
                    {
                        if (file.Length <= 0 || file.Length > 25 * 1024 * 1024) continue;
                        string safe = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(file.FileName ?? "file"), @"[^A-Za-z0-9_.-]", "_");
                        string name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{account?.Id ?? 0}-{safe}";
                        using var fs = System.IO.File.Create(Path.Combine(dir, name));
                        await file.CopyToAsync(fs);
                        r.Files.Add(name);
                    }
                }
                else
                {
                    foreach (var kv in RoomModeration.Fields(Request)) fields[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[bug] could not read report body: {ex.Message}"); }

            string Pick(params string[] names) { foreach (var n in names) if (fields.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v; return ""; }
            r.Title = Pick("title", "Title", "summary", "Summary", "subject");
            r.Description = Pick("description", "Description", "body", "Body", "details", "Details", "text", "message", "Message", "repro", "ReproSteps");
            r.Category = Pick("category", "Category", "type", "Type", "area", "Area", "bugType");
            r.Room = Pick("roomName", "RoomName", "room", "Room", "roomId", "RoomId");
            r.Platform = Pick("platform", "Platform", "device", "Device", "buildVersion", "appVersion");
            r.Raw = string.Join("\n", fields.Select(kv => $"{kv.Key}={(kv.Value.Length > 4000 ? kv.Value[..4000] + "…" : kv.Value)}"));
            if (r.Title.Length == 0 && r.Description.Length > 0) r.Title = r.Description.Length > 80 ? r.Description[..80] : r.Description;
            var hb = account != null ? HeartbeatDB.GetPlayerHeartbeat(account.Id) : null;
            if (r.Room.Length == 0 && hb?.roomInstance != null) r.Room = $"{hb.roomInstance.name} ({hb.roomInstance.roomId})";
            BugReportDB.Add(r);
            Console.WriteLine($"[bug] #{r.Id} from {r.Username} ({r.AccountId}): '{r.Title}' fields={fields.Count} files={r.Files.Count}");
            return Ok(new { success = true, error = "", value = r.Id, id = r.Id });
        }

        /// <summary>
        /// Eject players from one live instance (JSON {GameSessionId, PlayerIds}). Room moderators
        /// or staff. Only players actually in that instance are touched; nothing is stored and
        /// nothing stops them rejoining - that is what modKick is for. Answers {success,error}.
        /// Was an ack that did nothing.
        /// </summary>
        [HttpPost("/api/PlayerReporting/v1/instantKick")]
        public IActionResult InstantKick()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var f = RoomModeration.Fields(Request);
            long session = RoomModeration.Long(f, "GameSessionId", "RoomInstanceId");
            var ids = RoomModeration.ULongs(f, "PlayerIds", "PlayerId", "Ids");
            if (session == 0 || ids.Count == 0)
                return BadRequest(new { success = false, error = "GameSessionId and PlayerIds are required" });

            var inst = HeartbeatDB.FindInstanceById(session);
            if (inst == null)
                return NotFound(new { success = false, error = "No such game session" });
            var room = RoomDB.GetRoom((ulong)inst.roomId);
            if (!RoomDB.CanModerateRoom(room, account.Id))
                return StatusCode(403, JsonConvert.SerializeObject(new { success = false, error = "You cannot moderate this room" }));

            int kicked = 0;
            foreach (var pid in ids)
            {
                if (pid == account.Id) continue;
                if (RoomDB.CanModerateRoom(room, pid) && !PlayerDB.CheckDevFlag(account.Id)) continue;
                var hb = HeartbeatDB.GetPlayerHeartbeat(pid);
                if (hb?.roomInstance?.roomInstanceId != session) continue;
                RoomModeration.RemoveFromRoom(pid, (ulong)inst.roomId, session, $"You have been removed from {room?.Name ?? "the room"}.", account.Id, isBan: false, isHostKick: true);
                ModerationLogDB.Record("kick", pid, account.Id, (ulong)inst.roomId, session, 0, Moderation_Detail.ReportCategory.Moderator, "");
                kicked++;
            }
            Console.WriteLine($"[instantkick] {account.Id} kicked {kicked}/{ids.Count} from instance {session}");
            return Ok(new { success = true, error = "" });
        }

        [HttpGet("/api/banappeal/generateCode")]
        public IActionResult GenerateBanAppealCode()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                success = false,
                error = "",
                value = "Make a ticket in https://discord.gg/xH8MMwK5Bk"
            });
        }

        [HttpGet("/api/outfits/me")]
        [HttpGet("/outfits/me")]
        public IActionResult GetMyOutfits()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var player = PlayerDB.GetFullAccount(account.Id);

            // Was: no null check, then player.player_Extra.avatar - NRE for an
            // account with no row.
            if (player?.player_Extra == null)
            {
                Console.WriteLine($"[/outfits/me] no player/player_Extra row for {account.Id}");
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 400 };
            }

            if (string.IsNullOrEmpty(player.player_Extra.avatar))
            {
                return Ok(new
                {
                    LegacyData = new
                    {
                        SelectionsV1 = (string?)null,
                        SelectionsV2 = (string?)null,
                        FaceFeatures = (string?)null,
                        SkinColor = (string?)null,
                        HairColor = (string?)null
                    },
                    Selections = Array.Empty<object>(),
                    DataVersion = 9,
                    CustomizationSettings = (string?)null,
                    ThumbnailFileName = (string?)null,
                    Name = (string?)null,
                    Accessibility = 0,
                    Slot = 0
                });
            }


            // REVERTED 2026-08-23 to the original Ok(...) form. Changing this - along
            // with wrapping the stored avatar in a LegacyData envelope - stopped avatar
            // items loading entirely and broke the watch's Customize tab. The client
            // parse errors those changes removed were less harmful than the breakage
            // they caused, so the original behaviour stands until there is a way to
            // verify a replacement against the real client before shipping it.
            // Was: Ok(<string>). ASP.NET's StringOutputFormatter writes a raw string as
            // Content-Type: text/plain, so these JSON payloads were served mislabelled
            // and the client would not parse them. Verified live: /outfits/me returned
            // text/plain while /api/avatar/v2 returned application/json. Same bytes,
            // correct content type.
            return new ContentResult { Content = player.player_Extra.avatar, ContentType = "application/json", StatusCode = 200 };
        }

        [HttpPut("/api/outfits/me")]
        [HttpPut("/outfits/me")]
        public async Task<IActionResult> SetMyOutfit()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string jsonData = "";
            try
            {
                using var reader = new StreamReader(HttpContext.Request.Body);
                jsonData = await reader.ReadToEndAsync();
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

            if (string.IsNullOrWhiteSpace(jsonData))
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };

            try
            {
                Newtonsoft.Json.Linq.JToken.Parse(jsonData);
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


            // The newer client saves a WARDROBE PRESET by PUTting here with Slot > 0; the outfit
            // being WORN is Slot 0 (or no Slot at all). A preset save must NOT overwrite the worn
            // avatar - which is exactly what happened before, because Slot was ignored and every
            // save clobbered player_Extra.avatar. So Slot > 0 is upserted into the saved wardrobe
            // (the same store /api/avatar/v3/saved and /outfits/me/saved read) and returns here;
            // Slot 0 / absent falls through to the unchanged worn-avatar path below.
            int savedSlot = 0;
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(jsonData);
                if (jo.TryGetValue("Slot", StringComparison.OrdinalIgnoreCase, out var slotTok))
                    savedSlot = slotTok.Type == Newtonsoft.Json.Linq.JTokenType.Integer ? slotTok.Value<int>() : 0;
            }
            catch { /* not an object / no Slot -> treat as worn (slot 0) */ }

            if (savedSlot > 0)
            {
                try
                {
                    var preset = JsonConvert.DeserializeObject<avatar_data_saved>(jsonData) ?? new avatar_data_saved();
                    preset.Slot = (ulong)savedSlot;
                    // Keep the raw document the client sent, so a preset round-trips without losing
                    // any field our structured type doesn't model.
                    if (string.IsNullOrEmpty(preset.Avatar)) preset.Avatar = jsonData;
                    PlayerDB.SetAvatarSaved(account.Id, new List<avatar_data_saved> { preset });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[/outfits/me] preset save (slot {savedSlot}) failed: {ex.Message}");
                    return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 400 };
                }

                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { Success = true, Error = (string?)null, error_id = (string?)null }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            var player = PlayerDB.GetFullAccount(account.Id);
            if (player == null)
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 401
                };

            try
            {
                PlayerDB.set_player_avatar(jsonData, player.playerid);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Avatar Set Error] {ex}");
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            return new ContentResult()
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The caller's saved-outfit wardrobe. Was `ConfigCampus.Bracket` - an economy config
        // object, not a list of outfits - so the newer wardrobe screen showed nothing no matter
        // what you saved, which read as "saving outfits doesn't work". Returns the real saved
        // outfits (the same store PUT /outfits/me slot>0 and /api/avatar/v4/saved/set write to,
        // and /api/avatar/v3/saved reads); "[]" for a player who has saved none.
        [HttpGet("/api/outfits/me/saved")]
        [HttpGet("/outfits/me/saved")]
        public ContentResult GetMySavedOutfits()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = PlayerDB.GetAvatarSaved(account.Id),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Several players' WORN outfits at once, keyed by account id - the call the newer client
        // makes to dress a whole room (and to render other people's avatars, e.g. on a profile).
        // Ported from RecFlare's POST /outfits/bulk; it was missing, so in the newer client every
        // OTHER player rendered with no/default avatar. Body is { AccountIds: [..] }; the answer is
        // { OutfitsByAccountId: { "<id>": <worn outfit>, ... } }. A player with no saved avatar is
        // simply ABSENT from the map (a map says "no outfit" by not having the key) rather than
        // carrying a null. Worn outfit = player_Extra.avatar, handed back exactly as it was saved.
        [HttpPost("/outfits/bulk")]
        public async Task<ContentResult> GetOutfitsBulk()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string body;
            using (var reader = new StreamReader(HttpContext.Request.Body))
                body = await reader.ReadToEndAsync();

            List<ulong> ids = new();
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(body);
                if (jo.TryGetValue("AccountIds", StringComparison.OrdinalIgnoreCase, out var arr)
                    && arr is Newtonsoft.Json.Linq.JArray jarr)
                {
                    foreach (var t in jarr)
                        if (t.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                            ids.Add(t.Value<ulong>());
                }
            }
            catch
            {
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 400 };
            }

            // One round trip, so cap the list (a room never holds this many). Distinct ids only.
            var distinct = ids.Distinct().Take(99).ToList();

            var map = new Dictionary<string, object>();
            foreach (var pid in distinct)
            {
                var p = PlayerDB.GetFullAccount(pid);
                var worn = p?.player_Extra?.avatar;
                if (string.IsNullOrEmpty(worn)) continue;   // absent from the map, never null
                try { map[pid.ToString()] = Newtonsoft.Json.Linq.JToken.Parse(worn); }
                catch { /* stored avatar isn't valid JSON - skip rather than break the whole map */ }
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { OutfitsByAccountId = map }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        [HttpGet("/sampling")]
        public IActionResult Sampling()
        {
            // Validate the user token
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var samplingData = new List<object>
            {
                new { eventType = "SimilarEntitiesContext", shouldBeSampled = false },
                new { eventType = "OnlineRankingContext", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_modify_object", shouldBeSampled = false },
                new { eventType = "heartbeat", shouldBeSampled = false },
                new { eventType = "edible_bite", shouldBeSampled = false },
                new { eventType = "impression", shouldBeSampled = false },
                new { eventType = "downloadable_cache_loaded", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_selectionChanged", shouldBeSampled = false },
                new { eventType = "hand_gesture_use", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_open_menu", shouldBeSampled = false },
                new { eventType = "load_outfit_failed", shouldBeSampled = false },
                new { eventType = "subroom_stats", shouldBeSampled = false },
                new { eventType = "button_press", shouldBeSampled = false },
                new { eventType = "click", shouldBeSampled = false },
                new { eventType = "emote_use", shouldBeSampled = false },
                new { eventType = "watch_stats", shouldBeSampled = false },
                new { eventType = "edible_consume", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_menu_item_selected", shouldBeSampled = false },
                new { eventType = "drinkable_drink", shouldBeSampled = false },
                new { eventType = "sessionMetadata", shouldBeSampled = false },
                new { eventType = "stacked_UI_flow_change", shouldBeSampled = false },
                new { eventType = "food_spawned", shouldBeSampled = false },
                new { eventType = "Announcement_Shown", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_connect", shouldBeSampled = false },
                new { eventType = "mtm_round_stats", shouldBeSampled = false },
                new { eventType = "RRUI_route_change", shouldBeSampled = false },
                new { eventType = "drinkable_consume", shouldBeSampled = false },
                new { eventType = "makerpen_session_detailed", shouldBeSampled = false },
                new { eventType = "store_search", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_create_object", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_changed_setting", shouldBeSampled = false },
                new { eventType = "ugc_room_save_stats_detailed", shouldBeSampled = false },
                new { eventType = "downloadable_item_loaded", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_configure_object", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_search", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_acm_movement_check", shouldBeSampled = false },
                new { eventType = "eligible_for_matchmaking_toast_event", shouldBeSampled = false },
                new { eventType = "ineligible_for_matchmaking_toast_event", shouldBeSampled = false },
                new { eventType = "external_room_progression_shown", shouldBeSampled = false },
                new { eventType = "time_to_content_load", shouldBeSampled = false },
                new { eventType = "time_to_first_card_loaded", shouldBeSampled = false },
                new { eventType = "avatar_outfit_changed", shouldBeSampled = false },
                new { eventType = "DataAPIQuery", shouldBeSampled = false },
                new { eventType = "RoomElasticsearchQuery", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_favorite_object", shouldBeSampled = false },
                new { eventType = "mlm_quest_step_start", shouldBeSampled = false },
                new { eventType = "mlm_acquire_egg", shouldBeSampled = false },
                new { eventType = "mlm_monster_added", shouldBeSampled = false },
                new { eventType = "mlm_monster_need_met", shouldBeSampled = false },
                new { eventType = "mlm_monster_lvl_up", shouldBeSampled = false },
                new { eventType = "mlm_travel_companion_change", shouldBeSampled = false },
                new { eventType = "mlm_monster_rename", shouldBeSampled = false },
                new { eventType = "mlm_left_evergrown", shouldBeSampled = false },
                new { eventType = "mlm_crafting_complete", shouldBeSampled = false },
                new { eventType = "mlm_sanctuary_lvl_up", shouldBeSampled = false },
                new { eventType = "mlm_monster_light_lvl_up", shouldBeSampled = false },
                new { eventType = "mlm_daily_check_in", shouldBeSampled = false },
                new { eventType = "mlm_add_guaranteed_item", shouldBeSampled = false },
                new { eventType = "RoomAutocompleteElasticsearchQuery", shouldBeSampled = false },
                new { eventType = "mlm_player_capacity_reached", shouldBeSampled = false },
                new { eventType = "mlm_tag_failure", shouldBeSampled = false },
                new { eventType = "mlm_npc_nearby", shouldBeSampled = false },
                new { eventType = "mlm_conversation_start", shouldBeSampled = false },
                new { eventType = "autocomplete_impression", shouldBeSampled = false },
                new { eventType = "rtb_player_stats", shouldBeSampled = false },
                new { eventType = "rtb_challenge_stats", shouldBeSampled = false },
                new { eventType = "rtb_public_event_stats", shouldBeSampled = false },
                new { eventType = "ugc_makerpen_container_rename", shouldBeSampled = false },
                new { eventType = "rtb_feedback", shouldBeSampled = false },
                new { eventType = "contextual_features", shouldBeSampled = false },
                new { eventType = "RoomSearchRequest", shouldBeSampled = false },
                new { eventType = "StatsigParameterDesync", shouldBeSampled = false },
                new { eventType = "reccon_candy_stats", shouldBeSampled = false },
                new { eventType = "reccon_roomstats", shouldBeSampled = false },
                new { eventType = "reccon_trivia_stats", shouldBeSampled = false },
                new { eventType = "reccon_fashion_stats", shouldBeSampled = false },
                new { eventType = "mlm_missing_replicators", shouldBeSampled = false },
                new { eventType = "engagementTitlescreenJumpRightInStatsigTimeout", shouldBeSampled = false },
                new { eventType = "engagementTitlescreenJumpRightInOnlineFriendClick", shouldBeSampled = false },
                new { eventType = "engagementTitlescreenJumpRightInOnlineFriendsPagination", shouldBeSampled = false },
                new { eventType = "engagementTitlescreenJumpRightInOnlineFriendsJoinFriendClick", shouldBeSampled = false },
                new { eventType = "engagementTitlescreenJumpRightInOnlineFriendsViewAll", shouldBeSampled = false },
                new { eventType = "smart_circuit_makerpen_drag_released", shouldBeSampled = false },
                new { eventType = "ugc_room_event", shouldBeSampled = false },
                new { eventType = "CustomAvatarItemSearchRequest", shouldBeSampled = false },
                new { eventType = "perf_metrics", shouldBeSampled = false },
                new { eventType = "whalegame_boatmaterialreset", shouldBeSampled = false },
                new { eventType = "engagementTitlescreenJumpRightInOnlineFriendsViewMore", shouldBeSampled = false },
                new { eventType = "ValentinesDay2025", shouldBeSampled = false },
                new { eventType = "orientation_purchase_flow_steps", shouldBeSampled = false },
                new { eventType = "ai_error", shouldBeSampled = false },
                new { eventType = "ai_mistake", shouldBeSampled = false },
                new { eventType = "ai_missing_functionality", shouldBeSampled = false },
                new { eventType = "ai_response_error", shouldBeSampled = false },
                new { eventType = "ai_text_bug", shouldBeSampled = false },
                new { eventType = "socialcreation1_robotparts", shouldBeSampled = false },
                new { eventType = "socialcreation1_robotactivated", shouldBeSampled = false },
                new { eventType = "ai_conversation", shouldBeSampled = false },
                new { eventType = "ai_expense", shouldBeSampled = false },
                new { eventType = "crossed_bridge", shouldBeSampled = false },
                new { eventType = "completed_task_normally", shouldBeSampled = false },
                new { eventType = "bridge_dropped_time", shouldBeSampled = false },
                new { eventType = "Player_pressed_button", shouldBeSampled = false },
                new { eventType = "bridge_dropped_due_to_time", shouldBeSampled = false },
                new { eventType = "Players_at_finishline", shouldBeSampled = false },
                new { eventType = "Players_at_timeout", shouldBeSampled = false },
                new { eventType = "Social_creation_2_events", shouldBeSampled = false },
                new { eventType = "socail_creation_2_completed_task_normally", shouldBeSampled = false },
                new { eventType = "Socialcreation2_Players_at_finishline", shouldBeSampled = false },
                new { eventType = "Socialcreation2_completed_task_normally", shouldBeSampled = false },
                new { eventType = "Socialcreation2_Placed_object", shouldBeSampled = false },
                new { eventType = "Socialcreation2_Players_at_timeout", shouldBeSampled = false },
                new { eventType = "Socialcreation2_crossed_bridge", shouldBeSampled = false },
                new { eventType = "Socialcreation2_bridge_dropped_due_to_time", shouldBeSampled = false },
                new { eventType = "Socialcreation2_Player_pressed_button", shouldBeSampled = false },
                new { eventType = "widget_watch_state_change", shouldBeSampled = false },
                new { eventType = "ListFilteredEntities", shouldBeSampled = false },
                new { eventType = "roomie_ai_session", shouldBeSampled = false },
                new { eventType = "roomie_ai_response", shouldBeSampled = false },
                new { eventType = "roomie_ai_conversation_history", shouldBeSampled = false },
                new { eventType = "ai_session", shouldBeSampled = false },
                new { eventType = "ai_conversation_history", shouldBeSampled = false },
                new { eventType = "ai_response", shouldBeSampled = false },
                new { eventType = "roomie_ai_expense", shouldBeSampled = false },
                new { eventType = "roomie_ai_error", shouldBeSampled = false },
                new { eventType = "roomie_ai_response_error", shouldBeSampled = false },
                new { eventType = "avatar_item_consumable", shouldBeSampled = false },
                new { eventType = "ham_reached", shouldBeSampled = false },
                new { eventType = "prompt_submitted", shouldBeSampled = false },
                new { eventType = "llm_response_received", shouldBeSampled = false },
                new { eventType = "AccountsToGenericCandidatesContext", shouldBeSampled = false },
                new { eventType = "RoomsToGenericSimilarEntitiesContext", shouldBeSampled = false },
                new { eventType = "Rooms_searchContext", shouldBeSampled = false },
                new { eventType = "QueryRecommendation", shouldBeSampled = false },
                new { eventType = "Generic_searchContext", shouldBeSampled = false },
                new { eventType = "avatar_effect_grenade", shouldBeSampled = false },
                new { eventType = "game_ai_session", shouldBeSampled = false },
                new { eventType = "game_ai_conversation_history", shouldBeSampled = false },
                new { eventType = "game_ai_response", shouldBeSampled = false },
                new { eventType = "game_ai_response_error", shouldBeSampled = false },
                new { eventType = "roomie_avatar_equip", shouldBeSampled = false },
                new { eventType = "game_ai_error", shouldBeSampled = false },
                new { eventType = "roomie_ai_stuck_in_text", shouldBeSampled = false },
                new { eventType = "roomie_ai_restart", shouldBeSampled = false },
                new { eventType = "gift_button_clicked", shouldBeSampled = false },
                new { eventType = "gift_button_consume_clicked", shouldBeSampled = false },
                new { eventType = "rma_booth_entered", shouldBeSampled = false },
                new { eventType = "rma_rate_button_pushed ", shouldBeSampled = false },
                new { eventType = "rma_rating_completion", shouldBeSampled = false },
                new { eventType = "sj_rate_button_pushed", shouldBeSampled = false },
                new { eventType = "sj_rating_completion", shouldBeSampled = false },
                new { eventType = "avatar_glitch_effect_grenade", shouldBeSampled = false },
                new { eventType = "game_ai_expense", shouldBeSampled = false },
                new { eventType = "roomie_interaction", shouldBeSampled = false },
                new { eventType = "roomie_ai_energy_updated", shouldBeSampled = false },
                new { eventType = "Ticket consumed", shouldBeSampled = false }
            };

            return Ok(samplingData);
        }

        [HttpGet("/api/consumables/v1/all")]
        public async Task<IActionResult> GetUnlockableConsumables()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "AllUnlockableConsumables.json");
            string content = await System.IO.File.ReadAllTextAsync(path);
            // Was: Ok(<string>). ASP.NET's StringOutputFormatter writes a raw string as
            // Content-Type: text/plain, so these JSON payloads were served mislabelled
            // and the client would not parse them. Verified live: /outfits/me returned
            // text/plain while /api/avatar/v2 returned application/json. Same bytes,
            // correct content type.
            return new ContentResult { Content = content, ContentType = "application/json", StatusCode = 200 };
        }

        [HttpPost("/statsigUserProperties")]
        public IActionResult GetStatsigUserProperties()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                success = ConfigCampus.StatsigEnabled
            });
        }

        [HttpGet("/api/referee/files")]
        public IActionResult GetRefereeFiles()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpPost("/api/PlayerReporting/v1/referee")] // we hate referee so false it to hell
        public IActionResult RefereeCheck()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok("false");
        }

        [HttpGet("/api/ugcPurchasables/v1/items/room/{roomId}")]
        public IActionResult RefereeCheck(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(ConfigCampus.Bracket);
        }

        [HttpPost("/lists/contextualfeatures")]
        public IActionResult ContextualFeatures()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                success = true,
                error_id = (string?)null,
                error = (string?)null
            });
        }

        // /api/progressionEvents/active moved to ProgressionEventsController (limited-time events).

        // Maker AI free trial: the 20250718 client reads an OBJECT (the reference's bare `false`
        // was a 2023 shape - "expected:'{', actual:'false'" in Player.log). Not eligible either way.
        [HttpGet("/api/makerai/checkfreetrialeligibility")]
        public IActionResult CheckFreeTrialEligibility()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Content("{\"IsEligible\":false,\"Eligible\":false,\"Success\":true,\"Error\":null}", "application/json");
        }

        // The Maker AI free-trial settings (two durations) and the button that starts a trial. There is no Maker AI trial here, so the settings are
        // an empty object (every field left at its default, which the client reads without complaint - it asked 32 times in two days and got 404)
        // and starting one is refused politely.
        [HttpGet("/api/makerai/freetrialconfig")]
        public IActionResult MakerAiFreeTrialConfig()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Content("{}", "application/json");
        }

        [HttpPost("/api/makerai/activatefreetrial")]
        public IActionResult MakerAiActivateFreeTrial()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Ok(new { success = false, error_id = (string?)null, error = "The Maker AI free trial is not available." });
        }

        // The age-verification button asks for a code to take to a verification page. There is no age verification here: same refusal shape as the
        // ban-appeal code (success / error / value) so the client shows the text instead of failing on a 404.
        [HttpPost("/api/ageverification/generateCode")]
        public IActionResult GenerateAgeVerificationCode()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Ok(new { success = false, error = "Age verification is not available on Campus.", value = "" });
        }

        // "Introduce me to these players" after a friend request is accepted ({friend ids, ...}). Nothing to do - an accepted answer stops the
        // client logging a failed call (same {Success, Message} shape as the cheer reply).
        [HttpPost("/api/relationships/sendfriendintroductions")]
        public IActionResult SendFriendIntroductions()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Content(JsonConvert.SerializeObject(new { Success = true, Message = (string?)null }), "application/json");
        }

        // The friends the caller and another player have in common - what the client shows on
        // someone else's profile. Ported from RecFlare's /relationships/mutualfriends; the C#
        // handler was a stub that returned ConfigCampus.Bracket (nonsense on the profile panel).
        //
        // Answers account cards (AccountId, Username, DisplayName, ProfileImage), ascending by
        // account id, capped at 100. Only real friendships on BOTH sides count. Degenerate cases
        // (no id, id <= 0, or the caller asking for mutuals with themselves) return [] rather than
        // an error - this feeds a profile panel. Mutual ids with no account row are dropped, so
        // the list can be shorter than the raw intersection. ProfileImage is "" (never null).
        [HttpGet("/api/relationships/mutualfriends")]
        public IActionResult GetMutualFriends([FromQuery(Name = "id")] long id = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (id <= 0 || (ulong)id == account.Id)
                return Ok(Array.Empty<object>());

            ulong otherId = (ulong)id;

            HashSet<ulong> FriendIds(ulong who) => FriendsDB.GetRelationships(who)
                .Where(r => r.RelationshipType == RelationshipType.Friend)
                .Select(r => r.PlayerID)
                .ToHashSet();

            var mine = FriendIds(account.Id);
            var theirs = FriendIds(otherId);
            mine.IntersectWith(theirs);
            mine.Remove(account.Id);
            mine.Remove(otherId);

            // The element type is the client's RELATIONSHIP DTO, not an account card:
            // BaseAccountModel.MutualFriendList is List<LANKKABFAEB>, the same type its
            // get_Relationship() returns - {PlayerID, RelationshipType, Favorited, Ignored, Muted},
            // exactly one row of /api/relationships/v2/get. The reference server (and this handler
            // until now) answered account cards, which deserialise into rows with PlayerID 0 - so
            // "friends in common" on another player's profile came up empty even when the server
            // had found ten. The card fields are kept alongside: unknown keys cost nothing, and
            // anything that did read them keeps working.
            var cards = mine
                .OrderBy(x => x)
                .Take(100)
                .Select(fid => PlayerDB.GetFullAccount(fid)?.player)
                .Where(p => p != null)
                .Select(p => new
                {
                    PlayerID = p!.Id,
                    RelationshipType = (int)RelationshipType.Friend,
                    Favorited = 0,
                    Ignored = 0,
                    Muted = 0,
                    AccountId = p.Id,
                    Username = p.Username ?? "",
                    DisplayName = p.DisplayName ?? "",
                    ProfileImage = p.profileImage ?? ""
                })
                .ToList();

            return Ok(cards);
        }


        [HttpGet("/api/CampusCard/v1/SignUpBonus")]
        public IActionResult GetRRPlusSignUpBonus()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                RRPlusSignUpBonusId = 3,
                MinFreeItemsPrice = 6000,
                MaxFreeItemsPrice = 10000
            });
        }

        // Store category carousel -> name needles (reference lists.app.ts STORE_ROW_RULES). A slug
        // here draws store items whose FriendlyName contains ANY needle; null/empty = the whole
        // store (itemsforyou); skinsitems is present so it resolves to an (empty) skins row rather
        // than falling through to the file lookup. The catalog records no real slot, so name match
        // is the crude stand-in the reference uses too.
        // The wardrobe slots (RecRoom.Avatars.OutfitType) each category row sells - the reference's
        // CUSTOM_ITEM_ROW_OUTFIT_TYPES. Used for every Watch row whose slot is known
        // (StoreGenerator.OutfitTypeOf: custom items, and legacy items with a downloadable twin);
        // the name needles below only sort what no record slots.
        private static readonly Dictionary<string, int[]> StoreRowSlots = new(StringComparer.OrdinalIgnoreCase)
        {
            ["headwearitems"]   = new[] { 0 },             // Hat
            ["topsitems"]       = new[] { 101, 104 },      // Shirt, TeamJersey
            ["handsitems"]      = new[] { 200, 203 },      // Wrist, TeamWrist
            ["hairitems"]       = new[] { 2 },             // Hair
            ["facialhairitems"] = new[] { 20 },            // Beard
            ["waistitems"]      = new[] { 102 },           // Waist
            ["accessoriesitems"]= new[] { 3, 10, 103 },    // Ear, Eye, Neck
            ["footwearitems"]   = new[] { 301 },           // Feet
            ["bottomsitems"]    = new[] { 300 },           // Legs
            ["shoulderitems"]   = new[] { 100 },           // Shoulder
        };

        private static readonly Dictionary<string, string[]?> StoreRowRules = new(StringComparer.OrdinalIgnoreCase)
        {
            ["headwearitems"] = new[] { "hat" },
            ["topsitems"] = new[] { "shirt", "jacket", "dress" },
            ["handsitems"] = new[] { "glove", "hand", "wrist" },
            ["hairitems"] = new[] { "hair" },
            ["facialhairitems"] = new[] { "beard", "mustache" },
            ["waistitems"] = new[] { "belt" },
            ["accessoriesitems"] = new[] { "earrings", "hearing aids" },
            ["footwearitems"] = new[] { "shoes", "sneakers", "sandals", "boots" },
            ["bottomsitems"] = new[] { "pants", "shorts" },
            ["shoulderitems"] = new[] { "quiver", "backpack", "sword" },
            ["skinsitems"] = System.Array.Empty<string>(), // no storefront carries skins yet -> empty
            ["itemsforyou"] = null,                        // whole store, no name filter
        };

        [HttpGet("/lists/algorithmiclists/{algorithmicList}")]
        public async Task<IActionResult> GetAlgorithmicList(string algorithmicList, [FromQuery] int type)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var safeFileName = Path.GetFileName(algorithmicList);

            // The store's carousels are algorithmic-list rows too, but they name PURCHASABLE ITEMS
            // (not rooms), and there is no live "what's new in the store" feed - so RecFlare serves
            // them from a fixed, hand-picked id list (STATIC_ROW_ENTITIES). Without this the store's
            // "New" (newitems) and Featured "Medieval" (summerpartycarousel) carousels came back
            // empty and the store landing looked broken. The ids resolve against the storefront
            // catalogs the client then fetches. Keyed on the lowercased slug, like the reference.
            var storeCarousel = safeFileName.ToLowerInvariant();

            // "New" / "Hot" / the featured carousel: a draw from the generated Watch store rather
            // than a hand-picked id list. The old fixed ids (257, 192, 641, 657) were
            // PurchasableItemIds from ANOTHER capture - in ours they either named nothing or
            // named a different item, which is one way a click showed the wrong thing. Nothing
            // here ranks items yet, so it is a random draw of sellable rows - honestly arbitrary,
            // like the reference, instead of pretending to be a ranking.
            if (storeCarousel == "newitems" || storeCarousel == "hotitems" || storeCarousel == "summerpartycarousel")
            {
                var all = (Campus_live_rewrite.api.server.StoreGenerator.MergedWatchStore()["StoreItems"] as JArray ?? new JArray())
                    .Where(it => ((int?)it["GiftDrop"]?["Rarity"] ?? 0) != -1 && !string.IsNullOrEmpty((string?)it["GiftDrop"]?["AvatarItemDesc"]))
                    .Select(it => ((int?)it["PurchasableItemId"])?.ToString())
                    .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                // STABLE within the hour. A fresh Random per request meant the row the client drew
                // the board from and the row it re-fetched when a tile was clicked were two different
                // draws - so tile N now named a different item and the click opened "a random item".
                // Seeded on the hour and the row name, the same request gets the same list until the
                // hour rolls over, which is also what the board's "Items change in..." timer is for.
                var rng0 = new Random(HashCode.Combine(storeCarousel, DateTime.UtcNow.Year, DateTime.UtcNow.DayOfYear, DateTime.UtcNow.Hour));
                var draw = all.OrderBy(_ => rng0.Next()).Take(24).ToList();
                string pfx = type == 5 ? "0." : "";
                return Ok(new
                {
                    Type = type,
                    Entities = draw.Select(id => new { Id = pfx + id, Context = (string?)null }).ToArray()
                });
            }

            // LOCAL EXPERIMENT (HoldNewAccountCreation): the creation screen's clothing tabs are built from customizeitems?type=5. That list is a
            // capture of the real game's store ids (3,065 ids, ~1,500 of which exist in our store), so few bottoms resolve and the Bottoms tab is empty
            // even though the dorm - which lists what the account OWNS - shows plenty. Serve the owned items that our store sells instead.
            if (ConfigCampus.HoldNewAccountCreation && type == 5 && storeCarousel == "customizeitems")
            {
                var ownedSet = PlayerDB.OwnedAvatarItemDescs(account.Id);
                var storeRows = Campus_live_rewrite.api.server.StoreGenerator.MergedWatchStore()["StoreItems"] as JArray ?? new JArray();
                var mine = storeRows
                    .Where(it => !string.IsNullOrWhiteSpace((string?)it["GiftDrop"]?["AvatarItemDesc"])
                              && ownedSet.Contains(PlayerDB.NormalizeAvatarItemDesc((string?)it["GiftDrop"]?["AvatarItemDesc"])))
                    .Select(it => ((int?)it["PurchasableItemId"])?.ToString())
                    .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList();
                Console.WriteLine($"[lists] customizeitems for {account.Id}: {mine.Count} owned item(s) that the store sells (the capture file lists 3065)");
                return Ok(new { Type = type, Entities = mine.Select(id => new { Id = id, Context = (string?)null }).ToArray() });
            }

            // Store CATEGORY carousels, generated from our storefront by NAME - the reference's
            // lists.app.ts STORE_ROW_RULES, ported. The reference's StoreCategories page names a
            // slug per wardrobe slot (headwearitems, topsitems, ..., skinsitems); each draws a
            // random handful of matching store items so those carousels populate instead of showing
            // empty. Ids are our store's PurchasableItemIds, which the client resolves against the
            // storefront it fetches. Generic rows (type=5) need the "0." PurchasableItem prefix;
            // the store's own rows are type=4 and use bare ids. skinsitems stays empty until a
            // storefront carries skins - exactly as the reference notes for its own skins row.
            if (StoreRowRules.TryGetValue(storeCarousel, out var needles))
            {
                var picks = new List<string>();
                if (storeCarousel == "skinsitems")
                {
                    // Every skin row in the Watch store - the captured ones and the ones generated
                    // from Equipments.json (StoreGenerator), stable per hour like the other rows.
                    var rngS = new Random(HashCode.Combine(storeCarousel, DateTime.UtcNow.Year, DateTime.UtcNow.DayOfYear, DateTime.UtcNow.Hour));
                    picks = Campus_live_rewrite.api.server.StoreGenerator.SkinPurchasableIds()
                        .OrderBy(_ => rngS.Next()).Take(60).Select(i => i.ToString()).ToList();
                }
                else
                {
                    // From the GENERATED Watch store, so every id handed out here is one the
                    // client's loaded storefront can resolve to the same item.
                    {
                        var storeItems = Campus_live_rewrite.api.server.StoreGenerator.MergedWatchStore()["StoreItems"] as JArray ?? new JArray();
                        StoreRowSlots.TryGetValue(storeCarousel, out var slots);
                        var matched = storeItems.Where(it =>
                        {
                            var gd = it["GiftDrop"];
                            if (gd == null) return false;
                            if (((int?)gd["Rarity"] ?? 0) == -1) return false;            // unsellable tier
                            if (!string.IsNullOrEmpty((string?)gd["EquipmentPrefabName"])) return false;   // skins have their own row
                            if (needles == null || needles.Length == 0) return true;      // whole store (itemsforyou)
                            // The real slot when a record carries one; the name only for the rest.
                            int slot = Campus_live_rewrite.api.server.StoreGenerator.OutfitTypeOf(it);
                            if (slot >= 0) return slots != null && slots.Contains(slot);
                            string name = (((string?)gd["FriendlyName"]) ?? "").ToLowerInvariant();
                            return needles.Any(n => name.Contains(n));
                        })
                        .Select(it => ((int?)it["PurchasableItemId"])?.ToString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Select(s => s!)
                        .ToList();

                        // Same stable-per-hour seed as the featured rows, for the same reason: a
                        // re-fetch during a click must return the list the board was drawn from.
                        var rng = new Random(HashCode.Combine(storeCarousel, DateTime.UtcNow.Year, DateTime.UtcNow.DayOfYear, DateTime.UtcNow.Hour));
                        picks = matched.OrderBy(_ => rng.Next()).Take(50).ToList(); // reference: ORDER BY RANDOM() LIMIT 50
                    }
                }

                string idPrefix = type == 5 ? "0." : "";
                return Ok(new
                {
                    Type = type,
                    Entities = picks.Select(pid => new { Id = idPrefix + pid, Context = (string?)null }).ToArray()
                });
            }

            // Room rows (type 1 = Rooms): built from this server's rooms - foryou, HotList,
            // staffpicks, new, recentlyupdated, inkshowcase and the genre rows. These answered
            // from files that mostly didn't exist, which is why the Play tab's carousels were blank.
            if (type == 1 && Campus_live_rewrite.api.server.DiscoveryLists.Handles(safeFileName))
            {
                var ids = Campus_live_rewrite.api.server.DiscoveryLists.RoomIds(safeFileName);
                return Ok(new
                {
                    Type = type,
                    Entities = ids.Select(id => new { Id = id, Context = (string?)null }).ToArray()
                });
            }

            string path = Path.Combine(
                Environment.CurrentDirectory,
                "Data", "APIS", "AlgorithmicLists",
                $"{safeFileName}_{type}.json"
            );

            if (!System.IO.File.Exists(path))
                return Ok(new
                {
                    Type = type,
                    Entities = Array.Empty<object>()
                });

            try 
            {
                var jsonText = await System.IO.File.ReadAllTextAsync(path);
                var node = JsonNode.Parse(jsonText);
                
                return Ok(new
                {
                    Type = type,
                    Entities = node as JsonArray ?? new JsonArray()
                });
            }
            catch (System.Text.Json.JsonException)
            {
                return Ok(new
                {
                    Type = type,
                    Entities = Array.Empty<object>()
                });
            }
        }

        // The room genre tags a room can be filed under (wire-shapes: curatedlists-lookup-and-
        // pagesource-names: name=RoomGenreTags&type=5, ItemIds are the tag names verbatim). Was a
        // placeholder with no ids, so room settings offered no genre.
        private static readonly string[] RoomGenreTags =
            { "Hangout", "Battle", "Quests", "Roleplay", "Horror", "Casual", "Explore", "Art", "Sports", "Music", "Parkour", "Comedy" };

        private static object CuratedListObj(long listId, ulong creator, string name, int type, IEnumerable<string> ids, DateTime? created = null, int? accessibility = null)
        {
            var o = new JObject
            {
                ["ListId"] = listId,
                ["CreatorAccountId"] = (long)creator,
                ["Name"] = name,
                ["Description"] = null,
                ["ImageName"] = "",
                ["Type"] = type,
                ["ItemIds"] = new JArray(ids),
                ["CreatedAt"] = (created ?? new DateTime(2025, 7, 18, 0, 0, 0, DateTimeKind.Utc)).ToString("o"),
            };
            if (accessibility.HasValue) o["Accessibility"] = accessibility.Value;
            return o;
        }

        [HttpGet("/lists/curatedlists")]
        public async Task<IActionResult> GetCuratedlists([FromQuery] ulong creatorAccountId, [FromQuery] ulong type, [FromQuery] string name)
        {
            Console.WriteLine($"[lists] curatedlists{Request.QueryString}");
            name ??= "";

            // A player's OWN list (Saved for Later) - a real, stable ListId whether or not it has
            // been created yet, so the client's save has something to save into. Was ListId 0,
            // which the client reads as "No such list" and never writes to.
            if (name.StartsWith("__") || CuratedListDB.Get(creatorAccountId, (int)type, name) != null)
            {
                var mine = CuratedListDB.Get(creatorAccountId, (int)type, name);
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(mine != null
                        ? CuratedListObj(mine.ListId, mine.CreatorAccountId, mine.Name, mine.Type, mine.ItemIds, mine.CreatedAt, mine.Accessibility)
                        : CuratedListObj(CuratedListDB.IdFor(creatorAccountId, (int)type, name), creatorAccountId, name, (int)type, Array.Empty<string>(), null, 0)),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (string.Equals(name, "RoomGenreTags", StringComparison.OrdinalIgnoreCase))
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(CuratedListObj(1, 1, "RoomGenreTags", 5, RoomGenreTags)),
                    ContentType = "application/json",
                    StatusCode = 200
                };

            string path = Path.Combine(
                Environment.CurrentDirectory,
                "Data", "APIS", "CuratedLists",
                $"{creatorAccountId}_{type}_{name}.json"
            );

            if (System.IO.File.Exists(path))
                return PhysicalFile(path, "application/json");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(CuratedListObj(0, creatorAccountId, name, (int)type, Array.Empty<string>())),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Save a room for later (reference: PUT /curatedlists/{name}/items/{itemId}/createlistifneeded).
        // The owner is the TOKEN's account; `type` and `accessibility` (query or form) apply only
        // when the list is created. Answers the list as it now stands, without Accessibility.
        [HttpPut("/lists/curatedlists/{name}/items/{itemId}/createlistifneeded")]
        [HttpPost("/lists/curatedlists/{name}/items/{itemId}/createlistifneeded")]
        public IActionResult SaveToCuratedList(string name, string itemId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            string Q(string k) { string v = Request.Query[k].ToString(); if (string.IsNullOrEmpty(v) && Request.HasFormContentType) v = Request.Form[k].ToString(); return v; }
            int type = int.TryParse(Q("type"), out var t) ? t : 1;
            int acc = int.TryParse(Q("accessibility"), out var a) ? a : 0;
            var list = CuratedListDB.AddItem(account.Id, type, name, acc, itemId);
            Console.WriteLine($"[lists] {account.Id} saved {itemId} into '{name}' ({list.ItemIds.Count} item(s))");
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(CuratedListObj(list.ListId, list.CreatorAccountId, list.Name, list.Type, list.ItemIds, list.CreatedAt)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Un-save (the spelling is a guess at the reference's inverse; any other write under
        // /lists/curatedlists is logged by the catch-all below so it can be named).
        [HttpDelete("/lists/curatedlists/{name}/items/{itemId}")]
        [HttpDelete("/lists/curatedlists/{name}/items/{itemId}/createlistifneeded")]
        public IActionResult RemoveFromCuratedList(string name, string itemId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            int type = int.TryParse(Request.Query["type"], out var t) ? t : 1;
            var list = CuratedListDB.RemoveItem(account.Id, type, name, itemId);
            if (list == null) return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
            Console.WriteLine($"[lists] {account.Id} removed {itemId} from '{name}'");
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(CuratedListObj(list.ListId, list.CreatorAccountId, list.Name, list.Type, list.ItemIds, list.CreatedAt)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("/lists/curatedlists/{**rest}")]
        [HttpPost("/lists/curatedlists/{**rest}")]
        [HttpDelete("/lists/curatedlists/{**rest}")]
        public IActionResult UnknownCuratedListWrite(string rest)
        {
            string body = "";
            try { if (Request.HasFormContentType) body = string.Join("&", Request.Form.Select(kv => kv.Key + "=" + kv.Value)); } catch { }
            Console.WriteLine($"[lists] UNHANDLED {Request.Method} /lists/curatedlists/{rest}{Request.QueryString} body='{body}'");
            return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
        }        // Bulk curated-list lookup (the client repeats ?id=). The store landing (StoreCategories)
        // has a "store-featured" section sourced from a CuratedList, and it fetches the list here;
        // this route was MISSING, so the section 404'd and the store landing had nothing to draw.
        // Matches RecFlare: one canned list whatever is asked for, shaped the way the client parses
        // it - ItemIds are STRINGS, Description may be null, ImageName MUST be a string. Answers 200
        // even for an unknown id (a 404 shows as a row that failed to load).
        [HttpGet("/lists/curatedlists/bulk")]
        public IActionResult GetCuratedListsBulk()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            Console.WriteLine($"[lists] curatedlists/bulk{Request.QueryString}");

            // The Play/Watch pages ask for live Rec Room's playlists by id ("Fun with Friends",
            // "New Ways to Play"). Each is answered as a ROOM list built here: a playlist id the
            // page names maps to one of the DiscoveryLists rows, anything else to the hot list.
            // Was a placeholder list of two room ids that don't exist.
            var lists = new List<object>();
            foreach (var raw in Request.Query["id"])
            {
                if (!long.TryParse(raw, out var listId)) continue;
                var owned = CuratedListDB.GetById(listId);
                if (owned != null)
                {
                    lists.Add(CuratedListObj(owned.ListId, owned.CreatorAccountId, owned.Name, owned.Type, owned.ItemIds, owned.CreatedAt));
                    continue;
                }
                string row = listId switch
                {
                    7732248245192312892 => "hangout_algoendpoint",   // Fun with Friends
                    640498224290317839  => "new",                    // New Ways to Play
                    _                   => "hotlist",
                };
                string name = listId switch
                {
                    7732248245192312892 => "Fun with Friends",
                    640498224290317839  => "New Ways to Play",
                    _                   => "Trending",
                };
                lists.Add(new
                {
                    ListId = listId,
                    CreatorAccountId = 1L,
                    Name = name,
                    Description = (string?)null,
                    ImageName = "",
                    Type = 1,                       // ListEntityType: 0 Accounts, 1 Rooms, 2 Inventions, 3 CustomAvatarItems, 4 PurchasableItems, 5 Generic
                    ItemIds = Campus_live_rewrite.api.server.DiscoveryLists.RoomIds(row).ToArray(),
                    CreatedAt = "2025-07-18T00:00:00Z"
                });
            }
            return Ok(lists);
        }

        // The charades activity's word lists - Icebreakers, Charades, ... - one file per list
        // in Data/APIS/Charades/<list>.json ([{Id, Difficulty, EN_US}]). ^3DCharades and
        // ^Legacy3DCharades ask for /words/Charades when a round starts; when that file was
        // missing this answered [] and the cards fell back to the client's built-in words.
        // Charades.json is the full 1.3k-word bank; CharadesApril.json replaces it on April 1st
        // (UTC), and a list name with no file of its own gets the Charades bank rather than
        // nothing (mirrors the reference server, where the name isn't matched on at all).
        [HttpGet("/api/activities/charades/v1/words/{list}")]
        public IActionResult GetCharadesWords(string list, ulong RoomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string dir = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "Charades");
            string safe = System.Text.RegularExpressions.Regex.Replace(list ?? "", @"[^A-Za-z0-9_-]", "");
            string path = Path.Combine(dir, safe + ".json");
            if (safe.Length == 0 || !System.IO.File.Exists(path))
                path = Path.Combine(dir, "Charades.json");
            if (safe.Equals("Charades", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTime.UtcNow;
                string april = Path.Combine(dir, "CharadesApril.json");
                if (now.Month == 4 && now.Day == 1 && System.IO.File.Exists(april)) path = april;
            }
            if (!System.IO.File.Exists(path))
                return Ok(ConfigCampus.Bracket);

            return PhysicalFile(path, "application/json");
        }

        [HttpGet("/aimoderation/roomie/enabledForPlayer")]
        public IActionResult IsRoomieEnabledForPlayer()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Was: Ok(<string>). ASP.NET's StringOutputFormatter writes a raw string as
            // Content-Type: text/plain, so these JSON payloads were served mislabelled
            // and the client would not parse them. Verified live: /outfits/me returned
            // text/plain while /api/avatar/v2 returned application/json. Same bytes,
            // correct content type.
            return new ContentResult { Content = "true", ContentType = "application/json", StatusCode = 200 };
        }

        [HttpPost("/aimoderation/roomie/conversation")]
        public IActionResult SaveRoomieConversation([FromForm] string conversation)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            if (string.IsNullOrEmpty(conversation)) 
                return BadRequest();

            string pattern = @"(?<=\d{2}:\d{2}:\d{2}\s)(USER|AI)(?=\s\(|:)";

            string processedConversation = Regex.Replace(conversation ?? "", pattern, m => 
            {
                return m.Value == "USER" ? account.Username : "Roomie";
            });

            _ = Task.Run(() => Webhook.Send(
                        account.Id, 
                        "roomie", 
                        "Roomie Info",
                        $"User: @{account.Username} ({account.Id})\n\nLog: {processedConversation}",
                        5898927, 
                        null
                    ));

            return Ok();
        }

        [HttpGet("/api/players/v1/playerPhotoTaggingSetting")]
        public IActionResult GetPlayerPhotoTaggingSetting()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) 
                return error;

            return Ok(PlayerDB.GetPlayerPhotoTaggingSetting(account.Id));
        }

        [HttpPut("/api/players/v1/playerPhotoTaggingSetting")]
        public IActionResult SetPlayerPhotoTaggingSetting([FromBody] SetPlayerPhotoTaggingSettingRequest request)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) 
                return error;

            return Ok(PlayerDB.SetPlayerPhotoTaggingSetting(account.Id, request.Setting));
        }

        [HttpGet("/api/keepsakes/globalconfig")]
        public IActionResult GetKeepsakeGlobalConfig()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) 
                return error;

            return Ok(new
            {
                KeepsakeFeatureEnabled = true,
                KeepsakeRoomLimit = 10,
                SocialXpBoostEnabled = false
            });
        }

        // The pending quick-play action (join a friend / deep link), read-and-clear. Nothing
        // queues one, so all three fields are null - the reference shape; a 204 read as
        // "Response was empty".
        [HttpGet("/api/quickPlay/v1/getandclear")]
        public IActionResult GetAndClearQuickPlay()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) 
                return error;

            return Content("{\"RoomName\":null,\"ActionCode\":null,\"TargetPlayerId\":null}", "application/json");
        }

        [HttpGet("/api/keepsakes/rooms/{roomId}")]
        public IActionResult GetKeepsakesForRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) 
                return error;

            return NoContent();
        }

        [HttpPost("/api/messages/v1/friendOnlineStatus")]
        public IActionResult GetFriendOnlineCount()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Real count: the caller's accepted friends that currently hold a live notification
            // websocket. Was int.MaxValue as a joke, which surfaced verbatim in the client's
            // "Failed to load room" dialog as "You have 2147483647 friends online."
            return Ok(new
            {
                success = true,
                value = new
                {
                    FriendsOnlineCount = FriendsDB.CountOnlineFriends(account.Id)
                }
            });
        }

        [HttpGet("/api/purchasableXpBoosts/activations")]
        public IActionResult GetPurchasableXpBoosts()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            
            return Ok(new
            {
                Results = Array.Empty<object>(),
                TotalResults = 0
            });
        }

        [HttpPost("/api/consumables/v1/updateActive")]
        public IActionResult UpdateActiveConsumable()
        {
            return NoContent();
        }

        // A player's inventions as another player sees them (the profile's Inventions tab):
        // PUBLISHED ones only; the creator's own private saves stay in /inventions/v2/mine.
        [HttpGet("/api/inventions/v1/fromcreators")]
        public IActionResult InventionsFromCreator([FromQuery] ulong id, [FromQuery] int skip, [FromQuery] int take)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            bool self = id == account.Id;
            var list = InventionDB.GetByCreator(id)
                .Where(i => self || i.IsPublished)
                .Select(Campus_live_rewrite.Controllers.InventionController.InventionDto).ToList();
            if (skip > 0) list = list.Skip(skip).ToList();
            if (take > 0) list = list.Take(take).ToList();
            return new ContentResult { Content = JsonConvert.SerializeObject(list), ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("/sections/bulk")]
        public IActionResult GetSectionsBulk([FromQuery] List<string> id)
        {
            var filePath = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "WatchPages", "bulk.json");

            if (!System.IO.File.Exists(filePath))
                return NotFound();

            var jsonString = System.IO.File.ReadAllText(filePath);
            var allSections = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object>>>(jsonString);

            if (id == null || !id.Any())
                return Ok(new List<object>());

            var filtered = allSections.Where(s => s.ContainsKey("id") && id.Contains(s["id"]?.ToString())).ToList();

            return Ok(filtered);
        }
    }
}
