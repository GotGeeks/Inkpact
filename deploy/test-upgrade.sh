#!/usr/bin/env bash
# The upgrade test (run B.8): a database and a data volume made by an older image are taken over by a newer one, and
# nothing is lost. Run by .github/workflows/image.yml on the image it built, before anything is published, and by hand:
#   deploy/test-upgrade.sh ghcr.io/gotgeeks/inkpact:latest inkpact:local
#
# With FROM, it imports a Joplin export (notebooks, notes, a picture and a PDF, a to-do, a tag), renames a page, stars it
# and waits for its version. It records the export of everything, the page list, the tags, a file's bytes, a search and
# the version list. Then TO starts on the same volumes, migrates, and must give the same answers. When TO has migrations
# FROM lacks, a dump must be in /data/backups.
#
# Needs Docker with Compose, curl, node 26 (it runs web/e2e/jex.ts) and python3 (it reads the zips).
set -euo pipefail

from="${1:?Usage: test-upgrade.sh FROM-IMAGE TO-IMAGE}"
to="${2:?Usage: test-upgrade.sh FROM-IMAGE TO-IMAGE}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
work="$(mktemp -d)"
port="${INKPACT_TEST_PORT:-18081}"
base="http://127.0.0.1:$port"
email="admin@example.com"
password="a long test password"
jar="$work/cookies"
# The first Python that runs (on Windows, `python3` can be a stub that only offers to install one).
py=""
for candidate in python3 python; do
  if "$candidate" -c 'import zipfile' >/dev/null 2>&1; then py="$candidate" && break; fi
done
[[ -n "$py" ]] || { echo "test-upgrade.sh needs python3" >&2; exit 1; }

export COMPOSE_PROJECT_NAME="inkpact-upgrade-test-$$"
export INKPACT_PORT="$port"
export POSTGRES_PASSWORD="upgrade-test-password"
export AUTH_SETUP_CODE="upgrade-test-code"

# The shipped compose file, with a quick history capture (a rename is captured at the next round) and no update check.
cat >"$work/test.yml" <<'YAML'
services:
  app:
    environment:
      History__CaptureInterval: '00:00:02'
      UpdateCheck__Enabled: 'false'
YAML
compose() { docker compose -f "$here/docker-compose.yml" -f "$work/test.yml" "$@"; }

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

fail() {
  echo "UPGRADE TEST FAILED: $*" >&2
  exit 1
}

# One field of a JSON document on standard input, such as .page.id or .[0].id
# shellcheck disable=SC2016 # the script is JavaScript: its $1 is a regular expression's, not the shell's
field() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{let v=JSON.parse(s);for(const k of process.argv[1].split(".").filter(Boolean))v=v[k.replace(/^\[(\d+)\]$/,"$1")];console.log(typeof v==="object"?JSON.stringify(v):v)})' "$1"; }

token() { curl -fsS -b "$jar" -c "$jar" "$base/api/auth/state" | field .csrfToken; }

api() {
  local method="$1" path="$2" body="${3:-}"
  if [[ -n "$body" ]]; then
    curl -fsS -b "$jar" -c "$jar" -X "$method" "$base$path" -H "X-CSRF-Token: $(token)" -H 'Content-Type: application/json' -d "$body"
  elif [[ "$method" != GET ]]; then
    curl -fsS -b "$jar" -c "$jar" -X "$method" "$base$path" -H "X-CSRF-Token: $(token)"
  else
    curl -fsS -b "$jar" -c "$jar" "$base$path"
  fi
}

wait_until_ready() {
  for _ in $(seq 1 150); do
    if curl -fsS "$base/api/health/ready" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  fail "the app did not become ready"
}

# Waits until a command's output is not empty and not "0", up to a minute.
wait_for() {
  local what="$1"
  shift
  for _ in $(seq 1 60); do
    local out
    out="$("$@" 2>/dev/null || true)"
    if [[ -n "$out" && "$out" != 0 ]]; then return 0; fi
    sleep 1
  done
  fail "$what did not happen within a minute"
}

migrations() { compose exec -T postgres psql -U inkpact -d inkpact -tAc 'select count(*) from "__EFMigrationsHistory"'; }
dumps() { compose exec -T app sh -c 'ls /data/backups 2>/dev/null | wc -l' | tr -d ' \r'; }

