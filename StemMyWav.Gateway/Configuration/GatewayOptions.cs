using System.ComponentModel.DataAnnotations;

namespace StemMyWav.Gateway.Configuration;

/// <summary>Einstellungen aus dem Abschnitt Gateway. Sie werden beim Start geprüft, damit eine
/// Fehlkonfiguration sofort auffällt und nicht erst beim ersten Auftrag.</summary>
public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    [Required]
    public string DataDirectory { get; set; } = "data";

    /// <summary>Wird beim Start aus Gateway:ApiKey oder Gateway:ApiKeyFile gefüllt.</summary>
    [Required]
    public string? ApiKey { get; set; }

    /// <summary>Wie viele Aufträge gleichzeitig warten oder laufen dürfen.</summary>
    [Range(1, 100)]
    public int MaxPendingJobs { get; set; } = 2;

    /// <summary>Frist, nach der ein nicht bestätigter abgeschlossener Auftrag entfernt wird.</summary>
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 1;

    /// <summary>Frist, nach der ein Auftrag den unerreichbaren Mac aufgibt. Gleitkomma, damit
    /// Tests die Grenze in Sekunden ausreizen können; 0 ist ausgeschlossen, weil sonst schon der
    /// erste Fehlversuch die hochgeladene Datei verwerfen würde.</summary>
    [Range(0.001, 8760)]
    public double MaxQueueHours { get; set; } = 24;
}
