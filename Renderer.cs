using static ClaudeUsageWidget.Native;

namespace ClaudeUsageWidget;

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

    private static readonly uint Background = Rgb(0x17, 0x17, 0x1A);
    private static readonly uint TitleColor = Rgb(0xEC, 0xEC, 0xEE);
    private static readonly uint SubColor = Rgb(0x86, 0x86, 0x90);
    private static readonly uint LabelColor = Rgb(0xA8, 0xA8, 0xB0);
    private static readonly uint MutedColor = Rgb(0x6E, 0x6E, 0x78);
    private static readonly uint TrackColor = Rgb(0x2E, 0x2E, 0x36);
    private static readonly uint DividerColor = Rgb(0x26, 0x26, 0x2C);

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
                h += s.Limits.Count * S(24, scale);
                h += ResetGroups(s).Count * S(16, scale);
            }

            if (i < statuses.Count - 1) h += S(16, scale);       // divider gap
        }

        return h + S(14, scale);                                 // bottom padding
    }

    public static void Draw(
        IntPtr hdc, int width, int height,
        IReadOnlyList<AccountStatus> statuses, FontSet fonts, double scale,
        int hoverButton = ButtonNone, string? freshness = null, string? update = null)
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
        // but not worth a colour that means "a limit is nearly gone".
        if (update is not null)
        {
            SelectObject(hdc, fonts.Sub);
            DrawLine(hdc, update, S(16, scale), 0, barTextWidth, S(TitleBarHeight, scale),
                Rgb(0xC9, 0x9A, 0x3E), DT_RIGHT | DT_VCENTER);
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
            var subtitle = s.Email ?? s.Account.ConfigDir ?? "default profile";
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
                foreach (var limit in s.Limits)
                {
                    DrawLimitRow(hdc, limit, padX, y, contentWidth, fonts, scale);
                    y += S(24, scale);
                }

                SelectObject(hdc, fonts.Sub);

                var now = DateTime.Now;

                foreach (var (labels, reset) in ResetGroups(s))
                {
                    DrawLine(hdc, $"{labels} resets {ResetTime.Describe(reset, now)}",
                        padX, y, contentWidth, S(16, scale), MutedColor, DT_LEFT);
                    y += S(16, scale);
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
        IntPtr hdc, LimitRow limit, int x, int y, int contentWidth, FontSet fonts, double scale)
    {
        var labelWidth = S(48, scale);
        var valueWidth = S(44, scale);
        var rowHeight = S(24, scale);

        SelectObject(hdc, fonts.Label);
        DrawLine(hdc, ShortLabel(limit.Label), x, y, labelWidth, rowHeight, LabelColor,
            DT_LEFT | DT_VCENTER);

        var barLeft = x + labelWidth + S(6, scale);
        var barRight = x + contentWidth - valueWidth - S(6, scale);
        var barHeight = S(8, scale);
        var barTop = y + (rowHeight - barHeight) / 2;
        var color = ColorFor(limit.Percent);

        FillRoundRect(hdc, barLeft, barTop, barRight, barTop + barHeight, TrackColor);

        var fillWidth = (int)Math.Round((barRight - barLeft) * Math.Clamp(limit.Percent, 0, 100) / 100.0);
        if (fillWidth > 0)
        {
            // Keep the fill at least as wide as its own rounding, or the cap renders as a sliver.
            fillWidth = Math.Max(fillWidth, barHeight);
            FillRoundRect(hdc, barLeft, barTop, barLeft + fillWidth, barTop + barHeight, color);
        }

        SelectObject(hdc, fonts.Value);
        DrawLine(hdc, limit.Percent + "%", x + contentWidth - valueWidth, y, valueWidth, rowHeight,
            color, DT_RIGHT | DT_VCENTER);
    }

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

    /// <summary>
    /// Reset times, one line per distinct moment, naming the rows that share it. The session
    /// resets in hours and the weekly windows in days, so a single line could only ever be
    /// right about one of them — and the weekly rows almost always share a reset, which is
    /// what keeps this to two lines rather than one per row.
    /// </summary>
    private static List<(string Labels, string Reset)> ResetGroups(AccountStatus s)
    {
        var groups = new List<(string Labels, string Reset)>();

        foreach (var limit in s.Limits)
        {
            if (limit.Resets is null) continue;

            // Grouped on the exact stamp rather than its description: two resets twenty minutes
            // apart can both describe as "in 3h" without being the same moment.
            var reset = ResetTime.StripZone(limit.Resets);
            var label = ShortLabel(limit.Label);
            var existing = groups.FindIndex(g => g.Reset == reset);

            if (existing >= 0)
                groups[existing] = (groups[existing].Labels + " · " + label, reset);
            else
                groups.Add((label, reset));
        }

        return groups;
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
