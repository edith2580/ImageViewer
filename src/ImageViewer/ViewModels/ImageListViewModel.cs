using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageViewer.Helpers;
using ImageViewer.Models;

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
    }

    public int Index { get; }

    public ImageInfo Info { get; }

    public string Name { get; }

    public string DateText { get; }

    public string SizeText { get; }

    public bool IsSupported => Info.IsSupported;
}

/// <summary>Enter 키로 여는 이미지 목록 대화 상자.</summary>
public sealed partial class ImageListViewModel : ObservableObject
{
    public ImageListViewModel(
        IReadOnlyList<ImageInfo> images,
        int currentIndex,
        ImageSourceInfo? source,
        IReadOnlyList<ThumbnailItemViewModel>? thumbnails = null)
    {
        var items = images.Select((info, i) => new ImageListItem(i, info)).ToList();
        Items = items;
        Thumbnails = thumbnails ?? [];
        CurrentIndex = currentIndex;
        Title = source is null
            ? "이미지 목록"
            : $"이미지 목록 - {source.DisplayName} ({Items.Count:N0}개)";
        SelectedItem = currentIndex >= 0 && currentIndex < Items.Count ? Items[currentIndex] : Items.FirstOrDefault();
        ApplySort();
    }

    public string Title { get; }

    public IReadOnlyList<ImageListItem> Items { get; }

    public ObservableCollection<ImageListItem> ItemsView { get; } = [];

    public int CurrentIndex { get; }

    /// <summary>상단 썸네일 줄 (메인 화면과 같은 항목을 공유해 이미 만든 썸네일을 재사용한다).</summary>
    public IReadOnlyList<ThumbnailItemViewModel> Thumbnails { get; }

    public bool HasThumbnails => Thumbnails.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedThumbnailIndex))]
    private ImageListItem? _selectedItem;

    /// <summary>썸네일 줄의 선택 위치. 목록 선택과 서로 동기화된다.</summary>
    public int SelectedThumbnailIndex
    {
        get => SelectedItem?.Index ?? -1;
        set
        {
            if (value >= 0 && value < Items.Count && value != SelectedItem?.Index)
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

    public string NameHeader => "파일명" + Arrow(ImageListSortColumn.Name);

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
                ImageListSortColumn.Size => a.Info.Size.CompareTo(b.Info.Size),
                _ => 0,
            };

            // 파일명 정렬은 원래 순서(자연 정렬), 같은 값이면 파일명 순서로
            if (result == 0)
            {
                result = a.Index.CompareTo(b.Index);
            }

            return ascending ? result : -result;
        }
    }
}
