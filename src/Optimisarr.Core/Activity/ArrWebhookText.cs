using System.Globalization;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Makes text a webhook caller chose safe to show: in the log, in the receipt sent back, and in the
/// outcome saved on the connection. A line break could forge a log line and a direction override could
/// disguise a path, so control and formatting characters become <c>?</c>; anything longer than the
/// limit is cut, so a caller cannot make the log or the saved outcome arbitrarily large.
/// </summary>
public static class ArrWebhookText
{
    public const int MaxPathLength = 512;

    public const int MaxDetailLength = 1024;

    public const int MaxEventTypeLength = 64;

    private const char Cut = '…';

    public static string Shown(string text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        var shown = string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = IsHidden(source[i]) ? '?' : source[i];
            }
        });

        if (shown.Length <= maxLength)
        {
            return shown;
        }

        var keep = maxLength - 1;
        if (char.IsHighSurrogate(shown[keep - 1]))
        {
            keep--;
        }
        return string.Concat(shown.AsSpan(0, keep), [Cut]);
    }

    private static bool IsHidden(char character) =>
        char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format;
}
