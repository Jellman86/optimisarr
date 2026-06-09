using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class DecodeIntegrityParserTests
{
    [Fact]
    public void Clean_decode_has_no_errors()
    {
        var integrity = DecodeIntegrityParser.Parse("");

        Assert.Equal(0, integrity.ErrorCount);
        Assert.Null(integrity.FirstError);
    }

    [Fact]
    public void Counts_every_error_line_and_keeps_the_first()
    {
        const string stderr =
            "[h264 @ 0x55] error while decoding MB 10 20, bytestream -7\n" +
            "[h264 @ 0x55] concealing 400 DC, 400 AC errors\n" +
            "[matroska,webm @ 0x60] Read error\n";

        var integrity = DecodeIntegrityParser.Parse(stderr);

        Assert.Equal(3, integrity.ErrorCount);
        Assert.Contains("error while decoding MB", integrity.FirstError);
    }

    [Fact]
    public void Blank_lines_are_ignored()
    {
        var integrity = DecodeIntegrityParser.Parse("\n\n  \n[h264 @ 0x55] error\n\n");

        Assert.Equal(1, integrity.ErrorCount);
    }
}
