using Microsoft.AspNetCore.Mvc;
using Campus_live_rewrite.api.dbs;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server.Classes.db_class;
using static Campus_live_rewrite.api.dbs.HeartbeatDB;
using Campus_live_rewrite.api.server;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Campus_live_rewrite.api;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static Campus_live_rewrite.api.server.Classes.RRPlus;
using Discord.Net;
using System.Net.Http.Headers;

namespace Campus_live_rewrite.Controllers
{
    [Route("Matchmaking")]
    public class MatchmakingController : ControllerBase
    {
        public RoomInstance DefaultSessionDorm = new RoomInstance
        {
            isFull = false,
            isInProgress = false,
            isPrivate = true,
            location = "76d98498-60a1-430c-ab76-b54a29b7a163",
            maxCapacity = 4,
            name = "^" + "DormRoom",
            roomId = 1,
            roomInstanceId = 1,
            roomInstanceType = 0,
            voiceAuthId = null,
            voiceServerId = null,
            subRoomId = 1,
            matchmakingPolicy = 0,
        };


        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTime> _lastOnlineNotice = new();
        private static bool FriendOnlineNoticeDue(ulong accountId)
        {
            var now = DateTime.UtcNow;
            if (_lastOnlineNotice.TryGetValue(accountId, out var last)
                && (now - last).TotalMinutes < ConfigCampus.FriendOnlineNoticeCooldownMinutes)
                return false;
            _lastOnlineNotice[accountId] = now;
            return true;
        }

        // The most-populated clubhouse rooms. BARE ARRAY of {RoomId, ClubId, PlayerCount} - a {}
        // raises the client's Malformed Response throw (wire-shapes: clubhousesearch-mostactivenow).
        // ClubId is a plain long: send 0, never null. Was a 404.
        [HttpGet("clubhousesearch/mostactivenow")]
        public IActionResult MostActiveClubhousesNow()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var rows = HeartbeatDB.LivePlayerCountsByRoom()
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .Take(20)
                .Select(kv => new { RoomId = kv.Key, ClubId = 0L, PlayerCount = kv.Value })
                .ToList();
            return Ok(rows);
        }

        [HttpPost("player/login")]
        [HttpPost("player/exclusivelogin")]
        public async Task<ContentResult> PlayerLogin()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Daily login streak: credited once per day, before the friend-notice cooldown below can return early.
            LoginStreaks.OnLogin((ulong)account.Id);

            // The client posts this on every (re)connect - both /login and /exclusivelogin, and
            // again after a Photon reconnect - so friends used to get "X is online" two or three
            // times per session. One notice per player per ConfigCampus.FriendOnlineNoticeCooldownMinutes.
            if (!FriendOnlineNoticeDue(account.Id))
            {
                return new ContentResult() { Content = "", ContentType = "application/json", StatusCode = 200 };
            }

            var relationships = FriendsDB.GetRelationships(account.Id);

            List<ulong> mutualFriendIds = relationships.Where(r => r.RelationshipType == RelationshipType.Friend).Select(r => r.PlayerID).Distinct().ToList();

            var message = new MessageData
            {
                FromPlayerId = (ulong)account.Id,
                Id = (ulong)new Random().Next(),
                SentTime = DateTime.UtcNow,
                Type = WebsocketEvents.MessageType.FriendStatusOnline,
                // Data is parsed as a JSON OBJECT by the client's notification decoder: the bare
                // word "Online" logged "expected:'{', actual:'O', at offset:0" on every friend
                // login and the frame was dropped.
                Data = "{}",
                RoomId = 1
            };

            var toSkip = new List<ulong>();
            foreach (var friendId in mutualFriendIds)
            {
                if (SettingsDB.GetPlayerSettingValue("ReceiveFriendOnlineStatus", friendId) == "False" || DoNotDisturb.Active(friendId))
                {
                    toSkip.Add(friendId);
                }
            }
            // Was: mutualFriendIds.Remove(friendId) INSIDE the foreach over
            // mutualFriendIds - modifying a list while enumerating it throws
            // InvalidOperationException ("Collection was modified"). It only fired
            // when a friend actually had the setting off, so it lurked. Collect
            // first, remove after.
            mutualFriendIds = mutualFriendIds.Where(id => !toSkip.Contains(id)).ToList();

