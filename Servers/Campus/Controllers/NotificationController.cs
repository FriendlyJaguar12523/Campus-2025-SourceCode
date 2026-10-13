using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using Newtonsoft.Json;

namespace CampusLiveSource.Controllers
{
    [ApiController]
    [Route("Notifications")]
    public class NotificationsController : Controller
    {
        public static ConcurrentDictionary<ulong, WebSocket> WebSockets { get; } = new();

        // Set from Program.cs to ApplicationStopping. Hub receive loops end on it, so a restart
        // doesn't sit out the full 30 s HostOptions.ShutdownTimeout waiting for every connected
        // game to hang up (each deploy was ~30 s of "connection refused" behind Caddy).
        public static CancellationToken Stopping { get; set; }

        // viewer -> the OTHER players this connection asked for realtime updates about, via the
        // hub's SubscribeToPlayers / UnsubscribeFrom invocations. The 2025 client subscribes to
        // everyone it is displaying - friends list, the in-room player list, party members -
        // and expects PresenceUpdate / AccountUpdate frames for them. This used to be acked and
        // forgotten, so only FRIENDS ever received a presence push and nothing else updated live.
        public static ConcurrentDictionary<ulong, HashSet<ulong>> Subscriptions { get; } = new();

        /// <summary>Every connected viewer that subscribed to <paramref name="subjectId"/>.</summary>
        public static List<ulong> SubscribersOf(ulong subjectId)
        {
            var result = new List<ulong>();
            foreach (var kv in Subscriptions)
            {
                lock (kv.Value) { if (kv.Value.Contains(subjectId)) result.Add(kv.Key); }
            }
            return result;
        }

