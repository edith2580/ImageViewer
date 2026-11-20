using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageViewer.Services;
using ImageViewer.Services.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageViewer.ViewModels;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Installing,
    Failed,
}

/// <summary>
/// 업데이트 확인 · 알림 · 설치. 메인 창 오른쪽 위 알림과 정보 창이 같은 인스턴스를 쓴다.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    /// <summary>자동 확인 간격.</summary>
    internal static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(20);

    private readonly UpdateService _service;
    private readonly SettingsService _settings;
    private readonly IAppLifetime _lifetime;
    private readonly ILogger<UpdateViewModel> _logger;
    private readonly CancellationTokenSource _cts = new();
    private ReleaseInfo? _release;

    public UpdateViewModel(
        UpdateService service,
        SettingsService settings,
        IAppLifetime lifetime,
        ILogger<UpdateViewModel>? logger = null,
        InstallKind? kind = null,
        string? exePath = null)
    {
        _service = service;
        _settings = settings;
        _lifetime = lifetime;
        _logger = logger ?? NullLogger<UpdateViewModel>.Instance;
        Kind = kind ?? UpdateInstaller.DetectCurrent();
        ExePath = exePath ?? UpdateInstaller.CurrentExePath;
    }

    public InstallKind Kind { get; }

    public string ExePath { get; }

    /// <summary>업데이트 뒤 다시 열 경로 (보고 있던 이미지 또는 ZIP). 메인 뷰 모델이 지정한다.</summary>
    public Func<string?>? RelaunchPathProvider { get; set; }

    public string CurrentVersionText => _service.CurrentVersion.ToString(3);

    public string KindText => Kind switch
    {
        InstallKind.Installed => "설치 버전",
        InstallKind.Portable => "포터블",
        _ => "수동 설치 (자동 업데이트 미지원)",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsBusy), nameof(IsDownloading), nameof(CanInstall), nameof(InstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(InstallCommand))]
    private UpdateState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _progress;

    /// <summary>메인 창 오른쪽 위 알림 표시 여부.</summary>
    [ObservableProperty]
    private bool _isNoticeVisible;

    public string? AvailableVersionText => _release?.Version.ToString(3);

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    public bool IsDownloading => State == UpdateState.Downloading;

    /// <summary>알림 · 정보 창의 [업데이트] 버튼 표시 여부.</summary>
    public bool CanInstall => _release is not null && State is UpdateState.Available or UpdateState.Failed;

    public string InstallButtonText => Kind == InstallKind.Unsupported || (State == UpdateState.Failed && _release is not null)
        ? "다운로드 페이지"
        : "지금 업데이트";

    public string StatusText => State switch
    {
        UpdateState.Checking => "새 버전을 확인하는 중…",
        UpdateState.UpToDate => $"최신 버전입니다. ({CurrentVersionText})",
        UpdateState.Available => $"새 버전 {AvailableVersionText} 을(를) 설치할 수 있습니다. (현재 {CurrentVersionText})",
        UpdateState.Downloading => $"새 버전 {AvailableVersionText} 을(를) 받는 중… {Progress:0}%",
        UpdateState.Installing => Kind == InstallKind.Installed
            ? "설치 프로그램을 실행합니다. 설치가 끝나면 ImageViewer 가 다시 시작됩니다."
            : "새 버전으로 다시 시작합니다.",
        UpdateState.Failed => ErrorMessage ?? "업데이트하지 못했습니다.",
        _ => $"현재 버전 {CurrentVersionText}",
    };

    /// <summary>
    /// 시작할 때 자동 확인. 설정에서 껐거나 최근에 확인했으면 건너뛴다.
    /// 새 버전이 있고 사용자가 건너뛴 버전이 아니면 알림을 띄운다. 오류는 기록만 한다.
    /// </summary>
    public async Task AutoCheckAsync(TimeSpan delay, DateTimeOffset? now = null)
    {
        var update = _settings.Current.Update;
        var time = now ?? DateTimeOffset.UtcNow;
        if (!update.AutoCheck || (update.LastCheckUtc is { } last && time - last < AutoCheckInterval && time >= last))
        {
            return;
        }

        try
        {
            await Task.Delay(delay, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (State != UpdateState.Idle)
        {
            return; // 그사이 직접 확인을 눌렀다
        }

        var found = await CheckCoreAsync(quiet: true);
        update.LastCheckUtc = time;
        _settings.Save();

        if (found && !string.Equals(update.SkippedVersion, AvailableVersionText, StringComparison.Ordinal))
        {
            IsNoticeVisible = true;
        }
    }

    private bool CanCheckNow() => !IsBusy;

    /// <summary>정보 창의 [업데이트 확인]: 건너뛴 버전도 다시 알린다.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        if (await CheckCoreAsync(quiet: false))
        {
            IsNoticeVisible = true;
        }
    }

    /// <returns>새 버전이 있으면 true.</returns>
    private async Task<bool> CheckCoreAsync(bool quiet)
    {
        State = UpdateState.Checking;
        ErrorMessage = null;
        try
        {
            var release = await _service.GetLatestReleaseAsync(_cts.Token);
            _release = release is not null && release.Version > _service.CurrentVersion ? release : null;
            OnPropertyChanged(nameof(AvailableVersionText));
            State = _release is null ? UpdateState.UpToDate : UpdateState.Available;
            _logger.LogInformation("업데이트 확인: 현재 {Current}, 최신 {Latest}", CurrentVersionText, release?.Tag ?? "(없음)");
            return _release is not null;
        }
        catch (UpdateException ex)
        {
            _logger.LogWarning(ex, "업데이트 확인 실패");
            ErrorMessage = ex.Message;
            State = quiet ? UpdateState.Idle : UpdateState.Failed;
            return false;
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Idle;
            return false;
        }
    }

    private bool CanInstallNow() => CanInstall;

    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private async Task InstallAsync()
    {
        var release = _release;
        if (release is null)
        {
            return;
        }

        // 개발 빌드이거나 이전 시도가 실패했으면 다운로드 페이지를 연다.
        if (Kind == InstallKind.Unsupported || State == UpdateState.Failed)
        {
            OpenReleasePage();
            return;
        }

        IsNoticeVisible = true;
        Progress = 0;
        State = UpdateState.Downloading;
        try
        {
            var package = await _service.ResolvePackageAsync(release, Kind, _cts.Token);
            var progress = new Progress<double>(p => Progress = p * 100);
            var relaunch = SafeRelaunchPath();

            if (Kind == InstallKind.Installed)
            {
                var setup = UpdateInstaller.InstallerDownloadPath(package);
                await _service.DownloadAsync(package, setup, progress, _cts.Token);
                State = UpdateState.Installing;
                _lifetime.ExitAndRun(UpdateInstaller.InstallerStartInfo(setup, relaunch));
            }
            else
            {
                var target = UpdateInstaller.PortableDownloadPath(ExePath);
                await _service.DownloadAsync(package, target, progress, _cts.Token);
                UpdateInstaller.SwapPortable(ExePath, target);
                State = UpdateState.Installing;
                _lifetime.ExitAndRun(UpdateInstaller.RelaunchStartInfo(ExePath, relaunch));
            }
        }
        catch (UpdateException ex)
        {
            _logger.LogWarning(ex, "업데이트 설치 실패");
            ErrorMessage = ex.Message;
            State = UpdateState.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "업데이트 설치 실패");
            ErrorMessage = "업데이트 파일을 저장하지 못했습니다.\n" + ex.Message;
            State = UpdateState.Failed;
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Available;
        }
    }

    private string? SafeRelaunchPath()
    {
        try
        {
            return RelaunchPathProvider?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>이 버전은 알리지 않는다 (더 새 버전이 나오면 다시 알린다).</summary>
    [RelayCommand]
    private void SkipVersion()
    {
        _settings.Current.Update.SkippedVersion = AvailableVersionText;
        _settings.Save();
        IsNoticeVisible = false;
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsNoticeVisible = false;
        if (State == UpdateState.Failed)
        {
            State = _release is null ? UpdateState.Idle : UpdateState.Available;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _release?.PageUrl.AbsoluteUri ??
                  (UpdateService.Repository is { } repo ? $"https://github.com/{repo}/releases/latest" : null);
        if (url is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "릴리스 페이지를 열지 못했습니다.");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
