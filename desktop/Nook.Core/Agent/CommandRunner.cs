using System.Diagnostics;
using System.Text;
using Nook.Core.Platform;

namespace Nook.Core.Agent;

public sealed record CommandResult(int? ExitCode, string Output, bool TimedOut, bool Truncated, bool Stopped);

/// <summary>Runs one shell command with a timeout and an output cap; cancelling the token kills the whole process tree.</summary>
public static class CommandRunner
{
    public static async Task<CommandResult> RunAsync(IPlatform platform, string command, string workingDir,
        TimeSpan timeout, int maxOutputChars, Action<string>? onOutput, CancellationToken ct)
    {
        var (file, args) = platform.Shell(command);
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TERM"] = "dumb";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PAGER"] = "cat";

        var output = new StringBuilder();
        var truncated = false;
        var gate = new object();
        void Append(string? line)
        {
            if (line == null) return;
            lock (gate)
            {
                if (output.Length >= maxOutputChars) { truncated = true; return; }
                var piece = line + "\n";
                if (output.Length + piece.Length > maxOutputChars)
                {
                    piece = piece[..(maxOutputChars - output.Length)];
                    truncated = true;
                }
                output.Append(piece);
            }
            onOutput?.Invoke(line + "\n");
        }

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => Append(e.Data);
        proc.ErrorDataReceived += (_, e) => Append(e.Data);
        try { proc.Start(); }
        catch (Exception e) { return new CommandResult(null, $"Could not start the shell: {e.Message}", false, false, false); }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try { proc.StandardInput.Close(); } catch { } // commands that wait for input get EOF instead of hanging

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        bool timedOut = false, stopped = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            stopped = !timedOut;
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        // Let the async readers flush their last lines.
        if (proc.HasExited) try { proc.WaitForExit(2000); } catch { }

        string text;
        lock (gate) text = output.ToString();
        return new CommandResult(proc.HasExited && !timedOut && !stopped ? proc.ExitCode : null, text, timedOut, truncated, stopped);
    }
}
