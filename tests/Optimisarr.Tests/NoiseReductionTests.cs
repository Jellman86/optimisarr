using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class NoiseReductionTests
{
    [Fact]
    public void Off_adds_no_filter() => Assert.Null(NoiseReductionFilter.For(NoiseReduction.Off));

    [Fact]
    public void Strong_cleans_harder_than_light_and_colour_harder_than_brightness()
    {
        static double[] Strengths(NoiseReduction level) => NoiseReductionFilter.For(level)!["hqdn3d=".Length..].Split(':')
            .Select(part => double.Parse(part.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var light = Strengths(NoiseReduction.Light);
        var strong = Strengths(NoiseReduction.Strong);

        Assert.All(light.Zip(strong), pair => Assert.True(pair.Second > pair.First));
        Assert.True(light[1] > light[0] && light[3] > light[2]);
    }

    [Theory]
    [InlineData(NoiseReduction.Off, MediaKind.Video, false)]
    [InlineData(NoiseReduction.Light, MediaKind.Video, true)]
    [InlineData(NoiseReduction.Strong, MediaKind.Audio, false)]
    [InlineData(NoiseReduction.Strong, MediaKind.Image, false)]
    public void Only_video_that_is_cleaned_stays_on_the_server(NoiseReduction level, MediaKind kind, bool stays) =>
        Assert.Equal(stays, NoiseReductionFilter.KeepsOnServer(level, kind));

    [Fact]
    public void Libraries_saved_before_noise_reduction_existed_upgrade_to_off()
    {
        var root = Directory.CreateTempSubdirectory("optimisarr-noise-migration-").FullName;
        try
        {
            var options = new DbContextOptionsBuilder<Optimisarr.Data.OptimisarrDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "noise.db")};Pooling=False").Options;
            using var db = new Optimisarr.Data.OptimisarrDbContext(options);
            db.Database.Migrate();
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "noise.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT dflt_value FROM pragma_table_info('Libraries') WHERE name = 'NoiseReduction'";
            Assert.Equal("'Off'", command.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
