using System.Diagnostics;
using System.Text.Json;
using BeamerPresenter.App;

namespace BeamerPresenter.App.Tests;

public sealed class FirewallProcessTests
{
    [Fact]
    public async Task Process_runner_captures_output_and_drains_errors_without_opening_a_window()
    {
        var result = await new FirewallProcessRunner().RunAsync(WindowsFirewallService.CreatePowerShellStartInfo(
            "[Console]::Error.WriteLine('simulated error'); Write-Output 'hello'; exit 7"), CancellationToken.None);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("hello", result.Output.Trim());
    }

    [Fact]
    public async Task Process_runner_honors_cancellation_before_start_and_while_running()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new FirewallProcessRunner().RunAsync(
            new ProcessStartInfo("must-not-start.exe"), cancelled.Token));
        using var running = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FirewallProcessRunner().RunAsync(
            WindowsFirewallService.CreatePowerShellStartInfo("Start-Sleep -Seconds 20"), running.Token));
    }

    [Fact]
    public async Task Process_runner_times_out_and_terminates_its_own_child()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => new FirewallProcessRunner(TimeSpan.FromMilliseconds(300)).RunAsync(
            WindowsFirewallService.CreatePowerShellStartInfo("Start-Sleep -Seconds 20"), CancellationToken.None));
    }

    [Fact]
    public async Task Real_query_can_read_and_deserialize_the_windows_policy_without_mutation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "BeamerPresenter.App.exe");
        var result = await new FirewallProcessRunner().RunAsync(WindowsFirewallService.CreatePowerShellStartInfo(
            WindowsFirewallScripts.WithRequest(WindowsFirewallScripts.Query, 8765, path)), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var snapshot = JsonSerializer.Deserialize<FirewallSnapshot>(result.Output);
        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot.Profiles.Length);
        Assert.All(snapshot.Rules, rule => Assert.True(rule.FiltersComplete));
        Assert.Equal(3, FirewallPolicy.Evaluate(snapshot, 8765, path).Profiles.Count);
    }

    [Fact]
    public async Task Rule_script_creates_then_updates_the_same_rule_and_port_with_subnet_and_program_scope()
    {
        // These functions shadow NetSecurity cmdlets. No real firewall rule is ever written.
        var fake = """
            $script:rule = $null
            $script:creates = 0; $script:updates = 0
            function Get-NetFirewallRule { param($PolicyStore) if ($script:rule) { $script:rule } }
            function Get-NetFirewallApplicationFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject)
                process { [pscustomobject]@{ Program = $InputObject.Program } }
            }
            function New-NetFirewallRule {
                param($Name,$DisplayName,$Group,$PolicyStore,$Description,$Enabled,$Direction,$Action,$Profile,
                    $Protocol,$LocalPort,$RemotePort,$Program,$Service,$LocalAddress,$RemoteAddress,$InterfaceAlias,$InterfaceType,$EdgeTraversalPolicy)
                $script:creates++
                $script:rule = [pscustomobject]@{ Name=$Name; Group=$Group; Description=$Description; Program=$Program;
                    Port=$LocalPort; Profile=$Profile; RemoteAddress=$RemoteAddress; Protocol=$Protocol;
                    Direction=$Direction; Action=$Action; Enabled=$Enabled; EdgeTraversal=$EdgeTraversalPolicy }
            }
            function Set-NetFirewallRule {
                param($Name,$NewDisplayName,$PolicyStore,$Description,$Enabled,$Direction,$Action,$Profile,
                    $Protocol,$LocalPort,$RemotePort,$Program,$Service,$LocalAddress,$RemoteAddress,$InterfaceAlias,$InterfaceType,$EdgeTraversalPolicy)
                if ($Name -ne $script:rule.Name) { throw 'Incorrect rule identity' }
                $script:updates++; $script:rule.Port = $LocalPort
            }
            """;
        var path = @"C:\Presenter's `$folder\BeamerPresenter.App.exe";
        var script = fake + WindowsFirewallScripts.WithRequest(WindowsFirewallScripts.EnsureRule, 8765, path) + "\n" +
            WindowsFirewallScripts.WithRequest(WindowsFirewallScripts.EnsureRule, 8765, path) + "\n" +
            WindowsFirewallScripts.WithRequest(WindowsFirewallScripts.EnsureRule, 9123, path) + "\n" +
            "[pscustomobject]@{Creates=$script:creates;Updates=$script:updates;Rule=$script:rule} | ConvertTo-Json -Compress";
        var result = await new FirewallProcessRunner().RunAsync(WindowsFirewallService.CreatePowerShellStartInfo(script), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(1, json.RootElement.GetProperty("Creates").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("Updates").GetInt32());
        var rule = json.RootElement.GetProperty("Rule");
        Assert.Equal("9123", rule.GetProperty("Port").GetString());
        Assert.Equal("Any", rule.GetProperty("Profile").GetString());
        Assert.Equal(path, rule.GetProperty("Program").GetString());
        Assert.Equal("LocalSubnet", rule.GetProperty("RemoteAddress").GetString());
        Assert.Equal("Block", rule.GetProperty("EdgeTraversal").GetString());
        Assert.Equal("TCP", rule.GetProperty("Protocol").GetString());
        Assert.Equal("Inbound", rule.GetProperty("Direction").GetString());
        Assert.Equal("Allow", rule.GetProperty("Action").GetString());
        Assert.Equal("True", rule.GetProperty("Enabled").GetString());
    }

    [Theory]
    [InlineData("Other", "BeamerPresenter managed LAN web access", @"C:\Presenter.exe")]
    [InlineData("BeamerPresenter", "Other", @"C:\Presenter.exe")]
    [InlineData("BeamerPresenter", "BeamerPresenter managed LAN web access", @"C:\Other.exe")]
    public async Task Name_collision_never_takes_over_a_foreign_rule(string group, string description, string existingProgram)
    {
        var fake = """
            function Get-NetFirewallRule { param($PolicyStore)
                [pscustomobject]@{ Name=$request.Name; Group=$fixture.Group; Description=$fixture.Description; Program=$fixture.Program }
            }
            function Get-NetFirewallApplicationFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject)
                process { [pscustomobject]@{ Program = $InputObject.Program } }
            }
            function Set-NetFirewallRule { Write-Output 'MUTATED'; throw 'Must not update a foreign rule' }
            function New-NetFirewallRule { Write-Output 'MUTATED'; throw 'Must not create duplicate rule' }
            """;
        var fixture = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Group = group, Description = description, Program = existingProgram })));
        var script = "$fixture = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + fixture + "')) | ConvertFrom-Json\n" +
            fake + WindowsFirewallScripts.WithRequest(WindowsFirewallScripts.EnsureRule, 8765, @"C:\Presenter.exe");
        var result = await new FirewallProcessRunner().RunAsync(WindowsFirewallService.CreatePowerShellStartInfo(script), CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("MUTATED", result.Output);
    }

    [Fact]
    public async Task Invalid_helper_cli_exits_before_acquiring_the_normal_instance_or_starting_the_host()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "BeamerPresenter.App.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("--allow-web-firewall");
        startInfo.ArgumentList.Add("80");
        var result = await new FirewallProcessRunner().RunAsync(startInfo, CancellationToken.None);
        Assert.Equal(2, result.ExitCode);
    }
}
