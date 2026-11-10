# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/TheDownloader/TheDownloader.csproj src/TheDownloader/
RUN dotnet restore src/TheDownloader/TheDownloader.csproj
COPY src/ src/
RUN dotnet publish src/TheDownloader/TheDownloader.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG TARGETARCH

RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg ca-certificates curl unzip \
 && rm -rf /var/lib/apt/lists/*

# yt-dlp (standalone build) - kept in a dir the app user owns so it can self-update on start.
RUN mkdir -p /opt/yt-dlp \
 && case "$TARGETARCH" in arm64) f=yt-dlp_linux_aarch64 ;; *) f=yt-dlp_linux ;; esac \
 && curl -fsSL "https://github.com/yt-dlp/yt-dlp/releases/latest/download/$f" -o /opt/yt-dlp/yt-dlp \
 && chmod +x /opt/yt-dlp/yt-dlp \
 && ln -s /opt/yt-dlp/yt-dlp /usr/local/bin/yt-dlp

# Deno: yt-dlp needs a JavaScript runtime for YouTube.
RUN case "$TARGETARCH" in arm64) a=aarch64 ;; *) a=x86_64 ;; esac \
 && curl -fsSL "https://github.com/denoland/deno/releases/latest/download/deno-$a-unknown-linux-gnu.zip" -o /tmp/deno.zip \
 && unzip -q /tmp/deno.zip -d /usr/local/bin \
 && rm /tmp/deno.zip \
 && deno --version

RUN mkdir -p /downloads && chown -R app:app /downloads /opt/yt-dlp

WORKDIR /app
COPY --from=build /app .
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Downloader__DownloadDirectory=/downloads \
    Downloader__RetentionMinutes=10 \
    YTDLP_AUTO_UPDATE=true
EXPOSE 8080
ENTRYPOINT ["docker-entrypoint.sh"]
