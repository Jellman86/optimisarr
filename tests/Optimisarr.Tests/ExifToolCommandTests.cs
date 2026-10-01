using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class ExifToolCommandTests
{
    [Fact]
    public void Windows_filenames_travel_as_UTF8_argument_lines_without_console_globbing()
    {
        var command = ExifToolCommand.Build(["-s", @"C:\photos\source ' [字幕].jpg"], windows: true);
        Assert.Equal(new[] { "-charset", "filename=UTF8", "-@", "-" }, command.Arguments);
        Assert.Equal("-s\nC:\\photos\\source ' [字幕].jpg\n", command.StandardInput);
    }

    [Fact]
    public void Unix_arguments_retain_literal_line_breaks_in_filenames()
    {
        string[] arguments = ["-s", "/photos/line\nbreak.jpg"];
        var command = ExifToolCommand.Build(arguments, windows: false);
        Assert.Equal(arguments, command.Arguments);
        Assert.Null(command.StandardInput);
    }

    [Fact]
    public void Windows_argument_lines_cannot_smuggle_extra_options() =>
        Assert.Throws<ArgumentException>(() => ExifToolCommand.Build(["-s", "name\n-overwrite_original"], windows: true));
}
