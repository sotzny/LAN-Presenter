using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public enum UpdatePhase { Idle, Checking, Downloading, Ready, Installing, Failed }
public sealed record UpdateAsset(string Name, string Url, long Size, string Sha256);
public sealed record ApplicationRelease(string Version, UpdateAsset Installer, UpdateAsset Portable);
public sealed record PreparedUpdate(string Version, string PackagePath, bool Installed, string Sha256, long Size);
public sealed record ApplicationUpdateSnapshot(string InstalledVersion, string? AvailableVersion, string Phase,
    int Progress, DateTimeOffset? InstallAtUtc, bool AutomaticUpdatesEnabled, string? Error);
public sealed record UpdatePersistentState(PreparedUpdate? Prepared = null, DateTimeOffset? InstallAtUtc = null,
    string? FailedVersion = null, string? Error = null);

public interface IApplicationUpdateService
{
    ApplicationUpdateSnapshot Current { get; }
    Task CheckAsync(CancellationToken cancellationToken = default);
    Task InstallAsync(CancellationToken cancellationToken = default);
    Task PostponeAsync(CancellationToken cancellationToken = default);
    Task SetAutomaticAsync(bool enabled, CancellationToken cancellationToken = default);
}
public interface IApplicationReleaseSource
{
    Task<ApplicationRelease?> GetLatestAsync(CancellationToken cancellationToken);
    Task<PreparedUpdate> PrepareAsync(ApplicationRelease release, Action<int> progress, CancellationToken cancellationToken);
    Task<bool> IsPreparedAsync(PreparedUpdate prepared, CancellationToken cancellationToken);
}
public interface IUpdateStateStore
{
    Task<UpdatePersistentState> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(UpdatePersistentState state, CancellationToken cancellationToken);
}
public interface IUpdateInstaller
{
    string InstalledVersion { get; }
    Task LaunchAsync(PreparedUpdate update, CancellationToken cancellationToken);
}

public static class StableReleaseVersion
{
    public static Version? Parse(string? value)
    {
        var text = value?.StartsWith('v') == true ? value[1..] : value;
        if (string.IsNullOrEmpty(text) || text.Split('.').Length != 3 ||
            text.Any(character => !char.IsAsciiDigit(character) && character != '.') ||
            !Version.TryParse(text, out var version) || version.Major < 1) return null;
        return version;
    }
}

