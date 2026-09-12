using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Floaty.Services;
using Floaty.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Floaty;

/// <summary>
/// Application entry point and composition root. Replaces both the MAUI <c>App</c> and
/// <c>MauiProgram</c>: Avalonia has no builder of its own, so the service collection is built here
/// and exposed through <see cref="Services"/> for the few places that resolve lazily.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The composition root. Views resolve their dependencies through constructor injection; this is
    /// for the handful of places (tray menu, slash commands) that need a late lookup.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Re-align the OS autostart registration with the saved config and current exe path (updates
        // can move the install), and keep the singleton alive so later config saves propagate to the
        // registry via SettingsService.Changed.
        try
        {
            Services.GetService<IAutostartService>()?.SyncOnStartup();
        }
        catch
        {
            // Autostart sync is best-effort and must never crash startup.
        }

        // Fire-and-forget: on installed builds, check GitHub for a newer release and download it in
        // the background. It never restarts on its own - the Updates settings tab surfaces a
        // "Restart & update" button once a download is pending.
        StartBackgroundUpdateCheck();

        var overlay = Services.GetRequiredService<OverlayWindow>();
        desktop.MainWindow = overlay;

        ApplyAccentColor(Services.GetRequiredService<SettingsService>().Current.AccentColor);
        InstallTrayIcon(desktop, overlay);

        // Show before binding the controllers: TryGetPlatformHandle only yields an HWND once the
        // window exists, and this replaces MAUI's OnWindowCreated lifecycle hook.
        overlay.Show();

        var overlayController = Services.GetRequiredService<IOverlayWindowController>();
        if (overlayController is Platforms.Windows.AvaloniaOverlayWindowController avaloniaOverlay)
            avaloniaOverlay.Initialize(overlay);
        overlay.BindWindowController(overlayController);

#if WINDOWS
        // Screen-history hooks need a message-pumping thread, which the UI thread is. Teardown is
        // tied to the overlay window alone, so closing Settings never unhooks a running history.
        if (Services.GetRequiredService<IScreenHistoryService>()
                is Platforms.Windows.WindowsScreenHistoryService screenHistory
            && screenHistory.Initialize())
        {
            overlay.Closed += (_, _) => screenHistory.Shutdown();
        }
#endif

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Packaged ring images and built-in sounds, read through avares://. Registered before
        // SettingsService, which takes it.
        services.AddSingleton<IAppAssets, Platforms.AvaloniaAppAssets>();

        // Local config (~/.floaty/config.json) and the AI chat service built on Microsoft.Extensions.AI.
        services.AddSingleton<SettingsService>();
        // Turns the configured providers + role assignments into IChatClient/IEmbeddingGenerator.
        // Everything that talks to a model goes through here rather than building its own client.
        services.AddSingleton<AiClientFactory>();
        // Text-to-image through whichever provider holds the image role. Files land in ~/.floaty/generated.
        services.AddSingleton<IImageGenerationService, ImageGenerationService>();
        services.AddSingleton<IChatService, ChatService>();

        // The day's screen-history log (~/.floaty/captures/YYYY-MM-DD.md) and the ledger of lines
        // already written to it. Singleton because that ledger is the whole point: it spans captures.
        services.AddSingleton<CaptureDayLog>();

        // Capture memory: embeddings persisted to the local LiteGraph vector store (~/.floaty/floaty.db).
        services.AddSingleton<IMemoryService, MemoryService>();

        // Dropped files: the size caps, classification and fallbacks are cross-platform; the document
        // text extractor behind it is platform-conditional (registered in the #if blocks below).
        services.AddSingleton<IFileIngestService, FileIngestService>();

        // MCP servers: connected on demand, tools exposed to chat via /server slash commands.
        services.AddSingleton<IMcpService, McpService>();

        // Persisted chat threads (~/.floaty/conversations), switchable via the /chats slash command.
        services.AddSingleton<ConversationService>();

        // Agent skills (SKILL.md), shipped with the app and discovered from disk, invokable via
        // /skill slash commands.
        services.AddSingleton<SkillService>();

        // In-app auto-update (Velopack) checking the GitHub Releases of nor0x/Floaty.
        services.AddSingleton<UpdateService>();

        // Local speech-to-text: the transcribe.cpp native runtime (~/.floaty/native) and the
        // model downloads (~/.floaty/models) for the Voice input settings.
        services.AddSingleton<NativeRuntimeService>();
        services.AddSingleton<ModelDownloadService>();

        // The floating overlay window. Unlike MAUI this is the top-level Window itself, not a page
        // hosted in one, so there is no separate window/page pair to keep in sync.
        services.AddSingleton<OverlayWindow>();

        // The chat panel is transient: it is rebuilt whenever the chat placement changes, and the
        // standalone chat window gets its own instance.
        services.AddTransient<Views.Chat.ChatPanelView>();

        // Settings: a fresh view-model per window, so each visit starts from a clean clone
        // of the saved config.
        services.AddTransient<ViewModels.Settings.SettingsViewModel>();

