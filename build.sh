#!/usr/bin/env bash
# Cake Frosting bootstrapper. Runs from the repo root.
#   ./build.sh                  # Default: Build + Test
#   ./build.sh --target Bundle  # Re-bundle Monaco + monaco-yaml into wwwroot/dist
#   ./build.sh --target Pack    # Produce the NuGet package into artifacts/
#   ./build.sh --target Demo    # Run the WASM demo harness
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --project build/Build.csproj -- "$@"
