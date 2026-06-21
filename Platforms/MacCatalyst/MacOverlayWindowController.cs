using CoreGraphics;
using Floaty.Services;
using Foundation;
using ObjCRuntime;
using UIKit;

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

    public void Resize(double widthDip, double heightDip)
    {
        if (_nsWindow is null)
            return;

        if (_nsWindow.ValueForKey(new NSString("frame")) is not NSValue frameValue)
            return;

        var frame = frameValue.CGRectValue;

        // AppKit origin is bottom-left, so keep the bottom edge (frame.Y) fixed and grow upward,
        // and keep the horizontal center fixed.
        var centerX = frame.X + (frame.Width / 2);
        var resized = new CGRect(centerX - (widthDip / 2), frame.Y, widthDip, heightDip);
        _nsWindow.SetValueForKey(NSValue.FromCGRect(resized), new NSString("frame"));
    }
}
