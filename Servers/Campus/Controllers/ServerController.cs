using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using System.ComponentModel.DataAnnotations;
using System.Text;
using static Campus_live_rewrite.api.dbs.HeartbeatDB;
using static Campus_live_rewrite.api.server.Classes.db_class.LeaderboardDBClasses;
using static Campus_live_rewrite.api.server.Images;

namespace Campus_live_rewrite.Controllers
{
    [Route("server")]
    public class PlayerSettingsController : ControllerBase
    {
        [HttpGet("PlayerSettings/playersettings")]
        public IActionResult GetPlayerSettings()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var settings = SettingsDB.LoadSettings(account.Id);
            if (ConfigCampus.LocalDev)
            {
                // what the client sees for the onboarding keys, to line up against the PUTs it makes straight after
                var ob = settings.Where(s => s.Key.StartsWith("Recroom.AccountCreation", StringComparison.OrdinalIgnoreCase) || s.Key == "Recroom.OOBE" || s.Key.StartsWith("TUTORIAL", StringComparison.OrdinalIgnoreCase))
                                 .Select(s => s.Key.Replace("Recroom.", "") + "=" + s.Value);
                Console.WriteLine($"[settings] {account.Id} GET playersettings ({settings.Count} keys): {string.Join(", ", ob)}");
            }
            // Was: Ok(<string>). ASP.NET's StringOutputFormatter writes a raw string as
            // Content-Type: text/plain, so these JSON payloads were served mislabelled
            // and the client would not parse them. Verified live: /outfits/me returned
            // text/plain while /api/avatar/v2 returned application/json. Same bytes,
            // correct content type.
            return new ContentResult { Content = JsonConvert.SerializeObject(settings), ContentType = "application/json", StatusCode = 200 };
        }

        [HttpPut("PlayerSettings/playersettings")]
        public IActionResult UpdatePlayerSetting([FromForm, Required] string key, [FromForm, Required] string value)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (string.IsNullOrWhiteSpace(key))
                return BadRequest("");

            if (NewPlayerBootstrap.ShouldIgnoreCreationPut(account.Id, key, value ?? ""))
            {
                Console.WriteLine($"[settings] {account.Id} PUT {key} = {value} IGNORED (new account is held in account creation)");
                return Ok(new { success = true, errorId = (string?)null, error = (string?)null });
            }
            SettingsDB.SetPlayerSetting(key, value ?? "", account.Id);
            // Account-creation / tutorial keys are logged: they are what the client's onboarding
            // branches on, and a client that flips them itself is otherwise invisible.
            if (key.StartsWith("Recroom.AccountCreation", StringComparison.OrdinalIgnoreCase) || key.StartsWith("TUTORIAL", StringComparison.OrdinalIgnoreCase) || key.Contains("ORIENTATION", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"[settings] {account.Id} PUT {key} = {value}");

            return Ok(new 
            {
                success = true,
                errorId = (string?)null,
                error = (string?)null
            });
        }

        [HttpDelete("PlayerSettings/playersettings")]
        public IActionResult UpdatePlayerSetting([FromForm, Required] string key)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (string.IsNullOrWhiteSpace(key))
                return BadRequest("");

            SettingsDB.DeletePlayerKey(key ?? "", account.Id);

