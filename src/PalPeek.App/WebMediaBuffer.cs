using System.Globalization;
using System.Text;

namespace PalPeek;

public sealed record WebMediaSegment(long Sequence, int DurationMilliseconds, byte[] Payload);

public sealed class WebMediaBuffer
{
    private const int MaximumSegments = 12;
    private readonly object _gate = new();
    private string? _sessionId;
    private byte[]? _initialization;
    private readonly SortedDictionary<long, WebMediaSegment> _segments = new();
    private string? _error;
    private int _targetDurationSeconds = 1;

    public void Reset(string sessionId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
            _initialization = null;
            _segments.Clear();
            _error = null;
            _targetDurationSeconds = 1;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessionId = null;
            _initialization = null;
            _segments.Clear();
            _error = null;
            _targetDurationSeconds = 1;
        }
    }

    public bool SetInitialization(string sessionId, byte[] payload)
    {
        if (payload.Length is 0 or > 8 * 1024 * 1024)
            return false;
        lock (_gate)
        {
            if (_sessionId != sessionId)
                return false;
            _initialization = payload;
            return true;
        }
    }

    public bool AddSegment(string sessionId, WebMediaSegment segment)
    {
        if (segment.Payload.Length is 0 or > 8 * 1024 * 1024 ||
            segment.DurationMilliseconds is < 100 or > 10_000)
            return false;
        lock (_gate)
        {
            if (_sessionId != sessionId)
                return false;
            _segments[segment.Sequence] = segment;
            _targetDurationSeconds = Math.Max(
                _targetDurationSeconds,
                (int)Math.Ceiling(segment.DurationMilliseconds / 1000d));
            while (_segments.Count > MaximumSegments)
                _segments.Remove(_segments.Keys.First());
            return true;
        }
    }

    public void SetError(string sessionId, string message)
    {
        lock (_gate)
        {
            if (_sessionId == sessionId)
                _error = message;
        }
    }

    public byte[]? GetInitialization(string sessionId)
    {
        lock (_gate)
            return _sessionId == sessionId ? _initialization : null;
    }

    public WebMediaSegment? GetSegment(string sessionId, long sequence)
    {
        lock (_gate)
            return _sessionId == sessionId && _segments.TryGetValue(sequence, out var segment)
                ? segment
                : null;
    }

    public string? BuildPlaylist(string sessionId)
    {
        lock (_gate)
        {
            if (_sessionId != sessionId || _initialization is null || _segments.Count == 0)
                return null;
            var first = _segments.Values.First();
            var builder = new StringBuilder()
                .AppendLine("#EXTM3U")
                .AppendLine("#EXT-X-VERSION:7")
                .Append("#EXT-X-TARGETDURATION:")
                .AppendLine(_targetDurationSeconds.ToString(CultureInfo.InvariantCulture))
                .AppendLine($"#EXT-X-MEDIA-SEQUENCE:{first.Sequence}")
                .AppendLine("#EXT-X-INDEPENDENT-SEGMENTS")
                .AppendLine("#EXT-X-MAP:URI=\"init.mp4\"");
            foreach (var segment in _segments.Values)
            {
                builder.Append("#EXTINF:")
                    .Append((segment.DurationMilliseconds / 1000d)
                        .ToString("0.000", CultureInfo.InvariantCulture))
                    .AppendLine(",")
                    .Append("segment-")
                    .Append(segment.Sequence)
                    .AppendLine(".m4s");
            }
            return builder.ToString();
        }
    }

    public string? GetError(string sessionId)
    {
        lock (_gate)
            return _sessionId == sessionId ? _error : null;
    }
}
