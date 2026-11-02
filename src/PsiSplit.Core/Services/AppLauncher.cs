using System.Diagnostics;
using PsiSplit.Core.Models;

namespace PsiSplit.Core.Services;

/// <summary>
/// Launches user-defined applications with proxy environment variables. For
/// Chromium-based browsers an explicit --proxy-server flag is added as well,
/// because a browser started from a shortcut may ignore environment variables.
/// </summary>
public sealed class AppLauncher
{
    private readonly LogService _log;

    public AppLauncher(LogService log)
    {
        _log = log;
    }

    public bool Launch(AppProfile profile, int fullPort, int filteredPort)
    {
        if (profile is null)
        {
            return false;
        }

        var executable = Environment.ExpandEnvironmentVariables(profile.Path).Trim().Trim('"');
        if (!File.Exists(executable))
        {
            _log.Error($"Application not found: {profile.Path}");
            return false;
        }

        var port = profile.Mode == TunnelMode.Full ? fullPort : filteredPort;
        var arguments = profile.Arguments ?? string.Empty;
        var fileName = Path.GetFileName(executable);

        if (IsChromiumBrowser(fileName))
        {
            if (!arguments.Contains("--proxy-server", StringComparison.OrdinalIgnoreCase) &&
                !arguments.Contains("--proxy-pac-url", StringComparison.OrdinalIgnoreCase))
            {
                arguments = $"--proxy-server=http://127.0.0.1:{port} {arguments}".Trim();
            }

            if (!arguments.Contains("--user-data-dir", StringComparison.OrdinalIgnoreCase) &&
                IsBrowserProcessRunning(fileName))
            {
                _log.Warn(
                    $"{fileName} is already running. A new process will hand the tab to the existing instance and ignore --proxy-server. " +
                    "Close the browser before launching, or add a separate --user-data-dir argument.");
            }
        }
        else if (fileName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn(
                "Firefox does not use HTTP_PROXY from the environment. Enable Site mode (PAC) for it - the PAC is applied to the browser automatically.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        ShellLauncher.SetProxyEnvironment(startInfo, port);

        try
        {
            Process.Start(startInfo);
            _log.Info($"Launched application \"{profile.Name}\" through port {port} ({profile.Mode.ToString().ToLowerInvariant()}).");
            return true;
        }
        catch (Exception exception)
        {
            _log.Error($"Failed to launch \"{profile.Name}\"", exception);
            return false;
        }
    }

    private static bool IsChromiumBrowser(string fileName)
        => fileName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("opera.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowserProcessRunning(string fileName)
    {
        var processName = Path.GetFileNameWithoutExtension(fileName);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            process.Dispose();
            return true;
        }

        return false;
    }
}
