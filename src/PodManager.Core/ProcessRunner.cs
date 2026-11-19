using System.Diagnostics;
using System.Text;

namespace PodManager.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    /// <param name="onStdOutLine">If given, called with each line of output as it arrives (from a background
    /// thread, one line at a time); the full output is still returned.</param>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken ct = default,
        Action<string>? onStdOutLine = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // ssh/sftp must never wait on a prompt
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {fileName}.");
        process.StandardInput.Close();
        var stdout = onStdOutLine is null
            ? process.StandardOutput.ReadToEndAsync(CancellationToken.None)
            : ReadLinesAsync(process.StandardOutput, onStdOutLine);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        var all = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            all.Append(line).Append('\n');
            onLine(line);
        }
        return all.ToString();
    }
}
