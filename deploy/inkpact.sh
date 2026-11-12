#!/usr/bin/env bash
# Builds Inkpact from this checkout and runs it against a PostgreSQL server you already run. Settings: deploy/.env (copy
# deploy/external-db.env.example). See docs/self-hosting.md, "With a PostgreSQL you already run".
#
#   deploy/inkpact.sh init         make the data folder, and the database user and database when PG_CONTAINER is set
#   deploy/inkpact.sh build        build the image from this checkout, without starting it
#   deploy/inkpact.sh up           build what changed, and start or restart the app
#   deploy/inkpact.sh update       git pull, then up
#   deploy/inkpact.sh setup-code   the code for creating the administrator, while there is no account yet
#   deploy/inkpact.sh status       the container, whether the app answers and whether it is ready
#   deploy/inkpact.sh logs         follow the app's log
#   deploy/inkpact.sh backup [folder]     the database and the data folder into a new folder (default: backups/<time>)
#   deploy/inkpact.sh restore <folder>    put a backup back; the database must be empty
#   deploy/inkpact.sh reset-password <email> [--generate | --password-stdin]   a new password for an account
#   deploy/inkpact.sh down         stop and remove the container (the database and the data folder stay)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
env_file="${INKPACT_ENV_FILE:-$here/.env}"
compose_file="$here/docker-compose.external-db.yml"
override=""
cleanup() {
  if [[ -n "$override" ]]; then rm -f "$override"; fi
}
trap cleanup EXIT

die() {
  echo "inkpact: $*" >&2
  exit 1
}

load_env() {
  [[ -f "$env_file" ]] || die "no settings in $env_file: copy $here/external-db.env.example there and fill it in"
  set -a
  # shellcheck disable=SC1090
  source "$env_file"
  set +a
  [[ -n "${DB_HOST:-}" ]] || die "set DB_HOST in $env_file"
  [[ -n "${DB_PASSWORD:-}" ]] || die "set DB_PASSWORD in $env_file"
  container="${INKPACT_CONTAINER:-inkpact}"
}

compose() {
  local files=(-f "$compose_file")
  if [[ -n "${INKPACT_NETWORK:-}" ]]; then
    # Joining a network that exists already (the Postgres container's) instead of one of its own.
    [[ -n "$override" ]] || override="$(mktemp)"
    printf 'networks:\n  default:\n    name: %s\n    external: true\n' "$INKPACT_NETWORK" >"$override"
    files+=(-f "$override")
  fi
  if docker compose version >/dev/null 2>&1; then
    docker compose --project-directory "$here" --env-file "$env_file" "${files[@]}" "$@"
  elif command -v docker-compose >/dev/null 2>&1; then
    docker-compose --project-directory "$here" --env-file "$env_file" "${files[@]}" "$@"
  else
    die "Docker Compose is needed (on Unraid: the Docker Compose Manager plugin)"
  fi
}

