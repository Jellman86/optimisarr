using System.Xml.Linq;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class TrayMarkupTests
{
    [Fact]
    public void Buttons_with_a_menu_or_tooltip_keep_the_icon_font_on_their_glyph()
    {
        // A WPF button's context menu and tooltip inherit its font. With the icon font set on the
        // button itself, PICARD showed "Open Optimisarr" and "Quit tray" as rows of empty boxes.
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var markup = XDocument.Load(Path.Combine(RepositoryRoot(), "sidecars", "windows", "src",
            "Optimisarr.Sidecar.Tray", "MonitorWindow.xaml"));

        var offenders = markup.Descendants(wpf + "Button")
            .Where(button => button.Attribute("FontFamily")?.Value.Contains("Icons", StringComparison.Ordinal) == true
                && (button.Attribute("ToolTip") is not null || button.Element(wpf + "Button.ContextMenu") is not null))
            .Select(button => button.Attribute("ToolTip")?.Value ?? button.ToString())
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Optimisarr.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
