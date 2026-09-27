using System.Globalization;
using System.Text;
using Optimisarr.Core.Calibration;
using Optimisarr.Core.Verification;

internal sealed record StudyRow(
    string Source, int Window, string Encoder, int Quality, long Bytes, string Model,
    double? Harmonic, double? FifthPercentile, double? Minimum, double? Mean, int? Frames, string? Error,
    string? ClipSha256 = null)
{
    public static string Csv(IEnumerable<StudyRow> rows)
    {
        var text = new StringBuilder("source,window,encoder,quality,bytes,model,harmonic,p5,min,mean,frames,error,clip_sha256\n");
        foreach (var row in rows)
        {
            text.AppendLine(string.Join(',',
                Quote(row.Source), row.Window, Quote(row.Encoder), row.Quality, row.Bytes, Quote(row.Model),
                Number(row.Harmonic), Number(row.FifthPercentile), Number(row.Minimum), Number(row.Mean),
                row.Frames?.ToString(CultureInfo.InvariantCulture) ?? "", Quote(row.Error ?? ""), Quote(row.ClipSha256 ?? "")));
        }
        return text.ToString();
    }

    /// <summary>Reads back what <see cref="Csv"/> wrote.</summary>
    public static List<StudyRow> Parse(string csv)
    {
        var rows = new List<StudyRow>();
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv))
        {
            TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        _ = parser.ReadFields();
        while (!parser.EndOfData)
        {
            var f = parser.ReadFields() ?? [];
            if (f.Length is not (12 or 13)) throw new FormatException("Expected twelve or thirteen CSV fields per measurement.");
            static double? D(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            rows.Add(new StudyRow(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), f[2],
                int.Parse(f[3], CultureInfo.InvariantCulture), long.Parse(f[4], CultureInfo.InvariantCulture), f[5],
                D(f[6]), D(f[7]), D(f[8]), D(f[9]),
                int.TryParse(f[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) ? frames : null,
                f[11].Length == 0 ? null : f[11], f.Length == 13 && f[12].Length > 0 ? f[12] : null));
        }
        return rows;
    }

    private static string Number(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
