using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageMagick;
using ImageViewer.Models;
using SkiaSharp;

namespace ImageViewer.Services;

/// <summary>
/// 애니메이션(GIF / WebP) 원본 데이터와 프레임 정보.
/// 프레임은 미리 모두 풀어 두지 않고 <see cref="AnimationPlayback"/> 이 재생하면서 차례로 디코딩한다.
/// (큰 애니메이션을 한 번에 풀면 수 GB 의 메모리와 수 초의 대기가 필요하다)
/// </summary>
public sealed class AnimationSource
{
    private readonly byte[] _data;

    private AnimationSource(byte[] data, string format, int width, int height, int[] delaysMs)
    {
        _data = data;
        Format = format;
        Width = width;
        Height = height;
        DelaysMs = delaysMs;
    }

    public string Format { get; }

    /// <summary>캔버스(전체 화면) 크기.</summary>
    public int Width { get; }

    public int Height { get; }

    public int FrameCount => DelaysMs.Count;

    /// <summary>각 프레임의 표시 시간(ms). <see cref="NormalizeDelays"/> 로 보정한 값.</summary>
    public IReadOnlyList<int> DelaysMs { get; }

    /// <summary>원본 데이터 크기 (메모리 사용량 추정용).</summary>
    public long DataLength => _data.LongLength;

    /// <summary>GIF 프레임 지연이 이 값 이하이면 100ms 로 표시한다 (브라우저 동작과 같다).</summary>
    internal const int GifMinDelayMs = 10;

    internal const int DefaultDelayMs = 100;

    /// <summary>
    /// 프레임 지연 보정.
    /// - GIF: 0 ~ 10ms 는 100ms (지연 0 으로 최대한 빠르게 깜빡이게 만든 GIF 에 대한 브라우저 공통 규칙).
    /// - WebP: 동영상을 변환한 파일은 30fps 같은 시간을 ms 정수로 나누느라 40, 40, 40, 40, 1, 39 처럼
    ///   1ms 프레임이 섞여 있다. 이를 100ms 로 늘리면 주기적으로 멈칫하므로 그대로 쓴다.
    ///   모든 프레임이 0 ~ 10ms 인 경우에만(지연 정보가 없는 파일) 100ms 로 표시한다.
    /// </summary>
    internal static int[] NormalizeDelays(IReadOnlyList<int> durationsMs, string format)
    {
        if (format == "GIF" || durationsMs.All(d => d <= GifMinDelayMs))
        {
            return durationsMs.Select(d => d <= GifMinDelayMs ? DefaultDelayMs : d).ToArray();
        }

        return durationsMs.Select(d => Math.Max(1, d)).ToArray();
    }

    /// <summary>
    /// 프레임이 2개 이상인 애니메이션이면 정보를 만든다. 애니메이션이 아니거나 이 디코더로 읽을 수 없으면 null.
    /// </summary>
    internal static AnimationSource? TryCreate(byte[] data, string format)
    {
        using var codec = OpenCodec(data);
        if (codec is null || codec.FrameCount < 2)
        {
            return null;
        }

        var delays = NormalizeDelays(codec.FrameInfo.Select(f => f.Duration).ToArray(), format);
        return new AnimationSource(data, format, codec.Info.Width, codec.Info.Height, delays);
    }

    internal SKCodec OpenCodec() => OpenCodec(_data) ?? throw new ImageLoadException("애니메이션을 읽을 수 없습니다.");

    private static SKCodec? OpenCodec(byte[] data) => SKCodec.Create(new MemoryStream(data, writable: false));

    /// <summary>첫 프레임을 정지 이미지로 디코딩한다 (재생을 끈 경우 · 썸네일 · 필터 적용용).</summary>
    internal MagickImage DecodeFirstFrame()
    {
        using var codec = OpenCodec();
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(SKColors.Transparent);
        var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(0));
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new ImageLoadException($"애니메이션 첫 프레임을 읽을 수 없습니다. ({result})");
        }

        var image = new MagickImage();
        image.ReadPixels(bitmap.Bytes, new PixelReadSettings((uint)Width, (uint)Height, StorageType.Char, PixelMapping.BGRA));
        return image;
    }
}

/// <summary>
/// 애니메이션을 재생하면서 백그라운드에서 프레임을 차례로 디코딩한다.
/// - 모든 프레임이 메모리 한도 안에 들어가면 한 번만 디코딩해 보관한다.
/// - 넘으면 현재 재생 위치 앞쪽 일정 개수만 보관하고, 지나간 프레임은 버리며 계속 디코딩한다.
/// 프레임은 회전 · 반전을 적용하고 <see cref="Scale"/> 배율로 줄인 고정(Freeze) 비트맵이다.
/// </summary>
public sealed class AnimationPlayback : IDisposable
{
    /// <summary>디코딩한 프레임을 보관하는 기본 메모리 한도.</summary>
    public const long DefaultBudgetBytes = 512L * 1024 * 1024;

