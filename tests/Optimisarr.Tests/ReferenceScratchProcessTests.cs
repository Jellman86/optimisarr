using System.Diagnostics;
using System.Text.Json;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class ReferenceScratchProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reference scratch " + Guid.NewGuid());
    private string Pid => Path.Combine(_root, "pid");

    private VerificationService Service()
    {
        Directory.CreateDirectory(_root);
        var tool = Path.Combine(_root, "fake media tool");
        File.WriteAllText(tool, TestPython.Shebang + """
            import os,sys,time,pathlib,json
            root=pathlib.Path(__ROOT__)
            if '--hang' in sys.argv:
                (root/'pid').write_text(str(os.getpid()))
                time.sleep(30)
            if '-show_streams' in sys.argv:
                print(json.dumps({'streams':[{'codec_type':'video','codec_name':'h264','width':64,'height':64,'pix_fmt':'yuv420p','duration':'1'}],'format':{'duration':'1','format_name':'matroska'}}))
            elif '-y' in sys.argv and sys.argv[-1].endswith('.mkv'):
                pathlib.Path(sys.argv[-1]).write_text('reference evidence')
                if sys.argv[-1].endswith('failed.mkv'):
                    sys.stderr.write('reference encode failed\n');sys.exit(9)
            """.Replace("__ROOT__", JsonSerializer.Serialize(_root)));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new(new MediaProbeService(tool), new DecodeHealthCheck(tool), new TimestampIntegrityCheck(tool),
            new ReferenceFrameAlignmentProbe(tool), new QualityScoreService(tool), new LoudnessService(tool),
            new ImageQualityService(tool), new ImageMetadataService(tool), new TranscodeOptions(tool));
    }

    [PosixFact]
    public async Task Cancellation_returns_only_after_the_reference_process_is_reaped()
    {
        var service = Service();
        using var cancellation = new CancellationTokenSource();
        var run = service.RunReferenceClipAsync(["--hang"], cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(Pid) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(File.Exists(Pid));
        var pid = int.Parse(await File.ReadAllTextAsync(Pid));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        AssertExited(pid);
    }

    [PosixFact]
    public async Task Failed_reference_creation_captures_partial_output_for_the_callers_cleanup()
    {
        var service = Service();
        var reference = Path.Combine(_root, "failed.mkv");
        var original = Path.Combine(_root, "source.mkv");
        var output = Path.Combine(_root, "candidate.mkv");
        await File.WriteAllTextAsync(original, "unchanged original");
        await File.WriteAllTextAsync(output, "candidate");
        string? captured = null;
        var deleted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAsync(
            new(original, 100, 1, 0, 0, false, false), output, VerificationPolicy.Default, default,
            new VerificationClip(1, 0, reference), scratch: new(path => captured = path,
                _ => { deleted = true; return Task.FromResult(true); })));
        Assert.Equal(reference, captured);
        Assert.Equal("reference evidence", await File.ReadAllTextAsync(reference));
        Assert.False(deleted); // The enclosing owned workspace handles failure before verification starts.
        Assert.Equal("unchanged original", await File.ReadAllTextAsync(original));
    }

    [PosixFact]
    public async Task Verification_does_not_delete_a_reference_without_explicit_caller_ownership()
    {
        var service = Service();
        var original = Path.Combine(_root, "source.mkv");
        var output = Path.Combine(_root, "candidate.mkv");
        var reference = Path.Combine(_root, "reference.mkv");
        await File.WriteAllTextAsync(original, "unchanged original");
        await File.WriteAllTextAsync(output, "candidate");
        var outcome = await service.VerifyAsync(new(original, 100, 1, 0, 0, false, false), output,
            VerificationPolicy.Default with { MeasureVmaf = false }, default, new VerificationClip(1, 0, reference));
        Assert.NotNull(outcome.Report);
        Assert.Equal("reference evidence", await File.ReadAllTextAsync(reference));
        Assert.Equal("unchanged original", await File.ReadAllTextAsync(original));
    }

    [PosixFact]
    public async Task Reference_creation_cannot_overwrite_either_verification_input()
    {
        var service = Service();
        var original = Path.Combine(_root, "source.mkv");
        var output = Path.Combine(_root, "candidate.mkv");
        await File.WriteAllTextAsync(original, "unchanged original");
        await File.WriteAllTextAsync(output, "unchanged candidate");
        foreach (var destination in new[] { original, output })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAsync(
                new(original, 100, 1, 0, 0, false, false), output, VerificationPolicy.Default, default,
                new VerificationClip(1, 0, destination)));
            Assert.Equal("unchanged original", await File.ReadAllTextAsync(original));
            Assert.Equal("unchanged candidate", await File.ReadAllTextAsync(output));
        }
    }

    [PosixFact]
    public async Task Reference_creation_rejects_a_physical_alias_of_the_original()
    {
        var service = Service();
        var original = Path.Combine(_root, "source.mkv");
        var output = Path.Combine(_root, "candidate.mkv");
        var alias = Path.Combine(_root, "source-alias.mkv");
        await File.WriteAllTextAsync(original, "unchanged original");
        await File.WriteAllTextAsync(output, "unchanged candidate");
        File.CreateSymbolicLink(alias, original);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAsync(
            new(original, 100, 1, 0, 0, false, false), output, VerificationPolicy.Default, default,
            new VerificationClip(1, 0, alias)));
        Assert.Equal("unchanged original", await File.ReadAllTextAsync(original));
    }

    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }

    public void Dispose()
    {
        if (File.Exists(Pid))
        {
            try
            {
                using var process = Process.GetProcessById(int.Parse(File.ReadAllText(Pid)));
                if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
            }
            catch (ArgumentException) { }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
