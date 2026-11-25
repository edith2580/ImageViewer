using ImageViewer.ViewModels;

namespace ImageViewer.Services;

/// <summary>ViewModel 이 창·대화 상자에 직접 의존하지 않도록 분리한 인터페이스.</summary>
public interface IDialogService
{
    string? PickFolder();

    string? PickFile();

    /// <summary>이미지 목록 대화 상자. 열기를 선택하면 true.</summary>
    bool ShowImageList(ImageListViewModel viewModel);

    bool ShowSettings(SettingsViewModel viewModel);

    void ShowMessage(string message, bool isError = false);

    bool Confirm(string message);

    /// <summary>
    /// 모달 대화 상자가 열려 있는지. 대화 상자가 떠 있는 동안에도 메인 창의 타이머 · 비동기 작업은 계속 돌기 때문에,
    /// 화면을 바꾸는 작업(슬라이드쇼 · 폴더 변경 반영)은 이 값을 보고 대화 상자가 닫힐 때까지 미룬다.
    /// </summary>
    bool IsModalOpen { get; }
}
