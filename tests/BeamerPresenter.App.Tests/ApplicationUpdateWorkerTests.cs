using System.Threading.Channels;
using BeamerPresenter.App;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeamerPresenter.App.Tests;

public sealed class ApplicationUpdateWorkerTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Every_start_checks_immediately_after_readiness_and_again_after_six_hours(bool showStatusWindow, bool automatic)
    {
        var clock = new ManualClock();
        var source = new RecordingSource(clock);
        var store = new MemoryState();
        for (var start = 0; start < 2; start++)
        {
            // The worker only reads Ready; this test never requests application shutdown.
            var lifetime = new ApplicationLifetime(null!) { ShowStatusWindow = showStatusWindow };
            var updates = new ApplicationUpdateCoordinator(new Settings(automatic), source, store, new Installer(), clock);
            using var worker = new ApplicationUpdateWorker(updates, clock, lifetime, NullLogger<ApplicationUpdateWorker>.Instance);
            var startedAt = clock.GetUtcNow();
            var previousChecks = source.CheckCount;
            await worker.StartAsync(CancellationToken.None);
            Assert.Equal(previousChecks, source.CheckCount);
            lifetime.Ready.TrySetResult();
            Assert.Equal(startedAt, await source.Checks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(previousChecks + 1, source.CheckCount);

            await clock.AdvanceAsync(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
            Assert.Equal(previousChecks + 1, source.CheckCount);
            await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(startedAt.AddHours(6), await source.Checks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(previousChecks + 2, source.CheckCount);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(deadline.Token);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
        private TaskCompletionSource? observed;
        private ManualTimer? timer;
        public override DateTimeOffset GetUtcNow()
        {
            var value = now;
            observed?.TrySetResult();
            return value;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new ManualTimer(callback, state);
            return timer;
        }
        public async Task AdvanceAsync(TimeSpan duration)
        {
            now += duration;
            observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timer!.Tick();
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Tick() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSource(ManualClock clock) : IApplicationReleaseSource
    {
        private int checks;
        public int CheckCount => Volatile.Read(ref checks);
        public Channel<DateTimeOffset> Checks { get; } = Channel.CreateUnbounded<DateTimeOffset>();
        public Task<ApplicationRelease?> GetLatestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref checks);
            Checks.Writer.TryWrite(clock.GetUtcNow());
            return Task.FromResult<ApplicationRelease?>(null);
        }
        public Task<PreparedUpdate> PrepareAsync(ApplicationRelease release, Action<int> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsPreparedAsync(PreparedUpdate prepared, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryState : IUpdateStateStore
    {
        private UpdatePersistentState state = new();
        public Task<UpdatePersistentState> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(state);
        public Task WriteAsync(UpdatePersistentState value, CancellationToken cancellationToken) { state = value; return Task.CompletedTask; }
    }

    private sealed class Installer : IUpdateInstaller
    {
        public string InstalledVersion => "1.2.1";
        public Task LaunchAsync(PreparedUpdate update, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Settings(bool automatic) : IPresenterSettingsService
    {
        public Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PresenterSettings { AutomaticUpdatesEnabled = automatic });
        public Task SaveAsync(PresenterSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
