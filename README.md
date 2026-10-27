# Inkpact

Self-hosted, multi-user notes app with real-time collaborative editing. AGPL-3.0, no paid features. Work in progress: see `docs/architecture.md` for the plan.

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
