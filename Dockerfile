FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# نسخ ملف المشروع (الموجود في نفس مستوى Dockerfile)
COPY *.sln .
COPY VideoDownloader/*.csproj ./VideoDownloader/
RUN dotnet restore "VideoDownloader/VideoDownloader.csproj"

# نسخ باقي الملفات
COPY VideoDownloader/. ./VideoDownloader/
WORKDIR /src/VideoDownloader
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app

# تثبيت الأدوات المطلوبة
RUN apt-get update && apt-get install -y \
    ffmpeg \
    python3 \
    python3-pip \
    curl \
    && rm -rf /var/lib/apt/lists/*

# تثبيت yt-dlp
RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    -o /usr/local/bin/yt-dlp && chmod a+rx /usr/local/bin/yt-dlp

# تثبيت spotdl
RUN pip3 install spotdl --break-system-packages --no-cache-dir

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VideoDownloader.dll"]
