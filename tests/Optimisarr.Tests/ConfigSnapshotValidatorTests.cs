using Optimisarr.Core.Settings;

namespace Optimisarr.Tests;

public sealed class ConfigSnapshotValidatorTests
{
    private static readonly IReadOnlySet<string> AllowedKeys =
        new HashSet<string> { "queue.maxConcurrentJobs", "queue.encoderMode" };

    [Fact]
    public void A_minimal_snapshot_is_valid()
    {
        var result = ConfigSnapshotValidator.Validate(Empty(), AllowedKeys);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void A_newer_version_is_rejected()
    {
        var snapshot = Empty() with { Version = ConfigSnapshot.CurrentVersion + 1 };

        var result = ConfigSnapshotValidator.Validate(snapshot, AllowedKeys);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("newer Optimisarr"));
    }

    [Fact]
    public void An_unknown_setting_key_is_rejected()
    {
        var snapshot = Empty() with
        {
            Settings = new Dictionary<string, string> { ["queue.maxConcurrentJobs"] = "2", ["evil.key"] = "x" }
        };

        var result = ConfigSnapshotValidator.Validate(snapshot, AllowedKeys);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("evil.key"));
    }

    [Fact]
    public void A_library_with_a_bad_enum_or_blank_field_is_rejected()
    {
        var snapshot = Empty() with
        {
            Libraries =
            [
                new LibrarySnapshot(
                    Name: "",
                    Path: "/data/tv",
                    MediaType: "NotAType",
                    RuleProfile: "ConservativeHevc",
                    Enabled: true,
                    Priority: 0,
                    MinFileSizeBytes: null,
                    MaxHeight: null,
                    TargetVideoCodec: null,
                    TargetContainer: null,
                    HdrHandling: "Nonsense",
                    ExcludePaths: null,
                    QualityCrf: null,
                    EncoderPreset: null,
                    MoveOnComplete: false,
                    TargetFolder: null)
            ]
        };

        var result = ConfigSnapshotValidator.Validate(snapshot, AllowedKeys);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("name is required"));
        Assert.Contains(result.Errors, e => e.Contains("media type is not valid"));
        Assert.Contains(result.Errors, e => e.Contains("HDR handling is not valid"));
    }

    [Fact]
    public void A_well_formed_library_watcher_and_target_are_valid()
    {
        var snapshot = Empty() with
        {
            Settings = new Dictionary<string, string> { ["queue.maxConcurrentJobs"] = "3" },
            Libraries =
            [
                new LibrarySnapshot(
                    "Films", "/data/films", "Film", "ConservativeHevc", true, 5,
                    null, 1080, "hevc", "mkv", "Exclude", null, 22, "slow", false, null)
            ],
            ActivityWatchers =
            [
                new ActivityWatcherSnapshot("Living room Plex", "Plex", "http://10.0.0.2:32400", true, true)
            ],
            NotificationTargets =
            [
                new NotificationTargetSnapshot("ntfy", "Ntfy", "https://ntfy.sh/optimisarr", true, true, true)
            ]
        };

        var result = ConfigSnapshotValidator.Validate(snapshot, AllowedKeys);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void A_library_with_malformed_kept_audio_languages_is_rejected()
    {
        var snapshot = Empty() with
        {
            Libraries =
            [
                new LibrarySnapshot(
                    "Films", "/data/films", "Film", "ConservativeHevc", true, 0,
                    null, null, null, null, null, null, null, null, false, null,
                    KeepAudioLanguages: "english")
            ]
        };

        var result = ConfigSnapshotValidator.Validate(snapshot, AllowedKeys);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("kept audio languages"));
    }

    private static ConfigSnapshot Empty() => new(
        ConfigSnapshot.CurrentVersion,
        DateTimeOffset.UnixEpoch,
        new Dictionary<string, string>(),
        [],
        [],
        [],
        []);
}
