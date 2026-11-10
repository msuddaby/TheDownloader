using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TheDownloader.Services;

/// <summary>What a link resolves to. Usually one item; posts with several videos (e.g. a tweet
/// quoting another video tweet) or playlists yield several.</summary>
public sealed record VideoInfo(
    string Title,
    string? Uploader,
    string? Site,
    IReadOnlyList<VideoItem> Items);

/// <param name="Index">1-based playlist index to pass back to /api/download; null for a single video.</param>
public sealed record VideoItem(
    int? Index,
    string Title,
    string? Thumbnail,
    double? Duration,
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

    /// <summary>Max entries looked up for multi-video links, to keep lookups fast.</summary>
    public const int MaxItems = 20;

    /// <summary>
    /// YouTube share links often carry a &amp;list= that would expand into a whole playlist;
    /// there the video itself is what's wanted. Elsewhere (X/Twitter, Instagram, Reddit, ...)
    /// a "playlist" is the set of videos in one post, and we want to offer all of them.
    /// </summary>
    public static bool PreferSingleVideo(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase)) &&
        !u.AbsolutePath.StartsWith("/playlist", StringComparison.OrdinalIgnoreCase);

    public async Task<VideoInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        var psi = NewStartInfo();
        var args = new List<string> { "-J", "--no-warnings" };
        if (PreferSingleVideo(url)) args.Add("--no-playlist");
        else args.AddRange(["--playlist-items", $"1:{MaxItems}"]);
        args.AddRange(["--", url]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new YtDlpException("Could not start yt-dlp.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));

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

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static VideoInfo ParseInfo(JsonElement root)
    {
        var items = new List<VideoItem>();
        if (Str(root, "_type") == "playlist" && root.TryGetProperty("entries", out var entries) &&
            entries.ValueKind == JsonValueKind.Array)
        {
            var pos = 0;
            foreach (var e in entries.EnumerateArray())
            {
                pos++;
                if (e.ValueKind != JsonValueKind.Object) continue; // unavailable entries come back as null
                var index = (int?)Num(e, "playlist_index") ?? pos;
                items.Add(ParseItem(e, index));
            }
            if (items.Count == 0) throw new YtDlpException("No downloadable videos found at this link.");
            // A one-video "playlist" is just a video; still pin the index so we download the right thing.
        }
        else
        {
            items.Add(ParseItem(root, null));
        }

        return new VideoInfo(
            Title: Str(root, "title") ?? items[0].Title,
            Uploader: Str(root, "uploader") ?? Str(root, "channel") ?? Str(root, "uploader_id"),
            Site: Str(root, "extractor_key") ?? Str(root, "ie_key"),
            Items: items);
    }

    private static VideoItem ParseItem(JsonElement root, int? index)
    {
        var videoFormats = new List<(int Height, int? Width, string VCodec, long? Size, bool HasAudio)>();
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
                    videoFormats.Add((height!.Value, (int?)Num(f, "width"), vcodec, size, isAudio));
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
            videoFormats.Add(((int)h, (int?)Num(root, "width"), Str(root, "vcodec") ?? "", (long?)Num(root, "filesize"), true));
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
                Label: QualityLabel(group.Key, group.Max(f => f.Width)),
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

        return new VideoItem(
            Index: index,
            Title: Str(root, "title") ?? "video",
            Thumbnail: Str(root, "thumbnail"),
            Duration: Num(root, "duration"),
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

    /// <summary>Labels by the short side, so a 1080x1920 portrait clip reads "1080p", not "1920p".</summary>
    private static string QualityLabel(int height, int? width)
    {
        var portrait = width is > 0 && width < height;
        var p = portrait ? width!.Value : height;
        var label = p switch
        {
            >= 4320 => $"{p}p (8K)",
            >= 2160 => $"{p}p (4K)",
            >= 1440 => $"{p}p (2K)",
            _ => $"{p}p",
        };
        return portrait ? $"{label} · portrait" : label;
    }
}
