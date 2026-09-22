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

        routes.MapPost("/api/jobs", CreateJobAsync)
            .WithName("CreateSeparationJob")
            .WithSummary("Nimmt eine FLAC-Datei zur Stem-Separation an")
            .WithDescription("Der Request-Body ist die rohe FLAC-Datei, kein Multipart-Formular. dereverb=true erzeugt zusätzlich trockenen Gesang und, sofern das Modell ihn ausgibt, den Hallanteil. Die Location-Antwort zeigt auf den Job-Status. Maximal 512 MiB. Bei voller Warteschlange nennt Retry-After den Abstand bis zum nächsten Versuch.")
            .Accepts<Stream>("audio/flac")
            .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status401Unauthorized);

        routes.MapGet("/api/jobs", ListJobs)
            .WithName("ListSeparationJobs")
            .WithSummary("Listet alle bekannten Aufträge")
            .WithDescription("Jüngste zuerst. Zeigt auch Aufträge, die die Warteschlange belegen, damit ein hängender Auftrag gefunden und mit DELETE abgebrochen werden kann.")
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
            .WithDescription("Enthält 48-kHz-/16-Bit-Stereo-WAVs: vocals.wav und instrumental.wav; bei dereverb=true zusätzlich vocals_dry.wav und, sofern das Modell ihn ausgibt, vocals_reverb.wav. Erst nach erfolgreichem Speichern und Importieren DELETE aufrufen.")
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

    private static async Task<IResult> CreateJobAsync(HttpContext context, JobStore store, bool? dereverb)
    {
        if (context.Request.ContentType is not ("audio/flac" or "audio/x-flac"))
            return Results.Problem("Content-Type muss audio/flac sein.", statusCode: 415);

        // Der Dateianfang entscheidet über die Annahme, bevor bis zu 512 MiB auf die Platte gehen.
        var prefix = new byte[4];
        var read = await context.Request.Body.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, context.RequestAborted);
        if (read != prefix.Length || !prefix.AsSpan().SequenceEqual("fLaC"u8))
            return Results.Problem("Ungültige FLAC-Datei.", statusCode: 400);

        var job = await store.CreateAsync(prefix, context.Request.Body, dereverb == true, context.RequestAborted);
        if (job is null)
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Problem("Warteschlange voll. Später erneut versuchen.", statusCode: 429);
        }
        return Results.Accepted($"/api/jobs/{job.Id}", new JobAcceptedResponse(job.Id, job.Status));
    }

    private static IResult ListJobs(JobStore store) =>
        Results.Ok(store.All().Select(Describe).ToList());

    private static JobStatusResponse Describe(JobRecord job) =>
        new(job.Id, job.Status, job.Attempts, job.LastError, job.CreatedUtc, job.UpdatedUtc);

    private static IResult ReadJob(Guid id, JobStore store) =>
        store.Read(id) is { } job ? Results.Ok(Describe(job)) : Results.NotFound();

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
