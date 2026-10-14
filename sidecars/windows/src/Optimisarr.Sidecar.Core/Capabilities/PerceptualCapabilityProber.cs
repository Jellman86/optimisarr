using System.Buffers.Binary;
using System.Security.Cryptography;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Capabilities;

internal sealed record PerceptualCapabilityOperations(
    Func<string, string, string, CancellationToken, Task<bool>> Encoder,
    Func<string, string, CancellationToken, Task<bool>> Metadata,
    Func<string, CancellationToken, Task<RemoteImagePerceptualEvidence?>> Image,
    Func<string, CancellationToken, Task<RemoteAudioQualityEvidence?>> Audio);

/// <summary>Qualifies the actual tools on owned synthetic inputs; a listing is never proof.</summary>
public sealed class PerceptualCapabilityProber
{
    private readonly PerceptualCapabilityOperations _operations;
    public PerceptualCapabilityProber(PerceptualNativeTools tools) : this(tools, Native(tools)) { }
    internal PerceptualCapabilityProber(PerceptualNativeTools tools, PerceptualCapabilityOperations operations) => _operations = operations;
    public static PerceptualCapabilityProber ForFfmpeg(string ffmpeg) => new(PerceptualJobRuntime.ResolveNativeTools(ffmpeg));

    public async Task<WorkerPerceptualCapabilities> ProbeAsync(string scratchRoot, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(1));
        var root = Path.Combine(scratchRoot, "native-capability-" + Guid.NewGuid().ToString("N"));
        var owned = false;
        try
        {
            if (Directory.Exists(root) || File.Exists(root)) return new();
            Directory.CreateDirectory(root); owned = true;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var source = Path.Combine(root, "source.png");
            var copy = Path.Combine(root, "copy.png");
            var audio = Path.Combine(root, "stereo.wav");
            var fixture = Fixture();
            await File.WriteAllBytesAsync(source, fixture, deadline.Token);
            await File.WriteAllBytesAsync(copy, BareImage(fixture), deadline.Token);
            await File.WriteAllBytesAsync(audio, Wave(), deadline.Token);
            var sourceHash = await Hash(source, deadline.Token);
            var audioHash = await Hash(audio, deadline.Token);
            var encoders = new List<string>();
            foreach (var encoder in new[] { "mjpeg", "libwebp" })
                if (await Attempt(() => _operations.Encoder(encoder, source, Path.Combine(root, encoder + (encoder == "mjpeg" ? ".jpg" : ".webp")), deadline.Token)))
                    encoders.Add(encoder);
            var metadata = await Attempt(() => _operations.Metadata(source, copy, deadline.Token));
            var image = await Attempt(() => _operations.Image(source, deadline.Token));
            var sound = await Attempt(() => _operations.Audio(audio, deadline.Token));
            var imageValid = RemoteImagePerceptualEvidence.VersionError(image) is null && image?.Assessment is { } picture
                && PerceptualSearchEvidence.Image(picture, sourceHash, sourceHash, 0).Available
                && picture.Measurement is { Width: 32, Height: 32, Alpha: false, Score: 100 };
            var audioValid = AudioQualityReporting.Validate(sound, audioHash, audioHash) is null
                && sound?.Assessment is { Reference: { DurationSeconds: 1, Channels: 2, SampleRate: 48000 }, CoveredSeconds: 1, WorstChannelDistance: 0 };
            return new(encoders, metadata, audioValid ? AudioQualityResultParser.Revision : null,
                imageValid ? ImagePerceptualResultParser.Revision : null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(); }
        catch (Exception error) when (Unavailable(error)) { return new(); }
        finally
        {
            if (owned)
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> Attempt(Func<Task<bool>> work)
    {
        try { return await work(); }
        catch (Exception error) when (Unavailable(error)) { return false; }
    }
    private static async Task<T?> Attempt<T>(Func<Task<T?>> work) where T : class
    {
        try { return await work(); }
        catch (Exception error) when (Unavailable(error)) { return null; }
    }
    private static bool Unavailable(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception;

    private static PerceptualCapabilityOperations Native(PerceptualNativeTools tools)
    {
        var image = new ImagePerceptualQualityService(tools.ImageMetric);
        return new(Encode, Metadata,
            async (source, token) => RemoteImagePerceptualEvidence.From(await image.MeasureAsync(source, source, token)),
            async (source, token) =>
            {
                var service = AudioQualityTools.Create(tools.Ffmpeg, tools.Ffprobe, tools.AudioMetric, Path.Combine(Path.GetDirectoryName(source)!, "audio-metric"));
                return service is null ? null : RemoteAudioQualityEvidence.From(await service.MeasureAsync(source, source, token));
            });

        async Task<bool> Encode(string encoder, string source, string candidate, CancellationToken token)
        {
            var encoded = await BoundedToolProcess.RunAsync(tools.Ffmpeg,
                ["-nostdin", "-hide_banner", "-v", "error", "-xerror", "-n", "-i", source, "-map", "0:v:0", "-frames:v", "1",
                    "-c:v", encoder, encoder == "mjpeg" ? "-q:v" : "-quality", encoder == "mjpeg" ? "4" : "80", "-fs", "1048576", candidate], token);
            if (encoded.ExitCode != 0 || !File.Exists(candidate) || new FileInfo(candidate).Length is <= 0 or > 1048576) return false;
            var probed = await new MediaProbeService(tools.Ffprobe).ProbeAsync(candidate, token);
            if (!probed.Success || probed.VideoCodec != (encoder == "mjpeg" ? "mjpeg" : "webp") || probed.Width != 32 || probed.Height != 32) return false;
            if (!await Metadata(source, candidate, token)) return false;
            var measured = await image.MeasureAsync(source, candidate, token);
            return PerceptualSearchEvidence.Image(measured, await Hash(source, token), await Hash(candidate, token), 0).Available
                && (await new DecodeHealthCheck(tools.Ffmpeg).CheckAsync(candidate, token)).Healthy;
        }

        async Task<bool> Metadata(string source, string candidate, CancellationToken token)
        {
            var marker = new ImageMarkerService(tools.ExifTool);
            var inspection = new ImageMetadataService(tools.ExifTool);
            var before = await inspection.ReadAsync(source, token);
            if (!before.Measured || !before.Metadata.HasIccProfile || !before.Metadata.HasExif
                || !await marker.CopyMetadataAsync(source, candidate, token)) return false;
            const string value = "optimisarr/capability-proof";
            if (!await marker.WriteAsync(candidate, value, token) || await marker.ReadAsync(candidate, token) != value) return false;
            var after = await inspection.ReadAsync(candidate, token);
            if (!after.Measured || !after.Metadata.HasIccProfile || !after.Metadata.HasExif) return false;
            var extraction = ExifToolCommand.Build(["-b", "-ICC_Profile", "-w", "icc", candidate], OperatingSystem.IsWindows());
            var extracted = await BoundedToolProcess.RunWithInputAsync(tools.ExifTool, extraction.Arguments, extraction.StandardInput, token);
            var profile = Path.ChangeExtension(candidate, ".icc");
            return extracted.ExitCode == 0 && File.Exists(profile) && new FileInfo(profile).Length == 588
                && Same(await Hash(profile, token), "6f6fe5cc53cd24ceeb7997fb24ce2889fdfb88d88ce4fdc5f8e25e0481294953");
        }
    }

    private static byte[] Fixture()
    {
        using var stream = typeof(PerceptualCapabilityProber).Assembly.GetManifestResourceStream("Optimisarr.CapabilityProfile.png")
            ?? throw new IOException("The native capability fixture is missing.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        var fixture = bytes.ToArray();
        if (fixture.Length != 2769 || !Same(Convert.ToHexString(SHA256.HashData(fixture)), "9a49280691c473164d889e1804fe1668893eeaa7f4193ab26a38a94987fb7ce4"))
            throw new IOException("The native capability fixture does not match this build.");
        return fixture;
    }
    private static byte[] BareImage(byte[] fixture)
    {
        using var output = new MemoryStream(); output.Write(fixture, 0, 8);
        for (var offset = 8; offset < fixture.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(fixture.AsSpan(offset, 4)) + 12);
            var type = System.Text.Encoding.ASCII.GetString(fixture, offset + 4, 4);
            if (type is not ("iCCP" or "eXIf")) output.Write(fixture, offset, length);
            offset += length;
        }
        return output.ToArray();
    }
    private static byte[] Wave()
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8); writer.Write(36 + 48000 * 4); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(48000 * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(48000 * 4);
        for (var frame = 0; frame < 48000; frame++)
        {
            writer.Write((short)(3276 * Math.Sin(2 * Math.PI * 440 * frame / 48000)));
            writer.Write((short)(3276 * Math.Sin(2 * Math.PI * 880 * frame / 48000)));
        }
        return output.ToArray();
    }
    private static async Task<string> Hash(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
