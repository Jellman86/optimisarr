namespace Optimisarr.Core.Tools;

/// <summary>Reads <c>ffmpeg -filters</c> output without confusing similarly named filters.</summary>
public static class FfmpegFilterParser
{
    public static bool Contains(string output, string filterName)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(filterName))
        {
            return false;
        }

        return output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Any(columns => columns.Length >= 2
                && string.Equals(columns[1], filterName, StringComparison.Ordinal));
    }
}
