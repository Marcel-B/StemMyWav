using System.Text.Json;

public sealed class JobRecord
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "queued";
    public bool Dereverb { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JobStore(IConfiguration configuration)
{
    private readonly string _root = Path.GetFullPath(configuration["Gateway:DataDirectory"] ?? "data");
    private readonly object _sync = new();
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private readonly int _maxPending = Math.Max(1, configuration.GetValue("Gateway:MaxPendingJobs", 2));

    public async Task<JobRecord> CreateAsync(Stream input, bool dereverb, CancellationToken token)
    {
        await _createGate.WaitAsync(token);
        try { return await CreateCoreAsync(input, dereverb, token); }
        finally { _createGate.Release(); }
    }

    private async Task<JobRecord> CreateCoreAsync(Stream input, bool dereverb, CancellationToken token)
    {
        Directory.CreateDirectory(_root);
        if (CountActive() >= _maxPending)
            throw new BadHttpRequestException("Warteschlange voll. Später erneut versuchen.", 429);
        var job = new JobRecord { Id = Guid.NewGuid(), Dereverb = dereverb };
        var dir = Path.Combine(_root, job.Id.ToString("D"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "input.flac");
            await using (var file = File.Create(source)) await input.CopyToAsync(file, token);
            await using (var file = File.OpenRead(source))
            {
                var header = new byte[4];
                if (await file.ReadAsync(header, token) != 4 || !header.AsSpan().SequenceEqual("fLaC"u8))
                    throw new BadHttpRequestException("Ungültige FLAC-Datei.", 400);
            }
            Save(job);
            return job;
        }
        catch { Directory.Delete(dir, true); throw; }
    }

    public IEnumerable<JobRecord> Pending()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            if (Guid.TryParse(Path.GetFileName(dir), out var id) && Read(id) is { } job &&
                job.Status is "queued" or "processing" && job.NextAttemptUtc <= DateTimeOffset.UtcNow)
                yield return job;
        }
    }

    private int CountActive()
    {
        if (!Directory.Exists(_root)) return 0;
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(_root))
            if (Guid.TryParse(Path.GetFileName(dir), out var id) && Read(id) is { } job &&
                job.Status is "queued" or "processing")
                count++;
        return count;
    }

    public JobRecord? Read(Guid id)
    {
        var path = Path.Combine(_root, id.ToString("D"), "job.json");
        lock (_sync) return File.Exists(path) ? JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(path)) : null;
    }

    public void Save(JobRecord job)
    {
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        var dir = Path.Combine(_root, job.Id.ToString("D"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "job.json");
        var temp = target + ".tmp";
        lock (_sync)
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(job));
            File.Move(temp, target, true);
        }
    }

    public string InputPath(Guid id) => Path.Combine(_root, id.ToString("D"), "input.flac");
    public string ResultPath(Guid id) => Path.Combine(_root, id.ToString("D"), "stems.zip");

    public void RemoveInput(Guid id) => File.Delete(InputPath(id));

    public bool DeleteTerminal(Guid id)
    {
        lock (_sync)
        {
            var job = Read(id);
            if (job is null || job.Status is not ("completed" or "failed")) return false;
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
                job.Status is "completed" or "failed" && job.UpdatedUtc < cutoff)
                Directory.Delete(dir, true);
        }
    }
}
