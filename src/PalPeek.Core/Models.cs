using System.Text.Json.Serialization;

namespace PalPeek.Core;

public static class Protocol
{
    public const int SchemaVersion = 1;
    public const int ApiPort = 48191;
    public const int MaxViewers = 3;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StreamQuality
{
    P720_30,
    P720_60,
    P1080_60
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureState
{
    Idle,
    Stabilizing,
    Ready,
    WindowUnavailable,
    AudioUnavailable,
    HostUnavailable
}

public sealed record GameInfo(
    uint AppId,
    string Name,
    string InstallDirectory,
    int ProcessId,
    long WindowHandle,
    string SessionId);

public sealed record NodeIdentity(string Id, string HostName, string DnsName, string Ip, bool Online);

public sealed record HostStatus(
    int SchemaVersion,
    string Nickname,
    string Version,
    bool Online,
    GameInfo? Game,
    CaptureState CaptureState,
    StreamQuality Quality,
    int ViewerCount,
    bool CanWatch,
    string? Message)
{
    public static HostStatus Offline(string nickname, string message) =>
        new(Protocol.SchemaVersion, nickname, BuildInfo.Version, false, null,
            CaptureState.Idle, StreamQuality.P720_60, 0, false, message);
}

public static class BuildInfo
{
    public const string Version = "0.4.6";
}

public sealed record PairRequest(int SchemaVersion, string ClientId, string Pin);
public sealed record ReservationRequest(int SchemaVersion, string SessionId, string ViewerId, string ViewerName);
public sealed record ReservationResponse(
    int SchemaVersion,
    string LeaseId,
    string SessionId,
    string Host,
    DateTimeOffset ExpiresAt,
    string WatchUri);
public sealed record ApiError(int SchemaVersion, string Code, string Message);

public sealed record PalPeekEvent(
    int SchemaVersion,
    string EventId,
    string Type,
    DateTimeOffset Timestamp,
    string Host,
    string? SessionId,
    GameInfo? Game,
    string? ViewerName,
    string? WatchUri);

public sealed class PalPeekOptions
{
    public string Nickname { get; set; } = Environment.UserName;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StreamQuality Quality { get; set; } = StreamQuality.P720_60;
    public bool Invisible { get; set; }
    public HashSet<uint> BlockedGameAppIds { get; set; } = [];
    public bool StartWithWindows { get; set; } = true;
}
