using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.App;

internal sealed class PresenterForm : Form
{
    private readonly IPresenterSettingsService _settingsService;
    private readonly IMediaFolderService _mediaFolderService;
    private readonly IFfprobeService _ffprobeService;
    private readonly IMonitorService _monitorService;
    private readonly StartupRegistrationService _startupRegistration;
    private readonly PlaybackController _playback;
    private readonly PlaybackOrchestrator _playbackOrchestrator;
    private readonly IBrowserController _browser;
    private readonly IWindowsFirewallService _firewall;
    private readonly IApplicationUpdateService _updates;
    private readonly ApplicationLifetime _lifetime;
    private readonly Label _updateStatus = new() { AutoSize = true, MaximumSize = new Size(430, 0) };
    private readonly CheckBox _automaticUpdates = new() { AutoSize = true, Text = AppText.Get("Automatische Updates") };
    private readonly ToolStripMenuItem _trayUpdateStatus = new() { Enabled = false };
    private bool _refreshingUpdates;
    private string? _notifiedUpdate;
    private readonly string _updateNotificationPath;
    private readonly PresenterHostSettings _runningSettings;
    private readonly Label _version = new() { AutoSize = true };
    private readonly Label _build = new() { AutoSize = true };
    private readonly Label _commit = new() { AutoSize = true };
    private readonly Label _runtime = new() { AutoSize = true };
    private readonly Label _presenterStatus = new() { AutoSize = true };
    private readonly Label _chromeStatus = new() { AutoSize = true };
    private readonly ComboBox _monitor = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _monitorStatus = new() { AutoSize = true };
    private readonly TextBox _chromePath = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _alwaysOnTop = new() { AutoSize = true, Checked = true, Text = AppText.Get("Always On Top") };
    private readonly CheckBox _aggressiveTopmost = new() { AutoSize = true, Text = AppText.Get("Aggressive Topmost") };
    private readonly CheckBox _preventDisplaySleep = new() { AutoSize = true, Checked = true, Text = AppText.Get("Bildschirmabschaltung verhindern") };
    private readonly CheckBox _preventSystemSleep = new() { AutoSize = true, Checked = true, Text = AppText.Get("Windows-Standby verhindern") };
    private readonly CheckBox _startWithWindows = new() { AutoSize = true, Text = AppText.Get("Mit Windows starten") };
    private readonly ListBox _mediaFolders = new() { Dock = DockStyle.Fill, Height = 90 };
    private readonly CheckBox _includeSubdirectories = new() { AutoSize = true, Checked = true, Text = AppText.Get("Unterverzeichnisse durchsuchen") };
    private readonly TextBox _webPort = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _language = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _allowLanAccess = new() { AutoSize = true, Text = AppText.Get("Web UI im LAN freigeben") };
    private readonly Label _firewallStatus = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private readonly Label _firewallHint = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private readonly Button _checkFirewall = new() { AutoSize = true, Enabled = false, Text = AppText.Get("Erneut prüfen") };
    private readonly Button _allowFirewall = new() { AutoSize = true, Enabled = false, Text = AppText.Get("In Firewall freigeben") };
    private readonly Button _saveSettings = new() { AutoSize = true, Text = AppText.Get("Einstellungen speichern"), Anchor = AnchorStyles.Left };
    private readonly TextBox _ffprobePath = new() { Dock = DockStyle.Fill };
    private readonly Label _ffprobeStatus = new() { AutoSize = true };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _passwordRepeat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _webUrl = new() { AutoSize = true };
    private readonly Label _passwordStatus = new() { AutoSize = true };
    private readonly ToolStripMenuItem _trayStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem _activatePresenter = new() { Text = AppText.Get("Presenter aktivieren") };
    private readonly ToolStripMenuItem _pausePresenter = new() { Text = AppText.Get("Presenter pausieren") };
    private readonly ToolStripMenuItem _hidePresenter = new() { Text = AppText.Get("Presenter ausblenden") };
    private readonly ToolStripMenuItem _stopPresenter = new() { Text = AppText.Get("Presenter stoppen") };
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _firewallTimer = new() { Interval = 500 };
    private CancellationTokenSource? _firewallCancellation;
    private FirewallCheckResult? _firewallResult;
    private int _firewallRevision;
    private int _savedWebPort;
    private bool _savedAllowLanAccess;
    private bool _loadingSettings;
    private bool _firewallActionInProgress;
    private bool _allowExit;
    private bool _configuredMonitorMissing;
    private bool _statusRefreshInProgress;
    private string _localWebUrl = new UriBuilder(Uri.UriSchemeHttp, "localhost", 8765).Uri.AbsoluteUri;

