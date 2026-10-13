using System.Net.WebSockets;
using LiteDB;
using Newtonsoft.Json;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using Campus_live_rewrite.Controllers;
using static Campus_live_rewrite.api.WebsocketEvents;
using static Campus_live_rewrite.Controllers.AccountsController;

namespace Campus_live_rewrite.api.dbs
{
    public class FriendsDB
    {
        public static LiteDatabase PlayerFriendDBFile = new LiteDatabase(Environment.CurrentDirectory + "/Data/DBs/PlayersFriends.db");


        // ==========================
        // GET ALL RELATIONSHIPS
        // ==========================
        public static List<RelationshipDetail> GetRelationships(ulong playerId)
        {
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");

            List<RelationshipDetail> list = new();

            // Read the caller's OWN side of each pair (PlayerID == caller). The client's
            // RelationshipResponse is projected from the caller's point of view: PlayerID is the
            // OTHER player, RelationshipType/Favorited/Ignored/Muted are the CALLER's own values
            // (RecFlare's toResponse does exactly this).
            //
            // This used to read the OTHER player's row instead (filter OtherPlayerID == caller),
            // which returned that player's flags and, worse, the OTHER side's request direction -
            // so a friend request you SENT showed up as one you RECEIVED (and the flags a favorite
            // will set below would have landed on the wrong person's list).
            foreach (var data in col.FindAll())
            {
                if (data.Relationship == null || data.Relationship.PlayerID != playerId)
                    continue;

                list.Add(new RelationshipDetail
                {
                    Id = data.Relationship.Id,
                    PlayerID = data.Relationship.OtherPlayerID,   // the OTHER player (client's shape)
                    OtherPlayerID = playerId,                     // the caller
                    RelationshipType = data.Relationship.RelationshipType,
                    Muted = data.Relationship.Muted,
                    Ignored = data.Relationship.Ignored,
                    Favorited = data.Relationship.Favorited,
                    VoiceVolume = data.Relationship.VoiceVolume
                });
            }

            return list;
        }

        // ==========================
        // BLOCK ("ignore" on the wire). Unlike favorite / mute this lands on BOTH sides of the
        // pair: the blocker's row gets Local, the blocked player's row gets Remote (Mutual when
        // both have blocked each other), so each of them reads Ignored = true and their clients
        // hide and mute the other. Only the blocker's own part is theirs to lift: the blocked
        // player's "unblock" cannot clear a Remote, so a block stays until the player who set it
        // takes it off. Blocking also ends a friendship / pending request between the two.
        // Both players get a live RelationshipChanged frame (id 1) - which is what the client
        // repaints its card / friends list from - plus a website refresh.
        // ==========================
        public static RelationshipDetail SetBlock(ulong caller, ulong target, bool value)
        {
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");
            var mine = GetOrCreateRow(col, caller, target);
            var theirs = GetOrCreateRow(col, target, caller);

            static ReciprocalStatus With(ReciprocalStatus s, ReciprocalStatus part, bool on)
            {
                bool local = s == ReciprocalStatus.Local || s == ReciprocalStatus.Mutual;
                bool remote = s == ReciprocalStatus.Remote || s == ReciprocalStatus.Mutual;
                if (part == ReciprocalStatus.Local) local = on; else remote = on;
                return local && remote ? ReciprocalStatus.Mutual : local ? ReciprocalStatus.Local : remote ? ReciprocalStatus.Remote : ReciprocalStatus.None;
            }
            bool wasBlocking = mine.Relationship.Ignored == ReciprocalStatus.Local || mine.Relationship.Ignored == ReciprocalStatus.Mutual;
            mine.Relationship.Ignored = With(mine.Relationship.Ignored, ReciprocalStatus.Local, value);
            theirs.Relationship.Ignored = With(theirs.Relationship.Ignored, ReciprocalStatus.Remote, value);

            bool unfriended = false;
            if (value && (mine.Relationship.RelationshipType != RelationshipType.None || theirs.Relationship.RelationshipType != RelationshipType.None))
            {
                mine.Relationship.RelationshipType = RelationshipType.None;
                theirs.Relationship.RelationshipType = RelationshipType.None;
                unfriended = true;
            }
            col.Update(mine);
            col.Update(theirs);
            if (unfriended)
            {
                try { RemoveFriendInviteMessage(caller, target); RemoveFriendInviteMessage(target, caller); } catch { }
                try { SitePrefsDB.Forget(caller, target); SitePrefsDB.Forget(target, caller); } catch { }   // nickname / home pin go with the friendship
            }

            string what = value ? (wasBlocking ? "re-blocked" : "blocked") : (wasBlocking ? "unblocked" : "asked to unblock (not theirs to lift)");
            Console.WriteLine($"[block] {caller} {what} {target}: mine={mine.Relationship.Ignored} theirs={theirs.Relationship.Ignored}{(unfriended ? " (friendship / request ended)" : "")}");

            // Live to both sides: the frame the client's RelationshipChanged handler repaints
            // from, in each player's own point of view.
            Notifications.FireAndForget(Notifications.SendToPlayer(caller, JsonConvert.SerializeObject(new Reponse { Id = "1", Msg = RelationshipView(mine.Relationship) })), "block changed (self)");
            Notifications.FireAndForget(Notifications.SendToPlayer(target, JsonConvert.SerializeObject(new Reponse { Id = "1", Msg = RelationshipView(theirs.Relationship) })), "block changed (other)");
            Notifications.FireAndForget(Notifications.SendHubInvocation(caller, "RelationshipsInvalid"), "block invalidate (self)");
            Notifications.FireAndForget(Notifications.SendHubInvocation(target, "RelationshipsInvalid"), "block invalidate (other)");
            SiteNotifications.Notify(caller);
            SiteNotifications.Notify(target);
            // Each now sees the other as offline (PresenceIsBlocked); refresh what they see.
            Notifications.FireAndForget(Notifications.PushPresenceTo(caller, target), "block presence refresh (self)");
            Notifications.FireAndForget(Notifications.PushPresenceTo(target, caller), "block presence refresh (other)");

            return new RelationshipDetail
            {
                Id = mine.Relationship.Id,
                PlayerID = target,
                OtherPlayerID = caller,
                RelationshipType = mine.Relationship.RelationshipType,
                Muted = mine.Relationship.Muted,
                Ignored = mine.Relationship.Ignored,
                Favorited = mine.Relationship.Favorited,
                VoiceVolume = mine.Relationship.VoiceVolume
            };
        }

