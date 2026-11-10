using Microsoft.Extensions.Options;

namespace TheDownloader.Services;

/// <summary>Deletes downloads once they pass their retention window, plus any orphaned folders.</summary>
public sealed class CleanupService(
    JobManager jobs,
    IOptions<DownloaderOptions> options,
    ILogger<CleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = options.Value.ResolvedDownloadDirectory;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var job in jobs.All.ToList())
                {
                    if (now >= jobs.ExpiresAt(job) && job.Status is JobStatus.Done or JobStatus.Error)
                    {
                        logger.LogInformation("Expiring download {Id} ({File})", job.Id, job.FileName);
                        jobs.Remove(job);
                    }
                    // Safety net for anything stuck far past its window.
                    else if (now - job.CreatedAt > jobs.Retention + TimeSpan.FromMinutes(options.Value.DownloadTimeoutMinutes + 5))
                    {
                        jobs.Remove(job);
                    }
                }

                // Folders left over from a previous run/crash.
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        if (jobs.Get(Path.GetFileName(dir)) is null &&
                            now - Directory.GetLastWriteTimeUtc(dir) > jobs.Retention)
                        {
                            try { Directory.Delete(dir, recursive: true); }
                            catch (Exception ex) { logger.LogWarning(ex, "Could not delete {Dir}", dir); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cleanup pass failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
