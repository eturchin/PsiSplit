using System.Diagnostics;
using System.Xml.Linq;

namespace PsiSplit.CodexLauncher;

internal sealed record CodexInstallation(string DesktopExecutable, string RuntimeExecutable, string Version)
{
    public static CodexInstallation Find()
    {
        // Resolve on each launch, rather than saving a versioned WindowsApps path.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var candidates = Directory.EnumerateDirectories(directory, "OpenAI.Codex_*_x64__2p2nqsd0c76g0")
            .Select(path => (Path: path, Version: ParseVersion(path)))
            .OrderByDescending(item => item.Version);
        foreach (var item in candidates)
        {
            try
            {
                var manifest = XDocument.Load(Path.Combine(item.Path, "AppxManifest.xml"));
                if (manifest.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")
                    ?.Attribute("Name")?.Value != "OpenAI.Codex") continue;
                var desktop = Path.Combine(item.Path, "app", "ChatGPT.exe");
                var runtime = Path.Combine(item.Path, "app", "resources", "codex.exe");
                if (File.Exists(desktop) && File.Exists(runtime))
                    return new(desktop, runtime, item.Version.ToString());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // An incomplete/inaccessible update must not hide an older usable package.
            }
        }
        throw new FileNotFoundException("The Windows x64 Codex app was not found. Install Codex before using this launcher.");
    }

    public static bool IsCodexRunning()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Refuse a new launch if an existing instance cannot be inspected.
                    if (!process.HasExited) return true;
                }
            }
        }
        return false;
    }

    private static Version ParseVersion(string path)
        => System.Version.TryParse(Path.GetFileName(path).Split('_').ElementAtOrDefault(1), out var version)
            ? version : new Version();
}
