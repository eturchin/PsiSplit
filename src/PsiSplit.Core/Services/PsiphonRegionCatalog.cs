using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PsiSplit.Core.Services;

public static class PsiphonRegionCatalog
{
    public const string DefaultRegion = "NL";
    private static readonly Lazy<IReadOnlyList<string>> SeedRegions = new(ReadBundledRegions);
    public static IReadOnlyList<string> BundledRegions => SeedRegions.Value;

    // An empty code means automatic choice across all countries.
    public static string? NormalizeCode(string? code)
    {
        var value = (code ?? "").Trim().ToUpperInvariant();
        if (value.Length == 0) return "";
        return value.Length == 2 && value.All(c => c is >= 'A' and <= 'Z') ? value : null;
    }

    public static string DisplayName(string code)
    {
        if (code.Length == 0) return "Automatic (best available)";
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return code; }
    }

    private static IReadOnlyList<string> ReadBundledRegions()
    {
        using var stream = typeof(PsiphonRegionCatalog).Assembly.GetManifestResourceStream("PsiSplit.Psiphon.server_list.dat")
            ?? throw new FileNotFoundException("Bundled Psiphon seed list is missing.");
        using var reader = new StreamReader(stream);
        return ReadSeedRegions(reader);
    }

    internal static IReadOnlyList<string> ReadSeedRegions(TextReader reader)
    {
        var regions = new SortedSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromHexString(line.Trim()));
                // Upstream encoding has four space-delimited legacy fields,
                // followed by the JSON server entry. Read only its public region.
                var offset = 0;
                for (var i = 0; i < 4 && offset >= 0; i++)
                {
                    var separator = decoded.IndexOf(' ', offset);
                    offset = separator < 0 ? -1 : separator + 1;
                }
                if (offset < 0) continue;
                using var entry = JsonDocument.Parse(decoded[offset..]);
                if (entry.RootElement.ValueKind == JsonValueKind.Object &&
                    entry.RootElement.TryGetProperty("region", out var region) && region.ValueKind == JsonValueKind.String &&
                    NormalizeCode(region.GetString()) is { Length: 2 } code)
                {
                    regions.Add(code);
                }
            }
            catch (Exception exception) when (exception is FormatException or JsonException) { }
        }
        return Array.AsReadOnly(regions.ToArray());
    }
}

public sealed record EgressRegionReport(IReadOnlyList<string> Codes, DateTimeOffset? ReportedAtUtc, bool FromCache)
{
    public static EgressRegionReport Empty { get; } = new(Array.Empty<string>(), null, false);
}
