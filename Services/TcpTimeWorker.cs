using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GPS2NTP.Services;

public sealed class TcpTimeWorker : BackgroundService
{
    private readonly ILogger<TcpTimeWorker> _logger;
    private readonly TcpTimeOptions _options;
    private readonly GpsTimeSource _timeSource;

    private static readonly DateTimeOffset NtpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public TcpTimeWorker(
        ILogger<TcpTimeWorker> logger,
        IOptions<TcpTimeOptions> options,
        GpsTimeSource timeSource)
    {
        _logger = logger;
        _options = options.Value;
        _timeSource = timeSource;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Port == 0)
        {
            _logger.LogInformation("TCP TIME server disabled");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        _logger.LogInformation("TCP TIME server listening on port {Port}", _options.Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stoppingToken);
            _ = HandleClientAsync(client, stoppingToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var s = client.GetStream();
        var now = _timeSource.NowUtc().UtcDateTime;
        uint seconds = (uint)(now - NtpEpoch).TotalSeconds;
        byte[] buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, seconds);

        await s.WriteAsync(buf, token);
    }
}
