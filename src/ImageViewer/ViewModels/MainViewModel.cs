using System.Collections.ObjectModel;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageViewer.Helpers;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;
using Microsoft.Extensions.Logging;

namespace ImageViewer.ViewModels;

/// <summary>표시 크기가 바뀌었음을 View 에 알린다. 새 이미지이면 이동(Pan) 위치를 초기화한다.</summary>
public sealed class LayoutChangedEventArgs(bool isNewImage) : EventArgs
{
    public bool IsNewImage { get; } = isNewImage;
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLoadedThumbnails = 300;

    private readonly SettingsService _settingsService;
    private readonly IImageSourceFactory _sourceFactory;
    private readonly ImageLoaderService _loader;
    private readonly ImageRenderService _renderer;
    private readonly ThumbnailService _thumbnails;
    private readonly ImageNavigationService _navigation;
    private readonly SlideShowService _slideShow;
    private readonly ShellIntegrationService _shell;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _renderDebounce;
    private readonly DispatcherTimer _osdTimer;
    private readonly DispatcherTimer _loadingIndicatorTimer;
    private readonly LinkedList<ThumbnailItemViewModel> _loadedThumbnails = new();

    private IImageSource? _source;
    private IReadOnlyList<ImageInfo> _images = [];
    private bool[] _widePages = [];
    private readonly HashSet<int> _manualComicBlanks = [];
    private bool _comicPagesProbed;
    private IReadOnlyList<ComicSpread> _comicSpreads = [];
    private DecodedImage? _decoded;
    private DecodedImage? _secondDecoded;
    private string? _bitmapImageKey;
    private bool _pendingNewImage;
    private double _zoomFactor = 1.0;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _dpiScale = 1.0;
    private int _lastDirection = 1;
    private int _loadVersion;
    private int _renderVersion;
    private int _openVersion;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _comicProbeCts;

