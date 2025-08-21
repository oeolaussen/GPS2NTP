using System.Globalization;

namespace GPS2NTP.Services;

/// <summary>
/// Minimal, robust NMEA parser for time-bearing sentences.
/// - Parses $--RMC (all talkers, case-insensitive) and $--ZDA
/// - Ignores AIS (!AIVDM/!AIVDO and $--VDM/$--VDO, case-insensitive)
/// - Checksum tolerant to leading text before '$'/'!' (e.g., "Conn init$GPRMC...")
/// - Supports hhmmss with/without fractional seconds
/// - Maps years: 00–79 → 2000–2079, 80–99 → 1980–1999
/// </summary>
public static class NmeaParser
{
    /// <summary>If true, reject bad checksums; if false, still try to parse.</summary>
    public static bool RequireChecksum { get; set; } = false;

    /// <summary>
    /// Returns true if this line should be ignored (AIS etc.).
    /// </summary>
    public static bool ShouldIgnore(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;

        line = line.Trim();

        // AIS frames always start with '!'
        if (line.StartsWith("!AIVDM", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("!AIVDO", StringComparison.OrdinalIgnoreCase)) return true;

        // Some bridges forward AIS as NMEA sentences with $..VDM / $..VDO
        if (line.Length >= 6 && line[0] == '$')
        {
            char c3 = char.ToUpperInvariant(line[3]);
            char c4 = char.ToUpperInvariant(line[4]);
            char c5 = char.ToUpperInvariant(line[5]);
            if (c3 == 'V' && c4 == 'D' && (c5 == 'M' || c5 == 'O'))
                return true;
        }

        return false;
    }

    public static bool IsRmc(string line)
    {
        if (line.Length < 7 || line[0] != '$' || line[6] != ',') return false;
        return char.ToUpperInvariant(line[3]) == 'R'
            && char.ToUpperInvariant(line[4]) == 'M'
            && char.ToUpperInvariant(line[5]) == 'C';
    }

    public static bool IsZda(string line)
    {
        if (line.Length < 7 || line[0] != '$' || line[6] != ',') return false;
        return char.ToUpperInvariant(line[3]) == 'Z'
            && char.ToUpperInvariant(line[4]) == 'D'
            && char.ToUpperInvariant(line[5]) == 'A';
    }

    /// <summary>
    /// Verifies NMEA checksum (XOR of chars between '$'/'!' and '*'),
    /// tolerant of leading noise.
    /// </summary>
    public static bool ChecksumOk(string sentence)
    {
        if (string.IsNullOrEmpty(sentence)) return false;

        int star = sentence.LastIndexOf('*');
        if (star < 0 || star + 3 > sentence.Length) return false;

        int startSymbol = sentence.IndexOfAny(new[] { '$', '!' });
        if (startSymbol < 0 || startSymbol + 1 >= star) return false;

        int cs = 0;
        for (int i = startSymbol + 1; i < star; i++)
            cs ^= sentence[i];

        return byte.TryParse(sentence.AsSpan(star + 1, 2),
                             NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                             out var given)
               && cs == given;
    }

    /// <summary>
    /// Try to parse either RMC or ZDA into a UTC time.
    /// </summary>
    public static bool TryParseSentence(string line, out DateTimeOffset utc, out bool valid)
    {
        utc = default; valid = false;

        if (IsRmc(line)) return TryParseRmc(line, out utc, out valid);
        if (IsZda(line)) return TryParseZda(line, out utc);
        return false;
    }

    /// <summary>
    /// Parses $--RMC time + date. Returns UTC and validity flag (A=valid).
    /// </summary>
    public static bool TryParseRmc(string line, out DateTimeOffset utc, out bool valid)
    {
        utc = default; valid = false;
        if (RequireChecksum && !ChecksumOk(line)) return false;

        int star = line.IndexOf('*');
        string core = star > 0 ? line[..star] : line;
        string[] p = core.Split(',');
        if (p.Length < 10) return false;

        string timeStr = p[1];   // hhmmss(.sss)
        string status = p[2];    // A/V
        string dateStr = p[9];   // ddmmyy

        if (!TryParseHhMmSsMicros(timeStr, out int hh, out int mm, out int ss, out int micros)) return false;
        if (!TryParseDdMmYy(dateStr, out int dd, out int MM, out int yyyy)) return false;

        try
        {
            utc = new DateTimeOffset(yyyy, MM, dd, hh, mm, ss, TimeSpan.Zero).AddTicks(micros * 10);
            valid = string.Equals(status, "A", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Parses $--ZDA time + date. Returns UTC (valid implied).
    /// </summary>
    public static bool TryParseZda(string line, out DateTimeOffset utc)
    {
        utc = default;
        if (RequireChecksum && !ChecksumOk(line)) return false;

        int star = line.IndexOf('*');
        string core = star > 0 ? line[..star] : line;
        string[] p = core.Split(',');
        // $--ZDA,hhmmss(.sss),dd,MM,yyyy,ltzh,ltzm
        if (p.Length < 5) return false;

        string timeStr = p[1];
        if (!int.TryParse(p[2], out int dd)) return false;
        if (!int.TryParse(p[3], out int MM)) return false;
        if (!int.TryParse(p[4], out int yyyy)) return false;

        if (!TryParseHhMmSsMicros(timeStr, out int hh, out int mm, out int ss, out int micros)) return false;

        try
        {
            utc = new DateTimeOffset(yyyy, MM, dd, hh, mm, ss, TimeSpan.Zero).AddTicks(micros * 10);
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseHhMmSsMicros(string s, out int hh, out int mm, out int ss, out int micros)
    {
        hh = mm = ss = micros = 0;
        if (string.IsNullOrEmpty(s) || s.Length < 6) return false;

        if (!int.TryParse(s.AsSpan(0, 2), out hh)) return false;
        if (!int.TryParse(s.AsSpan(2, 2), out mm)) return false;
        if (!int.TryParse(s.AsSpan(4, 2), out ss)) return false;

        int dot = s.IndexOf('.');
        if (dot >= 0 && dot + 1 < s.Length)
        {
            string frac = s[(dot + 1)..];
            if (frac.Length > 6) frac = frac[..6];
            while (frac.Length < 6) frac += "0";
            _ = int.TryParse(frac, NumberStyles.None, CultureInfo.InvariantCulture, out micros);
        }
        return true;
    }

    private static bool TryParseDdMmYy(string s, out int dd, out int MM, out int yyyy)
    {
        dd = MM = yyyy = 0;
        if (string.IsNullOrEmpty(s) || s.Length != 6) return false;

        if (!int.TryParse(s.AsSpan(0, 2), out dd)) return false;
        if (!int.TryParse(s.AsSpan(2, 2), out MM)) return false;
        if (!int.TryParse(s.AsSpan(4, 2), out int yy)) return false;

        yyyy = (yy <= 79) ? 2000 + yy : 1900 + yy;
        return true;
    }
}
