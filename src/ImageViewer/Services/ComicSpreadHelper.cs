using ImageMagick;
using ImageViewer.Models;
using ImageViewer.Services.ImageSources;

namespace ImageViewer.Services;

/// <summary>
/// 코믹 뷰어에서의 한 화면(펼침) 단위.
/// - 세로 이미지 2장이 짝을 이루면 <see cref="SecondIndex"/> 가 채워진다.
/// - 정사각형 · 가로 이미지(<see cref="IsWideSingle"/> = true)는 양면 단독 1장으로 표시한다.
/// - 사용자가 수동으로 빈 페이지를 넣으면 <see cref="TrailingBlank"/> = true 가 되어
///   현재 페이지 뒤에 빈 페이지가 배치되고 다음 페이지부터 짝이 한 칸씩 밀린다.
/// </summary>
public sealed record ComicSpread(
    int SpreadIndex,
    int FirstIndex,
    int? SecondIndex,
    bool IsWideSingle,
    bool TrailingBlank = false);

/// <summary>
/// 코믹 뷰어(2페이지 보기)의 펼침(Spread) 구성 · 수동 빈 페이지 · 인덱스 · 좌·우 배치 계산.
/// </summary>
public static class ComicSpreadHelper
{
    /// <summary>
    /// 1페이지(세로 이미지: 높이 &gt; 너비)가 아니라 2페이지 크기(정사각형 · 가로 이미지: 너비 &gt;= 높이)인지 판별한다.
    /// EXIF 회전(90°/270°, Orientation 5~8)이 있으면 너비·높이를 교환해 판별한다.
    /// </summary>
    public static bool IsWideImage(uint width, uint height, bool swapped = false)
    {
        var (w, h) = swapped ? (height, width) : (width, height);
        return w > 0 && h > 0 && w >= h;
    }

