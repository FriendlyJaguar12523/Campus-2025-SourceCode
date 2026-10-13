using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;
using Campus_live_rewrite.api.server.Classes.db_class;
// SavedImageTypeEnum / ImageAccessibility are nested in ImageMetadataDB, same as APIController.
using static ImageMetadataDB;
using MessageType = Campus_live_rewrite.api.WebsocketEvents.MessageType;

namespace Campus_live_rewrite.Controllers
{
    // Real data for the public website - the endpoints that replace data.js's demo content
    // (six invented rooms, picsum photo URLs, dicebear avatars, hardcoded "online friends").
    //
    // These are deliberately SEPARATE from the game endpoints rather than the site calling
    // /rooms/hot and /api/images/v3/... directly:
    //   - the game routes all sit behind the patch-header gate, which a browser cannot pass;
    //   - they return the client's DTOs (huge - room DataBlobs, save payloads, platform ids)
    //     where the site needs a handful of display fields;
    //   - they leak more than an anonymous web visitor should see.
    // So this projects the same DB reads down to a small, public-safe shape.
    //
    // Everything here treats "signed out" as valid: the site is public. Endpoints that need
    // an account (friends, notifications, "my" anything) say so and 401 instead.
    [ApiController]
    public class SiteContentController : ControllerBase
    {
        private const string SiteCookie = "campus_site";

        // Mirrors SiteController.CurrentUser. Kept local rather than shared through a base
        // class so the two controllers can be read independently.
        private player_info? CurrentUser()
        {
            string token = Request.Cookies[SiteCookie] ?? "";
            if (string.IsNullOrEmpty(token)) return null;

            // MUST require the "website" surface, exactly like SiteController.CurrentUser - the
            // website token is minted with surface="website" and DecodeToken now refuses website
            // tokens on every path that doesn't ask for them. Without this, every endpoint in
            // this controller (friends, notifications, profile, photos, rooms) 401s the site.
            string data = ClientSecurity.DecodeToken(token, requireSurface: "website");
            if (string.IsNullOrEmpty(data) || data.StartsWith("error_")) return null;

            try { return JsonConvert.DeserializeObject<player_info>(data); }
            catch { return null; }
        }

        private ulong ViewerId() => CurrentUser()?.Id ?? 0UL;

        // ---- shared projections -----------------------------------------------------------

        // Everything image-shaped on the site goes through /img/<name>, which ImagesController
        // already serves. Null when the account/room has no image, so the front-end can fall
        // back to its generated avatar rather than requesting a 404.
        // ---- what the website may say about WHERE something is ---------------------------
        // A dorm is a personal space and an unpublished (Private) room is a draft: the site never
        // names either - not in "Online · ^Room" status lines, not on a photo. Checked live on
        // every request, so a room that gets published starts showing up by name straight away.
        private static bool RoomIsShowable(RoomDBClasses.RoomRoot? room) =>
            room != null && !room.IsDorm && RoomDB.IsRoomPublished(room);

        /// <summary>The room a player is in, named for the site - null for a dorm / unpublished room.</summary>
        private static string? ShowableRoom(HeartbeatDB.RoomInstance? inst)
        {
            if (inst == null || string.IsNullOrWhiteSpace(inst.name) || inst.name == "not in a room.") return null;
            RoomDBClasses.RoomRoot? room = null;
            try { room = inst.roomId > 0 ? RoomDB.GetRoom((ulong)inst.roomId) : RoomDB.GetRoomByName(inst.name.TrimStart('^', '@')); } catch { }
            return RoomIsShowable(room) ? inst.name.TrimStart('^', '@') : null;
        }

        /// <summary>Where this player is, for the site - null offline, in a dorm or in an unpublished room.</summary>
        internal static string? ShowableRoomOf(ulong playerId)
        {
            try
            {
                var (online, inst) = HeartbeatDB.PeekPresence(playerId);
                return online ? ShowableRoom(inst) : null;
            }
            catch { return null; }
        }

        /// <summary>The room a photo was taken in, if the site may name it (published, not a dorm).</summary>
        private static RoomDBClasses.RoomRoot? PhotoRoom(long roomId)
        {
            if (roomId <= 0) return null;
            try { var r = RoomDB.GetRoom((ulong)roomId); return RoomIsShowable(r) ? r : null; } catch { return null; }
        }

        /// <summary>A photo's roomId as the site may send it: 0 while its room is a dorm or unpublished.</summary>
        private static long PhotoRoomId(long roomId) => PhotoRoom(roomId)?.RoomId is ulong id ? (long)id : 0;

        private static string? ImgUrl(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null : "/img/" + name;

        private static object RoomCard(RoomDBClasses.RoomRoot r, string? creatorName) => new
        {
            id = r.RoomId,
            name = r.Name,
            description = r.Description ?? "",
            image = ImgUrl(r.ImageName),
            creator = creatorName,
            creatorId = r.CreatorAccountId,
            visits = r.Stats?.VisitCount ?? 0,
            cheers = r.Stats?.CheerCount ?? 0,
            favorites = r.Stats?.FavoriteCount ?? 0,
            maxPlayers = r.MaxPlayers,
            approved = r.IsRecRoomApproved,
            // who's in the room right now - the "N playing now" chips on the site
            players = HeartbeatDB.CountInRoom(r.RoomId),
        };

        // Room rows carry a CreatorAccountId, not a name. Resolving one account per row would
        // be N queries per page, so the caller batches the distinct ids through this first.
        private static Dictionary<ulong, string> CreatorNames(IEnumerable<ulong> ids)
        {
            var map = new Dictionary<ulong, string>();
            foreach (var id in ids.Distinct())
            {
                if (id == 0) continue;
                var acc = PlayerDB.GetAccountById(id);
                if (acc != null)
                    map[id] = acc.DisplayName ?? acc.Username ?? "";
            }
            return map;
        }

        private List<object> ProjectRooms(IEnumerable<RoomDBClasses.RoomRoot?> rooms, int take)
        {
            ulong viewer = ViewerId();

            var visible = rooms
                .Where(r => r != null && !r.IsDorm && RoomDB.CanPlayerSeeRoom(r, viewer))
                .Select(r => r!)
                .Take(take)
                .ToList();

            var names = CreatorNames(visible.Select(r => r.CreatorAccountId));

            return visible
                .Select(r => RoomCard(r, names.TryGetValue(r.CreatorAccountId, out var n) ? n : null))
                .ToList();
        }

        // ---- rooms ------------------------------------------------------------------------

        // GET /api/site/rooms/hot?take=12
        // Replaces data.js `rooms` - the six hand-written Whisker Heights / Midnight Mews
        // entries that every visitor saw regardless of what was actually on the server.
        [HttpGet("/api/site/rooms/hot")]
        public IActionResult HotRooms(int take = 12)
        {
            take = Math.Clamp(take, 1, 50);
            try
            {
                return Ok(new { ok = true, results = ProjectRooms(RoomDB.GetHotRooms(), take) });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] hot rooms failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load rooms" });
            }
        }

        // GET /api/site/rooms/browse?tab=hot|rro|new&q=&skip=0&take=24
        // The website's Rooms page. hot = most visited, rro = the Rec Room Originals, new = latest
        // created; q filters by name (and creator). Public/visible rooms only, paged.
        [HttpGet("/api/site/rooms/browse")]
        public IActionResult BrowseRooms(string tab = "hot", string q = "", int skip = 0, int take = 24)
        {
            take = Math.Clamp(take, 1, 60);
            skip = Math.Max(0, skip);
            try
            {
                ulong viewer = ViewerId();
                IEnumerable<RoomDBClasses.RoomRoot> rooms = RoomDB.GetHotRooms()
                    .Where(r => r != null && !r.IsDorm && RoomDB.CanPlayerSeeRoom(r, viewer));

                tab = (tab ?? "hot").Trim().ToLowerInvariant();
                if (tab == "rro") rooms = rooms.Where(RoomDB.IsRecRoomOriginal);
                else if (tab == "new") rooms = rooms.Where(r => !RoomDB.IsRecRoomOriginal(r));

                q = (q ?? "").Trim().TrimStart('^');
                if (q.Length > 0)
                    rooms = rooms.Where(r => (r.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || (r.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));

                rooms = tab == "new"
                    ? rooms.OrderByDescending(r => r.CreatedAt)
                    : rooms.OrderByDescending(r => r.Stats?.VisitCount ?? 0).ThenByDescending(r => r.Stats?.CheerCount ?? 0);

                var all = rooms.ToList();
                var page = all.Skip(skip).Take(take).ToList();
                var names = CreatorNames(page.Select(r => r.CreatorAccountId));
                var results = page.Select(r => RoomCard(r, names.TryGetValue(r.CreatorAccountId, out var n) ? n : null)).ToList();
                return Ok(new { ok = true, results, total = all.Count, skip, take });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] browse rooms failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load rooms" });
            }
        }

        // GET /api/site/rooms/featured
        // The SAME admin-curated featured rooms the game shows (FeaturedRoomsStore), in the same
        // order. Public/visible rooms only. Empty list when nothing is featured (the section hides).
        [HttpGet("/api/site/rooms/featured")]
        public IActionResult FeaturedRooms(int take = 12)
        {
            try
            {
                ulong viewer = ViewerId();
                var rooms = new List<RoomDBClasses.RoomRoot>();
                foreach (var id in FeaturedRoomsStore.GetIds())
                {
                    var r = RoomDB.GetRoom(id);
                    if (r != null && !r.IsDorm && RoomDB.CanPlayerSeeRoom(r, viewer))
                        rooms.Add(r);
                    if (rooms.Count >= take) break;
                }
                var names = CreatorNames(rooms.Select(r => r.CreatorAccountId));
                var results = rooms
                    .Select(r => RoomCard(r, names.TryGetValue(r.CreatorAccountId, out var n) ? n : null))
                    .ToList();
                return Ok(new { ok = true, results });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] featured rooms failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load featured rooms" });
            }
        }

