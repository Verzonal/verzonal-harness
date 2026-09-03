using System.Diagnostics;
using System.Text;
using Dsh.Cordis;

namespace Dsh.Shell;

/// <summary>One command to run.</summary>
/// <param name="Command">The command line, in the shell's own syntax.</param>
/// <param name="WorkingDirectory">Where it runs; the workspace when omitted.</param>
/// <param name="TimeoutMs">How long it may run before it is killed.</param>
public sealed record ShellRequest(string Command, string? WorkingDirectory = null, int? TimeoutMs = null);

/// <summary>What running a command produced.</summary>
/// <param name="Output">Its combined standard output and error.</param>
/// <param name="ExitCode">Its exit status, when it exited on its own.</param>
/// <param name="TimedOut">Whether it was killed for running too long.</param>
/// <param name="Truncated">Whether the output shown is the tail of a longer stream.</param>
/// <param name="TotalBytes">How many bytes the command actually wrote.</param>
public sealed record ShellResult(
    string Output,
    int? ExitCode,
    bool TimedOut = false,
    bool Truncated = false,
    long TotalBytes = 0);

/// <summary>
/// The shell capability's Service Definition.
/// </summary>
/// <remarks>
/// Each call gets a fresh shell, so nothing carries between commands — no lingering
/// working directory, no exported variable. A model that could change state
/// invisibly between calls would make every later call unpredictable, so the working
/// directory is a parameter instead.
/// </remarks>
public abstract class ShellService : Service
{
    /// <param name="ctx">The mounting plugin's context.</param>
    protected ShellService(Context ctx) : base(ctx, ShellKeys.Service) { }

    /// <summary>The tool name this shell is exposed to the model as, such as <c>pwsh</c> or <c>bash</c>.</summary>
    public abstract string ShellName { get; }

    /// <summary>Whether this shell is actually available on the machine.</summary>
    public abstract bool IsAvailable { get; }

    /// <summary>
    /// Run one command.
    /// </summary>
    /// <param name="request">What to run.</param>
    /// <param name="cancellationToken">Cancels the command and kills the process.</param>
    /// <returns>What it produced.</returns>
    public abstract Task<ShellResult> RunAsync(ShellRequest request, CancellationToken cancellationToken);
}

/// <summary>The context key the shell capability is published under.</summary>
public static class ShellKeys
{
    /// <summary>The context key a shell provider claims.</summary>
    public const string Service = "shell";
}

/// <summary>How the local shell provider behaves.</summary>
/// <param name="Workspace">The directory commands run in when none is named.</param>
/// <param name="DefaultTimeoutMs">How long a command may run when the caller names no limit.</param>
/// <param name="MaxTimeoutMs">The longest a caller may ask for.</param>
/// <param name="MaxOutputBytes">How much output is kept before only the tail is shown.</param>
public sealed record LocalShellConfig(
    string Workspace,
    int DefaultTimeoutMs = 120_000,
    int MaxTimeoutMs = 600_000,
    int MaxOutputBytes = 60_000);

/// <summary>
/// Runs commands through the machine's own shell — PowerShell on Windows, bash
/// elsewhere.
/// </summary>
public sealed class LocalShell : ShellService
{
    private readonly LocalShellConfig _config;
    private readonly string _executable;
    private readonly string[] _leadingArguments;

    /// <param name="ctx">The mounting plugin's context.</param>
    /// <param name="config">How the provider behaves.</param>
    public LocalShell(Context ctx, LocalShellConfig config) : base(ctx)
    {
        _config = config;
        if (OperatingSystem.IsWindows())
        {
            ShellName = "pwsh";
            _executable = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe") ?? "powershell.exe";
            _leadingArguments = ["-NoProfile", "-NonInteractive", "-Command"];
        }
        else
        {
            ShellName = "bash";
            _executable = FindOnPath("bash") ?? "/bin/bash";
            _leadingArguments = ["-lc"];
        }
    }

    /// <inheritdoc />
    public override string ShellName { get; }

    /// <inheritdoc />
    public override bool IsAvailable => File.Exists(_executable) || FindOnPath(Path.GetFileName(_executable)) is not null;

    /// <inheritdoc />
    public override async Task<ShellResult> RunAsync(ShellRequest request, CancellationToken cancellationToken)
    {
        var timeout = Math.Clamp(
            request.TimeoutMs ?? _config.DefaultTimeoutMs,
            1,
            _config.MaxTimeoutMs);

        var workingDirectory = request.WorkingDirectory is null
            ? _config.Workspace
            : Path.GetFullPath(Path.IsPathRooted(request.WorkingDirectory)
                ? request.WorkingDirectory
                : Path.Combine(_config.Workspace, request.WorkingDirectory));

        var start = new ProcessStartInfo
        {
            FileName = _executable,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : _config.Workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in _leadingArguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(request.Command);

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        long totalBytes = 0;
        var gate = new object();

        void Capture(string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                totalBytes += line.Length + 1;
                output.AppendLine(line);
            }
        }

        process.OutputDataReceived += (_, args) => Capture(args.Data);
        process.ErrorDataReceived += (_, args) => Capture(args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            KillTree(process);

            // The exit is awaited even after the kill, so the capture callbacks have
            // finished and the output below is the whole of what the command wrote.
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The process ended between the kill and this wait; nothing to await.
            }
        }

        string text;
        lock (gate) text = output.ToString();

        var truncated = text.Length > _config.MaxOutputBytes;
        if (truncated)
        {
            // The tail is what matters: a failing command's error is at the end.
            text = "[earlier output truncated]\n" + text[^_config.MaxOutputBytes..];
        }

        int? exitCode = null;
        if (!timedOut && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = null;
            }
        }

        return new ShellResult(text.TrimEnd('\n'), exitCode, timedOut, truncated, totalBytes);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (NotSupportedException)
        {
            // The platform cannot walk the tree; the direct child is already killed.
        }
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Mount the local shell provider.</summary>
    /// <param name="config">How the provider behaves.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(LocalShellConfig config)
        => ServicePlugin.Create<ShellService>(
            "shell-local",
            ShellKeys.Service,
            ctx => new LocalShell(ctx, config));
}
