using ImageMagick;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;

namespace ImageViewer.IntegrationTests;

public class TransformRenderingTests
{
    /// <summary>
    /// 3×2 이미지의 각 픽셀을 다른 색으로 만들어, 화면 기준 조작을 하나씩 적용한 결과와
    /// 합성된 <see cref="ImageTransform"/> 을 한 번에 적용한 결과가 같은지 확인한다.
    /// </summary>
    [Theory]
    [InlineData("R")]
    [InlineData("L")]
    [InlineData("H")]
    [InlineData("V")]
    [InlineData("RH")]
    [InlineData("HR")]
    [InlineData("VRL")]
    [InlineData("RRHVL")]
    public void Composed_transform_matches_sequential_operations(string operations)
    {
        using var source = CreatePattern();
        using var expected = (MagickImage)source.Clone();
        var transform = ImageTransform.Identity;
        foreach (var op in operations)
        {
            switch (op)
            {
                case 'R':
                    expected.Rotate(90);
                    transform = transform.RotateClockwise();
                    break;
                case 'L':
                    expected.Rotate(270);
                    transform = transform.RotateCounterClockwise();
                    break;
                case 'H':
                    expected.Flop();
                    transform = transform.FlipHorizontally();
                    break;
                case 'V':
                    expected.Flip();
                    transform = transform.FlipVertically();
                    break;
            }
        }

        using var actual = (MagickImage)source.Clone();
        transform.Apply(actual);

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    [Fact]
    public async Task Render_applies_rotation_with_swapped_size()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("a.png"), TestImages.Png(400, 200));
        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        using var folder = new FolderImageSource(temp.Path);
        var info = (await folder.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(settings);
        var decoded = await loader.LoadAsync(folder, info, CancellationToken.None);

        var renderer = new ImageRenderService(new ImageFilterService(settings));
        var rotated = ImageTransform.Identity.RotateClockwise();
        var bitmap = await renderer.RenderAsync(decoded, 50, 100, ImageFilter.None, rotated, CancellationToken.None);

        Assert.Equal(50, bitmap.PixelSize.Width);
        Assert.Equal(100, bitmap.PixelSize.Height);
        decoded.Release();
    }

    private static MagickImage CreatePattern()
    {
        var image = new MagickImage(MagickColors.Black, 3, 2);
        var colors = new[] { "red", "lime", "blue", "yellow", "cyan", "magenta" };
        using var pixels = image.GetPixels();
        for (var i = 0; i < colors.Length; i++)
        {
            var c = new MagickColor(colors[i]);
            pixels.SetPixel(i % 3, i / 3, [c.R, c.G, c.B]);
        }

        return image;
    }

    private static string Pixels(MagickImage image)
    {
        using var pixels = image.GetPixels();
        return Convert.ToHexString(pixels.ToByteArray(PixelMapping.RGB)!);
    }
}

public class MultiFrameTests
{
    private static SettingsService Settings(TempFolder temp)
    {
        var s = new SettingsService(settingsPath: temp.File("settings.json"));
        s.Load();
        return s;
    }

    private static byte[] CreateAnimatedGif(int frames, uint delayTicks)
    {
        using var collection = new MagickImageCollection();
        var colors = new[] { "red", "lime", "blue", "yellow" };
        for (var i = 0; i < frames; i++)
        {
            var frame = new MagickImage(new MagickColor(colors[i % colors.Length]), 30, 20)
            {
                AnimationDelay = delayTicks,
            };
            collection.Add(frame);
        }

        return collection.ToByteArray(MagickFormat.Gif);
    }

