using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageViewer.Helpers;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;
using Microsoft.Extensions.Logging;

namespace ImageViewer.ViewModels;

/// <summary>
/// 회전 · 반전, 다중 페이지 탐색, 애니메이션 재생, 이미지 정보 패널, 폴더 감시.
/// </summary>
public sealed partial class MainViewModel
{
    private ImageTransform _transform = ImageTransform.Identity;
    private AnimationPlayback? _playback;
    private readonly System.Diagnostics.Stopwatch _animationClock = new();
    private double _nextFrameDueMs;
    private int _animationIndex;
    private DispatcherTimer? _animationTimer;
    private DispatcherTimer? _watchDebounce;
    private IImageSource? _watchedSource;
    private int _refreshVersion;

    #region 회전 · 반전

    [RelayCommand]
    private void RotateClockwise() => ApplyTransform(_transform.RotateClockwise());

    [RelayCommand]
    private void RotateCounterClockwise() => ApplyTransform(_transform.RotateCounterClockwise());

    [RelayCommand]
    private void FlipHorizontal() => ApplyTransform(_transform.FlipHorizontally());

    [RelayCommand]
    private void FlipVertical() => ApplyTransform(_transform.FlipVertically());

    [RelayCommand]
    private void ResetTransform() => ApplyTransform(ImageTransform.Identity);

    private void ApplyTransform(ImageTransform transform)
    {
        if (_decoded is null)
        {
            return;
        }

        _transform = transform;
        _zoomFactor = 1.0;

        // 폭과 높이가 바뀔 수 있으므로 이전 비트맵을 늘려 보여 주지 않고 새로 렌더링한 뒤 가운데에 다시 배치한다.
        _bitmapImageKey = null;
        _pendingNewImage = true;
        _renderDebounce.Stop();
        _ = RenderCurrentAsync();

        if (CurrentImage is { } info)
        {
            UpdateImageInfo(info, _decoded);
        }

        ShowOsd(transform.Describe());
    }

    #endregion

    #region 다중 페이지

    /// <summary>PageUp: 다중 페이지 이미지면 이전 페이지, 아니면 이전 이미지.</summary>
    [RelayCommand]
    private Task PreviousPageAsync()
    {
        if (_decoded is { PageCount: > 1 } d)
        {
            return d.PageIndex > 0 ? GoToPageAsync(d.PageIndex - 1) : ShowEdge("첫 페이지입니다.");
        }

        return PreviousCommand.ExecuteAsync(null);
    }

    /// <summary>PageDown: 다중 페이지 이미지면 다음 페이지, 아니면 다음 이미지.</summary>
    [RelayCommand]
    private Task NextPageAsync()
    {
        if (_decoded is { PageCount: > 1 } d)
        {
            return d.PageIndex < d.PageCount - 1 ? GoToPageAsync(d.PageIndex + 1) : ShowEdge("마지막 페이지입니다.");
        }

        return NextCommand.ExecuteAsync(null);
    }

    private Task GoToPageAsync(int page)
    {
        _slideShow.RestartTimer();
        return LoadCurrentAsync(page, resetTransform: false);
    }

    private Task ShowEdge(string message)
    {
        ShowOsd(message);
        return Task.CompletedTask;
    }

    #endregion

    #region 애니메이션

    /// <summary>GIF / WebP 애니메이션 재생 여부 (끄면 첫 프레임만 표시).</summary>
    public bool PlayAnimations
    {
        get => Settings.Display.PlayAnimations;
        set
        {
            if (Settings.Display.PlayAnimations == value)
            {
                return;
            }

            Settings.Display.PlayAnimations = value;
            OnPropertyChanged();
            if (_decoded is { IsAnimated: true })
            {
                _bitmapImageKey = null;
                _ = RenderCurrentAsync();
            }

            ShowOsd(value ? "애니메이션 재생" : "애니메이션 정지 (첫 프레임 표시)");
        }
    }

    [RelayCommand]
    private void TogglePlayAnimations() => PlayAnimations = !PlayAnimations;

    /// <summary>
    /// 애니메이션을 표시할 디코딩 배율. 모든 프레임을 원본 크기로 보관할 수 있으면 원본(1),
    /// 아니면 화면 표시에 필요한 크기와 메모리 한도에 맞는 크기 중 큰 쪽으로 줄인다.
    /// </summary>
    internal static double AnimationScale(DecodedImage decoded, AnimationSource animation, ImageTransform transform, uint renderPixelWidth, uint renderPixelHeight)
    {
        var budgetScale = Math.Sqrt(AnimationPlayback.DefaultBudgetBytes /
                                    ((double)animation.Width * animation.Height * 4 * animation.FrameCount));
        if (budgetScale >= 1)
        {
            return 1;
        }

        var (width, height) = transform.Apply(decoded.OriginalWidth, decoded.OriginalHeight);
        var displayScale = Math.Max((double)renderPixelWidth / Math.Max(1u, width), (double)renderPixelHeight / Math.Max(1u, height));
        return Math.Min(1, Math.Max(budgetScale, displayScale));
    }

