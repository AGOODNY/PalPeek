using System.Buffers.Binary;
using System.Text;

namespace PalPeek;

public sealed record WebMediaMessage(
    byte Type,
    long Sequence,
    int DurationMilliseconds,
    string SessionId,
    byte[] Payload);

public static class WebMediaProtocol
{
    private const uint Magic = 0x4D575050;
    private const ushort Version = 1;
    private const int HeaderLength = 26;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async ValueTask<WebMediaMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactAsync(stream, header, cancellationToken))
            return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != Version)
            throw new InvalidDataException("网页媒体管道协议无效。");
        var sessionLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22));
        if (sessionLength is 0 or > 128 || payloadLength is < 0 or > 8 * 1024 * 1024)
            throw new InvalidDataException("网页媒体管道消息过大。");
        var sessionBytes = new byte[sessionLength];
        if (!await ReadExactAsync(stream, sessionBytes, cancellationToken))
            throw new EndOfStreamException("网页媒体管道会话 ID 不完整。");
        var payload = new byte[payloadLength];
        if (!await ReadExactAsync(stream, payload, cancellationToken))
            throw new EndOfStreamException("网页媒体管道负载不完整。");
        string sessionId;
        try
        {
            sessionId = StrictUtf8.GetString(sessionBytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("网页媒体管道会话 ID 不是有效 UTF-8。", ex);
        }
        return new WebMediaMessage(
            header[6],
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16)),
            sessionId,
            payload);
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0)
                return false;
            offset += count;
        }
        return true;
    }
}
