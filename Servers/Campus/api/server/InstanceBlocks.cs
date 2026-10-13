using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Campus_live_rewrite.api.dbs;
using Newtonsoft.Json;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Instance blocks: what a room KICK leaves behind. A kicked player is kept out of the one
    /// live instance they were kicked from (ConfigCampus.RoomKickInstanceBlockSeconds, 8 h by
    /// default) and nothing else - they can still enter any other instance of the room, and
    /// the room's ban list is untouched. A room BAN (POST rooms/{id}/bans) is the separate,
    /// owner-lifted thing.
    ///
    /// Before this a kick (roomModKick) was stored as a permanent room ban with the reason
    /// "Kicked by X", so anyone a room moderator kicked from ^RecCenter was banned from the
    /// whole map until someone found and lifted it ("random bans").
    ///
    /// Kept in memory and mirrored to Data/instance_blocks.json so a restart does not lift
    /// them early. An instance is matched by its id or its Photon room name (the same instance
    /// can be handed around under either).
    /// </summary>
    public static class InstanceBlocks
    {
        public class Block
        {
            public ulong PlayerId { get; set; }
            public long RoomInstanceId { get; set; }
            public string PhotonRoom { get; set; } = "";
            public long RoomId { get; set; }
            public string RoomName { get; set; } = "";
            public ulong By { get; set; }
            public string Reason { get; set; } = "";
            public DateTime StartedAt { get; set; }
            public DateTime Until { get; set; }
        }

        private static readonly ConcurrentDictionary<string, Block> _blocks = new();
        private static readonly object _io = new();
        private static bool _loaded;
        private static string PathOnDisk => Path.Combine(Environment.CurrentDirectory, "Data", "instance_blocks.json");

        private static string Key(ulong player, long instanceId, string? photon)
            => $"{player}|{instanceId}|{photon ?? ""}";

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_io)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    if (!File.Exists(PathOnDisk)) return;
                    var list = JsonConvert.DeserializeObject<List<Block>>(File.ReadAllText(PathOnDisk)) ?? new();
                    int live = 0;
                    foreach (var b in list)
                    {
                        if (b.Until <= DateTime.UtcNow) continue;
                        _blocks[Key(b.PlayerId, b.RoomInstanceId, b.PhotonRoom)] = b;
                        live++;
                    }
                    if (live > 0) Console.WriteLine($"[kick] {live} instance block(s) still in force after restart");
                }
                catch (Exception ex) { Console.WriteLine($"[kick] could not read instance blocks: {ex.Message}"); }
            }
        }

        private static void Save()
        {
            lock (_io)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
                    var live = _blocks.Values.Where(b => b.Until > DateTime.UtcNow).ToList();
                    File.WriteAllText(PathOnDisk, JsonConvert.SerializeObject(live, Formatting.Indented));
                }
                catch (Exception ex) { Console.WriteLine($"[kick] could not save instance blocks: {ex.Message}"); }
            }
        }

        /// <summary>Keep a player out of one live instance for a while.</summary>
        public static Block Add(ulong player, HeartbeatDB.RoomInstance inst, ulong by, TimeSpan duration, string reason)
        {
            EnsureLoaded();
            var b = new Block
            {
                PlayerId = player,
                RoomInstanceId = inst.roomInstanceId,
                PhotonRoom = inst.photonRoomIdYeee ?? "",
                RoomId = inst.roomId,
                RoomName = inst.name ?? "",
                By = by,
                Reason = reason ?? "",
                StartedAt = DateTime.UtcNow,
                Until = DateTime.UtcNow + duration
            };
            // One block per (player, instance): a second kick restarts the clock.
            foreach (var k in _blocks.Where(kv => kv.Value.PlayerId == player && Same(kv.Value, inst)).Select(kv => kv.Key).ToList())
                _blocks.TryRemove(k, out _);
            _blocks[Key(player, b.RoomInstanceId, b.PhotonRoom)] = b;
            Save();
            Console.WriteLine($"[kick] {by} blocked {player} from instance {b.RoomInstanceId} of ^{b.RoomName} (room {b.RoomId}) for {duration.TotalHours:0.#} h");
            return b;
        }

        private static bool Same(Block b, HeartbeatDB.RoomInstance inst)
            => (b.RoomInstanceId != 0 && b.RoomInstanceId == inst.roomInstanceId)
               || (b.PhotonRoom.Length > 0 && string.Equals(b.PhotonRoom, inst.photonRoomIdYeee, StringComparison.Ordinal));

        /// <summary>The block keeping this player out of this instance, or null.</summary>
        /// <summary>
        /// Every block in force as (player, Photon room) - for CampusNet, which refuses a Photon join
        /// into a room the player was kicked from. A kicked client reconnects to its old Photon room
        /// by itself, straight through the Photon master, without asking the API - so the
        /// matchmaking check alone never saw it (2026-09-27).
        /// </summary>
        public static List<(ulong player, string photonRoom)> ActivePhotonBlocks()
        {
            EnsureLoaded();
            var now = DateTime.UtcNow;
            var list = new List<(ulong, string)>();
            foreach (var kv in _blocks)
            {
                if (kv.Value.Until <= now) { _blocks.TryRemove(kv.Key, out _); continue; }
                string photon = kv.Value.PhotonRoom;
                if (photon.Length == 0 && kv.Value.RoomInstanceId != 0)
                    photon = HeartbeatDB.FindInstanceById(kv.Value.RoomInstanceId)?.photonRoomIdYeee ?? "";
                if (photon.Length > 0) list.Add((kv.Value.PlayerId, photon));
            }
            return list;
        }

        public static Block? Active(ulong player, HeartbeatDB.RoomInstance? inst)
        {
            if (inst == null || player == 0) return null;
            EnsureLoaded();
            Block? hit = null;
            foreach (var kv in _blocks)
            {
                if (kv.Value.PlayerId != player) continue;
                if (kv.Value.Until <= DateTime.UtcNow) { _blocks.TryRemove(kv.Key, out _); continue; }
                if (Same(kv.Value, inst)) hit = kv.Value;
            }
            return hit;
        }

        public static bool IsBlocked(ulong player, HeartbeatDB.RoomInstance? inst) => Active(player, inst) != null;

        /// <summary>Lift every block a player has on a room's instances (an owner "unban", or admin).</summary>
        public static int Clear(ulong player, long roomId = 0)
        {
            EnsureLoaded();
            var keys = _blocks.Where(kv => kv.Value.PlayerId == player && (roomId == 0 || kv.Value.RoomId == roomId)).Select(kv => kv.Key).ToList();
            foreach (var k in keys) _blocks.TryRemove(k, out _);
            if (keys.Count > 0) Save();
            return keys.Count;
        }

        /// <summary>Every block in force (admin view).</summary>
        public static List<Block> All()
        {
            EnsureLoaded();
            return _blocks.Values.Where(b => b.Until > DateTime.UtcNow).OrderByDescending(b => b.StartedAt).ToList();
        }
    }
}
