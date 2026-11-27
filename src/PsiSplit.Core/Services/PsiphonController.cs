using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PsiSplit.Core.Services;

public enum PsiphonState { Stopped, Starting, Connected, Reconnecting, Stopping, Failed }

/// <summary>
/// Runs the bundled tunnel core without psiphon3.exe, its registry or datastore.
/// Ownership is tied to a Process handle, never a process-name scan.
/// </summary>
public sealed class PsiphonController : IAsyncDisposable
{
    // A tunnel loss is normal for a mobile-friendly engine: the core usually
    // restores its relay within seconds. Keep the session alive while it does,
    // and fail only when recovery has clearly stopped making progress.
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TunnelLossDebounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TunnelLossGrace = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ListenerLossGrace = TimeSpan.FromSeconds(15);

    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Action<Process>? _processStarted;
    private Process? _process;
    private string? _processExecutable;
    private FileStream? _lease;
    private int _httpPort;
    private int _socksPort;
    private int _tunnelCount;
    private volatile string _lastError = "";
    private long _bytesSent;
    private long _bytesReceived;
    private string _connectedRegion = "";
    private EgressRegionReport _regionReport = EgressRegionReport.Empty;
    private string _attachedSettingsKey = "";
    private string _noticePath;
    private volatile PsiphonState _state = PsiphonState.Stopped;
    private CancellationTokenSource? _watchCts;
    private Task? _noticeTask;
    private Task? _healthTask;
    private bool _disposed;

    public PsiphonController(LogService log, string? runtimeDirectory = null,
        string egressRegion = PsiphonRegionCatalog.DefaultRegion, Action<Process>? processStarted = null)
    {
        _log = log;
        _processStarted = processStarted;
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PsiSplit", "Tunnel"));
        RequestedEgressRegion = PsiphonRegionCatalog.NormalizeCode(egressRegion)
            ?? throw new ArgumentException("Choose a two-letter exit country code or Automatic.", nameof(egressRegion));
        _noticePath = Path.Combine(RuntimeDirectory, "notices.jsonl");
        LoadRegionCache();
    }

    public string RuntimeDirectory { get; }
    public PsiphonState State => _state;
    public int HttpProxyPort => Volatile.Read(ref _httpPort);
    public int SocksProxyPort => Volatile.Read(ref _socksPort);
    public int? ProcessId => _process?.Id;
    public bool CanStop => _process is not null;
    public string RequestedEgressRegion { get; }
    public string ConnectedRegion => Volatile.Read(ref _connectedRegion);
    public string LastError => _lastError;
    public EgressRegionReport RegionReport => Volatile.Read(ref _regionReport);
    public TransferTotals TrafficTotals => new(Interlocked.Read(ref _bytesSent), Interlocked.Read(ref _bytesReceived));
    private string SettingsKey => "traffic-regions-v1:" + RequestedEgressRegion;
    private string RegionCachePath => Path.Combine(RuntimeDirectory, "regions.json");
    private string SessionPath => Path.Combine(RuntimeDirectory, "session.json");
    private string NoticePath => _noticePath;

