using static ClaudeUsageWidget.Native;

namespace ClaudeUsageWidget;

/// <summary>
/// Builds the app icon: a solid badge in the severity colour with the percentage on it.
/// Legible at 16px in a way a thin ring is not, and opaque throughout, which sidesteps
/// the alpha channel GDI text drawing leaves at zero.
/// </summary>
internal static class IconBuilder
{
    public static IntPtr Build(int? percent, bool hasError, int size)
    {
        var color = percent is null || hasError
            ? Rgb(0x6E, 0x6E, 0x78)
            : Renderer.ColorFor(percent.Value);

        var text = percent is null ? (hasError ? "!" : "–") : percent.Value.ToString();

        using var surface = new DibSurface(size, size);

        var full = new RECT { Left = 0, Top = 0, Right = size, Bottom = size };
        var brush = CreateSolidBrush(color);
        FillRect(surface.Hdc, ref full, brush);
        DeleteObject(brush);

        // Digits sit on a bright badge, so ink is near-black regardless of severity colour.
        SetBkMode(surface.Hdc, TRANSPARENT);
        SetTextColor(surface.Hdc, Rgb(0x12, 0x12, 0x16));

        var fontHeight = text.Length >= 3 ? size * 0.52 : size * 0.66;
        var font = CreateFontW(-(int)Math.Round(fontHeight), 0, 0, 0, 700, 0, 0, 0,
            DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        var oldFont = SelectObject(surface.Hdc, font);

        var textRect = full;
        DrawTextW(surface.Hdc, text, text.Length, ref textRect,
            DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER | 0x0001 /* DT_CENTER */);

        SelectObject(surface.Hdc, oldFont);
        DeleteObject(font);

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
}
