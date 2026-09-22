using StemMyWav.Gateway.Catalog;
using StemMyWav.Gateway.Jobs;

namespace StemMyWav.Gateway.Http;

/// <summary>Die Antwortkörper der Gateway-API. Die Endpunkte geben genau diese Typen zurück,
/// damit das OpenAPI-Dokument nicht von der tatsächlichen Antwort abweichen kann.</summary>
public sealed record HealthResponse(string Status);

public sealed record JobAcceptedResponse(Guid Id, JobStatus Status, string Model);

public sealed record JobStatusResponse(
    Guid Id,
    JobStatus Status,
    string Model,
    IReadOnlyList<string> Stems,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>Ein Modell aus dem Katalog. Die Datei, die der Mac lädt, steht bewusst nicht darin:
/// nach außen zählt die Kennung, der Dateiname ist eine Angelegenheit des Macs.</summary>
public sealed record ModelResponse(
    string Id,
    string Name,
    string Family,
    ModelTask Task,
    IReadOnlyList<string> Stems,
    ModelSpeed Speed,
    double? RealtimeFactor,
    bool Measured,
    IReadOnlyDictionary<string, double>? PublishedSdr,
    string? Notes,
    bool IsDefault);
