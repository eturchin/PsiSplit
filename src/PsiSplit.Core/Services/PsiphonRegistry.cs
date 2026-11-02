using Microsoft.Win32;

namespace PsiSplit.Core.Services;

/// <summary>
/// Reads/writes Psiphon's per-user settings. SkipProxySettings=1 is what keeps
/// Psiphon from turning itself into a system-wide proxy.
/// </summary>
public static class PsiphonRegistry
{
    private const string KeyPath = @"Software\Psiphon3";

    public static int ReadHttpProxyPort(int fallback)
        => ReadInt("LocalHTTPProxyPort", fallback);

    public static int ReadSocksProxyPort(int fallback)
        => ReadInt("LocalSOCKSProxyPort", fallback);

    /// <summary>Returns true when at least one value was changed.</summary>
    public static bool EnsureSelectiveMode()
    {
        var changed = false;

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (key is null)
        {
            return false;
        }

        if (ReadInt(key, "SkipProxySettings", 0) != 1)
        {
            key.SetValue("SkipProxySettings", 1, RegistryValueKind.DWord);
            changed = true;
        }

        if (ReadInt(key, "SkipBrowser", 0) != 1)
        {
            key.SetValue("SkipBrowser", 1, RegistryValueKind.DWord);
            changed = true;
        }

        return changed;
    }

    public static bool GetSkipProxySettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key is not null && ReadInt(key, "SkipProxySettings", 0) == 1;
    }

    public static void SetSkipProxySettings(bool skip)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key?.SetValue("SkipProxySettings", skip ? 1 : 0, RegistryValueKind.DWord);
    }

    private static int ReadInt(string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key is null ? fallback : ReadInt(key, name, fallback);
    }

    private static int ReadInt(RegistryKey key, string name, int fallback)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int intValue => intValue,
            uint uintValue => unchecked((int)uintValue),
            long longValue when longValue is > int.MinValue and < int.MaxValue => (int)longValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => fallback
        };
    }
}
