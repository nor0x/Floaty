using Floaty.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace Floaty;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// Local config (~/.floaty/config.json) and the AI chat service built on Microsoft.Extensions.AI.
		builder.Services.AddSingleton<SettingsService>();
		builder.Services.AddSingleton<IChatService, ChatService>();

		// The floating overlay page (native MAUI UI) and the settings window.
		builder.Services.AddTransient<OverlayPage>();
		builder.Services.AddTransient<SettingsPage>();

#if WINDOWS
		builder.Services.AddSingleton<IOverlayWindowController, Floaty.Platforms.Windows.WindowsOverlayWindowController>();
#elif MACCATALYST
		builder.Services.AddSingleton<IOverlayWindowController, Floaty.Platforms.MacCatalyst.MacOverlayWindowController>();
#else
		builder.Services.AddSingleton<IOverlayWindowController, NullOverlayWindowController>();
#endif

		ConfigureOverlayWindow(builder);

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	private static void ConfigureOverlayWindow(MauiAppBuilder builder)
	{
		builder.ConfigureLifecycleEvents(events =>
		{
#if WINDOWS
			events.AddWindows(windows => windows.OnWindowCreated(nativeWindow =>
			{
				if (IPlatformApplication.Current?.Services.GetService<IOverlayWindowController>()
					is Floaty.Platforms.Windows.WindowsOverlayWindowController controller)
				{
					controller.Initialize(nativeWindow);
				}
			}));
#elif MACCATALYST
			events.AddiOS(ios => ios.OnActivated(app =>
			{
				var uiWindow = app.KeyWindow ?? app.Windows.FirstOrDefault();
				if (uiWindow is not null
					&& IPlatformApplication.Current?.Services.GetService<IOverlayWindowController>()
						is Floaty.Platforms.MacCatalyst.MacOverlayWindowController controller)
				{
					controller.Initialize(uiWindow);
				}
			}));
#endif
		});
	}
}
