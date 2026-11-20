using Avalonia.Input;
using ImageViewer.Helpers;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Tests;

public class ImageTransformTests
{
    [Fact]
    public void Four_clockwise_rotations_return_to_identity()
    {
        var t = ImageTransform.Identity;
        for (var i = 0; i < 4; i++)
        {
            t = t.RotateClockwise();
        }

        Assert.True(t.IsIdentity);
    }

    [Fact]
    public void Opposite_operations_cancel_out()
    {
        var t = ImageTransform.Identity.FlipHorizontally().RotateClockwise();
        Assert.True(t.RotateCounterClockwise().FlipHorizontally().IsIdentity);
        Assert.True(ImageTransform.Identity.FlipVertically().FlipVertically().IsIdentity);
        Assert.True(ImageTransform.Identity.FlipHorizontally().FlipHorizontally().IsIdentity);
    }

    [Fact]
    public void Quarter_turns_swap_dimensions()
    {
        var t = ImageTransform.Identity.RotateClockwise();
        Assert.True(t.SwapsDimensions);
        Assert.Equal((300u, 400u), t.Apply(400, 300));
        Assert.Equal((400u, 300u), t.RotateClockwise().Apply(400, 300));
    }

    [Fact]
    public void Flip_vertical_equals_flip_horizontal_plus_half_turn()
    {
        var t = ImageTransform.Identity.FlipVertically();
        Assert.Equal(new ImageTransform(180, true), t);
    }
}

public class NewShortcutTests
{
    [Theory]
    [InlineData(Key.R, ShortcutAction.RotateClockwise)]
    [InlineData(Key.L, ShortcutAction.RotateCounterClockwise)]
    [InlineData(Key.H, ShortcutAction.FlipHorizontal)]
    [InlineData(Key.V, ShortcutAction.FlipVertical)]
    [InlineData(Key.Tab, ShortcutAction.ToggleInfoPanel)]
    [InlineData(Key.PageUp, ShortcutAction.PreviousPage)]
    [InlineData(Key.PageDown, ShortcutAction.NextPage)]
    [InlineData(Key.OemOpenBrackets, ShortcutAction.PreviousSource)]
    [InlineData(Key.OemCloseBrackets, ShortcutAction.NextSource)]
    [InlineData(Key.C, ShortcutAction.ToggleComicMode)]
    [InlineData(Key.D, ShortcutAction.ToggleComicDirection)]
    public void Maps_feature_shortcuts(Key key, ShortcutAction expected) =>
        Assert.Equal(expected, KeyboardHelper.GetAction(key, KeyModifiers.None));

    [Fact]
    public void Finds_adjacent_folders_in_natural_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "iv_adj_dir_" + Guid.NewGuid().ToString("N"));
        try
        {
            var d1 = Directory.CreateDirectory(Path.Combine(root, "ch1")).FullName;
            var d2 = Directory.CreateDirectory(Path.Combine(root, "ch2")).FullName;
            var d10 = Directory.CreateDirectory(Path.Combine(root, "ch10")).FullName;

            var info = new ImageSourceInfo(d2, ImageSourceType.File);
            Assert.Equal(d1, ViewModels.MainViewModel.FindAdjacentSourcePath(info, -1));
            Assert.Equal(d10, ViewModels.MainViewModel.FindAdjacentSourcePath(info, +1));
            Assert.Null(ViewModels.MainViewModel.FindAdjacentSourcePath(new ImageSourceInfo(d1, ImageSourceType.File), -1));
            Assert.Null(ViewModels.MainViewModel.FindAdjacentSourcePath(new ImageSourceInfo(d10, ImageSourceType.File), +1));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Finds_adjacent_zip_files_in_natural_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "iv_adj_zip_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var z1 = Path.Combine(root, "vol1.zip");
            var z2 = Path.Combine(root, "vol2.cbz");
            var z10 = Path.Combine(root, "vol10.zip");
            var txt = Path.Combine(root, "readme.txt");
            File.WriteAllBytes(z1, []);
            File.WriteAllBytes(z2, []);
            File.WriteAllBytes(z10, []);
            File.WriteAllBytes(txt, []);

            var info = new ImageSourceInfo(z2, ImageSourceType.Zip);
            Assert.Equal(z1, ViewModels.MainViewModel.FindAdjacentSourcePath(info, -1));
            Assert.Equal(z10, ViewModels.MainViewModel.FindAdjacentSourcePath(info, +1));
            Assert.Null(ViewModels.MainViewModel.FindAdjacentSourcePath(new ImageSourceInfo(z1, ImageSourceType.Zip), -1));
            Assert.Null(ViewModels.MainViewModel.FindAdjacentSourcePath(new ImageSourceInfo(z10, ImageSourceType.Zip), +1));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

public class ExifFormattingTests
{
    [Fact]
    public void Formats_exif_dates() =>
        Assert.Equal("2026-11-15 10:20:30", ExifReader.FormatExifDate("2026:11:15 10:20:30"));

    [Theory]
    [InlineData(1u, 250u, "1/250 초")]
    [InlineData(2u, 1u, "2 초")]
    public void Formats_exposure(uint numerator, uint denominator, string expected) =>
        Assert.Equal(expected, ExifReader.FormatExposure(new ImageMagick.Rational(numerator, denominator)));
}
