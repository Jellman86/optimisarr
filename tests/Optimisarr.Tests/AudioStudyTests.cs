namespace Optimisarr.Tests;

public sealed class AudioStudyTests
{
    private static string[] Arguments => ["--reference", "source.wav", "--candidate", "candidate.opus",
        "--metric", "metric", "--ffmpeg", "ffmpeg", "--ffprobe", "ffprobe", "--report", "report.json"];

    [Fact]
    public void All_tools_and_files_are_explicit_and_duplicate_or_unknown_options_are_rejected()
    {
        Assert.NotNull(AudioStudyOptions.Parse(Arguments));
        Assert.Null(AudioStudyOptions.Parse(Arguments[..^2]));
        Assert.Null(AudioStudyOptions.Parse([.. Arguments, "--report", "other.json"]));
        Assert.Null(AudioStudyOptions.Parse([.. Arguments, "--require", "0.1"]));
        Assert.Null(AudioStudyOptions.Parse([.. Arguments, "--scratch"]));
        Assert.Null(AudioStudyOptions.Parse([]));
    }

    [Fact]
    public async Task Reports_cannot_overwrite_inputs_existing_evidence_or_tools()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "original.wav");
            await File.WriteAllTextAsync(existing, "preserve");
            var options = AudioStudyOptions.Parse([.. Arguments[..^1], existing])!;
            Assert.Equal(1, await AudioStudyRunner.RunAsync(options, default));
            Assert.Equal("preserve", await File.ReadAllTextAsync(existing));
        }
        finally { Directory.Delete(root, true); }
    }
}