        /// <summary>The caller's voice volume for one player (their own side of the row).</summary>
        /// <returns>The caller's relationship with the target afterwards, in the client's shape
        /// (the client caches the update's response as that player's relationship).</returns>
        public static object SetVoiceVolume(ulong caller, ulong target, int volume)
        {
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");
            var row = GetOrCreateRow(col, caller, target);
            row.Relationship.VoiceVolume = volume;
            col.Update(row);
            return RelationshipView(row.Relationship);
        }

        /// <summary>True when either of the two has blocked the other.</summary>
        public static bool IsBlockedEitherWay(ulong a, ulong b)
        {
            if (a == 0 || b == 0 || a == b) return false;
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");
            var row = col.FindOne(x => x.PlayerID == a && x.Relationship.OtherPlayerID == b);
            if (row?.Relationship != null && row.Relationship.Ignored != ReciprocalStatus.None) return true;
            var other = col.FindOne(x => x.PlayerID == b && x.Relationship.OtherPlayerID == a);
            return other?.Relationship != null && other.Relationship.Ignored != ReciprocalStatus.None;
        }

        /// <summary>True when <paramref name="blocker"/> has blocked <paramref name="other"/> (their own Local part).</summary>
        public static bool HasBlocked(ulong blocker, ulong other)
        {
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");
            var row = col.FindOne(x => x.PlayerID == blocker && x.Relationship.OtherPlayerID == other);
            return row?.Relationship != null && (row.Relationship.Ignored == ReciprocalStatus.Local || row.Relationship.Ignored == ReciprocalStatus.Mutual);
        }

        private static YourFriendData GetOrCreateRow(ILiteCollection<YourFriendData> col, ulong owner, ulong other)
        {
            var row = col.FindOne(x => x.PlayerID == owner && x.Relationship.OtherPlayerID == other);
            if (row != null) return row;
            row = new YourFriendData
            {
                Id = ObjectId.NewObjectId(),
                PlayerID = owner,
                Relationship = new RelationshipDetail
                {
                    Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    PlayerID = owner,
                    OtherPlayerID = other,
                    RelationshipType = RelationshipType.None,
                    Favorited = ReciprocalStatus.None,
                    Ignored = ReciprocalStatus.None,
                    Muted = ReciprocalStatus.None,
                    VoiceVolume = 100
                }
            };
            col.Insert(row);
            return row;
        }

