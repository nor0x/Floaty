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
		// Turns the configured providers + role assignments into IChatClient/IEmbeddingGenerator.
		// Everything that talks to a model goes through here rather than building its own client.
		builder.Services.AddSingleton<AiClientFactory>();
		builder.Services.AddSingleton<IChatService, ChatService>();

		// Capture memory: embeddings persisted to the local LiteGraph vector store (~/.floaty/floaty.db).
		builder.Services.AddSingleton<IMemoryService, MemoryService>();

		// Dropped files: the size caps, classification and fallbacks are cross-platform; the document
		// text extractor behind it is platform-conditional (registered in the #if blocks below).
		builder.Services.AddSingleton<IFileIngestService, FileIngestService>();

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
		builder.Services.AddTransient<ChatPanelView>();
		builder.Services.AddTransient<SettingsPage>();

#if WINDOWS
		builder.Services.AddSingleton<Floaty.Platforms.Windows.NativeWindowBinder>();
		builder.Services.AddSingleton<IOverlayWindowController, Floaty.Platforms.Windows.WindowsOverlayWindowController>();
		builder.Services.AddSingleton<IChatWindowController, Floaty.Platforms.Windows.WindowsChatWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, Floaty.Platforms.Windows.WindowsScreenCaptureService>();
		// Automatic screen history: captures the foreground window into memory on window/tab switches.
		builder.Services.AddSingleton<IScreenHistoryService, Floaty.Platforms.Windows.WindowsScreenHistoryService>();
		// Voice input: NAudio mic capture + sherpa-onnx local speech-to-text.
		builder.Services.AddSingleton<IAudioCaptureService, Floaty.Platforms.Windows.WindowsAudioCaptureService>();
		builder.Services.AddSingleton<IVoiceInputService, Floaty.Platforms.Windows.WindowsVoiceInputService>();
		// Autostart on sign-in: mirrors config.AutostartMode into the HKCU Run registry key.
		builder.Services.AddSingleton<IAutostartService, Floaty.Platforms.Windows.WindowsAutostartService>();
		// Text out of dropped documents (PDF/Office/…) via the Xberg native runtime.
		builder.Services.AddSingleton<ITextExtractionService, Floaty.Platforms.Windows.WindowsTextExtractionService>();
		// The selection in whatever app was in front when the summon hotkey fired.
		builder.Services.AddSingleton<ISelectionCaptureService, Floaty.Platforms.Windows.WindowsSelectionCaptureService>();
		// Capture shutter / assistant-reply sounds, played through NAudio (Settings → Sounds).
		builder.Services.AddSingleton<ISoundService, Floaty.Platforms.Windows.WindowsSoundService>();
		// On-device embedding models (ONNX Runtime), so memory and screen history can run without a cloud key.
		builder.Services.AddSingleton<ILocalEmbeddingFactory, Floaty.Platforms.Windows.WindowsLocalEmbeddingFactory>();
#elif MACCATALYST
		builder.Services.AddSingleton<IOverlayWindowController, Floaty.Platforms.MacCatalyst.MacOverlayWindowController>();
		builder.Services.AddSingleton<IChatWindowController, NullFloatingWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, NullScreenCaptureService>();
		builder.Services.AddSingleton<IScreenHistoryService, NullScreenHistoryService>();
		builder.Services.AddSingleton<IAudioCaptureService, NullAudioCaptureService>();
		builder.Services.AddSingleton<IVoiceInputService, NullVoiceInputService>();
		builder.Services.AddSingleton<IAutostartService, NullAutostartService>();
		builder.Services.AddSingleton<ITextExtractionService, NullTextExtractionService>();
		builder.Services.AddSingleton<ISelectionCaptureService, NullSelectionCaptureService>();
		builder.Services.AddSingleton<ISoundService, NullSoundService>();
		builder.Services.AddSingleton<ILocalEmbeddingFactory, NullLocalEmbeddingFactory>();
#else
		builder.Services.AddSingleton<IOverlayWindowController, NullOverlayWindowController>();
		builder.Services.AddSingleton<IChatWindowController, NullFloatingWindowController>();
		builder.Services.AddSingleton<IScreenCaptureService, NullScreenCaptureService>();
		builder.Services.AddSingleton<IScreenHistoryService, NullScreenHistoryService>();
		builder.Services.AddSingleton<IAudioCaptureService, NullAudioCaptureService>();
		builder.Services.AddSingleton<IVoiceInputService, NullVoiceInputService>();
		builder.Services.AddSingleton<IAutostartService, NullAutostartService>();
		builder.Services.AddSingleton<ITextExtractionService, NullTextExtractionService>();
		builder.Services.AddSingleton<ISelectionCaptureService, NullSelectionCaptureService>();
		builder.Services.AddSingleton<ISoundService, NullSoundService>();
		builder.Services.AddSingleton<ILocalEmbeddingFactory, NullLocalEmbeddingFactory>();
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
				if (IPlatformApplication.Current?.Services.GetService<Floaty.Platforms.Windows.NativeWindowBinder>()
					is { } binder
					&& binder.TryConsume(nativeWindow))
				{
					return;
				}

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


}
