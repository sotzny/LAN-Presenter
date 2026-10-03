using BeamerPresenter.App;
using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;
using BeamerPresenter.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.App.Tests;

// A real, isolated Windows/Kestrel process for lifetime tests; never uses personal app data.
internal static class ShutdownProcessHost
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3) return 2;
        var directory = Path.GetFullPath(args[0]);
        using var instance = SingleInstanceCoordinator.Acquire(args[2]);
        if (!instance.IsPrimary)
        {
            instance.SignalPrimaryAsync().GetAwaiter().GetResult();
            return 0;
        }
        PresenterDatabase.GetConfiguredWebPort(Path.Combine(directory, "Data"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddPresenterInfrastructure(Path.Combine(directory, "Data"));
        builder.Services.AddPresenterWebUi();
        builder.Services.AddSingleton<IBrowserController>(_ => new AsyncBrowser(directory, args[1] == "hang"));
        builder.Services.AddSingleton<IPowerManagementService, NoPower>();
        builder.Services.AddSingleton<PlaybackOrchestrator>();
        builder.Services.AddSingleton<ApplicationLifetime>();
        var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<ApplicationLifetime>();
        host.MapGet("/quit", () => { lifetime.RequestShutdown(); return "stopping"; });
        host.StartAsync().GetAwaiter().GetResult();
        using var form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        lifetime.Bind(host, () => form.BeginInvoke(form.Close));
        instance.StartListening(() => File.WriteAllText(Path.Combine(directory, "activated.txt"), "activated"), lifetime.RequestShutdown);
        form.Shown += (_, _) =>
        {
            form.Hide();
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            File.WriteAllText(Path.Combine(directory, "ready.txt"), address);
        };
        System.Windows.Forms.Application.Run(form);
        lifetime.WaitAsync().GetAwaiter().GetResult();
        return 0;
    }

    private sealed class AsyncBrowser(string directory, bool hang) : IBrowserController, IAsyncDisposable
    {
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (hang) await new TaskCompletionSource().Task;
            else await Task.Delay(25, cancellationToken);
        }
        public Task ShowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HideAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsTopmostAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public async ValueTask DisposeAsync() { await Task.Delay(25); await File.WriteAllTextAsync(Path.Combine(directory, "disposed.txt"), "disposed"); }
    }
    private sealed class NoPower : IPowerManagementService
    {
        public Task ApplyAsync(bool preventDisplaySleep, bool preventSystemSleep, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReleaseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
