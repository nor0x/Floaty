using System.Runtime.InteropServices;
using System.Text;
using Floaty.Services;
using Interop.UIAutomationClient;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Windows implementation of <see cref="ISelectionCaptureService"/>. Two rungs, tried in order:
/// <list type="number">
///   <item><description>
///     UI Automation's <c>TextPattern</c> on the focused element — instant and completely free of side
///     effects, but only apps that implement the pattern answer (WinUI/WPF/WinForms/Office, UWP).
///   </description></item>
///   <item><description>
///     A synthesized Ctrl+C, then a clipboard read with the previous text put back. Chromium, Electron
///     and terminals expose nothing usable through rung 1, which is most of where people select text,
///     so the clipboard round-trip is the difference between the feature working and not.
///   </description></item>
/// </list>
/// Everything runs off the UI thread and every rung is time-boxed: the summon animation waits on this,
/// so a wedged app must cost a bounded delay, never a hang.
/// </summary>
public sealed class WindowsSelectionCaptureService : ISelectionCaptureService
{
    // Same order of magnitude as ChatPanelView's per-attachment cap — anything past this is trimmed
    // again at send time anyway, and there's no point marshalling a novel across a COM boundary.
    private const int MaxSelectionChars = 12_000;

    // Per-rung budgets. UI Automation is a cross-process COM call that usually returns in tens of
    // milliseconds but can stall on a busy app; the clipboard rung has to wait for the target to react
    // to the keystrokes. Together they bound the summon delay at roughly a quarter second.
    private const int AutomationBudgetMs = 80;
    private const int ClipboardBudgetMs = 220;

    // How long to wait for the target app to answer Ctrl+C, and how often to check.
    private const int ClipboardPollMs = 160;
    private const int ClipboardPollIntervalMs = 10;

    // Beat between releasing the physically-held hotkey modifiers and sending Ctrl+C, so the target
    // processes the key-ups first and doesn't see the copy as Ctrl+Alt+C.
    private const int ModifierReleaseSettleMs = 15;

    // The clipboard is a shared, singly-owned resource: another app may hold it open for a moment.
    private const int ClipboardOpenAttempts = 5;
    private const int ClipboardOpenRetryMs = 10;

    public async Task<SelectedText?> TryCaptureAsync(
        nint foregroundHwnd,
        CancellationToken cancellationToken = default)
    {
        if (foregroundHwnd == nint.Zero)
            return null;

        var text = await RunBoundedAsync(TryReadViaAutomation, AutomationBudgetMs, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            text = await RunBoundedAsync(
                () => TryReadViaClipboard(foregroundHwnd),
                ClipboardBudgetMs,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text!.Length > MaxSelectionChars)
            text = text[..MaxSelectionChars];

        var title = GetWindowTitle(foregroundHwnd);
        return new SelectedText(text, string.IsNullOrWhiteSpace(title) ? "another window" : title);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool and gives up on it after
    /// <paramref name="budgetMs"/>. An overrun task is abandoned rather than cancelled — the native
    /// calls underneath aren't cancellable, and letting a background thread finish into the void is
    /// preferable to blocking the summon on it.
    /// </summary>
    private static async Task<string?> RunBoundedAsync(
        Func<string?> work,
        int budgetMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = Task.Run(work, cancellationToken);
            var finished = await Task.WhenAny(task, Task.Delay(budgetMs, cancellationToken))
                .ConfigureAwait(false);
            return finished == task ? await task.ConfigureAwait(false) : null;
        }
        catch
        {
            // Cancellation or a rung that threw: both mean "no selection", never an error to surface.
            return null;
        }
    }

    // --- Rung 1: UI Automation TextPattern ---

    private const int UIA_TextPatternId = 10014;

    private static string? TryReadViaAutomation()
    {
        try
        {
            IUIAutomation automation = new CUIAutomation();

            // The focused element is global, which is exactly what's wanted: the caret is wherever the
            // user was typing, regardless of which top-level window owns it.
            var focused = automation.GetFocusedElement();
            if (focused is null)
                return null;

            if (focused.GetCurrentPattern(UIA_TextPatternId) is not IUIAutomationTextPattern pattern)
                return null;

            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.Length == 0)
                return null;

            var sb = new StringBuilder();
            for (var i = 0; i < ranges.Length && sb.Length < MaxSelectionChars; i++)
            {
                // A caret with nothing selected still yields one degenerate range whose text is empty,
                // which falls through to the clipboard rung — correct, since that's "no selection".
                var chunk = ranges.GetElement(i)?.GetText(MaxSelectionChars);
                if (!string.IsNullOrEmpty(chunk))
                    sb.Append(chunk);
            }

            return sb.ToString();
        }
        catch
        {
            // Any COM failure (app closed mid-call, pattern refused, no accessibility) is just "nothing".
            return null;
        }
    }

