using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Single source of truth for "can the client actually download this room blob?".
    ///
    /// Room JSON references blobs by FILENAME ONLY. If the client is handed a name it
    /// cannot download, the join aborts outright rather than degrading, with either
    /// "Error downloading room data" (room-level DataBlob) or "Error downloading
    /// subroom data" (SubRooms[].CurrentSave.DataBlob) - both "(Code 8715)".
    ///
    /// On the live server most referenced blobs cannot be served:
    ///   - 148 of 151 files in Data/CDN/RoomBlobs are ZERO BYTES (created, never written)
    ///   - NO .meta files exist at all, yet room-level DataBlob points at .meta names
    /// Those blobs are original rec.net CDN references we never held the content for;
    /// there is nothing to restore, so the only fix is to stop advertising them.
    ///
    /// Stripping is safe because null is a normal state for both fields - Rec Center
    /// ships with DataBlob = null and loads its default scene fine.
    ///
    /// IMPORTANT: only call this on GET/serialize paths. The PUT/POST endpoints
    /// (SetRoomName, SetRoomData, ...) load-modify-save the very same objects, so
    /// stripping there would PERSIST the null into LiteDB and destroy the reference
    /// permanently. Applied at serialization only, this is self-correcting: drop real
    /// files into Data/CDN/RoomBlobs and the blobs are served again with no code change.
    /// </summary>
    public static class RoomBlobFilter
    {
        /// <summary>
        /// Shipped stand-in for a room's metadata blob. Byte-identical to what the client
        /// uploads for a room with default economy settings.
        /// </summary>
        public const string DefaultRoomMetaBlob = "default_room.META";

        private static bool HasDefaultRoomMeta()
        {
            try
            {
                var fi = new FileInfo(Path.Combine(BlobDirectory, DefaultRoomMetaBlob));
                return fi.Exists && fi.Length > 0;
            }
            catch { return false; }
        }

        public static string BlobDirectory =>
            Path.Combine(Environment.CurrentDirectory, "Data", "CDN", "RoomBlobs");

        /// <summary>
        /// True when the blob can actually be served. Existence is NOT enough:
        /// File.Exists returns true for the zero-byte placeholders, which is what made
        /// an earlier existence-only guard a silent no-op. Require real content.
        /// </summary>
        public static bool BlobHasContent(string? blob)
        {
            if (string.IsNullOrWhiteSpace(blob)) return true;   // nothing referenced = nothing to fetch

            try
            {
                var fi = new FileInfo(Path.Combine(BlobDirectory, blob!));
                return fi.Exists && fi.Length > 0;
            }
            catch
            {
                return false;   // unreadable path = treat as unservable
            }
        }

        /// <summary>Nulls any blob reference on this room that the client could not download.</summary>
        public static RoomDBClasses.RoomRoot? Strip(RoomDBClasses.RoomRoot? room)
        {
            if (room == null) return room;

            // The room-level Accessibility the CLIENT reads must be the effective one (the Home
            // subroom's - what the owner set). An imported room carried Private at room level
            // with a Public Home, so it was served in a creator's list and the client hid it
            // ("@Importer's Mr_Edwins_Reclympics_4770 doesn't appear"). Startup syncs the DB;
            // this covers a room changed since.
            try
            {
                if (!room.IsRRO && room.SubRooms != null && room.SubRooms.Count > 0)
                {
                    var eff = Campus_live_rewrite.api.dbs.RoomDB.EffectiveRoomAccessibility(room);
                    if (room.Accessibility != eff) room.Accessibility = eff;
                }
            }
            catch { }

            // ROOM-LEVEL blob.
            //
            // Nulling this made rooms load, but the client then treats a room with no
            // room-level blob as incomplete: it hides it from Create -> My Rooms and
            // reports "This room has never been saved". Only room 84 - the one room that
            // had a real DataBlob - was listed, out of nine owned rooms.
            //
            // Substituting a valid default is strictly better than nulling. Every .META
            // the client uploads for a room with no custom economy config is byte-for-byte
            // IDENTICAL (verified: 4 distinct uploads, one md5 - b77fd492...), so
            // default_room.META is exactly what the client itself would have sent. Rooms
            // that never saved, and rooms pointing at a dead rec.net .meta, both get a
            // blob that actually downloads.
            //
            // A room with its own servable blob is left completely alone.
            if (!BlobHasContent(room.DataBlob) || string.IsNullOrWhiteSpace(room.DataBlob))
            {
                if (BlobHasContent(DefaultRoomMetaBlob) && HasDefaultRoomMeta())
                {
                    room.DataBlob = DefaultRoomMetaBlob;
                }
                else
                {
                    // No default on disk - fall back to the old behaviour so the room at
                    // least loads rather than 404ing on a blob that isn't there.
                    Console.WriteLine($"[room {room.RoomId}] room-level DataBlob missing and " +
                                      $"{DefaultRoomMetaBlob} is absent from Data/CDN/RoomBlobs - serving null");
                    room.DataBlob = null!;
                }
            }

            if (room.SubRooms == null) return room;

            foreach (var sub in room.SubRooms)
            {
                string? blob = sub?.CurrentSave?.DataBlob;
                if (string.IsNullOrWhiteSpace(blob)) continue;
                if (BlobHasContent(blob)) continue;

                // Once per blob per day: this runs on every room read and listing, and 106 missing
                // blobs were logging ~100,000 lines a day between them (room 48 alone: 66,843).
                if (WarnedToday(blob))
                    Console.WriteLine($"[room {room.RoomId}/sub {sub!.SubRoomId}] CurrentSave blob '{blob}' " +
                                      "missing or empty in Data/CDN/RoomBlobs - serving room without it so it loads " +
                                      "its default scene instead of failing with 'Error downloading subroom data' (logged once a day)");
                sub!.CurrentSave = null;
            }

            return room;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _warned = new();
        private static bool WarnedToday(string blob)
        {
            var now = DateTime.UtcNow;
            if (_warned.TryGetValue(blob, out var at) && now - at < TimeSpan.FromDays(1)) return false;
            _warned[blob] = now;
            return true;
        }

        /// <summary>
        /// List overload. Listings matter as much as single-room GETs: the client caches
        /// them and can join straight from a cached entry (rooms/visitedby/me is fetched
        /// moments before every join), so an unservable blob left in a listing resurfaces
        /// the same failure by another route.
        /// </summary>
        public static List<RoomDBClasses.RoomRoot>? Strip(List<RoomDBClasses.RoomRoot>? rooms)
        {
            if (rooms == null) return rooms;
            foreach (var r in rooms) Strip(r);
            return rooms;
        }
    }
}
