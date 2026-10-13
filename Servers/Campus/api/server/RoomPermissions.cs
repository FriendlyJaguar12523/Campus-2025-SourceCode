using System;
using System.Collections.Generic;
using System.Linq;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;
using Newtonsoft.Json;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// What each role may do inside a room - the table the client applies when it spawns
    /// (GET /Room_server/photon_access_token -> Permissions), and the per-subroom overrides a
    /// room's managers save on top of it (PUT /rooms/{id}/subrooms/{sub}/permissions).
    /// Ported from the reference rooms.app.ts photonAccessToken / SubRoomPermissionsRequest.
    ///
    /// Entries are addressed by the (Permission, Role) PAIR: an override with a pair the defaults
    /// carry replaces that default in place (so the order never shifts under the client and no
    /// pair is listed twice with two values); one naming a new pair is appended.
    ///
    /// The defaults here are NOT the reference's. The reference hands CAN_USE_ROOM_RESET_BUTTON
    /// and CAN_USE_DELETE_ALL_BUTTON to Role 0 - everyone - which is why any player in the Rec
    /// Center could press Reset. Here those start at Moderator (ConfigCampus.RoomResetMinRole).
    /// </summary>
    public static class RoomPermissions
    {
        public class Entry
        {
            public bool Override { get; set; } = true;
            public string Permission { get; set; } = "";
            public int Role { get; set; }
            public int Type { get; set; }
            /// <summary>A STRING, not a bool - usually "True"/"False", kept verbatim.</summary>
            public string Value { get; set; } = "True";
        }

        public const string ResetButton   = "CAN_USE_ROOM_RESET_BUTTON";
        public const string DeleteAll     = "CAN_USE_DELETE_ALL_BUTTON";
        public const string SaveInventions = "CAN_SAVE_INVENTIONS";
        public const string SpawnInventions = "CAN_SPAWN_INVENTIONS";
        public const string PlayGizmos    = "CAN_USE_PLAY_GIZMOS_TOGGLE";
        public const string MakerPen      = "CAN_USE_MAKER_PEN";
        public const string Invite        = "CAN_INVITE";

        private static Entry P(string permission, int role, bool overrideFlag = true, string value = "True") =>
            new() { Permission = permission, Role = role, Override = overrideFlag, Type = 0, Value = value };

        /// <summary>The default table for one caller in one room.</summary>
        public static List<Entry> Defaults(ulong accountId)
        {
            int everyone = (int)RoomDBClasses.Role_data.None;         // 0
            int mod      = ConfigCampus.RoomResetMinRole;              // 20 = Moderator
            int coOwner  = (int)RoomDBClasses.Role_data.CoOwner;      // 30

            int host        = (int)RoomDBClasses.Role_data.Host;         // 10
            int contributor = (int)RoomDBClasses.Role_data.Contributor;  // 25
            int creator     = (int)RoomDBClasses.Role_data.Creator;      // 255

            // Every role gets an EXPLICIT row for the maker pen and the two destructive buttons.
            // The table used to carry grants only (and the maker pen's one row as Override:false,
            // i.e. "use the built-in default"), so a player whose role had no row - everyone at
            // Role 0, a host, a moderator - fell to the client's own default, and the client's
            // default lets the pen out. "You can pull it out anywhere." Now a role that may not
            // has a row saying False, and a role that may has a row saying True.
            // No developer shortcut: a dev used to get CAN_USE_MAKER_PEN True for EVERY role in every
            // room just for carrying the flag. The pen is a room permission - creator / co-owner /
            // contributor, or whatever the room's managers granted on the subroom - for everyone.
            var t = new List<Entry>();

            foreach (int role in new[] { everyone, host, mod, contributor, coOwner, creator })
            {
                bool staff    = role >= mod;          // moderator and up
                bool builder  = role >= coOwner;      // co-owner and up (contributors build too, below)
                bool canBuild = builder || role == contributor;

                t.Add(P(MakerPen,        role, value: canBuild ? "True" : "False"));
                t.Add(P(ResetButton,     role, value: staff ? "True" : "False"));
                t.Add(P(DeleteAll,       role, value: staff ? "True" : "False"));
                // Everyone: build with inventions and toggle play/build.
                t.Add(P(SaveInventions,  role));
                t.Add(P(SpawnInventions, role));
                t.Add(P(PlayGizmos,      role));
            }

            return t;
        }

        /// <summary>Defaults with a subroom's stored overrides merged over them, by (Permission, Role).</summary>
        public static List<Entry> Merge(List<Entry> defaults, IEnumerable<Entry>? overrides)
        {
            if (overrides == null) return defaults;
            foreach (var o in overrides)
            {
                if (string.IsNullOrWhiteSpace(o.Permission)) continue;
                var stored = new Entry { Override = true, Permission = o.Permission, Role = o.Role, Type = o.Type, Value = o.Value ?? "True" };
                int i = defaults.FindIndex(p => p.Permission == stored.Permission && p.Role == stored.Role);
                if (i == -1) defaults.Add(stored); else defaults[i] = stored;
            }
            return defaults;
        }

        /// <summary>
        /// Apply a PUT body to a subroom's stored overrides. `Override:true` stores the pair's
        /// value; `Override:false` is the client's unchecked box - it DELETES the stored pair so
        /// it falls back to the default. Pairs not sent are left alone. Returns the new table.
        /// </summary>
        public static List<Entry> Apply(List<Entry>? current, IEnumerable<Entry> changes)
        {
            var table = current?.Select(e => new Entry { Override = true, Permission = e.Permission, Role = e.Role, Type = e.Type, Value = e.Value }).ToList()
                        ?? new List<Entry>();
            foreach (var c in changes)
            {
                if (string.IsNullOrWhiteSpace(c.Permission)) continue;
                table.RemoveAll(e => e.Permission == c.Permission && e.Role == c.Role);
                if (c.Override)
                    table.Add(new Entry { Override = true, Permission = c.Permission, Role = c.Role, Type = c.Type, Value = c.Value ?? "True" });
            }
            return table;
        }

        /// <summary>Parse the client's JSON array body. Null when it is not one.</summary>
        public static List<Entry>? ParseBody(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var arr = JsonConvert.DeserializeObject<List<Entry>>(raw);
                return arr?.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Permission)).ToList();
            }
            catch { return null; }
        }

        // ---- "instant" ---------------------------------------------------------------------

        /// <summary>
        /// Tell a player's client its room changed - the SubscriptionUpdateRoom frame (id 15) the
        /// reference pushes after a role change, which makes the client re-read the room and the
        /// permissions it now grants them. Best-effort: the write has already committed.
        /// </summary>
        public static void PushRoomUpdate(ulong playerId, RoomDBClasses.RoomRoot? room)
        {
            if (room == null || playerId == 0) return;
            try
            {
                // The client dispatches this by its STRING name ("RoomUpdate" - reference
                // notification-types.ts); the numeric 15 was an unrelated id and silently dropped,
                // so a permission / role change only showed after re-entering the instance.
                var frame = new api.WebsocketEvents.Reponse
                {
                    Id = "RoomUpdate",
                    Msg = RoomBlobFilter.Strip(room)
                };
                Notifications.FireAndForget(Notifications.SendToPlayer(playerId, JsonConvert.SerializeObject(frame)), "room update push");
            }
            catch (Exception ex) { Console.WriteLine($"[room-perms] push to {playerId} failed: {ex.Message}"); }
        }

        /// <summary>
        /// A role change, pushed to EVERYONE standing in the room plus the affected player(s)
        /// wherever they are (reference: pushRoomUpdateToRoom). The room's Roles are what every
        /// client draws the nametag crown / host / moderator badge from, so telling only the
        /// affected player left everybody else seeing the old badge until they rejoined.
        /// </summary>
        public static void PushRoomUpdateToRoom(ulong roomId, RoomDBClasses.RoomRoot? room, params ulong[] alsoTell)
        {
            if (room == null) return;
            foreach (var pid in PlayersInRoom(roomId).Concat(alsoTell).Where(p => p != 0).Distinct())
                PushRoomUpdate(pid, room);
        }

        /// <summary>Everyone currently standing in any instance of this room.</summary>
        public static List<ulong> PlayersInRoom(ulong roomId)
        {
            var ids = new List<ulong>();
            try
            {
                foreach (var pid in PlayerDB.PlayerDBFile.GetCollection<player_data>("players").FindAll().Select(p => p.playerid))
                {
                    var (online, inst) = HeartbeatDB.PeekPresence(pid);
                    if (online && inst != null && (ulong)inst.roomId == roomId) ids.Add(pid);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[room-perms] presence scan failed: {ex.Message}"); }
            return ids;
        }
    }
}
