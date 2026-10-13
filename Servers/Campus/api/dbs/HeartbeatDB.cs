using System.Text.Json.Serialization;
using LiteDB;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.dbs
{
    public class HeartbeatDB
    {
        private static readonly LiteDatabase _db = new($"{Environment.CurrentDirectory}/Data/DBs/Heartbeats.db");

        // Merge this store's write-ahead log back into the data file.
        // See DbMaintenance for why this must be called explicitly.
        public static void Checkpoint() => _db.Checkpoint();
        private static readonly ILiteCollection<PlayerHeartbeatData> _col =
            _db.GetCollection<PlayerHeartbeatData>("players_heartbeat");

        public static void Setup()
        {
            _col.EnsureIndex(x => x.playerid, unique: true);

            // Rows written before the key change carry a sequential iD (1, 2, 3...)
            // rather than the player's id, so the next insert for a player whose id
            // matches an old sequential value collides on _id and throws - which is
            // the failure the key change exists to remove.
            //
            // Heartbeats are transient (GetPlayerHeartbeat falls back to
            // CreateDefaultHeartbeat when a player has no row), so dropping stale
            // rows costs nothing and is safer than leaving the collision armed.
            try
            {
                var stale = _col.FindAll().Where(x => x.iD != x.playerid).ToList();
                if (stale.Count > 0)
                {
                    foreach (var row in stale) _col.Delete(row.iD);
                    Console.WriteLine($"[HeartbeatDB] migrated: dropped {stale.Count} row(s) keyed by row-count instead of playerid");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeartbeatDB] key migration skipped: {ex.Message}");
            }

            // Correct EVERY stored appVersion to the current client build. The old hardcoded
            // "20250731.01" is NEWER than the 20250718.01 client, so a player whose row still
            // carried it showed to everyone as "ON NEWER VERSION" and couldn't be joined. The
            // per-heartbeat fix only touches a row when that player moves; this catches the
            // offline/stationary rows at startup so nobody is left stale.
            try
            {
                string want = Campus_live_rewrite.api.server.ConfigCampus.PresenceAppVersion;
                var wrong = _col.FindAll().Where(x => x.Heartbeat != null && x.Heartbeat.appVersion != want).ToList();
                foreach (var row in wrong) { row.Heartbeat.appVersion = want; _col.Update(row); }
                if (wrong.Count > 0)
                    Console.WriteLine($"[HeartbeatDB] appVersion migration: corrected {wrong.Count} row(s) to {want}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeartbeatDB] appVersion migration skipped: {ex.Message}");
            }
        }

        public class PlayerHeartbeatData
        {
            // Keyed by playerid. This used to be `(ulong)_col.LongCount() + 1` at every
            // insert, which is not a usable primary key: delete any row and the count
            // drops, so the next insert reuses an existing id and LiteDB throws
            // "Cannot insert duplicate key in unique index '_id'". Two concurrent
            // inserts read the same count and collide identically. A throw there aborts
            // the write, so the heartbeat row silently stops being updated.
            // playerid is already unique (see the index in Setup), so it IS the key.
            [BsonId]
            public ulong iD { get; set; }
            public ulong playerid { get; set; }
            public Heartbeat Heartbeat { get; set; }
        }

        private static Heartbeat CreateDefaultHeartbeat(ulong playerid, Platforms platform, DeviceClasses deviceClasses) =>
            new()
            {
                playerId = (long)playerid,
                appVersion = Campus_live_rewrite.api.server.ConfigCampus.PresenceAppVersion,
                // 0 = Unknown. Was -1, which is NOT a valid DeviceClasses value - the client's
                // strict presence parser threw on the out-of-range enum and dropped the WHOLE
                // presence array, so a friends/player list built against it (offline friends all
                // get this default) came back EMPTY. Same class of bug as the statusVisibility=4
                // fix below; the reference emits `deviceClass ?? 0`.
                deviceClass = 0,
                isOnline = false,
                platform = 0,
                roomInstance = null,
                statusVisibility = 0, // 0 = Everyone (matches the reference); was 4 (out of range)
                vrMovementMode = 1,   // reference default (`?? 1`); 0 was also fine but match it
            };

        // Where we last DELIBERATELY put each player, held in memory alongside the DB row.
        //
        // The heartbeat answer is authoritative to the client: answer "nowhere" and it
        // decides it was disconnected and walks itself home to its dorm. So a room
        // instance that goes missing by ACCIDENT - a row that never got written, a lost
        // LiteDB update, a restart, a concurrent write - must not be reported as "nowhere".
        //
        // Only UpdatePlayerHeartbeat writes this, so an entry existing means "we put them
        // there on purpose and nothing has deliberately moved them since". A real logout or
        // ban clears the entry (it goes through UpdatePlayerHeartbeat with a null instance),
        // so intentional "nowhere" still answers nowhere.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, RoomInstance> _placedIn = new();

        // When each player's presence last changed (online/offline or room). The hub re-sends a
        // recently changed presence to a viewer who re-subscribes - see Notifications.EchoPresence.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTime> _presenceChangedAt = new();

        public static bool PresenceChangedWithin(ulong playerid, TimeSpan window) =>
            _presenceChangedAt.TryGetValue(playerid, out var at) && DateTime.UtcNow - at < window;

        // Last instance id we answered per player, purely so a CHANGE can be logged. A
        // change the player did not ask for is the drop, and this is the line that names it.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> _lastAnswered = new();

        // ---- Short-term memory that survives a mass drop -------------------------------------
        //
        // An instance only "exists" while some ONLINE heartbeat row points at it. That is fine
        // until the Photon master client (the first player into the instance) leaves: every other
        // client in that Photon room runs its master-switch recovery - a full logout + re-login,
        // then "join the player I was with" - and the drop itself has just wiped all of their
        // rows. So each recovering player asks about a friend whose row is now empty, gets
        // "target offline / not in a room", and lands in their dorm. Seen live 2026-09-11 22:12:
        // 6 leaves ^RecCenter -> 11 and 64 both reset within 6 s -> all three in dorms.
        //
        // These two maps remember, for a few minutes, where each player was LAST placed and
        // which instances were live, independent of the rows. They are consulted only when the
        // live answer is empty, so nothing changes for a player who is simply somewhere else.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (RoomInstance inst, DateTime seen)> _recentInstances = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (RoomInstance inst, DateTime seen)> _lastPlacement = new();
        /// <summary>How long after a drop a player's last instance is still offered for regrouping.</summary>
        public static readonly TimeSpan RegroupWindow = TimeSpan.FromMinutes(2);
        /// <summary>How long an instance is answerable by id after its last occupant's row is cleared.</summary>
        public static readonly TimeSpan InstanceMemory = TimeSpan.FromMinutes(15);

        // The instance each player was in BEFORE their latest move, so "were these two just
        // together?" can be answered after one of them has already moved on (the party leader
        // has left the room by the time the member asks to follow).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (long instanceId, DateTime left)> _prevPlacement = new();

        private static void Remember(ulong playerid, RoomInstance inst)
        {
            if (inst == null || inst.roomInstanceId == 0) return;
            var now = DateTime.UtcNow;
            if (_lastPlacement.TryGetValue(playerid, out var last) && last.inst.roomInstanceId != inst.roomInstanceId)
                _prevPlacement[playerid] = (last.inst.roomInstanceId, now);
            _recentInstances[inst.roomInstanceId] = (inst, now);
            _lastPlacement[playerid] = (inst, now);
        }

        /// <summary>
        /// Were <paramref name="a"/> and <paramref name="b"/> standing in the same instance within
        /// the regroup window? True when a's current/last instance equals b's current OR the one b
        /// just left. This is what stands in for "same party" on a follow: the client keeps its
        /// party on the Photon side and never posts the member list, so being together a moment
        /// ago is the best evidence the server has.
        /// </summary>
        public static bool WereTogetherRecently(ulong a, ulong b)
        {
            var cutoff = DateTime.UtcNow - RegroupWindow;
            long aInst = _lastPlacement.TryGetValue(a, out var la) && la.seen >= cutoff ? la.inst.roomInstanceId : 0;
            if (aInst == 0) return false;
            if (_lastPlacement.TryGetValue(b, out var lb) && lb.inst.roomInstanceId == aInst) return true;
            if (_prevPlacement.TryGetValue(b, out var pb) && pb.instanceId == aInst && pb.left >= cutoff) return true;
            return false;
        }

        /// <summary>The instance this player was in within RegroupWindow, or null.</summary>
        public static RoomInstance? RecentInstanceFor(ulong playerid)
        {
            if (!_lastPlacement.TryGetValue(playerid, out var p)) return null;
            return DateTime.UtcNow - p.seen <= RegroupWindow ? p.inst : null;
        }

        /// <summary>The most recently live instance of a room (public or private) within InstanceMemory, or null.</summary>
        public static RoomInstance? RecentInstanceOfRoomAny(ulong roomId)
        {
            var cutoff = DateTime.UtcNow - InstanceMemory;
            return _recentInstances.Values
                .Where(v => v.seen >= cutoff && v.inst.roomId == (long)roomId && !string.IsNullOrEmpty(v.inst.photonRoomIdYeee))
                .OrderByDescending(v => v.seen)
                .Select(v => v.inst)
                .FirstOrDefault();
        }

        // ---- games in progress -------------------------------------------------------------
        // PUT roominstance/{id}/inprogress (the client flips it when a round starts / ends). Room
        // matchmaking never places a player into an instance with a game running - the reference's
        // getJoinableInstance filters is_in_progress = 0 - so they get a fresh instance instead of
        // landing mid-round (Dodgeball: spawned on top of the players, acting as if in the game).
        // Following a friend / an invite still lands in their instance, as in the reference.
        // Kept here as well as on the presence rows because the regroup memory holds its own
        // copies; expires so a client that crashed mid-round can't lock an instance for good.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> _inProgress = new();
        private static readonly TimeSpan InProgressExpiry = TimeSpan.FromMinutes(45);

        public static void SetInstanceInProgress(long roomInstanceId, bool inProgress)
        {
            if (inProgress) _inProgress[roomInstanceId] = DateTime.UtcNow;
            else _inProgress.TryRemove(roomInstanceId, out _);
        }

        // ---- broken instances ---------------------------------------------------------------
        // CampusNet reports joiners stuck on the loading screen (POST /photon/stuckload). Such an
        // instance keeps working for the players already in it, but nobody new is matchmade into
        // it for BrokenInstanceWindow - they get another instance instead of the same hang.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> _broken = new();
        public static readonly TimeSpan BrokenInstanceWindow = TimeSpan.FromMinutes(30);

        public static void MarkInstanceBroken(long roomInstanceId)
        {
            if (roomInstanceId != 0) _broken[roomInstanceId] = DateTime.UtcNow;
        }

        public static bool IsInstanceBroken(RoomInstance? inst)
        {
            if (inst == null || !_broken.TryGetValue(inst.roomInstanceId, out var at)) return false;
            if (DateTime.UtcNow - at < BrokenInstanceWindow) return true;
            _broken.TryRemove(inst.roomInstanceId, out _);
            return false;
        }

        /// <summary>The live (or recently live) instance behind a Photon room name, or null.</summary>
        public static RoomInstance? FindInstanceByPhotonName(string photonRoom)
        {
            if (string.IsNullOrEmpty(photonRoom)) return null;
            var live = _col.FindAll().Select(x => x.Heartbeat?.roomInstance)
                           .FirstOrDefault(r => r != null && r.photonRoomIdYeee == photonRoom);
            if (live != null) return live;
            return _recentInstances.Values.Select(v => v.inst).FirstOrDefault(r => r.photonRoomIdYeee == photonRoom);
        }

        public static bool IsInstanceInProgress(RoomInstance? inst)
        {
            if (inst == null) return false;
            if (_inProgress.TryGetValue(inst.roomInstanceId, out var since))
            {
                if (DateTime.UtcNow - since < InProgressExpiry) return true;
                _inProgress.TryRemove(inst.roomInstanceId, out _);
                return false;
            }
            return inst.isInProgress;
        }

        /// <summary>The most recently live PUBLIC instance of a room within RegroupWindow, or null.</summary>
        public static RoomInstance? RecentInstanceOfRoom(ulong roomId)
        {
            var cutoff = DateTime.UtcNow - RegroupWindow;
            return _recentInstances.Values
                .Where(v => v.seen >= cutoff && v.inst.roomId == (long)roomId && !v.inst.isPrivate
                         && !IsInstanceInProgress(v.inst)
                         && !string.IsNullOrEmpty(v.inst.photonRoomIdYeee))
                .OrderByDescending(v => v.seen)
                .Select(v => v.inst)
                .FirstOrDefault();
        }

        /// <summary>
        /// Whether this player has a stored heartbeat at all. Distinct from "has no room
        /// instance" - a player sitting in a menu has a row with a null instance.
        /// </summary>
        public static bool HasHeartbeatRow(ulong playerid) =>
            _col.FindOne(x => x.playerid == playerid) != null;

        /// <summary>
        /// Read a player's presence WITHOUT the repair-writes/logging that GetPlayerHeartbeat does -
        /// for read-only surfaces like the admin players list that must not mutate state or spam the
        /// console when they enumerate everyone. Returns whether they're online and their current
        /// room instance (null if nowhere), honoring the in-memory placement fallback.
        /// </summary>
        /// <summary>
        /// The room a player is placed in, for the Tachyon voice lookup. Unlike PeekPresence this does
        /// NOT require a live notification WebSocket: Tachyon only asks while the player is connecting
        /// voice, which already proves they're in the game, and the hub socket is often mid-reconnect
        /// at exactly that moment - answering 0 then isolated the player (no voice) for the whole
        /// visit (53 times in a week). A logout/ban still clears the placement, so "nowhere" stays
        /// nowhere.
        /// </summary>
        public static RoomInstance? PlacementForVoice(ulong playerid)
        {
            if (_placedIn.TryGetValue(playerid, out var placed)) return placed;
            return _col.FindOne(x => x.playerid == playerid)?.Heartbeat?.roomInstance;
        }

        public static (bool online, RoomInstance? room) PeekPresence(ulong playerid)
        {
            // Online = a LIVE notification WebSocket, not the stored isOnline flag. The flag goes
            // stale on a hard disconnect (shows "online" forever) and is unaffected by - but also
            // doesn't help with - "appear offline". The WebSocket is ground truth: it's opened on
            // connect, removed on disconnect, and is independent of the player's status visibility,
            // so the admin sees who is ACTUALLY connected even if they're appearing offline to
            // everyone else. If they're not connected, they're offline here regardless of the flag.
            bool online = CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(playerid);

            var hb = _col.FindOne(x => x.playerid == playerid)?.Heartbeat;
            RoomInstance? room = hb?.roomInstance;
            if (room == null && _placedIn.TryGetValue(playerid, out var placed))
                room = placed;

            // Only surface a room while actually online, so a stale room from a dropped session
            // doesn't read as "still in that room".
            return online ? (true, room) : (false, null);
        }

        /// <summary>
        /// Presence of ANOTHER player as it should appear in someone else's friends/player list.
        /// Unlike GetPlayerHeartbeat (which is the player's OWN authoritative heartbeat and
        /// deliberately refuses to answer "nowhere"), this is what OTHERS see, so it is gated on a
        /// LIVE notification WebSocket - the same ground truth PeekPresence uses for the admin panel:
        ///   * not actually connected  -> isOnline=false, roomInstance=null (fixes "shows online in
        ///     a room they aren't in" after a crash/close, where the stored row keeps isOnline=true
        ///     and the last room forever, and _placedIn still holds the old instance);
        ///   * connected but "appear offline" (statusVisibility 3 = No One) -> also reported offline.
        /// Side-effect-free: never writes the DB or restores _placedIn, so enumerating a whole
        /// friends list doesn't churn repair-writes or spam the console. Enum fields stay valid
        /// (appVersion/deviceClass/etc.) so the client's strict presence parser still accepts it.
        /// </summary>
        public static Heartbeat GetPresenceForList(ulong playerid, bool viewerIsSelf, bool viewerIsFriend, bool viewerIsFavorite, ulong viewerId = 0)
        {
            // An invite is consent to be seen: while a live invite from this player to the viewer
            // exists, the viewer sees them online and in their (private) instance no matter what
            // "Appear Online To" says. Without this the invitee's client refused the invite with
            // "Cannot accept invite. Target player is offline." whenever the inviter wasn't
            // visible to them (non-friend + Friends-only status, or Favorites-only).
            if (!viewerIsSelf && viewerId != 0 && viewerId != playerid
                && Campus_live_rewrite.api.server.RoomInvites.HasInviteFromTo(playerid, viewerId))
            {
                viewerIsFriend = true;
                viewerIsFavorite = true;
            }
            var stored = _col.FindOne(x => x.playerid == playerid)?.Heartbeat;
            var hb = stored ?? CreateDefaultHeartbeat(playerid, Platforms.Steam, DeviceClasses.Screen);

            hb.serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (string.IsNullOrEmpty(hb.lastOnline)) hb.lastOnline = DateTime.UtcNow.ToString("o");
            hb.appVersion = Campus_live_rewrite.api.server.ConfigCampus.PresenceAppVersion;

            // Defensive clamp: a legacy row with an out-of-range deviceClass (-1) or vrMovementMode
            // makes the client's strict presence parser throw and drop the WHOLE friends-list array
            // (see the deviceClass=-1 trap). Never serve one from this read.
            if (hb.deviceClass < 0) hb.deviceClass = 0;      // 0 = Unknown
            if (hb.vrMovementMode < 0) hb.vrMovementMode = 1;
            // statusVisibility is 0..3 on the client; the old default row carried 4, and one such
            // entry in a friends-list page made the client drop the WHOLE page ("some friends are
            // missing and their profile won't open"). Clamp on the way out too.
            if (hb.statusVisibility < 0 || hb.statusVisibility > 3) hb.statusVisibility = 0;

            bool live = CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(playerid);

            // "Appear Online To" (statusVisibility): 0 Everyone, 1 Friends, 2 Favorites, 3 No One.
            // The player always sees their OWN real presence; others see it only if the setting
            // allows THEM. Changing this in-game (PUT player/statusvisibility) therefore reflects to
            // friends within one presence poll. Unknown/legacy values are treated as Everyone.
            int vis = hb.statusVisibility;
            bool hiddenByStatus = !viewerIsSelf && (
                   vis == 3
                || (vis == 1 && !viewerIsFriend)
                || (vis == 2 && !viewerIsFavorite));
            // "Appear offline" must not hide a player from the people IN THE SAME INSTANCE: the client builds its player list (and the
            // vote-to-kick menu) from this presence, so a player who read as offline there could not be voted out - an exploit.
            // Anyone standing in the room can see them anyway; friends / the rest of the world still see them offline.
            if (hiddenByStatus && !viewerIsSelf && viewerId != 0 && live && SameInstance(viewerId, hb)) hiddenByStatus = false;
            // A block hides presence both ways: neither of them sees the other online or where.
            if (!viewerIsSelf && viewerId != 0 && !hiddenByStatus && FriendsDB.IsBlockedEitherWay(viewerId, playerid))
                hiddenByStatus = true;

            // online-in-no-room: a player whose presence says online but names no instance (just
            // kicked, mid-recovery) NREs the 2025 client's friend card (NotInSameInstanceHide
            // dereferences the instance when online) - the card renders blank and the profile
            // won't open. To everyone else that player reads as offline until they land somewhere.
            if (!viewerIsSelf && hb.isOnline && hb.roomInstance == null) hb.isOnline = false;

            if (!live || hiddenByStatus)
            {
                hb.isOnline = false;
                hb.roomInstance = null;
            }
            // "Full" on the friends list / room card: computed from live presence, never stored.
            if (hb.roomInstance != null) hb.roomInstance.isFull = InstanceIsFull(hb.roomInstance);
            return hb;
        }

        /// <summary>Is the viewer in the very instance this (stored) heartbeat says the other player is in? Read-only.</summary>
        private static bool SameInstance(ulong viewerId, Heartbeat other)
        {
            long theirs = other.roomInstance?.roomInstanceId ?? 0;
            if (theirs == 0) return false;
            long mine = _col.FindOne(x => x.playerid == viewerId)?.Heartbeat?.roomInstance?.roomInstanceId ?? 0;
            if (mine == 0 && _placedIn.TryGetValue(viewerId, out var placed)) mine = placed?.roomInstanceId ?? 0;
            return mine == theirs;
        }

        public static Heartbeat GetPlayerHeartbeat(ulong playerid)
        {
            var data = _col.FindOne(x => x.playerid == playerid);
            var hb = data?.Heartbeat;
            // own-heartbeat clamp: the same out-of-range guard GetPresenceForList applies
            if (hb != null)
            {
                if (hb.statusVisibility < 0 || hb.statusVisibility > 3) hb.statusVisibility = 0;
                if (hb.deviceClass < 0) hb.deviceClass = 0;
                if (hb.vrMovementMode < 0) hb.vrMovementMode = 1;
            }

            if (hb == null)
            {
                hb = CreateDefaultHeartbeat(playerid, Platforms.Steam, DeviceClasses.Screen);

                // No row at all. If we know where we put them, say that rather than
                // "nowhere", and write the row back so matchmaking agrees with us.
                if (_placedIn.TryGetValue(playerid, out var placed))
                {
                    hb.roomInstance = placed;
                    hb.isOnline = true;
                    Console.WriteLine($"[heartbeat] {playerid}: no row - restored instance " +
                                      $"{placed.roomInstanceId} ({placed.name}) instead of answering 'nowhere'");
                    try { UpdatePlayerHeartbeat(playerid, placed); } catch { }
                }
            }
            else if (hb.roomInstance == null && _placedIn.TryGetValue(playerid, out var placed))
            {
                // Row exists but has lost its room instance while we still believe the
                // player is somewhere. Nothing deliberate nulls it without clearing
                // _placedIn, so this is drift - repair it rather than sending them home.
                hb.roomInstance = placed;
                hb.isOnline = true;
                Console.WriteLine($"[heartbeat] {playerid}: row lost its instance - restored " +
                                  $"{placed.roomInstanceId} ({placed.name}) instead of answering 'nowhere'");
                try { UpdatePlayerHeartbeat(playerid, placed); } catch { }
            }

            // serverTime was DECLARED and never assigned anywhere, so every presence
            // heartbeat this server has ever sent carried serverTime = 0 - i.e. 1970. Any
            // client-side check that compares our clock to its own sees a ~56 year skew.
            // Unix SECONDS, matching the JWT iat/exp the client already parses from our
            // tokens and the rest of this codebase (BanDB, moderation timestamps).
            hb.serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Also never set: null reads as "never seen", which is wrong for a player who
            // is heartbeating at us right now.
            hb.lastOnline = DateTime.UtcNow.ToString("o");

            long now = hb.roomInstance?.roomInstanceId ?? 0;
            long prev = _lastAnswered.TryGetValue(playerid, out var p) ? p : 0;
            if (now != prev)
            {
                Console.WriteLine($"[heartbeat] {playerid}: answer changed {prev} -> {now} " +
                                  $"({hb.roomInstance?.name ?? "nowhere"})");
                _lastAnswered[playerid] = now;
            }

            return hb;
        }

        public static string GetPlayerHeartbeatRoomName(ulong playerid)
        {
            var data = _col.FindOne(x => x.playerid == playerid);
            return data?.Heartbeat?.roomInstance?.name ?? "nowhere!";
        }

        public static bool CreatePlayerHeartbeat(ulong playerid, out Heartbeat heartbeat, Platforms platform, DeviceClasses deviceClasses)
        {
            if (_col.Exists(x => x.playerid == playerid))
            {
                heartbeat = null!;
                return false;
            }

            heartbeat = CreateDefaultHeartbeat(playerid, platform, deviceClasses);
            _col.Insert(new PlayerHeartbeatData
            {
                iD = playerid,   // keyed by playerid - see note on PlayerHeartbeatData.iD
                playerid = playerid,
                Heartbeat = heartbeat
            });

            return true;
        }

        public static void UpdateVrMovementMode(ulong playerid, int mode)
        {
            var data = _col.FindOne(x => x.playerid == playerid);
            if (data?.Heartbeat == null) return;
            data.Heartbeat.vrMovementMode = mode;
            _col.Update(data);
        }

        public static void UpdateStatusVisibility(ulong playerid, int visibility)
        {
            var data = _col.FindOne(x => x.playerid == playerid);
            if (data != null)
            {
                data.Heartbeat.statusVisibility = visibility;
                _col.Update(data);
            }
            else
            {
                // Upsert: a player who changes their status before any heartbeat row exists used
                // to have the change silently dropped. Create the row so it persists (and so the
                // change is honored by GetPresenceForList right away).
                var hb = CreateDefaultHeartbeat(playerid, Platforms.Steam, DeviceClasses.Screen);
                hb.statusVisibility = visibility;
                _col.Insert(new PlayerHeartbeatData { iD = playerid, playerid = playerid, Heartbeat = hb });
            }
            Console.WriteLine($"[presence] {playerid} statusVisibility -> {visibility}");
        }

        public static string GetPlayerCurrentRoomName(ulong playerid)
        {
            var data = _col.FindOne(x => x.playerid == playerid);
            return data?.Heartbeat?.roomInstance?.name ?? "not in a room.";
        }

        /// <summary>
        /// Finds a live room instance by its id, whoever is in it.
        ///
        /// connection-info is asked about a SPECIFIC roomInstanceId and used to answer it
        /// from the caller's own heartbeat instead. That was survivable only while every
        /// public instance shared the Photon room name "room" - any mismatch still landed
        /// the player in the same place. With per-instance names a mismatch sends them to
        /// the wrong Photon room and the load dies with "disconnect during load".
        /// </summary>
        /// <summary>
        /// The live instances of a room with who is standing in each - the owner's "Instances"
        /// tab (reference match GET /room/{id}/instances). Grouped by Photon room, which IS the
        /// instance as far as the client is concerned.
        /// </summary>
        public static List<(RoomInstance inst, List<ulong> players)> LiveInstancesOfRoom(ulong roomId)
        {
            var rows = _col.FindAll()
                .Where(x => x.Heartbeat != null && x.Heartbeat.isOnline && x.Heartbeat.roomInstance != null
                         && x.Heartbeat.roomInstance.roomId == (long)roomId
                         && CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(x.playerid))
                .ToList();
            return rows
                .GroupBy(x => string.IsNullOrEmpty(x.Heartbeat!.roomInstance!.photonRoomIdYeee) ? x.Heartbeat.roomInstance.roomInstanceId.ToString() : x.Heartbeat.roomInstance.photonRoomIdYeee!)
                .Select(g => (g.First().Heartbeat!.roomInstance!, g.Select(x => x.playerid).Distinct().ToList()))
                .OrderBy(t => t.Item1.roomInstanceId)
                .ToList();
        }

        /// <summary>
        /// Change a flag on a live instance. Presence keeps a COPY of the instance on every
        /// player's row, so a change (private, in progress) has to land on each of them or the
        /// next heartbeat serves the old value back.
        /// </summary>
        public static int UpdateInstanceEverywhere(long roomInstanceId, Action<RoomInstance> edit)
        {
            int n = 0;
            foreach (var row in _col.FindAll().Where(x => x.Heartbeat?.roomInstance != null && x.Heartbeat.roomInstance.roomInstanceId == roomInstanceId).ToList())
            {
                edit(row.Heartbeat!.roomInstance!);
                _col.Update(row);
                n++;
            }
            if (_recentInstances.TryGetValue(roomInstanceId, out var r)) edit(r.inst);
            return n;
        }

        /// <summary>
        /// One-off repair of stored presence rows that carry values the client's strict parser
        /// refuses (statusVisibility 4 from the old default, deviceClass -1). Returns how many.
        /// </summary>
        public static int FixOutOfRangePresence()
        {
            int n = 0;
            foreach (var row in _col.FindAll().ToList())
            {
                var hb = row.Heartbeat;
                if (hb == null) continue;
                bool changed = false;
                if (hb.statusVisibility < 0 || hb.statusVisibility > 3) { hb.statusVisibility = 0; changed = true; }
                if (hb.deviceClass < 0) { hb.deviceClass = 0; changed = true; }
                if (hb.vrMovementMode < 0) { hb.vrMovementMode = 1; changed = true; }
                if (changed) { _col.Update(row); n++; }
            }
            return n;
        }

        /// <summary>Everyone connected whose presence puts them in this room instance right now.</summary>
        public static List<ulong> PlayerIdsInInstance(long roomInstanceId)
        {
            if (roomInstanceId == 0) return new List<ulong>();
            return _col.FindAll()
                .Where(x => x.Heartbeat?.roomInstance != null && x.Heartbeat.roomInstance.roomInstanceId == roomInstanceId
                         && CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(x.playerid))
                .Select(x => x.playerid).Distinct().ToList();
        }

        public static RoomInstance? FindInstanceById(long roomInstanceId)
        {
            if (roomInstanceId == 0) return null;

            var live = _col.FindAll()
                .Where(x => x.Heartbeat?.roomInstance != null
                         && x.Heartbeat.roomInstance.roomInstanceId == roomInstanceId)
                .Select(x => x.Heartbeat!.roomInstance!)
                .FirstOrDefault();
            if (live != null) return live;

            // Nobody's row points at it right now - but if it was live minutes ago, a client that
            // still holds the id (a recovering one) can be sent back to the same Photon room.
            if (_recentInstances.TryGetValue(roomInstanceId, out var r) && DateTime.UtcNow - r.seen <= InstanceMemory)
                return r.inst;
            return null;
        }

        /// <summary>
        /// A LIVE instance of a given room, whoever is in it - the same (name, isPrivate) the admin
        /// panel shows. Used by the Discord-presence room lookup so it reflects the instance the
        /// player is actually in (works for dorms, which the static room API doesn't describe the
        /// same way). Returns null if nobody is in an instance of that room right now.
        /// </summary>
        /// <summary>
        /// A player's OWN current room + their status-visibility, straight from their heartbeat row
        /// (no WebSocket gating - the caller IS the player, so they're online by definition). Used
        /// by the Discord-presence self endpoint: statusVisibility decides whether the room may be
        /// shown publicly at all, roomInstance is where they are. Honors the placement fallback.
        /// </summary>
        public static (RoomInstance? room, int statusVisibility) GetSelfPresence(ulong playerid)
        {
            var hb = _col.FindOne(x => x.playerid == playerid)?.Heartbeat;
            RoomInstance? room = hb?.roomInstance;
            if (room == null && _placedIn.TryGetValue(playerid, out var placed)) room = placed;
            return (room, hb?.statusVisibility ?? 0);
        }

        public static RoomInstance? FindInstanceByRoomId(long roomId)
        {
            if (roomId == 0) return null;

            return _col.FindAll()
                .Where(x => x.Heartbeat?.roomInstance != null
                         && x.Heartbeat.roomInstance.roomId == roomId)
                .Select(x => x.Heartbeat!.roomInstance!)
                .FirstOrDefault();
        }

        /// <summary>
        /// Finds a public instance of <paramref name="roomId"/> that someone is already in
        /// and that still has space, so a second player joining the same room LANDS WITH
        /// THEM instead of in a private world of their own.
        ///
        /// This is required by the unique Photon room names in Sessions.CreateRoom. While
        /// every public instance shared the constant name "room", players met by accident -
        /// and also met everyone in every other room, which was the bug. Now that names are
        /// per-instance, matchmaking has to deliberately reuse an instance for people to
        /// meet at all.
        ///
        /// Returns null when nobody is in the room, or every instance is full - the caller
        /// then creates a fresh one.
        /// </summary>
        public static RoomInstance? FindJoinableInstance(ulong roomId, ulong excludePlayerId = 0, long excludeInstanceId = 0)
        {
            // excludeInstanceId = the instance the player is already in. Never offered back: the
            // client keys the room change off a NEW roomInstanceId, so re-matchmaking into the
            // current one ("Join new instance" in a room with others in it) looped the join.
            var occupants = _col.FindAll()
                .Where(x => x.playerid != excludePlayerId
                         && (excludeInstanceId == 0 || x.Heartbeat?.roomInstance?.roomInstanceId != excludeInstanceId)
                         && x.Heartbeat != null
                         && x.Heartbeat.isOnline
                         && x.Heartbeat.roomInstance != null
                         && x.Heartbeat.roomInstance.roomId == (long)roomId
                         && !x.Heartbeat.roomInstance.isPrivate
                         && !IsInstanceInProgress(x.Heartbeat.roomInstance)   // no joining a round mid-game
                         && !IsInstanceBroken(x.Heartbeat.roomInstance)       // joiners hang there (CampusNet stuck-load)
                         && !string.IsNullOrEmpty(x.Heartbeat.roomInstance.photonRoomIdYeee)
                         && CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(x.playerid)
                         // an instance they were kicked out of is not offered to them again
                         && !server.InstanceBlocks.IsBlocked(excludePlayerId, x.Heartbeat.roomInstance))
                .Select(x => x.Heartbeat.roomInstance!)
                .ToList();

            if (occupants.Count == 0)
            {
                // Nobody recorded in the room - but if an instance of it was live moments ago, this
                // is most likely a player recovering from the mass drop above. Put them back in the
                // same Photon room so the group re-forms instead of each opening a fresh instance.
                var recent = RecentInstanceOfRoom(roomId);
                if (recent != null && server.InstanceBlocks.IsBlocked(excludePlayerId, recent)) recent = null;
                if (recent != null && excludeInstanceId != 0 && recent.roomInstanceId == excludeInstanceId) recent = null;
                if (recent != null && IsInstanceBroken(recent)) recent = null;
                if (recent != null)
                    Console.WriteLine($"[matchmake] regrouping into recent instance {recent.roomInstanceId} of room {roomId} (photon {recent.photonRoomIdYeee})");
                return recent;
            }

            // Group by the Photon room - that IS the instance as far as the client is
            // concerned - and pick the fullest one with space, so players gather together
            // rather than scattering one-per-instance.
            var best = occupants
                .GroupBy(r => r.photonRoomIdYeee)
                .Select(g => new { Instance = g.First(), Count = g.Count() })
                .Where(g => g.Count < EffectiveCapacity(g.Instance))
                .OrderByDescending(g => g.Count)
                .FirstOrDefault();

            return best?.Instance;
        }

        /// <summary>
        /// Live player count per ROOM id, in one pass over presence - what Room.CCU carries.
        /// Rooms with nobody in them are simply absent from the map.
        /// </summary>
        private static Dictionary<long, int> _ccuCache = new();
        private static DateTime _ccuCacheAt = DateTime.MinValue;

        public static Dictionary<long, int> LivePlayerCountsByRoom()
        {
            // Cached for a few seconds: this is called once per room response (including the hot
            // list) and a full presence scan per request would be the expensive part of it.
            if ((DateTime.UtcNow - _ccuCacheAt).TotalSeconds < 5) return _ccuCache;

            var counts = new Dictionary<long, int>();
            foreach (var x in _col.FindAll())
            {
                var inst = x?.Heartbeat?.roomInstance;
                if (x?.Heartbeat == null || !x.Heartbeat.isOnline || inst == null) continue;
                counts[inst.roomId] = counts.TryGetValue(inst.roomId, out var n) ? n + 1 : 1;
            }
            _ccuCache = counts; _ccuCacheAt = DateTime.UtcNow;
            return counts;
        }

        private static Dictionary<long, int> _connectedCache = new();
        private static DateTime _connectedCacheAt = DateTime.MinValue;

        /// <summary>
        /// Like <see cref="LivePlayerCountsByRoom"/>, but only players holding a live notification
        /// socket - a crashed or closed game leaves its presence row "online" until the next login,
        /// and those ghosts must not make a room look busy in the Hot / Rising feeds.
        /// </summary>
        public static Dictionary<long, int> ConnectedPlayerCountsByRoom()
        {
            if ((DateTime.UtcNow - _connectedCacheAt).TotalSeconds < 5) return _connectedCache;
            var counts = new Dictionary<long, int>();
            foreach (var x in _col.FindAll())
            {
                var inst = x?.Heartbeat?.roomInstance;
                if (x?.Heartbeat == null || !x.Heartbeat.isOnline || inst == null) continue;
                if (!CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(x.playerid)) continue;
                counts[inst.roomId] = counts.TryGetValue(inst.roomId, out var n) ? n + 1 : 1;
            }
            _connectedCache = counts; _connectedCacheAt = DateTime.UtcNow;
            return counts;
        }

        /// <summary>
        /// The cap that actually applies to an instance: the subroom's CURRENT MaxPlayers (what
        /// the room settings show), falling back to the value stamped when the instance was
        /// opened, and finally 4 like the reference. Read live so an owner raising the cap in
        /// room settings frees the running instance instead of it staying "full" at the old
        /// number. Cached briefly - this runs on every heartbeat.
        /// </summary>
        private static readonly Dictionary<(long, long), (int cap, DateTime at)> _capCache = new();
        public static int EffectiveCapacity(RoomInstance? inst)
        {
            if (inst == null) return 0;
            int fromSub = 0;
            var key = (inst.roomId, inst.subRoomId);
            lock (_capCache)
            {
                if (_capCache.TryGetValue(key, out var c) && (DateTime.UtcNow - c.at).TotalSeconds < 20) fromSub = c.cap;
                else
                {
                    try { fromSub = RoomDB.GetSubRoom((ulong)inst.roomId, (ulong)inst.subRoomId)?.MaxPlayers ?? 0; } catch { fromSub = 0; }
                    _capCache[key] = (fromSub, DateTime.UtcNow);
                    if (_capCache.Count > 5000) _capCache.Clear();
                }
            }
            if (fromSub > 0) return fromSub;
            return inst.maxCapacity > 0 ? inst.maxCapacity : 4;
        }

        /// <summary>Whether the instance is at its player cap (the subroom's MaxPlayers).</summary>
        public static bool InstanceIsFull(RoomInstance? inst)
        {
            if (inst == null) return false;
            int cap = EffectiveCapacity(inst);
            inst.maxCapacity = cap;
            return cap > 0 && CountInInstance(inst.roomInstanceId) >= cap;
        }

        /// <summary>How many online players' presence points at this instance.</summary>
        // live-connection gate: a presence row stays "online" after a crash / closed game until
        // the player logs in again, so ghosts piled up in an instance and it read as full while
        // half the slots were empty. Only a player holding a live notification socket counts.
        /// <summary>Players in any instance of this room right now (live websocket only).</summary>
        public static int CountInRoom(ulong roomId) =>
            _col.FindAll().Count(x => x.Heartbeat != null && x.Heartbeat.isOnline
                                   && x.Heartbeat.roomInstance != null
                                   && x.Heartbeat.roomInstance.roomId == (long)roomId
                                   && CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(x.playerid));

        public static int CountInInstance(long roomInstanceId) =>
            _col.FindAll().Count(x => x.Heartbeat != null && x.Heartbeat.isOnline
                                   && x.Heartbeat.roomInstance != null
                                   && x.Heartbeat.roomInstance.roomInstanceId == roomInstanceId
                                   && CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(x.playerid));

        public static void ClearPlayersHeartbeats()
        {
            foreach (var data in _col.FindAll())
            {
                UpdatePlayerHeartbeat(data.playerid, null, false);
            }
        }

        public static void ClearPlayerHeartbeat(ulong playerId) =>
            UpdatePlayerHeartbeat(playerId, null, false);

        /// <summary>
        /// When this player was last connected, from the STORED row (GetPlayerHeartbeat rewrites
        /// lastOnline to "now" on every read, so it can't be used for this). Stamped by
        /// StampLastSeen on every placement and on notification-socket close, so for an offline
        /// player it is the moment they disconnected. Null when never seen.
        /// </summary>
        public static string? LastSeen(ulong playerid) => _col.FindOne(x => x.playerid == playerid)?.Heartbeat?.lastOnline;

        public static void StampLastSeen(ulong playerid)
        {
            try
            {
                var data = _col.FindOne(x => x.playerid == playerid);
                if (data?.Heartbeat == null) return;
                data.Heartbeat.lastOnline = DateTime.UtcNow.ToString("o");
                _col.Update(data);
            }
            catch { }
        }

        public static void UpdatePlayerHeartbeat(
      ulong playerid,
      RoomInstance? roomInstance,
      bool online = true,
      Platforms platform = Platforms.All,
      DeviceClasses deviceClasses = DeviceClasses.Unknown)
        {
            // Record the DELIBERATE placement before touching the DB. Everything that
            // moves a player goes through here, so this is the one place that can tell
            // "we put them nowhere" apart from "they fell off the record by accident".
            // See _placedIn on GetPlayerHeartbeat.
            if (online && roomInstance != null) { _placedIn[playerid] = roomInstance; Remember(playerid, roomInstance); }
            else _placedIn.TryRemove(playerid, out _);

            var data = _col.FindOne(x => x.playerid == playerid);

            // Capture the prior state so we only push a realtime presence update to friends when it
            // ACTUALLY changed (online<->offline, or a different room instance) - the periodic
            // heartbeat doesn't come through here, but matchmake/join/dorm/ban all do, some with the
            // same instance repeatedly.
            long oldInstance = data?.Heartbeat?.roomInstance?.roomInstanceId ?? 0;
            bool wasOnline  = data?.Heartbeat?.isOnline ?? false;

            if (data != null)
            {
                var hb = data.Heartbeat;
                hb.roomInstance = online ? roomInstance : null;
                hb.isOnline = online;
                hb.lastOnline = DateTime.UtcNow.ToString("o");   // see LastSeen
                // Correct any row still carrying the old hardcoded build, so existing players stop
                // showing as a version mismatch on their next heartbeat (not just fresh rows).
                hb.appVersion = Campus_live_rewrite.api.server.ConfigCampus.PresenceAppVersion;

                _col.Update(data);
            }
            else
            {
                // Create a new heartbeat and assign the current roomInstance
                var hb = CreateDefaultHeartbeat(playerid, platform, deviceClasses);
                hb.roomInstance = roomInstance;
                hb.isOnline = online;

                _col.Insert(new PlayerHeartbeatData
                {
                    iD = playerid,   // keyed by playerid - see note on PlayerHeartbeatData.iD
                    playerid = playerid,
                    Heartbeat = hb
                });
            }

            long newInstance = (online ? roomInstance?.roomInstanceId : null) ?? 0;
            if (wasOnline != online || oldInstance != newInstance)
            {
                _presenceChangedAt[playerid] = DateTime.UtcNow;
                Campus_live_rewrite.api.server.Notifications.FireAndForget(
                    Campus_live_rewrite.api.server.Notifications.PushPresenceToFriends(playerid),
                    $"presence-change {playerid}");
                Campus_live_rewrite.api.server.Notifications.FireAndForget(
                    Campus_live_rewrite.api.server.Notifications.EchoPresence(playerid),
                    $"presence-echo {playerid}");
            }
        }

        public class Heartbeat
        {
            // The client reads the heartbeat's `serverTime` as a DateTime and syncs its whole
            // "server clock" to it. It used to go out as Unix SECONDS (1790349494), which the client
            // took as DateTime TICKS - its server clock read 0001-01-01 00:02:59 (seen in the running
            // PC client's memory, 2026-09-25). Every "time left" was then ~2000 years, and the PC
            // (x86) converts that to int32 as -2147483648: the reward watch's expiry timer fired at
            // once, auto-picked card 1 and closed ("reward watch closes instantly on PC"). ARM
            // saturates to +int.Max instead, which is why Quest never showed it.
            // So the wire field is a UTC ISO-8601 date; the stored number is kept (DB compat) but
            // never serialized.
            [Newtonsoft.Json.JsonIgnore, System.Text.Json.Serialization.JsonIgnore]
            public long serverTime { get; set; }
            [BsonIgnore, Newtonsoft.Json.JsonProperty("serverTime"), JsonPropertyName("serverTime")]
            public string serverTimeUtc => DateTime.UtcNow.ToString("o");
            public long playerId { get; set; }
            public int statusVisibility { get; set; }
            public int platform { get; set; }
            public int deviceClass { get; set; }
            public int vrMovementMode { get; set; }
            public RoomInstance roomInstance { get; set; } // Can be null
            public string lastOnline { get; set; } // ISO 8601 string
            public bool isOnline { get; set; }
            public string appVersion { get; set; }
        }

        public class FullRoomInstance
        {
            public RoomInstance? roomInstance { get; set; }
            public string? correlationId { get; set; }
        }

        public class RoomInstance
        {
            public long roomInstanceId { get; set; }
            public long roomId { get; set; }
            public long subRoomId { get; set; }
            public string? location { get; set; }
            public int roomInstanceType { get; set; }
            public string? name { get; set; }
            // The event/club this instance belongs to (0 = none). The client reads eventId to
            // show the "This Event" tab on the room panel when you're in an event instance
            // (mirrors the reference RoomInstanceDto's eventId/clubId).
            public long eventId { get; set; }
            public long clubId { get; set; }
            public int maxCapacity { get; set; }
            public bool isFull { get; set; }
            public bool isPrivate { get; set; }
            public bool isInProgress { get; set; }
            [JsonIgnore]
            public string? photonRoomIdYeee { get; set; } // im crack
            public string? voiceServerId { get; set; }
            public string? voiceAuthId { get; set; }
            public int matchmakingPolicy { get; set; }

            // Whether the Photon Voice stream is encrypted. RecFlare sets this to false on every
            // room instance, and it MUST be present: absent, the client falls back to its own
            // default (encrypt), and then peers encrypt their voice with no shared key exchange
            // set up - they connect fine but hear each other as silence, which is exactly the
            // "mic works, nobody can hear you" symptom. false = plain voice, everyone hears everyone.
            public bool EncryptVoiceChat { get; set; } = false;
        }

        public enum MatchmakingErrorCode
        {
            Success,
            NoSuchGame,
            PlayerNotOnline,
            InsufficientSpace,
            EventNotStarted,
            EventAlreadyFinished,
            EventCreatorNotReady,
            BlockedFromRoom,
            ProfileLocked,
            NoBirthday,
            MarkedForDelete,
            JuniorNotAllowed,
            Banned,
            AlreadyInBestInstance,
            InsufficientRelationship,
            UpdateRequired = 16,
            AlreadyInTargetInstance,
            RegistrationRequired,
            UGCNotAllowed,
            NoSuchRoom,
            RoomCreatorNotReady,
            RoomIsNotActive,
            RoomBlockedByCreator,
            RoomBlockingCreator,
            RoomIsPrivate,
            RoomInstanceIsPrivate,
            DeviceClassNotSupported = 30,
            DeviceClassNotSupportedByRoomOwner,
            VRMovementModeNotSupportedByRoomOwner,
            EventIsPrivate = 35,
            RoomInviteExpired = 40,
            NoAvailableRegion = 45,
            NotorietyTooPoor = 50,
            BannedFromRoom = 55
        }
    }
}
