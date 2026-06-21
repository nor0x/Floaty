namespace Floaty;

/// <summary>
/// Native MAUI page that hosts the Blazor <c>Settings</c> component. Opened in its own window
/// from the overlay's ⚙ button; keeps normal window chrome (see WindowsOverlayWindowController).
/// </summary>
public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
    }
}