        // ==========================
        // SET A PER-SIDE FLAG (favorite / ignore / mute) on the CALLER's own side.
        // Mirrors RecFlare's setRelationshipFlag: the write lands on the caller's side of the
        // pair, and flagging someone you have no relationship with creates a bare (None) row to
        // hold the flag - so you can favorite/ignore/mute a player you were never friends with.
        // Value maps to ReciprocalStatus.Local (set, serializes as 1) or None (clear, 0), which
        // is the 0/1 the client reads for each flag. Returns the caller-POV relationship.
        // ==========================
        public static RelationshipDetail SetRelationshipFlag(ulong caller, ulong target, FriendInfo flag, bool value)
        {
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");

            var row = col.FindOne(x => x.PlayerID == caller && x.Relationship.OtherPlayerID == target);
            if (row == null)
            {
                row = new YourFriendData
                {
                    Id = ObjectId.NewObjectId(),
                    PlayerID = caller,
                    Relationship = new RelationshipDetail
                    {
                        Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        PlayerID = caller,
                        OtherPlayerID = target,
                        RelationshipType = RelationshipType.None,
                        Favorited = ReciprocalStatus.None,
                        Ignored = ReciprocalStatus.None,
                        Muted = ReciprocalStatus.None,
                        VoiceVolume = 100
                    }
                };
                col.Insert(row);
            }

            var status = value ? ReciprocalStatus.Local : ReciprocalStatus.None;
            switch (flag)
            {
                case FriendInfo.Favorited: row.Relationship.Favorited = status; break;
                case FriendInfo.Muted:     row.Relationship.Muted = status; break;
                case FriendInfo.Ignored:   row.Relationship.Ignored = status; break;
            }
            col.Update(row);

            // Realtime: the caller's game re-reads its relationships (favorite star, mute/ignore
            // state) and their website tabs refresh - the same channels a friend request uses.
            // A favorite also changes what the caller may SEE of the target ("Appear Online To:
            // Favorites"), so re-send that one presence to them with the new relationship applied.
            Notifications.FireAndForget(Notifications.SendHubInvocation(caller, "RelationshipsInvalid"), "relationship-flag invalidate");
            Notifications.FireAndForget(Notifications.SendToPlayer(caller, JsonConvert.SerializeObject(new Reponse { Id = "1", Msg = RelationshipView(row.Relationship) })), "relationship-flag changed");
            SiteNotifications.Notify(caller);
            if (flag == FriendInfo.Favorited)
                Notifications.FireAndForget(Notifications.PushPresenceTo(caller, target), "favorite presence refresh");

            // Return in the caller's POV shape (PlayerID = the other player), the same projection
            // GetRelationships uses, so the caller can apply it directly.
            return new RelationshipDetail
            {
                Id = row.Relationship.Id,
                PlayerID = target,
                OtherPlayerID = caller,
                RelationshipType = row.Relationship.RelationshipType,
                Muted = row.Relationship.Muted,
                Ignored = row.Relationship.Ignored,
                Favorited = row.Relationship.Favorited,
                VoiceVolume = row.Relationship.VoiceVolume
            };
        }

        // ==========================
        // COUNT ONLINE FRIENDS - the caller's accepted friends that currently hold a live
        // notification websocket. Replaces the hardcoded int.MaxValue/0 stub.
        // ==========================
        public static int CountOnlineFriends(ulong playerId)
        {
            return GetRelationships(playerId)
                .Where(r => r.RelationshipType == RelationshipType.Friend)
                .Select(r => r.PlayerID)
                .Distinct()
                .Count(fid => CampusLiveSource.Controllers.NotificationsController.WebSockets.ContainsKey(fid));
        }




