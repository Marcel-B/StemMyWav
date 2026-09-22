using System.ComponentModel.DataAnnotations;

namespace StemMyWav.Api.Configuration;

/// <summary>Einstellungen aus dem Abschnitt Separator.</summary>
public sealed class SeparatorOptions
{
    public const string Section = "Separator";

    /// <summary>Pfad zur MLX-CLI.</summary>
    [Required]
    public string Executable { get; set; } = "mlx-audio-separator";

    /// <summary>Kennung aus models.json für Anfragen ohne model-Parameter. Leer bedeutet die
    /// Voreinstellung des Katalogs. Eine unbekannte Kennung bricht den Start ab.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Das De-Reverb-Modell steht nicht im Katalog: es ist keine Wahl des Aufrufers,
    /// sondern der Nachbearbeitungsschritt hinter dereverb=true.</summary>
    [Required]
    public string DereverbModel { get; set; } = "dereverb_mel_band_roformer_anvuew_sdr_19.1729.ckpt";

    /// <summary>Modell-Cache. Leer bedeutet, dass die CLI ihren eigenen Standardpfad verwendet.</summary>
    public string? ModelDirectory { get; set; }
}
