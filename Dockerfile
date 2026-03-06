FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app

RUN apt-get update && apt-get install -y \
    ffmpeg python3 python3-pip curl \
    && rm -rf /var/lib/apt/lists/*

# yt-dlp: دايمًا آخر إصدار عند كل deploy
# ده بيحل 90% من مشاكل الحظر لأن YouTube بيتغير كتير
RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    -o /usr/local/bin/yt-dlp && chmod +x /usr/local/bin/yt-dlp

# spotdl
RUN pip3 install spotdl --break-system-packages

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "VideoDownloader.dll"]