using Campus_live_rewrite.api;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using static Campus_live_rewrite.api.server.Moderation_Detail;

namespace Campus_live_rewrite.Controllers
{
    /// <summary>
    /// The admin room editor's People section: who owns a room and who has which role on it, its
    /// bans - and changing any of that without being in the room. Roles are GRANTED directly (the
    /// in-game co-owner / contributor invite-and-accept step is for owners, not staff). Bans and
    /// unbans do what the in-game ones do: the banned player is sent out of the room if they're in
    /// it, instance kick-blocks are cleared on unban, and both go to the moderation log. Anyone in the
    /// room sees role changes live (RoomPermissions.PushRoomUpdateToRoom), like an in-game change.
    /// </summary>
    public class AdminRoomPeopleController : ControllerBase
    {
        private static ContentResult J(object o) => new()
        { Content = JsonConvert.SerializeObject(o), ContentType = "application/json", StatusCode = 200 };

        private static readonly Dictionary<int, string> RoleNames = new()
        {
            [0] = "No role", [1] = "Banned", [10] = "Host", [20] = "Moderator", [25] = "Contributor",
            [30] = "Co-owner", [31] = "Temporary co-owner", [255] = "Owner",
        };
        private static string RoleName(RoomDBClasses.Role_data r) => RoleNames.TryGetValue((int)r, out var n) ? n : r.ToString();
        // What the panel may hand out (Owner goes through Transfer, Banned through Ban).
        private static readonly RoomDBClasses.Role_data[] Grantable =
        {
            RoomDBClasses.Role_data.None, RoomDBClasses.Role_data.Host, RoomDBClasses.Role_data.Moderator,
            RoomDBClasses.Role_data.Contributor, RoomDBClasses.Role_data.CoOwner,
        };

        private static object Who(long id)
        {
            var p = id > 0 ? PlayerDB.GetAccountById((ulong)id) : null;
            return new { accountId = id, username = p?.Username ?? "", displayName = p?.DisplayName ?? "" };
        }

        /// <summary>"@name", "name" or an account id -> the account, or 0 with why.</summary>
        private static ulong Resolve(string? input, out string why)
        {
            why = "";
            string s = (input ?? "").Trim().TrimStart('@');
            if (s.Length == 0) { why = "Enter a player's @username or account id."; return 0; }
            if (ulong.TryParse(s, out var id))
            {
                if (PlayerDB.GetAccountById(id) != null) return id;
                why = $"No account {id}."; return 0;
            }
            ulong byName = PlayerDB.GetAccountIdByUsernameCI(s);
            if (byName == 0) why = $"No player called @{s}.";
            return byName;
        }

