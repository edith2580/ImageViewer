using ImageMagick;
using ImageViewer.Helpers;
using ImageViewer.Models;
using ImageViewer.Services.ImageSources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageViewer.Services;

/// <summary>디코딩 실패 등 사용자에게 안내할 이미지 오류.</summary>
public sealed class ImageLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 화면 표시에 필요한 크기만큼만 디코딩하기 위한 정보 (장치 픽셀 기준 표시 영역과 표시 모드).
/// JPEG 은 디코딩 단계에서 1/2 · 1/4 · 1/8 등으로 줄여 읽을 수 있어 큰 사진을 훨씬 빨리 연다.
/// 원본 크기 보기 · 확대 등 더 큰 해상도가 필요하면 원본을 다시 디코딩한다.
/// </summary>
public sealed record DecodeTarget(DisplayMode Mode, double ViewportPixelWidth, double ViewportPixelHeight)
{
    /// <summary>
    /// 원본(방향 반영) 크기가 <paramref name="width"/>×<paramref name="height"/> 인 이미지를 표시하는 데 필요한 크기.
    /// 원본에 가까운 크기가 필요하면(축소 효과가 작으면) null = 원본 디코딩.
    /// </summary>
    public (uint Width, uint Height)? RequiredSize(uint width, uint height)
    {
        if (width == 0 || height == 0 || ViewportPixelWidth <= 0 || ViewportPixelHeight <= 0)
        {
            return null;
        }

        var scale = ImageResizeService.GetBaseScale(Mode, width, height, ViewportPixelWidth, ViewportPixelHeight);
        if (scale >= 0.9)
        {
            return null;
        }

        return ((uint)Math.Ceiling(width * scale), (uint)Math.Ceiling(height * scale));
    }

    /// <summary>이미 디코딩된 이미지가 이 표시 조건에 충분한 해상도인지.</summary>
    public bool IsSatisfiedBy(DecodedImage image)
    {
        if (!image.IsReduced)
        {
            return true;
        }

        return RequiredSize(image.OriginalWidth, image.OriginalHeight) is { } required &&
               image.Width >= required.Width && image.Height >= required.Height;
    }
}

/// <summary>디코딩 결과. 애니메이션이면 <see cref="Animation"/>, 다중 페이지면 <see cref="PageCount"/> 가 1보다 크다.</summary>
public sealed class DecodeResult(MagickImage image, string format)
{
    public MagickImage Image { get; } = image;

    public string Format { get; } = format;

    /// <summary>원본(방향 반영) 크기. 화면 크기로 줄여 디코딩했으면 <see cref="Image"/> 보다 크다.</summary>
    public uint OriginalWidth { get; init; } = image.Width;

    public uint OriginalHeight { get; init; } = image.Height;

    public int PageIndex { get; init; }

    public int PageCount { get; init; } = 1;

    /// <summary>애니메이션(GIF / WebP)이면 프레임을 재생하면서 디코딩할 원본. <see cref="Image"/> 는 첫 프레임.</summary>
    public AnimationSource? Animation { get; init; }

    public IReadOnlyList<MetadataItem> Exif { get; init; } = [];

    /// <summary>EXIF 외의 메타 정보 (PNG 텍스트 청크, 주석, IPTC, XMP 등).</summary>
    public IReadOnlyList<MetadataItem> Metadata { get; init; } = [];

    public void Deconstruct(out MagickImage image, out string format)
    {
        image = Image;
        format = Format;
    }
}

/// <summary>
/// 디코딩된 원본 이미지 (EXIF 방향 반영, sRGB 변환).
/// 다중 페이지 이미지는 페이지 하나, 애니메이션은 첫 프레임과 재생용 원본 데이터(<see cref="Animation"/>)를 가진다.
/// 캐시와 화면이 동시에 참조할 수 있으므로 참조 카운트로 해제 시점을 관리한다.
/// </summary>
public sealed class DecodedImage
{
    private readonly MagickImage _image;
    private readonly Lock _lock = new();
    private int _refCount = 1;

    internal DecodedImage(ImageInfo info, DecodeResult result)
    {
        Info = info;
        _image = result.Image;
        Animation = result.Animation;
        Width = _image.Width;
        Height = _image.Height;
        OriginalWidth = Math.Max(result.OriginalWidth, Width);
        OriginalHeight = Math.Max(result.OriginalHeight, Height);
        Format = result.Format;
        PageIndex = result.PageIndex;
        PageCount = result.PageCount;
        FrameCount = Animation?.FrameCount ?? 1;
        FrameDelaysMs = Animation?.DelaysMs ?? [];
        Exif = result.Exif;
        Metadata = result.Metadata;
    }

