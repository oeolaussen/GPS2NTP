using System.Diagnostics;

namespace GPS2NTP.Services;

public sealed class GpsTimeSource
{
    private readonly object _gate = new();
    private double? _offset;
    private DateTimeOffset? _lastFix;
    private bool _valid;
    private string? _lastSentence;

    public void NoteSentence(string s)
    {
        lock (_gate) _lastSentence = s.Length > 240 ? s[..240] : s;
    }

    public void Update(DateTimeOffset gpsUtc, bool valid)
    {
        lock (_gate)
        {
            double mono = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            double unix = gpsUtc.ToUnixTimeMilliseconds() / 1000.0;
            _offset = unix - mono;
            _lastFix = gpsUtc;
            _valid = valid;
        }
    }

    public (DateTimeOffset nowUtc, bool valid, DateTimeOffset? lastFix, string? lastSentence) Snapshot()
    {
        lock (_gate)
        {
            var now = _offset is null
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
                : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + _offset.Value;

            var sec = Math.Floor(now);
            var frac = now - sec;
            var dto = DateTimeOffset.FromUnixTimeSeconds((long)sec).AddSeconds(frac);
            return (dto, _valid, _lastFix, _lastSentence);
        }
    }

    public DateTimeOffset NowUtc()
    {
        lock (_gate)
        {
            var now = _offset is null
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
                : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + _offset.Value;

            var sec = Math.Floor(now);
            var frac = now - sec;
            return DateTimeOffset.FromUnixTimeSeconds((long)sec).AddSeconds(frac);
        }
    }

    public bool IsValid() { lock (_gate) return _valid; }
}
