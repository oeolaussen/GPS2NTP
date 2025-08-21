namespace GPS2NTP.Services;

public sealed class GpsOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 10110;
}

public sealed class NtpOptions
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 12300;
}

public sealed class TcpTimeOptions
{
    public int Port { get; set; } = 0; // 0 disables TCP TIME server
}
