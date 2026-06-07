using Microsoft.EntityFrameworkCore;

namespace Optimisarr.Data;

public sealed class OptimisarrDbContext(DbContextOptions<OptimisarrDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<Library> Libraries => Set<Library>();

    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(160);
            entity.Property(setting => setting.Value).IsRequired();
        });

        modelBuilder.Entity<Library>(entity =>
        {
            entity.HasKey(library => library.Id);
            entity.Property(library => library.Name).IsRequired().HasMaxLength(160);
            entity.Property(library => library.Path).IsRequired().HasMaxLength(1024);
            entity.HasIndex(library => library.Path).IsUnique();
            entity.Property(library => library.MediaType).HasConversion<string>().HasMaxLength(32);
            entity.Property(library => library.RuleProfile).HasConversion<string>().HasMaxLength(32);
            entity.Property(library => library.HdrHandling).HasConversion<string>().HasMaxLength(32);
            entity.Property(library => library.TargetVideoCodec).HasMaxLength(64);
            entity.Property(library => library.TargetContainer).HasMaxLength(32);
            entity.Property(library => library.ExcludePaths).HasMaxLength(2048);
        });

        modelBuilder.Entity<MediaFile>(entity =>
        {
            entity.HasKey(file => file.Id);
            entity.Property(file => file.Path).IsRequired().HasMaxLength(1024);
            entity.HasIndex(file => file.Path).IsUnique();
            entity.Property(file => file.RelativePath).HasMaxLength(1024);
            entity.Property(file => file.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(file => file.Container).HasMaxLength(32);
            entity.Property(file => file.VideoCodec).HasMaxLength(64);
            entity.Property(file => file.AudioCodecs).HasMaxLength(256);

            // Removing a library removes its inventory; orphan media files make no sense.
            entity.HasOne(file => file.Library)
                .WithMany(library => library.MediaFiles)
                .HasForeignKey(file => file.LibraryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(file => file.LibraryId);
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.Property(job => job.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(job => job.WorkOutputPath).HasMaxLength(1024);

            // Deleting a media file (e.g. via its library) removes its jobs too.
            entity.HasOne(job => job.MediaFile)
                .WithMany()
                .HasForeignKey(job => job.MediaFileId)
                .OnDelete(DeleteBehavior.Cascade);

            // The scheduler queries by status and orders by priority then enqueue time.
            entity.HasIndex(job => job.Status);
            entity.HasIndex(job => new { job.Priority, job.EnqueuedAt });
        });
    }
}
