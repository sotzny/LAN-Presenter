namespace BeamerPresenter.App;

internal enum FirewallStatus { Allowed, Missing, Blocked, Disabled, Unknown }

internal sealed record FirewallProfileResult(string Profile, FirewallStatus Status);

internal sealed record FirewallCheckResult(IReadOnlyList<FirewallProfileResult> Profiles)
{
    public bool IsAllowed => Profiles.Count == 3 && Profiles.All(profile => profile.Status == FirewallStatus.Allowed);
    public bool NeedsRule => Profiles.Any(profile => profile.Status is FirewallStatus.Missing or FirewallStatus.Unknown);
    public static FirewallCheckResult Unknown => new(FirewallPolicy.ProfileNames.Select(name => new FirewallProfileResult(name, FirewallStatus.Unknown)).ToArray());
}

internal sealed record FirewallSnapshot
{
    public FirewallProfileSnapshot[] Profiles { get; init; } = [];
    public FirewallRuleSnapshot[] Rules { get; init; } = [];
}

internal sealed record FirewallProfileSnapshot(string Name)
{
    public string Enabled { get; init; } = "True";
    public string DefaultInboundAction { get; init; } = "Block";
    public string AllowInboundRules { get; init; } = "True";
    public string AllowLocalFirewallRules { get; init; } = "True";
}

internal sealed record FirewallRuleSnapshot
{
    public string Enabled { get; init; } = "True";
    public string Direction { get; init; } = "Inbound";
    public string Action { get; init; } = "Allow";
    public string Profile { get; init; } = "Any";
    public string Protocol { get; init; } = "TCP";
    public string Program { get; init; } = "Any";
    public string[] LocalPort { get; init; } = ["Any"];
    public string[] RemotePort { get; init; } = ["Any"];
    public string[] LocalAddress { get; init; } = ["Any"];
    public string[] RemoteAddress { get; init; } = ["Any"];
    public string[] InterfaceAlias { get; init; } = ["Any"];
    public string InterfaceType { get; init; } = "Any";
    public string Service { get; init; } = "Any";
    public string Package { get; init; } = "";
    public string Authentication { get; init; } = "NotRequired";
    public string Encryption { get; init; } = "NotRequired";
    public string LocalUser { get; init; } = "Any";
    public string RemoteUser { get; init; } = "Any";
    public string RemoteMachine { get; init; } = "Any";
    public string PolicyStoreSourceType { get; init; } = "Local";
    public string PrimaryStatus { get; init; } = "OK";
    public string[] EnforcementStatus { get; init; } = ["Enforced"];
    public string Owner { get; init; } = "";
    public string PolicyAppId { get; init; } = "";
    public string[] Platform { get; init; } = [];
    public string[] RemoteDynamicKeywordAddresses { get; init; } = [];
    public bool FiltersComplete { get; init; } = true;
}

internal static class FirewallPolicy
{
    public static readonly string[] ProfileNames = ["Private", "Public", "Domain"];

    public static FirewallCheckResult Evaluate(FirewallSnapshot snapshot, int port, string executablePath)
    {
        ValidatePort(port);
        return new(ProfileNames.Select(name => new FirewallProfileResult(name,
            EvaluateProfile(snapshot, name, port, executablePath))).ToArray());
    }

    public static void ValidatePort(int port) => ArgumentOutOfRangeException.ThrowIfNotEqual(port is >= 1024 and <= 65535, true, nameof(port));

