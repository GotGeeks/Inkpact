# syntax=docker/dockerfile:1
# Inkpact: the server, the web bundle and the Yjs sidecar in one image. Runs as a non-root user and works with a
# read-only root filesystem: only /data (Data Protection keys, pre-migration backups, attachments) and /tmp need to
# be writable.
#   docker build -t inkpact .
# Tags are pinned with their digest; Dependabot's docker ecosystem moves them.

# The web bundle.
FROM node:24-bookworm-slim@sha256:55fb5f5a527ea2c5c4a95f963e48ef90aa38c7b346054fdc8ee9957a2b55d205 AS web
WORKDIR /src
RUN corepack enable
# Manifests first, so the dependency layer is only rebuilt when they change.
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY web/package.json web/
COPY server/sidecar/package.json server/sidecar/
RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile --filter web...
COPY web web
RUN pnpm --filter web build

# The sidecar with its production dependencies. `pnpm deploy` puts the store inside the folder, so the sidecar's
# permission model needs no path outside it.
FROM node:24-bookworm-slim@sha256:55fb5f5a527ea2c5c4a95f963e48ef90aa38c7b346054fdc8ee9957a2b55d205 AS sidecar
WORKDIR /src
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY web/package.json web/
COPY server/sidecar server/sidecar
RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile --filter sidecar... \
 && pnpm --filter sidecar deploy --prod /out

# The server. The SDK tag is the one in global.json (rollForward is disabled there).
FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS server
WORKDIR /src
COPY global.json ./
COPY server/Directory.Build.props server/Directory.Packages.props server/
COPY server/src/Inkpact.Api/Inkpact.Api.csproj server/src/Inkpact.Api/
COPY server/src/Inkpact.Data/Inkpact.Data.csproj server/src/Inkpact.Data/
RUN dotnet restore server/src/Inkpact.Api/Inkpact.Api.csproj
COPY server/src server/src
RUN dotnet publish server/src/Inkpact.Api/Inkpact.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS final

# pg_dump 18: a migration backs the database up first and refuses to run without it, and a dump of a Postgres 18
# server needs a client of the same major version, which the distribution does not have. curl is for the health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
 && install -d /usr/share/postgresql-common/pgdg \
 && curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc \
 && . /etc/os-release \
 && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client-18 \
 && apt-get purge -y gnupg \
 && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

# Node, for the sidecar: one binary from the official image.
COPY --from=web /usr/local/bin/node /usr/local/bin/node

WORKDIR /app
COPY --from=server /out ./
COPY --from=sidecar /out ./sidecar
COPY --from=web /src/web/dist ./wwwroot
RUN install -d -o app -g app /data

# Kestrel listens on 8080 (the base image sets ASPNETCORE_HTTP_PORTS; a non-root user cannot bind 80). Keys,
# backups and attachments go on the /data volume.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DataProtection__KeysDirectory=/data/keys \
    Database__BackupDirectory=/data/backups \
    Attachments__Directory=/data/attachments
VOLUME /data
EXPOSE 8080
USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/health || exit 1

LABEL org.opencontainers.image.title="Inkpact" \
      org.opencontainers.image.description="Self-hosted, multi-user notes with real-time collaborative editing" \
      org.opencontainers.image.source="https://github.com/GotGeeks/Inkpact" \
      org.opencontainers.image.licenses="AGPL-3.0-only"

ENTRYPOINT ["dotnet", "Inkpact.Api.dll"]
