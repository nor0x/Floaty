using CommunityToolkit.Mvvm.Input;
using Floaty.Services;

namespace Floaty.ViewModels.Settings;

/// <summary>
/// The Jobs page: recurring jobs from <c>~/.floaty/jobs</c>, a form to add one, and how a finished run is
/// announced.
/// </summary>
/// <remarks>
/// Two kinds of state live here, and they commit differently. The jobs themselves are files owned by
/// <see cref="JobService"/>, so every row action (toggle, run, delete) and Add write the file at once -
/// the same as the chat's job tools, and the list follows the folder live. The master switch and the
/// notification options are ordinary config on the working clone and wait for Save like everything else.
/// </remarks>
public sealed partial class SettingsViewModel
{
    private List<RecurringJob> _jobs = new();
    private string _newJobName = string.Empty;
    private string _newJobSchedule = "0 9 * * *";
    private string _newJobPrompt = string.Empty;
    private string _newJobModel = string.Empty;
    private string? _jobError;
    private string? _confirmDeleteJob;

    /// <summary>One job in the list, with its display text worked out on repaint.</summary>
    public sealed record JobRow(
        string Name,
        bool Enabled,
        string Schedule,
        string Status,
        string? Problem,
        bool IsBusy,
        bool ConfirmingDelete)
    {
        public bool CanRun => !IsBusy;
    }

    public IReadOnlyList<JobRow> JobRows => _jobs.Select(ToJobRow).ToList();

    public bool HasJobs => _jobs.Count > 0;

    public bool JobsEnabled
    {
        get => _config.JobsEnabled;
        set { _config.JobsEnabled = value; _saved = false; OnPropertyChanged(); }
    }

    public bool JobToastEnabled
    {
        get => _config.JobToastEnabled;
        set { _config.JobToastEnabled = value; _saved = false; OnPropertyChanged(); }
    }

    /// <summary>The same flag as the "Recurring job finished" row under Sounds, which also picks the sound.</summary>
    public bool JobSoundEnabled
    {
        get => _config.JobSoundEnabled;
        set { _config.JobSoundEnabled = value; _soundsEdited = true; _saved = false; OnPropertyChanged(); }
    }

    public bool JobNotifyOnlyOnFailure
    {
        get => _config.JobNotifyOnlyOnFailure;
        set { _config.JobNotifyOnlyOnFailure = value; _saved = false; OnPropertyChanged(); }
    }

