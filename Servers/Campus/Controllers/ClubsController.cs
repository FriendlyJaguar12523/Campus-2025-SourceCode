using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes;

namespace Campus_live_rewrite.Controllers
{
    [Route("clubs")]
    public class ClubsController : ControllerBase
    {
        // subscription/mine/member
        // club/categoryTags // the tags for clubs // todo: get club tags from rec.net
        // club/home/me // return 404 if you don't have a club home set, i think this return just "1" room id
        // announcements/v2/mine/unread
        // announcements/v2/subscription/mine/unread
        // /clubs/club/mine/created

        // Club category tags for the Clubs browse/filter UI AND the "Create a Club" screen's
        // "Primary Club Tag" dropdown. An empty array left that dropdown empty, which disables
        // the CREATE button (the client requires a tag). The fixed set matches the RecFlare
        // reference (clubs.app.ts /club/categoryTags).
        public static readonly string[] CategoryTags =
            { "Social", "Creative", "Competitive", "Casual", "Entertainment" };

        [HttpGet("club/categoryTags")]
        public ContentResult ClubCategoryTags() => new ContentResult
        {
            Content = JsonConvert.SerializeObject(CategoryTags),
            ContentType = "application/json",
            StatusCode = 200
        };

        // ── club DTO builders (match the RecFlare wire shapes) ──────────────────────────────
        private static ContentResult ClubJson(object o, int status = 200) => new ContentResult
        {
            Content = JsonConvert.SerializeObject(o),
            ContentType = "application/json",
            StatusCode = status
        };
        private static object ClubError(string message) => new { error = message, success = false, value = (object?)null };

        private static object ClubDtoObj(ClubDB.Club c) => new
        {
            c.ClubId,
            c.Name,
            c.Description,
            c.Category,
            c.Visibility,
            c.Joinability,
            c.AllowJuniors,
            c.MainImageName,
            c.ClubType,
            ClubhouseRoomId = c.ClubhouseRoomId,
            CreatorAccountId = c.CreatorAccountId,
            c.IsRRO,
            c.MinLevel,
            c.State,
            MemberCount = ClubDB.MemberCount(c.ClubId),
        };

        private static object ClubPermObj(int clubId, int tier) => new
        {
            ClubId = clubId,
            Type = tier,
            ApproveMember = tier >= ClubDB.TierModerator,
            BanUnban = tier >= ClubDB.TierModerator,
            CreateEvent = tier >= ClubDB.TierCoOwner,
            EditDetails = tier >= ClubDB.TierCoOwner,
            EditPermissionSettings = tier >= ClubDB.TierCoOwner,
            PostAnnouncement = tier >= ClubDB.TierModerator,
        };

        private static object ClubMemberObj(ClubDB.ClubMember m) => new
        {
            m.ClubMemberId,
            m.ClubId,
            AccountId = m.AccountId,
            MembershipType = m.MembershipType,
            CreatedAt = m.CreatedAt,
        };

        // A club gallery image as a whole SavedImage record (the client deserializes each entry
        // into an object; a bare name fails its parser). Reads the real row when the name is a
        // known image, else synthesizes a minimal record around the name.
        private static object ClubImageObj(string name)
        {
            var img = ImageMetadataDB.GetImageByName(name);
            if (img != null)
                return new
                {
                    Id = img.Id,
                    Type = (int)img.Type,
                    Accessibility = (int)img.Accessibility,
                    AccessibilityLocked = img.AccessibilityLocked,
                    ImageName = img.ImageName,
                    Description = img.Description,
                    PlayerId = img.PlayerId,
                    TaggedPlayerIds = img.TaggedPlayerIds ?? new List<ulong>(),
                    RoomId = (int?)img.RoomId,
                    PlayerEventId = (ulong?)img.PlayerEventId,
                    CreatedAt = img.CreatedAt,
                    CheerCount = img.CheerCount,
                    CommentCount = img.CommentCount,
                };
            return new
            {
                Id = 0,
                Type = 3,
                Accessibility = 1,
                AccessibilityLocked = false,
                ImageName = name,
                Description = (string?)null,
                PlayerId = 0UL,
                TaggedPlayerIds = new List<ulong>(),
                RoomId = (int?)null,
                PlayerEventId = (ulong?)null,
                CreatedAt = DateTime.UtcNow,
                CheerCount = 0,
                CommentCount = 0,
            };
        }

        private static object ClubDetailsObj(ClubDB.Club c, ulong viewer) => new
        {
            AdditionalImages = (c.AdditionalImageNames ?? new List<string>()).Select(ClubImageObj).ToList(),
            Club = ClubDtoObj(c),
            ClubId = c.ClubId,
            CoownerPermissions = ClubPermObj(c.ClubId, ClubDB.TierCoOwner),
            CustomTags = c.CustomTags ?? new List<string>(),
            MemberPermissions = ClubPermObj(c.ClubId, ClubDB.TierMember),
            ModeratorPermissions = ClubPermObj(c.ClubId, ClubDB.TierModerator),
            MyMembershipType = ClubDB.MyMembership(c.ClubId, viewer),
        };

