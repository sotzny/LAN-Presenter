using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BeamerPresenter.Application;

namespace BeamerPresenter.Infrastructure;

public static class UpdateJson
{
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken token = default)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token);
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<bool> VerifyAsync(string path, long size, string sha256, CancellationToken token = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return false;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        return string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FileUpdateStateStore(string directory) : IUpdateStateStore
{
    public string StatePath => Path.Combine(directory, "update-state.json");
    public async Task<UpdatePersistentState> ReadAsync(CancellationToken cancellationToken) =>
        await UpdateJson.ReadAsync<UpdatePersistentState>(StatePath, cancellationToken) ?? new();
    public Task WriteAsync(UpdatePersistentState state, CancellationToken cancellationToken) => UpdateJson.WriteAsync(StatePath, state, cancellationToken);
}

public sealed record PortableManifest(string Version, string[] Files);

public static class PortableUpdateFiles
{
    public const string ManifestName = "update-manifest.json";
    public const string ApplicationName = "BeamerPresenter.App.exe";
    public const string UpdaterName = "BeamerPresenter.Updater.exe";
    private const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;

    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || Path.IsPathRooted(relative) ||
            relative.Split(['/', '\\']).Any(part => part is ".." or "." || part.EndsWith(' ') || part.EndsWith('.') || IsDeviceName(part)))
            throw new IOException("Invalid update path.");
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update path escapes its directory.");
        for (var current = new DirectoryInfo(Path.GetDirectoryName(path)!); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not allowed.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not allowed.");
        return path;
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            name.Length == 4 && name[3] is >= '1' and <= '9' && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal));
    }

    private static void ValidateManifest(PortableManifest manifest, string version)
    {
        if (manifest.Version != version || manifest.Files is null || manifest.Files.Length is 0 or > 20000 ||
            manifest.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
            manifest.Files.Contains(ManifestName, StringComparer.OrdinalIgnoreCase) ||
            !manifest.Files.Contains(ApplicationName, StringComparer.OrdinalIgnoreCase) ||
            !manifest.Files.Contains(UpdaterName, StringComparer.OrdinalIgnoreCase)) throw new IOException("Invalid package manifest.");
    }

    public static async Task<PortableManifest> ExtractAsync(string archivePath, string stagingDirectory, string version, CancellationToken token = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 20001) throw new IOException("Too many update files.");
        var manifestEntry = archive.GetEntry(ManifestName) ?? throw new IOException("Package manifest is missing.");
        if (manifestEntry.Length > 2 * 1024 * 1024) throw new IOException("Package manifest is too large.");
        PortableManifest manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<PortableManifest>(stream, cancellationToken: token) ?? throw new IOException("Invalid manifest.");
        ValidateManifest(manifest, version);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long extractedSize = 0;
        foreach (var entry in archive.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException("Archive links are not allowed.");
            _ = SafePath(stagingDirectory, entry.FullName.TrimEnd('/'));
            if (entry.FullName.EndsWith('/')) continue;
            if (!entries.TryAdd(entry.FullName, entry)) throw new IOException("Duplicate archive entry.");
            extractedSize = checked(extractedSize + entry.Length);
            if (extractedSize > MaximumExtractedBytes) throw new IOException("Update exceeds extraction limit.");
            if (entry.FullName != ManifestName && !manifest.Files.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase))
                throw new IOException("Unmanaged archive file.");
        }
        Directory.CreateDirectory(stagingDirectory);
        foreach (var name in manifest.Files.Append(ManifestName))
        {
            var entry = entries.GetValueOrDefault(name) ?? throw new IOException("Manifest file is missing.");
            var destination = SafePath(stagingDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, token);
        }
        return manifest;
    }

    public static async Task ApplyAsync(string stagingDirectory, string targetDirectory, string backupDirectory, string version, CancellationToken token = default)
    {
        var manifest = await UpdateJson.ReadAsync<PortableManifest>(Path.Combine(stagingDirectory, ManifestName), token)
            ?? throw new IOException("Manifest is missing.");
        ValidateManifest(manifest, version);
        var old = await UpdateJson.ReadAsync<PortableManifest>(SafePath(targetDirectory, ManifestName), token);
        var managed = manifest.Files.Append(ManifestName).Concat(old?.Files ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate and back up all collisions before changing even one target file.
        foreach (var name in managed)
        {
            var target = SafePath(targetDirectory, name);
            var backup = SafePath(backupDirectory, name);
            if (File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: false);
                existing.Add(name);
            }
        }
        var changed = new List<string>();
        try
        {
            foreach (var name in managed)
            {
                token.ThrowIfCancellationRequested();
                var target = SafePath(targetDirectory, name);
                var source = SafePath(stagingDirectory, name);
                if (manifest.Files.Contains(name, StringComparer.OrdinalIgnoreCase) || name == ManifestName)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    ReplaceFile(source, target);
                }
                else if (File.Exists(target)) File.Delete(target);
                changed.Add(name);
            }
        }
        catch
        {
            foreach (var name in changed.AsEnumerable().Reverse())
            {
                var target = SafePath(targetDirectory, name);
                if (existing.Contains(name)) ReplaceFile(SafePath(backupDirectory, name), target);
                else if (File.Exists(target)) File.Delete(target);
            }
            throw;
        }
    }

    private static void ReplaceFile(string source, string target)
    {
        var temporary = target + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            // Rename on the same volume prevents a failed copy from truncating the target.
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
