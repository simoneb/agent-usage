using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ClaudeUsageWidget.Native;

namespace ClaudeUsageWidget;

/// <summary>
/// The whole UI: a borderless always-on-top panel that also owns a taskbar button.
/// The taskbar button carries a progress bar; hovering it (or minimising the panel)
/// shows the same panel rendered as a custom DWM iconic bitmap.
/// </summary>
internal sealed class Widget : IDisposable
{
    private const string ClassName = "ClaudeUsageWidgetWindow";
    private static Widget? _instance;

    private const int IdRefresh = 1;
    private const int IdAlwaysOnTop = 2;
    private const int IdMinimize = 3;
    private const int IdEditConfig = 4;
    private const int IdReload = 5;
    private const int IdExit = 6;
    private const int IdShowPanel = 7;

    private const uint TrayIconId = 1;

    private static readonly IntPtr PollTimerId = new(1);

    private IntPtr _hwnd;
    private AppConfig _config;
    private string _claudePath;
    private FontSet _fonts;
    private double _scale = 1.0;
    private TaskbarProgress? _taskbar;

    private IntPtr _iconSmall;
    private IntPtr _iconBig;

    private int _hoverButton = Renderer.ButtonNone;
    private bool _mouseTracking;
    private bool _trayAdded;

    private volatile AccountStatus[] _statuses = Array.Empty<AccountStatus>();
    private readonly ConcurrentDictionary<string, AuthStatus> _authCache = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _refreshing;

    public Widget()
    {
        _config = ConfigStore.Load();
        _claudePath = UsageProbe.ResolveClaudePath(_config.ClaudePath);
        _fonts = new FontSet(1.0);
        _instance = this;
    }

