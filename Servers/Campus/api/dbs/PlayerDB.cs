using System;
using System.Numerics;
using System.Security.Principal;
using LiteDB;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
using Campus_live_rewrite.Controllers;
using SixLabors.ImageSharp;
using static ImageMetadataDB;

namespace Campus_live_rewrite.api.dbs
{
    public class PlayerDB
    {
        public static LiteDatabase PlayerDBFile = new LiteDatabase(Environment.CurrentDirectory + $"/Data/DBs/Players.db");
        private static readonly Random random = new Random();
        private static readonly object randLock = new object();

        public static bool CreateAccount(Platforms platform, ulong platformId, string deviceId, ulong discord_id, out account_data account)
        {
            account = new account_data();

            Console.WriteLine($"[CreateAccount] === START === platform={platform} platformId={platformId} deviceId='{deviceId}' discord_id={discord_id}");

            var col = PlayerDBFile.GetCollection<player_data>("players");
            var maxId = col.FindAll().OrderByDescending(x => x.id).FirstOrDefault()?.id ?? 0;
            ulong accid = maxId + 1;
            account.accountId = accid;
            string name = Utils.GetRandomName();

            Console.WriteLine($"[CreateAccount] Assigning accid={accid} (maxId was {maxId}), generated name='{name}'");

            account = new account_data
            {
                createdAt = DateTime.UtcNow,
                identityFlags = 0,
                isJunior = null,
                personalPronouns = 0,
                platforms = 0,
                displayName = name,
                username = name,
                profileImage = "DefaultPFP.png",
                accountId = accid
            };
            col.Insert(new player_data
            {
                playerid = account.accountId,
                id = accid,
                platform = platform,
                //platformId = platformId,
                CreatedInGame = true,
                deviceId = new List<string>
                {
                    deviceId
                },
                authtoken = Utils.GetRandomString(400),
                password = "",
                player = new player_info
                {
                    Id = accid,
                    Username = account.username,
                    DisplayName = account.displayName,
                    XP = 0,
                    Level = 1,
                    created_at = account.createdAt,
                    bannerImage = Campus_live_rewrite.api.server.ConfigCampus.DefaultBannerImage,
                    profileImage = account.profileImage,
                    IsJunior = false,
                    AvoidJuniors = true,
                    PlayerReputation = new mPlayerReputation
                    {
                        Noteriety = 0,
                        CheerCredit = 20,
                        CheerGeneral = 1,
                        CheerHelpful = 1,
                        CheerGreatHost = 1,
                        CheerSportsman = 1,
                        CheerCreative = 1,
                        SubscriberCount = 0,
                        SubscribedCount = 0,
                        SelectedCheer = CheerCategoryEnum.None
                    },
                    PlatformIds = new List<mPlatformID>
                    {
                         new mPlatformID
                         {
                             Platform = platform,
                             PlatformId = platformId
                         }
                     }
                },
                player_Extra = new player_extra
                {
                    DormRoomID = 1,
                    IpAddresses = new List<string>(),
                    DiscordId = discord_id,
                    Bio = "",
                    avatar = "{\"FaceFeatures\": \"\",\"HairColor\": \"\",\"OutfitSelections\": \"\",\"SkinColor\": \"\"}",
                    SavedOutfits = new List<avatar_data_saved>(),
                    //ModerationBlockDetails = new Moderation_Detail.ModerationBlockDetails()
                    CheeredRooms = new List<ulong>(),
                    FavoritedRooms = new List<ulong>(),
                    sub_count = 0,
                    SubbedTo = new List<ulong>(),
                    IsRecentRoomHistoryVisible = true,
                    Influencer = new Influencer
                    {
                        creatorCode = "",
                        isInfluencer = false,
                    },
                    CustomAvatarItems = new Campus_live_rewrite.api.server.Classes.db_class.CustomAvatarItems(),
                },
                dev_Flag = DevFlag.none
            });
            //Settings.SaveSettings_db(Settings.CreateDefaultSettings(), accid);
            Console.WriteLine($"[CreateAccount] Row inserted. accid={accid} platform={platform} discord_id={discord_id} display='{name}' img='{account.profileImage}'");
            HeartbeatDB.CreatePlayerHeartbeat(accid, out _, platform, DeviceClasses.Unknown);
            Console.WriteLine($"[CreateAccount] Heartbeat created for accid={accid}");
            CreateDormForPlayerId(accid);
            Console.WriteLine($"[CreateAccount] Dorm created for accid={accid}. === DONE ===");
            return true;
        }

        // ================================================================================
        // WEBSITE ACCOUNT CREATION  (Campus accounts are now made on the website, not in game)
        //
        // The game used to auto-create an account on first launch (AuthController
        // grant_type=create_account). That path is disabled - accounts are made here, on the
        // site, with a username + password the player chooses, and the game only ever LOGS IN
        // to them. The account carries NO platform binding at creation: a browser has no Steam
        // id. The Steam id is stamped on the account the first time the player signs in from
        // the game (AccountLock.TryEnforceAndBind), which is what "locks the account to your
        // Steam" means.
        // ================================================================================

        /// <summary>Case-insensitive username -> account id, or 0 if no such account.</summary>
        public static ulong GetAccountIdByUsernameCI(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return 0;
            string want = username.Trim();
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var p = col.FindAll().FirstOrDefault(x =>
                x?.player?.Username != null &&
                string.Equals(x.player.Username, want, StringComparison.OrdinalIgnoreCase));
            return p?.playerid ?? 0;
        }

