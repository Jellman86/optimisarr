# syntax=docker/dockerfile:1.7

FROM node:26-bookworm-slim AS web-build
# The build context excludes `.git`, so the short commit is passed in here and
# read by vite.config.ts (it falls back to "unknown" when absent).
ARG GIT_HASH=unknown
ENV GIT_HASH=${GIT_HASH}
WORKDIR /src
COPY web/package*.json web/
WORKDIR /src/web
RUN npm ci
COPY web/ /src/web/
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY Optimisarr.slnx global.json ./
COPY src/Optimisarr.Api/Optimisarr.Api.csproj src/Optimisarr.Api/
COPY src/Optimisarr.Core/Optimisarr.Core.csproj src/Optimisarr.Core/
COPY src/Optimisarr.Data/Optimisarr.Data.csproj src/Optimisarr.Data/
COPY tests/Optimisarr.Tests/Optimisarr.Tests.csproj tests/Optimisarr.Tests/
RUN dotnet restore
COPY . .
COPY --from=web-build /src/src/Optimisarr.Api/wwwroot/ src/Optimisarr.Api/wwwroot/
RUN dotnet publish src/Optimisarr.Api/Optimisarr.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Dedicated, dependency-free VMAF measurement binary. This image is multi-architecture
# (linux/amd64 + linux/arm64), includes libvmaf, and is pinned by manifest digest so an upstream
# tag change cannot silently alter Optimisarr's verification toolchain.
FROM mwader/static-ffmpeg:8.1.2@sha256:33f770f812cbfc3de96c547157fc9faf8bd95a36481753439ffa761045167585 AS vmaf-ffmpeg

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# jellyfin-ffmpeg drives transcoding and hardware detection. It supplies NVENC and, crucially, the
# Intel iHD driver + oneVPL runtime for QSV/VA-API on iGPUs like the N100, but its Debian build does
# not include libvmaf. Quality measurement therefore uses the dedicated static binary copied below;
# loudness and image SSIM share it so every optional measurement uses one known toolchain.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        ffmpeg \
        gnupg \
        gosu \
        libimage-exiftool-perl \
        passwd \
        tzdata \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key \
        | gpg --dearmor -o /etc/apt/keyrings/jellyfin.gpg \
    && . /etc/os-release \
    && printf 'Types: deb\nURIs: https://repo.jellyfin.org/%s\nSuites: %s\nComponents: main\nArchitectures: %s\nSigned-By: /etc/apt/keyrings/jellyfin.gpg\n' \
        "$ID" "$VERSION_CODENAME" "$(dpkg --print-architecture)" \
        > /etc/apt/sources.list.d/jellyfin.sources \
    && apt-get update \
    && apt-get install -y --no-install-recommends jellyfin-ffmpeg7 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=vmaf-ffmpeg /ffmpeg /usr/local/lib/optimisarr/ffmpeg-vmaf
RUN /usr/local/lib/optimisarr/ffmpeg-vmaf -hide_banner -filters 2>&1 \
    | grep -Eq '^[[:space:]].*[[:space:]]libvmaf[[:space:]]'

COPY --chmod=0755 docker/entrypoint.sh /entrypoint.sh
COPY --from=api-build /app/publish/ /app/

ENV ASPNETCORE_URLS=http://0.0.0.0:8787 \
    OPTIMISARR_CONFIG_DIR=/config \
    OPTIMISARR_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg \
    OPTIMISARR_FFPROBE=/usr/lib/jellyfin-ffmpeg/ffprobe \
    OPTIMISARR_FFMPEG_VMAF=/usr/local/lib/optimisarr/ffmpeg-vmaf \
    PUID=1000 \
    PGID=1000 \
    UMASK=002

EXPOSE 8787
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:8787/api/ready || exit 1
ENTRYPOINT ["/entrypoint.sh"]