    /// <summary>지금 재생 중인 애니메이션을 이 조건에서 그대로 쓸 수 있는지 (해상도가 모자라면 다시 만든다).</summary>
    private bool CanReusePlayback(AnimationSource animation, ImageTransform transform, double scale) =>
        _playback is { } p && ReferenceEquals(p.Source, animation) && p.Transform == transform &&
        (p.Scale >= 0.999 || p.Scale + 0.02 >= scale);

    private void StartAnimation(AnimationPlayback playback)
    {
        // 같은 재생이면(창 크기 변경 · 확대 등) 재생 위치를 유지한다.
        if (ReferenceEquals(playback, _playback))
        {
            return;
        }

        StopAnimation();
        _playback = playback;
        _animationIndex = 0;
        _animationDelaysMs = playback.DelaysMs.ToArray();
        CurrentBitmap = playback.TryGetFrame(0);
        UpdateAnimationInfo();

        if (_animationTimer is null)
        {
            _animationTimer = new DispatcherTimer(DispatcherPriority.Render);
            _animationTimer.Tick += OnAnimationTick;
        }

        _animationClock.Restart();
        _nextFrameDueMs = _animationDelaysMs[0];
        _animationTimer.Interval = TimeSpan.FromMilliseconds(_animationDelaysMs[0]);
        _animationTimer.Start();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_playback is not { } playback || _animationTimer is null)
        {
            return;
        }

        var next = (_animationIndex + 1) % playback.FrameCount;
        if (playback.TryGetFrame(next) is not { } frame)
        {
            // 다음 프레임을 아직 디코딩 중: 현재 프레임을 유지하고 곧 다시 확인한다.
            _animationTimer.Interval = TimeSpan.FromMilliseconds(10);
            return;
        }

        _animationIndex = next;
        playback.SetPosition(next);
        CurrentBitmap = frame;
        UpdateAnimationInfo();

