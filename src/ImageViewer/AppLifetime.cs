using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ImageViewer.Services;
using Microsoft.Extensions.Logging;

namespace ImageViewer;

/// <summary>
/// 업데이트 적용을 위한 종료. 메인 창을 닫아 평소처럼 설정을 저장하고 종료한 뒤(App.OnExit)
/// 설치 프로그램 또는 새 실행 파일을 실행한다.
/// </summary>
internal sealed class AppLifetime : IAppLifetime
{
    private ProcessStartInfo? _pending;

    public void ExitAndRun(ProcessStartInfo afterExit)
    {
        _pending = afterExit;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not { } main)
        {
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return;
        }

        // 정보 창(모달)에서 [업데이트]를 누른 경우: 대화 상자를 먼저 닫고, 그 루프가 끝난 뒤 메인 창을 닫는다.
        foreach (var window in desktop.Windows.Where(w => !ReferenceEquals(w, main)).ToList())
        {
            window.Close();
        }

        Dispatcher.UIThread.Post(main.Close);
    }

    public void RunPending(ILogger? logger)
    {
        if (_pending is not { } info)
        {
            return;
        }

        _pending = null;
        try
        {
            Process.Start(info)?.Dispose();
            logger?.LogInformation("업데이트 적용: {File} {Args}", info.FileName, info.Arguments);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "업데이트 프로그램을 실행하지 못했습니다: {File}", info.FileName);
        }
    }
}
