using ImageViewer.Models;
using ImageViewer.Services.ImageSources;

namespace ImageViewer.Tests;

public class ZipLimitTests
{
    private const long MB = 1024 * 1024;

    [Theory]
    [InlineData(1000L, 512 * MB, 1000)]                                 // 작은 항목은 헤더 크기 그대로
    [InlineData(500 * MB, 512 * MB, ZipImageSource.MaxInitialCapacity)] // 헤더가 큰 값을 적어도 미리 잡지 않는다
    [InlineData(3L * 1024 * MB, 4L * 1024 * MB, ZipImageSource.MaxInitialCapacity)] // 2GB 넘는 값도 넘치지 않는다
    [InlineData(-1L, 512 * MB, 0)]                                      // 크기를 모르는 항목
    [InlineData(5000L, 4000L, 4000)]                                    // 한도보다 크게 잡지 않는다
    public void Initial_buffer_does_not_trust_the_declared_size(long declared, long limit, int expected) =>
        Assert.Equal(expected, ZipImageSource.InitialCapacity(declared, limit));

    [Fact]
    public void Zip_entry_limit_stays_below_the_array_limit()
    {
        var settings = new ViewerSettings { Limits = { MaxZipEntryMegabytes = 4096 } }.Normalize();
        Assert.Equal(LimitSettings.MaxZipEntryMegabytesLimit, settings.Limits.MaxZipEntryMegabytes);
        Assert.True((long)settings.Limits.MaxZipEntryMegabytes * MB <= Array.MaxLength);

        Assert.Equal(512, new ViewerSettings().Normalize().Limits.MaxZipEntryMegabytes);
    }
}
