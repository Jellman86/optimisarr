namespace Optimisarr.Data;

/// <summary>SQLite stores EF DateTimeOffset as offset-bearing text. Keep exact UTC tick order.</summary>
internal static class SqliteUtcTicks
{
    public static string For(string expression) =>
        $"(CAST(strftime('%s', substr({expression}, 1, 19) || substr({expression}, -6)) AS INTEGER) * 10000000 + 621355968000000000 + "
        + $"CASE WHEN substr({expression}, 20, 1) = '.' THEN CAST(substr(substr({expression}, 21, length({expression}) - 26) || '0000000', 1, 7) AS INTEGER) ELSE 0 END)";
}
