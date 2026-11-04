#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$project_dir/dist}"
mkdir -p "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT

version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$project_dir/LyricifyIsland.csproj" | head -n 1)"
[[ "$version" =~ ^[0-9][0-9A-Za-z._+~]*$ ]] || { echo "Invalid project version: $version" >&2; exit 1; }
commit_time="$(git -C "$project_dir" log -1 --format=%ct)"
commit_hash="$(git -C "$project_dir" rev-parse --short=12 HEAD)"
release="1.${commit_time}.git${commit_hash}"
source_name="lyricify-island-$version"
source_dir="$work_dir/$source_name"
topdir="$work_dir/rpmbuild"
mkdir -p "$source_dir" "$topdir"/{SOURCES,SPECS,SRPMS} "$work_dir/nuget-feed"

(
    cd "$project_dir"
    tar -cf - --exclude-vcs --exclude='*/bin' --exclude='*/obj' \
        ./*.cs LyricifyIsland.csproj LICENSE README.md THIRD_PARTY_NOTICES.md \
        Resources licenses packaging \
        vendor/Lyricify-Lyrics-Helper/LICENSE \
        vendor/Lyricify-Lyrics-Helper/README.md \
        vendor/Lyricify-Lyrics-Helper/Lyricify.Lyrics.Helper
) | tar -xf - -C "$source_dir"
tar -czf "$topdir/SOURCES/$source_name.tar.gz" -C "$work_dir" "$source_name"

python3 "$source_dir/packaging/apply-copr-helper.py" "$source_dir"
export DOTNET_CLI_HOME="$work_dir/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$work_dir/nuget-packages}"
dotnet restore "$source_dir/LyricifyIsland.csproj" -p:SelfContained=false -p:NuGetAudit=false

python3 - "$source_dir/obj/project.assets.json" "$NUGET_PACKAGES" "$work_dir/nuget-feed" <<'PY'
import json
import shutil
import sys
from pathlib import Path

assets = json.loads(Path(sys.argv[1]).read_text())
packages = Path(sys.argv[2])
feed = Path(sys.argv[3])
for name, item in assets["libraries"].items():
    if item["type"] != "package":
        continue
    package_dir = packages / item["path"]
    archives = list(package_dir.glob("*.nupkg"))
    if len(archives) != 1:
        raise SystemExit(f"Cannot find NuGet archive for {name} in {package_dir}")
    shutil.copyfile(archives[0], feed / archives[0].name)
PY
tar -cf "$topdir/SOURCES/lyricify-island-nuget-$version.tar" -C "$work_dir" nuget-feed
sed -e "s/^Version:.*/Version:        $version/" \
    -e "s/^Release:.*/Release:        $release%{?dist}/" \
    "$project_dir/packaging/lyricify-island.spec" > "$topdir/SPECS/lyricify-island.spec"
rpmbuild -bs --target x86_64 --define "_topdir $topdir" --define "_srcrpmdir $output_dir" "$topdir/SPECS/lyricify-island.spec"
