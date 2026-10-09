using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Floaty.Services;

namespace Floaty.ViewModels.Settings;

/// <summary>
/// Backs the settings window. This is a direct port of the Blazor page's <c>@code</c> block: that
/// logic was already framework-agnostic - a working clone of <see cref="FloatyConfig"/>, provider
/// CRUD, model-list probing, download orchestration and save/revert - so the rewrite changes the
/// presentation, not the behaviour.
/// </summary>
/// <remarks>
/// The clone is the part to preserve carefully: edits are not committed until Save, and the clone is
/// written back wholesale, so <b>a config property missing from the copy in <c>Initialize</c> is
/// silently reset to its default when the user saves</b>. That trap survives the port intact.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    // One copy shared with ChatService, so "Restore default" restores the prompt the chat actually uses.
    private const string DefaultSystemPrompt = SettingsService.DefaultSystemPrompt;

    public enum SettingsSection
    {
        Behavior,
        Appearance,
        Sounds,
        ModelProvider,
        ScreenHistory,
        VoiceInput,
        Mcp,
        Exec,
        Skills,
        Updates,
    }

    public sealed record RingImageOption(string Value, string Label, bool IsBuiltIn,
        Avalonia.Media.Imaging.Bitmap? Preview = null);

    public sealed record SoundOption(string Value, string Label, bool IsBuiltIn);

    /// <summary>
    /// One occasion Floaty makes a noise, paired with the config it reads and writes. Both slots draw
    /// from the same pool of sounds, so the section is one loop over these rather than two copies of
    /// the same markup.
    /// </summary>
    public sealed record SoundSlot(
        string Key,
        string Label,
        string ToggleText,
        string Hint,
        string DefaultSound,
        Func<FloatyConfig, bool> GetEnabled,
        Action<FloatyConfig, bool> SetEnabled,
        Func<FloatyConfig, string> GetSelection,
        Action<FloatyConfig, string> SetSelection);

    private static readonly SoundSlot[] SoundSlots =
    [
        new("captureSound",
            "Screen capture",
            "Play a sound when a window is captured",
            "Fires for /capture and for windows you attach with @. Automatic screen history stays " +
            "silent — it captures constantly. The ring's shutter animation plays either way.",
            SettingsService.DefaultCaptureSound,
            c => c.CaptureSoundEnabled,
            (c, v) => c.CaptureSoundEnabled = v,
            c => c.CaptureSoundFileName,
            (c, v) => c.CaptureSoundFileName = v),
        new("assistantDoneSound",
            "Assistant reply finished",
            "Play a sound when a reply finishes",
            "Plays once the assistant has stopped streaming, so you can look away while it works.",
            SettingsService.DefaultAssistantDoneSound,
            c => c.AssistantDoneSoundEnabled,
            (c, v) => c.AssistantDoneSoundEnabled = v,
            c => c.AssistantDoneSoundFileName,
            (c, v) => c.AssistantDoneSoundFileName = v),
    ];

    private static readonly (string Label, string Value)[] AccentPresets =
    [
        ("Blue", "#2b7fff"),
        ("Indigo", "#6366f1"),
        ("Violet", "#8b5cf6"),
        ("Pink", "#ec4899"),
        ("Red", "#ef4444"),
        ("Orange", "#f97316"),
        ("Green", "#22c55e"),
        ("Teal", "#14b8a6"),
    ];

    private readonly SettingsService _settings;
    private readonly SkillService _skillService;
    private readonly UpdateService _updateService;
    private readonly IMemoryService _memory;
    private readonly ModelDownloadService _modelDownloads;
    private readonly AiClientFactory _aiClients;
    private readonly ILocalEmbeddingFactory _localEmbeddings;
    private readonly ISpeechSynthesisService _speech;
    private readonly IVoiceOutputService _voiceOutput;
    private readonly CaptureRuleService _captureRules;

    public SettingsViewModel(
        SettingsService settings,
        SkillService skillService,
        UpdateService updateService,
        IMemoryService memory,
        ModelDownloadService modelDownloads,
        AiClientFactory aiClients,
        ILocalEmbeddingFactory localEmbeddings,
        ISpeechSynthesisService speech,
        IVoiceOutputService voiceOutput,
        CaptureRuleService captureRules)
    {
        _settings = settings;
        _skillService = skillService;
        _updateService = updateService;
        _memory = memory;
        _modelDownloads = modelDownloads;
        _aiClients = aiClients;
        _localEmbeddings = localEmbeddings;
        _speech = speech;
        _voiceOutput = voiceOutput;
        _captureRules = captureRules;
    }

    /// <summary>
    /// Blazor re-rendered the whole component on StateHasChanged. The port keeps that coarse
    /// granularity rather than hand-annotating ~90 properties: a settings form repaints rarely and
    /// only in response to a click, so an empty property name (which tells Avalonia "everything may
    /// have changed") costs nothing and cannot go stale.
    /// </summary>
    private void RaiseAllChanged()
    {
        OnPropertyChanged(string.Empty);

        // The role rows are their own binding sources, so "everything changed" has to reach them too.
        foreach (var row in _roleRows ?? [])
            row.Refresh();
    }

    /// <summary>
    /// Opens a URI in the user's browser or file manager. Avalonia's launcher hangs off a TopLevel,
    /// which a view-model has no business knowing about, so the settings window supplies this.
    /// </summary>
    public Func<Uri, Task>? ExternalOpener { get; set; }

    private Task OpenExternal(Uri uri) => ExternalOpener?.Invoke(uri) ?? Task.CompletedTask;

    private FloatyConfig _config = new();
    private string _systemPrompt = string.Empty;
    private List<RingImageOption> _ringOptions = new();
    private int _customRingCount;
    private List<SoundOption> _soundOptions = new();
    private int _customSoundCount;
    private bool _saved;
    private SettingsSection _activeSection = SettingsSection.Behavior;

    // Whether the user has touched the three settings this page shares with the live overlay. An
    // untouched one follows the overlay (see AdoptExternalState); a touched one waits for Save.
    private bool _placementEdited;
    private bool _ringSizeEdited;
    private bool _ringImageEdited;

    // Voice output can also be flipped from the chat panel's speaker button while this page is open;
    // like placement, an untouched checkbox here follows it and a touched one waits for Save.
    private bool _voiceOutputEdited;

    // The chat's settings tools (set_appearance, set_sounds, set_voice_output, update_system_prompt) write
    // these while this page may be open. Same rule: untouched here follows the live value.
    private bool _accentEdited;
    private bool _soundsEdited;
    private bool _speechEdited;
    private bool _systemPromptEdited;

    // Capture rules are added/removed both here and by the chat's capture-rule tools.
    private bool _captureRulesEdited;

    // "Always allow" on the chat's command approval card switches exec to automatic.
    private bool _execApprovalEdited;

    // Add-capture-rule form state (Screen history page).
    private string _newRuleMatch = string.Empty;
    private int _newRuleTriggerIndex;
    private double _newRuleAmount = 5;
    private bool _newRuleIncludeScreenshot = true;
    private string? _captureRuleError;

    // Add-MCP-server form state. Args/Env/Headers are edited as text and parsed on Add.
    private McpServerConfig _newServer = new();
    private string _newServerArgs = string.Empty;
    private string _newServerEnv = string.Empty;
    private string _newServerHeaders = string.Empty;
    private string? _mcpError;

    // Discovered agent skills (metadata); enable state is tracked via _config.DisabledSkills.
    private List<FloatySkill> _skills = new();

    // Screen history section state. The count loads lazily when the section is opened.
    private int? _autoCaptureCount;
    private bool _confirmClearHistory;
    private bool _clearingHistory;
    private bool _reindexing;
    private (int Done, int Total)? _reindexProgress;
    private string? _reindexResult;

    // Model Provider section state. Provider edits live on the _config clone and commit on Save;
    // local-model downloads (like the voice ones) hit disk immediately.
    private string _selectedProviderId = string.Empty;
    private string _presetToAdd = ProviderPresets.OpenAiId;
    private string? _confirmRemoveProviderId;
    private readonly Dictionary<string, string> _testResults = new();
    private bool _testing;
    private readonly HashSet<string> _embeddingDownloading = new();
    private readonly Dictionary<string, double> _embeddingProgress = new();
    private string? _embeddingError;
    private string? _confirmDeleteEmbeddingId;
    private List<string> _ollamaModels = new();
    private bool _ollamaLoading;

    // The embedding role as it was when the page opened, so the "you must re-index" warning only
    // appears when this visit actually changes it.
    private string _originalEmbeddingRole = string.Empty;

    // Voice input section state. Downloads mutate disk immediately; selection/mode commit on Save.
    private readonly HashSet<string> _sttDownloading = new();
    private readonly Dictionary<string, double> _sttProgress = new();
    private string? _sttError;
    private string? _confirmDeleteModelId;

    // Update section state.
    private bool _checking;
    private bool _downloading;
    private bool _updateAvailable;
    private bool _updatePending;
    private int _downloadProgress;
    private string? _latestVersion;
    private string? _updateStatus;
    private string? _notesHtml;

    /// <summary>
    /// Loads the working clone and everything the page probes off disk. Synchronous on purpose:
    /// nothing here awaits, and the window is shown only once it has run, so no section ever
    /// paints the default <see cref="FloatyConfig"/> the bindings were attached to.
    /// </summary>
    public void Initialize()
    {
        // Work on a copy so edits aren't committed until the user clicks Save.
        var current = _settings.Current;
        _config = new FloatyConfig
        {
            Providers = current.Providers.Select(CloneProvider).ToList(),
            ActiveProviderId = current.ActiveProviderId,
            ChatRole = CloneRole(current.ChatRole),
            EmbeddingRole = CloneRole(current.EmbeddingRole),
            VisionRole = CloneRole(current.VisionRole),
            ImageRole = CloneRole(current.ImageRole),
            SpeechRole = CloneRole(current.SpeechRole),
            RingImageFileName = current.RingImageFileName,
            RingSize = current.RingSize,
            AccentColor = current.AccentColor,
            CaptureSoundEnabled = current.CaptureSoundEnabled,
            CaptureSoundFileName = current.CaptureSoundFileName,
            AssistantDoneSoundEnabled = current.AssistantDoneSoundEnabled,
            AssistantDoneSoundFileName = current.AssistantDoneSoundFileName,
            SoundVolume = current.SoundVolume,
            ScreenHistoryMode = current.ScreenHistoryMode,
            AutostartMode = current.AutostartMode,
            // Set from the ring's context menu, not this page — but the clone is saved wholesale, so
            // anything left out here is silently reset to its default on Save.
            AlwaysOnTop = current.AlwaysOnTop,
            ChatPanelPlacement = current.ChatPanelPlacement,
            OverlayWindowX = current.OverlayWindowX,
            OverlayWindowY = current.OverlayWindowY,
            ChatWindowX = current.ChatWindowX,
            ChatWindowY = current.ChatWindowY,
            ChatWindowWidth = current.ChatWindowWidth,
            ChatWindowHeight = current.ChatWindowHeight,
            RememberTaggedCaptures = current.RememberTaggedCaptures,
            RememberDroppedFiles = current.RememberDroppedFiles,
            StartWithNewConversation = current.StartWithNewConversation,
            ReplyToastEnabled = current.ReplyToastEnabled,
            ToastDurationSeconds = current.ToastDurationSeconds,
            AttachSelectionOnSummon = current.AttachSelectionOnSummon,
            McpServers = current.McpServers.Select(CloneServer).ToList(),
            CaptureRules = current.CaptureRules.Select(r => r.Clone()).ToList(),
            DisabledSkills = new List<string>(current.DisabledSkills),
            SttSelectedModelId = current.SttSelectedModelId,
            VoiceSendMode = current.VoiceSendMode,
            AutoSendPauseSeconds = current.AutoSendPauseSeconds,
            VoiceOutputEnabled = current.VoiceOutputEnabled,
            SpeechVoice = current.SpeechVoice,
            SpeechSpeed = current.SpeechSpeed,
            SpeechInstructions = current.SpeechInstructions,
            SpeechVolume = current.SpeechVolume,
            ExecEnabled = current.ExecEnabled,
            ExecApprovalMode = current.ExecApprovalMode,
            ExecShell = current.ExecShell,
            ExecCustomShellPath = current.ExecCustomShellPath,
            ExecCustomShellArgs = current.ExecCustomShellArgs,
        };

        _placementEdited = false;
        _ringSizeEdited = false;
        _ringImageEdited = false;
        _voiceOutputEdited = false;
        _accentEdited = false;
        _soundsEdited = false;
        _speechEdited = false;
        _systemPromptEdited = false;
        _captureRulesEdited = false;
        _execApprovalEdited = false;
        _speechTestStatus = null;

        // The overlay keeps editing the live config while this window is open, so follow it rather
        // than sitting on the snapshot taken here (see OnLiveConfigChanged).
        _settings.Changed -= OnLiveConfigChanged;
        _settings.Changed += OnLiveConfigChanged;

        // "Last captured" in the rule list moves whenever a rule fires.
        _captureRules.Captured -= OnCaptureRuleFired;
        _captureRules.Captured += OnCaptureRuleFired;

        _selectedProviderId = ActiveProfile?.Id ?? _config.Providers.FirstOrDefault()?.Id ?? string.Empty;
        _originalEmbeddingRole = RoleKey(ModelRole.Embedding);
        _presetToAdd = AvailablePresets().FirstOrDefault()?.Id ?? string.Empty;

        _systemPrompt = _settings.GetSystemPrompt(DefaultSystemPrompt);
        _skills = _skillService.Skills.ToList();

        // A background startup check may have already downloaded an update; surface its notes.
        _updatePending = _updateService.IsUpdatePending;
        if (_updatePending)
        {
            _latestVersion = _updateService.PendingVersion;
            _notesHtml = _updateService.PendingNotesHtml;
        }

        ReloadRingImages();
        ReloadSounds();

        // The DataContext is already bound to the old _config and an empty prompt; without this
        // the window paints defaults until some unrelated command happens to repaint it.
        RaiseAllChanged();
    }

    /// <summary>
    /// Keeps the working clone in step with config the app writes while this window is open. Without
    /// it the page shows whatever was true when it opened - the ring's context menu can flip the chat
    /// placement or the always-on-top pin behind its back - and because the clone is saved wholesale,
    /// the next Save would quietly put every one of those values back.
    /// </summary>
    private void OnLiveConfigChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // After a Save of our own the clone *is* the live config, so there is nothing to copy -
            // but the controls still have to repaint, since a change made elsewhere reaches the
            // clone without ever raising PropertyChanged.
            if (!ReferenceEquals(_settings.Current, _config))
                AdoptExternalState(_settings.Current);

            RaiseAllChanged();
        });

    /// <summary>
    /// Folds the state the rest of the app owns back into the clone: the ring's context menu
    /// (placement, always-on-top), its drag and Ctrl+scroll (position, size), and the chat window's
    /// own move/resize. Placement and ring size are also editable here, so an unsaved edit on this
    /// page wins over the live value - everything else is not editable here at all and always follows.
    /// </summary>
    private void AdoptExternalState(FloatyConfig current)
    {
        _config.AlwaysOnTop = current.AlwaysOnTop;
        _config.OverlayWindowX = current.OverlayWindowX;
        _config.OverlayWindowY = current.OverlayWindowY;
        _config.ChatWindowX = current.ChatWindowX;
        _config.ChatWindowY = current.ChatWindowY;
        _config.ChatWindowWidth = current.ChatWindowWidth;
        _config.ChatWindowHeight = current.ChatWindowHeight;

        if (!_placementEdited)
            _config.ChatPanelPlacement = current.ChatPanelPlacement;

        if (!_ringSizeEdited)
            _config.RingSize = current.RingSize;

        if (!_voiceOutputEdited)
            _config.VoiceOutputEnabled = current.VoiceOutputEnabled;

        if (!_speechEdited)
        {
            _config.SpeechVoice = current.SpeechVoice;
            _config.SpeechSpeed = current.SpeechSpeed;
        }

        if (!_accentEdited)
            _config.AccentColor = current.AccentColor;

        if (!_soundsEdited)
        {
            _config.CaptureSoundEnabled = current.CaptureSoundEnabled;
            _config.CaptureSoundFileName = current.CaptureSoundFileName;
            _config.AssistantDoneSoundEnabled = current.AssistantDoneSoundEnabled;
            _config.AssistantDoneSoundFileName = current.AssistantDoneSoundFileName;
            _config.SoundVolume = current.SoundVolume;
        }

        if (!_execApprovalEdited)
            _config.ExecApprovalMode = current.ExecApprovalMode;

        if (!_captureRulesEdited)
            _config.CaptureRules = current.CaptureRules.Select(r => r.Clone()).ToList();

        // update_system_prompt writes floaty.md directly; the Save below writes this box back wholesale.
        if (!_systemPromptEdited)
            _systemPrompt = _settings.GetSystemPrompt(DefaultSystemPrompt);

        // The set_ring_image chat tool writes a new file and points the config at it while this page may
        // be open. Without adopting it, the wholesale Save below would quietly put the old ring back.
        if (!_ringImageEdited
            && !string.Equals(_config.RingImageFileName, current.RingImageFileName, StringComparison.Ordinal))
        {
            _config.RingImageFileName = current.RingImageFileName;

            // The generated file is on disk by now, so the gallery can show it as the selected thumbnail.
            ReloadRingImages();
        }
    }

    private async Task CheckForUpdates()
    {
        _checking = true;
        _updateStatus = null;
        _updateAvailable = false;
        _notesHtml = null;
        RaiseAllChanged();

        var result = await _updateService.CheckAsync();

        _checking = false;
        if (result.Error is not null)
        {
            _updateStatus = result.Error;
        }
        else if (result.UpdateAvailable)
        {
            _updateAvailable = true;
            _latestVersion = result.TargetVersion;
            _notesHtml = result.NotesHtml;
        }
        else
        {
            _updateStatus = "You're up to date.";
        }

        RaiseAllChanged();
    }

    private async Task DownloadUpdate()
    {
        _downloading = true;
        _downloadProgress = 0;
        RaiseAllChanged();

        var progress = new Progress<int>(p =>
        {
            _downloadProgress = p;
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RaiseAllChanged);
        });

        try
        {
            await _updateService.DownloadAsync(progress);
            _updatePending = _updateService.IsUpdatePending;
        }
        catch (Exception ex)
        {
            _updateStatus = ex.Message;
        }

        _downloading = false;
        _updateAvailable = false;
        RaiseAllChanged();
    }

    private void RestartAndUpdate() => _updateService.ApplyAndRestart();

    private bool IsSttDownloaded(SttModelInfo model) => model.IsAvailable && _modelDownloads.IsDownloaded(model);

    private void SelectSttModel(string? modelId)
    {
        _config.SttSelectedModelId = modelId;
        _saved = false;
    }

    private async Task DownloadSttModel(SttModelInfo model)
    {
        _sttError = null;
        _sttDownloading.Add(model.Id);
        _sttProgress[model.Id] = 0;
        RaiseAllChanged();

        var progress = new Progress<double>(p =>
        {
            _sttProgress[model.Id] = p;
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RaiseAllChanged);
        });

        try
        {
            await _modelDownloads.DownloadAsync(model, progress);
        }
        catch (Exception ex)
        {
            _sttError = $"Download failed: {ex.Message}";
        }

        _sttDownloading.Remove(model.Id);
        RaiseAllChanged();
    }

    private void DeleteSttModel(SttModelInfo model)
    {
        _confirmDeleteModelId = null;
        _modelDownloads.Delete(model);
        if (_config.SttSelectedModelId == model.Id)
            _config.SttSelectedModelId = null;

        // Deleting files takes effect immediately, so if the saved config still points at this
        // model, clear it right away (not on Save) — a dangling selection would keep claiming
        // voice input is configured while nothing is on disk.
        if (_settings.Current.SttSelectedModelId == model.Id)
        {
            _settings.Current.SttSelectedModelId = null;
            _settings.Save(_settings.Current);
        }
    }

    private async Task OpenReleasesPage()
    {
        try
        {
            await OpenExternal(new Uri(_updateService.ReleasesUrl));
        }
        catch
        {
            // Opening the browser is best-effort.
        }
    }

    private void Save()
    {
        _settings.Save(_config);
        _settings.SaveSystemPrompt(_systemPrompt);
        _saved = true;
        _placementEdited = false;
        _ringSizeEdited = false;
        _ringImageEdited = false;
        _voiceOutputEdited = false;
        _accentEdited = false;
        _soundsEdited = false;
        _speechEdited = false;
        _systemPromptEdited = false;
        _captureRulesEdited = false;
        _execApprovalEdited = false;
    }

    private bool IsSkillEnabled(string name) =>
        !_config.DisabledSkills.Contains(name, StringComparer.OrdinalIgnoreCase);

    private void SetSkillEnabled(string name, bool enabled)
    {
        _config.DisabledSkills.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
            _config.DisabledSkills.Add(name);
        _saved = false;
    }

    private void ReloadSkills()
    {
        _skillService.Reload();
        _skills = _skillService.Skills.ToList();
        _saved = false;
    }

    private async Task OpenSkillsFolder()
    {
        try
        {
            var uri = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = FloatyPaths.Skills }.Uri;
            await OpenExternal(uri);
        }
        catch
        {
            // Opening the folder is best-effort.
        }
    }

    private void AddRule()
    {
        _captureRuleError = null;
        var match = _newRuleMatch.Trim();
        if (match.Length == 0)
        {
            _captureRuleError = "Enter an app name (e.g. notepad, devenv) or a word from the window title.";
            return;
        }

        var amount = (int)Math.Round(_newRuleAmount);
        _config.CaptureRules.Add(new CaptureRule
        {
            Id = Services.Tools.CaptureTools.NewId(),
            Match = match,
            Trigger = _newRuleTriggerIndex switch
            {
                1 => CaptureRuleTrigger.AfterOpen,
                2 => CaptureRuleTrigger.Interval,
                _ => CaptureRuleTrigger.OnOpen,
            },
            IntervalMinutes = Math.Clamp(amount, 1, 1440),
            DelaySeconds = Math.Clamp(amount, 1, 86400),
            IncludeScreenshot = _newRuleIncludeScreenshot,
            CreatedAt = DateTimeOffset.Now,
        });

        _captureRulesEdited = true;
        _saved = false;
        _newRuleMatch = string.Empty;
    }

    private string RuleDetail(CaptureRule rule)
    {
        var parts = new List<string>();
        if (!rule.Enabled)
            parts.Add("paused");
        if (rule.ExpiresAt is { } exp)
            parts.Add(exp <= DateTimeOffset.Now ? "expired" : $"until {exp:ddd d MMM HH:mm}");
        if (_captureRules.LastCaptured(rule.Id) is { } last)
            parts.Add($"last captured {last:HH:mm}");
        if (!string.IsNullOrWhiteSpace(rule.Note))
            parts.Add($"“{rule.Note}”");
        return string.Join(" · ", parts);
    }

    private void OnCaptureRuleFired(object? sender, CaptureRuleFired e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(RaiseAllChanged);

    private void AddServer()
    {
        var name = _newServer.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            _mcpError = "Name is required.";
            return;
        }

        if (_config.McpServers.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _mcpError = $"A server named '{name}' already exists.";
            return;
        }

        var server = new McpServerConfig
        {
            Name = name,
            Transport = _newServer.Transport,
            Enabled = true,
        };

        if (string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            server.Url = _newServer.Url?.Trim() ?? string.Empty;
            server.Headers = ParsePairs(_newServerHeaders);
        }
        else
        {
            server.Command = _newServer.Command?.Trim() ?? string.Empty;
            server.Args = ParseLines(_newServerArgs);
            server.Env = ParsePairs(_newServerEnv);
        }

        _config.McpServers.Add(server);

        _newServer = new McpServerConfig();
        _newServerArgs = _newServerEnv = _newServerHeaders = string.Empty;
        _mcpError = null;
        _saved = false;
    }

    private void RemoveServer(McpServerConfig server)
    {
        _config.McpServers.Remove(server);
        _saved = false;
    }

    // --- Model Provider section ---------------------------------------------------------------
    //
    // Everything here edits the _config clone, so provider changes commit with the shared Save
    // button like the rest of the page. The one exception is downloading a local embedding model,
    // which writes to ~/.floaty/models/embed immediately — mirroring how the voice models behave.

    public ProviderProfile? SelectedProvider =>
        _config.Providers.FirstOrDefault(p => p.Id == _selectedProviderId) ?? _config.Providers.FirstOrDefault();

    private static string ProviderLabel(ProviderProfile provider) =>
        string.IsNullOrWhiteSpace(provider.DisplayName)
            ? ProviderPresets.Find(provider.PresetId)?.DisplayName ?? provider.Id
            : provider.DisplayName;

    private static bool IsOllama(ProviderProfile provider) => provider.PresetId == ProviderPresets.OllamaId;

    private bool ShowOllamaPicker(ProviderProfile provider) => IsOllama(provider) && _ollamaModels.Count > 0;

    /// <summary>The provider follow-active roles resolve to in the unsaved config, if it still exists.</summary>
    private ProviderProfile? ActiveProfile =>
        _config.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, _config.ActiveProviderId, StringComparison.OrdinalIgnoreCase));

    private void SetActiveProvider(ProviderProfile provider)
    {
        _config.ActiveProviderId = provider.Id;
        _saved = false;
    }

    /// <summary>
    /// Presets still worth offering: everything the user hasn't added yet, plus the ones that can
    /// legitimately appear more than once (custom endpoints, a second Azure resource).
    /// </summary>
    private List<ProviderPreset> AvailablePresets()
    {
        var used = _config.Providers.Select(p => p.PresetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ProviderPresets.All
            .Where(p => p.AllowMultiple || !used.Contains(p.Id))
            .Where(p => p.Kind != ProviderKind.LocalOnnx || _localEmbeddings.IsSupported)
            .ToList();
    }

    private void SelectProvider(string id)
    {
        _selectedProviderId = id;
        _confirmRemoveProviderId = null;
        _confirmDeleteEmbeddingId = null;

        // The Ollama tab is only useful with a live model list, and asking costs a 3s timeout at
        // worst, so fetch it when the tab is opened rather than making the user press Refresh.
        if (_config.Providers.FirstOrDefault(p => p.Id == id) is { } provider && IsOllama(provider))
            _ = RefreshOllamaModels(provider);
    }

    private void AddProvider()
    {
        var preset = ProviderPresets.Find(_presetToAdd);
        if (preset is null)
            return;

        var profile = ProviderPresets.CreateProfile(preset, _config.Providers);
        _config.Providers.Add(profile);
        _selectedProviderId = profile.Id;
        _saved = false;

        // The first provider becomes the active one, so adding it is all it takes to be configured.
        // An on-device embedding provider can't chat, so it only takes the slot when nothing else is there.
        if (ActiveProfile is null && profile.Kind != ProviderKind.LocalOnnx)
            _config.ActiveProviderId = profile.Id;

        // Adopt unfilled roles straight away: adding your first provider should leave you configured,
        // not configured-but-unassigned.
        AdoptEmptyRoles(profile);

        _presetToAdd = AvailablePresets().FirstOrDefault()?.Id ?? string.Empty;

        if (IsOllama(profile))
            _ = RefreshOllamaModels(profile);
    }

    /// <summary>
    /// Points any still-unassigned role somewhere, when this provider has a model for that job: at the
    /// active provider when this is it, otherwise pinned to this one. A local embedding provider is never
    /// active, so it is always pinned — which is what keeps memory on-device whichever provider is active.
    /// </summary>
    private void AdoptEmptyRoles(ProviderProfile profile)
    {
        var isActive = ReferenceEquals(ActiveProfile, profile);

        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var assignment = _config.RoleFor(role);
            if (assignment.IsAssigned || string.IsNullOrWhiteSpace(profile.ModelFor(role)) ||
                !AiClientFactory.CanServe(profile.Kind, role))
                continue;

            if (isActive)
                assignment.FollowActive = true;
            else
                assignment.ProviderId = profile.Id;
        }
    }

    private void RemoveProvider(ProviderProfile provider)
    {
        _config.Providers.Remove(provider);
        _confirmRemoveProviderId = null;
        _testResults.Remove(provider.Id);

        // Unassign rather than silently leaving a role pointing at nothing, which would read as
        // "configured" in the dropdown while failing on every call.
        foreach (var role in _config.AllRoles)
        {
            if (!role.FollowActive && role.ProviderId == provider.Id)
            {
                role.ProviderId = string.Empty;
                role.Model = string.Empty;
            }
        }

        // Follow-active roles move to whichever provider is left rather than switching off.
        if (ActiveProfile is null)
            _config.ActiveProviderId = _config.Providers.FirstOrDefault(p => p.Kind != ProviderKind.LocalOnnx)?.Id
                ?? string.Empty;

        _selectedProviderId = _config.Providers.FirstOrDefault()?.Id ?? string.Empty;
        _presetToAdd = AvailablePresets().FirstOrDefault()?.Id ?? _presetToAdd;
        _saved = false;
    }

    private async Task TestProvider(ProviderProfile provider)
    {
        _testing = true;
        _testResults.Remove(provider.Id);
        RaiseAllChanged();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var error = await _aiClients.TestAsync(provider, timeout.Token);
            _testResults[provider.Id] = error is null ? "Works." : $"Failed: {error}";
        }
        finally
        {
            _testing = false;
        }
    }

    // --- Role assignment ---

    /// <summary>The value a role's source dropdown stores for "follow the active provider".</summary>
    private const string ActiveRoleSource = "@active";

    /// <summary>The value a role's source dropdown stores for "off".</summary>
    private const string OffRoleSource = "";

    /// <summary>
    /// The provider and model a role resolves to in the unsaved config, mirroring
    /// <see cref="AiClientFactory"/>'s Resolve minus the key/download checks. Null when it points nowhere.
    /// </summary>
    private (ProviderProfile Profile, string Model)? ResolveRole(ModelRole role)
    {
        var assignment = _config.RoleFor(role);
        if (!assignment.IsAssigned)
            return null;

        var profile = assignment.FollowActive
            ? ActiveProfile
            : _config.Providers.FirstOrDefault(p =>
                string.Equals(p.Id, assignment.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            return null;

        var model = assignment.FollowActive || string.IsNullOrWhiteSpace(assignment.Model)
            ? profile.ModelFor(role)
            : assignment.Model;
        return string.IsNullOrWhiteSpace(model) ? null : (profile, model.Trim());
    }

    /// <summary>"providerId|model" for what a role resolves to, so a change of either is noticed.</summary>
    private string RoleKey(ModelRole role) =>
        ResolveRole(role) is { } r ? $"{r.Profile.Id}|{r.Model}" : string.Empty;

    /// <summary>What a role's source dropdown offers: the active provider, every capable provider, off.</summary>
    private List<RoleSourceOption> RoleSources(ModelRole role)
    {
        var active = ActiveProfile;
        var options = new List<RoleSourceOption>
        {
            new(ActiveRoleSource, active is null ? "Active provider (none)" : $"Active ({ProviderLabel(active)})"),
        };

        foreach (var provider in _config.Providers.Where(p => AiClientFactory.CanServe(p.Kind, role)))
            options.Add(new RoleSourceOption(provider.Id, ProviderLabel(provider)));

        // A hand-edited config can pin a role to a provider that can't serve it; keep it listed so the
        // dropdown shows the truth rather than blanking.
        var current = _config.RoleFor(role);
        if (!current.FollowActive && current.IsAssigned && options.All(o => o.Key != current.ProviderId))
        {
            var provider = _config.Providers.FirstOrDefault(p => p.Id == current.ProviderId);
            options.Add(new RoleSourceOption(current.ProviderId, provider is null ? current.ProviderId : ProviderLabel(provider)));
        }

        options.Add(new RoleSourceOption(OffRoleSource, "Off"));
        return options;
    }

    private string GetRoleSource(ModelRole role)
    {
        var assignment = _config.RoleFor(role);
        return assignment.FollowActive ? ActiveRoleSource
            : assignment.IsAssigned ? assignment.ProviderId
            : OffRoleSource;
    }

    /// <summary>
    /// Points a role at the active provider, a specific one, or nothing. The model override is cleared:
    /// a model id belongs to the provider it was typed for, and the new source has its own default.
    /// </summary>
    private void SetRoleSource(ModelRole role, string? source)
    {
        var assignment = _config.RoleFor(role);
        source ??= OffRoleSource;

        assignment.FollowActive = source == ActiveRoleSource;
        assignment.ProviderId = assignment.FollowActive ? string.Empty : source;
        assignment.Model = string.Empty;
        _saved = false;
    }

    /// <summary>
    /// The model a role uses, as its textbox shows it. A follow-active role has no model of its own, so
    /// it reads and writes the active provider's default for the role; a pinned role edits its override.
    /// </summary>
    private string GetRoleModel(ModelRole role)
    {
        var assignment = _config.RoleFor(role);
        return assignment.FollowActive ? ActiveProfile?.ModelFor(role) ?? string.Empty : assignment.Model;
    }

    private void SetRoleModel(ModelRole role, string? model)
    {
        var assignment = _config.RoleFor(role);
        model ??= string.Empty;

        if (assignment.FollowActive)
            ActiveProfile?.SetModelFor(role, model);
        else
            assignment.Model = model;
        _saved = false;
    }

    /// <summary>Placeholder for a role's model textbox: the provider default a blank override falls back to.</summary>
    private string RoleModelPlaceholder(ModelRole role)
    {
        var assignment = _config.RoleFor(role);
        if (assignment.FollowActive || !assignment.IsAssigned)
            return "model id";

        var fallback = _config.Providers.FirstOrDefault(p => p.Id == assignment.ProviderId)?.ModelFor(role);
        return string.IsNullOrWhiteSpace(fallback) ? "model id" : $"default: {fallback}";
    }

    /// <summary>
    /// Whether this visit changed the embedding role. Switching vector spaces leaves every stored
    /// capture unsearchable until it is re-indexed, so the section says so before the user saves.
    /// </summary>
    private bool EmbeddingRoleChanged => RoleKey(ModelRole.Embedding) != _originalEmbeddingRole;

    // --- Voice output ---

    private string? _speechTestStatus;
    private bool _speechTesting;

    /// <summary>
    /// The provider and model the unsaved speech role points at, falling back to the provider's default
    /// model the way <see cref="AiClientFactory"/> does. Null when the role is effectively unassigned.
    /// </summary>
    private (ProviderProfile Profile, string Model)? ResolveSpeechRole() => ResolveRole(ModelRole.Speech);

    /// <summary>
    /// Speaks a short sample with the settings as they stand on this page, saved or not, so the user
    /// can hear a voice before committing to it. Goes through the real playback path.
    /// </summary>
    private async Task TestSpeechVoice()
    {
        var resolved = ResolveSpeechRole();
        var client = resolved is { } r ? AiClientFactory.CreateSpeechClient(r.Profile, r.Model) : null;
        if (resolved is null || client is null)
        {
            _speechTestStatus = "Assign a Speech model to an OpenAI-compatible provider first.";
            RaiseAllChanged();
            return;
        }

        _speechTesting = true;
        _speechTestStatus = "Synthesizing…";
        RaiseAllChanged();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var settings = new SpeechVoiceSettings(_config.SpeechVoice, _config.SpeechSpeed, _config.SpeechInstructions);
            var wav = await _speech.SynthesizeAsync(
                client, resolved.Value.Model, settings,
                "Hi, I'm Floaty. This is how I'll sound when I read my replies to you.",
                timeout.Token);

            _voiceOutput.PlayClip(wav, _config.SpeechVolume);
            _speechTestStatus = "Works.";
        }
        catch (Exception ex)
        {
            _speechTestStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            _speechTesting = false;
            RaiseAllChanged();
        }
    }

    // --- Local (on-device) embedding models ---

    private void SelectLocalEmbeddingModel(ProviderProfile provider, string modelId)
    {
        provider.EmbeddingModel = modelId;

        // A local provider exists to serve the embedding role, so picking a model here also points
        // the role at it. Anything else would need two interactions to do the obvious thing.
        _config.EmbeddingRole = new ModelAssignment { ProviderId = provider.Id, Model = modelId };
        _saved = false;
    }

    private async Task DownloadEmbeddingModel(LocalEmbeddingModelInfo model)
    {
        _embeddingError = null;
        _embeddingDownloading.Add(model.Id);
        _embeddingProgress[model.Id] = 0;

        try
        {
            var progress = new Progress<double>(value =>
            {
                _embeddingProgress[model.Id] = value;
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RaiseAllChanged);
            });

            await _modelDownloads.DownloadAsync(model, progress);
        }
        catch (Exception ex)
        {
            _embeddingError = $"Download failed: {ex.Message}";
        }
        finally
        {
            _embeddingDownloading.Remove(model.Id);
            _embeddingProgress.Remove(model.Id);
        }
    }

    private void DeleteEmbeddingModel(ProviderProfile provider, LocalEmbeddingModelInfo model)
    {
        _confirmDeleteEmbeddingId = null;
        _modelDownloads.Delete(model);

        // Clear a selection that now points at nothing, so the panel and the role agree with disk.
        if (provider.EmbeddingModel == model.Id)
        {
            provider.EmbeddingModel = string.Empty;
            if (_config.EmbeddingRole.ProviderId == provider.Id)
                _config.EmbeddingRole = new ModelAssignment();
            _saved = false;
        }
    }

    // --- Ollama ---

    private async Task RefreshOllamaModels(ProviderProfile provider)
    {
        _ollamaLoading = true;
        RaiseAllChanged();

        try
        {
            _ollamaModels = (await OllamaProbe.ListModelsAsync(provider.BaseUrl)).ToList();
        }
        finally
        {
            _ollamaLoading = false;
            RaiseAllChanged();
        }
    }

    private static ProviderProfile CloneProvider(ProviderProfile p) => new()
    {
        Id = p.Id,
        PresetId = p.PresetId,
        DisplayName = p.DisplayName,
        Kind = p.Kind,
        ApiKey = p.ApiKey,
        BaseUrl = p.BaseUrl,
        ChatModel = p.ChatModel,
        EmbeddingModel = p.EmbeddingModel,
        VisionModel = p.VisionModel,
        ImageModel = p.ImageModel,
        SpeechModel = p.SpeechModel,
        ChatApi = p.ChatApi,
        RequestThinking = p.RequestThinking,
        ThinkingBudgetTokens = p.ThinkingBudgetTokens,
        ReasoningEffort = p.ReasoningEffort,
        Verbosity = p.Verbosity,
    };

    private static ModelAssignment CloneRole(ModelAssignment a) => new()
    {
        FollowActive = a.FollowActive,
        ProviderId = a.ProviderId,
        Model = a.Model,
    };

    private static McpServerConfig CloneServer(McpServerConfig s) => new()
    {
        Name = s.Name,
        Transport = s.Transport,
        Enabled = s.Enabled,
        Command = s.Command,
        Args = new List<string>(s.Args),
        Env = new Dictionary<string, string>(s.Env),
        Url = s.Url,
        Headers = new Dictionary<string, string>(s.Headers),
    };

    private static List<string> ParseLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static Dictionary<string, string> ParsePairs(string? text)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
                continue;
            var key = line[..index].Trim();
            if (key.Length > 0)
                result[key] = line[(index + 1)..].Trim();
        }

        return result;
    }

    private void ReloadRingImages()
    {
        var customImages = _settings.GetAvailableRingImages().ToList();
        var builtInImages = _settings
            .GetBuiltInRingImages()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();


        // Previews are real bitmaps now. The webview needed base64 data URLs served through a
        // custom https://localfiles scheme; an Avalonia Image binds to a Bitmap directly.
        var builtInOptions = builtInImages
            .Select(name => new RingImageOption(name, name, true, LoadBuiltInRingPreview(name)))
            .ToList();

        var customOptions = customImages
            .Select(name => new RingImageOption(name, name, false, LoadCustomRingPreview(name)))
            .ToList();

        _ringOptions =
        [
            .. builtInOptions,
            .. customOptions,
        ];

        _customRingCount = customImages.Count;

        if (!_settings.IsValidRingSelection(_config.RingImageFileName))
        {
            _config.RingImageFileName = string.Empty;
        }

        _saved = false;
    }

    private Avalonia.Media.Imaging.Bitmap? LoadBuiltInRingPreview(string name)
    {
        try
        {
            var uri = new Uri($"avares://Floaty/Resources/Images/{name}");
            return Avalonia.Platform.AssetLoader.Exists(uri)
                ? new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(uri))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private Avalonia.Media.Imaging.Bitmap? LoadCustomRingPreview(string name)
    {
        try
        {
            var path = _settings.GetRingImageFullPath(name);
            return path is null ? null : new Avalonia.Media.Imaging.Bitmap(path);
        }
        catch
        {
            // A ring image that no longer decodes just loses its thumbnail.
            return null;
        }
    }

    private void SelectRing(string value)
    {
        if (string.Equals(_config.RingImageFileName, value, StringComparison.OrdinalIgnoreCase))
            return;

        _config.RingImageFileName = value;
        _ringImageEdited = true;
        _saved = false;
    }

    /// <summary>
    /// Ring diameter. Bound to the Appearance slider; live-previews on the overlay as it moves and is
    /// committed only on Save. (In Blazor this was an oninput handler taking a ChangeEventArgs.)
    /// </summary>
    public double RingSize
    {
        get => _config.RingSize;
        set
        {
            var clamped = SettingsService.ClampRingSize(value);
            if (Math.Abs(_config.RingSize - clamped) < 0.001)
                return;

            _config.RingSize = clamped;
            _ringSizeEdited = true;
            _saved = false;
            _settings.PreviewRingSize(clamped);
            OnPropertyChanged();
        }
    }

    // --- Sounds ---

    private void ReloadSounds()
    {
        var customSounds = _settings.GetAvailableSounds().ToList();

        _soundOptions =
        [
            .. _settings.GetBuiltInSounds()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new SoundOption(name, name, true)),
            .. customSounds.Select(name => new SoundOption(name, name, false)),
        ];

        _customSoundCount = customSounds.Count;

        // A file deleted behind Floaty's back falls back to the slot's built-in rather than erroring.
        foreach (var slot in SoundSlots)
        {
            if (!_settings.IsValidSoundSelection(slot.GetSelection(_config)))
                slot.SetSelection(_config, string.Empty);
        }

        _saved = false;
    }

    // An empty selection means "the slot's default built-in", so that option shows as the selected one.
    private bool IsSelectedSound(SoundSlot slot, string value)
    {
        var selection = slot.GetSelection(_config);
        return string.IsNullOrWhiteSpace(selection)
            ? string.Equals(value, slot.DefaultSound, StringComparison.OrdinalIgnoreCase)
            : string.Equals(selection, value, StringComparison.OrdinalIgnoreCase);
    }

    private void SetSoundEnabled(SoundSlot slot, bool enabled)
    {
        slot.SetEnabled(_config, enabled);
        _soundsEdited = true;
        _saved = false;
    }

    private void SelectSound(SoundSlot slot, string value)
    {
        if (string.Equals(slot.GetSelection(_config), value, StringComparison.OrdinalIgnoreCase))
            return;

        slot.SetSelection(_config, value);
        _soundsEdited = true;
        _saved = false;
    }

    // Auditions through ISoundService (see SettingsService.PreviewSound) so the user hears it on the
    // same device at the volume currently on the slider, saved or not.
    private void PreviewSound(string fileName) => _settings.PreviewSound(fileName, _config.SoundVolume);

    /// <summary>Playback volume as a 0-100 percentage, which is what the slider speaks.</summary>
    public double SoundVolumePercent
    {
        get => _config.SoundVolume * 100.0;
        set
        {
            var clamped = SettingsService.ClampSoundVolume(value / 100.0);
            if (Math.Abs(_config.SoundVolume - clamped) < 0.0001)
                return;

            _config.SoundVolume = clamped;
            _soundsEdited = true;
            _saved = false;
            OnPropertyChanged();
        }
    }

    private async Task OpenSoundsFolder()
    {
        try
        {
            var uri = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = FloatyPaths.Sounds }.Uri;
            await OpenExternal(uri);
        }
        catch
        {
            // Opening the folder is a convenience; the path is spelled out in the hint either way.
        }
    }

    private bool IsSelectedAccent(string value) =>
        string.Equals(_config.AccentColor, value, StringComparison.OrdinalIgnoreCase);

    // Live-preview the accent on the overlay as the user picks; committed only on Save.
    private void SelectAccent(string value)
    {
        _config.AccentColor = SettingsService.NormalizeAccentColor(value);
        _accentEdited = true;
        _saved = false;
        _settings.PreviewAccentColor(_config.AccentColor);
    }

    /// <summary>The accent as a hex string, bound to the custom-colour box.</summary>
    public string AccentColor
    {
        get => _config.AccentColor;
        set
        {
            var normalized = SettingsService.NormalizeAccentColor(value);
            if (string.Equals(_config.AccentColor, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            SelectAccent(normalized);
            OnPropertyChanged();
        }
    }

    // If the settings window closes without saving, revert any live previews (ring size, accent)
    // to the persisted values so the overlay doesn't keep uncommitted state.
    public void Dispose()
    {
        _settings.Changed -= OnLiveConfigChanged;
        _captureRules.Captured -= OnCaptureRuleFired;
        _settings.PreviewRingSize(_settings.Current.RingSize);
        _settings.PreviewAccentColor(_settings.Current.AccentColor);
    }

    private bool IsSelectedRing(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.IsNullOrWhiteSpace(_config.RingImageFileName);

        return string.Equals(_config.RingImageFileName, value, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectSection(SettingsSection section)
    {
        _activeSection = section;

        if (section == SettingsSection.ScreenHistory)
        {
            _confirmClearHistory = false;
            _autoCaptureCount = null;
            _ = LoadAutoCaptureCountAsync();
        }
    }

    private static string AutostartModeHint(AutostartMode mode) => mode switch
    {
        AutostartMode.Disabled => "Floaty only runs when you launch it yourself.",
        AutostartMode.Minimized => "Floaty starts hidden when you sign in — summon it with Alt+F or the tray icon.",
        _ => "The floating ring appears as soon as you sign in.",
    };

    private static string ChatPanelPlacementHint(ChatPanelPlacement placement) => placement switch
    {
        ChatPanelPlacement.Floating => "The chat opens right beside the ring and follows it around the screen.",
        _ => "The chat gets its own window, starting bottom-left. Drag it by the bar above the messages; " +
            "the ring just shows and hides it.",
    };

    private static string ScreenHistoryModeHint(ScreenHistoryMode mode) => mode switch
    {
        ScreenHistoryMode.Disabled => "Nothing is recorded.",
        ScreenHistoryMode.TextOnly => "When you settle on a window or tab, its visible text is saved to " +
            "local memory (one embedding call per snapshot). No screenshots.",
        _ => "Also saves a PNG of the window; if a Snapshot model is set, it describes the image " +
            "(adds a vision call per snapshot).",
    };

    private async Task LoadAutoCaptureCountAsync()
    {
        try
        {
            _autoCaptureCount = await _memory.CountAutoCapturesAsync();
        }
        catch
        {
            _autoCaptureCount = 0; // a fresh install has no database yet
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RaiseAllChanged);
    }

    private async Task ClearScreenHistory()
    {
        _clearingHistory = true;
        RaiseAllChanged();

        try
        {
            await _memory.DeleteAutoCapturesAsync();
            _autoCaptureCount = 0;
        }
        catch
        {
            // Best-effort; the count reload below reflects whatever actually happened.
            _ = LoadAutoCaptureCountAsync();
        }

        _clearingHistory = false;
        _confirmClearHistory = false;
        RaiseAllChanged();
    }

    private async Task ReindexCaptures()
    {
        _reindexing = true;
        _reindexResult = null;
        _reindexProgress = (0, 0);

        // Marshalled back onto the UI thread: the reporter fires from the background sweep.
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            _reindexProgress = p;
            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RaiseAllChanged);
        });

        try
        {
            var count = await _memory.ReindexCapturesAsync(progress);
            _reindexResult = $"Re-indexed {count} capture(s).";
        }
        catch (Exception ex)
        {
            _reindexResult = $"Re-index failed: {ex.Message}";
        }

        _reindexProgress = null;
        _reindexing = false;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RaiseAllChanged);
    }

    private string TabClass(SettingsSection section) => section == _activeSection ? "active" : string.Empty;
}
