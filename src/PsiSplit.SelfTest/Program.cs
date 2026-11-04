using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PsiSplit.Core.Models;
using PsiSplit.Core.Services;

internal static class Program
{
    private const int FullPort = 28080;
    private const int FilteredPort = 28081;
    private const int PacPort = 28082;

    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== PsiSplit self-test ===");
        Console.WriteLine($"Directories: {Path.GetTempPath()}");

        var root = Path.Combine(Path.GetTempPath(), "PsiSplitSelfTest", Guid.NewGuid().ToString("N"));

        var store = new ConfigStore(root);
        var log = new LogService(store.LogDirectory, entry =>
            Console.WriteLine($"    [{entry.Level}] {entry.Message}"));

        TestDomainMatcher();
        TestPacGenerator();
        TestChatGptDefaults(root);
        TestPacSyntaxWithJScript();
        await BundledEngineTests.RunLocalAsync(root, Check);
        await ConnectionFeatureTests.RunLocalAsync(root, Check);

        if (args.Contains("--local", StringComparer.OrdinalIgnoreCase))
        {
            await TestSmartProxyWithFakeUpstreamAsync();
            return Finish();
        }

        // The default integration test is isolated. Global PAC mutation is only
        // exercised with an explicit opt-in and never alongside a live session.
        if (!args.Contains("--system-proxy", StringComparer.OrdinalIgnoreCase))
        {
            await BundledEngineTests.RunIsolatedAsync(root, Check);
            return Finish();
        }

        if (ProcessTree.IsRunning("PsiSplit") || ProcessTree.AnyPsiphonRunning())
        {
            Check("System-proxy test requires no active PsiSplit/Psiphon session", false, "Use the default isolated test instead.");
            return Finish();
        }

        var psiphon = new PsiphonController(log, Path.Combine(root, "Tunnel"));
        psiphon.StateChanged += state => Console.WriteLine($"    state -> {state}");

        SmartProxyServer? fullProxy = null;
        SmartProxyServer? filteredProxy = null;
        PacServer? pacServer = null;
        SystemProxyManager? systemProxy = null;

