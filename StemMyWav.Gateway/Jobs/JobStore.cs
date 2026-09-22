using System.Text.Json;
using Microsoft.Extensions.Options;
using StemMyWav.Gateway.Configuration;

namespace StemMyWav.Gateway.Jobs;

/// <summary>Hält die Aufträge als Verzeichnis je Auftrag auf der Platte und wacht über die
/// Kapazität der Warteschlange. Die Gültigkeit der hochgeladenen Datei prüft die API-Schicht.</summary>
public sealed class JobStore(IOptions<GatewayOptions> options)
{
    private readonly string _root = Path.GetFullPath(options.Value.DataDirectory);
    private readonly int _maxPending = options.Value.MaxPendingJobs;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _createGate = new(1, 1);

    /// <summary>Legt einen Auftrag an und schreibt die Eingabe auf die Platte. Liefert null,
    /// wenn die Warteschlange voll ist. Der bereits gelesene Anfang des Bodys wird vorangestellt.</summary>
    public async Task<JobRecord?> CreateAsync(ReadOnlyMemory<byte> prefix, Stream rest, string model, string format, bool dereverb, CancellationToken token)
    {
        await _createGate.WaitAsync(token);
        try { return await CreateCoreAsync(prefix, rest, model, format, dereverb, token); }
        finally { _createGate.Release(); }
    }

    private async Task<JobRecord?> CreateCoreAsync(ReadOnlyMemory<byte> prefix, Stream rest, string model, string format, bool dereverb, CancellationToken token)
    {
        Directory.CreateDirectory(_root);
        if (CountActive() >= _maxPending) return null;
        var job = new JobRecord { Id = Guid.NewGuid(), Model = model, InputFormat = format, Dereverb = dereverb };
        var dir = Path.Combine(_root, job.Id.ToString("D"));
        Directory.CreateDirectory(dir);
        try
        {
            await using (var file = File.Create(Path.Combine(dir, "input." + format)))
            {
                await file.WriteAsync(prefix, token);
                await rest.CopyToAsync(file, token);
            }
            Save(job);
            return job;
        }
        catch { Directory.Delete(dir, true); throw; }
    }

    /// <summary>Alle bekannten Aufträge, jüngste zuerst.</summary>
    public IReadOnlyList<JobRecord> All()
    {
        if (!Directory.Exists(_root)) return [];
        var jobs = new List<JobRecord>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
            if (Guid.TryParse(Path.GetFileName(dir), out var id) && Read(id) is { } job)
                jobs.Add(job);
        jobs.Sort((left, right) => right.CreatedUtc.CompareTo(left.CreatedUtc));
        return jobs;
    }

    public IEnumerable<JobRecord> Pending()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            if (Guid.TryParse(Path.GetFileName(dir), out var id) && Read(id) is { } job &&
                job.Status is JobStatus.Queued or JobStatus.Processing && job.NextAttemptUtc <= DateTimeOffset.UtcNow)
                yield return job;
        }
    }

    private int CountActive()
    {
        if (!Directory.Exists(_root)) return 0;
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(_root))
            if (Guid.TryParse(Path.GetFileName(dir), out var id) && Read(id) is { } job &&
                job.Status is JobStatus.Queued or JobStatus.Processing)
                count++;
        return count;
    }

    public JobRecord? Read(Guid id)
    {
        var path = Path.Combine(_root, id.ToString("D"), "job.json");
        lock (_sync) return File.Exists(path) ? JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(path)) : null;
    }

    /// <summary>Schreibt den Zustand atomar. Ein inzwischen abgebrochener Auftrag wird dabei
    /// nicht wiederbelebt: fehlt sein Verzeichnis, verfällt der Schreibvorgang.</summary>
    public void Save(JobRecord job)
    {
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        var dir = Path.Combine(_root, job.Id.ToString("D"));
        var target = Path.Combine(dir, "job.json");
        var temp = target + ".tmp";
        lock (_sync)
        {
            if (!Directory.Exists(dir)) return;
            File.WriteAllText(temp, JsonSerializer.Serialize(job));
            File.Move(temp, target, true);
        }
    }

    /// <summary>Die hochgeladene Datei behält ihre Endung, damit FFmpeg und die MLX-CLI auf dem
    /// Mac nicht über eine als .flac ausgegebene WAV stolpern.</summary>
    public string InputPath(JobRecord job) => Path.Combine(_root, job.Id.ToString("D"), "input." + job.InputFormat);
    public string ResultPath(Guid id) => Path.Combine(_root, id.ToString("D"), "stems.zip");

    public void RemoveInput(JobRecord job) => File.Delete(InputPath(job));

    /// <summary>Entfernt einen Auftrag, sofern der Worker ihn nicht gerade überträgt.</summary>
    public bool Delete(Guid id)
    {
        lock (_sync)
        {
            var job = Read(id);
            if (job is null || job.Status == JobStatus.Processing) return false;
            Directory.Delete(Path.Combine(_root, id.ToString("D")), true);
            return true;
        }
    }

    public void CleanupExpired(TimeSpan retention)
    {
        if (!Directory.Exists(_root)) return;
        var cutoff = DateTimeOffset.UtcNow - retention;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            if (Guid.TryParse(Path.GetFileName(dir), out var id) && Read(id) is { } job &&
                job.Status is JobStatus.Completed or JobStatus.Failed && job.UpdatedUtc < cutoff)
                lock (_sync)
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
