using System.Net;
using System.Net.Sockets;

namespace PsiSplit.Core.Services;

public static class PortProbe
{
    public static async Task<bool> IsListeningAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsListeningAsync(port, TimeSpan.FromMilliseconds(400), cancellationToken))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }
}
