using System.IO.Compression;

public sealed class JobWorker(JobStore store, IHttpClientFactory clients, IConfiguration config, ILogger<JobWorker> logger) : BackgroundService
{
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
                    store.CleanupExpired(TimeSpan.FromDays(Math.Max(1, config.GetValue("Gateway:RetentionDays", 7))));
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
        job.Status = "processing";
        job.Attempts++;
        store.Save(job);
        try
        {
            var baseUrl = config["MacBackend:Url"]!.TrimEnd('/');
            var url = baseUrl + "/api/separate?dereverb=" + job.Dereverb.ToString().ToLowerInvariant();
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Api-Key", Secrets.Read(config, "MacBackend:ApiKey"));
            await using var input = File.OpenRead(store.InputPath(job.Id));
            request.Content = new StreamContent(input);
            request.Content.Headers.ContentType = new("audio/flac");
            using var response = await clients.CreateClient("mac").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != System.Net.HttpStatusCode.RequestTimeout)
                    throw new PermanentJobException($"Mac-API antwortete mit {(int)response.StatusCode}.");
                throw new HttpRequestException($"Mac-API antwortete mit {(int)response.StatusCode}.");
            }
            var target = store.ResultPath(job.Id);
            var temporary = target + ".tmp";
            await using (var file = File.Create(temporary)) await response.Content.CopyToAsync(file, token);
            using (var zip = ZipFile.OpenRead(temporary))
            {
                if (zip.GetEntry("vocals.wav") is null || zip.GetEntry("instrumental.wav") is null ||
                    (job.Dereverb && zip.GetEntry("vocals_dry.wav") is null))
                    throw new InvalidDataException("Mac-API lieferte unvollständige Stems.");
            }
            File.Move(temporary, target, true);
            job.Status = "completed";
            job.LastError = null;
            store.Save(job);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            job.Status = "queued";
            store.Save(job);
            throw;
        }
        catch (PermanentJobException error)
        {
            job.Status = "failed";
            job.LastError = error.Message;
            store.Save(job);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Job {JobId} will be retried", job.Id);
            job.Status = "queued";
            job.LastError = "Mac nicht erreichbar oder vorübergehend fehlgeschlagen.";
            job.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(5, job.Attempts - 1))));
            store.Save(job);
        }
    }
}

public sealed class PermanentJobException(string message) : Exception(message);
