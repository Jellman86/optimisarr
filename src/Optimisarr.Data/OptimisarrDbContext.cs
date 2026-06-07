using Microsoft.EntityFrameworkCore;

namespace Optimisarr.Data;

public sealed class OptimisarrDbContext(DbContextOptions<OptimisarrDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(160);
            entity.Property(setting => setting.Value).IsRequired();
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
        });
    }
}
