using Avalonia.Controls;
using Floaty.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Floaty.Views.Settings;

/// <summary>
/// The settings window. Replaces the MAUI <c>SettingsPage</c>, its <c>BlazorWebView</c> and the
/// <c>https://localfiles/…</c> resource interceptor that existed only to feed ring previews into it.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Avalonia's launcher hangs off a TopLevel, which the view-model has no business knowing.
        viewModel.ExternalOpener = async uri =>
        {
            if (Launcher is { } launcher)
                await launcher.LaunchUriAsync(uri);
        };

        // Closing without saving must not leave the overlay wearing an uncommitted ring size or
        // accent, so the view-model reverts its live previews.
        Closed += (_, _) =>
        {
            _viewModel.Dispose();
            _open = null;
        };
    }

    /// <summary>
    /// Opens the settings window, focusing the existing one if it is already up. Shared by the ring's
    /// context menu and the <c>/settings</c> slash command, which live in different views.
    /// </summary>
    public static void OpenWindow(IServiceProvider services)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        var viewModel = services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow(viewModel);
        _open = window;
        window.Show();

        // Deliberately after Show(): the initial load reads config and probes disk, and the window
        // should already be on screen while that happens.
        _ = viewModel.InitializeAsync();
    }
}
