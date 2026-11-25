using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageMagick;
using ImageViewer.Helpers;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;
using ImageViewer.Services.Updates;
using ImageViewer.ViewModels;
using ImageViewer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ImageViewer;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private readonly AppLifetime _lifetime = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var args = desktop.Args ?? [];
            var options = CommandLineHelper.Parse(args);

            // 설치 프로그램에서 사용하는 컨텍스트 메뉴 등록/해제 모드
            if (options.RegisterShell || options.UnregisterShell)
            {
                var exitCode = RunShellCommand(options);
                desktop.Shutdown(exitCode);
                return;
            }

            _services = ConfigureServices();
            _logger = _services.GetRequiredService<ILogger<App>>();
            RegisterExceptionHandlers();
            ConfigureMagick();

            _services.GetRequiredService<SettingsService>().Load();

            // 포터블 업데이트로 남은 이전 실행 파일(.old) 정리
            _ = UpdateInstaller.CleanupLeftoversAsync(UpdateInstaller.CurrentExePath);

            var window = _services.GetRequiredService<MainWindow>();
            desktop.MainWindow = window;
            desktop.Exit += OnExit;

            // macOS Finder에서 '다음으로 열기' 또는 더블클릭으로 전달된 파일/폴더 처리
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, activatedArgs) =>
                {
                    var openedPath = ExtractActivatedPath(activatedArgs);
                    if (!string.IsNullOrEmpty(openedPath))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            window.Activate();
                            _ = window.ViewModel.OpenPathAsync(openedPath);
                        });
                    }
                };
            }

            window.Show();
            _logger.LogInformation("ImageViewer 시작 (Magick.NET {Version})", MagickNET.Version);

            if (options.Error is not null)
            {
                _ = MessageBoxWindow.ShowAsync(window, options.Error, "ImageViewer", isYesNo: false);
            }
            else if (options.Path is not null)
            {
                _ = window.ViewModel.OpenPathAsync(options.Path);
            }

            // 새 버전 자동 확인 (하루 한 번, 시작 뒤 잠시 기다렸다가)
            _ = _services.GetRequiredService<UpdateViewModel>().AutoCheckAsync(TimeSpan.FromSeconds(5));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? ExtractActivatedPath(ActivatedEventArgs args)
    {
        if (args is FileActivatedEventArgs fileArgs)
        {
            foreach (var item in fileArgs.Files)
            {
                var local = item.TryGetLocalPath();
                if (!string.IsNullOrEmpty(local))
                {
                    return local;
                }

                if (item.Path is { IsFile: true } uri)
                {
                    return uri.LocalPath;
                }
            }
        }
        else if (args is ProtocolActivatedEventArgs { Uri.IsFile: true } protoArgs)
        {
            return protoArgs.Uri.LocalPath;
        }

        return null;
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _services?.Dispose();
        _lifetime.RunPending(_logger);
    }

    private ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
            builder.AddProvider(new FileLoggerProvider(AppPaths.LogDirectory));
        });

        services.AddSingleton<SettingsService>(sp => new SettingsService(sp.GetRequiredService<ILogger<SettingsService>>()));
        services.AddSingleton<IImageSourceFactory, ImageSourceFactory>();
        services.AddSingleton<ImageLoaderService>();
        services.AddSingleton<ImageFilterService>();
        services.AddSingleton<ImageRenderService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<ImageNavigationService>();
        services.AddSingleton<SlideShowService>(_ => new SlideShowService());
        services.AddSingleton<ShellIntegrationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAppLifetime>(_lifetime);
        services.AddSingleton<UpdateService>(sp => new UpdateService(sp.GetRequiredService<ILogger<UpdateService>>()));
        services.AddSingleton<UpdateViewModel>(sp => new UpdateViewModel(
            sp.GetRequiredService<UpdateService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IAppLifetime>(),
            sp.GetRequiredService<ILogger<UpdateViewModel>>()));
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void ConfigureMagick()
    {
        // 다른 ImageMagick 호출보다 먼저: 스크립트 · 문서 코더와 외부 프로그램 실행을 막는 보안 정책
        MagickSecurity.Initialize(Path.Combine(AppPaths.DataDirectory, "magick"), _logger);

        // 지나치게 큰 이미지로 인한 메모리 과다 사용 방지 (픽셀 수 검사는 디코딩 전에 별도로 수행)
        ResourceLimits.Width = 100_000;
        ResourceLimits.Height = 100_000;
        ResourceLimits.ListLength = ImageLoaderService.MaxFrames + 24; // 애니메이션 · 다중 페이지 프레임 수 한도
    }

    private void RegisterExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "처리되지 않은 비동기 작업 예외");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                _logger?.LogCritical(ex, "처리되지 않은 예외");
            }
        };
    }

    /// <summary>오류 알림 창이 떠 있는 동안 같은 오류가 되풀이되어도 창을 더 띄우지 않는다.</summary>
    private bool _errorBoxOpen;

    /// <summary>
    /// 프로세스 상태를 믿을 수 없게 만드는 예외. 이런 예외는 삼키지 않고 기록한 뒤 프로그램을 끝낸다.
    /// (메모리 부족은 큰 이미지 하나의 할당 실패인 경우가 대부분이라 캐시를 비우고 계속한다)
    /// </summary>
    internal static bool IsFatal(Exception ex) => ex is InvalidProgramException
        or AccessViolationException
        or System.Runtime.InteropServices.SEHException
        or BadImageFormatException
        or TypeInitializationException
        or StackOverflowException;

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        if (IsFatal(ex))
        {
            _logger?.LogCritical(ex, "복구할 수 없는 UI 스레드 예외로 종료합니다.");
            try
            {
                _services?.GetService<SettingsService>()?.Save();
            }
            catch (Exception)
            {
            }

            return; // 처리하지 않음 → 프로그램 종료
        }

        // 개별 이미지 오류 등으로 프로그램 전체가 종료되지 않도록 한다.
        e.Handled = true;
        string message;
        if (ex is OutOfMemoryException or InsufficientExecutionStackException)
        {
            _logger?.LogError(ex, "메모리 부족: 캐시를 비웁니다.");
            FreeMemory();
            message = "메모리가 부족해 작업을 마치지 못했습니다.\n이미지 캐시를 비웠습니다. 너무 큰 이미지가 문제라면 설정 > 안전 한도의 최대 이미지 크기를 낮춰 주세요.";
        }
        else
        {
            _logger?.LogError(ex, "UI 스레드 예외");
            message = "작업 중 오류가 발생했습니다.\n" + ex.Message;
        }

        if (_errorBoxOpen)
        {
            return;
        }

        try
        {
            _errorBoxOpen = true;
            var owner = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            MessageBoxWindow.ShowAsync(owner, message, "ImageViewer", isYesNo: false)
                .ContinueWith(_ => Dispatcher.UIThread.Post(() => _errorBoxOpen = false), TaskScheduler.Default);
        }
        catch (Exception)
        {
            _errorBoxOpen = false;
        }
    }

    private void FreeMemory()
    {
        try
        {
            _services?.GetService<ImageLoaderService>()?.ClearCache();
            _services?.GetService<ImageRenderService>()?.ClearCache();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }
        catch (Exception)
        {
        }
    }

    private static int RunShellCommand(CommandLineOptions options)
    {
        var shell = new ShellIntegrationService();
        try
        {
            if (options.UnregisterShell)
            {
                // 제거 프로그램이 실행한다: 탐색기 메뉴와 기본 앱 등록을 함께 지운다.
                shell.Unregister();
                shell.UnregisterDefaultApp();
            }

            if (options.RegisterShell)
            {
                shell.Register();
            }

            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