        // ==========================
        // SEND FRIEND REQUEST
        // ==========================
        public static RelationshipDetail SendFriendRequest(ulong fromPlayer, ulong toPlayer)
        {
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");

            // Blocked either way: nothing is sent and nobody is told; the caller just gets their
            // current (None) relationship back.
            if (IsBlockedEitherWay(fromPlayer, toPlayer))
            {
                Console.WriteLine($"[friends] {fromPlayer} -> {toPlayer}: friend request refused, one has blocked the other");
                var blockedRow = GetOrCreateRow(col, fromPlayer, toPlayer).Relationship;
                return new RelationshipDetail
                {
                    Id = blockedRow.Id, PlayerID = toPlayer, OtherPlayerID = fromPlayer,
                    RelationshipType = blockedRow.RelationshipType, Muted = blockedRow.Muted, Ignored = blockedRow.Ignored,
                    Favorited = blockedRow.Favorited, VoiceVolume = blockedRow.VoiceVolume
                };
            }

            // ---- A -> B ----
            var relA = col.FindOne(x =>
                x.PlayerID == fromPlayer &&
                x.Relationship.OtherPlayerID == toPlayer
            );

            if (relA == null)
            {
                relA = new YourFriendData
                {
                    Id = ObjectId.NewObjectId(),
                    PlayerID = fromPlayer,
                    Relationship = new RelationshipDetail
                    {
                        Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        PlayerID = fromPlayer,
                        OtherPlayerID = toPlayer,
                        RelationshipType = RelationshipType.None,
                        Favorited = ReciprocalStatus.None,
                        Ignored = ReciprocalStatus.None,
                        Muted = ReciprocalStatus.None,
                        VoiceVolume = 100
                    }
                };

                col.Insert(relA);
            }

            // ---- B -> A ----
            var relB = col.FindOne(x =>
                x.PlayerID == toPlayer &&
                x.Relationship.OtherPlayerID == fromPlayer
            );

            if (relB == null)
            {
                relB = new YourFriendData
                {
                    Id = ObjectId.NewObjectId(),
                    PlayerID = toPlayer,
                    Relationship = new RelationshipDetail
                    {
                        Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        PlayerID = toPlayer,
                        OtherPlayerID = fromPlayer,
                        RelationshipType = RelationshipType.None,
                        Favorited = ReciprocalStatus.None,
                        Ignored = ReciprocalStatus.None,
                        Muted = ReciprocalStatus.None,
                        VoiceVolume = 100
                    }
                };

                col.Insert(relB);
            }


            // Update types
            relA.Relationship.RelationshipType = RelationshipType.FriendRequestSent;
            relB.Relationship.RelationshipType = RelationshipType.FriendRequestReceived;

            col.Update(relA);
            col.Update(relB);

            //todo: send ws event to anounce the player that it got a new friend request

            // Websocket pushes. fromPlayer is always the SENDER, toPlayer the RECIPIENT (both
            // callers pass that order).
            //   - sender: their side is now "Sent" (relationship changed -> shows Pending).
            //   - recipient: the incoming request, as BOTH a relationship change (so the friends
            //     UI updates live) AND a message notification "<sender> sent you a friend request".
            //     The message used to be sent to the SENDER, so the recipient's game never got the
            //     heads-up and the request only showed on a later fetch.
            Notifications.SendToPlayer(
                fromPlayer,
                JsonConvert.SerializeObject(CreateResponseFriendRequestSent(toPlayer))
            );

            Notifications.SendToPlayer(
                toPlayer,
                JsonConvert.SerializeObject(CreateResponseFriendRequestReceived(fromPlayer, toPlayer))
            );

            // A stored FriendInvite message for the recipient: the in-game notifications list is
            // /api/messages/v2/get, so a request that only ever went out as a live frame vanished
            // from it on the next open - "friend requests don't show unless you search for the
            // player". One per sender: re-sending replaces rather than stacks. Removed again when
            // the request is accepted, declined or cancelled (RemoveFriendInviteMessage).
            RemoveFriendInviteMessage(toPlayer, fromPlayer);
            var inviteMsg = PlayerDB.AddMessage(toPlayer, fromPlayer, WebsocketEvents.MessageType.FriendInvite, "");
            if (inviteMsg != null)
                Notifications.FireAndForget(Notifications.SendToPlayer(toPlayer, JsonConvert.SerializeObject(WebsocketEvents.CreateMessageResponse(inviteMsg))), "friend-request message");

            // (The extra "FriendInvite" message frame that used to go out here as well showed up
            // as a SECOND notification for the same request - one alert per request now.)

            // Fire the client's OWN hub handlers (proven by the 2023 server) so the request shows
            // in-game live: FriendRequestReceived raises the alert on the recipient, and
            // RelationshipsInvalid makes BOTH sides re-fetch /api/relationships so the incoming
            // request and the sender's "pending" state appear without a relog.
            Notifications.FireAndForget(Notifications.SendHubInvocation(toPlayer, "FriendRequestReceived", (long)fromPlayer), "friend-request alert");
            Notifications.FireAndForget(Notifications.SendHubInvocation(toPlayer, "RelationshipChanged", RelationshipView(relB.Relationship)), "friend-request changed (recipient)");
            Notifications.FireAndForget(Notifications.SendHubInvocation(fromPlayer, "RelationshipChanged", RelationshipView(relA.Relationship)), "friend-request changed (sender)");
            Notifications.FireAndForget(Notifications.SendHubInvocation(toPlayer, "RelationshipsInvalid"), "friend-request invalidate (recipient)");
            Notifications.FireAndForget(Notifications.SendHubInvocation(fromPlayer, "RelationshipsInvalid"), "friend-request invalidate (sender)");
            return relA.Relationship;
        }

