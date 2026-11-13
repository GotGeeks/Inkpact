#!/usr/bin/env bash
# What image.yml builds and publishes (run B.8), written to $GITHUB_OUTPUT (or printed without it):
#   version  the version stamped into the image: a release's x.y.z, or <manifest version>+edge.<short sha> for main
#   release  true for a release
#   tags     the tags to publish, one per line: x.y.z, x.y and latest for a release; edge and sha-<short sha> otherwise
#   publish  true when the images are pushed: a release, a push to main, or a manual run on main that asks for it
#   from     the image the upgrade test starts from: the newest release (latest), or edge while there is none
#
# Reads RELEASE (the release's version, empty otherwise), EVENT (github.event_name), REF (github.ref), PUBLISH_INPUT (a
# manual run's choice), SHA (the commit) and IMAGE (ghcr.io/<owner>/<repository>, lower case).
set -euo pipefail

release="${RELEASE:-}"
event="${EVENT:?Set EVENT}"
ref="${REF:?Set REF}"
sha="${SHA:?Set SHA}"
image="${IMAGE:?Set IMAGE}"
short="${sha:0:7}"

if [[ -n "$release" ]]; then
  [[ "$release" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] \
    || { echo "::error::The release version '$release' is not x.y.z"; exit 1; }
  version="$release"
  tags="$image:$release"$'\n'"$image:${BASH_REMATCH[1]}.${BASH_REMATCH[2]}"$'\n'"$image:latest"
  publish=true
else
  manifest="$(node -p "require('./.release-please-manifest.json')['.']")"
  [[ "$manifest" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "::error::.release-please-manifest.json has no x.y.z for '.'"; exit 1; }
  version="$manifest+edge.$short"
  tags="$image:edge"$'\n'"$image:sha-$short"
  case "$event" in
    push) publish=true ;;
    workflow_dispatch) publish="${PUBLISH_INPUT:-false}" ;;
    *) publish=false ;;
  esac
  # edge is main, and main only: a manual run on another branch builds and checks, and publishes nothing (its signature
  # would name that branch, which the guide's cosign verify refuses).
  if [[ "$publish" == true && "$ref" != refs/heads/main ]]; then
    echo "::notice::$ref is not main: built and checked, not published."
    publish=false
  fi
fi

# The newest release once there is one; its database is what a user upgrades from.
if docker manifest inspect "$image:latest" >/dev/null 2>&1; then
  from="$image:latest"
else
  from="$image:edge"
fi

out="${GITHUB_OUTPUT:-/dev/stdout}"
{
  echo "version=$version"
  if [[ -n "$release" ]]; then echo "release=true"; else echo "release=false"; fi
  echo "publish=$publish"
  echo "from=$from"
  echo "tags<<TAGS"
  echo "$tags"
  echo "TAGS"
} >>"$out"
