using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Security.Principal;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;
using static Campus_live_rewrite.api.dbs.HeartbeatDB;

namespace Campus_live_rewrite.api.server
{
    public class Sessions
    {
        private static readonly Random random = new Random();

        public static RoomInstance? CreateRoom(string roomName, string sceneName = "", bool isPrivate = false)
        {
            //const string uuidPrefix = "Campus_live_2023";
            string formattedRoomName = $"^{roomName}";

            if (!string.IsNullOrEmpty(sceneName))
            {
                Console.WriteLine($"[Sessions]: Finding room: \"{roomName}\" with scene id: \"{sceneName}\"");
            }
            else
            {
                Console.WriteLine($"[Sessions]: Finding room: \"{roomName}\"");
            }

            long gameSessionId = random.Next(0x1000, 0x7fffffff);

            RoomDBClasses.RoomRoot roomData = RoomDB.GetRoomByName(roomName);
            if (roomData == null)
            {
                Console.WriteLine($"[Sessions]: Room \"{roomName}\" not found.");
                return null;
            }

            Console.WriteLine($"[Sessions]: {roomName} found! Joining...");

            var firstSubRoom = roomData.SubRooms?.FirstOrDefault();
            string dataBlob = firstSubRoom?.CurrentSave?.DataBlob ?? string.Empty;
            long instanceNumber = random.Next(1000, 999999999);
            Guid instanceId = Guid.NewGuid();

            // Public rooms used to get the CONSTANT "room" here. Photon groups players by
            // room NAME, so every public instance of every room shared one Photon room:
            // people in completely different rooms saw each other, could interact with
            // each other's worlds, and showed up in each other's player lists.
            //
            // The name must be unique per INSTANCE but identical for everyone in that
            // instance - that is exactly what makes two people meet. It is assigned at the
            // end of this method instead of here, because ApplyRandomPublicSubroom /
            // ApplySceneSettings can still change subRoomId below and the name has to
            // reflect the final subroom.
            string photonSuffix = isPrivate
                ? $"_private-room-{instanceNumber}-{instanceId}"
                : "";

            var session = new RoomInstance
            {
                roomInstanceId = instanceNumber,
                roomId = (long)roomData.RoomId,
                subRoomId = (long)roomData.SubRooms[0].SubRoomId,
                location = roomData.SubRooms[0].UnitySceneId,
                roomInstanceType = 0,
                name = formattedRoomName,
                maxCapacity = CapacityOf(roomData.SubRooms[0]),
                isFull = false,
                isPrivate = isPrivate,
                isInProgress = false,
                photonRoomIdYeee = photonSuffix,
                voiceServerId = null, // this not supported and game may explode when connecting to a server that dont exist
                voiceAuthId = null,
                matchmakingPolicy = 0
            };

            if (!string.IsNullOrEmpty(sceneName))
            {
                ApplySceneSettings(session, roomData, roomName, sceneName, isPrivate);
            }
            else
            {
                ApplyRandomPublicSubroom(session, roomData, roomName, isPrivate);
            }

            // Now that the subroom is final, give a public instance its own Photon room.
            // Room + subroom + instance: two players in the SAME instance produce the same
            // name and meet; different instances never collide.
            if (!isPrivate)
                session.photonRoomIdYeee = $"room-{session.roomId}-{session.subRoomId}-{session.roomInstanceId}";

            return session;
        }

