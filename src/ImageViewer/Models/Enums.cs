namespace ImageViewer.Models;

/// <summary>이미지 크기 조절 모드 (단축키 0 / 7 / 8 / 9).</summary>
public enum DisplayMode
{
    Original,
    FitWidth,
    FitHeight,
    /// <summary>비율을 유지한 채 이미지 전체를 창에 맞춰 최대한 크게 표시 (잘리지 않음).</summary>
    Fill,
}

/// <summary>화면 표시용 이미지 필터 (단축키 U / I / S).</summary>
public enum ImageFilter
{
    None,
    Smooth,
    SmoothSharpen,
}

public enum SlideShowMode
{
    Sequential,
    Random,
}

public enum MouseWheelAction
{
    Zoom,
    Navigate,
}

public enum ImageSourceType
{
    File,
    Zip,
}

public enum ViewerBackground
{
    Black,
    DarkGray,
    Gray,
    White,
    Checkerboard,
}

/// <summary>코믹 뷰어(2페이지 보기) 페이지 방향.</summary>
public enum ComicReadingDirection
{
    /// <summary>좌 → 우 ([1p, 2p]).</summary>
    LeftToRight,

    /// <summary>우 → 좌 ([2p, 1p]).</summary>
    RightToLeft,
}
