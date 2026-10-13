using LiteDB;

// Club storage for the in-game Clubs feature. Ported to match the RecFlare reference
// (apps/clubs): clubs + membership rows, the { error, success, value } create envelope,
// and the ClubDetails payload the client renders a club screen from.
//
// Global namespace (like ImageMetadataDB) so controllers reference ClubDB.* unqualified.
public static class ClubDB
{
    private static readonly string DbPath = Path.Combine("Data", "DBs", "Clubs.db");
    private static readonly Lazy<LiteDatabase> _db = new(() => new LiteDatabase(DbPath));
    private static LiteDatabase Db => _db.Value;

    // Membership tiers (ClubMembershipType): -1 banned, 0 none, 1 pending request,
    // 2 pending invite, 3 denied, 10 member, 20 moderator, 30 co-owner, 100 creator.
    public const int TierNone = 0, TierMember = 10, TierModerator = 20, TierCoOwner = 30, TierCreator = 100;

    public const string DefaultMainImage = "DefaultImgPurple";
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 512;

    public class Club
    {
        public int ClubId { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "Social";
        public int Visibility { get; set; } = 1;   // 0 Private, 1 Public
        public int Joinability { get; set; } = 0;   // 0 Open, 1 InviteOnly, 2 AskToJoin
        public bool AllowJuniors { get; set; } = true;
        public string MainImageName { get; set; } = DefaultMainImage;
        public int ClubType { get; set; } = 0;      // 0 regular (never 1 = subscription)
        public int? ClubhouseRoomId { get; set; } = null;
        public ulong CreatorAccountId { get; set; }
        public bool IsRRO { get; set; } = false;
        public int MinLevel { get; set; } = 0;
        public int State { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<string> CustomTags { get; set; } = new();
        public List<string> AdditionalImageNames { get; set; } = new();
    }

    public class ClubMember
    {
        public int ClubMemberId { get; set; }
        public int ClubId { get; set; }
        public ulong AccountId { get; set; }
        public int MembershipType { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A club noticeboard post (reference: ClubAnnouncement).</summary>
    public class ClubAnnouncement
    {
        public int AnnouncementId { get; set; }
        public int ClubId { get; set; }
        public ulong AccountId { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string ImageName { get; set; } = "";
        public string Meta { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ModifiedAt { get; set; }
    }

    /// <summary>The club a player spawns into (their "home club"); one row per account.</summary>
    public class HomeClub
    {
        public int Id { get; set; }
        public ulong AccountId { get; set; }
        public int ClubId { get; set; }
    }

    private static ILiteCollection<HomeClub> Homes()
    {
        var c = Db.GetCollection<HomeClub>("home_clubs");
        c.EnsureIndex(x => x.AccountId, true);
        return c;
    }
    public static int GetHomeClubId(ulong accountId) => Homes().FindOne(h => h.AccountId == accountId)?.ClubId ?? 0;
    public static void SetHomeClub(ulong accountId, int clubId)
    {
        var col = Homes();
        var h = col.FindOne(x => x.AccountId == accountId);
        if (h == null) col.Insert(new HomeClub { AccountId = accountId, ClubId = clubId });
        else { h.ClubId = clubId; col.Update(h); }
    }
    public static void ClearHomeClub(ulong accountId) => Homes().DeleteMany(x => x.AccountId == accountId);

    /// <summary>How far a player has read a club's noticeboard (the unread badge).</summary>
    public class AnnouncementRead
    {
        public int Id { get; set; }
        public int ClubId { get; set; }
        public ulong AccountId { get; set; }
        public int LastReadAnnouncementId { get; set; }
    }

    // ---- CREATOR (subscription) CLUBS ----
    // Subscribing to a player joins that player's auto-created "creator club" (ClubType 1). They
    // are not stored: the id is 5_000_000 + creator account id (ClubsController.CreatorClubId)
    // and the club is synthesised from the account + its subscriber list whenever it's asked
    // for, so /club/{id}, /details, /members and the noticeboard all work for it. Before this
    // every one of those 404'd (the "click a player's subscribers" screen was blank).
    public const long CreatorClubBase = 5_000_000L;
    public static bool IsCreatorClub(long clubId) => clubId >= CreatorClubBase;
    public static ulong CreatorOfClub(long clubId) => IsCreatorClub(clubId) ? (ulong)(clubId - CreatorClubBase) : 0;

    public static Club? CreatorClub(ulong creatorAccountId)
    {
        var acc = Campus_live_rewrite.api.dbs.PlayerDB.GetAccountById(creatorAccountId);
        if (acc == null) return null;
        var full = Campus_live_rewrite.api.dbs.PlayerDB.GetFullAccount(creatorAccountId);
        return new Club
        {
            ClubId = (int)(CreatorClubBase + (long)creatorAccountId),
            Name = acc.Username ?? creatorAccountId.ToString(),
            Description = "",
            Category = "CreatorAutoTag",
            Visibility = 1,
            Joinability = 0,
            AllowJuniors = true,
            MainImageName = "DefaultClubImage2k.jpg",
            ClubType = 1,
            ClubhouseRoomId = null,
            CreatorAccountId = creatorAccountId,
            IsRRO = false,
            MinLevel = 0,
            State = 0,
            CreatedAt = full?.player?.created_at ?? DateTime.UtcNow,
        };
    }

    private static ILiteCollection<Club> Clubs()
    {
        var c = Db.GetCollection<Club>("clubs");
        c.EnsureIndex(x => x.ClubId, true);
        c.EnsureIndex(x => x.CreatorAccountId);
        return c;
    }
    private static ILiteCollection<ClubMember> Members()
    {
        var c = Db.GetCollection<ClubMember>("members");
        c.EnsureIndex(x => x.ClubId);
        c.EnsureIndex(x => x.AccountId);
        return c;
    }

    private static int NextId<T>(ILiteCollection<T> col, System.Linq.Expressions.Expression<System.Func<T, int>> key)
    {
        var last = col.Query().OrderByDescending(key).Limit(1).FirstOrDefault();
        return last == null ? 1 : (key.Compile()(last) + 1);
    }

    public static int CountByCreator(ulong accountId) =>
        Clubs().Count(x => x.CreatorAccountId == accountId);

    /// <summary>Admin panel: every stored club, newest first, and how many there are.</summary>
    public static int CountAll() => Clubs().Count();
    public static List<Club> All(int take = 500) =>
        Clubs().FindAll().OrderByDescending(c => c.CreatedAt).Take(take).ToList();

    public static Club? GetById(int clubId) =>
        IsCreatorClub(clubId) ? CreatorClub(CreatorOfClub(clubId)) : Clubs().FindById(clubId);

    public static List<Club> GetByCreator(ulong accountId) =>
        Clubs().Query().Where(x => x.CreatorAccountId == accountId && x.ClubType == 0)
               .OrderByDescending(x => x.ClubId).ToList();

    // Clubs the account belongs to (member tier or above), excluding subscription clubs.
    public static List<Club> GetByMember(ulong accountId)
    {
        var ids = Members().Query()
            .Where(m => m.AccountId == accountId && m.MembershipType >= TierMember)
            .ToList().Select(m => m.ClubId).Distinct().ToList();
        if (ids.Count == 0) return new();
        return Clubs().Query().Where(x => ids.Contains(x.ClubId) && x.ClubType == 0)
                      .OrderByDescending(x => x.ClubId).ToList();
    }

    public static int MemberCount(int clubId) =>
        IsCreatorClub(clubId)
            ? Campus_live_rewrite.api.dbs.PlayerDB.GetSubscriberIds(CreatorOfClub(clubId)).Count
            : Members().Count(m => m.ClubId == clubId && m.MembershipType >= TierMember);

    public static List<ClubMember> GetMembers(int clubId, int? membershipType, string? sortBy)
    {
        List<ClubMember> list;
        if (IsCreatorClub(clubId))
        {
            // The creator owns their club; every subscriber is a plain member.
            ulong creator = CreatorOfClub(clubId);
            list = new List<ClubMember> { new() { ClubMemberId = 0, ClubId = clubId, AccountId = creator, MembershipType = TierCreator } };
            int n = 1;
            foreach (var sub in Campus_live_rewrite.api.dbs.PlayerDB.GetSubscriberIds(creator))
                list.Add(new ClubMember { ClubMemberId = n++, ClubId = clubId, AccountId = sub, MembershipType = TierMember });
        }
        else list = Members().Query().Where(m => m.ClubId == clubId).ToList();
        if (membershipType.HasValue) list = list.Where(m => m.MembershipType == membershipType.Value).ToList();
        list = sortBy switch
        {
            "1" => list.OrderBy(m => m.AccountId).ToList(),
            "2" => list.OrderBy(m => m.CreatedAt).ToList(),
            _   => list.OrderByDescending(m => m.MembershipType).ThenBy(m => m.CreatedAt).ToList(),
        };
        return list;
    }

    // The caller's tier in a club (0 = none / signed out).
    public static int MyMembership(int clubId, ulong accountId)
    {
        if (accountId == 0) return TierNone;
        if (IsCreatorClub(clubId))
        {
            ulong creator = CreatorOfClub(clubId);
            if (creator == accountId) return TierCreator;
            return Campus_live_rewrite.api.dbs.PlayerDB.GetSubscribedToIds(accountId).Contains(creator) ? TierMember : TierNone;
        }
        var m = Members().FindOne(x => x.ClubId == clubId && x.AccountId == accountId);
        return m?.MembershipType ?? TierNone;
    }

    // Create a club and seed the creator's membership row (tier 100). Returns the new club.
    public static Club CreateClub(ulong creator, string name, string description, string category,
                                  int visibility, int joinability, bool allowJuniors, int minLevel)
    {
        var clubs = Clubs();
        var club = new Club
        {
            ClubId = NextId(clubs, x => x.ClubId),
            Name = name,
            Description = description ?? "",
            Category = string.IsNullOrWhiteSpace(category) ? "Social" : category,
            Visibility = visibility,
            Joinability = joinability,
            AllowJuniors = allowJuniors,
            MainImageName = DefaultMainImage,
            ClubType = 0,
            CreatorAccountId = creator,
            MinLevel = minLevel < 0 ? 0 : minLevel,
            CreatedAt = DateTime.UtcNow,
            CustomTags = new(),
        };
        clubs.Insert(club);

        var members = Members();
        members.Insert(new ClubMember
        {
            ClubMemberId = NextId(members, x => x.ClubMemberId),
            ClubId = club.ClubId,
            AccountId = creator,
            MembershipType = TierCreator,
            CreatedAt = DateTime.UtcNow,
        });
        return club;
    }

    // Set the club's main image (the club photo). Returns the updated club, or null.
    public static Club? SetMainImage(int clubId, string imageName)
    {
        var col = Clubs();
        var club = col.FindById(clubId);
        if (club == null) return null;
        club.MainImageName = imageName;
        col.Update(club);
        return club;
    }

    // Set one gallery image by position (packed: past-the-end appends). Returns updated club.
    public static Club? SetAdditionalImage(int clubId, int index, string imageName)
    {
        var col = Clubs();
        var club = col.FindById(clubId);
        if (club == null) return null;
        club.AdditionalImageNames ??= new();
        if (index < 0 || index >= club.AdditionalImageNames.Count) club.AdditionalImageNames.Add(imageName);
        else club.AdditionalImageNames[index] = imageName;
        col.Update(club);
        return club;
    }

    // Remove one gallery image and shift the rest up (no blank slot). Returns updated club.
    public static Club? RemoveAdditionalImage(int clubId, int index)
    {
        var col = Clubs();
        var club = col.FindById(clubId);
        if (club == null) return null;
        club.AdditionalImageNames ??= new();
        if (index >= 0 && index < club.AdditionalImageNames.Count) club.AdditionalImageNames.RemoveAt(index);
        col.Update(club);
        return club;
    }

    // Edit a club's details (co-owner+). Every param is optional: null = leave unchanged.
    // A non-null customTags REPLACES the tag set (capped at 3). Returns the updated club, or null.
    public static Club? UpdateDetails(int clubId, string? name, string? description, string? category,
        int? visibility, int? joinability, bool? allowJuniors, int? minLevel, List<string>? customTags)
    {
        var col = Clubs();
        var club = col.FindById(clubId);
        if (club == null) return null;
        if (!string.IsNullOrWhiteSpace(name)) club.Name = name.Trim();
        if (description != null) club.Description = description;
        if (!string.IsNullOrWhiteSpace(category)) club.Category = category.Trim();
        if (visibility.HasValue) club.Visibility = visibility.Value;
        if (joinability.HasValue) club.Joinability = joinability.Value;
        if (allowJuniors.HasValue) club.AllowJuniors = allowJuniors.Value;
        if (minLevel.HasValue) club.MinLevel = minLevel.Value < 0 ? 0 : minLevel.Value;
        if (customTags != null) club.CustomTags = customTags.Take(3).ToList();
        col.Update(club);
        return club;
    }

    // ---- ANNOUNCEMENTS (the club noticeboard) ----
    private static ILiteCollection<ClubAnnouncement> Announcements()
    {
        var c = Db.GetCollection<ClubAnnouncement>("announcements");
        c.EnsureIndex(x => x.AnnouncementId, true);
        c.EnsureIndex(x => x.ClubId);
        return c;
    }
    private static ILiteCollection<AnnouncementRead> Reads()
    {
        var c = Db.GetCollection<AnnouncementRead>("announcement_reads");
        c.EnsureIndex(x => x.ClubId);
        c.EnsureIndex(x => x.AccountId);
        return c;
    }

    /// <summary>A club's noticeboard, newest first.</summary>
    public static List<ClubAnnouncement> GetAnnouncements(int clubId) =>
        Announcements().Query().Where(a => a.ClubId == clubId).ToList()
                       .OrderByDescending(a => a.AnnouncementId).ToList();

    public static int LatestAnnouncementId(int clubId) =>
        Announcements().Query().Where(a => a.ClubId == clubId).ToList()
                       .Select(a => a.AnnouncementId).DefaultIfEmpty(0).Max();

    public static ClubAnnouncement PostAnnouncement(int clubId, ulong accountId, string? title, string? body, string? imageName, string? meta)
    {
        var col = Announcements();
        var a = new ClubAnnouncement
        {
            AnnouncementId = NextId(col, x => x.AnnouncementId),
            ClubId = clubId,
            AccountId = accountId,
            Title = (title ?? "").Trim(),
            Body = (body ?? "").Trim(),
            ImageName = (imageName ?? "").Trim(),
            Meta = meta ?? "",
        };
        col.Insert(a);
        // The poster has obviously read their own post.
        MarkAnnouncementsRead(clubId, accountId, a.AnnouncementId);
        return a;
    }

    public static ClubAnnouncement? EditAnnouncement(int announcementId, string? title, string? body, string? imageName, string? meta)
    {
        var col = Announcements();
        var a = col.FindOne(x => x.AnnouncementId == announcementId);
        if (a == null) return null;
        if (title != null) a.Title = title.Trim();
        if (body != null) a.Body = body;
        if (imageName != null) a.ImageName = imageName.Trim();
        if (meta != null) a.Meta = meta;
        a.ModifiedAt = DateTime.UtcNow;
        col.Update(a);
        return a;
    }

    public static ClubAnnouncement? DeleteAnnouncement(int announcementId)
    {
        var col = Announcements();
        var a = col.FindOne(x => x.AnnouncementId == announcementId);
        if (a != null) col.DeleteMany(x => x.AnnouncementId == announcementId);
        return a;
    }

    public static int LastReadAnnouncementId(int clubId, ulong accountId) =>
        Reads().FindOne(r => r.ClubId == clubId && r.AccountId == accountId)?.LastReadAnnouncementId ?? 0;

    public static void MarkAnnouncementsRead(int clubId, ulong accountId, int upToAnnouncementId)
    {
        if (accountId == 0) return;
        var col = Reads();
        var r = col.FindOne(x => x.ClubId == clubId && x.AccountId == accountId);
        if (r == null) col.Insert(new AnnouncementRead { ClubId = clubId, AccountId = accountId, LastReadAnnouncementId = upToAnnouncementId });
        else if (upToAnnouncementId > r.LastReadAnnouncementId) { r.LastReadAnnouncementId = upToAnnouncementId; col.Update(r); }
    }

    public static bool HasUnreadAnnouncements(int clubId, ulong accountId) =>
        LatestAnnouncementId(clubId) > LastReadAnnouncementId(clubId, accountId);

    public static void Checkpoint()
    {
        try { Db.Checkpoint(); } catch { /* best-effort */ }
    }

    // ---- browse ----------------------------------------------------------------------------
    public const int TierBanned = -1, TierPendingRequest = 1, TierPendingInvite = 2, TierDenied = 3;

    /// <summary>
    /// The Clubs browse page (reference searchClubs): public, non-subscription clubs, optionally
    /// one category (exact, case-insensitive) and a name/description substring; sort 1 = newest,
    /// 2 = by name, anything else = most members first. Returns the page and the full match count.
    /// </summary>
    public static (List<Club> page, int total) Search(string? category, string? query, string? sort, int count)
    {
        var all = Clubs().Query().Where(x => x.Visibility == 1 && x.ClubType == 0).ToList();
        string wanted = (category ?? "").Trim();
        string term = (query ?? "").Trim();
        var matched = all.Where(c =>
            (wanted.Length == 0 || string.Equals(c.Category, wanted, StringComparison.OrdinalIgnoreCase)) &&
            (term.Length == 0
                || (c.Name ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                || (c.Description ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        var counts = matched.ToDictionary(c => c.ClubId, c => MemberCount(c.ClubId));
        matched = sort switch
        {
            "1" => matched.OrderByDescending(c => c.CreatedAt).ToList(),
            "2" => matched.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _   => matched.OrderByDescending(c => counts[c.ClubId]).ThenByDescending(c => c.CreatedAt).ToList(),
        };
        return (matched.Take(count).ToList(), matched.Count);
    }

    /// <summary>Clubs an account created that anyone may see (public ones), for their profile.</summary>
    public static List<Club> PublicByCreator(ulong accountId) =>
        Clubs().Query().Where(x => x.CreatorAccountId == accountId && x.ClubType == 0 && x.Visibility == 1)
               .OrderByDescending(x => x.ClubId).ToList();

    // ---- membership writes -----------------------------------------------------------------
    private static void Upsert(int clubId, ulong accountId, int tier)
    {
        var members = Members();
        var m = members.FindOne(x => x.ClubId == clubId && x.AccountId == accountId);
        if (m == null)
        {
            members.Insert(new ClubMember
            {
                ClubMemberId = NextId(members, x => x.ClubMemberId),
                ClubId = clubId, AccountId = accountId, MembershipType = tier, CreatedAt = DateTime.UtcNow,
            });
        }
        else { m.MembershipType = tier; members.Update(m); }
    }

    /// <summary>
    /// The caller asks to join (reference requesttojoin / join): an Open club takes them in as a
    /// Member, an AskToJoin club records a pending request, an InviteOnly club refuses. A pending
    /// invite is accepted by asking. Banned stays banned. Returns the resulting tier.
    /// </summary>
    public static int RequestJoin(int clubId, ulong accountId)
    {
        var club = Clubs().FindById(clubId);
        if (club == null) return TierNone;
        int have = MyMembership(clubId, accountId);
        if (have == TierBanned || have >= TierMember) return have;
        if (have == TierPendingInvite) { Upsert(clubId, accountId, TierMember); return TierMember; }
        if (club.Joinability == 0) { Upsert(clubId, accountId, TierMember); return TierMember; }
        if (club.Joinability == 2) { Upsert(clubId, accountId, TierPendingRequest); return TierPendingRequest; }
        return have;   // InviteOnly: nothing changes
    }

    /// <summary>Leave (or withdraw a request). The creator can't; a ban stays. Returns the new tier.</summary>
    public static int Leave(int clubId, ulong accountId)
    {
        int have = MyMembership(clubId, accountId);
        if (have == TierCreator || have == TierBanned) return have;
        Members().DeleteMany(x => x.ClubId == clubId && x.AccountId == accountId);
        return TierNone;
    }

    /// <summary>A co-owner adds / promotes / demotes someone (reference members/invite). Never the creator, never to Creator.</summary>
    public static bool SetTier(int clubId, ulong accountId, int tier)
    {
        var club = Clubs().FindById(clubId);
        if (club == null || club.CreatorAccountId == accountId) return false;
        if (tier != TierMember && tier != TierModerator && tier != TierCoOwner && tier != TierBanned
            && tier != TierPendingInvite && tier != TierNone) return false;
        if (tier == TierNone) { Members().DeleteMany(x => x.ClubId == clubId && x.AccountId == accountId); return true; }
        Upsert(clubId, accountId, tier);
        return true;
    }

    public static Club? SetClubhouse(int clubId, int? roomId)
    {
        var clubs = Clubs();
        var c = clubs.FindById(clubId);
        if (c == null) return null;
        c.ClubhouseRoomId = roomId;
        clubs.Update(c);
        return c;
    }

    /// <summary>Delete a club with its memberships and announcements (creator only - the caller checks).</summary>
    public static bool Delete(int clubId)
    {
        var clubs = Clubs();
        if (clubs.FindById(clubId) == null) return false;
        Members().DeleteMany(x => x.ClubId == clubId);
        try { Db.GetCollection<ClubAnnouncement>("announcements").DeleteMany(x => x.ClubId == clubId); } catch { }
        clubs.Delete(clubId);
        return true;
    }
}
