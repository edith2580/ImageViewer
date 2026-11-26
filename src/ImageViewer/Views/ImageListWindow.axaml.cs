using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageViewer.Helpers;
using ImageViewer.ViewModels;

namespace ImageViewer.Views;

public partial class ImageListWindow : Window
{
    private readonly ImageListViewModel _vm;

    public ImageListWindow(ImageListViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        Opened += OnOpened;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ThumbnailStrip.AddHandler(PointerWheelChangedEvent, OnThumbnailPointerWheelChanged, RoutingStrategies.Tunnel);
        ThumbnailStrip.AddHandler(PointerPressedEvent, OnThumbnailPointerPressed, RoutingStrategies.Tunnel);
        ComicThumbnailStrip.AddHandler(PointerWheelChangedEvent, OnComicThumbnailPointerWheelChanged, RoutingStrategies.Tunnel);
        ComicThumbnailStrip.AddHandler(PointerPressedEvent, OnComicThumbnailPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ScrollSelectionIntoView(focusList: true);
    }

    /// <summary>
    /// Esc 로 닫고, Enter 로 선택을 확정한다 (Linux/Wayland 에서 부모 창으로 키 이벤트가 새는 것을 방지).
    /// 위 · 아래 · Page Up · Page Down · Home · End 는 목록의 선택 자체를 옮긴다.
    /// (ListBox 에 맡기면 Page Up / Down 이나 포커스를 잃은 뒤의 방향키가 스크롤만 해서, Enter 가 이전 선택을 연다)
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            Accept();
        }
        else if (e.KeyModifiers == KeyModifiers.None && ToNavigation(e.Key) is { } navigation)
        {
            e.Handled = true;
            MoveSelection(navigation);
        }
    }

    internal static ListNavigation? ToNavigation(Key key) => key switch
    {
        Key.Up => ListNavigation.Up,
        Key.Down => ListNavigation.Down,
        Key.PageUp => ListNavigation.PageUp,
        Key.PageDown => ListNavigation.PageDown,
        Key.Home => ListNavigation.Home,
        Key.End => ListNavigation.End,
        _ => null,
    };

    private void MoveSelection(ListNavigation navigation)
    {
        if (_vm.MoveSelection(navigation, VisibleRowCount()) is not { } item)
        {
            return;
        }

        // 새 선택을 보이게 하고 포커스도 옮겨 둔다 (포커스 표시와 선택이 어긋나지 않게).
        List.ScrollIntoView(item);
        Dispatcher.UIThread.Post(() =>
        {
            if (List.ContainerFromItem(item) is Control container)
            {
                container.Focus(NavigationMethod.Directional);
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Page Up / Page Down 한 번에 옮길 줄 수: 목록에 한 화면에 보이는 줄 수에서 한 줄 뺀 값 (최소 1).</summary>
    private int VisibleRowCount()
    {
        var viewport = List.FindDescendantOfType<ScrollViewer>()?.Viewport.Height ?? 0;
        var rowHeight = List.GetRealizedContainers().FirstOrDefault()?.Bounds.Height ?? 0;
        if (viewport <= 0 || rowHeight <= 0)
        {
            return 10;
        }

        return Math.Max(1, (int)(viewport / rowHeight) - 1);
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 목록에서 선택하면 썸네일 줄도 해당 위치로 스크롤한다.
        ScrollActiveThumbnailStrip();

        if (_vm.SelectedItem is { } item && !List.IsKeyboardFocusWithin)
        {
            List.ScrollIntoView(item);
        }
    }

    /// <summary>선택한 이미지의 썸네일을 상단 썸네일 줄의 가운데로 스크롤한다 (처음 · 끝 근처는 가능한 만큼).</summary>
    private void ScrollActiveThumbnailStrip()
    {
        if (_vm.IsComicMode)
        {
            ThumbnailStripHelper.CenterOnIndex(ComicThumbnailStrip, _vm.SelectedComicThumbnailIndex);
        }
        else
        {
            ThumbnailStripHelper.CenterOnIndex(ThumbnailStrip, _vm.SelectedThumbnailIndex);
        }
    }

    private void ScrollSelectionIntoView(bool focusList)
    {
        if (_vm.SelectedItem is not { } item)
        {
            if (focusList)
            {
                List.Focus();
            }

            return;
        }

        List.ScrollIntoView(item);
        ScrollActiveThumbnailStrip();

        if (!focusList)
        {
            return;
        }

        // 컨테이너가 만들어진 뒤 포커스를 줘야 방향키로 바로 이동할 수 있다.
        Dispatcher.UIThread.Post(() =>
        {
            if (List.ContainerFromItem(item) is Control container)
            {
                container.Focus();
            }
            else
            {
                List.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnThumbnailPointerWheelChanged(object? sender, PointerWheelEventArgs e) =>
        ScrollStripByWheel(ThumbnailStrip, e);

    private void OnComicThumbnailPointerWheelChanged(object? sender, PointerWheelEventArgs e) =>
        ScrollStripByWheel(ComicThumbnailStrip, e);

    private static void ScrollStripByWheel(ListBox strip, PointerWheelEventArgs e)
    {
        if (strip.FindDescendantOfType<ScrollViewer>() is { } scroll)
        {
            scroll.Offset = new Avalonia.Vector(
                Math.Max(0, scroll.Offset.X - e.Delta.Y * 48),
                scroll.Offset.Y);
            e.Handled = true;
        }
    }

    private void OnThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ThumbnailStrip).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Control source ||
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: ThumbnailItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        _vm.SelectedThumbnailIndex = item.Index;
        if (e.ClickCount == 2)
        {
            Accept();
        }
    }

    private void OnComicThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ComicThumbnailStrip).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Control source ||
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: ComicThumbnailItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        _vm.SelectedComicThumbnailIndex = item.SpreadIndex;
        if (e.ClickCount == 2)
        {
            Accept();
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
        {
            Accept();
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Accept();

    private void Accept() => Close(_vm.Accept());
}