    private static FirewallStatus EvaluateProfile(FirewallSnapshot snapshot, string name, int port, string path)
    {
        var profiles = snapshot.Profiles.Where(profile => Is(profile.Name, name)).ToArray();
        if (profiles.Length != 1) return FirewallStatus.Unknown;
        var profile = profiles[0];
        if (Is(profile.Enabled, "False")) return FirewallStatus.Disabled;
        if (!Is(profile.Enabled, "True")) return FirewallStatus.Unknown;
        if (Is(profile.AllowInboundRules, "False")) return FirewallStatus.Blocked;

        var allowed = Is(profile.DefaultInboundAction, "Allow");
        var uncertain = !Is(profile.DefaultInboundAction, "Block") && !allowed;
        var partialAllow = false;
        foreach (var rule in snapshot.Rules)
        {
            if (!Is(rule.Enabled, "True") || !Is(rule.Direction, "Inbound") || !AppliesToProfile(rule.Profile, name)) continue;
            if (!rule.FiltersComplete) { uncertain = true; continue; }
            if (!Is(rule.Protocol, "TCP", "6", "Any", "256")) continue;
            if (!IsAny(rule.Program) && !Is(Environment.ExpandEnvironmentVariables(rule.Program), path)) continue;
            // The WinForms host is neither a Windows service nor a packaged application.
            if (!IsAny(rule.Service) || !string.IsNullOrEmpty(rule.Package)) continue;
            var portMatch = MatchesPort(rule.LocalPort, port);
            if (portMatch == false) continue;
            if (Is(profile.AllowLocalFirewallRules, "False") && Is(rule.PolicyStoreSourceType, "Local")) continue;

            var unrestricted = portMatch == true && IsUnrestricted(rule);
            if (Is(rule.Action, "Block") && unrestricted) return FirewallStatus.Blocked;
            if (!unrestricted && Is(rule.Action, "Allow")) partialAllow = true;
            else if (!unrestricted || !Is(rule.Action, "Allow", "Block")) uncertain = true;
            else if (Is(rule.Action, "Allow")) allowed = true;
        }

        // A partial block or an unsupported condition may invalidate any allow rule.
        if (uncertain) return FirewallStatus.Unknown;
        if (allowed) return FirewallStatus.Allowed;
        if (partialAllow) return FirewallStatus.Unknown;
        return Is(profile.AllowLocalFirewallRules, "False") ? FirewallStatus.Blocked : FirewallStatus.Missing;
    }

    private static bool IsUnrestricted(FirewallRuleSnapshot rule) =>
        rule.FiltersComplete && Is(rule.PrimaryStatus, "OK") && rule.EnforcementStatus.Length > 0 &&
        rule.EnforcementStatus.All(status => Is(status, "Enforced", "ProfileInactive", "InactiveProfile")) &&
        AllAny(rule.RemotePort) && AllAny(rule.LocalAddress) &&
        rule.RemoteAddress.Any(address => Is(address, "Any", "*", "LocalSubnet", "LocalSubnet4")) &&
        AllAny(rule.InterfaceAlias) && IsAny(rule.InterfaceType) && IsAny(rule.Service) &&
        string.IsNullOrEmpty(rule.Package) && string.IsNullOrEmpty(rule.PolicyAppId) && string.IsNullOrEmpty(rule.Owner) &&
        rule.Platform.Length == 0 && rule.RemoteDynamicKeywordAddresses.Length == 0 &&
        Is(rule.Authentication, "NotRequired") && Is(rule.Encryption, "NotRequired") &&
        IsAny(rule.LocalUser) && IsAny(rule.RemoteUser) && IsAny(rule.RemoteMachine);

    internal static bool? MatchesPort(IEnumerable<string> values, int port)
    {
        var unknown = false;
        var any = false;
        foreach (var token in values.SelectMany(value => value.Split(',')))
        {
            any = true;
            var value = token.Trim();
            if (IsAny(value)) return true;
            if (int.TryParse(value, out var single)) { if (single == port) return true; continue; }
            var range = value.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out var start) && int.TryParse(range[1], out var end) && start <= end)
            {
                if (port >= start && port <= end) return true;
            }
            else unknown = true;
        }
        return unknown || !any ? null : false;
    }

    private static bool AppliesToProfile(string profiles, string name) =>
        Is(profiles, "Any") || profiles.Split(',').Any(value => Is(value.Trim(), name));
    private static bool AllAny(string[] values) => values.Length > 0 && values.All(IsAny);
    private static bool IsAny(string value) => Is(value, "Any", "*");
    private static bool Is(string value, params string[] candidates) => candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
}
