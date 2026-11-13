# Self-hosting Inkpact

Inkpact runs as two containers: the app (server, web client and the Yjs sidecar in one image) and PostgreSQL 18. The image is published to GitHub's container registry for amd64 and arm64.

> Until the first release, the only tag is `edge`: the latest merge to main. Expect rough edges.

## Start it

1. Make a folder, and copy `deploy/docker-compose.yml` and `deploy/.env.example` into it. Rename the second to `.env`.
2. Fill in `POSTGRES_PASSWORD` and `AUTH_SETUP_CODE` in `.env` (any long random strings, for example `openssl rand -base64 24`). The compose file refuses to start without them.
3. `docker compose up -d`
4. Open `http://<host>:8080`. The setup page asks for the setup code and creates the administrator. It closes for good once the first account exists; you can then remove `AUTH_SETUP_CODE` from `.env`.

The app container has a read-only root filesystem, no extra privileges and runs as a non-root user (UID 1654). Only two places are writable: the `inkpact-data` volume at `/data`, and a temporary `/tmp`.

## Install and use pages offline

Serve Inkpact over **HTTPS** to install it or use its offline app shell. Browsers make an exception for `localhost`, but plain HTTP to a LAN address is not a secure context: Inkpact remains usable online there without promising installation or offline reloads. A browser that supports installation may offer it; on iPhone, use Safari's **Add to Home Screen** instead.

First visit Inkpact online and open the pages you want available. After the app shell and a page have been saved on the device, you can reload that page's URL offline, edit it and reconnect later. The status distinguishes changes saved on this device from changes confirmed by the server. Keep the tab open if local storage reports a failure and the server has not confirmed the changes. Tree browsing, search, login, page management, imports and attachment uploads need the server; attachment files themselves are not stored for offline use.

The browser stores page content and recovery copies in IndexedDB, partitioned by the last authenticated account, workspace and page. Signing out clears the active offline account and removes that account's cached pages that the server already has. Pages with edits the server has not confirmed, and recovery copies, stay on the device, because removing them would lose work; after an account switch they remain partitioned and are withheld until the original account is revalidated. The account marker selects local data and does **not** grant server access. Browser-profile access can expose cached notes: this storage is not encrypted by Inkpact. If the browser reports that it could not clear the account marker, do not share that profile.

Browser storage is not a backup. Browsers may deny or evict it, private browsing may clear it, and installed apps or other browser profiles may have separate storage. Inkpact requests persistent storage where supported, but the browser decides whether to grant it. It keeps at most 40 server-confirmed cached pages without pending recovery copies across the profile; older ones must be opened online again. Pages with unconfirmed edits and recovery copies are retained, so local storage can still grow. Back up the server as described below, and sync edits and resolve recovery copies before clearing site data or removing the app.

Application updates do not force open tabs to reload. When an update is ready, Inkpact says so and offers **Update now** once nothing waits to reach the server; it updates only when that is the only Inkpact tab (another tab still runs the old version and needs its files), otherwise it asks you to close the others. Old shell caches are retained while any other Inkpact window is open, including a first-visit tab not yet controlled by a worker, and cleaned up when the update activates without them. Closing all Inkpact tabs and opening the app again also updates it.

## Backup and restore

Two things hold everything, and they belong together: a page refers to its files by id, and a file is only found again through the database.

- **The database** (`inkpact-db`, the Postgres volume): all notes and accounts.
- **`/data`** (`inkpact-data`): the files added to pages (`/data/attachments`), the keys that protect sign-in sessions (`/data/keys`; lose them and everyone signs in again) and the dumps written before each database migration (`/data/backups`). `/data/imports` only holds an export while it is being imported and is left out.

Browser storage (the offline copies) is not a backup.

### Backing up

Run these in the folder with `docker-compose.yml`, while the app runs. `pg_dump` reads one consistent state of the database, so nobody has to stop working.

```bash
mkdir -p backup
docker compose exec -T postgres pg_dump -U inkpact --format=custom inkpact > backup/inkpact.dump
docker compose exec -T app tar -C /data --exclude=./imports -cf - . > backup/data.tar
```

Copy the folder somewhere else than the disk Inkpact is on. The aim is to lose at most a day, so run it every night. A script that keeps the last 14:

```bash
#!/bin/bash
set -euo pipefail
cd /mnt/user/appdata/inkpact            # the folder with docker-compose.yml
target="/mnt/user/backups/inkpact/$(date +%Y-%m-%d)"
mkdir -p "$target"
docker compose exec -T postgres pg_dump -U inkpact --format=custom inkpact > "$target/inkpact.dump"
docker compose exec -T app tar -C /data --exclude=./imports -cf - . > "$target/data.tar"
ls -1d /mnt/user/backups/inkpact/*/ | head -n -14 | xargs -r rm -rf
```

