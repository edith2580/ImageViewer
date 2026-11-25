using System.Text;
using ImageViewer.Helpers;
using Microsoft.Extensions.Logging;

namespace ImageViewer.Services;

/// <summary>
/// %LOCALAPPDATA%\ImageViewer\logs 에 일자별 로그 파일을 남기는 간단한 로거.
/// 개인 정보 보호를 위해 메시지 · 예외(스택 추적 포함)의 전체 경로는 파일명만 남긴다 (<see cref="LogSanitizer"/>).
/// 여러 ImageViewer 창(프로세스)이 같은 파일에 쓰므로, 다른 창이 쓰는 중이면 잠시 기다렸다가 다시 쓴다.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 7;

    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly Lock _lock = new();

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        TryCleanup();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>로그 한 줄(과 예외 내용)을 만든다. 경로는 파일명만 남긴다.</summary>
    internal static string Format(DateTime time, string category, LogLevel level, string message, Exception? exception)
    {
        var sb = new StringBuilder();
        sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(level.ToString().ToUpperInvariant()[..4]).Append("] ")
          .Append(category[(category.LastIndexOf('.') + 1)..]).Append(": ")
          .AppendLine(LogSanitizer.RedactPaths(message));

        if (exception is not null)
        {
            // 원인을 찾을 수 있도록 내부 예외 · 스택 추적까지 남기되, 경로는 파일명만 남긴다.
            foreach (var line in LogSanitizer.RedactPaths(exception.ToString()).Split('\n'))
            {
                sb.Append("    ").AppendLine(line.TrimEnd('\r'));
            }
        }

        return sb.ToString();
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var bytes = Encoding.UTF8.GetBytes(Format(DateTime.Now, category, level, message, exception));
        lock (_lock)
        {
            // 다른 ImageViewer 창이 같은 파일에 쓰는 중이면(잠겨 있으면) 잠시 뒤 다시 시도한다.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    var file = Path.Combine(_directory, $"imageviewer-{DateTime.Now:yyyyMMdd}.log");
                    AppendShared(file, bytes);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(10 * (attempt + 1));
                }
                catch (Exception)
                {
                    return; // 로그 기록 실패는 무시한다.
                }
            }
        }
    }

    /// <summary>
    /// 쓰는 동안은 다른 프로세스가 쓰지 못하게 열고(읽기는 허용) 끝에 덧붙인다.
    /// .NET 의 Append 모드는 연 순간의 끝 위치에 쓰므로, 두 프로세스가 동시에 열어 두고 쓰면 서로의 줄을 덮어쓴다.
    /// 그래서 함께 열지 않고, 잠겨 있으면 IOException 으로 알려 호출부가 다시 시도하게 한다.
    /// </summary>
    internal static void AppendShared(string file, byte[] bytes)
    {
        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        stream.Write(bytes);
    }

    private void TryCleanup()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var threshold = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "imageviewer-*.log"))
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
