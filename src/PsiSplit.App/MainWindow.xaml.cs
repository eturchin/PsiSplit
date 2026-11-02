using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PsiSplit.Core.Models;
using PsiSplit.Core.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PsiSplit.App;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush NeutralTint = new(Color.FromRgb(0x1B, 0x22, 0x30));
    private static readonly SolidColorBrush NeutralBorder = new(Color.FromRgb(0x2A, 0x32, 0x42));
    private static readonly SolidColorBrush GreenTint = new(Color.FromRgb(0x12, 0x26, 0x1E));
    private static readonly SolidColorBrush GreenBorder = new(Color.FromRgb(0x1F, 0x4A, 0x38));
    private static readonly SolidColorBrush AmberTint = new(Color.FromRgb(0x2A, 0x21, 0x13));
    private static readonly SolidColorBrush AmberBorder = new(Color.FromRgb(0x4A, 0x3A, 0x1B));
    private static readonly SolidColorBrush RedTint = new(Color.FromRgb(0x2A, 0x17, 0x18));
    private static readonly SolidColorBrush RedBorder = new(Color.FromRgb(0x4A, 0x26, 0x29));

    private readonly ConfigStore _store;
    private readonly bool _preview;
    private readonly AppConfig _config;
    private readonly LogService _log;
    private readonly SystemProxyManager _systemProxy;
    private readonly ShellLauncher _shellLauncher;
    private readonly AppLauncher _appLauncher;
    private readonly ObservableCollection<SiteRule> _sites;
    private readonly ObservableCollection<AppProfile> _apps;

    private DomainMatcher _matcher;
    private PsiphonController? _psiphon;
    private PacServer? _pacServer;
    private SmartProxyServer? _fullProxy;
    private SmartProxyServer? _filteredProxy;
    private DispatcherTimer? _watchdog;
    private DispatcherTimer? _metricsTimer;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayVpnItem;
    private Drawing.Icon? _trayIcon;
    private bool _siteMode;
    private bool _busy;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow() : this(new ConfigStore()) { }

    public MainWindow(ConfigStore store, bool preview = false)
    {
        InitializeComponent();

        SourceInitialized += (_, _) => WindowTheme.Apply(this);

        _store = store;
        _preview = preview;
        _config = _store.Load();
        _log = new LogService(_store.LogDirectory, AppendLog);
        _systemProxy = new SystemProxyManager(_store, _log);
        _shellLauncher = new ShellLauncher(_log);
        _appLauncher = new AppLauncher(_log);
        _matcher = new DomainMatcher(_config.Sites);
        _sites = new ObservableCollection<SiteRule>(_config.Sites);
        _apps = new ObservableCollection<AppProfile>(_config.Apps);

        SitesList.ItemsSource = _sites;
        AppsList.ItemsSource = _apps;

        LoadSettingsUi();
        if (!_preview)
        {
            CreateTrayIcon();
            BuildController();
        }
        UpdateAllUi();

        Loaded += MainWindow_Loaded;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_preview)
        {
            // Template-generated row buttons may appear after Loaded. Permit
            // only tab navigation/scrolling, even when those controls materialize.
            PreviewMouseDown += (_, args) =>
            {
                var node = args.OriginalSource as DependencyObject;
                while (node is Visual)
                {
                    if (node is TabItem or ScrollBar) return;
                    node = VisualTreeHelper.GetParent(node);
                }
                args.Handled = true;
            };
            PreviewKeyDown += (_, args) => args.Handled = true;
            ClampToWorkArea();
            DisablePreviewActions(this);
            return;
        }

        try
        {
            _systemProxy.RecoverIfNeeded();
            LoadSettingsUi();

            StartLocalServers();

            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _watchdog.Tick += Watchdog_Tick;
            _watchdog.Start();

            _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _metricsTimer.Tick += (_, _) => UpdateTrafficUi();
            _metricsTimer.Start();

            ClampToWorkArea();

            _log.Info("PsiSplit ready. Local services started.");
            UpdateAllUi();

            if (_config.StartPsiphonOnLaunch)
            {
                await StartVpnAsync();
            }
        }
        catch (Exception exception)
        {
            _log.Error("Failed to start local services", exception);
            MessageBox.Show(
                this,
                exception.Message + Environment.NewLine + Environment.NewLine +
                "Check that the ports in settings are not in use, then restart PsiSplit.",
                "PsiSplit",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            UpdateAllUi();
        }
    }

    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_preview) return;
        if (_exiting)
        {
            return;
        }

        if (MinimizeTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        e.Cancel = true;
        await ExitAsync();
    }

    private static void DisablePreviewActions(DependencyObject parent)
    {
        if (parent is ButtonBase button) button.IsEnabled = false;
        if (parent is TextBox textBox) textBox.IsReadOnly = true;
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is DependencyObject dependencyObject) DisablePreviewActions(dependencyObject);
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && MinimizeTrayCheck.IsChecked == true)
        {
            HideToTray();
        }
    }

    private void ClampToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (Width > area.Width)
        {
            Width = area.Width;
        }

        if (Height > area.Height)
        {
            Height = area.Height;
        }

        if (Left + Width > area.Right)
        {
            Left = Math.Max(area.Left, area.Right - Width);
        }

        if (Top + Height > area.Bottom)
        {
            Top = Math.Max(area.Top, area.Bottom - Height);
        }
    }

    private void Watchdog_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_siteMode)
            {
                _systemProxy.EnsureStillApplied();
            }

            UpdateAllUi();
        }
        catch (Exception exception)
        {
            _log.Warn($"Watchdog: {exception.Message}");
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _watchdog?.Stop();
        _metricsTimer?.Stop();

        try
        {
            if (_siteMode)
            {
                DisableSiteMode();
            }
        }
        catch (Exception exception)
        {
            _log.Warn($"Restoring proxy on exit: {exception.Message}");
        }

        try
        {
            if (_psiphon is not null && _config.StopPsiphonOnExit && _psiphon.CanStop)
            {
                await _psiphon.StopAsync();
            }
            else if (_psiphon is { CanStop: false, State: PsiphonState.Connected })
            {
                _log.Info("Psiphon was started externally - leaving it running.");
            }
        }
        catch (Exception exception)
        {
            _log.Warn($"Stopping Psiphon on exit: {exception.Message}");
        }

        try
        {
            if (_psiphon is not null)
            {
                await _psiphon.DisposeAsync();
                _psiphon = null;
            }
        }
        catch (Exception exception)
        {
            _log.Warn($"Dispose Psiphon: {exception.Message}");
        }

        await StopLocalServersAsync();

        try
        {
            PersistConfig();
        }
        catch (Exception exception)
        {
            _log.Warn($"Saving configuration on exit: {exception.Message}");
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;

        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------
    // Local services
    // ------------------------------------------------------------------

    private void StartLocalServers()
    {
        var upstream = _psiphon?.State == PsiphonState.Connected
            ? _psiphon.HttpProxyPort
            : 0;

        if (_fullProxy is null)
        {
            _fullProxy = new SmartProxyServer(
                _config.FullProxyPort,
                TunnelMode.Full,
                upstream,
                _ => true,
                IsUpstreamReady,
                _log);
            _fullProxy.Start();
        }
        else
        {
            _fullProxy.UpstreamPort = upstream;
        }

        if (_filteredProxy is null)
        {
            _filteredProxy = new SmartProxyServer(
                _config.FilteredProxyPort,
                TunnelMode.Filtered,
                upstream,
                host => _matcher.IsMatch(host),
                IsUpstreamReady,
                _log);
            _filteredProxy.Start();
        }
        else
        {
            _filteredProxy.UpstreamPort = upstream;
        }

        if (_pacServer is null)
        {
            _pacServer = new PacServer(_config.PacPort, _log);
            _pacServer.UpdatePac(BuildPac());
            _pacServer.Start();
        }
        else
        {
            _pacServer.UpdatePac(BuildPac());
        }
    }

    private async Task StopLocalServersAsync()
    {
        if (_pacServer is not null)
        {
            await _pacServer.DisposeAsync();
            _pacServer = null;
        }

        if (_filteredProxy is not null)
        {
            await _filteredProxy.DisposeAsync();
            _filteredProxy = null;
        }

        if (_fullProxy is not null)
        {
            await _fullProxy.DisposeAsync();
            _fullProxy = null;
        }
    }

    private async Task RestartLocalServersAsync()
    {
        if (_siteMode)
        {
            DisableSiteMode();
        }

        await StopLocalServersAsync();
        StartLocalServers();
    }

    private string BuildPac()
        => PacGenerator.Generate(_sites, _config.FilteredProxyPort);

    private bool IsUpstreamReady()
        => _psiphon?.State == PsiphonState.Connected;

    // ------------------------------------------------------------------
    // Psiphon
    // ------------------------------------------------------------------

    private void BuildController()
    {
        if (_psiphon is not null)
        {
            _psiphon.StateChanged -= OnPsiphonStateChanged;
            var old = _psiphon;
            _ = old.DisposeAsync();
        }

        _psiphon = new PsiphonController(_log);
        _psiphon.StateChanged += OnPsiphonStateChanged;
    }

    private void OnPsiphonStateChanged(PsiphonState state)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (state == PsiphonState.Connected && _psiphon is not null)
            {
                var port = _psiphon.HttpProxyPort;
                if (_fullProxy is not null)
                {
                    _fullProxy.UpstreamPort = port;
                }

                if (_filteredProxy is not null)
                {
                    _filteredProxy.UpstreamPort = port;
                }

                if (_config.ApplyPacOnStart && !_siteMode)
                {
                    EnableSiteMode();
                }
            }
            else if (state == PsiphonState.Failed && _siteMode)
            {
                DisableSiteMode();
                _log.Warn("Site mode disabled: connection to Psiphon lost.");
            }

            UpdateAllUi();
        }));
    }

    private async Task StartVpnAsync()
    {
        if (_psiphon is null)
        {
            BuildController();
        }

        if (_psiphon is null || _busy)
        {
            return;
        }

        _busy = true;
        UpdateAllUi();

        try
        {
            await _psiphon.StartAsync();
            var port = _psiphon.HttpProxyPort;

            if (_fullProxy is not null)
            {
                _fullProxy.UpstreamPort = port;
            }

            if (_filteredProxy is not null)
            {
                _filteredProxy.UpstreamPort = port;
            }

            _log.Info($"VPN connected. Psiphon local HTTP proxy: 127.0.0.1:{port}.");

            if (_config.ApplyPacOnStart && !_siteMode)
            {
                EnableSiteMode();
            }
        }
        catch (Exception exception)
        {
            _log.Error("Failed to start VPN", exception);
            MessageBox.Show(
                this,
                DescribeVpnStartError(exception),
                "PsiSplit — VPN not started",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            UpdateAllUi();
        }
    }

    private static string DescribeVpnStartError(Exception exception)
        => exception switch
        {
            FileNotFoundException =>
                "The bundled tunnel engine is incomplete. Use a complete PsiSplit build.",
            TimeoutException =>
                "The tunnel did not connect in time. Check your internet connection and the PsiSplit log.",
            _ =>
                "Failed to start Psiphon. Details were written to the log."
        };

    private async Task StopVpnAsync()
    {
        if (_psiphon is null || _busy)
        {
            return;
        }

        if (_psiphon.State == PsiphonState.Connected && !_psiphon.CanStop)
        {
            MessageBox.Show(
                this,
                "Psiphon was started outside PsiSplit. Close it manually if you need to disconnect the VPN.",
                "PsiSplit",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _busy = true;
        UpdateAllUi();

        try
        {
            if (_siteMode)
            {
                DisableSiteMode();
            }

            await _psiphon.StopAsync();
            _log.Info("VPN stopped.");
        }
        catch (Exception exception)
        {
            _log.Error("Failed to stop VPN", exception);
        }
        finally
        {
            _busy = false;
            UpdateAllUi();
        }
    }

    private async Task ToggleVpnAsync()
    {
        var state = _psiphon?.State ?? PsiphonState.Stopped;
        if (state == PsiphonState.Connected)
        {
            await StopVpnAsync();
        }
        else
        {
            await StartVpnAsync();
        }
    }

    // ------------------------------------------------------------------
    // Site mode
    // ------------------------------------------------------------------

    private void ToggleSiteMode()
    {
        if (_siteMode)
        {
            DisableSiteMode();
            UpdateAllUi();
            return;
        }

        if (_psiphon?.State != PsiphonState.Connected)
        {
            MessageBox.Show(
                this,
                "Start the VPN first: without Psiphon, the listed sites will not open.",
                "PsiSplit",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        EnableSiteMode();
        UpdateAllUi();
    }

    private void EnableSiteMode()
    {
        if (_pacServer is null)
        {
            _log.Warn("PAC server is not running - Site mode cannot be enabled.");
            return;
        }

        ApplySiteModePac();
        _siteMode = true;
        _log.Info("Site mode enabled: only listed sites go through Psiphon, everything else goes direct.");
    }

    /// <summary>
    /// Re-applies the system PAC. The URL carries a version query so WinINet and
    /// Chrome fetch the new rule set instead of using a cached PAC file.
    /// </summary>
    private void ApplySiteModePac()
    {
        if (_pacServer is null)
        {
            return;
        }

        _pacServer.UpdatePac(BuildPac());
        var version = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        _systemProxy.ApplyPac(_pacServer.Url + "?v=" + version);
    }

    private void DisableSiteMode()
    {
        _systemProxy.Restore();
        _siteMode = false;
        _log.Info("Site mode disabled. System proxy restored.");
    }

    // ------------------------------------------------------------------
    // Quick launch
    // ------------------------------------------------------------------

    private void LaunchCmd_Click(object sender, RoutedEventArgs e)
    {
        var port = _config.TunnelShellsThroughSiteFilter
            ? _config.FilteredProxyPort
            : _config.FullProxyPort;

        if (_psiphon?.State != PsiphonState.Connected)
        {
            _log.Warn("VPN is not connected yet: CMD will open, but the proxy will work after the VPN starts.");
        }

        _shellLauncher.LaunchCmd(port);
    }

    private void LaunchPowerShell_Click(object sender, RoutedEventArgs e)
    {
        var port = _config.TunnelShellsThroughSiteFilter
            ? _config.FilteredProxyPort
            : _config.FullProxyPort;

        if (_psiphon?.State != PsiphonState.Connected)
        {
            _log.Warn("VPN is not connected yet: PowerShell will open, but the proxy will work after the VPN starts.");
        }

        _shellLauncher.LaunchPowerShell(port);
    }

    private void LaunchChrome_Click(object sender, RoutedEventArgs e)
    {
        if (_psiphon?.State != PsiphonState.Connected)
        {
            MessageBox.Show(
                this,
                "Start the VPN first.",
                "PsiSplit",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!_siteMode)
        {
            EnableSiteMode();
        }

        var browser = ExecutableLocator.FindChrome() ?? ExecutableLocator.FindEdge();
        if (browser is null)
        {
            MessageBox.Show(
                this,
                "Chrome or Edge was not found. Specify the browser manually on the Apps tab.",
                "PsiSplit",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = browser,
                UseShellExecute = true
            });
            _log.Info($"Browser launched: {Path.GetFileName(browser)} (Site mode).");
        }
        catch (Exception exception)
        {
            _log.Error("Failed to launch browser", exception);
            MessageBox.Show(this, exception.Message, "PsiSplit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateAllUi();
    }

    // ------------------------------------------------------------------
    // Sites
    // ------------------------------------------------------------------

    private void AddSite_Click(object sender, RoutedEventArgs e)
        => AddSiteFromBox();

    private void NewSiteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddSiteFromBox();
            e.Handled = true;
        }
    }

    private void NewSiteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SiteStatusText is not null)
        {
            SiteStatusText.Text = string.Empty;
        }
    }

    private void AddSiteFromBox()
    {
        var pattern = SiteRule.NormalizePattern(NewSiteBox.Text);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            SiteStatusText.Text = "Enter a domain, for example chatgpt.com.";
            NewSiteBox.Focus();
            return;
        }

        if (_sites.Any(rule => string.Equals(rule.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            SiteStatusText.Text = $"\"{pattern}\" is already in the list.";
            _log.Warn($"Domain is already in the list: {pattern}");
            NewSiteBox.SelectAll();
            NewSiteBox.Focus();
            return;
        }

        _sites.Add(new SiteRule { Pattern = pattern, Enabled = true });
        NewSiteBox.Clear();
        SiteStatusText.Text = $"Added: {pattern}";
        NewSiteBox.Focus();

        PersistConfig();
        _log.Info($"Site added: {pattern}");
        UpdateAllUi();
    }

    private void RemoveSite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SiteRule rule)
        {
            return;
        }

        _sites.Remove(rule);
        PersistConfig();
        _log.Info($"Site removed: {rule.Pattern}");
        UpdateAllUi();
    }

    private void SiteEnabled_Click(object sender, RoutedEventArgs e)
    {
        PersistConfig();
        UpdateAllUi();
    }

    // ------------------------------------------------------------------
    // Applications
    // ------------------------------------------------------------------

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var profile = new AppProfile();
        var editor = new AppEditorWindow(profile, isNew: true) { Owner = this };

        if (editor.ShowDialog() == true)
        {
            _apps.Add(profile);
            PersistConfig();
            _log.Info($"Application added: {profile.Name} ({profile.Mode.ToString().ToLowerInvariant()}).");
            UpdateAllUi();
        }
    }

    private void EditApp_Click(object sender, RoutedEventArgs e)
        => EditSelectedApp();

    private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => EditSelectedApp();

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateAppButtons();

    private void EditSelectedApp()
    {
        if (AppsList.SelectedItem is not AppProfile profile)
        {
            MessageBox.Show(this, "Select an app in the list.", "PsiSplit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = new AppEditorWindow(profile, isNew: false) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            CollectionViewSource.GetDefaultView(AppsList.ItemsSource)?.Refresh();
            PersistConfig();
            _log.Info($"Application updated: {profile.Name} ({profile.Mode.ToString().ToLowerInvariant()}).");
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppProfile profile)
        {
            return;
        }

        _apps.Remove(profile);
        PersistConfig();
        _log.Info($"Application removed: {profile.Name}.");
        UpdateAllUi();
    }

    private void LaunchApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppProfile profile)
        {
            MessageBox.Show(this, "Select an app in the list.", "PsiSplit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_psiphon?.State != PsiphonState.Connected)
        {
            MessageBox.Show(this, "Start the VPN first.", "PsiSplit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _appLauncher.Launch(profile, _config.FullProxyPort, _config.FilteredProxyPort);
    }

    // ------------------------------------------------------------------
    // Log / settings
    // ------------------------------------------------------------------

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
        => OpenFolder(_store.LogDirectory);

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolder(_store.RootDirectory);

    private void ClearLog_Click(object sender, RoutedEventArgs e)
        => LogBox.Clear();

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // Explorer is optional; ignore.
        }
    }

    private void LoadSettingsUi()
    {
        EngineVersionText.Text = PsiphonBundle.Version;
        FullPortBox.Text = _config.FullProxyPort.ToString(CultureInfo.InvariantCulture);
        FilteredPortBox.Text = _config.FilteredProxyPort.ToString(CultureInfo.InvariantCulture);
        PacPortBox.Text = _config.PacPort.ToString(CultureInfo.InvariantCulture);

        StartOnLaunchCheck.IsChecked = _config.StartPsiphonOnLaunch;
        ApplyPacCheck.IsChecked = _config.ApplyPacOnStart;
        StopOnExitCheck.IsChecked = _config.StopPsiphonOnExit;
        MinimizeTrayCheck.IsChecked = _config.MinimizeToTray;
        ShellsFilteredCheck.IsChecked = _config.TunnelShellsThroughSiteFilter;

    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParsePort(FullPortBox.Text, out var fullPort) ||
            !TryParsePort(FilteredPortBox.Text, out var filteredPort) ||
            !TryParsePort(PacPortBox.Text, out var pacPort))
        {
            MessageBox.Show(this, "Ports must be numbers from 1 to 65535.", "PsiSplit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (fullPort == filteredPort || fullPort == pacPort || filteredPort == pacPort)
        {
            MessageBox.Show(this, "Ports must not match.", "PsiSplit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var portsChanged = fullPort != _config.FullProxyPort ||
                           filteredPort != _config.FilteredProxyPort ||
                           pacPort != _config.PacPort;
        _config.FullProxyPort = fullPort;
        _config.FilteredProxyPort = filteredPort;
        _config.PacPort = pacPort;
        _config.StartPsiphonOnLaunch = StartOnLaunchCheck.IsChecked == true;
        _config.ApplyPacOnStart = ApplyPacCheck.IsChecked == true;
        _config.StopPsiphonOnExit = StopOnExitCheck.IsChecked == true;
        _config.MinimizeToTray = MinimizeTrayCheck.IsChecked == true;
        _config.TunnelShellsThroughSiteFilter = ShellsFilteredCheck.IsChecked == true;

        PersistConfig();
        SettingsStatusText.Text = "Settings saved.";

        if (portsChanged)
        {
            var state = _psiphon?.State ?? PsiphonState.Stopped;
            if (state is PsiphonState.Connected or PsiphonState.Starting or PsiphonState.Stopping)
            {
                SettingsStatusText.Text += " New ports will apply after the VPN stops and PsiSplit restarts.";
            }
            else
            {
                try
                {
                    await RestartLocalServersAsync();
                    SettingsStatusText.Text += " Local servers restarted.";
                    _log.Info("Local servers restarted with new ports.");
                }
                catch (Exception exception)
                {
                    _log.Error("Failed to restart local servers", exception);
                    SettingsStatusText.Text += " Failed to restart servers: " + exception.Message;
                }
            }
        }

        LoadSettingsUi();
        UpdateAllUi();
    }

    private void RestoreProxy_Click(object sender, RoutedEventArgs e)
    {
        DisableSiteMode();
        UpdateAllUi();
        SettingsStatusText.Text = "System proxy restored. Site mode is off.";
    }

    private static bool TryParsePort(string text, out int port)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) &&
           port is > 0 and <= 65535;

    private void PersistConfig()
    {
        _config.Sites = _sites.ToList();
        _config.Apps = _apps.ToList();

        try
        {
            _store.Save(_config);
        }
        catch (Exception exception)
        {
            _log.Error("Failed to save configuration", exception);
        }

        _matcher = new DomainMatcher(_config.Sites);

        if (_pacServer is not null)
        {
            if (_siteMode)
            {
                ApplySiteModePac();
            }
            else
            {
                _pacServer.UpdatePac(BuildPac());
            }
        }
    }

    // ------------------------------------------------------------------
    // UI state
    // ------------------------------------------------------------------

    private void UpdateAllUi()
    {
        var state = _psiphon?.State ?? PsiphonState.Stopped;

        VpnHeadline.Text = state switch
        {
            PsiphonState.Starting => "Connecting…",
            PsiphonState.Connected => "VPN connected",
            PsiphonState.Stopping => "Disconnecting…",
            PsiphonState.Failed => "VPN not connected",
            _ => "VPN off"
        };

        VpnDetail.Text = state switch
        {
            PsiphonState.Starting => "The built-in engine is establishing the tunnel. This usually takes 10–30 seconds.",
            PsiphonState.Connected when _psiphon?.CanStop == true =>
                "The tunnel is ready. The built-in Psiphon engine is running without a separate application window.",
            PsiphonState.Connected =>
                "The tunnel is ready. Psiphon was started externally — PsiSplit will not stop it.",
            PsiphonState.Stopping => "Stopping Psiphon and restoring the system proxy.",
            PsiphonState.Failed => "The connection was lost or could not be established. Check your internet connection and the log.",
            _ => "Psiphon is not running. System proxy settings are unchanged."
        };

        StartProgress.Visibility = state is PsiphonState.Starting or PsiphonState.Stopping
            ? Visibility.Visible
            : Visibility.Collapsed;

        var isConnected = state == PsiphonState.Connected;
        var isTransitioning = state is PsiphonState.Starting or PsiphonState.Stopping;

        StartButton.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !_busy && !isTransitioning;
        StartButton.Content = state == PsiphonState.Starting
            ? "Connecting…"
            : state == PsiphonState.Failed ? "Retry" : "Start VPN";
        StopButton.IsEnabled = !_busy && isConnected && (_psiphon?.CanStop ?? false);
        StopButton.ToolTip = isConnected && !(_psiphon?.CanStop ?? false)
            ? "Psiphon was started externally — stop it manually."
            : "Stop the VPN and restore the system proxy.";

        SiteModeButton.IsEnabled = !_busy && (_siteMode || isConnected);
        SiteModeButton.Content = _siteMode ? "Site mode: on" : "Site mode: off";
        SiteModeButton.ToolTip = _siteMode
            ? "Turn off Site mode and restore the system proxy."
            : isConnected
                ? "Enable PAC: only listed sites go through the VPN."
                : "Site mode is enabled after the VPN connects.";
        SiteModeApplyButton.IsEnabled = SiteModeButton.IsEnabled;
        SiteModeApplyButton.Content = _siteMode ? "Turn off Site mode" : "Enable Site mode";

        var accent = state switch
        {
            PsiphonState.Connected => (Brush)FindResource("GreenBrush"),
            PsiphonState.Starting or PsiphonState.Stopping => (Brush)FindResource("AmberBrush"),
            PsiphonState.Failed => (Brush)FindResource("RedBrush"),
            _ => (Brush)FindResource("MutedBrush")
        };

        StatusDot.Fill = accent;
        StatusText.Foreground = accent;
        StatusText.Text = "VPN: " + state switch
        {
            PsiphonState.Starting => "connecting",
            PsiphonState.Connected => "connected",
            PsiphonState.Stopping => "disconnecting",
            PsiphonState.Failed => "error",
            _ => "off"
        };

        (StatusPill.Background, StatusPill.BorderBrush) = state switch
        {
            PsiphonState.Connected => (GreenTint, GreenBorder),
            PsiphonState.Starting or PsiphonState.Stopping => (AmberTint, AmberBorder),
            PsiphonState.Failed => (RedTint, RedBorder),
            _ => (NeutralTint, NeutralBorder)
        };

        SiteModeStatusText.Text = _siteMode
            ? "Enabled: " + (_pacServer?.Url ?? "PAC server is not running")
            : "Disabled. Chrome and other browsers go direct.";

        PsiphonEndpointText.Text = _psiphon is null || _psiphon.HttpProxyPort == 0
            ? "—"
            : $"127.0.0.1:{_psiphon.HttpProxyPort} · {StateLabel(state)}";

        FullProxyText.Text = $"127.0.0.1:{_config.FullProxyPort} — all traffic";
        FilteredProxyText.Text = $"127.0.0.1:{_config.FilteredProxyPort} — listed sites only";
        PacText.Text = $"http://127.0.0.1:{_config.PacPort}/proxy.pac";

        SitesCountText.Text = $"{_sites.Count(rule => rule.Enabled)} of {_sites.Count}";
        AppsCountText.Text = _apps.Count.ToString(CultureInfo.InvariantCulture);

        SitesEmptyText.Visibility = _sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsEmptyText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAppButtons();
        UpdateTrafficUi();

        if (_trayVpnItem is not null)
        {
            _trayVpnItem.Text = state == PsiphonState.Connected ? "Stop VPN" : "Start VPN";
            _trayVpnItem.Enabled = !_busy && (state != PsiphonState.Connected || (_psiphon?.CanStop ?? false));
        }
    }

    private void UpdateAppButtons()
    {
        var hasSelection = AppsList.SelectedItem is AppProfile;
        EditAppButton.IsEnabled = hasSelection;
        RemoveAppButton.IsEnabled = hasSelection;
        LaunchAppButton.IsEnabled = hasSelection;
    }

    private void UpdateTrafficUi()
    {
        if (_fullProxy is not null)
        {
            var snapshot = _fullProxy.Metrics.Snapshot();
            FullTrafficText.Text = FormatTraffic(snapshot);
            FullConnectionsText.Text = FormatConnections(snapshot);
        }
        else
        {
            FullTrafficText.Text = "↑ 0 B · ↓ 0 B";
            FullConnectionsText.Text = "0/0 active · VPN 0 · direct 0";
        }

        if (_filteredProxy is not null)
        {
            var snapshot = _filteredProxy.Metrics.Snapshot();
            FilteredTrafficText.Text = FormatTraffic(snapshot);
            FilteredConnectionsText.Text = FormatConnections(snapshot);
        }
        else
        {
            FilteredTrafficText.Text = "↑ 0 B · ↓ 0 B";
            FilteredConnectionsText.Text = "0/0 active · VPN 0 · direct 0";
        }
    }

    private static string FormatTraffic(ProxyMetricsSnapshot snapshot)
        => $"↑ {FormatBytes(snapshot.BytesSent)} · ↓ {FormatBytes(snapshot.BytesReceived)}";

    private static string FormatConnections(ProxyMetricsSnapshot snapshot)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1} active · VPN {2} · direct {3}",
            snapshot.ActiveConnections,
            snapshot.TotalConnections,
            snapshot.TunneledConnections,
            snapshot.DirectConnections);

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " " + units[0];
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string StateLabel(PsiphonState state) => state switch
    {
        PsiphonState.Starting => "connecting",
        PsiphonState.Connected => "connected",
        PsiphonState.Stopping => "disconnecting",
        PsiphonState.Failed => "error",
        _ => "not running"
    };

    private void AppendLog(LogEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLog(entry)));
            return;
        }

        if (LogBox.Text.Length > 300_000)
        {
            LogBox.Text = LogBox.Text[^150_000..];
            LogBox.CaretIndex = LogBox.Text.Length;
        }

        var marker = entry.Level switch
        {
            LogLevel.Warn => "WARN ",
            LogLevel.Error => "ERR  ",
            _ => ""
        };

        LogBox.AppendText($"{entry.Timestamp:HH:mm:ss} {marker}{entry.Message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    // ------------------------------------------------------------------
    // Tray
    // ------------------------------------------------------------------

    private void CreateTrayIcon()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();

            menu.Items.Add("Open PsiSplit", null, (_, _) =>
                Dispatcher.InvokeAsync(ShowFromTray));

            menu.Items.Add(new Forms.ToolStripSeparator());

            _trayVpnItem = new Forms.ToolStripMenuItem("Start VPN", null, (_, _) =>
                Dispatcher.InvokeAsync(() => _ = ToggleVpnAsync()));
            menu.Items.Add(_trayVpnItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add("Exit", null, (_, _) =>
                Dispatcher.InvokeAsync(() => _ = ExitAsync()));

            _trayIcon = LoadAppIcon();
            _tray = new Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Text = "PsiSplit — Selective VPN",
                Visible = true,
                ContextMenuStrip = menu
            };

            _tray.DoubleClick += (_, _) => Dispatcher.InvokeAsync(ShowFromTray);
        }
        catch (Exception exception)
        {
            _log.Warn("Tray is unavailable: " + exception.Message);
        }
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // Fall through to the generic icon below.
        }

        return Drawing.SystemIcons.Application;
    }

    private void HideToTray()
    {
        Hide();

        if (!_trayHintShown && _tray is not null)
        {
            _trayHintShown = true;
            _tray.BalloonTipTitle = "PsiSplit";
            _tray.BalloonTipText = "The app is minimized to tray. The VPN keeps running. Use the tray menu to exit.";
            _tray.ShowBalloonTip(2500);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ------------------------------------------------------------------
    // Buttons that only forward
    // ------------------------------------------------------------------

    private async void StartButton_Click(object sender, RoutedEventArgs e)
        => await StartVpnAsync();

    private async void StopButton_Click(object sender, RoutedEventArgs e)
        => await StopVpnAsync();

    private void SiteModeButton_Click(object sender, RoutedEventArgs e)
        => ToggleSiteMode();

    private void SiteModeApply_Click(object sender, RoutedEventArgs e)
        => ToggleSiteMode();
}
