using AgentUsage;
using static AgentUsage.Widget.Native;

namespace AgentUsage.Widget;

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
///
/// Every bar also carries the pace: a dark notch where an even burn would have reached by now,
/// and the fill past that notch a shade lighter. So the icon answers two questions at a glance
/// rather than one — how much is gone, and whether it is going faster than it refills. Anything
/// bright to the right of a notch is spending that the window will not have paid back by the
/// time it resets.
/// </summary>
internal static class IconBuilder
{
    private const int MaxPairs = 2;
    private const int MaxBars = 4;

    private static readonly uint Slab = Rgb(0x17, 0x17, 0x1A);
    private static readonly uint Track = Rgb(0x3A, 0x3A, 0x44);
    private static readonly uint Unknown = Rgb(0x6E, 0x6E, 0x78);
    private static readonly uint Ink = Rgb(0x12, 0x12, 0x16);

    /// <summary>
    /// One window: how much of it is spent, and how much of it has gone by. <see cref="Pace"/>
    /// is null for a window whose length the provider never states, and a bar with no pace is
    /// drawn exactly as it was before — an unmarked bar is honest, a notch placed by guesswork
    /// is not.
    /// </summary>
    private readonly record struct Gauge(int Used, int? Pace);

    private readonly record struct Reading(Gauge Week, Gauge? Session);

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

        var now = DateTimeOffset.Now;

        foreach (var s in statuses)
        {
            if (s.Error is not null) { anyError = true; continue; }

            if (iconLimit is not null)
            {
                if (Read(s.LimitFor(iconLimit), now) is Gauge chosen)
                    readings.Add(new Reading(chosen, null));

                continue;
            }

            if (Read(s.HeadlineLimit, now) is not Gauge week) continue;

            readings.Add(new Reading(week, Read(s.SessionLimit, now)));
        }

        using var surface = new DibSurface(size, size);

