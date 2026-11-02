using System.Text.Json;

namespace PsiSplit.Core.Services;

internal readonly record struct PsiphonNotice(string Type, int Value, string Message)
{
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

            notice = new PsiphonNotice(name, value, message);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
