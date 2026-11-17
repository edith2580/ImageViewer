using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using ImageMagick;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;

namespace ImageViewer.IntegrationTests;

public class ImageLoaderServiceTests
{
    private static SettingsService Settings(TempFolder temp)
    {
        var s = new SettingsService(settingsPath: temp.File("settings.json"));
        s.Load();
        return s;
    }

    [Theory]
    [InlineData(MagickFormat.Jpeg, "x.jpg")]
    [InlineData(MagickFormat.Png, "x.png")]
    [InlineData(MagickFormat.Bmp, "x.bmp")]
    [InlineData(MagickFormat.Gif, "x.gif")]
    [InlineData(MagickFormat.Tiff, "x.tif")]
    [InlineData(MagickFormat.WebP, "x.webp")]
    [InlineData(MagickFormat.Ico, "x.ico")]
    public async Task Decodes_common_formats(MagickFormat format, string name)
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File(name), TestImages.Create(32, 16, format));
        using var source = new FolderImageSource(temp.Path);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();

        using var loader = new ImageLoaderService(Settings(temp));
        var decoded = await loader.LoadAsync(source, info, CancellationToken.None);
        try
        {
            Assert.Equal(32u, decoded.Width);
            Assert.Equal(16u, decoded.Height);
        }
        finally
        {
            decoded.Release();
        }
    }

    [Fact]
    public async Task Applies_exif_orientation()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("rotated.jpg"), TestImages.Create(40, 20, MagickFormat.Jpeg, orientation: Orientation.RightTop));
        using var source = new FolderImageSource(temp.Path);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();

        using var loader = new ImageLoaderService(Settings(temp));
        var decoded = await loader.LoadAsync(source, info, CancellationToken.None);
        Assert.Equal(20u, decoded.Width);
        Assert.Equal(40u, decoded.Height);
        decoded.Release();
    }

    [Fact]
    public async Task Corrupt_image_throws_image_load_exception()
    {
        using var temp = new TempFolder();
        var bytes = TestImages.Create(64, 64, MagickFormat.Jpeg);
        File.WriteAllBytes(temp.File("broken.jpg"), bytes[..20]);
        File.WriteAllBytes(temp.File("random.png"), Enumerable.Range(0, 500).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllBytes(temp.File("empty.gif"), []);
        using var source = new FolderImageSource(temp.Path);
        var images = await source.GetImagesAsync(CancellationToken.None);

        using var loader = new ImageLoaderService(Settings(temp));
        foreach (var info in images)
        {
            await Assert.ThrowsAsync<ImageLoadException>(() => loader.LoadAsync(source, info, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Svg_disguised_as_png_is_rejected()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("fake.png"), "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>");
        using var source = new FolderImageSource(temp.Path);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(Settings(temp));
        await Assert.ThrowsAsync<ImageLoadException>(() => loader.LoadAsync(source, info, CancellationToken.None));
    }

    [Fact]
    public async Task Mismatched_extension_is_decoded_by_content()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("actually-png.jpg"), TestImages.Png(9, 7));
        using var source = new FolderImageSource(temp.Path);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(Settings(temp));
        var decoded = await loader.LoadAsync(source, info, CancellationToken.None);
        Assert.Equal("PNG", decoded.Format);
        decoded.Release();
    }

    [Fact]
    public void Pixel_limit_is_enforced_before_decoding()
    {
        using var stream = new MemoryStream(TestImages.Png(100, 100));
        var ex = Assert.Throws<ImageLoadException>(() => ImageLoaderService.Decode(stream, maxPixels: 5_000, null));
        Assert.Contains("너무 큽니다", ex.Message);
    }

    [Fact]
    public async Task Cached_image_survives_until_released()
    {
        using var temp = new TempFolder();
        for (var i = 0; i < 12; i++) // 캐시(8개)보다 많이 읽어 첫 이미지가 밀려나게 한다
        {
            File.WriteAllBytes(temp.File($"{i}.png"), TestImages.Png());
        }

        using var source = new FolderImageSource(temp.Path);
        var images = await source.GetImagesAsync(CancellationToken.None);
        using var loader = new ImageLoaderService(Settings(temp));

        var first = await loader.LoadAsync(source, images[0], CancellationToken.None);
        foreach (var info in images.Skip(1))
        {
            (await loader.LoadAsync(source, info, CancellationToken.None)).Release();
        }

        // 캐시에서 밀려났어도 호출자가 참조를 가진 동안에는 사용할 수 있다.
        using (var clone = first.CloneImage())
        {
            Assert.NotNull(clone);
        }

        first.Release();
        Assert.True(first.IsDisposed);
        Assert.Null(first.CloneImage());
    }
}

