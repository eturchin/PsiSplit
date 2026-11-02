using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PsiSplit.Core.Services;

/// <summary>
/// Tiny HTTP server for /proxy.pac. Implemented with TcpListener instead of
/// HttpListener so it works without administrator rights or URL ACL changes.
/// </summary>
public sealed class PacServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly LogService _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private volatile string _pac = "function FindProxyForURL(url, host) { return \"DIRECT\"; }";
    private Task? _acceptTask;

    public PacServer(int port, LogService log)
    {
        _port = port;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => _port;
    public string Url => $"http://127.0.0.1:{_port}/proxy.pac";

    public void UpdatePac(string pac) => _pac = pac;

    public void Start()
    {
        _listener.Start(64);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info($"PAC server started: {Url}");
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

                _log.Warn($"PAC accept: {exception.Message}");
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                var header = await HttpHeaderReader.ReadHeaderAsync(stream, 16 * 1024, timeout.Token);
                if (header is null)
                {
                    return;
                }

                var requestLine = header.Split('\n')[0].TrimEnd('\r');
                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var path = parts.Length >= 2 ? parts[1] : "/";

                // PAC URLs are versioned with ?v=... so WinINet/Chrome refetch
                // the file after the site list changes.
                var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
                if (queryIndex >= 0)
                {
                    path = path[..queryIndex];
                }

                if (path is "/proxy.pac" or "/pac" or "/")
                {
                    var body = Encoding.UTF8.GetBytes(_pac);
                    var response =
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/x-ns-proxy-autoconfig\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                        "Connection: close\r\n\r\n";

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
                    await stream.WriteAsync(body, timeout.Token);
                }
                else
                {
                    var body = Encoding.UTF8.GetBytes("Not found");
                    var response =
                        "HTTP/1.1 404 Not Found\r\n" +
                        "Content-Type: text/plain\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Connection: close\r\n\r\n";

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
                    await stream.WriteAsync(body, timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal timeout/stop.
            }
            catch (Exception exception)
            {
                _log.Warn($"PAC request: {exception.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

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
