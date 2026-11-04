using System.Text;
using System.Text.Json;
using PsiSplit.Core.Models;
using PsiSplit.Core.Services;

internal static class ConnectionFeatureTests
{
    public static async Task RunLocalAsync(string root, Action<string, bool, string?> check)
    {
        var rates = new TransferRateSampler();
        check("Rate starts from a baseline", rates.Sample(100, 200, TimeSpan.Zero) == default, null);
        var measured = rates.Sample(1124, 4296, TimeSpan.FromSeconds(2));
        check("Speed uses actual elapsed time for upload/download", measured.BytesSentPerSecond == 512 && measured.BytesReceivedPerSecond == 2048, null);
        check("Zero elapsed time preserves a valid rate", rates.Sample(2000, 6000, TimeSpan.FromSeconds(2)) == measured, null);
        var next = rates.Sample(3172, 8392, TimeSpan.FromSeconds(3));
        check("Extra UI refreshes do not consume transferred bytes", next.BytesSentPerSecond == 2048 && next.BytesReceivedPerSecond == 4096, null);
        check("Idle traffic returns to zero", rates.Sample(3172, 8392, TimeSpan.FromSeconds(4)) == default, null);
        check("Restarted counters do not produce negative speed", rates.Sample(2, 4, TimeSpan.FromSeconds(5)) == default, null);
        check("Speed resumes after a counter reset", rates.Sample(10, 20, TimeSpan.FromSeconds(6)) == new TransferRateSnapshot(8, 16), null);
        rates.Reset();
        check("An explicit reset forgets previous session traffic", rates.Sample(100000, 200000, TimeSpan.FromSeconds(7)) == default, null);

        var store = new ConfigStore(Path.Combine(root, "country-preference"));
        var config = JsonSerializer.Deserialize<AppConfig>("{}")!;
        check("Older profiles preserve Netherlands as their exit preference", config.EgressRegion == "NL", null);
        config.EgressRegion = " de ";
        store.Save(config);
        check("Country preference round-trips and normalizes", store.Load().EgressRegion == "DE" && config.Clone().EgressRegion == "DE", null);
        config.EgressRegion = "";
        store.Save(config);
        check("Automatic country selection persists as an empty code", store.Load().EgressRegion == "", null);
        config.EgressRegion = "not-a-country";
        config.Normalize();
        check("Invalid saved country codes fall back safely", config.EgressRegion == "NL", null);

        var automatic = PsiphonBundle.CreateConfig(Path.Combine(root, "automatic"), "");
        var germany = PsiphonBundle.CreateConfig(Path.Combine(root, "germany"), "DE");
        check("Automatic selection overrides the bundled exit region", automatic["EgressRegion"]!.GetValue<string>() == "", null);
        check("Explicit country is passed into the real core config", germany["EgressRegion"]!.GetValue<string>() == "DE", null);
        check("Transfer notices and available-country reporting are enabled without diagnostics",
            germany["EmitBytesTransferred"]!.GetValue<bool>() && !germany["DisableServerEntriesReporter"]!.GetValue<bool>() && !germany["EmitDiagnosticNotices"]!.GetValue<bool>(), null);

        var regionNotice = PsiphonNotice.TryParse("""{"noticeType":"AvailableEgressRegions","data":{"regions":["NL","DE","NL"]}}""", out var regions);
        check("Core countries are deduplicated and sorted", regionNotice && regions.Regions.SequenceEqual(new[] { "DE", "NL" }), null);
        check("An empty core country report is supported", PsiphonNotice.TryParse("""{"noticeType":"AvailableEgressRegions","data":{"regions":null}}""", out var empty) && empty.Regions.Count == 0, null);
        check("Connected country comes from its own notice", PsiphonNotice.TryParse("""{"noticeType":"ConnectedServerRegion","data":{"serverRegion":"DE"}}""", out var connected) && connected.Region == "DE", null);
        check("64-bit transfer counts are preserved", PsiphonNotice.TryParse("""{"noticeType":"BytesTransferred","data":{"sent":4294967296,"received":8589934592}}""", out var bytes) && bytes.BytesSent == 4294967296 && bytes.BytesReceived == 8589934592, null);
        foreach (var line in new[]
        {
            """{"noticeType":"AvailableEgressRegions","data":{"regions":"DE"}}""",
            """{"noticeType":"AvailableEgressRegions","data":{"regions":["DE",99]}}""",
            """{"noticeType":"AvailableEgressRegions","data":{"regions":["INVALID"]}}""",
            """{"noticeType":"ConnectedServerRegion","data":{"serverRegion":{}}}""",
            """{"noticeType":"BytesTransferred","data":{"sent":-1,"received":10}}""",
            """{"noticeType":"BytesTransferred","data":{"sent":1.5,"received":10}}""",
            """{"noticeType":"BytesTransferred","data":{"sent":1}}"""
        }) check("Malformed country/traffic notices are ignored", !PsiphonNotice.TryParse(line, out _), null);

        var entry = Convert.ToHexString(Encoding.UTF8.GetBytes("ip port secret cert {\"region\":\"NL\"}"));
        var entryDe = Convert.ToHexString(Encoding.UTF8.GetBytes("ip port secret cert {\"region\":\"DE\"}"));
        using var seed = new StringReader(entry + "\ninvalid\n" + entryDe + "\n" + entry);
        check("Offline country discovery follows the upstream seed encoding", PsiphonRegionCatalog.ReadSeedRegions(seed).SequenceEqual(new[] { "DE", "NL" }), null);
        check("The actual bundle provides valid offline countries", PsiphonRegionCatalog.BundledRegions.Count > 0 && PsiphonRegionCatalog.BundledRegions.All(code => code.Length == 2) && PsiphonRegionCatalog.BundledRegions.Contains("NL"), null);

        var cacheRoot = Path.Combine(root, "region-cache");
        Directory.CreateDirectory(cacheRoot);
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, "regions.json"), JsonSerializer.Serialize(new
        {
            Regions = new[] { "NL", "DE", "NL" }, ReportedAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
        }));
        await using (var cached = new PsiphonController(new LogService(), cacheRoot, "DE"))
        {
            check("Saved countries load read-only as a previous report", cached.RegionReport.FromCache && cached.RegionReport.Codes.SequenceEqual(new[] { "DE", "NL" }) && cached.State == PsiphonState.Stopped, null);
        }
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, "regions.json"), "invalid cache");
        await using (var invalid = new PsiphonController(new LogService(), cacheRoot))
        {
            check("A broken country cache cannot prevent startup", invalid.RegionReport.ReportedAtUtc is null, null);
        }
    }
}
