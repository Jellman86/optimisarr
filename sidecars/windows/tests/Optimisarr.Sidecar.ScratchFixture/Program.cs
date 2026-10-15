using System.Diagnostics;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.ScratchFixture;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        if (args[0] == "child")
        {
            await File.WriteAllTextAsync(args[1], "ready", token);
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return 0;
        }
        if (args[0] != "parent") return 2;
        using var owner = NativeScratchWorkspace.CreateJob(args[1], 42, Guid.Parse("81223344-1234-4321-8123-123456789012"));
        await File.WriteAllTextAsync(Path.Combine(owner.DirectoryPath, "source"), "owned test copy", token);
        var ready = Path.Combine(owner.DirectoryPath, "child-ready");
        using var child = new Process { StartInfo = new(args[2]) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "exec", "--runtimeconfig", args[3], "--depsfile", args[4],
                     typeof(Program).Assembly.Location, "child", ready }) child.StartInfo.ArgumentList.Add(argument);
        try
        {
            child.Start();
            var output = child.StandardOutput.ReadToEndAsync(token);
            var error = child.StandardError.ReadToEndAsync(token);
            while (!File.Exists(ready))
            {
                if (child.HasExited) throw new IOException("The test child did not start.");
                await Task.Delay(10, token);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { childId = child.Id, directory = owner.DirectoryPath }));
            await Console.Out.FlushAsync(token);
            await child.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
            return 0;
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
            owner.Cleanup();
        }
    }
}