public class ImageRenderServiceTests
{
    /// <summary>검은색 | 흰색 경계에서 흐림이 번진 정도 (경계 옆 픽셀이 원래 값에서 벗어난 양, 0~1).</summary>
    private static double EdgeSpread(double displayScale)
    {
        using var temp = new TempFolder();
        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        using var image = new MagickImage(MagickColors.Black, 40, 10);
        image.Draw(new ImageMagick.Drawing.DrawableFillColor(MagickColors.White), new ImageMagick.Drawing.DrawableRectangle(20, 0, 40, 10));
        new ImageFilterService(settings).Apply(image, ImageFilter.Smooth, displayScale);
        using var pixels = image.GetPixels();
        var nextToEdge = pixels.GetPixel(19, 5)!.ToColor()!;
        return nextToEdge.R / 255.0;
    }

    [Fact]
    public void Smooth_strength_follows_screen_pixels()
    {
        var actualSize = EdgeSpread(1.0);
        var enlarged = EdgeSpread(4.0);   // 작은 이미지를 4배로 키워 볼 때: 이미지 픽셀 기준으로는 더 약하게
        var reduced = EdgeSpread(0.5);    // 화면에서 절반으로 줄여 볼 때: 이미지 픽셀 기준으로는 더 강하게

        Assert.True(actualSize > 0.01, $"기본 강도에서도 경계가 부드러워져야 함 ({actualSize:0.000})");
        Assert.True(enlarged < actualSize, $"{enlarged:0.000} < {actualSize:0.000}");
        Assert.True(reduced > actualSize, $"{reduced:0.000} > {actualSize:0.000}");
    }

    [Theory]
    [InlineData(ImageFilter.None)]
    [InlineData(ImageFilter.Smooth)]
    [InlineData(ImageFilter.SmoothSharpen)]
    public async Task Renders_display_size_bitmap_with_filters(ImageFilter filter)
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("a.png"), TestImages.Create(400, 200, MagickFormat.Png, "#3366cc"));
        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        using var source = new FolderImageSource(temp.Path);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(settings);
        var decoded = await loader.LoadAsync(source, info, CancellationToken.None);

        var renderer = new ImageRenderService(new ImageFilterService(settings));
        var bitmap = await renderer.RenderAsync(decoded, 100, 50, filter, ImageTransform.Identity, CancellationToken.None);

        Assert.Equal(100, bitmap.PixelSize.Width);
        Assert.Equal(50, bitmap.PixelSize.Height);

        // 원본은 변경되지 않는다.
        Assert.Equal(400u, decoded.Width);

        // 같은 요청은 캐시에서 반환된다.
        var again = await renderer.RenderAsync(decoded, 100, 50, filter, ImageTransform.Identity, CancellationToken.None);
        Assert.Same(bitmap, again);
        decoded.Release();
    }

    [Fact]
    public async Task Preserves_transparency()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("t.png"), TestImages.Create(10, 10, MagickFormat.Png, "transparent"));
        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        using var source = new FolderImageSource(temp.Path);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(settings);
        var decoded = await loader.LoadAsync(source, info, CancellationToken.None);
        var renderer = new ImageRenderService(new ImageFilterService(settings));
        var bitmap = await renderer.RenderAsync(decoded, 10, 10, ImageFilter.None, ImageTransform.Identity, CancellationToken.None);

        var wb = (WriteableBitmap)bitmap;
        var pixels = new byte[10 * 10 * 4];
        using (var fb = wb.Lock())
        {
            Marshal.Copy(fb.Address, pixels, 0, pixels.Length);
        }

        Assert.Equal(0, pixels[3]); // alpha
        decoded.Release();
    }
}

public class ThumbnailServiceTests
{
    [Fact]
    public async Task Creates_small_thumbnail()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("a.jpg"), TestImages.Create(800, 400, MagickFormat.Jpeg));
        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        using var source = new FolderImageSource(temp.Path);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();

        var service = new ThumbnailService(settings);
        var tcs = new TaskCompletionSource<(Bitmap?, bool)>();
        service.Request(source, info, 96, (b, done) => tcs.TrySetResult((b, done)));
        var (bitmap, completed) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(completed);
        Assert.NotNull(bitmap);
        Assert.Equal(96, bitmap!.PixelSize.Width);
        Assert.Equal(48, bitmap.PixelSize.Height);
    }
}