    [Fact]
    public async Task Animated_gif_keeps_all_frames_and_delays()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("anim.gif"), CreateAnimatedGif(3, 50));
        var settings = Settings(temp);
        using var folder = new FolderImageSource(temp.Path);
        var info = (await folder.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(settings);

        var decoded = await loader.LoadAsync(folder, info, CancellationToken.None);
        Assert.True(decoded.IsAnimated);
        Assert.Equal(3, decoded.FrameCount);

        Assert.Equal([500, 500, 500], decoded.FrameDelaysMs);
        Assert.Equal(
            "프레임 1 / 3 \n0.00 / 1.50초  ·  지연 500ms\n30 × 20  ·  GIF",
            ImageViewer.ViewModels.MainViewModel.BuildStaticFrameInfo(decoded));
        Assert.Equal(
            "프레임 2 / 3 \n0.50 / 1.50초  ·  지연 500ms\n30 × 20  ·  GIF",
            ImageViewer.ViewModels.MainViewModel.FormatAnimationInfo(1, decoded.FrameDelaysMs, 30, 20, "GIF", playing: true));

        using var playback = new AnimationPlayback(decoded.Animation!, ImageTransform.Identity);
        Assert.True(playback.FitsInBudget);
        for (var i = 0; i < 3; i++)
        {
            var frame = await playback.WaitForFrameAsync(i, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(30, frame.PixelSize.Width);
        }

        decoded.Release();
    }

    [Fact]
    public async Task Zero_delay_frames_play_at_100ms()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("fast.gif"), CreateAnimatedGif(2, 0));
        var settings = Settings(temp);
        using var folder = new FolderImageSource(temp.Path);
        var info = (await folder.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(settings);
        var decoded = await loader.LoadAsync(folder, info, CancellationToken.None);

        Assert.Equal([100, 100], decoded.FrameDelaysMs);
        decoded.Release();
    }

    [Fact]
    public async Task Multi_page_tiff_pages_are_loaded_individually()
    {
        using var temp = new TempFolder();
        using (var collection = new MagickImageCollection())
        {
            collection.Add(new MagickImage(MagickColors.Red, 40, 20));
            collection.Add(new MagickImage(MagickColors.Lime, 50, 30));
            collection.Add(new MagickImage(MagickColors.Blue, 60, 40));
            collection.Write(temp.File("doc.tif"), MagickFormat.Tiff);
        }

        using var folder = new FolderImageSource(temp.Path);
        var info = (await folder.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(Settings(temp));

        var first = await loader.LoadAsync(folder, info, 0, CancellationToken.None);
        Assert.Equal(3, first.PageCount);
        Assert.Equal(0, first.PageIndex);
        Assert.Equal(40u, first.Width);
        Assert.False(first.IsAnimated);

        Assert.Equal("페이지 1 / 3\n40 × 20  ·  TIFF", ImageViewer.ViewModels.MainViewModel.BuildStaticFrameInfo(first));

        var third = await loader.LoadAsync(folder, info, 2, CancellationToken.None);
        Assert.Equal(2, third.PageIndex);
        Assert.Equal(60u, third.Width);
        Assert.NotEqual(first.Key, third.Key);

        // 범위를 벗어난 페이지는 마지막 페이지로 맞춘다.
        var clamped = await loader.LoadAsync(folder, info, 99, CancellationToken.None);
        Assert.Equal(2, clamped.PageIndex);

        first.Release();
        third.Release();
        clamped.Release();
    }
}

