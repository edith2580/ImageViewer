using System.Text.RegularExpressions;

namespace ImageViewer.Helpers;

/// <summary>
/// 로그에 남길 글에서 전체 경로를 지우고 파일명만 남긴다 (개인 정보 보호).
/// 예외 메시지("Could not find file 'D:\사진\가족\a.jpg'.")와 스택 추적("in D:\src\Foo.cs:line 12")에 들어 있는
/// Windows 경로(드라이브 · UNC)와 사용자 데이터가 있는 Unix 경로를 <c>…\a.jpg</c> 처럼 바꾼다.
/// </summary>
public static partial class LogSanitizer
{
    // 경로 조각에 쓸 수 없는 글자 + 따옴표 · 줄바꿈 · 콜론(스택 추적의 ":line 12" 앞에서 끊기 위해).
    private const string WinSegment = @"[^\\/:*?""<>|\r\n'`]";
    private const string UnixSegment = @"[^/\s'""`:]";

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])[A-Za-z]:\\(?:" + WinSegment + @"+\\)*" + WinSegment + "*" +
        @"|\\\\" + WinSegment + @"+\\(?:" + WinSegment + @"+\\)*" + WinSegment + "*" +
        @"|(?<![\w.~-])/(?:home|Users|tmp|var|mnt|media|run|private|Volumes|root|opt|srv|data)(?:/" + UnixSegment + "+)+/?",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    /// <summary><paramref name="text"/> 안의 전체 경로를 마지막 이름만 남긴 형태로 바꾼다.</summary>
    public static string RedactPaths(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return PathPattern().Replace(text, match =>
        {
            var path = match.Value.TrimEnd(' ', '.');
            var trailing = match.Value[path.Length..];
            var separator = path.Contains('\\') ? '\\' : '/';
            var trimmed = path.TrimEnd('\\', '/');
            var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];

            // 드라이브 루트("D:\")처럼 이름이 없으면 경로 자체가 짧으므로 그대로 둔다.
            if (name.Length == 0 || name.EndsWith(':'))
            {
                return match.Value;
            }

            return "…" + separator + name + trailing;
        });
    }
}