    // --- Rung 2: synthesized Ctrl+C ---

    /// <summary>
    /// Copies the selection out of <paramref name="foregroundHwnd"/> with a synthetic Ctrl+C and puts
    /// the previous clipboard text back afterwards. The restore is text-only and best-effort: if the
    /// clipboard held an image or a private format, that content is lost to the copy and cannot be
    /// reinstated. Nothing is touched at all when the target ignores the keystroke, which is the
    /// no-selection case.
    /// </summary>
    private static string? TryReadViaClipboard(nint foregroundHwnd)
    {
        // Focus may have moved on between the hotkey firing and this rung. Copying from whatever is in
        // front now would attach something the user never selected, so bail instead.
        if (GetForegroundWindow() != foregroundHwnd)
            return null;

        var previousText = ReadClipboardText();
        var previousSequence = GetClipboardSequenceNumber();

        SendCopy();

        string? copied = null;
        for (var waited = 0; waited < ClipboardPollMs; waited += ClipboardPollIntervalMs)
        {
            Thread.Sleep(ClipboardPollIntervalMs);
            if (GetClipboardSequenceNumber() == previousSequence)
                continue;

            copied = ReadClipboardText();
            break;
        }

        // The sequence number never moved: the app had nothing to copy, and the clipboard is untouched.
        if (copied is null)
            return null;

        if (previousText is not null)
            WriteClipboardText(previousText);

        return copied;
    }

    /// <summary>
    /// Types Ctrl+C into the foreground app, working around the fact that the user is still physically
    /// holding the hotkey down. Two details matter and both are load-bearing:
    /// <list type="bullet">
    ///   <item><description>
    ///     Alt is down (the hotkey is Alt+F), so a naive Ctrl+C arrives as Ctrl+Alt+C and does nothing.
    ///     Every held modifier gets a key-up first.
    ///   </description></item>
    ///   <item><description>
    ///     Ctrl goes down <em>before</em> those key-ups. The app already saw Alt press; releasing it
    ///     with nothing in between reads as a bare Alt tap, which focuses the menu bar in Chrome and
    ///     most Win32 apps and swallows the copy. Holding Ctrl across the release breaks that gesture.
    ///   </description></item>
    /// </list>
    /// </summary>
    private static void SendCopy()
    {
        // Ctrl is excluded on purpose: it needs to stay down through the whole sequence.
        ReadOnlySpan<ushort> maybeHeld =
        [
            VK_LMENU, VK_RMENU, VK_LSHIFT, VK_RSHIFT, VK_LWIN, VK_RWIN, VK_F,
        ];

        var opening = new List<INPUT>(maybeHeld.Length + 1) { KeyInput(VK_CONTROL, up: false) };
        foreach (var key in maybeHeld)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                opening.Add(KeyInput(key, up: true));
        }

        Send(opening.ToArray());

        // Let the target process the release before the copy lands on it.
        Thread.Sleep(ModifierReleaseSettleMs);

        Send(
        [
            KeyInput(VK_C, up: false),
            KeyInput(VK_C, up: true),
            KeyInput(VK_CONTROL, up: true),
        ]);
    }

    private static void Send(INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

    // --- Clipboard (raw Win32: thread-agnostic, unlike the WinRT and MAUI wrappers) ---

    private static string? ReadClipboardText()
    {
        if (!TryOpenClipboard())
            return null;

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == nint.Zero)
                return null;

            var ptr = GlobalLock(handle);
            if (ptr == nint.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void WriteClipboardText(string text)
    {
        if (!TryOpenClipboard())
            return;

        var block = nint.Zero;
        try
        {
            EmptyClipboard();

            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            block = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (block == nint.Zero)
                return;

            var ptr = GlobalLock(block);
            if (ptr == nint.Zero)
                return;

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(block);
            }

            // Ownership of the block transfers to the clipboard only if this succeeds.
            if (SetClipboardData(CF_UNICODETEXT, block) != nint.Zero)
                block = nint.Zero;
        }
        catch
        {
            // A failed restore is not worth surfacing; the copied text stays on the clipboard.
        }
        finally
        {
            if (block != nint.Zero)
                GlobalFree(block);
            CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < ClipboardOpenAttempts; attempt++)
        {
            if (OpenClipboard(nint.Zero))
                return true;
            Thread.Sleep(ClipboardOpenRetryMs);
        }

        return false;
    }

    private static string GetWindowTitle(nint hwnd)
    {
        try
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return string.Empty;

            var buffer = new StringBuilder(length + 1);
            GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // --- Native ---

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;
    private const ushort VK_F = 0x46;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    // MOUSEINPUT is the largest member; it has to be part of the union or the marshalled INPUT comes
    // out too small and SendInput rejects the call outright.
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint hMem);
}
