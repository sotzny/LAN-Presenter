using BeamerPresenter.Application;

namespace BeamerPresenter.TestSupport;

internal sealed class RecordingApplicationUpdates : IApplicationUpdateService
{
    public ApplicationUpdateSnapshot Snapshot = new("1.1.1", "1.2.0", "Ready", 100, DateTimeOffset.UtcNow.AddMinutes(5), true, null);
    public ApplicationUpdateSnapshot Current => Snapshot;
    public int Checks; public int Installs; public int Postpones;
    public Task CheckAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref Checks); return Task.CompletedTask; }
    public Task InstallAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref Installs); Snapshot = Snapshot with { Phase = "Installing", InstallAtUtc = null }; return Task.CompletedTask; }
    public Task PostponeAsync(CancellationToken cancellationToken = default) { Postpones++; Snapshot = Snapshot with { InstallAtUtc = DateTimeOffset.UtcNow.AddHours(1) }; return Task.CompletedTask; }
    public Task SetAutomaticAsync(bool enabled, CancellationToken cancellationToken = default)
    { Snapshot = Snapshot with { AutomaticUpdatesEnabled = enabled, InstallAtUtc = enabled ? DateTimeOffset.UtcNow.AddMinutes(5) : null }; return Task.CompletedTask; }
}
