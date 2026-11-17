using System.Diagnostics;
using Microsoft.Win32;

namespace ImageViewer.Services.Updates;

/// <summary>지금 실행 중인 ImageViewer 가 어떻게 설치되었는지.</summary>
public enum InstallKind
{
    /// <summary>설치 프로그램으로 설치 (%LOCALAPPDATA%\Programs\ImageViewer 등): 새 설치 파일을 조용히 실행해 업데이트.</summary>
    Installed,

    /// <summary>단일 실행 파일(포터블): 같은 폴더에 새 exe 를 받아 바꿔 끼운다.</summary>
    Portable,

    /// <summary>개발 빌드 등: 자동 설치하지 않고 다운로드 페이지만 연다.</summary>
    Unsupported,
}

/// <summary>
/// 받은 업데이트를 적용한다.
///
/// 설치 버전: 앱이 종료된 뒤 "setup.exe /S /RELAUNCH=경로" 를 실행한다. 설치 프로그램은 앱이 완전히 끝나기를
///           기다렸다가 덮어쓰고, 끝나면 앱을 다시 실행한다 (현재 사용자 설치라 관리자 권한 창이 뜨지 않는다).
/// 포터블:   실행 중인 exe 는 덮어쓸 수 없지만 이름은 바꿀 수 있다.
///           ImageViewer.exe → ImageViewer.exe.old, 받은 ImageViewer.exe.new → ImageViewer.exe 로 바꾼 뒤
///           새 exe 를 실행하고, 새 exe 가 시작하면서 .old 를 지운다. 파일 이름 · 경로가 그대로라
///           바로 가기와 탐색기 메뉴 등록이 그대로 동작한다.
/// </summary>
public static class UpdateInstaller
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ImageViewer";
    public const string NewSuffix = ".new";
    public const string OldSuffix = ".old";

    public static string CurrentExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ImageViewer.exe");

    public static InstallKind DetectCurrent() =>
        OperatingSystem.IsWindows()
            ? Detect(CurrentExePath, ReadInstallLocation(), File.Exists(Path.Combine(AppContext.BaseDirectory, "ImageViewer.dll")))
            : InstallKind.Unsupported;

    /// <param name="exePath">실행 중인 exe.</param>
    /// <param name="installLocation">설치 프로그램이 기록한 설치 폴더 (없으면 null).</param>
    /// <param name="hasSeparateAppDll">exe 옆에 ImageViewer.dll 이 있으면 단일 파일이 아니다 (설치본 또는 개발 빌드).</param>
    internal static InstallKind Detect(string exePath, string? installLocation, bool hasSeparateAppDll)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        if (!string.IsNullOrWhiteSpace(installLocation) && SameDirectory(dir, installLocation))
        {
            return InstallKind.Installed;
        }

        return hasSeparateAppDll ? InstallKind.Unsupported : InstallKind.Portable;
    }

    private static bool SameDirectory(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b.Trim('"'))),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? ReadInstallLocation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>설치 파일을 내려받을 위치 (%TEMP%\ImageViewer-update\파일명).</summary>
    public static string InstallerDownloadPath(UpdatePackage package)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ImageViewer-update");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Path.GetFileName(package.Asset.Name));
    }

    /// <summary>포터블 새 exe 를 받을 위치 (실행 파일 옆 ImageViewer.exe.new). 폴더에 쓸 수 없으면 <see cref="UpdateException"/>.</summary>
    public static string PortableDownloadPath(string exePath)
    {
        var target = exePath + NewSuffix;
        try
        {
            using (File.Create(target, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(
                $"실행 파일이 있는 폴더에 쓸 수 없어 자동으로 업데이트할 수 없습니다.\n{Path.GetDirectoryName(exePath)}", ex);
        }

        return target;
    }

    /// <summary>설치 파일 실행 정보 (앱 종료 뒤 실행한다).</summary>
    public static ProcessStartInfo InstallerStartInfo(string setupPath, string? relaunchPath)
    {
        // NSIS 는 /S 를 직접 해석하고, /RELAUNCH= 값은 다음 " /" 앞까지 읽는다 (따옴표 없이 넘긴다).
        var args = "/S /RELAUNCH";
        if (!string.IsNullOrEmpty(relaunchPath) && !relaunchPath.Contains(" /") && !relaunchPath.Contains('"'))
        {
            args += "=" + relaunchPath;
        }

        return new ProcessStartInfo(setupPath, args) { UseShellExecute = false };
    }

    /// <summary>
    /// 포터블 exe 를 바꿔 끼운다. 실패하면 원래 상태로 되돌리고 예외를 던진다.
    /// </summary>
    public static void SwapPortable(string exePath, string newExePath)
    {
        var old = exePath + OldSuffix;
        try
        {
            if (File.Exists(old))
            {
                File.Delete(old);
            }

            File.Move(exePath, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("실행 파일을 바꾸지 못했습니다. 같은 ImageViewer 가 여러 개 실행 중이면 모두 닫고 다시 시도해 주세요.", ex);
        }

        try
        {
            File.Move(newExePath, exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(old, exePath); // 되돌리기
            throw new UpdateException("새 실행 파일로 바꾸지 못했습니다.", ex);
        }
    }

    /// <summary>포터블 업데이트 뒤 다시 실행할 정보.</summary>
    public static ProcessStartInfo RelaunchStartInfo(string exePath, string? path)
    {
        var info = new ProcessStartInfo(exePath) { UseShellExecute = false };
        if (!string.IsNullOrEmpty(path))
        {
            info.ArgumentList.Add(path);
        }

        return info;
    }

    /// <summary>
    /// 포터블 업데이트로 남은 ImageViewer.exe.old / 받다 만 .new 를 지운다.
    /// 이전 프로세스가 아직 끝나는 중일 수 있으므로 몇 초 동안 다시 시도한다.
    /// </summary>
    public static async Task CleanupLeftoversAsync(string exePath, int attempts = 20)
    {
        foreach (var path in new[] { exePath + OldSuffix, exePath + NewSuffix, exePath + NewSuffix + ".part" })
        {
            for (var i = 0; i < attempts && File.Exists(path); i++)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }
    }
}
