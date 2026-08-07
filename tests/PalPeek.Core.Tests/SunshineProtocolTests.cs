using PalPeek.Core;
using System.Text.Json;

namespace PalPeek.Core.Tests;

public sealed class SunshineProtocolTests
{
    [Fact]
    public void SerializeCommand_UsesProtocolCamelCaseForInferredMembers()
    {
        var game = new GameInfo(
            250900,
            "The Binding of Isaac: Rebirth",
            @"E:\SteamLibrary\steamapps\common\The Binding of Isaac Rebirth",
            27372,
            133506,
            "session-1");

        var json = SunshineProtocol.SerializeCommand(new
        {
            protocolVersion = SunshineProtocol.Version,
            command = "setTarget",
            game.Name,
            game.SessionId
        });
        using var document = JsonDocument.Parse(json);

        Assert.Equal(game.Name, document.RootElement.GetProperty("name").GetString());
        Assert.Equal(game.SessionId, document.RootElement.GetProperty("sessionId").GetString());
        Assert.False(document.RootElement.TryGetProperty("Name", out _));
        Assert.False(document.RootElement.TryGetProperty("SessionId", out _));
    }

    [Fact]
    public void ParseStatusResponse_ReadsAllRuntimeStates()
    {
        const string json = """
            {
              "ok": true,
              "protocolVersion": 2,
              "capture": "capturing",
              "audio": "ready",
              "encoding": "streaming",
              "webStream": "streaming",
              "webStreamError": null,
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
        Assert.Equal(SunshineWebStreamStatus.Streaming, status.WebStream);
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
    public void EnsureSuccess_LocalizesWebStreamFailure()
    {
        const string json = """
            {
              "ok": false,
              "error": {
                "code": "web_stream_failed",
                "message": "AAC encoder is unavailable"
              }
            }
            """;

        var error = Assert.Throws<SunshineProtocolException>(
            () => SunshineProtocol.EnsureSuccess(json));

        Assert.Equal("web_stream_failed", error.Code);
        Assert.Equal("启动网页媒体流失败。", error.Message);
    }

    [Fact]
    public void ParseStatusResponse_RejectsProtocolMismatch()
    {
        const string json = """
            {
              "ok": true,
              "protocolVersion": 1,
              "capture": "idle",
              "audio": "idle",
              "encoding": "waitingForTarget",
              "webStream": "stopped",
              "target": null
            }
            """;

        var error = Assert.Throws<SunshineProtocolException>(
            () => SunshineProtocol.ParseStatusResponse(json));

        Assert.Equal("protocol_version_mismatch", error.Code);
    }

    [Theory]
    [InlineData("pairing_rejected", true)]
    [InlineData("invalid_pin", false)]
    [InlineData("internal_error", false)]
    public void PairingRetryOnlyHandlesPendingMoonlightRequest(string code, bool expected)
    {
        var error = new SunshineProtocolException(code, "test");

        Assert.Equal(expected, SunshineProtocol.IsRetryablePairingError(error));
    }

    [Theory]
    [InlineData("stale_session", true)]
    [InlineData("web_stream_failed", false)]
    [InlineData("internal_error", false)]
    public void WebStreamRetryOnlyHandlesStaleSession(string code, bool expected)
    {
        var error = new SunshineProtocolException(code, "test");

        Assert.Equal(expected, SunshineProtocol.IsStaleSessionError(error));
    }
}
