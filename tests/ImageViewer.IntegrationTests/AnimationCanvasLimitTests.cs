using ImageMagick;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;

namespace ImageViewer.IntegrationTests;

/// <summary>
/// 애니메이션 캔버스(GIF 논리 화면, WebP 캔버스)가 픽셀 수 한도를 우회하지 못하는지 검사한다.
/// 프레임은 1×1 이라 ImageMagick Ping 은 1×1 로 보지만, 재생 · 썸네일은 캔버스 크기로 디코딩한다.
/// </summary>
public class AnimationCanvasLimitTests
{
    private const long Limit = 150_000_000;

    /// <summary>논리 화면이 <paramref name="screenWidth"/>×<paramref name="screenHeight"/> 이고 1×1 프레임 2개인 GIF.</summary>
    internal static byte[] GifWithCanvas(ushort screenWidth, ushort screenHeight)
    {
        var ms = new MemoryStream();
        void Write(params byte[] bytes) => ms.Write(bytes);
        void WriteUInt16(ushort value) => Write((byte)(value & 0xFF), (byte)(value >> 8));

        Write("GIF89a"u8.ToArray());
        WriteUInt16(screenWidth);
        WriteUInt16(screenHeight);
        Write(0x80, 0, 0);                 // 전역 색상표 (2색)
        Write(0, 0, 0, 255, 255, 255);
        Write(0x21, 0xFF, 0x0B);           // NETSCAPE2.0 반복
        Write("NETSCAPE2.0"u8.ToArray());
        Write(3, 1, 0, 0, 0);
        for (var i = 0; i < 2; i++)
        {
            Write(0x21, 0xF9, 4, 0, 10, 0, 0, 0); // 지연 100ms
            Write(0x2C);
            WriteUInt16(0);
            WriteUInt16(0);
            WriteUInt16(1);
            WriteUInt16(1);
            Write(0);
            Write(2, 2, 0x4C, 0x01, 0);    // LZW: 픽셀 1개
        }

        Write(0x3B);
        return ms.ToArray();
    }

    /// <summary>
    /// 캔버스가 <paramref name="width"/>×<paramref name="height"/> 이고 1×1 프레임 2개인 애니메이션 WebP.
    /// ImageMagick 은 캔버스를 프레임 크기(1×1)로 쓰므로, VP8X 헤더의 캔버스 크기 필드를 직접 바꾼다.
    /// </summary>
    internal static byte[] WebpWithCanvas(uint width, uint height)
    {
        using var collection = new MagickImageCollection();
        foreach (var color in new[] { MagickColors.Red, MagickColors.Blue })
        {
            collection.Add(new MagickImage(color, 1, 1) { AnimationDelay = 10 });
        }

        var data = collection.ToByteArray(MagickFormat.WebP);
        Assert.Equal("VP8X", System.Text.Encoding.ASCII.GetString(data, 12, 4));

        // VP8X: 오프셋 24 에 (캔버스 폭 - 1), 27 에 (캔버스 높이 - 1), 각각 24비트 리틀 엔디언.
        void Write24(int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
        }

        Write24(24, width - 1);
        Write24(27, height - 1);
        return data;
    }

    [Fact]
    public void Gif_canvas_over_the_limit_is_rejected_even_when_frames_are_tiny()
    {
        var data = GifWithCanvas(15000, 15000); // 225 MP > 150 MP
        Assert.Equal(1u, new MagickImageInfo(data).Width); // Ping 은 첫 프레임 크기만 본다

        var full = Assert.Throws<ImageLoadException>(() => ImageLoaderService.Decode(new MemoryStream(data), Limit, null, "big.gif"));
        Assert.Contains("너무 큽니다", full.Message);
        Assert.Contains("15,000 × 15,000", full.Message);

        // 썸네일도 같은 경로로 캔버스를 디코딩하므로 막아야 한다.
        Assert.Throws<ImageLoadException>(() => ImageLoaderService.Decode(new MemoryStream(data), Limit, 96, "big.gif"));
    }

    [Fact]
    public void Gif_canvas_within_the_limit_still_plays()
    {
        var data = GifWithCanvas(200, 100);
        var result = ImageLoaderService.Decode(new MemoryStream(data), Limit, null, "ok.gif");
        using (result.Image)
        {
            Assert.Equal(200u, result.Image.Width);
            Assert.Equal(100u, result.Image.Height);
            Assert.Equal(2, result.Animation?.FrameCount);
        }
    }

    /// <summary>WebP 는 ImageMagick Ping 이 캔버스 크기를 보므로 원래 막혀 있었다. 회귀 방지용.</summary>
    [Fact]
    public void Webp_canvas_over_the_limit_is_rejected()
    {
        var data = WebpWithCanvas(16000, 16000); // 256 MP > 150 MP

        // 한도 안의 캔버스는 그대로 재생된다 (헤더를 고친 파일이 유효한지 함께 확인).
        var ok = ImageLoaderService.Decode(new MemoryStream(WebpWithCanvas(300, 200)), Limit, null, "ok.webp");
        using (ok.Image)
        {
            Assert.Equal(300u, ok.Image.Width);
            Assert.Equal(2, ok.Animation?.FrameCount);
        }

        var ex = Assert.Throws<ImageLoadException>(() => ImageLoaderService.Decode(new MemoryStream(data), Limit, null, "big.webp"));
        Assert.Contains("너무 큽니다", ex.Message);
    }

    [Fact]
    public async Task Loader_reports_oversized_canvas_as_an_error_message()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("bomb.gif"), GifWithCanvas(20000, 20000));
        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        using var folder = new FolderImageSource(temp.Path);
        var info = (await folder.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(settings);

        var ex = await Assert.ThrowsAsync<ImageLoadException>(() => loader.LoadAsync(folder, info, CancellationToken.None));
        Assert.Contains("너무 큽니다", ex.Message);
    }
}
