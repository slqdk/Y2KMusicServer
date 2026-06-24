using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// The EF Core context backing the SQLite database at
/// <c>{DataPath}/data/y2k.db</c>. Created via <see cref="IDbContextFactory{TContext}"/>;
/// never held for the lifetime of a singleton service.
/// </summary>
public sealed class Y2KDbContext : DbContext
{
    public Y2KDbContext(DbContextOptions<Y2KDbContext> options) : base(options) { }

    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryFolder> CategoryFolders => Set<CategoryFolder>();
    public DbSet<CategorySlot> CategorySlots => Set<CategorySlot>();
    public DbSet<PlaylistEntry> PlaylistEntries => Set<PlaylistEntry>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<MixCache> MixCache => Set<MixCache>();
    public DbSet<Settings> Settings => Set<Settings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Track>(e =>
        {
            e.Property(t => t.FilePath).IsRequired();
            e.HasIndex(t => t.FilePath).IsUnique();
            e.HasOne(t => t.Category)
                .WithMany()
                .HasForeignKey(t => t.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Category>(e =>
        {
            e.Property(c => c.Name).IsRequired();
            e.HasMany(c => c.Folders)
                .WithOne(f => f.Category!)
                .HasForeignKey(f => f.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Slots)
                .WithOne(s => s.Category!)
                .HasForeignKey(s => s.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CategorySlot>()
            .HasIndex(s => new { s.CategoryId, s.SlotIndex })
            .IsUnique();

        b.Entity<PlaylistEntry>(e =>
        {
            e.Property(p => p.Source).HasConversion<string>();
            e.HasOne(p => p.Track)
                .WithMany()
                .HasForeignKey(p => p.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Request>(e =>
        {
            e.Property(r => r.Status).HasConversion<string>();
            e.HasOne(r => r.Track)
                .WithMany()
                .HasForeignKey(r => r.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MixCache>(e =>
        {
            e.HasIndex(m => new { m.FromTrackId, m.ToTrackId }).IsUnique();
            // Two FKs to Tracks from one row — Restrict avoids ambiguous cascade
            // paths and keeps the cache around until explicitly cleaned up.
            e.HasOne<Track>()
                .WithMany()
                .HasForeignKey(m => m.FromTrackId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Track>()
                .WithMany()
                .HasForeignKey(m => m.ToTrackId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
