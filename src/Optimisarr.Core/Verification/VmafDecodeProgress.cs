using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Verification;

internal sealed record VmafDecodeProgressPlan(QualityScoreCommand Command, long ReferenceEndFrame, long CandidateEndFrame);

// Only the local service adds telemetry. Frozen worker commands keep their existing allowlist
// and graph, and the telemetry never selects, duplicates or retimes a picture (#406).
internal static class VmafDecodeProgress
{
    internal const int FrameInterval = 50;

    internal static VmafDecodeProgressPlan Prepare(QualityScoreCommand command, QualityMeasurementContext context)
    {
        if (!QualityScoreCommandBuilder.UsesAbsoluteFramePairing(context) || context.MeasureDurationSeconds is not > 0)
            return new(command, 0, 0);

        var referenceEnd = Math.Max(0, (long)Math.Round(context.ReferenceStartSeconds!.Value * context.ReferenceFrameRate!.Value));
        var candidateEnd = Math.Max(0, (long)Math.Round(context.DistortedStartSeconds!.Value * context.ReferenceFrameRate!.Value));
        if (referenceEnd == 0 && candidateEnd == 0) return new(command, 0, 0);
        var graph = command.FilterGraph;
        if (candidateEnd > 0) graph = graph.Replace("[0:v]", "[0:v]" + Filters("candidate", 2, candidateEnd), StringComparison.Ordinal);
        if (referenceEnd > 0) graph = graph.Replace("[1:v]", "[1:v]" + Filters("reference", 1, referenceEnd), StringComparison.Ordinal);
        var arguments = command.Arguments.ToArray();
        arguments[Array.IndexOf(arguments, "-lavfi") + 1] = graph;
        return new(command with { Arguments = arguments, FilterGraph = graph }, referenceEnd, candidateEnd);
    }

    private static string Filters(string input, int pipe, long endFrame)
    {
        var key = "lavfi.optimisarr.decode." + input;
        var enable = FormattableString.Invariant($"'not(mod(n,{FrameInterval}))*lt(n,{endFrame})'");
        // Separate pipes keep each frame header paired with its input's marker. Explicit file/pipe
        // output works at loglevel error; direct mode flushes counters during slow decoding.
        return $"metadata=mode=delete:key={key}:enable={enable},"
            + $"metadata=mode=add:key={key}:value=1:enable={enable},"
            + $"metadata=mode=print:key={key}:file='pipe\\:{pipe}':direct=1:enable={enable},";
    }
}

internal enum DecodeProgressReading { Unrelated, Telemetry, Advanced }

internal sealed partial class VmafDecodedFrameProgress(string input, long endFrame)
{
    private readonly string _marker = "lavfi.optimisarr.decode." + input + "=1";
    private long _pendingFrame = -1;
    private long _lastFrame = -1;
    internal long LastFrame => Interlocked.Read(ref _lastFrame);

    [GeneratedRegex(@"^frame:\s*([0-9]+)\s+pts:")]
    private static partial Regex Header();

    internal DecodeProgressReading Observe(string line)
    {
        if (line.StartsWith("frame:", StringComparison.Ordinal))
        {
            var match = Header().Match(line);
            _pendingFrame = match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var frame) && frame >= 0 && frame < endFrame
                && frame % VmafDecodeProgress.FrameInterval == 0 ? frame : -1;
            return DecodeProgressReading.Telemetry;
        }
        if (line.StartsWith("lavfi.optimisarr.decode.", StringComparison.Ordinal))
        {
            var advanced = line == _marker && _pendingFrame > LastFrame;
            if (advanced) Interlocked.Exchange(ref _lastFrame, _pendingFrame);
            _pendingFrame = -1;
            return advanced ? DecodeProgressReading.Advanced : DecodeProgressReading.Telemetry;
        }
        return DecodeProgressReading.Unrelated;
    }
}
