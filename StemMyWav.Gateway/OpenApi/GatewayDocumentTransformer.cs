using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace StemMyWav.Gateway.OpenApi;

/// <summary>Ergänzt das erzeugte Dokument um Titel, Beschreibung und das Schlüsselschema und
/// hängt letzteres an jeden Pfad unterhalb von /api.</summary>
public sealed class GatewayDocumentTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeName = "GatewayApiKey";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "StemMyWav Gateway API";
        document.Info.Description = "Asynchrone Trennung einer FLAC- oder WAV-Datei in 48-kHz-/16-Bit-Stereo-WAV-Stems. GET /api/models nennt die wählbaren Trennmodelle; die Kennung gehört in den model-Parameter von POST /api/jobs. YuE_To_Logic lädt das Ergebnis-ZIP herunter und bestätigt den Import mit DELETE.";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            [SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Api-Key",
                Description = "Gateway__ApiKey aus der Gateway-Konfiguration."
            }
        };
        foreach (var (path, item) in document.Paths)
        {
            if (!path.StartsWith("/api/", StringComparison.Ordinal)) continue;
            if (item.Operations is null) continue;
            foreach (var operation in item.Operations.Values)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
                });
            }
        }
        return Task.CompletedTask;
    }
}
