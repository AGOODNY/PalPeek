using System.Net;

namespace PalPeek.Core;

public static class PeerConnectionPolicy
{
    public static bool IsTransientHeartbeatFailure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;
}
