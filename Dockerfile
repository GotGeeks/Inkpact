# syntax=docker/dockerfile:1
# Inkpact: the server, the web bundle and the Yjs sidecar in one image. Runs as a non-root user and works with a
# read-only root filesystem: only /data (Data Protection keys, pre-migration backups, attachments) and /tmp need to
# be writable.
#   docker build -t inkpact .
# Tags are pinned with their digest; Dependabot's docker ecosystem moves them.

# The web bundle.
FROM node:26-bookworm-slim@sha256:add5c17c14752c54d2f3d519ec46a3815684d41c33f198bcb09fb998f7dcbf68 AS web
WORKDIR /src
# Node 25 and later no longer ship Corepack: pnpm comes from npm, at the version packageManager names.
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
RUN npm install --global "pnpm@$(node -p "require('./package.json').packageManager.split('@')[1]")"
# Manifests first, so the dependency layer is only rebuilt when they change.
COPY web/package.json web/
COPY server/sidecar/package.json server/sidecar/
RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile --filter web...
COPY web web
# The build type-checks the tests too, and the import's contract test uses the sidecar's builder.
COPY server/sidecar/src server/sidecar/src
RUN pnpm --filter web build

# The sidecar with its production dependencies. `pnpm deploy` puts the store inside the folder, so the sidecar's
# permission model needs no path outside it.
FROM node:26-bookworm-slim@sha256:add5c17c14752c54d2f3d519ec46a3815684d41c33f198bcb09fb998f7dcbf68 AS sidecar
WORKDIR /src
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
RUN npm install --global "pnpm@$(node -p "require('./package.json').packageManager.split('@')[1]")"
COPY web/package.json web/
COPY server/sidecar server/sidecar
RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile --filter sidecar... \
 && pnpm --filter sidecar deploy --prod /out

# The server. The SDK tag is the one in global.json (rollForward is disabled there).
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS server
WORKDIR /src
COPY global.json ./
COPY server/Directory.Build.props server/Directory.Packages.props server/
COPY server/src/Inkpact.Api/Inkpact.Api.csproj server/src/Inkpact.Api/
COPY server/src/Inkpact.Data/Inkpact.Data.csproj server/src/Inkpact.Data/
RUN dotnet restore server/src/Inkpact.Api/Inkpact.Api.csproj
COPY server/src server/src
# The import's allow-list and the words of the export's README are the client's, built into the server.
COPY web/src/editor/allowList.json web/src/editor/
COPY web/src/locales/en.json web/src/locales/nl.json web/src/locales/
# The version the app says it is (run B.8): a release's (0.1.0) or a build of main's (0.1.0+edge.abc1234). The assembly
# version takes only the numbers; the whole text is the informational version, which the app reads. Without it, a
# developer's build: 0.0.0-dev.
ARG INKPACT_VERSION=0.0.0-dev
RUN case "$INKPACT_VERSION" in *[!0-9A-Za-z.+-]*|'') echo "INKPACT_VERSION is not a version: $INKPACT_VERSION" >&2; exit 1;; esac \
 && dotnet publish server/src/Inkpact.Api/Inkpact.Api.csproj -c Release -o /out --no-restore \
      "-p:Version=${INKPACT_VERSION%%[-+]*}" "-p:InformationalVersion=$INKPACT_VERSION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS final

# pg_dump 18: a migration backs the database up first and refuses to run without it, and a dump of a Postgres 18
# server needs a client of the same major version, which the distribution does not have. curl is for the health check, libatomic1 for the Node binary.
# The upgrade takes Ubuntu's security fixes that the base image does not have yet (Microsoft rebuilds it later).
RUN apt-get update \
 && apt-get upgrade -y --no-install-recommends \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg libatomic1 \
 && install -d /usr/share/postgresql-common/pgdg \
 && curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc  && gpg --show-keys --with-colons /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc | grep -q '^fpr:::::::::B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8:$' \
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
    Attachments__Directory=/data/attachments \
    Import__Directory=/data/imports
VOLUME /data
EXPOSE 8080
USER app

# Ready, not only running: unhealthy when the database does not answer (Docker does not restart it; `docker ps` and
# Unraid's Docker tab show it). The start period covers the dump before a migration of a large library.
HEALTHCHECK --interval=30s --timeout=5s --start-period=5m --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/health/ready || exit 1

LABEL org.opencontainers.image.title="Inkpact" \
      org.opencontainers.image.description="Self-hosted, multi-user notes with real-time collaborative editing" \
      org.opencontainers.image.source="https://github.com/GotGeeks/Inkpact" \
      org.opencontainers.image.licenses="AGPL-3.0-only"

ENTRYPOINT ["dotnet", "Inkpact.Api.dll"]
