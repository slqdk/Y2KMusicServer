using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Diagnostics;
using Y2KMusicServer.Server.Hubs;
using Y2KMusicServer.Server.Network;
using Y2KMusicServer.Server.Playback;
using Y2KMusicServer.Server.Scanning;
using Y2KMusicServer.Server.Streaming;
using Y2KMusicServer.Server.Updates;

namespace Y2KMusicServer.Server;

public static class Program
{
    public static void Main(string[] args)
    {
        // ── Resolve data + log paths from config BEFORE host startup so
        //    Serilog can log to disk from the first line. The default
        //    appsettings.json points at C:\ProgramData\Y2KMusicServer;
        //    appsettings.Development.json overrides to a local folder.
        var bootstrapConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var logPath = bootstrapConfig["LogPath"] ?? "logs";
        Directory.CreateDirectory(logPath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logPath, "y2k-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            Log.Information("Y2K Music Server starting up. Version {Version}",
                Assembly.GetExecutingAssembly().GetName().Version);

            // Live log plumbing, created before the host so the Serilog pipeline
            // and the DI container share the same instances. The ring buffer backs
            // the admin log feed (and the logs endpoint); the verbosity switch is
            // flipped at runtime by the Settings.DebugLogging toggle.
            var logBuffer = new LogRingBuffer();
            var logVerbosity = new LogVerbositySwitch();

            var builder = WebApplication.CreateBuilder(args);

            // Run as Windows Service when launched by the SCM.
            builder.Host.UseWindowsService(opts =>
            {
                opts.ServiceName = "Y2KMusicServer";
            });

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                // DebugLogging drives this at runtime: Debug = verbose, else Information.
                .MinimumLevel.ControlledBy(logVerbosity.Switch)
                // Keep framework chatter (EF Core SQL, ASP.NET, SignalR) out of the
                // operator log even when verbose is on — only our own logs go to Debug.
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(
                    Path.Combine(ctx.Configuration["LogPath"] ?? "logs", "y2k-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                // Mirror every event into the in-memory ring for the admin log feed.
                .WriteTo.Sink(new RingBufferSink(logBuffer)));

            // EF Core + SQLite. The engine services and controllers mint a
            // context per logical operation via IDbContextFactory and dispose
            // it promptly — never hold one for the lifetime of a singleton.
            // The db lives at {DataPath}/data/y2k.db (ensured to exist here).
            var dbPath = DataPaths.EnsureDbPath(builder.Configuration);
            builder.Services.AddDbContextFactory<Y2KDbContext>(opts =>
                opts.UseSqlite($"Data Source={dbPath}"));

            // SignalR for live updates to the admin page.
            builder.Services.AddSignalR();

            // The update checker is a singleton; a hosted service triggers
            // periodic checks. Both go into DI here so the admin
            // controller can read the latest cached UpdateInfoDto.
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<GitHubUpdateChecker>();
            builder.Services.AddHostedService<UpdateCheckScheduler>();

            // Network-share credentials + connector: lets the LocalSystem service
            // authenticate to SMB shares (stored per host, DPAPI-encrypted on disk)
            // so it can read music folders on a NAS. The connector wraps the Win32
            // WNetAddConnection2 API; see NetworkShareConnector / NetworkShareStore.
            builder.Services.AddSingleton<NetworkShareConnector>();

            // On startup, re-authenticate to the servers behind any network
            // category folders (sessions don't survive a restart), so playback /
            // analysis / scanning can read network-stored tracks from boot.
            builder.Services.AddHostedService<NetworkShareReconnectService>();

            // Library scanner (engine service) + the broadcaster that forwards
            // its progress events to the SignalR hub.
            builder.Services.AddSingleton<LibraryScanner>();
            builder.Services.AddHostedService<ScanHubBroadcaster>();

            // Audio engine (single-deck for now) + the broadcaster that
            // forwards now-playing / progress / VU events to the SignalR hub.
            builder.Services.AddSingleton<AudioEngine>();
            builder.Services.AddHostedService<PlaybackBroadcaster>();

            // Live logging surface: the shared ring buffer + verbosity switch, the
            // broadcaster that pushes each line to the admin page (logEntry), and
            // the engine-event -> activity-log translator (track starts + crossfades).
            builder.Services.AddSingleton(logBuffer);
            builder.Services.AddSingleton(logVerbosity);
            builder.Services.AddHostedService<LogHubBroadcaster>();
            builder.Services.AddHostedService<PlaybackLogger>();

            // Live BPM verification: on each crossfade, re-detect the outgoing
            // deck's tempo at the mix point and log it vs the stored value
            // (verbose/Debug only — see BpmCheckService).
            builder.Services.AddHostedService<BpmCheckService>();

            // Background library tempo sweep: while verbose is on, re-detects each
            // stored-BPM track and flags suspected octave errors / mismatches at
            // Warning (verbose-gated — see BpmRecheckService).
            builder.Services.AddHostedService<BpmRecheckService>();

            // Live broadcast. One StreamingEncoder instance serves both the DI
            // graph (StreamController) and the host (it runs the distribute
            // thread and subscribes to the engine's tap changes in StartAsync).
            builder.Services.AddSingleton<StreamingEncoder>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamingEncoder>());

            // Auto DJ. PlaylistService is the singleton that owns the playlist
            // table + the track selector + the in-memory play history; the
            // scheduler is the loop that chains the engine and triggers top-ups.
            builder.Services.AddSingleton<PlaylistService>();
            builder.Services.AddHostedService<AutoDjScheduler>();

            // Audio analysis pass (Phase 5): fills loudness (and later BPM/beats).
            // Singleton service + a broadcaster that forwards its progress to the hub.
            builder.Services.AddSingleton<AudioAnalysisService>();
            builder.Services.AddHostedService<AnalysisHubBroadcaster>();

            // Keeps the analysis columns filled automatically so the operator
            // never runs a pass by hand: a missing-only pass is kicked after each
            // scan completes, and once at startup if any track still lacks
            // BPM/loudness. Reuses AudioAnalysisService — see AnalysisAutoFillService.
            builder.Services.AddHostedService<AnalysisAutoFillService>();

            // Phase 0 only: a heartbeat that proves the SignalR wire
            // from server to admin page is alive before any real audio
            // engine ships. Replace with real-engine events later.
            builder.Services.AddHostedService<PlaybackHeartbeat>();

            // Controllers
            builder.Services.AddControllers();

            var app = builder.Build();

            // Create the schema on first run and seed categories + settings.
            DbInitializer.Initialize(app.Services, app.Configuration);

            // Apply the persisted "verbose logging" preference to the live switch.
            try
            {
                using var scope = app.Services.CreateScope();
                var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<Y2KDbContext>>();
                using var db = dbf.CreateDbContext();
                bool debug = db.Settings.AsNoTracking().FirstOrDefault()?.DebugLogging ?? false;
                logVerbosity.SetDebug(debug);
                Log.Information("Verbose logging {State} at startup (Settings.DebugLogging).",
                    debug ? "ON" : "OFF");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read DebugLogging at startup; logging stays at Information.");
            }

            // Per-request access log ([WebServer]) — Debug-gated, so it only
            // appears when verbose logging is on. Registered first so the
            // elapsed time and status reflect the whole request pipeline.
            app.UseMiddleware<RequestLoggingMiddleware>();

            // Serve the React frontend from wwwroot. In Phase 0
            // wwwroot is empty (no real frontend build yet); the
            // MapFallback below detects that and serves a
            // placeholder landing page instead. Once Phase 4
            // builds the real frontend and publish.ps1 drops
            // index.html into wwwroot, UseDefaultFiles +
            // UseStaticFiles take over and SPA deep-links fall
            // back to that index.html. The placeholder evaporates
            // naturally — no flag-flip needed.
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.UseRouting();
            app.MapControllers();
            app.MapHub<PlaybackHub>("/hub/playback");

            // Liveness probe used by the installer to verify the service
            // started cleanly.
            app.MapGet("/health", () => Results.Ok(new { ok = true }));

            // SPA fallback. Anything not matched by static files,
            // controllers, hubs, or /health lands here.
            app.MapFallback(async ctx =>
            {
                var indexPath = Path.Combine(app.Environment.WebRootPath, "index.html");
                if (File.Exists(indexPath))
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.SendFileAsync(indexPath);
                }
                else
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync(
                        Phase0Placeholder.Html(ctx.Request.Path));
                }
            });

            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
