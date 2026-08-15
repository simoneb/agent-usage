using AgentUsage;
using static ClaudeUsageWidget.Native;

namespace ClaudeUsageWidget;

/// <summary>
/// Builds the app icon.
///
/// Two things can stop you working, on different horizons: the session window, which resets in
/// hours, and the weekly one, which locks you out for days. Both belong in the icon. There is
/// only so much room, so detail is dropped in a fixed order as accounts are added:
///
///   1 account    digits for the week, with the session as a strip along the top edge
///   2 accounts   a bar pair per account — session above week, matching the panel's row order
///   3-4 accounts one bar per account, week only
///   5+           digits for the worst week
///
/// Colour always means severity and never identity; accounts are told apart by position, in
/// config order, so the panel reads as the icon's legend.
/// </summary>
internal static class IconBuilder
{
    private const int MaxPairs = 2;
    private const int MaxBars = 4;

    private static readonly uint Slab = Rgb(0x17, 0x17, 0x1A);
    private static readonly uint Track = Rgb(0x3A, 0x3A, 0x44);
    private static readonly uint Unknown = Rgb(0x6E, 0x6E, 0x78);
    private static readonly uint Ink = Rgb(0x12, 0x12, 0x16);

    private readonly record struct Reading(int Week, int? Session);

    /// <param name="iconLimit">
    /// A row label fragment to report instead of the default pairing. When set, every account
    /// contributes that one number and nothing else — the reader asked for a single metric, so
    /// showing a second one alongside it would answer a question they did not ask.
    /// </param>
    public static IntPtr Build(IReadOnlyList<AccountStatus> statuses, int size, string? iconLimit)
    {
        // Only accounts reporting a real number can be drawn. A signed-out or failed one has
        // no bar to show, and inventing an empty one would read as "0% used".
        var readings = new List<Reading>();
        var anyError = false;

        foreach (var s in statuses)
        {
            if (s.Error is not null) { anyError = true; continue; }

            if (iconLimit is not null)
            {
                if (s.PercentFor(iconLimit) is int chosen)
                    readings.Add(new Reading(Math.Clamp(chosen, 0, 100), null));

                continue;
            }

            if (s.HeadlinePercent is not int week) continue;

            readings.Add(new Reading(
                Math.Clamp(week, 0, 100),
                s.SessionPercent is int session ? Math.Clamp(session, 0, 100) : null));
        }

        using var surface = new DibSurface(size, size);

        if (readings.Count == 0)
        {
            DrawBadge(surface.Hdc, size, Unknown, anyError ? "!" : "–", null);
        }
        else if (readings.Count == 1)
        {
            var only = readings[0];
            DrawBadge(surface.Hdc, size, Renderer.ColorFor(only.Week),
                only.Week.ToString(), only.Session);
        }
        else if (readings.Count <= MaxPairs && iconLimit is null)
        {
            DrawPairs(surface.Hdc, size, readings);
        }
        else if (readings.Count <= MaxBars)
        {
            DrawBars(surface.Hdc, size, readings);
        }
        else
        {
            var worst = readings.Max(r => r.Week);
            DrawBadge(surface.Hdc, size, Renderer.ColorFor(worst), worst.ToString(), null);
        }

        surface.ForceOpaque();

        // CreateIconIndirect still wants a mask bitmap; an all-zero mask means "fully visible".
        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);

        var info = new ICONINFO
        {
            fIcon = 1,
            hbmMask = mask,
            hbmColor = surface.Bitmap,
        };

        var icon = CreateIconIndirect(ref info);
        DeleteObject(mask);

