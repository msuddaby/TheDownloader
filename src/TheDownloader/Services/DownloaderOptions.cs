namespace TheDownloader.Services;

public sealed class DownloaderOptions
{
    /// <summary>Path or name of the yt-dlp executable.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>Where temporary downloads are stored. Empty = system temp dir.</summary>
    public string DownloadDirectory { get; set; } = "";

    /// <summary>How long a finished download is kept before it is deleted.</summary>
    public int RetentionMinutes { get; set; } = 10;

    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>A download running longer than this is killed.</summary>
    public int DownloadTimeoutMinutes { get; set; } = 30;

    public string ResolvedDownloadDirectory =>
        string.IsNullOrWhiteSpace(DownloadDirectory)
            ? Path.Combine(Path.GetTempPath(), "thedownloader")
            : DownloadDirectory;
}
