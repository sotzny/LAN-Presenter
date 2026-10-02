using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BeamerPresenter.App;

internal interface IWindowsFirewallService
{
    Task<FirewallCheckResult> CheckAsync(int port, CancellationToken cancellationToken = default);
    Task<FirewallOpenResult> AllowAsync(int port, CancellationToken cancellationToken = default);
}

internal enum FirewallOpenStatus { Allowed, Cancelled, Failed, NotConfirmed }
internal sealed record FirewallOpenResult(FirewallOpenStatus Status, FirewallCheckResult Check);
internal sealed record FirewallProcessResult(int ExitCode, string Output);

internal interface IFirewallProcessRunner
{
    Task<FirewallProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken);
}

internal sealed class FirewallProcessRunner(TimeSpan? processTimeout = null) : IFirewallProcessRunner
{
    public async Task<FirewallProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Starting runas may wait for UAC. Keep even that wait off the WinForms thread.
        using var process = await Task.Run(() => Process.Start(startInfo) ?? throw new InvalidOperationException("Firewall process did not start"), cancellationToken);
        var output = startInfo.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
        var errors = startInfo.RedirectStandardError ? process.StandardError.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(processTimeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var result = await output;
            await errors;
            return new(process.ExitCode, result);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            // Drain the pipes before disposing them; stderr is deliberately never exposed in the UI.
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("Firewall process timed out");
            throw;
        }
    }
}

internal sealed class WindowsFirewallService(string executablePath, IFirewallProcessRunner runner) : IWindowsFirewallService
{
    internal const string HelperArgument = "--allow-web-firewall";

    public async Task<FirewallCheckResult> CheckAsync(int port, CancellationToken cancellationToken = default)
    {
        FirewallPolicy.ValidatePort(port);
        try
        {
            var result = await runner.RunAsync(CreatePowerShellStartInfo(WindowsFirewallScripts.WithRequest(WindowsFirewallScripts.Query, port, executablePath)), cancellationToken);
            if (result.ExitCode != 0) return FirewallCheckResult.Unknown;
            var snapshot = JsonSerializer.Deserialize<FirewallSnapshot>(result.Output);
            return snapshot?.Profiles is null || snapshot.Rules is null ? FirewallCheckResult.Unknown : FirewallPolicy.Evaluate(snapshot, port, executablePath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is Win32Exception or IOException or JsonException or TimeoutException or InvalidOperationException)
        {
            Serilog.Log.Warning("Windows firewall check failed ({ErrorType})", exception.GetType().Name);
            return FirewallCheckResult.Unknown;
        }
    }

    public async Task<FirewallOpenResult> AllowAsync(int port, CancellationToken cancellationToken = default)
    {
        FirewallPolicy.ValidatePort(port);
        try
        {
            var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            startInfo.ArgumentList.Add(HelperArgument);
            startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
            var result = await runner.RunAsync(startInfo, cancellationToken);
            var check = await CheckAsync(port, cancellationToken);
            return new(result.ExitCode != 0 ? FirewallOpenStatus.Failed : check.IsAllowed ? FirewallOpenStatus.Allowed : FirewallOpenStatus.NotConfirmed, check);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new(FirewallOpenStatus.Cancelled, await CheckAsync(port, cancellationToken));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is Win32Exception or IOException or TimeoutException or InvalidOperationException)
        {
            Serilog.Log.Warning("Windows firewall update failed ({ErrorType})", exception.GetType().Name);
            return new(FirewallOpenStatus.Failed, await CheckAsync(port, cancellationToken));
        }
    }

    internal static ProcessStartInfo CreatePowerShellStartInfo(string script)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script)));
        return startInfo;
    }

    internal static bool IsHelperRequest(string[] args) => args.Any(arg => arg.Equals(HelperArgument, StringComparison.OrdinalIgnoreCase));

    internal static async Task<int> RunHelperAsync(string[] args, string executablePath, IFirewallProcessRunner runner)
    {
        if (args.Length != 2 || !args[0].Equals(HelperArgument, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535) return 2;
        try
        {
            var result = await runner.RunAsync(CreatePowerShellStartInfo(WindowsFirewallScripts.WithRequest(WindowsFirewallScripts.EnsureRule, port, executablePath)), CancellationToken.None);
            return result.ExitCode == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or TimeoutException or InvalidOperationException) { return 1; }
    }
}
