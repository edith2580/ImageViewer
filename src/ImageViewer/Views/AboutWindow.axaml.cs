using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ImageViewer.Services;
using ImageViewer.ViewModels;

namespace ImageViewer.Views;

public partial class AboutWindow : Window
{
    private readonly AboutInfo _info;

    public AboutWindow(AboutInfo info, UpdateViewModel? update = null)
    {
        InitializeComponent();
        _info = info;
        DataContext = info;
        if (update is null)
        {
            UpdateGroup.IsVisible = false;
        }
        else
        {
            UpdateGroup.DataContext = update;
        }

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            Activate();
            CloseButton.Focus();
        };
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        if (_info.ProjectUrl is not { } url)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 기본 브라우저가 없으면 무시
        }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_info.ToText());
                CopiedText.IsVisible = true;
            }
        }
        catch (Exception)
        {
            // 다른 프로그램이 클립보드를 잡고 있으면 실패할 수 있다
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
