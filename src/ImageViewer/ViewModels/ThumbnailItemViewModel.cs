using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageViewer.Models;

namespace ImageViewer.ViewModels;

/// <summary>
/// 하단 썸네일 항목. 썸네일은 처음 화면에 바인딩될 때(가상화된 컨테이너가 생성될 때) 요청한다.
/// </summary>
public sealed partial class ThumbnailItemViewModel : ObservableObject
{
    private readonly Action<ThumbnailItemViewModel> _requestThumbnail;
    private Bitmap? _thumbnail;
    private bool _requested;

    public ThumbnailItemViewModel(int index, ImageInfo info, Action<ThumbnailItemViewModel> requestThumbnail)
    {
        Index = index;
        Info = info;
        _requestThumbnail = requestThumbnail;
    }

    public int Index { get; private set; }

    public ImageInfo Info { get; private set; }

    public string Name => Info.Name;

    public string ToolTip => $"{Index + 1}. {Info.DisplayPath}";

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isFailed;

    public Bitmap? Thumbnail
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _requestThumbnail(this);
            }

            return _thumbnail;
        }
    }

    internal bool HasThumbnail => _thumbnail is not null;

    /// <summary>이미 만들어 둔 썸네일 (없으면 null). <see cref="Thumbnail"/> 과 달리 생성을 요청하지 않는다.</summary>
    internal Bitmap? CachedThumbnail => _thumbnail;

    /// <summary>폴더 변경으로 목록이 다시 만들어질 때 위치 · 정보만 갱신한다 (썸네일은 유지).</summary>
    internal void Update(int index, ImageInfo info)
    {
        Index = index;
        Info = info;
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ToolTip));
    }

    internal void SetThumbnail(Bitmap? bitmap)
    {
        _thumbnail = bitmap;
        IsFailed = bitmap is null;
        OnPropertyChanged(nameof(Thumbnail));
    }

    /// <summary>메모리 확보를 위해 썸네일을 해제한다. 다시 화면에 나타나면 재생성한다.</summary>
    internal void ReleaseThumbnail()
    {
        _thumbnail = null;
        _requested = false;
        OnPropertyChanged(nameof(Thumbnail));
    }

    /// <summary>요청이 처리되지 않고 버려졌을 때 다음 바인딩에서 다시 요청할 수 있게 한다.</summary>
    internal void ResetRequest() => _requested = false;
}
