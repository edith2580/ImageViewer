using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using ImageMagick;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;
using ImageViewer.ViewModels;

namespace ImageViewer.IntegrationTests;

public class AnimationPlaybackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>단색 프레임 <paramref name="frames"/> 장짜리 애니메이션 WebP (프레임마다 색이 다르다, 40ms).</summary>
    private static byte[] CreateWebP(int frames, uint width, uint height)
    {
        using var collection = new MagickImageCollection();
        for (var i = 0; i < frames; i++)
        {
            collection.Add(new MagickImage(new MagickColor((byte)(i * 37 % 256), (byte)(i * 91 % 256), 128), width, height)
            {
                AnimationDelay = 4,
            });
        }

        return collection.ToByteArray(MagickFormat.WebP);
    }

    private static AnimationSource Source(byte[] data, string format = "WEBP") =>
        AnimationSource.TryCreate(data, format) ?? throw new InvalidOperationException("애니메이션이 아님");

    private static byte[] Pixels(Bitmap bitmap)
    {
        var wb = (WriteableBitmap)bitmap;
        using var fb = wb.Lock();
        var stride = wb.PixelSize.Width * 4;
        var bytes = new byte[stride * wb.PixelSize.Height];
        for (var y = 0; y < wb.PixelSize.Height; y++)
        {
            Marshal.Copy(fb.Address + y * fb.RowBytes, bytes, y * stride, stride);
        }

        return bytes;
    }

    [Fact]
    public async Task Animation_over_the_old_total_pixel_limit_still_plays()
    {
        // 400×300 × 100 프레임 = 12 MP: 예전에는 프레임 합이 한도(10 MP)를 넘으면 첫 프레임만 표시했다.
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("big.webp"), CreateWebP(100, 400, 300));
        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        settings.Current.Limits.MaxImageMegapixels = 10;
        using var folder = new FolderImageSource(temp.Path);
        var info = (await folder.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(settings);

        var decoded = await loader.LoadAsync(folder, info, CancellationToken.None);
        Assert.True(decoded.IsAnimated);
        Assert.Equal(100, decoded.FrameCount);
        Assert.Equal(400u, decoded.Width);
        Assert.Equal("WEBP", decoded.Format);
        Assert.All(decoded.FrameDelaysMs, d => Assert.Equal(40, d));

        using var playback = new AnimationPlayback(decoded.Animation!, ImageTransform.Identity);
        var last = await playback.WaitForFrameAsync(99, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(400, last.PixelSize.Width);
        decoded.Release();
    }

    [Theory]
    [InlineData("GIF", new[] { 0, 5, 10, 20, 40 }, new[] { 100, 100, 100, 20, 40 })]
    [InlineData("WEBP", new[] { 40, 40, 40, 40, 1, 39 }, new[] { 40, 40, 40, 40, 1, 39 })]   // 동영상 변환 WebP
    [InlineData("WEBP", new[] { 40, 0, 40 }, new[] { 40, 1, 40 })]
    [InlineData("WEBP", new[] { 0, 0, 0 }, new[] { 100, 100, 100 })]                           // 지연 정보 없음
    public void Frame_delays_are_normalized_per_format(string format, int[] durations, int[] expected) =>
        Assert.Equal(expected, AnimationSource.NormalizeDelays(durations, format));

    [Fact]
    public void Thumbnail_decode_reads_only_the_first_frame()
    {
        using var stream = new MemoryStream(CreateWebP(20, 200, 100));
        var result = ImageLoaderService.Decode(stream, 150_000_000, thumbnailHint: 96, "a.webp");
        Assert.Null(result.Animation);
        Assert.Equal(200u, result.Image.Width);
        result.Image.Dispose();
    }

    [Fact]
    public async Task Partial_frames_are_composited_like_imagemagick()
    {
        // 바탕 위에 작은 프레임을 위치를 바꿔 가며 겹치는 GIF (Background 정리 포함).
        using var collection = new MagickImageCollection();
        var background = new MagickImage(MagickColors.Red, 40, 30) { AnimationDelay = 10 };
        collection.Add(background);
        var blue = new MagickImage(MagickColors.Blue, 10, 10) { AnimationDelay = 10, GifDisposeMethod = GifDisposeMethod.Background };
        blue.Page = new MagickGeometry(5, 5, 10, 10);
        collection.Add(blue);
        var lime = new MagickImage(MagickColors.Lime, 12, 8) { AnimationDelay = 10 };
        lime.Page = new MagickGeometry(20, 12, 12, 8);
        collection.Add(lime);
        var gif = collection.ToByteArray(MagickFormat.Gif);

        using var expected = new MagickImageCollection(gif);
        expected.Coalesce();

        using var playback = new AnimationPlayback(Source(gif, "GIF"), ImageTransform.Identity);
        Assert.Equal(expected.Count, playback.FrameCount);
        for (var i = 0; i < expected.Count; i++)
        {
            var frame = await playback.WaitForFrameAsync(i, CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal(40, frame.PixelSize.Width);
            var actual = Pixels(frame);
            using var pixels = expected[i].GetPixels();
            var reference = pixels.ToByteArray(PixelMapping.BGRA)!;
            for (var p = 0; p < actual.Length; p += 4)
            {
                if (reference[p + 3] == 0)
                {
                    Assert.Equal(0, actual[p + 3]); // 투명 (premultiplied 라 색 값은 비교하지 않는다)
                    continue;
                }

                Assert.True(actual.AsSpan(p, 4).SequenceEqual(reference.AsSpan(p, 4)), $"프레임 {i} 픽셀 {p / 4} 이 다름");
            }
        }
    }

    [Fact]
    public async Task Over_budget_animation_keeps_only_a_window_ahead()
    {
        var source = Source(CreateWebP(60, 100, 100));          // 프레임당 40 KB
        using var playback = new AnimationPlayback(source, ImageTransform.Identity, budgetBytes: 10 * 40_000);
        Assert.False(playback.FitsInBudget);
        Assert.Equal(10, playback.Window);

        // 두 바퀴 재생: 다음 프레임은 항상 준비되고, 보관 프레임 수는 한도를 넘지 않는다.
        for (var step = 0; step < 120; step++)
        {
            var index = step % 60;
            await playback.WaitForFrameAsync(index, CancellationToken.None).WaitAsync(Timeout);
            playback.SetPosition(index);
            Assert.True(playback.CachedCount <= playback.Window, $"{playback.CachedCount} > {playback.Window}");
        }

        // 지나간 프레임은 버린다.
        Assert.Null(playback.TryGetFrame(30));
    }

    [Fact]
    public async Task Rotation_and_scale_are_applied_to_frames()
    {
        var source = Source(CreateWebP(3, 200, 100));
        using var rotated = new AnimationPlayback(source, ImageTransform.Identity.RotateClockwise());
        var frame = await rotated.WaitForFrameAsync(1, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(100, frame.PixelSize.Width);
        Assert.Equal(200, frame.PixelSize.Height);

        using var half = new AnimationPlayback(source, ImageTransform.Identity, scale: 0.5);
        var small = await half.WaitForFrameAsync(2, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(100, small.PixelSize.Width);
        Assert.Equal(50, small.PixelSize.Height);
        Assert.Equal(0.5, half.Scale, 2);
    }

    [Fact]
    public void Scale_is_reduced_only_when_frames_do_not_fit()
    {
        using var image = new MagickImage(MagickColors.Red, 2000, 1000);
        var info = new ImageInfo("a.webp", ImageSourceType.File, "a.webp", null, 0, null);
        var decoded = new DecodedImage(info, new DecodeResult(image, "WEBP"));

        // 3 프레임은 원본 크기로 모두 보관할 수 있다: 화면이 작아도 줄이지 않는다.
        var few = Source(CreateWebP(3, 2000, 1000));
        Assert.Equal(1, MainViewModel.AnimationScale(decoded, few, ImageTransform.Identity, 500, 250));

        // 2000×1000 × 100 프레임(800 MB)은 한도(512 MB)를 넘는다: 모두 보관할 수 있는 크기까지만 줄인다.
        var many = Source(CreateWebP(100, 2000, 1000));
        var fitScale = Math.Sqrt(AnimationPlayback.DefaultBudgetBytes / (2000.0 * 1000 * 4 * 100));
        Assert.Equal(fitScale, MainViewModel.AnimationScale(decoded, many, ImageTransform.Identity, 1000, 500), 3);

        // 화면에 그보다 큰 크기가 필요하면 화면 크기까지 (이때는 앞쪽 프레임만 보관하며 재생한다).
        Assert.Equal(0.9, MainViewModel.AnimationScale(decoded, many, ImageTransform.Identity, 1800, 900), 3);

        // 화면보다 크게 보여야 하면 원본 크기.
        Assert.Equal(1, MainViewModel.AnimationScale(decoded, many, ImageTransform.Identity, 4000, 2000));
    }
}
