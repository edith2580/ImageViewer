using System.IO.Compression;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;

namespace ImageViewer.IntegrationTests;

public class FolderImageSourceTests
{
    [Fact]
    public async Task Lists_only_images_in_natural_order()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp.File("img10.png"), TestImages.Png());
        File.WriteAllBytes(temp.File("img2.png"), TestImages.Png());
        File.WriteAllBytes(temp.File("사진 1 (복사본).PNG"), TestImages.Png());
        File.WriteAllText(temp.File("readme.txt"), "not an image");
        Directory.CreateDirectory(temp.File("sub"));
        File.WriteAllBytes(Path.Combine(temp.File("sub"), "nested.png"), TestImages.Png());

        using var source = new FolderImageSource(temp.Path);
        var images = await source.GetImagesAsync(CancellationToken.None);

        // 숫자는 값 기준으로 정렬 (문자 종류 간 순서는 현재 로캘을 따른다)
        var names = images.Select(i => i.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("사진 1 (복사본).PNG", names);
        Assert.True(names.IndexOf("img2.png") < names.IndexOf("img10.png"));
        Assert.All(images, i => Assert.Equal(ImageSourceType.File, i.SourceType));
        Assert.All(images, i => Assert.NotNull(i.ModifiedAt));
        Assert.True(images[0].Size > 0);
    }

    [Fact]
    public async Task Empty_folder_returns_empty_list()
    {
        using var temp = new TempFolder();
        using var source = new FolderImageSource(temp.Path);
        Assert.Empty(await source.GetImagesAsync(CancellationToken.None));
    }

    [Fact]
    public void Factory_opens_folder_of_image_file_and_selects_it()
    {
        using var temp = new TempFolder();
        var file = temp.File("b.png");
        File.WriteAllBytes(file, TestImages.Png());

        var factory = new ImageSourceFactory(new SettingsService(settingsPath: temp.File("s.json")));
        var result = factory.Create(file);
        using var _ = result.Source;
        Assert.IsType<FolderImageSource>(result.Source);
        Assert.Equal("b.png", result.InitialFileName);
    }

    [Fact]
    public void Factory_rejects_unsupported_file()
    {
        using var temp = new TempFolder();
        var file = temp.File("doc.txt");
        File.WriteAllText(file, "hello");
        var factory = new ImageSourceFactory(new SettingsService(settingsPath: temp.File("s.json")));
        Assert.Throws<ImageSourceException>(() => factory.Create(file));
    }
}

public class ZipImageSourceTests
{
    private static string CreateZip(TempFolder temp, string name, params (string Entry, byte[] Data)[] entries)
    {
        var path = temp.File(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in entries)
        {
            var e = zip.CreateEntry(entry);
            e.LastWriteTime = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
            using var s = e.Open();
            s.Write(data);
        }

        return path;
    }

