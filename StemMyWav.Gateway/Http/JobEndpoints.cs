using Microsoft.Extensions.Options;
using StemMyWav.Gateway.Catalog;
using StemMyWav.Gateway.Configuration;
using StemMyWav.Gateway.Jobs;

namespace StemMyWav.Gateway.Http;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
            .WithName("GetHealth")
            .WithSummary("Prüft, ob der Gateway-Prozess läuft")
            .WithDescription("Prüft nicht die Erreichbarkeit der Mac-API.")
            .Produces<HealthResponse>();

        routes.MapGet("/api/models", ListModels)
            .WithName("ListSeparationModels")
            .WithSummary("Listet die wählbaren Trennmodelle")
            .WithDescription("Die Kennung aus id gehört in den model-Parameter von POST /api/jobs. stems nennt die Dateien im Ergebnis-ZIP, jeweils mit der Endung .wav. speed schätzt den Rechenaufwand: fast rechnet schneller als der Titel dauert, verySlow braucht ein Vielfaches davon. realtimeFactor ist Audiodauer geteilt durch Rechenzeit — 0,3 heißt, dass ein Vier-Minuten-Titel rund dreizehn Minuten dauert. Ist measured false, stammt die Einstufung aus dem Vergleich mit einem gemessenen Modell derselben Familie. Die Liste kommt aus dem Gateway selbst und ist deshalb auch dann abrufbar, wenn der Mac gerade nicht erreichbar ist.")
            .Produces<IReadOnlyList<ModelResponse>>()
            .Produces(StatusCodes.Status401Unauthorized);

        routes.MapPost("/api/jobs", CreateJobAsync)
            .WithName("CreateSeparationJob")
            .WithSummary("Nimmt eine FLAC- oder WAV-Datei zur Stem-Separation an")
            .WithDescription("Der Request-Body ist die rohe Audiodatei, kein Multipart-Formular; Content-Type ist audio/flac oder audio/wav. model wählt das Trennmodell aus GET /api/models; ohne Angabe gilt die Voreinstellung. Das Ergebnis ist unabhängig von der Eingabe immer 48-kHz-/16-Bit-Stereo-WAV. dereverb=true erzeugt zusätzlich trockenen Gesang und, sofern das Modell ihn ausgibt, den Hallanteil; es setzt ein Modell mit Gesangs-Stem voraus. Die Location-Antwort zeigt auf den Job-Status. Maximal 512 MiB. Bei voller Warteschlange nennt Retry-After den Abstand bis zum nächsten Versuch.")
            .Accepts<Stream>("audio/flac", "audio/wav")
            .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status401Unauthorized);

        routes.MapGet("/api/jobs", ListJobs)
            .WithName("ListSeparationJobs")
            .WithSummary("Listet alle bekannten Aufträge")
            .WithDescription("Jüngste zuerst. model nennt das verwendete Trennmodell und stems die Dateien, die das Ergebnis-ZIP enthält. Zeigt auch Aufträge, die die Warteschlange belegen, damit ein hängender Auftrag gefunden und mit DELETE abgebrochen werden kann.")
            .Produces<IReadOnlyList<JobStatusResponse>>()
            .Produces(StatusCodes.Status401Unauthorized);

        routes.MapGet("/api/jobs/{id:guid}", ReadJob)
            .WithName("GetSeparationJob")
            .WithSummary("Liefert den Job-Status")
            .WithDescription("Statuswerte: queued, processing, completed, failed. Bei completed kann das Ergebnis geladen werden; bei failed enthält lastError den Grund.")
            .Produces<JobStatusResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        routes.MapGet("/api/jobs/{id:guid}/result", DownloadResult)
            .WithName("DownloadSeparationResult")
            .WithSummary("Lädt das Ergebnis-ZIP herunter")
            .WithDescription("Enthält 48-kHz-/16-Bit-Stereo-WAVs, benannt nach den stems des gewählten Modells — bei den Zwei-Stem-Modellen also vocals.wav und instrumental.wav, bei htdemucs_6s sechs Dateien. Bei dereverb=true kommen vocals_dry.wav und, sofern das De-Reverb-Modell ihn ausgibt, vocals_reverb.wav hinzu. Erst nach erfolgreichem Speichern und Importieren DELETE aufrufen.")
            .Produces<Stream>(StatusCodes.Status200OK, "application/zip")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        routes.MapDelete("/api/jobs/{id:guid}", DeleteJob)
            .WithName("DeleteSeparationJob")
            .WithSummary("Bestätigt den Import oder bricht einen wartenden Auftrag ab")
            .WithDescription("Nach erfolgreichem Import aufrufen; ZIP und Job-Status werden sofort entfernt. Ein noch wartender Auftrag (queued) wird damit abgebrochen und gibt seinen Platz in der Warteschlange frei. Nur während der laufenden Übertragung zum Mac (processing) ist das Löschen gesperrt.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static IResult ListModels(ModelCatalog catalog, IOptions<GatewayOptions> options)
    {
        var fallback = catalog.ResolveDefault(options.Value.DefaultModel);
        return Results.Ok(catalog.Models.Select(model => new ModelResponse(
            model.Id, model.Name, model.Family, model.Task, model.Stems, model.Speed,
            model.RealtimeFactor, model.Measured, model.PublishedSdr, model.Notes,
            model.Id == fallback.Id)).ToList());
    }

    private static async Task<IResult> CreateJobAsync(
        HttpContext context, JobStore store, ModelCatalog catalog, IOptions<GatewayOptions> options, string? model, bool? dereverb)
    {
        var extension = AudioUpload.Extension(context.Request.ContentType);
        if (extension is null) return Results.Problem(AudioUpload.UnsupportedType, statusCode: 415);

        // Die Kennung wird hier geprüft und nicht erst auf dem Mac: eine Datei, die ohnehin nie
        // laufen kann, soll gar nicht erst einen Platz in der Warteschlange belegen.
        var chosen = model is { Length: > 0 } ? catalog.Find(model) : catalog.ResolveDefault(options.Value.DefaultModel);
        if (chosen is null)
            return Results.Problem($"Unbekanntes Modell: {model}. Die wählbaren Kennungen stehen unter /api/models.", statusCode: 400);
        if (dereverb == true && !chosen.ProducesVocals)
            return Results.Problem($"Das Modell {chosen.Id} erzeugt keinen Gesangs-Stem; dereverb ist damit nicht möglich.", statusCode: 400);

        // Der Dateianfang entscheidet über die Annahme, bevor bis zu 512 MiB auf die Platte gehen.
        var prefix = new byte[AudioUpload.PrefixLength];
        var read = await context.Request.Body.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, context.RequestAborted);
        if (!AudioUpload.Matches(extension, prefix.AsSpan(0, read)))
            return Results.Problem(AudioUpload.Invalid(extension), statusCode: 400);

        var job = await store.CreateAsync(prefix.AsMemory(0, read), context.Request.Body, chosen.Id, extension,
            dereverb == true, context.RequestAborted);
        if (job is null)
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Problem("Warteschlange voll. Später erneut versuchen.", statusCode: 429);
        }
        return Results.Accepted($"/api/jobs/{job.Id}", new JobAcceptedResponse(job.Id, job.Status, job.Model));
    }

    private static IResult ListJobs(JobStore store, ModelCatalog catalog) =>
        Results.Ok(store.All().Select(job => Describe(job, catalog)).ToList());

    private static JobStatusResponse Describe(JobRecord job, ModelCatalog catalog) =>
        new(job.Id, job.Status, job.Model, catalog.Find(job.Model)?.Stems ?? [],
            job.Attempts, job.LastError, job.CreatedUtc, job.UpdatedUtc);

    private static IResult ReadJob(Guid id, JobStore store, ModelCatalog catalog) =>
        store.Read(id) is { } job ? Results.Ok(Describe(job, catalog)) : Results.NotFound();

    private static IResult DownloadResult(Guid id, JobStore store)
    {
        var job = store.Read(id);
        if (job is null) return Results.NotFound();
        if (job.Status != JobStatus.Completed) return Results.Problem("Ergebnis noch nicht verfügbar.", statusCode: 409);
        try
        {
            return Results.File(File.OpenRead(store.ResultPath(id)), "application/zip", "stems.zip");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static IResult DeleteJob(Guid id, JobStore store)
    {
        if (store.Read(id) is null) return Results.NotFound();
        if (store.Delete(id)) return Results.NoContent();
        return store.Read(id) is null
            ? Results.NotFound()
            : Results.Problem("Auftrag wird gerade übertragen.", statusCode: 409);
    }
}
