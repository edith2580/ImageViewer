using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageViewer.Helpers;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.ViewModels;

public enum ImageListSortColumn
{
    Name,
    Date,
    Size,
}

public sealed class ImageListItem
{
    public ImageListItem(int index, ImageInfo info)
    {
        Index = index;
        Info = info;
        Name = info.DisplayPath;
        DateText = info.ModifiedAt is { } d ? d.ToString("yyyy-MM-dd HH:mm") : "-";
        SizeText = info.Size >= 0 ? FileSizeHelper.Format(info.Size) : "-";
        SortSize = info.Size;
        IsSupported = info.IsSupported;
    }

    public ImageListItem(
        int startIndex,
        ImageInfo first,
        ImageInfo? second,
        ComicReadingDirection direction,
        bool isWideSingle = false,
        bool trailingBlank = false)
    {
        Index = startIndex;
        Info = first;
        if (second is not null)
        {
            Name = direction == ComicReadingDirection.RightToLeft
                ? $"[{startIndex + 2}p | {startIndex + 1}p]  {second.DisplayPath}  |  {first.DisplayPath}"
                : $"[{startIndex + 1}p | {startIndex + 2}p]  {first.DisplayPath}  |  {second.DisplayPath}";
            var combinedSize = (first.Size >= 0 ? first.Size : 0) + (second.Size >= 0 ? second.Size : 0);
            SortSize = first.Size >= 0 || second.Size >= 0 ? combinedSize : -1;
            SizeText = SortSize >= 0 ? FileSizeHelper.Format(SortSize) : "-";
            IsSupported = first.IsSupported || second.IsSupported;
        }
        else if (isWideSingle)
        {
            Name = $"[{startIndex + 1}p · 양면]  {first.DisplayPath}";
            SortSize = first.Size;
            SizeText = first.Size >= 0 ? FileSizeHelper.Format(first.Size) : "-";
            IsSupported = first.IsSupported;
        }
        else if (trailingBlank)
        {
            Name = direction == ComicReadingDirection.RightToLeft
                ? $"[빈 | {startIndex + 1}p]  (빈 페이지)  |  {first.DisplayPath}"
                : $"[{startIndex + 1}p | 빈]  {first.DisplayPath}  |  (빈 페이지)";
            SortSize = first.Size;
            SizeText = first.Size >= 0 ? FileSizeHelper.Format(first.Size) : "-";
            IsSupported = first.IsSupported;
        }
        else
        {
            Name = $"[{startIndex + 1}p]  {first.DisplayPath}";
            SortSize = first.Size;
            SizeText = first.Size >= 0 ? FileSizeHelper.Format(first.Size) : "-";
            IsSupported = first.IsSupported;
        }

        DateText = first.ModifiedAt is { } d ? d.ToString("yyyy-MM-dd HH:mm") : "-";
    }

    public int Index { get; }

    public ImageInfo Info { get; }

    public string Name { get; }

    public string DateText { get; }

    public string SizeText { get; }

    internal long SortSize { get; }

    public bool IsSupported { get; }
}

/// <summary>Enter 키로 여는 이미지 목록 대화 상자.</summary>
public sealed partial class ImageListViewModel : ObservableObject
{
    private readonly IReadOnlyList<ComicSpread> _comicSpreads = [];

    public ImageListViewModel(
        IReadOnlyList<ImageInfo> images,
        int currentIndex,
        ImageSourceInfo? source,
        IReadOnlyList<ThumbnailItemViewModel>? thumbnails = null,
        bool isComicMode = false,
        ComicReadingDirection comicDirection = ComicReadingDirection.LeftToRight,
        IReadOnlyList<ComicThumbnailItemViewModel>? comicThumbnails = null,
        IReadOnlyList<ComicSpread>? comicSpreads = null)
    {
        IsComicMode = isComicMode;
        ComicDirection = comicDirection;
        Thumbnails = thumbnails ?? [];
        ComicThumbnails = comicThumbnails ?? [];

        List<ImageListItem> items;
        if (isComicMode)
        {
            _comicSpreads = comicSpreads is { Count: > 0 } ? comicSpreads : ComicSpreadHelper.BuildSpreads(images.Count);
            items = new List<ImageListItem>(_comicSpreads.Count);
            foreach (var spread in _comicSpreads)
            {
                var first = images[spread.FirstIndex];
                var second = spread.SecondIndex is { } s && s < images.Count ? images[s] : null;
                items.Add(new ImageListItem(
                    spread.FirstIndex,
                    first,
                    second,
                    comicDirection,
                    spread.IsWideSingle,
                    spread.TrailingBlank));
            }

            var selectedSpread = ComicSpreadHelper.FindSpreadIndex(_comicSpreads, currentIndex);
            CurrentIndex = selectedSpread >= 0 ? _comicSpreads[selectedSpread].FirstIndex : -1;
            var dirLabel = comicDirection == ComicReadingDirection.RightToLeft ? "우→좌" : "좌→우";
            Title = source is null
                ? $"이미지 목록 [코믹: {dirLabel}]"
                : $"이미지 목록 [코믹: {dirLabel}] - {source.DisplayName} ({images.Count:N0}개)";
            SelectedItem = selectedSpread >= 0 && selectedSpread < items.Count ? items[selectedSpread] : items.FirstOrDefault();
        }
        else
        {
            items = images.Select((info, i) => new ImageListItem(i, info)).ToList();
            CurrentIndex = currentIndex;
            Title = source is null
                ? "이미지 목록"
                : $"이미지 목록 - {source.DisplayName} ({items.Count:N0}개)";
            SelectedItem = currentIndex >= 0 && currentIndex < items.Count ? items[currentIndex] : items.FirstOrDefault();
        }

        Items = items;
        ApplySort();
    }

