using System.Text.Json.Serialization;

namespace PsiSplit.Core.Models;

public enum TunnelMode
{
    /// <summary>All requests received by the selected local proxy go through Psiphon.</summary>
    Full,

    /// <summary>Only hosts from the site list go through Psiphon.</summary>
    Filtered
}

/// <summary>
/// A user-defined application that can be launched with proxy environment
/// variables pointing at PsiSplit's local smart proxy.
/// </summary>
public sealed class AppProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TunnelMode Mode { get; set; } = TunnelMode.Full;

    [JsonIgnore]
    public string ModeLabel => Mode == TunnelMode.Full
        ? "All traffic"
        : "Listed sites only";

    [JsonIgnore]
    public bool IsBrowser =>
        string.Equals(System.IO.Path.GetFileName(Path), "chrome.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(System.IO.Path.GetFileName(Path), "msedge.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(System.IO.Path.GetFileName(Path), "firefox.exe", StringComparison.OrdinalIgnoreCase);

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }

        Name = (Name ?? string.Empty).Trim();
        Path = (Path ?? string.Empty).Trim().Trim('"');
        Arguments = (Arguments ?? string.Empty).Trim();
    }
}
