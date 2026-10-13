using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using System;
using System.Reflection;
using System.Threading.Tasks;

public class DiscordBot
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _commands;
    private readonly IServiceProvider _services;
    private readonly IServiceProvider _serviceProvider;
    private static string BOT_TOKEN = "REDACTED"; 

    public DiscordBot(IServiceProvider services)
    {
        // Minimal intents needed for slash commands: Guilds only. Fewer intents = fewer
        // Discord-portal toggles needed and no privileged-intent gate.
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            LogLevel = LogSeverity.Info
        });
        _commands = new InteractionService(_client.Rest);
        _serviceProvider = services;
    }

    public async Task RunAsync()
    {
        _client.Log += Log;
        _client.Ready += Ready;
        _client.Connected += () => { Console.WriteLine("[DiscordBot] Gateway Connected."); return Task.CompletedTask; };
        _client.Disconnected += ex => { Console.WriteLine($"[DiscordBot] Gateway Disconnected: {ex?.Message}"); return Task.CompletedTask; };
        _client.InteractionCreated += HandleInteraction;

        Console.WriteLine("[DiscordBot] Logging in...");
        await _client.LoginAsync(TokenType.Bot, BOT_TOKEN);
        Console.WriteLine("[DiscordBot] Login OK. Starting gateway...");
        await _client.StartAsync();
        Console.WriteLine("[DiscordBot] StartAsync returned. Waiting for Ready (fallback timer at 20s).");

        // Fallback: if Ready hasn't run within 20s, kick off registration anyway.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            if (!_hasRegistered)
            {
                Console.WriteLine("[DiscordBot] Ready still hasn't fired — running RegisterCommandsAsync via fallback.");
                await RegisterCommandsAsync();
            }
        });

        await Task.Delay(-1);
    }

    private bool _hasRegistered = false;

    private async Task Ready()
    {
        Console.WriteLine($"[DiscordBot] READY event fired. Logged in as {_client.CurrentUser?.Username}#{_client.CurrentUser?.Discriminator} ({_client.CurrentUser?.Id}). Guilds visible: {_client.Guilds.Count}");
        await RegisterCommandsAsync();
    }

    private async Task RegisterCommandsAsync()
    {
        if (_hasRegistered) return;
        _hasRegistered = true;
        try
        {
            Console.WriteLine("[DiscordBot] Loading command modules...");
            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
            Console.WriteLine($"[DiscordBot] Loaded {_commands.SlashCommands.Count} slash commands.");

            // Known guild IDs — REST call works even if the gateway hasn't cached the guild yet.
            var targetGuildIds = new HashSet<ulong>
            {
                1526392715590500362, // Andromeda • 2023 (user-provided)
            };

            // Merge in whatever the gateway has cached (fresh guilds the bot joins later).
            foreach (var g in _client.Guilds) targetGuildIds.Add(g.Id);

            Console.WriteLine($"[DiscordBot] Registering commands to {targetGuildIds.Count} guild(s): {string.Join(",", targetGuildIds)}");

            foreach (var gid in targetGuildIds)
            {
                try
                {
                    await _commands.RegisterCommandsToGuildAsync(gid);
                    Console.WriteLine($"[DiscordBot] ✅ Registered commands to guild {gid}.");
                }
                catch (Exception gex)
                {
                    Console.WriteLine($"[DiscordBot] ❌ Failed to register to guild {gid}: {gex.Message}");
                }
            }

            // NOTE: Do NOT register commands globally — if a command exists both globally and
            // per-guild, Discord's client shows it TWICE in the picker. Wipe any stale globals
            // that a previous build left behind so old duplicates disappear.
            try
            {
                await _client.Rest.BulkOverwriteGlobalCommands(Array.Empty<Discord.ApplicationCommandProperties>());
                Console.WriteLine("[DiscordBot] Cleared any previously-registered global commands.");
            }
            catch (Exception gex)
            {
                Console.WriteLine($"[DiscordBot] Global clear failed: {gex.Message}");
            }

            _client.JoinedGuild += async g =>
            {
                try
                {
                    await _commands.RegisterCommandsToGuildAsync(g.Id);
                    Console.WriteLine($"[DiscordBot] ✅ Auto-registered commands to newly joined guild '{g.Name}' ({g.Id}).");
                }
                catch (Exception jex)
                {
                    Console.WriteLine($"[DiscordBot] ❌ Auto-register failed for '{g.Name}': {jex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiscordBot] RegisterCommandsAsync EXCEPTION: {ex}");
        }
    }


    private Task Log(LogMessage log)
    {
        Console.WriteLine(log);
        return Task.CompletedTask;
    }

    private async Task HandleInteraction(SocketInteraction interaction)
    {
        // Handle the interaction using the InteractionService
        var context = new SocketInteractionContext(_client, interaction);
        await _commands.ExecuteCommandAsync(context, _serviceProvider);
    }
}