public sealed class ApplicationUpdateCoordinator(
    IPresenterSettingsService settings, IApplicationReleaseSource source, IUpdateStateStore store,
    IUpdateInstaller installer, TimeProvider clock) : IApplicationUpdateService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdatePersistentState state = new();
    private bool initialized;
    private volatile ApplicationUpdateSnapshot snapshot = new(installer.InstalledVersion, null, "Idle", 0, null, true, null);
    public ApplicationUpdateSnapshot Current => snapshot;

    private async Task InitializeAsync(CancellationToken token)
    {
        if (initialized) return;
        state = await store.ReadAsync(token);
        var automatic = (await settings.GetAsync(token)).AutomaticUpdatesEnabled;
        // A restart must retain an existing postponement/countdown, including a failed attempt.
        snapshot = snapshot with
        {
            AvailableVersion = state.Prepared?.Version,
            InstallAtUtc = automatic ? state.InstallAtUtc : null,
            AutomaticUpdatesEnabled = automatic,
            Error = state.Error,
            Phase = state.Error is null ? "Idle" : "Failed"
        };
        initialized = true;
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await InitializeAsync(cancellationToken); await CheckCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task CheckCoreAsync(CancellationToken token)
    {
        if (snapshot.Phase == "Installing") return;
        snapshot = snapshot with { Phase = "Checking" };
        try
        {
            var release = await source.GetLatestAsync(token);
            if (release is null || StableReleaseVersion.Parse(release.Version) is not { } latest ||
                !Version.TryParse(installer.InstalledVersion, out var current) || latest <= current)
            {
                state = new(Error: state.FailedVersion is null ? state.Error : null);
                snapshot = snapshot with { Phase = state.Error is null ? "Idle" : "Failed", AvailableVersion = null, InstallAtUtc = null, Progress = 0, Error = state.Error };
                await store.WriteAsync(state, token);
                return;
            }
            snapshot = snapshot with { AvailableVersion = release.Version };
            if (state.FailedVersion == release.Version)
            {
                snapshot = snapshot with { Phase = "Failed", InstallAtUtc = null, Error = state.Error };
                return;
            }
            var asset = state.Prepared?.Installed == true ? release.Installer : release.Portable;
            if (state.Prepared?.Version != release.Version || state.Prepared.Size != asset.Size ||
                !string.Equals(state.Prepared.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !await source.IsPreparedAsync(state.Prepared, token))
            {
                snapshot = snapshot with { Phase = "Downloading", Progress = 0, InstallAtUtc = null };
                var prepared = await source.PrepareAsync(release, percent => snapshot = snapshot with { Progress = percent }, token);
                state = new(prepared, snapshot.AutomaticUpdatesEnabled ? clock.GetUtcNow().AddMinutes(5) : null);
            }
            else if (snapshot.AutomaticUpdatesEnabled && state.InstallAtUtc is null)
                state = state with { InstallAtUtc = clock.GetUtcNow().AddMinutes(5) };
            state = state with { Error = null };
            snapshot = snapshot with { Phase = "Ready", Progress = 100, InstallAtUtc = state.InstallAtUtc, Error = null };
            await store.WriteAsync(state, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or UnauthorizedAccessException)
        {
            state = state with { InstallAtUtc = null, Error = "Das Update konnte nicht geladen oder geprüft werden." };
            snapshot = snapshot with { Phase = "Failed", InstallAtUtc = null, Error = state.Error };
            await store.WriteAsync(state, token);
        }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            if (snapshot.Phase == "Installing") return;
            if (state.Prepared is null || snapshot.Phase != "Ready")
            {
                state = state with { FailedVersion = null };
                await CheckCoreAsync(cancellationToken);
            }
            await InstallCoreAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task InstallCoreAsync(CancellationToken token)
    {
        if (state.Prepared is null || snapshot.Phase != "Ready") return;
        try
        {
            if (!await source.IsPreparedAsync(state.Prepared, token)) throw new IOException("Package verification failed.");
            snapshot = snapshot with { Phase = "Installing", InstallAtUtc = null };
            // Persist before launching: a crashed helper cannot cause an installation loop.
            state = state with { InstallAtUtc = null, FailedVersion = state.Prepared.Version, Error = "Das Update wurde nicht erfolgreich abgeschlossen." };
            await store.WriteAsync(state, token);
            await installer.LaunchAsync(state.Prepared, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            state = state with { InstallAtUtc = null, FailedVersion = state.Prepared?.Version, Error = "Das Update konnte nicht installiert werden." };
            snapshot = snapshot with { Phase = "Failed", InstallAtUtc = null, Error = state.Error };
            await store.WriteAsync(state, token);
        }
    }

    public async Task TickAsync(CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token)) return;
        try
        {
            await InitializeAsync(token);
            if (snapshot.AutomaticUpdatesEnabled && snapshot.Phase == "Ready" && state.InstallAtUtc <= clock.GetUtcNow())
                await InstallCoreAsync(token);
        }
        finally { gate.Release(); }
    }

    public async Task PostponeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            if (snapshot.Phase != "Ready" || !snapshot.AutomaticUpdatesEnabled) return;
            state = state with { InstallAtUtc = clock.GetUtcNow().AddHours(1) };
            snapshot = snapshot with { InstallAtUtc = state.InstallAtUtc };
            await store.WriteAsync(state, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task SetAutomaticAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            if (snapshot.Phase == "Installing") return;
            var configuration = await settings.GetAsync(cancellationToken);
            configuration.AutomaticUpdatesEnabled = enabled;
            await settings.SaveAsync(configuration, cancellationToken);
            state = state with { InstallAtUtc = enabled && snapshot.Phase == "Ready" ? clock.GetUtcNow().AddMinutes(5) : null };
            snapshot = snapshot with { AutomaticUpdatesEnabled = enabled, InstallAtUtc = state.InstallAtUtc };
            await store.WriteAsync(state, cancellationToken);
        }
        finally { gate.Release(); }
    }
}

public sealed record NewsResume(NewsItem Item, DateTimeOffset? ExpiresUtc);
public sealed record PlaybackResume(PresenterState State, long? QueueEntryId, TimeSpan? Position,
    bool MediaRequested, NewsResume? Main, NewsResume? Suspended, NewsResume? Ticker);
public sealed record UpdateInstallRequest(PreparedUpdate Update, string TargetDirectory, string StateDirectory,
    int ProcessId, long ProcessStartTicks, string UserSid, PlaybackResume Resume, bool ShowStatusWindow);
