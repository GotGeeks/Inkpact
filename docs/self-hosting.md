# Self-hosting Inkpact

Inkpact runs as two containers: the app (server, web client and the Yjs sidecar in one image) and PostgreSQL 18. The image is published to GitHub's container registry for amd64 and arm64.

> Until the first release, the only tag is `edge`: the latest merge to main. Expect rough edges.

## Start it

1. Make a folder, and copy `deploy/docker-compose.yml` and `deploy/.env.example` into it. Rename the second to `.env`.
2. Fill in `POSTGRES_PASSWORD` and `AUTH_SETUP_CODE` in `.env` (any long random strings, for example `openssl rand -base64 24`). The compose file refuses to start without them.
3. `docker compose up -d`
4. Open `http://<host>:8080`. The setup page asks for the setup code and creates the administrator. It closes for good once the first account exists; you can then remove `AUTH_SETUP_CODE` from `.env`.

The app container has a read-only root filesystem, no extra privileges and runs as a non-root user (UID 1654). Only two places are writable: the `inkpact-data` volume at `/data`, and a temporary `/tmp`.

## What to back up

- **`inkpact-db`** (the Postgres volume): all notes and accounts. Back it up with `pg_dump` while running, or stop the stack and copy the volume.
- **`inkpact-data`** (`/data`): the keys that protect sign-in sessions (`/data/keys`; lose them and everyone signs in again) and the dumps written before each database migration (`/data/backups`).

## Updating

```bash
docker compose pull
docker compose up -d
```

When the new version changes the database, the app writes a `pg_dump` to `/data/backups` first and stops with a clear message if that fails, before it changes anything. Old dumps are not removed; delete them when you no longer need them.

## Behind a reverse proxy

The proxy must terminate HTTPS and send `X-Forwarded-Proto`, and must pass the `Host` header through unchanged (the sync WebSocket checks that the `Origin` matches). The app trusts forwarded headers from the private ranges (10/8, 172.16/12, 192.168/16), which covers Docker and Unraid. For a proxy outside those, uncomment `Network__TrustedProxies__0` in the compose file. WebSockets need no extra proxy setting.

The app sets its own security headers, including a strict Content-Security-Policy. Add only HSTS in the proxy.

### Traefik example

With Traefik in the same Docker network, remove the `ports` section from `app` and add labels (names to match your setup):

```yaml
    labels:
      - traefik.enable=true
      - traefik.http.routers.inkpact.rule=Host(`inkpact.example.com`)
      - traefik.http.routers.inkpact.entrypoints=websecure
      - traefik.http.routers.inkpact.tls.certresolver=letsencrypt
      - traefik.http.services.inkpact.loadbalancer.server.port=8080
    networks:
      - default
      - traefik
```

with the `traefik` network declared as external at the bottom of the file.

## Unraid

Use the Docker Compose Manager plugin: add a stack, paste the compose file and the `.env`, and point the volumes at `/mnt/user/appdata/inkpact/...` if you prefer bind mounts (the folder for `/data` must be writable by UID 1654). A Community Apps template comes with the first release.

## Building the image yourself

```bash
docker build -t inkpact .
INKPACT_IMAGE=inkpact docker compose up -d
```
