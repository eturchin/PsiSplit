using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using PsiSplit.Core.Models;
using PsiSplit.Core.Services;

namespace PsiSplit.CodexLauncher;

internal static class Program
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProxy");

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var isBridge = args.Length > 0 && args[0] is not ("--check" or "--check-proxy" or "--proxy-port") &&
            Environment.GetEnvironmentVariable(CodexProxyLaunch.RuntimeEnvironmentVariable) is { Length: > 0 };
        try
        {
            if (isBridge)
                return await RuntimeBridge.RunAsync(Environment.GetEnvironmentVariable(CodexProxyLaunch.RuntimeEnvironmentVariable)!, args);

            if (args is ["--check-proxy", var checkPort, var reportPath])
            {
                var result = await CheckProxyAsync(ParsePort(checkPort));
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { WriteIndented = true }));
                return result.CodexReachable ? 0 : 3;
            }

            var installation = CodexInstallation.Find();
            if (args.Length > 0 && args[0] == "--check")
            {
                if (args.Length != 2) throw new ArgumentException("Usage: CodexProxy.exe --check <report.json>");
                // A read-only check: no tunnel, application launch, profile or registry writes.
                await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
                {
                    LauncherVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown",
                    installation.Version, CodexRunning = CodexInstallation.IsCodexRunning(),
                    DesktopFound = File.Exists(installation.DesktopExecutable),
                    RuntimeFound = File.Exists(installation.RuntimeExecutable)
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            int? externalPort = args switch
            {
                [] => null,
                ["--proxy-port", var port] => ParsePort(port),
                _ => throw new ArgumentException("Usage: CodexProxy.exe [--proxy-port <local HTTP proxy port>]")
            };

            using var gate = new Semaphore(1, 1, @"Local\PsiSplit.CodexProxy.Launcher");
            if (!gate.WaitOne(0))
            {
                // A second shortcut launch is normally a request to see the app
                // that is already running. Focus its window instead of showing a
                // modal notice; keep the notice only when the window is not there
                // yet, for example while the first launch is still connecting.
                if (WindowActivation.TryActivateCodex(TimeSpan.FromSeconds(3))) return 0;
                Show("Codex Proxy is already running or connecting. Codex will open in its existing window shortly.");
                return 1;
            }
            try
            {
                if (CodexInstallation.IsCodexRunning())
                {
                    Show("Codex is already open. Finish your current work and fully exit Codex, then use this shortcut. No application was stopped and no settings were changed.");
                    return 2;
                }

                // The job is the last-resort owner: if this launcher is terminated
                // from Task Manager, Windows closes the handle and also terminates
                // the tunnel core, Codex and every child they started.
                using var ownership = new OwnedProcessJob();
                return externalPort is { } endpoint
                    ? await RunExternalAsync(installation, ownership, endpoint)
                    : await RunDesktopAsync(installation, ownership);
            }
            finally { gate.Release(); }
        }
        catch (Exception exception)
        {
            if (isBridge)
            {
                try
                {
                    using var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                    await error.WriteLineAsync("Codex Proxy runtime bridge: " + exception.Message);
                }
                catch (Exception writeError) when (writeError is IOException or ObjectDisposedException)
                {
                    // The parent closed the pipe while the bridge was failing.
                }
            }
            else Show(exception.Message + "\n\nDetails: %LOCALAPPDATA%\\CodexProxy\\logs", error: true);
            return 1;
        }
    }

    private static int ParsePort(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535)
        {
            return port;
        }
        throw new ArgumentException("Enter a local HTTP proxy port between 1 and 65535.");
    }

    private sealed record ProxyCheck(int PublicStatus, int CodexStatus, bool CodexReachable, string? Error = null);

    private static async Task<ProxyCheck> CheckProxyAsync(int port)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(ShellLauncher.ProxyUrl(port)),
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var web = await client.GetAsync("https://chatgpt.com/robots.txt", HttpCompletionOption.ResponseHeadersRead);
            using var api = await client.GetAsync("https://chatgpt.com/backend-api/wham/usage", HttpCompletionOption.ResponseHeadersRead);
            var reachable = web.StatusCode == HttpStatusCode.OK
                && api.StatusCode == HttpStatusCode.Unauthorized
                && api.Content.Headers.ContentType?.MediaType == "application/json";
            return new ProxyCheck((int)web.StatusCode, (int)api.StatusCode, reachable);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return new ProxyCheck(0, 0, false, exception.Message);
        }
    }

    private static async Task<int> RunExternalAsync(CodexInstallation installation, OwnedProcessJob ownership, int port)
    {
        var check = await CheckProxyAsync(port);
        if (!check.CodexReachable)
        {
            throw new InvalidOperationException(check.Error
                ?? $"The proxy could not reach Codex (HTTP {check.PublicStatus} / {check.CodexStatus}).");
        }
        var log = new LogService(Path.Combine(StateDirectory, "logs"));
        return await LaunchDesktopAndWaitAsync(installation, ownership, port, log);
    }

    private static async Task<int> RunDesktopAsync(CodexInstallation installation, OwnedProcessJob ownership)
    {
        var log = new LogService(Path.Combine(StateDirectory, "logs"));
        var tunnel = new PsiphonController(log, Path.Combine(StateDirectory, "Tunnel"),
            egressRegion: "", processStarted: ownership.Add);
        using var lifetime = new CancellationTokenSource();
        SmartProxyServer? proxy = null;
        Task? reconnect = null;
        try
        {
            await tunnel.StartAsync(lifetime.Token);
            proxy = new SmartProxyServer(0, TunnelMode.Full, tunnel.HttpProxyPort, _ => true,
                () => tunnel.State is PsiphonState.Connected or PsiphonState.Reconnecting, log);
            proxy.Start();
            reconnect = MaintainTunnelAsync(tunnel, proxy, log, lifetime.Token);
            return await LaunchDesktopAndWaitAsync(installation, ownership, proxy.Port, log);
        }
        catch (Exception exception)
        {
            log.Error("Codex Proxy launch failed", exception);
            throw;
        }
        finally
        {
            lifetime.Cancel();
            await StopOwnedServicesAsync(reconnect, proxy, tunnel, log);
        }
    }

    private static async Task<int> LaunchDesktopAndWaitAsync(CodexInstallation installation, OwnedProcessJob ownership,
        int port, LogService log)
    {
        var start = CodexProxyLaunch.CreateDesktopStartInfo(installation.DesktopExecutable,
            installation.RuntimeExecutable, Environment.ProcessPath!, port);
        using var desktop = Process.Start(start) ?? throw new InvalidOperationException("Codex could not be started.");
        try
        {
            ownership.Add(desktop);
            log.Info("Codex and its child processes are owned by the launcher job.");
        }
        catch (Exception exception)
        {
            log.Warn("Codex could not be registered for lifetime cleanup: " + exception.Message);
        }
        log.Info("Codex launched with an application-only proxy.");
        await desktop.WaitForExitAsync();
        // Electron may hand over to another process during an update/relaunch.
        var absent = 0;
        while (absent < 3)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            absent = CodexInstallation.IsCodexRunning() ? 0 : absent + 1;
        }
        return 0;
    }

    /// <summary>
    /// Best-effort shutdown. Cleanup errors are logged, never shown as a modal
    /// error: the user already closed Codex, and a hidden message box would make
    /// the launcher look stuck.
    /// </summary>
    private static async Task StopOwnedServicesAsync(Task? reconnect, SmartProxyServer? proxy,
        PsiphonController tunnel, LogService log)
    {
        if (reconnect is not null)
        {
            try { await reconnect; }
            catch (Exception exception) { log.Warn("Tunnel reconnect loop: " + exception.Message); }
        }

        if (proxy is not null)
        {
            try { await proxy.DisposeAsync(); }
            catch (Exception exception) { log.Warn("Proxy shutdown: " + exception.Message); }
        }

        try { await tunnel.StopAsync(); }
        catch (Exception exception) { log.Warn("Tunnel shutdown: " + exception.Message); }

        try { await tunnel.DisposeAsync(); }
        catch (Exception exception) { log.Warn("Tunnel disposal: " + exception.Message); }
    }

    private static async Task MaintainTunnelAsync(PsiphonController tunnel, SmartProxyServer proxy,
        LogService log, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                if (tunnel.State != PsiphonState.Failed) continue;
                try
                {
                    await tunnel.StartAsync(cancellationToken);
                    proxy.UpstreamPort = tunnel.HttpProxyPort;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    log.Warn("Tunnel reconnect failed: " + exception.Message);
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void Show(string message, bool error = false)
        => MessageBoxW(IntPtr.Zero, message, "Codex Proxy", error ? 0x10u : 0x40u);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
