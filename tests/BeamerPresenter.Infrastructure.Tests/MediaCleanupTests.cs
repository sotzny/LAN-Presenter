using System.Security.AccessControl;
using System.Security.Principal;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class MediaCleanupTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
    private ServiceProvider provider = null!;
    private string MediaDirectory => Path.Combine(root, "Media");
    private IDbContextFactory<PresenterDbContext> Factory => provider.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
    private IMediaScanner Scanner => provider.GetRequiredService<IMediaScanner>();
    private IMediaCleanupService Cleanup => provider.GetRequiredService<IMediaCleanupService>();
    private IMediaLibraryService Library => provider.GetRequiredService<IMediaLibraryService>();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(MediaDirectory);
        var data = Path.Combine(root, "Data");
        PresenterDatabase.GetConfiguredWebPort(data);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPresenterInfrastructure(data);
        provider = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Automatic_cleanup_removes_previously_missing_records_and_preserves_history()
    {
        await provider.GetRequiredService<IMediaFolderService>().AddAsync(MediaDirectory, true);
        var missing = await AddVideoAsync("deleted.mp4", available: false);
        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.PlaybackHistory.Add(new PlaybackHistory { MediaId = missing.Id, StartedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var result = await Scanner.ScanAllAsync();

        Assert.Equal(1, result.Missing);
        Assert.Empty(await Library.GetAllAsync());
        await using var verification = await Factory.CreateDbContextAsync();
        Assert.Equal(missing.Id, (await verification.PlaybackHistory.SingleAsync()).MediaId);
        Assert.Equal(0, (await Scanner.ScanAllAsync()).Missing);
    }

    [Fact]
    public async Task Disconnected_folder_is_retained_and_reconnection_restores_same_record()
    {
        await provider.GetRequiredService<IMediaFolderService>().AddAsync(MediaDirectory, true);
        var videoPath = Path.Combine(MediaDirectory, "retained.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3]);
        await Scanner.ScanAllAsync();
        var original = Assert.Single(await Library.GetAllAsync());
        await Library.SetEnabledAsync(original.Id, false);
        var disconnected = Path.Combine(root, "Disconnected");
        Directory.Move(MediaDirectory, disconnected);

        await Scanner.ScanAllAsync();
        await Scanner.ScanAllAsync();

        var retained = Assert.Single(await Library.GetAllAsync());
        Assert.Equal(original.Id, retained.Id);
        Assert.False(retained.IsAvailable);
        Assert.False(retained.Enabled);
        Directory.Move(disconnected, MediaDirectory);
        await Scanner.ScanAllAsync();
        var restored = Assert.Single(await Library.GetAllAsync());
        Assert.Equal(original.Id, restored.Id);
        Assert.True(restored.IsAvailable);
        Assert.False(restored.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_cleanup_respects_folder_scope(bool disabled)
    {
        var folder = await provider.GetRequiredService<IMediaFolderService>().AddAsync(MediaDirectory, false);
        var video = await AddVideoAsync(disabled ? "disabled.mp4" : Path.Combine("Nested", "out-of-scope.mp4"), available: false);
        if (disabled)
        {
            await using var db = await Factory.CreateDbContextAsync();
            (await db.MediaFolders.SingleAsync(item => item.Id == folder.Id)).Enabled = false;
            await db.SaveChangesAsync();
        }

        await Scanner.ScanAllAsync();

        Assert.Equal(video.Id, Assert.Single(await Library.GetAllAsync()).Id);
    }

    [Fact]
    public async Task Incomplete_scan_due_to_inaccessible_subdirectory_does_not_remove_records()
    {
        if (!OperatingSystem.IsWindows()) return;
        await provider.GetRequiredService<IMediaFolderService>().AddAsync(MediaDirectory, true);
        var video = await AddVideoAsync("missing.mp4", available: false);
        var blocked = Directory.CreateDirectory(Path.Combine(MediaDirectory, "Blocked"));
        var restricted = blocked.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        var denyListing = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        restricted.AddAccessRule(denyListing);
        try
        {
            blocked.SetAccessControl(restricted);
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFiles(blocked.FullName));

            await Scanner.ScanAllAsync();

            Assert.Equal(video.Id, Assert.Single(await Library.GetAllAsync()).Id);
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(denyListing);
            blocked.SetAccessControl(restricted);
        }
    }

    [Fact]
    public async Task Unconfigured_folder_is_not_automatically_cleaned()
    {
        var video = await AddVideoAsync("unconfigured.mp4", available: false);
        await Scanner.ScanAllAsync();
        Assert.Equal(video.Id, Assert.Single(await Library.GetAllAsync()).Id);
    }

    [Fact]
    public async Task Manual_single_and_bulk_cleanup_remove_offline_entries_but_keep_existing_files()
    {
        var first = await AddVideoAsync("first.mp4", available: false);
        var second = await AddVideoAsync("second.mp4", available: false);
        var present = await AddVideoAsync("present.mp4", available: false);
        await File.WriteAllBytesAsync(present.FullPath, [1, 2, 3]);
        var offline = await AddVideoAsync(Path.Combine("Offline", "offline.mp4"), available: false);

        Assert.Equal(0, await Cleanup.RemoveMissingAsync(present.Id));
        Assert.Equal(0, await Cleanup.RemoveMissingAsync(int.MaxValue));
        Assert.Equal(1, await Cleanup.RemoveMissingAsync(first.Id));
        Assert.NotNull(await Library.GetByIdAsync(second.Id));
        Assert.NotNull(await Library.GetByIdAsync(offline.Id));
        Assert.Equal(2, await Cleanup.RemoveMissingAsync());
        Assert.Equal(present.Id, Assert.Single(await Library.GetAllAsync()).Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(present.FullPath));
        Assert.Equal(0, await Cleanup.RemoveMissingAsync());
    }

    [Fact]
    public async Task Manual_cleanup_rechecks_files_even_when_availability_flag_is_stale()
    {
        var video = await AddVideoAsync("stale.mp4", available: true);
        Assert.Equal(1, await Cleanup.RemoveMissingAsync(video.Id));
        Assert.Empty(await Library.GetAllAsync());
    }

    private async Task<VideoAsset> AddVideoAsync(string relativePath, bool available)
    {
        var video = new VideoAsset
        {
            FileName = Path.GetFileName(relativePath),
            FullPath = Path.Combine(MediaDirectory, relativePath),
            IsAvailable = available,
            ProbeStatus = available ? MediaProbeStatus.Unknown : MediaProbeStatus.Missing
        };
        await using var db = await Factory.CreateDbContextAsync();
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        return video;
    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
