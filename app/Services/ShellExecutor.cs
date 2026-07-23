using System.Diagnostics;
using System.Text;

namespace Floaty.Services;

/// <summary>
/// Resolves the user's configured shell (<see cref="FloatyConfig.ExecShell"/>) and runs a single command
/// through it, capturing stdout+stderr. This is the only place in the app that launches arbitrary
/// processes; the <c>exec</c> agent tool in <see cref="ChatService"/> calls it after the user approves a
/// command. Output is truncated and the process is killed on timeout so a runaway command can't hang chat.
/// </summary>
public static class ShellExecutor
{
    /// <summary>Placeholder in a custom argument template that gets replaced by the command text.</summary>
    private const string CommandToken = "{command}";

    /// <summary>Upper bound on returned output; longer output is truncated with a marker.</summary>
    private const int MaxOutputChars = 8 * 1024;

    /// <summary>
    /// A human-friendly name for the configured shell, used in the approval prompt (e.g. "PowerShell",
    /// or the executable name for a custom shell).
    /// </summary>
    public static string ShellDisplayName(FloatyConfig config) => config.ExecShell switch
    {
        ExecShellKind.PowerShell => "PowerShell",
        ExecShellKind.Pwsh => "PowerShell Core (pwsh)",
        ExecShellKind.Cmd => "Command Prompt (cmd)",
        ExecShellKind.Bash => "bash",
        ExecShellKind.Zsh => "zsh",
        ExecShellKind.Sh => "sh",
        ExecShellKind.Custom => string.IsNullOrWhiteSpace(config.ExecCustomShellPath)
            ? "custom shell"
            : Path.GetFileName(config.ExecCustomShellPath),
        _ => "shell",
    };

    /// <summary>
    /// Resolves the configured shell into an executable file name and the full argument list needed to run
    /// <paramref name="command"/>. Throws <see cref="InvalidOperationException"/> when a custom shell is
    /// selected but no executable path is configured.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Args) Resolve(FloatyConfig config, string command)
    {
        switch (config.ExecShell)
        {
            case ExecShellKind.PowerShell:
                return ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command });
            case ExecShellKind.Pwsh:
                return ("pwsh", new[] { "-NoProfile", "-NonInteractive", "-Command", command });
            case ExecShellKind.Cmd:
                return ("cmd.exe", new[] { "/c", command });
            case ExecShellKind.Bash:
                return ("bash", new[] { "-c", command });
            case ExecShellKind.Zsh:
                return ("zsh", new[] { "-lc", command });
            case ExecShellKind.Sh:
                return ("sh", new[] { "-c", command });
            case ExecShellKind.Custom:
                if (string.IsNullOrWhiteSpace(config.ExecCustomShellPath))
                    throw new InvalidOperationException("No custom shell executable is configured in Settings.");
                return (config.ExecCustomShellPath, BuildCustomArgs(config.ExecCustomShellArgs, command));
            default:
                throw new InvalidOperationException($"Unsupported shell '{config.ExecShell}'.");
        }
    }

    // Splits the custom argument template on whitespace and substitutes {command}. When no token is present
    // the command is appended as a single trailing argument so simple templates like "-c" still work.
    private static IReadOnlyList<string> BuildCustomArgs(string template, string command)
    {
        template ??= string.Empty;
        var parts = template.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (!template.Contains(CommandToken, StringComparison.Ordinal))
        {
            var list = new List<string>(parts) { command };
            return list;
        }

        return parts
            .Select(p => p.Replace(CommandToken, command, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Runs <paramref name="command"/> through the configured shell and returns combined stdout+stderr
    /// prefixed with the exit code. The process is killed (with its child tree) if it exceeds
    /// <paramref name="timeout"/>. Failures (missing shell, launch errors) return a readable message rather
    /// than throwing.
    /// </summary>
    public static async Task<string> RunAsync(
        FloatyConfig config,
        string command,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "No command was provided.";

        string fileName;
        IReadOnlyList<string> args;
        try
        {
            (fileName, args) = Resolve(config, command);
        }
        catch (Exception ex)
        {
            return $"Could not resolve shell: {ex.Message}";
        }

        var workDir = ResolveWorkingDirectory(workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                return $"Failed to start '{fileName}'.";
        }
        catch (Exception ex)
        {
            return $"Failed to start '{fileName}': {ex.Message}";
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            if (!timedOut)
                throw; // caller-requested cancellation
        }

        return Format(process, stdout.ToString(), stderr.ToString(), timedOut, timeout);
    }

    private static string ResolveWorkingDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && Directory.Exists(requested))
            return requested;

        try
        {
            return FloatyPaths.Home;
        }
        catch
        {
            return Environment.CurrentDirectory;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: the process may have exited between the check and the kill.
        }
    }

    private static string Format(Process process, string stdout, string stderr, bool timedOut, TimeSpan timeout)
    {
        var sb = new StringBuilder();

        if (timedOut)
            sb.AppendLine($"Command timed out after {timeout.TotalSeconds:0}s and was terminated.");
        else
            sb.AppendLine($"Exit code: {SafeExitCode(process)}");

        var output = stdout.TrimEnd();
        var error = stderr.TrimEnd();

        if (output.Length > 0)
        {
            sb.AppendLine("stdout:");
            sb.AppendLine(output);
        }

        if (error.Length > 0)
        {
            sb.AppendLine("stderr:");
            sb.AppendLine(error);
        }

        if (output.Length == 0 && error.Length == 0 && !timedOut)
            sb.AppendLine("(no output)");

        return Truncate(sb.ToString().TrimEnd());
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch
        {
            return "unknown";
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxOutputChars
            ? text
            : text[..MaxOutputChars] + $"\n… (output truncated at {MaxOutputChars} characters)";
}
