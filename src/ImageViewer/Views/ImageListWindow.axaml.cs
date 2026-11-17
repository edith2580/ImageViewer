using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ScrollSelectionIntoView(focusList: true);
    }

    /// <summary>취소 버튼이 없으므로 Esc 로 닫는다.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 목록에서 선택하면 썸네일 줄도 해당 위치로 스크롤한다.
        var index = _vm.SelectedThumbnailIndex;
        if (index >= 0 && index < ThumbnailStrip.Items.Count)
        {
            ThumbnailStrip.ScrollIntoView(index);
        }

        if (_vm.SelectedItem is { } item && !List.IsKeyboardFocusWithin)
        {
            List.ScrollIntoView(item);
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
        var index = _vm.SelectedThumbnailIndex;
        if (index >= 0 && index < ThumbnailStrip.Items.Count)
        {
            ThumbnailStrip.ScrollIntoView(index);
        }

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

    private void OnThumbnailPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ThumbnailStrip.FindDescendantOfType<ScrollViewer>() is { } scroll)
        {
            scroll.Offset = new Avalonia.Vector(
                Math.Max(0, scroll.Offset.X - e.Delta.Y * 48),
                scroll.Offset.Y);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 썸네일 클릭 → 목록에서 선택, 더블클릭 → 열기.
    /// (포커스를 받지 않는 항목은 ListBox 가 클릭을 선택으로 처리하지 않으므로 직접 처리한다)
    /// </summary>
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
