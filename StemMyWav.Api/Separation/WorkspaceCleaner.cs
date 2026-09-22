namespace StemMyWav.Api.Separation;

/// <summary>Sammelt Arbeitsverzeichnisse ein, die ein Absturz oder ein Neustart mitten in einer
/// Trennung hinterlassen hat. Ohne das bliebe eine angefangene Trennung mit mehreren hundert
/// Megabyte WAV liegen, bis macOS irgendwann sein Temp-Verzeichnis bereinigt.</summary>
public sealed class WorkspaceCleaner(Workspaces workspaces, ILogger<WorkspaceCleaner> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Eine Trennung dauert Minuten, keine Stunde. Was älter ist, läuft nicht mehr.</summary>
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = workspaces.RemoveOrphans(MinimumAge);
                if (removed > 0) logger.LogInformation("Removed {Count} orphaned work directories", removed);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Orphaned work directory cleanup failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
