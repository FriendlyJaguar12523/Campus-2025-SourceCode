using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Campus_live_rewrite.api.dbs;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Campus_live_rewrite.api.server
{
    /// <summary>
    /// Shared pieces of the in-room moderation routes (warn / kick / room ban / instant kick).
    ///
    /// Fields() reads a request's parameters whatever way the client sent them - form body,
    /// query string or a JSON object - into one case-insensitive map, because the moderation
    /// calls are form posts on some paths and JSON on others (instantKick) and the exact key
    /// spellings of the 2025 build are not all pinned. Every caller logs Dump() so a new
    /// spelling shows up in the journal instead of silently reading as "no player".
    /// </summary>
    public static class RoomModeration
    {
        public static Dictionary<string, string> Fields(HttpRequest req)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var kv in req.Query) d[kv.Key] = string.Join(",", kv.Value.ToArray());
            }
            catch { }
            try
            {
                if (req.HasFormContentType)
                    foreach (var kv in req.Form) d[kv.Key] = string.Join(",", kv.Value.ToArray());
            }
            catch { }
            try
            {
                string ct = req.ContentType ?? "";
                if (!req.HasFormContentType && (req.ContentLength ?? 0) > 0 || ct.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    req.EnableBuffering();
                    req.Body.Position = 0;
                    using var sr = new StreamReader(req.Body, leaveOpen: true);
                    string raw = sr.ReadToEndAsync().GetAwaiter().GetResult();
                    req.Body.Position = 0;
                    raw = raw.Trim();
                    if (raw.StartsWith("{"))
                    {
                        foreach (var p in JObject.Parse(raw).Properties())
                        {
                            if (p.Value is JArray arr) d[p.Name] = string.Join(",", arr.Select(x => x.ToString()));
                            else if (p.Value.Type == JTokenType.Null) d[p.Name] = "";
                            else d[p.Name] = p.Value.ToString();
                        }
                    }
                    else if (raw.Length > 0 && raw.Contains('=') && !raw.Contains('{'))
                    {
                        // A form body sent without the form content type.
                        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = pair.IndexOf('=');
                            string k = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                            string v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                            d[k] = v;
                        }
                    }
                }
            }
            catch { }
            return d;
        }

        public static string Dump(Dictionary<string, string> f) =>
            f.Count == 0 ? "(no fields)" : string.Join(" ", f.Select(kv => $"{kv.Key}={kv.Value}"));

        public static string? Str(Dictionary<string, string> f, params string[] names)
        {
            foreach (var n in names)
                if (f.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return null;
        }

        public static long Long(Dictionary<string, string> f, params string[] names)
        {
            foreach (var n in names)
            {
                if (!f.TryGetValue(n, out var v) || string.IsNullOrWhiteSpace(v)) continue;
                string first = v.Split(',')[0].Trim();
                if (long.TryParse(first, out var l)) return l;
                if (double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dbl)) return (long)dbl;
            }
            return 0;
        }

        public static ulong ULong(Dictionary<string, string> f, params string[] names)
        {
            long l = Long(f, names);
            return l > 0 ? (ulong)l : 0;
        }

        public static List<ulong> ULongs(Dictionary<string, string> f, params string[] names)
        {
            var list = new List<ulong>();
            foreach (var n in names)
            {
                if (!f.TryGetValue(n, out var v) || string.IsNullOrWhiteSpace(v)) continue;
                foreach (var part in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (ulong.TryParse(part.Trim().Trim('[', ']', '"'), out var id) && id > 0 && !list.Contains(id)) list.Add(id);
                if (list.Count > 0) break;
            }
            return list;
        }

        public static Moderation_Detail.ReportCategory Category(Dictionary<string, string> f, params string[] names)
        {
            string raw = Str(f, names) ?? "";
            if (int.TryParse(raw, out var n)) return (Moderation_Detail.ReportCategory)n;
            if (Enum.TryParse<Moderation_Detail.ReportCategory>(raw, true, out var c)) return c;
            return Moderation_Detail.ReportCategory.Moderator;
        }

        /// <summary>
        /// Take a player out of the instance they are in: the ModerationKick frame (id 22) their
        /// client leaves on, and their presence cleared so the heartbeat resync does not put them
        /// straight back. Nothing account-wide is stored - this is a room action, not a timeout.
        /// </summary>
        public static void RemoveFromRoom(ulong playerId, ulong roomId, long gameSessionId, string message, ulong by, bool isBan, bool isHostKick)
        {
            var block = new Moderation_Detail.ModerationBlockDetails
            {
                ReportCategory = Moderation_Detail.ReportCategory.Moderator,
                Duration = 0,
                GameSessionId = gameSessionId,
                IsHostKick = isHostKick,
                Message = message,
                PlayerIdReporter = by,
                IsBan = isBan,
                IsWarning = false,
                TimeoutStartedAt = DateTime.UtcNow
            };
            try { Notifications.SendToPlayer(playerId, JsonConvert.SerializeObject(WebsocketEvents.CreateBan(block))); } catch { }
            SendHome(playerId, roomId, gameSessionId);
            Console.WriteLine($"[moderation] {by} removed {playerId} from room {roomId} (instance {gameSessionId}){(isBan ? " [ban]" : "")}");
        }

        /// <summary>
        /// Point the player's presence at their own dorm if they are in that room (roomId /
        /// gameSessionId 0 = whatever room they are in). The client leaves the room on any
        /// moderation frame and then goes where its presence says - left on the old instance, it
        /// reconnected straight back into the room it had just been warned / kicked out of.
        /// </summary>
        public static void SendHome(ulong playerId, ulong roomId, long gameSessionId)
        {
            try
            {
                var hb = HeartbeatDB.GetPlayerHeartbeat(playerId);
                bool inThatRoom = hb?.roomInstance != null
                                  && (roomId == 0 || (ulong)hb.roomInstance.roomId == roomId)
                                  && (gameSessionId == 0 || hb.roomInstance.roomInstanceId == gameSessionId);
                // Rewrite their presence to their own dorm rather than to "nowhere" (reference): a
                // player who is online with no instance breaks the friend cards of everyone who
                // has them on their list, and the kicked client lands in its dorm anyway.
                if (inThatRoom)
                {
                    var acc = PlayerDB.GetAccountById(playerId);
                    HeartbeatDB.RoomInstance? dorm = null;
                    try { dorm = Sessions.CreateDorm(playerId, acc?.Username ?? "player"); } catch { }
                    HeartbeatDB.UpdatePlayerHeartbeat(playerId, dorm, online: true);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[moderation] could not clear presence for {playerId}: {ex.Message}"); }
        }
    }
}
