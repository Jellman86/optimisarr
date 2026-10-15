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
        if (args[0] == "encode-wait")
        {
            await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        if (args[0] == "pipe-holder")
        {
            await File.WriteAllTextAsync(Path.Combine(args[1], "holder-ready"), "ready", token);
            while (!File.Exists(Path.Combine(args[1], "late-progress"))) await Task.Delay(20, token);
            try { Console.WriteLine("out_time_us=2000000"); await Console.Out.FlushAsync(token); }
            catch (IOException) { }
            await File.WriteAllTextAsync(Path.Combine(args[1], "late-attempted"), "attempted", token);
            while (!File.Exists(Path.Combine(args[1], "release"))) await Task.Delay(20, token);
            return 0;
        }
        if (args[0] == "pipe-parent")
        {
            using var workspace = NativeScratchWorkspace.CreateJob(args[1], 42, Guid.NewGuid());
            using var holder = new Process { StartInfo = new(args[2]) { UseShellExecute = false } };
            foreach (var argument in new[] { "exec", "--runtimeconfig", args[3], "--depsfile", args[4],
                         typeof(Program).Assembly.Location, "pipe-holder", workspace.DirectoryPath })
                holder.StartInfo.ArgumentList.Add(argument);
            holder.Start();
            while (!File.Exists(Path.Combine(workspace.DirectoryPath, "holder-ready")))
            {
                if (holder.HasExited) throw new IOException("The pipe holder did not start.");
                await Task.Delay(20, token);
            }
            await File.WriteAllTextAsync(args[5], JsonSerializer.Serialize(new { childId = holder.Id, directory = workspace.DirectoryPath }), token);
            Console.WriteLine("out_time_us=1000000");
            await Console.Out.FlushAsync(token);
            // The child deliberately survives this parent with both pipes and the real scratch
            // guard. Its test owns release; direct exit must never be treated as retirement.
            return 0;
        }
        if (args[0] == "child")
        {
            await File.WriteAllTextAsync(args[1], "ready", token);
            if (args.Length > 2) while (!File.Exists(args[2])) await Task.Delay(20, token);
            else await Task.Delay(TimeSpan.FromSeconds(30), token);
            return 0;
        }
        if (args[0] != "parent") return 2;
        var lease = Guid.Parse("81223344-1234-4321-8123-123456789012");
        using var owner = args.Length > 5 ? NativeScratchWorkspace.CreateAccountedJob(args[1], 42, lease, Guid.Parse(args[5]))
            : NativeScratchWorkspace.CreateJob(args[1], 42, lease);
        await File.WriteAllTextAsync(Path.Combine(owner.DirectoryPath, "source"), "owned test copy", token);
        var ready = Path.Combine(owner.DirectoryPath, "child-ready");
        using var child = new Process { StartInfo = new(args[2]) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "exec", "--runtimeconfig", args[3], "--depsfile", args[4],
                     typeof(Program).Assembly.Location, "child", ready }) child.StartInfo.ArgumentList.Add(argument);
        if (args.Length > 6) child.StartInfo.ArgumentList.Add(args[6]);
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
