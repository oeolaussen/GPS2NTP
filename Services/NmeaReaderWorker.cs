using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace GPS2NTP.Services;

public sealed class NmeaReaderWorker : BackgroundService
{
    private readonly ILogger<NmeaReaderWorker> _logger;
    private readonly GpsOptions _gps;
    private readonly GpsTimeSource _ts;

    public NmeaReaderWorker(
        ILogger<NmeaReaderWorker> logger,
        IOptions<GpsOptions> gpsOptions,
        GpsTimeSource timeSource)
    {
        _logger = logger;
        _gps = gpsOptions.Value;
        _ts = timeSource;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                _logger.LogInformation("NMEA: connecting to {Host}:{Port}…", _gps.Host, _gps.Port);
                client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(_gps.Host, _gps.Port, stoppingToken);
                _logger.LogInformation("NMEA: {Host}:{Port} connected.", _gps.Host, _gps.Port);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested) break;
                _logger.LogWarning("NMEA: connect failed to {Host}:{Port} ({Msg}). Retry in 5s…",
                    _gps.Host, _gps.Port, ex.Message);
                client?.Dispose();
                await DelaySafe(stoppingToken);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("NMEA: unexpected error while connecting: {Msg}. Retry in 5s…", ex.Message);
                client?.Dispose();
                await DelaySafe(stoppingToken);
                continue;
            }

            // ---- Read loop ----
            long seen = 0, ais = 0, badCs = 0, parsed = 0, bad = 0;
            var nextStatsAt = DateTime.UtcNow.AddSeconds(10);

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var raw = await reader.ReadLineAsync();
                    if (raw is null) throw new IOException("connection closed by remote");

                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    seen++;

                    // keep only the NMEA part (some tools prepend banners)
                    int sym = line.IndexOfAny(new[] { '$', '!' });
                    if (sym < 0) continue;
                    if (sym > 0) line = line[sym..];

                    // --- AIS / VDM filter ---
                    if (NmeaParser.ShouldIgnore(line))
                    {
                        ais++;
                        _logger.LogDebug("Ignored AIS/VDM line: {Line}", line);
                        goto STATS;
                    }

                    // --- Checksum check ---
                    if (!NmeaParser.ChecksumOk(line))
                        badCs++; // we still try parsing

                    // --- Parse ---
                    if (NmeaParser.TryParseSentence(line, out var gpsUtc, out var valid))
                    {
                        _ts.Update(gpsUtc, valid);
                        _ts.NoteSentence(line);
                        parsed++;
                    }
                    else
                    {
                        bad++;
                    }

                STATS:
                    if (DateTime.UtcNow >= nextStatsAt)
                    {
                        _logger.LogInformation(
                            "NMEA stats: seen={Seen}, parsed={Parsed}, aisIgnored={AIS}, badChecksum={BadCs}, unparsable={Bad}",
                            seen, parsed, ais, badCs, bad);
                        nextStatsAt = DateTime.UtcNow.AddSeconds(10);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                if (stoppingToken.IsCancellationRequested) break;
                _logger.LogWarning("NMEA: connection lost ({Msg}). Reconnecting in 5s…", ex.Message);
                await DelaySafe(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("NMEA: unexpected error ({Msg}). Reconnecting in 5s…", ex.Message);
                await DelaySafe(stoppingToken);
            }
            finally
            {
                try { client?.Close(); client?.Dispose(); } catch { }
            }
        }
    }

    private static async Task DelaySafe(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
    }
}
