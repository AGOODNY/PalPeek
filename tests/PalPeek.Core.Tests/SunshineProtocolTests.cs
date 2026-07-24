using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class SunshineProtocolTests
{
    [Fact]
    public void ParseStatusResponse_ReadsAllRuntimeStates()
    {
        const string json = """
            {
              "ok": true,
              "protocolVersion": 1,
              "capture": "capturing",
              "audio": "ready",
              "encoding": "streaming",
              "target": {
                "pid": 42,
                "hwnd": 123456,
                "sessionId": "session-1",
                "generation": 7
              },
              "errorCode": null,
              "message": null
            }
            """;

        var status = SunshineProtocol.ParseStatusResponse(json);

        Assert.Equal(SunshineCaptureStatus.Capturing, status.Capture);
        Assert.Equal(SunshineAudioStatus.Ready, status.Audio);
        Assert.Equal(SunshineEncodingStatus.Streaming, status.Encoding);
        Assert.Equal(42, status.Target?.Pid);
        Assert.Equal(123456, status.Target?.Hwnd);
        Assert.Equal("session-1", status.Target?.SessionId);
    }

    [Fact]
    public void EnsureSuccess_ThrowsStructuredError()
    {
        const string json = """
            {
              "ok": false,
              "error": {
                "code": "invalid_window",
                "message": "The selected window is unavailable."
              }
            }
            """;

        var error = Assert.Throws<SunshineProtocolException>(
            () => SunshineProtocol.EnsureSuccess(json));

        Assert.Equal("invalid_window", error.Code);
        Assert.Equal("目标游戏窗口无效或已关闭。", error.Message);
    }

    [Fact]
    public void ParseStatusResponse_RejectsProtocolMismatch()
    {
        const string json = """
            {
              "ok": true,
              "protocolVersion": 2,
              "capture": "idle",
              "audio": "idle",
              "encoding": "waitingForTarget",
              "target": null
            }
            """;

        var error = Assert.Throws<SunshineProtocolException>(
            () => SunshineProtocol.ParseStatusResponse(json));

        Assert.Equal("protocol_version_mismatch", error.Code);
    }
}