        if (readings.Count == 0)
        {
            DrawBadge(surface.Hdc, size, Unknown, anyError ? "!" : "–", null, null);
        }
        else if (readings.Count == 1)
        {
            var only = readings[0];
            DrawBadge(surface.Hdc, size, Renderer.ColorFor(only.Week.Used),
                only.Week.Used.ToString(), only.Session, only.Week);
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
            // One number for many accounts, so there is no single window behind it and no pace
            // to mark: the worst week here and the clock it runs against belong to whichever
            // account happens to be worst, and a notch from one bar under a figure from another
            // would be a comparison nobody made.
            var worst = readings.Max(r => r.Week.Used);
            DrawBadge(surface.Hdc, size, Renderer.ColorFor(worst), worst.ToString(), null, null);
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
    /// A solid colour block with the week percentage on it, and a strip along each edge that has
    /// something to say: the session across the top — above the digits, the way session sits
    /// above week in the panel — and the week across the bottom.
    ///
    /// The bottom strip repeats the number the digits already give, which is the point: a figure
    /// cannot show a notch. The strip exists to put the week's spending beside the week's clock,
    /// so it is drawn only when there is a pace to mark and left off entirely when there is not.
    /// </summary>
    private static void DrawBadge(
        IntPtr hdc, int size, uint colour, string text, Gauge? session, Gauge? week)
    {
        Fill(hdc, 0, 0, size, size, colour);

        var strip = Math.Max(2, size / 8);

        // The week strip earns its pixels only when it has a notch to carry; the session strip
        // earns them whenever there is a session at all, since the digits say nothing about it.
        var top = session is not null ? strip : 0;
        var bottom = week is { Pace: not null } ? strip : 0;

        if (session is Gauge above) DrawBar(hdc, 0, 0, size, top, above);
        if (week is Gauge below && bottom > 0) DrawBar(hdc, 0, size - bottom, size, size, below);

        SetBkMode(hdc, TRANSPARENT);
        SetTextColor(hdc, Ink);

        var fontHeight = text.Length >= 3 ? size * 0.52 : size * 0.66;
        var font = CreateFontW(-(int)Math.Round(fontHeight), 0, 0, 0, 700, 0, 0, 0,
            DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        var oldFont = SelectObject(hdc, font);

        // Centre the digits in what is left between the strips, not in the whole square.
        var rect = new RECT { Left = 0, Top = top, Right = size, Bottom = size - bottom };
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

        var gap = Math.Max(2, size / 8);
        var heights = SplitRows(size, readings.Count * 2, gap, readings.Count - 1);

        var y = 0;

        for (var i = 0; i < readings.Count; i++)
        {
            // No session row means the CLI did not report one; leave its track empty rather
            // than borrowing the weekly number and implying a reading that does not exist.
            DrawBar(hdc, 0, y, size, y + heights[i * 2], readings[i].Session);
            y += heights[i * 2];

            DrawBar(hdc, 0, y, size, y + heights[i * 2 + 1], readings[i].Week);
            y += heights[i * 2 + 1] + (i < readings.Count - 1 ? gap : 0);
        }
    }

    /// <summary>One bar per account, week only — the horizon that costs days.</summary>
    private static void DrawBars(IntPtr hdc, int size, List<Reading> readings)
    {
        Fill(hdc, 0, 0, size, size, Slab);

        var gap = Math.Max(1, size / 16);
        var heights = SplitRows(size, readings.Count, gap, readings.Count - 1);

        var y = 0;

        for (var i = 0; i < readings.Count; i++)
        {
            DrawBar(hdc, 0, y, size, y + heights[i], readings[i].Week);
            y += heights[i] + gap;
        }
    }

    /// <summary>
    /// Row heights that add up to exactly the space available, remainder spread over the top
    /// rows a pixel at a time. At tray sizes the whole icon is sixteen pixels tall, so rounding
    /// every row down the same way — the obvious thing — throws away up to three of them and the
    /// icon renders visibly shorter than the ones either side of it.
    /// </summary>
    internal static int[] SplitRows(int size, int rows, int gap, int gaps)
    {
        var ink = Math.Max(rows, size - gap * gaps);
        var height = ink / rows;
        var spare = ink - height * rows;

        var heights = new int[rows];
        for (var i = 0; i < rows; i++) heights[i] = height + (i < spare ? 1 : 0);

        return heights;
    }

    private static void DrawBar(IntPtr hdc, int left, int top, int right, int bottom, Gauge? gauge)
    {
        Fill(hdc, left, top, right, bottom, Track);

        if (gauge is not Gauge g) return;

        var width = right - left;
        var filled = (int)Math.Round(width * g.Used / 100.0);

        // A non-zero reading must never render as an empty track, so anything above zero keeps
        // at least one visible column.
        if (g.Used > 0) filled = Math.Max(filled, 1);

        var colour = Renderer.ColorFor(g.Used);

        if (filled > 0) Fill(hdc, left, top, left + filled, bottom, colour);

        if (g.Pace is not int budget) return;

        var mark = (int)Math.Round(width * budget / 100.0);

        // The fill past the notch is spending the clock has not caught up with. A lighter shade
        // of the bar's own colour, so hue still means severity and only brightness means overrun.
        if (filled - mark >= 2)
            Fill(hdc, left + mark, top, left + filled, bottom, Renderer.Brighten(colour));

        // The notch itself, cut through fill and track alike: at tray sizes a whole bar is
        // sixteen pixels wide, and a boundary you have to find by comparing two lengths is one
        // nobody finds. A dark column splits the bar into "spent so far" and "spent early", and
        // any colour on the far side of it is the second.
        var thickness = Math.Max(1, width / 24);
        var notch = Math.Clamp(left + mark - thickness / 2, left, right - thickness);

        Fill(hdc, notch, top, notch + thickness, bottom, Slab);
    }

    /// <summary>
    /// What the icon needs from a limit row: its percentage, and how far through its window we
    /// are. Null for a row that cannot produce a percentage at all — a count with no ceiling, or
    /// a window that has already rolled over.
    /// </summary>
    private static Gauge? Read(LimitRow? row, DateTimeOffset now) =>
        row?.Percent is int used
            ? new Gauge(Math.Clamp(used, 0, 100), row.ElapsedPercent(now))
            : null;

    private static void Fill(IntPtr hdc, int left, int top, int right, int bottom, uint colour)
    {
        if (right <= left || bottom <= top) return;

        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        var brush = CreateSolidBrush(colour);
        FillRect(hdc, ref rect, brush);
        DeleteObject(brush);
    }
}
