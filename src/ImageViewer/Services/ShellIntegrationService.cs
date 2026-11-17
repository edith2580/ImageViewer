using System.Runtime.InteropServices;
using ImageViewer.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ImageViewer.Services;

/// <summary>
/// Windows 탐색기 컨텍스트 메뉴 "ImageViewer에서 보기" 등록/해제.
/// 관리자 권한 없이 현재 사용자(HKCU\Software\Classes)에 등록한다.
/// 설치 프로그램도 이 기능(<c>--register-shell</c> / <c>--unregister-shell</c>)을 실행해 같은 키를 쓴다.
///
/// - 폴더:            Directory\shell\ImageViewer                    ("%1")
/// - 폴더 빈 공간:    Directory\Background\shell\ImageViewer         ("%V")
/// - ZIP/CBZ 파일:    SystemFileAssociations\.zip\shell\ImageViewer  ("%1")
/// - 이미지 파일:     SystemFileAssociations\.jpg\shell\ImageViewer  ("%1") — 지원하는 모든 확장자
///                    (이미지 파일을 열면 그 폴더를 열고 해당 이미지를 선택한다)
///
/// 여러 항목을 선택했을 때 창이 여러 개 뜨지 않도록 단일 선택에서만 메뉴를 보인다.
/// Windows 11 에서는 클래식 메뉴(추가 옵션 표시, Shift+F10)에 표시된다.
/// 메뉴 이름은 "ImageViewer에서 보기(3)" 이고, 메뉴를 연 뒤 3 을 누르면 바로 실행된다.
/// </summary>
public sealed class ShellIntegrationService
{
    public const string VerbName = "ImageViewer";
    public const string MenuText = "ImageViewer에서 보기";

    /// <summary>
    /// 메뉴 단축키. 우클릭(Shift+우클릭) 메뉴를 연 뒤 이 키를 누르면 바로 실행된다.
    /// 다른 메뉴 항목과 겹치기 쉬운 영문자 대신 숫자를 쓴다 (Shift 를 누른 왼손으로 누르기 쉬운 3).
    /// </summary>
    public const char AccessKey = '3';

    /// <summary>레지스트리에 쓰는 메뉴 이름. 탐색기는 "&amp;" 다음 글자를 밑줄 친 단축키로 표시한다.</summary>
    public static string MenuLabel => $"{MenuText}(&{AccessKey})";

    private const string Classes = @"Software\Classes\";

    private readonly ILogger<ShellIntegrationService> _logger;

    public ShellIntegrationService(ILogger<ShellIntegrationService>? logger = null)
    {
        _logger = logger ?? NullLogger<ShellIntegrationService>.Instance;
    }

    /// <summary>등록할 메뉴 키 (HKCU 기준 경로) 와 폴더 빈 공간 여부.</summary>
    internal static IReadOnlyList<(string Path, bool Background)> TargetKeys { get; } = BuildTargetKeys();

    private static List<(string, bool)> BuildTargetKeys()
    {
        var keys = new List<(string, bool)>
        {
            (Classes + @"Directory\shell\" + VerbName, false),
            (Classes + @"Directory\Background\shell\" + VerbName, true),
            (Classes + @"SystemFileAssociations\.zip\shell\" + VerbName, false),
            (Classes + @"SystemFileAssociations\.cbz\shell\" + VerbName, false),
        };

        foreach (var ext in ImageFormatHelper.SupportedExtensions.Order(StringComparer.OrdinalIgnoreCase))
        {
            keys.Add((Classes + @"SystemFileAssociations\" + ext.ToLowerInvariant() + @"\shell\" + VerbName, false));
        }

        return keys;
    }

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ImageViewer.exe");

    public static string BuildCommand(string exePath, bool background) =>
        $"\"{exePath}\" \"{(background ? "%V" : "%1")}\"";

    public bool IsRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TargetKeys[0].Path);
            using var command = key?.OpenSubKey("command");
            var value = command?.GetValue(null) as string;

            // 메뉴 이름이 예전 것(단축키 없음)이면 미등록으로 보고 다시 등록하게 한다.
            return value is not null &&
                   value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                   key?.GetValue(null) as string == MenuLabel;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "컨텍스트 메뉴 등록 상태를 확인하지 못했습니다.");
            return false;
        }
    }

    public void Register()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exe = ExecutablePath;
        foreach (var (path, background) in TargetKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue(null, MenuLabel);
            key.SetValue("Icon", $"\"{exe}\",0");
            key.SetValue("MultiSelectModel", "Single");
            using var command = key.CreateSubKey("command", writable: true);
            command.SetValue(null, BuildCommand(exe, background));
        }

        NotifyShell();
        _logger.LogInformation("탐색기 컨텍스트 메뉴를 등록했습니다. ({Count}개 항목)", TargetKeys.Count);
    }

    public void Unregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var (path, _) in TargetKeys)
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }

        NotifyShell();
        _logger.LogInformation("탐색기 컨텍스트 메뉴를 제거했습니다.");
    }

    /// <summary>Windows 기본 앱 후보로 지금 실행 파일이 등록되어 있는지.</summary>
    public bool IsDefaultAppRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return DefaultAppRegistration.IsRegistered(Registry.CurrentUser, ExecutablePath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "기본 앱 등록 상태를 확인하지 못했습니다.");
            return false;
        }
    }

    /// <summary>Windows 기본 앱 후보로 등록한다 (기본 앱 지정은 사용자가 Windows 설정에서 한다).</summary>
    public void RegisterDefaultApp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DefaultAppRegistration.Register(Registry.CurrentUser, ExecutablePath);
        NotifyShell();
        _logger.LogInformation("Windows 기본 앱 후보로 등록했습니다. ({Count}개 형식)", DefaultAppRegistration.Associations.Count);
    }

    public void UnregisterDefaultApp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DefaultAppRegistration.Unregister(Registry.CurrentUser);
        NotifyShell();
        _logger.LogInformation("Windows 기본 앱 등록을 제거했습니다.");
    }

    private static void NotifyShell()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // 알림 실패는 등록 자체에 영향을 주지 않는다.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
