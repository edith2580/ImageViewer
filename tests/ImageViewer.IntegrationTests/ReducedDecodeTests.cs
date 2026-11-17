using ImageMagick;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;

namespace ImageViewer.IntegrationTests;

public class ReducedDecodeTests
{
    private static readonly DecodeTarget FitWidth1280 = new(DisplayMode.FitWidth, 1264, 800);

    private static SettingsService Settings(TempFolder temp)
    {
        var s = new SettingsService(settingsPath: temp.File("settings.json"));
        s.Load();
        return s;
    }

    private static async Task<(FolderImageSource Source, ImageInfo Info)> Open(TempFolder temp)
    {
        var source = new FolderImageSource(temp.Path);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();
        return (source, info);
    }

    [Fact]
    public async Task Large_jpeg_is_decoded_at_screen_size_then_full_on_request()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("big.jpg"), TestImages.Create(4000, 3000, MagickFormat.Jpeg));
        var (source, info) = await Open(temp);
        using var _ = source;
        using var loader = new ImageLoaderService(Settings(temp));

        var reduced = await loader.LoadAsync(source, info, 0, FitWidth1280, CancellationToken.None);
        Assert.True(reduced.IsReduced);
        Assert.Equal(4000u, reduced.OriginalWidth);
        Assert.Equal(3000u, reduced.OriginalHeight);
        Assert.True(reduced.Width >= 1264 && reduced.Width < 4000, $"decoded width {reduced.Width}");
        Assert.True(FitWidth1280.IsSatisfiedBy(reduced));

        // 같은 조건이면 캐시된 축소 결과를 재사용한다.
        var again = await loader.LoadAsync(source, info, 0, FitWidth1280, CancellationToken.None);
        Assert.Same(reduced, again);
        again.Release();

        // 원본 요청은 원본 크기로 다시 읽는다.
        var full = await loader.LoadAsync(source, info, 0, target: null, CancellationToken.None);
        Assert.False(full.IsReduced);
        Assert.Equal(4000u, full.Width);
        Assert.NotEqual(reduced.Key, full.Key);

        // 원본이 캐시에 있으면 축소 요청에도 원본을 돌려준다.
        var afterFull = await loader.LoadAsync(source, info, 0, FitWidth1280, CancellationToken.None);
        Assert.Same(full, afterFull);

        reduced.Release();
        full.Release();
        afterFull.Release();
    }

    [Fact]
    public async Task Larger_viewport_needs_a_new_decode()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("big.jpg"), TestImages.Create(4000, 3000, MagickFormat.Jpeg));
        var (source, info) = await Open(temp);
        using var _ = source;
        using var loader = new ImageLoaderService(Settings(temp));

        var small = await loader.LoadAsync(source, info, 0, new DecodeTarget(DisplayMode.FitWidth, 600, 400), CancellationToken.None);
        var bigger = new DecodeTarget(DisplayMode.FitWidth, 2400, 1600);
        Assert.False(bigger.IsSatisfiedBy(small));

        var large = await loader.LoadAsync(source, info, 0, bigger, CancellationToken.None);
        Assert.True(large.Width >= 2400);
        small.Release();
        large.Release();
    }

    [Fact]
    public async Task Exif_rotated_jpeg_keeps_oriented_original_size()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("rotated.jpg"), TestImages.Create(4000, 2000, MagickFormat.Jpeg, orientation: Orientation.RightTop));
        var (source, info) = await Open(temp);
        using var _ = source;
        using var loader = new ImageLoaderService(Settings(temp));

        var decoded = await loader.LoadAsync(source, info, 0, new DecodeTarget(DisplayMode.FitHeight, 1000, 800), CancellationToken.None);
        Assert.Equal(2000u, decoded.OriginalWidth);
        Assert.Equal(4000u, decoded.OriginalHeight);
        Assert.True(decoded.IsReduced);
        Assert.True(decoded.Height >= 800 && decoded.Height < decoded.Width * 3, $"{decoded.Width}x{decoded.Height}");
        Assert.True(decoded.Height > decoded.Width, "세로 방향이 유지되어야 한다");
        decoded.Release();
    }

    [Fact]
    public async Task Non_jpeg_and_small_images_are_not_reduced()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("big.png"), TestImages.Create(3000, 2000, MagickFormat.Png));
        var (source, info) = await Open(temp);
        using var _ = source;
        using var loader = new ImageLoaderService(Settings(temp));

        var decoded = await loader.LoadAsync(source, info, 0, FitWidth1280, CancellationToken.None);
        Assert.False(decoded.IsReduced);
        Assert.Equal(3000u, decoded.Width);
        decoded.Release();

        Assert.Null(new DecodeTarget(DisplayMode.FitWidth, 1264, 800).RequiredSize(1300, 900));
        Assert.Null(new DecodeTarget(DisplayMode.Original, 1264, 800).RequiredSize(8000, 6000));
    }

    [Fact]
    public async Task Rendering_reduced_image_leaves_mild_downscale_to_screen()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("big.jpg"), TestImages.Create(4000, 3000, MagickFormat.Jpeg));
        var settings = Settings(temp);
        var (source, info) = await Open(temp);
        using var _ = source;
        using var loader = new ImageLoaderService(settings);
        var decoded = await loader.LoadAsync(source, info, 0, FitWidth1280, CancellationToken.None);

        var renderer = new ImageRenderService(new ImageFilterService(settings));
        var bitmap = await renderer.RenderAsync(decoded, 1264, 948, ImageFilter.None, ImageTransform.Identity, CancellationToken.None);

        // 축소 디코딩 결과(화면보다 조금 큼)는 CPU 로 다시 줄이지 않고 화면 표시 단계에서 줄인다.
        Assert.Equal((int)decoded.Width, bitmap.PixelSize.Width);
        Assert.True(bitmap.PixelSize.Width >= 1264 && bitmap.PixelSize.Height >= 948);
        Assert.Equal(4.0 / 3.0, (double)bitmap.PixelSize.Width / bitmap.PixelSize.Height, 2);
        decoded.Release();
    }

    [Theory]
    [InlineData(1920u, 1080u, 1280u, 720u)]   // 폭 맞춤 1280 창 (예전에는 5/8 = 1200 으로 부족했다)
    [InlineData(2560u, 1707u, 1280u, 854u)]
    [InlineData(4000u, 3000u, 1264u, 948u)]
    [InlineData(2592u, 1728u, 1280u, 854u)]
    [InlineData(6000u, 4000u, 700u, 467u)]
    public async Task Reduced_decode_is_never_smaller_than_required(uint width, uint height, uint requiredW, uint requiredH)
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("photo.jpg"), TestImages.Create(width, height, MagickFormat.Jpeg));
        var (source, info) = await Open(temp);
        using var _ = source;
        using var loader = new ImageLoaderService(Settings(temp));

        var target = new DecodeTarget(DisplayMode.FitWidth, requiredW, requiredW * 2);
        var decoded = await loader.LoadAsync(source, info, 0, target, CancellationToken.None);
        Assert.True(decoded.Width >= requiredW && decoded.Height >= requiredH - 1, $"{decoded.Width}x{decoded.Height} < {requiredW}x{requiredH}");
        Assert.True(target.IsSatisfiedBy(decoded), "미리 읽어 둔 결과를 그대로 다시 쓸 수 있어야 한다");
        decoded.Release();
    }

    [Fact]
    public void Jpeg_scale_hint_rounds_up_to_next_eighth()
    {
        // 1920 → 1280 필요: 5/8(1200) 은 부족하므로 6/8(1440) 이 되도록 요청
        Assert.Equal((1441u, 811u), ImageLoaderService.JpegScaleHint(1920, 1080, (1280, 720)));
        Assert.Null(ImageLoaderService.JpegScaleHint(1000, 800, (950, 760)));   // 8/8 → 축소 없음
        Assert.False(ImageRenderService.NeedsCpuResize(1440, 810, 1280, 720));   // 89% → 화면에서 축소
        Assert.True(ImageRenderService.NeedsCpuResize(4000, 3000, 1264, 948));   // 32% → CPU 축소
    }
}
