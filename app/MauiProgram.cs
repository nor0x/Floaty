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
				fonts.AddFont("tabler-icons.ttf", "TablerIconsLine");
				fonts.AddFont("tabler-icons-filled.ttf", "TablerIconsFilled");
			});

		builder.Services.AddMauiBlazorWebView();

		// Local config (~/.floaty/config.json) and the AI chat service built on Microsoft.Extensions.AI.
		builder.Services.AddSingleton<SettingsService>();
		builder.Services.AddSingleton<IChatService, ChatService>();

		// Capture memory: embeddings persisted to the local LiteGraph vector store (~/.floaty/litegraph.db).
		builder.Services.AddSingleton<IMemoryService, MemoryService>();

		// MCP servers: connected on demand, tools exposed to chat via /server slash commands.
		builder.Services.AddSingleton<IMcpService, McpService>();

		// The floating overlay page (native MAUI UI) and the settings window.
		builder.Services.AddTransient<OverlayPage>();
		builder.Services.AddTransient<SettingsPage>();

#if WINDOWS
		builder.Services.AddSingleton<IOverlayWindowController, Floaty.Platforms.Windows.WindowsOverlayWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, Floaty.Platforms.Windows.WindowsScreenCaptureService>();
#elif MACCATALYST
		builder.Services.AddSingleton<IOverlayWindowController, Floaty.Platforms.MacCatalyst.MacOverlayWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, NullScreenCaptureService>();
#else
		builder.Services.AddSingleton<IOverlayWindowController, NullOverlayWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, NullScreenCaptureService>();
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
