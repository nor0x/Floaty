using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Floaty.Services;

namespace Floaty.ViewModels.Settings;

/// <summary>
/// The binding surface the settings views see, over the state ported from the Blazor page.
/// </summary>
/// <remarks>
/// Kept in its own file so the ported logic stays a recognisable, reviewable copy of the original
/// <c>@code</c> block rather than being rewritten around Avalonia's binding needs. Everything here is
/// a thin projection: Razor could reach private members directly, Avalonia's compiled bindings cannot.
/// </remarks>
public sealed partial class SettingsViewModel
{
    // --- Shell ---

    public IReadOnlyList<SettingsSection> Sections { get; } = Enum.GetValues<SettingsSection>();

    public SettingsSection ActiveSection
    {
        get => _activeSection;
        set
        {
            if (_activeSection == value)
                return;

            SelectSection(value);
            OnPropertyChanged();
            RaiseAllChanged();
        }
    }

    public bool Saved => _saved;

    /// <summary>The live config clone every simple field binds straight through to.</summary>
    public FloatyConfig Config => _config;

    public string SystemPrompt
    {
        get => _systemPrompt;
        set
        {
            if (_systemPrompt == value)
                return;
            _systemPrompt = value;
            _saved = false;
            OnPropertyChanged();
            RestoreSystemPromptCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>True while the prompt in the box differs from the one Floaty ships with.</summary>
    public bool CanRestoreSystemPrompt => _systemPrompt != DefaultSystemPrompt;

    [RelayCommand(CanExecute = nameof(CanRestoreSystemPrompt))]
    private void RestoreSystemPrompt() => SystemPrompt = DefaultSystemPrompt;

    // --- Behavior ---

    public IReadOnlyList<AutostartMode> AutostartModes { get; } = Enum.GetValues<AutostartMode>();

    public AutostartMode AutostartMode
    {
        get => _config.AutostartMode;
        set { _config.AutostartMode = value; _saved = false; OnPropertyChanged(); }
    }

    public bool RememberTaggedCaptures
    {
        get => _config.RememberTaggedCaptures;
        set { _config.RememberTaggedCaptures = value; _saved = false; OnPropertyChanged(); }
    }

    public bool RememberDroppedFiles
    {
        get => _config.RememberDroppedFiles;
        set { _config.RememberDroppedFiles = value; _saved = false; OnPropertyChanged(); }
    }

    public bool AttachSelectionOnSummon
    {
        get => _config.AttachSelectionOnSummon;
        set { _config.AttachSelectionOnSummon = value; _saved = false; OnPropertyChanged(); }
    }

    public IReadOnlyList<ChatPanelPlacement> ChatPlacements { get; } = Enum.GetValues<ChatPanelPlacement>();

    public ChatPanelPlacement ChatPanelPlacement
    {
        get => _config.ChatPanelPlacement;
        set { _config.ChatPanelPlacement = value; _placementEdited = true; _saved = false; OnPropertyChanged(); }
    }

    // --- Appearance ---

    public IReadOnlyList<RingImageOption> RingOptions => _ringOptions;

    public int CustomRingCount => _customRingCount;

    public string SelectedRing => _config.RingImageFileName;

    /// <summary>
    /// The accent presets as a named record. The ported code holds them as a ValueTuple, which
    /// Avalonia's compiled bindings cannot address by element name.
    /// </summary>
    public sealed record AccentSwatch(string Label, string Value);

    public IReadOnlyList<AccentSwatch> AccentSwatches { get; } =
        AccentPresets.Select(p => new AccentSwatch(p.Label, p.Value)).ToList();

    [RelayCommand]
    private void PickRing(string value)
    {
        SelectRing(value);
        RaiseAllChanged();
    }

    [RelayCommand]
    private void PickAccent(string value)
    {
        SelectAccent(value);
        OnPropertyChanged(nameof(AccentColor));
        RaiseAllChanged();
    }

    [RelayCommand]
    private void RefreshRings()
    {
        ReloadRingImages();
        RaiseAllChanged();
    }

    // --- Sounds ---

    public IReadOnlyList<SoundOption> SoundOptions => _soundOptions;

    public int CustomSoundCount => _customSoundCount;

    public IReadOnlyList<SoundSlot> Slots => SoundSlots;

    public bool CaptureSoundEnabled
    {
        get => _config.CaptureSoundEnabled;
        set { _config.CaptureSoundEnabled = value; _saved = false; OnPropertyChanged(); }
    }

    public bool AssistantDoneSoundEnabled
    {
        get => _config.AssistantDoneSoundEnabled;
        set { _config.AssistantDoneSoundEnabled = value; _saved = false; OnPropertyChanged(); }
    }

    public string CaptureSoundFileName
    {
        get => _config.CaptureSoundFileName;
        set { _config.CaptureSoundFileName = value ?? string.Empty; _saved = false; OnPropertyChanged(); }
    }

    public string AssistantDoneSoundFileName
    {
        get => _config.AssistantDoneSoundFileName;
        set { _config.AssistantDoneSoundFileName = value ?? string.Empty; _saved = false; OnPropertyChanged(); }
    }

    [RelayCommand]
    private void AuditionSound(string fileName) => PreviewSound(fileName);

    [RelayCommand]
    private Task OpenSounds() => OpenSoundsFolder();

    [RelayCommand]
    private void RefreshSounds()
    {
        ReloadSounds();
        RaiseAllChanged();
    }

    // --- Screen history ---

    public IReadOnlyList<ScreenHistoryMode> ScreenHistoryModes { get; } = Enum.GetValues<ScreenHistoryMode>();

    public ScreenHistoryMode ScreenHistoryMode
    {
        get => _config.ScreenHistoryMode;
        set { _config.ScreenHistoryMode = value; _saved = false; OnPropertyChanged(); }
    }

    public int? AutoCaptureCount => _autoCaptureCount;
    public bool ConfirmClearHistory => _confirmClearHistory;
    public bool ClearingHistory => _clearingHistory;
    public bool Reindexing => _reindexing;
    public string? ReindexResult => _reindexResult;

    public string? ReindexProgressText => _reindexProgress is { } p
        ? $"Re-indexing {p.Done} of {p.Total}…"
        : null;

    [RelayCommand]
    private void AskClearHistory()
    {
        _confirmClearHistory = true;
        RaiseAllChanged();
    }

    [RelayCommand]
    private void CancelClearHistory()
    {
        _confirmClearHistory = false;
        RaiseAllChanged();
    }

    [RelayCommand]
    private Task ConfirmClearHistoryNow() => ClearScreenHistory();

    [RelayCommand]
    private Task Reindex() => ReindexCaptures();

    // --- Voice input ---

    public IReadOnlyList<VoiceSendMode> VoiceSendModes { get; } = Enum.GetValues<VoiceSendMode>();

    public VoiceSendMode VoiceSendMode
    {
        get => _config.VoiceSendMode;
        set { _config.VoiceSendMode = value; _saved = false; OnPropertyChanged(); }
    }

    public double AutoSendPauseSeconds
    {
        get => _config.AutoSendPauseSeconds;
        set { _config.AutoSendPauseSeconds = value; _saved = false; OnPropertyChanged(); }
    }

    public string? SttError => _sttError;

    // --- Voice output (Model provider page) ---

    /// <summary>OpenAI's built-in voices. A compatible endpoint with its own can be set by hand in config.json.</summary>
    public static IReadOnlyList<string> SpeechVoices { get; } =
        ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"];

    public bool VoiceOutputEnabled
    {
        get => _config.VoiceOutputEnabled;
        set { _config.VoiceOutputEnabled = value; _voiceOutputEdited = true; _saved = false; OnPropertyChanged(); }
    }

    public string SpeechVoice
    {
        get => _config.SpeechVoice;
        set { _config.SpeechVoice = value ?? string.Empty; _saved = false; OnPropertyChanged(); }
    }

    public double SpeechSpeed
    {
        get => _config.SpeechSpeed;
        set { _config.SpeechSpeed = value; _saved = false; OnPropertyChanged(); }
    }

    public string SpeechInstructions
    {
        get => _config.SpeechInstructions;
        set { _config.SpeechInstructions = value ?? string.Empty; _saved = false; OnPropertyChanged(); }
    }

    public double SpeechVolume
    {
        get => _config.SpeechVolume;
        set { _config.SpeechVolume = value; _saved = false; OnPropertyChanged(); }
    }

    /// <summary>Whether the (unsaved) speech role points at a provider and model — gates the voice options.</summary>
    public bool SpeechRoleAssigned => ResolveSpeechRole() is not null;

    public string? SpeechTestStatus => _speechTestStatus;

    public bool SpeechTesting => _speechTesting;

    [RelayCommand]
    private Task TestVoice() => TestSpeechVoice();

    // --- MCP ---

    public IReadOnlyList<McpServerConfig> McpServers => _config.McpServers;
    public string? McpError => _mcpError;
    public McpServerConfig NewServer => _newServer;

    public string NewServerArgs
    {
        get => _newServerArgs;
        set { _newServerArgs = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string NewServerEnv
    {
        get => _newServerEnv;
        set { _newServerEnv = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string NewServerHeaders
    {
        get => _newServerHeaders;
        set { _newServerHeaders = value ?? string.Empty; OnPropertyChanged(); }
    }

    [RelayCommand]
    private void AddMcpServer()
    {
        AddServer();
        RaiseAllChanged();
    }

    // --- Model provider ---

    // Static because the provider card's DataContext is the ProviderProfile itself, not this view model;
    // the view reaches these through x:Static.
    public static IReadOnlyList<ReasoningEffortLevel> ReasoningEfforts { get; } = Enum.GetValues<ReasoningEffortLevel>();

    public static IReadOnlyList<OutputVerbosity> Verbosities { get; } = Enum.GetValues<OutputVerbosity>();

    // --- Exec ---

    public bool ExecEnabled
    {
        get => _config.ExecEnabled;
        set { _config.ExecEnabled = value; _saved = false; OnPropertyChanged(); }
    }

    public IReadOnlyList<ExecApprovalMode> ExecApprovalModes { get; } = Enum.GetValues<ExecApprovalMode>();

    public ExecApprovalMode ExecApprovalMode
    {
        get => _config.ExecApprovalMode;
        set { _config.ExecApprovalMode = value; _saved = false; OnPropertyChanged(); }
    }

    public IReadOnlyList<ExecShellKind> ExecShells { get; } = Enum.GetValues<ExecShellKind>();

    public ExecShellKind ExecShell
    {
        get => _config.ExecShell;
        set { _config.ExecShell = value; _saved = false; OnPropertyChanged(); }
    }

    public string ExecCustomShellPath
    {
        get => _config.ExecCustomShellPath;
        set { _config.ExecCustomShellPath = value ?? string.Empty; _saved = false; OnPropertyChanged(); }
    }

    public string ExecCustomShellArgs
    {
        get => _config.ExecCustomShellArgs;
        set { _config.ExecCustomShellArgs = value ?? string.Empty; _saved = false; OnPropertyChanged(); }
    }

    // --- Skills ---

    public IReadOnlyList<FloatySkill> Skills => _skills;

    [RelayCommand]
    private void RescanSkills()
    {
        ReloadSkills();
        RaiseAllChanged();
    }

    [RelayCommand]
    private Task OpenSkills() => OpenSkillsFolder();

    // --- Updates ---

    public bool Checking => _checking;
    public bool Downloading => _downloading;
    public bool UpdateAvailable => _updateAvailable;
    public bool UpdatePending => _updatePending;
    public int DownloadProgress => _downloadProgress;
    public string? LatestVersion => _latestVersion;
    public string? UpdateStatus => _updateStatus;
    public string CurrentVersion => _updateService.CurrentVersion;
    public bool UpdatesSupported => _updateService.IsSupported;

    [RelayCommand]
    private Task CheckUpdates() => CheckForUpdates();

    [RelayCommand]
    private Task DownloadUpdateNow() => DownloadUpdate();

    [RelayCommand]
    private void ApplyUpdate() => _updateService.ApplyAndRestart();

    [RelayCommand]
    private Task OpenReleases() => OpenReleasesPage();

    // --- Model provider ---

    public IReadOnlyList<ProviderProfile> Providers => _config.Providers;

    public string ActiveProviderId
    {
        get => _activeProviderId;
        set
        {
            if (_activeProviderId == value)
                return;
            _activeProviderId = value ?? string.Empty;
            OnPropertyChanged();
            RaiseAllChanged();
        }
    }

    public bool Testing => _testing;
    public string? EmbeddingError => _embeddingError;
    public bool OllamaLoading => _ollamaLoading;
    public IReadOnlyList<string> OllamaModels => _ollamaModels;

    public string? TestResultFor(string providerId) =>
        _testResults.TryGetValue(providerId, out var result) ? result : null;

    [RelayCommand]
    private void AddProviderFromPreset(string presetId)
    {
        _presetToAdd = presetId;
        AddProvider();
        RaiseAllChanged();
    }

    [RelayCommand]
    private void DropProvider(ProviderProfile provider)
    {
        RemoveProvider(provider);
        RaiseAllChanged();
    }

    // --- Save ---

    [RelayCommand]
    private void SaveSettings()
    {
        Save();
        RaiseAllChanged();
    }

    // --- Management flows ---
    //
    // These wrap logic that was already ported but had no UI reaching it, which the compiler noticed:
    // the _confirm*Id fields backing the inline "are you sure?" states were assigned and never read.

    /// <summary>Presets still available to add (a preset already in use is not offered twice).</summary>
    public IReadOnlyList<ProviderPreset> AddablePresets => AvailablePresets();

    /// <summary>The built-in speech-to-text catalog, with per-model download state folded in.</summary>
    public IReadOnlyList<ModelRow> SttCatalog => SttModelCatalog.Models
        .Where(m => m.IsAvailable)
        .Select(m => new ModelRow(
            m.Id,
            m.DisplayName,
            $"{m.SizeNote} · {m.LanguageNote}",
            _modelDownloads.IsDownloaded(m),
            _sttDownloading.Contains(m.Id),
            _sttProgress.TryGetValue(m.Id, out var p) ? p : 0,
            string.Equals(_config.SttSelectedModelId, m.Id, StringComparison.OrdinalIgnoreCase),
            _confirmDeleteModelId == m.Id))
        .ToList();

    /// <summary>The built-in on-device embedding catalog, same shape as <see cref="SttCatalog"/>.</summary>
    public IReadOnlyList<ModelRow> EmbeddingCatalog => LocalModelCatalog.Models
        .Select(m => new ModelRow(
            m.Id,
            m.DisplayName,
            $"{m.SizeNote} · {m.LanguageNote} · {m.Dimensions}d",
            _modelDownloads.IsDownloaded(m),
            _embeddingDownloading.Contains(m.Id),
            _embeddingProgress.TryGetValue(m.Id, out var p) ? p : 0,
            false,
            _confirmDeleteEmbeddingId == m.Id))
        .ToList();

    /// <summary>One catalog row, flattened for binding.</summary>
    public sealed record ModelRow(
        string Id,
        string DisplayName,
        string Note,
        bool IsDownloaded,
        bool IsDownloading,
        double Progress,
        bool IsSelected,
        bool ConfirmingDelete)
    {
        /// <summary>Avalonia bindings have no inline boolean AND, so the compound states are properties.</summary>
        public bool CanDownload => !IsDownloaded && !IsDownloading;

        public bool CanDelete => IsDownloaded && !ConfirmingDelete;

        public bool CanUse => IsDownloaded && !IsSelected;
    }

    [RelayCommand]
    private void AddPreset(ProviderPreset preset)
    {
        _presetToAdd = preset.Id;
        AddProvider();
        RaiseAllChanged();
    }

    // Destructive actions are two-step, as they were in the Blazor page: the first click arms the
    // confirmation, the second carries it out. The _confirm*Id fields backing this were already
    // maintained by the ported code and are cleared by it on success.

    /// <summary>True once removing the selected provider has been armed.</summary>
    public bool ConfirmingRemoveProvider =>
        _confirmRemoveProviderId is not null && _confirmRemoveProviderId == _activeProviderId;

    [RelayCommand]
    private void AskRemoveProvider()
    {
        _confirmRemoveProviderId = _activeProviderId;
        RaiseAllChanged();
    }

    [RelayCommand]
    private void CancelDestructive()
    {
        _confirmRemoveProviderId = null;
        _confirmDeleteEmbeddingId = null;
        _confirmDeleteModelId = null;
        RaiseAllChanged();
    }

    [RelayCommand]
    private void DeleteProvider(ProviderProfile provider)
    {
        RemoveProvider(provider);
        _activeProviderId = _config.Providers.FirstOrDefault()?.Id ?? string.Empty;
        RaiseAllChanged();
    }

    [RelayCommand]
    private void DeleteServer(McpServerConfig server)
    {
        RemoveServer(server);
        RaiseAllChanged();
    }

    [RelayCommand]
    private async Task DownloadStt(string modelId)
    {
        if (SttModelCatalog.Find(modelId) is { } model)
            await DownloadSttModel(model);
        RaiseAllChanged();
    }

    /// <summary>The STT model whose deletion is currently armed, if any.</summary>
    public string? ConfirmingDeleteStt => _confirmDeleteModelId;

    [RelayCommand]
    private void AskDeleteStt(string modelId)
    {
        _confirmDeleteModelId = modelId;
        RaiseAllChanged();
    }

    [RelayCommand]
    private void DeleteStt(string modelId)
    {
        if (SttModelCatalog.Find(modelId) is { } model)
            DeleteSttModel(model);
        RaiseAllChanged();
    }

    [RelayCommand]
    private void SelectStt(string modelId)
    {
        _config.SttSelectedModelId = modelId;
        _saved = false;
        RaiseAllChanged();
    }

    [RelayCommand]
    private async Task DownloadEmbedding(string modelId)
    {
        if (LocalModelCatalog.Find(modelId) is { } model)
            await DownloadEmbeddingModel(model);
        RaiseAllChanged();
    }

    /// <summary>The embedding model whose deletion is currently armed, if any.</summary>
    public string? ConfirmingDeleteEmbedding => _confirmDeleteEmbeddingId;

    [RelayCommand]
    private void AskDeleteEmbedding(string modelId)
    {
        _confirmDeleteEmbeddingId = modelId;
        RaiseAllChanged();
    }

    [RelayCommand]
    private void DeleteEmbedding(string modelId)
    {
        if (ActiveProvider is { } provider && LocalModelCatalog.Find(modelId) is { } model)
            DeleteEmbeddingModel(provider, model);
        RaiseAllChanged();
    }

    /// <summary>Skills as rows carrying their own enabled state, which lives in DisabledSkills.</summary>
    public IReadOnlyList<SkillRow> SkillRows =>
        _skills.Select(s => new SkillRow(s.Name, s.Description, IsSkillEnabled(s.Name))).ToList();

    public sealed record SkillRow(string Name, string Description, bool Enabled);

    [RelayCommand]
    private void ToggleSkill(string name)
    {
        SetSkillEnabled(name, !IsSkillEnabled(name));
        RaiseAllChanged();
    }
}
