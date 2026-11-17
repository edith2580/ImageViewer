using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ImageViewer.Services;
using ImageViewer.Services.Updates;
using ImageViewer.ViewModels;

namespace ImageViewer.Tests;

public sealed class UpdateTests : IDisposable
{
    private static readonly Uri Feed = new("https://example.test/repos/o/r/");
    private readonly string _dir = Directory.CreateTempSubdirectory("iv-update-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>요청 주소별로 응답을 돌려주는 가짜 서버.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        public void Json(string url, string json) =>
            Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public void Bytes(string url, byte[] bytes) =>
            Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var make)
                ? make()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static UpdateService Service(FakeServer server, string current = "1.0.5") =>
        new(http: new HttpClient(server), feedBase: Feed) { CurrentVersion = Version.Parse(current) };

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string ReleaseJson(string tag, string assetsJson, bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/o/r/releases/tag/{{tag}}",
          "body": "변경 내용",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [{{assetsJson}}]
        }
        """;

    private static string Asset(string name, string? digest = null, long size = 0) => $$"""
        { "name": "{{name}}", "size": {{size}}, "browser_download_url": "https://example.test/dl/{{name}}"{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} }
        """;

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("v1.0.6-beta", null)]
    [InlineData("release", null)]
    [InlineData("", null)]
    public void Tags_are_parsed_as_versions(string tag, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), UpdateService.ParseVersion(tag));

    [Fact]
    public void Release_json_is_parsed_and_prereleases_are_ignored()
    {
        var hash = new string('a', 64);
        var release = UpdateService.ParseRelease(ReleaseJson("v1.0.6",
            Asset("ImageViewer-1.0.6-setup.exe", hash, 100) + "," + Asset("ImageViewer-1.0.6-portable.exe")))!;

        Assert.Equal(new Version(1, 0, 6), release.Version);
        Assert.Equal("https://github.com/o/r/releases/tag/v1.0.6", release.PageUrl.AbsoluteUri);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(hash, release.Assets[0].Sha256);
        Assert.Equal(100, release.Assets[0].Size);
        Assert.Null(release.Assets[1].Sha256);

        Assert.Null(UpdateService.ParseRelease(ReleaseJson("v1.0.7", "", prerelease: true)));
        Assert.Null(UpdateService.ParseRelease(ReleaseJson("nightly", "")));
        Assert.Null(UpdateService.ParseRelease("{ not json"));
    }

    [Fact]
    public void Asset_matches_install_kind()
    {
        var release = UpdateService.ParseRelease(ReleaseJson("v1.0.6",
            Asset("ImageViewer-1.0.6-setup.exe") + "," + Asset("ImageViewer-1.0.6-portable.exe") + "," + Asset("SHA256SUMS.txt")))!;
        Assert.Equal("ImageViewer-1.0.6-setup.exe", UpdateService.SelectAsset(release, InstallKind.Installed)!.Name);
        Assert.Equal("ImageViewer-1.0.6-portable.exe", UpdateService.SelectAsset(release, InstallKind.Portable)!.Name);
    }

    [Fact]
    public void Sums_file_lookup_accepts_sha256sum_formats()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = $"{a}  ImageViewer-1.0.6-setup.exe\r\n{b} *ImageViewer-1.0.6-portable.exe\n";
        Assert.Equal(a, UpdateService.FindInSums(sums, "ImageViewer-1.0.6-setup.exe"));
        Assert.Equal(b.ToLowerInvariant(), UpdateService.FindInSums(sums, "imageviewer-1.0.6-PORTABLE.exe"));
        Assert.Null(UpdateService.FindInSums(sums, "other.exe"));
        Assert.Null(UpdateService.FindInSums("xyz  ImageViewer-1.0.6-setup.exe", "ImageViewer-1.0.6-setup.exe"));
    }

    [Fact]
    public void Install_kind_is_detected_from_install_location()
    {
        var installDir = Path.Combine(_dir, "Programs", "ImageViewer");
        var toolsDir = Path.Combine(_dir, "Tools");
        var devDir = Path.Combine(_dir, "src", "bin", "Debug");

        Assert.Equal(InstallKind.Installed, UpdateInstaller.Detect(
            Path.Combine(installDir, "ImageViewer.exe"), installDir + Path.DirectorySeparatorChar, true));
        Assert.Equal(InstallKind.Portable, UpdateInstaller.Detect(
            Path.Combine(toolsDir, "ImageViewer.exe"), installDir, false));
        Assert.Equal(InstallKind.Portable, UpdateInstaller.Detect(Path.Combine(toolsDir, "ImageViewer.exe"), null, false));
        Assert.Equal(InstallKind.Unsupported, UpdateInstaller.Detect(Path.Combine(devDir, "ImageViewer.exe"), null, true));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(InstallKind.Installed, UpdateInstaller.Detect(
                @"C:\Users\u\AppData\Local\Programs\ImageViewer\ImageViewer.exe",
                @"C:\Users\u\AppData\Local\Programs\imageviewer\", true));
        }
    }

    [Fact]
    public void Installer_is_run_silently_with_relaunch_path()
    {
        Assert.Equal(@"/S /RELAUNCH=C:\사진 폴더\a b.jpg",
            UpdateInstaller.InstallerStartInfo(@"C:\t\setup.exe", @"C:\사진 폴더\a b.jpg").Arguments);
        Assert.Equal("/S /RELAUNCH", UpdateInstaller.InstallerStartInfo(@"C:\t\setup.exe", null).Arguments);
        // NSIS 가 " /" 를 다음 옵션으로 읽으므로 그런 경로는 넘기지 않는다.
        Assert.Equal("/S /RELAUNCH", UpdateInstaller.InstallerStartInfo(@"C:\t\setup.exe", @"C:\a /b\c.jpg").Arguments);
    }

    [Fact]
    public async Task Latest_release_request_identifies_the_client()
    {
        var server = new FakeServer();
        server.Json(Feed + "releases/latest", ReleaseJson("v1.0.6", ""));
        using var service = Service(server);

        var release = await service.GetLatestReleaseAsync(CancellationToken.None);
        Assert.Equal(new Version(1, 0, 6), release!.Version);
        var request = Assert.Single(server.Requests);
        Assert.Contains("ImageViewer-Updater", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Missing_release_is_null_and_server_errors_are_reported()
    {
        var server = new FakeServer();
        using var service = Service(server);
        Assert.Null(await service.GetLatestReleaseAsync(CancellationToken.None)); // 404

        server.Routes[Feed + "releases/latest"] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync(CancellationToken.None));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Hash_comes_from_digest_or_sums_file_and_is_required()
    {
        var server = new FakeServer();
        using var service = Service(server);
        var digest = new string('c', 64);

        var withDigest = UpdateService.ParseRelease(ReleaseJson("v1.0.6", Asset("ImageViewer-1.0.6-portable.exe", digest)))!;
        Assert.Equal(digest, (await service.ResolvePackageAsync(withDigest, InstallKind.Portable, CancellationToken.None)).Sha256);

        var fromSums = new string('d', 64);
        server.Bytes("https://example.test/dl/SHA256SUMS.txt", Encoding.UTF8.GetBytes($"{fromSums}  ImageViewer-1.0.6-setup.exe\n"));
        var withSums = UpdateService.ParseRelease(ReleaseJson("v1.0.6", Asset("ImageViewer-1.0.6-setup.exe") + "," + Asset("SHA256SUMS.txt")))!;
        Assert.Equal(fromSums, (await service.ResolvePackageAsync(withSums, InstallKind.Installed, CancellationToken.None)).Sha256);

        var none = UpdateService.ParseRelease(ReleaseJson("v1.0.6", Asset("ImageViewer-1.0.6-setup.exe")))!;
        await Assert.ThrowsAsync<UpdateException>(() => service.ResolvePackageAsync(none, InstallKind.Installed, CancellationToken.None));

        // 포터블 파일이 없는 릴리스
        await Assert.ThrowsAsync<UpdateException>(() => service.ResolvePackageAsync(withSums, InstallKind.Portable, CancellationToken.None));
    }

    [Fact]
    public async Task Download_is_kept_only_when_the_hash_matches()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        var server = new FakeServer();
        server.Bytes("https://example.test/dl/ImageViewer-1.0.6-portable.exe", data);
        using var service = Service(server);
        var release = UpdateService.ParseRelease(ReleaseJson("v1.0.6", Asset("ImageViewer-1.0.6-portable.exe", Sha(data))))!;
        var package = await service.ResolvePackageAsync(release, InstallKind.Portable, CancellationToken.None);

        var target = Path.Combine(_dir, "ImageViewer.exe.new");
        var reports = new List<double>();
        await service.DownloadAsync(package, target, new SyncProgress(reports.Add), CancellationToken.None);
        Assert.Equal(data, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(1.0, reports[^1], 3);

        // 내용이 바뀐 파일은 버린다.
        File.Delete(target);
        var tampered = package with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(tampered, target, null, CancellationToken.None));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public void Portable_swap_keeps_the_old_exe_until_cleanup_and_rolls_back_on_failure()
    {
        var exe = Path.Combine(_dir, "ImageViewer.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(exe + ".new", "new");

        UpdateInstaller.SwapPortable(exe, exe + ".new");
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));

        // 새 파일이 없으면 원래 exe 를 되돌려 놓는다.
        Assert.Throws<UpdateException>(() => UpdateInstaller.SwapPortable(exe, exe + ".missing"));
        Assert.Equal("new", File.ReadAllText(exe));
    }

    [Fact]
    public async Task Leftovers_are_removed_on_next_start()
    {
        var exe = Path.Combine(_dir, "ImageViewer.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(exe + ".old", "x");
        File.WriteAllText(exe + ".new", "x");

        await UpdateInstaller.CleanupLeftoversAsync(exe, attempts: 2);
        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }

    private sealed class FakeLifetime : IAppLifetime
    {
        public ProcessStartInfo? Ran { get; private set; }

        public void ExitAndRun(ProcessStartInfo afterExit) => Ran = afterExit;
    }

    private SettingsService Settings()
    {
        var settings = new SettingsService(settingsPath: Path.Combine(_dir, "settings.json"));
        settings.Load();
        return settings;
    }

    [Fact]
    public async Task Auto_check_runs_once_a_day_and_respects_skipped_version()
    {
        var server = new FakeServer();
        server.Json(Feed + "releases/latest", ReleaseJson("v1.0.6", ""));
        using var service = Service(server);
        var settings = Settings();
        using var vm = new UpdateViewModel(service, settings, new FakeLifetime(), kind: InstallKind.Portable);
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

        await vm.AutoCheckAsync(TimeSpan.Zero, now);
        Assert.True(vm.IsNoticeVisible);
        Assert.Equal(UpdateState.Available, vm.State);
        Assert.Equal("1.0.6", vm.AvailableVersionText);
        Assert.Equal(now, settings.Current.Update.LastCheckUtc);

        // 같은 날 다시 시작하면 확인하지 않는다.
        vm.DismissCommand.Execute(null);
        await vm.AutoCheckAsync(TimeSpan.Zero, now.AddHours(3));
        Assert.Single(server.Requests);

        // 건너뛴 버전은 다음 날 확인해도 알리지 않는다.
        vm.SkipVersionCommand.Execute(null);
        Assert.Equal("1.0.6", settings.Current.Update.SkippedVersion);
        using var nextDay = new UpdateViewModel(service, settings, new FakeLifetime(), kind: InstallKind.Portable);
        await nextDay.AutoCheckAsync(TimeSpan.Zero, now.AddDays(1));
        Assert.Equal(2, server.Requests.Count);
        Assert.False(nextDay.IsNoticeVisible);

        // 자동 확인을 끄면 요청하지 않는다.
        settings.Current.Update.AutoCheck = false;
        using var off = new UpdateViewModel(service, settings, new FakeLifetime(), kind: InstallKind.Portable);
        await off.AutoCheckAsync(TimeSpan.Zero, now.AddDays(5));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Up_to_date_check_shows_no_notice()
    {
        var server = new FakeServer();
        server.Json(Feed + "releases/latest", ReleaseJson("v1.0.5", ""));
        using var service = Service(server);
        using var vm = new UpdateViewModel(service, Settings(), new FakeLifetime(), kind: InstallKind.Portable);

        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.UpToDate, vm.State);
        Assert.False(vm.IsNoticeVisible);
        Assert.False(vm.CanInstall);
    }

    [Fact]
    public async Task Portable_update_swaps_the_exe_and_relaunches_with_the_current_path()
    {
        var newExe = RandomNumberGenerator.GetBytes(50_000);
        var server = new FakeServer();
        server.Json(Feed + "releases/latest", ReleaseJson("v1.0.6", Asset("ImageViewer-1.0.6-portable.exe", Sha(newExe))));
        server.Bytes("https://example.test/dl/ImageViewer-1.0.6-portable.exe", newExe);
        using var service = Service(server);

        var exe = Path.Combine(_dir, "ImageViewer.exe");
        File.WriteAllText(exe, "old");
        var lifetime = new FakeLifetime();
        using var vm = new UpdateViewModel(service, Settings(), lifetime, kind: InstallKind.Portable, exePath: exe)
        {
            RelaunchPathProvider = () => @"D:\사진\a.jpg",
        };

        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.True(vm.CanInstall);
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Installing, vm.State);
        Assert.Equal(newExe, File.ReadAllBytes(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.Equal(exe, lifetime.Ran!.FileName);
        Assert.Equal([@"D:\사진\a.jpg"], lifetime.Ran.ArgumentList);
    }

    [Fact]
    public async Task Failed_download_keeps_the_app_and_offers_the_release_page()
    {
        var server = new FakeServer();
        server.Json(Feed + "releases/latest", ReleaseJson("v1.0.6", Asset("ImageViewer-1.0.6-portable.exe", new string('e', 64))));
        server.Bytes("https://example.test/dl/ImageViewer-1.0.6-portable.exe", [1, 2, 3]); // 확인값과 다름
        using var service = Service(server);

        var exe = Path.Combine(_dir, "ImageViewer.exe");
        File.WriteAllText(exe, "old");
        var lifetime = new FakeLifetime();
        using var vm = new UpdateViewModel(service, Settings(), lifetime, kind: InstallKind.Portable, exePath: exe);

        await vm.CheckNowCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("SHA-256", vm.StatusText);
        Assert.Equal("다운로드 페이지", vm.InstallButtonText);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
        Assert.Null(lifetime.Ran);
    }
}
