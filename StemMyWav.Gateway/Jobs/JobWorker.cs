using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StemMyWav.Gateway.Configuration;

namespace StemMyWav.Gateway.Jobs;

/// <summary>Überträgt wartende Aufträge an die Mac-API und hält das Ergebnis vor.</summary>
public sealed class JobWorker(
    JobStore store,
    IHttpClientFactory clients,
    IOptions<GatewayOptions> gateway,
    IOptions<MacBackendOptions> backend,
    ILogger<JobWorker> logger) : BackgroundService
{
    private readonly GatewayOptions _gateway = gateway.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastCleanup = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var job in store.Pending())
                    await ProcessAsync(job, stoppingToken);
                if (DateTimeOffset.UtcNow - lastCleanup >= TimeSpan.FromHours(1))
                {
                    store.CleanupExpired(TimeSpan.FromDays(_gateway.RetentionDays));
                    lastCleanup = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Job queue scan failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessAsync(JobRecord job, CancellationToken token)
    {
        job.Status = JobStatus.Processing;
        job.Attempts++;
        store.Save(job);
        try
        {
            var url = backend.Value.Url!.TrimEnd('/') + "/api/separate?dereverb=" + job.Dereverb.ToString().ToLowerInvariant();
            var target = store.ResultPath(job.Id);
            var temporary = target + ".tmp";
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            await using (var input = File.OpenRead(store.InputPath(job.Id)))
            {
                request.Headers.Add("X-Api-Key", backend.Value.ApiKey);
                request.Content = new StreamContent(input);
                request.Content.Headers.ContentType = new("audio/flac");
                using var response = await clients.CreateClient("mac").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsRejectedInput(response.StatusCode))
                        throw new PermanentJobException(await DescribeAsync(response, token));
                    throw new HttpRequestException($"Mac-API antwortete mit {(int)response.StatusCode}.");
                }
                await using (var file = File.Create(temporary)) await response.Content.CopyToAsync(file, token);
            }
            using (var zip = ZipFile.OpenRead(temporary))
            {
                if (zip.GetEntry("vocals.wav") is null || zip.GetEntry("instrumental.wav") is null ||
                    (job.Dereverb && zip.GetEntry("vocals_dry.wav") is null))
                    throw new InvalidDataException("Mac-API lieferte unvollständige Stems.");
            }
            File.Move(temporary, target, true);
            job.Status = JobStatus.Completed;
            job.LastError = null;
            store.Save(job);
            RemoveInput(job.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            job.Status = JobStatus.Queued;
            store.Save(job);
            throw;
        }
        catch (PermanentJobException error)
        {
            job.Status = JobStatus.Failed;
            job.LastError = error.Message;
            store.Save(job);
            RemoveInput(job.Id);
        }
        catch (Exception error)
        {
            if (DateTimeOffset.UtcNow - job.CreatedUtc >= TimeSpan.FromHours(_gateway.MaxQueueHours))
            {
                logger.LogError(error, "Job {JobId} gave up after {Hours} hours", job.Id, _gateway.MaxQueueHours);
                job.Status = JobStatus.Failed;
                job.LastError = $"Mac seit über {_gateway.MaxQueueHours} Stunden nicht erreichbar.";
                store.Save(job);
                RemoveInput(job.Id);
                return;
            }
            logger.LogWarning(error, "Job {JobId} will be retried", job.Id);
            job.Status = JobStatus.Queued;
            job.LastError = "Mac nicht erreichbar oder vorübergehend fehlgeschlagen.";
            job.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(5, job.Attempts - 1))));
            store.Save(job);
        }
    }

    /// <summary>Übernimmt den Grund aus der Problemantwort des Macs, damit im Job-Status steht,
    /// was mit der Datei nicht stimmte, und nicht nur der Statuscode.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken token)
    {
        var fallback = $"Mac-API antwortete mit {(int)response.StatusCode}.";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (document.RootElement.TryGetProperty("detail", out var detail) &&
                detail.GetString() is { Length: > 0 } reason)
                return reason.Length > 300 ? reason[..300] : reason;
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or InvalidOperationException)
        {
            // Ohne verwertbaren Körper bleibt der Statuscode.
        }
        return fallback;
    }

    /// <summary>Nur eine Ablehnung der FLAC selbst ist endgültig; alles andere, auch 401 nach einem
    /// Schlüsselwechsel, ist behebbar und darf die Eingabe nicht verwerfen.</summary>
    private static bool IsRejectedInput(HttpStatusCode status) => (int)status is 400 or 413 or 415 or 422;

    private void RemoveInput(Guid id)
    {
        try { store.RemoveInput(id); }
        catch (IOException error) { logger.LogWarning(error, "Input cleanup failed for job {JobId}", id); }
        catch (UnauthorizedAccessException error) { logger.LogWarning(error, "Input cleanup failed for job {JobId}", id); }
    }
}

/// <summary>Signalisiert, dass eine Wiederholung sinnlos wäre, weil die Mac-API die Datei ablehnt.</summary>
public sealed class PermanentJobException(string message) : Exception(message);
