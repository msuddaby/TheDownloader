namespace TheDownloader.Services;

public sealed class DownloaderOptions
{
    /// <summary>Path or name of the yt-dlp executable.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>Path or name of the ffmpeg executable (used to stamp the creation date).</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Set the file's creation date to the download time, so iOS Photos files it at the
    /// front of the camera roll instead of by original upload date (or import order).
    /// </summary>
    public bool StampCreationDate { get; set; } = true;

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
