using System.ComponentModel.DataAnnotations;

namespace StemMyWav.Api.Configuration;

/// <summary>Einstellungen aus dem Abschnitt Separator.</summary>
public sealed class SeparatorOptions
{
    public const string Section = "Separator";

    /// <summary>Pfad zur MLX-CLI.</summary>
    [Required]
    public string Executable { get; set; } = "mlx-audio-separator";

    [Required]
    public string Model { get; set; } = "model_bs_roformer_ep_317_sdr_12.9755.ckpt";

    [Required]
    public string DereverbModel { get; set; } = "dereverb_mel_band_roformer_anvuew_sdr_19.1729.ckpt";

    /// <summary>Modell-Cache. Leer bedeutet, dass die CLI ihren eigenen Standardpfad verwendet.</summary>
    public string? ModelDirectory { get; set; }
}
