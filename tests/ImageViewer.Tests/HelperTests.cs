using Avalonia.Input;
using ImageViewer.Helpers;

namespace ImageViewer.Tests;

public class NaturalStringComparerTests
{
    [Fact]
    public void Sorts_numbers_by_value()
    {
        var names = new List<string> { "image10.jpg", "image2.jpg", "image1.jpg", "Image3.jpg" };
        names.Sort(NaturalStringComparer.Instance);
        Assert.Equal(["image1.jpg", "image2.jpg", "Image3.jpg", "image10.jpg"], names);
    }

    [Fact]
    public void Handles_korean_spaces_and_special_characters()
    {
        var names = new List<string> { "사진 10.png", "사진 2.png", "가나다.png", "[표지] 01.png" };
        names.Sort(NaturalStringComparer.Instance);
        Assert.Equal("사진 2.png", names[^2]);
        Assert.Equal("사진 10.png", names[^1]);
    }

    [Fact]
    public void Leading_zeros_compare_by_value()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("p002", "p10") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("p01", "p1") < 0);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("same", "same"));
    }

    [Fact]
    public void Matches_windows_explorer_order_for_mixed_scripts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var names = new List<string> { "漢子", "가나다", "abc", "[표지]", "2", "10" };
        names.Sort(NaturalStringComparer.Instance);
        Assert.Equal(["[표지]", "2", "10", "abc", "가나다", "漢子"], names);
    }
}

public class FileSizeHelperTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(850 * 1024, "850 KB")]
    [InlineData(2_202_010, "2.1 MB")]
    [InlineData(-1, "-")]
    public void Formats(long bytes, string expected) => Assert.Equal(expected, FileSizeHelper.Format(bytes));
}

public class ImageFormatHelperTests
{
    [Theory]
    [InlineData("a.JPG", true)]
    [InlineData("b.webp", true)]
    [InlineData("c.heic", true)]
    [InlineData("d.svg", false)]
    [InlineData("e.txt", false)]
    [InlineData("noext", false)]
    public void Extension_filter(string name, bool expected) =>
        Assert.Equal(expected, ImageFormatHelper.IsSupportedExtension(name));

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", true)]
    [InlineData("  \r\n<?xml version=\"1.0\"?>", true)]
    [InlineData("%!PS-Adobe-3.0", true)]
    [InlineData("%PDF-1.7", true)]
    public void Detects_script_or_document_headers(string header, bool expected) =>
        Assert.Equal(expected, ImageFormatHelper.LooksLikeScriptOrDocument(System.Text.Encoding.UTF8.GetBytes(header)));

    [Fact]
    public void Png_header_is_not_script()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.False(ImageFormatHelper.LooksLikeScriptOrDocument(png));
    }

    [Fact]
    public void Zip_detection() =>
        Assert.True(ImageFormatHelper.IsZipFile(@"D:\Archives\images.ZIP") && ImageFormatHelper.IsZipFile("comic.cbz"));
}

public class CommandLineHelperTests
{
    [Fact]
    public void Drive_root_with_broken_quote_is_repaired()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 탐색기가 "D:\" 를 "%1" 로 넘기면 D:" 로 들어온다.
        var path = CommandLineHelper.NormalizePath("C:\"");
        Assert.Equal(@"C:\", path);
    }

    [Fact]
    public void Existing_folder_is_accepted()
    {
        var dir = Path.GetTempPath();
        var options = CommandLineHelper.Parse([dir]);
        Assert.Null(options.Error);
        Assert.Equal(Path.GetFullPath(dir), options.Path);
    }

    [Fact]
    public void Missing_path_reports_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), "iv-missing-" + Guid.NewGuid().ToString("N"));
        var options = CommandLineHelper.Parse([missing]);
        Assert.Null(options.Path);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void Shell_switches_are_parsed()
    {
        var options = CommandLineHelper.Parse(["--register-shell"]);
        Assert.True(options.RegisterShell);
        Assert.False(options.UnregisterShell);
        Assert.Null(options.Path);
    }
}

public class KeyboardHelperTests
{
    [Theory]
    [InlineData(Key.Left, ShortcutAction.Previous)]
    [InlineData(Key.Up, ShortcutAction.Previous)]
    [InlineData(Key.Right, ShortcutAction.Next)]
    [InlineData(Key.Down, ShortcutAction.Next)]
    [InlineData(Key.Enter, ShortcutAction.OpenImageList)]
    [InlineData(Key.Escape, ShortcutAction.Exit)]
    [InlineData(Key.Oem3, ShortcutAction.ToggleMenus)]
    [InlineData(Key.D0, ShortcutAction.ModeOriginal)]
    [InlineData(Key.D7, ShortcutAction.ModeFitWidth)]
    [InlineData(Key.D8, ShortcutAction.ModeFitHeight)]
    [InlineData(Key.D9, ShortcutAction.ModeFill)]
    [InlineData(Key.U, ShortcutAction.FilterNone)]
    [InlineData(Key.I, ShortcutAction.FilterSmooth)]
    [InlineData(Key.S, ShortcutAction.FilterSmoothSharpen)]
    [InlineData(Key.OemPlus, ShortcutAction.ZoomIn)]
    [InlineData(Key.OemMinus, ShortcutAction.ZoomOut)]
    [InlineData(Key.Space, ShortcutAction.ToggleSlideShow)]
    public void Maps_plan_shortcuts(Key key, ShortcutAction expected) =>
        Assert.Equal(expected, KeyboardHelper.GetAction(key, KeyModifiers.None));

    [Fact]
    public void Alt_enter_toggles_full_screen() =>
        Assert.Equal(ShortcutAction.ToggleFullScreen, KeyboardHelper.GetAction(Key.Enter, KeyModifiers.Alt));

    [Fact]
    public void Ctrl_combinations_are_left_to_menus() =>
        Assert.Equal(ShortcutAction.None, KeyboardHelper.GetAction(Key.O, KeyModifiers.Control));
}
