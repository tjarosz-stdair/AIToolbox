using System.Diagnostics;
using System.Text;

namespace AIToolbox.Ductwork.Worker;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs the heavy part of a task in a process of its own. Behind an interface so the rest can be tested without Python.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Starts a child with nothing it was not given: no shell, an empty environment apart from what is passed (so the API key and
/// anything else in the worker's own environment never reach it), the scratch folder as its working and temp folder, bounded
/// output, a timeout, and the whole process tree killed on timeout or cancellation.
/// </summary>
public sealed class ChildProcessRunner : IProcessRunner
{
    private const int MaxCapturedChars = 1_000_000;

    // The few variables a child needs on each platform to start at all. Nothing here can carry a secret.
    private static readonly string[] Inherited = ["SystemRoot", "SYSTEMROOT", "windir", "PATH", "Path", "PATHEXT", "COMSPEC"];

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        info.Environment.Clear();
        foreach (var name in Inherited)
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["TEMP"] = workingDirectory;
        info.Environment["TMP"] = workingDirectory;
        info.Environment["TMPDIR"] = workingDirectory;
        foreach (var (name, value) in environment) info.Environment[name] = value;

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(error, e.Data);

        if (!process.Start()) throw new TaskFailedException($"Could not start {fileName}.", retryable: false);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit();                                   // lets the redirected output finish flushing
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TaskFailedException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalMinutes:0.#} minutes and was stopped.", retryable: true);
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null || builder.Length >= MaxCapturedChars) return;
        lock (builder) builder.AppendLine(line);
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
