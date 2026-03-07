FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# نسخ ملف المشروع (موجود في نفس المستوى)
COPY *.csproj .
RUN dotnet restore

# نسخ باقي الملفات
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
# تثبيت المكتبات المطلوبة لـ impersonate
RUN apt-get update && apt-get install -y \
    ffmpeg \
    python3 \
    python3-pip \
    curl \
    libcurl4-openssl-dev \
    libssl-dev \
    && rm -rf /var/lib/apt/lists/*

# تثبيت yt-dlp بأحدث إصدار
RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    -o /usr/local/bin/yt-dlp && chmod a+rx /usr/local/bin/yt-dlp

# تثبيت مكتبات Python الإضافية لـ impersonate
RUN pip3 install --upgrade pip && \
    pip3 install brotli certifi && \
    pip3 install spotdl --break-system-packages --no-cache-dir
# تثبيت spotdl
RUN pip3 install spotdl --break-system-packages --no-cache-dir

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VideoDownloader.dll"]
