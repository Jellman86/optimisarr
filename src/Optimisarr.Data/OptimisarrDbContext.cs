using Microsoft.EntityFrameworkCore;

namespace Optimisarr.Data;

public sealed class OptimisarrDbContext(DbContextOptions<OptimisarrDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<Library> Libraries => Set<Library>();

    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<Replacement> Replacements => Set<Replacement>();

    public DbSet<ActivityWatcher> ActivityWatchers => Set<ActivityWatcher>();

    public DbSet<NotificationTarget> NotificationTargets => Set<NotificationTarget>();

    public DbSet<ArrConnection> ArrConnections => Set<ArrConnection>();

    public DbSet<Exclusion> Exclusions => Set<Exclusion>();

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
            entity.Property(library => library.ImageDownscaleMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(library => library.TargetVideoCodec).HasMaxLength(64);
            entity.Property(library => library.TargetContainer).HasMaxLength(32);
            entity.Property(library => library.ExcludePaths).HasMaxLength(2048);
            entity.Property(library => library.EncoderPreset).HasMaxLength(32);
            entity.Property(library => library.AudioTargetCodec).HasMaxLength(32);
            entity.Property(library => library.KeepAudioLanguages).HasMaxLength(256);
            entity.Property(library => library.TargetImageFormat).HasMaxLength(32);
            entity.Property(library => library.TargetFolder).HasMaxLength(1024);
        });

        modelBuilder.Entity<MediaFile>(entity =>
        {
            entity.HasKey(file => file.Id);
            entity.Property(file => file.Path).IsRequired().HasMaxLength(1024);
            entity.HasIndex(file => file.Path).IsUnique();
            entity.Property(file => file.RelativePath).HasMaxLength(1024);
            entity.Property(file => file.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(file => file.MediaKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(file => file.Container).HasMaxLength(32);
            entity.Property(file => file.VideoCodec).HasMaxLength(64);
            entity.Property(file => file.VideoProfile).HasMaxLength(64);
            entity.Property(file => file.PixelFormat).HasMaxLength(32);
            entity.Property(file => file.AudioCodecs).HasMaxLength(256);
            entity.Property(file => file.AudioLanguages).HasMaxLength(256);

            // Removing a library removes its inventory; orphan media files make no sense.
            entity.HasOne(file => file.Library)
                .WithMany(library => library.MediaFiles)
                .HasForeignKey(file => file.LibraryId)
                .OnDelete(DeleteBehavior.Cascade);
            // Serves both a library filter (leftmost prefix) and the inventory list's order-by-path,
            // so a large library pages without a table sort.
            entity.HasIndex(file => new { file.LibraryId, file.RelativePath });
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.Property(job => job.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(job => job.Type).HasConversion<string>().HasMaxLength(16);
            entity.Property(job => job.FailureCategory).HasConversion<string>().HasMaxLength(32);
            entity.Property(job => job.WorkOutputPath).HasMaxLength(1024);
            entity.Property(job => job.VideoEncoder).HasMaxLength(64);
            entity.Property(job => job.VideoQualityMode).HasMaxLength(16);

            // Deleting a media file (e.g. via its library) removes its jobs too.
            entity.HasOne(job => job.MediaFile)
                .WithMany()
                .HasForeignKey(job => job.MediaFileId)
                .OnDelete(DeleteBehavior.Cascade);

            // The scheduler queries by status and orders by priority then enqueue time.
            entity.HasIndex(job => job.Status);
            entity.HasIndex(job => new { job.Priority, job.EnqueuedAt });
            entity.HasIndex(job => job.CalibrationSessionId);
        });

        modelBuilder.Entity<Replacement>(entity =>
        {
            entity.HasKey(replacement => replacement.Id);
            entity.Property(replacement => replacement.OriginalPath).IsRequired().HasMaxLength(1024);
            entity.Property(replacement => replacement.QuarantinePath).IsRequired().HasMaxLength(1024);
            entity.Property(replacement => replacement.FinalPath).IsRequired().HasMaxLength(1024);
            entity.Property(replacement => replacement.Status).HasConversion<string>().HasMaxLength(32);

            // A replacement records destructive moves; keep it even if the job row is
            // removed, so quarantine and rollback history is never silently lost.
            entity.HasOne(replacement => replacement.Job)
                .WithMany()
                .HasForeignKey(replacement => replacement.JobId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(replacement => replacement.Status);
        });

        modelBuilder.Entity<ActivityWatcher>(entity =>
        {
            entity.HasKey(watcher => watcher.Id);
            entity.Property(watcher => watcher.Name).IsRequired().HasMaxLength(160);
            entity.Property(watcher => watcher.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(watcher => watcher.BaseUrl).IsRequired().HasMaxLength(1024);
            entity.Property(watcher => watcher.ApiToken).HasMaxLength(512);
        });

        modelBuilder.Entity<NotificationTarget>(entity =>
        {
            entity.HasKey(target => target.Id);
            entity.Property(target => target.Name).IsRequired().HasMaxLength(160);
            entity.Property(target => target.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(target => target.Url).IsRequired().HasMaxLength(1024);
            entity.Property(target => target.Token).HasMaxLength(512);
        });

        modelBuilder.Entity<ArrConnection>(entity =>
        {
            entity.HasKey(connection => connection.Id);
            entity.Property(connection => connection.Name).IsRequired().HasMaxLength(160);
            entity.Property(connection => connection.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(connection => connection.BaseUrl).IsRequired().HasMaxLength(1024);
            entity.Property(connection => connection.ApiKey).HasMaxLength(512);
        });

        modelBuilder.Entity<Exclusion>(entity =>
        {
            entity.HasKey(exclusion => exclusion.Id);
            entity.Property(exclusion => exclusion.Path).IsRequired().HasMaxLength(1024);
            // One exclusion per file; the unique path is also what makes an exclusion durable
            // across re-scans and library re-adds.
            entity.HasIndex(exclusion => exclusion.Path).IsUnique();
            entity.Property(exclusion => exclusion.RelativePath).HasMaxLength(1024);
            entity.Property(exclusion => exclusion.Reason).HasMaxLength(512);
            entity.Property(exclusion => exclusion.Source).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(exclusion => exclusion.LibraryId);
        });
    }
}
