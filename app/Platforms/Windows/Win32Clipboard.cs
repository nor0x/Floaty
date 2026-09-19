using System.Runtime.InteropServices;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Plain-text clipboard access through raw Win32. Thread-agnostic, unlike the WinRT and Avalonia
/// wrappers, which is why both the summon hotkey's selection grab and the chat's clipboard tools use it
/// from whatever thread they happen to be on. Never throws.
/// </summary>
internal static class Win32Clipboard
{
    // Another process can hold the clipboard open for a moment (clipboard managers, RDP); a few short
    // retries ride that out without stalling the caller noticeably.
    private const int OpenAttempts = 5;
    private const int OpenRetryMs = 10;

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>The clipboard's text, or null when it holds none or cannot be opened.</summary>
    public static string? ReadText()
    {
        if (!TryOpen())
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

    /// <summary>Replaces the clipboard with <paramref name="text"/>. False when it could not be written.</summary>
    public static bool WriteText(string text)
    {
        if (!TryOpen())
            return false;

        var block = nint.Zero;
        try
        {
            EmptyClipboard();

            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            block = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (block == nint.Zero)
                return false;

            var ptr = GlobalLock(block);
            if (ptr == nint.Zero)
                return false;

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
            if (SetClipboardData(CF_UNICODETEXT, block) == nint.Zero)
                return false;

            block = nint.Zero;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (block != nint.Zero)
                GlobalFree(block);
            CloseClipboard();
        }
    }

    private static bool TryOpen()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(nint.Zero))
                return true;
            Thread.Sleep(OpenRetryMs);
        }

        return false;
    }

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

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint hMem);
}
