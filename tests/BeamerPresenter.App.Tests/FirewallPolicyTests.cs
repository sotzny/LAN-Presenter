using BeamerPresenter.App;

namespace BeamerPresenter.App.Tests;

public sealed class FirewallPolicyTests
{
    private const string ProgramPath = @"C:\Presenter\BeamerPresenter.App.exe";
    private static FirewallSnapshot Snapshot(params FirewallRuleSnapshot[] rules) => new()
    {
        Profiles = FirewallPolicy.ProfileNames.Select(name => new FirewallProfileSnapshot(name)).ToArray(),
        Rules = rules
    };
    private static FirewallRuleSnapshot Rule => new() { Program = ProgramPath, LocalPort = ["8765"], RemoteAddress = ["LocalSubnet"] };

    [Fact]
    public void Missing_rule_is_reported_for_every_profile()
    {
        var result = FirewallPolicy.Evaluate(Snapshot(), 8765, ProgramPath);
        Assert.False(result.IsAllowed);
        Assert.True(result.NeedsRule);
        Assert.All(result.Profiles, profile => Assert.Equal(FirewallStatus.Missing, profile.Status));
    }

    [Fact]
    public void Program_and_subnet_scoped_rule_covers_all_profiles()
    {
        var result = FirewallPolicy.Evaluate(Snapshot(Rule), 8765, ProgramPath);
        Assert.True(result.IsAllowed);
        Assert.False(result.NeedsRule);
    }

    [Theory]
    [InlineData("Any", "Any")]
    [InlineData("Any", "8760-8770")]
    [InlineData("Any", "80,443,8765")]
    [InlineData(ProgramPath, "Any")]
    [InlineData("c:\\presenter\\BEAMERPRESENTER.app.exe", "8765")]
    public void Existing_generic_port_or_program_rules_are_recognized(string program, string ports)
    {
        Assert.True(FirewallPolicy.Evaluate(Snapshot(Rule with { Program = program, LocalPort = [ports] }), 8765, ProgramPath).IsAllowed);
    }

    [Fact]
    public void Rules_for_other_ports_programs_protocols_directions_and_disabled_rules_do_not_allow_access()
    {
        var result = FirewallPolicy.Evaluate(Snapshot(
            Rule with { LocalPort = ["80", "8700-8764"] }, Rule with { Program = @"C:\Other.exe" },
            Rule with { Protocol = "UDP" }, Rule with { Direction = "Outbound" }, Rule with { Enabled = "False" }), 8765, ProgramPath);
        Assert.All(result.Profiles, profile => Assert.Equal(FirewallStatus.Missing, profile.Status));
    }

    [Fact]
    public void Profile_coverage_is_not_confused_with_a_global_allow()
    {
        var result = FirewallPolicy.Evaluate(Snapshot(Rule with { Profile = "Private, Domain" }), 8765, ProgramPath);
        Assert.False(result.IsAllowed);
        Assert.Equal(FirewallStatus.Missing, result.Profiles.Single(profile => profile.Profile == "Public").Status);
        Assert.Equal(FirewallStatus.Allowed, result.Profiles.Single(profile => profile.Profile == "Private").Status);
    }

    [Fact]
    public void Block_takes_precedence_over_an_allow_regardless_of_rule_order()
    {
        foreach (var rules in new[] { new[] { Rule, Rule with { Action = "Block" } }, new[] { Rule with { Action = "Block" }, Rule } })
            Assert.All(FirewallPolicy.Evaluate(Snapshot(rules), 8765, ProgramPath).Profiles, profile => Assert.Equal(FirewallStatus.Blocked, profile.Status));
    }

    [Fact]
    public void Partial_blocks_prevent_false_success_but_partial_allows_do_not_invalidate_a_complete_allow()
    {
        var restricted = Rule with { RemoteAddress = ["192.168.1.50"] };
        Assert.True(FirewallPolicy.Evaluate(Snapshot(restricted, Rule), 8765, ProgramPath).IsAllowed);
        Assert.All(FirewallPolicy.Evaluate(Snapshot(restricted with { Action = "Block" }, Rule), 8765, ProgramPath).Profiles,
            profile => Assert.Equal(FirewallStatus.Unknown, profile.Status));
    }

