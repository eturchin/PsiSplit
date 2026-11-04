using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PsiSplit.Core.Services;

namespace PsiSplit.App;

public partial class MainWindow
{
    private readonly ObservableCollection<CountryChoice> _countries = new();
    private readonly Stopwatch _trafficClock = Stopwatch.StartNew();
    private readonly TransferRateSampler _vpnRates = new();
    private readonly TransferRateSampler _fullRates = new();
    private readonly TransferRateSampler _filteredRates = new();
    private string _countryListKey = "";
    private bool _updatingCountries;

    private void OnConnectionInfoChanged()
    {
        if (_exiting) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_exiting) UpdateCountryUi(); }));
    }

    private async Task EnsureControllerSelectionAsync()
    {
        if (_psiphon is not null && _psiphon.RequestedEgressRegion != _config.EgressRegion)
        {
            // A changed preference is applied only by an explicit start/reconnect.
            // Await disposal so the private datastore lease cannot race its successor.
            if (_psiphon.CanStop) await _psiphon.StopAsync();
            _psiphon.StateChanged -= OnPsiphonStateChanged;
            _psiphon.ConnectionInfoChanged -= OnConnectionInfoChanged;
            await _psiphon.DisposeAsync();
            _psiphon = null;
        }
        if (_psiphon is null) BuildController();
    }

    private void UpdateCountryUi()
    {
        var report = _psiphon?.RegionReport ?? EgressRegionReport.Empty;
        var selected = CountryList.SelectedValue as string ?? _config.EgressRegion;
        var codes = report.ReportedAtUtc.HasValue ? report.Codes : PsiphonRegionCatalog.BundledRegions;
        var options = codes.Append(_config.EgressRegion).Append(selected)
            .Where(code => code.Length == 2).Distinct(StringComparer.Ordinal)
            .OrderBy(PsiphonRegionCatalog.DisplayName, StringComparer.Ordinal)
            .Select(code => new CountryChoice(code, PsiphonRegionCatalog.DisplayName(code),
                report.ReportedAtUtc.HasValue
                    ? report.Codes.Contains(code) ? report.FromCache ? "Last reported by Psiphon" : "Reported by Psiphon" : "Not in the latest report"
                    : PsiphonRegionCatalog.BundledRegions.Contains(code) ? "Bundled country list" : "Saved preference"))
            .Prepend(new CountryChoice("", "Automatic (best available)", "Let Psiphon choose the country and server"))
            .ToArray();
        var key = string.Join("|", options.Select(option => option.Code + ":" + option.Source));
        if (_countryListKey != key)
        {
            _updatingCountries = true;
            try
            {
                _countries.Clear();
                foreach (var option in options) _countries.Add(option);
                CountryList.SelectedValue = selected;
                _countryListKey = key;
            }
            finally { _updatingCountries = false; }
        }

        CountryCountText.Text = $"{codes.Count} countries";
        CountryReportText.Text = report.ReportedAtUtc is { } reported
            ? $"{(report.FromCache ? "Last known list" : "Reported by Psiphon")} · {reported.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Bundled list · connect to discover the current countries.";
        var region = _psiphon?.ConnectedRegion ?? "";
        var connectedText = _psiphon?.State == PsiphonState.Connected
            ? region.Length == 2 ? CountryLabel(region) : "Detecting exit country…"
            : _psiphon?.State == PsiphonState.Starting ? "Connecting…" : "Not connected";
        ConnectedCountryText.Text = connectedText;
        HomeCountryText.Text = connectedText;
        SavedCountryText.Text = CountryLabel(_config.EgressRegion);
        UpdateCountryButtons();
    }

    private void UpdateCountryButtons()
    {
        var selected = CountryList.SelectedItem as CountryChoice;
        var transitioning = _psiphon?.State is PsiphonState.Starting or PsiphonState.Stopping;
        SaveCountryButton.IsEnabled = !_preview && !_busy && selected is not null && selected.Code != _config.EgressRegion;
        ConnectCountryButton.IsEnabled = !_preview && !_busy && !transitioning && selected is not null;
        ConnectCountryButton.Content = _psiphon?.State == PsiphonState.Connected
            ? "Reconnect with selection" : "Connect with selection";
    }

    private void CountryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCountries || CountryList.SelectedItem is not CountryChoice selected) return;
        CountryStatusText.Text = selected.Code == _config.EgressRegion
            ? "This is your saved preference."
            : "Save for the next connection, or connect with this selection.";
        UpdateCountryButtons();
    }

    private bool SaveCountrySelection()
    {
        if (_preview || _busy || CountryList.SelectedItem is not CountryChoice selected) return false;
        var previous = _config.EgressRegion;
        try
        {
            _config.EgressRegion = selected.Code;
            _config.Sites = _sites.ToList();
            _config.Apps = _apps.ToList();
            _store.Save(_config);
            CountryStatusText.Text = _psiphon?.State == PsiphonState.Connected
                ? "Saved. Your current tunnel continues until you reconnect."
                : "Saved. The selection will apply when the VPN starts.";
            UpdateCountryUi();
            return true;
        }
        catch (Exception exception)
        {
            _config.EgressRegion = previous;
            _log.Error("Saving exit country", exception);
            CountryStatusText.Text = "Could not save the country. Check the log.";
            return false;
        }
    }

    private void SaveCountry_Click(object sender, RoutedEventArgs e) => SaveCountrySelection();

    private async void ConnectCountry_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCountrySelection()) return;
        if (_psiphon?.CanStop == true)
        {
            await StopVpnAsync();
            if (_psiphon.CanStop)
            {
                CountryStatusText.Text = "The current tunnel could not be stopped. Check the log.";
                return;
            }
        }
        await StartVpnAsync();
        CountryStatusText.Text = _psiphon?.State == PsiphonState.Connected
            ? "Connected using the saved preference."
            : "Connection was not established. Check the log or choose another country.";
    }

    private static string CountryLabel(string code)
        => code.Length == 0 ? "Automatic (best available)" : $"{PsiphonRegionCatalog.DisplayName(code)} ({code})";

    private static string FormatSpeed(double bytesPerSecond)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string FormatSpeeds(TransferRateSnapshot rates)
        => $"↑ {FormatSpeed(rates.BytesSentPerSecond)} · ↓ {FormatSpeed(rates.BytesReceivedPerSecond)}";

    public sealed record CountryChoice(string Code, string Name, string Source)
    {
        public string CodeLabel => Code.Length == 0 ? "AUTO" : Code;
    }
}
