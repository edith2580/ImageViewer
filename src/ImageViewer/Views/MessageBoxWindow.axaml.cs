using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ImageViewer.Views;

public partial class MessageBoxWindow : Window
{
    public MessageBoxWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            Activate();
            PrimaryButton.Focus();
        };
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
    }

    public static Task<bool> ShowAsync(Window? owner, string message, string title, bool isYesNo)
    {
        var dialog = new MessageBoxWindow
        {
            Title = title,
        };
        dialog.MessageText.Text = message;
        if (isYesNo)
        {
            dialog.PrimaryButton.Content = "예";
            dialog.SecondaryButton.IsVisible = true;
        }
        else
        {
            dialog.PrimaryButton.Content = "확인";
        }

        if (owner is { IsVisible: true })
        {
            return dialog.ShowDialog<bool>(owner);
        }

        var tcs = new TaskCompletionSource<bool>();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        dialog.Show();
        return tcs.Task;
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnSecondaryClick(object? sender, RoutedEventArgs e) => Close(false);
}
