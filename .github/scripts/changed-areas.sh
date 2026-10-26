#!/usr/bin/env bash
# Decides which CI jobs a pull request needs, from the files it changes, and writes server, web and e2e (true or false)
# to $GITHUB_OUTPUT. Anything that is not clearly one area (a lockfile, a workflow, the root config) runs everything,
# and a push to main always does. Usage: changed-areas.sh <base sha> <head sha>
set -euo pipefail

base="${1:-}"
head="${2:-}"

server=false
web=false
e2e=false

if [[ -z "$base" || -z "$head" ]]; then
  server=true web=true e2e=true
else
  while IFS= read -r file; do
    case "$file" in
      docs/* | *.md) ;;                       # Documentation changes nothing that is tested.
      web/*) web=true e2e=true ;;
      server/sidecar/*) server=true web=true e2e=true ;; # The web job also checks the sidecar.
      server/*) server=true e2e=true ;;
      *) server=true web=true e2e=true ;;
    esac
  done < <(git diff --name-only "$base...$head")
fi

echo "Changed areas: server=$server web=$web e2e=$e2e"
{
  echo "server=$server"
  echo "web=$web"
  echo "e2e=$e2e"
} >> "${GITHUB_OUTPUT:-/dev/null}"
