using System.Globalization;
using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;
using BeamerPresenter.Updater;
using BeamerPresenter.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BeamerPresenter.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (WindowsFirewallService.IsHelperRequest(args))
        {
            Environment.ExitCode = WindowsFirewallService.RunHelperAsync(args,
                Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath,
                new FirewallProcessRunner()).GetAwaiter().GetResult();
            return;
        }

        var startMinimized = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        if (WindowsUpdateProcesses.IsUpdating(Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath)) return;
        using var singleInstance = SingleInstanceCoordinator.Acquire();
        if (!singleInstance.IsPrimary)
        {
            if (!startMinimized)
            {
                singleInstance.SignalPrimaryAsync().GetAwaiter().GetResult();
            }

            return;
        }

        var paths = PresenterPaths.CreateDefault();
        Log.Logger = PresenterLogging.CreateLogger(paths.LogsDirectory);
        try
        {
            var build = BuildInformation.Current;
            Log.Information("Starting presenter {Version} ({GitCommitSha})", build.Version, build.ShortGitCommitSha);
            var hostSettings = PresenterDatabase.GetConfiguredHostSettings(paths.DataDirectory);
            PresenterLanguage.Apply(hostSettings.LanguagePreference, CultureInfo.CurrentUICulture);
            ApplicationConfiguration.Initialize();
            var presenterHost = BuildPresenterHost(paths, hostSettings);
            presenterHost.StartAsync().GetAwaiter().GetResult();
            var resumeIndex = Array.IndexOf(args, "--resume-update");
            UpdateInstallRequest? updateRequest = null;
            if (resumeIndex >= 0 && args.Length > resumeIndex + 1 && Guid.TryParseExact(args[resumeIndex + 1], "N", out _))
            {
                var job = Path.Combine(paths.RootDirectory, "Updates", args[resumeIndex + 1]);
                updateRequest = UpdateJson.ReadAsync<UpdateInstallRequest>(Path.Combine(job, "request.json")).GetAwaiter().GetResult();
                // Consume once; a subsequent ordinary start must not replay a stale checkpoint.
                if (updateRequest is not null) File.Move(Path.Combine(job, "request.json"), Path.Combine(job, "consumed.json"), overwrite: true);
            }
            if (updateRequest is not null)
            {
                try
                {
                    ValidateResumeMediaAsync(presenterHost.Services, updateRequest.Resume).GetAwaiter().GetResult();
                    presenterHost.Services.GetRequiredService<PlaybackOrchestrator>().RestoreAfterUpdateAsync(updateRequest.Resume).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Playback could not be restored after update");
                    presenterHost.Services.GetRequiredService<PlaybackOrchestrator>().StopAsync().GetAwaiter().GetResult();
                    var store = new FileUpdateStateStore(paths.RootDirectory);
                    store.WriteAsync(new(Error: "Die Wiedergabe konnte nach dem Update nicht wiederhergestellt werden. Bitte Medium und Monitor prüfen."), CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            using var presenterForm = new PresenterForm(presenterHost, updateRequest is null ? startMinimized : !updateRequest.ShowStatusWindow);
            var lifetime = presenterHost.Services.GetRequiredService<ApplicationLifetime>();
            lifetime.Bind(presenterHost, () =>
            {
                if (!presenterForm.IsDisposed && presenterForm.IsHandleCreated) presenterForm.BeginInvoke(presenterForm.CloseAfterShutdown);
            });
            singleInstance.StartListening(() =>
            {
                if (!presenterForm.IsDisposed && presenterForm.IsHandleCreated)
                {
                    presenterForm.BeginInvoke(presenterForm.ShowFromExternalLaunch);
                }
            }, lifetime.RequestShutdown);
            System.Windows.Forms.Application.Run(presenterForm);
            lifetime.RequestShutdown();
            lifetime.WaitAsync().GetAwaiter().GetResult();
            Log.Information("Presenter stopped normally");
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Presenter terminated unexpectedly");
            throw new InvalidOperationException(AppText.Get("Der Presenter wurde unerwartet beendet."), exception);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static async Task ValidateResumeMediaAsync(IServiceProvider services, PlaybackResume resume)
    {
        if (!resume.MediaRequested || resume.State == BeamerPresenter.Domain.PresenterState.Stopped) return;
        var entry = (await services.GetRequiredService<PlaybackQueueService>().GetQueueAsync())
            .SingleOrDefault(item => item.Id == resume.QueueEntryId);
        if (entry?.MediaId is not int mediaId) return;
        var asset = await services.GetRequiredService<IMediaLibraryService>().GetByIdAsync(mediaId);
        if (asset is null || !File.Exists(asset.FullPath))
            throw new InvalidOperationException("Das gespeicherte Medium fehlt oder ist nicht erreichbar.");
        var path = Path.GetFullPath(asset.FullPath);
        var folders = await services.GetRequiredService<IMediaFolderService>().GetAllAsync();
        if (!folders.Any(folder => folder.Enabled && path.StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Das gespeicherte Medium liegt nicht mehr in einem aktivierten Medienordner.");
    }

    private static WebApplication BuildPresenterHost(PresenterPaths paths, PresenterHostSettings hostSettings)
    {
        var webPort = hostSettings.WebPort;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.Host.UseSerilog(Log.Logger, dispose: false);
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseUrls($"http://{(hostSettings.AllowLanAccess ? "0.0.0.0" : "127.0.0.1")}:{webPort}");
        builder.Services.AddPresenterInfrastructure(paths.DataDirectory, paths.ToolsDirectory);
        builder.Services.AddPresenterWebUi();
        builder.Services.AddSingleton<IMonitorService, WindowsMonitorService>();
        builder.Services.AddSingleton<IChromeProcessLauncher, ChromeProcessLauncher>();
        builder.Services.AddSingleton<IChromeWindowController, ChromeWindowController>();
        builder.Services.AddSingleton<IBrowserController>(provider => new ChromeBrowserController(
            provider.GetRequiredService<IPresenterSettingsService>(),
            provider.GetRequiredService<IMonitorService>(),
            provider.GetRequiredService<IChromeProcessLauncher>(),
            provider.GetRequiredService<IChromeWindowController>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ChromeBrowserController>>(),
            paths.ChromeProfileDirectory,
            $"http://127.0.0.1:{webPort}/presenter"));
        builder.Services.AddSingleton<IPowerManagementService, WindowsPowerManagementService>();
        builder.Services.AddSingleton<PlaybackOrchestrator>();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<ApplicationLifetime>();
        builder.Services.AddSingleton<IUpdateStateStore>(new FileUpdateStateStore(paths.RootDirectory));
        builder.Services.AddSingleton<IUpdateInstaller, WindowsApplicationUpdateInstaller>();
        builder.Services.AddSingleton<IApplicationReleaseSource>(_ => new GitHubApplicationReleaseSource(
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) },
            Path.Combine(paths.RootDirectory, "Updates", "Downloads"), () => WindowsUpdateProcesses.IsInstalled(AppContext.BaseDirectory)));
        builder.Services.AddSingleton<ApplicationUpdateCoordinator>();
        builder.Services.AddSingleton<IApplicationUpdateService>(provider => provider.GetRequiredService<ApplicationUpdateCoordinator>());
        builder.Services.AddHostedService<ApplicationUpdateWorker>();
        builder.Services.AddSingleton<IPlaybackCommandService>(provider => provider.GetRequiredService<PlaybackOrchestrator>());
        builder.Services.AddSingleton<IPresenterControlService>(provider => provider.GetRequiredService<PlaybackOrchestrator>());
        builder.Services.AddSingleton<INewsCommandService>(provider => provider.GetRequiredService<PlaybackOrchestrator>());
        builder.Services.AddSingleton<INewsDisplayState>(provider => provider.GetRequiredService<PlaybackOrchestrator>());
        builder.Services.AddSingleton<IPresenterRecoveryService>(provider => provider.GetRequiredService<PlaybackOrchestrator>());
        builder.Services.AddHostedService<NewsSchedulingWorker>();
        builder.Services.AddHostedService<PresenterWatchdog>();
        builder.Services.AddSingleton(new StartupRegistrationService(Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath));
        builder.Services.AddSingleton(hostSettings);
        builder.Services.AddSingleton<IWindowsFirewallService>(new WindowsFirewallService(
            Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath, new FirewallProcessRunner()));
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = 5L * 1024 * 1024 * 1024);
        var application = builder.Build();
        application.UseStaticFiles();
        application.UsePresenterLoopbackProtection();
        application.UseAuthentication();
        application.UseAuthorization();
        application.UseAntiforgery();
        application.UseSerilogRequestLogging();
        application.MapPresenterWebUi();
        return application;
    }
}
