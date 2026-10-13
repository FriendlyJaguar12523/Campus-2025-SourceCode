using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;
using CampusLiveSource.Controllers;
using System.Net.WebSockets;
using System.Security.Principal;
using System.Text;

namespace Campus_live_rewrite.api.server
{
    public static class Notifications
    {
        private static readonly byte[] Separator = Encoding.UTF8.GetBytes("\u001e");

        // SendToPlayer/SendToPlayers are `async Task`, but 19 call sites discarded the
        // returned Task (compiler warning CS4014). A websocket push that threw was then
        // swallowed completely - a chat message would simply never arrive, with nothing
        // in the log to say so.
        //
        // This preserves the exact fire-and-forget timing (awaiting would make a chat
        // send wait on every recipient socket) but surfaces failures instead of hiding
        // them. Behaviour is unchanged; only visibility improves.
        public static void FireAndForget(Task task, string what)
        {
            if (task == null) return;
            _ = task.ContinueWith(
                t => Console.WriteLine($"[Notifications] {what} FAILED: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        public static async Task SendToPlayer(ulong playerId, string message)
        {
            // Any notification headed for the game also nudges the player's open website tabs to
            // re-fetch their inbox, so the web bell updates the instant the game event fires.
            SiteNotifications.Notify(playerId);

            if (!NotificationsController.WebSockets.TryGetValue(playerId, out var socket) || socket == null)
            {
                Console.WriteLine($"[Notifications] No active WebSocket for player {playerId}");
                return;
            }

            await SendAsync(socket, message, playerId);
        }

        // Wire names of the subscription-update frames. The client dispatches these by STRING name;
        // the numeric ResponseResults values (12 / 11) were silently dropped, which is why nothing
        // updated live. Confirmed against a live client by the reference (recflare notify
        // notification-types.ts): "PresenceUpdate", "AccountUpdate", "SelfAccountUpdate".
        public const string WirePresenceUpdate = "PresenceUpdate";
        public const string WireAccountUpdate = "AccountUpdate";
        public const string WireSelfAccountUpdate = "SelfAccountUpdate";

        // The reduced, REDACTED presence payload (reference: presenceUpdateMessage). NOT the raw
        // heartbeat: photonRoomIdYeee / voice ids must never ride along (a frame carrying them reads
        // to the client as "local presence out of sync" and it walks itself home), statusVisibility
        // is forced to 0 so the subject isn't hidden from a viewer allowed to see them, and
        // appVersion must be a STRING or the client aborts the whole frame.
        private static object PresencePayload(ulong subjectId, HeartbeatDB.Heartbeat? hb, bool forSelf = false)
        {
            var inst = hb?.roomInstance;
            object? redacted = inst == null ? null : new
            {
                roomInstanceId   = inst.roomInstanceId,
                roomId           = inst.roomId,
                subRoomId        = inst.subRoomId,
                location         = inst.location,
                roomInstanceType = inst.roomInstanceType,
                name             = inst.name,
                maxCapacity      = inst.maxCapacity,
                isFull           = HeartbeatDB.InstanceIsFull(inst),
                isPrivate        = inst.isPrivate,
                isInProgress     = inst.isInProgress
            };
            return new
            {
                playerId         = subjectId,
                // Only the player themself learns their real "Appear Online To"; a viewer's copy
                // says Everyone, because the setting has already been applied to what they see.
                statusVisibility = forSelf ? (hb?.statusVisibility ?? 0) : 0,
                deviceClass      = hb?.deviceClass ?? 0,
                vrMovementMode   = hb?.vrMovementMode ?? 0,
                roomInstance     = redacted,
                isOnline         = hb?.isOnline ?? false,
                appVersion       = hb?.appVersion ?? Campus_live_rewrite.api.server.ConfigCampus.PresenceAppVersion
            };
        }

        private static string Frame(string wireId, object payload) =>
            JsonConvert.SerializeObject(new Campus_live_rewrite.api.WebsocketEvents.Reponse { Id = wireId, Msg = payload });

        /// <summary>
        /// Push the subject's current presence to ONE connected viewer, with that viewer's own
        /// relationship and the subject's "Appear Online To" applied. Used when a viewer first
        /// subscribes, so their list is live immediately.
        /// </summary>
        public static async Task PushPresenceTo(ulong viewerId, ulong subjectId)
        {
            try
            {
                if (!NotificationsController.WebSockets.TryGetValue(viewerId, out var socket) || socket == null) return;
                var rel = FriendsDB.GetRelationships(viewerId);
                bool friend = rel.Any(r => r.PlayerID == subjectId && r.RelationshipType == RelationshipType.Friend);
                bool favorite = friend && rel.Any(r => r.PlayerID == subjectId && r.Favorited != ReciprocalStatus.None);
                var hb = HeartbeatDB.GetPresenceForList(subjectId, viewerIsSelf: false, viewerIsFriend: friend, viewerIsFavorite: favorite, viewerId: viewerId);
                await SendAsync(socket, Frame(WirePresenceUpdate, PresencePayload(subjectId, hb)), viewerId, quiet: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[presence-push] {subjectId} -> {viewerId} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Push the player's OWN presence to their own socket - the record with their real
        /// statusVisibility and IsOnline true. The client's "you" tile / status picker reads this;
        /// without it a status change only showed after the next presence poll (a minute or
        /// more), and "online" never lit up for yourself on connect.
        /// </summary>
        public static async Task PushPresenceToSelf(ulong playerId)
        {
            try
            {
                if (!NotificationsController.WebSockets.TryGetValue(playerId, out var socket) || socket == null) return;
                var hb = HeartbeatDB.GetPresenceForList(playerId, viewerIsSelf: true, viewerIsFriend: true, viewerIsFavorite: true);
                await SendAsync(socket, Frame(WirePresenceUpdate, PresencePayload(playerId, hb, forSelf: true)), playerId, quiet: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[presence-push] self {playerId} failed: {ex.Message}");
            }
        }

        // Realtime presence. Pushes a PresenceUpdate to every connected viewer that should see the
        // subject - their FRIENDS plus everyone who SUBSCRIBED to them over the hub (the in-room
        // player list, party members, profile cards) - whenever the subject's presence changes
        // (connect, disconnect, room change, "Appear Online To"). Per-viewer, so each sees the
        // subject with THEIR relationship and the subject's visibility applied. Not through
        // SendToPlayer, so a presence tick never nudges anyone's website inbox.
        public static async Task PushPresenceToFriends(ulong subjectId)
        {
            try
            {
                var friendIds = FriendsDB.GetRelationships(subjectId)
                    .Where(r => r.RelationshipType == RelationshipType.Friend)
                    .Select(r => r.PlayerID)
                    .Distinct()
                    .ToHashSet();
                var viewers = new HashSet<ulong>(friendIds);
                foreach (var v in NotificationsController.SubscribersOf(subjectId)) viewers.Add(v);
                viewers.Remove(subjectId);
                // The subject sees the change too, instantly - status picker, own online dot.
                await PushPresenceToSelf(subjectId);
                // Friends' open website tabs re-render too (online friends rail, friends modal),
                // and any tab looking at this player's profile page sees the new status/room.
                foreach (var fid in friendIds) SiteNotifications.Notify(fid);
                SiteNotifications.ProfileChanged(subjectId);
                int sent = await PushPresenceToViewers(subjectId, friendIds, viewers);
                if (sent > 0) Console.WriteLine($"[presence-push] {subjectId} -> {sent} viewer(s)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[presence-push] subject {subjectId} failed: {ex.Message}");
            }
        }

        private static async Task<int> PushPresenceToViewers(ulong subjectId, HashSet<ulong> friendIds, HashSet<ulong> viewers)
        {
            int sent = 0;
            foreach (var vid in viewers)
            {
                // Only currently-connected viewers get a live frame; the rest read fresh
                // presence from GET /Matchmaking/player when their client next asks.
                if (!NotificationsController.WebSockets.TryGetValue(vid, out var socket) || socket == null)
                    continue;

                bool friend = friendIds.Contains(vid);
                bool favorite = friend && FriendsDB.GetRelationships(vid)
                    .Any(r => r.PlayerID == subjectId && r.Favorited != ReciprocalStatus.None);

                var hb = HeartbeatDB.GetPresenceForList(
                    subjectId, viewerIsSelf: false, viewerIsFriend: friend, viewerIsFavorite: favorite, viewerId: vid);
                await SendAsync(socket, Frame(WirePresenceUpdate, PresencePayload(subjectId, hb)), vid, quiet: true);
                sent++;
            }
            return sent;
        }

        // A presence change is sent again 3 s and 10 s later. The one push alone could be overtaken:
        // after a server restart (2026-09-27) Kyle's client subscribed at 07:33:12 and was answered
        // poly's stale "Dorm" row; poly landed in ^RecCenter at 07:33:14, and the friends list and
        // Here Now kept showing the private dorm (Party Up and the voice slider greyed out) with poly
        // standing in the room, because nothing re-sends an unchanged presence. A newer change
        // cancels the older echo - it schedules its own.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> _echoVersion = new();
        private static readonly int[] EchoDelaysMs = { 3000, 7000 };

        public static async Task EchoPresence(ulong subjectId)
        {
            long ver = _echoVersion.AddOrUpdate(subjectId, 1, (_, v) => v + 1);
            foreach (int delay in EchoDelaysMs)
            {
                await Task.Delay(delay);
                if (!_echoVersion.TryGetValue(subjectId, out var cur) || cur != ver) return;
                var friendIds = FriendsDB.GetRelationships(subjectId)
                    .Where(r => r.RelationshipType == RelationshipType.Friend)
                    .Select(r => r.PlayerID)
                    .ToHashSet();
                var viewers = new HashSet<ulong>(friendIds);
                foreach (var v in NotificationsController.SubscribersOf(subjectId)) viewers.Add(v);
                viewers.Remove(subjectId);
                await PushPresenceToViewers(subjectId, friendIds, viewers);
            }
        }

        // The public account projection the hub carries (reference: AccountUpdatePayload).
        // PascalCase, with `createdAt` lowercase-camel - the client's canonical spelling.
        private static Dictionary<string, object> AccountUpdatePayload(player_data p)
        {
            return new Dictionary<string, object>
            {
                ["AccountId"]        = p.playerid,
                ["UserName"]         = p.player?.Username ?? "",
                ["DisplayName"]      = p.player?.DisplayName ?? "",
                ["DisplayEmoji"]     = p.player?.DisplayEmoji ?? "",
                ["ProfileImage"]     = string.IsNullOrEmpty(p.player?.profileImage) ? "DefaultProfileImage.jpg" : p.player!.profileImage!,
                // Same default /account/bulk serves. An empty BannerImage here blanked the banner
                // on the Portfolio of anyone with no banner stored (563 players) the moment they
                // changed their picture / bio / name - ProfileBannerImageImpl redraws from it.
                ["BannerImage"]      = string.IsNullOrEmpty(p.player?.bannerImage) ? ConfigCampus.DefaultBannerImage : p.player!.bannerImage!,
                ["TreatAsJunior"]    = p.player?.IsJunior ?? false,
                ["HasBirthday"]      = true,
                ["PersonalPronouns"] = p.player?.pronounFlags ?? 0,
                ["IdentityFlags"]    = p.player?.identityFlags ?? 0,
                ["createdAt"]        = (p.player?.created_at ?? DateTime.UtcNow).ToString("o"),
                ["IsJunior"]         = p.player?.IsJunior ?? false,
            };
        }

        /// <summary>
        /// Push the frames that follow a profile change (reference: pushAccountUpdate). The owner
        /// receives SelfAccountUpdate (owner-only fields FIRST, then the public ones - the client's
        /// decoder emits a derived DTO's own members before its base's) plus AccountUpdate; everyone
        /// connected receives AccountUpdate, so player cards, lists and the room's nameplates
        /// re-render with the new name / picture immediately. Nulls are omitted: a null enum field
        /// makes the client's parser throw. Best-effort - never fails the write that triggered it.
        /// </summary>
        public static async Task PushAccountUpdate(ulong accountId)
        {
            try
            {
                var p = PlayerDB.GetFullAccount(accountId);
                if (p?.player == null) return;
                SiteNotifications.ProfileChanged(accountId);   // website tabs on this profile re-fetch it
                var pub = AccountUpdatePayload(p);

                var self = new Dictionary<string, object>
                {
                    ["Email"] = p.player.Email ?? "",
                    ["Phone"] = "",
                    ["Birthday"] = string.IsNullOrEmpty(p.player.Birthday) ? "1904-01-01T00:00:00.000Z" : p.player.Birthday!,
                    ["JuniorState"] = p.player.IsJunior ? 1 : 0,
                    ["AvailableUsernameChanges"] = 3,
                };
                foreach (var kv in pub) self[kv.Key] = kv.Value;

                if (NotificationsController.WebSockets.TryGetValue(accountId, out var own) && own != null)
                {
                    await SendAsync(own, Frame(WireSelfAccountUpdate, self), accountId, quiet: true);
                    await SendAsync(own, Frame(WireAccountUpdate, pub), accountId, quiet: true);
                }
                string frame = Frame(WireAccountUpdate, pub);
                int sent = 0;
                foreach (var kv in NotificationsController.WebSockets)
                {
                    if (kv.Key == accountId || kv.Value == null) continue;
                    await SendAsync(kv.Value, frame, kv.Key, quiet: true);
                    sent++;
                }
                Console.WriteLine($"[account-push] {accountId} -> self + {sent} viewer(s)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[account-push] {accountId} failed: {ex.Message}");
            }
        }

        // Invoke ONE of the client's OWN hub handlers by name (RelationshipChanged,
        // FriendRequestReceived, RelationshipsInvalid, GiftPackageReceived, ...) instead of the
        // generic "Notification" wrapper SendToPlayer uses. The 2023 server proved against real
        // client traffic that these named handlers are what actually raise a friend-request alert
        // and flip the friends list live; the wrapped "Notification" path updates a message toast
        // but never fires them. `type=1` = a SignalR invocation. Additive and best-effort: a client
        // without the handler ignores it.
        // The client has exactly ONE hub handler: the "Notification" invocation carrying a JSON
        // string {Id, Msg}, dispatched by Id (reference notify/notification-types.ts). A SignalR
        // invocation with any other target name is parsed and dropped in silence - which is what
        // every "RelationshipsInvalid" / "RelationshipChanged" sent this way was doing. So a named
        // invocation is turned into the frame the client dispatches: the relationship names map
        // to their numeric ids (50 / 1), anything else rides under its own name (AccountUpdate,
        // RoomUpdate, PresenceUpdate ... are name-addressed on the client).
        private static readonly Dictionary<string, string> HubInvocationToFrame = new(StringComparer.OrdinalIgnoreCase)
        {
            ["RelationshipsInvalid"] = "50",
            ["RelationshipChanged"] = "1",
            ["MessageReceived"] = "2",
            ["MessageDeleted"] = "3",
            ["RefreshLogin"] = "5",
            ["Logout"] = "6",
        };
        // Names with no client channel at all - sending them is a no-op, so they are skipped
        // (the RelationshipChanged / stored-message frames beside them carry the news).
        private static readonly HashSet<string> HubInvocationNoChannel = new(StringComparer.OrdinalIgnoreCase)
        {
            "FriendRequestReceived", "FriendRequestCancelled",
        };

        public static async Task SendHubInvocation(ulong playerId, string target, params object[] arguments)
        {
            if (HubInvocationNoChannel.Contains(target)) return;
            string id = HubInvocationToFrame.TryGetValue(target, out var mapped) ? mapped : target;
            object? msg = arguments != null && arguments.Length > 0 ? arguments[0] : null;
            var frame = new WebsocketEvents.Reponse { Id = id, Msg = msg };
            if (!NotificationsController.WebSockets.TryGetValue(playerId, out var socket) || socket == null)
                return;
            if (socket.State != WebSocketState.Open) return;
            try
            {
                await SendAsync(socket, JsonConvert.SerializeObject(frame), playerId, quiet: true);
                Console.WriteLine($"[Notifications] hub '{target}' (frame {id}) -> {playerId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Notifications] hub '{target}' -> {playerId} FAILED: {ex.Message}");
            }
        }

        // Broadcast a named hub invocation to EVERY connected client (the SendToAll twin of
        // SendHubInvocation). Used for the public AccountUpdate so anyone looking at a player's
        // profile redraws it live.
        public static async Task SendHubInvocationToAll(string target, params object[] arguments)
        {
            var packet = new { type = 1, target, arguments = arguments ?? Array.Empty<object>() };
            var json = JsonConvert.SerializeObject(packet);
            var data = Encoding.UTF8.GetBytes(json);
            var buffer = new byte[data.Length + Separator.Length];
            Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
            Buffer.BlockCopy(Separator, 0, buffer, data.Length, Separator.Length);

            var tasks = new List<Task>();
            foreach (var kv in NotificationsController.WebSockets)
            {
                var socket = kv.Value;
                if (socket == null || socket.State != WebSocketState.Open) continue;
                tasks.Add(socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None));
            }
            try { await Task.WhenAll(tasks); } catch (Exception ex) { Console.WriteLine($"[Notifications] hub-all '{target}' FAILED: {ex.Message}"); }
        }

        // The PROVEN account-update push, mirroring the reference server (RecFlare) exactly:
        //   1. SelfAccountUpdate (PRIVATE self DTO) -> owner: their own account state.
        //   2. AccountUpdate     (PUBLIC DTO)       -> owner: redraws the owner's open profile.
        //   3. AccountUpdate     (PUBLIC DTO)       -> broadcast: everyone redraws THIS player's
        //      profile (new picture / banner / name) without a refetch.
        // The old code sent only SelfAccountUpdate (owner) - which does NOT redraw the profile
        // panel (that's why a new photo needed a relog) - and, when it once broadcast, it sent the
        // PRIVATE account_data_me to everyone, which the client applied to their OWN account and
        // mass-kicked them. The PUBLIC DTO (no email/settings) is what makes the broadcast safe.
        // The PUBLIC account DTO as an AccountUpdate notification. `nickname` (the viewer's own name for this player) replaces the
        // display name when given - nothing else about the account changes, and only that one viewer's client ever receives it.
        private static string PublicAccountUpdateJson(dynamic self, string? nickname)
        {
            var pub = new
            {
                accountId = self.accountId,
                username = self.username,
                displayName = nickname ?? (string)self.displayName,
                profileImage = self.profileImage,
                bannerImage = self.bannerImage,
                displayEmoji = self.displayEmoji,
                isJunior = self.isJunior,
                platforms = self.platforms,
                personalPronouns = self.personalPronouns,
                identityFlags = self.identityFlags,
                createdAt = self.createdAt,
            };
            return JsonConvert.SerializeObject(new WebsocketEvents.Reponse { Id = "AccountUpdate", Msg = pub });
        }

        /// <summary>Tell ONE viewer's client to redraw a player under that viewer's nickname (or the real name once cleared) - used when
        /// the nickname is set or removed on the website, so the game updates without a relog.</summary>
        public static async Task PushAccountUpdateToViewer(ulong viewerId, ulong accountId)
        {
            var self = PlayerDB.GetAccountMeSafe(accountId);
            if (self == null) return;
            await SendToPlayer(viewerId, PublicAccountUpdateJson(self, SitePrefsDB.NicknameFor(viewerId, accountId)));
        }

        public static async Task PushAccountUpdate(ulong accountId, bool broadcast = true)
        {
            var self = PlayerDB.GetAccountMeSafe(accountId);
            if (self == null) return;

            // Deliver the SAME way the working notifications do (level/reputation/accept toast):
            // wrapped by SendToPlayer/SendToAll as target="Notification" with an inner {Id, Msg},
            // where the client dispatches on the inner Id. A raw named-target SignalR invocation is
            // ignored by THIS (2025) client - it only reads the "Notification" envelope.
            //
            // ONLY AccountUpdate is sent - deliberately. It's the profile-redraw notification and
            // carries the new picture/banner/name. Sending SelfAccountUpdate as WELL would make the
            // client reload the SAME image id a second time concurrently and double-complete its
            // load promise -> the [385120] dorm drop. One update = one load.
            string pubJson = PublicAccountUpdateJson(self, null);

            await SendToPlayer(accountId, pubJson);    // owner: redraw their own profile
            if (broadcast)
            {
                // everyone: redraw THIS player's profile - each viewer gets their OWN nickname for them where they set one
                var tasks = new List<Task>();
                foreach (var kv in NotificationsController.WebSockets)
                {
                    string? nick = kv.Key != accountId ? SitePrefsDB.NicknameFor(kv.Key, accountId) : null;
                    tasks.Add(SendAsync(kv.Value, nick == null ? pubJson : PublicAccountUpdateJson(self, nick), kv.Key));
                }
                await Task.WhenAll(tasks);
            }
        }

        public static async Task SendToAll(string message)
        {
            var tasks = NotificationsController.WebSockets
                .Select(kv => SendAsync(kv.Value, message, kv.Key));

            await Task.WhenAll(tasks);
        }

        public static async Task SendToPlayers(List<ulong> playerIds, string message)
        {
            var tasks = new List<Task>();

            foreach (var id in playerIds)
            {
                if (NotificationsController.WebSockets.TryGetValue(id, out var socket) && socket != null)
                    tasks.Add(SendAsync(socket, message, id));
            }

            await Task.WhenAll(tasks);
        }

        private static async Task SendAsync(WebSocket socket, string message, ulong? playerId = null, bool quiet = false)
        {
            if (socket == null || socket.State != WebSocketState.Open)
                return;

            var packet = new SockSignalR
            {
                type = MessageTypes.Idk,
                target = "Notification",
                arguments = new object[] { message },
                nonblocking = true,
                result = "200 OK"
            };

            var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(packet));
            var buffer = new byte[data.Length + Separator.Length];
            Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
            Buffer.BlockCopy(Separator, 0, buffer, data.Length, Separator.Length);

            // Log the actual message text, not the byte[] (which prints "System.Byte[]" and made
            // this line useless for debugging why a push didn't land).
            if (!quiet)
                Console.WriteLine($"[Notifications] Sending to {(playerId.HasValue ? playerId.ToString() : "unknown player")}: {message}");

            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Notifications] Failed to send to {(playerId.HasValue ? playerId.ToString() : "unknown player")}: {ex.Message}");

                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Send failed", CancellationToken.None);
                }
                catch 
                { 

                }
            }
        }

        /// <summary>
        /// Tells a player their own account changed (new profile picture, banner, identity
        /// flags, ...) so their client re-reads it.
        ///
        /// ONLY the owner is notified, and that is deliberate.
        ///
        /// This used to ALSO push an "AccountUpdate" to every connected socket via
        /// SendToAll - and the payload was GetAccountMeSafe(accountId), i.e. the changing
        /// player's OWN account object (account_data_me: their id, their settings, their
        /// email). Every other player in the server received one person's personal account
        /// record and applied it over their own, which corrupted their client state: they
        /// were error-kicked to their dorm, sometimes with the game breaking outright, and
        /// sometimes bounced back into the room afterwards.
        ///
        /// It also silently broke moderation. A banned player who got mass-kicked simply
        /// rejoined the room they were in, so changing a profile picture effectively undid
        /// bans for everyone online.
        ///
        /// Other players do not need this push: the client re-reads profiles through
        /// /Accounts/account/bulk when it renders them, so a new picture still appears -
        /// just on next fetch instead of instantly.
        ///
        /// If a genuine public "someone's profile changed" broadcast is ever wanted, it
        /// needs its own PUBLIC payload shape (never account_data_me) - the whole problem
        /// was sending a private per-player object to everyone.
        /// </summary>
        public static async Task<bool> RefreshAccount(ulong accountId)
        {
            try
            {
                var selfUpdate = JsonConvert.SerializeObject(
                    WebsocketEvents.create_player_updated_account2_Response(
                        "SelfAccountUpdate", PlayerDB.GetAccountMeSafe(accountId)));

                await Notifications.SendToPlayer(accountId, selfUpdate);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send notification: {ex.Message}");
                return false;
            }
        }

        private class SockSignalR
        {
            public MessageTypes type { get; set; }
            public string? target { get; set; }
            public object[]? arguments { get; set; }
            public bool nonblocking { get; set; }
            public object? result { get; set; }
        }

        private enum MessageTypes
        {
            Invocation,
            Idk
        }
    }
}
