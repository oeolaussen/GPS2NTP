using GPS2NTP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

// Bind options from appsettings.json
builder.Services.Configure<GpsOptions>(builder.Configuration.GetSection("Gps"));
builder.Services.Configure<NtpOptions>(builder.Configuration.GetSection("Ntp"));
builder.Services.Configure<TcpTimeOptions>(builder.Configuration.GetSection("TcpTime"));

// Allow running as Windows Service (optional)
builder.Services.AddWindowsService(o => o.ServiceName = "GPS2NTP");

// DI registrations
builder.Services.AddSingleton<GpsTimeSource>();
builder.Services.AddHostedService<NmeaReaderWorker>();
builder.Services.AddHostedService<NtpServerWorker>();
builder.Services.AddHostedService<TcpTimeWorker>();      // no-op if Port == 0
builder.Services.AddHostedService<StatusLoggerWorker>();

await builder.Build().RunAsync();
