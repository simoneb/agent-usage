using System.Globalization;

namespace ClaudeUsageWidget;

/// <summary>
/// Turns the reset stamp the CLI prints into something a reader can act on.
///
/// "Aug 15, 2:59am" answers the wrong question when the answer you want is whether to start
/// another task now. A countdown answers it directly, but only stays useful while the moment
/// is close — at six days out, a date is what you want back.
/// </summary>
public static class ResetTime
{
    /// <summary>Past this, a countdown stops being easier to read than the date.</summary>
    private static readonly TimeSpan CountdownHorizon = TimeSpan.FromHours(24);

    // The CLI omits the minutes at the top of the hour ("3am", not "3:00am"), and never prints
    // a year.
    private static readonly string[] Formats =
    {
        "MMM d, h:mmtt",
        "MMM d, htt",
        "MMM d, H:mm",
        "MMM d, H",
    };

    /// <summary>Drops the trailing zone the CLI appends, e.g. " (Europe/Rome)".</summary>
    public static string StripZone(string reset)
    {
        var open = reset.IndexOf(" (", StringComparison.Ordinal);
        return open > 0 ? reset[..open] : reset;
    }

    public static bool TryParse(string text, DateTime now, out DateTime reset)
    {
        // Designators arrive lowercase; the invariant culture's are not.
        var core = StripZone(text)
            .Replace("am", "AM", StringComparison.OrdinalIgnoreCase)
            .Replace("pm", "PM", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (!DateTime.TryParseExact(core, Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            reset = default;
            return false;
        }

        // No year in the text, so parsing assumes this one. A reset always lies ahead, so a
        // date that lands well behind us is a window that rolls over into next year.
        reset = parsed < now.AddDays(-1) ? parsed.AddYears(1) : parsed;
        return true;
    }

    /// <summary>
    /// A countdown while the reset is near, the original stamp once it is far enough away that
    /// counting down to it stops helping. Unparseable input falls back to the stamp, since a
    /// stamp we cannot read is still one the reader can.
    /// </summary>
    public static string Describe(string reset, DateTime now)
    {
        var stamp = StripZone(reset);

        if (!TryParse(reset, now, out var at)) return stamp;

        var remaining = at - now;

        if (remaining <= TimeSpan.Zero) return "any moment";
        if (remaining >= CountdownHorizon) return stamp;
        if (remaining < TimeSpan.FromMinutes(1)) return "in under a minute";
        if (remaining < TimeSpan.FromHours(1)) return $"in {(int)remaining.TotalMinutes}m";

        var hours = (int)remaining.TotalHours;

        return remaining.Minutes == 0 ? $"in {hours}h" : $"in {hours}h {remaining.Minutes}m";
    }
}