    /// <summary>
    /// 모두 보관할 수 없을 때 재생 위치 앞쪽에 미리 디코딩해 두는 양의 상한.
    /// 디코딩이 재생보다 훨씬 빨라서(1080×1920 WebP 프레임당 약 15ms) 짧은 여유분이면 충분하다.
    /// </summary>
    public const long StreamingBudgetBytes = 128L * 1024 * 1024;

    private const int MinWindow = 4;

    private readonly AnimationSource _source;
    private readonly ImageTransform _transform;
    private readonly int _frameWidth;
    private readonly int _frameHeight;
    private readonly Bitmap?[] _frames;
    private readonly int _window;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource _frameAdded = NewSignal();
    private TaskCompletionSource _positionChanged = NewSignal();
    private int _position;
    private int _cachedCount;
    private Exception? _error;

    /// <param name="scale">원본 대비 디코딩 배율 (1 이하). WebP 만 줄여 읽을 수 있고, GIF 는 원본 크기로 읽는다.</param>
    public AnimationPlayback(AnimationSource source, ImageTransform transform, double scale = 1.0, long budgetBytes = DefaultBudgetBytes)
    {
        _source = source;
        _transform = transform;
        _frames = new Bitmap?[source.FrameCount];

        using (var codec = source.OpenCodec())
        {
            var size = scale < 1.0 ? codec.GetScaledDimensions((float)Math.Max(scale, 0.01)) : codec.Info.Size;
            _frameWidth = Math.Max(1, size.Width);
            _frameHeight = Math.Max(1, size.Height);
        }

        Scale = (double)_frameWidth / source.Width;
        var frameBytes = (long)_frameWidth * _frameHeight * 4;
        FitsInBudget = frameBytes * source.FrameCount <= budgetBytes;
        _window = FitsInBudget
            ? source.FrameCount
            : (int)Math.Clamp(Math.Min(budgetBytes, StreamingBudgetBytes) / Math.Max(1, frameBytes), Math.Min(MinWindow, source.FrameCount), source.FrameCount);

        _ = Task.Factory.StartNew(Produce, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public AnimationSource Source => _source;

    public ImageTransform Transform => _transform;

    /// <summary>실제 디코딩 배율 (원본 대비).</summary>
    public double Scale { get; }

    public int FrameCount => _frames.Length;

    public IReadOnlyList<int> DelaysMs => _source.DelaysMs;

    /// <summary>모든 프레임을 메모리에 보관하는지 (아니면 재생 위치 앞쪽만 보관하며 계속 디코딩한다).</summary>
    public bool FitsInBudget { get; }

    /// <summary>한 번에 보관하는 최대 프레임 수.</summary>
    internal int Window => _window;

    /// <summary>지금 보관 중인 프레임 수 (테스트용).</summary>
    internal int CachedCount
    {
        get
        {
            lock (_lock)
            {
                return _cachedCount;
            }
        }
    }

    /// <summary>프레임이 준비되었으면 반환한다. 아직 디코딩 전이거나 이미 버렸으면 null.</summary>
    public Bitmap? TryGetFrame(int index)
    {
        lock (_lock)
        {
            return (uint)index < (uint)_frames.Length ? _frames[index] : null;
        }
    }

    /// <summary>재생 위치를 알린다. 보관 한도를 넘는 애니메이션은 이 위치를 기준으로 앞쪽 프레임을 디코딩한다.</summary>
    public void SetPosition(int index)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (_position == index)
            {
                return;
            }

            _position = index;
            EvictOutsideWindow();
            signal = _positionChanged;
            _positionChanged = NewSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>프레임이 준비될 때까지 기다린다.</summary>
    public async Task<Bitmap> WaitForFrameAsync(int index, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_frames[index] is { } frame)
                {
                    return frame;
                }

                if (_error is not null)
                {
                    throw new ImageLoadException("애니메이션 프레임을 읽을 수 없습니다.", _error);
                }

                ObjectDisposedException.ThrowIf(_cts.IsCancellationRequested, this);
                signal = _frameAdded.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            Array.Clear(_frames);
            _cachedCount = 0;
        }

        _positionChanged.TrySetResult();
        _frameAdded.TrySetResult();
    }

