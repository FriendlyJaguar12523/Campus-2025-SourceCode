using Discord;
using LiteDB;
using Campus_live_rewrite.api.server.Classes.db_class;
using static Campus_live_rewrite.api.server.Classes.db_class.EventDBClasses;

namespace Campus_live_rewrite.api.dbs
{
    public class EventDB
    {
        public static LiteDatabase EventDBFile = new LiteDatabase(Environment.CurrentDirectory + "/Data/DBs/Events.db");
        private static ILiteCollection<FullPlayerEvent> Events => EventDBFile.GetCollection<FullPlayerEvent>("events");

        // Event ids are NEVER reused. They used to be MAX(id) + 1, which handed the newest event's
        // id straight back out once it was deleted: delete an event, create another, and the new
        // one wore the old id - and inherited everything still filed under it outside the event
        // (a PlayerEventInvitation message's PlayerEventId, the client's own cache). A counter that
        // only goes up (reference: event_id_seq AUTOINCREMENT), seeded past every existing event.
        private sealed class Counter { public string Id { get; set; } = ""; public ulong Value { get; set; } }
        private static readonly object IdLock = new();
        private static ulong GetNextId()
        {
            lock (IdLock)
            {
                var counters = EventDBFile.GetCollection<Counter>("counters");
                var c = counters.FindById("event_id") ?? new Counter { Id = "event_id", Value = 0 };
                var last = Events.Query().OrderByDescending(x => x.PlayerEventId).FirstOrDefault();
                ulong next = Math.Max(c.Value, last?.PlayerEventId ?? 0) + 1;
                c.Value = next;
                counters.Upsert(c);
                return next;
            }
        }

        public static FullPlayerEvent? GetFullEventById(ulong eventId)
        {
            return Events.FindById(eventId);
        }

        /// <summary>Apply an edit to a stored event and persist it. Null when the event is unknown.</summary>
        public static FullPlayerEvent? Mutate(ulong eventId, Action<FullPlayerEvent> edit)
        {
            var ev = Events.FindById(eventId);
            if (ev == null) return null;
            edit(ev);
            Events.Update(ev);
            return ev;
        }

        public static FullPlayerEvent? SetEventName(ulong eventId, string NewName)
        {
            var ev = Events.FindById(eventId);
            if (ev == null)
                return null;

            ev.Name = NewName;
            Events.Update(ev);

            return ev;
        }

        public static FullPlayerEvent? SetEventImageName(ulong eventId, string NewImageName)
        {
            var ev = Events.FindById(eventId);
            if (ev == null)
                return null;

            ev.ImageName = NewImageName;
            Events.Update(ev);

            return ev;
        }

        public static FullPlayerEvent? SetEventDescription(ulong eventId, string newDescription)
        {
            var ev = Events.FindById(eventId);
            if (ev == null) return null;
            ev.Description = newDescription;
            Events.Update(ev);
            return ev;
        }

        public static FullPlayerEvent? SetEventTime(ulong eventId, DateTime start, DateTime end)
        {
            var ev = Events.FindById(eventId);
            if (ev == null) return null;
            ev.StartTime = start;
            ev.EndTime = end;
            Events.Update(ev);
            return ev;
        }

        public static FullPlayerEvent? SetEventAccessibility(ulong eventId, EventDBClasses.PlayerEventAccessibility accessibility)
        {
            var ev = Events.FindById(eventId);
            if (ev == null) return null;
            ev.Accessibility = accessibility;
            Events.Update(ev);
            return ev;
        }

        public static FullPlayerEvent? SetEventTags(ulong eventId, List<string> tags)
        {
            var ev = Events.FindById(eventId);
            if (ev == null) return null;
            ev.Tags = (tags ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => new EventDBClasses.FullTag { Tag = t.Trim(), Type = EventDBClasses.TagType.Unknown1 })
                .ToList();
            Events.Update(ev);
            return ev;
        }

        public static List<FullPlayerEvent> SearchEventByQuery(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Events.Query()
                    .OrderBy(e => e.StartTime)
                    .ToList();
            }

            query = query.Trim();

            bool isTagSearch =
                query.StartsWith("#") ||
                query.StartsWith("%23");

            if (isTagSearch)
            {
                string tag = query
                    .Replace("%23", "")
                    .TrimStart('#')
                    .ToLower();

                return Events
                    .Find(Query.EQ("Tags.Tag", tag))
                    .OrderBy(e => e.StartTime)
                    .ToList();
            }

            string nameQuery = query.ToLower();

            return Events.Query()
                .Where(e =>
                    e.Name != null &&
                    e.Name.ToLower().Contains(nameQuery))
                .OrderBy(e => e.StartTime)
                .ToList();
        }



