# syntax=docker/dockerfile:1.7

FROM node:24-bookworm-slim AS web-build
WORKDIR /src
COPY web/package*.json web/
WORKDIR /src/web
RUN npm ci
COPY web/ /src/web/
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY Optimisarr.sln global.json ./
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        ffmpeg \
        gosu \
        passwd \
        tzdata \
    && rm -rf /var/lib/apt/lists/*

COPY docker/entrypoint.sh /entrypoint.sh
COPY --from=api-build /app/publish/ /app/

ENV ASPNETCORE_URLS=http://0.0.0.0:8787 \
    OPTIMISARR_CONFIG_DIR=/config \
    PUID=1000 \
    PGID=1000 \
    UMASK=002

EXPOSE 8787
ENTRYPOINT ["/entrypoint.sh"]