    public event Action<PsiphonState>? StateChanged;
    public event Action? ConnectionInfoChanged;

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
                    _attachedSettingsKey == SettingsKey &&
                    await ProbeAsync(HttpProxyPort, cancellationToken))
                {
                    _log.Info($"Reattached to the PsiSplit tunnel core, PID {_process!.Id}.");
                    SetState(PsiphonState.Connected);
                    // Replay the current notice file so a detached core restores
                    // its tunnel counter, exit region and traffic totals.
                    StartWatching(readExistingNotices: true);
                    return;
                }
                await KillOwnedProcessAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            _httpPort = _socksPort = _tunnelCount = 0;
            _lastError = "";
            ResetTunnelInfo();
            var dataDirectory = Path.Combine(RuntimeDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            var configPath = Path.Combine(RuntimeDirectory, "psiphon.config");
            await File.WriteAllTextAsync(configPath, PsiphonBundle.CreateConfig(dataDirectory, RequestedEgressRegion).ToJsonString(), cancellationToken);
            var noticesDirectory = Path.Combine(RuntimeDirectory, "notices");
            Directory.CreateDirectory(noticesDirectory);
            _noticePath = Path.Combine(noticesDirectory, Guid.NewGuid().ToString("N") + ".jsonl");
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
            if (_processStarted is not null)
            {
                try { _processStarted(_process); }
                catch (Exception exception)
                {
                    _log.Warn("The tunnel core could not be registered for lifetime cleanup: " + exception.Message);
                }
            }
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
            var stopped = await KillOwnedProcessAsync();
            ReleaseLease();
            SetState(stopped ? PsiphonState.Stopped : PsiphonState.Failed);
        }
        catch (Exception exception)
        {
            _log.Error("Failed to stop the tunnel core", exception);
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
        var noticeTask = _noticeTask;
        var healthTask = _healthTask;
        try
        {
            await _watchCts.CancelAsync();
            await Task.WhenAll(noticeTask ?? Task.CompletedTask, healthTask ?? Task.CompletedTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // A monitor failure must never block shutdown.
            _log.Warn("Stopping tunnel monitors: " + exception.Message);
        }
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
                if (notice.Value == 0) Volatile.Write(ref _connectedRegion, "");
                _log.Info(notice.Value > 0 ? "Tunnel core established a tunnel." : "Tunnel core is connecting...");
                break;
            case "AvailableEgressRegions":
                var report = new EgressRegionReport(notice.Regions, DateTimeOffset.UtcNow, false);
                Volatile.Write(ref _regionReport, report);
                SaveRegionCache(report);
                NotifyConnectionInfoChanged();
                break;
            case "ConnectedServerRegion":
                Volatile.Write(ref _connectedRegion, notice.Region);
                NotifyConnectionInfoChanged();
                break;
            case "BytesTransferred":
                Interlocked.Add(ref _bytesSent, notice.BytesSent);
                Interlocked.Add(ref _bytesReceived, notice.BytesReceived);
                break;
            case "Error":
                _lastError = notice.Message;
                _log.Error("Tunnel core: " + notice.Message);
                break;
        }
    }

    private void ResetTunnelInfo()
    {
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Volatile.Write(ref _connectedRegion, "");
        NotifyConnectionInfoChanged();
    }

    private void NotifyConnectionInfoChanged()
    {
        try { ConnectionInfoChanged?.Invoke(); }
        catch (Exception exception) { _log.Warn("Connection information handler: " + exception.Message); }
    }

    private void LoadRegionCache()
    {
        try
        {
            var info = new FileInfo(RegionCachePath);
            if (!info.Exists || info.Length > 64 * 1024) return;
            var cached = JsonSerializer.Deserialize<RegionCache>(File.ReadAllText(RegionCachePath));
            if (cached is null || cached.Regions is null || cached.Regions.Length > 300) return;
            var codes = cached.Regions.Select(PsiphonRegionCatalog.NormalizeCode).ToArray();
            if (codes.Any(code => code is not { Length: 2 })) return;
            _regionReport = new EgressRegionReport(Array.AsReadOnly(codes.Select(code => code!).Distinct().Order().ToArray()),
                cached.ReportedAtUtc, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn("Ignoring unavailable country cache: " + exception.Message);
        }
    }

    private void SaveRegionCache(EgressRegionReport report)
    {
        try
        {
            var temp = RegionCachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new RegionCache(report.Codes.ToArray(), report.ReportedAtUtc!.Value)));
            File.Move(temp, RegionCachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not save the country list: " + exception.Message);
        }
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? listenerDownSince = null;
        DateTimeOffset? tunnelDownSince = null;
        try
        {
            while (true)
            {
                await Task.Delay(HealthInterval, cancellationToken);
                var state = _state;
                if (state is not (PsiphonState.Connected or PsiphonState.Reconnecting)) continue;

                var process = Volatile.Read(ref _process);
                if (process is null || HasExited(process))
                {
                    // A killed or crashed core will never recover on its own.
                    _log.Error("Tunnel core exited unexpectedly. Click Start VPN to reconnect.");
                    SetState(PsiphonState.Failed);
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                if (!await IsListenerAliveAsync(HttpProxyPort, cancellationToken))
                {
                    listenerDownSince ??= now;
                    var downFor = now - listenerDownSince.Value;
                    if (state == PsiphonState.Connected)
                    {
                        _log.Warn("Tunnel core is not accepting local connections; waiting for it to recover.");
                        SetState(PsiphonState.Reconnecting);
                    }
                    if (downFor >= ListenerLossGrace)
                    {
                        _log.Error("Tunnel connection lost: the local proxy stopped responding. Click Start VPN to reconnect.");
                        SetState(PsiphonState.Failed);
                        break;
                    }
                    continue;
                }

                listenerDownSince = null;

                if (Volatile.Read(ref _tunnelCount) > 0)
                {
                    tunnelDownSince = null;
                    if (state == PsiphonState.Reconnecting)
                    {
                        _log.Info("Tunnel connection restored.");
                        SetState(PsiphonState.Connected);
                    }
                    continue;
                }

                // The listener is alive but the relay is gone. Psiphon normally
                // rebuilds it in a few seconds; give it a generous grace period.
                tunnelDownSince ??= now;
                var tunnelDownFor = now - tunnelDownSince.Value;
                if (state == PsiphonState.Connected && tunnelDownFor >= TunnelLossDebounce)
                {
                    _log.Warn("Tunnel relay is down; waiting for the core to reconnect.");
                    SetState(PsiphonState.Reconnecting);
                }
                if (tunnelDownFor >= TunnelLossGrace)
                {
                    _log.Error("Tunnel connection was not restored in time. Click Start VPN to reconnect.");
                    SetState(PsiphonState.Failed);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _log.Error("Tunnel health monitor stopped unexpectedly", exception);
            SetState(PsiphonState.Failed);
        }
    }

    /// <summary>
    /// Checks only that the core's loopback listener accepts a socket. Unlike
    /// <see cref="ProbeAsync"/> this never depends on a remote site or the relay,
    /// so a temporary egress outage is not mistaken for a dead process.
    /// </summary>
    private static async Task<bool> IsListenerAliveAsync(int port, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { return false; }
    }

    private void SaveSession()
    {
        try
        {
            var process = _process!;
            var session = new OwnedSession(process.Id, process.StartTime.ToUniversalTime().Ticks,
                _processExecutable!, HttpProxyPort, SocksProxyPort, SettingsKey, ConnectedRegion, Path.GetFileName(NoticePath));
            var temp = SessionPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(session));
            File.Move(temp, SessionPath, overwrite: true);
        }
        catch (Exception exception)
        {
            // Detach metadata is optional. A locked or unavailable state folder
            // must not fail a launch, a reconnect or a clean shutdown.
            _log.Warn("Could not save the tunnel session: " + exception.Message);
        }
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
            var noticeFile = session.NoticeFile ?? "";
            if (noticeFile.Length > 0 && noticeFile != "notices.jsonl" &&
                (Path.GetFileName(noticeFile) != noticeFile || Path.GetExtension(noticeFile) != ".jsonl" ||
                 !Guid.TryParseExact(Path.GetFileNameWithoutExtension(noticeFile), "N", out _))) return false;
            candidate = Process.GetProcessById(session.ProcessId);
            _ = candidate.SafeHandle; // Retain the exact process identity across PID reuse.
            if (candidate.HasExited || candidate.StartTime.ToUniversalTime().Ticks != session.StartTimeUtcTicks ||
                !string.Equals(candidate.MainModule?.FileName, session.Executable, StringComparison.OrdinalIgnoreCase)) return false;
            _process = candidate;
            _processExecutable = session.Executable;
            candidate = null;
            _httpPort = session.HttpPort;
            _socksPort = session.SocksPort;
            _attachedSettingsKey = session.SettingsKey ?? "";
            _noticePath = noticeFile.Length == 0 || noticeFile == "notices.jsonl"
                ? Path.Combine(RuntimeDirectory, "notices.jsonl")
                : Path.Combine(RuntimeDirectory, "notices", noticeFile);
            Volatile.Write(ref _connectedRegion, PsiphonRegionCatalog.NormalizeCode(session.ConnectedRegion) ?? "");
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

    private async Task<bool> KillOwnedProcessAsync()
    {
        var process = _process;
        if (process is null) return true;
        _process = null;
        var stopped = true;
        try
        {
            if (!HasExited(process))
            {
                // Retained handle, no taskkill, name scans, or global core cleanup.
                var killRequested = false;
                try
                {
                    process.Kill();
                    killRequested = true;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The core may exit between the HasExited check and Kill.
                    _log.Warn($"Tunnel core {process.Id} could not be killed: {exception.Message}");
                }

                if (killRequested && !HasExited(process))
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Release ownership anyway: a launcher-owned Windows job
                        // still guarantees process cleanup if the caller exits.
                        _log.Warn($"Tunnel core {process.Id} did not exit within 10 seconds.");
                    }
                }

                stopped = HasExited(process);
            }

            _log.Info(stopped
                ? $"PsiSplit tunnel core {process.Id} stopped."
                : $"PsiSplit released the tunnel core {process.Id}, which is still running.");
        }
        catch (Exception exception)
        {
            // Shutdown must not surface a modal error because a process raced us.
            _log.Warn($"Stopping tunnel core {process.Id}: {exception.Message}");
            stopped = false;
        }
        finally
        {
            process.Dispose();
            _httpPort = _socksPort = _tunnelCount = 0;
            ResetTunnelInfo();
            try { File.Delete(SessionPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Previous tunnel session is still in use: " + exception.Message);
            }
            // Each launch gets its own notice file. A reader or antivirus holding an
            // old file must not prevent a country switch from starting a new core.
            try { File.Delete(NoticePath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Previous tunnel notices are still in use: " + exception.Message);
            }
        }

        return stopped;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private void SetState(PsiphonState state)
    {
        lock (_stateLock)
        {
            if (_state == state) return;
            _state = state;
        }
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
                if (_process is not null && !HasExited(_process)) SaveSession();
            }
            catch (Exception exception)
            {
                _log.Warn("Saving the tunnel session while disposing: " + exception.Message);
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

    private sealed record RegionCache(string[] Regions, DateTimeOffset ReportedAtUtc);
    private sealed record OwnedSession(int ProcessId, long StartTimeUtcTicks, string Executable, int HttpPort, int SocksPort,
        string SettingsKey = "", string ConnectedRegion = "", string NoticeFile = "");
}
