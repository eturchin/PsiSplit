using System.Globalization;

namespace PsiSplit.Core.Models;

/// <summary>
/// A single domain rule. Examples:
///   example.com      -> example.com and all subdomains
///   *.example.com    -> subdomains only
///   *google*         -> wildcard match
/// </summary>
public sealed class SiteRule
{
    public string Pattern { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
        Pattern = NormalizePattern(Pattern);
    }

    public static string NormalizePattern(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var value = input.Trim();

        // Accept full URLs pasted from the browser.
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                value = uri.Host;
            }
        }
        else
        {
            // Strip path/query and port from "example.com:443/path".
            var slash = value.IndexOf('/');
            if (slash >= 0)
            {
                value = value[..slash];
            }

            var question = value.IndexOf('?');
            if (question >= 0)
            {
                value = value[..question];
            }
        }

        value = value.Trim().TrimEnd('.').ToLowerInvariant();

        if (value.StartsWith(".", StringComparison.Ordinal))
        {
            value = "*" + value;
        }

        // A bare "*" means "everything". Keep it.
        if (value == "*" || value == "*.")
        {
            return "*";
        }

        if (value.StartsWith("*.", StringComparison.Ordinal) && value.Length <= 2)
        {
            return "*";
        }

        // Convert IDN labels to punycode when there is no wildcard. PAC and
        // Proxy CONNECT both see the ASCII form of the host name.
        if (!value.Contains('*', StringComparison.Ordinal))
        {
            try
            {
                value = new IdnMapping().GetAscii(value);
            }
            catch (ArgumentException)
            {
                // Keep the original value; it will simply not match.
            }
        }

        return value;
    }
}
