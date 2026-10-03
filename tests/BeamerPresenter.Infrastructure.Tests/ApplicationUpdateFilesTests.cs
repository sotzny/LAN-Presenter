using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class ApplicationUpdateFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BeamerPresenter.UpdateTests", Guid.NewGuid().ToString("N"));
    public ApplicationUpdateFilesTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    [Fact]
    public async Task State_is_atomic_and_hash_verification_requires_exact_length_and_digest()
    {
        var store = new FileUpdateStateStore(root);
        Assert.Equal(new UpdatePersistentState(), await store.ReadAsync(default));
        var data = Path.Combine(root, "package"); await File.WriteAllBytesAsync(data, [1, 2, 3]);
        var sha = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
        Assert.True(await UpdateJson.VerifyAsync(data, 3, sha));
        Assert.False(await UpdateJson.VerifyAsync(data, 4, sha));
        Assert.False(await UpdateJson.VerifyAsync(data, 3, new string('0', 64)));
        Assert.False(await UpdateJson.VerifyAsync(data, 3, "bad"));
        Assert.False(await UpdateJson.VerifyAsync(data + "missing", 3, sha));
        var state = new UpdatePersistentState(new("1.2.0", data, false, sha, 3), DateTimeOffset.UtcNow.AddHours(1));
        await store.WriteAsync(state, default);
        Assert.Equal(state, await store.ReadAsync(default));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        await store.WriteAsync(state with { Error = "failed" }, default);
        Assert.Equal("failed", (await store.ReadAsync(default)).Error);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("x/../../escape")]
    [InlineData("C:/escape")]
    [InlineData("file:stream")]
    [InlineData("CON")]
    [InlineData("a/NUL.txt")]
    [InlineData("x/COM1.exe")]
    [InlineData("x/./file")]
    [InlineData("file.")]
    [InlineData("file ")]
    public void Paths_must_stay_inside_the_update_directory(string path) => Assert.Throws<IOException>(() => PortableUpdateFiles.SafePath(root, path));

    private async Task<string> ArchiveAsync(string version = "1.2.0", string? extra = null, bool manifest = true)
    {
        var zipPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var names = new[] { PortableUpdateFiles.ApplicationName, PortableUpdateFiles.UpdaterName, "wwwroot/main.js" };
        foreach (var name in names)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("new:" + name);
        }
        if (manifest)
        {
            await using var stream = archive.CreateEntry(PortableUpdateFiles.ManifestName).Open();
            await JsonSerializer.SerializeAsync(stream, new PortableManifest(version, names));
        }
        if (extra is not null) { using var writer = new StreamWriter(archive.CreateEntry(extra).Open()); writer.Write("unsafe"); }
        return zipPath;
    }

    [Fact]
    public async Task Portable_update_removes_only_previous_managed_files_and_retains_unknown_files()
    {
        var target = Path.Combine(root, "portable with spaces"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "user.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(target, "obsolete.dll"), "old");
        await File.WriteAllTextAsync(Path.Combine(target, PortableUpdateFiles.ApplicationName), "old app");
        await UpdateJson.WriteAsync(Path.Combine(target, PortableUpdateFiles.ManifestName), new PortableManifest("1.1.1", ["obsolete.dll", PortableUpdateFiles.ApplicationName]));
        var staged = Path.Combine(root, "staged");
        await PortableUpdateFiles.ExtractAsync(await ArchiveAsync(), staged, "1.2.0");
        await PortableUpdateFiles.ApplyAsync(staged, target, Path.Combine(root, "backup"), "1.2.0");
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "user.txt")));
        Assert.False(File.Exists(Path.Combine(target, "obsolete.dll")));
        Assert.Equal("new:" + PortableUpdateFiles.ApplicationName, await File.ReadAllTextAsync(Path.Combine(target, PortableUpdateFiles.ApplicationName)));
        Assert.Equal("old app", await File.ReadAllTextAsync(Path.Combine(root, "backup", PortableUpdateFiles.ApplicationName)));
    }

    [Fact]
    public async Task Locked_file_rolls_back_already_replaced_program_files()
    {
        var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, PortableUpdateFiles.ApplicationName), "old app");
        await File.WriteAllTextAsync(Path.Combine(target, PortableUpdateFiles.UpdaterName), "old helper");
        var staged = Path.Combine(root, "staged"); await PortableUpdateFiles.ExtractAsync(await ArchiveAsync(), staged, "1.2.0");
        using var locked = new FileStream(Path.Combine(target, PortableUpdateFiles.UpdaterName), FileMode.Open, FileAccess.Read, FileShare.Read);
        var failure = await Record.ExceptionAsync(() => PortableUpdateFiles.ApplyAsync(staged, target, Path.Combine(root, "backup"), "1.2.0"));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("old app", await File.ReadAllTextAsync(Path.Combine(target, PortableUpdateFiles.ApplicationName)));
        Assert.Equal("old helper", await File.ReadAllTextAsync(Path.Combine(target, PortableUpdateFiles.UpdaterName)));
    }

    [Theory]
    [InlineData("../escape", true, "1.2.0")]
    [InlineData("extra.txt", true, "1.2.0")]
    [InlineData(null, false, "1.2.0")]
    [InlineData(null, true, "1.1.1")]
    public async Task Unmanaged_traversing_missing_or_wrong_version_packages_are_rejected(string? extra, bool manifest, string version)
    {
        var zip = await ArchiveAsync(version, extra, manifest);
        await Assert.ThrowsAsync<IOException>(() => PortableUpdateFiles.ExtractAsync(zip, Path.Combine(root, "stage"), "1.2.0"));
        Assert.False(File.Exists(Path.Combine(root, "escape")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitHub_package_is_selected_downloaded_verified_and_rechecked(bool installed)
    {
        var bytes = new byte[] { 4, 5, 6 };
        using var http = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
            ? new(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(bytes)) }
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var source = new GitHubApplicationReleaseSource(http, root, () => installed);
        var release = await source.GetLatestAsync(default);
        var progress = new List<int>();
        var prepared = await source.PrepareAsync(release!, progress.Add, default);
        Assert.Equal(installed, prepared.Installed);
        Assert.True(await source.IsPreparedAsync(prepared, default)); Assert.Contains(100, progress);
        await File.WriteAllBytesAsync(prepared.PackagePath, [7, 8, 9]);
        Assert.False(await source.IsPreparedAsync(prepared, default));
        Assert.False(await source.IsPreparedAsync(prepared with { PackagePath = Path.GetTempPath() }, default));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("url")]
    [InlineData("size")]
    [InlineData("missing")]
    public async Task Release_assets_without_expected_metadata_are_rejected(string invalid)
    {
        var json = ReleaseJson([1, 2, 3]);
        json = invalid switch
        {
            "digest" => json.Replace("sha256:", "none:"),
            "url" => json.Replace("github.com/", "evil.example/"),
            "size" => json.Replace("\"size\":3", "\"size\":0"),
            _ => json.Replace("-Setup.exe", "-Other.exe")
        };
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var source = new GitHubApplicationReleaseSource(http, root, () => false);
        await Assert.ThrowsAsync<IOException>(() => source.GetLatestAsync(default));
    }

    [Fact]
    public async Task Missing_prerelease_and_corrupted_download_do_not_produce_ready_packages()
    {
        using var missing = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        Assert.Null(await new GitHubApplicationReleaseSource(missing, root, () => false).GetLatestAsync(default));
        using var preview = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson([1]).Replace("\"prerelease\":false", "\"prerelease\":true")) }));
        Assert.Null(await new GitHubApplicationReleaseSource(preview, root, () => false).GetLatestAsync(default));
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9, 9]) }));
        var source = new GitHubApplicationReleaseSource(http, root, () => false);
        var hash = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
        var asset = new UpdateAsset("bad.zip", "https://github.com/sotzny/LAN-Presenter/releases/download/v1.2.0/bad.zip", 3, hash);
        await Assert.ThrowsAsync<IOException>(() => source.PrepareAsync(new("1.2.0", asset, asset), _ => { }, default));
        Assert.Empty(Directory.GetFiles(root));
    }

    private static string ReleaseJson(byte[] bytes) => JsonSerializer.Serialize(new
    {
        tag_name = "v1.2.0",
        draft = false,
        prerelease = false,
        assets = new[] { "BeamerPresenter-1.2.0-Setup.exe", "BeamerPresenter-1.2.0-win-x64-portable.zip" }.Select(name => new
        {
            name,
            browser_download_url = "https://github.com/sotzny/LAN-Presenter/releases/download/v1.2.0/" + name,
            size = bytes.Length,
            digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes))
        })
    });
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
}
