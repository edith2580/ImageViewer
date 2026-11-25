using ImageMagick;

namespace ImageViewer.Helpers;

/// <summary>
/// 지원 확장자 목록과 파일 헤더 검사를 담당한다.
/// </summary>
public static class ImageFormatHelper
{
    /// <summary>
    /// 목록에 포함할 이미지 확장자.
    /// SVG/PDF/PS 계열은 보안 및 렌더링 일관성 검증 전까지 1차 버전에서 제외한다.
    /// </summary>
    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif",
            ".png", ".apng",
            ".bmp", ".dib",
            ".gif",
            ".tif", ".tiff",
            ".webp",
            ".heic", ".heif", ".avif",
            ".ico", ".cur",
            ".jxl", ".jp2", ".j2k",
            ".tga", ".psd", ".dds", ".pcx", ".qoi",
            ".ppm", ".pgm", ".pbm", ".pnm",
            ".hdr", ".exr",
            ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
        };

    /// <summary>
    /// 디코딩을 허용하는 형식 (허용 목록). 파일 내용으로 판별한 형식이 여기에 없으면 확장자와 관계없이 거부한다.
    /// <see cref="SupportedExtensions"/> 의 이미지 형식과, ImageMagick 이 같은 형식을 부르는 다른 이름(변형)만 넣는다.
    /// (이름이 바뀐 MagickFormat 값이 있어도 빌드가 깨지지 않도록 이름으로 찾는다)
    /// </summary>
    public static IReadOnlySet<MagickFormat> AllowedFormats { get; } = BuildAllowedFormats(
        // JPEG
        "Jpeg", "Jpg", "Jpe", "Pjpeg", "Jfif",
        // PNG · APNG
        "Png", "Png8", "Png24", "Png32", "Png48", "Png64", "Png00", "APng",
        // BMP
        "Bmp", "Bmp2", "Bmp3", "Dib",
        // GIF
        "Gif", "Gif87",
        // TIFF
        "Tif", "Tiff", "Tiff64", "Ptif",
        // WebP · HEIF · AVIF · JPEG XL · JPEG 2000
        "WebP", "Heic", "Heif", "Avif", "Jxl", "Jp2", "J2k", "J2c", "Jpc", "Jpt",
        // 아이콘 · 기타 래스터
        "Ico", "Icon", "Cur", "Tga", "Icb", "Vda", "Vst", "Psd", "Psb", "Dds", "Dxt1", "Dxt5",
        "Pcx", "Dcx", "Qoi", "Ppm", "Pgm", "Pbm", "Pnm", "Pam", "Hdr", "Exr",
        // 카메라 RAW (libraw)
        "Dng", "Cr2", "Cr3", "Crw", "Nef", "Nrw", "Arw", "Sr2", "Srf", "Srw", "Orf", "Rw2", "Raf",
        "Pef", "Dcr", "Kdc", "K25", "Mef", "Mrw", "Erf", "ThreeFr", "Rmf", "Raw");

    private static HashSet<MagickFormat> BuildAllowedFormats(params string[] names)
    {
        var set = new HashSet<MagickFormat>();
        foreach (var name in names)
        {
            if (Enum.TryParse<MagickFormat>(name, ignoreCase: true, out var format))
            {
                set.Add(format);
            }
        }

        return set;
    }

    /// <summary>파일 내용으로 판별한 형식을 디코딩해도 되는지.</summary>
    public static bool IsAllowedFormat(MagickFormat format) => AllowedFormats.Contains(format);

    public static bool IsSupportedExtension(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
    }

    public static bool IsZipFile(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 텍스트(XML/SVG/MSL), PostScript, PDF 로 보이는 헤더를 감지한다.
    /// 확장자가 이미지라도 이런 내용이면 ImageMagick 에 넘기지 않는다.
    /// </summary>
    public static bool LooksLikeScriptOrDocument(ReadOnlySpan<byte> header)
    {
        var span = header;

        // UTF-8 BOM
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        var i = 0;
        while (i < span.Length && (span[i] == (byte)' ' || span[i] == (byte)'\t' ||
                                   span[i] == (byte)'\r' || span[i] == (byte)'\n'))
        {
            i++;
        }

        span = span[i..];
        if (span.IsEmpty)
        {
            return false;
        }

        return span[0] == (byte)'<' ||
               span.StartsWith("%!"u8) ||
               span.StartsWith("%PDF"u8) ||
               span.StartsWith("push "u8) ||
               span.StartsWith("viewbox"u8);
    }
}