    public ImageInfo Info { get; }

    /// <summary>디코딩된 픽셀 크기 (화면 크기로 줄여 읽었으면 원본보다 작다).</summary>
    public uint Width { get; }

    public uint Height { get; }

    /// <summary>원본 이미지 크기 (EXIF 방향 반영). 배율 계산과 정보 표시에 쓴다.</summary>
    public uint OriginalWidth { get; }

    public uint OriginalHeight { get; }

    /// <summary>화면 표시용으로 원본보다 작게 디코딩했는지.</summary>
    public bool IsReduced => Width < OriginalWidth || Height < OriginalHeight;

    public string Format { get; }

    /// <summary>다중 페이지 이미지(TIFF)의 현재 페이지 (0부터).</summary>
    public int PageIndex { get; }

    public int PageCount { get; }

    public int FrameCount { get; }

    public bool IsAnimated => FrameCount > 1;

    /// <summary>애니메이션 재생용 원본 (프레임은 <see cref="AnimationPlayback"/> 이 재생하며 디코딩한다).</summary>
    public AnimationSource? Animation { get; }

    /// <summary>애니메이션 각 프레임의 표시 시간(ms). 애니메이션이 아니면 비어 있다.</summary>
    public IReadOnlyList<int> FrameDelaysMs { get; }

    public IReadOnlyList<MetadataItem> Exif { get; }

    /// <summary>EXIF 외의 메타 정보 (PNG 텍스트 청크, 주석, IPTC, XMP 등).</summary>
    public IReadOnlyList<MetadataItem> Metadata { get; }

    /// <summary>
    /// 렌더링 캐시 키 (이미지 + 페이지 + 디코딩 해상도).
    /// 줄여 읽은 결과와 원본 결과를 구분해, 원본으로 바꾼 뒤 흐린 렌더링이 재사용되지 않게 한다.
    /// </summary>
    public string Key => ImageLoaderService.CacheKey(Info, PageIndex) + (IsReduced ? $"~{Width}x{Height}" : string.Empty);

    /// <summary>메모리 사용량 추정치 (8bit BGRA 기준 + 애니메이션 원본 데이터).</summary>
    public long Cost => (long)Width * Height * 4 + (Animation?.DataLength ?? 0);

    public bool IsDisposed
    {
        get
        {
            lock (_lock)
            {
                return _refCount <= 0;
            }
        }
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            if (_refCount <= 0)
            {
                return false;
            }

            _refCount++;
            return true;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_refCount <= 0)
            {
                return;
            }

            if (--_refCount == 0)
            {
                _image.Dispose();
            }
        }
    }

    /// <summary>
    /// 작업용 복제본을 만든다. ImageMagick 의 복제는 픽셀 캐시를 공유(copy-on-write)하므로 가볍다.
    /// 원본이 이미 해제되었으면 null.
    /// </summary>
    public MagickImage? CloneImage()
    {
        lock (_lock)
        {
            return _refCount <= 0 ? null : (MagickImage)_image.Clone();
        }
    }
}

/// <summary>
/// Magick.NET 으로 이미지를 디코딩한다. 원본 크기의 디코딩 결과는 크기 제한 LRU 캐시에 보관한다.
/// </summary>
public sealed class ImageLoaderService : IDisposable
{
    private readonly ILogger<ImageLoaderService> _logger;
    private readonly SettingsService _settings;
    private readonly LruCache<string, DecodedImage> _cache;

    // 화면에 바로 보일 이미지(전경)와 미리 읽기(백그라운드)는 서로 기다리지 않도록 대기열을 나눈다.
    // 전경은 2개까지 동시에 처리해, 빠르게 넘길 때 이미 지나간 이미지의 디코딩이 새 이미지를 막지 않게 한다.
    // (ImageMagick 자체가 내부적으로 멀티스레드로 처리한다)
    private readonly PriorityGate _foregroundGate = new(capacity: 2, burstCapacity: 4);
    private readonly SemaphoreSlim _backgroundGate = new(2, 2);
    private readonly SharedWork<string, DecodedImage> _decodes = new();