#if WINDOWS
        services.AddSingleton<IScreenCaptureService, Platforms.Windows.WindowsScreenCaptureService>();
        // Voice input: NAudio mic capture + local speech-to-text.
        services.AddSingleton<IAudioCaptureService, Platforms.Windows.WindowsAudioCaptureService>();
        services.AddSingleton<IVoiceInputService, Platforms.Windows.WindowsVoiceInputService>();
        // Autostart on sign-in: mirrors config.AutostartMode into the HKCU Run registry key.
        services.AddSingleton<IAutostartService, Platforms.Windows.WindowsAutostartService>();
        // Text out of dropped documents (PDF/Office/…) via the Xberg native runtime.
        services.AddSingleton<ITextExtractionService, Platforms.Windows.WindowsTextExtractionService>();
        // The selection in whatever app was in front when the summon hotkey fired.
        services.AddSingleton<ISelectionCaptureService, Platforms.Windows.WindowsSelectionCaptureService>();
        // Capture shutter / assistant-reply sounds, played through NAudio (Settings → Sounds).
        services.AddSingleton<ISoundService, Platforms.Windows.WindowsSoundService>();
        // Native toasts behind the chat's notify / list_notifications / cancel_notification tools.
        // Scheduled ones are handed to Windows, so a reminder fires even after Floaty is quit.
        services.AddSingleton<INotificationService, Platforms.Windows.WindowsNotificationService>();
        // On-device embedding models (ONNX Runtime), so memory and screen history can run without a cloud key.
        services.AddSingleton<ILocalEmbeddingFactory, Platforms.Windows.WindowsLocalEmbeddingFactory>();

        // Borderless/transparent/always-on-top window behaviour plus the OS-level click-through that
        // Avalonia has no cross-platform answer for.
        services.AddSingleton<IOverlayWindowController, Platforms.Windows.AvaloniaOverlayWindowController>();
        services.AddSingleton<IChatWindowController, Platforms.Windows.AvaloniaChatWindowController>();
        // Automatic screen history: captures the foreground window into memory on window/tab switches.
        services.AddSingleton<IScreenHistoryService, Platforms.Windows.WindowsScreenHistoryService>();
#else
        services.AddSingleton<IOverlayWindowController, NullOverlayWindowController>();
        services.AddSingleton<IChatWindowController, NullFloatingWindowController>();
        services.AddSingleton<IScreenCaptureService, NullScreenCaptureService>();
        services.AddSingleton<IScreenHistoryService, NullScreenHistoryService>();
        services.AddSingleton<IAudioCaptureService, NullAudioCaptureService>();
        services.AddSingleton<IVoiceInputService, NullVoiceInputService>();
        services.AddSingleton<IAutostartService, NullAutostartService>();
        services.AddSingleton<ITextExtractionService, NullTextExtractionService>();
        services.AddSingleton<ISelectionCaptureService, NullSelectionCaptureService>();
        services.AddSingleton<ISoundService, NullSoundService>();
        services.AddSingleton<INotificationService, NullNotificationService>();
        services.AddSingleton<ILocalEmbeddingFactory, NullLocalEmbeddingFactory>();
#endif
    }

    /// <summary>
    /// Replaces MAUI's <c>WindowsTrayIcon</c> (215 lines of Shell_NotifyIcon interop) with Avalonia's
    /// own tray support. Left-click toggles the overlay; the menu offers the same Show/Hide + Quit.
    /// </summary>
    private void InstallTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, OverlayWindow overlay)
    {
        void ToggleOverlay()
        {
            if (overlay.IsVisible)
            {
                overlay.Hide();
            }
            else
            {
                overlay.Show();
                overlay.Activate();
            }
        }

        var showHide = new NativeMenuItem("Show / Hide Floaty");
        showHide.Click += (_, _) => ToggleOverlay();

        var quit = new NativeMenuItem("Quit Floaty");
        quit.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Floaty/Resources/AppIcon/floaty.ico"))),
            ToolTipText = "Floaty",
            IsVisible = true,
            Menu = new NativeMenu { showHide, new NativeMenuItemSeparator(), quit },
        };
        tray.Clicked += (_, _) => ToggleOverlay();

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    /// <summary>
    /// Pushes the configured accent into the application-level resources every window resolves
    /// through DynamicResource - Floaty's own accent brushes and the FluentTheme keys that would
    /// otherwise follow the OS accent. See <see cref="AccentResources"/>.
    /// </summary>
    public void ApplyAccentColor(string? hex)
    {
        var palette = AccentPalette.From(hex);
        AccentResources.Apply(Resources, palette);
        ApplyFluentPalette(Color.Parse(palette.Base));
    }

    /// <summary>
    /// Re-seeds FluentTheme's own palettes, which covers accent-derived resources
    /// <see cref="AccentResources"/> does not name explicitly. Best-effort: the overrides in
    /// Application.Resources are what the UI actually depends on.
    /// </summary>
    private void ApplyFluentPalette(Color accent)
    {
        try
        {
            var fluent = Styles.OfType<FluentTheme>().FirstOrDefault();
            if (fluent is null)
                return;

            foreach (var palette in fluent.Palettes.Values)
                palette.Accent = accent;
        }
        catch
        {
            // Palette mutation is not a documented runtime operation; never let it break startup.
        }
    }

    private static void StartBackgroundUpdateCheck()
    {
        var updateService = Services.GetService<UpdateService>();
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
}
