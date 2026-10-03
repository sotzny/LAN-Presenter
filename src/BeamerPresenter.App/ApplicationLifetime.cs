using BeamerPresenter.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BeamerPresenter.App;

internal sealed class ApplicationLifetime(PlaybackOrchestrator playback)
{
    private WebApplication? host;
    private Action? completed;
    private int requested;
    private Task? shutdown;
    public bool IsStopping => Volatile.Read(ref requested) != 0;
    public bool ShowStatusWindow { get; set; }
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Bind(WebApplication application, Action onCompleted) { host = application; completed = onCompleted; Ready.TrySetResult(); }

    public void RequestShutdown()
    {
        if (Interlocked.Exchange(ref requested, 1) != 0) return;
        playback.BeginShutdown();
        shutdown = Task.Run(async () =>
        {
            using var deadline = new System.Threading.Timer(_ =>
            {
                Log.Error("Presenter shutdown exceeded the 30 second deadline");
                Environment.Exit(1);
            }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await playback.ShutdownAsync(cancellation.Token); }
            catch (Exception exception) { Log.Warning(exception, "Presenter cleanup failed during shutdown"); }
            try
            {
                if (host is not null)
                {
                    await host.StopAsync(cancellation.Token);
                    await host.DisposeAsync();
                }
            }
            catch (Exception exception) { Log.Warning(exception, "Host cleanup failed during shutdown"); }
            completed?.Invoke();
        });
    }

    public Task WaitAsync() => shutdown ?? Task.CompletedTask;
}
