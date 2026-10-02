using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BeamerPresenter.App;

internal static class WindowsFirewallScripts
{
    public static string RuleName(string path) => "BeamerPresenter-Web-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..24];

    public static string WithRequest(string script, int port, string path)
    {
        FirewallPolicy.ValidatePort(port);
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Port = port, Program = path, Name = RuleName(path) })));
        return "$ErrorActionPreference = 'Stop'\n" +
            "$request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" + script;
    }

    // Batch each filter query and join by InstanceID: one filter per rule, including ActiveStore policies.
    public const string Query = """
        function Index-Filters($items) {
            $index = @{}
            foreach ($item in $items) { $index[$item.InstanceID] = $item }
            return $index
        }
        $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore | ForEach-Object {
            [pscustomobject]@{
                Name = [string]$_.Name; Enabled = [string]$_.Enabled
                DefaultInboundAction = [string]$_.DefaultInboundAction
                AllowInboundRules = [string]$_.AllowInboundRules
                AllowLocalFirewallRules = [string]$_.AllowLocalFirewallRules
            }
        })
        $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Inbound)
        $ports = Index-Filters @($rules | Get-NetFirewallPortFilter)
        $apps = Index-Filters @($rules | Get-NetFirewallApplicationFilter)
        $candidates = @($rules | Where-Object {
            $p = $ports[$_.InstanceID]; $a = $apps[$_.InstanceID]
            (!$p -or [string]$p.Protocol -in @('TCP', '6', 'Any', '256')) -and
            (!$a -or [string]$a.Program -in @('Any', '*') -or
                [Environment]::ExpandEnvironmentVariables([string]$a.Program) -eq $request.Program)
        })
        $addresses = Index-Filters @($candidates | Get-NetFirewallAddressFilter)
        $services = Index-Filters @($candidates | Get-NetFirewallServiceFilter)
        $interfaces = Index-Filters @($candidates | Get-NetFirewallInterfaceFilter)
        $types = Index-Filters @($candidates | Get-NetFirewallInterfaceTypeFilter)
        $security = Index-Filters @($candidates | Get-NetFirewallSecurityFilter)
        $result = @($candidates | ForEach-Object {
            $id = $_.InstanceID
            $p = $ports[$id]; $a = $apps[$id]; $d = $addresses[$id]
            $s = $services[$id]; $i = $interfaces[$id]; $t = $types[$id]; $c = $security[$id]
            [pscustomobject]@{
                Enabled = [string]$_.Enabled; Direction = [string]$_.Direction
                Action = [string]$_.Action; Profile = [string]$_.Profile
                PrimaryStatus = [string]$_.PrimaryStatus; PolicyStoreSourceType = [string]$_.PolicyStoreSourceType
                EnforcementStatus = @($_.EnforcementStatus | ForEach-Object { [string]$_ }); Owner = [string]$_.Owner
                PolicyAppId = [string]$_.PolicyAppId
                Platform = @($_.Platform | ForEach-Object { [string]$_ })
                RemoteDynamicKeywordAddresses = @($_.RemoteDynamicKeywordAddresses | ForEach-Object { [string]$_ })
                Protocol = [string]$p.Protocol; Program = [string]$a.Program; Package = [string]$a.Package
                LocalPort = @($p.LocalPort | ForEach-Object { [string]$_ })
                RemotePort = @($p.RemotePort | ForEach-Object { [string]$_ })
                LocalAddress = @($d.LocalAddress | ForEach-Object { [string]$_ })
                RemoteAddress = @($d.RemoteAddress | ForEach-Object { [string]$_ })
                Service = [string]$s.Service; InterfaceType = [string]$t.InterfaceType
                InterfaceAlias = @($i.InterfaceAlias | ForEach-Object { [string]$_ })
                Authentication = [string]$c.Authentication; Encryption = [string]$c.Encryption
                LocalUser = [string]$c.LocalUser; RemoteUser = [string]$c.RemoteUser; RemoteMachine = [string]$c.RemoteMachine
                FiltersComplete = [bool]($p -and $a -and $d -and $s -and $i -and $t -and $c)
            }
        })
        [pscustomobject]@{ Profiles = $profiles; Rules = $result } | ConvertTo-Json -Depth 6 -Compress
        """;

    public const string EnsureRule = """
        $existing = @(Get-NetFirewallRule -PolicyStore PersistentStore | Where-Object { $_.Name -eq $request.Name })
        $parameters = @{
            PolicyStore = 'PersistentStore'; Description = 'BeamerPresenter managed LAN web access'
            Enabled = 'True'; Direction = 'Inbound'; Action = 'Allow'; Profile = 'Any'
            Protocol = 'TCP'; LocalPort = [string]$request.Port; RemotePort = 'Any'
            Program = $request.Program; Service = 'Any'; LocalAddress = 'Any'; RemoteAddress = 'LocalSubnet'
            InterfaceAlias = 'Any'; InterfaceType = 'Any'; EdgeTraversalPolicy = 'Block'
        }
        if ($existing.Count -gt 0) {
            # Refuse a name collision rather than taking ownership of a foreign rule.
            if ($existing.Count -ne 1 -or $existing[0].Group -ne 'BeamerPresenter' -or
                $existing[0].Description -ne 'BeamerPresenter managed LAN web access') { throw 'Firewall rule ownership mismatch' }
            $app = $existing[0] | Get-NetFirewallApplicationFilter
            if ($app.Program -ne $request.Program) { throw 'Firewall rule program mismatch' }
            Set-NetFirewallRule -Name $request.Name -NewDisplayName 'Beamer Presenter - Web UI' @parameters | Out-Null
        } else {
            New-NetFirewallRule -Name $request.Name -DisplayName 'Beamer Presenter - Web UI' -Group 'BeamerPresenter' @parameters | Out-Null
        }
        """;
}
