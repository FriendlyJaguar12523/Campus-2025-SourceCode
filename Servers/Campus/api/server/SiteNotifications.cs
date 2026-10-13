using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Campus_live_rewrite.api.dbs;

namespace Campus_live_rewrite.api.server
{
    // A website-only realtime push channel, completely separate from the game's
    // NotificationsController hub. That hub is keyed by player id and calls
    // CloseExistingConnection, so if the browser connected there with the same id it would
    // disconnect the player's GAME socket. This keeps its own socket set and supports several
    // browser tabs per player.
    //
    // Messages (server -> browser), all JSON with a "type":
    //   refresh                       - re-fetch the inbox / anything social on screen (the original)
    //   profile  {id}                 - the profile with that account id changed (presence, room,
    //                                   name/bio/avatar, friend counts): re-fetch it
    //   spotify  {id, now_playing}    - what that account is listening to changed, already filtered
    //                                   by their "show to" choice for THIS viewer
    //   ping                          - keep-alive, ignored
    // The browser sends one thing: {"type":"watch","profile":"<account id | username>"} - the
    // profile its page is showing (its own on Settings). That is what the profile/spotify pushes
    // are addressed to; a tab that never sends it only gets refresh.
    public static class SiteNotifications
    {
        private sealed class Conn
        {
            public ulong PlayerId;
            public WebSocket Socket = null!;
            public ulong Watching;   // account id of the profile this tab shows (0 = none)
        }

        private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Guid, Conn>> Sockets = new();
        // profile account id -> the tabs looking at it
        private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Guid, Conn>> Watchers = new();
        private static readonly byte[] RefreshMsg = Encoding.UTF8.GetBytes("{\"type\":\"refresh\"}");
        private static readonly byte[] PingMsg = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");

