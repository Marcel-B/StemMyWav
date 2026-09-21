using System.ComponentModel.DataAnnotations;

namespace StemMyWav.Gateway.Configuration;

/// <summary>Adresse und Schlüssel der Mac-API hinter Tailscale Serve.</summary>
public sealed class MacBackendOptions
{
    public const string Section = "MacBackend";

    [Required]
    public string? Url { get; set; }

    /// <summary>Wird beim Start aus MacBackend:ApiKey oder MacBackend:ApiKeyFile gefüllt.</summary>
    [Required]
    public string? ApiKey { get; set; }
}