    private void Produce()
    {
        var ct = _cts.Token;
        try
        {
            using var codec = _source.OpenCodec();
            var frameInfos = codec.FrameInfo;
            var info = new SKImageInfo(_frameWidth, _frameHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKBitmap(info);
            var held = -1; // canvas 에 합성되어 있는 프레임
            var next = 0;

            while (!ct.IsCancellationRequested)
            {
                Task? waitFor = null;
                lock (_lock)
                {
                    if (FitsInBudget && _cachedCount == _frames.Length)
                    {
                        return; // 모두 보관했다
                    }

                    if (!FitsInBudget && Distance(_position, next) >= _window)
                    {
                        waitFor = _positionChanged.Task; // 충분히 앞서 있다: 재생이 따라올 때까지 기다린다
                    }
                }

                if (waitFor is not null)
                {
                    waitFor.Wait(ct);
                    continue;
                }

                var frameInfo = frameInfos[next];
                var required = frameInfo.RequiredFrame;

                // 바로 앞 프레임이 canvas 에 있고 다음 프레임의 바탕으로 쓸 수 있으면 이어서 합성한다.
                // 아니면 디코더가 필요한 앞 프레임부터 다시 합성한다.
                var prior = required >= 0 && held >= required && held < next &&
                            frameInfos[held].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious
                    ? held
                    : -1;
                if (prior < 0)
                {
                    canvas.Erase(SKColors.Transparent);
                }

                var result = codec.GetPixels(info, canvas.GetPixels(), new SKCodecOptions(next, prior));
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    throw new ImageLoadException($"애니메이션 프레임 {next + 1} 을 읽을 수 없습니다. ({result})");
                }

                held = next;
                var bitmap = ToBitmap(canvas, _transform);

                TaskCompletionSource signal;
                lock (_lock)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_frames[next] is null && (FitsInBudget || Distance(_position, next) < _window))
                    {
                        _frames[next] = bitmap;
                        _cachedCount++;
                    }

                    signal = _frameAdded;
                    _frameAdded = NewSignal();
                }

                signal.TrySetResult();
                next = (next + 1) % _frames.Length;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            TaskCompletionSource signal;
            lock (_lock)
            {
                _error = ex;
                signal = _frameAdded;
                _frameAdded = NewSignal();
            }

            signal.TrySetResult();
        }
    }

    /// <summary>재생 위치에서 <paramref name="index"/> 까지 앞으로 몇 프레임인지 (반복 재생 기준).</summary>
    private int Distance(int from, int index) => ((index - from) % _frames.Length + _frames.Length) % _frames.Length;

    private void EvictOutsideWindow()
    {
        if (FitsInBudget)
        {
            return;
        }

        for (var i = 0; i < _frames.Length; i++)
        {
            if (_frames[i] is not null && Distance(_position, i) >= _window)
            {
                _frames[i] = null;
                _cachedCount--;
            }
        }
    }

    /// <summary>합성된 캔버스에 회전 · 반전(시계 방향 회전 후 좌우 반전)을 적용해 Avalonia 비트맵으로 만든다.</summary>
    private static Bitmap ToBitmap(SKBitmap canvas, ImageTransform transform)
    {
        if (transform.IsIdentity)
        {
            return ToAvaloniaBitmap(canvas);
        }

        var (width, height) = transform.SwapsDimensions ? (canvas.Height, canvas.Width) : (canvas.Width, canvas.Height);
        using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var surface = new SKCanvas(target))
        {
            surface.Clear(SKColors.Transparent);
            surface.Translate(width / 2f, height / 2f);
            if (transform.FlipHorizontal)
            {
                surface.Scale(-1, 1);
            }

            surface.RotateDegrees(transform.Rotation);
            surface.Translate(-canvas.Width / 2f, -canvas.Height / 2f);
            surface.DrawBitmap(canvas, 0, 0);
        }

        return ToAvaloniaBitmap(target);
    }

    private static Bitmap ToAvaloniaBitmap(SKBitmap bitmap)
    {
        var result = new WriteableBitmap(
            new PixelSize(bitmap.Width, bitmap.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var fb = result.Lock())
        {
            var srcPtr = bitmap.GetPixels();
            var srcRowBytes = bitmap.RowBytes;
            var copyBytes = Math.Min(srcRowBytes, fb.RowBytes);
            unsafe
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    Buffer.MemoryCopy(
                        (void*)(srcPtr + y * srcRowBytes),
                        (void*)(fb.Address + y * fb.RowBytes),
                        fb.RowBytes,
                        copyBytes);
                }
            }
        }

        return result;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
