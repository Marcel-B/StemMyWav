using System.ComponentModel.DataAnnotations;

namespace StemMyWav.Api.Configuration;

/// <summary>Der Schlüssel, den der Gateway als X-Api-Key mitschicken muss.</summary>
public sealed class MacApiOptions
{
    public const string Section = "MacApi";

    [Required]
    public string? Key { get; set; }
}