        /// <summary>Case-insensitive: is this exact username already in use?</summary>
        public static bool IsUsernameTaken(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return true;
            string want = username.Trim();
            var col = PlayerDBFile.GetCollection<player_data>("players");
            return col.FindAll().Any(p =>
                p?.player?.Username != null &&
                string.Equals(p.player.Username, want, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Create an account from the website. No device id, no platform binding - those are
        /// stamped on the first in-game login. The account is marked onboarding-complete so the
        /// game drops the player straight in (no first-run "make a room" / username / password
        /// flow) since they already chose all of that here.
        /// </summary>
        public static bool CreateWebAccount(string username, string displayName, string password,
                                            string? email, out account_data account, out string error,
                                            bool isJunior = false, string? birthday = null)
        {
            account = new account_data();
            error = "";

            username = (username ?? "").Trim();
            displayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();

            if (IsUsernameTaken(username))
            {
                error = "That username is already taken.";
                return false;
            }

            var col = PlayerDBFile.GetCollection<player_data>("players");
            var maxId = col.FindAll().OrderByDescending(x => x.id).FirstOrDefault()?.id ?? 0;
            ulong accid = maxId + 1;

            account = new account_data
            {
                createdAt = DateTime.UtcNow,
                identityFlags = 0,
                isJunior = isJunior,
                personalPronouns = 0,
                platforms = 0,
                displayName = displayName,
                username = username,
                profileImage = "DefaultPFP.png",
                accountId = accid
            };

            col.Insert(new player_data
            {
                playerid = accid,
                id = accid,
                platform = Platforms.All,
                CreatedInGame = false,
                deviceId = new List<string>(),          // bound on first in-game login
                authtoken = Utils.GetRandomString(400),
                password = ClientSecurity.HashPassword(password),
                player = new player_info
                {
                    Id = accid,
                    Username = username,
                    DisplayName = displayName,
                    Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                    Birthday = string.IsNullOrWhiteSpace(birthday) ? null : birthday.Trim(),
                    XP = 0,
                    Level = 1,
                    created_at = account.createdAt,
                    bannerImage = Campus_live_rewrite.api.server.ConfigCampus.DefaultBannerImage,
                    profileImage = account.profileImage,
                    IsJunior = isJunior,
                    // Juniors are placed among juniors (don't avoid them); adults avoid juniors by default.
                    AvoidJuniors = !isJunior,
                    PlayerReputation = new mPlayerReputation
                    {
                        Noteriety = 0,
                        CheerCredit = 20,
                        CheerGeneral = 1,
                        CheerHelpful = 1,
                        CheerGreatHost = 1,
                        CheerSportsman = 1,
                        CheerCreative = 1,
                        SubscriberCount = 0,
                        SubscribedCount = 0,
                        SelectedCheer = CheerCategoryEnum.None
                    },
                    PlatformIds = new List<mPlatformID>()   // NO steam id yet
                },
                player_Extra = new player_extra
                {
                    DormRoomID = 1,
                    IpAddresses = new List<string>(),
                    DiscordId = 0,
                    Bio = "",
                    avatar = "{\"FaceFeatures\": \"\",\"HairColor\": \"\",\"OutfitSelections\": \"\",\"SkinColor\": \"\"}",
                    SavedOutfits = new List<avatar_data_saved>(),
                    CheeredRooms = new List<ulong>(),
                    FavoritedRooms = new List<ulong>(),
                    sub_count = 0,
                    SubbedTo = new List<ulong>(),
                    IsRecentRoomHistoryVisible = true,
                    Influencer = new Influencer { creatorCode = "", isInfluencer = false },
                    CustomAvatarItems = new Campus_live_rewrite.api.server.Classes.db_class.CustomAvatarItems(),
                },
                dev_Flag = DevFlag.none
            });

            HeartbeatDB.CreatePlayerHeartbeat(accid, out _, Platforms.All, DeviceClasses.Unknown);
            CreateDormForPlayerId(accid);

            // Mark first-run onboarding + tutorial complete so the game goes straight in - the
            // player already chose username/password on the website (mirrors the Kyle seed in
            // Program.cs and SettingsDB.CreateDefaultSettings).
            SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasStarted",        "true", accid);
            SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasFinished",       "true", accid);
            SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasChosenUsername", "true", accid);
            SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasCreatedPassword","true", accid);
            SettingsDB.SetPlayerSetting("TUTORIAL_COMPLETE_MASK",                    "4294967295", accid);

            Console.WriteLine($"[web-signup] created account {accid} @{username} (\"{displayName}\")"
                            + (email != null ? " with email" : ""));
            return true;
        }

        /// <summary>
        /// The account's bound platform id for a platform (0 if none). Ignores the placeholder
        /// 0 entry that some legacy rows carry.
        /// </summary>
        public static ulong GetAccountPlatformId(ulong accid, Platforms platform)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var p = col.FindOne(x => x.playerid == accid);
            var ids = p?.player?.PlatformIds;
            if (ids == null) return 0;
            var match = ids.FirstOrDefault(pid => pid.Platform == platform && pid.PlatformId != 0);
            return match?.PlatformId ?? 0;
        }

        /// <summary>Append a platform id to the account if it is not already present.</summary>
        public static void AddPlatformId(ulong accid, Platforms platform, ulong platformId)
        {
            if (platformId == 0) return;
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var p = col.FindOne(x => x.playerid == accid);
            if (p?.player == null) return;
            p.player.PlatformIds ??= new List<mPlatformID>();
            if (p.player.PlatformIds.Any(pid => pid.Platform == platform && pid.PlatformId == platformId))
                return;
            p.player.PlatformIds.Add(new mPlatformID { Platform = platform, PlatformId = platformId });
            col.Update(p);
            Console.WriteLine($"[account-lock] bound {platform} id {platformId} to account {accid}");
        }

        /// <summary>Every account carrying this platform id (used by the one-account-per-Steam lock).</summary>
        public static List<ulong> AccountsWithPlatformId(Platforms platform, ulong platformId)
        {
            if (platformId == 0) return new List<ulong>();
            var col = PlayerDBFile.GetCollection<player_data>("players");
            return col.FindAll()
                      .Where(x => x.player?.PlatformIds != null &&
                                  x.player.PlatformIds.Any(pid => pid.Platform == platform && pid.PlatformId == platformId))
                      .Select(x => x.playerid)
                      .ToList();
        }

        // ---- website signup rate limit -----------------------------------------------------
        // One successful account creation per IP per ConfigCampus.SignupRateLimitDays (default 7).
        // Persisted so a service restart does not reset the window. A tiny row per signup - the
        // volume is human-scale, not request-scale.
        public class WebSignupRecord
        {
            public int Id { get; set; }
            public string? Ip { get; set; }
            public ulong AccountId { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        /// <summary>The limiter's verdict: which layer refused, and when it frees up.</summary>
        public enum SignupLimit { None, Ip, Subnet, Global }

        /// <summary>
        /// May this IP register right now? Checks, in order, the global per-hour cap, the per-IP
        /// day/week caps and the /24 subnet cap (see ConfigCampus.SignupsPer*). On refusal says
        /// which layer refused and the earliest time it frees up, so the caller can tell the
        /// player something true. Blank/loopback/allowlisted IPs skip the per-address layers.
        /// </summary>
        public static SignupLimit CheckSignupLimit(string? ip, out DateTime nextAllowedUtc)
        {
            var now = DateTime.UtcNow;
            nextAllowedUtc = now;
            // Admin-allowlisted IPs test signup flows without tripping the caps (see
            // ConfigCampus.AdminRateLimitEnabled).
            if (AdminProtectionMiddleware.IsLimitExempt(ip)) return SignupLimit.None;
            var col = PlayerDBFile.GetCollection<WebSignupRecord>("web_signups");
            col.EnsureIndex(r => r.Ip);
            col.EnsureIndex(r => r.CreatedAt);

            // Global cap. This is a MASS-CREATION circuit breaker, not a growth cap, and for a
            // long time it behaved as the latter: at 15/hour across every address on the internet
            // it was the ONLY limit that ever fired. In one 12-hour window it refused 399 signups
            // - 120 of them headsets retrying create_account, 34 distinct devices - while letting
            // 19 through, and not one refusal came from the per-IP or per-subnet caps. Every real
            // player who arrived after the 15th that hour was told "try again later"; on Quest
            // that is a hard login failure the client then retries every ~8 s forever.
            //
            // So volume alone no longer trips it. Mass creation is CONCENTRATED - one actor behind
            // a handful of networks - because the per-IP (1/day) and per-/24 (4/day) caps already
            // bound what any single network can produce. Organic growth is DIFFUSE: a hundred
            // strangers on a hundred networks. The breaker now fires only when an hour is both
            // over the ceiling AND coming from few enough networks to be one actor.
            if (ConfigCampus.SignupsPerHour > 0)
            {
                var hourAgo = now.AddHours(-1);
                var lastHour = col.Find(r => r.CreatedAt >= hourAgo).OrderBy(r => r.CreatedAt).ToList();
                if (lastHour.Count >= ConfigCampus.SignupsPerHour)
                {
                    // Distinct networks behind the hour. An unattributable IP counts as its own
                    // network rather than being pooled with the other unattributable ones, so a
                    // flood cannot look diffuse just by stripping the address.
                    int networks = lastHour
                        .Select(r => SubnetKey(r.Ip) ?? ("ip:" + (r.Ip ?? "")))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    if (networks < ConfigCampus.SignupBurstDistinctNetworks)
                    {
                        nextAllowedUtc = lastHour[lastHour.Count - ConfigCampus.SignupsPerHour].CreatedAt.AddHours(1);
                        Console.WriteLine($"[signup-guard] GLOBAL cap tripped: {lastHour.Count} signups in the last hour " +
                                          $"from only {networks} distinct network(s) - mass creation, not growth");
                        return SignupLimit.Global;
                    }

                    Console.WriteLine($"[signup-guard] {lastHour.Count} signups in the last hour across {networks} " +
                                      $"distinct networks - over the ceiling but diffuse, allowed");
                }
            }

            if (string.IsNullOrWhiteSpace(ip)) return SignupLimit.None;
            ip = ip.Trim();
            if (ip == "127.0.0.1" || ip == "::1" || ip == "localhost") return SignupLimit.None;
            if (ConfigCampus.SignupExemptIps != null && ConfigCampus.SignupExemptIps.Contains(ip)) return SignupLimit.None;

            var weekAgo = now.AddDays(-7);
            var dayAgo = now.AddDays(-1);
            var mine = col.Find(r => r.Ip == ip && r.CreatedAt >= weekAgo).OrderBy(r => r.CreatedAt).ToList();

            if (ConfigCampus.SignupsPerIpPerWeek > 0 && mine.Count >= ConfigCampus.SignupsPerIpPerWeek)
            {
                nextAllowedUtc = mine[mine.Count - ConfigCampus.SignupsPerIpPerWeek].CreatedAt.AddDays(7);
                return SignupLimit.Ip;
            }
            var mineToday = mine.Where(r => r.CreatedAt >= dayAgo).ToList();
            if (ConfigCampus.SignupsPerIpPerDay > 0 && mineToday.Count >= ConfigCampus.SignupsPerIpPerDay)
            {
                nextAllowedUtc = mineToday[mineToday.Count - ConfigCampus.SignupsPerIpPerDay].CreatedAt.AddDays(1);
                return SignupLimit.Ip;
            }

            // Network block - /24 for IPv4, /64 for IPv6. Same window as the per-IP day cap.
            string? block = SubnetKey(ip);
            if (block != null && ConfigCampus.SignupsPerSubnetPerDay > 0)
            {
                // Compare on the computed KEY, not a text prefix: one IPv6 address can be written
                // several ways ("2001:db8::1" / "2001:db8:0:0::1"), so matching the stored string
                // missed hosts that a real /64 comparison catches.
                var inBlock = col.Find(r => r.CreatedAt >= dayAgo)
                                 .Where(r => string.Equals(SubnetKey(r.Ip), block, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(r => r.CreatedAt).ToList();
                if (inBlock.Count >= ConfigCampus.SignupsPerSubnetPerDay)
                {
                    nextAllowedUtc = inBlock[inBlock.Count - ConfigCampus.SignupsPerSubnetPerDay].CreatedAt.AddDays(1);
                    return SignupLimit.Subnet;
                }
            }
            return SignupLimit.None;
        }

        /// <summary>
        /// The network an address belongs to: the /24 for IPv4, the /64 for IPv6, null when the
        /// string is not an address at all.
        ///
        /// IPv6 used to return null, so an IPv6 client had NO subnet cap - the per-/24 rule simply
        /// did not apply to it and the global hourly ceiling was the only thing covering that
        /// hole, which is part of why the ceiling had to be set so low. A /64 is what a
        /// residential customer is handed, so it is the IPv6 equivalent of the /24 here.
        /// </summary>
        private static string? SubnetKey(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return null;
            if (!System.Net.IPAddress.TryParse(ip.Trim(), out var addr)) return null;
            var b = addr.GetAddressBytes();

            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return "v4:" + b[0] + "." + b[1] + "." + b[2];

            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var sb = new System.Text.StringBuilder("v6:");
                for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("x2"));   // the /64 prefix
                return sb.ToString();
            }
            return null;
        }

        public static void RecordSignup(string? ip, ulong accountId)
        {
            var col = PlayerDBFile.GetCollection<WebSignupRecord>("web_signups");
            col.Insert(new WebSignupRecord
            {
                Ip = string.IsNullOrWhiteSpace(ip) ? "" : ip.Trim(),
                AccountId = accountId,
                CreatedAt = DateTime.UtcNow
            });
        }

        public static bool ImportPlayers(string importPath)
        {
            Console.WriteLine($"[IMPORT] Starting player import");
            Console.WriteLine($"[IMPORT] Import path: {importPath}");

            try
            {
                if (!File.Exists(importPath))
                {
                    Console.WriteLine($"[ERROR] Import file not found: {importPath}");
                    return false;
                }

                Console.WriteLine("[IMPORT] Reading import file...");
                var json = File.ReadAllText(importPath);
                Console.WriteLine($"[IMPORT] File size: {json.Length} bytes");

                Console.WriteLine("[IMPORT] Deserializing player data...");
                var importedPlayers = JsonConvert.DeserializeObject<List<player_data>>(json);

                if (importedPlayers == null)
                {
                    Console.WriteLine("[ERROR] Deserialization returned null (invalid JSON?)");
                    return false;
                }

                if (importedPlayers.Count == 0)
                {
                    Console.WriteLine("[WARN] Import file contained 0 players");
                    return false;
                }

                Console.WriteLine($"[IMPORT] Players found in file: {importedPlayers.Count}");

                var col = PlayerDBFile.GetCollection<player_data>("players");

                int inserted = 0;
                int skipped = 0;

                foreach (var player in importedPlayers)
                {
                    if (player == null)
                    {
                        Console.WriteLine("[WARN] Encountered null player entry, skipping");
                        skipped++;
                        continue;
                    }

                    Console.WriteLine($"[IMPORT] Processing player ID: {player.id}");

                    try
                    {
                        // Avoid duplicate IDs
                        if (col.Exists(x => x.id == player.id))
                        {
                            Console.WriteLine($"[SKIP] Duplicate player ID {player.id}");
                            skipped++;
                            continue;
                        }
                        player.player_Extra.DormRoomID = RoomDB.clone_dorm_room(player.playerid);

                        col.Insert(player);
                        var name = player.player.Username ?? "<unknown>";
                        var displayName = player.player.DisplayName ?? "<unknown>";
                        var displayEmoji = player.player.DisplayEmoji ?? "";
                        Console.WriteLine($"[OK] Inserted player ({player.id}) @{name} {displayName} {displayEmoji}");
                        inserted++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Failed to insert player ID {player.id}: {ex.Message}");
                        skipped++;
                    }
                }

                Console.WriteLine("[IMPORT] Import completed");
                Console.WriteLine($"[IMPORT] Inserted: {inserted}");
                Console.WriteLine($"[IMPORT] Skipped: {skipped}");
                Console.WriteLine($"[IMPORT] Total processed: {importedPlayers.Count}");

                return inserted > 0;
            }
            catch (JsonException jex)
            {
                Console.WriteLine($"[ERROR] JSON parsing error: {jex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Import failed with exception: {ex}");
                return false;
            }
        }

        public static bool ExportPlayers(string exportPath)
        {
            try
            {
                var col = PlayerDBFile.GetCollection<player_data>("players");
                var allPlayers = col.FindAll().ToList();

                var json = JsonConvert.SerializeObject(allPlayers, Formatting.Indented);
                File.WriteAllText(exportPath, json);

                Console.WriteLine($"Exported {allPlayers.Count} players to {exportPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Export failed: {ex.Message}");
                return false;
            }
        }

        public static bool CreateDormForPlayerId(ulong playerid, bool log = false)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player = col.FindOne(x => x.player.Id == playerid);

            // Was: no null check, so an unknown player id threw here instead of
            // reporting failure to the caller.
            if (player == null)
            {
                Console.WriteLine($"[CreateDormForPlayerId] no player row for id {playerid}");
                return false;
            }

            if (player.player_Extra != null)
            {
                ulong roomid = RoomDB.clone_dorm_room(playerid);
                player.player_Extra.DormRoomID = roomid;
                col.Update(player);
                if (log)
                {
                    Console.WriteLine($"Room id is {roomid} and successfully updaeted player");
                }
                return true;
            }

            return false;
        }

        public static void EnsureDormsForAllPlayers()
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            // Get all players
            var players = col.FindAll();

            foreach (var player in players)
            {
                if (player?.player == null || player.player_Extra == null)
                    continue;

                // Missing or invalid dorm
                if (player.player_Extra.DormRoomID == null || player.player_Extra.DormRoomID == 1)
                {
                    ulong roomId = RoomDB.clone_dorm_room(player.player.Id);
                    player.player_Extra.DormRoomID = roomId;
                    col.Update(player);
                    Console.WriteLine($"Created dorm {roomId} for player {player.player.Id}");
                }
            }
        }

        public static void EnsureDormsDontBeStupid(bool log = false)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var players = col.FindAll();
            int fixCount = 0;

            foreach (var player in players)
            {
                if (player?.player == null || player.player_Extra == null)
                    continue;

                var room = RoomDB.GetRoom((ulong)player.player_Extra.DormRoomID);

                // Check if the room is missing or simply isn't flagged as a Dorm
                if (room == null || !room.IsDorm)
                {
                    if (log)
                    {
                        string reason = (room == null) ? "Room Missing" : "Room not a Dorm";
                        Console.WriteLine($"[DormFix] Player: {player.player.Username} ({player.player.Id}) | Reason: {reason}");
                    }

                    CreateDormForPlayerId(player.player.Id);
                    fixCount++;
                }
            }

            if (log && fixCount > 0)
            {
                Console.WriteLine($"[DormFix] Finished. Fixed {fixCount} player dorm records.");
            }
        }


        public static ulong GetDormId(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player = col.FindOne(x => x.player.Id == playerid);

            if (playerid == 1)
            {
                return 1;
            }

            // Was: dereferenced player.player_Extra with no null check, so an unknown
            // player id threw NullReferenceException here.
            if (player == null)
            {
                Console.WriteLine($"[GetDormId] no player row for id {playerid} - no dorm");
                return 0;
            }

            if (player.player_Extra != null)
            {
                return (ulong)player.player_Extra.DormRoomID;
            }

            // Was: `return 1`. That is player 1's dorm, so any player whose row had no
            // player_Extra was silently handed SOMEONE ELSE'S dorm room. 0 means "no
            // dorm yet" and makes the caller create a real one.
            Console.WriteLine($"[GetDormId] player {playerid} has no player_Extra - no dorm assigned yet");
            return 0;
        }

        public static bool GetLogins(Platforms platform, ulong platformId, out List<AccountLogin> accounts)
        {
            accounts = new List<AccountLogin>();

            var col = PlayerDBFile.GetCollection<player_data>("players");

            // Pull all players (or consider filtering by another simple field if possible)
            var allPlayers = col.FindAll().ToList();

            // Filter in memory
            var players = allPlayers
                .Where(player => player.player.PlatformIds != null &&
                                 player.player.PlatformIds.Any(pid => pid.Platform == platform &&
                                                                      pid.PlatformId == platformId))
                .ToList();

            if (players.Count == 0)
                return false;

            foreach (var player in players)
            {
                var pid = player.player.PlatformIds.First(p => p.Platform == platform &&
                                                               p.PlatformId == platformId);

                accounts.Add(new AccountLogin
                {
                    accountId = player.playerid,
                    createdAt = player.player.created_at,
                    lastLoginTime = player.player.last_login_time,
                    platform = (Platforms)pid.Platform,
                    platformId = pid.PlatformId.ToString()
                });
            }

            accounts = accounts
                .OrderByDescending(a => a.lastLoginTime)
                .ToList();

            return true;
        }



        // A synthesized stand-in for an account id that has no DB row. Mirrors RecFlare's
        // defaultAccount(): a "Player{id}" placeholder so the client can render a card instead
        // of choking on a 404 / missing entry. The account_data_web getters supply the rest of
        // the coalesced defaults (profileImage -> DefaultProfileImage.jpg, banner/emoji -> "").
        private static account_data_web DefaultAccountWeb(ulong id)
        {
            // The login placeholder (see PlatformIdentity.LoginPlaceholderAccountId): the headset's
            // cached-login picker asks for this id to label the "type your password" entry. Answer
            // with a neutral identity, not "Player0" - that read as a real account failing to log in.
            if (id == Campus_live_rewrite.api.server.PlatformIdentity.LoginPlaceholderAccountId)
                return new account_data_web
                {
                    accountId = id,
                    createdAt = DateTime.UtcNow,
                    username = Campus_live_rewrite.api.server.PlatformIdentity.LoginPlaceholderUsername,
                    displayName = Campus_live_rewrite.api.server.PlatformIdentity.LoginPlaceholderDisplayName,
                    profileImage = "DefaultPFP.png",
                    isJunior = false,
                    identityFlags = 0,
                    personalPronouns = 0,
                    platforms = 1   // Oculus - the platform the placeholder is served on
                };
            return new account_data_web
            {
                accountId = id,
                createdAt = DateTime.UtcNow,
                username = $"Player{id}",
                displayName = $"Player{id}",
                isJunior = false,
                identityFlags = 0,
                personalPronouns = 0,
                platforms = 0
            };
        }

        // A valid, non-negative `platforms` value for an account DTO. The account's primary
        // platform int (Steam=0, Oculus=1, ...), or 0 if none is known. NEVER returns -1: the
        // old code seeded a Platforms.All (-1) accumulator and OR'd onto it, so it was always
        // -1, and -1 is not a real platform - the client's strict account parser rejects it and
        // then NO player card / profile renders (own or others'). Same class as statusVisibility=4
        // and deviceClass=-1.
        public static long PrimaryPlatformValue(player_data? player)
        {
            var firstReal = player?.player?.PlatformIds?
                .Select(p => (int)p.Platform)
                .FirstOrDefault(v => v >= 0) ?? -1;
            if (firstReal >= 0) return firstReal;
            if (player != null && (int)player.platform >= 0) return (int)player.platform;
            return 0;
        }

        public static string GetAccountsBulk(List<ulong> playerIds, ulong viewerId = 0)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            col.EnsureIndex(x => x.id);

            // ONE entry per requested id, in the requested set. A missing row is SYNTHESIZED
            // (a Player{id} default), never dropped or 404'd - otherwise a profile / friends
            // list / player list that references a deleted or never-existed id (e.g. the purged
            // orphan ids 2,3,5) renders that card BLANK. This is exactly why single-account
            // reads were 404ing (AccountById -> here -> empty array -> 404). Matches RecFlare's
            // bulk, which guarantees one entry per requested id via defaultAccount().
            var requested = playerIds?.Distinct().ToList() ?? new List<ulong>();
            if (requested.Count == 0) return "[]";

            var found = col.Find(x => requested.Contains(x.playerid))
                           .Where(p => p?.player != null)
                           .GroupBy(p => p.playerid)
                           .ToDictionary(g => g.Key, g => g.First());

            var accounts = requested
                .Select(id =>
                {
                    if (!found.TryGetValue(id, out var player))
                        return DefaultAccountWeb(id);

                    // `platforms` MUST be a valid, non-negative number. It used to start at
                    // Platforms.All (-1) and OR onto it, so it was ALWAYS -1 - and -1 is not a
                    // real platform. The RecFlare account DTO emits a plain small number (0 by
                    // default; the reference never combines PlatformIds into it). A -1 here is
                    // the same out-of-range trap as statusVisibility=4 / deviceClass=-1: the
                    // client's strict account parser chokes on it and NO player card / profile
                    // renders (own or others'). Emit the account's primary platform int (Steam=0,
                    // Oculus=1, ...), defaulting to 0, never -1.
                    long platformsValue = 0;
                    var firstReal = player.player.PlatformIds?
                        .Select(p => (int)p.Platform)
                        .FirstOrDefault(v => v >= 0) ?? -1;
                    if (firstReal >= 0) platformsValue = firstReal;
                    else if ((int)player.platform >= 0) platformsValue = (int)player.platform;

                    return new account_data_web
                    {
                        accountId = player.playerid,
                        createdAt = player.player.created_at,
                        displayName = player.player.DisplayName,
                        displayEmoji = player.player.DisplayEmoji,
                        username = player.player.Username,
                        identityFlags = player.player.identityFlags,
                        isJunior = player.player.IsJunior,
                        personalPronouns = player.player.pronounFlags,
                        platforms = platformsValue,
                        profileImage = player.player.profileImage,
                        bannerImage = string.IsNullOrEmpty(player.player.bannerImage) ? Campus_live_rewrite.api.server.ConfigCampus.DefaultBannerImage : player.player.bannerImage
                    };
                })
                .OrderBy(a => a.accountId)
                .ToList();

            // FRIEND NICKNAMES: what the VIEWER calls a player on the website (SitePrefsDB) replaces that player's display name in
            // THIS response only. It is a per-viewer view - nobody else, and not the account itself, ever changes. Read once per call.
            if (viewerId != 0)
            {
                var nicks = Campus_live_rewrite.api.dbs.SitePrefsDB.Get(viewerId).Nicknames;
                if (nicks.Count > 0)
                    foreach (var a in accounts)
                        if (a.accountId != viewerId && nicks.TryGetValue(a.accountId.ToString(), out var nick) && !string.IsNullOrWhiteSpace(nick))
                            a.displayName = nick;
            }

            return JsonConvert.SerializeObject(accounts);
        }

        public static string GetAccountsBulkAdmin(List<ulong> playerIds)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            col.EnsureIndex(x => x.id);

            var players = col.Find(x => playerIds.Contains(x.playerid)).ToList();
            if (players.Count == 0)
            {
                return "[]";
            }

            var accounts = players
                .Select(player =>
                {
                    return new
                    {
                        accountId = player.playerid,
                        createdAt = player.player.created_at,
                        displayName = player.player.DisplayName,
                        displayEmoji = player.player.DisplayEmoji,
                        username = player.player.Username,
                        identityFlags = player.player.identityFlags,
                        isJunior = player.player.IsJunior,
                        personalPronouns = player.player.pronounFlags,
                        platforms = PrimaryPlatformValue(player),
                        profileImage = player.player.profileImage,
                        bannerImage = string.IsNullOrEmpty(player.player.bannerImage) ? Campus_live_rewrite.api.server.ConfigCampus.DefaultBannerImage : player.player.bannerImage,
                        bio = player.player_Extra.Bio,
                        mbd = JsonConvert.DeserializeObject(PlayerDB.Get_moderation_block(player.playerid)),
                        PlatformIDs = player.player.PlatformIds.Select(p => new
                        {
                            p.Platform,
                            PlatformId = p.PlatformId.ToString(),
                            // Username = Steam.GetAccountInfoAsync(p.PlatformId.ToString()).Result?.PersonaName ?? ""
                        }),
                        level = player.player.Level,
                        xp = player.player.XP
                    };
                })
                .OrderBy(a => a.accountId)
                .ToList();

            return JsonConvert.SerializeObject(accounts);
        }


        public static player_data? getfullaccount_token(ulong playerid, ulong platformid, int platform)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
                return null;

            var ids = player.player.PlatformIds;
            if (ids == null || ids.Count == 0)
                return null;

            bool match = ids.Any(p => p.PlatformId == platformid);

