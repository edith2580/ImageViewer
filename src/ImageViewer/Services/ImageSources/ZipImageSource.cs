using System.Globalization;
using System.Text;
using ImageViewer.Helpers;
using ImageViewer.Models;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ImageViewer.Services.ImageSources;

/// <summary>
/// ZIP 내부 이미지 소스. 압축을 해제하지 않고 항목 스트림을 읽는다.
/// 아카이브와 파일 스트림의 수명은 이 객체가 관리하며,
/// 단일 파일 스트림을 공유하므로 항목 읽기는 직렬화한 뒤 메모리 스트림으로 복사해 반환한다.
/// </summary>
public sealed class ZipImageSource : IImageSource
{
    /// <summary>항목 하나를 담을 수 있는 최대 크기 (메모리 스트림 · 배열 한도, 약 2GB).</summary>
    internal static readonly long MaxEntryBytesLimit = Array.MaxLength;

    /// <summary>
    /// 메모리 스트림을 미리 잡아 두는 최대 크기. 헤더의 크기 값은 ZIP 을 만든 쪽이 마음대로 적을 수 있으므로
    /// 그만큼 한 번에 잡지 않고, 이보다 큰 항목은 읽으면서 늘린다.
    /// </summary>
    internal const int MaxInitialCapacity = 16 * 1024 * 1024;

    private readonly string _zipPath;
    private readonly long _maxEntryBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _fileStream;
    private ZipArchive? _archive;
    private Dictionary<string, IArchiveEntry>? _entries;
    private bool _disposed;

    static ZipImageSource()
    {
        // ZIP 파일명 기본 인코딩(OEM 코드 페이지, 한국어 Windows 는 CP949)을 사용하기 위해 등록
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ZipImageSource(string zipPath, long maxEntryBytes = 512L * 1024 * 1024)
    {
        _zipPath = zipPath;
        _maxEntryBytes = Math.Clamp(maxEntryBytes, 1, MaxEntryBytesLimit);
        Info = new ImageSourceInfo(zipPath, ImageSourceType.Zip);
    }

    public ImageSourceInfo Info { get; }

    public async Task<IReadOnlyList<ImageInfo>> GetImagesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadEntries(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<ImageInfo> ReadEntries(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOpen();

        var list = new List<ImageInfo>();
        _entries = new Dictionary<string, IArchiveEntry>(StringComparer.Ordinal);

        try
        {
            foreach (var entry in _archive!.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                {
                    continue;
                }

                var key = entry.Key.Replace('\\', '/');
                var name = key[(key.LastIndexOf('/') + 1)..];

                // macOS 리소스 포크 등 숨김 항목 제외
                if (name.StartsWith("._", StringComparison.Ordinal) ||
                    key.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
                    !ImageFormatHelper.IsSupportedExtension(name))
                {
                    continue;
                }

                if (!_entries.TryAdd(key, entry))
                {
                    continue; // 중복 항목은 첫 번째만 사용
                }

                var supported = true;
                string? reason = null;
                if (entry.IsEncrypted)
                {
                    supported = false;
                    reason = "암호로 보호된 ZIP 항목은 지원하지 않습니다.";
                }
                else if (entry.Size > _maxEntryBytes)
                {
                    supported = false;
                    reason = $"ZIP 항목이 너무 큽니다 ({FileSizeHelper.Format(entry.Size)}).";
                }

                list.Add(new ImageInfo(
                    name,
                    ImageSourceType.Zip,
                    _zipPath,
                    key,
                    entry.Size,
                    entry.LastModifiedTime,
                    supported,
                    reason));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ImageSourceException)
        {
            throw new ImageSourceException("ZIP 파일이 손상되었거나 지원하지 않는 형식입니다.", ex);
        }

        // 폴더 구조를 유지하도록 내부 전체 경로 기준으로 정렬
        list.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.EntryPath, b.EntryPath));
        return list;
    }

    private void EnsureOpen()
    {
        if (_archive is not null)
        {
            return;
        }

        if (!File.Exists(_zipPath))
        {
            throw new ImageSourceException("ZIP 파일을 찾을 수 없습니다.");
        }

        try
        {
            _fileStream = new FileStream(_zipPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var options = new ReaderOptions
            {
                LeaveStreamOpen = true,
                ArchiveEncoding = new ArchiveEncoding { Default = oem },
            };
            _archive = ZipArchive.Open(_fileStream, options);
        }
        catch (UnauthorizedAccessException ex)
        {
            CloseArchive();
            throw new ImageSourceException("ZIP 파일에 접근할 권한이 없습니다.", ex);
        }
        catch (Exception ex)
        {
            CloseArchive();
            throw new ImageSourceException("ZIP 파일을 열 수 없습니다. 손상되었거나 지원하지 않는 형식입니다.", ex);
        }
    }

    public async Task<Stream> OpenImageAsync(ImageInfo image, CancellationToken cancellationToken)
    {
        if (image.EntryPath is null)
        {
            throw new ArgumentException("ZIP 항목 경로가 없습니다.", nameof(image));
        }

        if (!image.IsSupported)
        {
            throw new ImageSourceException(image.UnsupportedReason ?? "지원하지 않는 항목입니다.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run<Stream>(() =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries is null)
                {
                    ReadEntries(cancellationToken);
                }

                if (!_entries!.TryGetValue(image.EntryPath, out var entry))
                {
                    throw new ImageSourceException("ZIP 항목을 찾을 수 없습니다.");
                }

                try
                {
                    using var source = entry.OpenEntryStream();
                    var memory = new MemoryStream(InitialCapacity(entry.Size, _maxEntryBytes));
                    CopyWithLimit(source, memory, _maxEntryBytes, cancellationToken);
                    memory.Position = 0;
                    return memory;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ImageSourceException)
                {
                    throw;
                }
                catch (CryptographicException ex)
                {
                    throw new ImageSourceException("암호로 보호된 ZIP 항목은 지원하지 않습니다.", ex);
                }
                catch (Exception ex)
                {
                    throw new ImageSourceException("ZIP 항목을 읽을 수 없습니다. 손상되었을 수 있습니다.", ex);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>헤더에 적힌 크기로 정하는 메모리 스트림 초기 크기 (최대 <see cref="MaxInitialCapacity"/>).</summary>
    internal static int InitialCapacity(long declaredSize, long maxEntryBytes) =>
        (int)Math.Clamp(declaredSize, 0, Math.Min(maxEntryBytes, MaxInitialCapacity));

    /// <summary>헤더의 크기 정보를 신뢰하지 않고 실제 읽은 바이트 수로 상한을 검증한다.</summary>
    private static void CopyWithLimit(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            if (total > limit)
            {
                throw new ImageSourceException("ZIP 항목의 압축 해제 크기가 허용 한도를 초과했습니다.");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private void CloseArchive()
    {
        _archive?.Dispose();
        _archive = null;
        _fileStream?.Dispose();
        _fileStream = null;
        _entries = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 진행 중인 읽기가 끝날 때까지 기다린 뒤 닫는다.
        _gate.Wait();
        try
        {
            _disposed = true;
            CloseArchive();
        }
        finally
        {
            _gate.Release();
        }
    }
}
