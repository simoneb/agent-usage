using AgentUsage;
using AgentUsage.Providers;
using static AgentUsage.Widget.Native;

namespace AgentUsage.Widget;

/// <summary>An off-screen 32bpp DIB plus its memory DC. Used for the panel and the taskbar bitmaps.</summary>
internal sealed unsafe class DibSurface : IDisposable
{
    public IntPtr Hdc { get; }
    public IntPtr Bitmap { get; }
    public int Width { get; }
    public int Height { get; }

    private readonly IntPtr _bits;
    private readonly IntPtr _oldBitmap;

    public DibSurface(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = Width,
            biHeight = -Height,          // negative: top-down, so row 0 is the top
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };

        Hdc = CreateCompatibleDC(IntPtr.Zero);
        Bitmap = CreateDIBSection(Hdc, ref header, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        _oldBitmap = SelectObject(Hdc, Bitmap);
    }

    /// <summary>
    /// GDI text and shape calls leave the alpha channel at zero, which DWM reads as fully
    /// transparent. Force every pixel opaque before handing the bitmap to DWM.
    /// </summary>
    public unsafe void ForceOpaque()
    {
        var p = (byte*)_bits;
        if (p is null) return;

        var count = Width * Height;
        for (var i = 0; i < count; i++)
            p[i * 4 + 3] = 255;
    }

    public void Dispose()
    {
        SelectObject(Hdc, _oldBitmap);
        DeleteObject(Bitmap);
        DeleteDC(Hdc);
    }
}

internal sealed class FontSet : IDisposable
{
    public IntPtr Title { get; }
    public IntPtr Sub { get; }
    public IntPtr Label { get; }
    public IntPtr Value { get; }

    public FontSet(double scale)
    {
        Title = Make(15, 600, scale);
        Sub = Make(12, 400, scale);
        Label = Make(12, 400, scale);
        Value = Make(13, 700, scale);
    }

    private static IntPtr Make(int px, int weight, double scale) => CreateFontW(
        -(int)Math.Round(px * scale), 0, 0, 0, weight, 0, 0, 0,
        DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");

    public void Dispose()
    {
        DeleteObject(Title);
        DeleteObject(Sub);
        DeleteObject(Label);
        DeleteObject(Value);
    }
}

internal static class Renderer
{
    public const int BaseWidth = 330;

    /// <summary>
    /// A limit is two lines: what it is and how it stands on top, the bar under it — the shape
    /// Claude Desktop uses, and the one that leaves the bar wide enough to read a pace mark off.
    /// </summary>
    private const int LimitRowHeight = 32;

    /// <summary>Strip along the top holding the minimise and close buttons.</summary>
    public const int TitleBarHeight = 26;
    private const int ButtonWidth = 32;

    public const int ButtonNone = 0;
    public const int ButtonMinimize = 1;
    public const int ButtonClose = 2;

    /// <summary>Client-pixel bounds of a title-bar button. Close sits at the far right.</summary>
    public static RECT ButtonRect(int width, double scale, int button)
    {
        var w = S(ButtonWidth, scale);
        var h = S(TitleBarHeight, scale);
        var right = button == ButtonClose ? width : width - w;

        return new RECT { Left = right - w, Top = 0, Right = right, Bottom = h };
    }

    /// <summary>Which button the point falls in, or <see cref="ButtonNone"/>.</summary>
    public static int HitTestButton(int x, int y, int width, double scale)
    {
        foreach (var button in new[] { ButtonClose, ButtonMinimize })
        {
            var r = ButtonRect(width, scale, button);
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return button;
        }

        return ButtonNone;
    }

    /// <summary>
    /// Where the update notice sits, given the text it will hold. Both the drawing and the hit
    /// test go through here: a link whose clickable area is worked out separately from where it
    /// was painted is a link that stops working the first time either side is edited.
    ///
    /// The caller has already selected the font the notice is drawn in.
    /// </summary>
    public static RECT NoticeRect(IntPtr hdc, string text, int width, double scale)
    {
        GetTextExtentPoint32W(hdc, text, text.Length, out var size);

        // Right-aligned against the title-bar buttons, with a couple of pixels of slack so the
        // hit target is not exactly the glyphs.
        var right = width - S(ButtonWidth * 2, scale);
        var pad = S(4, scale);

        return new RECT
        {
            Left = Math.Max(S(8, scale), right - size.Width - pad),
            Top = 0,
            Right = right,
            Bottom = S(TitleBarHeight, scale),
        };
    }

