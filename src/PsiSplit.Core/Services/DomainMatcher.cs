using PsiSplit.Core.Models;

namespace PsiSplit.Core.Services;

/// <summary>
/// Immutable matcher used by both the proxy and the PAC generator. A rule
/// without a wildcard matches the apex domain and every subdomain.
/// </summary>
public sealed class DomainMatcher
{
    private readonly CompiledRule[] _rules;

    public DomainMatcher(IEnumerable<SiteRule> rules)
    {
        _rules = rules
            .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(rule => new CompiledRule(rule.Pattern))
            .ToArray();
    }

    public bool IsEmpty => _rules.Length == 0;

    public bool IsMatch(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var rule in _rules)
        {
            if (rule.IsMatch(host))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CompiledRule
    {
        private readonly string _pattern;
        private readonly RuleKind _kind;
        private readonly string? _suffix;
        private readonly System.Text.RegularExpressions.Regex? _regex;

        public CompiledRule(string pattern)
        {
            _pattern = pattern;

            if (pattern == "*")
            {
                _kind = RuleKind.Everything;
                return;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                _kind = RuleKind.SubdomainsOnly;
                _suffix = pattern[1..];
                return;
            }

            if (pattern.Contains('*', StringComparison.Ordinal))
            {
                _kind = RuleKind.Wildcard;
                var escaped = System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*", StringComparison.Ordinal);
                _regex = new System.Text.RegularExpressions.Regex(
                    "^" + escaped + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.Compiled |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                return;
            }

            _kind = RuleKind.DomainAndSubdomains;
        }

        public bool IsMatch(string host)
        {
            return _kind switch
            {
                RuleKind.Everything => true,
                RuleKind.SubdomainsOnly => host.Length > _suffix!.Length &&
                                           host.EndsWith(_suffix, StringComparison.Ordinal),
                RuleKind.Wildcard => _regex!.IsMatch(host),
                _ => host.Equals(_pattern, StringComparison.Ordinal) ||
                     host.EndsWith("." + _pattern, StringComparison.Ordinal)
            };
        }

        private enum RuleKind
        {
            DomainAndSubdomains,
            SubdomainsOnly,
            Wildcard,
            Everything
        }
    }
}