        /// <summary>
        /// Events scheduled in a given room. /api/playerevents/v1/room/{RoomId} used to answer
        /// a hardcoded "[]" (marked "todo db"), so a room never showed the events booked in it.
        /// </summary>
        public static List<FullPlayerEvent> GetEventsByCreator(ulong creatorId)
        {
            return Events.Query()
                .Where(e => e.CreatorPlayerId == creatorId)
                .OrderBy(e => e.StartTime)
                .ToList();
        }

        public static List<FullPlayerEvent> GetEventsForRoom(ulong roomId)
        {
            return Events.Query()
                .Where(e => e.RoomId == roomId)
                .OrderBy(e => e.StartTime)
                .ToList();
        }

        /// <summary>
        /// Events happening right now - started, not yet ended. /api/playerevents/v1/searchlive
        /// was another hardcoded "[]", so the "live now" list was always empty however many
        /// events were running.
        /// </summary>
        public static List<FullPlayerEvent> GetLiveEvents()
        {
            var now = DateTime.UtcNow;
            return Events.Query()
                .ToList()
                .Where(e => e.StartTime.ToUniversalTime() <= now && e.EndTime.ToUniversalTime() >= now)
                .OrderBy(e => e.StartTime)
                .ToList();
        }

        /// <summary>
        /// Records a player's RSVP and keeps AttendeeCount in step with it. One response per
        /// player per event - responding again replaces the previous answer.
        /// </summary>
        public static FullPlayerEvent? SetResponse(ulong eventId, ulong playerId, PlayerEventResponseType type)
            => SetResponse(eventId, playerId, type, out _);

        /// <summary>SetResponse, also handing back the player's response row as it now stands (null when removed).</summary>
        public static FullPlayerEvent? SetResponse(ulong eventId, ulong playerId, PlayerEventResponseType type, out FullEventResponses? row)
        {
            row = null;
            var ev = Events.FindById(eventId);
            if (ev == null) return null;

            ev.Responses ??= new List<FullEventResponses>();

            if (type == PlayerEventResponseType.None)
                ev.Responses.RemoveAll(r => r.PlayerId == playerId);
            else
            {
                // Changing your mind UPDATES the row (same id), as the reference's upsert does -
                // an invitee's Pending row becomes their answer rather than a second row.
                row = ev.Responses.FirstOrDefault(r => r.PlayerId == playerId);
                if (row == null)
                {
                    ulong nextId = ev.Responses.Count == 0 ? 1UL : ev.Responses.Max(r => r.PlayerEventResponseId) + 1;
                    row = new FullEventResponses { PlayerEventResponseId = nextId, PlayerEventId = eventId, PlayerId = playerId };
                    ev.Responses.Add(row);
                }
                row.Type = type;
                row.CreatedAt = DateTime.UtcNow;
            }

            // AttendeeCount was never maintained - it sat at 0 no matter how many people
            // responded, so every event advertised itself as empty.
            ev.AttendeeCount = ev.Responses.Count(r => r.Type == PlayerEventResponseType.Yes);
            Events.Update(ev);
            return ev;
        }

        /// <summary>
        /// Whether a player may enter an event's room, and why not when they may not.
        ///
        /// Rules:
        ///   - the CREATOR may join at any time, including before it starts (they have to be
        ///     able to set the room up).
        ///   - anyone INVITED may join at any time. An invite is a response row: the invite
        ///     flow writes Pending, and Yes / Interested / No all mean the player is already
        ///     part of the event, so any response row counts.
        ///   - everyone else may only join once it has actually started, and not after it has
        ///     ended.
        ///
        /// Returns true when entry is allowed; `reason` explains a refusal for the log.
        /// </summary>
        public static bool CanPlayerJoin(FullPlayerEvent ev, ulong playerId, out string reason)
        {
            reason = "";

            if (ev.CreatorPlayerId == playerId)
                return true;

            bool invited = ev.Responses?.Any(r => r.PlayerId == playerId) == true;
            if (invited)
                return true;

            var now = DateTime.UtcNow;

            // Doors open a few minutes early so people can be in the room when it starts.
            if (now < ev.StartTime.ToUniversalTime().AddMinutes(-Campus_live_rewrite.api.server.ConfigCampus.EventEarlyJoinMinutes))
            {
                reason = $"event has not started (starts {ev.StartTime:u}, not invited)";
                return false;
            }

            if (now > ev.EndTime.ToUniversalTime())
            {
                reason = $"event already ended ({ev.EndTime:u})";
                return false;
            }

            return true;
        }

