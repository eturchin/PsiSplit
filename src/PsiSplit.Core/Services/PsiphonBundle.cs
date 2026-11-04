using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsiSplit.Core.Services;

/// <summary>Self-contained, unmodified Psiphon engine and public network bootstrap.</summary>
public static class PsiphonBundle
{
    private const string ResourcePrefix = "PsiSplit.Psiphon.";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly BundleManifest Manifest = ReadManifest();

    public static string Version => Manifest.Version;
    public static string CoreHash => Manifest.Files["psiphon-tunnel-core.exe"];

    public static JsonObject CreateConfig(string dataDirectory, string egressRegion = PsiphonRegionCatalog.DefaultRegion)
    {
        using var resource = OpenResource("bootstrap.json");
        var config = JsonNode.Parse(resource)?.AsObject()
            ?? throw new InvalidDataException("The bundled Psiphon bootstrap is invalid.");

        // Only public bootstrap fields are embedded. No registry settings,
        // migration paths, account tokens, or opaque AdditionalParameters.
        config["DataRootDirectory"] = Path.GetFullPath(dataDirectory);
        config["LocalHttpProxyPort"] = 0;
        config["LocalSocksProxyPort"] = 0;
        config["ListenInterface"] = ""; // Psiphon default: loopback only.
        config["EnableUpgradeDownload"] = false;
        config["EnableFeedbackUpload"] = false;
        config["EmitDiagnosticNotices"] = false;
        config["EmitBytesTransferred"] = true;
        config["EgressRegion"] = PsiphonRegionCatalog.NormalizeCode(egressRegion)
            ?? throw new ArgumentException("Choose a two-letter exit country code or Automatic.", nameof(egressRegion));
        config["DisableServerEntriesReporter"] = false;
        config["EstablishTunnelTimeoutSeconds"] = 145;
        return config;
    }

    public static async Task<string> ExtractAsync(string directory, CancellationToken cancellationToken = default)
    {
        // Versioned by engine hash so an update cannot overwrite a running core.
        var targetDirectory = Path.Combine(Path.GetFullPath(directory), CoreHash);
        Directory.CreateDirectory(targetDirectory);
        foreach (var (file, expectedHash) in Manifest.Files)
        {
            var target = Path.Combine(targetDirectory, file);
            if (await HasHashAsync(target, expectedHash, cancellationToken))
            {
                continue;
            }

            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var source = OpenResource(file))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(output, cancellationToken);
                }

                if (!await HasHashAsync(temp, expectedHash, cancellationToken))
                {
                    throw new InvalidDataException($"Bundled Psiphon file failed its integrity check: {file}");
                }

                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        return Path.Combine(targetDirectory, "psiphon-tunnel-core.exe");
    }

    private static async Task<bool> HasHashAsync(string path, string hash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)) == hash;
    }

    private static Stream OpenResource(string file)
        => typeof(PsiphonBundle).Assembly.GetManifestResourceStream(ResourcePrefix + file)
            ?? throw new FileNotFoundException($"The bundled Psiphon engine is incomplete: {file}. Rebuild PsiSplit.");

    private static BundleManifest ReadManifest()
    {
        using var resource = OpenResource("manifest.json");
        return JsonSerializer.Deserialize<BundleManifest>(resource, JsonOptions)
            ?? throw new InvalidDataException("The bundled Psiphon manifest is invalid.");
    }

    private sealed class BundleManifest
    {
        public string Version { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new();
    }
}
