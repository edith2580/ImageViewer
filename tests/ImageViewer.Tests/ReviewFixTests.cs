using System.Text;
using ImageViewer.Helpers;
using ImageViewer.Models;
using ImageViewer.Services;
using Microsoft.Extensions.Logging;

namespace ImageViewer.Tests;

/// <summary>#7 로그 경로 제거 · 여러 프로세스 기록, #10 예외 기록.</summary>
public class LogPrivacyTests
{
    [Theory]
    [InlineData(@"Could not find file 'D:\사진\가족 여행\a b.jpg'.", @"Could not find file '…\a b.jpg'.")]
    [InlineData(@"Access to the path 'C:\Users\kim\AppData\Local\Temp\ImageViewer-update\x.exe' is denied.", @"Access to the path '…\x.exe' is denied.")]
    [InlineData(@"   at ImageViewer.Foo.Bar() in D:\ImageViewer\src\Foo.cs:line 12", @"   at ImageViewer.Foo.Bar() in …\Foo.cs:line 12")]
    [InlineData(@"share \\nas\photos\2026\a.png failed", @"share …\a.png failed")]
    [InlineData("Could not find file '/home/kim/Pictures/a.png'.", "Could not find file '…/a.png'.")]
    [InlineData("/Users/kim/Desktop/b.webp", "…/b.webp")]
    [InlineData("이미지가 너무 큽니다. (15,000 × 15,000, 허용 한도 150 MP)", "이미지가 너무 큽니다. (15,000 × 15,000, 허용 한도 150 MP)")]
    [InlineData("ImageViewer.Services.ImageLoaderService: a.jpg 18:30 ratio 1/2", "ImageViewer.Services.ImageLoaderService: a.jpg 18:30 ratio 1/2")]
    [InlineData(@"root 'D:\' is full", @"root 'D:\' is full")]
    public void Paths_are_reduced_to_file_names(string input, string expected) =>
        Assert.Equal(expected, LogSanitizer.RedactPaths(input));

    [Fact]
    public void Exceptions_are_logged_with_stack_and_inner_exception_but_without_paths()
    {
        Exception error;
        try
        {
            try
            {
                throw new FileNotFoundException(@"Could not find file 'D:\비밀\폴더\a.jpg'.");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("이미지를 열지 못했습니다.", inner);
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var text = FileLoggerProvider.Format(new DateTime(2026, 11, 25, 9, 0, 0), "ImageViewer.App", LogLevel.Error, @"열기 실패: C:\Users\kim\b.png", error);

        Assert.Contains(@"열기 실패: …\b.png", text);
        Assert.Contains("InvalidOperationException: 이미지를 열지 못했습니다.", text);
        Assert.Contains("FileNotFoundException", text);   // 내부 예외
        Assert.Contains(@"…\a.jpg", text);
        Assert.Contains(" at ", text);                    // 스택 추적
        Assert.DoesNotContain("비밀", text);
        Assert.DoesNotContain("kim", text);
    }

    [Fact]
    public async Task Log_lines_wait_for_another_process_that_is_writing_and_are_not_lost()
    {
        var dir = Directory.CreateTempSubdirectory("iv-log-").FullName;
        try
        {
            using var provider = new FileLoggerProvider(dir);
            var logger = provider.CreateLogger("ImageViewer.Test");
            logger.LogInformation("first");
            var file = Directory.GetFiles(dir, "imageviewer-*.log").Single();

            // 다른 창(프로세스)이 같은 파일에 쓰는 중이라 잠겨 있는 상황: 잠시 뒤 풀린다.
            var other = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            var release = Task.Run(async () =>
            {
                await Task.Delay(40);
                other.Write("other"u8);
                other.Write(System.Text.Encoding.UTF8.GetBytes(Environment.NewLine));
                other.Dispose();
            });

            logger.LogInformation("second"); // 잠금이 풀릴 때까지 다시 시도한다
            await release;
            logger.LogInformation("third");

            var text = File.ReadAllText(file);
            Assert.Contains("first", text);
            Assert.Contains("second", text);
            Assert.Contains("other", text);
            Assert.Contains("third", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Only_process_corrupting_exceptions_are_fatal()
    {
        Assert.True(App.IsFatal(new InvalidProgramException()));
        Assert.True(App.IsFatal(new AccessViolationException()));
        Assert.False(App.IsFatal(new OutOfMemoryException())); // 큰 할당 하나의 실패: 캐시를 비우고 계속
        Assert.False(App.IsFatal(new IOException()));
        Assert.False(App.IsFatal(new InvalidOperationException()));
    }
}

/// <summary>#8 여러 창이 설정을 저장할 때 서로 덮어쓰지 않는다.</summary>
public sealed class SettingsMergeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("iv-settings-").FullName;

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SettingsService Open()
    {
        var service = new SettingsService(settingsPath: SettingsPath);
        service.Load();
        return service;
    }

    [Fact]
    public void Two_windows_keep_each_others_changes()
    {
        var a = Open();
        var b = Open();

        a.Current.Filter.SmoothSigma = 1.5;
        a.Current.Slideshow.IntervalSeconds = 30;
        a.Save();

        b.Current.Window.Width = 1600;          // b 는 창 크기만 바꿨다
        b.Current.Navigation.WatchFolder = false;
        b.Save();

        var merged = Open().Current;
        Assert.Equal(1.5, merged.Filter.SmoothSigma);       // a 의 변경이 남아 있다
        Assert.Equal(30, merged.Slideshow.IntervalSeconds);
        Assert.Equal(1600, merged.Window.Width);             // b 의 변경
        Assert.False(merged.Navigation.WatchFolder);

        // b 가 다시 저장해도(바꾼 게 없으면) a 의 변경을 되돌리지 않는다.
        b.Save();
        Assert.Equal(1.5, Open().Current.Filter.SmoothSigma);

        // 같은 값을 둘 다 바꾸면 나중에 저장한 쪽이 이긴다.
        a.Current.Window.Width = 1200;
        a.Save();
        Assert.Equal(1200, Open().Current.Window.Width);
    }

    [Fact]
    public void Value_changed_back_to_default_is_saved()
    {
        var a = Open();
        a.Current.Navigation.WatchFolder = false;
        a.Save();

        var b = Open();
        b.Current.Navigation.WatchFolder = true; // 기본값으로 되돌림
        b.Save();

        Assert.True(Open().Current.Navigation.WatchFolder);
    }

    [Fact]
    public void Corrupt_file_is_replaced_by_this_windows_settings()
    {
        var a = Open();
        File.WriteAllText(SettingsPath, "{ not json");
        a.Current.Filter.SmoothSigma = 2.0;
        a.Save();

        Assert.Equal(2.0, Open().Current.Filter.SmoothSigma);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