        /// <summary>Marks an event live / not live. Used by roominstance/{id}/inprogress.</summary>
        public static FullPlayerEvent? SetBroadcastingInstance(ulong eventId, ulong roomInstanceId, bool inProgress)
        {
            var ev = Events.FindById(eventId);
            if (ev == null) return null;

            ev.BroadcastingRoomInstanceId = inProgress ? roomInstanceId : 0;
            ev.State = inProgress ? 1 : 0;
            Events.Update(ev);
            return ev;
        }

        /// <summary>The event currently broadcasting to a given room instance, if any.</summary>
        public static FullPlayerEvent? GetEventForRoomInstance(ulong roomInstanceId)
        {
            if (roomInstanceId == 0) return null;
            return Events.Query()
                .Where(e => e.BroadcastingRoomInstanceId == roomInstanceId)
                .FirstOrDefault();
        }

        /// <summary>Removes an event outright. Used by the admin panel for moderation.</summary>
        public static bool DeleteEvent(ulong eventId) => Events.Delete(eventId);

        /// <summary>
        /// Bulk invite: add each invited player as PENDING (an open invitation, not an acceptance -
        /// an invite that landed as Going looked already accepted on the client, and Pending does
        /// not count toward AttendeeCount). Only ever INSERTS: a player who already answered keeps
        /// their answer. Returns the event and the ids that actually gained a row, or null.
        /// </summary>
        public static FullPlayerEvent? InviteToEvent(ulong eventId, IEnumerable<ulong> playerIds, out List<ulong> added)
        {
            added = new List<ulong>();
            var ev = Events.FindById(eventId);
            if (ev == null) return null;
            ev.Responses ??= new List<FullEventResponses>();
            foreach (var pid in playerIds.Distinct())
            {
                if (pid == 0 || ev.Responses.Any(r => r.PlayerId == pid)) continue;
                ulong nextId = ev.Responses.Count == 0 ? 1UL : ev.Responses.Max(r => r.PlayerEventResponseId) + 1;
                ev.Responses.Add(new FullEventResponses { PlayerEventResponseId = nextId, PlayerEventId = eventId, PlayerId = pid, CreatedAt = DateTime.UtcNow, Type = PlayerEventResponseType.Pending });
                added.Add(pid);
            }
            if (added.Count > 0) Events.Update(ev);
            return ev;
        }

        /// <summary>
        /// Who is GOING to an event - the audience for PlayerEventUpdated / PlayerEventDeleted.
        /// Interested, declined and still-Pending players have nothing on screen to update. The
        /// creator is always included (older events have no Going row for them).
        /// </summary>
        public static List<ulong> GoingPlayerIds(FullPlayerEvent ev)
        {
            var ids = (ev.Responses ?? new List<FullEventResponses>())
                .Where(r => r.Type == PlayerEventResponseType.Yes).Select(r => r.PlayerId).ToList();
            if (ev.CreatorPlayerId is ulong creator && !ids.Contains(creator)) ids.Add(creator);
            return ids;
        }

        public static FullPlayerEvent CreateEvent(ulong creatorPlayerId, EventDBClasses.CreateEventRequest request)
        {
            var ev = new FullPlayerEvent
            {
                PlayerEventId = GetNextId(),
                CreatorPlayerId = creatorPlayerId,
                RoomId = request.RoomId,
                SubRoomId = request.SubRoomId,
                ClubId = request.ClubId,
                Name = request.Name,
                Description = request.Description,
                ImageName = request.ImageName,
                Tags = request.Tags?.Select(t => new EventDBClasses.FullTag
                {
                    Tag = t,
                    Type = EventDBClasses.TagType.Unknown1
                }).ToList(),
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                AttendeeCount = 0,
                State = 0,
                Accessibility = request.Accessibility,
                IsMultiInstance = request.IsMultiInstance,
                SupportMultiInstanceRoomChat = request.SupportMultiInstanceRoomChat,
                DefaultBroadcastPermissions = (EventDBClasses.BroadcastPermissionsEnum)request.DefaultBroadcastPermissions,
                CanRequestBroadcastPermissions = (EventDBClasses.BroadcastPermissionsEnum)request.CanRequestBroadcastPermissions,
                Responses = new List<EventDBClasses.FullEventResponses>
                {
                    // The creator is Going from the moment they create it (reference createEvent).
                    new EventDBClasses.FullEventResponses { PlayerEventResponseId = 1, PlayerId = creatorPlayerId, CreatedAt = DateTime.UtcNow, Type = PlayerEventResponseType.Yes }
                }
            };
            ev.Responses[0].PlayerEventId = ev.PlayerEventId;
            ev.AttendeeCount = 1;

            Events.Insert(ev);
            return ev;
        }
    }
}