# What the app says about everything that was put in, in a form that can be compared: one file per answer.
record() {
  local into="$1"
  mkdir -p "$into"
  api GET "/api/workspaces/$workspace/pages" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const t=JSON.parse(s);console.log(JSON.stringify(t.pages.map(p=>({id:p.id,parentId:p.parentId,title:p.title,sortKey:p.sortKey})).sort((a,b)=>a.id.localeCompare(b.id)),null,1))})' >"$into/pages.json"
  api GET "/api/workspaces/$workspace/tags" >"$into/tags.json"
  api GET "/api/workspaces/$workspace/favorites" >"$into/favorites.json"
  api GET "/api/pages/$renamed/versions" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const l=JSON.parse(s);console.log(JSON.stringify(l.versions.map(v=>({id:v.id,kind:v.kind,title:v.title,bytes:v.bytes}))))})' >"$into/versions.json"
  api GET "/api/workspaces/$workspace/search?q=$(node -p 'encodeURIComponent(process.argv[1])' "$search_word")" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const r=JSON.parse(s);console.log(JSON.stringify((r.items??r.results??r).map(i=>i.pageId??i.id).sort()))})' >"$into/search.json"
  curl -fsS -b "$jar" "$base/api/attachments/$attachment" -o "$into/attachment.bin"
  curl -fsS -b "$jar" "$base/api/workspaces/$workspace/export?lang=en" -o "$work/export.zip"
  # The zip's files with their bytes' digests; the README says when it was made, so only its presence counts.
  "$py" - "$work/export.zip" >"$into/export.txt" <<'PY'
import hashlib, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    for info in sorted(z.infolist(), key=lambda i: i.filename):
        data = z.read(info)
        digest = "readme" if info.filename.lower().endswith("readme.md") else hashlib.sha256(data).hexdigest()
        print(info.filename, digest)
PY
}

echo "== Start $from and put something in it"
INKPACT_IMAGE="$from" compose up -d --wait postgres
INKPACT_IMAGE="$from" compose up -d app
wait_until_ready
api POST /api/auth/setup "{\"setupCode\":\"$AUTH_SETUP_CODE\",\"email\":\"$email\",\"displayName\":\"Admin\",\"password\":\"$password\",\"workspaceName\":\"Notes\"}" >/dev/null
workspace="$(api GET /api/workspaces | field '.[0].id')"

(cd "$root" && node --input-type=module -e "
import { writeFileSync } from 'node:fs'
import { generateJex } from './web/e2e/jex.ts'
const jex = generateJex('Upgrade')
writeFileSync(process.argv[1], jex.file)
writeFileSync(process.argv[2], JSON.stringify(jex.titles))
" "$work/export.jex" "$work/titles.json")
import="$(curl -fsS -b "$jar" -c "$jar" -X POST "$base/api/workspaces/$workspace/imports/joplin?name=export.jex" \
  -H "X-CSRF-Token: $(token)" -H 'Content-Type: application/octet-stream' --data-binary "@$work/export.jex" | field .id)"
import_done() { [[ "$(api GET "/api/imports/$import" | field .state)" =~ ^(completed|failed)$ ]] && echo 1; }
wait_for "the import" import_done
[[ "$(api GET "/api/imports/$import" | field .state)" == completed ]] || fail "the import did not complete"

pictures_title="$(field .pictures <"$work/titles.json")"
renamed="$(api GET "/api/workspaces/$workspace/pages" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s).pages.find(p=>p.title===process.argv[1]).id))' "$pictures_title")"
page="$(api GET "/api/pages/$renamed")"
version="$(echo "$page" | field .page.version)"
api PATCH "/api/pages/$renamed" "{\"title\":\"$pictures_title, renamed\",\"expectedVersion\":$version}" >/dev/null
api PUT "/api/pages/$renamed/favorite" "{}" >/dev/null
attachment="$(compose exec -T postgres psql -U inkpact -d inkpact -tAc "select id from attachments order by id limit 1" | tr -d ' \r')"
[[ -n "$attachment" ]] || fail "the import made no attachment"
# A word the imported notes hold (the to-do's title): the search finds its page before and after.
search_word="$(field .todo <"$work/titles.json" | awk '{print $NF}')"
versions_made() { api GET "/api/pages/$renamed/versions" | field .versions.length; }
wait_for "the rename's version" versions_made

record "$work/before"
migrations_before="$(migrations)"
dumps_before="$(dumps)"
[[ "$(cat "$work/before/search.json")" != "[]" ]] || fail "the search found nothing before the upgrade"

echo "== Upgrade to $to"
INKPACT_IMAGE="$from" compose stop app
INKPACT_IMAGE="$to" compose up -d app
wait_until_ready

echo "== Check that nothing was lost"
rm -f "$jar"
curl -fsS -b "$jar" -c "$jar" -X POST "$base/api/auth/login" -H "X-CSRF-Token: $(token)" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$email\",\"password\":\"$password\"}" >/dev/null || fail "signing in after the upgrade"
record "$work/after"
for answer in pages.json tags.json favorites.json versions.json search.json attachment.bin export.txt; do
  cmp -s "$work/before/$answer" "$work/after/$answer" || {
    diff "$work/before/$answer" "$work/after/$answer" >&2 || true
    fail "$answer is not the same after the upgrade"
  }
done

migrations_after="$(migrations)"
if ((migrations_after > migrations_before)); then
  (($(dumps) > dumps_before)) || fail "the upgrade migrated without a dump in /data/backups"
  echo "The upgrade ran $((migrations_after - migrations_before)) migrations, after a dump."
else
  echo "The upgrade had no migrations to run."
fi

echo "The upgrade from $from to $to kept everything: the pages, tags, favorites, versions, a search, a file and the export."
