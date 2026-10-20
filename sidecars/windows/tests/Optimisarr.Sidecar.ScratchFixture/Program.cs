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
            await PublishReadyAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        if (args[0] == "pipe-holder")
        {
            await PublishReadyAsync(Path.Combine(args[1], "holder-ready"), "ready", token);
            while (!File.Exists(Path.Combine(args[1], "late-progress"))) await Task.Delay(20, token);
            try { Console.WriteLine("out_time_us=2000000"); await Console.Out.FlushAsync(token); }
            catch (IOException) { }
            await PublishReadyAsync(Path.Combine(args[1], "late-attempted"), "attempted", token);
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
            await PublishReadyAsync(args[5], JsonSerializer.Serialize(new { childId = holder.Id, directory = workspace.DirectoryPath }), token);
            Console.WriteLine("out_time_us=1000000");
            await Console.Out.FlushAsync(token);
            // The child deliberately survives this parent with both pipes and the real scratch
            // guard. Its test owns release; direct exit must never be treated as retirement.
            return 0;
        }
        if (args[0] == "child")
        {
            await PublishReadyAsync(args[1], "ready", token);
            if (args.Length > 2) while (!File.Exists(args[2])) await Task.Delay(20, token);
            else await Task.Delay(TimeSpan.FromSeconds(30), token);
            return 0;
        }
        if (args[0] == "powercut-store")
        {
            if (!OperatingSystem.IsLinux()) return 2;
            return await RunPowercutStore(args);
        }
        if (args[0] == "scratch-fifo")
        {
            if (!OperatingSystem.IsLinux() || args.Length != 2) return 2;
            using var root = NativeWorkspaceDirectory.OpenRoot(args[1], create: false);
            Console.WriteLine("OPENING");
            await Console.Out.FlushAsync(token);
            try { using var file = root.OpenFile("pipe"); return 3; }
            catch (IOException) { return 0; }
        }
        if (args[0] == "powercut-calibrate")
        {
            if (!OperatingSystem.IsLinux()) return 2;
            return RunPowercutCalibrate(args);
        }
        if (args[0] == "journal-parent")
        {
            if (!OperatingSystem.IsWindows()) return 2;
            return await RunJournalParent(args, token);
        }
        if (args[0] == "windows-journal-cut")
        {
            if (!OperatingSystem.IsWindows()) return 2;
            return await RunWindowsJournalCut(args);
        }
        if (args[0] != "parent") return 2;
        var lease = Guid.Parse("81223344-1234-4321-8123-123456789012");
        var scope = NativeScratchWorkspace.PrepareAccountingScope(args[1]);
        using var owner = args.Length > 7 && args[7] == "capability"
            ? NativeScratchWorkspace.CreateAccountedCapability(args[1], Guid.Parse(args[5]), scope)
            : args.Length > 5 ? NativeScratchWorkspace.CreateAccountedJob(args[1], 42, lease, Guid.Parse(args[5]), scope)
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
            Console.WriteLine(JsonSerializer.Serialize(new { childId = child.Id, directory = owner.DirectoryPath, scope }));
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

    // Fault-model calibration, deliberately NOT journal code: proves the cut
    // model can discriminate flushed from unflushed writes before any journal
    // result is trusted. `write` persists one file with an explicit flush,
    // writes a second without one, announces both, then waits to be killed.
    // `check` after restart reports exactly what survived.
    // args: powercut-calibrate <write|check> <directory>
    private static int RunPowercutCalibrate(string[] args)
    {
        var directory = args[2];
        var flushed = Path.Combine(directory, "flushed.txt");
        var unflushed = Path.Combine(directory, "unflushed.txt");
        if (args[1] == "check")
        {
            Console.WriteLine($"CALIBRATION flushed={(File.Exists(flushed) ? File.ReadAllText(flushed).Trim() : "absent")} unflushed={(File.Exists(unflushed) ? File.ReadAllText(unflushed).Trim() : "absent")}");
            return 0;
        }
        if (args[1] != "write") return 2;
        Directory.CreateDirectory(directory);
        using (var stream = new FileStream(flushed, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write("flushed-v1\n"u8);
            stream.Flush(flushToDisk: true);
        }
        Console.WriteLine("CALIBRATION-WRITTEN flushed");
        Console.Out.Flush();
        File.WriteAllText(unflushed, "unflushed-v1\n");
        Console.WriteLine("CALIBRATION-WRITTEN unflushed");
        Console.Out.Flush();
        Thread.Sleep(Timeout.InfiniteTimeSpan);
        return 0;
    }

    // Abrupt-termination qualification driver: provisioning, one durable commit
    // at a time, and reopen reporting for the Linux journal store. Every durable
    // phase is printed and flushed as one framed REACHED line carrying the trial
    // id, this boot's kernel boot id and a sequence number, so stale output from
    // an earlier boot can never trigger a later cut. When a block phase is named,
    // the process prints BLOCKED in the same frame and waits indefinitely to be
    // killed with the guest; there is no timeout release. COMMIT_RETURNED is
    // printed only after the operation returns; the host records acknowledgements.
    // args: powercut-store <provision|commit|report> <journal> <authority> [blockPhase] [trialId]
    private static async Task<int> RunPowercutStore(string[] args)
    {
        // blockPhase may carry an occurrence suffix ("checkpoint:renamed#2"): one commit writes
        // the checkpoint twice (pending, then committed), and each write boundary is its own
        // cut point. Without a suffix the first occurrence blocks, as before.
        var blockSpec = args.Length > 4 ? args[4] : null;
        var blockPhase = blockSpec;
        var blockOccurrence = 1;
        if (blockSpec is not null && blockSpec.LastIndexOf('#') is var hash and > 0
            && int.TryParse(blockSpec[(hash + 1)..], out var occurrence) && occurrence > 0)
        {
            blockPhase = blockSpec[..hash];
            blockOccurrence = occurrence;
        }
        var seen = 0;
        var trial = args.Length > 5 ? args[5] : "local";
        var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        var sequence = 0;
        void Step(string phase)
        {
            if (phase == blockPhase) seen++;
            Console.WriteLine($"REACHED trial={trial} boot={boot} seq={++sequence} phase={phase}");
            Console.Out.Flush();
            if (phase == blockPhase && seen == blockOccurrence)
            {
                Console.WriteLine($"BLOCKED trial={trial} boot={boot} seq={sequence} phase={phase} occurrence={seen}");
                Console.Out.Flush();
                Thread.Sleep(Timeout.InfiniteTimeSpan);
            }
        }
        try
        {
            if (args[1] == "provision")
            {
                await using var provisioned =
                    await NativeWorkJournalBootstrap.ProvisionNewAsync(args[2], args[3], Step);
                Console.WriteLine($"COMMIT_RETURNED revision={provisioned.Journal.Snapshot.Revision}");
                Console.Out.Flush();
                return 0;
            }
            await using var restored =
                await NativeWorkJournalBootstrap.OpenExistingAsync(args[2], args[3], Step);
            if (args[1] == "report")
            {
                var snapshot = restored.Journal.Snapshot;
                long disk = 0, memory = 0; var holding = 0;
                foreach (var obligation in snapshot.Obligations)
                {
                    if (!obligation.HoldsResources) continue;
                    holding++; disk += obligation.DiskCharge; memory += obligation.MemoryCharge;
                }
                Console.WriteLine($"VERDICT revision={snapshot.Revision} obligations={snapshot.Obligations.Length} holding={holding} disk={disk} memory={memory}");
                foreach (var obligation in snapshot.Obligations)
                    Console.WriteLine($"OBLIGATION owner={obligation.Owner:N} purpose={obligation.Purpose} phase={obligation.Phase} volume={obligation.VolumeIdentity} disk={obligation.DiskCharge} memory={obligation.MemoryCharge}");
                Console.Out.Flush();
                return 0;
            }
            if (args[1] != "commit") return 2;
            var root = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
            await restored.Journal.ExecuteAsync<int>((scope, _) => {
                scope.Commit(scope.Snapshot with {
                    Revision = scope.Snapshot.Revision + 1,
                    Obligations = [.. scope.Snapshot.Obligations, new NativeWorkObligation(
                        Guid.NewGuid(), NativeWorkPurpose.Job, 42, Guid.NewGuid(), root,
                        new("root:1", "control:1", "gate:1"), "device:powercut", 100, 200, false, NativeWorkPhase.Intent)] });
                return Task.CompletedTask;
            }, _ => Task.FromResult(42), (_, _) => Task.CompletedTask);
            Console.WriteLine($"COMMIT_RETURNED revision={restored.Journal.Snapshot.Revision}");
            Console.Out.Flush();
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"REFUSED {error.GetType().Name}");
            Console.Out.Flush();
            return 0;
        }
    }

    // Process-death tests only: the OS and its caches keep running. This fixture
    // deliberately bypasses the Linux-only bootstrap without adding any worker caller.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsJournalCut(string[] args)
    {
        // windows-journal-cut <provision|commit|reopen> <journal> <authority> <phase[#occurrence]> <trial>
        if (args.Length != 6 || args[1] is not ("provision" or "commit" or "reopen")
            || !Guid.TryParseExact(args[5], "N", out _)) return 2;
        var boundary = args[4];
        var phase = boundary;
        var occurrence = 1;
        var hash = boundary.LastIndexOf('#');
        if (hash >= 0)
        {
            if (hash == 0 || !int.TryParse(boundary[(hash + 1)..], out occurrence) || occurrence <= 0) return 2;
            phase = boundary[..hash];
        }
        var armed = args[1] != "commit";
        var seen = 0;
        void Step(string value)
        {
            if (!armed || value != phase || ++seen != occurrence) return;
            Console.WriteLine($"BLOCKED trial={args[5]} boundary={boundary}");
            Console.Out.Flush();
            Thread.Sleep(Timeout.InfiniteTimeSpan);
        }
        var storage = args[1] == "provision"
            ? WindowsNativeWorkJournalStorage.ProvisionNew(args[2], args[3], Step)
            : WindowsNativeWorkJournalStorage.OpenExisting(args[2], args[3], Step);
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        armed = true;
        if (args[1] == "commit")
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
            await journal.ExecuteAsync<int>((scope, _) => {
                scope.Commit(scope.Snapshot with {
                    Revision = scope.Snapshot.Revision + 1,
                    Obligations = [new(Guid.NewGuid(), NativeWorkPurpose.Capability, null, null, root,
                        new("root:1", "control:1", "gate:1"), "volume:1", 100, 200, false, NativeWorkPhase.Intent)] });
                return Task.CompletedTask;
            }, _ => Task.FromResult(42), (_, _) => Task.CompletedTask);
        }
        Console.WriteLine("OPERATION_RETURNED");
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> RunJournalParent(string[] args, CancellationToken token)
    {
        // args: journal-parent <journal> <authority> <childReady> <host> <runtimeconfig> <depsfile>
        using var storage = WindowsNativeWorkJournalStorage.ProvisionNew(args[1], args[2]);
        var child = new Process { StartInfo = new(args[4]) { UseShellExecute = false } };
        foreach (var argument in new[] { "exec", "--runtimeconfig", args[5], "--depsfile", args[6],
                     typeof(Program).Assembly.Location, "child", args[3] })
            child.StartInfo.ArgumentList.Add(argument);
        child.Start();
        try
        {
            while (!File.Exists(args[3]))
            {
                if (child.HasExited) throw new IOException("The journal child did not start.");
                await Task.Delay(20, token);
            }
            storage.ValidateBinding();
            Console.WriteLine(JsonSerializer.Serialize(new { childId = child.Id }));
            await Console.Out.FlushAsync(token);
            // The coordinator handle lives only in this parent. If handle
            // inheritance leaked it, the surviving child would keep the lock.
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static async Task PublishReadyAsync(string path, string text, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }
}
