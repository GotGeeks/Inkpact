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
- **`inkpact-data`** (`/data`): the files added to pages (`/data/attachments`; `/data/imports` only holds an export while it is being imported and need not be backed up), the keys that protect sign-in sessions (`/data/keys`; lose them and everyone signs in again) and the dumps written before each database migration (`/data/backups`).

Back up the database and `/data` together: a page refers to its files by id, and a file is only found again through the database.

## Attachments

A file added to a page is stored once per content (by its SHA-256), however often it is added, under `/data/attachments`. One file may be 50 MB; set `Attachments__MaxFileSize` (in bytes) in the compose file's `environment` to change that. To keep attachments on another disk, mount a second volume at `/data/attachments`, writable by UID 1654. If you change the limit and run behind a proxy, check that the proxy allows request bodies of that size too.

## Importing from Joplin

In Joplin desktop, choose File → Export all → JEX (leave encryption off; an encrypted note cannot be imported, and the report lists it). Then use the import page of Inkpact and pick the file.

- **Where the file waits:** the upload is written to `/data/imports` until the import has read it, and deleted when the import ends. Make room for it: it is as large as your export, resources included. A file left by a restart is removed at the next start.
- **How large it may be:** 2 GB. Set `Import__MaxFileSize` (in bytes) in the compose file's `environment` to change it. Behind a reverse proxy, check that the proxy allows request bodies of that size and does not cut a slow upload short (nginx: `client_max_body_size` and `proxy_request_buffering off`).
- **Very long notes:** a page is opened by sending its whole state in one go, so a note is only turned into text up to a limit. A note whose text is over 8,000,000 bytes (`Import__MaxNoteTextBytes`), or whose page would be over 8 MiB (`Import__MaxNoteUpdateBytes`), becomes a page that holds the note as a file (`.md` or `.html`) to download, with the pictures and files it uses; the report says so. A changed note counts with what the page already holds until it is compacted, so a note of over about 4 MiB that you change in Joplin becomes a file at the next import. A note over 50 MB cannot be kept and is skipped. Raise these two only together with `Sync__MaxQueuedBytes` (16 MiB by default), which must be at least twice as large as either: the server refuses to start otherwise. A page of over 5 MiB still opens and can be edited, with a notice that it may be slow.
- **One at a time:** a workspace takes one import at a time, and the server runs imports one after the other.
- **If the app stops during an import,** the import is marked as interrupted when it starts again. Upload the same file again: what was already imported is skipped, the rest is added.
- **Importing the same file twice** adds nothing; the report says every item was already imported.

### Importing again

While you use Joplin and Inkpact side by side, export again and import the new file. Nothing is duplicated: every note, notebook and tag is recognised by its Joplin id.

- **Changed only in Joplin:** taken over. That is a note's text (also on a page someone has open), title, notebook, tags, to-do state, completion and due date, a notebook's title and place, and a tag's name.
- **Changed only in Inkpact:** left alone, and not reported.
- **Changed in both:** what you changed in Inkpact stays. For the text of a note, Joplin's version becomes a new page right after it, titled with " (Joplin)" added. For a title, notebook, tag name or to-do field, the value in Inkpact stays and the report says so once.
- **Deleted in Joplin:** a note or notebook that is in Joplin's trash goes to the trash in Inkpact too, where you can restore it, unless you changed it here. A note that is missing from the export is never deleted: an export can be of one notebook. A page that the import put in the trash is not restored when you restore the note in Joplin; restore it in Inkpact.
- **Links between notes** become links to the pages, also to a note that you import later.
- **The first import after an update of the converter** converts the pages you did not change again, so a better conversion reaches notes that are already imported. A page you changed keeps its content.

## Search

The search box (run 1.17) finds a page by its title, its tags and its text, in the current workspace. Capitals and accents do not matter (`cafe` finds `Café`), and a word that is only half typed already finds its page (`vergad` finds `vergadering`), in English and Dutch alike: words are not reduced to their stems. A page in the trash, or under one, is not found.

- **When a word becomes findable:** a title or a tag at once; the text of a page after the page has been compacted, which is when it has had two minutes without an edit (`Compaction__IdleAfter`, `00:02:00` by default) or 500 edits.
- **How much of a page is searched:** the first 200,000 characters of its text, about 100 printed pages. A word further on is not found. A note that the import keeps as a file is found by its title and file names only.
- **The extension:** PostgreSQL's `unaccent` (part of every official image). The migration creates it, which the owner of the database may do; a database owned by another role needs `CREATE EXTENSION unaccent` run once by a superuser before the app starts.

## Updating

```bash
docker compose pull
docker compose up -d
```

