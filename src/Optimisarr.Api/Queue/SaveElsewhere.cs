using Optimisarr.Api.Library;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>Where a verified output was saved, or why it was not. Exactly one is set.</summary>
internal sealed record SavedElsewhere(string? Destination, string? Error);

/// <summary>
/// "Save optimised files in another folder", for one job. The folder was checked when the library
/// was saved, but a library can be added, a folder linked, or an older row can predate the rule.
/// A file saved inside a library would be scanned and optimised again; one in the working space or
/// Quarantine would be cleaned up. So the rule is applied again before encoding and again before
/// the file moves, and a refusal writes nothing. The original is never touched here.
/// </summary>
internal sealed class SaveElsewhere(IServiceScopeFactory scopeFactory, IHostEnvironment environment)
{
    private const string NoFolder =
        "No folder is set for optimised files. Choose one in the library's settings, under Schedule & replace. Your original has not been touched.";

    /// <summary>Why this library's save folder cannot be used, or null when it can.</summary>
    public async Task<string?> RefusalBeforeEncodingAsync(string targetFolder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            return $"Not encoded. {NoFolder}";
        }

        var verdict = await JudgeAsync(targetFolder, cancellationToken);
        return verdict.IsAllowed
            ? null
            : $"Not encoded. {verdict.Describe(targetFolder)} Change the folder in the library's settings, under Schedule & replace. Your original has not been touched.";
    }

    /// <summary>
    /// Moves the work output to its place under the save folder. On a refusal, or when a file is
    /// already there and overwrite is off, nothing is written and the output stays in the work
    /// folder.
    /// </summary>
    public async Task<SavedElsewhere> SaveAsync(string mediaWorkRoot, string outputPath, string targetFolder, bool overwrite)
    {
        // A blank folder would resolve to the server's own working directory.
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            return new SavedElsewhere(null, $"The optimised file was not saved. {NoFolder}");
        }

        var destination = MoveTarget.Resolve(mediaWorkRoot, outputPath, targetFolder);
        var verdict = await JudgeAsync(destination, CancellationToken.None);
        if (!verdict.IsAllowed)
        {
            return new SavedElsewhere(null,
                $"The optimised file was not saved. {verdict.Describe(destination)} Your original has not been touched.");
        }

        // Don't silently clobber an existing converted file unless the library opts into it.
        if (!overwrite && File.Exists(destination))
        {
            return new SavedElsewhere(null,
                $"A converted file already exists at the destination and overwrite is off: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        MoveFile(outputPath, destination);
        return new SavedElsewhere(destination, null);
    }

    private async Task<TargetFolderVerdict> JudgeAsync(string path, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await TargetFolderGuard.CheckDestinationAsync(
            path, scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>(), environment, cancellationToken);
    }

    // Move our own work output; never the original. Falls back to copy+delete across
    // filesystems, where an atomic rename isn't possible.
    private static void MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }
    }
}
