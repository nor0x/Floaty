using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Minimal Windows notification-area (system tray) icon for the overlay, implemented directly on
/// <c>Shell_NotifyIcon</c> so it needs no extra dependencies. Left-click toggles the overlay's
/// visibility; right-click opens a Show/Hide + Quit menu.
/// </summary>
public sealed class WindowsTrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;
    private const int TrayId = 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const int MenuShowHide = 100;
    private const int MenuQuit = 101;

    // Keep the delegate alive for the lifetime of the subclass to avoid GC of the callback.
    private readonly SUBCLASSPROC _subclassProc;

    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private bool _added;

    public WindowsTrayIcon(nint hwnd, AppWindow appWindow)
    {
        _hwnd = hwnd;
        _appWindow = appWindow;
        _subclassProc = WndProc;
    }

    public void Show()
    {
        SetWindowSubclass(_hwnd, _subclassProc, TrayId, 0);

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = LoadAppIcon(),
            szTip = "Floaty",
        };

        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (msg == CallbackMessage)
        {
            // For NIF_MESSAGE icons the mouse message lives in the low word of lParam.
            switch ((int)(lParam & 0xFFFF))
            {
                case WM_LBUTTONUP:
                    ToggleVisibility();
                    break;
                case WM_RBUTTONUP:
                    ShowContextMenu();
                    break;
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ToggleVisibility()
    {
        if (_appWindow.IsVisible)
            _appWindow.Hide();
        else
            _appWindow.Show(true);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, MenuShowHide, _appWindow.IsVisible ? "Hide" : "Show");
        AppendMenu(menu, MF_STRING, MenuQuit, "Quit");

        GetCursorPos(out var pt);
        // Required so the menu dismisses correctly when the user clicks elsewhere.
        SetForegroundWindow(_hwnd);

        var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, nint.Zero);
        DestroyMenu(menu);

        switch (cmd)
        {
            case MenuShowHide:
                ToggleVisibility();
                break;
            case MenuQuit:
                Microsoft.UI.Xaml.Application.Current.Exit();
                break;
        }
    }

    private static nint LoadAppIcon()
    {
        // Prefer the executable's own icon; fall back to the generic application icon.
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exePath)
            && ExtractIconEx(exePath, 0, out _, out var small, 1) > 0
            && small != nint.Zero)
        {
            return small;
        }

        return LoadIcon(nint.Zero, (nint)32512 /* IDI_APPLICATION */);
    }

    public void Dispose()
    {
        if (!_added)
            return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayId,
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        RemoveWindowSubclass(_hwnd, _subclassProc, TrayId);
        _added = false;
    }

    // --- Win32 interop ---

    private const int NIM_ADD = 0x0;
    private const int NIM_DELETE = 0x2;
    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;
    private const uint MF_STRING = 0x0;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
