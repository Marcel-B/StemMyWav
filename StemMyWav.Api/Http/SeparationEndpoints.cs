using StemMyWav.Api.Separation;

namespace StemMyWav.Api.Http;

public static class SeparationEndpoints
{
    public static IEndpointRouteBuilder MapSeparationEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        routes.MapPost("/api/separate", SeparateAsync);
        return routes;
    }

    private static async Task<IResult> SeparateAsync(
        HttpContext context,
        SeparatorService service,
        Workspaces workspaces,
        ILogger<SeparatorService> logger,
        bool? dereverb)
    {
        if (context.Request.ContentType is not ("audio/flac" or "audio/x-flac"))
            return Results.Problem("Content-Type muss audio/flac sein.", statusCode: 415);

        // Der Dateianfang entscheidet über die Annahme, bevor bis zu 512 MiB auf die Platte gehen.
        var prefix = new byte[4];
        var read = await context.Request.Body.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, context.RequestAborted);
        if (read != prefix.Length || !prefix.AsSpan().SequenceEqual("fLaC"u8))
            return Results.Problem("Ungültige FLAC-Datei.", statusCode: 400);

        var work = workspaces.Create();
        var keepUntilSent = false;
        try
        {
            var input = Path.Combine(work, "input.flac");
            await using (var file = File.Create(input))
            {
                await file.WriteAsync(prefix, context.RequestAborted);
                await context.Request.Body.CopyToAsync(file, context.RequestAborted);
            }
            var archive = await service.RunAsync(input, work, dereverb == true, context.RequestAborted);
            context.Response.OnCompleted(() => { workspaces.Release(work); return Task.CompletedTask; });
            keepUntilSent = true;
            return Results.File(archive, "application/zip", "stems.zip");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Separation failed");
            return Results.Problem("Stem-Separation fehlgeschlagen. Server-Logs prüfen.", statusCode: 500);
        }
        finally { if (!keepUntilSent) workspaces.Release(work); }
    }
}