        // Holds one browser tab's socket open until it closes. Sends an app-level ping every 25s
        // so the reverse proxy doesn't drop the idle connection (the client ignores "ping").
        public static async Task HandleAsync(ulong playerId, WebSocket socket)
        {
            var connId = Guid.NewGuid();
            var conn = new Conn { PlayerId = playerId, Socket = socket };
            var bag = Sockets.GetOrAdd(playerId, _ => new ConcurrentDictionary<Guid, Conn>());
            bag[connId] = conn;
            EnsureSpotifyLoop();

            using var pingCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!pingCts.IsCancellationRequested && socket.State == WebSocketState.Open)
                    {
                        await Task.Delay(25000, pingCts.Token);
                        try { await socket.SendAsync(PingMsg, WebSocketMessageType.Text, true, CancellationToken.None); }
                        catch { break; }
                    }
                }
                catch { }
            });

            try
            {
                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open)
                {
                    // Ends on server shutdown too, so a restart isn't held up by open browser tabs.
                    var result = await socket.ReceiveAsync(buffer, CampusLiveSource.Controllers.NotificationsController.Stopping);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text || result.Count == 0) continue;
                    try { HandleClientMessage(connId, conn, Encoding.UTF8.GetString(buffer, 0, result.Count)); }
                    catch { /* a malformed message from a browser is ignored */ }
                }
            }
            catch { }
            finally
            {
                pingCts.Cancel();
                bag.TryRemove(connId, out _);
                if (bag.IsEmpty) Sockets.TryRemove(playerId, out _);
                Unwatch(connId, conn);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // Send-only, 2 s cap: a full CloseAsync waits (forever) for a tab that's already gone.
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
                    }
                    catch { }
                }
            }
        }

        private static void HandleClientMessage(Guid connId, Conn conn, string text)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type != "watch") return;
            string who = root.TryGetProperty("profile", out var p) ? (p.ValueKind == JsonValueKind.Number ? p.GetRawText() : p.GetString() ?? "") : "";
            ulong id = ulong.TryParse(who, out var n) ? n : PlayerDB.GetAccountIdByUsernameCI(who.TrimStart('@'));
            Unwatch(connId, conn);
            if (id == 0) return;
            conn.Watching = id;
            Watchers.GetOrAdd(id, _ => new ConcurrentDictionary<Guid, Conn>())[connId] = conn;
            // First frame right away, so the page is current without waiting for a change.
            _ = SendSpotifyTo(conn, id);
        }

        private static void Unwatch(Guid connId, Conn conn)
        {
            if (conn.Watching == 0) return;
            if (Watchers.TryGetValue(conn.Watching, out var w)) { w.TryRemove(connId, out _); if (w.IsEmpty) Watchers.TryRemove(conn.Watching, out _); }
            conn.Watching = 0;
        }

        // Tell every open browser tab for this player to re-fetch its inbox. Fire-and-forget,
        // and a no-op (one dictionary lookup) when the player has no site tab open - safe to
        // call from hot paths like chat. Anything social about a player is also a change to
        // their PROFILE (friend counts, online friends), so the tabs watching it hear too.
        public static void Notify(ulong playerId)
        {
            if (playerId == 0) return;
            if (Sockets.TryGetValue(playerId, out var bag) && !bag.IsEmpty)
                foreach (var kv in bag)
                    if (kv.Value.Socket.State == WebSocketState.Open) _ = SendSafe(kv.Value, kv.Key, RefreshMsg);
            ProfileChanged(playerId);
        }

        /// <summary>Something visible on this account's profile changed (presence, room, name, bio, avatar, counts).</summary>
        public static void ProfileChanged(ulong accountId)
        {
            if (accountId == 0 || !Watchers.TryGetValue(accountId, out var w) || w.IsEmpty) return;
            var msg = Encoding.UTF8.GetBytes("{\"type\":\"profile\",\"id\":" + accountId + "}");
            foreach (var kv in w)
                if (kv.Value.Socket.State == WebSocketState.Open) _ = SendSafe(kv.Value, kv.Key, msg);
        }

        private static async Task SendSafe(Conn conn, Guid connId, byte[] msg)
        {
            try
            {
                await conn.Socket.SendAsync(new ArraySegment<byte>(msg), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                if (Sockets.TryGetValue(conn.PlayerId, out var bag)) bag.TryRemove(connId, out _);
                Unwatch(connId, conn);
            }
        }

        // ---- Spotify: push track changes to whoever is looking ---------------------------------
        // One loop for the whole server. Every 10 s it asks Spotify about each WATCHED account
        // that has a link (SpotifyLink caches per account, so that is one call per account, not
        // per viewer) and pushes a frame to each watcher whose view of it changed - a new track,
        // play/pause, a seek, or the song ending. Each watcher gets the payload as filtered by
        // the owner's "show to" choice for THEM, so nothing leaks through the push that the
        // page could not have fetched.
        private static readonly ConcurrentDictionary<ulong, string> _lastSpotify = new();   // watched account -> last state key
        private static int _spotifyLoopStarted;

        private static void EnsureSpotifyLoop()
        {
            if (Interlocked.Exchange(ref _spotifyLoopStarted, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await Task.Delay(10000);
                        foreach (var kv in Watchers.ToArray())
                        {
                            ulong owner = kv.Key;
                            if (kv.Value.IsEmpty || SpotifyLink.Get(owner) == null) continue;
                            var np = await SpotifyLink.Fetch(owner);
                            string key = np == null ? "-" : $"{np.TrackId}|{np.IsPlaying}|{(np.ProgressMs / 1000) - (long)(DateTime.UtcNow - np.FetchedAt).TotalSeconds}";
                            // The third term is "where the song started" in wall-clock terms: it
                            // only moves on a seek/restart, not as the song plays.
                            _lastSpotify.TryGetValue(owner, out var last);
                            if (key == last) continue;
                            _lastSpotify[owner] = key;
                            foreach (var w in kv.Value) _ = SendSpotifyTo(w.Value, owner);
                        }
                        foreach (var k in _lastSpotify.Keys.ToArray()) if (!Watchers.ContainsKey(k)) _lastSpotify.TryRemove(k, out _);
                    }
                    catch (Exception ex) { Console.WriteLine($"[site-ws] spotify loop: {ex.Message}"); }
                }
            });
        }

        private static async Task SendSpotifyTo(Conn conn, ulong owner)
        {
            try
            {
                if (conn.Socket.State != WebSocketState.Open) return;
                if (SpotifyLink.Get(owner) == null) return;
                object? payload = await SpotifyLink.PayloadFor(owner, conn.PlayerId);
                string json = JsonSerializer.Serialize(new { type = "spotify", id = owner, now_playing = payload });
                await conn.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        }
    }
}