    public string Title { get; }

    public bool IsComicMode { get; }

    public ComicReadingDirection ComicDirection { get; }

    public IReadOnlyList<ImageListItem> Items { get; }

    public ObservableCollection<ImageListItem> ItemsView { get; } = [];

    public int CurrentIndex { get; }

    /// <summary>상단 썸네일 줄 (메인 화면과 같은 항목을 공유해 이미 만든 썸네일을 재사용한다).</summary>
    public IReadOnlyList<ThumbnailItemViewModel> Thumbnails { get; }

    /// <summary>코믹 모드용 상단 2페이지 연결 썸네일 줄.</summary>
    public IReadOnlyList<ComicThumbnailItemViewModel> ComicThumbnails { get; }

    public bool HasSingleThumbnails => !IsComicMode && Thumbnails.Count > 0;

    public bool HasComicThumbnails => IsComicMode && ComicThumbnails.Count > 0;

    public bool HasThumbnails => HasSingleThumbnails || HasComicThumbnails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedThumbnailIndex))]
    [NotifyPropertyChangedFor(nameof(SelectedComicThumbnailIndex))]
    private ImageListItem? _selectedItem;

    /// <summary>썸네일 줄의 선택 위치. 목록 선택과 서로 동기화된다.</summary>
    public int SelectedThumbnailIndex
    {
        get => IsComicMode ? -1 : (SelectedItem?.Index ?? -1);
        set
        {
            if (!IsComicMode && value >= 0 && value < Items.Count && value != SelectedItem?.Index)
            {
                SelectedItem = Items[value];
            }
        }
    }

    /// <summary>코믹 모드 2페이지 썸네일 줄의 선택 위치.</summary>
    public int SelectedComicThumbnailIndex
    {
        get => IsComicMode && SelectedItem is not null
            ? ComicSpreadHelper.FindSpreadIndex(_comicSpreads, SelectedItem.Index)
            : -1;
        set
        {
            if (IsComicMode && value >= 0 && value < Items.Count && value != SelectedComicThumbnailIndex)
            {
                SelectedItem = Items[value];
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameHeader), nameof(DateHeader), nameof(SizeHeader))]
    private ImageListSortColumn _sortColumn = ImageListSortColumn.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameHeader), nameof(DateHeader), nameof(SizeHeader))]
    private bool _sortAscending = true;

    public string NameHeader => (IsComicMode ? "페이지 / 파일명" : "파일명") + Arrow(ImageListSortColumn.Name);

    public string DateHeader => "날짜" + Arrow(ImageListSortColumn.Date);

    public string SizeHeader => "크기" + Arrow(ImageListSortColumn.Size);

    /// <summary>대화 상자에서 선택한 결과 위치 (열기 시).</summary>
    public int? ResultIndex { get; private set; }

    private string Arrow(ImageListSortColumn column) =>
        SortColumn == column ? (SortAscending ? " ▲" : " ▼") : string.Empty;

    [RelayCommand]
    private void SortBy(ImageListSortColumn column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = true;
        }

        ApplySort();
    }

    public bool Accept()
    {
        if (SelectedItem is null)
        {
            return false;
        }

        ResultIndex = SelectedItem.Index;
        return true;
    }

    private void ApplySort()
    {
        var selected = SelectedItem;
        var sorted = Items.ToList();
        sorted.Sort(new ItemComparer(SortColumn, SortAscending));
        ItemsView.Clear();
        foreach (var item in sorted)
        {
            ItemsView.Add(item);
        }

        if (selected is not null)
        {
            SelectedItem = selected;
        }
    }

    private sealed class ItemComparer(ImageListSortColumn column, bool ascending) : IComparer<ImageListItem>
    {
        public int Compare(ImageListItem? a, ImageListItem? b)
        {
            if (a is null || b is null)
            {
                return 0;
            }

            var result = column switch
            {
                ImageListSortColumn.Date => Nullable.Compare(a.Info.ModifiedAt, b.Info.ModifiedAt),
                ImageListSortColumn.Size => a.SortSize.CompareTo(b.SortSize),
                _ => 0,
            };

            if (result == 0)
            {
                result = a.Index.CompareTo(b.Index);
            }

            return ascending ? result : -result;
        }
    }
}