            var ws_message = JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(message));

            await Notifications.SendToPlayers(mutualFriendIds, ws_message);

            return new ContentResult()
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [Route("player/logout")]
        public ContentResult PlayerLogout()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // The account-creation bootstrap fires a spurious logout right after a new player is
            // seeded into Orientation (instance -2); clearing it would bounce them to the dorm and
            // skip the identity flow. A logout that still points at Orientation is a no-op ack
            // (reference: match /player/logout).
            if (Campus_live_rewrite.api.server.NewPlayerBootstrap.IsInOrientation(account.Id))
            {
                Console.WriteLine($"[logout] {account.Id}: in Orientation - presence kept");
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 200 };
            }
            // Quitting the game leaves the party for real - the party thread is otherwise left
            // holding a player who is not online, which is what made the member count read 2 when
            // only one person was there.
            try
            {
                foreach (var partyId in Campus_live_rewrite.api.dbs.ChatDB.PartyThreadIdsFor(account.Id))
                {
                    Campus_live_rewrite.api.dbs.ChatDB.SystemNotice(partyId, $"Player <@U{account.Id}> left");
                    Campus_live_rewrite.api.dbs.ChatDB.RemoveMember(partyId, account.Id);
                    Console.WriteLine($"[party] {account.Id} logged out - removed from party thread {partyId}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[party] logout leave failed: {ex.Message}"); }

            HeartbeatDB.UpdatePlayerHeartbeat(account.Id, null, online: false);
            Webhook.Send(account.Id,
                   "prod",
                   "Matchmaking info",
                   $"({account.Id}) @{account.Username} {account.DisplayName} is logging out of **Campus**",
                   5898927
               );
            return new ContentResult()
            {
                Content = "",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The client PUTs its VR movement mode (teleport / walk) whenever the player changes it;
        // it was a 404. Stored on the heartbeat so the presence others see carries it.
        [HttpPut("player/vrmovementmode")]
        public IActionResult VrMovementMode()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            int mode = 0;
            try
            {
                if (Request.HasFormContentType)
                {
                    var v = Request.Form["vrMovementMode"].ToString();
                    if (string.IsNullOrEmpty(v)) v = Request.Form["VrMovementMode"].ToString();
                    int.TryParse(v, out mode);
                }
            }
            catch { }
            HeartbeatDB.UpdateVrMovementMode(account.Id, mode);
            return Ok("");
        }

        [HttpPut("player/statusvisibility")]
        [HttpPost("player/statusvisibility")]
        public IActionResult StatusVisibility()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Form (the client's), query or JSON; a number or the enum name
            // (Public 0 / FriendsOnly 1 / FavoriteFriendsOnly 2 / Offline 3).
            var f = RoomModeration.Fields(Request);
            string raw = RoomModeration.Str(f, "statusVisibility", "StatusVisibility", "visibility", "value") ?? (f.Count == 1 ? f.Values.First() : "");
            int statusVisibility;
            if (!int.TryParse(raw, out statusVisibility))
                statusVisibility = raw.ToLowerInvariant() switch
                {
                    "public" or "everyone" or "all" => 0,
                    "friendsonly" or "friends" => 1,
                    "favoritefriendsonly" or "favorites" or "favourites" => 2,
                    "offline" or "noone" or "nobody" => 3,
                    _ => -1
                };
            if (statusVisibility < 0 || statusVisibility > 3)
            {
                Console.WriteLine($"[presence] {account.Id} statusvisibility: could not read '{raw}' from {RoomModeration.Dump(f)}");
                return BadRequest(new { success = false, error = "statusVisibility must be 0-3" });
            }

            HeartbeatDB.UpdateStatusVisibility(account.Id, statusVisibility);
            // Realtime: "Appear Online To" changed - re-push presence so friends who should now see
            // (or stop seeing) this player update immediately, not on their next poll - and the
            // player's OWN record to them, so the picker/status dot flips at once.
            Notifications.FireAndForget(Notifications.PushPresenceToFriends(account.Id), $"presence-visibility {account.Id}");
            // Answer the player's own presence record (bare, the GET player?id= shape) - the
            // caller that discards it loses nothing; one that reads it is current immediately.
            var self = HeartbeatDB.GetPresenceForList(account.Id, viewerIsSelf: true, viewerIsFriend: true, viewerIsFavorite: true);
            return Content(JsonConvert.SerializeObject(self), "application/json");
        }

        // The presence heartbeat. THE CLIENT OBEYS WHATEVER WE ANSWER HERE.
        //
        // The request body is EMPTY - verified live, the client sends nothing. What looks
        // like a client report ("playerId", "roomInstance", "appVersion"...) is OUR OWN
        // response being read back: it matches HeartbeatDB.Heartbeat field for field and
        // omits exactly photonRoomIdYeee, the one field we mark [JsonIgnore].
        //
        // So there is nothing to reconcile against. The client compares our answer to
        // where it actually is, and on any disagreement logs "local presence is
        // out-of-sync!" and WALKS ITSELF to whatever we said. Answering "nowhere" reads to
        // it as "you got disconnected", and it goes home to its dorm.
        //
        // Everything that matters therefore happens on the read side: see
        // HeartbeatDB.GetPlayerHeartbeat, which refuses to answer "nowhere" unless
        // something deliberately put the player there.
        [HttpPost("player/heartbeat")]
        public IActionResult PlayerHeartbeat()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // The client DOES send a body here - 78 bytes of application/x-www-form-urlencoded.
            //
            // An earlier diagnostic reported it as empty and that was wrong: it read
            // Request.Form first (which consumes the stream) and then re-read Request.Body,
            // which by then had nothing left. Read the FORM, not the raw stream.
            //
            // Logged once per player so the field names are known without guessing at them.
            if (Request.HasFormContentType && _heartbeatFormLogged.Add(account.Id))
            {
                try
                {
                    Console.WriteLine($"[heartbeat] {account.Id} body: " +
                                      string.Join("&", Request.Form.Select(kv => $"{kv.Key}={kv.Value}")));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[heartbeat] {account.Id} could not read form: {ex.Message}");
                }
            }

            // Limited-time events: time in a public room earns event XP (ProgressionEvents).
            Campus_live_rewrite.api.server.ProgressionEvents.OnHeartbeat(account.Id);

            var hb = HeartbeatDB.GetPlayerHeartbeat(account.Id);
            // Lets the avatar-bundle diagnostics name the player/platform behind a download IP.
            Campus_live_rewrite.api.server.CdnDiag.NoteClient(HttpContext, account, hb.platform, hb.deviceClass);
            return Content(System.Text.Json.JsonSerializer.Serialize(hb), "application/json");
        }

        // Players whose heartbeat body has already been logged - once each, since
        // heartbeats fire every ~20s per player.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _heartbeatFormSeen = new();
        private static class _heartbeatFormLogged
        {
            public static bool Add(ulong id) => _heartbeatFormSeen.TryAdd(id, 0);
        }

        [HttpPost("matchmake/none")]
        public ContentResult Matchmaking_none()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Banned / timed out: do not seed a dorm session for them either. This is the
            // login-lock heartbeat, and seeding here would hand a banned player a fresh
            // room instance to connect to.
            if (PlayerDB.HasActiveModerationBlock(account.Id))
            {
                Console.WriteLine($"[matchmake/none] REFUSED {account.Id}: account is banned or timed out");
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new { roomInstance = (object?)null }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            // "none" = matchmake with no target. The client posts it when it needs an instance but
            // isn't going anywhere in particular - at startup, and during its own reconnect /
            // recovery sequences. Reference (recflare match /matchmake/none): "It answers the
            // instance the player is ALREADY in, so it never warps anyone out of the room they're
            // standing in; only a player with no live presence falls back to their dorm."
            //
            // This used to CLEAR the stored instance instead. Combined with the client posting it
            // mid-recovery (after a Photon drop, e.g. when the master client leaves), the next
            // heartbeat answered "nowhere" and the client booted itself to the dorm - the second
            // half of "one player leaves and the others get kicked" (the first half was
            // notifydisconnect, above). Presence is only ever cleared by player/logout now.
            var presence = HeartbeatDB.GetSelfPresence(account.Id).room;
            if (presence == null)
            {
                try
                {
                    var dormSession = Sessions.CreateDorm(account.Id, account.Username ?? "player");
                    if (dormSession != null)
                    {
                        HeartbeatDB.UpdatePlayerHeartbeat(account.Id, dormSession);
                        Console.WriteLine($"[matchmake/none] {account.Id} had no live presence: seeded dorm session photonRoomId={dormSession.photonRoomIdYeee}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[matchmake/none] Failed to seed dorm for {account.Id}: {ex.Message}");
                }
            }
            else
            {
                // Re-commit the instance they are in (keeps the row and the placement fresh).
                HeartbeatDB.UpdatePlayerHeartbeat(account.Id, presence, online: true);
            }

            var CurrentHeartbeat = JsonConvert.SerializeObject(HeartbeatDB.GetPlayerHeartbeat(account.Id));

            return new ContentResult()
            {
                Content = CurrentHeartbeat,
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("matchmake/dorm")]
        public IActionResult MatchmakeToDorm([FromForm] string correlationId)
        {
            string auth = HttpContext.Request.Headers["Authorization"];
            if (string.IsNullOrEmpty(auth))
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 401 };
            string playerdata = ClientSecurity.DecodeToken(auth);
            if (playerdata == null || playerdata.Contains("error_token"))
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 401 };

            var account = JsonConvert.DeserializeObject<player_info>(playerdata);

            string token = auth.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
            // Was: jwt.Payload["platform"] via the indexer, then .ToString() on the
            // result - KeyNotFoundException if the token carries no platform claim,
            // and a NullReferenceException if it carries a null one. Neither is
            // recoverable here, and this runs on every dorm join. Tolerate both.
            Platforms platform = Platforms.All;
            try
            {
                var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
                if (jwt.Payload.TryGetValue("platform", out var platformObj) && platformObj != null &&
                    int.TryParse(platformObj.ToString(), out int platformVal) &&
                    Enum.IsDefined(typeof(Platforms), platformVal))
                {
                    platform = (Platforms)platformVal;
                }
            }
            catch (Exception jwtEx)
            {
                Console.WriteLine($"[matchmake/dorm] JWT platform parse failed for {account.Id}: {jwtEx.Message}");
            }
            // Banned / timed out players DO get their own dorm (a private instance, alone) - it is
            // the one place they may be. Refusing it handed the client a null room ("error code 3").
            // Every other matchmake still refuses them with MatchmakingErrorCode.Banned.
            if (PlayerDB.HasActiveModerationBlock(account.Id))
                Console.WriteLine($"[matchmake/dorm] {account.Id} is banned or timed out - dorm only");

            var doesPlayerDormExist = RoomDB.DoesPlayerDormExist(account.Id);
            if (doesPlayerDormExist == false)
            {
                PlayerDB.CreateDormForPlayerId(account.Id);
            }

            try
            {
                var session = Sessions.CreateDorm(account.Id, account.Username);
                if (session == null)
                {
                    var hb = HeartbeatDB.GetPlayerHeartbeat(account.Id);

                    return Ok(hb);
                }

                HeartbeatDB.UpdatePlayerHeartbeat(account.Id, session, true, platform, DeviceClasses.Unknown);
                RoomDB.IncreaseRoomVisit((ulong)session.roomId);

                Webhook.Send(
                    account.Id,
                    "prod",
                    "Matchmaking Info",
                    $"({account.Id}) @{account.Username} is going to their Dorm Room",
                    5898927,
                    "",
                    (ulong)session.roomId
                );
            }
            catch (Exception ex)
            {
                // This catch is why the dorm failure was invisible for so long: it
                // swallowed the throw, UpdatePlayerHeartbeat never ran, and the method
                // still returned 200 with whatever stale heartbeat happened to be on
                // file. Keep swallowing (a 500 here would be worse for the client) but
                // make it unmistakable and greppable in journalctl.
                Console.WriteLine($"[matchmake/dorm] FAILED for player {account.Id}: {ex}");
            }

            var finalInstance = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance;
            if (finalInstance == null)
            {
                // The client is being handed no room at all - it will appear to join
                // nothing and will not start its heartbeat loop. Always worth a line.
                Console.WriteLine($"[matchmake/dorm] WARNING player {account.Id}: returning a NULL roomInstance");
            }

            return Content(System.Text.Json.JsonSerializer.Serialize(new FullRoomInstance
            {
                roomInstance = finalInstance,
                correlationId = string.IsNullOrEmpty(correlationId) ? "00000000-0000-0000-0000-000000000000" : correlationId
            }), "application/json");
        }

        // The v2 room-matchmake body as the 2025 client posts it (reference openapi:
        // MatchmakeRoomV2Request): JSON with real types. Only JoinMode (2 = private instance),
        // CorrelationId and AdditionalPlayerIds are acted on; AdditionalPlayerIds is the caller's
        // PARTY - null (not []) when alone - and is what pulls the party into the same instance.
        public class MatchmakeV2Request
        {
            public int? JoinMode { get; set; }
            public string CorrelationId { get; set; }
            public List<long>? AdditionalPlayerIds { get; set; }
            public int? InviteMode { get; set; }
            public bool? ShouldKeepPlayerWithParty { get; set; }
            // True when the GAME moves a group on together (end of a Laser Tag round) - see GroupMoves.
            public bool? AdditionalPlayersAutoFollow { get; set; }
            public bool? BypassMovementModeRestriction { get; set; }
            public int? MaxPersistenceVersion { get; set; }
            public int? Ugc1SubVersion { get; set; }
            public int? Ugc2SubVersion { get; set; }
            public string? VoiceServerVersion { get; set; }
            public string? LoginLock { get; set; }
            public string? ClientJoinData { get; set; }
        }

        // The 2025 client POSTs the follow-a-player / accept-invite / join-event calls WITHOUT a
        // JSON content-type (often an empty body), so a `[FromBody] MatchmakeV2Request` bind answered
        // 415 Unsupported Media Type before the handler ran - which the client surfaces as the
        // "Unable to change rooms" popup. These handlers read the (optional) CorrelationId tolerantly
        // instead, so ANY content-type or an empty body is accepted. matchmake/v2/room + /dorm keep
        // [FromBody] because the client does send them a proper JSON body.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _v2BodyLogged = new();

        private MatchmakeV2Request? ReadV2RequestBody()
        {
            try
            {
                // Diagnostic, a few times per route: the exact body the client posts (content type +
                // first 400 chars), so the party field's real spelling can be read off the log.
                // Always log the party-relevant fields of a join (small, joins are rare): this is
                // the only place the client's party intent is visible to the server.
                {
                    Request.EnableBuffering();
                    using var peek = new StreamReader(Request.Body, leaveOpen: true);
                    var rawPeek = peek.ReadToEndAsync().GetAwaiter().GetResult();
                    Request.Body.Position = 0;
                    string summary = rawPeek;
                    try
                    {
                        if (rawPeek.TrimStart().StartsWith("{"))
                        {
                            var jo = Newtonsoft.Json.Linq.JObject.Parse(rawPeek);
                            summary = $"JoinMode={jo["JoinMode"]} InviteMode={jo["InviteMode"]} KeepWithParty={jo["ShouldKeepPlayerWithParty"]} " +
                                      $"AutoFollow={jo["AdditionalPlayersAutoFollow"]} AdditionalPlayerIds={jo["AdditionalPlayerIds"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"}";
                        }
                    }
                    catch { }
                    if (summary.Length > 300) summary = summary.Substring(0, 300);
                    Console.WriteLine($"[matchmake-body] {Request.Path} {summary}");
                }
                // The 2025 client posts these as FORM-URLENCODED, not JSON:
                //   BypassMovementModeRestriction=..&LoginLock=..&VoiceServerVersion=..&CorrelationId=..&MaxPersistenceVersion=..
                // Reading the body as JSON failed, so CorrelationId came back null - and a matchmake
                // response with a null CorrelationId can't be matched to the client's request, so it
                // ignored the immediate answer and only moved on the next heartbeat (~10s later,
                // "error then it joins seconds after"). Read the FORM so CorrelationId is echoed.
                if (Request.HasFormContentType)
                {
                    var req = new MatchmakeV2Request();
                    if (Request.Form.TryGetValue("CorrelationId", out var corr))
                        req.CorrelationId = corr.ToString();
                    if (Request.Form.TryGetValue("JoinMode", out var jm) && int.TryParse(jm, out var jmv))
                        req.JoinMode = jmv;
                    if (Request.Form.TryGetValue("AdditionalPlayersAutoFollow", out var af) && bool.TryParse(af, out var afv))
                        req.AdditionalPlayersAutoFollow = afv;
                    // The 2023 client posts the party as a REPEATED field (one id each).
                    if (Request.Form.TryGetValue("AdditionalPlayerIds", out var ids))
                        req.AdditionalPlayerIds = ids.Select(v => long.TryParse(v, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
                    return req;
                }

                using var reader = new StreamReader(HttpContext.Request.Body);
                var raw = reader.ReadToEndAsync().GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return JsonConvert.DeserializeObject<MatchmakeV2Request>(raw);
            }
            catch { return null; }
        }


        [HttpPost("matchmake/v2/room/{RoomId}")]
        [HttpPost("matchmake/room/{RoomId}")]
        public IActionResult MatchmakeRoom(ulong RoomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var request = ReadV2RequestBody() ?? new MatchmakeV2Request();
            string? corr = request.CorrelationId;

            // Banned or timed out: matchmake NOWHERE.
            //
            // This used to hand back the player's current heartbeat, so if their room
            // instance was still set the client happily went right back to it - which is
            // how a banned player kept reappearing in the room they were banned from.
            // Answer with a null room instance so there is nothing to return to.
            if (PlayerDB.HasActiveModerationBlock(account.Id))
            {
                Console.WriteLine($"[matchmake] REFUSED {account.Id}: account is banned or timed out");
                return MatchmakeResultV2(MatchmakingErrorCode.Banned, null, corr);
            }

            var room = RoomDB.GetRoom(RoomId);
            if (room == null)
            {
                // Was NREing on room.Name below. Return current heartbeat so client doesn't
                // interpret a null response as auth failure and bounce to login.
                Console.WriteLine($"[matchmake/v2/room/{RoomId}] Room not found");
                return MatchmakeResultV2(MatchmakingErrorCode.NoSuchRoom, null, corr);
            }


            // ---- private rooms need an invite ----------------------------------
            // Roles ARE the invite on this server: Creator / CoOwner / TemporaryCoOwner /
            // Moderator / Host may enter; anyone else is refused. A room-level ban blocks
            // entry even to a public room.
            //
            // Refusing hands back the player's CURRENT heartbeat, so the client leaves them
            // where they are rather than treating it as an auth failure and bouncing them
            // to login.
            if (room == null || !RoomDB.CanPlayerEnterRoom(room, account.Id))
            {
                string why = room == null ? "room not found"
                    : RoomDB.PlayerIsBannedFrom(room, account.Id) ? "banned from that room"
                    : "not published / no invite";
                Console.WriteLine($"[matchmake] REFUSED {account.Id} -> room {RoomId}: {why}");
                var refuseCode = room != null && RoomDB.PlayerIsBannedFrom(room, account.Id)
                    ? MatchmakingErrorCode.BannedFromRoom : MatchmakingErrorCode.RoomIsPrivate;
                return MatchmakeResultV2(refuseCode, null, corr);
            }

            string roomName = room.Name ?? "";
            ulong roomId = room.RoomId;

            string roomIdentifier = $"^{roomName}";
            string tmp = "";
            bool isPrivateRoom = request.JoinMode == 2;
            bool autoFollow = request.AdditionalPlayersAutoFollow == true;
            // The instance they are in now. Never handed straight back: the client keys the room
            // change off a NEW roomInstanceId, so answering "Join new instance" with the instance
            // they are standing in made it rejoin the same room over and over.
            long fromInstance = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance?.roomInstanceId ?? 0;
            // Part of a group the game is moving together (end of a round): where the group went.
            var grouped = GroupMoves.TakeFor(account.Id, roomId, -1, fromInstance, autoFollow);
            // Public joins REUSE an instance someone is already in, so players actually
            // meet. Creating a fresh instance every time (which is what this used to do)
            // only worked because every public instance shared the Photon room name
            // "room" - the same bug that put strangers from other rooms in your world.
            RoomInstance session;
            if (grouped != null)
            {
                session = grouped;
            }
            else if (isPrivateRoom)
            {
                session = Sessions.CreateRoom(roomName, "", true);
            }
            else
            {
                // Only pool into someone else's session when sharing is allowed. A
                // published PRIVATE room is enterable but not shareable, so each visitor
                // gets their own instance and can bring their own friends.
                session = RoomDB.CanPlayerShareInstance(room, account.Id)
                    ? HeartbeatDB.FindJoinableInstance(roomId, account.Id, fromInstance)!
                    : null!;
                if (session != null)
                    Console.WriteLine($"[matchmake] {account.Id} joining existing instance of ^{roomName} " +
                                      $"(photon {session.photonRoomIdYeee}, instance {session.roomInstanceId})");
                else
                {
                    session = Sessions.CreateRoom(roomName)!;
                    if (session != null)
                        Console.WriteLine($"[matchmake] {account.Id} opening a NEW instance of ^{roomName} " +
                                          $"(photon {session.photonRoomIdYeee}, instance {session.roomInstanceId})");
                }
            }
            if (isPrivateRoom)
            {
                tmp = "[PRIVATE INSTANCE]";
            }
            RoomDB.IncreaseRoomVisit(roomId);
            PlayerDB.SetRecentRooms(account.Id, roomId);

            if (session == null)
            {
                Console.WriteLine($"[matchmake] {account.Id} -> room {RoomId}: no instance could be made");
                return MatchmakeResultV2(MatchmakingErrorCode.NoSuchRoom, null, corr);
            }

            HeartbeatDB.UpdatePlayerHeartbeat(account.Id, session);
            if (grouped == null)
                GroupMoves.Record(account.Id, fromInstance, session, request.AdditionalPlayerIds, autoFollow, -1);
            var CurrentHeartbeat = JsonConvert.SerializeObject(HeartbeatDB.GetPlayerHeartbeat(account.Id));
            Webhook.Send(
                 account.Id,
                "prod",
                "Matchmaking Info",
                $"({account.Id}) @{account.Username} is going to ^{roomName} {tmp}",
                5898927,
                "",
                roomId
            );

            var landed = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance ?? session;
            InviteParty(account.Id, account.Username, request.AdditionalPlayerIds, landed, fromInstance);
            return MatchmakeResultV2(MatchmakingErrorCode.Success, landed, corr);
        }

        [HttpPost("matchmake/v2/room/{RoomId}/{SubRoomId}")]
        [HttpPost("matchmake/room/{RoomId}/{SubRoomId}")]
        public IActionResult MatchmakeRoom(ulong RoomId, ulong SubRoomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var request = ReadV2RequestBody() ?? new MatchmakeV2Request();
            string? corr = request.CorrelationId;

            var roomCheck = RoomDB.GetRoom(RoomId);
            if (roomCheck == null)
            {
                Console.WriteLine($"[matchmake/v2/room/{RoomId}/{SubRoomId}] Room not found");
                return MatchmakeResultV2(MatchmakingErrorCode.NoSuchRoom, null, corr);
            }

            if (PlayerDB.HasActiveModerationBlock(account.Id))
            {
                var heartbeat = HeartbeatDB.GetPlayerHeartbeat(account.Id);

                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        heartbeat.playerId,
                        heartbeat.appVersion,
                        heartbeat.deviceClass,
                        errorCode = MatchmakingErrorCode.Banned,
                        heartbeat.isOnline,
                        heartbeat.lastOnline,
                        heartbeat.platform,
                        roomInstance = (string?)null,
                        heartbeat.statusVisibility,
                        heartbeat.vrMovementMode
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            var room = RoomDB.GetRoom(RoomId);


            // ---- private rooms need an invite ----------------------------------
            // Roles ARE the invite on this server: Creator / CoOwner / TemporaryCoOwner /
            // Moderator / Host may enter; anyone else is refused. A room-level ban blocks
            // entry even to a public room.
            //
            // Refusing hands back the player's CURRENT heartbeat, so the client leaves them
            // where they are rather than treating it as an auth failure and bouncing them
            // to login.
            if (room == null || !RoomDB.CanPlayerEnterRoom(room, account.Id))
            {
                string why = room == null ? "room not found"
                    : RoomDB.PlayerIsBannedFrom(room, account.Id) ? "banned from that room"
                    : "not published / no invite";
                Console.WriteLine($"[matchmake] REFUSED {account.Id} -> room {RoomId}: {why}");
                var refuseCode = room != null && RoomDB.PlayerIsBannedFrom(room, account.Id)
                    ? MatchmakingErrorCode.BannedFromRoom : MatchmakingErrorCode.RoomIsPrivate;
                return MatchmakeResultV2(refuseCode, null, corr);
            }

            string roomName = room.Name ?? "";
            ulong roomId = room.RoomId;

            string roomIdentifier = $"^{roomName}";
            string subroomname = RoomDB.get_subroom_from_id(RoomId, SubRoomId);
            string tmp = "";
            bool isPrivateRoom = request.JoinMode == 2;
            bool autoFollow = request.AdditionalPlayersAutoFollow == true;
            long fromInstance = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance?.roomInstanceId ?? 0;
            // Part of a group the game is moving together (the end of a Laser Tag round posts
            // this route from every client): land where the first of them went.
            var grouped = GroupMoves.TakeFor(account.Id, roomId, (long)SubRoomId, fromInstance, autoFollow);
            RoomInstance session = grouped
                ?? (isPrivateRoom
                    ? Sessions.CreateRoom(roomName, subroomname, true)
                    : Sessions.CreateRoom(roomName, subroomname));
            if (isPrivateRoom)
            {
                tmp = "[PRIVATE INSTANCE]";
            }
            RoomDB.IncreaseRoomVisit(roomId);
            PlayerDB.SetRecentRooms(account.Id, roomId);

            if (session == null)
            {
                var heartbeat = JsonConvert.SerializeObject(HeartbeatDB.GetPlayerHeartbeat(account.Id));

                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(heartbeat),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            HeartbeatDB.UpdatePlayerHeartbeat(account.Id, session);
            if (grouped == null)
                GroupMoves.Record(account.Id, fromInstance, session, request.AdditionalPlayerIds, autoFollow, (long)SubRoomId);

            var CurrentHeartbeat = JsonConvert.SerializeObject(HeartbeatDB.GetPlayerHeartbeat(account.Id));
            Webhook.Send(
                 account.Id,
                "prod",
                "Matchmaking Info",
                $"({account.Id}) @{account.Username} is going to ^{roomName}.{subroomname} {tmp}",
                5898927,
                "",
                roomId
            );

            var landed = HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance ?? session;
            InviteParty(account.Id, account.Username, request.AdditionalPlayerIds, landed, fromInstance);
            return MatchmakeResultV2(MatchmakingErrorCode.Success, landed, corr);
        }

        /// <summary>
        /// JOIN EVENT. The client POSTs this when the button is pressed; it did not exist, so
        /// every press 404'd and the client showed "Unable to change rooms".
        ///
        /// Entry rules (see EventDB.CanPlayerJoin):
        ///   creator      -> any time, so they can set up before it starts
        ///   invited      -> any time
        ///   anyone else  -> only once it has started, and not after it has ended
        ///
        /// A refusal hands back the player's CURRENT heartbeat, the same way the room
        /// matchmaker refuses: the client leaves them where they are instead of treating it
        /// as an auth failure.
        /// </summary>
        [HttpPost("matchmake/event/{EventId}")]
        public IActionResult MatchmakeEvent(ulong EventId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var request = ReadV2RequestBody();
            Console.WriteLine($"[matchmake/event] ENTER {account.Id} -> event {EventId}");
            // /matchmake/event is NOT a v2 path, so it answers the lowercase envelope. A refusal is a
            // non-Success code with a null instance (NOT the caller's current room - that echoed a
            // "go where you already are" that read as the "Unable to change rooms" stall).
            // The decision (which instance, overflow, first-in opens it) is shared with the website's Join button.
            var outcome = EventJoin.Resolve(EventId, account.Id);
            if (outcome.Session == null)
                return MatchmakeResultLower(outcome.Code, null, request?.CorrelationId);

            // Success in the lowercase envelope with the event's live instance, so the client GoTos now.
            return MatchmakeResultLower(MatchmakingErrorCode.Success, outcome.Session, request?.CorrelationId);
        }

        /// <summary>
        /// Marks a room instance as in progress. Also 404'd before now. The event UI uses it to
        /// flip an event to "happening now" and to point attendees at the right instance.
        /// </summary>
        // The room panel's refresh (the circular-arrow button beside PRIVATE MODE). It 404'd -
        // nothing implemented it - and, like Reset, it is an instance-wide action, so it is held
        // to the same role floor (ConfigCampus.RoomResetMinRole): a moderator or better in the
        // room the instance belongs to, or a dev. Anyone else gets a 403 and the client leaves
        // the instance alone. The reference server does not implement this route either, so the
        // ack shape is the same bare success the sibling inprogress route answers.
        [HttpPost("roominstance/{roomInstanceId}/refresh")]
        [HttpPut("roominstance/{roomInstanceId}/refresh")]
        public IActionResult RefreshRoomInstance(long roomInstanceId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var inst = HeartbeatDB.FindInstanceById(roomInstanceId);
            if (inst == null) return StatusCode(404, "");

            var room = RoomDB.GetRoom((ulong)inst.roomId);
            int role = (int)(RoomDB.PlayerRoleOn(room, account.Id) ?? RoomDBClasses.Role_data.None);
            bool allowed = role >= ConfigCampus.RoomResetMinRole || PlayerDB.CheckDevFlag(account.Id);
            if (!allowed)
            {
                Console.WriteLine($"[roominstance] {account.Id} (role {role}) tried to refresh instance {roomInstanceId} of room {inst.roomId} - refused (needs {ConfigCampus.RoomResetMinRole}+)");
                return StatusCode(403, "");
            }

            // A refresh OPENS A NEW INSTANCE of the same room/subroom and moves everyone who was
            // in the old one into it - the presence heartbeat walks each client over (it adopts
            // whatever instance the server reports), so the room reloads clean for all of them.
            // Before this it only re-sent the room DTO and nobody moved.
            string subName = room?.SubRooms?.FirstOrDefault(sr => (long)sr.SubRoomId == inst.subRoomId)?.Name ?? "";
            var fresh = Sessions.CreateRoom(room?.Name ?? inst.name ?? "", subName, inst.isPrivate);
            if (fresh == null) return StatusCode(500, "");
            fresh.eventId = inst.eventId; fresh.clubId = inst.clubId;
            var movers = Campus_live_rewrite.api.server.RoomPermissions.PlayersInRoom((ulong)inst.roomId)
                .Where(pid => HeartbeatDB.GetPlayerHeartbeat(pid)?.roomInstance?.roomInstanceId == roomInstanceId).ToList();
            foreach (var pid in movers) HeartbeatDB.UpdatePlayerHeartbeat(pid, fresh);
            // An event whose main (broadcasting) instance this was moves with its players.
            var movedEvent = EventDB.GetEventForRoomInstance((ulong)roomInstanceId);
            if (movedEvent != null)
            {
                EventDB.SetBroadcastingInstance(movedEvent.PlayerEventId, (ulong)fresh.roomInstanceId, true);
                Console.WriteLine($"[event] event {movedEvent.PlayerEventId} main instance {roomInstanceId} -> {fresh.roomInstanceId} (refresh)");
            }
            Console.WriteLine($"[roominstance] {account.Id} (role {role}) refreshed instance {roomInstanceId} of room {inst.roomId} -> new instance {fresh.roomInstanceId} ({movers.Count} player(s) moved)");
            return Ok(new { success = true, roomInstance = fresh });
        }

        [HttpPut("roominstance/{roomInstanceId}/inprogress")]
        public IActionResult SetRoomInstanceInProgress(ulong roomInstanceId, [FromQuery] bool inProgress = true)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // The client sends the flag as a form field (inProgress=True|False); the query is a
            // fallback. It is a property of the INSTANCE (reference: any player in it may flip
            // it), so it lands on every presence row holding that instance.
            try
            {
                if (Request.HasFormContentType && Request.Form.TryGetValue("inProgress", out var f) && bool.TryParse(f.ToString(), out var fb)) inProgress = fb;
            }
            catch { }
            bool was = HeartbeatDB.IsInstanceInProgress(HeartbeatDB.FindInstanceById((long)roomInstanceId));
            HeartbeatDB.UpdateInstanceEverywhere((long)roomInstanceId, i => i.isInProgress = inProgress);
            HeartbeatDB.SetInstanceInProgress((long)roomInstanceId, inProgress);
            if (was != inProgress)
            {
                var inst = HeartbeatDB.FindInstanceById((long)roomInstanceId);
                Console.WriteLine($"[matchmake] instance {roomInstanceId} (room {inst?.roomId.ToString() ?? "?"}) inProgress={inProgress} by {account.Id}" +
                                  (inProgress ? " - new players go to another instance until the round ends" : ""));
            }

            // A round starting/ending in an event's instance (a game played during the event) is
            // not the event moving: clearing BroadcastingRoomInstanceId here on inProgress=false
            // took the event's main instance away mid-event - overflow instances then got a
            // broadcast from an instance the event no longer named (the client's
            // BroadcastRoomInstanceMismatch) and new attendees were scattered. Only log it.
            var ev = EventDB.GetEventForRoomInstance(roomInstanceId);
            if (ev != null)
                Console.WriteLine($"[event] instance {roomInstanceId} inProgress={inProgress} (event {ev.PlayerEventId}, stays its broadcasting instance)");

            return Ok(new { success = true });
        }

        // ---- the owner's Instances tab (reference match: GET /room/{id}/instances) ----------
        // Live sessions of a room with who is in each. Creator / co-owner / staff only.
        [HttpGet("room/{roomId}/instances")]
        public IActionResult RoomInstances(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return NotFound();
            if (!RoomDB.CanModerateRoom(room, account.Id)) return StatusCode(403, "");
            var list = HeartbeatDB.LiveInstancesOfRoom(roomId).Select(t => new
            {
                roomInstanceId = t.inst.roomInstanceId,
                roomId = t.inst.roomId,
                subRoomId = t.inst.subRoomId,
                isFull = HeartbeatDB.InstanceIsFull(t.inst),
                isPrivate = t.inst.isPrivate,
                isInProgress = t.inst.isInProgress,
                playerCount = t.players.Count,
                maxCapacity = HeartbeatDB.EffectiveCapacity(t.inst),
                createdAt = DateTime.UtcNow.ToString("o"),
                playerIds = t.players.Select(p => (long)p).ToList(),
            }).ToList();
            return Ok(list);
        }

        // Mark a live instance private: public matchmaking stops pooling new players into it,
        // the people already inside are unaffected. Creator / co-owner only (reference).
        [HttpPost("roominstance/{roomInstanceId}/markprivate")]
        [HttpPut("roominstance/{roomInstanceId}/markprivate")]
        public IActionResult MarkInstancePrivate(ulong roomInstanceId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var inst = HeartbeatDB.FindInstanceById((long)roomInstanceId);
            if (inst == null) return NotFound();
            var room = RoomDB.GetRoom((ulong)inst.roomId);
            if (room == null || !RoomDB.CanModerateRoom(room, account.Id)) return StatusCode(403, "");
            int n = HeartbeatDB.UpdateInstanceEverywhere((long)roomInstanceId, i => i.isPrivate = true);
            Console.WriteLine($"[matchmake] {account.Id} marked instance {roomInstanceId} of ^{inst.name} private ({n} row(s))");
            return Ok(new { success = true, error = "" });
        }

        // Join one specific live instance of your own room - the Instances tab's "join" (reference
        // match POST /matchmake/instance/{id}). Owner / co-owner / staff; enters even a full or
        // in-progress instance, and never creates one.
        [HttpPost("matchmake/instance/{instanceId}")]
        [HttpPost("matchmake/v2/instance/{instanceId}")]
        public IActionResult MatchmakeInstance(long instanceId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var request = ReadV2RequestBody();
            string? corr = request?.CorrelationId;
            if (PlayerDB.HasActiveModerationBlock(account.Id)) return StayPut(account.Id, corr, MatchmakingErrorCode.Banned, "caller banned/timed out");
            var inst = HeartbeatDB.FindInstanceById(instanceId);
            if (inst == null) return StayPut(account.Id, corr, MatchmakingErrorCode.NoSuchRoom, $"instance {instanceId} is not live");
            var room = RoomDB.GetRoom((ulong)inst.roomId);
            if (room == null || !RoomDB.CanModerateRoom(room, account.Id))
                return StayPut(account.Id, corr, MatchmakingErrorCode.NoSuchRoom, "not a manager of that room");
            HeartbeatDB.UpdatePlayerHeartbeat(account.Id, inst);
            PlayerDB.SetRecentRooms(account.Id, (ulong)inst.roomId);
            Console.WriteLine($"[matchmake] {account.Id} (owner) joined instance {instanceId} of ^{inst.name}");
            return MatchmakeResultV2(MatchmakingErrorCode.Success, inst, corr);
        }

        // Matchmake into a club's clubhouse (reference match POST /matchmake/club/{id}): members
        // only, into the room the club set as its clubhouse.
        [HttpPost("matchmake/club/{clubId}")]
        [HttpPost("matchmake/v2/club/{clubId}")]
        public IActionResult MatchmakeClub(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var request = ReadV2RequestBody();
            string? corr = request?.CorrelationId;
            var club = ClubDB.GetById(clubId);
            if (club == null || club.ClubhouseRoomId == null || club.ClubhouseRoomId <= 0)
                return StayPut(account.Id, corr, MatchmakingErrorCode.NoSuchRoom, $"club {clubId} unknown or has no clubhouse");
            if (ClubDB.MyMembership(clubId, account.Id) <= 0)
                return StayPut(account.Id, corr, MatchmakingErrorCode.NoSuchRoom, $"not a member of club {clubId}");
            return MatchmakeRoom((ulong)club.ClubhouseRoomId.Value);
        }

        // Join a player out of a chat / party thread ("join" on a party member's card - the 2025
        // client posts matchmake/chatinvite/{threadId}/{playerId}, a live 404). Being on the same
        // thread is the consent: both must be members, then it is the ordinary follow-a-player join.
        [HttpPost("matchmake/chatinvite/{threadId}/{playerId}")]
        [HttpPost("matchmake/v2/chatinvite/{threadId}/{playerId}")]
        public IActionResult MatchmakeChatInvite(long threadId, ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var request = ReadV2RequestBody();
            string? corr = request?.CorrelationId;
            if (playerId == account.Id) return StayPut(account.Id, corr, MatchmakingErrorCode.AlreadyInTargetInstance, "can't join yourself");
            if (PlayerDB.HasActiveModerationBlock(account.Id)) return StayPut(account.Id, corr, MatchmakingErrorCode.Banned, "caller banned/timed out");
            if (!ChatDB.IsMember(threadId, account.Id) || !ChatDB.IsMember(threadId, playerId))
                return StayPut(account.Id, corr, MatchmakingErrorCode.InsufficientRelationship, $"not both on chat thread {threadId}");
            if (FriendsDB.IsBlockedEitherWay(account.Id, playerId))
                return StayPut(account.Id, corr, MatchmakingErrorCode.InsufficientRelationship, "one of them has blocked the other");
            if (PlayerDB.HasActiveModerationBlock(playerId) && !PlayerDB.CheckDevFlag(account.Id) && !PlayerDB.CheckModFlag(account.Id))
                return StayPut(account.Id, corr, MatchmakingErrorCode.PlayerNotOnline, "target is banned (dorm only)");
            var (online, targetRoom) = HeartbeatDB.PeekPresence(playerId);
            if (!online || targetRoom == null) targetRoom = HeartbeatDB.RecentInstanceFor(playerId);
            if (targetRoom == null) return StayPut(account.Id, corr, MatchmakingErrorCode.PlayerNotOnline, "target offline / not in a room");
            var room = RoomDB.GetRoom((ulong)targetRoom.roomId);
            if (room != null && RoomDB.PlayerIsBannedFrom(room, account.Id))
                return StayPut(account.Id, corr, MatchmakingErrorCode.BannedFromRoom, "banned from that room");
            return JoinInstance(account.Id, account.Username, corr, targetRoom, playerId);
        }

        [HttpGet("rooms/requiring/{requirement}")]// ["RecCenter", "test123"]
        public ContentResult RoomsRequiring(string requirement)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            requirement = requirement?.ToLowerInvariant();

            string jsonResponse = requirement switch
            {
                "developer" => "[]",
                "rrplus" => "[]",
                _ => "[]"
            };

            return new ContentResult
            {
                Content = jsonResponse,
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Join the player you tapped in the friends list, or whose game invite you accepted
        // (`POST /Matchmaking/matchmake/v2/player/{playerId}`). The 2025 client hits this for BOTH,
        // and it was NOT implemented (live log: 404), so "join" and invite-accept did nothing.
        //
        // Lands the caller in the SAME live instance the target is standing in (same Photon room),
        // read from the target's presence, so they actually meet. Access: mutual friends may always
        // follow; a non-friend may only join a PUBLIC instance (a private/dorm instance needs
        // friendship). Gated on a LIVE socket so we never chase a stale room, plus a room-ban check
        // since this hands out Photon coordinates without going through the room resolver. On any
        // refusal it returns the caller's CURRENT instance (leave them where they are) rather than a
        // null room, so the client doesn't read it as a disconnect and bounce them home. Mirrors the
        // reference `/matchmake/player/:playerId` + `/matchmake/v2/player/:playerId`.
        // "Leave the caller where they are" - the safe answer to a failed join. Returning a null
        // room instead reads to the client as a disconnect and walks them home to their dorm.
        // The PascalCase RoomInstance the v2 client reads out of a matchmake response to know WHERE
        // to go. `/matchmake/v2/*` and `/matchmake/invite/*` answer this shape; the lowercase
        // FullRoomInstance we used before had none of these fields, so the client couldn't read the
        // target's instance from a join-a-player / accept-invite response and fell back to the slow
        // heartbeat poll (~30s) - the "Unable to change rooms, then it force-joins later" symptom.
        // If this instance is one an event is broadcasting to, make sure it carries the event id
        // (the client reads it to show the "This Event" tab). Cheap lazy stamp on read.
        private static void StampInstanceEvent(RoomInstance? i)
        {
            if (i == null || i.eventId != 0) return;
            var ev = EventDB.GetEventForRoomInstance((ulong)i.roomInstanceId);
            if (ev != null) i.eventId = (long)ev.PlayerEventId;
        }

        private static object ToV2RoomInstance(RoomInstance i) => new
        {
            RoomInstanceId    = i.roomInstanceId,
            RoomId            = i.roomId,
            SubRoomId         = i.subRoomId,
            Location          = string.IsNullOrEmpty(i.location) ? "76d98498-60a1-430c-ab76-b54a29b7a163" : i.location,
            // NEVER null: the client's decoder is strict (reference: eventId 0 / clubId 0 /
            // roomCode ""). A null here failed the parse of a SUCCESSFUL join, the client showed
            // "Unable to change room", and the player only moved ~20 s later when the presence
            // heartbeat dragged them over - the "invites are delayed" symptom.
            EventId           = i.eventId,
            ClubId            = i.clubId,
            RoomCode          = "",
            Name              = i.name,
            MaxCapacity       = i.maxCapacity,
            IsFull            = i.isFull,
            IsPrivate         = i.isPrivate,
            IsInProgress      = i.isInProgress,
            EncryptVoiceChat  = i.EncryptVoiceChat,
            RoomInstanceType  = i.roomInstanceType,
            MatchmakingPolicy = i.matchmakingPolicy
        };

        // PascalCase matchmake envelope for /matchmake/v2/* and /matchmake/invite/* (what the 2025
        // client parses): { ErrorCode, CorrelationId, RoomInstance }. ErrorCode 0 = Success; a
        // refusal is a non-zero code with a null RoomInstance (the client then leaves the player put
        // WITHOUT reading it as a disconnect - so we no longer echo their current instance back).
        private IActionResult MatchmakeResultV2(MatchmakingErrorCode code, RoomInstance? instance, string? corr)
        {
            return Content(System.Text.Json.JsonSerializer.Serialize(new
            {
                ErrorCode     = (int)code,
                // Always a string: an invite accept posts an EMPTY body (seen live), and a null
                // echo fails the client's strict decoder. Reference: all-zero GUID when none sent.
                CorrelationId = string.IsNullOrEmpty(corr) ? "00000000-0000-0000-0000-000000000000" : corr,
                RoomInstance  = instance == null ? null : ToV2RoomInstance(StampAndReturn(instance))
            }), "application/json");
        }

        private static RoomInstance StampAndReturn(RoomInstance i) { StampInstanceEvent(i); return i; }

        // Lowercase matchmake envelope for the non-v2 paths (e.g. /matchmake/event/*):
        // { errorCode, result, roomInstance, correlationId } with the RAW (lowercase) instance.
        private IActionResult MatchmakeResultLower(MatchmakingErrorCode code, RoomInstance? instance, string? corr)
        {
            StampInstanceEvent(instance);
            return Content(System.Text.Json.JsonSerializer.Serialize(new
            {
                errorCode     = (int)code,
                result        = (int)code,
                roomInstance  = instance,
                // never null: the client's field is a non-nullable Guid (reference: Guid.Empty when none sent)
                correlationId = string.IsNullOrEmpty(corr) ? "00000000-0000-0000-0000-000000000000" : corr
            }), "application/json");
        }

        // Pull the caller's PARTY into the instance they just landed in (reference: inviteParty).
        // Each member gets the same recorded room invite POST /invite sends, pointing at this
        // instance; their client redeems it through /matchmake/invite/{id} or follows the leader
        // through /matchmake/v2/player/{leader}. The row is what makes the frame redeemable - a
        // frame with no row reads as expired the moment it arrives. Leader skipped, offline
        // members skipped, best-effort per member: it never fails the leader's matchmake.
        private void InviteParty(ulong leaderId, string? leaderName, List<long>? memberIds, RoomInstance? instance, long fromInstance)
        {
            if (memberIds == null || memberIds.Count == 0 || instance == null) return;
            // Only people actually going with them: on their party, or in the instance they just
            // left (the game moving a whole lobby on). The list is the client's, and it was pulling
            // players who had only ended up on a merged party thread (ChatController.AdmitToParty)
            // into whatever room this player went to next.
            var party = ChatDB.GetPartyThreadFor(leaderId)?.PlayerIds ?? new List<ulong>();
            foreach (var raw in memberIds.Distinct())
            {
                if (raw <= 0) continue;
                ulong pid = (ulong)raw;
                if (pid == leaderId) continue;
                var (online, theirRoom) = HeartbeatDB.PeekPresence(pid);
                if (!online) { Console.WriteLine($"[party] {leaderId}: member {pid} offline - not invited"); continue; }
                bool together = party.Contains(pid) || (fromInstance != 0 && theirRoom?.roomInstanceId == fromInstance);
                if (!together) { Console.WriteLine($"[party] {leaderId}: {pid} is not on their party or in their room - not pulled"); continue; }
                try
                {
                    var invite = RoomInvites.Create(leaderId, pid);
                    string data = JsonConvert.SerializeObject(new { InviteId = invite.RoomInviteId, Name = instance.name ?? "", InviteMode = 22 });
                    var msg = new MessageData
                    {
                        Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        FromPlayerId = leaderId,
                        ToPlayerId = pid,
                        SentTime = DateTime.UtcNow,
                        Type = WebsocketEvents.MessageType.GameInviteV2,
                        Data = data,
                        RoomId = (ulong)instance.roomId,
                        PlayerEventId = null
                    };
                    var frame = JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(msg));
                    Notifications.FireAndForget(Notifications.SendToPlayer(pid, frame), $"party invite {leaderId}->{pid}");
                    Console.WriteLine($"[party] {leaderId} @{leaderName} pulled {pid} into ^{instance.name} (instance {instance.roomInstanceId}, invite {invite.RoomInviteId})");
                }
                catch (Exception ex) { Console.WriteLine($"[party] invite {leaderId}->{pid} failed: {ex.Message}"); }
            }
        }

        private IActionResult StayPut(ulong accountId, string? corr, MatchmakingErrorCode code, string why)
        {
            Console.WriteLine($"[matchmake/join] {accountId}: {why} (code {(int)code} {code}); leaving them where they are");
            return MatchmakeResultV2(code, null, corr);
        }

        private IActionResult JoinInstance(ulong accountId, string? username, string? corr, RoomInstance target, ulong followedPlayerId)
        {
            // Kicked out of this instance: not back in through a friend / invite either, until the
            // block runs out. (Any other instance of the room is fine.)
            var block = InstanceBlocks.Active(accountId, target);
            if (block != null)
                return StayPut(accountId, corr, MatchmakingErrorCode.BlockedFromRoom,
                    $"kicked from instance {target.roomInstanceId} of ^{target.name}, blocked until {block.Until:HH:mm}Z");
            // A full instance (the subroom's MaxPlayers - 4 in Jumbotron) refuses the join
            // instead of stuffing a fifth player in; the client shows "room is full".
            if (HeartbeatDB.InstanceIsFull(target))
            {
                var mine = HeartbeatDB.PeekPresence(accountId).room;
                if (mine == null || mine.roomInstanceId != target.roomInstanceId)
                    return StayPut(accountId, corr, MatchmakingErrorCode.InsufficientSpace,
                        $"instance {target.roomInstanceId} of ^{target.name} is full ({HeartbeatDB.CountInInstance(target.roomInstanceId)}/{HeartbeatDB.EffectiveCapacity(target)})");
            }
            HeartbeatDB.UpdatePlayerHeartbeat(accountId, target);
            PlayerDB.SetRecentRooms(accountId, (ulong)target.roomId);
            Console.WriteLine($"[matchmake/join] {accountId} @{username} joining {followedPlayerId} in " +
                              $"^{target.name} (instance {target.roomInstanceId}, photon {target.photonRoomIdYeee})");
            // Return the TARGET's instance in the PascalCase shape so the client GoTos immediately.
            return MatchmakeResultV2(MatchmakingErrorCode.Success, target, corr);
        }

        [HttpPost("matchmake/v2/player/{playerId}")]
        [HttpPost("matchmake/player/{playerId}")]
        public IActionResult MatchmakePlayer(ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var request = ReadV2RequestBody();
            Console.WriteLine($"[matchmake/join] ENTER {account.Id} -> player {playerId}");
            string? corr = request?.CorrelationId;

            if (playerId == account.Id) return StayPut(account.Id, corr, MatchmakingErrorCode.AlreadyInTargetInstance, "can't join yourself");
            if (PlayerDB.HasActiveModerationBlock(account.Id)) return StayPut(account.Id, corr, MatchmakingErrorCode.Banned, "caller banned/timed out");
            if (FriendsDB.IsBlockedEitherWay(account.Id, playerId))
                return StayPut(account.Id, corr, MatchmakingErrorCode.InsufficientRelationship, "one of them has blocked the other");

            // Where is the target right now? Live-socket gated (PeekPresence) so a crashed/closed
            // player's stale room is never chased.
            // A banned player is signed in but confined to their own dorm, alone - nobody follows
            // them in (staff excepted).
            if (PlayerDB.HasActiveModerationBlock(playerId) && !PlayerDB.CheckDevFlag(account.Id) && !PlayerDB.CheckModFlag(account.Id))
                return StayPut(account.Id, corr, MatchmakingErrorCode.PlayerNotOnline, "target is banned (dorm only)");
            var (online, targetRoom) = HeartbeatDB.PeekPresence(playerId);
            if (!online || targetRoom == null)
            {
                // The target has no live room. If they were in one within the last couple of
                // minutes, the caller is almost certainly regrouping after the whole room dropped
                // (master-client switch recovery: both players reset, both rows cleared, each
                // asks to join the other). Send them to that instance so the group re-forms.
                var recent = HeartbeatDB.RecentInstanceFor(playerId);
                if (recent == null)
                    return StayPut(account.Id, corr, MatchmakingErrorCode.PlayerNotOnline, "target offline / not in a room");
                Console.WriteLine($"[matchmake/join] {account.Id} -> player {playerId}: target dropped moments ago, regrouping into their last instance {recent.roomInstanceId}");
                targetRoom = recent;
            }

            // Who may follow: mutual friends always; anyone holding a live invite FROM this player
            // (the accept-invite path); otherwise only into a PUBLIC instance.
            bool friends = FriendsDB.GetRelationships(account.Id)
                .Any(r => r.PlayerID == playerId && r.RelationshipType == RelationshipType.Friend);
            bool invited = RoomInvites.HasInviteFromTo(playerId, account.Id);
            // A party member following the leader: the client never posts the party, so "we were
            // in the same instance a moment ago" is what stands in for membership on the gate.
            bool together = HeartbeatDB.WereTogetherRecently(account.Id, playerId);
            if (!friends && !invited && !together && targetRoom.isPrivate)
                return StayPut(account.Id, corr, MatchmakingErrorCode.RoomInstanceIsPrivate, "not friends/invited/party and target is in a private instance");
            // An UNPUBLISHED room is the owner's draft: friendship alone does not get you in. Only a
            // role on the room or a live invite from someone inside does (a party regroup counts).
            var targetRoomData = RoomDB.GetRoom((ulong)targetRoom.roomId);
            if (targetRoomData != null && !RoomDB.IsRoomPublished(targetRoomData) && !RoomDB.PlayerIsMemberOf(targetRoomData, account.Id) && !invited && !together)
                return StayPut(account.Id, corr, MatchmakingErrorCode.RoomInstanceIsPrivate, "target is in an unpublished room and the caller was not invited");
            if (together && !friends && !invited) Console.WriteLine($"[matchmake/join] {account.Id} -> {playerId}: allowed as a recent party companion");

            var room = RoomDB.GetRoom((ulong)targetRoom.roomId);
            if (room != null && RoomDB.PlayerIsBannedFrom(room, account.Id))
                return StayPut(account.Id, corr, MatchmakingErrorCode.BannedFromRoom, "banned from that room");

            if (invited) RoomInvites.ConsumeFromTo(playerId, account.Id);
            return JoinInstance(account.Id, account.Username, corr, targetRoom, playerId);
        }

        // Redeem a game invite by its id (`POST /Matchmaking/matchmake/invite/{inviteId}` - the
        // 2025 client's join button on an invite). Follows the INVITER's live presence, so it lands
        // you wherever they are now, and is single-use. Was 404.
        [HttpPost("matchmake/invite/{inviteId}")]
        public IActionResult MatchmakeInvite(long inviteId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var request = ReadV2RequestBody();
            Console.WriteLine($"[matchmake/invite] ENTER {account.Id} -> invite {inviteId}");
            string? corr = request?.CorrelationId;

            var inv = RoomInvites.Get(inviteId);
            if (inv == null) return StayPut(account.Id, corr, MatchmakingErrorCode.RoomInviteExpired, $"invite {inviteId} expired/unknown");
            if (inv.ToPlayerId != account.Id) return StayPut(account.Id, corr, MatchmakingErrorCode.RoomInviteExpired, "invite not addressed to caller");
            if (FriendsDB.IsBlockedEitherWay(account.Id, inv.FromPlayerId))
                return StayPut(account.Id, corr, MatchmakingErrorCode.InsufficientRelationship, "one of them has blocked the other");

            if (PlayerDB.HasActiveModerationBlock(inv.FromPlayerId) && !PlayerDB.CheckDevFlag(account.Id) && !PlayerDB.CheckModFlag(account.Id))
                return StayPut(account.Id, corr, MatchmakingErrorCode.PlayerNotOnline, "inviter is banned (dorm only)");
            var (online, targetRoom) = HeartbeatDB.PeekPresence(inv.FromPlayerId);
            if (!online || targetRoom == null) return StayPut(account.Id, corr, MatchmakingErrorCode.PlayerNotOnline, "inviter offline / not in a room");

            var room = RoomDB.GetRoom((ulong)targetRoom.roomId);
            if (room != null && RoomDB.PlayerIsBannedFrom(room, account.Id))
                return StayPut(account.Id, corr, MatchmakingErrorCode.BannedFromRoom, "banned from that room");

            RoomInvites.Consume(inviteId);
            return JoinInstance(account.Id, account.Username, corr, targetRoom, inv.FromPlayerId);
        }

        // Send a game invite (`POST /Matchmaking/invite`) - was 404, which is why the friends-list
        // "Invite" button showed "Could not invite / Unable to send game invite". Records an invite
        // and pushes a GameInviteV2 MessageReceived frame to the target so their client shows the
        // join prompt. Mirrors RecFlare's POST /invite. Per the owner's rule, an OFFLINE (or
        // appear-offline) target CANNOT be invited.
        [HttpPost("invite")]
        public async Task<IActionResult> SendGameInvite()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Parse target + instance from form, JSON body, or query - the client's content-type
            // for this call was unconfirmed and a [FromForm]-only bind silently read 0 (-> spurious
            // 400 "playerId is required") whenever it wasn't exactly form-encoded.
            ulong playerId = 0, roomInstanceId = 0;
            if (Request.HasFormContentType)
            {
                // Form keys are case-sensitive; the client's casing here was unconfirmed, so read
                // both playerId/PlayerId (and the target-id aliases the invite/message APIs use).
                string FormAny(params string[] keys)
                {
                    foreach (var k in keys) { var v = Request.Form[k].ToString(); if (!string.IsNullOrWhiteSpace(v)) return v; }
                    return "";
                }
                ulong.TryParse(FormAny("playerId", "PlayerId", "toPlayerId", "ToPlayerId", "id"), out playerId);
                ulong.TryParse(FormAny("roomInstanceId", "RoomInstanceId"), out roomInstanceId);
            }
            else
            {
                try
                {
                    using var reader = new StreamReader(HttpContext.Request.Body);
                    var raw = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(raw);
                        playerId       = (ulong?)jo["playerId"]       ?? (ulong?)jo["PlayerId"]       ?? 0;
                        roomInstanceId = (ulong?)jo["roomInstanceId"] ?? (ulong?)jo["RoomInstanceId"] ?? 0;
                    }
                }
                catch { }
            }
            if (playerId == 0 && ulong.TryParse(Request.Query["playerId"], out var qp)) playerId = qp;
            if (roomInstanceId == 0 && ulong.TryParse(Request.Query["roomInstanceId"], out var qr)) roomInstanceId = qr;

            Console.WriteLine($"[invite] {account.Id} -> target={playerId} instance={roomInstanceId} form={Request.HasFormContentType}");

            if (playerId == 0)
            {
                Console.WriteLine($"[invite] {account.Id} REFUSED - no playerId in request");
                return BadRequest(new { success = false, error = "playerId is required" });
            }
            if (playerId == account.Id)
                return BadRequest(new { success = false, error = "You can't invite yourself." });
            if (FriendsDB.IsBlockedEitherWay(account.Id, playerId))
            {
                Console.WriteLine($"[invite] {account.Id} REFUSED - one of them has blocked the other ({playerId})");
                return Ok(new { success = false, error = "You can't invite that player." });
            }

            // Do Not Disturb (website setting): the target only takes invites from people they favourited.
            if (!DoNotDisturb.InviteAllowed(playerId, account.Id))
            {
                Console.WriteLine($"[invite] {account.Id} REFUSED - target {playerId} has Do Not Disturb on");
                return Ok(new { success = false, error = "That player isn't taking invites right now." });
            }

            // Offline / appearing offline -> not invitable (the owner's rule). Logged so a wrongly
            // "offline" target - e.g. one whose notify socket dropped on a restart - is visible.
            var (targetOnline, _) = HeartbeatDB.PeekPresence(playerId);
            if (!targetOnline)
            {
                Console.WriteLine($"[invite] {account.Id} REFUSED - target {playerId} reads offline (no live notify socket)");
                return BadRequest(new { success = false, error = "That player is offline." });
            }

            // The instance to invite into: the one the client named, else the caller's current room.
            // Unlike before, a missing instance no longer 400s - the reference records + sends the
            // invite anyway (RoomId/Name just ride empty), so an invite from a dorm/loading caller
            // still reaches the target.
            var instance = HeartbeatDB.FindInstanceById((long)roomInstanceId)
                           ?? HeartbeatDB.GetPlayerHeartbeat(account.Id)?.roomInstance;

            var invite = RoomInvites.Create(account.Id, playerId);

            // GameInviteV2 (type 6) for this client build: Data is JSON-in-a-string; RoomId rides on
            // the message. InviteMode 22 = PlayTogether (RecFlare GAME_INVITE_V2_INVITE_MODE).
            string data = JsonConvert.SerializeObject(new
            {
                InviteId = invite.RoomInviteId,
                Name = instance?.name ?? "",
                InviteMode = 22
            });
            var msg = new MessageData
            {
                Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FromPlayerId = account.Id,
                ToPlayerId = playerId,
                SentTime = DateTime.UtcNow,
                Type = WebsocketEvents.MessageType.GameInviteV2,
                Data = data,
                RoomId = (ulong)(instance?.roomId ?? 0),
                PlayerEventId = null
            };
            try
            {
                var frame = JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(msg));
                await Notifications.SendToPlayer(playerId, frame);
            }
            catch (Exception ex) { Console.WriteLine($"[invite] push to {playerId} failed: {ex.Message}"); }
            // The invitee's client checks the inviter's presence before it will accept. Push it
            // now (the invite makes the inviter visible to them - see GetPresenceForList).
            try { await Notifications.PushPresenceTo(playerId, account.Id); } catch { }

            Console.WriteLine($"[invite] {account.Id} @{account.Username} invited {playerId} to " +
                              $"^{instance?.name ?? "(no room)"} (instance {instance?.roomInstanceId ?? 0}, inviteId {invite.RoomInviteId})");
            return Ok(new
            {
                RoomInviteId = invite.RoomInviteId,
                FromPlayerId = account.Id,
                ToPlayerId = playerId,
                RoomId = instance?.roomId ?? 0
            });
        }

        // Decline / dismiss an invite (`DELETE /Matchmaking/invite/{inviteId}`) - was 404. Only the
        // recipient (or the sender, cancelling) may drop it.
        [HttpDelete("invite/{inviteId}")]
        public IActionResult DeleteGameInvite(long inviteId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var inv = RoomInvites.Get(inviteId);
            if (inv != null && (inv.ToPlayerId == account.Id || inv.FromPlayerId == account.Id))
            {
                RoomInvites.Consume(inviteId);
                Console.WriteLine($"[invite] {account.Id} dismissed invite {inviteId} ({inv.FromPlayerId} -> {inv.ToPlayerId})");
            }
            return Ok(new { success = true });
        }

        // The client's clean-exit signal (`POST /Matchmaking/player/notifydisconnect`) was NOT
        // implemented (live log: 404), so a graceful quit never cleared the player's presence and
        // friends kept seeing them "online in a room they aren't in". Clear the heartbeat here so
        // presence drops immediately on a normal exit (the friends-list read is also live-socket
        // gated now, but this makes the graceful case instant instead of waiting for the socket to
        // notice the drop).
        // The client posts this when its PHOTON connection to the room drops - which happens to
        // every remaining player the moment the Photon master client (the first one into the
        // instance) leaves - and then reconnects to the same room by itself. This used to CLEAR
        // the player's room instance. Their next presence heartbeat then answered "nowhere", and
        // the client treats that as an eviction:
        //
        //   [RecNet.Features.Matchmaking] RecNet presence heartbeat response indicates local
        //     presence is out-of-sync!
        //   [RoomLoad.RoomManager.PresenceUpdates] RecNet is directing us to go to an Invalid
        //     RoomInstance. This is RecNet booting us from the room ... so we trigger Fallbacks.
        //
        // - and sent itself to the dorm. That is "one player leaves, everyone else gets kicked",
        // verified in the server log: every "answer changed <instance> -> 0 (nowhere)" on
        // 2026-09-11 followed that same player's own notifydisconnect by 5-40 s.
        //
        // The reference (recflare match /player/notifydisconnect) logs it and does nothing:
        // presence is cleared by logout, and otherwise expires. Same here. A player who really
        // left is cleared by player/logout or reset by their next login / matchmake, both of
        // which still run.
        [HttpPost("player/notifydisconnect")]
        public IActionResult NotifyDisconnect()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string detail = "";
            try
            {
                if (Request.HasFormContentType)
                    detail = string.Join("&", Request.Form.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            catch { }
            var current = HeartbeatDB.GetSelfPresence(account.Id).room;
            Console.WriteLine($"[matchmake] {account.Id} notified a Photon disconnect ({detail}) - presence kept " +
                              $"(instance {current?.roomInstanceId ?? 0}); the client reconnects on its own");
            return Ok(new { success = true });
        }

        [HttpGet("player")]
        [HttpPost("player")]
        public IActionResult GetPlayerHeartbeatById([FromQuery] List<ulong> id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // POST form: the reference's presence heartbeat (a LoginLock guid, verified not
            // stored) answering the CALLER's own presence; with ids in the body it is the bulk
            // lookup. Was a 405 either way.
            if (HttpMethods.IsPost(Request.Method) && (id == null || id.Count == 0))
            {
                var f = RoomModeration.Fields(Request);
                var ids = RoomModeration.ULongs(f, "id", "ids", "playerIds", "PlayerIds");
                if (ids.Count > 0) id = ids;
                else return Ok(HeartbeatDB.GetPlayerHeartbeat(account.Id));
            }

            if (id == null || id.Count == 0)
            {
                return Ok(ConfigCampus.Bracket);
            }

            // The caller is the VIEWER. Precompute who they're friends with / have favorited once,
            // so each target's "Appear Online To" (statusVisibility) can be honored relative to
            // them (Friends-only / Favorites-only / No One) without a lookup per target.
            var rels = FriendsDB.GetRelationships(account.Id);
            var friendIds = rels.Where(r => r.RelationshipType == RelationshipType.Friend)
                                .Select(r => r.PlayerID).ToHashSet();
            var favIds = rels.Where(r => r.Favorited != ReciprocalStatus.None)
                             .Select(r => r.PlayerID).ToHashSet();

            var heartbeats = new List<object>();

            foreach (var playerId in id)
            {
                // Friends/player-list presence for OTHERS: live-socket gated (so a crashed/closed
                // player shows OFFLINE with no room instead of "online in a room they aren't in")
                // AND status-visibility honored relative to the viewer. The caller's OWN
                // authoritative heartbeat (which must never answer "nowhere") is served by
                // player/heartbeat, not here.
                var heartbeat = HeartbeatDB.GetPresenceForList(
                    playerId,
                    viewerIsSelf: playerId == account.Id,
                    viewerIsFriend: friendIds.Contains(playerId),
                    viewerIsFavorite: favIds.Contains(playerId),
                    viewerId: account.Id);
                if (heartbeat != null)
                    heartbeats.Add(heartbeat);
            }

            return Ok(heartbeats);
        }

        [HttpGet("player/qos")]
        public async Task<IActionResult> GetPlayerQos()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "QOS.json");
            string content = await System.IO.File.ReadAllTextAsync(path);
            // Was: Ok(<string>). ASP.NET's StringOutputFormatter writes a raw string as
            // Content-Type: text/plain, so these JSON payloads were served mislabelled
            // and the client would not parse them. Verified live: /outfits/me returned
            // text/plain while /api/avatar/v2 returned application/json. Same bytes,
            // correct content type.
            return new ContentResult { Content = content, ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("player/connection-info")]
        public IActionResult GetPlayerConnectionInfo([FromQuery] ulong roomInstanceId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var heartbeat = HeartbeatDB.GetPlayerHeartbeat(account.Id);

            // Answer about the instance the client ACTUALLY ASKED FOR.
            //
            // This used to ignore roomInstanceId entirely and reply with whatever room the
            // caller's own heartbeat pointed at. While every public instance shared the
            // Photon room name "room" that never showed up - any answer put you in the same
            // room. Now that names are per-instance, replying about the wrong instance
            // sends the client to a different Photon room mid-load, and it dies with
            // "Failed to load room due disconnect during load (Code 8706-7)".
            //
            // Falls back to the caller's own instance when the id is unknown (0, or an
            // instance nobody is in yet), which is the old behaviour.
            var requested = HeartbeatDB.FindInstanceById((long)roomInstanceId);
            var instance = requested ?? heartbeat?.roomInstance;

            string photonRoom = instance?.photonRoomIdYeee ?? $"solo-{account.Id}";

            if (requested == null && roomInstanceId != 0)
                Console.WriteLine($"[connection-info] {account.Id} asked about instance {roomInstanceId} " +
                                  $"- not found, falling back to their own ({instance?.roomInstanceId})");

            // Tachyon voice is only handed out for a room with a REAL instance id, exactly like the
            // reference's tachyonServerFor (roomInstanceId <= 0 -> empty -> Photon). The dorm/home
            // is instance -1 (no live instance), so it MUST stay on Photon; advertising Tachyon for
            // it made the client fail to connect to voice and blocked the whole room load (Code
            // 8719). Public/created rooms have a positive instance and get Tachyon.
            long voiceInstanceId = instance?.roomInstanceId ?? 0;
            bool useTachyon = !string.IsNullOrEmpty(ConfigCampus.TachyonVoiceEndpoint) && voiceInstanceId > 0;
            string voiceConn = useTachyon ? ConfigCampus.TachyonVoiceEndpoint : "";
            string voiceSrv = useTachyon ? ConfigCampus.TachyonVoiceServerId : "";
            Console.WriteLine($"[conn-info] acct={account.Id} instance={voiceInstanceId} " +
                              $"voice='{voiceConn}' serverId='{voiceSrv}' tachyonCfg='{ConfigCampus.TachyonVoiceEndpoint}'");

            // BOTH shapes in one body. This build's DTO is documented as a BARE 9-key object
            // (wire-shapes: player-connection-info) while the envelope below is what has actually
            // been serving working Photon joins here - Utf8Json drops whichever set it does not
            // know, so emitting both cannot break either reading.
            var connExperiments = new
            {
                NetworkTransformSyncInterval = 10.0,
                ShouldUseUnreliableOnChange = false,
                ShouldAvoidDiscontinuityRPCs = true,
                ShouldAvoidRedundantDiscontinuity = false,
                R2RuntimeStaticBaking = true,
                R2AutoEmbodiment = true,
                R2RuntimeStaticBakingMinShapeThreshold = 1,
                ShouldUseGameServerNetworking = false
            };
            return Ok(new
            {
                PhotonAuthToken = ClientSecurity.Createphotonauthtoken(account.Id, photonRoom),
                PhotonRealtimeAppId = ConfigCampus.PhotonRealtimeId,
                PhotonVoiceAppId = ConfigCampus.PhotonVoiceId,
                PhotonChatAppId = ConfigCampus.PhotonChatId,
                PhotonRegion = "us",
                PhotonRoomId = photonRoom,
                VoiceConnectionInfo = voiceConn,
                VoiceServerId = voiceSrv,
                Experiments = connExperiments,

                success = true,
                value = new
                {
                    photonAuthToken = ClientSecurity.Createphotonauthtoken(account.Id, photonRoom),// "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxNTAzMjY5NzUwIiwicm4ucGxhdGlkIjoiNzY1NjExOTk2MTM4Mzg5MjgiLCJybi5wbGF0IjoiMCIsInJuLmRldmljZWNsYXNzIjoiMiIsInJuLmVudiI6InByb2QiLCJleHAiOjE3NTQzMTYzNTMsImF1ZCI6ImM4YWFhODYwLTFjMGMtNDMzOC1iYWYyLWE1NTNjMDFiMmE1MyJ9.qY_O0tNeZeH2QKuPUIQPhRPnOpp2uhZn3b8eo-ybV3w",
                    photonRealtimeAppId = ConfigCampus.PhotonRealtimeId,
                    photonVoiceAppId = ConfigCampus.PhotonVoiceId,
                    photonChatAppId = ConfigCampus.PhotonChatId,
                    photonRegion = "us", // qos us-east1
                    photonRoomId = photonRoom,
                    // voiceConnectionInfo & voiceServerId: EMPTY STRINGS, matching RecFlare, which
                    // sets both to varOr(TACHYON_HOST_PORT/NAME, '') - i.e. "" when no dedicated
                    // (Tachyon) voice server is configured. Its comment: "Empty rather than null:
                    // the client's decoder is likelier to accept a missing-value string than a null
                    // on a string field." With no dedicated server named, the client falls back to
                    // PHOTON VOICE (photonVoiceAppId above) for peer-to-peer voice in photonRoomId -
                    // which is how players actually hear each other.
                    //
                    // Earlier tries here were "{}" (an OBJECT - parsed as a voice-server DTO, found
                    // nothing, and aborted with "Code 8719" blocking room load) and then null
                    // (which just skipped voice entirely - mic worked, nobody could hear you). The
                    // empty STRING is the value the reference uses and neither of those was.
                    //
                    // TACHYON: when a dedicated voice server is configured (ConfigCampus
                    // .TachyonVoiceEndpoint = "<ip>:<port>"), hand the client that endpoint so it
                    // routes voice through Tachyon instead of Photon P2P. voiceServerId must be a
                    // non-empty id alongside it; the client uses voiceConnectionInfo ("<ip>:<port>")
                    // to reach the server. Endpoint empty -> both stay "" -> Photon fallback (known
                    // good). Tachyon resolves each player's room via GET /tachyon?id=<AccountId>
                    // (TachyonController) so voice is scoped to the room they're actually in.
                    voiceConnectionInfo = voiceConn,
                    voiceServerId = voiceSrv,
                    experiments = new
                    {
                        networkTransformSyncInterval = 10.0,
                        shouldUseUnreliableOnChange = false,
                        shouldAvoidDiscontinuityRPCs = true,
                        shouldAvoidRedundantDiscontinuity = false,
                        r2RuntimeStaticBaking = true,
                        r2AutoEmbodiment = true,
                        r2RuntimeStaticBakingMinShapeThreshold = 1,
                        r2UseCheapReplicas = true,
                        shouldUseGameServerNetworking = false // true make it connect to 127.0.0.1:7777 qos, false it connects to photon servers ns.exitgames.com
                    }
                },
                // Match the reference envelope EXACTLY: { success, value, error }. The reference
                // always includes error: null (see match openapi ConnectionInfoResponse). We were
                // omitting it. The client may validate the whole envelope before trusting
                // value.voiceConnectionInfo, so a missing `error` can make it drop the voice info
                // ("No VoiceConnectionInfo") while Photon still connects via a looser read.
                error = (object?)null
            });
        }

        [HttpPut("player/gameserverregionpings")]
        public IActionResult UpdatePlayerGameServerRegionPings()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok("");
        }

        // Client publishes its measured Photon-region pings here so the server can pick the
        // closest region for matchmaking. We don't do region-aware matchmaking (single Photon
        // Cloud app, no region selection), so accept + discard. Returning 404 leaves the
        // client stuck in a "still measuring pings" state and delays room joins.
        [HttpPut("player/photonregionpings")]
        [HttpPost("player/photonregionpings")]
        public IActionResult UpdatePhotonRegionPings()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Ok("");
        }

        [HttpPost("roominstance/{roomInstanceId}/reportjoinresult")]
        public IActionResult ReportJoinResultForRoomInstanceId(ulong roomInstanceId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // The client reports how joining went (e.g. FailedToSpawnPlayer / code 8718 after a room load that threw). It used to be ignored,
            // so a room that "kicks you to your dorm" left nothing on the server side to look at.
            try
            {
                var f = RoomModeration.Fields(Request);
                Console.WriteLine($"[joinresult] {account.Id} instance {roomInstanceId}: {RoomModeration.Dump(f)}");
            }
            catch { }
            return Ok();
        }
    }
}
