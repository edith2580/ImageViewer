using ImageMagick;
using ImageMagick.Configuration;
using Microsoft.Extensions.Logging;

namespace ImageViewer.Services;

/// <summary>
/// ImageMagick 보안 정책(policy.xml).
/// ImageMagick 은 확장자가 아니라 파일 내용으로 형식을 판별하고, 형식 판별(Ping) 단계에서 이미 그 형식의 코더가
/// 헤더를 읽는다. 앱 코드의 허용 목록(<see cref="Helpers.ImageFormatHelper.IsAllowedFormat"/>)은 판별 뒤에 적용되므로,
/// 스크립트 · 문서 · 외부 참조를 처리하는 코더와 외부 프로그램(delegate) 실행은 ImageMagick 수준에서 아예 막는다.
/// </summary>
public static class MagickSecurity
{
    /// <summary>ImageMagick 수준에서 막는 코더 (이미지 뷰어가 쓰지 않는, 스크립트 · 문서 · 네트워크 · 화면 입력 형식).</summary>
    internal const string BlockedCoders =
        "PS,PS2,PS3,EPS,EPSF,EPSI,EPI,EPT,EPT2,EPT3,PDF,PDFA,AI,XPS,PCL,PJPEG2000," +
        "SVG,SVGZ,MSVG,RSVG,MVG,MSL,TEXT,TXT,LABEL,CAPTION,PANGO,HTML,SHTML," +
        "HTTP,HTTPS,FTP,URL,EPHEMERAL,INLINE,CLIPBOARD,SCREENSHOT,PRINT,SHOW,WIN,X,VID," +
        "MPEG,MPG,MP4,M2V,M4V,MOV,MKV,AVI,WMV,FLV,WEBM";

    internal static readonly string PolicyXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <policymap>
          <!-- 외부 프로그램(Ghostscript, ffmpeg 등) 실행 금지 -->
          <policy domain="delegate" rights="none" pattern="*" />
          <!-- 스크립트 · 문서 · 네트워크 형식 금지 (형식 판별 단계 포함) -->
          <policy domain="coder" rights="none" pattern="{"{" + BlockedCoders + "}"}" />
          <!-- "@파일" 간접 읽기 금지 -->
          <policy domain="path" rights="none" pattern="@*" />
        </policymap>
        """;

    /// <summary>
    /// ImageMagick 을 보안 정책과 함께 초기화한다. 다른 ImageMagick 호출보다 먼저 불러야 한다.
    /// 정책 파일을 쓸 수 없으면 기록만 하고 넘어간다 (앱 코드의 허용 목록은 그대로 적용된다).
    /// </summary>
    /// <returns>정책을 적용했으면 true.</returns>
    public static bool Initialize(string configDirectory, ILogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(configDirectory);
            var files = ConfigurationFiles.Default;
            files.Policy.Data = PolicyXml;
            MagickNET.Initialize(files, configDirectory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MagickException or InvalidOperationException)
        {
            logger?.LogWarning(ex, "ImageMagick 보안 정책을 적용하지 못했습니다.");
            return false;
        }
    }
}