        [HttpGet("/admin/api/room/{roomId}/people")]
        public ContentResult People(ulong roomId)
        {
            var (_, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return J(new { success = false, message = $"No room {roomId}." });
            var bans = RoomDB.ActiveRoomBans(room);   // also lifts any that have run out
            room = RoomDB.GetRoom(roomId) ?? room;
            var roles = (room.Roles ?? new())
                .Where(r => r.Role != RoomDBClasses.Role_data.Banned && r.Role != RoomDBClasses.Role_data.Creator
                            && (ulong)r.AccountId != room.CreatorAccountId)
                .OrderByDescending(r => (int)r.Role)
                .Select(r => new
                {
                    who = Who(r.AccountId), role = (int)r.Role, roleName = RoleName(r.Role),
                    pending = r.InvitedRole != 0 && r.Role == RoomDBClasses.Role_data.None,
                    pendingRole = RoleName((RoomDBClasses.Role_data)(byte)r.InvitedRole)
                }).ToList();
            var history = (room.RoomBans ?? new()).Where(b => b.Status != 0).OrderByDescending(b => b.BanStartTime).Take(25)
                .Select(b => new
                {
                    who = Who(b.AccountId), reason = b.Reason ?? "", by = Who(b.BannedByAccountId), start = b.BanStartTime, end = b.BanEndTime,
                    lifted = b.Status == 2 ? "unbanned" : "ran out", unbannedBy = b.UnbannedByAccountId > 0 ? Who(b.UnbannedByAccountId.Value) : null
                }).ToList();
            return J(new
            {
                success = true, roomId, name = room.Name, isDorm = room.IsDorm,
                owner = Who((long)room.CreatorAccountId),
                roles,
                bans = bans.Select(b => new { who = Who(b.AccountId), reason = b.Reason ?? "", by = Who(b.BannedByAccountId), start = b.BanStartTime, end = b.BanEndTime }),
                history,
                grantable = Grantable.Select(g => new { value = (int)g, name = RoleName(g) })
            });
        }

        [HttpPost("/admin/api/room/{roomId}/role")]
        public ContentResult SetRole(ulong roomId)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return J(new { success = false, message = $"No room {roomId}." });
            ulong target = Resolve(Request.Form["player"], out var why);
            if (target == 0) return J(new { success = false, message = why });
            if (target == room.CreatorAccountId) return J(new { success = false, message = "That's the owner - use Transfer ownership to change who owns the room." });
            if (!int.TryParse(Request.Form["role"], out int rv) || !Grantable.Any(g => (int)g == rv))
                return J(new { success = false, message = "Pick a role." });
            var role = (RoomDBClasses.Role_data)(byte)rv;
            if (RoomDB.PlayerIsBannedFrom(room, target) && role != RoomDBClasses.Role_data.None)
                return J(new { success = false, message = "They're banned from this room - unban them first." });
            if (!RoomDB.SetRoomRole(roomId, (long)target, role, (long)admin!.Id))
                return J(new { success = false, message = "Could not change their role." });
            PlayerDB.DeleteRoomRoleMessages(target, roomId);   // any pending in-game invite is superseded
            RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId), target);
            Console.WriteLine($"[admin] {admin.Username} ({admin.Id}) set {target}'s role on room {roomId} to {role}");
            return J(new { success = true, message = role == RoomDBClasses.Role_data.None ? "Role removed." : $"They're now {RoleName(role)}." });
        }

        [HttpPost("/admin/api/room/{roomId}/owner")]
        public ContentResult Transfer(ulong roomId)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            ulong target = Resolve(Request.Form["player"], out var why);
            if (target == 0) return J(new { success = false, message = why });
            bool keepOld = Request.Form["keepOld"].ToString() == "true";
            if (!RoomDB.TransferOwnership(roomId, target, keepOld, (long)admin!.Id, out var err))
                return J(new { success = false, message = err ?? "Could not transfer the room." });
            PlayerDB.DeleteRoomRoleMessages(target, roomId);
            InstanceBlocks.Clear(target, (long)roomId);
            RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId), target);
            return J(new { success = true, message = "Ownership transferred." });
        }

        [HttpPost("/admin/api/room/{roomId}/ban")]
        public ContentResult Ban(ulong roomId)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            var room = RoomDB.GetRoom(roomId);
            if (room == null) return J(new { success = false, message = $"No room {roomId}." });
            ulong target = Resolve(Request.Form["player"], out var why);
            if (target == 0) return J(new { success = false, message = why });
            if (target == room.CreatorAccountId) return J(new { success = false, message = "The owner can't be banned from their own room." });
            string reason = Request.Form["reason"].ToString().Trim();
            long minutes = long.TryParse(Request.Form["minutes"], out var m) && m > 0 ? m : 0;
            var ban = RoomDB.BanFromRoom(roomId, target, admin!.Id, reason, minutes > 0 ? TimeSpan.FromMinutes(minutes) : null);
            if (ban == null) return J(new { success = false, message = "Could not ban that player." });
            PlayerDB.DeleteRoomRoleMessages(target, roomId);
            RoomModeration.RemoveFromRoom(target, roomId, 0, $"You have been banned from {room.Name}.", admin.Id, isBan: false, isHostKick: true);
            ModerationLogDB.Record("roomban", target, admin.Id, roomId, 0, minutes * 60, ReportCategory.Moderator, reason);
            RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId), target);
            Console.WriteLine($"[admin] {admin.Username} ({admin.Id}) banned {target} from room {roomId} " +
                              (minutes > 0 ? $"for {minutes} min" : "until unbanned") + (reason.Length > 0 ? $" - {reason}" : ""));
            return J(new { success = true, message = minutes > 0 ? $"Banned for {minutes} minute(s)." : "Banned until unbanned." });
        }

        [HttpPost("/admin/api/room/{roomId}/unban")]
        public ContentResult Unban(ulong roomId)
        {
            var (admin, error) = AuthHelper.ValidateTokenDev(HttpContext);
            if (error != null) return error;
            if (RoomDB.GetRoom(roomId) == null) return J(new { success = false, message = $"No room {roomId}." });
            ulong target = Resolve(Request.Form["player"], out var why);
            if (target == 0) return J(new { success = false, message = why });
            int blocks = InstanceBlocks.Clear(target, (long)roomId);
            var ban = RoomDB.UnbanFromRoom(roomId, target, admin!.Id);
            if (ban == null && blocks == 0) return J(new { success = false, message = "They aren't banned from this room." });
            ModerationLogDB.Record("roomunban", target, admin.Id, roomId, 0, 0, ReportCategory.Moderator, "");
            RoomPermissions.PushRoomUpdateToRoom(roomId, RoomDB.GetRoom(roomId), target);
            Console.WriteLine($"[admin] {admin.Username} ({admin.Id}) unbanned {target} from room {roomId}");
            return J(new { success = true, message = "Unbanned." });
        }
    }
}
