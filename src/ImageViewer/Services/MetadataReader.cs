using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ImageMagick;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>
/// 정보 패널의 "메타 정보" 구역에 보여 줄, 이미지에 들어 있는 설명 · 주석 정보를 읽는다.
/// PNG 텍스트 청크(tEXt · zTXt · iTXt, 예: AI 생성 정보), JPEG · GIF 주석, EXIF 설명 · Windows 속성(XP*),
/// IPTC, XMP 를 읽고, 값이 없거나 이미 나온 값과 같은 항목은 건너뛴다.
/// </summary>
public static class MetadataReader
{
    /// <summary>한 항목에 표시할 최대 글자 수 (워크플로 JSON 처럼 아주 긴 값이 패널을 덮지 않게).</summary>
    internal const int MaxValueLength = 600;

    /// <summary>한 항목에 표시할 최대 줄 수.</summary>
    internal const int MaxValueLines = 12;

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    private static readonly XNamespace XmpRights = "http://ns.adobe.com/xap/1.0/rights/";

    /// <summary>자주 쓰이는 텍스트 키의 표시 이름. 그 밖의 키는 그대로 보여 준다.</summary>
    private static readonly Dictionary<string, string> AttributeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["comment"] = "주석",
        ["Title"] = "제목",
        ["Author"] = "작성자",
        ["Description"] = "설명",
        ["Copyright"] = "저작권",
        ["Creation Time"] = "생성 일시",
        ["Software"] = "소프트웨어",
        ["Source"] = "원본",
        ["Disclaimer"] = "고지",
        ["Warning"] = "경고",
        ["label"] = "레이블",
        ["caption"] = "캡션",
        ["parameters"] = "생성 정보",
        ["prompt"] = "프롬프트",
        ["workflow"] = "워크플로",
    };

    /// <param name="image">디코딩한 이미지 (AutoOrient · 색 변환 전).</param>
    /// <param name="shown">이미 다른 구역(EXIF)에 나온 항목. 같은 값은 다시 보여 주지 않는다.</param>
    public static IReadOnlyList<MetadataItem> Read(IMagickImage<byte> image, IEnumerable<MetadataItem>? shown = null)
    {
        var items = new List<MetadataItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in shown ?? [])
        {
            seen.Add(Normalize(item.Value));
        }

        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = Normalize(value);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                return;
            }

            items.Add(new MetadataItem(label, Truncate(normalized)) { IsCopyable = true, FullValue = normalized });
        }

        Try(() => ReadAttributes(image, Add));
        Try(() => ReadExifText(image, Add));
        Try(() => ReadIptc(image, Add));
        Try(() => ReadXmp(image, Add));
        return items;
    }

    private static void Try(Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is MagickException or XmlException or InvalidCastException or ArgumentException
                                       or FormatException or DecoderFallbackException or IndexOutOfRangeException)
        {
            // 손상된 메타 정보는 읽을 수 있는 데까지만 표시한다.
        }
    }

    /// <summary>
    /// ImageMagick 속성: PNG 텍스트 청크와 JPEG · GIF 주석이 키 그대로 들어온다.
    /// "png:IHDR..." · "jpeg:colorspace" · "exif:..." 처럼 콜론이 있는 키는 디코더 내부 정보라 건너뛴다.
    /// </summary>
    private static void ReadAttributes(IMagickImage<byte> image, Action<string, string?> add)
    {
        foreach (var name in image.AttributeNames.ToList())
        {
            if (name.Contains(':') || name.Equals("signature", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Raw profile type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = AttributeLabels.TryGetValue(name, out var known) ? known : name;
            add(label, image.GetAttribute(name));
        }
    }

    /// <summary>EXIF 의 설명 · 주석과 Windows 탐색기 속성(제목 · 주석 · 작성자 · 태그 · 주제).</summary>
    private static void ReadExifText(IMagickImage<byte> image, Action<string, string?> add)
    {
        var exif = image.GetExifProfile();
        if (exif is null)
        {
            return;
        }

        add("설명", exif.GetValue(ExifTag.ImageDescription)?.Value);
        add("사용자 주석", DecodeUserComment(exif.GetValue(ExifTag.UserComment)?.Value));
        add("제목", DecodeXp(exif.GetValue(ExifTag.XPTitle)?.Value));
        add("주제", DecodeXp(exif.GetValue(ExifTag.XPSubject)?.Value));
        add("주석", DecodeXp(exif.GetValue(ExifTag.XPComment)?.Value));
        add("작성자", DecodeXp(exif.GetValue(ExifTag.XPAuthor)?.Value));
        add("태그", DecodeXp(exif.GetValue(ExifTag.XPKeywords)?.Value));
    }

    private static void ReadIptc(IMagickImage<byte> image, Action<string, string?> add)
    {
        var iptc = image.GetIptcProfile();
        if (iptc is null)
        {
            return;
        }

        string? Get(IptcTag tag) => iptc.GetValue(tag)?.Value;

        add("제목", Get(IptcTag.Title));
        add("헤드라인", Get(IptcTag.Headline));
        add("설명", Get(IptcTag.Caption));
        add("작성자", Get(IptcTag.Byline));
        add("키워드", JoinDistinct(iptc.GetAllValues(IptcTag.Keyword).Select(v => v.Value)));
        add("저작권", Get(IptcTag.CopyrightNotice));
        add("출처 표기", Get(IptcTag.Credit));
        add("원본", Get(IptcTag.Source));
        add("위치", JoinDistinct([Get(IptcTag.SubLocation), Get(IptcTag.City), Get(IptcTag.ProvinceState), Get(IptcTag.Country)]));
        add("생성 날짜", FormatIptcDate(Get(IptcTag.CreatedDate)));
    }

    private static void ReadXmp(IMagickImage<byte> image, Action<string, string?> add)
    {
        var xmp = image.GetXmpProfile();
        if (xmp is null)
        {
            return;
        }

        if (xmp.ToXDocument() is { } document)
        {
            ReadXmp(document, add);
        }
    }

    internal static void ReadXmp(XDocument document, Action<string, string?> add)
    {
        add("제목", XmpValue(document, Dc + "title"));
        add("헤드라인", XmpValue(document, Photoshop + "Headline"));
        add("설명", XmpValue(document, Dc + "description"));
        add("작성자", XmpValue(document, Dc + "creator"));
        add("키워드", XmpValue(document, Dc + "subject"));
        add("저작권", XmpValue(document, Dc + "rights"));
        add("이용 조건", XmpValue(document, XmpRights + "UsageTerms"));
        add("출처 표기", XmpValue(document, Photoshop + "Credit"));
        add("원본", XmpValue(document, Photoshop + "Source"));
        add("편집 프로그램", XmpValue(document, Xmp + "CreatorTool"));

        if (int.TryParse(XmpValue(document, Xmp + "Rating"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating) &&
            rating is >= 1 and <= 5)
        {
            add("별점", new string('★', rating) + new string('☆', 5 - rating));
        }
    }

    /// <summary>
    /// XMP 속성 값. rdf:Description 의 속성 또는 자식 요소로 들어 있고,
    /// 요소면 rdf:Alt(언어별 - x-default 우선) · rdf:Seq / rdf:Bag(목록)일 수 있다.
    /// </summary>
    private static string? XmpValue(XDocument document, XName name)
    {
        foreach (var description in document.Descendants(Rdf + "Description"))
        {
            if (description.Attribute(name)?.Value is { Length: > 0 } attribute)
            {
                return attribute;
            }
        }

        var element = document.Descendants(name).FirstOrDefault();
        if (element is null)
        {
            return null;
        }

        var list = element.Elements().FirstOrDefault(e => e.Name.Namespace == Rdf);
        if (list is null)
        {
            return element.Value;
        }

        var entries = list.Elements(Rdf + "li").ToList();
        if (list.Name == Rdf + "Alt")
        {
            var preferred = entries.FirstOrDefault(li =>
                string.Equals((string?)li.Attribute(XNamespace.Xml + "lang"), "x-default", StringComparison.OrdinalIgnoreCase));
            return (preferred ?? entries.FirstOrDefault())?.Value;
        }

        return JoinDistinct(entries.Select(li => li.Value));
    }

    /// <summary>
    /// EXIF UserComment: 앞 8바이트가 문자 코드("ASCII", "UNICODE", "JIS", 0 = 정의 안 됨), 나머지가 본문.
    /// </summary>
    internal static string? DecodeUserComment(byte[]? data)
    {
        if (data is null || data.Length <= 8)
        {
            return null;
        }

        var code = Encoding.ASCII.GetString(data, 0, 8).TrimEnd('\0', ' ');
        var body = data.AsSpan(8);
        string text;
        if (code.Equals("UNICODE", StringComparison.OrdinalIgnoreCase))
        {
            // 바이트 순서가 표시되지 않는다. ASCII 범위 글자가 많은 쪽(0 바이트 위치)으로 판단한다.
            text = LooksBigEndian(body) ? Encoding.BigEndianUnicode.GetString(body) : Encoding.Unicode.GetString(body);
        }
        else
        {
            text = Encoding.UTF8.GetString(body);
        }

        text = text.TrimEnd('\0', ' ');

        // 일부 카메라는 빈 주석을 공백 · 0 으로 채워 둔다.
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool LooksBigEndian(ReadOnlySpan<byte> data)
    {
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0)
            {
                evenZeros++;
            }

            if (data[i + 1] == 0)
            {
                oddZeros++;
            }
        }

        return evenZeros > oddZeros;
    }

    /// <summary>Windows XP 속성(XPTitle 등)은 UTF-16LE 문자열이다.</summary>
    internal static string? DecodeXp(byte[]? data) =>
        data is { Length: >= 2 } ? Encoding.Unicode.GetString(data, 0, data.Length & ~1).TrimEnd('\0') : null;

    /// <summary>IPTC 날짜 "yyyyMMdd" → "yyyy-MM-dd".</summary>
    internal static string? FormatIptcDate(string? value) =>
        DateTime.TryParseExact(value?.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;

    private static string? JoinDistinct(IEnumerable<string?> values)
    {
        var parts = values
            .Select(v => v?.Trim().TrimEnd('\0'))
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim().Trim('\0').Trim();

    /// <summary>긴 값은 <see cref="MaxValueLength"/> 글자 · <see cref="MaxValueLines"/> 줄까지만 보여 준다.</summary>
    internal static string Truncate(string value)
    {
        var cut = value.Length > MaxValueLength ? MaxValueLength : -1;

        var lines = 0;
        for (var i = 0; i < value.Length && (cut < 0 || i < cut); i++)
        {
            if (value[i] == '\n' && ++lines >= MaxValueLines)
            {
                cut = i;
                break;
            }
        }

        return cut < 0 ? value : value[..cut].TrimEnd() + " …";
    }
}
