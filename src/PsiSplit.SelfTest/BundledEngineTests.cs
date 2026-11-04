using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PsiSplit.Core.Models;
using PsiSplit.Core.Services;

internal static class BundledEngineTests
{
    public static async Task RunLocalAsync(string root, Action<string, bool, string?> check)
    {
        check("Core notice: HTTP port", PsiphonNotice.TryParse(
            """{"noticeType":"ListeningHttpProxyPort","data":{"port":53123}}""", out var http) && http.Value == 53123, null);
        check("Core notice: tunnel ready", PsiphonNotice.TryParse(
            """{"noticeType":"Tunnels","data":{"count":1}}""", out var tunnels) && tunnels.Value == 1, null);
        foreach (var line in new[] { "invalid", "null", "[]", "{}",
            """{"noticeType":"ListeningHttpProxyPort","data":{"port":0}}""",
            """{"noticeType":"ListeningHttpProxyPort","data":{"port":65536}}""",
            """{"noticeType":"ListeningHttpProxyPort","data":{"port":"8080"}}""",
            """{"noticeType":"Tunnels","data":{"count":-1}}""" })
        {
            check("Core notice rejects malformed data", !PsiphonNotice.TryParse(line, out _), line);
        }

        var dataDirectory = Path.Combine(root, "private-data");
        var config = PsiphonBundle.CreateConfig(dataDirectory);
        check("Core uses its own data directory", config["DataRootDirectory"]!.GetValue<string>() == Path.GetFullPath(dataDirectory), null);
        check("Core asks the OS for free HTTP/SOCKS ports", config["LocalHttpProxyPort"]!.GetValue<int>() == 0 && config["LocalSocksProxyPort"]!.GetValue<int>() == 0, null);
        check("Core binds only to loopback", config["ListenInterface"]!.GetValue<string>() == "", null);
        check("Bootstrap has no account tokens, opaque overrides or migration paths",
            !config.Any(p => p.Key.StartsWith("Migrate") || p.Key is "Authorizations" or "AdditionalParameters" or "UpstreamProxyUrl" or "NetworkID" or "DeviceRegion"), null);
        check("GUI auto-update is disabled", config["EnableUpgradeDownload"]!.GetValue<bool>() == false, null);

        var bin = Path.Combine(root, "bundle-check");
        var executable = await PsiphonBundle.ExtractAsync(bin);
        check("Bundled core integrity", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable))) == PsiphonBundle.CoreHash, null);
        var bootstrap = Path.Combine(Path.GetDirectoryName(executable)!, "bootstrap.json");
        var original = await File.ReadAllTextAsync(bootstrap);
        await File.WriteAllTextAsync(bootstrap, "corrupt cache");
        await PsiphonBundle.ExtractAsync(bin);
        check("A corrupt cached resource is repaired", await File.ReadAllTextAsync(bootstrap) == original, null);

        using (var version = Process.Start(new ProcessStartInfo(executable, "--version")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        })!)
        {
            var output = await version.StandardOutput.ReadToEndAsync();
            await version.WaitForExitAsync();
            check("Bundled binary version matches manifest", version.ExitCode == 0 && output.Trim().Replace("\r\n", "\n") == PsiphonBundle.Version.Replace("\r\n", "\n"), output.Trim());
        }

        var cancelRoot = Path.Combine(root, "pre-cancelled");
        await using (var controller = new PsiphonController(new LogService(), cancelRoot))
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try { await controller.StartAsync(cancel.Token); }
            catch (OperationCanceledException) { }
            check("Cancelled startup creates no process or directory", controller.ProcessId is null && !Directory.Exists(cancelRoot), null);
        }

        var busyRoot = Path.Combine(root, "busy");
        Directory.CreateDirectory(busyRoot);
        using (var lease = new FileStream(Path.Combine(busyRoot, "controller.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        await using (var controller = new PsiphonController(new LogService(), busyRoot))
        {
            try { await controller.StartAsync(); }
            catch (IOException) { }
            check("Concurrent ownership is refused before process launch", controller.State == PsiphonState.Failed && controller.ProcessId is null, null);
        }
    }

    public static async Task RunIsolatedAsync(string root, Action<string, bool, string?> check)
    {
        Console.WriteLine("--- isolated real-engine test: no global proxy writes ---");
        var registryBefore = ReadRegistry();
        var liveBefore = LiveSessions();
        var testHandles = new List<Process>();
        var controllers = new List<PsiphonController>();
        var log = new LogService(Path.Combine(root, "logs"), entry => Console.WriteLine($"    [{entry.Level}] {entry.Message}"));
        try
        {
            var firstRoot = Path.Combine(root, "first engine with spaces");
            var first = new PsiphonController(log, firstRoot);
            controllers.Add(first);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
            await first.StartAsync(timeout.Token);
            testHandles.Add(Process.GetProcessById(first.ProcessId!.Value));
            check("Standalone bundled engine connects", first.State == PsiphonState.Connected, firstRoot);
            check("Core confirms the requested exit country", first.ConnectedRegion == "NL", first.ConnectedRegion);
            check("Core reports actual available countries", first.RegionReport.ReportedAtUtc is not null && !first.RegionReport.FromCache && first.RegionReport.Codes.Contains("NL"), $"{first.RegionReport.Codes.Count} reported countries");
            var rateClock = Stopwatch.StartNew();
            var realRates = new TransferRateSampler();
            var beforeTraffic = first.TrafficTotals;
            realRates.Sample(beforeTraffic.BytesSent, beforeTraffic.BytesReceived, rateClock.Elapsed);
            var initialPid = first.ProcessId;
            await first.StartAsync(timeout.Token);
            check("Repeated start keeps the connected owned process", first.ProcessId == initialPid, null);
            check("Core chooses independent HTTP and SOCKS ports", first.HttpProxyPort > 0 && first.SocksProxyPort > 0 && first.HttpProxyPort != 23934 && first.SocksProxyPort != 23933, $"HTTP={first.HttpProxyPort}, SOCKS={first.SocksProxyPort}");
            using (var core = Process.GetProcessById(first.ProcessId.Value))
            {
                check("Core has no application window", core.MainWindowHandle == IntPtr.Zero, null);
                check("Core is extracted inside the isolated test directory", core.MainModule!.FileName.StartsWith(firstRoot, StringComparison.OrdinalIgnoreCase), null);
            }
            check("HTTPS through the bundled HTTP proxy", (await FetchAsync($"http://127.0.0.1:{first.HttpProxyPort}", "https://example.com/")).Contains("Example Domain"), null);
            check("HTTPS through the bundled SOCKS5 proxy", (await FetchAsync($"socks5://127.0.0.1:{first.SocksProxyPort}", "https://example.com/")).Contains("Example Domain"), null);
            var trafficDeadline = Stopwatch.StartNew();
            while (trafficDeadline.Elapsed < TimeSpan.FromSeconds(12) && first.TrafficTotals.BytesReceived <= beforeTraffic.BytesReceived)
                await Task.Delay(200, timeout.Token);
            var afterTraffic = first.TrafficTotals;
            var speed = realRates.Sample(afterTraffic.BytesSent, afterTraffic.BytesReceived, rateClock.Elapsed);
            check("Real tunneled traffic produces upload/download rates", speed.BytesReceivedPerSecond > 0 && speed.BytesSentPerSecond > 0, null);

            var matcher = new DomainMatcher(new[] { new SiteRule { Pattern = "example.com", Enabled = true } });
            await using (var full = new SmartProxyServer(0, TunnelMode.Full, first.HttpProxyPort, _ => true, () => first.State == PsiphonState.Connected, log))
            await using (var filtered = new SmartProxyServer(0, TunnelMode.Filtered, first.HttpProxyPort, matcher.IsMatch, () => first.State == PsiphonState.Connected, log))
            {
                full.Start();
                filtered.Start();
                check("Full proxy tunnels HTTPS", (await FetchAsync($"http://127.0.0.1:{full.Port}", "https://example.com/")).Contains("Example Domain"), null);
                check("Filtered proxy tunnels listed HTTPS", (await FetchAsync($"http://127.0.0.1:{filtered.Port}", "https://example.com/")).Contains("Example Domain"), null);

                using var direct = new TcpListener(IPAddress.Loopback, 0);
                direct.Start();
                var directPort = ((IPEndPoint)direct.LocalEndpoint).Port;
                var responseTask = ReplyDirectAsync(direct);
                check("Filtered proxy sends unlisted HTTP directly", await FetchAsync($"http://127.0.0.1:{filtered.Port}", $"http://127.0.0.1:{directPort}/") == "direct-ok", null);
                await responseTask;
                var metrics = filtered.Metrics.Snapshot();
                check("Filtered routes are counted separately", metrics.TunneledConnections == 1 && metrics.DirectConnections == 1, $"VPN={metrics.TunneledConnections}, direct={metrics.DirectConnections}");
            }

            var pid = first.ProcessId;
            var httpPort = first.HttpProxyPort;
            await first.DisposeAsync();
            check("Detach preserves only the owned core", await PsiphonController.ProbeAsync(httpPort), null);
            var reattached = new PsiphonController(log, firstRoot);
            controllers.Add(reattached);
            await reattached.StartAsync(timeout.Token);
            check("A saved owned session reattaches to the same PID", reattached.ProcessId == pid && reattached.HttpProxyPort == httpPort, null);

            var secondRoot = Path.Combine(root, "second");
            Directory.CreateDirectory(secondRoot);
            // A stale/copied session with an existing PID and start time must
            // still fail the executable identity check. It must not adopt or
            // stop the engine in the other directory.
            await File.WriteAllTextAsync(Path.Combine(secondRoot, "session.json"), JsonSerializer.Serialize(new
            {
                ProcessId = pid,
                StartTimeUtcTicks = testHandles[0].StartTime.ToUniversalTime().Ticks,
                Executable = Path.Combine(secondRoot, "bin", PsiphonBundle.CoreHash, "psiphon-tunnel-core.exe"),
                HttpPort = httpPort,
                SocksPort = reattached.SocksProxyPort
            }));
            var second = new PsiphonController(log, secondRoot, "DE");
            controllers.Add(second);
            await second.StartAsync(timeout.Token);
            testHandles.Add(Process.GetProcessById(second.ProcessId!.Value));
            check("Two independent cores coexist", second.ProcessId != pid && second.HttpProxyPort != httpPort, null);
            check("A second country is applied without changing the first tunnel", second.ConnectedRegion == "DE" && reattached.ConnectedRegion == "NL", $"second={second.ConnectedRegion}, first={reattached.ConnectedRegion}");
            check("Stale session metadata cannot adopt an unrelated core", await PsiphonController.ProbeAsync(httpPort), null);
            await second.StopAsync();
            check("Stopping one core leaves the other working", await PsiphonController.ProbeAsync(httpPort), null);
            await reattached.DisposeAsync();
            using var oldSession = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(firstRoot, "session.json")));
            var oldNoticeName = oldSession.RootElement.GetProperty("NoticeFile").GetString()!;
            var oldNoticePath = Path.Combine(firstRoot, "notices", oldNoticeName);
            // Simulate a log viewer/antivirus retaining the previous notice file.
            using var heldNoticeReader = new FileStream(oldNoticePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var changed = new PsiphonController(log, firstRoot, "DE");
            controllers.Add(changed);
            await changed.StartAsync(timeout.Token);
            testHandles.Add(Process.GetProcessById(changed.ProcessId!.Value));
            check("Changing country does not reattach to the old-country session", changed.ProcessId != pid && testHandles[0].HasExited && changed.ConnectedRegion == "DE", null);
            using var newSession = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(firstRoot, "session.json")));
            check("Reconnect tolerates readers holding the previous notices", newSession.RootElement.GetProperty("NoticeFile").GetString() != oldNoticeName, null);
            var changedPort = changed.HttpProxyPort;
            await changed.StopAsync();
            check("Stop closes the owned proxy and clears ownership", !await PsiphonController.ProbeAsync(changedPort) && !changed.CanStop && changed.State == PsiphonState.Stopped, null);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var cancellingLog = new LogService(uiSink: entry =>
            {
                if (entry.Message.Contains("started without a window")) cancellation.Cancel();
            });
            var cancelled = new PsiphonController(cancellingLog, Path.Combine(root, "cancel-during-start"));
            controllers.Add(cancelled);
            try { await cancelled.StartAsync(cancellation.Token); }
            catch (OperationCanceledException) { }
            check("Cancellation cleans up the newly launched core", cancelled.State == PsiphonState.Stopped && cancelled.ProcessId is null && !File.Exists(Path.Combine(cancelled.RuntimeDirectory, "session.json")), null);
        }
        catch (Exception exception)
        {
            check("Isolated engine integration", false, exception.ToString());
        }
        finally
        {
            foreach (var controller in controllers)
            {
                try { if (controller.CanStop) await controller.StopAsync(); }
                catch (Exception exception) { check("Test core cleanup", false, exception.Message); }
                await controller.DisposeAsync();
            }
            // Exact handles retained only for test-created processes, including
            // an intentional detach if a later assertion/request failed.
            foreach (var handle in testHandles)
            {
                if (!handle.HasExited) { handle.Kill(); await handle.WaitForExitAsync(); }
                handle.Dispose();
            }
            check("Windows and Psiphon registry settings are unchanged", registryBefore == ReadRegistry(), null);
            var after = LiveSessions();
            check("All pre-existing sessions keep the same PID and start time", liveBefore.All(p => after.TryGetValue(p.Key, out var ticks) && ticks == p.Value), null);
            check("No extra Psiphon GUI or orphaned test core remains", after.Keys.Order().SequenceEqual(liveBefore.Keys.Order()), null);
        }
    }

    private static async Task<string> FetchAsync(string proxy, string url)
    {
        using var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy(proxy) { BypassProxyOnLocal = false } };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        return await client.GetStringAsync(url);
    }

    private static async Task ReplyDirectAsync(TcpListener listener)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        using var stream = client.GetStream();
        await HttpHeaderReader.ReadHeaderAsync(stream, 16384, timeout.Token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\ndirect-ok"), timeout.Token);
    }

    private static string ReadRegistry()
    {
        var values = new SortedDictionary<string, object?>();
        foreach (var path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", @"Software\Psiphon3" })
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            if (key is null) continue;
            foreach (var name in key.GetValueNames()) values[path + "\\" + name] = key.GetValue(name);
        }
        return JsonSerializer.Serialize(values);
    }

    private static Dictionary<int, long> LiveSessions()
    {
        var result = new Dictionary<int, long>();
        foreach (var name in new[] { "PsiSplit", "psiphon3", "psiphon-tunnel-core" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (!process.HasExited) result[process.Id] = process.StartTime.ToUniversalTime().Ticks;
            }
        }
        return result;
    }
}
