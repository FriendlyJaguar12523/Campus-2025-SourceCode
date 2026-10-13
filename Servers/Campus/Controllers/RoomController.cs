using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
using System.IdentityModel.Tokens.Jwt;
using static Campus_live_rewrite.api.server.Moderation_Detail;
using Microsoft.AspNetCore.Http.HttpResults;
using static Campus_live_rewrite.api.server.Classes.db_class.RoomDBClasses;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;


namespace Campus_live_rewrite.Controllers
{
    [Route("Room_server")]
    public class RoomController : ControllerBase
    {
        [HttpGet("rooms")]
        public ContentResult Room_server_rooms([FromQuery] ulong? roomid = null, [FromQuery] string? name = null, [FromQuery] ulong? include = null, [FromQuery] int? unityAssetTarget = null, [FromQuery] int? unityAssetVersion = null)
        {
            try
            {
                // DELETE by roomid
                if (HttpContext.Request.Method == HttpMethods.Delete && roomid.HasValue)
                {
                    var (account, error) = AuthHelper.ValidateToken(HttpContext);
                    if (error != null)
                        return error;

                    RoomDBClasses.RoomRoot room = RoomDB.GetRoom(roomid.Value);
                    if (room == null)
                    {
                        return new ContentResult()
                        {
                            Content = JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Value = (string?)null,
                                ErrorId = "Rooms.DoesntExist",
                                Error = "This room does not exist!"
                            }),
                            ContentType = "application/json",
                            StatusCode = 200
                        };
                    }

                    if (room.CreatorAccountId != account.Id)
                    {
                        return new ContentResult()
                        {
                            Content = JsonConvert.SerializeObject(new
                            {
                                Success = false,
                                Value = (string?)null,
                                ErrorId = "Rooms.PermissionDenied",
                                Error = "You are not the owner of this room!"
                            }),
                            ContentType = "application/json",
                            StatusCode = 200
                        };
                    }

                    RoomDB.DeleteRoom(roomid.Value);

                    return new ContentResult()
                    {
                        Content = "[]",
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                // Lookup by roomid
                if (roomid.HasValue)
                {
                    RoomDBClasses.RoomRoot existingRoom = RoomDB.GetRoom(roomid.Value);
                    if (existingRoom == null)
                    {
                        return new ContentResult()
                        {
                            Content = "[]",
                            ContentType = "application/json",
                            StatusCode = 200
                        };
                    }

                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(StripUnservableSaves(DirectLookup(existingRoom))),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                // Lookup by name
                if (!string.IsNullOrEmpty(name))
                {
                    // LOCAL EXPERIMENT (HoldNewAccountCreation): after the title-screen identity flow the client looks up
                    // "SocialOrientation" by name and heads for its LandingZone (offline instance -3, -4, ...), while the
                    // server's presence says ^Orientation (room 13, instance -2) - the clash ends in a logout. No such room =
                    // no multiplayer orientation, so the client should take the Orientation presence it is given.
                    if (ConfigCampus.HoldNewAccountCreation && name.Equals("SocialOrientation", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[rooms] SocialOrientation name lookup answered 404 (local experiment: client should take ^Orientation)");
                        return new ContentResult() { Content = "", ContentType = "application/json", StatusCode = 404 };
                    }
                    RoomDBClasses.RoomRoot room = RoomDB.GetRoomByName(name);
                    if (room == null)
                    {
                        return new ContentResult()
                        {
                            Content = "",
                            ContentType = "application/json",
                            StatusCode = 404
                        };
                    }

                    return new ContentResult()
                    {
                        Content = JsonConvert.SerializeObject(StripUnservableSaves(room)),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                // Neither roomid nor name provided
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }
        }


        // Strip a subroom's CurrentSave when its .room blob cannot be served.
        //
        // The client loads a room from the CurrentSave embedded in the ROOM OBJECT, not
        // from the /saves endpoint - so filtering /saves alone was not enough, and
        // "Error downloading subroom data! (Code 8715)" persisted. It requests
        // /cdn/room/<DataBlob>, which 404s when the file is absent from
        // Data/CDN/RoomBlobs (the cdn.rec.net fallback is dead - those blobs are no
        // longer public), and the load aborts.
        //
        // Joining an existing instance never fetches the blob (state replicates over
        // Photon from whoever is already inside), which is exactly why Rec Center worked
        // via a friend but failed alone.
        //
        // Nulling CurrentSave makes the room load its default Unity scene - the same
        // path rooms with no save (Park, dorms) already take successfully.
        //
        // Safe to mutate: GetRoom returns a freshly deserialized LiteDB object and no
        // caller writes it back (verified), so this never persists. Self-correcting -
        // put the real .room files in Data/CDN/RoomBlobs and saves return untouched.
        // List overload. Room LISTINGS matter as much as the single-room GETs: the client
        // caches these and can join straight from a cached entry (rooms/visitedby/me is
        // requested moments before every join), so an unservable blob left in a listing
        // resurfaces the same failure by another route.
        //
        // Only ever call this on GET/serialize paths. The PUT/POST mutation endpoints
        // (SetRoomName, SetRoomData, ...) load-modify-save the same objects, so stripping
        // there would PERSIST the null into the DB and destroy the reference for good.
        private static List<RoomDBClasses.RoomRoot> StripUnservableSaves(List<RoomDBClasses.RoomRoot>? rooms)
            => WithCCU(Campus_live_rewrite.api.server.RoomBlobFilter.Strip(rooms));

        /// <summary>
        /// The viewer's account id, or 0 when the request is unauthenticated.
        ///
        /// Some room endpoints (bulk, hot, search) never called ValidateToken, so there is
        /// no `account` in scope there. Visibility still has to be decided per viewer, so
        /// read it from the token directly. 0 means "no one" - which sees only published
        /// rooms, the correct default for an anonymous caller.
        /// </summary>
        private ulong ViewerId()
        {
            try
            {
                var (acc, err) = AuthHelper.ValidateToken(HttpContext);
                return err == null && acc != null ? acc.Id : 0UL;
            }
            catch
            {
                return 0UL;
            }
        }

        // ---- Visibility -------------------------------------------------------
        //
        // Applied at every point a room is SERVED, not just in listings. Fetching a room
        // straight by id used to bypass every check: any player could read another
        // player's unpublished private room - name, subrooms, save data - simply by
        // asking for /Room_server/rooms/78.
        //
        // A room is visible when it is published, or when the viewer holds a role on it
        // (owner / co-owner / moderator / host). See RoomDB.CanPlayerSeeRoom.

        /// <summary>Returns the room, or null when this viewer is not allowed to see it.</summary>
        private static RoomDBClasses.RoomRoot? VisibleTo(RoomDBClasses.RoomRoot? room, ulong viewerId)
            => RoomDB.CanPlayerSeeRoom(room, viewerId) ? room : null;

        // A DIRECT lookup (rooms/{id}, rooms?id=, rooms/bulk) answers the record whatever its
        // accessibility, exactly like the reference (and rec.net): Private / Unlisted only keep a
        // room off the browse lists and search - CanPlayerEnterRoom still gates joining, and the
        // saves are stripped separately. Hiding it here (answering null / []) broke every player
        // who was INSIDE a private room or another player's dorm: their friend card, search
        // result and profile all resolve the room they're in through these routes, the client
        // reads the empty answer as NoSuchRoom, and the card / profile never rendered - "some
        // players' profiles won't load for anyone". Only excludePrivateRooms=True filters, and
        // it filters on listing, not on who is asking.
        private static RoomDBClasses.RoomRoot? DirectLookup(RoomDBClasses.RoomRoot? room, bool excludePrivate = false)
            => room == null ? null : (excludePrivate && !RoomDB.IsRoomListed(room)) ? null : room;

        /// <summary>Drops every room this viewer is not allowed to see.</summary>
        private static List<RoomDBClasses.RoomRoot> VisibleTo(List<RoomDBClasses.RoomRoot>? rooms, ulong viewerId)
            => (rooms ?? new List<RoomDBClasses.RoomRoot>())
               .Where(r => RoomDB.CanPlayerSeeRoom(r, viewerId))
               .ToList();

        private static RoomDBClasses.RoomRoot? StripUnservableSaves(RoomDBClasses.RoomRoot? room)
            => WithCCU(Campus_live_rewrite.api.server.RoomBlobFilter.Strip(room));

        // Fill the client's CCU key (live player count) on whatever this response is about to
        // return. One presence pass per response, not per room.
        private static RoomDBClasses.RoomRoot? WithCCU(RoomDBClasses.RoomRoot? room)
        {
            if (room == null) return null;
            try { room.CCU = HeartbeatDB.LivePlayerCountsByRoom().TryGetValue((long)room.RoomId, out var n) ? n : 0; }
            catch { }
            return room;
        }

        private static List<RoomDBClasses.RoomRoot> WithCCU(List<RoomDBClasses.RoomRoot>? rooms)
        {
            rooms ??= new List<RoomDBClasses.RoomRoot>();
            try
            {
                var counts = HeartbeatDB.LivePlayerCountsByRoom();
                foreach (var r in rooms)
                    if (r != null) r.CCU = counts.TryGetValue((long)r.RoomId, out var n) ? n : 0;
            }
            catch { }
            return rooms;
        }

        [HttpGet("rooms/{roomid}")]
        public ContentResult GetRoomById(ulong roomid)
        {
            var room = RoomDB.GetRoom(roomid);
            if (room == null)
            {
                return new ContentResult
                {
                    Content = "[]",
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(StripUnservableSaves(DirectLookup(room))),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpDelete("rooms/{roomid}")]
        public ContentResult DeleteRoomById(ulong roomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var room = RoomDB.GetRoom(roomid);
            if (room == null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Value = (string?)null,
                        ErrorId = "Rooms.DoesntExist",
                        Error = "This room does not exist!"
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            if (room.CreatorAccountId != account.Id)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        Success = false,
                        Value = (string?)null,
                        ErrorId = "Rooms.PermissionDenied",
                        Error = "You are not the owner of this room!"
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            RoomDB.DeleteRoom(roomid);

            // This used to BAN the caller - Set_moderation_block(account.Id, ..., isBan: true,
            // duration: 1). account.Id is the room OWNER, i.e. whoever just deleted their own
            // room, so deleting a room banned you from the game. isBan sends websocket event 22,
            // which blacks the client out, logs it out and closes it. Duration is in SECONDS, so
            // the block cleared after one second and you could log straight back in - which is
            // exactly why it looked like "something/someone keeps closing my game" rather than
            // a ban.
            //
            // The intent was presumably to get the owner out of the room they just deleted. That
            // is a host kick, not a ban: isHostKick drops them to their dorm with no ban screen,
            // no logout and no moderation record. Same shape ChatCommands.HandleKick uses.
            PlayerDB.Set_moderation_block(
                    account.Id,
                    ReportCategory.Moderator,
                    duration: 1,
                    gameSessionId: -1,
                    isHostKick: true,
                    message: "The room you were in was deleted.",
                    playerIdReporter: null,
                    isBan: false,
                    bannedByPlayerId: 0
                );
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

        [HttpPost("rooms/bulk")]
        public IActionResult Room_server_rooms_bulk_post()
        {
            var f = RoomModeration.Fields(Request);
            var ids = RoomModeration.ULongs(f, "id", "ids", "roomIds", "Ids");
            string? name = RoomModeration.Str(f, "name");
            bool excl = (RoomModeration.Str(f, "excludePrivateRooms") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
            if (ids.Count <= 1) return Room_server_rooms_bulk(ids.Count == 1 ? ids[0] : 0, name, null, excl);
            ulong viewer = ViewerId();
            var rooms = new List<object>();
            foreach (var id in ids.Take(100))
            {
                var room = RoomDB.GetRoom(id);
                if (room == null) continue;
                if (DirectLookup(room, excl) == null) continue;
                rooms.Add(StripUnservableSaves(room));
            }
            return Content(JsonConvert.SerializeObject(rooms), "application/json");
        }

        [HttpGet("rooms/bulk")]
        public IActionResult Room_server_rooms_bulk([FromQuery(Name = "id")] ulong roomId, [FromQuery] string? name, [FromQuery] ulong? include, [FromQuery] bool? excludePrivateRooms)
        {
            RoomDBClasses.RoomRoot room = RoomDB.GetRoomByName(name);
            if (room == null)
            {
                RoomDBClasses.RoomRoot room2 = RoomDB.GetRoom(roomId);
                if (room2 == null)
                {
                    return Ok(ConfigCampus.Bracket);
                }
                // by-ID branch - this is the one the client uses (rooms/bulk?id=2) and it
                // was handing back CurrentSave unfiltered, which is how the DataBlob
                // kept reaching the client even after the room-load endpoints were fixed.
                return Content(DirectLookup(room2, excludePrivateRooms == true) == null ? "[]" : $"[{JsonConvert.SerializeObject(StripUnservableSaves(room2))}]", "application/json");
            }

            return Content(DirectLookup(room, excludePrivateRooms == true) == null ? "[]" : $"[{JsonConvert.SerializeObject(StripUnservableSaves(room))}]", "application/json");
        }

        // These three back the Create -> "My Rooms" list. createdby/me used to return a
        // hardcoded "[]" stub, and ownedby/me fell through to the ulong route below where
        // "me" bound to 0 - so the tab was always empty even with rooms in the DB.
        // contributedby/me did not exist at all and 404'd.
        [HttpGet("rooms/createdby/me")]
        public ContentResult roomscreatedbyme()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(StripUnservableSaves(RoomDB.GetRoomsByCreatorPaged(account.Id, 0, ConfigCampus.ProfileRoomsCap))),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The profile's Rooms tab asks by PLAYER ID (createdby/{id}, contributedby/{id}), not "me";
        // only the /me spellings existed, so another player's rooms - and your own after a restart,
        // once the client stopped using the locally cached list - came back 404 and the tab was
        // empty while the showcase (which does take an id) still filled.
        [HttpGet("rooms/createdby/{playerId:long}")]
        public ContentResult RoomsCreatedBy(ulong playerId, int skip = 0, int take = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            if (take <= 0) take = ConfigCampus.ProfileRoomsCap;
            var rooms = RoomDB.GetRoomsByCreatorPaged(playerId, skip, take);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(StripUnservableSaves(VisibleTo(rooms, account.Id).Where(r => !r.IsDorm).ToList())),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("rooms/contributedby/{playerId:long}")]
        public ContentResult RoomsContributedBy(ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            return new ContentResult
            {
                // GetRoomsContributedTo now leads with the player's OWN rooms, so this is the
                // profile's whole room list. VisibleTo is what keeps another player's unpublished
                // rooms off it - a viewer with no role on a room sees it only once it is public.
                Content = JsonConvert.SerializeObject(StripUnservableSaves(
                    VisibleTo(RoomDB.GetRoomsContributedTo(playerId), account.Id)
                        .Where(r => !r.IsDorm).ToList())),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // MUST be declared before rooms/ownedby/{PlayerId}: a literal segment beats the
        // parameter route, otherwise "me" is parsed as a ulong and silently becomes 0.
        [HttpGet("rooms/ownedby/me")]
        public ContentResult RoomsOwnedByMe()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(StripUnservableSaves(RoomDB.GetRoomsByCreatorPaged(account.Id, 0, ConfigCampus.ProfileRoomsCap))),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("rooms/contributedby/me")]
        public ContentResult RoomsContributedByMe()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(StripUnservableSaves(
                    RoomDB.GetRoomsContributedTo(account.Id).Where(r => !r.IsDorm).ToList())),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // What each role may do in ONE subroom - the Permissions tab of Room Settings. JSON array
        // body, each entry addressed by its (Permission, Role) pair: Override:true stores the
        // value, Override:false is the unchecked box and clears the pair back to the default.
        // The stored table is what GET /photon_access_token merges over its defaults for every
        // player standing in that subroom - so this IS "edit what the perms do", and it lives on
        // the subroom record, which is what a room save carries. (Reference: creator-only. Here
        // co-owners may too: "high enough permissions" - a co-owner already builds in the room.)
        [HttpPut("rooms/{roomId}/subrooms/{subRoomId}/permissions")]
        [HttpPost("rooms/{roomId}/subrooms/{subRoomId}/permissions")]
        public async Task<IActionResult> SetSubRoomPermissions(ulong roomId, ulong subRoomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var room = RoomDB.GetRoom(roomId);
            if (room == null || RoomDB.GetSubRoom(roomId, subRoomId) == null) return StatusCode(404, "");
            if (!RoomDB.PlayerCanManageRoles(roomId, account.Id))
            {
                Console.WriteLine($"[room-perms] {account.Id} may not edit permissions on room {roomId} - refused");
                return StatusCode(403, "");
            }

            string raw;
            using (var reader = new StreamReader(Request.Body)) raw = await reader.ReadToEndAsync();
            var changes = Campus_live_rewrite.api.server.RoomPermissions.ParseBody(raw);
            if (changes == null)
            {
                Console.WriteLine($"[room-perms] {account.Id}: unreadable body on room {roomId}/{subRoomId}: {raw.Substring(0, Math.Min(200, raw.Length))}");
                return StatusCode(400, "");
            }

            var current = RoomDB.GetSubRoom(roomId, subRoomId)?.Permissions;
            var table = Campus_live_rewrite.api.server.RoomPermissions.Apply(current, changes);
            RoomDB.SetSubRoomPermissions(roomId, subRoomId, table);
            Console.WriteLine($"[room-perms] {account.Id} set {changes.Count} permission(s) on room {roomId} subroom {subRoomId}: " +
                              string.Join(", ", changes.Select(c => $"{c.Permission}@{c.Role}={(c.Override ? c.Value : "default")}")));

            // Instant: everyone standing in the room re-reads it (and re-pulls photon_access_token
            // with the new table) instead of waiting for their next spawn.
            foreach (var pid in Campus_live_rewrite.api.server.RoomPermissions.PlayersInRoom(roomId))
                Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdate(pid, room);

            // The 2025 client's ChangeAndRefresh reads the answer through the enveloped room
            // helper - an empty body is "Response was empty" / "Failed to change subroom
            // permissions" in Player.log, even though the change was stored. (Reference: empty.)
            return RoomWriteEnvelope(roomId);
        }

        // The ROOM-level spelling (PUT rooms/{id}/permissions, 404'd): the same body, applied to
        // every subroom of the room, so a room with one subroom behaves exactly like the subroom
        // route and a multi-subroom room gets the change everywhere.
        [HttpPut("rooms/{roomId}/permissions")]
        [HttpPost("rooms/{roomId}/permissions")]
        public async Task<IActionResult> SetRoomPermissions(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return StatusCode(404, "");
            if (!RoomDB.PlayerCanManageRoles(roomId, account.Id)) return StatusCode(403, "");

            string raw;
            using (var reader = new StreamReader(Request.Body)) raw = await reader.ReadToEndAsync();
            var changes = Campus_live_rewrite.api.server.RoomPermissions.ParseBody(raw);
            if (changes == null) return StatusCode(400, "");

            RoomDB.AttachSubRooms(new[] { room });
            foreach (var sub in room.SubRooms ?? new List<SubRoom>())
            {
                var table = Campus_live_rewrite.api.server.RoomPermissions.Apply(sub.Permissions, changes);
                RoomDB.SetSubRoomPermissions(roomId, (ulong)sub.SubRoomId, table);
            }
            Console.WriteLine($"[room-perms] {account.Id} set {changes.Count} permission(s) on every subroom of room {roomId}");
            foreach (var pid in Campus_live_rewrite.api.server.RoomPermissions.PlayersInRoom(roomId))
                Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdate(pid, room);
            return RoomWriteEnvelope(roomId);
        }

        [HttpGet("rooms/{roomId}/subrooms/{subRoomId}/permissions")]
        public IActionResult GetSubRoomPermissions(ulong roomId, ulong subRoomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var sub = RoomDB.GetSubRoom(roomId, subRoomId);
            if (sub == null) return StatusCode(404, "");
            return Ok(sub.Permissions ?? new List<Campus_live_rewrite.api.server.RoomPermissions.Entry>());
        }

        [HttpGet("photon_access_token")]
        public IActionResult photon_access_token()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var heartbeat = HeartbeatDB.GetPlayerHeartbeat(account.Id);

            // The table the client applies when it spawns: RoomPermissions.Defaults (reset and
            // delete-all now start at Moderator, not at everyone) with the overrides the room's
            // managers saved on the SUBROOM the caller is standing in merged over it, by
            // (Permission, Role) - see PUT rooms/{id}/subrooms/{sub}/permissions.
            var permissions = Campus_live_rewrite.api.server.RoomPermissions.Defaults(account.Id);
            var inst = heartbeat?.roomInstance;
            if (inst != null && inst.subRoomId > 0)
            {
                var sub = RoomDB.GetSubRoom((ulong)inst.roomId, (ulong)inst.subRoomId);
                permissions = Campus_live_rewrite.api.server.RoomPermissions.Merge(permissions, sub?.Permissions);
            }

            return Ok(new
            {
                RoomInstanceId = inst?.roomInstanceId,
                PhotonAccessToken = ClientSecurity.Createphotonacesstoken(account.Id),
                Permissions = permissions.ToArray()
            });
        }


        [HttpGet("rooms/hot")]
        public ContentResult HotRooms(string tag, int skip = 0, int take = 30)
        {
            var rooms = RoomDB.GetHotRooms();

            if (rooms == null || rooms.Count == 0)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new Resultslist<RoomDBClasses.RoomRoot>
                    {
                        Results = new List<RoomDBClasses.RoomRoot>(),
                        TotalResults = 0
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            IEnumerable<RoomDBClasses.RoomRoot> query = rooms;

            bool isRRO = false;

            if (!string.IsNullOrEmpty(tag))
            {
                string t = tag.ToLower();

                // --- RRO Filter ---
                if (t == "rro" || t == "recroomoriginal")
                {
                    isRRO = true;

                    query = query.Where(r =>
                        r.Tags != null &&
                        r.Tags.Any(x =>
                        {
                            string tt = x.Tag?.ToLower();
                            return tt == "rro" || tt == "recroomoriginal";
                        })
                    );
                }
                else if (t == "new")
                {
                    // A pseudo-tag no room carries: the player-made (non-RRO) public rooms,
                    // NEWEST first (RecFlare). This used to take the 30 newest of everything and
                    // then re-sort them by visits, so "New" was really "popular among recent".
                    var fresh = rooms.Where(r => r.Accessibility == RoomDBClasses.RoomAccessibility.Public && !r.ExcludeFromLists
                                                 && !RoomDB.IsRecRoomOriginal(r))
                                     .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RoomId).ToList();
                    var freshPage = fresh.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToList();
                    RoomDB.AttachSubRooms(freshPage);
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new Resultslist<RoomDBClasses.RoomRoot>
                        {
                            Results = StripUnservableSaves(VisibleTo(freshPage, ViewerId())),
                            TotalResults = fresh.Count
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }
                else if (t == "community")
                {
                    // Pseudo-tag: rooms a PLAYER made (not the Coach account's stock rooms).
                    query = query.Where(r => r.CreatorAccountId != CoachAccountId && !RoomDB.IsRecRoomOriginal(r));
                }
            }
            else
            {
                // Default mode: EXCLUDE rro + base
                query = query.Where(r =>
                    r.Tags == null ||
                    !r.Tags.Any(x =>
                    {
                        string tt = x.Tag?.ToLower();
                        return tt == "base" || tt == "rro" || tt == "recroomoriginal";
                    })
                );
            }

            // Only public rooms when NOT requesting RRO
            if (!isRRO)
            {
                query = query.Where(r => r.Accessibility == RoomDBClasses.RoomAccessibility.Public);
            }

            // "Hot" is live population first (RecFlare): the rooms people are in right now lead,
            // then engagement for the empty ones, then RoomId so paging is stable.
            query = query.Where(r => !r.ExcludeFromLists);
            var live = HeartbeatDB.ConnectedPlayerCountsByRoom();
            query = query.OrderByDescending(r => live.GetValueOrDefault((long)r.RoomId))
                         .ThenByDescending(HotScore)
                         .ThenBy(r => r.RoomId);

            // Pagination
            int totalCount = query.Count();
            var paged = query.Skip(skip).Take(take).ToList();
            // Subrooms only for the page being returned (see RoomDB.GetHotRooms).
            RoomDB.AttachSubRooms(paged);

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new Resultslist<RoomDBClasses.RoomRoot>
                {
                    Results = StripUnservableSaves(VisibleTo(paged, ViewerId())),
                    TotalResults = totalCount
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }





        [HttpGet("rooms/{roomid}/interactionby/me")]
        public IActionResult Room_server_room_interactionby_me(ulong roomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var interaction = PlayerDB.GetInteraction(account.Id, roomid);

            // ContentResult, not Ok(string): a raw string goes out as text/plain (see CommerceController).
            return Content(JsonConvert.SerializeObject(new
            {
                Cheered = interaction.Cheered,
                Favorited = interaction.Favorited,
                LastVisitedAt = DateTime.Now // could persist if you want
            }), "application/json");
        }

        [Route("rooms/{roomid}/interactionby/me/cheer")]
        public IActionResult Room_server_room_interactionby_me_cheer(ulong roomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            bool cheered = PlayerDB.ToggleCheer(account.Id, roomid);
            var favorited = PlayerDB.GetInteraction(account.Id, roomid).Favorited;

            return Content(JsonConvert.SerializeObject(new
            {
                Cheered = cheered,
                Favorited = favorited,
                LastVisitedAt = DateTime.Now
            }), "application/json");
        }

        [Route("rooms/{roomid}/interactionby/me/favorite")]
        public IActionResult Room_server_room_interactionby_me_favorite(ulong roomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            bool favorited = PlayerDB.ToggleFavorite(account.Id, roomid);
            var cheered = PlayerDB.GetInteraction(account.Id, roomid).Cheered;

            return Content(JsonConvert.SerializeObject(new
            {
                Cheered = cheered,
                Favorited = favorited,
                LastVisitedAt = DateTime.Now
            }), "application/json");
        }

        [HttpGet("rooms/ownedby/{PlayerId}")]
        public ContentResult RoomsOwnedBy(ulong PlayerId, int skip = 0, int take = 0)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Page at the DB level - only this page's rooms (and their subrooms) are loaded. An
            // account that owns thousands (e.g. @Importer) used to load EVERY room + a subroom query
            // each on every profile view, overloading the server.
            if (take <= 0) take = ConfigCampus.ProfileRoomsCap;
            var owned = RoomDB.GetRoomsByCreatorPaged(PlayerId, skip, take);

            // The dorm is personal space, served by /dormroom/me - never part of a rooms list.
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(StripUnservableSaves(VisibleTo(owned, account.Id).Where(r => !r.IsDorm).ToList())),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Search suggestions for the search box - type-ahead. Ported from RecFlare.
        //
        // Returns a BARE ARRAY OF STRINGS (not rooms, not an envelope): room names and tags
        // drawn from the public, non-dorm rooms that /rooms/search searches, so every
        // suggestion finds something when submitted. A tag suggestion carries its '#' so it
        // searches by tag; a query starting with '#' suggests tags ONLY. Matches that START
        // with the query come first, then ones that merely contain it, alphabetical within
        // each. `take` caps the list (the client asks for 4). searchSessionId is ignored.
        //
        // This did not exist, so search-as-you-type got a 404 and no suggestions ever showed.
        [HttpGet("rooms/autocomplete_search")]
        public ContentResult RoomAutocompleteSearch(string? query = "", int take = 10, string? searchSessionId = null)
        {
            query = (query ?? "").Trim();
            bool tagsOnly = query.StartsWith("#");
            string q = query.TrimStart('#').ToLowerInvariant();

            var suggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var room in RoomDB.GetAllRooms())
            {
                if (room == null || room.IsDorm) continue;
                if (!RoomDB.IsRoomListed(room)) continue;   // only rooms search can actually find

                if (!tagsOnly && !string.IsNullOrWhiteSpace(room.Name))
                    suggestions.Add(room.Name);

                if (room.Tags != null)
                    foreach (var t in room.Tags)
                        if (!string.IsNullOrWhiteSpace(t?.Tag))
                            suggestions.Add("#" + t.Tag);
            }

            // Rank: starts-with the query before merely-contains, alphabetical within each.
            // A tag's '#' is ignored when matching, so "#rr" and "rr" both hit the "rro" tag.
            static string Bare(string s) => s.TrimStart('#').ToLowerInvariant();
            var ordered = suggestions
                .Where(s => tagsOnly ? s.StartsWith("#") : true)
                .Where(s => q.Length == 0 || Bare(s).Contains(q))
                .OrderByDescending(s => q.Length > 0 && Bare(s).StartsWith(q))
                .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Take(take <= 0 ? 10 : take)
                .ToList();

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(ordered),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("rooms/search")]
        public ContentResult RoomSearch(string query, int skip = 0, int take = 30)
        {
            var rooms = RoomDB.Search(query);

            if (rooms == null || rooms.Count == 0)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(
                        new RoomDBClasses.Resultslist<RoomDBClasses.RoomRoot>
                        {
                            Results = new List<RoomDBClasses.RoomRoot>(),
                            TotalResults = 0
                        }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            int total = rooms.Count;
            var paged = rooms.Skip(skip).Take(take).ToList();

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(
                    new RoomDBClasses.Resultslist<RoomDBClasses.RoomRoot>
                    {
                        Results = StripUnservableSaves(VisibleTo(paged, ViewerId())),
                        TotalResults = total
                    }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        [HttpGet("rooms/visitedby/me")]
        public ContentResult RoomsVisitedByMe(int skip = 0, int take = 20)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = PlayerDB.GetRecentRooms(account.Id, skip, take),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The "recent rooms" section on ANOTHER player's profile. Only /me existed, so opening
        // someone else's profile 404'd this section. Respects the target's recent-room-history
        // privacy setting exactly as Rec Room does: when they've hidden it, the viewer gets [].
        // Your own id always returns your history (privacy is about what OTHERS see).
        [HttpGet("rooms/visitedby/{playerid}")]
        public ContentResult RoomsVisitedByPlayer(ulong playerid, int skip = 0, int take = 20)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            if (playerid != account.Id)
            {
                var target = PlayerDB.GetFullAccount(playerid);
                bool visible = target?.player_Extra?.IsRecentRoomHistoryVisible ?? false;
                if (!visible)
                    return new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };
            }

            return new ContentResult()
            {
                Content = PlayerDB.GetRecentRooms(playerid, skip, take),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The room showcase on a player's profile. This used to be a hardcoded "[]"
        // stub, which is why the Profile tab rendered as one big empty panel even for
        // accounts that own rooms. The client wants a plain list of room ids: [1, 2, 3].
        //
        // Dorms and private rooms are deliberately excluded - a showcase is public, and
        // listing someone's private rooms to any viewer would leak them.
        // The showcase is the handful of rooms a player PICKS for the top of their profile
        // (PUT showcase, room ids). It used to list every room they own, which is what made
        // everything land in "Room Showcase" instead of the Rooms tab. No pick = empty, exactly
        // like the real service; the Rooms tab (createdby/{id}) is where all of them live.
        [HttpPut("showcase")]
        [HttpPost("showcase")]
        [HttpPut("showcase/me")]
        [HttpPost("showcase/me")]
        public ContentResult SetRoomShowcase()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var ids = new List<ulong>();
            void Add(string? v) { if (!string.IsNullOrWhiteSpace(v)) foreach (var p in v.Split(',')) if (ulong.TryParse(p.Trim(), out var id) && id != 0 && !ids.Contains(id)) ids.Add(id); }
            try
            {
                if (Request.HasFormContentType)
                    foreach (var k in new[] { "roomIds", "roomId", "ids", "id", "RoomIds" }) foreach (var v in Request.Form[k]) Add(v);
                foreach (var k in new[] { "roomIds", "roomId", "ids", "id" }) foreach (var v in Request.Query[k]) Add(v);
                if (ids.Count == 0 && !Request.HasFormContentType)
                {
                    Request.EnableBuffering(); Request.Body.Position = 0;
                    using var reader = new StreamReader(Request.Body, leaveOpen: true);
                    string raw = reader.ReadToEndAsync().GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var node = System.Text.Json.Nodes.JsonNode.Parse(raw);
                        if (node is System.Text.Json.Nodes.JsonArray ja) foreach (var n in ja) Add(n?.ToString());
                        else if (node is System.Text.Json.Nodes.JsonObject jo) foreach (var kv in jo) { if (kv.Value is System.Text.Json.Nodes.JsonArray a2) foreach (var n in a2) Add(n?.ToString()); else Add(kv.Value?.ToString()); }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[showcase] {account.Id}: could not read body - {ex.Message}"); }
            // Only rooms the player actually created may be showcased.
            ids = ids.Where(id => RoomDB.GetRoom(id)?.CreatorAccountId == account.Id).Take(ConfigCampus.ShowcaseCap).ToList();
            SettingsDB.SetPlayerSetting("Campus.RoomShowcase", string.Join(",", ids), account.Id);
            Console.WriteLine($"[showcase] {account.Id} set showcase to [{string.Join(",", ids)}]");
            return new ContentResult { Content = JsonConvert.SerializeObject(ids), ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("showcase/{playerid}")]
        public ContentResult RoomShowcaseForPlayer(ulong playerid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var picked = (SettingsDB.GetPlayerSettingValue("Campus.RoomShowcase", playerid) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => ulong.TryParse(p.Trim(), out var id) ? id : 0)
                .Where(id => id != 0)
                .ToList();
            var rooms = picked.Select(id => RoomDB.GetRoom(id)).Where(r => r != null && r!.CreatorAccountId == playerid).Select(r => r!).ToList();

            // NO FALLBACK. A showcase with nothing picked is EMPTY.
            //
            // This briefly fell back to "every room the player created", which did put the rooms
            // on the profile - but under the Room Showcase heading, all 33 of them, instead of in
            // the rooms list where the co-owned rooms (contributedby) correctly land. The rooms
            // list is fed by rooms/createdby/{id}, which is the route that was actually missing
            // (only the /me spelling existed, so a profile by id 404'd and the grid was empty).
            // That route is what makes an owner see their own rooms; this one stays a pick list.

            // Show what THIS VIEWER is allowed to see, not just public rooms.
            //
            // This used to be "Accessibility == Public", which hid almost everything: a
            // freshly created room defaults to PRIVATE (RoomAccessibility.Private = 0), so
            // players saw an empty portfolio even on their own profile. Now:
            //   - your own profile shows all your rooms, private included
            //   - someone else's shows their public/unlisted rooms, plus any private room
            //     you personally hold a role on (owner / co-owner / moderator / host)
            // Dorms are never listed - they are personal space, not portfolio pieces.
            var ids = rooms
                .Where(r => r != null
                         && !r.IsDorm
                         && RoomDB.CanPlayerSeeRoom(r, account.Id))
                .Select(r => r.RoomId)
                .Take(ConfigCampus.ShowcaseCap)
                .ToList();

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(ids),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Room role management (co-owner / moderator / host). These were ack/empty-array stubs, so
        // "invite as co-owner" did nothing and the roles list always showed empty. Now they persist.
        // Ban history for one account in one room. ENVELOPED {Value:{ActiveBan, PreviousBans}}
        // (wire-shapes: rooms-bans-history); "never banned" is a null ActiveBan and an empty list,
        // and nothing throws on either. Was a 404.
        [HttpGet("rooms/{roomId}/bans/history")]
        public IActionResult RoomBanHistory(ulong roomId, [FromQuery] long id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            // A room ban is the Banned ROLE on the room plus a RoomBans record (RoomDB.BanFromRoom);
            // the record carries who / when / why / until, and closed ones are the history.
            var room = RoomDB.GetRoom(roomId);
            object? BanRow(RoomDBClasses.RoomBanDetails d) => new
            {
                Status = d.Status,                    // Active 0 / Elapsed 1 / Lifted 2
                UnbannedByAccountId = d.UnbannedByAccountId.HasValue ? (int?)d.UnbannedByAccountId.Value : null,
                BanEndTime = d.BanEndTime,
                Reason = d.Reason ?? "",
                AccountId = (int)d.AccountId,
                BannedByAccountId = d.BannedByAccountId != 0 ? (int?)d.BannedByAccountId : null,
                BanStartTime = d.BanStartTime
            };
            var current = room != null ? RoomDB.ActiveRoomBan(room, (ulong)id) : null;
            object? active = current != null ? BanRow(current) : null;
            var previous = (room?.RoomBans ?? new())
                .Where(b => b.AccountId == id && b.Status != 0)
                .OrderByDescending(b => b.BanStartTime)
                .Select(b => BanRow(b)!)
                .ToArray();
            return Ok(new
            {
                Value = new { ActiveBan = active, PreviousBans = previous },
                Success = true,
                Error = (string?)null,
                error_id = (string?)null
            });
        }

        // What every role / permission WRITE answers: the room-write envelope with the updated
        // room as its value, {success, error, value}. The 2025 client sends all of these through
        // the room facade's enveloped helper (OALMDGJKBJH.PJPLCMBOAFB<T>) and parses the answer
        // as an object - Player.log, inviting a co-owner: "Malformed Response: '[{"AccountId":1,
        // ...}]' ... expected:'{', actual:'['", then "Failed to invite account to role"; and on
        // the permissions PUT: "Response was empty" / "Failed to change subroom permissions".
        // The bare roles ARRAY is what GET /roles answers (rooms-roles-wire-shape) and only that.
        // The wire keys accept any casing, so this is the same lowercase envelope PUT /name uses.
        private ContentResult RoomWriteEnvelope(ulong roomId) => new ContentResult
        {
            Content = JsonConvert.SerializeObject(new { success = true, error = "", value = RoomDB.GetRoom(roomId) }),
            ContentType = "application/json",
            StatusCode = 200
        };

        [HttpGet("rooms/{roomId}/roles")]
        public IActionResult GetRoomRolesEndpoint(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Ok(RoomDB.GetRoomRoles(roomId));   // BARE array - the one roles route that is
        }

        // Remove a player's role outright (the roles panel's "remove"). Same gate as invite.
        [HttpDelete("rooms/{roomId}/roles/{playerId}")]
        public IActionResult RemoveRoomRole(ulong roomId, long playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            // Removing YOURSELF: decline a pending invite, or step down from a role you hold.
            if ((ulong)playerId == account.Id)
            {
                if (RoomDB.PendingRoleInvite(roomId, playerId) != RoomDBClasses.Role_data.None)
                    return ResolveRoleInvite(roomId, account.Id, accept: false);
                var mine = RoomDB.GetRoom(roomId);
                if (mine != null && mine.CreatorAccountId == account.Id) return StatusCode(403, "");
                RoomDB.SetRoomRole(roomId, playerId, RoomDBClasses.Role_data.None, (long)account.Id);
                PlayerDB.DeleteRoomRoleMessages(account.Id, roomId);
                Console.WriteLine($"[room-role] {account.Id} left their role on room {roomId}");
                return RoomWriteEnvelope(roomId);
            }
            if (!RoomDB.PlayerCanManageRoles(roomId, account.Id))
                return StatusCode(403, "");
            bool ok = RoomDB.SetRoomRole(roomId, playerId, RoomDBClasses.Role_data.None, (long)account.Id);
            if (ok) PlayerDB.DeleteRoomRoleMessages((ulong)playerId, roomId);   // a withdrawn invite's notice goes too
            Console.WriteLine($"[room-role] {account.Id} removed player {playerId}'s role on room {roomId} -> {ok}");
            return RoomWriteEnvelope(roomId);
        }

        [HttpPut("rooms/{roomId}/roles/{playerId}")]
        [HttpPost("rooms/{roomId}/roles/{playerId}")]
        [HttpPut("rooms/{roomId}/roles/{playerId}/invite")]
        [HttpPost("rooms/{roomId}/roles/{playerId}/invite")]
        public IActionResult InviteRoomRole(ulong roomId, long playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            // The INVITEE answering their own pending invite comes through the same route with
            // their own id: that is an accept, never a self-grant (nothing here lets a caller give
            // themselves a role - the grant is exactly the role a manager offered).
            //
            // The answer is in the form body (reference answerRoomRoleInvite): accepting names the
            // offered tier, DECLINING is an explicit role=0. Every self-answer used to be taken as an
            // accept, so pressing Decline made you a co-owner / contributor anyway.
            if ((ulong)playerId == account.Id)
            {
                string answer = Request.HasFormContentType ? Request.Form["role"].ToString() : "";
                if (string.IsNullOrWhiteSpace(answer)) answer = Request.Query["role"].ToString();
                bool declining = int.TryParse(answer, out var answerRole) && answerRole == 0;

                var pending = RoomDB.PendingRoleInvite(roomId, playerId);
                // How fast was it answered? A person reading a notice takes a while; the log shows whether the client answers by itself.
                Console.WriteLine($"[room-role] {account.Id} answered the invite on room {roomId} with role='{answer}' {RoleOffers.SecondsSincePushed(roomId, account.Id):0.0} s after it was sent (pending: {pending})");
                if (pending != RoomDBClasses.Role_data.None)
                    return ResolveRoleInvite(roomId, account.Id, accept: !declining);

                // Nothing pending. The notice is answered either way, so it goes.
                PlayerDB.DeleteRoomRoleMessages(account.Id, roomId);
                var room0 = RoomDB.GetRoom(roomId);
                var held = RoomDB.PlayerRoleOn(room0, account.Id) ?? RoomDBClasses.Role_data.None;
                if (held != RoomDBClasses.Role_data.None && held != RoomDBClasses.Role_data.Banned)
                {
                    // "Reject" on a "you are added as" notice: step down (never the room's creator).
                    if (declining && room0 != null && room0.CreatorAccountId != account.Id)
                    {
                        RoomDB.SetRoomRole(roomId, playerId, RoomDBClasses.Role_data.None, (long)account.Id);
                        Console.WriteLine($"[room-role] {account.Id} stepped down from {held} on room {roomId}");
                        Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId), account.Id);
                    }
                    // Otherwise already accepted (tapped twice / a retry): a no-op success.
                    return RoomWriteEnvelope(roomId);
                }
                // No role and no invite (withdrawn, or already declined): nothing to grant. Answer
                // with the room so the client closes the notice instead of showing an error.
                Console.WriteLine($"[room-role] {account.Id} answered (role='{answer}') an invite on room {roomId} that is no longer pending");
                return RoomWriteEnvelope(roomId);
            }

            // Only the room's creator, a co-owner, or a dev/admin may offer roles.
            if (!RoomDB.PlayerCanManageRoles(roomId, account.Id))
                return StatusCode(403, "");

            // The client PUTs a small form body: role=<Role_data value> (30 CoOwner, 20 Moderator,
            // 10 Host, 0 None=remove). Fall back to query / invitedRole; default CoOwner if absent.
            string roleStr = Request.HasFormContentType ? Request.Form["role"].ToString() : "";
            if (string.IsNullOrWhiteSpace(roleStr)) roleStr = Request.Query["role"].ToString();
            if (string.IsNullOrWhiteSpace(roleStr) && Request.HasFormContentType) roleStr = Request.Form["invitedRole"].ToString();
            int roleVal = int.TryParse(roleStr, out var rv) ? rv : (int)RoomDBClasses.Role_data.CoOwner;
            var role = Enum.IsDefined(typeof(RoomDBClasses.Role_data), (byte)roleVal)
                ? (RoomDBClasses.Role_data)(byte)roleVal : RoomDBClasses.Role_data.CoOwner;

            // A creator can't be demoted through here, can't be invited to their own room, and
            // nobody can hand out Creator.
            var target_room = RoomDB.GetRoom(roomId);
            if (target_room != null && (ulong)playerId == target_room.CreatorAccountId)
            {
                Console.WriteLine($"[room-role] {account.Id} tried to change the creator's role on room {roomId} - refused");
                return StatusCode(403, "");
            }
            if (role == RoomDBClasses.Role_data.Creator) role = RoomDBClasses.Role_data.CoOwner;
            if (role == RoomDBClasses.Role_data.Banned) role = RoomDBClasses.Role_data.None;

            // Offering (or granting) a role needs the two to be friends - strangers were making players co-owners of
            // random rooms. Taking a role away (None) is never refused.
            if (role != RoomDBClasses.Role_data.None && !RoleOffers.MayOffer(roomId, account.Id, (ulong)playerId, out var refusal))
            {
                Console.WriteLine($"[room-role] {account.Id} offered player {playerId} {role} on room {roomId} - REFUSED: not friends / blocked");
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { success = false, error = refusal, value = (object?)null }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }

            bool ok;
            if (role == RoomDBClasses.Role_data.None)
            {
                ok = RoomDB.SetRoomRole(roomId, playerId, role, (long)account.Id);
                Console.WriteLine($"[room-role] {account.Id} removed player {playerId}'s role on room {roomId} -> {ok}");
                if (ok) PlayerDB.DeleteRoomRoleMessages((ulong)playerId, roomId);
                if (ok) Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId), (ulong)playerId);
                return RoomWriteEnvelope(roomId);
            }

            // Co-owner and contributor are OFFERED and wait for the invitee's Accept - they hand
            // over the room's contents. Moderator and host are GRANTED on the spot: they are
            // staffing roles, and an invite for them is what the owner reported as "adding a mod
            // makes them get an invite". The target's client is told immediately either way.
            if (role == RoomDBClasses.Role_data.Moderator || role == RoomDBClasses.Role_data.Host)
            {
                ok = RoomDB.SetRoomRole(roomId, playerId, role, (long)account.Id);
                Console.WriteLine($"[room-role] {account.Id} made player {playerId} {role} ({roleVal}) on room {roomId} -> {ok} (granted directly)");
                if (ok) Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId), (ulong)playerId);
                return RoomWriteEnvelope(roomId);
            }

            ok = RoomDB.InviteRoomRole(roomId, playerId, role, (long)account.Id);
            Console.WriteLine($"[room-role] {account.Id} invited player {playerId} as {role} ({roleVal}) on room {roomId} -> {ok} (pending acceptance)");
            if (ok)
            {
                try
                {
                    PlayerDB.DeleteRoomRoleMessages((ulong)playerId, roomId);
                    var msg = PlayerDB.AddMessage((ulong)playerId, account.Id, WebsocketEvents.MessageType.RoomCoOwnerInvited, ((int)role).ToString(), roomId, null);
                    if (msg != null) Notifications.SendToPlayer((ulong)playerId, JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(msg)));
                    RoleOffers.Pushed(roomId, (ulong)playerId);
                }
                catch (Exception ex) { Console.WriteLine($"[room-role] invite notification failed: {ex.Message}"); }
            }

            // The client re-GETs /roles right after; return the updated list so it reflects immediately.
            return RoomWriteEnvelope(roomId);
        }

        private IActionResult ResolveRoleInvite(ulong roomId, ulong accountId, bool accept)
        {
            var pending = RoomDB.PendingRoleInvite(roomId, (long)accountId);
            if (pending == RoomDBClasses.Role_data.None) return StatusCode(404, "");
            bool ok = RoomDB.ResolveRoleInvite(roomId, (long)accountId, accept);
            Console.WriteLine($"[room-role] {accountId} {(accept ? "ACCEPTED" : "declined")} the {pending} invite on room {roomId} -> {ok}");
            // The invite is answered: its notice leaves the list. (Accepting used to post a second
            // "You are added as Co-Owner" notice with its own Reject / Got it - the player had just
            // said yes, so it was one more thing to dismiss.)
            PlayerDB.DeleteRoomRoleMessages(accountId, roomId);
            if (ok && accept) Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId), accountId);
            return RoomWriteEnvelope(roomId);
        }

        // The explicit accept / decline spellings, plus "myrole" (the reference lists GET
        // rooms/{id}/roles/myrole; a PUT there is the same accept).
        [HttpPut("rooms/{roomId}/roles/{playerId}/accept")]
        [HttpPost("rooms/{roomId}/roles/{playerId}/accept")]
        public IActionResult AcceptRoomRole(ulong roomId, long playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if ((ulong)playerId != account.Id) return StatusCode(403, "");
            return ResolveRoleInvite(roomId, account.Id, accept: true);
        }

        [HttpPut("rooms/{roomId}/roles/{playerId}/decline")]
        [HttpPost("rooms/{roomId}/roles/{playerId}/decline")]
        [HttpDelete("rooms/{roomId}/roles/{playerId}/invite")]
        public IActionResult DeclineRoomRole(ulong roomId, long playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if ((ulong)playerId == account.Id) return ResolveRoleInvite(roomId, account.Id, accept: false);
            // A manager withdrawing an offer they made.
            if (!RoomDB.PlayerCanManageRoles(roomId, account.Id)) return StatusCode(403, "");
            RoomDB.ResolveRoleInvite(roomId, playerId, accept: false);
            PlayerDB.DeleteRoomRoleMessages((ulong)playerId, roomId);
            return RoomWriteEnvelope(roomId);
        }

        [HttpGet("rooms/{roomId}/roles/myrole")]
        public IActionResult MyRoomRole(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            return Ok((int)(RoomDB.PlayerRoleOn(room, account.Id) ?? RoomDBClasses.Role_data.None));
        }

        [HttpPut("rooms/{roomId}/roles/myrole")]
        [HttpPost("rooms/{roomId}/roles/myrole")]
        public IActionResult SetMyRoomRole(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return ResolveRoleInvite(roomId, account.Id, accept: true);
        }

        // Anything else under /roles: log the shape so an unknown client spelling names itself.
        [Route("rooms/{roomId}/roles/{**rest}")]
        public IActionResult UnhandledRoleRoute(ulong roomId, string rest)
        {
            string body = "";
            try { if (Request.HasFormContentType) body = string.Join("&", Request.Form.Select(kv => kv.Key + "=" + kv.Value)); } catch { }
            Console.WriteLine($"[room-role] UNHANDLED {Request.Method} rooms/{roomId}/roles/{rest}{Request.QueryString} body='{body}'");
            return StatusCode(404, "");
        }

        [HttpGet("rooms/base")]
        public ContentResult BaseRooms()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            List<RoomDBClasses.RoomRoot> room = RoomDB.Search("#base", isBase: true);
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(StripUnservableSaves(VisibleTo(room, ViewerId()))),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("rooms/{roomId}/clone")]
        public ContentResult CloneRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string RoomName = HttpContext.Request.Form["name"];
            if (string.IsNullOrEmpty(RoomName))
            {
                return Utils.Error("You must enter a name for your room.");
            }
            var checkifroomexists = RoomDB.GetRoomByName(RoomName);
            if (checkifroomexists != null)
            {
                return Utils.Error("A room with that name already exists!");
            }
            bool CheckSwears = !string.IsNullOrEmpty(RoomName) && Sanitize.ContainsSwears(RoomName);
            if (CheckSwears)
            {
                return Utils.Error("Room name contains swear words!");
            }
            // Anyone could clone ANY room (unpublished drafts included) and end up owning a copy.
            // Only the room's own creator/co-owners, or anyone when the owner allowed cloning on a
            // room others can see.
            var source = RoomDB.GetRoom(roomId);
            if (source == null) return Utils.Error("This room does not exist!");
            bool mineToClone = source.CreatorAccountId == account.Id || RoomDB.IsCoOwner(source, account.Id) || PlayerDB.CheckDevFlag(account.Id);
            bool template = RoomDB.RoomHasTag(source, "base") || source.IsRRO;   // the "create a room" templates
            if (!mineToClone && !template && !(source.CloningAllowed && RoomDB.CanPlayerSeeRoom(source, account.Id)))
            {
                Console.WriteLine($"[rooms] {account.Id} tried to clone room {roomId} ({source.Name}) without permission - refused");
                return Utils.Error("This room can't be cloned.");
            }
            ulong? temp = RoomDB.CloneRoom(roomId, RoomName, account.Id);
            if (temp == null)
            {
                return Utils.Error("You can't clone this room!");
            }
            var NewRoom = RoomDB.GetRoom(temp.Value);
            if (NewRoom == null)
            {
                Console.WriteLine($"[Room_server_room_clone] get_room({temp.Value}) returned null");
            }
            Console.WriteLine($"[Room_server_room_clone] clone_room returned id: {temp}");

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    error = "",
                    value = NewRoom
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // set room stuff

        [HttpPut("rooms/{roomId}/name")]
        public ContentResult SetRoomName(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string NewRoomName = HttpContext.Request.Form["name"].ToString();

            RoomDBClasses.RoomRoot? room_data = RoomDB.GetRoom(roomId);
            Uri.UnescapeDataString(NewRoomName);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }

            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            if (room_data.IsDorm == true)
            {
                return Utils.ErrorRoom("Rooms.Dorm", "You cannot edit your dorm room's name!");
            }

            if (NewRoomName.Contains(" "))
            {
                return Utils.ErrorRoom("Rooms.NoSpaces", "Your room name must not contain spaces!");
            }

            if (Regex.IsMatch(NewRoomName, @"[^\u0000-\u007F]"))
            {
                return Utils.ErrorRoom("Rooms.NoUnicodeCharacters", "Your room name contains Unicode characters!");
            }

            bool CheckSwears = !string.IsNullOrEmpty(NewRoomName) && Sanitize.ContainsSwears(NewRoomName);
            if (CheckSwears)
            {
                return Utils.ErrorRoom("Rooms.NoSwearWords", "Your room name contains swear words!");
            }

            RoomDBClasses.RoomRoot? temp = RoomDB.SetRoomName(roomId, NewRoomName);
            RoomDBClasses.RoomRoot? UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DuplicateName", "A room with the same name already exists!");
            }
        }

        [HttpPut("rooms/{roomId}/description")]
        public ContentResult SetRoomDescriptionAPI(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string NewDescription = HttpContext.Request.Form["description"].ToString();
            Uri.UnescapeDataString(NewDescription);

            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomId);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            bool CheckSwears = !string.IsNullOrEmpty(NewDescription) && Sanitize.ContainsSwears(NewDescription);
            if (CheckSwears)
            {
                return Utils.ErrorRoom("Rooms.NoSwearWords", "Your room description contains swear words!");
            }

            RoomDBClasses.RoomRoot temp = RoomDB.SetRoomDescription(roomId, NewDescription);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [HttpPut("rooms/{roomId}/accessibility")]
        public ContentResult SetRoomAccessibility(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string NewAccessibilityString = HttpContext.Request.Form["accessibility"].ToString();
            RoomDBClasses.RoomAccessibility accessibility = RoomDBClasses.RoomAccessibility.Private;
            if (int.TryParse(NewAccessibilityString, out int accessibilityValue) && Enum.IsDefined(typeof(RoomDBClasses.RoomAccessibility), accessibilityValue))
            {
                accessibility = (RoomDBClasses.RoomAccessibility)accessibilityValue;
            }
            else
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "",
                        value = (object?)null
                    }),
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }
            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomId);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            RoomDBClasses.RoomRoot temp = RoomDB.SetRoomAccessibility(roomId, accessibility);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        // Transfer a room to a new owner (reference rooms PUT /rooms/{id}/creator): the new owner's
        // role entry becomes Creator (255), the old owner's becomes CoOwner (30), and
        // CreatorAccountId - the field every gate and "rooms I made" list reads - moves with it.
        // Owner only (a co-owner cannot give a room away), and a dorm can never be transferred.
        [HttpPut("rooms/{roomId}/creator")]
        [HttpPost("rooms/{roomId}/creator")]
        public ContentResult TransferRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var f = RoomModeration.Fields(Request);
            ulong target = RoomModeration.ULong(f, "accountId", "id", "playerId", "newOwnerId");
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            bool isOwner = room.CreatorAccountId == account.Id || PlayerDB.CheckDevFlag(account.Id);
            if (!isOwner) return Utils.ErrorRoom("Rooms.PermissionDenied", "Only the owner can transfer this room!");
            if (room.IsDorm) return Utils.ErrorRoom("Rooms.PermissionDenied", "A dorm cannot be transferred!");
            if (target == 0 || PlayerDB.GetAccountById(target) == null) return Utils.ErrorRoom("Rooms.InvalidAccount", "You must name a valid player!");
            if (target == room.CreatorAccountId) return Utils.ErrorRoom("Rooms.InvalidAccount", "That player already owns this room!");

            var stored = RoomDB.col.FindOne(x => x.RoomId == roomId);
            if (stored == null) return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            ulong previous = stored.CreatorAccountId;
            stored.Roles ??= new List<RoomDBClasses.RoleClass>();
            stored.Roles.RemoveAll(x => (ulong)x.AccountId == target);
            stored.Roles.Add(new RoomDBClasses.RoleClass { AccountId = (long)target, Role = RoomDBClasses.Role_data.Creator, InvitedRole = 0, LastChangedByAccountId = (long)account.Id });
            var old = stored.Roles.FirstOrDefault(x => (ulong)x.AccountId == previous);
            if (old != null) { old.Role = RoomDBClasses.Role_data.CoOwner; old.InvitedRole = 0; old.LastChangedByAccountId = (long)account.Id; }
            else if (previous != 0) stored.Roles.Add(new RoomDBClasses.RoleClass { AccountId = (long)previous, Role = RoomDBClasses.Role_data.CoOwner, InvitedRole = 0, LastChangedByAccountId = (long)account.Id });
            stored.CreatorAccountId = target;
            RoomDB.col.Update(stored);
            Console.WriteLine($"[rooms] room {roomId} '{stored.Name}': ownership {previous} -> {target} (by {account.Id})");

            var updated = RoomDB.GetRoom(roomId);
            foreach (var pid in RoomPermissions.PlayersInRoom(roomId).Concat(new[] { previous, target }).Distinct())
                RoomPermissions.PushRoomUpdate(pid, updated);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = true, error = "", value = updated }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // A subroom's own visibility (reference rooms PUT /rooms/{id}/subrooms/{sub}/accessibility):
        // the client sends the RoomAccessibility NAME (accessibility=Private); the ordinal works too.
        // Owner only. Answers the updated ROOM so the client re-renders its subroom list.
        [HttpPut("rooms/{roomId}/subrooms/{subRoomId}/accessibility")]
        [HttpPost("rooms/{roomId}/subrooms/{subRoomId}/accessibility")]
        public ContentResult SetSubroomAccessibility(ulong roomId, ulong subRoomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var f = RoomModeration.Fields(Request);
            string raw = RoomModeration.Str(f, "accessibility", "Accessibility") ?? "";
            RoomDBClasses.RoomAccessibility acc;
            if (int.TryParse(raw, out var n) && Enum.IsDefined(typeof(RoomDBClasses.RoomAccessibility), n)) acc = (RoomDBClasses.RoomAccessibility)n;
            else if (!Enum.TryParse(raw, true, out acc)) return Utils.ErrorRoom("Rooms.InvalidArgument", "Unknown accessibility!");
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            if (room.CreatorAccountId != account.Id && !RoomDB.IsCoOwner(room, account.Id) && !PlayerDB.CheckDevFlag(account.Id))
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            var sub = room.SubRooms?.FirstOrDefault(s => s.SubRoomId == subRoomId);
            if (sub == null) return Utils.ErrorRoom("Rooms.DoesntExist", "This subroom does not exist!");
            RoomDB.ModifySubroom(roomId, subRoomId, sub.Name ?? "", acc, sub.MaxPlayers);
            var updated = RoomDB.GetRoom(roomId);
            RoomPermissions.PushRoomUpdate(account.Id, updated);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = true, error = "", value = updated }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPut("rooms/{roomId}/image")]
        public ContentResult SetRoomImageName(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string NewImageName = HttpContext.Request.Form["imageName"].ToString();

            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomId);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            RoomDBClasses.RoomRoot temp = RoomDB.SetRoomImageName(roomId, NewImageName);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [HttpPost("rooms/{roomId}/subrooms/{subroomId}/data")]
        public async Task<ContentResult> SetRoomData(ulong roomId, ulong subroomId, [FromBody] RoomFileData request)
        {
            string authtoken = HttpContext.Request.Headers["Authorization"].ToString();

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

            string token = authtoken.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);

            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);

            // "platform" is normally numeric, but int.Parse here threw an UNHANDLED
            // FormatException -> HTTP 500 on any token carrying a name ("Standalone").
            // A room save is the one request you never want to lose, so accept either
            // shape and fall back to All rather than dropping the save on the floor.
            string currentplatformstr = jwt.Payload.TryGetValue("platform", out var platObj)
                ? platObj?.ToString() ?? "" : "";
            Platforms platformparsed;
            if (int.TryParse(currentplatformstr, out int currentplatform))
                platformparsed = Enum.IsDefined(typeof(Platforms), currentplatform) ? (Platforms)currentplatform : Platforms.All;
            else if (!Enum.TryParse(currentplatformstr, ignoreCase: true, out platformparsed))
                platformparsed = Platforms.All;

            // The subroom blob (the actual build) and the ROOM-level blob are two
            // different uploads. Both lines used to read SubRoomData, so RoomData was
            // silently discarded and subroomFilename was dead code.
            string subroomFilename = request.SubRoomData?.Filename ?? "";
            string? roomLevelBlob  = request.RoomData?.Filename;

            string RoomDesc = request.Description ?? "";

            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomId);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (!RoomDB.CanEditContent(room_data, account?.Id ?? 0))   // creator, co-owner, or an ACCEPTED contributor
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            // Pass the versions the client reported with this save. Without them every
            // save was recorded as 0/0/0 and the client treated the room as dirty
            // immediately, prompting to save on exit even when nothing had changed.
            // A save never changes the room's VISIBILITY (accessibility is the owner's setting).
            // What AutoPublish decides is whether this save goes LIVE now or is staged as an
            // unpublished save until the owner publishes it (publish_save) - published and
            // unpublished saves are kept apart, so visitors keep loading the published build.

            RoomDBClasses.RoomRoot room = RoomDB.SetRoomSubroomRoomFile(
                roomId,
                subroomId,
                subroomFilename,
                RoomDesc,
                account.Id,
                platformparsed,
                request.PersistenceVersion,
                request.OMVersion,
                request.UgcSubVersion,
                roomLevelBlob,
                request.AutoPublish,
                out var createdSave);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                // The client parses value as { room, subRoomDataSave } (see wire-shapes
                // subroom-data-save-endpoint). We used to hand back the bare room, so the
                // deserializer NRE'd on the missing pair, the promise rejected with the generic
                // "Failed to save room", and the room stayed dirty - every save after the first
                // one in an instance "failed" even though the blob had been stored.
                // subRoomDataSave is a camelCase projection with its OWN field set, not the
                // PascalCase CurrentSave embedded in the room.
                // The save just made - staged or published - not necessarily the live CurrentSave.
                var save = createdSave ?? RoomDB.GetSubRoom(roomId, subroomId)?.CurrentSave;
                object? subRoomDataSave = save == null ? null : new
                {
                    subRoomDataSaveId = (long)save.SubRoomDataSaveId,
                    subRoomId = (long)save.SubRoomId,
                    unityAssetId = save.UnityAssetId,
                    unityAsset = (string?)null,
                    unityAssetHash = (string?)null,
                    dataBlob = save.DataBlob ?? "",
                    dataBlobHash = request.SubRoomData?.Hash ?? "",
                    savedByAccountId = (long?)save.SavedByAccountId,
                    savedOnPlatform = (int)(save.SavedOnPlatform ?? Platforms.All),
                    savedOnDeviceClass = (int)(save.SavedOnDeviceClass ?? DeviceClasses.Screen),
                    description = save.Description,
                    createdAt = save.CreatedAt
                };
                Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdate(account.Id, UpdatedRoom);
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = (string?)null,
                        value = new { room = UpdatedRoom, subRoomDataSave }
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [HttpPut("rooms/{roomId}/cloning")]
        public ContentResult SetRoomCloning(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string? cloningAllowedStr = HttpContext.Request.Form["cloningAllowed"];
            if (!bool.TryParse(cloningAllowedStr, out bool cloningAllowed))
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomId);
            if (room_data.IsDorm == true)
            {
                return Utils.ErrorRoom("Rooms.DormNotAllowed", "You cannot edit that in this dorm.");
            }
            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            RoomDBClasses.RoomRoot temp = RoomDB.SetRoomCloning(roomId, cloningAllowed);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [HttpPut("rooms/{roomId}/restrictions")]
        [HttpPut("rooms/{roomId}/v2/restrictions")]      // the 2025 client's spelling (404'd)
        [HttpPost("rooms/{roomId}/v2/restrictions")]
        public ContentResult SetRoomRestrictions(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            bool ParseBool(string key, bool defaultValue = false)
            {
                string? str = HttpContext.Request.Form[key];
                return bool.TryParse(str, out bool result) ? result : defaultValue;
            }

            bool supportsScreens = ParseBool("supportsScreens");
            bool supportsWalkVR = ParseBool("supportsWalkVR");
            bool supportsTeleportVR = ParseBool("supportsTeleportVR");
            bool supportsJuniors = ParseBool("supportsJuniors");

            RoomDBClasses.RoomRoot? room_data = RoomDB.GetRoom(roomId);
            if (room_data?.IsDorm == true)
            {
                return Utils.ErrorRoom("Rooms.DormNotAllowed", "You cannot edit that in dorms.");
            }
            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            RoomDBClasses.RoomRoot? temp = RoomDB.SetRoomRestrictions(roomId, supportsScreens, supportsWalkVR, supportsTeleportVR, supportsJuniors);
            RoomDBClasses.RoomRoot? UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [HttpPut("rooms/{roomId}/loadscreen")]
        public ContentResult SetRoomLoadingScreen(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string imageName = HttpContext.Request.Form["imageName"].ToString();
            string title = HttpContext.Request.Form["title"].ToString();
            string subtitle = HttpContext.Request.Form["subtitle"].ToString();

            RoomDBClasses.RoomRoot? room_data = RoomDB.GetRoom(roomId);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            if (!string.IsNullOrEmpty(title) && title.Length > 24)
            {
                return Utils.ErrorRoom("Room.LoadScreen.Title", "Title cannot exceed 24 characters!");
            }

            if (!string.IsNullOrEmpty(subtitle) && subtitle.Length > 150)
            {
                return Utils.ErrorRoom("Room.LoadScreen.Subtitle", "Subtitle cannot exceed 150 characters!");
            }

            RoomDBClasses.RoomRoot? temp = RoomDB.SetRoomLoadscreen(roomId, imageName, title, subtitle);
            RoomDBClasses.RoomRoot? UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [HttpPut("rooms/{roomId}/warning")]
        public ContentResult SetRoomWarning(ulong roomId, [FromForm] WarningMaskType warningMask)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomId);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            RoomDBClasses.RoomRoot temp = RoomDB.SetRoomWarningMask(roomId, warningMask);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomId);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [HttpPut("rooms/{roomId}/tags")]
        public IActionResult SetRoomTags(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var room = RoomDB.GetRoom(roomId);
            if (room == null)
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");

            if (!RoomDB.UserCanEditRoom(roomId, account.Id))
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");

            var restricted = new HashSet<string>
            {
                "rro",
                "recroomoriginal",
                "featured",
                "base"
            };

            List<string> Values(string name) => Request.HasFormContentType
                ? Request.Form[name].Select(t => Uri.UnescapeDataString(t ?? "").Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList()
                : new List<string>();
            var tags = Values("tag");
            var autoTags = Values("autoTag");
            string? primaryGenre = Values("primaryGenreTag").FirstOrDefault();

            if (tags.Count == 0 && autoTags.Count == 0 && primaryGenre == null)
                return Utils.ErrorRoom("Rooms.InvalidTag", "No valid tags provided.");
            if (tags.Any(t => restricted.Contains(t)) || (primaryGenre != null && restricted.Contains(primaryGenre)))
                return Utils.ErrorRoom("Rooms.TagNotAllowed", "One or more tags are not allowed!");
            // Derived tags are a closed set: what the client's own toggles produce.
            var allowedAuto = new HashSet<string> { "limitsv2", "beta", "rooms2", "r2" };
            if (autoTags.Any(t => !allowedAuto.Contains(t)))
                return Utils.ErrorRoom("Rooms.TagNotAllowed", "One or more tags are not allowed!");

            // A lone `tag` toggles; anything more is the whole-state save (see RoomDB.ApplyRoomTagEdit).
            bool isToggle = tags.Count == 1 && autoTags.Count == 0 && primaryGenre == null;
            Console.WriteLine($"[rooms] {account.Id} tags on room {roomId}: {(isToggle ? "toggle " + tags[0] : $"set [{string.Join(",", tags)}] auto [{string.Join(",", autoTags)}] genre {primaryGenre ?? "-"}")}");
            var updatedRoom = RoomDB.ApplyRoomTagEdit(roomId, isToggle ? tags[0] : null, isToggle ? null : (tags.Count > 0 ? tags : null), autoTags, primaryGenre);
            if (updatedRoom == null)
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");

            return Ok(new
            {
                success = true,
                error = "",
                value = updatedRoom
            });
        }



        [HttpGet("rooms/favoritedby/me")]
        public ContentResult MyFavoritedRooms()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                // Was: JsonConvert.SerializeObject(...) wrapped around a method that
                // ALREADY returns serialized JSON, so the client received the string
                // "[]" (quoted) instead of the array []. Verified live - this endpoint
                // was returning "[]" and it is the source of the client's
                // `Malformed Response: '[]'` errors.
                Content = PlayerDB.GetFavoritedRooms(account.Id),
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        // Don't advertise a save whose .room blob we cannot actually serve.
        //
        // Symptom this fixes: joining a room ALONE failed with no subroom data, but
        // joining a friend already inside worked. Creating an instance makes the client
        // load the room from its save - it requests /cdn/room/<DataBlob>, which 404s
        // when the file is absent from Data/CDN/RoomBlobs (the cdn.rec.net fallback in
        // CDNController is dead - those blobs are no longer public). Joining an existing
        // instance never fetches the blob; state replicates over Photon from whoever is
        // already there. That is exactly why the same room worked via a friend.
        //
        // Rooms with no save at all (Park, dorms) were unaffected - they just load their
        // default Unity scene. So make an unservable save look like no save and let the
        // room fall back to that same scene.
        //
        // Scope: only changes rooms that are ALREADY broken. If the blob is present the
        // save is returned exactly as before. Self-correcting - drop the real .room files
        // into Data/CDN/RoomBlobs and the saves come back with no code change.
        //
        // Trade-off: an affected room loads its base scene instead of its saved build.
        // Joinable-but-plain beats unjoinable, and it is reversible.
        private static List<object> FilterUnservableSaves(dynamic? results, ulong roomId, ulong subroomId)
        {
            var servable = new List<object>();
            int dropped = 0;

            if (results != null)
            {
                foreach (var s in results)
                {
                    string? blob = (string?)s?.DataBlob;
                    if (BlobHasContent(blob)) servable.Add(s!);
                    else dropped++;
                }
            }

            if (dropped > 0)
            {
                Console.WriteLine($"[rooms/{roomId}/subrooms/{subroomId}/saves] dropped {dropped} save(s) " +
                                  "whose .room blob is missing or empty in Data/CDN/RoomBlobs - room will load its " +
                                  "default scene instead of failing to load at all");
            }

            return servable;
        }

        [HttpGet("rooms/{roomId}/subrooms/{subroomId}/saves")] // SCUFFED CODE fix later
        public ContentResult GetRoomSaves(ulong roomId, ulong subroomId, int unityAssetTarget = 0, int unityAssetVersion = 1, int skip = 0, int take = 20)
        {


            var save = RoomDB.GetRoomSaves(roomId, subroomId, skip, take);

            if (save == null)
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }

            var servable = FilterUnservableSaves(save.Results, roomId, subroomId);

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    Results = servable,
                    TotalResults = servable.Count
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("rooms/{roomId}/subrooms/{subroomId}/saves/no_unity_assets")] // SCUFFED CODE fix later
        public ContentResult GetRoomSaves2(ulong roomId, ulong subroomId, int unityAssetTarget = 0, int unityAssetVersion = 1, int skip = 0, int take = 20)
        {


            var save = RoomDB.GetRoomSaves(roomId, subroomId, skip, take);

            if (save == null)
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }

            var servable = FilterUnservableSaves(save.Results, roomId, subroomId);

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(new
                {
                    Results = servable,
                    TotalResults = servable.Count
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }


        // GetRoomSaveById returns a boxed object whose concrete type varies, so read
        // DataBlob off its JSON form rather than guessing at properties vs fields -
        // an earlier reflection-based version silently matched nothing and let the
        // blob through.
        // Single source of truth for "can the client actually download this blob?".
        // NOTE: existence alone is not enough - see BlobHasContent for why.
        private static bool BlobHasContent(string? blob)
            => Campus_live_rewrite.api.server.RoomBlobFilter.BlobHasContent(blob);

        private static bool SaveBlobIsServable(object save)
        {
            string? blob;
            try
            {
                blob = Newtonsoft.Json.Linq.JObject
                    .FromObject(save)["DataBlob"]?.ToString();
            }
            catch
            {
                return true;   // unknown shape - never block on it
            }

            return BlobHasContent(blob);
        }

        [HttpGet("rooms/{roomId}/subrooms/{subroomId}/saves/{subRoomDataSaveId}")]
        public IActionResult GetRoomSave(ulong roomId, ulong subroomId, ulong subRoomDataSaveId, [FromQuery] int unityAssetTarget, [FromQuery] int unityAssetVersion)
        {
            var save = RoomDB.GetRoomSaveById(roomId, subroomId, subRoomDataSaveId, unityAssetTarget, unityAssetVersion);

            // Same rule as everywhere else: a save whose .room blob is not in
            // Data/CDN/RoomBlobs cannot be loaded, so report it as absent rather than
            // handing back a DataBlob that will 404 at /cdn/room/<blob>.
            if (save != null && !SaveBlobIsServable(save))
            {
                Console.WriteLine($"[rooms/{roomId}/subrooms/{subroomId}/saves/{subRoomDataSaveId}] " +
                                  "blob missing or empty in Data/CDN/RoomBlobs - reporting save as absent");
                save = null;
            }

            if (save == null)
            {
                return new ContentResult()
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }

            return Ok(save);
        }


        // The "rising" carousel: the listable rooms someone is in RIGHT NOW, busiest first
        // (RecFlare's trending feed). Live presence filters here, so a quiet server shows an
        // empty carousel rather than a stale one. Was a hardcoded "[]" - not even the
        // { Results, TotalResults } page the client reads.
        [HttpGet("rooms/carousel/rising")]
        public ContentResult RisingRooms(int skip = 0, int take = 100)
        {
            var live = HeartbeatDB.ConnectedPlayerCountsByRoom();
            var ranked = live.Count == 0 ? new List<RoomDBClasses.RoomRoot>()
                : RoomDB.GetHotRooms()
                    .Where(r => IsListable(r) && live.GetValueOrDefault((long)r.RoomId) > 0)
                    .OrderByDescending(r => live.GetValueOrDefault((long)r.RoomId))
                    .ThenByDescending(HotScore).ThenBy(r => r.RoomId).ToList();
            var page = ranked.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToList();
            RoomDB.AttachSubRooms(page);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new Resultslist<RoomDBClasses.RoomRoot>
                {
                    Results = StripUnservableSaves(VisibleTo(page, ViewerId())),
                    TotalResults = ranked.Count
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Recommended rooms: listable rooms by engagement, as a BARE array (the client's
        // recommendation source reads a plain list). splitTestId/splitTestValue are ignored.
        // Was an empty array.
        [HttpGet("/Room_server/rooms/recommendations")]
        public ContentResult RecommendedRooms(int skip = 0, int take = 100)
        {
            var page = RoomDB.GetHotRooms().Where(IsListable)
                .OrderByDescending(HotScore).ThenBy(r => r.RoomId)
                .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToList();
            RoomDB.AttachSubRooms(page);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(StripUnservableSaves(VisibleTo(page, ViewerId()))),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        /// <summary>The Coach account - owner of the stock (seeded) rooms.</summary>
        private const ulong CoachAccountId = 1;

        /// <summary>RecFlare's engagement score: cheers x3 + favourites x2 + unique visitors.</summary>
        private static long HotScore(RoomDBClasses.RoomRoot r) =>
            (r.Stats?.CheerCount ?? 0) * 3L + (r.Stats?.FavoriteCount ?? 0) * 2L + (r.Stats?.VisitorCount ?? 0);

        /// <summary>May appear in a browse feed at all: not a dorm, public, not opted out of lists.</summary>
        private static bool IsListable(RoomDBClasses.RoomRoot r) =>
            !r.IsDorm && !r.ExcludeFromLists && r.Accessibility == RoomDBClasses.RoomAccessibility.Public;



        [HttpGet("featuredrooms/current")]
        public ContentResult CurrentFeaturedRooms()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            /*
              Response:
              {
                  "FeaturedRoomGroupId": 1,
                  "name": "Featured Rooms",
                  "StartAt": "2025-12-01T11:01:00Z",
                  "EndAt": "9999-12-08T11:00:00Z",
                  "Rooms": [
                    {
                      "RoomId": 7243703403195289000,
                      "RoomName": "WinterAlmonds",
                      "ImageName": "96w7irfsiucisrei9fsmhipqn.jpg",
                      "IsRecRoomApproved": false,
                      "ExcludeFromLists": false,
                      "ExcludeFromSearch": false
                    }
                  ]
                }
            */

            // Build the ONE shared featured group from FeaturedRoomsStore (admin-curated, same
            // list the website reads). Skips ids whose room no longer exists. Returned as an ARRAY
            // of groups (what the client expects - the old placeholder was an empty array too).
            var featuredRooms = new List<object>();
            foreach (var rid in Campus_live_rewrite.api.server.FeaturedRoomsStore.GetIds())
            {
                var r = RoomDB.GetRoom(rid);
                if (r == null || r.IsDorm) continue;
                featuredRooms.Add(new
                {
                    RoomId = r.RoomId,
                    RoomName = r.Name ?? "",
                    ImageName = r.ImageName ?? "",   // client's DTO wants a string, never null
                    IsRecRoomApproved = r.IsRecRoomApproved,
                    ExcludeFromLists = false,
                    ExcludeFromSearch = false,
                });
            }

            // A SINGLE group object (FeaturedRoomGroupDto), NOT an array - that's what the client's
            // deserializer for this call expects (confirmed against the reference server). Wrapping
            // it in an array made the client fail to parse it, so the in-game Featured Room was blank.
            var group = new
            {
                FeaturedRoomGroupId = 1,
                name = "Featured Rooms",
                StartAt = "2025-01-01T00:00:00Z",
                EndAt = "9999-12-31T00:00:00Z",
                Rooms = featuredRooms,
            };

            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(group),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("rooms/{roomid}/subrooms")]
        public ContentResult CreateSubroomForRoom(ulong roomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string SubroomName = HttpContext.Request.Form["name"].ToString();
            Uri.UnescapeDataString(SubroomName);

            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomid);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }
            if (room_data.IsDorm == true)
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You cannot create a subroom for your dorm room!");
            }

            bool CheckSwears = !string.IsNullOrEmpty(SubroomName) && Sanitize.ContainsSwears(SubroomName);
            if (CheckSwears)
            {
                return Utils.ErrorRoom("Rooms.NoSwearWords", "Your subroom name contains swear words!");
            }

            RoomDBClasses.RoomRoot temp = RoomDB.CreateSubroomForRoom(roomid, SubroomName);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomid);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [HttpDelete("rooms/{roomid}/subrooms/{subroomid}")]
        public ContentResult DeleteSubroom(ulong roomid, ulong subroomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomid);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            bool success = RoomDB.DeleteSubroom(roomid, subroomid);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomid);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        // POST rooms/{id}/subrooms/{sid}/clone - a copy of the subroom (fresh SubRoomId, same
        // scene / settings / saves) in the same room. Owner or co-owner. Answers the updated ROOM
        // in the envelope, like create/delete - the client re-renders its subroom list from value.
        [HttpPost("rooms/{roomid}/subrooms/{subroomid}/clone")]
        public ContentResult CloneSubroom(ulong roomid, ulong subroomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room_data = RoomDB.GetRoom(roomid);
            if (room_data == null) return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            if (room_data.IsDorm == true) return Utils.ErrorRoom("Rooms.PermissionDenied", "You cannot clone a dorm subroom!");
            var cloned = RoomDB.CloneSubroom(roomid, subroomid);
            if (cloned == null) return Utils.ErrorRoom("Rooms.DoesntExist", "This subroom does not exist!");
            Console.WriteLine($"[rooms] {account!.Id} cloned subroom {subroomid} of room {roomid} -> {cloned.SubRoomId}");
            var updated = RoomDB.GetRoom(roomid);
            Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdate(account.Id, updated);
            return new ContentResult { Content = JsonConvert.SerializeObject(new { success = true, error = "", value = updated }), ContentType = "application/json", StatusCode = 200 };
        }

        // PUT rooms/{id}/max_player_calculation_mode - form `maxPlayerCalculationMode`, the enum NAME
        // (AllSubrooms 0 / OnlyEntrySubrooms 1, case-insensitive) or its ordinal. Owner / co-owner
        // only. A stored setting: MaxPlayers is not recomputed. Answers the room in the PascalCase
        // {Value, Success, Error, error_id} envelope (reference), a rejection as 200 + Success=false.
        // Was a stub that acked and stored nothing, so the setting never stuck.
        [HttpPut("rooms/{roomid}/max_player_calculation_mode")]
        [HttpPost("rooms/{roomid}/max_player_calculation_mode")]
        public ContentResult SetMaxPlayerCalculationMode(ulong roomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            ContentResult Env(object? value, string? err) => new ContentResult { Content = JsonConvert.SerializeObject(new { Value = value, Success = err == null, Error = err, error_id = (string?)null }), ContentType = "application/json", StatusCode = 200 };
            var room = RoomDB.GetRoom(roomid);
            if (room == null) return Env(null, "This room does not exist!");
            if (room.CreatorAccountId != account!.Id && !RoomDB.IsCoOwner(room, account.Id)) return Env(null, "You are not the owner of this room!");
            var f = Campus_live_rewrite.api.server.RoomModeration.Fields(Request);
            string raw = (Campus_live_rewrite.api.server.RoomModeration.Str(f, "maxPlayerCalculationMode", "MaxPlayerCalculationMode", "mode") ?? "").Trim();
            int mode = raw.ToLowerInvariant() switch { "allsubrooms" => 0, "onlyentrysubrooms" => 1, "0" => 0, "1" => 1, _ => -1 };
            if (mode < 0) return Env(null, "You must provide a valid maxPlayerCalculationMode!");
            var updated = RoomDB.SetMaxPlayerCalculationMode(roomid, mode);
            Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdate(account.Id, updated);
            return Env(updated, null);
        }

        // POST rooms/{id}/subrooms/{sid}/move - reorder a subroom in the room's list. The
        // position comes as index / newIndex / position (0-based) or direction (up/down). Shape
        // unpinned; every field is logged so it can be.
        [HttpPost("rooms/{roomid}/subrooms/{subroomid}/move")]
        [HttpPut("rooms/{roomid}/subrooms/{subroomid}/move")]
        public ContentResult MoveSubroom(ulong roomid, ulong subroomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room_data = RoomDB.GetRoom(roomid);
            if (room_data == null) return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            var f = Campus_live_rewrite.api.server.RoomModeration.Fields(Request);
            Console.WriteLine($"[rooms] {account!.Id} move subroom {subroomid} of {roomid}: {Campus_live_rewrite.api.server.RoomModeration.Dump(f)}");

            // `newRoomId` = move the subroom INTO another room (the reference's route). The caller
            // must manage both rooms. Answers the SOURCE room in the PascalCase
            // {Value, Success, Error, error_id} envelope - not the lowercase one the reorder below
            // (and the other subroom writes) answer; the client reads this one with another decoder.
            if (f.ContainsKey("newRoomId"))
            {
                ContentResult Refuse(string msg) => new ContentResult { Content = JsonConvert.SerializeObject(new { Value = (object?)null, Success = false, Error = msg, error_id = (string?)null }), ContentType = "application/json", StatusCode = 200 };
                long newRoomId = Campus_live_rewrite.api.server.RoomModeration.Long(f, "newRoomId");
                if (newRoomId <= 0) return Refuse("You must provide a valid newRoomId!");
                if ((ulong)newRoomId == roomid) return Refuse("This subroom is already in that room!");
                var target = RoomDB.GetRoom((ulong)newRoomId);
                if (target == null) return Refuse("The destination room does not exist!");
                if (target.CreatorAccountId != account.Id && !RoomDB.IsCoOwner(target, account.Id))
                    return Refuse("You are not the owner of the destination room!");
                var (moved, why, newSub) = RoomDB.MoveSubroomToRoom(roomid, subroomid, (ulong)newRoomId);
                if (moved == null)
                    return Refuse(why == "last_subroom" ? "You can't move a room's only subroom!" : "This subroom does not exist!");
                Console.WriteLine($"[rooms] {account.Id} moved subroom {subroomid} of {roomid} -> room {newRoomId} as subroom {newSub}");
                Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdate(account.Id, moved);
                var movedTarget = RoomDB.GetRoom((ulong)newRoomId);
                if (movedTarget != null) Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdate(account.Id, movedTarget);
                return new ContentResult { Content = JsonConvert.SerializeObject(new { Value = moved, Success = true, Error = (string?)null, error_id = (string?)null }), ContentType = "application/json", StatusCode = 200 };
            }

            string[] idxKeys = { "index", "newIndex", "position", "toIndex", "targetIndex" };
            long idx = idxKeys.Any(k => f.ContainsKey(k)) ? Campus_live_rewrite.api.server.RoomModeration.Long(f, idxKeys) : -1;
            string dir = (Campus_live_rewrite.api.server.RoomModeration.Str(f, "direction", "dir") ?? "").ToLowerInvariant();
            var subs = room_data.SubRooms ?? new List<RoomDBClasses.SubRoom>();
            int cur = subs.FindIndex(x => x.SubRoomId == subroomid);
            if (cur < 0) return Utils.ErrorRoom("Rooms.DoesntExist", "This subroom does not exist!");
            int to = idx >= 0 ? (int)Math.Min(idx, subs.Count - 1) : dir == "up" ? Math.Max(0, cur - 1) : dir == "down" ? Math.Min(subs.Count - 1, cur + 1) : cur;
            if (to != cur)
            {
                var item = subs[cur]; subs.RemoveAt(cur); subs.Insert(to, item);
                RoomDB.ReorderSubrooms(roomid, subs.Select(x => x.SubRoomId).ToList());
            }
            var updated = RoomDB.GetRoom(roomid);
            return new ContentResult { Content = JsonConvert.SerializeObject(new { success = true, error = "", value = updated }), ContentType = "application/json", StatusCode = 200 };
        }

        // POST rooms/{id}/subrooms/{sid}/publish_save - make the save named by the
        // subRoomDataSaveId form field the one players load (its CurrentSave). Saves here are
        // never staged, so this is the "restore an older save" half of the reference's route.
        // Owner-only, like the reference. Answers the updated ROOM in the envelope.
        [HttpPost("rooms/{roomid}/subrooms/{subroomid}/publish_save")]
        public ContentResult PublishSubroomSave(ulong roomid, ulong subroomid)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room_data = RoomDB.GetRoom(roomid);
            if (room_data == null) return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            var f = Campus_live_rewrite.api.server.RoomModeration.Fields(Request);
            long saveId = Campus_live_rewrite.api.server.RoomModeration.Long(f, "subRoomDataSaveId", "SubRoomDataSaveId", "saveId", "id");
            if (saveId <= 0) return Utils.ErrorRoom("Rooms.InvalidSave", "You must provide a valid save!");
            if (!RoomDB.PublishSubroomSave(roomid, subroomid, (ulong)saveId))
                return Utils.ErrorRoom("Rooms.DoesntExist", "That save does not exist!");
            Console.WriteLine($"[rooms] {account!.Id} published save {saveId} on subroom {subroomid} of room {roomid}");
            var updated = RoomDB.GetRoom(roomid);
            Campus_live_rewrite.api.server.RoomPermissions.PushRoomUpdate(account.Id, updated);
            return new ContentResult { Content = JsonConvert.SerializeObject(new { success = true, error = "", value = updated }), ContentType = "application/json", StatusCode = 200 };
        }

        [HttpPut("rooms/{roomid}/subrooms/{subroomid}/modify")]
        public ContentResult ModifySubroom(ulong roomid, ulong subroomid, [FromForm] string name, RoomDBClasses.RoomAccessibility accessibility, int maxPlayers = 32)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            RoomDBClasses.RoomRoot room_data = RoomDB.GetRoom(roomid);

            if (room_data == null)
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
            if (room_data.CreatorAccountId != account?.Id && !RoomDB.IsCoOwner(room_data, account?.Id ?? 0))
            {
                return Utils.ErrorRoom("Rooms.PermissionDenied", "You are not the owner of this room!");
            }

            bool success = RoomDB.ModifySubroom(roomid, subroomid, name, accessibility, maxPlayers);
            RoomDBClasses.RoomRoot UpdatedRoom = RoomDB.GetRoom(roomid);
            if (UpdatedRoom != null)
            {
                return new ContentResult()
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        error = "",
                        value = UpdatedRoom
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return Utils.ErrorRoom("Rooms.DoesntExist", "This room does not exist!");
            }
        }

        [Route("unity_assets/{guid}/{target}/{version}")]
        public IActionResult GetUnityAssets(string guid, int target, int version)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                UnityAssetId = guid,
                Target = target,
                Version = version,
                Filename = $"{guid}.assetbundle",
                Hash = (string?)null
            });
        }

        // Rooms 2.0 rooms resolve their pre-baked mesh assetbundles here. Rec Room generated those
        // bundles at publish time on its own backend, so we DON'T have them for imported (or most)
        // rooms - and returning 404 hard-fails the load with "Error downloading room assets (Code
        // 8715)". The client can BAKE geometry at runtime from the raw shape data in the .room blob
        // (GatherBakedPrimitiveDataJob / GatherBakedSplineDataJob), so the right answer for an id we
        // have no baked bundles for is a VALID, EMPTY result - never a 404. When we DO have a cached
        // BakedBulk/<id>_assets.json (base rooms), we return it.
        [Route("/Room_server/unity_assets/baked/bulk")]
        public async Task<IActionResult> GetUnityAssetsBakedBulk()
        {
            // The request carries one or more asset ids. Accept every shape the client might use:
            // repeated/comma form field(s), query, or a JSON body (array, {ids:[...]}, or {id:...}).
            var ids = new List<string>();
            void AddCsv(string? s) { if (!string.IsNullOrWhiteSpace(s)) ids.AddRange(s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())); }

            try
            {
                if (Request.HasFormContentType)
                {
                    foreach (var k in new[] { "id", "ids", "Id", "Ids", "assetId", "assetIds" })
                        foreach (var v in Request.Form[k]) AddCsv(v);
                }
            }
            catch { }
            foreach (var k in new[] { "id", "ids" })
                foreach (var v in Request.Query[k]) AddCsv(v);

            string rawBody = "";
            if (ids.Count == 0)
            {
                try
                {
                    Request.EnableBuffering();
                    Request.Body.Position = 0;
                    using var reader = new StreamReader(Request.Body, leaveOpen: true);
                    rawBody = await reader.ReadToEndAsync();
                    Request.Body.Position = 0;
                    if (!string.IsNullOrWhiteSpace(rawBody))
                    {
                        var node = JsonNode.Parse(rawBody);
                        if (node is JsonArray ja) foreach (var n in ja) { if (n != null) AddCsv(n.ToString()); }
                        else if (node is JsonObject jo)
                        {
                            foreach (var key in new[] { "ids", "id", "Ids", "Id", "assetIds", "assetId" })
                                if (jo[key] is JsonArray a2) foreach (var n in a2) { if (n != null) AddCsv(n.ToString()); }
                                else if (jo[key] != null) AddCsv(jo[key]!.ToString());
                        }
                        else if (node != null) AddCsv(node.ToString());
                    }
                }
                catch { }
            }

            ids = ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            Console.WriteLine($"[baked/bulk] method={Request.Method} ct={Request.ContentType} ids=[{string.Join(",", ids)}] rawBody={(rawBody.Length > 300 ? rawBody.Substring(0, 300) : rawBody)}");

            // Build one group per requested id: its cached baked bundles if we have them, else empty
            // (client runtime-bakes). If we couldn't parse any id, still return an empty array (200).
            string dir = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "BakedBulk");
            var groups = new List<object>();
            foreach (var id in ids)
            {
                JsonArray? baked = null;
                try
                {
                    string path = Path.Combine(dir, $"{id}_assets.json");
                    if (System.IO.File.Exists(path)) baked = JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonArray;
                }
                catch { }
                groups.Add(new
                {
                    UnityAssetId = id,
                    CreatedByAccountId = 1,
                    BakedUnityAssets = (object?)baked ?? new JsonArray(),
                    SubAssets = new List<object>(),
                    SourceAssetId = (string?)null
                });
            }

            return Ok(groups);
        }

        [HttpGet("/Room_server/publishState/configs")]
        public IActionResult GetPublishStateConfigs()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            /*
             {
                  "value": {
                    "UpdateMaxCount": 3,
                    "UpdateRollingWindowInDays": 365,
                    "UpdateExpirationInDays": 30,
                    "UpdateCooldownInDays": 45
                  },
                  "success": true,
                  "error_id": null,
                  "error": null
                } 
             */

            return Ok(new
            {
                value = new
                {
                    UpdateMaxCount = 3,
                    UpdateRollingWindowInDays = 365,
                    UpdateExpirationInDays = 30,
                    UpdateCooldownInDays = 45
                },
                success = true,
                error_id = (string?)null,
                error = (string?)null
            });
        }

        [HttpGet("/Room_server/dormroom/me")]
        public IActionResult GetMyDormRoom()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var FullAccount = PlayerDB.GetFullAccount(account.Id);

            return Ok(FullAccount.player_Extra.DormRoomID);
        }

        // Rooms like this one: the paged {Results, TotalResults} shape the room lists use, from
        // shared tags (then the hot list). Answered "[]" before - the client's "Malformed
        // Response: '[]'" and "Error refreshing rooms from source RoomSimilarity" every room load.
        [HttpGet("/Room_server/rooms/{roomId}/similar")]
        public ContentResult SimilarRooms(ulong roomId, int skip = 0, int take = 30)
        {
            var seed = RoomDB.GetRoom(roomId);
            var tags = new HashSet<string>((seed?.Tags ?? new()).Select(t => (t.Tag ?? "").ToLowerInvariant()).Where(t => t.Length > 0 && t != "rro" && t != "base" && t != "recroomoriginal"));
            var pool = RoomDB.GetHotRooms().Where(r => r.RoomId != roomId && !r.IsDorm && RoomDB.IsRoomListed(r) && !RoomDB.IsRecRoomOriginal(r)).ToList();
            var ranked = pool
                .Select(r => (room: r, shared: (r.Tags ?? new()).Count(t => tags.Contains((t.Tag ?? "").ToLowerInvariant()))))
                .OrderByDescending(x => x.shared)
                .ThenByDescending(x => x.room.Stats?.CheerCount ?? 0)
                .ThenByDescending(x => x.room.Stats?.VisitCount ?? 0)
                .Select(x => x.room)
                .ToList();
            var page = ranked.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToList();
            RoomDB.AttachSubRooms(page);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new RoomDBClasses.Resultslist<RoomDBClasses.RoomRoot>
                {
                    Results = StripUnservableSaves(VisibleTo(page, ViewerId())),
                    TotalResults = ranked.Count
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Room leaderboard slots (Room Settings -> Leaderboards). A configure / clear is
        // acknowledged; nothing stores slots yet. DELETE 405'd before.
        [HttpGet("/Room_server/rooms/{roomId}/leaderboards/{leaderboardId}")]
        public ContentResult RoomLeaderboard(ulong roomId, int leaderboardId) =>
            new ContentResult { Content = "[]", ContentType = "application/json", StatusCode = 200 };
        [HttpPost("/Room_server/rooms/{roomId}/leaderboards/{leaderboardId}")]
        [HttpPut("/Room_server/rooms/{roomId}/leaderboards/{leaderboardId}")]
        [HttpDelete("/Room_server/rooms/{roomId}/leaderboards/{leaderboardId}")]
        public IActionResult RoomLeaderboardWrite(ulong roomId, int leaderboardId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (!RoomDB.UserCanEditRoom(roomId, account.Id)) return StatusCode(403, "");
            Console.WriteLine($"[leaderboard] {Request.Method} room {roomId} slot {leaderboardId} by {account.Id}");
            return Ok(new { success = true, error = "", value = (object?)null });
        }

        // The room's XP settings write (Room Settings -> Experience) - echoes what GET answers.
        [HttpPost("/Room_server/rooms/{roomId}/experience")]
        [HttpPut("/Room_server/rooms/{roomId}/experience")]
        public IActionResult SetExperienceForRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            return Ok(new { DailyLimit = 1000000, Enabled = false, RoomExperienceId = 0 });
        }

        [HttpGet("/Room_server/rooms/{roomId}/experience")]
        public IActionResult GetExperienceForRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                DailyLimit = 1000000,
                Enabled = false,
                RoomExperienceId = 0
            });
        }

        [HttpGet("/Room_server/rooms/{roomId}/experience/player")]
        public IActionResult GetPlayerExperienceInRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                Enabled = false,
                Experience = (object?)null,
                ConcurrencyCode = (string?)null
            });
        }

        // Whether a player is banned from a room - read from the same bans the join gates refuse
        // on, so it answers what would actually happen. Was a constant false, which is why the
        // client offered "ban" on someone already banned and never showed "unban".
        [HttpGet("/Room_server/rooms/{roomId}/bans/{playerId}/isBanned")]
        public IActionResult GetIsBanned(ulong roomId, ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var room = RoomDB.GetRoom(roomId);
            return Ok(new
            {
                success = true,
                error = (string?)null,
                error_id = (string?)null,
                value = room != null && RoomDB.PlayerIsBannedFrom(room, playerId)
            });
        }

        private ContentResult BanEnvelope(RoomDBClasses.RoomBanDetails? ban, ulong roomId, string error = "") => new ContentResult
        {
            Content = JsonConvert.SerializeObject(new
            {
                success = ban != null,
                error,
                value = ban == null ? null : new
                {
                    RoomId = (long)roomId,
                    BannedPlayerId = ban.AccountId,
                    BanMask = 0,
                    BannedByAccountId = ban.BannedByAccountId,
                    CreatedAt = ban.BanStartTime
                }
            }),
            ContentType = "application/json",
            StatusCode = 200
        };

        // The room's active bans (Room Settings -> Moderation), newest first. camelCase entries
        // (reference: RoomBanEntryDto). Was a constant empty list.
        [HttpGet("/Room_server/rooms/{roomId}/bans")]
        public IActionResult GetRoomBans(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return Ok(Array.Empty<object>());
            return Ok(RoomDB.ActiveRoomBans(room).Select(b => new
            {
                accountId = b.AccountId,
                bannedByAccountId = b.BannedByAccountId,
                banStartTime = b.BanStartTime
            }));
        }

        // A room ban, except in a Rec Room Original (RecCenter...): there it is only a kick out of the instance the
        // player is in (the same block a modKick makes), because a permanent ban from the hub map locks the player
        // out of where everyone lands. The ban envelope the client expects is still answered, with a record that is
        // not stored. The admin panel's room editor can still ban anywhere.
        private RoomDBClasses.RoomBanDetails? BanOrKick(RoomDBClasses.RoomRoot room, ulong roomId, ulong target, ulong by, string reason, long seconds)
        {
            if (!RoomDB.IsRecRoomOriginal(room))
                return RoomDB.BanFromRoom(roomId, target, by, reason, seconds > 0 ? TimeSpan.FromSeconds(seconds) : null);
            if (room.CreatorAccountId == target) return null;
            var inst = HeartbeatDB.GetPlayerHeartbeat(target)?.roomInstance;
            if (inst != null && (ulong)inst.roomId == roomId)
                InstanceBlocks.Add(target, inst, by, TimeSpan.FromSeconds(Math.Max(60, ConfigCampus.RoomKickInstanceBlockSeconds)), reason.Length > 0 ? reason : "Removed by a moderator");
            Console.WriteLine($"[roomban] room {roomId} '{room.Name}' is a Rec Room Original: {by}'s ban on {target} became a kick");
            return new RoomDBClasses.RoomBanDetails { AccountId = (long)target, BannedByAccountId = (long)by, BanStartTime = DateTime.UtcNow, Reason = reason, Status = 0 };
        }

        // Ban a player from the room (form: id, banMask, and optionally reason / duration in
        // seconds). The room's owners, its moderators, or staff. The banned player gets the
        // ModerationKick frame their client leaves on, is moved out of the instance, and the
        // matchmaker refuses them until the ban is lifted. Answers {success,error,value:ban}.
        [HttpPost("/Room_server/rooms/{roomId}/bans")]
        [HttpPut("/Room_server/rooms/{roomId}/bans")]
        public IActionResult BanFromRoom(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var room = RoomDB.GetRoom(roomId);
            if (room == null) return BanEnvelope(null, roomId, "This room does not exist!");
            if (!RoomDB.CanModerateRoom(room, account.Id)) return StatusCode(403, "");

            var f = RoomModeration.Fields(Request);
            ulong target = RoomModeration.ULong(f, "id", "accountId", "playerId", "bannedPlayerId");
            if (target == 0) return BanEnvelope(null, roomId, "You must provide a valid player to ban!");
            if (target == account.Id) return BanEnvelope(null, roomId, "You cannot ban yourself!");
            if (room.CreatorAccountId == target || RoomDB.IsCoOwner(room, target))
                return BanEnvelope(null, roomId, "You cannot ban an owner of this room!");
            if (PlayerDB.CheckDevFlag(target) && !PlayerDB.CheckDevFlag(account.Id))
                return BanEnvelope(null, roomId, "You cannot ban a staff member!");

            long seconds = RoomModeration.Long(f, "duration", "durationSeconds", "banDuration");
            string reason = RoomModeration.Str(f, "reason", "message", "displayReason") ?? "";
            var ban = BanOrKick(room, roomId, target, account.Id, reason, seconds);
            if (ban == null) return BanEnvelope(null, roomId, "Could not ban that player.");

            bool isHostKick = room.CreatorAccountId == account.Id || RoomDB.IsCoOwner(room, account.Id);
            // isBan stays FALSE on the kick frame: `IsBan: true` is what the client renders as a ban
            // from the whole GAME (reference rooms POST /bans). The room ban itself is the stored row
            // the matchmaker refuses on (BannedFromRoom, 55); the frame only makes them leave.
            RoomModeration.RemoveFromRoom(target, roomId, 0, $"You have been banned from {room.Name}.", account.Id, isBan: false, isHostKick: isHostKick);
            ModerationLogDB.Record("roomban", target, account.Id, roomId, 0, seconds, ReportCategory.Moderator, reason);
            return BanEnvelope(ban, roomId);
        }

        // Lift a room ban. Answers the closed ban in the same envelope. Lifting also clears any
        // kick block the player has on the room's instances, so "unban" always means "let them
        // back in".
        [HttpDelete("/Room_server/rooms/{roomId}/bans/{playerId:long}")]
        [HttpPost("/Room_server/rooms/{roomId}/bans/{playerId:long}/unban")]
        public IActionResult UnbanFromRoom(ulong roomId, ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return BanEnvelope(null, roomId, "This room does not exist!");
            if (!RoomDB.CanModerateRoom(room, account.Id)) return StatusCode(403, "");
            int lifted = InstanceBlocks.Clear(playerId, (long)roomId);
            var ban = RoomDB.UnbanFromRoom(roomId, playerId, account.Id);
            if (ban == null && lifted == 0) return BanEnvelope(null, roomId, "This player is not banned from this room!");
            if (ban == null)
                ban = new RoomDBClasses.RoomBanDetails { AccountId = (long)playerId, BannedByAccountId = 0, BanStartTime = DateTime.UtcNow, Status = 2, UnbannedByAccountId = (long)account.Id, Reason = "" };
            ModerationLogDB.Record("roomunban", playerId, account.Id, roomId, 0, 0, ReportCategory.Moderator, "");
            return BanEnvelope(ban, roomId);
        }

        // The 2025 client's bulk forms of the two calls above: DELETE rooms/{id}/bans/bulk lifts
        // the bans on every id given (form / JSON / query: ids, accountIds, playerIds, id);
        // POST / PUT bans/bulk issues them. DELETE used to fall into the single-player route with
        // playerId = 0 and answer "This player is not banned from this room!" (Player.log:
        // LLCLDPPAGIJ) whatever was asked. Answers the ban envelope with a list as its value.
        [HttpDelete("/Room_server/rooms/{roomId}/bans/bulk")]
        [HttpPost("/Room_server/rooms/{roomId}/bans/bulk/unban")]
        public IActionResult UnbanBulk(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return BanEnvelope(null, roomId, "This room does not exist!");
            if (!RoomDB.CanModerateRoom(room, account.Id)) return StatusCode(403, "");
            var f = RoomModeration.Fields(Request);
            var ids = RoomModeration.ULongs(f, "ids", "accountIds", "playerIds", "bannedPlayerIds", "id", "accountId", "playerId");
            Console.WriteLine($"[roomban] bulk unban on room {roomId} by {account.Id}: {RoomModeration.Dump(f)}");
            if (ids.Count == 0) return BanEnvelope(null, roomId, "You must provide a valid player to unban!");
            var rows = new List<object>();
            foreach (var pid in ids)
            {
                int lifted = InstanceBlocks.Clear(pid, (long)roomId);
                var ban = RoomDB.UnbanFromRoom(roomId, pid, account.Id);
                if (ban == null && lifted == 0) continue;
                ModerationLogDB.Record("roomunban", pid, account.Id, roomId, 0, 0, ReportCategory.Moderator, "");
                rows.Add(new { RoomId = (long)roomId, BannedPlayerId = (long)pid, BanMask = 0, BannedByAccountId = ban?.BannedByAccountId ?? 0, CreatedAt = ban?.BanStartTime ?? DateTime.UtcNow });
            }
            if (rows.Count == 0) return BanEnvelope(null, roomId, "This player is not banned from this room!");
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = true, error = "", value = rows }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("/Room_server/rooms/{roomId}/bans/bulk")]
        [HttpPut("/Room_server/rooms/{roomId}/bans/bulk")]
        public IActionResult BanBulk(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return BanEnvelope(null, roomId, "This room does not exist!");
            if (!RoomDB.CanModerateRoom(room, account.Id)) return StatusCode(403, "");
            var f = RoomModeration.Fields(Request);
            var ids = RoomModeration.ULongs(f, "ids", "accountIds", "playerIds", "bannedPlayerIds", "id", "accountId", "playerId");
            Console.WriteLine($"[roomban] bulk ban on room {roomId} by {account.Id}: {RoomModeration.Dump(f)}");
            if (ids.Count == 0) return BanEnvelope(null, roomId, "You must provide a valid player to ban!");
            long seconds = RoomModeration.Long(f, "duration", "durationSeconds", "banDuration");
            string reason = RoomModeration.Str(f, "reason", "message", "displayReason") ?? "";
            bool isHostKick = room.CreatorAccountId == account.Id || RoomDB.IsCoOwner(room, account.Id);
            var rows = new List<object>();
            foreach (var pid in ids)
            {
                if (pid == account.Id || room.CreatorAccountId == pid || RoomDB.IsCoOwner(room, pid)) continue;
                if (PlayerDB.CheckDevFlag(pid) && !PlayerDB.CheckDevFlag(account.Id)) continue;
                var ban = BanOrKick(room, roomId, pid, account.Id, reason, seconds);
                if (ban == null) continue;
                RoomModeration.RemoveFromRoom(pid, roomId, 0, $"You have been banned from {room.Name}.", account.Id, isBan: false, isHostKick: isHostKick);
                ModerationLogDB.Record("roomban", pid, account.Id, roomId, 0, seconds, ReportCategory.Moderator, reason);
                rows.Add(new { RoomId = (long)roomId, BannedPlayerId = ban.AccountId, BanMask = 0, BannedByAccountId = ban.BannedByAccountId, CreatedAt = ban.BanStartTime });
            }
            if (rows.Count == 0) return BanEnvelope(null, roomId, "Could not ban that player.");
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new { success = true, error = "", value = rows }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("/Room_server/rooms/{roomId}/playerdata/me")]
        public IActionResult GetMyPlayerData(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var data = PlayerDB.GetPlayerData(account.Id, roomId);
            // The client expects a JSON body from this endpoint — 404 makes it treat the whole
            // room-join as "failed to fetch player state" and bounce. Return an empty payload
            // when there's no stored state yet (fresh player, never visited this room).
            return Ok(new
            {
                Data = data ?? ""
            });
        }

        [HttpPut("/Room_server/rooms/{roomId}/playerdata/me")]
        public IActionResult SetMyPlayerData(ulong roomId, [FromForm] string data)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            bool success = PlayerDB.SetPlayerData(account.Id, roomId, data);
            if (!success)
            {
                return BadRequest();
            }
            
            Console.WriteLine("Was updating playerdata successful: " + success);

            return NoContent();
        }

        [HttpGet("/Room_server/rooms/{roomId}/boosts/{playerId}/count")]
        public IActionResult GetBoostCountForRoom(ulong roomId, ulong playerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            
            return Ok("0");
        }

        [HttpGet("/Room_server/rooms/{roomId}/assets")]
        public IActionResult GetRoomAssets(ulong roomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            
            return Ok(new
            {
                success = true,
                error = (string?)null,
                error_id = (string?)null,
                value = Array.Empty<object>()
            });
        }

        // https://rooms.rec.net/rooms/1089513655118942017/snapshots/ade3aee2-e469-4445-728b-08de39979f5f/loadinfo?subRoomId=1384652020053214301
        [HttpGet("/Room_server/rooms/{roomId}/snapshots/{snapshotId}/loadinfo")]
        public IActionResult GetRoomAssets(ulong roomId, string snapshotId, [FromQuery] ulong subRoomId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var room = RoomDB.GetRoom(roomId);
            if (room == null) return NotFound();
            var subRoom = room.SubRooms?.FirstOrDefault(sr => sr.SubRoomId == subRoomId);
            
            return Ok(new
            {
                success = true,
                error = (string?)null,
                error_id = (string?)null,
                value = new
                {
                    Assets = Array.Empty<object>(),
                    RoomId = roomId,
                    SnapshotId = snapshotId,
                    SubRoom = new
                    {
                        SubRoomDataSaveId = subRoom?.CurrentSave?.SubRoomDataSaveId
                    },
                    SubRoomId = subRoomId,
                    UgcSubVersion = room.UgcSubVersion,
                    UgcVersion = room.UgcVersion
                }
            });
        }
    }
}
