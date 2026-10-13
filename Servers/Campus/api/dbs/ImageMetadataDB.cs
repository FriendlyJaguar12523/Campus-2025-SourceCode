using LiteDB;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

public static class ImageMetadataDB
{
    private static readonly string DbPath = Path.Combine("Data", "DBs", "Images.db");

    public class FullSavedImage
    {
        public int Id { get; set; }
        public ImageAccessibility Accessibility { get; set; }
        public bool AccessibilityLocked { get; set; } = false;
        public int CheerCount { get; set; } = 0;
        public int CommentCount { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? Description { get; set; }
        public string ImageName { get; set; }
        public ulong PlayerEventId { get; set; }
        public ulong PlayerId { get; set; } 
        public int RoomId { get; set; }
        //public string Description { get; set; } = "";
        public SavedImageTypeEnum Type { get; set; }
        public List<ulong> TaggedPlayerIds { get; set; } = new();
        // Website-only extra state. The game reads Accessibility (Private/Public) and never sees
        // this - a friends-only photo is stored Accessibility=Private (safe in-game), FriendsOnly=true,
        // and the WEBSITE shows it to the owner's friends.
        public bool FriendsOnly { get; set; } = false;
    }

    // NOTE: the client's v6 record keys the id as SavedImageId and the type as SavedImageType
    // (wire-shapes: api-images-v6). `Id`/`Type` are dropped silently by Utf8Json, which left every
    // v6 lookup with SavedImageId 0.
    public class ImageV6
    {
        public long SavedImageId { get; set; }
        public SavedImageTypeEnum SavedImageType { get; set; }
        public ulong ClubId { get; set; }

        public ImageAccessibility Accessibility { get; set; }
        public bool AccessibilityLocked { get; set; } = false;
        public int CheerCount { get; set; } = 0;
        public int CommentCount { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? Description { get; set; }
        public int Id { get; set; }
        public string ImageName { get; set; }
        public ulong PlayerEventId { get; set; }
        public ulong PlayerId { get; set; } 
        public int RoomId { get; set; }
        public List<ulong> TaggedPlayerIds { get; set; } = new();
        public SavedImageTypeEnum Type { get; set; }
    }

    public class ImagesPlayer
    {
        public ImageAccessibility Accessibility { get; set; }
        public bool AccessibilityLocked { get; set; } = false;
        public int CheerCount { get; set; } = 0;
        public int CommentCount { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? Description { get; set; }
        public string ImageName { get; set; }
        public ulong PlayerEventId { get; set; }
        public ulong PlayerId { get; set; } 
        public int RoomId { get; set; }
        public int SavedImageId { get; set; }
        public SavedImageTypeEnum SavedImageType { get; set; }
        public List<ulong> TaggedPlayerIds { get; set; } = new();
        public bool FriendsOnly { get; set; } = false;
    }

    public class ImageMetadata
    {
        public List<ulong> playerIds { get; set; } = new();
        public SavedImageTypeEnum savedImageType { get; set; }
        public int roomId { get; set; }
        public ImageAccessibility accessibility { get; set; }
        public string? description { get; set; }
    }

    public enum SavedImageTypeEnum
    {
        None, // 0
        ShareCamera, // 1
        OutfitThumbnail, // 2
        RoomThumbnail, // 3
        ProfileThumbnail, // 4
        InventionThumbnail, // 5
        PlayerEventThumbnail, // 6
        RoomLoadScreen // 7
    }

    public enum ImageAccessibility
    {
        Private = 0,
        Public = 1,
    }

    public class Cheer
    {
        public ObjectId Id { get; set; }
        public int SavedImageId { get; set; }
        public ulong PlayerId { get; set; }
    }

    public class CheerRequest
    {
        public int SavedImageId { get; set; }
        public bool Cheer { get; set; }
    }