            return match ? player : null;
        }

        /*public static player_data? GetFullAccount(ulong playerid, ulong plaformid, int plaform, string device_id)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
            {
                return null;
            }
            if (player.platformId == plaformid && player.deviceId.Contains(device_id))
                return player;
            return null;
        }
        */

        public static player_data? GetFullAccount(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);

            if (player == null)
            {
                return null;
            }

            return player;
        }

        // ---- Currency balances ------------------------------------------------
        // Stored per player in player_extra.Balances (CurrencyType -> amount).

        public static long GetBalance(ulong playerId, int currencyType)
        {
            var acc = GetFullAccount(playerId);
            if (acc?.player_Extra?.Balances == null) return 0;
            return acc.player_Extra.Balances.TryGetValue(currencyType, out var v) ? v : 0;
        }

        public static Dictionary<int, long> GetAllBalances(ulong playerId)
        {
            var acc = GetFullAccount(playerId);
            return acc?.player_Extra?.Balances ?? new Dictionary<int, long>();
        }

        /// <summary>Adds (or subtracts) an amount and returns the NEW balance. Clamped at 0.</summary>
        public static long AddBalance(ulong playerId, int currencyType, long delta)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerId);
            if (player == null) return 0;

            player.player_Extra ??= new player_extra();
            player.player_Extra.Balances ??= new Dictionary<int, long>();

            long current = player.player_Extra.Balances.TryGetValue(currencyType, out var v) ? v : 0;
            long updated = current + delta;
            if (updated < 0) updated = 0;
            player.player_Extra.Balances[currencyType] = updated;

            col.Update(player);
            return updated;
        }

        public static player_data? GetFullAccountByName(string username)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Username == username);

            if (player == null)
            {
                return null;
            }

            return player;
        }

        public static player_info? GetAccountById(ulong id)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player = col.FindById(id);
            return player?.player;
        }

        public static account_data_me? GetAccountMeSafe(ulong playerid)
        {

            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
            {
                return null;
            }
            // See PrimaryPlatformValue: a valid, non-negative platform number, NEVER -1 (which
            // is what the old Platforms.All accumulator always produced and which breaks the
            // client's account parser - the owner's OWN /account/me card wouldn't render).
            account_data_me account = new account_data_me
            {
                accountId = player.playerid,
                bannerImage = string.IsNullOrEmpty(player.player.bannerImage) ? Campus_live_rewrite.api.server.ConfigCampus.DefaultBannerImage : player.player.bannerImage,
                createdAt = player.player.created_at,
                displayName = player.player.DisplayName,
                email = player.player.Email,
                displayEmoji = player.player.DisplayEmoji,
                username = player.player.Username,
                identityFlags = player.player.identityFlags,
                isJunior = player.player.IsJunior,
                // Juniors are treated as juniors client-side (chat/social restrictions).
                TreatAsJunior = player.player.IsJunior,
                personalPronouns = player.player.pronounFlags,
                platforms = PrimaryPlatformValue(player),
                profileImage = player.player.profileImage,
            };
            // The client requires a present, valid birthday. Use the real one entered at signup
            // when we have it; otherwise keep the fixed placeholder the DTO defaults to.
            if (!string.IsNullOrWhiteSpace(player.player.Birthday))
            {
                account.birthday = player.player.Birthday;
                account.hasBirthday = true;
            }
            else if (string.Equals(SettingsDB.GetPlayerSettingValue("Recroom.AccountCreation.HasFinished", player.playerid), "false", StringComparison.OrdinalIgnoreCase))
            {
                // An in-game account still in its creation flow with no birthday on record: tell
                // the client so its title-screen account-creation flow (birthday -> avatar ->
                // username -> password) has a page it cannot skip. Website accounts entered a
                // birthday at signup and never hit this.
                account.hasBirthday = false;
            }
            /*
            account_data_me account = new account_data_me
            {
                accountId = player.playerid,
                bannerImage = string.IsNullOrEmpty(player.player.bannerImage) ? Campus_live_rewrite.api.server.ConfigCampus.DefaultBannerImage : player.player.bannerImage,
                createdAt = player.player.created_at,
                displayName = player.player.DisplayName,
                displayEmoji = player.player.DisplayEmoji,
                username = player.player.Username,
                identityFlags = player.player.identityFlags,
                isJunior = player.player.IsJunior,
                personalPronouns = player.player.pronounFlags,
                platforms = (long)combinedPlatforms,
                profileImage = player.player.profileImage,
            };
            */
            return account;

        }

        public static string Get_moderation_block(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
            {
                return JsonConvert.SerializeObject(new Moderation_Detail.ModerationBlockDetails());
            }

            var block = player.player_Extra.ModerationBlockDetails;

            // Only an ACTIVE penalty is a moderation block. A stored record with Duration <= 0 (or
            // no set-time) is NOT active - e.g. a logged report with no penalty applied
            // (IsBan=false, Duration=0). The client counts ANY non-empty moderationBlockDetails as a
            // recent moderation incident and then blocks club/room creation ("Creation Restricted -
            // too many moderation incidents"), so a non-active record MUST read back as empty.
            if (block == null || block.Duration <= 0 || block.ModerationSetUnixTime <= 0)
                return JsonConvert.SerializeObject(new Moderation_Detail.ModerationBlockDetails());

            // Permanent ban.
            if (block.Duration == int.MaxValue)
                return JsonConvert.SerializeObject(block);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int elapsed = (int)(now - block.ModerationSetUnixTime);
            int remaining = block.Duration - elapsed;

            if (remaining <= 0)
            {
                // Timeout has run out - clear it and report no block.
                player.player_Extra.ModerationBlockDetails = null;
                col.Update(player);
                return JsonConvert.SerializeObject(new Moderation_Detail.ModerationBlockDetails());
            }

            // Still serving time: report the block with the time that's left.
            block.Duration = remaining;
            return JsonConvert.SerializeObject(block);
        }



        public static string Set_moderation_block(
     ulong playerid,
     Moderation_Detail.ReportCategory category,
     int duration,
     long gameSessionId,
     bool? isHostKick,
     string? message,
     ulong? playerIdReporter,
     bool? isBan,
        ulong bannedByPlayerId,
        bool addAppealMessage = true,
        string? logKind = null,
        string? logNote = null
 )
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
                return "{}";
            string? plainMessage = message;   // what the moderator wrote, for the ban log
            if (addAppealMessage && isBan == true)
            {
                message += "\nTo appeal this ban, Make a ticket in https://discord.gg/xH8MMwK5Bk.";
            }
            var newBlock = new Moderation_Detail.ModerationBlockDetails
            {
                ReportCategory = category,
                Duration = duration,
                GameSessionId = gameSessionId,
                IsHostKick = isHostKick,
                Message = message,
                PlayerIdReporter = playerIdReporter,
                IsBan = isBan,
                ModerationSetUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BannedByPlayerId = bannedByPlayerId
            };

            player.player_Extra.ModerationBlockDetails = newBlock;
            col.Update(player);

            // Every moderation action is logged with WHO did it to WHOM and why. Without this
            // there is no way to tell a real moderator action from a bug calling this path -
            // and a 1-second isBan looks identical to "the game keeps closing itself" from the
            // player's side, because the block clears before anyone can inspect it.
            Console.WriteLine(
                $"[moderation] target={playerid} by={bannedByPlayerId} " +
                $"isBan={isBan} isHostKick={isHostKick} duration={duration}s " +
                $"category={category} reporter={playerIdReporter?.ToString() ?? "-"} " +
                $"msg=\"{(message ?? "").Replace("\n", " ")}\"");

            // Ban log (admin panel -> Ban Logs): every ban, and any other action a caller names
            // (kick, warning), with the room the player was standing in when it landed. Read
            // before the heartbeat is cleared below, or the room is always "none".
            string? kind = logKind ?? (isBan == true ? "ban" : null);
            if (kind != null)
            {
                ulong roomId = 0;
                try { roomId = (ulong)(HeartbeatDB.GetPlayerHeartbeat(playerid)?.roomInstance?.roomId ?? 0); } catch { }
                ModerationLogDB.Record(kind, playerid, bannedByPlayerId, roomId, gameSessionId > 0 ? gameSessionId : 0,
                    duration, category, plainMessage, logNote);
            }

            // A ban has to actually REMOVE them from the room.
            //
            // The client drops to its dorm and then resyncs against the presence heartbeat
            // - and the heartbeat still said "you are in room X", so it put the banned
            // player straight back into the room they were just banned from. Clearing the
            // room instance means the resync has nowhere to send them.
            if (isBan == true)
            {
                try
                {
                    HeartbeatDB.UpdatePlayerHeartbeat(playerid, null, online: false);
                    Console.WriteLine($"[ban] cleared room instance for banned player {playerid}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ban] could not clear heartbeat for {playerid}: {ex.Message}");
                }
            }

            try
            {
                Notifications.SendToPlayer(playerid, JsonConvert.SerializeObject(WebsocketEvents.CreateBan(newBlock)));
            }
            catch { }

            return JsonConvert.SerializeObject(newBlock);
        }


        // ---- MIC BAN ------------------------------------------------------------------------
        // Rec Room's mic ban has two halves, both served from here:
        //   * the GAME TOKEN's `rn.privilege` claim ["BanVChat"] - what switches the mic off
        //     (reference: apps/auth accountPrivileges). ClientSecurity.CreateToken adds it
        //     (TokenPrivileges) and ends the token with a timed ban, so it lifts on time;
        //   * a record in GET /privileges/me/restrictions (RestrictionsFor) - what the in-game
        //     Moderation Notice lists as the intervention, with its "Time Remaining" countdown.
        // A RefreshLogin push re-mints an online player's token, then ModerationUpdateRequired
        // asks the game to re-read its interventions, so a ban or a lift lands without a relog.
        //
        // PERMANENT bans loop: the restriction's EndDate is always PermanentMicBanLoop from now,
        // renewed on every read, so the countdown restarts instead of ever running out.
        //
        // Stored in player_Extra.VoiceBan, apart from ModerationBlockDetails: a mic ban never
        // blocks login or matchmaking and never overwrites a real ban/kick. (Tried first as an
        // IsVoiceModAutoban moderation block - the client showed that as a plain Timeout.)
        public static readonly TimeSpan PermanentMicBanLoop = TimeSpan.FromDays(30);

        /// <summary>The mic ban with the time still left (Duration; int.MaxValue = permanent), or null when none is running.</summary>
        public static Moderation_Detail.ModerationBlockDetails? ActiveMicBan(player_data? player)
        {
            var vb = player?.player_Extra?.VoiceBan;
            if (vb == null || vb.Duration <= 0 || vb.ModerationSetUnixTime <= 0) return null;
            if (vb.Duration == int.MaxValue) return vb;
            int remaining = vb.Duration - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - vb.ModerationSetUnixTime);
            if (remaining <= 0) return null;
            return new Moderation_Detail.ModerationBlockDetails
            {
                ReportCategory = vb.ReportCategory, Duration = remaining, Message = vb.Message,
                ModerationSetUnixTime = vb.ModerationSetUnixTime, BannedByPlayerId = vb.BannedByPlayerId
            };
        }

        public static Moderation_Detail.ModerationBlockDetails? GetActiveMicBan(ulong playerid) =>
            ActiveMicBan(PlayerDBFile.GetCollection<player_data>("players").FindOne(x => x.playerid == playerid));

        /// <summary>
        /// The token's rn.privilege values for this player, and when the token should end so the
        /// ban lifts on time (null for no ban / a permanent one - the normal lifetime applies).
        /// </summary>
        public static (List<string> privileges, DateTimeOffset? firstEnds) TokenPrivileges(ulong playerid)
        {
            var b = GetActiveMicBan(playerid);
            if (b == null) return (new List<string>(), null);
            DateTimeOffset? ends = b.Duration == int.MaxValue ? null : DateTimeOffset.UtcNow.AddSeconds(b.Duration);
            return (new List<string> { "BanVChat" }, ends);
        }

        /// <summary>
        /// The player's interventions for GET /privileges/me/restrictions - what the in-game
        /// Moderation Notice lists. Name is the privilege code the token carries (the client's code
        /// list: BanVChat, BanRmChat, BanCrEvt, BanCrCai, BanCmtImg, BanP2PBuy, BanP2PSell,
        /// BanP2PChat, BanPubRm, BanPubInv, BanPubCam, BanRumiAI, BanMkrAI, BanGameAI); EndDate
        /// drives the countdown. Empty list = "no active moderation interventions".
        /// </summary>
        public static List<object> RestrictionsFor(ulong playerid)
        {
            var list = new List<object>();
            var b = GetActiveMicBan(playerid);
            if (b == null) return list;
            list.Add(new
            {
                AccountId = (long)playerid,
                Name = "BanVChat",
                Description = "Voice chat",
                EndDate = b.Duration == int.MaxValue ? DateTime.UtcNow.Add(PermanentMicBanLoop) : DateTime.UtcNow.AddSeconds(b.Duration),
                AssociatedAccountId = (long?)null,
                AssociatedAccountUsername = (string?)null,
                ReportCategory = (int)b.ReportCategory,
                DisplayReason = b.Message
            });
            return list;
        }

        /// <summary>Mic-ban a player for <paramref name="seconds"/> (int.MaxValue = permanent).</summary>
        public static Moderation_Detail.ModerationBlockDetails? SetMicBan(ulong playerid, int seconds, string? message,
            Moderation_Detail.ReportCategory category, ulong byPlayerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null) return null;
            var hb = HeartbeatDB.GetPlayerHeartbeat(playerid);
            var b = new Moderation_Detail.ModerationBlockDetails
            {
                ReportCategory = category,
                Duration = seconds,
                GameSessionId = hb?.roomInstance?.roomInstanceId ?? 0,
                Message = string.IsNullOrWhiteSpace(message) ? null : message,
                IsBan = false,
                ModerationSetUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BannedByPlayerId = byPlayerId
            };
            player.player_Extra.VoiceBan = b;
            col.Update(player);
            ModerationLogDB.Record("mic-ban", playerid, byPlayerId, (ulong)(hb?.roomInstance?.roomId ?? 0), b.GameSessionId,
                seconds, category, message, null);
            Console.WriteLine($"[moderation] mic-ban target={playerid} by={byPlayerId} duration={(seconds == int.MaxValue ? "permanent" : seconds + "s")} msg=\"{message}\"");
            PushMicBanRefresh(playerid, "mic-ban set");
            return b;
        }

        /// <summary>Lift a mic ban. True when one was running.</summary>
        public static bool ClearMicBan(ulong playerid, ulong byPlayerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null) return false;
            bool had = ActiveMicBan(player) != null;
            if (player.player_Extra.VoiceBan == null) return false;
            player.player_Extra.VoiceBan = null;
            col.Update(player);
            ModerationLogDB.Record("mic-unban", playerid, byPlayerId, 0, 0, 0, Moderation_Detail.ReportCategory.Moderator, "",
                had ? "lifted early" : "expired ban cleared");
            Console.WriteLine($"[moderation] mic-unban target={playerid} by={byPlayerId}");
            PushMicBanRefresh(playerid, "mic-ban lifted");
            return had;
        }

        // RefreshLogin (5) so the game re-mints its token (which now carries or drops BanVChat), then
        // ModerationUpdateRequired (21) once the new token is in, so it re-reads moderationBlockDetails
        // and /privileges/me/restrictions and the Moderation Notice updates without a relog.
        private static void PushMicBanRefresh(ulong playerid, string why)
        {
            try
            {
                Notifications.FireAndForget(Notifications.SendToPlayer(playerid, JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                {
                    Id = ((int)WebsocketEvents.ResponseResults.RefreshLogin).ToString(),
                    Msg = new { Reason = why }
                })), $"mic-ban refresh-login {playerid}");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    try
                    {
                        await Notifications.SendToPlayer(playerid, JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                        {
                            Id = ((int)WebsocketEvents.ResponseResults.ModerationUpdateRequired).ToString(),
                            Msg = new { Reason = why }
                        }));
                    }
                    catch { }
                });
            }
            catch (Exception ex) { Console.WriteLine($"[moderation] mic-ban push to {playerid} failed: {ex.Message}"); }
        }

        /// <summary>
        /// The account's ban as the WEBSITE sees it: a running ban or timeout (not a kick or a
        /// warning, which are momentary). Null when the account is not banned. RemainingSeconds is
        /// null for a permanent ban. Reason is the moderator's text without the appeal line.
        /// </summary>
        public sealed record SiteBan(int? RemainingSeconds, DateTime? Until, string Reason);

        public static SiteBan? GetSiteBan(ulong playerid)
        {
            var player = PlayerDBFile.GetCollection<player_data>("players").FindOne(x => x.playerid == playerid);
            var block = player?.player_Extra?.ModerationBlockDetails;
            if (block == null || block.Duration <= 0 || block.ModerationSetUnixTime <= 0) return null;
            if (block.IsWarning || block.IsHostKick == true) return null;   // kicks / warnings are not bans
            // The moderator's text, without the "To appeal this ban..." line the ban path appends.
            string reason = (block.Message ?? "").Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("To appeal", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (block.Duration == int.MaxValue) return new SiteBan(null, null, reason);
            // A ban (IsBan) or a timeout of at least a minute; a 1-second kick record is not one.
            if (block.IsBan != true && block.Duration < 60) return null;
            int remaining = block.Duration - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - block.ModerationSetUnixTime);
            if (remaining <= 0) return null;
            return new SiteBan(remaining, DateTime.UtcNow.AddSeconds(remaining), reason);
        }

        public static bool HasActiveModerationBlock(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
                return false;

            var block = player.player_Extra.ModerationBlockDetails;
            if (block == null || block.Duration <= 0 || block.ModerationSetUnixTime <= 0)
                return false;

            if (block.Duration == int.MaxValue) // Permanent ban
                return true;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int elapsed = (int)(now - block.ModerationSetUnixTime);
            int remaining = block.Duration - elapsed;

            if (remaining <= 0)
            {

                return false;
            }

            return true;
        }

        public static bool CheckDevFlag(ulong id)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.id == id);

            if (player != null && (player.dev_Flag & DevFlag.dev) == DevFlag.dev)
            {
                return true;
            }

            return false;
        }

        public static bool CheckModFlag(ulong id)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.id == id);

            if (player != null && (player.dev_Flag & DevFlag.mod) == DevFlag.mod)
            {
                return true;
            }

            return false;
        }

        /// <summary>Dev OR mod - the admin panel's "can log in / reach the narrow mod slice" gate. Everything
        /// that should stay dev-only keeps calling CheckDevFlag alone (unchanged); this is only for the login
        /// check and the handful of endpoints AuthHelper.ValidateTokenDevOrMod protects.</summary>
        public static bool CheckDevOrModFlag(ulong id)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.id == id);
            return player != null && (player.dev_Flag & (DevFlag.dev | DevFlag.mod)) != DevFlag.none;
        }

        public static void set_player_avatar(string avatar, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            // Was: guarded `player_Setting1.player != null` but then wrote to
            // player_Setting1.player_Extra - the wrong field. A row with a null
            // player_Extra threw NullReferenceException while saving the avatar.
            if (player_Setting1?.player_Extra != null)
            {
                player_Setting1.player_Extra.avatar = avatar;
                col.Update(player_Setting1);
                return;
            }

            Console.WriteLine($"[set_player_avatar] no player_Extra for {playerid} - avatar not saved");
        }

        public static (bool Cheered, bool Favorited) GetInteraction(ulong playerid, ulong roomid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
                return (false, false);

            bool cheered = player.player_Extra.CheeredRooms?.Contains(roomid) ?? false;
            bool favorited = player.player_Extra.FavoritedRooms?.Contains(roomid) ?? false;

            return (cheered, favorited);
        }

        public static bool ToggleCheer(ulong playerid, ulong roomid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null) return false;

            if (player.player_Extra.CheeredRooms == null)
                player.player_Extra.CheeredRooms = new List<ulong>();

            bool nowCheered;
            if (player.player_Extra.CheeredRooms.Contains(roomid))
            {
                player.player_Extra.CheeredRooms.Remove(roomid);
                nowCheered = false;
                RoomDB.UpdateRoomCheerCount(roomid, -1);
            }
            else
            {
                player.player_Extra.CheeredRooms.Add(roomid);
                nowCheered = true;
                RoomDB.UpdateRoomCheerCount(roomid, +1);
            }

            col.Update(player);
            return nowCheered;
        }

        public static bool ToggleFavorite(ulong playerid, ulong roomid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null) return false;

            if (player.player_Extra.FavoritedRooms == null)
                player.player_Extra.FavoritedRooms = new List<ulong>();

            bool nowFavorited;
            if (player.player_Extra.FavoritedRooms.Contains(roomid))
            {
                player.player_Extra.FavoritedRooms.Remove(roomid);
                nowFavorited = false;
                RoomDB.UpdateRoomFavoriteCount(roomid, -1);
            }
            else
            {
                player.player_Extra.FavoritedRooms.Add(roomid);
                nowFavorited = true;
                RoomDB.UpdateRoomFavoriteCount(roomid, +1);
            }

            col.Update(player);
            return nowFavorited;
        }

        public static List<account_data> Search(string text)
        {
            // GET /Accounts/account/search with no `name` query parameter passes null
            // here, and the first thing the loop does is text.StartsWith("@") - an
            // unhandled NullReferenceException that Kestrel turns into a 500.
            // Verified live: probing the route with no argument returned HTTP 500.
            // An empty search matches nothing, so return an empty list.
            if (string.IsNullOrWhiteSpace(text))
                return new List<account_data>();

            // Match on USERNAME first (the reference server prefix-matches username, and the
            // People search is by @handle), and also on DISPLAY NAME so searching the shown name
            // works too. Case-insensitive Contains, which is a superset of the reference's prefix
            // match. A leading "@" is stripped so "@eli" and "eli" behave the same.
            //
            // The previous version only checked username when the query STARTED WITH "@", and
            // otherwise matched display name ONLY - so searching a plain "eli" (which is a
            // USERNAME) never checked usernames and returned []. It also left `found=true` when a
            // row had a null DisplayName, which quietly matched every such account.
            string q = text.Trim().TrimStart('@').ToLowerInvariant();
            if (q.Length == 0)
                return new List<account_data>();

            var col = PlayerDBFile.GetCollection<player_data>("players");

            List<account_data> list = new List<account_data>();
            foreach (player_data room in col.FindAll())
            {
                var p = room?.player;
                if (p == null)
                    continue;

                bool matchUsername = !string.IsNullOrEmpty(p.Username)
                                     && p.Username.ToLowerInvariant().Contains(q);
                bool matchDisplay = !string.IsNullOrEmpty(p.DisplayName)
                                    && p.DisplayName.ToLowerInvariant().Contains(q);

                if (!matchUsername && !matchDisplay)
                    continue;

                var room_data = GetAccountBulk(room.playerid);
                if (room_data is null)
                    continue;
                list.Add(room_data);
            }
            return list;
        }

        public static account_data GetAccountBulk(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
            {
                return null;
            }

            return new account_data
            {
                accountId = player.playerid,
                createdAt = player.player.created_at,
                displayName = player.player.DisplayName,
                username = player.player.Username,
                identityFlags = 0,
                isJunior = player.player.IsJunior,
                personalPronouns = 0,
                platforms = PrimaryPlatformValue(player),
                profileImage = player.player.profileImage,
            };
        }

        public static bool SetPlayerUsername(string username, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            // Case-INSENSITIVE: "kyle" next to "Kyle" is the whole point of a spoofed @name.
            var existingplayer = col.FindAll().FirstOrDefault(x => x.player?.Username != null
                && string.Equals(x.player.Username, username, StringComparison.OrdinalIgnoreCase) && x.playerid != playerid);
            if (existingplayer != null)
            {
                return false;
            }
            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player != null)
                {
                    player_Setting1.player.Username = username;
                    col.Update(player_Setting1);
                    RoomDB.RenameDormForPlayer(playerid);
            Campus_live_rewrite.api.server.Notifications.FireAndForget(Campus_live_rewrite.api.server.Notifications.PushAccountUpdate(playerid), $"account-update {playerid}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Every platform id linked to this account for a platform, in the order stored.
        /// An account may hold several - a player with two Steam accounts signs in on
        /// either and lands here.
        /// </summary>
        public static List<ulong> GetPlatformIds(ulong playerid, Platforms platform)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var row = col.FindOne(x => x.playerid == playerid);

            return row?.player?.PlatformIds?
                      .Where(p => p.Platform == platform)
                      .Select(p => p.PlatformId)
                      .ToList()
                   ?? new List<ulong>();
        }

        /// <summary>First platform id for this platform, or 0. Convenience for display.</summary>
        public static ulong GetPlatformId(ulong playerid, Platforms platform) =>
            GetPlatformIds(playerid, platform).FirstOrDefault();

        /// <summary>
        /// Every account carrying this platform id, ignoring <paramref name="ignorePlayerId"/>.
        ///
        /// Informational only - the admin panel reports shared ids rather than refusing
        /// them. Sharing IS allowed by design, but it is worth knowing about: GetLogins
        /// resolves a sign-in by (platform, platformId), so when several accounts hold the
        /// same id, that lookup returns them all and the caller picks.
        /// </summary>
        public static List<ulong> FindPlayersByPlatformId(Platforms platform, ulong platformId, ulong ignorePlayerId = 0)
        {
            if (platformId == 0) return new List<ulong>();

            var col = PlayerDBFile.GetCollection<player_data>("players");
            return col.FindAll()
                      .Where(x => x.playerid != ignorePlayerId &&
                                  x.player?.PlatformIds != null &&
                                  x.player.PlatformIds.Any(p => p.Platform == platform && p.PlatformId == platformId))
                      .Select(x => x.playerid)
                      .ToList();
        }

        /// <summary>
        /// Replaces every link for this platform with <paramref name="platformIds"/>.
        /// An empty list unlinks the platform entirely. Duplicates within the list are
        /// collapsed; the same id living on OTHER accounts is left alone - that is
        /// deliberate, several accounts may share one Steam id.
        /// </summary>
        public static bool SetPlatformIds(ulong playerid, Platforms platform, IEnumerable<ulong> platformIds)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var row = col.FindOne(x => x.playerid == playerid);
            if (row?.player == null) return false;

            row.player.PlatformIds ??= new List<mPlatformID>();
            row.player.PlatformIds.RemoveAll(p => p.Platform == platform);

            foreach (var id in platformIds.Where(i => i != 0).Distinct())
                row.player.PlatformIds.Add(new mPlatformID { Platform = platform, PlatformId = id });

            col.Update(row);
            return true;
        }

        /// <summary>Single-id form of <see cref="SetPlatformIds"/>; 0 unlinks.</summary>
        public static bool SetPlatformId(ulong playerid, Platforms platform, ulong platformId) =>
            SetPlatformIds(playerid, platform, platformId == 0 ? Array.Empty<ulong>() : new[] { platformId });

        /// <summary>Sets (or with 0, clears) the linked Discord account.</summary>
        public static bool SetDiscordId(ulong playerid, ulong discordId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var row = col.FindOne(x => x.playerid == playerid);
            if (row?.player == null) return false;

            row.player_Extra ??= new player_extra();
            row.player_Extra.DiscordId = discordId;

            col.Update(row);
            return true;
        }

        /// <summary>
        /// Finds whoever already has this Discord id linked, ignoring
        /// <paramref name="ignorePlayerId"/>. Returns 0 when nobody does.
        /// </summary>
        public static ulong FindPlayerByDiscordId(ulong discordId, ulong ignorePlayerId = 0)
        {
            if (discordId == 0) return 0;

            var col = PlayerDBFile.GetCollection<player_data>("players");
            var owner = col.FindAll().FirstOrDefault(x =>
                x.playerid != ignorePlayerId &&
                x.player_Extra != null &&
                x.player_Extra.DiscordId == discordId);

            return owner?.playerid ?? 0;
        }

        /// <summary>
        /// Link a Discord user to a Campus account (website Settings > Discord tab + the /link bot
        /// command). Sets BOTH the id and the display tag. This is ONLY an association for the bot
        /// and the site - it grants NO dev/admin/moderation privilege of any kind. Pass discordId 0
        /// to unlink.
        /// </summary>
        public static bool SetDiscordLink(ulong playerid, ulong discordId, string? discordUsername)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record?.player == null) return false;
            record.player_Extra ??= new player_extra();
            record.player_Extra.DiscordId = discordId;
            record.player_Extra.DiscordUsername = discordId == 0 ? null : discordUsername;
            col.Update(record);
            Console.WriteLine($"[discord-link] account {playerid} {(discordId == 0 ? "UNLINKED" : $"linked to discord {discordId} ({discordUsername})")}");
            return true;
        }

        // A random, non-identifying @name for an under-13 (junior) account, e.g. "FunkyHippo1293"
        // - Adjective + Noun (from the same word lists the in-game name generator uses) + a few
        // digits. Loops until it finds one not already taken; the digits make a collision after a
        // few tries astronomically unlikely, but the uniqueness check keeps it correct regardless.
        private static readonly Random _juniorRng = new();
        public static string GenerateJuniorUsername()
        {
            var adjectives = Campus_live_rewrite.api.server.Utils.Adjectives;
            var nouns = Campus_live_rewrite.api.server.Utils.Nouns;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                string adj, noun;
                lock (_juniorRng)
                {
                    adj  = adjectives[_juniorRng.Next(adjectives.Count)];
                    noun = nouns[_juniorRng.Next(nouns.Count)];
                }
                // Keep it to the same charset the site allows (letters/digits/underscore).
                string baseName = System.Text.RegularExpressions.Regex.Replace(adj + noun, "[^A-Za-z0-9]", "");
                if (baseName.Length == 0) baseName = "Camper";
                if (baseName.Length > 14) baseName = baseName.Substring(0, 14);
                int digits;
                lock (_juniorRng) digits = _juniorRng.Next(1000, 10000);
                string candidate = baseName + digits;
                if (!IsUsernameTaken(candidate)) return candidate;
            }
            // Extremely unlikely fallback: timestamp-based, guaranteed unique enough.
            return "Camper" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString().Substring(6);
        }

        /// <summary>
        /// Mark a player junior (under-13 restrictions) or lift it. Pushed live: the account
        /// update carries IsJunior / TreatAsJunior / JuniorState, which is what the client gates
        /// junior chat, voice and profile restrictions on, and a RefreshLogin re-mints the token
        /// so anything read at login follows too. Returns false for an unknown player.
        /// </summary>
        public static bool SetJunior(ulong playerid, bool junior, ulong byPlayerId, string why)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record?.player == null) return false;
            bool was = record.player.IsJunior;
            record.player.IsJunior = junior;
            col.Update(record);
            Console.WriteLine($"[junior] account {playerid} (@{record.player.Username}) junior {was} -> {junior} by {byPlayerId} ({why})");
            if (was == junior) return true;
            // Becoming junior replaces an identifying name, the same way an under-13 signup gets
            // one: a random name (e.g. FunkyHippo1293) as BOTH the @name and the display name.
            // Lifting junior leaves the name alone.
            if (junior)
            {
                string old = record.player.Username ?? "";
                for (int i = 0; i < 5; i++)
                {
                    string fresh = GenerateJuniorUsername();
                    if (!SetPlayerUsername(fresh, playerid)) continue;   // taken in the meantime: try another
                    SetDisplayName(fresh, playerid);
                    Console.WriteLine($"[junior] account {playerid} renamed @{old} -> @{fresh} (junior)");
                    break;
                }
            }
            try
            {
                Notifications.FireAndForget(Notifications.PushAccountUpdate(playerid), $"junior account-update {playerid}");
                Notifications.FireAndForget(Notifications.SendToPlayer(playerid, JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                {
                    Id = ((int)WebsocketEvents.ResponseResults.RefreshLogin).ToString(),
                    Msg = new { Reason = "junior status changed" }
                })), $"junior refresh-login {playerid}");
            }
            catch (Exception ex) { Console.WriteLine($"[junior] push to {playerid} failed: {ex.Message}"); }
            return true;
        }

        /// <summary>Whether a moderator has locked this player's @name / display name (admin panel).</summary>
        public static bool IsNameLocked(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            return record?.player_Extra?.NameLocked == true;
        }

        public enum ProfileLock { Bio, Picture, Banner }

        /// <summary>Whether a moderator has locked this part of the player's profile (admin panel).</summary>
        public static bool IsProfileLocked(ulong playerid, ProfileLock what)
        {
            var x = PlayerDBFile.GetCollection<player_data>("players").FindOne(p => p.playerid == playerid)?.player_Extra;
            if (x == null) return false;
            return what switch { ProfileLock.Bio => x.BioLocked, ProfileLock.Picture => x.PictureLocked, _ => x.BannerLocked };
        }

        /// <summary>What the player is told when they try to change a locked part.</summary>
        public static string ProfileLockMessage(ProfileLock what) => what switch
        {
            ProfileLock.Bio => "Your bio has been locked by a moderator and cannot be changed.",
            ProfileLock.Picture => "Your profile picture has been locked by a moderator and cannot be changed.",
            _ => "Your banner has been locked by a moderator and cannot be changed."
        };

        /// <summary>Lock or unlock a part of a player's profile. Admin edits are never gated by this.</summary>
        public static bool SetProfileLock(ulong playerid, ProfileLock what, bool locked)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record?.player == null) return false;
            record.player_Extra ??= new player_extra();
            switch (what)
            {
                case ProfileLock.Bio: record.player_Extra.BioLocked = locked; break;
                case ProfileLock.Picture: record.player_Extra.PictureLocked = locked; break;
                default: record.player_Extra.BannerLocked = locked; break;
            }
            col.Update(record);
            Console.WriteLine($"[profile-lock] account {playerid} (@{record.player.Username}) {what} lock {(locked ? "ON" : "off")}");
            return true;
        }

        /// <summary>Lock or unlock a player's names. Admin edits are never gated by this.</summary>
        public static bool SetNameLock(ulong playerid, bool locked)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record?.player == null) return false;
            record.player_Extra ??= new player_extra();
            record.player_Extra.NameLocked = locked;
            col.Update(record);
            Console.WriteLine($"[names] account {playerid} (@{record.player.Username}) name lock {(locked ? "ON" : "off")}");
            return true;
        }

        public static bool SetPlayerDev(DevFlag dev_Flag, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var playerRecord = col.FindOne(x => x.playerid == playerid);
            if (playerRecord != null)
            {
                if (playerRecord.player != null)
                {
                    playerRecord.dev_Flag = dev_Flag;
                    col.Update(playerRecord);
                    return true;
                }
            }
            return false;
        }

        public static bool SetAllDev()
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            foreach (var playerRecord in col.FindAll())
            {
                if (playerRecord.player != null)
                {
                    playerRecord.dev_Flag = DevFlag.dev;
                    col.Update(playerRecord);
                    return true;
                }
            }
            return false;
        }

        public static account_login<account_data> GetAccountPassword(string username, string password, bool isAdminPanel)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return new account_login<account_data>
                {
                    success = false,
                    error = "Your username or password is empty, please enter a username and password.",
                };
            }

            var player = col.FindOne(x => x.player.Username == username);
            if (player == null)
            {
                return new account_login<account_data>
                {
                    success = false,
                    error = "The account you are trying to login to does not exist.",
                };
            }

            if (string.IsNullOrEmpty(player.password))
            {
                return new account_login<account_data>
                {
                    success = false,
                    error = "The password or username you entered don't match the account you are trying to login to."
                };
            }

            if (string.IsNullOrEmpty(password))
            {
                return new account_login<account_data>
                {
                    success = false,
                    error = "The password or username you entered don't match the account you are trying to login to.",
                };
            }

            if (isAdminPanel)
            {
                // Mods can log in too now, just into a deliberately narrow slice of the panel -
                // CheckDevOrModFlag here, NOT CheckDevFlag alone. Which endpoints a mod can actually
                // reach once logged in is enforced per-endpoint by AuthHelper.ValidateTokenDevOrMod.
                var devCheckResult = PlayerDB.CheckDevOrModFlag(player.playerid);
                if (!devCheckResult)
                {
                    return new account_login<account_data>
                    {
                        success = false,
                        error = "You don't have admin panel access.",
                    };
                }
            }

            bool isValid = ClientSecurity.VerifyPassword(password, player.password);

            if (!isValid)
            {
                string oldHash = ClientSecurity.GetHashString(password);
                if (string.Equals(player.password, oldHash, StringComparison.OrdinalIgnoreCase))
                {
                    player.password = ClientSecurity.HashPassword(password);
                    col.Update(player);
                }
                else
                {
                    return new account_login<account_data>
                    {
                        success = false,
                        error = "The password or username you entered don't match the account you are trying to login to."
                    };
                }
            }

            return new account_login<account_data>
            {
                success = true,
                error = "",
                accid = player.playerid,
            };
        }

        public static void SetPlayerPassword(string pass, ulong playerid, bool log)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var playerRecord = col.FindOne(x => x.playerid == playerid);
            if (playerRecord != null && playerRecord.player != null)
            {
                playerRecord.password = ClientSecurity.HashPassword(pass); 
                col.Update(playerRecord);
                if (log)
                {
                    Console.WriteLine($"[Password Change] PlayerID: {playerid} has changed their password.");
                }
            }
        }

        public static bool SetPlayerBio(string bio, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player != null)
                {
                    player_Setting1.player_Extra.Bio = bio;
                    col.Update(player_Setting1);
            Campus_live_rewrite.api.server.Notifications.FireAndForget(Campus_live_rewrite.api.server.Notifications.PushAccountUpdate(playerid), $"account-update {playerid}");
                    return true;
                }
            }
            return false;
        }

        public static async Task<bool> SetPlayerLevel(int level, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player != null)
                {
                    player_Setting1.player.Level = level;
                    try
                    {
                        await Notifications.SendToAll(JsonConvert.SerializeObject(WebsocketEvents.createResponse_levelupdate(playerid, level, Progress.CumulativeXpForLevel(level, 0))));
                    }
                    catch
                    {

                    }
                    col.Update(player_Setting1);
                    return true;
                }
            }
            return false;
        }

        public static player_data? GetPlayerByDiscordId(ulong discordId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player_Extra.DiscordId == discordId);
            return player;
        }

        public static void SetDisplayName(string username, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player != null)
                {
                    player_Setting1.player.DisplayName = username;
                    col.Update(player_Setting1);
            Campus_live_rewrite.api.server.Notifications.FireAndForget(Campus_live_rewrite.api.server.Notifications.PushAccountUpdate(playerid), $"account-update {playerid}");
                    return;
                }
            }
        }

        public static bool SetRoleFlag(ulong playerid, DevFlag flag, bool enable)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;

            if (enable) record.dev_Flag |= flag;
            else        record.dev_Flag &= ~flag;

            col.Update(record);
            return true;
        }

        /// <summary>
        /// Sets XP and/or Level directly (admin panel). Pass null to leave one unchanged.
        /// Returns the values actually stored, or null if the player does not exist.
        /// </summary>
        public static (int XP, int Level)? SetProgression(ulong playerid, int? xp, int? level)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record?.player == null) return null;

            if (xp.HasValue)    record.player.XP    = xp.Value    < 0 ? 0 : xp.Value;
            if (level.HasValue) record.player.Level = level.Value < 0 ? 0 : level.Value;

            col.Update(record);
            return (record.player.XP, record.player.Level);
        }

        /// <summary>
        /// Grants XP to a player and applies any level-ups it now pays for, using the real
        /// progression curve (Progress.LEVEL_REQUIRED_XP). XP is progress INTO the current level;
        /// a grant that fills the level carries the remainder forward. Returns the resulting
        /// (Level, XP) and how many levels were gained, or null if the player has no row.
        /// This is what a game reward calls - it was the missing piece: nothing granted XP, so
        /// players never levelled up. Mirrors RecFlare's addXp.
        /// </summary>
        public static (int Level, int XP, int LevelsGained)? AddXp(ulong playerid, int amount)
        {
            if (amount <= 0)
            {
                var cur = Progress.GetPlayerLevel(playerid);
                return (cur.Level, cur.XP, 0);
            }

            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record?.player == null) return null;

            int startLevel = record.player.Level < 1 ? 1 : record.player.Level;
            int newXp = (record.player.XP < 0 ? 0 : record.player.XP) + amount;

            var (level, xp) = Progress.ApplyLevelUps(startLevel, newXp);
            int levelsGained = level - startLevel;

            record.player.Level = level;
            record.player.XP = xp;
            col.Update(record);

            // Push the new level/XP so the crown + progress bar update in real time (no relog) -
            // AddXp used to be silent, so a level earned in-game only showed after a relog. A level
            // CHANGE broadcasts (everyone's view of this player's crown updates); an XP-only gain
            // pings just the owner so their bar advances. Same PlayerProgressionLevelUpdate event
            // the admin level set + the progression GET use.
            try
            {
                // Report CUMULATIVE XP (client derives the crown level from XP - see
                // Progress.CumulativeXpForLevel), not the raw into-level XP.
                int reportXp = Progress.CumulativeXpForLevel(level, xp);
                string msg = JsonConvert.SerializeObject(WebsocketEvents.createResponse_levelupdate(playerid, level, reportXp));
                if (levelsGained > 0) Notifications.FireAndForget(Notifications.SendToAll(msg), "levelup broadcast");
                else Notifications.FireAndForget(Notifications.SendToPlayer(playerid, msg), "xp update");
            }
            catch { }

            // Every level crossed pays its box (reference: grantLevelUpGifts). After the level is
            // stored, so a failed roll costs a prize and not the level.
            if (levelsGained > 0)
            {
                try { Campus_live_rewrite.api.server.LevelRewards.GrantLevelUps(playerid, startLevel, level); }
                catch (Exception ex) { Console.WriteLine($"[levelrewards] {playerid}: {ex.Message}"); }
            }

            return (level, xp, levelsGained);
        }

        /// <summary>
        /// Claims a once-per-day reward. Returns false if it was already claimed today,
        /// in which case nothing is written.
        /// </summary>
        public static bool TryClaimDailyReward(ulong playerid, string rewardKey)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;

            record.player_Extra ??= new player_extra();
            record.player_Extra.DailyRewardClaims ??= new Dictionary<string, string>();

            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (record.player_Extra.DailyRewardClaims.TryGetValue(rewardKey, out var last) && last == today)
                return false;

            record.player_Extra.DailyRewardClaims[rewardKey] = today;
            col.Update(record);
            return true;
        }

        // ---- Gift boxes -------------------------------------------------------

        // ---- Avatar item ownership ------------------------------------------------
        //
        // The 2023 server treats AvatarItems.json as a CATALOG - the list of every item
        // that exists - and keeps what each player owns on the player record, seeded from
        // DefaultAvatarItems.json. The 2025 server had no ownership list at all and simply
        // served the whole catalog from /api/avatar/v4/items, so everybody owned
        // everything. These helpers restore the 2023 model.

        /// <summary>
        /// Canonical key for comparing two descriptors. Trailing empty colour slots are
        /// dropped, so "guid,,," and "guid," compare equal, while "guid,colour,,"
        /// stays distinct from "guid,,," - every colour of an item shares a base guid, so
        /// keying on the guid alone would merge Knight Cape (Red) into Knight Cape (Green).
        /// </summary>
        public static string NormalizeAvatarItemDesc(string? desc)
        {
            if (string.IsNullOrWhiteSpace(desc)) return "";

            var parts = desc.Trim().Split(',').Select(p => p.Trim()).ToList();
            while (parts.Count > 1 && string.IsNullOrEmpty(parts[^1])) parts.RemoveAt(parts.Count - 1);

            // Compare on the EXPANDED form of every id.
            //
            // Rec Room writes ids two ways - a full UUID, or a 22-char base64 "short guid" -
            // and avatar_items.json used to mix both. The catalog has since been rewritten to
            // full UUIDs throughout (see Data/APIS/items/README-avatar-items.md), but stored
            // ownership rows, DefaultAvatarItems.json and the storefronts still carry whichever
            // spelling they were written with. Expanding here means "guid,8XajS2adOEOqzqnLy6-zcQ"
            // and "guid,4ba376f1-9d66-4338-aace-a9cbcbafb371" are the same item, so nobody
            // loses an item they own just because the catalog changed spelling.
            for (int i = 0; i < parts.Count; i++)
            {
                var expanded = Campus_live_rewrite.api.server.Classes.ItemGuids.NormalizeGuid(parts[i]);
                if (expanded != null) parts[i] = expanded;
            }

            return string.Join(",", parts);
        }

        /// <summary>
        /// The starter wardrobe every non-developer account begins with. Loaded once.
        /// </summary>
        private static List<string>? _defaultAvatarItems;
        private static readonly object _defaultAvatarItemsLock = new();

        public static List<string> DefaultAvatarItemDescs()
        {
            if (_defaultAvatarItems != null) return _defaultAvatarItems;

            lock (_defaultAvatarItemsLock)
            {
                if (_defaultAvatarItems != null) return _defaultAvatarItems;

                var list = new List<string>();
                try
                {
                    string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "items", "DefaultAvatarItems.json");
                    if (File.Exists(path))
                    {
                        // The file is a flat array of descriptor strings.
                        var arr = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path));
                        foreach (var d in arr ?? new List<string>())
                            if (!string.IsNullOrWhiteSpace(d)) list.Add(d.Trim());
                    }
                    else
                    {
                        Console.WriteLine($"[items] DefaultAvatarItems.json not found at {path} - new accounts will start empty");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[items] could not read DefaultAvatarItems.json: {ex.Message}");
                }

                _defaultAvatarItems = list;
                Console.WriteLine($"[items] loaded {list.Count} default starter avatar item(s)");
                return _defaultAvatarItems;
            }
        }

        /// <summary>
        /// Everything this account owns, as canonical keys. Grants the starter set on
        /// first read so existing accounts need no migration.
        /// </summary>
        public static HashSet<string> OwnedAvatarItemDescs(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            record.player_Extra ??= new player_extra();

            if (record.player_Extra.AvatarItems == null)
            {
                record.player_Extra.AvatarItems = new List<string>(DefaultAvatarItemDescs());
                col.Update(record);
                Console.WriteLine($"[items] granted {record.player_Extra.AvatarItems.Count} starter item(s) to {playerid}");
            }

            var set = new HashSet<string>(
                record.player_Extra.AvatarItems.Select(NormalizeAvatarItemDesc).Where(d => d.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            // Unioned on READ rather than written into the wardrobe row, so every existing account
            // gets it with no migration and it cannot drift out of sync with the catalog.
            set.UnionWith(Campus_live_rewrite.api.server.HairGrants.FreeHairKeys());
            return set;
        }

        /// <summary>Adds an item to a player's wardrobe. Returns true if it was new.</summary>
        /// <summary>The normalised keys of every avatar item the player owns (defaults included).</summary>
        public static HashSet<string> GetOwnedAvatarItemKeys(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            var items = record?.player_Extra?.AvatarItems ?? DefaultAvatarItemDescs();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in items) { var k = NormalizeAvatarItemDesc(d); if (k.Length > 0) set.Add(k); }
            set.UnionWith(Campus_live_rewrite.api.server.HairGrants.FreeHairKeys());
            return set;
        }

        // Consumable descriptors come in short-guid and UUID spellings; own them by the UUID form.
        public static string NormalizeConsumableDesc(string? desc)
        {
            string s = (desc ?? "").Trim();
            if (s.Length == 0) return "";
            return (Campus_live_rewrite.api.server.Classes.ItemGuids.NormalizeGuid(s) ?? s).ToLowerInvariant();
        }

        /// <summary>The player's consumable stock (desc key -> count), empty when they own none.</summary>
        public static Dictionary<string, int> OwnedConsumables(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            return new Dictionary<string, int>(record?.player_Extra?.Consumables ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
        }

        public static void GrantConsumable(ulong playerid, string? desc, int count)
        {
            string ck = NormalizeConsumableDesc(desc);
            if (ck.Length == 0 || count <= 0) return;
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return;
            record.player_Extra ??= new player_extra();
            record.player_Extra.Consumables ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            record.player_Extra.Consumables[ck] = (record.player_Extra.Consumables.TryGetValue(ck, out var have) ? have : 0) + count;
            col.Update(record);
        }

        public static bool GrantAvatarItem(ulong playerid, string? desc)
        {
            string key = NormalizeAvatarItemDesc(desc);
            if (key.Length == 0) return false;

            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;

            record.player_Extra ??= new player_extra();
            record.player_Extra.AvatarItems ??= new List<string>(DefaultAvatarItemDescs());

            // They have it now (or already had it): it is no longer a wish.
            WishlistDB.RemoveAcquired(playerid, avatarItemDesc: desc);

            if (record.player_Extra.AvatarItems.Any(d => NormalizeAvatarItemDesc(d).Equals(key, StringComparison.OrdinalIgnoreCase)))
                return false;

            record.player_Extra.AvatarItems.Add(desc!.Trim());
            col.Update(record);
            Console.WriteLine($"[items] granted '{desc!.Trim()}' to {playerid}");
            return true;
        }

        /// <summary>Developers get the whole catalog - the dev locker.</summary>
        public static bool HasDevLocker(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            return record != null && (record.dev_Flag & DevFlag.dev) == DevFlag.dev;
        }

        /// <summary>
        /// Marks CONTENTLESS pending gift boxes as opened, so they stop lingering forever. An empty
        /// box (no gift drop, no avatar item, no consumable, no equipment) is filtered out of the
        /// gifts list the client is sent - it can never be opened there and would sit in the DB
        /// counting toward the gift badge as a "random gift" that won't clear. Marking it opened
        /// retires it. Returns how many were retired.
        /// </summary>
        public static int MarkEmptyGiftBoxesOpened(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            var boxes = record?.player_Extra?.PendingGiftBoxes;
            if (boxes == null || boxes.Count == 0) return 0;

            int cleaned = 0;
            foreach (var g in boxes)
            {
                if (g.Opened) continue;
                // Currency / XP boxes count as content (a token box has no item and is real).
                if (!Campus_live_rewrite.api.server.GiftBoxes.HasContent(g)) { g.Opened = true; cleaned++; }
            }

            if (cleaned > 0) col.Update(record);
            return cleaned;
        }

        public static bool AddPendingGiftBox(ulong playerid, int giftContext, string? message, ulong fromAccountId = 0)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;

            record.player_Extra ??= new player_extra();
            record.player_Extra.PendingGiftBoxes ??= new List<PendingGiftBox>();
            record.player_Extra.PendingGiftBoxes.Add(new PendingGiftBox
            {
                GiftContext = giftContext,
                Message = message,
                FromAccountId = fromAccountId
            });

            col.Update(record);
            return true;
        }

        /// <summary>Stores a fully-populated gift box (store purchases carry item data).</summary>
        /// <summary>Backfill a numeric Id onto any pending gift box that still has NumericId 0 (older
        /// reward/select boxes). Without it the gifts endpoint emits a guid Id the client rejects.</summary>
        // ---- gift package ids: ONE sequence for the whole server ------------------------------
        //
        // A gift box id has to be unique across PLAYERS, not just within one player's list. The
        // client networks a spawned box into the room by its package id, and "Open All" walks the
        // boxes it can see: with per-player ids (reward boxes 1001+ per player, purchases from a
        // counter that restarted at 5000 on every deploy) two players' boxes in the same room
        // shared ids. Player.log, opening gifts in the Rec Center after another player had: the
        // client posts consume for 5003 and 5002 (the other player's, already opened - "no matching
        // unopened box"), throws "Gift is missing from the list", and only then reaches its own
        // 5004 - which the other player ALSO had as 5004. The reference's received_gift table is
        // AUTOINCREMENT for the same reason.
        //
        // Seeded once above every id already stored, so nothing pending collides with a new box.
        private sealed class Counter { public int Id { get; set; } public int Value { get; set; } }
        private static readonly object _giftIdLock = new();

        public static int NextGiftPackageId()
        {
            lock (_giftIdLock)
            {
                var counters = PlayerDBFile.GetCollection<Counter>("counters");
                var row = counters.FindById(1);
                if (row == null)
                {
                    int max = 100_000;
                    try
                    {
                        foreach (var p in PlayerDBFile.GetCollection<player_data>("players").FindAll())
                            foreach (var b in p.player_Extra?.PendingGiftBoxes ?? new List<PendingGiftBox>())
                                if (b.NumericId > max) max = b.NumericId;
                    }
                    catch { }
                    row = new Counter { Id = 1, Value = max };
                    Console.WriteLine($"[gifts] gift package id sequence seeded at {max}");
                }
                row.Value++;
                counters.Upsert(row);
                return row.Value;
            }
        }

        /// <summary>True when a box with this id is (or was) some OTHER player's.</summary>
        public static bool GiftBoxBelongsToSomeoneElse(ulong playerid, int numericId)
        {
            try
            {
                foreach (var p in PlayerDBFile.GetCollection<player_data>("players").Find(x => x.playerid != playerid))
                    if ((p.player_Extra?.PendingGiftBoxes ?? new List<PendingGiftBox>()).Any(b => b.NumericId == numericId))
                        return true;
            }
            catch { }
            return false;
        }

        public static void EnsureGiftBoxNumericIds(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            var boxes = record?.player_Extra?.PendingGiftBoxes;
            if (boxes == null || boxes.Count == 0) return;
            bool changed = false;
            foreach (var b in boxes)
                if (b.NumericId == 0) { b.NumericId = NextGiftPackageId(); changed = true; }
            if (changed) col.Update(record);
        }

        // The guid is compared on its UUID form: Equipments.json spells every skin as a full UUID,
        // but 112 of the 187 skin rows in the Watch store spell the SAME skins as 22-char short
        // guids. Compared as text they never matched, so a bought skin was stored under a key
        // that /equipment/v2/getUnlocked could never find - "Can't load item" on the box, and the
        // skin never turned up in the locker.
        public static string EquipmentSkinKey(string? prefab, string? guid)
        {
            string g = Campus_live_rewrite.api.server.Classes.ItemGuids.NormalizeGuid(guid) ?? (guid ?? "").Trim().ToLowerInvariant();
            return $"{(prefab ?? "").Trim().ToLowerInvariant()}|{g}";
        }

        /// <summary>The profile's "show my recent rooms" privacy toggle. False if there's no such player.</summary>
        public static bool SetRecentRoomHistoryVisible(ulong playerid, bool visible)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;
            record.player_Extra ??= new player_extra();
            record.player_Extra.IsRecentRoomHistoryVisible = visible;
            col.Update(record);
            return true;
        }

        public static HashSet<string> OwnedEquipmentSkins(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            return new HashSet<string>(record?.player_Extra?.EquipmentSkins ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool OwnsCustomItem(ulong playerid, string caid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            return record?.player_Extra?.CustomItems?.Any(c => string.Equals(c, caid, StringComparison.OrdinalIgnoreCase)) == true;
        }

        public static HashSet<string> OwnedCustomItems(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            return new HashSet<string>(record?.player_Extra?.CustomItems ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Put a UGC item in the player's wardrobe. Returns true if it was new.</summary>
        public static bool GrantCustomItem(ulong playerid, string? caid)
        {
            if (string.IsNullOrWhiteSpace(caid)) return false;
            WishlistDB.RemoveAcquired(playerid, 0, caid);
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;
            record.player_Extra ??= new player_extra();
            record.player_Extra.CustomItems ??= new List<string>();
            if (record.player_Extra.CustomItems.Any(c => string.Equals(c, caid, StringComparison.OrdinalIgnoreCase))) return false;
            record.player_Extra.CustomItems.Add(caid);
            col.Update(record);
            Console.WriteLine($"[ugc] {playerid} now owns custom item {caid}");
            return true;
        }

        /// <summary>Put a skin in the player's locker. Returns true if it was new.</summary>
        public static bool GrantEquipmentSkin(ulong playerid, string? prefab, string? guid)
        {
            if (string.IsNullOrWhiteSpace(prefab) || string.IsNullOrWhiteSpace(guid)) return false;
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;
            record.player_Extra ??= new player_extra();
            record.player_Extra.EquipmentSkins ??= new List<string>();
            string key = EquipmentSkinKey(prefab, guid);
            WishlistDB.RemoveAcquired(playerid, equipPrefab: prefab, equipGuid: guid);
            if (record.player_Extra.EquipmentSkins.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) return false;
            record.player_Extra.EquipmentSkins.Add(key);
            col.Update(record);
            Console.WriteLine($"[skins] {playerid} now owns skin {prefab} {guid}");
            return true;
        }

        /// <param name="grantSkin">
        /// True only for a store purchase (for yourself or as a gift) or an admin-panel gift: those
        /// are the only ways to OWN an equipment skin. Any other box that carries a skin (a weekly
        /// challenge gift, a reward) still arrives, but does not unlock the skin.
        /// </param>
        public static bool AddPendingGiftBoxFull(ulong playerid, PendingGiftBox box, bool grantSkin = false)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;

            record.player_Extra ??= new player_extra();
            record.player_Extra.PendingGiftBoxes ??= new List<PendingGiftBox>();

            // Getting the item takes it off the recipient's wishlist (admin gifts, rewards, purchases).
            WishlistDB.RemoveAcquired(playerid, 0, box.CustomAvatarItemId, box.GiftDropId,
                box.AvatarItemDesc, box.ConsumableItemDesc, box.EquipmentPrefabName, box.EquipmentModificationGuid);

            // Gift messages are censored like chat (swears masked with *). Every box comes through
            // here, and callers push the same object afterwards, so the recipient and everyone who
            // sees the box open (CampusNet relays the package) read the censored text.
            if (!string.IsNullOrEmpty(box.Message))
            {
                string clean = Campus_live_rewrite.api.server.Classes.Sanitize.SanitizeMessage(box.Message);
                if (clean != box.Message) Console.WriteLine($"[gifts] censored the message on a gift to {playerid}");
                box.Message = clean;
            }

            // Every gift box MUST have a numeric Id. The gifts endpoint emits the guid Id when
            // NumericId is 0, but the client's gift model expects a NUMBER there and rejects the whole
            // gifts array as "Malformed Response" if it sees a guid - which silently killed every
            // reward gift box (and made the reward watch open then instantly close). Purchases already
            // set NumericId; reward/select boxes didn't, so assign one here - from the server-wide
            // sequence (NextGiftPackageId), never a per-player count.
            if (box.NumericId == 0) box.NumericId = NextGiftPackageId();

            record.player_Extra.PendingGiftBoxes.Add(box);

            // A purchased or gifted item has to enter the wardrobe too, now that owning it
            // is no longer implied by the catalog being served to everyone. Done here, in
            // the same write, so a bought item can never be left unowned.
            if (!string.IsNullOrWhiteSpace(box.AvatarItemDesc))
            {
                string key = NormalizeAvatarItemDesc(box.AvatarItemDesc);
                record.player_Extra.AvatarItems ??= new List<string>(DefaultAvatarItemDescs());

                if (!record.player_Extra.AvatarItems.Any(d =>
                        NormalizeAvatarItemDesc(d).Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    record.player_Extra.AvatarItems.Add(box.AvatarItemDesc!.Trim());
                    Console.WriteLine($"[items] {playerid} now owns '{box.AvatarItemDesc!.Trim()}' ({box.FriendlyName})");
                }
            }

            if (!string.IsNullOrWhiteSpace(box.CustomAvatarItemId))
            {
                record.player_Extra.CustomItems ??= new List<string>();
                if (!record.player_Extra.CustomItems.Any(c => string.Equals(c, box.CustomAvatarItemId, StringComparison.OrdinalIgnoreCase)))
                {
                    record.player_Extra.CustomItems.Add(box.CustomAvatarItemId!);
                    Console.WriteLine($"[ugc] {playerid} now owns custom item {box.CustomAvatarItemId} ({box.FriendlyName})");
                }
            }

            // A consumable: counted into the player's own stock (was: everyone had every
            // consumable with an infinite count, so nothing was ever really "got").
            if (!string.IsNullOrWhiteSpace(box.ConsumableItemDesc))
            {
                record.player_Extra.Consumables ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                string ck = NormalizeConsumableDesc(box.ConsumableItemDesc);
                int add = box.ConsumableCount > 0 ? box.ConsumableCount : 1;
                record.player_Extra.Consumables[ck] = (record.player_Extra.Consumables.TryGetValue(ck, out var have) ? have : 0) + add;
                Console.WriteLine($"[consumables] {playerid} +{add} {box.FriendlyName} -> {record.player_Extra.Consumables[ck]}");
            }

            // An equipment SKIN enters the locker the same way an avatar item enters the wardrobe -
            // in this write, so a bought skin can never be left unowned (it used to be: skins were
            // served to everyone, so nothing ever recorded a purchase).
            if (grantSkin && !string.IsNullOrWhiteSpace(box.EquipmentPrefabName) && !string.IsNullOrWhiteSpace(box.EquipmentModificationGuid))
            {
                record.player_Extra.EquipmentSkins ??= new List<string>();
                string skey = EquipmentSkinKey(box.EquipmentPrefabName, box.EquipmentModificationGuid);
                if (!record.player_Extra.EquipmentSkins.Any(k => string.Equals(k, skey, StringComparison.OrdinalIgnoreCase)))
                {
                    record.player_Extra.EquipmentSkins.Add(skey);
                    Console.WriteLine($"[skins] {playerid} now owns skin {box.EquipmentPrefabName} {box.EquipmentModificationGuid} ({box.FriendlyName})");
                }
            }

            col.Update(record);
            return true;
        }

        public static List<PendingGiftBox> GetPendingGiftBoxes(ulong playerid)
        {
            var acc = GetFullAccount(playerid);
            var all = acc?.player_Extra?.PendingGiftBoxes;
            if (all == null) return new List<PendingGiftBox>();
            return all.Where(g => !g.Opened).ToList();
        }

        // ================= Daily objectives / weekly challenges (quests) =================
        // Per-player progress only - the objective/challenge CATALOG (descriptions, targets,
        // rewards) is client-side. See APIController objectives + challenge handlers.

        // Daily progress belongs to the day it was made in (DailyChallenges.PeriodStartUtc). A group
        // whose ClearedAt is from an earlier day is reported - and, on the next write, stored - as a
        // fresh one: no progress, not completed, cleared now.
        public static (List<ObjectiveProgress> objectives, List<ObjectiveGroupProgress> groups)
            GetObjectivesProgress(ulong playerid)
        {
            var ex = GetFullAccount(playerid)?.player_Extra;
            var groups = ex?.ObjectiveGroups ?? new List<ObjectiveGroupProgress>();
            var stale = groups.Where(g => Campus_live_rewrite.api.server.DailyChallenges.IsStale(g.ClearedAt)).Select(g => g.Group).ToHashSet();
            var objectives = (ex?.Objectives ?? new List<ObjectiveProgress>()).Where(o => !stale.Contains(o.Group)).ToList();
            var shown = groups.Select(g => stale.Contains(g.Group)
                ? new ObjectiveGroupProgress { Group = g.Group, IsCompleted = false, ClearedAt = DateTime.UtcNow.ToString("o") }
                : g).ToList();
            return (objectives, shown);
        }

        /// <summary>Start a group's new day in place: drop its objectives, un-complete it, stamp ClearedAt.</summary>
        private static void ResetObjectiveGroup(player_extra ex, ObjectiveGroupProgress g)
        {
            ex.Objectives?.RemoveAll(o => o.Group == g.Group);
            g.IsCompleted = false;
            g.ClearedAt = DateTime.UtcNow.ToString("o");
        }

        /// <summary>
        /// Upsert one objective's progress on the (Group, Index) key. Returns true ONLY on the call
        /// that first completes it while its reward is unclaimed - the once-only reward latch - so
        /// the caller grants the reward exactly once. Hands back the parent group's state.
        /// </summary>
        /// <param name="groupJustCompleted">True ONLY on the call that finishes the group's last objective
        /// for today - the once-a-day latch the daily reward is paid on.</param>
        public static bool UpdateObjective(ulong playerid, long group, int index,
            double progress, double visualProgress, bool isCompleted,
            out ObjectiveGroupProgress groupState, out bool groupJustCompleted)
        {
            groupState = new ObjectiveGroupProgress { Group = group };
            groupJustCompleted = false;
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;

            record.player_Extra ??= new player_extra();
            record.player_Extra.Objectives ??= new List<ObjectiveProgress>();
            record.player_Extra.ObjectiveGroups ??= new List<ObjectiveGroupProgress>();

            // Yesterday's progress never counts toward today.
            var g = record.player_Extra.ObjectiveGroups.FirstOrDefault(x => x.Group == group);
            if (g == null)
            {
                g = new ObjectiveGroupProgress { Group = group, ClearedAt = DateTime.UtcNow.ToString("o") };
                record.player_Extra.ObjectiveGroups.Add(g);
            }
            else if (Campus_live_rewrite.api.server.DailyChallenges.IsStale(g.ClearedAt))
                ResetObjectiveGroup(record.player_Extra, g);

            var o = record.player_Extra.Objectives.FirstOrDefault(x => x.Group == group && x.Index == index);
            if (o == null)
            {
                o = new ObjectiveProgress { Group = group, Index = index };
                record.player_Extra.Objectives.Add(o);
            }

            o.Progress = progress;
            o.VisualProgress = visualProgress;

            bool justCompleted = isCompleted && !o.HasClaimedReward;
            if (isCompleted) o.IsCompleted = true;
            if (justCompleted) o.HasClaimedReward = true;

            // Every objective of today's set done -> the group completes, once a day.
            int done = record.player_Extra.Objectives.Count(x => x.Group == group && x.IsCompleted);
            if (!g.IsCompleted && done >= Campus_live_rewrite.api.server.DailyChallenges.RequiredFor(group))
            {
                g.IsCompleted = true;
                groupJustCompleted = true;
            }
            groupState = g;

            col.Update(record);
            return justCompleted;
        }

        /// <summary>
        /// The client's "start a new day" for a group. It used to mark the group COMPLETED, so every
        /// player's daily group sat at IsCompleted=true. Now it resets the group - but only once per
        /// day: the client asks on most logins, and resetting each time would wipe the day's progress.
        /// A group already reset today is handed back as it is.
        /// </summary>
        public static ObjectiveGroupProgress ClearObjectiveGroup(ulong playerid, long group)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            var g = new ObjectiveGroupProgress { Group = group, IsCompleted = false, ClearedAt = DateTime.UtcNow.ToString("o") };
            if (record == null) return g;

            record.player_Extra ??= new player_extra();
            record.player_Extra.ObjectiveGroups ??= new List<ObjectiveGroupProgress>();
            var existing = record.player_Extra.ObjectiveGroups.FirstOrDefault(x => x.Group == group);
            if (existing == null) record.player_Extra.ObjectiveGroups.Add(g);
            else if (Campus_live_rewrite.api.server.DailyChallenges.IsStale(existing.ClearedAt)) { ResetObjectiveGroup(record.player_Extra, existing); g = existing; }
            else return existing;

            col.Update(record);
            return g;
        }

        public static HashSet<int> GetCompletedChallengeIds(ulong playerid, int mapId)
        {
            var set = new HashSet<int>();
            var st = GetFullAccount(playerid)?.player_Extra?.ChallengeStatuses;
            if (st != null)
                foreach (var s in st)
                    if (s.ChallengeMapId == mapId && s.Complete) set.Add(s.ChallengeId);
            return set;
        }

        /// <summary>Latch one challenge's completion within a rotation (false-&gt;true only). Returns the stored state.</summary>
        public static bool RecordChallengeProgress(ulong playerid, int mapId, int challengeId, bool complete)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;

            record.player_Extra ??= new player_extra();
            record.player_Extra.ChallengeStatuses ??= new List<ChallengeStatus>();

            var s = record.player_Extra.ChallengeStatuses
                .FirstOrDefault(x => x.ChallengeMapId == mapId && x.ChallengeId == challengeId);
            if (s == null)
            {
                s = new ChallengeStatus { ChallengeMapId = mapId, ChallengeId = challengeId };
                record.player_Extra.ChallengeStatuses.Add(s);
            }
            s.Complete = s.Complete || complete;
            s.UpdatedAt = DateTime.UtcNow.ToString("o");

            col.Update(record);
            return s.Complete;
        }

        /// <summary>At-most-once gift claim for a challenge map. Returns true only on the FIRST claim.</summary>
        public static bool ClaimChallengeGift(ulong playerid, int mapId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record == null) return false;

            record.player_Extra ??= new player_extra();
            record.player_Extra.ClaimedChallengeMaps ??= new List<int>();
            if (record.player_Extra.ClaimedChallengeMaps.Contains(mapId)) return false;

            record.player_Extra.ClaimedChallengeMaps.Add(mapId);
            col.Update(record);
            return true;
        }

        /// <summary>
        /// Opens (consumes) a gift box for a player. Returns the box that was opened, or null
        /// when the player has no such unopened box.
        ///
        /// The item was already granted at PURCHASE time (see AddPendingGiftBoxFull) - opening
        /// is only the cosmetic reveal, so this just marks the box opened so it stops showing
        /// as pending. Without this the client's POST /api/avatar/v2/gifts/consume 404'd, the
        /// box was never marked opened, and GetPendingGiftBoxes handed it straight back - so a
        /// box could be "opened" over and over and never went away.
        ///
        /// The client sends back whatever id we emitted in the gifts list, which is NumericId
        /// for purchases and the guid Id otherwise (see the Gifts endpoint), so match on both.
        /// </summary>
        /// <summary>Flip a box's PayOnOpen off. Returns true only for the call that flipped it.</summary>
        public static bool ClearGiftBoxPayOnOpen(ulong playerid, PendingGiftBox box)
        {
            lock (_giftPayLock)
            {
                var col = PlayerDBFile.GetCollection<player_data>("players");
                var record = col.FindOne(x => x.playerid == playerid);
                var stored = record?.player_Extra?.PendingGiftBoxes?.FirstOrDefault(g =>
                    (box.NumericId != 0 && g.NumericId == box.NumericId) || (box.NumericId == 0 && g.Id == box.Id));
                if (stored == null || (!stored.PayOnOpen && !stored.XpOnOpen)) return false;
                stored.PayOnOpen = false; stored.XpOnOpen = false;
                box.PayOnOpen = false; box.XpOnOpen = false;
                col.Update(record);
                return true;
            }
        }
        private static readonly object _giftPayLock = new();

        /// <summary>Persist PayOnOpen=true on an already-stored box.</summary>
        public static void SetGiftBoxPayOnOpen(ulong playerid, PendingGiftBox box)
        {
            lock (_giftPayLock)
            {
                var col = PlayerDBFile.GetCollection<player_data>("players");
                var record = col.FindOne(x => x.playerid == playerid);
                var stored = record?.player_Extra?.PendingGiftBoxes?.FirstOrDefault(g =>
                    (box.NumericId != 0 && g.NumericId == box.NumericId) || (box.NumericId == 0 && g.Id == box.Id));
                if (stored == null) return;
                stored.PayOnOpen = true;
                col.Update(record);
            }
        }

        public static PendingGiftBox? ConsumeGiftBox(ulong playerid, string? id, int numericId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            var boxes = record?.player_Extra?.PendingGiftBoxes;
            if (boxes == null) return null;

            var box = boxes.FirstOrDefault(g => !g.Opened &&
                ((numericId != 0 && g.NumericId == numericId) ||
                 (!string.IsNullOrEmpty(id) && g.Id == id)));

            if (box == null) return null;

            box.Opened = true;
            col.Update(record);
            Console.WriteLine($"[gifts] {playerid} opened box {(numericId != 0 ? numericId.ToString() : id)} ({box.FriendlyName})");
            return box;
        }

        public static bool SetPlayerSteamId(ulong steamId, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record?.player == null) return false;

            record.platform = Platforms.Steam;
            record.player.PlatformIds ??= new List<mPlatformID>();
            // Replace any existing Steam entry, add if missing.
            var existing = record.player.PlatformIds.FirstOrDefault(p => p != null && p.Platform == Platforms.Steam);
            if (existing != null) existing.PlatformId = steamId;
            else record.player.PlatformIds.Add(new mPlatformID { Platform = Platforms.Steam, PlatformId = steamId });
            col.Update(record);
            return true;
        }

        /// <summary>The account using this email (case-insensitive), or 0.</summary>
        public static ulong GetAccountIdByEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return 0;
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var hit = col.FindAll().FirstOrDefault(x => x.player?.Email != null &&
                                                        string.Equals(x.player.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
            return hit?.playerid ?? 0;
        }

        public static bool SetPlayerEmail(string? email, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var record = col.FindOne(x => x.playerid == playerid);
            if (record?.player == null) return false;
            record.player.Email = email;
            col.Update(record);
            return true;
        }

        public static bool SetLastLoginTime(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Id == playerid);
            if (player != null)
            {
                player.player.last_login_time = DateTime.UtcNow;
                col.Update(player);
                return true;
            }
            return false;
        }

        public static bool ResetCheerCreditIfNewDay(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Id == playerid);

            if (player?.player?.PlayerReputation == null)
                return false;

            DateTime lastLoginDate = player.player.last_login_time.Date;
            DateTime today = DateTime.UtcNow.Date;

            if (lastLoginDate < today)
            {
                player.player.PlayerReputation.CheerCredit = 20;
                player.player.last_login_time = DateTime.UtcNow;

                col.Update(player);
                return true;
            }

            return false;
        }


        public static bool AddIpAddress(ulong playerid, string ip)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Id == playerid);
            if (player != null)
            {
                if (player.player_Extra.IpAddresses == null)
                    player.player_Extra.IpAddresses = new List<string>();

                string encryptedIp = Utils.EncryptAes(ip);

                if (!player.player_Extra.IpAddresses.Contains(encryptedIp))
                    player.player_Extra.IpAddresses.Add(encryptedIp);

                col.Update(player);
                return true;
            }
            return false;
        }


        public static List<string> GetDecryptedIpAddresses(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Id == playerid);

            List<string> result = new();
            if (player?.player_Extra?.IpAddresses != null)
            {
                foreach (string encryptedIp in player.player_Extra.IpAddresses)
                {
                    try
                    {
                        result.Add(Utils.DecryptAes(encryptedIp));
                    }
                    catch
                    {
                       
                    }
                }
            }
            return result;
        }

        public static void SetPlayerEmoji(string emoji, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player != null)
                {
                    player_Setting1.player.DisplayEmoji = emoji;
                    col.Update(player_Setting1);
                    return;
                }
            }
        }

        public static void SetPlayerPronounFlags(int pronounflag, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player != null)
                {
                    player_Setting1.player.pronounFlags = pronounflag;
                    col.Update(player_Setting1);
                }
                else
                {
                   
                }
            }
            else
            {
                
            }
        }
        public static void AddSubscription(ulong fromPlayerId, ulong toPlayerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var playerRecord = col.FindOne(x => x.playerid == fromPlayerId);
            if (playerRecord == null || playerRecord.player_Extra == null)
                throw new InvalidOperationException($"player_Extra for player {fromPlayerId} not found.");

            // check if already subscribed
            if (!playerRecord.player_Extra.SubbedTo.Contains(toPlayerId))
            {
                playerRecord.player_Extra.SubbedTo.Add(toPlayerId);
                col.Update(playerRecord);
                IncrementSubscriberCount(toPlayerId);
            }
        }

        public static void IncrementSubscriberCount(ulong playerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerId);
            if (player != null && player.player_Extra != null)
            {
                player.player_Extra.sub_count++;
                col.Update(player);
            }
        }

        public static void RemoveSubscription(ulong fromPlayerId, ulong toPlayerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var playerRecord = col.FindOne(x => x.playerid == fromPlayerId);
            if (playerRecord == null || playerRecord.player_Extra == null)
                return;

            if (playerRecord.player_Extra.SubbedTo.Remove(toPlayerId))
            {
                col.Update(playerRecord);
                DecrementSubscriberCount(toPlayerId);
            }
        }

        public static void DecrementSubscriberCount(ulong playerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerId);
            if (player != null && player.player_Extra != null && player.player_Extra.sub_count > 0)
            {
                player.player_Extra.sub_count--;
                col.Update(player);
            }
        }

        /// <summary>Everyone subscribed to <paramref name="playerId"/> (the creator's subscribers).</summary>
        public static List<ulong> GetSubscriberIds(ulong playerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            return col.FindAll()
                      .Where(x => x.player_Extra?.SubbedTo != null && x.player_Extra.SubbedTo.Contains(playerId))
                      .Select(x => x.playerid)
                      .ToList();
        }

        public static List<ulong> GetSubscribedToIds(ulong playerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var playerRecord = col.FindOne(x => x.playerid == playerId);
            if (playerRecord != null && playerRecord.player_Extra != null)
            {
                return playerRecord.player_Extra.SubbedTo;
            }
            return new List<ulong>();
        }

        public static void SetPlayerIdentityFlags(int IdentityFlag, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player != null)
                {
                    player_Setting1.player.identityFlags = IdentityFlag;
                    col.Update(player_Setting1);
                }
                else
                {

                }
            }
            else
            {
            }
        }

        public static void SetPlayerImageName(string imageName, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);

            if (player?.player == null)
                return;

            player.player.profileImage = imageName;
            col.Update(player);
            Campus_live_rewrite.api.server.Notifications.FireAndForget(Campus_live_rewrite.api.server.Notifications.PushAccountUpdate(playerid), $"account-update {playerid}");
        }

        public static void SetPlayerBanner(string imageName, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);

            if (player?.player == null)
                return;

            player.player.bannerImage = imageName;
            col.Update(player);
        }

        public static int GetAccountCountByPlatformId(ulong platformId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            return col.Count(x =>
                x.player.PlatformIds != null &&
                x.player.PlatformIds
                    .Select(pid => pid.PlatformId)
                    .Any(id => id == platformId)
            );
        }
        public static int GetAccountCountByDiscordId(ulong discordId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            return col.Count(x =>
                x.player_Extra != null &&
                x.player_Extra.DiscordId == discordId
            );
        }

        public static List<ulong> GetAccountIdsByDiscordId(ulong discordId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            return col
                .Find(x =>
                    x.player_Extra != null &&
                    x.player_Extra.DiscordId == discordId
                )
                .Select(x => (ulong)x.playerid)
                .ToList();
        }


        public static ulong GetPlayerCount()
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var players = col.FindAll().ToList();

            if (players.Count == 0)
                return 0;

            ulong minID = players.Min(p => p.playerid);
            ulong maxID = players.Max(p => p.playerid);
            ulong expectedCount = (ulong)(maxID - minID + 1);

            return expectedCount;
        }

        

        public static List<object> Find_all_player_filtered(string? id, string? displayName, string? minLevel, string? jrState, string? platformId, string? deviceId, bool onlyBannedPlayers)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players"); List<object> list = new List<object>();

            foreach (player_data player in col.FindAll())
            {
                if (id != null && player.playerid.ToString() != id)
                    continue;

                if (displayName != null && !player.player.DisplayName.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (minLevel != null && int.TryParse(minLevel, out int minLvl))
                {
                    if (player.player.Level < minLvl)
                        continue;
                }

                if (jrState != null && jrState != "N/A")
                {
                    bool expectedJunior = jrState == "true";
                    if (player.player.IsJunior != expectedJunior)
                        continue;
                }

                if (!string.IsNullOrEmpty(platformId) && (player.player.PlatformIds == null || !player.player.PlatformIds.Any(pid => pid.PlatformId.ToString() == platformId)))
                {
                    continue;
                }

                if (onlyBannedPlayers)
                {
                    var block = player.player_Extra.ModerationBlockDetails;
                    if (block == null || block.IsBan != true || !HasActiveModerationBlock(player.playerid))
                        continue;
                }


                if (deviceId != null && !string.Equals(player.deviceId.ToString(), deviceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var room_data = GetAccountBulk_full(player.playerid);
                if (room_data != null)
                    list.Add(room_data);
            }

            return list;
        }

        public static object? GetAccountBulk_full(ulong playerid)
        {

            var col = PlayerDBFile.GetCollection<player_data>("players");
            List<object> list = new List<object>();
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null)
            {
                return null;
            }
            // The admin row template reads `player.PlatformID` (singular) — surface the first
            // non-zero linked platform id so accounts created via /create-account show up as
            // "Steam 76561…" instead of "Steam undefined".
            var firstPlatformId = player?.player?.PlatformIds?
                .FirstOrDefault(p => p != null && p.PlatformId != 0)?.PlatformId.ToString();

            // PlatformID above is the first non-zero id of ANY platform, so on a Quest
            // account it is an Oculus id even though the admin table labels the column
            // "Steam". The Steam allowlist must never be fed that, so surface the real
            // Steam id separately - empty when the account has none.
            var steamId = player?.player?.PlatformIds?
                .FirstOrDefault(p => p != null && p.Platform == Platforms.Steam && p.PlatformId != 0)
                ?.PlatformId.ToString() ?? "";

            return new
            {
                AccountId = player?.playerid,
                Level = player?.player?.Level,
                xp = player?.player?.XP,
                DisplayName = player?.player?.DisplayName,
                DeviceId = player?.deviceId,
                Username = player?.player?.Username,
                IsJunior = player?.player?.IsJunior,
                ProfileImage = player?.player?.profileImage,
                LastLoginTime = player?.player?.last_login_time,
                CreatedAt = player?.player?.created_at,
                PlatformID  = firstPlatformId ?? "",
                SteamID     = steamId,
                PlatformIDs = player?.player?.PlatformIds,
                // Linked Discord (website Settings > Discord tab). Empty/0 when not linked.
                DiscordUsername = player?.player_Extra?.DiscordUsername ?? "",
                DiscordId       = (player?.player_Extra?.DiscordId ?? 0) == 0 ? "" : (player!.player_Extra!.DiscordId).ToString(),
                dormroom_id = player?.player_Extra?.DormRoomID,
                DevFlagRaw  = (int)(player?.dev_Flag ?? DevFlag.none),
                IsDev         = ((player?.dev_Flag ?? DevFlag.none) & DevFlag.dev)         == DevFlag.dev,
                IsMod         = ((player?.dev_Flag ?? DevFlag.none) & DevFlag.mod)         == DevFlag.mod,
                IsScreenshare = ((player?.dev_Flag ?? DevFlag.none) & DevFlag.screenshare) == DevFlag.screenshare,
                IsSupport     = ((player?.dev_Flag ?? DevFlag.none) & DevFlag.support)     == DevFlag.support,
                IsStudio      = ((player?.dev_Flag ?? DevFlag.none) & DevFlag.studio)      == DevFlag.studio,
                IsInfluencer  = player?.player_Extra?.Influencer?.isInfluencer == true,
            };
        }

        public static void DeletePlayer(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);

            if (player != null)
            {
                col.Delete(playerid);
                Console.WriteLine("Deleted player id " + playerid);
            }
            else
            {
                Console.WriteLine($"Player with ID {playerid} not found.");
            }
        }

        public static int DeletePlayersWithNullLastLogin()
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var minDate = DateTime.MinValue;

            var playersToDelete = col
                .Find(x => x.player.last_login_time == minDate)
                .Select(x => x.playerid)
                .ToList();

            int deleted = 0;

            foreach (var id in playersToDelete)
            {
                if (DeletePlayerFull(id))
                    deleted++;
            }

            Console.WriteLine($"Deleted {deleted} players with default LastLogin.");

            return deleted;
        }


        public static bool DeletePlayerFull(ulong playerid)
        {
            try
            {
                DeletePlayer(playerid);
                RoomDB.DeleteAllRoomsFromPlayerId(playerid);
                HeartbeatDB.ClearPlayerHeartbeat(playerid);
                SettingsDB.DeleteSettings(playerid);
                LinkDB.DeleteLinksFromPlayerId(playerid);
                ImageMetadataDB.DeleteImagesFromPlayerId(playerid);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string SetRecentRooms(ulong playerid, ulong room_id)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Id == playerid);

            if (player != null)
            {
                if (player.player.recent_rooms == null)
                {
                    player.player.recent_rooms = new List<ulong>();
                }

                player.player.recent_rooms.Remove(room_id);
                player.player.recent_rooms.Insert(0, room_id);

                col.Update(player);
            }
            return "";
        }

        public static string GetRecentRooms(ulong playerId, int skip = 0, int take = 20)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Id == playerId);

            if (player == null || player.player.recent_rooms == null || !player.player.recent_rooms.Any())
            {
                return "[]";
            }

            // Unpublished rooms (RoomDB.IsRoomPublished: a Private user room is a draft) are left
            // out of recently played - for the player themselves and on their profile alike.
            // Filtered BEFORE paging so a page is still `take` rooms long. Dorms are private by
            // nature rather than unpublished drafts, so they stay.
            var visitedRooms = new List<RoomDBClasses.RoomRoot>();
            int toSkip = Math.Max(0, skip), toTake = Math.Max(1, take);

            foreach (ulong roomId in player.player.recent_rooms)
            {
                var room = RoomDB.GetRoom(roomId);
                if (room == null) continue;
                if (!room.IsDorm && !RoomDB.IsRoomPublished(room)) continue;
                if (toSkip > 0) { toSkip--; continue; }
                visitedRooms.Add(room);
                if (visitedRooms.Count >= toTake) break;
            }

            // Strip blobs the client cannot download - this listing is fetched moments
            // before every join and the client can join straight from a cached entry.
            Campus_live_rewrite.api.server.RoomBlobFilter.Strip(visitedRooms);
            return JsonConvert.SerializeObject(visitedRooms, Formatting.None);
        }


        public static string ClearRecentRoomsForAllPlayers()
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var allPlayers = col.FindAll();

            foreach (var player in allPlayers)
            {
                if (player.player.recent_rooms != null && player.player.recent_rooms.Count > 0)
                {
                    player.player.recent_rooms = new List<ulong>();
                    col.Update(player);
                }
            }

            return "";
        }

        public static string GetFavoritedRooms(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Id == playerid);

            // Was: `player == null || player.player_Extra.FavoritedRooms == null` -
            // if player_Extra itself was null the second clause threw NRE before the
            // check could help.
            if (player?.player_Extra?.FavoritedRooms == null)
            {
                return "[]";
            }

            var favRooms = new List<RoomDBClasses.RoomRoot>();

            foreach (ulong roomId in player.player_Extra.FavoritedRooms)
            {
                var room = RoomDB.GetRoom(roomId);
                if (room != null)
                {
                    favRooms.Add(room);
                }
            }

            return JsonConvert.SerializeObject(favRooms);
        }

        /// <summary>
        /// Records a device this account has logged in from. An account may accumulate
        /// SEVERAL - a player is allowed to play on more than one machine, and each new
        /// one is remembered the first time they sign in on it.
        ///
        /// The restriction is on CREATION, not login: see CanCreateAccountOnDevice. A
        /// device that already belongs to an account cannot be used to register another,
        /// which is what stops alt farms, while a real player moving to a second device
        /// just signs in and has it added here.
        ///
        /// (This used to overwrite the list with a single id - one account, one device.
        /// That made a legitimate second machine look like a device move on every login,
        /// and it meant the creation check below had nothing reliable to test against.)
        ///
        /// Blank ids are ignored rather than stored, so a login that sends no device id
        /// cannot wipe the binding (and cannot create an unbannable empty-id account).
        ///
        /// Device bans still work: AccountsOnDevice matches by CONTAINMENT, so banning a
        /// device finds every account that has ever used it. Note the trade-off that comes
        /// with allowing several devices - banning one does not stop a player who has
        /// another registered.
        /// </summary>
        public static void SetPlayerDeviceId(string DeviceId, ulong playerid)
        {
            if (string.IsNullOrWhiteSpace(DeviceId)) return;

            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player?.player == null) return;

            string id = DeviceId.Trim();
            var current = player.deviceId ?? new List<string>();

            // Already known for this account - nothing to write.
            if (current.Contains(id)) return;

            current.Add(id);
            player.deviceId = current;
            col.Update(player);

            Console.WriteLine($"[device] account {playerid} added device '{id}' "
                            + $"({current.Count} total)");
        }

        /// <summary>Drop device bindings (the account lock replacing an idle device). The flat
        /// anti-alt deviceId list is left alone: that device stays "used" for alt purposes.</summary>
        public static void UnbindDevices(ulong playerid, IEnumerable<string> deviceIds)
        {
            var ids = new HashSet<string>(deviceIds, StringComparer.Ordinal);
            if (ids.Count == 0) return;
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player?.DeviceBindings == null) return;
            int before = player.DeviceBindings.Count;
            player.DeviceBindings.RemoveAll(b => b.DeviceId != null && ids.Contains(b.DeviceId));
            if (player.DeviceBindings.Count != before) col.Update(player);
        }

        /// <summary>The account's device bindings (device id + the platform it signed in on).
        /// Empty for legacy rows until the next login re-binds them.</summary>
        public static List<mDeviceBind> GetDeviceBindings(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            return player?.DeviceBindings ?? new List<mDeviceBind>();
        }

        /// <summary>
        /// Record that this account signed in from this device on this platform. Writes BOTH the
        /// flat deviceId list (the anti-alt source of truth) and the platform-aware DeviceBindings
        /// list (used by the account lock's one-PC-one-Quest cap). Idempotent per (device,platform).
        /// </summary>
        public static void BindDevice(string? deviceId, Platforms platform, ulong playerid)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            string id = deviceId.Trim();

            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player?.player == null) return;

            bool changed = false;

            var flat = player.deviceId ?? new List<string>();
            if (!flat.Contains(id)) { flat.Add(id); player.deviceId = flat; changed = true; }

            var binds = player.DeviceBindings ?? new List<mDeviceBind>();
            var mine = binds.FirstOrDefault(b => b.DeviceId == id && b.Platform == platform);
            if (mine == null)
            {
                binds.Add(new mDeviceBind { DeviceId = id, Platform = platform, LastSeen = DateTime.UtcNow });
                player.DeviceBindings = binds;
                changed = true;
            }
            else
            {
                // Touch: the account lock lets a NEW device of the same class take over when this
                // one has been idle (AccountLock), so every login has to stamp it.
                mine.LastSeen = DateTime.UtcNow;
                player.DeviceBindings = binds;
                changed = true;
            }

            if (changed)
            {
                col.Update(player);
                Console.WriteLine($"[device] account {playerid} bound device '{id}' on {platform} "
                                + $"({binds.Select(b => b.DeviceId).Distinct().Count()} device(s))");
            }
        }

        /// <summary>
        /// Clear an account's device bindings so it can be signed in from fresh hardware - the
        /// operator's "reset" when a player legitimately replaces their PC or headset. Clears both
        /// the flat deviceId list and the platform-aware DeviceBindings.
        /// </summary>
        public static bool ClearDeviceBindings(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player == null) return false;
            player.deviceId = new List<string>();
            player.DeviceBindings = new List<mDeviceBind>();
            col.Update(player);
            Console.WriteLine($"[device] account {playerid} device bindings CLEARED");
            return true;
        }

        /// <summary>
        /// May a NEW account be created from this device?
        ///
        /// False once any existing account has used it. This is the whole anti-alt rule:
        /// one account per device at creation time, while logins from further devices are
        /// unrestricted (SetPlayerDeviceId).
        ///
        /// A blank device id returns TRUE - it carries no information, and refusing on it
        /// would lock out every caller that does not send one. Callers that care must
        /// require a non-empty id themselves before asking.
        /// </summary>
        public static bool CanCreateAccountOnDevice(string? deviceId, out ulong existingAccountId)
        {
            existingAccountId = 0;
            if (string.IsNullOrWhiteSpace(deviceId)) return true;

            var owners = AccountsOnDevice(deviceId).Where(o => !AccountLock.IsExempt(o)).ToList();
            if (owners.Count == 0) return true;

            // A device may hold one game-made account and one website account (see
            // ConfigCampus.MaxAccountsPerDevice). Creating in-game takes the game-made slot: refused
            // once the device is full, or once it already has a game-made account.
            existingAccountId = owners[0];
            if (owners.Count >= ConfigCampus.MaxAccountsPerDevice) return false;
            var gameMade = owners.FirstOrDefault(o => OriginOf(o) == AccountOrigin.InGame);
            if (gameMade != 0) { existingAccountId = gameMade; return false; }
            return true;
        }

        public enum AccountOrigin { Unknown, InGame, Website }

        /// <summary>
        /// Where an account was made. Rows created since the flag exists say so explicitly; older
        /// rows are Unknown, and an Unknown account fits EITHER of a device's two slots (it only
        /// counts toward the cap), so nobody who existed before the rule is locked out by it.
        /// </summary>
        public static AccountOrigin OriginOf(ulong accountId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var p = col.FindOne(x => x.playerid == accountId);
            if (p?.CreatedInGame == null) return AccountOrigin.Unknown;
            return p.CreatedInGame.Value ? AccountOrigin.InGame : AccountOrigin.Website;
        }

        /// <summary>The account's single device id, or null if it has never logged in.</summary>
        public static string? GetDeviceId(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            var list = player?.deviceId;
            if (list == null || list.Count == 0) return null;
            return string.IsNullOrWhiteSpace(list[0]) ? null : list[0];
        }

        /// <summary>
        /// Cached-login picker entries for the accounts bound to this device on this platform
        /// (see ConfigCampus.QuestDeviceCachedLogin). requirePassword=false: device ownership is the
        /// credential, the cached_login grant re-checks the same binding before issuing a token.
        /// </summary>
        public static List<AccountLogin> GetDeviceLogins(string? deviceId, Platforms platform, ulong platformId)
        {
            var result = new List<AccountLogin>();
            if (string.IsNullOrWhiteSpace(deviceId)) return result;
            string id = deviceId.Trim();
            var col = PlayerDBFile.GetCollection<player_data>("players");
            foreach (var p in col.FindAll())
            {
                if (p?.player == null || p.deviceId == null || !p.deviceId.Contains(id)) continue;
                // Only a binding made from THIS platform class counts (a PC login on some shared
                // device id must not turn into a headset cached login).
                if (p.DeviceBindings == null || !p.DeviceBindings.Any(b => b.DeviceId == id && b.Platform == platform)) continue;
                result.Add(new AccountLogin
                {
                    accountId = p.playerid,
                    createdAt = p.player.created_at,
                    lastLoginTime = p.player.last_login_time,
                    platform = platform,
                    platformId = platformId.ToString(),
                    requirePassword = false
                });
            }
            return result.OrderByDescending(a => a.lastLoginTime).ToList();
        }

        /// <summary>Is this account bound to this device on this platform?</summary>
        public static bool IsDeviceBoundTo(ulong accountId, string? deviceId, Platforms platform)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            string id = deviceId.Trim();
            var binds = GetDeviceBindings(accountId);
            return binds.Any(b => b.DeviceId == id && b.Platform == platform);
        }

        /// <summary>
        /// Every account bound to this device id. Surfaced by the admin panel so a device
        /// ban shows what else it takes out (alt accounts).
        /// </summary>
        public static List<ulong> AccountsOnDevice(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return new List<ulong>();
            string id = deviceId.Trim();
            var col = PlayerDBFile.GetCollection<player_data>("players");
            return col.FindAll()
                      .Where(x => x.deviceId != null && x.deviceId.Contains(id))
                      .Select(x => x.playerid)
                      .ToList();
        }

        /// <summary>Clears an account-level ban. Device/IP bans are lifted separately (BanDB).</summary>
        public static bool ClearModerationBlock(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player?.player_Extra == null) return false;

            player.player_Extra.ModerationBlockDetails = null;
            col.Update(player);
            return true;
        }
        public static void SetPlayerDiscordId(ulong DiscordId, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player != null)
            {
                if (player.player != null)
                {
                    player.player_Extra.DiscordId = DiscordId;
                    col.Update(player);
                    return;
                }
            }
        }

        public static string GetPlayerNameById(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            return player?.player.Username ?? "Unknown";
        }

        public static ulong GetPlayerIdByName(string name)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player.Username == name);
            return player?.player.Id ?? 0;
        }

        public static string GetAvatarSaved(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            // Was: only `player` was null-checked, then player_Extra.SavedOutfits was
            // dereferenced - NRE when player_Extra is null. And SerializeObject(null)
            // returns the literal "null", not "[]", which the client cannot parse as a
            // list. Both paths now yield a real empty array.
            var saved = player?.player_Extra?.SavedOutfits;
            if (saved == null || saved.Count == 0)
            {
                return "[]";
            }

            // The newer client's Saved Outfits page (SavedOutfitsWatchUIFlow) deserializes each
            // outfit into the StoredOutfit envelope and THROWS "Expected LegacyData but was null"
            // (DCJDHBOIBBC.MissingLegacyData) on any entry without a `LegacyData` object - which
            // then blanks the whole page. Serializing the STRUCTURED avatar_data_saved (which has
            // no LegacyData field) is exactly that failure. So project each outfit into the shape
            // the client expects:
            //   - Preferred: round-trip the RAW document the client posted (SaveOutfit stores it in
            //     .Avatar); it already carries LegacyData/Selections/DataVersion verbatim.
            //   - Fallback (older saves / server-made presets with no raw body): synthesize the
            //     LegacyData envelope from the flat fields so LegacyData is never null.
            var arr = new Newtonsoft.Json.Linq.JArray();
            foreach (var o in saved)
            {
                Newtonsoft.Json.Linq.JObject? raw = null;
                if (!string.IsNullOrWhiteSpace(o.Avatar))
                {
                    try { raw = Newtonsoft.Json.Linq.JObject.Parse(o.Avatar); } catch { raw = null; }
                }

                if (raw != null && raw["LegacyData"] != null &&
                    raw["LegacyData"]!.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                {
                    // Slot/Name are the authority for slot overwrite - keep them in sync with the
                    // structured fields in case the stored raw body predated them.
                    raw["Slot"] = (long)o.Slot;
                    if (o.Name != null) raw["Name"] = o.Name;
                    arr.Add(raw);
                    continue;
                }

                arr.Add(new Newtonsoft.Json.Linq.JObject
                {
                    ["LegacyData"] = new Newtonsoft.Json.Linq.JObject
                    {
                        ["SelectionsV1"] = o.OutfitSelections,
                        ["SelectionsV2"] = o.OutfitSelectionsV2,
                        ["FaceFeatures"] = o.FaceFeatures,
                        ["SkinColor"]    = o.SkinColor,
                        ["HairColor"]    = o.HairColor
                    },
                    ["Selections"] = new Newtonsoft.Json.Linq.JArray(),
                    ["DataVersion"] = 9,
                    ["CustomizationSettings"] = (string?)null,
                    ["ThumbnailFileName"] = o.ImageName ?? o.PreviewImageName,
                    ["Name"] = o.Name,
                    ["Accessibility"] = 0,
                    ["Slot"] = (long)o.Slot
                });
            }
            return arr.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string SetAvatarSaved(ulong playerid, List<avatar_data_saved> avatars)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");

            var player = col.FindOne(x => x.playerid == playerid);
            // Was: `player == null` only - player_Extra was dereferenced unchecked.
            if (player?.player_Extra == null)
                return "[]";

            if (player.player_Extra.SavedOutfits == null)
                player.player_Extra.SavedOutfits = new List<avatar_data_saved>();

            var incomingSlots = avatars.Select(a => a.Slot).ToHashSet();
            player.player_Extra.SavedOutfits.RemoveAll(a => incomingSlots.Contains(a.Slot));

            player.player_Extra.SavedOutfits.AddRange(avatars);

            col.Update(player);

            return JsonConvert.SerializeObject(player.player_Extra.SavedOutfits);
        }

        /// <summary>May this account wear the Rec Room Developer cheer badge (9000)? Same rule that grants the developer role:
        /// the dev or owner flag. Nobody else, whatever the client posts.</summary>
        public static bool CanHoldDeveloperBadge(ulong id)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == id);
            return player != null && (player.dev_Flag & (DevFlag.dev | DevFlag.owner)) != 0;
        }

        /// <summary>The cheer badges a normal player may hold: none (-1) or one of the five real cheers (0, 10, 20, 30, 40).
        /// A developer (dev or owner flag) may hold ANY badge value, including the developer badge and custom numbers.</summary>
        public static bool MaySelectCheer(CheerCategoryEnum cheer, ulong id)
        {
            if (CanHoldDeveloperBadge(id)) return true;                                   // devs: any badge
            return cheer is CheerCategoryEnum.None or CheerCategoryEnum.General or CheerCategoryEnum.Helpful
                         or CheerCategoryEnum.Sportmanship or CheerCategoryEnum.GreatHost or CheerCategoryEnum.Creative;
        }

        /// <summary>The badge to SHOW for this account: what is stored if they may hold it, otherwise none. A non-dev never displays a
        /// badge they are not allowed (even a stored one, e.g. after their dev flag was removed).</summary>
        public static CheerCategoryEnum EffectiveSelectedCheer(CheerCategoryEnum stored, ulong id) =>
            MaySelectCheer(stored, id) ? stored : CheerCategoryEnum.None;

        public static void SetPlayerCheer(CheerCategoryEnum cheer = CheerCategoryEnum.None, ulong playerid = 0)
        {
            if (!MaySelectCheer(cheer, playerid))
            {
                Console.WriteLine($"[cheer] REFUSED selected cheer {(int)cheer} for account {playerid} (not a valid badge, or the developer badge on a non-dev)");
                return;
            }
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player.PlayerReputation != null)
                {
                    player_Setting1.player.PlayerReputation.SelectedCheer = cheer;
                    col.Update(player_Setting1);
                    return;
                }
            }
        }




        public static CheerCategoryEnum? GetPlayerCheer(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player_Setting1 = col.FindOne(x => x.playerid == playerid);

            if (player_Setting1 != null)
            {
                if (player_Setting1.player.PlayerReputation != null)
                {
                    return player_Setting1.player.PlayerReputation.SelectedCheer;
                }
            }

            return null;
        }

        public static List<ulong> GetInfluencerIds()
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var result = col.Query().Where(x => x.player_Extra.Influencer.isInfluencer == true).Select(x => x.playerid).ToList();
            return result;
        }

        public static player_data? GetInfluencerByCreatorCode(string creatorcode)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.player_Extra.Influencer.creatorCode == creatorcode && x.player_Extra.Influencer.isInfluencer == true);
            return player;
        }

        public static bool IsInfluencer(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            return player?.player_Extra?.Influencer?.isInfluencer == true;
        }

        public static void SetIsInfluencer(ulong playerid, bool IsInfluencer)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);

            if (player == null)
                return;

            if (player.player_Extra == null)
                player.player_Extra = new player_extra();

            if (player.player_Extra.Influencer == null)
                player.player_Extra.Influencer = new Influencer();

            player.player_Extra.Influencer.isInfluencer = IsInfluencer;

            col.Update(player);
        }

        public static void SetCreatorCode(ulong playerid, string creatorcode)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);

            if (player == null)
                return;

            if (player.player_Extra == null)
                player.player_Extra = new player_extra();

            if (player.player_Extra.Influencer == null)
                player.player_Extra.Influencer = new Influencer();

            player.player_Extra.Influencer.creatorCode = creatorcode;

            col.Update(player);
        }

        public static void SupportInfluencer(ulong requestplayerid, ulong SupportId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == requestplayerid);
            if (player != null)
            {
                if (player.player_Extra.Influencer == null)
                {
                    player.player_Extra.Influencer = new Influencer();
                }

                player.player_Extra.Influencer.SupportingInfluencer = SupportId;
                col.Update(player);
                return;
            }
        }

        public static ulong? GetMyInfluencer(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);

            if (player != null)
            {
                if (player.player_Extra.Influencer != null)
                {
                    return player.player_Extra.Influencer.SupportingInfluencer;
                }
            }

            return 0;
        }

        // message funcs

        public static List<MessageData> GetMessages(ulong playerId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerId);

            if (player == null)
                return new List<MessageData>();

            if (player.player_Extra.Messages == null)
                player.player_Extra.Messages = new List<MessageData>();

            return player.player_Extra.Messages;
        }

        public static MessageData? AddMessage(ulong toPlayerId, ulong fromPlayerId, WebsocketEvents.MessageType type, string? data, ulong? roomId = null, ulong? playerEventId = null)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == toPlayerId);

            if (player == null)
                return null;

            if (player.player_Extra.Messages == null)
                player.player_Extra.Messages = new List<MessageData>();

            var msg = new MessageData()
            {
                Id = GenerateRandomMessageId(),
                FromPlayerId = fromPlayerId,
                SentTime = DateTime.UtcNow,
                Type = type,
                Data = data,
                RoomId = roomId,
                PlayerEventId = playerEventId
            };

            player.player_Extra.Messages.Add(msg);
            col.Update(player);

            // Nudge the recipient's open website tabs to re-fetch the inbox (cheers, coach
            // messages, ...). No-op if they have no browser tab open.
            Campus_live_rewrite.api.server.SiteNotifications.Notify(toPlayerId);

            return msg;
        }

        public static void DeleteMessage(ulong playerId, ulong messageId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerId);

            if (player == null)
                return;

            if (player.player_Extra.Messages == null || player.player_Extra.Messages.Count == 0)
                return;

            var message = player.player_Extra.Messages.FirstOrDefault(m => m.Id == messageId);

            if (message == null)
                return;

            player.player_Extra.Messages.Remove(message);

            // Save changes
            col.Update(player);
        }


        /// <summary>
        /// Remove a player's room-role notifications (co-owner / contributor invite, "you are added
        /// as", "you were removed as") for one room, and tell their client to drop them (MessageDeleted
        /// frame). Called once the invite is answered, withdrawn or no longer applies - the client
        /// never removes these itself, so an answered invite used to sit in the list for good.
        /// Returns how many were removed.
        /// </summary>
        public static int DeleteRoomRoleMessages(ulong playerId, ulong roomId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerId);
            var list = player?.player_Extra?.Messages;
            if (list == null || list.Count == 0) return 0;

            var gone = list.Where(m => m.RoomId == roomId
                                       && (m.Type == WebsocketEvents.MessageType.RoomCoOwnerInvited
                                           || m.Type == WebsocketEvents.MessageType.RoomCoOwnerAdded
                                           || m.Type == WebsocketEvents.MessageType.RoomCoOwnerRemoved)).ToList();
            if (gone.Count == 0) return 0;
            foreach (var m in gone) list.Remove(m);
            col.Update(player);

            foreach (var m in gone)
                Campus_live_rewrite.api.server.Notifications.FireAndForget(
                    Campus_live_rewrite.api.server.Notifications.SendToPlayer(playerId,
                        Newtonsoft.Json.JsonConvert.SerializeObject(WebsocketEvents.CreateMessageDeleted(m.Id))),
                    "role notice delete");
            Campus_live_rewrite.api.server.SiteNotifications.Notify(playerId);
            return gone.Count;
        }

        public static ulong GenerateRandomMessageId()
        {
            lock (randLock)
            {
                byte[] buffer = new byte[8];
                random.NextBytes(buffer);
                return BitConverter.ToUInt64(buffer, 0);
            }
        }
        // message funcs

        /// <summary>The client's CheerCategory values: 0 General, 10 Helpful, 20 Sportsman, 30 GreatHost, 40 Creative.</summary>
        public static bool IsCheerCategory(int category) => category is 0 or 10 or 20 or 30 or 40;

        /// <summary>
        /// Count one cheer on the CHEERED player's record, in its category. This never happened
        /// before: a cheer spent the giver's credit and played on the receiver's client, but their
        /// counters stayed at the 1 every account starts with. Reference: recflare api addCheer.
        /// </summary>
        public static bool AddCheer(ulong playerid, int category)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var p = col.FindOne(x => x.playerid == playerid);
            var r = p?.player?.PlayerReputation;
            if (r == null) return false;
            switch (category)
            {
                case 0: r.CheerGeneral++; break;
                case 10: r.CheerHelpful++; break;
                case 20: r.CheerSportsman++; break;
                case 30: r.CheerGreatHost++; break;
                case 40: r.CheerCreative++; break;
                default: return false;
            }
            col.Update(p);
            return true;
        }

        public static int SetCheerCredit(int cheerCredit, ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player_Setting1 = col.FindOne(x => x.playerid == playerid);
            if (player_Setting1 != null)
            {
                if (player_Setting1.player != null)
                {
                    player_Setting1.player.PlayerReputation.CheerCredit = cheerCredit;
                    col.Update(player_Setting1);
                    return cheerCredit;
                }
            }
            return 0;
        }

        public static void Setup()
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            col.EnsureIndex(x => x.playerid, true);
            col.EnsureIndex(x => x.platform);
            col.EnsureIndex(x => x.player.PlatformIds);
            col.EnsureIndex(x => x.player.Username);
            col.EnsureIndex(x => x.deviceId);
            col.EnsureIndex(x => x.authtoken);
            col.EnsureIndex(x => x.id);
            col.EnsureIndex(x => x.player_Extra.Messages);
            col.EnsureIndex("infl", "playerExtra.Influencer.isInfluencer");
        }

        public static string? GetPlayerData(ulong playerId, ulong roomId)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerId);

            if (player?.player_Extra?.MyRoomData?.PlayerData == null)
                return null;

            var entry = player.player_Extra.MyRoomData.PlayerData.FirstOrDefault(p => p.RoomId == roomId);
            
            return entry?.Data;
        }

        public static bool SetPlayerData(ulong playerId, ulong roomId, string data)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerId);

            if (player == null) 
                return false;

            if (player.player_Extra == null)
                player.player_Extra = new player_extra();

            if (player.player_Extra.MyRoomData == null)
                player.player_Extra.MyRoomData = new MyRoomData();

            if (player.player_Extra.MyRoomData.PlayerData == null) 
                player.player_Extra.MyRoomData.PlayerData = new List<PlayerData>();

            var existingEntry = player.player_Extra.MyRoomData.PlayerData.FirstOrDefault(p => p.RoomId == roomId);

            if (existingEntry != null)
            {
                existingEntry.Data = data;
            }
            else
            {
                player.player_Extra.MyRoomData.PlayerData.Add(new PlayerData 
                { 
                    RoomId = roomId, 
                    Data = data 
                });
            }

            return col.Update(player);
        }

        public static PlayerPhotoTaggingSetting GetPlayerPhotoTaggingSetting(ulong playerid)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player != null)
            {
                if (player.player != null)
                {
                    return player.player_Extra.PlayerPhotoTaggingSetting;
                }
            }
            return PlayerPhotoTaggingSetting.Anyone;
        }

        public static bool SetPlayerPhotoTaggingSetting(ulong playerid, PlayerPhotoTaggingSetting playerPhotoTaggingSetting)
        {
            var col = PlayerDBFile.GetCollection<player_data>("players");
            var player = col.FindOne(x => x.playerid == playerid);
            if (player != null)
            {
                if (player.player != null)
                {
                    player.player_Extra.PlayerPhotoTaggingSetting = playerPhotoTaggingSetting;
                    col.Update(player);
                    return true;
                }
            }
            return false;
        }
    }
}
