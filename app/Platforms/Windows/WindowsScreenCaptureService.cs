using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Floaty.Services;
using Interop.UIAutomationClient;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Windows implementation of <see cref="IScreenCaptureService"/>. Captures the top-most application
/// window beneath the always-on-top overlay:
/// <list type="bullet">
///   <item><description>a PNG, via <c>PrintWindow</c> (renders the window even when Floaty covers it);</description></item>
///   <item><description>its accessibility text, via UI Automation.</description></item>
/// </list>
/// Both are written to <c>~/.floaty/captures</c>. All native work runs on a background thread.
/// </summary>
public sealed class WindowsScreenCaptureService : IScreenCaptureService
{
    // Stop walking the UI Automation tree past this many elements so pathological trees can't hang us.
    private const int MaxElements = 3000;

    // Automatic history screenshots are downscaled to this width: plenty for a vision model to read,
    // a fraction of the disk and token cost of a full-resolution window. Manual captures stay full-res.
    private const int AutoCaptureMaxImageWidth = 1280;

    public Task<CaptureResult?> CaptureUnderlyingWindowAsync(CancellationToken cancellationToken = default) =>
        Task.Run<CaptureResult?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hwnd = FindUnderlyingWindow();
            if (hwnd == nint.Zero)
                return null;

            return CaptureCore(hwnd, includeScreenshot: true, maxImageWidth: 0);
        }, cancellationToken);

    public Task<CaptureResult?> CaptureWindowAsync(
        nint hwnd,
        bool includeScreenshot,
        CancellationToken cancellationToken = default) =>
        Task.Run<CaptureResult?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-validate: between the foreground event and the debounced capture the window may
            // have closed, minimized, or been cloaked.
            if (hwnd == nint.Zero || !IsCandidateWindow(hwnd, (uint)Environment.ProcessId))
                return null;

            return CaptureCore(hwnd, includeScreenshot, AutoCaptureMaxImageWidth);
        }, cancellationToken);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<WindowInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ownPid = (uint)Environment.ProcessId;
            var windows = new List<WindowInfo>();

            EnumWindows((hwnd, _) =>
            {
                if (IsCandidateWindow(hwnd, ownPid))
                    windows.Add(new WindowInfo(hwnd, GetWindowText(hwnd), GetProcessName(hwnd)));
                return true; // keep enumerating: we want every candidate, front to back
            }, nint.Zero);

            return windows;
        }, cancellationToken);

    /// <summary>
    /// Captures <paramref name="hwnd"/>: accessibility text always, screenshot only when requested
    /// (text-only captures return an empty <see cref="CaptureResult.ImagePath"/>, which downstream
    /// consumers treat as "no image"). <paramref name="maxImageWidth"/> &gt; 0 downscales the PNG.
    /// </summary>
    private static CaptureResult CaptureCore(nint hwnd, bool includeScreenshot, int maxImageWidth)
    {
        var title = GetWindowText(hwnd);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var imagePath = includeScreenshot
            ? Path.Combine(FloatyPaths.Captures, $"capture-{stamp}.png")
            : string.Empty;
        var textPath = Path.Combine(FloatyPaths.Captures, $"capture-{stamp}.txt");

        if (includeScreenshot)
            SaveWindowImage(hwnd, imagePath, maxImageWidth);
        var content = SaveWindowText(hwnd, title, textPath);

        return new CaptureResult(imagePath, textPath, title, content);
    }

    // --- Target window selection -------------------------------------------------------------

    /// <summary>
    /// Returns the top-most real application window that isn't one of our own. EnumWindows yields
    /// top-level windows in Z-order (front to back), so the first match is the window the overlay
    /// is floating over.
    /// </summary>
    private static nint FindUnderlyingWindow()
    {
        var ownPid = (uint)Environment.ProcessId;
        var found = nint.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidateWindow(hwnd, ownPid))
                return true; // keep enumerating

            found = hwnd;
            return false; // stop at the first (top-most) match
        }, nint.Zero);

        return found;
    }

    private static bool IsCandidateWindow(nint hwnd, uint ownPid)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;

        // Skip Floaty's own windows (overlay + settings) without needing their handles.
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == ownPid)
            return false;

        // Skip DWM-cloaked windows (e.g. background virtual-desktop / UWP suspended windows).
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        // Skip non-interactive helper surfaces: tool windows and no-activate overlays such as the
        // touch keyboard / "Shell Handwriting Canvas" (TabTip), which otherwise sit topmost and
        // produce an empty capture.
        var exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 || (exStyle & WS_EX_NOACTIVATE) != 0)
            return false;

        // Must have a real, non-empty client rect.
        if (!GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return false;

        // Skip the desktop / shell surfaces.
        var cls = GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Button")
            return false;

        // Require a title so we land on a genuine app window rather than invisible helpers.
        return GetWindowText(hwnd).Length > 0;
    }

    // --- Screenshot --------------------------------------------------------------------------

    private static void SaveWindowImage(nint hwnd, string path, int maxWidth = 0)
    {
        GetWindowRect(hwnd, out var rect);
        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT renders DirectComposition / modern windows correctly.
                PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        // PrintWindow fills RGB but commonly leaves the alpha channel at 0, which saves as a fully
        // transparent ("empty") PNG. Force every pixel opaque so the real content shows.
        ForceOpaque(bmp);

        if (maxWidth > 0 && bmp.Width > maxWidth)
        {
            var height = Math.Max(1, (int)Math.Round(bmp.Height * (maxWidth / (double)bmp.Width)));
            using var scaled = new Bitmap(bmp, maxWidth, height);
            scaled.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void ForceOpaque(Bitmap bmp)
    {
        var bounds = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var length = Math.Abs(data.Stride) * data.Height;
            var buffer = new byte[length];
            Marshal.Copy(data.Scan0, buffer, 0, length);

            // BGRA layout: the alpha byte is every 4th byte.
            for (var i = 3; i < length; i += 4)
                buffer[i] = 0xFF;

            Marshal.Copy(buffer, 0, data.Scan0, length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // --- Accessibility text ------------------------------------------------------------------

    /// <summary>Writes the capture's accessibility text to <paramref name="path"/> and returns the body text.</summary>
    private static string SaveWindowText(nint hwnd, string title, string path)
    {
        var body = ExtractAccessibilityText(hwnd);

        var sb = new StringBuilder();
        GetWindowRect(hwnd, out var rect);

        sb.AppendLine("# Floaty capture");
        sb.AppendLine($"Title:   {title}");
        sb.AppendLine($"Process: {GetProcessName(hwnd)}");
        sb.AppendLine($"Time:    {DateTime.Now:O}");
        sb.AppendLine($"Bounds:  {rect.Left},{rect.Top} {rect.Width}x{rect.Height}");
        sb.AppendLine("----");
        sb.Append(body);

        File.WriteAllText(path, sb.ToString());
        return body;
    }

    private static string ExtractAccessibilityText(nint hwnd)
    {
        try
        {
            IUIAutomation automation = new CUIAutomation();
            var root = automation.ElementFromHandle(hwnd);
            if (root is null)
                return string.Empty;

            var walker = automation.ControlViewWalker;
            var sb = new StringBuilder();
            var lastLine = string.Empty;
            var count = 0;

            Walk(walker, root, sb, ref count, ref lastLine);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(accessibility content unavailable: {ex.Message})";
        }
    }

    private static void Walk(
        IUIAutomationTreeWalker walker,
        IUIAutomationElement element,
        StringBuilder sb,
        ref int count,
        ref string lastLine)
    {
        if (count >= MaxElements)
            return;
        count++;

        // Prefer the element's value (editable/text controls), else its name (labels, buttons, etc.).
        var text = GetPropertyString(element, UIA_ValueValuePropertyId);
        if (string.IsNullOrWhiteSpace(text))
            text = GetPropertyString(element, UIA_NamePropertyId);

        text = text?.Trim() ?? string.Empty;
        if (text.Length > 0 && text != lastLine)
        {
            sb.AppendLine(text);
            lastLine = text;
        }

        try
        {
            var child = walker.GetFirstChildElement(element);
            while (child is not null && count < MaxElements)
            {
                Walk(walker, child, sb, ref count, ref lastLine);
                child = walker.GetNextSiblingElement(child);
            }
        }
        catch
        {
            // A control can refuse traversal mid-walk; keep whatever we've gathered so far.
        }
    }

    private static string GetPropertyString(IUIAutomationElement element, int propertyId)
    {
        try
        {
            return element.GetCurrentPropertyValue(propertyId) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetProcessName(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string GetWindowText(nint hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // --- Win32 / DWM interop -----------------------------------------------------------------

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int DWMWA_CLOAKED = 14;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    // UI Automation property ids (uiautomationclient.h).
    private const int UIA_NamePropertyId = 30005;
    private const int UIA_ValueValuePropertyId = 30045;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
