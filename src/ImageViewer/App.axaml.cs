using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    private static void ConfigureMagick()
    {
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

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 개별 이미지 오류 등으로 프로그램 전체가 종료되지 않도록 한다.
        _logger?.LogError(e.Exception, "UI 스레드 예외");
        e.Handled = true;
        try
        {
            var owner = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            _ = MessageBoxWindow.ShowAsync(
                owner,
                "작업 중 오류가 발생했습니다.\n" + e.Exception.Message,
                "ImageViewer",
                isYesNo: false);
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
