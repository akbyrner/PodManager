using System.Text.Json;

namespace PodManager.Core;

public sealed record LoggedFailure(string Key, string Song, string Reason, DateTime Time);

public sealed record TransferRun
{
    public DateTime Started { get; init; }
    public DateTime Finished { get; init; }
    /// <summary>"Full sync", "Selected", "Retry" ...</summary>
    public string Kind { get; init; } = "";
    public int Requested { get; init; }
    public int Converted { get; init; }
    public int Copied { get; init; }
    public long BytesCopied { get; init; }
    public int Failed => Failures.Count;

    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan Duration => Finished - Started;
    /// <summary>A problem that stopped the whole run (no space, server unreachable, cancelled...).</summary>
    public string? Note { get; init; }
    public List<LoggedFailure> Failures { get; init; } = [];

    public string Summary =>
        $"{Copied:N0} copied" + (Converted > 0 ? $", {Converted:N0} converted" : "") +
        (Failed > 0 ? $", {Failed:N0} failed" : "") + (Note is null ? "" : $" — {Note}");
}

/// <summary>
/// History of syncs, plus each song's most recent failure (cleared when it later syncs).
/// Saved in %LOCALAPPDATA%\PodManager\transfer-log.json.
/// </summary>
public sealed class TransferLog
{
    private const int KeepRuns = 100;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<TransferRun> Runs { get; set; } = [];
    public Dictionary<string, LoggedFailure> CurrentErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "transfer-log.json");

    public static TransferLog Load(string? path = null)
    {
        try
        {
            var p = path ?? DefaultPath;
            if (File.Exists(p))
            {
                var log = JsonSerializer.Deserialize<TransferLog>(File.ReadAllText(p), Json) ?? new();
                log.CurrentErrors = new Dictionary<string, LoggedFailure>(log.CurrentErrors, StringComparer.OrdinalIgnoreCase);
                return log;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException) { }
        return new();
    }

    public void Save(string? path = null)
    {
        var p = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, p, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Record(TransferRun run, IEnumerable<string> succeededKeys)
    {
        Runs.Insert(0, run);
        if (Runs.Count > KeepRuns) Runs.RemoveRange(KeepRuns, Runs.Count - KeepRuns);
        foreach (var key in succeededKeys) CurrentErrors.Remove(key);
        foreach (var f in run.Failures) CurrentErrors[f.Key] = f;
    }

    /// <summary>Forget errors for songs that no longer need syncing (e.g. marked never-sync).</summary>
    public void ClearErrors(IEnumerable<string> keys)
    {
        foreach (var key in keys) CurrentErrors.Remove(key);
    }

    public IReadOnlyDictionary<string, string> ErrorReasons =>
        CurrentErrors.ToDictionary(kv => kv.Key, kv => kv.Value.Reason, StringComparer.OrdinalIgnoreCase);
}
