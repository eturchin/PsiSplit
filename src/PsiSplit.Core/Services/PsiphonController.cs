using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PsiSplit.Core.Services;

public enum PsiphonState { Stopped, Starting, Connected, Stopping, Failed }

/// <summary>
/// Runs the bundled tunnel core without psiphon3.exe, its registry or datastore.
/// Ownership is tied to a Process handle, never a process-name scan.
/// </summary>
public sealed class PsiphonController : IAsyncDisposable
{
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _processExecutable;
    private FileStream? _lease;
    private int _httpPort;
    private int _socksPort;
    private int _tunnelCount;
    private string _lastError = "";
    private volatile PsiphonState _state = PsiphonState.Stopped;
    private CancellationTokenSource? _watchCts;
    private Task? _noticeTask;
    private Task? _healthTask;
    private bool _disposed;

    public PsiphonController(LogService log, string? runtimeDirectory = null)
    {
        _log = log;
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PsiSplit", "Tunnel"));
    }

    public string RuntimeDirectory { get; }
    public PsiphonState State => _state;
    public int HttpProxyPort => Volatile.Read(ref _httpPort);
    public int SocksProxyPort => Volatile.Read(ref _socksPort);
    public int? ProcessId => _process?.Id;
    public bool CanStop => _process is not null;
    private string SessionPath => Path.Combine(RuntimeDirectory, "session.json");
    private string NoticePath => Path.Combine(RuntimeDirectory, "notices.jsonl");

