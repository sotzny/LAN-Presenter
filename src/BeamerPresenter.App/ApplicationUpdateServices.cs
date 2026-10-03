using System.Diagnostics;
using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;
using BeamerPresenter.Updater;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.App;

internal sealed class WindowsApplicationUpdateInstaller(PresenterPaths paths, PlaybackOrchestrator playback,
    IPresenterTelemetry telemetry, ApplicationLifetime lifetime) : IUpdateInstaller
{
    public string InstalledVersion => BuildInformation.Current.Version;

    public async Task LaunchAsync(PreparedUpdate update, CancellationToken cancellationToken)
    {
        var target = AppContext.BaseDirectory;
        WindowsUpdateProcesses.CheckWritable(target, update.Size * 5);
        var job = Path.Combine(paths.RootDirectory, "Updates", Guid.NewGuid().ToString("N"));
        var runner = Path.Combine(job, "runner");
        Directory.CreateDirectory(runner);
        WindowsUpdateProcesses.CheckWritable(job, update.Size * 5);
        // The helper runs entirely outside the installation; framework-dependent development
        // outputs need the adjacent runtime files as well as the executable.
        foreach (var file in Directory.EnumerateFiles(target))
            File.Copy(file, Path.Combine(runner, Path.GetFileName(file)));
        var helperPath = Path.Combine(runner, PortableUpdateFiles.UpdaterName);
        if (!File.Exists(helperPath)) throw new IOException("Der Update-Hilfsprozess fehlt.");
        using var current = Process.GetCurrentProcess();
        var requestPath = Path.Combine(job, "request.json");
        var request = new UpdateInstallRequest(update, target, paths.RootDirectory, current.Id,
            current.StartTime.ToUniversalTime().Ticks, WindowsUpdateProcesses.CurrentSid,
            new(BeamerPresenter.Domain.PresenterState.Stopped, null, null, false, null, null, null),
            lifetime.ShowStatusWindow);
        await UpdateJson.WriteAsync(requestPath, request, cancellationToken);
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply"); start.ArgumentList.Add(requestPath);
        using var helper = Process.Start(start) ?? throw new IOException("Der Update-Hilfsprozess konnte nicht gestartet werden.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        while (!File.Exists(Path.Combine(job, "ready.json")))
        {
            if (helper.HasExited) throw new IOException("Die Vorbereitung des Updates ist fehlgeschlagen.");
            try { await Task.Delay(100, timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                helper.Kill(entireProcessTree: true);
                throw new IOException("Die Vorbereitung des Updates hat zu lange gedauert.");
            }
        }
        try
        {
            var resume = await playback.CaptureResumeAsync(telemetry, cancellationToken);
            await UpdateJson.WriteAsync(requestPath, request with { Resume = resume }, cancellationToken);
            await UpdateJson.WriteAsync(Path.Combine(job, "go.json"), true, cancellationToken);
            lifetime.RequestShutdown();
        }
        catch
        {
            if (!helper.HasExited) helper.Kill(entireProcessTree: true);
            await playback.CancelUpdatePreparationAsync();
            throw;
        }
    }
}

internal sealed class ApplicationUpdateWorker(ApplicationUpdateCoordinator updates, TimeProvider clock, ApplicationLifetime lifetime,
    ILogger<ApplicationUpdateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await lifetime.Ready.Task.WaitAsync(stoppingToken);
        var nextCheck = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        do
        {
            try
            {
                if (clock.GetUtcNow() >= nextCheck)
                {
                    nextCheck = clock.GetUtcNow().AddHours(6);
                    await updates.CheckAsync(stoppingToken);
                }
                await updates.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Update check failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
