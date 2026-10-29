using System.Diagnostics;
using System.Text.Json;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PerceptualSampleProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "perceptual process " + Guid.NewGuid());
    private string Pid => Path.Combine(_root, "pid");
    private string Tool => Path.Combine(_root, "sample tool");
    private string Source => Path.Combine(_root, "source with spaces.png");

    private void Prepare()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Source, "unchanged source");
        File.WriteAllText(Tool, TestPython.Shebang + "import pathlib,os,time\npathlib.Path(" + JsonSerializer.Serialize(Pid)
            + ").write_text(str(os.getpid()))\ntime.sleep(30)\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private async Task<object> Run(bool image, CancellationToken token, TimeSpan timeout) => image
        ? await new ImageQualityService(Tool).MeasureSampleAsync(Source, Source, new(32, 32, false), token, timeout)
        : await new LoudnessService(Tool).MeasureSampleAsync(Source, token, timeout);

    private async Task<int> StartedAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(Pid) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(File.Exists(Pid));
        return int.Parse(await File.ReadAllTextAsync(Pid));
    }

    [PosixFact]
    public async Task Sample_auxiliary_cancellation_reaps_both_tools_before_returning()
    {
        Prepare();
        foreach (var image in new[] { false, true })
        {
            File.Delete(Pid);
            using var cancellation = new CancellationTokenSource();
            var run = Run(image, cancellation.Token, TimeSpan.FromSeconds(30));
            var pid = await StartedAsync();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            AssertExited(pid);
            Assert.Equal("unchanged source", await File.ReadAllTextAsync(Source));
        }
    }

    [PosixFact]
    public async Task Sample_auxiliary_deadlines_return_unavailable_and_reap_the_tool()
    {
        Prepare();
        foreach (var image in new[] { false, true })
        {
            File.Delete(Pid);
            var run = Run(image, CancellationToken.None, TimeSpan.FromSeconds(2));
            var pid = await StartedAsync();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(result is LoudnessResult { Measured: false } or ImageQualityResult { Measured: false });
            AssertExited(pid);
        }
    }

    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
