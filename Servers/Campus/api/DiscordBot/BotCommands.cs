using System.Linq;
using System.Text.RegularExpressions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.api.server;
using Campus_live_rewrite.api.server.Classes.db_class;

namespace Campus_live_rewrite.api.DiscordBot
{
    public class BotCommands : InteractionModuleBase<SocketInteractionContext>
    {
        // Custom ids for the /user-panel buttons. Prefix with "up:" so the modal handlers can
        // grab their own inputs unambiguously.
        private const string BtnEmail    = "up:email";
        private const string BtnPassword = "up:password";
        private const string BtnUsername = "up:username";
        private const string BtnDisplay  = "up:display";
        private const string BtnPfp      = "up:pfp";

        // Where uploaded profile pictures live. This path is one of the roots that
        // ImagesController serves from — files dropped here appear at /img/{filename}.
        private static string PlayerImagesDir =>
            Path.Combine(Environment.CurrentDirectory, "Data", "Images", "player_images");

        private const long MaxPfpBytes = 8L * 1024 * 1024; // 8 MB

        // -----------------------------------------------------------------------------------
        // /get-user
        // -----------------------------------------------------------------------------------
        [SlashCommand("get-user", "Get Campus user by ID or name.")]
        public async Task GetUserByIdOrName(string userInput)
        {
            try
            {
                player_data? player;

                if (ulong.TryParse(userInput, out ulong playerId))
                    player = PlayerDB.GetFullAccount(playerId);
                else
                    player = PlayerDB.GetFullAccountByName(userInput);

                if (player == null)
                {
                    await RespondAsync("No user found with the provided ID or name.", ephemeral: true);
                    return;
                }

                var bio = player.player_Extra?.Bio;
                if (string.IsNullOrWhiteSpace(bio)) bio = "This player hasn't written a bio yet!";

                var embed = new EmbedBuilder()
                    .WithTitle(player.player.Username)
                    .WithDescription(
                        $"Username: **@{player.player.Username}**\n" +
                        $"Display Name: **{player.player.DisplayName}**\n" +
                        $"Level: **{player.player.Level}**\n" +
                        $"Bio: ```{bio}```\n" +
                        $"Linked Discord: <@{player.player_Extra?.DiscordId}>")
                    .WithColor(new Color(155, 92, 255))
                    .WithThumbnailUrl($"https://camprec.net/img/{player.player.profileImage}?cropSquare=true")
                    .Build();

                await RespondAsync(embed: embed);
            }
            catch
            {
                await RespondAsync("Something went wrong. Please try again.", ephemeral: true);
            }
        }

        // -----------------------------------------------------------------------------------
        // /leaderboard
        // -----------------------------------------------------------------------------------
        [SlashCommand("leaderboard", "Display the leaderboard for Campus rooms.")]
        public async Task ShowLeaderboardAsync(string roomname, int statchannel)
        {
            try
            {
                var room = RoomDB.GetRoomByName(roomname);
                if (room == null)
                {
                    await RespondAsync("Room not found!", ephemeral: true);
                    return;
                }

                var leaderboard = LeaderboardDB.GetNearbyScores(1, statchannel, room.RoomId, 0);
                if (leaderboard == null || leaderboard.Count == 0)
                {
                    await RespondAsync("There is no leaderboard or there are no scores for this room!", ephemeral: true);
                    return;
                }

                var embedBuilder = new EmbedBuilder()
                    .WithTitle($"Campus - Top Players in ^{roomname} Leaderboard")
                    .WithDescription("Here are the top players.")
                    .WithColor(new Color(155, 92, 255))
                    .WithFooter("Keep playing to climb the ranks!");

                int rank = 1;
                foreach (var score in leaderboard)
                {
                    var player = PlayerDB.GetFullAccount((ulong)score.PlayerId);
                    if (player == null) continue;

                    string rankSuffix = rank switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
                    embedBuilder.AddField($"{rank}{rankSuffix} - @{player.player.Username} (Score: {score.Score})", true);
                    rank++;
                }

                await RespondAsync(embed: embedBuilder.Build());
            }
            catch
            {
                await RespondAsync("Something went wrong. Please try again.", ephemeral: true);
            }
        }

        // -----------------------------------------------------------------------------------
        // /create-account   (username + password, optional platform + platform id)
        // -----------------------------------------------------------------------------------
        public enum CreatePlatform
        {
            None = 0,
            Steam = 1,
            Meta  = 2
        }

