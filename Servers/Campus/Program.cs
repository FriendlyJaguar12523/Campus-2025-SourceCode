using LiteDB;
using Microsoft.Extensions.FileProviders;
using Campus_live_rewrite;
using Campus_live_rewrite.api.dbs;
using Campus_live_rewrite.Controllers;
using Campus_live_rewrite.Commands;
using Campus_live_rewrite.api.server;
using System.Security.Principal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Campus_live_rewrite.api.server.Classes;
using Newtonsoft.Json;

internal class Program
{
    private static async Task Main(string[] args)
    {
        // If a DB restore was staged from the admin panel, apply it NOW — before anything opens
        // a LiteDB file (they open lazily on first use, and can't be overwritten while held open).
        BackupService.RestorePendingIfAny();

        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.AllowSynchronousIO = true;
            serverOptions.AddServerHeader = false;   // do not announce "Kestrel"

            // ---- DoS / resource-exhaustion caps -------------------------------------------
            // Nothing bounded Kestrel before this, so a connection flood or a giant/slow body
            // could grow the process to max memory (the reported crash). These are hard ceilings
            // that never affect a normal client; they only stop abuse.
            //
            //  * Body size: an explicit per-request cap (== the historical 30 MB default, so real
            //    room/image uploads are unchanged) instead of relying on the implicit default.
            //  * Concurrent connections: bound how many sockets can be held open at once, so a
            //    flood of connections cannot balloon memory - excess connections are refused, the
            //    server stays up. Applies separately to normal and upgraded (WebSocket) sockets.
            //  * Header timeout: a client must finish sending request headers within 15s, which
            //    kills slowloris-style "trickle the headers forever" connection hoarding.
            //  * Keep-alive: reclaim idle connections after 60s instead of the 130s default.
            //  (MinRequestBodyDataRate is left at Kestrel's default - 240 B/s over a 5s grace -
            //   which already drops a client that trickles the BODY too slowly.)
            serverOptions.Limits.MaxRequestBodySize = ConfigCampus.MaxRequestBodyBytes;
            serverOptions.Limits.MaxConcurrentConnections = ConfigCampus.MaxConcurrentConnections;
            serverOptions.Limits.MaxConcurrentUpgradedConnections = ConfigCampus.MaxConcurrentUpgradedConnections;
            serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
        });

        builder.Services.AddHttpLogging(configureOptions => { });

        builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = null;
    });

        builder.Services.AddSignalR();

        builder.Services.AddSingleton<DiscordBot>();

        builder.Host.UseContentRoot(Directory.GetCurrentDirectory());

        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.SuppressModelStateInvalidFilter = false;
        });

        var app = builder.Build();

        if (!Directory.Exists(Environment.CurrentDirectory + "/Data"))
        {
            Directory.CreateDirectory(Environment.CurrentDirectory + "/Data");
            Directory.CreateDirectory(Environment.CurrentDirectory + "/Data/APIS");
            Directory.CreateDirectory(Environment.CurrentDirectory + "/Data/DBs");
        }

        if (!Directory.Exists(Environment.CurrentDirectory + "/Web"))
        {
            Directory.CreateDirectory(Environment.CurrentDirectory + "/Web");
            Directory.CreateDirectory(Environment.CurrentDirectory + "/Web/Admin");
        }

        // FIRST in the pipeline: a banned IP gets 502 for the whole domain and never
        // touches logging, routing or any controller. Loopback is exempt so the operator
        // cannot lock themselves out of the admin panel over the SSH tunnel.
        // Before even that: a connection that did not come from the reverse proxy on this machine is dropped (OriginLockMiddleware).
        app.UseMiddleware<Campus_live_rewrite.Middleware.OriginLockMiddleware>();
        // Then the real player address from the proxy (Caddy behind Cloudflare), written into X-Forwarded-For for every check below (ClientIpMiddleware).
        app.UseMiddleware<Campus_live_rewrite.Middleware.ClientIpMiddleware>();
        Campus_live_rewrite.api.server.YoutubeFetch.StartSweeper();   // deletes fetched YouTube videos once their 3 days are up
        app.UseMiddleware<BannedIpMiddleware>();

        // Which host name shows what: the website only on camprec.net, the admin panel only on admin.camprec.net, ns. / cdn. just their
        // own answers (HostRoutingMiddleware). Early, so a page on the wrong host is turned away before anything else runs.
        app.UseMiddleware<Campus_live_rewrite.Middleware.HostRoutingMiddleware>();

        // Per-IP flood + brute-force guard. Runs right after the ban check and BEFORE routing /
        // logging / controllers, so a flood is 429'd at the cheapest possible point and never
        // reaches LiteDB or the access log. General ceiling stops volumetric DoS; a tight ceiling
        // on /Auth/connect/token + /api/site/login stops credential brute-force and token farming.
        // Loopback exempt. Toggle with ConfigCampus.TrafficGuardEnabled.
        app.UseMiddleware<TrafficGuardMiddleware>();

        // Patch gate: refuse game requests from any IP that has not completed the patch handshake
        // (POST /api/patch/verify with the correct X-Campus-Patch header). A wrong/old/no patch
        // never validates, so it is blocked here. Toggle with PatchGateMiddleware.Enable; exempts
        // /api/patch/verify + /admin + browser assets + loopback.
        app.UseMiddleware<PatchGateMiddleware>();

        // Admin hardening: security headers + per-IP rate limit on /admin/* and /api/web/admin/*
        // (on top of the dev-token + admin-session auth). Runs after the gate so the login page is
        // still reachable to be throttled, and after the ban check so banned IPs never reach it.
        app.UseMiddleware<AdminProtectionMiddleware>();

        // Lightweight per-request access log for the admin "Log" tab (/admin/api/apilog).
        // Rewritten to an in-memory ring buffer + single shared file writer (see Log.cs), so it
        // no longer opens/closes the file per request or drops lines under concurrency. Now cheap
        // enough to run UNCONDITIONALLY - the admin log tool needs data whether or not the heavy
        // VerboseRequestLogging body-logging (Log2, below) is on. This only records one line per
        // request ("<time> <METHOD> <PATH> -> <status>"); it changes no response bytes.
        app.UseMiddleware<Log>(Path.Combine(Environment.CurrentDirectory, "Data", "APILog.txt"));

        // Registered unconditionally - it checks the flag itself and short-circuits,
        // so the flag can be read in one place and the pipeline shape stays constant.
        app.UseMiddleware<RequestResponseLoggingMiddleware>();

        // Banned website sessions: signed in and shown the ban banner, but every write they try
        // (profile picture, bio, photos, friends...) is refused - see SiteBanGateMiddleware.
        app.UseMiddleware<SiteBanGateMiddleware>();

        // Website writes are plain text only - no fancy-font letters, emoji or zalgo (SitePlainTextMiddleware).
        app.UseMiddleware<SitePlainTextMiddleware>();

        //app.UseMiddleware<VpnBlockMiddleware>();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.ToString();
            if (path.Contains("//"))
            {
                path = path.Replace("//", "/");
                context.Request.Path = path;
            }
            await next.Invoke();
        });

        // Security headers on the ADMIN panel only.
        //
        // The panel holds moderation controls, so harden its responses: no framing (anti-
        // clickjacking), no MIME sniffing, no referrer leak of admin URLs, and never cache an
        // authenticated admin page or its JSON. Scoped to /admin so game endpoints - which the
        // client caches aggressively - are untouched. Auth itself is already enforced per
        // request by AuthHelper.ValidateTokenDev (valid signed token AND a real dev flag);
        // these headers are defence-in-depth around that, not a replacement for it.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/admin"))
            {
                var h = context.Response.Headers;
                h["X-Frame-Options"] = "DENY";
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = "no-referrer";
                h["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
                h["Pragma"] = "no-cache";
                h["Content-Security-Policy"] = "frame-ancestors 'none'";
            }
            await next.Invoke();
        });

        app.UseWebSockets();

        app.UseAuthorization();

        app.UseMiddleware<IPAllowMiddleware>();

        // Quest clients get the Android conversions of first-party clothing (quest/<name>).
        app.UseMiddleware<Campus_live_rewrite.Middleware.QuestBundleMiddleware>();

        // app.UseStatusCodePagesWithReExecute("/Web/404");

        var webSocketOptions = new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromMinutes(2)
        };

        app.UseWebSockets(webSocketOptions);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "Data", "Videos")),
            RequestPath = "/cdn/video",
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.Remove("Accept-Ranges");
                var allowedContentTypes = new[] 
                {
                    "video/mp4", // some webm video break recroom video screen and show artifact
                    "video/mkv"
                };
                if (!allowedContentTypes.Contains(ctx.Context.Response.ContentType))
                {
                    ctx.Context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    ctx.Context.Response.Body = Stream.Null;
                }
            }
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(
         Path.Combine(builder.Environment.ContentRootPath, "Data", "CDN", "Configs")
     ),
            RequestPath = "/cdn/config",
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.Remove("Accept-Ranges");
            },
            ServeUnknownFileTypes = true // optional if some files have unknown extensions
        });

        // Third logging layer - dumps full request/response headers per request.
        // Same gate; this is pure overhead during normal play.
        if (ConfigCampus.VerboseRequestLogging)
        {
            app.UseHttpLogging();
        }

        // Campus admin panel: the LoginV4.html / HomeV4.html templates reference
        // /admin-assets/Cutout.png, /admin-assets/Background.png, /admin-assets/AnimatedIcon.png
        // for the Campus branding (purple/orange gradient + tile pattern + Cutout hero).
        // Mount those files from ContentRoot/AdminPanel/Assets - PackageServer.ps1 ships that
        // folder into the deploy tarball alongside Data/ and Web/.
        {
            string adminAssets = Path.Combine(builder.Environment.ContentRootPath, "AdminPanel", "Assets");
            if (Directory.Exists(adminAssets))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(Path.GetFullPath(adminAssets)),
                    RequestPath = "/admin-assets",
                });
                Console.WriteLine($"[admin] assets from {Path.GetFullPath(adminAssets)}");
            }
            else
            {
                Console.WriteLine($"[admin] WARN: AdminPanel/Assets not found at {adminAssets} - Campus branding won't load");
            }
        }

        // College portfolio - a standalone static page at /collegeportfolio, nothing to do with
        // Campus. It is the owner's own coursework (ContentRoot/CollegePortfolio: Portfolio.html
        // plus its audio), served verbatim so the folder can be handed in unchanged.
        //
        // Two details this needs and the other mounts don't:
        //   * a DEFAULT DOCUMENT, because the page is Portfolio.html rather than index.html;
        //   * a redirect from "/collegeportfolio" to "/collegeportfolio/", because the page
        //     references its audio relatively ("drum-beat.wav"). Without the trailing slash the
        //     browser resolves that against the site ROOT and the audio 404s.
        {
            // On the VPS the folder sits next to the binary (the deploy scripts put it there).
            // On a dev box it lives at the REPO root, two levels up - the owner keeps their
            // coursework outside the server project, so the fallback avoids a second copy that
            // could drift from the one they actually hand in.
            string portfolioRoot = Path.Combine(builder.Environment.ContentRootPath, "CollegePortfolio");
            if (!Directory.Exists(portfolioRoot))
            {
                string repoRoot = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "CollegePortfolio");
                if (Directory.Exists(repoRoot)) portfolioRoot = Path.GetFullPath(repoRoot);
            }
            if (Directory.Exists(portfolioRoot))
            {
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.Equals("/collegeportfolio", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.Redirect("/collegeportfolio/" + context.Request.QueryString, permanent: false);
                        return;
                    }
                    await next.Invoke();
                });

                var portfolioFiles = new PhysicalFileProvider(Path.GetFullPath(portfolioRoot));
                app.UseDefaultFiles(new DefaultFilesOptions
                {
                    FileProvider = portfolioFiles,
                    RequestPath = "/collegeportfolio",
                    DefaultFileNames = new List<string> { "Portfolio.html", "index.html" }
                });
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = portfolioFiles,
                    RequestPath = "/collegeportfolio",
                    // Unknown extensions are served rather than 404'd so an asset added to the
                    // folder later (a stem, a project file) does not need a code change.
                    ServeUnknownFileTypes = true,
                    DefaultContentType = "application/octet-stream",
                    OnPrepareResponse = ctx =>
                    {
                        ctx.Context.Response.Headers["Cache-Control"] =
                            ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                ? "no-cache"
                                : "public, max-age=3600";
                    }
                });
                Console.WriteLine($"[portfolio] /collegeportfolio -> {Path.GetFullPath(portfolioRoot)}");
            }
            else
            {
                Console.WriteLine($"[portfolio] WARN: {portfolioRoot} not found - /collegeportfolio will 404");
            }
        }

        // Campus public website, served straight off ContentRoot/Website - this is what used
        // to be the separate Flask process in Website/server.py. Only the web assets were
        // copied across: server.py / build.py / requirements.txt / StartFlask*.bat stayed
        // behind on purpose, because mounting the Flask project folder at the web root would
        // publish its source (and any token.json sitting next to it) as downloadable files.
        //
        // NO UseDefaultFiles here. It would serve index.html for "/" from inside this
        // middleware, before routing runs, and "/" MUST reach WebController.NsRoot or the
        // game client cannot connect. NsRoot serves index.html itself for browsers.
        {
            string siteRoot = Path.Combine(builder.Environment.ContentRootPath, "Website");
            if (Directory.Exists(siteRoot))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(Path.GetFullPath(siteRoot)),
                    RequestPath = "",     // mount at root: /app.js, /style.css, /assets/...
                    OnPrepareResponse = ctx =>
                    {
                        // Pages must not be cached (they change with every deploy); the
                        // fingerprinted css/js/images can be.
                        ctx.Context.Response.Headers["Cache-Control"] =
                            ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                ? "no-cache"
                                : "public, max-age=3600";
                    }
                });
                Console.WriteLine($"[site] serving website from {Path.GetFullPath(siteRoot)}");
            }
            else
            {
                Console.WriteLine($"[site] WARN: {siteRoot} not found - the landing page will 404");
            }
        }

        app.MapControllers();

        // CAMPUS_LOCAL_DEV only (a developer's own copy, started by the launcher's Dev channel): the launcher asks it to shut down cleanly
        // when the game closes - a plain kill could cut a database write short. Loopback only, and not through Caddy.
        if (ConfigCampus.LocalDev)
        {
            app.MapPost("/api/localdev/shutdown", (HttpContext c) =>
            {
                bool loop = c.Connection.RemoteIpAddress != null && System.Net.IPAddress.IsLoopback(c.Connection.RemoteIpAddress)
                            && !c.Request.Headers.ContainsKey("X-Forwarded-For");
                if (!loop) return Results.NotFound();
                Console.WriteLine("[localdev] shutdown requested by the launcher");
                _ = Task.Run(async () => { await Task.Delay(300); app.Lifetime.StopApplication(); });
                return Results.Ok(new { ok = true });
            });
        }

        //PlayerDB.EnsureDormsDontBeStupid(log: true);

        //RoomDB.d(51, 138, "f94c6b386d2b45a0a582f688022aba0c.room");

        //PlayerDB.ImportPlayers(Environment.CurrentDirectory + "/Data/exported_players.json");
        //await RoomDB.ImportRooms(Environment.CurrentDirectory + "/Data/yay.json");
        // Once everything is checked for prod release, uncomment this and import the players and rooms.
        /*
        await Task.Run(() =>
        {
            try
            {
                Webhook.Send(
                1,
                "prod",
                "Server Info",
                "Campus Live server just went online!",
                5898927
                );

            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex.Message}");
            }
        });
        */
        // PlayerDB.SetIsInfluencer(282, true);
        // PlayerDB.SetCreatorCode(282, "idk");
        // RoomDB.FixDuplicateDorms();
        // PlayerDB.EnsureDormsForAllPlayers();
        // PlayerDB.SetPlayerDev(Campus_live_rewrite.api.server.Classes.db_class.DevFlag.none, 3);
        // PlayerDB.ClearRecentRoomsForAllPlayers();
        // Start Discord bot in background — never let a bot fault take the web server down.
        _ = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine("[DiscordBot] Starting...");
                await StartBotAsync(app.Services);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DiscordBot] CRASHED: {ex}");
            }
        });
        // Only start the interactive command console when stdin is attached to a terminal.
        // Under systemd, stdin is /dev/null → ReadLine() returns null immediately and the loop
        // busy-spins, flooding the journal and starving other threads.
        if (!Console.IsInputRedirected)
        {
            new Thread(new ThreadStart(debug_run)).Start();
        }
        else
        {
            Console.WriteLine("[Startup] stdin is redirected — skipping CommandConsole (headless mode).");
        }
        
        PlayerDB.Setup();
        HeartbeatDB.Setup();
        RoomDB.Setup();
        RoomDB.RenameAllDormsToOwners();

        // Load the in-game token kill-switch epoch (admin "Expire all game tokens") so a revoke
        // survives restarts. See ClientSecurity / ConfigCampus.GameTokenValidFrom.
        Campus_live_rewrite.api.server.ClientSecurity.LoadGameTokenRevocation();

        // Index the UGC custom-avatar-item catalog (Data/APIS/items/UGC.json) into memory once, so
        // the customAvatarItems endpoints don't re-parse ~87MB per request. See CustomItemsCache.
        Campus_live_rewrite.api.server.CustomItemsCache.Load();

        // Bulk UGC import on boot: any avatar packs staged in Data/AvatarImportStaging/ (rsync'd
        // there - the no-browser path for a large library) are imported once, then the pack folder
        // is moved to _imported/ so a restart doesn't re-scan it. Assets are copied into AvatarBlobs
        // /Images and each item registered into the dev locker. See AvatarItemImporter.
        try
        {
            string staging = Path.Combine(Environment.CurrentDirectory, "Data", "AvatarImportStaging");
            if (Directory.Exists(staging))
            {
                string done = Path.Combine(staging, "_imported");
                foreach (var pack in Directory.EnumerateDirectories(staging))
                {
                    if (Path.GetFileName(pack).Equals("_imported", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Campus_live_rewrite.api.server.AvatarItemImporter.LooksLikeAvatarPack(pack)) continue;
                    var ar = Campus_live_rewrite.api.server.AvatarItemImporter.ImportDir(pack);
                    Console.WriteLine($"[avatar-staging] {Path.GetFileName(pack)}: {ar.Items} items, {ar.BundlesHosted} bundles, {ar.ThumbsHosted} images, {ar.MissingAssets.Count} missing");
                    try { Directory.CreateDirectory(done); Directory.Move(pack, Path.Combine(done, Path.GetFileName(pack) + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"))); } catch (Exception mv) { Console.WriteLine($"[avatar-staging] move-aside failed for {Path.GetFileName(pack)}: {mv.Message}"); }
                }
            }
        }
        catch (Exception stx) { Console.WriteLine($"[avatar-staging] scan failed: {stx.Message}"); }

        // Bulk ROOM import on boot from Data/RoomImportStaging/ (the no-browser path for big batches -
        // scp/rsync/sftp the files there, no fragile multi-GB browser upload). Handles, per entry:
        //   * a room-export FOLDER (RoomDetails.json + .binpb blobs) -> ImportRoomExport
        //   * a FOLDER of loose .room blobs                          -> wrap each loadable one
        //   * a .zip                    -> extract, then room-export OR loose-.room pack
        //   * a loose .room file        -> wrap it
        // Processed entries move to _imported/. See RoomDB.ImportRoomExport / RoomDB.WrapLooseRoom.
        try
        {
            string rstaging = Path.Combine(Environment.CurrentDirectory, "Data", "RoomImportStaging");
            if (Directory.Exists(rstaging))
            {
                ulong importerId = Campus_live_rewrite.api.dbs.PlayerDB.GetAccountIdByUsernameCI("Importer");
                ulong? owner = importerId == 0 ? (ulong?)null : importerId;
                string done = Path.Combine(rstaging, "_imported");

                async Task ProcessRoomDir(string dir, string label)
                {
                    if (Campus_live_rewrite.api.dbs.RoomDB.LooksLikeRoomExport(dir))
                    {
                        var rr = await Campus_live_rewrite.api.dbs.RoomDB.ImportRoomExport(dir, owner);
                        Console.WriteLine($"[room-staging] {label}: room-export -> {rr.Rooms} room(s) [{string.Join(", ", rr.Names.Take(8))}], {rr.Blobs} blob(s), {rr.Errors.Count} error(s)");
                        foreach (var e in rr.Errors.Take(10)) Console.WriteLine($"[room-staging]   ! {e}");
                    }
                    else
                    {
                        var roomFiles = Directory.EnumerateFiles(dir, "*.room", SearchOption.AllDirectories).ToList();
                        int made = 0, tooNew = 0, other = 0;
                        foreach (var rf in roomFiles)
                        {
                            var (st, id, ver, nm, _) = Campus_live_rewrite.api.dbs.RoomDB.WrapLooseRoom(rf, importerId);
                            if (st == Campus_live_rewrite.api.dbs.RoomDB.LooseWrapStatus.Created) made++;
                            else if (st == Campus_live_rewrite.api.dbs.RoomDB.LooseWrapStatus.TooNew) tooNew++;
                            else other++;
                        }
                        Console.WriteLine($"[room-staging] {label}: {roomFiles.Count} loose .room -> {made} room(s) created, {tooNew} too-new, {other} skipped");
                    }
                }

                // Loose .room files sitting directly in staging.
                foreach (var rf in Directory.EnumerateFiles(rstaging, "*.room", SearchOption.TopDirectoryOnly))
                {
                    var (st, id, ver, nm, err) = Campus_live_rewrite.api.dbs.RoomDB.WrapLooseRoom(rf, importerId);
                    Console.WriteLine($"[room-staging] {Path.GetFileName(rf)}: {st}" + (st == Campus_live_rewrite.api.dbs.RoomDB.LooseWrapStatus.Created ? $" '{nm}' (id {id}, v{ver})" : st == Campus_live_rewrite.api.dbs.RoomDB.LooseWrapStatus.TooNew ? $" (v{ver})" : ""));
                    try { Directory.CreateDirectory(done); Directory.Move(rf, Path.Combine(done, Path.GetFileName(rf))); } catch { }
                }

                // .zip files in staging: extract then process.
                foreach (var zip in Directory.EnumerateFiles(rstaging, "*.zip", SearchOption.TopDirectoryOnly))
                {
                    string ex = Path.Combine(rstaging, Path.GetFileNameWithoutExtension(zip) + "_x" + DateTime.UtcNow.ToString("HHmmss"));
                    try
                    {
                        Directory.CreateDirectory(ex);
                        System.IO.Compression.ZipFile.ExtractToDirectory(zip, ex, overwriteFiles: true);
                        await ProcessRoomDir(ex, Path.GetFileName(zip));
                        try { Directory.CreateDirectory(done); Directory.Move(zip, Path.Combine(done, Path.GetFileName(zip))); } catch { }
                        try { Directory.Delete(ex, true); } catch { }
                    }
                    catch (Exception zx) { Console.WriteLine($"[room-staging] {Path.GetFileName(zip)}: extract/import failed: {zx.Message}"); }
                }

                // Folders (room-export or loose-.room packs).
                foreach (var pack in Directory.EnumerateDirectories(rstaging))
                {
                    if (Path.GetFileName(pack).Equals("_imported", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(pack).Contains("_x", StringComparison.Ordinal) && !Directory.EnumerateFileSystemEntries(pack).Any()) continue;
                    await ProcessRoomDir(pack, Path.GetFileName(pack));
                    try { Directory.CreateDirectory(done); Directory.Move(pack, Path.Combine(done, Path.GetFileName(pack) + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"))); } catch (Exception mv) { Console.WriteLine($"[room-staging] move-aside failed for {Path.GetFileName(pack)}: {mv.Message}"); }
                }
            }
        }
        catch (Exception rex) { Console.WriteLine($"[room-staging] scan failed: {rex.Message}"); }

        // Downgrade already-imported rooms' declared build version to the client's level, so rooms
        // imported before this fix stop failing the client's "older version of Rec Room" gate.
        try
        {
            ulong impId = Campus_live_rewrite.api.dbs.PlayerDB.GetAccountIdByUsernameCI("Importer");
            int clamped = Campus_live_rewrite.api.dbs.RoomDB.ClampImportedRoomsToClient(impId);
            if (clamped > 0) Console.WriteLine($"[room-version] downgraded {clamped} imported room(s) to the client build version");
            int purged = Campus_live_rewrite.api.dbs.RoomDB.RemoveTooNewLooseRooms(impId);
            if (purged > 0) Console.WriteLine($"[room-version] removed {purged} too-new loose-blob room(s) that would freeze the client");
        }
        catch (Exception cvx) { Console.WriteLine($"[room-version] clamp failed: {cvx.Message}"); }

        // End-of-game / quest reward pools (Data/APIS/quest-rewards.json) - keyed by mode + gift
        // context, rolled into a gift box on POST /api/gamerewards/v1/request. See QuestRewards.
        Campus_live_rewrite.api.server.QuestRewards.Load();

        // Weekly-challenge definition (Data/APIS/weekly-challenge.json) served by /api/challenge/v2.
        Campus_live_rewrite.api.server.WeeklyChallenge.Load();

        // ---- LiteDB write-ahead log handling ---------------------------------------------
        // Every LiteDatabase here is a static that is never disposed, so LiteDB's checkpoint
        // (which merges <name>-log.db back into <name>.db) never ran. Data accumulated in the
        // WAL indefinitely - and because install.sh preserves Data/DBs/* across deploys, that
        // WAL was carried into every subsequent build, which is why deploying a known-good
        // server changed nothing. Verified fix: the boot report caught Settings.db at 8KB with
        // a 7.7MB log, and after checkpointing the data files hold the data and the logs are 0.
        DbMaintenance.ReportWalState();

        // Merge the write-ahead logs into the data files every 5 minutes, not just at
        // shutdown. Without this, a hard kill leaves every write since boot recoverable
        // only by replaying the log.
        DbMaintenance.StartAutoCheckpoint(5);

        // Snapshot every LiteDB store 3x/day into Data/Backups, keep 3 days (admin "Database" tab).
        BackupService.Start();

        // Creator earnings: pay the sales whose Sunday has come (EarningsDB).
        Campus_live_rewrite.api.dbs.EarningsDB.Start();

        // The Coach is Level 99 and nobody else can be (Progress.PLAYER_MAX_LEVEL).
        try
        {
            ulong coach = Campus_live_rewrite.api.dbs.PlayerDB.GetAccountIdByUsernameCI(Campus_live_rewrite.api.server.ConfigCampus.Level99Username);
            if (coach != 0)
            {
                var cur = Campus_live_rewrite.api.server.Classes.Progress.GetPlayerLevel(coach);
                if (cur.Level != Campus_live_rewrite.api.server.Classes.Progress.MAX_LEVEL)
                {
                    Campus_live_rewrite.api.dbs.PlayerDB.SetProgression(coach, 0, Campus_live_rewrite.api.server.Classes.Progress.MAX_LEVEL);
                    Console.WriteLine($"[progression] {Campus_live_rewrite.api.server.ConfigCampus.Level99Username} ({coach}) set to Level {Campus_live_rewrite.api.server.Classes.Progress.MAX_LEVEL}");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[progression] level-99 seed skipped: {ex.Message}"); }

        CampusLiveSource.Controllers.NotificationsController.Stopping = app.Lifetime.ApplicationStopping;
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            Console.WriteLine("[Startup] shutting down - checkpointing LiteDB stores ...");
            DbMaintenance.StopAutoCheckpoint();
            BackupService.Stop();
            DbMaintenance.CheckpointAll();
        });

        // ---- Admin seed --------------------------------------------------------------------
        // Idempotent. On every start, make sure there's a Kyle account with dev_Flag=dev and
        // the operator password so the admin panel is always reachable, even after a fresh
        // deploy that ships with an empty Players.db. If the account already exists we DON'T
        // clobber it - operator may have changed the password from the panel.
        try
        {
            var col = Campus_live_rewrite.api.dbs.PlayerDB.PlayerDBFile
                        .GetCollection<Campus_live_rewrite.api.server.Classes.db_class.player_data>("players");
            var existing = col.FindOne(x => x.player != null && x.player.Username == "Kyle");
            if (existing == null)
            {
                // Seed via CreateAccount - random name + starter reputation - then rename to Kyle.
                if (Campus_live_rewrite.api.dbs.PlayerDB.CreateAccount(
                        Campus_live_rewrite.api.server.Classes.db_class.Platforms.All,
                        0UL, "", 0UL, out var seeded))
                {
                    var fresh = col.FindOne(x => x.playerid == seeded.accountId);
                    if (fresh != null && fresh.player != null)
                    {
                        fresh.player.Username = "Kyle";
                        fresh.player.DisplayName = "Kyle";
                        fresh.password = Campus_live_rewrite.api.server.ClientSecurity.HashPassword("VanessaPlease16#");
                        fresh.dev_Flag = Campus_live_rewrite.api.server.Classes.db_class.DevFlag.dev;
                        col.Update(fresh);
                        Console.WriteLine($"[admin-seed] created Kyle (id={fresh.playerid}) with dev flag - login: Kyle / VanessaPlease16#");
                    }
                }
                else
                {
                    Console.WriteLine("[admin-seed] CreateAccount returned false, Kyle NOT seeded");
                }
            }
            else
            {
                bool changed = false;
                if (string.IsNullOrEmpty(existing.password))
                {
                    existing.password = Campus_live_rewrite.api.server.ClientSecurity.HashPassword("VanessaPlease16#");
                    changed = true;
                }
                if ((existing.dev_Flag & Campus_live_rewrite.api.server.Classes.db_class.DevFlag.dev) !=
                     Campus_live_rewrite.api.server.Classes.db_class.DevFlag.dev)
                {
                    existing.dev_Flag |= Campus_live_rewrite.api.server.Classes.db_class.DevFlag.dev;
                    changed = true;
                }
                // Stamp Kyle's owner Discord + Steam IDs.
                existing.player_Extra ??= new Campus_live_rewrite.api.server.Classes.db_class.player_extra();
                if (existing.player_Extra.DiscordId != 1450391092984807445UL)
                {
                    existing.player_Extra.DiscordId = 1450391092984807445UL;
                    changed = true;
                }
                existing.platform = Campus_live_rewrite.api.server.Classes.db_class.Platforms.Steam;
                existing.player.PlatformIds ??= new List<Campus_live_rewrite.api.server.Classes.db_class.mPlatformID>();
                var steamEntry = existing.player.PlatformIds.FirstOrDefault(p => p != null && p.Platform == Campus_live_rewrite.api.server.Classes.db_class.Platforms.Steam);
                if (steamEntry == null)
                {
                    existing.player.PlatformIds.Add(new Campus_live_rewrite.api.server.Classes.db_class.mPlatformID
                    {
                        Platform = Campus_live_rewrite.api.server.Classes.db_class.Platforms.Steam,
                        PlatformId = 76561199059115358UL
                    });
                    changed = true;
                }
                else if (steamEntry.PlatformId != 76561199059115358UL)
                {
                    steamEntry.PlatformId = 76561199059115358UL;
                    changed = true;
                }
                if (changed) { col.Update(existing); Console.WriteLine($"[admin-seed] refreshed Kyle (id={existing.playerid}) - dev flag / discord / steam stamped"); }
                else         { Console.WriteLine($"[admin-seed] Kyle (id={existing.playerid}) already fine - no changes"); }

                // Force skip-orientation flags on Kyle so first login on this build doesn't
                // loop through the broken SocialOrientation load path.
                Campus_live_rewrite.api.dbs.SettingsDB.SetPlayerSetting("TUTORIAL_COMPLETE_MASK",              "4294967295", existing.playerid);
                Campus_live_rewrite.api.dbs.SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasFinished", "true",       existing.playerid);
                Campus_live_rewrite.api.dbs.SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasChosenUsername",  "true", existing.playerid);
                Campus_live_rewrite.api.dbs.SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasCreatedPassword", "true", existing.playerid);
            }
        }
        catch (Exception seedEx)
        {
            Console.WriteLine($"[admin-seed] FAILED: {seedEx.Message}");
        }

        // ---- @Importer seed ----------------------------------------------------------------
        // A second owner-controlled account that HOLDS the imported rooms. It shares Kyle's Steam
        // id and is account-lock exempt (ConfigCampus.LockExemptUsernames), so it can be signed
        // into from the same Steam/device as Kyle (log in with username "Importer"). Idempotent.
        try
        {
            var col = Campus_live_rewrite.api.dbs.PlayerDB.PlayerDBFile
                        .GetCollection<Campus_live_rewrite.api.server.Classes.db_class.player_data>("players");
            var imp = col.FindOne(x => x.player != null && x.player.Username == "Importer");
            if (imp == null &&
                Campus_live_rewrite.api.dbs.PlayerDB.CreateAccount(
                    Campus_live_rewrite.api.server.Classes.db_class.Platforms.All, 0UL, "", 0UL, out var impSeeded))
            {
                imp = col.FindOne(x => x.playerid == impSeeded.accountId);
            }
            if (imp != null && imp.player != null)
            {
                bool changed = false;
                if (imp.player.Username != "Importer") { imp.player.Username = "Importer"; imp.player.DisplayName = "Importer"; changed = true; }
                if (string.IsNullOrEmpty(imp.password)) { imp.password = Campus_live_rewrite.api.server.ClientSecurity.HashPassword("ImporterPlease16#"); changed = true; }
                imp.platform = Campus_live_rewrite.api.server.Classes.db_class.Platforms.Steam;
                imp.player.PlatformIds ??= new List<Campus_live_rewrite.api.server.Classes.db_class.mPlatformID>();
                var s = imp.player.PlatformIds.FirstOrDefault(p => p != null && p.Platform == Campus_live_rewrite.api.server.Classes.db_class.Platforms.Steam);
                if (s == null) { imp.player.PlatformIds.Add(new Campus_live_rewrite.api.server.Classes.db_class.mPlatformID { Platform = Campus_live_rewrite.api.server.Classes.db_class.Platforms.Steam, PlatformId = 76561199059115358UL }); changed = true; }
                else if (s.PlatformId != 76561199059115358UL) { s.PlatformId = 76561199059115358UL; changed = true; }
                if (changed) col.Update(imp);
                Campus_live_rewrite.api.dbs.SettingsDB.SetPlayerSetting("TUTORIAL_COMPLETE_MASK",              "4294967295", imp.playerid);
                Campus_live_rewrite.api.dbs.SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasFinished",         "true", imp.playerid);
                Campus_live_rewrite.api.dbs.SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasChosenUsername",   "true", imp.playerid);
                Campus_live_rewrite.api.dbs.SettingsDB.SetPlayerSetting("Recroom.AccountCreation.HasCreatedPassword",  "true", imp.playerid);
                Console.WriteLine($"[admin-seed] Importer ready (id={imp.playerid}) - login: Importer / ImporterPlease16#");
            }
        }
        catch (Exception impEx) { Console.WriteLine($"[admin-seed] Importer seed FAILED: {impEx.Message}"); }

        // Cheer badges that should never have been accepted: a number that is not a real badge, or the Rec Room Developer badge on an
        // account that is not a developer (SetSelectedCheer used to store whatever number a client posted). Reset those to "no badge".
        try
        {
            var repCol = Campus_live_rewrite.api.dbs.PlayerDB.PlayerDBFile
                           .GetCollection<Campus_live_rewrite.api.server.Classes.db_class.player_data>("players");
            int cleaned = 0;
            foreach (var pl in repCol.FindAll().ToList())
            {
                var rep = pl.player?.PlayerReputation;
                if (rep == null) continue;
                if (!Campus_live_rewrite.api.dbs.PlayerDB.MaySelectCheer(rep.SelectedCheer, pl.playerid))
                {
                    Console.WriteLine($"[cheer] account {pl.playerid} had selected cheer {(int)rep.SelectedCheer} - reset to none");
                    rep.SelectedCheer = Campus_live_rewrite.api.server.Classes.db_class.CheerCategoryEnum.None;
                    repCol.Update(pl);
                    cleaned++;
                }
            }
            if (cleaned > 0) Console.WriteLine($"[cheer] {cleaned} invalid selected cheer badge(s) reset");
        }
        catch (Exception cheerEx) { Console.WriteLine($"[cheer] badge cleanup FAILED: {cheerEx.Message}"); }

        // Mirror each Rooms 2.0 room's Accessibility onto its primary subroom's, so existing
        // published rooms appear per their real visibility (they set it on the subroom, but
        // Search/hot read the room-level field).
        try { Campus_live_rewrite.api.dbs.RoomDB.FixPlayerRoomFlags(); }
        catch (Exception _fEx) { Console.WriteLine("[rooms] player-room flag fix skipped: " + _fEx.Message); }
        try
        {
            int _synced = Campus_live_rewrite.api.dbs.RoomDB.SyncAllRoomAccessibility();
            if (_synced > 0) Console.WriteLine($"[rooms] synced room-level accessibility from subrooms for {_synced} room(s)");
        }
        catch (Exception _rEx) { Console.WriteLine("[rooms] accessibility sync skipped: " + _rEx.Message); }
        try
        {
            int _pres = Campus_live_rewrite.api.dbs.HeartbeatDB.FixOutOfRangePresence();
            if (_pres > 0) Console.WriteLine($"[presence] repaired {_pres} presence row(s) with out-of-range values");
        }
        catch (Exception _pEx) { Console.WriteLine("[presence] repair skipped: " + _pEx.Message); }
        // Room kicks were stored as permanent room bans ("Kicked by X"); those are lifted once.
        try
        {
            int _kicks = Campus_live_rewrite.api.dbs.RoomDB.LiftKickBans();
            Campus_live_rewrite.api.server.BundleHashes.WarmUp();   // item definitions carry the hash of the bundle we actually serve
            if (_kicks > 0) Console.WriteLine($"[rooms] lifted {_kicks} room ban(s) that were really kicks");
        }
        catch (Exception _kEx) { Console.WriteLine("[rooms] kick-ban lift skipped: " + _kEx.Message); }
        try
        {
            int _orig = Campus_live_rewrite.api.dbs.RoomDB.LiftOriginalRoomBans();
            if (_orig > 0) Console.WriteLine($"[rooms] lifted {_orig} room ban(s) on Rec Room Originals");
        }
        catch (Exception _oEx2) { Console.WriteLine("[rooms] original-room ban lift skipped: " + _oEx2.Message); }

        // "Random bans": a Role_data.Banned entry with no active RoomBans record would still deny
        // entry (the join gates read the role cache). Sweep those orphans so they don't stick.
        try
        {
            int _orphans = Campus_live_rewrite.api.dbs.RoomDB.SweepOrphanBannedRoles();
            if (_orphans > 0) Console.WriteLine($"[rooms] cleared {_orphans} orphan Banned role row(s) (no active ban record)");
        }
        catch (Exception _oEx) { Console.WriteLine("[rooms] orphan ban sweep skipped: " + _oEx.Message); }

        // Resolve the account-lock exemptions (Coach/Kyle -> their current account ids) now that
        // the admin-seed has run, so the owner's test accounts can share one Steam/device.
        // LiteDB 5's BsonMapper builds an entity's member map lazily and registers the map
        // BEFORE filling it; two requests hitting a not-yet-mapped type at once made the second
        // see an empty map and throw "Member X not found on BsonMapper for type Y" (seen on
        // ImageMetadataDB+Cheer, ClubDB+Club, ClubDB+ClubMember - random 500s on first use).
        // Build every mapper here, single-threaded, before Kestrel accepts a request.
        try
        {
            int warmed = 0;
            foreach (var t in typeof(Program).Assembly.GetTypes())
            {
                if (!t.IsClass || t.IsAbstract || t.IsGenericTypeDefinition || t.IsNested && !t.IsNestedPublic && !t.IsNestedPrivate) continue;
                // The global-namespace stores (ClubDB, ImageMetadataDB, InventionDB, InventionStore...)
                // were skipped here, which is exactly where the race kept firing.
                if (t.Namespace != null && !(t.Namespace.StartsWith("Campus_live_rewrite.api.dbs") || t.Namespace.StartsWith("Campus_live_rewrite.api.server"))) continue;
                if (t.Name.EndsWith("Controller") || t.Name.EndsWith("Middleware")) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                try { LiteDB.BsonMapper.Global.ToDocument(t, Activator.CreateInstance(t)!); warmed++; } catch { }
            }
            Console.WriteLine($"[litedb] pre-built {warmed} entity mappers");
        }
        catch (Exception ex) { Console.WriteLine($"[litedb] mapper warm-up failed: {ex.Message}"); }

        // Nobody runs Orientation (ConfigCampus.SkipOrientation). Existing accounts that were
        // parked mid-flow - tutorial mask 0, AccountCreation.* false - are settled here, once, so
        // they stop being dropped back into it on login.
        if (Campus_live_rewrite.api.server.ConfigCampus.SkipOrientation)
        {
            try
            {
                int _skipped = Campus_live_rewrite.api.dbs.SettingsDB.SkipOrientationForAllPlayers();
                if (_skipped > 0) Console.WriteLine($"[orientation] marked complete for {_skipped} existing account(s)");
            }
            catch (Exception _oEx) { Console.WriteLine("[orientation] backfill skipped: " + _oEx.Message); }
        }

        try { Campus_live_rewrite.api.server.UploadGuard.LoadPenalties(); }
        catch (Exception ex) { Console.WriteLine($"[upload-guard] load failed: {ex.Message}"); }
        if (Campus_live_rewrite.api.server.ConfigCampus.UploadSweepOnStartup)
        {
            // A minute after boot so it never competes with startup; quarantines, never deletes.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                try { Campus_live_rewrite.api.server.UploadGuard.SweepStorage(dryRun: Campus_live_rewrite.api.server.ConfigCampus.UploadSweepDryRun); }
                catch (Exception ex) { Console.WriteLine($"[upload-guard] sweep failed: {ex.Message}"); }
            });
        }
        Campus_live_rewrite.api.server.CdnHost.Start();
        try { Campus_live_rewrite.api.server.AccountLock.ResolveExemptions(); }
        catch (Exception _lEx) { Console.WriteLine("[account-lock] exemption resolve skipped: " + _lEx.Message); }


        app.Run();
        //await botTask;
    }
    public static async Task StartBotAsync(IServiceProvider services)
    {
        var bot = services.GetRequiredService<DiscordBot>();
        await bot.RunAsync();
    }

    public static void debug_run()
    {
        new CommandConsole();
    }
}