    /// <summary>A website comment on a photo. CommentCount on the image is kept in step.</summary>
    public class ImageComment
    {
        public int Id { get; set; }
        public int SavedImageId { get; set; }
        public ulong PlayerId { get; set; }
        public string Text { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public static FullSavedImage? GetByName(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : GetDb().GetCollection<FullSavedImage>("images").FindOne(x => x.ImageName == name);

    public static ImageComment? GetComment(int commentId) =>
        GetDb().GetCollection<ImageComment>("comments").FindById(commentId);

    public static List<ImageComment> GetComments(int savedImageId) =>
        GetDb().GetCollection<ImageComment>("comments").Find(x => x.SavedImageId == savedImageId).OrderBy(x => x.CreatedAt).ToList();

    public static ImageComment? AddComment(int savedImageId, ulong playerId, string text)
    {
        var db = GetDb();
        if (db.GetCollection<FullSavedImage>("images").FindById(savedImageId) == null) return null;
        var c = new ImageComment { SavedImageId = savedImageId, PlayerId = playerId, Text = text, CreatedAt = DateTime.UtcNow };
        db.GetCollection<ImageComment>("comments").Insert(c);
        RecountComments(savedImageId);
        return c;
    }

    /// <summary>Delete a comment: its author, or the photo's owner, may.</summary>
    public static bool DeleteComment(int commentId, ulong byPlayer)
    {
        var db = GetDb();
        var col = db.GetCollection<ImageComment>("comments");
        var c = col.FindById(commentId);
        if (c == null) return false;
        var img = db.GetCollection<FullSavedImage>("images").FindById(c.SavedImageId);
        if (c.PlayerId != byPlayer && (img == null || img.PlayerId != byPlayer)) return false;
        col.Delete(commentId);
        RecountComments(c.SavedImageId);
        return true;
    }

    private static void RecountComments(int savedImageId)
    {
        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");
        var img = col.FindById(savedImageId);
        if (img == null) return;
        img.CommentCount = db.GetCollection<ImageComment>("comments").Count(x => x.SavedImageId == savedImageId);
        col.Update(img);
    }

    // PERF: Keep a thread-safe singleton LiteDatabase instance to avoid reopening files constantly.
    private static readonly Lazy<LiteDatabase> _dbInstance = new(() => new LiteDatabase(DbPath));
    public static LiteDatabase GetDb() => _dbInstance.Value;

    public static FullSavedImage InsertImage(FullSavedImage image)
    {
        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");
        col.EnsureIndex(x => x.Id, true);

        // If the image doesn't already have an ID, assign the next sequential ID.
        if (image.Id == 0)
        {
            // Get the highest existing ID and increment by 1.
            // PERF: This uses descending order to get the latest ID efficiently.
            var last = col.Query()
                          .OrderByDescending(x => x.Id)
                          .Limit(1)
                          .FirstOrDefault();

            image.Id = (last?.Id ?? 0) + 1;
        }

        col.Insert(image);
        return image;
    }


    public static List<FullSavedImage> GetRoomImages(
     ulong playerId,
     int roomId,
     int sort = 0,
     int filter = 0,
     int take = 50,
     int skip = 0)
    {
        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");

        // Base query: only images in this room
        var query = col.Query()
                       .Where(x => x.RoomId == roomId);

        // Apply filter conditions
        switch (filter)
        {
            case 2: // Only show player's private images
                query = query.Where(x => x.PlayerId == playerId);
                break;

            case 3: // Only images that have been cheered
                query = query.Where(x => x.CheerCount > 0);
                break;

                // filter == 1 will be handled in sorting
        }

        // Apply sorting
        if (sort == 0 && filter == 1)
        {
            // Special case: newest first when sort=0 & filter=1
            query = query.OrderByDescending(x => x.CreatedAt);
        }
        else
        {
            query = sort switch
            {
                1 => query.OrderByDescending(x => x.CreatedAt),   // Newest first
                2 => query.OrderByDescending(x => x.CheerCount),  // Most cheered
                3 => query.OrderByDescending(x => x.CommentCount),// Most commented
                _ => query.OrderBy(x => x.CreatedAt),             // Default: oldest first
            };
        }

        // Apply pagination
        return query
               .Skip(skip)
               .Limit(take)
               .ToList();
    }


    public static List<FullSavedImage> GetGlobalImages()
    {
        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");

        return col.Query()
                  .Where(x => x.Accessibility == ImageAccessibility.Public)    
                  .OrderByDescending(x => x.CheerCount) 
                  .ToList();
    }


    public const int SlideshowLimit = 10;
    public const int SlideshowMaxLimit = 100;

    /// <summary>
    /// The global slideshow feed: the most recent PUBLIC ShareCamera photos across all rooms,
    /// newest first. Ported from RecFlare's getSlideshowImages. Public-only (Accessibility ==
    /// Public) because this endpoint is unauthenticated - it must never surface a private photo.
    /// The caller joins Username + RoomName on top of these rows.
    /// </summary>
    public static List<FullSavedImage> GetSlideshowImages(int limit)
    {
        if (limit <= 0) limit = SlideshowLimit;
        if (limit > SlideshowMaxLimit) limit = SlideshowMaxLimit;

        var db = GetDb();
        return db.GetCollection<FullSavedImage>("images")
                 .Query()
                 .Where(x => x.Accessibility == ImageAccessibility.Public
                          && x.Type == SavedImageTypeEnum.ShareCamera)
                 .OrderByDescending(x => x.Id)
                 .Limit(limit)
                 .ToList();
    }

    /// <summary>
    /// The website "Recent Photos" feed: the newest PUBLIC camera photos across all rooms.
    /// Like GetSlideshowImages but ALSO includes Type == None, because this server's camera
    /// photos are saved as None (only ShareCamera would match nothing - the home feed was empty).
    /// Still excludes thumbnails (Outfit/Room/Profile/Invention/PlayerEvent/RoomLoadScreen) so
    /// profile pictures and room thumbnails never appear as "photos". Public-only (unauthenticated).
    /// </summary>
    // Public camera photos taken in one room, newest first - the website room page. Filters
    // in the query (not after a Take) so a room full of thumbnails can't crowd real photos
    // out of the window; None is included because this server saves camera photos as None.
    public static List<FullSavedImage> GetRoomPublicPhotos(int roomId, int limit)
    {
        if (limit <= 0) limit = 24;
        var db = GetDb();
        return db.GetCollection<FullSavedImage>("images")
                 .Query()
                 .Where(x => x.RoomId == roomId
                          && x.Accessibility == ImageAccessibility.Public
                          && (x.Type == SavedImageTypeEnum.None || x.Type == SavedImageTypeEnum.ShareCamera))
                 .OrderByDescending(x => x.Id)
                 .Limit(limit)
                 .ToList();
    }

    public static List<FullSavedImage> GetRecentPublicPhotos(int limit)
    {
        if (limit <= 0) limit = SlideshowLimit;
        if (limit > SlideshowMaxLimit) limit = SlideshowMaxLimit;

        // Only the LAST 24 HOURS - "Recent Photos" should be genuinely recent, not a months-deep
        // archive. Cutoff computed here (not inline) so LiteDB compares against a fixed value.
        // CreatedAt is stored UTC (DateTime.UtcNow default), so compare in UTC.
        var cutoff = DateTime.UtcNow.AddHours(-24);

        var db = GetDb();
        return db.GetCollection<FullSavedImage>("images")
                 .Query()
                 .Where(x => x.Accessibility == ImageAccessibility.Public
                          && (x.Type == SavedImageTypeEnum.None || x.Type == SavedImageTypeEnum.ShareCamera)
                          && x.CreatedAt >= cutoff)
                 .OrderByDescending(x => x.Id)
                 .Limit(limit)
                 .ToList();
    }

    /// <summary>Every saved photo, newest first - for the admin gallery (no accessibility filter).</summary>
    public static List<FullSavedImage> GetAllImages()
    {
        var db = GetDb();
        return db.GetCollection<FullSavedImage>("images")
                 .Query()
                 .OrderByDescending(x => x.Id)
                 .ToList();
    }

    /// <summary>
    /// Deletes one photo: its metadata row, its cheers, and the image file on disk. Returns
    /// the deleted row (for logging), or null if there was no such photo.
    /// </summary>
    public static FullSavedImage? DeleteImage(int id)
    {
        var db = GetDb();
        var imageCol = db.GetCollection<FullSavedImage>("images");
        var img = imageCol.FindById(id);
        if (img == null) return null;

        imageCol.Delete(id);
        db.GetCollection<Cheer>("cheers").DeleteMany(c => c.SavedImageId == id);
        db.GetCollection<ImageComment>("comments").DeleteMany(c => c.SavedImageId == id);

        // Remove the file from wherever /img serves polaroids from. Best-effort: the metadata
        // is already gone, so a stray file just 404s and is invisible.
        if (!string.IsNullOrWhiteSpace(img.ImageName))
        {
            foreach (var dir in new[] { "polaroid_images", "player_images" })
            {
                try
                {
                    var path = Path.Combine(Environment.CurrentDirectory, "Data", "Images", dir, Path.GetFileName(img.ImageName));
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) { Console.WriteLine($"[Delete] could not remove file {img.ImageName}: {ex.Message}"); }
            }
        }

        Console.WriteLine($"[Delete] removed photo {id} '{img.ImageName}' taken by {img.PlayerId}");
        return img;
    }

    /// <summary>
    /// A player's photos. `sort`: 0 newest first (default), 1 most cheered (the client's
    /// "sort by cheers" toggle, same value the reference uses), 2 oldest first. Was unsorted -
    /// LiteDB's natural order is insertion order, i.e. oldest first, and the toggle did nothing.
    /// </summary>
    public static List<ImagesPlayer> GetPlayerImages(ulong playeridthatrequesting, ulong playerid, int sort = 0, int skip = 0, int take = 0)
    {
        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");

        var query = col.Query().Where(x => x.PlayerId == playerid);

        if (playeridthatrequesting != playerid)
        {
            query = query.Where(x => x.Accessibility == ImageAccessibility.Public);
        }

        IEnumerable<FullSavedImage> rows = query.ToEnumerable();
        rows = sort switch
        {
            1 => rows.OrderByDescending(x => x.CheerCount).ThenByDescending(x => x.Id),
            2 => rows.OrderBy(x => x.Id),
            _ => rows.OrderByDescending(x => x.Id),
        };
        if (skip > 0) rows = rows.Skip(skip);
        if (take > 0) rows = rows.Take(take);
        return rows.Select(MapToImagesPlayer).ToList();
    }

    /// <summary>
    /// A player's photo FEED - the images they took PLUS the ones they are tagged in, newest
    /// first. This is what the photo tab on a profile shows (RecFlare getPlayerFeed +
    /// /api/images/v3/feed/player). Public-only unless the requester is the player themselves.
    /// </summary>
    public static List<ImagesPlayer> GetPlayerFeed(ulong requester, ulong playerid, int skip = 0, int take = 100, bool viewerIsFriend = false, int sort = 0)
    {
        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");

        IEnumerable<FullSavedImage> feed = col.FindAll()
            .Where(x => x.PlayerId == playerid
                     || (x.TaggedPlayerIds != null && x.TaggedPlayerIds.Contains(playerid)));

        // Non-owner sees public photos, plus friends-only ones when they're a friend
        // (friends-only is stored Accessibility=Private + FriendsOnly=true).
        if (requester != playerid)
            feed = feed.Where(x => x.Accessibility == ImageAccessibility.Public
                                || (x.FriendsOnly && viewerIsFriend));

        feed = sort switch
        {
            1 => feed.OrderByDescending(x => x.CheerCount).ThenByDescending(x => x.Id),
            2 => feed.OrderBy(x => x.Id),
            _ => feed.OrderByDescending(x => x.Id),
        };
        return feed.Skip(skip < 0 ? 0 : skip)
                   .Take(take <= 0 ? 100 : take)
                   .Select(MapToImagesPlayer)
                   .ToList();
    }

    private static ImagesPlayer MapToImagesPlayer(FullSavedImage x)
    {
        return new ImagesPlayer
        {
            Accessibility = x.Accessibility,
            AccessibilityLocked = x.AccessibilityLocked,
            CheerCount = x.CheerCount,
            CommentCount = x.CommentCount,
            CreatedAt = x.CreatedAt,
            Description = x.Description,
            ImageName = x.ImageName,
            PlayerEventId = x.PlayerEventId,
            PlayerId = x.PlayerId,
            RoomId = x.RoomId,
            SavedImageId = x.Id,
            SavedImageType = x.Type,
            TaggedPlayerIds = x.TaggedPlayerIds ?? new(),
            FriendsOnly = x.FriendsOnly
        };
    }

    /// <summary>One image row by id (null if none). For the website detail/edit endpoints.</summary>
    public static FullSavedImage? GetById(int id)
        => GetDb().GetCollection<FullSavedImage>("images").FindById(id);

    /// <summary>Owner-only visibility change: set Accessibility (what the game reads) + FriendsOnly
    /// (website). Returns false if the photo doesn't exist.</summary>
    public static bool SetVisibility(int id, ImageAccessibility accessibility, bool friendsOnly)
    {
        var col = GetDb().GetCollection<FullSavedImage>("images");
        var img = col.FindById(id);
        if (img == null) return false;
        img.Accessibility = accessibility;
        img.FriendsOnly = friendsOnly;
        col.Update(img);
        return true;
    }

    /// <summary>Owner-only caption change.</summary>
    public static bool SetDescription(int id, string description)
    {
        var col = GetDb().GetCollection<FullSavedImage>("images");
        var img = col.FindById(id);
        if (img == null) return false;
        img.Description = description;
        col.Update(img);
        return true;
    }

    /// <summary>Remove one tagged player from a photo. Returns the updated tag list, or null if
    /// the photo doesn't exist.</summary>
    public static List<ulong>? RemoveTag(int id, ulong taggedPlayerId)
    {
        var col = GetDb().GetCollection<FullSavedImage>("images");
        var img = col.FindById(id);
        if (img == null) return null;
        img.TaggedPlayerIds ??= new();
        img.TaggedPlayerIds.RemoveAll(x => x == taggedPlayerId);
        col.Update(img);
        return img.TaggedPlayerIds;
    }

    public static void UpdateImageCheerCount(int savedImageId, int cheerCount)
    {
        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");
        var img = col.FindById(savedImageId);
        if (img != null)
        {
            img.CheerCount = cheerCount;
            col.Update(img);
        }
    }

    public static bool ToggleCheer(int savedImageId, ulong playerId, bool cheer)
    {
        var db = GetDb();
        var col = db.GetCollection<Cheer>("cheers");

        var existing = col.FindOne(x => x.SavedImageId == savedImageId && x.PlayerId == playerId);

        if (cheer && existing == null)
        {
            col.Insert(new Cheer { SavedImageId = savedImageId, PlayerId = playerId });
        }
        else if (!cheer && existing != null)
        {
            col.Delete(existing.Id);
        }

        return col.Exists(x => x.SavedImageId == savedImageId && x.PlayerId == playerId);
    }

    public static int GetCheerCount(int savedImageId)
    {
        var db = GetDb();
        return db.GetCollection<Cheer>("cheers").Count(x => x.SavedImageId == savedImageId);
    }

    public static bool HasUserCheered(int savedImageId, ulong playerId)
    {
        var db = GetDb();
        return db.GetCollection<Cheer>("cheers")
                 .Exists(x => x.SavedImageId == savedImageId && x.PlayerId == playerId);
    }

    private static readonly string OldDir = Path.Combine("Data", "images_metadata");

    /// <summary>A v6 record for an image that does not exist - every id 0, so the client's
    /// normalising hook is satisfied and nothing renders. Never answer [] here.</summary>
    public static ImageV6 EmptyImageV6(string name) => new ImageV6
    {
        SavedImageId = 0, ImageName = name ?? "", PlayerId = 0, RoomId = 0,
        PlayerEventId = 0, ClubId = 0, Description = null,
        Accessibility = ImageAccessibility.Public, AccessibilityLocked = false,
        SavedImageType = SavedImageTypeEnum.None, CreatedAt = DateTime.UtcNow,
        CheerCount = 0, CommentCount = 0
    };

    public static ImageV6? GetImageV6ByName(string name) => ToV6(
        GetDb().GetCollection<FullSavedImage>("images").FindOne(x => x.ImageName == name));

    public static ImageV6? GetImageV6ById(long savedImageId) => ToV6(
        GetDb().GetCollection<FullSavedImage>("images").FindOne(x => x.Id == (int)savedImageId));

    /// <summary>
    /// Records for api/images/v5/bulk in the client's image-record shape (the same 13-key
    /// record as v6 - wire-shapes api-images-v6). Chat photo messages ("<Image:789>") resolve
    /// their picture through this; the raw rows it used to return carry the id as `Id`, which
    /// the client drops, so it never matched the id it asked for, re-asked every few seconds
    /// and left the photo as the grey placeholder.
    /// </summary>
    public static List<ImageV6> GetImagesV6ByIds(List<int> ids) =>
        GetImagesByIds(ids).Select(r => ToV6(r)!).ToList();

    private static ImageV6? ToV6(FullSavedImage? r) => r == null ? null : new ImageV6
    {
        SavedImageId = r.Id,
        Id = r.Id,
        ImageName = r.ImageName,
        PlayerId = r.PlayerId,
        RoomId = r.RoomId,
        PlayerEventId = r.PlayerEventId,
        ClubId = 0,
        Description = r.Description,
        Accessibility = r.Accessibility,
        AccessibilityLocked = r.AccessibilityLocked,
        SavedImageType = r.Type,
        Type = r.Type,
        CreatedAt = r.CreatedAt,
        CheerCount = r.CheerCount,
        CommentCount = r.CommentCount,
        TaggedPlayerIds = r.TaggedPlayerIds ?? new()
    };

    public static ImageV6 GetImageByName(string name)
    {
        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");
        var record = col.FindOne(x => x.ImageName == name);

        if (record == null) return null;

        return new ImageV6
        {
            Accessibility = record.Accessibility,
            AccessibilityLocked = record.AccessibilityLocked,
            CheerCount = record.CheerCount,
            CommentCount = record.CommentCount,
            CreatedAt = record.CreatedAt,
            Description = record.Description,
            Id  = record.Id,
            ImageName  = record.ImageName,
            PlayerEventId  = record.PlayerEventId,
            PlayerId  = record.PlayerId,
            RoomId  = record.RoomId,
            TaggedPlayerIds  = record.TaggedPlayerIds,
            Type  = record.Type,
        };
    }

    public static void DeleteImagesFromPlayerId(ulong playerId)
    {
        var db = GetDb();
        var imageCol = db.GetCollection<FullSavedImage>("images");
        var cheerCol = db.GetCollection<Cheer>("cheers");

        var imagesToDelete = imageCol.Query().Where(x => x.PlayerId == playerId).ToList();

        if (imagesToDelete.Count == 0)
            return;

        foreach (var img in imagesToDelete)
        {
            imageCol.Delete(img.Id);
            cheerCol.DeleteMany(c => c.SavedImageId == img.Id);
        }

        Console.WriteLine($"[Delete] Removed {imagesToDelete.Count} images and their cheers for PlayerId {playerId}");
    }

    public static List<FullSavedImage> GetImagesByIds(List<int> ids)
    {
        if (ids == null || ids.Count == 0)
            return new List<FullSavedImage>();

        var db = GetDb();
        var col = db.GetCollection<FullSavedImage>("images");

        // Efficient LiteDB WHERE query using Contains()
        return col.Query()
                  .Where(x => ids.Contains(x.Id))
                  .ToList();
    }


    public static void ImportOldJson()
    {
        if (!Directory.Exists(OldDir))
        {
            Console.WriteLine($"[Import] Old directory not found: {OldDir}");
            return;
        }

        var db = GetDb();
        var imageCol = db.GetCollection<FullSavedImage>("images");
        var cheerCol = db.GetCollection<Cheer>("cheers");
        imageCol.EnsureIndex(x => x.Id, true);

        // PERF: Parallelize JSON import for multiple files.
        var files = Directory.GetFiles(OldDir, "*.json");
        var cheerFiles = files.Where(f => f.EndsWith("_cheers.json")).ToArray();
        var imageFiles = files.Except(cheerFiles).ToArray();

        Parallel.ForEach(imageFiles, file =>
        {
            string fileName = Path.GetFileName(file);
            try
            {
                string json = File.ReadAllText(file);
                var images = JsonConvert.DeserializeObject<List<FullSavedImage>>(json);
                if (images == null) return;

                foreach (var img in images)
                {
                    if (!imageCol.Exists(x => x.Id == img.Id))
                    {
                        imageCol.Insert(img);
                        Console.WriteLine($"[Import] Imported image {img.Id} from {fileName}");
                    }
                    else
                    {
                        Console.WriteLine($"[Import] Skipped duplicate image {img.Id}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Import] Failed to import {fileName}: {ex.Message}");
            }
        });

        Parallel.ForEach(cheerFiles, file =>
        {
            try
            {
                string fileName = Path.GetFileName(file);
                int savedImageId = int.Parse(fileName.Replace("_cheers.json", ""));

                string json = File.ReadAllText(file);
                var cheerList = JsonConvert.DeserializeObject<List<ulong>>(json);
                if (cheerList == null) return;

                foreach (var pid in cheerList)
                {
                    if (!cheerCol.Exists(x => x.SavedImageId == savedImageId && x.PlayerId == pid))
                    {
                        cheerCol.Insert(new Cheer { SavedImageId = savedImageId, PlayerId = pid });
                    }
                }

                Console.WriteLine($"[Import] Imported {cheerList.Count} cheers for image {savedImageId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Import] Failed to import cheers from {file}: {ex.Message}");
            }
        });
    }
}
