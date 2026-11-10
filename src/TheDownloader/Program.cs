using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using TheDownloader.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DownloaderOptions>(builder.Configuration.GetSection("Downloader"));
builder.Services.AddSingleton<YtDlp>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddHostedService<CleanupService>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Typically runs behind a reverse proxy providing HTTPS (needed for the iOS share sheet).
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".m4a"] = "audio/mp4";
contentTypes.Mappings[".mkv"] = "video/x-matroska";
contentTypes.Mappings[".webm"] = "video/webm";

var api = app.MapGroup("/api");

api.MapPost("/info", async (UrlRequest req, YtDlp ytDlp, CancellationToken ct) =>
{
    if (!YtDlp.IsValidUrl(req.Url, out var uri))
        return Results.BadRequest(new { error = "Please enter a valid http(s) link." });
    try
    {
        return Results.Ok(await ytDlp.GetInfoAsync(uri!.ToString(), ct));
    }
    catch (YtDlpException ex)
    {
        return Results.UnprocessableEntity(new { error = ex.Message });
    }
});

api.MapPost("/download", (DownloadRequest req, JobManager jobs) =>
{
    if (!YtDlp.IsValidUrl(req.Url, out var uri))
        return Results.BadRequest(new { error = "Please enter a valid http(s) link." });
    var option = req.Option?.Trim() ?? "best";
    if (option is not ("best" or "audio") && !(int.TryParse(option, out var h) && h is > 0 and < 10000))
        return Results.BadRequest(new { error = "Unknown format option." });

    var job = jobs.Start(uri!.ToString(), option);
    return Results.Ok(ToDto(job, jobs));
});

api.MapGet("/jobs/{id}", (string id, JobManager jobs) =>
    jobs.Get(id) is { } job ? Results.Ok(ToDto(job, jobs)) : Results.NotFound(new { error = "This download has expired." }));

api.MapGet("/jobs/{id}/file", (string id, bool? inline, JobManager jobs) =>
{
    var job = jobs.Get(id);
    if (job is not { Status: JobStatus.Done, FilePath: { } path } || !File.Exists(path))
        return Results.NotFound(new { error = "This download has expired." });

    if (!contentTypes.TryGetContentType(path, out var type)) type = "application/octet-stream";
    return Results.File(
        path,
        contentType: type,
        fileDownloadName: inline == true ? null : job.FileName,
        enableRangeProcessing: true);
});

api.MapDelete("/jobs/{id}", (string id, JobManager jobs) =>
{
    if (jobs.Get(id) is { } job) jobs.Remove(job);
    return Results.NoContent();
});

app.Run();

static object ToDto(DownloadJob job, JobManager jobs)
{
    var ext = job.FileName is null ? null : Path.GetExtension(job.FileName).ToLowerInvariant();
    return new
    {
        id = job.Id,
        status = job.Status.ToString().ToLowerInvariant(),
        progress = Math.Round(job.Progress, 1),
        part = job.Part,
        speed = job.Speed,
        eta = job.Eta,
        fileName = job.FileName,
        fileSize = job.FileSize,
        fileUrl = job.Status == JobStatus.Done ? $"api/jobs/{job.Id}/file" : null,
        mimeType = ext switch
        {
            ".mp4" => "video/mp4",
            ".m4a" => "audio/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream",
        },
        error = job.Error,
        expiresAt = jobs.ExpiresAt(job),
    };
}

record UrlRequest(string? Url);
record DownloadRequest(string? Url, string? Option);