    [Fact]
    public async Task Lists_images_inside_zip_without_extracting()
    {
        using var temp = new TempFolder();
        var red = TestImages.Png(10, 10, "red");
        var zipPath = CreateZip(temp, "images.zip",
            ("page10.png", red),
            ("page2.png", red),
            ("chapter/한글 이미지.png", red),
            ("notes.txt", "text"u8.ToArray()),
            ("__MACOSX/._page2.png", red),
            ("folder/", []));

        using var source = new ZipImageSource(zipPath);
        var images = await source.GetImagesAsync(CancellationToken.None);

        Assert.Equal(["chapter/한글 이미지.png", "page2.png", "page10.png"], images.Select(i => i.EntryPath));
        Assert.Equal("한글 이미지.png", images[0].Name);
        Assert.All(images, i => Assert.Equal(ImageSourceType.Zip, i.SourceType));
        Assert.All(images, i => Assert.Equal(red.Length, i.Size));
        Assert.All(images, i => Assert.NotNull(i.ModifiedAt));

        // 압축 해제 폴더가 생기지 않는다.
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Opens_entry_streams_and_decodes()
    {
        using var temp = new TempFolder();
        var zipPath = CreateZip(temp, "a.zip", ("a.png", TestImages.Png(12, 8)), ("b.png", TestImages.Png(20, 10)));
        using var source = new ZipImageSource(zipPath);
        var images = await source.GetImagesAsync(CancellationToken.None);

        // 동시에 여러 항목을 요청해도 안전해야 한다.
        var streams = await Task.WhenAll(images.Select(i => source.OpenImageAsync(i, CancellationToken.None)));
        try
        {
            Assert.All(streams, s => Assert.True(s.CanSeek));
            var (image, format) = ImageLoaderService.Decode(streams[1], 1_000_000, null);
            using (image)
            {
                Assert.Equal(20u, image.Width);
                Assert.Equal("PNG", format);
            }
        }
        finally
        {
            foreach (var s in streams)
            {
                s.Dispose();
            }
        }
    }

    [Fact]
    public async Task Oversized_entry_is_marked_unsupported()
    {
        using var temp = new TempFolder();
        var zipPath = CreateZip(temp, "big.zip", ("big.png", new byte[4096]));
        using var source = new ZipImageSource(zipPath, maxEntryBytes: 1024);
        var images = await source.GetImagesAsync(CancellationToken.None);
        Assert.False(images[0].IsSupported);
        await Assert.ThrowsAsync<ImageSourceException>(() => source.OpenImageAsync(images[0], CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_zip_throws_friendly_error()
    {
        using var temp = new TempFolder();
        var path = temp.File("broken.zip");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7]);
        using var source = new ZipImageSource(path);
        await Assert.ThrowsAsync<ImageSourceException>(() => source.GetImagesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Empty_zip_returns_empty_list()
    {
        using var temp = new TempFolder();
        var zipPath = CreateZip(temp, "empty.zip");
        using var source = new ZipImageSource(zipPath);
        Assert.Empty(await source.GetImagesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Zip_file_is_released_after_dispose()
    {
        using var temp = new TempFolder();
        var zipPath = CreateZip(temp, "lock.zip", ("a.png", TestImages.Png()));
        var source = new ZipImageSource(zipPath);
        await source.GetImagesAsync(CancellationToken.None);
        source.Dispose();
        Assert.False(source.IsArchiveOpen);
        File.Delete(zipPath);
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public async Task Dispose_during_a_large_read_does_not_wait_and_still_closes_the_archive()
    {
        using var temp = new TempFolder();
        var path = temp.File("huge.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            // 압축하면 작지만 풀면 400MB 인 항목: 읽는 데 시간이 걸린다.
            var entry = zip.CreateEntry("huge.png", System.IO.Compression.CompressionLevel.Fastest);
            using var s = entry.Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 400; i++)
            {
                s.Write(chunk);
            }
        }

        var source = new ZipImageSource(path, maxEntryBytes: 1024L * 1024 * 1024);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();
        var read = Task.Run(() => source.OpenImageAsync(info, CancellationToken.None));
        await Task.Delay(100);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        source.Dispose(); // 창을 닫을 때 UI 스레드에서 불린다: 읽기가 끝나기를 기다리면 안 된다
        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds < 50, $"Dispose 가 {timer.ElapsedMilliseconds}ms 걸렸습니다.");

        // 진행 중이던 읽기는 취소되고, 끝나면서 아카이브를 닫는다.
        var ex = await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.False(source.IsArchiveOpen);
    }

    [Fact]
    public async Task Disposed_zip_rejects_new_reads()
    {
        using var temp = new TempFolder();
        var zipPath = CreateZip(temp, "a.zip", ("a.png", TestImages.Png()));
        var source = new ZipImageSource(zipPath);
        var info = (await source.GetImagesAsync(CancellationToken.None)).Single();
        source.Dispose();
        source.Dispose(); // 두 번 불러도 된다

        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.OpenImageAsync(info, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetImagesAsync(CancellationToken.None));
    }
}
