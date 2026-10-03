using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BeamerPresenter.Updater;

public static class WindowsUpdateProcesses
{
    public static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("User SID is unavailable.");
    public static string UpdateMutexName(string target) => "Local\\BeamerPresenter.Update." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(target).ToUpperInvariant() + CurrentSid)));

    public static bool IsUpdating(string target)
    {
        try { using var mutex = Mutex.OpenExisting(UpdateMutexName(target)); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    public static bool IsInstalled(string targetDirectory)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{C29A17C4-DC3A-4E06-9721-2A3CFD4F1AC4}_is1";
        using var uninstall = Registry.CurrentUser.OpenSubKey(key);
        return uninstall?.GetValue("InstallLocation") is string location &&
            string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(targetDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUpdateHelper(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var executable = process.MainModule?.FileName;
            var updatesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeamerPresenter", "Presenter", "Updates") + Path.DirectorySeparatorChar;
            return executable is not null && executable.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(executable) == BeamerPresenter.Infrastructure.PortableUpdateFiles.UpdaterName &&
                Matches(process, executable, CurrentSid);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }

    internal static bool Matches(Process process, string executablePath, string userSid, long? startTicks = null)
    {
        try
        {
            if (process.HasExited || !string.Equals(process.MainModule?.FileName, Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase) ||
                (startTicks.HasValue && process.StartTime.ToUniversalTime().Ticks != startTicks)) return false;
            if (!OpenProcessToken(process.Handle, 8, out var token)) return false;
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.Value == userSid;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }

    private static async Task RequestShutdownAsync(Process process, string userSid, CancellationToken token)
    {
        var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..16];
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "BeamerPresenterForLanParties." + userHash, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes($"shutdown:{process.Id}\n"), timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            _ = await reader.ReadLineAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException) { }
    }

    public static async Task StopTargetAsync(string executablePath, string userSid, CancellationToken token = default,
        TimeSpan? gracefulTimeout = null)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
        try
        {
            var targets = processes.Where(process => Matches(process, executablePath, userSid)).ToArray();
            using var graceful = CancellationTokenSource.CreateLinkedTokenSource(token);
            graceful.CancelAfter(gracefulTimeout ?? TimeSpan.FromSeconds(30));
            foreach (var process in targets)
            {
                if (graceful.IsCancellationRequested) break;
                await RequestShutdownAsync(process, userSid, graceful.Token);
            }
            try { await Task.WhenAll(targets.Select(process => process.WaitForExitAsync(graceful.Token))); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                foreach (var process in targets)
                    if (Matches(process, executablePath, userSid)) process.Kill(entireProcessTree: true);
                using var forced = CancellationTokenSource.CreateLinkedTokenSource(token);
                forced.CancelAfter(TimeSpan.FromSeconds(5));
                await Task.WhenAll(targets.Select(process => process.WaitForExitAsync(forced.Token)));
            }
            var remaining = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
            try
            {
                if (remaining.Any(process => Matches(process, executablePath, userSid))) throw new IOException("Presenter is still running.");
            }
            finally { foreach (var process in remaining) process.Dispose(); }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static void CheckWritable(string directory, long requiredBytes)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new IOException("Install directory is unavailable.");
        _ = BeamerPresenter.Infrastructure.PortableUpdateFiles.SafePath(root, ".update-probe");
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.AvailableFreeSpace < requiredBytes) throw new IOException("Insufficient free space.");
        var probe = Path.Combine(root, ".update-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
}
