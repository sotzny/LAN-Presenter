using BeamerPresenter.App;

namespace BeamerPresenter.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task Shutdown_pipe_accepts_only_the_addressed_primary_process()
    {
        var applicationId = "BeamerPresenter.Tests." + Guid.NewGuid().ToString("N");
        using var primary = SingleInstanceCoordinator.Acquire(applicationId);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(() => { }, () => stopped.TrySetResult());
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16];
        var pipeName = applicationId + "." + hash;
        async Task<string?> SendAsync(int pid)
        {
            await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"shutdown:{pid}\n"), timeout.Token);
            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            return await reader.ReadLineAsync(timeout.Token);
        }
        Assert.Null(await SendAsync(0));
        Assert.False(stopped.Task.IsCompleted);
        Assert.Equal("accepted", await SendAsync(Environment.ProcessId));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Startup_command_quotes_the_executable_path()
    {
        var registration = new StartupRegistrationService(@"C:\Program Files\Beamer Presenter\BeamerPresenter.App.exe");

        Assert.Equal("\"C:\\Program Files\\Beamer Presenter\\BeamerPresenter.App.exe\" --autostart", registration.StartupCommand);
    }

    [Fact]
    public async Task Second_instance_signals_the_primary_instance()
    {
        var applicationId = $"BeamerPresenter.Tests.{Guid.NewGuid():N}";
        using var primary = SingleInstanceCoordinator.Acquire(applicationId);
        using var secondary = SingleInstanceCoordinator.Acquire(applicationId);
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        primary.StartListening(() => activation.TrySetResult());

        Assert.True(await secondary.SignalPrimaryAsync());
        await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
