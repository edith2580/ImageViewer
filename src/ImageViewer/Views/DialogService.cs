using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageViewer.Services;
using ImageViewer.ViewModels;

namespace ImageViewer.Views;

public sealed class DialogService : IDialogService
{
    private const string Caption = "ImageViewer";

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static Window? Owner => Desktop?.MainWindow is { IsLoaded: true } w ? w : null;

    private int _modalDepth;

    public bool IsModalOpen => _modalDepth > 0;

    public string? PickFolder()
    {
        if (Owner?.StorageProvider is not { } storage)
        {
            return null;
        }

        return RunModal(async () =>
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "이미지 폴더 선택",
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        });
    }

    public string? PickFile()
    {
        if (Owner?.StorageProvider is not { } storage)
        {
            return null;
        }

        return RunModal(async () =>
        {
            var patterns = Helpers.ImageFormatHelper.SupportedExtensions.Select(e => "*" + e).ToArray();
            var allSupported = patterns.Concat(["*.zip", "*.cbz"]).ToArray();
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "이미지 또는 ZIP 파일 열기",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("이미지 및 ZIP") { Patterns = allSupported },
                    new FilePickerFileType("ZIP 파일") { Patterns = ["*.zip", "*.cbz"] },
                    new FilePickerFileType("이미지 파일") { Patterns = patterns },
                    FilePickerFileTypes.All,
                ],
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        });
    }

    public bool ShowImageList(ImageListViewModel viewModel)
    {
        if (Owner is not { } owner)
        {
            return false;
        }

        return RunModal(() =>
        {
            var window = new ImageListWindow(viewModel);
            return window.ShowDialog<bool>(owner);
        });
    }

    public bool ShowSettings(SettingsViewModel viewModel)
    {
        if (Owner is not { } owner)
        {
            return false;
        }

        return RunModal(() =>
        {
            var window = new SettingsWindow(viewModel);
            return window.ShowDialog<bool>(owner);
        });
    }

    public void ShowMessage(string message, bool isError = false)
    {
        var owner = Desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? Owner;
        RunModal(() => MessageBoxWindow.ShowAsync(owner, message, Caption, isYesNo: false));
    }

    public bool Confirm(string message)
    {
        var owner = Desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? Owner;
        return RunModal(() => MessageBoxWindow.ShowAsync(owner, message, Caption, isYesNo: true));
    }

    private T RunModal<T>(Func<Task<T>> action)
    {
        _modalDepth++;
        try
        {
            return RunModalCore(action);
        }
        finally
        {
            _modalDepth--;
        }
    }

    private static T RunModalCore<T>(Func<Task<T>> action)
    {
        var frame = new DispatcherFrame();
        T result = default!;
        Exception? error = null;

        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                result = await action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                frame.Continue = false;
            }
        }

        Dispatcher.UIThread.PushFrame(frame);
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }
}
