#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ -x "$project_dir/dist/LyricifyIsland" ]] || "$project_dir/build.sh"

cd "$project_dir"
exec "$project_dir/dist/LyricifyIsland" "$@"