        /// <summary>
        /// Befriend with no pending-request step (the in-game handshake). A pending request either
        /// way, or an existing friendship, is simply accepted / re-confirmed; otherwise the pair is
        /// minted as a request from <paramref name="me"/> and accepted in the same call, so both
        /// sides end up Friend with the usual "relationship changed" pushes.
        /// </summary>
        public static RelationshipDetail? AddFriendDirect(ulong me, ulong other)
        {
            if (me == 0 || other == 0 || me == other) return null;
            var accepted = AcceptFriendRequest(other, me);
            if (accepted != null) return accepted;
            SendFriendRequest(me, other);
            return AcceptFriendRequest(me, other);
        }

        public static RelationshipDetail? AcceptFriendRequest(ulong fromPlayer, ulong toPlayer)
        {
            if (fromPlayer == 0 || toPlayer == 0 || fromPlayer == toPlayer) return null;
            // The request is being answered: its notification goes, whichever side held it.
            RemoveFriendInviteMessage(fromPlayer, toPlayer);
            RemoveFriendInviteMessage(toPlayer, fromPlayer);

            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");

            var relA = col.FindOne(x => x.PlayerID == fromPlayer && x.Relationship.OtherPlayerID == toPlayer);
            var relB = col.FindOne(x => x.PlayerID == toPlayer && x.Relationship.OtherPlayerID == fromPlayer);

            static bool IsRequest(YourFriendData? r) =>
                r?.Relationship != null &&
                (r.Relationship.RelationshipType == RelationshipType.FriendRequestSent ||
                 r.Relationship.RelationshipType == RelationshipType.FriendRequestReceived);
            static bool IsFriend(YourFriendData? r) =>
                r?.Relationship != null && r.Relationship.RelationshipType == RelationshipType.Friend;

            // There must be a real, pending request on at least one side (or an already-formed
            // friendship we're just re-confirming). This blocks "accepting" a request nobody sent -
            // otherwise the row-healing below would let anyone force-friend a stranger.
            if (!IsRequest(relA) && !IsRequest(relB) && !IsFriend(relA) && !IsFriend(relB))
                return null;

            // Heal a half-formed pair: if one side's row is missing (e.g. a partial write or an
            // orphan cleanup removed it), recreate it so acceptance ALWAYS ends in a mutual
            // friendship rather than silently doing nothing. Mirrors SendFriendRequest's row shape.
            YourFriendData MakeRow(ulong owner, ulong other) => new YourFriendData
            {
                Id = ObjectId.NewObjectId(),
                PlayerID = owner,
                Relationship = new RelationshipDetail
                {
                    Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    PlayerID = owner,
                    OtherPlayerID = other,
                    RelationshipType = RelationshipType.None,
                    Favorited = ReciprocalStatus.None,
                    Ignored = ReciprocalStatus.None,
                    Muted = ReciprocalStatus.None,
                    VoiceVolume = 100
                }
            };
            if (relA == null) { relA = MakeRow(fromPlayer, toPlayer); col.Insert(relA); }
            if (relB == null) { relB = MakeRow(toPlayer, fromPlayer); col.Insert(relB); }

            // Work out who ORIGINALLY SENT the request (had FriendRequestSent) vs who ACCEPTED it
            // (had FriendRequestReceived), BEFORE overwriting both to Friend. The two callers pass
            // fromPlayer/toPlayer in opposite orders (website: accepter,sender - game: sender,accepter),
            // so we can't trust the arg order - the row types are the source of truth. Without this
            // the "X accepted your request" notice went to whoever happened to be fromPlayer, so the
            // ACCEPTER got told the SENDER accepted (backwards).
            ulong sender, accepter;
            if (relA.Relationship.RelationshipType == RelationshipType.FriendRequestSent) { sender = fromPlayer; accepter = toPlayer; }
            else if (relB.Relationship.RelationshipType == RelationshipType.FriendRequestSent) { sender = toPlayer; accepter = fromPlayer; }
            else { sender = fromPlayer; accepter = toPlayer; } // no pending side (re-confirm) - harmless default

            // Set both sides to Friend
            relA.Relationship.RelationshipType = RelationshipType.Friend;
            relB.Relationship.RelationshipType = RelationshipType.Friend;

            col.Update(relA);
            col.Update(relB);

            // Relationship-changed -> BOTH clients update their friends list live (order-agnostic).
            Notifications.SendToPlayer(fromPlayer,
                JsonConvert.SerializeObject(CreateResponseFriendAccepted(toPlayer, fromPlayer)));

            Notifications.SendToPlayer(toPlayer,
                JsonConvert.SerializeObject(CreateResponseFriendAccepted(fromPlayer, toPlayer)));

            // "<accepter> accepted your friend request" -> ONLY the original sender, naming the
            // accepter. Also persisted as a saved message so it shows in the sender's website inbox,
            // survives, and (via the AddMessage -> SiteNotifications.Notify hook) updates the web live.
            Notifications.SendToPlayer(sender,
               JsonConvert.SerializeObject(CreateResponseFriendAcceptedMsg(accepter)));
            PlayerDB.AddMessage(sender, accepter, WebsocketEvents.MessageType.FriendRequestAccepted, null);

            // Refresh BOTH friends lists live (the client's own RelationshipsInvalid handler).
            Notifications.FireAndForget(Notifications.SendHubInvocation(fromPlayer, "RelationshipsInvalid"), "accept invalidate");
            Notifications.FireAndForget(Notifications.SendHubInvocation(toPlayer, "RelationshipsInvalid"), "accept invalidate");

            // return the accepted version
            return relA.Relationship;
        }


