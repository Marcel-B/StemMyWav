using System.Text.Json.Serialization;

namespace StemMyWav.Gateway.Catalog;

/// <summary>Grobe Einordnung der Rechenzeit. Die Grenzen liegen beim Echtzeitfaktor 4, 1,5 und 0,7,
/// damit die Klasse eine Entscheidung trägt: fast rechnet schneller als der Titel dauert,
/// verySlow braucht mehr als das Anderthalbfache der Spieldauer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelSpeed>))]
public enum ModelSpeed
{
    [JsonStringEnumMemberName("fast")] Fast,
    [JsonStringEnumMemberName("moderate")] Moderate,
    [JsonStringEnumMemberName("slow")] Slow,
    [JsonStringEnumMemberName("verySlow")] VerySlow
}

/// <summary>Wofür ein Modell trainiert wurde. Die Namen sind Teil der HTTP-Antwort und deshalb
/// ausgeschrieben statt aus dem Enum abgeleitet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelTask>))]
public enum ModelTask
{
    [JsonStringEnumMemberName("vocals")] Vocals,
    [JsonStringEnumMemberName("instrumental")] Instrumental,
    [JsonStringEnumMemberName("karaoke")] Karaoke,
    [JsonStringEnumMemberName("4stem")] FourStem,
    [JsonStringEnumMemberName("6stem")] SixStem,
    [JsonStringEnumMemberName("drums")] Drums
}

/// <summary>Ein Eintrag aus models.json.</summary>
public sealed record SeparationModel
{
    /// <summary>Die Kennung, die ein Aufrufer mitschickt.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Die Datei, die mlx-audio-separator lädt. Nur die Mac-API braucht sie.</summary>
    public required string Filename { get; init; }

    public required string Family { get; init; }

    public required ModelTask Task { get; init; }

    /// <summary>Die Namen, unter denen die Stems im Ergebnis-ZIP liegen, ohne Endung.</summary>
    public required IReadOnlyList<string> Stems { get; init; }

    public required ModelSpeed Speed { get; init; }

    /// <summary>Audiodauer geteilt durch Rechenzeit; null, wenn nicht gemessen.</summary>
    public double? RealtimeFactor { get; init; }

    /// <summary>False bedeutet, dass die Einstufung aus dem Vergleich mit einem gemessenen
    /// Modell derselben Familie stammt.</summary>
    public bool Measured { get; init; }

    /// <summary>SDR-Werte aus dem Testset von audio-separator, nicht mit MUSDB18-Zahlen vergleichbar.</summary>
    public IReadOnlyDictionary<string, double>? PublishedSdr { get; init; }

    public string? Notes { get; init; }

    /// <summary>Nur Modelle mit Gesangs-Stem können anschließend enthallt werden.</summary>
    public bool ProducesVocals => Stems.Contains("vocals", StringComparer.OrdinalIgnoreCase);
}