    public MainViewModel(
        SettingsService settingsService,
        IImageSourceFactory sourceFactory,
        ImageLoaderService loader,
        ImageRenderService renderer,
        ThumbnailService thumbnails,
        ImageNavigationService navigation,
        SlideShowService slideShow,
        ShellIntegrationService shell,
        IDialogService dialogs,
        ILogger<MainViewModel> logger,
        UpdateViewModel? update = null)
    {
        _settingsService = settingsService;
        _sourceFactory = sourceFactory;
        _loader = loader;
        _renderer = renderer;
        _thumbnails = thumbnails;
        _navigation = navigation;
        _slideShow = slideShow;
        _shell = shell;
        _dialogs = dialogs;
        Update = update;
        if (update is not null)
        {
            update.RelaunchPathProvider = () => RelaunchPath;
        }
        _logger = logger;
        _dispatcher = Dispatcher.UIThread;

        _renderDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _renderDebounce.Tick += (_, _) =>
        {
            _renderDebounce.Stop();
            _ = RenderCurrentAsync();
        };

        _osdTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.6),
        };
        _osdTimer.Tick += (_, _) =>
        {
            _osdTimer.Stop();
            OsdMessage = null;
        };

        // 짧은 로딩에는 표시하지 않아 깜박임을 줄인다.
        _loadingIndicatorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _loadingIndicatorTimer.Tick += (_, _) =>
        {
            _loadingIndicatorTimer.Stop();
            IsLoading = true;
        };

        _slideShow.Tick += OnSlideShowTick;
        ApplySettingsToServices();
    }

    /// <summary>표시 크기 변경 알림 (View 가 이동 위치를 조정).</summary>
    public event EventHandler<LayoutChangedEventArgs>? LayoutChanged;

    public ViewerSettings Settings => _settingsService.Current;

    #region 바인딩 속성

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ImageSourceInfo? _sourceInfo;

    [ObservableProperty]
    private ObservableCollection<ThumbnailItemViewModel> _thumbnailItems = [];

    [ObservableProperty]
    private ObservableCollection<ComicThumbnailItemViewModel> _comicThumbnailItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(PositionText),
        nameof(ThumbnailSelectedIndex),
        nameof(HasImages),
        nameof(ShowThumbnailStrip),
        nameof(ShowSingleThumbnails),
        nameof(ShowComicThumbnails))]
    private int _currentIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ImageInfo? _currentImage;

    [ObservableProperty]
    private Bitmap? _currentBitmap;

    [ObservableProperty]
    private Bitmap? _leftPageBitmap;

    [ObservableProperty]
    private Bitmap? _rightPageBitmap;

    [ObservableProperty]
    private double _leftPageWidth;

    [ObservableProperty]
    private double _leftPageHeight;

    [ObservableProperty]
    private double _rightPageWidth;

    [ObservableProperty]
    private double _rightPageHeight;

    [ObservableProperty]
    private bool _hasLeftPageSlot;

    [ObservableProperty]
    private bool _hasRightPageSlot;

    [ObservableProperty]
    private double _displayWidth;

    [ObservableProperty]
    private double _displayHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleText))]
    private double _scale = 1.0;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _placeholderMessage = "폴더, ZIP 또는 이미지 파일을 열어 주세요.\n(파일 메뉴 · 드래그 앤 드롭 · 탐색기 우클릭 메뉴)";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _osdMessage;

    [ObservableProperty]
    private bool _isFullScreen;

    [ObservableProperty]
    private bool _isSlideShowPlaying;

    public bool HasImages => _images.Count > 0;

    public string WindowTitle
    {
        get
        {
            if (CurrentImage is null)
            {
                return SourceInfo is null ? "ImageViewer" : $"{SourceInfo.DisplayName} - ImageViewer";
            }

            var name = CurrentImage.Name;
            if (IsComicMode && CurrentIndex >= 0)
            {
                var spread = ComicSpreadHelper.FindSpread(_comicSpreads, CurrentIndex);
                if (spread is { SecondIndex: { } s } && s < _images.Count)
                {
                    name = ComicDirection == ComicReadingDirection.RightToLeft
                        ? $"{_images[s].Name} · {_images[spread.FirstIndex].Name}"
                        : $"{_images[spread.FirstIndex].Name} · {_images[s].Name}";
                }
            }

            return SourceInfo is null
                ? $"{name} - ImageViewer"
                : $"{name} - {SourceInfo.DisplayName} - ImageViewer";
        }
    }

    public string PositionText
    {
        get
        {
            if (_images.Count == 0)
            {
                return "0 / 0";
            }

            if (IsComicMode && CurrentIndex >= 0)
            {
                var spread = ComicSpreadHelper.FindSpread(_comicSpreads, CurrentIndex);
                if (spread is not null)
                {
                    return spread.SecondIndex is { } s
                        ? $"{spread.FirstIndex + 1:N0}-{s + 1:N0} / {_images.Count:N0}"
                        : $"{spread.FirstIndex + 1:N0} / {_images.Count:N0}";
                }
            }

            var text = $"{CurrentIndex + 1:N0} / {_images.Count:N0}";
            return _decoded is { PageCount: > 1 } d ? $"{text}  ·  {d.PageIndex + 1}/{d.PageCount}쪽" : text;
        }
    }

    public string ScaleText => $"{Scale * 100:0}%";

    /// <summary>썸네일 목록 선택 위치. 사용자가 선택하면 해당 이미지로 이동한다.</summary>
    public int ThumbnailSelectedIndex
    {
        get => CurrentIndex;
        set
        {
            if (value >= 0 && value != CurrentIndex)
            {
                _ = GoToAsync(value);
            }
        }
    }

    /// <summary>코믹 뷰어(2페이지) 하단 썸네일 선택 위치.</summary>
    public int ComicThumbnailSelectedIndex
    {
        get => ComicSpreadHelper.FindSpreadIndex(_comicSpreads, CurrentIndex);
        set
        {
            if (value >= 0 && value < ComicThumbnailItems.Count)
            {
                var start = ComicThumbnailItems[value].StartIndex;
                if (start != CurrentIndex)
                {
                    _ = GoToAsync(start);
                }
            }
        }
    }

    public bool IsComicMode
    {
        get => Settings.Display.ComicMode;
        set
        {
            if (Settings.Display.ComicMode == value)
            {
                return;
            }

            Settings.Display.ComicMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSingleThumbnails));
            OnPropertyChanged(nameof(ShowComicThumbnails));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ComicThumbnailSelectedIndex));
            OnPropertyChanged(nameof(IsCurrentSpreadManualBlank));
            OnPropertyChanged(nameof(ComicBlankButtonText));
            ShowOsd(value ? $"코믹 뷰어 켜기 ({ComicDirectionText})" : "코믹 뷰어 끄기");

            if (_images.Count == 0)
            {
                return;
            }

            if (value)
            {
                if (!_comicPagesProbed)
                {
                    StartComicPageProbe();
                }

                var start = ComicSpreadHelper.GetSpreadStart(_comicSpreads, CurrentIndex < 0 ? 0 : CurrentIndex);
                if (start < 0)
                {
                    start = 0;
                }

                _navigation.MoveTo(start);
                SetCurrent(start);
                _ = LoadCurrentAsync();
            }
            else
            {
                _comicProbeCts?.Cancel();
                ReplaceSecondDecoded(null);
                ClearComicPages();
                _ = LoadCurrentAsync();
            }
        }
    }

    public ComicReadingDirection ComicDirection
    {
        get => Settings.Display.ComicDirection;
        set
        {
            if (Settings.Display.ComicDirection == value)
            {
                return;
            }

            Settings.Display.ComicDirection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ComicDirectionText));
            OnPropertyChanged(nameof(WindowTitle));
            foreach (var item in ComicThumbnailItems)
            {
                item.SetDirection(value);
            }

            ShowOsd(value == ComicReadingDirection.RightToLeft
                ? "코믹 방향: 우 → 좌 [2p, 1p]"
                : "코믹 방향: 좌 → 우 [1p, 2p]");

            if (IsComicMode && _decoded is not null && !IsCurrentWideSingle(_decoded, _secondDecoded))
            {
                // 이미 렌더링된 좌/우 비트맵과 크기·슬롯만 즉시 뒤바꾼다.
                (LeftPageBitmap, RightPageBitmap) = (RightPageBitmap, LeftPageBitmap);
                (LeftPageWidth, RightPageWidth) = (RightPageWidth, LeftPageWidth);
                (LeftPageHeight, RightPageHeight) = (RightPageHeight, LeftPageHeight);
                (HasLeftPageSlot, HasRightPageSlot) = (HasRightPageSlot, HasLeftPageSlot);
                ApplyComicDimensions();
            }
        }
    }

    public string ComicDirectionText =>
        ComicDirection == ComicReadingDirection.RightToLeft ? "우→좌" : "좌→우";

    public bool HasManualComicBlanks => _manualComicBlanks.Count > 0;

    public bool IsCurrentSpreadManualBlank =>
        IsComicMode && CurrentIndex >= 0 &&
        ComicSpreadHelper.FindSpread(_comicSpreads, CurrentIndex) is { TrailingBlank: true };

    public string ComicBlankButtonText => IsCurrentSpreadManualBlank ? "빈쪽 해제" : "+ 빈쪽";

    public DisplayMode DisplayMode
    {
        get => Settings.Display.Mode;
        set
        {
            if (Settings.Display.Mode == value)
            {
                return;
            }

            Settings.Display.Mode = value;
            OnPropertyChanged();
            _zoomFactor = 1.0;
            ShowOsd(GetModeName(value));
            UpdateLayoutNow(renderImmediately: true);
        }
    }

    public ImageFilter Filter
    {
        get => Settings.Display.Filter;
        set
        {
            if (Settings.Display.Filter == value)
            {
                return;
            }

            Settings.Display.Filter = value;
            OnPropertyChanged();
            ShowOsd("필터: " + ImageFilterService.GetDisplayName(value));
            _ = RenderCurrentAsync();
        }
    }

    public int SlideShowInterval
    {
        get => Settings.Slideshow.IntervalSeconds;
        set
        {
            var v = SlideShowSettings.ClampInterval(value);
            if (Settings.Slideshow.IntervalSeconds == v)
            {
                return;
            }

            Settings.Slideshow.IntervalSeconds = v;
            _slideShow.IntervalSeconds = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SlideShowIntervalText));
        }
    }

    public string SlideShowIntervalText => $"{SlideShowInterval}초";

    public SlideShowMode SlideShowMode
    {
        get => Settings.Slideshow.Mode;
        set
        {
            if (Settings.Slideshow.Mode == value)
            {
                return;
            }

            Settings.Slideshow.Mode = value;
            _slideShow.Mode = value;
            OnPropertyChanged();
            ShowOsd(value == SlideShowMode.Random ? "랜덤 재생" : "순차 재생");
            if (IsSlideShowPlaying)
            {
                StartPrefetch(); // 재생 순서가 바뀌었으므로 다음 이미지를 다시 미리 읽는다
            }
        }
    }

    public bool SlideShowRepeat
    {
        get => Settings.Slideshow.Repeat;
        set
        {
            if (Settings.Slideshow.Repeat == value)
            {
                return;
            }

            Settings.Slideshow.Repeat = value;
            _slideShow.Repeat = value;
            OnPropertyChanged();
            if (IsSlideShowPlaying)
            {
                StartPrefetch();
            }
        }
    }

    public bool ThumbnailsVisible
    {
        get => Settings.Thumbnail.Visible;
        set
        {
            if (Settings.Thumbnail.Visible == value)
            {
                return;
            }

            Settings.Thumbnail.Visible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowThumbnailStrip));
            OnPropertyChanged(nameof(ShowSingleThumbnails));
            OnPropertyChanged(nameof(ShowComicThumbnails));
        }
    }

    public bool ShowThumbnailStrip => ThumbnailsVisible && HasImages;

    public bool ShowSingleThumbnails => ShowThumbnailStrip && !IsComicMode;

    public bool ShowComicThumbnails => ShowThumbnailStrip && IsComicMode;

    public double ThumbnailSize => Settings.Thumbnail.Size;

    public double ComicThumbnailWidth => Math.Round(Settings.Thumbnail.Size * 0.72);

    public double ComicThumbnailWideWidth => ComicThumbnailWidth * 2;

    /// <summary>하단 썸네일 목록의 가로 스크롤바 (설정 &gt; 썸네일). 켜면 넘칠 때만 표시.</summary>
    public ScrollBarVisibility ThumbnailScrollBarVisibility =>
        Settings.Thumbnail.ShowScrollBar
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Hidden;

    public ViewerBackground Background => Settings.Display.Background;

    /// <summary>상단 바 고정 여부. 해제하면 마우스를 위쪽에 올렸을 때만 나타난다.</summary>
    public bool TopBarPinned
    {
        get => Settings.Display.TopBarPinned;
        set
        {
            if (Settings.Display.TopBarPinned == value)
            {
                return;
            }

            Settings.Display.TopBarPinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BothBarsPinned));
        }
    }

    /// <summary>상단 · 하단 바가 모두 고정된 상태 (` 키 메뉴 항목 표시용).</summary>
    public bool BothBarsPinned => TopBarPinned && BottomBarPinned;

    /// <summary>하단 바 고정 여부. 해제하면 마우스를 올렸을 때만 나타난다.</summary>
    public bool BottomBarPinned
    {
        get => Settings.Display.BottomBarPinned;
        set
        {
            if (Settings.Display.BottomBarPinned == value)
            {
                return;
            }

            Settings.Display.BottomBarPinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BothBarsPinned));
        }
    }

    public MouseWheelAction MouseWheelAction => Settings.Display.MouseWheel;

    #endregion

    #region 열기

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var path = _dialogs.PickFolder();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = _dialogs.PickFile();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    /// <summary>폴더, ZIP 또는 이미지 파일 경로를 연다.</summary>
    public async Task OpenPathAsync(string path)
    {
        var version = ++_openVersion;
        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        var ct = _openCts.Token;

        ImageSourceOpenResult result;
        IReadOnlyList<ImageInfo> images;
        try
        {
            result = _sourceFactory.Create(path);
        }
        catch (ImageSourceException ex)
        {
            _dialogs.ShowMessage(ex.Message, isError: true);
            return;
        }

        try
        {
            images = await result.Source.GetImagesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            result.Source.Dispose();
            return;
        }
        catch (Exception ex)
        {
            result.Source.Dispose();
            _logger.LogWarning(ex, "이미지 소스를 열지 못했습니다: {Name}", Path.GetFileName(path));
            _dialogs.ShowMessage(ex is ImageSourceException ? ex.Message : "이미지 목록을 읽지 못했습니다.", isError: true);
            return;
        }

        if (version != _openVersion)
        {
            result.Source.Dispose();
            return;
        }

        var initialIndex = 0;
        if (result.InitialFileName is { } fileName)
        {
            var found = images.ToList().FindIndex(i => string.Equals(i.Name, fileName, StringComparison.OrdinalIgnoreCase));
            initialIndex = Math.Max(0, found);
        }

        SetSource(result.Source, images, initialIndex);
    }

    private void SetSource(IImageSource source, IReadOnlyList<ImageInfo> images, int initialIndex)
    {
        StopSlideShow(showMessage: false);
        CancelPending();
        _thumbnails.CancelAll();
        _loadedThumbnails.Clear();

        var old = _source;
        DetachWatcher(old);
        _comicProbeCts?.Cancel();
        _source = source;
        _images = images;
        _widePages = new bool[images.Count];
        _manualComicBlanks.Clear();
        _comicPagesProbed = false;
        _comicSpreads = ComicSpreadHelper.BuildSpreads(images.Count, _widePages, _manualComicBlanks);
        OnPropertyChanged(nameof(HasManualComicBlanks));
        OnPropertyChanged(nameof(IsCurrentSpreadManualBlank));
        OnPropertyChanged(nameof(ComicBlankButtonText));
        SourceInfo = source.Info;
        _navigation.Reset(images.Count, IsComicMode && images.Count > 0 ? ComicSpreadHelper.GetSpreadStart(_comicSpreads, initialIndex) : initialIndex);
        _slideShow.ResetOrder();
        _loader.ClearCache();
        _renderer.ClearCache();
        ReplaceDecoded(null);
        ReplaceSecondDecoded(null);
        ClearComicPages();

        // 이전 소스는 진행 중인 백그라운드 작업이 끝난 뒤 해제한다.
        if (old is not null)
        {
            _ = Task.Run(old.Dispose);
        }

        ThumbnailItems = new ObservableCollection<ThumbnailItemViewModel>(
            images.Select((info, i) => new ThumbnailItemViewModel(i, info, RequestThumbnail)));
        RebuildComicThumbnails();

        _logger.LogInformation("{Type} 열기: 이미지 {Count}개", source.Info.Label, images.Count);
        AttachWatcher(source);

        if (images.Count == 0)
        {
            CurrentIndex = -1;
            CurrentImage = null;
            CurrentBitmap = null;
            ErrorMessage = null;
            InfoItems = [];
            FrameInfoText = null;
            PlaceholderMessage = $"이 {source.Info.Label}에는 표시할 이미지가 없습니다.";
            OnPropertyChanged(nameof(PositionText));
            return;
        }

        PlaceholderMessage = null;
        _lastDirection = 1;
        SetCurrent(_navigation.CurrentIndex);
        if (IsComicMode)
        {
            StartComicPageProbe();
        }

        _ = LoadCurrentAsync();
    }

    #endregion

    #region 탐색

    [RelayCommand]
    private Task NextAsync() => IsComicMode
        ? NavigateAsync(ComicSpreadHelper.GetNextSpreadStart(_comicSpreads, CurrentIndex, _navigation.WrapAround), +1, "마지막 페이지입니다.")
        : NavigateAsync(_navigation.GetNextIndex(), +1, "마지막 이미지입니다.");

    [RelayCommand]
    private Task PreviousAsync() => IsComicMode
        ? NavigateAsync(ComicSpreadHelper.GetPreviousSpreadStart(_comicSpreads, CurrentIndex, _navigation.WrapAround), -1, "첫 번째 페이지입니다.")
        : NavigateAsync(_navigation.GetPreviousIndex(), -1, "첫 번째 이미지입니다.");

    [RelayCommand]
    private Task FirstAsync() => NavigateAsync(_navigation.GetFirstIndex(), +1, null);

    [RelayCommand]
    private Task LastAsync() => IsComicMode
        ? NavigateAsync(ComicSpreadHelper.GetLastSpreadStart(_comicSpreads), -1, null)
        : NavigateAsync(_navigation.GetLastIndex(), -1, null);

    public Task GoToAsync(int index)
    {
        var target = IsComicMode && _comicSpreads.Count > 0
            ? ComicSpreadHelper.GetSpreadStart(_comicSpreads, index)
            : index;
        return NavigateAsync(target, target >= CurrentIndex ? +1 : -1, null);
    }

    private Task NavigateAsync(int index, int direction, string? edgeMessage, bool fromSlideShow = false)
    {
        if (_images.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (index < 0)
        {
            if (edgeMessage is not null)
            {
                ShowOsd(edgeMessage);
            }

            return Task.CompletedTask;
        }

        if (!fromSlideShow)
        {
            // 정책: 슬라이드쇼 중 수동으로 이동하면 타이머를 다시 시작한다.
            _slideShow.RestartTimer();
            NoteManualNavigation();
        }

        if (index == CurrentIndex && _decoded is not null)
        {
            return Task.CompletedTask;
        }

        _lastDirection = direction;
        _navigation.MoveTo(index);
        SetCurrent(index);
        return LoadCurrentAsync();
    }

    private void SetCurrent(int index)
    {
        if (CurrentIndex >= 0 && CurrentIndex < ThumbnailItems.Count)
        {
            ThumbnailItems[CurrentIndex].IsCurrent = false;
        }

        var oldSpread = ComicSpreadHelper.FindSpreadIndex(_comicSpreads, CurrentIndex);
        if (oldSpread >= 0 && oldSpread < ComicThumbnailItems.Count)
        {
            ComicThumbnailItems[oldSpread].IsCurrent = false;
        }

        CurrentIndex = index;
        CurrentImage = index >= 0 && index < _images.Count ? _images[index] : null;

        if (index >= 0 && index < ThumbnailItems.Count)
        {
            ThumbnailItems[index].IsCurrent = true;
        }

        var newSpread = ComicSpreadHelper.FindSpreadIndex(_comicSpreads, index);
        if (newSpread >= 0 && newSpread < ComicThumbnailItems.Count)
        {
            ComicThumbnailItems[newSpread].IsCurrent = true;
        }

        OnPropertyChanged(nameof(ComicThumbnailSelectedIndex));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(IsCurrentSpreadManualBlank));
        OnPropertyChanged(nameof(ComicBlankButtonText));
    }

    #endregion

    #region 이미지 로딩 및 렌더링

    /// <summary>현재 이미지를 읽는다. 다중 페이지면 <paramref name="pageIndex"/> 페이지를 읽는다.</summary>
    /// <param name="resetTransform">다른 이미지로 이동할 때 회전 · 반전을 초기화한다 (페이지 이동은 유지).</param>
    private async Task LoadCurrentAsync(int pageIndex = 0, bool resetTransform = true)
    {
        var source = _source;
        var info = CurrentImage;
        if (source is null || info is null)
        {
            return;
        }

        var version = ++_loadVersion;
        _loadCts?.Cancel();
        _renderCts?.Cancel();   // 이전 이미지의 렌더링도 더 이상 필요 없다 (작업 자리를 비켜 준다)
        _prefetchCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _loadingIndicatorTimer.Stop();
        _loadingIndicatorTimer.Start();
        SchedulePlaceholder(version, info, isPageChange: !resetTransform);
        var timing = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var comicMode = IsComicMode;
            var currentIdx = CurrentIndex;
            var spread = comicMode ? ComicSpreadHelper.FindSpread(_comicSpreads, currentIdx) : null;
            var firstKnownWide = comicMode && (spread?.IsWideSingle ?? false);

            var decodeTarget = CurrentDecodeTarget();
            var firstDecodeTarget = comicMode && !firstKnownWide && decodeTarget is { } dt
                ? dt with { ViewportPixelWidth = Math.Max(1.0, dt.ViewportPixelWidth / 2.0) }
                : decodeTarget;

            ImageInfo? secondInfo = null;
            int secondIdx = -1;
            if (comicMode && spread is { SecondIndex: { } sIdx } && sIdx < _images.Count)
            {
                secondIdx = sIdx;
                secondInfo = _images[sIdx];
            }

            DecodedImage decoded;
            DecodedImage? secondDecoded = null;
            if (secondInfo is not null)
            {
                var firstTask = _loader.LoadAsync(source, info, pageIndex, firstDecodeTarget, ct);
                var secondTask = LoadOptionalSecondAsync(source, secondInfo, firstDecodeTarget, ct);
                await Task.WhenAll((Task)firstTask, secondTask);
                decoded = await firstTask;
                secondDecoded = await secondTask;
            }
            else
            {
                decoded = await _loader.LoadAsync(source, info, pageIndex, firstDecodeTarget, ct);
            }

            var decodeMs = timing.ElapsedMilliseconds;
            if (version != _loadVersion)
            {
                decoded.Release();
                secondDecoded?.Release();
                return;
            }

            // 코믹 모드에서 실제 디코딩 크기를 확인해 정사각형 · 가로 이미지(2페이지 크기)이면 단독 1장만 표시한다.
            if (comicMode && currentIdx >= 0 && currentIdx < _widePages.Length)
            {
                var firstIsWide = ComicSpreadHelper.IsWideImage(decoded.OriginalWidth, decoded.OriginalHeight);
                var secondIsWide = secondDecoded is not null &&
                                   ComicSpreadHelper.IsWideImage(secondDecoded.OriginalWidth, secondDecoded.OriginalHeight);
                var spreadChanged = false;
                if (_widePages[currentIdx] != firstIsWide)
                {
                    _widePages[currentIdx] = firstIsWide;
                    spreadChanged = true;
                }

                if (secondIdx >= 0 && secondIdx < _widePages.Length && _widePages[secondIdx] != secondIsWide)
                {
                    _widePages[secondIdx] = secondIsWide;
                    spreadChanged = true;
                }

                if (firstIsWide || secondIsWide)
                {
                    secondDecoded?.Release();
                    secondDecoded = null;
                }

                if (spreadChanged)
                {
                    RebuildComicSpreads();
                }
            }

            ReplaceDecoded(decoded);
            ReplaceSecondDecoded(secondDecoded);
            StopAnimation();
            if (resetTransform)
            {
                _transform = ImageTransform.Identity;
            }

            _zoomFactor = 1.0;
            _pendingNewImage = true;
            UpdateImageInfo(info, decoded);
            OnPropertyChanged(nameof(PositionText));
            await RenderCurrentAsync();

            // 표시가 느렸던 경우 원인을 알 수 있도록 단계별 시간을 남긴다 (파일 크기 · 해상도 포함).
            if (timing.ElapsedMilliseconds >= 300)
            {
                _logger.LogInformation(
                    "느린 표시: {Name} ({Size}, {Width}x{Height}{Reduced}) 디코딩(대기 포함) {Decode} ms, 렌더링 {Render} ms, 합계 {Total} ms",
                    info.Name, FileSizeHelper.Format(info.Size), decoded.OriginalWidth, decoded.OriginalHeight,
                    decoded.IsReduced ? $" → {decoded.Width}x{decoded.Height}" : string.Empty,
                    decodeMs, timing.ElapsedMilliseconds - decodeMs, timing.ElapsedMilliseconds);
            }

            if (version == _loadVersion)
            {
                StartPrefetch();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version != _loadVersion)
            {
                return;
            }

            if (ex is ImageLoadException)
            {
                _logger.LogInformation("이미지 표시 실패: {Name} - {Message}", info.Name, ex.Message);
            }
            else
            {
                _logger.LogError(ex, "이미지 표시 중 예외: {Name}", info.Name);
            }

            ShowError(info, ex is ImageLoadException ? ex.Message : "이미지를 표시할 수 없습니다.");
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loadingIndicatorTimer.Stop();
                _placeholderTimer?.Stop();
                IsLoading = false;
            }
        }
    }

    private void ShowError(ImageInfo info, string message)
    {
        StopAnimation();
        IsShowingPlaceholder = false;
        FrameInfoText = null;
        ReplaceDecoded(null);
        UpdateImageInfo(info, null);
        OnPropertyChanged(nameof(PositionText));
        CurrentBitmap = null;
        _bitmapImageKey = null;
        ErrorMessage = $"{info.Name}\n\n{message}";
        Scale = 1.0;

        if (IsSlideShowPlaying)
        {
            // 슬라이드쇼 중에는 오류 이미지를 오래 보여 주지 않는다.
            _slideShow.RestartTimer();
        }
    }

    /// <summary>현재 이미지를 현재 배율·필터로 다시 렌더링한다. 마지막 요청의 결과만 화면에 반영한다.</summary>
    private async Task RenderCurrentAsync()
    {
        var decoded = _decoded;
        if (decoded is null || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            return;
        }

        // 새 이미지를 읽는 동안(썸네일 임시 표시 중 등) 창 크기가 바뀌어도 이전 이미지를 다시 그리지 않는다.
        if (CurrentImage is null || decoded.Info.Key != CurrentImage.Key)
        {
            return;
        }

        var loadVersion = _loadVersion;
        var version = ++_renderVersion;
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        if (IsComicMode)
        {
            await RenderComicSpreadAsync(decoded, _secondDecoded, loadVersion, version, ct);
            return;
        }

        var layout = ComputeLayout(decoded);
        var transform = _transform;
        try
        {
            // 애니메이션: 재생하면서 프레임을 차례로 디코딩한다 (확대 · 축소는 화면에서 처리).
            // 모든 프레임이 메모리 한도 안이면 원본 크기로, 넘으면 화면에 필요한 크기로 줄여 디코딩한다.
            if (decoded is { IsAnimated: true, Animation: { } animation } && PlayAnimations)
            {
                var scale = AnimationScale(decoded, animation, transform, layout.RenderPixelWidth, layout.RenderPixelHeight);
                if (!CanReusePlayback(animation, transform, scale))
                {
                    var playback = new AnimationPlayback(animation, transform, scale);
                    try
                    {
                        await playback.WaitForFrameAsync(0, ct);
                    }
                    catch
                    {
                        playback.Dispose();
                        throw;
                    }

                    if (version != _renderVersion || loadVersion != _loadVersion || !ReferenceEquals(decoded, _decoded))
                    {
                        playback.Dispose();
                        return;
                    }

                    StartAnimation(playback);
                }

                IsShowingPlaceholder = false;
                _bitmapImageKey = decoded.Key;
                ErrorMessage = null;
                ApplyLayout(ComputeLayout(decoded));
                return;
            }

            // 화면 크기로 줄여 읽은 이미지로는 해상도가 부족하면(원본 크기 · 확대) 원본을 백그라운드에서 읽는다.
            RequestFullResolutionIfNeeded(decoded, layout, transform);

            var bitmap = await _renderer.RenderAsync(decoded, layout.RenderPixelWidth, layout.RenderPixelHeight, Filter, transform, ct);
            if (version != _renderVersion || loadVersion != _loadVersion || !ReferenceEquals(decoded, _decoded))
            {
                return;
            }

            // 렌더링 중에 배율이 바뀌었으면 최신 크기를 적용하고 다시 렌더링을 예약한다.
            var latest = ComputeLayout(decoded);
            StopAnimation();
            CurrentBitmap = bitmap;
            IsShowingPlaceholder = false;
            FrameInfoText = BuildStaticFrameInfo(decoded);
            _bitmapImageKey = decoded.Key;
            ErrorMessage = null;
            ApplyLayout(latest);
            if (latest.RenderPixelWidth != layout.RenderPixelWidth || latest.RenderPixelHeight != layout.RenderPixelHeight)
            {
                ScheduleRender();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // 캐시에서 해제된 경우 다시 읽는다.
            if (loadVersion == _loadVersion && ReferenceEquals(decoded, _decoded))
            {
                ReplaceDecoded(null);
                _ = LoadCurrentAsync();
            }
        }
        catch (Exception ex)
        {
            if (version == _renderVersion && CurrentImage is { } info)
            {
                _logger.LogError(ex, "렌더링 실패: {Name}", info.Name);
                ShowError(info, "이미지를 표시용으로 변환하지 못했습니다.");
            }
        }
    }

    /// <summary>회전 · 반전을 적용한 화면 기준 원본 이미지 크기 (축소 디코딩과 관계없이 원본 기준).</summary>
    private (uint Width, uint Height) DisplayedSize(DecodedImage decoded) =>
        _transform.Apply(decoded.OriginalWidth, decoded.OriginalHeight);

    private DisplayLayout ComputeLayout(DecodedImage decoded)
    {
        var (width, height) = DisplayedSize(decoded);
        return ImageResizeService.Calculate(
            DisplayMode,
            _zoomFactor,
            width,
            height,
            _viewportWidth * _dpiScale,
            _viewportHeight * _dpiScale);
    }

    private void ApplyLayout(DisplayLayout layout)
    {
        DisplayWidth = layout.DisplayPixelWidth / _dpiScale;
        DisplayHeight = layout.DisplayPixelHeight / _dpiScale;
        Scale = layout.Scale;

        var isNew = _pendingNewImage;
        _pendingNewImage = false;
        LayoutChanged?.Invoke(this, new LayoutChangedEventArgs(isNew));
    }

    /// <summary>
    /// 배율이나 표시 영역이 바뀌면 현재 비트맵을 즉시 늘리거나 줄여 보여 주고,
    /// 정확한 크기의 렌더링은 잠시 뒤에 수행한다.
    /// </summary>
    private void UpdateLayoutNow(bool renderImmediately)
    {
        var decoded = _decoded;
        if (decoded is null)
        {
            return;
        }

        if (IsComicMode)
        {
            if (_bitmapImageKey == decoded.Key && !_pendingNewImage)
            {
                var (firstLayout, secondLayout) = ComputeComicLayouts(decoded, _secondDecoded);
                var spread = ComicSpreadHelper.FindSpread(_comicSpreads, CurrentIndex);
                var isWideSingle = IsCurrentWideSingle(decoded, _secondDecoded);
                var trailingBlank = !isWideSingle && _secondDecoded is null && (spread?.TrailingBlank ?? false);
                AssignComicLayoutSizes(firstLayout, secondLayout, isWideSingle, trailingBlank);
                ApplyComicDimensions(firstLayout.Scale);
            }
        }
        else if (_bitmapImageKey == decoded.Key && !_pendingNewImage)
        {
            ApplyLayout(ComputeLayout(decoded));
        }

        if (renderImmediately)
        {
            _renderDebounce.Stop();
            _ = RenderCurrentAsync();
        }
        else
        {
            ScheduleRender();
        }
    }

    private void ScheduleRender()
    {
        _renderDebounce.Stop();
        _renderDebounce.Start();
    }

    /// <summary>View 의 표시 영역 크기(DIP)와 DPI 배율을 전달받는다.</summary>
    public void UpdateViewport(double width, double height, double dpiScale)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var first = _viewportWidth <= 0;
        if (Math.Abs(width - _viewportWidth) < 0.5 && Math.Abs(height - _viewportHeight) < 0.5 &&
            Math.Abs(dpiScale - _dpiScale) < 0.001)
        {
            return;
        }

        _viewportWidth = width;
        _viewportHeight = height;
        _dpiScale = dpiScale <= 0 ? 1.0 : dpiScale;
        UpdateLayoutNow(renderImmediately: first);
    }

    private void ReplaceDecoded(DecodedImage? decoded)
    {
        var old = _decoded;
        _decoded = decoded;
        old?.Release();
    }

    private void ReplaceSecondDecoded(DecodedImage? decoded)
    {
        var old = _secondDecoded;
        _secondDecoded = decoded;
        old?.Release();
    }

    private void ClearComicPages()
    {
        LeftPageBitmap = null;
        RightPageBitmap = null;
        LeftPageWidth = 0;
        LeftPageHeight = 0;
        RightPageWidth = 0;
        RightPageHeight = 0;
        HasLeftPageSlot = false;
        HasRightPageSlot = false;
    }

    private async Task<DecodedImage?> LoadOptionalSecondAsync(
        IImageSource source,
        ImageInfo info,
        DecodeTarget? decodeTarget,
        CancellationToken ct)
    {
        try
        {
            return await _loader.LoadAsync(source, info, 0, decodeTarget, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "코믹 뷰어 두 번째 페이지 로드 실패: {Name}", info.Name);
            return null;
        }
    }

    private static bool IsCurrentWideSingle(DecodedImage first, DecodedImage? second) =>
        second is null && ComicSpreadHelper.IsWideImage(first.OriginalWidth, first.OriginalHeight);

    private (DisplayLayout First, DisplayLayout? Second) ComputeComicLayouts(DecodedImage first, DecodedImage? second)
    {
        var fullViewportW = Math.Max(1.0, _viewportWidth * _dpiScale);
        var halfViewportW = Math.Max(1.0, fullViewportW / 2.0);
        var viewportH = Math.Max(1.0, _viewportHeight * _dpiScale);

        var (fw, fh) = DisplayedSize(first);
        var firstTargetW = IsCurrentWideSingle(first, second) ? fullViewportW : halfViewportW;
        var firstLayout = ImageResizeService.Calculate(DisplayMode, _zoomFactor, fw, fh, firstTargetW, viewportH);

        DisplayLayout? secondLayout = null;
        if (second is not null)
        {
            var (sw, sh) = DisplayedSize(second);
            secondLayout = ImageResizeService.Calculate(DisplayMode, _zoomFactor, sw, sh, halfViewportW, viewportH);
        }

        return (firstLayout, secondLayout);
    }

    private void AssignComicLayoutSizes(
        DisplayLayout firstLayout,
        DisplayLayout? secondLayout,
        bool isWideSingle = false,
        bool trailingBlank = false)
    {
        var fw = firstLayout.DisplayPixelWidth / _dpiScale;
        var fh = firstLayout.DisplayPixelHeight / _dpiScale;
        var sw = secondLayout is { } sl ? sl.DisplayPixelWidth / _dpiScale : 0;
        var sh = secondLayout is { } sl2 ? sl2.DisplayPixelHeight / _dpiScale : 0;

        if (isWideSingle)
        {
            LeftPageWidth = fw;
            LeftPageHeight = fh;
            RightPageWidth = 0;
            RightPageHeight = 0;
            HasLeftPageSlot = true;
            HasRightPageSlot = false;
            return;
        }

        // 논리적 Slot1(앞), Slot2(뒤): 수동 빈 페이지가 있으면 실물 페이지와 같은 크기로 자리를 확보한다.
        var slot1W = fw;
        var slot1H = fh;
        var hasSlot1 = true;
        var slot2W = trailingBlank ? fw : sw;
        var slot2H = trailingBlank ? fh : sh;
        var hasSlot2 = trailingBlank || secondLayout is not null;

        if (ComicDirection == ComicReadingDirection.RightToLeft)
        {
            LeftPageWidth = slot2W;
            LeftPageHeight = slot2H;
            HasLeftPageSlot = hasSlot2;
            RightPageWidth = slot1W;
            RightPageHeight = slot1H;
            HasRightPageSlot = hasSlot1;
        }
        else
        {
            LeftPageWidth = slot1W;
            LeftPageHeight = slot1H;
            HasLeftPageSlot = hasSlot1;
            RightPageWidth = slot2W;
            RightPageHeight = slot2H;
            HasRightPageSlot = hasSlot2;
        }
    }

    private void ApplyComicDimensions(double? scale = null)
    {
        DisplayWidth = LeftPageWidth + RightPageWidth;
        DisplayHeight = Math.Max(LeftPageHeight, RightPageHeight);
        if (scale is { } s)
        {
            Scale = s;
        }

        var isNew = _pendingNewImage;
        _pendingNewImage = false;
        LayoutChanged?.Invoke(this, new LayoutChangedEventArgs(isNew));
    }

    private async Task RenderComicSpreadAsync(
        DecodedImage first,
        DecodedImage? second,
        int loadVersion,
        int version,
        CancellationToken ct)
    {
        var (firstLayout, secondLayout) = ComputeComicLayouts(first, second);
        var transform = _transform;

        try
        {
            var firstTask = _renderer.RenderAsync(
                first, firstLayout.RenderPixelWidth, firstLayout.RenderPixelHeight, Filter, transform, ct);
            Task<Bitmap?> secondTask = second is not null && secondLayout is { } sl
                ? RenderOptionalSecondAsync(second, sl, transform, ct)
                : Task.FromResult<Bitmap?>(null);

            await Task.WhenAll((Task)firstTask, secondTask);
            var firstBmp = await firstTask;
            var secondBmp = await secondTask;

            if (!IsComicMode ||
                version != _renderVersion ||
                loadVersion != _loadVersion ||
                !ReferenceEquals(first, _decoded) ||
                CurrentImage is null ||
                first.Info.Key != CurrentImage.Key)
            {
                return;
            }

            var (latestFirst, latestSecond) = ComputeComicLayouts(first, second);
            var spread = ComicSpreadHelper.FindSpread(_comicSpreads, CurrentIndex);
            var isWideSingle = IsCurrentWideSingle(first, second);
            var trailingBlank = !isWideSingle && second is null && (spread?.TrailingBlank ?? false);
            StopAnimation();
            CurrentBitmap = firstBmp;

            if (isWideSingle)
            {
                LeftPageBitmap = firstBmp;
                RightPageBitmap = null;
            }
            else
            {
                var slot2Bmp = trailingBlank ? null : secondBmp;
                if (ComicDirection == ComicReadingDirection.RightToLeft)
                {
                    LeftPageBitmap = slot2Bmp;
                    RightPageBitmap = firstBmp;
                }
                else
                {
                    LeftPageBitmap = firstBmp;
                    RightPageBitmap = slot2Bmp;
                }
            }

            IsShowingPlaceholder = false;
            FrameInfoText = null;
            _bitmapImageKey = first.Key;
            ErrorMessage = null;
            AssignComicLayoutSizes(latestFirst, latestSecond, isWideSingle, trailingBlank);
            ApplyComicDimensions(latestFirst.Scale);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            if (loadVersion == _loadVersion && ReferenceEquals(first, _decoded))
            {
                ReplaceDecoded(null);
                ReplaceSecondDecoded(null);
                _ = LoadCurrentAsync();
            }
        }
        catch (Exception ex)
        {
            if (version == _renderVersion && CurrentImage is { } info)
            {
                _logger.LogError(ex, "코믹 렌더링 실패: {Name}", info.Name);
                ShowError(info, "이미지를 표시용으로 변환하지 못했습니다.");
            }
        }
    }

    private async Task<Bitmap?> RenderOptionalSecondAsync(
        DecodedImage second,
        DisplayLayout layout,
        ImageTransform transform,
        CancellationToken ct)
    {
        try
        {
            return await _renderer.RenderAsync(second, layout.RenderPixelWidth, layout.RenderPixelHeight, Filter, transform, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "코믹 뷰어 두 번째 페이지 렌더링 실패: {Name}", second.Info.Name);
            return null;
        }
    }

    private void RebuildComicSpreads()
    {
        _comicSpreads = ComicSpreadHelper.BuildSpreads(_images.Count, _widePages, _manualComicBlanks);
        RebuildComicThumbnails();
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ComicThumbnailSelectedIndex));
        OnPropertyChanged(nameof(HasManualComicBlanks));
        OnPropertyChanged(nameof(IsCurrentSpreadManualBlank));
        OnPropertyChanged(nameof(ComicBlankButtonText));
    }

    private void RebuildComicThumbnails()
    {
        foreach (var old in ComicThumbnailItems)
        {
            old.Detach();
        }

        if (_comicSpreads.Count == 0 && ThumbnailItems.Count > 0)
        {
            _comicSpreads = ComicSpreadHelper.BuildSpreads(ThumbnailItems.Count, _widePages, _manualComicBlanks);
        }

        var pairs = new List<ComicThumbnailItemViewModel>(_comicSpreads.Count);
        var currentSpread = ComicSpreadHelper.FindSpreadIndex(_comicSpreads, CurrentIndex);
        foreach (var spread in _comicSpreads)
        {
            if (spread.FirstIndex >= ThumbnailItems.Count)
            {
                continue;
            }

            var first = ThumbnailItems[spread.FirstIndex];
            var second = spread.SecondIndex is { } s && s < ThumbnailItems.Count ? ThumbnailItems[s] : null;
            pairs.Add(new ComicThumbnailItemViewModel(
                spread.SpreadIndex,
                first,
                second,
                ComicDirection,
                spread.IsWideSingle,
                spread.TrailingBlank)
            {
                IsCurrent = spread.SpreadIndex == currentSpread,
            });
        }

        ComicThumbnailItems = new ObservableCollection<ComicThumbnailItemViewModel>(pairs);
        OnPropertyChanged(nameof(ComicThumbnailSelectedIndex));
    }

    private void StartComicPageProbe()
    {
        var source = _source;
        var images = _images;
        if (source is null || images.Count == 0)
        {
            return;
        }

        _comicProbeCts?.Cancel();
        _comicProbeCts = new CancellationTokenSource();
        var ct = _comicProbeCts.Token;

        _ = Task.Run(async () =>
        {
            var results = new bool[images.Count];
            try
            {
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = source.Info.SourceType == ImageSourceType.File ? 4 : 1,
                    CancellationToken = ct,
                };
                await Parallel.ForEachAsync(Enumerable.Range(0, images.Count), options, async (idx, token) =>
                {
                    results[idx] = await ComicSpreadHelper.ProbeIsWideAsync(source, images[idx], token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "코믹 뷰어 이미지 크기 사전 확인 실패");
                return;
            }

            _dispatcher.Post(() =>
            {
                if (ct.IsCancellationRequested || !ReferenceEquals(source, _source) || !ReferenceEquals(images, _images))
                {
                    return;
                }

                _comicPagesProbed = true;
                var changed = false;
                for (var i = 0; i < results.Length && i < _widePages.Length; i++)
                {
                    if (_widePages[i] != results[i])
                    {
                        _widePages[i] = results[i];
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return;
                }

                var oldSpread = ComicSpreadHelper.FindSpread(_comicSpreads, CurrentIndex);
                RebuildComicSpreads();
                if (!IsComicMode || CurrentIndex < 0)
                {
                    return;
                }

                var newSpread = ComicSpreadHelper.FindSpread(_comicSpreads, CurrentIndex);
                if (newSpread is not null &&
                    (oldSpread?.FirstIndex != newSpread.FirstIndex ||
                     oldSpread?.SecondIndex != newSpread.SecondIndex ||
                     oldSpread?.IsWideSingle != newSpread.IsWideSingle ||
                     oldSpread?.TrailingBlank != newSpread.TrailingBlank))
                {
                    _navigation.MoveTo(newSpread.FirstIndex);
                    SetCurrent(newSpread.FirstIndex);
                    _ = LoadCurrentAsync();
                }
            }, DispatcherPriority.Background);
        }, ct);
    }

    private void CancelPending()
    {
        StopAnimation();
        _placeholderTimer?.Stop();
        IsShowingPlaceholder = false;
        _loadVersion++;
        _renderVersion++;
        _loadCts?.Cancel();
        _renderCts?.Cancel();
        _prefetchCts?.Cancel();
        _renderDebounce.Stop();
        _loadingIndicatorTimer.Stop();
        IsLoading = false;
    }

    #endregion

    #region 썸네일

    private void RequestThumbnail(ThumbnailItemViewModel item)
    {
        var source = _source;
        if (source is null)
        {
            return;
        }

        var pixelSize = (uint)Math.Clamp(Math.Round(Settings.Thumbnail.Size * _dpiScale), 32, 512);
        _thumbnails.Request(source, item.Info, pixelSize, (bitmap, completed) =>
        {
            _dispatcher.Post(() =>
            {
                if (!ReferenceEquals(source, _source))
                {
                    return;
                }

                if (!completed)
                {
                    item.ResetRequest();
                    return;
                }

                item.SetThumbnail(bitmap);
                if (bitmap is null)
                {
                    return;
                }

                _loadedThumbnails.AddLast(item);
                while (_loadedThumbnails.Count > MaxLoadedThumbnails && _loadedThumbnails.First is { } oldest)
                {
                    _loadedThumbnails.RemoveFirst();
                    oldest.Value.ReleaseThumbnail();
                }
            }, DispatcherPriority.Background);
        });
    }

    #endregion

    #region 표시 모드 · 확대 · 필터

    [RelayCommand]
    private void SetDisplayMode(DisplayMode mode)
    {
        if (DisplayMode == mode)
        {
            // 같은 모드를 다시 선택하면 확대 배율만 초기화한다.
            _zoomFactor = 1.0;
            ShowOsd(GetModeName(mode));
            UpdateLayoutNow(renderImmediately: true);
            return;
        }

        DisplayMode = mode;
    }

    [RelayCommand]
    private void SetFilter(ImageFilter filter) => Filter = filter;

    [RelayCommand]
    private void ZoomIn() => Zoom(true);

    [RelayCommand]
    private void ZoomOut() => Zoom(false);

    private void Zoom(bool zoomIn)
    {
        var decoded = _decoded;
        if (decoded is null)
        {
            return;
        }

        var (displayedWidth, displayedHeight) = DisplayedSize(decoded);
        var baseScale = ImageResizeService.GetBaseScale(
            DisplayMode, displayedWidth, displayedHeight,
            _viewportWidth * _dpiScale, _viewportHeight * _dpiScale);
        _zoomFactor = ImageResizeService.StepZoom(baseScale, _zoomFactor, zoomIn);
        UpdateLayoutNow(renderImmediately: false);
        ShowOsd(ScaleText);
    }

    [RelayCommand]
    private void ResetZoom()
    {
        _zoomFactor = 1.0;
        UpdateLayoutNow(renderImmediately: true);
        ShowOsd(ScaleText);
    }

    private static string GetModeName(DisplayMode mode) => mode switch
    {
        DisplayMode.Original => "원본 크기",
        DisplayMode.FitWidth => "폭 맞춤",
        DisplayMode.FitHeight => "높이 맞춤",
        DisplayMode.Fill => "꽉 차게 보기",
        _ => mode.ToString(),
    };

    #endregion

    #region 슬라이드쇼

    [RelayCommand]
    private void ToggleSlideShow()
    {
        if (IsSlideShowPlaying)
        {
            StopSlideShow(showMessage: true);
        }
        else
        {
            StartSlideShow();
        }
    }

    [RelayCommand]
    private void SetSlideShowMode(SlideShowMode mode) => SlideShowMode = mode;

    private void StartSlideShow()
    {
        if (_images.Count == 0)
        {
            ShowOsd("표시할 이미지가 없습니다.");
            return;
        }

        _slideShow.Start();
        IsSlideShowPlaying = true;
        ShowOsd($"슬라이드쇼 재생 ({SlideShowInterval}초, {(SlideShowMode == SlideShowMode.Random ? "랜덤" : "순차")})");

        // 재생 순서(랜덤이면 섞은 목록)를 바로 만들고 첫 번째 다음 이미지와 좌우 이미지를 미리 읽는다.
        StartPrefetch();
    }

    private void StopSlideShow(bool showMessage)
    {
        if (!IsSlideShowPlaying)
        {
            return;
        }

        _slideShow.Stop();
        IsSlideShowPlaying = false;
        if (showMessage)
        {
            ShowOsd("슬라이드쇼 일시정지");
        }
    }

    private void OnSlideShowTick(object? sender, EventArgs e)
    {
        if (_dialogs.IsModalOpen)
        {
            // 대화 상자(설정 · 이미지 목록 · 삭제 확인 등)를 보는 동안에는 넘기지 않는다.
            _slideShow.RestartTimer();
            return;
        }

        var next = _slideShow.GetNextIndex(CurrentIndex, _images.Count);
        if (next is null)
        {
            StopSlideShow(showMessage: false);
            ShowOsd("슬라이드쇼가 끝났습니다.");
            return;
        }

        _ = NavigateAsync(next.Value, +1, null, fromSlideShow: true);
    }

    #endregion

    #region 창 · 대화 상자

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    /// <summary>
    /// ` 키: 상단 · 하단 바의 고정(Pin)을 한꺼번에 전환한다.
    /// 하나라도 고정되어 있으면 모두 해제(숨김)하고, 모두 해제되어 있으면 모두 고정(표시)한다.
    /// </summary>
    [RelayCommand]
    private void ToggleMenus()
    {
        var pin = !(TopBarPinned || BottomBarPinned);
        TopBarPinned = pin;
        BottomBarPinned = pin;
        ShowOsd(pin
            ? "상단 · 하단 바 고정"
            : "상단 · 하단 바 고정 해제 (마우스를 위 / 아래 가장자리에 올리면 표시)");
    }

    [RelayCommand]
    private void ToggleTopBarPin() => TopBarPinned = !TopBarPinned;

    [RelayCommand]
    private void ToggleBottomBarPin() => BottomBarPinned = !BottomBarPinned;

    [RelayCommand]
    private void ToggleThumbnails() => ThumbnailsVisible = !ThumbnailsVisible;

    [RelayCommand]
    private void ToggleComicMode() => IsComicMode = !IsComicMode;

    [RelayCommand]
    private void ToggleComicDirection() => ComicDirection =
        ComicDirection == ComicReadingDirection.LeftToRight
            ? ComicReadingDirection.RightToLeft
            : ComicReadingDirection.LeftToRight;

    [RelayCommand]
    private void SetComicDirection(ComicReadingDirection direction) => ComicDirection = direction;

    [RelayCommand]
    private void ToggleComicBlankPage()
    {
        if (!IsComicMode)
        {
            ShowOsd("코믹 뷰어에서만 빈 페이지를 추가할 수 있습니다.");
            return;
        }

        if (_images.Count == 0 || CurrentIndex < 0)
        {
            return;
        }

        var spread = ComicSpreadHelper.FindSpread(_comicSpreads, CurrentIndex);
        if (spread is null)
        {
            return;
        }

        if (spread.IsWideSingle)
        {
            ShowOsd("양면 이미지에는 빈 페이지를 넣을 수 없습니다.");
            return;
        }

        if (_manualComicBlanks.Remove(spread.FirstIndex))
        {
            RebuildComicSpreads();
            _navigation.MoveTo(spread.FirstIndex);
            SetCurrent(spread.FirstIndex);
            ShowOsd($"{spread.FirstIndex + 1}p 뒤 빈 페이지 제거");
            _ = LoadCurrentAsync();
            return;
        }

        if (spread.SecondIndex is null)
        {
            ShowOsd("2페이지가 함께 표시된 화면에서만 빈 페이지를 넣을 수 있습니다.");
            return;
        }

        _manualComicBlanks.Add(spread.FirstIndex);
        RebuildComicSpreads();
        _navigation.MoveTo(spread.FirstIndex);
        SetCurrent(spread.FirstIndex);
        ShowOsd($"{spread.FirstIndex + 1}p 뒤에 빈 페이지 삽입");
        _ = LoadCurrentAsync();
    }

    [RelayCommand]
    private void ClearComicBlankPages()
    {
        if (_manualComicBlanks.Count == 0)
        {
            ShowOsd("초기화할 빈 페이지가 없습니다.");
            return;
        }

        _manualComicBlanks.Clear();
        RebuildComicSpreads();
        if (IsComicMode && CurrentIndex >= 0)
        {
            var start = ComicSpreadHelper.GetSpreadStart(_comicSpreads, CurrentIndex);
            if (start >= 0)
            {
                _navigation.MoveTo(start);
                SetCurrent(start);
                _ = LoadCurrentAsync();
            }
        }

        ShowOsd("수동 빈 페이지 모두 초기화");
    }

    private bool _isImageListOpen;
    private long _lastImageListClosedTick;

    [RelayCommand]
    private async Task ShowImageListAsync()
    {
        if (_isImageListOpen || Environment.TickCount64 - _lastImageListClosedTick < 300)
        {
            return;
        }

        if (_images.Count == 0)
        {
            ShowOsd("표시할 이미지가 없습니다.");
            return;
        }

        var snapshot = _images;
        var vm = new ImageListViewModel(
            snapshot,
            CurrentIndex,
            SourceInfo,
            ThumbnailItems,
            isComicMode: IsComicMode,
            comicDirection: ComicDirection,
            comicThumbnails: ComicThumbnailItems,
            comicSpreads: _comicSpreads);

        bool accepted;
        _isImageListOpen = true;
        try
        {
            accepted = _dialogs.ShowImageList(vm);
        }
        finally
        {
            _isImageListOpen = false;
            _lastImageListClosedTick = Environment.TickCount64;
        }

        if (accepted && vm.ResultIndex is { } index && index >= 0 && index < snapshot.Count)
        {
            // 대화 상자를 보는 동안 폴더 변경으로 목록이 바뀌었을 수 있으므로 번호가 아니라 이미지로 찾는다.
            var key = snapshot[index].Key;
            var current = FindIndexByKey(key);
            if (current >= 0)
            {
                await GoToAsync(current);
            }
            else
            {
                ShowOsd("선택한 이미지가 목록에서 사라졌습니다.");
            }
        }
    }

    private int FindIndexByKey(string key)
    {
        for (var i = 0; i < _images.Count; i++)
        {
            if (_images[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        var vm = new SettingsViewModel(Settings, _shell, _dialogs);
        if (!_dialogs.ShowSettings(vm))
        {
            return;
        }

        var thumbSizeChanged = vm.ThumbnailSize != Settings.Thumbnail.Size;
        var limitsChanged = vm.MaxImageMegapixels != Settings.Limits.MaxImageMegapixels;
        vm.ApplyTo(Settings);
        _settingsService.Save();
        ApplySettingsToServices();
        RefreshWatcher();

        OnPropertyChanged(nameof(SlideShowRepeat));
        OnPropertyChanged(nameof(ThumbnailsVisible));
        OnPropertyChanged(nameof(ShowThumbnailStrip));
        OnPropertyChanged(nameof(ShowSingleThumbnails));
        OnPropertyChanged(nameof(ShowComicThumbnails));
        OnPropertyChanged(nameof(ThumbnailSize));
        OnPropertyChanged(nameof(ComicThumbnailWidth));
        OnPropertyChanged(nameof(ComicThumbnailWideWidth));
        OnPropertyChanged(nameof(ThumbnailScrollBarVisibility));
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(MouseWheelAction));

        _renderer.ClearCache();
        if (limitsChanged && _decoded is null && CurrentImage is not null)
        {
            _ = LoadCurrentAsync();
        }
        else
        {
            _zoomFactor = 1.0;
            UpdateLayoutNow(renderImmediately: true);
        }

        if (thumbSizeChanged && _source is not null)
        {
            _thumbnails.CancelAll();
            _loadedThumbnails.Clear();
            ThumbnailItems = new ObservableCollection<ThumbnailItemViewModel>(
                _images.Select((info, i) => new ThumbnailItemViewModel(i, info, RequestThumbnail) { IsCurrent = i == CurrentIndex }));
            RebuildComicThumbnails();
        }
    }

    [RelayCommand]
    private void Exit() =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close();

    private void ApplySettingsToServices()
    {
        _navigation.WrapAround = Settings.Navigation.WrapAround;
        _slideShow.IntervalSeconds = Settings.Slideshow.IntervalSeconds;
        _slideShow.Mode = Settings.Slideshow.Mode;
        _slideShow.Repeat = Settings.Slideshow.Repeat;
    }

    public void ShowOsd(string message)
    {
        OsdMessage = message;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    #endregion

    /// <summary>종료 시 설정을 저장한다.</summary>
    public void SaveSettings() => _settingsService.Save();

    /// <summary>자동 업데이트 (메인 창 오른쪽 위 알림, 정보 창). 테스트에서는 없을 수 있다.</summary>
    public UpdateViewModel? Update { get; }

    /// <summary>업데이트 뒤 다시 열 경로: 폴더의 이미지는 그 파일, ZIP 은 ZIP 파일.</summary>
    public string? RelaunchPath =>
        CurrentImage is { SourceType: ImageSourceType.File, FullPath: { } file } && File.Exists(file)
            ? file
            : SourceInfo?.Path;

    public void Dispose()
    {
        DetachWatcher(_source);
        _comicProbeCts?.Cancel();
        CancelPending();
        _slideShow.Dispose();
        _thumbnails.CancelAll();
        foreach (var item in ComicThumbnailItems)
        {
            item.Detach();
        }

        ReplaceDecoded(null);
        ReplaceSecondDecoded(null);
        _loader.Dispose();
        _renderer.ClearCache();
        _source?.Dispose();
        _source = null;
    }
}