Schedule it with cron (`0 3 * * * /path/to/inkpact-backup.sh`), or on Unraid with the User Scripts plugin (add the script, schedule "Daily").

With a PostgreSQL you already run, `deploy/inkpact.sh backup [folder]` does the same two steps (into `deploy/backups/<time>` when no folder is named).

### Restoring

Use the same version of Inkpact that made the backup, or a newer one. A newer one migrates the database when it starts (and writes its own dump first). The database you restore into must be empty.

```bash
docker compose up -d --wait postgres      # the database alone; on a new machine it is empty
docker compose run --rm --no-deps -T --entrypoint tar app -C /data -xf - < backup/data.tar
docker compose exec -T postgres pg_restore -U inkpact -d inkpact --no-owner --role inkpact < backup/inkpact.dump
docker compose up -d
```

On a machine where Inkpact has already run, the database is not empty. Stop the app and empty it first, then run the commands above:

```bash
docker compose stop app
docker compose exec -T postgres dropdb -U inkpact --maintenance-db=postgres inkpact
docker compose exec -T postgres createdb -U inkpact --maintenance-db=postgres inkpact
```

With a PostgreSQL you already run: `deploy/inkpact.sh restore <folder>`. It stops the app, refuses a database that is not empty (it never overwrites one), restores both parts and starts the app.

**What a restore loses:** everything written after the backup was made. People stay signed in when `/data` was restored with it; without the keys in `/data/keys`, everyone signs in again. Offline edits on a device that the server had not received yet are sent when that device reconnects.

**Checking it worked:** `curl http://<host>:8080/api/health/ready` says `"status":"ok"`, you can sign in, a page with a picture shows it, and search finds a word from a note.

The CI of this repository follows these commands on every image it builds (`deploy/test-backup-restore.sh`): it makes a page with a tag and a file, backs up, removes the volumes, restores and checks that all of it is there.

### The dumps before a migration

Before a new version changes the database, the app writes a dump to `/data/backups`. They are never removed. They are part of `data.tar`, so they make each backup larger; delete the old ones when the version after them has run well for a while:

```bash
docker compose exec app sh -c 'ls -1t /data/backups/* | tail -n +4 | xargs -r rm --'   # keeps the newest three
```

## Health and logs

- **`GET /api/health`** says whether the app's process answers. It reads nothing, so it is the check for "is it up".
- **`GET /api/health/ready`** says whether it can do its work: `{"status":"ok","checks":{"database":"ok","sidecar":"ok"}}`. `degraded` (still `200`) means the part that reads the content of notes is not running: editing and sync go on, while search text, links and imports wait until it is back. `unhealthy` (`503`) means the database does not answer or is waiting for a migration. The answer says nothing else about the server, and is kept for five seconds.

The container's own health check uses the second one, so `docker ps` and Unraid's Docker tab show **unhealthy** when the database is gone. Docker does not restart an unhealthy container; the state is there for you to see. The check waits five minutes after a start, so the dump before a migration of a large library does not count against it.