    private static readonly uint Background = Rgb(0x17, 0x17, 0x1A);
    private static readonly uint TitleColor = Rgb(0xEC, 0xEC, 0xEE);
    private static readonly uint SubColor = Rgb(0x86, 0x86, 0x90);
    private static readonly uint LabelColor = Rgb(0xA8, 0xA8, 0xB0);
    private static readonly uint MutedColor = Rgb(0x6E, 0x6E, 0x78);
    private static readonly uint TrackColor = Rgb(0x2E, 0x2E, 0x36);
    private static readonly uint DividerColor = Rgb(0x26, 0x26, 0x2C);
    private static readonly uint PaceColor = Rgb(0xD2, 0xD2, 0xDC);

    public static uint ColorFor(int percent) => percent switch
    {
        >= 90 => Rgb(0xFF, 0x6B, 0x6B),
        >= 75 => Rgb(0xFF, 0xB0, 0x2E),
        _ => Rgb(0x62, 0xCB, 0x82),
    };

    private static int S(int v, double scale) => (int)Math.Round(v * scale);

    /// <summary>Height the panel needs for the given data, in physical pixels.</summary>
    public static int MeasureHeight(IReadOnlyList<AccountStatus> statuses, double scale)
    {
        var h = S(TitleBarHeight, scale) + S(2, scale);

        if (statuses.Count == 0)
        {
            return h + S(22, scale) + S(14, scale);
        }

        for (var i = 0; i < statuses.Count; i++)
        {
            var s = statuses[i];

            h += S(20, scale);                                   // title
            h += S(16, scale);                                   // subtitle (email or profile hint)
            h += S(6, scale);

            if (s.Error is not null)
            {
                h += S(18, scale);
            }
            else
            {
                h += s.Limits.Count * S(LimitRowHeight, scale);
            }

            if (i < statuses.Count - 1) h += S(16, scale);       // divider gap
        }

        return h + S(14, scale);                                 // bottom padding
    }

    public static void Draw(
        IntPtr hdc, int width, int height,
        IReadOnlyList<AccountStatus> statuses, FontSet fonts, double scale,
        int hoverButton = ButtonNone, string? freshness = null, string? update = null,
        bool updateHovered = false)
    {
        var full = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        FillSolid(hdc, full, Background);

        SetBkMode(hdc, TRANSPARENT);
        DrawTitleBarButtons(hdc, width, scale, hoverButton);

        // The title bar is otherwise empty on the left, so freshness costs no height.
        var barTextWidth = width - S(16, scale) - S(ButtonWidth * 2, scale);

        if (freshness is not null)
        {
            SelectObject(hdc, fonts.Sub);
            DrawLine(hdc, freshness, S(16, scale), 0, barTextWidth, S(TitleBarHeight, scale),
                MutedColor, DT_LEFT | DT_VCENTER);
        }

        // Right-aligned in the same strip: a new release is worth seeing without opening a menu,
        // but not worth a colour that means "a limit is nearly gone". It is also the button that
        // installs it, so hovering brightens it and underlines it the way a link does.
        if (update is not null)
        {
            SelectObject(hdc, fonts.Sub);

            var colour = updateHovered ? Rgb(0xF0, 0xC1, 0x5E) : Rgb(0xC9, 0x9A, 0x3E);
            var rect = NoticeRect(hdc, update, width, scale);

            DrawLine(hdc, update, rect.Left, rect.Top, rect.Width, rect.Height,
                colour, DT_RIGHT | DT_VCENTER);

            if (updateHovered)
            {
                GetTextExtentPoint32W(hdc, update, update.Length, out var size);

                var baseline = (rect.Height + size.Height) / 2;
                var rule = new RECT
                {
                    Left = rect.Right - size.Width,
                    Top = baseline,
                    Right = rect.Right,
                    Bottom = baseline + Math.Max(1, S(1, scale)),
                };

                FillSolid(hdc, rule, colour);
            }
        }

        var padX = S(16, scale);
        var y = S(TitleBarHeight, scale) + S(2, scale);

        if (statuses.Count == 0)
        {
            SelectObject(hdc, fonts.Sub);
            DrawLine(hdc, "Loading…", padX, y, width - padX * 2, S(20, scale), SubColor, DT_LEFT);
            return;
        }

        for (var i = 0; i < statuses.Count; i++)
        {
            var s = statuses[i];
            var contentWidth = width - padX * 2;

            SelectObject(hdc, fonts.Title);
            DrawLine(hdc, s.Account.Label, padX, y, contentWidth, S(20, scale), TitleColor, DT_LEFT);

            if (s.Error is null && s.HeadlinePercent is int head)
            {
                SelectObject(hdc, fonts.Title);
                DrawLine(hdc, head + "%", padX, y, contentWidth, S(20, scale), ColorFor(head), DT_RIGHT);
            }

            y += S(20, scale);

            SelectObject(hdc, fonts.Sub);

            // With more than one tool on the panel, which tool a row belongs to matters more
            // than which directory it came from — Codex has no email to show in the first place.
            var subtitle = s.Email ?? Subtitle(s);
            DrawLine(hdc, subtitle, padX, y, contentWidth, S(16, scale), SubColor, DT_LEFT);
            y += S(16, scale) + S(6, scale);

            if (s.Error is not null)
            {
                SelectObject(hdc, fonts.Label);
                DrawLine(hdc, s.Error, padX, y, contentWidth, S(18, scale),
                    Rgb(0xFF, 0xB0, 0x2E), DT_LEFT);
                y += S(18, scale);
            }
            else
            {
                var now = DateTimeOffset.Now;

                foreach (var limit in s.Limits)
                {
                    DrawLimitRow(hdc, limit, padX, y, contentWidth, fonts, scale, now);
                    y += S(LimitRowHeight, scale);
                }
            }

            if (i < statuses.Count - 1)
            {
                var lineY = y + S(8, scale);
                var divider = new RECT
                {
                    Left = padX,
                    Top = lineY,
                    Right = width - padX,
                    Bottom = lineY + Math.Max(1, S(1, scale)),
                };
                FillSolid(hdc, divider, DividerColor);
                y += S(16, scale);
            }
        }
    }

