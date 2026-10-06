using System.Globalization;

namespace QueueCache.Desktop.Formatting;

/// <summary>Sizes, rates and durations shown one way everywhere: binary units, at most three
/// significant digits, words instead of zero where a zero would read as a measurement.</summary>
public static class Format
{
    private static readonly string[] Units = ["bytes", "KiB", "MiB", "GiB", "TiB"];
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static string Bytes(ulong bytes)
    {
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{Significant(value)} {Units[unit]}";
    }
    public static string Bytes(long bytes) => Bytes((ulong)Math.Max(0, bytes));

    /// <summary>A transfer rate given in MiB per second.</summary>
    public static string Rate(double mibPerSecond)
    {
        if (mibPerSecond < 0.05)
            return "Idle";
        return mibPerSecond >= 1024 ? $"{Significant(mibPerSecond / 1024)} GiB/s" : $"{Significant(mibPerSecond)} MiB/s";
    }

    public static string Duration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 1 => $"{duration.TotalMilliseconds:0} ms",
        < 60 => $"{Significant(duration.TotalSeconds)} s",
        < 3600 => $"{duration.TotalMinutes:0} min",
        _ => $"{duration.TotalHours:0.#} h"
    };

    public static string Percent(double percent) => percent >= 99.95 ? "100%" : $"{Significant(percent)}%";

    /// <summary>"14:02" today, "Yesterday 14:02", otherwise the date.</summary>
    public static string When(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = moment.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", Culture);
        return local.Date == today ? time : local.Date == today.AddDays(-1) ? $"Yesterday {time}" : local.ToString("d MMM yyyy", Culture) + " " + time;
    }

    // Three significant digits, without trailing zeros: 1.5, 12.3, 256, 1,024.
    private static string Significant(double value) => value switch
    {
        >= 100 => value.ToString("#,0", Culture),
        >= 10 => value.ToString("0.#", Culture),
        _ => value.ToString("0.##", Culture)
    };
}
