using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.Infrastructure;

internal sealed class SqliteMediaScanner(
    IDbContextFactory<PresenterDbContext> contextFactory,
    IMediaFolderService mediaFolderService,
    IMediaProbeQueue mediaProbeQueue,
    ILogger<SqliteMediaScanner> logger) : IMediaScanner, IMediaScannerStatus, IMediaCleanupService
{
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private MediaScannerSnapshot current = new(false, null, null, null);

    public MediaScannerSnapshot Current => Volatile.Read(ref current);

    public async Task<int> RemoveMissingAsync(int? mediaId = null, CancellationToken cancellationToken = default)
    {
        await scanGate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var query = context.Videos.AsQueryable();
            if (mediaId is { } id)
            {
                query = query.Where(video => video.Id == id);
            }

            var videos = await query.ToListAsync(cancellationToken);
            // An explicit cleanup also applies to disconnected folders. Recheck the file
            // instead of trusting a potentially stale IsAvailable flag.
            var missing = videos.Where(video => IsConfirmedMissing(video.FullPath)).ToList();
            context.Videos.RemoveRange(missing);
            await context.SaveChangesAsync(cancellationToken);
            return missing.Count;
        }
        finally
        {
            scanGate.Release();
        }
    }

    public async Task<MediaScanResult> ScanAllAsync(CancellationToken cancellationToken = default)
    {
        await scanGate.WaitAsync(cancellationToken);
        Volatile.Write(ref current, Current with { IsRunning = true, LastError = null });
        try
        {
            var result = await ScanAllCoreAsync(cancellationToken);
            Volatile.Write(ref current, new MediaScannerSnapshot(false, DateTimeOffset.UtcNow, result, null));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref current, Current with { IsRunning = false });
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref current, new MediaScannerSnapshot(false, DateTimeOffset.UtcNow, Current.LastResult, exception.Message));
            throw;
        }
        finally
        {
            scanGate.Release();
        }
    }

    private async Task<MediaScanResult> ScanAllCoreAsync(CancellationToken cancellationToken)
    {
        var folders = (await mediaFolderService.GetAllAsync(cancellationToken)).Where(folder => folder.Enabled).ToList();
        var discoveredFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var scannedFolders = new List<MediaFolder>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.Path))
            {
                logger.LogWarning("Configured media folder {MediaFolder} is not available", folder.Path);
                continue;
            }

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = folder.IncludeSubdirectories,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            try
            {
                if ((File.GetAttributes(folder.Path) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                // Only a complete enumeration can establish that a file has disappeared.
                var files = Directory.EnumerateFiles(folder.Path, "*", enumerationOptions)
                    .Where(MediaFileSupport.IsSupported).Select(path => new FileInfo(path)).ToList();
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Read metadata before accepting the folder, in case it disconnects.
                    _ = file.Length;
                    discoveredFiles[file.FullName] = file;
                }
                scannedFolders.Add(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Configured media folder {MediaFolder} could not be scanned completely", folder.Path);
            }
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existingFiles = await context.Videos.ToListAsync(cancellationToken);
        var existingByPath = existingFiles.ToDictionary(video => video.FullPath, StringComparer.OrdinalIgnoreCase);
        var scanTimestamp = DateTimeOffset.UtcNow;
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var probeAssets = new List<VideoAsset>();
        foreach (var (path, file) in discoveredFiles)
        {
            var lastWriteUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            if (!existingByPath.TryGetValue(path, out var video))
            {
                var addedAsset = new VideoAsset
                {
                    FileName = file.Name,
                    FullPath = file.FullName,
                    FileSize = file.Length,
                    AddedAtUtc = scanTimestamp,
                    LastWriteUtc = lastWriteUtc,
                    LastScannedUtc = scanTimestamp,
                    IsAvailable = true
                };
                context.Videos.Add(addedAsset);
                probeAssets.Add(addedAsset);
                added++;
                continue;
            }

            var changed = video.FileSize != file.Length || video.LastWriteUtc != lastWriteUtc || !video.IsAvailable;
            video.FileName = file.Name;
            video.FileSize = file.Length;
            video.LastWriteUtc = lastWriteUtc;
            video.LastScannedUtc = scanTimestamp;
            video.IsAvailable = true;
            if (changed)
            {
                ResetProbeState(video);
                probeAssets.Add(video);
                updated++;
            }
            else
            {
                if (video.ProbeStatus == MediaProbeStatus.Unknown)
                {
                    probeAssets.Add(video);
                }

                unchanged++;
            }
        }

        var missing = 0;
        foreach (var video in existingFiles.Where(video => !discoveredFiles.ContainsKey(video.FullPath)))
        {
            if (IsConfirmedMissing(video.FullPath) && scannedFolders.Any(folder =>
                IsInFolder(video.FullPath, folder) && IsFolderReadable(folder.Path)))
            {
                context.Videos.Remove(video);
                missing++;
                continue;
            }

            if (!video.IsAvailable || File.Exists(video.FullPath))
            {
                continue;
            }

            video.IsAvailable = false;
            video.LastScannedUtc = scanTimestamp;
            video.ProbeStatus = MediaProbeStatus.Missing;
            video.PlaybackStatus = MediaPlaybackStatus.Unsupported;
            video.ProbeError = "Die Mediendatei wurde nicht gefunden.";
            missing++;
        }

        await context.SaveChangesAsync(cancellationToken);
        foreach (var asset in probeAssets)
        {
            await mediaProbeQueue.QueueAsync(asset.Id, asset.FullPath, cancellationToken);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Media reconciliation completed: {Added} added, {Updated} updated, {Missing} missing, {Unchanged} unchanged",
                added,
                updated,
                missing,
                unchanged);
        }
        return new MediaScanResult(added, updated, missing, unchanged);
    }

    private static bool IsInFolder(string fullPath, MediaFolder folder)
    {
        var relative = Path.GetRelativePath(folder.Path, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            (!folder.IncludeSubdirectories && Path.GetDirectoryName(relative) != string.Empty))
        {
            return false;
        }

        // Enumeration skips reparse points: absence below one is not proof of deletion.
        var parent = Path.GetDirectoryName(Path.GetFullPath(fullPath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
        while (parent is not null && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A removed ordinary subdirectory is eligible for automatic cleanup.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            parent = Path.GetDirectoryName(parent);
        }
        return true;
    }

    private static bool IsFolderReadable(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = entries.MoveNext();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsConfirmedMissing(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ResetProbeState(VideoAsset video)
    {
        video.ProbeStatus = MediaProbeStatus.Unknown;
        video.PlaybackStatus = MediaPlaybackStatus.Unknown;
        video.ProbeError = null;
    }
}

internal sealed class MediaReconciliationWorker(
    IMediaScanner mediaScanner,
    ILogger<MediaReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ScanSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(ReconciliationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ScanSafelyAsync(stoppingToken);
        }
    }

    private async Task ScanSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await mediaScanner.ScanAllAsync(cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Media reconciliation stopped because the host is shutting down");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Media reconciliation failed");
        }
    }
}
