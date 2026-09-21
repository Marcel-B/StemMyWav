using System.Security.Cryptography;
using System.Text;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
builder.Services.AddSingleton<JobStore>();
builder.Services.AddHttpClient("mac", client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "StemMyWav Gateway API";
    document.Info.Description = "Asynchrone Trennung einer FLAC-Datei in WAV-Stems. YuE_To_Logic lädt das Ergebnis-ZIP herunter und bestätigt den Import mit DELETE.";
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
    {
        ["GatewayApiKey"] = new OpenApiSecurityScheme
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
                [new OpenApiSecuritySchemeReference("GatewayApiKey", document)] = []
            });
        }
    }
    return Task.CompletedTask;
}));
var app = builder.Build();

var clientKey = Secrets.Read(builder.Configuration, "Gateway:ApiKey");
var macUrl = builder.Configuration["MacBackend:Url"];
if (!Uri.TryCreate(macUrl, UriKind.Absolute, out var backend) ||
    (backend.Scheme != Uri.UriSchemeHttps && !(app.Environment.IsDevelopment() && backend.IsLoopback && backend.Scheme == Uri.UriSchemeHttp)))
    throw new InvalidOperationException("MacBackend:Url muss eine HTTPS-URL sein (z. B. Tailscale Serve).");
_ = Secrets.Read(builder.Configuration, "MacBackend:ApiKey");

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !ValidKey(context.Request.Headers["X-Api-Key"].ToString(), clientKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "StemMyWav Gateway v1"));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .WithSummary("Prüft, ob der Gateway-Prozess läuft")
    .WithDescription("Prüft nicht die Erreichbarkeit der Mac-API.")
    .Produces<HealthResponse>();
app.MapPost("/api/jobs", async (HttpContext context, JobStore store, bool? dereverb) =>
{
    if (context.Request.ContentType is not ("audio/flac" or "audio/x-flac"))
        return Results.Problem("Content-Type muss audio/flac sein.", statusCode: 415);
    var job = await store.CreateAsync(context.Request.Body, dereverb == true, context.RequestAborted);
    return Results.Accepted($"/api/jobs/{job.Id}", new { job.Id, job.Status });
})
    .WithName("CreateSeparationJob")
    .WithSummary("Nimmt eine FLAC-Datei zur Stem-Separation an")
    .WithDescription("Der Request-Body ist die rohe FLAC-Datei, kein Multipart-Formular. dereverb=true erzeugt zusätzlich trockenen Gesang und Hall. Die Location-Antwort zeigt auf den Job-Status. Maximal 512 MiB.")
    .Accepts<Stream>("audio/flac")
    .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
    .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .Produces(StatusCodes.Status401Unauthorized);
app.MapGet("/api/jobs/{id:guid}", (Guid id, JobStore store) =>
    store.Read(id) is { } job
        ? Results.Ok(new { job.Id, job.Status, job.Attempts, job.LastError, job.CreatedUtc, job.UpdatedUtc })
        : Results.NotFound())
    .WithName("GetSeparationJob")
    .WithSummary("Liefert den Job-Status")
    .WithDescription("Statuswerte: queued, processing, completed, failed. Bei completed kann das Ergebnis geladen werden; bei failed enthält lastError den Grund.")
    .Produces<JobStatusResponse>()
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status404NotFound);
app.MapGet("/api/jobs/{id:guid}/result", (Guid id, JobStore store) =>
{
    var job = store.Read(id);
    if (job is null) return Results.NotFound();
    if (job.Status != "completed") return Results.Problem("Ergebnis noch nicht verfügbar.", statusCode: 409);
    return Results.File(store.ResultPath(id), "application/zip", "stems.zip");
})
    .WithName("DownloadSeparationResult")
    .WithSummary("Lädt das Ergebnis-ZIP herunter")
    .WithDescription("Enthält vocals.wav und instrumental.wav; bei dereverb=true zusätzlich vocals_dry.wav und vocals_reverb.wav. Erst nach erfolgreichem Speichern und Importieren DELETE aufrufen.")
    .Produces<Stream>(StatusCodes.Status200OK, "application/zip")
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);
app.MapDelete("/api/jobs/{id:guid}", (Guid id, JobStore store) =>
{
    var job = store.Read(id);
    if (job is null) return Results.NotFound();
    if (job.Status is "queued" or "processing")
        return Results.Problem("Auftrag wird noch verarbeitet.", statusCode: 409);
    return store.DeleteTerminal(id) ? Results.NoContent() : Results.NotFound();
})
    .WithName("DeleteSeparationJob")
    .WithSummary("Bestätigt den Import und löscht das Ergebnis")
    .WithDescription("Nur für completed oder failed. Nach erfolgreichem Import aufrufen; ZIP und Job-Status werden sofort entfernt. Aktive Jobs können nicht gelöscht werden.")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);
app.Run();

static bool ValidKey(string supplied, string expected)
{
    var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(left, right);
}

public sealed record HealthResponse(string Status);
public sealed record JobAcceptedResponse(Guid Id, string Status);
public sealed record JobStatusResponse(Guid Id, string Status, int Attempts, string? LastError, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
