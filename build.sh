#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
sdk_dir="$project_dir/.dotnet"
installer="$(mktemp)"
publish_dir="$(mktemp -d)"
trap 'rm -f "$installer"; rm -rf -- "$publish_dir"' EXIT

git -C "$project_dir" submodule update --init --recursive

if [[ ! -x "$sdk_dir/dotnet" ]]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --channel 8.0 --install-dir "$sdk_dir" --no-path
fi

"$sdk_dir/dotnet" publish "$project_dir/LyricifyIsland.csproj" \
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
