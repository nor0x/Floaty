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
        }
    }

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
        set { _config.ChatPanelPlacement = value; _saved = false; OnPropertyChanged(); }
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
}