        public static RoomInstance CreateDorm(ulong id, string name)
        {
            Random rng = new Random();

            long RandomNumber = rng.Next(1000, 999999999);

            ulong DormId = PlayerDB.GetDormId(id);

            RoomDBClasses.RoomRoot dorm = RoomDB.GetRoom(DormId);
            if (dorm == null)
            {
                // Was: created the dorm but never re-read it, then dereferenced the
                // still-null `dorm` below at `dorm.SubRooms` - a guaranteed
                // NullReferenceException. MatchmakeToDorm swallows that in its catch,
                // so UpdatePlayerHeartbeat never ran and the player was handed a room
                // instance the server had not registered.
                Console.WriteLine($"[CreateDorm] player {id}: dorm room {DormId} missing - creating a fresh one");

                if (!PlayerDB.CreateDormForPlayerId(id))
                {
                    Console.WriteLine($"[CreateDorm] player {id}: could not create a dorm");
                    return null;
                }

                DormId = PlayerDB.GetDormId(id);
                dorm = RoomDB.GetRoom(DormId);

                if (dorm == null)
                {
                    Console.WriteLine($"[CreateDorm] player {id}: dorm {DormId} still missing after create - giving up");
                    return null;
                }

                Console.WriteLine($"[CreateDorm] player {id}: dorm rebuilt as room {DormId}");
            }
            // REUSE the dorm's live session (reference: playerDormInstance - "the owner leaving and
            // coming back lands in the same session"). Minting a fresh instance id + Photon room on
            // every dorm matchmake meant: the client's presence disagreed with the server after any
            // repeat request (it then re-matchmade, got yet another id, and looped - five new dorm
            // ids in 30 s were seen live), and a guest invited into the dorm sat in a different
            // Photon room than the owner the moment the owner re-entered. Order of preference: the
            // owner's own current instance of this dorm, anyone else's live instance of it (a guest
            // already inside), then the recently-live one from memory.
            RoomInstance? existing = null;
            var own = HeartbeatDB.GetSelfPresence(id).room;
            if (own != null && own.roomId == (long)DormId && !string.IsNullOrEmpty(own.photonRoomIdYeee)) existing = own;
            existing ??= HeartbeatDB.FindInstanceByRoomId((long)DormId);
            existing ??= HeartbeatDB.RecentInstanceOfRoomAny(DormId);
            if (existing != null && !string.IsNullOrEmpty(existing.photonRoomIdYeee))
            {
                // Refresh the scene/subroom off the room record so a dorm edit shows on the next visit.
                existing.subRoomId = (long?)dorm.SubRooms.FirstOrDefault()?.SubRoomId ?? existing.subRoomId;
                existing.name = $"@{name}'s Dorm";
                RoomDB.IncreaseRoomVisit(DormId);
                return existing;
            }

            long instanceNumber = random.Next(1000, 999999999);
            Guid instanceId = Guid.NewGuid();

            string photonSuffix = $"Campus-Dorm-{instanceNumber}-{instanceId}";
            var session = new RoomInstance
            {
                roomInstanceId = RandomNumber,
                roomId = (long)DormId,
                subRoomId = (long?)dorm.SubRooms.FirstOrDefault()?.SubRoomId ?? 0,
                location = "76d98498-60a1-430c-ab76-b54a29b7a163",
                roomInstanceType = 2,   // RoomInstanceType.Dormroom (reference enum: Public 0, Private 1, Dormroom 2)
                name = $"@{name}'s Dorm",
                maxCapacity = CapacityOf(dorm.SubRooms.FirstOrDefault()),
                isFull = false,
                isPrivate = true,
                isInProgress = false,
                photonRoomIdYeee = photonSuffix,
                voiceServerId = null, // this not supported and game may explode when connecting to a server that dont exist
                voiceAuthId = null,
                matchmakingPolicy = 0
            };

            RoomDB.IncreaseRoomVisit(DormId);

            return session;
        }

        // What the room settings show as "Max players" for that subroom; 4 when unset (reference).
        public static int CapacityOf(RoomDBClasses.SubRoom? sub) => sub != null && sub.MaxPlayers > 0 ? sub.MaxPlayers : 4;

        private static void ApplySceneSettings(RoomInstance session, RoomDBClasses.RoomRoot roomData, string roomName, string sceneName, bool isPrivate)
        {
            foreach (var subroom in roomData.SubRooms)
            {
                if (subroom.Name.Equals(sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    session.subRoomId = (long)subroom.SubRoomId;
                    session.location = subroom.UnitySceneId;
                    session.maxCapacity = CapacityOf(subroom);   // the cap is per SUBROOM, not SubRooms[0]'s

                    if (!isPrivate)
                    {
                        //session.photonRoomId = $"{roomName}-hello-room-{sceneName}";
                    }
                    return;
                }
            }

            Console.WriteLine($"Scene \"{sceneName}\" not found in room \"{roomName}\".");
        }

        private static void ApplyRandomPublicSubroom(RoomInstance session, RoomDBClasses.RoomRoot roomData, string roomName, bool isPrivate)
        {
            var publicSubrooms = roomData.SubRooms
                .Where(sub => sub.Accessibility == RoomDBClasses.RoomAccessibility.Public)
                .ToList();

            if (publicSubrooms.Count == 0)
            {
                Console.WriteLine($"No public subrooms found for \"{roomName}\".");
                return;
            }

            var selectedSubroom = publicSubrooms[random.Next(publicSubrooms.Count)];

            session.subRoomId = (long)selectedSubroom.SubRoomId;
            session.location = selectedSubroom.UnitySceneId;
            session.maxCapacity = CapacityOf(selectedSubroom);

            if (!isPrivate)
            {
                //session.photonRoomId = $"{roomName}-hello-room-{selectedSubroom.name}";
            }
        }
    }
}
