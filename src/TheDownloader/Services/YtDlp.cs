using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TheDownloader.Services;

public sealed record VideoInfo(
    string Title,
    string? Thumbnail,
    double? Duration,
    string? Uploader,
    string? Site,
    IReadOnlyList<FormatOption> Options);

/// <param name="Id">Opaque option id sent back to /api/download ("1080", "best", "audio").</param>
/// <param name="IosCompatible">True when the result will be H.264/HEVC + AAC in MP4, which the iOS Photos app accepts.</param>
public sealed record FormatOption(
    string Id,
    string Label,
    int? Height,
    bool IosCompatible,
    string? Codec,
    long? ApproxBytes,
    bool AudioOnly);

public sealed class YtDlpException(string message) : Exception(message);

public sealed class YtDlp(IOptions<DownloaderOptions> options, ILogger<YtDlp> logger)
{
    private readonly DownloaderOptions _opt = options.Value;

    public static bool IsValidUrl(string? url, out Uri? uri) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public async Task<VideoInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        var psi = NewStartInfo();
        foreach (var a in new[] { "-J", "--no-playlist", "--no-warnings", "--", url })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new YtDlpException("Could not start yt-dlp.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw new YtDlpException("Timed out while reading video info.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogWarning("yt-dlp info failed for {Url}: {Err}", url, stderr);
            throw new YtDlpException(CleanError(stderr));
        }

        using var doc = JsonDocument.Parse(stdout);
        return ParseInfo(doc.RootElement);
    }

    private static VideoInfo ParseInfo(JsonElement root)
    {
        string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var videoFormats = new List<(int Height, string VCodec, long? Size, bool HasAudio)>();
        long? bestAudioSize = null;
        var hasAudio = false;

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var vcodec = Str(f, "vcodec") ?? "";
                var acodec = Str(f, "acodec") ?? "";
                var size = (long?)(Num(f, "filesize") ?? Num(f, "filesize_approx"));
                var height = (int?)Num(f, "height");
                var isVideo = vcodec != "none" && height is > 0;
                var isAudio = acodec != "none" && acodec != "";

                if (isVideo)
                    videoFormats.Add((height!.Value, vcodec, size, isAudio));
                else if (isAudio)
                {
                    hasAudio = true;
                    if (size is not null && (bestAudioSize is null || size > bestAudioSize))
                        bestAudioSize = size;
                }
                if (isVideo && isAudio) hasAudio = true;
            }
        }
        else if (Num(root, "height") is { } h && h > 0)
        {
            // Single-format extractors (no "formats" array).
            videoFormats.Add(((int)h, Str(root, "vcodec") ?? "", (long?)Num(root, "filesize"), true));
            hasAudio = true;
        }

        var options = new List<FormatOption>();
        foreach (var group in videoFormats.GroupBy(f => f.Height).OrderByDescending(g => g.Key))
        {
            var compatible = group.Where(f => IsIosVideoCodec(f.VCodec)).ToList();
            var pick = compatible.Count > 0 ? compatible : group.ToList();
            var size = pick.Max(f => f.Size);
            var needsAudio = !pick.Any(f => f.HasAudio);
            if (size is not null && needsAudio && bestAudioSize is not null) size += bestAudioSize;

            options.Add(new FormatOption(
                Id: group.Key.ToString(),
                Label: HeightLabel(group.Key),
                Height: group.Key,
                IosCompatible: compatible.Count > 0,
                Codec: FriendlyCodec(pick[0].VCodec),
                ApproxBytes: size,
                AudioOnly: false));
        }

        if (options.Count == 0)
            options.Add(new FormatOption("best", "Best available", null, false, null, null, false));

        if (hasAudio)
            options.Add(new FormatOption("audio", "Audio only (M4A)", null, false, "AAC", bestAudioSize, true));

        return new VideoInfo(
            Title: Str(root, "title") ?? "video",
            Thumbnail: Str(root, "thumbnail"),
            Duration: Num(root, "duration"),
            Uploader: Str(root, "uploader") ?? Str(root, "channel"),
            Site: Str(root, "extractor_key"),
            Options: options);
    }

    /// <summary>Builds yt-dlp format-selection args for an option id from <see cref="GetInfoAsync"/>.</summary>
    public static IEnumerable<string> FormatArgs(string optionId)
    {
        if (optionId == "audio")
            return ["-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a"];

        if (int.TryParse(optionId, out var h) && h > 0)
            return
            [
                "-f", $"bv*[height<={h}]+ba/b[height<={h}]/b",
                // Prefer the highest resolution under the cap, then iPhone-friendly codecs.
                "-S", "res,vcodec:h264,acodec:aac",
                "--merge-output-format", "mp4",
            ];

        return ["-f", "bv*+ba/b", "-S", "vcodec:h264,acodec:aac", "--merge-output-format", "mp4"];
    }

    public ProcessStartInfo NewStartInfo() => new()
    {
        FileName = _opt.YtDlpPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    public static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    public static string CleanError(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        if (line is null) return "yt-dlp failed to process this link.";
        return line["ERROR:".Length..].Trim();
    }

    private static bool IsIosVideoCodec(string vcodec) =>
        vcodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ||
        vcodec.StartsWith("h264", StringComparison.OrdinalIgnoreCase) ||
        vcodec.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase) ||
        vcodec.StartsWith("hev1", StringComparison.OrdinalIgnoreCase) ||
        vcodec.StartsWith("hevc", StringComparison.OrdinalIgnoreCase) ||
        vcodec.StartsWith("h265", StringComparison.OrdinalIgnoreCase);

    private static string? FriendlyCodec(string vcodec) => vcodec.ToLowerInvariant() switch
    {
        "" => null,
        var c when c.StartsWith("avc") || c.StartsWith("h264") => "H.264",
        var c when c.StartsWith("hvc") || c.StartsWith("hev") || c.StartsWith("h265") => "HEVC",
        var c when c.StartsWith("vp09") || c.StartsWith("vp9") => "VP9",
        var c when c.StartsWith("av01") => "AV1",
        var c => c.Split('.')[0].ToUpperInvariant(),
    };

    private static string HeightLabel(int h) => h switch
    {
        >= 4320 => $"{h}p (8K)",
        >= 2160 => $"{h}p (4K)",
        >= 1440 => $"{h}p (2K)",
        _ => $"{h}p",
    };
}
