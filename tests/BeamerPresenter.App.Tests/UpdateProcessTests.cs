using System.Diagnostics;
using System.Net.Sockets;
using BeamerPresenter.Updater;

namespace BeamerPresenter.App.Tests;

[Collection("Windows process tests")]
public sealed class UpdateProcessTests
{
    [Fact]
    public async Task Graceful_shutdown_disposes_async_services_releases_port_and_allows_a_new_primary()
    {
        await using var host = await Host.StartAsync("normal");
        using var client = new HttpClient();
        await client.GetAsync(host.Address + "/quit");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, host.Process.ExitCode);
        Assert.True(File.Exists(Path.Combine(host.Directory, "disposed.txt")));
        await WaitForPortReuseAsync(host.Address);
        using var primary = BeamerPresenter.App.SingleInstanceCoordinator.Acquire(host.InstanceId);
        Assert.True(primary.IsPrimary);
    }

    [Fact]
    public async Task Force_stop_targets_only_the_matching_path_and_user_and_frees_its_port()
    {
        await using var host = await Host.StartAsync("hang");
        await using var other = await Host.StartAsync("normal");
        Assert.True(WindowsUpdateProcesses.Matches(host.Process, host.Executable, WindowsUpdateProcesses.CurrentSid));
        Assert.False(WindowsUpdateProcesses.Matches(host.Process, other.Executable, WindowsUpdateProcesses.CurrentSid));
        Assert.False(WindowsUpdateProcesses.Matches(host.Process, host.Executable, "S-1-0-0"));
        Assert.False(WindowsUpdateProcesses.Matches(host.Process, host.Executable, WindowsUpdateProcesses.CurrentSid, 0));
        Assert.Throws<SocketException>(() => AssertPortCanBeReusedByKestrel(host.Address));
        await WindowsUpdateProcesses.StopTargetAsync(host.Executable, WindowsUpdateProcesses.CurrentSid, gracefulTimeout: TimeSpan.FromMilliseconds(100));
        Assert.True(host.Process.HasExited); Assert.False(other.Process.HasExited);
        await WaitForPortReuseAsync(host.Address);
        using var primary = BeamerPresenter.App.SingleInstanceCoordinator.Acquire(host.InstanceId);
        Assert.True(primary.IsPrimary);
    }

    private static void AssertPortCanBeReusedByKestrel(string address)
    {
        // Use the same socket options as the production server. TcpListener's exclusive
        // binding also rejects Windows TIME_WAIT sockets after the listener has gone.
        using var socket = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions.CreateDefaultBoundListenSocket(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, new Uri(address).Port));
        socket.Listen(1);
    }

    private static async Task WaitForPortReuseAsync(string address)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try { AssertPortCanBeReusedByKestrel(address); return; }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                if (timeout.IsCancellationRequested)
                {
                    var port = new Uri(address).Port;
                    var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                    var listeners = properties.GetActiveTcpListeners().Count(endpoint => endpoint.Port == port);
                    var connections = properties.GetActiveTcpConnections().Where(connection => connection.LocalEndPoint.Port == port)
                        .Select(connection => connection.State.ToString());
                    throw new IOException($"Kestrel port was not released within five seconds: listeners={listeners}; connections={string.Join(',', connections)}", exception);
                }
                await Task.Delay(50);
            }
        }
    }

    [Fact]
    public void Upgrade_guard_and_write_preflight_are_scoped_to_the_installation()
    {
        var root = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Preflight", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "BeamerPresenter.App.exe");
            Assert.False(WindowsUpdateProcesses.IsUpdating(target));
            using (var mutex = new Mutex(false, WindowsUpdateProcesses.UpdateMutexName(target))) Assert.True(WindowsUpdateProcesses.IsUpdating(target));
            Assert.False(WindowsUpdateProcesses.IsUpdating(target));
            WindowsUpdateProcesses.CheckWritable(root, 1);
            Assert.Empty(Directory.GetFiles(root));
            Assert.Throws<IOException>(() => WindowsUpdateProcesses.CheckWritable(root, long.MaxValue));
            Assert.Throws<IOException>(() => WindowsUpdateProcesses.CheckWritable(Path.Combine(root, "missing"), 1));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Host : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "BeamerPresenter.ProcessTests", Guid.NewGuid().ToString("N"));
        public string InstanceId { get; } = "BeamerPresenter.ProcessTests." + Guid.NewGuid().ToString("N");
        public string Executable => Path.Combine(Directory, "BeamerPresenter.App.exe");
        public Process Process { get; private set; } = null!;
        public string Address { get; private set; } = null!;
        public static async Task<Host> StartAsync(string mode)
        {
            var host = new Host(); System.IO.Directory.CreateDirectory(host.Directory);
            foreach (var file in System.IO.Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(host.Directory, Path.GetFileName(file)));
            var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
            if (System.IO.Directory.Exists(runtimes))
                foreach (var file in System.IO.Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(host.Directory, Path.GetRelativePath(AppContext.BaseDirectory, file));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            File.Copy(Path.Combine(AppContext.BaseDirectory, "BeamerPresenter.App.Tests.exe"), host.Executable, overwrite: true);
            var info = new ProcessStartInfo(host.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            info.ArgumentList.Add(host.Directory); info.ArgumentList.Add(mode); info.ArgumentList.Add(host.InstanceId);
            host.Process = Process.Start(info)!;
            var errors = host.Process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var ready = Path.Combine(host.Directory, "ready.txt");
                while (!File.Exists(ready))
                {
                    if (host.Process.HasExited) throw new IOException("Process smoke host exited before startup: " + await errors);
                    await Task.Delay(50, timeout.Token);
                }
                host.Address = await File.ReadAllTextAsync(ready, timeout.Token); return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Process is not null)
            {
                if (!Process.HasExited) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); }
                Process.Dispose();
            }
            for (var attempt = 0; attempt < 30 && System.IO.Directory.Exists(Directory); attempt++)
            {
                try
                {
                    foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
                catch (Exception exception) when (attempt < 29 && exception is IOException or UnauthorizedAccessException) { await Task.Delay(100); }
            }
        }
    }
}
