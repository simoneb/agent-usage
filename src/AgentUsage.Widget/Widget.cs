using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgentUsage;
using AgentUsage.Providers;
using static AgentUsage.Widget.Native;

namespace AgentUsage.Widget;

/// <summary>
/// The whole UI: a borderless always-on-top panel that also owns a taskbar button.
/// The taskbar button carries a progress bar; hovering it (or minimising the panel)
/// shows the same panel rendered as a custom DWM iconic bitmap.
/// </summary>
internal sealed class Widget : IDisposable
{
    private const string ClassName = "AgentUsageWidgetWindow";
    private static Widget? _instance;

    private const int IdRefresh = 1;
    private const int IdAlwaysOnTop = 2;
    private const int IdMinimize = 3;
    private const int IdEditConfig = 4;
    private const int IdReload = 5;
    private const int IdExit = 6;
    private const int IdShowPanel = 7;
    private const int IdAutostart = 8;
    private const int IdGetUpdate = 9;
    private const int IdReleaseNotes = 10;

    // What the update notice in the title bar is currently saying, which is also what clicking
    // it does: install the release, or — once an attempt has failed — hand over to the browser.
    private const int UpdateNone = 0;
    private const int UpdateAvailable = 1;
    private const int UpdateInstalling = 2;
    private const int UpdateFailed = 3;

    // The limit chooser is built from whatever rows the CLI reported, so its ids are assigned
    // at menu-build time from this base.
    private const int IdIconLimitEverything = 100;
    private const int IdIconLimitFirst = 101;

    private const uint TrayIconId = 1;

    private static readonly IntPtr PollTimerId = new(1);
    private static readonly IntPtr ClockTimerId = new(2);

    private IntPtr _hwnd;
    private AppConfig _config;
    private string _claudePath;
    private FontSet _fonts;
    private double _scale = 1.0;
    private TaskbarProgress? _taskbar;

    private IntPtr _iconSmall;
    private IntPtr _iconBig;

    private List<string> _limitLabels = new();

    private DateTime? _lastRefreshAt;
    private string? _freshness;

    private DateTime? _lastUpdateCheck;
    private volatile string? _updateTag;
    private int _checkingForUpdate;
    private int _updateState = UpdateNone;
    private int _installing;
    private RECT _noticeRect;
    private bool _noticeHovered;

    private int _hoverButton = Renderer.ButtonNone;
    private bool _mouseTracking;
    private bool _trayAdded;

    private volatile AccountStatus[] _statuses = Array.Empty<AccountStatus>();
    private readonly ConcurrentDictionary<string, AuthStatus> _authCache = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _refreshing;
    private int _refreshAgain;

    public Widget()
    {
        _config = ConfigStore.Load();
        _claudePath = ClaudeProvider.ResolveClaudePath(_config.ClaudePath);
        _fonts = new FontSet(1.0);
        _instance = this;
    }

