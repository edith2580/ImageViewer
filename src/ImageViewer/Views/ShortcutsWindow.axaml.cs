using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ImageViewer.Views;

public sealed record ShortcutItem(string Key, string Description);

public sealed record ShortcutGroup(string Title, ShortcutItem[] Items);

public partial class ShortcutsWindow : Window
{
    public ShortcutGroup[] Groups { get; } =
    [
        new("창 · 일반",
        [
            new("Alt + Enter", "전체 화면 전환"),
            new("Esc", "프로그램 종료 (대화 상자에서는 닫기)"),
            new("`", "상단 · 하단 바 고정(Pin) 일괄 전환"),
            new("Ctrl + O", "파일 / ZIP 열기"),
            new("Ctrl + Shift + O", "폴더 열기"),
            new("Tab", "이미지 정보(EXIF) 패널"),
            new("Enter", "이미지 목록")
        ]),
        new("탐색 · 슬라이드쇼",
        [
            new("← / ↑", "이전 이미지"),
            new("→ / ↓", "다음 이미지"),
            new("Home / End", "처음 / 마지막 이미지"),
            new("[ / ]", "이전 / 다음 폴더 또는 ZIP 파일"),
            new("PgUp / PgDn", "이전 / 다음 페이지 (다중 페이지 TIFF, 그 밖에는 이미지)"),
            new("Space", "슬라이드쇼 재생 / 일시정지")
        ]),
        new("보기 · 배율 · 필터",
        [
            new("0", "원본 크기"),
            new("7", "폭 맞춤"),
            new("8", "높이 맞춤"),
            new("9", "꽉 차게 보기"),
            new("+ / =", "확대"),
            new("-", "축소"),
            new("Ctrl + 0", "배율 초기화"),
            new("R / L", "시계 / 반시계 방향 90° 회전"),
            new("H / V", "좌우 / 상하 반전"),
            new("U", "필터 없음"),
            new("I", "부드럽게"),
            new("S", "부드럽게 + 선명하게")
        ]),
        new("코믹 뷰어 (2페이지 보기)",
        [
            new("C", "코믹 뷰어 (2페이지 보기) 전환"),
            new("D", "페이지 방향 전환 (좌→우 [1p,2p] / 우→좌 [2p,1p])"),
            new("B", "현재 2페이지 사이에 빈 페이지 삽입 / 제거")
        ]),
        new("마우스",
        [
            new("휠 위 / 아래", "이전 / 다음 이미지 (설정 변경 가능)"),
            new("Ctrl + 휠", "확대 / 축소"),
            new("Shift + 휠", "이미지 상하 이동"),
            new("드래그", "창 이동 (전체 화면에서는 이미지 이동)"),
            new("가운데 버튼 드래그", "이미지 이동"),
            new("더블클릭", "최대화 / 복원"),
            new("뒤로 / 앞으로 버튼", "이전 / 다음 이미지")
        ])
    ];

    public ShortcutsWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
