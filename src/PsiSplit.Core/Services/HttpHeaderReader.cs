using System.Text;

namespace PsiSplit.Core.Services;

internal static class HttpHeaderReader
{
    /// <summary>
    /// Reads bytes until the end of the HTTP header block. The body bytes are
    /// left untouched in the stream so they can be relayed afterwards.
    /// </summary>
    public static async Task<string?> ReadHeaderAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxBytes, 4096));
        var buffer = new byte[1];

        while (builder.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return builder.Length == 0 ? null : throw new IOException("Connection closed before the end of the HTTP header.");
            }

            var ch = (char)buffer[0];
            builder.Append(ch);

            if (builder.Length >= 4 &&
                builder[^4] == '\r' && builder[^3] == '\n' &&
                builder[^2] == '\r' && builder[^1] == '\n')
            {
                return builder.ToString();
            }

            if (builder.Length >= 2 && builder[^2] == '\n' && builder[^1] == '\n')
            {
                return builder.ToString();
            }
        }

        throw new InvalidDataException($"HTTP header exceeds {maxBytes} bytes.");
    }

    public static int ParseStatusCode(string header)
    {
        var lineEnd = header.IndexOf('\n');
        var line = lineEnd >= 0 ? header[..lineEnd] : header;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[1], out var status))
        {
            return status;
        }

        return 0;
    }
}
