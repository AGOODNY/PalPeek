using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalPeek.Core;

public enum SunshineProcessState
{
    Stopped,
    Starting,
    Running,
    Recovering,
    Stopping,
    Faulted,
    NotInstalled
}

public enum SunshineCaptureStatus
{
    Idle,
    TargetReady,
    Capturing,
    Error
}

public enum SunshineAudioStatus
{
    Idle,
    Ready,
    Capturing,
    Error
}

public enum SunshineEncodingStatus
{
    WaitingForTarget,
    Probing,
    Ready,
    Streaming,
    Error
}

public sealed record SunshineTargetStatus(
    int Pid,
    long Hwnd,
    string SessionId,
    ulong Generation);

public sealed record SunshineRuntimeStatus(
    int ProtocolVersion,
    SunshineCaptureStatus Capture,
    SunshineAudioStatus Audio,
    SunshineEncodingStatus Encoding,
    SunshineTargetStatus? Target,
    string? ErrorCode,
    string? Message);

public sealed record SunshineLifecycleStatus(
    SunshineProcessState State,
    int? ProcessId,
    int RestartCount,
    string? Message);

public sealed class SunshineProtocolException : Exception
{
    public SunshineProtocolException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}

public static class SunshineProtocol
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static SunshineRuntimeStatus ParseStatusResponse(string response)
    {
        using var document = ParseSuccessfulResponse(response);
        var root = document.RootElement;
        var protocolVersion = root.GetProperty("protocolVersion").GetInt32();
        if (protocolVersion != Version)
            throw new SunshineProtocolException(
                "protocol_version_mismatch",
                $"PalPeek Host IPC 协议版本不兼容（Host={protocolVersion}，PalPeek={Version}）。");

        return new SunshineRuntimeStatus(
            protocolVersion,
            ReadEnum<SunshineCaptureStatus>(root, "capture"),
            ReadEnum<SunshineAudioStatus>(root, "audio"),
            ReadEnum<SunshineEncodingStatus>(root, "encoding"),
            root.TryGetProperty("target", out var target) && target.ValueKind != JsonValueKind.Null
                ? JsonSerializer.Deserialize<SunshineTargetStatus>(target.GetRawText(), JsonOptions)
                : null,
            root.TryGetProperty("errorCode", out var errorCode) && errorCode.ValueKind == JsonValueKind.String
                ? errorCode.GetString()
                : null,
            root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null);
    }

    public static void EnsureSuccess(string response)
    {
        using var _ = ParseSuccessfulResponse(response);
    }

    private static JsonDocument ParseSuccessfulResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new SunshineProtocolException("empty_response", "PalPeek Host 未返回 IPC 响应。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response);
        }
        catch (JsonException ex)
        {
            throw new SunshineProtocolException("invalid_response", $"PalPeek Host 返回了无效响应：{ex.Message}");
        }

        var root = document.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return document;

        var code = "command_failed";
        var message = "PalPeek Host 拒绝了命令。";
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var errorCode))
                    code = errorCode.GetString() ?? code;
                if (error.TryGetProperty("message", out var errorMessage))
                    message = errorMessage.GetString() ?? message;
            }
            else if (error.ValueKind == JsonValueKind.String)
            {
                message = error.GetString() ?? message;
            }
        }

        document.Dispose();
        throw new SunshineProtocolException(code, message);
    }

    private static T ReadEnum<T>(JsonElement root, string propertyName) where T : struct, Enum
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<T>(value.GetString(), true, out var result))
            throw new SunshineProtocolException(
                "invalid_response",
                $"PalPeek Host 响应缺少有效的 {propertyName} 状态。");
        return result;
    }
}
