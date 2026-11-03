#!/usr/bin/env bash
# Builds Inkpact from this checkout and runs it against a PostgreSQL server you already run. Settings: deploy/.env (copy
# deploy/external-db.env.example). See docs/self-hosting.md, "With a PostgreSQL you already run".
#
#   deploy/inkpact.sh init         make the data folder, and the database user and database when PG_CONTAINER is set
#   deploy/inkpact.sh build        build the image from this checkout, without starting it
#   deploy/inkpact.sh up           build what changed, and start or restart the app
#   deploy/inkpact.sh update       git pull, then up
#   deploy/inkpact.sh setup-code   the code for creating the administrator, while there is no account yet
#   deploy/inkpact.sh status       the container, and whether the app answers
#   deploy/inkpact.sh logs         follow the app's log
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
  image="${INKPACT_IMAGE:-inkpact:local}"
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
  if curl -fsS "http://localhost:${INKPACT_PORT:-8080}/api/health" >/dev/null 2>&1; then
    echo "The app answers on port ${INKPACT_PORT:-8080}."
  else
    echo "The app does not answer on port ${INKPACT_PORT:-8080} yet (deploy/inkpact.sh logs)."
  fi
}

main() {
  local command="${1:-}"
  [[ -n "$command" ]] || {
    sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
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
    down) compose down ;;
    *) die "unknown command '$command' (run it without one for the list)" ;;
  esac
}

main "$@"