    /// <summary>이미지 헤더만 빠르게 읽어 정사각형·가로 이미지(2페이지 크기)인지 확인한다.</summary>
    public static async Task<bool> ProbeIsWideAsync(IImageSource source, ImageInfo info, CancellationToken ct)
    {
        if (!info.IsSupported)
        {
            return false;
        }

        try
        {
            if (info.SourceType == ImageSourceType.File && info.FullPath is { } fullPath && File.Exists(fullPath))
            {
                var header = new MagickImageInfo(fullPath);
                return IsWideImage(header.Width, header.Height, (int)header.Orientation is >= 5 and <= 8);
            }

            using var stream = await source.OpenImageAsync(info, ct).ConfigureAwait(false);
            var streamHeader = new MagickImageInfo(stream);
            return IsWideImage(streamHeader.Width, streamHeader.Height, (int)streamHeader.Orientation is >= 5 and <= 8);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 전체 이미지 목록과 가로/정사각형 여부, 사용자가 수동 삽입한 빈 페이지 위치로 펼침 목록을 구성한다.
    /// - 가로/정사각형 이미지는 단독 1장(<see cref="ComicSpread.IsWideSingle"/>)으로 표시한다.
    /// - <paramref name="manualBlankAfter"/> 에 포함된 인덱스는 뒤에 빈 페이지를 넣어 단독 펼침(<see cref="ComicSpread.TrailingBlank"/>)으로
    ///   구성하고, 그 다음 페이지부터 새로 2페이지씩 짝을 맞춘다.
    /// </summary>
    public static IReadOnlyList<ComicSpread> BuildSpreads(
        int count,
        IReadOnlyList<bool>? isWide = null,
        IReadOnlySet<int>? manualBlankAfter = null)
    {
        if (count <= 0)
        {
            return [];
        }

        var spreads = new List<ComicSpread>((count + 1) / 2);
        var i = 0;
        while (i < count)
        {
            var firstWide = isWide is not null && i < isWide.Count && isWide[i];
            if (firstWide)
            {
                spreads.Add(new ComicSpread(spreads.Count, i, null, IsWideSingle: true));
                i += 1;
                continue;
            }

            if (manualBlankAfter is not null && manualBlankAfter.Contains(i))
            {
                spreads.Add(new ComicSpread(spreads.Count, i, null, IsWideSingle: false, TrailingBlank: true));
                i += 1;
                continue;
            }

            var nextIsWide = i + 1 < count && isWide is not null && i + 1 < isWide.Count && isWide[i + 1];
            if (i + 1 < count && !nextIsWide)
            {
                spreads.Add(new ComicSpread(spreads.Count, i, i + 1, IsWideSingle: false));
                i += 2;
            }
            else
            {
                spreads.Add(new ComicSpread(spreads.Count, i, null, IsWideSingle: false));
                i += 1;
            }
        }

        return spreads;
    }

    /// <summary>주어진 이미지 인덱스가 속한 펼침의 인덱스(0..spreads.Count-1). 없으면 -1.</summary>
    public static int FindSpreadIndex(IReadOnlyList<ComicSpread> spreads, int imageIndex)
    {
        if (spreads.Count == 0 || imageIndex < 0)
        {
            return -1;
        }

        for (var i = 0; i < spreads.Count; i++)
        {
            var s = spreads[i];
            if (s.FirstIndex == imageIndex || s.SecondIndex == imageIndex)
            {
                return i;
            }
        }

        return spreads.Count - 1;
    }

    public static ComicSpread? FindSpread(IReadOnlyList<ComicSpread> spreads, int imageIndex)
    {
        var idx = FindSpreadIndex(spreads, imageIndex);
        return idx >= 0 ? spreads[idx] : null;
    }

    public static int GetSpreadStart(IReadOnlyList<ComicSpread> spreads, int imageIndex) =>
        FindSpread(spreads, imageIndex)?.FirstIndex ?? -1;

    public static int GetNextSpreadStart(IReadOnlyList<ComicSpread> spreads, int currentIndex, bool wrapAround)
    {
        var idx = FindSpreadIndex(spreads, currentIndex);
        if (idx < 0)
        {
            return -1;
        }

        if (idx + 1 < spreads.Count)
        {
            return spreads[idx + 1].FirstIndex;
        }

        return wrapAround && spreads.Count > 1 ? spreads[0].FirstIndex : -1;
    }

    public static int GetPreviousSpreadStart(IReadOnlyList<ComicSpread> spreads, int currentIndex, bool wrapAround)
    {
        var idx = FindSpreadIndex(spreads, currentIndex);
        if (idx < 0)
        {
            return -1;
        }

        if (idx - 1 >= 0)
        {
            return spreads[idx - 1].FirstIndex;
        }

        return wrapAround && spreads.Count > 1 ? spreads[^1].FirstIndex : -1;
    }

    public static int GetLastSpreadStart(IReadOnlyList<ComicSpread> spreads) =>
        spreads.Count > 0 ? spreads[^1].FirstIndex : -1;

    /// <summary>임의의 이미지 인덱스가 속한 2페이지 펼침의 시작 인덱스(짝수, 크기 정보 없을 때 기본값).</summary>
    public static int GetSpreadStart(int index, int count)
    {
        if (count <= 0)
        {
            return -1;
        }

        var clamped = Math.Clamp(index, 0, count - 1);
        return (clamped / 2) * 2;
    }

    /// <summary>펼침의 번호 (0, 1, 2, ...).</summary>
    public static int GetSpreadIndex(int imageIndex) => imageIndex < 0 ? -1 : imageIndex / 2;

    /// <summary>전체 펼침 개수.</summary>
    public static int GetSpreadCount(int count) => count <= 0 ? 0 : (count + 1) / 2;

    /// <summary>마지막 펼침의 시작 인덱스.</summary>
    public static int GetLastSpreadStart(int count) => count <= 0 ? -1 : ((count - 1) / 2) * 2;

    /// <summary>다음 펼침의 시작 인덱스. 이동할 수 없으면 -1.</summary>
    public static int GetNextSpreadStart(int currentIndex, int count, bool wrapAround) =>
        GetNextSpreadStart(BuildSpreads(count), currentIndex, wrapAround);

    /// <summary>이전 펼침의 시작 인덱스. 이동할 수 없으면 -1.</summary>
    public static int GetPreviousSpreadStart(int currentIndex, int count, bool wrapAround) =>
        GetPreviousSpreadStart(BuildSpreads(count), currentIndex, wrapAround);

    /// <summary>
    /// 현재 펼침의 (첫 번째 페이지, 두 번째 페이지) 인덱스.
    /// 총 개수가 홀수이고 마지막 페이지이면 두 번째는 null.
    /// </summary>
    public static (int First, int? Second) GetSpreadPair(int index, int count)
    {
        var first = GetSpreadStart(index, count);
        if (first < 0)
        {
            return (-1, null);
        }

        var second = first + 1 < count ? first + 1 : (int?)null;
        return (first, second);
    }

    /// <summary>읽기 방향에 따라 화면 (왼쪽, 오른쪽)에 배치할 항목을 반환한다.</summary>
    public static (T? Left, T? Right) OrderPair<T>(T? first, T? second, ComicReadingDirection direction) where T : class =>
        direction == ComicReadingDirection.RightToLeft ? (second, first) : (first, second);

    /// <summary>읽기 방향에 따라 화면 (왼쪽, 오른쪽)에 배치할 인덱스를 반환한다.</summary>
    public static (int? Left, int? Right) OrderIndices(int first, int? second, ComicReadingDirection direction) =>
        direction == ComicReadingDirection.RightToLeft ? (second, first) : (first, second);
}
