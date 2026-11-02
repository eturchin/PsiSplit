namespace PsiSplit.Core.Services;

/// <summary>
/// Thread-safe counters for one local proxy instance. The UI reads immutable
/// snapshots on a timer, so counter updates never touch WPF objects directly.
/// </summary>
public sealed class ProxyMetrics
{
    private long _bytesSent;
    private long _bytesReceived;
    private int _activeConnections;
    private int _totalConnections;
    private int _tunneledConnections;
    private int _directConnections;

    public void ConnectionOpened()
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
    }

    public void ConnectionClosed()
        => Interlocked.Decrement(ref _activeConnections);

    public void RouteChosen(bool throughPsiphon)
    {
        if (throughPsiphon)
        {
            Interlocked.Increment(ref _tunneledConnections);
        }
        else
        {
            Interlocked.Increment(ref _directConnections);
        }
    }

    public void AddBytesSent(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _bytesSent, count);
        }
    }

    public void AddBytesReceived(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _bytesReceived, count);
        }
    }

    public ProxyMetricsSnapshot Snapshot()
        => new(
            Interlocked.Read(ref _bytesSent),
            Interlocked.Read(ref _bytesReceived),
            Volatile.Read(ref _activeConnections),
            Volatile.Read(ref _totalConnections),
            Volatile.Read(ref _tunneledConnections),
            Volatile.Read(ref _directConnections));
}

public readonly record struct ProxyMetricsSnapshot(
    long BytesSent,
    long BytesReceived,
    int ActiveConnections,
    int TotalConnections,
    int TunneledConnections,
    int DirectConnections);
