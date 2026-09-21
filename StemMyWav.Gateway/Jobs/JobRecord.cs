namespace StemMyWav.Gateway.Jobs;

/// <summary>Der je Auftrag als job.json abgelegte Zustand.</summary>
public sealed class JobRecord
{
    public Guid Id { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public bool Dereverb { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptUtc { get; set; } = DateTimeOffset.UtcNow;
}
