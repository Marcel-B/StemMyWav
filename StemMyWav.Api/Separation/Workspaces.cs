using System.Collections.Concurrent;

namespace StemMyWav.Api.Separation;

/// <summary>Vergibt die Arbeitsverzeichnisse einer Trennung und räumt sie wieder ab.
/// Die vergebenen Verzeichnisse sind bekannt, damit das Aufräumen verwaister Ordner
/// niemals eine laufende Anfrage trifft.</summary>
public sealed class Workspaces(string root)
{
    public const string Prefix = "stemmywav-";

    private readonly ConcurrentDictionary<string, byte> _live = new(StringComparer.Ordinal);

    public string Root { get; } = Path.GetFullPath(root);

    public string Create()
    {
        var path = Path.Combine(Root, Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _live[path] = 0;
        return path;
    }

    /// <summary>Gibt ein Verzeichnis frei und löscht es. Schlägt das Löschen fehl, bleibt der
    /// Ordner als verwaist liegen und wird beim nächsten Durchgang eingesammelt.</summary>
    public void Release(string path)
    {
        _live.TryRemove(path, out _);
        try { Directory.Delete(path, true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Löscht Verzeichnisse, die ein beendeter Prozess hinterlassen hat. Verschont
    /// alles, was dieser Prozess vergeben hat, und alles, was jünger als das Mindestalter ist —
    /// letzteres könnte einer zweiten Instanz gehören, etwa während eines Neustarts.</summary>
    public int RemoveOrphans(TimeSpan minimumAge)
    {
        if (!Directory.Exists(Root)) return 0;
        var cutoff = DateTime.UtcNow - minimumAge;
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(Root, Prefix + "*"))
        {
            if (_live.ContainsKey(directory)) continue;
            if (Directory.GetLastWriteTimeUtc(directory) > cutoff) continue;
            try
            {
                Directory.Delete(directory, true);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Beim nächsten Durchgang erneut versuchen.
            }
        }
        return removed;
    }
}
