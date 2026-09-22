using System.Reflection;
using System.Text.Json;

namespace StemMyWav.Gateway.Catalog;

/// <summary>Der Modellkatalog aus der eingebetteten models.json. Gateway und Mac-API tragen
/// bewusst je eine Kopie dieser Klasse: geteilt wird die Datendatei, nicht ein Projekt.
/// Nur so kennt der Gateway die Kennungen auch dann, wenn der Mac nicht erreichbar ist,
/// und kann eine unbekannte Kennung sofort ablehnen, statt eine Datei stundenlang zu halten.</summary>
public sealed class ModelCatalog
{
    private const string Resource = "models.json";

    private readonly Dictionary<string, SeparationModel> _byId;

    private ModelCatalog(IReadOnlyList<SeparationModel> models, string defaultId)
    {
        Models = models;
        DefaultId = defaultId;
        _byId = models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Alle Modelle in der Reihenfolge der Datei.</summary>
    public IReadOnlyList<SeparationModel> Models { get; }

    /// <summary>Die Kennung, die gilt, wenn ein Aufrufer keine mitschickt.</summary>
    public string DefaultId { get; }

    public static ModelCatalog Load()
    {
        using var stream = typeof(ModelCatalog).GetTypeInfo().Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Die eingebettete {Resource} fehlt im Build.");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"{Resource} ist leer.");
        var duplicate = file.Models.GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Doppelte Modellkennung in {Resource}: {duplicate.Key}.");
        var catalog = new ModelCatalog(file.Models, file.Default);
        if (catalog.Find(file.Default) is null)
            throw new InvalidOperationException($"Die Voreinstellung {file.Default} steht nicht in {Resource}.");
        return catalog;
    }

    public SeparationModel? Find(string? id) =>
        id is { Length: > 0 } && _byId.TryGetValue(id.Trim(), out var model) ? model : null;

    /// <summary>Löst die konfigurierte Voreinstellung auf. Eine unbekannte Kennung bricht den
    /// Start ab, damit sie nicht erst beim ersten Auftrag auffällt.</summary>
    public SeparationModel ResolveDefault(string? configured)
    {
        var id = configured is { Length: > 0 } ? configured : DefaultId;
        return Find(id) ?? throw new InvalidOperationException(
            $"Unbekannte Modellkennung {id}. Bekannt sind: {string.Join(", ", Models.Select(model => model.Id))}.");
    }

    private sealed record CatalogFile(IReadOnlyList<SeparationModel> Models, string Default);
}
