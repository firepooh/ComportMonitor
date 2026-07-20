using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace ComportMonitor;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_MOVING = 0x0216;
    private const int SnapDistance = 20; // 자석 스냅 감지 거리 (DIU, DPI 배율 적용)
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_STYLECHANGING = 0x007C;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // 아크릴
    private const int EdgeMargin = 16;
    private static readonly TimeSpan HighlightDuration = TimeSpan.FromSeconds(4);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STYLESTRUCT
    {
        public int styleOld, styleNew;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public ObservableCollection<PortInfo> Ports { get; } = new();

    private readonly DispatcherTimer _debounce;
    private bool _refreshing;
    private bool _initialLoadDone;
    private bool _busyWatch = true;   // 포트 점유 상태 표시 옵션
    private DispatcherTimer? _busyTimer;
    private bool _probing;
    // 투명도 축: 양수 = 아크릴 위 틴트 농도(진하게), 0 = 순수 아크릴,
    // 음수 = 창 전체 알파 페이드(글자 포함 유령화, 255+_tint가 창 알파)
    private int _tint;
    private const int DefaultTint = 0;
    private const int MaxTint = 230;
    private const int MinTint = -155; // 창 알파 하한 100 (255-155)
    private const int TintStep = 15;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    // WPF는 자신이 관리하는 창의 WS_EX_LAYERED를 스타일 변경 처리 중 즉시 제거한다.
    // 서브클래스 프로시저에서 WPF 처리 후 비트를 되살리는 방식으로 우회한다.
    private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam,
        IntPtr lParam, UIntPtr id, UIntPtr refData);
    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate proc,
        UIntPtr id, UIntPtr refData);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private SubclassProcDelegate? _subclassProc; // GC 방지용 참조 유지
    private IntPtr _hwnd;
    private bool _inSizeMove;          // 사용자 드래그 중 여부
    private int _targetX, _targetY;    // 물리 픽셀 기준 목표 위치
    private bool _targetValid;
    private int _dragOffsetX, _dragOffsetY; // 드래그 시작 시 마우스-창 좌상단 오프셋

    // 자동 숨김: 일정 시간 포트 변동이 없으면 숨기고, 변동이 생기면 다시 표시
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private bool _autoHide = true;
    private bool _hiddenByTimeout;   // 타임아웃으로 숨겨진 상태 (변동 시 자동 재표시 대상)
    private DateTime _lastActivity = DateTime.Now;
    private DispatcherTimer? _idleTimer;
    private WinForms.NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        var infoVer = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "?";
        int plus = infoVer.IndexOf('+'); // 빌드 해시 접미사 제거
        VersionMenu.Header = $"ComportMonitor v{(plus > 0 ? infoVer[..plus] : infoVer)}";

        var settings = LoadSettings();
        _busyWatch = settings?.BusyWatch ?? true;
        BusyMenu.IsChecked = _busyWatch;
        _tint = Math.Clamp(settings?.Tint ?? DefaultTint, MinTint, MaxTint);
        ApplyTint();
        _autoHide = settings?.AutoHide ?? true;
        AutoHideMenu.IsChecked = _autoHide;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(); };

        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            ResizeToContent();
            PlaceInitial();
            UpdateBusyWatch();
            ApplyTint(); // WPF가 표시 시점에 확장 스타일을 되덮어쓰므로 표시 후 재적용

            // WinForms(NotifyIcon) 초기화는 DPI/좌표 컨텍스트에 영향을 줄 수 있어
            // 반드시 창 생성·배치가 끝난 뒤에 수행한다
            InitTray();
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _idleTimer.Tick += (_, _) => IdleTick();
            _idleTimer.Start();
            // 초기 스폰 위치와 최종 위치의 모니터 DPI가 다르면 WM_DPICHANGED 반영이
            // 끝난 뒤 크기를 한 번 더 맞춘다
            await Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                EnforceSize();
                ClampToWorkArea();
            });
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwnd = source.Handle;
        source.AddHook(WndProc);
        _subclassProc = SubclassProc;
        SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)1, UIntPtr.Zero);
        // Alt+Tab 목록에서 숨김
        SetWindowLong(_hwnd, GWL_EXSTYLE,
            GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        // Win11 DWM: 둥근 모서리 + 어두운 아크릴 백드롭 (반투명 효과)
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int dark = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(_hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        ApplyTint(); // 저장된 값이 페이드 구간(음수)이면 레이어드 알파 적용
        // 표시되기 전에 대략적 크기로 목표 위치에 배치 (Loaded에서 실측치로 재배치)
        PlaceInitial();
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr id, UIntPtr refData)
    {
        if (msg == WM_STYLECHANGING && wParam.ToInt64() == GWL_EXSTYLE && _tint < 0)
        {
            // WPF 처리(LAYERED 제거)를 먼저 통과시킨 뒤 비트를 되살린다
            IntPtr result = DefSubclassProc(hWnd, msg, wParam, lParam);
            var ss = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
            if ((ss.styleNew & WS_EX_LAYERED) == 0)
            {
                ss.styleNew |= WS_EX_LAYERED;
                Marshal.StructureToPtr(ss, lParam, false);
            }
            return result;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            // 장치 하나에도 메시지가 연달아 오므로 디바운스 후 1회만 재열거
            _debounce.Stop();
            _debounce.Start();
        }
        else if (msg == WM_ENTERSIZEMOVE)
        {
            _inSizeMove = true;
            // 스냅 해제 계산용: 마우스와 창 좌상단의 오프셋 기억
            GetCursorPos(out var cp);
            GetWindowRect(_hwnd, out var wr);
            _dragOffsetX = cp.X - wr.Left;
            _dragOffsetY = cp.Y - wr.Top;
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            _inSizeMove = false;
            // 드래그로 옮긴 위치를 새 목표로 기억
            GetWindowRect(_hwnd, out var r);
            _targetX = r.Left;
            _targetY = r.Top;
            _targetValid = true;
        }
        else if (msg == WM_MOVING && _inSizeMove)
        {
            // 스냅된 좌표를 기준으로 다음 이동이 계산되면 한번 붙은 창이 안 떨어지므로,
            // 항상 마우스 위치에서 "원래 있어야 할 위치"를 직접 계산한 뒤 스냅을 적용한다
            var r = Marshal.PtrToStructure<RECT>(lParam);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            GetCursorPos(out var cur);
            r.Left = cur.X - _dragOffsetX;
            r.Top = cur.Y - _dragOffsetY;
            r.Right = r.Left + w;
            r.Bottom = r.Top + h;
            SnapToWorkArea(ref r);
            Marshal.StructureToPtr(r, lParam, false);
            handled = true;
            return (IntPtr)1;
        }
        else if (msg == WM_DPICHANGED)
        {
            // 다른 DPI의 모니터로 이동: WPF 반영이 끝난 뒤 크기 재강제
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                EnforceSize();
                ClampToWorkArea();
            });
        }
        else if (msg == WM_WINDOWPOSCHANGING && _contentW > 0)
        {
            // 시스템 DPI != 모니터 DPI 환경에서 WPF가 HWND 크기/위치를 잘못된 DPI
            // 기준으로 밀어넣는 버그 우회: 크기는 항상 캐시된 콘텐츠 DIU × 실제 DPI로,
            // 위치는 드래그 중이 아니면 목표 위치(물리 픽셀)로 강제한다.
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            bool changed = false;
            if ((wp.flags & SWP_NOSIZE) == 0)
            {
                double scale = GetDpiForWindow(_hwnd) / 96.0;
                int pw = (int)Math.Round(_contentW * scale);
                int ph = (int)Math.Round(_contentH * scale);
                if (wp.cx != pw || wp.cy != ph)
                {
                    wp.cx = pw;
                    wp.cy = ph;
                    changed = true;
                }
            }
            if ((wp.flags & SWP_NOMOVE) == 0 && !_inSizeMove && _targetValid &&
                (wp.x != _targetX || wp.y != _targetY))
            {
                wp.x = _targetX;
                wp.y = _targetY;
                changed = true;
            }
            if (changed) Marshal.StructureToPtr(wp, lParam, false);
        }
        return IntPtr.Zero;
    }

    // ── 포트 목록 갱신 ────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _debounce.Start(); // 진행 중이면 잠시 후 다시
            return;
        }
        _refreshing = true;
        try
        {
            var current = await Task.Run(SerialPortEnumerator.Enumerate);
            if (current is null) return; // WMI 오류 시 이전 상태 유지
            ApplyDiff(current);
            _initialLoadDone = true;
        }
        finally
        {
            _refreshing = false;
        }
        await ProbeBusyAsync();
    }

    // ── 트레이 아이콘 / 자동 숨김 ─────────────────────────────────

    private void InitTray()
    {
        _tray = new WinForms.NotifyIcon { Text = "COM Port Monitor", Visible = true };
        using (var s = Application.GetResourceStream(
                   new Uri("pack://application:,,,/ComportMonitor.ico"))!.Stream)
        {
            _tray.Icon = new System.Drawing.Icon(s);
        }
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ToggleWidget();
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleWidget());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
    }

    /// <summary>포트 변동/사용자 조작 발생 — 유휴 타이머를 리셋하고 필요하면 재표시.</summary>
    private void MarkActivity()
    {
        _lastActivity = DateTime.Now;
        if (_hiddenByTimeout) ShowWidget();
    }

    private void IdleTick()
    {
        if (!_autoHide || !IsVisible) return;
        if (IsMouseOver)
        {
            _lastActivity = DateTime.Now; // 보고 있는 동안엔 숨기지 않는다
            return;
        }
        if (DateTime.Now - _lastActivity >= IdleTimeout)
        {
            _hiddenByTimeout = true;
            Hide();
        }
    }

    private void ToggleWidget()
    {
        if (IsVisible)
        {
            _hiddenByTimeout = false; // 사용자가 직접 숨김 → 변동에도 자동 재표시 안 함
            Hide();
        }
        else
        {
            ShowWidget();
        }
    }

    private void ShowWidget()
    {
        _hiddenByTimeout = false;
        _lastActivity = DateTime.Now;
        Show();
        // Show() 과정에서 WPF가 캐시된 스타일/크기를 되덮을 수 있어 재적용
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_hwnd == IntPtr.Zero) return;
            SetWindowLong(_hwnd, GWL_EXSTYLE,
                GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            ApplyTint();
            EnforceSize();
            ClampToWorkArea();
        });
    }

    private void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        _autoHide = AutoHideMenu.IsChecked;
        if (!_autoHide && _hiddenByTimeout) ShowWidget();
    }

    // ── 포트 점유 상태 ────────────────────────────────────────────

    private async Task ProbeBusyAsync()
    {
        if (!_busyWatch || _probing) return;
        _probing = true;
        try
        {
            var names = Ports.Where(p => p.Status != PortStatus.Removed)
                             .Select(p => p.PortName).ToList();
            if (names.Count == 0) return;
            var busy = await Task.Run(() => names.ToDictionary(n => n, PortProbe.IsBusy));
            bool changed = false;
            foreach (var port in Ports)
            {
                if (busy.TryGetValue(port.PortName, out var b) && port.IsBusy != b)
                {
                    port.IsBusy = b;
                    changed = true;
                }
            }
            if (changed)
            {
                ResizeToContent(); // 배지 표시로 폭이 변할 수 있음
                MarkActivity();    // 점유 시작/종료도 변동으로 간주
            }
        }
        finally
        {
            _probing = false;
        }
    }

    private void UpdateBusyWatch()
    {
        if (_busyWatch)
        {
            if (_busyTimer is null)
            {
                _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _busyTimer.Tick += (_, _) => _ = ProbeBusyAsync();
            }
            _busyTimer.Start();
            _ = ProbeBusyAsync();
        }
        else
        {
            _busyTimer?.Stop();
            bool changed = false;
            foreach (var port in Ports)
            {
                if (port.IsBusy)
                {
                    port.IsBusy = false;
                    changed = true;
                }
            }
            if (changed) ResizeToContent();
        }
    }

    private void ApplyDiff(List<PortEntry> current)
    {
        bool anyChange = false;
        var found = new HashSet<string>();
        foreach (var entry in current)
        {
            found.Add(entry.PortName);
            var existing = Ports.FirstOrDefault(p => p.PortName == entry.PortName);
            if (existing is null)
            {
                var info = new PortInfo(entry.PortName, entry.Number)
                {
                    Description = entry.Description,
                    PnpId = entry.PnpId,
                    Status = _initialLoadDone ? PortStatus.Added : PortStatus.Normal,
                };
                InsertSorted(info);
                if (info.Status == PortStatus.Added) FadeAddedLater(info);
                anyChange = true;
            }
            else
            {
                existing.Description = entry.Description;
                existing.PnpId = entry.PnpId;
                if (existing.Status == PortStatus.Removed)
                {
                    existing.Status = PortStatus.Added; // 제거 표시 중 재연결됨
                    FadeAddedLater(existing);
                    anyChange = true;
                }
            }
        }

        foreach (var port in Ports.Where(p => !found.Contains(p.PortName) &&
                                              p.Status != PortStatus.Removed).ToList())
        {
            port.Status = PortStatus.Removed;
            RemoveLater(port);
            anyChange = true;
        }

        if (_initialLoadDone)
        {
            ResizeToContent();
            if (anyChange) MarkActivity();
        }
    }

    private void InsertSorted(PortInfo info)
    {
        int i = 0;
        while (i < Ports.Count && Ports[i].Number < info.Number) i++;
        Ports.Insert(i, info);
    }

    private async void FadeAddedLater(PortInfo port)
    {
        await Task.Delay(HighlightDuration);
        if (port.Status == PortStatus.Added) port.Status = PortStatus.Normal;
    }

    private async void RemoveLater(PortInfo port)
    {
        await Task.Delay(HighlightDuration);
        if (port.Status == PortStatus.Removed)
        {
            Ports.Remove(port);
            ResizeToContent();
        }
    }

    // ── 크기/위치 (물리 픽셀 기준, Win32) ─────────────────────────

    // 콘텐츠의 무한대-측정 크기(DIU). WPF가 작은 창에 맞춰 Root를 재측정하면
    // Root.DesiredSize가 그 축소값으로 줄고, 크기 강제가 그 값을 기준 삼아 작은
    // 크기로 굳는 피드백 루프가 생긴다. 무한대 측정값을 여기 캐시해 모든 크기
    // 강제의 유일한 기준으로 쓴다 (축소된 창 크기의 영향을 받지 않음).
    private double _contentW, _contentH;

    private void MeasureContent()
    {
        // ObservableCollection 변경 직후엔 ItemsControl이 행 컨테이너를 아직 생성하지
        // 않아 Measure가 행 높이를 누락한다. UpdateLayout()으로 대기 중인 레이아웃(=행
        // 생성)을 먼저 반영한 뒤 무한대로 측정해야 정확한 크기가 나온다.
        UpdateLayout();
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _contentW = Root.DesiredSize.Width;
        _contentH = Root.DesiredSize.Height;
    }

    private void ResizeToContent()
    {
        MeasureContent();
        Width = _contentW;
        Height = _contentH;
        UpdateLayout();
        EnforceSize();
        ClampToWorkArea();
    }

    /// <summary>캐시된 콘텐츠 DIU × 창의 실제 DPI로 HWND 물리 크기를 맞춘다.</summary>
    private void EnforceSize()
    {
        if (_hwnd == IntPtr.Zero || _contentW <= 0) return;
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        int pw = (int)Math.Round(_contentW * scale);
        int ph = (int)Math.Round(_contentH * scale);
        GetWindowRect(_hwnd, out var r);
        if (r.Right - r.Left != pw || r.Bottom - r.Top != ph)
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, pw, ph,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>드래그 중인 rect가 작업 영역 가장자리 근처면 딱 붙도록 보정한다.</summary>
    private bool SnapToWorkArea(ref RECT r)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST), ref mi))
            return false;
        var work = mi.rcWork;
        int t = (int)Math.Round(SnapDistance * GetDpiForWindow(_hwnd) / 96.0);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        bool snapped = false;

        if (Math.Abs(r.Left - work.Left) <= t)
        {
            r.Left = work.Left;
            r.Right = r.Left + w;
            snapped = true;
        }
        else if (Math.Abs(work.Right - r.Right) <= t)
        {
            r.Right = work.Right;
            r.Left = r.Right - w;
            snapped = true;
        }

        if (Math.Abs(r.Top - work.Top) <= t)
        {
            r.Top = work.Top;
            r.Bottom = r.Top + h;
            snapped = true;
        }
        else if (Math.Abs(work.Bottom - r.Bottom) <= t)
        {
            r.Bottom = work.Bottom;
            r.Top = r.Bottom - h;
            snapped = true;
        }
        return snapped;
    }

    private RECT GetWorkArea()
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST), ref mi);
        return mi.rcWork;
    }

    private void PlaceInitial()
    {
        // 주 모니터 DPI 기준으로 최종 크기를 계산해 위치와 함께 한 번에 적용
        MeasureContent();
        GetDpiForMonitor(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY),
            0 /*MDT_EFFECTIVE_DPI*/, out uint dpiX, out _);
        double scale = dpiX / 96.0;
        int pw = (int)Math.Round(_contentW * scale);
        int ph = (int)Math.Round(_contentH * scale);

        var s = LoadSettings();
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        int x, y;
        if (s is not null &&
            s.Left >= vx && s.Left <= vx + vw - 50 &&
            s.Top >= vy && s.Top <= vy + vh - 50)
        {
            x = s.Left;
            y = s.Top;
        }
        else
        {
            // 기본 위치: 주 모니터 작업 영역 우측 상단
            // (SPI_GETWORKAREA는 DPI 가상화로 좌표가 왜곡될 수 있어 GetMonitorInfo 사용)
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY), ref mi);
            x = mi.rcWork.Right - pw - EdgeMargin;
            y = mi.rcWork.Top + EdgeMargin;
        }
        _targetX = x;
        _targetY = y;
        _targetValid = true;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, pw, ph, SWP_NOZORDER | SWP_NOACTIVATE);
        StartSettler();
    }

    // WPF가 DPI 전환 처리 중 낡은 좌표계로 창을 되돌리는 경우가 있어,
    // 시작 직후 잠시 동안 목표 rect로 수렴할 때까지 주기적으로 재적용한다.
    private DispatcherTimer? _settler;
    private int _settleStable, _settleTicks;
    private uint _settleLastDpi;

    private void StartSettler()
    {
        _settleStable = 0;
        _settleTicks = 0;
        _settleLastDpi = 0;
        if (_settler is null)
        {
            _settler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _settler.Tick += (_, _) => SettlerTick();
        }
        _settler.Start();
    }

    private void SettlerTick()
    {
        _settleTicks++;
        if (_inSizeMove) return;

        // 배치 중 GetDpiForWindow가 96↔실제값으로 잠깐 튀는 사이 잘못된 크기(DIU를
        // px로 처리한 축소본)로 굳는 경우가 있다. DPI가 직전 틱과 달라지면 수렴
        // 카운트를 리셋해, DPI가 안정된 뒤 반드시 한 번 더 올바른 크기로 재보정한다.
        uint dpi = GetDpiForWindow(_hwnd);
        if (dpi != _settleLastDpi)
        {
            _settleStable = 0;
            _settleLastDpi = dpi;
        }

        GetWindowRect(_hwnd, out var r);
        double scale = dpi / 96.0;
        int pw = (int)Math.Round(_contentW * scale);
        int ph = (int)Math.Round(_contentH * scale);
        bool ok = r.Left == _targetX && r.Top == _targetY &&
                  r.Right - r.Left == pw && r.Bottom - r.Top == ph;
        if (ok)
        {
            if (++_settleStable >= 3) _settler!.Stop();
        }
        else
        {
            _settleStable = 0;
            SetWindowPos(_hwnd, IntPtr.Zero, _targetX, _targetY, pw, ph,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        if (_settleTicks > 60) _settler!.Stop();
    }

    private void ClampToWorkArea()
    {
        if (_hwnd == IntPtr.Zero) return;
        GetWindowRect(_hwnd, out var r);
        var work = GetWorkArea();
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int x = Math.Max(work.Left, Math.Min(r.Left, work.Right - w));
        int y = Math.Max(work.Top, Math.Min(r.Top, work.Bottom - h));
        if (x != r.Left || y != r.Top)
        {
            _targetX = x;
            _targetY = y;
            _targetValid = true;
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    // ── 창 이동/메뉴 ──────────────────────────────────────────────

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        MarkActivity();
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void Topmost_Click(object sender, RoutedEventArgs e) => Topmost = TopmostMenu.IsChecked;

    private void BusyWatch_Click(object sender, RoutedEventArgs e)
    {
        _busyWatch = BusyMenu.IsChecked;
        UpdateBusyWatch();
    }

    // ── 투명도(틴트) 조절 ─────────────────────────────────────────

    private void ApplyTint()
    {
        Root.Background = new SolidColorBrush(
            Color.FromArgb((byte)Math.Max(_tint, 0), 0x10, 0x14, 0x1C));

        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (_tint < 0)
        {
            // 아크릴보다 더 투명: 창 전체 알파 페이드 (글자 포함)
            if ((ex & WS_EX_LAYERED) == 0)
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
            bool lwaOk = SetLayeredWindowAttributes(_hwnd, 0, (byte)(255 + _tint), LWA_ALPHA);
#if DEBUG
            int after = GetWindowLong(_hwnd, GWL_EXSTYLE);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "ComportMonitor.debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} ApplyTint tint={_tint} exBefore=0x{ex:X} exAfter=0x{after:X} lwaOk={lwaOk} err={Marshal.GetLastWin32Error()}\n");
#endif
        }
        else if ((ex & WS_EX_LAYERED) != 0)
        {
            // 페이드 구간을 벗어나면 레이어드 스타일 제거 (원래 렌더링 경로 복귀)
            SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        MarkActivity();
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        // 휠 위 = 진하게(불투명), 아래 = 투명하게 (0 아래는 전체 페이드 구간)
        _tint = Math.Clamp(_tint + (e.Delta > 0 ? TintStep : -TintStep), MinTint, MaxTint);
        ApplyTint();
        e.Handled = true;
    }

    private void ResetTint_Click(object sender, RoutedEventArgs e)
    {
        _tint = DefaultTint;
        ApplyTint();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ── 창 위치 저장/복원 (물리 픽셀) ─────────────────────────────

    private record WindowSettings(int Left, int Top, bool BusyWatch = true, int Tint = DefaultTint,
        bool AutoHide = true);

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ComportMonitor", "settings.json");

    private static WindowSettings? LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(SettingsPath));
        }
        catch (Exception) { }
        return null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        try
        {
            GetWindowRect(_hwnd, out var r);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(new WindowSettings(r.Left, r.Top, _busyWatch, _tint, _autoHide)));
        }
        catch (Exception) { }
    }
}