    public static TheoryData<object> Restrictions => new()
    {
        Rule with { RemoteAddress = ["192.168.1.50"] }, Rule with { LocalAddress = ["192.168.1.2"] },
        Rule with { RemotePort = ["1234"] }, Rule with { InterfaceType = "Wireless" },
        Rule with { InterfaceAlias = ["Ethernet"] }, Rule with { Authentication = "Required" },
        Rule with { Encryption = "Required" }, Rule with { LocalUser = "D:(A;;CC;;;WD)" },
        Rule with { RemoteUser = "specific user" }, Rule with { RemoteMachine = "specific machine" },
        Rule with { Platform = ["10.0"] }, Rule with { PolicyAppId = "managed app" },
        Rule with { PrimaryStatus = "Error" }, Rule with { FiltersComplete = false },
        Rule with { EnforcementStatus = ["LocalFirewallRulesDisallowed"] }, Rule with { EnforcementStatus = [] },
        Rule with { Owner = "S-1-5-21-restricted-user" },
        Rule with { RemoteDynamicKeywordAddresses = ["dynamic id"] }, Rule with { LocalPort = ["RPC"] },
        Rule with { LocalPort = [] }, Rule with { Action = "NotConfigured" }
    };

    [Theory]
    [MemberData(nameof(Restrictions))]
    public void Restricted_or_unsupported_rules_cannot_claim_general_subnet_access(object value)
    {
        var rule = Assert.IsType<FirewallRuleSnapshot>(value);
        Assert.All(FirewallPolicy.Evaluate(Snapshot(rule), 8765, ProgramPath).Profiles, profile => Assert.Equal(FirewallStatus.Unknown, profile.Status));
    }

    [Fact]
    public void Unrelated_service_and_packaged_app_rules_do_not_block_the_desktop_host()
    {
        Assert.True(FirewallPolicy.Evaluate(Snapshot(Rule, Rule with { Action = "Block", Service = "Dnscache" },
            Rule with { Action = "Block", Package = "PackageId" }), 8765, ProgramPath).IsAllowed);
    }

    [Theory]
    [InlineData("False", "True", "True", "Block", FirewallStatus.Disabled)]
    [InlineData("True", "False", "True", "Block", FirewallStatus.Blocked)]
    [InlineData("True", "True", "False", "Block", FirewallStatus.Blocked)]
    [InlineData("True", "True", "True", "Allow", FirewallStatus.Allowed)]
    [InlineData("NotConfigured", "True", "True", "Block", FirewallStatus.Unknown)]
    [InlineData("True", "True", "True", "Other", FirewallStatus.Unknown)]
    public void Effective_profile_settings_are_respected(string enabled, string inbound, string local, string action, object expected)
    {
        var snapshot = Snapshot(Rule) with
        {
            Profiles = [new("Private") { Enabled = enabled, AllowInboundRules = inbound, AllowLocalFirewallRules = local, DefaultInboundAction = action }]
        };
        Assert.Equal((FirewallStatus)expected, FirewallPolicy.Evaluate(snapshot, 8765, ProgramPath).Profiles[0].Status);
    }

    [Fact]
    public void Group_policy_allow_still_works_when_local_rule_merging_is_disabled()
    {
        var snapshot = Snapshot(Rule with { PolicyStoreSourceType = "GroupPolicy" }) with
        { Profiles = FirewallPolicy.ProfileNames.Select(name => new FirewallProfileSnapshot(name) { AllowLocalFirewallRules = "False" }).ToArray() };
        Assert.True(FirewallPolicy.Evaluate(snapshot, 8765, ProgramPath).IsAllowed);
    }

    [Fact]
    public void Incomplete_or_duplicate_profile_snapshot_remains_unknown()
    {
        Assert.All(FirewallPolicy.Evaluate(new(), 8765, ProgramPath).Profiles, profile => Assert.Equal(FirewallStatus.Unknown, profile.Status));
        var snapshot = Snapshot(Rule) with { Profiles = [new("Private"), new("Private")] };
        Assert.All(FirewallPolicy.Evaluate(snapshot, 8765, ProgramPath).Profiles, profile => Assert.Equal(FirewallStatus.Unknown, profile.Status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void Invalid_ports_are_rejected(int port) => Assert.Throws<ArgumentOutOfRangeException>(() => FirewallPolicy.Evaluate(Snapshot(), port, ProgramPath));
}
