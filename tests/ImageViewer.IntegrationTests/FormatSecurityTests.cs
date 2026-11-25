using System.Text;
using ImageMagick;
using ImageViewer.Helpers;
using ImageViewer.Services;

namespace ImageViewer.IntegrationTests;

/// <summary>#11: 형식 허용 목록과 ImageMagick 보안 정책.</summary>
public class FormatSecurityTests
{
    private const long Limit = 150_000_000;

    [Fact]
    public void Policy_is_applied_in_this_process() => Assert.True(AvaloniaTestInit.MagickPolicyApplied);

    [Theory]
    [InlineData(MagickFormat.Svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>")]
    [InlineData(MagickFormat.Mvg, "viewbox 0 0 10 10 fill red rectangle 0,0 10,10")]
    [InlineData(MagickFormat.Ps, "%!PS\n0 0 moveto showpage")]
    [InlineData(MagickFormat.Text, "hello")]
    public void Dangerous_coders_are_blocked_by_imagemagick_even_when_asked_explicitly(MagickFormat format, string content)
    {
        // 앱 코드의 헤더 검사 · 허용 목록을 거치지 않고 ImageMagick 에 직접 형식을 지정해도 정책이 막는다.
        var settings = new MagickReadSettings { Format = format };
        Assert.ThrowsAny<MagickException>(() => new MagickImageInfo(Encoding.UTF8.GetBytes(content), settings));
        Assert.ThrowsAny<MagickException>(() => new MagickImage(Encoding.UTF8.GetBytes(content), settings));
    }

    public static TheoryData<MagickFormat, string> SupportedSamples => new()
    {
        { MagickFormat.Jpeg, "a.jpg" },
        { MagickFormat.Png, "a.png" },
        { MagickFormat.Bmp, "a.bmp" },
        { MagickFormat.Gif, "a.gif" },
        { MagickFormat.Tiff, "a.tif" },
        { MagickFormat.WebP, "a.webp" },
        { MagickFormat.Ico, "a.ico" },
        { MagickFormat.Tga, "a.tga" },
        { MagickFormat.Psd, "a.psd" },
        { MagickFormat.Pcx, "a.pcx" },
        { MagickFormat.Qoi, "a.qoi" },
        { MagickFormat.Ppm, "a.ppm" },
        { MagickFormat.Pgm, "a.pgm" },
        { MagickFormat.Pbm, "a.pbm" },
        { MagickFormat.Hdr, "a.hdr" },
        { MagickFormat.Dds, "a.dds" },
        { MagickFormat.Jp2, "a.jp2" },
        { MagickFormat.Jxl, "a.jxl" },
        { MagickFormat.Avif, "a.avif" },
        { MagickFormat.Heic, "a.heic" },
        { MagickFormat.Exr, "a.exr" },
    };

    [Theory]
    [MemberData(nameof(SupportedSamples))]
    public void Supported_formats_still_decode_under_the_allowlist_and_policy(MagickFormat format, string fileName)
    {
        Assert.True(ImageFormatHelper.IsSupportedExtension(fileName));

        byte[] data;
        try
        {
            using var image = new MagickImage(MagickColors.Orange, 24, 16);
            data = image.ToByteArray(format);
        }
        catch (MagickException)
        {
            return; // 이 빌드의 ImageMagick 에 해당 형식의 인코더가 없다 (디코딩 검사 불가)
        }

        var result = ImageLoaderService.Decode(new MemoryStream(data), Limit, null, fileName);
        using (result.Image)
        {
            Assert.Equal(24u, result.Image.Width);
            Assert.Equal(16u, result.Image.Height);
        }
    }

    [Theory]
    [InlineData(MagickFormat.Miff, "disguised.png")]
    [InlineData(MagickFormat.Xbm, "disguised.jpg")]
    [InlineData(MagickFormat.Sgi, "disguised.bmp")]
    public void Other_formats_are_rejected_even_with_an_image_extension(MagickFormat actual, string fileName)
    {
        using var image = new MagickImage(MagickColors.Orange, 8, 8);
        var data = image.ToByteArray(actual);

        var ex = Assert.Throws<ImageLoadException>(() => ImageLoaderService.Decode(new MemoryStream(data), Limit, null, fileName));
        Assert.Contains("지원하지 않는 형식", ex.Message);
    }
}
