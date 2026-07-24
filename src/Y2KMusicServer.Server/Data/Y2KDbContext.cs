using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// The EF Core context backing the SQLite database at
/// <c>{DataPath}/data/y2k.db</c>. Created via <see cref="IDbContextFactory{TContext}"/>;
/// never held for the lifetime of a singleton service.
/// Schema v2: the category model is gone (one global folder list feeds a flat
/// library, filtered by format / genre / decade at query time) and saved
/// playlists carry the schedule slots Auto DJ runs on. See DbInitializer for
/// the recreate-on-old-schema rule.
/// </summary>
public sealed class Y2KDbContext : DbContext
{
    public Y2KDbContext(DbContextOptions<Y2KDbContext> options) : base(options) { }

    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<SavedPlaylist> SavedPlaylists => Set<SavedPlaylist>();
    public DbSet<SavedPlaylistTrack> SavedPlaylistTracks => Set<SavedPlaylistTrack>();
    public DbSet<SavedPlaylistSlot> SavedPlaylistSlots => Set<SavedPlaylistSlot>();
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
        });

        b.Entity<SavedPlaylist>(e =>
        {
            e.Property(p => p.Name).IsRequired();
            e.HasMany(p => p.Tracks)
                .WithOne(t => t.Playlist!)
                .HasForeignKey(t => t.SavedPlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Slots)
                .WithOne(s => s.Playlist!)
                .HasForeignKey(s => s.SavedPlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SavedPlaylistTrack>(e =>
        {
            e.HasIndex(t => new { t.SavedPlaylistId, t.Position });
            e.HasOne(t => t.Track)
                .WithMany()
                .HasForeignKey(t => t.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SavedPlaylistSlot>()
            .HasIndex(s => new { s.SavedPlaylistId, s.SlotIndex })
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