    public unsafe void Run()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);

        var hInstance = GetModuleHandleW(null);
        var classNamePtr = Marshal.StringToHGlobalUni(ClassName);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = CS_DBLCLKS,
            lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProcStatic,
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = classNamePtr,
        };

        if (RegisterClassExW(ref wc) == 0)
            return;

        var width = 330;
        var height = 160;

        _hwnd = CreateWindowExW(
            WS_EX_APPWINDOW,
            ClassName, "Claude Usage",
            WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN,
            0, 0, width, height,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            return;

        _scale = GetDpiForWindow(_hwnd) / 96.0;
        _fonts.Dispose();
        _fonts = new FontSet(_scale);

        // Rounded corners, and a custom bitmap for taskbar hover / minimised preview.
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        var enable = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_HAS_ICONIC_BITMAP, ref enable, sizeof(int));
        DwmSetWindowAttribute(_hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, ref enable, sizeof(int));

        _taskbar = TaskbarProgress.Create();

        PlaceWindow();
        ApplyTopmost();

        UpdateIcon();   // builds the placeholder badge the tray icon starts from
        AddTray();

        ShowWindow(_hwnd, SW_SHOW);
        UpdateWindow(_hwnd);

        SetTimer(_hwnd, PollTimerId, (uint)(_config.PollSeconds * 1000), IntPtr.Zero);
        StartRefresh();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        Marshal.FreeHGlobal(classNamePtr);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProcStatic(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return _instance?.WndProc(hwnd, msg, wParam, lParam)
                   ?? DefWindowProcW(hwnd, msg, wParam, lParam);
        }
        catch
        {
            // An exception crossing back into Win32 would tear the process down.
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                OnPaint(hwnd);
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return new IntPtr(1); // fully repainted in WM_PAINT; skip the flicker

            case WM_NCHITTEST:
            {
                // Everything drags the window except the two title-bar buttons, which need
                // real client-area messages to hover and click.
                var pt = new POINT
                {
                    X = (short)(lParam.ToInt64() & 0xFFFF),
                    Y = (short)((lParam.ToInt64() >> 16) & 0xFFFF),
                };
                ScreenToClient(hwnd, ref pt);
                GetClientRect(hwnd, out var client);

                return new IntPtr(
                    Renderer.HitTestButton(pt.X, pt.Y, client.Width, _scale) != Renderer.ButtonNone
                        ? HTCLIENT
                        : HTCAPTION);
            }

            case WM_MOUSEMOVE:
            {
                GetClientRect(hwnd, out var client);
                var hit = Renderer.HitTestButton(
                    (short)(lParam.ToInt64() & 0xFFFF),
                    (short)((lParam.ToInt64() >> 16) & 0xFFFF),
                    client.Width, _scale);

                SetHover(hwnd, hit);
                EnsureMouseTracking(hwnd);
                return IntPtr.Zero;
            }

            case WM_MOUSELEAVE:
                _mouseTracking = false;
                SetHover(hwnd, Renderer.ButtonNone);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
            {
                GetClientRect(hwnd, out var client);
                var hit = Renderer.HitTestButton(
                    (short)(lParam.ToInt64() & 0xFFFF),
                    (short)((lParam.ToInt64() >> 16) & 0xFFFF),
                    client.Width, _scale);

                if (hit == Renderer.ButtonMinimize) ShowWindow(hwnd, SW_MINIMIZE);
                else if (hit == Renderer.ButtonClose) HidePanel();

                return IntPtr.Zero;
            }

            case WM_NCLBUTTONDBLCLK:
            case WM_LBUTTONDBLCLK:
                StartRefresh();
                return IntPtr.Zero;

            case WM_NCRBUTTONUP:
                ShowMenu();
                return IntPtr.Zero;

            case WM_APP_TRAY:
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case WM_LBUTTONUP:
                        TogglePanel();
                        break;

                    case WM_RBUTTONUP:
                        ShowMenu();
                        break;
                }
                return IntPtr.Zero;

            case WM_COMMAND:
                OnCommand((int)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case WM_TIMER:
                if (wParam == PollTimerId) StartRefresh();
                return IntPtr.Zero;

            case WM_APP_REFRESHED:
                OnRefreshed();
                return IntPtr.Zero;

            case WM_EXITSIZEMOVE:
                SaveWindowPosition();
                return IntPtr.Zero;

            case WM_DPICHANGED:
            {
                _scale = (wParam.ToInt64() & 0xFFFF) / 96.0;
                _fonts.Dispose();
                _fonts = new FontSet(_scale);

                // lParam carries the position Windows suggests on the new monitor. Honour it,
                // then size to content at the new scale.
                if (lParam != IntPtr.Zero)
                {
                    var suggested = Marshal.PtrToStructure<RECT>(lParam);
                    SetWindowPos(hwnd, IntPtr.Zero, suggested.Left, suggested.Top,
                        suggested.Width, suggested.Height, SWP_NOACTIVATE);
                }

                ResizeToContent();
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            case WM_DWMSENDICONICTHUMBNAIL:
                SendIconicThumbnail((int)((lParam.ToInt64() >> 16) & 0xFFFF),
                                    (int)(lParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case WM_DWMSENDICONICLIVEPREVIEW:
                SendLivePreview();
                return IntPtr.Zero;

            case WM_CLOSE:
                HidePanel();   // close hides to the tray; exit lives in the tray menu
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---- painting --------------------------------------------------------

    private void OnPaint(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        GetClientRect(hwnd, out var client);

        using (var surface = new DibSurface(client.Width, client.Height))
        {
            Renderer.Draw(surface.Hdc, surface.Width, surface.Height, _statuses, _fonts, _scale,
                _hoverButton);
            BitBlt(hdc, 0, 0, client.Width, client.Height, surface.Hdc, 0, 0, SRCCOPY);
        }

        EndPaint(hwnd, ref ps);
    }

    /// <summary>Renders the panel into a fresh surface at its natural size.</summary>
    private DibSurface RenderNatural()
    {
        GetClientRect(_hwnd, out var client);
        var width = client.Width > 0 ? client.Width : (int)Math.Round(Renderer.BaseWidth * _scale);
        var height = client.Height > 0 ? client.Height : Renderer.MeasureHeight(_statuses, _scale);

        var surface = new DibSurface(width, height);
        Renderer.Draw(surface.Hdc, surface.Width, surface.Height, _statuses, _fonts, _scale);
        return surface;
    }

    private void SendIconicThumbnail(int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return;

        using var natural = RenderNatural();

        var ratio = Math.Min((double)maxWidth / natural.Width, (double)maxHeight / natural.Height);
        ratio = Math.Min(ratio, 1.0);

        var w = Math.Max(1, (int)(natural.Width * ratio));
        var h = Math.Max(1, (int)(natural.Height * ratio));

        using var scaled = new DibSurface(w, h);
        SetStretchBltMode(scaled.Hdc, HALFTONE);
        StretchBlt(scaled.Hdc, 0, 0, w, h, natural.Hdc, 0, 0, natural.Width, natural.Height, SRCCOPY);
        scaled.ForceOpaque();

        DwmSetIconicThumbnail(_hwnd, scaled.Bitmap, 0);
    }

    private void SendLivePreview()
    {
        using var natural = RenderNatural();
        natural.ForceOpaque();

        DwmSetIconicLivePreviewBitmap(_hwnd, natural.Bitmap, IntPtr.Zero, 0);
    }

    // ---- data ------------------------------------------------------------

    private void StartRefresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        var accounts = _config.Accounts.ToArray();
        var claudePath = _claudePath;
        var hwnd = _hwnd;

        _ = Task.Run(async () =>
        {
            try
            {
                // All accounts probed concurrently: separate processes, separate config dirs.
                var results = await Task.WhenAll(Array.ConvertAll(
                    accounts, a => UsageProbe.ProbeAsync(a, claudePath, CachedAuth(a), _shutdown.Token)));

                foreach (var result in results) RememberAuth(result);

                _statuses = results;
            }
            catch (Exception ex)
            {
                _statuses = Array.ConvertAll(accounts, a => new AccountStatus
                {
                    Account = a,
                    Error = ex.Message,
                    UpdatedAt = DateTime.Now,
                });
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
                if (!_shutdown.IsCancellationRequested)
                    PostMessageW(hwnd, WM_APP_REFRESHED, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    private static string AuthKey(AccountConfig account) => account.ConfigDir ?? string.Empty;

    private AuthStatus? CachedAuth(AccountConfig account) =>
        _authCache.TryGetValue(AuthKey(account), out var auth) ? auth : null;

    /// <summary>
    /// Holds on to a confirmed sign-in so later polls skip the `auth status` launch, and
    /// forgets it the moment a probe fails — a signed-out profile has to be noticed.
    /// </summary>
    private void RememberAuth(AccountStatus status)
    {
        var key = AuthKey(status.Account);

        if (status.Error is not null || !status.LoggedIn)
        {
            _authCache.TryRemove(key, out _);
            return;
        }

        _authCache[key] = new AuthStatus
        {
            LoggedIn = true,
            Email = status.Email,
            OrgName = status.OrgName,
            SubscriptionType = status.SubscriptionType,
        };
    }

    private void OnRefreshed()
    {
        ResizeToContent();
        InvalidateRect(_hwnd, IntPtr.Zero, false);
        UpdateTaskbar();
        UpdateIcon();

        // Tell DWM the cached hover bitmap is out of date.
        DwmInvalidateIconicBitmaps(_hwnd);
    }

    private void UpdateTaskbar()
    {
        if (_taskbar is null) return;

        var statuses = _statuses;
        int? headline = null;
        var anyError = false;

        foreach (var s in statuses)
        {
            if (s.Error is not null) { anyError = true; continue; }
            if (s.HeadlinePercent is int p) headline = headline is null ? p : Math.Max(headline.Value, p);
        }

        if (headline is null)
        {
            _taskbar.SetState(_hwnd, anyError
                ? TaskbarProgress.State.Error
                : TaskbarProgress.State.Indeterminate);
            return;
        }

        var pct = Math.Clamp(headline.Value, 0, 100);

        // Progress state doubles as colour: red at/above 90, amber at/above 75, green below.
        _taskbar.SetState(_hwnd, pct switch
        {
            >= 90 => TaskbarProgress.State.Error,
            >= 75 => TaskbarProgress.State.Paused,
            _ => TaskbarProgress.State.Normal,
        });

        _taskbar.SetValue(_hwnd, (ulong)pct, 100);
    }

    // ---- window plumbing -------------------------------------------------

    private void ResizeToContent()
    {
        var width = (int)Math.Round(Renderer.BaseWidth * _scale);
        var height = Renderer.MeasureHeight(_statuses, _scale);

        GetWindowRect(_hwnd, out var rect);

        // The panel grows once real data arrives; without clamping it grows off the screen edge.
        var (x, y) = ClampToWorkArea(rect.Left, rect.Top, width, height);

        SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE);
    }

    private void PlaceWindow()
    {
        var width = (int)Math.Round(Renderer.BaseWidth * _scale);
        var height = Renderer.MeasureHeight(_statuses, _scale);

        // -1/-1 was the old "unset" sentinel; treat it as unset so upgrades don't land at a corner.
        var saved = _config.WindowX is int sx && _config.WindowY is int sy && (sx, sy) != (-1, -1)
            ? ((int X, int Y)?)(sx, sy)
            : null;

        int x = saved?.X ?? 0, y = saved?.Y ?? 0;

        if (saved is null)
        {
            // Default: lower-right of the work area, so it never sits under the taskbar.
            var work = CurrentWorkArea();
            x = work.Right - width - (int)(24 * _scale);
            y = work.Bottom - height - (int)(24 * _scale);
        }

        (x, y) = ClampToWorkArea(x, y, width, height);
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE);
    }

    /// <summary>
    /// Work area of the monitor holding <paramref name="bounds"/>. SPI_GETWORKAREA only ever
    /// describes the primary monitor, so clamping against it drags the panel back off any
    /// secondary screen.
    /// </summary>
    private static RECT WorkAreaFor(RECT bounds)
    {
        var monitor = MonitorFromRect(ref bounds, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
            return info.rcWork;

        var work = new RECT();
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, ref work, 0)) return work;

        return new RECT
        {
            Left = 0,
            Top = 0,
            Right = GetSystemMetrics(SM_CXSCREEN),
            Bottom = GetSystemMetrics(SM_CYSCREEN),
        };
    }

    private RECT CurrentWorkArea()
    {
        var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
            return info.rcWork;

        GetWindowRect(_hwnd, out var rect);
        return WorkAreaFor(rect);
    }

    private static (int X, int Y) ClampToWorkArea(int x, int y, int width, int height)
    {
        // Clamp against the monitor the panel is actually on, not the primary one.
        var bounds = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        var work = WorkAreaFor(bounds);

        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));

        return (x, y);
    }

    private void UpdateIcon()
    {
        var statuses = _statuses;
        int? headline = null;
        var anyError = false;

        foreach (var s in statuses)
        {
            if (s.Error is not null) { anyError = true; continue; }
            if (s.HeadlinePercent is int p) headline = headline is null ? p : Math.Max(headline.Value, p);
        }

        var small = IconBuilder.Build(headline, anyError && headline is null, 16);
        var big = IconBuilder.Build(headline, anyError && headline is null, 32);

        SendMessageW(_hwnd, WM_SETICON, new IntPtr(ICON_SMALL), small);
        SendMessageW(_hwnd, WM_SETICON, new IntPtr(ICON_BIG), big);

        if (_iconSmall != IntPtr.Zero) DestroyIcon(_iconSmall);
        if (_iconBig != IntPtr.Zero) DestroyIcon(_iconBig);

        _iconSmall = small;
        _iconBig = big;

        UpdateTray();
    }

    private void ApplyTopmost() => SetWindowPos(
        _hwnd, _config.AlwaysOnTop ? HWND_TOPMOST : HWND_NOTOPMOST,
        0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    private void SaveWindowPosition()
    {
        GetWindowRect(_hwnd, out var rect);
        _config.WindowX = rect.Left;
        _config.WindowY = rect.Top;

        try
        {
            ConfigStore.Save(_config);
        }
        catch
        {
            // Position is a nicety; a failed write must not kill the widget.
        }
    }

    // ---- panel visibility and tray ---------------------------------------

    private void SetHover(IntPtr hwnd, int button)
    {
        if (_hoverButton == button) return;

        _hoverButton = button;
        InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    /// <summary>Without this the widget never learns the pointer left a button.</summary>
    private void EnsureMouseTracking(IntPtr hwnd)
    {
        if (_mouseTracking) return;

        var tme = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = hwnd,
        };

        _mouseTracking = TrackMouseEvent(ref tme);
    }

    private void HidePanel()
    {
        ShowWindow(_hwnd, SW_HIDE);
        SetHover(_hwnd, Renderer.ButtonNone);
    }

    private void ShowPanel()
    {
        ShowWindow(_hwnd, SW_SHOW);
        ApplyTopmost();
        SetForegroundWindow(_hwnd);
    }

    private void TogglePanel()
    {
        if (IsWindowVisible(_hwnd)) HidePanel();
        else ShowPanel();
    }

    private unsafe NOTIFYICONDATAW NewTrayData()
    {
        var data = default(NOTIFYICONDATAW);
        data.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        data.hWnd = _hwnd;
        data.uID = TrayIconId;
        return data;
    }

    private static unsafe void SetTip(ref NOTIFYICONDATAW data, string tip)
    {
        fixed (char* p = data.szTip)
        {
            var count = Math.Min(tip.Length, 127);
            for (var i = 0; i < count; i++) p[i] = tip[i];
            p[count] = '\0';
        }
    }

    private string TrayTip()
    {
        var statuses = _statuses;
        if (statuses.Length == 0) return "Claude usage — loading…";

        var parts = new List<string>();
        foreach (var s in statuses)
        {
            parts.Add(s.Error is not null
                ? $"{s.Account.Label}: {(s.LoggedIn ? "error" : "signed out")}"
                : $"{s.Account.Label}: {s.HeadlinePercent}%");
        }

        var tip = string.Join("\n", parts);
        return tip.Length > 127 ? tip[..127] : tip;
    }

    private void AddTray()
    {
        var data = NewTrayData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _iconSmall;
        SetTip(ref data, TrayTip());

        _trayAdded = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private void UpdateTray()
    {
        if (!_trayAdded) return;

        var data = NewTrayData();
        data.uFlags = NIF_ICON | NIF_TIP;
        data.hIcon = _iconSmall;
        SetTip(ref data, TrayTip());

        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void RemoveTray()
    {
        if (!_trayAdded) return;

        var data = NewTrayData();
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _trayAdded = false;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            if (!IsWindowVisible(_hwnd))
                AppendMenuW(menu, MF_STRING, new IntPtr(IdShowPanel), "Show panel");

            AppendMenuW(menu, MF_STRING, new IntPtr(IdRefresh), "Refresh now");
            AppendMenuW(menu, MF_STRING | (_config.AlwaysOnTop ? MF_CHECKED : 0),
                new IntPtr(IdAlwaysOnTop), "Always on top");
            AppendMenuW(menu, MF_STRING, new IntPtr(IdMinimize), "Minimise to taskbar");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, new IntPtr(IdEditConfig), "Edit config…");
            AppendMenuW(menu, MF_STRING, new IntPtr(IdReload), "Reload config");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, new IntPtr(IdExit), "Exit");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);

            var choice = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            if (choice != 0) OnCommand(choice);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void OnCommand(int id)
    {
        switch (id)
        {
            case IdShowPanel:
                ShowPanel();
                break;

            case IdRefresh:
                StartRefresh();
                break;

            case IdAlwaysOnTop:
                _config.AlwaysOnTop = !_config.AlwaysOnTop;
                ApplyTopmost();
                try { ConfigStore.Save(_config); } catch { }
                break;

            case IdMinimize:
                ShowWindow(_hwnd, SW_MINIMIZE);
                break;

            case IdEditConfig:
                OpenConfig();
                break;

            case IdReload:
                ReloadConfig();
                break;

            case IdExit:
                _shutdown.Cancel();
                _taskbar?.SetState(_hwnd, TaskbarProgress.State.NoProgress);
                RemoveTray();
                PostQuitMessage(0);
                break;
        }
    }

    private void OpenConfig()
    {
        if (!File.Exists(ConfigStore.FilePath))
        {
            try { ConfigStore.Save(_config); } catch { }
        }

        var result = ShellExecuteW(IntPtr.Zero, "open", ConfigStore.FilePath, null, null, SW_SHOW);

        // ShellExecute reports success with a value above 32. Anything lower means no
        // application is registered for .json, which is the default state of a clean Windows
        // install — fall back to the editor that is always there rather than doing nothing.
        if (result.ToInt64() <= 32)
            ShellExecuteW(IntPtr.Zero, "open", "notepad.exe", ConfigStore.FilePath, null, SW_SHOW);
    }

    private void ReloadConfig()
    {
        try
        {
            _config = ConfigStore.Load();
            _claudePath = UsageProbe.ResolveClaudePath(_config.ClaudePath);

            KillTimer(_hwnd, PollTimerId);
            SetTimer(_hwnd, PollTimerId, (uint)(_config.PollSeconds * 1000), IntPtr.Zero);

            ApplyTopmost();
            StartRefresh();
        }
        catch
        {
            // Invalid JSON: keep running on the previous config rather than dying.
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        RemoveTray();
        _fonts.Dispose();
        _taskbar?.Dispose();

        if (_iconSmall != IntPtr.Zero) DestroyIcon(_iconSmall);
        if (_iconBig != IntPtr.Zero) DestroyIcon(_iconBig);

        _shutdown.Dispose();
    }
}
