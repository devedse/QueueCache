using System.Diagnostics;

namespace QueueCache.Developer.Verification;

public sealed record ProcessResult(int ExitCode, string Output, string Error);
public sealed record ProcessIdentity(int Pid, DateTime StartedUtc);

/// <summary>Never invokes a shell. Owns only the process tree it started, including on timeout/cancellation.</summary>
public static class OwnedProcess
{
    public static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string prefix, TimeSpan timeout, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        RunStorage.AtomicJson(prefix + ".command.json", new { Executable = executable, Arguments = arguments,
            Started = DateTimeOffset.UtcNow, TimeoutSeconds = timeout.TotalSeconds });
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start " + executable);
        RunStorage.AtomicJson(prefix + ".process.json", new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime()));
        await using var stdout = File.Create(prefix + ".stdout.txt");
        await using var stderr = File.Create(prefix + ".stderr.txt");
        var output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var error = process.StandardError.BaseStream.CopyToAsync(stderr);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // Bounded even when a kernel request cannot be cancelled. Never free another process's buffers.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { throw new IOException($"Owned PID {process.Id} did not exit. Stop testing; inspect the VM before recovery."); }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException($"Process deadline exceeded: {executable}; evidence: {prefix}");
        }
        finally
        {
            RunStorage.AtomicJson(prefix + ".exit.json", new { FinishedUtc = DateTimeOffset.UtcNow,
                Exited = process.HasExited, ExitCode = process.HasExited ? (int?)process.ExitCode : null });
            // A descendant can inherit the pipe; it must not hold the coordinator forever.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { throw new IOException("Output pipes did not close; collection incomplete: " + prefix); }
        }
        await stdout.FlushAsync(CancellationToken.None); await stderr.FlushAsync(CancellationToken.None);
        await stdout.DisposeAsync(); await stderr.DisposeAsync();
        return new(process.ExitCode, await File.ReadAllTextAsync(prefix + ".stdout.txt", token),
            await File.ReadAllTextAsync(prefix + ".stderr.txt", token));
    }

    public static void EnsureStopped(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.process.json"))
        {
            var identity = System.Text.Json.JsonSerializer.Deserialize<ProcessIdentity>(File.ReadAllText(file))!;
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime() == identity.StartedUtc)
                    throw new IOException($"Owned PID {identity.Pid} is still alive. Recovery refused; inspect {file}.");
            }
            catch (ArgumentException) { /* PID no longer exists */ }
        }
    }
}
