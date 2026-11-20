using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.ViewModels;

public sealed record OptionItem<T>(T Value, string Label);

/// <summary>설정 대화 상자. 확인을 누르면 <see cref="ApplyTo"/> 로 반영한다.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellIntegrationService _shell;
    private readonly IDialogService _dialogs;

    public SettingsViewModel(ViewerSettings settings, ShellIntegrationService shell, IDialogService dialogs)
    {
        _shell = shell;
        _dialogs = dialogs;

        _mouseWheel = settings.Display.MouseWheel;
        _background = settings.Display.Background;
        _wrapAround = settings.Navigation.WrapAround;
        _watchFolder = settings.Navigation.WatchFolder;
        _slideShowRepeat = settings.Slideshow.Repeat;
        _thumbnailVisible = settings.Thumbnail.Visible;
        _thumbnailSize = settings.Thumbnail.Size;
        _thumbnailScrollBar = settings.Thumbnail.ShowScrollBar;
        _autoCheckUpdates = settings.Update.AutoCheck;
        _smoothSigma = settings.Filter.SmoothSigma;
        _sharpenSigma = settings.Filter.SharpenSigma;
        _sharpenAmount = settings.Filter.SharpenAmount;
        _maxImageMegapixels = settings.Limits.MaxImageMegapixels;
        _isShellRegistered = shell.IsRegistered();
    }

    public IReadOnlyList<OptionItem<MouseWheelAction>> MouseWheelOptions { get; } =
    [
        new(MouseWheelAction.Navigate, "위: 이전 / 아래: 다음 이미지 (Ctrl+휠: 확대/축소)"),
        new(MouseWheelAction.Zoom, "확대 / 축소 (Ctrl+휠: 이전/다음 이미지)"),
    ];

    public IReadOnlyList<OptionItem<ViewerBackground>> BackgroundOptions { get; } =
    [
        new(ViewerBackground.Black, "검정"),
        new(ViewerBackground.DarkGray, "어두운 회색"),
        new(ViewerBackground.Gray, "회색"),
        new(ViewerBackground.White, "흰색"),
        new(ViewerBackground.Checkerboard, "체크무늬 (투명 영역 확인)"),
    ];

    public bool IsWindows { get; } = OperatingSystem.IsWindows();

    public bool SupportsShellIntegration { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public string ShellSectionTitle => OperatingSystem.IsLinux()
        ? "Linux 데스크톱 연동 (연결 프로그램)"
        : "Windows 탐색기 연동";

    public string ShellSectionDescription => OperatingSystem.IsLinux()
        ? "이미지 · ZIP · 폴더의 '다른 프로그램으로 열기' 권장 목록과 앱 런처에 ImageViewer를 등록합니다. (~/.local/share/applications)"
        : "폴더와 ZIP 파일의 우클릭 메뉴에 'ImageViewer에서 보기'를 추가합니다. Windows 11 에서는 '추가 옵션 표시'(Shift+F10) 메뉴에 나타납니다.";

    [ObservableProperty] private MouseWheelAction _mouseWheel;
    [ObservableProperty] private ViewerBackground _background;
    [ObservableProperty] private bool _wrapAround;
    [ObservableProperty] private bool _watchFolder;
    [ObservableProperty] private bool _slideShowRepeat;
    [ObservableProperty] private bool _thumbnailVisible;
    [ObservableProperty] private int _thumbnailSize;
    [ObservableProperty] private bool _thumbnailScrollBar;
    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private double _smoothSigma;
    [ObservableProperty] private double _sharpenSigma;
    [ObservableProperty] private double _sharpenAmount;
    [ObservableProperty] private int _maxImageMegapixels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShellStatusText))]
    private bool _isShellRegistered;

    public string ShellStatusText => IsShellRegistered
        ? "현재 사용자에 등록되어 있습니다."
        : "등록되어 있지 않습니다.";

    /// <summary>
    /// 설정 창 아래 [기본 앱 설정]: ImageViewer 를 Windows 기본 앱 후보로 등록하고 기본 앱 설정 화면을 연다.
    /// (Windows 는 앱이 스스로 기본 앱이 되는 것을 막으므로, 형식 선택은 사용자가 그 화면에서 한다)
    /// </summary>
    [RelayCommand]
    private void OpenDefaultApps()
    {
        if (!_shell.IsDefaultAppRegistered())
        {
            if (!_dialogs.Confirm(
                    "ImageViewer 를 Windows 기본 앱 목록에 등록하고 [설정 > 앱 > 기본 앱] 을 엽니다.\n" +
                    "열리는 화면에서 ImageViewer 를 기본값으로 설정하거나, .jpg · .png 등 형식별로 선택해 주세요.\n" +
                    "(현재 사용자 레지스트리에 등록, 관리자 권한 불필요)\n\n계속하시겠습니까?"))
            {
                return;
            }

            try
            {
                _shell.RegisterDefaultApp();
            }
            catch (Exception ex)
            {
                _dialogs.ShowMessage("기본 앱 목록에 등록하지 못했습니다.\n" + ex.Message, isError: true);
                return;
            }
        }

        try
        {
            DefaultAppRegistration.OpenSettings();
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("Windows 기본 앱 설정을 열지 못했습니다.\n" + ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void RegisterShell()
    {
        var prompt = OperatingSystem.IsLinux()
            ? "현재 사용자의 데스크톱 엔트리(~/.local/share/applications/ImageViewer.desktop)를 등록합니다.\n" +
              "파일 관리자의 '다른 프로그램으로 열기' 권장 목록과 앱 런처에 ImageViewer가 표시됩니다.\n\n계속하시겠습니까?"
            : "폴더 및 ZIP 파일의 탐색기 우클릭 메뉴에 'ImageViewer에서 보기'를 추가합니다.\n" +
              "(현재 사용자 레지스트리 HKCU\\Software\\Classes 에 등록)\n\n계속하시겠습니까?";

        if (!_dialogs.Confirm(prompt))
        {
            return;
        }

        try
        {
            _shell.Register();
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("연동 등록에 실패했습니다.\n" + ex.Message, isError: true);
        }

        IsShellRegistered = _shell.IsRegistered();
    }

    [RelayCommand]
    private void UnregisterShell()
    {
        try
        {
            _shell.Unregister();
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("연동 제거에 실패했습니다.\n" + ex.Message, isError: true);
        }

        IsShellRegistered = _shell.IsRegistered();
    }

    public void ApplyTo(ViewerSettings settings)
    {
        settings.Display.MouseWheel = MouseWheel;
        settings.Display.Background = Background;
        settings.Navigation.WrapAround = WrapAround;
        settings.Navigation.WatchFolder = WatchFolder;
        settings.Slideshow.Repeat = SlideShowRepeat;
        settings.Thumbnail.Visible = ThumbnailVisible;
        settings.Thumbnail.Size = ThumbnailSize;
        settings.Thumbnail.ShowScrollBar = ThumbnailScrollBar;
        settings.Update.AutoCheck = AutoCheckUpdates;
        settings.Filter.SmoothSigma = SmoothSigma;
        settings.Filter.SharpenSigma = SharpenSigma;
        settings.Filter.SharpenAmount = SharpenAmount;
        settings.Limits.MaxImageMegapixels = MaxImageMegapixels;
        settings.Normalize();
    }
}
