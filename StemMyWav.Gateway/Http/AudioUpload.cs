namespace StemMyWav.Gateway.Http;

/// <summary>Erkennt die hochgeladene Datei am Content-Type und an den ersten Bytes, bevor bis zu
/// 512 MiB auf die Platte gehen. Beide Dienste tragen bewusst eine eigene Kopie: ein gemeinsames
/// Projekt für so wenige Zeilen würde Build und Auslieferung mehr belasten, als es spart.</summary>
public static class AudioUpload
{
    /// <summary>So viele Bytes braucht die Erkennung: "RIFF" + Länge + "WAVE". Für FLAC reichen vier.</summary>
    public const int PrefixLength = 12;

    /// <summary>Die Endung zum Content-Type, oder null, wenn der Typ nicht angenommen wird.</summary>
    public static string? Extension(string? contentType) => contentType switch
    {
        "audio/flac" or "audio/x-flac" => "flac",
        "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => "wav",
        _ => null
    };

    /// <summary>Prüft die Magic Bytes gegen die aus dem Content-Type abgeleitete Endung.</summary>
    public static bool Matches(string extension, ReadOnlySpan<byte> prefix) => extension switch
    {
        "flac" => prefix.Length >= 4 && prefix[..4].SequenceEqual("fLaC"u8),
        "wav" => prefix.Length >= PrefixLength && prefix[..4].SequenceEqual("RIFF"u8) && prefix[8..12].SequenceEqual("WAVE"u8),
        _ => false
    };

    public const string UnsupportedType = "Content-Type muss audio/flac oder audio/wav sein.";

    public static string Invalid(string extension) => $"Ungültige {extension.ToUpperInvariant()}-Datei.";
}
