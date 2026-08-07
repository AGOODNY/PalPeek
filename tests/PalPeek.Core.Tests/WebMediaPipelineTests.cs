using PalPeek;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace PalPeek.Core.Tests;

public sealed class WebMediaPipelineTests
{
    [Fact]
    public void BufferKeepsOnlyTheNewestTwelveSegments()
    {
        var buffer = new WebMediaBuffer();
        buffer.Reset("session");
        Assert.True(buffer.SetInitialization("session", [1, 2, 3]));
        for (var sequence = 0; sequence < 15; sequence++)
            Assert.True(buffer.AddSegment(
                "session",
                new WebMediaSegment(sequence, 1000, [(byte)sequence])));

        var playlist = Assert.IsType<string>(buffer.BuildPlaylist("session"));

        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:3", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("segment-2.m4s", playlist, StringComparison.Ordinal);
        Assert.Contains("segment-14.m4s", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistTargetDurationCoversLongestSegment()
    {
        var buffer = new WebMediaBuffer();
        buffer.Reset("session");
        Assert.True(buffer.SetInitialization("session", [1, 2, 3]));
        Assert.True(buffer.AddSegment("session", new WebMediaSegment(0, 2401, [4])));

        var playlist = Assert.IsType<string>(buffer.BuildPlaylist("session"));

        Assert.Contains("#EXT-X-TARGETDURATION:3", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistTargetDurationDoesNotDecreaseWithinSession()
    {
        var buffer = new WebMediaBuffer();
        buffer.Reset("session");
        Assert.True(buffer.SetInitialization("session", [1, 2, 3]));
        Assert.True(buffer.AddSegment("session", new WebMediaSegment(0, 2401, [4])));
        for (var sequence = 1; sequence <= 12; sequence++)
            Assert.True(buffer.AddSegment(
                "session",
                new WebMediaSegment(sequence, 1000, [(byte)sequence])));

        var playlist = Assert.IsType<string>(buffer.BuildPlaylist("session"));

        Assert.DoesNotContain("segment-0.m4s", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-TARGETDURATION:3", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedPipeAcceptsInitializationAndMediaMessagesInOrder()
    {
        const string sessionId = "pipe-test-session";
        var pipeName = $"PalPeekWebMediaTest-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accept;

        var readMessages = ReadTwoMessagesAsync(server, timeout.Token);
        await WriteMessageAsync(client, 1, sessionId, 0, 0, [9, 8, 7], timeout.Token);
        await WriteMessageAsync(client, 2, sessionId, 42, 1000, [6, 5, 4], timeout.Token);
        await client.FlushAsync(timeout.Token);

        var (initialization, segment) = await readMessages;

        Assert.NotNull(initialization);
        Assert.Equal(1, initialization.Type);
        Assert.Equal(sessionId, initialization.SessionId);
        Assert.Equal([9, 8, 7], initialization.Payload);
        Assert.NotNull(segment);
        Assert.Equal(2, segment.Type);
        Assert.Equal(42, segment.Sequence);
        Assert.Equal(1000, segment.DurationMilliseconds);
        Assert.Equal([6, 5, 4], segment.Payload);
    }

    private static async Task<(WebMediaMessage? First, WebMediaMessage? Second)> ReadTwoMessagesAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        (await WebMediaProtocol.ReadAsync(stream, cancellationToken),
         await WebMediaProtocol.ReadAsync(stream, cancellationToken));

    private static async Task WriteMessageAsync(
        Stream stream,
        byte type,
        string sessionId,
        long sequence,
        int durationMilliseconds,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var session = Encoding.UTF8.GetBytes(sessionId);
        var header = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4D575050);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
        header[6] = type;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), durationMilliseconds);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), (ushort)session.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(session, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }
}