public class ExifTests
{
    [Fact]
    public async Task Reads_camera_exif_fields()
    {
        using var temp = new TempFolder();
        using (var image = new MagickImage(MagickColors.Gray, 32, 24))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestMaker");
            exif.SetValue(ExifTag.Model, "TestMaker Z9");
            exif.SetValue(ExifTag.DateTimeOriginal, "2026:11:15 10:20:30");
            exif.SetValue(ExifTag.FNumber, new Rational(28, 10));
            exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250));
            exif.SetValue(ExifTag.ISOSpeedRatings, new ushort[] { 400 });
            exif.SetValue(ExifTag.FocalLength, new Rational(50, 1));
            image.SetProfile(exif);
            image.Write(temp.File("photo.jpg"), MagickFormat.Jpeg);
        }

        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        using var folder = new FolderImageSource(temp.Path);
        var info = (await folder.GetImagesAsync(CancellationToken.None)).Single();
        using var loader = new ImageLoaderService(settings);
        var decoded = await loader.LoadAsync(folder, info, CancellationToken.None);

        var values = decoded.Exif.ToDictionary(i => i.Label, i => i.Value);
        Assert.Equal("TestMaker Z9", values["카메라"]);
        Assert.Equal("2026-11-15 10:20:30", values["촬영 일시"]);
        Assert.Equal("f/2.8", values["조리개"]);
        Assert.Equal("1/250 초", values["노출 시간"]);
        Assert.Equal("400", values["ISO"]);
        Assert.Equal("50 mm", values["초점 거리"]);

        var panel = ImageViewer.ViewModels.MainViewModel.BuildInfoItems(info, decoded, ImageTransform.Identity);
        Assert.Contains(panel, i => i.IsHeader && i.Label == "EXIF");
        Assert.Contains(panel, i => i.Label == "해상도" && i.Value == "32 × 24");
        Assert.DoesNotContain(panel, i => i.IsHeader && i.Label == "메타 정보");
        decoded.Release();
    }

    [Fact]
    public async Task Reads_png_text_chunks_and_exif_descriptions()
    {
        using var temp = new TempFolder();
        using (var image = new MagickImage(MagickColors.Gray, 32, 24))
        {
            image.SetAttribute("parameters", "a cat\nNegative prompt: dog\nSteps: 20, Sampler: Euler a");
            image.SetAttribute("Title", "고양이");
            image.Comment = "hello comment";
            image.Write(temp.File("ai.png"), MagickFormat.Png);
        }

        using (var image = new MagickImage(MagickColors.Gray, 32, 24))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.ImageDescription, "Sunset at the beach");
            exif.SetValue(ExifTag.XPTitle, System.Text.Encoding.Unicode.GetBytes("바다 노을\0"));
            exif.SetValue(ExifTag.Software, "TestEditor 1.0");
            image.SetProfile(exif);
            image.Comment = "jpeg comment";
            image.Write(temp.File("photo.jpg"), MagickFormat.Jpeg);
        }

        var settings = new SettingsService(settingsPath: temp.File("s.json"));
        settings.Load();
        using var folder = new FolderImageSource(temp.Path);
        var infos = await folder.GetImagesAsync(CancellationToken.None);
        using var loader = new ImageLoaderService(settings);

        var png = await loader.LoadAsync(folder, infos.Single(i => i.Name == "ai.png"), CancellationToken.None);
        var pngValues = png.Metadata.ToDictionary(i => i.Label, i => i.Value);
        Assert.Equal("a cat\nNegative prompt: dog\nSteps: 20, Sampler: Euler a", pngValues["생성 정보"]);
        Assert.Equal("고양이", pngValues["제목"]);
        Assert.Equal("hello comment", pngValues["주석"]);
        Assert.DoesNotContain(png.Metadata, i => i.Label.Contains(':'));

        var panel = ImageViewer.ViewModels.MainViewModel.BuildInfoItems(png.Info, png, ImageTransform.Identity);
        Assert.Contains(panel, i => i.IsHeader && i.Label == "메타 정보");
        png.Release();

        var jpeg = await loader.LoadAsync(folder, infos.Single(i => i.Name == "photo.jpg"), CancellationToken.None);
        var jpegValues = jpeg.Metadata.ToDictionary(i => i.Label, i => i.Value);
        Assert.Equal("Sunset at the beach", jpegValues["설명"]);
        Assert.Equal("바다 노을", jpegValues["제목"]);
        Assert.Equal("jpeg comment", jpegValues["주석"]);

        // EXIF 구역에 이미 나온 값(소프트웨어)은 메타 정보에 다시 보이지 않는다.
        Assert.Equal("TestEditor 1.0", jpeg.Exif.Single(i => i.Label == "소프트웨어").Value);
        Assert.DoesNotContain(jpeg.Metadata, i => i.Value == "TestEditor 1.0");
        jpeg.Release();
    }

    [Fact]
    public void Reads_xmp_fields()
    {
        var document = System.Xml.Linq.XDocument.Parse("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:xmp="http://ns.adobe.com/xap/1.0/"
                                 xmp:Rating="4" xmp:CreatorTool="Lightroom">
                  <dc:title><rdf:Alt><rdf:li xml:lang="ko">제목</rdf:li><rdf:li xml:lang="x-default">Title</rdf:li></rdf:Alt></dc:title>
                  <dc:creator><rdf:Seq><rdf:li>Kim</rdf:li></rdf:Seq></dc:creator>
                  <dc:subject><rdf:Bag><rdf:li>sea</rdf:li><rdf:li>sunset</rdf:li></rdf:Bag></dc:subject>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """);

        var values = new Dictionary<string, string?>();
        MetadataReader.ReadXmp(document, (label, value) =>
        {
            if (value is not null)
            {
                values[label] = value;
            }
        });

        Assert.Equal("Title", values["제목"]);
        Assert.Equal("Kim", values["작성자"]);
        Assert.Equal("sea, sunset", values["키워드"]);
        Assert.Equal("Lightroom", values["편집 프로그램"]);
        Assert.Equal("★★★★☆", values["별점"]);
    }
}

public class FolderWatchTests
{
    [Fact]
    public async Task Raises_changed_for_image_files_only()
    {
        using var temp = new TempFolder();
        using var folder = new FolderImageSource(temp.Path);
        var raised = new TaskCompletionSource();
        var count = 0;
        folder.Changed += (_, _) =>
        {
            Interlocked.Increment(ref count);
            raised.TrySetResult();
        };
        folder.StartWatching();

        File.WriteAllText(temp.File("notes.txt"), "ignored");
        await Task.Delay(400);
        Assert.Equal(0, Volatile.Read(ref count));

        File.WriteAllBytes(temp.File("new.png"), TestImages.Png());
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var images = await folder.GetImagesAsync(CancellationToken.None);
        Assert.Single(images);

        folder.StopWatching();
        Interlocked.Exchange(ref count, 0);
        File.WriteAllBytes(temp.File("after-stop.png"), TestImages.Png());
        await Task.Delay(400);
        Assert.Equal(0, Volatile.Read(ref count));
    }
}
