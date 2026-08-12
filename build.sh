#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
publish_dir="$(mktemp -d)"
trap 'rm -rf -- "$publish_dir"' EXIT

git -C "$project_dir" submodule update --init --recursive
command -v dotnet >/dev/null || { echo 'dotnet SDK is required' >&2; exit 1; }

dotnet publish "$project_dir/LyricifyIsland.csproj" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  --output "$publish_dir"

mkdir -p "$project_dir/dist"
install -m755 "$publish_dir/LyricifyIsland" "$project_dir/dist/LyricifyIsland"

printf 'Built %s\n' "$project_dir/dist/LyricifyIsland"
