#!/usr/bin/env bash
# Prints the next version, derived from the newest vMAJOR.MINOR.PATCH tag in the repository.
#
#   next-version.sh patch   # v1.4.2 -> 1.4.3
#   next-version.sh minor   # v1.4.2 -> 1.5.0
#   next-version.sh major   # v1.4.2 -> 2.0.0
#
# Tags are the version source of truth, so no number is checked into the repo to be kept in sync by hand.
# With no tags at all the base is 0.0.0, which makes `major` the first release: 1.0.0.
#
# The computed version goes to stdout and nothing else does — callers capture it. Diagnostics go to stderr.
set -euo pipefail

bump="${1:-patch}"

# Deliberately not `git tag ... | head -n1`: head closes the pipe as soon as it has its line, git takes a
# SIGPIPE, and `set -o pipefail` reports the whole step as failed. Intermittently, only once the tag list
# is long enough for git to still be writing. Read the list into an array instead.
mapfile -t tags < <(git tag --list 'v[0-9]*' --sort=-v:refname)

latest=""
for tag in "${tags[@]}"; do
  # Release tags only. A prerelease tag like v1.2.3-rc1 is not something to count forward from, and its
  # suffix would land in the arithmetic below as a non-number.
  if [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    latest="$tag"
    break
  fi
done

base="${latest#v}"
base="${base:-0.0.0}"
IFS='.' read -r major minor patch <<< "$base"

case "$bump" in
  major) major=$((major + 1)); minor=0; patch=0 ;;
  minor) minor=$((minor + 1)); patch=0 ;;
  patch) patch=$((patch + 1)) ;;
  *)
    echo "next-version.sh: unknown bump '$bump' (expected major, minor or patch)" >&2
    exit 1
    ;;
esac

echo "next-version.sh: base tag ${latest:-<none>}, bump ${bump}" >&2
echo "${major}.${minor}.${patch}"
