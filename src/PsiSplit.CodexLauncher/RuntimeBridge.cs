using System.Diagnostics;
using PsiSplit.Core.Services;

namespace PsiSplit.CodexLauncher;

internal static class RuntimeBridge
{
    public static async Task<int> RunAsync(string runtime, string[] arguments)
    {
        using var job = new OwnedProcessJob();
        using var process = Process.Start(CodexProxyLaunch.CreateRuntimeStartInfo(runtime, arguments))
            ?? throw new InvalidOperationException("The Codex runtime could not be started.");
        try { job.Add(process); }
        catch
        {
            if (!HasExited(process)) process.Kill(entireProcessTree: true);
            throw;
        }

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var error = Console.OpenStandardError();
        using var cts = new CancellationTokenSource();
        var stdinTask = CopyInputAsync(input, process, cts.Token);
        var stdoutTask = CopyOutputAsync(process.StandardOutput.BaseStream, output);
        var stderrTask = CopyOutputAsync(process.StandardError.BaseStream, error);

        var childExitTask = process.WaitForExitAsync(cts.Token);
        var ownerExitTask = WaitForOwnerExitAsync(cts.Token);
        var completed = await Task.WhenAny(childExitTask, ownerExitTask);
        if (completed == ownerExitTask)
        {
            // The launcher is gone, so no Codex window can consume this runtime.
            // Do not leave the bridge or its native child behind.
            if (!HasExited(process))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                // Disposing the job below terminates the remaining process tree.
                return 1;
            }
        }
        else
        {
            await process.WaitForExitAsync();
        }

        cts.Cancel();

        // The child can hand its pipes to a grandchild. Wait only briefly for
        // the last buffered bytes; otherwise this bridge would stay alive after
        // the runtime has already exited.
        try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        // Do not wait for a synchronous Windows stdin read after the child exits.
        // The streams and process are disposed when this short-lived bridge exits.
        _ = stdinTask;
        return process.ExitCode;
    }

    private static Task WaitForOwnerExitAsync(CancellationToken cancellationToken)
    {
        var value = Environment.GetEnvironmentVariable(CodexProxyLaunch.LauncherEnvironmentVariable);
        if (!int.TryParse(value, out var ownerId) || ownerId <= 0 || ownerId == Environment.ProcessId)
            return Task.Delay(Timeout.Infinite, CancellationToken.None);

        Process owner;
        try { owner = Process.GetProcessById(ownerId); }
        catch (ArgumentException)
        {
            return Task.CompletedTask;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Access was denied: do not kill a possibly valid runtime.
            return Task.Delay(Timeout.Infinite, CancellationToken.None);
        }

        return WaitForExitAsync(owner, cancellationToken);
    }

    private static async Task WaitForExitAsync(Process owner, CancellationToken cancellationToken)
    {
        try { await owner.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { owner.Dispose(); }
    }

    private static async Task CopyInputAsync(Stream input, Process process, CancellationToken cancellationToken)
    {
        try
        {
            await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task CopyOutputAsync(Stream input, Stream output)
    {
        try
        {
            await input.CopyToAsync(output);
            await output.FlushAsync();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or NotSupportedException)
        {
            // The parent closed its pipe first. The bridge still has to exit.
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }
}