**The log** (`docker compose logs app`, or Unraid's log viewer) has one line per entry, with the time in UTC. For a log collector (Loki, Graylog), set `Logging__Console__FormatterName: json` on the app: one JSON object per entry. A log line holds ids, counts, codes and durations, never the content of a note, a title, a search, a file name, an address someone typed, or a password. The one exception is the setup code, which is written to the log while there is no account yet. How long logs are kept is Docker's setting (its log driver), not Inkpact's.

**The audit log** (logins, failed logins, exports, password resets) is a table in the database. Events older than 365 days are deleted once a day. `Audit__RetentionDays` changes that; `0` keeps everything.

## A forgotten password

There is no reset by email yet. Whoever can run a command in the container can set a new password for any account:

```bash
docker compose exec app dotnet Inkpact.Api.dll reset-password you@example.com
```

It asks for the new password twice, without showing it (12 to 128 characters). On Unraid, open the container's console and run the part from `dotnet`. With a PostgreSQL you already run: `deploy/inkpact.sh reset-password you@example.com`.

- `--generate` makes a random password and prints it once.
- `--password-stdin` reads it from standard input, for a script. The password is never given as an argument, where the shell's history and the process list would keep it.

The account is signed out everywhere, and a login lockout of that address is cleared. A browser that still has a page open stops syncing within five minutes and then asks to sign in. The command needs the app to have started once on this version: it refuses a database with a migration waiting.

## Version history

Inkpact keeps earlier versions of every page: a few minutes after a page stops changing (at the latest five minutes into
a long edit), and at every rename. Open a page's history from the clock in its header, look at a version, and restore it.
A restore puts back the content and the title, and keeps the page as it was in a version of its own for at least a day,
so a restore can be undone the same way. The page's place, tags, favorites and to-do settings are not touched.

**History is not a backup.** It lives in the same database as the pages: a lost database or volume takes it along. Keep
the backups described above.

The owner of the workspace sets how much is kept under **Settings** (in the account menu); everyone else can read it.

- **Every version** for the first hours (24 by default), **then the newest of each day** until a number of days (30),
  **then the newest of each week** until a number of days (365). Older versions are removed, within the hour.
- **A size limit** (off by default) counts the versions' content, including the text of notes kept as files, and not
  pictures, files or what Postgres adds. When it is reached, the oldest versions go first, also ones younger than the
  windows above; the page before a restore is protected for its day.
- **Turning history off** keeps no new versions; the versions there are stay and are thinned as before. Restoring needs
  history on, as it keeps the page as it was.
- Shortening a window or lowering the limit asks first: the versions that no longer fit are removed, and raising the
  numbers later does not bring them back.

After an upgrade from a version without history, existing pages get their first version in the background, oldest change
first: about two hours per 5,000 pages, without slowing editing down.

## Attachments

A file added to a page is stored once per content (by its SHA-256), however often it is added, under `/data/attachments`. One file may be 50 MB; set `Attachments__MaxFileSize` (in bytes) in the compose file's `environment` to change that. To keep attachments on another disk, mount a second volume at `/data/attachments`, writable by UID 1654. If you change the limit and run behind a proxy, check that the proxy allows request bodies of that size too.

## Exporting everything

**Account menu → Export everything** downloads a zip of every note you can read in the workspace, as Markdown files, with their pictures and files. It needs no Inkpact to read: open it in a text editor, Obsidian, or any tool that reads Markdown folders. A note is `Title.md` and the notes under it are in a folder with the same name beside it; pictures and files are in `_attachments`; links between notes are relative paths; and the title, dates, tags and to-do state of each note are at the top of its file. The `README.md` inside says the same.

This is a copy you can read, **not a replacement for the backup** above: it leaves out the trash, the edit history and accounts, and a note's tags and to-do state are text in its file, not something you can restore from it. Take one now and then, so that your notes are never only in a form that needs Inkpact.

The zip is made while it downloads, so a large library takes a minute and nothing is kept on the server. If the download stops half way it shows as failed in the browser; start it again. A reverse proxy that holds a response back until it is complete, or ends it after a time, needs to let this one through (for Traefik, the default is fine).

## Importing from Joplin

In Joplin desktop, choose File → Export all → JEX (leave encryption off; an encrypted note cannot be imported, and the report lists it). Then use the import page of Inkpact and pick the file.

- **Where the file waits:** the upload is written to `/data/imports` until the import has read it, and deleted when the import ends. Make room for it: it is as large as your export, resources included. A file left by a restart is removed at the next start.
- **How large it may be:** 2 GB. Set `Import__MaxFileSize` (in bytes) in the compose file's `environment` to change it. Behind a reverse proxy, check that the proxy allows request bodies of that size and does not cut a slow upload short (nginx: `client_max_body_size` and `proxy_request_buffering off`).
- **Very long notes:** a page is opened by sending its whole state in one go, so a note is only turned into text up to a limit. A note whose text is over 8,000,000 bytes (`Import__MaxNoteTextBytes`), or whose page would be over 8 MiB (`Import__MaxNoteUpdateBytes`), becomes a page that holds the note as a file (`.md` or `.html`) to download, with the pictures and files it uses; the report says so. When a large note changes in Joplin, the import compacts its page right after replacing the content, so only the new text has to fit; the old and new content together must be within three quarters of `Sync__MaxQueuedBytes` (12 MiB by default), or the note becomes a file. The page of a note is about a fifth larger than its text, so this holds for a note of up to about 5 MB of text; one of 6 MB becomes a file when it changes. If the compaction cannot run at that moment, the background compaction does it later (usually within half a minute). A note over 50 MB cannot be kept and is skipped. Raise these two only together with `Sync__MaxQueuedBytes` (16 MiB by default), which must be at least twice as large as either: the server refuses to start otherwise. A page of over 5 MiB still opens and can be edited, with a notice that it may be slow. When a change from elsewhere removes what you typed offline in a page, the app keeps it in a copy of the page, which may be a page's limit and an eighth more: 9 MiB by default, which raising `Sync__MaxQueuedBytes` raises too. That copy is sent in one request of about 13 MB (the content in base64), to `POST /api/workspaces/{id}/pages`. **A reverse proxy must let requests of that size through on every route, not only on the import:** nginx's default `client_max_body_size` is 1 MB, which would refuse the copy of any page over about 700 KB; set it to at least `20m` (Traefik and Caddy set no limit unless you add one).
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
- **How much of a page is searched:** the first 200,000 characters of its text, about 100 printed pages. A word further on is not found. A note that the import keeps as a file (a very long note) is found by its words like any other page: the import keeps its text for search in a column of its own, and a note imported as a file before that was added gets its text at the next import from Joplin, which converts it again (only when you did not change its page here). The words stay findable if you delete the file from the page by hand.
- **Words joined by punctuation** are found one by one: `pdf` finds `report.pdf`, `example` finds `user@example.com`, `11` finds `2026-11-03`.
- **The first start after an update that changes the search columns** (the update that brought search, and the one that searches notes kept as files) builds them again for every page, which rewrites the pages table once each time: with tens of thousands of long notes, it can take a minute or two before the app answers.
- **The extension:** PostgreSQL's `unaccent` (part of every official image). The migration creates it, which the owner of the database may do; a database owned by another role needs `CREATE EXTENSION unaccent` run once by a superuser before the app starts.

## Updating

```bash
docker compose pull
docker compose up -d
```

**Which image:** releases are published as `ghcr.io/gotgeeks/inkpact:latest`, the newest release. Until the first one is out, the compose file runs `edge`; set `INKPACT_IMAGE` to `ghcr.io/gotgeeks/inkpact:latest` once it is. Each release is also tagged with its version (`0.1.0`) and its minor version (`0.1`); set `INKPACT_IMAGE` to stay on one. The release notes are on [GitHub](https://github.com/GotGeeks/Inkpact/releases); read them before updating while Inkpact is at 0.x. `edge` is every change merged to `main`, for a test instance; it is checked the same way but has had no use yet.

A release's images are published a while after the release itself appears, once they have passed their checks: if `pull` finds nothing new, try again in half an hour.

When the new version changes the database, the app writes a `pg_dump` to `/data/backups` first and stops with a clear message if that fails, before it changes anything. Old dumps are not removed; see "The dumps before a migration" for removing them.

**Which version runs:** **Settings → About Inkpact** says so, for everyone signed in.

**The update notice:** once a day the app asks GitHub for Inkpact's newest release. When it is newer than the running version, the administrator sees it under Settings in the account menu, and **Settings** links to its release notes.

- **What is sent:** one request to `api.github.com` for the public release list, which names the app and its version. Nothing about your install, your users or your notes. The browser never contacts GitHub itself.
- **Turning it off:** set `UpdateCheck__Enabled` to `false` (it is in the compose file, commented out). A build without a version (see "Building the image yourself") never asks.

## Verifying the image

Every published image (each release, and `edge`) is signed with [cosign](https://docs.sigstore.dev/) by the workflow that built it, without a key: the signature names that workflow. To check an image before you run it:

```bash
cosign verify ghcr.io/gotgeeks/inkpact:0.1.0 \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity https://github.com/GotGeeks/Inkpact/.github/workflows/image.yml@refs/heads/main
```

Each image also carries its SBOM (every package in it) and its build provenance (which commit and workflow made it):

```bash
docker buildx imagetools inspect ghcr.io/gotgeeks/inkpact:0.1.0 --format '{{ json .SBOM }}'
docker buildx imagetools inspect ghcr.io/gotgeeks/inkpact:0.1.0 --format '{{ json .Provenance }}'
```

## Behind a reverse proxy

The proxy must terminate HTTPS and send `X-Forwarded-Proto`, (Traefik sends `wss` for the sync WebSocket, which the app reads as `https`) and must pass the `Host` header through unchanged (the sync WebSocket checks that the `Origin` matches). The app trusts forwarded headers from the private ranges (10/8, 172.16/12, 192.168/16), which covers Docker and Unraid. For a proxy outside those, uncomment `Network__TrustedProxies__0` in the compose file. The app expects one proxy in front of it. With two (a Cloudflare or Tailscale tunnel in front of Traefik, for example), set `Network__ForwardLimit` to `2`: otherwise every visitor appears to come from the outer proxy and they share one login limit (ten attempts a minute). WebSockets need no extra proxy setting.

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

Afterwards, **`deploy/inkpact.sh update`** pulls `main` and runs `up`, which builds what changed (every `up` does, `pull_policy: build`) and restarts the app. A migration writes a dump to the data folder's `backups` first. `status`, `logs` and `down` do what they say; `down` leaves the database and the data folder alone. `backup`, `restore` and `reset-password` are described under "Backup and restore" and "A forgotten password". To go back to an earlier version, check out that commit and run `up` (a database that a newer version migrated stays migrated: restore its dump for that).

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

Such an image says it is a development build (`0.0.0-dev`) and never looks for updates. To give it a version, build it with `--build-arg INKPACT_VERSION=0.1.0`, which is what the published images are built with.
