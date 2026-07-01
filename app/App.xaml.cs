using Floaty.Services;

namespace Floaty;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        // Fire-and-forget: on installed builds, check GitHub for a newer release and download it
        // in the background. It never restarts on its own — the Updates settings tab surfaces a
        // "Restart & update" button once a download is pending.
        StartBackgroundUpdateCheck();
    }

    private void StartBackgroundUpdateCheck()
    {
        var updateService = _services.GetService<UpdateService>();
        if (updateService is null || !updateService.IsSupported)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await updateService.AutoUpdateAsync();
            }
            catch
            {
                // Startup update checks are best-effort and must never crash the app.
            }
        });
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
