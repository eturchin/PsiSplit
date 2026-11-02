using System.Text.Json;
using Microsoft.Win32;

namespace PsiSplit.Core.Services;

public sealed class ProxySnapshot
{
    public int ProxyEnable { get; set; }
    public string? ProxyServer { get; set; }
    public string? ProxyOverride { get; set; }
    public string? AutoConfigUrl { get; set; }
}

/// <summary>
/// Applies/restores a PAC URL in HKCU Internet Settings. The PAC only sends
/// listed domains to PsiSplit, so all other sites stay direct.
///
/// The previous settings are saved to disk before the first change, which lets
/// the app recover if it was killed while site mode was active.
/// </summary>
public sealed class SystemProxyManager : IDisposable
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();
    private readonly ConfigStore _store;
    private readonly LogService _log;
    private ProxySnapshot? _snapshot;
    private string? _pacUrl;
    private bool _applied;

    public SystemProxyManager(ConfigStore store, LogService log)
    {
        _store = store;
        _log = log;
    }

    public bool IsApplied
    {
        get
        {
            lock (_sync)
            {
                return _applied;
            }
        }
    }

    public string? PacUrl
    {
        get
        {
            lock (_sync)
            {
                return _pacUrl;
            }
        }
    }

    public void ApplyPac(string pacUrl)
    {
        lock (_sync)
        {
            if (!_applied)
            {
                _snapshot = ReadSnapshot();

                // Never restore a global Psiphon proxy on exit: that is the
                // exact behavior this app is designed to avoid.
                if (_snapshot.ProxyEnable == 1 && IsPsiphonProxy(_snapshot.ProxyServer))
                {
                    _snapshot.ProxyEnable = 0;
                    _snapshot.ProxyServer = null;
                }

                SaveBackup(_snapshot);
                _applied = true;
            }

            _pacUrl = pacUrl;
            DisablePsiphonSystemProxyIfPresent();
            WriteAutoConfigUrl(pacUrl);
            NativeMethods.NotifyProxyChanged();
            _log.Info($"Site mode enabled. System PAC: {pacUrl}");
        }
    }

    public void Restore()
    {
        lock (_sync)
        {
            if (!_applied)
            {
                return;
            }

            var snapshot = _snapshot ?? ReadBackup();
            if (snapshot is not null)
            {
                WriteSnapshot(snapshot);
                NativeMethods.NotifyProxyChanged();
                _log.Info("System proxy settings restored.");
            }

            _applied = false;
            _snapshot = null;
            _pacUrl = null;
            DeleteBackup();
        }
    }

    /// <summary>
    /// If the app crashed while site mode was on, the backup file is still on
    /// disk. Restore it on the next start instead of leaving the PAC active.
    /// </summary>
    public void RecoverIfNeeded()
    {
        lock (_sync)
        {
            if (_applied || !File.Exists(_store.ProxyBackupPath))
            {
                return;
            }

            var snapshot = ReadBackup();
            var currentPac = ReadString("AutoConfigURL");

            if (snapshot is not null &&
                !string.IsNullOrWhiteSpace(currentPac) &&
                currentPac.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                currentPac.Contains("/proxy.pac", StringComparison.OrdinalIgnoreCase))
            {
                WriteSnapshot(snapshot);
                NativeMethods.NotifyProxyChanged();
                _log.Warn("Found proxy settings from a previous run. System PAC restored.");
            }

            DeleteBackup();
        }
    }

    /// <summary>Watchdog: Psiphon must not silently turn itself global.</summary>
    public void EnsureStillApplied()
    {
        lock (_sync)
        {
            if (!_applied || _pacUrl is null)
            {
                return;
            }

            var currentPac = ReadString("AutoConfigURL");
            if (!string.Equals(currentPac, _pacUrl, StringComparison.OrdinalIgnoreCase))
            {
                WriteAutoConfigUrl(_pacUrl);
                NativeMethods.NotifyProxyChanged();
                _log.Warn("System PAC was changed by another program - restored.");
            }

            if (ReadInt("ProxyEnable", 0) == 1 && IsPsiphonProxy(ReadString("ProxyServer")))
            {
                WriteProxyEnable(0);
                NativeMethods.NotifyProxyChanged();
                _log.Warn("Psiphon system proxy detected - disabled so the VPN does not become global.");
            }
        }
    }

    public void DisablePsiphonSystemProxyIfPresent()
    {
        if (ReadInt("ProxyEnable", 0) != 1 || !IsPsiphonProxy(ReadString("ProxyServer")))
        {
            return;
        }

        WriteProxyEnable(0);
        NativeMethods.NotifyProxyChanged();
        _log.Warn("Psiphon set the system proxy. Disabled: the VPN must only work where it is needed.");
    }

    public void SetSkipProxySettings(bool skip)
        => PsiphonRegistry.SetSkipProxySettings(skip);

    public bool GetSkipProxySettings()
        => PsiphonRegistry.GetSkipProxySettings();

    private static bool IsPsiphonProxy(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer))
        {
            return false;
        }

        return proxyServer.Contains("127.0.0.1:239", StringComparison.OrdinalIgnoreCase) ||
               proxyServer.Contains("localhost:239", StringComparison.OrdinalIgnoreCase);
    }

    private static ProxySnapshot ReadSnapshot()
    {
        return new ProxySnapshot
        {
            ProxyEnable = ReadInt("ProxyEnable", 0),
            ProxyServer = ReadString("ProxyServer"),
            ProxyOverride = ReadString("ProxyOverride"),
            AutoConfigUrl = ReadString("AutoConfigURL")
        };
    }

    private static int ReadInt(string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: false);
        var value = key?.GetValue(name);
        try
        {
            return value is null ? fallback : Convert.ToInt32(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static string? ReadString(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: false);
        return key?.GetValue(name) as string;
    }

    private static void WriteProxyEnable(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsPath, writable: true);
        key?.SetValue("ProxyEnable", value, RegistryValueKind.DWord);
    }

    private static void WriteAutoConfigUrl(string url)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsPath, writable: true);
        key?.SetValue("AutoConfigURL", url, RegistryValueKind.String);
    }

    private static void WriteSnapshot(ProxySnapshot snapshot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsPath, writable: true);
        if (key is null)
        {
            return;
        }

        key.SetValue("ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);
        SetOrDelete(key, "ProxyServer", snapshot.ProxyServer);
        SetOrDelete(key, "ProxyOverride", snapshot.ProxyOverride);
        SetOrDelete(key, "AutoConfigURL", snapshot.AutoConfigUrl);
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private void SaveBackup(ProxySnapshot snapshot)
    {
        try
        {
            File.WriteAllText(_store.ProxyBackupPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception exception)
        {
            _log.Warn($"Failed to save the proxy backup: {exception.Message}");
        }
    }

    private ProxySnapshot? ReadBackup()
    {
        try
        {
            if (!File.Exists(_store.ProxyBackupPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProxySnapshot>(
                File.ReadAllText(_store.ProxyBackupPath),
                JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void DeleteBackup()
    {
        try
        {
            if (File.Exists(_store.ProxyBackupPath))
            {
                File.Delete(_store.ProxyBackupPath);
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        Restore();
    }
}
