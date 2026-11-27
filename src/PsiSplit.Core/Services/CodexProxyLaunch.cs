using System.Diagnostics;

namespace PsiSplit.Core.Services;

/// <summary>Process-local routing for the Electron UI and the separate Codex runtime.</summary>
public static class CodexProxyLaunch
{
    public const string RuntimeEnvironmentVariable = "PSISPLIT_CODEX_REAL_RUNTIME";

    /// <summary>
    /// PID of the launcher that owns the Codex process tree. The runtime bridge
    /// watches it so a terminated launcher cannot leave a bridge or native
    /// runtime behind.
    /// </summary>
    public const string LauncherEnvironmentVariable = "PSISPLIT_CODEX_LAUNCHER_PID";

    public static ProcessStartInfo CreateDesktopStartInfo(string executable, string runtime,
        string launcher, int proxyPort)
    {
        if (proxyPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(proxyPort));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        start.ArgumentList.Add($"--proxy-server={ShellLauncher.ProxyUrl(proxyPort)}");
        start.ArgumentList.Add("--proxy-bypass-list=localhost;127.0.0.1;[::1]");
        ShellLauncher.SetProxyEnvironment(start, proxyPort);
        // The runtime can otherwise prefer an existing Windows PAC over these
        // variables when features.respect_system_proxy is enabled in config.toml.
        // Use the app's CLI override for this child only; never edit that config.
        start.Environment["CODEX_CLI_PATH"] = Path.GetFullPath(launcher);
        start.Environment[RuntimeEnvironmentVariable] = Path.GetFullPath(runtime);
        start.Environment[LauncherEnvironmentVariable] = Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return start;
    }

    public static ProcessStartInfo CreateRuntimeStartInfo(string runtime, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(runtime)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("features.respect_system_proxy=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("CODEX_CLI_PATH");
        start.Environment.Remove(RuntimeEnvironmentVariable);
        start.Environment.Remove(LauncherEnvironmentVariable);
        return start;
    }
}
