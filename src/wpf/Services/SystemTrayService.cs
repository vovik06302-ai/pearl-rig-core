using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PearlRigCore.Services;

public enum TrayHealthState
{
    Stopped,
    MiningHealthy,
    Warning,
    Critical
}

/// <summary>Pure Win32 Shell_NotifyIconW system tray integration with dynamic colored HICON and native popup context menu.</summary>
public sealed class SystemTrayService : IDisposable
{
    private const int WmAppTrayCallback = 0x8000 + 42;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmBottomAlign = 0x0020;

    private const int CmdRestore = 1;
    private const int CmdStartMiner = 2;
    private const int CmdStopMiner = 3;
    private const int CmdPresetEco = 4;
    private const int CmdPresetBalance = 5;
    private const int CmdPresetTurbo = 6;
    private const int CmdExit = 7;

    private readonly Window _window;
    private readonly Action _onRestoreRequested;
    private readonly Action _onStartMiner;
    private readonly Action _onStopMiner;
    private readonly Action<string> _onSelectPreset;
    private readonly Action _onExitApp;
    private HwndSource? _hwndSource;
    private NotifyIconData _data;
    private IntPtr _currentCustomIcon;
    private TrayHealthState _lastState = TrayHealthState.Stopped;
    private bool _added;
    private bool _disposed;

    public SystemTrayService(
        Window window,
        Action onRestoreRequested,
        Action onStartMiner,
        Action onStopMiner,
        Action<string> onSelectPreset,
        Action onExitApp)
    {
        _window = window;
        _onRestoreRequested = onRestoreRequested;
        _onStartMiner = onStartMiner;
        _onStopMiner = onStopMiner;
        _onSelectPreset = onSelectPreset;
        _onExitApp = onExitApp;
    }

    public void Initialize()
    {
        if (_disposed || _added) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);

        _currentCustomIcon = CreateStatusIcon(TrayHealthState.Stopped);
        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = handle,
            uID = 1001,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmAppTrayCallback,
            hIcon = _currentCustomIcon,
            szTip = "Pearl Rig Control · Локальный мониторинг"
        };

        _added = Shell_NotifyIconW(NimAdd, ref _data);
    }

    public void UpdateStatus(string text, TrayHealthState state)
    {
        if (!_added || _disposed) return;
        var clean = string.IsNullOrWhiteSpace(text) ? "Pearl Rig Control" : text.Trim();
        _data.szTip = clean.Length <= 120 ? clean : clean[..120];
        _data.uFlags = NifTip;

        if (state != _lastState)
        {
            var newIcon = CreateStatusIcon(state);
            if (newIcon != IntPtr.Zero)
            {
                var oldIcon = _currentCustomIcon;
                _currentCustomIcon = newIcon;
                _data.hIcon = newIcon;
                _data.uFlags |= NifIcon;
                _lastState = state;
                if (oldIcon != IntPtr.Zero) _ = DestroyIcon(oldIcon);
            }
        }

        Shell_NotifyIconW(NimModify, ref _data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmAppTrayCallback)
        {
            var mouseEvent = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseEvent is WmLButtonUp or WmLButtonDblClk)
            {
                _onRestoreRequested();
                handled = true;
            }
            else if (mouseEvent == WmRButtonUp)
            {
                ShowContextMenu(hwnd);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            _ = AppendMenuW(menu, MfString, CmdRestore, "Развернуть Pearl Rig Control");
            _ = AppendMenuW(menu, MfSeparator, 0, string.Empty);
            _ = AppendMenuW(menu, MfString, CmdStartMiner, "Запустить майнер");
            _ = AppendMenuW(menu, MfString, CmdStopMiner, "Остановить майнер");
            _ = AppendMenuW(menu, MfSeparator, 0, string.Empty);
            _ = AppendMenuW(menu, MfString, CmdPresetEco, "Профиль: Eco (Макс. GH/W)");
            _ = AppendMenuW(menu, MfString, CmdPresetBalance, "Профиль: Баланс 24/7");
            _ = AppendMenuW(menu, MfString, CmdPresetTurbo, "Профиль: Turbo (Макс. TH/s)");
            _ = AppendMenuW(menu, MfSeparator, 0, string.Empty);
            _ = AppendMenuW(menu, MfString, CmdExit, "Выход из программы");

            _ = GetCursorPos(out var pt);
            _ = SetForegroundWindow(hwnd);
            var selected = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmBottomAlign, pt.X, pt.Y, hwnd, IntPtr.Zero);
            switch (selected)
            {
                case CmdRestore: _onRestoreRequested(); break;
                case CmdStartMiner: _onStartMiner(); break;
                case CmdStopMiner: _onStopMiner(); break;
                case CmdPresetEco: _onSelectPreset("Eco"); break;
                case CmdPresetBalance: _onSelectPreset("Balance"); break;
                case CmdPresetTurbo: _onSelectPreset("Turbo"); break;
                case CmdExit: _onExitApp(); break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private static IntPtr CreateStatusIcon(TrayHealthState state)
    {
        const int size = 16;
        uint accentBgra = state switch
        {
            TrayHealthState.MiningHealthy => 0xFF00E5C0,
            TrayHealthState.Warning => 0xFFF59E0B,
            TrayHealthState.Critical => 0xFFEF4444,
            _ => 0xFF64748B
        };

        var pixels = new uint[size * size];
        var center = (size - 1) / 2.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= 3.6)
                    pixels[y * size + x] = accentBgra;
                else if (dist <= 6.2)
                    pixels[y * size + x] = 0xFF0B0F17;
                else if (dist <= 7.4)
                    pixels[y * size + x] = accentBgra;
                else
                    pixels[y * size + x] = 0x00000000;
            }
        }

        var colorBmp = CreateBitmap(size, size, 1, 32, pixels);
        var maskBmp = CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        try
        {
            var iconInfo = new IconInfo { fIcon = true, hbmColor = colorBmp, hbmMask = maskBmp };
            return CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            if (colorBmp != IntPtr.Zero) _ = DeleteObject(colorBmp);
            if (maskBmp != IntPtr.Zero) _ = DeleteObject(maskBmp);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            Shell_NotifyIconW(NimDelete, ref _data);
            _added = false;
        }
        if (_currentCustomIcon != IntPtr.Zero)
        {
            _ = DestroyIcon(_currentCustomIcon);
            _currentCustomIcon = IntPtr.Zero;
        }
        _hwndSource?.RemoveHook(WndProc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointStruct lpPoint);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo icon);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, uint[] lpvBits);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, byte[] lpvBits);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
