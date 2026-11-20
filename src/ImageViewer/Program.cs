using Avalonia;
using Avalonia.Media;

namespace ImageViewer;

internal static class Program
{
    private const string EmbeddedKoreanFont = "avares://ImageViewer/Resources/Fonts#NanumGothic";

    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        if (OperatingSystem.IsLinux() &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            builder = builder.UseWayland();
        }

        return builder
            .With(new FontManagerOptions
            {
                DefaultFamilyName = OperatingSystem.IsWindows() ? "Segoe UI" : EmbeddedKoreanFont,
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily(EmbeddedKoreanFont) },
                    new FontFallback { FontFamily = new FontFamily("Malgun Gothic") },
                    new FontFallback { FontFamily = new FontFamily("Apple SD Gothic Neo") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans CJK KR") }
                ]
            })
            .LogToTrace();
    }
}