    public event Action<PsiphonState>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var attemptedStart = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == PsiphonState.Connected && _process is { HasExited: false } &&
                await ProbeAsync(HttpProxyPort, cancellationToken)) return;

            attemptedStart = true;
            SetState(PsiphonState.Starting);
            await StopWatchingAsync();
            await KillOwnedProcessAsync();
            Directory.CreateDirectory(RuntimeDirectory);
            _lease ??= new FileStream(Path.Combine(RuntimeDirectory, "controller.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var executable = await PsiphonBundle.ExtractAsync(Path.Combine(RuntimeDirectory, "bin"), cancellationToken);
            if (TryAttachOwnedSession())
            {
                if (string.Equals(_processExecutable, executable, StringComparison.OrdinalIgnoreCase) &&
                    await ProbeAsync(HttpProxyPort, cancellationToken))
                {
                    _log.Info($"Reattached to the PsiSplit tunnel core, PID {_process!.Id}.");
                    SetState(PsiphonState.Connected);
                    StartWatching(readExistingNotices: false);
                    return;
                }
                await KillOwnedProcessAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            _httpPort = _socksPort = _tunnelCount = 0;
            _lastError = "";
            var dataDirectory = Path.Combine(RuntimeDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            var configPath = Path.Combine(RuntimeDirectory, "psiphon.config");
            await File.WriteAllTextAsync(configPath, PsiphonBundle.CreateConfig(dataDirectory).ToJsonString(), cancellationToken);
            await File.WriteAllTextAsync(NoticePath, "", cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = RuntimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(configPath);
            startInfo.ArgumentList.Add("--serverList");
            startInfo.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(executable)!, "server_list.dat"));
            // File notices also permit intentional detach without broken pipes.
            startInfo.ArgumentList.Add("--notices");
            startInfo.ArgumentList.Add(NoticePath);
            _processExecutable = executable;
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the bundled tunnel core.");
            SaveSession();
            _log.Info($"Bundled Psiphon tunnel core started without a window, PID {_process.Id}.");
            StartWatching(readExistingNotices: true);

            if (!await WaitForReadyAsync(TimeSpan.FromSeconds(150), cancellationToken))
                throw new TimeoutException("The tunnel core did not connect within 150 seconds. Check the internet connection and the log.");

            SaveSession();
            SetState(PsiphonState.Connected);
        }
        catch (Exception exception)
        {
            if (!attemptedStart) throw; // A cancelled readiness probe must not stop an established session.
            _log.Error("Failed to start the tunnel core", exception);
            try
            {
                await StopWatchingAsync();
                await KillOwnedProcessAsync();
            }
            finally
            {
                if (_process is null) ReleaseLease();
                SetState(exception is OperationCanceledException && _process is null ? PsiphonState.Stopped : PsiphonState.Failed);
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SetState(PsiphonState.Stopping);
            await StopWatchingAsync();
            await KillOwnedProcessAsync();
            ReleaseLease();
            SetState(PsiphonState.Stopped);
        }
        catch
        {
            SetState(PsiphonState.Failed);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
                throw new InvalidOperationException($"Tunnel core exited with code {_process?.ExitCode}. {_lastError}");

            // Listeners exist before the tunnel. Require both its notice and CONNECT.
            if (Volatile.Read(ref _tunnelCount) > 0 && HttpProxyPort > 0 &&
                await ProbeAsync(HttpProxyPort, cancellationToken)) return true;
            await Task.Delay(250, cancellationToken);
        }
        return false;
    }

    public static async Task<bool> ProbeAsync(int httpPort, CancellationToken cancellationToken = default)
    {
        if (httpPort is < 1 or > 65535) return false;
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(IPAddress.Loopback, httpPort, timeout.Token);
            using var stream = client.GetStream();
            var request = "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\nProxy-Connection: keep-alive\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            var response = await HttpHeaderReader.ReadHeaderAsync(stream, 16 * 1024, timeout.Token);
            return response is not null && HttpHeaderReader.ParseStatusCode(response) == 200;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { return false; }
    }

    private void StartWatching(bool readExistingNotices)
    {
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        _noticeTask = WatchNoticesAsync(readExistingNotices, token);
        _healthTask = HealthLoopAsync(token);
    }

    private async Task StopWatchingAsync()
    {
        if (_watchCts is null) return;
        await _watchCts.CancelAsync();
        try { await Task.WhenAll(_noticeTask ?? Task.CompletedTask, _healthTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        finally
        {
            _watchCts.Dispose();
            _watchCts = null;
            _noticeTask = _healthTask = null;
        }
    }

    private async Task WatchNoticesAsync(bool readExisting, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(NoticePath, FileMode.OpenOrCreate, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            if (!readExisting) file.Seek(0, SeekOrigin.End);
            using var reader = new StreamReader(file);
            var pending = new StringBuilder();
            var buffer = new char[4096];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (PsiphonNotice.TryParse(pending.ToString(), out var notice)) HandleNotice(notice);
                        pending.Clear();
                    }
                    else if (pending.Length < 64 * 1024) pending.Append(buffer[i]);
                }
                if (count == 0) await Task.Delay(150, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Error("Reading tunnel core notices", exception);
        }
    }

    private void HandleNotice(PsiphonNotice notice)
    {
        switch (notice.Type)
        {
            case "ListeningHttpProxyPort":
                Volatile.Write(ref _httpPort, notice.Value);
                _log.Info($"Tunnel core HTTP proxy: 127.0.0.1:{notice.Value}");
                break;
            case "ListeningSocksProxyPort":
                Volatile.Write(ref _socksPort, notice.Value);
                _log.Info($"Tunnel core SOCKS proxy: 127.0.0.1:{notice.Value}");
                break;
            case "Tunnels":
                Volatile.Write(ref _tunnelCount, notice.Value);
                _log.Info(notice.Value > 0 ? "Tunnel core established a tunnel." : "Tunnel core is connecting...");
                break;
            case "Error":
                _lastError = notice.Message;
                _log.Error("Tunnel core: " + notice.Message);
                break;
        }
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                if (_state != PsiphonState.Connected) continue;
                if (_process is { HasExited: false } && await ProbeAsync(HttpProxyPort, cancellationToken))
                {
                    failures = 0;
                    continue;
                }
                _log.Warn($"Tunnel core is not responding ({++failures}/2).");
                if (failures >= 2)
                {
                    _log.Error("Tunnel connection lost. Click Start VPN to reconnect.");
                    SetState(PsiphonState.Failed);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void SaveSession()
    {
        var process = _process!;
        var session = new OwnedSession(process.Id, process.StartTime.ToUniversalTime().Ticks,
            _processExecutable!, HttpProxyPort, SocksProxyPort);
        var temp = SessionPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(session));
        File.Move(temp, SessionPath, overwrite: true);
    }

    private bool TryAttachOwnedSession()
    {
        // A stale PID or another path is never adopted or stopped.
        if (!File.Exists(SessionPath)) return false;
        Process? candidate = null;
        try
        {
            var session = JsonSerializer.Deserialize<OwnedSession>(File.ReadAllText(SessionPath));
            if (session is null || !IsPrivateCorePath(session.Executable)) return false;
            candidate = Process.GetProcessById(session.ProcessId);
            _ = candidate.SafeHandle; // Retain the exact process identity across PID reuse.
            if (candidate.HasExited || candidate.StartTime.ToUniversalTime().Ticks != session.StartTimeUtcTicks ||
                !string.Equals(candidate.MainModule?.FileName, session.Executable, StringComparison.OrdinalIgnoreCase)) return false;
            _process = candidate;
            _processExecutable = session.Executable;
            candidate = null;
            _httpPort = session.HttpPort;
            _socksPort = session.SocksPort;
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Warn("Ignoring stale tunnel session: " + exception.Message);
            return false;
        }
        finally { candidate?.Dispose(); }
    }

    private bool IsPrivateCorePath(string executable)
    {
        var relative = Path.GetRelativePath(Path.Combine(RuntimeDirectory, "bin"), executable);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        // Also recognize a previous bundled version, so upgrades can retire an
        // owned detached engine before opening the shared private datastore.
        return parts.Length == 2 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit) &&
            parts[1].Equals("psiphon-tunnel-core.exe", StringComparison.OrdinalIgnoreCase);
    }

    private async Task KillOwnedProcessAsync()
    {
        if (_process is null) return;
        if (!_process.HasExited)
        {
            // Retained handle, no taskkill, name scans, or global core cleanup.
            _process.Kill();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(timeout.Token);
        }
        _log.Info($"PsiSplit tunnel core {_process.Id} stopped.");
        _process.Dispose();
        _process = null;
        _httpPort = _socksPort = _tunnelCount = 0;
        File.Delete(SessionPath);
    }

    private void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private void SetState(PsiphonState state)
    {
        if (_state == state) return;
        _state = state;
        try { StateChanged?.Invoke(state); }
        catch (Exception exception) { _log.Warn($"StateChanged handler: {exception.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopWatchingAsync();
            // The caller chooses StopAsync or detach using the exit preference.
            try
            {
                if (_process is { HasExited: false }) SaveSession();
            }
            finally
            {
                _process?.Dispose();
                _process = null;
                ReleaseLease();
            }
        }
        finally { _gate.Release(); }
    }

    private sealed record OwnedSession(int ProcessId, long StartTimeUtcTicks, string Executable, int HttpPort, int SocksPort);
}