    public ImageLoaderService(SettingsService settings, ILogger<ImageLoaderService>? logger = null)
    {
        _settings = settings;
        _logger = logger ?? NullLogger<ImageLoaderService>.Instance;
        _cache = new LruCache<string, DecodedImage>(
            maxCount: 8,
            maxCost: 1024L * 1024 * 1024,
            costOf: d => d.Cost,
            onEvicted: (_, d) => d.Release());
    }

    private long MaxPixels => (long)_settings.Current.Limits.MaxImageMegapixels * 1_000_000;

    /// <summary>애니메이션 · 다중 페이지 이미지의 최대 프레임 수.</summary>
    public const int MaxFrames = 1000;

    internal static string CacheKey(ImageInfo info, int pageIndex) => $"{info.Key}#{pageIndex}";

    public Task<DecodedImage> LoadAsync(IImageSource source, ImageInfo info, CancellationToken cancellationToken) =>
        LoadAsync(source, info, 0, target: null, cancellationToken);

    public Task<DecodedImage> LoadAsync(IImageSource source, ImageInfo info, int pageIndex, CancellationToken cancellationToken) =>
        LoadAsync(source, info, pageIndex, target: null, cancellationToken);

    /// <summary>
    /// 이미지(다중 페이지면 지정한 페이지)를 디코딩해 반환한다.
    /// <paramref name="target"/> 이 있으면 화면 표시에 필요한 크기까지만 디코딩할 수 있다 (JPEG).
    /// 캐시에 원본이 있거나 충분히 큰 결과가 있으면 그것을 돌려주고,
    /// 같은 디코딩이 진행 중이면(미리 읽기 등) 그 결과를 함께 기다린다.
    /// 반환된 객체는 호출자가 사용 후 <see cref="DecodedImage.Release"/> 해야 한다.
    /// </summary>
    /// <param name="background">미리 읽기 등 화면에 바로 필요하지 않은 작업.</param>
    public async Task<DecodedImage> LoadAsync(
        IImageSource source,
        ImageInfo info,
        int pageIndex,
        DecodeTarget? target,
        CancellationToken cancellationToken,
        bool background = false)
    {
        var fullKey = CacheKey(info, pageIndex);
        var reducedKey = fullKey + ReducedSuffix;
        var requestKey = target is null
            ? fullKey
            : $"{reducedKey}|{target.Mode}|{target.ViewportPixelWidth:0}x{target.ViewportPixelHeight:0}";

        for (var attempt = 0; ; attempt++)
        {
            if (TryGetSuitable(fullKey, reducedKey, target, out var cached))
            {
                return cached;
            }

            var decoded = await _decodes.RunAsync(
                requestKey,
                () => DecodeAndCacheAsync(source, info, pageIndex, target, background, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            // 결과는 캐시가 소유한다. 호출자 몫의 참조를 얻는다 (그 사이 캐시에서 밀려나 해제되었으면 다시 읽는다).
            if (decoded.TryAcquire())
            {
                return decoded;
            }

            if (attempt >= 2)
            {
                throw new ObjectDisposedException(nameof(DecodedImage));
            }
        }
    }

    private async Task<DecodedImage> DecodeAndCacheAsync(
        IImageSource source,
        ImageInfo info,
        int pageIndex,
        DecodeTarget? target,
        bool background,
        CancellationToken cancellationToken)
    {
        // 전경 작업은 이미 지나간 이미지의 작업이 자리를 막고 있으면 넘어서 바로 시작한다 (PriorityGate).
        var slot = background
            ? await _backgroundGate.EnterAsync(cancellationToken).ConfigureAwait(false)
            : await _foregroundGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fullKey = CacheKey(info, pageIndex);
            if (TryGetSuitable(fullKey, fullKey + ReducedSuffix, target, out var cached))
            {
                cached.Release(); // 캐시가 가진 참조만 남긴다
                return cached;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 시작한 디코딩은 끝까지 수행한다 (함께 기다리는 다른 요청이 결과를 쓸 수 있도록).
            var decoded = await Task.Run(async () =>
            {
                await using var stream = await OpenSeekableAsync(source, info, CancellationToken.None).ConfigureAwait(false);
                var result = Decode(stream, MaxPixels, thumbnailHint: null, info.Name, pageIndex, target);
                return new DecodedImage(info, result);
            }).ConfigureAwait(false);

            // 새로 만든 참조는 캐시가 소유한다.
            // (범위를 벗어난 페이지를 요청했으면 실제 페이지 키로 저장된다)
            _cache.Add(CacheKey(info, decoded.PageIndex) + (decoded.IsReduced ? ReducedSuffix : string.Empty), decoded);
            return decoded;
        }
        finally
        {
            slot.Dispose();
        }
    }

    private const string ReducedSuffix = "~r";

    /// <summary>원본이 캐시에 있으면 원본을, 없으면 이 표시 조건에 충분한 축소 결과를 찾는다.</summary>
    private bool TryGetSuitable(string fullKey, string reducedKey, DecodeTarget? target, out DecodedImage decoded)
    {
        if (TryGetCached(fullKey, out decoded))
        {
            return true;
        }

        if (target is not null && TryGetCached(reducedKey, out decoded))
        {
            if (target.IsSatisfiedBy(decoded))
            {
                return true;
            }

            decoded.Release();
        }

        decoded = null!;
        return false;
    }

    private bool TryGetCached(string key, out DecodedImage decoded)
    {
        if (_cache.TryGet(key, out decoded!) && decoded.TryAcquire())
        {
            return true;
        }

        decoded = null!;
        return false;
    }

    public void ClearCache() => _cache.Clear();

    /// <summary>파일이 바뀌었을 때 해당 이미지의 모든 페이지를 캐시에서 제거한다.</summary>
    public void Invalidate(ImageInfo info)
    {
        var prefix = info.Key + "#";
        _cache.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    public void Dispose() => _cache.Clear();

    internal static async Task<Stream> OpenSeekableAsync(IImageSource source, ImageInfo info, CancellationToken cancellationToken)
    {
        if (!info.IsSupported)
        {
            throw new ImageLoadException(info.UnsupportedReason ?? "지원하지 않는 항목입니다.");
        }

        Stream stream;
        try
        {
            stream = await source.OpenImageAsync(info, cancellationToken).ConfigureAwait(false);
        }
        catch (ImageSourceException ex)
        {
            throw new ImageLoadException(ex.Message, ex);
        }

        if (stream.CanSeek)
        {
            return stream;
        }

        await using (stream)
        {
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            memory.Position = 0;
            return memory;
        }
    }

    /// <summary>
    /// 공통 디코딩 절차.
    /// 1) 헤더 검사로 스크립트/문서 포맷 거부 2) Ping 으로 포맷·픽셀 수 검증
    /// 3) GIF / WebP 애니메이션은 첫 프레임만 디코딩 (나머지는 재생하며 디코딩), 다중 페이지 TIFF 는 페이지 수 확인
    /// 4) 디코딩 5) EXIF 읽기 · 방향 반영 6) sRGB 변환
    /// 내용으로 포맷을 판별할 수 없는 경우(ICO, TGA, RAW 등)에는 파일 확장자를 힌트로 다시 시도한다.
    /// 썸네일(<paramref name="thumbnailHint"/>)은 항상 첫 프레임 / 첫 페이지만 읽는다.
    /// </summary>
    internal static DecodeResult Decode(
        Stream stream,
        long maxPixels,
        uint? thumbnailHint,
        string? fileName = null,
        int pageIndex = 0,
        DecodeTarget? target = null)
    {
        Span<byte> header = stackalloc byte[64];
        var read = stream.Read(header);
        stream.Position = 0;
        if (read == 0)
        {
            throw new ImageLoadException("빈 파일입니다.");
        }

        if (ImageFormatHelper.LooksLikeScriptOrDocument(header[..read]))
        {
            throw new ImageLoadException("지원하지 않는 형식입니다. (텍스트/벡터/문서 형식)");
        }

        var settings = new MagickReadSettings
        {
            FrameIndex = 0,
            FrameCount = 1,
        };

        if (thumbnailHint is { } hint)
        {
            // JPEG 은 디코딩 단계에서 축소해 썸네일 생성 속도를 높인다.
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{hint}x{hint}");
        }

        MagickFormat format;
        uint originalWidth, originalHeight;
        try
        {
            MagickImageInfo info;
            try
            {
                info = new MagickImageInfo(stream, settings);
            }
            catch (MagickMissingDelegateErrorException) when (GetFormatHint(fileName) is { } hinted)
            {
                stream.Position = 0;
                settings.Format = hinted;
                info = new MagickImageInfo(stream, settings);
            }

            stream.Position = 0;
            format = info.Format;

            if (ImageFormatHelper.BlockedFormats.Contains(format))
            {
                throw new ImageLoadException($"지원하지 않는 형식입니다. ({format})");
            }

            // EXIF 방향이 90° 회전(5~8)이면 화면 기준 폭 · 높이가 바뀐다.
            var swapped = (int)info.Orientation is >= 5 and <= 8;
            (originalWidth, originalHeight) = swapped ? (info.Height, info.Width) : (info.Width, info.Height);

            // JPEG: 화면에 필요한 크기만큼만 디코딩 (libjpeg 의 1/2 · 1/4 · 1/8 등 축소 디코딩)
            if (thumbnailHint is null && target?.RequiredSize(originalWidth, originalHeight) is { } required &&
                format is MagickFormat.Jpeg or MagickFormat.Jpg or MagickFormat.Jpe or MagickFormat.Pjpeg &&
                JpegScaleHint(info.Width, info.Height, swapped ? (required.Height, required.Width) : (required.Width, required.Height)) is { } sizeHint)
            {
                settings.SetDefine(MagickFormat.Jpeg, "size", $"{sizeHint.Width}x{sizeHint.Height}");
            }

            var pixels = (long)info.Width * info.Height;
            if (info.Width == 0 || info.Height == 0)
            {
                throw new ImageLoadException("이미지 크기 정보를 읽을 수 없습니다.");
            }

            if (pixels > maxPixels)
            {
                throw new ImageLoadException(
                    $"이미지가 너무 큽니다. ({info.Width:N0} × {info.Height:N0}, 허용 한도 {maxPixels / 1_000_000:N0} MP)");
            }
        }
        catch (MagickException ex)
        {
            throw new ImageLoadException(DescribeMagickError(ex), ex);
        }

        var formatName = format.ToString().ToUpperInvariant();
        var animated = formatName is "GIF" or "WEBP";
        var paged = formatName is "TIFF" or "TIF" or "TIFF64" or "PTIF";

        // 애니메이션: 첫 프레임만 바로 디코딩하고, 나머지는 재생하면서 차례로 디코딩한다 (AnimationPlayback).
        // ImageMagick 은 첫 프레임만 요청해도 모든 프레임을 풀기 때문에 큰 애니메이션에서 수 초 · 수 GB 가 든다.
        if (animated && TryReadAnimation(stream, formatName, keepAnimation: thumbnailHint is null) is { } animation)
        {
            return animation;
        }

        var frameCount = 1;
        if (thumbnailHint is null && paged)
        {
            frameCount = CountFrames(stream, settings.Format);
        }

        var page = paged ? Math.Clamp(pageIndex, 0, frameCount - 1) : 0;
        settings.FrameIndex = (uint)page;

        MagickImage? image = null;
        try
        {
            if (format is MagickFormat.Ico or MagickFormat.Cur)
            {
                image = ReadLargestFrame(stream, settings.Format);
            }
            else
            {
                image = new MagickImage(stream, settings);
            }

            var exif = thumbnailHint is null ? ExifReader.Read(image) : [];
            var metadata = thumbnailHint is null ? MetadataReader.Read(image, exif) : [];
            image.AutoOrient();
            NormalizeColor(image);
            return new DecodeResult(image, formatName)
            {
                OriginalWidth = Math.Max(originalWidth, image.Width),
                OriginalHeight = Math.Max(originalHeight, image.Height),
                PageIndex = page,
                PageCount = paged ? frameCount : 1,
                Exif = exif,
                Metadata = metadata,
            };
        }
        catch (MagickException ex)
        {
            image?.Dispose();
            throw new ImageLoadException(DescribeMagickError(ex), ex);
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// libjpeg 축소 디코딩 비율(n/8)을 필요한 크기 이상이 되도록 고르고, 그 비율이 나오게 하는 "jpeg:size" 값을 만든다.
    /// ImageMagick 은 요청 크기 / 원본 크기 × 8 을 내림해 n 을 정하므로, 요청 크기를 그대로 넘기면
    /// 필요한 크기보다 작게(예: 1920→1280 요청 시 5/8 = 1200) 디코딩된다. 축소 효과가 없으면(n = 8) null.
    /// </summary>
    internal static (uint Width, uint Height)? JpegScaleHint(uint storedWidth, uint storedHeight, (uint Width, uint Height) required)
    {
        if (storedWidth == 0 || storedHeight == 0)
        {
            return null;
        }

        var ratio = Math.Max((double)required.Width / storedWidth, (double)required.Height / storedHeight);
        var n = (int)Math.Ceiling(ratio * 8 - 1e-9);
        if (n >= 8)
        {
            return null;
        }

        n = Math.Max(1, n);

        // 내림했을 때 정확히 n 이 되도록 n/8 크기보다 1px 크게 요청한다.
        return ((uint)Math.Ceiling(storedWidth * n / 8.0) + 1, (uint)Math.Ceiling(storedHeight * n / 8.0) + 1);
    }

    /// <summary>Ping 으로 프레임(페이지) 수를 센다. 실패하면 1.</summary>
    private static int CountFrames(Stream stream, MagickFormat? formatHint)
    {
        try
        {
            var probe = new MagickReadSettings();
            if (formatHint is { } f)
            {
                probe.Format = f;
            }

            return Math.Clamp(MagickImageInfo.ReadCollection(stream, probe).Take(MaxFrames).Count(), 1, MaxFrames);
        }
        catch (MagickException)
        {
            return 1;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    /// <summary>
    /// GIF / WebP 가 애니메이션(프레임 2개 이상)이면 첫 프레임과 재생용 원본을 만든다.
    /// 애니메이션이 아니거나 읽을 수 없으면 null (ImageMagick 으로 다시 읽는다).
    /// </summary>
    private static DecodeResult? TryReadAnimation(Stream stream, string formatName, bool keepAnimation)
    {
        stream.Position = 0;
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        stream.Position = 0;

        try
        {
            if (AnimationSource.TryCreate(data, formatName) is not { } animation)
            {
                return null;
            }

            var first = animation.DecodeFirstFrame();
            return new DecodeResult(first, formatName)
            {
                Animation = keepAnimation ? animation : null,
            };
        }
        catch (ImageLoadException)
        {
            return null;
        }
    }

    private static MagickFormat? GetFormatHint(string? fileName)
    {
        var ext = Path.GetExtension(fileName)?.TrimStart('.');
        if (string.IsNullOrEmpty(ext) || !ImageFormatHelper.IsSupportedExtension(fileName))
        {
            return null;
        }

        return Enum.TryParse<MagickFormat>(ext, ignoreCase: true, out var format) &&
               !ImageFormatHelper.BlockedFormats.Contains(format)
            ? format
            : null;
    }

    private static MagickImage ReadLargestFrame(Stream stream, MagickFormat? format)
    {
        var settings = new MagickReadSettings();
        if (format is { } f)
        {
            settings.Format = f;
        }

        using var collection = new MagickImageCollection(stream, settings);
        var best = collection.OrderByDescending(f => (long)f.Width * f.Height).First();
        return new MagickImage(best);
    }

    private static void NormalizeColor(MagickImage image)
    {
        try
        {
            var profile = image.GetColorProfile();
            if (image.ColorSpace == ColorSpace.CMYK)
            {
                if (profile is null)
                {
                    image.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
                }
                else
                {
                    image.TransformColorSpace(ColorProfiles.SRGB);
                }
            }
            else if (profile is not null && profile.ColorSpace == ColorSpace.sRGB &&
                     !string.Equals(profile.Description, ColorProfiles.SRGB.Description, StringComparison.Ordinal))
            {
                // 광색역 RGB 프로파일(Adobe RGB, Display P3 등)을 sRGB 로 변환
                image.TransformColorSpace(ColorProfiles.SRGB);
            }

            if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.Gray or ColorSpace.RGB or ColorSpace.Transparent))
            {
                image.ColorSpace = ColorSpace.sRGB;
            }
        }
        catch (MagickException)
        {
            // 색상 변환 실패는 표시를 막지 않는다.
        }
    }

    internal static string DescribeMagickError(MagickException ex) => ex switch
    {
        MagickCorruptImageErrorException => "이미지 파일이 손상되었습니다.",
        MagickMissingDelegateErrorException => "지원하지 않는 이미지 형식이거나 코덱이 없습니다.",
        MagickResourceLimitErrorException => "이미지를 처리하기 위한 메모리가 부족합니다.",
        MagickCoderErrorException => "이미지를 디코딩할 수 없습니다.",
        _ => "이미지를 읽을 수 없습니다.",
    };
}
