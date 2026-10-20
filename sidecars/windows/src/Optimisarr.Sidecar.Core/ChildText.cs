using System.Runtime.CompilerServices;
using System.Text;

namespace Optimisarr.Sidecar.Core;

internal static class ChildText
{
    public const int EvidenceLimit = 1024 * 1024;
    private const int LineLimit = 4096;
    internal readonly record struct Line(string Text, bool Truncated);

    public static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length + count > EvidenceLimit)
                throw new InvalidOperationException("Child process evidence exceeded the 1 MiB limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    public static async IAsyncEnumerable<Line> ReadLinesAsync(
        TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var text = new StringBuilder(LineLimit);
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    yield return new(text.ToString().TrimEnd('\r'), truncated);
                    text.Clear();
                    truncated = false;
                }
                else if (text.Length < LineLimit) text.Append(character);
                else truncated = true;
            }
        }
        if (text.Length != 0 || truncated) yield return new(text.ToString().TrimEnd('\r'), truncated);
    }
}
