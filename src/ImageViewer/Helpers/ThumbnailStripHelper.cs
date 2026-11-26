using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ImageViewer.Helpers;

/// <summary>
/// 가로 썸네일 줄(메인 화면 하단 · 이미지 목록 상단)에서 현재 항목을 가능한 한 가운데에 오도록 스크롤한다.
/// 처음 · 끝 근처라 가운데로 올 수 없으면 줄의 시작 · 끝까지만 스크롤한다.
/// </summary>
public static class ThumbnailStripHelper
{
    /// <summary>
    /// <paramref name="index"/> 번째 항목을 가운데로 스크롤한다.
    /// 가상화된 줄은 항목이 화면에 만들어지기 전에는 위치를 알 수 없으므로, 먼저 보이게 한 뒤
    /// 레이아웃이 끝나면 실제 위치로 다시 맞춘다 (코믹 2페이지처럼 폭이 다른 항목도 정확히 가운데에 온다).
    /// </summary>
    public static void CenterOnIndex(ListBox strip, int index)
    {
        // 숨겨진 줄(예: 마우스를 올려야 나타나는 하단 바)은 폭이 없어 가운데를 계산할 수 없다.
        // 줄이 나타날 때 다시 부르도록 호출하는 쪽에서 처리한다.
        if (index < 0 || index >= strip.ItemCount || !strip.IsEffectivelyVisible)
        {
            return;
        }

        strip.ScrollIntoView(index);
        Center(strip, index);
        Dispatcher.UIThread.Post(() => Center(strip, index), DispatcherPriority.Loaded);
    }

    private static void Center(ListBox strip, int index)
    {
        if (index >= strip.ItemCount || strip.FindDescendantOfType<ScrollViewer>() is not { } scroll)
        {
            return;
        }

        var viewport = scroll.Viewport.Width;
        var extent = scroll.Extent.Width;
        if (viewport <= 0 || extent <= viewport)
        {
            return;
        }

        // 항목 가운데의 위치 (스크롤 내용 기준). 항목이 만들어져 있으면 실제 위치, 아니면 평균 폭으로 추정한다.
        double itemCenter;
        if (strip.ContainerFromIndex(index) is Control container &&
            container.TranslatePoint(new Point(container.Bounds.Width / 2, 0), scroll) is { } point)
        {
            itemCenter = scroll.Offset.X + point.X;
        }
        else
        {
            itemCenter = (index + 0.5) * extent / strip.ItemCount;
        }

        var offset = CenteredOffset(itemCenter, viewport, extent);
        if (Math.Abs(offset - scroll.Offset.X) > 0.5)
        {
            scroll.Offset = new Vector(offset, scroll.Offset.Y);
        }
    }

    /// <summary>항목 가운데(<paramref name="itemCenter"/>)가 화면 가운데에 오는 스크롤 위치. 처음 · 끝을 넘지 않는다.</summary>
    internal static double CenteredOffset(double itemCenter, double viewport, double extent)
    {
        var max = Math.Max(0, extent - viewport);
        return Math.Clamp(itemCenter - viewport / 2, 0, max);
    }
}
