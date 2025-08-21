using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GPS2NTP.Services;

public sealed class StatusLoggerWorker : BackgroundService
{
    private readonly ILogger<StatusLoggerWorker> _logger;
    private readonly GpsTimeSource _timeSource;

    public StatusLoggerWorker(
        ILogger<StatusLoggerWorker> logger,
        GpsTimeSource timeSource)
    {
        _logger = logger;
        _timeSource = timeSource;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var (now, valid, lastFix, lastSentence) = _timeSource.Snapshot();
            _logger.LogInformation("Now: {Now:O}, Valid: {Valid}, LastFix: {Fix:O}, Sentence: {Sentence}",
                now, valid, lastFix, lastSentence);
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
