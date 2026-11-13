# Inkpact

[![Latest release](https://img.shields.io/github/v/release/GotGeeks/Inkpact)](https://github.com/GotGeeks/Inkpact/releases/latest)

Self-hosted, multi-user notes app with real-time collaborative editing. AGPL-3.0, no paid features. Work in progress: see `docs/architecture.md` for the plan.

## Status

Inkpact is in early releases (0.x): it works, and is used daily by its maintainer, but it is not announced yet and can still change in ways that need care when updating (the release notes say so). What works today: accounts and login, notebooks and notes in a tree, the editor with real-time sync between devices, attachments, search, tags, favorites, links between notes with backlinks, version history with restore, importing from Joplin (JEX) and exporting to Markdown, and offline use as an installed app (PWA). Not there yet: sharing with other people, to-dos and reminders in the interface, and the desktop app. The roadmap, with what each run delivers, is in `docs/architecture.md`.

## Run it with Docker

```bash
cp deploy/.env.example deploy/.env   # set POSTGRES_PASSWORD and AUTH_SETUP_CODE
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d
```

Then open `http://localhost:8080`. The full guide (backups, updating, Traefik, Unraid) is in `docs/self-hosting.md`.

## First start

1. Start Inkpact with a PostgreSQL database (the connection string is `ConnectionStrings__Inkpact`).
2. Look in the server log for the line that starts with `FIRST-RUN SETUP`. It contains the **setup code**.
3. Open Inkpact in a browser. The setup page asks for the code and creates the administrator account.

Instead of a generated code you can set your own with `Auth__SetupCode`. The setup page closes for good once the first account exists.

Behind a reverse proxy (Traefik, nginx) that terminates HTTPS, the proxy must send `X-Forwarded-Proto`. It is trusted from loopback and private network addresses by default; `Network__TrustedProxies__0`, `__1`, and so on take CIDR ranges to change that.

Inkpact runs as one instance. Sessions and the keys that protect them live in the database and on the data volume (`DataProtection__KeysDirectory`, default `data/keys`): keep that volume across restarts.
