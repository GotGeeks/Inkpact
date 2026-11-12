#!/usr/bin/env bash
# Follows the backup and restore commands of docs/self-hosting.md against a real stack, and checks that what was there
# is there again: an account, a page, a tag and a file. Then it resets the password with the guide's command. Run by .github/workflows/image.yml on the image it built, and
# by hand: INKPACT_IMAGE=inkpact:local deploy/test-backup-restore.sh
#
# When a command here changes, the guide's "Backup and restore" section changes with it: they are the same commands.
# Needs Docker with Compose, curl and node.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
port="${INKPACT_TEST_PORT:-18080}"
base="http://127.0.0.1:$port"
email="admin@example.com"
password="a long test password"
jar="$work/cookies"

export COMPOSE_PROJECT_NAME="inkpact-restore-test-$$"
export INKPACT_IMAGE="${INKPACT_IMAGE:?Set INKPACT_IMAGE to the image to test}"
export INKPACT_PORT="$port"
export POSTGRES_PASSWORD="restore-test-password"
export AUTH_SETUP_CODE="restore-test-code"

compose() { docker compose -f "$here/docker-compose.yml" "$@"; }

cleanup() {
  status=$?
  if [[ $status != 0 ]]; then
    echo "--- the app's log ---" >&2
    compose logs --tail 80 app >&2 || true
  fi
  compose down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

# One field of a JSON document on standard input, such as .page.id or .[0].id
field() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{let v=JSON.parse(s);for(const k of process.argv[1].split(".").filter(Boolean))v=v[k.replace(/^\[(\d+)\]$/,"$1")];console.log(v)})' "$1"; }

wait_until_ready() {
  for _ in $(seq 1 90); do
    if curl -fsS "$base/api/health/ready" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  echo "the app did not become ready" >&2
  return 1
}

token() { curl -fsS -b "$jar" -c "$jar" "$base/api/auth/state" | field .csrfToken; }

# api METHOD PATH [JSON]: a signed-in JSON request.
api() {
  local method="$1" path="$2" body="${3:-}"
  if [[ -n "$body" ]]; then
    curl -fsS -b "$jar" -c "$jar" -X "$method" "$base$path" -H "X-CSRF-Token: $(token)" -H 'Content-Type: application/json' -d "$body"
  else
    curl -fsS -b "$jar" -c "$jar" -X "$method" "$base$path"
  fi
}

echo "== Start a stack and put something in it"
compose up -d --wait postgres
compose up -d app
wait_until_ready

api POST /api/auth/setup "{\"setupCode\":\"$AUTH_SETUP_CODE\",\"email\":\"$email\",\"displayName\":\"Admin\",\"password\":\"$password\",\"workspaceName\":\"Notes\"}" >/dev/null
workspace="$(api GET /api/workspaces | field '.[0].id')"
page="$(api POST "/api/workspaces/$workspace/pages" '{"title":"Kept through a restore"}' | field .page.id)"
api POST "/api/pages/$page/tags" '{"add":["restored"],"remove":[]}' >/dev/null
head -c 20000 /dev/urandom >"$work/file.bin"
attachment="$(curl -fsS -b "$jar" -c "$jar" -X POST "$base/api/pages/$page/attachments?name=file.bin" \
  -H "X-CSRF-Token: $(token)" -H 'Content-Type: application/octet-stream' --data-binary "@$work/file.bin" | field .id)"

echo "== Back up, as the guide says"
mkdir "$work/backup"
compose exec -T postgres pg_dump -U inkpact --format=custom inkpact >"$work/backup/inkpact.dump"
compose exec -T app tar -C /data --exclude=./imports -cf - . >"$work/backup/data.tar"

echo "== Lose everything"
compose down -v

echo "== Restore, as the guide says"
compose up -d --wait postgres
compose run --rm --no-deps -T --entrypoint tar app -C /data -xf - <"$work/backup/data.tar"
compose exec -T postgres pg_restore -U inkpact -d inkpact --no-owner --role inkpact <"$work/backup/inkpact.dump"
compose up -d app
wait_until_ready

echo "== Check that it is all there"
rm -f "$jar"
curl -fsS -b "$jar" -c "$jar" -X POST "$base/api/auth/login" -H "X-CSRF-Token: $(token)" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$email\",\"password\":\"$password\"}" >/dev/null
[[ "$(api GET "/api/pages/$page" | field .page.title)" == "Kept through a restore" ]] || { echo "the page is not there" >&2; exit 1; }
[[ "$(api GET "/api/pages/$page" | field '.tags.[0].name')" == "restored" ]] || { echo "the tag is not there" >&2; exit 1; }
curl -fsS -b "$jar" "$base/api/attachments/$attachment" -o "$work/restored.bin"
cmp "$work/file.bin" "$work/restored.bin" || { echo "the file's bytes differ" >&2; exit 1; }

echo "== A forgotten password, as the guide says"
generated="$(compose exec -T app dotnet Inkpact.Api.dll reset-password "$email" --generate | tail -n 1 | awk '{print $NF}')"
# The old session is gone, and the new password signs in.
[[ "$(api GET /api/auth/state | field .user)" == "null" ]] || { echo "the reset left the session signed in" >&2; exit 1; }
rm -f "$jar"
curl -fsS -b "$jar" -c "$jar" -X POST "$base/api/auth/login" -H "X-CSRF-Token: $(token)" -H 'Content-Type: application/json'   -d "{\"email\":\"$email\",\"password\":\"$generated\"}" >/dev/null

echo "The backup restored: sign-in, the page, its tag and its file are there. The password reset signs in."
