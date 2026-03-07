FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "VideoDownloader.csproj"
RUN dotnet publish "VideoDownloader.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app

RUN apt-get update && apt-get install -y \
    ffmpeg python3 python3-pip python3-dev gcc curl \
    && rm -rf /var/lib/apt/lists/*

# Always get the LATEST yt-dlp binary
RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    -o /usr/local/bin/yt-dlp && chmod +x /usr/local/bin/yt-dlp

# curl_cffi >= 0.7.0 is required for chrome-131 impersonation support
RUN pip3 install --break-system-packages --upgrade \
    "curl_cffi>=0.7.0" \
    "spotdl==4.4.3"

# Verify chrome-131 is available (shows in build log)
RUN yt-dlp --list-impersonate-targets 2>&1 | grep -i chrome || echo "WARNING: chrome targets not found"

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "VideoDownloader.dll"]