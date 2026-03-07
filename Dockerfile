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

# تثبيت الأدوات المطلوبة
RUN apt-get update && apt-get install -y \
    ffmpeg \
    python3 \
    python3-pip \
    python3-venv \
    curl \
    libcurl4-openssl-dev \
    libssl-dev \
    && rm -rf /var/lib/apt/lists/*

# إنشاء بيئة افتراضية Python وتثبيت الحزم فيها
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# تحديث pip وتثبيت الحزم في البيئة الافتراضية
RUN pip3 install --upgrade pip && \
    pip3 install brotli certifi && \
    pip3 install spotdl --no-cache-dir

# تثبيت yt-dlp (يظل كملف تنفيذي مستقل)
RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    -o /usr/local/bin/yt-dlp && chmod a+rx /usr/local/bin/yt-dlp

# نسخ التطبيق من مرحلة البناء
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VideoDownloader.dll"]