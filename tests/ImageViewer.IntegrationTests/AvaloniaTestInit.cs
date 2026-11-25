using System.Runtime.CompilerServices;
using Avalonia.Skia;
using ImageViewer.Services;

namespace ImageViewer.IntegrationTests;

internal static class AvaloniaTestInit
{
    /// <summary>테스트 프로세스에 적용한 ImageMagick 보안 정책이 실제로 적용되었는지.</summary>
    internal static bool MagickPolicyApplied { get; private set; }

    [ModuleInitializer]
    internal static void Init()
    {
        SkiaPlatform.Initialize();

        // 앱과 같은 보안 정책으로 ImageMagick 을 초기화해, 모든 통합 테스트가 정책 아래에서 실행되게 한다.
        MagickPolicyApplied = MagickSecurity.Initialize(
            Path.Combine(Path.GetTempPath(), "iv-test-magick-" + Environment.ProcessId));
    }
}
