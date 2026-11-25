using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageViewer.Services.Updates;

/// <summary>릴리스에 올라간 파일 하나.</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>GitHub 의 최신 릴리스.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, Uri PageUrl, string Notes, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>설치 방식에 맞는 업데이트 파일과 그 SHA-256.</summary>
public sealed record UpdatePackage(ReleaseInfo Release, ReleaseAsset Asset, string Sha256);

/// <summary>업데이트를 받을 수 없는 상황 (사용자에게 그대로 보여 줄 메시지).</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// GitHub 릴리스로 새 버전을 확인하고 내려받는다.
///
/// - 최신 릴리스: GET https://api.github.com/repos/{저장소}/releases/latest (임시 · 사전 릴리스 제외)
/// - 설치 버전은 ImageViewer-x.y.z-setup.exe, 포터블은 ImageViewer-x.y.z-portable.exe 를 받는다.
/// - 받은 파일은 SHA-256 을 확인한 뒤에만 쓴다. 값은 릴리스의 SHA256SUMS.txt 에서 찾고,
///   그 파일은 SHA256SUMS.txt.sig (ECDSA P-256 / SHA-256 서명) 를 앱에 들어 있는 공개키로 확인한 뒤에만 믿는다.
///   GitHub 가 계산한 파일 digest 는 같은 릴리스에서 오므로 진위 확인에는 쓰지 않고, 있으면 값이 같은지만 본다.
///   (릴리스 파일과 해시를 함께 바꿀 수 있는 사람도 서명 키 없이는 업데이트를 만들 수 없다)
/// - 서명 키는 GitHub 의 보호된 환경(release-signing)에만 있고, 릴리스할 때 승인을 받아 서명한다.
/// - 저장소와 공개키는 빌드 시 프로젝트 파일의 UpdateRepository · UpdatePublicKey 값으로 정해진다.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string SumsFileName = "SHA256SUMS.txt";
    public const string SignatureFileName = SumsFileName + ".sig";

    /// <summary>확인값 · 서명 파일의 최대 크기 (이보다 크면 받지 않는다).</summary>
    private const int MaxSmallFileBytes = 64 * 1024;
    public const string SetupSuffix = "-setup.exe";
    public const string PortableSuffix = "-portable.exe";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger<UpdateService> _logger;

    private readonly string? _publicKey;

    /// <param name="publicKey">릴리스 서명 확인용 공개키 (SubjectPublicKeyInfo, Base64). 없으면 프로젝트 파일의 UpdatePublicKey.</param>
    public UpdateService(ILogger<UpdateService>? logger = null, HttpClient? http = null, Uri? feedBase = null, string? publicKey = null)
    {
        _logger = logger ?? NullLogger<UpdateService>.Instance;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        FeedBase = feedBase ?? DefaultFeedBase();
        CurrentVersion = ParseVersion(AboutInfo.VersionOf(typeof(UpdateService).Assembly)) ?? new Version(0, 0, 0);
        _publicKey = publicKey ?? PublicKey;
    }

    /// <summary>프로젝트 파일의 &lt;AssemblyMetadata Include="UpdatePublicKey" /&gt; (릴리스 서명 확인용 공개키).</summary>
    public static string? PublicKey =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdatePublicKey")?.Value is { Length: > 0 } key ? key : null;

    /// <summary>릴리스 정보 주소의 기준 (끝이 / 인 주소). 최신 릴리스는 이 아래 releases/latest.</summary>
    public Uri FeedBase { get; }

    public Version CurrentVersion { get; init; }

    /// <summary>
    /// 다운로드 중 이 시간 동안 받은 데이터가 없으면(서버 · 연결이 멈춤) 중단한다.
    /// 전체 시간은 제한하지 않는다 (느린 연결에서도 계속 받고 있으면 끝까지 받는다).
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>프로젝트 파일의 &lt;AssemblyMetadata Include="UpdateRepository" /&gt; (예: owner/ImageViewer).</summary>
    public static string? Repository =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;

    public static Uri DefaultFeedBase()
    {
#if DEBUG
        // 개발 빌드에서만 로컬 테스트 서버를 가리킬 수 있다 (예: http://127.0.0.1:8765/).
        if (Environment.GetEnvironmentVariable("IMAGEVIEWER_UPDATE_FEED") is { Length: > 0 } feed &&
            Uri.TryCreate(feed.EndsWith('/') ? feed : feed + "/", UriKind.Absolute, out var local))
        {
            return local;
        }
#endif
        return new Uri($"https://api.github.com/repos/{Repository ?? "unknown/unknown"}/");
    }

    /// <summary>최신 릴리스. 릴리스가 하나도 없으면 null.</summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(FeedBase, "releases/latest"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ImageViewer-Updater", CurrentVersion.ToString(3)));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpdateException("업데이트 서버에 연결하지 못했습니다. 인터넷 연결을 확인해 주세요.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null; // 릴리스 없음 (또는 비공개 저장소)
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"업데이트 정보를 가져오지 못했습니다. (HTTP {(int)response.StatusCode})");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseRelease(json) ?? throw new UpdateException("업데이트 정보의 형식이 올바르지 않습니다.");
        }
    }

    /// <summary>GitHub 릴리스 JSON 을 읽는다. 버전 태그가 아니거나 사전 릴리스면 null.</summary>
    internal static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Bool(root, "draft") || Bool(root, "prerelease"))
            {
                return null;
            }

            var tag = Str(root, "tag_name");
            if (tag is null || ParseVersion(tag) is not { } version ||
                !Uri.TryCreate(Str(root, "html_url"), UriKind.Absolute, out var page))
            {
                return null;
            }

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    if (Str(a, "name") is { } name &&
                        Uri.TryCreate(Str(a, "browser_download_url"), UriKind.Absolute, out var url))
                    {
                        var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                        assets.Add(new ReleaseAsset(name, url, size, ParseDigest(Str(a, "digest"))));
                    }
                }
            }

            return new ReleaseInfo(version, tag, page, Str(root, "body") ?? "", assets);
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>"v1.2.3" · "1.2.3" → 1.2.3. "-beta" 같은 꼬리표가 붙은 사전 버전은 null.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V'))
        {
            t = t[1..];
        }

        if (!Version.TryParse(t, out var v) || v.Major < 0)
        {
            return null;
        }

        // 1.2 → 1.2.0 처럼 비교할 수 있게 자리를 맞춘다.
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>GitHub asset digest "sha256:abcd..." → 소문자 16진수.</summary>
    internal static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return IsSha256Hex(hex) ? hex : null;
    }

    /// <summary>sha256sum 형식 ("hash  파일명" 또는 "hash *파일명") 에서 파일 하나의 값을 찾는다.</summary>
    internal static string? FindInSums(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            var hash = line[..space].ToLowerInvariant();
            var name = line[(space + 1)..].TrimStart(' ', '*');
            if (IsSha256Hex(hash) && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return hash;
            }
        }

        return null;
    }

    private static bool IsSha256Hex(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    /// <summary>설치 방식에 맞는 파일 (설치 버전 = -setup.exe, 포터블 = -portable.exe).</summary>
    internal static ReleaseAsset? SelectAsset(ReleaseInfo release, InstallKind kind)
    {
        var suffix = kind == InstallKind.Installed ? SetupSuffix : PortableSuffix;
        return release.Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 받을 파일과 그 SHA-256 을 정한다. 서명된 SHA256SUMS.txt 에서만 값을 얻는다.
    /// 파일 · 확인값 · 서명 중 하나라도 없거나 서명이 맞지 않으면 <see cref="UpdateException"/>.
    /// </summary>
    public async Task<UpdatePackage> ResolvePackageAsync(ReleaseInfo release, InstallKind kind, CancellationToken ct)
    {
        var asset = SelectAsset(release, kind) ?? throw new UpdateException(
            $"이 릴리스({release.Tag})에는 {(kind == InstallKind.Installed ? "설치 파일" : "포터블 실행 파일")}이 없습니다.");

        if (_publicKey is null)
        {
            throw new UpdateException("이 빌드에는 업데이트 서명을 확인할 키가 없어 자동으로 설치할 수 없습니다.");
        }

        var sumsAsset = FindAsset(release, SumsFileName);
        var signatureAsset = FindAsset(release, SignatureFileName);
        if (sumsAsset is null || signatureAsset is null)
        {
            throw new UpdateException("이 릴리스에는 서명된 파일 확인값(SHA256SUMS.txt.sig)이 없어 안전하게 설치할 수 없습니다.");
        }

        var sums = await DownloadSmallAsync(sumsAsset, "파일 확인값(SHA256SUMS.txt)", ct).ConfigureAwait(false);
        var signature = await DownloadSmallAsync(signatureAsset, "릴리스 서명(SHA256SUMS.txt.sig)", ct).ConfigureAwait(false);
        if (!VerifySignature(sums, Encoding.ASCII.GetString(signature), _publicKey))
        {
            _logger.LogWarning("업데이트 서명 확인 실패: {Tag}", release.Tag);
            throw new UpdateException("릴리스 서명이 올바르지 않아 설치하지 않았습니다. 다운로드 페이지에서 직접 받아 주세요.");
        }

        var sha = FindInSums(Encoding.UTF8.GetString(sums), asset.Name) ?? throw new UpdateException(
            $"서명된 파일 확인값에 {asset.Name} 이(가) 없어 안전하게 설치할 수 없습니다.");

        // GitHub digest 는 같은 릴리스에서 오므로 서명을 대신하지 못한다. 있으면 같은 값인지만 본다.
        if (asset.Sha256 is { } digest && !string.Equals(digest, sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("릴리스 파일의 확인값이 서명된 값과 달라 설치하지 않았습니다.");
        }

        return new UpdatePackage(release, asset, sha);
    }

    private static ReleaseAsset? FindAsset(ReleaseInfo release, string name) =>
        release.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>확인값 · 서명처럼 작은 파일을 받는다 (<see cref="MaxSmallFileBytes"/> 를 넘으면 거부).</summary>
    private async Task<byte[]> DownloadSmallAsync(ReleaseAsset asset, string what, CancellationToken ct)
    {
        RequireAllowedScheme(asset.DownloadUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"{what}을(를) 받지 못했습니다. (HTTP {(int)response.StatusCode})");
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > MaxSmallFileBytes)
                {
                    throw new UpdateException($"{what}이(가) 너무 큽니다.");
                }
            }

            return memory.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpdateException($"{what}을(를) 받지 못했습니다.", ex);
        }
    }

    /// <summary>
    /// <paramref name="data"/> 에 대한 ECDSA P-256 / SHA-256 서명(Base64, DER 또는 r||s)을
    /// <paramref name="publicKeyBase64"/>(SubjectPublicKeyInfo, Base64)로 확인한다.
    /// </summary>
    internal static bool VerifySignature(byte[] data, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            if (key.KeySize != 256)
            {
                return false;
            }

            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence) ||
                   key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="targetPath"/> 로 내려받는다. 먼저 ".part" 에 받고, SHA-256 이 맞을 때만 최종 이름으로 바꾼다.
    /// 값이 다르면 받은 파일을 지우고 <see cref="UpdateException"/>.
    /// 응답이 <see cref="StallTimeout"/> 동안 멈추면 <see cref="UpdateException"/>,
    /// <paramref name="ct"/> 로 취소하면 <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task DownloadAsync(UpdatePackage package, string targetPath, IProgress<double>? progress, CancellationToken ct)
    {
        RequireAllowedScheme(package.Asset.DownloadUrl);
        var part = targetPath + ".part";

        // 데이터를 받을 때마다 다시 맞추는 "멈춤" 타이머. 응답 헤더를 기다리는 시간에도 적용된다.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using (var response = await _http.GetAsync(package.Asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new UpdateException($"업데이트 파일을 받지 못했습니다. (HTTP {(int)response.StatusCode})");
                }

                var total = response.Content.Headers.ContentLength ?? package.Asset.Size;
                await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await ReadNetworkAsync(input, buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(1.0, (double)done / total));
                    }
                }

                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("업데이트 파일 SHA-256 불일치: {Name} 예상 {Expected}, 실제 {Actual}",
                        package.Asset.Name, package.Sha256, actual);
                    throw new UpdateException("받은 파일의 확인값(SHA-256)이 맞지 않아 설치하지 않았습니다. 잠시 뒤 다시 시도해 주세요.");
                }
            }

            File.Move(part, targetPath, overwrite: true);
            _logger.LogInformation("업데이트 파일을 받았습니다: {Name}", Path.GetFileName(targetPath));
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("업데이트 파일을 받는 중 연결이 끊겼습니다.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("업데이트 파일 다운로드가 {Seconds}초 동안 멈춰 중단했습니다: {Name}",
                StallTimeout.TotalSeconds, package.Asset.Name);
            throw new UpdateException("업데이트 서버가 응답하지 않아 다운로드를 중단했습니다. 잠시 뒤 다시 시도해 주세요.", ex);
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>
    /// 응답 스트림에서 읽는다. 연결이 끊겨 생긴 <see cref="IOException"/> 은 디스크 쓰기 오류와 구분되도록
    /// <see cref="HttpRequestException"/> 으로 바꾼다.
    /// </summary>
    private static async ValueTask<int> ReadNetworkAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        try
        {
            return await input.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // 취소(멈춤 타이머 포함)로 연결을 끊은 경우에도 IOException 이 올 수 있다.
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ex.Message, ex, ct);
            }

            throw new HttpRequestException(ex.Message, ex);
        }
    }

    private static void RequireAllowedScheme(Uri url)
    {
#if !DEBUG
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateException("업데이트 파일 주소가 HTTPS 가 아닙니다.");
        }
#endif
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
