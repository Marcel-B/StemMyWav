namespace StemMyWav.Gateway.Configuration;

/// <summary>Liest ein Geheimnis entweder direkt aus der Konfiguration oder, wenn ein Schlüssel
/// mit dem Zusatz File gesetzt ist, aus der dort genannten Datei.</summary>
public static class Secrets
{
    public static string Read(IConfiguration configuration, string name)
    {
        var file = configuration[name + "File"];
        var value = file is { Length: > 0 } ? File.ReadAllText(file).TrimEnd('\r', '\n') : configuration[name];
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} muss gesetzt sein.");
        return value;
    }
}
