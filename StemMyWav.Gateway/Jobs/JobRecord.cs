namespace StemMyWav.Gateway.Jobs;

/// <summary>Der je Auftrag als job.json abgelegte Zustand.</summary>
public sealed class JobRecord
{
    public Guid Id { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Die Modellkennung aus models.json. Der Anfangswert gilt für Aufträge, die noch
    /// vor der Modellauswahl angelegt wurden und deshalb kein Feld dafür in ihrer job.json haben.</summary>
    public string Model { get; set; } = "bs-roformer-viperx-1297";

    /// <summary>Die Endung der hochgeladenen Datei, flac oder wav. Ältere job.json kennen das
    /// Feld nicht; deren Eingabe liegt als input.flac.</summary>
    public string InputFormat { get; set; } = "flac";

    public bool Dereverb { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptUtc { get; set; } = DateTimeOffset.UtcNow;
}
