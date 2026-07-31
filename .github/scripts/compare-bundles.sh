#!/usr/bin/env bash
# Compares the bundled web assets of two .nupkg files.
#
#   compare-bundles.sh a.nupkg b.nupkg
#
# The release rebuilds from source rather than promoting the package CI already built, because a NuGet
# version is baked into the .nuspec and the filename — there is no retag. That is the right trade for
# NuGet, but it means the bytes being published are not literally the bytes CI tested. This closes the
# gap by checking the one part that could differ without any test noticing: wwwroot/dist, the output of
# npm and esbuild.
#
# Only staticwebassets/ is compared. Everything else in the package legitimately differs between two
# versions — the .nuspec, the assembly (its version is stamped in), the package relationship parts — so
# diffing them would be noise that trains you to ignore this check.
set -euo pipefail

[ $# -eq 2 ] || { echo "usage: compare-bundles.sh <a.nupkg> <b.nupkg>" >&2; exit 2; }

readonly A="$1" B="$2"

for pkg in "$A" "$B"; do
  [ -f "$pkg" ] || { echo "compare-bundles.sh: no such file: $pkg" >&2; exit 2; }
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Hashes every bundled asset, path-relative so the two extractions are comparable.
hash_assets() {
  local pkg="$1" dir="$work/$2"
  mkdir -p "$dir"

  # -o so a rerun overwrites; the glob limits extraction to the assets under comparison.
  unzip -q -o "$pkg" 'staticwebassets/*' -d "$dir" 2>/dev/null || true

  if [ -z "$(find "$dir" -type f -print -quit)" ]; then
    echo "compare-bundles.sh: $pkg contains no staticwebassets/ — wrong package?" >&2
    exit 1
  fi

  (cd "$dir" && find staticwebassets -type f | sort | xargs sha256sum)
}

hash_assets "$A" a > "$work/a.sums"
hash_assets "$B" b > "$work/b.sums"

if diff -u "$work/a.sums" "$work/b.sums" > "$work/diff.txt"; then
  echo "compare-bundles.sh: bundled assets identical ($(wc -l < "$work/a.sums") files)"
  exit 0
fi

echo "compare-bundles.sh: BUNDLED ASSETS DIFFER between the two packages." >&2
echo "  a: $A" >&2
echo "  b: $B" >&2
echo >&2
sed -n '3,40p' "$work/diff.txt" >&2
echo >&2
echo "The release build produced different JavaScript from the one CI tested. Same commit and the same" >&2
echo "locked toolchain should give the same bundle, so treat this as the build having become" >&2
echo "non-deterministic rather than as a flaky check." >&2
exit 1
