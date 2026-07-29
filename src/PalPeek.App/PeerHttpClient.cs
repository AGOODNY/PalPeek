namespace PalPeek;

internal static class PeerHttpClient
{
    public static HttpClient Create(TimeSpan timeout) =>
        new(new SocketsHttpHandler
        {
            // PalPeek peer APIs are reached directly through Tailscale. Sending
            // 100.x addresses through a system HTTP proxy can produce spurious
            // 502 responses while the Moonlight media path remains connected.
            UseProxy = false
        })
        {
            Timeout = timeout
        };
}