        private static WebsocketEvents.Reponse CreateResponseFriendAcceptedMsg(ulong fromPlayer) => new WebsocketEvents.Reponse
        {
            Id = "2",
            Msg = new
            {
                Id = (ulong)new Random().Next(),
                FromPlayerId = fromPlayer,
                SentTime = DateTime.UtcNow,
                Type = WebsocketEvents.MessageType.FriendRequestAccepted,
                Data = "",
                RoomId = (ulong?)null,
                PlayerEventId = (ulong?)null,
            }
        };

        public static void DeleteFriends(ulong playerId)
        {
            if (playerId == 0) return;
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");
            col.DeleteMany(x => x.PlayerID == playerId);
        }

        // The RelationshipChanged (1) payload is EXACTLY the five fields the client's
        // RelationshipResponse has (reference RelationshipChangedPayload: PlayerID,
        // RelationshipType, Muted, Ignored, Favorited) - the same strict parser that dropped the
        // whole /relationships/v2/get array over the extra Id/OtherPlayerID/VoiceVolume keys.
        // These frames used to carry the raw RelationshipDetail (with its ~1.79-trillion Id), so
        // the live "you have a friend request" frame never parsed and requests sent from the
        // website only showed up on the next relationships fetch. PlayerID = the OTHER player.
        //
        // VoiceVolume IS a field of that response (the client's formatter reads PlayerID,
        // RelationshipType, Muted, Ignored, Favorited and VoiceVolume - a nullable byte, 0-200,
        // null = the default 100). It was dropped along with Id/OtherPlayerID, but it is what the
        // per-player volume slider on a player's card reads, so it has to ride along.
        private static object RelWire(ulong otherPlayer, RelationshipType type, bool favorited = false, bool muted = false, ReciprocalStatus ignored = ReciprocalStatus.None, int? voiceVolume = null) => new
        {
            PlayerID = otherPlayer,
            RelationshipType = (int)type,
            Muted = muted ? 1 : 0,
            Ignored = IgnoredWire(ignored),
            Favorited = favorited ? 1 : 0,
            VoiceVolume = VoiceVolumeWire(voiceVolume)
        };

        /// <summary>The wire VoiceVolume: 0-200 (the client's slider range), null = default.</summary>
        public static int? VoiceVolumeWire(int? v) => v == null ? null : Math.Clamp(v.Value, 0, 200);

        public static Reponse CreateResponseFriendAccepted(ulong playerid, ulong fromplayerid)
            => new Reponse { Id = "1", Msg = RelWire(playerid, RelationshipType.Friend) };

        public static Reponse CreateResponseFriendRemoved(ulong playerid, ulong fromplayerid)
            => new Reponse { Id = "1", Msg = RelWire(playerid, RelationshipType.None) };


        // Sent to the RECIPIENT. Client convention (see GetRelationships): PlayerID = the OTHER
        // player (the sender), OtherPlayerID = the caller (the recipient). OtherPlayerID used to be
        // set to the sender too, which is wrong and can stop the client rendering the request.
        private static WebsocketEvents.Reponse CreateResponseFriendRequestReceived(ulong fromPlayer, ulong toPlayer) => new WebsocketEvents.Reponse
        {
            Id = "1",
            Msg = RelWire(fromPlayer, RelationshipType.FriendRequestReceived)
        };

        private static WebsocketEvents.Reponse CreateResponseFriendRequestMsg(ulong fromPlayer) => new WebsocketEvents.Reponse
        {
            Id = "2",
            Msg = new 
            {
                Id = (ulong)new Random().Next(),
                FromPlayerId = fromPlayer,
                SentTime = DateTime.UtcNow,
                Type = WebsocketEvents.MessageType.FriendInvite,
                Data = "",
                RoomId = (ulong?)null,
                PlayerEventId = (ulong?)null,
            }
        };

