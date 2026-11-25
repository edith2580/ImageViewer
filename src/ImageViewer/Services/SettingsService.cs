using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ImageViewer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageViewer.Services;

/// <summary>
/// 사용자 설정을 %LOCALAPPDATA%\ImageViewer\settings.json 에 저장·복원한다.
/// 파일이 없거나 손상되었으면 기본값을 사용한다.
///
/// ImageViewer 는 창(프로세스)을 여러 개 띄울 수 있으므로, 저장할 때 파일을 통째로 덮어쓰지 않는다.
/// 마지막으로 읽거나 저장한 뒤 이 창에서 바뀐 값만 지금 파일 내용 위에 반영해, 다른 창이 바꾼 설정을 지우지 않는다.
/// </summary>
public sealed class SettingsService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<SettingsService> _logger;

    /// <summary>마지막으로 읽거나 저장했을 때의 이 창의 설정 (바뀐 값을 찾는 기준).</summary>
    private JsonObject? _baseline;

    public SettingsService(ILogger<SettingsService>? logger = null, string? settingsPath = null)
    {
        _logger = logger ?? NullLogger<SettingsService>.Instance;
        SettingsPath = settingsPath ?? Path.Combine(AppPaths.DataDirectory, "settings.json");
    }

    public string SettingsPath { get; }

    public ViewerSettings Current { get; private set; } = new();

    public ViewerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Current = (JsonSerializer.Deserialize<ViewerSettings>(json, JsonOptions) ?? new ViewerSettings()).Normalize();
                _baseline = ToJson(Current);
                return Current;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "설정 파일을 읽을 수 없어 기본값을 사용합니다.");
        }

        Current = new ViewerSettings().Normalize();
        _baseline = ToJson(Current);
        return Current;
    }

    private static JsonObject ToJson(ViewerSettings settings) =>
        JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var mine = ToJson(Current);
            using (CrossProcessLock())
            {
                // 다른 창이 그사이 저장한 내용 위에, 이 창에서 바뀐 값만 반영한다.
                var merged = ReadFileJson() is { } theirs ? Merge(_baseline, mine, theirs) : mine;

                // 저장 도중 종료되어도 기존 파일이 깨지지 않도록 임시 파일에 쓴 뒤 교체한다.
                // (여러 창이 동시에 저장해도 임시 파일이 겹치지 않게 프로세스 번호를 붙인다)
                var temp = $"{SettingsPath}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temp, merged.ToJsonString(JsonOptions));
                File.Move(temp, SettingsPath, overwrite: true);
            }

            _baseline = mine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "설정 파일을 저장하지 못했습니다.");
        }
    }

    private JsonObject? ReadFileJson()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonNode.Parse(File.ReadAllText(SettingsPath), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // 손상된 파일은 이 창의 설정으로 덮어쓴다
        }
    }

    /// <summary>
    /// 3방향 병합: <paramref name="theirs"/>(지금 파일) 위에, <paramref name="baseline"/> 이후
    /// <paramref name="mine"/> 에서 바뀐 값만 덮어쓴다. 객체는 속성별로 내려가며 비교하고, 그 밖의 값(배열 포함)은 통째로 비교한다.
    /// </summary>
    internal static JsonObject Merge(JsonObject? baseline, JsonObject mine, JsonObject theirs)
    {
        var result = theirs.DeepClone().AsObject();
        foreach (var (key, mineValue) in mine)
        {
            var baseValue = baseline?[key];
            if (mineValue is JsonObject mineObject && result[key] is JsonObject theirObject)
            {
                result[key] = Merge(baseValue as JsonObject, mineObject, theirObject);
            }
            else if (!result.ContainsKey(key) || !JsonNode.DeepEquals(mineValue, baseValue))
            {
                result[key] = mineValue?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>여러 창이 동시에 저장할 때 읽기 · 병합 · 쓰기가 섞이지 않게 하는 프로세스 간 잠금.</summary>
    private IDisposable CrossProcessLock()
    {
        Mutex? mutex = null;
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(SettingsPath).ToUpperInvariant())))[..16];
            mutex = new Mutex(false, "ImageViewer.Settings." + hash);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // 다른 창이 저장 중에 끝났다: 잠금은 넘겨받았다
            }

            if (acquired)
            {
                return new MutexRelease(mutex);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or WaitHandleCannotBeOpenedException)
        {
            _logger.LogDebug(ex, "설정 잠금을 사용할 수 없습니다.");
        }

        mutex?.Dispose();
        return new MutexRelease(null); // 잠금 없이 저장한다 (최악의 경우 마지막 저장이 남는다)
    }

    private sealed class MutexRelease(Mutex? mutex) : IDisposable
    {
        public void Dispose()
        {
            if (mutex is null)
            {
                return;
            }

            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageViewer");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