    private static void DrawTitleBarButtons(IntPtr hdc, int width, double scale, int hoverButton)
    {
        DrawButton(hdc, ButtonRect(width, scale, ButtonMinimize), ButtonMinimize,
            hoverButton == ButtonMinimize, scale);

        DrawButton(hdc, ButtonRect(width, scale, ButtonClose), ButtonClose,
            hoverButton == ButtonClose, scale);
    }

    private static void DrawButton(IntPtr hdc, RECT r, int button, bool hovered, double scale)
    {
        if (hovered)
        {
            // Windows convention: close goes red on hover, everything else a neutral tint.
            FillSolid(hdc, r, button == ButtonClose
                ? Rgb(0xC4, 0x2B, 0x1C)
                : Rgb(0x2E, 0x2E, 0x36));
        }

        var ink = hovered ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x9C, 0x9C, 0xA6);
        var thickness = Math.Max(1, S(1, scale));
        var pen = CreatePen(PS_SOLID, thickness, ink);
        var oldPen = SelectObject(hdc, pen);

        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Top + r.Bottom) / 2;
        var arm = S(5, scale);

        if (button == ButtonClose)
        {
            MoveToEx(hdc, cx - arm, cy - arm, IntPtr.Zero);
            LineTo(hdc, cx + arm + 1, cy + arm + 1);
            MoveToEx(hdc, cx + arm, cy - arm, IntPtr.Zero);
            LineTo(hdc, cx - arm - 1, cy + arm + 1);
        }
        else
        {
            MoveToEx(hdc, cx - arm, cy, IntPtr.Zero);
            LineTo(hdc, cx + arm + 1, cy);
        }