        // 타이머는 매번 조금씩 늦게 울리므로(약 15ms 단위) 실제 경과 시간 기준으로 다음 프레임 시각을 잡아
        // 늦어진 만큼 다음 간격을 줄인다. 디코딩을 기다리느라 크게 밀렸으면 지금부터 다시 센다.
        var now = _animationClock.Elapsed.TotalMilliseconds;
        _nextFrameDueMs = now - _nextFrameDueMs > 250 ? now + _animationDelaysMs[next] : _nextFrameDueMs + _animationDelaysMs[next];
        _animationTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _nextFrameDueMs - now));
    }

    private int[] _animationDelaysMs = [];

    /// <summary>
    /// 왼쪽 아래 반투명 표시. 프레임이 하나뿐인 이미지는 null (표시 안 함).
    /// - 애니메이션: 프레임 n / m · 재생 상태 / 재생 위치 · 전체 길이 · 현재 프레임 지연 / 크기 · 형식
    /// - 다중 페이지: 페이지 n / m / 크기 · 형식
    /// </summary>
    [ObservableProperty]
    private string? _frameInfoText;

    private void UpdateAnimationInfo()
    {
        if (_decoded is { } decoded)
        {
            FrameInfoText = FormatAnimationInfo(_animationIndex, _animationDelaysMs, decoded.Width, decoded.Height, decoded.Format, playing: true);
        }
    }

    internal static string FormatAnimationInfo(
        int index,
        IReadOnlyList<int> delaysMs,
        uint width,
        uint height,
        string format,
        bool playing)
    {
        var count = delaysMs.Count;
        var elapsedMs = delaysMs.Take(index).Sum();
        var totalMs = delaysMs.Sum();
        var delay = index < count ? delaysMs[index] : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"프레임 {index + 1} / {count} \n" +
            $"{elapsedMs / 1000.0:0.00} / {totalMs / 1000.0:0.00}초  ·  지연 {delay}ms\n" +
            $"{width:N0} × {height:N0}  ·  {format}");
    }

    /// <summary>정지 이미지로 렌더링했을 때의 표시 (재생을 끈 애니메이션 · 다중 페이지).</summary>
    internal static string? BuildStaticFrameInfo(DecodedImage? decoded) => decoded switch
    {
        { IsAnimated: true } d => FormatAnimationInfo(0, d.FrameDelaysMs, d.Width, d.Height, d.Format, playing: false),
        { PageCount: > 1 } d => string.Create(CultureInfo.InvariantCulture,
            $"페이지 {d.PageIndex + 1} / {d.PageCount}\n{d.Width:N0} × {d.Height:N0}  ·  {d.Format}"),
        _ => null,
    };

    private void StopAnimation()
    {
        _animationTimer?.Stop();
        _playback?.Dispose();
        _playback = null;
    }

    #endregion

    #region 이미지 정보 패널

    [ObservableProperty]
    private IReadOnlyList<MetadataItem> _infoItems = [];

    /// <summary>Tab 으로 여닫는 이미지 정보(EXIF) 패널.</summary>
    public bool IsInfoPanelVisible
    {
        get => Settings.Display.InfoPanelVisible;
        set
        {
            if (Settings.Display.InfoPanelVisible == value)
            {
                return;
            }

            Settings.Display.InfoPanelVisible = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleInfoPanel() => IsInfoPanelVisible = !IsInfoPanelVisible;

    private void UpdateImageInfo(ImageInfo info, DecodedImage? decoded)
    {
        InfoItems = BuildInfoItems(info, decoded, _transform);
    }

    internal static IReadOnlyList<MetadataItem> BuildInfoItems(ImageInfo info, DecodedImage? decoded, ImageTransform transform)
    {
        var items = new List<MetadataItem>
        {
            MetadataItem.Header("파일"),
            new("이름", info.DisplayPath),
        };

        if (info.Size >= 0)
        {
            items.Add(new("크기", $"{FileSizeHelper.Format(info.Size)} ({info.Size.ToString("N0", CultureInfo.InvariantCulture)} 바이트)"));
        }

        if (info.ModifiedAt is { } modified)
        {
            items.Add(new("수정 날짜", modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        if (decoded is null)
        {
            return items;
        }

        items.Add(MetadataItem.Header("이미지"));
        items.Add(new("해상도", $"{decoded.OriginalWidth:N0} × {decoded.OriginalHeight:N0}"));
        items.Add(new("형식", decoded.Format));
        if (decoded.PageCount > 1)
        {
            items.Add(new("페이지", $"{decoded.PageIndex + 1} / {decoded.PageCount}"));
        }

        if (decoded.IsAnimated)
        {
            items.Add(new("프레임", $"{decoded.FrameCount}개 (애니메이션)"));
        }

        if (!transform.IsIdentity)
        {
            items.Add(new("표시 변환", transform.Describe()));
        }

        if (decoded.Exif.Count > 0)
        {
            items.Add(MetadataItem.Header("EXIF"));
            items.AddRange(decoded.Exif);
        }

        if (decoded.Metadata.Count > 0)
        {
            items.Add(MetadataItem.Header("메타 정보") with { IsCopyable = true });
            items.AddRange(decoded.Metadata);
        }

        return items;
    }

    /// <summary>
    /// 정보 패널의 메타 정보 항목을 클릭했을 때 클립보드에 넣을 글. 항목이면 잘리지 않은 값,
    /// "메타 정보" 제목이면 구역 전체("이름: 값", 빈 줄로 구분). 복사할 수 없는 항목이면 null.
    /// </summary>
    internal static string? BuildMetadataCopyText(IReadOnlyList<MetadataItem> items, MetadataItem clicked)
    {
        if (!clicked.IsCopyable)
        {
            return null;
        }

        var text = clicked.IsHeader
            ? string.Join("\n\n", items.Where(i => i.IsCopyable && !i.IsHeader).Select(i => $"{i.Label}: {i.CopyValue}"))
            : clicked.CopyValue;
        return text.Length == 0 ? null : text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    /// <summary>메타 정보 항목을 클립보드에 복사한다. 클립보드 쓰기는 창(View)이 맡는다.</summary>
    public async Task CopyMetadataAsync(MetadataItem clicked, Func<string, Task> setClipboardText)
    {
        if (BuildMetadataCopyText(InfoItems, clicked) is not { } text)
        {
            return;
        }

        try
        {
            await setClipboardText(text);
            ShowOsd(clicked.IsHeader ? "메타 정보 전체를 클립보드에 복사했습니다." : $"'{clicked.Label}' 을(를) 클립보드에 복사했습니다.");
        }
        catch (Exception)
        {
            // 다른 프로그램이 클립보드를 잡고 있으면 실패할 수 있다
            ShowOsd("클립보드에 복사하지 못했습니다.");
        }
    }

    #endregion

    #region 빠른 표시: 화면 크기 디코딩 · 원본 업그레이드 · 미리 읽기

    private string? _upgradingKey;
    private DateTime _lastManualNavigationAt = DateTime.MinValue;
    private bool _rapidNavigation;

    /// <summary>키 · 휠 · 썸네일 등으로 직접 이동한 시각을 기록한다 (빠른 연속 이동 감지).</summary>
    private void NoteManualNavigation()
    {
        var now = DateTime.UtcNow;
        _rapidNavigation = now - _lastManualNavigationAt < TimeSpan.FromMilliseconds(600);
        _lastManualNavigationAt = now;
    }

    /// <summary>현재 표시 영역 · 모드에서 필요한 디코딩 크기 정보. 표시 영역을 아직 모르면 null (원본 디코딩).</summary>
    private DecodeTarget? CurrentDecodeTarget() =>
        _viewportWidth > 0 && _viewportHeight > 0
            ? new DecodeTarget(DisplayMode, _viewportWidth * _dpiScale, _viewportHeight * _dpiScale)
            : null;

    /// <summary>
    /// 줄여 읽은 이미지로 그려야 할 크기가 디코딩된 크기보다 크면(원본 크기 모드 · 확대 · 창 확대)
    /// 원본을 백그라운드에서 다시 읽어 바꿔 끼운다. 그동안은 줄여 읽은 이미지를 늘려 보여 준다.
    /// </summary>
    private void RequestFullResolutionIfNeeded(DecodedImage decoded, DisplayLayout layout, ImageTransform transform)
    {
        if (!decoded.IsReduced)
        {
            return;
        }

        var (width, height) = transform.Apply(decoded.Width, decoded.Height);
        if (layout.RenderPixelWidth <= width * 1.02 && layout.RenderPixelHeight <= height * 1.02)
        {
            return;
        }

        _ = UpgradeToFullResolutionAsync(decoded);
    }

    private async Task UpgradeToFullResolutionAsync(DecodedImage reduced)
    {
        var source = _source;
        if (source is null || _upgradingKey == reduced.Key)
        {
            return;
        }

        _upgradingKey = reduced.Key;
        var loadVersion = _loadVersion;
        try
        {
            var full = await _loader.LoadAsync(source, reduced.Info, reduced.PageIndex, target: null, CancellationToken.None, background: true);
            if (loadVersion != _loadVersion || !ReferenceEquals(reduced, _decoded))
            {
                full.Release();
                return;
            }

            // 같은 이미지 · 같은 비율이므로 현재 비트맵을 그대로 둔 채 바꿔 끼우고 정확한 크기로 다시 그린다.
            ReplaceDecoded(full);
            _bitmapImageKey = full.Key;
            _renderDebounce.Stop();
            await RenderCurrentAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "원본 해상도 읽기 실패: {Name}", reduced.Info.Name);
        }
        finally
        {
            if (_upgradingKey == reduced.Key)
            {
                _upgradingKey = null;
            }
        }
    }

    /// <summary>
    /// 다음에 볼 가능성이 높은 이미지를 우선순위대로 미리 디코딩하고 현재 표시 조건으로 렌더링까지 해 둔다.
    /// 이동하면 캐시에서 바로 꺼내 보여 줄 수 있다. 다른 이미지로 이동하면 취소된다.
    /// - 슬라이드쇼 재생 중: ① 슬라이드쇼의 다음 이미지(랜덤이면 섞은 순서의 다음) ② 오른쪽 ③ 왼쪽
    /// - 그 밖에는: 이동 방향의 다음 두 장, 반대 방향 한 장
    /// </summary>
    private void StartPrefetch()
    {
        var source = _source;
        if (source is null || _images.Count < 2 || CurrentDecodeTarget() is not { } target)
        {
            return;
        }

        var candidates = GetPrefetchCandidates();
        if (candidates.Count == 0)
        {
            return;
        }

        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        _ = PrefetchAsync(source, candidates.Select(i => _images[i]).ToList(), target, Filter, _prefetchCts.Token);
    }

    internal List<int> GetPrefetchCandidates()
    {
        var count = _images.Count;
        var candidates = new List<int>();
        if (CurrentIndex < 0 || count < 2)
        {
            return candidates;
        }

        void AddIndex(int index)
        {
            if (index >= 0 && index < count && index != CurrentIndex && !candidates.Contains(index))
            {
                candidates.Add(index);
            }
        }

        void AddOffset(int offset)
        {
            var index = CurrentIndex + offset;
            if (_navigation.WrapAround)
            {
                index = ((index % count) + count) % count;
            }

            AddIndex(index);
        }

        var forward = _lastDirection >= 0 ? 1 : -1;
        var manualRecently = (DateTime.UtcNow - _lastManualNavigationAt) < TimeSpan.FromSeconds(2);

        // 슬라이드쇼만 보고 있을 때: 다음 슬라이드를 가장 먼저 (랜덤이면 이 호출에서 섞은 순서가 만들어지고,
        // 다음 타이머가 같은 이미지를 꺼낸다).
        int? slideShowNext = IsSlideShowPlaying ? _slideShow.PeekNextIndex(CurrentIndex, count) : null;
        if (slideShowNext is { } first && !manualRecently)
        {
            AddIndex(first);
            AddOffset(+1);
            AddOffset(-1);
            return candidates;
        }

        // 키 · 휠로 넘기고 있을 때: 진행 방향을 우선 (빠르게 연속으로 넘기면 더 멀리),
        // 슬라이드쇼 다음 이미지는 타이머(최소 5초)까지 여유가 있으므로 그 뒤에.
        AddOffset(forward);
        AddOffset(forward * 2);
        if (_rapidNavigation)
        {
            AddOffset(forward * 3);
            AddOffset(forward * 4);
        }

        if (slideShowNext is { } next)
        {
            AddIndex(next);
        }

        AddOffset(-forward);
        return candidates;
    }

    private async Task PrefetchAsync(
        IImageSource source,
        IReadOnlyList<ImageInfo> images,
        DecodeTarget target,
        ImageFilter filter,
        CancellationToken ct)
    {
        // 우선순위 순서대로 두 장씩 동시에 준비한다 (JPEG 디코딩은 코어 하나만 쓰므로 병렬로 처리량을 높인다).
        foreach (var pair in images.Chunk(2))
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(source, _source))
            {
                return;
            }

            await Task.WhenAll(pair.Select(info => PrefetchOneAsync(source, info, target, filter, ct)));
        }
    }

    private async Task PrefetchOneAsync(
        IImageSource source,
        ImageInfo info,
        DecodeTarget target,
        ImageFilter filter,
        CancellationToken ct)
    {
        if (!info.IsSupported)
        {
            return;
        }

        {

            try
            {
                var decoded = await _loader.LoadAsync(source, info, 0, target, ct, background: true);
                try
                {
                    // 애니메이션은 첫 프레임만 디코딩해 두고, 나머지 프레임은 재생하면서 디코딩한다.
                    if (!(decoded.IsAnimated && PlayAnimations))
                    {
                        // 이동하면 회전 · 반전과 배율은 초기화되므로 그 조건으로 미리 그린다.
                        var (width, height) = (decoded.OriginalWidth, decoded.OriginalHeight);
                        var layout = ImageResizeService.Calculate(
                            DisplayMode, 1.0, width, height,
                            _viewportWidth * _dpiScale, _viewportHeight * _dpiScale);
                        await _renderer.RenderAsync(decoded, layout.RenderPixelWidth, layout.RenderPixelHeight, filter, ImageTransform.Identity, ct, background: true);
                    }
                }
                finally
                {
                    decoded.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "미리 읽기 실패: {Name}", info.Name);
            }
        }
    }

    #endregion

    #region 느린 로딩: 썸네일 임시 표시

    /// <summary>고화질 로딩이 이 시간보다 오래 걸리면 썸네일을 먼저 보여 준다.</summary>
    internal static readonly TimeSpan PlaceholderDelay = TimeSpan.FromMilliseconds(250);

    private DispatcherTimer? _placeholderTimer;
    private int _placeholderVersion;
    private ImageInfo? _placeholderInfo;

    /// <summary>
    /// 썸네일을 흐리게 늘려 임시로 보여 주는 중인지. 고화질 렌더링 결과가 나오면 바로 바뀐다.
    /// (View 는 이때 이미지에 블러 효과를 준다)
    /// </summary>
    [ObservableProperty]
    private bool _isShowingPlaceholder;

    /// <summary>
    /// 다른 이미지로 이동했을 때, 고화질이 <see cref="PlaceholderDelay"/> 안에 준비되지 않으면 썸네일을 대신 보여 주도록 예약한다.
    /// 미리 읽어 둔 이미지는 그 전에 표시되므로 평소 이동에서는 나타나지 않는다.
    /// </summary>
    private void SchedulePlaceholder(int loadVersion, ImageInfo info, bool isPageChange)
    {
        _placeholderTimer?.Stop();

        // 같은 이미지 안의 페이지 이동, 원본 크기 보기(실제 크기를 아직 모름)에는 쓰지 않는다.
        if (isPageChange || DisplayMode == DisplayMode.Original)
        {
            return;
        }

        if (_placeholderTimer is null)
        {
            _placeholderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = PlaceholderDelay };
            _placeholderTimer.Tick += (_, _) =>
            {
                _placeholderTimer.Stop();
                ShowPlaceholderIfStillLoading();
            };
        }

        _placeholderVersion = loadVersion;
        _placeholderInfo = info;
        _placeholderTimer.Start();
    }

    private void ShowPlaceholderIfStillLoading()
    {
        // 그 사이 다른 이미지로 이동했거나 이미 고화질이 표시되었으면 아무것도 하지 않는다.
        if (_placeholderVersion != _loadVersion ||
            _placeholderInfo is not { } info ||
            CurrentImage?.Key != info.Key ||
            _bitmapImageKey is { } shown && _decoded is { } d && d.Info.Key == info.Key && shown == d.Key)
        {
            return;
        }

        // 이미 만들어 둔 썸네일만 쓴다 (느린 이미지는 썸네일을 새로 만드는 것도 느리다).
        var index = CurrentIndex;
        if (index < 0 || index >= ThumbnailItems.Count || ThumbnailItems[index].Info.Key != info.Key ||
            ThumbnailItems[index].CachedThumbnail is not { } thumbnail ||
            thumbnail.PixelSize.Width <= 0 || thumbnail.PixelSize.Height <= 0)
        {
            return;
        }

        var (width, height) = PlaceholderDisplaySize(
            DisplayMode, (uint)thumbnail.PixelSize.Width, (uint)thumbnail.PixelSize.Height,
            _viewportWidth * _dpiScale, _viewportHeight * _dpiScale);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        StopAnimation();
        FrameInfoText = null;
        ErrorMessage = null;
        CurrentBitmap = thumbnail;
        IsShowingPlaceholder = true;
        _bitmapImageKey = null;

        // 고화질과 같은 위치 · 크기에 배치해 교체할 때 움직이지 않게 한다 (맞춤 모드는 비율만으로 크기가 정해진다).
        DisplayWidth = width / _dpiScale;
        DisplayHeight = height / _dpiScale;
        _pendingNewImage = true;
        LayoutChanged?.Invoke(this, new LayoutChangedEventArgs(isNewImage: true));
    }

    /// <summary>썸네일 비율로 계산한 화면 표시 크기 (장치 픽셀). 맞춤 모드에서는 원본으로 계산한 크기와 같다.</summary>
    internal static (double Width, double Height) PlaceholderDisplaySize(
        DisplayMode mode,
        uint thumbnailWidth,
        uint thumbnailHeight,
        double viewportPixelWidth,
        double viewportPixelHeight)
    {
        var layout = ImageResizeService.Calculate(mode, 1.0, thumbnailWidth, thumbnailHeight, viewportPixelWidth, viewportPixelHeight);
        return (layout.DisplayPixelWidth, layout.DisplayPixelHeight);
    }

    #endregion

    #region 폴더 감시

    /// <summary>폴더의 이미지 추가 · 삭제 · 이름 변경 · 수정을 감시해 목록을 자동으로 갱신한다.</summary>
    public bool WatchFolder => Settings.Navigation.WatchFolder;

    private void AttachWatcher(IImageSource source)
    {
        if (!Settings.Navigation.WatchFolder || source is not FolderImageSource folder)
        {
            return;
        }

        try
        {
            folder.Changed += OnSourceChanged;
            folder.StartWatching();
            _watchedSource = folder;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            folder.Changed -= OnSourceChanged;
            _logger.LogWarning(ex, "폴더 감시를 시작하지 못했습니다.");
        }
    }

    private void DetachWatcher(IImageSource? source)
    {
        if (source is FolderImageSource folder)
        {
            folder.Changed -= OnSourceChanged;
            folder.StopWatching();
        }

        if (ReferenceEquals(source, _watchedSource))
        {
            _watchedSource = null;
        }

        _watchDebounce?.Stop();
    }

    /// <summary>설정 변경 후 감시를 다시 구성한다.</summary>
    private void RefreshWatcher()
    {
        if (_source is null)
        {
            return;
        }

        DetachWatcher(_source);
        AttachWatcher(_source);
    }

    /// <summary>파일 시스템 이벤트(백그라운드 스레드) → 잠시 모았다가 UI 스레드에서 한 번에 갱신.</summary>
    private void OnSourceChanged(object? sender, EventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (!ReferenceEquals(sender, _source))
            {
                return;
            }

            if (_watchDebounce is null)
            {
                _watchDebounce = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(700),
                };
                _watchDebounce.Tick += (_, _) =>
                {
                    if (_dialogs.IsModalOpen)
                    {
                        return; // 대화 상자가 닫힐 때까지 목록을 바꾸지 않는다 (타이머가 계속 다시 확인)
                    }

                    _watchDebounce.Stop();
                    _ = RefreshSourceAsync();
                };
            }

            _watchDebounce.Stop();
            _watchDebounce.Start();
        });
    }

    /// <summary>
    /// 이미지 목록을 다시 읽는다. 현재 이미지는 그대로 유지하고,
    /// 현재 이미지가 삭제되었으면 같은 위치의 이미지를, 내용이 바뀌었으면 다시 읽어 표시한다.
    /// <paramref name="osdMessage"/> 가 있으면 변경 요약 대신 그 메시지를 보여 준다.
    /// </summary>
    internal async Task RefreshSourceAsync(string? osdMessage = null)
    {
        var source = _source;
        if (source is null)
        {
            return;
        }

        var version = ++_refreshVersion;
        IReadOnlyList<ImageInfo> images;
        try
        {
            images = await source.GetImagesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "변경된 폴더를 다시 읽지 못했습니다.");
            return;
        }

        if (version != _refreshVersion || !ReferenceEquals(source, _source))
        {
            return;
        }

        var previous = CurrentImage;
        var previousIndex = CurrentIndex;
        var oldCount = _images.Count;

        // 바뀐 파일의 캐시를 버린다.
        var oldByKey = _images.ToDictionary(i => i.Key);
        var anyModified = false;
        foreach (var image in images)
        {
            if (oldByKey.TryGetValue(image.Key, out var old) && IsModified(old, image))
            {
                anyModified = true;
                _loader.Invalidate(image);
                _renderer.Invalidate(image);
            }
        }

        // 이미 반영한 변경(예: Del 로 지운 뒤 뒤늦게 온 폴더 감시 이벤트)이면 아무것도 하지 않는다.
        if (!anyModified && images.Count == _images.Count && images.Select(i => i.Key).SequenceEqual(_images.Select(i => i.Key)))
        {
            return;
        }

        var newIndex = previous is null ? -1 : images.ToList().FindIndex(i => i.Key == previous.Key);
        var currentRemoved = previous is not null && newIndex < 0;
        var currentModified = newIndex >= 0 && IsModified(previous!, images[newIndex]);
        if (newIndex < 0 && images.Count > 0)
        {
            newIndex = Math.Clamp(previousIndex, 0, images.Count - 1);
        }

        _images = images;
        _navigation.Reset(images.Count, Math.Max(0, newIndex));
        _slideShow.ResetOrder();
        RebuildThumbnails(images);

        if (images.Count == 0)
        {
            StopSlideShow(showMessage: false);
            CancelPending();
            ReplaceDecoded(null);
            CurrentIndex = -1;
            CurrentImage = null;
            CurrentBitmap = null;
            ErrorMessage = null;
            InfoItems = [];
            FrameInfoText = null;
            PlaceholderMessage = $"이 {source.Info.Label}에는 표시할 이미지가 없습니다.";
            OnPropertyChanged(nameof(PositionText));
            ShowOsd(osdMessage ?? "폴더에 이미지가 없습니다.");
            return;
        }

        PlaceholderMessage = null;
        SetCurrent(newIndex);
        OnPropertyChanged(nameof(PositionText));

        if (currentRemoved || currentModified || _decoded is null)
        {
            _ = LoadCurrentAsync(resetTransform: currentRemoved);
        }

        var delta = images.Count - oldCount;
        ShowOsd(osdMessage ?? delta switch
        {
            > 0 => $"폴더 변경: 이미지 {delta}개 추가 (총 {images.Count:N0}개)",
            < 0 => $"폴더 변경: 이미지 {-delta}개 제거 (총 {images.Count:N0}개)",
            _ => currentModified ? "현재 이미지가 변경되어 다시 읽었습니다." : $"폴더 변경 반영 (총 {images.Count:N0}개)",
        });
    }

    private static bool IsModified(ImageInfo before, ImageInfo after) =>
        before.Size != after.Size || before.ModifiedAt != after.ModifiedAt;

    /// <summary>바뀌지 않은 항목은 기존 썸네일을 재사용해 목록을 다시 만든다.</summary>
    private void RebuildThumbnails(IReadOnlyList<ImageInfo> images)
    {
        var existing = new Dictionary<string, ThumbnailItemViewModel>();
        foreach (var item in ThumbnailItems)
        {
            existing.TryAdd(item.Info.Key, item);
        }

        var items = new List<ThumbnailItemViewModel>(images.Count);
        for (var i = 0; i < images.Count; i++)
        {
            var info = images[i];
            if (existing.TryGetValue(info.Key, out var item) && !IsModified(item.Info, info))
            {
                item.Update(i, info);
                item.IsCurrent = false;
            }
            else
            {
                item = new ThumbnailItemViewModel(i, info, RequestThumbnail);
            }

            items.Add(item);
        }

        // 목록에서 빠진 항목은 썸네일 메모리 관리 대상에서도 뺀다.
        var kept = new HashSet<ThumbnailItemViewModel>(items);
        var node = _loadedThumbnails.First;
        while (node is not null)
        {
            var next = node.Next;
            if (!kept.Contains(node.Value))
            {
                _loadedThumbnails.Remove(node);
            }

            node = next;
        }

        // CurrentIndex 가 가리키던 이전 항목의 표시를 SetCurrent 가 지우지 않도록 먼저 목록을 바꾼다.
        CurrentIndex = -1;
        _widePages = new bool[images.Count];
        _manualComicBlanks.Clear();
        _comicPagesProbed = false;
        ThumbnailItems = new ObservableCollection<ThumbnailItemViewModel>(items);
        RebuildComicSpreads();
        if (IsComicMode)
        {
            StartComicPageProbe();
        }
    }

    #endregion

    #region 파일 삭제 (Del)

    /// <summary>
    /// 현재 이미지를 지울 수 있는지: 폴더에서 연 일반 이미지 파일만 (ZIP 내부 항목은 제외).
    /// </summary>
    internal static bool CanDeleteFile(IImageSource? source, ImageInfo? info) =>
        source is FolderImageSource &&
        info is { SourceType: ImageSourceType.File, FullPath: { Length: > 0 } };

    /// <summary>
    /// 현재 보고 있는 이미지 파일을 확인 대화 상자를 거쳐 휴지통으로 옮기고, 목록에서 빼 같은 위치의 이미지를 보여 준다.
    /// </summary>
    [RelayCommand]
    private async Task DeleteCurrentFileAsync()
    {
        var source = _source;
        var info = CurrentImage;
        if (info is null)
        {
            return;
        }

        if (!CanDeleteFile(source, info))
        {
            ShowOsd("압축 파일 안의 이미지는 삭제할 수 없습니다.");
            return;
        }

        var path = info.FullPath!;
        var confirmed = _dialogs.Confirm(
            $"이 파일을 휴지통으로 옮길까요?\n\n{info.Name}\n{FileSizeHelper.Format(info.Size)} · {Path.GetDirectoryName(path)}");

        if (IsSlideShowPlaying)
        {
            // 대화 상자를 보는 동안 지난 시간 때문에 바로 다음 이미지로 넘어가지 않게 한다.
            _slideShow.RestartTimer();
        }

        if (!confirmed || !ReferenceEquals(source, _source))
        {
            return;
        }

        try
        {
            // 디코딩된 이미지가 파일을 잡고 있지는 않지만, 같은 경로의 새 파일과 섞이지 않게 캐시를 비운다.
            _loader.Invalidate(info);
            _renderer.Invalidate(info);
            FileTrash.MoveToTrash(path);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "파일을 휴지통으로 옮기지 못했습니다: {Name}", info.Name);
            _dialogs.ShowMessage($"파일을 삭제하지 못했습니다.\n\n{info.Name}\n{ex.Message}", isError: true);
            return;
        }

        _logger.LogInformation("휴지통으로 옮김: {Name}", info.Name);

        // 폴더 감시가 꺼져 있어도 바로 목록에서 빼고, 같은 위치의 이미지를 보여 준다.
        await RefreshSourceAsync($"휴지통으로 옮겼습니다: {info.Name}");
    }

    #endregion

    #region 이전 · 다음 폴더 / 압축파일 ([ / ])

    [RelayCommand]
    private Task PreviousSourceAsync() => OpenAdjacentSourceAsync(-1);

    [RelayCommand]
    private Task NextSourceAsync() => OpenAdjacentSourceAsync(+1);

    private async Task OpenAdjacentSourceAsync(int direction)
    {
        if (_source is null)
        {
            await OpenFileAsync();
            return;
        }

        var info = _source.Info;
        var target = FindAdjacentSourcePath(info, direction);
        if (target is null)
        {
            var kind = info.SourceType == ImageSourceType.Zip ? "ZIP 파일" : "폴더";
            ShowOsd(direction < 0 ? $"이전 {kind}이(가) 없습니다." : $"다음 {kind}이(가) 없습니다.");
            return;
        }

        await OpenPathAsync(target);
    }

    internal static string? FindAdjacentSourcePath(ImageSourceInfo info, int direction)
    {
        var current = info.SourceType == ImageSourceType.File
            ? Path.TrimEndingDirectorySeparator(info.Path)
            : info.Path;
        var parent = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return null;
        }

        List<string> candidates;
        try
        {
            candidates = info.SourceType == ImageSourceType.File
                ? Directory.EnumerateDirectories(parent).ToList()
                : Directory.EnumerateFiles(parent).Where(ImageFormatHelper.IsZipFile).ToList();
        }
        catch (Exception)
        {
            return null;
        }

        candidates.Sort((a, b) => NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));

        var index = candidates.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var targetIndex = index + direction;
        return targetIndex >= 0 && targetIndex < candidates.Count ? candidates[targetIndex] : null;
    }

    #endregion
}
