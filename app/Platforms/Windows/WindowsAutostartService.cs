using Floaty.Services;
using Microsoft.Win32;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Windows autostart via the per-user Run registry key (the app is unpackaged, so the MSIX
/// StartupTask API is unavailable). The value points at the current executable and carries
/// <c>--minimized</c> when the overlay should start hidden.
/// </summary>
public sealed class WindowsAutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Floaty";

    private readonly SettingsService _settings;

    public WindowsAutostartService(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) => Sync();
    }

    public bool IsSupported => true;

    public void SyncOnStartup() => Sync();

    /// <summary>Deletes the Run value outright; called from Velopack's uninstall hook.</summary>
    public static void RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Uninstall cleanup is best-effort.
        }
    }

    private void Sync()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
                return;

            var mode = _settings.Current.AutostartMode;
            if (mode == AutostartMode.Disabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;

            var value = mode == AutostartMode.Minimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"";
            if (key.GetValue(ValueName) as string != value)
                key.SetValue(ValueName, value);
        }
        catch
        {
            // Registry problems must never crash the app; the setting simply won't stick.
        }
    }
}