When the new version changes the database, the app writes a `pg_dump` to `/data/backups` first and stops with a clear message if that fails, before it changes anything. Old dumps are not removed; delete them when you no longer need them.

## Behind a reverse proxy

The proxy must terminate HTTPS and send `X-Forwarded-Proto`, (Traefik sends `wss` for the sync WebSocket, which the app reads as `https`) and must pass the `Host` header through unchanged (the sync WebSocket checks that the `Origin` matches). The app trusts forwarded headers from the private ranges (10/8, 172.16/12, 192.168/16), which covers Docker and Unraid. For a proxy outside those, uncomment `Network__TrustedProxies__0` in the compose file. WebSockets need no extra proxy setting.

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

Use the Docker Compose Manager plugin: add a stack, paste the compose file and the `.env`, and point the volumes at `/mnt/user/appdata/inkpact/...` if you prefer bind mounts (the folder for `/data` must be writable by UID 1654). A Community Apps template comes with the first release. If Unraid runs a PostgreSQL server already, use the next section instead.

## With a PostgreSQL you already run

`deploy/docker-compose.external-db.yml` runs the app alone against a PostgreSQL server you have (on Unraid, a NAS, another host), with an image built from a checkout of this repository, so a change on `main` is running as soon as it is built. `deploy/inkpact.sh` does the work; it needs bash, git, Docker with BuildKit and Docker Compose (on Unraid: the Docker Compose Manager plugin).

1. **Settings:** copy `deploy/external-db.env.example` to `deploy/.env` and fill it in: where Postgres is (`DB_HOST`, the host's LAN address from a container, not `localhost`; or the Postgres container's name with `INKPACT_NETWORK` set to its network), the user and database to use, the port, and where the app's files go (`INKPACT_DATA`, for example `/mnt/user/appdata/inkpact`).
2. **`deploy/inkpact.sh init`:** makes the data folder and gives it to UID 1654. With `PG_CONTAINER` set (the Postgres container's name), it also creates the user and the database in it, and says so when the server is not PostgreSQL 18. Without it, create them yourself: `CREATE ROLE inkpact LOGIN PASSWORD '...'; CREATE DATABASE inkpact OWNER inkpact;`.
3. **`deploy/inkpact.sh build`**, then **`deploy/inkpact.sh up`**. The first build takes a few minutes; later ones reuse its layers.
4. **`deploy/inkpact.sh setup-code`** shows the code for creating the administrator. Open `http://<host>:<INKPACT_PORT>` and create it.

Afterwards, **`deploy/inkpact.sh update`** pulls `main` and runs `up`, which builds what changed (every `up` does, `pull_policy: build`) and restarts the app. A migration writes a dump to the data folder's `backups` first. `status`, `logs` and `down` do what they say; `down` leaves the database and the data folder alone. To go back to an earlier version, check out that commit and run `up` (a database that a newer version migrated stays migrated: restore its dump for that).

**Unraid, with the Docker Compose Manager plugin** (no script):

1. Once, in the terminal: the data folder (`mkdir -p /mnt/user/appdata/inkpact && chown -R 1654:1654 /mnt/user/appdata/inkpact`), and the user and database in your Postgres container (`docker exec -it <postgres container> psql -U postgres`, then the two statements above).
2. **Docker → Add New Stack**, name it `inkpact`. Edit its compose file: paste `deploy/docker-compose.external-db.yml`. Edit its env file: paste `deploy/external-db.env.example` and fill it in, with `INKPACT_SOURCE` set to your checkout (for example `/mnt/user/appdata/inkpact-src`) or to `https://github.com/GotGeeks/Inkpact.git#main`, which needs no checkout.
3. **Compose Up** builds the image (a few minutes the first time) and starts the app. The first-run setup code is in the stack's log.
4. **To update:** with a checkout, `git pull` in it first; then **Compose Up** again. With the GitHub address, Compose Up alone builds the newest `main`. Only what changed is built again.

To join the network your Postgres container is on (and use its name as `DB_HOST`), add this to the stack's compose file:

```yaml
networks:
  default:
    name: proxynet # the Postgres container's network
    external: true
```

**PostgreSQL:** Inkpact makes its own tables and one extension that ships with PostgreSQL (`unaccent`, for search; see "Search"). It is tested on PostgreSQL 18. An older server should work but is not tested; a newer one cannot be backed up by the image's `pg_dump` 18 before a migration, and the app then does not start. If the app cannot connect, check that the server accepts connections from the container's address (`pg_hba.conf`), or put both containers on one network with `INKPACT_NETWORK`. A password with a `;` in it does not fit the connection string: choose another.

## Building the image yourself

```bash
docker build -t inkpact .
INKPACT_IMAGE=inkpact docker compose up -d
```