            return Ok(new
            {
                success = true,
                errorId = (string?)null,
                error = (string?)null
            });
        }


        [HttpPost("Storage/upload")]
        public async Task<ContentResult> server_storage_upload([FromForm] FileUploadModel model)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var ua = Request.Headers["User-Agent"].ToString();

            if (ua.Contains("PostmanRuntime"))
            {
                return new ContentResult
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 403
                };
            }

            if (PlayerDB.HasActiveModerationBlock(account.Id))
            {
                return new ContentResult
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 403
                };
            }

            try
            {
                if (model?.File == null || model.File.Length <= 0)
                {
                    return new ContentResult()
                    {
                        Content = "",
                        ContentType = "application/json",
                        StatusCode = 400
                    };
                }
                byte[] array = Array.Empty<byte>();
                MemoryStream stream = new MemoryStream();
                await model.File.CopyToAsync(stream);
                array = stream.ToArray();

                FileType_data data_type = model.FileType;

                // Only files the game itself produced get stored (see UploadGuard).
                var refused = UploadGuard.Gate(HttpContext, account, UploadGuard.KindOf(data_type), array, $"Storage/upload:{data_type}");
                if (refused != null) return refused;

                try
                {
                    var fileResult = Images.SaveFileUnifiedv2(array, data_type);

                    if (fileResult != null && fileResult.saved)
                    {
                        if (fileResult.FileType == FileType_data.Image)
                        {
                            _ = Task.Run(() => Webhook.Send(
                            account.Id, "prod", "Server info",
                            $"({account.Id}) @{account.Username} has uploaded an polaroid image",
                            5898927, $"https://camprec.net/img/{fileResult.rawfilename}"
                        ));
                        }
                        return new ContentResult()
                        {
                            Content = JsonConvert.SerializeObject(new
                            {
                                filename = fileResult.FileName,
                                Hash = "e", // Not needed for game, game expect it so hardcode 
                                OwnershipProof = "e" // Not needed for game, game expect it so hardcode 

                            }, Formatting.Indented),
                            ContentType = "application/json",
                            StatusCode = 200
                        };
                    }

                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Failed to upload!",
                            value = (string?)null
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };

                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            filename = (string?)null,
                            Hash = (string?)null,
                            OwnershipProof = (string?)null,
                        }, Formatting.Indented),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        filename = (string?)null,
                        Hash = (string?)null,
                        OwnershipProof = (string?)null,
                    }, Formatting.Indented),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
        }

        // A defunct limited-time contest's hit recorder (Glitch City grenade event). Was a live
        // 404; nothing tracks the contest on this server, so ack with an empty object rather than
        // 404 (the leaderboard/room UI that pings it shouldn't error).
        [HttpPost("Leaderboard/{contest}/RecordHits")]
        public ContentResult ContestRecordHits(string contest) => new ContentResult
        {
            Content = "{}",
            ContentType = "application/json",
            StatusCode = 200
        };

        // Every leaderboard read is per-player and rank is computed at read time, so no response
        // may ever be served from a cache - stamped on all three read routes.
        private void NoCache()
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
        }

        // A board read's body, taken field by field and LENIENTLY (the reference's int(x, default)).
        // The strict typed deserialise threw on any field it didn't like - a null or negative
        // PlayerId, a number sent as a string - and the catch answered an EMPTY board, which the
        // client draws as a board with nobody on it. Unreadable fields now fall back to defaults.
        private sealed class BoardRead
        {
            public ulong PlayerId; public int StatChannel; public ulong RoomId; public int FilterType;
            public bool SortAscending; public int WindowSize = 10; public int RankStart; public int RankEnd = 9;
            public string Raw = "";
        }

        private static BoardRead ParseBoardRead(string body)
        {
            var r = new BoardRead { Raw = body.Length > 300 ? body.Substring(0, 300) : body };
            Newtonsoft.Json.Linq.JObject o;
            try { o = Newtonsoft.Json.Linq.JObject.Parse(body); } catch { return r; }
            long N(string key, long dflt)
            {
                var t = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
                if (t == null || t.Type == Newtonsoft.Json.Linq.JTokenType.Null) return dflt;
                if (t.Type == Newtonsoft.Json.Linq.JTokenType.Boolean) return (bool)t ? 1 : 0;
                string v = t.ToString();
                if (bool.TryParse(v, out bool flag)) return flag ? 1 : 0;
                return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)d : dflt;
            }
            long pid = N("PlayerId", 0), room = N("RoomId", 0);
            r.PlayerId = pid > 0 ? (ulong)pid : 0;
            r.RoomId = room > 0 ? (ulong)room : 0;
            r.StatChannel = (int)N("StatChannel", 0);
            r.FilterType = (int)N("FilterType", 0);
            r.SortAscending = N("SortAscending", 0) != 0;
            r.WindowSize = (int)Math.Clamp(N("WindowSize", 10), 0, 50);
            r.RankStart = (int)Math.Max(N("RankStart", 0), 0);
            r.RankEnd = (int)Math.Min(N("RankEnd", 9), r.RankStart + 99L);
            return r;
        }

        // FilterType 1 = Friends: the viewer and their friends, ranked among themselves (the
        // reference's friendsOf). Any other filter (Global 0, Champions 2) reads the whole board.
        private static HashSet<int>? FriendsFilter(BoardRead r)
        {
            if (r.FilterType != 1 || r.PlayerId == 0) return null;
            var set = new HashSet<int> { (int)r.PlayerId };
            try
            {
                foreach (var rel in FriendsDB.GetRelationships(r.PlayerId) ?? new())
                    if (rel.RelationshipType == Campus_live_rewrite.api.server.Classes.db_class.RelationshipType.Friend)
                        set.Add((int)rel.PlayerID);
            }
            catch { }
            return set;
        }

        // Every read made from a race-time room (or asking about one) is logged with its body and
        // what it got, so an empty in-game board can be matched to the read that fed it.
        private static void LogBoardRead(string what, ulong caller, BoardRead r, List<Entry> rows)
        {
            bool raceRoom = ConfigCampus.LeaderboardLowestWinsRooms.Contains(r.RoomId);
            if (!raceRoom)
            {
                var here = HeartbeatDB.GetPlayerHeartbeat(caller)?.roomInstance;
                if (here == null || !ConfigCampus.LeaderboardLowestWinsRooms.Contains((ulong)here.roomId)) return;
            }
            string top = rows.Count == 0 ? "" : " [" + string.Join(", ", rows.Take(3).Select(x => $"#{x.Rank + 1} p{x.PlayerId} {x.Score}")) + (rows.Count > 3 ? ", ..." : "") + "]";
            Console.WriteLine($"[leaderboard] room {r.RoomId} sc{r.StatChannel} {what} by {caller} filter {r.FilterType} -> {rows.Count} row(s){top} body {r.Raw}");
        }

        [HttpPost("Leaderboard/leaderboard/GetNearbyScores")]
        public async Task<ContentResult> GetNearbyScores()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            NoCache();

            string body = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var rows = new List<Entry>();
            try
            {
                var r = ParseBoardRead(body);
                // No PlayerId in the body: centre on the caller.
                if (r.PlayerId == 0) r.PlayerId = account.Id;
                rows = LeaderboardDB.GetNearbyScores(r.PlayerId, r.StatChannel, r.RoomId, r.WindowSize, r.SortAscending, FriendsFilter(r));
                LogBoardRead("GetNearbyScores", account.Id, r, rows);
            }
            catch (Exception ex) { Console.WriteLine($"[leaderboard] GetNearbyScores failed: {ex.Message} body {body}"); }

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new { Rows = rows }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        // One line per board the first time an unranked player asks about it - enough to see a
        // board's RoomId / StatChannel / sort direction without logging every read.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _unrankedSeen = new();
        private static void LogUnranked(GetNearbyScoresRequest req, int score)
        {
            string key = $"{req.RoomId}/{req.StatChannel}/{req.SortAscending}";
            if (_unrankedSeen.TryAdd(key, true))
                Console.WriteLine($"[leaderboard] unranked read on room {req.RoomId} sc{req.StatChannel} ascending={req.SortAscending} -> Score {score}");
        }

        [HttpPost("Leaderboard/leaderboard/GetPlayerRank")]
        public async Task<ContentResult> GetPlayerRank()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            NoCache();

            string body = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            try
            {
                var read = ParseBoardRead(body);
                if (read.PlayerId == 0) read.PlayerId = account.Id;
                var req = new GetNearbyScoresRequest
                {
                    PlayerId = read.PlayerId, StatChannel = read.StatChannel, RoomId = read.RoomId,
                    SortAscending = read.SortAscending, WindowSize = read.WindowSize
                };

                var rank = LeaderboardDB.GetPlayerRank(
                    req.PlayerId,
                    req.StatChannel,
                    req.RoomId,
                    req.SortAscending,
                    FriendsFilter(read)
                );

                bool raceTimes = ConfigCampus.LeaderboardLowestWinsRooms.Contains(req.RoomId);
                if (raceTimes)
                    Console.WriteLine($"[leaderboard] room {req.RoomId} sc{req.StatChannel} GetPlayerRank p{req.PlayerId} -> {(rank == null ? "no time yet (answered worst time)" : $"{rank.Score} ms, rank {rank.Rank}")}");

                if (rank == null && raceTimes)
                {
                    // Race-time board, no time stored: the WORST possible time. Two answers failed live:
                    //  * Score 0 - read as a stored 0 ms best; no run beats it, so no time was written;
                    //  * JSON null - the client's HTTP layer throws "Response was empty", the read
                    //    fails, and again nothing is written.
                    // int.MaxValue is beaten by any real run (so the first finish is written and is a
                    // personal best), and the game's own best-time code keeps any value > 0 as-is.
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new { PlayerId = req.PlayerId, Score = int.MaxValue, Rank = 99999 }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                if (rank == null)
                {
                    // No row on this board = UNRANKED. The client expects NUMERIC sentinels, not
                    // null: Rank 99999 (a big number, since 0 is a real rank = first place) and a
                    // Score that ANY real result beats. Echo the BODY's PlayerId - the client matches
                    // the answer to the player it asked about.
                    //
                    // The Score is where the reference (Score 0) is wrong for half the boards. The
                    // client writes a result only when it beats the Score read here, so:
                    //   * higher-is-better (SortAscending false): 0 - any positive score beats it;
                    //   * lower-is-better  (SortAscending true, race times): int.MaxValue. With 0,
                    //     "0 seconds" can never be beaten, so NO time was ever stored - Stunt Runner
                    //     had zero rows on the server, an empty board, and a "personal best" banner
                    //     the room worked out from the run alone.
                    int unrankedScore = req.SortAscending ? int.MaxValue : 0;
                    LogUnranked(req, unrankedScore);
                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            PlayerId = req.PlayerId,
                            Score = unrankedScore,
                            Rank = 99999,
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(rank),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        PlayerId = account.Id,
                        Score = 0,
                        Rank = 99999,
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
        }


        [HttpPost("Leaderboard/leaderboard/CheckAndSetStat")]
        public async Task<ContentResult> CheckAndSetStat()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string body = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            try
            {
                var req = JsonConvert.DeserializeObject<CheckAndSetStatRequest>(body);
                if (req == null)
                    throw new Exception("Invalid");

                var room = RoomDB.GetRoom(req.RoomId);

                if (ConfigCampus.LeaderboardLowestWinsRooms.Contains(req.RoomId))
                {
                    Console.WriteLine($"[leaderboard] room {req.RoomId} sc{req.StatChannel} CheckAndSetStat p{account.Id} time {req.StatValue} ms (client thinks stored: {(req.CurrentStatValue?.ToString() ?? "none")})");
                    if (req.StatValue < ConfigCampus.LeaderboardMinTimeMs)
                    {
                        Campus_live_rewrite.api.server.LeaderboardGuard.Refuse(account.Id, account.Username ?? "", req.StatChannel, req.RoomId, room?.Name ?? "?", req.StatValue,
                            Campus_live_rewrite.api.server.LeaderboardGuard.Verdict.TooFast, $"a {req.StatValue} ms run is under the {ConfigCampus.LeaderboardMinTimeMs} ms minimum");
                        return new ContentResult() { Content = "0", ContentType = "application/json", StatusCode = 200 };
                    }
                    // Times aren't points: the gain caps below don't apply, and SetStat keeps only
                    // an improvement. No currency payout either (a time isn't a score gain).
                    LeaderboardDB.SetStat(account.Id, req.StatChannel, req.RoomId, req.StatValue, req.CurrentStatValue);
                    // The overall board is the sum of the player's best course times, not one
                    // event's total - recomputed after every course (or overall) save.
                    LeaderboardDB.RecomputeTotal(account.Id, req.RoomId);
                    return new ContentResult() { Content = "0", ContentType = "application/json", StatusCode = 200 };
                }

                // Plausibility gate, Rec Room Original rooms only (LeaderboardGuard): a score that
                // jumps a thousand in a second is a hacked client, not a game. Refused writes are
                // not stored, are logged, and land in the admin panel's Reports as Cheating. The
                // answer is still the bare 0 the client expects - a refusal must not read as a
                // broken leaderboard.
                var stored = LeaderboardDB.GetEntry(account.Id, req.StatChannel, req.RoomId);
                var verdict = Campus_live_rewrite.api.server.LeaderboardGuard.Check(account.Id, req.StatChannel, req.RoomId, req.StatValue, stored, out string why);
                if (verdict == Campus_live_rewrite.api.server.LeaderboardGuard.Verdict.TooFast
                    || verdict == Campus_live_rewrite.api.server.LeaderboardGuard.Verdict.TooMuchToday)
                {
                    Campus_live_rewrite.api.server.LeaderboardGuard.Refuse(account.Id, account.Username ?? "", req.StatChannel, req.RoomId, room?.Name ?? "?", req.StatValue, verdict, why);
                    return new ContentResult() { Content = "0", ContentType = "application/json", StatusCode = 200 };
                }

                // Compare-and-set on the caller's own row: write StatValue only if the stored value
                // still equals CurrentStatValue (null = write unconditionally).
                LeaderboardDB.SetStat(
                    account.Id,
                    req.StatChannel,
                    req.RoomId,
                    req.StatValue,
                    req.CurrentStatValue
                );

                // The score pays out (ConfigCampus.LeaderboardPaysRoomCurrency): the gain over the
                // stored score, in the currency of the room the player is standing in - only a
                // balance room (Crescendo -> Silver, Isle -> Gold) pays, and only what the guard
                // above accepted. A run that does not beat the stored score pays nothing.
                if (ConfigCampus.LeaderboardPaysRoomCurrency)
                {
                    long gain = (long)req.StatValue - (stored?.Score ?? 0);
                    var live = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance;
                    var cur = Campus_live_rewrite.api.server.Classes.GameCurrency.ForBalanceRoom(live?.name);
                    if (gain > 0 && cur != null && live != null && (ulong)live.roomId == req.RoomId
                        && Campus_live_rewrite.api.server.Classes.GameCurrency.IsPlayerGrantable((int)cur.Value))
                    {
                        long total = PlayerDB.AddBalance(account.Id, (int)cur.Value, gain);
                        Campus_live_rewrite.api.server.GiftBoxes.PushBalance(account.Id, (int)cur.Value, total, "leaderboard");
                        Console.WriteLine($"[leaderboard] {account.Id} scored {req.StatValue} (was {stored?.Score ?? 0}) in '{room?.Name}' -> +{gain} {cur} = {total}");
                    }
                }

                Webhook.Send(
                    account.Id,
                    "prod",
                    "Leaderboard Info",
                    $"Player @{account.Username} ({account.Id}) has updated their score to {req.StatValue} in {(room != null ? room.Name : "Unknown Room")}",
                    5898927
                );

                // The live service answers the BARE number 0 here (not an envelope, not the result
                // enum) whether or not the compare passed - that's what the client's parser expects.
                return new ContentResult()
                {
                    Content = "0",
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                // Still answer a bare 0 - the client's parser expects a number here, and a 400 would
                // read as a failed stat write / broken leaderboard.
                return new ContentResult()
                {
                    Content = "0",
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
        }


        [HttpPost("Leaderboard/leaderboard/GetRanks")]
        public async Task<ContentResult> GetRanks()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            NoCache();

            string body = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var rows = new List<Entry>();
            try
            {
                var r = ParseBoardRead(body);
                // A friends slice needs a viewer; the body's, else the caller.
                if (r.PlayerId == 0 && r.FilterType == 1) r.PlayerId = account.Id;
                rows = LeaderboardDB.GetRanks(r.StatChannel, r.RoomId, r.RankStart, r.RankEnd, r.SortAscending, FriendsFilter(r));
                LogBoardRead("GetRanks", account.Id, r, rows);
            }
            catch (Exception ex) { Console.WriteLine($"[leaderboard] GetRanks failed: {ex.Message} body {body}"); }

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new { Rows = rows }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

    }
}
