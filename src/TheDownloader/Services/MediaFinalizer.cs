using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TheDownloader.Services;

/// <summary>
/// Turns whatever yt-dlp produced into something iOS Photos accepts: an MP4 with H.264 or HEVC
/// video and AAC audio (or an M4A for audio-only), stamped with today's creation date.
/// Streams that are already compatible are copied; anything else (VP9, AV1, Opus, ...) is re-encoded.
/// </summary>
public sealed class MediaFinalizer(IOptions<DownloaderOptions> options, ILogger<MediaFinalizer> logger)
{
    private readonly DownloaderOptions _opt = options.Value;

    private sealed record Probe(string? VideoCodec, string? PixFmt, int? VideoIndex, string? AudioCodec, int? AudioIndex, double? Duration);

    /// <param name="onEncodeProgress">Called with 0-100 while re-encoding (not called for copy-only).</param>
    /// <returns>Path of the final file (may differ from <paramref name="path"/> in extension).</returns>
    public async Task<string> FinalizeAsync(string path, Action<double> onEncodeProgress, CancellationToken ct)
    {
        var probe = await ProbeAsync(path, ct)
            ?? throw new YtDlpException("Downloaded file couldn't be read.");
        if (probe.VideoIndex is null && probe.AudioIndex is null)
            throw new YtDlpException("Downloaded file has no audio or video.");

        var audioOnly = probe.VideoIndex is null;
        var dir = Path.GetDirectoryName(path)!;
        var finalPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + (audioOnly ? ".m4a" : ".mp4"));
        var tmp = Path.Combine(dir, ".finalizing" + (audioOnly ? ".m4a" : ".mp4"));

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-y", "-i", path };
        var encoding = false;

        if (probe.VideoIndex is { } v)
        {
            args.AddRange(["-map", $"0:{v}"]);
            var codec = probe.VideoCodec ?? "";
            var eightBit = probe.PixFmt is null or "yuv420p" or "yuvj420p";
            if (codec == "h264" && eightBit)
                args.AddRange(["-c:v", "copy"]);
            else if (codec == "hevc")
                args.AddRange(["-c:v", "copy", "-tag:v", "hvc1"]); // Apple only plays HEVC tagged hvc1
            else
            {
                encoding = true;
                args.AddRange([
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                    "-pix_fmt", "yuv420p", "-profile:v", "high",
                ]);
            }
        }

        if (probe.AudioIndex is { } a)
        {
            args.AddRange(["-map", $"0:{a}"]);
            if (probe.AudioCodec == "aac")
                args.AddRange(["-c:a", "copy"]);
            else
                args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }

        args.AddRange(["-map_metadata", "0", "-map_chapters", "-1"]);
        if (_opt.StampCreationDate)
        {
            // iOS Photos sorts by creation date; stamping "now" puts the video at the front of the camera roll.
            var now = DateTimeOffset.UtcNow;
            args.AddRange([
                "-metadata", $"creation_time={now:yyyy-MM-ddTHH:mm:ss.ffffffZ}",
                "-metadata", $"com.apple.quicktime.creationdate={now.ToLocalTime():yyyy-MM-ddTHH:mm:sszzz}",
            ]);
        }
        args.AddRange(["-movflags", "+faststart+use_metadata_tags", "-progress", "pipe:1", tmp]);

        logger.LogInformation("Finalizing {File}: video={V}/{Pix} audio={A} -> {Mode}",
            Path.GetFileName(path), probe.VideoCodec, probe.PixFmt, probe.AudioCodec, encoding ? "re-encode" : "copy");

        var psi = NewStartInfo(_opt.FfmpegPath, args);
        try
        {
            using var proc = Process.Start(psi) ?? throw new YtDlpException("Could not start ffmpeg.");
            using var reg = ct.Register(() => YtDlp.TryKill(proc));
            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
            {
                if (encoding && probe.Duration is > 0 &&
                    line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us >= 0)
                {
                    onEncodeProgress(Math.Min(100, us / 1e6 / probe.Duration.Value * 100));
                }
            }

            await proc.WaitForExitAsync(CancellationToken.None);
            var stderr = await stderrTask;
            ct.ThrowIfCancellationRequested();

            if (proc.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                logger.LogWarning("ffmpeg failed for {File}: {Err}", path, stderr);
                throw new YtDlpException("Couldn't convert the video to MP4.");
            }

            File.Delete(path);
            File.Move(tmp, finalPath, overwrite: true);
            return finalPath;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* moved or never created */ }
        }
    }

    private async Task<Probe?> ProbeAsync(string path, CancellationToken ct)
    {
        var psi = NewStartInfo(_opt.FfprobePath,
        [
            "-v", "error", "-of", "json",
            "-show_entries", "stream=index,codec_type,codec_name,pix_fmt:stream_disposition=attached_pic:format=duration",
            path,
        ]);
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        if (proc.ExitCode != 0)
        {
            logger.LogWarning("ffprobe failed for {File}: {Err}", path, await stderrTask);
            return null;
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        string? vCodec = null, pix = null, aCodec = null;
        int? vIdx = null, aIdx = null;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var name = s.TryGetProperty("codec_name", out var n) ? n.GetString() : null;
                var idx = s.GetProperty("index").GetInt32();
                var isCover = s.TryGetProperty("disposition", out var d) &&
                              d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;

                if (type == "video" && !isCover && vIdx is null)
                {
                    vIdx = idx;
                    vCodec = name;
                    pix = s.TryGetProperty("pix_fmt", out var p) ? p.GetString() : null;
                }
                else if (type == "audio" && aIdx is null)
                {
                    aIdx = idx;
                    aCodec = name;
                }
            }
        }

        double? duration = root.TryGetProperty("format", out var fmt) &&
                           fmt.TryGetProperty("duration", out var du) &&
                           double.TryParse(du.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)
            ? dv : null;

        return new Probe(vCodec, pix, vIdx, aCodec, aIdx, duration);
    }

    private static ProcessStartInfo NewStartInfo(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
