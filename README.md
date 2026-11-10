# The Downloader

A small ASP.NET Core (.NET 10) web wrapper around [yt-dlp](https://github.com/yt-dlp/yt-dlp).
Paste a link, choose a resolution, and save the file. Downloads are deleted after 10 minutes.

## Run

```sh
docker compose up -d --build
# open http://<host>:8080
```

## iPhone / Safari: "Save to Photos"

When the download finishes, the page loads the file into the browser, then shows a **Save to Photos**
button. That button opens the iOS share sheet, where you tap **Save Video**.

- **HTTPS is required for this button.** iOS only allows the share sheet with files on a secure origin.
  Put the container behind a reverse proxy with TLS (Caddy, Traefik, nginx, `tailscale serve`, a
  Cloudflare tunnel, ...). Over plain HTTP the page falls back to a normal download, which goes to Files.
- Options marked **✓ Photos** are H.264/HEVC MP4. The Photos app accepts these. Options marked
  **Files only** (usually YouTube's 1440p and 4K, which are VP9/AV1) may not save to the camera roll.
- To send links from the share sheet in other apps, make an iOS Shortcut that opens
  `https://<host>/?url=<Shortcut Input>`. The page reads the link and looks it up automatically.

## Configuration (environment variables)

| Variable | Default | |
|---|---|---|
| `Downloader__RetentionMinutes` | `10` | How long finished files are kept |
| `Downloader__MaxConcurrentDownloads` | `2` | Parallel yt-dlp downloads |
| `Downloader__DownloadTimeoutMinutes` | `30` | A single download is stopped after this |
| `Downloader__StampCreationDate` | `true` | Set the file's creation date to the download time, so it lands at the front of the iOS camera roll |
| `YTDLP_AUTO_UPDATE` | `true` | Run `yt-dlp -U` when the container starts |

The app has no authentication. Don't expose it to the open internet unless something in front of it
handles auth, such as proxy basic auth, Tailscale, or Cloudflare Access.

## Local dev

Requires `yt-dlp`, `ffmpeg` and `deno` on PATH.

```sh
cd src/TheDownloader && dotnet run
```
