namespace PsiSplit.Core.Models;

public sealed class AppConfig
{
    /// <summary>Legacy setting retained for config compatibility; the bundled engine ignores it.</summary>
    public string PsiphonPath { get; set; } = string.Empty;

    /// <summary>Local proxy for applications that should send all traffic to Psiphon.</summary>
    public int FullProxyPort { get; set; } = 18080;

    /// <summary>Local proxy that sends only matching domains to Psiphon.</summary>
    public int FilteredProxyPort { get; set; } = 18081;

    /// <summary>Local HTTP server that serves the PAC file.</summary>
    public int PacPort { get; set; } = 18082;

    /// <summary>Legacy setting; the bundled engine now allocates its own free loopback ports.</summary>
    public int PsiphonHttpProxyPort { get; set; } = 23934;

    public bool StartPsiphonOnLaunch { get; set; }
    public bool ApplyPacOnStart { get; set; } = true;
    public bool StopPsiphonOnExit { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>When true, cmd/PowerShell launched from the app use the site list instead of full tunnel.</summary>
    public bool TunnelShellsThroughSiteFilter { get; set; }

    public List<SiteRule> Sites { get; set; } = new();
    public List<AppProfile> Apps { get; set; } = new();

    public void Normalize()
    {
        PsiphonPath = (PsiphonPath ?? string.Empty).Trim().Trim('"');
        FullProxyPort = NormalizePort(FullProxyPort, 18080);
        FilteredProxyPort = NormalizePort(FilteredProxyPort, 18081);
        PacPort = NormalizePort(PacPort, 18082);
        PsiphonHttpProxyPort = NormalizePort(PsiphonHttpProxyPort, 23934);

        Sites ??= new List<SiteRule>();
        foreach (var rule in Sites)
        {
            rule.Normalize();
        }

        Sites.RemoveAll(rule => string.IsNullOrWhiteSpace(rule.Pattern));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Sites.RemoveAll(rule => !seen.Add(rule.Pattern));

        Apps ??= new List<AppProfile>();
        foreach (var app in Apps)
        {
            app.Normalize();
        }

        Apps.RemoveAll(app => string.IsNullOrWhiteSpace(app.Name) || string.IsNullOrWhiteSpace(app.Path));
    }

    public AppConfig Clone()
    {
        var clone = new AppConfig
        {
            PsiphonPath = PsiphonPath,
            FullProxyPort = FullProxyPort,
            FilteredProxyPort = FilteredProxyPort,
            PacPort = PacPort,
            PsiphonHttpProxyPort = PsiphonHttpProxyPort,
            StartPsiphonOnLaunch = StartPsiphonOnLaunch,
            ApplyPacOnStart = ApplyPacOnStart,
            StopPsiphonOnExit = StopPsiphonOnExit,
            MinimizeToTray = MinimizeToTray,
            TunnelShellsThroughSiteFilter = TunnelShellsThroughSiteFilter,
            Sites = Sites.Select(s => new SiteRule { Pattern = s.Pattern, Enabled = s.Enabled }).ToList(),
            Apps = Apps.Select(a => new AppProfile
            {
                Id = a.Id,
                Name = a.Name,
                Path = a.Path,
                Arguments = a.Arguments,
                Mode = a.Mode
            }).ToList()
        };

        return clone;
    }

    private static int NormalizePort(int value, int fallback)
        => value is > 0 and <= 65535 ? value : fallback;
}
