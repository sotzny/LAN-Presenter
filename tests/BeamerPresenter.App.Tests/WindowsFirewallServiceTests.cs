using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BeamerPresenter.App;

namespace BeamerPresenter.App.Tests;

public sealed class WindowsFirewallServiceTests
{
    private const string AppPath = @"C:\My Presenter\BeamerPresenter.App.exe";
    private static string Snapshot(bool allowed) => JsonSerializer.Serialize(new FirewallSnapshot
    {
        Profiles = FirewallPolicy.ProfileNames.Select(name => new FirewallProfileSnapshot(name)).ToArray(),
        Rules = allowed ? [new() { Program = AppPath, LocalPort = ["8765"] }] : []
    });

    [Fact]
    public async Task Check_reads_active_policy_without_elevation_or_shell_interpretation()
    {
        var runner = new FakeRunner(new FirewallProcessResult(0, Snapshot(true)));
        var result = await new WindowsFirewallService(AppPath, runner).CheckAsync(8765);
        Assert.True(result.IsAllowed);
        var call = Assert.Single(runner.Calls);
        Assert.False(call.UseShellExecute);
        Assert.True(call.CreateNoWindow);
        Assert.True(call.RedirectStandardOutput);
        Assert.EndsWith(@"System32\WindowsPowerShell\v1.0\powershell.exe", call.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand" }, call.ArgumentList.Take(3));
        Assert.Contains("-PolicyStore ActiveStore", Decode(call));
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "invalid JSON")]
    [InlineData(0, "null")]
    [InlineData(0, "{\"Profiles\":null,\"Rules\":null}")]
    public async Task Failed_or_corrupt_queries_are_not_reported_as_open(int exitCode, string output)
    {
        var result = await new WindowsFirewallService(AppPath, new FakeRunner(new FirewallProcessResult(exitCode, output))).CheckAsync(8765);
        Assert.All(result.Profiles, profile => Assert.Equal(FirewallStatus.Unknown, profile.Status));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("access")]
    [InlineData("io")]
    [InlineData("start")]
    public async Task Query_process_errors_become_an_unknown_status(string kind)
    {
        var result = await new WindowsFirewallService(AppPath, new ThrowingRunner(ExceptionFor(kind))).CheckAsync(8765);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Open_launches_only_the_app_helper_then_checks_effective_policy()
    {
        var runner = new FakeRunner(new(0, ""), new(0, Snapshot(true)));
        var result = await new WindowsFirewallService(AppPath, runner).AllowAsync(8765);
        Assert.Equal(FirewallOpenStatus.Allowed, result.Status);
        Assert.Equal(2, runner.Calls.Count);
        var helper = runner.Calls[0];
        Assert.Equal(AppPath, helper.FileName);
        Assert.True(helper.UseShellExecute);
        Assert.Equal("runas", helper.Verb);
        Assert.Equal(new[] { WindowsFirewallService.HelperArgument, "8765" }, helper.ArgumentList);
        Assert.False(runner.Calls[1].UseShellExecute);
    }

    [Theory]
    [InlineData(0, FirewallOpenStatus.NotConfirmed)]
    [InlineData(1, FirewallOpenStatus.Failed)]
    public async Task Successful_process_exit_is_insufficient_without_confirmed_access(int helperExit, object expected)
    {
        var runner = new FakeRunner(new(helperExit, ""), new(0, Snapshot(false)));
        Assert.Equal((FirewallOpenStatus)expected, (await new WindowsFirewallService(AppPath, runner).AllowAsync(8765)).Status);
    }

    [Fact]
    public async Task Uac_cancellation_is_distinguished_from_failure_and_refreshes_status()
    {
        var runner = new ThrowingRunner(new Win32Exception(1223), new(0, Snapshot(false)));
        Assert.Equal(FirewallOpenStatus.Cancelled, (await new WindowsFirewallService(AppPath, runner).AllowAsync(8765)).Status);
        Assert.Equal(2, runner.Count);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("access")]
    [InlineData("io")]
    [InlineData("start")]
    public async Task Failed_elevation_refreshes_status_and_never_claims_success(string kind)
    {
        var runner = new ThrowingRunner(ExceptionFor(kind), new(0, Snapshot(true)));
        Assert.Equal(FirewallOpenStatus.Failed, (await new WindowsFirewallService(AppPath, runner).AllowAsync(8765)).Status);
    }

    [Fact]
    public async Task Invalid_port_and_helper_arguments_cannot_reach_the_process_runner()
    {
        var runner = new FakeRunner();
        var service = new WindowsFirewallService(AppPath, runner);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CheckAsync(80));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AllowAsync(65536));
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--allow-web-firewall" }, new[] { "--allow-web-firewall", "80" },
            new[] { "--allow-web-firewall", "8765;whoami" }, new[] { "--allow-web-firewall", "8765", "--autostart" }, new[] { "--other", "8765" } })
            Assert.Equal(2, await WindowsFirewallService.RunHelperAsync(args, AppPath, runner));
        Assert.Empty(runner.Calls);
        Assert.True(WindowsFirewallService.IsHelperRequest(["--autostart", "--ALLOW-WEB-FIREWALL"]));
        Assert.False(WindowsFirewallService.IsHelperRequest(["--autostart"]));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task Valid_helper_request_runs_only_the_rule_script(int processExit, int expected)
    {
        var runner = new FakeRunner(new FirewallProcessResult(processExit, ""));
        Assert.Equal(expected, await WindowsFirewallService.RunHelperAsync(["--allow-web-firewall", "8765"], AppPath, runner));
        Assert.Contains("New-NetFirewallRule", Decode(Assert.Single(runner.Calls)));
        Assert.DoesNotContain("Get-NetFirewallProfile", Decode(runner.Calls[0]));
    }

    [Fact]
    public async Task Helper_process_failure_has_a_nonzero_exit_code()
    {
        Assert.Equal(1, await WindowsFirewallService.RunHelperAsync(["--allow-web-firewall", "8765"], AppPath, new ThrowingRunner(new Win32Exception(5))));
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        var service = new WindowsFirewallService(AppPath, new ThrowingRunner(new OperationCanceledException()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CheckAsync(8765));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.AllowAsync(8765));
    }

    [Fact]
    public void Paths_are_encoded_as_data_and_rule_identity_survives_port_changes_and_path_casing()
    {
        const string path = @"C:\Presenter's `$folder\BeamerPresenter.App.exe";
        var script = WindowsFirewallScripts.WithRequest(WindowsFirewallScripts.Query, 8765, path);
        Assert.DoesNotContain(path, script);
        var encoded = script.Split("FromBase64String('")[1].Split("')")[0];
        using var request = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.Equal(path, request.RootElement.GetProperty("Program").GetString());
        Assert.Equal(8765, request.RootElement.GetProperty("Port").GetInt32());
        Assert.Equal(WindowsFirewallScripts.RuleName(path), WindowsFirewallScripts.RuleName(path.ToUpperInvariant()));
        Assert.NotEqual(WindowsFirewallScripts.RuleName(path), WindowsFirewallScripts.RuleName(AppPath));
        Assert.Contains(WindowsFirewallScripts.RuleName(path), Encoding.UTF8.GetString(Convert.FromBase64String(
            WindowsFirewallScripts.WithRequest("", 9123, path).Split("FromBase64String('")[1].Split("')")[0])));
    }

    private static Exception ExceptionFor(string kind) => kind switch
    { "timeout" => new TimeoutException(), "access" => new Win32Exception(5), "io" => new IOException(), _ => new InvalidOperationException() };
    private static string Decode(ProcessStartInfo info) => Encoding.Unicode.GetString(Convert.FromBase64String(info.ArgumentList[3]));

    private sealed class FakeRunner(params FirewallProcessResult[] results) : IFirewallProcessRunner
    {
        private readonly Queue<FirewallProcessResult> _results = new(results);
        public List<ProcessStartInfo> Calls { get; } = [];
        public Task<FirewallProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            Calls.Add(startInfo);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ThrowingRunner(Exception exception, FirewallProcessResult? subsequent = null) : IFirewallProcessRunner
    {
        public int Count { get; private set; }
        public Task<FirewallProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken) =>
            ++Count == 1 || subsequent is null ? Task.FromException<FirewallProcessResult>(exception) : Task.FromResult(subsequent);
    }
}