        SelectObject(hdc, oldPen);
        DeleteObject(pen);
    }

    private static void DrawLimitRow(
        IntPtr hdc, LimitRow limit, int x, int y, int contentWidth, FontSet fonts, double scale,
        DateTimeOffset now)
    {
        var textHeight = S(16, scale);

        // Wider than a percentage needs: a count reads "412 / 1500" and a window that has rolled
        // over reads "stale", and both have to fit without being clipped to something misleading.
        var valueWidth = S(58, scale);
        var labelWidth = S(58, scale);

        var percent = limit.Percent;
        var color = percent is int p ? ColorFor(p) : MutedColor;

        SelectObject(hdc, fonts.Label);
        DrawLine(hdc, ShortLabel(limit.Label), x, y, labelWidth, textHeight, LabelColor,
            DT_LEFT | DT_VCENTER);

        SelectObject(hdc, fonts.Value);
        DrawLine(hdc, limit.Display, x + contentWidth - valueWidth, y, valueWidth, textHeight,
            color, DT_RIGHT | DT_VCENTER);

        // Measured rather than assumed to be the column: the value column is wide enough for
        // "412 / 1500", so a row reading "11%" would otherwise leave the reset stamp floating
        // half an inch off the number it sits beside, and no two rows would agree on the gap.
        GetTextExtentPoint32W(hdc, limit.Display, limit.Display.Length, out var valueSize);

        var noteLeft = x + labelWidth + S(4, scale);
        var noteRight = x + contentWidth - valueSize.Width - S(6, scale);

        SelectObject(hdc, fonts.Sub);

        if (ResetText(limit, now) is string resets)
        {
            DrawLine(hdc, resets, noteLeft, y, noteRight - noteLeft, textHeight,
                MutedColor, DT_RIGHT | DT_VCENTER);
        }

        var barTop = y + textHeight + S(4, scale);
        var barHeight = S(8, scale);
        var barRight = x + contentWidth;

        FillRoundRect(hdc, x, barTop, barRight, barTop + barHeight, TrackColor);

        // No percentage means no bar. A limit counted in requests with no stated ceiling, or a
        // window that has already reset, has nothing to fill a track with — and a bar drawn at
        // zero would read as "none used", which is a different claim entirely.
        if (percent is int value)
        {
            var fillWidth = (int)Math.Round(contentWidth * Math.Clamp(value, 0, 100) / 100.0);
            if (fillWidth > 0)
            {
                // Keep the fill at least as wide as its own rounding, or the cap renders as a sliver.
                fillWidth = Math.Max(fillWidth, barHeight);
                FillRoundRect(hdc, x, barTop, x + fillWidth, barTop + barHeight, color);
            }

            // Where an even burn would have reached by now. Fill short of this mark is spare;
            // fill past it is a window that runs out before it resets. Drawn inside the bar and
            // over the fill, because the comparison is the point and the row is not the place
            // for a second number saying the same thing.
            if (limit.ElapsedPercent(now) is int elapsed)
            {
                var thickness = Math.Max(1, S(2, scale));
                var markLeft = x + (int)Math.Round((contentWidth - thickness) * elapsed / 100.0);
                var mark = new RECT
                {
                    Left = markLeft,
                    Top = barTop,
                    Right = markLeft + thickness,
                    Bottom = barTop + barHeight,
                };

                FillSolid(hdc, mark, PaceColor);
            }
        }
    }

    /// <summary>
    /// When this window rolls over: a countdown while that is the more useful answer, the date
    /// once it is not. The word "resets" is dropped now that the stamp sits on the row it belongs
    /// to — every pixel it costs is one the panel is wider by, on every row.
    /// </summary>
    private static string? ResetText(LimitRow limit, DateTimeOffset now)
    {
        // A window that has already rolled over has nothing to count down to.
        if (limit.Expired) return null;

        if (limit.ResetsAt is DateTimeOffset at)
            return ResetTime.Describe(at, now);

        return limit.Resets is string stamp
            ? ResetTime.Describe(stamp, now.LocalDateTime)
            : null;
    }

    /// <summary>
    /// The line under an account's name when there is no email to put there. Names the tool,
    /// and — for a provider whose figures only refresh while you are using it — how old the
    /// reading is. Without that, a number recorded last Tuesday looks exactly like a live one.
    /// </summary>
    private static string Subtitle(AccountStatus s)
    {
        var provider = ProviderRegistry.Find(s.Provider)?.DisplayName ?? s.Provider;

        if (s.MeasuredAt is DateTime at && DateTime.Now - at > TimeSpan.FromMinutes(5))
            return $"{provider} · measured {Age(DateTime.Now - at)} ago";

        return s.Account.ConfigDir is { Length: > 0 } dir ? $"{provider} · {dir}" : provider;
    }

    private static string Age(TimeSpan age) =>
        age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
        : $"{(int)age.TotalDays}d";

    /// <summary>"Current week (all models)" is too long for a 48px gutter; shorten to a stable key.</summary>
    private static string ShortLabel(string label)
    {
        if (label.Contains("session", StringComparison.OrdinalIgnoreCase)) return "session";
        if (label.Contains("all models", StringComparison.OrdinalIgnoreCase)) return "week";

        var open = label.IndexOf('(');
        var close = label.IndexOf(')');
        if (open >= 0 && close > open) return label[(open + 1)..close].ToLowerInvariant();

        return label.Replace("Current ", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    }

    private static void DrawLine(
        IntPtr hdc, string text, int x, int y, int width, int height, uint color, uint format)
    {
        SetTextColor(hdc, color);
        var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        DrawTextW(hdc, text, text.Length, ref rect,
            format | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
    }

    private static void FillSolid(IntPtr hdc, RECT rect, uint color)
    {
        var brush = CreateSolidBrush(color);
        FillRect(hdc, ref rect, brush);
        DeleteObject(brush);
    }

    private static void FillRoundRect(IntPtr hdc, int left, int top, int right, int bottom, uint color)
    {
        if (right <= left || bottom <= top) return;

        var radius = bottom - top;
        var brush = CreateSolidBrush(color);
        var pen = CreatePen(PS_SOLID, 1, color);
        var oldBrush = SelectObject(hdc, brush);
        var oldPen = SelectObject(hdc, pen);

        RoundRect(hdc, left, top, right + 1, bottom + 1, radius, radius);

        SelectObject(hdc, oldBrush);
        SelectObject(hdc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);
    }
}
