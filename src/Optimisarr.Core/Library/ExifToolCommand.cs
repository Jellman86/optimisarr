namespace Optimisarr.Core.Library;

public sealed record ExifToolInvocation(IReadOnlyList<string> Arguments, string? StandardInput);

/// <summary>Windows ExifTool reads UTF-8 argument lines to preserve Unicode and literal brackets.</summary>
public static class ExifToolCommand
{
    public static ExifToolInvocation Build(IReadOnlyList<string> arguments, bool windows)
    {
        if (!windows) return new(arguments, null);
        if (arguments.Any(value => value.Contains('\n') || value.Contains('\r')))
            throw new ArgumentException("Windows ExifTool argument lines cannot contain line breaks.", nameof(arguments));
        return new(["-charset", "filename=UTF8", "-@", "-"], string.Join('\n', arguments) + "\n");
    }
}
