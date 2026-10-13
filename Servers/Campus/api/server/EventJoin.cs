using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;
using static Campus_live_rewrite.api.dbs.HeartbeatDB;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// JOIN AN EVENT: the one place that decides which room instance a player lands in. The game's
    /// POST matchmake/event/{id} and the website's Join button both use it, so they can never disagree.
    ///
    /// Entry rules (EventDB.CanPlayerJoin): the creator any time, an invited player any time, everyone
    /// else only once it has started and before it ended. Everyone attending belongs in the SAME instance
    /// (the one the event is broadcasting to); a full main instance overflows into another instance of
    /// the event; the first one in opens the instance the rest land in. On success the player's heartbeat
    /// is pointed at that instance, so their client walks into it.
    /// </summary>
    public static class EventJoin
    {
        public sealed class Outcome
        {
            public RoomInstance? Session;
            public MatchmakingErrorCode Code = MatchmakingErrorCode.Success;
            public string Why = "";
            public EventDBClasses.FullPlayerEvent? Event;
            public string RoomName = "";
            public string SubRoomName = "";
        }

        public static Outcome Resolve(ulong eventId, ulong accountId)
        {
            Outcome Refuse(MatchmakingErrorCode code, string why, EventDBClasses.FullPlayerEvent? found = null)
            {
                Console.WriteLine($"[matchmake/event] REFUSED {accountId} -> event {eventId}: {why} (code {(int)code} {code})");
                return new Outcome { Code = code, Why = why, Event = found };
            }

            if (PlayerDB.HasActiveModerationBlock(accountId))
                return Refuse(MatchmakingErrorCode.Banned, "account is banned or timed out");

            var ev = EventDB.GetFullEventById(eventId);
            if (ev == null)
                return Refuse(MatchmakingErrorCode.NoSuchGame, "no such event");

            if (!EventDB.CanPlayerJoin(ev, accountId, out string reason))
                return Refuse(MatchmakingErrorCode.EventNotStarted, reason, ev);

            var room = RoomDB.GetRoom(ev.RoomId);
            if (room == null)
                return Refuse(MatchmakingErrorCode.NoSuchRoom, $"event room {ev.RoomId} no longer exists", ev);

            // The event's room still applies its own rules - a private room the player has no
            // role on is not opened up just because an event points at it.
            if (!RoomDB.CanPlayerEnterRoom(room, accountId))
                return Refuse(MatchmakingErrorCode.RoomIsPrivate, $"cannot enter room {ev.RoomId} (private / banned)", ev);

            string roomName = room.Name ?? "";
            // No subroom on the event -> the room's Home (else first) subroom, not "Unknown Room".
            string subroomname = ev.SubRoomId.HasValue && ev.SubRoomId.Value != 0
                ? RoomDB.get_subroom_from_id(ev.RoomId, ev.SubRoomId.Value)
                : (room.SubRooms?.FirstOrDefault(sr => string.Equals(sr.Name, "Home", StringComparison.OrdinalIgnoreCase)) ?? room.SubRooms?.FirstOrDefault())?.Name ?? "";

            RoomInstance? session = null;
            if (ev.BroadcastingRoomInstanceId != 0)
                session = HeartbeatDB.FindInstanceById((long)ev.BroadcastingRoomInstanceId);
            if (session != null && InstanceBlocks.IsBlocked(accountId, session))
                return Refuse(MatchmakingErrorCode.BlockedFromRoom, $"kicked from the event's instance {session.roomInstanceId}", ev);

            // The event's instance is FULL: overflow into another instance of the event instead of
            // cramming past the cap (the overflow instances receive the broadcast over Photon Chat).
            if (session != null && HeartbeatDB.InstanceIsFull(session)
                && HeartbeatDB.GetPlayerHeartbeat(accountId)?.roomInstance?.roomInstanceId != session.roomInstanceId)
            {
                var main = session;
                var overflow = HeartbeatDB.LiveInstancesOfRoom(ev.RoomId)
                    .Where(t => t.inst.roomInstanceId != main.roomInstanceId && t.inst.eventId == (long)eventId
                             && !t.inst.isPrivate && !HeartbeatDB.InstanceIsFull(t.inst) && !InstanceBlocks.IsBlocked(accountId, t.inst))
                    .OrderByDescending(t => t.players.Count)
                    .Select(t => t.inst)
                    .FirstOrDefault()
                    ?? Sessions.CreateRoom(roomName, subroomname);
                if (overflow != null)
                {
                    Console.WriteLine($"[matchmake/event] event {eventId}: main instance {main.roomInstanceId} is full - {accountId} goes to overflow instance {overflow.roomInstanceId} (sees the broadcast)");
                    session = overflow;
                }
            }

            if (session == null)
            {
                session = HeartbeatDB.FindJoinableInstance(ev.RoomId, accountId)
                          ?? Sessions.CreateRoom(roomName, subroomname);

                if (session != null)
                {
                    // First one in defines the instance the rest will land in.
                    EventDB.SetBroadcastingInstance(eventId, (ulong)session.roomInstanceId, true);
                    Console.WriteLine($"[matchmake/event] event {eventId} is now live in instance {session.roomInstanceId}");
                }
            }

            if (session == null)
                return Refuse(MatchmakingErrorCode.InsufficientSpace, "could not create a room instance", ev);

            // Mark the instance as this event's so the client shows the "This Event" tab.
            session.eventId = (long)eventId;
            // ClubId is nullable: `null != 0` is TRUE, and the cast then threw "Nullable object must
            // have a value" - every join of a non-club event 500'd after the instance was created.
            if (ev.ClubId.HasValue && ev.ClubId.Value != 0) session.clubId = (long)ev.ClubId.Value;

            RoomDB.IncreaseRoomVisit(ev.RoomId);
            PlayerDB.SetRecentRooms(accountId, ev.RoomId);
            HeartbeatDB.UpdatePlayerHeartbeat(accountId, session);

            Console.WriteLine($"[matchmake/event] {accountId} joined event {eventId} '{ev.Name}' -> ^{roomName}.{subroomname} instance {session.roomInstanceId}");
            return new Outcome { Session = session, Event = ev, RoomName = roomName, SubRoomName = subroomname };
        }
    }
}