        // The relationship as the client's RelationshipChanged handler expects it (the same shape
        // /relationships/v2/get lists): PlayerID = the OTHER player from the receiver's view.
        private static object RelationshipView(RelationshipDetail r) =>
            RelWire(r.OtherPlayerID, r.RelationshipType, r.Favorited != ReciprocalStatus.None, r.Muted != ReciprocalStatus.None, r.Ignored, r.VoiceVolume);

        /// <summary>
        /// What the wire `Ignored` means: "I have blocked this player" - the caller's OWN part of
        /// the pair (Local / Mutual). A Remote (the other player blocked me) is kept for the
        /// server-side gates but is never shown: the blocked player is not told, and reading it
        /// as true made their client draw the block as if THEY had blocked the blocker.
        /// </summary>
        public static bool IsOwnBlock(ReciprocalStatus s) => s == ReciprocalStatus.Local || s == ReciprocalStatus.Mutual;

        /// <summary>
        /// The wire `Ignored` is the client's ReciprocalStatus, from the receiver's point of view:
        ///   0 None    - nobody blocked anybody
        ///   1 Local   - I blocked them        (their card shows Blocked + my Unblock button)
        ///   2 Remote  - they blocked me       (their card shows Blocked, no Unblock - not mine to lift)
        ///   3 Mutual  - both                  (Unblock lifts only MY part; theirs stays -> 2)
        /// Sending a flat 0/1 was the bug: a 1 on both rows drew the block as the blocked
        /// player's own (they got an Unblock button), and a 0 hid it from them entirely.
        /// </summary>
        public static int IgnoredWire(ReciprocalStatus s) => (int)s;

        /// <summary>Drop the stored "X sent you a friend request" notification (and tell the game).</summary>
        public static void RemoveFriendInviteMessage(ulong toPlayer, ulong fromPlayer)
        {
            try
            {
                var full = PlayerDB.GetFullAccount(toPlayer);
                var stale = full?.player_Extra?.Messages?
                    .Where(m => m.Type == WebsocketEvents.MessageType.FriendInvite && m.FromPlayerId == fromPlayer)
                    .Select(m => m.Id).ToList();
                if (stale == null || stale.Count == 0) return;
                foreach (var id in stale)
                {
                    PlayerDB.DeleteMessage(toPlayer, id);
                    Notifications.FireAndForget(Notifications.SendToPlayer(toPlayer, JsonConvert.SerializeObject(WebsocketEvents.CreateMessageDeleted(id))), "friend-request message gone");
                }
                SiteNotifications.Notify(toPlayer);
            }
            catch (Exception ex) { Console.WriteLine($"[friends] invite message cleanup {fromPlayer}->{toPlayer}: {ex.Message}"); }
        }

