using System.Diagnostics;
using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;

namespace BeamerPresenter.Updater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length is 4 or 5 && args[0] == "--guard" && int.TryParse(args[3], out var setupId))
            {
                var executable = Path.GetFullPath(args[1]);
                using var guard = new Mutex(false, WindowsUpdateProcesses.UpdateMutexName(executable), out var created);
                try
                {
                    if (!created && (args.Length != 5 || !int.TryParse(args[4], out var helperId) ||
                        !WindowsUpdateProcesses.IsUpdateHelper(helperId))) throw new IOException("An update is already running.");
                    using var setup = Process.GetProcessById(setupId);
                    await WindowsUpdateProcesses.StopTargetAsync(executable, WindowsUpdateProcesses.CurrentSid);
                    await UpdateJson.WriteAsync(Path.GetFullPath(args[2]), 0);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    while (!setup.HasExited && !File.Exists(Path.GetFullPath(args[2]) + ".release"))
                        await Task.Delay(100, timeout.Token);
                    guard.Dispose();
                    await UpdateJson.WriteAsync(Path.GetFullPath(args[2]) + ".done", true);
                    return 0;
                }
                catch { await UpdateJson.WriteAsync(Path.GetFullPath(args[2]), 1); return 1; }
            }
            if (args.Length == 2 && args[0] == "--stop")
            {
                await WindowsUpdateProcesses.StopTargetAsync(Path.GetFullPath(args[1]), WindowsUpdateProcesses.CurrentSid);
                return 0;
            }
            if (args.Length != 2 || args[0] != "--apply") return 2;
            return await ApplyAsync(Path.GetFullPath(args[1]));
        }
        catch { return 1; }
    }

    private static async Task<int> ApplyAsync(string requestPath)
    {
        var request = await UpdateJson.ReadAsync<UpdateInstallRequest>(requestPath) ?? throw new IOException("Missing update request.");
        var jobDirectory = Path.GetDirectoryName(requestPath)!;
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamerPresenter", "Presenter");
        if (Path.GetFullPath(request.StateDirectory) != expectedRoot || request.UserSid != WindowsUpdateProcesses.CurrentSid ||
            !jobDirectory.StartsWith(Path.Combine(expectedRoot, "Updates") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid update request.");
        var target = Path.Combine(request.TargetDirectory, PortableUpdateFiles.ApplicationName);
        using var updateMutex = new Mutex(false, WindowsUpdateProcesses.UpdateMutexName(target), out var created);
        if (!created) throw new IOException("An update is already running.");
        var store = new FileUpdateStateStore(request.StateDirectory);
        try
        {
            if (StableReleaseVersion.Parse(request.Update.Version) is null ||
                !await UpdateJson.VerifyAsync(request.Update.PackagePath, request.Update.Size, request.Update.Sha256)) throw new IOException("Invalid update package.");
            var staged = Path.Combine(jobDirectory, "staged");
            if (!request.Update.Installed) await PortableUpdateFiles.ExtractAsync(request.Update.PackagePath, staged, request.Update.Version);
            WindowsUpdateProcesses.CheckWritable(request.TargetDirectory, request.Update.Size * 5);
            using (var parent = Process.GetProcessById(request.ProcessId))
                if (!WindowsUpdateProcesses.Matches(parent, target, request.UserSid, request.ProcessStartTicks)) throw new IOException("Application identity changed.");
            await UpdateJson.WriteAsync(Path.Combine(jobDirectory, "ready.json"), true);
            // The app commits the resume checkpoint only after successful helper preflight.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (!File.Exists(Path.Combine(jobDirectory, "go.json"))) await Task.Delay(100, timeout.Token);
            request = await UpdateJson.ReadAsync<UpdateInstallRequest>(requestPath) ?? throw new IOException("Resume checkpoint is missing.");
            await WindowsUpdateProcesses.StopTargetAsync(target, request.UserSid);
            if (request.Update.Installed)
            {
                var info = new ProcessStartInfo(request.Update.PackagePath) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("/VERYSILENT"); info.ArgumentList.Add("/SUPPRESSMSGBOXES");
                info.ArgumentList.Add("/NORESTART"); info.ArgumentList.Add("/NORESTARTAPPLICATIONS"); info.ArgumentList.Add("/SP-");
                info.ArgumentList.Add("/DIR=" + request.TargetDirectory);
                info.ArgumentList.Add("/UpdateHelper=" + Environment.ProcessId);
                using var setup = Process.Start(info) ?? throw new IOException("Setup could not start.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                try { await setup.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    if (!setup.HasExited) setup.Kill(entireProcessTree: true);
                    throw new IOException("Setup timed out.");
                }
                if (setup.ExitCode != 0) throw new IOException("Setup failed.");
            }
            else await PortableUpdateFiles.ApplyAsync(staged, request.TargetDirectory, Path.Combine(jobDirectory, "backup"), request.Update.Version);
            if (FileVersionInfo.GetVersionInfo(target).FileVersion is not { } version || !Version.TryParse(version, out var actual) ||
                actual.ToString(3) != request.Update.Version) throw new IOException("Installed version does not match.");
            await store.WriteAsync(new(), CancellationToken.None);
            // Release the installation guard before restarting the app.
            updateMutex.Dispose();
            var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = request.TargetDirectory };
            start.ArgumentList.Add("--resume-update"); start.ArgumentList.Add(Path.GetFileName(jobDirectory));
            using var restarted = Process.Start(start) ?? throw new IOException("Application restart failed.");
            return 0;
        }
        catch
        {
            await store.WriteAsync(new(request.Update, null, request.Update.Version, "Das Update konnte nicht installiert werden. Bitte das Setup erneut ausführen."), CancellationToken.None);
            await UpdateJson.WriteAsync(Path.Combine(jobDirectory, "failed.json"), true);
            return 1;
        }
    }
}
