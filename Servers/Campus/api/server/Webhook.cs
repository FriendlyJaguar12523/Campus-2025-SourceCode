using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server.Classes.db_class;
using System.Text;

namespace Campus_live_rewrite.api.server
{
    public class Webhook
    {
        private static readonly Random rng = new Random();

        // One shared client for the whole process. The old code did
        // `using var client = new HttpClient()` per call, which leaks sockets into
        // TIME_WAIT under load and can eventually exhaust the ephemeral port range.
        // The timeout matters too: without it a slow or rate-limiting Discord can
        // hold the caller indefinitely.
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Webhook types Discord has answered 404 / 401 for: the webhook was deleted (or its token
        // revoked), which never heals. Posting to it anyway cost ~2,200 failed requests a day and a
        // "[Webhook] failed: 404" line each. Skipped until restart; replace its URL to revive it.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _dead = new();

        public static void Send(ulong accid, string type, string title, string description, long color, string? imageUrl = null, ulong? roomid = null)
        {
            if (_dead.ContainsKey(type.ToLower())) return;
            string? webhookUrl = type.ToLower() switch
            {
                "prod" => "https://discord.com/api/webhooks/0/REDACTED",
                "admin" => "https://discord.com/api/webhooks/0/REDACTED",
                "roomie" => "https://discord.com/api/webhooks/0/REDACTED",
                "gaylog" => "https://discord.com/api/webhooks/0/REDACTED",
                _ => null
            };

            if (string.IsNullOrEmpty(webhookUrl))
            {
                Console.WriteLine($"Invalid webhook type: {type}");
                return;
            }


            player_data account = PlayerDB.GetFullAccount(accid);
            bool dev = PlayerDB.CheckDevFlag(accid);
            RoomDBClasses.RoomRoot? room = roomid.HasValue ? RoomDB.GetRoom(roomid.Value) : null;

            string authorName = account?.player?.Username ?? "Unknown User";
            if (dev)
            {
                //authorName += " [DEV]";
            }
            //string authorIcon = "https://example.com/icon.png";

            var payload = JsonConvert.SerializeObject(new discord_content
            {
                content = "",
                attachments = new List<string>(),
                embeds = new List<discord_embed>
                {
                    new discord_embed
                    {
                        title = title,
                        description = description,
                        color = (int)2067276,
                        thumbnail = room != null && !string.IsNullOrEmpty(room.ImageName) ? new { url = $"https://camprec.net/img/{room.ImageName}" } : null,
                        image = string.IsNullOrEmpty(imageUrl) ? null : new discord_embed_image { url = imageUrl },
                        author = new discord_embed_author
                        {
                            name = authorName,
                            icon_url = $"https://camprec.net/img/{account?.player?.profileImage}?width=80&cropSquare=true",
                            url = $"https://camprec.net/user/{account.player.Username}"
                        }
                    }
                }
            });

            Console.WriteLine(
                $"--- {title} ({type.ToUpper()}) ----------------------\n\n" +
                $"{description}\n\n" +
                $"----------------------------------");
            ulong number = (ulong)rng.Next(1000, 999999999);
            // Was: `client.PostAsync(...).Result` - a BLOCKING wait on a Discord HTTP
            // round-trip, executed inline on the request thread. 13 of the 17 callers
            // are request paths (login, exclusivelogin, matchmake/dorm, auth, account
            // actions), so every one of those held a thread-pool thread until Discord
            // replied. When Discord is slow or rate-limiting (429) that is hundreds of
            // milliseconds to seconds PER REQUEST, and blocked threads starve the pool -
            // which looks exactly like "everything is delayed".
            //
            // Now fire-and-forget with failure logging. A Discord notification is a side
            // channel; it must never gate the game client's response. This changes no
            // response byte - only whether the caller waits.
            _ = Task.Run(async () =>
            {
                try
                {
                    var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    var response = await _http.PostAsync(webhookUrl, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        int code = (int)response.StatusCode;
                        if ((code == 404 || code == 401) && _dead.TryAdd(type.ToLower(), true))
                            Console.WriteLine($"[Webhook] the '{type}' webhook answered {code} - Discord no longer has it (deleted?). " +
                                              "Not posting to it again until restart: make a new webhook and put its URL in Webhook.cs.");
                        else if (code != 404 && code != 401)
                            Console.WriteLine($"[Webhook] failed: {code} {response.ReasonPhrase}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Webhook] error: {ex.Message}");
                }
            });
        }

        public class discord_content
        {
            public string? content { get; set; }
            public List<discord_embed>? embeds { get; set; }
            public List<string>? attachments { get; set; }
        }

        public class discord_embed
        {
            public string? title { get; set; }
            public string? description { get; set; }
            public long color { get; set; }
            public object? thumbnail { get; set; }
            public discord_embed_image? image { get; set; }
            public discord_embed_author? author { get; set; }
        }

        public class discord_embed_image
        {
            public string? url { get; set; }
        }
        public class discord_embed_author
        {
            public string? name { get; set; }
            public string? url { get; set; } 
            public string? icon_url { get; set; } 
        }
    }
}
