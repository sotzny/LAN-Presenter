using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application.Tests;

public sealed class ApplicationUpdateTests
{
    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.20.3", true)]
    [InlineData("v0.2.3", false)]
    [InlineData("v1.2.3-beta", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("1.2.3+meta", false)]
    [InlineData(null, false)]
    public void Only_stable_versions_are_accepted(string? text, bool valid) => Assert.Equal(valid, StableReleaseVersion.Parse(text) is not null);

    [Fact]
    public async Task Download_countdown_postpone_disable_and_reenable_share_one_state()
    {
        var fixture = new Fixture();
        await fixture.Service.CheckAsync();
        Assert.Equal("Ready", fixture.Service.Current.Phase);
        Assert.Equal(100, fixture.Service.Current.Progress);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Service.Current.InstallAtUtc);
        await fixture.Service.PostponeAsync();
        var postponed = fixture.Clock.GetUtcNow().AddHours(1);
        Assert.Equal(postponed, fixture.Service.Current.InstallAtUtc);
        await fixture.Service.CheckAsync();
        Assert.Equal(postponed, fixture.Service.Current.InstallAtUtc);
        Assert.Equal(1, fixture.Source.Downloads);
        var restarted = fixture.CreateCoordinator();
        await restarted.CheckAsync();
        Assert.Equal(postponed, restarted.Current.InstallAtUtc);
        await restarted.SetAutomaticAsync(false);
        Assert.False(fixture.Settings.Value.AutomaticUpdatesEnabled);
        Assert.Null(restarted.Current.InstallAtUtc);
        fixture.Clock.Now = postponed.AddHours(1);
        await restarted.TickAsync();
        Assert.Equal(0, fixture.Installer.Starts);
        await restarted.SetAutomaticAsync(true);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(5), restarted.Current.InstallAtUtc);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(5);
        await restarted.TickAsync();
        Assert.Equal(1, fixture.Installer.Starts);
        Assert.Equal("Installing", restarted.Current.Phase);
        await restarted.TickAsync();
        await restarted.SetAutomaticAsync(false);
        await restarted.CheckAsync();
        Assert.Equal(1, fixture.Installer.Starts);
    }

    [Fact]
    public async Task Immediate_install_prepares_first_and_is_deduplicated()
    {
        var fixture = new Fixture();
        fixture.Settings.Value.AutomaticUpdatesEnabled = false;
        await Task.WhenAll(fixture.Service.InstallAsync(), fixture.Service.InstallAsync());
        Assert.Equal(1, fixture.Source.Downloads);
        Assert.Equal(1, fixture.Installer.Starts);
        Assert.NotNull(fixture.Store.State.FailedVersion); // crash protection is persisted before launch
        Assert.Null(fixture.Store.State.InstallAtUtc);
    }

    [Fact]
    public async Task Failed_install_is_persistent_and_only_retries_on_explicit_install()
    {
        var fixture = new Fixture();
        fixture.Installer.Fail = true;
        await fixture.Service.InstallAsync();
        Assert.Equal("Failed", fixture.Service.Current.Phase);
        Assert.NotNull(fixture.Service.Current.Error);
        var restarted = fixture.CreateCoordinator();
        await restarted.CheckAsync();
        await restarted.TickAsync();
        Assert.Equal(1, fixture.Installer.Starts);
        Assert.Equal("Failed", restarted.Current.Phase);
        fixture.Installer.Fail = false;
        await restarted.InstallAsync();
        Assert.Equal(2, fixture.Installer.Starts);
    }

    [Theory]
    [InlineData("1.1.1")]
    [InlineData("1.0.0")]
    [InlineData("1.2.0-beta")]
    public async Task Current_older_and_invalid_releases_do_not_install(string version)
    {
        var fixture = new Fixture(); fixture.Source.Version = version;
        await fixture.Service.CheckAsync(); await fixture.Service.InstallAsync(); await fixture.Service.TickAsync();
        Assert.Equal("Idle", fixture.Service.Current.Phase);
        Assert.Null(fixture.Service.Current.AvailableVersion);
        Assert.Equal(0, fixture.Source.Downloads); Assert.Equal(0, fixture.Installer.Starts);
    }

    [Fact]
    public async Task Offline_or_unverifiable_download_removes_countdown_and_keeps_app_running()
    {
        var fixture = new Fixture();
        await fixture.Service.CheckAsync();
        fixture.Source.Fail = true;
        await fixture.Service.CheckAsync();
        Assert.Equal("Failed", fixture.Service.Current.Phase);
        Assert.Null(fixture.Service.Current.InstallAtUtc);
        await fixture.Service.TickAsync();
        Assert.Equal(0, fixture.Installer.Starts);
        fixture.Source.Fail = false; fixture.Source.Valid = false;
        await fixture.Service.CheckAsync();
        Assert.Equal(2, fixture.Source.Downloads);
        await fixture.Service.InstallAsync();
        Assert.Equal("Failed", fixture.Service.Current.Phase);
        Assert.Equal(0, fixture.Installer.Starts);
    }

    [Fact]
    public async Task Cancellation_does_not_become_an_installation_error()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CheckAsync(cancellation.Token));
        Assert.Null(fixture.Service.Current.Error);
    }

    private sealed class Fixture
    {
        public readonly Clock Clock = new(); public readonly Settings Settings = new(); public readonly Source Source = new();
        public readonly Store Store = new(); public readonly Installer Installer = new();
        public ApplicationUpdateCoordinator Service { get; }
        public Fixture() => Service = CreateCoordinator();
        public ApplicationUpdateCoordinator CreateCoordinator() => new(Settings, Source, Store, Installer, Clock);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Settings : IPresenterSettingsService
    {
        public PresenterSettings Value = new();
        public Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(PresenterSettings settings, CancellationToken cancellationToken = default) { Value = settings; return Task.CompletedTask; }
        public Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
    private sealed class Store : IUpdateStateStore
    {
        public UpdatePersistentState State = new();
        public Task<UpdatePersistentState> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task WriteAsync(UpdatePersistentState state, CancellationToken cancellationToken) { State = state; return Task.CompletedTask; }
    }
    private sealed class Source : IApplicationReleaseSource
    {
        public int Downloads; public string Version = "1.2.0"; public bool Fail; public bool Valid = true;
        public Task<ApplicationRelease?> GetLatestAsync(CancellationToken token) => Fail ? throw new HttpRequestException() :
            Task.FromResult<ApplicationRelease?>(new(Version, new("setup", "", 1, ""), new("zip", "", 1, "")));
        public Task<PreparedUpdate> PrepareAsync(ApplicationRelease release, Action<int> progress, CancellationToken cancellationToken)
        { Downloads++; progress(50); progress(100); return Task.FromResult(new PreparedUpdate(release.Version, "package", false, "", 1)); }
        public Task<bool> IsPreparedAsync(PreparedUpdate prepared, CancellationToken cancellationToken) => Task.FromResult(Valid);
    }
    private sealed class Installer : IUpdateInstaller
    {
        public string InstalledVersion => "1.1.1"; public int Starts; public bool Fail;
        public Task LaunchAsync(PreparedUpdate update, CancellationToken cancellationToken)
        { Starts++; if (Fail) throw new IOException(); return Task.CompletedTask; }
    }
}
