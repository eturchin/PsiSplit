using PsiSplit.Core.Services;

internal static class CodexLauncherTests
{
    public static void Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Codex launcher checks");
        var desktop = Path.Combine(directory, "ChatGPT.exe");
        var runtime = Path.Combine(directory, "resources", "codex.exe");
        var launcher = Path.Combine(directory, "CodexProxy.exe");
        var originalProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var originalCli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        var start = CodexProxyLaunch.CreateDesktopStartInfo(desktop, runtime, launcher, 32345);
        check("Codex desktop uses its own explicit loopback proxy", !start.UseShellExecute &&
            start.ArgumentList.Contains("--proxy-server=http://127.0.0.1:32345"), null);
        check("Codex native requests receive proxy environment variables", new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }
            .All(key => start.Environment[key] == "http://127.0.0.1:32345"), null);
        check("Codex loopback communication bypasses the tunnel", start.Environment["NO_PROXY"]!.Contains("127.0.0.1") &&
            start.ArgumentList.Contains("--proxy-bypass-list=localhost;127.0.0.1;[::1]"), null);
        check("Codex runtime override is limited to the launched child", start.Environment["CODEX_CLI_PATH"] == launcher &&
            start.Environment[CodexProxyLaunch.RuntimeEnvironmentVariable] == runtime &&
            Environment.GetEnvironmentVariable("HTTPS_PROXY") == originalProxy &&
            Environment.GetEnvironmentVariable("CODEX_CLI_PATH") == originalCli, null);
        check("Codex child tree remembers the owning launcher", start.Environment[CodexProxyLaunch.LauncherEnvironmentVariable] ==
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

        var bridge = CodexProxyLaunch.CreateRuntimeStartInfo(runtime, new[] { "app-server", "-c", "some.setting=\"value with spaces\"" });
        check("Codex bridge overrides only native system-proxy discovery", bridge.ArgumentList.Take(2)
            .SequenceEqual(new[] { "-c", "features.respect_system_proxy=false" }), null);
        check("Codex bridge preserves argument boundaries and all three streams", bridge.RedirectStandardInput &&
            bridge.RedirectStandardOutput && bridge.RedirectStandardError && bridge.ArgumentList.Skip(2)
                .SequenceEqual(new[] { "app-server", "-c", "some.setting=\"value with spaces\"" }), null);
        check("Codex bridge cannot recursively override itself", !bridge.Environment.ContainsKey("CODEX_CLI_PATH") &&
            !bridge.Environment.ContainsKey(CodexProxyLaunch.RuntimeEnvironmentVariable) &&
            !bridge.Environment.ContainsKey(CodexProxyLaunch.LauncherEnvironmentVariable), null);
        var rejected = 0;
        foreach (var port in new[] { -1, 0, 65536 })
        {
            try { CodexProxyLaunch.CreateDesktopStartInfo(desktop, runtime, launcher, port); }
            catch (ArgumentOutOfRangeException) { rejected++; }
        }
        check("Codex launcher rejects missing or invalid proxy ports", rejected == 3, null);
    }
}
