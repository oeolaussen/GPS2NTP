using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GPS2NTP.Services;

public sealed class NtpServerWorker : BackgroundService
{
    private readonly ILogger<NtpServerWorker> _logger;
    private readonly NtpOptions _cfg;
    private readonly GpsTimeSource _ts;

    public NtpServerWorker(
        ILogger<NtpServerWorker> logger,
        IOptions<NtpOptions> opt,
        GpsTimeSource ts)
    {
        _logger = logger;
        _cfg = opt.Value;
        _ts = ts;
    }

    private static readonly DateTime NtpEpochUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bind IPv4 only (simple & compatible). If you need dual-stack, use a Socket with DualMode.
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(_cfg.Host), _cfg.Port));
        _logger.LogInformation("NTP/UDP listening on {Host}:{Port}", _cfg.Host, _cfg.Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                var rxTime = _ts.NowUtc(); // when we received the request

                // build and send in a background task so we don't block the receive loop
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var resp = BuildResponse(result.Buffer, rxTime);
                        if (resp.Length == 48)
                            await udp.SendAsync(resp, result.RemoteEndPoint);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "NTP request handling error");
                    }
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NTP socket error");
            }
        }
    }

    private byte[] BuildResponse(byte[] req, DateTimeOffset rxTime)
    {
        // Must be at least a 48-byte client request
        if (req is null || req.Length < 48) return Array.Empty<byte>();

        var (nowUtc, valid, lastFix, _) = _ts.Snapshot();

        // Version: reflect client (3 or 4), otherwise respond v4
        byte vn = (byte)((req[0] >> 3) & 0x7);
        if (vn < 3 || vn > 4) vn = 4;

        var resp = new byte[48];

        // LI (0 if OK, 3 = alarm if unsynchronised), VN, Mode=4 (server)
        byte li = (byte)(valid ? 0 : 3);
        resp[0] = (byte)((li << 6) | (vn << 3) | 4);

        // Stratum: 1 if GPS valid, else 16 (unsynchronised)
        resp[1] = (byte)(valid ? 1 : 16);

        // Poll & precision
        resp[2] = (req.Length > 2) ? req[2] : (byte)6;     // echo client poll if present
        resp[3] = unchecked((byte)-20);                    // precision ≈ 1 µs

        // Root delay & dispersion
        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(4, 4), 0);                            // 0 seconds
        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(8, 4), (uint)(0.0005 * (1 << 16)));   // ~0.5 ms

        // Reference ID: "GPS\0"
        System.Text.Encoding.ASCII.GetBytes("GPS\0").CopyTo(resp.AsSpan(12, 4));

        // Reference timestamp (time of last update from GPS, else now)
        WriteNtpTimestamp(resp.AsSpan(16, 8), lastFix ?? nowUtc);

        // Originate timestamp = client's Transmit (bytes 40..47 of request)
        Array.Copy(req, 40, resp, 24, 8);

        // Receive timestamp (when we received the request)
        WriteNtpTimestamp(resp.AsSpan(32, 8), rxTime);

        // Transmit timestamp (right now)
        WriteNtpTimestamp(resp.AsSpan(40, 8), _ts.NowUtc());

        return resp;
    }

    private static void WriteNtpTimestamp(Span<byte> dest, DateTimeOffset utc)
    {
        var delta = utc.UtcDateTime - NtpEpochUtc;
        double total = delta.TotalSeconds;
        uint seconds = (uint)Math.Floor(total);
        uint fraction = (uint)((total - seconds) * 4294967296.0); // 2^32
        BinaryPrimitives.WriteUInt32BigEndian(dest[..4], seconds);
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(4, 4), fraction);
    }
}
