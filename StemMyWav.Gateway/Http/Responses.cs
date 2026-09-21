using StemMyWav.Gateway.Jobs;

namespace StemMyWav.Gateway.Http;

/// <summary>Die Antwortkörper der Gateway-API. Die Endpunkte geben genau diese Typen zurück,
/// damit das OpenAPI-Dokument nicht von der tatsächlichen Antwort abweichen kann.</summary>
public sealed record HealthResponse(string Status);

public sealed record JobAcceptedResponse(Guid Id, JobStatus Status);

public sealed record JobStatusResponse(
    Guid Id,
    JobStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
