using CoreGraphics;
using Floaty.Services;
using Foundation;
using ObjCRuntime;
using UIKit;
using System.Runtime.InteropServices;

namespace Floaty.Platforms.MacCatalyst;

/// <summary>
/// Mac Catalyst implementation of the overlay window controller.
///
/// Catalyst cannot reference AppKit directly, so we reach the backing <c>NSWindow</c> through the
/// semi-private <c>nsWindow</c> KVC bridge on <see cref="UIWindow"/> and drive it via KVC + the
/// Objective-C runtime. This relies on a non-public key and should be re-verified on a real Mac and
/// after MAUI / macOS upgrades.
/// </summary>
public sealed class MacOverlayWindowController : IOverlayWindowController
{
    // AppKit constants (not available as symbols in Catalyst).
    private const long NSWindowStyleMaskBorderless = 0;
    private const long NSFloatingWindowLevel = 3;

    private NSObject? _nsWindow;

    /// <summary>
    /// Resolves the backing NSWindow for the given Catalyst UIWindow and applies the overlay styling.
    /// Safe to call repeatedly; only the first successful resolution takes effect.
    /// </summary>
    public void Initialize(UIWindow uiWindow)
    {
        if (_nsWindow is not null)
            return;

        _nsWindow = uiWindow.ValueForKey(new NSString("nsWindow"));
        if (_nsWindow is null)
            return;

        // Borderless + transparent + no shadow.
        _nsWindow.SetValueForKey(NSNumber.FromBoolean(false), new NSString("opaque"));
        _nsWindow.SetValueForKey(NSNumber.FromBoolean(false), new NSString("hasShadow"));
        _nsWindow.SetValueForKey(NSNumber.FromInt64(NSWindowStyleMaskBorderless), new NSString("styleMask"));

        // Always-on-top (floating window level).
        _nsWindow.SetValueForKey(NSNumber.FromInt64(NSFloatingWindowLevel), new NSString("level"));

        // Clear background — grab +[NSColor clearColor] via the ObjC runtime and assign it.
        var clearColor = Runtime.GetNSObject(
            Messaging.IntPtr_objc_msgSend(
                Class.GetHandle("NSColor"),
                Selector.GetHandle("clearColor")));
        if (clearColor is not null)
            _nsWindow.SetValueForKey(clearColor, new NSString("backgroundColor"));
    }

    public void MoveBy(double dxDip, double dyDip)
    {
        if (_nsWindow is null)
            return;

        if (_nsWindow.ValueForKey(new NSString("frame")) is not NSValue frameValue)
            return;

        var frame = frameValue.CGRectValue;
        // AppKit's origin is bottom-left with +y pointing up, so a downward MAUI drag (positive dy)
        // decreases the AppKit y.
        var moved = new CGRect(frame.X + dxDip, frame.Y - dyDip, frame.Width, frame.Height);
        _nsWindow.SetValueForKey(NSValue.FromCGRect(moved), new NSString("frame"));
    }

    public void Resize(double widthDip, double heightDip, bool anchorLeft = false)
    {
        if (_nsWindow is null)
            return;

        if (_nsWindow.ValueForKey(new NSString("frame")) is not NSValue frameValue)
            return;

        var frame = frameValue.CGRectValue;

        // AppKit origin is bottom-left, so keep the bottom edge (frame.Y) fixed and grow upward.
        // Horizontally anchor the left edge (frame.X) or the center depending on the caller.
        var newX = anchorLeft ? frame.X : frame.X + (frame.Width / 2) - (widthDip / 2);
        var resized = new CGRect(newX, frame.Y, widthDip, heightDip);
        _nsWindow.SetValueForKey(NSValue.FromCGRect(resized), new NSString("frame"));
    }

    // The global summon hotkey is not yet implemented on macOS; this event never fires there.
#pragma warning disable CS0067
    public event Action<int, int>? SummonRequested;
#pragma warning restore CS0067

    public (int X, int Y) GetPosition() => (0, 0);

    public (int Width, int Height) GetSize() => (0, 0);

    public void MoveTo(int x, int y)
    {
        // Not implemented on macOS yet (summon is Windows-only for now).
    }

    public void Activate()
    {
        // Not implemented on macOS yet.
    }

    public void Hide()
    {
        if (_nsWindow is null)
            return;

        Messaging.void_objc_msgSend_IntPtr(
            _nsWindow.Handle,
            Selector.GetHandle("orderOut:"),
            IntPtr.Zero);
    }

    public void FloatToTaskbarAndHide()
    {
        if (_nsWindow is null)
            return;

        if (_nsWindow.ValueForKey(new NSString("frame")) is not NSValue frameValue)
        {
            Hide();
            return;
        }

        var frame = frameValue.CGRectValue;
        var screen = UIScreen.MainScreen.Bounds;
        const double margin = 12;

        // AppKit frame origin is bottom-left. Top-right placement therefore uses a high y value.
        var targetX = screen.Width - frame.Width - margin;
        var targetY = screen.Height - frame.Height - margin;
        _nsWindow.SetValueForKey(
            NSValue.FromCGRect(new CGRect(targetX, targetY, frame.Width, frame.Height)),
            new NSString("frame"));

        Hide();
    }
}