        [SlashCommand("create-account", "Create a Campus account bound to your Discord (username + password).")]
        public async Task CreateCampusAccount(
            [Summary("username", "1-20 chars, letters/numbers/underscore only.")] string username,
            [Summary("password", "8+ chars. Kept secret; only you will see it.")] string password,
            [Summary("platform", "Optional: Steam or Meta (leave empty for Discord-only).")] CreatePlatform platform = CreatePlatform.None,
            [Summary("platform_id", "Optional: your Steam ID (76561...) or Meta ID. Ignored if platform is empty.")] string? platform_id = null)
        {
            try
            {
                ulong discordId = Context.User.Id;
                Console.WriteLine($"[/create-account] discord={discordId} tag={Context.User.Username} tried username='{username}' platform={platform} platformId='{platform_id}'");

                username = (username ?? "").Trim();
                password = password ?? "";

                if (username.Length < 1 || username.Length > 20 || !Regex.IsMatch(username, @"^[a-zA-Z0-9_]+$"))
                {
                    await RespondAsync("Username must be 1-20 chars, letters/numbers/underscore only.", ephemeral: true);
                    return;
                }

                if (password.Length < 8)
                {
                    await RespondAsync("Password must be at least 8 characters.", ephemeral: true);
                    return;
                }

                if (PlayerDB.GetAccountCountByDiscordId(discordId) > 0)
                {
                    await RespondAsync("You already have a Campus account linked to this Discord.", ephemeral: true);
                    return;
                }

                if (PlayerDB.GetFullAccountByName(username) != null)
                {
                    await RespondAsync($"Username **{username}** is already taken. Try another.", ephemeral: true);
                    return;
                }

                // Resolve platform + platform id.
                Platforms platformEnum;
                ulong platformIdParsed = 0UL;
                string deviceTag;
                switch (platform)
                {
                    case CreatePlatform.Steam:
                        platformEnum = Platforms.Steam;
                        deviceTag = $"discord-slash-steam-{discordId}";
                        if (!string.IsNullOrWhiteSpace(platform_id))
                        {
                            if (!ulong.TryParse(platform_id.Trim(), out platformIdParsed) || !platform_id.Trim().StartsWith("7656"))
                            {
                                await RespondAsync("Steam ID looks invalid — expected a 17-digit number starting with 7656.", ephemeral: true);
                                return;
                            }
                            if (PlayerDB.GetAccountCountByPlatformId(platformIdParsed) > 0)
                            {
                                await RespondAsync("That Steam ID is already linked to another Campus account.", ephemeral: true);
                                return;
                            }
                        }
                        break;

                    case CreatePlatform.Meta:
                        platformEnum = Platforms.Oculus;
                        deviceTag = $"discord-slash-meta-{discordId}";
                        if (!string.IsNullOrWhiteSpace(platform_id))
                        {
                            if (!ulong.TryParse(platform_id.Trim(), out platformIdParsed))
                            {
                                await RespondAsync("Meta ID must be numeric.", ephemeral: true);
                                return;
                            }
                            if (PlayerDB.GetAccountCountByPlatformId(platformIdParsed) > 0)
                            {
                                await RespondAsync("That Meta ID is already linked to another Campus account.", ephemeral: true);
                                return;
                            }
                        }
                        break;

                    default:
                        platformEnum = Platforms.RecNet;
                        deviceTag = $"discord-slash-{discordId}";
                        break;
                }

                account_data acc;
                if (!PlayerDB.CreateAccount(platformEnum, platformIdParsed, deviceTag, discordId, out acc))
                {
                    await RespondAsync("Account creation failed. Try again later.", ephemeral: true);
                    return;
                }

                if (!PlayerDB.SetPlayerUsername(username, acc.accountId))
                {
                    await RespondAsync($"Username **{username}** was taken while creating. Try another.", ephemeral: true);
                    return;
                }
                PlayerDB.SetDisplayName(username, acc.accountId);
                PlayerDB.SetPlayerPassword(password, acc.accountId, log: false);

                Console.WriteLine($"[/create-account] SUCCESS discord={discordId} accid={acc.accountId} username='{username}' platform={platformEnum}");

                string platformNote = platform == CreatePlatform.None
                    ? "Discord-only account (no platform linked)."
                    : $"Linked platform: **{platform}**" + (platformIdParsed > 0 ? $" ({platformIdParsed})" : " (no ID provided)");

                await RespondAsync(
                    $"Account **{username}** created and linked to your Discord.\n" +
                    $"{platformNote}\n" +
                    $"You can log in at https://camprec.net/login",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[/create-account] EXCEPTION: {ex}");
                await RespondAsync("Something went wrong. Try again.", ephemeral: true);
            }
        }

        // -----------------------------------------------------------------------------------
        // /link  (link THIS Discord to an existing Campus account, via the code from the website
        //         Settings > Discord tab). Isolated single purpose: it only records the
        //         Discord<->account association (PlayerDB.SetDiscordLink) - it grants no admin/dev
        //         access and touches nothing to do with the admin panel login.
        // -----------------------------------------------------------------------------------
        [SlashCommand("link", "Link your Discord to your Campus account using the code from the website (Settings > Discord).")]
        public async Task LinkAccount(
            [Summary("code", "The 6-character code from camprec.net > Settings > Discord")] string code)
        {
            try
            {
                ulong discordId = Context.User.Id;
                string tag = Context.User.Username;

                if (!DiscordLinkCodes.TryResolve(code, out ulong accountId))
                {
                    await RespondAsync("That code is invalid or expired. Open **Settings > Discord** on the website and press **Link Discord** for a fresh code (valid 10 minutes).", ephemeral: true);
                    return;
                }

                var acc = PlayerDB.GetFullAccount(accountId);
                if (acc?.player == null)
                {
                    await RespondAsync("Couldn't find that Campus account. Try generating a new code.", ephemeral: true);
                    return;
                }

                // This Discord already tied to a DIFFERENT account?
                ulong otherAcc = PlayerDB.FindPlayerByDiscordId(discordId, accountId);
                if (otherAcc != 0)
                {
                    await RespondAsync($"Your Discord is already linked to another Campus account (id {otherAcc}). Unlink it there first (Settings > Discord > Unlink).", ephemeral: true);
                    return;
                }

                // This account already tied to a DIFFERENT Discord?
                ulong existing = acc.player_Extra?.DiscordId ?? 0;
                if (existing != 0 && existing != discordId)
                {
                    await RespondAsync("That Campus account is already linked to a different Discord. Unlink it on the website first.", ephemeral: true);
                    return;
                }

                PlayerDB.SetDiscordLink(accountId, discordId, tag);
                DiscordLinkCodes.Consume(code);
                Console.WriteLine($"[/link] discord={discordId} ({tag}) linked to account {accountId} (@{acc.player.Username})");

                await RespondAsync($"Linked! Your Discord is now connected to **@{acc.player.Username}**. You can manage your account with `/user-panel`.", ephemeral: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[/link] EXCEPTION: {ex}");
                await RespondAsync("Something went wrong linking your account. Try again.", ephemeral: true);
            }
        }

        // -----------------------------------------------------------------------------------
        // /user-panel  (attach email, reset password, change @username, change display name)
        // -----------------------------------------------------------------------------------
        [SlashCommand("user-panel", "Manage your Campus account: email, password, username, display name.")]
        public async Task UserPanel()
        {
            var discordId = Context.User.Id;
            var accountIds = PlayerDB.GetAccountIdsByDiscordId(discordId);
            if (accountIds == null || !accountIds.Any())
            {
                await RespondAsync(
                    "No Campus account is linked to your Discord yet. Use `/create-account` first.",
                    ephemeral: true);
                return;
            }

            var player = PlayerDB.GetFullAccount(accountIds.First());
            if (player?.player == null)
            {
                await RespondAsync("Couldn't load your account. Please try again later.", ephemeral: true);
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("Your Campus Account")
                .WithDescription(
                    $"**Username:** @{player.player.Username}\n" +
                    $"**Display Name:** {player.player.DisplayName}\n" +
                    $"**Email:** {(string.IsNullOrWhiteSpace(player.player.Email) ? "(not set)" : player.player.Email)}\n" +
                    $"**Account ID:** {player.player.Id}\n" +
                    $"**Level:** {player.player.Level}\n\n" +
                    $"Pick an action below.")
                .WithColor(new Color(155, 92, 255))
                .WithThumbnailUrl($"https://camprec.net/img/{player.player.profileImage}?cropSquare=true")
                .Build();

            var buttons = new ComponentBuilder()
                .WithButton("Attach Email",     BtnEmail,    ButtonStyle.Secondary)
                .WithButton("Reset Password",   BtnPassword, ButtonStyle.Danger)
                .WithButton("Change @Username", BtnUsername, ButtonStyle.Primary)
                .WithButton("Change Display Name", BtnDisplay, ButtonStyle.Primary)
                .WithButton("Change Profile Picture", BtnPfp, ButtonStyle.Secondary)
                .Build();

            await RespondAsync(embed: embed, components: buttons, ephemeral: true);
        }

        // ---------------- Button handlers → open modals ----------------
        [ComponentInteraction(BtnEmail)]
        public async Task OpenEmailModal() =>
            await Context.Interaction.RespondWithModalAsync<EmailModal>("up-modal:email");

        [ComponentInteraction(BtnPassword)]
        public async Task OpenPasswordModal() =>
            await Context.Interaction.RespondWithModalAsync<PasswordModal>("up-modal:password");

        [ComponentInteraction(BtnUsername)]
        public async Task OpenUsernameModal() =>
            await Context.Interaction.RespondWithModalAsync<UsernameModal>("up-modal:username");

        [ComponentInteraction(BtnDisplay)]
        public async Task OpenDisplayModal() =>
            await Context.Interaction.RespondWithModalAsync<DisplayModal>("up-modal:display");

        [ComponentInteraction(BtnPfp)]
        public async Task PfpInfo() =>
            await RespondAsync(
                "Discord modals can't accept file uploads. Run `/set-pfp` and attach your image there — " +
                "PNG/JPG/GIF/WebP up to 8 MB. It'll replace your current profile picture immediately.",
                ephemeral: true);

        // -----------------------------------------------------------------------------------
        // /set-pfp   (upload a new profile picture — attachments only work via slash params)
        // -----------------------------------------------------------------------------------
        [SlashCommand("set-pfp", "Upload a new profile picture for your Campus account.")]
        public async Task SetPfp(
            [Summary("image", "PNG, JPG, GIF, or WebP. Max 8 MB.")] IAttachment image)
        {
            await DeferAsync(ephemeral: true);

            var accId = ResolveMyAccountId();
            if (accId == 0)
            {
                await FollowupAsync("No Campus account is linked to your Discord. Use `/create-account` first.", ephemeral: true);
                return;
            }

            if (image == null || string.IsNullOrEmpty(image.Url))
            {
                await FollowupAsync("No image was attached.", ephemeral: true);
                return;
            }

            if (image.Size > MaxPfpBytes)
            {
                await FollowupAsync($"That file is {image.Size / 1024 / 1024} MB — max is 8 MB.", ephemeral: true);
                return;
            }

            var ctype = (image.ContentType ?? "").ToLowerInvariant();
            var allowedTypes = new[] { "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp" };
            if (!allowedTypes.Any(t => ctype.StartsWith(t)))
            {
                await FollowupAsync($"Unsupported file type `{image.ContentType}`. Use PNG, JPG, GIF, or WebP.", ephemeral: true);
                return;
            }

            if (PlayerDB.IsProfileLocked(accId, PlayerDB.ProfileLock.Picture))
            {
                await FollowupAsync(PlayerDB.ProfileLockMessage(PlayerDB.ProfileLock.Picture), ephemeral: true);
                return;
            }

            try
            {
                Directory.CreateDirectory(PlayerImagesDir);

                // Filename: pfp_{accId}_{timestamp}.{ext}. Timestamp defeats Discord/CDN caches
                // so the new picture appears right away instead of the client showing the old one.
                var ext = Path.GetExtension(image.Filename ?? "").ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(ext) || ext.Length > 6)
                {
                    ext = ctype.Contains("png")  ? ".png"
                        : ctype.Contains("jpeg") ? ".jpg"
                        : ctype.Contains("gif")  ? ".gif"
                        : ctype.Contains("webp") ? ".webp"
                        : ".png";
                }
                var filename = $"pfp_{accId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{ext}";
                var fullPath = Path.Combine(PlayerImagesDir, filename);

                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                {
                    var bytes = await http.GetByteArrayAsync(image.Url);
                    if (bytes.Length > MaxPfpBytes)
                    {
                        await FollowupAsync("File too large after download.", ephemeral: true);
                        return;
                    }
                    await File.WriteAllBytesAsync(fullPath, bytes);
                }

                PlayerDB.SetPlayerImageName(filename, accId);
                Console.WriteLine($"[/set-pfp] discord={Context.User.Id} accid={accId} saved {filename} ({image.Size} bytes)");

                await FollowupAsync(
                    $"Profile picture updated. Preview: https://camprec.net/img/{filename}?cropSquare=true",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[/set-pfp] EXCEPTION discord={Context.User.Id} accid={accId}: {ex}");
                await FollowupAsync("Something went wrong while uploading your image. Try again.", ephemeral: true);
            }
        }

        // ---------------- Modal shapes ----------------
        public class EmailModal : IModal
        {
            public string Title => "Attach Email";
            [InputLabel("Email address")]
            [ModalTextInput("email", TextInputStyle.Short, placeholder: "you@example.com", maxLength: 120)]
            public string Email { get; set; } = "";
        }

        public class PasswordModal : IModal
        {
            public string Title => "Reset Password";
            [InputLabel("New password (8+ chars)")]
            [ModalTextInput("password", TextInputStyle.Short, placeholder: "New password", minLength: 8, maxLength: 100)]
            public string Password { get; set; } = "";
        }

        public class UsernameModal : IModal
        {
            public string Title => "Change @Username";
            [InputLabel("New username (1-20 chars, a-z 0-9 _)")]
            [ModalTextInput("username", TextInputStyle.Short, placeholder: "yourname", minLength: 1, maxLength: 20)]
            public string Username { get; set; } = "";
        }

        public class DisplayModal : IModal
        {
            public string Title => "Change Display Name";
            [InputLabel("New display name (1-32 chars)")]
            [ModalTextInput("display", TextInputStyle.Short, placeholder: "Your Display Name", minLength: 1, maxLength: 32)]
            public string Display { get; set; } = "";
        }

        // ---------------- Modal submit handlers ----------------
        [ModalInteraction("up-modal:email")]
        public async Task SubmitEmail(EmailModal modal)
        {
            var accId = ResolveMyAccountId();
            if (accId == 0) { await RespondAsync("No linked account.", ephemeral: true); return; }

            var email = (modal.Email ?? "").Trim();
            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                await RespondAsync("That doesn't look like a valid email.", ephemeral: true);
                return;
            }
            var ok = PlayerDB.SetPlayerEmail(email, accId);
            Console.WriteLine($"[/user-panel] email set discord={Context.User.Id} accid={accId} ok={ok}");
            await RespondAsync(ok ? $"Email updated to `{email}`." : "Failed to update email.", ephemeral: true);
        }

        [ModalInteraction("up-modal:password")]
        public async Task SubmitPassword(PasswordModal modal)
        {
            var accId = ResolveMyAccountId();
            if (accId == 0) { await RespondAsync("No linked account.", ephemeral: true); return; }

            var pw = modal.Password ?? "";
            if (pw.Length < 8)
            {
                await RespondAsync("Password must be at least 8 characters.", ephemeral: true);
                return;
            }
            PlayerDB.SetPlayerPassword(pw, accId, log: false);
            Console.WriteLine($"[/user-panel] password reset discord={Context.User.Id} accid={accId}");
            await RespondAsync("Password updated.", ephemeral: true);
        }

        [ModalInteraction("up-modal:username")]
        public async Task SubmitUsername(UsernameModal modal)
        {
            var accId = ResolveMyAccountId();
            if (accId == 0) { await RespondAsync("No linked account.", ephemeral: true); return; }

            var uname = (modal.Username ?? "").Trim();
            if (uname.Length < 1 || uname.Length > 20 || !Regex.IsMatch(uname, @"^[a-zA-Z0-9_]+$"))
            {
                await RespondAsync("Username must be 1-20 chars, letters/numbers/underscore only.", ephemeral: true);
                return;
            }
            if (PlayerDB.GetFullAccountByName(uname) != null)
            {
                await RespondAsync($"Username **{uname}** is already taken.", ephemeral: true);
                return;
            }
            var ok = PlayerDB.SetPlayerUsername(uname, accId);
            Console.WriteLine($"[/user-panel] username change discord={Context.User.Id} accid={accId} new='{uname}' ok={ok}");
            await RespondAsync(ok ? $"Username updated to **@{uname}**." : "Failed to update username.", ephemeral: true);
        }

        [ModalInteraction("up-modal:display")]
        public async Task SubmitDisplay(DisplayModal modal)
        {
            var accId = ResolveMyAccountId();
            if (accId == 0) { await RespondAsync("No linked account.", ephemeral: true); return; }

            var dn = (modal.Display ?? "").Trim();
            if (dn.Length < 1 || dn.Length > 32)
            {
                await RespondAsync("Display name must be 1-32 characters.", ephemeral: true);
                return;
            }
            PlayerDB.SetDisplayName(dn, accId);
            Console.WriteLine($"[/user-panel] display change discord={Context.User.Id} accid={accId} new='{dn}'");
            await RespondAsync($"Display name updated to **{dn}**.", ephemeral: true);
        }

        // Resolve the caller's Campus account id from their Discord id. 0 if none.
        private ulong ResolveMyAccountId()
        {
            var ids = PlayerDB.GetAccountIdsByDiscordId(Context.User.Id);
            return (ids != null && ids.Any()) ? ids.First() : 0UL;
        }
    }
}