        // Accept an enum arriving as a name or an ordinal (the client sends either).
        private static int ParseEnum(string? raw, Dictionary<string, int> names, int fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            raw = raw.Trim();
            if (int.TryParse(raw, out var n)) return n;
            foreach (var kv in names) if (string.Equals(kv.Key, raw, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return fallback;
        }
        private static bool ParseBool(string? raw, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            raw = raw.Trim();
            if (bool.TryParse(raw, out var b)) return b;
            return raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        // POST /clubs/club/create - the "Create a Club" screen. Form: name, description,
        // category, and optionally visibility/joinability/allowJuniors/minLevel. Returns the
        // { error, success, value } envelope carrying the new club's full details.
        [HttpPost("club/create")]
        public ContentResult CreateClub()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            string Field(string name) => Request.HasFormContentType ? Request.Form[name].ToString() : "";

            string clubName = Field("name")?.Trim() ?? "";
            string description = Field("description") ?? "";
            if (string.IsNullOrWhiteSpace(clubName))
                return ClubJson(ClubError("You must enter a name for your club."), 400);
            if (clubName.Length > ClubDB.MaxNameLength)
                return ClubJson(ClubError($"Club names can be at most {ClubDB.MaxNameLength} characters."), 400);
            if ((description ?? "").Length > ClubDB.MaxDescriptionLength)
                return ClubJson(ClubError($"Club descriptions can be at most {ClubDB.MaxDescriptionLength} characters."), 400);

            int visibility = ParseEnum(Field("visibility"),
                new() { ["Private"] = 0, ["Public"] = 1 }, 1);
            int joinability = ParseEnum(Field("joinability"),
                new() { ["Open"] = 0, ["InviteOnly"] = 1, ["AskToJoin"] = 2 }, 0);
            bool allowJuniors = ParseBool(Field("allowJuniors"), true);
            int minLevel = int.TryParse(Field("minLevel"), out var ml) ? ml : 0;
            string category = Field("category")?.Trim() ?? "";

            var club = ClubDB.CreateClub(account.Id, clubName, description, category,
                                         visibility, joinability, allowJuniors, minLevel);
            Console.WriteLine($"[clubs] '{club.Name}' (#{club.ClubId}) created by {account.Id}");
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(club, account.Id) });
        }

        // GET /clubs/club/{clubId} - the bare club DTO (404 if none). A creator auto-club id
        // (5000000+) resolves too - the profile's subscription club 404'd here before.
        [HttpGet("club/{clubId:int}")]
        public ContentResult GetClub(int clubId)
        {
            var club = ClubDB.GetById(clubId) ?? (ClubDB.IsCreatorClub(clubId) ? ClubDB.CreatorClub(ClubDB.CreatorOfClub(clubId)) : null);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            return ClubJson(ClubDtoObj(club));
        }

        // ---- HOME CLUB (the club whose clubhouse the player spawns into) ----
        // GET 404s when none is set - that is what the client expects for "no home club"; it
        // errors on an empty object. PUT/DELETE answer the envelope.
        [HttpGet("club/home/me")]
        public ContentResult GetHomeClub()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            int id = ClubDB.GetHomeClubId(account.Id);
            var club = id == 0 ? null : ClubDB.GetById(id);
            if (club == null) return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
            return ClubJson(ClubDtoObj(club));
        }