    public PresenterForm(WebApplication host, bool startMinimized = false)
    {
        _settingsService = host.Services.GetRequiredService<IPresenterSettingsService>();
        _mediaFolderService = host.Services.GetRequiredService<IMediaFolderService>();
        _ffprobeService = host.Services.GetRequiredService<IFfprobeService>();
        _monitorService = host.Services.GetRequiredService<IMonitorService>();
        _startupRegistration = host.Services.GetRequiredService<StartupRegistrationService>();
        _playback = host.Services.GetRequiredService<PlaybackController>();
        _playbackOrchestrator = host.Services.GetRequiredService<PlaybackOrchestrator>();
        _browser = host.Services.GetRequiredService<IBrowserController>();
        _firewall = host.Services.GetRequiredService<IWindowsFirewallService>();
        _updates = host.Services.GetRequiredService<IApplicationUpdateService>();
        _lifetime = host.Services.GetRequiredService<ApplicationLifetime>();
        _updateNotificationPath = Path.Combine(host.Services.GetRequiredService<PresenterPaths>().RootDirectory, "update-notification.json");
        _runningSettings = host.Services.GetRequiredService<PresenterHostSettings>();
        var buildInformation = BuildInformation.Current;
        _version.Text = buildInformation.Version;
        _build.Text = buildInformation.BuildTimestampUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? AppText.Get("Nicht verfügbar");
        _commit.Text = buildInformation.ShortGitCommitSha;
        _runtime.Text = buildInformation.RuntimeVersion;
        Text = "Beamer Presenter for LAN-Parties"; MinimumSize = new Size(740, 800); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(CreateContent()); FormClosing += OnFormClosing; Shown += async (_, _) =>
        {
            await LoadSettingsAsync();
            _statusTimer.Start();
            if (startMinimized)
            {
                Hide();
            }
        };
        _activatePresenter.Click += async (_, _) => await ChangePresenterStateAsync(_playbackOrchestrator.ActivateAsync, requiresMonitor: true);
        _pausePresenter.Click += async (_, _) => await ChangePresenterStateAsync(_playbackOrchestrator.PauseAsync);
        _hidePresenter.Click += async (_, _) => await ChangePresenterStateAsync(_playbackOrchestrator.HideAsync);
        _stopPresenter.Click += async (_, _) => await ChangePresenterStateAsync(_playbackOrchestrator.StopAsync);
        _monitor.SelectedIndexChanged += (_, _) => UpdateSelectedMonitorStatus();
        _statusTimer.Tick += async (_, _) => await RefreshRuntimeStatusAsync();
        _automaticUpdates.CheckedChanged += async (_, _) =>
        {
            if (!_refreshingUpdates) await RunUpdateActionAsync(token => _updates.SetAutomaticAsync(_automaticUpdates.Checked, token));
        };
        _webPort.TextChanged += (_, _) => ScheduleFirewallCheck();
        _allowLanAccess.CheckedChanged += (_, _) => ScheduleFirewallCheck();
        _firewallTimer.Tick += async (_, _) => { _firewallTimer.Stop(); await RefreshFirewallAsync(); };
        _checkFirewall.Click += async (_, _) => await RefreshFirewallAsync();
        _allowFirewall.Click += async (_, _) => await AllowFirewallAsync();
        var menu = CreateTrayMenu();
        _notifyIcon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Beamer Presenter for LAN-Parties", Visible = true, ContextMenuStrip = menu };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        UpdatePresenterStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _firewallRevision++;
            _firewallCancellation?.Cancel();
            _firewallCancellation?.Dispose();
            _firewallTimer.Dispose();
            _statusTimer.Dispose();
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
    private TableLayoutPanel CreateContent()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 25, AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        AddRow(root, 0, AppText.Get("Version:"), _version); AddRow(root, 1, AppText.Get("Build:"), _build); AddRow(root, 2, AppText.Get("Commit:"), _commit); AddRow(root, 3, AppText.Get("Runtime:"), _runtime); AddRow(root, 4, AppText.Get("Presenter:"), _presenterStatus);
        AddRow(root, 5, AppText.Get("Chrome-Status:"), _chromeStatus); AddRow(root, 6, AppText.Get("Monitor:"), _monitor); AddRow(root, 7, AppText.Get("Monitorstatus:"), _monitorStatus); AddRow(root, 8, AppText.Get("Chrome-Pfad:"), CreateChromePathControl()); AddRow(root, 9, AppText.Get("Presenter-Optionen:"), CreatePresenterOptionsControl());
        AddRow(root, 10, AppText.Get("Autostart:"), _startWithWindows); AddRow(root, 11, AppText.Get("Web UI:"), _webUrl); AddRow(root, 12, AppText.Get("Web UI Port:"), _webPort); AddRow(root, 13, AppText.Get("Netzwerk:"), _allowLanAccess);
        AddRow(root, 14, AppText.Get("Windows-Firewall:"), CreateFirewallControl());
        AddRow(root, 15, AppText.Get("Videoordner:"), CreateMediaFolderControl()); AddRow(root, 16, AppText.Get("FFprobe-Pfad:"), CreateFfprobePathControl()); AddRow(root, 17, AppText.Get("FFprobe-Status:"), CreateFfprobeStatusControl()); AddRow(root, 18, AppText.Get("Web-Passwort:"), _password); AddRow(root, 19, AppText.Get("Passwort wiederholen:"), _passwordRepeat); AddRow(root, 20, AppText.Get("Schutzstatus:"), _passwordStatus);
        AddRow(root, 21, AppText.Get("Sprache:"), _language);
        _language.Items.AddRange([
            new LanguageListItem(null, AppText.Get("Systemstandard")),
            new LanguageListItem("de", AppText.Get("Deutsch")),
            new LanguageListItem("en", AppText.Get("Englisch")),
            new LanguageListItem("es", AppText.Get("Spanisch"))
        ]);
        _saveSettings.Click += async (_, _) => await SaveSettingsAsync(); root.Controls.Add(_saveSettings, 1, 22);
        root.Controls.Add(new Label { AutoSize = true, Text = AppText.Get("Port-, Netzwerk- und Sprachänderungen gelten nach einem Neustart.") }, 1, 23);
        AddRow(root, 24, AppText.Get("Updates:"), CreateUpdateControl()); return root;
    }
    private FlowLayoutPanel CreateUpdateControl()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(_updateStatus);
        panel.Controls.Add(_automaticUpdates);
        foreach (var (label, action) in new (string, Func<CancellationToken, Task>)[] {
            ("Nach Updates suchen", _updates.CheckAsync), ("Sofort", _updates.InstallAsync), ("1 Stunde später", _updates.PostponeAsync) })
        {
            var button = new Button { AutoSize = true, Text = AppText.Get(label) };
            button.Click += async (_, _) => await RunUpdateActionAsync(action);
            panel.Controls.Add(button);
        }
        return panel;
    }
    private async Task RunUpdateActionAsync(Func<CancellationToken, Task> action)
    {
        if (_lifetime.IsStopping) return;
        try { await action(CancellationToken.None); }
        catch (Exception) { if (!IsDisposed) _updateStatus.Text = AppText.Get("Die Update-Aktion ist fehlgeschlagen."); }
        if (!IsDisposed) RefreshUpdateStatus();
    }
    private void RefreshUpdateStatus()
    {
        var update = _updates.Current;
        var text = AppText.Format("Installiert: {0} · Verfügbar: {1}", update.InstalledVersion, update.AvailableVersion ?? "–") +
            "\n" + AppText.Get(update.Phase) + (update.Phase == "Downloading" ? $" {update.Progress}%" : string.Empty);
        if (update.InstallAtUtc is { } deadline)
            text += "\n" + AppText.Format("Installation in {0} Sekunden", Math.Max(0, (int)(deadline - DateTimeOffset.UtcNow).TotalSeconds));
        if (update.Error is not null) text += "\n" + AppText.Get(update.Error);
        _updateStatus.Text = text;
        _trayUpdateStatus.Text = text.Replace('\n', ' ');
        _refreshingUpdates = true; _automaticUpdates.Checked = update.AutomaticUpdatesEnabled; _refreshingUpdates = false;
        _automaticUpdates.Enabled = update.Phase != "Installing";
        if (update.Phase == "Ready" && update.AvailableVersion != _notifiedUpdate)
        {
            _notifiedUpdate = update.AvailableVersion;
            _ = SaveUpdateNotificationAsync(update.AvailableVersion!);
            _notifyIcon.ShowBalloonTip(10000, AppText.Get("Update verfügbar"), text, ToolTipIcon.Info);
        }
    }
    private async Task SaveUpdateNotificationAsync(string version)
    {
        try { await UpdateJson.WriteAsync(_updateNotificationPath, version); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private FlowLayoutPanel CreateFirewallControl()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.Add(_checkFirewall);
        actions.Controls.Add(_allowFirewall);
        panel.Controls.Add(_firewallStatus);
        panel.Controls.Add(actions);
        panel.Controls.Add(_firewallHint);
        return panel;
    }
    private static void AddRow(TableLayoutPanel panel, int row, string label, Control input) { panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); panel.Controls.Add(input, 1, row); }
    private TableLayoutPanel CreateMediaFolderControl()
    {
        var add = new Button { Text = AppText.Get("Hinzufügen"), AutoSize = true };
        add.Click += async (_, _) => await AddMediaFolderAsync();
        var remove = new Button { Text = AppText.Get("Entfernen"), AutoSize = true };
        remove.Click += async (_, _) => await RemoveMediaFolderAsync();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.Add(add);
        actions.Controls.Add(remove);
        actions.Controls.Add(_includeSubdirectories);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, ColumnCount = 1 };
        panel.Controls.Add(_mediaFolders, 0, 0);
        panel.Controls.Add(actions, 0, 1);
        return panel;
    }
    private TableLayoutPanel CreateFfprobePathControl()
    {
        var select = new Button { Text = AppText.Get("Auswählen"), AutoSize = true };
        select.Click += (_, _) => SelectFfprobePath();
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(_ffprobePath, 0, 0);
        panel.Controls.Add(select, 1, 0);
        return panel;
    }
    private TableLayoutPanel CreateChromePathControl()
    {
        var select = new Button { Text = AppText.Get("Auswählen"), AutoSize = true };
        select.Click += (_, _) => SelectChromePath();
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(_chromePath, 0, 0);
        panel.Controls.Add(select, 1, 0);
        return panel;
    }
    private FlowLayoutPanel CreatePresenterOptionsControl()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(_alwaysOnTop);
        panel.Controls.Add(_aggressiveTopmost);
        panel.Controls.Add(_preventDisplaySleep);
        panel.Controls.Add(_preventSystemSleep);
        return panel;
    }
    private FlowLayoutPanel CreateFfprobeStatusControl()
    {
        var check = new Button { Text = AppText.Get("Erneut prüfen"), AutoSize = true };
        check.Click += async (_, _) => await RefreshFfprobeStatusAsync();
        var install = new Button { Text = AppText.Get("FFmpeg installieren"), AutoSize = true };
        install.Click += async (_, _) => await InstallFfprobeAsync();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        panel.Controls.Add(_ffprobeStatus);
        panel.Controls.Add(check);
        panel.Controls.Add(install);
        return panel;
    }
    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_trayStatus);
        menu.Items.Add(_trayUpdateStatus);
        menu.Items.Add(AppText.Get("Nach Updates suchen"), null, async (_, _) => await RunUpdateActionAsync(_updates.CheckAsync));
        menu.Items.Add(AppText.Get("Sofort aktualisieren"), null, async (_, _) => await RunUpdateActionAsync(_updates.InstallAsync));
        menu.Items.Add(AppText.Get("1 Stunde später"), null, async (_, _) => await RunUpdateActionAsync(_updates.PostponeAsync));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_activatePresenter);
        menu.Items.Add(_pausePresenter);
        menu.Items.Add(_hidePresenter);
        menu.Items.Add(_stopPresenter);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(AppText.Get("Web UI öffnen"), null, (_, _) => OpenWebUi());
        menu.Items.Add(AppText.Get("Einstellungen"), null, (_, _) => ShowFromTray());
        menu.Items.Add(AppText.Get("Status"), null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(AppText.Get("Beenden"), null, (_, _) => ExitApplication());
        return menu;
    }
    private async Task ChangePresenterStateAsync(Func<CancellationToken, Task> command, bool requiresMonitor = false)
    {
        if (requiresMonitor && (_configuredMonitorMissing || _monitor.SelectedItem is null))
        {
            MessageBox.Show(this, AppText.Get("Der konfigurierte Monitor ist nicht verfügbar. Bitte einen Fallback-Monitor auswählen und die Einstellungen speichern."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            await command(CancellationToken.None);
            UpdatePresenterStatus();
            RefreshUpdateStatus();
            await RefreshRuntimeStatusAsync();
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Presenter state change failed");
            MessageBox.Show(this, AppText.Get("Der Presenter-Befehl ist fehlgeschlagen."), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void UpdatePresenterStatus()
    {
        _presenterStatus.Text = AppText.Get(_playback.State.ToString());
        _trayStatus.Text = AppText.Format("● Presenter {0}", AppText.Get(_playback.State.ToString()));
        _activatePresenter.Enabled = _playback.State != BeamerPresenter.Domain.PresenterState.Active;
        _pausePresenter.Enabled = _playback.State == BeamerPresenter.Domain.PresenterState.Active;
        _hidePresenter.Enabled = _playback.State is BeamerPresenter.Domain.PresenterState.Active or BeamerPresenter.Domain.PresenterState.Paused;
        _stopPresenter.Enabled = _playback.State != BeamerPresenter.Domain.PresenterState.Stopped;
    }
    private async Task LoadSettingsAsync()
    {
        try { _notifiedUpdate = await UpdateJson.ReadAsync<string>(_updateNotificationPath); }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        var settings = await _settingsService.GetAsync();
        _loadingSettings = true;
        _savedWebPort = settings.WebPort;
        _savedAllowLanAccess = settings.AllowLanAccess;
        _startWithWindows.Checked = _startupRegistration.IsEnabled();
        _webPort.Text = settings.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _allowLanAccess.Checked = settings.AllowLanAccess;
        _language.SelectedItem = _language.Items.Cast<LanguageListItem>()
            .FirstOrDefault(item => item.Code == settings.LanguagePreference) ?? _language.Items[0];
        _ffprobePath.Text = settings.FfprobePath ?? string.Empty;
        _chromePath.Text = settings.ChromePath ?? string.Empty;
        _alwaysOnTop.Checked = settings.AlwaysOnTop;
        _aggressiveTopmost.Checked = settings.AggressiveTopmost;
        _preventDisplaySleep.Checked = settings.PreventDisplaySleep;
        _preventSystemSleep.Checked = settings.PreventSystemSleep;
        _localWebUrl = $"http://localhost:{settings.WebPort}";
        _webUrl.Text = PresenterNetworkAddresses.FormatWebUrls(settings.WebPort, settings.AllowLanAccess, PresenterNetworkAddresses.GetLanIpv4Addresses());
        _passwordStatus.Text = string.IsNullOrWhiteSpace(settings.PasswordHash) ? AppText.Get("Noch nicht eingerichtet") : AppText.Get("Aktiv");
        LoadMonitors(settings.MonitorDeviceName);
        _loadingSettings = false;
        ScheduleFirewallCheck();
        await LoadMediaFoldersAsync();
        await RefreshFfprobeStatusAsync();
        await RefreshRuntimeStatusAsync();
    }
    private async Task LoadMediaFoldersAsync()
    {
        var folders = await _mediaFolderService.GetAllAsync();
        _mediaFolders.Items.Clear();
        _mediaFolders.Items.AddRange(folders.Select(folder => new MediaFolderListItem(folder.Id, folder.Path, folder.IncludeSubdirectories)).ToArray());
    }
    private async Task AddMediaFolderAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = AppText.Get("Videoordner auswählen"), UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await _mediaFolderService.AddAsync(dialog.SelectedPath, _includeSubdirectories.Checked);
        await LoadMediaFoldersAsync();
    }
    private async Task RemoveMediaFolderAsync()
    {
        if (_mediaFolders.SelectedItem is not MediaFolderListItem selectedFolder)
        {
            return;
        }

        await _mediaFolderService.RemoveAsync(selectedFolder.Id);
        await LoadMediaFoldersAsync();
    }
    private void SelectFfprobePath()
    {
        using var dialog = new OpenFileDialog { Filter = $"FFprobe|ffprobe.exe|{AppText.Get("Programme")}|*.exe", CheckFileExists = true, Multiselect = false, Title = AppText.Get("FFprobe auswählen") };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ffprobePath.Text = dialog.FileName;
        }
    }
    private void SelectChromePath()
    {
        using var dialog = new OpenFileDialog { Filter = $"Google Chrome|chrome.exe|{AppText.Get("Programme")}|*.exe", CheckFileExists = true, Multiselect = false, Title = AppText.Get("Chrome auswählen") };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _chromePath.Text = dialog.FileName;
        }
    }
    private void LoadMonitors(string? configuredDeviceName)
    {
        _monitor.Items.Clear();
        var monitors = _monitorService.GetAll().Select(DisplayMonitorListItem.From).ToArray();
        _monitor.Items.AddRange(monitors);
        var configuredMonitor = monitors.FirstOrDefault(item => string.Equals(item.DeviceName, configuredDeviceName, StringComparison.OrdinalIgnoreCase));
        _configuredMonitorMissing = !string.IsNullOrWhiteSpace(configuredDeviceName) && configuredMonitor is null;
        if (configuredMonitor is not null)
        {
            _monitor.SelectedItem = configuredMonitor;
        }
        else if (!_configuredMonitorMissing)
        {
            _monitor.SelectedItem = monitors.FirstOrDefault(item => item.IsPrimary) ?? monitors.FirstOrDefault();
        }

        if (_configuredMonitorMissing)
        {
            _monitorStatus.Text = AppText.Format("Nicht verfügbar: {0}", configuredDeviceName);
        }
        else if (monitors.Length == 0)
        {
            _monitorStatus.Text = AppText.Get("Keine Anzeige erkannt");
        }
        else
        {
            _monitorStatus.Text = AppText.Get("Verfügbar");
        }
    }
    private void UpdateSelectedMonitorStatus()
    {
        if (_configuredMonitorMissing && _monitor.SelectedItem is DisplayMonitorListItem)
        {
            _monitorStatus.Text = AppText.Get("Fallback ausgewählt – bitte speichern");
        }
        else if (_monitor.SelectedItem is DisplayMonitorListItem)
        {
            _monitorStatus.Text = AppText.Get("Verfügbar");
        }
    }
    private async Task RefreshFfprobeStatusAsync()
    {
        _ffprobeStatus.Text = AppText.Get("Wird geprüft …");
        var availability = await _ffprobeService.CheckAvailabilityAsync();
        if (!availability.IsAvailable)
        {
            Serilog.Log.Warning("FFprobe unavailable: {Error}", availability.Error);
        }
        _ffprobeStatus.Text = availability.IsAvailable
            ? $"✓ {availability.Version}"
            : AppText.Get("✗ FFprobe ist nicht verfügbar. Details stehen im Log.");
    }
    private async Task InstallFfprobeAsync()
    {
        _ffprobeStatus.Text = AppText.Get("Installation läuft …");
        var availability = await _ffprobeService.InstallWithWinGetAsync();
        if (!availability.IsAvailable)
        {
            Serilog.Log.Warning("FFprobe installation failed: {Error}", availability.Error);
        }
        _ffprobeStatus.Text = availability.IsAvailable
            ? $"✓ {availability.Version}"
            : AppText.Get("✗ FFprobe ist nicht verfügbar. Details stehen im Log.");
    }
    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(_webPort.Text, out var webPort) || webPort is < 1024 or > 65535) { MessageBox.Show(this, AppText.Get("Bitte einen Port zwischen 1024 und 65535 angeben."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!string.IsNullOrWhiteSpace(_password.Text) && _password.Text != _passwordRepeat.Text) { MessageBox.Show(this, AppText.Get("Die Passwörter stimmen nicht überein."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try { _startupRegistration.SetEnabled(_startWithWindows.Checked); } catch (UnauthorizedAccessException) { MessageBox.Show(this, AppText.Get("Der Windows-Autostart konnte nicht geändert werden."), Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (_monitor.SelectedItem is not DisplayMonitorListItem selectedMonitor) { MessageBox.Show(this, AppText.Get("Bitte einen verfügbaren Monitor auswählen."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var settings = await _settingsService.GetAsync(); settings.WebPort = webPort; settings.AllowLanAccess = _allowLanAccess.Checked; settings.LanguagePreference = (_language.SelectedItem as LanguageListItem)?.Code; settings.FfprobePath = string.IsNullOrWhiteSpace(_ffprobePath.Text) ? null : Path.GetFullPath(_ffprobePath.Text.Trim()); settings.ChromePath = string.IsNullOrWhiteSpace(_chromePath.Text) ? null : Path.GetFullPath(_chromePath.Text.Trim()); settings.MonitorDeviceName = selectedMonitor.DeviceName; settings.AlwaysOnTop = _alwaysOnTop.Checked; settings.AggressiveTopmost = _aggressiveTopmost.Checked; settings.PreventDisplaySleep = _preventDisplaySleep.Checked; settings.PreventSystemSleep = _preventSystemSleep.Checked; await _settingsService.SaveAsync(settings); if (!string.IsNullOrWhiteSpace(_password.Text)) await _settingsService.SetWebPasswordAsync(_password.Text);
        _password.Clear(); _passwordRepeat.Clear(); await LoadSettingsAsync(); MessageBox.Show(this, AppText.Get("Einstellungen gespeichert."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private bool TryGetFirewallPort(out int port) => int.TryParse(_webPort.Text, out port) && port is >= 1024 and <= 65535;

    private void ScheduleFirewallCheck()
    {
        if (_loadingSettings || _firewallActionInProgress || IsDisposed) return;
        _firewallTimer.Stop();
        _firewallRevision++;
        _firewallCancellation?.Cancel();
        _firewallResult = null;
        UpdateFirewallHint();
        var valid = TryGetFirewallPort(out _);
        _checkFirewall.Enabled = valid && _allowLanAccess.Checked;
        _allowFirewall.Enabled = false;
        if (!valid) _firewallStatus.Text = AppText.Get("Bitte einen Port zwischen 1024 und 65535 angeben.");
        else if (!_allowLanAccess.Checked) _firewallStatus.Text = AppText.Get("LAN-Zugriff deaktiviert – keine Freigabe erforderlich.");
        else { _firewallStatus.Text = AppText.Get("Wird geprüft …"); _firewallTimer.Start(); }
    }

    private void UpdateFirewallHint()
    {
        if (!TryGetFirewallPort(out var port)) { _firewallHint.Text = string.Empty; return; }
        var hint = AppText.Format("Prüfung für TCP-Port {0}. Freigabe: alle Profile, nur diese Anwendung und das lokale Subnetz. Administratorrechte erforderlich.", port);
        if (port != _savedWebPort || _allowLanAccess.Checked != _savedAllowLanAccess)
            hint += Environment.NewLine + AppText.Get("Geänderte Einstellungen zuerst speichern und die Anwendung neu starten.");
        else if (port != _runningSettings.WebPort || _allowLanAccess.Checked != _runningSettings.AllowLanAccess)
            hint += Environment.NewLine + AppText.Get("Gespeicherte Netzwerkänderungen gelten nach einem Neustart.");
        _firewallHint.Text = hint;
    }

    private async Task RefreshFirewallAsync()
    {
        _firewallTimer.Stop();
        if (_firewallActionInProgress || !TryGetFirewallPort(out var port) || !_allowLanAccess.Checked) return;
        _firewallCancellation?.Cancel();
        _firewallCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _firewallCancellation = cancellation;
        var revision = ++_firewallRevision;
        _checkFirewall.Enabled = _allowFirewall.Enabled = false;
        _firewallStatus.Text = AppText.Get("Wird geprüft …");
        try
        {
            var result = await _firewall.CheckAsync(port, cancellation.Token);
            if (!IsDisposed && revision == _firewallRevision) DisplayFirewallResult(result);
        }
        catch (OperationCanceledException) { }
    }

    private void DisplayFirewallResult(FirewallCheckResult result)
    {
        _firewallResult = result;
        _firewallStatus.Text = string.Join(Environment.NewLine, result.Profiles.Select(profile =>
            $"{AppText.Get(profile.Profile)}: {AppText.Get(profile.Status switch
            {
                FirewallStatus.Allowed => "Freigegeben",
                FirewallStatus.Missing => "Keine passende Freigabe",
                FirewallStatus.Blocked => "Blockiert (Regel oder Richtlinie)",
                FirewallStatus.Disabled => "Firewall deaktiviert",
                _ => "Nicht eindeutig prüfbar"
            })}"));
        _checkFirewall.Enabled = TryGetFirewallPort(out _) && _allowLanAccess.Checked;
        _allowFirewall.Enabled = _checkFirewall.Enabled && result.NeedsRule;
    }

    private async Task AllowFirewallAsync()
    {
        if (_firewallActionInProgress || !TryGetFirewallPort(out var port) || !_allowLanAccess.Checked || _firewallResult?.NeedsRule != true) return;
        _firewallActionInProgress = true;
        _firewallTimer.Stop();
        _firewallRevision++;
        _firewallCancellation?.Cancel();
        _checkFirewall.Enabled = _allowFirewall.Enabled = _webPort.Enabled = _allowLanAccess.Enabled = _saveSettings.Enabled = false;
        _firewallStatus.Text = AppText.Get("Firewall-Freigabe läuft …");
        try
        {
            var result = await _firewall.AllowAsync(port);
            if (IsDisposed) return;
            DisplayFirewallResult(result.Check);
            var message = result.Status switch
            {
                FirewallOpenStatus.Allowed => "Der Web-Port ist in der Windows-Firewall freigegeben.",
                FirewallOpenStatus.Cancelled => "Firewall-Freigabe abgebrochen.",
                FirewallOpenStatus.NotConfirmed => "Die Regel wurde gespeichert, die Freigabe ist jedoch nicht bestätigt. Bitte Regeln und Richtlinien in der Windows-Firewall prüfen.",
                _ => "Die Firewall-Regel konnte nicht geändert werden. Bitte Administratorrechte und Windows-Richtlinien prüfen."
            };
            MessageBox.Show(this, AppText.Get(message), Text, MessageBoxButtons.OK,
                result.Status == FirewallOpenStatus.Allowed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            _firewallActionInProgress = false;
            if (!IsDisposed) { _webPort.Enabled = _allowLanAccess.Enabled = _saveSettings.Enabled = true; UpdateFirewallHint(); }
        }
    }
    private async Task RefreshRuntimeStatusAsync()
    {
        if (_statusRefreshInProgress || IsDisposed)
        {
            return;
        }

        _statusRefreshInProgress = true;
        try
        {
            _chromeStatus.Text = await _browser.IsRunningAsync() ? AppText.Get("Läuft") : AppText.Get("Gestoppt");
            UpdatePresenterStatus();
        }
        catch (Exception)
        {
            _chromeStatus.Text = AppText.Get("Status nicht verfügbar");
        }
        finally
        {
            _statusRefreshInProgress = false;
        }
    }
    private void OpenWebUi() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_localWebUrl) { UseShellExecute = true });
    private void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); if (_savedWebPort != 0) ScheduleFirewallCheck(); }
    internal void ShowFromExternalLaunch() => ShowFromTray();
    private void ExitApplication() { _statusTimer.Stop(); _lifetime.RequestShutdown(); }
    internal void CloseAfterShutdown() { _allowExit = true; Close(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (_lifetime is not null) _lifetime.ShowStatusWindow = Visible; }
    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowExit) return;
        if (eventArgs.CloseReason == CloseReason.UserClosing) { eventArgs.Cancel = true; Hide(); }
        else { eventArgs.Cancel = true; ExitApplication(); }
    }

    private sealed record MediaFolderListItem(int Id, string Path, bool IncludeSubdirectories)
    {
        public override string ToString() => IncludeSubdirectories ? AppText.Format("{0} (inkl. Unterordner)", Path) : Path;
    }

    private sealed record LanguageListItem(string? Code, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DisplayMonitorListItem(string DeviceName, string FriendlyName, int X, int Y, int Width, int Height, bool IsPrimary)
    {
        public static DisplayMonitorListItem From(DisplayMonitor monitor) =>
            new(monitor.DeviceName, monitor.FriendlyName, monitor.X, monitor.Y, monitor.Width, monitor.Height, monitor.IsPrimary);

        public override string ToString() => $"{FriendlyName} / {DeviceName} — {Width} × {Height} @ {X}, {Y}{(IsPrimary ? AppText.Get(" (Primär)") : string.Empty)}";
    }
}
