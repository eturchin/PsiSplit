using System.Text.Json;

namespace PsiSplit.Core.Services;

internal readonly record struct PsiphonNotice(string Type, int Value, string Message)
{
    public IReadOnlyList<string> Regions { get; init; } = Array.Empty<string>();
    public string Region { get; init; } = "";
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }

    public static bool TryParse(string line, out PsiphonNotice notice)
    {
        notice = default;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("noticeType", out var type) || type.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var name = type.GetString()!;
            var value = 0;
            var message = "";
            if (name is "ListeningHttpProxyPort" or "ListeningSocksProxyPort" or "Tunnels")
            {
                if (!data.TryGetProperty(name == "Tunnels" ? "count" : "port", out var number) ||
                    number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out value) ||
                    (name == "Tunnels" ? value < 0 : value is < 1 or > 65535))
                {
                    return false;
                }
            }
            if (data.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
            {
                message = text.GetString() ?? "";
                if (message.Length > 1000) message = message[..1000];
            }

            var parsed = new PsiphonNotice(name, value, message);
            if (name == "AvailableEgressRegions")
            {
                if (!data.TryGetProperty("regions", out var regions) ||
                    regions.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null)) return false;
                var codes = new SortedSet<string>(StringComparer.Ordinal);
                if (regions.ValueKind == JsonValueKind.Array)
                {
                    if (regions.GetArrayLength() > 300) return false;
                    foreach (var region in regions.EnumerateArray())
                    {
                        if (region.ValueKind != JsonValueKind.String ||
                            PsiphonRegionCatalog.NormalizeCode(region.GetString()) is not { Length: 2 } code) return false;
                        codes.Add(code);
                    }
                }
                parsed = parsed with { Regions = Array.AsReadOnly(codes.ToArray()) };
            }
            else if (name == "ConnectedServerRegion")
            {
                if (!data.TryGetProperty("serverRegion", out var region) || region.ValueKind != JsonValueKind.String ||
                    PsiphonRegionCatalog.NormalizeCode(region.GetString()) is not { Length: 2 } code) return false;
                parsed = parsed with { Region = code };
            }
            else if (name == "BytesTransferred")
            {
                if (!data.TryGetProperty("sent", out var sent) || !data.TryGetProperty("received", out var received) ||
                    sent.ValueKind != JsonValueKind.Number || received.ValueKind != JsonValueKind.Number ||
                    !sent.TryGetInt64(out var sentBytes) || !received.TryGetInt64(out var receivedBytes) ||
                    sentBytes < 0 || receivedBytes < 0) return false;
                parsed = parsed with { BytesSent = sentBytes, BytesReceived = receivedBytes };
            }

            notice = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
