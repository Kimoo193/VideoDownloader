FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY VideoDownloader/ .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app

RUN apt-get update && apt-get install -y \
    ffmpeg \
    python3 \
    python3-pip \
    python3-dev \
    gcc \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Install yt-dlp latest
RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    -o /usr/local/bin/yt-dlp && chmod +x /usr/local/bin/yt-dlp

# Install curl_cffi — required for --impersonate (TikTok, Instagram, etc.)
# and spotdl for Spotify
RUN pip3 install curl_cffi spotdl --break-system-packages

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "VideoDownloader.dll"]