using System.Diagnostics;

namespace StemMyWav.Api.Separation;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {executable}.");
        using var cancellation = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var result = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {error}");
        return result;
    }
}