        // GET /api/site/rooms/{id} - the room.html detail page. {id} is the room NAME (how rooms
        // are shared/searched); a numeric slug still works as an account-... er, room id fallback.
        [HttpGet("/api/site/rooms/{id}")]
        public IActionResult Room(string id)
        {
            try
            {
                var room = RoomDB.GetRoomByName(id);
                if (room == null && ulong.TryParse(id, out var rid)) room = RoomDB.GetRoom(rid);
                // Dorms are personal spaces, not published rooms: never shown on the website.
                if (room == null || room.IsDorm || !RoomDB.CanPlayerSeeRoom(room, ViewerId()))
                    return NotFound(new { ok = false, error = "room not found" });

                var names = CreatorNames(new[] { room.CreatorAccountId });
                names.TryGetValue(room.CreatorAccountId, out var creator);

                // Photos taken in this room, newest first - the room page's gallery.
                // Same two filters as the profile feed: real camera photos only, and public
                // only - this page is reachable signed out, so it must never surface a
                // private photo regardless of what GetRoomImages returns.
                var photos = ImageMetadataDB.GetRoomPublicPhotos((int)room.RoomId, 48) ?? new();
                ulong viewerId = ViewerId();
                var photoAuthors = CreatorNames(photos.Select(i => i.PlayerId));
                var photoCards = photos.Select(i =>
                {
                    var acc = PlayerDB.GetAccountById(i.PlayerId);
                    return new
                    {
                        id = i.Id,
                        image = ImgUrl(i.ImageName),
                        description = i.Description,
                        cheers = i.CheerCount,
                        cheered = viewerId != 0 && ImageMetadataDB.HasUserCheered(i.Id, viewerId),
                        comments = i.CommentCount,
                        createdAt = i.CreatedAt,
                        taggedPlayers = ResolveTagged(i.TaggedPlayerIds),
                        author = acc == null ? null : new { id = acc.Id, username = acc.Username, display_name = acc.DisplayName, profile_image = ImgUrl(acc.profileImage) },
                    };
                }).ToList();

                return Ok(new
                {
                    ok = true,
                    room = RoomCard(room, creator),
                    subrooms = room.SubRooms?.Select(s => new { id = s.SubRoomId, name = s.Name }).ToList(),
                    photos = photoCards,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] room {id} failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load the room" });
            }
        }

        // ---- profiles ---------------------------------------------------------------------

        // GET /api/site/profile/{handle}   (handle = username, or a numeric account id)
        //
        // One call for the whole profile page. In-game this is 8 separate per-id fetches
        // (account, bio, privacy, progression, reputation, photo feed, showcase, visited) -
        // see the profile-panel notes. The site has no reason to pay eight round trips.
        [HttpGet("/api/site/profile/{handle}")]
        public IActionResult Profile(string handle)
        {
            try
            {
                // Resolve by USERNAME first, THEN fall back to a numeric account id. A purely
                // numeric USERNAME (e.g. "123", whose account id is actually 221) must resolve to
                // that user, not to account id 123 - the old id-first check got them confused.
                player_data? full = PlayerDB.GetFullAccountByName(handle);
                if (full == null && ulong.TryParse(handle, out var byId))
                    full = PlayerDB.GetFullAccount(byId);

                if (full?.player == null)
                    return NotFound(new { ok = false, error = "player not found" });

                ulong id = full.playerid;
                ulong viewer = ViewerId();

                // Block: if the profile owner has this viewer blocked (their own Ignored flag set
                // toward the viewer), the viewer may not see the profile. GetRelationships(owner)
                // projects PlayerID = the OTHER player and Ignored = the owner's own value, so a
                // set Ignored toward the viewer means the owner blocked them. Owner and the not-
                // signed-in (viewer 0) case are never blocked.
                if (viewer != 0 && viewer != id)
                {
                    // Only the OWNER's own block hides their profile. (Ignored != None also matched
                    // Remote - i.e. the viewer having blocked the owner - which locked the blocker
                    // out of the profile they'd need to unblock from.)
                    var ownerRel = FriendsDB.GetRelationships(id)?
                                            .FirstOrDefault(r => r.PlayerID == viewer);
                    if (ownerRel != null && FriendsDB.IsOwnBlock(ownerRel.Ignored))
                        return StatusCode(403, new
                        {
                            ok = false,
                            blocked = true,
                            error = "This Profile cannot be viewed at this time",
                        });
                }

                var progress = Progress.GetPlayerLevel(id);
                var rep = full.player.PlayerReputation ?? new mPlayerReputation();

                // Photo feed: their photos plus ones they're tagged in, filtered by the
                // requester so a private photo isn't exposed to a stranger.
                // GetPlayerFeed handles the accessibility filter (public-only unless you
                // are looking at yourself) but returns every image TYPE. Restrict to
                // ShareCamera so the photo tab shows photos the player actually took, not
                // their profile picture and room thumbnails repeated back at them.
                bool viewerIsFriend = viewer != 0 && viewer != id && AreFriends(viewer, id);
                var rawFeed = ImageMetadataDB.GetPlayerFeed(viewer, id, 0, 200, viewerIsFriend) ?? new();
                Console.WriteLine($"[site] profile {id} feed types: " +
                    string.Join(", ", rawFeed.GroupBy(i => i.SavedImageType).Select(g => $"{g.Key}={g.Count()}")));

                // "Photos the player took" = actual camera photos (ShareCamera), plus any untyped
                // (None) image, which is what the photo tab shows. Thumbnails (outfit/room/profile/
                // invention/event/loadscreen) are deliberately excluded so the tab isn't the
                // avatar and room covers repeated back. isPrivate drives the lock badge; for your
                // OWN profile private photos are included (GetPlayerFeed only hides others'
                // private), so a lock marks the ones only you can see.
                // Each photo carries two flags so the profile can split them across tabs:
                //   taken  = this player took it (PlayerId == id)
                //   tagged = this player is tagged in it (theirs OR someone else's photo)
                var photos = rawFeed
                    .Where(i => i.SavedImageType == SavedImageTypeEnum.ShareCamera
                             || i.SavedImageType == SavedImageTypeEnum.None)
                    .Take(96)
                    .Select(i => new
                    {
                        id = i.SavedImageId,
                        image = ImgUrl(i.ImageName),
                        cheers = i.CheerCount,
                        comments = i.CommentCount,
                        roomId = PhotoRoomId(i.RoomId),
                        description = i.Description,
                        createdAt = i.CreatedAt,
                        isPrivate = i.Accessibility != ImageAccessibility.Public,
                        visibility = i.FriendsOnly ? "friends" : (i.Accessibility == ImageAccessibility.Public ? "public" : "private"),
                        taken = i.PlayerId == id,
                        tagged = i.TaggedPlayerIds != null && i.TaggedPlayerIds.Contains(id),
                    })
                    .ToList();

                var rooms = ProjectRooms(RoomDB.GetRoomsByCreator(id) ?? new(), 12);

                // Presence is disclosed ONLY to a signed-in viewer. A signed-out visitor (viewer 0)
                // gets null, and the site hides the status line entirely - you can't see anyone's
                // online/offline state unless you're logged in.
                object? status = null;
                if (viewer != 0)
                {
                    var (isOnline, room) = HeartbeatDB.PeekPresence(id);
                    // when they were last seen heartbeating, so the site can say "last online 3h ago"
                    string? lastOnline = null;
                    try { lastOnline = HeartbeatDB.LastSeen(id); } catch { }
                    status = new
                    {
                        online = isOnline,
                        where = isOnline ? ShowableRoom(room) : null,
                        lastOnline = isOnline ? null : lastOnline,
                        // What they're listening to on Spotify, if their setting lets THIS viewer see it.
                        spotify = SpotifyLink.PayloadFor(id, viewer).GetAwaiter().GetResult(),
                    };
                }

                return Ok(new
                {
                    ok = true,
                    status,
                    user = new
                    {
                        id,
                        username = full.player.Username,
                        display_name = full.player.DisplayName,
                        profile_image = ImgUrl(full.player.profileImage),
                        banner_image = ImgUrl(string.IsNullOrEmpty(full.player.bannerImage) ? ConfigCampus.DefaultBannerImage : full.player.bannerImage),
                        bio = full.player_Extra?.Bio ?? "",
                        socials = SocialLinks.ProfileRows(id, full.player_Extra),
                        level = progress?.Level ?? full.player.Level,
                        xp = progress?.XP ?? full.player.XP,
                        created_at = full.player.created_at,
                        is_junior = full.player.IsJunior,
                    },
                    reputation = rep,
                    subscribers = PlayerDB.GetSubscriberIds(id).Count,
                    friends = FriendsDB.GetRelationships(id)?
                                       .Count(r => r.RelationshipType == RelationshipType.Friend) ?? 0,
                    photos,
                    rooms,
                    // Only meaningful when signed in; null tells the front-end to render the
                    // "Add friend" button in its neutral state rather than guessing.
                    relationship = viewer == 0 || viewer == id ? null : RelationshipWith(viewer, id),
                    // The viewer's own state toward this profile, so the buttons render truthfully.
                    subscribed = viewer != 0 && viewer != id && PlayerDB.GetSubscribedToIds(viewer).Contains(id),
                    favourite = viewer != 0 && viewer != id && (FriendsDB.GetRelationships(viewer)?
                                       .Any(r => r.PlayerID == id && r.Favorited != ReciprocalStatus.None) ?? false),
                    // "I have blocked this player" (the viewer's own part of the pair)
                    blockedByMe = viewer != 0 && viewer != id && FriendsDB.HasBlocked(viewer, id),
                    // the viewer's website-side extras for this friend
                    nickname = viewer != 0 && viewer != id ? SitePrefsDB.NicknameFor(viewer, id) : null,
                    pinned = viewer != 0 && viewer != id && SitePrefsDB.IsPinned(viewer, id),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] profile {handle} failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load the profile" });
            }
        }

        // The caller's OWN side of the pair - never the target's row. Reading the wrong side
        // inverts pending-request direction (a request you sent shows as received).
        private static string? RelationshipWith(ulong viewer, ulong target)
        {
            var rel = FriendsDB.GetRelationships(viewer)
                               ?.FirstOrDefault(r => r.PlayerID == target || r.OtherPlayerID == target);
            return rel?.RelationshipType.ToString();
        }

        // ---- friend actions from the website ----------------------------------------------
        // These run the SAME FriendsDB operations the game's /api/relationships/v2/* endpoints do,
        // but authenticate with the WEBSITE session (campus_site cookie via CurrentUser) instead
        // of the game bearer token - so a friend request sent from the site shows up in-game, and
        // vice-versa. Arg order mirrors the game controllers exactly: (otherPlayerId, myId).
        public class FriendActionBody { public ulong id { get; set; } }

        private IActionResult FriendAction(FriendActionBody? body, Func<ulong, ulong, RelationshipDetail?> op, string what)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null || body.id == 0 || body.id == me.Id)
                return BadRequest(new { ok = false, error = "invalid target" });
            // Light anti-spam: one friend action per account per 2s.
            if (!RateLimit.TryConsume($"sitefriend:{me.Id}", cooldownSeconds: 2, out int retry))
            {
                Response.Headers["Retry-After"] = retry.ToString();
                return StatusCode(429, new { ok = false, error = "Slow down a moment." });
            }
            try
            {
                // Caller is the FROM player (the sender), target is the TO player - the order the
                // reference/client expects. SendFriendRequest marks the FROM row FriendRequestSent,
                // so the sender's own relationship reads "Sent" and the profile stays on "Pending"
                // after a refresh. (Accept/Remove are symmetric, so the order is harmless there.)
                var rel = op(me.Id, body.id);
                Console.WriteLine($"[site] {me.Id} {what} {body.id} -> {rel?.RelationshipType.ToString() ?? "null"}");
                SiteNotifications.Notify(me.Id);
                SiteNotifications.Notify(body.id);
                return Ok(new { ok = true, relationship = rel?.RelationshipType.ToString() });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] friend {what} {me.Id}->{body.id} failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not update the friendship" });
            }
        }

        // The friends of a profile: All / Favourites, plus pending requests when it is your own.
        // Presence comes from the game hub (online = socket connected), so the list is the same
        // truth the in-game friends list shows. Other people's lists show accepted friends only.
        [HttpGet("/api/site/profile/{handle}/friends")]
        public IActionResult ProfileFriends(string handle)
        {
            try
            {
                player_data? full = PlayerDB.GetFullAccountByName(handle);
                if (full == null && ulong.TryParse(handle, out var byId)) full = PlayerDB.GetFullAccount(byId);
                if (full?.player == null) return NotFound(new { ok = false, error = "player not found" });
                ulong id = full.playerid;
                ulong viewer = ViewerId();
                bool own = viewer != 0 && viewer == id;

                var rels = FriendsDB.GetRelationships(id) ?? new List<RelationshipDetail>();
                var online = CampusLiveSource.Controllers.NotificationsController.WebSockets.Keys.ToHashSet();
                var viewerRels = own ? rels : (viewer != 0 ? FriendsDB.GetRelationships(viewer) ?? new List<RelationshipDetail>() : new List<RelationshipDetail>());

                object Row(RelationshipDetail r, bool favourite)
                {
                    var acc = PlayerDB.GetAccountById(r.PlayerID);
                    if (acc == null) return null!;
                    bool isOnline = online.Contains(r.PlayerID);
                    return new
                    {
                        id = acc.Id,
                        username = acc.Username,
                        display_name = acc.DisplayName,
                        profile_image = ImgUrl(acc.profileImage),
                        favourite,
                        online = isOnline,
                        where = isOnline ? ShowableRoomOf(r.PlayerID) : null,
                        spotify = SpotifyLink.CachedPayloadFor(r.PlayerID, viewer),
                    };
                }

                var friends = rels.Where(r => r.RelationshipType == RelationshipType.Friend)
                    // "favourite" is always from the VIEWER's point of view.
                    .Select(r => Row(r, own ? r.Favorited != ReciprocalStatus.None
                                            : viewerRels.Any(v => v.PlayerID == r.PlayerID && v.Favorited != ReciprocalStatus.None)))
                    .Where(x => x != null).ToList();
                var pending = own
                    ? rels.Where(r => r.RelationshipType == RelationshipType.FriendRequestReceived).Select(r => Row(r, false)).Where(x => x != null).ToList()
                    : new List<object>();
                var sent = own
                    ? rels.Where(r => r.RelationshipType == RelationshipType.FriendRequestSent).Select(r => Row(r, false)).Where(x => x != null).ToList()
                    : new List<object>();

                return Ok(new { ok = true, own, friends, pending, sent });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] profile friends {handle} failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load friends" });
            }
        }

        // The people subscribed to a profile, and the creators that profile subscribes to. Both
        // read the same store the in-game Subscribe button writes. Public, like the counts.
        [HttpGet("/api/site/profile/{handle}/subscribers")]
        public IActionResult ProfileSubscribers(string handle) => SubscriptionList(handle, subscribers: true);

        [HttpGet("/api/site/profile/{handle}/subscriptions")]
        public IActionResult ProfileSubscriptions(string handle) => SubscriptionList(handle, subscribers: false);

        private IActionResult SubscriptionList(string handle, bool subscribers)
        {
            try
            {
                player_data? full = PlayerDB.GetFullAccountByName(handle);
                if (full == null && ulong.TryParse(handle, out var byId)) full = PlayerDB.GetFullAccount(byId);
                if (full?.player == null) return NotFound(new { ok = false, error = "player not found" });
                ulong id = full.playerid;
                ulong viewer = ViewerId();
                var ids = subscribers ? PlayerDB.GetSubscriberIds(id) : PlayerDB.GetSubscribedToIds(id);
                var mine = viewer != 0 ? PlayerDB.GetSubscribedToIds(viewer) : new List<ulong>();
                var online = CampusLiveSource.Controllers.NotificationsController.WebSockets.Keys.ToHashSet();
                var results = new List<object>();
                foreach (var pid in ids.Distinct())
                {
                    var acc = PlayerDB.GetAccountById(pid);
                    if (acc == null) continue;
                    bool isOnline = online.Contains(pid);
                    results.Add(new
                    {
                        id = acc.Id,
                        username = acc.Username,
                        display_name = acc.DisplayName,
                        profile_image = ImgUrl(acc.profileImage),
                        online = isOnline,
                        where = isOnline ? ShowableRoomOf(pid) : null,
                        subscribers = PlayerDB.GetSubscriberIds(pid).Count,
                        // whether the VIEWER is subscribed to this person (so the row can offer a button)
                        subscribed = viewer != 0 && viewer != pid && mine.Contains(pid),
                    });
                }
                return Ok(new { ok = true, own = viewer != 0 && viewer == id, results });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] subscription list {handle} failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load the list" });
            }
        }

        public class FavouriteBody { public ulong id { get; set; } public bool on { get; set; } }

        // Favourite / unfavourite a friend from the website. Same store and same realtime pushes as
        // the in-game star (FriendsDB.SetRelationshipFlag), so the game updates the moment it's clicked.
        [HttpPost("/api/site/friends/favourite")]
        public IActionResult SiteFavourite([FromBody] FavouriteBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null || body.id == 0 || body.id == me.Id) return BadRequest(new { ok = false, error = "invalid target" });
            if (!AreFriends(me.Id, body.id)) return BadRequest(new { ok = false, error = "you can only favourite a friend" });
            var rel = FriendsDB.SetRelationshipFlag(me.Id, body.id, FriendInfo.Favorited, body.on);
            return Ok(new { ok = true, favourite = rel.Favorited != ReciprocalStatus.None });
        }

        public class BlockBody { public ulong id { get; set; } public bool on { get; set; } }

        // POST /api/site/friends/block {id, on}  - block / unblock from the website. The SAME
        // FriendsDB.SetBlock the in-game button uses, so the block (and its direction) shows in
        // the game instantly and only the blocker's own part is lifted by an unblock.
        [HttpPost("/api/site/friends/block")]
        public IActionResult SiteBlock([FromBody] BlockBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null || body.id == 0 || body.id == me.Id) return BadRequest(new { ok = false, error = "invalid target" });
            if (PlayerDB.GetAccountById(body.id) == null) return NotFound(new { ok = false, error = "player not found" });
            var rel = FriendsDB.SetBlock(me.Id, body.id, body.on);
            return Ok(new { ok = true, blocked = FriendsDB.IsOwnBlock(rel.Ignored), blockedMe = rel.Ignored == ReciprocalStatus.Remote || rel.Ignored == ReciprocalStatus.Mutual });
        }

        public class NicknameBody { public ulong id { get; set; } public string? nickname { get; set; } }
        public class PinBody { public ulong id { get; set; } public bool on { get; set; } }

        // POST /api/site/friends/nickname {id, nickname}  - what YOU call this friend on the site
        // (empty removes it). Website-only; stored per owner in SitePrefsDB.
        [HttpPost("/api/site/friends/nickname")]
        public IActionResult SiteNickname([FromBody] NicknameBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null || body.id == 0 || body.id == me.Id) return BadRequest(new { ok = false, error = "invalid target" });
            if (!AreFriends(me.Id, body.id)) return BadRequest(new { ok = false, error = "you can only nickname a friend" });
            string? nick = (body.nickname ?? "").Trim();
            if (nick.Length > 0) nick = Campus_live_rewrite.api.server.Classes.Sanitize.SanitizeMessage(nick);
            nick = SitePrefsDB.SetNickname(me.Id, body.id, nick);
            // the game shows this nickname too (only to the player who set it): redraw that friend on their client now
            Campus_live_rewrite.api.server.Notifications.FireAndForget(Campus_live_rewrite.api.server.Notifications.PushAccountUpdateToViewer(me.Id, body.id), "nickname push");
            return Ok(new { ok = true, nickname = nick });
        }

        // POST /api/site/friends/pin {id, on}  - pin / unpin a friend on your home page rail.
        [HttpPost("/api/site/friends/pin")]
        public IActionResult SitePin([FromBody] PinBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null || body.id == 0 || body.id == me.Id) return BadRequest(new { ok = false, error = "invalid target" });
            if (body.on && !AreFriends(me.Id, body.id)) return BadRequest(new { ok = false, error = "you can only pin a friend" });
            return Ok(new { ok = true, pinned = SitePrefsDB.SetPinned(me.Id, body.id, body.on) });
        }

        // GET /api/site/me/pinned  - the friends pinned to my home page, with their live status.
        [HttpGet("/api/site/me/pinned")]
        public IActionResult MyPinned()
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var prefs = SitePrefsDB.Get(me.Id);
            var rows = new List<object>();
            foreach (var pid in prefs.Pinned)
            {
                var acc = PlayerDB.GetAccountById(pid);
                if (acc == null || !AreFriends(me.Id, pid)) continue;
                var (isOnline, room) = HeartbeatDB.PeekPresence(pid);
                rows.Add(new
                {
                    id = pid, username = acc.Username, display_name = acc.DisplayName, profile_image = ImgUrl(acc.profileImage),
                    nickname = SitePrefsDB.NicknameFor(me.Id, pid),
                    online = isOnline, where = isOnline ? ShowableRoom(room) : null,
                });
            }
            return Ok(new { ok = true, pinned = rows });
        }

        public class SubscribeBody { public ulong id { get; set; } public bool on { get; set; } }

        // Subscribe / unsubscribe to a creator from the website - the same store the in-game
        // Subscribe button writes (ClubsController), so both places agree instantly.
        [HttpPost("/api/site/subscribe")]
        public IActionResult SiteSubscribe([FromBody] SubscribeBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null || body.id == 0 || body.id == me.Id) return BadRequest(new { ok = false, error = "invalid target" });
            if (!RateLimit.TryConsume($"sitesub:{me.Id}", cooldownSeconds: 2, out int retry))
            {
                Response.Headers["Retry-After"] = retry.ToString();
                return StatusCode(429, new { ok = false, error = "Slow down a moment." });
            }
            try
            {
                if (body.on) PlayerDB.AddSubscription(me.Id, body.id); else PlayerDB.RemoveSubscription(me.Id, body.id);
                Console.WriteLine($"[site] {me.Id} {(body.on ? "subscribed to" : "unsubscribed from")} {body.id}");
                SiteNotifications.Notify(me.Id);
                SiteNotifications.Notify(body.id);
                return Ok(new { ok = true, subscribed = body.on, subscribers = PlayerDB.GetSubscriberIds(body.id).Count });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] subscribe {me.Id}->{body.id} failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not update the subscription" });
            }
        }

        [HttpPost("/api/site/friends/request")]
        public IActionResult SiteSendFriendRequest([FromBody] FriendActionBody? body)
            => FriendAction(body, FriendsDB.SendFriendRequest, "sent request to");

        [HttpPost("/api/site/friends/accept")]
        public IActionResult SiteAcceptFriend([FromBody] FriendActionBody? body)
            => FriendAction(body, FriendsDB.AcceptFriendRequest, "accepted");

        [HttpPost("/api/site/friends/remove")]
        public IActionResult SiteRemoveFriend([FromBody] FriendActionBody? body)
            => FriendAction(body, FriendsDB.RemoveFriend, "removed/cancelled");

        // ---- notifications (website inbox) ------------------------------------------------
        // A real, persistent, game-synced inbox. Aggregates the two server-side sources of
        // truth so the website shows the same notifications the game does:
        //   1. Incoming friend requests - pending FriendRequestReceived rows in the relationship
        //      DB. Actionable here (Accept -> /friends/accept, Decline -> /friends/remove) and
        //      they live until actioned, exactly like in-game.
        //   2. Saved messages - player_Extra.Messages (cheers, coach messages, ...), the same
        //      store /api/messages/v2/get serves the game, dismissed via the same delete.
        // Game / party / join invites can't be actioned from a browser, so they render as
        // "Launch the game to view." Read fresh every call, so web and game stay in sync.

        // The invite kinds a browser cannot act on - they need the running game/session.
        private static readonly HashSet<MessageType> LaunchGameTypes = new()
        {
            MessageType.GameInvite, MessageType.GameInviteV2, MessageType.GameInviteDeclined,
            MessageType.PartyActivitySwitch, MessageType.PartyActivitySwitchV2, MessageType.FriendInvite,
            MessageType.RequestGameInvite, MessageType.RequestGameInviteDeclined,
        };

        public class NotifDismissBody { public string? id { get; set; } }

        // Project a player to {id, username, display_name, profile_image} plus a display name.
        private static (object? proj, string name) WhoNamed(ulong pid)
        {
            if (pid == 0) return (null, "Someone");
            var a = PlayerDB.GetAccountById(pid);
            if (a == null) return (null, "Someone");
            string nm = !string.IsNullOrWhiteSpace(a.DisplayName) ? a.DisplayName
                      : (!string.IsNullOrWhiteSpace(a.Username) ? a.Username : "Someone");
            return (new { id = a.Id, username = a.Username, display_name = a.DisplayName, profile_image = ImgUrl(a.profileImage) }, nm);
        }

        // Aggregates a player's inbox from the two server-side sources of truth. Static so a
        // diagnostic/self-test can call it directly with a player id (no cookie needed).
        public static List<object> BuildNotifications(ulong meId)
        {
            var items = new List<object>();

            // 1) incoming friend requests (actionable, persist until accepted/declined)
            try
            {
                var rels = FriendsDB.GetRelationships(meId) ?? new List<RelationshipDetail>();
                foreach (var r in rels.Where(r => r.RelationshipType == RelationshipType.FriendRequestReceived))
                {
                    ulong other = r.PlayerID; // GetRelationships projects PlayerID = the other player
                    var (proj, name) = WhoNamed(other);
                    items.Add(new
                    {
                        id = "fr:" + other,
                        kind = "friend_request",
                        actionable = true,
                        launchGame = false,
                        fromPlayer = proj,
                        targetId = other,
                        text = $"{name} sent you a friend request",
                        time = (DateTime?)null
                    });
                }
            }
            catch { }

            // 2) saved messages (cheers, coach messages, and any launch-game invites if present)
            try
            {
                var msgs = PlayerDB.GetMessages(meId) ?? new List<MessageData>();
                foreach (var m in msgs.OrderByDescending(m => m.SentTime))
                {
                    // The stored FriendInvite message is the GAME's copy of a pending request; the
                    // website already lists the request itself (friend_request rows above).
                    if (m.Type == MessageType.FriendInvite) continue;
                    bool launch = LaunchGameTypes.Contains(m.Type);
                    bool anon = m.Type == MessageType.PlayerCheerAnonymous;
                    var (proj, name) = anon ? ((object?)null, "Someone") : WhoNamed(m.FromPlayerId);

                    string text;
                    if (launch) text = "Launch the game to view.";
                    else text = m.Type switch
                    {
                        MessageType.PlayerCheer => $"{name} cheered you",
                        MessageType.PlayerCheerAnonymous => "Someone cheered you",
                        MessageType.FriendRequestAccepted => $"{name} accepted your friend request",
                        MessageType.CoachMessage => string.IsNullOrWhiteSpace(m.Data) ? "Message from the Campus team" : m.Data!,
                        MessageType.TextMessage => $"{name} sent you a message",
                        _ => "New notification"
                    };

                    items.Add(new
                    {
                        id = "msg:" + m.Id,
                        kind = launch ? "launch" : "message",
                        actionable = false,
                        launchGame = launch,
                        fromPlayer = proj,
                        text,
                        time = (DateTime?)m.SentTime
                    });
                }
            }
            catch { }

            // 3) support: staff replied on one of my tickets (cleared by opening the ticket), and for
            // staff, one line counting the tickets waiting on staff (cleared as they're answered).
            try
            {
                foreach (var t in SupportDB.ForAccount(meId).Where(SupportDB.UnreadForPlayer))
                {
                    var lastStaff = t.Messages.LastOrDefault(m => m.Staff && !m.Internal);
                    items.Add(new
                    {
                        id = "sup:" + t.Id,
                        kind = "support",
                        actionable = false,
                        launchGame = false,
                        fromPlayer = (object?)null,
                        link = "/support/ticket/" + t.Id,
                        text = lastStaff != null && lastStaff.System
                            ? $"Your support ticket #{t.Id} was updated: {lastStaff.Body}"
                            : $"Staff replied to your support ticket #{t.Id} \u201c{t.Subject}\u201d",
                        time = (DateTime?)(lastStaff?.At ?? t.UpdatedAt)
                    });
                }
                if (SiteController.IsSupportStaff(meId))
                {
                    int waiting = SupportDB.AwaitingStaffCount();
                    if (waiting > 0)
                        items.Add(new
                        {
                            id = "supq",
                            kind = "support",
                            actionable = false,
                            launchGame = false,
                            fromPlayer = (object?)null,
                            link = "/support/logs",
                            text = waiting == 1 ? "1 support ticket is waiting for staff" : $"{waiting} support tickets are waiting for staff",
                            time = (DateTime?)null
                        });
                }
            }
            catch { }

            return items;
        }

        [HttpGet("/api/site/notifications")]
        public IActionResult GetSiteNotifications()
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var items = BuildNotifications(me.Id);
            return Ok(new { ok = true, count = items.Count, items });
        }

        // Realtime push: the browser opens this websocket; whenever a notification is created for
        // this player (SiteNotifications.Notify, fired from Notifications.SendToPlayer and
        // PlayerDB.AddMessage) the socket receives a {"type":"refresh"} and the page re-fetches
        // the inbox above. Separate from the game hub so it never disturbs the player's game
        // socket. Cookie-authed like the rest of the site endpoints.
        [HttpGet("/api/site/notifications/ws")]
        public async Task SiteNotificationsSocket()
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var me = CurrentUser();
            if (me == null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await Campus_live_rewrite.api.server.SiteNotifications.HandleAsync(me.Id, socket);
        }

        // Dismiss a SAVED message (msg:<id>) - deletes it from the same store the game reads,
        // so it clears in both places. Friend requests are not dismissed here; they are accepted
        // or declined through /api/site/friends/*.
        [HttpPost("/api/site/notifications/dismiss")]
        public IActionResult SiteDismissNotification([FromBody] NotifDismissBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null || string.IsNullOrWhiteSpace(body.id))
                return BadRequest(new { ok = false, error = "no id" });

            if (body.id.StartsWith("msg:") && ulong.TryParse(body.id.Substring(4), out var mid))
            {
                DismissMessageEverywhere(me.Id, mid);
                return Ok(new { ok = true });
            }
            // "Staff replied to your ticket": dismissing it marks the ticket read.
            if (body.id.StartsWith("sup:") && int.TryParse(body.id.Substring(4), out var tid)
                && SupportDB.Get(tid)?.AccountId == me.Id)
            {
                SupportDB.MarkRead(tid, asStaff: false);
                SiteNotifications.Notify(me.Id);
                return Ok(new { ok = true });
            }
            if (body.id == "supq") return Ok(new { ok = true });   // clears itself as tickets are answered
            return BadRequest(new { ok = false, error = "not a dismissable notification" });
        }

        // Delete the stored message AND tell the running game to drop it (MessageDeleted frame),
        // so "mark as read" on the website removes the notification in-game right away instead of
        // on the next notifications fetch.
        private static void DismissMessageEverywhere(ulong playerId, ulong messageId)
        {
            PlayerDB.DeleteMessage(playerId, messageId);
            try
            {
                Notifications.FireAndForget(
                    Notifications.SendToPlayer(playerId, Newtonsoft.Json.JsonConvert.SerializeObject(Campus_live_rewrite.api.WebsocketEvents.CreateMessageDeleted(messageId))),
                    "site dismiss -> game");
            }
            catch { }
        }

        // POST /api/site/notifications/dismissall - "Mark all as read": every dismissable
        // notification (friend requests stay - they are actioned, not read) goes in one call.
        [HttpPost("/api/site/notifications/dismissall")]
        public IActionResult SiteDismissAllNotifications()
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var full = PlayerDB.GetFullAccount(me.Id);
            var msgs = full?.player_Extra?.Messages?.Select(m => m.Id).ToList() ?? new List<ulong>();
            foreach (var id in msgs) DismissMessageEverywhere(me.Id, id);
            foreach (var t in SupportDB.ForAccount(me.Id).Where(SupportDB.UnreadForPlayer)) SupportDB.MarkRead(t.Id, asStaff: false);
            SiteNotifications.Notify(me.Id);
            return Ok(new { ok = true, dismissed = msgs.Count });
        }

        // ---- photos -----------------------------------------------------------------------

        // GET /api/site/photos/recent?take=24
        // The home feed. Replaces the picsum.photos seed URLs and the invented post list.
        [HttpGet("/api/site/photos/recent")]
        public IActionResult RecentPhotos(int take = 24)
        {
            take = Math.Clamp(take, 1, 60);
            try
            {
                // GetRecentPublicPhotos, not GetGlobalImages/GetSlideshowImages:
                //   - GetGlobalImages returns EVERY public image type (profile pictures, room
                //     and outfit thumbnails showed up as if they were photos) and sorts by
                //     CheerCount, so "Recent Photos" was neither photos-only nor recent.
                //   - GetSlideshowImages is Public + ShareCamera only, but THIS server saves
                //     camera photos as Type None, so it matched nothing and the feed was empty.
                // GetRecentPublicPhotos is Public + (None|ShareCamera), newest first - the actual
                // camera photos, excluding thumbnails, and unauthenticated-safe.
                var rows = ImageMetadataDB.GetRecentPublicPhotos(take) ?? new();

                // Batch the author lookups the same way rooms do.
                var authors = new Dictionary<ulong, player_info>();
                foreach (var pid in rows.Select(r => r.PlayerId).Distinct())
                {
                    var acc = PlayerDB.GetAccountById(pid);
                    if (acc != null) authors[pid] = acc;
                }

                ulong viewerId = ViewerId();
                var results = rows.Select(i =>
                {
                    authors.TryGetValue(i.PlayerId, out var a);
                    return new
                    {
                        id = i.Id,
                        image = ImgUrl(i.ImageName),
                        description = i.Description,
                        cheers = i.CheerCount,
                        cheered = viewerId != 0 && ImageMetadataDB.HasUserCheered(i.Id, viewerId),
                        comments = i.CommentCount,
                        roomId = PhotoRoomId(i.RoomId),
                        createdAt = i.CreatedAt,
                        author = a == null ? null : new
                        {
                            id = a.Id,
                            username = a.Username,
                            display_name = a.DisplayName,
                            profile_image = ImgUrl(a.profileImage),
                        },
                    };
                }).ToList();

                return Ok(new { ok = true, results });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] recent photos failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load photos" });
            }
        }

        // True when a and b are friends (a's relationship list contains b as a Friend).
        private bool AreFriends(ulong a, ulong b)
        {
            if (a == 0 || b == 0 || a == b) return false;
            var rels = FriendsDB.GetRelationships(a);
            return rels != null && rels.Any(r => r.PlayerID == b && r.RelationshipType == RelationshipType.Friend);
        }

        // The website "visibility" of a photo, projected from the two stored fields.
        private static string VisibilityOf(ImageMetadataDB.FullSavedImage img) =>
            img.FriendsOnly ? "friends"
            : (img.Accessibility == ImageMetadataDB.ImageAccessibility.Public ? "public" : "private");

        // Resolve a list of tagged account ids to display rows (skips ids with no account).
        private List<object> ResolveTagged(List<ulong>? ids)
        {
            var outp = new List<object>();
            if (ids == null) return outp;
            foreach (var pid in ids.Distinct())
            {
                var acc = PlayerDB.GetAccountById(pid);
                if (acc == null) continue;
                outp.Add(new
                {
                    id = acc.Id,
                    username = acc.Username,
                    display_name = acc.DisplayName,
                    profile_image = ImgUrl(acc.profileImage),
                });
            }
            return outp;
        }

        // GET /api/site/photos/{id} - one photo, for the image detail page (/image/{id}).
        [HttpGet("/api/site/photos/{id:int}")]
        public IActionResult Photo(int id)
        {
            try
            {
                var img = ImageMetadataDB.GetById(id);
                if (img == null)
                    return NotFound(new { ok = false, error = "photo not found" });

                ulong viewer = ViewerId();
                bool isOwner = viewer != 0 && viewer == img.PlayerId;
                bool isPublic = img.Accessibility == ImageMetadataDB.ImageAccessibility.Public && !img.FriendsOnly;
                bool canView = isOwner || isPublic
                            || (img.FriendsOnly && AreFriends(img.PlayerId, viewer));
                if (!canView)
                    return StatusCode(403, new { ok = false, error = "This photo is private." });

                var a = PlayerDB.GetAccountById(img.PlayerId);
                // the room it was taken in, for the "^Room" link - only once that room is published
                // (never a dorm or an unpublished draft; see PhotoRoom)
                var photoRoom = PhotoRoom(img.RoomId);
                return Ok(new
                {
                    ok = true,
                    photo = new
                    {
                        id = img.Id,
                        image = ImgUrl(img.ImageName),
                        description = img.Description,
                        cheers = img.CheerCount,
                        cheered = viewer != 0 && ImageMetadataDB.HasUserCheered(img.Id, viewer),
                        comments = img.CommentCount,
                        commentList = CommentRows(ImageMetadataDB.GetComments(img.Id)),
                        roomId = photoRoom == null ? 0 : (long)photoRoom.RoomId,
                        room = photoRoom == null ? null : new { id = photoRoom.RoomId, name = photoRoom.Name },
                        createdAt = img.CreatedAt,
                        isOwner,
                        visibility = VisibilityOf(img),
                        taggedPlayers = ResolveTagged(img.TaggedPlayerIds),
                        author = a == null ? null : new
                        {
                            id = a.Id,
                            username = a.Username,
                            display_name = a.DisplayName,
                            profile_image = ImgUrl(a.profileImage),
                        },
                    },
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] photo {id} failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load the photo" });
            }
        }

        public class PhotoVisibilityBody { public string? visibility { get; set; } }
        public class PhotoTextBody { public string? text { get; set; } public string? description { get; set; } }

        private List<object> CommentRows(List<ImageMetadataDB.ImageComment> list)
        {
            var outp = new List<object>();
            foreach (var c in list)
            {
                var acc = PlayerDB.GetAccountById(c.PlayerId);
                outp.Add(new
                {
                    id = c.Id,
                    playerId = c.PlayerId,
                    username = acc?.Username ?? $"Player{c.PlayerId}",
                    display_name = acc?.DisplayName ?? acc?.Username ?? $"Player{c.PlayerId}",
                    profile_image = ImgUrl(acc?.profileImage),
                    text = c.Text,
                    createdAt = c.CreatedAt,
                });
            }
            return outp;
        }

        private static bool CanSeePhoto(ImageMetadataDB.FullSavedImage img, ulong viewer, Func<ulong, ulong, bool> friends)
            => (viewer != 0 && viewer == img.PlayerId)
               || (img.Accessibility == ImageMetadataDB.ImageAccessibility.Public && !img.FriendsOnly)
               || (img.FriendsOnly && friends(img.PlayerId, viewer));

        // GET /api/site/me/photos?taken=1&tagged=1  - every camera photo of mine (private ones
        // included - it's my own export), for Settings > Export > Export Photos. Only the list:
        // the browser fetches each /img/ file itself and builds the zip client-side, so nothing
        // is assembled or stored on the server.
        [HttpGet("/api/site/me/photos")]
        public IActionResult MyPhotos([FromQuery] bool taken = true, [FromQuery] bool tagged = true)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var rows = (ImageMetadataDB.GetPlayerFeed(me.Id, me.Id, 0, 5000) ?? new())
                .Where(i => i.SavedImageType == SavedImageTypeEnum.ShareCamera || i.SavedImageType == SavedImageTypeEnum.None)
                .Select(i => new
                {
                    id = i.SavedImageId,
                    image = ImgUrl(i.ImageName),
                    file = i.ImageName,
                    description = i.Description,
                    createdAt = i.CreatedAt,
                    taken = i.PlayerId == me.Id,
                    tagged = i.TaggedPlayerIds != null && i.TaggedPlayerIds.Contains(me.Id),
                })
                .Where(x => (taken && x.taken) || (tagged && x.tagged))
                .ToList();
            return Ok(new { ok = true, photos = rows });
        }

        // ---- reports ----------------------------------------------------------------------
        //
        // POST /api/site/report  {type: player|photo|comment, id, photoId?, reason, details}
        // The site's Report buttons. Lands in the SAME queue as in-game reports (ReportsDB), so
        // the admin panel's Reports tab shows both and the same ban / warn / dismiss actions
        // apply. The reported player is resolved server-side from what was reported, never
        // trusted from the body. Anonymous to the reported player; the reporter is recorded.
        public class SiteReportBody
        {
            public string? type { get; set; }
            public string? id { get; set; }
            public int? photoId { get; set; }
            public string? reason { get; set; }
            public string? details { get; set; }
        }
        private static readonly Dictionary<ulong, List<DateTime>> _reportRate = new();
        private static readonly string[] ReportReasons = { "Spam or scam", "Harassment or bullying", "Inappropriate content", "Hate speech", "Impersonation", "Other" };

        [HttpPost("/api/site/report")]
        public IActionResult SiteReport([FromBody] SiteReportBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "Log in to send a report." });
            string type = (body?.type ?? "").Trim().ToLowerInvariant();
            string reason = (body?.reason ?? "").Trim();
            if (!ReportReasons.Contains(reason)) return BadRequest(new { ok = false, error = "Pick a reason." });
            string details = (body?.details ?? "").Trim();
            if (details.Length > 300) details = details[..300];

            // 5 reports per 10 minutes per account - same limit the page enforces, but here
            // so it can't be skipped.
            lock (_reportRate)
            {
                if (!_reportRate.TryGetValue(me.Id, out var times)) _reportRate[me.Id] = times = new();
                times.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-10));
                if (times.Count >= 5) return StatusCode(429, new { ok = false, error = "You've sent too many reports. Please try again later." });
                times.Add(DateTime.UtcNow);
            }

            ulong reported = 0; long subjectId = 0; string link = ""; string subjectText = "";
            switch (type)
            {
                case "player":
                {
                    string handle = (body?.id ?? "").Trim().TrimStart('@');
                    reported = ulong.TryParse(handle, out var pid) ? pid : PlayerDB.GetAccountIdByUsernameCI(handle);
                    var acc = reported != 0 ? PlayerDB.GetAccountById(reported) : null;
                    if (acc == null) return NotFound(new { ok = false, error = "player not found" });
                    subjectId = (long)reported; link = "/user/" + Uri.EscapeDataString(acc.Username ?? handle);
                    subjectText = "profile @" + acc.Username;
                    break;
                }
                case "photo":
                {
                    if (!int.TryParse(body?.id, out var imgId)) return BadRequest(new { ok = false, error = "bad photo id" });
                    var img = ImageMetadataDB.GetById(imgId);
                    if (img == null) return NotFound(new { ok = false, error = "photo not found" });
                    reported = img.PlayerId; subjectId = imgId; link = "/image/" + imgId;
                    subjectText = "photo #" + imgId + (string.IsNullOrWhiteSpace(img.Description) ? "" : " \"" + img.Description + "\"");
                    break;
                }
                case "comment":
                {
                    if (!int.TryParse(body?.id, out var cid)) return BadRequest(new { ok = false, error = "bad comment id" });
                    var c = ImageMetadataDB.GetComment(cid);
                    if (c == null) return NotFound(new { ok = false, error = "comment not found" });
                    reported = c.PlayerId; subjectId = cid; link = "/image/" + c.SavedImageId;
                    subjectText = "comment on photo #" + c.SavedImageId + ": \"" + c.Text + "\"";
                    break;
                }
                default:
                    return BadRequest(new { ok = false, error = "bad report type" });
            }
            if (reported == me.Id) return BadRequest(new { ok = false, error = "You can't report yourself." });

            // one open report per reporter per thing - a second click doesn't stack the queue
            if (ReportsDB.HasOpenDuplicate(me.Id, type, subjectId, reported))
                return Ok(new { ok = true, duplicate = true });

            var category = reason switch
            {
                "Harassment or bullying" => Moderation_Detail.ReportCategory.Harassment,
                "Hate speech" => Moderation_Detail.ReportCategory.CoC_Discrimination,
                "Inappropriate content" => Moderation_Detail.ReportCategory.CoC_Trolling,
                "Impersonation" => Moderation_Detail.ReportCategory.CoC_NameOrProfile,
                _ => Moderation_Detail.ReportCategory.Misc,
            };
            var r = ReportsDB.Create(new ReportsDB.Report
            {
                ReporterPlayerId = me.Id,
                ReportedPlayerId = reported,
                ReportCategory = (int)category,
                Details = (subjectText + (details.Length > 0 ? "\n\n" + details : "")).Trim(),
                Source = "website",
                Subject = type,
                SubjectId = subjectId,
                SubjectLink = link,
                Reason = reason,
            });
            return Ok(new { ok = true, id = r.Id });
        }

        // POST /api/site/photos/{id}/cheer  -> toggles the SAME cheer the game records
        // (ImageMetadataDB cheers), so a cheer on the site shows in-game and vice versa.
        [HttpPost("/api/site/photos/{id:int}/cheer")]
        public IActionResult CheerPhoto(int id)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var img = ImageMetadataDB.GetById(id);
            if (img == null) return NotFound(new { ok = false, error = "photo not found" });
            if (!CanSeePhoto(img, me.Id, AreFriends)) return StatusCode(403, new { ok = false, error = "This photo is private." });
            bool now = ImageMetadataDB.ToggleCheer(id, me.Id, !ImageMetadataDB.HasUserCheered(id, me.Id));
            int count = ImageMetadataDB.GetCheerCount(id);
            ImageMetadataDB.UpdateImageCheerCount(id, count);
            return Ok(new { ok = true, cheered = now, cheers = count });
        }

        // GET /api/site/photos/{id}/comments ; POST {text} ; DELETE /comments/{commentId}
        [HttpGet("/api/site/photos/{id:int}/comments")]
        public IActionResult PhotoComments(int id)
        {
            var img = ImageMetadataDB.GetById(id);
            if (img == null) return NotFound(new { ok = false, error = "photo not found" });
            if (!CanSeePhoto(img, ViewerId(), AreFriends)) return StatusCode(403, new { ok = false, error = "This photo is private." });
            return Ok(new { ok = true, comments = CommentRows(ImageMetadataDB.GetComments(id)) });
        }

        [HttpPost("/api/site/photos/{id:int}/comments")]
        public IActionResult AddPhotoComment(int id, [FromBody] PhotoTextBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var img = ImageMetadataDB.GetById(id);
            if (img == null) return NotFound(new { ok = false, error = "photo not found" });
            if (!CanSeePhoto(img, me.Id, AreFriends)) return StatusCode(403, new { ok = false, error = "This photo is private." });
            string text = (body?.text ?? "").Trim();
            if (text.Length == 0) return BadRequest(new { ok = false, error = "Say something first." });
            if (text.Length > 200) text = text[..200];
            text = Campus_live_rewrite.api.server.Classes.Sanitize.SanitizeMessage(text);
            var c = ImageMetadataDB.AddComment(id, me.Id, text);
            if (c == null) return NotFound(new { ok = false, error = "photo not found" });
            Console.WriteLine($"[site] {me.Id} commented on photo {id}");
            return Ok(new { ok = true, comment = CommentRows(new List<ImageMetadataDB.ImageComment> { c })[0], comments = ImageMetadataDB.GetById(id)?.CommentCount ?? 0 });
        }

        [HttpDelete("/api/site/photos/{id:int}/comments/{commentId:int}")]
        [HttpPost("/api/site/photos/{id:int}/comments/{commentId:int}/delete")]
        public IActionResult DeletePhotoComment(int id, int commentId)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (!ImageMetadataDB.DeleteComment(commentId, me.Id)) return StatusCode(403, new { ok = false, error = "not your comment" });
            return Ok(new { ok = true, comments = ImageMetadataDB.GetById(id)?.CommentCount ?? 0 });
        }

        // POST /api/site/photos/{id}/description {description}  - the photo's title/caption,
        // the same field the game shows (modifydescription).
        [HttpPost("/api/site/photos/{id:int}/description")]
        public IActionResult SetPhotoDescription(int id, [FromBody] PhotoTextBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var img = ImageMetadataDB.GetById(id);
            if (img == null) return NotFound(new { ok = false, error = "photo not found" });
            if (img.PlayerId != me.Id && !PlayerDB.CheckDevFlag(me.Id)) return StatusCode(403, new { ok = false, error = "not your photo" });
            string text = (body?.description ?? body?.text ?? "").Trim();
            if (text.Length > 120) text = text[..120];
            text = Campus_live_rewrite.api.server.Classes.Sanitize.SanitizeMessage(text);
            ImageMetadataDB.SetDescription(id, text);
            return Ok(new { ok = true, description = text });
        }

        public class BannerBody { public int photoId { get; set; } public bool reset { get; set; } }

        // POST /api/site/me/banner {photoId} | {reset:true} - use one of your (or a public) photos
        // as your profile banner; reset puts the starting banner.png back. Pushed to the game.
        [HttpPost("/api/site/me/banner")]
        public IActionResult SetMyBanner([FromBody] BannerBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (PlayerDB.IsProfileLocked(me.Id, PlayerDB.ProfileLock.Banner))
                return BadRequest(new { ok = false, error = PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Banner) });
            string name;
            if (body == null || body.reset || body.photoId <= 0) name = ConfigCampus.DefaultBannerImage;
            else
            {
                var img = ImageMetadataDB.GetById(body.photoId);
                if (img == null) return NotFound(new { ok = false, error = "photo not found" });
                if (!CanSeePhoto(img, me.Id, AreFriends)) return StatusCode(403, new { ok = false, error = "This photo is private." });
                name = img.ImageName;
            }
            PlayerDB.SetPlayerBanner(name, me.Id);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(me.Id), "site banner refresh");
            return Ok(new { ok = true, banner_image = ImgUrl(name) });
        }

        // ---- events on the site: a player's profile (Events tab) and a room's page ----------------------------------
        // An event card: what the profile / room page draws. Times are UTC ISO (the page shows them in the visitor's
        // own timezone), status is live / upcoming / ended, going / interested are the RSVP counts, myResponse is the
        // signed-in visitor's own answer.
        private static object EventCard(EventDBClasses.FullPlayerEvent e, ulong viewer)
        {
            RoomDBClasses.RoomRoot? room = null;
            try { room = e.RoomId > 0 ? RoomDB.GetRoom(e.RoomId) : null; } catch { }
            player_data? host = null;
            try { host = e.CreatorPlayerId is ulong c && c > 0 ? PlayerDB.GetFullAccount(c) : null; } catch { }
            var now = DateTime.UtcNow;
            var start = e.StartTime.ToUniversalTime();
            var end = e.EndTime.ToUniversalTime();
            string status = start <= now && end >= now ? "live" : start > now ? "upcoming" : "ended";
            var resp = e.Responses ?? new List<EventDBClasses.FullEventResponses>();
            var mine = viewer == 0 ? null : resp.FirstOrDefault(r => r.PlayerId == viewer);
            return new
            {
                id = e.PlayerEventId,
                name = string.IsNullOrWhiteSpace(e.Name) ? "Untitled event" : e.Name,
                description = e.Description ?? "",
                image = ImgUrl(e.ImageName),
                start,
                end,
                status,
                room = RoomIsShowable(room) ? new { id = room!.RoomId, name = room.Name } : null,
                host = host?.player == null ? null : new { id = host.playerid, username = host.player.Username, display_name = host.player.DisplayName, profile_image = ImgUrl(host.player.profileImage) },
                going = resp.Count(r => r.Type == EventDBClasses.PlayerEventResponseType.Yes),
                interested = resp.Count(r => r.Type == EventDBClasses.PlayerEventResponseType.Interested),
                accessibility = e.Accessibility.ToString().ToLowerInvariant(),
                canJoin = viewer != 0 && e.EndTime.ToUniversalTime() >= now && EventDB.CanPlayerJoin(e, viewer, out _),
                myResponse = mine == null ? null : mine.Type == EventDBClasses.PlayerEventResponseType.Yes ? "going"
                           : mine.Type == EventDBClasses.PlayerEventResponseType.Interested ? "interested"
                           : mine.Type == EventDBClasses.PlayerEventResponseType.No ? "not_interested" : null,
            };
        }

        // Public events for everyone; private / unlisted ones only for the host. Live first, then what is coming up
        // (soonest first), then the last few that ended in the past two weeks.
        private static List<object> EventCards(IEnumerable<EventDBClasses.FullPlayerEvent> events, ulong viewer, int max = 30)
        {
            var now = DateTime.UtcNow;
            var shown = events
                .Where(e => e.Accessibility == EventDBClasses.PlayerEventAccessibility.Public || (viewer != 0 && e.CreatorPlayerId == viewer))
                .Where(e => e.EndTime.ToUniversalTime() >= now.AddDays(-14))
                .ToList();
            var current = shown.Where(e => e.EndTime.ToUniversalTime() >= now).OrderBy(e => e.StartTime.ToUniversalTime() > now ? 1 : 0).ThenBy(e => e.StartTime).ToList();
            var past = shown.Where(e => e.EndTime.ToUniversalTime() < now).OrderByDescending(e => e.EndTime).Take(6).ToList();
            return current.Concat(past).Take(max).Select(e => EventCard(e, viewer)).ToList();
        }

        // GET /api/site/profile/{handle}/events - the events this player is hosting.
        [HttpGet("/api/site/profile/{handle}/events")]
        public IActionResult ProfileEvents(string handle)
        {
            try
            {
                player_data? full = PlayerDB.GetFullAccountByName(handle);
                if (full == null && ulong.TryParse(handle, out var byId)) full = PlayerDB.GetFullAccount(byId);
                if (full?.player == null) return NotFound(new { ok = false, error = "player not found" });
                return Ok(new { ok = true, events = EventCards(EventDB.GetEventsByCreator(full.playerid), ViewerId()) });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] profile events failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load events" });
            }
        }

        // GET /api/site/rooms/{id}/events - the events booked in this room (only for a room the site shows).
        [HttpGet("/api/site/rooms/{roomId:long}/events")]
        public IActionResult RoomEvents(long roomId)
        {
            try
            {
                var room = roomId > 0 ? RoomDB.GetRoom((ulong)roomId) : null;
                if (!RoomIsShowable(room)) return Ok(new { ok = true, events = new List<object>() });
                return Ok(new { ok = true, events = EventCards(EventDB.GetEventsForRoom((ulong)roomId), ViewerId()) });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] room events failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load events" });
            }
        }

        // GET /api/site/events/{id} - one event for its page: the card plus who is going (up to 30, with pictures).
        [HttpGet("/api/site/events/{eventId:long}")]
        public IActionResult GetEvent(long eventId)
        {
            try
            {
                var ev = eventId > 0 ? EventDB.GetFullEventById((ulong)eventId) : null;
                ulong viewer = ViewerId();
                if (ev == null || (ev.Accessibility != EventDBClasses.PlayerEventAccessibility.Public && !(viewer != 0 && ev.CreatorPlayerId == viewer)))
                    return NotFound(new { ok = false, error = "event not found" });
                var going = (ev.Responses ?? new List<EventDBClasses.FullEventResponses>())
                    .Where(r => r.Type == EventDBClasses.PlayerEventResponseType.Yes)
                    .OrderBy(r => r.CreatedAt).Select(r => r.PlayerId).Distinct().Take(30)
                    .Select(pid => { var a = PlayerDB.GetAccountById(pid); return a == null ? null : new { id = a.Id, username = a.Username, display_name = a.DisplayName, profile_image = ImgUrl(a.profileImage) }; })
                    .Where(x => x != null).ToList();
                return Ok(new { ok = true, @event = EventCard(ev, viewer), going_players = going });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] event {eventId} failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load the event" });
            }
        }

        // POST /api/site/events/{id}/join - the website's JOIN button. If you are online in the game right now, your
        // game is pointed at the event's room instance (the same decision the game's own Join button makes) and walks
        // there on its next heartbeat - up to ~20 s. Not online -> it says so and does nothing.
        [HttpPost("/api/site/events/{eventId:long}/join")]
        public IActionResult JoinEventFromSite(long eventId)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "Log in first." });
            if (!RateLimit.TryConsume($"eventjoin:{me.Id}", cooldownSeconds: 5, out int retryAfter))
                return StatusCode(429, new { ok = false, error = $"Slow down - try again in {retryAfter}s." });

            var ev = eventId > 0 ? EventDB.GetFullEventById((ulong)eventId) : null;
            if (ev == null || (ev.Accessibility != EventDBClasses.PlayerEventAccessibility.Public && ev.CreatorPlayerId != me.Id))
                return NotFound(new { ok = false, error = "event not found" });
            if (ev.EndTime.ToUniversalTime() < DateTime.UtcNow) return BadRequest(new { ok = false, error = "This event has ended." });

            var (online, _) = HeartbeatDB.PeekPresence(me.Id);
            if (!online)
                return BadRequest(new { ok = false, offline = true, error = "You're not online in Campus right now. Start the game, then press Join again." });

            var outcome = EventJoin.Resolve((ulong)eventId, me.Id);
            if (outcome.Session == null)
            {
                string msg = outcome.Code switch
                {
                    HeartbeatDB.MatchmakingErrorCode.EventNotStarted => string.IsNullOrWhiteSpace(outcome.Why) ? "This event hasn't started yet." : char.ToUpper(outcome.Why[0]) + outcome.Why[1..] + (outcome.Why.EndsWith(".") ? "" : "."),
                    HeartbeatDB.MatchmakingErrorCode.Banned => "Your account can't join rooms right now.",
                    HeartbeatDB.MatchmakingErrorCode.RoomIsPrivate => "This event's room is private.",
                    HeartbeatDB.MatchmakingErrorCode.BlockedFromRoom => "You can't join this event.",
                    HeartbeatDB.MatchmakingErrorCode.NoSuchRoom => "This event's room no longer exists.",
                    _ => "Couldn't join the event right now. Try again in a moment."
                };
                return BadRequest(new { ok = false, error = msg });
            }
            // the client acts on its next heartbeat (the same move the admin panel's "force join" makes)
            HeartbeatDB.UpdatePlayerHeartbeat(me.Id, outcome.Session, online: true);
            Console.WriteLine($"[site] {me.Id} pressed Join on event {eventId} '{ev.Name}' -> instance {outcome.Session.roomInstanceId}");
            return Ok(new { ok = true, room = outcome.RoomName, message = $"Sending you to {ev.Name}. It can take up to 20 seconds - keep Campus open." });
        }

        public class EventRsvpBody { public string? response { get; set; } }

        // POST /api/site/events/{id}/respond {response: "going" | "interested" | "not_interested" | "none"} - RSVP from the site; the same
        // answer the game's event page records (one per player, answering again replaces it).
        [HttpPost("/api/site/events/{eventId:long}/respond")]
        public IActionResult RespondToEvent(long eventId, [FromBody] EventRsvpBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var ev = eventId > 0 ? EventDB.GetFullEventById((ulong)eventId) : null;
            if (ev == null) return NotFound(new { ok = false, error = "event not found" });
            if (ev.Accessibility != EventDBClasses.PlayerEventAccessibility.Public && ev.CreatorPlayerId != me.Id)
                return StatusCode(403, new { ok = false, error = "This event is private." });
            if (ev.EndTime.ToUniversalTime() < DateTime.UtcNow) return BadRequest(new { ok = false, error = "This event has ended." });
            var type = (body?.response ?? "").Trim().ToLowerInvariant() switch
            {
                "going" or "yes" => EventDBClasses.PlayerEventResponseType.Yes,
                "interested" or "maybe" => EventDBClasses.PlayerEventResponseType.Interested,
                "not_interested" or "notinterested" or "no" => EventDBClasses.PlayerEventResponseType.No,   // an answer of its own
                _ => EventDBClasses.PlayerEventResponseType.None,                                          // "none": take the answer back
            };
            var updated = EventDB.SetResponse((ulong)eventId, me.Id, type);
            if (updated == null) return NotFound(new { ok = false, error = "event not found" });
            return Ok(new { ok = true, @event = EventCard(updated, me.Id) });
        }

        public class PfpBody { public int photoId { get; set; } }

        // POST /api/site/me/pfp {photoId} - use one of your (or a public) photos as your PROFILE PICTURE, the same
        // way the banner works and the way the game's own "set as profile picture" does (the account points at the
        // saved photo). The website used to download the photo in the browser and upload it back (/api/profile-image):
        // any hiccup there - the 10 s / 10 per hour limit, a picture lock, a dropped request - only showed a toast and
        // the picture stayed as it was on the profile. Pushed to the game too.
        [HttpPost("/api/site/me/pfp")]
        public IActionResult SetMyPfp([FromBody] PfpBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (PlayerDB.IsProfileLocked(me.Id, PlayerDB.ProfileLock.Picture))
                return BadRequest(new { ok = false, error = PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Picture) });
            if (body == null || body.photoId <= 0) return BadRequest(new { ok = false, error = "no photo chosen" });
            var img = ImageMetadataDB.GetById(body.photoId);
            if (img == null || string.IsNullOrWhiteSpace(img.ImageName)) return NotFound(new { ok = false, error = "photo not found" });
            if (!CanSeePhoto(img, me.Id, AreFriends)) return StatusCode(403, new { ok = false, error = "This photo is private." });
            PlayerDB.SetPlayerImageName(img.ImageName, me.Id);
            Notifications.FireAndForget(Notifications.PushAccountUpdate(me.Id), "site pfp-from-photo refresh");
            Console.WriteLine($"[site] {me.Id} set photo {body.photoId} ({img.ImageName}) as their profile picture");
            return Ok(new { ok = true, profile_image = ImgUrl(img.ImageName) });
        }

        // DELETE /api/site/photos/{id}  (owner or staff) - the same delete the game does.
        [HttpDelete("/api/site/photos/{id:int}")]
        [HttpPost("/api/site/photos/{id:int}/delete")]
        public IActionResult DeletePhoto(int id)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            var img = ImageMetadataDB.GetById(id);
            if (img == null) return NotFound(new { ok = false, error = "photo not found" });
            if (img.PlayerId != me.Id && !PlayerDB.CheckDevFlag(me.Id)) return StatusCode(403, new { ok = false, error = "not your photo" });
            ImageMetadataDB.DeleteImage(id);
            Console.WriteLine($"[site] {me.Id} deleted photo {id}");
            return Ok(new { ok = true });
        }
        public class PhotoUntagBody { public ulong playerId { get; set; } }

        // POST /api/site/photos/{id}/visibility  {visibility:"public"|"friends"|"private"}
        // Owner-only. public/private map straight to the in-game Accessibility (0/1); "friends"
        // stores Private + FriendsOnly so it never leaks in-game but shows to friends on the site.
        [HttpPost("/api/site/photos/{id:int}/visibility")]
        public IActionResult SetPhotoVisibility(int id, [FromBody] PhotoVisibilityBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });

            var img = ImageMetadataDB.GetById(id);
            if (img == null) return NotFound(new { ok = false, error = "photo not found" });
            if (img.PlayerId != me.Id) return StatusCode(403, new { ok = false, error = "not your photo" });

            ImageMetadataDB.ImageAccessibility acc;
            bool friendsOnly;
            switch ((body?.visibility ?? "").Trim().ToLowerInvariant())
            {
                case "public":  acc = ImageMetadataDB.ImageAccessibility.Public;  friendsOnly = false; break;
                case "friends": acc = ImageMetadataDB.ImageAccessibility.Private; friendsOnly = true;  break;
                case "private": acc = ImageMetadataDB.ImageAccessibility.Private; friendsOnly = false; break;
                default: return BadRequest(new { ok = false, error = "visibility must be public, friends or private" });
            }

            ImageMetadataDB.SetVisibility(id, acc, friendsOnly);
            Console.WriteLine($"[site] photo {id} visibility -> {body!.visibility} by {me.Id}");
            return Ok(new { ok = true, visibility = friendsOnly ? "friends" : (acc == ImageMetadataDB.ImageAccessibility.Public ? "public" : "private") });
        }

        // POST /api/site/photos/{id}/untag  {playerId}
        // Owner-only: remove a tagged player from the photo. Returns the updated tag list.
        [HttpPost("/api/site/photos/{id:int}/untag")]
        public IActionResult UntagPhoto(int id, [FromBody] PhotoUntagBody? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });

            var img = ImageMetadataDB.GetById(id);
            if (img == null) return NotFound(new { ok = false, error = "photo not found" });
            if (img.PlayerId != me.Id) return StatusCode(403, new { ok = false, error = "not your photo" });
            if (body == null || body.playerId == 0) return BadRequest(new { ok = false, error = "playerId is required" });

            var updated = ImageMetadataDB.RemoveTag(id, body.playerId);
            Console.WriteLine($"[site] photo {id} untag {body.playerId} by {me.Id}");
            return Ok(new { ok = true, taggedPlayers = ResolveTagged(updated) });
        }

        // ---- presence ---------------------------------------------------------------------

        // GET /api/site/online
        // Who is actually in-game right now, and where. Replaces data.js `onlineFriends`.
        // Public (the site already has an "online players" surface), so it exposes only
        // display name, avatar and room NAME - never room ids or instance data.
        [HttpGet("/api/site/online")]
        public IActionResult Online(int take = 20)
        {
            take = Math.Clamp(take, 1, 100);
            try
            {
                var results = new List<object>();

                foreach (var (playerId, _) in CampusLiveSource.Controllers.NotificationsController.WebSockets)
                {
                    if (results.Count >= take) break;

                    var acc = PlayerDB.GetAccountById(playerId);
                    if (acc == null) continue;

                    string? where = ShowableRoomOf(playerId);

                    results.Add(new
                    {
                        id = acc.Id,
                        username = acc.Username,
                        display_name = acc.DisplayName,
                        profile_image = ImgUrl(acc.profileImage),
                        // The DB sentinel is the literal string "not in a room." - map it to
                        // something a person would read rather than showing it verbatim.
                        where = string.IsNullOrWhiteSpace(where) ? "Online now" : where,
                    });
                }

                return Ok(new { ok = true, results, total = CampusLiveSource.Controllers.NotificationsController.WebSockets.Count });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] online list failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load online players" });
            }
        }

        // GET /api/site/friends - the signed-in user's friends, with live presence.
        // Replaces the localStorage-only friends list, which was per-browser and invisible
        // to the game.
        // GET /api/site/stats - the signed-in creator's numbers: room visits, what sold, tokens
        // pending and earned, per item (with its picture). Drives the website's Stats page.
        [HttpGet("/api/site/stats")]
        public IActionResult SiteStats()
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            try
            {
                var rooms = RoomDB.GetRoomsByCreator(me.Id).Where(r => !r.IsDorm).ToList();
                long visits = rooms.Sum(r => (long)(r.Stats?.VisitCount ?? 0));
                long cheers = rooms.Sum(r => (long)(r.Stats?.CheerCount ?? 0));
                var inventions = InventionDB.GetByCreator(me.Id);
                var (pending, paid, next) = EarningsDB.Summary(me.Id);
                var sales = EarningsDB.ForPlayer(me.Id, 5000);

                string? Image(string kind, string itemId)
                {
                    try
                    {
                        switch (kind)
                        {
                            case "invention": return int.TryParse(itemId, out var iid) ? ImgUrl(InventionDB.GetById(iid)?.ImageName) : null;
                            case "ugc": return ImgUrl((string?)CustomItemsCache.Get(itemId)?["ImageName"]);
                            case "roomkey": return long.TryParse(itemId, out var kid) ? ImgUrl(RoomEconDB.GetKey(kid)?.ImageName) : null;
                            case "roomcurrency": return Guid.TryParse(itemId, out var cid) ? ImgUrl(RoomEconDB.GetCurrency(cid)?.ImageName) : null;
                            case "roomconsumable": return Guid.TryParse(itemId, out var xid) ? ImgUrl(RoomEconDB.GetConsumable(xid)?.ImageName) : null;
                        }
                    }
                    catch { }
                    return null;
                }

                var items = sales.GroupBy(e => (e.Kind, e.ItemId)).Select(g => new
                {
                    kind = g.Key.Kind,
                    itemId = g.Key.ItemId,
                    name = g.OrderByDescending(e => e.BoughtAt).First().ItemName,
                    image = Image(g.Key.Kind, g.Key.ItemId),
                    sold = g.Count(),
                    revenue = g.Sum(e => e.Spent),
                    pending = g.Where(e => e.PaidAt == null).Sum(e => e.Share),
                    earned = g.Where(e => e.PaidAt != null).Sum(e => e.Share),
                    lastSold = g.Max(e => e.BoughtAt).ToString("o"),
                    nextPayout = g.Where(e => e.PaidAt == null).Select(e => (DateTime?)e.PayoutAt).Min()?.ToString("o")
                }).OrderByDescending(x => x.sold).ThenByDescending(x => x.lastSold).ToList();

                return Ok(new
                {
                    ok = true,
                    username = me.Username,
                    tokens = PlayerDB.GetBalance(me.Id, (int)Campus_live_rewrite.api.server.Classes.CurrencyType.RecCenterTokens),
                    level = Campus_live_rewrite.api.server.Classes.Progress.GetPlayerLevel(me.Id)?.Level ?? 1,
                    rooms = rooms.Count,
                    roomVisits = visits,
                    roomCheers = cheers,
                    inventions = inventions.Count,
                    inventionsPublished = inventions.Count(i => i.IsPublished),
                    inventionsBought = inventions.Sum(i => (long)i.NumDownloads),
                    customItems = CustomItemsCache.ByCreatorRecs(me.Id).Count,
                    keys = rooms.Sum(r => RoomEconDB.KeysForRoom((long)r.RoomId).Count),
                    currencies = rooms.Sum(r => RoomEconDB.CurrenciesForRoom((long)r.RoomId).Count),
                    consumables = rooms.Sum(r => RoomEconDB.ConsumablesForRoom((long)r.RoomId).Count),
                    salesCount = sales.Count,
                    revenue = sales.Sum(e => e.Spent),
                    tokensPending = pending,
                    tokensEarned = paid,
                    nextPayout = next?.ToString("o"),
                    percent = ConfigCampus.CreatorEarningsPercent,
                    pendDays = ConfigCampus.CreatorEarningsPendDays,
                    // Per-day totals for the earnings graph (share = your cut, spent = what buyers paid).
                    series = sales.GroupBy(e => e.BoughtAt.ToString("yyyy-MM-dd"))
                        .Select(g => new { d = g.Key, share = g.Sum(e => e.Share), spent = g.Sum(e => e.Spent), n = g.Count() })
                        .OrderBy(x => x.d).ToList(),
                    items,
                    recent = sales.Take(50).Select(e => new
                    {
                        e.Kind, item = e.ItemName, e.Spent, e.Share, buyer = PlayerDB.GetAccountById(e.BuyerId)?.Username ?? e.BuyerId.ToString(),
                        at = e.BoughtAt.ToString("o"), payoutAt = e.PayoutAt.ToString("o"), paid = e.PaidAt != null
                    })
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] stats failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load stats" });
            }
        }

        [HttpGet("/api/site/friends")]
        public IActionResult Friends()
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });

            try
            {
                var rels = FriendsDB.GetRelationships(me.Id) ?? new List<RelationshipDetail>();
                var online = CampusLiveSource.Controllers.NotificationsController.WebSockets.Keys.ToHashSet();

                var results = new List<object>();
                foreach (var r in rels)
                {
                    // GetRelationships already projects from the caller's point of view, so
                    // PlayerID is the OTHER player here.
                    ulong other = r.PlayerID;
                    var acc = PlayerDB.GetAccountById(other);
                    if (acc == null) continue;

                    bool isOnline = online.Contains(other);
                    results.Add(new
                    {
                        id = acc.Id,
                        username = acc.Username,
                        display_name = acc.DisplayName,
                        profile_image = ImgUrl(acc.profileImage),
                        status = r.RelationshipType.ToString(),
                        favourite = r.Favorited != ReciprocalStatus.None,
                        online = isOnline,
                        where = isOnline ? ShowableRoomOf(other) : null,
                        spotify = SpotifyLink.CachedPayloadFor(other, me.Id),
                    });
                }

                return Ok(new { ok = true, results });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[site] friends failed: {ex.Message}");
                return StatusCode(500, new { ok = false, error = "could not load friends" });
            }
        }

        // ---- me ---------------------------------------------------------------------------

        // POST /api/site/profile  {display_name?, bio?}
        // Settings page. These used to be written to localStorage only ('campus-mynick',
        // 'campus-mybio'), so they never reached the game or another browser.
        [HttpPost("/api/site/profile")]
        public IActionResult UpdateProfile([FromBody] ProfileEdit? body)
        {
            var me = CurrentUser();
            if (me == null) return Unauthorized(new { ok = false, error = "not signed in" });
            if (body == null) return BadRequest(new { ok = false, error = "nothing to update" });

            bool changed = false;

            // Name lock (admin panel): neither name may change while it is on. Checked before
            // anything is written so a locked player can still edit their bio.
            bool nameLocked = PlayerDB.IsNameLocked(me.Id);
            if (nameLocked && body.username != null && !string.Equals(body.username.Trim(), me.Username, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { ok = false, error = "Your name has been locked by a moderator and cannot be changed." });
            if (nameLocked && body.display_name != null && body.display_name.Trim() != (me.DisplayName ?? ""))
                return BadRequest(new { ok = false, error = "Your name has been locked by a moderator and cannot be changed." });

            // @ name (username). Unique, so this can fail; check it FIRST before writing anything.
            if (body.username != null)
            {
                string un = body.username.Trim();
                if (!string.Equals(un, me.Username, StringComparison.OrdinalIgnoreCase))
                {
                    if (un.Length is < 3 or > 24)
                        return BadRequest(new { ok = false, error = "@ name must be 3-24 characters." });
                    if (!System.Text.RegularExpressions.Regex.IsMatch(un, "^[A-Za-z0-9_.]+$"))
                        return BadRequest(new { ok = false, error = "@ name can only use letters, numbers, _ and ." });
                    if (!PlayerDB.SetPlayerUsername(un, me.Id))
                        return BadRequest(new { ok = false, error = "That @ name is already taken." });
                    changed = true;
                }
            }

            if (body.display_name != null)
            {
                string dn = body.display_name.Trim();
                if (dn.Length is < 1 or > 24)
                    return BadRequest(new { ok = false, error = "Display name must be 1-24 characters." });
                PlayerDB.SetDisplayName(dn, me.Id);
                changed = true;
            }

            if (body.bio != null && PlayerDB.IsProfileLocked(me.Id, PlayerDB.ProfileLock.Bio)
                && body.bio.Trim() != (PlayerDB.GetFullAccount(me.Id)?.player_Extra?.Bio ?? "").Trim())
                return BadRequest(new { ok = false, error = PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Bio) });

            if (body.bio != null)
            {
                string bio = body.bio.Trim();
                if (bio.Length > 300)
                    return BadRequest(new { ok = false, error = "Bio must be 300 characters or fewer." });
                PlayerDB.SetPlayerBio(bio, me.Id);
                changed = true;
            }

            // Push the change to the game (and anyone viewing this profile) so a new @name /
            // display name shows without a relog. AccountUpdate carries name fields; bio isn't in
            // that DTO, so a bio change still persists and shows on the next profile open in-game.
            if (changed)
                Notifications.FireAndForget(Notifications.PushAccountUpdate(me.Id), "site profile edit");

            var full = PlayerDB.GetFullAccount(me.Id);
            return Ok(new
            {
                ok = true,
                username = full?.player?.Username,
                display_name = full?.player?.DisplayName,
                bio = full?.player_Extra?.Bio ?? "",
            });
        }

        public class ProfileEdit
        {
            public string? username { get; set; }
            public string? display_name { get; set; }
            public string? bio { get; set; }
        }

    }
}
