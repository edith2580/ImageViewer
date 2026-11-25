namespace ImageViewer.Models;

/// <summary>정보 패널의 한 줄. <see cref="IsHeader"/> 이면 구역 제목.</summary>
public sealed record MetadataItem(string Label, string Value, bool IsHeader = false)
{
    /// <summary>"메타 정보" 구역의 항목(제목 포함). 클릭하면 클립보드에 복사한다.</summary>
    public bool IsCopyable { get; init; }

    /// <summary>잘리지 않은 원래 값. 화면에는 <see cref="Value"/>(긴 값은 잘림)를 보여 주고, 복사할 때 이 값을 쓴다.</summary>
    public string? FullValue { get; init; }

    public string CopyValue => FullValue ?? Value;

    public static MetadataItem Header(string title) => new(title, string.Empty, IsHeader: true);
}
