using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageViewer.Helpers;
using ImageViewer.Models;
using ImageViewer.ViewModels;
using ImageViewer.Views;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // 이미지 좌상단 위치 (표시 영역 기준, DIP)
    private double _offsetX;
    private double _offsetY;

    // 마지막으로 배치에 사용한 크기 (크기 변경 시 화면 중심을 유지하기 위해 보관)
    private double _lastImageWidth;
    private double _lastImageHeight;
    private double _lastViewportWidth;
    private double _lastViewportHeight;

    private Point? _zoomAnchor;
    private Point _dragStart;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;
    private bool _isDragging;

    // 전체 화면 전환 전 상태
    private WindowState _restoreState;

    // 고정 해제된 상단 · 하단 바를 불러내는 영역 = 각 바가 차지하는 높이 (DIP).
    private const double MinRevealZone = 24;
    private double _topBarExtent = 70;     // 타이틀바 32 + 메뉴바 38
    private double _bottomBarExtent = 170; // 컨트롤 줄 + 썸네일 + 아래 여백
    private bool _menuHover;
    private bool _barHover;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        var isMac = OperatingSystem.IsMacOS();
        MacCaptionPanel.IsVisible = isMac;
        WinCaptionPanel.IsVisible = !isMac;

        // Linux(X11/Ubuntu)에서는 ExtendClientAreaToDecorationsHint만으로 OS 기본 타이틀바가 숨겨지지 않으므로
        // SystemDecorations.None으로 중복 타이틀바를 제거하고 가장자리 리사이즈를 직접 처리한다.
        if (OperatingSystem.IsLinux())
        {
            WindowDecorations = WindowDecorations.None;
            AddHandler(PointerPressedEvent, OnLinuxResizePointerPressed, RoutingStrategies.Tunnel);
        }

        _vm.LayoutChanged += OnLayoutChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        TopBar.SizeChanged += (_, _) =>
        {
            if (TopBar.Bounds.Height > 0)
            {
                _topBarExtent = TopBar.Bounds.Height;
            }
        };
        BottomBar.SizeChanged += (_, _) =>
        {
            if (BottomBar.Bounds.Height > 0)
            {
                _bottomBarExtent = BottomBar.Bounds.Height + BottomBar.Margin.Bottom;
            }

            UpdateChrome(allowHide: false);
        };

        Viewport.SizeChanged += (_, _) => UpdateViewport();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel);
        PointerExited += OnWindowPointerExited;
        AddHandler(DragDrop.DragEnterEvent, OnDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        TopBar.AddHandler(PointerPressedEvent, OnTopBarPointerPressed, RoutingStrategies.Bubble);
        Viewport.AddHandler(PointerWheelChangedEvent, OnViewportPointerWheelChanged, RoutingStrategies.Tunnel);
        Viewport.AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Bubble);
        Viewport.AddHandler(PointerMovedEvent, OnViewportPointerMoved, RoutingStrategies.Bubble);
        Viewport.AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Bubble);
        Viewport.PointerCaptureLost += OnViewportPointerCaptureLost;

        ThumbnailList.AddHandler(PointerWheelChangedEvent, OnThumbnailPointerWheelChanged, RoutingStrategies.Tunnel);
        ThumbnailList.AddHandler(PointerPressedEvent, OnThumbnailPointerPressed, RoutingStrategies.Tunnel);
        ComicThumbnailList.AddHandler(PointerWheelChangedEvent, OnComicThumbnailPointerWheelChanged, RoutingStrategies.Tunnel);
        ComicThumbnailList.AddHandler(PointerPressedEvent, OnComicThumbnailPointerPressed, RoutingStrategies.Tunnel);

        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                OnWindowStateChanged(WindowState);
            }
            else if (e.Property == OffScreenMarginProperty)
            {
                UpdateMaximizedMargin();
            }
        };
        ScalingChanged += (_, _) => UpdateViewport();

        RestoreWindowPlacement();

        Opened += (_, _) =>
        {
            UpdateChrome(allowHide: true);
            Viewport.Focus();
            UpdateViewport();
        };
        Closing += OnClosing;
    }

    public MainViewModel ViewModel => _vm;

    #region 창 위치 저장 · 복원

    private void RestoreWindowPlacement()
    {
        var w = _vm.Settings.Window;
        Width = w.Width;
        Height = w.Height;

        if (w.Left is { } left && w.Top is { } top && IsOnScreen(left, top, w.Width, w.Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var scale = DesktopScaling;
            Position = new PixelPoint((int)Math.Round(left * scale), (int)Math.Round(top * scale));
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (w.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private bool IsOnScreen(double left, double top, double width, double height)
    {
        var scale = DesktopScaling;
        var titleBar = new PixelRect(
            (int)Math.Round((left + 40) * scale),
            (int)Math.Round(top * scale),
            Math.Max(1, (int)Math.Round((width - 80) * scale)),
            (int)Math.Round(30 * scale));
        return Screens.All.Any(s => s.Bounds.Intersects(titleBar));
    }

    private void SaveWindowPlacement()
    {
        var w = _vm.Settings.Window;
        var state = _vm.IsFullScreen ? _restoreState : WindowState;

        if (state == WindowState.Normal)
        {
            var scale = DesktopScaling;
            w.Left = Position.X / scale;
            w.Top = Position.Y / scale;
            w.Width = Width;
            w.Height = Height;
        }

        w.IsMaximized = state == WindowState.Maximized;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveWindowPlacement();
        _vm.SaveSettings();
        _vm.Dispose();
    }

    #endregion

    #region 전체 화면

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsFullScreen):
                ApplyFullScreen(_vm.IsFullScreen);
                UpdateChrome(allowHide: true);
                break;
            case nameof(MainViewModel.TopBarPinned):
            case nameof(MainViewModel.BottomBarPinned):
                UpdateChrome(allowHide: true);
                break;
            case nameof(MainViewModel.CurrentIndex):
                ScrollThumbnailIntoView();
                break;
            case nameof(MainViewModel.ThumbnailItems):
            case nameof(MainViewModel.ComicThumbnailItems):
            case nameof(MainViewModel.IsComicMode):
                Dispatcher.UIThread.Post(ScrollThumbnailIntoView, DispatcherPriority.Loaded);
                break;
        }
    }

    private void ApplyFullScreen(bool fullScreen)
    {
        if (fullScreen)
        {
            _restoreState = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _restoreState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }

        Viewport.Focus();
    }

    #endregion

    #region 상단 메뉴 · 하단 바 표시

    private bool IsMenuInUse =>
        TopMenu.IsKeyboardFocusWithin ||
        TopMenu.Items.OfType<MenuItem>().Any(m => m.IsSubMenuOpen);

    private void UpdateChrome(bool allowHide)
    {
        var menuVisible = _vm.TopBarPinned || _menuHover || IsMenuInUse || TopBar.IsPointerOver;
        var barVisible = _vm.BottomBarPinned || _barHover || BottomBar.IsKeyboardFocusWithin || BottomBar.IsPointerOver;

        if (menuVisible)
        {
            TopBar.IsVisible = true;
        }
        else if (allowHide)
        {
            TopBar.IsVisible = false;
        }

        if (barVisible)
        {
            BottomBar.IsVisible = true;
        }
        else if (allowHide)
        {
            BottomBar.IsVisible = false;
        }

        BottomBar.MaxWidth = Math.Max(200, RootGrid.Bounds.Width - 32);

        // 프레임 정보는 하단 바가 보이면 그 위에 둔다.
        var frameBottom = BottomBar.IsVisible && BottomBar.Bounds.Height > 0
            ? BottomBar.Bounds.Height + BottomBar.Margin.Bottom + 10
            : 14;
        if (Math.Abs(FrameInfo.Margin.Bottom - frameBottom) > 0.5)
        {
            FrameInfo.Margin = new Thickness(14, 0, 0, frameBottom);
        }

        // 정보 패널은 고정된 상단 바 아래에 둔다.
        var infoTop = (_vm.TopBarPinned ? TopBar.Bounds.Height : 0) + 12;
        if (Math.Abs(InfoPanel.Margin.Top - infoTop) > 0.5)
        {
            InfoPanel.Margin = new Thickness(12, infoTop, 0, 0);
        }

        // 업데이트 알림은 보이는 상단 바 아래에 둔다.
        var noticeTop = (TopBar.IsVisible ? TopBar.Bounds.Height : 0) + 12;
        if (Math.Abs(UpdateNotice.Margin.Top - noticeTop) > 0.5)
        {
            UpdateNotice.Margin = new Thickness(0, noticeTop, 16, 0);
        }
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            return;
        }

        var p = e.GetPosition(RootGrid);
        var height = RootGrid.Bounds.Height;

        _menuHover = p.Y <= Math.Max(MinRevealZone, _topBarExtent);
        _barHover = p.Y >= height - Math.Max(MinRevealZone, _bottomBarExtent);
        UpdateChrome(allowHide: true);
    }

    private void OnWindowPointerExited(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(RootGrid);
        var bounds = RootGrid.Bounds;
        if (bounds.Width > 0 && bounds.Height > 0 &&
            p.X >= 0 && p.X <= bounds.Width &&
            p.Y >= 0 && p.Y <= bounds.Height)
        {
            if (TopBar.IsPointerOver || p.Y <= Math.Max(MinRevealZone, _topBarExtent) ||
                BottomBar.IsPointerOver || p.Y >= bounds.Height - Math.Max(MinRevealZone, _bottomBarExtent))
            {
                return;
            }
        }

        _menuHover = false;
        _barHover = false;
        UpdateChrome(allowHide: true);
    }

    #endregion

    #region 직접 그린 타이틀바

    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TopBar).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Control source &&
            (source.FindAncestorOfType<MenuItem>(includeSelf: true) is not null ||
             source.FindAncestorOfType<Button>(includeSelf: true) is not null ||
             source.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is not null))
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (_vm.IsFullScreen)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (_vm.IsFullScreen)
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnWindowStateChanged(WindowState state)
    {
        UpdateMaximizedMargin();
        var maximized = state == WindowState.Maximized;
        if (this.TryFindResource(maximized ? "IconRestore" : "IconMaximize", ActualThemeVariant, out var resource) &&
            resource is Avalonia.Media.Geometry geometry)
        {
            MaximizeIcon.Data = geometry;
        }
        var tip = maximized ? "이전 크기로 복원" : "최대화";
        ToolTip.SetTip(MaximizeButton, tip);
        ToolTip.SetTip(MacMaximizeButton, tip);
    }

    private void UpdateMaximizedMargin()
    {
        var margin = WindowState == WindowState.Maximized && !_vm.IsFullScreen
            ? OffScreenMargin
            : new Thickness(0);

        if (RootGrid.Margin != margin)
        {
            RootGrid.Margin = margin;
        }
    }

    #endregion

    #region 키보드

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // 팝업 다이얼로그가 열려 있으면 메인 창 단축키(특히 Esc 종료)를 실행하지 않고,
        // 포커스가 메인 창에 남아 있는 환경(Linux 등)에서도 Esc 로 열린 다이얼로그를 닫는다.
        if (OwnedWindows.LastOrDefault(w => w.IsVisible) is { } activeDialog)
        {
            if (e.Key == Key.Escape)
            {
                activeDialog.Close();
                e.Handled = true;
            }

            return;
        }

        if (KeyboardHelper.IsInputControlFocused(this) ||
            FocusManager?.GetFocusedElement() is MenuItem)
        {
            return;
        }

        var action = KeyboardHelper.GetAction(e.Key, e.KeyModifiers);
        if (action == ShortcutAction.None)
        {
            return;
        }

        e.Handled = true;
        switch (action)
        {
            case ShortcutAction.ToggleFullScreen:
                _vm.ToggleFullScreenCommand.Execute(null);
                break;
            case ShortcutAction.Exit:
                Close();
                break;
            case ShortcutAction.ToggleMenus:
                _vm.ToggleMenusCommand.Execute(null);
                break;
            case ShortcutAction.PreviousPage:
                _vm.PreviousPageCommand.Execute(null);
                break;
            case ShortcutAction.NextPage:
                _vm.NextPageCommand.Execute(null);
                break;
            case ShortcutAction.PreviousSource:
                _vm.PreviousSourceCommand.Execute(null);
                break;
            case ShortcutAction.NextSource:
                _vm.NextSourceCommand.Execute(null);
                break;
            case ShortcutAction.RotateClockwise:
                _vm.RotateClockwiseCommand.Execute(null);
                break;
            case ShortcutAction.RotateCounterClockwise:
                _vm.RotateCounterClockwiseCommand.Execute(null);
                break;
            case ShortcutAction.FlipHorizontal:
                _vm.FlipHorizontalCommand.Execute(null);
                break;
            case ShortcutAction.FlipVertical:
                _vm.FlipVerticalCommand.Execute(null);
                break;
            case ShortcutAction.ToggleInfoPanel:
                _vm.ToggleInfoPanelCommand.Execute(null);
                break;
            case ShortcutAction.ToggleComicMode:
                _vm.ToggleComicModeCommand.Execute(null);
                break;
            case ShortcutAction.ToggleComicDirection:
                _vm.ToggleComicDirectionCommand.Execute(null);
                break;
            case ShortcutAction.ToggleComicBlankPage:
                _vm.ToggleComicBlankPageCommand.Execute(null);
                break;
            case ShortcutAction.Previous:
                _vm.PreviousCommand.Execute(null);
                break;
            case ShortcutAction.Next:
                _vm.NextCommand.Execute(null);
                break;
            case ShortcutAction.First:
                _vm.FirstCommand.Execute(null);
                break;
            case ShortcutAction.Last:
                _vm.LastCommand.Execute(null);
                break;
            case ShortcutAction.OpenImageList:
                _vm.ShowImageListCommand.Execute(null);
                break;
            case ShortcutAction.ModeOriginal:
                _vm.SetDisplayModeCommand.Execute(DisplayMode.Original);
                break;
            case ShortcutAction.ModeFitWidth:
                _vm.SetDisplayModeCommand.Execute(DisplayMode.FitWidth);
                break;
            case ShortcutAction.ModeFitHeight:
                _vm.SetDisplayModeCommand.Execute(DisplayMode.FitHeight);
                break;
            case ShortcutAction.ModeFill:
                _vm.SetDisplayModeCommand.Execute(DisplayMode.Fill);
                break;
            case ShortcutAction.FilterNone:
                _vm.SetFilterCommand.Execute(ImageFilter.None);
                break;
            case ShortcutAction.FilterSmooth:
                _vm.SetFilterCommand.Execute(ImageFilter.Smooth);
                break;
            case ShortcutAction.FilterSmoothSharpen:
                _vm.SetFilterCommand.Execute(ImageFilter.SmoothSharpen);
                break;
            case ShortcutAction.ZoomIn:
                ZoomAt(null, zoomIn: true);
                break;
            case ShortcutAction.ZoomOut:
                ZoomAt(null, zoomIn: false);
                break;
            case ShortcutAction.ToggleSlideShow:
                _vm.ToggleSlideShowCommand.Execute(null);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private async void OnShowShortcuts(object? sender, RoutedEventArgs e)
    {
        var dialog = new ShortcutsWindow();
        await dialog.ShowDialog(this);
    }

    private async void OnShowAbout(object? sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow(Services.AboutInfo.Create(), _vm.Update);
        await dialog.ShowDialog(this);
    }

    #endregion

    #region 표시 영역 · 이동(Pan) · 확대

    private void UpdateViewport()
    {
        var dpi = DesktopScaling;
        _vm.UpdateViewport(Viewport.Bounds.Width, Viewport.Bounds.Height, dpi);
        PositionImage(isNewImage: false);
    }

    private void OnLayoutChanged(object? sender, LayoutChangedEventArgs e) => PositionImage(e.IsNewImage);

    private void PositionImage(bool isNewImage)
    {
        var vw = Viewport.Bounds.Width;
        var vh = Viewport.Bounds.Height;
        var w = _vm.DisplayWidth;
        var h = _vm.DisplayHeight;

        if (isNewImage || _lastImageWidth <= 0 || _lastImageHeight <= 0)
        {
            _offsetX = (vw - w) / 2;
            _offsetY = h > vh ? 0 : (vh - h) / 2;
        }
        else
        {
            var anchor = _zoomAnchor ?? new Point(_lastViewportWidth / 2, _lastViewportHeight / 2);
            var fx = (anchor.X - _offsetX) / _lastImageWidth;
            var fy = (anchor.Y - _offsetY) / _lastImageHeight;

            var newAnchor = _zoomAnchor ?? new Point(vw / 2, vh / 2);
            _offsetX = newAnchor.X - fx * w;
            _offsetY = newAnchor.Y - fy * h;
        }

        _zoomAnchor = null;
        _lastImageWidth = w;
        _lastImageHeight = h;
        _lastViewportWidth = vw;
        _lastViewportHeight = vh;
        ClampAndApply();
    }

    private void ClampAndApply()
    {
        var vw = Viewport.Bounds.Width;
        var vh = Viewport.Bounds.Height;
        var w = _vm.DisplayWidth;
        var h = _vm.DisplayHeight;

        _offsetX = w <= vw ? (vw - w) / 2 : Math.Clamp(_offsetX, vw - w, 0);
        _offsetY = h <= vh ? (vh - h) / 2 : Math.Clamp(_offsetY, vh - h, 0);

        Canvas.SetLeft(DisplaySurface, _offsetX);
        Canvas.SetTop(DisplaySurface, _offsetY);

        Viewport.Cursor = _isDragging
            ? new Cursor(StandardCursorType.SizeAll)
            : (_vm.IsFullScreen && CanPan ? new Cursor(StandardCursorType.Hand) : Cursor.Default);
        UpdateChrome(allowHide: false);
    }

    private void ZoomAt(Point? anchor, bool zoomIn)
    {
        _zoomAnchor = anchor;
        if (zoomIn)
        {
            _vm.ZoomInCommand.Execute(null);
        }
        else
        {
            _vm.ZoomOutCommand.Execute(null);
        }

        _zoomAnchor = null;
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        var modifiers = e.KeyModifiers;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            _offsetY += e.Delta.Y * 60.0;
            ClampAndApply();
            return;
        }

        var zoom = _vm.MouseWheelAction == MouseWheelAction.Zoom;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            zoom = !zoom;
        }

        if (zoom)
        {
            ZoomAt(e.GetPosition(Viewport), e.Delta.Y > 0);
        }
        else if (e.Delta.Y > 0)
        {
            _vm.PreviousCommand.Execute(null);
        }
        else
        {
            _vm.NextCommand.Execute(null);
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        var props = point.Properties;

        if (props.PointerUpdateKind == PointerUpdateKind.XButton1Pressed)
        {
            _vm.PreviousCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (props.PointerUpdateKind == PointerUpdateKind.XButton2Pressed)
        {
            _vm.NextCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (props.IsMiddleButtonPressed)
        {
            Viewport.Focus();
            if (CanPan)
            {
                StartPan(e);
            }

            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        Viewport.Focus();
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            if (!_vm.IsFullScreen)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }

            return;
        }

        if (_vm.IsFullScreen)
        {
            if (CanPan)
            {
                StartPan(e);
            }

            return;
        }

        BeginMoveDrag(e);
    }

    private void StartPan(PointerPressedEventArgs e)
    {
        _dragStart = e.GetPosition(Viewport);
        _dragStartOffsetX = _offsetX;
        _dragStartOffsetY = _offsetY;
        e.Pointer.Capture(Viewport);
        _isDragging = true;
        ClampAndApply();
    }

    private bool CanPan =>
        _vm.CurrentBitmap is not null &&
        (_vm.DisplayWidth > Viewport.Bounds.Width + 0.5 || _vm.DisplayHeight > Viewport.Bounds.Height + 0.5);

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var p = e.GetPosition(Viewport);
        _offsetX = _dragStartOffsetX + (p.X - _dragStart.X);
        _offsetY = _dragStartOffsetY + (p.Y - _dragStart.Y);
        ClampAndApply();
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && e.InitialPressMouseButton is MouseButton.Left or MouseButton.Middle)
        {
            e.Pointer.Capture(null);
            _isDragging = false;
            ClampAndApply();
        }
    }

    private void OnViewportPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isDragging = false;
        ClampAndApply();
    }

    #endregion

    #region 썸네일

    private void ScrollThumbnailIntoView()
    {
        if (_vm.IsComicMode)
        {
            ScrollListBoxCentered(ComicThumbnailList, _vm.ComicThumbnailSelectedIndex);
        }
        else
        {
            ScrollListBoxCentered(ThumbnailList, _vm.CurrentIndex);
        }
    }

    private static void ScrollListBoxCentered(ListBox listBox, int index)
    {
        var count = listBox.Items.Count;
        if (index < 0 || index >= count || !listBox.IsVisible)
        {
            return;
        }

        listBox.ScrollIntoView(index);

        if (listBox.FindDescendantOfType<ScrollViewer>() is { } scroll)
        {
            var extentWidth = scroll.Extent.Width;
            var viewportWidth = scroll.Viewport.Width;
            if (extentWidth > viewportWidth && viewportWidth > 0)
            {
                var itemWidth = extentWidth / count;
                var targetX = index * itemWidth - (viewportWidth - itemWidth) / 2;
                var maxScroll = Math.Max(0, extentWidth - viewportWidth);
                scroll.Offset = new Vector(Math.Clamp(targetX, 0, maxScroll), scroll.Offset.Y);
            }
        }
    }

    private void OnThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ThumbnailList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Control source &&
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ThumbnailItemViewModel item })
        {
            e.Handled = true;
            _ = _vm.GoToAsync(item.Index);
            Viewport.Focus();
        }
    }

    private void OnComicThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ComicThumbnailList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Control source &&
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ComicThumbnailItemViewModel item })
        {
            e.Handled = true;
            _ = _vm.GoToAsync(item.StartIndex);
            Viewport.Focus();
        }
    }

    private void OnThumbnailPointerWheelChanged(object? sender, PointerWheelEventArgs e) =>
        ScrollListBoxByWheel(ThumbnailList, e);

    private void OnComicThumbnailPointerWheelChanged(object? sender, PointerWheelEventArgs e) =>
        ScrollListBoxByWheel(ComicThumbnailList, e);

    private static void ScrollListBoxByWheel(ListBox listBox, PointerWheelEventArgs e)
    {
        if (listBox.FindDescendantOfType<ScrollViewer>() is { } scroll)
        {
            scroll.Offset = new Vector(
                Math.Max(0, scroll.Offset.X - e.Delta.Y * 48),
                scroll.Offset.Y);
            e.Handled = true;
        }
    }

    #endregion

    #region 드래그 앤 드롭

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasPayload =
            OperatingSystem.IsLinux() ||
            e.DataTransfer.Contains(DataFormat.File) ||
            e.DataTransfer.Contains(DataFormat.Text) ||
            e.DataTransfer.Formats.Count > 0;

        if (!hasPayload)
        {
            e.DragEffects = DragDropEffects.None;
        }
        else if (e.DragEffects.HasFlag(DragDropEffects.Copy))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else if (e.DragEffects.HasFlag(DragDropEffects.Link))
        {
            e.DragEffects = DragDropEffects.Link;
        }
        else if (e.DragEffects.HasFlag(DragDropEffects.Move))
        {
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            e.DragEffects = DragDropEffects.Copy;
        }

        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        e.Handled = true;
        var path = ExtractDropPath(e.DataTransfer);
        if (path is not null)
        {
            Activate();
            await _vm.OpenPathAsync(path);
        }
    }

    private static string? ExtractDropPath(IDataTransfer data)
    {
        var files = data.TryGetFiles();
        if (files is not null)
        {
            foreach (var item in files)
            {
                var local = item.TryGetLocalPath();
                if (!string.IsNullOrEmpty(local))
                {
                    return local;
                }

                if (item.Path is { IsFile: true } uri)
                {
                    return uri.LocalPath;
                }
            }
        }

        var text = data.TryGetText();
        if (ParseDropText(text) is { } fromText)
        {
            return fromText;
        }

        // Linux(X11/Wayland) 파일 관리자가 text/uri-list, x-special/gnome-copied-files 등으로 전달하는 경우
        foreach (var item in data.Items)
        {
            foreach (var format in item.Formats)
            {
                if (format is DataFormat<string> stringFormat)
                {
                    if (ParseDropText(item.TryGetRaw(stringFormat) as string) is { } parsed)
                    {
                        return parsed;
                    }
                }
                else if (format is DataFormat<byte[]> bytesFormat &&
                         item.TryGetRaw(bytesFormat) is byte[] bytes &&
                         bytes.Length > 0)
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                    if (ParseDropText(decoded) is { } parsed)
                    {
                        return parsed;
                    }
                }
            }
        }

        return null;
    }

    private static string? ParseDropText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith('#') ||
                rawLine.Equals("copy", StringComparison.OrdinalIgnoreCase) ||
                rawLine.Equals("cut", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(rawLine, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                return uri.LocalPath;
            }

            if (rawLine.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var unescaped = Uri.UnescapeDataString(rawLine["file://".Length..]);
                if (File.Exists(unescaped) || Directory.Exists(unescaped))
                {
                    return unescaped;
                }
            }

            if (File.Exists(rawLine) || Directory.Exists(rawLine))
            {
                return rawLine;
            }
        }

        return null;
    }

    private void OnLinuxResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var edge = HitTestResizeEdge(e.GetPosition(this));
        if (edge is { } resizeEdge)
        {
            e.Handled = true;
            BeginResizeDrag(resizeEdge, e);
        }
    }

    private WindowEdge? HitTestResizeEdge(Point p)
    {
        const double border = 6;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= border * 2 || h <= border * 2)
        {
            return null;
        }

        var left = p.X <= border;
        var right = p.X >= w - border;
        var top = p.Y <= border;
        var bottom = p.Y >= h - border;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }

    #endregion
}