    public string NewJobName
    {
        get => _newJobName;
        set
        {
            _newJobName = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NewJobFileHint));
        }
    }

    /// <summary>The file the name will become, since "Morning digest" is saved as morning-digest.md.</summary>
    public string NewJobFileHint =>
        JobService.ToJobName(_newJobName) is { Length: > 0 } name ? $"Saved as {name}.md" : string.Empty;

    public string NewJobSchedule
    {
        get => _newJobSchedule;
        set
        {
            _newJobSchedule = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NewJobScheduleText));
            OnPropertyChanged(nameof(NewJobScheduleValid));
        }
    }

    public bool NewJobScheduleValid => CronText.TryParse(_newJobSchedule, out _, out _);

    /// <summary>"Every day at 09:00 · next Sat 11 Oct 09:00", or why the expression doesn't parse.</summary>
    public string NewJobScheduleText
    {
        get
        {
            if (!CronText.TryParse(_newJobSchedule, out var cron, out var error))
                return error ?? string.Empty;

            var text = CronText.Describe(_newJobSchedule);
            return CronText.Next(cron!, DateTimeOffset.Now) is { } next ? $"{text} · next {next:ddd d MMM HH:mm}" : text;
        }
    }

    public string NewJobPrompt
    {
        get => _newJobPrompt;
        set { _newJobPrompt = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string NewJobModel
    {
        get => _newJobModel;
        set { _newJobModel = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string? JobError => _jobError;

    public string JobsFolder => FloatyPaths.Jobs;

    [RelayCommand]
    private void AddJob()
    {
        var name = JobService.ToJobName(_newJobName);
        if (name.Length == 0)
        {
            _jobError = "Give the job a name.";
        }
        else if (_jobService.Create(name, _newJobSchedule, _newJobPrompt, enabled: true,
                     string.IsNullOrWhiteSpace(_newJobModel) ? null : _newJobModel, null, out var error) is null)
        {
            _jobError = error;
        }
        else
        {
            _jobError = null;
            _newJobName = string.Empty;
            _newJobPrompt = string.Empty;
            _newJobModel = string.Empty;
            _jobs = _jobService.Jobs.ToList();
        }

        RaiseAllChanged();
    }

    [RelayCommand]
    private void ToggleJob(string name)
    {
        var job = _jobService.Find(name);
        if (job is not null && !_jobService.SetEnabled(job.Name, !job.Enabled, out var error))
            _jobError = error;

        _jobs = _jobService.Jobs.ToList();
        RaiseAllChanged();
    }

    [RelayCommand]
    private void RunJob(string name)
    {
        _jobError = _jobScheduler.RunNow(name, out var message) ? null : message;
        RaiseAllChanged();
    }

    [RelayCommand]
    private async Task EditJob(string name)
    {
        if (_jobService.Find(name) is not { } job)
            return;

        try
        {
            await OpenExternal(new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = job.FilePath }.Uri);
        }
        catch (Exception ex)
        {
            _jobError = $"Couldn't open {job.FilePath}: {ex.Message}";
            RaiseAllChanged();
        }
    }

    [RelayCommand]
    private void AskDeleteJob(string name)
    {
        _confirmDeleteJob = name;
        RaiseAllChanged();
    }

    [RelayCommand]
    private void CancelDeleteJob()
    {
        _confirmDeleteJob = null;
        RaiseAllChanged();
    }

    [RelayCommand]
    private void ConfirmDeleteJob(string name)
    {
        _confirmDeleteJob = null;
        _jobError = _jobService.Delete(name, out var error) ? null : error;
        _jobs = _jobService.Jobs.ToList();
        RaiseAllChanged();
    }

    [RelayCommand]
    private void RescanJobs()
    {
        _jobService.Reload();
        _jobs = _jobService.Jobs.ToList();
        RaiseAllChanged();
    }

    [RelayCommand]
    private async Task OpenJobsFolder()
    {
        try
        {
            await OpenExternal(new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = FloatyPaths.Jobs }.Uri);
        }
        catch (Exception ex)
        {
            _jobError = $"Couldn't open {FloatyPaths.Jobs}: {ex.Message}";
            RaiseAllChanged();
        }
    }

    private JobRow ToJobRow(RecurringJob job)
    {
        var busy = _jobScheduler.IsBusy(job.Name);
        var parts = new List<string>();

        if (busy)
            parts.Add("Running now…");
        else if (!job.Enabled)
            parts.Add("Paused");
        else if (job.IsValid && job.NextRun is { } next)
            parts.Add(next <= DateTimeOffset.Now ? "Due now" : $"Next {next:ddd d MMM HH:mm}");

        if (job.LastRun is { } last && !busy)
        {
            var mark = job.LastStatus switch
            {
                JobStatus.Success => "✓",
                JobStatus.Error => "✕",
                _ => "·",
            };
            parts.Add($"Last {mark} {Relative(last)}");
        }
        else if (job.LastRun is null && !busy)
        {
            parts.Add("Never run");
        }

        if (job.Model is not null)
            parts.Add(job.Model);
        if (job.McpTools.Count > 0)
            parts.Add($"{job.McpTools.Count} MCP tool{(job.McpTools.Count == 1 ? "" : "s")}");

        var problem = job.ParseError
            ?? (job.LastStatus == JobStatus.Error && !busy ? job.LastError : null);

        return new JobRow(
            job.Name,
            job.Enabled,
            job.IsValid ? $"{job.ScheduleText}  ({job.Schedule})" : job.Schedule,
            string.Join(" · ", parts),
            problem,
            busy,
            string.Equals(_confirmDeleteJob, job.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(DateTimeOffset when)
    {
        var ago = DateTimeOffset.Now - when;
        if (ago.TotalMinutes < 1)
            return "just now";
        if (ago.TotalHours < 1)
            return $"{(int)ago.TotalMinutes} min ago";
        if (ago.TotalDays < 1)
            return $"{(int)ago.TotalHours} h ago";
        return when.ToString("ddd d MMM HH:mm");
    }

    private void OnJobsChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _jobs = _jobService.Jobs.ToList();
            RaiseAllChanged();
        });

    private void OnJobRunStarted(object? sender, string jobName) => OnJobsChanged(sender, EventArgs.Empty);

    private void OnJobRunCompleted(object? sender, JobRunResult e) => OnJobsChanged(sender, EventArgs.Empty);
}