        return icon;
    }

    /// <summary>
    /// A solid colour block with the week percentage on it, and the session as a strip across
    /// the top — above the digits, the way session sits above week in the panel.
    /// </summary>
    private static void DrawBadge(IntPtr hdc, int size, uint colour, string text, int? session)
    {
        Fill(hdc, 0, 0, size, size, colour);

        var stripHeight = 0;

        if (session is int pct)
        {
            stripHeight = Math.Max(2, size / 8);

            Fill(hdc, 0, 0, size, stripHeight, Slab);

            var filled = (int)Math.Round(size * pct / 100.0);
            if (pct > 0) filled = Math.Max(filled, 1);
            if (filled > 0) Fill(hdc, 0, 0, filled, stripHeight, Renderer.ColorFor(pct));
        }

        SetBkMode(hdc, TRANSPARENT);
        SetTextColor(hdc, Ink);

        var fontHeight = text.Length >= 3 ? size * 0.52 : size * 0.66;
        var font = CreateFontW(-(int)Math.Round(fontHeight), 0, 0, 0, 700, 0, 0, 0,
            DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        var oldFont = SelectObject(hdc, font);

        // Centre the digits in what is left below the strip, not in the whole square.
        var rect = new RECT { Left = 0, Top = stripHeight, Right = size, Bottom = size };
        DrawTextW(hdc, text, text.Length, ref rect,
            DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER | 0x0001 /* DT_CENTER */);

        SelectObject(hdc, oldFont);
        DeleteObject(font);
    }

    /// <summary>
    /// Session above week for each account, the two touching so a pair reads as one object,
    /// with the gap reserved for separating accounts.
    /// </summary>
    private static void DrawPairs(IntPtr hdc, int size, List<Reading> readings)
    {
        Fill(hdc, 0, 0, size, size, Slab);

        var pad = Math.Max(1, size / 16);
        var groupGap = Math.Max(2, size / 8);

        var rows = readings.Count * 2;
        var barHeight = Math.Max(1,
            (size - pad * 2 - groupGap * (readings.Count - 1)) / rows);

        var blockHeight = barHeight * rows + groupGap * (readings.Count - 1);
        var top = (size - blockHeight) / 2;

        var left = pad;
        var right = size - pad;

        for (var i = 0; i < readings.Count; i++)
        {
            var y = top + i * (barHeight * 2 + groupGap);

            // No session row means the CLI did not report one; leave its track empty rather
            // than borrowing the weekly number and implying a reading that does not exist.
            DrawBar(hdc, left, y, right, y + barHeight, readings[i].Session);
            DrawBar(hdc, left, y + barHeight, right, y + barHeight * 2, readings[i].Week);
        }
    }

    /// <summary>One bar per account, week only — the horizon that costs days.</summary>
    private static void DrawBars(IntPtr hdc, int size, List<Reading> readings)
    {
        Fill(hdc, 0, 0, size, size, Slab);

        var pad = Math.Max(1, size / 8);
        var gap = Math.Max(1, size / 16);

        // Whatever is left over after padding and gaps, split evenly. Integer division can
        // leave a row or two unused; centring the block hides that.
        var barHeight = Math.Max(1, (size - pad * 2 - gap * (readings.Count - 1)) / readings.Count);
        var blockHeight = barHeight * readings.Count + gap * (readings.Count - 1);
        var top = (size - blockHeight) / 2;

        for (var i = 0; i < readings.Count; i++)
        {
            var y = top + i * (barHeight + gap);
            DrawBar(hdc, pad, y, size - pad, y + barHeight, readings[i].Week);
        }
    }

    private static void DrawBar(IntPtr hdc, int left, int top, int right, int bottom, int? pct)
    {
        Fill(hdc, left, top, right, bottom, Track);

        if (pct is not int value) return;

        var filled = (int)Math.Round((right - left) * value / 100.0);

        // A non-zero reading must never render as an empty track, so anything above zero keeps
        // at least one visible column.
        if (value > 0) filled = Math.Max(filled, 1);

        if (filled > 0)
            Fill(hdc, left, top, left + filled, bottom, Renderer.ColorFor(value));
    }

    private static void Fill(IntPtr hdc, int left, int top, int right, int bottom, uint colour)
    {
        if (right <= left || bottom <= top) return;

        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        var brush = CreateSolidBrush(colour);
        FillRect(hdc, ref rect, brush);
        DeleteObject(brush);
    }
}
