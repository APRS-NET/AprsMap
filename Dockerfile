# syntax=docker/dockerfile:1
# The gateway image: ASP.NET Core host + engine + the browser app as static files. Docker-first deployment for the web
# app (docs/deployment.md). Multi-arch: the build stage always runs natively and cross-publishes for TARGETARCH.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
# Release builds pass the tag's version; MinVer can't see git history inside the build context.
ARG VERSION=
WORKDIR /src

# Emscripten (wasm-tools) needs Python to link SkiaSharp into the browser app.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*
RUN dotnet workload install wasm-tools

# Restore first, from project files only, so the layer is cached until a dependency changes.
COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY .editorconfig BannedSymbols*.txt ./
COPY build/ build/
COPY src/AprsMap.Contracts/*.csproj src/AprsMap.Contracts/
COPY src/AprsMap.Core/*.csproj src/AprsMap.Core/
COPY src/AprsMap.Tiles/*.csproj src/AprsMap.Tiles/
COPY src/AprsMap.Client.Remote/*.csproj src/AprsMap.Client.Remote/
COPY src/AprsMap.UI/*.csproj src/AprsMap.UI/
COPY src/AprsMap.Browser/*.csproj src/AprsMap.Browser/
COPY src/AprsMap.Hosting/*.csproj src/AprsMap.Hosting/
COPY src/AprsMap.Gateway/*.csproj src/AprsMap.Gateway/
RUN dotnet restore src/AprsMap.Browser/AprsMap.Browser.csproj \
    && dotnet restore src/AprsMap.Gateway/AprsMap.Gateway.csproj -a $TARGETARCH

COPY src/ src/
RUN set -eu; \
    version=""; \
    if [ -n "$VERSION" ]; then version="-p:MinVerVersionOverride=$VERSION"; fi; \
    dotnet publish src/AprsMap.Browser/AprsMap.Browser.csproj -c Release -o /out/browser $version; \
    dotnet publish src/AprsMap.Gateway/AprsMap.Gateway.csproj -c Release -a $TARGETARCH --no-restore \
        -o /out/gateway $version; \
    cp -r /out/browser/wwwroot/. /out/gateway/wwwroot/; \
    mkdir -p /out/data

# Chiseled: no shell or package manager, runs as the non-root 'app' user, listens on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /out/gateway/ ./
# Settings (station profiles) and the map's tile cache live in /data; mount a volume there to keep them. Owned by the
# image's 'app' user.
COPY --from=build --chown=$APP_UID:$APP_UID /out/data /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    PATH="/app:${PATH}" \
    Engine__SettingsFolder=/data \
    Tiles__CacheFolder=/data/tiles
VOLUME /data
EXPOSE 8080
LABEL org.opencontainers.image.title="AprsMap gateway" \
      org.opencontainers.image.description="APRS map: engine, web UI and SignalR gateway" \
      org.opencontainers.image.source="https://github.com/APRS-NET/AprsMap" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
ENTRYPOINT ["./aprsmap-gateway"]
