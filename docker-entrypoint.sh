#!/bin/sh
set -e
# Sites break yt-dlp regularly; grab the newest release on each start.
if [ "$YTDLP_AUTO_UPDATE" = "true" ]; then
  yt-dlp -U || echo "yt-dlp update failed, continuing with $(yt-dlp --version)"
fi
exec dotnet TheDownloader.dll "$@"
