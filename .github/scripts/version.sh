#!/usr/bin/env bash
# Reads and bumps the released version, which lives in Directory.Build.props.
#
#   version.sh                 # print the current version
#   version.sh bump patch      # 1.4.2 -> 1.4.3, rewrite the file, print the new version
#   version.sh bump minor      # 1.4.2 -> 1.5.0
#   version.sh bump major      # 1.4.2 -> 2.0.0
#
# The file is the single source of truth: MSBuild reads it for every build, so what it says is what
# packs. Bumping rewrites it in place — the caller commits the change, and the release workflow
# deliberately does that only AFTER the package is published.
#
# The version goes to stdout and nothing else does — callers capture it. Diagnostics go to stderr.
set -euo pipefail

readonly FILE="Directory.Build.props"
readonly PATTERN='<VersionPrefix>[0-9]+\.[0-9]+\.[0-9]+</VersionPrefix>'

[ -f "$FILE" ] || { echo "version.sh: $FILE not found (run from the repository root)" >&2; exit 1; }

read_version() {
  grep -oE "$PATTERN" "$FILE" | head -n1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' || true
}

current="$(read_version)"

if [ -z "$current" ]; then
  echo "version.sh: no <VersionPrefix>MAJOR.MINOR.PATCH</VersionPrefix> in $FILE" >&2
  exit 1
fi

# No bump requested: report what is there. This is also the first-release path, where the file already
# holds the version to publish and nothing should move.
if [ "${1:-}" != "bump" ]; then
  echo "$current"
  exit 0
fi

IFS='.' read -r major minor patch <<< "$current"

case "${2:-}" in
  major) major=$((major + 1)); minor=0; patch=0 ;;
  minor) minor=$((minor + 1)); patch=0 ;;
  patch) patch=$((patch + 1)) ;;
  *)
    echo "version.sh: unknown bump '${2:-}' (expected major, minor or patch)" >&2
    exit 1
    ;;
esac

next="${major}.${minor}.${patch}"

# Anchored on the exact current value rather than the loose pattern, so this cannot rewrite some other
# version-shaped string that finds its way into the file later.
sed -i "s|<VersionPrefix>${current}</VersionPrefix>|<VersionPrefix>${next}</VersionPrefix>|" "$FILE"

# Prove the rewrite landed. A silent no-op here would publish the old version under a new tag.
written="$(read_version)"
[ "$written" = "$next" ] || { echo "version.sh: rewrite failed, $FILE still reads '$written'" >&2; exit 1; }

echo "version.sh: ${current} -> ${next} (${2})" >&2
echo "$next"
