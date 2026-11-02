using System.Diagnostics;

namespace PsiSplit.Core.Services;

/// <summary>
/// Opens cmd/PowerShell with HTTP_PROXY/HTTPS_PROXY pointing at one of
/// PsiSplit's local proxies. The variables are inherited by child processes
/// (curl, git, npm, pip, Go, Python, .NET tools, etc.).
/// </summary>
public sealed class ShellLauncher
{
    private readonly LogService _log;

    public ShellLauncher(LogService log)
    {
        _log = log;
    }

    public bool LaunchCmd(int proxyPort)
    {
        var proxyUrl = ProxyUrl(proxyPort);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/k title CMD via PsiSplit & echo [PsiSplit] VPN proxy: " + proxyUrl,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = false
        };

        SetProxyEnvironment(startInfo, proxyPort);
        return Start(startInfo, $"CMD (port {proxyPort})");
    }

    public bool LaunchPowerShell(int proxyPort, bool preferPowerShell7 = true)
    {
        var executable = preferPowerShell7
            ? ExecutableLocator.FindPowerShell7()
            : ExecutableLocator.FindWindowsPowerShell();

        return LaunchPowerShellCore(executable, proxyPort);
    }

    public bool LaunchWindowsPowerShell(int proxyPort)
        => LaunchPowerShellCore(ExecutableLocator.FindWindowsPowerShell(), proxyPort);

    private bool LaunchPowerShellCore(string executable, int proxyPort)
    {
        var proxyUrl = ProxyUrl(proxyPort);
        var script = string.Join(Environment.NewLine, new[]
        {
            $"$p = '{proxyUrl}'",
            "$env:HTTP_PROXY = $p",
            "$env:HTTPS_PROXY = $p",
            "$env:ALL_PROXY = $p",
            "$env:NO_PROXY = 'localhost,127.0.0.1,::1,.local'",
            "try { [System.Net.WebRequest]::DefaultWebProxy = [System.Net.WebProxy]::new($p, $true) } catch { }",
            "try { [System.Net.Http.HttpClient]::DefaultProxy = [System.Net.WebProxy]::new($p, $true) } catch { }",
            "try { $PSDefaultParameterValues['Invoke-WebRequest:Proxy'] = $p } catch { }",
            "try { $PSDefaultParameterValues['Invoke-RestMethod:Proxy'] = $p } catch { }",
            "Write-Host ('[PsiSplit] VPN proxy: ' + $p) -ForegroundColor Green"
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = false
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        SetProxyEnvironment(startInfo, proxyPort);
        return Start(startInfo, $"PowerShell (port {proxyPort})");
    }

    public static string ProxyUrl(int port) => $"http://127.0.0.1:{port}";

    public static void SetProxyEnvironment(ProcessStartInfo startInfo, int proxyPort)
    {
        var proxyUrl = ProxyUrl(proxyPort);
        startInfo.Environment["HTTP_PROXY"] = proxyUrl;
        startInfo.Environment["HTTPS_PROXY"] = proxyUrl;
        startInfo.Environment["ALL_PROXY"] = proxyUrl;
        startInfo.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1,.local";
    }

    private bool Start(ProcessStartInfo startInfo, string description)
    {
        try
        {
            Process.Start(startInfo);
            _log.Info($"Launched: {description}.");
            return true;
        }
        catch (Exception exception)
        {
            _log.Error($"Failed to launch {description}", exception);
            return false;
        }
    }
}
