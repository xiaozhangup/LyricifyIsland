#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
version="${1:-0.1.1}"
topdir="$(mktemp -d)"
trap 'rm -rf -- "$topdir"' EXIT

command -v rpmbuild >/dev/null || { echo 'rpmbuild is required' >&2; exit 1; }
[[ "$version" =~ ^[0-9][0-9A-Za-z._+~]*$ ]] || { echo "invalid RPM version: $version" >&2; exit 1; }

"$project_dir/build.sh"
mkdir -p "$topdir"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
install -m755 "$project_dir/dist/LyricifyIsland" "$topdir/SOURCES/LyricifyIsland"
install -m644 "$project_dir/packaging/lyricify-island.desktop" "$topdir/SOURCES/lyricify-island.desktop"
install -m644 "$project_dir/vendor/Lyricify-Lyrics-Helper/Lyricify.Lyrics.Helper/Resources/icon.png" "$topdir/SOURCES/lyricify-island.png"

cat >"$topdir/SPECS/lyricify-island.spec" <<EOF
%global __strip /bin/true
Name:           lyricify-island
Version:        $version
Release:        1%{?dist}
Summary:        Spotify desktop lyrics island
License:        MIT AND Apache-2.0
URL:            https://github.com/xiaozhangup/LyricifyIsland
BuildArch:      x86_64
Requires:       libX11, libXfixes

%description
Desktop topmost lyrics island for Spotify.

%install
install -Dm755 %{_sourcedir}/LyricifyIsland %{buildroot}%{_bindir}/LyricifyIsland
install -Dm644 %{_sourcedir}/lyricify-island.desktop %{buildroot}%{_datadir}/applications/lyricify-island.desktop
install -Dm644 %{_sourcedir}/lyricify-island.png %{buildroot}%{_datadir}/icons/hicolor/128x128/apps/lyricify-island.png

%files
%{_bindir}/LyricifyIsland
%{_datadir}/applications/lyricify-island.desktop
%{_datadir}/icons/hicolor/128x128/apps/lyricify-island.png
EOF

rpmbuild -bb --define "_topdir $topdir" "$topdir/SPECS/lyricify-island.spec"
mkdir -p "$project_dir/dist"
find "$topdir/RPMS" -name '*.rpm' -exec install -m644 {} "$project_dir/dist/" \;
printf 'Built %s\n' "$project_dir"/dist/*.rpm
