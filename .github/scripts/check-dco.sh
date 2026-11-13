#!/usr/bin/env bash
# Fails when a commit in base..head has no "Signed-off-by:" line that matches its author.
# Usage: check-dco.sh <base-sha> <head-sha>
#
# BOT_AUTHOR (optional): the login of a GitHub App's bot, such as inkpact-release[bot], whose own commits need no
# sign-off: ci.yml sets it for the release pull request (run B.8), which release-please writes through the release App.
# Only that bot's commits are skipped, by its name and its noreply address both; anyone else's commit on the same branch
# is checked as usual.
set -euo pipefail

base="$1"
head="$2"
bot="${BOT_AUTHOR:-}"
failed=0

# Merge commits are made by GitHub and carry no sign-off.
for sha in $(git rev-list --no-merges "$base..$head"); do
  name="$(git log -1 --format='%an' "$sha")"
  email="$(git log -1 --format='%ae' "$sha")"
  if [[ -n "$bot" && "$bot" == *"[bot]" && "$name" == "$bot" && "$email" =~ ^[0-9]+\+(.*)@users\.noreply\.github\.com$ && "${BASH_REMATCH[1]}" == "$bot" ]]; then
    continue
  fi
  author="$name <$email>"
  if ! git log -1 --format='%B' "$sha" | grep -qxF "Signed-off-by: $author"; then
    echo "::error::Commit ${sha:0:7} is not signed off by its author ($author). Use git commit -s."
    failed=1
  fi
done

exit "$failed"
