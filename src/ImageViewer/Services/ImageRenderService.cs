using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageMagick;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>
/// 디코딩된 이미지를 화면 표시 크기로 변환하고 필터 · 회전 · 반전을 적용한 뒤 Avalonia Bitmap 으로 만든다.
/// 결과는 (이미지, 크기, 필터, 변환) 단위로 LRU 캐시한다. 모든 처리는 UI 스레드 밖에서 수행한다.
/// </summary>
public sealed class ImageRenderService(ImageFilterService filterService)
{
    private readonly record struct RenderKey(string ImageKey, uint Width, uint Height, ImageFilter Filter, ImageTransform Transform);

    private readonly LruCache<RenderKey, Bitmap> _cache = new(
        maxCount: 16,
        maxCost: 384L * 1024 * 1024,
        costOf: b => (long)b.PixelSize.Width * b.PixelSize.Height * 4);

    // 화면에 바로 보일 렌더링(전경)과 미리 읽기(백그라운드)는 서로 기다리지 않도록 대기열을 나눈다.
    // 전경은 2개까지 동시에 처리해, 빠르게 넘길 때 이미 지나간 이미지의 렌더링이 새 이미지를 막지 않게 한다.
    private readonly PriorityGate _foregroundGate = new(capacity: 2, burstCapacity: 4);
    private readonly SemaphoreSlim _backgroundGate = new(2, 2);
    private readonly SharedWork<RenderKey, Bitmap> _renders = new();

    /// <summary>
    /// 정지 이미지를 렌더링한다. <paramref name="width"/> · <paramref name="height"/> 는 회전 후(화면 기준) 크기.
    /// 같은 렌더링이 진행 중이면(미리 읽기 등) 그 결과를 함께 기다린다.
    /// </summary>
    /// <param name="background">미리 읽기 등 화면에 바로 필요하지 않은 작업.</param>
    public Task<Bitmap> RenderAsync(
        DecodedImage image,
        uint width,
        uint height,
        ImageFilter filter,
        ImageTransform transform,
        CancellationToken cancellationToken,
        bool background = false)
    {
        var key = new RenderKey(image.Key, width, height, filter, transform);
        if (_cache.TryGet(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        return _renders.RunAsync(key, () => RenderCoreAsync(image, key, background, cancellationToken), cancellationToken);
    }

    private async Task<Bitmap> RenderCoreAsync(DecodedImage image, RenderKey key, bool background, CancellationToken cancellationToken)
    {
        // 전경 작업은 이미 지나간 이미지의 작업이 자리를 막고 있으면 넘어서 바로 시작한다 (PriorityGate).
        var slot = background
            ? await _backgroundGate.EnterAsync(cancellationToken).ConfigureAwait(false)
            : await _foregroundGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(key, out var cached))
            {
                return cached;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 시작한 렌더링은 끝까지 수행한다 (함께 기다리는 다른 요청이 결과를 쓸 수 있도록).
            var bitmap = await Task.Run(() =>
            {
                using var work = image.CloneImage() ?? throw new ObjectDisposedException(nameof(DecodedImage));

                // 회전 전 기준 크기로 먼저 축소한 뒤 회전한다 (픽셀 수가 적을 때 회전하는 편이 빠르다).
                var (targetW, targetH) = key.Transform.SwapsDimensions ? (key.Height, key.Width) : (key.Width, key.Height);
                if (NeedsCpuResize(work.Width, work.Height, targetW, targetH))
                {
                    work.Resize(new MagickGeometry(targetW, targetH) { IgnoreAspectRatio = true }, FilterType.Triangle);
                }

                // 필터 강도는 화면 픽셀 기준: CPU 로 줄이지 않은 경우 WPF 가 확대 · 축소하는 비율을 반영한다.
                var displayScale = work.Width > 0 ? (double)targetW / work.Width : 1.0;
                filterService.Apply(work, key.Filter, displayScale);
                key.Transform.Apply(work);
                return ToBitmap(work);
            }).ConfigureAwait(false);

            _cache.Add(key, bitmap);
            return bitmap;
        }
        finally
        {
            slot.Dispose();
        }
    }

    /// <summary>화면 크기보다 이 비율 이상이면 CPU 로 줄이지 않고 화면 표시 단계(WPF 고품질 스케일링)에 맡긴다.</summary>
    internal const double GpuScaleThreshold = 0.6;

    /// <summary>
    /// CPU(ImageMagick) 축소가 필요한지. ImageMagick 축소는 단일 스레드라 1920×1080 → 1280×720 에도 100ms 이상 걸린다.
    /// 화면 크기로 줄여 디코딩한 JPEG 처럼 원본이 화면보다 조금만 크면 WPF 가 표시하면서 고품질로 줄이게 하고,
    /// 크게 줄여야 할 때(60% 미만)만 CPU 로 줄인다. 확대는 항상 화면에서 처리한다.
    /// </summary>
    internal static bool NeedsCpuResize(uint sourceWidth, uint sourceHeight, uint targetWidth, uint targetHeight)
    {
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            return false;
        }

        var ratio = Math.Max((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        return ratio < GpuScaleThreshold;
    }

    /// <summary>파일이 바뀌었을 때 해당 이미지의 렌더링 결과를 버린다.</summary>
    public void Invalidate(ImageInfo info)
    {
        var prefix = info.Key + "#";
        _cache.RemoveWhere(k => k.ImageKey.StartsWith(prefix, StringComparison.Ordinal));
    }

    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// MagickImage 의 픽셀을 BGRA 8bit 배열로 복사해 Avalonia Bitmap 을 만든다.
    /// 반환된 비트맵은 자체 버퍼를 가지므로 MagickImage 를 해제해도 안전하다.
    /// </summary>
    public static Bitmap ToBitmap(MagickImage image)
    {
        var width = (int)image.Width;
        var height = (int)image.Height;
        using var pixels = image.GetPixelsUnsafe();
        var bytes = pixels.ToByteArray(PixelMapping.BGRA)
                    ?? throw new InvalidOperationException("픽셀 데이터를 읽을 수 없습니다.");

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using (var fb = bitmap.Lock())
        {
            var srcStride = width * 4;
            if (fb.RowBytes == srcStride)
            {
                Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(bytes, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
                }
            }
        }

        return bitmap;
    }
}
