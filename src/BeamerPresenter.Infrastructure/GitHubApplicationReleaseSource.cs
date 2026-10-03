using System.Net;
using System.Text.Json;
using BeamerPresenter.Application;

namespace BeamerPresenter.Infrastructure;

public sealed class GitHubApplicationReleaseSource(HttpClient client, string directory, Func<bool> isInstalled) : IApplicationReleaseSource
{
    private const string Repository = "sotzny/LAN-Presenter";
    private const long MaximumPackageSize = 1024L * 1024 * 1024;

    public async Task<ApplicationRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("LAN-Presenter-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        try
        {
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() || StableReleaseVersion.Parse(tag) is not { } version) return null;
            var normalized = version.ToString(3);
            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            return new(normalized, ParseAsset(assets, tag!, $"BeamerPresenter-{normalized}-Setup.exe"),
                ParseAsset(assets, tag!, $"BeamerPresenter-{normalized}-win-x64-portable.zip"));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IOException("Invalid release metadata.", exception);
        }
    }

    private static UpdateAsset ParseAsset(JsonElement[] assets, string tag, string name)
    {
        var matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new IOException("Release asset is missing or ambiguous.");
        var asset = matches[0];
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
        var url = asset.GetProperty("browser_download_url").GetString();
        var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        if (url != expected || size <= 0 || size > MaximumPackageSize || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new IOException("Release asset is not verifiable.");
        return new(name, url, size, digest[7..]);
    }

    public async Task<PreparedUpdate> PrepareAsync(ApplicationRelease release, Action<int> progress, CancellationToken cancellationToken)
    {
        var installed = isInstalled();
        var asset = installed ? release.Installer : release.Portable;
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, asset.Name);
        var partial = destination + ".partial";
        try
        {
            var url = new Uri(asset.Url);
            HttpResponseMessage? response = null;
            try
            {
                for (var redirect = 0; redirect < 6; redirect++)
                {
                    if (url.Scheme != "https" || url.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                        throw new IOException("Invalid download redirect.");
                    response?.Dispose();
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd("LAN-Presenter-Updater/1.0");
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                    { url = new Uri(url, location); continue; }
                    response.EnsureSuccessStatusCode();
                    break;
                }
                if (response is null || (int)response.StatusCode is >= 300 and < 400) throw new IOException("Too many download redirects.");
                if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new IOException("Unexpected package size.");
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        received += count;
                        if (received > asset.Size) throw new IOException("Package is too large.");
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        progress((int)(received * 100 / asset.Size));
                    }
                }
                if (!await UpdateJson.VerifyAsync(partial, asset.Size, asset.Sha256, cancellationToken)) throw new IOException("Package checksum mismatch.");
                File.Move(partial, destination, overwrite: true);
                return new(release.Version, destination, installed, asset.Sha256, asset.Size);
            }
            finally { response?.Dispose(); }
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public Task<bool> IsPreparedAsync(PreparedUpdate prepared, CancellationToken cancellationToken)
    {
        if (prepared.Installed != isInstalled() ||
            !Path.GetFullPath(prepared.PackagePath).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);
        return UpdateJson.VerifyAsync(prepared.PackagePath, prepared.Size, prepared.Sha256, cancellationToken);
    }
}
