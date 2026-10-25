#!/usr/bin/env bash
# Fails when a commit in base..head has no "Signed-off-by:" line that matches its author.
# Usage: check-dco.sh <base-sha> <head-sha>
set -euo pipefail

base="$1"
head="$2"
failed=0

# Merge commits are made by GitHub and carry no sign-off.
for sha in $(git rev-list --no-merges "$base..$head"); do
  author="$(git log -1 --format='%an <%ae>' "$sha")"
  if ! git log -1 --format='%B' "$sha" | grep -qxF "Signed-off-by: $author"; then
    echo "::error::Commit ${sha:0:7} is not signed off by its author ($author). Use git commit -s."
    failed=1
  fi
done

exit "$failed"
