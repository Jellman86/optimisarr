using Microsoft.Extensions.Logging;

namespace Optimisarr.Api.Workers;

/// <summary>
/// How loudly the end of a per-title quality search should be said.
///
/// <para>Three outcomes wear one sentence otherwise. A search that chose a value is the feature
/// working. A search that ran and learned nothing, and so used the library's value, is the feature
/// <em>not</em> working — and logged at the same level as a success it cannot be told apart from
/// one. A hundred and seven jobs fell back over a fortnight, encoded at the library's quality, came
/// out larger than their sources and failed the size gate; the lines saying so were there the whole
/// time, at Information, among the ordinary ones.</para>
///
/// <para>The exception is a library with no quality gate. There is nothing for a search to measure
/// against and nothing wrong, so using the library's value is the configured answer rather than a
/// failure, and stays ordinary information. Treating that as a warning would put one in front of an
/// operator for every job in such a library and teach them to ignore the rest.</para>
/// </summary>
internal static class AdaptiveSelectionOutcome
{
    public static LogLevel SeverityOf(bool fellBack, bool expected) =>
        fellBack && !expected ? LogLevel.Warning : LogLevel.Information;

    /// <summary>What happened, in the words the log line uses.</summary>
    public static string Describe(bool fellBack, bool expected) => (fellBack, expected) switch
    {
        (false, _) => "selected a per-title value",
        (true, true) => "fell back to the library setting",
        (true, false) => "learned nothing and fell back to the library setting",
    };
}
