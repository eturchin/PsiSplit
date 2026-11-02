using System.Diagnostics;

namespace PsiSplit.Core.Services;

public static class ProcessTree
{
    /// <summary>
    /// Kills a process and all of its children. psiphon3.exe spawns
    /// psiphon-tunnel-core.exe, so killing only the wrapper is not enough.
    /// </summary>
    public static void Kill(int processId, LogService? log = null)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            log?.Info($"Process {processId} stopped.");
            return;
        }
        catch (Exception exception)
        {
            log?.Warn($"Failed to stop process {processId} via Process.Kill: {exception.Message}");
        }

        try
        {
            using var taskkill = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/PID {processId} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            taskkill?.WaitForExit(5000);
        }
        catch (Exception exception)
        {
            log?.Warn($"taskkill for {processId} also failed: {exception.Message}");
        }
    }

    public static bool IsRunning(string processName)
        => Process.GetProcessesByName(processName).Any(process =>
        {
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        });

    public static bool AnyPsiphonRunning()
        => IsRunning("psiphon3") || IsRunning("psiphon-tunnel-core");
}
