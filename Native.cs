using System.Runtime.InteropServices;

namespace ClaudeUsageWidget;

internal static class Native
{
    // ---- window messages -------------------------------------------------

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    public const uint WM_NCRBUTTONUP = 0x00A5;
    public const uint WM_EXITSIZEMOVE = 0x0232;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_APP_REFRESHED = 0x8001;
    public const uint WM_APP_TRAY = 0x8002;

    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSELEAVE = 0x02A3;

    // Custom iconic (taskbar) bitmap messages.
    public const uint WM_DWMSENDICONICTHUMBNAIL = 0x0323;
    public const uint WM_DWMSENDICONICLIVEPREVIEW = 0x0326;

    public const int HTCAPTION = 2;
    public const int HTCLIENT = 1;

    // ---- window styles ---------------------------------------------------

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_CLIPCHILDREN = 0x02000000;

    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_EX_TOPMOST = 0x00000008;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public const uint CS_DBLCLKS = 0x0008;

    // ---- SetWindowPos ----------------------------------------------------

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    // ---- structs ---------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public long rgbReserved1;
        public long rgbReserved2;
        public long rgbReserved3;
        public long rgbReserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    // ---- user32 ----------------------------------------------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint intervalMs, IntPtr callback);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hWnd, IntPtr id);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr cursorName);

    public static readonly IntPtr IDC_ARROW = new(32512);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint flags, IntPtr id, string? item);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(
        IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfoW(uint action, uint param, ref RECT data, uint winIni);

    public const uint SPI_GETWORKAREA = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_SETICON = 0x0080;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const uint MF_STRING = 0x0000;
    public const uint MF_POPUP = 0x0010;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_GRAYED = 0x0001;

    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

    public const uint DT_LEFT = 0x0000;
    public const uint DT_RIGHT = 0x0002;
    public const uint DT_TOP = 0x0000;
    public const uint DT_SINGLELINE = 0x0020;
    public const uint DT_NOPREFIX = 0x0800;
    public const uint DT_END_ELLIPSIS = 0x8000;
    public const uint DT_VCENTER = 0x0004;

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr hbr);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll")]
    public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    public const uint TME_LEAVE = 0x00000002;

    // ---- tray icon -------------------------------------------------------

    // CharSet matters even though the buffers are `fixed`: LayoutKind.Sequential defaults to
    // CharSet.Ansi, which declares every char in this struct to be one byte wide. The tip is
    // written as UTF-16, so a reader working to the Ansi contract stops at the first char's
    // high byte — a tooltip of "default: …" renders as "d".
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersionOrTimeout;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // ExactSpelling stops the runtime from ever probing for a mangled variant: the W entry
    // point is the one being named, and silently binding anything else is how a tooltip ends
    // up being read a byte at a time.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;

    /// <summary>
    /// Without this the icon runs with the original Shell_NotifyIcon semantics, where szTip is
    /// only 64 characters and the shell decides when a tooltip appears. Version 4 uses the
    /// full buffer and honours NIF_SHOWTIP.
    /// </summary>
    public const uint NOTIFYICON_VERSION_4 = 4;

    public const uint NIF_MESSAGE = 0x0001;
    public const uint NIF_ICON = 0x0002;
    public const uint NIF_TIP = 0x0004;
    public const uint NIF_SHOWTIP = 0x0080;

    // ---- gdi32 -----------------------------------------------------------

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits,
        IntPtr section, uint offset);

    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(
        IntPtr dest, int x, int y, int cx, int cy, IntPtr src, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    public static extern bool StretchBlt(
        IntPtr dest, int x, int y, int cx, int cy,
        IntPtr src, int sx, int sy, int scx, int scy, uint rop);

    public const uint SRCCOPY = 0x00CC0020;

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(IntPtr hdc, int mode);

    public const int HALFTONE = 4;

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    public static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    public static extern bool LineTo(IntPtr hdc, int x, int y);

    public const int PS_SOLID = 0;

    [DllImport("gdi32.dll")]
    public static extern bool RoundRect(
        IntPtr hdc, int left, int top, int right, int bottom, int ellipseW, int ellipseH);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
        string faceName);

    public const uint DEFAULT_CHARSET = 1;
    public const uint CLEARTYPE_QUALITY = 5;

    [DllImport("gdi32.dll")]
    public static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr hdc, int mode);

    public const int TRANSPARENT = 1;

    /// <summary>GDI wants 0x00BBGGRR, so channels are swapped relative to hex colour literals.</summary>
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ---- dwmapi ----------------------------------------------------------

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, uint attr, ref int value, int size);

    public const uint DWMWA_FORCE_ICONIC_REPRESENTATION = 7;
    public const uint DWMWA_HAS_ICONIC_BITMAP = 10;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetIconicThumbnail(IntPtr hWnd, IntPtr hbmp, uint flags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetIconicLivePreviewBitmap(
        IntPtr hWnd, IntPtr hbmp, IntPtr clientOffset, uint flags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmInvalidateIconicBitmaps(IntPtr hWnd);

    // ---- shell / com -----------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecuteW(
        IntPtr hWnd, string? verb, string file, string? parameters, string? directory, int showCmd);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, uint flags);

    public const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    public const uint CLSCTX_INPROC_SERVER = 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    // ---- winhttp ---------------------------------------------------------
    //
    // Used instead of HttpClient because the TLS stack lives in Windows here rather than in the
    // binary: the managed one costs nearly 2 MB on a 2.4 MB app whose whole pitch is its size.

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WinHttpOpen(
        string? agent, uint accessType, string? proxy, string? proxyBypass, uint flags);

    public const uint WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY = 4;

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WinHttpConnect(IntPtr session, string serverName, ushort port, uint reserved);

    public const ushort INTERNET_DEFAULT_HTTPS_PORT = 443;

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WinHttpOpenRequest(
        IntPtr connect, string verb, string objectName, string? version,
        string? referrer, IntPtr acceptTypes, uint flags);

    public const uint WINHTTP_FLAG_SECURE = 0x00800000;

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WinHttpSendRequest(
        IntPtr request, string? headers, uint headersLength,
        IntPtr optional, uint optionalLength, uint totalLength, IntPtr context);

    [DllImport("winhttp.dll", SetLastError = true)]
    public static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);

    [DllImport("winhttp.dll", SetLastError = true)]
    public static extern bool WinHttpQueryDataAvailable(IntPtr request, out uint available);

    [DllImport("winhttp.dll", SetLastError = true)]
    public static extern bool WinHttpReadData(IntPtr request, byte[] buffer, uint toRead, out uint read);

    [DllImport("winhttp.dll", SetLastError = true)]
    public static extern bool WinHttpSetTimeouts(
        IntPtr handle, int resolve, int connect, int send, int receive);

    [DllImport("winhttp.dll", SetLastError = true)]
    public static extern bool WinHttpCloseHandle(IntPtr handle);
}
