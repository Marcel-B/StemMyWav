using Microsoft.Extensions.Options;
using StemMyWav.Api.Catalog;
using StemMyWav.Api.Configuration;
using StemMyWav.Api.Separation;

namespace StemMyWav.Api.Http;

public static class SeparationEndpoints
{
    public static IEndpointRouteBuilder MapSeparationEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        routes.MapGet("/api/models", ListModels);
        routes.MapPost("/api/separate", SeparateAsync);
        return routes;
    }

    /// <summary>Derselbe Katalog, den der Gateway ausliefert. Hier steht zusätzlich die
    /// Modelldatei, damit sich auf dem Mac nachvollziehen lässt, was geladen wird.</summary>
    private static IResult ListModels(ModelCatalog catalog, IOptions<SeparatorOptions> options)
    {
        var fallback = catalog.ResolveDefault(options.Value.DefaultModel);
        return Results.Ok(catalog.Models.Select(model => new
        {
            model.Id,
            model.Name,
            model.Filename,
            model.Family,
            model.Task,
            model.Stems,
            model.Speed,
            model.RealtimeFactor,
            model.Measured,
            model.PublishedSdr,
            model.Notes,
            IsDefault = model.Id == fallback.Id
        }));
    }

    private static async Task<IResult> SeparateAsync(
        HttpContext context,
        SeparatorService service,
        Workspaces workspaces,
        ModelCatalog catalog,
        IOptions<SeparatorOptions> options,
        ILogger<SeparatorService> logger,
        string? model,
        bool? dereverb)
    {
        var extension = AudioUpload.Extension(context.Request.ContentType);
        if (extension is null) return Results.Problem(AudioUpload.UnsupportedType, statusCode: 415);

        // 422 und nicht 500: der Gateway schließt den Auftrag damit sofort ab, statt eine Datei
        // stundenlang gegen eine Kennung zu schicken, die es nie geben wird.
        var chosen = model is { Length: > 0 } ? catalog.Find(model) : catalog.ResolveDefault(options.Value.DefaultModel);
        if (chosen is null) return Results.Problem($"Unbekanntes Modell: {model}.", statusCode: 422);
        if (dereverb == true && !chosen.ProducesVocals)
            return Results.Problem($"Das Modell {chosen.Id} erzeugt keinen Gesangs-Stem; dereverb ist damit nicht möglich.", statusCode: 422);

        // Der Dateianfang entscheidet über die Annahme, bevor bis zu 512 MiB auf die Platte gehen.
        var prefix = new byte[AudioUpload.PrefixLength];
        var read = await context.Request.Body.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, context.RequestAborted);
        if (!AudioUpload.Matches(extension, prefix.AsSpan(0, read)))
            return Results.Problem(AudioUpload.Invalid(extension), statusCode: 400);

        var work = workspaces.Create();
        var keepUntilSent = false;
        try
        {
            var input = Path.Combine(work, "input." + extension);
            await using (var file = File.Create(input))
            {
                await file.WriteAsync(prefix.AsMemory(0, read), context.RequestAborted);
                await context.Request.Body.CopyToAsync(file, context.RequestAborted);
            }
            var archive = await service.RunAsync(input, work, chosen, dereverb == true, context.RequestAborted);
            context.Response.OnCompleted(() => { workspaces.Release(work); return Task.CompletedTask; });
            keepUntilSent = true;
            return Results.File(archive, "application/zip", "stems.zip");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (UnreadableInputException error)
        {
            // Als 4xx gemeldet, damit der Gateway den Auftrag sofort abschließt, statt eine
            // unbrauchbare Datei stundenlang erneut zu schicken und die Warteschlange zu belegen.
            logger.LogWarning(error, "Rejected an unreadable upload");
            return Results.Problem(error.Message, statusCode: 400);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Separation failed with model {Model}", chosen.Id);
            return Results.Problem("Stem-Separation fehlgeschlagen. Server-Logs prüfen.", statusCode: 500);
        }
        finally { if (!keepUntilSent) workspaces.Release(work); }
    }
}
