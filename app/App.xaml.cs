namespace Floaty;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var overlay = _services.GetRequiredService<OverlayPage>();

        // Small overlay parked toward the bottom-right of the primary display.
        const double width = 200;
        const double height = 250;
        var display = DeviceDisplay.Current.MainDisplayInfo;
        var x = (display.Width / display.Density) - width - 40;
        var y = (display.Height / display.Density) - height - 80;

        return new Window(overlay)
        {
            Title = "Floaty",
            Width = width,
            Height = height,
            X = x > 0 ? x : 100,
            Y = y > 0 ? y : 100,
        };
    }
}