        try
        {
            var proxyBefore = ReadProxyState();
            Console.WriteLine($"System proxy before: enable={proxyBefore.Enable}, server='{proxyBefore.Server}', pac='{proxyBefore.Pac}'");

            var sw = Stopwatch.StartNew();
            await psiphon.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(180)).Token);
            sw.Stop();
            Check("Psiphon connected", psiphon.State == PsiphonState.Connected, $"state={psiphon.State}, {sw.Elapsed.TotalSeconds:F1}s");

            var proxyAfterStart = ReadProxyState();
            Check("Psiphon did not change the system PAC", string.Equals(proxyBefore.Pac, proxyAfterStart.Pac, StringComparison.OrdinalIgnoreCase));
            Check("Psiphon did not enable the global proxy",
                !(proxyAfterStart.Enable == 1 && IsPsiphonProxy(proxyAfterStart.Server)),
                $"enable={proxyAfterStart.Enable}, server='{proxyAfterStart.Server}'");

            var directIp = await GetDirectIpAsync();
            var viaPsiphonIp = await GetIpViaProxyAsync($"http://127.0.0.1:{psiphon.HttpProxyPort}");
            Check("Direct IP obtained", directIp is not null, directIp);
            Check("IP through Psiphon obtained", viaPsiphonIp is not null, viaPsiphonIp);
            Check("IP through Psiphon differs from direct", directIp != viaPsiphonIp, $"direct={directIp}, psiphon={viaPsiphonIp}");

            var rules = new List<SiteRule>
            {
                new() { Pattern = "api.ipify.org", Enabled = true }
            };
            var matcher = new DomainMatcher(rules);

            fullProxy = new SmartProxyServer(
                FullPort,
                TunnelMode.Full,
                psiphon.HttpProxyPort,
                _ => true,
                () => psiphon.State == PsiphonState.Connected,
                log);
            fullProxy.Start();

            filteredProxy = new SmartProxyServer(
                FilteredPort,
                TunnelMode.Filtered,
                psiphon.HttpProxyPort,
                host => matcher.IsMatch(host),
                () => psiphon.State == PsiphonState.Connected,
                log);
            filteredProxy.Start();

            var fullIp = await GetIpViaProxyAsync($"http://127.0.0.1:{FullPort}");
            Check("Full proxy routes through Psiphon", fullIp == viaPsiphonIp, $"full={fullIp}, psiphon={viaPsiphonIp}");

            var filteredListedIp = await GetIpViaProxyAsync($"http://127.0.0.1:{FilteredPort}", "https://api.ipify.org?format=json");
            Check("Filtered proxy: listed site goes through Psiphon", filteredListedIp == viaPsiphonIp, $"got={filteredListedIp}, psiphon={viaPsiphonIp}");

            var filteredUnlistedIp = await GetIpViaProxyAsyncAny(
                $"http://127.0.0.1:{FilteredPort}",
                new[]
                {
                    "https://ipv4.icanhazip.com/",
                    "https://ipinfo.io/ip",
                    "https://ifconfig.me/ip"
                });
            Check("Filtered proxy: unlisted site goes direct", filteredUnlistedIp is not null && filteredUnlistedIp != viaPsiphonIp, $"got={filteredUnlistedIp}, direct={directIp}");

            var httpStatus = await GetStatusViaProxyAnyAsync(
                $"http://127.0.0.1:{FullPort}",
                new[]
                {
                    "http://example.com/",
                    "http://api.ipify.org?format=json"
                });
            Check("HTTP (non-CONNECT) through Full proxy", httpStatus is >= 200 and < 400, $"status={httpStatus}");

            var shellIp = await RunAndExtractIpAsync(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/c curl.exe -sS --max-time 45 https://api.ipify.org?format=json",
                $"http://127.0.0.1:{FullPort}");
            Check("cmd + curl use the proxy from the environment", shellIp == viaPsiphonIp, $"cmd={shellIp}, psiphon={viaPsiphonIp}");

            var pac = PacGenerator.Generate(rules, FilteredPort);
            pacServer = new PacServer(PacPort, log);
            pacServer.UpdatePac(pac);
            pacServer.Start();

            var pacText = await GetTextDirectAsync(pacServer.Url);
            Check("PAC server serves PAC", pacText.Contains($"PROXY 127.0.0.1:{FilteredPort}", StringComparison.Ordinal), pacText.Length.ToString());
            Check("PAC contains DIRECT for other hosts", pacText.Contains("return \"DIRECT\";", StringComparison.Ordinal));

            systemProxy = new SystemProxyManager(store, log);
            systemProxy.RecoverIfNeeded();
            systemProxy.ApplyPac(pacServer.Url);

            var afterApply = ReadProxyState();
            Check("Site mode wrote AutoConfigURL", string.Equals(afterApply.Pac, pacServer.Url, StringComparison.OrdinalIgnoreCase), afterApply.Pac);

            var pacResult = await RunAndExtractIpAsync(
                ExecutableLocator.FindWindowsPowerShell(),
                "-NoProfile -Command \"(Invoke-WebRequest -UseBasicParsing -TimeoutSec 45 'https://api.ipify.org?format=json').Content\"",
                proxyUrl: null);
            Check("PAC is applied system-wide (PowerShell/WinINet)", pacResult == viaPsiphonIp, $"powershell={pacResult}, psiphon={viaPsiphonIp}");

            var chrome = ExecutableLocator.FindChrome();
            if (chrome is not null)
            {
                var chromeListed = await RunChromeAsync(
                    chrome,
                    $"https://api.ipify.org?format=json",
                    $"--proxy-server=http://127.0.0.1:{FilteredPort}");
                Check("Chrome via Filtered proxy: listed site goes through Psiphon",
                    chromeListed is not null && chromeListed == viaPsiphonIp,
                    $"chrome={chromeListed}, psiphon={viaPsiphonIp}");

                var chromeUnlisted = await RunChromeAsync(
                    chrome,
                    "https://ipv4.icanhazip.com/",
                    $"--proxy-server=http://127.0.0.1:{FilteredPort}");
                Check("Chrome via Filtered proxy: unlisted site goes direct",
                    chromeUnlisted is not null && chromeUnlisted != viaPsiphonIp,
                    $"chrome={chromeUnlisted}, psiphon={viaPsiphonIp}");

                var chromePac = await RunChromeAsync(
                    chrome,
                    "https://api.ipify.org?format=json",
                    $"--proxy-pac-url={pacServer.Url}");
                if (chromePac is null)
                {
                    Console.WriteLine("    SKIP: Chrome --proxy-pac-url returned no result.");
                }
                else
                {
                    Check("Chrome accepts the PsiSplit PAC file", chromePac == viaPsiphonIp, $"chromePac={chromePac}, psiphon={viaPsiphonIp}");
                }
            }
            else
            {
                Console.WriteLine("    SKIP: Chrome not found, browser check skipped.");
            }
        }
        catch (Exception exception)
        {
            _failed++;
            Failures.Add($"Exception: {exception.Message}");
            Console.WriteLine($"FAIL: {exception}");
            log.Error("Self-test exception", exception);
        }
        finally
        {
            Console.WriteLine("--- cleanup ---");

            try
            {
                systemProxy?.Restore();
                systemProxy?.Dispose();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"cleanup systemProxy: {exception.Message}");
            }

            var afterRestore = ReadProxyState();
            Console.WriteLine($"System proxy after: enable={afterRestore.Enable}, server='{afterRestore.Server}', pac='{afterRestore.Pac}'");

            if (pacServer is not null)
            {
                await pacServer.DisposeAsync();
            }

            if (filteredProxy is not null)
            {
                await filteredProxy.DisposeAsync();
            }

            if (fullProxy is not null)
            {
                await fullProxy.DisposeAsync();
            }

            try
            {
                if (psiphon.CanStop)
                {
                    await psiphon.StopAsync();
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"cleanup psiphon: {exception.Message}");
            }

            await psiphon.DisposeAsync();
        }

        return Finish();
    }

    private static void TestDomainMatcher()
    {
        var rules = new List<SiteRule>
        {
            new() { Pattern = "example.com", Enabled = true },
            new() { Pattern = "*.wild.com", Enabled = true },
            new() { Pattern = "*google*", Enabled = true },
            new() { Pattern = "disabled.com", Enabled = false }
        };

        var matcher = new DomainMatcher(rules);
        Check("matcher: apex", matcher.IsMatch("example.com"));
        Check("matcher: subdomain", matcher.IsMatch("a.example.com"));
        Check("matcher: bad suffix", !matcher.IsMatch("badexample.com"));
        Check("matcher: wildcard subdomain", matcher.IsMatch("a.wild.com"));
        Check("matcher: wildcard does not match apex", !matcher.IsMatch("wild.com"));
        Check("matcher: general wildcard", matcher.IsMatch("www.google.com"));
        Check("matcher: disabled", !matcher.IsMatch("disabled.com"));
        Check("matcher: empty", !matcher.IsMatch(""));
    }

    private static void TestPacGenerator()
    {
        var pac = PacGenerator.Generate(new List<SiteRule>
        {
            new() { Pattern = "example.com", Enabled = true },
            new() { Pattern = "*.wild.com", Enabled = true },
            new() { Pattern = "disabled.com", Enabled = false }
        }, 12345);

        Check("pac: proxy statement", pac.Contains("PROXY 127.0.0.1:12345", StringComparison.Ordinal));
        Check("pac: apex condition", pac.Contains("host === \"example.com\"", StringComparison.Ordinal));
        Check("pac: subdomain condition", pac.Contains("dnsDomainIs(host, \".example.com\")", StringComparison.Ordinal));
        Check("pac: wildcard condition", pac.Contains("dnsDomainIs(host, \".wild.com\")", StringComparison.Ordinal));
        Check("pac: disabled rule absent", !pac.Contains("disabled.com", StringComparison.Ordinal));
        Check("pac: default direct", pac.Contains("return \"DIRECT\";", StringComparison.Ordinal));
    }

    private static void TestChatGptDefaults(string root)
    {
        var config = new ConfigStore(root).CreateDefault();
        var required = new[] { "chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com" };

        foreach (var domain in required)
        {
            Check(
                $"chatgpt default: {domain}",
                config.Sites.Any(rule => rule.Enabled && string.Equals(rule.Pattern, domain, StringComparison.OrdinalIgnoreCase)));
        }

        var pac = PacGenerator.Generate(config.Sites, 18081);
        Check("chatgpt pac: chatgpt.com", pac.Contains("host === \"chatgpt.com\"", StringComparison.Ordinal));
        Check("chatgpt pac: chatgpt.com subdomains", pac.Contains("dnsDomainIs(host, \".chatgpt.com\")", StringComparison.Ordinal));
        Check("chatgpt pac: oaistatic.com", pac.Contains("dnsDomainIs(host, \".oaistatic.com\")", StringComparison.Ordinal));
        Check("chatgpt pac: oaiusercontent.com", pac.Contains("dnsDomainIs(host, \".oaiusercontent.com\")", StringComparison.Ordinal));
    }

    private static async Task TestSmartProxyWithFakeUpstreamAsync()
    {
        var log = new LogService(null, entry => Console.WriteLine($"    [{entry.Level}] {entry.Message}"));
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 29090);
        listener.Start();

        var upstreamTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[1];
            while (header.Length < 64 * 1024)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 1));
                if (read == 0)
                {
                    break;
                }

                header.Append((char)buffer[0]);
                if (header.Length >= 4 &&
                    header[^4] == '\r' && header[^3] == '\n' &&
                    header[^2] == '\r' && header[^1] == '\n')
                {
                    break;
                }
            }

            Console.WriteLine("    fake upstream request:");
            Console.WriteLine(header.ToString());

            var body = Encoding.ASCII.GetBytes("OK");
            var response = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            await stream.WriteAsync(body);
        });

        SmartProxyServer? proxy = null;
        try
        {
            proxy = new SmartProxyServer(
                29080,
                TunnelMode.Full,
                29090,
                _ => true,
                () => true,
                log);
            proxy.Start();

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy("http://127.0.0.1:29080") { BypassProxyOnLocal = false },
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var text = await http.GetStringAsync("http://fake.local/hello");
            Check("SmartProxy: plain HTTP through fake upstream", text == "OK", text);

            var metrics = proxy.Metrics.Snapshot();
            Check(
                "SmartProxy: tunneled connection counted",
                metrics.TotalConnections >= 1 && metrics.TunneledConnections >= 1,
                $"total={metrics.TotalConnections}, tunneled={metrics.TunneledConnections}");
            Check(
                "SmartProxy: traffic counted in both directions",
                metrics.BytesSent > 0 && metrics.BytesReceived > 0,
                $"sent={metrics.BytesSent}, received={metrics.BytesReceived}");
        }
        catch (Exception exception)
        {
            Check("SmartProxy: plain HTTP through fake upstream", false, exception.Message);
            Console.WriteLine(exception);
        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }

            listener.Stop();

            try
            {
                await upstreamTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TestPacSyntaxWithJScript()
    {
        var cscript = Path.Combine(Environment.SystemDirectory, "cscript.exe");
        if (!File.Exists(cscript))
        {
            Console.WriteLine("    SKIP: cscript.exe not found.");
            return;
        }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "PsiSplitPacTest");
            Directory.CreateDirectory(directory);
            var pacPath = Path.Combine(directory, "test.pac");
            var harnessPath = Path.Combine(directory, "harness.js");

            File.WriteAllText(pacPath, PacGenerator.Generate(new List<SiteRule>
            {
                new() { Pattern = "example.com", Enabled = true },
                new() { Pattern = "*.wild.com", Enabled = true }
            }, 18081));

            File.WriteAllText(harnessPath, """
var fso = new ActiveXObject("Scripting.FileSystemObject");
var pac = fso.OpenTextFile(WScript.Arguments(0), 1).ReadAll();
eval(pac);
function dnsDomainIs(host, domain) {
  return host.length >= domain.length && host.substring(host.length - domain.length) === domain;
}
function shExpMatch(str, shexp) {
  var re = new RegExp('^' + shexp.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$');
  return re.test(str);
}
WScript.Echo(FindProxyForURL('https://example.com/', 'example.com'));
WScript.Echo(FindProxyForURL('https://a.example.com/', 'a.example.com'));
WScript.Echo(FindProxyForURL('https://a.wild.com/', 'a.wild.com'));
WScript.Echo(FindProxyForURL('https://other.org/', 'other.org'));
""");

            var result = RunProcess(cscript, $"//nologo \"{harnessPath}\" \"{pacPath}\"", null, TimeSpan.FromSeconds(15));
            var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .ToArray();

            Check("PAC JS: apex -> PROXY", lines.Length > 0 && lines[0].Contains("PROXY", StringComparison.Ordinal), result.StdOut);
            Check("PAC JS: subdomain -> PROXY", lines.Length > 1 && lines[1].Contains("PROXY", StringComparison.Ordinal), result.StdOut);
            Check("PAC JS: wildcard -> PROXY", lines.Length > 2 && lines[2].Contains("PROXY", StringComparison.Ordinal), result.StdOut);
            Check("PAC JS: other -> DIRECT", lines.Length > 3 && lines[3].Contains("DIRECT", StringComparison.Ordinal), result.StdOut);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"    SKIP: PAC JS test: {exception.Message}");
        }
    }

    private static async Task<string?> GetDirectIpAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            return ExtractIp(await client.GetStringAsync("https://api.ipify.org?format=json"));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"    direct IP error: {exception.Message}");
            return null;
        }
    }

    private static async Task<string?> GetIpViaProxyAsync(string proxyUrl, string url = "https://api.ipify.org?format=json")
    {
        try
        {
            var proxy = new WebProxy(proxyUrl) { BypassProxyOnLocal = false };
            using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            return ExtractIp(await client.GetStringAsync(url));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"    proxy IP error ({proxyUrl}, {url}): {exception.Message}");
            return null;
        }
    }

    private static async Task<string?> GetIpViaProxyAsyncAny(string proxyUrl, IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            var result = await GetIpViaProxyAsync(proxyUrl, url);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static async Task<int?> GetStatusViaProxyAsync(string proxyUrl, string url)
    {
        try
        {
            var proxy = new WebProxy(proxyUrl) { BypassProxyOnLocal = false };
            using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await client.GetAsync(url);
            return (int)response.StatusCode;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"    proxy status error: {exception.Message}");
            return null;
        }
    }

    private static async Task<int?> GetStatusViaProxyAnyAsync(string proxyUrl, IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            var status = await GetStatusViaProxyAsync(proxyUrl, url);
            if (status is >= 200 and < 500)
            {
                return status;
            }
        }

        return null;
    }

    private static async Task<string> GetTextDirectAsync(string url)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        return await client.GetStringAsync(url);
    }

    private static async Task<string?> RunAndExtractIpAsync(string fileName, string arguments, string? proxyUrl)
    {
        try
        {
            var process = StartProcess(fileName, arguments, proxyUrl);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                Console.WriteLine($"    process exit {process.ExitCode}: {error.Trim()}");
            }

            return ExtractIp(output);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"    process error ({fileName}): {exception.Message}");
            return null;
        }
    }

    private static async Task<string?> RunChromeAsync(string chrome, string url, string proxyArgument)
    {
        var profile = Path.Combine(Path.GetTempPath(), "PsiSplitChromeTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        try
        {
            var arguments =
                $"--headless=new --disable-gpu --no-first-run --no-default-browser-check " +
                $"--user-data-dir=\"{profile}\" {proxyArgument} --dump-dom \"{url}\"";
            var process = StartProcess(chrome, arguments, proxyUrl: null);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                Console.WriteLine("    chrome timeout");
                return null;
            }

            var output = await outputTask;
            var error = await errorTask;
            var ip = ExtractIp(output);
            if (ip is null)
            {
                Console.WriteLine($"    chrome no IP. exit={process.ExitCode}. stderr={Trim(error, 300)}");
            }

            return ip;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"    chrome error: {exception.Message}");
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch
            {
                // Chrome may still hold files; harmless in temp.
            }
        }
    }

    private static Process StartProcess(string fileName, string arguments, string? proxyUrl)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (proxyUrl is not null)
        {
            startInfo.Environment["HTTP_PROXY"] = proxyUrl;
            startInfo.Environment["HTTPS_PROXY"] = proxyUrl;
            startInfo.Environment["ALL_PROXY"] = proxyUrl;
            startInfo.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        }
        else
        {
            startInfo.Environment.Remove("HTTP_PROXY");
            startInfo.Environment.Remove("HTTPS_PROXY");
            startInfo.Environment.Remove("ALL_PROXY");
            startInfo.Environment.Remove("NO_PROXY");
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}");
    }

    private static ProcessResult RunProcess(string fileName, string arguments, string? proxyUrl, TimeSpan timeout)
    {
        using var process = StartProcess(fileName, arguments, proxyUrl);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
        }

        return new ProcessResult(output, error);
    }

    private static string? ExtractIp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b");
        if (match.Success)
        {
            return match.Value;
        }

        match = Regex.Match(text, @"\b(?:[0-9a-fA-F]{1,4}:){2,7}[0-9a-fA-F]{1,4}\b");
        return match.Success ? match.Value : null;
    }

    private static bool IsPsiphonProxy(string? server)
        => !string.IsNullOrWhiteSpace(server) &&
           (server.Contains("127.0.0.1:239", StringComparison.OrdinalIgnoreCase) ||
            server.Contains("localhost:239", StringComparison.OrdinalIgnoreCase));

    private static (int Enable, string? Server, string? Pac) ReadProxyState()
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        var enable = 0;
        try
        {
            enable = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0);
        }
        catch
        {
            // ignore
        }

        return (enable, key?.GetValue("ProxyServer") as string, key?.GetValue("AutoConfigURL") as string);
    }

    private static void Check(string name, bool condition, string? details = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"PASS  {name}" + (details is null ? "" : $"  ({details})"));
        }
        else
        {
            _failed++;
            Failures.Add(name + (details is null ? "" : $": {details}"));
            Console.WriteLine($"FAIL  {name}" + (details is null ? "" : $"  ({details})"));
        }
    }

    private static string Trim(string text, int length)
        => text.Length <= length ? text : text[..length] + "...";

    private static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"=== {_passed} passed, {_failed} failed ===");
        if (Failures.Count > 0)
        {
            Console.WriteLine("Failures:");
            foreach (var failure in Failures)
            {
                Console.WriteLine($"  - {failure}");
            }
        }

        return _failed == 0 ? 0 : 1;
    }

    private sealed record ProcessResult(string StdOut, string StdErr);
}