        public static RelationshipDetail RemoveFriend(ulong playerA, ulong playerB)
        {
            // website extras (nickname / home pin) don't outlive the friendship
            try { SitePrefsDB.Forget(playerA, playerB); SitePrefsDB.Forget(playerB, playerA); } catch { }
            // Decline / cancel / unfriend: any pending "sent you a friend request" notification goes.
            RemoveFriendInviteMessage(playerA, playerB);
            RemoveFriendInviteMessage(playerB, playerA);
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");

            // A -> B
            var relA = col.FindOne(x => x.PlayerID == playerA &&
                                        x.Relationship.OtherPlayerID == playerB);

            // B -> A
            var relB = col.FindOne(x => x.PlayerID == playerB &&
                                        x.Relationship.OtherPlayerID == playerA);

            // If they were never connected, return empty relationship
            if (relA == null && relB == null)
            {
                return new RelationshipDetail
                {
                    PlayerID = playerA,
                    OtherPlayerID = playerB,
                    RelationshipType = RelationshipType.None
                };
            }

            // Cancelling a PENDING request (or declining one) - tell the other side the request is
            // gone, so their notification disappears instead of lingering until they act on it.
            bool wasPending = (relA?.Relationship.RelationshipType == RelationshipType.FriendRequestSent
                            || relA?.Relationship.RelationshipType == RelationshipType.FriendRequestReceived
                            || relB?.Relationship.RelationshipType == RelationshipType.FriendRequestSent
                            || relB?.Relationship.RelationshipType == RelationshipType.FriendRequestReceived);

            // Delete A->B
            if (relA != null)
                col.Delete(relA.Id);

            // Delete B->A
            if (relB != null)
                col.Delete(relB.Id);
            if (wasPending)
            {
                Notifications.FireAndForget(Notifications.SendHubInvocation(playerB, "FriendRequestCancelled", (long)playerA), "friend-request cancelled");
                Notifications.FireAndForget(Notifications.SendHubInvocation(playerB, "RelationshipChanged", RelationshipView(new RelationshipDetail { PlayerID = playerB, OtherPlayerID = playerA, RelationshipType = RelationshipType.None, VoiceVolume = 100 })), "friend-request cancelled (changed)");
                Notifications.FireAndForget(Notifications.SendHubInvocation(playerA, "RelationshipChanged", RelationshipView(new RelationshipDetail { PlayerID = playerA, OtherPlayerID = playerB, RelationshipType = RelationshipType.None, VoiceVolume = 100 })), "friend-request cancelled (self)");
                Console.WriteLine($"[friends] {playerA} cancelled/declined the pending request with {playerB}");
            }

            // Send WS update to player A
            Notifications.SendToPlayer(
                playerA,
                JsonConvert.SerializeObject(
                    CreateResponseFriendRemoved(playerA, playerB)
                )
            );

            // Send WS update to player B
            Notifications.SendToPlayer(
                playerB,
                JsonConvert.SerializeObject(
                    CreateResponseFriendRemoved(playerB, playerA)
                )
            );

            // Refresh BOTH friends lists live (unfriend / decline / cancel).
            Notifications.FireAndForget(Notifications.SendHubInvocation(playerA, "RelationshipsInvalid"), "remove invalidate");
            Notifications.FireAndForget(Notifications.SendHubInvocation(playerB, "RelationshipsInvalid"), "remove invalidate");

            // Return final relationship state
            return new RelationshipDetail
            {
                PlayerID = playerA,
                OtherPlayerID = playerB,
                RelationshipType = RelationshipType.None,
                Favorited = ReciprocalStatus.None,
                Ignored = ReciprocalStatus.None,
                Muted = ReciprocalStatus.None,
                VoiceVolume = 100
            };
        }

        public static RelationshipDetail AddFriend(ulong playerA, ulong playerB)
        {
            var col = PlayerFriendDBFile.GetCollection<YourFriendData>("PlayersFriends");

            // A -> B
            var relA = col.FindOne(x => x.PlayerID == playerA &&
                                       x.Relationship.OtherPlayerID == playerB);

            if (relA == null)
            {
                relA = new YourFriendData
                {
                    Id = ObjectId.NewObjectId(),
                    PlayerID = playerA,
                    Relationship = new RelationshipDetail
                    {
                        Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        PlayerID = playerA,
                        OtherPlayerID = playerB,
                        Favorited = ReciprocalStatus.None,
                        Ignored = ReciprocalStatus.None,
                        Muted = ReciprocalStatus.None,
                        VoiceVolume = 100
                    }
                };
                col.Insert(relA);
            }

            // B -> A
            var relB = col.FindOne(x => x.PlayerID == playerB &&
                                       x.Relationship.OtherPlayerID == playerA);

            if (relB == null)
            {
                relB = new YourFriendData
                {
                    Id = ObjectId.NewObjectId(),
                    PlayerID = playerB,
                    Relationship = new RelationshipDetail
                    {
                        Id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        PlayerID = playerB,
                        OtherPlayerID = playerA,
                        Favorited = ReciprocalStatus.None,
                        Ignored = ReciprocalStatus.None,
                        Muted = ReciprocalStatus.None,
                        VoiceVolume = 100
                    }
                };
                col.Insert(relB);
            }

            // Set both sides to Friend
            relA.Relationship.RelationshipType = RelationshipType.Friend;
            relB.Relationship.RelationshipType = RelationshipType.Friend;

            col.Update(relA);
            col.Update(relB);

            // WebSocket updates
            Notifications.SendToPlayer(
                playerA,
                JsonConvert.SerializeObject(CreateResponseFriendAccepted(playerA, playerB))
            );

            Notifications.SendToPlayer(
                playerB,
                JsonConvert.SerializeObject(CreateResponseFriendAccepted(playerB, playerA))
            );

            return relA.Relationship;
        }




        // {"type":1,"target":"Notification","arguments":["{\"id\":\"2\",\"msg\":{\"id\":16212007930,\"fromPlayerId\":1764227894,\"sentTime\":\"2025-06-23T17:20:53.72092Z\",\"type\":4,\"data\":\"\",\"roomId\":null,\"playerEventId\":null}}"]}

        private static WebsocketEvents.Reponse CreateResponseFriendRequestSent(ulong toPlayer) => new WebsocketEvents.Reponse
        {
            Id = "50",
            Msg = ""
        };
    }
}
