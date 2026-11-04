namespace PsiSplit.Core.Services;

/// <summary>Measures transferred bytes per second using actual monotonic sample times.</summary>
public sealed class TransferRateSampler
{
    private bool _initialized;
    private long _sent;
    private long _received;
    private TimeSpan _sampleTime;

    public TransferRateSnapshot Current { get; private set; }

    public TransferRateSnapshot Sample(long bytesSent, long bytesReceived, TimeSpan monotonicTime)
    {
        if (bytesSent < 0 || bytesReceived < 0) throw new ArgumentOutOfRangeException(nameof(bytesSent));
        if (!_initialized || bytesSent < _sent || bytesReceived < _received)
        {
            _initialized = true;
            _sent = bytesSent;
            _received = bytesReceived;
            _sampleTime = monotonicTime;
            return Current = default;
        }

        var elapsed = (monotonicTime - _sampleTime).TotalSeconds;
        if (elapsed <= 0) return Current;
        Current = new((bytesSent - _sent) / elapsed, (bytesReceived - _received) / elapsed);
        _sent = bytesSent;
        _received = bytesReceived;
        _sampleTime = monotonicTime;
        return Current;
    }

    public void Reset()
    {
        _initialized = false;
        Current = default;
    }
}

public readonly record struct TransferRateSnapshot(double BytesSentPerSecond, double BytesReceivedPerSecond);
public readonly record struct TransferTotals(long BytesSent, long BytesReceived);
