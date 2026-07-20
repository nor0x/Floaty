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

		// Persisted chat threads (~/.floaty/conversations), switchable via the /chats slash command.
		builder.Services.AddSingleton<ConversationService>();

		// Agent skills (SKILL.md) discovered from disk, invokable via /skill slash commands.
		builder.Services.AddSingleton<SkillService>();

		// In-app auto-update (Velopack) checking the GitHub Releases of nor0x/Floaty.
		builder.Services.AddSingleton<UpdateService>();

		// Local speech-to-text: the transcribe.cpp native runtime (~/.floaty/native) and the
		// model downloads (~/.floaty/models) for the Voice input settings.
		builder.Services.AddSingleton<NativeRuntimeService>();
		builder.Services.AddSingleton<ModelDownloadService>();

		// The floating overlay page (native MAUI UI) and the settings window.
		builder.Services.AddTransient<OverlayPage>();
		builder.Services.AddTransient<SettingsPage>();

#if WINDOWS
		builder.Services.AddSingleton<IOverlayWindowController, Floaty.Platforms.Windows.WindowsOverlayWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, Floaty.Platforms.Windows.WindowsScreenCaptureService>();
		// Automatic screen history: captures the foreground window into memory on window/tab switches.
		builder.Services.AddSingleton<IScreenHistoryService, Floaty.Platforms.Windows.WindowsScreenHistoryService>();
		// Voice input: NAudio mic capture + sherpa-onnx local speech-to-text.
		builder.Services.AddSingleton<IAudioCaptureService, Floaty.Platforms.Windows.WindowsAudioCaptureService>();
		builder.Services.AddSingleton<IVoiceInputService, Floaty.Platforms.Windows.WindowsVoiceInputService>();
		// Autostart on sign-in: mirrors config.AutostartMode into the HKCU Run registry key.
		builder.Services.AddSingleton<IAutostartService, Floaty.Platforms.Windows.WindowsAutostartService>();
#elif MACCATALYST
		builder.Services.AddSingleton<IOverlayWindowController, Floaty.Platforms.MacCatalyst.MacOverlayWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, NullScreenCaptureService>();
		builder.Services.AddSingleton<IScreenHistoryService, NullScreenHistoryService>();
		builder.Services.AddSingleton<IAudioCaptureService, NullAudioCaptureService>();
		builder.Services.AddSingleton<IVoiceInputService, NullVoiceInputService>();
		builder.Services.AddSingleton<IAutostartService, NullAutostartService>();
#else
		builder.Services.AddSingleton<IOverlayWindowController, NullOverlayWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, NullScreenCaptureService>();
		builder.Services.AddSingleton<IScreenHistoryService, NullScreenHistoryService>();
		builder.Services.AddSingleton<IAudioCaptureService, NullAudioCaptureService>();
		builder.Services.AddSingleton<IVoiceInputService, NullVoiceInputService>();
		builder.Services.AddSingleton<IAutostartService, NullAutostartService>();
#endif

		ConfigureOverlayWindow(builder);
		ConfigureEditorChrome();

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

				// Screen history hooks live on the overlay window's dispatcher (it pumps messages).
				// Initialize only takes effect on the first window (the overlay); teardown is tied to
				// that window alone so closing Settings doesn't unhook a running history.
				if (IPlatformApplication.Current?.Services.GetService<IScreenHistoryService>()
						is Floaty.Platforms.Windows.WindowsScreenHistoryService screenHistory
					&& screenHistory.Initialize(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()))
				{
					nativeWindow.Closed += (_, _) => screenHistory.Shutdown();
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

	// Message bubbles use a read-only Editor instead of a Label so text can be drag-selected and
	// copied (see OverlayPage.xaml's SelectableBubbleEditor style). Editors render with native
	// textbox chrome by default; strip it here so bubbles still look like plain labels.
	private static void ConfigureEditorChrome()
	{
#if WINDOWS
		Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("NoChrome", (handler, _) =>
		{
			var textBox = handler.PlatformView;
			textBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
			textBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
			textBox.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

			// WinUI's default TextBox style enforces a ~32px MinHeight, which made single-line
			// bubbles noticeably taller than the old Label. AutoSize already sizes the box to its
			// text, so the theme minimum just adds dead space here.
			textBox.MinHeight = 0;

			// WinUI's default TextBox style swaps in these theme brushes on pointer-over/focus
			// regardless of the Background/BorderThickness set above, so override them too.
			var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
			foreach (var key in new[]
			{
				"TextControlBackground", "TextControlBackgroundPointerOver",
				"TextControlBackgroundFocused", "TextControlBackgroundDisabled",
				"TextControlBorderBrush", "TextControlBorderBrushPointerOver",
				"TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
			})
			{
				textBox.Resources[key] = transparent;
			}
		});
#elif MACCATALYST
		Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("NoChrome", (handler, _) =>
		{
			var textView = handler.PlatformView;
			textView.BackgroundColor = UIKit.UIColor.Clear;
			textView.TextContainerInset = UIKit.UIEdgeInsets.Zero;
			// AutoSize handles growth to fit content, so the Editor never needs its own scroll region.
			textView.ScrollEnabled = false;
		});
#endif
	}
}
