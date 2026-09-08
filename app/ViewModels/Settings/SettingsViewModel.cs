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
    private const string DefaultSystemPrompt =
        "You are Floaty, a desktop assistant that lives in a floating overlay. The user can capture " +
        "what's on their screen, and Floaty may also snapshot windows automatically as the user switches " +
        "between them (screen history); both are stored in local memory. When the user asks about " +
        "something they previously saw, viewed, read, or captured — or about their earlier activity — " +
        "call the search_captures tool to retrieve it before answering, and ground your answer in what " +
        "it returns. Be concise.";

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

    public SettingsViewModel(
        SettingsService settings,
        SkillService skillService,
        UpdateService updateService,
        IMemoryService memory,
        ModelDownloadService modelDownloads,
        AiClientFactory aiClients,
        ILocalEmbeddingFactory localEmbeddings)
    {
        _settings = settings;
        _skillService = skillService;
        _updateService = updateService;
        _memory = memory;
        _modelDownloads = modelDownloads;
        _aiClients = aiClients;
        _localEmbeddings = localEmbeddings;
    }

    /// <summary>
    /// Blazor re-rendered the whole component on StateHasChanged. The port keeps that coarse
    /// granularity rather than hand-annotating ~90 properties: a settings form repaints rarely and
    /// only in response to a click, so an empty property name (which tells Avalonia "everything may
    /// have changed") costs nothing and cannot go stale.
    /// </summary>
    private void RaiseAllChanged() => OnPropertyChanged(string.Empty);

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
    private string _activeProviderId = string.Empty;
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
            ChatRole = CloneRole(current.ChatRole),
            EmbeddingRole = CloneRole(current.EmbeddingRole),
            VisionRole = CloneRole(current.VisionRole),
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
            ChatWindowX = current.ChatWindowX,
            ChatWindowY = current.ChatWindowY,
            ChatWindowWidth = current.ChatWindowWidth,
            ChatWindowHeight = current.ChatWindowHeight,
            RememberTaggedCaptures = current.RememberTaggedCaptures,
            RememberDroppedFiles = current.RememberDroppedFiles,
            AttachSelectionOnSummon = current.AttachSelectionOnSummon,
            McpServers = current.McpServers.Select(CloneServer).ToList(),
            DisabledSkills = new List<string>(current.DisabledSkills),
            SttSelectedModelId = current.SttSelectedModelId,
            VoiceSendMode = current.VoiceSendMode,
            AutoSendPauseSeconds = current.AutoSendPauseSeconds,
            ExecEnabled = current.ExecEnabled,
            ExecApprovalMode = current.ExecApprovalMode,
            ExecShell = current.ExecShell,
            ExecCustomShellPath = current.ExecCustomShellPath,
            ExecCustomShellArgs = current.ExecCustomShellArgs,
        };

        _activeProviderId = _config.Providers.FirstOrDefault()?.Id ?? string.Empty;
        _originalEmbeddingRole = RoleKey(_config.EmbeddingRole);
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

    /// <summary>One entry in a role dropdown: a provider plus the model it would use for that role.</summary>
    private sealed record RoleOption(string Key, string Label);

    public ProviderProfile? ActiveProvider =>
        _config.Providers.FirstOrDefault(p => p.Id == _activeProviderId) ?? _config.Providers.FirstOrDefault();

    private static string ProviderLabel(ProviderProfile provider) =>
        string.IsNullOrWhiteSpace(provider.DisplayName)
            ? ProviderPresets.Find(provider.PresetId)?.DisplayName ?? provider.Id
            : provider.DisplayName;

    private static bool IsOllama(ProviderProfile provider) => provider.PresetId == ProviderPresets.OllamaId;

    private bool ShowOllamaPicker(ProviderProfile provider) => IsOllama(provider) && _ollamaModels.Count > 0;

    /// <summary>How many of the three roles this provider currently serves; shown as a tab badge.</summary>
    private int RoleCountFor(string providerId) =>
        new[] { _config.ChatRole, _config.EmbeddingRole, _config.VisionRole }
            .Count(r => r.ProviderId == providerId);

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
        _activeProviderId = id;
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
        _activeProviderId = profile.Id;
        _saved = false;

        // Adopt unfilled roles straight away: adding your first provider should leave you configured,
        // not configured-but-unassigned.
        AdoptEmptyRoles(profile);

        _presetToAdd = AvailablePresets().FirstOrDefault()?.Id ?? string.Empty;

        if (IsOllama(profile))
            _ = RefreshOllamaModels(profile);
    }

    /// <summary>Points any still-unassigned role at this provider, when it has a model for that job.</summary>
    private void AdoptEmptyRoles(ProviderProfile profile)
    {
        if (!_config.ChatRole.IsAssigned && !string.IsNullOrWhiteSpace(profile.ChatModel))
            _config.ChatRole = new ModelAssignment { ProviderId = profile.Id, Model = profile.ChatModel };

        if (!_config.EmbeddingRole.IsAssigned && !string.IsNullOrWhiteSpace(profile.EmbeddingModel))
            _config.EmbeddingRole = new ModelAssignment { ProviderId = profile.Id, Model = profile.EmbeddingModel };

        if (!_config.VisionRole.IsAssigned && !string.IsNullOrWhiteSpace(profile.VisionModel))
            _config.VisionRole = new ModelAssignment { ProviderId = profile.Id, Model = profile.VisionModel };
    }

    private void RemoveProvider(ProviderProfile provider)
    {
        _config.Providers.Remove(provider);
        _confirmRemoveProviderId = null;
        _testResults.Remove(provider.Id);

        // Unassign rather than silently leaving a role pointing at nothing, which would read as
        // "configured" in the dropdown while failing on every call.
        foreach (var role in new[] { _config.ChatRole, _config.EmbeddingRole, _config.VisionRole })
        {
            if (role.ProviderId == provider.Id)
            {
                role.ProviderId = string.Empty;
                role.Model = string.Empty;
            }
        }

        _activeProviderId = _config.Providers.FirstOrDefault()?.Id ?? string.Empty;
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

    /// <summary>
    /// Serializes an assignment as "providerId|model" so one &lt;select&gt; can offer several models
    /// from the same provider without needing a second control.
    /// </summary>
    private static string RoleKey(ModelAssignment assignment) =>
        assignment.IsAssigned ? $"{assignment.ProviderId}|{assignment.Model}" : string.Empty;

    private List<RoleOption> RoleOptions(ModelRole role)
    {
        var options = new List<RoleOption>();

        foreach (var provider in _config.Providers)
        {
            var model = role switch
            {
                ModelRole.Chat => provider.ChatModel,
                ModelRole.Embedding => provider.EmbeddingModel,
                _ => provider.VisionModel,
            };

            if (string.IsNullOrWhiteSpace(model))
                continue;

            options.Add(new RoleOption($"{provider.Id}|{model}", $"{ProviderLabel(provider)} · {model}"));
        }

        // A role can point at a model the provider no longer defaults to (hand-edited config, or a
        // model field cleared afterwards). Keep it listed so opening Settings doesn't silently
        // reset a working setup to "not configured".
        var current = role switch
        {
            ModelRole.Chat => _config.ChatRole,
            ModelRole.Embedding => _config.EmbeddingRole,
            _ => _config.VisionRole,
        };

        var key = RoleKey(current);
        if (key.Length > 0 && options.All(o => o.Key != key))
        {
            var provider = _config.Providers.FirstOrDefault(p => p.Id == current.ProviderId);
            if (provider is not null)
                options.Add(new RoleOption(key, $"{ProviderLabel(provider)} · {current.Model}"));
        }

        return options;
    }

    private void AssignRole(ModelAssignment assignment, string? key)
    {
        var parts = (key ?? string.Empty).Split('|', 2);
        assignment.ProviderId = parts[0];
        assignment.Model = parts.Length > 1 ? parts[1] : string.Empty;
        _saved = false;
    }

    /// <summary>
    /// Whether this visit changed the embedding role. Switching vector spaces leaves every stored
    /// capture unsearchable until it is re-indexed, so the section says so before the user saves.
    /// </summary>
    private bool EmbeddingRoleChanged => RoleKey(_config.EmbeddingRole) != _originalEmbeddingRole;

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
        UseResponsesApi = p.UseResponsesApi,
    };

    private static ModelAssignment CloneRole(ModelAssignment a) => new()
    {
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
        _saved = false;
    }

    private void SelectSound(SoundSlot slot, string value)
    {
        if (string.Equals(slot.GetSelection(_config), value, StringComparison.OrdinalIgnoreCase))
            return;

        slot.SetSelection(_config, value);
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
