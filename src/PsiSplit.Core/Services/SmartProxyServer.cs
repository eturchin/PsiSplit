using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PsiSplit.Core.Models;

namespace PsiSplit.Core.Services;

/// <summary>
/// A small HTTP proxy with per-domain routing.
///
/// Full mode:     every CONNECT/HTTP request is sent to Psiphon's local proxy.
/// Filtered mode: only hosts matched by <see cref="DomainMatcher"/> are sent to
///                Psiphon; everything else is connected directly.
///
/// The proxy intentionally handles one request per client connection and sets
/// "Connection: close". This keeps the implementation small and predictable;
/// browsers open additional connections when needed.
/// </summary>
public sealed class SmartProxyServer : IAsyncDisposable
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Proxy-Connection",
        "Proxy-Authorization",
        "Connection",
        "Keep-Alive",
        "TE",
        "Trailer"
    };

    private readonly int _port;
    private readonly TunnelMode _mode;
    private int _upstreamPort;
    private readonly Func<string, bool> _shouldTunnel;
    private readonly Func<bool> _isUpstreamReady;
    private readonly LogService _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();
    private readonly ProxyMetrics _metrics = new();
    private long _nextClientId;
    private int _boundPort;
    private Task? _acceptTask;

    public SmartProxyServer(
        int port,
        TunnelMode mode,
        int upstreamPort,
        Func<string, bool> shouldTunnel,
        Func<bool> isUpstreamReady,
        LogService log)
    {
        _port = port;
        _mode = mode;
        _upstreamPort = upstreamPort;
        _shouldTunnel = shouldTunnel;
        _isUpstreamReady = isUpstreamReady;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => _boundPort == 0 ? _port : _boundPort;

    /// <summary>
    /// Psiphon's local HTTP proxy port. Kept mutable because the actual port is
    /// announced by the bundled engine after the OS allocates a free port.
    /// </summary>
    public int UpstreamPort
    {
        get => Volatile.Read(ref _upstreamPort);
        set => Volatile.Write(ref _upstreamPort, value);
    }

    public string Name => _mode == TunnelMode.Full ? "full" : "filtered";

    public ProxyMetrics Metrics => _metrics;

    public void Start()
    {
        _listener.Start(512);
        _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info($"Proxy [{Name}] listening on 127.0.0.1:{Port} ({(_mode == TunnelMode.Full ? "all traffic" : "listed sites only")}).");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _log.Warn($"Proxy [{Name}] accept: {exception.Message}");
                continue;
            }

            var id = Interlocked.Increment(ref _nextClientId);
            _clients[id] = client;
            _metrics.ConnectionOpened();
            _ = HandleClientAsync(id, client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(long id, TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));

                string? header;
                try
                {
                    header = await HttpHeaderReader.ReadHeaderAsync(stream, 64 * 1024, headerTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException exception)
                {
                    _log.Warn($"Proxy [{Name}] reading header: {exception.Message}");
                    return;
                }

                if (header is null)
                {
                    return;
                }

                var lines = header.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var requestLine = lines[0].Trim();
                var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (requestParts.Length < 3)
                {
                    await WriteErrorAsync(stream, 400, "Invalid HTTP request line.");
                    return;
                }

                var method = requestParts[0];
                var target = requestParts[1];
                var version = requestParts[2];
                var headers = ParseHeaders(lines);

                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleConnectAsync(client, stream, target, cancellationToken);
                }
                else
                {
                    await HandleHttpAsync(client, stream, method, target, version, headers, cancellationToken);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Normal disconnect path.
        }
        catch (Exception exception)
        {
            _log.Error($"Proxy [{Name}] unexpected error", exception);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _metrics.ConnectionClosed();
        }
    }

    private async Task HandleConnectAsync(
        TcpClient client,
        NetworkStream clientStream,
        string target,
        CancellationToken cancellationToken)
    {
        var (host, port) = ParseAuthority(target, 443);
        if (string.IsNullOrWhiteSpace(host))
        {
            await WriteErrorAsync(clientStream, 400, "Invalid CONNECT host.");
            return;
        }

        var usePsiphon = _mode == TunnelMode.Full || _shouldTunnel(host);
        _metrics.RouteChosen(usePsiphon);
        if (usePsiphon && !_isUpstreamReady())
        {
            _log.Warn($"Proxy [{Name}] {host}:{port}: Psiphon is not connected.");
            await WriteErrorAsync(clientStream, 502, "Psiphon is not connected. Click Start VPN.");
            return;
        }

        TcpClient? upstream = null;
        try
        {
            if (usePsiphon)
            {
                upstream = await ConnectAsync("127.0.0.1", UpstreamPort, cancellationToken);
                var upstreamStream = upstream.GetStream();
                var hostPort = $"{FormatHost(host)}:{port}";
                var connectRequest =
                    $"CONNECT {hostPort} HTTP/1.1\r\n" +
                    $"Host: {hostPort}\r\n" +
                    "Proxy-Connection: keep-alive\r\n\r\n";

                await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(connectRequest), cancellationToken);

                using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                responseTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                var response = await HttpHeaderReader.ReadHeaderAsync(upstreamStream, 32 * 1024, responseTimeout.Token);
                var status = response is null ? 0 : HttpHeaderReader.ParseStatusCode(response);

                if (status != 200)
                {
                    _log.Warn($"Proxy [{Name}] Psiphon CONNECT {host}:{port} -> {status}");
                    await WriteErrorAsync(clientStream, 502, $"Psiphon rejected CONNECT ({status}).");
                    return;
                }

                await clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancellationToken);
                _log.Info($"Proxy [{Name}] CONNECT {host}:{port} -> Psiphon");
            }
            else
            {
                upstream = await ConnectAsync(host, port, cancellationToken);
                await clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancellationToken);
                _log.Info($"Proxy [{Name}] CONNECT {host}:{port} -> direct");
            }

            await RelayAsync(client, upstream, cancellationToken, _metrics);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            _log.Warn($"Proxy [{Name}] CONNECT {host}:{port}: {exception.Message}");
            await WriteErrorAsync(clientStream, 502, "Failed to establish a connection.");
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    private async Task HandleHttpAsync(
        TcpClient client,
        NetworkStream clientStream,
        string method,
        string target,
        string version,
        List<Header> headers,
        CancellationToken cancellationToken)
    {
        if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(clientStream, 400, "HTTPS must use CONNECT.");
            return;
        }

        var hostHeader = GetHeader(headers, "Host");
        Uri? uri = null;

        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) &&
            string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            uri = absolute;
        }
        else if (!string.IsNullOrWhiteSpace(hostHeader) &&
                 Uri.TryCreate("http://" + hostHeader + target, UriKind.Absolute, out var constructed))
        {
            uri = constructed;
        }

        if (uri is null || string.IsNullOrWhiteSpace(uri.Host))
        {
            await WriteErrorAsync(clientStream, 400, "Invalid HTTP request.");
            return;
        }

        var host = uri.Host;
        var port = uri.Port;
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var usePsiphon = _mode == TunnelMode.Full || _shouldTunnel(host);
        _metrics.RouteChosen(usePsiphon);

        if (usePsiphon && !_isUpstreamReady())
        {
            _log.Warn($"Proxy [{Name}] {method} {host}:{port}: Psiphon is not connected.");
            await WriteErrorAsync(clientStream, 502, "Psiphon is not connected. Click Start VPN.");
            return;
        }

        TcpClient? upstream = null;
        try
        {
            upstream = usePsiphon
                ? await ConnectAsync("127.0.0.1", UpstreamPort, cancellationToken)
                : await ConnectAsync(host, port, cancellationToken);

            var requestTarget = usePsiphon ? uri.AbsoluteUri : pathAndQuery;
            var request = new StringBuilder();
            request.Append(method).Append(' ').Append(requestTarget).Append(' ').Append(version).Append("\r\n");

            var hasHost = false;
            var isWebSocket = false;

            foreach (var header in headers)
            {
                if (HopByHopHeaders.Contains(header.Name))
                {
                    continue;
                }

                if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    hasHost = true;
                }

                if (header.Name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) &&
                    header.Value.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    isWebSocket = true;
                }

                request.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
            }

            if (!hasHost)
            {
                request.Append("Host: ").Append(uri.Authority).Append("\r\n");
            }

            request.Append(isWebSocket ? "Connection: Upgrade\r\n" : "Connection: close\r\n");
            request.Append("\r\n");

            var upstreamStream = upstream.GetStream();
            var requestBytes = Encoding.UTF8.GetBytes(request.ToString());
            await upstreamStream.WriteAsync(requestBytes, cancellationToken);
            _metrics.AddBytesSent(requestBytes.Length);

            _log.Info($"Proxy [{Name}] {method} {host}:{port} -> {(usePsiphon ? "Psiphon" : "direct")}");
            await RelayAsync(client, upstream, cancellationToken, _metrics);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            _log.Warn($"Proxy [{Name}] {method} {host}:{port}: {exception.Message}");
            await WriteErrorAsync(clientStream, 502, "Failed to establish a connection.");
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    private static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(host, port, timeout.Token);
            client.NoDelay = true;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task RelayAsync(
        TcpClient first,
        TcpClient second,
        CancellationToken cancellationToken,
        ProxyMetrics metrics)
    {
        using var relayTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        relayTimeout.CancelAfter(TimeSpan.FromMinutes(15));

        var firstStream = first.GetStream();
        var secondStream = second.GetStream();

        var firstToSecond = PumpAsync(
            firstStream,
            secondStream,
            second.Client,
            relayTimeout.Token,
            metrics.AddBytesSent);
        var secondToFirst = PumpAsync(
            secondStream,
            firstStream,
            first.Client,
            relayTimeout.Token,
            metrics.AddBytesReceived);

        try
        {
            await Task.WhenAll(firstToSecond, secondToFirst);
        }
        catch (OperationCanceledException)
        {
            // Timeout or shutdown.
        }
        catch (IOException)
        {
            // Peer reset.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown.
        }
    }

    private static async Task PumpAsync(
        Stream from,
        Stream to,
        Socket toSocket,
        CancellationToken cancellationToken,
        Action<int> countBytes)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await to.FlushAsync(cancellationToken);
                countBytes(read);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Normal close path.
        }
        finally
        {
            try
            {
                toSocket.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // Already closed.
            }
        }
    }

    private static async Task WriteErrorAsync(NetworkStream stream, int statusCode, string message)
    {
        try
        {
            var reason = statusCode switch
            {
                400 => "Bad Request",
                502 => "Bad Gateway",
                _ => "Error"
            };

            var body = Encoding.UTF8.GetBytes(message);
            var header =
                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(body);
        }
        catch
        {
            // Client is already gone.
        }
    }

    private static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        authority = authority.Trim();

        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            var end = authority.IndexOf(']');
            if (end > 0)
            {
                var host = authority[1..end];
                var port = defaultPort;
                if (authority.Length > end + 2 &&
                    authority[end + 1] == ':' &&
                    int.TryParse(authority[(end + 2)..], out var parsedPort))
                {
                    port = parsedPort;
                }

                return (host, port);
            }
        }

        var colon = authority.LastIndexOf(':');
        if (colon > 0 &&
            authority.IndexOf(':') == colon &&
            int.TryParse(authority[(colon + 1)..], out var portValue))
        {
            return (authority[..colon], portValue);
        }

        return (authority, defaultPort);
    }

    private static string FormatHost(string host)
        => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    private static List<Header> ParseHeaders(IEnumerable<string> lines)
    {
        var headers = new List<Header>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers.Add(new Header(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        return headers;
    }

    private static string? GetHeader(IEnumerable<Header> headers, string name)
        => headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private readonly record struct Header(string Name, string Value);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        foreach (var client in _clients.Values)
        {
            try
            {
                client.Close();
            }
            catch
            {
                // ignore
            }
        }

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }
        }

        _cts.Dispose();
    }
}
