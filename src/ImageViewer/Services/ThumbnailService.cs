using Avalonia.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services.ImageSources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageViewer.Services;

/// <summary>
/// 하단 네비게이션용 썸네일을 백그라운드에서 생성한다.
/// 요청은 LIFO 로 처리해 가장 최근에 화면에 나타난 항목을 먼저 만든다.
/// 소스가 바뀌면 <see cref="CancelAll"/> 로 대기 중인 요청을 폐기한다.
/// </summary>
public sealed class ThumbnailService
{
    private const int MaxWorkers = 2;
    private const int MaxPending = 400;

    private readonly ILogger<ThumbnailService> _logger;
    private readonly SettingsService _settings;
    private readonly List<ThumbnailRequest> _pending = [];
    private readonly Lock _lock = new();
    private int _generation;
    private int _workers;

    public ThumbnailService(SettingsService settings, ILogger<ThumbnailService>? logger = null)
    {
        _settings = settings;
        _logger = logger ?? NullLogger<ThumbnailService>.Instance;
    }

    private sealed record ThumbnailRequest(int Generation, IImageSource Source, ImageInfo Info, uint PixelSize, Action<Bitmap?, bool> Callback);

    /// <summary>
    /// 썸네일 생성을 요청한다. 콜백은 백그라운드 스레드에서 (비트맵, 처리 완료 여부)로 호출된다.
    /// 처리 완료 여부가 false 이면 요청이 버려진 것이므로 다시 요청할 수 있다.
    /// </summary>
    public void Request(IImageSource source, ImageInfo info, uint pixelSize, Action<Bitmap?, bool> callback)
    {
        lock (_lock)
        {
            _pending.Add(new ThumbnailRequest(_generation, source, info, pixelSize, callback));
            if (_pending.Count > MaxPending)
            {
                // 가장 오래된(화면에서 이미 벗어났을 가능성이 높은) 요청을 버린다.
                var dropped = _pending[0];
                _pending.RemoveAt(0);
                dropped.Callback(null, false);
            }

            if (_workers < MaxWorkers)
            {
                _workers++;
                _ = Task.Run(WorkerLoop);
            }
        }
    }

    public void CancelAll()
    {
        lock (_lock)
        {
            _generation++;
            _pending.Clear();
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            ThumbnailRequest request;
            int generation;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _workers--;
                    return;
                }

                request = _pending[^1];
                _pending.RemoveAt(_pending.Count - 1);
                generation = _generation;
            }

            if (request.Generation != generation)
            {
                continue;
            }

            Bitmap? result = null;
            try
            {
                result = Create(request);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "썸네일 생성 실패: {Name}", request.Info.Name);
            }

            lock (_lock)
            {
                if (request.Generation != _generation)
                {
                    continue;
                }
            }

            request.Callback(result, true);
        }
    }

    private Bitmap? Create(ThumbnailRequest request)
    {
        if (!request.Info.IsSupported)
        {
            return null;
        }

        using var stream = ImageLoaderService
            .OpenSeekableAsync(request.Source, request.Info, CancellationToken.None)
            .GetAwaiter().GetResult();

        var maxPixels = (long)_settings.Current.Limits.MaxImageMegapixels * 1_000_000;
        var (image, _) = ImageLoaderService.Decode(stream, maxPixels, request.PixelSize * 2, request.Info.Name);
        using (image)
        {
            image.Thumbnail(new ImageMagick.MagickGeometry(request.PixelSize, request.PixelSize));
            return ImageRenderService.ToBitmap(image);
        }
    }
}
