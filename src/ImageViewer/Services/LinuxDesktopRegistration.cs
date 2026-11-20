using System.Diagnostics;
using Avalonia.Platform;

namespace ImageViewer.Services;

/// <summary>
/// Linux(FreeDesktop) 사용자 데스크톱 엔트리(~/.local/share/applications/ImageViewer.desktop) 등록/해제.
/// 파일 관리자의 "다른 프로그램으로 열기" 권장 목록 및 앱 런처에 ImageViewer 를 표시한다.
/// 관리자 권한 없이 현재 사용자 디렉토리에 기록하며, AppImage 실행 시에는 APPIMAGE 경로를 사용한다.
/// </summary>
public static class LinuxDesktopRegistration
{
    public const string DesktopFileName = "ImageViewer.desktop";
    public const string IconFileName = "ImageViewer.png";

    public const string MimeTypes =
        "image/jpeg;image/png;image/gif;image/webp;image/bmp;image/tiff;" +
        "image/heic;image/heif;image/avif;image/x-icon;image/jxl;image/jp2;" +
        "image/x-tga;image/vnd.adobe.photoshop;image/x-portable-pixmap;" +
        "image/x-portable-graymap;image/x-portable-bitmap;image/x-portable-anymap;" +
        "image/x-dcraw;application/zip;application/vnd.comicbook+zip;inode/directory;";

    public static string GetExecutablePath()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage))
        {
            return appImage;
        }

        return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ImageViewer");
    }

    public static string GetDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return xdg;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share");
    }

    public static string GetDesktopFilePath(string? dataHome = null) =>
        Path.Combine(dataHome ?? GetDataHome(), "applications", DesktopFileName);

    public static string GetIconFilePath(string? dataHome = null) =>
        Path.Combine(dataHome ?? GetDataHome(), "icons", "hicolor", "256x256", "apps", IconFileName);

    public static bool IsRegistered(string? exePath = null, string? dataHome = null)
    {
        var desktopPath = GetDesktopFilePath(dataHome);
        if (!File.Exists(desktopPath))
        {
            return false;
        }

        var targetExe = exePath ?? GetExecutablePath();
        var content = File.ReadAllText(desktopPath);
        return content.Contains(BuildExecLine(targetExe), StringComparison.Ordinal);
    }

    public static void Register(string? exePath = null, string? dataHome = null)
    {
        var baseDir = dataHome ?? GetDataHome();
        var targetExe = exePath ?? GetExecutablePath();

        var appsDir = Path.Combine(baseDir, "applications");
        Directory.CreateDirectory(appsDir);

        TryWriteIcon(GetIconFilePath(baseDir));

        var desktopContent = BuildDesktopEntry(targetExe);
        File.WriteAllText(GetDesktopFilePath(baseDir), desktopContent);

        TryRunTool("update-desktop-database", appsDir);
        TryRunTool("gtk-update-icon-cache", "-f", "-t", Path.Combine(baseDir, "icons", "hicolor"));
    }

    public static void Unregister(string? dataHome = null)
    {
        var baseDir = dataHome ?? GetDataHome();
        var desktopPath = GetDesktopFilePath(baseDir);
        if (File.Exists(desktopPath))
        {
            File.Delete(desktopPath);
        }

        var iconPath = GetIconFilePath(baseDir);
        if (File.Exists(iconPath))
        {
            File.Delete(iconPath);
        }

        TryRunTool("update-desktop-database", Path.Combine(baseDir, "applications"));
    }

    internal static string BuildExecLine(string exePath) =>
        $"Exec=\"{exePath.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" %F";

    internal static string BuildDesktopEntry(string exePath) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=ImageViewer\n" +
        "Comment=Cross-platform image viewer for folders and ZIP archives\n" +
        BuildExecLine(exePath) + "\n" +
        "Icon=ImageViewer\n" +
        "Terminal=false\n" +
        "Categories=Graphics;Viewer;\n" +
        $"MimeType={MimeTypes}\n";

    private static void TryWriteIcon(string iconPath)
    {
        try
        {
            var iconDir = Path.GetDirectoryName(iconPath);
            if (!string.IsNullOrEmpty(iconDir))
            {
                Directory.CreateDirectory(iconDir);
            }

            var uri = new Uri("avares://ImageViewer/Resources/ImageViewer.png");
            if (AssetLoader.Exists(uri))
            {
                using var src = AssetLoader.Open(uri);
                using var dst = File.Create(iconPath);
                src.CopyTo(dst);
            }
        }
        catch (Exception)
        {
            // 아이콘 복사 실패가 .desktop 등록을 막지 않도록 무시한다.
        }
    }

    private static void TryRunTool(string fileName, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            process?.WaitForExit(3000);
        }
        catch (Exception)
        {
            // 배포판에 도구가 없을 수 있으므로 무시한다.
        }
    }
}