        // SignalR gives no schema for the argument, so accept every plausible spelling:
        // [{PlayerIds:[1,2]}] / [{playerIds:[..]}] (what this client sends), [[1,2]], and 1,2.
        // null = unreadable (never treated as "nobody").
        private static List<ulong>? ParsePlayerIds(JsonElement root)
        {
            if (!root.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Array) return null;
            var ids = new List<ulong>();
            bool any = false;
            foreach (var arg in args.EnumerateArray())
            {
                if (arg.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in arg.EnumerateObject())
                    {
                        if (!prop.Name.Equals("PlayerIds", StringComparison.OrdinalIgnoreCase)) continue;
                        if (prop.Value.ValueKind != JsonValueKind.Array) return null;
                        any = true;
                        foreach (var v in prop.Value.EnumerateArray())
                            if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var id)) ids.Add(id);
                    }
                }
                else if (arg.ValueKind == JsonValueKind.Array)
                {
                    any = true;
                    foreach (var v in arg.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var id)) ids.Add(id);
                }
                else if (arg.ValueKind == JsonValueKind.Number && arg.TryGetUInt64(out var single))
                {
                    any = true; ids.Add(single);
                }
            }
            return any ? ids : null;
        }

        // ===========================================================
        // Negotiation endpoint
        // ===========================================================
        [HttpPost("hub/v1/negotiate")]
        public ContentResult Negotiate()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            var response = new
            {
                negotiateVersion = 0,
                connectionId = account.Id.ToString(),
                availableTransports = new[]
                {
                    new {
                        transport = "WebSockets",
                        transferFormats = new[] { "Text", "Binary" }
                    }
                }
            };

            return JsonResult(response);
        }

        // ===========================================================
        // Notification preferences/config
        // ===========================================================
        [HttpGet("preferences")]
        public IActionResult GetPreferences()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return Ok(new
            {
                mutedCategories = Array.Empty<object>()
            });
        }

        // The caller's CRM (campaign/messaging) config, fetched at every login. An OBJECT, not
        // an envelope and not a list: the client parses it as a config object, and the `[]`
        // this answered before failed that parse ("Malformed Response: '[]'" on every boot -
        // Player.log). Empty rather than invented, like the reference: nothing here runs
        // campaigns, and made-up keys would switch on messaging paths with nothing behind them.
        [HttpGet("crm/me/config/v3")]
        public ContentResult GetCRMConfig()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return new ContentResult { Content = "{}", ContentType = "application/json", StatusCode = 200 };
        }

        [HttpGet("accounts/{id}/receives/GameplayInvites")]
        public ContentResult GetGameplayInvites(ulong id)
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            return JsonResult(true);
        }

        [HttpGet("config/categories")]
        public IActionResult GetCategories()
        {
            var (account, error) = AuthHelper.ValidateToken(HttpContext);
            if (error != null)
                return error;

            string path = Path.Combine(Environment.CurrentDirectory, "Data", "APIS", "NotiConfigCategories.json");

            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine("Warning: Notification config json file not found, returning []");
                return Ok(new
                {
                    results = Array.Empty<object>(),
                    totalResults = 0
                });
            }

            return PhysicalFile(path, "application/json");
        }

        // ===========================================================
        // WebSocket handler
        // ===========================================================
        [HttpGet("hub/v1")]
        public async Task HandleHub([FromQuery] string id)
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!ulong.TryParse(id, out var playerId))
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            Console.WriteLine($"[WebSocket] Player {playerId} connected.");

            await HandleConnectionAsync(playerId, socket);
        }

        private static async Task HandleConnectionAsync(ulong playerId, WebSocket socket)
        {
            using var pingCts = new CancellationTokenSource();

            try
            {
                await CloseExistingConnection(playerId);

                WebSockets[playerId] = socket;

                await SendHandshakeAsync(socket);

                // Realtime presence: this player just came ONLINE - tell their online friends so
                // their friends lists flip to online instantly instead of on the next poll.
                Campus_live_rewrite.api.server.Notifications.FireAndForget(
                    Campus_live_rewrite.api.server.Notifications.PushPresenceToFriends(playerId),
                    $"presence-online {playerId}");

                // Start ping loop
                _ = Task.Run(() => PingLoopAsync(socket, pingCts.Token));

                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, Stopping);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count).TrimEnd('\x1e');
                    // keep-alive pings ({"type":6}) are most of the traffic - ~49,000 lines a day
                    if (!message.StartsWith("{\"type\":6}"))
                        Console.WriteLine($"Player ({playerId}) sent {message}");

                    await HandleClientMessageAsync(playerId, socket, message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error:{playerId}] {ex.Message}");
            }
            finally
            {
                pingCts.Cancel();

                // Only tear down if THIS socket is still the player's registered one. When the game
                // reconnects, the new connection registers itself first and this (old) handler ends
                // afterwards - an unconditional TryRemove(playerId) here deleted the NEW socket, so
                // a connected player read as offline: no pushes, friends saw them offline, and the
                // voice lookup answered "nowhere".
                bool wasCurrent = WebSockets.TryRemove(new KeyValuePair<ulong, WebSocket>(playerId, socket));
                if (wasCurrent)
                {
                    Subscriptions.TryRemove(playerId, out _);
                    Campus_live_rewrite.api.dbs.HeartbeatDB.StampLastSeen(playerId);   // "last online" = when the socket closed

                    // Realtime presence: the socket is gone, so this player now reads OFFLINE - tell
                    // their online friends so their friends lists flip to offline instantly (the socket
                    // is the ground truth GetPresenceForList gates on).
                    Campus_live_rewrite.api.server.Notifications.FireAndForget(
                        Campus_live_rewrite.api.server.Notifications.PushPresenceToFriends(playerId),
                        $"presence-offline {playerId}");
                }

                // The client may already be gone (no close handshake) - that's a normal hang-up,
                // not an error, so it must not escape as an unhandled request exception.
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing connection", closeCts.Token);
                    }
                    catch { }
                }

                Console.WriteLine($"[WebSocket] Player {playerId} disconnected.");
            }
        }

        private static async Task HandleClientMessageAsync(ulong playerId, WebSocket socket, string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetInt32() != 1) return;

                string target = root.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
                string? invocationId = root.TryGetProperty("invocationId", out var inv) ? inv.GetString() : null;

                async Task Complete(object? result, string? error = null)
                {
                    if (string.IsNullOrEmpty(invocationId)) return;
                    if (error != null) await SendJsonAsync(socket, new { type = 3, invocationId, error });
                    else await SendJsonAsync(socket, new { type = 3, invocationId, result });
                }

                switch (target)
                {
                    case "SubscribeToPlayers":
                    {
                        var ids = ParsePlayerIds(root);
                        if (ids == null)
                        {
                            Console.WriteLine($"[hub] {playerId}: unreadable SubscribeToPlayers argument - ignored");
                            await Complete(null);
                            break;
                        }
                        var set = Subscriptions.GetOrAdd(playerId, _ => new HashSet<ulong>());
                        var added = new List<ulong>();
                        var resend = new List<ulong>();
                        lock (set)
                        {
                            foreach (var id in ids)
                            {
                                if (id == playerId) continue;
                                if (set.Add(id)) added.Add(id);
                                // Already subscribed, but their presence changed in the last minute:
                                // send it again in case an older answer overtook the change (the
                                // client re-subscribes whenever its in-room player list changes).
                                else if (Campus_live_rewrite.api.dbs.HeartbeatDB.PresenceChangedWithin(id, TimeSpan.FromMinutes(1))) resend.Add(id);
                            }
                        }
                        await Complete(null);
                        // Send the current state of every NEW subject straight away, so a list renders
                        // live from the moment it opens instead of after that player's next change.
                        foreach (var id in added.Concat(resend))
                            await Campus_live_rewrite.api.server.Notifications.PushPresenceTo(playerId, id);
                        Console.WriteLine($"[hub] {playerId}: subscribed to {ids.Count} player(s), {added.Count} new");
                        break;
                    }
                    case "UnsubscribeFrom":
                    case "UnsubscribeFromPlayers":
                    {
                        var ids = ParsePlayerIds(root);
                        if (ids != null && Subscriptions.TryGetValue(playerId, out var set))
                            lock (set) { foreach (var id in ids) set.Remove(id); }
                        await Complete(null);
                        break;
                    }
                    case "GetSubscriptions":
                    {
                        ulong[] current = Array.Empty<ulong>();
                        if (Subscriptions.TryGetValue(playerId, out var set)) lock (set) current = set.ToArray();
                        await Complete(current);
                        break;
                    }
                    default:
                        // A hub method we don't implement is a client expectation we've missed.
                        Console.WriteLine($"[hub] {playerId}: unknown invocation target '{target}'");
                        await Complete(null, $"Unknown method '{target}'");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ParseError] {ex.Message}");
            }
        }

        private static async Task SendHandshakeAsync(WebSocket socket)
        {
            var handshake = new { protocol = "json", version = 1 };
            await SendJsonAsync(socket, handshake);
        }

        private static async Task PingLoopAsync(WebSocket socket, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var ping = new { type = 6 };
                    await SendJsonAsync(socket, ping);
                    await Task.Delay(10000, token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[PingError] {ex.Message}");
            }
        }

        private static async Task CloseExistingConnection(ulong playerId)
        {
            if (WebSockets.TryRemove(playerId, out var oldSocket) && oldSocket.State == WebSocketState.Open)
            {
                // Send-only close with a short timeout: a full CloseAsync waits for the old (often
                // dead) client to answer, with no timeout, while the NEW connection waits here
                // unregistered. The old handler's own receive loop ends on the close.
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await oldSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Replaced by new connection", cts.Token);
                }
                catch { oldSocket.Abort(); }
                Console.WriteLine($"[WebSocket] Closed existing connection for {playerId}");
            }
        }

        private static async Task SendJsonAsync(WebSocket socket, object obj)
        {
            var json = JsonConvert.SerializeObject(obj) + "\x1e";
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static ContentResult JsonResult(object data) => new()
        {
            Content = JsonConvert.SerializeObject(data),
            ContentType = "application/json",
            StatusCode = 200
        };
    }
}