    public unsafe void Run()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);

        // Whatever the previous version renamed aside on its way out. It could not delete itself
        // while it was the process running.
        Updates.CleanLeftovers();

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
            ClassName, "Agent Usage",
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
        SetTimer(_hwnd, ClockTimerId, 1000, IntPtr.Zero);
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
                    || InNotice(pt.X, pt.Y)
                        ? HTCLIENT
                        : HTCAPTION);
            }

            case WM_SETCURSOR:
            {
                // Only the client area gets a say; the rest is the caption, and Windows knows
                // what cursor a caption wants.
                if ((lParam.ToInt64() & 0xFFFF) != HTCLIENT) break;

                GetCursorPos(out var pt);
                ScreenToClient(hwnd, ref pt);

                if (!InNotice(pt.X, pt.Y)) break;

                SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND));
                return new IntPtr(1);
            }

            case WM_MOUSEMOVE:
            {
                var x = (short)(lParam.ToInt64() & 0xFFFF);
                var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

                GetClientRect(hwnd, out var client);

                SetHover(hwnd, Renderer.HitTestButton(x, y, client.Width, _scale));
                SetNoticeHover(hwnd, InNotice(x, y));
                EnsureMouseTracking(hwnd);
                return IntPtr.Zero;
            }

            case WM_MOUSELEAVE:
                _mouseTracking = false;
                SetHover(hwnd, Renderer.ButtonNone);
                SetNoticeHover(hwnd, false);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
            {
                var x = (short)(lParam.ToInt64() & 0xFFFF);
                var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

                GetClientRect(hwnd, out var client);
                var hit = Renderer.HitTestButton(x, y, client.Width, _scale);

                if (hit == Renderer.ButtonMinimize) ShowWindow(hwnd, SW_MINIMIZE);
                else if (hit == Renderer.ButtonClose) HidePanel();
                else if (InNotice(x, y)) StartUpdate();

                return IntPtr.Zero;
            }

            case WM_NCLBUTTONDBLCLK:
            case WM_LBUTTONDBLCLK:
                StartRefresh(force: true);
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
                else if (wParam == ClockTimerId) UpdateFreshness();
                return IntPtr.Zero;

            case WM_APP_REFRESHED:
                OnRefreshed();
                return IntPtr.Zero;

            case WM_APP_REFRESH_NOW:
                StartRefresh();
                return IntPtr.Zero;

            case WM_APP_UPDATE_DONE:
                OnUpdateFinished((InstallResult)(int)wParam.ToInt64());
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

        var notice = UpdateNotice();

        using (var surface = new DibSurface(client.Width, client.Height))
        {
            Renderer.Draw(surface.Hdc, surface.Width, surface.Height, _statuses, _fonts, _scale,
                _hoverButton, _freshness, notice, _noticeHovered);

            // Measured off the same surface that just drew it. The panel repaints whenever the
            // notice appears, changes or resizes, so by the time a mouse message arrives this
            // rectangle describes what is actually on screen.
            SelectObject(surface.Hdc, _fonts.Sub);
            _noticeRect = notice is null
                ? default
                : Renderer.NoticeRect(surface.Hdc, notice, surface.Width, _scale);

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
        Renderer.Draw(surface.Hdc, surface.Width, surface.Height, _statuses, _fonts, _scale,
            Renderer.ButtonNone, _freshness, UpdateNotice());
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

    /// <param name="force">
    /// This refresh was asked for, rather than being the timer coming round again. A poll
    /// already in flight is running against whatever the config was when it started, so
    /// dropping the request would mean an explicit Refresh or Reload doing nothing at all —
    /// silently, and only when the timing happens to overlap.
    /// </param>
    private void StartRefresh(bool force = false)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            // Queued rather than run alongside: two probes in flight can finish out of order,
            // and the older one landing last would put back exactly what the reload removed.
            if (force) Interlocked.Exchange(ref _refreshAgain, 1);
            return;
        }

        var config = _config;
        var accounts = config.Accounts.ToArray();
        var hwnd = _hwnd;

        _ = Task.Run(async () =>
        {
            try
            {
                var results = await UsageService.ProbeAllAsync(config, CachedAuth, _shutdown.Token);

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
                {
                    PostMessageW(hwnd, WM_APP_REFRESHED, IntPtr.Zero, IntPtr.Zero);

                    if (Interlocked.Exchange(ref _refreshAgain, 0) == 1)
                        PostMessageW(hwnd, WM_APP_REFRESH_NOW, IntPtr.Zero, IntPtr.Zero);
                }
            }
        });
    }

    /// <summary>
    /// How stale the reading is, in the coarsest unit that still reads as a number. Recomputed
    /// every second but only repainted when the rendered text actually changes, so a panel
    /// sitting at "3m ago" is not redrawn sixty times a minute.
    /// </summary>
    private void UpdateFreshness()
    {
        var text = _lastRefreshAt is DateTime at ? Describe(DateTime.Now - at) : null;

        if (text == _freshness) return;

        _freshness = text;
        InvalidateRect(_hwnd, IntPtr.Zero, false);
        DwmInvalidateIconicBitmaps(_hwnd);
    }

    private static string Describe(TimeSpan age)
    {
        if (age.TotalSeconds < 5) return "just now";
        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds}s ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";

        return $"{(int)age.TotalHours}h ago";
    }

    /// <summary>
    /// The title bar's right-hand strip. Phrased as the action it performs rather than as an
    /// observation, because clicking it is what installs the release.
    /// </summary>
    private string? UpdateNotice() => _updateState switch
    {
        UpdateInstalling => "updating…",
        UpdateFailed => "update failed",
        UpdateAvailable when _updateTag is string tag => $"update to {tag}",
        _ => null,
    };

    /// <summary>Whether clicking the notice would do anything.</summary>
    private bool NoticeIsClickable() =>
        _updateState is UpdateAvailable or UpdateFailed;

    private bool InNotice(int x, int y) =>
        NoticeIsClickable() &&
        x >= _noticeRect.Left && x < _noticeRect.Right &&
        y >= _noticeRect.Top && y < _noticeRect.Bottom;

    // Keyed by provider as well as directory: two accounts can legitimately share a config dir
    // of null while being different tools entirely.
    private static string AuthKey(AccountConfig account) =>
        $"{ProviderIds.Normalise(account.Provider)}:{account.ConfigDir}";

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

    /// <summary>
    /// Piggybacks on the usage poll rather than owning a timer: the interval is a day, so the
    /// worst this costs is one comparison every thirty seconds.
    /// </summary>
    private void MaybeCheckForUpdate()
    {
        if (!_config.CheckForUpdates) return;
        if (_lastUpdateCheck is DateTime last && DateTime.Now - last < Updates.CheckInterval) return;
        if (Interlocked.Exchange(ref _checkingForUpdate, 1) == 1) return;

        _lastUpdateCheck = DateTime.Now;
        var hwnd = _hwnd;

        _ = Task.Run(async () =>
        {
            try
            {
                var tag = await Updates.LatestTagAsync(_shutdown.Token);

                _updateTag = tag is not null && Updates.IsNewer(tag, Updates.CurrentVersion())
                    ? tag
                    : null;

                // An attempt that failed earlier keeps saying so; a check that finds nothing new
                // clears the notice entirely.
                if (_updateState != UpdateInstalling && _updateState != UpdateFailed)
                    _updateState = _updateTag is null ? UpdateNone : UpdateAvailable;
            }
            finally
            {
                Interlocked.Exchange(ref _checkingForUpdate, 0);

                if (!_shutdown.IsCancellationRequested)
                    PostMessageW(hwnd, WM_APP_REFRESHED, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    /// <summary>
    /// Downloads the newest release, verifies it against the checksum published beside it, puts
    /// it in place of this binary and restarts into it. Started by clicking the notice, by the
    /// tray menu, or on its own when the config asks for it.
    /// </summary>
    private void StartUpdate()
    {
        // A failed attempt has already said everything it can from inside the app. The browser
        // is the honest fallback: whatever went wrong, the release page still works.
        if (_updateState == UpdateFailed)
        {
            ShellExecuteW(IntPtr.Zero, "open", Updates.ReleasesPage, null, null, SW_SHOW);
            return;
        }

        if (_updateState != UpdateAvailable) return;
        if (Interlocked.Exchange(ref _installing, 1) == 1) return;

        _updateState = UpdateInstalling;
        SetNoticeHover(_hwnd, false);
        InvalidateRect(_hwnd, IntPtr.Zero, false);
        DwmInvalidateIconicBitmaps(_hwnd);

        var hwnd = _hwnd;

        _ = Task.Run(async () =>
        {
            var result = InstallResult.DownloadFailed;

            try
            {
                result = await Updates.InstallLatestAsync(_shutdown.Token);
            }
            finally
            {
                Interlocked.Exchange(ref _installing, 0);

                if (!_shutdown.IsCancellationRequested)
                    PostMessageW(hwnd, WM_APP_UPDATE_DONE, new IntPtr((int)result), IntPtr.Zero);
            }
        });
    }

    private void OnUpdateFinished(InstallResult result)
    {
        switch (result)
        {
            case InstallResult.Installed:
                RestartIntoNewVersion();
                return;

            // The check said there was something newer and the release says otherwise: nothing
            // is wrong, there is just nothing to do.
            case InstallResult.AlreadyCurrent:
                _updateTag = null;
                _updateState = UpdateNone;
                break;

            default:
                _updateState = UpdateFailed;
                break;
        }

        InvalidateRect(_hwnd, IntPtr.Zero, false);
        DwmInvalidateIconicBitmaps(_hwnd);
    }

    /// <summary>
    /// The binary at this path is now the new one. Leave the tray before starting it, so there
    /// is never a moment with two icons, and quit as soon as it is on its way.
    /// </summary>
    private void RestartIntoNewVersion()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;

        _shutdown.Cancel();
        _taskbar?.SetState(_hwnd, TaskbarProgress.State.NoProgress);
        RemoveTray();
        ShowWindow(_hwnd, SW_HIDE);

        ShellExecuteW(IntPtr.Zero, "open", exe, null, Path.GetDirectoryName(exe), SW_SHOW);
        PostQuitMessage(0);
    }

    private void OnRefreshed()
    {
        _lastRefreshAt = DateTime.Now;
        _freshness = "just now";

        MaybeCheckForUpdate();

        // Off by default: an app that replaces itself and restarts while you are reading it is
        // a surprise, and the notice is one click either way.
        if (_config.AutoUpdate && _updateState == UpdateAvailable) StartUpdate();

        ResizeToContent();
        InvalidateRect(_hwnd, IntPtr.Zero, false);
        UpdateTaskbar();
        UpdateIcon();

        // Tell DWM the cached hover bitmap is out of date.
        DwmInvalidateIconicBitmaps(_hwnd);
    }

    /// <summary>
    /// The single number this account contributes to the taskbar bar and the tooltip: the
    /// chosen limit, or the weekly headline when no choice has been made.
    /// </summary>
    private int? MetricFor(AccountStatus status) =>
        _config.IconLimit is string fragment ? status.PercentFor(fragment) : status.HeadlinePercent;

    private void UpdateTaskbar()
    {
        if (_taskbar is null) return;

        var statuses = _statuses;
        int? headline = null;
        var anyError = false;

        foreach (var s in statuses)
        {
            if (s.Error is not null) { anyError = true; continue; }
            if (MetricFor(s) is int p) headline = headline is null ? p : Math.Max(headline.Value, p);
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

    /// <summary>
    /// The DPI of the monitor the taskbar is on, which is the one the tray icon is drawn at.
    /// Not necessarily the panel's: a laptop screen at 150% beside an external one at 100% has
    /// the tray on whichever of the two holds the taskbar.
    /// </summary>
    private uint TrayDpi()
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        var dpi = tray != IntPtr.Zero ? GetDpiForWindow(tray) : 0;

        if (dpi == 0) dpi = GetDpiForWindow(_hwnd);

        return dpi == 0 ? 96 : dpi;
    }

    private static int Metric(int index, uint dpi, int fallback)
    {
        var value = GetSystemMetricsForDpi(index, dpi);
        return value > 0 ? value : fallback;
    }

    private void UpdateIcon()
    {
        var statuses = _statuses;

        // Built at the size the shell is going to draw, rather than a fixed 16: on a display at
        // 150% the tray's box is 24 pixels, and a 16-pixel icon dropped into it is both blurry
        // and visibly smaller than everything beside it.
        var dpi = TrayDpi();

        var small = IconBuilder.Build(statuses, Metric(SM_CXSMICON, dpi, 16), _config.IconLimit);
        var big = IconBuilder.Build(statuses, Metric(SM_CXICON, dpi, 32), _config.IconLimit);

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

    private void SetNoticeHover(IntPtr hwnd, bool hovered)
    {
        if (_noticeHovered == hovered) return;

        _noticeHovered = hovered;
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
        SetNoticeHover(_hwnd, false);
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
        if (statuses.Length == 0) return "Agent usage — loading…";

        // Name the metric, since which one the icon reports is now a choice.
        var metric = _config.IconLimit is string fragment ? MenuLabel(fragment).ToLowerInvariant() : "week";

        var parts = new List<string>();
        foreach (var s in statuses)
        {
            parts.Add(s.Error is not null
                ? $"{s.Account.Label} · {(s.LoggedIn ? "error" : "signed out")}"
                : $"{s.Account.Label} · {metric} {MetricFor(s)}%");
        }

        var tip = string.Join("\n", parts);
        return tip.Length > 127 ? tip[..127] : tip;
    }

    private void AddTray()
    {
        var data = NewTrayData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _iconSmall;
        SetTip(ref data, TrayTip());

        _trayAdded = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_trayAdded) return;

        // Opt into the modern contract, which is what makes the full tip buffer and
        // NIF_SHOWTIP mean anything. The callback still reports its event in the low word of
        // lParam, so the existing handler needs no change.
        var version = NewTrayData();
        version.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref version);
    }

    private void UpdateTray()
    {
        if (!_trayAdded) return;

        var data = NewTrayData();
        data.uFlags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
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

    /// <summary>
    /// The chooser lists the limit rows the CLI actually reported, so a new window like a
    /// model-specific weekly cap appears here without a code change.
    /// </summary>
    private IntPtr BuildLimitMenu()
    {
        _limitLabels = new List<string>();

        foreach (var s in _statuses)
            foreach (var limit in s.Limits)
                if (!_limitLabels.Contains(limit.Label))
                    _limitLabels.Add(limit.Label);

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return IntPtr.Zero;

        AppendMenuW(menu, MF_STRING | (_config.IconLimit is null ? MF_CHECKED : 0),
            new IntPtr(IdIconLimitEverything), "Everything that fits");

        if (_limitLabels.Count > 0)
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);

        for (var i = 0; i < _limitLabels.Count; i++)
        {
            var active = _config.IconLimit is string fragment &&
                         _limitLabels[i].Contains(fragment, StringComparison.OrdinalIgnoreCase);

            AppendMenuW(menu, MF_STRING | (active ? MF_CHECKED : 0),
                new IntPtr(IdIconLimitFirst + i), MenuLabel(_limitLabels[i]));
        }

        return menu;
    }

    /// <summary>"Current week (all models)" reads as noise in a menu of them; drop the prefix.</summary>
    private static string MenuLabel(string label)
    {
        var trimmed = label.StartsWith("Current ", StringComparison.OrdinalIgnoreCase)
            ? label["Current ".Length..]
            : label;

        return trimmed.Length > 0 ? char.ToUpperInvariant(trimmed[0]) + trimmed[1..] : label;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            if (_updateState == UpdateInstalling)
            {
                AppendMenuW(menu, MF_STRING | MF_GRAYED, new IntPtr(IdGetUpdate), "Updating…");
                AppendMenuW(menu, MF_STRING, new IntPtr(IdReleaseNotes), "Release notes…");
                AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
            }
            else if (_updateTag is string tag)
            {
                AppendMenuW(menu, MF_STRING, new IntPtr(IdGetUpdate),
                    _updateState == UpdateFailed ? $"Retry update to {tag}" : $"Update to {tag}");

                AppendMenuW(menu, MF_STRING, new IntPtr(IdReleaseNotes), "Release notes…");
                AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
            }

            if (!IsWindowVisible(_hwnd))
                AppendMenuW(menu, MF_STRING, new IntPtr(IdShowPanel), "Show panel");

            AppendMenuW(menu, MF_STRING, new IntPtr(IdRefresh), "Refresh now");
            var limitMenu = BuildLimitMenu();
            if (limitMenu != IntPtr.Zero)
                AppendMenuW(menu, MF_STRING | MF_POPUP, limitMenu, "Icon shows");

            AppendMenuW(menu, MF_STRING | (_config.AlwaysOnTop ? MF_CHECKED : 0),
                new IntPtr(IdAlwaysOnTop), "Always on top");

            // Read live rather than cached: Task Manager's Startup apps can turn this off
            // behind our back, and a stale checkmark would be a lie.
            AppendMenuW(menu, MF_STRING | (Autostart.IsEnabled() ? MF_CHECKED : 0),
                new IntPtr(IdAutostart), "Start with Windows");
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
        if (id == IdIconLimitEverything)
        {
            SetIconLimit(null);
            return;
        }

        if (id >= IdIconLimitFirst && id < IdIconLimitFirst + _limitLabels.Count)
        {
            SetIconLimit(_limitLabels[id - IdIconLimitFirst]);
            return;
        }

        switch (id)
        {
            case IdShowPanel:
                ShowPanel();
                break;

            case IdRefresh:
                StartRefresh(force: true);
                break;

            case IdGetUpdate:
                // A retry after a failure starts a real attempt rather than opening the browser,
                // which is what clicking the notice in that state would do.
                if (_updateState == UpdateFailed) _updateState = UpdateAvailable;
                StartUpdate();
                break;

            case IdReleaseNotes:
                ShellExecuteW(IntPtr.Zero, "open", Updates.ReleasesPage, null, null, SW_SHOW);
                break;

            case IdAutostart:
                Autostart.Set(!Autostart.IsEnabled());
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

    private void SetIconLimit(string? label)
    {
        _config.IconLimit = label;

        try { ConfigStore.Save(_config); } catch { }

        UpdateIcon();
        UpdateTaskbar();
        DwmInvalidateIconicBitmaps(_hwnd);
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
            _claudePath = ClaudeProvider.ResolveClaudePath(_config.ClaudePath);

            // Drop readings for accounts that are gone, and redraw now rather than at the end of
            // whichever poll finishes next. An account removed from the file has to leave the
            // panel when you press Reload, or pressing Reload looks like it did nothing.
            _statuses = Readings.ForAccounts(_config.Accounts, _statuses);

            ResizeToContent();
            InvalidateRect(_hwnd, IntPtr.Zero, false);
            UpdateTaskbar();
            UpdateIcon();
            DwmInvalidateIconicBitmaps(_hwnd);

            KillTimer(_hwnd, PollTimerId);
            SetTimer(_hwnd, PollTimerId, (uint)(_config.PollSeconds * 1000), IntPtr.Zero);

            ApplyTopmost();
            StartRefresh(force: true);
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
