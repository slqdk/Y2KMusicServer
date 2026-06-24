using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// First-run database setup. Creates the schema (no migrations yet — current
/// instances are greenfield, with no deployed database to evolve and the only
/// durable data arriving later via the legacy import) and seeds the 14
/// categories plus the singleton <see cref="Settings"/> row when absent.
/// Idempotent: safe to run on every startup.
/// </summary>
public static class DbInitializer
{
    private static readonly string[] BuiltInCategories =
        { "Pop", "Rock", "Metal", "Dance", "Techno", "Country", "Classical" };

    public static void Initialize(IServiceProvider services, IConfiguration cfg)
    {
        var factory = services.GetRequiredService<IDbContextFactory<Y2KDbContext>>();
        using var db = factory.CreateDbContext();

        db.Database.EnsureCreated();

        SeedCategories(db);
        SeedSettings(db, cfg);

        db.SaveChanges();
    }

    private static void SeedCategories(Y2KDbContext db)
    {
        if (db.Categories.Any()) return;

        var order = 0;

        foreach (var name in BuiltInCategories)
            db.Categories.Add(new Category
            {
                Name = name,
                IsCustom = false,
                Enabled = false,
                DisplayOrder = order++
            });

        for (var i = 1; i <= 7; i++)
            db.Categories.Add(new Category
            {
                Name = $"Custom{i}",
                IsCustom = true,
                Enabled = false,
                DisplayOrder = order++
            });
    }

    private static void SeedSettings(Y2KDbContext db, IConfiguration cfg)
    {
        if (db.Settings.Any()) return;

        // The install-time default LUFS lives in appsettings (Defaults:TargetLufs);
        // fall back to the legacy -18 target if it is unset.
        var targetLufs = cfg.GetValue<double?>("Defaults:TargetLufs") ?? -18.0;

        // Defaults match the legacy WinForms app's fresh-install state.
        db.Settings.Add(new Settings
        {
            Id = 1,
            SmartMix = true,
            SmartBeatFader = false,
            NextTriggerPct = 50,
            NextFadeSeconds = 6,
            AutoDj = true,
            AutoDjTracks = 3,
            AutoDjBpmDev = 5,
            ScanWorkers = 4,
            NormalizeEnabled = true,
            LimiterEnabled = true,
            TargetLufs = targetLufs,
            Volume = 80,
            StreamingEnabled = false,
            StreamingBitrate = 128,
            AllowWebNext = false,
            ShowWebCategories = true,
            DebugLogging = false,
            UpdateUrl = null
        });
    }
}