cmd_init() {
  local data="${INKPACT_DATA:-}"
  if [[ "$data" == /* ]]; then
    mkdir -p "$data"
    # The app runs as UID 1654 and writes its keys, attachments and backups here.
    if [[ "$(id -u)" == 0 ]]; then
      chown -R 1654:1654 "$data"
    elif [[ "$(stat -c %u "$data")" != 1654 ]]; then
      echo "inkpact: $data must be writable by UID 1654: sudo chown -R 1654:1654 $data" >&2
    fi
    echo "Data folder: $data"
  else
    echo "Data: the Docker volume ${data:-inkpact-data}"
  fi

  if [[ -z "${PG_CONTAINER:-}" ]]; then
    echo "PG_CONTAINER is not set: create the user ${DB_USER:-inkpact} and the database ${DB_NAME:-inkpact} (owned by it) yourself."
    return
  fi

  local su="${PG_SUPERUSER:-postgres}"
  local version
  version="$(docker exec "$PG_CONTAINER" psql -U "$su" -tAc 'show server_version_num')"
  if ((version / 10000 != 18)); then
    echo "inkpact: this is PostgreSQL $((version / 10000)); Inkpact is tested on 18. Older should work; newer than 18 cannot be backed up before a migration, and the app then does not start." >&2
  fi

  # The values go in as psql variables, so a password with quotes in it needs no escaping here.
  docker exec -i "$PG_CONTAINER" psql -U "$su" -v ON_ERROR_STOP=1 \
    -v user="${DB_USER:-inkpact}" -v pass="$DB_PASSWORD" -v db="${DB_NAME:-inkpact}" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'user', :'pass') WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'user') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', :'db', :'user') WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'db') \gexec
SQL
  echo "Database ${DB_NAME:-inkpact} and user ${DB_USER:-inkpact} are there (an existing user keeps its password)."
}

cmd_build() {
  # From this checkout (or INKPACT_SOURCE), with BuildKit, reusing every layer that did not change.
  compose build
}

cmd_up() {
  # The compose file builds on every up (pull_policy: build), and a new image recreates the container.
  compose up -d
  echo "Inkpact: http://<this host>:${INKPACT_PORT:-8080}"
}

cmd_update() {
  git -C "$root" pull --ff-only
  cmd_up
}

cmd_setup_code() {
  local line
  line="$(docker logs "$container" 2>&1 | grep 'FIRST-RUN SETUP' | tail -n 1 || true)"
  if [[ -n "$line" ]]; then
    echo "$line"
  elif [[ -n "${AUTH_SETUP_CODE:-}" ]]; then
    echo "The setup code is AUTH_SETUP_CODE from $env_file."
  else
    echo "No setup code in the log: an account exists already, or the app has not started (deploy/inkpact.sh logs)."
  fi
}

cmd_status() {
  compose ps
  local url="http://localhost:${INKPACT_PORT:-8080}/api/health"
  if ! curl -fsS "$url" >/dev/null 2>&1; then
    echo "The app does not answer on port ${INKPACT_PORT:-8080} yet (deploy/inkpact.sh logs)."
    return
  fi
  echo "The app answers on port ${INKPACT_PORT:-8080}."
  # 200 with "ok" or "degraded" (the Yjs sidecar is not there: editing works, search text and imports wait), 503
  # when the database does not answer. The body says which.
  local ready
  ready="$(curl -sS "$url/ready" 2>/dev/null || true)"
  echo "Ready: ${ready:-no answer}"
}

# Runs a PostgreSQL client tool from the app's image against the configured database. The password goes in through
# the environment, never on a command line.
pg_tool() {
  local tool="$1"
  shift
  compose run --rm --no-deps -T -e PGPASSWORD="$DB_PASSWORD" --entrypoint "$tool" app \
    -h "$DB_HOST" -p "${DB_PORT:-5432}" -U "${DB_USER:-inkpact}" "$@"
}

cmd_backup() {
  local folder="${1:-$here/backups/$(date -u +%Y%m%dT%H%M%SZ)}"
  [[ ! -e "$folder" ]] || die "$folder exists already: name a new folder"
  mkdir -p "$folder"
  # Safe while the app runs: pg_dump reads one consistent state of the database.
  pg_tool pg_dump --format=custom -d "${DB_NAME:-inkpact}" >"$folder/inkpact.dump"
  # /data without the uploads that are being imported: the files of pages, the session keys, the dumps before migrations.
  compose run --rm --no-deps -T --entrypoint tar app -C /data --exclude=./imports -cf - . >"$folder/data.tar"
  echo "Backup written to $folder (inkpact.dump and data.tar). Keep the two together."
}

cmd_restore() {
  local folder="${1:-}"
  [[ -n "$folder" ]] || die "which backup? deploy/inkpact.sh restore <folder>"
  [[ -f "$folder/inkpact.dump" && -f "$folder/data.tar" ]] || die "$folder has no inkpact.dump and data.tar"

  compose stop app >/dev/null 2>&1 || true
  local tables
  tables="$(pg_tool psql -d "${DB_NAME:-inkpact}" -tAc "select count(*) from pg_tables where schemaname = 'public'")"
  [[ "${tables//[[:space:]]/}" == 0 ]] \
    || die "the database ${DB_NAME:-inkpact} is not empty. A restore never overwrites: drop and create it again (DROP DATABASE, CREATE DATABASE ... OWNER ${DB_USER:-inkpact}), then run this again."

  compose run --rm --no-deps -T --entrypoint tar app -C /data -xf - <"$folder/data.tar"
  pg_tool pg_restore --no-owner -d "${DB_NAME:-inkpact}" <"$folder/inkpact.dump"
  cmd_up
  echo "Restored from $folder. A newer app than the one that made the backup migrates the database when it starts."
}

cmd_reset_password() {
  [[ $# -ge 1 ]] || die "which account? deploy/inkpact.sh reset-password <email>"
  # With a terminal the password is asked for without showing it; without one, use --password-stdin or --generate.
  local tty=(-i)
  if [[ -t 0 ]]; then tty=(-it); fi
  docker exec "${tty[@]}" "$container" dotnet Inkpact.Api.dll reset-password "$@"
}

main() {
  local command="${1:-}"
  [[ -n "$command" ]] || {
    sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 1
  }
  load_env
  case "$command" in
    init) cmd_init ;;
    build) cmd_build ;;
    up) cmd_up ;;
    update) cmd_update ;;
    setup-code) cmd_setup_code ;;
    status) cmd_status ;;
    logs) compose logs -f app ;;
    backup) cmd_backup "${2:-}" ;;
    restore) cmd_restore "${2:-}" ;;
    reset-password) cmd_reset_password "${@:2}" ;;
    down) compose down ;;
    *) die "unknown command '$command' (run it without one for the list)" ;;
  esac
}

main "$@"
