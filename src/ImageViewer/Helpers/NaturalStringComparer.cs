using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ImageViewer.Helpers;

/// <summary>
/// 파일명 · 폴더명 정렬용 비교자.
/// Windows 에서는 탐색기(Explorer)와 완전히 동일한 <c>StrCmpLogicalW</c> 로 정렬하고,
/// macOS / Linux 에서는 로캘 기반 자연 정렬을 사용한다.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (OperatingSystem.IsWindows())
        {
            var cmp = StrCmpLogicalW(x, y);
            return cmp != 0 ? cmp : string.CompareOrdinal(x, y);
        }

        return CompareFallback(x, y);
    }

    private static int CompareFallback(string x, string y)
    {
        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsAsciiDigit(x[ix]) && char.IsAsciiDigit(y[iy]))
            {
                var sx = ix;
                var sy = iy;
                while (ix < x.Length && char.IsAsciiDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsAsciiDigit(y[iy])) iy++;

                var nx = x.AsSpan(sx, ix - sx).TrimStart('0');
                var ny = y.AsSpan(sy, iy - sy).TrimStart('0');

                if (nx.Length != ny.Length)
                {
                    return nx.Length.CompareTo(ny.Length);
                }

                var cmp = nx.SequenceCompareTo(ny);
                if (cmp != 0)
                {
                    return cmp;
                }

                // 숫자 값이 같으면 앞자리 0이 많은 쪽을 먼저 (01 < 1, StrCmpLogicalW 와 동일)
                var lenCmp = (iy - sy).CompareTo(ix - sx);
                if (lenCmp != 0)
                {
                    return lenCmp;
                }

                continue;
            }

            var tx = ix;
            var ty = iy;
            while (ix < x.Length && !char.IsAsciiDigit(x[ix])) ix++;
            while (iy < y.Length && !char.IsAsciiDigit(y[iy])) iy++;

            var textCmp = CultureInfoCompare(x.AsSpan(tx, ix - tx), y.AsSpan(ty, iy - ty));
            if (textCmp != 0)
            {
                return textCmp;
            }
        }

        var remain = (x.Length - ix).CompareTo(y.Length - iy);
        return remain != 0 ? remain : string.CompareOrdinal(x, y);
    }

    private static int CultureInfoCompare(ReadOnlySpan<char> x, ReadOnlySpan<char> y) =>
        System.Globalization.CultureInfo.CurrentCulture.CompareInfo.Compare(
            x, y, System.Globalization.CompareOptions.IgnoreCase);

    [SupportedOSPlatform("windows")]
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
