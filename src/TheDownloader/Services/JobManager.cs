using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace TheDownloader.Services;

public enum JobStatus { Queued, Downloading, Processing, Converting, Done, Error }

public sealed class DownloadJob
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string OptionId { get; init; }
    /// <summary>1-based playlist index for multi-video links; null = single video.</summary>
    public int? Index { get; init; }
    public required string Directory { get; init; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cts { get; } = new();

    public volatile JobStatus Status = JobStatus.Queued;
    public double Progress;          // 0-100 for the current part
    public int Part;                 // 1-based part counter (video, audio, ...)
    public string? Speed;
    public string? Eta;
    public string? FilePath;
    public string? FileName;
    public long? FileSize;
    public string? Error;
    public DateTimeOffset? FinishedAt;
}

public sealed class JobManager
{
    private const string ProgressMarker = "@@P ";

    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly SemaphoreSlim _slots;
    private readonly DownloaderOptions _opt;
    private readonly YtDlp _ytDlp;
    private readonly MediaFinalizer _finalizer;
    private readonly ILogger<JobManager> _logger;

    public JobManager(IOptions<DownloaderOptions> options, YtDlp ytDlp, MediaFinalizer finalizer, ILogger<JobManager> logger)
    {
        _opt = options.Value;
        _ytDlp = ytDlp;
        _finalizer = finalizer;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrentDownloads));
        Directory.CreateDirectory(_opt.ResolvedDownloadDirectory);
    }

    public TimeSpan Retention => TimeSpan.FromMinutes(_opt.RetentionMinutes);

    public DownloadJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IEnumerable<DownloadJob> All => _jobs.Values;

    public DownloadJob Start(string url, string optionId, int? index)
    {
        var id = Guid.NewGuid().ToString("N");
        var job = new DownloadJob
        {
            Id = id,
            Url = url,
            OptionId = optionId,
            Index = index,
            Directory = Path.Combine(_opt.ResolvedDownloadDirectory, id),
        };
        _jobs[id] = job;
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    public DateTimeOffset ExpiresAt(DownloadJob job) =>
        (job.FinishedAt ?? job.CreatedAt) + Retention;

    public void Remove(DownloadJob job)
    {
        _jobs.TryRemove(job.Id, out _);
        try { job.Cts.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            if (Directory.Exists(job.Directory)) Directory.Delete(job.Directory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete {Dir}", job.Directory);
        }
    }

    private async Task RunAsync(DownloadJob job)
    {
        var ct = job.Cts.Token;
        try
        {
            await _slots.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            job.Status = JobStatus.Downloading;
            job.Part = 1;
            Directory.CreateDirectory(job.Directory);

            var psi = _ytDlp.NewStartInfo();
            string[] baseArgs =
            [
                "--no-warnings", "--newline", "--progress",
                "--no-mtime",
                "--progress-template",
                $"download:{ProgressMarker}%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
                "--print", "after_move:filepath",
                "-o", Path.Combine(job.Directory, "%(title).120B.%(ext)s"),
            ];
            string[] itemArgs = job.Index is { } i ? ["--playlist-items", i.ToString()] : ["--no-playlist"];
            foreach (var a in baseArgs.Concat(itemArgs).Concat(YtDlp.FormatArgs(job.OptionId))) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(job.Url);

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new YtDlpException("Could not start yt-dlp.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(_opt.DownloadTimeoutMinutes));
            using var reg = timeout.Token.Register(() => YtDlp.TryKill(proc));

            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            string? finalPath = null;
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
            {
                if (line.StartsWith(ProgressMarker, StringComparison.Ordinal))
                    HandleProgress(job, line[ProgressMarker.Length..]);
                else if (!string.IsNullOrWhiteSpace(line))
                    finalPath = line.Trim();
            }

            await proc.WaitForExitAsync(CancellationToken.None);
            var stderr = await stderrTask;

            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new YtDlpException("Download took too long and was stopped.");
            ct.ThrowIfCancellationRequested();

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("yt-dlp download failed for {Url}: {Err}", job.Url, stderr);
                throw new YtDlpException(YtDlp.CleanError(stderr));
            }

            // Fall back to scanning the job dir if --print didn't give us a usable path.
            if (finalPath is null || !File.Exists(finalPath))
                finalPath = Directory.EnumerateFiles(job.Directory)
                    .Where(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl"))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();

            if (finalPath is null || !Path.GetFullPath(finalPath).StartsWith(Path.GetFullPath(job.Directory)))
                throw new YtDlpException("Download finished but no file was produced.");

            job.Status = JobStatus.Processing;
            finalPath = await _finalizer.FinalizeAsync(finalPath, pct =>
            {
                job.Status = JobStatus.Converting;
                job.Progress = pct;
                job.Speed = job.Eta = null;
            }, ct);

            job.FilePath = finalPath;
            job.FileName = Path.GetFileName(finalPath);
            job.FileSize = new FileInfo(finalPath).Length;
            job.Progress = 100;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.Status = JobStatus.Done;
        }
        catch (OperationCanceledException)
        {
            // Job removed (expired) while running - nothing to report.
        }
        catch (Exception ex)
        {
            job.Error = ex is YtDlpException ? ex.Message : "Unexpected error while downloading.";
            if (ex is not YtDlpException) _logger.LogError(ex, "Download job {Id} failed", job.Id);
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.Status = JobStatus.Error;
        }
        finally
        {
            _slots.Release();
        }
    }

    private static void HandleProgress(DownloadJob job, string payload)
    {
        var parts = payload.Split('|');
        if (parts.Length < 3) return;

        if (double.TryParse(parts[0].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            // A drop back to a low % means yt-dlp moved on to the next stream (e.g. audio after video).
            if (pct + 20 < job.Progress) job.Part++;
            job.Progress = pct;
            job.Status = pct >= 100 ? JobStatus.Processing : JobStatus.Downloading;
        }
        job.Speed = Clean(parts[1]);
        job.Eta = Clean(parts[2]);

        static string? Clean(string s)
        {
            s = s.Trim();
            return s is "" or "NA" or "N/A" or "Unknown" ? null : s;
        }
    }
}
