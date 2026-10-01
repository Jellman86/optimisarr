using System.Diagnostics;
using System.Text;

namespace Optimisarr.Core.Tools;

internal sealed record ToolProcessResult(int ExitCode, string Output, string? Error);

/// <summary>Small tool probes must finish promptly and cannot leave a child behind on abort.</summary>
internal static class BoundedToolProcess
{
    private const int MaximumCharacters = 1024 * 1024;

    public static async Task<ToolProcessResult> RunAsync(
        string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(-1, string.Empty, ex.Message);
        }

        // Start both readers before waiting for either pipe: the other pipe may fill first.
        var stdout = DrainAsync(process.StandardOutput, deadline.Token);
        var stderr = DrainAsync(process.StandardError, deadline.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            var output = await stdout;
            var error = await stderr;
            return output.Truncated || error.Truncated
                ? new(-1, string.Empty, "Tool probe output exceeded its bounded capture limit.")
                : new(process.ExitCode, output.Text, FirstLine(error.Text));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(-1, string.Empty, "Tool probe timed out.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // Observe reader completion even when the wait was cancelled.
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            var retained = Math.Min(count, MaximumCharacters - text.Length);
            text.Append(buffer, 0, retained);
            truncated |= retained != count;
        }
        return (text.ToString(), truncated);
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
}