        [HttpPut("club/home/me")]
        [HttpPost("club/home/me")]
        public ContentResult SetHomeClub()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var f = Campus_live_rewrite.api.server.RoomModeration.Fields(Request);
            int clubId = (int)Campus_live_rewrite.api.server.RoomModeration.Long(f, "clubId", "ClubId", "id");
            if (clubId <= 0) return ClubJson(new { error = "Invalid clubId.", success = false, value = (object?)null }, 400);
            var club = ClubDB.GetById(clubId);
            if (club == null) return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
            if (ClubDB.MyMembership(clubId, account.Id) < 30 && club.CreatorAccountId != account.Id)
                return ClubJson(new { error = "You are not a member of that club.", success = false, value = (object?)null }, 403);
            ClubDB.SetHomeClub(account.Id, clubId);
            return ClubJson(new { error = "", success = true, value = ClubDtoObj(club) });
        }

        [HttpDelete("club/home/me")]
        public ContentResult ClearHomeClub()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            ClubDB.ClearHomeClub(account.Id);
            return ClubJson(new { error = "", success = true, value = (object?)null });
        }

        // GET /clubs/account/{accountId}/clubs - the clubs an account belongs to or created
        // (public ones only for someone else's profile). Bare array of club DTOs.
        [HttpGet("account/{accountId:long}/clubs")]
        [HttpGet("club/account/{accountId:long}/clubs")]
        public ContentResult ClubsOfAccount(long accountId)
        {
            var (account, _) = AuthHelper.ValidateToken(HttpContext);
            ulong who = (ulong)Math.Max(0, accountId);
            var clubs = ClubDB.GetByMember(who).Concat(ClubDB.GetByCreator(who))
                              .GroupBy(c => c.ClubId).Select(g => g.First())
                              .Where(c => account?.Id == who || c.Visibility == 1 || ClubDB.MyMembership(c.ClubId, account?.Id ?? 0) >= 30)
                              .ToList();
            return ClubJson(clubs.Select(ClubDtoObj).ToList());
        }

        // GET /clubs/club/{clubId}/details - the full club-screen payload (NOT enveloped).
        [HttpGet("club/{clubId:int}/details")]
        public ContentResult GetClubDetails(int clubId)
        {
            var club = ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            var (account, _) = AuthHelper.ValidateToken(HttpContext);
            ulong viewer = account?.Id ?? 0;
            return ClubJson(ClubDetailsObj(club, viewer));
        }

        // GET /clubs/club/{clubId}/members - the members list envelope (empty for unknown club).
        [HttpGet("club/{clubId:int}/members")]
        public ContentResult GetClubMembers(int clubId)
        {
            int? tier = int.TryParse(Request.Query["membershipType"].ToString(), out var t) ? t : (int?)null;
            string? sortBy = Request.Query["sortBy"].ToString();
            var members = ClubDB.GetMembers(clubId, tier, string.IsNullOrEmpty(sortBy) ? null : sortBy)
                                .Select(ClubMemberObj).ToList();
            return ClubJson(new { error = "", success = true, value = members });
        }

        // PUT /clubs/club/{clubId}/modifydetails  (and /modify) — edit a club (co-owner+). The
        // create flow's step-4 (privacy / member-access / custom tags) PUTs here; it was missing,
        // so it showed "Failed to Modify Club Details". Form fields (all optional, absent = keep):
        // name, description, category, visibility, joinability, allowJuniors, minLevel, customTags.
        // Answers the details envelope.
        [HttpPut("club/{clubId:int}/modifydetails")]
        [HttpPut("club/{clubId:int}/modify")]
        public ContentResult ModifyClub(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var club = ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            if (ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner)
                return ClubJson(ClubError("Insufficient permissions."), 403);

            bool Has(string k) => Request.HasFormContentType && Request.Form.ContainsKey(k);
            string F(string k) => Request.HasFormContentType ? Request.Form[k].ToString() : "";

            string? name = Has("name") ? F("name") : null;
            string? desc = Has("description") ? F("description") : null;
            string? cat  = Has("category") ? F("category") : null;
            int? vis  = Has("visibility")  ? ParseEnum(F("visibility"),  new() { ["Private"] = 0, ["Public"] = 1 }, club.Visibility)  : (int?)null;
            int? join = Has("joinability") ? ParseEnum(F("joinability"), new() { ["Open"] = 0, ["InviteOnly"] = 1, ["AskToJoin"] = 2 }, club.Joinability) : (int?)null;
            bool? aj  = Has("allowJuniors") ? ParseBool(F("allowJuniors"), club.AllowJuniors) : (bool?)null;
            int? ml   = (Has("minLevel") && int.TryParse(F("minLevel"), out var mlv)) ? mlv : (int?)null;

            List<string>? tags = null;
            if (Has("customTags"))
                tags = Request.Form["customTags"]
                    .SelectMany(s => (s ?? "").Split(','))
                    .Select(t => t.Trim()).Where(t => t.Length > 0)
                    .Distinct().Take(3).ToList();

            var updated = ClubDB.UpdateDetails(clubId, name, desc, cat, vis, join, aj, ml, tags);
            Console.WriteLine($"[clubs] {account.Id} modified club {clubId}");
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(updated!, account.Id) });
        }

        // ---- browse / profile ------------------------------------------------------------
        //
        // GET /clubs/club/search?sort=0&category=Social&count=6 - the Clubs page's rows, one
        // request per category. This 404'd (46 times in the log), which is why nobody could see
        // anyone's club: the browse page was empty. Reference: public, non-subscription clubs,
        // { Clubs, ContinuationToken, TotalClubs }.
        [HttpGet("club/search")]
        public ContentResult SearchClubs([FromQuery] string? category, [FromQuery] string? query, [FromQuery] string? sort, [FromQuery] int count = 30)
        {
            if (count <= 0 || count > 100) count = 30;
            var (page, total) = ClubDB.Search(category, query, sort, count);
            return ClubJson(new
            {
                Clubs = page.Select(ClubDtoObj).ToList(),
                ContinuationToken = (string?)null,
                TotalClubs = total
            });
        }

        // GET /clubs/club/account/{accountId}/created - the clubs card on a PROFILE (404'd). The
        // public clubs that account created, as a bare array like club/mine/created; the owner
        // looking at their own profile sees private ones too.
        [HttpGet("club/account/{accountId:long}/created")]
        public ContentResult ClubsCreatedBy(long accountId)
        {
            var (account, _) = AuthHelper.ValidateToken(HttpContext);
            var clubs = (account != null && (long)account.Id == accountId)
                ? ClubDB.GetByCreator((ulong)accountId)
                : ClubDB.PublicByCreator((ulong)accountId);
            return ClubJson(clubs.Select(ClubDtoObj).ToList());
        }

        // GET /clubs/club/{clubId}/members/{membershipType} - one tier of the member list (the
        // client asks /members/0 for the pending requests it can approve). Same envelope.
        [HttpGet("club/{clubId:int}/members/{membershipType:int}")]
        public ContentResult GetClubMembersOfTier(int clubId, int membershipType)
        {
            var members = ClubDB.GetMembers(clubId, membershipType == 0 ? ClubDB.TierPendingRequest : membershipType, null)
                                .Select(ClubMemberObj).ToList();
            return ClubJson(new { error = "", success = true, value = members });
        }

        // ---- membership --------------------------------------------------------------------
        private ContentResult DetailsEnvelope(int clubId, ulong viewer)
        {
            var club = ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(club, viewer) });
        }

        // PUT /clubs/club/{id}/members/requesttojoin - the JOIN button. Open club -> member,
        // AskToJoin -> pending, InviteOnly -> refused. Answers the details envelope so the client
        // reads its new MyMembershipType.
        [HttpPut("club/{clubId:int}/members/requesttojoin")]
        [HttpPost("club/{clubId:int}/members/requesttojoin")]
        public ContentResult RequestToJoin(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (ClubDB.IsCreatorClub(clubId) || ClubDB.GetById(clubId) == null)
                return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            int tier = ClubDB.RequestJoin(clubId, account.Id);
            Console.WriteLine($"[clubs] {account.Id} asked to join club {clubId} -> tier {tier}");
            return DetailsEnvelope(clubId, account.Id);
        }

        // POST /clubs/club/{id}/join and /leave - the bare-club forms of the same two actions.
        [HttpPost("club/{clubId:int}/join")]
        public ContentResult JoinClub(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var club = ClubDB.IsCreatorClub(clubId) ? null : ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            ClubDB.RequestJoin(clubId, account.Id);
            return ClubJson(ClubDtoObj(ClubDB.GetById(clubId)!));
        }

        [HttpPost("club/{clubId:int}/leave")]
        public ContentResult LeaveClubBare(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var club = ClubDB.IsCreatorClub(clubId) ? null : ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            if (club.CreatorAccountId == account.Id)
                return ClubJson(ClubError("You created this club - delete it instead of leaving."), 403);
            ClubDB.Leave(clubId, account.Id);
            return ClubJson(ClubDtoObj(ClubDB.GetById(clubId)!));
        }

        // POST/PUT /clubs/club/{id}/members/leave - leave (or withdraw a request); details envelope.
        [HttpPost("club/{clubId:int}/members/leave")]
        [HttpPut("club/{clubId:int}/members/leave")]
        public ContentResult LeaveClub(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var club = ClubDB.IsCreatorClub(clubId) ? null : ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            if (club.CreatorAccountId == account.Id)
                return ClubJson(ClubError("You created this club - delete it instead of leaving."), 403);
            ClubDB.Leave(clubId, account.Id);
            Console.WriteLine($"[clubs] {account.Id} left club {clubId}");
            return DetailsEnvelope(clubId, account.Id);
        }

        // PUT /clubs/club/{id}/members/invite - a co-owner adds / promotes / demotes / bans someone
        // (form: accountId, membershipType; absent tier = Member). Also how a pending request is
        // approved (membershipType=10) or denied (membershipType=0). Never the creator.
        [HttpPut("club/{clubId:int}/members/invite")]
        [HttpPost("club/{clubId:int}/members/invite")]
        [HttpPut("club/{clubId:int}/members/modify")]
        [HttpPost("club/{clubId:int}/members/modify")]
        public ContentResult InviteMember(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var club = ClubDB.IsCreatorClub(clubId) ? null : ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            int me = ClubDB.MyMembership(clubId, account.Id);
            if (me < ClubDB.TierModerator) return ClubJson(ClubError("Insufficient permissions."), 403);

            string F(string k) => Request.HasFormContentType ? Request.Form[k].ToString() : Request.Query[k].ToString();
            if (!ulong.TryParse(F("accountId"), out var target) || target == 0)
                return ClubJson(ClubError("Invalid accountId."), 400);
            int tier = int.TryParse(F("membershipType"), out var t) ? t : ClubDB.TierMember;
            // A moderator may approve/deny/ban members; only a co-owner hands out staff tiers.
            if (tier >= ClubDB.TierModerator && me < ClubDB.TierCoOwner)
                return ClubJson(ClubError("Insufficient permissions."), 403);
            if (target == club.CreatorAccountId)
                return ClubJson(ClubError("The club's creator can't be changed."), 400);
            if (!ClubDB.SetTier(clubId, target, tier))
                return ClubJson(ClubError("Invalid membership type."), 400);
            Console.WriteLine($"[clubs] {account.Id} set {target} to tier {tier} in club {clubId}");
            return DetailsEnvelope(clubId, account.Id);
        }

        // DELETE /clubs/club/{id}/members/{accountId} - kick (moderator+, never the creator).
        [HttpDelete("club/{clubId:int}/members/{accountId:long}")]
        public ContentResult KickMember(int clubId, long accountId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var club = ClubDB.IsCreatorClub(clubId) ? null : ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            if (ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierModerator) return ClubJson(ClubError("Insufficient permissions."), 403);
            if ((ulong)accountId == club.CreatorAccountId) return ClubJson(ClubError("The club's creator can't be removed."), 400);
            ClubDB.SetTier(clubId, (ulong)accountId, ClubDB.TierNone);
            return DetailsEnvelope(clubId, account.Id);
        }

        // PUT /clubs/club/{id}/clubhouse - point the club at its clubhouse room (form roomId; 0 clears).
        [HttpPut("club/{clubId:int}/clubhouse")]
        public ContentResult SetClubhouse(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner) return ClubJson(ClubError("Insufficient permissions."), 403);
            string raw = Request.HasFormContentType ? Request.Form["roomId"].ToString() : Request.Query["roomId"].ToString();
            int? roomId = int.TryParse(raw, out var r) && r > 0 ? r : (int?)null;
            var club = ClubDB.SetClubhouse(clubId, roomId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(club, account.Id) });
        }

        // PUT /clubs/club/{id}/minlevel - the club's minimum level (form minLevel).
        [HttpPut("club/{clubId:int}/minlevel")]
        public ContentResult SetMinLevel(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            if (ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner) return ClubJson(ClubError("Insufficient permissions."), 403);
            string raw = Request.HasFormContentType ? Request.Form["minLevel"].ToString() : Request.Query["minLevel"].ToString();
            int ml = int.TryParse(raw, out var v) ? Math.Max(0, v) : 0;
            var updated = ClubDB.UpdateDetails(clubId, null, null, null, null, null, null, ml, null);
            if (updated == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(updated, account.Id) });
        }

        // DELETE /clubs/club/{id} - the creator deletes the club (memberships and announcements go with it).
        [HttpDelete("club/{clubId:int}")]
        public ContentResult DeleteClub(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var club = ClubDB.IsCreatorClub(clubId) ? null : ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            if (club.CreatorAccountId != account.Id) return ClubJson(ClubError("Only the club's creator can delete it."), 403);
            ClubDB.Delete(clubId);
            Console.WriteLine($"[clubs] {account.Id} deleted club {clubId} ('{club.Name}')");
            return ClubJson(new { error = "", success = true, value = (object?)null });
        }

        // GET /clubs/club/{clubId}/hasDisabledClubChat - nothing disables club chat yet.
        [HttpGet("club/{clubId:int}/hasDisabledClubChat")]
        public ContentResult ClubChatDisabled(int clubId) => ClubJson(false);

        // viewer / tier: who is looking, so their own posts (and a co-owner's board) carry the
        // edit / delete flags the client shows the buttons from.
        private static object AnnouncementObj(ClubDB.ClubAnnouncement a, ulong viewer = 0, int viewerTier = 0)
        {
            bool mine = viewer != 0 && a.AccountId == viewer;
            bool canEdit = mine || viewerTier >= ClubDB.TierCoOwner;
            return new
            {
                a.AnnouncementId,
                a.ClubId,
                a.AccountId,
                CreatorAccountId = a.AccountId,
                PlayerId = a.AccountId,
                Title = a.Title ?? "",
                Body = a.Body ?? "",
                ImageName = a.ImageName ?? "",
                Meta = a.Meta ?? "",
                CreatedAt = a.CreatedAt.ToUniversalTime().ToString("o"),
                ModifiedAt = (a.ModifiedAt ?? a.CreatedAt).ToUniversalTime().ToString("o"),
                IsMine = mine,
                CanEdit = canEdit,
                CanDelete = canEdit,
                IsEditable = canEdit,
            };
        }

        // GET /clubs/announcements/club/{clubId} - the noticeboard, newest first, in the
        // reference envelope. Reading it marks everything read for the viewer (that is what
        // clears the unread badge the two /v2/.../unread lists drive). Works for creator
        // (subscription) clubs too - a creator's posts reach every subscriber this way.
        [HttpGet("announcements/club/{clubId:int}")]
        public ContentResult ClubAnnouncements(int clubId)
        {
            var (account, _) = AuthHelper.ValidateToken(HttpContext);
            ulong viewer = account?.Id ?? 0;
            var list = ClubDB.GetAnnouncements(clubId);
            int lastRead = ClubDB.LastReadAnnouncementId(clubId, viewer);
            if (list.Count > 0 && viewer != 0) ClubDB.MarkAnnouncementsRead(clubId, viewer, list[0].AnnouncementId);
            return ClubJson(new
            {
                error = "",
                success = true,
                value = new
                {
                    Announcements = list.Select(a => AnnouncementObj(a, viewer, ClubDB.MyMembership(clubId, viewer))).ToList(),
                    ClubId = clubId,
                    LastAnnouncementId = list.Count > 0 ? list[0].AnnouncementId : (int?)null,
                    LastReadAnnouncementId = lastRead,
                }
            });
        }

        // POST /clubs/announcements/club/{clubId} - post to the noticeboard. Form fields title /
        // body / imageName / meta (case-insensitive). Co-owner or above; for a creator club that
        // is the creator. The envelope's value is the new announcement's id (reference).
        [HttpPost("announcements/club/{clubId:int}")]
        public ContentResult PostClubAnnouncement(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var club = ClubDB.GetById(clubId);
            if (club == null) return ClubJson(ClubError("No such club."), 404);
            if (ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner)
                return ClubJson(ClubError("Insufficient permissions."), 403);

            string? Field(string name)
            {
                if (!Request.HasFormContentType) return null;
                foreach (var k in Request.Form.Keys)
                    if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return Request.Form[k].ToString();
                return null;
            }
            var a = ClubDB.PostAnnouncement(clubId, account.Id, Field("title"), Field("body"), Field("imageName"), Field("meta"));
            Console.WriteLine($"[clubs] {account.Id} posted announcement {a.AnnouncementId} to club {clubId} ('{a.Title}')");

            // Tell the members it's there: a creator club's subscribers, a regular club's members.
            try
            {
                var members = ClubDB.GetMembers(clubId, null, null).Select(m => m.AccountId).Where(id => id != account.Id).Distinct().ToList();
                foreach (var m in members)
                    Notifications.FireAndForget(Notifications.SendToPlayer(m, JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                    {
                        Id = "ClubAnnouncementReceived",
                        Msg = new { ClubId = clubId, AnnouncementId = a.AnnouncementId, ClubName = club.Name, Title = a.Title }
                    })), "club-announcement");
            }
            catch (Exception ex) { Console.WriteLine($"[clubs] announcement push failed: {ex.Message}"); }

            return ClubJson(new { error = "", success = true, value = a.AnnouncementId });
        }

        // GET /clubs/announcements/club/{clubId}/{announcementId} - one announcement (the client
        // opens it on click). Was 404.
        [HttpGet("announcements/club/{clubId:int}/{announcementId:int}")]
        public ContentResult GetClubAnnouncement(int clubId, int announcementId)
        {
            var (account, _) = AuthHelper.ValidateToken(HttpContext);
            var a = ClubDB.GetAnnouncements(clubId).FirstOrDefault(x => x.AnnouncementId == announcementId);
            if (a == null) return ClubJson(ClubError("No such announcement."), 404);
            return ClubJson(new { error = "", success = true, value = AnnouncementObj(a, account?.Id ?? 0, ClubDB.MyMembership(clubId, account?.Id ?? 0)) });
        }

        // PUT/POST /clubs/announcements/club/{clubId}/{announcementId} - edit (the poster, or a
        // co-owner+ / the creator). Form fields title / body / imageName / meta; only the ones
        // sent change. Was 404, so the edit button had nothing to save to.
        [HttpPut("announcements/club/{clubId:int}/{announcementId:int}")]
        [HttpPost("announcements/club/{clubId:int}/{announcementId:int}")]
        [HttpPost("announcements/club/{clubId:int}/{announcementId:int}/edit")]
        public ContentResult EditClubAnnouncement(int clubId, int announcementId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var existing = ClubDB.GetAnnouncements(clubId).FirstOrDefault(a => a.AnnouncementId == announcementId);
            if (existing == null) return ClubJson(ClubError("No such announcement."), 404);
            if (existing.AccountId != account.Id && ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner && !PlayerDB.CheckDevFlag(account.Id))
                return ClubJson(ClubError("Insufficient permissions."), 403);
            var f = Campus_live_rewrite.api.server.RoomModeration.Fields(Request);
            string? title = Campus_live_rewrite.api.server.RoomModeration.Str(f, "title", "Title");
            string? body = Campus_live_rewrite.api.server.RoomModeration.Str(f, "body", "Body", "text", "Text");
            string? image = Campus_live_rewrite.api.server.RoomModeration.Str(f, "imageName", "ImageName");
            string? meta = Campus_live_rewrite.api.server.RoomModeration.Str(f, "meta", "Meta");
            var updated = ClubDB.EditAnnouncement(announcementId, title, body, image, meta);
            if (updated == null) return ClubJson(ClubError("No such announcement."), 404);
            Console.WriteLine($"[clubs] {account.Id} edited announcement {announcementId} in club {clubId}");
            return ClubJson(new { error = "", success = true, value = AnnouncementObj(updated, account.Id, ClubDB.MyMembership(clubId, account.Id)) });
        }

        // DELETE /clubs/announcements/club/{clubId}/{announcementId} - co-owner+ (or the poster).
        // POST /clubs/announcements/club/{clubId}/{announcementId}/read - the reader saw it (the
        // unread badge). Idempotent; answers the envelope.
        [HttpPost("announcements/club/{clubId:int}/{announcementId:int}/read")]
        [HttpPut("announcements/club/{clubId:int}/{announcementId:int}/read")]
        public ContentResult MarkAnnouncementRead(int clubId, int announcementId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            ClubDB.MarkAnnouncementsRead(clubId, account.Id, announcementId);
            return ClubJson(new { error = "", success = true, value = (object?)null });
        }

        [HttpDelete("announcements/club/{clubId:int}/{announcementId:int}")]
        public ContentResult DeleteClubAnnouncement(int clubId, int announcementId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;
            var existing = ClubDB.GetAnnouncements(clubId).FirstOrDefault(a => a.AnnouncementId == announcementId);
            if (existing == null) return ClubJson(ClubError("No such announcement."), 404);
            if (existing.AccountId != account.Id && ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner)
                return ClubJson(ClubError("Insufficient permissions."), 403);
            ClubDB.DeleteAnnouncement(announcementId);
            return ClubJson(new { error = "", success = true, value = announcementId });
        }

        // Read the form field "imageName" (case-insensitive), or "".
        private string FormImageName()
        {
            if (!Request.HasFormContentType) return "";
            foreach (var k in Request.Form.Keys)
                if (string.Equals(k, "imageName", StringComparison.OrdinalIgnoreCase))
                    return Request.Form[k].ToString().Trim();
            return "";
        }

        // GET /clubs/club/{clubId}/mainimage - the reference 404s here but the client asks for
        // it, so answer the details envelope (MainImageName rides on value.Club).
        [HttpGet("club/{clubId:int}/mainimage")]
        public ContentResult GetClubMainImage(int clubId)
        {
            var club = ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            var (account, _) = AuthHelper.ValidateToken(HttpContext);
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(club, account?.Id ?? 0) });
        }

        // PUT /clubs/club/{clubId}/mainimage - set the club's main image from an uploaded
        // image name (the "Take a club photo" step). Co-owner or above. Answers the details
        // envelope. Was missing -> the create flow showed "Failed to Modify Club Image".
        [HttpPut("club/{clubId:int}/mainimage")]
        public ContentResult SetClubMainImage(int clubId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var club = ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            if (ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner)
                return ClubJson(ClubError("Insufficient permissions."), 403);

            string name = FormImageName();
            if (string.IsNullOrEmpty(name)) return ClubJson(ClubError("imageName is required."), 400);

            var updated = ClubDB.SetMainImage(clubId, name);
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(updated!, account.Id) });
        }

        // PUT/DELETE /clubs/club/{clubId}/additionalimage/{index} - one gallery image by 0-based
        // position (PUT sets/appends; DELETE removes and shifts up). Co-owner or above.
        [HttpPut("club/{clubId:int}/additionalimage/{index:int}")]
        public ContentResult SetClubGalleryImage(int clubId, int index)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var club = ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            if (ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner)
                return ClubJson(ClubError("Insufficient permissions."), 403);

            string name = FormImageName();
            if (string.IsNullOrEmpty(name)) return ClubJson(ClubError("imageName is required."), 400);

            var updated = ClubDB.SetAdditionalImage(clubId, index, name);
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(updated!, account.Id) });
        }

        [HttpDelete("club/{clubId:int}/additionalimage/{index:int}")]
        public ContentResult DeleteClubGalleryImage(int clubId, int index)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null) return error;

            var club = ClubDB.GetById(clubId);
            if (club == null) return ClubJson(new { error = "No such club.", success = false, value = (object?)null }, 404);
            if (ClubDB.MyMembership(clubId, account.Id) < ClubDB.TierCoOwner)
                return ClubJson(ClubError("Insufficient permissions."), 403);

            var updated = ClubDB.RemoveAdditionalImage(clubId, index);
            return ClubJson(new { error = "", success = true, value = ClubDetailsObj(updated!, account.Id) });
        }

        [HttpGet("club/mine/created")]
        public ContentResult MyClubs()
        {
            // Signed-out -> [] (never error), as the reference does. Otherwise the caller's
            // created clubs as a bare array of club DTOs.
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            var clubs = (error != null || account == null)
                ? new List<ClubDB.Club>()
                : ClubDB.GetByCreator(account.Id);
            return ClubJson(clubs.Select(ClubDtoObj).ToList());
        }

        // GetMyMembershipClubs (the client calls /clubs/club/mine/member). This MUST return
        // [] with 200 - NEVER 404. The People/social panel loads this right after the
        // relationship list, and the RecFlare note is explicit: "an error there breaks the
        // screen." A 404 here threw inside the client's social load and blanked the WHOLE
        // panel - friends list, player cards and profiles (own + others). Confirmed from the
        // client's Player.log: HTTP 404 thrown inside the relationship-list callback, and this
        // endpoint 404'd on every refresh right after /api/relationships/v2/get. No club system
        // yet, so the list is empty. Deliberately NOT auth-gated (return [] even signed out, as
        // the reference does) so it can never 401/404 the screen.
        [HttpGet("club/mine/member")]
        public ContentResult MyMemberClubs()
        {
            // Signed-out -> [] (never error/404 - this loads with the social panel). Otherwise
            // the clubs the caller belongs to, as a bare array of club DTOs.
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            var clubs = (error != null || account == null)
                ? new List<ClubDB.Club>()
                : ClubDB.GetByMember(account.Id);
            return ClubJson(clubs.Select(ClubDtoObj).ToList());
        }

        // The client calls /clubs/subscription/mine/member (no auth). Empty array = no club
        // subscription memberships; the client chokes on null and errors on a 404 - same
        // screen-breaking class as club/mine/member above.
        // The 2025 client's "who am I subscribed to" call (it no longer uses my/subscriptions).
        // Answered [] until now, so the client believed it was subscribed to nobody - the button
        // never showed "Subscribed" even after a successful subscribe. The shape is the creator
        // auto-club object captured from live RecNet traffic (see the comment at the bottom of
        // this file): one club per creator the caller subscribes to, clubType 1, creatorAccountId
        // = the creator. Club ids are the same synthetic ones subscription/details hands out.
        // The caller's subscriptions, one row each: { AccountId, ClubId, SubscriberCount } - the
        // shape the client's decoder reads (reference: recovered from the client, AccountId int,
        // ClubId long, SubscriberCount int). It used to answer a lowercase club object per
        // creator, which that decoder cannot read. The client has been seen calling this WITHOUT
        // an auth header, so no/invalid token answers [] rather than 401 (it chokes on null).
        [HttpGet("subscription/mine/member")]
        public ContentResult MySubscriptionMemberships()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            var rows = new List<object>();
            if (error == null && account != null)
            {
                foreach (var creator in PlayerDB.GetSubscribedToIds(account.Id).Distinct())
                {
                    if (PlayerDB.GetAccountById(creator) == null) continue;
                    rows.Add(new { AccountId = creator, ClubId = CreatorClubId(creator), SubscriberCount = CountSubscribers(creator) });
                }
            }
            return new ContentResult { Content = JsonConvert.SerializeObject(rows), ContentType = "application/json", StatusCode = 200 };
        }

        // The caller's regular clubs whose noticeboard has posts they haven't read.
        [HttpGet("announcements/v2/mine/unread")]
        public ContentResult UnreadAnnoucements()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var clubs = ClubDB.GetByMember(account.Id)
                .Where(c => ClubDB.HasUnreadAnnouncements(c.ClubId, account.Id))
                .Select(c => UnreadEntry(c, account.Id)).ToList();
            return ClubJson(clubs);
        }

        /// <summary>
        /// One entry of the two "unread announcements" feeds. The client flattens these with
        /// SelectMany(entry => entry.Announcements), so every entry MUST carry an Announcements
        /// list - they used to be bare club cards, which threw a NullReferenceException in
        /// NotificationModel the moment a player had an unread post, and the whole Notifications
        /// panel (every tab) stopped responding. Same shape as GET announcements/club/{id}'s value,
        /// with the club card's fields alongside, holding only the posts newer than what the
        /// player has read. Nothing is marked read here - opening the noticeboard does that.
        /// </summary>
        private static Newtonsoft.Json.Linq.JObject UnreadEntry(ClubDB.Club c, ulong viewer)
        {
            var entry = Newtonsoft.Json.Linq.JObject.FromObject(ClubDtoObj(c));
            int lastRead = ClubDB.LastReadAnnouncementId(c.ClubId, viewer);
            var all = ClubDB.GetAnnouncements(c.ClubId);
            int membership = ClubDB.MyMembership(c.ClubId, viewer);
            entry["Announcements"] = Newtonsoft.Json.Linq.JArray.FromObject(
                all.Where(a => a.AnnouncementId > lastRead).Select(a => AnnouncementObj(a, viewer, membership)).ToList());
            entry["LastAnnouncementId"] = all.Count > 0 ? all[0].AnnouncementId : null;
            entry["LastReadAnnouncementId"] = lastRead;
            return entry;
        }

        // The creators the caller subscribes to whose noticeboard has unread posts - the same
        // club shape /subscription/mine/member hands out.
        [HttpGet("announcements/v2/subscription/mine/unread")]
        public ContentResult UnreadAnnoucements2()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var clubs = new List<object>();
            foreach (var creator in PlayerDB.GetSubscribedToIds(account.Id).Distinct())
            {
                int clubId = (int)CreatorClubId(creator);
                if (!ClubDB.HasUnreadAnnouncements(clubId, account.Id)) continue;
                var club = ClubDB.CreatorClub(creator);
                if (club != null) clubs.Add(UnreadEntry(club, account.Id));
            }
            return ClubJson(clubs);
        }


        // The profile panel asks for /api/subscriptions/v1/subscriberCount/{id}, which
        // had no route at all and 404'd. Same number the clubs route below serves, just
        // at the path the client actually calls (absolute route - not under /clubs).
        [HttpGet("/api/subscriptions/v1/subscriberCount/{PlayerId}")]
        public ContentResult SubscriberCountApi(ulong PlayerId) => SubscriberCount(PlayerId);

        // Every account has a creator club on the real RecNet (research/RecNet API.postman:
        // /subscription/details/:id -> {clubId: <nonzero>, accountId, subscriberCount}). The
        // client enables its Subscribe button off that clubId; answering 0 (as this did, and as
        // the reference still does) reads as "this player has no club" and the button never
        // fires. The club id is synthetic and stable - derived from the account id - since no
        // club record is needed for anything but this handshake.
        public static long CreatorClubId(ulong accountId) => 5_000_000L + (long)accountId;

        // The real count, from the subscription lists themselves rather than the cached
        // sub_count (which drifted whenever a subscribe raced an unsubscribe).
        private static int CountSubscribers(ulong accountId) => PlayerDB.GetSubscriberIds(accountId).Count;

        [HttpGet("subscription/subscriberCount/{PlayerId}")]
        public ContentResult SubscriberCount(ulong PlayerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult()
            {
                Content = CountSubscribers(PlayerId).ToString(),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // Postman: GET /subscription/my/subscribers -> bare array of account ids.
        [HttpGet("subscription/my/subscribers")]
        public ContentResult MySubscribers()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(PlayerDB.GetSubscriberIds(account.Id)),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpPost("subscription/{PlayerId}")]
        public async Task<ContentResult> SubscribeToPlayer(ulong PlayerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            // Refused like the reference: yourself, or an id that is no account (404).
            if (PlayerId == account.Id)
                return new ContentResult { Content = JsonConvert.SerializeObject(new { value = (object?)null, success = false, errorId = (string?)null, error = "You cannot subscribe to yourself." }), ContentType = "application/json", StatusCode = 400 };
            if (PlayerDB.GetAccountById(PlayerId) == null)
                return new ContentResult { Content = "", ContentType = "application/json", StatusCode = 404 };
            await Task.Run(() => PlayerDB.AddSubscription(account.Id, PlayerId));
            Console.WriteLine($"[subscription] {account.Id} subscribed to {PlayerId}");
            // Website tabs of both parties refresh (subscriber count / Subscribed state).
            SiteNotifications.Notify(account.Id);
            SiteNotifications.Notify(PlayerId);
            try
            {
                var selfUpdate = JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                {
                    Id = "CreatorClubSubscriptionUpdate",
                    Msg = new { creatorAccountId = PlayerId, clubId = CreatorClubId(PlayerId), membershipType = 10 }
                });
                var notifySelfTask = Notifications.SendToPlayer(account.Id, selfUpdate);

                await Task.WhenAll(notifySelfTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send notification: {ex.Message}");
            }
            return new ContentResult()
            {
                // Postman: {value: <clubId>, success, errorId, error}
                Content = JsonConvert.SerializeObject(new
                {
                    value = CreatorClubId(PlayerId),
                    success = true,
                    errorId = (string?)null,
                    error = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpDelete("subscription/{PlayerId}")]
        public async Task<ContentResult> UnsubscribeToPlayer(ulong PlayerId)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            await Task.Run(() => PlayerDB.RemoveSubscription(account.Id, PlayerId));
            Console.WriteLine($"[subscription] {account.Id} unsubscribed from {PlayerId}");
            SiteNotifications.Notify(account.Id);
            SiteNotifications.Notify(PlayerId);
            try
            {
                var selfUpdate = JsonConvert.SerializeObject(new WebsocketEvents.Reponse
                {
                    Id = "CreatorClubSubscriptionUpdate",
                    Msg = new { creatorAccountId = PlayerId, clubId = CreatorClubId(PlayerId), membershipType = 0 }
                });
                var notifySelfTask = Notifications.SendToPlayer(account.Id, selfUpdate);

                await Task.WhenAll(notifySelfTask);
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
                    errorId = (string?)null,
                    error = (string?)null
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [HttpGet("subscription/my/subscriptions")]
        public ContentResult MySubscriptions()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;
            var subs = PlayerDB.GetSubscribedToIds(account.Id);
            return new ContentResult()
            {
                Content = JsonConvert.SerializeObject(subs),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // The profile panel fetches /clubs/subscription/details/{accountId} - for the caller's
        // OWN profile too. It was only documented as the comment below and 404'd, which broke
        // the OWN profile specifically (other players' profiles rendered, your own didn't).
        // RecFlare returns a zeroed object; there's no subscription-club system here, so clubId
        // 0. Deliberately NOT auth-gated - a 404/401 here blanks the panel.
        // https://clubs.rec.net/subscription/details/1503269750 returns { clubId, accountId, subscriberCount }
        [HttpGet("subscription/details/{accountId}")]
        public ContentResult SubscriptionDetails(ulong accountId) => new ContentResult
        {
            Content = JsonConvert.SerializeObject(new
            {
                clubId = CreatorClubId(accountId),
                accountId = accountId,
                subscriberCount = CountSubscribers(accountId)
            }),
            ContentType = "application/json",
            StatusCode = 200
        };

        // https://clubs.rec.net/club/2065378865047881941 return
        /*
        {
            "clubId": 2065378865047881941,
            "name": "33c7nhjv7txs1dsitn4v92pb5",
            "category": "CreatorAutoTag",
            "mainImageName": "DefaultClubImage2k.jpg",
            "description": null,
            "creatorAccountId": 1503269750,
            "state": 0,
            "memberCount": 0,
            "isRRO": false,
            "allowJuniors": true,
            "minLevel": 0,
            "visibility": 1,
            "joinability": 0,
            "clubhouseRoomId": null,
            "clubType": 1,
            "createdAt": "2025-08-03T13:18:44.3295131Z",
            "clubChatEnabled": false
        }
        */
    }
}
