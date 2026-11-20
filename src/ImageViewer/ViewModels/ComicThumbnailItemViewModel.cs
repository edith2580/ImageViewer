using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageViewer.Models;

namespace ImageViewer.ViewModels;

/// <summary>
/// 코믹 뷰어(2페이지 보기)용 썸네일 항목.
/// 기존 <see cref="ThumbnailItemViewModel"/> 하나(양면 또는 빈 페이지 포함 단독) 또는 두 개(세로 이미지 2장)를 참조해
/// 이미 만든 썸네일을 그대로 공유하며, 읽기 방향(좌→우 / 우→좌)에 따라 왼쪽·오른쪽 이미지를 연결해 표시한다.
/// </summary>
public sealed partial class ComicThumbnailItemViewModel : ObservableObject
{
    private ComicReadingDirection _direction;

    public ComicThumbnailItemViewModel(
        int spreadIndex,
        ThumbnailItemViewModel first,
        ThumbnailItemViewModel? second,
        ComicReadingDirection direction,
        bool isWideSingle = false,
        bool trailingBlank = false)
    {
        SpreadIndex = spreadIndex;
        First = first;
        Second = second;
        IsWideSingle = isWideSingle;
        TrailingBlank = trailingBlank;
        _direction = direction;

        First.PropertyChanged += OnChildPropertyChanged;
        if (Second is not null)
        {
            Second.PropertyChanged += OnChildPropertyChanged;
        }
    }

    public int SpreadIndex { get; }

    public int StartIndex => First.Index;

    public ThumbnailItemViewModel First { get; }

    public ThumbnailItemViewModel? Second { get; }

    /// <summary>정사각형 · 가로 이미지라서 1장이 양면 전체를 차지하는 경우.</summary>
    public bool IsWideSingle { get; }

    public bool TrailingBlank { get; }

    public bool IsTwoPagePair => !IsWideSingle;

    public Bitmap? WideThumbnail => IsWideSingle ? First.Thumbnail : null;

    public string WideName => First.Name;

    public ThumbnailItemViewModel? LeftItem =>
        IsWideSingle ? First : (_direction == ComicReadingDirection.RightToLeft ? Second : First);

    public ThumbnailItemViewModel? RightItem =>
        IsWideSingle ? null : (_direction == ComicReadingDirection.RightToLeft ? First : Second);

    public Bitmap? LeftThumbnail => LeftItem?.Thumbnail;

    public Bitmap? RightThumbnail => RightItem?.Thumbnail;

    public bool IsLeftBlank => !IsWideSingle && LeftItem is null && TrailingBlank;

    public bool IsRightBlank => !IsWideSingle && RightItem is null && TrailingBlank;

    public bool HasLeft => LeftItem is not null || IsLeftBlank;

    public bool HasRight => RightItem is not null || IsRightBlank;

    public string LeftName => IsLeftBlank ? "빈 페이지" : (LeftItem?.Name ?? string.Empty);

    public string RightName => IsRightBlank ? "빈 페이지" : (RightItem?.Name ?? string.Empty);

    public string PageLabel => Second is not null
        ? $"{First.Index + 1}-{Second.Index + 1}"
        : $"{First.Index + 1}";

    public string ToolTip
    {
        get
        {
            if (IsWideSingle)
            {
                return $"{First.Index + 1}. [양면] {First.Info.DisplayPath}";
            }

            if (Second is not null)
            {
                return _direction == ComicReadingDirection.RightToLeft
                    ? $"{Second.Index + 1} · {First.Index + 1} ({Second.Name} | {First.Name})"
                    : $"{First.Index + 1} · {Second.Index + 1} ({First.Name} | {Second.Name})";
            }

            if (TrailingBlank)
            {
                return _direction == ComicReadingDirection.RightToLeft
                    ? $"빈 페이지 · {First.Index + 1} (빈 페이지 | {First.Name})"
                    : $"{First.Index + 1} · 빈 페이지 ({First.Name} | 빈 페이지)";
            }

            return First.ToolTip;
        }
    }

    [ObservableProperty]
    private bool _isCurrent;

    public void SetDirection(ComicReadingDirection direction)
    {
        if (_direction == direction)
        {
            return;
        }

        _direction = direction;
        OnPropertyChanged(nameof(LeftItem));
        OnPropertyChanged(nameof(RightItem));
        OnPropertyChanged(nameof(LeftThumbnail));
        OnPropertyChanged(nameof(RightThumbnail));
        OnPropertyChanged(nameof(IsLeftBlank));
        OnPropertyChanged(nameof(IsRightBlank));
        OnPropertyChanged(nameof(HasLeft));
        OnPropertyChanged(nameof(HasRight));
        OnPropertyChanged(nameof(LeftName));
        OnPropertyChanged(nameof(RightName));
        OnPropertyChanged(nameof(ToolTip));
    }

    public void Detach()
    {
        First.PropertyChanged -= OnChildPropertyChanged;
        if (Second is not null)
        {
            Second.PropertyChanged -= OnChildPropertyChanged;
        }
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ThumbnailItemViewModel.Thumbnail))
        {
            return;
        }

        if (IsWideSingle && ReferenceEquals(sender, First))
        {
            OnPropertyChanged(nameof(WideThumbnail));
        }

        if (ReferenceEquals(sender, LeftItem))
        {
            OnPropertyChanged(nameof(LeftThumbnail));
        }

        if (ReferenceEquals(sender, RightItem))
        {
            OnPropertyChanged(nameof(RightThumbnail));
        }
    }
}
