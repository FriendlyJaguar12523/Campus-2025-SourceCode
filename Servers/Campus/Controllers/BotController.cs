using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;

namespace Campus_live_rewrite.Controllers
{
    public class BotController : Controller
    {
        [HttpPost("/api/bot/player/level_up/{discorduserid}")]
        public async Task<ContentResult> LevelUp(ulong discorduserid)
        {
            string botkey = Request.Headers["Bot-Key"].ToString();
            var newLevel = Request.Form["new_level"].ToString();

            if (botkey != ConfigCampus.Campus_bot_key)
            {
                return new ContentResult // The people tryna find this api wont get in
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }

            // Was: int.Parse(newLevel) - FormatException (500) when the new_level form
            // field is missing or non-numeric. And account.player.Id was dereferenced
            // unguarded even though the line below already uses account?. - an unknown
            // Discord id therefore threw instead of reporting not-found.
            if (!int.TryParse(newLevel, out int parsedLevel))
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { success = false, message = "new_level must be an integer" }),
                    ContentType = "application/json",
                    StatusCode = 400
                };
            }

            var account = PlayerDB.GetPlayerByDiscordId(discorduserid);
            if (account?.player == null)
            {
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new { success = false, message = $"no account for discord id {discorduserid}" }),
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }

            await PlayerDB.SetPlayerLevel(parsedLevel, account.player.Id);
            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"Leveled up user {account?.player.Username} to level {newLevel}"
                }),
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [Route("/api/bot/create")]
        public ContentResult CreateAccViaBot()
        {
            string key = HttpContext.Request.Headers["Key"].ToString();
            string steamid = HttpContext.Request.Form["steam_id"].ToString();
            string discordUserId = HttpContext.Request.Form["discord_user_id"].ToString();

            ulong steamid2 = ulong.Parse(steamid);

            if (key == ConfigCampus.Campus_bot_key)
            {
                var blockedIds = new HashSet<string>
                {
                    "1176571557414445137",
                    "1193883666946998383",
                    "1344328341951615106"
                };

                if (PlayerDB.GetAccountCountByPlatformId(steamid2) >= 1)
                {
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            success = false,
                            value = "You already have a account!"
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                if (blockedIds.Contains(discordUserId))
                {
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            success = false,
                            value = "You require manual verification. Make a ticket in https://discord.gg/xH8MMwK5Bk on Discord"
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                var blockedSteamIds = new HashSet<string>
                {
                    "76561199485696854"

                };

                if (blockedSteamIds.Contains(steamid))
                {
                    return new ContentResult
                    {
                        Content = JsonConvert.SerializeObject(new
                        {
                            success = false,
                            value = "You require manual verification. Make a ticket in https://discord.gg/xH8MMwK5Bk on Discord"
                        }),
                        ContentType = "application/json",
                        StatusCode = 200
                    };
                }

                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(new
                    {
                        eeee = discordUserId, // todo
                    }),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            else
            {
                return new ContentResult
                {
                    Content = "",
                    ContentType = "application/json",
                    StatusCode = 404
                };
            }
        }
    }
}
